import { describe, it, expect, beforeAll } from 'bun:test';
import { TypeScriptExtractor } from '../../extractors/typescript-extractor.js';
import { ParserManager } from '../../parser/parser-manager.js';
import { initializeLogger, LogLevel } from '../../utils/logger.js';
import { MillerPaths } from '../../utils/miller-paths.js';

describe('TypeScript Symbol Positioning', () => {
  let parserManager: ParserManager;

  beforeAll(async () => {
    // Initialize logger for tests
    const tempPaths = new MillerPaths('/tmp/miller-typescript-positioning-test');
    await tempPaths.ensureDirectories();
    initializeLogger(tempPaths, LogLevel.WARN); // Quiet for tests

    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Class Declaration Positioning', () => {
    it('should position class symbol at the class name, not the entire class body', async () => {
      const code = `export class FileWatcher {
  private watchers = new Map();

  constructor() {
    // implementation
  }
}`;

      const parseResult = await parserManager.parseFile('test.ts', code);
      const extractor = new TypeScriptExtractor('typescript', 'test.ts', code);
      const symbols = extractor.extractSymbols(parseResult.tree);

      // Find the class symbol
      const classSymbol = symbols.find(s => s.name === 'FileWatcher' && s.kind === 'class');
      expect(classSymbol).toBeDefined();

      // Class name "FileWatcher" starts at column 13 in "export class FileWatcher"
      expect(classSymbol!.startLine).toBe(1);
      expect(classSymbol!.startColumn).toBe(13); // Start of "FileWatcher"
      expect(classSymbol!.endColumn).toBe(24);   // End of "FileWatcher"

      // Should NOT span the entire class body
      expect(classSymbol!.endLine).toBe(1); // Should be on the same line as the name
    });

    it('should position interface symbol at the interface name', async () => {
      const code = `export interface FileWatcherOptions {
  debounceMs?: number;
  recursive?: boolean;
}`;

      const parseResult = await parserManager.parseFile('test.ts', code);
      const extractor = new TypeScriptExtractor('typescript', 'test.ts', code);
      const symbols = extractor.extractSymbols(parseResult.tree);

      const interfaceSymbol = symbols.find(s => s.name === 'FileWatcherOptions' && s.kind === 'interface');
      expect(interfaceSymbol).toBeDefined();

      // Interface name "FileWatcherOptions" starts at column 17 in "export interface FileWatcherOptions"
      expect(interfaceSymbol!.startLine).toBe(1);
      expect(interfaceSymbol!.startColumn).toBe(17);
      expect(interfaceSymbol!.endColumn).toBe(35); // "FileWatcherOptions" (18 chars) from column 17 ends at 35
      expect(interfaceSymbol!.endLine).toBe(1);
    });

    it('should position function symbol at the function name', async () => {
      const code = `export function createWatcher(options: Options) {
  return new FileWatcher(options);
}`;

      const parseResult = await parserManager.parseFile('test.ts', code);
      const extractor = new TypeScriptExtractor('typescript', 'test.ts', code);
      const symbols = extractor.extractSymbols(parseResult.tree);

      const functionSymbol = symbols.find(s => s.name === 'createWatcher' && s.kind === 'function');
      expect(functionSymbol).toBeDefined();

      // Function name "createWatcher" starts at column 16 in "export function createWatcher"
      expect(functionSymbol!.startLine).toBe(1);
      expect(functionSymbol!.startColumn).toBe(16);
      expect(functionSymbol!.endColumn).toBe(29);
      expect(functionSymbol!.endLine).toBe(1);
    });

    it('should enable precise go-to-definition at class name position', async () => {
      const code = `export class FileWatcher {
  constructor() {}
}`;

      const parseResult = await parserManager.parseFile('test.ts', code);
      const extractor = new TypeScriptExtractor('typescript', 'test.ts', code);
      const symbols = extractor.extractSymbols(parseResult.tree);

      const classSymbol = symbols.find(s => s.name === 'FileWatcher' && s.kind === 'class');
      expect(classSymbol).toBeDefined();

      // Simulate go-to-definition at the middle of "FileWatcher" (column 18)
      const testColumn = 18; // Middle of "FileWatcher"

      // Symbol should contain this position
      expect(classSymbol!.startColumn).toBeLessThanOrEqual(testColumn);
      expect(classSymbol!.endColumn).toBeGreaterThanOrEqual(testColumn);
      expect(classSymbol!.startLine).toBe(1);
      expect(classSymbol!.endLine).toBe(1);
    });
  });

  describe('Real FileWatcher.ts Test', () => {
    it('should correctly position FileWatcher class from actual file', async () => {
      // This tests the exact scenario that was failing
      const code = `export class FileWatcher {`;

      const parseResult = await parserManager.parseFile('test.ts', code);
      const extractor = new TypeScriptExtractor('typescript', 'test.ts', code);
      const symbols = extractor.extractSymbols(parseResult.tree);

      const classSymbol = symbols.find(s => s.name === 'FileWatcher' && s.kind === 'class');
      expect(classSymbol).toBeDefined();

      // In "export class FileWatcher", "FileWatcher" starts at column 13
      expect(classSymbol!.startColumn).toBe(13);
      expect(classSymbol!.endColumn).toBe(24); // "FileWatcher" is 11 characters
      expect(classSymbol!.startLine).toBe(1);
      expect(classSymbol!.endLine).toBe(1);
    });
  });
});