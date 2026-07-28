using System.Security.Cryptography;
using System.Text;

namespace Miller.Indexing.Semantic;

/// <summary>Deterministic discovery identity and filesystem/pipe layout for one semantic model broker.</summary>
public sealed class SemanticBrokerEndpoint
{
    private const string IdentityPrefix =
        "julie.semantic.broker|1|julie.embedding.sidecar|1|";

    private SemanticBrokerEndpoint(string millerHome, SemanticEncoderPin pin)
    {
        string identityInput = IdentityPrefix + pin.ModelId + "|" + pin.ModelSha256;
        Identity = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(identityInput)))[..16];
        DirectoryPath = Path.Combine(millerHome, "semantic");
        ServiceLockPath = Path.Combine(DirectoryPath, $"broker-{Identity}.lock");
        UnixSocketPath = Path.Combine(DirectoryPath, $"broker-{Identity}.sock");
        AcceleratorLockPath = Path.Combine(DirectoryPath, "accelerator-v1.lock");
        WindowsPipeName = $"miller-semantic-{Identity}";
        WindowsServerPipeName = $@"\\.\pipe\{WindowsPipeName}";
        ServerEndpoint = OperatingSystem.IsWindows() ? WindowsServerPipeName : UnixSocketPath;
        BrokerArguments =
        [
            "broker",
            "--model", pin.ModelId,
            "--endpoint", ServerEndpoint,
            "--lock", ServiceLockPath,
            "--accelerator-lock", AcceleratorLockPath,
        ];
    }

    public string Identity { get; }

    public string DirectoryPath { get; }

    public string ServiceLockPath { get; }

    public string UnixSocketPath { get; }

    public string AcceleratorLockPath { get; }

    public string WindowsPipeName { get; }

    public string WindowsServerPipeName { get; }

    public string ServerEndpoint { get; }

    public IReadOnlyList<string> BrokerArguments { get; }

    public static SemanticBrokerEndpoint Create(string millerHome, SemanticEncoderPin pin)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(millerHome);
        ArgumentNullException.ThrowIfNull(pin);
        return new SemanticBrokerEndpoint(Path.GetFullPath(millerHome), pin);
    }
}
