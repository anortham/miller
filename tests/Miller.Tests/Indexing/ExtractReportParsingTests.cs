using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins ExtractReport's v1 nested parse: revision.latest_revision_id is the freshness cursor;
/// created_revision_id is null on a no-op; counts/rows_written carry the per-op outcome; a null artifact
/// block is preserved (not invented). Each outcome the freshness path branches on (changed / no-op /
/// deleted / not_found / failed) is parsed from a representative report and asserted on the accessors.
/// </summary>
public sealed class ExtractReportParsingTests
{
    // update of a CHANGED file -> status=ok, files_changed=1, revision bumps (created==latest).
    private const string ChangedJson = """
        { "report_schema_version": 1, "status": "ok", "operation": "update", "mode": "single_file",
          "input": { "db_path": "/abs/.miller/symbols.db", "root_path": "/abs/repo", "file_path": "/abs/repo/src/a.cs",
                     "root_relative_path": "src/a.cs", "format": null, "output_path": null },
          "artifact": { "db_path": "/abs/.miller/symbols.db", "root_path": "/abs/repo", "artifact_id": "art-1",
                        "schema_version": 1, "extract_contract_version": 1, "sqlite_schema_version": 1,
                        "jsonl_schema_version": 1, "hash_algorithm": "blake3",
                        "parser_inventory_fingerprint": "sha256:pi", "capability_snapshot_fingerprint": "sha256:cs" },
          "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
          "revision": { "latest_revision_id": 7, "created_revision_id": 7 },
          "counts": { "files_scanned": 0, "files_changed": 1, "files_unchanged": 0, "files_unsupported": 0,
                      "files_deleted": 0, "files_failed": 0,
                      "rows_written": { "symbols": 5 }, "totals": { "files": 12, "symbols": 134 } },
          "errors": [], "warnings": [] }
        """;

    // no-op update -> status=no_change, created_revision_id null, latest carries the prior cursor.
    private const string NoChangeJson = """
        { "report_schema_version": 1, "status": "no_change", "operation": "update", "mode": "single_file",
          "input": { "db_path": "/abs/db", "root_path": "/abs/r", "file_path": "/abs/r/a.cs",
                     "root_relative_path": "a.cs", "format": null, "output_path": null },
          "artifact": { "db_path": "/abs/db", "root_path": "/abs/r", "artifact_id": "a", "schema_version": 1,
                        "extract_contract_version": 1, "sqlite_schema_version": 1, "jsonl_schema_version": null,
                        "hash_algorithm": "blake3", "parser_inventory_fingerprint": "sha256:p",
                        "capability_snapshot_fingerprint": "sha256:c" },
          "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
          "revision": { "latest_revision_id": 6, "created_revision_id": null },
          "counts": { "files_scanned": 0, "files_changed": 0, "files_unchanged": 1, "files_unsupported": 0,
                      "files_deleted": 0, "files_failed": 0, "rows_written": {}, "totals": { "files": 12 } },
          "errors": [], "warnings": [] }
        """;

    // delete of a removed file -> status=ok, files_deleted=1, revision bumps.
    private const string DeletedJson = """
        { "report_schema_version": 1, "status": "ok", "operation": "delete", "mode": "single_file",
          "input": { "db_path": "/abs/db", "root_path": "/abs/r", "file_path": "/abs/r/a.cs",
                     "root_relative_path": "a.cs", "format": null, "output_path": null },
          "artifact": { "db_path": "/abs/db", "root_path": "/abs/r", "artifact_id": "a", "schema_version": 1,
                        "extract_contract_version": 1, "sqlite_schema_version": 1, "jsonl_schema_version": 1,
                        "hash_algorithm": "blake3", "parser_inventory_fingerprint": "sha256:p",
                        "capability_snapshot_fingerprint": "sha256:c" },
          "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
          "revision": { "latest_revision_id": 8, "created_revision_id": 8 },
          "counts": { "files_scanned": 0, "files_changed": 0, "files_unchanged": 0, "files_unsupported": 0,
                      "files_deleted": 1, "files_failed": 0, "rows_written": {}, "totals": { "files": 11 } },
          "errors": [], "warnings": [] }
        """;

    // a SECOND delete -> status=not_found, files_deleted=0 (idempotent), created_revision_id null.
    private const string NotFoundJson = """
        { "report_schema_version": 1, "status": "not_found", "operation": "delete", "mode": "single_file",
          "input": { "db_path": "/abs/db", "root_path": "/abs/r", "file_path": "/abs/r/a.cs",
                     "root_relative_path": "a.cs", "format": null, "output_path": null },
          "artifact": { "db_path": "/abs/db", "root_path": "/abs/r", "artifact_id": "a", "schema_version": 1,
                        "extract_contract_version": 1, "sqlite_schema_version": 1, "jsonl_schema_version": 1,
                        "hash_algorithm": "blake3", "parser_inventory_fingerprint": "sha256:p",
                        "capability_snapshot_fingerprint": "sha256:c" },
          "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
          "revision": { "latest_revision_id": 8, "created_revision_id": null },
          "counts": { "files_scanned": 0, "files_changed": 0, "files_unchanged": 0, "files_unsupported": 0,
                      "files_deleted": 0, "files_failed": 0, "rows_written": {}, "totals": { "files": 11 } },
          "errors": [], "warnings": [] }
        """;

    // a scan where some files failed to parse -> status=partial, files_failed>0, a CONSISTENT artifact +
    // revision (Interpret returns it; the caller WARN-logs the dropped files). errors[] carry the per-file codes.
    private const string PartialJson = """
        { "report_schema_version": 1, "status": "partial", "operation": "scan", "mode": "full",
          "input": { "db_path": "/abs/db", "root_path": "/abs/r", "file_path": null,
                     "root_relative_path": null, "format": null, "output_path": null },
          "artifact": { "db_path": "/abs/db", "root_path": "/abs/r", "artifact_id": "a", "schema_version": 1,
                        "extract_contract_version": 1, "sqlite_schema_version": 1, "jsonl_schema_version": 1,
                        "hash_algorithm": "blake3", "parser_inventory_fingerprint": "sha256:p",
                        "capability_snapshot_fingerprint": "sha256:c" },
          "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
          "revision": { "latest_revision_id": 9, "created_revision_id": 9 },
          "counts": { "files_scanned": 10, "files_changed": 8, "files_unchanged": 0, "files_unsupported": 0,
                      "files_deleted": 0, "files_failed": 2,
                      "rows_written": { "symbols": 40 }, "totals": { "files": 10, "symbols": 120 } },
          "errors": [ { "code": "parse_error", "message": "tree-sitter failed", "path": "/abs/r/broken/a.rs",
                        "root_relative_path": "broken/a.rs", "recoverable": true, "details": {} },
                      { "code": "parse_error", "message": "tree-sitter failed", "path": "/abs/r/broken/b.rs",
                        "root_relative_path": "broken/b.rs", "recoverable": true, "details": {} } ],
          "warnings": [] }
        """;

    private const string FailedJson = """
        { "report_schema_version": 1, "status": "failed", "operation": "update", "mode": "single_file",
          "input": { "db_path": "/abs/db", "root_path": "/abs/r", "file_path": "/abs/r/a.cs",
                     "root_relative_path": "a.cs", "format": null, "output_path": null },
          "artifact": null,
          "tool": { "binary_name": "julie-extract", "binary_version": "2.0.0" },
          "revision": null,
          "counts": { "files_scanned": 0, "files_changed": 0, "files_unchanged": 0, "files_unsupported": 0,
                      "files_deleted": 0, "files_failed": 1, "rows_written": {}, "totals": {} },
          "errors": [ { "code": "data_loss_guard", "message": "refusing to wipe a populated file",
                        "path": "/abs/r/a.cs", "root_relative_path": "a.cs", "recoverable": false, "details": {} } ],
          "warnings": [] }
        """;

    [Fact]
    public void Parse_Changed_CursorIsLatestRevision_AndCreatedSignalsMutation()
    {
        var r = JulieExtractRunner.ParseReport(ChangedJson);
        Assert.Equal("ok", r.Status);
        Assert.Equal("blake3", r.HashAlgorithm);   // sourced from artifact.hash_algorithm
        Assert.Equal(7L, r.Revision);              // latest_revision_id
        Assert.Equal(7L, r.CreatedRevision);       // this call mutated
        Assert.Equal(1u, r.FilesUpdated);          // counts.files_changed
        Assert.Equal(0u, r.FilesDeleted);
        Assert.Equal(5u, r.SymbolsExtracted);      // counts.rows_written.symbols
    }

    [Fact]
    public void Parse_NoChange_CreatedRevisionNull_CursorStillPresent()
    {
        var r = JulieExtractRunner.ParseReport(NoChangeJson);
        Assert.Equal("no_change", r.Status);
        Assert.Equal(6L, r.Revision);              // latest_revision_id still present after a no-op
        Assert.Null(r.CreatedRevision);            // no mutation -> created_revision_id null
        Assert.Equal(0u, r.FilesUpdated);
    }

    [Fact]
    public void Parse_Deleted_PopulatesFilesDeleted_AndBumpsRevision()
    {
        var r = JulieExtractRunner.ParseReport(DeletedJson);
        Assert.Equal("ok", r.Status);
        Assert.Equal(1u, r.FilesDeleted);          // counts.files_deleted
        Assert.Equal(8L, r.Revision);
        Assert.Equal(8L, r.CreatedRevision);       // the delete mutated
    }

    [Fact]
    public void Parse_NotFound_IsIdempotent_FilesDeletedZero_CreatedRevisionNull()
    {
        var r = JulieExtractRunner.ParseReport(NotFoundJson);
        Assert.Equal("not_found", r.Status);
        Assert.Equal(0u, r.FilesDeleted);
        Assert.Equal(8L, r.Revision);              // cursor unchanged from the prior delete
        Assert.Null(r.CreatedRevision);            // a no-op delete did not mutate
    }

    [Fact]
    public void Parse_Partial_IsPartialTrue_AndFilesFailedCounted()
    {
        var r = JulieExtractRunner.ParseReport(PartialJson);
        Assert.Equal("partial", r.Status);
        Assert.True(r.IsPartial);
        Assert.Equal(2u, r.FilesFailed);          // counts.files_failed — the dropped files
        Assert.Equal(9L, r.Revision);             // a partial artifact is consistent and carries a revision
        Assert.Equal(2, r.Errors.Count);          // per-file parse diagnostics surfaced to the caller
    }

    [Fact]
    public void Parse_HealthyOk_IsNotPartial_FilesFailedZero()
    {
        var r = JulieExtractRunner.ParseReport(ChangedJson);
        Assert.False(r.IsPartial);                 // status=ok must never read as partial
        Assert.Equal(0u, r.FilesFailed);
    }

    [Fact]
    public void Parse_Failed_NullArtifact_AndCarriesDiagnostics()
    {
        var r = JulieExtractRunner.ParseReport(FailedJson);
        Assert.Equal("failed", r.Status);
        Assert.Null(r.Artifact);                   // null artifact preserved, not invented
        Assert.Null(r.HashAlgorithm);              // no artifact => null accessor (gate fail in A3)
        Assert.Null(r.Revision);
        var d = Assert.Single(r.Errors);
        Assert.Equal("data_loss_guard", d.Code);
        Assert.False(d.Recoverable);               // data_loss_guard is non-recoverable in v1 (commands.rs:1099-1116); the per-diagnostic flag replaces the hardcoded transient set
    }
}
