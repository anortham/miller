using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Providers.Rust;

[Trait("Category", "Scale")]
public sealed class RustProviderScaleTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-rust-scale-").FullName;

    private readonly HashSet<string> _ctTemps = new(StringComparer.Ordinal);

    public void Dispose()
    {
        BestEffortDelete(_dir);
        foreach (var temp in _ctTemps)
            BestEffortDelete(temp);
    }

    [Fact]
    public async Task Cargo_smoke_executes_a_tiny_fixture_and_parses_results()
    {
        RequireCargoOrSkip();
        var ct = TestContext.Current.CancellationToken;
        var repo = Path.Combine(_dir, "repo");
        var src = Path.Combine(repo, "src");
        Directory.CreateDirectory(src);
        await File.WriteAllTextAsync(
            Path.Combine(repo, "Cargo.toml"),
            """
            [package]
            name = "adder"
            version = "0.1.0"
            edition = "2021"
            """,
            ct);
        await File.WriteAllTextAsync(
            Path.Combine(src, "lib.rs"),
            """
            pub fn add(a: i32, b: i32) -> i32 { a + b }

            #[cfg(test)]
            mod tests {
                use super::*;

                #[test]
                fn add_works() {
                    assert_eq!(add(1, 1), 2);
                }

                #[test]
                fn add_zero() {
                    assert_eq!(add(2, 0), 2);
                }
            }
            """,
            ct);

        var workspace = new ContinuousTestWorkspace(
            WorkspaceId: "ws:scale",
            WorkspaceRoot: repo,
            ProjectPath: Path.Combine(repo, "Cargo.toml"),
            BuildOutputRoot: Path.Combine(_dir, "state", "workspaces", "ws-safe", "ct-build"),
            Framework: "cargo");
        _ctTemps.Add(CtTempPaths.ForWorkspace(workspace));

        var runner = new TestProcessRunner();
        var provider = new RustTestProvider(runner);

        var cases = await provider.DiscoverAsync(workspace, ct);
        Assert.Contains(cases, row => row.Id == "rust-test:adder::lib/adder::tests::add_works");
        Assert.Contains(cases, row => row.Id == "rust-test:adder::lib/adder::tests::add_zero");
        Assert.All(cases, row => Assert.Equal("cargo", row.Framework));

        var result = await provider.RunAsync(
            new ContinuousTestProviderRunRequest(
                Workspace: workspace,
                SelectedRevision: "12",
                IndexIdentity: "store:scale-identity",
                RunId: "run:scale-smoke",
                TestCaseIds: ["rust-test:adder::lib/adder::tests::add_works"]),
            ct);

        var caseResult = Assert.Single(result.CaseResults);
        Assert.Equal("passed", result.Status);
        Assert.Equal("run:scale-smoke", result.RunId);
        Assert.Equal("rust-test:adder::lib/adder::tests::add_works", caseResult.TestCaseId);
        Assert.Equal("passed", caseResult.Status);
        Assert.Equal("12", caseResult.ResultRevision);
        Assert.Equal("store:scale-identity", caseResult.IndexIdentity);
        Assert.True(CtGenerationPaths.IsGenerationId(result.GenerationId));
        Assert.NotNull(result.ResultArtifactPath);
        Assert.True(File.Exists(result.ResultArtifactPath!));
        Assert.StartsWith(
            CtGenerationPaths.For(workspace, result.GenerationId!).ResultsDirectory,
            result.ResultArtifactPath!,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(repo, "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path) == "target");
    }

    private static void RequireCargoOrSkip()
    {
        if (FindOnPath(OperatingSystem.IsWindows() ? "cargo.exe" : "cargo") is null)
            Assert.Skip("cargo is required for RustTestProvider Scale smoke");
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return null;

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            var candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static void BestEffortDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
