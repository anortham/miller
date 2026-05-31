using System.Text;
using Miller.Core.Contracts;

namespace Miller.Core.Resolver;

/// <summary>
/// Builds a type's <see cref="FieldSet"/> (the scorer's Jaccard corroborator, design §5) and unwraps endpoint return
/// types to a named user type (the <c>responds→</c> edge, design §4 Leg 1). Pure and deterministic.
///
/// <para>Two field-set sources, because C# records differ from classes structurally:
/// <list type="bullet">
/// <item>class/interface: fields come from child symbols (<c>kind=property|field</c>) reached via <c>parent_id</c>;</item>
/// <item>C# <c>record</c>: records have NO property children, so the fields are the positional params parsed from the
/// declaration <c>signature</c> — a naive child query returns empty and would misfire the corroborator.</item>
/// </list>
/// A <c>[JsonProperty("x")]</c> annotation renames a field to its wire name so the field-set matches the JSON the TS
/// side sees.</para>
/// </summary>
public static class FieldSetExtractor
{
    // Generic wrappers that the responds-> edge unwraps to reach the carried user type.
    private static readonly string[] Wrappers =
    [
        "Task", "ValueTask", "ActionResult", "IActionResult", "IEnumerable", "IList", "List", "ICollection",
        "IReadOnlyList", "IReadOnlyCollection", "IQueryable", "IAsyncEnumerable", "Collection",
    ];

    // C# primitive / framework value types that are NOT a named user DTO target for the responds-> edge.
    private static readonly HashSet<string> Primitives = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint", "long", "ulong", "short",
        "ushort", "string", "object", "void", "Guid", "DateTime", "DateTimeOffset", "TimeSpan", "Boolean", "Int32",
        "Int64", "Int16", "Double", "Single", "Decimal", "String", "Object", "Byte", "Char",
    };

    // Wrappers that, when reached as the final unwrapped token (no generic arg left), mean "no named user type".
    private static readonly HashSet<string> BareNonTypes = new(StringComparer.Ordinal)
    {
        "ActionResult", "IActionResult", "Task", "ValueTask", "void",
    };

    /// <summary>
    /// Build the <see cref="FieldSet"/> for <paramref name="owner"/>. For a C# <c>record</c>, parses positional params
    /// from <paramref name="owner"/>'s signature; otherwise reads <paramref name="children"/> (the owner's child
    /// symbols) for properties/fields. <paramref name="annotations"/> supply <c>[JsonProperty]</c> renames keyed by the
    /// child symbol id.
    /// </summary>
    /// <param name="owner">The owning type symbol.</param>
    /// <param name="children">The owner's child symbols (already scoped to this owner by the caller via parent_id).</param>
    /// <param name="annotations">Annotations on the children (e.g. <c>[JsonProperty]</c>); may be empty.</param>
    public static FieldSet ExtractFields(
        SymbolDetail owner,
        IReadOnlyList<SymbolDetail> children,
        IReadOnlyList<SymbolAnnotation> annotations)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(children);
        ArgumentNullException.ThrowIfNull(annotations);

        return IsRecord(owner.Kind)
            ? new FieldSet(owner.Id, ParseRecordParams(owner.Signature))
            : new FieldSet(owner.Id, FromChildren(children, annotations));
    }

    /// <summary>
    /// Unwrap a return-type signature to the named user type it carries, peeling generic wrappers by balanced bracket
    /// depth (so <c>Task&lt;ActionResult&lt;X&gt;&gt;</c> and <c>Task&lt;ActionResult&gt;</c> are told apart — one
    /// token, not a substring). Returns null when the result is a bare wrapper (<c>ActionResult</c>/<c>IActionResult</c>),
    /// a primitive (<c>Task&lt;bool&gt;</c>), or otherwise not a named user type (so the <c>responds→</c> edge is dropped).
    /// </summary>
    /// <param name="returnType">The signature return type, e.g. <c>Task&lt;ActionResult&lt;AppSetting&gt;&gt;</c>.</param>
    public static string? UnwrapReturnType(string returnType)
    {
        ArgumentNullException.ThrowIfNull(returnType);

        var current = returnType.Trim();
        // Peel one wrapper layer at a time. Each layer is "Wrapper<inner>"; inner is taken by balanced brackets.
        while (true)
        {
            int open = current.IndexOf('<');
            if (open < 0)
                break; // no generic args left

            var head = current[..open].Trim();
            if (!IsWrapper(head))
                break; // a generic user type like Page<X> — head IS the named type; stop and evaluate it below

            var inner = BalancedInner(current, open);
            if (inner is null)
                break;
            current = inner.Trim();

            // A multi-arg generic inner (e.g. Dictionary<K,V>) is not a single carried DTO — give up.
            if (TopLevelCommaCount(current) > 0)
                return null;
        }

        // Strip any remaining nullable marker.
        current = current.TrimEnd('?').Trim();

        if (current.Length == 0)
            return null;
        if (BareNonTypes.Contains(current))
            return null;
        if (Primitives.Contains(current))
            return null;

        return current;
    }

    private static bool IsRecord(string kind)
        => kind.Contains("record", StringComparison.OrdinalIgnoreCase);

    private static bool IsWrapper(string head)
    {
        foreach (var w in Wrappers)
        {
            if (string.Equals(head, w, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <summary>Properties/fields from the owner's children, in declaration order, with any JsonProperty rename applied.</summary>
    private static IReadOnlyList<FieldMember> FromChildren(
        IReadOnlyList<SymbolDetail> children, IReadOnlyList<SymbolAnnotation> annotations)
    {
        var renames = JsonPropertyRenames(annotations);
        var fields = new List<FieldMember>();
        foreach (var child in children)
        {
            if (!IsFieldKind(child.Kind))
                continue;

            var name = renames.TryGetValue(child.Id, out var wire) ? wire : child.Name;
            var type = TypeFromMemberSignature(child.Signature);
            fields.Add(new FieldMember(name, type));
        }
        return fields;
    }

    private static bool IsFieldKind(string kind)
        => kind.Equals("property", StringComparison.OrdinalIgnoreCase)
        || kind.Equals("field", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The declared type of a member from a signature like <c>int Id</c>, <c>string Email { get; set; }</c>, or
    /// <c>public required string Name</c>. The member name is the LAST top-level token (after dropping any accessor
    /// block / expression body / initializer), and the type is everything before it with leading modifiers stripped.
    /// Tokenizing from the end (rather than locating the member name by substring) avoids the false hit when the name
    /// is a substring of its own type (e.g. <c>OrderRef Order</c>) and handles modifier-laden declarations.
    /// </summary>
    private static string TypeFromMemberSignature(string signature)
    {
        var sig = signature.Trim();
        if (sig.Length == 0)
            return string.Empty;

        // Drop a property accessor block / expression body / field initializer / terminator, e.g.
        // "string Email { get; set; }" -> "string Email", "int Id => _id;" -> "int Id", "string Tag = \"x\"" -> "string Tag".
        int cut = FirstTopLevelTerminator(sig);
        if (cut >= 0)
            sig = sig[..cut].Trim();

        // The member name is the last top-level token; the type is everything before it (leading modifiers removed).
        int lastSpace = LastTopLevelSpace(sig);
        if (lastSpace <= 0)
            return string.Empty; // only a single token (the name, or empty) — no separable declared type

        return StripLeadingWords(sig[..lastSpace].Trim(), MemberModifiers);
    }

    /// <summary>Map child symbol id → JSON wire name parsed from a <c>[JsonProperty("x")]</c> raw_text.</summary>
    private static Dictionary<string, string> JsonPropertyRenames(IReadOnlyList<SymbolAnnotation> annotations)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var a in annotations)
        {
            if (!a.AnnotationKey.Equals("jsonproperty", StringComparison.OrdinalIgnoreCase))
                continue;
            var name = FirstStringArg(a.RawText);
            if (name is not null)
                map[a.SymbolId] = name;
        }
        return map;
    }

    /// <summary>The first double-quoted string argument in a raw attribute text, or null.</summary>
    private static string? FirstStringArg(string rawText)
    {
        int start = rawText.IndexOf('"');
        if (start < 0)
            return null;
        int end = rawText.IndexOf('"', start + 1);
        if (end <= start)
            return null;
        return rawText[(start + 1)..end];
    }

    /// <summary>Parse a record's positional params from its signature into ordered (name, type) fields.</summary>
    private static IReadOnlyList<FieldMember> ParseRecordParams(string signature)
    {
        var open = signature.IndexOf('(');
        if (open < 0)
            return [];
        var inner = BalancedInner(signature, open);
        if (inner is null || inner.Trim().Length == 0)
            return [];

        var fields = new List<FieldMember>();
        foreach (var param in SplitTopLevel(inner))
        {
            var member = ParseParam(param);
            if (member is not null)
                fields.Add(member);
        }
        return fields;
    }

    /// <summary>One record param "Type Name" (modifiers / default values tolerated) → a field, or null if malformed.</summary>
    private static FieldMember? ParseParam(string param)
    {
        var p = param.Trim();
        if (p.Length == 0)
            return null;

        // Drop a default value: "int Page = 1" -> "int Page".
        int eq = TopLevelIndexOf(p, '=');
        if (eq >= 0)
            p = p[..eq].Trim();

        // The name is the last top-level token; the type is everything before it.
        int lastSpace = LastTopLevelSpace(p);
        if (lastSpace <= 0)
            return null;

        var type = p[..lastSpace].Trim();
        var name = p[(lastSpace + 1)..].Trim();

        // Strip leading parameter modifiers from the type ("params string[]" / "in Foo").
        type = StripLeadingModifiers(type);

        if (type.Length == 0 || name.Length == 0)
            return null;
        return new FieldMember(name, type);
    }

    // Parameter modifiers that can precede a record positional param's type.
    private static readonly string[] ParamModifiers = ["params", "in", "out", "ref", "this", "readonly"];

    // Member modifiers that can precede a property/field's type in a signature.
    private static readonly string[] MemberModifiers =
    [
        "public", "private", "protected", "internal", "static", "readonly", "required", "virtual",
        "override", "abstract", "sealed", "new", "volatile", "const", "extern", "unsafe", "async",
    ];

    private static string StripLeadingModifiers(string type) => StripLeadingWords(type, ParamModifiers);

    /// <summary>Strip any leading space-delimited word in <paramref name="words"/> from the front of <paramref name="s"/>.</summary>
    private static string StripLeadingWords(string s, string[] words)
    {
        var t = s;
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var w in words)
            {
                if (t.StartsWith(w + " ", StringComparison.Ordinal))
                {
                    t = t[(w.Length + 1)..].TrimStart();
                    changed = true;
                }
            }
        }
        return t;
    }

    // ---- balanced-bracket helpers (treat <...> and (...) by depth) ---------------------------------------------

    /// <summary>The substring between the bracket at <paramref name="open"/> and its matching close, or null.</summary>
    private static string? BalancedInner(string s, int open)
    {
        char openCh = s[open];
        char closeCh = openCh switch { '<' => '>', '(' => ')', '[' => ']', '{' => '}', _ => '\0' };
        if (closeCh == '\0')
            return null;

        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == openCh)
                depth++;
            else if (s[i] == closeCh)
            {
                depth--;
                if (depth == 0)
                    return s[(open + 1)..i];
            }
        }
        return null;
    }

    /// <summary>Split a param list on top-level commas (ignoring commas nested in &lt;&gt;/()/[]).</summary>
    private static IEnumerable<string> SplitTopLevel(string s)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        int depth = 0;
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '<' or '(' or '[':
                    depth++;
                    sb.Append(ch);
                    break;
                case '>' or ')' or ']':
                    depth--;
                    sb.Append(ch);
                    break;
                case ',' when depth == 0:
                    parts.Add(sb.ToString());
                    sb.Clear();
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        if (sb.Length > 0)
            parts.Add(sb.ToString());
        return parts;
    }

    private static int TopLevelCommaCount(string s)
    {
        int depth = 0, count = 0;
        foreach (var ch in s)
        {
            if (ch is '<' or '(' or '[')
                depth++;
            else if (ch is '>' or ')' or ']')
                depth--;
            else if (ch == ',' && depth == 0)
                count++;
        }
        return count;
    }

    private static int TopLevelIndexOf(string s, char target)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch is '<' or '(' or '[')
                depth++;
            else if (ch is '>' or ')' or ']')
                depth--;
            else if (ch == target && depth == 0)
                return i;
        }
        return -1;
    }

    private static int LastTopLevelSpace(string s)
    {
        int depth = 0, last = -1;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch is '<' or '(' or '[')
                depth++;
            else if (ch is '>' or ')' or ']')
                depth--;
            else if ((ch == ' ' || ch == '\t') && depth == 0)
                last = i;
        }
        return last;
    }

    /// <summary>The index of the first top-level accessor/body/initializer terminator (<c>{</c>, <c>=</c>, or <c>;</c>), or -1.</summary>
    private static int FirstTopLevelTerminator(string s)
    {
        int depth = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch is '<' or '(' or '[')
                depth++;
            else if (ch is '>' or ')' or ']')
                depth--;
            else if (depth == 0 && ch is '{' or '=' or ';')
                return i;
        }
        return -1;
    }
}
