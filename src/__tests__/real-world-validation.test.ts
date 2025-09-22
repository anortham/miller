import { describe, test, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../parser/parser-manager.js';
import { TypeScriptExtractor } from '../extractors/typescript-extractor.js';
import { PythonExtractor } from '../extractors/python-extractor.js';
import { SwiftExtractor } from '../extractors/swift-extractor.js';
import { KotlinExtractor } from '../extractors/kotlin-extractor.js';
import { JavaExtractor } from '../extractors/java-extractor.js';
import * as fs from 'fs';
import * as path from 'path';

const REAL_WORLD_TEST_DIR = path.join(process.cwd(), 'debug/test-workspace-real');

describe('Real-World Code Validation', () => {
  let parserManager: ParserManager;

  beforeAll(async () => {
    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Kotlin Real-World Files', () => {
    const kotlinDir = path.join(REAL_WORLD_TEST_DIR, 'kotlin');

    if (fs.existsSync(kotlinDir)) {
      const kotlinFiles = fs.readdirSync(kotlinDir).filter(f => f.endsWith('.kt'));

      for (const fileName of kotlinFiles) {
        test(`should extract symbols from real-world Kotlin file: ${fileName}`, async () => {
          const filePath = path.join(kotlinDir, fileName);
          const content = fs.readFileSync(filePath, 'utf-8');

          console.log(`🤖 Processing real Kotlin file: ${fileName} (${content.length} chars)`);

          const result = await parserManager.parseFile(fileName, content);
          expect(result.tree).toBeDefined();

          const extractor = new KotlinExtractor('kotlin', fileName, content);
          const symbols = extractor.extractSymbols(result.tree);
          const relationships = extractor.extractRelationships(result.tree, symbols);

          // Validate meaningful extraction
          expect(symbols.length).toBeGreaterThan(0);
          console.log(`📊 Extracted ${symbols.length} symbols, ${relationships.length} relationships`);

          // Log symbol types for validation
          const symbolSummary = symbols.reduce((acc, s) => {
            acc[s.kind] = (acc[s.kind] || 0) + 1;
            return acc;
          }, {} as Record<string, number>);
          console.log(`🔍 Symbol breakdown:`, symbolSummary);
        });
      }
    }
  });

  describe('Swift Real-World Files', () => {
    const swiftDir = path.join(REAL_WORLD_TEST_DIR, 'swift');

    if (fs.existsSync(swiftDir)) {
      const swiftFiles = fs.readdirSync(swiftDir).filter(f => f.endsWith('.swift'));

      for (const fileName of swiftFiles) {
        test(`should extract symbols from real-world Swift file: ${fileName}`, async () => {
          const filePath = path.join(swiftDir, fileName);
          const content = fs.readFileSync(filePath, 'utf-8');

          console.log(`🍎 Processing real Swift file: ${fileName} (${content.length} chars)`);

          const result = await parserManager.parseFile(fileName, content);
          expect(result.tree).toBeDefined();

          const extractor = new SwiftExtractor('swift', fileName, content);
          const symbols = extractor.extractSymbols(result.tree);
          const relationships = extractor.extractRelationships(result.tree, symbols);

          // Validate meaningful extraction
          expect(symbols.length).toBeGreaterThan(0);
          console.log(`📊 Extracted ${symbols.length} symbols, ${relationships.length} relationships`);

          // Log symbol types for validation
          const symbolSummary = symbols.reduce((acc, s) => {
            acc[s.kind] = (acc[s.kind] || 0) + 1;
            return acc;
          }, {} as Record<string, number>);
          console.log(`🔍 Symbol breakdown:`, symbolSummary);
        });
      }
    }
  });

  describe('Java Real-World Files', () => {
    const javaDir = path.join(REAL_WORLD_TEST_DIR, 'java');

    if (fs.existsSync(javaDir)) {
      const javaFiles = fs.readdirSync(javaDir).filter(f => f.endsWith('.java'));

      for (const fileName of javaFiles) {
        test(`should extract symbols from real-world Java file: ${fileName}`, async () => {
          const filePath = path.join(javaDir, fileName);
          const content = fs.readFileSync(filePath, 'utf-8');

          console.log(`☕ Processing real Java file: ${fileName} (${content.length} chars)`);

          const result = await parserManager.parseFile(fileName, content);
          expect(result.tree).toBeDefined();

          const extractor = new JavaExtractor('java', fileName, content);
          const symbols = extractor.extractSymbols(result.tree);
          const relationships = extractor.extractRelationships(result.tree, symbols);

          // Validate meaningful extraction
          expect(symbols.length).toBeGreaterThan(0);
          console.log(`📊 Extracted ${symbols.length} symbols, ${relationships.length} relationships`);

          // Log symbol types for validation
          const symbolSummary = symbols.reduce((acc, s) => {
            acc[s.kind] = (acc[s.kind] || 0) + 1;
            return acc;
          }, {} as Record<string, number>);
          console.log(`🔍 Symbol breakdown:`, symbolSummary);
        });
      }
    }
  });

  describe('Vue Real-World Files', () => {
    const vueDir = path.join(REAL_WORLD_TEST_DIR, 'vue');

    if (fs.existsSync(vueDir)) {
      const vueFiles = fs.readdirSync(vueDir).filter(f => f.endsWith('.vue'));

      for (const fileName of vueFiles) {
        test(`should extract symbols from real-world Vue file: ${fileName}`, async () => {
          const filePath = path.join(vueDir, fileName);
          const content = fs.readFileSync(filePath, 'utf-8');

          console.log(`🟢 Processing real Vue file: ${fileName} (${content.length} chars)`);

          const result = await parserManager.parseFile(fileName, content);
          expect(result.tree).toBeDefined();

          // Note: Vue files use a special SFC handler, so we don't extract directly
          // This test validates that Vue files can be parsed without errors
          console.log(`📊 Vue file parsed successfully`);
        });
      }
    }
  });

  describe('Cross-Language Project Validation', () => {
    test('should handle multi-language real-world project', async () => {
      const allSymbols: any[] = [];
      const allRelationships: any[] = [];

      // Process all languages in the real-world test directory
      const languages = ['kotlin', 'swift', 'java', 'typescript', 'vue'];

      for (const lang of languages) {
        const langDir = path.join(REAL_WORLD_TEST_DIR, lang);
        if (!fs.existsSync(langDir)) continue;

        const files = fs.readdirSync(langDir).filter(f =>
          f.endsWith('.kt') || f.endsWith('.swift') || f.endsWith('.java') || f.endsWith('.ts') || f.endsWith('.vue')
        );

        for (const fileName of files) {
          const filePath = path.join(langDir, fileName);
          const content = fs.readFileSync(filePath, 'utf-8');

          const result = await parserManager.parseFile(fileName, content);
          if (!result.tree) continue;

          let extractor: any;
          if (fileName.endsWith('.kt')) {
            extractor = new KotlinExtractor('kotlin', fileName, content);
          } else if (fileName.endsWith('.swift')) {
            extractor = new SwiftExtractor('swift', fileName, content);
          } else if (fileName.endsWith('.java')) {
            extractor = new JavaExtractor('java', fileName, content);
          } else if (fileName.endsWith('.ts')) {
            extractor = new TypeScriptExtractor('typescript', fileName, content);
          } else if (fileName.endsWith('.vue')) {
            // Vue files use special SFC handling, just validate they can be parsed
            console.log(`🟢 Vue file ${fileName} parsed successfully`);
            continue;
          }

          if (extractor) {
            const symbols = extractor.extractSymbols(result.tree);
            const relationships = extractor.extractRelationships(result.tree, symbols);

            allSymbols.push(...symbols);
            allRelationships.push(...relationships);
          }
        }
      }

      console.log(`🌍 Multi-language project analysis:`);
      console.log(`📊 Total symbols: ${allSymbols.length}`);
      console.log(`🔗 Total relationships: ${allRelationships.length}`);

      // Validate we extracted meaningful data across languages
      expect(allSymbols.length).toBeGreaterThan(0);

      // Log breakdown by language and symbol type
      const languageBreakdown = allSymbols.reduce((acc, s) => {
        const lang = s.filePath.split('.').pop() || 'unknown';
        if (!acc[lang]) acc[lang] = {};
        acc[lang][s.kind] = (acc[lang][s.kind] || 0) + 1;
        return acc;
      }, {} as Record<string, Record<string, number>>);

      console.log(`🔍 Multi-language breakdown:`, languageBreakdown);
    });
  });
});