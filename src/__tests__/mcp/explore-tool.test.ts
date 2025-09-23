import { describe, it, expect, beforeAll, afterAll } from 'bun:test';

// Test the explore MCP tool functionality
describe('Explore MCP Tool', () => {
  let engine: any;

  beforeAll(async () => {
    // Initialize logger for tests
    const { initializeLogger } = await import('../../utils/logger.js');
    const { MillerPaths } = await import('../../utils/miller-paths.js');
    const paths = new MillerPaths(process.cwd());
    await paths.ensureDirectories();
    initializeLogger(paths);

    // Import and set up the engine
    const { CodeIntelligenceEngine } = await import('../../engine/code-intelligence.js');

    engine = new CodeIntelligenceEngine({
      workspacePath: process.cwd()
    });

    await engine.initialize();
  }, 30000);

  afterAll(async () => {
    if (engine) {
      await engine.dispose();
    }
  });

  describe('Overview Action', () => {
    it('should provide project overview with key statistics', async () => {
      const stats = await engine.getStats();
      const workspaces = await engine.listIndexedWorkspaces();

      // Get most important symbols (high-frequency references)
      const coreSymbols = await engine.searchCode("", {
        limit: 20,
        symbolKinds: ["class", "interface", "function"],
        includeSignature: true
      });

      expect(stats).toBeDefined();
      expect(stats.database).toBeDefined();
      expect(stats.database.symbols).toBeGreaterThan(0);
      expect(stats.database.files).toBeGreaterThan(0);
      expect(stats.extractors).toBeDefined();
      expect(stats.extractors.languages).toBeInstanceOf(Array);
      expect(stats.extractors.languages.length).toBeGreaterThan(0);

      expect(coreSymbols).toBeInstanceOf(Array);
      expect(coreSymbols.length).toBeGreaterThan(0);

      // Verify core symbols have required properties
      const firstSymbol = coreSymbols[0];
      expect(firstSymbol).toBeDefined();
      expect(firstSymbol.text).toBeDefined();
      expect(firstSymbol.kind).toBeDefined();
      expect(firstSymbol.file).toBeDefined();
      expect(firstSymbol.line).toBeDefined();
    });

    it('should identify Miller codebase structure', async () => {
      const coreSymbols = await engine.searchCode("", {
        limit: 50,
        symbolKinds: ["class", "interface", "function"],
        includeSignature: true
      });

      // Should find key Miller components
      const millerClasses = coreSymbols.filter(s =>
        s.text.includes('Miller') ||
        s.text.includes('Extractor') ||
        s.text.includes('Engine') ||
        s.text.includes('Parser')
      );

      expect(millerClasses.length).toBeGreaterThan(0);
    });
  });

  describe('Trace Action', () => {
    it('should trace execution flow for key Miller functions', async () => {
      // Test tracing a known function in Miller
      const searchResults = await engine.searchCode("extractSymbols", {
        limit: 10,
        symbolKinds: ["function"],
        includeSignature: true
      });

      expect(searchResults).toBeInstanceOf(Array);
      expect(searchResults.length).toBeGreaterThan(0);

      const extractSymbolsFunc = searchResults.find(s => s.text === 'extractSymbols');
      if (extractSymbolsFunc) {
        // Try to find references to this function
        const references = await engine.findReferences(
          extractSymbolsFunc.file,
          extractSymbolsFunc.line,
          0
        );

        expect(references).toBeDefined();
        expect(references.references).toBeInstanceOf(Array);
      }
    });

    it('should trace user authentication flow if present', async () => {
      const authRelated = await engine.searchCode("auth", {
        limit: 20,
        includeSignature: true
      });

      // This test is flexible - auth may or may not be present in Miller
      expect(authRelated).toBeInstanceOf(Array);
    });
  });

  describe('Find Action', () => {
    it('should find specific symbols instantly', async () => {
      // Test finding Miller-specific symbols
      const parserManager = await engine.searchCode("ParserManager", {
        limit: 10,
        symbolKinds: ["class"],
        includeSignature: true
      });

      expect(parserManager).toBeInstanceOf(Array);
      expect(parserManager.length).toBeGreaterThan(0);

      const found = parserManager.find(s => s.text === 'ParserManager');
      expect(found).toBeDefined();
      expect(found?.kind).toBe('class');
      expect(found?.file).toContain('parser-manager');
    });

    it('should find symbols across multiple languages', async () => {
      // Search for common function names that might exist across extractors
      const extractMethods = await engine.searchCode("extract", {
        limit: 50,
        symbolKinds: ["function"],
        includeSignature: true
      });

      expect(extractMethods).toBeInstanceOf(Array);
      expect(extractMethods.length).toBeGreaterThan(0);

      // Should find methods across different extractor files
      const files = new Set(extractMethods.map(s => s.file));
      expect(files.size).toBeGreaterThan(1);
    });
  });

  describe('Understand Action', () => {
    it('should provide semantic analysis of key components', async () => {
      // Test understanding of BaseExtractor
      const baseExtractor = await engine.searchCode("BaseExtractor", {
        limit: 5,
        symbolKinds: ["class"],
        includeSignature: true
      });

      if (baseExtractor.length > 0) {
        const extractor = baseExtractor[0];
        expect(extractor).toBeDefined();
        expect(extractor.text).toBe('BaseExtractor');
        expect(extractor.file).toContain('base-extractor');

        // Try to find related symbols
        const relatedSymbols = await engine.searchCode("Extractor", {
          limit: 20,
          symbolKinds: ["class"],
          includeSignature: true
        });

        expect(relatedSymbols.length).toBeGreaterThan(1);
      }
    });

    it('should understand Miller architecture patterns', async () => {
      // Search for architecture patterns in Miller
      const engines = await engine.searchCode("Engine", {
        limit: 10,
        symbolKinds: ["class"],
        includeSignature: true
      });

      const managers = await engine.searchCode("Manager", {
        limit: 10,
        symbolKinds: ["class"],
        includeSignature: true
      });

      // Miller should have engine and manager patterns
      expect(engines.length).toBeGreaterThan(0);
      expect(managers.length).toBeGreaterThan(0);
    });
  });

  describe('Related Action', () => {
    it('should find all connections to core Miller components', async () => {
      // Test finding all symbols related to extractors
      const extractorRelated = await engine.searchCode("extractor", {
        limit: 30,
        includeSignature: true
      });

      expect(extractorRelated).toBeInstanceOf(Array);
      expect(extractorRelated.length).toBeGreaterThan(0);

      // Should find multiple types of symbols (classes, functions, variables)
      const symbolKinds = new Set(extractorRelated.map(s => s.kind));
      expect(symbolKinds.size).toBeGreaterThan(1);
    });

    it('should map relationships between parser and extractor components', async () => {
      const parserSymbols = await engine.searchCode("parser", {
        limit: 20,
        includeSignature: true
      });

      const extractorSymbols = await engine.searchCode("extractor", {
        limit: 20,
        includeSignature: true
      });

      expect(parserSymbols.length).toBeGreaterThan(0);
      expect(extractorSymbols.length).toBeGreaterThan(0);

      // Both should exist as they're core Miller components
      const parserFiles = new Set(parserSymbols.map(s => s.file));
      const extractorFiles = new Set(extractorSymbols.map(s => s.file));

      expect(parserFiles.size).toBeGreaterThan(0);
      expect(extractorFiles.size).toBeGreaterThan(0);
    });
  });

  describe('Performance and Response Format', () => {
    it('should respond quickly to explore queries', async () => {
      const startTime = Date.now();

      const result = await engine.searchCode("Miller", {
        limit: 10,
        includeSignature: true
      });

      const duration = Date.now() - startTime;

      expect(result).toBeDefined();
      expect(duration).toBeLessThan(1000); // Should be fast < 1 second
    });

    it('should provide consistent response format', async () => {
      const result = await engine.searchCode("test", {
        limit: 5,
        includeSignature: true
      });

      expect(result).toBeInstanceOf(Array);

      if (result.length > 0) {
        const firstResult = result[0];
        expect(firstResult).toHaveProperty('text');
        expect(firstResult).toHaveProperty('file');
        expect(firstResult).toHaveProperty('line');
        expect(typeof firstResult.text).toBe('string');
        expect(typeof firstResult.file).toBe('string');
        expect(typeof firstResult.line).toBe('number');
      }
    });
  });
});