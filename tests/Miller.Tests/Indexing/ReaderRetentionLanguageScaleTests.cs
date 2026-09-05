using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing.Reads;
using Miller.Indexing.Store;
using Xunit;

namespace Miller.Tests.Indexing;

[Trait("Category", "Scale")]
public sealed class ReaderRetentionLanguageScaleTests(ITestOutputHelper output)
{
    [Fact]
    public void AdmittedSnapshotServesEveryProducerLanguageAndReleasesItsRegistration()
    {
        string binary = ScaleTestSupport.RequireJulieServer();
        using var catalog = JsonDocument.Parse(ScaleTestSupport.RunJulie(binary, "languages", "--json"));
        JsonElement[] languages = catalog.RootElement.GetProperty("languages").GetProperty("languages")
            .EnumerateArray().ToArray();
        string[] supported = languages.Select(language => language.GetProperty("language").GetString()!)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(supported);
        Assert.Equal(supported, Samples.Select(sample => sample.Language).Order(StringComparer.Ordinal).ToArray());

        string directory = Path.Combine(Path.GetTempPath(), "miller-reader-languages-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(directory, "workspace");
        var binding = new StoreFamilyBinding(Guid.NewGuid(), Path.Combine(directory, "family"),
            "language-matrix", root, StoreBindingState.Ready);
        try
        {
            Directory.CreateDirectory(root);
            foreach (var sample in Samples)
            {
                JsonElement capability = Assert.Single(languages,
                    language => language.GetProperty("language").GetString() == sample.Language);
                string[] extensions = capability.GetProperty("extensions").EnumerateArray()
                    .Select(extension => extension.GetString()!).ToArray();
                if (sample.Language == "qmldir")
                {
                    Assert.Empty(extensions);
                    Assert.Equal("qmldir", sample.FileName);
                }
                else
                    Assert.Contains(Path.GetExtension(sample.FileName).TrimStart('.'), extensions);
                string sampleDirectory = Path.Combine(root, sample.Language);
                Directory.CreateDirectory(sampleDirectory);
                File.WriteAllText(Path.Combine(sampleDirectory, sample.FileName), sample.Source);
            }

            string request = "language-matrix-import";
            ScaleTestSupport.RunJulie(binary, "store", "import", "--store", binding.StoreRoot,
                "--family", binding.FamilyId.ToString("D"), "--root", root, "--view", binding.ViewId,
                "--level", "full", "--jobs", "1", "--request-id", request,
                "--idempotency-key", request, "--json");
            StoreWorkspacePointer.Write(root, binding);
            Assert.Equal(0, RegistrationCount(binding));
            using (var session = WorkspaceReadSessionFactory.Open(
                Path.Combine(root, ".miller", "symbols.db"), root, null,
                new JulieStoreClient(binary), storeEnabled: true))
            {
                Assert.Equal(binding.StoreRoot, session.FamilyStoreRoot);
                Assert.Equal(1, RegistrationCount(binding));
                Assert.Equal(1, RegistrationCount(binding, session.Snapshot));
                string[] served = session.Read(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = "SELECT language,kind,COUNT(*) FROM symbols GROUP BY 1,2 ORDER BY 1,2";
                    using var rows = command.ExecuteReader();
                    var actual = new HashSet<string>(StringComparer.Ordinal);
                    while (rows.Read())
                    {
                        string language = rows.GetString(0);
                        long count = rows.GetInt64(2);
                        Assert.True(count > 0);
                        actual.Add(language);
                        output.WriteLine($"{language}|{rows.GetString(1)}|{count}");
                    }
                    return actual.Order(StringComparer.Ordinal).ToArray();
                });
                Assert.Equal(supported, served);
                Assert.Equal(1, RegistrationCount(binding, session.Snapshot));
                output.WriteLine($"languages={served.Length}; registration_while_serving=1; owner_pid={Environment.ProcessId}");
            }
            Assert.Equal(0, RegistrationCount(binding));
            output.WriteLine("registration_after_disposal=0");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static long RegistrationCount(StoreFamilyBinding binding, WorkspaceReadSnapshot? snapshot = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(binding.StoreRoot, "coord.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM reader_registrations";
        if (snapshot is not null)
        {
            command.CommandText += """
                 WHERE owner_pid=$pid AND view_id=$view AND generation_name=$generation
                   AND manifest_generation=$manifest
                """;
            command.Parameters.AddWithValue("$pid", Environment.ProcessId);
            command.Parameters.AddWithValue("$view", binding.ViewId);
            command.Parameters.AddWithValue("$generation", snapshot.GenerationName);
            command.Parameters.AddWithValue("$manifest", snapshot.ManifestGeneration);
        }
        return (long)command.ExecuteScalar()!;
    }

    // Basic fixture sources, with tabs expanded to four spaces, from julie-extractors commit 3b3e5b6f03b724448df9012bb75224e99ca68f5d:
    // fixtures/extraction/<language>/basic/<filename>. These are parsed, never compiled or executed.
    // Keep the catalog equality assertion: adding a producer language requires a representative sample here.
    private static readonly (string Language, string FileName, string Source)[] Samples =
    [
        ("rust", "source.rs", """"
        use std::collections::HashMap;

        pub mod fixture {
            #[derive(Debug)]
            pub struct Worker {
                pub id: i32,
            }

            impl Worker {
                pub fn run(&self) -> i32 {
                    self.mark();
                    Self::mark();
                    record_run(self.id);
                    helper(self.id)
                }
            }

            /// Emits a worker-run marker for observability hooks.
            pub fn record_run(id: i32) {
                observe_run("worker-run", id);
            }

            /// Records a named worker event for downstream hooks.
            pub fn observe_run(event: &str, id: i32) {
                let _ = (event, id);
            }

            pub fn helper(value: i32) -> i32 {
                value + 1
            }

            /// Doubles a worker id.
            pub fn double(value: i32) -> i32 {
                value * 2
            }

            /// Checks the worker service health endpoint.
            pub fn fetch_status() {
                fetch_url("https://api.example.com/workers/status");
            }

            fn fetch_url(url: &str) {
                let _ = url;
            }

            pub fn build_index() -> HashMap<String, Vec<u8>> {
                HashMap::new()
            }

            pub fn evaluate(count: i32, enabled: bool) -> i32 {
                let mut total = 0;
                if enabled {
                    for i in 0..count {
                        total += i;
                    }
                }
                total
            }
        }

        """"),
        ("c", "source.c", """"
        /**
         * Worker state passed through the C helper pipeline.
         */
        typedef struct Worker {
            int id;
        } Worker;

        int helper(int value);
        void worker_log(const char *message);

        /**
         * Run the worker through the helper pipeline.
         */
        [[nodiscard]]
        int worker_run(Worker *worker) {
            worker_log("worker-run");
            return helper(worker->id);
        }

        int helper(int value) {
            return value + 1;
        }

        int evaluate(int count, int enabled) {
            int total = 0;
            if (enabled) {
                for (int i = 0; i < count; i++) {
                    total += i;
                }
            }
            return total;
        }

        struct bar;

        struct node {
            struct bar *next;
        };

        void receive_facts(struct Worker *x, const char *s, int n) {
            struct Worker *p = x;
            int buf[8];
        }

        void handler(void (*cb)(int)) {
        }

        """"),
        ("cpp", "source.cpp", """"
        /// Base identity provider.
        class Base {
        public:
            int id() const {
                return 1;
            }
        };

        /// Worker pipeline implementation.
        class Worker : public Base {
        public:
            explicit Worker(int id) : id_(id) {}

            /// Run the worker helper pipeline.
            [[nodiscard]]
            int run() const {
                log("worker-run");
                this->helper(id_);
                return helper(id_);
            }

            void ping() const;

        private:
            int helper(int value) const {
                return value + 1;
            }

            int id_;
        };

        void Worker::ping() const {
            this->id();
            (*this).id();
        }

        /// Convert a raw value into a helper result.
        [[nodiscard]]
        int helper_value(int value) {
            return value + 2;
        }

        int run_worker() {
            return helper_value(20);
        }

        int evaluate(int count, bool enabled) {
            int total = 0;
            if (enabled) {
                for (int i = 0; i < count; i++) {
                    total += i;
                }
            }
            return total;
        }

        template<typename K, typename V>
        struct Map {};

        template<typename T>
        struct Vec {};

        struct Item {};

        Map<int, Vec<Item>> worker_index;

        void use_facts(const Item& a, Item* b, Item c, Item&& d) {
            auto made = std::make_unique<Item>();
            auto unknown = Unknown();
            Item declared;
            auto constructed = Item();
            auto allocated = new Item();
        }

        """"),
        ("go", "source.go", """"
        package fixture

        import "net/http"

        type List[T any] struct{}

        type Map[K, V any] struct{}

        type Worker struct {
            ID int `json:"id" db:"worker_id"`
        }

        var workerIndex Map[string, List[int]]

        func NewWorker(id int) Worker {
            return Worker{ID: id}
        }

        func (w Worker) Run() int {
            recordRun(w.ID)
            return helper(w.ID)
        }

        func (w Worker) Start() {
            next := NewWorker(w.ID)
            _ = next
            w.Run()
            other := Worker{}
            other.Run()
        }

        // recordRun emits a worker-run marker for observability hooks.
        func recordRun(id int) {
            observeRun("worker-run", id)
        }

        // observeRun records a named worker event for downstream hooks.
        func observeRun(event string, id int) {
            _ = event
            _ = id
        }

        // helper increments a worker id.
        func helper(value int) int {
            return value + 1
        }

        // FetchStatus checks the worker service health endpoint.
        func FetchStatus() error {
            _, err := http.Get("https://api.example.com/workers/status")
            return err
        }

        //go:noinline
        func Evaluate(count int, enabled bool) int {
            total := 0
            if enabled {
                for i := 0; i < count; i++ {
                    total += i
                }
            }
            return total
        }

        """"),
        ("zig", "source.zig", """"
        const std = @import("std");

        threadlocal var worker_tls: i32 = 0;

        pub const Worker = struct {
            id: i32,

            pub fn run(self: Worker) i32 {
                record_run(self.id);
                return helper(self.id);
            }

            pub fn go(self: *Worker) i32 {
                return self.run();
            }

            pub fn probe(self: *Worker) void { self.missingWave2(); }
            const Self = @This();
        };

        /// Emits a worker-run marker for observability hooks.
        fn record_run(id: i32) void {
            observe_run("worker-run", id);
        }

        /// Records a named worker event for downstream hooks.
        fn observe_run(event: []const u8, id: i32) void {}

        /// Increment a worker id.
        pub fn helper(value: i32) i32 {
            return value + 1;
        }

        fn hypotenuse(x: f64, y: f64) f64 {
            return @sqrt(x * x + y * y);
        }

        inline fn fast_path(value: i32) i32 {
            return value + 1;
        }

        fn identity(comptime T: type, value: T) T {
            return value;
        }

        export fn ffi_entry(value: i32) i32 {
            return helper(value);
        }

        /// Checks the worker service health endpoint.
        pub fn fetch_status() void {
            fetch_url("https://api.example.com/workers/status");
        }

        fn fetch_url(url: []const u8) void {}

        pub fn runWorker(worker: Worker) i32 {
            return helper(worker.id);
        }

        pub fn evaluate(count: i32, enabled: bool) i32 {
            var total: i32 = 0;
            if (enabled) {
                for (0..count) |i| {
                    total += i;
                }
            } else if (count > 0) {
                total = if (count > 10) 1 else 0;
            }
            return total;
        }

        var workerIndex: Map(Key, ArrayList(User)) = undefined;

        const Store = struct {
            items: i32,
        };

        fn make() void {}

        fn demo() void {
            const s = Store{ .items = 1 };
            const a = Unknown{};
            const b = std.ArrayList(u8).init(undefined);
            const c = make();
            var buf: [8]u8 = undefined;
            _ = .{ s, a, b, c, buf };
        }

        """"),
        ("typescript", "source.ts", """"
        export interface Job {
            run(): number;
        }

        @Component()
        export class Worker implements Job {
            constructor(private id: number) {}

            run(): number {
                return helper(this.id);
            }
        }

        /**
         * Increment a worker id.
         * @param value the worker id
         * @returns the incremented id
         */
        function helper(value: number): number {
            return value + 1;
        }

        function evaluate(count: number, enabled: boolean): number {
            let total = 0;
            if (enabled) {
                for (let i = 0; i < count; i++) {
                    total += i;
                }
            }
            return total;
        }

        """"),
        ("tsx", "source.tsx", """"
        type Props = {
            label: string;
        };

        @Component()
        export class WorkerModel {
            constructor(public label: string) {}
        }

        const workerIndex: Map<string, Array<number>> = new Map();

        function fetchWorkers() {
            return fetch("/api/workers");
        }

        function observeRun(event: string) {
            void event;
        }

        export function Badge(props: Props) {
            return (
                <button data-action="run" onClick={() => observeRun("worker-run")}>
                    {format(props.label)}
                </button>
            );
        }

        /**
         * Format the badge label.
         * @returns formatted string
         */
        function format(value: string): string {
            return value.trim();
        }

        function evaluate(count: number, enabled: boolean): number {
            let total = 0;
            if (enabled) {
                for (let i = 1; i <= count; i++) {
                    total += i;
                }
            } else if (count > 0) {
                total = 1;
            }
            return total;
        }

        """"),
        ("javascript", "source.js", """"
        @registered
        export class Worker {
            constructor(id) {
                this.id = id;
            }

            run() {
                return helper(this.id);
            }
        }

        /**
         * Increment a worker id.
         * @returns {number}
         */
        function helper(value) {
            return value + 1;
        }

        function evaluate(count, enabled) {
            let total = 0;
            if (enabled) {
                for (let i = 0; i < count; i++) {
                    total += i;
                }
            }
            return total;
        }

        """"),
        ("jsx", "source.jsx", """"
        @registered
        export class WorkerModel {
            constructor(label) {
                this.label = label;
            }
        }

        export function Badge({ label }) {
            function handleClick() {
                fetch("/api/workers");
                return format(label);
            }

            return (
                <button data-action="run" onClick={handleClick}>
                    {format(label)}
                </button>
            );
        }

        /**
         * Format the badge label.
         * @returns {string}
         */
        function format(value) {
            return value.trim();
        }

        function evaluate(count, enabled) {
            let total = 0;
            if (enabled) {
                for (let i = 1; i <= count; i++) {
                    total += i;
                }
            } else if (count > 0) {
                total = 1;
            }
            return total;
        }

        """"),
        ("html", "source.html", """"
        <!doctype html>
        <html>
          <head>
            <title>Worker</title>
          </head>
          <body>
            <!-- Worker action panel. -->
            <section id="worker">
              <h1>Worker</h1>
              <a href="/workers">Workers</a>
              <button data-action="run">Run</button>
            </section>
            <script>
              function helper(value) {
                return value + 1;
              }
            </script>
          </body>
        </html>

        """"),
        ("css", "source.css", """"
        :root {
            --accent: #0f766e;
        }

        .logo {
            background-image: url("/assets/worker.svg");
        }

        .button,
        #save {
            color: var(--accent);
            animation: spin 1s linear;
        }

        @media (min-width: 40rem) {
            .button {
                display: inline-flex;
            }
        }

        @keyframes spin {
            from {
                opacity: 0;
            }
            to {
                opacity: 1;
            }
        }

        @charset "UTF-8";
        @namespace url(http://www.w3.org/1999/xhtml);

        @supports (display: grid) {
            .grid {
                display: grid;
            }
        }

        @container (min-width: 20rem) {
            .card {
                color: navy;
            }
        }

        @font-face {
            font-family: "Worker";
            src: url("/fonts/worker.woff2");
        }

        @layer utilities {
            .m-0 {
                margin: 0;
            }
        }

        """"),
        ("vue", "source.vue", """"
        <template>
          <section class="worker" v-if="title">
            <HeaderBar />
            <h1>{{ title }}</h1>
            <RouterLink to="/calendar">Calendar</RouterLink>
            <template #actions>
              <RouterLink to="/inside-slot">Inside</RouterLink>
            </template>
            <RouterLink to="/after-slot">After</RouterLink>
            <button @click.prevent="evaluate(1, true)" :class="{ active: title }">Run</button>
          </section>
        </template>

        <script setup lang="ts">
        import CalendarView from "../views/CalendarView.vue";
        import SettingsView from "../views/SettingsView.vue";

        defineOptions({ name: "Worker" });

        const props = defineProps<{ title: string }>();
        const emit = defineEmits<{ update: [] }>();

        const title = format("Worker");
        const workerIndex: Map<string, Array<number>> = new Map();
        const routes = [
            {
                meta: { requiresAuth: true },
                path: "/calendar",
                name: "calendar",
                component: CalendarView,
                children: [{ path: "settings", component: SettingsView }]
            }
        ];

        function format(value: string): string {
            return value.trim();
        }

        function evaluate(count: number, enabled: boolean): number {
            let total = 0;
            if (enabled) {
                for (let i = 1; i <= count; i++) {
                    total += i;
                }
            } else if (count > 0) {
                total = 1;
            }
            return total;
        }

        defineExpose({ format, evaluate });
        </script>

        <style scoped>
        @charset "UTF-8";
        @namespace url(http://www.w3.org/1999/xhtml);

        :root {
          --accent: #0f766e;
        }

        .worker {
          color: #0f766e;
        }

        @media (min-width: 40rem) {
          .worker {
            display: block;
          }
        }

        @keyframes spin {
          from { opacity: 0; }
          to { opacity: 1; }
        }

        @supports (display: grid) {
          .worker { display: grid; }
        }

        @container (min-width: 20rem) {
          .worker { padding: 1rem; }
        }

        @font-face {
          font-family: "Worker";
          src: url("/worker.woff2");
        }

        @layer utilities {
          .m-0 { margin: 0; }
        }
        </style>

        """"),
        ("python", "source.py", """"
        from typing import Dict, List

        worker_index: Dict[str, List[int]] = {}


        class Worker:
            def __init__(self, id: int) -> None:
                self.id = id

            def run(self) -> int:
                record_run(self.id)
                return helper(self.id)

            @staticmethod
            def default_id() -> int:
                return 0


        def record_run(id: int) -> None:
            """Emits a worker-run marker for observability hooks."""
            observe_run("worker-run", id)


        def observe_run(event: str, id: int) -> None:
            """Records a named worker event for downstream hooks."""
            pass


        def helper(value: int) -> int:
            """Increment a worker id."""
            return value + 1


        def fetch_status() -> None:
            """Checks the worker service health endpoint."""
            fetch_url("https://api.example.com/workers/status")


        def fetch_url(url: str) -> None:
            pass


        def evaluate(count: int, enabled: bool) -> int:
            total = 0
            if enabled:
                for i in range(count):
                    total += i
            return total

        """"),
        ("java", "source.java", """"
        package fixture;

        interface Job {
            int run();
        }

        class Worker implements Job {
            private final int id;
            private Map<String, List<Integer>> index;

            Worker(int id) {
                this.id = id;
            }

            @Deprecated
            public int run() {
                recordRun(id);
                return helper(id);
            }

            private static final Object lock = new Object();

            static void guardedFetch() {
                synchronized (lock) {
                    fetchStatus();
                }
            }

            static void readConfig() {
                try (AutoCloseable stream = openStream()) {
                    stream.close();
                } catch (Exception ignored) {
                }
            }

            private static AutoCloseable openStream() {
                return () -> {};
            }

            @SuppressWarnings("unchecked")
            static void observeAsync(Runnable task) {
                Runnable wrapped = () -> task.run();
                wrapped.run();
            }

            /**
             * Increments a worker id.
             *
             * @param value the worker id
             * @return the incremented id
             */
            private static int helper(int value) {
                return value + 1;
            }

            /** Emits a worker-run marker for observability hooks. */
            private static void recordRun(int id) {
                observeRun("worker-run", id);
            }

            /** Records a named worker event for downstream hooks. */
            private static void observeRun(String event, int id) {
            }

            /** Checks the worker service health endpoint. */
            static void fetchStatus() {
                fetchUrl("https://api.example.com/workers/status");
            }

            private static void fetchUrl(String url) {
            }

            static int evaluate(int count, boolean enabled) {
                int total = 0;
                if (enabled) {
                    for (int i = 0; i < count; i++) {
                        total += i;
                    }
                }
                return total;
            }

            /** Fully-qualified static call: the terminal receiver `Worker` must stay name-visible. */
            static void auditViaQualifiedCall() {
                int latest = fixture.Worker.evaluate(2, true);
                fixture.Worker.observeRun("qualified-audit", latest);
            }

            void consume(Job[] jobs) {
                for (Job job : jobs) {
                    if (job instanceof Worker bound) {
                        bound.run();
                    }
                }
                this.recordRun(id);
            }
        }

        class Supervisor extends Worker {
            Supervisor(int id) {
                super(id);
            }

            @Override
            public int run() {
                return super.run();
            }

            void combine(BinaryOperator<Integer> op) {
                BinaryOperator<Integer> sum = (left, right) -> left + right;
                sum.apply(1, 2);
            }
        }

        """"),
        ("csharp", "source.cs", """"
        namespace Fixture;

        public interface IJob
        {
            int Run();
        }

        public sealed class Worker : IJob
        {
            public Worker(int id)
            {
                Id = id;
            }

            public int Id { get; }

            public int Run()
            {
                return Helper(Id);
            }

            /// <summary>Increments a worker id.</summary>
            /// <param name="value">The worker id.</param>
            /// <returns>The incremented id.</returns>
            [Obsolete("use IncrementV2")]
            private static int Helper(int value)
            {
                return value + 1;
            }
        }

        public static class ComplexityFixture
        {
            private static Dictionary<string, List<int>> index;

            public static int Evaluate(int count, bool enabled)
            {
                var total = 0;
                if (enabled)
                {
                    for (var i = 0; i < count; i++)
                    {
                        total += i;
                    }
                }
                return total;
            }
        }

        public static class GraphTraversal
        {
            public static int Reach(int seed) => seed;
        }

        public sealed class TraceAttribute : Attribute
        {
            public int Level;
        }

        // variable_ref reference cases: static-access receiver, object-initializer
        // member, attribute named argument, nameof operand, and a bare const read.
        public sealed class Registry
        {
            public int Capacity;
            private const int Default = 8;
            private const int Scale = 4;

            [Trace(Level = 1)]
            public int Configure(int requested)
            {
                var reached = GraphTraversal.Reach(requested);
                var slot = new Registry { Capacity = reached };
                var label = nameof(Default);
                return slot.Capacity > 0 ? reached : Default;
            }

            // Pointer mis-parse recovery: tree-sitter-c-sharp resolves `requested * Scale`
            // in argument position as a pointer-type declaration_expression. With no unsafe
            // context it is a multiplication, so both operands emit variable_ref (otherwise
            // the `Scale` const looks dead). Mirrors Miller's SymbolSuggestionEngine hit.
            public int Scaled(int requested)
            {
                return Math.Max(requested * Scale, 1);
            }
        }

        internal class VisibilityFixture
        {
            internal VisibilityFixture() { }
            internal int InternalMethod() => 1;
            internal int InternalProperty { get; set; }
            internal int InternalField;

            private int ExplicitPrivateField;
            int DefaultPrivateField;
            private int ExplicitPrivateProperty { get; set; }
            int DefaultPrivateProperty { get; set; }
            private int ExplicitPrivateMethod() => 2;
            int DefaultPrivateMethod() => 3;
        }

        public sealed class WorkerIndex
        {
            public IReadOnlyList<Worker> this[int i] => null;
        }

        """"),
        ("vbnet", "source.vb", """"
        Namespace Fixture
            Public Interface IJob
                Function Run() As Integer
            End Interface

            Public Class Worker
                Implements IJob

                Public Event Completed As EventHandler

                Private ReadOnly Index As Dictionary(Of String, List(Of Integer))

                <Obsolete("Use WorkerId")>
                Public Property Id As Integer

                <TestMethod>
                Public Function Run() As Integer Implements IJob.Run
                    RecordRun(Id)
                    Return Helper(Id)
                End Function

                Private Sub HandleClick(sender As Object, e As EventArgs) Handles Button.Click
                End Sub

                <Obsolete("Use HelperV2")>
                ''' <summary>Increments a worker id.</summary>
                Private Function Helper(value As Integer) As Integer
                    Return value + 1
                End Function

                Private Shared Sub RecordRun(id As Integer)
                    ObserveRun("worker-run", id)
                End Sub

                Private Shared Sub ObserveRun(eventName As String, id As Integer)
                End Sub

                ''' <summary>Checks the worker service health endpoint.</summary>
                Public Shared Sub FetchStatus()
                    FetchUrl("https://api.example.com/workers/status")
                End Sub

                Private Shared Sub FetchUrl(url As String)
                End Sub

                Public Sub ProbeFacts(ByVal a As Worker, ByRef b As Worker)
                    Dim nullableSeed As Integer?
                    Dim built = New Worker()
                    Dim asNew As New Worker()
                    Dim fromBuild = Build()
                    Me.Run()
                End Sub

                Public Function Evaluate(count As Integer, enabled As Boolean) As Integer
                    Dim total As Integer = 0
                    If enabled Then
                        For i As Integer = 1 To count
                            total += i
                        Next
                    ElseIf count > 0 Then
                        total = 1
                    End If
                    Return total
                End Function

                Public Sub ProbeShapes(ByVal ids() As Integer, ByRef pool As Worker())
                    Dim builder As System.Text.StringBuilder
                    Dim names As System.Collections.Generic.List(Of String)
                End Sub
            End Class
        End Namespace

        """"),
        ("php", "source.php", """"
        <?php

        namespace Fixture;

        use Symfony\Component\HttpFoundation\Response as HttpResponse;

        trait Timestampable
        {
        }

        #[\Attribute(\Attribute::TARGET_CLASS | \Attribute::TARGET_METHOD | \Attribute::TARGET_PROPERTY)]
        class Entity
        {
        }

        #[\Attribute(\Attribute::TARGET_METHOD)]
        class Route
        {
            public function __construct(public string $path)
            {
            }
        }

        #[\Attribute(\Attribute::TARGET_PROPERTY)]
        class Required
        {
        }

        #[Entity]
        class Worker
        {
            use Timestampable;

            #[Required]
            public int $id;

            public const STATUS = 'ready';

            public function __construct(int $id)
            {
                $this->id = $id;
            }

            #[Route('/run')]
            public function run(): int
            {
                recordRun($this->id);
                $this->missingWave2();
                return helper($this->id);
            }
        }

        /**
         * Increment a worker id.
         *
         * @param int $value the worker id
         * @return int the incremented id
         */
        function helper(int $value): int
        {
            return $value + 1;
        }

        /** Emits a worker-run marker for observability hooks. */
        function recordRun(int $id): void
        {
            observeRun("worker-run", $id);
        }

        /** Records a named worker event for downstream hooks. */
        function observeRun(string $event, int $id): void
        {
        }

        /** Checks the worker service health endpoint. */
        function fetchStatus(): void
        {
            fetchUrl("https://api.example.com/workers/status");
        }

        function fetchUrl(string $url): void
        {
        }

        function withMapper(int $value): int
        {
            $mapper = function (int $input): int {
                return $input + 1;
            };
            return $mapper($value);
        }

        function evaluate(int $count, bool $enabled): int
        {
            $total = 0;
            if ($enabled) {
                for ($i = 0; $i < $count; $i++) {
                    $total += $i;
                }
            } elseif ($count > 0) {
                $total = match (true) {
                    $count > 10 => 1,
                    default => 0,
                };
            }
            return $total;
        }

        """"),
        ("ruby", "source.rb", """"
        require "json"
        require_relative "./helper"

        class Widget
        end

        class Worker
          include Enumerable

          DEFAULT_LABEL = "worker"

          def initialize(id)
            @id = id
          end

          def reset
            @id = 0
          end

          def assemble(a, b = 1, *rest, key:, &blk)
            w = Widget.new
            u = Unknown.new
            n = Net::HTTP.new
            v = build
            self.helper
            self.missing_wave2
          end

          def run
            [1, 2].map { |value| helper(value) }
          end

          def risky
            1 / 0
          rescue ZeroDivisionError
            0
          end

          private

          # Increments a worker id.
          def helper(value)
            value + 1
          end

          def evaluate(count, enabled)
            total = 0
            if enabled
              for i in 0...count
                total += i
              end
            end
            until total >= count
              total += 1
            end
            total
          end
        end

        """"),
        ("swift", "source.swift", """"
        protocol Job {
            func run() -> Int
        }

        @MainActor
        struct Worker: Job {
            let id: Int
            let mapping: Array<Dictionary<String, Int>>

            func run() -> Int {
                recordRun(id)
                return helper(id)
            }
        }

        actor Counter {
            func increment() async {
                let next = await computeNext()
                _ = next
            }

            private func computeNext() async -> Int {
                return 1
            }
        }

        @available(iOS 17.0, *)
        extension Worker {
            @Published var status: String = "ready"
        }

        @available(*, deprecated, message: "use ModernHandler")
        typealias LegacyHandler = () -> Void

        enum WorkerStatus {
            @available(*, deprecated)
            case legacy
            case current
        }

        /// Increments a worker id.
        func helper(_ value: Int) -> Int {
            value + 1
        }

        /// Emits a worker-run marker for observability hooks.
        func recordRun(_ id: Int) {
            observeRun("worker-run", id: id)
        }

        /// Records a named worker event for downstream hooks.
        func observeRun(_ event: String, id: Int) {
        }

        /// Checks the worker service health endpoint.
        @available(iOS 13.0, *)
        func fetchStatus() {
            fetchUrl("https://api.example.com/workers/status")
        }

        func fetchUrl(_ url: String) {
        }

        func evaluate(_ count: Int, enabled: Bool) -> Int {
            var total = 0
            if enabled {
                for i in 0..<count {
                    total += i
                }
            }
            return total
        }

        class ReceiverBox: ServiceBase {
            let stored: Foo
            init(seed: Bar) {
                self.stored = seed
            }
            func use(x: Foo, y: inout Bar) {
                let optional: Foo? = nil
                let constructed = Foo()
                let unknown = Unknown()
                let imported = UIKit.UIView()
                let helpered = makeFoo()
                var items: [Foo] = []
                self.persist()
                super.restore()
                _ = optional
                _ = constructed
                _ = unknown
                _ = imported
                _ = helpered
                _ = items
                _ = x
                _ = y
            }
        }

        class ServiceBase {}
        class Foo {}
        func makeFoo() -> Foo {
            return Foo()
        }

        extension ReceiverBox {
            func extra() {
                self.persist()
            }
        }

        """"),
        ("kotlin", "source.kt", """"
        package fixture

        interface Job {
            fun run(): Int
        }

        @Singleton
        object WorkerRegistry

        @Suppress("UNCHECKED_CAST")
        typealias WorkerCallback = (Int) -> Unit

        @Deprecated("Use WorkerV2")
        class Worker(
            @Suppress("UNUSED") private val id: Int,
        ) : Job {
            @Volatile
            var status: String = "ready"

            private val index: List<Map<String, Int>> = emptyList()

            @Deprecated("Legacy entry point")
            override fun run(): Int {
                recordRun(id)
                return helper(id)
            }

            suspend fun loadRemote(): Int {
                return helper(id)
            }

            val runner by lazy { Worker(id) }

            /**
             * Increments a worker id.
             */
            private fun helper(value: Int): Int {
                return value + 1
            }

            /** Emits a worker-run marker for observability hooks. */
            private fun recordRun(id: Int) {
                observeRun("worker-run", id)
            }

            /** Records a named worker event for downstream hooks. */
            private fun observeRun(event: String, id: Int) {
            }

            /** Checks the worker service health endpoint. */
            fun fetchStatus() {
                fetchUrl("https://api.example.com/workers/status")
            }

            private fun fetchUrl(url: String) {
            }

            fun persist() {
                this.recordRun(id)
                this.missingWave2()
            }

            constructor(label: String) : this(label.length)
        }

        fun evaluate(count: Int, enabled: Boolean): Int {
            val maybe: Job? = null
            var total = 0
            if (enabled) {
                for (i in 0 until count) {
                    total += i
                }
            }
            return total
        }

        """"),
        ("scala", "source.scala", """"
        package fixture

        trait Job {
          def run(): Int
        }

        @deprecated("Use WorkerV2", since = "2.0")
        class Worker(val id: Int) extends Job {
          @deprecated("Prefer runSync", since = "2.0")
          def run(): Int = {
            recordRun(id)
            helper(id)
          }

          private def helper(value: Int): Int = value + 1

          /** Emits a worker-run marker for observability hooks. */
          private def recordRun(id: Int): Unit = {
            observeRun("worker-run", id)
          }

          /** Records a named worker event for downstream hooks. */
          private def observeRun(event: String, id: Int): Unit = ()

          /** Checks the worker service health endpoint. */
          def fetchStatus(): Unit = {
            fetchUrl("https://api.example.com/workers/status")
          }

          private def fetchUrl(url: String): Unit = ()

          def evaluate(count: Int, enabled: Boolean): Int = {
            var total = 0
            if (enabled) {
              for (i <- 0 until count) total += i
            } else if (count > 0) {
              total = if (count > 10) 1 else 0
            }
            total
          }

          def scanPositive(items: List[Int]): List[Int] =
            for {
              item <- items
              if item > 0
            } yield item * 2
        }

        given Ordering[Int] = Ordering.Int

        @singleton
        object WorkerRegistry {
          @tracked val runs: Int = 0
        }

        @opaque
        type WorkerId = Int

        extension (value: Int)
          @inline def doubled: Int = value * 2

        @deprecated("legacy", since = "1.0")
        def legacyHook(): Unit = ()

        class Foo

        case class Payload(a: Foo)
        class Query(a: Foo)

        class Widget {
          def this(seed: Foo) = this()
          def ping(): Unit = {
            val typed: Foo = null
            val constructed = new Foo()
            val sameFile = Foo()
            val unknown = Unknown()
            val imported = scala.collection.mutable.ListBuffer()
            val built = build()
            this.m()
            this.missingWave2()
            other.m()
          }
          def m(): Unit = ()
          def annotate(x: Foo, xs: List[Foo]): Unit = ()
        }

        def build(): Int = 1

        """"),
        ("dart", "source.dart", """"
        abstract class Job {
          int run();
        }

        class Worker extends Job {
          final int id;

          Worker(this.id);

          @override
          int run() {
            recordRun(id);
            return helper(id);
          }

          Future<int> loadRemote() async {
            return await helper(id);
          }
        }

        class Foo {
          Foo();
          Foo.named();
        }

        class ServiceBase {}

        class OrderService extends ServiceBase {
          void process(Foo x, List<Foo> xs, Worker other) {
            this.persist();
            super.restore();
            other.run();
            final Foo typed = Foo();
            final inferred = Foo();
            var constructed = new Foo();
            final named = Foo.named();
            Foo? nullable;
            final a = Unknown();
            final b = http.Client();
            final c = build();
          }
        }

        /// Increments a worker id.
        int helper(int value) {
          return value + 1;
        }

        /// Emits a worker-run marker for observability hooks.
        void recordRun(int id) {
          observeRun("worker-run", id);
        }

        /// Records a named worker event for downstream hooks.
        void observeRun(String event, int id) {
        }

        /// Checks the worker service health endpoint.
        void fetchStatus() {
          fetchUrl("https://api.example.com/workers/status");
        }

        void fetchUrl(String url) {
        }

        int evaluate(int count, bool enabled) {
          var total = 0;
          if (enabled) {
            for (var i = 0; i < count; i++) {
              total += i;
            }
          }
          return total;
        }

        """"),
        ("elixir", "source.ex", """"
        defmodule Fixture.Worker do
          @moduledoc "Worker helpers for fixture extraction."
          @spec run(integer()) :: integer()
          @type worker_index :: list(list(integer()))

          import Kernel, only: [apply: 2]
          alias Fixture.Helper
          require Logger

          def run(id) do
            record_run(id)
            helper(id)
          end

          @doc "Increment a worker id."
          defp helper(value) do
            value + 1
          end

          defp record_run(id) do
            observe_run("worker-run", id)
          end

          defp observe_run(_event, _id), do: :ok

          @doc "Checks the worker service health endpoint."
          def fetch_status do
            fetch_url("https://api.example.com/workers/status")
          end

          defp fetch_url(_url), do: :ok

          def piped(id), do: id |> helper() |> Kernel.abs()

          def safe_div(a, b) do
            with true <- b != 0 do
              div(a, b)
            else
              _ -> 0
            end
          end

          def evaluate(count, enabled) do
            if enabled do
              for i <- 1..count, reduce: 0 do
                acc -> acc + i
              end
            else
              if count > 0, do: 1, else: 0
            end
          end

          def bind(%Worker{} = w, n), do: {w, n}

          def assemble(x) do
            y = %Job{id: x}
            z = Map.new()
            q = %{a: 1}
            {y, z, q}
          end

        end

        """"),
        ("fsharp", "source.fs", """"
        module Domain =
          open System
          open System.Collections.Generic

          /// Coordinates used by the domain model.
          [<Struct>]
          type Point = { X: int; Y: int }

          type Shape =
            | Circle of radius: float
            | Empty

          type Id = int
          type Foo() = class end
          type Bar() = class end


          type Base() = class end

          type Calculator(value: int) =
            inherit Base()

            /// Current calculator value.
            [<Obsolete>]
            member _.Value = value

            member _.Calculate() =
              if value > 0 then value else 0

            static member Create() = Calculator(0)

            member this.Helper() = 0
            member this.Run(a: Bar) = this.Helper()
            member x.Go() = x.Helper()
            member this.CallOther(other: Calculator) = other.Helper()


          let createPoint: Point = { X = 1; Y = 2 }
          let convert (value: Point) : Result<Point, string> = Ok value

          let f (x: Foo) (xs: Foo list) y = y

          let local value = value + 1

          let callPoint point =
            local point
            System.Console.WriteLine(point.X)
            point.X

          let makeCalculator = Calculator(1)
          let literalString = "hello"
          let literalChar = 'x'
          let literalInt = 42
          let literalFloat = 3.14
          let literalDecimal = 1.5M
          let literalBool = true
          let literalUnit = ()

          let flow count =
            if count > 0 then
              match count with
              | 1 -> 1
              | n when n > 1 -> n
              | _ -> 0
            else
              try
                while count > 0 do
                  ()
                0
              with
              | :? Exception -> -1
              | _ -> -2

        """"),
        ("erlang", "source.erl", """"
        %% @doc Account bookkeeping primitives.
        %% Balances are stored in whole cents.
        -module(bank).
        -moduledoc "Account ledger entry points.".

        -behaviour(gen_server).

        -export([open/1, balance/1, deposit/2, history/1, run/2, go/1, scratch/0]).
        -export_type([account/0]).
        -import(lists, [reverse/1]).

        -define(MAX_BALANCE, 1000000).
        -define(LOG(Msg), io:format("~p~n", [Msg])).

        -record(account, {id :: integer(), balance = 0 :: integer()}).
        -record(state, {n = 0}).
        -record(req, {id}).

        -type account() :: #account{}.
        -opaque token() :: binary().

        -callback init(Args :: term()) -> {ok, term()}.

        -spec open(integer()) -> #account{}.
        %% @doc Open a new account with a zero balance.
        open(Id) ->
            #account{id = Id}.

        -doc "Read the stored balance of an account.".
        balance(#account{balance = B}) ->
            B.

        deposit(Acct, Amount) when Amount > 0 ->
            Acct#account{balance = Acct#account.balance + Amount};
        deposit(Acct, _Amount) ->
            Acct.

        % internal helper, never exported
        audit(Acct) ->
            ?LOG(Acct),
            ok.

        -doc "Summarise an account for the audit log.".
        history(Acct) ->
            Ids = reverse([Acct#account.id]),
            Limit = ?MAX_BALANCE,
            Reader = fun balance/1,
            Sizer = fun erlang:length/1,
            {Ids, Limit, Reader, Sizer, self()}.

        balance_test() ->
            0 = balance(#account{id = 1}).

        run(#state{} = S, N) ->
            {S, N};
        run(S, 0) ->
            S.

        go(X) ->
            R = #req{id = X},
            R.

        scratch() ->
            M = maps:new(),
            M.

        """"),
        ("lua", "source.lua", """"
        local json = require("json")

        local Worker = {}
        Worker.__index = Worker

        --- Increment a worker id.
        local function helper(value)
            return value + 1
        end

        local function run_worker(worker)
            return helper(worker.id)
        end

        function Worker:new(id)
            return setmetatable({ id = id }, Worker)
        end

        function Worker:run()
            return helper(self.id)
        end

        function Worker:log()
            self:missing_wave2()
            return self:run()
        end

        local function evaluate(count, enabled)
            local total = 0
            if enabled then
                for i = 1, count do
                    total = total + i
                end
            elseif count > 0 then
                total = count > 10 and 1 or 0
            end
            return total
        end

        local co = coroutine.create(function()
            return 1
        end)

        local worker = Worker.new(1)
        local boxed = setmetatable({}, Worker)

        return Worker

        """"),
        ("qml", "source.qml", """"
        import QtQuick 2.15

        Item {
            id: root
            property string title: "Worker"
            property int workerId: 0
            property LocalCard card
            property list<Item> rows
            property var payload
            property alias label: title
            signal activated(string value)

            /**
             * Format the badge label.
             */
            function format(value) {
                return value.trim()
            }

            function buildIndex(m: Map<string, Array<User>>): void {
                void m
            }

            function run() {
                recordRun(workerId)
            }

            function recordRun(id) {
                observeRun("worker-run", id)
            }

            function observeRun(event, id) {
            }

            function fetchStatus() {
                fetchUrl("https://api.example.com/workers/status")
            }

            function fetchUrl(url) {
            }

            function formatPair(title, count) {
            }

            function seed() {
                this.missingWave2()
                let localCard = new LocalCard()
                let d = new Date()
                let n = compute()
                let graph = new ns.GraphTraversal()
            }

            function evaluate(count, enabled) {
                var total = 0
                if (enabled) {
                    for (var i = 1; i <= count; i++) {
                        total += i
                    }
                } else if (count > 0) {
                    total = 1
                }
                return total
            }

            Text {
                text: root.format(root.title)
            }
        }

        """"),
        ("qmldir", "qmldir", """"
        module Example.Module
        Button 1.0 Button.qml
        singleton Theme 1.0 Theme.qml
        internal Private Private.qml
        MyScript 1.0 MyScript.js
        plugin examplemodule plugins
        optional plugin optionalmodule
        classname ExamplePlugin
        typeinfo plugins.qmltypes
        depends QtQuick 2.15
        import Shared auto
        optional import Optional.Module 1.0
        default import Default.Module 2.0
        designersupported
        prefer :/qt/qml/Example/Module
        linktarget ExampleModule
        not_a_directive not-a-version not-a-file

        """"),
        ("r", "source.r", """"
        library(dplyr)

        #' Increment a worker id.
        #' @param value worker id
        helper <- function(value) {
          value + 1
        }

        model_formula <- total ~ count

        run_worker <- function(id) {
          id |> helper()
        }

        evaluate <- function(count, enabled) {
          total <- 0
          if (enabled) {
            for (i in 1:count) {
              total <- total + i
            }
          } else if (count > 0) {
            total <- 1
          }
          total <- switch(count %% 3, total, total + 1, total + 2)
          total
        }

        Worker <- R6::R6Class(
          "Worker",
          public = list(
            id = NULL,
            initialize = function(id) {
              self$id <- id
            },
            run = function() {
              helper(self$id)
              self$missing_wave2()
            }
          )
        )

        w <- Worker$new()

        """"),
        ("bash", "source.sh", """"
        #!/usr/bin/env bash

        helper() {
            local value="$1"
            echo "$((value + 1))"
        }

        run_worker() {
            helper "$1"
        }

        evaluate() {
            local count=$1
            local enabled=$2
            local total=0
            if [ "$enabled" = "true" ]; then
                for i in $(seq 1 "$count"); do
                    total=$((total + i))
                done
            elif [ "$count" -gt 0 ]; then
                total=1
            fi
            echo "$total"
        }

        export APP_ENV="production"
        readonly MAX=3

        """"),
        ("powershell", "source.ps1", """"
        function Invoke-Helper {
            param([int]$Value)
            return $Value + 1
        }

        function Invoke-Run {
            param([int]$Value)
            return Invoke-Helper $Value
        }

        function Evaluate {
            [CmdletBinding()]
            param([int]$Count, [bool]$Enabled)
            $total = 0
            if ($Enabled) {
                for ($i = 1; $i -le $Count; $i++) {
                    $total += $i
                }
            } elseif ($Count -gt 0) {
                $total = 1
            }
            return $total
        }

        function Get-Filtered {
            Get-Process | Select-Object -First 1
        }

        [Dictionary[string, List[int]]]$script:WorkerIndex = @{}

        class Worker {
            [int]$Id

            Worker([int]$id) {
                $this.Id = $id
            }

            [int] Run() {
                return Invoke-Helper $this.Id
            }
        }

        function Get-Name {
            [CmdletBinding()]
            param(
                [Parameter()]
                [string]
                $Name
            )
        }

        class Widget {
            [string]$Title

            Widget() {}

            [void] Run([Foo]$f) {
                $this.Run($f)
                $other.Run($f)
            }
        }

        function Use-Facts {
            [System.Collections.Generic.List[string]]$items = @()
            $w = [Widget]::new()
            $n = New-Object Widget
            $g = Get-Thing
        }

        function Use-Arrays {
            [string[]]$names = @()
            [int[,]]$grid = $null
        }

        """"),
        ("gdscript", "source.gd", """"
        class_name Worker
        extends Node

        signal activated(value)

        @export var id: int
        var worker_index: Array[Array[int]]

        func _init(value: int) -> void:
            id = value

        func run() -> int:
            record_run(id)
            return helper(id)

        ## Increment a worker id.
        func helper(value: int) -> int:
            return value + 1

        func record_run(worker_id: int) -> void:
            observe_run("worker-run", worker_id)

        func observe_run(_event: String, _worker_id: int) -> void:
            pass

        func fetch_status() -> void:
            fetch_url("https://api.example.com/workers/status")

        func fetch_url(_url: String) -> void:
            pass

        func evaluate(count: int, enabled: bool) -> int:
            var total = 0
            if enabled:
                for i in range(1, count + 1):
                    total += i
            elif count > 0:
                total = 1
            match count % 3:
                0:
                    total += 0
                1:
                    total += 1
                _:
                    total += 2
            return total

        func typed_params(x: Foo, y := 2, z) -> void:
            var typed_local: Foo = null
            var inferred_local := Foo.new()
            var unknown_local = Unknown.new()
            var loaded = load("res://x.tscn").instantiate()
            var made = make()
            var items: Array[Foo]
            self.persist()
            super.restore()

        class Foo:
            pass

        class Bar extends Resource:
            func inner_run() -> void:
                self.persist()
                super.restore()

        """"),
        ("razor", "source.razor", """"
        @page "/worker"

        <h1>@Format(Title)</h1>

        <WorkerCard Snapshot="Title" />
        <input @bind-Value="Title" />

        @code {
            [Route("/worker")]
            public partial class WorkerPage
            {
                [Parameter]
                public string Title { get; set; } = "Worker";

                private Dictionary<string, List<int>> index;

                [Authorize]
                private int Run(int id)
                {
                    RecordRun(id);
                    this.Refresh();
                    return Helper(id);
                }

                private string Format(string value)
                {
                    return value.Trim();
                }

                private int Evaluate(int count, bool enabled)
                {
                    int total = 0;
                    if (enabled)
                    {
                        for (int i = 1; i <= count; i++)
                        {
                            total += i;
                        }
                    }
                    else if (count > 0)
                    {
                        total = 1;
                    }
                    return total;
                }

                private static int Helper(int value)
                {
                    return value + 1;
                }

                private static void RecordRun(int id)
                {
                    ObserveRun("worker-run", id);
                }

                private static void ObserveRun(string eventName, int id)
                {
                }

                public static void FetchStatus()
                {
                    FetchUrl("https://api.example.com/workers/status");
                }

                private static void FetchUrl(string url)
                {
                }
            }
        }

        """"),
        ("sql", "source.sql", """"
        CREATE TABLE workers (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL DEFAULT 'fixture-worker'
        );

        CREATE TABLE jobs (
            id INTEGER PRIMARY KEY,
            worker_id INTEGER NOT NULL,
            FOREIGN KEY (worker_id) REFERENCES workers(id),
            CONSTRAINT chk_worker_id_positive CHECK (worker_id > 0)
        );

        CREATE VIEW active_workers AS
        SELECT id, name
        FROM workers
        WHERE id > 0;

        CREATE INDEX idx_workers_name ON workers (name);

        WITH recent_workers AS (
            SELECT id, name FROM workers WHERE id > 0
        )
        SELECT w.id, w.name
        FROM recent_workers rw
        JOIN workers w ON rw.id = w.id;

        BEGIN;
        UPDATE workers SET name = 'updated' WHERE id = 1;
        COMMIT;

        CREATE TRIGGER refresh_active_workers
        AFTER INSERT ON workers
        FOR EACH ROW
        BEGIN
            INSERT INTO jobs (worker_id)
            SELECT NEW.id
            FROM workers
            WHERE NEW.id > 0;
        END;

        """"),
        ("regex", "source.regex", """"
        ^(?<name>[A-Za-z]+)-(?<id>\d+)-\k<name>-(foo)-\3$

        """"),
        ("markdown", "source.md", """"
        ---
        title: Worker Guide
        tags:
          - docs
          - api
        ---

        # Worker Guide

        Use `run_worker` to process a worker id.

        ## Usage

        Review the [Worker API](https://api.example.com/workers) before running a job.

        ```rust
        fn helper(value: i32) -> i32 {
            value + 1
        }
        ```

        [worker-ref]: https://api.example.com/workers "Worker API"

        | Field | Value |
        | ----- | ----- |
        | id | 1 |
        | name | fixture |

        """"),
        ("json", "source.json", """"
        {
          "worker": {
            "id": 1,
            "name": "fixture",
            "active": true,
            "tags": ["fixture", "active"],
            "api_url": "https://api.example.com/workers/status"
          }
        }

        """"),
        ("toml", "source.toml", """"
        [worker]
        id = 1
        name = "fixture"
        active = true
        profile = { role = "admin", active = true }
        api_url = "https://api.example.com/workers/status"

        [[items]]
        key = "alpha"

        """"),
        ("yaml", "source.yaml", """"
        defaults: &defaults
          active: true

        # Worker configuration section
        worker:
          <<: *defaults
          # Primary worker identifier
          id: 1
          name: "fixture"
          tags:
            - fixture
            - active
          api_url: "https://api.example.com/workers/status"

        """"),
        ("xml", "source.xml", """"
        <?xml version="1.0" encoding="UTF-8"?>
        <!-- Application configuration for the phone book service. -->
        <configuration name="phonebook">
          <appSettings>
            <add id="ConnectionTimeout" type="xs:int">30</add>
            <add id="RetryCount" type="xs:int">3</add>
            <add />
          </appSettings>
          <logging name="serilog">
            <sinks>
              <sink name="console" type="Serilog.Sinks.Console" />
              <sink name="file" type="Serilog.Sinks.File">
                <path>logs/phonebook.log</path>
              </sink>
            </sinks>
          </logging>
          <features>
            <feature>audit</feature>
            <feature>export</feature>
          </features>
        </configuration>

        """"),
    ];
}
