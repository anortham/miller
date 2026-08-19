namespace Miller.Testing.Parsing;

/// <summary>
/// The Rust provider's load-bearing test-case identity codec. IDs are the ONLY channel the run
/// path receives (<see cref="ContinuousTestProviderRunRequest"/> passes id strings; providers have
/// no store access), so run grouping parses these encoded IDs directly.
///
/// <para>Grammar (crate and target names cannot contain <c>:</c> or <c>/</c>, and the kind token is
/// drawn from a fixed set, so every split below is unambiguous):</para>
/// <list type="bullet">
///   <item>per-test — <c>rust-test:&lt;package&gt;::&lt;kind&gt;/&lt;target&gt;::&lt;libtest path&gt;</c>
///     e.g. <c>rust-test:julie-core::lib/julie-core::index::tests::rebuild_is_idempotent</c></item>
///   <item>whole-target aggregate (harness=false / un-enumerable target) —
///     <c>rust-test:&lt;package&gt;::&lt;kind&gt;/&lt;target&gt;</c></item>
///   <item>doc aggregate — <c>rust-test:&lt;package&gt;::doc</c></item>
/// </list>
///
/// <para>Legacy slice-3 IDs (<c>rust-test:Cargo.toml</c>, <c>rust-test:tests/api.rs</c>) carry no
/// <c>::</c> and therefore never parse — <see cref="TryParse"/> returns false and the run path
/// routes them through its <c>cargo test --workspace</c> legacy fallback.</para>
/// </summary>
public sealed record RustTestCaseId
{
    public const string Prefix = "rust-test:";
    public const string DocKind = "doc";

    private const string Separator = "::";

    private static readonly IReadOnlySet<string> TargetKinds =
        new HashSet<string>(StringComparer.Ordinal) { "lib", "bin", "test", "bench", "example" };

    private RustTestCaseId(string package, string kind, string? targetName, string? testName)
    {
        Package = package;
        Kind = kind;
        TargetName = targetName;
        TestName = testName;
    }

    /// <summary>The workspace member package name (e.g. <c>julie-core</c>).</summary>
    public string Package { get; }

    /// <summary>Target kind token: <c>lib</c>, <c>bin</c>, <c>test</c>, <c>bench</c>, <c>example</c>, or <c>doc</c>.</summary>
    public string Kind { get; }

    /// <summary>The cargo target name; null only for a doc aggregate.</summary>
    public string? TargetName { get; }

    /// <summary>The libtest test path; null for a doc aggregate or a whole-target aggregate.</summary>
    public string? TestName { get; }

    /// <summary>True for the per-package doc-test aggregate.</summary>
    public bool IsDoc => string.Equals(Kind, DocKind, StringComparison.Ordinal);

    /// <summary>True for a whole-target aggregate (harness=false / un-enumerable target).</summary>
    public bool IsWholeTarget => !IsDoc && TestName is null;

    /// <summary>True for a per-test case with an enumerated libtest path.</summary>
    public bool IsPerTest => !IsDoc && TestName is not null;

    /// <summary>Encodes a per-test case.</summary>
    public static RustTestCaseId ForTest(string package, string kind, string targetName, string testName)
    {
        ValidateTarget(package, kind, targetName);
        if (string.IsNullOrEmpty(testName))
            throw new ArgumentException("must not be empty", nameof(testName));
        return new RustTestCaseId(package, kind, targetName, testName);
    }

    /// <summary>Encodes a whole-target aggregate case.</summary>
    public static RustTestCaseId ForWholeTarget(string package, string kind, string targetName)
    {
        ValidateTarget(package, kind, targetName);
        return new RustTestCaseId(package, kind, targetName, testName: null);
    }

    /// <summary>Encodes a per-package doc-test aggregate case.</summary>
    public static RustTestCaseId ForDoc(string package)
    {
        if (string.IsNullOrEmpty(package))
            throw new ArgumentException("must not be empty", nameof(package));
        return new RustTestCaseId(package, DocKind, targetName: null, testName: null);
    }

    /// <summary>The full <c>rust-test:</c> id string.</summary>
    public string Encode()
    {
        if (IsDoc)
            return $"{Prefix}{Package}{Separator}{DocKind}";

        var target = $"{Kind}/{TargetName}";
        return TestName is null
            ? $"{Prefix}{Package}{Separator}{target}"
            : $"{Prefix}{Package}{Separator}{target}{Separator}{TestName}";
    }

    public override string ToString() => Encode();

    /// <summary>
    /// The cargo target selector arguments for this case's target: <c>--lib</c>, <c>--bin n</c>,
    /// <c>--test n</c>, <c>--bench n</c>, <c>--example n</c>, or <c>--doc</c>.
    /// </summary>
    public IReadOnlyList<string> SelectorArgs() => Kind switch
    {
        "lib" => ["--lib"],
        DocKind => ["--doc"],
        _ => [$"--{Kind}", TargetName!],
    };

    /// <summary>
    /// The (package, kind, target) grouping key that maps a set of case IDs onto one cargo
    /// invocation. Doc aggregates group per package under the <c>doc</c> kind.
    /// </summary>
    public (string Package, string Kind, string? TargetName) GroupKey() => (Package, Kind, TargetName);

    /// <summary>
    /// Parses a <c>rust-test:</c> id into its (package, kind, target, test) parts, or returns false
    /// for a legacy / non-conforming id (which the run path routes to the workspace fallback).
    /// </summary>
    public static bool TryParse(string? id, out RustTestCaseId parsed)
    {
        parsed = null!;
        if (string.IsNullOrEmpty(id) || !id.StartsWith(Prefix, StringComparison.Ordinal))
            return false;

        var body = id[Prefix.Length..];

        var pkgEnd = body.IndexOf(Separator, StringComparison.Ordinal);
        if (pkgEnd <= 0)
            return false;

        var package = body[..pkgEnd];
        var rest = body[(pkgEnd + Separator.Length)..];
        if (rest.Length == 0)
            return false;

        if (string.Equals(rest, DocKind, StringComparison.Ordinal))
        {
            parsed = ForDoc(package);
            return true;
        }

        var targetEnd = rest.IndexOf(Separator, StringComparison.Ordinal);
        string targetSpec;
        string? testName;
        if (targetEnd < 0)
        {
            targetSpec = rest;
            testName = null;
        }
        else
        {
            targetSpec = rest[..targetEnd];
            testName = rest[(targetEnd + Separator.Length)..];
            if (testName.Length == 0)
                return false;
        }

        var slash = targetSpec.IndexOf('/');
        if (slash <= 0 || slash == targetSpec.Length - 1)
            return false;

        var kind = targetSpec[..slash];
        var targetName = targetSpec[(slash + 1)..];
        if (!TargetKinds.Contains(kind))
            return false;

        parsed = testName is null
            ? ForWholeTarget(package, kind, targetName)
            : ForTest(package, kind, targetName, testName);
        return true;
    }

    private static void ValidateTarget(string package, string kind, string targetName)
    {
        if (string.IsNullOrEmpty(package))
            throw new ArgumentException("must not be empty", nameof(package));
        if (!TargetKinds.Contains(kind))
            throw new ArgumentException($"unsupported target kind '{kind}'", nameof(kind));
        if (string.IsNullOrEmpty(targetName))
            throw new ArgumentException("must not be empty", nameof(targetName));
    }
}
