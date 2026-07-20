using System.Globalization;

namespace Miller.Core.Analysis;

/// <summary>One body to analyze: a caller-chosen stable id plus the raw body text.</summary>
public sealed record NearDuplicateInput(string Id, string Text);

/// <summary>
/// A near-duplicate (Type-2) group: two or more bodies that normalize to highly overlapping token shingles.
/// <see cref="MemberIds"/> is ordinal-sorted and holds ONE representative per identical-body class, so bodies
/// the exact clone surface already reports are never re-reported here. <see cref="Similarity"/> is the weakest
/// accepted pairwise Jaccard edge that linked the group — a floor, not an average.
/// </summary>
public sealed record NearDuplicateGroup(double Similarity, IReadOnlyList<string> MemberIds);

/// <summary>Caller-tunable bounds. The detection constants themselves are fixed on <see cref="NearDuplicateAnalyzer"/>.</summary>
public sealed record NearDuplicateOptions
{
    /// <summary>Minimum pairwise Jaccard similarity for an edge to link two bodies.</summary>
    public double MinSimilarity { get; init; } = NearDuplicateAnalyzer.DefaultMinSimilarity;

    /// <summary>Maximum groups returned, applied after the deterministic ordering.</summary>
    public int MaxGroups { get; init; } = NearDuplicateAnalyzer.DefaultMaxGroups;
}

/// <summary>
/// Deterministic token-shingle MinHash/LSH detection of Type-2 clones — bodies that differ only in identifier
/// names, literal values, formatting, or a few edited tokens. Pure logic: no I/O, no clock, no
/// <see cref="Random"/>, no <c>string.GetHashCode</c>. The same inputs produce byte-identical groups on every
/// run, process, and platform, and the input ORDER does not affect the result.
///
/// <para><b>Fixed constants (the determinism contract — changing any one changes every reported group):</b></para>
/// <list type="bullet">
/// <item><b>Normalization</b> — whitespace is dropped; non-keyword words collapse to a single
/// <c>identifier</c> placeholder; numeric, string, and character literals collapse to <c>number</c>/<c>string</c>
/// placeholders; the fixed cross-language <see cref="KeywordCount"/>-word reserved set survives lowercased and
/// verbatim; every other character is its own punctuation token. Keeping keywords is what stops
/// <c>if</c>/<c>return</c>/<c>for</c> skeletons from matching everything.</item>
/// <item><b>Shingle size</b> = <see cref="ShingleSize"/> consecutive tokens. Small enough to survive local
/// edits, large enough that generic punctuation runs do not collide.</item>
/// <item><b>Token floor</b> = <see cref="MinTokens"/>. Below it a body is too small for an honest similarity
/// claim (a two-line accessor matches every other accessor), so it is skipped entirely.</item>
/// <item><b>Signature</b> = <see cref="SignatureLength"/> MinHash permutations, seeded by a SplitMix64 chain
/// from <see cref="SeedBase"/> (the golden-ratio constant). Fixed seeds, hashed with a locally implemented
/// FNV-1a/SplitMix64 pair, are why the signature is platform-stable.</item>
/// <item><b>LSH banding</b> = <see cref="BandCount"/> bands x <see cref="RowsPerBand"/> rows
/// (= <see cref="SignatureLength"/>). Its ~(1/b)^(1/r) knee sits just below
/// <see cref="DefaultMinSimilarity"/>, so pairs at the threshold are reliably proposed as candidates.</item>
/// <item><b>Similarity</b> = the EXACT Jaccard of the two shingle sets, computed only for LSH candidate pairs.
/// The MinHash signature prunes; it never decides. Reported similarity therefore carries no estimator error.</item>
/// <item><b>Threshold</b> = <see cref="DefaultMinSimilarity"/>: high enough that a reported pair is a real
/// rename-or-retune of the same code rather than two functions of a similar shape.</item>
/// </list>
///
/// <para>Known limitation: comments are not stripped (that is language-specific), so a body whose only
/// difference is prose in a comment can still land in a group.</para>
/// </summary>
public static class NearDuplicateAnalyzer
{
    /// <summary>Consecutive normalized tokens per shingle.</summary>
    public const int ShingleSize = 5;

    /// <summary>Normalized tokens a body needs before it is eligible for a similarity claim.</summary>
    public const int MinTokens = 24;

    /// <summary>MinHash permutations per body. Equals <see cref="BandCount"/> * <see cref="RowsPerBand"/>.</summary>
    public const int SignatureLength = 128;

    /// <summary>LSH bands over the signature.</summary>
    public const int BandCount = 32;

    /// <summary>Signature rows per LSH band.</summary>
    public const int RowsPerBand = 4;

    /// <summary>Seed for the SplitMix64 chain that derives the permutation seeds (golden-ratio constant).</summary>
    public const ulong SeedBase = 0x9E3779B97F4A7C15UL;

    /// <summary>Default minimum pairwise Jaccard similarity for a reported near-duplicate edge.</summary>
    public const double DefaultMinSimilarity = 0.75;

    /// <summary>Default cap on returned groups.</summary>
    public const int DefaultMaxGroups = 50;

    private const string IdentifierToken = "id";
    private const string NumberToken = "num";
    private const string StringToken = "str";

    private static readonly ulong[] PermutationSeeds = BuildPermutationSeeds();

    /// <summary>
    /// The fixed cross-language reserved-word set kept verbatim during normalization. It is deliberately a
    /// SET of control-flow/declaration words shared across the languages julie-extractors supports, never a
    /// per-language table: a word absent here simply normalizes to the identifier placeholder, which loses a
    /// little structure but never mis-attributes a language.
    /// </summary>
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "else", "elif", "elseif", "unless", "for", "foreach", "while", "do", "loop", "repeat",
        "switch", "case", "when", "match", "default", "break", "continue", "return", "yield", "goto",
        "try", "catch", "except", "finally", "throw", "throws", "raise", "rescue", "ensure", "defer",
        "class", "struct", "interface", "enum", "record", "trait", "impl", "protocol", "extension",
        "function", "func", "fn", "def", "sub", "method", "constructor", "new", "delete", "lambda",
        "var", "let", "const", "val", "static", "final", "readonly", "mutable", "mut", "volatile",
        "public", "private", "protected", "internal", "package", "module", "namespace", "using",
        "import", "export", "from", "require", "include", "extends", "implements", "inherits",
        "abstract", "virtual", "override", "sealed", "partial", "async", "await", "sync", "go", "chan",
        "select", "where", "with", "as", "is", "in", "of", "not", "and", "or", "xor", "null", "nil",
        "none", "undefined", "true", "false", "this", "self", "super", "base", "typeof", "sizeof",
        "instanceof", "void", "int", "long", "short", "byte", "char", "bool", "boolean", "float",
        "double", "decimal", "string", "object", "any", "unknown", "never", "auto", "type", "typedef",
        "operator", "delegate", "event", "get", "set", "out", "ref", "params", "throw_", "end", "then",
        "begin", "elsewhere", "pass", "del", "global", "nonlocal", "lateinit", "suspend", "unsafe",
    };

    /// <summary>Size of the fixed reserved-word set, surfaced so the doc contract can name it.</summary>
    public static int KeywordCount => Keywords.Count;

    /// <summary>
    /// Group the supplied bodies into Type-2 near-duplicate groups. Bodies whose raw text is byte-identical
    /// collapse to a single representative (the ordinally-first id) BEFORE matching, so identical bodies never
    /// form a group on their own and never appear twice inside one. Returns groups ordered by descending
    /// similarity, then descending member count, then the ordinally-first member id.
    /// </summary>
    public static IReadOnlyList<NearDuplicateGroup> FindGroups(
        IReadOnlyList<NearDuplicateInput> inputs,
        NearDuplicateOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        NearDuplicateOptions opts = options ?? new NearDuplicateOptions();
        if (inputs.Count < 2 || opts.MaxGroups < 1)
            return [];

        List<Candidate> candidates = BuildCandidates(inputs);
        if (candidates.Count < 2)
            return [];

        List<Edge> edges = ScoreCandidatePairs(candidates, opts.MinSimilarity);
        if (edges.Count == 0)
            return [];

        return AssembleGroups(candidates, edges, opts.MaxGroups);
    }

    private static List<Candidate> BuildCandidates(IReadOnlyList<NearDuplicateInput> inputs)
    {
        var byBody = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (NearDuplicateInput input in inputs)
        {
            if (input is null || string.IsNullOrEmpty(input.Id) || string.IsNullOrEmpty(input.Text))
                continue;
            if (!byBody.TryGetValue(input.Text, out string? representative)
                || string.CompareOrdinal(input.Id, representative) < 0)
            {
                byBody[input.Text] = input.Id;
            }
        }

        var candidates = new List<Candidate>(byBody.Count);
        foreach ((string text, string id) in byBody)
        {
            List<string> tokens = Normalize(text);
            if (tokens.Count < MinTokens)
                continue;
            HashSet<ulong> shingles = Shingle(tokens);
            if (shingles.Count == 0)
                continue;
            candidates.Add(new Candidate(id, shingles, Signature(shingles)));
        }

        candidates.Sort(static (left, right) => string.CompareOrdinal(left.Id, right.Id));
        return candidates;
    }

    private static List<Edge> ScoreCandidatePairs(List<Candidate> candidates, double minSimilarity)
    {
        var buckets = new Dictionary<ulong, List<int>>();
        for (int index = 0; index < candidates.Count; index++)
        {
            ulong[] signature = candidates[index].Signature;
            for (int band = 0; band < BandCount; band++)
            {
                ulong key = Fnv1aOffsetBasis;
                key = Fnv1aStep(key, (ulong)band);
                for (int row = 0; row < RowsPerBand; row++)
                    key = Fnv1aStep(key, signature[(band * RowsPerBand) + row]);

                if (!buckets.TryGetValue(key, out List<int>? members))
                    buckets[key] = members = [];
                members.Add(index);
            }
        }

        var scored = new Dictionary<(int Left, int Right), double>();
        foreach (List<int> members in buckets.Values)
        {
            if (members.Count < 2)
                continue;
            for (int i = 0; i < members.Count; i++)
            {
                for (int j = i + 1; j < members.Count; j++)
                {
                    (int left, int right) = (members[i], members[j]);
                    if (left == right || scored.ContainsKey((left, right)))
                        continue;
                    scored[(left, right)] = Jaccard(candidates[left].Shingles, candidates[right].Shingles);
                }
            }
        }

        var edges = new List<Edge>();
        foreach (((int left, int right), double similarity) in scored)
        {
            if (similarity >= minSimilarity)
                edges.Add(new Edge(left, right, similarity));
        }

        edges.Sort(static (a, b) => a.Left != b.Left ? a.Left.CompareTo(b.Left) : a.Right.CompareTo(b.Right));
        return edges;
    }

    private static List<NearDuplicateGroup> AssembleGroups(
        List<Candidate> candidates,
        List<Edge> edges,
        int maxGroups)
    {
        int[] parent = new int[candidates.Count];
        for (int i = 0; i < parent.Length; i++)
            parent[i] = i;

        foreach (Edge edge in edges)
            Union(parent, edge.Left, edge.Right);

        var members = new Dictionary<int, List<string>>();
        var floors = new Dictionary<int, double>();
        foreach (Edge edge in edges)
        {
            int root = Find(parent, edge.Left);
            floors[root] = floors.TryGetValue(root, out double current)
                ? Math.Min(current, edge.Similarity)
                : edge.Similarity;
        }

        for (int index = 0; index < candidates.Count; index++)
        {
            int root = Find(parent, index);
            if (!floors.ContainsKey(root))
                continue;
            if (!members.TryGetValue(root, out List<string>? ids))
                members[root] = ids = [];
            ids.Add(candidates[index].Id);
        }

        var groups = new List<NearDuplicateGroup>(members.Count);
        foreach ((int root, List<string> ids) in members)
        {
            if (ids.Count < 2)
                continue;
            ids.Sort(StringComparer.Ordinal);
            groups.Add(new NearDuplicateGroup(floors[root], ids));
        }

        groups.Sort(static (a, b) =>
        {
            int bySimilarity = b.Similarity.CompareTo(a.Similarity);
            if (bySimilarity != 0)
                return bySimilarity;
            int byCount = b.MemberIds.Count.CompareTo(a.MemberIds.Count);
            return byCount != 0 ? byCount : string.CompareOrdinal(a.MemberIds[0], b.MemberIds[0]);
        });

        if (groups.Count > maxGroups)
            groups.RemoveRange(maxGroups, groups.Count - maxGroups);
        return groups;
    }

    private static int Find(int[] parent, int node)
    {
        while (parent[node] != node)
        {
            parent[node] = parent[parent[node]];
            node = parent[node];
        }
        return node;
    }

    private static void Union(int[] parent, int left, int right)
    {
        int a = Find(parent, left);
        int b = Find(parent, right);
        if (a == b)
            return;
        if (a < b)
            parent[b] = a;
        else
            parent[a] = b;
    }

    private static double Jaccard(HashSet<ulong> left, HashSet<ulong> right)
    {
        HashSet<ulong> smaller = left.Count <= right.Count ? left : right;
        HashSet<ulong> larger = ReferenceEquals(smaller, left) ? right : left;

        int intersection = 0;
        foreach (ulong value in smaller)
        {
            if (larger.Contains(value))
                intersection++;
        }

        int union = left.Count + right.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    private static ulong[] Signature(HashSet<ulong> shingles)
    {
        ulong[] signature = new ulong[SignatureLength];
        Array.Fill(signature, ulong.MaxValue);
        foreach (ulong shingle in shingles)
        {
            for (int i = 0; i < SignatureLength; i++)
            {
                ulong permuted = SplitMix64(shingle ^ PermutationSeeds[i]);
                if (permuted < signature[i])
                    signature[i] = permuted;
            }
        }
        return signature;
    }

    private static HashSet<ulong> Shingle(List<string> tokens)
    {
        var shingles = new HashSet<ulong>();
        int last = tokens.Count - ShingleSize;
        for (int start = 0; start <= last; start++)
        {
            ulong hash = Fnv1aOffsetBasis;
            for (int offset = 0; offset < ShingleSize; offset++)
            {
                hash = Fnv1aString(hash, tokens[start + offset]);
                hash = Fnv1aByte(hash, 0);
            }
            shingles.Add(hash);
        }
        return shingles;
    }

    private static List<string> Normalize(string text)
    {
        var tokens = new List<string>();
        int index = 0;
        while (index < text.Length)
        {
            char current = text[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
            }
            else if (IsWordStart(current))
            {
                int start = index;
                while (index < text.Length && IsWordPart(text[index]))
                    index++;
                string word = text[start..index].ToLowerInvariant();
                tokens.Add(Keywords.Contains(word) ? word : IdentifierToken);
            }
            else if (char.IsAsciiDigit(current))
            {
                while (index < text.Length && IsNumberPart(text[index]))
                    index++;
                tokens.Add(NumberToken);
            }
            else if (current is '"' or '\'' or '`')
            {
                index = SkipQuoted(text, index, current);
                tokens.Add(StringToken);
            }
            else
            {
                tokens.Add(current.ToString(CultureInfo.InvariantCulture));
                index++;
            }
        }
        return tokens;
    }

    private static int SkipQuoted(string text, int openIndex, char quote)
    {
        int index = openIndex + 1;
        while (index < text.Length)
        {
            char current = text[index];
            if (current == '\\')
            {
                index += 2;
                continue;
            }
            index++;
            if (current == quote)
                break;
        }
        return Math.Min(index, text.Length);
    }

    private static bool IsWordStart(char value) => char.IsLetter(value) || value is '_' or '$' or '@' or '#';

    private static bool IsWordPart(char value) => char.IsLetterOrDigit(value) || value is '_' or '$' or '@' or '#';

    private static bool IsNumberPart(char value) => char.IsLetterOrDigit(value) || value is '.' or '_';

    private static ulong[] BuildPermutationSeeds()
    {
        ulong[] seeds = new ulong[SignatureLength];
        ulong state = SeedBase;
        for (int i = 0; i < SignatureLength; i++)
        {
            state = unchecked(state + SeedBase);
            seeds[i] = SplitMix64(state);
        }
        return seeds;
    }

    private static ulong SplitMix64(ulong value)
    {
        unchecked
        {
            ulong z = value + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    private const ulong Fnv1aOffsetBasis = 0xCBF29CE484222325UL;
    private const ulong Fnv1aPrime = 0x100000001B3UL;

    private static ulong Fnv1aByte(ulong hash, byte value) => unchecked((hash ^ value) * Fnv1aPrime);

    private static ulong Fnv1aStep(ulong hash, ulong value)
    {
        unchecked
        {
            for (int shift = 0; shift < 64; shift += 8)
                hash = Fnv1aByte(hash, (byte)(value >> shift));
            return hash;
        }
    }

    private static ulong Fnv1aString(ulong hash, string value)
    {
        unchecked
        {
            foreach (char character in value)
            {
                hash = Fnv1aByte(hash, (byte)character);
                hash = Fnv1aByte(hash, (byte)(character >> 8));
            }
            return hash;
        }
    }

    private readonly record struct Edge(int Left, int Right, double Similarity);

    private sealed record Candidate(string Id, HashSet<ulong> Shingles, ulong[] Signature);
}
