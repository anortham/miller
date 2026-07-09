using System.Text.Json;
using Miller.Server;
using Miller.Server.Cli;
using Miller.Tests.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Miller.Tests.Server.Cli;

/// <summary>
/// Pins the <c>miller impact --from-index-revision N</c> CLI surface — the index-revision delta contract
/// (design 2026-07-03-ct-revision-delta-design.md §1, R0–R4). Asserts the frozen envelope shape Eros parses,
/// R2 truthful exclusion (ignored/tooling paths never appear), R3 honest span failure through the CLI, flag
/// hygiene, and R4 capability advertisement. Uses a real <see cref="JulieDbFixture"/> extract DB carrying the
/// change journal; <see cref="WorkspaceContext"/> is built directly so the test never chdirs.
/// </summary>
public sealed class ImpactRevisionDeltaCliTests : IDisposable
{
    private const string DefaultArtifactId = "artifact-default";

    private readonly string _dir;

    public ImpactRevisionDeltaCliTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "miller-ct-delta-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private WorkspaceContext Context(JulieDbFixture fx) =>
        new(
            WorkspaceRoot: fx.WorkspaceRoot,
            ExtractDbPath: fx.DbPath,
            TelemetryDbPath: Path.Combine(_dir, "telemetry.db"),
            RegistryDbPath: Path.Combine(_dir, "workspaces.db"),
            ToolsRoot: Path.Combine(_dir, ".tools"),
            WorkspaceId: null);

    private static (int Code, string Out, string Err) Run(IReadOnlyList<string> args, WorkspaceContext ctx)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int code = CliDispatch.Run(args, ctx, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static JulieDbFixture.SymbolRow Symbol(string id, string name, string path) =>
        new(id, name, "method", "csharp", path, $"void {name}()", 1, ParentId: null) { EndLine = 3 };

    private JulieDbFixture Build(
        IReadOnlyList<JulieDbFixture.RevisionRow> revisions,
        IReadOnlyList<JulieDbFixture.RevisionFileChangeRow> changes) =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[] { Symbol("00000000000000000000000000000001", "Handle", "src/Service.cs") },
            revisions: revisions,
            fileChanges: changes);

    [Fact]
    public void Delta_Json_EmitsFrozenEnvelope()
    {
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(1), new JulieDbFixture.RevisionRow(2) },
            changes: new[] { new JulieDbFixture.RevisionFileChangeRow(2, "src/Service.cs", "updated") });

        var (code, outText, errText) = Run(
            new[]
            {
                "impact", "--workspace-id", "current", "--json",
                "--from-index-revision", "1", "--from-artifact-id", DefaultArtifactId,
            },
            Context(fx));

        Assert.Equal(0, code);
        Assert.Empty(errText);

        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;

        // Byte-for-byte frozen contract (Eros Task 1 fixtures): field names + types.
        Assert.Equal("current", root.GetProperty("workspace_id").GetString());
        Assert.Equal("complete", root.GetProperty("delta_status").GetString());
        Assert.Equal(DefaultArtifactId, root.GetProperty("artifact_id").GetString());
        Assert.Equal(DefaultArtifactId, root.GetProperty("from_artifact_id").GetString());
        Assert.Equal("complete", root.GetProperty("delta_reason").GetString());
        Assert.Equal(1, root.GetProperty("from_revision").GetInt64());
        Assert.Equal(2, root.GetProperty("to_revision").GetInt64());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("changed_paths").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("impacted").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("tests").ValueKind);

        JsonElement traversal = root.GetProperty("traversal");
        Assert.Equal("exhausted", traversal.GetProperty("status").GetString());
        Assert.Equal("complete", traversal.GetProperty("reason").GetString());
        Assert.Equal(2, traversal.GetProperty("max_depth").GetInt32());
        Assert.Equal(100, traversal.GetProperty("limit").GetInt32());
        Assert.Equal(0, traversal.GetProperty("reached_count").GetInt32());
        Assert.Equal(0, traversal.GetProperty("returned_count").GetInt32());
        Assert.False(traversal.GetProperty("truncated_by_depth").GetBoolean());
        Assert.False(traversal.GetProperty("truncated_by_limit").GetBoolean());
        Assert.Equal(new[] { "src/Service.cs" }, traversal.GetProperty("seeded_paths").EnumerateArray()
            .Select(static item => item.GetString()));
        Assert.Empty(traversal.GetProperty("unseeded_paths").EnumerateArray());

        string[] changed = root.GetProperty("changed_paths").EnumerateArray()
            .Select(e => e.GetString()).ToArray()!;
        Assert.Contains("src/Service.cs", changed);
    }

    [Fact]
    public void Delta_Json_CompleteEmptyDelta_ReportsTraversalNotRunForNoChanges()
    {
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(1) },
            changes: Array.Empty<JulieDbFixture.RevisionFileChangeRow>());

        var (code, outText, errText) = Run(
            new[]
            {
                "impact", "--workspace-id", "current", "--json",
                "--from-index-revision", "1", "--from-artifact-id", DefaultArtifactId,
                "--max-depth", "0", "--limit", "0",
            },
            Context(fx));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement traversal = doc.RootElement.GetProperty("traversal");
        Assert.Equal("not_run", traversal.GetProperty("status").GetString());
        Assert.Equal("no_changes", traversal.GetProperty("reason").GetString());
        Assert.Equal(1, traversal.GetProperty("max_depth").GetInt32());
        Assert.Equal(1, traversal.GetProperty("limit").GetInt32());
    }

    [Fact]
    public void Delta_Json_ExcludesIgnoredPaths_R2()
    {
        // R2 truthful exclusion: a journal row under a tooling dir (.miller) must never reach changed_paths, while
        // the real watched source change does.
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(1), new JulieDbFixture.RevisionRow(2) },
            changes: new[]
            {
                new JulieDbFixture.RevisionFileChangeRow(2, "src/Service.cs", "updated"),
                new JulieDbFixture.RevisionFileChangeRow(2, ".miller/telemetry-cache.cs", "updated"),
                new JulieDbFixture.RevisionFileChangeRow(2, "target/generated/Build.cs", "inserted"),
            });

        var (code, outText, _) = Run(
            new[]
            {
                "impact", "--workspace-id", "current", "--json",
                "--from-index-revision", "1", "--from-artifact-id", DefaultArtifactId,
            },
            Context(fx));

        Assert.Equal(0, code);
        using JsonDocument doc = JsonDocument.Parse(outText);
        string[] changed = doc.RootElement.GetProperty("changed_paths").EnumerateArray()
            .Select(e => e.GetString()).ToArray()!;

        Assert.Contains("src/Service.cs", changed);
        Assert.DoesNotContain(".miller/telemetry-cache.cs", changed);
        Assert.DoesNotContain("target/generated/Build.cs", changed);
    }

    [Fact]
    public void Delta_Json_UnavailableWhenBaseAheadOfCurrent_R3()
    {
        // R3 through the CLI: a base ahead of current (rebuilt-index counter reset) → delta_status unavailable with
        // an empty changed_paths and the real current revision echoed, never a guessed-empty complete delta.
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(1), new JulieDbFixture.RevisionRow(2) },
            changes: new[] { new JulieDbFixture.RevisionFileChangeRow(2, "src/Service.cs", "updated") });

        var (code, outText, _) = Run(
            new[]
            {
                "impact", "--workspace-id", "current", "--json",
                "--from-index-revision", "999", "--from-artifact-id", DefaultArtifactId,
            },
            Context(fx));

        Assert.Equal(0, code);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.Equal("unavailable", root.GetProperty("delta_status").GetString());
        Assert.Equal("from_after_current", root.GetProperty("delta_reason").GetString());
        Assert.Equal(999, root.GetProperty("from_revision").GetInt64());
        Assert.Equal(2, root.GetProperty("to_revision").GetInt64());
        Assert.Empty(root.GetProperty("changed_paths").EnumerateArray());
        Assert.Equal("not_run", root.GetProperty("traversal").GetProperty("status").GetString());
        Assert.Equal("delta_unavailable", root.GetProperty("traversal").GetProperty("reason").GetString());
    }

    [Fact]
    public void Delta_Json_MissingArtifactBase_IsUnavailable_NotComplete()
    {
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(1), new JulieDbFixture.RevisionRow(2) },
            changes: new[] { new JulieDbFixture.RevisionFileChangeRow(2, "src/Service.cs", "updated") });

        var (code, outText, errText) = Run(
            new[] { "impact", "--workspace-id", "current", "--json", "--from-index-revision", "1" },
            Context(fx));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.Equal("unavailable", root.GetProperty("delta_status").GetString());
        Assert.Equal("missing_from_artifact_id", root.GetProperty("delta_reason").GetString());
        Assert.Equal(DefaultArtifactId, root.GetProperty("artifact_id").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("from_artifact_id").ValueKind);
        Assert.Empty(root.GetProperty("changed_paths").EnumerateArray());
    }

    [Fact]
    public void Delta_Json_ArtifactMismatch_IsUnavailableEvenWhenRevisionSpanLooksComplete()
    {
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(51), new JulieDbFixture.RevisionRow(101) },
            changes: new[] { new JulieDbFixture.RevisionFileChangeRow(101, "src/Service.cs", "updated") });

        var (code, outText, errText) = Run(
            new[]
            {
                "impact", "--workspace-id", "current", "--json",
                "--from-index-revision", "50", "--from-artifact-id", "artifact-before-rebuild",
            },
            Context(fx));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement root = doc.RootElement;
        Assert.Equal("unavailable", root.GetProperty("delta_status").GetString());
        Assert.Equal("artifact_changed", root.GetProperty("delta_reason").GetString());
        Assert.Equal(DefaultArtifactId, root.GetProperty("artifact_id").GetString());
        Assert.Equal("artifact-before-rebuild", root.GetProperty("from_artifact_id").GetString());
        Assert.Equal(101, root.GetProperty("to_revision").GetInt64());
        Assert.Empty(root.GetProperty("changed_paths").EnumerateArray());
    }

    [Fact]
    public void Delta_MalformedRevision_IsUsageError()
    {
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(1) },
            changes: Array.Empty<JulieDbFixture.RevisionFileChangeRow>());

        var (code, _, errText) = Run(
            new[] { "impact", "--workspace-id", "current", "--json", "--from-index-revision", "not-a-number" },
            Context(fx));

        Assert.Equal(2, code);
        Assert.Contains("--from-index-revision", errText);
    }

    [Fact]
    public void Delta_NegativeRevision_IsUsageError()
    {
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(1) },
            changes: Array.Empty<JulieDbFixture.RevisionFileChangeRow>());

        var (code, _, errText) = Run(
            new[]
            {
                "impact", "--workspace-id", "current", "--json",
                "--from-index-revision", "-1", "--from-artifact-id", DefaultArtifactId,
            },
            Context(fx));

        Assert.Equal(2, code);
        Assert.Contains("--from-index-revision", errText);
    }

    [Fact]
    public void Delta_RejectsCombiningWithSymbolTarget()
    {
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(1) },
            changes: Array.Empty<JulieDbFixture.RevisionFileChangeRow>());

        var (code, _, errText) = Run(
            new[]
            {
                "impact", "--workspace-id", "current", "--json",
                "--from-index-revision", "1", "--from-artifact-id", DefaultArtifactId, "SomeSymbol",
            },
            Context(fx));

        Assert.Equal(2, code);
        Assert.Contains("--from-index-revision", errText);
    }

    [Fact]
    public void Capabilities_Json_AdvertisesImpactIndexRevisionDeltaFeature_R4()
    {
        using JulieDbFixture fx = Build(
            revisions: new[] { new JulieDbFixture.RevisionRow(1) },
            changes: Array.Empty<JulieDbFixture.RevisionFileChangeRow>());

        var (code, outText, _) = Run(new[] { "capabilities", "--json" }, Context(fx));

        Assert.Equal(0, code);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement features = doc.RootElement.GetProperty("features");
        Assert.Equal(JsonValueKind.Array, features.ValueKind);
        string[] names = features.EnumerateArray().Select(e => e.GetString()).ToArray()!;
        Assert.Contains("impact_index_revision_delta", names);
    }

    [Fact]
    public void NegotiatedFeatures_GatesOnActiveFlag_R4()
    {
        // Advertise-only-when-active: the feature appears iff its flag is set. An inactive build omits it, so an
        // old Miller degrades by negotiation rather than by a failed/legacy-shaped response.
        Assert.Contains(CliCapabilities.ImpactIndexRevisionDeltaFeature, CliCapabilities.NegotiatedFeatures(true));
        Assert.DoesNotContain(CliCapabilities.ImpactIndexRevisionDeltaFeature, CliCapabilities.NegotiatedFeatures(false));
    }
}
