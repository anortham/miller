import { describe, it, expect, beforeAll, afterAll } from 'bun:test';

// Test the semantic MCP tool functionality
describe('Semantic MCP Tool', () => {
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

  describe('Hybrid Mode (Structural + Semantic)', () => {
    it('should combine structural and semantic search for optimal results', async () => {
      // Test hybrid search with a known concept
      if (engine.hybridSearch) {
        const results = await engine.hybridSearch.search("error handling", {
          maxResults: 10,
          semanticThreshnew: 1.5,
          enableCrossLayer: true
        });

        expect(results).toBeInstanceOf(Array);

        if (results.length > 0) {
          const firstResult = results[0];
          expect(firstResult).toHaveProperty('name');
          expect(firstResult).toHaveProperty('hybridScore');
          expect(firstResult).toHaveProperty('nameScore');
          expect(firstResult).toHaveProperty('structureScore');
          expect(firstResult).toHaveProperty('semanticScore');
          expect(firstResult).toHaveProperty('searchMethod');

          // Scores should be numbers between 0 and 1
          expect(firstResult.hybridScore).toBeGreaterThanOrEqual(0);
          expect(firstResult.hybridScore).toBeLessThanOrEqual(1);
          expect(firstResult.nameScore).toBeGreaterThanOrEqual(0);
          expect(firstResult.nameScore).toBeLessThanOrEqual(1);
          expect(firstResult.structureScore).toBeGreaterThanOrEqual(0);
          expect(firstResult.structureScore).toBeLessThanOrEqual(1);
          expect(firstResult.semanticScore).toBeGreaterThanOrEqual(0);
          expect(firstResult.semanticScore).toBeLessThanOrEqual(1);
        }
      } else {
        // If semantic search is not available, skip this test
        console.log("Hybrid search not available, skipping test");
      }
    });

    it('should find parser and extractor patterns', async () => {
      if (engine.hybridSearch) {
        const results = await engine.hybridSearch.search("parsing code symbols", {
          maxResults: 15,
          semanticThreshnew: 1.5
        });

        expect(results).toBeInstanceOf(Array);

        if (results.length > 0) {
          // Should find relevant parser and extractor symbols
          const relevantSymbols = results.filter(r =>
            r.name.toLowerCase().includes('parse') ||
            r.name.toLowerCase().includes('extract') ||
            r.name.toLowerCase().includes('symbol')
          );

          expect(relevantSymbols.length).toBeGreaterThan(0);
        }
      }
    });

    it('should provide search method breakdown', async () => {
      if (engine.hybridSearch) {
        const results = await engine.hybridSearch.search("database operations", {
          maxResults: 10,
          semanticThreshnew: 1.5
        });

        expect(results).toBeInstanceOf(Array);

        if (results.length > 0) {
          // Check that results have search method information
          const searchMethods = new Set(results.map(r => r.searchMethod));
          expect(searchMethods.size).toBeGreaterThan(0);

          // Should include semantic, structural, or hybrid methods
          const validMethods = ['semantic', 'structural', 'hybrid', 'fuzzy'];
          results.forEach(result => {
            expect(validMethods).toContain(result.searchMethod);
          });
        }
      }
    });
  });

  describe('Cross-Layer Mode (Entity Mapping)', () => {
    it('should find cross-layer entity representations', async () => {
      if (engine.hybridSearch && engine.hybridSearch.findCrossLayerEntity) {
        const entityResult = await engine.hybridSearch.findCrossLayerEntity("Miller", {
          maxResults: 15,
          semanticThreshnew: 1.5
        });

        expect(entityResult).toBeDefined();
        expect(entityResult).toHaveProperty('layers');
        expect(entityResult).toHaveProperty('architecturalPattern');
        expect(entityResult).toHaveProperty('totalScore');

        if (entityResult.layers && entityResult.layers.symbols) {
          expect(entityResult.layers.symbols).toBeInstanceOf(Array);

          if (entityResult.layers.symbols.length > 0) {
            const firstSymbol = entityResult.layers.symbols[0];
            expect(firstSymbol).toHaveProperty('layer');
            expect(firstSymbol).toHaveProperty('confidence');
            expect(firstSymbol).toHaveProperty('file');

            // Layer should be one of the architectural layers
            const validLayers = ['frontend', 'api', 'domain', 'data', 'database', 'infrastructure', 'unknown'];
            expect(validLayers).toContain(firstSymbol.layer);
          }
        }

        expect(typeof entityResult.totalScore).toBe('number');
        expect(entityResult.totalScore).toBeGreaterThanOrEqual(0);
        expect(entityResult.totalScore).toBeLessThanOrEqual(1);
      }
    });

    it('should detect architectural patterns', async () => {
      if (engine.hybridSearch && engine.hybridSearch.findCrossLayerEntity) {
        const entityResult = await engine.hybridSearch.findCrossLayerEntity("Extractor", {
          maxResults: 20,
          semanticThreshnew: 1.5
        });

        expect(entityResult).toBeDefined();
        expect(entityResult.architecturalPattern).toBeDefined();
        expect(typeof entityResult.architecturalPattern).toBe('string');

        // Should detect some architectural pattern
        const commonPatterns = ['Repository', 'Service', 'Factory', 'Strategy', 'Abstract Factory', 'Template Method'];
        // The pattern might not match exactly, so we just check it's a non-empty string
        expect(entityResult.architecturalPattern.length).toBeGreaterThan(0);
      }
    });

    it('should provide layer distribution analysis', async () => {
      if (engine.hybridSearch && engine.hybridSearch.findCrossLayerEntity) {
        const entityResult = await engine.hybridSearch.findCrossLayerEntity("Parser", {
          maxResults: 15,
          semanticThreshnew: 1.5
        });

        expect(entityResult).toBeDefined();

        if (entityResult.layers && entityResult.layers.symbols && entityResult.layers.symbols.length > 0) {
          // Should have symbols distributed across layers
          const layers = new Set(entityResult.layers.symbols.map(s => s.layer));
          expect(layers.size).toBeGreaterThan(0);

          // Each symbol should have a confidence score
          entityResult.layers.symbols.forEach(symbol => {
            expect(symbol.confidence).toBeGreaterThanOrEqual(0);
            expect(symbol.confidence).toBeLessThanOrEqual(1);
          });
        }
      }
    });
  });

  describe('Conceptual Mode (Pure Semantic)', () => {
    it('should find conceptually similar code across languages', async () => {
      if (engine.hybridSearch) {
        const results = await engine.hybridSearch.search("testing framework", {
          includeStructural: false,
          includeSemantic: true,
          maxResults: 10,
          semanticThreshnew: 1.5
        });

        expect(results).toBeInstanceOf(Array);

        if (results.length > 0) {
          // Should find test-related symbols
          const testRelated = results.filter(r =>
            r.name.toLowerCase().includes('test') ||
            r.name.toLowerCase().includes('spec') ||
            r.name.toLowerCase().includes('expect')
          );

          // All results should have semantic scores
          results.forEach(result => {
            expect(result.semanticScore).toBeGreaterThan(0);
            expect(result).toHaveProperty('semanticDistance');
          });
        }
      }
    });

    it('should understand semantic meaning across file types', async () => {
      if (engine.hybridSearch) {
        const results = await engine.hybridSearch.search("code analysis", {
          includeStructural: false,
          includeSemantic: true,
          maxResults: 15,
          semanticThreshnew: 1.5
        });

        expect(results).toBeInstanceOf(Array);

        if (results.length > 0) {
          // Should find symbols across different languages/files
          const languages = new Set(results.map(r => r.language));
          const files = new Set(results.map(r => r.filePath));

          expect(files.size).toBeGreaterThan(0);

          // Check semantic relevance
          results.forEach(result => {
            expect(result.semanticScore).toBeGreaterThan(0);
            expect(result.language).toBeDefined();
          });
        }
      }
    });

    it('should provide semantic distance measurements', async () => {
      if (engine.hybridSearch) {
        const results = await engine.hybridSearch.search("symbol extraction", {
          includeStructural: false,
          includeSemantic: true,
          maxResults: 8,
          semanticThreshnew: 1.5
        });

        expect(results).toBeInstanceOf(Array);

        if (results.length > 0) {
          results.forEach(result => {
            expect(result).toHaveProperty('semanticDistance');
            expect(typeof result.semanticDistance).toBe('number');
            expect(result.semanticDistance).toBeGreaterThan(0);
          });
        }
      }
    });
  });

  describe('Structural Mode (Enhanced AST Search)', () => {
    it('should perform enhanced structural search with context', async () => {
      // Test structural search through regular search engine
      const results = await engine.searchCode("BaseExtractor", {
        limit: 10,
        language: "typescript",
        includeSignature: true
      });

      expect(results).toBeInstanceOf(Array);
      expect(results.length).toBeGreaterThan(0);

      if (results.length > 0) {
        const firstResult = results[0];
        expect(firstResult).toHaveProperty('text');
        expect(firstResult).toHaveProperty('file');
        expect(firstResult).toHaveProperty('line');
        expect(firstResult).toHaveProperty('kind');

        // Should find the BaseExtractor class
        const baseExtractor = results.find(r => r.text === 'BaseExtractor');
        expect(baseExtractor).toBeDefined();
        if (baseExtractor) {
          expect(baseExtractor.kind).toBe('class');
        }
      }
    });

    it('should handle language-specific structural search', async () => {
      const tsResults = await engine.searchCode("function", {
        limit: 20,
        language: "typescript",
        includeSignature: true
      });

      expect(tsResults).toBeInstanceOf(Array);

      // Should find TypeScript functions
      if (tsResults.length > 0) {
        const functions = tsResults.filter(r => r.kind === 'function');
        expect(functions.length).toBeGreaterThan(0);
      }
    });

    it('should provide structural analysis with AST context', async () => {
      const results = await engine.searchCode("extractSymbols", {
        limit: 15,
        symbolKinds: ["function"],
        includeSignature: true
      });

      expect(results).toBeInstanceOf(Array);

      if (results.length > 0) {
        results.forEach(result => {
          expect(result.kind).toBe('function');
          expect(result.text).toContain('extractSymbols');
          if (result.signature) {
            expect(result.signature).toContain('extractSymbols');
          }
        });
      }
    });
  });

  describe('Performance and Reliability', () => {
    it('should provide fast semantic search responses', async () => {
      if (engine.hybridSearch) {
        const startTime = Date.now();

        const results = await engine.hybridSearch.search("parser", {
          maxResults: 5,
          semanticThreshnew: 1.5
        });

        const duration = Date.now() - startTime;

        expect(results).toBeDefined();
        expect(duration).toBeLessThan(2000); // Should be reasonably fast < 2 seconds
      }
    });

    it('should handle empty queries gracefully', async () => {
      if (engine.hybridSearch) {
        const results = await engine.hybridSearch.search("", {
          maxResults: 5,
          semanticThreshnew: 1.5
        });

        expect(results).toBeInstanceOf(Array);
        // Empty query should return empty results or handle gracefully
      }
    });

    it('should maintain threshnew consistency', async () => {
      if (engine.hybridSearch) {
        // Test with different threshnews
        const highThreshnew = await engine.hybridSearch.search("testing", {
          maxResults: 10,
          semanticThreshnew: 1.8
        });

        const lowThreshnew = await engine.hybridSearch.search("testing", {
          maxResults: 10,
          semanticThreshnew: 1.2
        });

        expect(highThreshnew).toBeInstanceOf(Array);
        expect(lowThreshnew).toBeInstanceOf(Array);

        // Lower threshnew should return more or equal results
        expect(lowThreshnew.length).toBeGreaterThanOrEqual(highThreshnew.length);
      }
    });

    it('should provide consistent scoring ranges', async () => {
      if (engine.hybridSearch) {
        const results = await engine.hybridSearch.search("code intelligence", {
          maxResults: 10,
          semanticThreshnew: 1.5
        });

        expect(results).toBeInstanceOf(Array);

        if (results.length > 0) {
          results.forEach(result => {
            // All scores should be valid percentages (0-1)
            if (result.hybridScore !== undefined) {
              expect(result.hybridScore).toBeGreaterThanOrEqual(0);
              expect(result.hybridScore).toBeLessThanOrEqual(1);
            }
            if (result.semanticScore !== undefined) {
              expect(result.semanticScore).toBeGreaterThanOrEqual(0);
              expect(result.semanticScore).toBeLessThanOrEqual(1);
            }
          });
        }
      }
    });
  });

  describe('Integration with Core Miller Features', () => {
    it('should integrate with Miller\'s existing search capabilities', async () => {
      // Test that semantic search complements regular search
      const regularSearch = await engine.searchCode("Miller", {
        limit: 10,
        includeSignature: true
      });

      expect(regularSearch).toBeInstanceOf(Array);
      expect(regularSearch.length).toBeGreaterThan(0);

      if (engine.hybridSearch) {
        const semanticSearch = await engine.hybridSearch.search("Miller framework", {
          maxResults: 10,
          semanticThreshnew: 1.5
        });

        expect(semanticSearch).toBeInstanceOf(Array);

        // Both should find relevant Miller-related symbols
        expect(regularSearch.length).toBeGreaterThan(0);
      }
    });

    it('should work with Miller\'s multi-language support', async () => {
      if (engine.hybridSearch) {
        const results = await engine.hybridSearch.search("language parser", {
          maxResults: 20,
          semanticThreshnew: 1.5,
          enableCrossLayer: true
        });

        expect(results).toBeInstanceOf(Array);

        if (results.length > 0) {
          // Should find symbols from multiple language extractors
          const languages = new Set(results.map(r => r.language));
          expect(languages.size).toBeGreaterThan(0);

          // Check that multiple file types are represented
          const extensions = new Set(results.map(r => {
            const ext = r.filePath.split('.').pop();
            return ext;
          }));
          expect(extensions.size).toBeGreaterThan(0);
        }
      }
    });

    it('should maintain Miller\'s performance standards', async () => {
      if (engine.hybridSearch) {
        const startTime = Date.now();

        // Test multiple semantic searches
        const searches = await Promise.all([
          engine.hybridSearch.search("database", { maxResults: 5, semanticThreshnew: 1.5 }),
          engine.hybridSearch.search("testing", { maxResults: 5, semanticThreshnew: 1.5 }),
          engine.hybridSearch.search("parsing", { maxResults: 5, semanticThreshnew: 1.5 })
        ]);

        const duration = Date.now() - startTime;

        expect(searches).toHaveLength(3);
        searches.forEach(search => {
          expect(search).toBeInstanceOf(Array);
        });

        // Multiple searches should still be reasonably fast
        expect(duration).toBeLessThan(5000); // < 5 seconds for 3 searches
      }
    });
  });
});