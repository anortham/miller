import { watch, FSWatcher, readFile } from 'fs';
import { readFile as readFileAsync, stat as statAsync } from 'fs/promises';
import path from 'path';

export interface FileWatcherOptions {
  debounceMs?: number;
  recursive?: boolean;
  ignorePatterns?: string[];
  supportedExtensions?: string[];
  maxFileSize?: number; // in bytes
}

export interface FileChangeEvent {
  type: 'create' | 'change' | 'delete';
  filePath: string;
  content?: string;
  timestamp: number;
}

export class FileWatcher {
  private watchers = new Map<string, FSWatcher>();
  private updateQueue = new Map<string, NodeJS.Timeout>();
  private processing = new Set<string>();
  private options: Required<FileWatcherOptions>;

  private defaultIgnorePatterns = [
    '**/node_modules/**',
    '**/.git/**',
    '**/.vscode/**',
    '**/.idea/**',
    '**/dist/**',
    '**/build/**',
    '**/coverage/**',
    '**/*.log',
    '**/*.tmp',
    '**/*.temp',
    '**/.*', // Hidden files
    '**/.DS_Store'
  ];

  private defaultSupportedExtensions = [
    '.js', '.jsx', '.ts', '.tsx',
    '.py', '.pyw',
    '.rs',
    '.go',
    '.java',
    '.cs',
    '.c', '.h', '.cpp', '.cc', '.cxx', '.hpp', '.hxx',
    '.rb',
    '.php'
  ];

  constructor(
    private onFileChange: (event: FileChangeEvent) => Promise<void>,
    private onFileDelete: (filePath: string) => Promise<void>,
    private onError?: (error: Error, filePath?: string) => void,
    options: FileWatcherOptions = {}
  ) {
    this.options = {
      debounceMs: options.debounceMs ?? 300,
      recursive: options.recursive ?? true,
      ignorePatterns: options.ignorePatterns ?? this.defaultIgnorePatterns,
      supportedExtensions: options.supportedExtensions ?? this.defaultSupportedExtensions,
      maxFileSize: options.maxFileSize ?? 5 * 1024 * 1024 // 5MB default
    };
  }

  async watchDirectory(dirPath: string): Promise<void> {
    try {
      const absolutePath = path.resolve(dirPath);

      console.log(`Starting file watcher for: ${absolutePath}`);

      const watcher = watch(
        absolutePath,
        { recursive: this.options.recursive },
        (eventType, filename) => {
          if (!filename) return;

          const filePath = path.join(absolutePath, filename);
          this.handleRawFileEvent(filePath, eventType);
        }
      );

      watcher.on('error', (error) => {
        this.handleError(error, absolutePath);
      });

      this.watchers.set(absolutePath, watcher);
      console.log(`File watcher active for ${absolutePath} (recursive: ${this.options.recursive})`);
    } catch (error) {
      this.handleError(error as Error, dirPath);
    }
  }

  async watchFile(filePath: string): Promise<void> {
    try {
      const absolutePath = path.resolve(filePath);

      if (!this.isFileSupported(absolutePath)) {
        console.warn(`File type not supported: ${absolutePath}`);
        return;
      }

      console.log(`Starting file watcher for file: ${absolutePath}`);

      const watcher = watch(absolutePath, (eventType) => {
        this.handleRawFileEvent(absolutePath, eventType);
      });

      watcher.on('error', (error) => {
        this.handleError(error, absolutePath);
      });

      this.watchers.set(absolutePath, watcher);
    } catch (error) {
      this.handleError(error as Error, filePath);
    }
  }

  private handleRawFileEvent(filePath: string, eventType: string): void {
    // Filter out files we don't care about
    if (!this.shouldWatchFile(filePath)) {
      return;
    }

    // Debounce rapid changes
    if (this.updateQueue.has(filePath)) {
      clearTimeout(this.updateQueue.get(filePath));
    }

    const timeoutId = setTimeout(() => {
      this.handleDebouncedFileEvent(filePath, eventType);
    }, this.options.debounceMs);

    this.updateQueue.set(filePath, timeoutId);
  }

  private async handleDebouncedFileEvent(filePath: string, eventType: string): Promise<void> {
    this.updateQueue.delete(filePath);

    // Prevent concurrent processing of the same file
    if (this.processing.has(filePath)) {
      return;
    }

    this.processing.add(filePath);

    try {
      await this.processFileEvent(filePath, eventType);
    } catch (error) {
      this.handleError(error as Error, filePath);
    } finally {
      this.processing.delete(filePath);
    }
  }

  private async processFileEvent(filePath: string, eventType: string): Promise<void> {
    try {
      if (eventType === 'rename') {
        // Check if file still exists (rename can mean delete or create)
        const fileExists = await this.fileExists(filePath);

        if (fileExists) {
          // File was created or moved
          const content = await this.readFileWithSizeCheck(filePath);
          if (content !== null) {
            await this.onFileChange({
              type: 'create',
              filePath,
              content,
              timestamp: Date.now()
            });
          }
        } else {
          // File was deleted
          await this.onFileDelete(filePath);
        }
      } else if (eventType === 'change') {
        // File was modified
        const content = await this.readFileWithSizeCheck(filePath);
        if (content !== null) {
          await this.onFileChange({
            type: 'change',
            filePath,
            content,
            timestamp: Date.now()
          });
        }
      }
    } catch (error) {
      this.handleError(error as Error, filePath);
    }
  }

  private async fileExists(filePath: string): Promise<boolean> {
    try {
      await statAsync(filePath);
      return true;
    } catch {
      return false;
    }
  }

  private async readFileWithSizeCheck(filePath: string): Promise<string | null> {
    try {
      // Check file size first
      const stats = await statAsync(filePath);
      if (stats.size > this.options.maxFileSize) {
        console.warn(`File too large to process: ${filePath} (${stats.size} bytes)`);
        return null;
      }

      const content = await readFileAsync(filePath, 'utf-8');
      return content;
    } catch (error) {
      this.handleError(error as Error, filePath);
      return null;
    }
  }

  private shouldWatchFile(filePath: string): boolean {
    // Check if file extension is supported
    if (!this.isFileSupported(filePath)) {
      return false;
    }

    // Check ignore patterns
    const normalizedPath = path.normalize(filePath);
    for (const pattern of this.options.ignorePatterns) {
      if (this.matchesPattern(normalizedPath, pattern)) {
        return false;
      }
    }

    return true;
  }

  private isFileSupported(filePath: string): boolean {
    const ext = path.extname(filePath).toLowerCase();
    return this.options.supportedExtensions.includes(ext);
  }

  private matchesPattern(filePath: string, pattern: string): boolean {
    // Simple pattern matching - could be enhanced with a proper glob library
    if (pattern.includes('**')) {
      // Handle ** patterns
      let regexPattern = pattern
        .replace(/\*\*/g, '.*')
        .replace(/\*/g, '[^/\\\\]*')
        .replace(/\?/g, '[^/\\\\]');

      // Special case for hidden files pattern: **/.* should match files starting with dot
      if (pattern === '**/.*') {
        regexPattern = '.*/\\.[^/\\\\]*';
      }

      const regex = new RegExp(regexPattern, 'i');
      return regex.test(filePath);
    } else {
      // Simple wildcard matching
      const regexPattern = pattern
        .replace(/\*/g, '[^/\\\\]*')
        .replace(/\?/g, '[^/\\\\]');

      const regex = new RegExp(regexPattern, 'i');
      return regex.test(path.basename(filePath));
    }
  }

  private handleError(error: Error, filePath?: string): void {
    console.error(`File watcher error${filePath ? ` for ${filePath}` : ''}:`, error);

    if (this.onError) {
      this.onError(error, filePath);
    }
  }

  stopWatching(dirPath?: string): void {
    if (dirPath) {
      const absolutePath = path.resolve(dirPath);
      const watcher = this.watchers.get(absolutePath);
      if (watcher) {
        watcher.close();
        this.watchers.delete(absolutePath);
        console.log(`Stopped watching: ${absolutePath}`);
      }
    } else {
      // Stop all watchers
      for (const [path, watcher] of this.watchers) {
        watcher.close();
        console.log(`Stopped watching: ${path}`);
      }
      this.watchers.clear();
    }

    // Clear any pending updates
    for (const timeoutId of this.updateQueue.values()) {
      clearTimeout(timeoutId);
    }
    this.updateQueue.clear();
  }

  isWatching(dirPath?: string): boolean {
    if (dirPath) {
      const absolutePath = path.resolve(dirPath);
      return this.watchers.has(absolutePath);
    }
    return this.watchers.size > 0;
  }

  getWatchedPaths(): string[] {
    return Array.from(this.watchers.keys());
  }

  getStats() {
    return {
      watchedPaths: this.watchers.size,
      pendingUpdates: this.updateQueue.size,
      processingFiles: this.processing.size,
      supportedExtensions: this.options.supportedExtensions.length,
      ignorePatterns: this.options.ignorePatterns.length
    };
  }

  // Update configuration
  updateOptions(newOptions: Partial<FileWatcherOptions>): void {
    this.options = {
      ...this.options,
      ...newOptions,
      ignorePatterns: newOptions.ignorePatterns ?? this.options.ignorePatterns,
      supportedExtensions: newOptions.supportedExtensions ?? this.options.supportedExtensions
    };

    console.log('File watcher options updated:', this.options);
  }

  addIgnorePattern(pattern: string): void {
    if (!this.options.ignorePatterns.includes(pattern)) {
      this.options.ignorePatterns.push(pattern);
      console.log(`Added ignore pattern: ${pattern}`);
    }
  }

  removeIgnorePattern(pattern: string): void {
    const index = this.options.ignorePatterns.indexOf(pattern);
    if (index > -1) {
      this.options.ignorePatterns.splice(index, 1);
      console.log(`Removed ignore pattern: ${pattern}`);
    }
  }

  addSupportedExtension(extension: string): void {
    const ext = extension.startsWith('.') ? extension : `.${extension}`;
    if (!this.options.supportedExtensions.includes(ext)) {
      this.options.supportedExtensions.push(ext);
      console.log(`Added supported extension: ${ext}`);
    }
  }

  removeSupportedExtension(extension: string): void {
    const ext = extension.startsWith('.') ? extension : `.${extension}`;
    const index = this.options.supportedExtensions.indexOf(ext);
    if (index > -1) {
      this.options.supportedExtensions.splice(index, 1);
      console.log(`Removed supported extension: ${ext}`);
    }
  }

  // Force process a file (useful for testing or manual triggers)
  async forceProcessFile(filePath: string): Promise<void> {
    if (!this.shouldWatchFile(filePath)) {
      throw new Error(`File not supported or ignored: ${filePath}`);
    }

    const content = await this.readFileWithSizeCheck(filePath);
    if (content !== null) {
      await this.onFileChange({
        type: 'change',
        filePath,
        content,
        timestamp: Date.now()
      });
    }
  }

  // Cleanup method
  dispose(): void {
    this.stopWatching();
    this.processing.clear();
  }
}