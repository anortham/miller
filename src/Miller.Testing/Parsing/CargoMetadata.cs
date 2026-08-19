using System.Text.Json;

namespace Miller.Testing.Parsing;

/// <summary>
/// A cargo target as reported by <c>cargo metadata --format-version 1</c>: its name, raw kind list,
/// and the <c>test</c>/<c>doctest</c> booleans. Discovery keys off those booleans, NOT the kind
/// list — kind filtering silently skips proc-macro and rlib/cdylib lib-variant targets whose unit
/// tests plain <c>cargo test</c> still runs (verified against real cargo 1.96.0 metadata).
/// </summary>
public sealed record CargoTarget(
    string Name,
    IReadOnlyList<string> Kinds,
    bool IsTest,
    bool IsDoctest)
{
    private static readonly IReadOnlySet<string> LibKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "lib", "rlib", "dylib", "cdylib", "staticlib", "proc-macro",
    };

    /// <summary>
    /// The provider's target-kind token used in the case ID and the cargo selector: <c>lib</c>,
    /// <c>bin</c>, <c>test</c>, <c>bench</c>, or <c>example</c>. Null for a non-runnable kind
    /// (e.g. <c>custom-build</c>), which is never a test target.
    /// </summary>
    public string? SelectorKind
    {
        get
        {
            if (Kinds.Any(LibKinds.Contains))
                return "lib";
            foreach (var kind in Kinds)
            {
                if (string.Equals(kind, "bin", StringComparison.Ordinal)
                    || string.Equals(kind, "test", StringComparison.Ordinal)
                    || string.Equals(kind, "bench", StringComparison.Ordinal)
                    || string.Equals(kind, "example", StringComparison.Ordinal))
                    return kind;
            }

            return null;
        }
    }

    /// <summary>True when a plain <c>cargo test</c> would build and run this target's libtest binary.</summary>
    public bool IsTestCapable => IsTest && SelectorKind is not null;

    /// <summary>
    /// The cargo target-selector arguments for this target: <c>--lib</c>, <c>--bin n</c>,
    /// <c>--test n</c>, <c>--bench n</c>, or <c>--example n</c>. Empty for a non-runnable kind.
    /// </summary>
    public IReadOnlyList<string> SelectorArgs() => SelectorKind switch
    {
        null => [],
        "lib" => ["--lib"],
        _ => [$"--{SelectorKind}", Name],
    };
}

/// <summary>A workspace member package: name, id, manifest path, and its targets.</summary>
public sealed record CargoPackage(
    string Name,
    string Id,
    string ManifestPath,
    IReadOnlyList<CargoTarget> Targets)
{
    /// <summary>The package root directory (the directory containing <c>Cargo.toml</c>).</summary>
    public string PackageRoot => Path.GetDirectoryName(ManifestPath) ?? ManifestPath;

    /// <summary>Targets a plain <c>cargo test</c> would build and run, in metadata order.</summary>
    public IEnumerable<CargoTarget> TestCapableTargets => Targets.Where(t => t.IsTestCapable);

    /// <summary>True when the package's library exposes doc-tests (<c>doctest: true</c>).</summary>
    public bool HasDoctests => Targets.Any(t => t.IsDoctest);
}

/// <summary>
/// Parsed <c>cargo metadata --no-deps --format-version 1</c>: the workspace member packages only
/// (<c>workspace.exclude</c> honored by construction, since excluded manifests never appear in
/// <c>workspace_members</c>).
/// </summary>
public sealed class CargoMetadata
{
    private CargoMetadata(IReadOnlyList<CargoPackage> workspaceMembers) =>
        WorkspaceMembers = workspaceMembers;

    /// <summary>The workspace member packages, in metadata order.</summary>
    public IReadOnlyList<CargoPackage> WorkspaceMembers { get; }

    /// <summary>
    /// Parses metadata JSON, keeping only packages whose id appears in <c>workspace_members</c>.
    /// On cargo ≥ 1.77 the member ids are the stable <c>path+file:///abs#version</c> form, which
    /// equals the package's own <c>id</c>; the match is exact-string.
    /// </summary>
    public static CargoMetadata Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ContinuousTestProviderException("cargo metadata produced no output.");

        using var document = ParseDocument(json);
        var root = document.RootElement;

        var memberIds = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("workspace_members", out var members) && members.ValueKind == JsonValueKind.Array)
        {
            foreach (var member in members.EnumerateArray())
            {
                if (member.ValueKind == JsonValueKind.String)
                    memberIds.Add(member.GetString()!);
            }
        }

        var packages = new List<CargoPackage>();
        if (root.TryGetProperty("packages", out var packageArray) && packageArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var package in packageArray.EnumerateArray())
            {
                var id = StringProp(package, "id");
                if (id is null || !memberIds.Contains(id))
                    continue;

                var name = StringProp(package, "name")
                    ?? throw new ContinuousTestProviderException($"cargo metadata package '{id}' has no name.");
                var manifestPath = StringProp(package, "manifest_path")
                    ?? throw new ContinuousTestProviderException($"cargo metadata package '{name}' has no manifest_path.");
                manifestPath = NormalizeCargoManifestPath(manifestPath);

                packages.Add(new CargoPackage(name, id, manifestPath, ParseTargets(package)));
            }
        }

        return new CargoMetadata(packages);
    }

    private static IReadOnlyList<CargoTarget> ParseTargets(JsonElement package)
    {
        var targets = new List<CargoTarget>();
        if (!package.TryGetProperty("targets", out var targetArray) || targetArray.ValueKind != JsonValueKind.Array)
            return targets;

        foreach (var target in targetArray.EnumerateArray())
        {
            var name = StringProp(target, "name");
            if (name is null)
                continue;

            var kinds = new List<string>();
            if (target.TryGetProperty("kind", out var kindArray) && kindArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var kind in kindArray.EnumerateArray())
                {
                    if (kind.ValueKind == JsonValueKind.String)
                        kinds.Add(kind.GetString()!);
                }
            }

            targets.Add(new CargoTarget(name, kinds, BoolProp(target, "test"), BoolProp(target, "doctest")));
        }

        return targets;
    }

    private static JsonDocument ParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new ContinuousTestProviderException($"cargo metadata output was not valid JSON: {ex.Message}", ex);
        }
    }

    private static string? StringProp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool BoolProp(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Cargo metadata JSON uses forward slashes for <c>manifest_path</c> even on Windows; normalize to
    /// backslashes so paths match <see cref="Path.Combine"/> and other OS-native consumers.
    /// Unix-style fixture paths without a drive letter are left unchanged.
    /// </summary>
    private static string NormalizeCargoManifestPath(string manifestPath)
    {
        if (!OperatingSystem.IsWindows())
            return manifestPath;

        if (manifestPath.Length >= 2
            && char.IsAsciiLetter(manifestPath[0])
            && manifestPath[1] == ':')
            return manifestPath.Replace('/', '\\');

        return manifestPath;
    }
}
