using System.Security.Cryptography;
using System.Text;

namespace Miller.Indexing.Semantic;

/// <summary>Deterministic discovery identity and filesystem/pipe layout for one semantic model broker.</summary>
public sealed class SemanticBrokerEndpoint
{
    private const string IdentityPrefix =
        "julie.semantic.broker|1|julie.embedding.sidecar|1|";
    private const string ShortSocketRoot = "/tmp";
    private const int LinuxUnixSocketPathLimit = 107;
    private const int MacOsUnixSocketPathLimit = 103;

    private SemanticBrokerEndpoint(string millerHome, SemanticEncoderPin pin)
    {
        string identityInput = IdentityPrefix + pin.ModelId + "|" + pin.ModelSha256;
        Identity = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(identityInput)))[..16];
        DirectoryPath = Path.Combine(millerHome, "semantic");
        ServiceLockPath = Path.Combine(DirectoryPath, $"broker-{Identity}.lock");
        UnixSocketPath = ResolveUnixSocketPath(millerHome, Identity);
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

    private static string ResolveUnixSocketPath(string millerHome, string identity)
    {
        string legacyPath = Path.Combine(millerHome, "semantic", $"broker-{identity}.sock");
        if (OperatingSystem.IsWindows())
        {
            return legacyPath;
        }

        int platformLimit = OperatingSystem.IsMacOS()
            ? MacOsUnixSocketPathLimit
            : LinuxUnixSocketPathLimit;
        if (Encoding.UTF8.GetByteCount(legacyPath) <= platformLimit)
        {
            return legacyPath;
        }

        string userHash = ShortHash(Environment.UserName);
        string homeHash = ShortHash(millerHome);
        string fallbackPath = Path.Combine(
            ShortSocketRoot,
            $"miller-semantic-u{userHash}",
            $"broker-{homeHash}-{identity}.sock");
        if (Encoding.UTF8.GetByteCount(fallbackPath) > platformLimit)
        {
            throw new InvalidOperationException(
                "Semantic broker Unix socket fallback path exceeds the platform limit.");
        }

        return fallbackPath;
    }

    private static string ShortHash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

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
