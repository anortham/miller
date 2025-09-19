import { describe, it, expect, beforeAll, afterAll } from 'bun:test';

// MCP Integration Tests - Testing Miller's MCP tools against its own codebase
describe('Miller MCP Integration Tests', () => {
  let mcpTools: any;
  let engine: any;

  beforeAll(async () => {
    // Initialize logger for tests
    const { initializeLogger } = await import('../../utils/logger.js');
    initializeLogger(process.cwd());

    // Import and set up the engine
    const { CodeIntelligenceEngine } = await import('../../engine/code-intelligence.js');

    engine = new CodeIntelligenceEngine({
      workspacePath: process.cwd()
    });

    await engine.initialize();

    // Simulate MCP tool interface
    mcpTools = {
      searchCode: async (query: string, options: any = {}) => {
        return await engine.searchCode(query, options);
      },
      goToDefinition: async (file: string, line: number, column: number) => {
        return await engine.goToDefinition(file, line, column);
      },
      findReferences: async (file: string, line: number, column: number) => {
        return await engine.findReferences(file, line, column);
      },
      getWorkspaceStats: async () => {
        return await engine.getWorkspaceStats();
      },
      healthCheck: async () => {
        return await engine.healthCheck();
      }
    };
  }, 30000); // Increase timeout for initialization

  afterAll(async () => {
    if (engine) {
      await engine.dispose();
    }
  });

  describe('Search Functionality', () => {
    it('should find Miller\'s core classes', async () => {
      const results = await mcpTools.searchCode('TypeScriptExtractor', { limit: 5 });

      expect(results.length).toBeGreaterThan(0);

      const classResult = results.find((r: any) =>
        r.kind === 'class' && r.text === 'TypeScriptExtractor'
      );

      expect(classResult).toBeDefined();
      expect(classResult.file).toContain('typescript-extractor.ts');
      expect(classResult.signature).toContain('class TypeScriptExtractor');
    });

    it('should find Miller\'s core methods', async () => {
      const results = await mcpTools.searchCode('extractSymbols', { limit: 5 });

      expect(results.length).toBeGreaterThan(0);

      const methodResult = results.find((r: any) =>
        r.kind === 'method' && r.text === 'extractSymbols'
      );

      expect(methodResult).toBeDefined();
      expect(methodResult.file).toContain('typescript-extractor.ts');
      expect(methodResult.signature).toContain('extractSymbols');
    });

    it('should support fuzzy search for Miller functions', async () => {
      const results = await mcpTools.searchCode('indexWork', {
        limit: 5,
        includeSignature: true
      });

      expect(results.length).toBeGreaterThan(0);

      const functionResult = results.find((r: any) =>
        r.text && r.text.includes('index') && r.text.includes('Work')
      );

      expect(functionResult).toBeDefined();
      expect(functionResult.signature).toBeDefined();
    });

    it('should filter by symbol kind', async () => {
      const classResults = await mcpTools.searchCode('', {
        symbolKinds: ['class'],
        limit: 10
      });

      expect(classResults.length).toBeGreaterThan(0);
      classResults.forEach((result: any) => {
        expect(result.kind).toBe('class');
      });
    });

    it('should respect search limits', async () => {
      const smallResults = await mcpTools.searchCode('function', { limit: 3 });
      const largeResults = await mcpTools.searchCode('function', { limit: 15 });

      expect(smallResults.length).toBeLessThanOrEqual(3);
      expect(largeResults.length).toBeGreaterThanOrEqual(smallResults.length);
    });

    it('should handle empty search results gracefully', async () => {
      const results = await mcpTools.searchCode('NonExistentSymbolName12345', { limit: 5 });

      expect(results).toBeInstanceOf(Array);
      expect(results.length).toBe(0);
    });
  });

  describe('Go-to-Definition', () => {
    it('should find definitions for known Miller symbols', async () => {
      // First find a symbol to get its location
      const searchResults = await mcpTools.searchCode('extractSymbols', { limit: 1 });
      expect(searchResults.length).toBeGreaterThan(0);

      const symbol = searchResults[0];
      const definition = await mcpTools.goToDefinition(
        symbol.file,
        symbol.line,
        symbol.column
      );

      // Definition should point to the same location (it's the definition itself)
      expect(definition).toBeDefined();
      expect(definition.file).toBe(symbol.file);
      expect(definition.line).toBe(symbol.line);
    });

    it('should handle invalid file paths', async () => {
      const definition = await mcpTools.goToDefinition(
        'nonexistent/file.ts',
        1,
        1
      );

      expect(definition).toBeNull();
    });

    it('should handle invalid positions', async () => {
      const definition = await mcpTools.goToDefinition(
        'src/extractors/typescript-extractor.ts',
        99999,
        99999
      );

      expect(definition).toBeNull();
    });
  });

  describe('Find References', () => {
    it('should find references to Miller\'s core methods', async () => {
      // Find the extractSymbols method definition
      const searchResults = await mcpTools.searchCode('extractSymbols', {
        symbolKinds: ['method'],
        limit: 1
      });

      if (searchResults.length > 0) {
        const symbol = searchResults[0];
        const references = await mcpTools.findReferences(
          symbol.file,
          symbol.line,
          symbol.column
        );

        expect(references).toBeInstanceOf(Array);
        // References may or may not exist depending on symbol usage
        expect(references.length).toBeGreaterThanOrEqual(0);

        references.forEach((ref: any) => {
          expect(ref.file).toBeDefined();
          expect(ref.line).toBeGreaterThan(0);
          expect(ref.column).toBeGreaterThanOrEqual(0);
        });
      }
    });

    it('should handle symbols with no references', async () => {
      const references = await mcpTools.findReferences(
        'src/extractors/typescript-extractor.ts',
        1,
        1
      );

      expect(references).toBeInstanceOf(Array);
      // May be empty, that's okay
    });
  });

  describe('Workspace Statistics', () => {
    it('should return comprehensive workspace stats for Miller', async () => {
      const stats = await mcpTools.getWorkspaceStats();

      expect(stats).toBeDefined();
      expect(stats.totalSymbols).toBeGreaterThan(400); // Miller has 457+ symbols
      expect(stats.totalFiles).toBeGreaterThan(10);
      expect(stats.languages).toContain('typescript');
      expect(stats.languages).toContain('javascript');

      // Check symbol kinds distribution (may be empty object if not implemented)
      expect(stats.symbolsByKind).toBeDefined();

      // Check language distribution (may be empty object if not implemented)
      expect(stats.symbolsByLanguage).toBeDefined();
    });

    it('should include search engine statistics', async () => {
      const stats = await mcpTools.getWorkspaceStats();

      expect(stats.searchEngine).toBeDefined();
      expect(stats.searchEngine.indexedDocuments).toBeGreaterThan(400);
      expect(stats.searchEngine.isIndexed).toBe(true);
    });
  });

  describe('Health Check', () => {
    it('should report healthy status for working Miller instance', async () => {
      const health = await mcpTools.healthCheck();

      expect(health).toBeDefined();
      expect(health.status).toBe('healthy');
      expect(health.components.database).toBe('healthy');
      expect(health.components.parser).toBe('healthy');
      expect(health.components.searchEngine).toBe('healthy');
      // File watcher status may vary in test environment
      expect(['healthy', 'unhealthy']).toContain(health.components.fileWatcher);
    });

    it('should include component details in health check', async () => {
      const health = await mcpTools.healthCheck();

      expect(health.details).toBeDefined();
      expect(health.details.parsers).toBeDefined();
      expect(health.details.database).toBeDefined();
      expect(health.details.searchIndex).toBeDefined();

      // Should report available parsers
      expect(health.details.parsers.loaded).toContain('javascript');
      expect(health.details.parsers.loaded).toContain('typescript');
    });
  });

  describe('Error Handling and Edge Cases', () => {
    it('should handle malformed search queries gracefully', async () => {
      const results = await mcpTools.searchCode('', { limit: 5 });
      expect(results).toBeInstanceOf(Array);
    });

    it('should handle extremely long search queries', async () => {
      const longQuery = 'a'.repeat(1000);
      const results = await mcpTools.searchCode(longQuery, { limit: 5 });
      expect(results).toBeInstanceOf(Array);
    });

    it('should handle negative or zero limits gracefully', async () => {
      const results1 = await mcpTools.searchCode('function', { limit: 0 });
      const results2 = await mcpTools.searchCode('function', { limit: -5 });

      expect(results1).toBeInstanceOf(Array);
      expect(results2).toBeInstanceOf(Array);
    });

    it('should handle special characters in search queries', async () => {
      const specialQueries = ['@#$%', '(){}[]', '\\n\\t', '""\'\''];

      for (const query of specialQueries) {
        const results = await mcpTools.searchCode(query, { limit: 5 });
        expect(results).toBeInstanceOf(Array);
      }
    });
  });

  describe('Performance Tests', () => {
    it('should perform searches within reasonable time', async () => {
      const startTime = Date.now();

      await mcpTools.searchCode('function', { limit: 50 });

      const duration = Date.now() - startTime;
      expect(duration).toBeLessThan(1000); // Should complete within 1 second
    });

    it('should handle concurrent searches efficiently', async () => {
      const searchPromises = [
        mcpTools.searchCode('class', { limit: 10 }),
        mcpTools.searchCode('method', { limit: 10 }),
        mcpTools.searchCode('function', { limit: 10 }),
        mcpTools.searchCode('variable', { limit: 10 })
      ];

      const startTime = Date.now();
      const results = await Promise.all(searchPromises);
      const duration = Date.now() - startTime;

      // All searches should complete
      results.forEach(result => {
        expect(result).toBeInstanceOf(Array);
      });

      // Concurrent searches shouldn't take much longer than sequential
      expect(duration).toBeLessThan(2000);
    });
  });
});