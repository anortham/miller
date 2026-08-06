using Miller.Indexing;
using Xunit;

namespace Miller.Tests.Indexing;

/// <summary>
/// Pins the W8 forensics contract for <see cref="JulieExtractException.ExitCodeOf"/>: a julie-extract exit-3
/// refusal reaches the scan-failure journal carrying <c>3</c>, while the read-path gates — which never ran a
/// subprocess — stay null. The exit-3 fixture is a REAL julie-extract 2.27.0 <c>rebind</c> refusal, the same
/// capture <see cref="JulieExtractRunnerRebindTests"/> uses; only filesystem paths are shortened.
/// </summary>
public sealed class JulieExtractExceptionExitCodeTests
{
    [Fact]
    public void ExitCodeOf_IncompatibleExtractCarryingAProcessCode_ReturnsIt()
    {
        var error = new IncompatibleExtractException("julie-extract refused the rebind (exit 3).", exitCode: 3);

        Assert.Equal(3, error.ExitCode);
        Assert.Equal(3, JulieExtractException.ExitCodeOf(error));
    }

    [Fact]
    public void ExitCodeOf_IncompatibleExtractFromAReadPathGate_IsNull()
    {
        Assert.Null(JulieExtractException.ExitCodeOf(
            new IncompatibleExtractException("schema_version 9 is newer than this Miller understands.")));
        Assert.Null(JulieExtractException.ExitCodeOf(
            new IncompatibleExtractException("table `identifiers` is missing.", new InvalidOperationException())));
    }

    [Fact]
    public void ExitCodeOf_JulieExtractException_StillReadsTheProcessCode()
    {
        Assert.Equal(137, JulieExtractException.ExitCodeOf(new JulieExtractException("killed", "", 137)));
        Assert.Equal(2, JulieExtractException.ExitCodeOf(new JulieExtractUsageException("usage text")));
        Assert.Null(JulieExtractException.ExitCodeOf(new JulieExtractException("exec failed", "")));
    }

    [Fact]
    public void ExitCodeOf_AnythingElse_IsNull()
    {
        Assert.Null(JulieExtractException.ExitCodeOf(new InvalidOperationException()));
        Assert.Null(JulieExtractException.ExitCodeOf(null));
    }

    [Fact]
    public void Interpret_RebindFingerprintMismatch_ThrowsCarryingExitCode3()
    {
        var error = Assert.Throws<IncompatibleExtractException>(() =>
            JulieExtractRunner.Interpret(exitCode: 3, stdout: FingerprintMismatchJson, stderr: ""));

        Assert.Equal(3, JulieExtractException.ExitCodeOf(error));
    }

    [Fact]
    public void Interpret_Exit3WithUnparseableStdout_StillCarriesExitCode3()
    {
        var error = Assert.Throws<IncompatibleExtractException>(() =>
            JulieExtractRunner.Interpret(exitCode: 3, stdout: "", stderr: "no_committed_revision"));

        Assert.Equal(3, JulieExtractException.ExitCodeOf(error));
    }

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
}
