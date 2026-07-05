namespace Miller.Indexing;

public static class ContentCorpusSchema
{
    public const int SchemaVersion = 2;
    public const string ChunkerVersion = "line-v1";

    public const string SchemaDdl = """
        CREATE TABLE content_sources(
            source_id TEXT PRIMARY KEY,
            content_kind TEXT NOT NULL,
            workspace_id TEXT NULL,
            workspace_revision INTEGER NULL,
            path TEXT NULL,
            url TEXT NULL,
            display_path TEXT NOT NULL,
            language TEXT NOT NULL,
            content_hash TEXT NOT NULL,
            source_bytes INTEGER NOT NULL,
            line_count INTEGER NOT NULL,
            is_test INTEGER NOT NULL,
            status TEXT NOT NULL,
            indexed_at_utc TEXT NOT NULL);
        CREATE TABLE content_chunks(
            chunk_id TEXT PRIMARY KEY,
            source_id TEXT NOT NULL,
            content_kind TEXT NOT NULL,
            path TEXT NULL,
            url TEXT NULL,
            display_path TEXT NOT NULL,
            language TEXT NOT NULL,
            line_start INTEGER NOT NULL,
            line_end INTEGER NOT NULL,
            byte_start INTEGER NOT NULL,
            byte_end INTEGER NOT NULL,
            raw_text TEXT NOT NULL,
            doc_len INTEGER NOT NULL,
            is_test INTEGER NOT NULL,
            source_bytes INTEGER NOT NULL,
            containing_symbol_id TEXT NULL,
            containing_symbol_name TEXT NULL);
        CREATE TABLE content_symbol_spans(
            source_id TEXT NOT NULL,
            symbol_id TEXT NOT NULL,
            symbol_name TEXT NOT NULL,
            path TEXT NOT NULL,
            start_line INTEGER NOT NULL,
            end_line INTEGER NOT NULL,
            PRIMARY KEY(source_id, symbol_id));
        CREATE VIRTUAL TABLE content_fts USING fts5(
            chunk_id UNINDEXED, body, tokenize='unicode61 remove_diacritics 0');
        CREATE TABLE content_meta(
            schema_version INTEGER NOT NULL,
            workspace_revision INTEGER NULL,
            chunker_version TEXT NOT NULL,
            source_count INTEGER NOT NULL,
            chunk_count INTEGER NOT NULL,
            indexed_source_bytes INTEGER NOT NULL,
            stored_raw_bytes INTEGER NOT NULL,
            updated_at_utc TEXT NOT NULL,
            skipped_status INTEGER NOT NULL DEFAULT 0,
            skipped_scope INTEGER NOT NULL DEFAULT 0,
            skipped_large INTEGER NOT NULL DEFAULT 0,
            skipped_missing INTEGER NOT NULL DEFAULT 0,
            skipped_hash INTEGER NOT NULL DEFAULT 0,
            skipped_utf8 INTEGER NOT NULL DEFAULT 0,
            skipped_io INTEGER NOT NULL DEFAULT 0);
        CREATE INDEX ix_content_sources_kind ON content_sources(content_kind);
        CREATE INDEX ix_content_chunks_kind ON content_chunks(content_kind);
        CREATE INDEX ix_content_chunks_path ON content_chunks(path);
        CREATE INDEX ix_content_chunks_source_line ON content_chunks(source_id, line_start, chunk_id);
        CREATE INDEX ix_content_chunks_symbol_id
            ON content_chunks(content_kind, containing_symbol_id, display_path, line_start, chunk_id, is_test);
        CREATE INDEX ix_content_chunks_symbol_name
            ON content_chunks(content_kind, containing_symbol_name, containing_symbol_id, display_path, line_start, chunk_id, is_test);
        CREATE INDEX ix_content_symbol_spans_source ON content_symbol_spans(source_id, start_line, end_line);
        """;
}
