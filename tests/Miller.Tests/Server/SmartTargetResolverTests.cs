using Miller.Indexing;
using Miller.Server.Resolution;
using Miller.Tests.Indexing;
using Xunit;

namespace Miller.Tests.Server;

/// <summary>
/// Pins the smart-string target resolution (miller-toolbox.md L47-56): a single string is inferred as a FILE
/// path, an opaque SYMBOL ID, or a SYMBOL NAME (with 0/1/&gt;1 disambiguation), plus the <c>scope</c> and
/// <c>as</c> overrides. Driven against the M1 synthesized fixture index. The resolver does no I/O beyond the
/// in-memory index, so this lives in the default (fast) suite.
/// </summary>
public sealed class SmartTargetResolverTests
{
    private static MillerRepositoryIndex BuildIndex(JulieDbFixture fx) =>
        MillerRepositoryIndex.Build(SqliteSymbolReader.Read(fx.DbPath));

    // ---- FILE detection (rules 1/3/4: separators, indexed path, indexed extension — decision-4) ----

    [Theory]
    [InlineData("auth/UserService.cs")]   // exact indexed path with '/'
    [InlineData("core/math.rs")]
    public void Resolve_SlashPath_ResolvesToThatFile(string target)
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var file = Assert.IsType<TargetResolution.File>(resolver.Resolve(target));
        Assert.Equal(target, file.Path);
    }

    [Theory]
    // A bare basename that uniquely names an indexed file canonicalizes to its full indexed path.
    [InlineData("Server.go", "http/Server.go")]
    [InlineData("token.ts", "auth/token.ts")]
    [InlineData("strings.py", "util/strings.py")]
    public void Resolve_BareBasename_ResolvesToCanonicalIndexedPath(string target, string expectedPath)
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var file = Assert.IsType<TargetResolution.File>(resolver.Resolve(target));
        Assert.Equal(expectedPath, file.Path);
    }

    [Fact]
    public void Resolve_FileDetection_IsCrossLanguage_NotAHardcodedExtensionList()
    {
        // decision-4: a target is a file because julie INDEXED that extension here — not because the extension
        // is on a hand-picked allowlist. Seed languages the old 22-entry whitelist omitted (.vue, .ex, .zig)
        // and assert they resolve as files purely from the indexed data.
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("a0112233445566778899aabbccddee01", "App", "class", "vue",
                "ui/App.vue", "App", 1, null),
            new JulieDbFixture.SymbolRow("b0112233445566778899aabbccddee02", "Worker", "module", "elixir",
                "lib/worker.ex", "Worker", 1, null),
            new JulieDbFixture.SymbolRow("c0112233445566778899aabbccddee03", "main", "function", "zig",
                "src/main.zig", "fn main()", 1, null),
        });
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        // Bare basename of an indexed .vue / .zig file → file (canonicalized).
        Assert.Equal("ui/App.vue", Assert.IsType<TargetResolution.File>(resolver.Resolve("App.vue")).Path);
        // A NOT-indexed path but with an extension julie did emit here (.ex) → still classified file (rule 4).
        Assert.Equal("other/helper.ex",
            Assert.IsType<TargetResolution.File>(resolver.Resolve("other/helper.ex")).Path);
    }

    [Fact]
    public void Resolve_DottedName_WithUnindexedExtension_FallsThroughToNameLookup()
    {
        // "Math.PI" looks file-ish but ".PI" was never indexed → it is NOT a file; rule 5 name lookup runs
        // (and finds nothing here) — proving the extension check is index-derived, not "anything with a dot".
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        Assert.IsType<TargetResolution.NotFound>(resolver.Resolve("Math.PI"));
    }

    // ---- ID-shape detection (rule 2: 32-hex MD5 | contains '::' | starts file_) ----

    [Fact]
    public void Resolve_32HexId_ResolvesToThatSymbolDirectly()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        // GetUser's opaque id from the fixture.
        var result = resolver.Resolve("b2c3d4e5f6001122334455667788990a");

        var sym = Assert.IsType<TargetResolution.Symbol>(result);
        Assert.Equal("GetUser", sym.Value.Name);
        Assert.Equal("b2c3d4e5f6001122334455667788990a", sym.Value.SymbolId);
    }

    [Fact]
    public void Resolve_32HexId_NotInIndex_IsNotFound()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        // Well-formed id shape but not present → NotFound (no fallback to a name search of a hex string).
        var result = resolver.Resolve("ffffffffffffffffffffffffffffffff");

        Assert.IsType<TargetResolution.NotFound>(result);
    }

    [Theory]
    [InlineData("Namespace::Type")]
    [InlineData("file_abc123")]
    public void Resolve_IdShape_ButAbsent_IsNotFound(string target)
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        // '::' and 'file_' are id shapes; absent in the fixture → NotFound, not a name lookup.
        Assert.IsType<TargetResolution.NotFound>(resolver.Resolve(target));
    }

    // ---- NAME lookup (rule 3) ----

    [Fact]
    public void Resolve_UniqueName_ResolvesToThatSymbol()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var result = resolver.Resolve("parseToken");

        var sym = Assert.IsType<TargetResolution.Symbol>(result);
        Assert.Equal("parseToken", sym.Value.Name);
        Assert.Equal("auth/token.ts", sym.Value.FilePath);
    }

    [Fact]
    public void Resolve_UnknownName_IsNotFound()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        Assert.IsType<TargetResolution.NotFound>(resolver.Resolve("NoSuchSymbolAnywhere"));
    }

    [Fact]
    public void Resolve_AmbiguousName_ReturnsAllCandidates()
    {
        // Two distinct symbols share the name "Handle" across two files.
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("aa11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "a/First.cs", "void Handle()", 3, null),
            new JulieDbFixture.SymbolRow("bb11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "b/Second.cs", "void Handle()", 7, null),
        });
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var result = resolver.Resolve("Handle");

        var cands = Assert.IsType<TargetResolution.Candidates>(result);
        Assert.Equal(2, cands.Matches.Count);
        Assert.Contains(cands.Matches, c => c.FilePath == "a/First.cs");
        Assert.Contains(cands.Matches, c => c.FilePath == "b/Second.cs");
    }

    [Fact]
    public void Resolve_AmbiguousName_PrefersProductionDefinitionOverTestDefinition()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("aa11223344556677889900aabbccddee", "Flask", "class", "python",
                "src/flask/app.py", "class Flask(App):", 112, null),
            new JulieDbFixture.SymbolRow("bb11223344556677889900aabbccddee", "Flask", "class", "python",
                "tests/test_basic.py", "class Flask:", 7, null),
            new JulieDbFixture.SymbolRow("cc11223344556677889900aabbccddee", "Flask", "class", "python",
                "examples/tutorial/flask_app.py", "class Flask:", 4, null),
        });
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var result = resolver.Resolve("Flask");

        var sym = Assert.IsType<TargetResolution.Symbol>(result);
        Assert.Equal("src/flask/app.py", sym.Value.FilePath);
    }

    [Theory]
    [InlineData("node_modules/pkg/src/Button.ts", "packages/web/src/Button.ts")]
    [InlineData("vendor/pkg/src/Button.go", "cmd/web/src/Button.go")]
    [InlineData("coverage/src/Button.ts", "packages/web/src/Button.ts")]
    public void Resolve_AmbiguousName_PrefersSourceOverDependencyOrCoverageNoise(
        string noisyPath,
        string sourcePath)
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("aa11223344556677889900aabbccddee", "Button", "class", "typescript",
                noisyPath, "class Button {}", 3, null),
            new JulieDbFixture.SymbolRow("bb11223344556677889900aabbccddee", "Button", "class", "typescript",
                sourcePath, "class Button {}", 7, null),
        });
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var result = resolver.Resolve("Button");

        var sym = Assert.IsType<TargetResolution.Symbol>(result);
        Assert.Equal(sourcePath, sym.Value.FilePath);
    }

    [Fact]
    public void Resolve_AmbiguousName_TiedProductionDefinitionsRemainCandidates()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("aa11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "src/First.cs", "void Handle()", 3, null),
            new JulieDbFixture.SymbolRow("bb11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "src/Second.cs", "void Handle()", 7, null),
        });
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var result = resolver.Resolve("Handle");

        var cands = Assert.IsType<TargetResolution.Candidates>(result);
        Assert.Equal(2, cands.Matches.Count);
    }

    [Fact]
    public void Resolve_NameWithSingleTypeAndConstructors_ResolvesToType()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("aa11223344556677889900aabbccddee", "SearchTool", "class", "csharp",
                "src/Miller.Server/Tools/SearchTool.cs", "public sealed class SearchTool", 54, null),
            new JulieDbFixture.SymbolRow("bb11223344556677889900aabbccddee", "SearchTool", "constructor", "csharp",
                "src/Miller.Server/Tools/SearchTool.cs", "public SearchTool(IWorkspaceSearchProvider workspaceProvider)",
                74, "aa11223344556677889900aabbccddee"),
            new JulieDbFixture.SymbolRow("cc11223344556677889900aabbccddee", "SearchTool", "constructor", "csharp",
                "src/Miller.Server/Tools/SearchTool.cs",
                "public SearchTool(IWorkspaceSearchProvider workspaceProvider, IWorkspaceRegionSearchProvider regionProvider)",
                90, "aa11223344556677889900aabbccddee"),
        });
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var result = resolver.Resolve("SearchTool");

        var sym = Assert.IsType<TargetResolution.Symbol>(result);
        Assert.Equal("class", sym.Value.Kind);
        Assert.Equal("src/Miller.Server/Tools/SearchTool.cs", sym.Value.FilePath);
    }

    [Fact]
    public void Resolve_NameWithSingleDefinitionAndImports_ResolvesToDefinition()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("aa11223344556677889900aabbccddee", "RegistryPaths", "struct", "rust",
                "crates/julie-core/src/paths.rs", "pub struct RegistryPaths", 243, null),
            new JulieDbFixture.SymbolRow("bb11223344556677889900aabbccddee", "RegistryPaths", "import", "rust",
                "src/dashboard/standalone.rs", "use crate::paths::RegistryPaths;", 10, null),
            new JulieDbFixture.SymbolRow("cc11223344556677889900aabbccddee", "RegistryPaths", "import", "rust",
                "src/registry/shutdown.rs", "use crate::paths::RegistryPaths;", 18, null),
        });
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var result = resolver.Resolve("RegistryPaths");

        var sym = Assert.IsType<TargetResolution.Symbol>(result);
        Assert.Equal("struct", sym.Value.Kind);
        Assert.Equal("crates/julie-core/src/paths.rs", sym.Value.FilePath);
    }

    // ---- overrides ----

    [Fact]
    public void Resolve_Scope_DisambiguatesAmbiguousNameToOneFile()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("aa11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "a/First.cs", "void Handle()", 3, null),
            new JulieDbFixture.SymbolRow("bb11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "b/Second.cs", "void Handle()", 7, null),
        });
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var result = resolver.Resolve("Handle", scope: "b/Second.cs");

        var sym = Assert.IsType<TargetResolution.Symbol>(result);
        Assert.Equal("b/Second.cs", sym.Value.FilePath);
    }

    [Fact]
    public void Resolve_BackslashScope_NormalizesSeparatorsAndDisambiguates()
    {
        // Windows dogfood gap: a backslash-separated scope must match the verbatim '/'-separated indexed
        // path instead of silently filtering every match to zero.
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("aa11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "a/First.cs", "void Handle()", 3, null),
            new JulieDbFixture.SymbolRow("bb11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "b/Second.cs", "void Handle()", 7, null),
        });
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var result = resolver.Resolve("Handle", scope: @"b\Second.cs");

        var sym = Assert.IsType<TargetResolution.Symbol>(result);
        Assert.Equal("b/Second.cs", sym.Value.FilePath);
    }

    [Fact]
    public void Resolve_ScopeCasingMismatch_StillDisambiguates()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("aa11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "a/First.cs", "void Handle()", 3, null),
            new JulieDbFixture.SymbolRow("bb11223344556677889900aabbccddee", "Handle", "method", "csharp",
                "b/Second.cs", "void Handle()", 7, null),
        });
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var result = resolver.Resolve("Handle", scope: "B/SECOND.CS");

        var sym = Assert.IsType<TargetResolution.Symbol>(result);
        Assert.Equal("b/Second.cs", sym.Value.FilePath);
    }

    [Fact]
    public void Resolve_WrongFileScope_SurfacesOutOfScopeMatches_NotBareNotFound()
    {
        // A wrong scope must never mask a resolvable name: the unscoped matches come back as suggestions
        // with the missed scope recorded, so an agent can self-correct in one turn.
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var result = resolver.Resolve("parseToken", scope: "wrong/Other.cs");

        var nf = Assert.IsType<TargetResolution.NotFound>(result);
        Assert.Equal("wrong/Other.cs", nf.ScopeMissed);
        var suggestion = Assert.Single(nf.Suggestions!);
        Assert.Equal("parseToken", suggestion.Name);
        Assert.Equal("auth/token.ts", suggestion.FilePath);
        Assert.Contains("auth/token.ts", nf.RenderMessage());
    }

    [Fact]
    public void Resolve_MisspelledName_SuggestsNearMissNames()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        // Truncated typo of "parseToken" — close enough that the agent gets a one-turn correction.
        var nf = Assert.IsType<TargetResolution.NotFound>(resolver.Resolve("parseToke"));

        Assert.Null(nf.ScopeMissed);
        Assert.NotNull(nf.Suggestions);
        Assert.Contains(nf.Suggestions!, s => s.Name == "parseToken");
        string message = nf.RenderMessage();
        Assert.Contains("Closest:", message);
        Assert.Contains("parseToken", message);
    }

    [Theory]
    [InlineData("parseTokenn")] // one extra trailing character
    [InlineData("parseTkoen")]  // adjacent transposition
    public void Resolve_OneEditTypo_SuggestsNearMissName(string query)
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var nf = Assert.IsType<TargetResolution.NotFound>(resolver.Resolve(query));

        Assert.NotNull(nf.Suggestions);
        Assert.Contains(nf.Suggestions!, s => s.Name == "parseToken");
    }

    [Fact]
    public void Resolve_CamelCaseSuffixTypo_SuggestsNearMissName()
    {
        using var fx = JulieDbFixture.Create(
            JulieDbFixture.PinnedSchema,
            JulieDbFixture.PinnedContract,
            new[]
            {
                new JulieDbFixture.SymbolRow(
                    "aa11223344556677889900aabbccdd91",
                    "ReadReferencesAsync",
                    "method",
                    "csharp",
                    "src/References/Reader.cs",
                    "Task ReadReferencesAsync()",
                    12,
                    null),
            });
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var nf = Assert.IsType<TargetResolution.NotFound>(resolver.Resolve("ReadReferencesAsyncc"));

        Assert.NotNull(nf.Suggestions);
        Assert.Contains(nf.Suggestions!, s => s.Name == "ReadReferencesAsync");
    }

    [Fact]
    public void Resolve_WrongCaseName_SuggestsCaseInsensitiveExactFirst()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var nf = Assert.IsType<TargetResolution.NotFound>(resolver.Resolve("userservice"));

        Assert.NotNull(nf.Suggestions);
        Assert.Equal("UserService", nf.Suggestions![0].Name);
    }

    [Fact]
    public void Resolve_NearMisses_AreCappedAtThree()
    {
        var rows = new List<JulieDbFixture.SymbolRow>();
        for (int i = 0; i < 6; i++)
            rows.Add(new JulieDbFixture.SymbolRow($"aa11223344556677889900aabbccdd0{i}", $"RunJob{i}", "method",
                "csharp", $"f{i}/Jobs.cs", $"void RunJob{i}()", 3 + i, null));
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, rows);
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var nf = Assert.IsType<TargetResolution.NotFound>(resolver.Resolve("RunJob"));

        Assert.NotNull(nf.Suggestions);
        Assert.Equal(3, nf.Suggestions!.Count);
    }

    [Fact]
    public void Resolve_UnknownName_WithNoCloseNames_KeepsBareNotFoundMessage()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var nf = Assert.IsType<TargetResolution.NotFound>(resolver.Resolve("Zebraphone"));

        Assert.True(nf.Suggestions is null or { Count: 0 });
        Assert.Equal("'Zebraphone' not found. Try search to locate it.", nf.RenderMessage());
    }

    [Fact]
    public void Resolve_ClassQualifiedMember_ResolvesThroughParentNameAndScope()
    {
        using var fx = JulieDbFixture.Create(JulieDbFixture.PinnedSchema, JulieDbFixture.PinnedContract, new[]
        {
            new JulieDbFixture.SymbolRow("aa11223344556677889900aabbccddee", "WorkspaceTool", "class", "csharp",
                "src/Miller.Server/Tools/WorkspaceTool.cs", "public sealed class WorkspaceTool", 31, null),
            new JulieDbFixture.SymbolRow("bb11223344556677889900aabbccddee", "ResolveTarget", "method", "csharp",
                "src/Miller.Server/Tools/WorkspaceTool.cs", "private TargetWorkspace ResolveTarget(string? workspaceId, string? path)", 592,
                "aa11223344556677889900aabbccddee"),
            new JulieDbFixture.SymbolRow("cc11223344556677889900aabbccddee", "ResolveTarget", "method", "csharp",
                "src/Other/WorkspaceTool.cs", "private TargetWorkspace ResolveTarget()", 12, null),
        });
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var result = resolver.Resolve(
            "WorkspaceTool.ResolveTarget",
            scope: "src/Miller.Server/Tools/WorkspaceTool.cs");

        var sym = Assert.IsType<TargetResolution.Symbol>(result);
        Assert.Equal("ResolveTarget", sym.Value.Name);
        Assert.Equal("src/Miller.Server/Tools/WorkspaceTool.cs", sym.Value.FilePath);
    }

    [Fact]
    public void Resolve_AsFile_ForcesNameLikeStringToFile()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        // "UserService" has no path markers; as=file forces FILE interpretation.
        var result = resolver.Resolve("UserService", asKind: TargetKind.File);

        var file = Assert.IsType<TargetResolution.File>(result);
        Assert.Equal("UserService", file.Path);
    }

    [Fact]
    public void Resolve_AsSymbol_ForcesPathLikeStringToNameLookup()
    {
        // A symbol literally named "auth/token.ts" would never exist, but as=symbol must skip FILE inference
        // and do a NAME lookup (here producing NotFound, proving the path heuristic was bypassed).
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        var result = resolver.Resolve("auth/token.ts", asKind: TargetKind.Symbol);

        Assert.IsType<TargetResolution.NotFound>(result);
    }

    [Fact]
    public void Resolve_NullOrWhitespace_Throws()
    {
        using var fx = JulieDbFixture.CreateDefault();
        var index = BuildIndex(fx);
        var resolver = new SmartTargetResolver(index);

        Assert.Throws<ArgumentException>(() => resolver.Resolve("  "));
    }
}
