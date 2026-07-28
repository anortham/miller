using System.Globalization;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Miller.Indexing.Semantic;
using Xunit;

namespace Miller.Tests.Support;

/// <summary>
/// The fault a fake sidecar injects. Each one maps to a row of the protocol contract's failure taxonomy that
/// Miller's session must survive.
/// </summary>
public enum FakeSidecarFault
{
    None,

    /// <summary>Reads the request and never answers, so the session's per-request budget is what ends the wait.</summary>
    StallForever,

    /// <summary>Writes a non-JSON line on stdout before the response — the desync the contract calls fatal.</summary>
    GarbageOnStdout,

    /// <summary>Exits mid-request without answering, so stdout closes under an outstanding call.</summary>
    CrashMidBatch,

    /// <summary>Answers with a well-formed error envelope, which must NOT restart the child.</summary>
    ErrorEnvelope,

    /// <summary>Reports <c>ready: false</c> with <c>degraded_reason: model_not_prepared</c>.</summary>
    ModelNotPrepared,

    /// <summary>Substitutes a zero vector for the poison text, per the contract's per-item isolation.</summary>
    PoisonItem,

    /// <summary>Echoes a wrong <c>request_id</c>, the stream desync the consumer must never accept.</summary>
    RequestIdDesync,
}

/// <summary>
/// A deterministic in-repo sidecar speaking <c>julie.embedding.sidecar</c> v1, with no model and no download.
/// Vectors are hash-derived and unit-norm, so the same text yields the same vector on every platform and a
/// round-trip can be asserted exactly.
/// </summary>
/// <remarks>
/// Two shapes share one implementation. <see cref="Serve"/> is the protocol loop itself, driven in-process
/// over a pair of pipes for the fast suite. The Scale suite runs the SAME loop as a real child process: the
/// module initializer below hijacks this test assembly's own executable when
/// <see cref="ModeVariable"/> is set, so a real spawn needs no extra project and no script interpreter.
/// The launch signal is deliberately NOT the julie-extract one, so the Scale-trait convention guard — which
/// keys on <c>RequireJulieServer</c>/<c>LocateJulieServer</c>/<c>RunJulie</c> — is untouched by this file.
/// </remarks>
public static class FakeSemanticSidecar
{
    /// <summary>Set on the child to make the test executable serve the protocol instead of running tests.</summary>
    public const string ModeVariable = "MILLER_FAKE_SEMANTIC_SIDECAR";

    public const string FaultVariable = "MILLER_FAKE_SEMANTIC_SIDECAR_FAULT";

    /// <summary>Comma-separated batch indices that fail to encode and receive a zero vector.</summary>
    public const string PoisonIndicesVariable = "MILLER_FAKE_SEMANTIC_SIDECAR_POISON";

    private const string Schema = "julie.embedding.sidecar";

    private const int Version = 1;

    /// <summary>
    /// Runs the fake as this process's whole job when the child marker is set. A module initializer is the one
    /// hook that reliably precedes the test runner's entry point, which is what lets the test binary double as
    /// the sidecar executable without a second project.
    /// </summary>
    [ModuleInitializer]
    internal static void HijackProcessWhenLaunchedAsSidecar()
    {
        if (Environment.GetEnvironmentVariable(ModeVariable) != "1")
            return;

        var fault = Enum.TryParse(Environment.GetEnvironmentVariable(FaultVariable), out FakeSidecarFault parsed)
            ? parsed
            : FakeSidecarFault.None;

        // fd-level stdout purity: the loop below is the only writer, and diagnostics have nowhere else to go.
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        Serve(stdin, stdout, fault, ParsePoisonIndices());
        Environment.Exit(0);
    }

    /// <summary>
    /// Locates this test assembly's own executable, or SKIPS the calling test when it is absent (an IDE or
    /// coverage host that runs the .dll without an apphost). Skipping, never failing, is the Scale-suite rule.
    /// </summary>
    public static string RequireSidecarExecutable() => RequireSidecarExecutable(LocateSidecarExecutable());

    /// <summary>The pure guard behind <see cref="RequireSidecarExecutable"/>, so the skip-never-fail behavior is
    /// itself testable without deleting the running host's own executable.</summary>
    internal static string RequireSidecarExecutable(string? located)
    {
        Assert.SkipWhen(located is null,
            "The Miller.Tests apphost was not found next to the test assembly, so the fake sidecar cannot be " +
            "spawned as a child process. Build the test project to enable this Scale test.");
        return located!;
    }

    /// <summary>The apphost path, or <c>null</c> when this build produced no native host.</summary>
    public static string? LocateSidecarExecutable()
    {
        string name = OperatingSystem.IsWindows() ? "Miller.Tests.exe" : "Miller.Tests";
        string candidate = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>A launcher that spawns the fake as a real child process with the requested fault injected.</summary>
    public static ISemanticSidecarConnectionFactory ProcessLauncher(
        string executable,
        FakeSidecarFault fault = FakeSidecarFault.None,
        IReadOnlyList<int>? poisonIndices = null)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ModeVariable] = "1",
            [FaultVariable] = fault.ToString(),
        };
        if (poisonIndices is { Count: > 0 })
            environment[PoisonIndicesVariable] = string.Join(',', poisonIndices);

        return new ProcessSemanticSidecarLauncher(executable, arguments: null, environment);
    }

    /// <summary>A launcher that runs the same protocol loop in-process over pipes — no subprocess, fast suite safe.</summary>
    public static ISemanticSidecarConnectionFactory InProcessLauncher(
        FakeSidecarFault fault = FakeSidecarFault.None,
        IReadOnlyList<int>? poisonIndices = null,
        SemanticEncoderPin? encoder = null) =>
        new PipeLauncher(fault, poisonIndices ?? [], encoder ?? MillerSemanticContract.DefaultEncoder);

    /// <summary>
    /// A launcher whose faults change per launch, so a test can prove recovery: launch <c>n</c> gets
    /// <c>faults[n]</c>, and the tail value repeats once the sequence is exhausted.
    /// </summary>
    public static ISemanticSidecarConnectionFactory SequencedLauncher(params FakeSidecarFault[] faults) =>
        new PipeLauncher(faults);

    /// <summary>The vector this fake produces for a text in a role. The session's round-trip asserts against it.</summary>
    public static float[] ExpectedVector(string role, string text, int dims)
    {
        var values = new double[dims];
        double sumOfSquares = 0;
        int produced = 0;
        int counter = 0;

        byte[] seed = Encoding.UTF8.GetBytes($"{role}\0{text}");
        while (produced < dims)
        {
            byte[] block = SHA256.HashData([.. seed, .. BitConverter.GetBytes(counter++)]);
            for (int i = 0; i + 4 <= block.Length && produced < dims; i += 4)
            {
                uint word = BitConverter.ToUInt32(block, i);
                double component = (word / (double)uint.MaxValue) * 2.0 - 1.0;
                values[produced++] = component;
                sumOfSquares += component * component;
            }
        }

        // A degenerate all-zero draw would fail the session's norm bar; the constant fallback keeps the fake
        // total without weakening the assertion.
        double norm = Math.Sqrt(sumOfSquares);
        if (norm <= 0)
            return Enumerable.Repeat(1f / MathF.Sqrt(dims), dims).ToArray();

        var unit = new float[dims];
        for (int i = 0; i < dims; i++)
            unit[i] = (float)(values[i] / norm);
        return unit;
    }

    /// <summary>
    /// The protocol loop: newline-delimited JSON in, one response line out, exactly one of result/error, and a
    /// process that survives every application error.
    /// </summary>
    public static void Serve(
        TextReader input,
        TextWriter output,
        FakeSidecarFault fault,
        IReadOnlyList<int> poisonIndices,
        CancellationToken cancellationToken = default,
        SemanticEncoderPin? encoder = null)
    {
        SemanticEncoderPin pin = encoder ?? MillerSemanticContract.DefaultEncoder;

        while (input.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            string requestId = string.Empty;
            string method;
            JsonDocument document;

            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                Write(output, ErrorEnvelope("", "invalid_json", "line was not valid JSON"));
                continue;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    Write(output, ErrorEnvelope("", "invalid_request", "request was not an object"));
                    continue;
                }

                requestId = ReadRequestId(root);

                if (root.TryGetProperty("schema", out JsonElement schema)
                    && (schema.ValueKind != JsonValueKind.String || schema.GetString() != Schema))
                {
                    Write(output, ErrorEnvelope(requestId, "invalid_request", "schema mismatch"));
                    continue;
                }

                if (root.TryGetProperty("version", out JsonElement version)
                    && (version.ValueKind != JsonValueKind.Number || version.GetInt32() != Version))
                {
                    Write(output, ErrorEnvelope(requestId, "invalid_request", "version mismatch"));
                    continue;
                }

                if (!root.TryGetProperty("method", out JsonElement methodElement)
                    || methodElement.ValueKind != JsonValueKind.String)
                {
                    Write(output, ErrorEnvelope(requestId, "invalid_request", "method must be a string"));
                    continue;
                }

                method = methodElement.GetString()!;
                JsonElement parameters = root.TryGetProperty("params", out JsonElement rawParams)
                    ? rawParams
                    : default;

                if (method != "health")
                {
                    if (fault == FakeSidecarFault.StallForever)
                    {
                        // A cancellable wait rather than an unbounded block: the fast suite must not strand a
                        // thread-pool thread once the session has already timed out and moved on.
                        cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                        return;
                    }

                    if (fault == FakeSidecarFault.CrashMidBatch)
                        return;

                    if (fault == FakeSidecarFault.GarbageOnStdout)
                    {
                        Write(output, "this is not json");
                        continue;
                    }
                }

                string response = method switch
                {
                    "health" => HealthResponse(requestId, pin, fault),
                    "embed_query" => EmbedQueryResponse(requestId, pin, parameters, fault),
                    "embed_batch" => EmbedBatchResponse(requestId, pin, parameters, fault, poisonIndices),
                    "shutdown" => ResultEnvelope(requestId, "{\"stopping\":true}"),
                    _ => ErrorEnvelope(requestId, "unknown_method", $"unknown method '{method}'"),
                };

                if (fault == FakeSidecarFault.RequestIdDesync && method != "health")
                    response = response.Replace($"\"request_id\":\"{requestId}\"", "\"request_id\":\"desync\"",
                        StringComparison.Ordinal);

                Write(output, response);

                if (method == "shutdown")
                    return;
            }
        }
    }

    private static string HealthResponse(string requestId, SemanticEncoderPin pin, FakeSidecarFault fault)
    {
        if (fault == FakeSidecarFault.ModelNotPrepared)
            return ResultEnvelope(requestId, "{\"ready\":false,\"degraded_reason\":\"model_not_prepared\"}");

        return ResultEnvelope(requestId, Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ready", true);
            writer.WriteNumber("dims", pin.Dims);
            writer.WriteString("model_id", pin.ModelId);
            writer.WriteString("model_sha256", pin.ModelSha256);
            writer.WriteString("model_revision", pin.ModelRevision);
            writer.WriteString("pooling", pin.Pooling);
            writer.WriteString("normalization", "l2");
            writer.WriteString("runtime", "fake");
            writer.WriteString("device", "cpu");
            writer.WriteString("resolved_backend", "cpu");
            writer.WriteBoolean("accelerated", false);
            writer.WriteNull("degraded_reason");
            writer.WriteStartObject("capabilities");
            foreach (string backend in (string[])["cpu", "cuda", "directml", "mps", "metal", "vulkan"])
            {
                writer.WriteStartObject(backend);
                writer.WriteBoolean("available", backend == "cpu");
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteStartObject("load_policy");
            writer.WriteString("requested_device_backend", "cpu");
            writer.WriteString("resolved_device_backend", "cpu");
            writer.WriteBoolean("accelerated", false);
            writer.WriteNull("degraded_reason");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }));
    }

    private static string EmbedQueryResponse(
        string requestId,
        SemanticEncoderPin pin,
        JsonElement parameters,
        FakeSidecarFault fault)
    {
        if (fault == FakeSidecarFault.ErrorEnvelope)
            return ErrorEnvelope(requestId, "internal_error", "RuntimeError: injected embed failure");

        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("text", out JsonElement text)
            || text.ValueKind != JsonValueKind.String)
        {
            return ErrorEnvelope(requestId, "invalid_request", "params.text must be a string");
        }

        return ResultEnvelope(requestId, Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("dims", pin.Dims);
            WriteVector(writer, "vector", ExpectedVector("query", Sanitize(text.GetString()), pin.Dims));
            writer.WriteEndObject();
        }));
    }

    private static string EmbedBatchResponse(
        string requestId,
        SemanticEncoderPin pin,
        JsonElement parameters,
        FakeSidecarFault fault,
        IReadOnlyList<int> poisonIndices)
    {
        if (fault == FakeSidecarFault.ErrorEnvelope)
            return ErrorEnvelope(requestId, "internal_error", "RuntimeError: injected embed failure");

        if (parameters.ValueKind != JsonValueKind.Object
            || !parameters.TryGetProperty("texts", out JsonElement texts)
            || texts.ValueKind != JsonValueKind.Array)
        {
            return ErrorEnvelope(requestId, "invalid_request", "params.texts must be an array");
        }

        var inputs = new List<string>();
        foreach (JsonElement item in texts.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return ErrorEnvelope(requestId, "invalid_request", "params.texts must contain only strings");
            inputs.Add(Sanitize(item.GetString()));
        }

        bool poisoning = fault == FakeSidecarFault.PoisonItem;

        return ResultEnvelope(requestId, Json(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("dims", pin.Dims);
            writer.WriteStartArray("vectors");
            for (int i = 0; i < inputs.Count; i++)
            {
                float[] vector = poisoning && poisonIndices.Contains(i)
                    ? new float[pin.Dims]
                    : ExpectedVector("document", inputs[i], pin.Dims);
                WriteVectorValue(writer, vector);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }));
    }

    // Reference sanitation: a non-string, empty, or whitespace-only input embeds as "[empty]", never an error.
    private static string Sanitize(string? text)
    {
        string value = (text ?? string.Empty).Replace("\0", string.Empty, StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(value) ? "[empty]" : value;
    }

    private static IReadOnlyList<int> ParsePoisonIndices()
    {
        string? raw = Environment.GetEnvironmentVariable(PoisonIndicesVariable);
        if (string.IsNullOrWhiteSpace(raw))
            return [];

        return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => int.TryParse(part, CultureInfo.InvariantCulture, out int index) ? index : -1)
            .Where(index => index >= 0)];
    }

    private static string ReadRequestId(JsonElement root)
    {
        if (root.TryGetProperty("request_id", out JsonElement explicitId) && explicitId.ValueKind == JsonValueKind.String)
            return explicitId.GetString() ?? string.Empty;
        if (root.TryGetProperty("id", out JsonElement alias) && alias.ValueKind == JsonValueKind.String)
            return alias.GetString() ?? string.Empty;
        return string.Empty;
    }

    private static string ResultEnvelope(string requestId, string resultJson) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("schema", Schema);
        writer.WriteNumber("version", Version);
        writer.WriteString("request_id", requestId);
        writer.WritePropertyName("result");
        using (JsonDocument result = JsonDocument.Parse(resultJson))
            result.RootElement.WriteTo(writer);
        writer.WriteEndObject();
    });

    private static string ErrorEnvelope(string requestId, string code, string message) => Json(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("schema", Schema);
        writer.WriteNumber("version", Version);
        writer.WriteString("request_id", requestId);
        writer.WriteStartObject("error");
        writer.WriteString("code", code);
        writer.WriteString("message", message);
        writer.WriteEndObject();
        writer.WriteEndObject();
    });

    private static void WriteVector(Utf8JsonWriter writer, string property, float[] vector)
    {
        writer.WritePropertyName(property);
        WriteVectorValue(writer, vector);
    }

    private static void WriteVectorValue(Utf8JsonWriter writer, float[] vector)
    {
        writer.WriteStartArray();
        foreach (float component in vector)
            writer.WriteNumberValue(component);
        writer.WriteEndArray();
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Write(TextWriter output, string line)
    {
        output.Write(line);
        output.Write('\n');
        output.Flush();
    }

    /// <summary>Runs <see cref="Serve"/> over an in-memory pipe pair, one instance per launch.</summary>
    private sealed class PipeLauncher : ISemanticSidecarConnectionFactory
    {
        private readonly FakeSidecarFault[] _faults;
        private readonly IReadOnlyList<int> _poisonIndices;
        private readonly SemanticEncoderPin _encoder;
        private int _launches;

        public PipeLauncher(
            FakeSidecarFault fault,
            IReadOnlyList<int> poisonIndices,
            SemanticEncoderPin? encoder = null)
        {
            _faults = [fault];
            _poisonIndices = poisonIndices;
            _encoder = encoder ?? MillerSemanticContract.DefaultEncoder;
        }

        public PipeLauncher(FakeSidecarFault[] faults)
        {
            _faults = faults.Length > 0 ? faults : [FakeSidecarFault.None];
            _poisonIndices = [];
            _encoder = MillerSemanticContract.DefaultEncoder;
        }

        public ValueTask<ISemanticSidecarConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FakeSidecarFault fault = _faults[Math.Min(_launches++, _faults.Length - 1)];
            return ValueTask.FromResult<ISemanticSidecarConnection>(
                new PipeConnection(fault, _poisonIndices, _encoder));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PipeConnection : ISemanticSidecarConnection
    {
        private readonly AnonymousPipeServerStreamPair _toChild = new();
        private readonly AnonymousPipeServerStreamPair _fromChild = new();
        private readonly CancellationTokenSource _stopping = new();
        private readonly Thread _loop;

        public PipeConnection(
            FakeSidecarFault fault,
            IReadOnlyList<int> poisonIndices,
            SemanticEncoderPin encoder)
        {
            Input = new StreamWriter(_toChild.Writer, new UTF8Encoding(false)) { AutoFlush = false };
            Output = new StreamReader(_fromChild.Reader, new UTF8Encoding(false));

            var childIn = new StreamReader(_toChild.Reader, new UTF8Encoding(false));
            var childOut = new StreamWriter(_fromChild.Writer, new UTF8Encoding(false)) { AutoFlush = false };

            // A dedicated thread, not the pool: the loop blocks in ReadLine for its whole life, and a fast suite
            // running these in parallel would otherwise starve the pool the session's own reads depend on.
            _loop = new Thread(() =>
            {
                try
                {
                    Serve(childIn, childOut, fault, poisonIndices, _stopping.Token, encoder);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // A killed or disposed channel is the normal end of this loop.
                }
                finally
                {
                    IsClosed = true;
                    Quietly(childOut.Dispose);
                    Quietly(childIn.Dispose);
                    // Closing the write end is what gives the reader EOF, which is how a crashed child looks.
                    Quietly(_fromChild.CloseWriteEnd);
                }
            })
            {
                IsBackground = true,
                Name = "fake-semantic-sidecar",
            };
            _loop.Start();
        }

        public TextWriter Input { get; }

        public TextReader Output { get; }

        public bool IsClosed { get; private set; }

        public void Abort()
        {
            _stopping.Cancel();
            // Closing the child's stdin write end is what ends a blocking ReadLine; closing its stdout write
            // end is what gives the session EOF if the loop is parked elsewhere.
            Quietly(_toChild.CloseWriteEnd);
            Quietly(_fromChild.CloseWriteEnd);
        }

        public ValueTask DisposeAsync()
        {
            Abort();
            _loop.Join(TimeSpan.FromSeconds(2));

            Quietly(Input.Dispose);
            Quietly(Output.Dispose);
            Quietly(_toChild.Dispose);
            Quietly(_fromChild.Dispose);
            _stopping.Dispose();
            return ValueTask.CompletedTask;
        }

        private static void Quietly(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Tearing down a half-closed pipe pair; the state we want is already reached.
            }
        }
    }

    /// <summary>A one-directional in-memory pipe whose read end sees EOF when the write end closes — the
    /// behavior a crashed child's stdout must reproduce.</summary>
    private sealed class AnonymousPipeServerStreamPair : IDisposable
    {
        private readonly AnonymousPipeServerStream _server = new(PipeDirection.Out, HandleInheritability.None);
        private readonly AnonymousPipeClientStream _client;
        private bool _writeEndClosed;

        public AnonymousPipeServerStreamPair() =>
            _client = new AnonymousPipeClientStream(PipeDirection.In, _server.ClientSafePipeHandle);

        public Stream Writer => _server;

        public Stream Reader => _client;

        public void CloseWriteEnd()
        {
            if (_writeEndClosed)
                return;
            _writeEndClosed = true;
            _server.Dispose();
        }

        public void Dispose()
        {
            CloseWriteEnd();
            _client.Dispose();
        }
    }
}
