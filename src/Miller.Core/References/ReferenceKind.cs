namespace Miller.Core.References;

/// <summary>Canonical reference kinds shared across extractor evidence sources.</summary>
public enum ReferenceKind
{
    Unknown,
    Call,
    TypeUsage,
    MemberAccess,
    VariableReference,
    Instantiation,
    Inheritance,
    Implementation,
    Import,
    Reference,
    Usage,
}
