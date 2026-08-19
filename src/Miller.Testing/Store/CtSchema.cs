using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Miller.Testing;

/// <summary>
/// Self-contained DDL for <c>&lt;workspace&gt;/.miller/ct.db</c>. No foreign keys leave this file.
/// Freshness-bearing tables persist the composite <c>(index_identity, revision)</c> from
/// <c>WorkspaceReadSnapshot.IndexIdentity</c>.
/// </summary>
public static class CtSchema
{
    public const int SchemaVersion = 1;
    public const string DbFileName = "ct.db";
    public const string MillerDirectoryName = ".miller";

    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS meta(
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS test_suites (
            id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL,
            name TEXT NOT NULL,
            framework TEXT,
            source TEXT NOT NULL,
            metadata_json TEXT NOT NULL DEFAULT '{}',
            UNIQUE (workspace_id, name, source)
        );

        CREATE TABLE IF NOT EXISTS test_cases (
            id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL,
            file_path TEXT,
            content_hash TEXT,
            symbol_name TEXT,
            symbol_path TEXT,
            suite_id TEXT REFERENCES test_suites(id) ON DELETE SET NULL,
            name TEXT NOT NULL,
            qualified_name TEXT NOT NULL,
            selector TEXT NOT NULL,
            framework TEXT,
            role TEXT NOT NULL,
            source TEXT NOT NULL,
            confidence REAL NOT NULL CHECK (confidence >= 0.0 AND confidence <= 1.0),
            metadata_json TEXT NOT NULL DEFAULT '{}',
            provenance_json TEXT NOT NULL DEFAULT '{}',
            UNIQUE (workspace_id, selector, source)
        );

        CREATE TABLE IF NOT EXISTS run_artifacts (
            id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL,
            kind TEXT NOT NULL,
            path TEXT,
            payload_json TEXT NOT NULL DEFAULT '{}',
            created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
        );

        CREATE TABLE IF NOT EXISTS test_runs (
            id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL,
            index_identity TEXT NOT NULL,
            revision INTEGER NOT NULL,
            command TEXT,
            framework TEXT,
            status TEXT NOT NULL,
            started_at TEXT,
            ended_at TEXT,
            selected_revision TEXT,
            completed_revision TEXT,
            artifact_id TEXT REFERENCES run_artifacts(id) ON DELETE SET NULL,
            environment_hash TEXT,
            metadata_json TEXT NOT NULL DEFAULT '{}'
        );

        CREATE TABLE IF NOT EXISTS test_results (
            id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL,
            index_identity TEXT NOT NULL,
            revision INTEGER NOT NULL,
            test_case_id TEXT NOT NULL REFERENCES test_cases(id) ON DELETE CASCADE,
            test_run_id TEXT NOT NULL REFERENCES test_runs(id) ON DELETE CASCADE,
            status TEXT NOT NULL,
            result_revision TEXT,
            duration_seconds REAL,
            failure_text_hash TEXT,
            failure_summary TEXT,
            source_artifact_id TEXT REFERENCES run_artifacts(id) ON DELETE SET NULL,
            metadata_json TEXT NOT NULL DEFAULT '{}',
            UNIQUE (workspace_id, test_case_id, test_run_id)
        );

        CREATE TABLE IF NOT EXISTS coverage_files (
            id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL,
            index_identity TEXT NOT NULL,
            revision INTEGER NOT NULL,
            artifact_id TEXT REFERENCES run_artifacts(id) ON DELETE SET NULL,
            format TEXT NOT NULL,
            path TEXT NOT NULL,
            parser TEXT NOT NULL,
            source_hash TEXT NOT NULL,
            generated_at TEXT,
            metadata_json TEXT NOT NULL DEFAULT '{}'
        );

        CREATE TABLE IF NOT EXISTS coverage_spans (
            id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL,
            index_identity TEXT NOT NULL,
            revision INTEGER NOT NULL,
            coverage_file_id TEXT NOT NULL REFERENCES coverage_files(id) ON DELETE CASCADE,
            file_path TEXT,
            content_hash TEXT,
            symbol_name TEXT,
            symbol_path TEXT,
            start_line INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            hits INTEGER NOT NULL CHECK (hits >= 0),
            branch_hits INTEGER,
            metadata_json TEXT NOT NULL DEFAULT '{}'
        );

        CREATE TABLE IF NOT EXISTS test_links (
            id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL,
            test_case_id TEXT REFERENCES test_cases(id) ON DELETE CASCADE,
            source_file_path TEXT,
            source_content_hash TEXT,
            source_symbol_name TEXT,
            source_symbol_path TEXT,
            tier TEXT NOT NULL,
            confidence REAL NOT NULL CHECK (confidence >= 0.0 AND confidence <= 1.0),
            explanation TEXT NOT NULL,
            source_fact_ids_json TEXT NOT NULL DEFAULT '[]',
            metadata_json TEXT NOT NULL DEFAULT '{}'
        );

        CREATE TABLE IF NOT EXISTS test_quality_findings (
            id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL,
            test_case_id TEXT REFERENCES test_cases(id) ON DELETE CASCADE,
            file_path TEXT,
            content_hash TEXT,
            symbol_name TEXT,
            symbol_path TEXT,
            finding_type TEXT NOT NULL,
            severity TEXT NOT NULL,
            confidence REAL NOT NULL CHECK (confidence >= 0.0 AND confidence <= 1.0),
            explanation TEXT NOT NULL,
            evidence_json TEXT NOT NULL DEFAULT '{}'
        );

        CREATE TABLE IF NOT EXISTS implementation_quality_findings (
            id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL,
            file_path TEXT,
            content_hash TEXT,
            symbol_name TEXT,
            symbol_path TEXT,
            finding_type TEXT NOT NULL,
            severity TEXT NOT NULL,
            confidence REAL NOT NULL CHECK (confidence >= 0.0 AND confidence <= 1.0),
            explanation TEXT NOT NULL,
            evidence_json TEXT NOT NULL DEFAULT '{}'
        );

        CREATE TABLE IF NOT EXISTS confidence_snapshots (
            id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL,
            index_identity TEXT NOT NULL,
            revision INTEGER NOT NULL,
            subject_type TEXT NOT NULL,
            subject_id TEXT NOT NULL,
            state TEXT NOT NULL,
            score REAL NOT NULL CHECK (score >= 0.0 AND score <= 1.0),
            evidence_json TEXT NOT NULL DEFAULT '[]',
            freshness_json TEXT NOT NULL DEFAULT '{}',
            limitations_json TEXT NOT NULL DEFAULT '[]',
            recommended_command TEXT,
            computed_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
        );

        CREATE TABLE IF NOT EXISTS ct_test_states (
            test_case_id TEXT PRIMARY KEY REFERENCES test_cases(id) ON DELETE CASCADE,
            workspace_id TEXT NOT NULL,
            index_identity TEXT NOT NULL,
            revision INTEGER NOT NULL,
            state TEXT NOT NULL,
            last_run_revision TEXT,
            stale_since_revision TEXT,
            running_run_id TEXT REFERENCES test_runs(id) ON DELETE SET NULL,
            running_revision TEXT,
            last_result_status TEXT,
            last_result_at TEXT,
            failure_summary TEXT,
            flakiness_score REAL NOT NULL DEFAULT 0.0 CHECK (
                flakiness_score >= 0.0 AND flakiness_score <= 1.0
            ),
            updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            CHECK (state IN ('unknown', 'green', 'red', 'skipped', 'running', 'stale'))
        );

        CREATE TABLE IF NOT EXISTS ct_test_projects (
            id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL,
            project_path TEXT NOT NULL,
            framework TEXT,
            command TEXT,
            enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
            metadata_json TEXT NOT NULL DEFAULT '{}',
            exclude_traits TEXT NOT NULL DEFAULT '[]',
            inventory_stale INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
        );

        CREATE TABLE IF NOT EXISTS ct_case_fresh_watermarks (
            test_case_id TEXT NOT NULL REFERENCES test_cases(id) ON DELETE CASCADE,
            workspace_id TEXT NOT NULL,
            index_identity TEXT NOT NULL,
            revision INTEGER NOT NULL CHECK (revision >= 0),
            updated_at TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
            PRIMARY KEY (test_case_id, index_identity)
        );

        CREATE TABLE IF NOT EXISTS ct_generations (
            build_output_root TEXT NOT NULL,
            generation_id TEXT NOT NULL,
            state TEXT NOT NULL CHECK (state IN ('allocated', 'complete', 'reap_eligible', 'reaped')),
            owner_token TEXT NOT NULL,
            allocated_at TEXT NOT NULL,
            completed_at TEXT,
            PRIMARY KEY (build_output_root, generation_id)
        );

        CREATE TABLE IF NOT EXISTS ct_generation_reap_debt (
            build_output_root TEXT NOT NULL,
            directory_name TEXT NOT NULL,
            bytes INTEGER NOT NULL,
            first_failed_at TEXT NOT NULL,
            last_failed_at TEXT NOT NULL,
            PRIMARY KEY (build_output_root, directory_name)
        );

        CREATE TABLE IF NOT EXISTS ct_generation_disk (
            build_output_root TEXT PRIMARY KEY,
            bytes INTEGER NOT NULL,
            stale INTEGER NOT NULL DEFAULT 0,
            measured_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ct_generation_pressure (
            id INTEGER PRIMARY KEY CHECK (id = 1),
            budget_bytes INTEGER NOT NULL,
            roots_total INTEGER NOT NULL,
            roots_measured INTEGER NOT NULL,
            evaluated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ct_coverage_maps (
            map_id TEXT PRIMARY KEY,
            workspace_id TEXT NOT NULL,
            index_identity TEXT NOT NULL,
            revision INTEGER NOT NULL,
            test_case_id TEXT NOT NULL REFERENCES test_cases(id) ON DELETE CASCADE,
            project_path TEXT NOT NULL,
            run_id TEXT NOT NULL,
            generation_id TEXT NOT NULL,
            revision_at_start TEXT,
            start_converged INTEGER NOT NULL,
            revision_at_end TEXT,
            end_converged INTEGER NOT NULL,
            complete INTEGER NOT NULL,
            failure_reason TEXT,
            granularity TEXT NOT NULL CHECK (granularity IN ('test', 'class')),
            valid_through_revision TEXT,
            invalidated_at_revision TEXT,
            recorded_at TEXT NOT NULL,
            source TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS ct_coverage_map_files (
            map_id TEXT NOT NULL REFERENCES ct_coverage_maps(map_id) ON DELETE CASCADE,
            workspace_id TEXT NOT NULL,
            index_identity TEXT NOT NULL,
            revision INTEGER NOT NULL,
            file_path TEXT NOT NULL,
            content_hash TEXT,
            PRIMARY KEY (map_id, file_path)
        );

        CREATE TABLE IF NOT EXISTS ct_coverage_delta_receipts (
            workspace_id TEXT NOT NULL,
            index_identity TEXT NOT NULL,
            revision INTEGER NOT NULL,
            from_revision TEXT NOT NULL,
            to_revision TEXT NOT NULL,
            changed_paths_digest TEXT NOT NULL,
            applied_at TEXT NOT NULL,
            PRIMARY KEY (workspace_id, index_identity, from_revision, to_revision)
        );

        CREATE TABLE IF NOT EXISTS ct_coverage_delta_map_applications (
            workspace_id TEXT NOT NULL,
            index_identity TEXT NOT NULL,
            revision INTEGER NOT NULL,
            from_revision TEXT NOT NULL,
            to_revision TEXT NOT NULL,
            map_id TEXT NOT NULL REFERENCES ct_coverage_maps(map_id) ON DELETE CASCADE,
            PRIMARY KEY (workspace_id, index_identity, from_revision, to_revision, map_id),
            FOREIGN KEY (workspace_id, index_identity, from_revision, to_revision)
                REFERENCES ct_coverage_delta_receipts(workspace_id, index_identity, from_revision, to_revision)
                ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS ct_coverage_maintenance_state (
            workspace_id TEXT PRIMARY KEY,
            next_offer_sequence INTEGER NOT NULL CHECK (next_offer_sequence > 0)
        );

        CREATE TABLE IF NOT EXISTS ct_coverage_project_offers (
            workspace_id TEXT NOT NULL,
            project_path TEXT NOT NULL,
            last_offer_sequence INTEGER NOT NULL CHECK (last_offer_sequence > 0),
            PRIMARY KEY (workspace_id, project_path)
        );

        CREATE UNIQUE INDEX IF NOT EXISTS ux_ct_test_projects_workspace_project_path
            ON ct_test_projects(workspace_id, project_path);
        CREATE UNIQUE INDEX IF NOT EXISTS idx_ct_coverage_maps_test_case
            ON ct_coverage_maps(workspace_id, test_case_id);

        CREATE INDEX IF NOT EXISTS idx_test_suites_workspace_id ON test_suites(workspace_id);
        CREATE INDEX IF NOT EXISTS idx_test_cases_workspace_id ON test_cases(workspace_id);
        CREATE INDEX IF NOT EXISTS idx_test_cases_file_path ON test_cases(file_path);
        CREATE INDEX IF NOT EXISTS idx_test_runs_workspace_revision ON test_runs(workspace_id, index_identity, revision);
        CREATE INDEX IF NOT EXISTS idx_test_results_workspace_revision ON test_results(workspace_id, index_identity, revision);
        CREATE INDEX IF NOT EXISTS idx_test_results_test_case_id ON test_results(test_case_id);
        CREATE INDEX IF NOT EXISTS idx_test_results_test_run_id ON test_results(test_run_id);
        CREATE INDEX IF NOT EXISTS idx_coverage_files_workspace_revision ON coverage_files(workspace_id, index_identity, revision);
        CREATE INDEX IF NOT EXISTS idx_coverage_spans_file_id ON coverage_spans(coverage_file_id);
        CREATE INDEX IF NOT EXISTS idx_coverage_spans_file_path ON coverage_spans(file_path);
        CREATE INDEX IF NOT EXISTS idx_test_links_workspace_id ON test_links(workspace_id);
        CREATE INDEX IF NOT EXISTS idx_test_links_test_case_id ON test_links(test_case_id);
        CREATE INDEX IF NOT EXISTS idx_ct_test_states_workspace_state ON ct_test_states(workspace_id, state, test_case_id);
        CREATE INDEX IF NOT EXISTS idx_ct_test_states_freshness ON ct_test_states(workspace_id, index_identity, revision);
        CREATE INDEX IF NOT EXISTS idx_ct_case_fresh_watermarks_workspace ON ct_case_fresh_watermarks(workspace_id, test_case_id);
        CREATE INDEX IF NOT EXISTS idx_ct_generations_state ON ct_generations(state, build_output_root);
        CREATE INDEX IF NOT EXISTS idx_ct_generations_owner ON ct_generations(owner_token, state);
        CREATE INDEX IF NOT EXISTS idx_ct_coverage_maps_project ON ct_coverage_maps(workspace_id, project_path, recorded_at);
        CREATE INDEX IF NOT EXISTS idx_ct_coverage_maps_freshness ON ct_coverage_maps(workspace_id, index_identity, revision);
        CREATE INDEX IF NOT EXISTS idx_ct_coverage_map_files_file ON ct_coverage_map_files(workspace_id, file_path);
        CREATE INDEX IF NOT EXISTS idx_ct_coverage_delta_applications_map ON ct_coverage_delta_map_applications(map_id);
        CREATE INDEX IF NOT EXISTS idx_ct_coverage_project_offers_sequence
            ON ct_coverage_project_offers(workspace_id, last_offer_sequence, project_path);
        CREATE INDEX IF NOT EXISTS idx_confidence_snapshots_subject
            ON confidence_snapshots(workspace_id, subject_type, subject_id);
        """;

    public static string DbPathFor(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        return Path.Combine(workspaceRoot, MillerDirectoryName, DbFileName);
    }

    public static void Apply(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys=ON;";
            foreignKeys.ExecuteNonQuery();
        }

        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }

        using (var ddl = connection.CreateCommand())
        {
            ddl.CommandText = Ddl;
            ddl.ExecuteNonQuery();
        }

        using var meta = connection.CreateCommand();
        meta.CommandText = "INSERT OR IGNORE INTO meta(key, value) VALUES ('schema_version', $v);";
        meta.Parameters.AddWithValue("$v", SchemaVersion.ToString(CultureInfo.InvariantCulture));
        meta.ExecuteNonQuery();
    }

    public static int? ReadSchemaVersion(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE key = 'schema_version';";
        try
        {
            object? raw = command.ExecuteScalar();
            if (raw is null or DBNull)
                return null;
            return int.TryParse(
                Convert.ToString(raw, CultureInfo.InvariantCulture),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int version)
                ? version
                : null;
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    public static bool IsNewerSchema(SqliteConnection connection) =>
        ReadSchemaVersion(connection) is int version && version > SchemaVersion;
}
