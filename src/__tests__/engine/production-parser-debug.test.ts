/**
 * Production Parser Debug Test
 *
 * Test to compare production vs test ParserManager behavior
 */

import { describe, test, expect } from 'bun:test';
import path from 'path';

describe('Production Parser Debug', () => {
  test('Check production ParserManager via MCP', async () => {
    // Test using the actual running Miller instance
    const response = await fetch('http://localhost:3000/debug-parser', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ test: 'extensions' })
    }).catch(() => null);

    if (!response) {
      console.log('MCP server not running - testing direct import');

      // Test by importing the actual enhanced engine
      const { EnhancedCodeIntelligenceEngine } = await import('../../engine/enhanced-code-intelligence.js');
      const { ParserManager } = await import('../../parser/parser-manager.js');
      const { CodeIntelDB } = await import('../../database/schema.js');
      const { MillerPaths } = await import('../../utils/miller-paths.js');

      const paths = new MillerPaths('/tmp/test');
      const db = new CodeIntelDB(paths);
      const parserManager = new ParserManager();

      console.log('Before initialization:');
      console.log('- Extensions:', parserManager.getSupportedExtensions().length);
      console.log('- Languages:', parserManager.getSupportedLanguages().length);

      await parserManager.initialize();

      console.log('After initialization:');
      console.log('- Extensions:', parserManager.getSupportedExtensions().length);
      console.log('- Languages:', parserManager.getSupportedLanguages().length);
      console.log('- First 5 extensions:', parserManager.getSupportedExtensions().slice(0, 5));

      // Test the exact production scenario
      const engine = new EnhancedCodeIntelligenceEngine(db, {
        workspacePath: '/Users/murphy/Source/miller',
        enableWatcher: false,
        enableSemanticSearch: false
      });

      // Access the private parser manager
      const productionParserManager = engine['parserManager'];

      console.log('Production engine parser manager:');
      console.log('- Extensions:', productionParserManager.getSupportedExtensions().length);
      console.log('- Languages:', productionParserManager.getSupportedLanguages().length);

      expect(productionParserManager.getSupportedExtensions().length).toBeGreaterThan(0);
    }
  });
});