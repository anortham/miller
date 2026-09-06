using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Miller.Indexing.Store;

internal sealed record StoreConsumerCursorOutcome(
    bool Succeeded,
    bool Applied,
    string? SourceGeneration,
    string? ConsumerId,
    long? ConsumerSequence,
    string? Error);

internal static class StoreConsumerCursorRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaximumTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1L);

    internal static StoreConsumerCursorOutcome Advance(
        string binaryPath,
        string storeRoot,
        string? familyId,
        string expectedSourceGeneration,
        string consumerId,
        long sequence,
        TimeSpan? timeout = null)
    {
        if (!ValidCommon(binaryPath, storeRoot, familyId, consumerId, timeout) ||
            string.IsNullOrWhiteSpace(expectedSourceGeneration) || sequence < 0)
        {
            return Failure("cursor advance received invalid arguments");
        }

        var arguments = CommonArguments("advance", storeRoot, familyId, consumerId);
        arguments.AddRange(["--sequence", sequence.ToString(CultureInfo.InvariantCulture), "--apply", "--json"]);
        return Run(binaryPath, arguments, timeout, "cursor_advance", familyId, expectedSourceGeneration, consumerId, sequence);
    }

    internal static StoreConsumerCursorOutcome Release(
        string binaryPath,
        string storeRoot,
        string? familyId,
        string consumerId,
        TimeSpan? timeout = null)
    {
        if (!ValidCommon(binaryPath, storeRoot, familyId, consumerId, timeout))
            return Failure("cursor release received invalid arguments");

        var arguments = CommonArguments("release", storeRoot, familyId, consumerId);
        arguments.AddRange(["--apply", "--json"]);
        return Run(binaryPath, arguments, timeout, "cursor_release", familyId, null, consumerId, null);
    }

    private static StoreConsumerCursorOutcome Run(
        string binaryPath,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout,
        string action,
        string? expectedFamilyId,
        string? expectedSourceGeneration,
        string expectedConsumerId,
        long? expectedSequence)
    {
        ProcessOutput output;
        try
        {
            output = Invoke(binaryPath, arguments, timeout ?? DefaultTimeout).GetAwaiter().GetResult();
        }
        catch (Exception error) when (
            error is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return Failure(error.Message);
        }

        if (output.Error is not null)
            return Failure(output.Error);
        if (output.ExitCode != 0)
        {
            string? producerError = ReadProducerError(output.StandardOutput);
            string detail = producerError ?? FirstLine(output.StandardError);
            return Failure($"cursor operation exited {output.ExitCode}: {detail}");
        }

        return ParseSuccess(
            output.StandardOutput,
            action,
            expectedFamilyId,
            expectedSourceGeneration,
            expectedConsumerId,
            expectedSequence);
    }

    private static StoreConsumerCursorOutcome ParseSuccess(
        string reportJson,
        string action,
        string? expectedFamilyId,
        string? expectedSourceGeneration,
        string expectedConsumerId,
        long? expectedSequence)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(reportJson, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Failure("cursor operation emitted an invalid report");
            RejectDuplicateKeys(root);

            string? familyId = Text(root, "family_id");
            string? sourceGeneration = Text(root, "source_generation");
            string? consumerId = Text(root, "consumer_id");
            long? sequence = NumberOrNull(root, "consumer_sequence");
            string? disposition = Text(root, "disposition");
            bool dispositionMatches = action switch
            {
                "cursor_advance" => disposition is "advanced" or "no_change",
                "cursor_release" => disposition is "released" or "no_change",
                _ => false,
            };
            bool familyMatches = !string.IsNullOrWhiteSpace(familyId)
                                 && (expectedFamilyId is null || familyId == expectedFamilyId);
            bool generationMatches = !string.IsNullOrWhiteSpace(sourceGeneration)
                                     && (expectedSourceGeneration is null || sourceGeneration == expectedSourceGeneration);
            bool sequenceMatches = expectedSequence is null ? sequence is null : sequence == expectedSequence;
            if (Number(root, "report_schema_version") != 1
                || Text(root, "action") != action
                || Text(root, "mode") != "apply"
                || !dispositionMatches
                || !familyMatches
                || !generationMatches
                || consumerId != expectedConsumerId
                || !sequenceMatches
                || Text(root, "failure_class") != "none"
                || !Null(root, "error"))
            {
                return Failure("cursor operation report did not match the request");
            }

            return new StoreConsumerCursorOutcome(
                true,
                disposition != "no_change",
                sourceGeneration,
                consumerId,
                sequence,
                null);
        }
        catch (JsonException)
        {
            return Failure("cursor operation emitted an unreadable report");
        }
        catch (InvalidOperationException)
        {
            return Failure("cursor operation emitted an invalid report");
        }
    }

    private static async Task<ProcessOutput> Invoke(
        string binaryPath,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(binaryPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            return new ProcessOutput(null, "", "", $"could not start '{binaryPath}'");

        WindowsKillOnCloseJobAttachment attachment = WindowsKillOnCloseJob.Attach(process);
        using WindowsKillOnCloseJob? containment = attachment.Job;
        if (attachment.FailureReason is { } containmentFailure)
        {
            KillQuietly(process);
            return new ProcessOutput(null, "", "", $"cursor process containment failed: {containmentFailure}");
        }

        using var deadline = new CancellationTokenSource(timeout);
        Task<string> stdout = JulieStoreClient.ReadReaderOutputAsync(process.StandardOutput.BaseStream, deadline.Token);
        Task<string> stderr = JulieStoreClient.ReadReaderOutputAsync(process.StandardError.BaseStream, deadline.Token);
        Task exited = process.WaitForExitAsync(deadline.Token);
        try
        {
            var pending = new List<Task> { stdout, stderr, exited };
            while (pending.Count > 0)
            {
                Task finished = await Task.WhenAny(pending).ConfigureAwait(false);
                await finished.ConfigureAwait(false);
                pending.Remove(finished);
            }
            return new ProcessOutput(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), null);
        }
        catch (Exception error) when (
            error is OperationCanceledException
                or IOException
                or StoreReaderRegistrationException)
        {
            deadline.Cancel();
            KillQuietly(process);
            Observe(stdout);
            Observe(stderr);
            Observe(exited);
            return new ProcessOutput(
                null,
                "",
                "",
                error is OperationCanceledException
                    ? "cursor operation timed out"
                    : "cursor operation output was invalid or exceeded the capture limit");
        }
    }

    private static List<string> CommonArguments(
        string operation,
        string storeRoot,
        string? familyId,
        string consumerId)
    {
        var arguments = new List<string> { "store", "maintain", "cursor", operation, "--store", storeRoot };
        if (familyId is not null)
            arguments.AddRange(["--family", familyId]);
        arguments.AddRange(["--consumer", consumerId]);
        return arguments;
    }

    private static bool ValidCommon(
        string binaryPath,
        string storeRoot,
        string? familyId,
        string consumerId,
        TimeSpan? timeout) =>
        !string.IsNullOrWhiteSpace(binaryPath)
        && !string.IsNullOrWhiteSpace(storeRoot)
        && (familyId is null || !string.IsNullOrWhiteSpace(familyId))
        && !string.IsNullOrWhiteSpace(consumerId)
        && (timeout is null || timeout > TimeSpan.Zero && timeout <= MaximumTimeout);

    private static long? Number(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long number)
            ? number
            : null;

    private static long? NumberOrNull(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value))
            throw new InvalidOperationException();
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)
            ? number
            : throw new InvalidOperationException();
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Null(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Null;

    private static string? ReadProducerError(string reportJson)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(reportJson, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("error", out JsonElement error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            string? code = Text(error, "code");
            string? message = Text(error, "message");
            return (code, message) switch
            {
                ({ Length: > 0 }, { Length: > 0 }) => $"{code}: {message}",
                (_, { Length: > 0 }) => message,
                ({ Length: > 0 }, _) => code,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void RejectDuplicateKeys(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in node.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidOperationException();
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in node.EnumerateArray())
                RejectDuplicateKeys(child);
        }
    }

    private static void KillQuietly(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private static void Observe(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static string FirstLine(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
                return trimmed;
        }
        return "no diagnostic output";
    }

    private static StoreConsumerCursorOutcome Failure(string error) =>
        new(false, false, null, null, null, error);

    private sealed record ProcessOutput(int? ExitCode, string StandardOutput, string StandardError, string? Error);
}
