import Parser from 'web-tree-sitter';
import { BaseExtractor, Symbol, Relationship, SymbolKind, RelationshipKind } from './base-extractor.js';

/**
 * Java language extractor that handles Java-specific constructs including:
 * - Classes, interfaces, and enums
 * - Methods, constructors, and fields
 * - Packages and imports (regular, static, wildcard)
 * - Generics and type parameters
 * - Nested classes and annotations
 * - Inheritance and implementation relationships
 */
export class JavaExtractor extends BaseExtractor {
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
      case 'package_declaration':
        return this.extractPackage(node, parentId);
      case 'import_declaration':
        return this.extractImport(node, parentId);
      case 'class_declaration':
        return this.extractClass(node, parentId);
      case 'interface_declaration':
        return this.extractInterface(node, parentId);
      case 'method_declaration':
        return this.extractMethod(node, parentId);
      case 'constructor_declaration':
        return this.extractConstructor(node, parentId);
      case 'field_declaration':
        return this.extractField(node, parentId);
      case 'enum_declaration':
        return this.extractEnum(node, parentId);
      case 'enum_constant':
        return this.extractEnumConstant(node, parentId);
      case 'annotation_type_declaration':
        return this.extractAnnotation(node, parentId);
      default:
        return null;
    }
  }

  private extractPackage(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const scopedId = node.children.find(c => c.type === 'scoped_identifier');
    if (!scopedId) return null;

    const packageName = this.getNodeText(scopedId);
    const signature = `package ${packageName}`;

    return this.createSymbol(node, packageName, SymbolKind.Namespace, {
      signature,
      visibility: 'public',
      parentId
    });
  }

  private extractImport(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const scopedId = node.children.find(c => c.type === 'scoped_identifier');
    if (!scopedId) return null;

    let fullImportPath = this.getNodeText(scopedId);

    // Check if it's a static import
    const isStatic = node.children.some(c => c.type === 'static');

    // Check for wildcard imports (asterisk node)
    const asteriskNode = node.children.find(c => c.type === 'asterisk');
    if (asteriskNode) {
      fullImportPath += '.*'; // Append the wildcard
    }

    // Extract the class/member name (last part after the last dot)
    const parts = fullImportPath.split('.');
    const name = parts[parts.length - 1];

    // Handle wildcard imports
    if (name === '*') {
      const packageName = parts[parts.length - 2];
      const signature = isStatic ? `import static ${fullImportPath}` : `import ${fullImportPath}`;
      return this.createSymbol(node, packageName, SymbolKind.Import, {
        signature,
        visibility: 'public',
        parentId
      });
    }

    const signature = isStatic ? `import static ${fullImportPath}` : `import ${fullImportPath}`;

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
    const superclass = this.extractSuperclass(node);
    const interfaces = this.extractImplementedInterfaces(node);

    if (superclass) {
      signature += ` extends ${superclass}`;
    }

    if (interfaces.length > 0) {
      signature += ` implements ${interfaces.join(', ')}`;
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

    // Check for interface inheritance (extends)
    const superInterfaces = this.extractExtendedInterfaces(node);
    if (superInterfaces.length > 0) {
      signature += ` extends ${superInterfaces.join(', ')}`;
    }

    // Handle generic type parameters
    const typeParams = this.extractTypeParameters(node);
    if (typeParams) {
      signature = signature.replace(`interface ${name}`, `interface ${name}${typeParams}`);
    }

    return this.createSymbol(node, name, SymbolKind.Interface, {
      signature,
      visibility,
      parentId
    });
  }

  private extractMethod(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Get return type (comes before the method name in the AST)
    const nameIndex = node.children.indexOf(nameNode);
    const returnTypeNode = node.children.slice(0, nameIndex).find(c =>
      c.type === 'type_identifier' ||
      c.type === 'generic_type' ||
      c.type === 'void_type' ||
      c.type === 'array_type' ||
      c.type === 'primitive_type' ||
      c.type === 'integral_type' ||
      c.type === 'floating_point_type' ||
      c.type === 'boolean_type'
    );
    const returnType = returnTypeNode ? this.getNodeText(returnTypeNode) : 'void';

    // Get parameters
    const paramList = node.children.find(c => c.type === 'formal_parameters');
    const params = paramList ? this.extractParameters(paramList) : '()';

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
    const visibility = this.determineVisibility(modifiers);

    // Get parameters
    const paramList = node.children.find(c => c.type === 'formal_parameters');
    const params = paramList ? this.extractParameters(paramList) : '()';

    // Build signature (constructors don't have return types)
    const modifierStr = modifiers.length > 0 ? `${modifiers.join(' ')} ` : '';
    const signature = `${modifierStr}${name}${params}`;

    return this.createSymbol(node, name, SymbolKind.Constructor, {
      signature,
      visibility,
      parentId
    });
  }

  private extractField(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Get type
    const typeNode = node.children.find(c =>
      c.type === 'type_identifier' ||
      c.type === 'generic_type' ||
      c.type === 'array_type' ||
      c.type === 'primitive_type'
    );
    const type = typeNode ? this.getNodeText(typeNode) : 'unknown';

    // Get variable declarator(s) - there can be multiple fields in one declaration
    const declarators = node.children.filter(c => c.type === 'variable_declarator');

    // For now, handle the first declarator (we could extend to handle multiple)
    if (declarators.length === 0) return null;

    const declarator = declarators[0];
    const nameNode = declarator.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Check if it's a constant (static final)
    const isConstant = modifiers.includes('static') && modifiers.includes('final');
    const symbolKind = isConstant ? SymbolKind.Constant : SymbolKind.Property;

    // Get initializer if present
    const assignIndex = declarator.children.findIndex(c => c.type === '=');
    let initializer = '';
    if (assignIndex !== -1) {
      const initNodes = declarator.children.slice(assignIndex + 1);
      initializer = ` = ${initNodes.map(n => this.getNodeText(n)).join('')}`;
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

  private extractEnum(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Build signature
    const modifierStr = modifiers.length > 0 ? `${modifiers.join(' ')} ` : '';
    const signature = `${modifierStr}enum ${name}`;

    return this.createSymbol(node, name, SymbolKind.Enum, {
      signature,
      visibility,
      parentId
    });
  }

  private extractEnumConstant(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Build signature - include arguments if present
    let signature = name;
    const argumentList = node.children.find(c => c.type === 'argument_list');
    if (argumentList) {
      signature += this.getNodeText(argumentList);
    }

    return this.createSymbol(node, name, SymbolKind.EnumMember, {
      signature,
      visibility: 'public', // Enum constants are always public in Java
      parentId
    });
  }

  private extractAnnotation(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Build signature
    const modifierStr = modifiers.length > 0 ? `${modifiers.join(' ')} ` : '';
    const signature = `${modifierStr}@interface ${name}`;

    return this.createSymbol(node, name, SymbolKind.Interface, {
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

  // Helper methods for Java-specific parsing
  private extractModifiers(node: Parser.SyntaxNode): string[] {
    const modifiersNode = node.children.find(c => c.type === 'modifiers');
    if (!modifiersNode) return [];

    return modifiersNode.children
      .map(c => this.getNodeText(c));
  }

  private determineVisibility(modifiers: string[]): 'public' | 'private' | 'protected' | 'package' {
    if (modifiers.includes('public')) return 'public';
    if (modifiers.includes('private')) return 'private';
    if (modifiers.includes('protected')) return 'protected';
    return 'package'; // Default visibility in Java
  }

  private extractSuperclass(node: Parser.SyntaxNode): string | null {
    const superclassNode = node.children.find(c => c.type === 'superclass');
    if (!superclassNode) return null;

    const typeNode = superclassNode.children.find(c => c.type === 'type_identifier' || c.type === 'generic_type');
    return typeNode ? this.getNodeText(typeNode) : null;
  }

  private extractImplementedInterfaces(node: Parser.SyntaxNode): string[] {
    const interfacesNode = node.children.find(c => c.type === 'super_interfaces');
    if (!interfacesNode) return [];

    const typeListNode = interfacesNode.children.find(c => c.type === 'type_list');
    if (!typeListNode) return [];

    return typeListNode.children
      .filter(c => c.type === 'type_identifier' || c.type === 'generic_type')
      .map(c => this.getNodeText(c));
  }

  private extractExtendedInterfaces(node: Parser.SyntaxNode): string[] {
    const extendsNode = node.children.find(c => c.type === 'extends_interfaces');
    if (!extendsNode) return [];

    const typeListNode = extendsNode.children.find(c => c.type === 'type_list');
    if (!typeListNode) return [];

    return typeListNode.children
      .filter(c => c.type === 'type_identifier' || c.type === 'generic_type')
      .map(c => this.getNodeText(c));
  }

  private extractTypeParameters(node: Parser.SyntaxNode): string | null {
    const typeParamsNode = node.children.find(c => c.type === 'type_parameters');
    if (!typeParamsNode) return null;

    return this.getNodeText(typeParamsNode);
  }

  private extractParameters(paramList: Parser.SyntaxNode): string {
    // Get the full parameter list text
    return this.getNodeText(paramList);
  }
}