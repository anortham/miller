namespace Miller.Core.Contracts;

/// <summary>
/// The symbol facts the cross-language resolver needs about one <c>symbols</c> row, beyond what the dependency graph
/// carries. A pure projection of julie's <c>symbols</c> table for M4: enough to resolve a type name to a symbol
/// (name + namespace/file for the tie-break), to read a return/DbSet/record signature, and to apply the route
/// <c>[controller]</c> token expansion (which needs the controller method's parent CLASS name).
/// </summary>
/// <param name="Id"><c>symbols.id</c>: the symbol's resolved id (never invented; comes from the index).</param>
/// <param name="Name"><c>symbols.name</c>: the symbol's name — the string <c>SymbolResolver</c> matches type names against.</param>
/// <param name="Kind"><c>symbols.kind</c>: e.g. <c>class</c>, <c>interface</c>, <c>record</c>, <c>method</c>, <c>property</c>.</param>
/// <param name="FilePath"><c>symbols.file_path</c> (workspace-relative): used for the namespace/project tie-break and as evidence.</param>
/// <param name="Signature">
/// <c>symbols.signature</c>: the declaration signature. Source of the endpoint return type (balanced-bracket unwrap),
/// the <c>DbSet&lt;T&gt;</c> generic arg, a C# record's positional params, and the <c>[FromBody]</c> parameter type.
/// </param>
/// <param name="Namespace">
/// The symbol's declaring namespace when known (for the name-resolution tie-break), or null. Two same-named types in
/// different namespaces are distinguished by this; absent a tie-break, &gt;1 match is ambiguous.
/// </param>
/// <param name="TestRole">
/// The julie <c>test_role</c> from <c>symbols.metadata</c>, or null when the field is absent. Used to exclude test
/// HttpClient url literals from the route bridge (see <see cref="Contracts.TestRole"/>).
/// </param>
/// <param name="ParentClassName">
/// For a controller METHOD, the name of its parent class (e.g. <c>AppSettingsController</c>) — the input the route
/// normalizer needs to expand the <c>[controller]</c> token BEFORE prefix concatenation. Null for symbols that are
/// not controller methods (or whose parent class is unknown).
/// </param>
public sealed record SymbolDetail(
    string Id,
    string Name,
    string Kind,
    string FilePath,
    string Signature,
    string? Namespace,
    TestRole? TestRole,
    string? ParentClassName);
