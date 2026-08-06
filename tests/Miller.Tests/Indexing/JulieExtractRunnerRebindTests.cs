using System.Text.Json;
using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the <c>rebind</c> seams WITHOUT spawning julie-extract (the live path is
/// <see cref="RebindVerbScaleTests"/>). Every report fixture below is a REAL julie-extract 2.27.0 report,
/// captured by running the pinned binary against a scratch artifact; only filesystem paths are shortened, so
/// every emitted key, null, and diagnostic detail is the extractor's own. The one exception is
/// <see cref="ArtifactChangedJson"/>: its envelope is a captured exit-1 rebind refusal and its diagnostic is
/// the one julie emits from <c>artifact_access.rs check_validated_identity</c>, because the refusal needs a
/// writer racing the validation that no fixture can stage.
/// </summary>
public sealed class JulieExtractRunnerRebindTests
{
    private const string AbsDb = "/repo/.miller/symbols.db.rebuild";
    private const string AbsRoot = "/repo/checkout-b";
    private const string PreviousRoot = "/repo/checkout-a";
    private const string PreviousArtifactId = "artifact-1785974073783974000";
    private const string NewArtifactId = "artifact-561d2148f24e99d0ba69a0684fd2b3d7";

    [Fact]
    public void BuildRebindArgs_ProducesTheContractArgv_RootBeforeDb()
    {
        var args = JulieExtractRunner.BuildRebindArgs(AbsDb, AbsRoot);

        Assert.Equal(
            new[] { "rebind", "--root", AbsRoot, "--db", AbsDb, "--strict-schema", "--json" },
            args);
    }

    [Fact]
    public void BuildRebindArgs_CarriesNoFileOrForceFlag_TheVerbAcceptsNeither()
    {
        var args = JulieExtractRunner.BuildRebindArgs(AbsDb, AbsRoot);

        Assert.DoesNotContain("--file", args);
        Assert.DoesNotContain("--force", args);
        Assert.DoesNotContain("--ignore-file", args);
    }

    [Fact]
    public void BuildRebindArgs_PassesPathsVerbatim_NoNormalizationInTheBuilder()
    {
        var args = JulieExtractRunner.BuildRebindArgs(AbsDb, AbsRoot);

        Assert.Equal(AbsRoot, args[args.ToList().IndexOf("--root") + 1]);
        Assert.Equal(AbsDb, args[args.ToList().IndexOf("--db") + 1]);
    }

    [Theory]
    [InlineData(null, AbsRoot)]
    [InlineData(AbsDb, null)]
    [InlineData("", AbsRoot)]
    [InlineData(AbsDb, "   ")]
    public void BuildRebindArgs_RejectsNullOrBlankArguments(string? db, string? root)
    {
        Assert.ThrowsAny<ArgumentException>(() => JulieExtractRunner.BuildRebindArgs(db!, root!));
    }


    [Fact]
    public void ParseRebindReport_RoundTripsAllFiveFields()
    {
        var rebind = JulieExtractRunner.ParseRebindReport(RebindOkJson);

        Assert.Equal(PreviousRoot, rebind.PreviousRoot);
        Assert.Equal(AbsRoot, rebind.NewRoot);
        Assert.Equal(PreviousArtifactId, rebind.PreviousArtifactId);
        Assert.Equal(NewArtifactId, rebind.NewArtifactId);
        Assert.True(rebind.Changed);
    }

    [Fact]
    public void ParseRebindReport_SameRootNoOp_ParsesChangedFalse_WithTheIdentityUnmoved()
    {
        var rebind = JulieExtractRunner.ParseRebindReport(RebindNoChangeJson);

        Assert.False(rebind.Changed);
        Assert.Equal(rebind.PreviousRoot, rebind.NewRoot);
        Assert.Equal(rebind.PreviousArtifactId, rebind.NewArtifactId);
    }

    [Fact]
    public void ParseRebindReport_RefusedReportWithNoRebindSection_Throws()
    {
        Assert.Throws<JsonException>(() => JulieExtractRunner.ParseRebindReport(FingerprintMismatchJson));
    }

    [Fact]
    public void ParseRebindReport_SectionMissingAField_Throws()
    {
        const string missingChanged = """
            { "status": "ok", "operation": "rebind",
              "rebind": { "previous_root": "/repo/checkout-a", "new_root": "/repo/checkout-b",
                          "previous_artifact_id": "artifact-a", "new_artifact_id": "artifact-b" } }
            """;

        Assert.Throws<JsonException>(() => JulieExtractRunner.ParseRebindReport(missingChanged));
    }

    [Fact]
    public void Interpret_RebindOk_Exit0_ReturnsAMetadataReportThatCommittedNoRevision()
    {
        var report = JulieExtractRunner.Interpret(exitCode: 0, stdout: RebindOkJson, stderr: "");

        Assert.Equal("ok", report.Status);
        Assert.Equal("rebind", report.Operation);
        Assert.Equal("metadata", report.Mode);
        Assert.Equal(1L, report.Revision);
        Assert.Null(report.CreatedRevision);
        Assert.Equal(AbsRoot, report.Artifact!.RootPath);
        Assert.Equal(NewArtifactId, report.Artifact.ArtifactId);
    }

    [Fact]
    public void Interpret_RebindNoChange_Exit0_ReturnsANoChangeReport()
    {
        var report = JulieExtractRunner.Interpret(exitCode: 0, stdout: RebindNoChangeJson, stderr: "");

        Assert.True(report.IsNoChange);
        Assert.Empty(report.Errors);
    }

    [Fact]
    public void Interpret_FingerprintMismatch_Exit3_ThrowsIncompatible_PreservingTheCode()
    {
        var ex = Assert.Throws<IncompatibleExtractException>(() =>
            JulieExtractRunner.Interpret(exitCode: 3, stdout: FingerprintMismatchJson, stderr: ""));

        Assert.Contains("fingerprint_mismatch", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Interpret_NoCommittedRevision_Exit3_ThrowsIncompatible_PreservingTheCode()
    {
        var ex = Assert.Throws<IncompatibleExtractException>(() =>
            JulieExtractRunner.Interpret(exitCode: 3, stdout: NoCommittedRevisionJson, stderr: ""));

        Assert.Contains("no_committed_revision", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Interpret_ArtifactChanged_Exit1_ThrowsFailed_CarryingTheRecoverableDiagnostic()
    {
        var ex = Assert.Throws<JulieExtractFailedException>(() =>
            JulieExtractRunner.Interpret(exitCode: 1, stdout: ArtifactChangedJson, stderr: ""));

        var error = Assert.Single(ex.Errors);
        Assert.Equal("artifact_changed", error.Code);
        Assert.True(error.Recoverable);
        Assert.Equal(1, ex.ExitCode);
    }

    [Theory]
    [InlineData(null, AbsRoot)]
    [InlineData(AbsDb, null)]
    [InlineData("", AbsRoot)]
    [InlineData(AbsDb, "   ")]
    public void Rebind_RejectsNullOrBlankArguments_BeforeSpawning(string? db, string? root)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => RealRunner().Rebind(db!, root!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Rebind_AlreadyCanceledToken_ThrowsBeforeSpawning()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => RealRunner().Rebind(AbsDb, AbsRoot, cts.Token));
    }

    private static JulieExtractRunner RealRunner() =>
        new(typeof(JulieExtractRunnerRebindTests).Assembly.Location);

    private const string RebindOkJson = """
        {
          "report_schema_version": 3,
          "status": "ok",
          "operation": "rebind",
          "mode": "metadata",
          "input": {
            "db_path": "/repo/.miller/symbols.db.rebuild",
            "root_path": "/repo/checkout-b",
            "file_path": null,
            "root_relative_path": null,
            "format": null,
            "output_path": null
          },
          "artifact": {
            "db_path": "/repo/.miller/symbols.db.rebuild",
            "root_path": "/repo/checkout-b",
            "artifact_id": "artifact-561d2148f24e99d0ba69a0684fd2b3d7",
            "schema_version": 5,
            "extract_contract_version": 4,
            "sqlite_schema_version": 5,
            "jsonl_schema_version": 4,
            "hash_algorithm": "blake3",
            "parser_inventory_fingerprint": "sha256:b0c37e709ae49526dfed416ee23a52971991da8e4d5f489df37054b1ce84e8d4",
            "capability_snapshot_fingerprint": "sha256:3459e067b516616c9c3994eda324110ccac5ab6831ed408ed7fcf56a853ca5ae",
            "index_level": "full"
          },
          "tool": {
            "binary_name": "julie-extract",
            "binary_version": "2.27.0"
          },
          "revision": {
            "latest_revision_id": 1,
            "created_revision_id": null
          },
          "counts": {
            "files_scanned": 0,
            "files_changed": 0,
            "files_unchanged": 0,
            "files_unsupported": 0,
            "files_deleted": 0,
            "files_failed": 0,
            "rows_written": {
              "artifact_metadata": 0,
              "parser_inventory": 0,
              "language_capabilities": 0,
              "language_capability_fixtures": 0,
              "language_capability_gaps": 0,
              "extraction_revisions": 0,
              "revision_file_changes": 0,
              "files": 0,
              "symbols": 0,
              "symbol_annotations": 0,
              "reference_sites": 0,
              "identifiers": 0,
              "relationships": 0,
              "pending_relationships": 0,
              "type_facts": 0,
              "type_argument_usages": 0,
              "type_arguments": 0,
              "literals": 0,
              "source_regions": 0,
              "structural_facts": 0,
              "complexity_metrics": 0,
              "parse_diagnostics": 0,
              "pending_resolutions": 0,
              "identifier_resolutions": 0
            },
            "totals": {
              "artifact_metadata": 0,
              "parser_inventory": 0,
              "language_capabilities": 0,
              "language_capability_fixtures": 0,
              "language_capability_gaps": 0,
              "extraction_revisions": 0,
              "revision_file_changes": 0,
              "files": 0,
              "symbols": 0,
              "symbol_annotations": 0,
              "reference_sites": 0,
              "identifiers": 0,
              "relationships": 0,
              "pending_relationships": 0,
              "type_facts": 0,
              "type_argument_usages": 0,
              "type_arguments": 0,
              "literals": 0,
              "source_regions": 0,
              "structural_facts": 0,
              "complexity_metrics": 0,
              "parse_diagnostics": 0,
              "pending_resolutions": 0,
              "identifier_resolutions": 0
            },
            "file_rows_truncated": false,
            "file_rows": []
          },
          "rebind": {
            "previous_root": "/repo/checkout-a",
            "new_root": "/repo/checkout-b",
            "previous_artifact_id": "artifact-1785974073783974000",
            "new_artifact_id": "artifact-561d2148f24e99d0ba69a0684fd2b3d7",
            "changed": true
          },
          "errors": [],
          "warnings": []
        }
        """;

    private const string RebindNoChangeJson = """
        {
          "report_schema_version": 3,
          "status": "no_change",
          "operation": "rebind",
          "mode": "metadata",
          "input": {
            "db_path": "/repo/.miller/symbols.db.rebuild",
            "root_path": "/repo/checkout-b",
            "file_path": null,
            "root_relative_path": null,
            "format": null,
            "output_path": null
          },
          "artifact": {
            "db_path": "/repo/.miller/symbols.db.rebuild",
            "root_path": "/repo/checkout-b",
            "artifact_id": "artifact-561d2148f24e99d0ba69a0684fd2b3d7",
            "schema_version": 5,
            "extract_contract_version": 4,
            "sqlite_schema_version": 5,
            "jsonl_schema_version": 4,
            "hash_algorithm": "blake3",
            "parser_inventory_fingerprint": "sha256:b0c37e709ae49526dfed416ee23a52971991da8e4d5f489df37054b1ce84e8d4",
            "capability_snapshot_fingerprint": "sha256:3459e067b516616c9c3994eda324110ccac5ab6831ed408ed7fcf56a853ca5ae",
            "index_level": "full"
          },
          "tool": {
            "binary_name": "julie-extract",
            "binary_version": "2.27.0"
          },
          "revision": {
            "latest_revision_id": 1,
            "created_revision_id": null
          },
          "counts": {
            "files_scanned": 0,
            "files_changed": 0,
            "files_unchanged": 0,
            "files_unsupported": 0,
            "files_deleted": 0,
            "files_failed": 0,
            "rows_written": {
              "artifact_metadata": 0,
              "parser_inventory": 0,
              "language_capabilities": 0,
              "language_capability_fixtures": 0,
              "language_capability_gaps": 0,
              "extraction_revisions": 0,
              "revision_file_changes": 0,
              "files": 0,
              "symbols": 0,
              "symbol_annotations": 0,
              "reference_sites": 0,
              "identifiers": 0,
              "relationships": 0,
              "pending_relationships": 0,
              "type_facts": 0,
              "type_argument_usages": 0,
              "type_arguments": 0,
              "literals": 0,
              "source_regions": 0,
              "structural_facts": 0,
              "complexity_metrics": 0,
              "parse_diagnostics": 0,
              "pending_resolutions": 0,
              "identifier_resolutions": 0
            },
            "totals": {
              "artifact_metadata": 0,
              "parser_inventory": 0,
              "language_capabilities": 0,
              "language_capability_fixtures": 0,
              "language_capability_gaps": 0,
              "extraction_revisions": 0,
              "revision_file_changes": 0,
              "files": 0,
              "symbols": 0,
              "symbol_annotations": 0,
              "reference_sites": 0,
              "identifiers": 0,
              "relationships": 0,
              "pending_relationships": 0,
              "type_facts": 0,
              "type_argument_usages": 0,
              "type_arguments": 0,
              "literals": 0,
              "source_regions": 0,
              "structural_facts": 0,
              "complexity_metrics": 0,
              "parse_diagnostics": 0,
              "pending_resolutions": 0,
              "identifier_resolutions": 0
            },
            "file_rows_truncated": false,
            "file_rows": []
          },
          "rebind": {
            "previous_root": "/repo/checkout-b",
            "new_root": "/repo/checkout-b",
            "previous_artifact_id": "artifact-561d2148f24e99d0ba69a0684fd2b3d7",
            "new_artifact_id": "artifact-561d2148f24e99d0ba69a0684fd2b3d7",
            "changed": false
          },
          "errors": [],
          "warnings": []
        }
        """;

    private const string FingerprintMismatchJson = """
        {
          "report_schema_version": 3,
          "status": "failed",
          "operation": "rebind",
          "mode": "metadata",
          "input": {
            "db_path": "/repo/.miller/symbols.db.rebuild",
            "root_path": "/repo/checkout-b",
            "file_path": null,
            "root_relative_path": null,
            "format": null,
            "output_path": null
          },
          "artifact": null,
          "tool": {
            "binary_name": "julie-extract",
            "binary_version": "2.27.0"
          },
          "revision": null,
          "counts": {
            "files_scanned": 0,
            "files_changed": 0,
            "files_unchanged": 0,
            "files_unsupported": 0,
            "files_deleted": 0,
            "files_failed": 0,
            "rows_written": {
              "artifact_metadata": 0,
              "parser_inventory": 0,
              "language_capabilities": 0,
              "language_capability_fixtures": 0,
              "language_capability_gaps": 0,
              "extraction_revisions": 0,
              "revision_file_changes": 0,
              "files": 0,
              "symbols": 0,
              "symbol_annotations": 0,
              "reference_sites": 0,
              "identifiers": 0,
              "relationships": 0,
              "pending_relationships": 0,
              "type_facts": 0,
              "type_argument_usages": 0,
              "type_arguments": 0,
              "literals": 0,
              "source_regions": 0,
              "structural_facts": 0,
              "complexity_metrics": 0,
              "parse_diagnostics": 0,
              "pending_resolutions": 0,
              "identifier_resolutions": 0
            },
            "totals": {
              "artifact_metadata": 0,
              "parser_inventory": 0,
              "language_capabilities": 0,
              "language_capability_fixtures": 0,
              "language_capability_gaps": 0,
              "extraction_revisions": 0,
              "revision_file_changes": 0,
              "files": 0,
              "symbols": 0,
              "symbol_annotations": 0,
              "reference_sites": 0,
              "identifiers": 0,
              "relationships": 0,
              "pending_relationships": 0,
              "type_facts": 0,
              "type_argument_usages": 0,
              "type_arguments": 0,
              "literals": 0,
              "source_regions": 0,
              "structural_facts": 0,
              "complexity_metrics": 0,
              "parse_diagnostics": 0,
              "pending_resolutions": 0,
              "identifier_resolutions": 0
            },
            "file_rows_truncated": false,
            "file_rows": []
          },
          "errors": [
            {
              "code": "fingerprint_mismatch",
              "message": "artifact capability fingerprints do not match this binary",
              "path": "/repo/.miller/symbols.db.rebuild",
              "root_relative_path": null,
              "recoverable": false,
              "details": {
                "action": "julie-extract scan",
                "artifact_capability_snapshot_fingerprint": "sha256:3459e067b516616c9c3994eda324110ccac5ab6831ed408ed7fcf56a853ca5ae",
                "artifact_parser_inventory_fingerprint": "sha256:0000000000000000000000000000000000000000000000000000000000000000",
                "expected_capability_snapshot_fingerprint": "sha256:3459e067b516616c9c3994eda324110ccac5ab6831ed408ed7fcf56a853ca5ae",
                "expected_parser_inventory_fingerprint": "sha256:b0c37e709ae49526dfed416ee23a52971991da8e4d5f489df37054b1ce84e8d4"
              }
            }
          ],
          "warnings": []
        }
        """;

    private const string NoCommittedRevisionJson = """
        {
          "report_schema_version": 3,
          "status": "failed",
          "operation": "rebind",
          "mode": "metadata",
          "input": {
            "db_path": "/repo/.miller/symbols.db.rebuild",
            "root_path": "/repo/checkout-b",
            "file_path": null,
            "root_relative_path": null,
            "format": null,
            "output_path": null
          },
          "artifact": null,
          "tool": {
            "binary_name": "julie-extract",
            "binary_version": "2.27.0"
          },
          "revision": null,
          "counts": {
            "files_scanned": 0,
            "files_changed": 0,
            "files_unchanged": 0,
            "files_unsupported": 0,
            "files_deleted": 0,
            "files_failed": 0,
            "rows_written": {
              "artifact_metadata": 0,
              "parser_inventory": 0,
              "language_capabilities": 0,
              "language_capability_fixtures": 0,
              "language_capability_gaps": 0,
              "extraction_revisions": 0,
              "revision_file_changes": 0,
              "files": 0,
              "symbols": 0,
              "symbol_annotations": 0,
              "reference_sites": 0,
              "identifiers": 0,
              "relationships": 0,
              "pending_relationships": 0,
              "type_facts": 0,
              "type_argument_usages": 0,
              "type_arguments": 0,
              "literals": 0,
              "source_regions": 0,
              "structural_facts": 0,
              "complexity_metrics": 0,
              "parse_diagnostics": 0,
              "pending_resolutions": 0,
              "identifier_resolutions": 0
            },
            "totals": {
              "artifact_metadata": 0,
              "parser_inventory": 0,
              "language_capabilities": 0,
              "language_capability_fixtures": 0,
              "language_capability_gaps": 0,
              "extraction_revisions": 0,
              "revision_file_changes": 0,
              "files": 0,
              "symbols": 0,
              "symbol_annotations": 0,
              "reference_sites": 0,
              "identifiers": 0,
              "relationships": 0,
              "pending_relationships": 0,
              "type_facts": 0,
              "type_argument_usages": 0,
              "type_arguments": 0,
              "literals": 0,
              "source_regions": 0,
              "structural_facts": 0,
              "complexity_metrics": 0,
              "parse_diagnostics": 0,
              "pending_resolutions": 0,
              "identifier_resolutions": 0
            },
            "file_rows_truncated": false,
            "file_rows": []
          },
          "errors": [
            {
              "code": "no_committed_revision",
              "message": "artifact carries no committed extraction revision",
              "path": "/repo/.miller/symbols.db.rebuild",
              "root_relative_path": null,
              "recoverable": true,
              "details": {
                "action": "julie-extract scan"
              }
            }
          ],
          "warnings": []
        }
        """;

    private const string ArtifactChangedJson = """
        {
          "report_schema_version": 3,
          "status": "failed",
          "operation": "rebind",
          "mode": "metadata",
          "input": {
            "db_path": "/repo/.miller/symbols.db.rebuild",
            "root_path": "/repo/checkout-b",
            "file_path": null,
            "root_relative_path": null,
            "format": null,
            "output_path": null
          },
          "artifact": null,
          "tool": {
            "binary_name": "julie-extract",
            "binary_version": "2.27.0"
          },
          "revision": null,
          "counts": {
            "files_scanned": 0,
            "files_changed": 0,
            "files_unchanged": 0,
            "files_unsupported": 0,
            "files_deleted": 0,
            "files_failed": 0,
            "rows_written": {
              "artifact_metadata": 0,
              "parser_inventory": 0,
              "language_capabilities": 0,
              "language_capability_fixtures": 0,
              "language_capability_gaps": 0,
              "extraction_revisions": 0,
              "revision_file_changes": 0,
              "files": 0,
              "symbols": 0,
              "symbol_annotations": 0,
              "reference_sites": 0,
              "identifiers": 0,
              "relationships": 0,
              "pending_relationships": 0,
              "type_facts": 0,
              "type_argument_usages": 0,
              "type_arguments": 0,
              "literals": 0,
              "source_regions": 0,
              "structural_facts": 0,
              "complexity_metrics": 0,
              "parse_diagnostics": 0,
              "pending_resolutions": 0,
              "identifier_resolutions": 0
            },
            "totals": {
              "artifact_metadata": 0,
              "parser_inventory": 0,
              "language_capabilities": 0,
              "language_capability_fixtures": 0,
              "language_capability_gaps": 0,
              "extraction_revisions": 0,
              "revision_file_changes": 0,
              "files": 0,
              "symbols": 0,
              "symbol_annotations": 0,
              "reference_sites": 0,
              "identifiers": 0,
              "relationships": 0,
              "pending_relationships": 0,
              "type_facts": 0,
              "type_argument_usages": 0,
              "type_arguments": 0,
              "literals": 0,
              "source_regions": 0,
              "structural_facts": 0,
              "complexity_metrics": 0,
              "parse_diagnostics": 0,
              "pending_resolutions": 0,
              "identifier_resolutions": 0
            },
            "file_rows_truncated": false,
            "file_rows": []
          },
          "errors": [
            {
              "code": "artifact_changed",
              "message": "artifact changed while rebind was validating",
              "path": "/repo/.miller/symbols.db.rebuild",
              "root_relative_path": null,
              "recoverable": true,
              "details": {
                "expected_root_path": "/repo/checkout-a",
                "found_root_path": "/repo/checkout-c",
                "expected_artifact_id": "artifact-1785974073783974000",
                "found_artifact_id": "artifact-9b1c7f0e4d2a41f68e5307c9ab14d2e3"
              }
            }
          ],
          "warnings": []
        }
        """;
}
