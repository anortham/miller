using System.Text.Json;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Cli;
using Miller.Server.Tools;
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
        JsonElement testEvidenceScope = root.GetProperty("test_evidence_scope");
        Assert.Equal("candidate_only", testEvidenceScope.GetProperty("status").GetString());
        Assert.Equal("unknown", testEvidenceScope.GetProperty("absence").GetString());

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
    public void Delta_McpAndCli_UseByteEquivalentSharedCore()
    {
        using JulieDbFixture fx = Build(
            revisions: [new JulieDbFixture.RevisionRow(1), new JulieDbFixture.RevisionRow(2)],
            changes: [new JulieDbFixture.RevisionFileChangeRow(2, "src/Service.cs", "updated")]);
        var (code, cliOutput, errText) = Run(
            [
                "impact", "--workspace-id", "current", "--json",
                "--from-index-revision", "1", "--from-artifact-id", DefaultArtifactId,
            ],
            Context(fx));
        MillerRepositoryIndex index = RepositoryIndexLoader.Load(fx.DbPath);
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(
                index, fx.DbPath, "current", fx.WorkspaceRoot));
        string mcpOutput = new ImpactTool(provider).Impact(
            from_index_revision: 1,
            from_artifact_id: DefaultArtifactId,
            format: "json");

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument mcpDocument = JsonDocument.Parse(mcpOutput);
        Assert.False(
            mcpDocument.RootElement.TryGetProperty("diagnostic", out JsonElement diagnostic),
            diagnostic.ValueKind == JsonValueKind.Undefined ? string.Empty : diagnostic.GetRawText());
        Assert.Equal(cliOutput.TrimEnd(), mcpOutput);
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
    public void Delta_Json_SeparatesDeletedPathsFromOtherUnseededPaths()
    {
        using JulieDbFixture fx = Build(
            revisions: [new JulieDbFixture.RevisionRow(1), new JulieDbFixture.RevisionRow(2)],
            changes:
            [
                new JulieDbFixture.RevisionFileChangeRow(2, "src/Removed.cs", "deleted"),
                new JulieDbFixture.RevisionFileChangeRow(2, "config/settings.json", "updated"),
            ]);

        var (code, outText, errText) = Run(
            [
                "impact", "--workspace-id", "current", "--json",
                "--from-index-revision", "1", "--from-artifact-id", DefaultArtifactId,
            ],
            Context(fx));

        Assert.Equal(0, code);
        Assert.Empty(errText);
        using JsonDocument doc = JsonDocument.Parse(outText);
        JsonElement traversal = doc.RootElement.GetProperty("traversal");
        Assert.Equal(
            ["src/Removed.cs"],
            traversal.GetProperty("deleted_paths").EnumerateArray().Select(static item => item.GetString()));
        Assert.Equal(
            ["config/settings.json"],
            traversal.GetProperty("unseeded_paths").EnumerateArray().Select(static item => item.GetString()));
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
    public void Capabilities_Json_AdvertisesIndependentImpactContracts()
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
        Assert.Equal(CliCapabilities.ImpactIndexRevisionDeltaActive,
            names.Contains(CliCapabilities.ImpactIndexRevisionDeltaFeature));
        Assert.Equal(CliCapabilities.ImpactTraversalEvidenceActive,
            names.Contains(CliCapabilities.ImpactTraversalEvidenceFeature));
        Assert.Equal(CliCapabilities.ImpactTestRoleEvidenceActive,
            names.Contains(CliCapabilities.ImpactTestRoleEvidenceFeature));

        JsonElement[] contracts = doc.RootElement.GetProperty("json_contracts").EnumerateArray().ToArray();
        JsonElement[] traversalContracts = contracts
            .Where(contract => contract.GetProperty("name").GetString() == "impact_traversal_evidence")
            .ToArray();
        Assert.Equal(CliCapabilities.ImpactTraversalEvidenceActive, traversalContracts.Length == 1);
        JsonElement traversalContract = Assert.Single(traversalContracts);
        Assert.Equal(
            "impact --json --from-index-revision N --from-artifact-id ID",
            traversalContract.GetProperty("command").GetString());
        Assert.Equal(1, traversalContract.GetProperty("schema_version").GetInt32());
        Assert.Equal("docs/contracts/impact-traversal-evidence-v1.md",
            traversalContract.GetProperty("doc").GetString());

        JsonElement[] testRoleContracts = contracts
            .Where(contract => contract.GetProperty("name").GetString() == "impact_test_role_evidence")
            .ToArray();
        Assert.Equal(CliCapabilities.ImpactTestRoleEvidenceActive, testRoleContracts.Length == 1);
        JsonElement testRoleContract = Assert.Single(testRoleContracts);
        Assert.Equal("impact --json", testRoleContract.GetProperty("command").GetString());
        Assert.Equal(1, testRoleContract.GetProperty("schema_version").GetInt32());
        Assert.Equal("docs/contracts/impact-test-role-evidence-v1.md",
            testRoleContract.GetProperty("doc").GetString());
    }

    [Fact]
    public void NegotiatedFeatures_GatesImpactFeaturesIndependently()
    {
        IReadOnlyList<string> deltaOnly = CliCapabilities.NegotiatedFeatures(
            impactIndexRevisionDelta: true, impactTraversalEvidence: false, impactTestRoleEvidence: false);
        Assert.Contains(CliCapabilities.ImpactIndexRevisionDeltaFeature, deltaOnly);
        Assert.DoesNotContain(CliCapabilities.ImpactTraversalEvidenceFeature, deltaOnly);
        Assert.DoesNotContain(CliCapabilities.ImpactTestRoleEvidenceFeature, deltaOnly);

        IReadOnlyList<string> traversalOnly = CliCapabilities.NegotiatedFeatures(
            impactIndexRevisionDelta: false, impactTraversalEvidence: true, impactTestRoleEvidence: false);
        Assert.DoesNotContain(CliCapabilities.ImpactIndexRevisionDeltaFeature, traversalOnly);
        Assert.Contains(CliCapabilities.ImpactTraversalEvidenceFeature, traversalOnly);
        Assert.DoesNotContain(CliCapabilities.ImpactTestRoleEvidenceFeature, traversalOnly);

        IReadOnlyList<string> testRoleOnly = CliCapabilities.NegotiatedFeatures(
            impactIndexRevisionDelta: false, impactTraversalEvidence: false, impactTestRoleEvidence: true);
        Assert.DoesNotContain(CliCapabilities.ImpactIndexRevisionDeltaFeature, testRoleOnly);
        Assert.DoesNotContain(CliCapabilities.ImpactTraversalEvidenceFeature, testRoleOnly);
        Assert.Contains(CliCapabilities.ImpactTestRoleEvidenceFeature, testRoleOnly);

        IReadOnlyList<string> neither = CliCapabilities.NegotiatedFeatures(
            impactIndexRevisionDelta: false, impactTraversalEvidence: false, impactTestRoleEvidence: false);
        Assert.DoesNotContain(CliCapabilities.ImpactIndexRevisionDeltaFeature, neither);
        Assert.DoesNotContain(CliCapabilities.ImpactTraversalEvidenceFeature, neither);
        Assert.DoesNotContain(CliCapabilities.ImpactTestRoleEvidenceFeature, neither);
    }

    [Fact]
    public void NegotiatedJsonContracts_GatesImpactEvidenceContractsIndependently()
    {
        var active = CliCapabilities.NegotiatedJsonContracts(
            impactTraversalEvidence: true, impactTestRoleEvidence: true);
        var neither = CliCapabilities.NegotiatedJsonContracts(
            impactTraversalEvidence: false, impactTestRoleEvidence: false);
        var testRoleOnly = CliCapabilities.NegotiatedJsonContracts(
            impactTraversalEvidence: false, impactTestRoleEvidence: true);

        Assert.Contains(active, contract => contract.Name == "impact_traversal_evidence");
        Assert.Contains(active, contract => contract.Name == "impact_test_role_evidence");
        Assert.DoesNotContain(neither, contract => contract.Name == "impact_traversal_evidence");
        Assert.DoesNotContain(neither, contract => contract.Name == "impact_test_role_evidence");
        Assert.DoesNotContain(testRoleOnly, contract => contract.Name == "impact_traversal_evidence");
        Assert.Contains(testRoleOnly, contract => contract.Name == "impact_test_role_evidence");

        var deltaContract = Assert.Single(neither,
            contract => contract.Name == CliCapabilities.ImpactIndexRevisionDeltaFeature);
        Assert.Equal("impact --json --from-index-revision N --from-artifact-id ID", deltaContract.Command);
        Assert.Equal(1, deltaContract.SchemaVersion);
        Assert.Equal("docs/contracts/impact-index-revision-delta-v1.md", deltaContract.Doc);
    }
}
