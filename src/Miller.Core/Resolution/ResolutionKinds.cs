namespace Miller.Core.Resolution;

/// <summary>Maps extractor kind strings onto <see cref="ResolutionRefKind"/>. Null means no chain.</summary>
public static class ResolutionKinds
{
    public static ResolutionRefKind? FromIdentifierKind(string kind) => kind switch
    {
        "call" => ResolutionRefKind.Call,
        "type_usage" => ResolutionRefKind.TypeUsage,
        "member_access" => ResolutionRefKind.MemberAccess,
        "variable_ref" => ResolutionRefKind.VariableRef,
        _ => null,
    };

    public static ResolutionRefKind? FromPendingKind(string kind) => kind switch
    {
        "calls" => ResolutionRefKind.Call,
        "instantiates" => ResolutionRefKind.Instantiates,
        "uses" or "extends" or "implements" => ResolutionRefKind.TypeUsage,
        _ => null,
    };
}
