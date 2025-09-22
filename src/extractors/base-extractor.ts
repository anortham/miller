import { Parser } from 'web-tree-sitter';
import { createHash } from 'crypto';
import { log, LogLevel } from '../utils/logger.js';

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
  Struct = 'struct',
  Union = 'union',
  Field = 'field',
  Constructor = 'constructor',
  Destructor = 'destructor',
  Operator = 'operator',
  Import = 'import',
  Export = 'export',
  Event = 'event',
  Delegate = 'delegate'
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
  Instantiates = 'instantiates',
  References = 'references',
  Defines = 'defines',
  Overrides = 'overrides',
  Contains = 'contains',
  Joins = 'joins'
}

export interface TypeInfo {
  symbolId: string;
  resolvedType: string;
  genericParams?: string[];
  constraints?: string[];
  isInferred: boolean;
  language: string;
  metadata?: any;
}

export abstract class BaseExtractor {
  protected symbolMap = new Map<string, Symbol>();
  protected relationships: Relationship[] = [];
  protected typeInfo = new Map<string, TypeInfo>();

  constructor(
    protected language: string,
    protected filePath: string,
    protected content: string
  ) {}

  // Abstract methods that each language extractor must implement
  abstract extractSymbols(tree: Parser.Tree): Symbol[];
  abstract extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[];
  abstract inferTypes(symbols: Symbol[]): Map<string, string>;

  // Utility methods available to all extractors
  protected getNodeText(node: Parser.SyntaxNode): string {
    return this.content.substring(node.startIndex, node.endIndex);
  }

  protected findDocComment(node: Parser.SyntaxNode): string | undefined {
    // Look for comment nodes preceding the current node
    const previousSibling = node.previousNamedSibling;
    if (previousSibling?.type.includes('comment')) {
      return this.getNodeText(previousSibling);
    }

    // Look for JSDoc-style comments above the node
    const parent = node.parent;
    if (parent) {
      for (const child of parent.children) {
        if (child.startPosition.row < node.startPosition.row && child.type.includes('comment')) {
          const commentText = this.getNodeText(child);
          // Check if it's a documentation comment (starts with /** or ///)
          if (commentText.startsWith('/**') || commentText.startsWith('///')) {
            return commentText;
          }
        }
      }
    }

    return undefined;
  }

  protected generateId(name: string, line: number, column: number): string {
    return createHash('md5')
      .update(`${this.filePath}:${name}:${line}:${column}`)
      .digest('hex');
  }

  protected createSymbol(
    node: Parser.SyntaxNode,
    name: string,
    kind: SymbolKind,
    options: {
      signature?: string;
      visibility?: 'public' | 'private' | 'protected';
      parentId?: string;
      metadata?: any;
      docComment?: string;
    } = {}
  ): Symbol {
    const id = this.generateId(name, node.startPosition.row, node.startPosition.column);

    const symbol: Symbol = {
      id,
      name,
      kind,
      language: this.language,
      filePath: this.filePath,
      startLine: node.startPosition.row + 1,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row + 1,
      endColumn: node.endPosition.column,
      startByte: node.startIndex,
      endByte: node.endIndex,
      signature: options.signature,
      docComment: options.docComment ?? this.findDocComment(node),
      visibility: options.visibility,
      parentId: options.parentId,
      metadata: options.metadata
    };

    this.symbolMap.set(id, symbol);
    return symbol;
  }

  protected createRelationship(
    fromSymbolId: string,
    toSymbolId: string,
    kind: RelationshipKind,
    node: Parser.SyntaxNode,
    confidence = 1.0,
    metadata?: any
  ): Relationship {
    return {
      fromSymbolId,
      toSymbolId,
      kind,
      filePath: this.filePath,
      lineNumber: node.startPosition.row + 1,
      confidence,
      metadata
    };
  }

  protected findContainingSymbol(node: Parser.SyntaxNode, symbols: Symbol[]): Symbol | undefined {
    const position = node.startPosition;

    // Find symbols that contain this position
    // Note: symbols store 1-based line numbers and 0-based column numbers
    const containingSymbols = symbols.filter(s => {
      const lineContains = s.startLine <= position.row + 1 && s.endLine >= position.row + 1;

      // For column containment, we need to be careful about multi-line spans
      let colContains = false;
      if (position.row + 1 === s.startLine && position.row + 1 === s.endLine) {
        // Single line span
        colContains = s.startColumn <= position.column && s.endColumn >= position.column;
      } else if (position.row + 1 === s.startLine) {
        // First line of multi-line span
        colContains = s.startColumn <= position.column;
      } else if (position.row + 1 === s.endLine) {
        // Last line of multi-line span
        colContains = s.endColumn >= position.column;
      } else {
        // Middle line of multi-line span
        colContains = true;
      }

      return lineContains && colContains;
    });

    if (containingSymbols.length === 0) return undefined;

    // Prefer functions, methods, classes over variables when finding the containing symbol
    const priorityOrder = {
      [SymbolKind.Function]: 1,
      [SymbolKind.Method]: 1,
      [SymbolKind.Constructor]: 1,
      [SymbolKind.Class]: 2,
      [SymbolKind.Interface]: 2,
      [SymbolKind.Namespace]: 3,
      [SymbolKind.Variable]: 10,
      [SymbolKind.Constant]: 10,
      [SymbolKind.Property]: 10
    };

    return containingSymbols.sort((a, b) => {
      // First, sort by priority (functions first)
      const priorityA = priorityOrder[a.kind] || 5;
      const priorityB = priorityOrder[b.kind] || 5;
      if (priorityA !== priorityB) return priorityA - priorityB;

      // Then by size (smaller first)
      const sizeA = (a.endLine - a.startLine) * 1000 + (a.endColumn - a.startColumn);
      const sizeB = (b.endLine - b.startLine) * 1000 + (b.endColumn - b.startColumn);
      return sizeA - sizeB;
    })[0];
  }

  protected extractVisibility(node: Parser.SyntaxNode): 'public' | 'private' | 'protected' | undefined {
    // Look for visibility modifiers in child nodes
    for (const child of node.children) {
      if (['public', 'private', 'protected'].includes(child.type)) {
        return child.type as 'public' | 'private' | 'protected';
      }
    }

    // Check for language-specific patterns
    const text = this.getNodeText(node);
    if (text.includes('public ')) return 'public';
    if (text.includes('private ')) return 'private';
    if (text.includes('protected ')) return 'protected';

    return undefined;
  }

  protected extractIdentifierName(node: Parser.SyntaxNode): string {
    // Try to find the identifier node
    const identifierNode = node.childForFieldName('name') ||
                          node.child(0) ||
                          node;

    if (identifierNode?.type === 'identifier') {
      return this.getNodeText(identifierNode);
    }

    // Fallback: extract from the node text
    const text = this.getNodeText(node).trim();
    const match = text.match(/^[a-zA-Z_$][a-zA-Z0-9_$]*/);
    return match ? match[0] : 'Anonymous';
  }

  protected walkTree(
    node: Parser.SyntaxNode,
    visitor: (node: Parser.SyntaxNode, depth: number) => void,
    depth = 0
  ): void {
    visitor(node, depth);

    for (const child of node.children) {
      this.walkTree(child, visitor, depth + 1);
    }
  }

  protected findNodesByType(node: Parser.SyntaxNode, type: string): Parser.SyntaxNode[] {
    const nodes: Parser.SyntaxNode[] = [];

    this.walkTree(node, (n) => {
      if (n.type === type) {
        nodes.push(n);
      }
    });

    return nodes;
  }

  protected findParentOfType(node: Parser.SyntaxNode, type: string): Parser.SyntaxNode | null {
    let current = node.parent;
    while (current) {
      if (current.type === type) {
        return current;
      }
      current = current.parent;
    }
    return null;
  }

  // Method to check if a node has an error
  protected hasError(node: Parser.SyntaxNode): boolean {
    return node.hasError || node.type === 'ERROR';
  }

  // Method to get all child nodes of a specific type
  protected getChildrenOfType(node: Parser.SyntaxNode, type: string): Parser.SyntaxNode[] {
    return node.children.filter(child => child.type === type);
  }

  // Method to safely get text from a field
  protected getFieldText(node: Parser.SyntaxNode, fieldName: string): string | undefined {
    const fieldNode = node.childForFieldName(fieldName);
    return fieldNode ? this.getNodeText(fieldNode) : undefined;
  }

  // Clean up method
  reset(): void {
    this.symbolMap.clear();
    this.relationships = [];
    this.typeInfo.clear();
  }

  // Tree traversal helper - standardized across all extractors
  protected traverseTree(node: Parser.SyntaxNode, callback: (node: Parser.SyntaxNode) => void): void {
    try {
      callback(node);
    } catch (error) {
      log.extractor(LogLevel.WARN, `Error processing node ${node.type}:`, error);
    }

    // Recursively traverse children
    if (node.children && node.children.length > 0) {
      for (const child of node.children) {
        try {
          this.traverseTree(child, callback);
        } catch (error) {
          // Skip problematic child nodes
          continue;
        }
      }
    }
  }

  // Find first child node by type
  protected findChildByType(node: Parser.SyntaxNode, type: string): Parser.SyntaxNode | null {
    for (const child of node.children) {
      if (child.type === type) {
        return child;
      }
    }
    return null;
  }

  // Find all child nodes by type
  protected findChildrenByType(node: Parser.SyntaxNode, type: string): Parser.SyntaxNode[] {
    const results: Parser.SyntaxNode[] = [];
    for (const child of node.children) {
      if (child.type === type) {
        results.push(child);
      }
    }
    return results;
  }

  // Find child node by multiple possible types
  protected findChildByTypes(node: Parser.SyntaxNode, types: string[]): Parser.SyntaxNode | null {
    for (const child of node.children) {
      if (types.includes(child.type)) {
        return child;
      }
    }
    return null;
  }

  // Alias for findDocComment - used by newer extractors for consistency
  protected extractDocumentation(node: Parser.SyntaxNode): string | undefined {
    return this.findDocComment(node);
  }

  // Get extraction results
  getResults() {
    return {
      symbols: Array.from(this.symbolMap.values()),
      relationships: this.relationships,
      types: this.typeInfo
    };
  }
}