using Miller.Server.Tools;
using Miller.Testing;
using Miller.Tests.Support;
using Xunit;

namespace Miller.Tests.Server;

[Collection(StoreEnvironmentCollection.Name)]
public sealed class WorkspaceCtDiskPolicyTests
{
    [Fact]
    public void DiskAccountingHonorsTheProcessKillSwitch()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-ct-disk-policy-" + Guid.NewGuid());
        string? original = Environment.GetEnvironmentVariable(CtEnvironment.KillSwitch);
        try
        {
            using var store = new ContinuousTestStore(CtSchema.DbPathFor(root));
            store.UpsertCtGenerationPressure(1024, 0, 0, DateTimeOffset.UtcNow);
            Environment.SetEnvironmentVariable(CtEnvironment.KillSwitch, "on");
            Assert.NotNull(WorkspaceFactsAssembler.ReadCtDisk(root));

            Environment.SetEnvironmentVariable(CtEnvironment.KillSwitch, "off");
            Assert.Null(WorkspaceFactsAssembler.ReadCtDisk(root));
        }
        finally
        {
            Environment.SetEnvironmentVariable(CtEnvironment.KillSwitch, original);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
