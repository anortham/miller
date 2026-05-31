using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the M3 additions to <see cref="ExtractReport"/> (verified-fact 7): <c>revision</c>, <c>files_updated</c>,
/// <c>files_deleted</c>, <c>workspace_id</c> — the fields the freshness path branches on. Each outcome the
/// runner must distinguish (<c>changed</c> / <c>unchanged</c> / <c>deleted</c> / <c>not_found</c> / <c>failed</c>)
/// is parsed from a representative report and asserted on the new fields, NOT just "does it deserialize".
/// </summary>
public sealed class ExtractReportParsingTests
{
    // `extract update --file` of a CHANGED file → status=changed, files_updated=1, revision bumps (verified-fact 2).
    private const string ChangedJson = """
        {
          "status": "changed", "operation": "update",
          "workspace_id": "ws-abc-123",
          "db_path": "/abs/.miller/symbols.db", "root": "/abs/repo",
          "schema_version": 28, "schema_state": "current", "extract_contract_version": 2,
          "revision": 7, "analyzed_revision": 6, "analysis_state": "stale",
          "files_scanned": 0, "files_updated": 1, "files_deleted": 0,
          "symbols_extracted": 5, "files_total": 12, "symbols_total": 134,
          "relationships_total": 40, "identifiers_total": 512, "types_total": 9,
          "errors": []
        }
        """;

    // A NO-OP update (content hash unchanged) → status=unchanged, files_updated=0, revision does NOT bump.
    private const string UnchangedJson = """
        {
          "status": "unchanged", "operation": "update",
          "workspace_id": "ws-abc-123",
          "db_path": "/abs/.miller/symbols.db", "root": "/abs/repo",
          "schema_version": 28, "extract_contract_version": 2,
          "revision": 6, "files_scanned": 0, "files_updated": 0, "files_deleted": 0,
          "symbols_extracted": 0, "files_total": 12, "symbols_total": 134,
          "relationships_total": 40, "identifiers_total": 512, "types_total": 9,
          "errors": []
        }
        """;

    // `extract delete --file` of a removed file → status=deleted, files_deleted=1, revision bumps.
    private const string DeletedJson = """
        {
          "status": "deleted", "operation": "delete",
          "workspace_id": "ws-abc-123",
          "db_path": "/abs/.miller/symbols.db", "root": "/abs/repo",
          "schema_version": 28, "extract_contract_version": 2,
          "revision": 8, "files_scanned": 0, "files_updated": 0, "files_deleted": 1,
          "symbols_extracted": 0, "files_total": 11, "symbols_total": 129,
          "relationships_total": 38, "identifiers_total": 500, "types_total": 9,
          "errors": []
        }
        """;

    // A SECOND delete → status=not_found, files_deleted=0 (idempotent), revision unchanged.
    private const string NotFoundJson = """
        {
          "status": "not_found", "operation": "delete",
          "workspace_id": "ws-abc-123",
          "db_path": "/abs/.miller/symbols.db", "root": "/abs/repo",
          "schema_version": 28, "extract_contract_version": 2,
          "revision": 8, "files_scanned": 0, "files_updated": 0, "files_deleted": 0,
          "symbols_extracted": 0, "files_total": 11, "symbols_total": 129,
          "relationships_total": 38, "identifiers_total": 500, "types_total": 9,
          "errors": []
        }
        """;

    private const string FailedJson = """
        {
          "status": "failed", "operation": "update",
          "workspace_id": "ws-abc-123",
          "db_path": "/abs/.miller/symbols.db", "root": "/abs/repo",
          "schema_version": 28, "extract_contract_version": 2,
          "revision": 6, "files_scanned": 0, "files_updated": 0, "files_deleted": 0,
          "symbols_extracted": 0, "files_total": 12, "symbols_total": 134,
          "relationships_total": 40, "identifiers_total": 512, "types_total": 9,
          "errors": [ { "code": "empty_reparse_guard", "message": "refusing to wipe a populated file", "path": "/abs/repo/src/a.cs" } ]
        }
        """;

    [Fact]
    public void Parse_Changed_PopulatesRevisionAndFilesUpdated()
    {
        var r = JulieExtractRunner.ParseReport(ChangedJson);

        Assert.Equal("changed", r.Status);
        Assert.Equal("ws-abc-123", r.WorkspaceId);
        Assert.Equal(7L, r.Revision);
        Assert.Equal(1u, r.FilesUpdated);
        Assert.Equal(0u, r.FilesDeleted);
    }

    [Fact]
    public void Parse_Unchanged_FilesUpdatedZero_RevisionUnbumped()
    {
        var r = JulieExtractRunner.ParseReport(UnchangedJson);

        Assert.Equal("unchanged", r.Status);
        Assert.Equal(0u, r.FilesUpdated);
        Assert.Equal(6L, r.Revision);
    }

    [Fact]
    public void Parse_Deleted_PopulatesFilesDeleted_AndBumpsRevision()
    {
        var r = JulieExtractRunner.ParseReport(DeletedJson);

        Assert.Equal("deleted", r.Status);
        Assert.Equal(1u, r.FilesDeleted);
        Assert.Equal(8L, r.Revision);
    }

    [Fact]
    public void Parse_NotFound_IsIdempotent_FilesDeletedZero()
    {
        var r = JulieExtractRunner.ParseReport(NotFoundJson);

        Assert.Equal("not_found", r.Status);
        Assert.Equal(0u, r.FilesDeleted);
        Assert.Equal(8L, r.Revision); // unchanged from the prior delete
    }

    [Fact]
    public void Parse_Failed_CarriesErrorsAndWorkspaceId()
    {
        var r = JulieExtractRunner.ParseReport(FailedJson);

        Assert.Equal("failed", r.Status);
        Assert.Equal("ws-abc-123", r.WorkspaceId);
        var err = Assert.Single(r.Errors);
        Assert.Equal("empty_reparse_guard", err.Code);
    }

    [Fact]
    public void Parse_MissingNewFields_DefaultToNullOrZero_NotAFailure()
    {
        // A scan report (the M1 shape) omits revision/files_updated/files_deleted/workspace_id. The new fields
        // must default gracefully so the existing scan parse path is unaffected.
        const string scanJson = """
            { "status": "scanned", "operation": "scan", "db_path": "/abs/db", "root": "/abs/r",
              "schema_version": 28, "extract_contract_version": 2,
              "files_scanned": 3, "symbols_extracted": 9, "files_total": 3, "symbols_total": 9,
              "relationships_total": 0, "identifiers_total": 0, "types_total": 0, "errors": [] }
            """;

        var r = JulieExtractRunner.ParseReport(scanJson);

        Assert.Null(r.WorkspaceId);
        Assert.Null(r.Revision);
        Assert.Equal(0u, r.FilesUpdated);
        Assert.Equal(0u, r.FilesDeleted);
    }
}
