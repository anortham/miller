using System.Text.Json;
using System.Text.Json.Nodes;
using Miller.SemanticBrokerProbe;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SemanticBrokerSoakValidationTests
{
    [Fact]
    public void ValidSummary_AllowsExplicitHardwareSkip()
    {
        SoakValidationResult result = Validate(ValidSummary());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Errors));
    }

    [Theory]
    [InlineData("brokerCrash")]
    [InlineData("ownerCrash")]
    public void RecoveryMustBeObservedAfterTheKill(string scenario)
    {
        JsonObjectBuilder summary = ValidSummary();
        summary.Set($"{scenario}.recoveryUnixTimeMilliseconds", null);

        SoakValidationResult result = Validate(summary);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains($"{scenario} has no post-kill recovery", StringComparison.Ordinal));
    }

    [Fact]
    public void RecoveryBeforeKillIsRejected()
    {
        JsonObjectBuilder summary = ValidSummary();
        summary.Set("brokerCrash.recoveryUnixTimeMilliseconds", 999L);

        SoakValidationResult result = Validate(summary);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains("brokerCrash recovery predates its kill", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedEventsMissingCompletionsAndNonzeroExitCodesAreRejected()
    {
        JsonObjectBuilder summary = ValidSummary();
        summary.Set("failedEventCount", 1);
        summary.Set("normalProbeCompleteCount", 7);
        summary.Set("normalProbeExitCodes", new[] { 0, 0, 9 });

        SoakValidationResult result = Validate(summary);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains("failed event", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("normal probe completion", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("nonzero normal probe exit", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfiguredSoakMustReachItsObservedDuration()
    {
        JsonObjectBuilder summary = ValidSummary();
        summary.Set("soak.observedTrafficMilliseconds", 119_000L);
        summary.Set("soak.configuredDurationSeconds", 1_800);

        SoakValidationResult result = Validate(summary);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains("soak ended before its configured duration", StringComparison.Ordinal));
    }

    [Fact]
    public void GpuCannotPassWithoutAnAcceleratedWarmModelAndMeaningfulDelta()
    {
        JsonObjectBuilder summary = ValidSummary();
        summary.Set("gpu.pass", true);
        summary.Set("gpu.warmAccelerated", false);
        summary.Set("gpu.oneSessionDeltaMiB", 0);
        summary.Set("gpu.manySessionDeltaMiB", 0);

        SoakValidationResult result = Validate(summary);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains("gpu.warmAccelerated is not true", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("gpu.oneSessionDeltaMiB is below", StringComparison.Ordinal));
    }

    [Fact]
    public void RecordedNvidiaEvidenceRejectsCpuZerosEvenWhenTheDeltaFormulaPasses()
    {
        JsonObjectBuilder summary = ValidSummary();
        summary.Set("gpu.pass", true);
        summary.Set("gpu.warmAccelerated", false);
        summary.Set("gpu.warmBrokerCount", 0);
        summary.Set("gpu.oneSessionDeltaMiB", 0);
        summary.Set("gpu.manySessionDeltaMiB", 0);
        using JsonDocument document = JsonDocument.Parse(summary.ToJson());

        SoakValidationResult result =
            SemanticBrokerSoakValidation.ValidateRecordedNvidiaSoak(document.RootElement.GetProperty("gpu"));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains("warmAccelerated", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("warmBrokerCount", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("oneSessionDeltaMiB", StringComparison.Ordinal));
    }

    [Fact]
    public void AnyNonNullFalseAcceptanceRowFailsTheRun()
    {
        JsonObjectBuilder summary = ValidSummary();
        summary.Set("acceptance.sameModelOneBroker", false);

        SoakValidationResult result = Validate(summary);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.Contains("acceptance.sameModelOneBroker is false", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    public void GpuAcceptanceCannotClaimSuccessWithoutGpuProof(bool? gpuPass)
    {
        JsonObjectBuilder summary = ValidSummary();
        summary.Set("gpu.pass", gpuPass);
        summary.Set("acceptance.gpuEffectivelyConstant", true);

        SoakValidationResult result = Validate(summary);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors,
            error => error.Contains("acceptance.gpuEffectivelyConstant does not match gpu.pass", StringComparison.Ordinal));
    }

    [Fact]
    public void GpuAcceptanceCannotBeNullWhenGpuProofPassed()
    {
        JsonObjectBuilder summary = ValidSummary();
        summary.Set("gpu.pass", true);
        summary.Set("acceptance.gpuEffectivelyConstant", null);

        SoakValidationResult result = Validate(summary);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors,
            error => error.Contains("acceptance.gpuEffectivelyConstant does not match gpu.pass", StringComparison.Ordinal));
    }

    private static SoakValidationResult Validate(JsonObjectBuilder summary)
    {
        using JsonDocument document = JsonDocument.Parse(summary.ToJson());
        return SemanticBrokerSoakValidation.Validate(document.RootElement);
    }

    private static JsonObjectBuilder ValidSummary() => new(
        """
        {
          "sameModelBrokerCount": 1,
          "oldEndpoint": "old",
          "newEndpoint": "new",
          "acceleratedBrokerCount": 1,
          "brokerRecoverySeconds": 1.5,
          "ownerRecoverySeconds": 2.0,
          "hungRequests": 0,
          "failedRequests": 0,
          "failedEventCount": 0,
          "normalProbeExitCodes": [0, 0, 0],
          "normalProbeExpectedCount": 3,
          "normalProbeCompleteCount": 3,
          "expectedKillCount": 2,
          "observedExpectedKillCount": 2,
          "finalBrokerCount": 0,
          "brokerCrash": {
            "killUnixTimeMilliseconds": 1000,
            "recoveryUnixTimeMilliseconds": 2500
          },
          "ownerCrash": {
            "killUnixTimeMilliseconds": 3000,
            "recoveryUnixTimeMilliseconds": 5000
          },
          "soak": {
            "configuredDurationSeconds": 5,
            "observedTrafficMilliseconds": 5000
          },
          "gpu": {
            "warmBrokerCount": 1,
            "warmAccelerated": true,
            "oneSessionDeltaMiB": 128,
            "manySessionDeltaMiB": 256,
            "thresholdMiB": 256,
            "pass": null
          },
          "acceptance": {
            "sameModelOneBroker": true,
            "gpuEffectivelyConstant": null
          }
        }
        """);

    private sealed class JsonObjectBuilder(string json)
    {
        private readonly JsonObject _root = JsonNode.Parse(json)!.AsObject();

        public void Set(string path, object? value)
        {
            string[] parts = path.Split('.');
            JsonObject current = _root;
            for (var index = 0; index < parts.Length - 1; index++)
                current = current[parts[index]]!.AsObject();
            current[parts[^1]] = JsonValue.Create(value);
        }

        public string ToJson() => _root.ToJsonString();
    }
}
