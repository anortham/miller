import { Parser, Language } from 'web-tree-sitter';
import { createHash } from 'crypto';
import { log, LogLevel } from '../utils/logger.js';

export interface ParseResult {
  tree: Parser.Tree;
  language: string;
  filePath: string;
  content: string;
  hash: string;
}

export interface LanguageConfig {
  name: string;
  extensions: string[];
  wasmPath?: string;
}

export class ParserManager {
  private parsers = new Map<string, Parser>();
  private languages = new Map<string, Parser.Language>();
  private extensionToLanguage = new Map<string, string>();
  private initialized = false;

  private languageConfigs: LanguageConfig[] = [
    // Core web languages
    { name: 'javascript', extensions: ['.js', '.jsx', '.mjs'] },
    { name: 'typescript', extensions: ['.ts', '.tsx'] },
    { name: 'css', extensions: ['.css', '.scss', '.sass', '.less'] },
    { name: 'html', extensions: ['.html', '.htm'] },

    // Microsoft priority languages
    { name: 'python', extensions: ['.py', '.pyw'] },
    { name: 'rust', extensions: ['.rs'] },
    { name: 'go', extensions: ['.go'] },
    { name: 'java', extensions: ['.java'] },
    { name: 'c_sharp', extensions: ['.cs'] },
    { name: 'c', extensions: ['.c', '.h'] },
    { name: 'cpp', extensions: ['.cpp', '.cc', '.cxx', '.hpp', '.hxx'] },
    { name: 'ruby', extensions: ['.rb'] },
    { name: 'php', extensions: ['.php'] },

    // Mobile development
    { name: 'swift', extensions: ['.swift'], wasmPath: './wasm/tree-sitter-swift.wasm' },
    { name: 'kotlin', extensions: ['.kt', '.kts'], wasmPath: './wasm/tree-sitter-kotlin.wasm' },
    { name: 'qmljs', extensions: ['.qml', '.qmldir'], wasmPath: './wasm/tree-sitter-javascript.wasm' },

    // Web frameworks
    { name: 'vue', extensions: ['.vue'] }, // Special handling - no WASM needed
    { name: 'razor', extensions: ['.razor', '.cshtml'], wasmPath: './wasm/tree-sitter-razor.wasm' },

    // Database languages
    { name: 'sql', extensions: ['.sql', '.ddl', '.dml'] },

    // Systems programming languages
    { name: 'zig', extensions: ['.zig'] },

    // Mobile development languages
    { name: 'dart', extensions: ['.dart'] },

    // DevOps/scripting languages
    { name: 'bash', extensions: ['.sh', '.bash', '.zsh'] },
    { name: 'powershell', extensions: ['.ps1', '.psm1', '.psd1'] },

    // Game development languages
    { name: 'lua', extensions: ['.lua'] },
    { name: 'gdscript', extensions: ['.gd'] },

    // Utility parsers
    { name: 'regex', extensions: ['.regex'], wasmPath: './wasm/tree-sitter-regex.wasm' }, // Support .regex files for testing and standalone regex patterns
  ];

  async initialize() {
    if (this.initialized) return;

    await Parser.init();

    // Set up extension to language mappings (but don't load parsers yet - lazy loading)
    for (const config of this.languageConfigs) {
      // Skip languages without extensions, except special parsers
      if (config.extensions.length === 0 && !['regex', 'vue'].includes(config.name)) {
        log.engine(LogLevel.DEBUG, `Skipping ${config.name} - no file extensions defined`);
        continue;
      }

      // Map extensions to languages for all supported languages
      for (const ext of config.extensions) {
        this.extensionToLanguage.set(ext, config.name);
      }
    }

    this.initialized = true;
    log.engine(LogLevel.INFO, `Parser manager initialized with lazy loading for ${this.languageConfigs.length} languages`);
  }

  private async loadLanguageParser(language: string): Promise<Parser.Language | null> {
    // Return already loaded parser
    if (this.languages.has(language)) {
      return this.languages.get(language)!;
    }

    // Find config for this language
    const config = this.languageConfigs.find(c => c.name === language);
    if (!config) {
      throw new Error(`Unknown language: ${language}`);
    }

    try {
      // Vue is handled specially - no WASM parser needed
      if (config.name === 'vue') {
        log.engine(LogLevel.DEBUG, `Vue SFC handler ready for ${config.extensions.join(', ')}`);
        return null; // Vue doesn't need a tree-sitter parser
      }

      // Use our locally built WASM files
      let wasmPath = config.wasmPath;
      if (!wasmPath) {
        // All parsers use our locally built WASM files
        // Special handling for c_sharp to maintain underscore in filename
        let fileName = config.name;
        if (config.name !== 'c_sharp') {
          fileName = config.name.replace('_', '-');
        }
        wasmPath = `./wasm/tree-sitter-${fileName}.wasm`;
      }

      log.engine(LogLevel.DEBUG, `Loading parser for ${config.name}...`);
      const languageParser = await Language.load(wasmPath);
      this.languages.set(config.name, languageParser);
      log.engine(LogLevel.DEBUG, `✓ Loaded parser for ${config.name}`);

      return languageParser;
    } catch (error) {
      log.engine(LogLevel.WARN, `Failed to load parser for ${config.name}:`, error);

      // Fallback: Use JavaScript parser for TypeScript files
      if (config.name === 'typescript' && this.languages.has('javascript')) {
        log.engine(LogLevel.INFO, `Using JavaScript parser as fallback for TypeScript`);
        const jsParser = this.languages.get('javascript')!;
        this.languages.set('typescript', jsParser);
        return jsParser;
      }

      // Fallback: Use JavaScript parser for QML/JS files (QML contains embedded JavaScript)
      if (config.name === 'qmljs') {
        log.engine(LogLevel.INFO, `Using JavaScript parser as fallback for QML/JS`);
        // Load JavaScript parser if not already loaded
        if (!this.languages.has('javascript')) {
          await this.loadLanguageParser('javascript');
        }
        const jsParser = this.languages.get('javascript')!;
        this.languages.set('qmljs', jsParser);
        return jsParser;
      }

      throw error;
    }
  }

  getLanguageForFile(filePath: string): string | undefined {
    const ext = filePath.substring(filePath.lastIndexOf('.'));
    return this.extensionToLanguage.get(ext);
  }

  getSupportedLanguages(): string[] {
    return Array.from(this.languages.keys());
  }

  getSupportedExtensions(): string[] {
    return Array.from(this.extensionToLanguage.keys());
  }

  isFileSupported(filePath: string): boolean {
    return this.getLanguageForFile(filePath) !== undefined;
  }

  async parseFile(filePath: string, content: string): Promise<ParseResult> {
    if (!this.initialized) {
      throw new Error('Parser manager not initialized. Call initialize() first.');
    }

    const language = this.getLanguageForFile(filePath);
    if (!language) {
      throw new Error(`No parser available for file: ${filePath}`);
    }

    // Special handling for Vue files - they don't use tree-sitter parsing
    if (language === 'vue') {
      return {
        tree: null as any, // Vue extractor doesn't need a tree
        language,
        filePath,
        content,
        hash: createHash('md5').update(content).digest('hex')
      };
    }

    // Load parser on-demand (lazy loading)
    const languageObj = await this.loadLanguageParser(language);
    if (!languageObj) {
      throw new Error(`Failed to load language parser: ${language}`);
    }

    const parser = new Parser();
    parser.setLanguage(languageObj);

    let tree: Parser.Tree;
    try {
      tree = parser.parse(content);
    } catch (error) {
      // Tree-sitter WASM parsing failed - create a minimal fallback tree
      log.engine(LogLevel.WARN, `Tree-sitter parsing failed for ${filePath}:`, error);
      tree = this.createFallbackTree(content, language);
    }

    if (!tree || !tree.rootNode) {
      // Second fallback - create an even more basic tree
      log.engine(LogLevel.WARN, `No valid tree or root node for ${filePath}, creating basic fallback`);
      tree = this.createFallbackTree(content, language);
    }

    return {
      tree,
      language,
      filePath,
      content,
      hash: this.hashContent(content)
    };
  }

  async parseString(content: string, language: string): Promise<Parser.Tree> {
    if (!this.initialized) {
      throw new Error('Parser manager not initialized. Call initialize() first.');
    }

    // Load language parser if not already loaded
    let languageObj = this.languages.get(language);
    if (!languageObj) {
      languageObj = await this.loadLanguageParser(language);
      if (!languageObj) {
        throw new Error(`Failed to load parser for language: ${language}`);
      }
    }

    const parser = new Parser();
    parser.setLanguage(languageObj);

    return parser.parse(content);
  }

  generateSymbolId(filePath: string, name: string, line: number, column: number): string {
    return createHash('md5')
      .update(`${filePath}:${name}:${line}:${column}`)
      .digest('hex');
  }

  hashContent(content: string): string {
    return createHash('sha256').update(content).digest('hex');
  }

  // Utility method to get node text from source content
  getNodeText(node: Parser.SyntaxNode, content: string): string {
    return content.substring(node.startIndex, node.endIndex);
  }

  // Utility method to check if parser is available for a language
  hasParser(language: string): boolean {
    return this.languages.has(language);
  }

  // Get parser statistics
  getStats() {
    return {
      initialized: this.initialized,
      loadedLanguages: this.languages.size,
      supportedExtensions: this.extensionToLanguage.size,
      languages: Array.from(this.languages.keys())
    };
  }

  // Add a custom language configuration
  addLanguageConfig(config: LanguageConfig) {
    this.languageConfigs.push(config);

    // If already initialized, load the new language
    if (this.initialized) {
      this.loadLanguage(config);
    }
  }

  private async loadLanguage(config: LanguageConfig) {
    try {
      const wasmPath = config.wasmPath ||
        `./node_modules/tree-sitter-${config.name.replace('_', '-')}/tree-sitter-${config.name.replace('_', '-')}.wasm`;

      const language = await Parser.Language.load(wasmPath);
      this.languages.set(config.name, language);

      // Map extensions to languages
      for (const ext of config.extensions) {
        this.extensionToLanguage.set(ext, config.name);
      }

      log.engine(LogLevel.DEBUG, `Dynamically loaded parser for ${config.name}`);
    } catch (error) {
      log.engine(LogLevel.WARN, `Failed to dynamically load parser for ${config.name}:`, error);
    }
  }

  // Check if content needs reparsing based on hash
  needsReparsing(currentHash: string, newContent: string): boolean {
    const newHash = this.hashContent(newContent);
    return currentHash !== newHash;
  }

  // Cleanup method
  cleanup() {
    this.parsers.clear();
    this.languages.clear();
    this.extensionToLanguage.clear();
    this.initialized = false;
  }

  private createFallbackTree(content: string, language: string): Parser.Tree {
    // Create a minimal fallback tree when tree-sitter parsing fails
    // This is a workaround for complex content that tree-sitter can't handle

    const fallbackNode: any = {
      type: 'document',
      startPosition: { row: 0, column: 0 },
      endPosition: { row: 0, column: content.length },
      startIndex: 0,
      endIndex: content.length,
      text: content,
      children: [],
      parent: null,
      nextSibling: null,
      previousSibling: null,
      toString: () => content,
      walk: () => ({
        nodeType: 'document',
        currentNode: fallbackNode,
        gotoFirstChild: () => false,
        gotoNextSibling: () => false,
        gotoParent: () => false,
        delete: () => {}
      })
    };

    // Add a minimal ERROR child node to indicate parsing issues
    const errorNode: any = {
      type: 'ERROR',
      startPosition: { row: 0, column: 0 },
      endPosition: { row: 0, column: content.length },
      startIndex: 0,
      endIndex: content.length,
      text: content,
      children: [],
      parent: fallbackNode,
      nextSibling: null,
      previousSibling: null,
      toString: () => content
    };

    fallbackNode.children = [errorNode];

    const fallbackTree: any = {
      rootNode: fallbackNode,
      edit: () => {},
      copy: () => fallbackTree,
      delete: () => {},
      getLanguage: () => null,
      walk: () => fallbackNode.walk()
    };

    return fallbackTree as Parser.Tree;
  }
}