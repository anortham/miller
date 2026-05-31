using Miller.Core.Contracts;

namespace Miller.Core.Resolver;

/// <summary>
/// Computes the field-set overlap the scorer's <see cref="FieldSetSignal"/> corroborator carries (design §5). A leg
/// builds the signal from two <see cref="FieldSet"/>s; this helper produces the Jaccard ratio over field NAMES and the
/// anchoring field count (the smaller shape's count — so a 1-field/generic shape can be refused as a corroborator). Pure
/// and deterministic; case-insensitive on field names so a <c>userId</c>↔<c>UserId</c> rename still overlaps.
/// </summary>
public static class FieldSetSimilarity
{
    /// <summary>
    /// Build a <see cref="FieldSetSignal"/> from two field-sets. <see cref="FieldSetSignal.Jaccard"/> is the field-NAME
    /// Jaccard (|A∩B| / |A∪B|, distinct names, case-insensitive); <see cref="FieldSetSignal.FieldCount"/> is the MIN of
    /// the two distinct-name counts — the value the §5 "1-field can't anchor" rule reads (the smaller shape is what is
    /// being corroborated, so a 1-field side caps the anchor at 1). When both shapes are empty the Jaccard is 0.
    /// </summary>
    /// <param name="a">One field-set (order irrelevant — the comparison is set-based).</param>
    /// <param name="b">The other field-set.</param>
    /// <param name="evidence">Optional <c>file:line</c> evidence for the resulting signal.</param>
    public static FieldSetSignal Compare(FieldSet a, FieldSet b, Evidence? evidence = null)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var namesA = ToNameSet(a);
        var namesB = ToNameSet(b);

        int fieldCount = Math.Min(namesA.Count, namesB.Count);
        double jaccard = Jaccard(namesA, namesB);

        return new FieldSetSignal(fieldCount, jaccard, evidence);
    }

    /// <summary>The field-NAME Jaccard of two field-sets in [0,1]; 0 when their union is empty.</summary>
    /// <param name="a">One field-set.</param>
    /// <param name="b">The other field-set.</param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public static double Jaccard(FieldSet a, FieldSet b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return Jaccard(ToNameSet(a), ToNameSet(b));
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 && b.Count == 0)
            return 0.0;

        int intersection = 0;
        foreach (var name in a)
        {
            if (b.Contains(name))
                intersection++;
        }

        int union = a.Count + b.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    /// <summary>The distinct (case-insensitive) field names of a field-set.</summary>
    private static HashSet<string> ToNameSet(FieldSet fieldSet)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fieldSet.Fields)
        {
            if (!string.IsNullOrWhiteSpace(field.Name))
                set.Add(field.Name);
        }
        return set;
    }
}
