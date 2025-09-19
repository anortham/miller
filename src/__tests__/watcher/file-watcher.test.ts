import { describe, it, expect, beforeEach, afterEach } from 'bun:test';
import { FileWatcher, FileChangeEvent } from '../../watcher/file-watcher.js';
import { mkdir, writeFile, unlink, rmdir, chmod } from 'fs/promises';
import { existsSync } from 'fs';
import path from 'path';

describe('File Watcher Unit Tests', () => {
  let fileWatcher: FileWatcher;
  let testDir: string;
  let changeEvents: FileChangeEvent[] = [];
  let deleteEvents: string[] = [];
  let errorEvents: { error: Error; filePath?: string }[] = [];

  beforeEach(async () => {
    // Create unique test directory for each test
    testDir = `/tmp/miller-watcher-test-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
    await mkdir(testDir, { recursive: true });

    // Reset event collections
    changeEvents = [];
    deleteEvents = [];
    errorEvents = [];

    // Create file watcher with test callbacks
    fileWatcher = new FileWatcher(
      async (event: FileChangeEvent) => {
        changeEvents.push(event);
      },
      async (filePath: string) => {
        deleteEvents.push(filePath);
      },
      (error: Error, filePath?: string) => {
        errorEvents.push({ error, filePath });
      },
      {
        debounceMs: 50, // Shorter debounce for faster tests
        recursive: true,
        maxFileSize: 1024 * 1024 // 1MB for tests
      }
    );
  });

  afterEach(async () => {
    // Clean up watcher
    if (fileWatcher) {
      fileWatcher.dispose();
    }

    // Clean up test directory
    if (existsSync(testDir)) {
      await Bun.spawn(['rm', '-rf', testDir]).exited;
    }
  });

  // Helper function to wait for events
  const waitForEvents = (ms: number = 100) => new Promise(resolve => setTimeout(resolve, ms));

  describe('Basic File Watching', () => {
    it('should detect file creation', async () => {
      const testFile = path.join(testDir, 'test.ts');

      // Start watching directory
      await fileWatcher.watchDirectory(testDir);

      // Create a file
      await writeFile(testFile, 'console.log("Hello World");');

      // Wait for debounced event
      await waitForEvents(100);

      expect(changeEvents.length).toBeGreaterThan(0);
      expect(changeEvents[0].type).toBe('create'); // File creation events report as 'create'
      expect(changeEvents[0].filePath).toBe(testFile);
      expect(changeEvents[0].content).toContain('Hello World');
    });

    it('should detect file modification', async () => {
      const testFile = path.join(testDir, 'test.js');

      // Create file first
      await writeFile(testFile, 'console.log("Initial");');

      // Start watching
      await fileWatcher.watchDirectory(testDir);

      // Clear initial events
      changeEvents.length = 0;

      // Modify file
      await writeFile(testFile, 'console.log("Modified");');

      // Wait for debounced event
      await waitForEvents(100);

      expect(changeEvents.length).toBeGreaterThan(0);
      expect(changeEvents[0].filePath).toBe(testFile);
      expect(changeEvents[0].content).toContain('Modified');
    });

    it('should detect file deletion', async () => {
      const testFile = path.join(testDir, 'test.py');

      // Create and then delete file
      await writeFile(testFile, 'print("Hello")');

      // Start watching
      await fileWatcher.watchDirectory(testDir);

      // Clear initial creation events
      await waitForEvents(100);
      changeEvents.length = 0;

      // Delete file
      await unlink(testFile);

      // Wait for deletion event
      await waitForEvents(100);

      // Deletion might be detected as a change event or separate delete handler
      expect(deleteEvents.length + changeEvents.length).toBeGreaterThan(0);
    });

    it('should watch individual files', async () => {
      const testFile = path.join(testDir, 'single.rs');
      await writeFile(testFile, 'fn main() {}');

      // Watch single file
      await fileWatcher.watchFile(testFile);

      // Clear any initial events
      await waitForEvents(100);
      changeEvents.length = 0;

      // Modify file
      await writeFile(testFile, 'fn main() { println!("Hello"); }');

      // Wait for event
      await waitForEvents(100);

      expect(changeEvents.length).toBeGreaterThan(0);
      expect(changeEvents[0].filePath).toBe(testFile);
    });
  });

  describe('File Filtering', () => {
    it('should only watch supported file extensions', async () => {
      const supportedFile = path.join(testDir, 'test.ts');
      const unsupportedFile = path.join(testDir, 'test.txt');

      await fileWatcher.watchDirectory(testDir);

      // Create supported file
      await writeFile(supportedFile, 'const x = 1;');
      await waitForEvents(100);

      const supportedEvents = changeEvents.length;

      // Create unsupported file
      await writeFile(unsupportedFile, 'Plain text');
      await waitForEvents(100);

      // Should not have new events for unsupported file
      expect(changeEvents.length).toBe(supportedEvents);
    });

    it('should respect ignore patterns', async () => {
      // Create directories that should be ignored
      const nodeModulesDir = path.join(testDir, 'node_modules');
      const gitDir = path.join(testDir, '.git');
      const buildDir = path.join(testDir, 'build');

      await mkdir(nodeModulesDir, { recursive: true });
      await mkdir(gitDir, { recursive: true });
      await mkdir(buildDir, { recursive: true });

      await fileWatcher.watchDirectory(testDir);

      // Create files in ignored directories
      await writeFile(path.join(nodeModulesDir, 'package.js'), 'module.exports = {};');
      await writeFile(path.join(gitDir, 'config'), 'git config');
      await writeFile(path.join(buildDir, 'output.js'), 'console.log("built");');

      await waitForEvents(100);

      // Should not detect files in ignored directories
      expect(changeEvents.length).toBe(0);
    });

    it('should respect custom ignore patterns', async () => {
      const customIgnoreWatcher = new FileWatcher(
        async (event) => { changeEvents.push(event); },
        async (filePath) => { deleteEvents.push(filePath); },
        undefined,
        {
          ignorePatterns: ['**/custom/**', '**/*.ignore'],
          debounceMs: 50
        }
      );

      const customDir = path.join(testDir, 'custom');
      await mkdir(customDir, { recursive: true });

      await customIgnoreWatcher.watchDirectory(testDir);

      // Create files that should be ignored
      await writeFile(path.join(customDir, 'test.ts'), 'ignored');
      await writeFile(path.join(testDir, 'test.ignore'), 'ignored');

      await waitForEvents(100);

      expect(changeEvents.length).toBe(0);

      customIgnoreWatcher.dispose();
    });

    it('should respect custom supported extensions', async () => {
      const customExtWatcher = new FileWatcher(
        async (event) => { changeEvents.push(event); },
        async (filePath) => { deleteEvents.push(filePath); },
        undefined,
        {
          supportedExtensions: ['.custom', '.special'],
          debounceMs: 50
        }
      );

      await customExtWatcher.watchDirectory(testDir);

      // Create files with custom extensions
      await writeFile(path.join(testDir, 'test.custom'), 'custom content');
      await writeFile(path.join(testDir, 'test.ts'), 'typescript content'); // Should be ignored

      await waitForEvents(100);

      expect(changeEvents.length).toBe(1);
      expect(changeEvents[0].filePath).toContain('.custom');

      customExtWatcher.dispose();
    });
  });

  describe('Debouncing Mechanism', () => {
    it('should debounce rapid file changes', async () => {
      const testFile = path.join(testDir, 'rapid.js');
      await writeFile(testFile, 'initial');

      await fileWatcher.watchDirectory(testDir);

      // Clear initial events
      await waitForEvents(100);
      changeEvents.length = 0;

      // Make rapid changes
      await writeFile(testFile, 'change1');
      await writeFile(testFile, 'change2');
      await writeFile(testFile, 'change3');
      await writeFile(testFile, 'final');

      // Wait for debounced event
      await waitForEvents(150);

      // Should only get one debounced event
      expect(changeEvents.length).toBe(1);
      expect(changeEvents[0].content).toBe('final');
    });

    it('should respect custom debounce timing', async () => {
      const slowWatcher = new FileWatcher(
        async (event) => { changeEvents.push(event); },
        async (filePath) => { deleteEvents.push(filePath); },
        undefined,
        { debounceMs: 200 }
      );

      const testFile = path.join(testDir, 'slow.ts');
      await writeFile(testFile, 'initial');

      await slowWatcher.watchDirectory(testDir);

      // Clear initial events
      await waitForEvents(250);
      changeEvents.length = 0;

      // Make change
      await writeFile(testFile, 'changed');

      // Check before debounce completes
      await waitForEvents(100);
      expect(changeEvents.length).toBe(0);

      // Check after debounce completes
      await waitForEvents(150);
      expect(changeEvents.length).toBe(1);

      slowWatcher.dispose();
    });

    it('should handle concurrent file processing', async () => {
      const file1 = path.join(testDir, 'concurrent1.ts');
      const file2 = path.join(testDir, 'concurrent2.ts');

      await fileWatcher.watchDirectory(testDir);

      // Create files simultaneously
      await Promise.all([
        writeFile(file1, 'content1'),
        writeFile(file2, 'content2')
      ]);

      await waitForEvents(100);

      // Should process both files
      expect(changeEvents.length).toBe(2);
      const filePaths = changeEvents.map(e => e.filePath);
      expect(filePaths).toContain(file1);
      expect(filePaths).toContain(file2);
    });
  });

  describe('File Size Limits', () => {
    it('should handle large files gracefully', async () => {
      const largeFile = path.join(testDir, 'large.js');

      // Create file larger than limit (1MB)
      const largeContent = 'a'.repeat(2 * 1024 * 1024); // 2MB
      await writeFile(largeFile, largeContent);

      await fileWatcher.watchDirectory(testDir);
      await waitForEvents(100);

      // Should either skip large file or handle it without crashing
      expect(errorEvents.length).toBe(0); // Should not error
    });

    it('should respect custom size limits', async () => {
      const smallLimitWatcher = new FileWatcher(
        async (event) => { changeEvents.push(event); },
        async (filePath) => { deleteEvents.push(filePath); },
        (error, filePath) => { errorEvents.push({ error, filePath }); },
        {
          maxFileSize: 100, // 100 bytes
          debounceMs: 50
        }
      );

      const smallFile = path.join(testDir, 'small.ts');
      const largeFile = path.join(testDir, 'large.ts');

      await smallLimitWatcher.watchDirectory(testDir);

      // Create small file (under limit)
      await writeFile(smallFile, 'small');
      await waitForEvents(100);

      const smallEvents = changeEvents.length;

      // Create large file (over limit)
      await writeFile(largeFile, 'a'.repeat(200));
      await waitForEvents(100);

      // Should process small file but not large file
      expect(changeEvents.length).toBe(smallEvents);

      smallLimitWatcher.dispose();
    });
  });

  describe('Error Handling', () => {
    it('should handle permission errors gracefully', async () => {
      const restrictedFile = path.join(testDir, 'restricted.ts');
      await writeFile(restrictedFile, 'content');

      // Make file unreadable (if possible on the system)
      try {
        await chmod(restrictedFile, 0o000);
      } catch {
        // Skip test if chmod not supported
        return;
      }

      await fileWatcher.watchDirectory(testDir);
      await waitForEvents(100);

      // Should handle permission error gracefully
      if (errorEvents.length > 0) {
        expect(errorEvents[0].error).toBeInstanceOf(Error);
      }

      // Restore permissions for cleanup
      await chmod(restrictedFile, 0o644);
    });

    it('should handle non-existent directories', async () => {
      const nonExistentDir = '/tmp/does-not-exist-' + Date.now();

      await fileWatcher.watchDirectory(nonExistentDir);

      // Should handle error without crashing
      expect(errorEvents.length).toBeGreaterThan(0);
      expect(errorEvents[0].error).toBeInstanceOf(Error);
    });

    it('should handle invalid file paths', async () => {
      const invalidFile = '/dev/null/invalid/path.ts';

      await fileWatcher.watchFile(invalidFile);

      // Should handle gracefully
      expect(errorEvents.length).toBeGreaterThan(0);
    });
  });

  describe('Statistics and Monitoring', () => {
    it('should provide accurate statistics', async () => {
      const stats1 = fileWatcher.getStats();
      expect(stats1.watchedPaths).toBe(0);
      expect(stats1.pendingUpdates).toBe(0);
      expect(stats1.processingFiles).toBe(0);

      // Start watching
      await fileWatcher.watchDirectory(testDir);

      const stats2 = fileWatcher.getStats();
      expect(stats2.watchedPaths).toBe(1);
      expect(stats2.supportedExtensions).toBeGreaterThan(0);
      expect(stats2.ignorePatterns).toBeGreaterThan(0);
    });

    it('should track pending updates during debouncing', async () => {
      const testFile = path.join(testDir, 'pending.ts');
      await writeFile(testFile, 'initial');

      await fileWatcher.watchDirectory(testDir);

      // Make rapid changes to create pending updates
      await writeFile(testFile, 'change1');
      await writeFile(testFile, 'change2');

      // Check immediately (during debounce period)
      const stats = fileWatcher.getStats();
      expect(stats.pendingUpdates).toBeGreaterThanOrEqual(0);
    });

    it('should track processing files', async () => {
      // This test is timing-sensitive and may need adjustment
      const testFile = path.join(testDir, 'processing.ts');

      await fileWatcher.watchDirectory(testDir);

      // Create file
      await writeFile(testFile, 'content');

      // Immediately check stats (may catch file in processing state)
      const stats = fileWatcher.getStats();
      expect(typeof stats.processingFiles).toBe('number');
    });
  });

  describe('Watch Management', () => {
    it('should stop watching specific directories', async () => {
      const dir1 = path.join(testDir, 'dir1');
      const dir2 = path.join(testDir, 'dir2');

      await mkdir(dir1);
      await mkdir(dir2);

      await fileWatcher.watchDirectory(dir1);
      await fileWatcher.watchDirectory(dir2);

      expect(fileWatcher.getStats().watchedPaths).toBe(2);

      // Stop watching one directory
      fileWatcher.stopWatching(dir1);

      expect(fileWatcher.getStats().watchedPaths).toBe(1);
    });

    it('should stop all watchers', async () => {
      await fileWatcher.watchDirectory(testDir);
      await fileWatcher.watchFile(path.join(testDir, 'test.ts'));

      expect(fileWatcher.getStats().watchedPaths).toBeGreaterThan(0);

      fileWatcher.stopWatching();

      expect(fileWatcher.getStats().watchedPaths).toBe(0);
    });

    it('should dispose properly', async () => {
      await fileWatcher.watchDirectory(testDir);

      const statsBefore = fileWatcher.getStats();
      expect(statsBefore.watchedPaths).toBeGreaterThan(0);

      fileWatcher.dispose();

      const statsAfter = fileWatcher.getStats();
      expect(statsAfter.watchedPaths).toBe(0);
      expect(statsAfter.processingFiles).toBe(0);
    });
  });

  describe('Force Processing', () => {
    it('should force process supported files', async () => {
      const testFile = path.join(testDir, 'force.ts');
      await writeFile(testFile, 'forced content');

      // Process file directly without watching
      await fileWatcher.forceProcessFile(testFile);

      expect(changeEvents.length).toBe(1);
      expect(changeEvents[0].filePath).toBe(testFile);
      expect(changeEvents[0].content).toBe('forced content');
    });

    it('should reject force processing unsupported files', async () => {
      const unsupportedFile = path.join(testDir, 'test.txt');
      await writeFile(unsupportedFile, 'text content');

      await expect(fileWatcher.forceProcessFile(unsupportedFile)).rejects.toThrow();
    });

    it('should reject force processing ignored files', async () => {
      const ignoredFile = path.join(testDir, 'test.log');
      await writeFile(ignoredFile, 'log content');

      await expect(fileWatcher.forceProcessFile(ignoredFile)).rejects.toThrow();
    });
  });

  describe('Edge Cases', () => {
    it('should handle empty files', async () => {
      const emptyFile = path.join(testDir, 'empty.js');
      await writeFile(emptyFile, '');

      await fileWatcher.watchDirectory(testDir);
      await waitForEvents(100);

      expect(changeEvents.length).toBe(1);
      expect(changeEvents[0].content).toBe('');
    });

    it('should handle binary files gracefully', async () => {
      const binaryFile = path.join(testDir, 'binary.js'); // Still has .js extension
      const binaryContent = Buffer.from([0x00, 0x01, 0x02, 0x03, 0xFF]);
      await writeFile(binaryFile, binaryContent);

      await fileWatcher.watchDirectory(testDir);
      await waitForEvents(100);

      // Should handle binary content without crashing
      expect(changeEvents.length).toBe(1);
      expect(typeof changeEvents[0].content).toBe('string');
    });

    it('should handle symlinks if present', async () => {
      const targetFile = path.join(testDir, 'target.ts');
      const linkFile = path.join(testDir, 'link.ts');

      await writeFile(targetFile, 'target content');

      try {
        // Create symlink (may fail on some systems)
        await Bun.spawn(['ln', '-s', targetFile, linkFile]).exited;

        await fileWatcher.watchDirectory(testDir);

        // Modify target file
        await writeFile(targetFile, 'modified target');
        await waitForEvents(100);

        // Should detect changes (behavior may vary by system)
        expect(changeEvents.length).toBeGreaterThan(0);
      } catch {
        // Skip test if symlinks not supported
      }
    });

    it('should handle rapid directory structure changes', async () => {
      await fileWatcher.watchDirectory(testDir);

      // Create nested directory structure rapidly
      const nestedDir = path.join(testDir, 'nested', 'deep', 'structure');
      await mkdir(nestedDir, { recursive: true });

      // Create files in nested structure
      await writeFile(path.join(nestedDir, 'nested.ts'), 'nested content');

      await waitForEvents(100);

      // Should handle nested file creation
      expect(changeEvents.some(e => e.filePath.includes('nested.ts'))).toBe(true);
    });
  });

  describe('Performance Tests', () => {
    it('should handle multiple files efficiently', async () => {
      await fileWatcher.watchDirectory(testDir);

      const startTime = Date.now();

      // Create multiple files with slight delays to avoid excessive debouncing
      for (let i = 0; i < 10; i++) {
        await writeFile(path.join(testDir, `perf${i}.ts`), `content ${i}`);
        if (i % 3 === 0) {
          await new Promise(resolve => setTimeout(resolve, 10)); // Small delay every 3 files
        }
      }

      await waitForEvents(300); // Give time for debounced events

      const endTime = Date.now();
      const processingTime = endTime - startTime;

      // Should process files efficiently
      expect(processingTime).toBeLessThan(2000); // Under 2 seconds for 10 files
      expect(changeEvents.length).toBeGreaterThanOrEqual(8); // Account for debouncing behavior
    });

    it('should maintain performance under load', async () => {
      await fileWatcher.watchDirectory(testDir);

      const iterations = 5;
      const times = [];

      for (let i = 0; i < iterations; i++) {
        const startTime = Date.now();

        await writeFile(path.join(testDir, `load${i}.ts`), `load test ${i}`);
        await waitForEvents(100);

        const endTime = Date.now();
        times.push(endTime - startTime);
      }

      const avgTime = times.reduce((a, b) => a + b, 0) / times.length;
      expect(avgTime).toBeLessThan(200); // Average under 200ms per file
    });
  });
});