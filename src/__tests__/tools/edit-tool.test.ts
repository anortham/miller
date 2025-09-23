import { describe, test, expect, beforeEach, afterEach } from 'bun:test';
import { mkdirSync, writeFileSync, readFileSync, rmSync, existsSync } from 'fs';
import path from 'path';
import DiffMatchPatch = require('diff-match-patch');
import { EditTool, EditAction, EditResult } from '../../tools/edit-tool.js';

/**
 * Edit Tool Test Suite
 *
 * Comprehensive TDD test suite for Miller's surgical editing capabilities
 * using Google's diff-match-patch library for proven reliability.
 *
 * Test scenarios cover:
 * 1. Line-based insert with diff-match-patch
 * 2. Multi-line replace with context preservation
 * 3. Search and replace across files
 * 4. Preview mode returns diff without applying
 * 5. Atomic multi-file operations rollback on failure
 * 6. Handle edge cases (empty files, binary files)
 * 7. Preserve indentation and formatting
 * 8. Concurrent edit protection
 */

// Interfaces are now imported from the actual implementation

describe('EditTool with diff-match-patch', () => {
  let testDir: string;
  let editTool: EditTool;

  beforeEach(() => {
    // Create temporary test directory
    testDir = path.join(process.cwd(), 'test-files');
    if (existsSync(testDir)) {
      rmSync(testDir, { recursive: true });
    }
    mkdirSync(testDir, { recursive: true });

    editTool = new EditTool();
  });

  afterEach(() => {
    // Clean up test directory
    if (existsSync(testDir)) {
      rmSync(testDir, { recursive: true });
    }
  });

  describe('Test 1: Line-based insert with diff-match-patch', () => {
    test('should insert content at exact line position', async () => {
      // Setup test file
      const testFile = path.join(testDir, 'test-insert.ts');
      const originalContent = `function calculateSum(a: number, b: number): number {
  return a + b;
}

function calculateProduct(a: number, b: number): number {
  return a * b;
}`;

      writeFileSync(testFile, originalContent);

      // Test: Insert validation comment at line 2
      const action: EditAction = {
        action: 'insert',
        file: testFile,
        line: 2,
        content: '  // TODO: Add input validation',
        preview: false
      };

      const result = await editTool.execute(action);

      expect(result.success).toBe(true);
      expect(result.changes).toHaveLength(1);
      expect(result.changes[0].file).toBe(testFile);
      expect(result.changes[0].applied).toBe(true);

      // Verify the insertion was made correctly
      const modifiedContent = readFileSync(testFile, 'utf-8');
      const expectedContent = `function calculateSum(a: number, b: number): number {
  // TODO: Add input validation
  return a + b;
}

function calculateProduct(a: number, b: number): number {
  return a * b;
}`;

      expect(modifiedContent).toBe(expectedContent);
    });

    test('should handle insertion at file beginning and end', async () => {
      const testFile = path.join(testDir, 'test-insert-edges.ts');
      const originalContent = `const x = 2;
const y = 2;`;

      writeFileSync(testFile, originalContent);

      // Insert at beginning (line 1)
      let action: EditAction = {
        action: 'insert',
        file: testFile,
        line: 1,
        content: '// File header comment',
        preview: false
      };

      await editTool.execute(action);

      // Insert at end (line after last line)
      action = {
        action: 'insert',
        file: testFile,
        line: 4, // After the newly inserted comment + 2 original lines
        content: 'const z = 3;',
        preview: false
      };

      await editTool.execute(action);

      const finalContent = readFileSync(testFile, 'utf-8');
      const expectedContent = `// File header comment
const x = 2;
const y = 2;
const z = 3;`;

      expect(finalContent).toBe(expectedContent);
    });
  });

  describe('Test 2: Multi-line replace with context preservation', () => {
    test('should replace line ranges while preserving surrounding context', async () => {
      const testFile = path.join(testDir, 'test-replace.ts');
      const originalContent = `class UserService {
  private users: User[] = [];

  getUserById(id: string): User | null {
    const user = this.users.find(u => u.id === id);
    if (!user) {
      return null;
    }
    return user;
  }

  addUser(user: User): void {
    this.users.push(user);
  }
}`;

      writeFileSync(testFile, originalContent);

      // Replace the getUserById method (lines 4-9) with improved version
      const action: EditAction = {
        action: 'replace',
        file: testFile,
        line: 4,
        endLine: 9,
        content: `  async getUserById(id: string): Promise<User | null> {
    try {
      const user = await this.userRepository.findById(id);
      return user || null;
    } catch (error) {
      this.logger.error('Failed to get user', error);
      throw new Error('User retrieval failed');
    }
  }`,
        preview: false
      };

      const result = await editTool.execute(action);

      expect(result.success).toBe(true);
      expect(result.changes[0].linesChanged).toBe(6); // 6 lines replaced

      const modifiedContent = readFileSync(testFile, 'utf-8');

      // Verify the class structure is preserved
      expect(modifiedContent).toContain('class UserService {');
      expect(modifiedContent).toContain('private users: User[] = [];');
      expect(modifiedContent).toContain('addUser(user: User): void {');

      // Verify the replacement was made
      expect(modifiedContent).toContain('async getUserById');
      expect(modifiedContent).toContain('userRepository.findById');
      expect(modifiedContent).toContain('this.logger.error');
    });

    test('should preserve indentation when replacing content', async () => {
      const testFile = path.join(testDir, 'test-indentation.ts');
      const originalContent = `    if (condition) {
      doSomething();
      doSomethingElse();
    }`;

      writeFileSync(testFile, originalContent);

      const action: EditAction = {
        action: 'replace',
        file: testFile,
        line: 2,
        endLine: 3,
        content: `      // Enhanced functionality
      await doSomethingAsync();
      await doSomethingElseAsync();
      this.logOperation();`,
        preserveIndentation: true,
        preview: false
      };

      await editTool.execute(action);

      const modifiedContent = readFileSync(testFile, 'utf-8');

      // Verify indentation is preserved
      expect(modifiedContent).toContain('    if (condition) {');
      expect(modifiedContent).toContain('      // Enhanced functionality');
      expect(modifiedContent).toContain('      await doSomethingAsync()');
      expect(modifiedContent).toContain('    }');
    });
  });

  describe('Test 3: Search and replace across files', () => {
    test('should perform search and replace across multiple files with file patterns', async () => {
      // Create multiple test files
      const files = [
        'service1.ts',
        'service2.ts',
        'utils.ts',
        'config.js' // Should be excluded by pattern
      ];

      const originalContent = `export class ServiceClass {
  getUserDataAsyncAsyncAsyncAsyncAsyncAsync() {
    return this.getData();
  }
}`;

      files.forEach(file => {
        writeFileSync(path.join(testDir, file), originalContent);
      });

      // Search and replace across TypeScript files only
      const action: EditAction = {
        action: 'search_replace',
        searchText: 'getUserDataAsyncAsyncAsyncAsyncAsyncAsync',
        replaceText: 'getUserDataAsyncAsyncAsyncAsyncAsyncAsyncAsync',
        filePattern: path.join(testDir, '**/*.ts'),
        preview: false,
        atomic: true
      };

      const result = await editTool.execute(action);

      expect(result.success).toBe(true);
      expect(result.changes).toHaveLength(3); // Only TS files

      // Verify changes in TypeScript files
      ['service1.ts', 'service2.ts', 'utils.ts'].forEach(file => {
        const content = readFileSync(path.join(testDir, file), 'utf-8');
        expect(content).toContain('getUserDataAsyncAsyncAsyncAsyncAsyncAsyncAsync');
        expect(content).not.toContain('getUserDataAsyncAsyncAsyncAsyncAsyncAsync()');
      });

      // Verify JavaScript file was not changed
      const jsContent = readFileSync(path.join(testDir, 'config.js'), 'utf-8');
      expect(jsContent).toContain('getUserDataAsyncAsyncAsyncAsyncAsyncAsync');
    });

    test('should handle search text not found gracefully', async () => {
      const testFile = path.join(testDir, 'test-not-found.ts');
      writeFileSync(testFile, 'const x = 2;');

      const action: EditAction = {
        action: 'search_replace',
        searchText: 'newFunction',
        replaceText: 'newFunction',
        filePattern: path.join(testDir, '**/*.ts'),
        preview: false
      };

      const result = await editTool.execute(action);

      expect(result.success).toBe(true);
      expect(result.changes).toHaveLength(0); // No changes made
    });
  });

  describe('Test 4: Preview mode returns diff without applying', () => {
    test('should return diff preview without modifying files', async () => {
      const testFile = path.join(testDir, 'test-preview.ts');
      const originalContent = `function newFunction() {
  return 'new';
}`;

      writeFileSync(testFile, originalContent);

      const action: EditAction = {
        action: 'replace',
        file: testFile,
        line: 1,
        endLine: 3,
        content: `function newFunction() {
  return 'new';
}`,
        preview: true
      };

      const result = await editTool.execute(action);

      expect(result.success).toBe(true);
      expect(result.changes[0].preview).toBeDefined();
      expect(result.changes[0].applied).toBe(false);

      // Verify file was not modified
      const fileContent = readFileSync(testFile, 'utf-8');
      expect(fileContent).toBe(originalContent);

      // Verify preview contains diff information
      const preview = result.changes[0].preview!;
      expect(preview).toContain('newFunction');
      expect(preview).toContain('newFunction');
    });

    test('should show context lines in preview when requested', async () => {
      const testFile = path.join(testDir, 'test-preview-context.ts');
      const originalContent = `// File header
class TestClass {
  method1() {
    return 1;
  }

  method2() {
    return 2;
  }

  method3() {
    return 3;
  }
}
// File footer`;

      writeFileSync(testFile, originalContent);

      const action: EditAction = {
        action: 'replace',
        file: testFile,
        line: 7,
        endLine: 9,
        content: `  async method2() {
    return await this.asyncOperation();
  }`,
        preview: true,
        contextLines: 2
      };

      const result = await editTool.execute(action);

      const preview = result.changes[0].preview!;

      // Should show 2 lines before and after the change
      expect(preview).toContain('method1()');
      expect(preview).toContain('method3()');
      expect(preview).toContain('async method2()');
    });
  });

  describe('Test 5: Atomic multi-file operations rollback on failure', () => {
    test('should rollback all changes if any file fails in atomic operation', async () => {
      // Create test files
      const file1 = path.join(testDir, 'file1.ts');
      const file2 = path.join(testDir, 'file2.ts');
      const file3 = path.join(testDir, 'readonly.ts');

      const content = 'const x = 2;';
      writeFileSync(file1, content);
      writeFileSync(file2, content);
      writeFileSync(file3, content);

      // Make one file readonly to simulate failure
      const fs = require('fs');
      fs.chmodSync(file3, 0o444); // Read-only

      const action: EditAction = {
        action: 'search_replace',
        searchText: 'const x = 2;',
        replaceText: 'const x = 3;',
        filePattern: path.join(testDir, '**/*.ts'),
        atomic: true,
        preview: false
      };

      const result = await editTool.execute(action);

      expect(result.success).toBe(false);
      expect(result.error).toContain('permission');

      // Verify no files were modified (rollback worked)
      expect(readFileSync(file1, 'utf-8')).toBe(content);
      expect(readFileSync(file2, 'utf-8')).toBe(content);
      expect(readFileSync(file3, 'utf-8')).toBe(content);

      // Cleanup
      fs.chmodSync(file3, 0o644);
    });

    test('should succeed when all files can be modified in atomic operation', async () => {
      const files = ['file1.ts', 'file2.ts', 'file3.ts'];
      const content = 'const oldValue = 1;';

      files.forEach(file => {
        writeFileSync(path.join(testDir, file), content);
      });

      const action: EditAction = {
        action: 'search_replace',
        searchText: 'oldValue',
        replaceText: 'newValue',
        filePattern: path.join(testDir, '**/*.ts'),
        atomic: true,
        preview: false
      };

      const result = await editTool.execute(action);

      expect(result.success).toBe(true);
      expect(result.changes).toHaveLength(3);

      // Verify all files were modified
      files.forEach(file => {
        const modifiedContent = readFileSync(path.join(testDir, file), 'utf-8');
        expect(modifiedContent).toBe('const newValue = 1;');
      });
    });
  });

  describe('Test 6: Handle edge cases (empty files, binary files)', () => {
    test('should handle empty files gracefully', async () => {
      const testFile = path.join(testDir, 'empty.ts');
      writeFileSync(testFile, '');

      const action: EditAction = {
        action: 'insert',
        file: testFile,
        line: 1,
        content: '// First line in empty file',
        preview: false
      };

      const result = await editTool.execute(action);

      expect(result.success).toBe(true);
      expect(readFileSync(testFile, 'utf-8')).toBe('// First line in empty file');
    });

    test('should reject binary files with clear error message', async () => {
      const binaryFile = path.join(testDir, 'test.png');

      // Create a mock binary file
      const binaryContent = Buffer.from([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
      writeFileSync(binaryFile, binaryContent);

      const action: EditAction = {
        action: 'insert',
        file: binaryFile,
        line: 1,
        content: 'text',
        preview: false
      };

      const result = await editTool.execute(action);

      expect(result.success).toBe(false);
      expect(result.error).toContain('binary');
    });

    test('should handle very large files efficiently', async () => {
      const largeFile = path.join(testDir, 'large.ts');

      // Create a large file (1000 lines)
      const lines = Array.from({ length: 1000 }, (_, i) =>
        `const line${i} = ${i};`
      );
      writeFileSync(largeFile, lines.join('\n'));

      const action: EditAction = {
        action: 'insert',
        file: largeFile,
        line: 500,
        content: '// Inserted in middle of large file',
        preview: false
      };

      const startTime = Date.now();
      const result = await editTool.execute(action);
      const duration = Date.now() - startTime;

      expect(result.success).toBe(true);
      expect(duration).toBeLessThan(1000); // Should complete within 1 second

      const content = readFileSync(largeFile, 'utf-8');
      expect(content).toContain('// Inserted in middle of large file');
    });
  });

  describe('Test 7: Preserve indentation and formatting', () => {
    test('should detect and preserve existing indentation style', async () => {
      const testFile = path.join(testDir, 'indented.ts');
      const originalContent = `class MyClass {
    constructor() {
        this.value = 1;
    }

    method() {
        return this.value;
    }
}`;

      writeFileSync(testFile, originalContent);

      const action: EditAction = {
        action: 'insert',
        file: testFile,
        line: 4,
        content: 'this.name = "test";',
        preserveIndentation: true,
        preview: false
      };

      await editTool.execute(action);

      const modifiedContent = readFileSync(testFile, 'utf-8');

      // Should preserve the 8-space indentation
      expect(modifiedContent).toContain('        this.name = "test";');
    });

    test('should handle mixed indentation styles correctly', async () => {
      const testFile = path.join(testDir, 'mixed-indent.ts');
      const originalContent = `function example() {
\tif (true) {  // Tab indented
\t\treturn 1;  // Double tab
\t}
    else {  // Space indented
        return 2;  // Spaces
    }
}`;

      writeFileSync(testFile, originalContent);

      const action: EditAction = {
        action: 'insert',
        file: testFile,
        line: 3,
        content: 'console.log("debug");',
        preserveIndentation: true,
        preview: false
      };

      await editTool.execute(action);

      const modifiedContent = readFileSync(testFile, 'utf-8');

      // Should preserve tab indentation at that level
      expect(modifiedContent).toContain('\t\tconsole.log("debug");');
    });
  });

  describe('Test 8: Concurrent edit protection', () => {
    test('should detect concurrent modifications and prevent conflicts', async () => {
      const testFile = path.join(testDir, 'concurrent.ts');
      const originalContent = 'const x = 2;';

      writeFileSync(testFile, originalContent);

      // Simulate file being modified externally during edit operation
      const action1: EditAction = {
        action: 'replace',
        file: testFile,
        line: 1,
        endLine: 1,
        content: 'const x = 2;',
        preview: false
      };

      // Start first edit
      const promise1 = editTool.execute(action1);

      // Modify file externally (simulating concurrent access)
      setTimeout(() => {
        writeFileSync(testFile, 'const x = 999; // External change');
      }, 10);

      // Start second edit
      const action2: EditAction = {
        action: 'insert',
        file: testFile,
        line: 1,
        content: '// Comment',
        preview: false
      };

      const promise2 = editTool.execute(action2);

      const results = await Promise.all([promise1, promise2]);

      // At least one should detect the conflict or file lock
      const hasConflict = results.some(result =>
        !result.success && (result.error?.includes('conflict') || result.error?.includes('locked'))
      );
      expect(hasConflict).toBe(true);
    });

    test('should use file locking to prevent simultaneous edits', async () => {
      const testFile = path.join(testDir, 'locked.ts');
      writeFileSync(testFile, 'const x = 2;');

      const action: EditAction = {
        action: 'insert',
        file: testFile,
        line: 1,
        content: '// Comment',
        preview: false
      };

      // Start two edits simultaneously
      const promise1 = editTool.execute(action);
      const promise2 = editTool.execute(action);

      const results = await Promise.all([promise1, promise2]);

      // One should succeed, one should fail due to lock
      expect(results.filter(r => r.success)).toHaveLength(1);
      expect(results.filter(r => !r.success)).toHaveLength(1);

      const failedResult = results.find(r => !r.success);
      expect(failedResult?.error).toContain('lock');
    });
  });

  describe('Integration Tests', () => {
    test('should maintain diff-match-patch configuration for code editing', async () => {
      // Verify diff-match-patch is configured correctly for code
      const dmp = new DiffMatchPatch();

      expect(dmp.Diff_Timeout).toBe(1.0);
      expect(dmp.Match_Threshold).toBe(0.5);
    });

    test('should generate meaningful diff output for preview mode', async () => {
      const testFile = path.join(testDir, 'diff-test.ts');
      const originalContent = `function hello() {
  console.log("Hello");
}`;

      writeFileSync(testFile, originalContent);

      const action: EditAction = {
        action: 'replace',
        file: testFile,
        line: 2,
        endLine: 2,
        content: '  console.log("Hello, World!");',
        preview: true
      };

      const result = await editTool.execute(action);

      expect(result.success).toBe(true);

      const preview = result.changes[0].preview!;
      expect(preview).toBeTruthy();

      // Preview should show what changed
      expect(preview).toContain('Hello');
      expect(preview).toContain('World');
    });
  });
});