using System.Collections.Frozen;

namespace Miller.Core.Resolution;

/// <summary>The five lookup arms the driver may run.</summary>
public enum ResolutionTier
{
    Local,
    Import,
    Receiver,
    StaticType,
    Global,
}

/// <summary>Kind tables, tier chains, and confidence constants for resolution policy v6.</summary>
public static class ResolutionPolicy
{
    public const int Version = 6;
    public const double LocalConfidence = 0.95;
    public const double ImportConfidence = 0.85;
    public const double ReceiverDeclaredConfidence = 0.75;
    public const double ReceiverInferredConfidence = 0.65;
    public const double StaticTypeConfidence = 0.70;
    public const double GlobalConfidence = 0.55;
    public const string LocalMethod = "tier1_local";
    public const string ImportMethod = "tier2_import";
    public const string ReceiverMethod = "tier3_receiver";
    public const string StaticTypeMethod = "tier3_static_type";
    public const string GlobalMethod = "tier4_global";

    public static readonly FrozenSet<FactSymbolKind> TypeLike = new[]
    {
        FactSymbolKind.Class,
        FactSymbolKind.Interface,
        FactSymbolKind.Struct,
        FactSymbolKind.Enum,
        FactSymbolKind.Type,
        FactSymbolKind.Trait,
        FactSymbolKind.Union,
        FactSymbolKind.Delegate,
    }.ToFrozenSet();

    private static readonly FrozenSet<FactSymbolKind> CallKinds =
        new[] { FactSymbolKind.Function, FactSymbolKind.Method, FactSymbolKind.Constructor }.ToFrozenSet();

    private static readonly FrozenSet<FactSymbolKind> InstantiatesKinds =
        new[] { FactSymbolKind.Class, FactSymbolKind.Struct, FactSymbolKind.Constructor }.ToFrozenSet();

    private static readonly FrozenSet<FactSymbolKind> MemberAccessKinds = new[]
    {
        FactSymbolKind.Property,
        FactSymbolKind.Field,
        FactSymbolKind.Method,
        FactSymbolKind.Constant,
        FactSymbolKind.EnumMember,
    }.ToFrozenSet();

    private static readonly FrozenSet<FactSymbolKind> VariableRefKinds = new[]
    {
        FactSymbolKind.Variable,
        FactSymbolKind.Constant,
        FactSymbolKind.Field,
        FactSymbolKind.Property,
    }.ToFrozenSet();

    private static readonly FrozenSet<FactSymbolKind> Tier4CallKinds =
        new[] { FactSymbolKind.Function, FactSymbolKind.Constructor }.ToFrozenSet();

    private static readonly FrozenSet<FactSymbolKind> EmptyKinds = FrozenSet<FactSymbolKind>.Empty;

    private static readonly FrozenSet<FactSymbolKind> EsModuleTypeKinds =
        new[] { FactSymbolKind.Class, FactSymbolKind.Enum }.ToFrozenSet();

    public static bool IsTypeLike(FactSymbolKind kind) => TypeLike.Contains(kind);

    public static bool IsEsModuleLanguage(string language) => language is "javascript" or "jsx" or "typescript" or "tsx";

    public static bool IsTier2Language(string language) => language is "typescript" or "javascript";

    public static IReadOnlySet<FactSymbolKind> CompatibleKinds(ResolutionRefKind refKind, bool tier4) =>
        (refKind, tier4) switch
        {
            (ResolutionRefKind.Call, false) => CallKinds,
            (ResolutionRefKind.Call, true) => Tier4CallKinds,
            (ResolutionRefKind.Instantiates, _) => InstantiatesKinds,
            (ResolutionRefKind.TypeUsage, _) => TypeLike,
            (ResolutionRefKind.MemberAccess, false) => MemberAccessKinds,
            (ResolutionRefKind.VariableRef, false) => VariableRefKinds,
            _ => EmptyKinds,
        };

    public static IReadOnlySet<FactSymbolKind> EsModuleStaticTypeKinds => EsModuleTypeKinds;

    public static IReadOnlyList<ResolutionTier> Chain(
        ResolutionOrigin origin,
        ResolutionRefKind refKind,
        bool hasReceiver)
    {
        if (origin == ResolutionOrigin.Pending)
        {
            return refKind switch
            {
                ResolutionRefKind.Call when hasReceiver => [ResolutionTier.Receiver, ResolutionTier.StaticType],
                ResolutionRefKind.Call or ResolutionRefKind.Instantiates or ResolutionRefKind.TypeUsage
                    => [ResolutionTier.Import, ResolutionTier.Receiver, ResolutionTier.StaticType, ResolutionTier.Global],
                ResolutionRefKind.MemberAccess when hasReceiver => [ResolutionTier.Receiver, ResolutionTier.StaticType],
                ResolutionRefKind.MemberAccess => [ResolutionTier.Import, ResolutionTier.Receiver, ResolutionTier.StaticType],
                _ => [],
            };
        }

        return refKind switch
        {
            ResolutionRefKind.Call when hasReceiver => [ResolutionTier.Receiver, ResolutionTier.StaticType],
            ResolutionRefKind.Call or ResolutionRefKind.TypeUsage
                => [ResolutionTier.Import, ResolutionTier.StaticType, ResolutionTier.Global],
            ResolutionRefKind.MemberAccess when hasReceiver => [ResolutionTier.Receiver, ResolutionTier.StaticType],
            ResolutionRefKind.VariableRef => [ResolutionTier.Local],
            _ => [],
        };
    }

    public static FactSymbolKind? ParseSymbolKind(string kind) => kind switch
    {
        "class" => FactSymbolKind.Class,
        "interface" => FactSymbolKind.Interface,
        "function" => FactSymbolKind.Function,
        "method" => FactSymbolKind.Method,
        "variable" => FactSymbolKind.Variable,
        "constant" => FactSymbolKind.Constant,
        "property" => FactSymbolKind.Property,
        "enum" => FactSymbolKind.Enum,
        "enum_member" => FactSymbolKind.EnumMember,
        "module" => FactSymbolKind.Module,
        "namespace" => FactSymbolKind.Namespace,
        "type" => FactSymbolKind.Type,
        "trait" => FactSymbolKind.Trait,
        "struct" => FactSymbolKind.Struct,
        "union" => FactSymbolKind.Union,
        "field" => FactSymbolKind.Field,
        "constructor" => FactSymbolKind.Constructor,
        "destructor" => FactSymbolKind.Destructor,
        "operator" => FactSymbolKind.Operator,
        "import" => FactSymbolKind.Import,
        "export" => FactSymbolKind.Export,
        "event" => FactSymbolKind.Event,
        "delegate" => FactSymbolKind.Delegate,
        _ => null,
    };

    public static bool? ParseIsStatic(string? raw) => raw switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    public static int TierNumber(ResolutionTier tier) => tier switch
    {
        ResolutionTier.Local => 1,
        ResolutionTier.Import => 2,
        ResolutionTier.Receiver or ResolutionTier.StaticType => 3,
        _ => 4,
    };

    public static string TierMethod(ResolutionTier tier) => tier switch
    {
        ResolutionTier.Local => LocalMethod,
        ResolutionTier.Import => ImportMethod,
        ResolutionTier.Receiver => ReceiverMethod,
        ResolutionTier.StaticType => StaticTypeMethod,
        _ => GlobalMethod,
    };
}
