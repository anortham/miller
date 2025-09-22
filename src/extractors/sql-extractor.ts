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
          case 'create_table':
            console.log('DEBUG: Found create_table node');
            symbol = this.extractTableDefinition(node, parentId);
            console.log('DEBUG: extractTableDefinition returned:', symbol?.name);
            break;
          case 'create_procedure':
          case 'create_function':
            symbol = this.extractStoredProcedure(node, parentId);
            break;
          case 'create_view':
            symbol = this.extractView(node, parentId);
            break;
          case 'create_index':
            symbol = this.extractIndex(node, parentId);
            break;
          case 'create_trigger':
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
        // Extract columns and constraints for tables
        if (node.type === 'create_table') {
          this.extractTableColumns(node, symbols, symbol.id);
          this.extractTableConstraints(node, symbols, symbol.id);
        }
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
    // Look for table name inside object_reference node
    const objectRefNode = this.findChildByType(node, 'object_reference');
    const tableNameNode = objectRefNode ?
      this.findChildByType(objectRefNode, 'identifier') :
      (this.findChildByType(node, 'identifier') || this.findChildByType(node, 'table_name'));

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

        // Find SQL data type nodes (bigint, varchar, etc.)
        const dataTypeNode = this.findChildByType(node, 'data_type') ||
                            this.findChildByType(node, 'type_name') ||
                            this.findChildByType(node, 'bigint') ||
                            this.findChildByType(node, 'varchar') ||
                            this.findChildByType(node, 'int') ||
                            this.findChildByType(node, 'text') ||
                            this.findChildByType(node, 'char') ||
                            this.findChildByType(node, 'decimal') ||
                            this.findChildByType(node, 'boolean') ||
                            this.findChildByType(node, 'keyword_boolean') ||
                            this.findChildByType(node, 'keyword_bigint') ||
                            this.findChildByType(node, 'keyword_varchar') ||
                            this.findChildByType(node, 'keyword_int') ||
                            this.findChildByType(node, 'keyword_text') ||
                            this.findChildByType(node, 'keyword_json') ||
                            this.findChildByType(node, 'json') ||
                            this.findChildByType(node, 'keyword_jsonb') ||
                            this.findChildByType(node, 'jsonb') ||
                            this.findChildByType(node, 'date') ||
                            this.findChildByType(node, 'timestamp');
        const dataType = dataTypeNode ? this.getNodeText(dataTypeNode) : 'unknown';

        const columnSymbol: Symbol = {
          id: this.generateId(`${columnName}_col`, node.startPosition),
          name: columnName,
          kind: SymbolKind.Field, // Columns are fields within the table
          signature: `${dataType}${this.extractColumnConstraints(node)}`,
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

  private extractTableConstraints(tableNode: Parser.SyntaxNode, symbols: Symbol[], parentTableId: string): void {
    this.traverseTree(tableNode, (node) => {
      if (node.type === 'constraint') {
        let constraintSymbol: Symbol | null = null;
        let constraintType = 'unknown';
        let constraintName = `constraint_${node.startPosition.row}`;

        // Determine constraint type based on child nodes
        const hasCheck = this.findChildByType(node, 'keyword_check');
        const hasPrimary = this.findChildByType(node, 'keyword_primary');
        const hasForeign = this.findChildByType(node, 'keyword_foreign');
        const hasUnique = this.findChildByType(node, 'keyword_unique');
        const hasIndex = this.findChildByType(node, 'keyword_index');
        const namedConstraint = this.findChildByType(node, 'identifier');

        if (namedConstraint) {
          constraintName = this.getNodeText(namedConstraint);
        }

        if (hasCheck) {
          constraintType = 'check';
        } else if (hasPrimary) {
          constraintType = 'primary_key';
        } else if (hasForeign) {
          constraintType = 'foreign_key';
        } else if (hasUnique) {
          constraintType = 'unique';
        } else if (hasIndex) {
          constraintType = 'index';
        }

        constraintSymbol = this.createConstraintSymbol(node, constraintType, parentTableId, constraintName);

        if (constraintSymbol) {
          symbols.push(constraintSymbol);
        }
      }
    });
  }

  private createConstraintSymbol(node: Parser.SyntaxNode, constraintType: string, parentTableId: string, constraintName?: string): Symbol {
    const name = constraintName || `${constraintType}_constraint_${node.startPosition.row}`;

    return {
      id: this.generateId(name, node.startPosition),
      name: name,
      kind: SymbolKind.Interface, // Constraints as Interface symbols
      signature: constraintType === 'index' ? `INDEX ${name}` : `CONSTRAINT ${constraintType.toUpperCase()}`,
      startLine: node.startPosition.row,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row,
      endColumn: node.endPosition.column,
      filePath: this.filePath,
      language: this.language,
      parentId: parentTableId,
      visibility: 'public',
      metadata: { isConstraint: true, constraintType }
    };
  }

  private extractColumnConstraints(columnNode: Parser.SyntaxNode): string {
    const constraints: string[] = [];

    // Check for PRIMARY KEY (keyword_primary + keyword_key)
    let hasPrimary = false;
    let hasKey = false;

    this.traverseTree(columnNode, (node) => {
      switch (node.type) {
        case 'primary_key_constraint':
        case 'primary_key':
          constraints.push('PRIMARY KEY');
          break;
        case 'keyword_primary':
          hasPrimary = true;
          break;
        case 'keyword_key':
          hasKey = true;
          break;
        case 'foreign_key_constraint':
        case 'foreign_key':
          constraints.push('FOREIGN KEY');
          break;
        case 'not_null_constraint':
        case 'not_null':
          constraints.push('NOT NULL');
          break;
        case 'keyword_not':
          // Check if followed by keyword_null
          if (node.nextSibling && node.nextSibling.type === 'keyword_null') {
            constraints.push('NOT NULL');
          }
          break;
        case 'keyword_unique':
          constraints.push('UNIQUE');
          break;
        case 'unique_constraint':
        case 'unique':
          constraints.push('UNIQUE');
          break;
        case 'check_constraint':
          constraints.push('CHECK');
          break;
        case 'keyword_default':
          // Find the default value (next sibling or following nodes)
          if (node.nextSibling) {
            const defaultValue = node.nextSibling.text;
            constraints.push(`DEFAULT ${defaultValue}`);
          }
          break;
      }
    });

    // Add PRIMARY KEY if both keywords found
    if (hasPrimary && hasKey) {
      constraints.push('PRIMARY KEY');
    }

    return constraints.length > 0 ? ` ${constraints.join(' ')}` : '';
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
    // Look for table name inside object_reference node (same as extractTableDefinition)
    const objectRefNode = this.findChildByType(node, 'object_reference');
    const nameNode = objectRefNode ?
      this.findChildByType(objectRefNode, 'identifier') :
      (this.findChildByType(node, 'identifier') || this.findChildByType(node, 'table_name'));
    const tableName = nameNode ? this.getNodeText(nameNode) : 'unknown';

    // Count columns for a brief signature
    let columnCount = 0;
    this.traverseTree(node, (childNode) => {
      if (childNode.type === 'column_definition') {
        columnCount++;
      }
    });

    return `CREATE TABLE ${tableName} (${columnCount} columns)`;
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
          case 'constraint':
            // Check if this is a foreign key constraint
            const hasForeign = this.findChildByType(node, 'keyword_foreign');
            if (hasForeign) {
              this.extractForeignKeyRelationship(node, symbols, relationships);
            }
            break;
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
    // Look for object_reference after keyword_references
    const referencesKeyword = this.findChildByType(node, 'keyword_references');
    if (!referencesKeyword) return;

    const objectRefNode = this.findChildByType(node, 'object_reference');
    const referencedTableNode = objectRefNode ?
      this.findChildByType(objectRefNode, 'identifier') :
      (this.findChildByType(node, 'table_name') || this.findChildByType(node, 'identifier'));

    if (!referencedTableNode) return;

    const referencedTable = this.getNodeText(referencedTableNode);

    // Find the source table (parent of this foreign key)
    let currentNode = node.parent;
    while (currentNode && currentNode.type !== 'create_table') {
      currentNode = currentNode.parent;
    }

    if (!currentNode) return;

    // Look for table name in object_reference (same pattern as extractTableDefinition)
    const sourceObjectRefNode = this.findChildByType(currentNode, 'object_reference');
    const sourceTableNode = sourceObjectRefNode ?
      this.findChildByType(sourceObjectRefNode, 'identifier') :
      (this.findChildByType(currentNode, 'identifier') || this.findChildByType(currentNode, 'table_name'));

    if (!sourceTableNode) return;

    const sourceTable = this.getNodeText(sourceTableNode);

    // Find corresponding symbols
    const sourceSymbol = symbols.find(s => s.name === sourceTable && s.kind === SymbolKind.Class);
    const targetSymbol = symbols.find(s => s.name === referencedTable && s.kind === SymbolKind.Class);

    // Create relationship if we have at least the source symbol
    // Target symbol might not exist if referencing external table
    if (sourceSymbol) {
      relationships.push({
        fromSymbolId: sourceSymbol.id,
        toSymbolId: targetSymbol?.id || `external_${referencedTable}`,
        kind: RelationshipKind.References, // Foreign key reference
        filePath: this.filePath,
        lineNumber: node.startPosition.row,
        confidence: targetSymbol ? 1.0 : 0.8, // Lower confidence for external references
        metadata: {
          targetTable: referencedTable,
          sourceTable: sourceTable,
          relationshipType: 'foreign_key',
          isExternal: !targetSymbol
        }
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
          types.set(symbol.id, typeMatch[0].toUpperCase());
        }
      }

      // Use metadata for SQL-specific types
      if (symbol.metadata?.isTable) {
        types.set(symbol.id, 'TABLE');
      }
      if (symbol.metadata?.isView) {
        types.set(symbol.id, 'VIEW');
      }
      if (symbol.metadata?.isStoredProcedure) {
        types.set(symbol.id, 'PROCEDURE');
      }
    }

    return types;
  }
}