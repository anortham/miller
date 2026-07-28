using System.Security.Cryptography;
using System.Text;
using Miller.Indexing.Semantic;
using Xunit;

namespace Miller.Tests.Indexing;

public sealed class SemanticBrokerEndpointTests
{
    [Fact]
    public void Create_DerivesTheFrozenIdentityLayoutAndBrokerArguments()
    {
        string millerHome = Path.Combine(Path.GetTempPath(), "miller-broker-endpoint", "home");
        SemanticEncoderPin pin = SemanticEncoderSelection.Active;
        string input =
            $"julie.semantic.broker|1|julie.embedding.sidecar|1|{pin.ModelId}|{pin.ModelSha256}";
        string identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..16];

        SemanticBrokerEndpoint endpoint = SemanticBrokerEndpoint.Create(millerHome, pin);

        Assert.Equal(identity, endpoint.Identity);
        Assert.Equal(Path.Combine(millerHome, "semantic"), endpoint.DirectoryPath);
        Assert.Equal(
            Path.Combine(millerHome, "semantic", $"broker-{identity}.lock"),
            endpoint.ServiceLockPath);
        Assert.Equal(
            Path.Combine(millerHome, "semantic", $"broker-{identity}.sock"),
            endpoint.UnixSocketPath);
        Assert.Equal(
            Path.Combine(millerHome, "semantic", "accelerator-v1.lock"),
            endpoint.AcceleratorLockPath);
        Assert.Equal($"miller-semantic-{identity}", endpoint.WindowsPipeName);
        Assert.Equal($@"\\.\pipe\miller-semantic-{identity}", endpoint.WindowsServerPipeName);
        Assert.Equal(
            [
                "broker",
                "--model", pin.ModelId,
                "--endpoint", endpoint.ServerEndpoint,
                "--lock", endpoint.ServiceLockPath,
                "--accelerator-lock", endpoint.AcceleratorLockPath,
            ],
            endpoint.BrokerArguments);
    }

    [Fact]
    public void Create_DoesNotTouchTheFilesystem()
    {
        string root = Path.Combine(Path.GetTempPath(), "miller-broker-endpoint-" + Guid.NewGuid());
        string millerHome = Path.Combine(root, "home");

        _ = SemanticBrokerEndpoint.Create(millerHome, SemanticEncoderSelection.Active);

        Assert.False(Directory.Exists(root));
    }
}
