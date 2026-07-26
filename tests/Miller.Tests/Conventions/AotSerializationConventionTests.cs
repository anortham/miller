using Xunit;

namespace Miller.Tests.Conventions;

/// <summary>
/// Native AOT publish is a release gate, not part of the normal fast build. Keep AOT-critical server paths from
/// drifting back to reflection-based System.Text.Json overloads that only fail in the release workflow.
/// </summary>
public sealed class AotSerializationConventionTests
{
    [Fact]
    public void LeaderScanRequestQueue_UsesSourceGeneratedJsonMetadata()
    {
        string sourcePath = Path.Combine(
            ScaleTestSupport.RepoRoot(),
            "src",
            "Miller.Server",
            "Workspaces",
            "LeaderScanRequestQueue.cs");
        string source = File.ReadAllText(sourcePath);

        Assert.DoesNotContain("JsonSerializer.Serialize(request)", source);
        Assert.DoesNotContain("JsonSerializer.Deserialize<FullScanRequest>(json)", source);
    }

    [Fact]
    public void Server_DoesNotSerializeAnonymousTypesThroughReflection()
    {
        string serverRoot = Path.Combine(ScaleTestSupport.RepoRoot(), "src", "Miller.Server");
        foreach (string path in Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories))
            Assert.DoesNotContain("JsonSerializer.Serialize(new", File.ReadAllText(path), StringComparison.Ordinal);
    }
}
