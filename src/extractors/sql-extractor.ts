import { Parser } from 'web-tree-sitter';
import { BaseExtractor, Symbol, Relationship, SymbolKind, RelationshipKind } from './base-extractor.js';

/**
 * SQL language extractor that handles SQL-specific constructs for cross-language tracing:
 * - Table definitions (CREATE TABLE)
 * - Column definitions and constraints
 * - Stored procedures and functions
 * - Views and triggers
 * - Indexes and foreign keys
 * - Query patterns and table references
 *
 * This enables full-stack symbol tracing from frontend → API → database schema.
 */
export class SqlExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];

    const visitNode = (node: Parser.SyntaxNode, parentId?: string) => {
      if (!node || !node.type) {
        return; // Skip invalid nodes
      }

      let symbol: Symbol | null = null;

      try {
        switch (node.type) {
          case 'create_table_statement':
            symbol = this.extractTableDefinition(node, parentId);
            break;
          case 'create_procedure_statement':
          case 'create_function_statement':
            symbol = this.extractStoredProcedure(node, parentId);
            break;
          case 'create_view_statement':
            symbol = this.extractView(node, parentId);
            break;
          case 'create_index_statement':
            symbol = this.extractIndex(node, parentId);
            break;
          case 'create_trigger_statement':
            symbol = this.extractTrigger(node, parentId);
            break;
          default:
            // Handle other SQL constructs
            break;
        }
      } catch (error) {
        console.warn(`Error extracting SQL symbol from ${node.type}:`, error);
      }

      if (symbol) {
        symbols.push(symbol);
        parentId = symbol.id;
      }

      // Recursively visit children
      if (node.children && node.children.length > 0) {
        for (const child of node.children) {
          try {
            visitNode(child, parentId);
          } catch (error) {
            // Skip problematic child nodes
            continue;
          }
        }
      }
    };

    try {
      visitNode(tree.rootNode);
    } catch (error) {
      console.warn('SQL parsing failed:', error);
    }

    return symbols;
  }

  private extractTableDefinition(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const tableNameNode = this.findChildByType(node, 'identifier') ||
                         this.findChildByType(node, 'table_name');

    if (!tableNameNode) return null;

    const tableName = this.getNodeText(tableNameNode);

    const tableSymbol = this.createSymbol(node, tableName, SymbolKind.Class, {
      signature: this.extractTableSignature(node),
      visibility: 'public',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isTable: true }
    });

    return tableSymbol;
  }

  private extractTableColumns(tableNode: Parser.SyntaxNode, symbols: Symbol[], parentTableId: string): void {
    this.traverseTree(tableNode, (node) => {
      if (node.type === 'column_definition') {
        const columnNameNode = this.findChildByType(node, 'identifier') ||
                              this.findChildByType(node, 'column_name');

        if (!columnNameNode) return;

        const columnName = this.getNodeText(columnNameNode);
        const dataTypeNode = this.findChildByType(node, 'data_type') ||
                            this.findChildByType(node, 'type_name');
        const dataType = dataTypeNode ? this.getNodeText(dataTypeNode) : 'unknown';

        const columnSymbol: Symbol = {
          id: this.generateId(`${columnName}_col`, node.startPosition),
          name: columnName,
          kind: SymbolKind.Property, // Columns are like object properties
          signature: `${columnName}: ${dataType}${this.extractColumnConstraints(node)}`,
          startLine: node.startPosition.row,
          startColumn: node.startPosition.column,
          endLine: node.endPosition.row,
          endColumn: node.endPosition.column,
          filePath: this.filePath,
          language: this.language,
          parentId: parentTableId,
          visibility: 'public'
        };

        symbols.push(columnSymbol);
      }
    });
  }

  private extractColumnConstraints(columnNode: Parser.SyntaxNode): string {
    const constraints: string[] = [];

    this.traverseTree(columnNode, (node) => {
      switch (node.type) {
        case 'primary_key_constraint':
        case 'primary_key':
          constraints.push('PRIMARY KEY');
          break;
        case 'foreign_key_constraint':
        case 'foreign_key':
          constraints.push('FOREIGN KEY');
          break;
        case 'not_null_constraint':
        case 'not_null':
          constraints.push('NOT NULL');
          break;
        case 'unique_constraint':
        case 'unique':
          constraints.push('UNIQUE');
          break;
        case 'check_constraint':
          constraints.push('CHECK');
          break;
      }
    });

    return constraints.length > 0 ? ` (${constraints.join(', ')})` : '';
  }

  private extractStoredProcedure(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier') ||
                    this.findChildByType(node, 'procedure_name') ||
                    this.findChildByType(node, 'function_name');

    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const isFunction = node.type.includes('function');

    const symbol = this.createSymbol(node, name,
      isFunction ? SymbolKind.Function : SymbolKind.Method, {
      signature: this.extractProcedureSignature(node),
      visibility: 'public',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isFunction, isStoredProcedure: true }
    });

    return symbol;
  }

  private extractProcedureParameters(procNode: Parser.SyntaxNode, symbols: Symbol[], parentId: string): void {
    this.traverseTree(procNode, (node) => {
      if (node.type === 'parameter_declaration' || node.type === 'parameter') {
        const paramNameNode = this.findChildByType(node, 'identifier') ||
                             this.findChildByType(node, 'parameter_name');

        if (!paramNameNode) return;

        const paramName = this.getNodeText(paramNameNode);
        const typeNode = this.findChildByType(node, 'data_type') ||
                        this.findChildByType(node, 'type_name');
        const paramType = typeNode ? this.getNodeText(typeNode) : 'unknown';

        const paramSymbol: Symbol = {
          id: this.generateId(`${paramName}_param`, node.startPosition),
          name: paramName,
          kind: SymbolKind.Variable,
          signature: `${paramName}: ${paramType}`,
          startLine: node.startPosition.row,
          startColumn: node.startPosition.column,
          endLine: node.endPosition.row,
          endColumn: node.endPosition.column,
          filePath: this.filePath,
          language: this.language,
          parentId,
          visibility: 'public'
        };

        symbols.push(paramSymbol);
      }
    });
  }

  private extractView(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier') ||
                    this.findChildByType(node, 'view_name');

    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    const symbol = this.createSymbol(node, name, SymbolKind.Interface, {
      signature: `VIEW ${name}`,
      visibility: 'public',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isView: true }
    });

    return symbol;
  }

  private extractIndex(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier') ||
                    this.findChildByType(node, 'index_name');

    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    const symbol = this.createSymbol(node, name, SymbolKind.Property, {
      signature: `INDEX ${name}`,
      visibility: 'public',
      parentId,
      metadata: { isIndex: true }
    });

    return symbol;
  }

  private extractTrigger(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier') ||
                    this.findChildByType(node, 'trigger_name');

    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    const symbol = this.createSymbol(node, name, SymbolKind.Method, {
      signature: `TRIGGER ${name}`,
      visibility: 'public',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isTrigger: true }
    });

    return symbol;
  }

  private extractTableSignature(node: Parser.SyntaxNode): string {
    const nameNode = this.findChildByType(node, 'identifier') ||
                    this.findChildByType(node, 'table_name');
    const tableName = nameNode ? this.getNodeText(nameNode) : 'unknown';

    // Count columns for a brief signature
    let columnCount = 0;
    this.traverseTree(node, (childNode) => {
      if (childNode.type === 'column_definition') {
        columnCount++;
      }
    });

    return `TABLE ${tableName} (${columnCount} columns)`;
  }

  private extractProcedureSignature(node: Parser.SyntaxNode): string {
    const nameNode = this.findChildByType(node, 'identifier') ||
                    this.findChildByType(node, 'procedure_name') ||
                    this.findChildByType(node, 'function_name');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknown';

    // Extract parameter list
    const params: string[] = [];
    this.traverseTree(node, (childNode) => {
      if (childNode.type === 'parameter_declaration' || childNode.type === 'parameter') {
        const paramNameNode = this.findChildByType(childNode, 'identifier');
        const typeNode = this.findChildByType(childNode, 'data_type') ||
                        this.findChildByType(childNode, 'type_name');

        if (paramNameNode) {
          const paramName = this.getNodeText(paramNameNode);
          const paramType = typeNode ? this.getNodeText(typeNode) : '';
          params.push(paramType ? `${paramName}: ${paramType}` : paramName);
        }
      }
    });

    const isFunction = node.type.includes('function');
    const keyword = isFunction ? 'FUNCTION' : 'PROCEDURE';

    return `${keyword} ${name}(${params.join(', ')})`;
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];

    this.traverseTree(tree.rootNode, (node) => {
      try {
        switch (node.type) {
          case 'foreign_key_constraint':
          case 'references_clause':
            this.extractForeignKeyRelationship(node, symbols, relationships);
            break;
          case 'select_statement':
          case 'from_clause':
            this.extractTableReferences(node, symbols, relationships);
            break;
        }
      } catch (error) {
        console.warn(`Error extracting SQL relationship from ${node.type}:`, error);
      }
    });

    return relationships;
  }

  private extractForeignKeyRelationship(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    // Extract foreign key relationships between tables
    const referencedTableNode = this.findChildByType(node, 'table_name') ||
                               this.findChildByType(node, 'identifier');

    if (!referencedTableNode) return;

    const referencedTable = this.getNodeText(referencedTableNode);

    // Find the source table (parent of this foreign key)
    let currentNode = node.parent;
    while (currentNode && currentNode.type !== 'create_table_statement') {
      currentNode = currentNode.parent;
    }

    if (!currentNode) return;

    const sourceTableNode = this.findChildByType(currentNode, 'identifier') ||
                           this.findChildByType(currentNode, 'table_name');

    if (!sourceTableNode) return;

    const sourceTable = this.getNodeText(sourceTableNode);

    // Find corresponding symbols
    const sourceSymbol = symbols.find(s => s.name === sourceTable && s.kind === SymbolKind.Class);
    const targetSymbol = symbols.find(s => s.name === referencedTable && s.kind === SymbolKind.Class);

    if (sourceSymbol && targetSymbol) {
      relationships.push({
        id: this.generateId(`fk_${sourceTable}_${referencedTable}`, node.startPosition),
        sourceId: sourceSymbol.id,
        targetId: targetSymbol.id,
        kind: RelationshipKind.References, // Foreign key reference
        filePath: this.filePath,
        startLine: node.startPosition.row,
        startColumn: node.startPosition.column,
        endLine: node.endPosition.row,
        endColumn: node.endPosition.column
      });
    }
  }

  private extractTableReferences(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    // Extract table references in SELECT statements for query analysis
    this.traverseTree(node, (childNode) => {
      if (childNode.type === 'table_name' ||
          (childNode.type === 'identifier' && childNode.parent?.type === 'from_clause')) {

        const tableName = this.getNodeText(childNode);
        const tableSymbol = symbols.find(s => s.name === tableName && s.kind === SymbolKind.Class);

        if (tableSymbol) {
          // This represents a query dependency - the query uses this table
          // We could create a relationship to track which queries use which tables
        }
      }
    });
  }

  extractTypes(tree: Parser.Tree): Map<string, string> {
    const types = new Map<string, string>();

    this.traverseTree(tree.rootNode, (node) => {
      if (node.type === 'column_definition') {
        const nameNode = this.findChildByType(node, 'identifier') ||
                        this.findChildByType(node, 'column_name');
        const typeNode = this.findChildByType(node, 'data_type') ||
                        this.findChildByType(node, 'type_name');

        if (nameNode && typeNode) {
          const columnName = this.getNodeText(nameNode);
          const dataType = this.getNodeText(typeNode);
          types.set(columnName, dataType);
        }
      }
    });

    return types;
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();

    // SQL type inference based on symbol metadata and signatures
    for (const symbol of symbols) {
      if (symbol.signature) {
        // Extract SQL data types from signatures like "CREATE TABLE users (id INT, name VARCHAR(100))"
        const sqlTypePattern = /\b(INT|INTEGER|VARCHAR|TEXT|DECIMAL|FLOAT|BOOLEAN|DATE|TIMESTAMP|CHAR|BIGINT|SMALLINT)\b/gi;
        const typeMatch = symbol.signature.match(sqlTypePattern);
        if (typeMatch) {
          types.set(symbol.name, typeMatch[0].toUpperCase());
        }
      }

      // Use metadata for SQL-specific types
      if (symbol.metadata?.isTable) {
        types.set(symbol.name, 'TABLE');
      }
      if (symbol.metadata?.isView) {
        types.set(symbol.name, 'VIEW');
      }
      if (symbol.metadata?.isStoredProcedure) {
        types.set(symbol.name, 'PROCEDURE');
      }
    }

    return types;
  }
}