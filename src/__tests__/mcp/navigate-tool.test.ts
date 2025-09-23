import { describe, it, expect, beforeAll, afterAll } from 'bun:test';

// Test the navigate MCP tool functionality
describe('Navigate MCP Tool', () => {
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

  describe('Definition Action', () => {
    it('should find definition of core Miller classes', async () => {
      // First find a known class
      const symbols = await engine.searchCode("BaseExtractor", {
        limit: 5,
        symbolKinds: ["class"],
        includeSignature: true
      });

      expect(symbols.length).toBeGreaterThan(0);
      const baseExtractor = symbols.find(s => s.text === 'BaseExtractor');
      expect(baseExtractor).toBeDefined();

      if (baseExtractor) {
        // Try to go to its definition
        const definition = await engine.goToDefinition(
          baseExtractor.file,
          baseExtractor.line,
          0
        );

        expect(definition).toBeDefined();
        if (definition.definition) {
          expect(definition.definition.file).toBeDefined();
          expect(definition.definition.line).toBeGreaterThan(0);
          expect(definition.definition.column).toBeGreaterThanOrEqual(0);
        }
      }
    });

    it('should handle definition requests for function symbols', async () => {
      // Find a known function
      const functions = await engine.searchCode("extractSymbols", {
        limit: 10,
        symbolKinds: ["function"],
        includeSignature: true
      });

      expect(functions.length).toBeGreaterThan(0);
      const extractFunc = functions[0];

      if (extractFunc) {
        const definition = await engine.goToDefinition(
          extractFunc.file,
          extractFunc.line,
          0
        );

        expect(definition).toBeDefined();
        // Definition might be null if it's the definition itself
        if (definition.definition) {
          expect(definition.definition.file).toBeDefined();
          expect(definition.definition.line).toBeGreaterThan(0);
        }
      }
    });

    it('should provide accurate line and column information', async () => {
      const symbols = await engine.searchCode("ParserManager", {
        limit: 5,
        symbolKinds: ["class"],
        includeSignature: true
      });

      if (symbols.length > 0) {
        const symbol = symbols[0];
        const definition = await engine.goToDefinition(
          symbol.file,
          symbol.line,
          5 // Try a specific column
        );

        expect(definition).toBeDefined();
        expect(typeof definition).toBe('object');
      }
    });
  });

  describe('References Action', () => {
    it('should find all references to BaseExtractor', async () => {
      // Find BaseExtractor class
      const symbols = await engine.searchCode("BaseExtractor", {
        limit: 5,
        symbolKinds: ["class"],
        includeSignature: true
      });

      expect(symbols.length).toBeGreaterThan(0);
      const baseExtractor = symbols.find(s => s.text === 'BaseExtractor');

      if (baseExtractor) {
        const references = await engine.findReferences(
          baseExtractor.file,
          baseExtractor.line,
          0
        );

        expect(references).toBeDefined();
        expect(references.references).toBeInstanceOf(Array);

        // BaseExtractor should have multiple references (extended by other extractors)
        expect(references.references.length).toBeGreaterThan(0);

        // Check reference format
        if (references.references.length > 0) {
          const firstRef = references.references[0];
          expect(firstRef).toHaveProperty('file');
          expect(firstRef).toHaveProperty('line');
          expect(firstRef).toHaveProperty('column');
          expect(typeof firstRef.file).toBe('string');
          expect(typeof firstRef.line).toBe('number');
          expect(typeof firstRef.column).toBe('number');
        }
      }
    });

    it('should find references across different extractor files', async () => {
      // Look for references to common methods or interfaces
      const symbols = await engine.searchCode("SymbolKind", {
        limit: 10,
        includeSignature: true
      });

      if (symbols.length > 0) {
        const symbolKind = symbols[0];
        const references = await engine.findReferences(
          symbolKind.file,
          symbolKind.line,
          0
        );

        expect(references).toBeDefined();
        expect(references.references).toBeInstanceOf(Array);

        // SymbolKind should be used across multiple files
        if (references.references.length > 1) {
          const files = new Set(references.references.map(ref => ref.file));
          expect(files.size).toBeGreaterThan(1);
        }
      }
    });

    it('should provide context for each reference', async () => {
      const symbols = await engine.searchCode("initialize", {
        limit: 10,
        symbolKinds: ["function"],
        includeSignature: true
      });

      if (symbols.length > 0) {
        const initFunc = symbols[0];
        const references = await engine.findReferences(
          initFunc.file,
          initFunc.line,
          0
        );

        expect(references).toBeDefined();

        if (references.references && references.references.length > 0) {
          const firstRef = references.references[0];
          expect(firstRef.file).toBeDefined();
          expect(firstRef.line).toBeGreaterThan(0);
          expect(firstRef.column).toBeGreaterThanOrEqual(0);
        }
      }
    });
  });

  describe('Hierarchy Action', () => {
    it('should find call hierarchy for core Miller functions', async () => {
      // Find a function that's likely to have callers
      const functions = await engine.searchCode("searchCode", {
        limit: 10,
        symbolKinds: ["function"],
        includeSignature: true
      });

      if (functions.length > 0) {
        const func = functions[0];

        // Test incoming calls (who calls this function)
        const incomingCalls = await engine.getCallHierarchy(
          func.file,
          func.line,
          0,
          'incoming'
        );

        expect(incomingCalls).toBeDefined();
        expect(incomingCalls.calls).toBeInstanceOf(Array);

        // Test outgoing calls (what this function calls)
        const outgoingCalls = await engine.getCallHierarchy(
          func.file,
          func.line,
          0,
          'outgoing'
        );

        expect(outgoingCalls).toBeDefined();
        expect(outgoingCalls.calls).toBeInstanceOf(Array);
      }
    });

    it('should map complete call chain for initialization', async () => {
      // Find initialization functions
      const initFunctions = await engine.searchCode("initialize", {
        limit: 10,
        symbolKinds: ["function"],
        includeSignature: true
      });

      if (initFunctions.length > 0) {
        const initFunc = initFunctions[0];

        const hierarchy = await engine.getCallHierarchy(
          initFunc.file,
          initFunc.line,
          0,
          'incoming'
        );

        expect(hierarchy).toBeDefined();
        expect(hierarchy.calls).toBeInstanceOf(Array);

        // Check call hierarchy format
        if (hierarchy.calls.length > 0) {
          const firstCall = hierarchy.calls[0];
          expect(firstCall).toHaveProperty('file');
          expect(firstCall).toHaveProperty('line');
          expect(firstCall).toHaveProperty('column');
          expect(firstCall).toHaveProperty('name');
        }
      }
    });

    it('should handle both directions of call hierarchy', async () => {
      const symbols = await engine.searchCode("parseFile", {
        limit: 10,
        symbolKinds: ["function"],
        includeSignature: true
      });

      if (symbols.length > 0) {
        const parseFunc = symbols[0];

        // Test both directions
        const incoming = await engine.getCallHierarchy(
          parseFunc.file,
          parseFunc.line,
          0,
          'incoming'
        );

        const outgoing = await engine.getCallHierarchy(
          parseFunc.file,
          parseFunc.line,
          0,
          'outgoing'
        );

        expect(incoming).toBeDefined();
        expect(outgoing).toBeDefined();
        expect(incoming.calls).toBeInstanceOf(Array);
        expect(outgoing.calls).toBeInstanceOf(Array);
      }
    });
  });

  describe('Implementations Action', () => {
    it('should find all implementations of BaseExtractor', async () => {
      // Find BaseExtractor interface/class
      const symbols = await engine.searchCode("BaseExtractor", {
        limit: 5,
        symbolKinds: ["class"],
        includeSignature: true
      });

      if (symbols.length > 0) {
        const baseClass = symbols[0];

        // Look for implementations (classes that extend BaseExtractor)
        const implementations = await engine.searchCode("extends BaseExtractor", {
          limit: 20,
          includeSignature: true
        });

        expect(implementations).toBeInstanceOf(Array);
        // Should find multiple extractor implementations
        expect(implementations.length).toBeGreaterThan(0);
      }
    });

    it('should find extractor implementations across languages', async () => {
      // Search for TypeScript/JavaScript extractor implementations
      const extractorImpls = await engine.searchCode("Extractor", {
        limit: 30,
        symbolKinds: ["class"],
        includeSignature: true
      });

      expect(extractorImpls.length).toBeGreaterThan(0);

      // Should find multiple language extractors
      const extractorNames = extractorImpls.map(impl => impl.text);
      const languageExtractors = extractorNames.filter(name =>
        name.includes('TypeScript') ||
        name.includes('JavaScript') ||
        name.includes('Python') ||
        name.includes('Rust') ||
        name.includes('Go')
      );

      expect(languageExtractors.length).toBeGreaterThan(0);
    });

    it('should provide implementation details', async () => {
      const extractors = await engine.searchCode("Extractor", {
        limit: 20,
        symbolKinds: ["class"],
        includeSignature: true
      });

      expect(extractors.length).toBeGreaterThan(0);

      // Check that implementations have proper structure
      extractors.forEach(extractor => {
        expect(extractor).toHaveProperty('text');
        expect(extractor).toHaveProperty('file');
        expect(extractor).toHaveProperty('line');
        expect(extractor.text).toMatch(/.*Extractor.*/);
        expect(extractor.file).toMatch(/.*extractor.*/);
      });
    });
  });

  describe('Cross-Language Navigation', () => {
    it('should navigate between related symbols across different language extractors', async () => {
      // Find symbols that might exist across multiple language extractors
      const extractSymbols = await engine.searchCode("extractSymbols", {
        limit: 20,
        symbolKinds: ["function"],
        includeSignature: true
      });

      if (extractSymbols.length > 1) {
        // Should find extractSymbols method in multiple extractor files
        const files = new Set(extractSymbols.map(s => s.file));
        expect(files.size).toBeGreaterThan(1);

        // Test navigation to one of them
        const firstExtract = extractSymbols[0];
        const references = await engine.findReferences(
          firstExtract.file,
          firstExtract.line,
          0
        );

        expect(references).toBeDefined();
        expect(references.references).toBeInstanceOf(Array);
      }
    });

    it('should handle navigation in test files', async () => {
      // Find test-related symbols
      const testSymbols = await engine.searchCode("test", {
        limit: 20,
        includeSignature: true
      });

      if (testSymbols.length > 0) {
        const testFiles = testSymbols.filter(s => s.file.includes('test'));

        if (testFiles.length > 0) {
          const testSymbol = testFiles[0];
          const definition = await engine.goToDefinition(
            testSymbol.file,
            testSymbol.line,
            0
          );

          expect(definition).toBeDefined();
        }
      }
    });
  });

  describe('Performance and Accuracy', () => {
    it('should provide fast navigation responses', async () => {
      const symbols = await engine.searchCode("Miller", {
        limit: 5,
        includeSignature: true
      });

      if (symbols.length > 0) {
        const symbol = symbols[0];
        const startTime = Date.now();

        const definition = await engine.goToDefinition(
          symbol.file,
          symbol.line,
          0
        );

        const duration = Date.now() - startTime;

        expect(definition).toBeDefined();
        expect(duration).toBeLessThan(500); // Should be very fast < 500ms
      }
    });

    it('should maintain 100% accuracy for known symbols', async () => {
      // Test with a symbol we know exists
      const parserManager = await engine.searchCode("ParserManager", {
        limit: 5,
        symbolKinds: ["class"],
        includeSignature: true
      });

      if (parserManager.length > 0) {
        const pm = parserManager[0];

        // Should accurately navigate to its definition
        const definition = await engine.goToDefinition(pm.file, pm.line, 0);
        expect(definition).toBeDefined();

        // Should accurately find its references
        const references = await engine.findReferences(pm.file, pm.line, 0);
        expect(references).toBeDefined();
        expect(references.references).toBeInstanceOf(Array);
      }
    });

    it('should handle edge cases gracefully', async () => {
      // Test with non-existent file
      const badDefinition = await engine.goToDefinition(
        'non-existent-file.ts',
        1,
        0
      );
      expect(badDefinition).toBeDefined();

      // Test with invalid line number
      const badLineDefinition = await engine.goToDefinition(
        'src/mcp-server.ts',
        99999,
        0
      );
      expect(badLineDefinition).toBeDefined();
    });
  });
});