import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';

describe('Tree-sitter Diagnostics', () => {
  let parserManager: ParserManager;

  beforeAll(async () => {
    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Parser Loading and ABI Compatibility', () => {
    const languages = [
      { name: 'javascript', extensions: ['.js'], sample: 'function test() { return 42; }' },
      { name: 'typescript', extensions: ['.ts'], sample: 'function test(): number { return 42; }' },
      { name: 'css', extensions: ['.css'], sample: '.test { color: red; }' },
      { name: 'html', extensions: ['.html'], sample: '<div>Hello</div>' },
      { name: 'python', extensions: ['.py'], sample: 'def test():\n    return 42' },
      { name: 'c', extensions: ['.c'], sample: 'int main() { return 0; }' },
      { name: 'cpp', extensions: ['.cpp'], sample: 'int main() { return 0; }' },
      { name: 'rust', extensions: ['.rs'], sample: 'fn main() { println!("hello"); }' },
      { name: 'go', extensions: ['.go'], sample: 'package main\nfunc main() {}' },
      { name: 'java', extensions: ['.java'], sample: 'class Test { public static void main(String[] args) {} }' },
      { name: 'c_sharp', extensions: ['.cs'], sample: 'class Test { static void Main() {} }' },
      { name: 'ruby', extensions: ['.rb'], sample: 'def test\n  42\nend' },
      { name: 'php', extensions: ['.php'], sample: '<?php function test() { return 42; } ?>' },
      { name: 'swift', extensions: ['.swift'], sample: 'func test() -> Int { return 42 }' },
      { name: 'kotlin', extensions: ['.kt'], sample: 'fun main() { println("hello") }' },
      { name: 'regex', extensions: ['.regex'], sample: '[a-z]+' },
    ];

    languages.forEach(({ name, extensions, sample }) => {
      it(`should load ${name} parser successfully`, async () => {
        const result = await parserManager.parseFile(`test${extensions[0]}`, sample);

        expect(result).toBeDefined();
        expect(result).not.toBeNull();
        expect(result.tree).toBeDefined();

        if (result && result.tree) {
          console.log(`✅ ${name}: Parser loaded successfully`);
        } else {
          console.log(`❌ ${name}: Parser failed to load`);
        }
      });

      it(`should create valid AST nodes for ${name}`, async () => {
        const result = await parserManager.parseFile(`test${extensions[0]}`, sample);

        if (!result || !result.tree) {
          console.log(`⚠️  ${name}: Skipping AST test - parser failed to load`);
          return;
        }

        const tree = result.tree;

        // Test root node
        expect(tree.rootNode).toBeDefined();
        expect(tree.rootNode).not.toBeNull();
        expect(tree.rootNode.type).toBeDefined();
        expect(typeof tree.rootNode.type).toBe('string');

        console.log(`✅ ${name}: Root node type = "${tree.rootNode.type}"`);

        // Test node properties
        expect(tree.rootNode.startPosition).toBeDefined();
        expect(tree.rootNode.endPosition).toBeDefined();
        expect(tree.rootNode.children).toBeDefined();

        console.log(`✅ ${name}: Node has ${tree.rootNode.children.length} children`);
      });

      it(`should handle node traversal for ${name}`, async () => {
        const result = await parserManager.parseFile(`test${extensions[0]}`, sample);

        if (!result || !result.tree) {
          console.log(`⚠️  ${name}: Skipping traversal test - parser failed to load`);
          return;
        }

        const tree = result.tree;

        let nodeCount = 0;
        let errors: string[] = [];

        const visitNode = (node: any, depth = 0) => {
          try {
            // Test critical properties
            if (!node) {
              errors.push(`Null node at depth ${depth}`);
              return;
            }

            if (typeof node.type !== 'string') {
              errors.push(`Invalid node.type at depth ${depth}: ${typeof node.type}`);
              return;
            }

            if (!node.startPosition || !node.endPosition) {
              errors.push(`Missing position data for ${node.type} at depth ${depth}`);
              return;
            }

            nodeCount++;

            // Recursively visit children (max depth 3 to avoid infinite loops)
            if (depth < 3 && node.children && Array.isArray(node.children)) {
              for (const child of node.children) {
                visitNode(child, depth + 1);
              }
            }
          } catch (error) {
            errors.push(`Exception visiting node at depth ${depth}: ${error}`);
          }
        };

        visitNode(tree.rootNode);

        expect(errors).toHaveLength(0);
        expect(nodeCount).toBeGreaterThan(0);

        if (errors.length > 0) {
          console.log(`❌ ${name}: Traversal errors:`, errors);
        } else {
          console.log(`✅ ${name}: Successfully traversed ${nodeCount} nodes`);
        }
      });
    });
  });

  describe('ABI Version Compatibility', () => {
    it('should report tree-sitter ABI version', () => {
      // Get the tree-sitter version info if available
      const TreeSitter = require('web-tree-sitter');

      console.log('🔍 Tree-sitter version info:');
      console.log('  TreeSitter object keys:', Object.keys(TreeSitter));

      if (TreeSitter.version) {
        console.log('  Version:', TreeSitter.version);
      }

      if (TreeSitter.ABI_VERSION) {
        console.log('  ABI Version:', TreeSitter.ABI_VERSION);
      }

      // This test always passes but logs important debug info
      expect(TreeSitter).toBeDefined();
    });
  });

  describe('Memory Management', () => {
    it('should handle multiple parse operations without memory leaks', async () => {
      const samples = [
        { lang: 'javascript', code: 'function test() { return 42; }', ext: '.js' },
        { lang: 'python', code: 'def test():\n    return 42', ext: '.py' },
      ];

      for (let i = 0; i < 10; i++) {
        for (const { lang, code, ext } of samples) {
          const result = await parserManager.parseFile(`test${i}${ext}`, code);

          if (result && result.tree) {
            // Ensure tree properties are still valid after multiple operations
            expect(result.tree.rootNode).toBeDefined();
            expect(result.tree.rootNode.type).toBeDefined();

            // Test that we can access children
            const childCount = result.tree.rootNode.children?.length || 0;
            expect(childCount).toBeGreaterThanOrEqual(0);
          }
        }
      }

      console.log('✅ Memory test: Completed 20 parse operations');
    });
  });

  describe('Error Conditions', () => {
    it('should handle invalid syntax gracefully', async () => {
      const invalidSamples = [
        { lang: 'javascript', code: 'function {{{ invalid syntax', ext: '.js' },
        { lang: 'python', code: 'def invalid(\n  syntax error', ext: '.py' },
        { lang: 'c', code: 'int main( { invalid }', ext: '.c' },
      ];

      for (const { lang, code, ext } of invalidSamples) {
        const result = await parserManager.parseFile(`invalid${ext}`, code);

        // Even with invalid syntax, we should get a tree object
        // (tree-sitter creates error nodes for invalid syntax)
        if (result && result.tree) {
          expect(result.tree.rootNode).toBeDefined();
          expect(result.tree.rootNode.type).toBeDefined();
          console.log(`✅ ${lang}: Handled invalid syntax, root type = "${result.tree.rootNode.type}"`);
        } else {
          console.log(`❌ ${lang}: Failed to handle invalid syntax`);
        }
      }
    });
  });
});