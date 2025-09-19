import { describe, it, expect, beforeEach, afterEach } from 'bun:test';
import { unlink } from 'fs/promises';
import { existsSync } from 'fs';
import { CodeIntelDB } from '../../database/schema.js';
import { MillerPaths } from '../../utils/miller-paths.js';
import { initializeLogger } from '../../utils/logger.js';

describe('Database Schema CRUD Operations', () => {
  let db: CodeIntelDB;
  let tempDbPath: string;
  let paths: MillerPaths;

  // Helper function to insert symbols with correct parameter count
  const insertTestSymbol = (
    name: string,
    kind: string,
    filePath: string,
    startLine: number,
    startColumn: number,
    endLine: number,
    endColumn: number,
    signature: string = '',
    docComment: string = '',
    language: string = 'typescript',
    visibility: string = 'public',
    parentId: string | null = null
  ) => {
    const symbolId = `symbol_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
    db.insertSymbol.run(
      symbolId,
      name,
      kind,
      language,
      filePath,
      startLine,
      startColumn,
      endLine,
      endColumn,
      startLine * 100, // start_byte (estimated)
      endLine * 100 + endColumn, // end_byte (estimated)
      signature,
      docComment,
      visibility,
      parentId,
      JSON.stringify({}) // metadata
    );
    return symbolId; // Return the string ID we used
  };

  // Helper function to insert relationships with correct parameter count
  const insertTestRelationship = (
    fromSymbolId: string,
    toSymbolId: string,
    relationshipKind: string,
    filePath: string,
    lineNumber: number,
    confidence: number = 0.9,
    metadata: any = {}
  ) => {
    return db.insertRelationship.run(
      fromSymbolId,
      toSymbolId,
      relationshipKind,
      filePath,
      lineNumber,
      confidence,
      JSON.stringify(metadata)
    ).lastInsertRowid as number;
  };

  // Helper function to insert types with correct parameter count
  const insertTestType = (
    symbolId: string,
    resolvedType: string,
    genericParams: string = '',
    constraints: string = '',
    isInferred: boolean = false,
    language: string = 'typescript',
    metadata: any = {}
  ) => {
    db.insertType.run(
      symbolId,
      resolvedType,
      genericParams,
      constraints,
      isInferred ? 1 : 0,
      language,
      JSON.stringify(metadata)
    );
    return symbolId; // Return the symbol ID since types table uses symbol_id as PRIMARY KEY
  };

  // Helper function to insert bindings with correct parameter count
  const insertTestBinding = (
    sourceSymbolId: string,
    targetSymbolId: string,
    bindingKind: string,
    sourceLanguage: string,
    targetLanguage: string,
    endpoint: string,
    metadata: any = {}
  ) => {
    return db.insertBinding.run(
      sourceSymbolId,
      targetSymbolId,
      bindingKind,
      sourceLanguage,
      targetLanguage,
      endpoint,
      JSON.stringify(metadata)
    ).lastInsertRowid as number;
  };

  beforeEach(async () => {
    // Create a temporary database for testing
    const testDir = `/tmp/miller-test-${Date.now()}`;
    paths = new MillerPaths(testDir);
    await paths.ensureDirectories();
    initializeLogger(paths);

    tempDbPath = paths.getDatabasePath();
    db = new CodeIntelDB(paths);
    await db.initialize();

    // Enable foreign key constraints for testing
    db['db'].run('PRAGMA foreign_keys = ON');
  });

  afterEach(async () => {
    // Clean up
    if (db) {
      await db.close();
    }
    // Clean up the entire test directory
    const testDir = paths.getWorkspaceRoot();
    if (existsSync(testDir)) {
      await Bun.spawn(['rm', '-rf', testDir]).exited;
    }
  });

  describe('Files Table CRUD', () => {
    it('should insert and retrieve file records', () => {
      const filePath = '/test/file.ts';
      const language = 'typescript';
      const lastModified = Date.now();
      const size = 1024;
      const hash = 'abc123';
      const parseTime = 50;

      // Insert file
      db.insertFile.run(filePath, language, lastModified, size, hash, parseTime);

      // Retrieve file
      const file = db['db'].prepare('SELECT * FROM files WHERE path = ?').get(filePath) as any;

      expect(file).toBeDefined();
      expect(file.path).toBe(filePath);
      expect(file.language).toBe(language);
      expect(file.last_modified).toBe(lastModified);
      expect(file.size).toBe(size);
      expect(file.hash).toBe(hash);
      expect(file.parse_time_ms).toBe(parseTime);
    });

    it('should update file records', () => {
      const filePath = '/test/file.ts';

      // Insert initial record
      db.insertFile.run(filePath, 'typescript', 100, 1024, 'hash1', 50);

      // Update record
      db.insertFile.run(filePath, 'typescript', 200, 2048, 'hash2', 75);

      // Verify update
      const file = db['db'].prepare('SELECT * FROM files WHERE path = ?').get(filePath) as any;
      expect(file.last_modified).toBe(200);
      expect(file.size).toBe(2048);
      expect(file.hash).toBe('hash2');
      expect(file.parse_time_ms).toBe(75);
    });

    it('should delete file records and cascade to symbols', () => {
      const filePath = '/test/file.ts';

      // Insert file and symbol
      db.insertFile.run(filePath, 'typescript', 100, 1024, 'hash1', 50);

      const symbolId = insertTestSymbol(
        'testFunction',
        'function',
        filePath,
        1, 0, 1, 10,
        'function testFunction() {}',
        ''
      );

      // Verify symbol exists
      let symbol = db['db'].prepare('SELECT * FROM symbols WHERE id = ?').get(symbolId);
      expect(symbol).toBeDefined();

      // Clear file data (should cascade delete symbols)
      db.clearFileData(filePath);

      // Verify symbol is deleted
      symbol = db['db'].prepare('SELECT * FROM symbols WHERE id = ?').get(symbolId);
      expect(symbol).toBeNull();

      // Verify file is deleted
      const file = db['db'].prepare('SELECT * FROM files WHERE path = ?').get(filePath);
      expect(file).toBeNull();
    });
  });

  describe('Symbols Table CRUD', () => {
    it('should insert and retrieve symbol records', () => {
      const filePath = '/test/file.ts';

      // Insert file first
      db.insertFile.run(filePath, 'typescript', 100, 1024, 'hash1', 50);

      // Insert symbol
      const symbolId = insertTestSymbol(
        'testFunction',
        'function',
        filePath,
        1, 0, 5, 20,
        'function testFunction(): void {}',
        'A test function'
      );

      // Retrieve symbol
      const symbol = db['db'].prepare('SELECT * FROM symbols WHERE id = ?').get(symbolId) as any;

      expect(symbol).toBeDefined();
      expect(symbol.name).toBe('testFunction');
      expect(symbol.kind).toBe('function');
      expect(symbol.file_path).toBe(filePath);
      expect(symbol.start_line).toBe(1);
      expect(symbol.start_column).toBe(0);
      expect(symbol.end_line).toBe(5);
      expect(symbol.end_column).toBe(20);
      expect(symbol.signature).toBe('function testFunction(): void {}');
      expect(symbol.doc_comment).toBe('A test function');
      expect(symbol.language).toBe('typescript');
    });

    it('should find symbols by file path', () => {
      const filePath = '/test/file.ts';

      // Insert file
      db.insertFile.run(filePath, 'typescript', 100, 1024, 'hash1', 50);

      // Insert multiple symbols
      insertTestSymbol('func1', 'function', filePath, 1, 0, 1, 10, 'func1()');
      insertTestSymbol('func2', 'function', filePath, 2, 0, 2, 10, 'func2()');
      insertTestSymbol('class1', 'class', filePath, 3, 0, 3, 10, 'class1');

      // Find symbols by file
      const symbols = db['db'].prepare('SELECT * FROM symbols WHERE file_path = ?').all(filePath) as any[];

      expect(symbols).toHaveLength(3);
      expect(symbols.map(s => s.name).sort()).toEqual(['class1', 'func1', 'func2']);
    });

    it('should find symbols by kind', () => {
      const filePath = '/test/file.ts';

      // Insert file
      db.insertFile.run(filePath, 'typescript', 100, 1024, 'hash1', 50);

      // Insert symbols of different kinds
      insertTestSymbol('func1', 'function', filePath, 1, 0, 1, 10, 'func1()');
      insertTestSymbol('func2', 'function', filePath, 2, 0, 2, 10, 'func2()');
      insertTestSymbol('class1', 'class', filePath, 3, 0, 3, 10, 'class1');

      // Find functions
      const functions = db['db'].prepare('SELECT * FROM symbols WHERE kind = ?').all('function') as any[];
      expect(functions).toHaveLength(2);

      // Find classes
      const classes = db['db'].prepare('SELECT * FROM symbols WHERE kind = ?').all('class') as any[];
      expect(classes).toHaveLength(1);
    });
  });

  describe('Relationships Table CRUD', () => {
    it('should insert and retrieve relationships', () => {
      const filePath = '/test/file.ts';

      // Insert file
      db.insertFile.run(filePath, 'typescript', 100, 1024, 'hash1', 50);

      // Insert symbols
      const funcId = insertTestSymbol('func1', 'function', filePath, 1, 0, 1, 10, 'func1()');
      const classId = insertTestSymbol('class1', 'class', filePath, 2, 0, 2, 10, 'class1');

      // Insert relationship
      const relationshipId = insertTestRelationship(
        funcId,
        classId,
        'contains',
        filePath,
        1
      );

      // Retrieve relationship
      const relationship = db['db'].prepare('SELECT * FROM relationships WHERE id = ?').get(relationshipId) as any;

      expect(relationship).toBeDefined();
      expect(relationship.from_symbol_id).toBe(funcId);
      expect(relationship.to_symbol_id).toBe(classId);
      expect(relationship.relationship_kind).toBe('contains');
      expect(relationship.file_path).toBe(filePath);
      expect(relationship.line_number).toBe(1);
    });

    it('should find relationships by symbol', () => {
      const filePath = '/test/file.ts';

      // Insert file
      db.insertFile.run(filePath, 'typescript', 100, 1024, 'hash1', 50);

      // Insert symbols
      const funcId = insertTestSymbol('func1', 'function', filePath, 1, 0, 1, 10, 'func1()');
      const classId = insertTestSymbol('class1', 'class', filePath, 2, 0, 2, 10, 'class1');
      const varId = insertTestSymbol('var1', 'variable', filePath, 3, 0, 3, 10, 'var1');

      // Insert relationships
      insertTestRelationship(funcId, classId, 'calls', filePath, 1);
      insertTestRelationship(funcId, varId, 'uses', filePath, 2);

      // Find outgoing relationships from func1
      const outgoing = db['db'].prepare(
        'SELECT * FROM relationships WHERE from_symbol_id = ?'
      ).all(funcId) as any[];

      expect(outgoing).toHaveLength(2);
      expect(outgoing.map(r => r.relationship_kind).sort()).toEqual(['calls', 'uses']);

      // Find incoming relationships to classId
      const incoming = db['db'].prepare(
        'SELECT * FROM relationships WHERE to_symbol_id = ?'
      ).all(classId) as any[];

      expect(incoming).toHaveLength(1);
      expect(incoming[0].relationship_kind).toBe('calls');
    });
  });

  describe('Types Table CRUD', () => {
    it('should insert and retrieve type information', () => {
      const filePath = '/test/file.ts';

      // Insert file and symbol
      db.insertFile.run(filePath, 'typescript', 100, 1024, 'hash1', 50);
      const symbolId = insertTestSymbol('func1', 'function', filePath, 1, 0, 1, 10, 'func1()');

      // Insert type
      const typeId = insertTestType(
        symbolId,
        'string'
      );

      // Retrieve type
      const type = db['db'].prepare('SELECT * FROM types WHERE symbol_id = ?').get(symbolId) as any;

      expect(type).toBeDefined();
      expect(type.symbol_id).toBe(symbolId);
      expect(type.resolved_type).toBe('string');
    });

    it('should find types by symbol', () => {
      const filePath = '/test/file.ts';

      // Insert file and symbol
      db.insertFile.run(filePath, 'typescript', 100, 1024, 'hash1', 50);
      const symbolId = insertTestSymbol('func1', 'function', filePath, 1, 0, 1, 10, 'func1()');

      // Insert type for the symbol (only one type per symbol due to PRIMARY KEY)
      insertTestType(symbolId, 'string');

      // Find type by symbol
      const type = db['db'].prepare('SELECT * FROM types WHERE symbol_id = ?').get(symbolId) as any;

      expect(type).toBeDefined();
      expect(type.resolved_type).toBe('string');
    });
  });

  describe('Bindings Table CRUD', () => {
    it('should insert and retrieve cross-language bindings', () => {
      const filePath = '/test/file.ts';

      // Insert file and symbols
      db.insertFile.run(filePath, 'typescript', 100, 1024, 'hash1', 50);
      const sourceSymbolId = insertTestSymbol('func1', 'function', filePath, 1, 0, 1, 10, 'func1()');
      const targetSymbolId = insertTestSymbol('external_func', 'function', filePath, 2, 0, 2, 10, 'external_func()');

      // Insert binding
      const bindingId = insertTestBinding(
        sourceSymbolId,
        targetSymbolId,
        'ffi',
        'typescript',
        'C',
        'lib.so'
      );

      // Retrieve binding
      const binding = db['db'].prepare('SELECT * FROM bindings WHERE id = ?').get(bindingId) as any;

      expect(binding).toBeDefined();
      expect(binding.source_symbol_id).toBe(sourceSymbolId);
      expect(binding.target_symbol_id).toBe(targetSymbolId);
      expect(binding.binding_kind).toBe('ffi');
      expect(binding.source_language).toBe('typescript');
      expect(binding.target_language).toBe('C');
      expect(binding.endpoint).toBe('lib.so');
    });

    it('should find bindings by language', () => {
      const filePath = '/test/file.ts';

      // Insert file and symbols
      db.insertFile.run(filePath, 'typescript', 100, 1024, 'hash1', 50);
      const symbolId1 = insertTestSymbol('func1', 'function', filePath, 1, 0, 1, 10, 'func1()');
      const symbolId2 = insertTestSymbol('func2', 'function', filePath, 2, 0, 2, 10, 'func2()');
      const cSymbolId = insertTestSymbol('c_func', 'function', filePath, 3, 0, 3, 10, 'c_func()');
      const pySymbolId = insertTestSymbol('py_func', 'function', filePath, 4, 0, 4, 10, 'py_func()');

      // Insert bindings to different languages
      insertTestBinding(symbolId1, cSymbolId, 'ffi', 'typescript', 'C', 'lib.so');
      insertTestBinding(symbolId2, pySymbolId, 'api', 'typescript', 'Python', 'module.py');

      // Find C bindings
      const cBindings = db['db'].prepare('SELECT * FROM bindings WHERE target_language = ?').all('C') as any[];
      expect(cBindings).toHaveLength(1);
      expect(cBindings[0].target_symbol_id).toBe(cSymbolId);

      // Find Python bindings
      const pyBindings = db['db'].prepare('SELECT * FROM bindings WHERE target_language = ?').all('Python') as any[];
      expect(pyBindings).toHaveLength(1);
      expect(pyBindings[0].target_symbol_id).toBe(pySymbolId);
    });
  });

  describe('Database Statistics', () => {
    it('should provide accurate statistics', () => {
      const filePath = '/test/file.ts';

      // Insert test data
      db.insertFile.run(filePath, 'typescript', 100, 1024, 'hash1', 50);
      const symbolId1 = insertTestSymbol('func1', 'function', filePath, 1, 0, 1, 10, 'func1()');
      const symbolId2 = insertTestSymbol('class1', 'class', filePath, 2, 0, 2, 10, 'class1');

      insertTestRelationship(symbolId1, symbolId2, 'calls', filePath, 1);
      insertTestType(symbolId1, 'string');
      const cSymbolId = insertTestSymbol('c_func', 'function', filePath, 3, 0, 3, 10, 'c_func()');
      insertTestBinding(symbolId1, cSymbolId, 'ffi', 'typescript', 'C', 'lib.so');

      // Get statistics
      const stats = db.getStats();

      expect(stats.files).toBe(1);
      expect(stats.symbols).toBe(3); // func1, class1, c_func
      expect(stats.relationships).toBe(1);
    });
  });

  describe('Transactions and Error Handling', () => {
    it('should handle invalid foreign key references', () => {
      // Try to insert symbol with invalid parent_id (should fail with foreign key constraint)
      expect(() => {
        const symbolId = `symbol_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
        db['db'].run(`
          INSERT INTO symbols
          (id, name, kind, language, file_path, start_line, start_column,
           end_line, end_column, start_byte, end_byte, signature,
           doc_comment, visibility, parent_id, metadata)
          VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        `, symbolId, 'func1', 'function', 'typescript', '/test/file.ts',
           1, 0, 1, 10, 100, 110, 'func1()', '', 'public', 'nonexistent_parent_id', '{}');
      }).toThrow();
    });

    it('should handle duplicate file insertions', () => {
      const filePath = '/test/file.ts';

      // Insert file twice (should update, not create duplicate)
      db.insertFile.run(filePath, 'typescript', 100, 1024, 'hash1', 50);
      db.insertFile.run(filePath, 'typescript', 200, 2048, 'hash2', 75);

      // Should only have one file record
      const files = db['db'].prepare('SELECT COUNT(*) as count FROM files WHERE path = ?').get(filePath) as any;
      expect(files.count).toBe(1);
    });

    it('should handle cascading deletes correctly', () => {
      const filePath = '/test/file.ts';

      // Insert complex data structure
      db.insertFile.run(filePath, 'typescript', 100, 1024, 'hash1', 50);
      const symbolId = insertTestSymbol('func1', 'function', filePath, 1, 0, 1, 10, 'func1()');

      insertTestRelationship(symbolId, symbolId, 'self', filePath, 1);
      insertTestType(symbolId, 'string');
      const targetSymbolId = insertTestSymbol('c_func', 'function', filePath, 2, 0, 2, 10, 'c_func()');
      insertTestBinding(symbolId, targetSymbolId, 'ffi', 'typescript', 'C', 'lib.so');

      // Delete file (should cascade)
      db.clearFileData(filePath);

      // Verify all related data is deleted
      const symbols = db['db'].prepare('SELECT COUNT(*) as count FROM symbols').get() as any;
      const relationships = db['db'].prepare('SELECT COUNT(*) as count FROM relationships').get() as any;
      const types = db['db'].prepare('SELECT COUNT(*) as count FROM types').get() as any;
      const bindings = db['db'].prepare('SELECT COUNT(*) as count FROM bindings').get() as any;

      expect(symbols.count).toBe(0);
      expect(relationships.count).toBe(0);
      expect(types.count).toBe(0);
      expect(bindings.count).toBe(0);
    });
  });
});