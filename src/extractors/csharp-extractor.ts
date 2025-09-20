import Parser from 'web-tree-sitter';
import { BaseExtractor, Symbol, Relationship, SymbolKind, RelationshipKind } from './base-extractor.js';

/**
 * C# language extractor that handles C#-specific constructs including:
 * - Namespaces and using statements (regular, static, global)
 * - Classes, interfaces, structs, and enums
 * - Methods, constructors, and properties
 * - Fields, events, and delegates
 * - Records and nested types
 * - Attributes and generics
 * - Inheritance and implementation relationships
 */
export class CSharpExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    this.walkTree(tree.rootNode, symbols);
    return symbols;
  }

  private walkTree(node: Parser.SyntaxNode, symbols: Symbol[], parentId?: string) {
    const symbol = this.extractSymbol(node, parentId);
    if (symbol) {
      symbols.push(symbol);
      parentId = symbol.id;
    }

    for (const child of node.children) {
      this.walkTree(child, symbols, parentId);
    }
  }

  protected extractSymbol(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    switch (node.type) {
      case 'namespace_declaration':
        return this.extractNamespace(node, parentId);
      case 'using_directive':
        return this.extractUsing(node, parentId);
      case 'class_declaration':
        return this.extractClass(node, parentId);
      case 'interface_declaration':
        return this.extractInterface(node, parentId);
      case 'struct_declaration':
        return this.extractStruct(node, parentId);
      case 'enum_declaration':
        return this.extractEnum(node, parentId);
      case 'enum_member_declaration':
        return this.extractEnumMember(node, parentId);
      case 'method_declaration':
        return this.extractMethod(node, parentId);
      case 'constructor_declaration':
        return this.extractConstructor(node, parentId);
      case 'property_declaration':
        return this.extractProperty(node, parentId);
      case 'field_declaration':
        return this.extractField(node, parentId);
      case 'event_field_declaration':
        return this.extractEvent(node, parentId);
      case 'delegate_declaration':
        return this.extractDelegate(node, parentId);
      case 'record_declaration':
        return this.extractRecord(node, parentId);
      default:
        return null;
    }
  }

  private extractNamespace(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'qualified_name' || c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const signature = `namespace ${name}`;

    return this.createSymbol(node, name, SymbolKind.Namespace, {
      signature,
      visibility: 'public',
      parentId
    });
  }

  private extractUsing(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Handle different using patterns: using System; using static System.Math; using alias = System.Collections.Generic;
    const nameNode = node.children.find(c =>
      c.type === 'qualified_name' ||
      c.type === 'identifier' ||
      c.type === 'member_access_expression'
    );
    if (!nameNode) return null;

    let fullUsingPath = this.getNodeText(nameNode);

    // Check if it's a static using
    const isStatic = node.children.some(c => c.type === 'static');

    // Check for alias (using alias = namespace)
    const aliasNode = node.children.find(c => c.type === 'name_equals');
    let name = fullUsingPath;

    if (aliasNode) {
      // Extract alias name
      const aliasIdentifier = aliasNode.children.find(c => c.type === 'identifier');
      if (aliasIdentifier) {
        name = this.getNodeText(aliasIdentifier);
      }
    } else {
      // Extract the last part of the namespace for the symbol name
      const parts = fullUsingPath.split('.');
      name = parts[parts.length - 1];
    }

    const signature = isStatic ? `using static ${fullUsingPath}` : `using ${fullUsingPath}`;

    return this.createSymbol(node, name, SymbolKind.Import, {
      signature,
      visibility: 'public',
      parentId
    });
  }

  private extractClass(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Build signature
    let signature = modifiers.length > 0 ? `${modifiers.join(' ')} class ${name}` : `class ${name}`;

    // Handle generic type parameters
    const typeParams = this.extractTypeParameters(node);
    if (typeParams) {
      signature = signature.replace(`class ${name}`, `class ${name}${typeParams}`);
    }

    // Check for inheritance and implementations
    const baseList = this.extractBaseList(node);
    if (baseList.length > 0) {
      signature += ` : ${baseList.join(', ')}`;
    }

    return this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      visibility,
      parentId
    });
  }

  private extractInterface(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Build signature
    let signature = modifiers.length > 0 ? `${modifiers.join(' ')} interface ${name}` : `interface ${name}`;

    // Handle generic type parameters
    const typeParams = this.extractTypeParameters(node);
    if (typeParams) {
      signature = signature.replace(`interface ${name}`, `interface ${name}${typeParams}`);
    }

    // Check for interface inheritance
    const baseList = this.extractBaseList(node);
    if (baseList.length > 0) {
      signature += ` : ${baseList.join(', ')}`;
    }

    return this.createSymbol(node, name, SymbolKind.Interface, {
      signature,
      visibility,
      parentId
    });
  }

  private extractStruct(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Build signature
    let signature = modifiers.length > 0 ? `${modifiers.join(' ')} struct ${name}` : `struct ${name}`;

    // Handle generic type parameters
    const typeParams = this.extractTypeParameters(node);
    if (typeParams) {
      signature = signature.replace(`struct ${name}`, `struct ${name}${typeParams}`);
    }

    // Check for interface implementations
    const baseList = this.extractBaseList(node);
    if (baseList.length > 0) {
      signature += ` : ${baseList.join(', ')}`;
    }

    return this.createSymbol(node, name, SymbolKind.Struct, {
      signature,
      visibility,
      parentId
    });
  }

  private extractEnum(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Build signature
    let signature = modifiers.length > 0 ? `${modifiers.join(' ')} enum ${name}` : `enum ${name}`;

    // Check for base type (e.g., : byte)
    const baseList = this.extractBaseList(node);
    if (baseList.length > 0) {
      signature += ` : ${baseList[0]}`;
    }

    return this.createSymbol(node, name, SymbolKind.Enum, {
      signature,
      visibility,
      parentId
    });
  }

  private extractEnumMember(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Build signature - include value if present
    let signature = name;
    const equalsIndex = node.children.findIndex(c => c.type === '=');
    if (equalsIndex !== -1 && equalsIndex < node.children.length - 1) {
      const valueNodes = node.children.slice(equalsIndex + 1);
      const value = valueNodes.map(n => this.getNodeText(n)).join('').trim();
      if (value) {
        signature += ` = ${value}`;
      }
    }

    return this.createSymbol(node, name, SymbolKind.EnumMember, {
      signature,
      visibility: 'public', // Enum members are always public in C#
      parentId
    });
  }

  private extractMethod(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Get return type
    const returnType = this.extractReturnType(node) || 'void';

    // Get parameters
    const paramList = node.children.find(c => c.type === 'parameter_list');
    const params = paramList ? this.getNodeText(paramList) : '()';

    // Handle generic type parameters on the method
    const typeParams = this.extractTypeParameters(node);

    // Build signature
    const modifierStr = modifiers.length > 0 ? `${modifiers.join(' ')} ` : '';
    const typeParamStr = typeParams ? `${typeParams} ` : '';
    const signature = `${modifierStr}${typeParamStr}${returnType} ${name}${params}`;

    return this.createSymbol(node, name, SymbolKind.Method, {
      signature,
      visibility,
      parentId
    });
  }

  private extractConstructor(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers, node.type);

    // Get parameters
    const paramList = node.children.find(c => c.type === 'parameter_list');
    const params = paramList ? this.getNodeText(paramList) : '()';

    // Build signature (constructors don't have return types)
    const modifierStr = modifiers.length > 0 ? `${modifiers.join(' ')} ` : '';
    const signature = `${modifierStr}${name}${params}`;

    return this.createSymbol(node, name, SymbolKind.Constructor, {
      signature,
      visibility,
      parentId
    });
  }

  private extractProperty(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Get property type
    const type = this.extractPropertyType(node) || 'var';

    // Get accessor list (get/set)
    const accessorList = node.children.find(c => c.type === 'accessor_list');
    let accessors = '';
    if (accessorList) {
      accessors = ' ' + this.getNodeText(accessorList);
    } else {
      // Expression-bodied property
      const arrowIndex = node.children.findIndex(c => c.type === '=>');
      if (arrowIndex !== -1) {
        accessors = ' => ' + node.children.slice(arrowIndex + 1).map(n => this.getNodeText(n)).join('').trim();
      }
    }

    // Build signature
    const modifierStr = modifiers.length > 0 ? `${modifiers.join(' ')} ` : '';
    const signature = `${modifierStr}${type} ${name}${accessors}`;

    return this.createSymbol(node, name, SymbolKind.Property, {
      signature,
      visibility,
      parentId
    });
  }

  private extractField(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Get field type
    const type = this.extractFieldType(node) || 'var';

    // Get variable declaration and then variable declarator(s)
    const varDeclaration = node.children.find(c => c.type === 'variable_declaration');
    if (!varDeclaration) return null;

    const declarators = varDeclaration.children.filter(c => c.type === 'variable_declarator');

    // For now, handle the first declarator (we could extend to handle multiple)
    if (declarators.length === 0) return null;

    const declarator = declarators[0];
    const nameNode = declarator.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Check if it's a constant (const or static readonly)
    const isConstant = modifiers.includes('const') ||
                     (modifiers.includes('static') && modifiers.includes('readonly'));
    const symbolKind = isConstant ? SymbolKind.Constant : SymbolKind.Field;

    // Get initializer if present
    const equalsIndex = declarator.children.findIndex(c => c.type === '=');
    let initializer = '';
    if (equalsIndex !== -1) {
      const initNodes = declarator.children.slice(equalsIndex + 1);
      initializer = ` = ${initNodes.map(n => this.getNodeText(n)).join('').trim()}`;
    }

    // Build signature
    const modifierStr = modifiers.length > 0 ? `${modifiers.join(' ')} ` : '';
    const signature = `${modifierStr}${type} ${name}${initializer}`;

    return this.createSymbol(node, name, symbolKind, {
      signature,
      visibility,
      parentId
    });
  }

  private extractEvent(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // For event_field_declaration, the structure is:
    // modifier* event variable_declaration
    const varDeclaration = node.children.find(c => c.type === 'variable_declaration');
    if (!varDeclaration) return null;

    const varDeclarator = varDeclaration.children.find(c => c.type === 'variable_declarator');
    if (!varDeclarator) return null;

    const nameNode = varDeclarator.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Get event type (first child of variable_declaration that's not variable_declarator)
    const typeNode = varDeclaration.children.find(c => c.type !== 'variable_declarator');
    const type = typeNode ? this.getNodeText(typeNode) : 'EventHandler';

    // Build signature
    const modifierStr = modifiers.length > 0 ? `${modifiers.join(' ')} ` : '';
    const signature = `${modifierStr}event ${type} ${name}`;

    return this.createSymbol(node, name, SymbolKind.Event, {
      signature,
      visibility,
      parentId
    });
  }

  private extractDelegate(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Get return type
    const returnType = this.extractReturnType(node) || 'void';

    // Get parameters
    const paramList = node.children.find(c => c.type === 'parameter_list');
    const params = paramList ? this.getNodeText(paramList) : '()';

    // Handle generic type parameters
    const typeParams = this.extractTypeParameters(node);

    // Build signature
    const modifierStr = modifiers.length > 0 ? `${modifiers.join(' ')} ` : '';
    const nameWithTypeParams = typeParams ? `${name}${typeParams}` : name;
    const signature = `${modifierStr}delegate ${returnType} ${nameWithTypeParams}${params}`;

    return this.createSymbol(node, name, SymbolKind.Delegate, {
      signature,
      visibility,
      parentId
    });
  }

  private extractRecord(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Determine if it's a record struct
    const isStruct = modifiers.includes('struct') ||
                    node.children.some(c => c.type === 'struct');

    // Build signature
    const recordType = isStruct ? 'record struct' : 'record';
    let signature = modifiers.length > 0 ? `${modifiers.join(' ')} ${recordType} ${name}` : `${recordType} ${name}`;

    // Handle record parameters
    const paramList = node.children.find(c => c.type === 'parameter_list');
    if (paramList) {
      signature += this.getNodeText(paramList);
    }

    const symbolKind = isStruct ? SymbolKind.Struct : SymbolKind.Class;

    return this.createSymbol(node, name, symbolKind, {
      signature,
      visibility,
      parentId
    });
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    // TODO: Implement relationship extraction
    return [];
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    // TODO: Implement type inference
    return new Map();
  }

  // Helper methods for C#-specific parsing
  private extractModifiers(node: Parser.SyntaxNode): string[] {
    // Extract attributes and modifiers
    const attributes: string[] = [];
    const modifiers: string[] = [];

    // Extract attributes
    const attributeLists = node.children.filter(c => c.type === 'attribute_list');
    for (const attrList of attributeLists) {
      attributes.push(this.getNodeText(attrList));
    }

    // Extract modifiers
    const modifierNodes = node.children.filter(c => c.type === 'modifier');
    for (const modifier of modifierNodes) {
      modifiers.push(this.getNodeText(modifier));
    }

    // Combine attributes and modifiers
    return [...attributes, ...modifiers];
  }

  private determineVisibility(modifiers: string[], nodeType?: string): 'public' | 'private' | 'protected' | 'internal' {
    if (modifiers.includes('public')) return 'public';
    if (modifiers.includes('private')) return 'private';
    if (modifiers.includes('protected')) return 'protected';
    if (modifiers.includes('internal')) return 'internal';

    // Special cases for default visibility
    if (nodeType === 'constructor_declaration') {
      return 'public'; // Constructors default to public when in public classes
    }

    return 'internal'; // Default visibility in C#
  }

  private extractBaseList(node: Parser.SyntaxNode): string[] {
    const baseList = node.children.find(c => c.type === 'base_list');
    if (!baseList) return [];

    return baseList.children
      .filter(c => c.type !== ':' && c.type !== ',')
      .map(c => this.getNodeText(c));
  }

  private extractTypeParameters(node: Parser.SyntaxNode): string | null {
    const typeParams = node.children.find(c => c.type === 'type_parameter_list');
    if (!typeParams) return null;

    return this.getNodeText(typeParams);
  }

  private extractReturnType(node: Parser.SyntaxNode): string | null {
    // Find return type - usually comes before the method name
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const nameIndex = node.children.indexOf(nameNode);
    const returnTypeNode = node.children.slice(0, nameIndex).find(c =>
      c.type === 'predefined_type' ||
      c.type === 'identifier' ||
      c.type === 'qualified_name' ||
      c.type === 'generic_name' ||
      c.type === 'array_type' ||
      c.type === 'nullable_type'
    );

    return returnTypeNode ? this.getNodeText(returnTypeNode) : null;
  }

  private extractPropertyType(node: Parser.SyntaxNode): string | null {
    // Property type comes before the property name
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const nameIndex = node.children.indexOf(nameNode);
    const typeNode = node.children.slice(0, nameIndex).find(c =>
      c.type === 'predefined_type' ||
      c.type === 'identifier' ||
      c.type === 'qualified_name' ||
      c.type === 'generic_name' ||
      c.type === 'array_type' ||
      c.type === 'nullable_type'
    );

    return typeNode ? this.getNodeText(typeNode) : null;
  }

  private extractFieldType(node: Parser.SyntaxNode): string | null {
    // Field type is the first child of variable_declaration
    const varDeclaration = node.children.find(c => c.type === 'variable_declaration');
    if (!varDeclaration) return null;

    const typeNode = varDeclaration.children.find(c =>
      c.type === 'predefined_type' ||
      c.type === 'identifier' ||
      c.type === 'qualified_name' ||
      c.type === 'generic_name' ||
      c.type === 'array_type' ||
      c.type === 'nullable_type'
    );

    return typeNode ? this.getNodeText(typeNode) : null;
  }

  private extractEventType(node: Parser.SyntaxNode): string | null {
    // Event type comes after 'event' keyword
    const eventKeyword = node.children.find(c => c.type === 'event');
    if (!eventKeyword) return null;

    const eventIndex = node.children.indexOf(eventKeyword);
    const typeNode = node.children.slice(eventIndex + 1).find(c =>
      c.type === 'predefined_type' ||
      c.type === 'identifier' ||
      c.type === 'qualified_name' ||
      c.type === 'generic_name'
    );

    return typeNode ? this.getNodeText(typeNode) : null;
  }
}