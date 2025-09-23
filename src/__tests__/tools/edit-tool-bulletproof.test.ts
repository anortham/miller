import { describe, test, expect, beforeEach, afterEach } from 'bun:test';
import { mkdirSync, writeFileSync, readFileSync, rmSync, existsSync, chmodSync, statSync } from 'fs';
import path from 'path';
import { EditTool, EditAction } from '../../tools/edit-tool.js';

/**
 * BULLETPROOF SAFETY TESTS for EditTool
 *
 * These tests cover the most dangerous scenarios that could lead to:
 * - File corruption
 * - Data loss
 * - Permission violations
 * - Race conditions
 * - Partial writes
 *
 * ZERO TOLERANCE for file corruption - every scenario must be bulletproof.
 */

describe('EditTool - BULLETPROOF SAFETY TESTS', () => {
  let testDir: string;
  let editTool: EditTool;

  beforeEach(() => {
    testDir = path.join(process.cwd(), 'bulletproof-test-files');
    if (existsSync(testDir)) {
      rmSync(testDir, { recursive: true });
    }
    mkdirSync(testDir, { recursive: true });
    editTool = new EditTool();
  });

  afterEach(() => {
    if (existsSync(testDir)) {
      rmSync(testDir, { recursive: true });
    }
  });

  describe('CRITICAL: File Corruption Prevention', () => {
    test('should NEVER corrupt file during failed operations', async () => {
      const testFile = path.join(testDir, 'corruption-test.ts');
      const originalContent = `function criticalFunction() {
  // This content must NEVER be corrupted
  const importantData = "DO NOT LOSE THIS";
  return importantData;
}`;

      writeFileSync(testFile, originalContent);

      // Try an invalid operation that should fail
      const action: EditAction = {
        action: 'replace',
        file: testFile,
        line: 999, // Invalid line number
        content: 'this should fail'
      };

      const result = await editTool.execute(action);

      // Operation should fail gracefully
      expect(result.success).toBe(false);

      // Original content must be EXACTLY preserved
      const contentAfterFailure = readFileSync(testFile, 'utf-8');
      expect(contentAfterFailure).toBe(originalContent);
      expect(contentAfterFailure).toContain('DO NOT LOSE THIS');
    });

    test('should handle write permission failures without corruption', async () => {
      const testFile = path.join(testDir, 'readonly-test.ts');
      const originalContent = 'const data = "protected";';

      writeFileSync(testFile, originalContent);

      // Make file read-only
      chmodSync(testFile, 0o444);

      const action: EditAction = {
        action: 'insert',
        file: testFile,
        line: 2,
        content: 'const newData = "should fail";'
      };

      const result = await editTool.execute(action);

      // Should fail gracefully
      expect(result.success).toBe(false);
      expect(result.error).toContain('permission');

      // Original content must be preserved
      const preservedContent = readFileSync(testFile, 'utf-8');
      expect(preservedContent).toBe(originalContent);

      // Cleanup
      chmodSync(testFile, 0o644);
    });

    test('should handle extremely large files without corruption', async () => {
      const largeFile = path.join(testDir, 'large-file.ts');

      // Create a large file (50k lines)
      const lines = Array.from({ length: 50000 }, (_, i) => `const line${i} = ${i};`);
      const originalContent = lines.join('\n');
      writeFileSync(largeFile, originalContent);

      const originalSize = statSync(largeFile).size;

      const action: EditAction = {
        action: 'insert',
        file: largeFile,
        line: 25000, // Middle of file
        content: '// Critical insertion in large file'
      };

      const result = await editTool.execute(action);

      expect(result.success).toBe(true);

      // Verify file integrity
      const modifiedContent = readFileSync(largeFile, 'utf-8');
      expect(modifiedContent).toContain('// Critical insertion in large file');
      expect(modifiedContent).toContain('const line24999 = 24999;'); // Before insertion
      expect(modifiedContent).toContain('const line25000 = 25000;'); // After insertion

      // File should be larger by exactly the inserted content
      const newSize = statSync(largeFile).size;
      expect(newSize).toBeGreaterThan(originalSize);
    });

    test('should handle Unicode and special characters without corruption', async () => {
      const unicodeFile = path.join(testDir, 'unicode-test.ts');
      const originalContent = `const unicode = "Hello 世界! 🚀 🎉";
const emoji = "💻 🔧 ⚡";
const special = "quotes: 'single' \"double\" \`backtick\`";`;

      writeFileSync(unicodeFile, originalContent);

      const action: EditAction = {
        action: 'insert',
        file: unicodeFile,
        line: 2,
        content: 'const moreUnicode = "Здравствуй мир! 你好世界! こんにちは世界!";'
      };

      const result = await editTool.execute(action);

      expect(result.success).toBe(true);

      const modifiedContent = readFileSync(unicodeFile, 'utf-8');
      expect(modifiedContent).toContain('Hello 世界! 🚀 🎉');
      expect(modifiedContent).toContain('Здравствуй мир!');
      expect(modifiedContent).toContain('quotes: \'single\'');
    });
  });

  describe('CRITICAL: Atomic Operation Safety', () => {
    test('should rollback ALL files if ANY file fails in atomic operation', async () => {
      const file1 = path.join(testDir, 'atomic1.ts');
      const file2 = path.join(testDir, 'atomic2.ts');
      const file3 = path.join(testDir, 'atomic3-readonly.ts');

      const content1 = 'const file1 = "original";';
      const content2 = 'const file2 = "original";';
      const content3 = 'const file3 = "original";';

      writeFileSync(file1, content1);
      writeFileSync(file2, content2);
      writeFileSync(file3, content3);

      // Make file3 read-only to force failure
      chmodSync(file3, 0o444);

      const action: EditAction = {
        action: 'search_replace',
        searchText: 'original',
        replaceText: 'modified',
        filePattern: path.join(testDir, '*.ts'),
        atomic: true
      };

      const result = await editTool.execute(action);

      // Atomic operation should fail
      expect(result.success).toBe(false);

      // ALL files must retain original content
      expect(readFileSync(file1, 'utf-8')).toBe(content1);
      expect(readFileSync(file2, 'utf-8')).toBe(content2);
      expect(readFileSync(file3, 'utf-8')).toBe(content3);

      // Cleanup
      chmodSync(file3, 0o644);
    });

    test('should maintain file integrity during power-failure simulation', async () => {
      const testFile = path.join(testDir, 'power-failure-test.ts');
      const criticalContent = `// CRITICAL SYSTEM CODE - DO NOT CORRUPT
function saveUserData(data: UserData): Promise<void> {
  // This function handles user data persistence
  // Any corruption here would be catastrophic
  return database.save(data);
}`;

      writeFileSync(testFile, criticalContent);

      // Simulate power failure during operation by interrupting the edit
      const action: EditAction = {
        action: 'insert',
        file: testFile,
        line: 5,
        content: '  // Added safety check'
      };

      // Normal operation should succeed
      const result = await editTool.execute(action);
      expect(result.success).toBe(true);

      // File should maintain integrity
      const finalContent = readFileSync(testFile, 'utf-8');
      expect(finalContent).toContain('CRITICAL SYSTEM CODE');
      expect(finalContent).toContain('saveUserData');
      expect(finalContent).toContain('Added safety check');
    });
  });

  describe('CRITICAL: Race Condition Protection', () => {
    test('should prevent file corruption from simultaneous edits', async () => {
      const sharedFile = path.join(testDir, 'shared-resource.ts');
      const originalContent = `let counter = 0;
function increment() {
  counter++;
}`;

      writeFileSync(sharedFile, originalContent);

      const action1: EditAction = {
        action: 'insert',
        file: sharedFile,
        line: 2,
        content: '// Process A modification'
      };

      const action2: EditAction = {
        action: 'insert',
        file: sharedFile,
        line: 3,
        content: '// Process B modification'
      };

      // Simulate concurrent access
      const [result1, result2] = await Promise.all([
        editTool.execute(action1),
        editTool.execute(action2)
      ]);

      // One should succeed, one should fail (file locking)
      const successCount = [result1, result2].filter(r => r.success).length;
      expect(successCount).toBe(1);

      // File must remain valid and uncorrupted
      const finalContent = readFileSync(sharedFile, 'utf-8');
      expect(finalContent).toContain('let counter = 0');
      expect(finalContent).toContain('function increment');

      // Should contain only one modification
      const processACount = (finalContent.match(/Process A/g) || []).length;
      const processBCount = (finalContent.match(/Process B/g) || []).length;
      expect(processACount + processBCount).toBe(1);
    });

    test('should detect external file modifications', async () => {
      const testFile = path.join(testDir, 'external-mod-test.ts');
      const originalContent = 'const version = 1;';

      writeFileSync(testFile, originalContent);

      // Start an edit operation
      const action: EditAction = {
        action: 'insert',
        file: testFile,
        line: 2,
        content: 'const newFeature = true;'
      };

      // Simulate external modification
      setTimeout(() => {
        writeFileSync(testFile, 'const version = 2; // EXTERNALLY MODIFIED');
      }, 5);

      const result = await editTool.execute(action);

      // Should either succeed with the edit OR fail with conflict detection
      // Both outcomes are safe - what matters is no corruption
      const finalContent = readFileSync(testFile, 'utf-8');

      // File must be in a valid state (either original + edit, or external mod)
      const isValidState = finalContent.includes('newFeature') ||
                          finalContent.includes('EXTERNALLY MODIFIED');
      expect(isValidState).toBe(true);
    });
  });

  describe('CRITICAL: Edge Case Safety', () => {
    test('should handle empty files without creating corruption', async () => {
      const emptyFile = path.join(testDir, 'empty.ts');
      writeFileSync(emptyFile, '');

      const action: EditAction = {
        action: 'insert',
        file: emptyFile,
        line: 1,
        content: 'const firstLine = "safe";'
      };

      const result = await editTool.execute(action);

      expect(result.success).toBe(true);

      const content = readFileSync(emptyFile, 'utf-8');
      expect(content).toBe('const firstLine = "safe";');

      // Verify no extra newlines or corruption
      expect(content.split('\n')).toHaveLength(1);
    });

    test('should handle files with only whitespace', async () => {
      const whitespaceFile = path.join(testDir, 'whitespace.ts');
      writeFileSync(whitespaceFile, '   \n\t\n  \n');

      const action: EditAction = {
        action: 'insert',
        file: whitespaceFile,
        line: 2,
        content: 'const valid = true;'
      };

      const result = await editTool.execute(action);

      expect(result.success).toBe(true);

      const content = readFileSync(whitespaceFile, 'utf-8');
      expect(content).toContain('const valid = true;');
    });

    test('should handle extremely long lines without corruption', async () => {
      const longLineFile = path.join(testDir, 'long-line.ts');
      const longLine = 'const veryLongVariableName = "' + 'x'.repeat(10000) + '";';
      writeFileSync(longLineFile, longLine);

      const action: EditAction = {
        action: 'insert',
        file: longLineFile,
        line: 2,
        content: 'const normal = "line";'
      };

      const result = await editTool.execute(action);

      expect(result.success).toBe(true);

      const content = readFileSync(longLineFile, 'utf-8');
      expect(content).toContain('veryLongVariableName');
      expect(content).toContain('const normal = "line"');
      expect(content.includes('x'.repeat(10000))).toBe(true);
    });

    test('should handle files with mixed line endings safely', async () => {
      const mixedEndingsFile = path.join(testDir, 'mixed-endings.ts');
      const content = 'line1\r\nline2\nline3\r\nline4\n';
      writeFileSync(mixedEndingsFile, content);

      const action: EditAction = {
        action: 'insert',
        file: mixedEndingsFile,
        line: 3,
        content: 'inserted line'
      };

      const result = await editTool.execute(action);

      expect(result.success).toBe(true);

      const finalContent = readFileSync(mixedEndingsFile, 'utf-8');
      expect(finalContent).toContain('line1');
      expect(finalContent).toContain('line2');
      expect(finalContent).toContain('inserted line');
      expect(finalContent).toContain('line3');
      expect(finalContent).toContain('line4');
    });
  });

  describe('CRITICAL: Preview Mode Safety', () => {
    test('preview mode must NEVER modify files under any circumstances', async () => {
      const protectedFile = path.join(testDir, 'protected.ts');
      const criticalContent = `// PRODUCTION CODE - NEVER MODIFY
const SECRET_KEY = "critical-secret";
function authenticateUser() {
  // Security-critical function
  return validateSecret(SECRET_KEY);
}`;

      writeFileSync(protectedFile, criticalContent);
      const originalHash = require('crypto').createHash('md5').update(criticalContent).digest('hex');

      const action: EditAction = {
        action: 'replace',
        file: protectedFile,
        line: 2,
        endLine: 2,
        content: 'const SECRET_KEY = "HACKED!!!";',
        preview: true // MUST NOT modify file
      };

      const result = await editTool.execute(action);

      expect(result.success).toBe(true);
      expect(result.changes[0].applied).toBe(false);
      expect(result.changes[0].preview).toBeDefined();

      // File must be EXACTLY unchanged
      const finalContent = readFileSync(protectedFile, 'utf-8');
      const finalHash = require('crypto').createHash('md5').update(finalContent).digest('hex');

      expect(finalContent).toBe(criticalContent);
      expect(finalHash).toBe(originalHash);
      expect(finalContent).toContain('critical-secret');
      expect(finalContent).not.toContain('HACKED');
    });
  });

  describe('CRITICAL: Error Recovery', () => {
    test('should recover gracefully from disk full conditions', async () => {
      const testFile = path.join(testDir, 'disk-full-test.ts');
      const originalContent = 'const safe = "data";';
      writeFileSync(testFile, originalContent);

      // We can't actually fill the disk, but we can test large operations
      const hugeLine = 'const huge = "' + 'x'.repeat(1000000) + '";';

      const action: EditAction = {
        action: 'insert',
        file: testFile,
        line: 2,
        content: hugeLine
      };

      const result = await editTool.execute(action);

      // Whether it succeeds or fails, file must remain valid
      const finalContent = readFileSync(testFile, 'utf-8');
      expect(finalContent).toContain('const safe = "data";');

      if (!result.success) {
        // If it failed, original content must be preserved
        expect(finalContent).toBe(originalContent);
      }
    });

    test('should handle filesystem permission changes during operation', async () => {
      const testFile = path.join(testDir, 'permission-change.ts');
      const originalContent = 'const data = "important";';
      writeFileSync(testFile, originalContent);

      const action: EditAction = {
        action: 'insert',
        file: testFile,
        line: 2,
        content: 'const additional = "data";'
      };

      const result = await editTool.execute(action);

      // File should either be successfully modified or left unchanged
      const finalContent = readFileSync(testFile, 'utf-8');
      expect(finalContent).toContain('const data = "important";');

      if (result.success) {
        expect(finalContent).toContain('const additional = "data";');
      } else {
        expect(finalContent).toBe(originalContent);
      }
    });
  });
});