namespace Miller.Core.Resolution;

/// <summary>Whether the edge came from a bare identifier or a pending relationship.</summary>
public enum ResolutionOrigin
{
    Identifier,
    Pending,
}

/// <summary>Canonical reference kind after identifier or pending-kind mapping.</summary>
public enum ResolutionRefKind
{
    Call,
    Instantiates,
    TypeUsage,
    MemberAccess,
    VariableRef,
}

/// <summary>The four total outcomes of a query-time resolve.</summary>
public enum ResolutionOutcomeKind
{
    Resolved,
    Ambiguous,
    Missing,
    NoContext,
}

/// <summary>Known <c>symbols.kind</c> values. Unknown strings are dropped before they reach the resolver.</summary>
public enum FactSymbolKind
{
    Class,
    Interface,
    Function,
    Method,
    Variable,
    Constant,
    Property,
    Enum,
    EnumMember,
    Module,
    Namespace,
    Type,
    Trait,
    Struct,
    Union,
    Field,
    Constructor,
    Destructor,
    Operator,
    Import,
    Export,
    Event,
    Delegate,
}

/// <summary>Identity of one symbol row in a pinned file version.</summary>
public readonly record struct FactSymbolKey(long VersionId, string SymbolId);

/// <summary>One symbol fact the resolver may bind to.</summary>
/// <param name="Key">Version plus symbol id.</param>
/// <param name="Name">Exact extracted name.</param>
/// <param name="Kind">Parsed symbol kind.</param>
/// <param name="Language">File language of the symbol.</param>
/// <param name="Parent">Parent symbol in the same version, or null at file top level.</param>
/// <param name="Signature">Extracted signature text, when present.</param>
/// <param name="Visibility">Extracted visibility, when present.</param>
/// <param name="IsStatic">Tri-state static flag from metadata.</param>
public sealed record FactSymbol(
    FactSymbolKey Key,
    string Name,
    FactSymbolKind Kind,
    string Language,
    FactSymbolKey? Parent,
    string? Signature,
    string? Visibility,
    bool? IsStatic);

/// <summary>One resolved-type fact attached to a symbol.</summary>
/// <param name="ResolvedType">Verbatim resolved type name. No namespace or generic stripping.</param>
/// <param name="IsInferred">True when the extractor marked the fact inferred.</param>
public sealed record FactTypeFact(string ResolvedType, bool IsInferred);

/// <summary>One identifier or pending edge to resolve.</summary>
public sealed record ResolutionInput(
    ResolutionOrigin Origin,
    ResolutionRefKind RefKind,
    string Language,
    long VersionId,
    string Name,
    string? Receiver,
    string? ReceiverQualifier,
    string? CallerScopeSymbolId,
    double SourceConfidence,
    string? ConsumerPath = null,
    string? ReceiverType = null);

/// <summary>The total outcome of one <see cref="QueryTimeResolver.Resolve"/> call.</summary>
public sealed record ResolutionOutcome(
    ResolutionOutcomeKind Kind,
    FactSymbolKey? Target,
    int? Tier,
    double? Confidence,
    string? Method,
    int? CandidateCount)
{
    /// <summary>No applicable chain, empty name, or an unmapped kind the caller already skipped.</summary>
    public static ResolutionOutcome NoContext { get; } =
        new(ResolutionOutcomeKind.NoContext, null, null, null, null, null);

    /// <summary>At least one tier ran and none produced a unique or ambiguous answer.</summary>
    public static ResolutionOutcome Missing { get; } =
        new(ResolutionOutcomeKind.Missing, null, null, null, null, null);

    public static ResolutionOutcome Ambiguous(int candidateCount) =>
        new(ResolutionOutcomeKind.Ambiguous, null, null, null, null, candidateCount);

    public static ResolutionOutcome Resolved(
        FactSymbolKey target,
        int tier,
        double confidence,
        string method) =>
        new(ResolutionOutcomeKind.Resolved, target, tier, confidence, method, null);
}
