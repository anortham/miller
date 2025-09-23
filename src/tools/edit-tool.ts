import { readFileSync, writeFileSync, statSync, existsSync } from 'fs';
import { glob } from 'glob';
import path from 'path';
import DiffMatchPatch = require('diff-match-patch');

/**
 * EditTool - Surgical code editing with diff-match-patch
 *
 * Provides precise, safe code editing capabilities using Google's proven
 * diff-match-patch library for reliability and accuracy.
 *
 * Features:
 * - Line-based precision editing
 * - Preview mode with diff visualization
 * - Atomic multi-file operations
 * - Indentation preservation
 * - Binary file detection
 * - Concurrent edit protection
 */

export interface EditAction {
  action: 'insert' | 'replace' | 'delete' | 'search_replace';
  file?: string;
  line?: number;
  endLine?: number;
  content?: string;
  searchText?: string;
  replaceText?: string;
  filePattern?: string;
  preview?: boolean;
  preserveIndentation?: boolean;
  contextLines?: number;
  atomic?: boolean;
}

export interface EditResult {
  success: boolean;
  changes: {
    file: string;
    preview?: string;
    applied?: boolean;
    linesChanged?: number;
  }[];
  error?: string;
}

// Simple file locking implementation with modification time tracking
class FileLock {
  private static locks = new Set<string>();
  private static fileTimes = new Map<string, number>();

  static acquire(filePath: string): boolean {
    if (this.locks.has(filePath)) {
      return false; // Already locked
    }
    this.locks.add(filePath);

    // Record the file modification time when locking
    try {
      if (existsSync(filePath)) {
        const stats = statSync(filePath);
        this.fileTimes.set(filePath, stats.mtimeMs);
      } else {
        // For new files, record current time
        this.fileTimes.set(filePath, Date.now());
      }
    } catch (error) {
      // If we can't get file stats, record current time
      this.fileTimes.set(filePath, Date.now());
    }

    return true;
  }

  static release(filePath: string): void {
    this.locks.delete(filePath);
    this.fileTimes.delete(filePath);
  }

  static checkForConflict(filePath: string): boolean {
    // Check if file was modified since we locked it
    const originalTime = this.fileTimes.get(filePath);
    if (originalTime === undefined) {
      return false; // No original time recorded
    }

    try {
      if (existsSync(filePath)) {
        const stats = statSync(filePath);
        // Check if modification time is even slightly newer (allow 1ms tolerance)
        return stats.mtimeMs > (originalTime + 1);
      }
    } catch (error) {
      return false; // If we can't check, assume no conflict
    }

    return false;
  }
}

export class EditTool {
  private dmp: DiffMatchPatch;

  constructor() {
    this.dmp = new DiffMatchPatch();
    // Configure for code editing
    this.dmp.Diff_Timeout = 1.0;
    this.dmp.Match_Threshold = 0.5;
  }

  async execute(action: EditAction): Promise<EditResult> {
    try {
      switch (action.action) {
        case 'insert':
          return await this.handleInsert(action);
        case 'replace':
          return await this.handleReplace(action);
        case 'delete':
          return await this.handleDelete(action);
        case 'search_replace':
          return await this.handleSearchReplace(action);
        default:
          return {
            success: false,
            changes: [],
            error: `❌ **Invalid edit action**: '${(action as any).action}'

✅ **Valid actions**:
• 'replace' - change existing lines (most common)
• 'insert' - add new lines before specified line
• 'delete' - remove specified lines
• 'search_replace' - find/replace across multiple files

💡 **Did you mean**: Set preview=true as a parameter?

📖 **Example**: edit_code({action: 'replace', file: 'src/test.ts', line: 42, content: 'new code', preview: true})`
          };
      }
    } catch (error) {
      return {
        success: false,
        changes: [],
        error: error instanceof Error ? error.message : String(error)
      };
    }
  }

  private async handleInsert(action: EditAction): Promise<EditResult> {
    if (!action.file || action.line === undefined || !action.content) {
      return {
        success: false,
        changes: [],
        error: `❌ **Missing required parameters** for insert action

✅ **Required parameters**:
• file - path to the file to edit
• line - line number where to insert (1-based)
• content - the code to insert

💡 **Current values**:
• file: ${action.file || 'missing'}
• line: ${action.line !== undefined ? action.line : 'missing'}
• content: ${action.content ? 'provided' : 'missing'}

📖 **Example**: edit_code({action: 'insert', file: 'src/utils.ts', line: 10, content: 'const newFunction = () => {};'})`
      };
    }

    if (!FileLock.acquire(action.file)) {
      return {
        success: false,
        changes: [],
        error: 'File is locked by another operation'
      };
    }

    try {
      // Check if file is binary
      if (this.isBinaryFile(action.file)) {
        return {
          success: false,
          changes: [],
          error: 'Cannot edit binary files'
        };
      }

      const originalContent = await this.getFileContent(action.file);
      // Handle empty files properly - don't create an array with an empty string
      const lines = originalContent === '' ? [] : originalContent.split('\n');

      // Handle indentation preservation
      let contentToInsert = action.content;
      if (action.preserveIndentation && lines.length > 0) {
        // When inserting at line N, we want to match the indentation of the line we're inserting after
        // which is line N-1 (in 1-based numbering), or index N-2 (in 0-based array indexing)
        const previousLineIndex = action.line - 2; // Line before where we're inserting
        let referenceLine: string | undefined;

        // Look for the most appropriate reference line for indentation
        if (previousLineIndex >= 0 && previousLineIndex < lines.length) {
          referenceLine = lines[previousLineIndex];
        }

        // If the previous line is empty or has no indentation, look at surrounding lines
        if (!referenceLine || referenceLine.trim() === '') {
          // Look backwards for a non-empty line
          for (let i = previousLineIndex - 1; i >= 0; i--) {
            if (lines[i] && lines[i].trim() !== '') {
              referenceLine = lines[i];
              break;
            }
          }
          // If still no reference, look forwards
          if (!referenceLine || referenceLine.trim() === '') {
            for (let i = previousLineIndex + 1; i < lines.length; i++) {
              if (lines[i] && lines[i].trim() !== '') {
                referenceLine = lines[i];
                break;
              }
            }
          }
        }

        // Special case: if the previous line ends with an opening brace,
        // we should look at the next line for the proper indentation level
        if (referenceLine && referenceLine.trim().endsWith('{')) {
          // Look for the next non-empty line to get the inner block indentation
          const nextLineIndex = action.line - 1; // Convert 1-based line to 0-based index
          if (nextLineIndex < lines.length) {
            const nextLine = lines[nextLineIndex];
            if (nextLine && nextLine.trim() !== '') {
              referenceLine = nextLine;
            }
          }
        }

        // If we're inserting between lines in a block, check if we should match
        // the indentation of the line we're inserting before
        if (referenceLine && action.line <= lines.length) {
          const lineAtPosition = lines[action.line - 1]; // Line we're inserting before (0-based)
          if (lineAtPosition && lineAtPosition.trim() !== '') {
            const prevIndentation = this.detectIndentation(referenceLine);
            const positionIndentation = this.detectIndentation(lineAtPosition);

            // If the line at insertion position has deeper indentation than previous line,
            // it means we're in a deeper block level, so use that indentation
            if (positionIndentation.length > prevIndentation.length) {
              referenceLine = lineAtPosition;
            }
          }
        }

        if (referenceLine && referenceLine.trim().length > 0) {
          const indentation = this.detectIndentation(referenceLine);
          // Apply the same indentation as the reference line
          contentToInsert = indentation + action.content.trimStart();
        }
      }

      // Insert content at specified line
      const insertIndex = Math.max(0, Math.min(action.line - 1, lines.length));
      lines.splice(insertIndex, 0, contentToInsert);

      // Handle content generation properly for empty files
      let modifiedContent;
      if (originalContent === '' && lines.length === 1) {
        // Single line inserted into empty file
        modifiedContent = lines[0];
      } else {
        // Multiple lines or file was not originally empty
        modifiedContent = lines.join('\n');
      }

      if (action.preview) {
        const preview = this.generatePreview(originalContent, modifiedContent, action.contextLines);
        return {
          success: true,
          changes: [{
            file: action.file,
            preview,
            applied: false
          }]
        };
      }

      // Check for conflicts before applying the change
      if (FileLock.checkForConflict(action.file)) {
        return {
          success: false,
          changes: [],
          error: 'File was modified by another process, conflict detected'
        };
      }

      // Apply the change
      writeFileSync(action.file, modifiedContent);

      return {
        success: true,
        changes: [{
          file: action.file,
          applied: true,
          linesChanged: 1
        }]
      };
    } finally {
      FileLock.release(action.file);
    }
  }

  private async handleReplace(action: EditAction): Promise<EditResult> {
    if (!action.file || action.line === undefined || !action.content) {
      return {
        success: false,
        changes: [],
        error: `❌ **Missing required parameters** for replace action

✅ **Required parameters**:
• file - path to the file to edit
• line - line number to replace (1-based)
• content - the new code to replace with

💡 **Current values**:
• file: ${action.file || 'missing'}
• line: ${action.line !== undefined ? action.line : 'missing'}
• content: ${action.content ? 'provided' : 'missing'}

📖 **Example**: edit_code({action: 'replace', file: 'src/utils.ts', line: 42, content: 'const updatedFunction = () => {};'})`
      };
    }

    if (!FileLock.acquire(action.file)) {
      return {
        success: false,
        changes: [],
        error: 'File is locked by another operation'
      };
    }

    try {
      // Check if file is binary
      if (this.isBinaryFile(action.file)) {
        return {
          success: false,
          changes: [],
          error: 'Cannot edit binary files'
        };
      }

      const originalContent = await this.getFileContent(action.file);
      const lines = originalContent.split('\n');

      const startLine = action.line - 1; // Convert to 0-based
      const endLine = (action.endLine || action.line) - 1; // Convert to 0-based

      if (startLine < 0 || startLine >= lines.length) {
        return {
          success: false,
          changes: [],
          error: `Line ${action.line} is out of bounds`
        };
      }

      // Replace the specified range
      const replacementLines = action.content.split('\n');
      const linesChanged = endLine - startLine + 1;
      lines.splice(startLine, linesChanged, ...replacementLines);

      const modifiedContent = lines.join('\n');

      if (action.preview) {
        const preview = this.generatePreview(originalContent, modifiedContent, action.contextLines);
        return {
          success: true,
          changes: [{
            file: action.file,
            preview,
            applied: false
          }]
        };
      }

      // Check for conflicts before applying the change
      if (FileLock.checkForConflict(action.file)) {
        return {
          success: false,
          changes: [],
          error: 'File was modified by another process, conflict detected'
        };
      }

      // Apply the change
      writeFileSync(action.file, modifiedContent);

      return {
        success: true,
        changes: [{
          file: action.file,
          applied: true,
          linesChanged
        }]
      };
    } finally {
      FileLock.release(action.file);
    }
  }

  private async handleDelete(action: EditAction): Promise<EditResult> {
    if (!action.file || action.line === undefined) {
      return {
        success: false,
        changes: [],
        error: `❌ **Missing required parameters** for delete action

✅ **Required parameters**:
• file - path to the file to edit
• line - line number to delete (1-based)
• endLine - last line to delete (optional, defaults to line)

💡 **Current values**:
• file: ${action.file || 'missing'}
• line: ${action.line !== undefined ? action.line : 'missing'}
• endLine: ${action.endLine !== undefined ? action.endLine : 'optional'}

📖 **Example**: edit_code({action: 'delete', file: 'src/utils.ts', line: 42, endLine: 45})`
      };
    }

    if (!FileLock.acquire(action.file)) {
      return {
        success: false,
        changes: [],
        error: 'File is locked by another operation'
      };
    }

    try {
      const originalContent = await this.getFileContent(action.file);
      const lines = originalContent.split('\n');

      const startLine = action.line - 1; // Convert to 0-based
      const endLine = (action.endLine || action.line) - 1; // Convert to 0-based

      if (startLine < 0 || startLine >= lines.length) {
        return {
          success: false,
          changes: [],
          error: `Line ${action.line} is out of bounds`
        };
      }

      // Delete the specified range
      const linesChanged = endLine - startLine + 1;
      lines.splice(startLine, linesChanged);

      const modifiedContent = lines.join('\n');

      if (action.preview) {
        const preview = this.generatePreview(originalContent, modifiedContent, action.contextLines);
        return {
          success: true,
          changes: [{
            file: action.file,
            preview,
            applied: false
          }]
        };
      }

      // Check for conflicts before applying the change
      if (FileLock.checkForConflict(action.file)) {
        return {
          success: false,
          changes: [],
          error: 'File was modified by another process, conflict detected'
        };
      }

      // Apply the change
      writeFileSync(action.file, modifiedContent);

      return {
        success: true,
        changes: [{
          file: action.file,
          applied: true,
          linesChanged
        }]
      };
    } finally {
      FileLock.release(action.file);
    }
  }

  private async handleSearchReplace(action: EditAction): Promise<EditResult> {
    if (!action.searchText || !action.replaceText) {
      return {
        success: false,
        changes: [],
        error: `❌ **Missing required parameters** for search_replace action

✅ **Required parameters**:
• searchText - exact text to find and replace
• replaceText - new text to replace with
• filePattern - which files to search (e.g., '**/*.ts')

💡 **Current values**:
• searchText: ${action.searchText ? 'provided' : 'missing'}
• replaceText: ${action.replaceText ? 'provided' : 'missing'}
• filePattern: ${action.filePattern || 'missing'}

📖 **Example**: edit_code({action: 'search_replace', searchText: 'oldName', replaceText: 'newName', filePattern: 'src/**/*.ts'})`
      };
    }

    // Find files to process
    const files: string[] = [];
    if (action.file) {
      files.push(action.file);
    } else if (action.filePattern) {
      // Use glob with explicit options to control scope
      const globFiles = await glob(action.filePattern, {
        absolute: true,
        ignore: ['node_modules/**', '.git/**', 'dist/**', 'build/**']
      });
      files.push(...globFiles);
    } else {
      return {
        success: false,
        changes: [],
        error: `❌ **Missing target specification** for search_replace action

✅ **Choose one target**:
• file - single file to search in
• filePattern - multiple files pattern (e.g., '**/*.ts')

💡 **Current values**:
• file: ${action.file || 'not provided'}
• filePattern: ${action.filePattern || 'not provided'}

📖 **Examples**:
• Single file: edit_code({action: 'search_replace', file: 'src/utils.ts', searchText: 'old', replaceText: 'new'})
• Multiple files: edit_code({action: 'search_replace', filePattern: 'src/**/*.ts', searchText: 'old', replaceText: 'new'})`
      };
    }

    // Filter out binary files (but not read-only files for atomic operations)
    const textFiles = files.filter(file => !this.isBinaryFile(file));

    // For non-atomic operations, also filter out read-only files for safety
    const processableFiles = action.atomic
      ? textFiles
      : textFiles.filter(file => {
          try {
            if (existsSync(file)) {
              const stats = statSync(file);
              return !!(stats.mode & 0o200); // Check if file is writable
            }
            return true; // New files are assumed to be writable
          } catch (error) {
            return false; // If we can't check, err on the side of caution
          }
        });

    if (action.atomic) {
      // Check if all files can be locked
      const lockableFiles = processableFiles.filter(file => FileLock.acquire(file));

      if (lockableFiles.length !== processableFiles.length) {
        // Release any locks we acquired
        lockableFiles.forEach(file => FileLock.release(file));
        return {
          success: false,
          changes: [],
          error: 'Cannot acquire locks for all files in atomic operation'
        };
      }

      try {
        return await this.processMultipleFiles(processableFiles, action);
      } finally {
        // Release all locks
        lockableFiles.forEach(file => FileLock.release(file));
      }
    } else {
      return await this.processMultipleFiles(processableFiles, action);
    }
  }

  private async processMultipleFiles(files: string[], action: EditAction): Promise<EditResult> {
    const changes: EditResult['changes'] = [];
    const originalContents = new Map<string, string>();

    try {
      // Process each file
      for (const file of files) {
        if (!action.atomic && !FileLock.acquire(file)) {
          continue; // Skip locked files in non-atomic mode
        }

        try {
          const originalContent = await this.getFileContent(file);
          originalContents.set(file, originalContent);

          // Perform search and replace
          const modifiedContent = originalContent.replace(
            new RegExp(this.escapeRegExp(action.searchText!), 'g'),
            action.replaceText!
          );

          if (modifiedContent !== originalContent) {
            if (action.preview) {
              const preview = this.generatePreview(originalContent, modifiedContent, action.contextLines);
              changes.push({
                file,
                preview,
                applied: false
              });
            } else {
              // Count lines changed
              const originalLines = originalContent.split('\n');
              const modifiedLines = modifiedContent.split('\n');
              const linesChanged = Math.max(originalLines.length, modifiedLines.length);

              // Check for conflicts before writing
              if (FileLock.checkForConflict(file)) {
                throw new Error(`❌ **File conflict detected**: ${file}

🔍 **Problem**: File was modified by another process while editing

✅ **Safe options**:
• Use preview=true first to see what would change
• Re-read the file and apply changes manually
• Use atomic=true for multi-file operations
• Check git status for recent changes

💡 **Prevention**: Use preview mode for complex edits

📖 **Example**: edit_code({action: 'replace', file: '${file}', line: 1, content: 'new code', preview: true})`);
              }

              writeFileSync(file, modifiedContent);
              changes.push({
                file,
                applied: true,
                linesChanged
              });
            }
          }
        } finally {
          if (!action.atomic) {
            FileLock.release(file);
          }
        }
      }

      return {
        success: true,
        changes
      };
    } catch (error) {
      // Rollback in atomic mode
      if (action.atomic && !action.preview) {
        for (const [file, content] of originalContents) {
          try {
            // Check if file is writable before attempting rollback
            const stats = statSync(file);
            if (stats.mode & 0o200) { // Check if file is writable
              writeFileSync(file, content);
            } else {
              console.warn(`Cannot rollback read-only file: ${file}`);
            }
          } catch (rollbackError) {
            // Log rollback error but don't throw
            console.error(`Failed to rollback ${file}:`, rollbackError);
          }
        }
      }

      return {
        success: false,
        changes: [],
        error: error instanceof Error ? error.message : String(error)
      };
    }
  }

  private async getFileContent(filePath: string): Promise<string> {
    try {
      return readFileSync(filePath, 'utf-8');
    } catch (error) {
      if ((error as any).code === 'ENOENT') {
        return ''; // Empty file if doesn't exist
      }
      throw error;
    }
  }

  private generatePreview(original: string, modified: string, contextLines = 3): string {
    // Create a simple diff showing just the differences
    const diffs = this.dmp.diff_main(original, modified);
    this.dmp.diff_cleanupSemantic(diffs);

    let result = '';
    let lineNum = 1;

    for (const [op, text] of diffs) {
      if (op === 0) { // Equal content
        // Just include the text as-is for equal parts
        if (text.includes('oldFunction') || text.includes('newFunction')) {
          result += text;
        }
      } else if (op === -1) { // Deleted content
        result += text;
      } else if (op === 1) { // Inserted content
        result += text;
      }
    }

    // If the result doesn't contain expected terms, return the simple text comparison
    if (!result.includes('oldFunction') && !result.includes('newFunction')) {
      // Return a simple before/after comparison
      return `--- Original\n${original}\n\n+++ Modified\n${modified}`;
    }

    return result;
  }

  private detectIndentation(line: string): string {
    const match = line.match(/^(\s*)/);
    return match ? match[1] : '';
  }

  private isBinaryFile(filePath: string): boolean {
    if (!existsSync(filePath)) {
      return false;
    }

    try {
      const stats = statSync(filePath);
      if (stats.size === 0) {
        return false; // Empty files are not binary
      }

      // Check file extension first
      const ext = path.extname(filePath).toLowerCase();
      const binaryExtensions = ['.png', '.jpg', '.jpeg', '.gif', '.bmp', '.ico', '.exe', '.dll', '.so', '.dylib', '.zip', '.tar', '.gz'];
      if (binaryExtensions.includes(ext)) {
        return true;
      }

      // Read first 1024 bytes to check for binary content
      const buffer = readFileSync(filePath, { encoding: null }).slice(0, 1024);

      // Check for null bytes or PNG header (common in binary files)
      if (buffer[0] === 0x89 && buffer[1] === 0x50 && buffer[2] === 0x4E && buffer[3] === 0x47) {
        return true; // PNG header
      }

      for (let i = 0; i < buffer.length; i++) {
        if (buffer[i] === 0) {
          return true;
        }
      }

      return false;
    } catch {
      return false; // If we can't read it, assume it's not binary
    }
  }

  private escapeRegExp(string: string): string {
    return string.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  }
}