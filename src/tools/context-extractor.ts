import { readFileSync, existsSync, statSync } from 'fs';
import path from 'path';

/**
 * ContextExtractor - Smart context extraction for AI-sized windows
 *
 * Provides intelligent context around edit locations, understanding:
 * - Language syntax and boundaries
 * - Function/class scopes
 * - Multi-file relationships
 * - Symbol definitions and references
 *
 * Built for Miller's surgical editing capabilities.
 */

export interface ContextExtractionRequest {
  file: string;
  line: number;
  column?: number;
  windowSize?: number;          // Lines of context (default: 20)
  includeSymbols?: boolean;     // Include symbol definitions
  includeReferences?: boolean;  // Include related references
  language?: string;            // Override language detection
  smartBoundaries?: boolean;    // Respect function/class boundaries
}

export interface ContextWindow {
  file: string;
  startLine: number;
  endLine: number;
  content: string;
  focusLine: number;            // The line of interest within this window
  symbols?: SymbolInfo[];       // Related symbols in this context
  references?: ReferenceInfo[]; // Related references
}

export interface SymbolInfo {
  name: string;
  type: 'function' | 'class' | 'variable' | 'interface' | 'type';
  line: number;
  column: number;
  signature?: string;
}

export interface ReferenceInfo {
  file: string;
  line: number;
  column: number;
  context: string;  // Brief context around the reference
}

export interface ContextExtractionResult {
  success: boolean;
  primaryContext: ContextWindow;
  relatedContexts?: ContextWindow[];  // From other files
  error?: string;
}

export class ContextExtractor {
  private symbolDatabase?: any;
  private cache = new Map<string, ContextWindow>();

  constructor() {
    // Initialize extractor
  }

  setSymbolDatabase(db: any): void {
    this.symbolDatabase = db;
  }

  async extract(request: ContextExtractionRequest): Promise<ContextExtractionResult> {
    try {
      // Validate input
      if (!request.file || !request.line) {
        return {
          success: false,
          primaryContext: this.createEmptyContext(request.file, request.line),
          error: 'File and line are required'
        };
      }

      // Check if file exists
      if (!existsSync(request.file)) {
        return {
          success: false,
          primaryContext: this.createEmptyContext(request.file, request.line),
          error: 'File not found'
        };
      }

      // Check if file is binary
      if (this.isBinaryFile(request.file)) {
        return {
          success: false,
          primaryContext: this.createEmptyContext(request.file, request.line),
          error: 'Cannot extract context from binary files'
        };
      }

      // Read file content
      const fileContent = readFileSync(request.file, 'utf-8');
      const lines = fileContent.split('\n');

      // Validate line number
      if (request.line < 1 || request.line > lines.length) {
        // Clamp to valid range
        const clampedLine = Math.max(1, Math.min(request.line, lines.length || 1));
        request.line = clampedLine;
      }

      // Create cache key
      const cacheKey = this.createCacheKey(request);
      if (this.cache.has(cacheKey)) {
        return {
          success: true,
          primaryContext: this.cache.get(cacheKey)!
        };
      }

      // Extract primary context
      const primaryContext = await this.extractPrimaryContext(request, lines);

      // Cache the result
      this.cache.set(cacheKey, primaryContext);

      const result: ContextExtractionResult = {
        success: true,
        primaryContext
      };

      // Add related contexts if requested
      if (request.includeReferences || request.includeSymbols) {
        result.relatedContexts = await this.extractRelatedContexts(request, primaryContext);
      }

      return result;

    } catch (error) {
      return {
        success: false,
        primaryContext: this.createEmptyContext(request.file, request.line),
        error: error instanceof Error ? error.message : String(error)
      };
    }
  }

  private async extractPrimaryContext(request: ContextExtractionRequest, lines: string[]): Promise<ContextWindow> {
    const windowSize = request.windowSize || 20;
    const targetLine = request.line;
    const halfWindow = Math.floor(windowSize / 2);

    let startLine = Math.max(1, targetLine - halfWindow);
    let endLine = Math.min(lines.length, targetLine + halfWindow);

    // Apply smart boundaries if requested
    if (request.smartBoundaries) {
      const boundaries = this.findSmartBoundaries(lines, targetLine, request.language);
      if (boundaries) {
        startLine = Math.max(startLine, boundaries.start);
        endLine = Math.min(endLine, boundaries.end);
      }
    }

    // Extract content
    const contextLines = lines.slice(startLine - 1, endLine);
    const content = contextLines.join('\n');

    // Find symbols if requested
    let symbols: SymbolInfo[] | undefined;
    if (request.includeSymbols) {
      symbols = this.extractSymbols(contextLines, startLine, request.language);
    }

    return {
      file: request.file,
      startLine,
      endLine,
      content,
      focusLine: targetLine,
      symbols
    };
  }

  private findSmartBoundaries(lines: string[], targetLine: number, language?: string): { start: number; end: number } | null {
    const targetIndex = targetLine - 1; // Convert to 0-based

    // Language-specific boundary patterns
    const patterns = this.getBoundaryPatterns(language);

    // Find enclosing function/class/block
    let start = targetIndex;
    let end = targetIndex;
    let braceCount = 0;
    let foundStart = false;
    let foundEnd = false;

    // Search backwards for function/class start
    for (let i = targetIndex; i >= 0; i--) {
      const line = lines[i].trim();

      // Count braces to understand nesting
      braceCount += (line.match(/\}/g) || []).length;
      braceCount -= (line.match(/\{/g) || []).length;

      // Check for function/class declaration
      if (braceCount <= 0 && patterns.some(pattern => pattern.test(line))) {
        start = i + 1; // Convert to 1-based
        foundStart = true;
        break;
      }
    }

    // Reset brace count and search forwards for end
    braceCount = 0;
    for (let i = targetIndex; i < lines.length; i++) {
      const line = lines[i].trim();

      braceCount += (line.match(/\{/g) || []).length;
      braceCount -= (line.match(/\}/g) || []).length;

      if (braceCount < 0) {
        end = i + 1; // Convert to 1-based
        foundEnd = true;
        break;
      }
    }

    if (foundStart || foundEnd) {
      return {
        start: foundStart ? start : Math.max(1, targetLine - 10),
        end: foundEnd ? end : Math.min(lines.length, targetLine + 10)
      };
    }

    return null;
  }

  private getBoundaryPatterns(language?: string): RegExp[] {
    const common = [
      /^(function|class|interface|type|enum)\s+/,
      /^(export\s+)?(function|class|interface|type|enum)\s+/,
      /^(async\s+)?function\s+/,
      /^(public|private|protected)\s+(static\s+)?(async\s+)?(\w+)\s*\(/
    ];

    switch (language?.toLowerCase()) {
      case 'typescript':
      case 'javascript':
        return [
          ...common,
          /^\s*(const|let|var)\s+\w+\s*=\s*(async\s+)?\(/,
          /^\s*\w+\s*:\s*(async\s+)?\(/
        ];

      case 'python':
        return [
          /^(class|def|async\s+def)\s+/,
          /^@\w+/  // Decorators
        ];

      case 'java':
      case 'csharp':
        return [
          ...common,
          /^(public|private|protected|internal)\s+(static\s+)?(class|interface|enum)\s+/
        ];

      default:
        return common;
    }
  }

  private extractSymbols(lines: string[], startLine: number, language?: string): SymbolInfo[] {
    const symbols: SymbolInfo[] = [];
    const patterns = this.getSymbolPatterns(language);

    lines.forEach((line, index) => {
      const lineNumber = startLine + index;

      patterns.forEach(({ pattern, type }) => {
        const match = line.match(pattern);
        if (match) {
          const name = match[1] || match[2]; // Different capture groups
          if (name) {
            symbols.push({
              name,
              type,
              line: lineNumber,
              column: line.indexOf(name),
              signature: line.trim()
            });
          }
        }
      });
    });

    return symbols;
  }

  private getSymbolPatterns(language?: string): Array<{ pattern: RegExp; type: SymbolInfo['type'] }> {
    const common = [
      { pattern: /^(export\s+)?function\s+(\w+)/, type: 'function' as const },
      { pattern: /^(export\s+)?class\s+(\w+)/, type: 'class' as const },
      { pattern: /^(export\s+)?interface\s+(\w+)/, type: 'interface' as const },
      { pattern: /^(export\s+)?type\s+(\w+)/, type: 'type' as const }
    ];

    switch (language?.toLowerCase()) {
      case 'typescript':
      case 'javascript':
        return [
          ...common,
          { pattern: /^(const|let|var)\s+(\w+)\s*=/, type: 'variable' as const },
          { pattern: /^\s*(\w+)\s*:\s*\(/, type: 'function' as const }
        ];

      case 'python':
        return [
          { pattern: /^def\s+(\w+)/, type: 'function' as const },
          { pattern: /^class\s+(\w+)/, type: 'class' as const },
          { pattern: /^(\w+)\s*=/, type: 'variable' as const }
        ];

      default:
        return common;
    }
  }

  private async extractRelatedContexts(request: ContextExtractionRequest, primaryContext: ContextWindow): Promise<ContextWindow[]> {
    const relatedContexts: ContextWindow[] = [];

    if (!this.symbolDatabase) {
      return relatedContexts;
    }

    // Extract imports and references from primary context
    const imports = this.extractImports(primaryContext.content, request.language);

    for (const importInfo of imports) {
      try {
        // Resolve import to actual file
        const resolvedFile = this.resolveImport(importInfo.path, request.file);
        if (resolvedFile && existsSync(resolvedFile)) {
          // Extract context from related file
          const relatedRequest: ContextExtractionRequest = {
            file: resolvedFile,
            line: 1, // Start of file for now
            windowSize: 15,
            includeSymbols: true
          };

          const relatedResult = await this.extract(relatedRequest);
          if (relatedResult.success) {
            relatedContexts.push(relatedResult.primaryContext);
          }
        }
      } catch (error) {
        // Continue with other imports if one fails
        continue;
      }
    }

    return relatedContexts;
  }

  private extractImports(content: string, language?: string): Array<{ name: string; path: string }> {
    const imports: Array<{ name: string; path: string }> = [];
    const lines = content.split('\n');

    const patterns = this.getImportPatterns(language);

    lines.forEach(line => {
      patterns.forEach(pattern => {
        const match = line.match(pattern);
        if (match) {
          const path = match[match.length - 1]; // Last capture group is usually the path
          const name = match[1] || 'default';
          imports.push({ name, path });
        }
      });
    });

    return imports;
  }

  private getImportPatterns(language?: string): RegExp[] {
    switch (language?.toLowerCase()) {
      case 'typescript':
      case 'javascript':
        return [
          /^import\s+\{([^}]+)\}\s+from\s+['"]([^'"]+)['"]/,
          /^import\s+(\w+)\s+from\s+['"]([^'"]+)['"]/,
          /^import\s+\*\s+as\s+(\w+)\s+from\s+['"]([^'"]+)['"]/
        ];

      case 'python':
        return [
          /^from\s+(\S+)\s+import\s+/,
          /^import\s+(\S+)/
        ];

      default:
        return [
          /^import\s+\{([^}]+)\}\s+from\s+['"]([^'"]+)['"]/,
          /^import\s+(\w+)\s+from\s+['"]([^'"]+)['"]/
        ];
    }
  }

  private resolveImport(importPath: string, currentFile: string): string | null {
    if (importPath.startsWith('.')) {
      // Relative import
      const currentDir = path.dirname(currentFile);
      const resolved = path.resolve(currentDir, importPath);

      // Try common extensions
      const extensions = ['.ts', '.js', '.tsx', '.jsx'];
      for (const ext of extensions) {
        const withExt = resolved + ext;
        if (existsSync(withExt)) {
          return withExt;
        }
      }

      // Try index files
      for (const ext of extensions) {
        const indexFile = path.join(resolved, 'index' + ext);
        if (existsSync(indexFile)) {
          return indexFile;
        }
      }
    }

    return null;
  }

  private createEmptyContext(file: string, line: number): ContextWindow {
    return {
      file,
      startLine: line,
      endLine: line,
      content: '',
      focusLine: line
    };
  }

  private createCacheKey(request: ContextExtractionRequest): string {
    return `${request.file}:${request.line}:${request.windowSize || 20}:${request.smartBoundaries || false}`;
  }

  private isBinaryFile(filePath: string): boolean {
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

      // Check for null bytes (common in binary files)
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
}