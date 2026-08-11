using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Miller.Indexing.Semantic;

/// <summary>The lifecycle states a <see cref="SemanticEmbeddingSession"/> reports to status/health surfaces.</summary>
public enum SemanticSessionState
{
    /// <summary>No connection has been opened yet — the session is start-on-demand.</summary>
    NotStarted,

    /// <summary>A connection is live and its handshake was accepted.</summary>
    Ready,

    /// <summary>The broker is live, but the selected model has not been prepared yet.</summary>
    ModelNotPrepared,

    /// <summary>The last call failed at the transport level; the next call reconnects after a backoff.</summary>
    Restarting,

    /// <summary>Consecutive transport failures reached the threshold. Permanently degraded, with a reason.</summary>
    CircuitOpen,

    /// <summary>Disposed. Every call returns a stated failure rather than relaunching.</summary>
    Stopped,
}

/// <summary>
/// The encoder identity captured from the sidecar's <c>health</c> handshake, resolved against the pins in
/// <see cref="MillerSemanticContract"/>. <see cref="EncoderFingerprint"/> is the value a generation is stamped
/// with, so a store whose fingerprint differs was produced by a different encoder.
/// </summary>
public sealed record SemanticEncoderHandshake(
    SemanticEncoderPin Pin,
    string EncoderFingerprint,
    int Dims,
    bool Accelerated,
    string ResolvedBackend,
    string? DegradedReason,
    bool AcceleratorLeaseHeld = false);

/// <summary>
/// The outcome of one embed call. A failure carries a stated <see cref="FailureReason"/> rather than throwing,
/// because every caller's correct response is to degrade to lexical and say why.
/// </summary>
public sealed record SemanticEmbedOutcome(
    bool Succeeded,
    IReadOnlyList<float[]> Vectors,
    IReadOnlyList<int> FlaggedIndices,
    string? FailureReason,
    bool TimedOut = false)
{
    public static SemanticEmbedOutcome Ok(IReadOnlyList<float[]> vectors, IReadOnlyList<int> flagged) =>
        new(true, vectors, flagged, null);

    public static SemanticEmbedOutcome Fail(string reason, bool timedOut = false) =>
        new(false, [], [], reason, timedOut);
}

/// <summary>One transport-neutral protocol-v1 connection.</summary>
public interface ISemanticSidecarConnection : IAsyncDisposable
{
    TextWriter Input { get; }

    TextReader Output { get; }

    bool IsClosed { get; }

    void Abort();
}

/// <summary>Opens sidecar connections without exposing whether the transport is stdio or shared IPC.</summary>
public interface ISemanticSidecarConnectionFactory : IAsyncDisposable
{
    ValueTask<ISemanticSidecarConnection> ConnectAsync(CancellationToken cancellationToken);
}

internal interface ISemanticBrokerSnapshotRecorder
{
    void RecordHandshake(SemanticEncoderHandshake handshake);
}

/// <summary>Tunable budgets and the injectable delay that keeps backoff testable without real sleeps.</summary>
public sealed record SemanticSessionOptions
{
    /// <summary>Per-request response budget (protocol v1 § Timeouts).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>First <c>health</c> probe budget, which must cover a cold model load.</summary>
    public TimeSpan InitTimeout { get; init; } = TimeSpan.FromSeconds(120);

    /// <summary>Hard budget for an explicit <c>shutdown</c> response before closing the connection.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Consecutive transport failures that open the circuit permanently.</summary>
    public int FatalThreshold { get; init; } = 3;

    public TimeSpan RestartBackoff { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan RestartBackoffCap { get; init; } = TimeSpan.FromSeconds(10);

    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
}

/// <summary>
/// Miller's client half of the <c>julie.embedding.sidecar</c> v1 relationship: start-on-demand connection, the
/// <c>health</c> handshake that pins encoder identity, one-in-flight request/response over newline-delimited
/// JSON, reconnect-with-backoff after a transport fault, and a circuit that opens after
/// <see cref="SemanticSessionOptions.FatalThreshold"/> consecutive faults.
/// </summary>
/// <remarks>
/// The contract's central distinction is preserved verbatim: a well-formed <c>error</c> envelope means the
/// protocol loop survived, so it fails the request WITHOUT reconnecting or counting toward the circuit.
/// Everything the contract calls connection-fatal — unwritable stdin, closed stdout, response timeout,
/// undecodable line, envelope/request-id/dims/count violation — aborts that connection instead.
/// Failures are returned as outcomes rather than thrown: the caller's correct response is always to degrade to
/// lexical with a stated reason, never to fault the converge pass.
/// </remarks>
public sealed class SemanticEmbeddingSession : IAsyncDisposable
{
    public const string Schema = "julie.embedding.sidecar";

    public const int ProtocolVersion = 1;

    /// <summary>Protocol v1 § Conformance group C: every emitted vector is L2-normalized to this tolerance.</summary>
    public const double NormTolerance = 1e-3;

    private const int MaxAttemptsPerCall = 2;

    private readonly ISemanticSidecarConnectionFactory _connectionFactory;
    private readonly bool _ownsConnectionFactory;
    private readonly SemanticSessionOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ISemanticSidecarConnection? _connection;
    private SidecarLineReader? _reader;
    private readonly SemanticEncoderPin? _expectedEncoder;
    private int _consecutiveFatals;
    private long _requestSequence;
    private bool _runtimeHealthRefreshSuppressed;
    private bool _disposed;

    public SemanticEmbeddingSession(
        ISemanticSidecarConnectionFactory connectionFactory,
        SemanticSessionOptions? options = null,
        SemanticEncoderPin? expectedEncoder = null,
        bool ownsConnectionFactory = false)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        _connectionFactory = connectionFactory;
        _ownsConnectionFactory = ownsConnectionFactory;
        _options = options ?? new SemanticSessionOptions();
        _expectedEncoder = expectedEncoder;
    }

    public SemanticSessionState State { get; private set; } = SemanticSessionState.NotStarted;

    /// <summary>Why the semantic arm cannot serve right now, or <c>null</c> when it can. Never blank on a
    /// non-<see cref="SemanticSessionState.Ready"/> state that a caller can observe.</summary>
    public string? UnavailableReason { get; private set; }

    /// <summary>Encoder identity from the accepted handshake, or <c>null</c> before the first successful start.</summary>
    public SemanticEncoderHandshake? Handshake { get; private set; }

    /// <summary>How many times a fault forced a reconnect. Surfaced as a status fact, not wired to telemetry here.</summary>
    public int RestartCount { get; private set; }

    /// <summary>
    /// <see cref="MatchEncoder(SemanticSidecarHealth, out string?)"/> plus the stricter requirement that the
    /// sidecar loaded the encoder Miller actually selected. Without this, a sidecar serving a different — but
    /// still pinned — encoder passes the handshake and the mismatch only surfaces as a dimension error at
    /// vector-commit time, permanently wedging the build (the bge-under-qwen3-sidecar failure, 2026-07-21).
    /// </summary>
    public static SemanticEncoderHandshake? MatchEncoder(
        SemanticSidecarHealth health,
        SemanticEncoderPin expected,
        out string? refusalReason)
    {
        ArgumentNullException.ThrowIfNull(health);
        ArgumentNullException.ThrowIfNull(expected);

        if (!health.Ready)
        {
            refusalReason = "sidecar reported ready=false" +
                (string.IsNullOrEmpty(health.DegradedReason) ? "" : $" ({health.DegradedReason})");
            return null;
        }

        if (!string.Equals(health.ModelId, expected.ModelId, StringComparison.Ordinal))
        {
            refusalReason =
                $"sidecar loaded model_id '{health.ModelId}' but Miller selected '{expected.ModelId}' — " +
                "the sidecar must be launched with `serve --model` matching the active encoder";
            return null;
        }

        string? disagreement = FirstDisagreement(expected, health);
        if (disagreement is not null)
        {
            refusalReason = $"sidecar handshake disagrees with pin '{expected.ModelId}': {disagreement}";
            return null;
        }

        refusalReason = null;
        return new SemanticEncoderHandshake(
            expected,
            MillerSemanticContract.EncoderFingerprint(expected),
            health.Dims,
            health.Accelerated,
            health.ResolvedBackend,
            health.DegradedReason,
            health.AcceleratorLeaseHeld ?? health.Accelerated);
    }

    /// <summary>
    /// Resolves the sidecar's reported model identity against the pinned encoders and returns the fingerprint a
    /// generation written from this sidecar must carry. A disagreement on any embedding-affecting field is a
    /// stated refusal, never a coerced match: writing vectors under a fingerprint the sidecar did not produce
    /// would make the store's generation identity a lie.
    /// </summary>
    public static SemanticEncoderHandshake? MatchEncoder(SemanticSidecarHealth health, out string? refusalReason)
    {
        ArgumentNullException.ThrowIfNull(health);

        if (!health.Ready)
        {
            refusalReason = "sidecar reported ready=false" +
                (string.IsNullOrEmpty(health.DegradedReason) ? "" : $" ({health.DegradedReason})");
            return null;
        }

        SemanticEncoderPin? pin = MillerSemanticContract.FindEncoder(health.ModelId);
        if (pin is null)
        {
            refusalReason = $"sidecar loaded model_id '{health.ModelId}', which is not a pinned Miller encoder";
            return null;
        }

        string? disagreement = FirstDisagreement(pin, health);
        if (disagreement is not null)
        {
            refusalReason = $"sidecar handshake disagrees with pin '{pin.ModelId}': {disagreement}";
            return null;
        }

        refusalReason = null;
        return new SemanticEncoderHandshake(
            pin,
            MillerSemanticContract.EncoderFingerprint(pin),
            health.Dims,
            health.Accelerated,
            health.ResolvedBackend,
            health.DegradedReason,
            health.AcceleratorLeaseHeld ?? health.Accelerated);
    }

    public Task<SemanticEmbedOutcome> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        return CallAsync("embed_batch", texts, cancellationToken);
    }

    public Task<SemanticEmbedOutcome> EmbedQueryAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return CallAsync("embed_query", [text], cancellationToken);
    }

    /// <summary>Launches and handshakes if needed, so a caller can surface readiness before embedding anything.</summary>
    public async Task<SemanticEncoderHandshake?> EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return null;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!CanProbeReadiness())
                return null;

            return await StartIfNeededAsync(cancellationToken).ConfigureAwait(false) ? Handshake : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Explicit protocol stop. Disposal never sends <c>shutdown</c>; this method sends it only for this
    /// connection and closes the connection once <see cref="SemanticSessionOptions.ShutdownTimeout"/> elapses.
    /// </summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null)
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                budget.CancelAfter(_options.ShutdownTimeout);
                try
                {
                    using SidecarResponse _ = await ExchangeAsync(
                        "shutdown", "{}", _options.ShutdownTimeout, budget.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SidecarTransportException or OperationCanceledException
                    or IOException or ObjectDisposedException)
                {
                    // A sidecar that cannot answer its own shutdown is terminated below; that is the contract.
                }
            }

            await CloseConnectionAsync(abort: false).ConfigureAwait(false);
            State = SemanticSessionState.Stopped;
            UnavailableReason ??= "session stopped";
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseConnectionAsync(abort: false).ConfigureAwait(false);
            State = SemanticSessionState.Stopped;
            UnavailableReason ??= "session disposed";
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
            if (_ownsConnectionFactory)
                await _connectionFactory.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<SemanticEmbedOutcome> CallAsync(
        string method,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
    {
        if (_disposed)
            return SemanticEmbedOutcome.Fail(UnavailableReason ?? "semantic session is disposed");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TryEnterCall(out string? blockedReason))
                return SemanticEmbedOutcome.Fail(blockedReason!);

            // Carries the character of the most recent transport fault to the returned outcome, so the arm can
            // count an embed timeout distinctly from any other embed error without parsing the reason string.
            bool lastTimedOut = false;

            for (int attempt = 0; attempt < MaxAttemptsPerCall; attempt++)
            {
                if (attempt > 0 && !await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false))
                    return SemanticEmbedOutcome.Fail(UnavailableReason!, lastTimedOut);

                if (!await StartIfNeededAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (State is SemanticSessionState.CircuitOpen or SemanticSessionState.ModelNotPrepared)
                        return SemanticEmbedOutcome.Fail(UnavailableReason!, lastTimedOut);
                    continue;
                }

                string parameters = method == "embed_query"
                    ? BuildQueryParams(texts[0])
                    : BuildBatchParams(texts);

                SidecarResponse response;
                try
                {
                    response = await ExchangeAsync(method, parameters, _options.RequestTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (SidecarTransportException ex)
                {
                    lastTimedOut = ex.TimedOut;
                    if (await RecordFatalAsync(ex.Message).ConfigureAwait(false))
                        return SemanticEmbedOutcome.Fail(UnavailableReason!, lastTimedOut);
                    continue;
                }

                using (response)
                {
                    if (response.Error is not null)
                    {
                        // The loop answered, so the transport is healthy: fail the request, keep the child.
                        _consecutiveFatals = 0;
                        return SemanticEmbedOutcome.Fail(
                            $"sidecar error for method '{method}': [{response.Error.Code}] {response.Error.Message}");
                    }

                    try
                    {
                        SemanticEmbedOutcome outcome = ReadVectors(method, texts.Count, response.Result!.Value);
                        _consecutiveFatals = 0;
                        if (Handshake?.Accelerated == true && !_runtimeHealthRefreshSuppressed)
                            await RefreshRuntimeHealthAsync(cancellationToken).ConfigureAwait(false);
                        return outcome;
                    }
                    catch (SidecarTransportException ex)
                    {
                        lastTimedOut = ex.TimedOut;
                        if (await RecordFatalAsync(ex.Message).ConfigureAwait(false))
                            return SemanticEmbedOutcome.Fail(UnavailableReason!, lastTimedOut);
                    }
                }
            }

            return SemanticEmbedOutcome.Fail(UnavailableReason ?? "sidecar request failed after a restart", lastTimedOut);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryEnterCall(out string? blockedReason)
    {
        if (_disposed || State == SemanticSessionState.Stopped)
        {
            blockedReason = UnavailableReason ?? "semantic session is stopped";
            return false;
        }

        if (State is SemanticSessionState.CircuitOpen or SemanticSessionState.ModelNotPrepared)
        {
            blockedReason = UnavailableReason;
            return false;
        }

        blockedReason = null;
        return true;
    }

    private bool CanProbeReadiness() =>
        !_disposed && State is not SemanticSessionState.Stopped and not SemanticSessionState.CircuitOpen;

    private async Task RefreshRuntimeHealthAsync(CancellationToken cancellationToken)
    {
        try
        {
            using SidecarResponse response =
                await ExchangeAsync("health", "{}", _options.RequestTimeout, cancellationToken).ConfigureAwait(false);
            if (response.Error is not null)
                return;

            SemanticSidecarHealth health = SemanticSidecarHealth.Parse(response.Result!.Value);
            SemanticEncoderHandshake? handshake = _expectedEncoder is null
                ? MatchEncoder(health, out _)
                : MatchEncoder(health, _expectedEncoder, out _);
            if (handshake is null || handshake == Handshake)
                return;

            Handshake = handshake;
            if (_connectionFactory is ISemanticBrokerSnapshotRecorder recorder)
                recorder.RecordHandshake(handshake);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await QuarantineRuntimeHealthRefreshAsync("canceled").ConfigureAwait(false);
        }
        catch (SidecarTransportException ex)
        {
            await QuarantineRuntimeHealthRefreshAsync(ex.Message).ConfigureAwait(false);
        }
    }

    private async Task QuarantineRuntimeHealthRefreshAsync(string reason)
    {
        _runtimeHealthRefreshSuppressed = true;
        await CloseConnectionAsync(abort: true).ConfigureAwait(false);
        RestartCount++;
        State = SemanticSessionState.Restarting;
        UnavailableReason = $"sidecar runtime health refresh failed: {reason}";
    }

    private async Task<bool> StartIfNeededAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null && !_connection.IsClosed)
            return true;

        await CloseConnectionAsync(abort: false).ConfigureAwait(false);

        try
        {
            _connection = await _connectionFactory.ConnectAsync(cancellationToken).ConfigureAwait(false);
            _reader = new SidecarLineReader(_connection.Output);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await RecordFatalAsync($"could not connect to the semantic sidecar: {ex.Message}").ConfigureAwait(false);
            return false;
        }

        SidecarResponse response;
        try
        {
            response = await ExchangeAsync("health", "{}", _options.InitTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SidecarTransportException ex)
        {
            await RecordFatalAsync($"sidecar health probe failed: {ex.Message}").ConfigureAwait(false);
            return false;
        }

        SemanticSidecarHealth health;
        using (response)
        {
            if (response.Error is not null)
            {
                await RecordFatalAsync(
                    $"sidecar health probe returned [{response.Error.Code}] {response.Error.Message}")
                    .ConfigureAwait(false);
                return false;
            }

            try
            {
                health = SemanticSidecarHealth.Parse(response.Result!.Value);
            }
            catch (SidecarTransportException ex)
            {
                await RecordFatalAsync(ex.Message).ConfigureAwait(false);
                return false;
            }
        }

        SemanticEncoderHandshake? handshake = _expectedEncoder is null
            ? MatchEncoder(health, out string? refusal)
            : MatchEncoder(health, _expectedEncoder, out refusal);
        if (handshake is null)
        {
            await CloseConnectionAsync(abort: false).ConfigureAwait(false);
            Handshake = null;
            State = !health.Ready && string.Equals(
                health.DegradedReason,
                "model_not_prepared",
                StringComparison.Ordinal)
                ? SemanticSessionState.ModelNotPrepared
                : SemanticSessionState.CircuitOpen;
            UnavailableReason = refusal;
            return false;
        }

        Handshake = handshake;
        if (_connectionFactory is ISemanticBrokerSnapshotRecorder recorder)
            recorder.RecordHandshake(handshake);
        _runtimeHealthRefreshSuppressed = false;
        State = SemanticSessionState.Ready;
        UnavailableReason = null;
        // The fatal counter is NOT reset here: reconnecting is what a fault costs, so a connection that handshakes
        // and then faults again must still march toward the circuit. Only a completed request clears it.
        return true;
    }

    private async Task<bool> BackoffAsync(int attempt, CancellationToken cancellationToken)
    {
        if (State == SemanticSessionState.CircuitOpen)
            return false;

        TimeSpan wait = TimeSpan.FromTicks(Math.Min(
            _options.RestartBackoff.Ticks * (1L << (attempt - 1)),
            _options.RestartBackoffCap.Ticks));
        await _options.Delay(wait, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Records a connection-fatal condition. Returns true once the circuit has opened for good.</summary>
    private async Task<bool> RecordFatalAsync(string reason)
    {
        await CloseConnectionAsync(abort: true).ConfigureAwait(false);
        RestartCount++;
        _consecutiveFatals++;

        if (_consecutiveFatals >= _options.FatalThreshold)
        {
            State = SemanticSessionState.CircuitOpen;
            UnavailableReason =
                $"semantic sidecar disabled after {_consecutiveFatals} consecutive failures: {reason}";
            return true;
        }

        State = SemanticSessionState.Restarting;
        UnavailableReason = reason;
        return false;
    }

    private async ValueTask CloseConnectionAsync(bool abort)
    {
        // Transport before reader: closing the stream is what unblocks the pump's in-flight read —
        // Windows pipe reads can outlive cancellation, and joining the pump first pays its full 1s cap.
        ISemanticSidecarConnection? connection = _connection;
        _connection = null;
        if (connection is not null)
        {
            if (abort)
            {
                try
                {
                    connection.Abort();
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
                {
                }
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _reader?.Dispose();
        _reader = null;
    }

    private async Task<SidecarResponse> ExchangeAsync(
        string method,
        string parametersJson,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ISemanticSidecarConnection connection = _connection
            ?? throw new SidecarTransportException("the sidecar connection is not open");
        SidecarLineReader reader = _reader
            ?? throw new SidecarTransportException("the sidecar reader is not open");

        string requestId = (++_requestSequence).ToString(CultureInfo.InvariantCulture);
        string line = BuildRequest(requestId, method, parametersJson);

        try
        {
            await connection.Input.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            await connection.Input.WriteAsync("\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            await connection.Input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            throw new SidecarTransportException($"could not write the '{method}' request to sidecar stdin: {ex.Message}");
        }

        string? response = await reader.ReadLineAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            throw new SidecarTransportException(
                reader.EndedByTimeout
                    ? $"no response to '{method}' within {timeout.TotalMilliseconds:F0} ms"
                    : $"sidecar stdout closed while awaiting '{method}'",
                reader.EndedByTimeout);
        }

        return SidecarResponse.Parse(response, requestId);
    }

    private static string BuildRequest(string requestId, string method, string parametersJson)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteNumber("version", ProtocolVersion);
            writer.WriteString("request_id", requestId);
            writer.WriteString("method", method);
            writer.WritePropertyName("params");
            using (JsonDocument parameters = JsonDocument.Parse(parametersJson))
                parameters.RootElement.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string BuildBatchParams(IReadOnlyList<string> texts)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("texts");
            foreach (string text in texts)
                writer.WriteStringValue(text);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string BuildQueryParams(string text)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("text", text);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private SemanticEmbedOutcome ReadVectors(string method, int requestedCount, JsonElement result)
    {
        int expectedDims = Handshake?.Dims ?? 0;

        if (!result.TryGetProperty("dims", out JsonElement dimsElement)
            || dimsElement.ValueKind != JsonValueKind.Number
            || !dimsElement.TryGetInt32(out int dims))
        {
            throw new SidecarTransportException($"'{method}' result has no integer 'dims'");
        }

        if (dims != expectedDims)
            throw new SidecarTransportException($"'{method}' returned dims {dims}, but health declared {expectedDims}");

        var vectors = new List<float[]>();
        if (method == "embed_query")
        {
            vectors.Add(ReadVector(result, "vector", dims, 0));
        }
        else
        {
            if (!result.TryGetProperty("vectors", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array)
                throw new SidecarTransportException("'embed_batch' result has no 'vectors' array");

            if (rows.GetArrayLength() != requestedCount)
            {
                throw new SidecarTransportException(
                    $"'embed_batch' returned {rows.GetArrayLength()} vectors for {requestedCount} texts");
            }

            int index = 0;
            foreach (JsonElement row in rows.EnumerateArray())
                vectors.Add(ReadVectorElement(row, dims, index++));
        }

        return SemanticEmbedOutcome.Ok(vectors, FlaggedIndices(result, vectors));
    }

    private static float[] ReadVector(JsonElement result, string property, int dims, int index)
    {
        if (!result.TryGetProperty(property, out JsonElement vector))
            throw new SidecarTransportException($"result has no '{property}' array");
        return ReadVectorElement(vector, dims, index);
    }

    private static float[] ReadVectorElement(JsonElement element, int dims, int index)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new SidecarTransportException($"vector at index {index} is not an array");

        if (element.GetArrayLength() != dims)
        {
            throw new SidecarTransportException(
                $"vector at index {index} has length {element.GetArrayLength()}, expected {dims}");
        }

        var values = new float[dims];
        int position = 0;
        double sumOfSquares = 0;
        foreach (JsonElement component in element.EnumerateArray())
        {
            if (component.ValueKind != JsonValueKind.Number || !component.TryGetDouble(out double value))
                throw new SidecarTransportException($"vector at index {index} has a non-numeric component");

            values[position++] = (float)value;
            sumOfSquares += value * value;
        }

        // A zero vector is the contract's substitution for an item that failed to encode, so it is exempt from
        // the norm bar; anything else must arrive L2-normalized.
        double norm = Math.Sqrt(sumOfSquares);
        if (norm > NormTolerance && Math.Abs(norm - 1.0) > NormTolerance)
        {
            throw new SidecarTransportException(
                $"vector at index {index} has L2 norm {norm.ToString("F6", CultureInfo.InvariantCulture)}, " +
                $"outside {NormTolerance.ToString("G", CultureInfo.InvariantCulture)} of 1.0");
        }

        return values;
    }

    private static IReadOnlyList<int> FlaggedIndices(JsonElement result, IReadOnlyList<float[]> vectors)
    {
        var flagged = new SortedSet<int>();

        if (result.TryGetProperty("flagged_indices", out JsonElement declared)
            && declared.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in declared.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int index))
                    flagged.Add(index);
            }
        }

        for (int i = 0; i < vectors.Count; i++)
        {
            if (vectors[i].All(component => component == 0f))
                flagged.Add(i);
        }

        return [.. flagged];
    }

    private static string? FirstDisagreement(SemanticEncoderPin pin, SemanticSidecarHealth health)
    {
        if (health.Dims != pin.Dims)
            return $"dims {health.Dims} != pinned {pin.Dims}";

        if (Differs(health.ModelSha256, pin.ModelSha256))
            return $"model_sha256 '{health.ModelSha256}' != pinned '{pin.ModelSha256}'";

        if (Differs(health.ModelRevision, pin.ModelRevision))
            return $"model_revision '{health.ModelRevision}' != pinned '{pin.ModelRevision}'";

        if (Differs(health.Pooling, pin.Pooling))
            return $"pooling '{health.Pooling}' != pinned '{pin.Pooling}'";

        if (!string.IsNullOrEmpty(health.Normalization)
            && !string.Equals(health.Normalization, "l2", StringComparison.Ordinal))
        {
            return $"normalization '{health.Normalization}' != 'l2'";
        }

        return null;

        // An absent field is silence, not disagreement: the additive health keys may be omitted by a v1 peer.
        static bool Differs(string reported, string pinned) =>
            !string.IsNullOrEmpty(reported) && !string.Equals(reported, pinned, StringComparison.Ordinal);
    }
}

/// <summary>
/// The <c>health</c> result, reduced to the fields Miller reads. Every additive key is optional, per the
/// protocol's ignore-unknown rule, so an omitted value parses as empty rather than failing the handshake.
/// </summary>
public sealed record SemanticSidecarHealth(
    bool Ready,
    int Dims,
    string ModelId,
    string ModelSha256,
    string ModelRevision,
    string Pooling,
    string Normalization,
    string ResolvedBackend,
    bool Accelerated,
    string? DegradedReason,
    bool? AcceleratorLeaseHeld = null)
{
    internal static SemanticSidecarHealth Parse(JsonElement result)
    {
        if (!result.TryGetProperty("ready", out JsonElement readyElement)
            || readyElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new SidecarTransportException("health result has no boolean 'ready'");
        }

        bool ready = readyElement.GetBoolean();
        int dims = 0;
        if (result.TryGetProperty("dims", out JsonElement dimsElement)
            && dimsElement.ValueKind == JsonValueKind.Number)
        {
            dimsElement.TryGetInt32(out dims);
        }
        else if (ready)
        {
            throw new SidecarTransportException("health result declares ready=true with no 'dims'");
        }

        return new SemanticSidecarHealth(
            ready,
            dims,
            Text(result, "model_id"),
            Text(result, "model_sha256"),
            Text(result, "model_revision"),
            Text(result, "pooling"),
            Text(result, "normalization"),
            Text(result, "resolved_backend"),
            result.TryGetProperty("accelerated", out JsonElement accelerated)
                && accelerated.ValueKind == JsonValueKind.True,
            NullableText(result, "degraded_reason"),
            result.TryGetProperty("accelerator_lease_held", out JsonElement lease)
                && lease.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? lease.GetBoolean()
                    : null);

        static string Text(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        static string? NullableText(JsonElement element, string property) =>
            element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}

/// <summary>A connection-fatal transport condition. Internal on purpose: callers see stated outcomes, not throws.</summary>
internal sealed class SidecarTransportException : Exception
{
    public SidecarTransportException(string message, bool timedOut = false)
        : base(message)
    {
        TimedOut = timedOut;
    }

    /// <summary>True when this fault was the per-request budget elapsing, not any other transport failure —
    /// the distinction the canary's <c>embed_timeout</c> reason is counted from.</summary>
    public bool TimedOut { get; }
}

internal sealed record SidecarError(string Code, string Message);

internal sealed record SidecarResponse(JsonElement? Result, SidecarError? Error, JsonDocument? Owner) : IDisposable
{
    public void Dispose() => Owner?.Dispose();

    /// <summary>
    /// Validates the response envelope exactly as the contract's consumer does — schema, version, request-id
    /// echo, exactly-one-of result/error — and throws a transport fault on any violation. A line that is not
    /// decodable JSON never reaches a partial parse: it fails here, loudly.
    /// </summary>
    public static SidecarResponse Parse(string line, string expectedRequestId)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new SidecarTransportException($"sidecar stdout line was not decodable JSON: {ex.Message}");
        }

        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new SidecarTransportException("sidecar response was not a JSON object");
        }

        try
        {
            if (!root.TryGetProperty("schema", out JsonElement schema)
                || schema.ValueKind != JsonValueKind.String
                || schema.GetString() != SemanticEmbeddingSession.Schema)
            {
                throw new SidecarTransportException(
                    $"sidecar response schema is not '{SemanticEmbeddingSession.Schema}'");
            }

            if (!root.TryGetProperty("version", out JsonElement version)
                || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int versionValue)
                || versionValue != SemanticEmbeddingSession.ProtocolVersion)
            {
                throw new SidecarTransportException(
                    $"sidecar response version is not {SemanticEmbeddingSession.ProtocolVersion}");
            }

            string? echoed = root.TryGetProperty("request_id", out JsonElement requestId)
                && requestId.ValueKind == JsonValueKind.String
                    ? requestId.GetString()
                    : null;
            if (!string.Equals(echoed, expectedRequestId, StringComparison.Ordinal))
            {
                throw new SidecarTransportException(
                    $"sidecar response echoed request_id '{echoed}', expected '{expectedRequestId}' (stream desync)");
            }

            bool hasResult = root.TryGetProperty("result", out JsonElement result)
                && result.ValueKind != JsonValueKind.Null;
            bool hasError = root.TryGetProperty("error", out JsonElement error)
                && error.ValueKind != JsonValueKind.Null;

            if (hasResult == hasError)
                throw new SidecarTransportException("sidecar response carries neither or both of 'result'/'error'");

            if (hasError)
            {
                string code = error.TryGetProperty("code", out JsonElement codeElement)
                    && codeElement.ValueKind == JsonValueKind.String
                        ? codeElement.GetString() ?? string.Empty
                        : string.Empty;
                string message = error.TryGetProperty("message", out JsonElement messageElement)
                    && messageElement.ValueKind == JsonValueKind.String
                        ? messageElement.GetString() ?? string.Empty
                        : string.Empty;
                return new SidecarResponse(null, new SidecarError(code, message), document);
            }

            return new SidecarResponse(result, null, document);
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Pumps the child's stdout into a queue on a background task so a stalled sidecar costs a bounded wait rather
/// than a blocked caller — the contract's per-request timeout is only enforceable off the read path.
/// </summary>
internal sealed class SidecarLineReader : IDisposable
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _pump;

    public SidecarLineReader(TextReader stdout)
    {
        _pump = Task.Run(async () =>
        {
            try
            {
                while (await stdout.ReadLineAsync(_stopping.Token).ConfigureAwait(false) is { } line)
                    await _lines.Writer.WriteAsync(line, _stopping.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Cancellation and a closed pipe are both "no more lines"; the completion below says so.
            }
            finally
            {
                _lines.Writer.TryComplete();
            }
        });
    }

    /// <summary>True when the last <see cref="ReadLineAsync"/> returned null because the budget elapsed rather
    /// than because stdout closed — the two are different faults and get different reasons.</summary>
    public bool EndedByTimeout { get; private set; }

    public async Task<string?> ReadLineAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        EndedByTimeout = false;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(timeout);

        try
        {
            return await _lines.Reader.ReadAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            EndedByTimeout = true;
            return null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _stopping.Cancel();
        try
        {
            _pump.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The pump's own faults are already absorbed above; a wait fault here changes nothing.
        }

        _stopping.Dispose();
    }
}

/// <summary>Opens a protocol-v1 connection by launching the sidecar as a stdio child process.</summary>
public sealed class StdioSemanticSidecarConnectionFactory : ISemanticSidecarConnectionFactory
{
    private readonly string _executable;
    private readonly IReadOnlyList<string> _arguments;
    private readonly IReadOnlyDictionary<string, string> _environment;

    public StdioSemanticSidecarConnectionFactory(
        string executable,
        IReadOnlyList<string>? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        _executable = executable;
        _arguments = arguments ?? [];
        _environment = environment ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The serve-mode factory for <paramref name="pin"/>: the verb and model id are passed explicitly so the
    /// child can never silently serve a different encoder than the one Miller selected. The sidecar reads no
    /// environment for model selection — <c>serve --model</c> is the only channel.
    /// </summary>
    public static StdioSemanticSidecarConnectionFactory ForServe(string executable, SemanticEncoderPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return new StdioSemanticSidecarConnectionFactory(executable, ["serve", "--model", pin.ModelId]);
    }

    /// <summary>The argv passed to the sidecar, exposed so launch wiring is provable without spawning.</summary>
    public IReadOnlyList<string> Arguments => _arguments;

    public ValueTask<ISemanticSidecarConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(_executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in _arguments)
            start.ArgumentList.Add(argument);
        foreach ((string key, string value) in _environment)
            start.Environment[key] = value;

        Process process = Process.Start(start)
            ?? throw new InvalidOperationException($"could not start '{_executable}'");

        // stderr is free-form diagnostics the contract says the consumer discards; draining it asynchronously
        // is what keeps a chatty child from blocking on a full pipe buffer.
        process.ErrorDataReceived += static (_, _) => { };
        process.BeginErrorReadLine();

        return ValueTask.FromResult<ISemanticSidecarConnection>(new ProcessConnection(process));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class ProcessConnection : ISemanticSidecarConnection
    {
        private readonly Process _process;

        public ProcessConnection(Process process) => _process = process;

        public TextWriter Input => _process.StandardInput;

        public TextReader Output => _process.StandardOutput;

        public bool IsClosed => _process.HasExited;

        public void Abort()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                Abort();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException
                or OperationCanceledException)
            {
            }

            _process.Dispose();
        }
    }
}

/// <summary>Compatibility name for callers migrating from the original process-launch seam.</summary>
public sealed class ProcessSemanticSidecarLauncher : ISemanticSidecarConnectionFactory
{
    private readonly StdioSemanticSidecarConnectionFactory _inner;

    public ProcessSemanticSidecarLauncher(
        string executable,
        IReadOnlyList<string>? arguments = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        _inner = new StdioSemanticSidecarConnectionFactory(executable, arguments, environment);
    }

    public static ProcessSemanticSidecarLauncher ForServe(string executable, SemanticEncoderPin pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        return new ProcessSemanticSidecarLauncher(executable, ["serve", "--model", pin.ModelId]);
    }

    public IReadOnlyList<string> Arguments => _inner.Arguments;

    public ValueTask<ISemanticSidecarConnection> ConnectAsync(CancellationToken cancellationToken) =>
        _inner.ConnectAsync(cancellationToken);

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
