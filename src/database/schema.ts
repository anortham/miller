import { Database } from 'bun:sqlite';
import { MillerPaths } from '../utils/miller-paths.js';
import { log, LogLevel } from '../utils/logger.js';

export class CodeIntelDB {
  private db: Database;
  private dbPath: string;

  constructor(paths: MillerPaths) {
    this.dbPath = paths.getDatabasePath();

    // Ensure parent directory exists before creating database
    const dir = require('path').dirname(this.dbPath);
    if (!require('fs').existsSync(dir)) {
      require('fs').mkdirSync(dir, { recursive: true });
    }

    this.db = new Database(this.dbPath);
    this.initialize();
  }

  private initialize() {
    log.database(LogLevel.INFO, `Initializing database at ${this.dbPath}`);

    // Core symbols table - stores all code entities
    this.db.run(`
      CREATE TABLE IF NOT EXISTS symbols (
        id TEXT PRIMARY KEY,
        name TEXT NOT NULL,
        kind TEXT NOT NULL,           -- 'class', 'function', 'variable', etc.
        language TEXT NOT NULL,
        file_path TEXT NOT NULL,
        start_line INTEGER NOT NULL,
        start_column INTEGER NOT NULL,
        end_line INTEGER NOT NULL,
        end_column INTEGER NOT NULL,
        start_byte INTEGER NOT NULL,   -- For incremental updates
        end_byte INTEGER NOT NULL,
        signature TEXT,                -- Type signature
        doc_comment TEXT,              -- Documentation
        visibility TEXT,               -- 'public', 'private', etc.
        parent_id TEXT,
        metadata JSON,                 -- Language-specific data
        FOREIGN KEY(parent_id) REFERENCES symbols(id) ON DELETE CASCADE
      )
    `);

    // Relationships between symbols (calls, extends, implements, etc.)
    this.db.run(`
      CREATE TABLE IF NOT EXISTS relationships (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        from_symbol_id TEXT NOT NULL,
        to_symbol_id TEXT NOT NULL,
        relationship_kind TEXT NOT NULL,  -- 'calls', 'extends', 'implements', 'uses', 'returns'
        file_path TEXT NOT NULL,
        line_number INTEGER NOT NULL,
        confidence REAL DEFAULT 1.0,      -- For inferred relationships
        metadata JSON,
        FOREIGN KEY(from_symbol_id) REFERENCES symbols(id) ON DELETE CASCADE,
        FOREIGN KEY(to_symbol_id) REFERENCES symbols(id) ON DELETE CASCADE
      )
    `);

    // Type information
    this.db.run(`
      CREATE TABLE IF NOT EXISTS types (
        symbol_id TEXT PRIMARY KEY,
        resolved_type TEXT NOT NULL,      -- Fully resolved type
        generic_params JSON,               -- Generic/template parameters
        constraints JSON,                  -- Type constraints
        is_inferred BOOLEAN DEFAULT FALSE,
        language TEXT NOT NULL,
        metadata JSON,
        FOREIGN KEY(symbol_id) REFERENCES symbols(id) ON DELETE CASCADE
      )
    `);

    // Cross-language bindings (API calls, FFI, etc.)
    this.db.run(`
      CREATE TABLE IF NOT EXISTS bindings (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        source_symbol_id TEXT NOT NULL,
        target_symbol_id TEXT,
        binding_kind TEXT NOT NULL,       -- 'rest_api', 'grpc', 'ffi', 'graphql'
        source_language TEXT NOT NULL,
        target_language TEXT,
        endpoint TEXT,                     -- For API calls
        metadata JSON,
        FOREIGN KEY(source_symbol_id) REFERENCES symbols(id) ON DELETE CASCADE,
        FOREIGN KEY(target_symbol_id) REFERENCES symbols(id) ON DELETE CASCADE
      )
    `);

    // File metadata for incremental updates
    this.db.run(`
      CREATE TABLE IF NOT EXISTS files (
        path TEXT PRIMARY KEY,
        language TEXT NOT NULL,
        last_modified INTEGER NOT NULL,
        size INTEGER NOT NULL,
        hash TEXT NOT NULL,
        parse_time_ms INTEGER
      )
    `);

    // Create indexes for performance
    this.db.run(`
      CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);
      CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_path);
      CREATE INDEX IF NOT EXISTS idx_symbols_kind ON symbols(kind);
      CREATE INDEX IF NOT EXISTS idx_symbols_parent ON symbols(parent_id);
      CREATE INDEX IF NOT EXISTS idx_relationships_from ON relationships(from_symbol_id);
      CREATE INDEX IF NOT EXISTS idx_relationships_to ON relationships(to_symbol_id);
      CREATE INDEX IF NOT EXISTS idx_types_language ON types(language);
    `);

    // Workspace tracking table
    this.db.run(`
      CREATE TABLE IF NOT EXISTS workspaces (
        path TEXT PRIMARY KEY,
        last_indexed DATETIME DEFAULT CURRENT_TIMESTAMP,
        symbol_count INTEGER DEFAULT 0,
        file_count INTEGER DEFAULT 0,
        metadata JSON
      )
    `);

    // FTS5 table for code search
    this.db.run(`
      CREATE VIRTUAL TABLE IF NOT EXISTS code_search USING fts5(
        symbol_id UNINDEXED,
        name,
        content,
        file_path UNINDEXED,
        tokenize = 'porter ascii'
      )
    `);

    log.database(LogLevel.INFO, 'Database schema initialized successfully');
  }

  // Add getters for prepared statements (for performance)
  get insertSymbol() {
    return this.db.prepare(`
      INSERT OR REPLACE INTO symbols
      (id, name, kind, language, file_path, start_line, start_column,
       end_line, end_column, start_byte, end_byte, signature,
       doc_comment, visibility, parent_id, metadata)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    `);
  }

  get insertRelationship() {
    return this.db.prepare(`
      INSERT INTO relationships
      (from_symbol_id, to_symbol_id, relationship_kind, file_path,
       line_number, confidence, metadata)
      VALUES (?, ?, ?, ?, ?, ?, ?)
    `);
  }

  get insertType() {
    return this.db.prepare(`
      INSERT OR REPLACE INTO types
      (symbol_id, resolved_type, generic_params, constraints,
       is_inferred, language, metadata)
      VALUES (?, ?, ?, ?, ?, ?, ?)
    `);
  }

  get insertBinding() {
    return this.db.prepare(`
      INSERT INTO bindings
      (source_symbol_id, target_symbol_id, binding_kind, source_language,
       target_language, endpoint, metadata)
      VALUES (?, ?, ?, ?, ?, ?, ?)
    `);
  }

  get insertFile() {
    return this.db.prepare(`
      INSERT OR REPLACE INTO files
      (path, language, last_modified, size, hash, parse_time_ms)
      VALUES (?, ?, ?, ?, ?, ?)
    `);
  }

  get insertSearchEntry() {
    return this.db.prepare(`
      INSERT INTO code_search
      (symbol_id, name, content, file_path)
      VALUES (?, ?, ?, ?)
    `);
  }

  // Query methods
  findSymbolAtPosition(filePath: string, line: number, column: number) {
    return this.db.prepare(`
      SELECT * FROM symbols
      WHERE file_path = ?
        AND start_line <= ?
        AND end_line >= ?
        AND start_column <= ?
        AND end_column >= ?
      ORDER BY (end_line - start_line) * (end_column - start_column) ASC
      LIMIT 1
    `).get(filePath, line, line, column, column);
  }

  findSymbolsByName(name: string, limit = 50) {
    return this.db.prepare(`
      SELECT * FROM symbols
      WHERE name LIKE ?
      ORDER BY name
      LIMIT ?
    `).all(`%${name}%`, limit);
  }

  findReferences(symbolId: string) {
    return this.db.prepare(`
      SELECT
        s.file_path,
        s.start_line,
        s.start_column,
        s.name,
        r.relationship_kind
      FROM relationships r
      JOIN symbols s ON s.id = r.from_symbol_id
      WHERE r.to_symbol_id = ?
        AND r.relationship_kind IN ('calls', 'uses', 'references')
    `).all(symbolId);
  }

  findSymbolById(symbolId: string) {
    return this.db.prepare(`
      SELECT * FROM symbols WHERE id = ?
    `).get(symbolId);
  }

  findTypeInfo(symbolId: string) {
    return this.db.prepare(`
      SELECT * FROM types WHERE symbol_id = ?
    `).get(symbolId);
  }

  searchSymbols(query: string, limit = 50) {
    return this.db.prepare(`
      SELECT
        s.id,
        s.name,
        s.kind,
        s.file_path,
        s.start_line,
        s.signature,
        cs.rank
      FROM code_search cs
      JOIN symbols s ON s.id = cs.symbol_id
      WHERE code_search MATCH ?
      ORDER BY cs.rank
      LIMIT ?
    `).all(query, limit);
  }

  clearFileData(filePath: string) {
    // Delete all symbols and related data for a file
    this.db.run('DELETE FROM symbols WHERE file_path = ?', filePath);
    this.db.run('DELETE FROM relationships WHERE file_path = ?', filePath);
    this.db.run('DELETE FROM files WHERE path = ?', filePath);
    // Remove from search index
    this.db.run(`
      DELETE FROM code_search
      WHERE file_path = ?
    `, filePath);
  }

  // Transaction wrapper for bulk operations
  transaction<T>(fn: (db: Database) => T): T {
    return this.db.transaction(fn)(this.db);
  }

  // Get database statistics
  getStats() {
    const symbolCount = this.db.prepare('SELECT COUNT(*) as count FROM symbols').get() as { count: number };
    const fileCount = this.db.prepare('SELECT COUNT(*) as count FROM files').get() as { count: number };
    const relationshipCount = this.db.prepare('SELECT COUNT(*) as count FROM relationships').get() as { count: number };

    return {
      symbols: symbolCount.count,
      files: fileCount.count,
      relationships: relationshipCount.count
    };
  }

  // Workspace management methods
  recordWorkspace(workspacePath: string) {
    this.db.prepare(`
      INSERT OR REPLACE INTO workspaces (path, last_indexed)
      VALUES (?, CURRENT_TIMESTAMP)
    `).run(workspacePath);
  }

  updateWorkspaceStats(workspacePath: string, symbolCount: number, fileCount: number) {
    this.db.prepare(`
      UPDATE workspaces
      SET symbol_count = ?, file_count = ?, last_indexed = CURRENT_TIMESTAMP
      WHERE path = ?
    `).run(symbolCount, fileCount, workspacePath);
  }

  getWorkspaces() {
    return this.db.prepare(`
      SELECT
        path,
        last_indexed,
        symbol_count,
        file_count
      FROM workspaces
      ORDER BY last_indexed DESC
    `).all();
  }

  removeWorkspace(workspacePath: string): boolean {
    // Remove workspace record
    const workspaceResult = this.db.prepare('DELETE FROM workspaces WHERE path = ?').run(workspacePath);

    // Remove all symbols/relationships for this workspace
    this.db.prepare('DELETE FROM symbols WHERE file_path LIKE ?').run(`${workspacePath}%`);

    return workspaceResult.changes > 0;
  }

  // Close database connection
  close() {
    this.db.close();
  }
}