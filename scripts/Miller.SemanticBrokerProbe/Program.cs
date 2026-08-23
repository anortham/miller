using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Miller.Indexing.Semantic;

if (args is ["--verify-summary", string summaryPath])
{
    using JsonDocument summary = JsonDocument.Parse(await File.ReadAllTextAsync(summaryPath));
    Miller.SemanticBrokerProbe.SoakValidationResult validation =
        Miller.SemanticBrokerProbe.SemanticBrokerSoakValidation.Validate(summary.RootElement);
    if (!validation.Succeeded)
    {
        foreach (string error in validation.Errors)
            Console.Error.WriteLine(error);
        return 1;
    }
    Console.WriteLine("""{"status":"verified"}""");
    return 0;
}

return await BrokerProbe.RunAsync(args).ConfigureAwait(false);

internal static class BrokerProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        ProbeOptions options;
        try
        {
            options = ProbeOptions.Parse(args);
        }
        catch (Exception ex)
        {
            Write(new { @event = "failed", reason = ex.Message });
            return 2;
        }

        string candidate = SemanticSidecarLayout.ExecutablePath(options.ToolsRoot);
        if (!File.Exists(candidate))
        {
            Write(new
            {
                @event = "skipped",
                reason = $"Broker-capable julie-semantic-sidecar not found at {candidate}. Restore the pinned package with scripts/restore-semantic-sidecar.sh or scripts/restore-semantic-sidecar.ps1.",
            });
            return 77;
        }

        SemanticEncoderPin? pin = MillerSemanticContract.FindEncoder(options.ModelId);
        if (pin is null)
        {
            Write(new { @event = "failed", reason = $"Unknown semantic model identity: {options.ModelId}" });
            return 2;
        }

        await using FileStream candidateStream = File.OpenRead(candidate);
        string checksum = Convert.ToHexString(
            await SHA256.HashDataAsync(candidateStream).ConfigureAwait(false)).ToLowerInvariant();
        var stopwatch = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(
            options.StartupTimeoutSeconds + options.DurationSeconds + options.GraceSeconds));
        var queryCount = 0;
        var batchCount = 0;
        var failedCount = 0;
        var hungCount = 0;
        long? outageStartedMilliseconds = null;
        long maxRecoveryMilliseconds = 0;
        var failureReasons = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            await using var factory = new SharedSemanticBrokerConnectionFactory(
                options.ToolsRoot,
                options.MillerHome,
                pin);
            await using var session = new SemanticEmbeddingSession(
                factory,
                new SemanticSessionOptions
                {
                    InitTimeout = TimeSpan.FromSeconds(options.StartupTimeoutSeconds),
                    RequestTimeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds),
                },
                pin,
                ownsConnectionFactory: false);

            SemanticEncoderHandshake? handshake = await session.EnsureStartedAsync(deadline.Token)
                .ConfigureAwait(false);
            if (handshake is null)
            {
                Write(Result("failed", options, candidate, checksum, stopwatch, factory.Snapshot,
                    null, queryCount, batchCount, failedCount + 1, hungCount,
                    [session.UnavailableReason ?? "handshake failed"]));
                return 1;
            }

            Write(Result("ready", options, candidate, checksum, stopwatch, factory.Snapshot,
                handshake, queryCount, batchCount, failedCount, hungCount, []));

            if (options.HealthOnly)
            {
                await Task.Delay(TimeSpan.FromSeconds(options.DurationSeconds), deadline.Token)
                    .ConfigureAwait(false);
                Write(Result("complete", options, candidate, checksum, stopwatch, factory.Snapshot,
                    handshake, queryCount, batchCount, failedCount, hungCount, []));
                return 0;
            }

            DateTimeOffset end = DateTimeOffset.UtcNow.AddSeconds(options.DurationSeconds);
            long trafficStartedMilliseconds = stopwatch.ElapsedMilliseconds;
            int observedReconnectCount = factory.Snapshot.ReconnectCount;
            do
            {
                Task<SemanticEmbedOutcome> query = session.EmbedQueryAsync(
                    $"probe-query-{queryCount % 17}",
                    deadline.Token);
                Task<SemanticEmbedOutcome> batch = session.EmbedBatchAsync(
                    Enumerable.Range(0, options.BatchSize)
                        .Select(index => $"probe-document-{batchCount % 13}-{index}")
                        .ToArray(),
                    deadline.Token);
                SemanticEmbedOutcome[] outcomes = await Task.WhenAll(query, batch).ConfigureAwait(false);
                queryCount++;
                batchCount++;
                bool iterationFailed = false;
                foreach (SemanticEmbedOutcome outcome in outcomes)
                {
                    if (outcome.Succeeded)
                        continue;
                    iterationFailed = true;
                    failedCount++;
                    if (outcome.TimedOut)
                        hungCount++;
                    if (!string.IsNullOrWhiteSpace(outcome.FailureReason))
                        failureReasons.Add(outcome.FailureReason);
                }
                if (iterationFailed)
                {
                    outageStartedMilliseconds ??= stopwatch.ElapsedMilliseconds;
                }
                else if (outageStartedMilliseconds is long outageStarted)
                {
                    long recoveryMilliseconds = stopwatch.ElapsedMilliseconds - outageStarted;
                    maxRecoveryMilliseconds = Math.Max(maxRecoveryMilliseconds, recoveryMilliseconds);
                    Write(new
                    {
                        @event = "recovered",
                        unixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        label = options.Label,
                        processId = Environment.ProcessId,
                        elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                        recoveryMilliseconds,
                        reconnectCount = factory.Snapshot.ReconnectCount,
                    });
                    outageStartedMilliseconds = null;
                }
                else if (factory.Snapshot.ReconnectCount > observedReconnectCount)
                {
                    Write(new
                    {
                        @event = "recovered",
                        unixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        label = options.Label,
                        processId = Environment.ProcessId,
                        elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                        recoveryMilliseconds = 0,
                        reconnectCount = factory.Snapshot.ReconnectCount,
                    });
                }
                observedReconnectCount = factory.Snapshot.ReconnectCount;

                if (options.IntervalMilliseconds > 0)
                    await Task.Delay(options.IntervalMilliseconds, deadline.Token).ConfigureAwait(false);
            }
            while (DateTimeOffset.UtcNow < end);

            Write(Result("complete", options, candidate, checksum, stopwatch, factory.Snapshot,
                handshake, queryCount, batchCount, failedCount, hungCount, failureReasons,
                maxRecoveryMilliseconds,
                stopwatch.ElapsedMilliseconds - trafficStartedMilliseconds));
            return failedCount == 0 && hungCount == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Write(new
            {
                @event = "failed",
                unixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                label = options.Label,
                processId = Environment.ProcessId,
                elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                candidate,
                candidateSha256 = checksum,
                queryCount,
                batchCount,
                failedCount = failedCount + 1,
                hungCount = hungCount + 1,
                reason =
                    $"Probe exceeded startup + duration + {options.GraceSeconds}s grace deadline.",
            });
            return 124;
        }
        catch (Exception ex)
        {
            Write(new
            {
                @event = "failed",
                unixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                label = options.Label,
                processId = Environment.ProcessId,
                elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                candidate,
                candidateSha256 = checksum,
                queryCount,
                batchCount,
                failedCount = failedCount + 1,
                hungCount,
                reason = ex.Message,
            });
            return 1;
        }
    }

    private static object Result(
        string eventName,
        ProbeOptions options,
        string candidate,
        string checksum,
        Stopwatch stopwatch,
        SemanticBrokerSnapshot snapshot,
        SemanticEncoderHandshake? handshake,
        int queryCount,
        int batchCount,
        int failedCount,
        int hungCount,
        IEnumerable<string> failureReasons,
        long maxRecoveryMilliseconds = 0,
        long trafficElapsedMilliseconds = 0) => new
        {
            @event = eventName,
            unixTimeMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            label = options.Label,
            processId = Environment.ProcessId,
            elapsedMilliseconds = stopwatch.ElapsedMilliseconds,
            candidate,
            candidateSha256 = checksum,
            modelId = handshake?.Pin.ModelId ?? options.ModelId,
            modelSha256 = handshake?.Pin.ModelSha256,
            encoderFingerprint = handshake?.EncoderFingerprint,
            dims = handshake?.Dims,
            backend = handshake?.ResolvedBackend ?? snapshot.Backend,
            accelerated = handshake?.Accelerated ?? snapshot.Accelerated,
            degradedReason = handshake?.DegradedReason
                ?? snapshot.BackendDegradedReason
                ?? snapshot.OwnershipDegradedReason,
            endpointIdentity = snapshot.EndpointIdentity,
            brokerState = snapshot.State,
            isOwner = snapshot.IsOwner,
            ownershipDegraded = snapshot.OwnershipDegraded,
            ownerProcessId = snapshot.OwnerProcessId,
            reconnectCount = snapshot.ReconnectCount,
            spawnAttempts = snapshot.SpawnAttempts,
            retiredOwnerCount = snapshot.RetiredOwnerCount,
            queryCount,
            batchCount,
            failedCount,
            hungCount,
            maxRecoveryMilliseconds,
            trafficElapsedMilliseconds,
            failureReasons = failureReasons.Order(StringComparer.Ordinal).ToArray(),
        };

    private static void Write(object value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
        Console.Out.Flush();
    }
}

internal sealed record ProbeOptions(
    string ToolsRoot,
    string MillerHome,
    string ModelId,
    string Label,
    int DurationSeconds,
    int IntervalMilliseconds,
    int BatchSize,
    bool HealthOnly,
    int StartupTimeoutSeconds,
    int RequestTimeoutSeconds,
    int GraceSeconds)
{
    public static ProbeOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
                throw new ArgumentException("Arguments must be --name value pairs.");
            values[args[index][2..]] = args[index + 1];
        }

        string toolsRoot = Required(values, "tools-root");
        string millerHome = Required(values, "miller-home");
        Directory.CreateDirectory(millerHome);
        return new ProbeOptions(
            Path.GetFullPath(toolsRoot),
            Path.GetFullPath(millerHome),
            values.GetValueOrDefault("model", MillerSemanticContract.DefaultEncoder.ModelId),
            values.GetValueOrDefault("label", "probe"),
            Positive(values, "duration-seconds", 1, allowZero: true),
            Positive(values, "interval-ms", 100, allowZero: true),
            Positive(values, "batch-size", 8),
            Boolean(values, "health-only", false),
            Positive(
                values,
                "startup-timeout-seconds",
                Positive(values, "timeout-seconds", 120)),
            Positive(values, "request-timeout-seconds", 30),
            Positive(values, "grace-seconds", 30));
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"Missing required --{name}.");

    private static int Positive(
        IReadOnlyDictionary<string, string> values,
        string name,
        int fallback,
        bool allowZero = false)
    {
        if (!values.TryGetValue(name, out string? raw))
            return fallback;
        if (!int.TryParse(raw, out int value) || value < (allowZero ? 0 : 1))
            throw new ArgumentException($"--{name} must be {(allowZero ? "non-negative" : "positive")}.");
        return value;
    }

    private static bool Boolean(
        IReadOnlyDictionary<string, string> values,
        string name,
        bool fallback)
    {
        if (!values.TryGetValue(name, out string? raw))
            return fallback;
        if (!bool.TryParse(raw, out bool value))
            throw new ArgumentException($"--{name} must be true or false.");
        return value;
    }
}
