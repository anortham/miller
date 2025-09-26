/**
 * File Discovery Debug Tests
 *
 * TDD approach to debug why getAllCodeFiles returns 0 files
 */

import { describe, test, expect, beforeEach } from 'bun:test';
import path from 'path';
import { ParserManager } from '../../parser/parser-manager.js';
import { MillerPaths } from '../../utils/miller-paths.js';
import { initializeLogger, LogLevel } from '../../utils/logger.js';

describe('File Discovery Debug Tests', () => {
  let parserManager: ParserManager;

  beforeEach(async () => {
    // Initialize logger for tests
    const tempPaths = new MillerPaths('/tmp');
    initializeLogger(tempPaths, LogLevel.DEBUG);

    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  test('Parser manager should return supported extensions', () => {
    const extensions = parserManager.getSupportedExtensions();

    console.log('Supported extensions:', extensions);

    // Should have basic extensions
    expect(extensions.length).toBeGreaterThan(0);
    expect(extensions).toContain('.ts');
    expect(extensions).toContain('.js');
    expect(extensions).toContain('.py');
  });

  test('Should detect TypeScript files in src directory', () => {
    const testFile = path.join(process.cwd(), 'src/mcp-server.ts');
    const isSupported = parserManager.isFileSupported(testFile);

    console.log('Is mcp-server.ts supported?', isSupported);
    expect(isSupported).toBe(true);
  });

  test('Manual file discovery should find TypeScript files', async () => {
    const { readdir } = await import('node:fs/promises');

    // Manual check - should find .ts files in src/
    const srcDir = path.join(process.cwd(), 'src');
    const entries = await readdir(srcDir, { withFileTypes: true });

    const tsFiles = entries.filter(entry =>
      entry.isFile() && entry.name.endsWith('.ts')
    );

    console.log('Found .ts files in src:', tsFiles.map(f => f.name));
    expect(tsFiles.length).toBeGreaterThan(0);
  });

  test('Should have language configs with extensions', () => {
    const languages = parserManager.getSupportedLanguages();

    console.log('Supported languages:', languages);
    expect(languages.length).toBeGreaterThan(0);
    expect(languages).toContain('typescript');
    expect(languages).toContain('javascript');
  });

  test('Extension to language mapping should work', () => {
    const extensionToLang = parserManager['extensionToLanguage'];

    console.log('Extension mappings:', Array.from(extensionToLang.entries()).slice(0, 10));

    expect(extensionToLang.has('.ts')).toBe(true);
    expect(extensionToLang.get('.ts')).toBe('typescript');
    expect(extensionToLang.has('.js')).toBe(true);
    expect(extensionToLang.get('.js')).toBe('javascript');
  });

  test('Mock getAllCodeFiles should find files', async () => {
    const { readdir } = await import('node:fs/promises');

    // Replicate the exact logic from getAllCodeFiles
    const files: string[] = [];
    const supportedExtensions = parserManager.getSupportedExtensions();
    const dirPath = path.join(process.cwd(), 'src');

    console.log('Mock test - Supported extensions:', supportedExtensions.slice(0, 10));

    async function walk(dir: string) {
      try {
        const entries = await readdir(dir, { withFileTypes: true });

        for (const entry of entries) {
          const fullPath = path.join(dir, entry.name);

          if (entry.isDirectory()) {
            // Skip common ignore directories
            if (!['node_modules', '.git', '.vscode', 'dist', 'build', 'coverage'].includes(entry.name) &&
                !entry.name.startsWith('.')) {
              await walk(fullPath);
            }
          } else if (entry.isFile()) {
            const ext = path.extname(entry.name);
            console.log(`Checking file: ${entry.name}, ext: ${ext}, supported: ${supportedExtensions.includes(ext)}`);
            if (supportedExtensions.includes(ext)) {
              files.push(fullPath);
            }
          }
        }
      } catch (error) {
        console.warn(`Cannot read directory ${dir}:`, error);
      }
    }

    await walk(dirPath);

    console.log(`Mock getAllCodeFiles found ${files.length} files:`, files.slice(0, 5));
    expect(files.length).toBeGreaterThan(0);
    expect(files.some(f => f.endsWith('.ts'))).toBe(true);
  });
});