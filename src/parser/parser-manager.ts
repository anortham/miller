import Parser from 'web-tree-sitter';
import { createHash } from 'crypto';

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

    // Web frameworks
    { name: 'vue', extensions: ['.vue'] }, // Special handling - no WASM needed
    { name: 'razor', extensions: ['.razor', '.cshtml'], wasmPath: './wasm/tree-sitter-razor.wasm' },

    // Utility parsers
    { name: 'regex', extensions: [] }, // Regex patterns don't have file extensions - handled specially
  ];

  async initialize() {
    if (this.initialized) return;

    await Parser.init();

    // Load all language parsers
    for (const config of this.languageConfigs) {
      try {
        // Skip languages without extensions, except special parsers
        if (config.extensions.length === 0 && !['regex', 'vue'].includes(config.name)) {
          console.log(`Skipping ${config.name} - no file extensions defined`);
          continue;
        }

        // Vue is handled specially - no WASM parser needed
        if (config.name === 'vue') {
          // Map extensions to languages for Vue
          for (const ext of config.extensions) {
            this.extensionToLanguage.set(ext, config.name);
          }
          console.log(`Registered Vue SFC handler for ${config.extensions.join(', ')}`);
          continue;
        }

        // Use Microsoft's pre-built WASM files first, fall back to individual packages
        let wasmPath = config.wasmPath;
        if (!wasmPath) {
          // Try Microsoft's @vscode/tree-sitter-wasm first
          const msWasmPath = `./node_modules/@vscode/tree-sitter-wasm/wasm/tree-sitter-${config.name.replace('_', '-')}.wasm`;

          // Fallback to individual npm packages
          const packageName = config.name.replace('_', '-');
          let wasmFileName = config.name.replace('-', '_'); // For c-sharp -> c_sharp

          // Special cases for known different naming
          if (config.name === 'c_sharp') {
            wasmFileName = 'c_sharp';
          }

          const fallbackPath = `./node_modules/tree-sitter-${packageName}/tree-sitter-${wasmFileName}.wasm`;

          // Choose Microsoft's path for supported languages
          const msSupported = ['javascript', 'typescript', 'python', 'rust', 'go', 'java', 'c_sharp', 'cpp', 'ruby', 'php', 'regex'];
          if (msSupported.includes(config.name)) {
            wasmPath = msWasmPath;
          } else {
            wasmPath = fallbackPath;
          }

        }

        const language = await Parser.Language.load(wasmPath);
        this.languages.set(config.name, language);

        // Map extensions to languages
        for (const ext of config.extensions) {
          this.extensionToLanguage.set(ext, config.name);
        }

        console.log(`Loaded parser for ${config.name}`); // Keep as console for MCP startup visibility
      } catch (error) {
        console.warn(`Failed to load parser for ${config.name}:`, error);

        // Fallback: Use JavaScript parser for TypeScript files
        if (config.name === 'typescript' && this.languages.has('javascript')) {
          console.log(`Using JavaScript parser as fallback for TypeScript`);
          this.languages.set('typescript', this.languages.get('javascript')!);

          // Map TypeScript extensions to typescript language (but use JS parser)
          for (const ext of config.extensions) {
            this.extensionToLanguage.set(ext, 'typescript');
          }
        }
      }
    }

    this.initialized = true;
    console.log(`Parser manager initialized with ${this.languages.size} languages`); // Keep as console for MCP startup visibility
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

    const languageObj = this.languages.get(language);
    if (!languageObj) {
      throw new Error(`Language parser not loaded: ${language}`);
    }

    const parser = new Parser();
    parser.setLanguage(languageObj);

    const tree = parser.parse(content);

    if (!tree.rootNode) {
      throw new Error(`Failed to parse file: ${filePath}`);
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

    const languageObj = this.languages.get(language);
    if (!languageObj) {
      throw new Error(`Language parser not loaded: ${language}`);
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

      console.log(`Dynamically loaded parser for ${config.name}`); // Keep as console for debugging
    } catch (error) {
      console.warn(`Failed to dynamically load parser for ${config.name}:`, error);
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
}