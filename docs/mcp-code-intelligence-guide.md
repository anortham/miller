# Multi-Language Code Intelligence MCP Server

## Project Overview

Build an MCP (Model Context Protocol) server that provides LSP-quality code intelligence across 15-20 programming languages without the overhead of running multiple language servers. This server will parse code using Tree-sitter, extract type information, and provide fast code search and cross-language analysis.

### Key Features
- Parse 15-20 languages using Tree-sitter WASM
- Extract and store type information in a graph structure
- Provide LSP-like features (go-to-definition, find-references, hover, etc.)
- Fast code search with proper handling of code syntax
- Cross-language reference tracking (e.g., REST API calls, FFI bindings)
- Incremental updates via file watching

### Technology Stack
- **Runtime**: Bun (for speed, cross-platform support, and built-in SQLite)
- **Parsing**: Tree-sitter WASM (web-tree-sitter npm package or Microsoft's vscode-tree-sitter-wasm builds)
- **Storage**: SQLite with graph-like schema (built into Bun)
- **Search**: MiniSearch for fuzzy search, ripgrep for exact matches
- **File Watching**: Bun's built-in file watcher or chokidar
- **MCP SDK**: @modelcontextprotocol/sdk

> **Note on Tree-sitter WASM**: You can either use pre-built WASM files from npm packages or build them yourself using [Microsoft's vscode-tree-sitter-wasm](https://github.com/microsoft/vscode-tree-sitter-wasm) - this is the same build system VS Code uses internally!

## Architecture

```
┌─────────────────────────────────────────────────┐
│                 MCP Client                       │
│              (Claude, VSCode, etc)               │
└────────────────────▲────────────────────────────┘
                     │ stdio
┌────────────────────▼────────────────────────────┐
│              MCP Server (Bun)                   │
├──────────────────────────────────────────────────┤
│  ┌──────────────────────────────────────────┐   │
│  │         MCP Request Handler              │   │
│  └──────────────▲───────────────────────────┘   │
│                 │                                │
│  ┌──────────────▼───────────────────────────┐   │
│  │      Code Intelligence Engine            │   │
│  ├──────────────────────────────────────────┤   │
│  │ • Type Resolver                          │   │
│  │ • Reference Finder                       │   │
│  │ • Symbol Extractor                       │   │
│  └──────┬───────────────────┬───────────────┘   │
│         │                   │                    │
│  ┌──────▼────────┐  ┌──────▼───────────────┐   │
│  │  Tree-sitter  │  │   Search Engine       │   │
│  │   Parsers     │  │  (MiniSearch)        │   │
│  └──────┬────────┘  └──────┬───────────────┘   │
│         │                   │                    │
│  ┌──────▼───────────────────▼───────────────┐   │
│  │         SQLite Database                  │   │
│  │  • Symbols Table                         │   │
│  │  • Relationships Graph                   │   │
│  │  • Types Index                           │   │
│  │  • Search Index                          │   │
│  └───────────────────────────────────────────┘  │
│                                                  │
│  ┌──────────────────────────────────────────┐   │
│  │          File Watcher                    │   │
│  │     (Monitors workspace changes)         │   │
│  └──────────────────────────────────────────┘   │
└──────────────────────────────────────────────────┘
```

## Implementation Guide

### Step 1: Project Setup

```bash
# Create project directory
mkdir mcp-code-intelligence
cd mcp-code-intelligence

# Initialize Bun project
bun init

# Install dependencies
bun add @modelcontextprotocol/sdk
bun add web-tree-sitter
bun add minisearch
bun add chokidar

# Install Tree-sitter language parsers
bun add tree-sitter-javascript
bun add tree-sitter-typescript  
bun add tree-sitter-python
bun add tree-sitter-rust
bun add tree-sitter-go
bun add tree-sitter-java
bun add tree-sitter-c-sharp
bun add tree-sitter-c
bun add tree-sitter-cpp
bun add tree-sitter-ruby
bun add tree-sitter-php
# ... add more as needed
```

### Step 2: Database Schema

```typescript
// src/database/schema.ts
import { Database } from 'bun:sqlite';

export class CodeIntelDB {
  private db: Database;

  constructor(dbPath = './code-intel.db') {
    this.db = new Database(dbPath);
    this.initialize();
  }

  private initialize() {
    // Core symbols table - stores all code entities
    this.db.run(`
      CREATE TABLE IF NOT EXISTS symbols (
        id TEXT PRIMARY KEY,
        name TEXT NOT NULL,
        kind TEXT NOT NULL,           -- 'class', 'function', 'variable', etc.
        language TEXT NOT NULL,
        file_path TEXT NOT NULL,
        start_line INTEGER NOT NULL,
        start_column INTEGER NOT NULL,
        end_line INTEGER NOT NULL,
        end_column INTEGER NOT NULL,
        start_byte INTEGER NOT NULL,   -- For incremental updates
        end_byte INTEGER NOT NULL,
        signature TEXT,                -- Type signature
        doc_comment TEXT,              -- Documentation
        visibility TEXT,               -- 'public', 'private', etc.
        parent_id TEXT,
        metadata JSON,                 -- Language-specific data
        FOREIGN KEY(parent_id) REFERENCES symbols(id) ON DELETE CASCADE
      )
    `);

    // Relationships between symbols (calls, extends, implements, etc.)
    this.db.run(`
      CREATE TABLE IF NOT EXISTS relationships (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        from_symbol_id TEXT NOT NULL,
        to_symbol_id TEXT NOT NULL,
        relationship_kind TEXT NOT NULL,  -- 'calls', 'extends', 'implements', 'uses', 'returns'
        file_path TEXT NOT NULL,
        line_number INTEGER NOT NULL,
        confidence REAL DEFAULT 1.0,      -- For inferred relationships
        metadata JSON,
        FOREIGN KEY(from_symbol_id) REFERENCES symbols(id) ON DELETE CASCADE,
        FOREIGN KEY(to_symbol_id) REFERENCES symbols(id) ON DELETE CASCADE
      )
    `);

    // Type information
    this.db.run(`
      CREATE TABLE IF NOT EXISTS types (
        symbol_id TEXT PRIMARY KEY,
        resolved_type TEXT NOT NULL,      -- Fully resolved type
        generic_params JSON,               -- Generic/template parameters
        constraints JSON,                  -- Type constraints
        is_inferred BOOLEAN DEFAULT FALSE,
        language TEXT NOT NULL,
        metadata JSON,
        FOREIGN KEY(symbol_id) REFERENCES symbols(id) ON DELETE CASCADE
      )
    `);

    // Cross-language bindings (API calls, FFI, etc.)
    this.db.run(`
      CREATE TABLE IF NOT EXISTS bindings (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        source_symbol_id TEXT NOT NULL,
        target_symbol_id TEXT,
        binding_kind TEXT NOT NULL,       -- 'rest_api', 'grpc', 'ffi', 'graphql'
        source_language TEXT NOT NULL,
        target_language TEXT,
        endpoint TEXT,                     -- For API calls
        metadata JSON,
        FOREIGN KEY(source_symbol_id) REFERENCES symbols(id) ON DELETE CASCADE,
        FOREIGN KEY(target_symbol_id) REFERENCES symbols(id) ON DELETE CASCADE
      )
    `);

    // File metadata for incremental updates
    this.db.run(`
      CREATE TABLE IF NOT EXISTS files (
        path TEXT PRIMARY KEY,
        language TEXT NOT NULL,
        last_modified INTEGER NOT NULL,
        size INTEGER NOT NULL,
        hash TEXT NOT NULL,
        parse_time_ms INTEGER
      )
    `);

    // Create indexes for performance
    this.db.run(`
      CREATE INDEX IF NOT EXISTS idx_symbols_name ON symbols(name);
      CREATE INDEX IF NOT EXISTS idx_symbols_file ON symbols(file_path);
      CREATE INDEX IF NOT EXISTS idx_symbols_kind ON symbols(kind);
      CREATE INDEX IF NOT EXISTS idx_symbols_parent ON symbols(parent_id);
      CREATE INDEX IF NOT EXISTS idx_relationships_from ON relationships(from_symbol_id);
      CREATE INDEX IF NOT EXISTS idx_relationships_to ON relationships(to_symbol_id);
      CREATE INDEX IF NOT EXISTS idx_types_language ON types(language);
    `);

    // FTS5 table for code search
    this.db.run(`
      CREATE VIRTUAL TABLE IF NOT EXISTS code_search USING fts5(
        symbol_id UNINDEXED,
        name,
        content,
        file_path UNINDEXED,
        tokenize = 'porter ascii'
      )
    `);
  }

  // Add getters for prepared statements (for performance)
  get insertSymbol() {
    return this.db.prepare(`
      INSERT OR REPLACE INTO symbols 
      (id, name, kind, language, file_path, start_line, start_column, 
       end_line, end_column, start_byte, end_byte, signature, 
       doc_comment, visibility, parent_id, metadata)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    `);
  }

  get insertRelationship() {
    return this.db.prepare(`
      INSERT INTO relationships 
      (from_symbol_id, to_symbol_id, relationship_kind, file_path, 
       line_number, confidence, metadata)
      VALUES (?, ?, ?, ?, ?, ?, ?)
    `);
  }
}
```

### Step 3: Tree-sitter Parser Manager

#### WASM File Sources

You have two options for obtaining Tree-sitter WASM files:

**Option 1: Pre-built from npm packages** (Recommended for quick start)
```bash
# These packages include pre-built WASM files
bun add tree-sitter-javascript tree-sitter-typescript tree-sitter-python
# WASM files will be in node_modules/tree-sitter-{language}/tree-sitter-{language}.wasm
```

**Option 2: Microsoft's vscode-tree-sitter-wasm** (How VS Code does it)
```bash
# Clone Microsoft's build system - this is what VS Code actually uses!
git clone https://github.com/microsoft/vscode-tree-sitter-wasm
cd vscode-tree-sitter-wasm
npm install

# Build WASM files (requires emscripten)
npm run build-wasm

# This creates optimized WASM files in the same format VS Code uses
# You can modify build/main.ts to add/remove languages
```

The Microsoft approach gives you:
- Consistent WASM builds across all languages
- Optimized file sizes
- The exact same binaries VS Code uses
- Ability to customize the build process

```typescript
// src/parser/parser-manager.ts
import Parser from 'web-tree-sitter';
import { createHash } from 'crypto';

export class ParserManager {
  private parsers = new Map<string, Parser>();
  private languages = new Map<string, Parser.Language>();
  private initialized = false;

  async initialize() {
    if (this.initialized) return;
    
    await Parser.init();

    // Language configurations
    const languageConfigs = [
      { name: 'javascript', extensions: ['.js', '.jsx', '.mjs'] },
      { name: 'typescript', extensions: ['.ts', '.tsx'] },
      { name: 'python', extensions: ['.py', '.pyw'] },
      { name: 'rust', extensions: ['.rs'] },
      { name: 'go', extensions: ['.go'] },
      { name: 'java', extensions: ['.java'] },
      { name: 'c_sharp', extensions: ['.cs'] },
      { name: 'c', extensions: ['.c', '.h'] },
      { name: 'cpp', extensions: ['.cpp', '.cc', '.cxx', '.hpp', '.hxx'] },
      { name: 'ruby', extensions: ['.rb'] },
      { name: 'php', extensions: ['.php'] },
      // Add more languages as needed
    ];

    // Load all language parsers
    for (const config of languageConfigs) {
      // Use either npm package WASM or Microsoft's built WASM
      const wasmPath = `./node_modules/tree-sitter-${config.name.replace('_', '-')}/tree-sitter-${config.name.replace('_', '-')}.wasm`;
      // OR if using Microsoft's builds:
      // const wasmPath = `./vscode-tree-sitter-wasm/out/${config.name}.wasm`;
      
      const language = await Parser.Language.load(wasmPath);
      this.languages.set(config.name, language);
      
      // Map extensions to languages
      for (const ext of config.extensions) {
        this.extensionToLanguage.set(ext, config.name);
      }
    }

    this.initialized = true;
  }

  private extensionToLanguage = new Map<string, string>();

  getLanguageForFile(filePath: string): string | undefined {
    const ext = filePath.substring(filePath.lastIndexOf('.'));
    return this.extensionToLanguage.get(ext);
  }

  async parseFile(filePath: string, content: string): Promise<ParseResult> {
    const language = this.getLanguageForFile(filePath);
    if (!language) {
      throw new Error(`No parser available for file: ${filePath}`);
    }

    const parser = new Parser();
    parser.setLanguage(this.languages.get(language)!);
    
    const tree = parser.parse(content);
    
    return {
      tree,
      language,
      filePath,
      content,
      hash: createHash('sha256').update(content).digest('hex')
    };
  }

  generateSymbolId(filePath: string, name: string, line: number, column: number): string {
    return createHash('md5')
      .update(`${filePath}:${name}:${line}:${column}`)
      .digest('hex');
  }
}

interface ParseResult {
  tree: Parser.Tree;
  language: string;
  filePath: string;
  content: string;
  hash: string;
}
```

### Step 4: Type Extractor Base Class

```typescript
// src/extractors/base-extractor.ts
import { Parser } from 'web-tree-sitter';

export interface Symbol {
  id: string;
  name: string;
  kind: SymbolKind;
  language: string;
  filePath: string;
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
  startByte: number;
  endByte: number;
  signature?: string;
  docComment?: string;
  visibility?: 'public' | 'private' | 'protected';
  parentId?: string;
  metadata?: any;
}

export enum SymbolKind {
  Class = 'class',
  Interface = 'interface',
  Function = 'function',
  Method = 'method',
  Variable = 'variable',
  Constant = 'constant',
  Property = 'property',
  Enum = 'enum',
  EnumMember = 'enum_member',
  Module = 'module',
  Namespace = 'namespace',
  Type = 'type',
  Trait = 'trait',
  Struct = 'struct'
}

export interface Relationship {
  fromSymbolId: string;
  toSymbolId: string;
  kind: RelationshipKind;
  filePath: string;
  lineNumber: number;
  confidence: number;
  metadata?: any;
}

export enum RelationshipKind {
  Calls = 'calls',
  Extends = 'extends',
  Implements = 'implements',
  Uses = 'uses',
  Returns = 'returns',
  Parameter = 'parameter',
  Imports = 'imports',
  Instantiates = 'instantiates'
}

export abstract class BaseExtractor {
  constructor(
    protected language: string,
    protected filePath: string,
    protected content: string
  ) {}

  abstract extractSymbols(tree: Parser.Tree): Symbol[];
  abstract extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[];
  abstract inferTypes(symbols: Symbol[]): Map<string, string>;

  protected getNodeText(node: Parser.SyntaxNode): string {
    return this.content.substring(node.startIndex, node.endIndex);
  }

  protected findDocComment(node: Parser.SyntaxNode): string | undefined {
    // Look for comment nodes preceding the current node
    const previousSibling = node.previousNamedSibling;
    if (previousSibling?.type.includes('comment')) {
      return this.getNodeText(previousSibling);
    }
    return undefined;
  }

  protected generateId(name: string, line: number, column: number): string {
    const crypto = require('crypto');
    return crypto.createHash('md5')
      .update(`${this.filePath}:${name}:${line}:${column}`)
      .digest('hex');
  }
}
```

### Step 5: TypeScript/JavaScript Extractor Example

```typescript
// src/extractors/typescript-extractor.ts
import { Parser } from 'web-tree-sitter';
import { BaseExtractor, Symbol, SymbolKind, Relationship, RelationshipKind } from './base-extractor';

export class TypeScriptExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    const visitNode = (node: Parser.SyntaxNode, parentId?: string) => {
      let symbol: Symbol | null = null;

      switch (node.type) {
        case 'class_declaration':
          symbol = this.extractClass(node, parentId);
          break;
        case 'interface_declaration':
          symbol = this.extractInterface(node, parentId);
          break;
        case 'function_declaration':
        case 'function':
        case 'arrow_function':
          symbol = this.extractFunction(node, parentId);
          break;
        case 'method_definition':
          symbol = this.extractMethod(node, parentId);
          break;
        case 'variable_declarator':
          symbol = this.extractVariable(node, parentId);
          break;
        case 'type_alias_declaration':
          symbol = this.extractTypeAlias(node, parentId);
          break;
        case 'enum_declaration':
          symbol = this.extractEnum(node, parentId);
          break;
      }

      if (symbol) {
        symbols.push(symbol);
        parentId = symbol.id;
      }

      // Recursively visit children
      for (const child of node.children) {
        visitNode(child, parentId);
      }
    };

    visitNode(tree.rootNode);
    return symbols;
  }

  private extractClass(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';
    
    // Extract extends clause
    const heritage = node.childForFieldName('heritage');
    const extendsClause = heritage?.children.find(c => c.type === 'extends_clause');
    const implementsClause = heritage?.children.find(c => c.type === 'implements_clause');

    const signature = this.buildClassSignature(node);
    
    return {
      id: this.generateId(name, node.startPosition.row, node.startPosition.column),
      name,
      kind: SymbolKind.Class,
      language: this.language,
      filePath: this.filePath,
      startLine: node.startPosition.row + 1,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row + 1,
      endColumn: node.endPosition.column,
      startByte: node.startIndex,
      endByte: node.endIndex,
      signature,
      docComment: this.findDocComment(node),
      visibility: this.extractVisibility(node),
      parentId,
      metadata: {
        extends: extendsClause ? this.getNodeText(extendsClause) : null,
        implements: implementsClause ? this.getNodeText(implementsClause) : null,
        isAbstract: node.children.some(c => c.type === 'abstract')
      }
    };
  }

  private extractFunction(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';
    
    const parameters = node.childForFieldName('parameters');
    const returnType = node.childForFieldName('return_type');
    
    const signature = this.buildFunctionSignature(node);
    
    return {
      id: this.generateId(name, node.startPosition.row, node.startPosition.column),
      name,
      kind: SymbolKind.Function,
      language: this.language,
      filePath: this.filePath,
      startLine: node.startPosition.row + 1,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row + 1,
      endColumn: node.endPosition.column,
      startByte: node.startIndex,
      endByte: node.endIndex,
      signature,
      docComment: this.findDocComment(node),
      visibility: this.extractVisibility(node),
      parentId,
      metadata: {
        isAsync: node.children.some(c => c.type === 'async'),
        isGenerator: node.children.some(c => c.type === '*'),
        parameters: parameters ? this.getNodeText(parameters) : null,
        returnType: returnType ? this.getNodeText(returnType) : null
      }
    };
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];
    const symbolMap = new Map(symbols.map(s => [s.name, s]));

    const visitNode = (node: Parser.SyntaxNode) => {
      switch (node.type) {
        case 'call_expression': {
          const callee = node.childForFieldName('function');
          if (callee) {
            const calleeName = this.getNodeText(callee);
            const calledSymbol = symbolMap.get(calleeName);
            
            if (calledSymbol) {
              // Find the calling symbol
              const callingSymbol = this.findContainingSymbol(node, symbols);
              if (callingSymbol) {
                relationships.push({
                  fromSymbolId: callingSymbol.id,
                  toSymbolId: calledSymbol.id,
                  kind: RelationshipKind.Calls,
                  filePath: this.filePath,
                  lineNumber: node.startPosition.row + 1,
                  confidence: 1.0
                });
              }
            }
          }
          break;
        }

        case 'extends_clause': {
          const parent = node.parent;
          if (parent?.type === 'class_declaration') {
            const className = this.getNodeText(parent.childForFieldName('name')!);
            const classSymbol = symbolMap.get(className);
            const superClass = this.getNodeText(node.childForFieldName('value')!);
            const superSymbol = symbolMap.get(superClass);
            
            if (classSymbol && superSymbol) {
              relationships.push({
                fromSymbolId: classSymbol.id,
                toSymbolId: superSymbol.id,
                kind: RelationshipKind.Extends,
                filePath: this.filePath,
                lineNumber: node.startPosition.row + 1,
                confidence: 1.0
              });
            }
          }
          break;
        }

        case 'import_statement': {
          // Handle imports for cross-file relationships
          const source = node.childForFieldName('source');
          if (source) {
            const importPath = this.getNodeText(source).replace(/['"]/g, '');
            // Store import relationships for later resolution
            relationships.push({
              fromSymbolId: `import:${this.filePath}`,
              toSymbolId: `module:${importPath}`,
              kind: RelationshipKind.Imports,
              filePath: this.filePath,
              lineNumber: node.startPosition.row + 1,
              confidence: 1.0,
              metadata: { importPath }
            });
          }
          break;
        }
      }

      // Visit children
      for (const child of node.children) {
        visitNode(child);
      }
    };

    visitNode(tree.rootNode);
    return relationships;
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();
    
    for (const symbol of symbols) {
      if (symbol.metadata?.returnType) {
        types.set(symbol.id, symbol.metadata.returnType);
      } else if (symbol.kind === SymbolKind.Variable) {
        // Try to infer from initialization
        const inferredType = this.inferVariableType(symbol);
        if (inferredType) {
          types.set(symbol.id, inferredType);
        }
      }
    }
    
    return types;
  }

  private inferVariableType(symbol: Symbol): string | null {
    // Basic type inference from initialization
    // This would need to be more sophisticated in practice
    const content = this.content.substring(symbol.startByte, symbol.endByte);
    
    if (content.includes('= new ')) {
      const match = content.match(/= new (\w+)/);
      if (match) return match[1];
    }
    
    if (content.includes('= [')) return 'Array';
    if (content.includes('= {')) return 'Object';
    if (content.includes('= "') || content.includes("= '")) return 'string';
    if (content.match(/= \d+/)) return 'number';
    if (content.includes('= true') || content.includes('= false')) return 'boolean';
    
    return null;
  }

  private findContainingSymbol(node: Parser.SyntaxNode, symbols: Symbol[]): Symbol | undefined {
    const position = node.startPosition;
    
    return symbols.find(s => 
      s.startLine <= position.row + 1 &&
      s.endLine >= position.row + 1 &&
      s.startColumn <= position.column &&
      s.endColumn >= position.column
    );
  }

  private extractVisibility(node: Parser.SyntaxNode): 'public' | 'private' | 'protected' | undefined {
    const modifiers = node.children.filter(c => 
      ['public', 'private', 'protected'].includes(c.type)
    );
    
    if (modifiers.length > 0) {
      return modifiers[0].type as 'public' | 'private' | 'protected';
    }
    
    return undefined;
  }

  private buildClassSignature(node: Parser.SyntaxNode): string {
    const name = this.getNodeText(node.childForFieldName('name')!);
    const typeParams = node.childForFieldName('type_parameters');
    const heritage = node.childForFieldName('heritage');
    
    let signature = `class ${name}`;
    if (typeParams) signature += this.getNodeText(typeParams);
    if (heritage) signature += ` ${this.getNodeText(heritage)}`;
    
    return signature;
  }

  private buildFunctionSignature(node: Parser.SyntaxNode): string {
    const name = node.childForFieldName('name');
    const params = node.childForFieldName('parameters');
    const returnType = node.childForFieldName('return_type');
    
    let signature = name ? this.getNodeText(name) : 'function';
    if (params) signature += this.getNodeText(params);
    if (returnType) signature += `: ${this.getNodeText(returnType)}`;
    
    return signature;
  }

  private extractInterface(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // Similar to extractClass but for interfaces
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';
    
    return {
      id: this.generateId(name, node.startPosition.row, node.startPosition.column),
      name,
      kind: SymbolKind.Interface,
      language: this.language,
      filePath: this.filePath,
      startLine: node.startPosition.row + 1,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row + 1,
      endColumn: node.endPosition.column,
      startByte: node.startIndex,
      endByte: node.endIndex,
      signature: `interface ${name}`,
      docComment: this.findDocComment(node),
      parentId
    };
  }

  private extractMethod(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // Similar to extractFunction but for class methods
    return this.extractFunction(node, parentId);
  }

  private extractVariable(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';
    
    return {
      id: this.generateId(name, node.startPosition.row, node.startPosition.column),
      name,
      kind: SymbolKind.Variable,
      language: this.language,
      filePath: this.filePath,
      startLine: node.startPosition.row + 1,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row + 1,
      endColumn: node.endPosition.column,
      startByte: node.startIndex,
      endByte: node.endIndex,
      parentId
    };
  }

  private extractTypeAlias(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';
    const value = node.childForFieldName('value');
    
    return {
      id: this.generateId(name, node.startPosition.row, node.startPosition.column),
      name,
      kind: SymbolKind.Type,
      language: this.language,
      filePath: this.filePath,
      startLine: node.startPosition.row + 1,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row + 1,
      endColumn: node.endPosition.column,
      startByte: node.startIndex,
      endByte: node.endIndex,
      signature: `type ${name} = ${value ? this.getNodeText(value) : 'unknown'}`,
      parentId
    };
  }

  private extractEnum(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';
    
    return {
      id: this.generateId(name, node.startPosition.row, node.startPosition.column),
      name,
      kind: SymbolKind.Enum,
      language: this.language,
      filePath: this.filePath,
      startLine: node.startPosition.row + 1,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row + 1,
      endColumn: node.endPosition.column,
      startByte: node.startIndex,
      endByte: node.endIndex,
      signature: `enum ${name}`,
      parentId
    };
  }
}
```

### Step 6: Search Engine Integration

```typescript
// src/search/search-engine.ts
import MiniSearch from 'minisearch';
import { $ } from 'bun';
import { Database } from 'bun:sqlite';

export interface SearchResult {
  file: string;
  line: number;
  column: number;
  text: string;
  score?: number;
  symbolId?: string;
  kind?: string;
}

export class SearchEngine {
  private miniSearch: MiniSearch;
  private db: Database;
  
  constructor(db: Database) {
    this.db = db;
    this.miniSearch = new MiniSearch({
      fields: ['name', 'content', 'signature'],
      storeFields: ['file', 'line', 'column', 'kind', 'symbolId'],
      searchOptions: {
        boost: { name: 2 },
        fuzzy: 0.2,
        prefix: true
      },
      tokenize: (text: string) => {
        // Custom tokenizer for code
        return text
          .split(/(?=[A-Z])|[^a-zA-Z0-9]+/) // Split on camelCase and special chars
          .filter(t => t.length > 1)
          .map(t => t.toLowerCase());
      }
    });
  }

  async indexSymbols() {
    const symbols = this.db.prepare(`
      SELECT 
        s.id as symbolId,
        s.name,
        s.kind,
        s.signature as content,
        s.file_path as file,
        s.start_line as line,
        s.start_column as column
      FROM symbols s
    `).all();

    const documents = symbols.map((s: any, index: number) => ({
      id: index,
      symbolId: s.symbolId,
      name: s.name,
      content: s.content || s.name,
      signature: s.content,
      file: s.file,
      line: s.line,
      column: s.column,
      kind: s.kind
    }));

    this.miniSearch.addAll(documents);
  }

  async searchFuzzy(query: string, limit = 50): Promise<SearchResult[]> {
    const results = this.miniSearch.search(query, { limit });
    
    return results.map(r => ({
      file: r.file,
      line: r.line,
      column: r.column,
      text: r.content || r.name,
      score: r.score,
      symbolId: r.symbolId,
      kind: r.kind
    }));
  }

  async searchExact(pattern: string, directory?: string): Promise<SearchResult[]> {
    // Use ripgrep for exact pattern matching
    const dir = directory || '.';
    
    try {
      const result = await $`rg --json --line-number --column "${pattern}" ${dir}`.text();
      
      const hits: SearchResult[] = [];
      for (const line of result.split('\n')) {
        if (!line) continue;
        
        const data = JSON.parse(line);
        if (data.type === 'match') {
          hits.push({
            file: data.data.path.text,
            line: data.data.line_number,
            column: data.data.submatches[0].start,
            text: data.data.lines.text
          });
        }
      }
      
      return hits;
    } catch (error) {
      // Ripgrep not available, fall back to SQL search
      return this.searchDatabase(pattern);
    }
  }

  private async searchDatabase(pattern: string): Promise<SearchResult[]> {
    const results = this.db.prepare(`
      SELECT 
        file_path as file,
        start_line as line,
        start_column as column,
        name as text,
        id as symbolId,
        kind
      FROM symbols
      WHERE name LIKE ?
      ORDER BY name
      LIMIT 100
    `).all(`%${pattern}%`);

    return results as SearchResult[];
  }

  async searchByType(typeName: string): Promise<SearchResult[]> {
    const results = this.db.prepare(`
      SELECT 
        s.file_path as file,
        s.start_line as line,
        s.start_column as column,
        s.name as text,
        s.id as symbolId,
        s.kind,
        t.resolved_type
      FROM types t
      JOIN symbols s ON s.id = t.symbol_id
      WHERE t.resolved_type LIKE ?
      ORDER BY s.name
      LIMIT 100
    `).all(`%${typeName}%`);

    return results as SearchResult[];
  }

  async updateIndex(filePath: string, symbols: any[]) {
    // Remove old entries for this file
    const oldDocs = this.miniSearch.search(filePath, { 
      filter: (result: any) => result.file === filePath 
    });
    
    this.miniSearch.removeAll(oldDocs);
    
    // Add new entries
    const documents = symbols.map((s, index) => ({
      id: `${filePath}:${index}`,
      symbolId: s.id,
      name: s.name,
      content: s.signature || s.name,
      file: filePath,
      line: s.startLine,
      column: s.startColumn,
      kind: s.kind
    }));
    
    this.miniSearch.addAll(documents);
  }
}
```

### Step 7: File Watcher and Incremental Updates

```typescript
// src/watcher/file-watcher.ts
import { watch } from 'fs';
import { readFile } from 'fs/promises';
import path from 'path';

export class FileWatcher {
  private watchers = new Map<string, FSWatcher>();
  private updateQueue = new Map<string, number>();
  private processing = false;
  
  constructor(
    private onFileChange: (filePath: string, content: string) => Promise<void>,
    private onFileDelete: (filePath: string) => Promise<void>
  ) {}

  async watchDirectory(dirPath: string, recursive = true) {
    const watcher = watch(
      dirPath, 
      { recursive },
      async (eventType, filename) => {
        if (!filename) return;
        
        const filePath = path.join(dirPath, filename);
        
        // Debounce rapid changes
        if (this.updateQueue.has(filePath)) {
          clearTimeout(this.updateQueue.get(filePath));
        }
        
        this.updateQueue.set(
          filePath,
          setTimeout(() => this.handleFileChange(filePath, eventType), 100)
        );
      }
    );
    
    this.watchers.set(dirPath, watcher);
  }

  private async handleFileChange(filePath: string, eventType: string) {
    this.updateQueue.delete(filePath);
    
    try {
      if (eventType === 'rename') {
        // Check if file exists (rename can mean delete)
        try {
          await readFile(filePath);
          // File exists, treat as change
          const content = await readFile(filePath, 'utf-8');
          await this.onFileChange(filePath, content);
        } catch {
          // File doesn't exist, treat as delete
          await this.onFileDelete(filePath);
        }
      } else if (eventType === 'change') {
        const content = await readFile(filePath, 'utf-8');
        await this.onFileChange(filePath, content);
      }
    } catch (error) {
      console.error(`Error handling file change for ${filePath}:`, error);
    }
  }

  stopWatching(dirPath?: string) {
    if (dirPath) {
      const watcher = this.watchers.get(dirPath);
      if (watcher) {
        watcher.close();
        this.watchers.delete(dirPath);
      }
    } else {
      // Stop all watchers
      for (const [path, watcher] of this.watchers) {
        watcher.close();
      }
      this.watchers.clear();
    }
  }
}
```

### Step 8: Code Intelligence Engine

```typescript
// src/engine/code-intelligence.ts
import { Database } from 'bun:sqlite';
import { ParserManager } from '../parser/parser-manager';
import { SearchEngine } from '../search/search-engine';
import { FileWatcher } from '../watcher/file-watcher';
import { TypeScriptExtractor } from '../extractors/typescript-extractor';
// Import other extractors...

export class CodeIntelligenceEngine {
  private db: Database;
  private parserManager: ParserManager;
  private searchEngine: SearchEngine;
  private fileWatcher: FileWatcher;
  private extractors = new Map<string, typeof BaseExtractor>();

  constructor(dbPath = './code-intel.db') {
    this.db = new Database(dbPath);
    this.parserManager = new ParserManager();
    this.searchEngine = new SearchEngine(this.db);
    
    this.fileWatcher = new FileWatcher(
      this.handleFileChange.bind(this),
      this.handleFileDelete.bind(this)
    );
    
    // Register extractors
    this.extractors.set('typescript', TypeScriptExtractor);
    this.extractors.set('javascript', TypeScriptExtractor);
    // Register other extractors...
  }

  async initialize() {
    await this.parserManager.initialize();
    await this.searchEngine.indexSymbols();
  }

  async indexWorkspace(workspacePath: string) {
    console.log(`Indexing workspace: ${workspacePath}`);
    
    // Get all code files
    const files = await this.getAllCodeFiles(workspacePath);
    
    // Process files in batches for better performance
    const batchSize = 10;
    for (let i = 0; i < files.length; i += batchSize) {
      const batch = files.slice(i, i + batchSize);
      await Promise.all(batch.map(file => this.indexFile(file)));
      console.log(`Indexed ${Math.min(i + batchSize, files.length)}/${files.length} files`);
    }
    
    // Start watching for changes
    await this.fileWatcher.watchDirectory(workspacePath);
    
    console.log('Workspace indexing complete');
  }

  private async indexFile(filePath: string) {
    try {
      const content = await readFile(filePath, 'utf-8');
      const parseResult = await this.parserManager.parseFile(filePath, content);
      
      // Get appropriate extractor
      const ExtractorClass = this.extractors.get(parseResult.language);
      if (!ExtractorClass) {
        console.warn(`No extractor for language: ${parseResult.language}`);
        return;
      }
      
      const extractor = new ExtractorClass(
        parseResult.language,
        filePath,
        content
      );
      
      // Extract symbols and relationships
      const symbols = extractor.extractSymbols(parseResult.tree);
      const relationships = extractor.extractRelationships(parseResult.tree, symbols);
      const types = extractor.inferTypes(symbols);
      
      // Store in database
      await this.storeSymbols(symbols);
      await this.storeRelationships(relationships);
      await this.storeTypes(types);
      
      // Update search index
      await this.searchEngine.updateIndex(filePath, symbols);
      
      // Update file metadata
      await this.updateFileMetadata(filePath, parseResult.hash);
      
    } catch (error) {
      console.error(`Error indexing file ${filePath}:`, error);
    }
  }

  private async handleFileChange(filePath: string, content: string) {
    console.log(`File changed: ${filePath}`);
    
    // Check if file needs reindexing
    const needsReindex = await this.checkIfNeedsReindex(filePath, content);
    if (!needsReindex) return;
    
    // Clear old data
    await this.clearFileData(filePath);
    
    // Reindex
    await this.indexFile(filePath);
  }

  private async handleFileDelete(filePath: string) {
    console.log(`File deleted: ${filePath}`);
    await this.clearFileData(filePath);
  }

  // LSP-like features
  async goToDefinition(filePath: string, line: number, column: number) {
    const symbol = await this.findSymbolAtPosition(filePath, line, column);
    if (!symbol) return null;
    
    return {
      file: symbol.file_path,
      line: symbol.start_line,
      column: symbol.start_column
    };
  }

  async findReferences(symbolId: string) {
    const references = this.db.prepare(`
      SELECT 
        s.file_path,
        s.start_line,
        s.start_column,
        s.name
      FROM relationships r
      JOIN symbols s ON s.id = r.from_symbol_id
      WHERE r.to_symbol_id = ?
        AND r.relationship_kind IN ('calls', 'uses', 'references')
    `).all(symbolId);
    
    return references;
  }

  async hover(filePath: string, line: number, column: number) {
    const symbol = await this.findSymbolAtPosition(filePath, line, column);
    if (!symbol) return null;
    
    const type = this.db.prepare(`
      SELECT * FROM types WHERE symbol_id = ?
    `).get(symbol.id);
    
    return {
      name: symbol.name,
      kind: symbol.kind,
      signature: symbol.signature,
      type: type?.resolved_type,
      documentation: symbol.doc_comment
    };
  }

  async getCallHierarchy(symbolId: string, direction: 'incoming' | 'outgoing') {
    const query = direction === 'incoming'
      ? `
        WITH RECURSIVE call_tree AS (
          SELECT from_symbol_id as symbol_id, 0 as level
          FROM relationships
          WHERE to_symbol_id = ? AND relationship_kind = 'calls'
          
          UNION ALL
          
          SELECT r.from_symbol_id, ct.level + 1
          FROM relationships r
          JOIN call_tree ct ON r.to_symbol_id = ct.symbol_id
          WHERE r.relationship_kind = 'calls' AND ct.level < 5
        )
        SELECT DISTINCT s.*, ct.level
        FROM call_tree ct
        JOIN symbols s ON s.id = ct.symbol_id
        ORDER BY ct.level
      `
      : `
        WITH RECURSIVE call_tree AS (
          SELECT to_symbol_id as symbol_id, 0 as level
          FROM relationships
          WHERE from_symbol_id = ? AND relationship_kind = 'calls'
          
          UNION ALL
          
          SELECT r.to_symbol_id, ct.level + 1
          FROM relationships r
          JOIN call_tree ct ON r.from_symbol_id = ct.symbol_id
          WHERE r.relationship_kind = 'calls' AND ct.level < 5
        )
        SELECT DISTINCT s.*, ct.level
        FROM call_tree ct
        JOIN symbols s ON s.id = ct.symbol_id
        ORDER BY ct.level
      `;
    
    return this.db.prepare(query).all(symbolId);
  }

  // Cross-language features
  async findCrossLanguageBindings(filePath: string) {
    // Find API calls, FFI bindings, etc.
    const bindings = this.db.prepare(`
      SELECT 
        b.*,
        s1.name as source_name,
        s1.language as source_language,
        s2.name as target_name,
        s2.language as target_language
      FROM bindings b
      JOIN symbols s1 ON s1.id = b.source_symbol_id
      LEFT JOIN symbols s2 ON s2.id = b.target_symbol_id
      WHERE s1.file_path = ?
    `).all(filePath);
    
    return bindings;
  }

  // Helper methods
  private async findSymbolAtPosition(filePath: string, line: number, column: number) {
    return this.db.prepare(`
      SELECT * FROM symbols
      WHERE file_path = ?
        AND start_line <= ?
        AND end_line >= ?
        AND start_column <= ?
        AND end_column >= ?
      ORDER BY (end_line - start_line) * (end_column - start_column) ASC
      LIMIT 1
    `).get(filePath, line, line, column, column);
  }

  private async storeSymbols(symbols: Symbol[]) {
    const insert = this.db.prepare(`
      INSERT OR REPLACE INTO symbols 
      (id, name, kind, language, file_path, start_line, start_column,
       end_line, end_column, start_byte, end_byte, signature,
       doc_comment, visibility, parent_id, metadata)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
    `);
    
    const transaction = this.db.transaction((symbols) => {
      for (const symbol of symbols) {
        insert.run(
          symbol.id,
          symbol.name,
          symbol.kind,
          symbol.language,
          symbol.filePath,
          symbol.startLine,
          symbol.startColumn,
          symbol.endLine,
          symbol.endColumn,
          symbol.startByte,
          symbol.endByte,
          symbol.signature,
          symbol.docComment,
          symbol.visibility,
          symbol.parentId,
          JSON.stringify(symbol.metadata)
        );
      }
    });
    
    transaction(symbols);
  }

  private async storeRelationships(relationships: Relationship[]) {
    const insert = this.db.prepare(`
      INSERT OR IGNORE INTO relationships
      (from_symbol_id, to_symbol_id, relationship_kind, file_path,
       line_number, confidence, metadata)
      VALUES (?, ?, ?, ?, ?, ?, ?)
    `);
    
    const transaction = this.db.transaction((relationships) => {
      for (const rel of relationships) {
        insert.run(
          rel.fromSymbolId,
          rel.toSymbolId,
          rel.kind,
          rel.filePath,
          rel.lineNumber,
          rel.confidence,
          JSON.stringify(rel.metadata)
        );
      }
    });
    
    transaction(relationships);
  }

  private async storeTypes(types: Map<string, string>) {
    const insert = this.db.prepare(`
      INSERT OR REPLACE INTO types
      (symbol_id, resolved_type, language, is_inferred)
      VALUES (?, ?, ?, ?)
    `);
    
    const transaction = this.db.transaction((entries) => {
      for (const [symbolId, type] of entries) {
        insert.run(symbolId, type, 'typescript', true);
      }
    });
    
    transaction(Array.from(types.entries()));
  }

  private async clearFileData(filePath: string) {
    // Delete all symbols and related data for a file
    this.db.run('DELETE FROM symbols WHERE file_path = ?', filePath);
    this.db.run('DELETE FROM relationships WHERE file_path = ?', filePath);
    this.db.run('DELETE FROM files WHERE path = ?', filePath);
  }

  private async updateFileMetadata(filePath: string, hash: string) {
    this.db.prepare(`
      INSERT OR REPLACE INTO files (path, language, last_modified, size, hash)
      VALUES (?, ?, ?, ?, ?)
    `).run(
      filePath,
      this.parserManager.getLanguageForFile(filePath),
      Date.now(),
      0, // Would calculate actual file size
      hash
    );
  }

  private async checkIfNeedsReindex(filePath: string, content: string): Promise<boolean> {
    const existing = this.db.prepare(
      'SELECT hash FROM files WHERE path = ?'
    ).get(filePath);
    
    if (!existing) return true;
    
    const newHash = require('crypto')
      .createHash('sha256')
      .update(content)
      .digest('hex');
    
    return existing.hash !== newHash;
  }

  private async getAllCodeFiles(dirPath: string): Promise<string[]> {
    const { readdir } = require('fs/promises');
    const path = require('path');
    
    const files: string[] = [];
    const extensions = new Set([
      '.js', '.jsx', '.ts', '.tsx', '.py', '.rs', '.go',
      '.java', '.cs', '.c', '.cpp', '.rb', '.php'
    ]);
    
    async function walk(dir: string) {
      const entries = await readdir(dir, { withFileTypes: true });
      
      for (const entry of entries) {
        const fullPath = path.join(dir, entry.name);
        
        if (entry.isDirectory()) {
          // Skip node_modules, .git, etc.
          if (!entry.name.startsWith('.') && entry.name !== 'node_modules') {
            await walk(fullPath);
          }
        } else if (entry.isFile()) {
          const ext = path.extname(entry.name);
          if (extensions.has(ext)) {
            files.push(fullPath);
          }
        }
      }
    }
    
    await walk(dirPath);
    return files;
  }
}
```

### Step 9: MCP Server Implementation

```typescript
// src/mcp-server.ts
import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { CodeIntelligenceEngine } from './engine/code-intelligence';

class CodeIntelligenceMCPServer {
  private server: Server;
  private engine: CodeIntelligenceEngine;
  private workspacePath: string = process.cwd();

  constructor() {
    this.engine = new CodeIntelligenceEngine();
    
    this.server = new Server({
      name: "code-intelligence",
      version: "1.0.0",
      description: "Multi-language code intelligence server"
    }, {
      capabilities: {
        tools: {}
      }
    });

    this.setupHandlers();
  }

  private setupHandlers() {
    // List available tools
    this.server.setRequestHandler("tools/list", async () => ({
      tools: [
        {
          name: "search_code",
          description: "Search for code symbols, functions, classes, etc.",
          inputSchema: {
            type: "object",
            properties: {
              query: { type: "string", description: "Search query" },
              type: { 
                type: "string", 
                enum: ["fuzzy", "exact", "type"],
                description: "Search type"
              },
              limit: { type: "number", description: "Max results (default: 50)" }
            },
            required: ["query"]
          }
        },
        {
          name: "goto_definition",
          description: "Find the definition of a symbol",
          inputSchema: {
            type: "object",
            properties: {
              file: { type: "string", description: "File path" },
              line: { type: "number", description: "Line number" },
              column: { type: "number", description: "Column number" }
            },
            required: ["file", "line", "column"]
          }
        },
        {
          name: "find_references",
          description: "Find all references to a symbol",
          inputSchema: {
            type: "object",
            properties: {
              file: { type: "string", description: "File path" },
              line: { type: "number", description: "Line number" },
              column: { type: "number", description: "Column number" }
            },
            required: ["file", "line", "column"]
          }
        },
        {
          name: "get_hover_info",
          description: "Get type and documentation for symbol at position",
          inputSchema: {
            type: "object",
            properties: {
              file: { type: "string", description: "File path" },
              line: { type: "number", description: "Line number" },
              column: { type: "number", description: "Column number" }
            },
            required: ["file", "line", "column"]
          }
        },
        {
          name: "get_call_hierarchy",
          description: "Get incoming or outgoing call hierarchy for a function",
          inputSchema: {
            type: "object",
            properties: {
              file: { type: "string", description: "File path" },
              line: { type: "number", description: "Line number" },
              column: { type: "number", description: "Column number" },
              direction: { 
                type: "string", 
                enum: ["incoming", "outgoing"],
                description: "Direction of call hierarchy"
              }
            },
            required: ["file", "line", "column", "direction"]
          }
        },
        {
          name: "find_cross_language_bindings",
          description: "Find API calls and FFI bindings between languages",
          inputSchema: {
            type: "object",
            properties: {
              file: { type: "string", description: "File path to analyze" }
            },
            required: ["file"]
          }
        },
        {
          name: "index_workspace",
          description: "Index or reindex a workspace directory",
          inputSchema: {
            type: "object",
            properties: {
              path: { type: "string", description: "Workspace path (default: current directory)" }
            }
          }
        }
      ]
    }));

    // Handle tool calls
    this.server.setRequestHandler("tools/call", async (request) => {
      const { name, arguments: args } = request.params;

      switch (name) {
        case "search_code": {
          const results = args.type === 'exact' 
            ? await this.engine.searchEngine.searchExact(args.query)
            : args.type === 'type'
            ? await this.engine.searchEngine.searchByType(args.query)
            : await this.engine.searchEngine.searchFuzzy(args.query, args.limit || 50);
          
          return {
            content: [{
              type: "text",
              text: JSON.stringify(results, null, 2)
            }]
          };
        }

        case "goto_definition": {
          const result = await this.engine.goToDefinition(
            args.file, args.line, args.column
          );
          
          return {
            content: [{
              type: "text",
              text: result 
                ? `Definition found at ${result.file}:${result.line}:${result.column}`
                : "No definition found"
            }]
          };
        }

        case "find_references": {
          const symbol = await this.engine.findSymbolAtPosition(
            args.file, args.line, args.column
          );
          
          if (!symbol) {
            return {
              content: [{
                type: "text",
                text: "No symbol found at position"
              }]
            };
          }

          const references = await this.engine.findReferences(symbol.id);
          
          return {
            content: [{
              type: "text",
              text: JSON.stringify(references, null, 2)
            }]
          };
        }

        case "get_hover_info": {
          const info = await this.engine.hover(
            args.file, args.line, args.column
          );
          
          return {
            content: [{
              type: "text",
              text: info 
                ? JSON.stringify(info, null, 2)
                : "No information available"
            }]
          };
        }

        case "get_call_hierarchy": {
          const symbol = await this.engine.findSymbolAtPosition(
            args.file, args.line, args.column
          );
          
          if (!symbol) {
            return {
              content: [{
                type: "text",
                text: "No symbol found at position"
              }]
            };
          }

          const hierarchy = await this.engine.getCallHierarchy(
            symbol.id, args.direction
          );
          
          return {
            content: [{
              type: "text",
              text: JSON.stringify(hierarchy, null, 2)
            }]
          };
        }

        case "find_cross_language_bindings": {
          const bindings = await this.engine.findCrossLanguageBindings(args.file);
          
          return {
            content: [{
              type: "text",
              text: JSON.stringify(bindings, null, 2)
            }]
          };
        }

        case "index_workspace": {
          const path = args.path || this.workspacePath;
          await this.engine.indexWorkspace(path);
          
          return {
            content: [{
              type: "text",
              text: `Workspace indexed: ${path}`
            }]
          };
        }

        default:
          throw new Error(`Unknown tool: ${name}`);
      }
    });
  }

  async start() {
    await this.engine.initialize();
    
    // Auto-index current workspace on startup
    console.log(`Indexing workspace: ${this.workspacePath}`);
    await this.engine.indexWorkspace(this.workspacePath);
    
    const transport = new StdioServerTransport();
    await this.server.connect(transport);
    console.log("Code Intelligence MCP Server running");
  }
}

// Start the server
const server = new CodeIntelligenceMCPServer();
server.start().catch(console.error);
```

### Step 10: Package Configuration

```json
// package.json
{
  "name": "mcp-code-intelligence",
  "version": "1.0.0",
  "description": "Multi-language code intelligence MCP server",
  "main": "src/mcp-server.ts",
  "type": "module",
  "scripts": {
    "start": "bun run src/mcp-server.ts",
    "dev": "bun --watch src/mcp-server.ts",
    "build": "bun build src/mcp-server.ts --compile --target=bun --outfile=code-intelligence-server"
  },
  "dependencies": {
    "@modelcontextprotocol/sdk": "^0.5.0",
    "web-tree-sitter": "^0.20.8",
    "minisearch": "^6.3.0",
    "chokidar": "^3.5.3",
    "tree-sitter-javascript": "^0.20.0",
    "tree-sitter-typescript": "^0.20.0",
    "tree-sitter-python": "^0.20.0",
    "tree-sitter-rust": "^0.20.0",
    "tree-sitter-go": "^0.20.0",
    "tree-sitter-java": "^0.20.0",
    "tree-sitter-c-sharp": "^0.20.0",
    "tree-sitter-c": "^0.20.0",
    "tree-sitter-cpp": "^0.20.0",
    "tree-sitter-ruby": "^0.20.0",
    "tree-sitter-php": "^0.20.0"
  }
}
```

```json
// tsconfig.json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ES2022",
    "lib": ["ES2022"],
    "moduleResolution": "node",
    "types": ["bun-types"],
    "esModuleInterop": true,
    "forceConsistentCasingInFileNames": true,
    "strict": true,
    "skipLibCheck": true,
    "resolveJsonModule": true
  },
  "include": ["src/**/*"],
  "exclude": ["node_modules"]
}
```

## Getting Started

1. **Clone and Install**
```bash
git clone <repo>
cd mcp-code-intelligence
bun install
```

2. **Run the Server**
```bash
bun start
```

3. **Configure MCP Client**
Add to your MCP client configuration:
```json
{
  "mcpServers": {
    "code-intelligence": {
      "command": "bun",
      "args": ["run", "/path/to/mcp-code-intelligence/src/mcp-server.ts"]
    }
  }
}
```

## Performance Optimizations

1. **Batch Processing**: Index files in batches to avoid overwhelming the system
2. **Incremental Updates**: Only reindex changed files
3. **Caching**: Keep frequently accessed symbols in memory
4. **Prepared Statements**: Use prepared statements for all database queries
5. **Debouncing**: Debounce file change events to avoid rapid reindexing
6. **Parallel Processing**: Use Bun's worker threads for CPU-intensive parsing

## Next Steps

1. **Add More Language Extractors**: Implement extractors for Python, Rust, Go, etc.
2. **Enhance Type Inference**: Implement more sophisticated type inference
3. **Add Semantic Analysis**: Use tree-sitter queries for better semantic understanding
4. **Implement Code Actions**: Quick fixes, refactoring suggestions
5. **Add Documentation Generation**: Extract and format documentation
6. **Cross-Language Type Mapping**: Map types between different languages
7. **Performance Monitoring**: Add metrics and profiling
8. **Testing**: Add comprehensive unit and integration tests
9. **Optimize WASM Builds**: Consider using Microsoft's vscode-tree-sitter-wasm for production builds to match VS Code's optimizations

## Architecture Benefits

- **Fast**: Bun runtime + SQLite + MiniSearch = microsecond queries
- **Scalable**: Can handle millions of symbols efficiently
- **Incremental**: Only reindexes changed files
- **Cross-Language**: Unified interface for all languages
- **Extensible**: Easy to add new languages and features
- **No External Dependencies**: Everything runs in-process

This architecture provides LSP-quality features without the overhead of running multiple language servers, making it perfect for multi-language codebases.
