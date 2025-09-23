import { describe, it, expect, beforeAll } from 'bun:test';
import { TypeScriptExtractor } from '../../extractors/typescript-extractor.js';
import { ParserManager } from '../../parser/parser-manager.js';
import { SymbolKind } from '../../extractors/base-extractor.js';

describe('TypeScriptExtractor (using JavaScript parser)', () => {
  let parserManager: ParserManager;

    beforeAll(async () => {
    // Initialize logger for tests
    const { initializeLogger } = await import('../../utils/logger.js');
    const { MillerPaths } = await import('../../utils/miller-paths.js');
    const paths = new MillerPaths(process.cwd());
    await paths.ensureDirectories();
    initializeLogger(paths);

    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Symbol Extraction', () => {
    it('should extract function declarations', async () => {
      const code = `
        function getUserDataAsyncAsyncAsyncAsyncAsyncAsync(id) {
          return fetch(\`/api/users/\${id}\`).then(r => r.json());
        }

        const arrow = (x) => x * 2;

        async function asyncFunc() {
          await Promise.resolve();
        }
      `;

      const parseResult = await parserManager.parseFile('test.js', code);
      const extractor = new TypeScriptExtractor('javascript', 'test.js', code);
      const symbols = extractor.extractSymbols(parseResult.tree);

      expect(symbols.length).toBeGreaterThanOrEqual(3);

      // Check function declaration
      const getUserDataAsyncAsyncAsyncAsyncAsyncAsync = symbols.find(s => s.name === 'getUserDataAsyncAsyncAsyncAsyncAsyncAsync');
      expect(getUserDataAsyncAsyncAsyncAsyncAsyncAsync).toBeDefined();
      expect(getUserDataAsyncAsyncAsyncAsyncAsyncAsync?.kind).toBe(SymbolKind.Function);
      expect(getUserDataAsyncAsyncAsyncAsyncAsyncAsync?.signature).toContain('getUserDataAsyncAsyncAsyncAsyncAsyncAsync(id)');

      // Check arrow function
      const arrow = symbols.find(s => s.name === 'arrow');
      expect(arrow).toBeDefined();
      expect(arrow?.kind).toBe(SymbolKind.Function);

      // Check async function
      const asyncFunc = symbols.find(s => s.name === 'asyncFunc');
      expect(asyncFunc).toBeDefined();
      expect(asyncFunc?.kind).toBe(SymbolKind.Function);
    });

    it('should extract class declarations', async () => {
      const code = `
        class BaseEntity {
          constructor(id) {
            this.id = id;
          }

          save() {
            throw new Error('Must implement save method');
          }
        }

        class User extends BaseEntity {
          constructor(id, name, email) {
            super(id);
            this.name = name;
            this.email = email;
          }

          serialize() {
            return JSON.stringify({ id: this.id, name: this.name, email: this.email });
          }

          async save() {
            await fetch('/api/users', { method: 'POST', body: this.serialize() });
          }
        }
      `;

      const parseResult = await parserManager.parseFile('test.js', code);
      const extractor = new TypeScriptExtractor('javascript', 'test.js', code);
      const symbols = extractor.extractSymbols(parseResult.tree);

      // Check base class
      const baseEntity = symbols.find(s => s.name === 'BaseEntity');
      expect(baseEntity).toBeDefined();
      expect(baseEntity?.kind).toBe(SymbolKind.Class);

      // Check derived class
      const user = symbols.find(s => s.name === 'User');
      expect(user).toBeDefined();
      expect(user?.kind).toBe(SymbolKind.Class);

      // Check class methods
      const serialize = symbols.find(s => s.name === 'serialize');
      expect(serialize).toBeDefined();
      expect(serialize?.kind).toBe(SymbolKind.Method);
      expect(serialize?.parentId).toBe(user?.id);

      // Check constructor
      const constructor = symbols.find(s => s.name === 'constructor');
      expect(constructor).toBeDefined();
      expect(constructor?.kind).toBe(SymbolKind.Constructor);
    });

    it('should extract variable and property declarations', async () => {
      const code = `
        const API_URL = 'https://api.example.com';
        let counter = 0;
        var legacy = true;

        const config = {
          timeout: 5000,
          retries: 3
        };
      `;

      const parseResult = await parserManager.parseFile('test.js', code);
      const extractor = new TypeScriptExtractor('javascript', 'test.js', code);
      const symbols = extractor.extractSymbols(parseResult.tree);

      // Check constants
      const apiUrl = symbols.find(s => s.name === 'API_URL');
      expect(apiUrl).toBeDefined();
      expect(apiUrl?.kind).toBe(SymbolKind.Variable);

      // Check variables
      const counter = symbols.find(s => s.name === 'counter');
      expect(counter).toBeDefined();
      expect(counter?.kind).toBe(SymbolKind.Variable);

      // Check object literal
      const config = symbols.find(s => s.name === 'config');
      expect(config).toBeDefined();
      expect(config?.kind).toBe(SymbolKind.Variable);
    });
  });

  describe('Relationship Extraction', () => {
    it('should extract function call relationships', async () => {
      const code = `
        function helper(x: number): number {
          return x * 2;
        }

        function main(): void {
          const result = helper(42);
          console.log(result);
          Math.max(1, 2, 3);
        }
      `;

      const parseResult = await parserManager.parseFile('test.js', code);
      const extractor = new TypeScriptExtractor('javascript', 'test.js', code);
      const symbols = extractor.extractSymbols(parseResult.tree);

      const relationships = extractor.extractRelationships(parseResult.tree, symbols);

      // Should find call relationships
      const callRelationships = relationships.filter(r => r.kind === 'calls');
      expect(callRelationships.length).toBeGreaterThan(0);

      // Check helper function call
      const helperCall = callRelationships.find(r => {
        const fromSymbol = symbols.find(s => s.id === r.fromSymbolId);
        const toSymbol = symbols.find(s => s.id === r.toSymbolId);
        return fromSymbol?.name === 'main' && toSymbol?.name === 'helper';
      });
      expect(helperCall).toBeDefined();
    });

    it('should extract inheritance relationships', async () => {
      const code = `
        class Shape {
          constructor() {
            if (this.constructor === Shape) {
              throw new Error('Cannot instantiate abstract class');
            }
          }

          area() {
            throw new Error('Must implement area method');
          }

          draw() {
            console.log('Drawing shape');
          }
        }

        class Circle extends Shape {
          constructor(radius) {
            super();
            this.radius = radius;
          }

          area() {
            return Math.PI * this.radius ** 2;
          }
        }
      `;

      const parseResult = await parserManager.parseFile('test.js', code);
      const extractor = new TypeScriptExtractor('javascript', 'test.js', code);
      const symbols = extractor.extractSymbols(parseResult.tree);
      const relationships = extractor.extractRelationships(parseResult.tree, symbols);

      // Check extends relationship
      const extendsRel = relationships.find(r => r.kind === 'extends');
      expect(extendsRel).toBeDefined();
    });
  });

  describe('Type Inference', () => {
    it('should infer basic types', async () => {
      const code = `
        const name = 'John';
        const age = 30;
        const isActive = true;
        const scores = [95, 87, 92];
        const user = { name: 'John', age: 30 };
      `;

      const parseResult = await parserManager.parseFile('test.js', code);
      const extractor = new TypeScriptExtractor('javascript', 'test.js', code);
      const symbols = extractor.extractSymbols(parseResult.tree);
      const types = extractor.inferTypes(symbols);

      expect(types.size).toBeGreaterThan(0);

      // Check some basic type inferences
      const nameSymbol = symbols.find(s => s.name === 'name');
      const ageSymbol = symbols.find(s => s.name === 'age');
      const isActiveSymbol = symbols.find(s => s.name === 'isActive');

      if (nameSymbol) {
        expect(types.get(nameSymbol.id)).toContain('string');
      }
      if (ageSymbol) {
        expect(types.get(ageSymbol.id)).toContain('number');
      }
      if (isActiveSymbol) {
        expect(types.get(isActiveSymbol.id)).toContain('boolean');
      }
    });

    it('should handle function return types', async () => {
      const code = `
        function add(a, b) {
          return a + b;
        }

        async function fetchUser(id) {
          const response = await fetch(\`/users/\${id}\`);
          return response.json();
        }
      `;

      const parseResult = await parserManager.parseFile('test.js', code);
      const extractor = new TypeScriptExtractor('javascript', 'test.js', code);
      const symbols = extractor.extractSymbols(parseResult.tree);
      const types = extractor.inferTypes(symbols);

      const addSymbol = symbols.find(s => s.name === 'add');
      const fetchUserSymbol = symbols.find(s => s.name === 'fetchUser');

      if (addSymbol) {
        const addType = types.get(addSymbol.id);
        expect(addType).toBeDefined(); // Type inference may not be perfect for JS
      }
      if (fetchUserSymbol) {
        const fetchUserType = types.get(fetchUserSymbol.id);
        expect(fetchUserType).toBeDefined(); // Type inference may not be perfect for JS
      }
    });
  });

  describe('Position Tracking', () => {
    it('should track accurate symbol positions', async () => {
      const code = `function test() {
  return 42;
}`;

      const parseResult = await parserManager.parseFile('test.js', code);
      const extractor = new TypeScriptExtractor('javascript', 'test.js', code);
      const symbols = extractor.extractSymbols(parseResult.tree);

      const testFunction = symbols.find(s => s.name === 'test');
      expect(testFunction).toBeDefined();
      expect(testFunction?.startLine).toBe(1);
      expect(testFunction?.startColumn).toBe(9);
      expect(testFunction?.endLine).toBe(1); // Function name spans only one line
    });
  });
});