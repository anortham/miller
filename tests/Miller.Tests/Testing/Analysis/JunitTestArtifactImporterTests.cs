using Miller.Testing;
using Miller.Testing.Parsing;
using Xunit;

namespace Miller.Tests.Testing.Analysis;

public sealed class JunitTestArtifactImporterTests : IDisposable
{
    private const string Workspace = "ws:1";
    private static readonly CtFreshnessKey Fresh = new("gen-1", 1);

    private readonly string _dir;
    private readonly string _dbPath;

    public JunitTestArtifactImporterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-ct-junit-import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, CtSchema.DbFileName);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Import_writes_artifact_cases_results_and_current_statuses()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteJunitArtifact(root);

        var report = JunitTestArtifactImporter.Import(store, Request(root, artifact));

        Assert.Equal("test_results", report.Kind);
        Assert.Equal("junit", report.Parser);
        Assert.Equal("failed", report.State);
        Assert.Equal(1, report.Counts["artifacts"]);
        Assert.Equal(1, report.Counts["suites"]);
        Assert.Equal(3, report.Counts["cases"]);
        Assert.Equal(3, report.Counts["results"]);
        Assert.Equal("artifacts/junit.xml", report.ArtifactPath);

        Assert.Single(store.ListRunArtifacts(Workspace));
        Assert.Single(store.ListTestRuns(Workspace));
        Assert.Equal(3, store.ListTestCases(Workspace).Count);
        Assert.Equal(3, store.ListTestResults(Workspace).Count);
        Assert.Equal(report.ArtifactId, Assert.Single(store.ListTestRuns(Workspace)).ArtifactId);
        Assert.Equal(3, store.ListTestResults(Workspace).Count(row => row.SourceArtifactId == report.ArtifactId));

        var cases = store.ListTestCases(Workspace);
        Assert.Equal(
            [
                "tests/test_billing::test_charge_card",
                "tests/test_billing::test_declined_card",
                "tests/test_billing::test_refund_card",
            ],
            cases.Select(row => row.Selector).ToArray());
        Assert.All(cases, row =>
        {
            Assert.Equal("artifact", row.Source);
            Assert.Equal(ContinuousTestRole.TestCase, row.Role);
            Assert.Equal(0.75, row.Confidence);
        });

        var caseNames = cases.ToDictionary(row => row.Id, row => row.Name, StringComparer.Ordinal);
        var statuses = store.ListContinuousTestStatuses(Workspace);
        Assert.Equal(
            [
                ("test_charge_card", ContinuousTestState.Green),
                ("test_declined_card", ContinuousTestState.Red),
                ("test_refund_card", ContinuousTestState.Skipped),
            ],
            statuses
                .Select(status => (caseNames[status.TestCaseId], status.State))
                .OrderBy(row => row.Item1, StringComparer.Ordinal)
                .ToArray());
        var red = statuses.Single(status => status.State == ContinuousTestState.Red);
        Assert.Equal("AssertionError: card declined", red.FailureSummary);
        Assert.Equal("1", red.LastRunRevision);
    }

    [Fact]
    public void Import_is_idempotent_by_artifact_hash()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteJunitArtifact(root);

        var first = JunitTestArtifactImporter.Import(store, Request(root, artifact));
        var second = JunitTestArtifactImporter.Import(store, Request(root, artifact));

        Assert.Equal(first.ArtifactId, second.ArtifactId);
        Assert.Equal(first.TestRunId, second.TestRunId);
        Assert.Single(store.ListRunArtifacts(Workspace));
        Assert.Single(store.ListTestRuns(Workspace));
        Assert.Equal(3, store.ListTestCases(Workspace).Count);
        Assert.Equal(3, store.ListTestResults(Workspace).Count);
    }

    [Fact]
    public void Import_reconciles_parsed_cases_to_existing_provider_test_case_ids()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteSingleJunitArtifactAt(root, Path.Combine("artifacts", "junit.xml"));
        store.PutTestCase(new ContinuousTestCase(
            Id: "provider:test:1",
            WorkspaceId: Workspace,
            Name: "test_charge_card",
            QualifiedName: "tests.test_billing.test_charge_card",
            Selector: "-id xunit-id-1",
            Framework: "xunit",
            Role: ContinuousTestRole.TestCase,
            Source: "ct-provider:dotnet",
            Confidence: 1.0,
            Metadata: new Dictionary<string, object?>
            {
                ["class"] = "tests.test_billing",
                ["method"] = "test_charge_card",
            }));

        var report = JunitTestArtifactImporter.Import(
            store,
            Request(
                root,
                artifact,
                runId: "run:provider",
                testCaseIdsBySelector: new Dictionary<string, string>
                {
                    ["tests/test_billing::test_charge_card"] = "provider:test:1",
                }));

        Assert.Equal("run:provider", report.TestRunId);
        var testCase = Assert.Single(store.ListTestCases(Workspace));
        Assert.Equal("ct-provider:dotnet", testCase.Source);
        Assert.Equal(1.0, testCase.Confidence);
        var result = Assert.Single(store.ListTestResults(Workspace));
        Assert.Equal("provider:test:1", result.TestCaseId);
        Assert.Equal(report.ArtifactId, result.SourceArtifactId);
        Assert.Equal("passed", store.ListContinuousTestStatuses(Workspace).Single().LastResultStatus);
    }

    /// <summary>
    /// Every data row of a theory keeps its own result row.
    ///
    /// <para>The three artifact rows share one <c>class::method</c> key. While that key was a plain
    /// assignment the last case written won it, all three results carried one test case id, and the
    /// upsert on (workspace, case, run) left ONE row for the whole theory - so the red row was
    /// published as whatever its last sibling did.</para>
    /// </summary>
    [Fact]
    public void Import_keeps_one_result_row_for_each_theory_data_row()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteTheoryJunitArtifact(root);
        PutTheoryCase(store, "5", 1);
        PutTheoryCase(store, "0", 2);
        PutTheoryCase(store, "7", 3);

        var report = JunitTestArtifactImporter.Import(
            store,
            Request(root, artifact, runId: "run:theory", testCaseIdsBySelector: ArtifactSelectorMap(store)));

        Assert.Equal("failed", report.State);
        Assert.Equal(3, store.ListTestCases(Workspace).Count);
        var results = store.ListTestResults(Workspace);
        Assert.Equal(3, results.Count);
        Assert.Equal(
            [
                ("xunit:" + TheoryDisplayName("0"), "failed"),
                ("xunit:" + TheoryDisplayName("5"), "passed"),
                ("xunit:" + TheoryDisplayName("7"), "passed"),
            ],
            results
                .Select(row => (row.TestCaseId, row.Status))
                .OrderBy(row => row.TestCaseId, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            "AssertionError: amount 0 was charged",
            results.Single(row => row.Status == "failed").FailureSummary);
    }

    /// <summary>
    /// The red data row survives its green siblings all the way to the published state.
    /// </summary>
    [Fact]
    public void Import_does_not_let_a_green_theory_row_overwrite_a_red_sibling()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteTheoryJunitArtifact(root);
        PutTheoryCase(store, "5", 1);
        PutTheoryCase(store, "0", 2);
        PutTheoryCase(store, "7", 3);

        JunitTestArtifactImporter.Import(
            store,
            Request(root, artifact, runId: "run:theory", testCaseIdsBySelector: ArtifactSelectorMap(store)));

        var statuses = store.ListContinuousTestStatuses(Workspace);
        Assert.Equal(
            [
                ("xunit:" + TheoryDisplayName("0"), ContinuousTestState.Red),
                ("xunit:" + TheoryDisplayName("5"), ContinuousTestState.Green),
                ("xunit:" + TheoryDisplayName("7"), ContinuousTestState.Green),
            ],
            statuses
                .Select(status => (status.TestCaseId, status.State))
                .OrderBy(row => row.TestCaseId, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            "AssertionError: amount 0 was charged",
            statuses.Single(status => status.State == ContinuousTestState.Red).FailureSummary);
    }

    /// <summary>
    /// The same guarantee against a REAL artifact: <see cref="RealXunitTheoryArtifact"/> is the file a
    /// live <c>Miller.Tests.exe -jUnit</c> run wrote for a three-row theory, so the row shape, the
    /// reporter's backslash escaping, and the failure element are the runner's own, not a guess.
    /// </summary>
    [Fact]
    public void Import_maps_a_real_xunit_junit_artifact_row_to_its_own_theory_case()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteArtifact(root, Path.Combine("artifacts", "real.junit.xml"), RealXunitTheoryArtifact);
        PutRealTheoryCase(store, "visa", 1);
        PutRealTheoryCase(store, "amex", 2);
        PutRealTheoryCase(store, "maestro", 3);

        JunitTestArtifactImporter.Import(
            store,
            Request(root, artifact, runId: "run:real", testCaseIdsBySelector: ArtifactSelectorMap(store)));

        Assert.Equal(3, store.ListTestCases(Workspace).Count);
        var results = store.ListTestResults(Workspace);
        Assert.Equal(3, results.Count);
        Assert.Equal(
            [
                ("xunit:" + RealDisplayName("amex"), "failed"),
                ("xunit:" + RealDisplayName("maestro"), "passed"),
                ("xunit:" + RealDisplayName("visa"), "passed"),
            ],
            results
                .Select(row => (row.TestCaseId, row.Status))
                .OrderBy(row => row.TestCaseId, StringComparer.Ordinal)
                .ToArray());
        Assert.Contains(
            "Temp_capture_probe_theory(String card)",
            results.Single(row => row.Status == "failed").FailureSummary);
    }

    /// <summary>
    /// The collapsed fallback still resolves. A provider that does not pre-enumerate, and a theory whose
    /// data cannot be enumerated up front, produce ONE case for ONE selector; that case is the only
    /// claimant of its <c>class::method</c> key, so an argument-carrying artifact row still finds it.
    /// </summary>
    [Fact]
    public void Import_still_resolves_a_single_case_through_the_collapsed_selector()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteArtifact(
            root,
            Path.Combine("artifacts", "single-theory.junit.xml"),
            OneTheoryRowArtifact("5"));
        store.PutTestCase(DelayEnumeratedTheoryCase());

        JunitTestArtifactImporter.Import(
            store,
            Request(root, artifact, runId: "run:delayed", testCaseIdsBySelector: ArtifactSelectorMap(store)));

        string expectedId = "xunit:" + TheoryClass + "." + TheoryMethod;
        Assert.Equal(expectedId, Assert.Single(store.ListTestCases(Workspace)).Id);
        var result = Assert.Single(store.ListTestResults(Workspace));
        Assert.Equal(expectedId, result.TestCaseId);
        Assert.Equal("passed", result.Status);
    }

    /// <summary>
    /// An artifact row that matches only a collapsed key MORE THAN ONE case claims resolves to nothing
    /// and gets its own artifact case, rather than being attributed to an arbitrary sibling.
    /// </summary>
    [Fact]
    public void Import_does_not_attribute_a_row_to_an_ambiguous_class_method_key()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteArtifact(
            root,
            Path.Combine("artifacts", "unknown-row.junit.xml"),
            OneTheoryRowArtifact("99"));
        PutTheoryCase(store, "5", 1);
        PutTheoryCase(store, "0", 2);
        PutTheoryCase(store, "7", 3);

        JunitTestArtifactImporter.Import(
            store,
            Request(root, artifact, runId: "run:unknown", testCaseIdsBySelector: ArtifactSelectorMap(store)));

        Assert.Equal(4, store.ListTestCases(Workspace).Count);
        var result = Assert.Single(store.ListTestResults(Workspace));
        Assert.DoesNotContain(
            result.TestCaseId,
            new[] { "5", "0", "7" }.Select(amount => "xunit:" + TheoryDisplayName(amount)).ToArray());
        var minted = store.ListTestCases(Workspace).Single(row => row.Id == result.TestCaseId);
        Assert.Equal("artifact", minted.Source);
        Assert.Equal("Payments/Tests/ChargeTests::Charges_the_card(amount: 99)", minted.Selector);
    }

    /// <summary>
    /// The provider-selector key is ambiguity-aware too: two cases that claim one selector resolve to
    /// nothing, so the artifact row keeps its own case instead of taking a sibling's id.
    /// </summary>
    [Fact]
    public void Import_does_not_attribute_a_row_to_an_ambiguous_provider_selector()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteSingleJunitArtifactAt(root, Path.Combine("artifacts", "junit.xml"));
        store.PutTestCase(ClaimantCase("provider:python:1", "ct-provider:python"));
        store.PutTestCase(ClaimantCase("provider:dotnet:1", "ct-provider:dotnet"));

        JunitTestArtifactImporter.Import(
            store,
            Request(root, artifact, runId: "run:ambiguous", testCaseIdsBySelector: ArtifactSelectorMap(store)));

        Assert.Equal(3, store.ListTestCases(Workspace).Count);
        var result = Assert.Single(store.ListTestResults(Workspace));
        Assert.NotEqual("provider:python:1", result.TestCaseId);
        Assert.NotEqual("provider:dotnet:1", result.TestCaseId);
    }

    [Fact]
    public void Import_rejects_artifacts_outside_workspace_root()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var outside = WriteJunitArtifactAt(_dir, "outside-junit.xml");

        var ex = Assert.Throws<ArgumentException>(() =>
            JunitTestArtifactImporter.Import(store, Request(root, outside)));

        Assert.Equal("artifactPath", ex.ParamName);
        Assert.Empty(store.ListRunArtifacts(Workspace));
    }

    [Fact]
    public void Import_allows_artifact_names_that_start_with_two_dots_inside_workspace_root()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteJunitArtifactAt(root, "..junit.xml");

        var report = JunitTestArtifactImporter.Import(store, Request(root, artifact));

        Assert.Equal("..junit.xml", report.ArtifactPath);
        Assert.Equal(3, store.ListTestResults(Workspace).Count);
    }

    [Fact]
    public void Import_rejects_dtd_entity_payload_and_writes_nothing()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteArtifact(
            root,
            Path.Combine("artifacts", "xxe.xml"),
            """
            <?xml version="1.0"?>
            <!DOCTYPE testsuite [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <testsuite name="pytest"><testcase name="&xxe;" /></testsuite>
            """);

        var ex = Assert.Throws<TestArtifactParseException>(() =>
            JunitTestArtifactImporter.Import(store, Request(root, artifact)));

        Assert.Equal("test_artifact.parse_error", ex.Code);
        Assert.Contains("unsafe XML", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.ListRunArtifacts(Workspace));
        Assert.Empty(store.ListTestCases(Workspace));
        Assert.Empty(store.ListTestResults(Workspace));
    }

    [Fact]
    public void Import_rejects_truncated_xml_and_writes_nothing()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteArtifact(root, Path.Combine("artifacts", "trunc.xml"), "<testsuite><testcase name=\"oops\"");

        var ex = Assert.Throws<TestArtifactParseException>(() =>
            JunitTestArtifactImporter.Import(store, Request(root, artifact)));

        Assert.Contains("malformed XML", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.ListRunArtifacts(Workspace));
        Assert.Empty(store.ListTestResults(Workspace));
    }

    [Fact]
    public void Import_rejects_garbage_payload_and_writes_nothing()
    {
        using var store = new ContinuousTestStore(_dbPath);
        var root = WorkspaceRoot();
        var artifact = WriteArtifact(root, Path.Combine("artifacts", "junk.xml"), "this is not xml at all <<<");

        Assert.Throws<TestArtifactParseException>(() =>
            JunitTestArtifactImporter.Import(store, Request(root, artifact)));
        Assert.Empty(store.ListRunArtifacts(Workspace));
        Assert.Empty(store.ListTestCases(Workspace));
    }

    private string WorkspaceRoot()
    {
        var root = Path.Combine(_dir, "repo");
        Directory.CreateDirectory(Path.Combine(root, "artifacts"));
        return root;
    }

    private static JunitTestArtifactImportRequest Request(
        string root,
        string artifact,
        string? runId = null,
        IReadOnlyDictionary<string, string>? testCaseIdsBySelector = null) =>
        new(
            WorkspaceId: Workspace,
            WorkspaceRoot: root,
            ArtifactPath: artifact,
            SelectedRevision: "1",
            IndexIdentity: Fresh.IndexIdentity,
            Revision: Fresh.Revision,
            RunId: runId,
            TestCaseIdsBySelector: testCaseIdsBySelector);

    private static string WriteJunitArtifact(string root) =>
        WriteJunitArtifactAt(root, Path.Combine("artifacts", "junit.xml"));

    private static string WriteSingleJunitArtifactAt(string root, string relativePath) =>
        WriteArtifact(
            root,
            relativePath,
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <testsuite name="pytest" tests="1">
              <testcase classname="tests.test_billing" name="test_charge_card" time="0.041" />
            </testsuite>
            """);

    private static string WriteJunitArtifactAt(string root, string relativePath) =>
        WriteArtifact(
            root,
            relativePath,
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <testsuite name="pytest" tests="3" failures="1" skipped="1" time="0.126">
              <testcase classname="tests.test_billing" name="test_charge_card" time="0.041" />
              <testcase classname="tests.test_billing" name="test_declined_card" time="0.052">
                <failure message="assert False">AssertionError: card declined</failure>
              </testcase>
              <testcase classname="tests.test_billing" name="test_refund_card" time="0.033">
                <skipped message="not implemented" />
              </testcase>
            </testsuite>
            """);

    private const string TheoryClass = "Payments.Tests.ChargeTests";
    private const string TheoryMethod = "Charges_the_card";
    private const string RealTheoryClass = "Miller.Tests.Testing.Analysis.JunitTestArtifactImporterTests";
    private const string RealTheoryMethod = "Temp_capture_probe_theory";

    /// <summary>
    /// The artifact-selector mapping under test, built the way the daemon builds it for an import.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ArtifactSelectorMap(ContinuousTestStore store) =>
        ContinuousTestCoordinator.TestCaseIdsByArtifactSelector(store.ListTestCases(Workspace));

    private static string TheoryDisplayName(string amount) =>
        $"{TheoryClass}.{TheoryMethod}(amount: {amount})";

    private static string RealDisplayName(string card) =>
        $"{RealTheoryClass}.{RealTheoryMethod}(card: \"{card}\")";

    /// <summary>
    /// One pre-enumerated data row of a theory, as xUnit discovery reports it: the display name carries
    /// the arguments while the class and method metadata are shared by every row.
    /// </summary>
    /// <remarks>
    /// Discovery gives every row of a theory the same <c>-method</c> selector, which the unique index on
    /// (workspace_id, selector, source) still rejects, so the rows carry distinct <c>-id</c> selectors
    /// here. The collapsed class::method key is shared either way, and that is the key under test.
    /// </remarks>
    private static void PutTheoryCase(ContinuousTestStore store, string amount, int ordinal) =>
        store.PutTestCase(new ContinuousTestCase(
            Id: "xunit:" + TheoryDisplayName(amount),
            WorkspaceId: Workspace,
            Name: TheoryDisplayName(amount),
            QualifiedName: TheoryDisplayName(amount),
            Selector: $"-id row-{ordinal}",
            Framework: "xunit",
            Role: ContinuousTestRole.TestCase,
            Source: "ct-provider:dotnet",
            Confidence: 1.0,
            Metadata: new Dictionary<string, object?>
            {
                ["class"] = TheoryClass,
                ["method"] = TheoryMethod,
            }));

    private static void PutRealTheoryCase(ContinuousTestStore store, string card, int ordinal) =>
        store.PutTestCase(new ContinuousTestCase(
            Id: "xunit:" + RealDisplayName(card),
            WorkspaceId: Workspace,
            Name: RealDisplayName(card),
            QualifiedName: RealDisplayName(card),
            Selector: $"-id real-{ordinal}",
            Framework: "xunit",
            Role: ContinuousTestRole.TestCase,
            Source: "ct-provider:dotnet",
            Confidence: 1.0,
            Metadata: new Dictionary<string, object?>
            {
                ["class"] = RealTheoryClass,
                ["method"] = RealTheoryMethod,
            }));

    /// <summary>
    /// A theory whose data cannot be enumerated up front: ONE case for the whole method, so its display
    /// name carries no arguments and its collapsed key has exactly one claimant.
    /// </summary>
    private static ContinuousTestCase DelayEnumeratedTheoryCase() =>
        new(
            Id: "xunit:" + TheoryClass + "." + TheoryMethod,
            WorkspaceId: Workspace,
            Name: TheoryClass + "." + TheoryMethod,
            QualifiedName: TheoryClass + "." + TheoryMethod,
            Selector: "-method " + TheoryMethod,
            Framework: "xunit",
            Role: ContinuousTestRole.TestCase,
            Source: "ct-provider:dotnet",
            Confidence: 1.0,
            Metadata: new Dictionary<string, object?>
            {
                ["class"] = TheoryClass,
                ["method"] = TheoryMethod,
            });

    /// <summary>One of two cases that claim the same provider selector, from two different providers.</summary>
    private static ContinuousTestCase ClaimantCase(string id, string source) =>
        new(
            Id: id,
            WorkspaceId: Workspace,
            Name: "test_charge_card",
            QualifiedName: "tests.test_billing.test_charge_card",
            Selector: "tests/test_billing::test_charge_card",
            Framework: "pytest",
            Role: ContinuousTestRole.TestCase,
            Source: source,
            Confidence: 1.0);

    private static string WriteTheoryJunitArtifact(string root) =>
        WriteArtifact(
            root,
            Path.Combine("artifacts", "theory.junit.xml"),
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <testsuite name="xunit" tests="3" failures="1" time="0.036">
              <testcase classname="Payments.Tests.ChargeTests" name="Charges_the_card(amount: 5)" time="0.011" />
              <testcase classname="Payments.Tests.ChargeTests" name="Charges_the_card(amount: 0)" time="0.012">
                <failure message="assert False">AssertionError: amount 0 was charged</failure>
              </testcase>
              <testcase classname="Payments.Tests.ChargeTests" name="Charges_the_card(amount: 7)" time="0.013" />
            </testsuite>
            """);

    private static string OneTheoryRowArtifact(string amount) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <testsuite name="xunit" tests="1" failures="0" time="0.011">
          <testcase classname="{TheoryClass}" name="{TheoryMethod}(amount: {amount})" time="0.011" />
        </testsuite>
        """;

    /// <summary>
    /// A real xUnit v3 jUnit artifact, captured verbatim from
    /// <c>Miller.Tests.exe -preEnumerateTheories -method &lt;theory&gt; -jUnit &lt;path&gt;</c> for a
    /// three-row theory with one failing row. Only the whitespace between elements was reflowed and the
    /// failure's stack trace was cut to its first frame; every attribute is the runner's own.
    ///
    /// <para>It shows the row shape this mapping has to match: <c>classname</c> is the class, and
    /// <c>name</c> is the FULL display name with the arguments, whose quotes the reporter
    /// backslash-escapes.</para>
    /// </summary>
    private const string RealXunitTheoryArtifact =
        """
        <testsuites name="Test results" time="0.09" tests="3" failures="1" errors="0" disabled="0">
          <testsuite name="Test collection for Miller.Tests.Testing.Analysis.JunitTestArtifactImporterTests (id: 5c8da1b5b62f04fa166aec5d5f89962b0f7dd312844221183ae447929cb27e75)" time="0.020" tests="3" failures="1" skipped="0">
            <testcase name="Miller.Tests.Testing.Analysis.JunitTestArtifactImporterTests.Temp_capture_probe_theory(card: \&quot;maestro\&quot;)" classname="Miller.Tests.Testing.Analysis.JunitTestArtifactImporterTests" time="0.0162904" />
            <testcase name="Miller.Tests.Testing.Analysis.JunitTestArtifactImporterTests.Temp_capture_probe_theory(card: \&quot;amex\&quot;)" classname="Miller.Tests.Testing.Analysis.JunitTestArtifactImporterTests" time="0.0030258"><failure type="Xunit.Sdk.NotEqualException" message="Assert.NotEqual() Failure: Strings are equal&#xD;&#xA;Expected: Not \&quot;amex\&quot;&#xD;&#xA;Actual:       \&quot;amex\&quot;">   at Miller.Tests.Testing.Analysis.JunitTestArtifactImporterTests.Temp_capture_probe_theory(String card) in C:\source\miller\tests\Miller.Tests\Testing\Analysis\JunitTestArtifactImporterTests.cs:line 231</failure></testcase>
            <testcase name="Miller.Tests.Testing.Analysis.JunitTestArtifactImporterTests.Temp_capture_probe_theory(card: \&quot;visa\&quot;)" classname="Miller.Tests.Testing.Analysis.JunitTestArtifactImporterTests" time="0.0004527" />
          </testsuite>
        </testsuites>
        """;

    private static string WriteArtifact(string root, string relativePath, string content)
    {
        var artifact = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(artifact)!);
        File.WriteAllText(artifact, content);
        return artifact;
    }
}
