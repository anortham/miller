using System.Text.Json;

namespace Miller.SemanticBrokerProbe;

public sealed record SoakValidationResult(bool Succeeded, IReadOnlyList<string> Errors);

public static class SemanticBrokerSoakValidation
{
    private const int MinimumMeaningfulGpuDeltaMiB = 64;
    private const int MaximumAdditionalGpuDeltaMiB = 256;

    public static SoakValidationResult Validate(JsonElement root)
    {
        var errors = new List<string>();
        RequireEqual(root, "sameModelBrokerCount", 1, errors);
        RequireAtMost(root, "acceleratedBrokerCount", 1, errors);
        RequireEqual(root, "hungRequests", 0, errors);
        RequireEqual(root, "failedRequests", 0, errors);
        RequireEqual(root, "failedEventCount", 0, errors, "summary contains failed event records");
        RequireEqual(root, "finalBrokerCount", 0, errors);

        string? oldEndpoint = String(root, "oldEndpoint");
        string? newEndpoint = String(root, "newEndpoint");
        if (string.IsNullOrWhiteSpace(oldEndpoint)
            || string.IsNullOrWhiteSpace(newEndpoint)
            || string.Equals(oldEndpoint, newEndpoint, StringComparison.Ordinal))
        {
            errors.Add("old/new model endpoint identities are missing or equal");
        }

        int expectedCompletions = Int(root, "normalProbeExpectedCount", errors);
        int observedCompletions = Int(root, "normalProbeCompleteCount", errors);
        if (expectedCompletions != observedCompletions)
        {
            errors.Add(
                $"normal probe completion count was {observedCompletions}; expected {expectedCompletions}");
        }

        if (root.TryGetProperty("normalProbeExitCodes", out JsonElement exitCodes)
            && exitCodes.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement exitCode in exitCodes.EnumerateArray())
            {
                if (exitCode.ValueKind != JsonValueKind.Number || exitCode.GetInt32() != 0)
                {
                    errors.Add("summary contains a nonzero normal probe exit code");
                    break;
                }
            }
        }
        else
        {
            errors.Add("normalProbeExitCodes is missing or not an array");
        }

        int expectedKills = Int(root, "expectedKillCount", errors);
        int observedKills = Int(root, "observedExpectedKillCount", errors);
        if (expectedKills != observedKills)
            errors.Add($"observed expected-kill count was {observedKills}; expected {expectedKills}");

        ValidateRecovery(root, "brokerCrash", errors);
        ValidateRecovery(root, "ownerCrash", errors);
        ValidateSoak(root, errors);
        ValidateGpu(root, errors);
        ValidateAcceptance(root, errors);
        return new SoakValidationResult(errors.Count == 0, errors);
    }

    private static void ValidateRecovery(JsonElement root, string name, List<string> errors)
    {
        if (!root.TryGetProperty(name, out JsonElement recovery)
            || recovery.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{name} evidence is missing");
            return;
        }

        long kill = Long(recovery, "killUnixTimeMilliseconds", errors, $"{name} kill timestamp");
        if (!recovery.TryGetProperty("recoveryUnixTimeMilliseconds", out JsonElement recovered)
            || recovered.ValueKind == JsonValueKind.Null)
        {
            errors.Add($"{name} has no post-kill recovery event");
            return;
        }

        if (recovered.ValueKind != JsonValueKind.Number)
        {
            errors.Add($"{name} recovery timestamp is not numeric");
            return;
        }

        long recoveryTime = recovered.GetInt64();
        if (recoveryTime <= kill)
        {
            errors.Add($"{name} recovery predates its kill");
            return;
        }

        if (recoveryTime - kill > 30_000)
            errors.Add($"{name} recovery exceeded 30 seconds");
    }

    private static void ValidateSoak(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("soak", out JsonElement soak)
            || soak.ValueKind != JsonValueKind.Object)
        {
            errors.Add("soak duration evidence is missing");
            return;
        }

        long configuredMilliseconds =
            Long(soak, "configuredDurationSeconds", errors, "configured soak duration") * 1000;
        long observedMilliseconds =
            Long(soak, "observedTrafficMilliseconds", errors, "observed soak duration");
        if (observedMilliseconds < configuredMilliseconds)
        {
            errors.Add(
                $"soak ended before its configured duration ({observedMilliseconds}ms < {configuredMilliseconds}ms)");
        }
    }

    private static void ValidateGpu(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("gpu", out JsonElement gpu)
            || gpu.ValueKind != JsonValueKind.Object
            || !gpu.TryGetProperty("pass", out JsonElement pass)
            || pass.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        SoakValidationResult result = ValidateRecordedNvidiaSoak(gpu);
        errors.AddRange(result.Errors);
    }

    public static SoakValidationResult ValidateRecordedNvidiaSoak(JsonElement gpu)
    {
        var errors = new List<string>();
        if (gpu.ValueKind != JsonValueKind.Object)
        {
            errors.Add("GPU evidence is missing");
            return new SoakValidationResult(false, errors);
        }

        bool passed = gpu.TryGetProperty("pass", out JsonElement pass)
            && pass.ValueKind == JsonValueKind.True;
        if (!passed)
            errors.Add("gpu.pass is not true");

        bool accelerated = gpu.TryGetProperty("warmAccelerated", out JsonElement warmAccelerated)
            && warmAccelerated.ValueKind == JsonValueKind.True;
        int warmBrokerCount = Int(gpu, "warmBrokerCount", errors);
        if (!accelerated)
            errors.Add("gpu.warmAccelerated is not true");
        if (warmBrokerCount != 1)
            errors.Add($"gpu.warmBrokerCount was {warmBrokerCount}; expected 1");

        int oneDelta = Int(gpu, "oneSessionDeltaMiB", errors);
        int manyDelta = Int(gpu, "manySessionDeltaMiB", errors);
        if (oneDelta < MinimumMeaningfulGpuDeltaMiB)
            errors.Add(
                $"gpu.oneSessionDeltaMiB is below the {MinimumMeaningfulGpuDeltaMiB} MiB proof floor");
        if (manyDelta > oneDelta + MaximumAdditionalGpuDeltaMiB)
        {
            errors.Add(
                $"gpu.manySessionDeltaMiB exceeds gpu.oneSessionDeltaMiB + {MaximumAdditionalGpuDeltaMiB} MiB");
        }

        return new SoakValidationResult(errors.Count == 0, errors);
    }

    private static void ValidateAcceptance(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("acceptance", out JsonElement acceptance)
            || acceptance.ValueKind != JsonValueKind.Object)
        {
            errors.Add("acceptance object is missing");
            return;
        }

        JsonValueKind gpuPassKind = root.TryGetProperty("gpu", out JsonElement gpu)
            && gpu.ValueKind == JsonValueKind.Object
            && gpu.TryGetProperty("pass", out JsonElement gpuPass)
                ? gpuPass.ValueKind
                : JsonValueKind.Undefined;
        JsonValueKind gpuAcceptanceKind =
            acceptance.TryGetProperty("gpuEffectivelyConstant", out JsonElement gpuAcceptance)
                ? gpuAcceptance.ValueKind
                : JsonValueKind.Undefined;
        bool gpuFieldsMatch =
            gpuPassKind == JsonValueKind.True && gpuAcceptanceKind == JsonValueKind.True
            || gpuPassKind == JsonValueKind.Null && gpuAcceptanceKind == JsonValueKind.Null;
        if (!gpuFieldsMatch)
            errors.Add("acceptance.gpuEffectivelyConstant does not match gpu.pass");

        foreach (JsonProperty row in acceptance.EnumerateObject())
        {
            if (row.Value.ValueKind == JsonValueKind.False)
                errors.Add($"acceptance.{row.Name} is false");
            else if (row.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.Null))
                errors.Add($"acceptance.{row.Name} is not boolean or null");
        }
    }

    private static void RequireEqual(
        JsonElement root,
        string name,
        int expected,
        List<string> errors,
        string? message = null)
    {
        int actual = Int(root, name, errors);
        if (actual != expected)
            errors.Add(message ?? $"{name} was {actual}; expected {expected}");
    }

    private static void RequireAtMost(
        JsonElement root,
        string name,
        int maximum,
        List<string> errors)
    {
        int actual = Int(root, name, errors);
        if (actual > maximum)
            errors.Add($"{name} was {actual}; maximum is {maximum}");
    }

    private static int Int(JsonElement root, string name, List<string> errors)
    {
        if (root.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int result))
        {
            return result;
        }

        errors.Add($"{name} is missing or not an integer");
        return 0;
    }

    private static long Long(
        JsonElement root,
        string name,
        List<string> errors,
        string displayName)
    {
        if (root.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long result))
        {
            return result;
        }

        errors.Add($"{displayName} is missing or not an integer");
        return 0;
    }

    private static string? String(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
