import { describe, it, expect } from 'bun:test';
import { JavaExtractor } from './src/extractors/java-extractor.js';
import { ParserManager } from './src/parser/parser-manager.js';

describe('Debug JavaExtractor', () => {
  it('should show what nodes are found', async () => {
    const parserManager = new ParserManager();
    await parserManager.initialize();

    const javaCode = `
package com.example;

public class Calculator {
    public int add(int a, int b) {
        return a + b;
    }

    public void reset() {
        // private method
    }
}
`;

    const result = await parserManager.parseFile('test.java', javaCode);

    // Debug the tree structure
    function walkTree(node: any, depth = 0) {
      const indent = '  '.repeat(depth);
      console.log(`${indent}${node.type} (${node.startPosition.row}:${node.startPosition.column}-${node.endPosition.row}:${node.endPosition.column})`);

      if (node.children && depth < 4) {
        for (const child of node.children) {
          walkTree(child, depth + 1);
        }
      }
    }

    console.log('\n=== Java AST Structure ===');
    walkTree(result.tree.rootNode);

    const extractor = new JavaExtractor('java', 'test.java', javaCode);
    const symbols = extractor.extractSymbols(result.tree);

    console.log('\n=== Extracted Symbols ===');
    symbols.forEach((symbol, i) => {
      console.log(`${i + 1}: ${symbol.name} (${symbol.kind}) - ${symbol.signature}`);
    });

    expect(symbols.length).toBeGreaterThanOrEqual(0);
  });
});