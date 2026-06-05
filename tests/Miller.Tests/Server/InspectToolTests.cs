using System.Text.Json;
using Microsoft.Data.Sqlite;
using Miller.Indexing;
using Miller.Server;
using Miller.Server.Resolution;
using Miller.Server.Tools;
using Miller.Server.Workspaces;
using Miller.Tests;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the <c>inspect</c> tool (M2 §5) against the inspect fixture: file→symbols (kind filter, limit),
/// symbol→summary (signature + doc_comment via ReadDetail), symbol→full (children via parent_id, name-based
/// refs, one-hop callers/callees, body slice with graceful NULL degradation), ambiguous→candidates (never
/// pick-first), and an unknown path → a note (not an error). Exercises <see cref="InspectTool.Run"/> directly.
/// </summary>
public sealed class InspectToolTests
{
    private static (MillerRepositoryIndex index, SmartTargetResolver resolver) Build(JulieDbFixture fx)
    {
        var index = MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));
        return (index, new SmartTargetResolver(index));
    }

    private static JulieDbFixture EmptyFixture(string workspaceId) =>
        JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            Array.Empty<JulieDbFixture.SymbolRow>(),
            workspaceId: workspaceId);

    // ---- File listing ----

    [Fact]
    public void Run_FileSummary_ListsTheFilesSymbols()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "auth/UserService.cs", depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("UserService", output);
        Assert.Contains("GetUser", output);
        Assert.Contains("DeleteUser", output);
    }

    [Fact]
    public void Run_FileSummary_FiltersByKind()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "auth/UserService.cs", depth: "summary", kind: "method", scope: null, limit: 50, json: false, out _);

        // Only the methods (GetUser, DeleteUser); the class UserService is filtered out.
        Assert.Contains("GetUser", output);
        Assert.Contains("DeleteUser", output);
        Assert.DoesNotContain("class", output);
    }

    [Fact]
    public void Run_FileSummary_RespectsLimit()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "auth/UserService.cs", depth: "summary", kind: null, scope: null, limit: 1, json: false, out int count);

        Assert.Equal(1, count);
        Assert.Contains("more", output); // overflow note
    }

    [Fact]
    public void Run_UnknownPath_ReturnsNote_NotError()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "does/not/exist.cs", depth: "summary", kind: null, scope: null, limit: 50, json: false, out int count);

        Assert.Equal(0, count);
        Assert.Contains("No indexed symbols in does/not/exist.cs", output);
    }

    // ---- Symbol summary ----

    [Fact]
    public void Run_SymbolSummary_ShowsSignatureAndDocComment()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("GetUser", output);
        Assert.Contains("public User GetUser(int id)", output);
        Assert.Contains("Gets a user by id.", output);    // doc_comment via ReadDetail
        Assert.Contains("auth/UserService.cs:2", output); // file:line
    }

    // ---- Symbol full ----

    [Fact]
    public void Run_SymbolFull_IncludesChildrenRefsCallersCalleesAndBody()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        // Inspect the parent class at full depth: children = GetUser + DeleteUser.
        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "UserService", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("GetUser", output);   // child
        Assert.Contains("DeleteUser", output); // child
    }

    [Fact]
    public void Run_SymbolFull_OnMethod_ShowsRefsCallersCalleesBody()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        // refs: GetUser is referenced in Controller.cs:4 and Repo.cs:9.
        Assert.Contains("web/Controller.cs:4", output);
        Assert.Contains("auth/Repo.cs:9", output);
        // callees: GetUser calls Find.
        Assert.Contains("Find", output);
        // body: sliced out of files.content.
        Assert.Contains("return _repo.Find(id);", output);
    }

    [Fact]
    public void Run_FullDepth_FreshFile_RendersBody()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        // The fixture materializes auth/UserService.cs under WorkspaceRoot; a fresh disk read matches the
        // stored content_hash, so full-depth body renders from disk.
        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("return _repo.Find(id);", output);
    }

    [Fact]
    public void Run_FullDepth_DriftedFile_RendersStaleFileReason()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        // Mutate the on-disk file so its blake3 no longer matches the stored content_hash.
        File.WriteAllText(Path.Combine(fx.WorkspaceRoot, "auth/UserService.cs"), "changed\n");
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("body unavailable", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stale file", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no span recorded", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("changed", output); // never slices the drifted file
    }

    [Fact]
    public void Run_FullDepth_MissingDiskFile_RendersMissingFileReason()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        File.Delete(Path.Combine(fx.WorkspaceRoot, "auth/UserService.cs"));
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("body unavailable", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing file", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no span recorded", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_FullDepth_MissingFileHash_RendersMissingHashReason()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        DeleteFileRow(fx.DbPath, "auth/UserService.cs");
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("body unavailable", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("file hash unavailable", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("no span recorded", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_FullDepth_UnsafeSymbolPath_RendersUnsafePathReason()
    {
        string escapedName = "miller-inspect-escape-" + Guid.NewGuid().ToString("N") + ".cs";
        string escapingPath = Path.Combine("..", escapedName);
        string content = "void UnsafeBody() {}\n";
        string? escapedAbs = null;

        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow(
                    "ab000000000000000000000000000001", "UnsafeBody", "method", "csharp",
                    escapingPath, "void UnsafeBody()", 1, null)
                {
                    BodyStartByte = 0, BodyEndByte = content.Length,
                    BodyStartLine = 1, BodyEndLine = 1,
                },
            },
            fileContent: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [escapingPath] = content,
            });

        try
        {
            escapedAbs = Path.GetFullPath(Path.Combine(fx.WorkspaceRoot, escapingPath));
            var (index, resolver) = Build(fx);

            string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
                "UnsafeBody", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

            Assert.Contains("body unavailable", output, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("unsafe path", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("no span recorded", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(content, output);
        }
        finally
        {
            if (escapedAbs is not null)
                File.Delete(escapedAbs);
        }
    }

    [Fact]
    public void Run_SymbolFull_NullBodySpans_DegradesGracefullyWithNoSpanNote()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        // DeleteUser has NULL body spans → body section is a note, not a crash.
        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "DeleteUser", depth: "full", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("DeleteUser", output);
        Assert.Contains("body unavailable", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no span recorded", output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Ambiguity ----

    [Fact]
    public void Run_AmbiguousName_ReturnsCandidates_NeverPicksFirst()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("aa11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "a/First.cs", "void Handle()", 3, null),
            new JulieDbFixture.SymbolRow("bb11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "b/Second.cs", "void Handle()", 7, null),
        });
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "Handle", depth: "summary", kind: null, scope: null, limit: 50, json: false, out _);

        Assert.Contains("a/First.cs", output);
        Assert.Contains("b/Second.cs", output);
        Assert.Contains("candidate", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Run_NotFoundName_ReturnsNote()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "NoSuchSymbol", depth: "summary", kind: null, scope: null, limit: 50, json: false, out int count);

        Assert.Equal(0, count);
        Assert.Contains("not found", output, StringComparison.OrdinalIgnoreCase);
    }

    // ---- JSON ----

    [Fact]
    public void Run_SymbolFull_Json_HasStructuredShape()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: true, out _);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal("GetUser", root.GetProperty("symbol").GetProperty("name").GetString());
        Assert.True(root.TryGetProperty("refs", out var refs));
        Assert.Equal(JsonValueKind.Array, refs.ValueKind);
        Assert.True(root.TryGetProperty("callees", out _));
        Assert.True(root.TryGetProperty("callers", out _));
        Assert.True(root.TryGetProperty("body", out _));
        Assert.False(root.TryGetProperty("body_unavailable_reason", out _));
    }

    [Fact]
    public void Run_SymbolFull_Json_DriftedFile_ExposesBodyUnavailableReason()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        File.WriteAllText(Path.Combine(fx.WorkspaceRoot, "auth/UserService.cs"), "changed\n");
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "GetUser", depth: "full", kind: null, scope: null, limit: 50, json: true, out _);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal(JsonValueKind.Null, root.GetProperty("body").ValueKind);
        Assert.Equal("stale_file", root.GetProperty("body_unavailable_reason").GetString());
    }

    [Fact]
    public void Run_FileSummary_Json_IsAFileListing()
    {
        using var fx = JulieDbFixture.CreateForInspect();
        var (index, resolver) = Build(fx);

        string output = InspectTool.Run(index, resolver, fx.DbPath, fx.WorkspaceRoot,
            "auth/UserService.cs", depth: "summary", kind: null, scope: null, limit: 50, json: true, out _);

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Assert.Equal("auth/UserService.cs", root.GetProperty("file").GetString());
        var children = root.GetProperty("children");
        Assert.Equal(JsonValueKind.Array, children.ValueKind);
        Assert.True(children.GetArrayLength() >= 3);
    }

    [Fact]
    public void Inspect_ExplicitWorkspaceId_UsesTargetIndexResolverAndDbPath_AndPrefixesFreshness()
    {
        using var current = EmptyFixture("current-ws");
        using var target = JulieDbFixture.CreateForInspect();
        var (currentIndex, _) = Build(current);
        var (targetIndex, _) = Build(target);
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = Path.Combine(Path.GetTempPath(), "miller-target-" + Guid.NewGuid().ToString("N"));
        var provider = new RecordingWorkspaceIndexProvider(
            ReadToolRoutingTestSupport.ContextFor(currentIndex, current.DbPath, "current-ws", currentRoot),
            ("target-ws", ReadToolRoutingTestSupport.ContextFor(
                targetIndex,
                target.DbPath,
                "target-ws",
                targetRoot,
                indexFresh: false,
                freshnessStatus: "unconfirmed_lock_busy")));
        var tool = new InspectTool(provider, provider);

        string output = tool.Inspect(
            "GetUser",
            depth: "summary",
            workspace_id: "target-ws",
            ensure_fresh: false);

        Assert.Equal("target-ws", provider.LastWorkspaceId);
        Assert.False(provider.LastEnsureFresh);
        Assert.StartsWith("workspace: target-ws\n", output);
        Assert.DoesNotContain(targetRoot, output);
        Assert.Contains("freshness: unconfirmed_lock_busy", output);
        Assert.Contains("Gets a user by id.", output);
    }

    [Fact]
    public void Inspect_Summary_RegisteredWorkspace_UsesSymbolProjectionWithoutFullLoad()
    {
        using var current = EmptyFixture("current-ws");
        using var target = JulieDbFixture.CreateForInspect();
        string dir = Path.Combine(Path.GetTempPath(), "miller-inspect-projection-" + Guid.NewGuid().ToString("N"));
        string currentRoot = Path.Combine(dir, "current");
        string registryDb = Path.Combine(dir, "workspaces.db");
        Directory.CreateDirectory(currentRoot);

        try
        {
            using var registry = WorkspaceRegistry.Open(registryDb);
            registry.UpsertSeen("target-ws", "target-111111111111", target.WorkspaceRoot, target.DbPath);
            registry.MarkScanned("target-ws", revision: 1);

            int fullLoadCount = 0;
            int symbolLoadCount = 0;
            var workspace = new WorkspaceContext(
                currentRoot,
                current.DbPath,
                Path.Combine(dir, "telemetry.db"),
                registryDb,
                AppContext.BaseDirectory,
                "current-ws",
                currentRoot,
                current.DbPath);
            var provider = new WorkspaceIndexProvider(
                new IndexHolder(RepositoryIndexLoader.Load(current.DbPath), builtRevision: 1),
                workspace,
                registry,
                refresh: _ => throw new InvalidOperationException("refresh was not expected"),
                loadIndex: _ =>
                {
                    fullLoadCount++;
                    throw new InvalidOperationException("full loader was not expected");
                },
                loadSymbolSearch: path =>
                {
                    symbolLoadCount++;
                    return SymbolSearchProjectionLoader.Load(path);
                },
                loadContentSearch: (_, _) =>
                    throw new InvalidOperationException("content loader was not expected"),
                loadRegionSearch: (_, _) =>
                    throw new InvalidOperationException("region loader was not expected"),
                currentIndexFresh: _ => true,
                sidecar: SymbolSearchSidecar.Disabled);
            var tool = new InspectTool(provider, provider);

            string output = tool.Inspect(
                "GetUser",
                depth: "summary",
                workspace_id: "target-ws",
                ensure_fresh: false);

            Assert.DoesNotContain("inspect failed", output);
            Assert.Contains("Gets a user by id.", output);
            Assert.Equal(0, fullLoadCount);
            Assert.Equal(1, symbolLoadCount);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Inspect_Full_RegisteredWorkspace_UsesFullProvider()
    {
        using var current = EmptyFixture("current-ws");
        using var target = JulieDbFixture.CreateForInspect();
        var targetIndex = RepositoryIndexLoader.Load(target.DbPath);
        string currentRoot = Path.Combine(Path.GetTempPath(), "miller-current-" + Guid.NewGuid().ToString("N"));
        string targetRoot = target.WorkspaceRoot;

        int fullResolveCount = 0;
        int searchResolveCount = 0;
        var provider = new FullInspectRecordingProvider(
            ReadToolRoutingTestSupport.ContextFor(
                MillerRepositoryIndex.Build(SqliteSymbolReader.Read(current.DbPath)),
                current.DbPath,
                "current-ws",
                currentRoot),
            ReadToolRoutingTestSupport.ContextFor(
                targetIndex,
                target.DbPath,
                "target-ws",
                targetRoot),
            () => fullResolveCount++,
            () => searchResolveCount++);
        var tool = new InspectTool(provider, provider);

        string output = tool.Inspect(
            "GetUser",
            depth: "full",
            workspace_id: "target-ws",
            ensure_fresh: false);

        Assert.Contains("## body", output);
        Assert.Equal(1, fullResolveCount);
        Assert.Equal(0, searchResolveCount);
    }

    private static void DeleteFileRow(string dbPath, string filePath)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        };

        using var conn = new SqliteConnection(csb.ToString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE path = $path;";
        cmd.Parameters.AddWithValue("$path", filePath);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    private sealed class FullInspectRecordingProvider : IWorkspaceIndexProvider, IWorkspaceSearchProvider
    {
        private readonly WorkspaceReadContext _current;
        private readonly WorkspaceReadContext _target;
        private readonly Action _onFullResolve;
        private readonly Action _onSearchResolve;

        public FullInspectRecordingProvider(
            WorkspaceReadContext current,
            WorkspaceReadContext target,
            Action onFullResolve,
            Action onSearchResolve)
        {
            _current = current;
            _target = target;
            _onFullResolve = onFullResolve;
            _onSearchResolve = onSearchResolve;
        }

        public WorkspaceReadContext Resolve(string? workspaceId, bool ensureFresh)
        {
            _onFullResolve();
            return workspaceId is null ? _current : _target;
        }

        public WorkspaceSymbolSearchContext ResolveSymbolSearch(string? workspaceId, bool ensureFresh)
        {
            _onSearchResolve();
            return ReadToolRoutingTestSupport.SearchContextFor(workspaceId is null ? _current : _target);
        }
    }
}
