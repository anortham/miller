using Miller.Server;
using Miller.Server.Cli;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

[Collection(StoreEnvironmentCollection.Name)]
public sealed class CliMalformedPointerTests : IDisposable
{
    private readonly string _root;
    private readonly string _artifactPath;

    public CliMalformedPointerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "miller-cli-pointer-" + Guid.NewGuid().ToString("N"));
        _artifactPath = SymbolsLevelArtifact.Create(Path.Combine(_root, ".miller"));
        File.WriteAllText(Path.Combine(_root, ".miller", "store.json"), "not-json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void MalformedPointerRemainsAReadBlockerAfterTheFirstCliRefusal()
    {
        string? priorStoreMode = Environment.GetEnvironmentVariable("MILLER_INDEX_STORE");
        Environment.SetEnvironmentVariable("MILLER_INDEX_STORE", null);
        try
        {
            WorkspaceContext context = new(
                _root,
                _artifactPath,
                Path.Combine(_root, ".miller", "telemetry.db"),
                Path.Combine(_root, ".miller", "workspaces.db"),
                Path.Combine(_root, ".tools"),
                WorkspaceId: null);

            int first = RunSearch(context);
            int second = RunSearch(context);

            Assert.NotEqual(0, first);
            Assert.NotEqual(0, second);
            Assert.True(File.Exists(Path.Combine(_root, ".miller", "store.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MILLER_INDEX_STORE", priorStoreMode);
        }
    }

    private static int RunSearch(WorkspaceContext context)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        return CliDispatch.Run(["search", "Alpha"], context, stdout, stderr);
    }
}
