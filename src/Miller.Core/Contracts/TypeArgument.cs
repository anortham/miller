namespace Miller.Core.Contracts;

/// <summary>
/// One row of julie's <c>type_arguments</c> table (extract reality 28/2): an ordered generic type argument at a
/// use-site, e.g. one side of a <c>CreateMap&lt;A,B&gt;</c>. Keyed by <see cref="IdentifierId"/> — all arguments of
/// one generic use-site share that id and are ordered by <see cref="Ordinal"/>.
///
/// <para><b>Resolution is by name.</b> The verified extract has <c>target_symbol_id</c> NULL for 0/1797 rows, so the
/// bridge never gets a resolved link — <see cref="TypeName"/> is resolved to a symbol by string name downstream
/// (<c>SymbolResolver</c>). This record deliberately omits the always-NULL <c>target_symbol_id</c> column.</para>
/// </summary>
/// <param name="IdentifierId">
/// <c>type_arguments.identifier_id</c> → <c>identifiers(id)</c>. The grouping key: all generic args of one use-site
/// (one <c>CreateMap</c> call) carry the same id; read them ordered by <see cref="Ordinal"/>.
/// </param>
/// <param name="Ordinal">
/// <c>type_arguments.ordinal</c>. The declared position (0-based). For <c>CreateMap&lt;A,B&gt;</c> the verified
/// invariant is copy-source→copy-dest (ordinal 0 = source, 1 = dest); it is NOT entity-vs-DTO — that is classified
/// independently downstream.
/// </param>
/// <param name="ParentArgId">
/// <c>type_arguments.parent_arg_id</c> → <c>type_arguments(id)</c>, or null at the top level. Encodes nesting for
/// generic args like <c>List&lt;Foo&gt;</c>; null for an un-nested argument.
/// </param>
/// <param name="TypeName">
/// <c>type_arguments.type_name</c>. The verbatim type name as written at the use-site (may be namespace-qualified,
/// e.g. <c>Core.Reporting.Data.Account</c>). The string the resolver matches by.
/// </param>
/// <param name="FilePath">
/// <c>type_arguments.file_path</c>. The use-site's file (a workspace-relative path by Miller convention), kept as
/// evidence and for the namespace/project tie-break in name resolution.
/// </param>
public sealed record TypeArgument(
    string IdentifierId,
    int Ordinal,
    string? ParentArgId,
    string TypeName,
    string FilePath);
