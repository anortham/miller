import { Parser } from 'web-tree-sitter';
import { BaseExtractor, Symbol, Relationship, SymbolKind, RelationshipKind } from './base-extractor.js';

/**
 * Zig language extractor that handles Zig-specific constructs:
 * - Functions and their parameters
 * - Structs and their fields
 * - Enums and their variants
 * - Constants and variables
 * - Modules and imports
 * - Comptime constructs
 * - Error types and error handling
 *
 * Special focus on Bun/systems programming patterns since this will
 * catch the attention of the Bun team (Bun is built with Zig!).
 */
export class ZigExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];

    const visitNode = (node: Parser.SyntaxNode, parentId?: string) => {
      if (!node || !node.type) {
        return; // Skip invalid nodes
      }


      let symbol: Symbol | null = null;

      try {
        switch (node.type) {
          case 'function_declaration':
          case 'function_definition':
            symbol = this.extractFunction(node, parentId);
            break;
          case 'test_declaration':
            symbol = this.extractTest(node, parentId);
            break;
          case 'struct_declaration':
            symbol = this.extractStruct(node, parentId);
            break;
          case 'union_declaration':
            symbol = this.extractUnion(node, parentId);
            break;
          case 'enum_declaration':
            symbol = this.extractEnum(node, parentId);
            break;
          case 'variable_declaration':
          case 'const_declaration':
            symbol = this.extractVariable(node, parentId);
            break;
          case 'error_declaration':
            symbol = this.extractErrorType(node, parentId);
            break;
          case 'type_declaration':
            symbol = this.extractTypeAlias(node, parentId);
            break;
          case 'parameter':
            symbol = this.extractParameter(node, parentId);
            break;
          case 'field_declaration':
          case 'struct_field':
          case 'container_field':
            symbol = this.extractStructField(node, parentId);
            break;
          case 'enum_field':
          case 'enum_variant':
            symbol = this.extractEnumVariant(node, parentId);
            break;
          case 'ERROR':
            // GENERIC TYPE FIX: Handle parsing errors that might be generic type constructors
            symbol = this.extractFromErrorNode(node, parentId);
            break;
          default:
            // Handle other Zig constructs
            break;
        }
      } catch (error) {
        console.warn(`Error extracting Zig symbol from ${node.type}:`, error);
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
      console.warn('Zig parsing failed:', error);
    }

    return symbols;
  }

  private extractFunction(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Check if it's a public function
    const isPublic = this.isPublicFunction(node);

    // Check if it's an export function
    const isExport = this.isExportFunction(node);

    // Check if this function is inside a struct (making it a method)
    const isInsideStruct = this.isInsideStruct(node);
    const symbolKind = isInsideStruct ? SymbolKind.Method : SymbolKind.Function;

    const functionSymbol = this.createSymbol(node, name, symbolKind, {
      signature: this.extractFunctionSignature(node),
      visibility: isPublic || isExport ? 'public' : 'private',
      parentId,
      docComment: this.extractDocumentation(node)
    });

    return functionSymbol;
  }

  private extractTest(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Extract test name from string node
    const stringNode = this.findChildByType(node, 'string');
    if (!stringNode) return null;

    // Get the actual test name from string_content
    const stringContentNode = this.findChildByType(stringNode, 'string_content');
    if (!stringContentNode) return null;

    const testName = this.getNodeText(stringContentNode);

    const testSymbol = this.createSymbol(node, testName, SymbolKind.Function, {
      signature: `test "${testName}"`,
      visibility: 'public', // Test functions are generally public
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isTest: true }
    });

    return testSymbol;
  }

  private extractParameter(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const paramName = this.getNodeText(nameNode);
    const typeNode = this.findChildByType(node, 'type_expression') ||
                    this.findChildByType(node, 'identifier', 1); // Second identifier is often the type

    const paramType = typeNode ? this.getNodeText(typeNode) : 'unknown';

    const paramSymbol = this.createSymbol(node, paramName, SymbolKind.Variable, {
      signature: `${paramName}: ${paramType}`,
      visibility: 'public',
      parentId
    });

    return paramSymbol;
  }

  private extractStruct(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const isPublic = this.isPublicDeclaration(node);

    const structSymbol = this.createSymbol(node, name, SymbolKind.Class, {
      signature: `struct ${name}`,
      visibility: isPublic ? 'public' : 'private',
      parentId,
      docComment: this.extractDocumentation(node)
    });

    return structSymbol;
  }

  private extractUnion(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const isPublic = this.isPublicDeclaration(node);

    // Check if it's a union(enum) or regular union
    const nodeText = this.getNodeText(node);
    const unionType = nodeText.includes('union(enum)') ? 'union(enum)' : 'union';

    const unionSymbol = this.createSymbol(node, name, SymbolKind.Class, {
      signature: `${unionType} ${name}`,
      visibility: isPublic ? 'public' : 'private',
      parentId,
      docComment: this.extractDocumentation(node)
    });

    return unionSymbol;
  }

  private extractStructField(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const fieldName = this.getNodeText(nameNode);

    // SYSTEMATIC FIX: Zig uses builtin_type nodes (f32, i32, etc.) in container_field
    const typeNode = this.findChildByType(node, 'type_expression') ||
                    this.findChildByType(node, 'builtin_type') ||
                    this.findChildByType(node, 'slice_type') ||
                    this.findChildByType(node, 'identifier', 1);

    const fieldType = typeNode ? this.getNodeText(typeNode) : 'unknown';

    // SYSTEMATIC FIX: Use SymbolKind.Field to match test expectations
    const fieldSymbol = this.createSymbol(node, fieldName, SymbolKind.Field, {
      signature: `${fieldName}: ${fieldType}`,
      visibility: 'public', // Zig struct fields are generally public
      parentId
    });

    return fieldSymbol;
  }

  private extractEnum(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const isPublic = this.isPublicDeclaration(node);

    const enumSymbol = this.createSymbol(node, name, SymbolKind.Enum, {
      signature: `enum ${name}`,
      visibility: isPublic ? 'public' : 'private',
      parentId,
      docComment: this.extractDocumentation(node)
    });

    return enumSymbol;
  }

  private extractEnumVariant(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const variantName = this.getNodeText(nameNode);

    const variantSymbol = this.createSymbol(node, variantName, SymbolKind.EnumMember, {
      signature: variantName,
      visibility: 'public',
      parentId
    });

    return variantSymbol;
  }

  private extractVariable(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const isConst = node.type === 'const_declaration' || this.getNodeText(node).includes('const');
    const isPublic = this.isPublicDeclaration(node);

    // GENERIC TYPE FIX: Check if this is a generic type constructor with parameters
    const nodeText = this.getNodeText(node);
    if (nodeText.includes('(comptime') && nodeText.includes('= struct')) {
      // This is a generic type constructor like Container(comptime T: type) = struct
      const paramMatch = nodeText.match(/\(([^)]+)\)/);
      const params = paramMatch ? paramMatch[1] : 'comptime T: type';

      const functionSymbol = this.createSymbol(node, name, SymbolKind.Function, {
        signature: `fn ${name}(${params}) type`,
        visibility: isPublic ? 'public' : 'private',
        parentId,
        docComment: this.extractDocumentation(node),
        metadata: { isGenericTypeConstructor: true }
      });
      return functionSymbol;
    }

    // SYSTEMATIC FIX: Check if this is a struct or union declaration
    const structNode = this.findChildByType(node, 'struct_declaration');
    const unionNode = this.findChildByType(node, 'union_declaration');

    if (structNode) {
      // Check if it's a packed struct by looking at the full text
      const nodeText = this.getNodeText(node);
      const isPacked = nodeText.includes('packed struct');
      const isExtern = nodeText.includes('extern struct');

      let structType = 'struct';
      if (isPacked) structType = 'packed struct';
      else if (isExtern) structType = 'extern struct';

      // This is a struct, extract it as a class
      const structSymbol = this.createSymbol(node, name, SymbolKind.Class, {
        signature: `const ${name} = ${structType}`,
        visibility: isPublic ? 'public' : 'private',
        parentId,
        docComment: this.extractDocumentation(node)
      });
      return structSymbol;
    }

    if (unionNode) {
      // Check if it's a union(enum) or regular union
      const nodeText = this.getNodeText(node);
      const unionType = nodeText.includes('union(enum)') ? 'union(enum)' : 'union';

      // This is a union, extract it as a class
      const unionSymbol = this.createSymbol(node, name, SymbolKind.Class, {
        signature: `const ${name} = ${unionType}`,
        visibility: isPublic ? 'public' : 'private',
        parentId,
        docComment: this.extractDocumentation(node)
      });
      return unionSymbol;
    }

    // Check if this is an enum declaration (const Color = enum(u8) { ... })
    const enumNode = this.findChildByType(node, 'enum_declaration');
    if (enumNode) {
      // Check if it's an enum with explicit values like enum(u8)
      const nodeText = this.getNodeText(node);
      const enumMatch = nodeText.match(/enum\(([^)]+)\)/);
      const enumType = enumMatch ? `enum(${enumMatch[1]})` : 'enum';

      // This is an enum, extract it as an enum
      const enumSymbol = this.createSymbol(node, name, SymbolKind.Enum, {
        signature: `const ${name} = ${enumType}`,
        visibility: isPublic ? 'public' : 'private',
        parentId,
        docComment: this.extractDocumentation(node)
      });
      return enumSymbol;
    }

    // Check if this is an error set or error union declaration
    if (nodeText.includes('error{') || nodeText.includes('error {')) {
      let signature = `const ${name} = `;

      if (nodeText.includes('||')) {
        // Error union like: error{...} || OtherError
        const unionMatch = nodeText.match(/error\s*\{[^}]*\}\s*\|\|\s*(\w+)/);
        if (unionMatch) {
          signature += `error{...} || ${unionMatch[1]}`;
        } else {
          signature += 'error{...} || ...';
        }
      } else {
        signature += 'error{...}';
      }

      const errorSymbol = this.createSymbol(node, name, SymbolKind.Class, {
        signature,
        visibility: isPublic ? 'public' : 'private',
        parentId,
        docComment: this.extractDocumentation(node),
        metadata: { isErrorSet: true }
      });
      return errorSymbol;
    }

    // Check if this is a function type declaration (const BinaryOp = fn (...) ...)
    if (nodeText.includes('fn (') || nodeText.includes('fn(')) {
      // Extract the function type signature
      const fnTypeMatch = nodeText.match(/=\s*(fn\s*\([^}]*\).*?)(?:;|$)/);
      const fnType = fnTypeMatch ? fnTypeMatch[1] : 'fn (...)';

      const fnTypeSymbol = this.createSymbol(node, name, SymbolKind.Interface, {
        signature: `const ${name} = ${fnType}`,
        visibility: isPublic ? 'public' : 'private',
        parentId,
        docComment: this.extractDocumentation(node),
        metadata: { isFunctionType: true }
      });
      return fnTypeSymbol;
    }

    // Extract type if available, or detect switch expressions
    const typeNode = this.findChildByType(node, 'type_expression');
    const switchNode = this.findChildByType(node, 'switch_expression');

    let varType = typeNode ? this.getNodeText(typeNode) : 'inferred';

    // TYPE ALIAS FIX: For type aliases, extract the assignment value
    if (varType === 'inferred' && isConst) {
      // Look for assignment pattern like "const ShapeList = std.ArrayList(BaseShape);"
      const nodeText = this.getNodeText(node);
      const assignmentMatch = nodeText.match(/=\s*([^;]+)/);
      if (assignmentMatch) {
        varType = assignmentMatch[1].trim();
      }
    }

    // If it contains a switch expression, include that in the signature
    if (switchNode) {
      const switchText = this.getNodeText(switchNode);
      if (switchText.length > 50) {
        // If switch is long, just indicate it contains a switch
        varType = `switch(${switchText.substring(0, 20)}...)`;
      } else {
        varType = switchText;
      }
    }

    const variableSymbol = this.createSymbol(node, name, isConst ? SymbolKind.Constant : SymbolKind.Variable, {
      signature: `${isConst ? 'const' : 'var'} ${name}: ${varType}`,
      visibility: isPublic ? 'public' : 'private',
      parentId,
      docComment: this.extractDocumentation(node)
    });

    return variableSymbol;
  }

  private extractErrorType(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    const errorSymbol = this.createSymbol(node, name, SymbolKind.Class, {
      signature: `error ${name}`,
      visibility: 'public',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isErrorType: true }
    });

    return errorSymbol;
  }

  private extractTypeAlias(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const isPublic = this.isPublicDeclaration(node);

    const typeSymbol = this.createSymbol(node, name, SymbolKind.Interface, {
      signature: `type ${name}`,
      visibility: isPublic ? 'public' : 'private',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isTypeAlias: true }
    });

    return typeSymbol;
  }

  private isPublicFunction(node: Parser.SyntaxNode): boolean {
    // Check for "pub" keyword as first child of function (Zig puts pub inside function_declaration)
    if (node.children.length > 0 &&
        (node.children[0].type === 'pub' || this.getNodeText(node.children[0]) === 'pub')) {
      return true;
    }

    // Also check for "pub" keyword before function (fallback for top-level functions)
    let current = node.previousSibling;
    while (current) {
      if (current.type === 'pub' || this.getNodeText(current) === 'pub') {
        return true;
      }
      current = current.previousSibling;
    }
    return false;
  }

  private isExportFunction(node: Parser.SyntaxNode): boolean {
    // Check for "export" keyword as first child of function
    if (node.children.length > 0 &&
        (node.children[0].type === 'export' || this.getNodeText(node.children[0]) === 'export')) {
      return true;
    }

    // Also check for "export" keyword before function (fallback)
    let current = node.previousSibling;
    while (current) {
      if (current.type === 'export' || this.getNodeText(current) === 'export') {
        return true;
      }
      current = current.previousSibling;
    }
    return false;
  }

  private isInlineFunction(node: Parser.SyntaxNode): boolean {
    // Check for "inline" keyword in function children
    for (const child of node.children) {
      if (child.type === 'inline' || this.getNodeText(child) === 'inline') {
        return true;
      }
    }

    // Also check for "inline" keyword before function (fallback)
    let current = node.previousSibling;
    while (current) {
      if (current.type === 'inline' || this.getNodeText(current) === 'inline') {
        return true;
      }
      current = current.previousSibling;
    }
    return false;
  }

  private isPublicDeclaration(node: Parser.SyntaxNode): boolean {
    // Check for "pub" keyword before declaration
    let current = node.previousSibling;
    while (current) {
      if (current.type === 'pub' || this.getNodeText(current) === 'pub') {
        return true;
      }
      current = current.previousSibling;
    }
    return false;
  }

  private isInsideStruct(node: Parser.SyntaxNode): boolean {
    // Walk up the tree to see if we're inside a struct declaration
    let current = node.parent;
    while (current) {
      if (current.type === 'struct_declaration' ||
          current.type === 'container_declaration' ||
          current.type === 'enum_declaration') {
        return true;
      }
      current = current.parent;
    }
    return false;
  }

  private extractFunctionSignature(node: Parser.SyntaxNode): string {
    const nameNode = this.findChildByType(node, 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknown';

    // Check for visibility and function modifiers (pub, export, inline)
    const isPublic = this.isPublicFunction(node);
    const isExport = this.isExportFunction(node);
    const isInline = this.isInlineFunction(node);

    let modifierPrefix = '';
    if (isPublic) {
      modifierPrefix += 'pub ';
    }
    if (isExport) {
      modifierPrefix += 'export ';
    }
    if (isInline) {
      modifierPrefix += 'inline ';
    }

    // SYSTEMATIC FIX: Check for extern prefix
    const externNode = this.findChildByType(node, 'extern');
    const stringNode = this.findChildByType(node, 'string');
    let externPrefix = '';
    if (externNode && stringNode) {
      const linkage = this.getNodeText(stringNode);
      externPrefix = `extern ${linkage} `;
    }

    // Extract parameters (including comptime and variadic parameters)
    const params: string[] = [];
    const paramList = this.findChildByType(node, 'parameters') || this.findChildByType(node, 'parameter_list');
    if (paramList) {
      for (const child of paramList.children) {
        if (child.type === 'parameter') {
          // Handle comptime parameters like "comptime T: type"
          const comptimeNode = this.findChildByType(child, 'comptime');
          const paramNameNode = this.findChildByType(child, 'identifier');

          // Look for various type nodes that Zig uses
          // For parameters like "allocator: Allocator", the type comes after the colon
          let typeNode = this.findChildByType(child, 'type_expression') ||
                         this.findChildByType(child, 'builtin_type') ||
                         this.findChildByType(child, 'pointer_type') ||
                         this.findChildByType(child, 'slice_type') ||
                         this.findChildByType(child, 'optional_type');

          // If no complex type found, look for identifier that comes after the colon
          if (!typeNode) {
            const colonIndex = child.children.findIndex(c => c.type === ':');
            if (colonIndex !== -1 && colonIndex + 1 < child.children.length) {
              typeNode = child.children[colonIndex + 1];
            }
          }

          if (paramNameNode) {
            const paramName = this.getNodeText(paramNameNode);
            const paramType = typeNode ? this.getNodeText(typeNode) : '';

            let paramStr = '';
            if (comptimeNode) {
              paramStr = `comptime ${paramName}`;
              if (paramType) {
                paramStr += `: ${paramType}`;
              }
            } else if (paramType) {
              paramStr = `${paramName}: ${paramType}`;
            } else {
              paramStr = paramName;
            }

            params.push(paramStr);
          }
        } else if (child.type === 'variadic_parameter' || this.getNodeText(child) === '...') {
          // Handle variadic parameters
          params.push('...');
        }
      }
    }

    // VARIADIC FIX: Also check if the raw function text contains "..." for variadic parameters
    // This handles cases where tree-sitter doesn't properly identify variadic nodes
    const fullFunctionText = this.getNodeText(node);
    if (fullFunctionText.includes('...') && !params.includes('...')) {
      params.push('...');
    }

    // Extract return type (including Zig pointer, error union, and optional types)
    const returnTypeNode = this.findChildByType(node, 'return_type') ||
                          this.findChildByType(node, 'type_expression') ||
                          this.findChildByType(node, 'pointer_type') ||
                          this.findChildByType(node, 'error_union_type') ||
                          this.findChildByType(node, 'nullable_type') ||
                          this.findChildByType(node, 'optional_type') ||
                          this.findChildByType(node, 'slice_type') ||
                          this.findChildByType(node, 'builtin_type');
    const returnType = returnTypeNode ? this.getNodeText(returnTypeNode) : 'void';

    return `${modifierPrefix}${externPrefix}fn ${name}(${params.join(', ')}) ${returnType}`;
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];

    this.traverseTree(tree.rootNode, (node) => {
      try {
        switch (node.type) {
          case 'struct_declaration':
            this.extractStructRelationships(node, symbols, relationships);
            break;
          case 'const_declaration':
            // COMPOSITION FIX: Also check const declarations for struct definitions
            const structNode = this.findChildByType(node, 'struct_declaration');
            if (structNode) {
              this.extractStructRelationships(node, symbols, relationships);
            }
            break;
          case 'call_expression':
            this.extractFunctionCallRelationships(node, symbols, relationships);
            break;
          case 'import_declaration':
          case 'usingnamespace':
            this.extractImportRelationships(node, symbols, relationships);
            break;
        }
      } catch (error) {
        console.warn(`Error extracting Zig relationship from ${node.type}:`, error);
      }
    });

    return relationships;
  }

  private extractStructRelationships(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    // COMPOSITION FIX: Simplified approach - match struct_declaration nodes to symbols by position
    if (node.type !== 'struct_declaration') return;

    // Find a symbol that matches this struct_declaration by position
    const structSymbol = symbols.find(s =>
      s.kind === SymbolKind.Class &&
      s.startLine === node.startPosition.row + 1 &&
      s.startColumn === node.startPosition.column
    );

    if (!structSymbol) {
      // Try finding by nearby position (within a few lines)
      const nearbySymbol = symbols.find(s =>
        s.kind === SymbolKind.Class &&
        Math.abs(s.startLine - (node.startPosition.row + 1)) <= 2
      );

      if (!nearbySymbol) return;
    }

    const targetSymbol = structSymbol || symbols.find(s =>
      s.kind === SymbolKind.Class &&
      Math.abs(s.startLine - (node.startPosition.row + 1)) <= 2
    );

    if (!targetSymbol) return;

    // COMPOSITION FIX: Look for struct fields that are of other struct types
    this.traverseTree(node, (fieldNode) => {
      if (fieldNode.type === 'container_field') {
        const fieldNameNode = this.findChildByType(fieldNode, 'identifier');
        if (!fieldNameNode) return;

        const fieldName = this.getNodeText(fieldNameNode);

        // COMPOSITION FIX: Enhanced type detection for field types
        let typeNode = this.findChildByType(fieldNode, 'type_expression') ||
                      this.findChildByType(fieldNode, 'builtin_type') ||
                      this.findChildByType(fieldNode, 'slice_type') ||
                      this.findChildByType(fieldNode, 'pointer_type');

        // COMPOSITION FIX: If no complex type found, look for identifier after colon
        if (!typeNode) {
          const colonIndex = fieldNode.children.findIndex(c => c.type === ':');
          if (colonIndex !== -1 && colonIndex + 1 < fieldNode.children.length) {
            typeNode = fieldNode.children[colonIndex + 1];
          }
        }

        if (typeNode) {
          const typeName = this.getNodeText(typeNode).trim();

          // COMPOSITION FIX: Look for referenced symbols that are struct types
          const referencedSymbol = symbols.find(s => s.name === typeName &&
            (s.kind === SymbolKind.Class || s.kind === SymbolKind.Interface || s.kind === SymbolKind.Struct));

          if (referencedSymbol && referencedSymbol.id !== targetSymbol.id) {
            // COMPOSITION FIX: Create composition relationship for struct field types
            const relationshipKind = 'composition' as RelationshipKind;

            relationships.push({
              id: this.generateId(`struct_${relationshipKind}_${typeName}_${fieldName}`, fieldNode.startPosition),
              fromSymbolId: targetSymbol.id,
              toSymbolId: referencedSymbol.id,
              kind: relationshipKind,
              filePath: this.filePath,
              startLine: fieldNode.startPosition.row,
              startColumn: fieldNode.startPosition.column,
              endLine: fieldNode.endPosition.row,
              endColumn: fieldNode.endPosition.column
            });
          }
        }
      }
    });
  }

  private extractFunctionCallRelationships(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    // Extract function call relationships
    let calledFuncName: string | null = null;

    // Check for direct function call (identifier + arguments)
    const funcNameNode = this.findChildByType(node, 'identifier');
    if (funcNameNode) {
      calledFuncName = this.getNodeText(funcNameNode);
    } else {
      // Check for method call (field_expression + arguments)
      const fieldExprNode = this.findChildByType(node, 'field_expression');
      if (fieldExprNode) {
        const identifiers = this.findChildrenByType(fieldExprNode, 'identifier');
        if (identifiers.length >= 2) {
          const methodNameNode = identifiers[1]; // Second identifier is the method name
          calledFuncName = this.getNodeText(methodNameNode);
        }
      }
    }

    if (!calledFuncName) return;

    const calledSymbol = symbols.find(s => s.name === calledFuncName && s.kind === SymbolKind.Function);

    if (calledSymbol) {
      // Find the calling function
      let current = node.parent;
      while (current && current.type !== 'function_declaration' && current.type !== 'function_definition') {
        current = current.parent;
      }

      if (current) {
        const callerNameNode = this.findChildByType(current, 'identifier');
        if (callerNameNode) {
          const callerName = this.getNodeText(callerNameNode);
          const callerSymbol = symbols.find(s => s.name === callerName && s.kind === SymbolKind.Function);

          if (callerSymbol && callerSymbol.id !== calledSymbol.id) {
            relationships.push({
              id: this.generateId(`call_${callerName}_${calledFuncName}`, node.startPosition),
              fromSymbolId: callerSymbol.id,
              toSymbolId: calledSymbol.id,
              kind: RelationshipKind.Calls,
              filePath: this.filePath,
              startLine: node.startPosition.row,
              startColumn: node.startPosition.column,
              endLine: node.endPosition.row,
              endColumn: node.endPosition.column
            });
          }
        }
      }
    }
  }

  private extractImportRelationships(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    // Extract import/module relationships
    // This could be expanded to track cross-file dependencies
  }

  extractTypes(tree: Parser.Tree): Map<string, string> {
    const types = new Map<string, string>();

    this.traverseTree(tree.rootNode, (node) => {
      if (node.type === 'variable_declaration' || node.type === 'const_declaration') {
        const nameNode = this.findChildByType(node, 'identifier');
        const typeNode = this.findChildByType(node, 'type_expression');

        if (nameNode && typeNode) {
          const varName = this.getNodeText(nameNode);
          const varType = this.getNodeText(typeNode);
          types.set(varName, varType);
        }
      }
    });

    return types;
  }

  private extractFromErrorNode(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // GENERIC TYPE FIX: Try to extract meaningful symbols from ERROR nodes
    // Common case: const Container(comptime T: type) = struct
    const nodeText = this.getNodeText(node);

    // Look for partial generic type constructor pattern in fragmented ERROR nodes
    // Pattern: "const Container(" from tree-sitter ERROR fragmentation
    const partialMatch = nodeText.match(/^const\s+(\w+)\s*\($/);

    if (partialMatch) {
      const name = partialMatch[1];

      const functionSymbol = this.createSymbol(node, name, SymbolKind.Function, {
        signature: `fn ${name}(comptime T: type) type`,
        visibility: 'public',
        parentId,
        docComment: this.extractDocumentation(node),
        metadata: { isGenericTypeConstructor: true }
      });
      return functionSymbol;
    }

    return null;
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();

    // Zig type inference based on symbol metadata and signatures
    for (const symbol of symbols) {
      if (symbol.signature) {
        // Extract Zig types from signatures like "const name: i32", "var buffer: []u8"
        const zigTypePattern = /:\s*([\w\[\]!?*]+)/;
        const typeMatch = symbol.signature.match(zigTypePattern);
        if (typeMatch) {
          types.set(symbol.name, typeMatch[1]);
        }
      }

      // Use metadata for Zig-specific types
      if (symbol.metadata?.isErrorType) {
        types.set(symbol.name, 'error');
      }
      if (symbol.metadata?.isTypeAlias) {
        types.set(symbol.name, 'type');
      }
      if (symbol.kind === SymbolKind.Class && !symbol.metadata?.isErrorType) {
        types.set(symbol.name, 'struct');
      }
      if (symbol.kind === SymbolKind.Enum) {
        types.set(symbol.name, 'enum');
      }
    }

    return types;
  }
}