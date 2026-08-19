using Miller.Testing;
using Xunit;

namespace Miller.Tests.Testing.Store.Coverage;

public sealed class CoverageFilesTests : IDisposable
{
    private const string Workspace = "ws:1";
    private const string Identity = "gen-1";

    private readonly string _dir =
        Directory.CreateTempSubdirectory("miller-ct-coverage-files-").FullName;

    private string DbPath => Path.Combine(_dir, CtSchema.DbFileName);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Put_coverage_file_and_span_round_trip_path_hash_and_name_path_keys()
    {
        using var store = new ContinuousTestStore(DbPath);
        var generatedAt = new DateTimeOffset(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);
        store.PutCoverageFile(new CoverageFile(
            Id: "cov:1",
            WorkspaceId: Workspace,
            IndexIdentity: Identity,
            Revision: 12,
            Format: "cobertura",
            Path: "coverage.xml",
            Parser: "cobertura",
            SourceHash: "blake3:file",
            GeneratedAt: generatedAt,
            Metadata: new Dictionary<string, object?> { ["tool"] = "coverlet" }));
        store.PutCoverageSpan(new CoverageSpan(
            Id: "span:1",
            WorkspaceId: Workspace,
            IndexIdentity: Identity,
            Revision: 12,
            CoverageFileId: "cov:1",
            StartLine: 10,
            EndLine: 20,
            Hits: 3,
            FilePath: "src/Foo.cs",
            ContentHash: "blake3:src",
            SymbolName: "Foo.Bar",
            SymbolPath: "src/Foo.cs",
            BranchHits: 1));

        CoverageFile file = store.GetCoverageFile("cov:1")!;
        Assert.Equal(Identity, file.IndexIdentity);
        Assert.Equal(12, file.Revision);
        Assert.Equal("cobertura", file.Format);
        Assert.Equal("coverage.xml", file.Path);
        Assert.Equal("blake3:file", file.SourceHash);
        Assert.Equal(generatedAt, file.GeneratedAt);
        Assert.Equal("coverlet", file.Metadata["tool"]);

        CoverageSpan span = Assert.Single(store.ListCoverageSpans("cov:1"));
        Assert.Equal("src/Foo.cs", span.FilePath);
        Assert.Equal("blake3:src", span.ContentHash);
        Assert.Equal("Foo.Bar", span.SymbolName);
        Assert.Equal("src/Foo.cs", span.SymbolPath);
        Assert.Equal(10, span.StartLine);
        Assert.Equal(20, span.EndLine);
        Assert.Equal(3, span.Hits);
        Assert.Equal(1, span.BranchHits);
        Assert.Equal(Identity, span.IndexIdentity);
        Assert.Equal(12, span.Revision);
    }

    [Fact]
    public void Put_coverage_file_upserts_by_id()
    {
        using var store = new ContinuousTestStore(DbPath);
        store.PutCoverageFile(Coverage("cov:1", "old.xml"));
        store.PutCoverageFile(Coverage("cov:1", "new.xml"));

        Assert.Equal("new.xml", store.GetCoverageFile("cov:1")!.Path);
    }

    [Fact]
    public void Missing_db_coverage_file_reads_return_empty()
    {
        using var store = new ContinuousTestStore(DbPath);

        Assert.Null(store.GetCoverageFile("cov:1"));
        Assert.Empty(store.ListCoverageSpans("cov:1"));
        Assert.False(File.Exists(DbPath));
    }

    private static CoverageFile Coverage(string id, string path) =>
        new(
            Id: id,
            WorkspaceId: Workspace,
            IndexIdentity: Identity,
            Revision: 1,
            Format: "cobertura",
            Path: path,
            Parser: "cobertura",
            SourceHash: "blake3:x");
}
