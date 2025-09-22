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
          case 'create_function_statement':
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
          case 'cte':
            // Extract Common Table Expressions as functions/views
            symbol = this.extractCte(node, parentId);
            break;
          case 'create_schema':
            symbol = this.extractSchema(node, parentId);
            break;
          case 'create_sequence':
            symbol = this.extractSequence(node, parentId);
            break;
          case 'create_domain':
            symbol = this.extractDomain(node, parentId);
            break;
          case 'create_type':
            symbol = this.extractType(node, parentId);
            break;
          case 'alter_table':
            this.extractConstraintsFromAlterTable(node, symbols, parentId);
            break;
          case 'select':
            // Extract SELECT query aliases as fields
            this.extractSelectAliases(node, symbols, parentId);
            break;
          case 'ERROR':
            // Handle DELIMITER syntax issues - extract multiple symbols from ERROR nodes
            this.extractMultipleFromErrorNode(node, symbols, parentId);
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
        // Extract view column aliases
        if (node.type === 'create_view') {
          this.extractViewColumns(node, symbols, symbol.id);
        }
        // Extract view columns from ERROR nodes that contain views
        if (node.type === 'ERROR' && symbol.metadata?.isView) {
          this.extractViewColumnsFromErrorNode(node, symbols, symbol.id);
        }
        // Extract parameters for procedures/functions from ERROR nodes
        if (node.type === 'ERROR' && (symbol.metadata?.isStoredProcedure || symbol.metadata?.isFunction)) {
          this.extractParametersFromErrorNode(node, symbols, symbol.id);
        }
        // Extract DECLARE variables from function bodies
        if ((node.type === 'create_function' || node.type === 'create_function_statement') && symbol) {
          this.extractDeclareVariables(node, symbols, symbol.id);
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
    // Look for function/procedure name - it may be inside an object_reference
    const objectRefNode = this.findChildByType(node, 'object_reference');
    const nameNode = objectRefNode ?
      this.findChildByType(objectRefNode, 'identifier') :
      (this.findChildByType(node, 'identifier') ||
       this.findChildByType(node, 'procedure_name') ||
       this.findChildByType(node, 'function_name'));

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
    // Look for view name - it may be inside an object_reference
    const objectRefNode = this.findChildByType(node, 'object_reference');
    const nameNode = objectRefNode ?
      this.findChildByType(objectRefNode, 'identifier') :
      (this.findChildByType(node, 'identifier') ||
       this.findChildByType(node, 'view_name'));

    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    const symbol = this.createSymbol(node, name, SymbolKind.Interface, {
      signature: `CREATE VIEW ${name}`,
      visibility: 'public',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isView: true }
    });

    return symbol;
  }

  private extractSchema(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Look for schema name - it may be inside an object_reference
    const objectRefNode = this.findChildByType(node, 'object_reference');
    const nameNode = objectRefNode ?
      this.findChildByType(objectRefNode, 'identifier') :
      (this.findChildByType(node, 'identifier') ||
       this.findChildByType(node, 'schema_name'));

    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    const symbol = this.createSymbol(node, name, SymbolKind.Namespace, {
      signature: `CREATE SCHEMA ${name}`,
      visibility: 'public',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isSchema: true }
    });

    return symbol;
  }

  private extractSequence(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Look for sequence name - it may be inside an object_reference
    const objectRefNode = this.findChildByType(node, 'object_reference');
    const nameNode = objectRefNode ?
      this.findChildByType(objectRefNode, 'identifier') :
      (this.findChildByType(node, 'identifier') ||
       this.findChildByType(node, 'sequence_name'));

    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Build sequence signature with options
    const nodeText = this.getNodeText(node);
    let signature = `CREATE SEQUENCE ${name}`;

    // Add sequence options if present
    const options = [];
    if (nodeText.includes('START WITH')) {
      const startMatch = nodeText.match(/START\s+WITH\s+(\d+)/i);
      if (startMatch) options.push(`START WITH ${startMatch[1]}`);
    }
    if (nodeText.includes('INCREMENT BY')) {
      const incMatch = nodeText.match(/INCREMENT\s+BY\s+(\d+)/i);
      if (incMatch) options.push(`INCREMENT BY ${incMatch[1]}`);
    }
    if (nodeText.includes('MINVALUE')) {
      const minMatch = nodeText.match(/MINVALUE\s+(\d+)/i);
      if (minMatch) options.push(`MINVALUE ${minMatch[1]}`);
    }
    if (nodeText.includes('MAXVALUE')) {
      const maxMatch = nodeText.match(/MAXVALUE\s+(\d+)/i);
      if (maxMatch) options.push(`MAXVALUE ${maxMatch[1]}`);
    }
    if (nodeText.includes('CACHE')) {
      const cacheMatch = nodeText.match(/CACHE\s+(\d+)/i);
      if (cacheMatch) options.push(`CACHE ${cacheMatch[1]}`);
    }

    if (options.length > 0) {
      signature += ` (${options.join(', ')})`;
    }

    const symbol = this.createSymbol(node, name, SymbolKind.Variable, {
      signature,
      visibility: 'public',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isSequence: true }
    });

    return symbol;
  }

  private extractDomain(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Look for domain name - it may be inside an object_reference
    const objectRefNode = this.findChildByType(node, 'object_reference');
    const nameNode = objectRefNode ?
      this.findChildByType(objectRefNode, 'identifier') :
      (this.findChildByType(node, 'identifier') ||
       this.findChildByType(node, 'domain_name'));

    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Build domain signature with base type and constraints
    const nodeText = this.getNodeText(node);
    let signature = `CREATE DOMAIN ${name}`;

    // Extract the base type (AS datatype)
    const asMatch = nodeText.match(/AS\s+([A-Z]+(?:\(\d+(?:,\s*\d+)?\))?)/i);
    if (asMatch) {
      signature += ` AS ${asMatch[1]}`;
    }

    // Add CHECK constraint if present
    const checkMatch = nodeText.match(/CHECK\s*\(([^)]+(?:\([^)]*\)[^)]*)*)\)/i);
    if (checkMatch) {
      signature += ` CHECK (${checkMatch[1].trim()})`;
    }

    const symbol = this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      visibility: 'public',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isDomain: true }
    });

    return symbol;
  }

  private extractType(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Look for type name in object_reference
    const objectRefNode = this.findChildByType(node, 'object_reference');
    const nameNode = objectRefNode ?
      this.findChildByType(objectRefNode, 'identifier') :
      this.findChildByType(node, 'identifier');

    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Check if this is an ENUM type
    const nodeText = this.getNodeText(node);
    if (nodeText.includes('AS ENUM')) {
      // Extract enum values from enum_elements
      const enumElementsNode = this.findChildByType(node, 'enum_elements');
      let enumValues = '';
      if (enumElementsNode) {
        enumValues = this.getNodeText(enumElementsNode);
      }

      const signature = `CREATE TYPE ${name} AS ENUM ${enumValues}`;

      const symbol = this.createSymbol(node, name, SymbolKind.Class, {
        signature,
        visibility: 'public',
        parentId,
        docComment: this.extractDocumentation(node),
        metadata: { isEnum: true, isType: true }
      });

      return symbol;
    }

    // Handle other types (non-enum)
    const signature = `CREATE TYPE ${name}`;
    const symbol = this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      visibility: 'public',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isType: true }
    });

    return symbol;
  }

  private extractConstraintsFromAlterTable(node: Parser.SyntaxNode, symbols: Symbol[], parentId?: string): void {
    const nodeText = this.getNodeText(node);

    // Extract ADD CONSTRAINT statements
    const constraintMatch = nodeText.match(/ADD\s+CONSTRAINT\s+([a-zA-Z_][a-zA-Z0-9_]*)\s+(CHECK|FOREIGN\s+KEY|UNIQUE|PRIMARY\s+KEY)/i);
    if (constraintMatch) {
      const constraintName = constraintMatch[1];
      const constraintType = constraintMatch[2].toUpperCase();

      let signature = `ALTER TABLE ADD CONSTRAINT ${constraintName} ${constraintType}`;

      // Add more details based on constraint type
      if (constraintType === 'CHECK') {
        const checkMatch = nodeText.match(/CHECK\s*\(([^)]+(?:\([^)]*\)[^)]*)*)\)/i);
        if (checkMatch) {
          signature += ` (${checkMatch[1].trim()})`;
        }
      } else if (constraintType.includes('FOREIGN')) {
        const fkMatch = nodeText.match(/FOREIGN\s+KEY\s*\(([^)]+)\)\s*REFERENCES\s+([a-zA-Z_][a-zA-Z0-9_]*)/i);
        if (fkMatch) {
          signature += ` (${fkMatch[1]}) REFERENCES ${fkMatch[2]}`;
        }

        // Add ON DELETE/UPDATE actions
        const onDeleteMatch = nodeText.match(/ON\s+DELETE\s+(CASCADE|RESTRICT|SET\s+NULL|NO\s+ACTION)/i);
        if (onDeleteMatch) {
          signature += ` ON DELETE ${onDeleteMatch[1].toUpperCase()}`;
        }

        const onUpdateMatch = nodeText.match(/ON\s+UPDATE\s+(CASCADE|RESTRICT|SET\s+NULL|NO\s+ACTION)/i);
        if (onUpdateMatch) {
          signature += ` ON UPDATE ${onUpdateMatch[1].toUpperCase()}`;
        }
      }

      const constraintSymbol: Symbol = {
        id: this.generateId(`${constraintName}_constraint`, node.startPosition),
        name: constraintName,
        kind: SymbolKind.Property,
        signature,
        startLine: node.startPosition.row,
        startColumn: node.startPosition.column,
        endLine: node.endPosition.row,
        endColumn: node.endPosition.column,
        filePath: this.filePath,
        language: this.language,
        parentId,
        visibility: 'public',
        metadata: { isConstraint: true, constraintType }
      };

      symbols.push(constraintSymbol);
    }
  }

  private extractViewColumns(viewNode: Parser.SyntaxNode, symbols: Symbol[], parentViewId: string): void {
    // Look for the SELECT statement inside the view and extract its aliases
    this.traverseTree(viewNode, (node) => {
      if (node.type === 'select_statement' || node.type === 'select') {
        this.extractSelectAliases(node, symbols, parentViewId);
      }
    });
  }

  private extractViewColumnsFromErrorNode(node: Parser.SyntaxNode, symbols: Symbol[], parentViewId: string): void {
    const errorText = this.getNodeText(node);

    // Only process if this ERROR node contains a CREATE VIEW statement
    if (!errorText.includes('CREATE VIEW')) {
      return;
    }

    // Find the SELECT part of the view and only process that section
    const createViewIndex = errorText.indexOf('CREATE VIEW');
    const selectIndex = errorText.indexOf('SELECT', createViewIndex);
    if (selectIndex === -1) return;

    // Find the FROM clause to limit our search to the SELECT list only
    // Use regex to find the table FROM clause, not FROM inside expressions
    const fromMatch = errorText.match(/\bFROM\s+[a-zA-Z_][a-zA-Z0-9_]*\s+[a-zA-Z_]/i);
    const fromIndex = fromMatch ? errorText.indexOf(fromMatch[0], selectIndex) : -1;
    const selectSection = fromIndex > selectIndex ?
      errorText.substring(selectIndex, fromIndex) :
      errorText.substring(selectIndex);

    // Extract SELECT aliases using regex patterns - only within SELECT section
    // Pattern: any_expression AS alias_name (more flexible to catch complex expressions)
    const aliasRegex = /(?:^|,|\s)\s*(.+?)\s+(?:AS\s+)?([a-zA-Z_][a-zA-Z0-9_]*)\s*(?:,|$)/gim;

    let match;
    while ((match = aliasRegex.exec(selectSection)) !== null) {
      const fullExpression = match[1].trim();
      const aliasName = match[2];

      // Skip if this looks like a table alias or common SQL keywords
      if (['u', 'ae', 'users', 'analytics_events', 'id', 'username', 'email'].includes(aliasName)) {
        continue;
      }

      // Skip if the expression looks like a simple column reference (no functions/calculations)
      if (!fullExpression.includes('(') && !fullExpression.includes('COUNT') && !fullExpression.includes('MIN') &&
          !fullExpression.includes('MAX') && !fullExpression.includes('AVG') && !fullExpression.includes('SUM') &&
          !fullExpression.includes('EXTRACT') && !fullExpression.includes('CASE') &&
          fullExpression.split('.').length <= 2) {
        continue;
      }

      const aliasSymbol: Symbol = {
        id: this.generateId(`${aliasName}_alias`, node.startPosition),
        name: aliasName,
        kind: SymbolKind.Field,
        signature: `${fullExpression} AS ${aliasName}`,
        startLine: node.startPosition.row,
        startColumn: node.startPosition.column,
        endLine: node.endPosition.row,
        endColumn: node.endPosition.column,
        filePath: this.filePath,
        language: this.language,
        parentId: parentViewId,
        visibility: 'public',
        metadata: { isSelectAlias: true, isComputedField: true, extractedFromError: true }
      };

      symbols.push(aliasSymbol);
    }
  }

  private extractSelectAliases(selectNode: Parser.SyntaxNode, symbols: Symbol[], parentId?: string): void {
    // Look for aliased expressions in SELECT lists - specifically in 'term' nodes
    this.traverseTree(selectNode, (node) => {
      // Look for 'term' nodes that contain [expression, keyword_as, identifier] pattern
      if (node.type === 'term' && node.children && node.children.length >= 3) {
        // Check if this term has the pattern [expression, keyword_as, identifier]
        const children = node.children;
        for (let i = 0; i < children.length - 2; i++) {
          if (children[i + 1].type === 'keyword_as' && children[i + 2].type === 'identifier') {
            const exprNode = children[i];
            const asNode = children[i + 1];
            const aliasNode = children[i + 2];

            const aliasName = this.getNodeText(aliasNode);
            const exprText = this.getNodeText(exprNode);

            // Determine expression type for better signatures
            let expression = 'expression';
            if (exprNode.type === 'case') {
              expression = 'CASE expression';
            } else if (exprNode.type === 'window_function' || exprText.includes('OVER (')) {
              // Keep the OVER ( clause in the signature for window functions
              if (exprText.includes('OVER (')) {
                const overIndex = exprText.indexOf('OVER (');
                const endIndex = exprText.indexOf(')', overIndex);
                if (endIndex !== -1) {
                  expression = exprText.substring(0, endIndex + 1);
                } else {
                  expression = exprText; // Keep full text if no closing paren
                }
              } else {
                expression = exprText; // Keep full text for window_function type
              }
            } else if (exprText.includes('COUNT') || exprText.includes('SUM') || exprText.includes('AVG')) {
              expression = 'aggregate function';
            } else {
              expression = exprText.length > 30 ? exprText.substring(0, 30) + '...' : exprText;
            }

            const aliasSymbol = this.createSymbol(aliasNode, aliasName, SymbolKind.Field, {
              signature: `${expression} AS ${aliasName}`,
              visibility: 'public',
              parentId,
              metadata: { isSelectAlias: true, isComputedField: true }
            });
            symbols.push(aliasSymbol);
            break; // Found the alias in this term, move to next term
          }
        }
      }
    });
  }

  private extractCte(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Extract CTE name from identifier child
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Check if this is a recursive CTE by looking for RECURSIVE keyword in the parent context
    let signature = `WITH ${name} AS (...)`;
    const parent = node.parent;
    if (parent) {
      const parentText = this.getNodeText(parent);
      if (parentText.includes('RECURSIVE')) {
        signature = `WITH RECURSIVE ${name} AS (...)`;
      }
    }

    const symbol = this.createSymbol(node, name, SymbolKind.Interface, {
      signature,
      visibility: 'public', // CTEs are accessible within the query
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isCte: true, isTemporaryView: true }
    });

    return symbol;
  }

  private extractIndex(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier') ||
                    this.findChildByType(node, 'index_name');

    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Get the full index text for signature
    const nodeText = this.getNodeText(node);
    const isUnique = nodeText.includes('UNIQUE');

    // Build a more comprehensive signature that includes key parts
    let signature = isUnique ? `CREATE UNIQUE INDEX ${name}` : `CREATE INDEX ${name}`;

    // Add table and column information if found
    const onMatch = nodeText.match(/ON\s+([a-zA-Z_][a-zA-Z0-9_]*)/i);
    if (onMatch) {
      signature += ` ON ${onMatch[1]}`;
    }

    // Add USING clause if present (before columns)
    const usingMatch = nodeText.match(/USING\s+([A-Z]+)/i);
    if (usingMatch) {
      signature += ` USING ${usingMatch[1]}`;
    }

    // Add column information if found
    const columnMatch = nodeText.match(/(?:ON\s+[a-zA-Z_][a-zA-Z0-9_]*(?:\s+USING\s+[A-Z]+)?\s*)?(\([^)]+\))/i);
    if (columnMatch) {
      signature += ` ${columnMatch[1]}`;
    }

    // Add INCLUDE clause if present
    const includeMatch = nodeText.match(/INCLUDE\s*(\([^)]+\))/i);
    if (includeMatch) {
      signature += ` INCLUDE ${includeMatch[1]}`;
    }

    // Add WHERE clause if present
    const whereMatch = nodeText.match(/WHERE\s+(.+?)(?:;|$)/i);
    if (whereMatch) {
      signature += ` WHERE ${whereMatch[1].trim()}`;
    }

    const symbol = this.createSymbol(node, name, SymbolKind.Property, {
      signature,
      visibility: 'public',
      parentId,
      metadata: { isIndex: true, isUnique }
    });

    return symbol;
  }

  private extractFromErrorNode(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const errorText = this.getNodeText(node);

    // Extract stored procedures from DELIMITER syntax
    const procedureMatch = errorText.match(/CREATE\s+PROCEDURE\s+([a-zA-Z_][a-zA-Z0-9_]*)/i);
    if (procedureMatch) {
      const procedureName = procedureMatch[1];
      const procedureSymbol = this.createSymbol(node, procedureName, SymbolKind.Function, {
        signature: `CREATE PROCEDURE ${procedureName}(...)`,
        visibility: 'public',
        parentId,
        metadata: { isStoredProcedure: true, extractedFromError: true }
      });
      return procedureSymbol;
    }

    // Extract functions with RETURNS clause
    const functionMatch = errorText.match(/CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+([a-zA-Z_][a-zA-Z0-9_]*)\s*\([^)]*\)\s*RETURNS?\s+([A-Z0-9(),\s]+)/i);
    if (functionMatch) {
      const functionName = functionMatch[1];
      const returnType = functionMatch[2].trim();
      const functionSymbol = this.createSymbol(node, functionName, SymbolKind.Function, {
        signature: `CREATE FUNCTION ${functionName}(...) RETURNS ${returnType}`,
        visibility: 'public',
        parentId,
        metadata: { isFunction: true, extractedFromError: true, returnType }
      });
      return functionSymbol;
    }

    // Fallback: Extract any CREATE FUNCTION
    const simpleFunctionMatch = errorText.match(/CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+([a-zA-Z_][a-zA-Z0-9_]*)/i);
    if (simpleFunctionMatch) {
      const functionName = simpleFunctionMatch[1];
      const functionSymbol = this.createSymbol(node, functionName, SymbolKind.Function, {
        signature: `CREATE FUNCTION ${functionName}(...)`,
        visibility: 'public',
        parentId,
        metadata: { isFunction: true, extractedFromError: true }
      });
      return functionSymbol;
    }

    // Extract schemas from ERROR nodes
    const schemaMatch = errorText.match(/CREATE\s+SCHEMA\s+([a-zA-Z_][a-zA-Z0-9_]*)/i);
    if (schemaMatch) {
      const schemaName = schemaMatch[1];
      const schemaSymbol = this.createSymbol(node, schemaName, SymbolKind.Namespace, {
        signature: `CREATE SCHEMA ${schemaName}`,
        visibility: 'public',
        parentId,
        metadata: { isSchema: true, extractedFromError: true }
      });
      return schemaSymbol;
    }

    // Extract views from ERROR nodes
    const viewMatch = errorText.match(/CREATE\s+VIEW\s+([a-zA-Z_][a-zA-Z0-9_]*)\s+AS/i);
    if (viewMatch) {
      const viewName = viewMatch[1];
      const viewSymbol = this.createSymbol(node, viewName, SymbolKind.Interface, {
        signature: `CREATE VIEW ${viewName}`,
        visibility: 'public',
        parentId,
        metadata: { isView: true, extractedFromError: true }
      });
      return viewSymbol;
    }

    // Extract triggers from ERROR nodes
    const triggerMatch = errorText.match(/CREATE\s+TRIGGER\s+([a-zA-Z_][a-zA-Z0-9_]*)/i);
    if (triggerMatch) {
      const triggerName = triggerMatch[1];

      // Try to extract trigger details (BEFORE/AFTER, event, table)
      const detailsMatch = errorText.match(/CREATE\s+TRIGGER\s+[a-zA-Z_][a-zA-Z0-9_]*\s+(BEFORE|AFTER)\s+(INSERT|UPDATE|DELETE)\s+ON\s+([a-zA-Z_][a-zA-Z0-9_]*)/i);

      let signature = `CREATE TRIGGER ${triggerName}`;
      if (detailsMatch) {
        const timing = detailsMatch[1];
        const event = detailsMatch[2];
        const table = detailsMatch[3];
        signature = `CREATE TRIGGER ${triggerName} ${timing} ${event} ON ${table}`;
      }

      const triggerSymbol = this.createSymbol(node, triggerName, SymbolKind.Method, {
        signature,
        visibility: 'public',
        parentId,
        metadata: { isTrigger: true, extractedFromError: true }
      });
      return triggerSymbol;
    }

    return null;
  }

  private extractMultipleFromErrorNode(node: Parser.SyntaxNode, symbols: Symbol[], parentId?: string): void {
    const errorText = this.getNodeText(node);

    // Try to extract each type of symbol from the ERROR node
    // Extract stored procedures
    const procedureMatch = errorText.match(/CREATE\s+PROCEDURE\s+([a-zA-Z_][a-zA-Z0-9_]*)/i);
    if (procedureMatch) {
      const procedureName = procedureMatch[1];
      const procedureSymbol = this.createSymbol(node, procedureName, SymbolKind.Function, {
        signature: `CREATE PROCEDURE ${procedureName}(...)`,
        visibility: 'public',
        parentId,
        metadata: { isStoredProcedure: true, extractedFromError: true }
      });
      symbols.push(procedureSymbol);
      // Extract parameters for this procedure
      this.extractParametersFromErrorNode(node, symbols, procedureSymbol.id);
    }

    // Extract functions with RETURNS clause
    const functionMatch = errorText.match(/CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+([a-zA-Z_][a-zA-Z0-9_]*)\s*\([^)]*\)\s*RETURNS?\s+([A-Z0-9(),\s]+)/i);
    if (functionMatch) {
      const functionName = functionMatch[1];
      const returnType = functionMatch[2].trim();
      const functionSymbol = this.createSymbol(node, functionName, SymbolKind.Function, {
        signature: `CREATE FUNCTION ${functionName}(...) RETURNS ${returnType}`,
        visibility: 'public',
        parentId,
        metadata: { isFunction: true, extractedFromError: true, returnType }
      });
      symbols.push(functionSymbol);
      // Extract DECLARE variables from function body
      this.extractDeclareVariables(node, symbols, functionSymbol.id);
    } else {
      // Fallback: Extract any CREATE FUNCTION
      const simpleFunctionMatch = errorText.match(/CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+([a-zA-Z_][a-zA-Z0-9_]*)/i);
      if (simpleFunctionMatch) {
        const functionName = simpleFunctionMatch[1];
        const functionSymbol = this.createSymbol(node, functionName, SymbolKind.Function, {
          signature: `CREATE FUNCTION ${functionName}(...)`,
          visibility: 'public',
          parentId,
          metadata: { isFunction: true, extractedFromError: true }
        });
        symbols.push(functionSymbol);
        // Extract DECLARE variables from function body
        this.extractDeclareVariables(node, symbols, functionSymbol.id);
      }
    }

    // Extract schemas
    const schemaMatch = errorText.match(/CREATE\s+SCHEMA\s+([a-zA-Z_][a-zA-Z0-9_]*)/i);
    if (schemaMatch) {
      const schemaName = schemaMatch[1];
      const schemaSymbol = this.createSymbol(node, schemaName, SymbolKind.Namespace, {
        signature: `CREATE SCHEMA ${schemaName}`,
        visibility: 'public',
        parentId,
        metadata: { isSchema: true, extractedFromError: true }
      });
      symbols.push(schemaSymbol);
    }

    // Extract views
    const viewMatch = errorText.match(/CREATE\s+VIEW\s+([a-zA-Z_][a-zA-Z0-9_]*)\s+AS/i);
    if (viewMatch) {
      const viewName = viewMatch[1];
      const viewSymbol = this.createSymbol(node, viewName, SymbolKind.Interface, {
        signature: `CREATE VIEW ${viewName}`,
        visibility: 'public',
        parentId,
        metadata: { isView: true, extractedFromError: true }
      });
      symbols.push(viewSymbol);
      // Extract view columns from this ERROR node
      this.extractViewColumnsFromErrorNode(node, symbols, viewSymbol.id);
    }

    // Extract triggers
    const triggerMatch = errorText.match(/CREATE\s+TRIGGER\s+([a-zA-Z_][a-zA-Z0-9_]*)/i);
    if (triggerMatch) {
      const triggerName = triggerMatch[1];

      // Try to extract trigger details (BEFORE/AFTER, event, table)
      const detailsMatch = errorText.match(/CREATE\s+TRIGGER\s+[a-zA-Z_][a-zA-Z0-9_]*\s+(BEFORE|AFTER)\s+(INSERT|UPDATE|DELETE)\s+ON\s+([a-zA-Z_][a-zA-Z0-9_]*)/i);

      let signature = `CREATE TRIGGER ${triggerName}`;
      if (detailsMatch) {
        const timing = detailsMatch[1];
        const event = detailsMatch[2];
        const table = detailsMatch[3];
        signature += ` ${timing} ${event} ON ${table}`;
      }

      const triggerSymbol = this.createSymbol(node, triggerName, SymbolKind.Method, {
        signature,
        visibility: 'public',
        parentId,
        docComment: this.extractDocumentation(node),
        metadata: { isTrigger: true, extractedFromError: true }
      });
      symbols.push(triggerSymbol);
    }

    // Extract constraints from ALTER TABLE statements
    const constraintMatch = errorText.match(/ALTER\s+TABLE\s+[a-zA-Z_][a-zA-Z0-9_]*\s+ADD\s+CONSTRAINT\s+([a-zA-Z_][a-zA-Z0-9_]*)\s+(CHECK|FOREIGN\s+KEY|UNIQUE|PRIMARY\s+KEY)/i);
    if (constraintMatch) {
      const constraintName = constraintMatch[1];
      const constraintType = constraintMatch[2].toUpperCase();

      let signature = `ALTER TABLE ADD CONSTRAINT ${constraintName} ${constraintType}`;

      // Add more details based on constraint type
      if (constraintType === 'CHECK') {
        const checkMatch = errorText.match(/CHECK\s*\(([^)]+(?:\([^)]*\)[^)]*)*)\)/i);
        if (checkMatch) {
          signature += ` (${checkMatch[1].trim()})`;
        }
      } else if (constraintType.includes('FOREIGN')) {
        const fkMatch = errorText.match(/FOREIGN\s+KEY\s*\(([^)]+)\)\s*REFERENCES\s+([a-zA-Z_][a-zA-Z0-9_]*)/i);
        if (fkMatch) {
          signature += ` (${fkMatch[1]}) REFERENCES ${fkMatch[2]}`;
        }

        // Add ON DELETE/UPDATE actions
        const onDeleteMatch = errorText.match(/ON\s+DELETE\s+(CASCADE|RESTRICT|SET\s+NULL|NO\s+ACTION)/i);
        if (onDeleteMatch) {
          signature += ` ON DELETE ${onDeleteMatch[1].toUpperCase()}`;
        }

        const onUpdateMatch = errorText.match(/ON\s+UPDATE\s+(CASCADE|RESTRICT|SET\s+NULL|NO\s+ACTION)/i);
        if (onUpdateMatch) {
          signature += ` ON UPDATE ${onUpdateMatch[1].toUpperCase()}`;
        }
      }

      const constraintSymbol: Symbol = {
        id: this.generateId(`${constraintName}_constraint`, node.startPosition),
        name: constraintName,
        kind: SymbolKind.Property,
        signature,
        startLine: node.startPosition.row,
        startColumn: node.startPosition.column,
        endLine: node.endPosition.row,
        endColumn: node.endPosition.column,
        filePath: this.filePath,
        language: this.language,
        parentId,
        visibility: 'public',
        metadata: { isConstraint: true, constraintType, extractedFromError: true }
      };

      symbols.push(constraintSymbol);
    }

    // Extract domains
    const domainMatch = errorText.match(/CREATE\s+DOMAIN\s+([a-zA-Z_][a-zA-Z0-9_]*)\s+AS\s+([A-Z]+(?:\(\d+(?:,\s*\d+)?\))?)/i);
    if (domainMatch) {
      const domainName = domainMatch[1];
      const baseType = domainMatch[2];

      let signature = `CREATE DOMAIN ${domainName} AS ${baseType}`;

      // Add CHECK constraint if present
      const checkMatch = errorText.match(/CHECK\s*\(([^)]+(?:\([^)]*\)[^)]*)*)\)/i);
      if (checkMatch) {
        signature += ` CHECK (${checkMatch[1].trim()})`;
      }

      const domainSymbol = this.createSymbol(node, domainName, SymbolKind.Class, {
        signature,
        visibility: 'public',
        parentId,
        metadata: { isDomain: true, extractedFromError: true, baseType }
      });
      symbols.push(domainSymbol);
    }

    // Extract enum/custom types
    const enumMatch = errorText.match(/CREATE\s+TYPE\s+([a-zA-Z_][a-zA-Z0-9_]*)\s+AS\s+ENUM\s*\(([\s\S]*?)\)/i);
    if (enumMatch) {
      const enumName = enumMatch[1];
      const enumValues = enumMatch[2];

      const signature = `CREATE TYPE ${enumName} AS ENUM (${enumValues.trim()})`;

      const enumSymbol = this.createSymbol(node, enumName, SymbolKind.Class, {
        signature,
        visibility: 'public',
        parentId,
        metadata: { isEnum: true, extractedFromError: true }
      });
      symbols.push(enumSymbol);
    }

    // Extract aggregate functions
    const aggregateMatch = errorText.match(/CREATE\s+AGGREGATE\s+([a-zA-Z_][a-zA-Z0-9_]*)\s*\(([^)]*)\)/i);
    if (aggregateMatch) {
      const aggregateName = aggregateMatch[1];
      const parameters = aggregateMatch[2];

      const signature = `CREATE AGGREGATE ${aggregateName}(${parameters})`;

      const aggregateSymbol = this.createSymbol(node, aggregateName, SymbolKind.Function, {
        signature,
        visibility: 'public',
        parentId,
        metadata: { isAggregate: true, extractedFromError: true }
      });
      symbols.push(aggregateSymbol);
    }
  }

  private extractDeclareVariables(functionNode: Parser.SyntaxNode, symbols: Symbol[], parentId: string): void {

    // Look for DECLARE statements within function bodies
    this.traverseTree(functionNode, (node) => {
      // PostgreSQL style: function_declaration nodes like "v_current_prefs JSONB;"
      if (node.type === 'function_declaration') {
        // Parse the declaration text to extract variable name and type
        const declarationText = this.getNodeText(node).trim();
        // Match patterns like "v_current_prefs JSONB;" or "v_score DECIMAL(10,2) DEFAULT 0.0;"
        const match = declarationText.match(/^([a-zA-Z_][a-zA-Z0-9_]*)\s+([A-Z0-9(),\s]+)/i);

        if (match) {
          const variableName = match[1];
          const variableType = match[2].split(/\s+/)[0]; // Get first word as type

          const variableSymbol = this.createSymbol(node, variableName, SymbolKind.Variable, {
            signature: `DECLARE ${variableName} ${variableType}`,
            visibility: 'private',
            parentId,
            metadata: { isLocalVariable: true, isDeclaredVariable: true }
          });
          symbols.push(variableSymbol);
        }
      }

      // MySQL style: keyword_declare followed by identifier and type
      else if (node.type === 'keyword_declare') {
        // For MySQL DECLARE statements, look for the pattern in the surrounding text
        const parent = node.parent;
        if (parent) {
          const parentText = this.getNodeText(parent);

          // Look for DECLARE patterns in the parent text
          const declareRegex = /DECLARE\s+([a-zA-Z_][a-zA-Z0-9_]*)\s+(DECIMAL\([^)]+\)|INT|BIGINT|VARCHAR\([^)]+\)|TEXT|BOOLEAN)/gi;
          let match;

          while ((match = declareRegex.exec(parentText)) !== null) {
            const variableName = match[1];
            const variableType = match[2];

            const variableSymbol = this.createSymbol(node, variableName, SymbolKind.Variable, {
              signature: `DECLARE ${variableName} ${variableType}`,
              visibility: 'private',
              parentId,
              metadata: { isLocalVariable: true, isDeclaredVariable: true }
            });
            symbols.push(variableSymbol);
          }
        }
      }
    });
  }

  private extractParametersFromErrorNode(node: Parser.SyntaxNode, symbols: Symbol[], parentId: string): void {
    const errorText = this.getNodeText(node);

    // Extract parameters from procedure/function definitions
    // Look for patterns like "IN p_user_id BIGINT", "OUT p_total_events INT"
    const paramRegex = /(IN|OUT|INOUT)?\s*([a-zA-Z_][a-zA-Z0-9_]*)\s+(BIGINT|INT|VARCHAR|DECIMAL|DATE|BOOLEAN|TEXT)/gi;

    let match;
    while ((match = paramRegex.exec(errorText)) !== null) {
      const direction = match[1] || 'IN'; // Default to IN if not specified
      const paramName = match[2];
      const paramType = match[3];

      // Don't extract procedure/function names as parameters
      if (!errorText.includes(`PROCEDURE ${paramName}`) && !errorText.includes(`FUNCTION ${paramName}`)) {
        const paramSymbol: Symbol = {
          id: this.generateId(`${paramName}_param`, node.startPosition),
          name: paramName,
          kind: SymbolKind.Variable,
          signature: `${direction} ${paramName} ${paramType}`,
          startLine: node.startPosition.row,
          startColumn: node.startPosition.column,
          endLine: node.endPosition.row,
          endColumn: node.endPosition.column,
          filePath: this.filePath,
          language: this.language,
          parentId,
          visibility: 'public',
          metadata: { isParameter: true, extractedFromError: true }
        };

        symbols.push(paramSymbol);
      }
    }
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
    // Extract function/procedure name from object_reference if present
    const objectRefNode = this.findChildByType(node, 'object_reference');
    const nameNode = objectRefNode ?
      this.findChildByType(objectRefNode, 'identifier') :
      (this.findChildByType(node, 'identifier') ||
       this.findChildByType(node, 'procedure_name') ||
       this.findChildByType(node, 'function_name'));
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

    // For functions, try to extract the RETURNS clause and LANGUAGE
    let returnClause = '';
    let languageClause = '';
    if (isFunction) {
      // Look for decimal node for RETURNS DECIMAL(10,2) - search recursively
      const decimalNodes = this.findNodesByType(node, 'decimal');
      if (decimalNodes.length > 0) {
        const decimalText = this.getNodeText(decimalNodes[0]);
        returnClause = ` RETURNS ${decimalText}`;
      } else {
        // Look for other return types as direct children
        const returnTypeNodes = ['keyword_boolean', 'keyword_bigint', 'keyword_int', 'keyword_varchar', 'keyword_text', 'keyword_jsonb'];
        for (const typeStr of returnTypeNodes) {
          const typeNode = this.findChildByType(node, typeStr);
          if (typeNode) {
            const typeText = this.getNodeText(typeNode).replace('keyword_', '').toUpperCase();
            returnClause = ` RETURNS ${typeText}`;
            break;
          }
        }
      }

      // Look for LANGUAGE clause (PostgreSQL functions)
      const languageNode = this.findChildByType(node, 'function_language');
      if (languageNode) {
        const languageText = this.getNodeText(languageNode);
        languageClause = ` ${languageText}`;
      }
    }

    return `${keyword} ${name}(${params.join(', ')})${returnClause}${languageClause}`;
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
          case 'join':
          case 'join_clause':
            this.extractJoinRelationships(node, symbols, relationships);
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

  private extractJoinRelationships(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    // Extract JOIN relationships from SQL queries
    this.traverseTree(node, (childNode) => {
      if (childNode.type === 'table_name' ||
          (childNode.type === 'identifier' && childNode.parent?.type === 'object_reference')) {

        const tableName = this.getNodeText(childNode);
        const tableSymbol = symbols.find(s => s.name === tableName && s.kind === SymbolKind.Class);

        if (tableSymbol) {
          // Create a join relationship
          relationships.push({
            fromSymbolId: tableSymbol.id,
            toSymbolId: tableSymbol.id, // Self-reference for joins
            kind: RelationshipKind.Joins,
            filePath: this.filePath,
            lineNumber: node.startPosition.row,
            confidence: 0.9,
            metadata: {
              joinType: 'join',
              tableName: tableName
            }
          });
        }
      }
    });
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