namespace Miller.Core.Contracts;

/// <summary>
/// A DbContext <c>DbSet&lt;T&gt;</c> property — the verified-strong entity↔table anchor (Leg 3). Built from a
/// <c>symbols</c> row whose <c>kind=property</c> and whose <c>signature</c> contains <c>DbSet&lt;…&gt;</c>; the EF
/// convention is that the table name IS the property name and the entity IS the generic argument. BOTH come from this
/// one property symbol.
///
/// <para><b>Do not derive the table from the use-site.</b> The verified mistake the v2 design made was following the
/// DbSet use-site identifier's <c>containing_symbol_id</c>, which points at the DbContext CLASS (e.g.
/// <c>MyraNextContext</c>) for every table. MyraNext entities carry no <c>[Table]</c> attribute (0 <c>table</c> keys),
/// so the property name + convention is the anchor, not a pluralizer on the entity name.</para>
/// </summary>
/// <param name="PropertySymbolId">The id of the DbContext property symbol (<c>kind=property</c>) this is parsed from.</param>
/// <param name="TableName">
/// The table name = the property's name (EF convention), e.g. property <c>ApplicationUsers</c> ⇒ table
/// <c>ApplicationUsers</c>. NOT the pluralized entity name.
/// </param>
/// <param name="EntityTypeName">
/// The entity type = the <c>DbSet&lt;T&gt;</c> generic argument parsed from the signature (e.g. <c>ApplicationUser</c>).
/// Resolved to an entity symbol by name downstream.
/// </param>
/// <param name="FilePath">The DbContext file (workspace-relative), for evidence.</param>
/// <param name="StartLine">The 1-based line of the property declaration, for evidence (file:line).</param>
public sealed record DbSetProperty(
    string PropertySymbolId,
    string TableName,
    string EntityTypeName,
    string FilePath,
    int StartLine);
