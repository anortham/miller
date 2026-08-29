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
        string millerHome = OperatingSystem.IsWindows()
            ? Path.Combine(Path.GetTempPath(), "miller-broker-endpoint", "home")
            : "/tmp/miller-broker-endpoint/home";
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

    [Fact]
    public void Create_UsesDeterministicPerHomeFallbackWhenUnixSocketPathExceedsPlatformLimit()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix socket path fallback is not used on Windows.");

        SemanticEncoderPin pin = SemanticEncoderSelection.Active;
        string homeA = Path.Combine("/tmp", new string('a', 160));
        string homeB = Path.Combine("/tmp", new string('b', 160));
        SemanticBrokerEndpoint endpointA = SemanticBrokerEndpoint.Create(homeA, pin);
        SemanticBrokerEndpoint endpointB = SemanticBrokerEndpoint.Create(homeB, pin);

        int platformLimit = OperatingSystem.IsMacOS() ? 103 : 107;
        Assert.InRange(Encoding.UTF8.GetByteCount(endpointA.UnixSocketPath), 1, platformLimit);
        Assert.InRange(Encoding.UTF8.GetByteCount(endpointB.UnixSocketPath), 1, platformLimit);
        Assert.Equal(endpointA.UnixSocketPath, SemanticBrokerEndpoint.Create(homeA, pin).UnixSocketPath);
        Assert.NotEqual(endpointA.UnixSocketPath, endpointB.UnixSocketPath);

        string identity = endpointA.Identity;
        string userHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserName)))[..16];
        string homeHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(homeA))))[..16];
        string expectedFallbackPath = Path.Combine(
            "/tmp",
            $"miller-semantic-u{userHash}",
            $"broker-{homeHash}-{identity}.sock");
        Assert.Equal(expectedFallbackPath, endpointA.UnixSocketPath);
        Assert.Equal(endpointA.UnixSocketPath, endpointA.ServerEndpoint);
        Assert.Equal(
            Path.Combine(homeA, "semantic", $"broker-{identity}.lock"),
            endpointA.ServiceLockPath);
        Assert.Equal(
            Path.Combine(homeA, "semantic", "accelerator-v1.lock"),
            endpointA.AcceleratorLockPath);
        Assert.Equal(Path.Combine(homeA, "semantic"), endpointA.DirectoryPath);
        Assert.Equal(endpointA.Identity, endpointB.Identity);
    }

    [Fact]
    public void Create_UsesFallbackWhenUtf8ByteCountExceedsLimitDespiteShortCharacterCount()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix socket path fallback is not used on Windows.");

        string millerHome = Path.Combine("/tmp", new string('é', 60));
        SemanticBrokerEndpoint endpoint =
            SemanticBrokerEndpoint.Create(millerHome, SemanticEncoderSelection.Active);
        int platformLimit = OperatingSystem.IsMacOS() ? 103 : 107;
        string legacyPath = Path.Combine(
            millerHome,
            "semantic",
            $"broker-{endpoint.Identity}.sock");

        Assert.True(legacyPath.Length <= platformLimit);
        Assert.True(Encoding.UTF8.GetByteCount(legacyPath) > platformLimit);
        Assert.InRange(Encoding.UTF8.GetByteCount(endpoint.UnixSocketPath), 1, platformLimit);
        Assert.NotEqual(legacyPath, endpoint.UnixSocketPath);
        Assert.Matches(
            $"^/tmp/miller-semantic-u[0-9a-f]{{16}}/broker-[0-9a-f]{{16}}-{endpoint.Identity}\\.sock$",
            endpoint.UnixSocketPath);
    }
}
