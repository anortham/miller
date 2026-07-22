namespace Miller.Server.Telemetry;

/// <summary>Runtime telemetry contract and identifier-shadow sampling policy for an active canary mode.</summary>
public sealed class CanaryContractProfile
{
    public const int V2ContractVersion = 2;
    public const int V3ContractVersion = 3;

    private static readonly CanaryContractProfile V2 = new(V2ContractVersion, 10);
    private static readonly CanaryContractProfile V3 = new(V3ContractVersion, 100);

    private CanaryContractProfile(int contractVersion, int identifierShadowPercent)
    {
        ContractVersion = contractVersion;
        IdentifierShadowPercent = identifierShadowPercent;
    }

    public int ContractVersion { get; }

    public int IdentifierShadowPercent { get; }

    public static CanaryContractProfile For(CanaryMode mode) => mode switch
    {
        CanaryMode.On => V2,
        CanaryMode.Decision => V3,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Inactive canary modes have no contract profile."),
    };
}
