namespace Miller.Core.Contracts;

/// <summary>
/// One field of a type's shape: an ordered (name, type) pair. For a class/interface this comes from a child symbol
/// (a property/field via <c>parent_id</c>); for a C# <c>record</c> it is parsed from the positional parameters in the
/// declaration <c>signature</c> (records have no property children). A <c>[JsonProperty("x")]</c> rename, when
/// present, replaces <see cref="Name"/> with the wire name.
/// </summary>
/// <param name="Name">The field's name (the JSON wire name when a <c>[JsonProperty]</c> rename applies).</param>
/// <param name="Type">The field's declared type name as written (may be qualified or generic).</param>
public sealed record FieldMember(string Name, string Type);

/// <summary>
/// The ordered field shape of a type — the corroborator the scorer's field-set Jaccard reads. Built by
/// <c>FieldSetExtractor</c> from either child symbols (class/interface properties via <c>parent_id</c>) or a C#
/// <c>record</c>'s positional params from its signature. A 1-field/generic shape can never anchor an edge on its own
/// (the scorer enforces that via <see cref="Count"/>); the field-set only RAISES an edge that already has a signal.
/// </summary>
/// <param name="OwnerId">The owning type symbol's id (the type whose shape this is).</param>
/// <param name="Fields">The ordered fields. Order is the declaration order (property order, or record param order).</param>
public sealed record FieldSet(string OwnerId, IReadOnlyList<FieldMember> Fields)
{
    /// <summary>The number of fields — the scorer reads this to refuse a 1-field shape as a sole anchor.</summary>
    public int Count => Fields.Count;
}
