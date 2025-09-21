import { Parser } from 'web-tree-sitter';
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
      case 'destructor_declaration':
        return this.extractDestructor(node, parentId);
      case 'operator_declaration':
        return this.extractOperator(node, parentId);
      case 'conversion_operator_declaration':
        return this.extractConversionOperator(node, parentId);
      case 'indexer_declaration':
        return this.extractIndexer(node, parentId);
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

    // Handle where clauses (type parameter constraints)
    const whereClauses = node.children.filter(c => c.type === 'type_parameter_constraints_clause');
    if (whereClauses.length > 0) {
      const whereTexts = whereClauses.map(clause => this.getNodeText(clause));
      signature += ' ' + whereTexts.join(' ');
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

    // Handle where clauses (type parameter constraints)
    const whereClauses = node.children.filter(c => c.type === 'type_parameter_constraints_clause');
    if (whereClauses.length > 0) {
      const whereTexts = whereClauses.map(clause => this.getNodeText(clause));
      signature += ' ' + whereTexts.join(' ');
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
    // Find method name identifier - comes before parameter_list (may have type_parameter_list in between)
    const paramListIndex = node.children.findIndex(c => c.type === 'parameter_list');
    if (paramListIndex === -1) return null;

    // Look backwards from parameter_list to find the method name identifier
    let nameNode: Parser.SyntaxNode | null = null;
    for (let i = paramListIndex - 1; i >= 0; i--) {
      const child = node.children[i];
      if (child.type === 'identifier') {
        nameNode = child;
        break;
      }
    }

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
    let signature = `${modifierStr}${typeParamStr}${returnType} ${name}${params}`;

    // Handle expression-bodied method (=> expression)
    const arrowClause = node.children.find(c => c.type === 'arrow_expression_clause');
    if (arrowClause) {
      signature += ' ' + this.getNodeText(arrowClause);
    }

    // Handle where clauses (type parameter constraints)
    const whereClauses = node.children.filter(c => c.type === 'type_parameter_constraints_clause');
    if (whereClauses.length > 0) {
      const whereTexts = whereClauses.map(clause => this.getNodeText(clause));
      signature += ' ' + whereTexts.join(' ');
    }

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
      const arrowClause = node.children.find(c => c.type === 'arrow_expression_clause');
      if (arrowClause) {
        accessors = ' ' + this.getNodeText(arrowClause);
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
    // For delegates, we need to find the delegate name identifier, not the return type identifier
    // The structure is: modifiers delegate returnType delegateName<typeParams>(params)
    // We need to find the identifier that comes after the return type

    // First, find the 'delegate' keyword
    const delegateKeyword = node.children.find(c => c.type === 'delegate');
    if (!delegateKeyword) return null;

    const delegateIndex = node.children.indexOf(delegateKeyword);

    // Find identifiers after the delegate keyword
    const identifiersAfterDelegate = node.children.slice(delegateIndex + 1).filter(c => c.type === 'identifier');

    // The delegate name is typically the last identifier before type_parameter_list or parameter_list
    // For "delegate TResult Func<T>(T input)", identifiers are: ["TResult", "Func"]
    // For "delegate void EventHandler<T>(T data)", identifiers are: ["EventHandler"]
    let nameNode: Parser.SyntaxNode | undefined;

    if (identifiersAfterDelegate.length === 1) {
      // Simple case: delegate void EventHandler<T>(T data)
      nameNode = identifiersAfterDelegate[0];
    } else if (identifiersAfterDelegate.length >= 2) {
      // Complex case: delegate TResult Func<T>(T input) - the name is the second identifier
      nameNode = identifiersAfterDelegate[1];
    }

    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Get return type - for delegates, it's the first type-like node after 'delegate'
    let returnType = 'void';
    for (const child of node.children.slice(delegateIndex + 1)) {
      if (child.type === 'predefined_type' ||
          child.type === 'identifier' ||
          child.type === 'qualified_name' ||
          child.type === 'generic_name') {
        returnType = this.getNodeText(child);
        break;
      }
    }

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

    // Handle inheritance (base_list)
    const baseList = node.children.find(c => c.type === 'base_list');
    if (baseList) {
      signature += ' ' + this.getNodeText(baseList);
    }

    const symbolKind = isStruct ? SymbolKind.Struct : SymbolKind.Class;

    return this.createSymbol(node, name, symbolKind, {
      signature,
      visibility,
      parentId
    });
  }

  private extractDestructor(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Find class name identifier (Child 1)
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const className = this.getNodeText(nameNode);
    const name = `~${className}`;

    // Get parameters
    const paramList = node.children.find(c => c.type === 'parameter_list');
    const params = paramList ? this.getNodeText(paramList) : '()';

    // Build signature
    const signature = `~${className}${params}`;

    return this.createSymbol(node, name, SymbolKind.Method, {
      signature,
      visibility: 'protected', // Destructors are implicitly protected
      parentId
    });
  }

  private extractOperator(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Find operator symbol (Child 4: +, -, ==, !=, etc.)
    const operatorSymbol = node.children.find(c =>
      c.text === '+' || c.text === '-' || c.text === '*' || c.text === '/' ||
      c.text === '==' || c.text === '!=' || c.text === '<' || c.text === '>' ||
      c.text === '<=' || c.text === '>=' || c.text === '!' || c.text === '~' ||
      c.text === '++' || c.text === '--' || c.text === '%' || c.text === '&' ||
      c.text === '|' || c.text === '^' || c.text === '<<' || c.text === '>>' ||
      c.text === 'true' || c.text === 'false'
    );

    if (!operatorSymbol) return null;

    const name = `operator ${operatorSymbol.text}`;
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Find return type (before 'operator' keyword)
    const operatorKeywordIndex = node.children.findIndex(c => c.text === 'operator');
    const returnTypeNode = node.children.slice(0, operatorKeywordIndex).find(c =>
      c.type === 'predefined_type' ||
      c.type === 'identifier' ||
      c.type === 'generic_name'
    );
    const returnType = returnTypeNode ? this.getNodeText(returnTypeNode) : 'void';

    // Get parameters
    const paramList = node.children.find(c => c.type === 'parameter_list');
    const params = paramList ? this.getNodeText(paramList) : '()';

    // Build signature
    const modifierStr = modifiers.length > 0 ? `${modifiers.join(' ')} ` : '';
    let signature = `${modifierStr}${returnType} operator ${operatorSymbol.text}${params}`;

    // Handle expression-bodied operator
    const arrowClause = node.children.find(c => c.type === 'arrow_expression_clause');
    if (arrowClause) {
      signature += ' ' + this.getNodeText(arrowClause);
    }

    return this.createSymbol(node, name, SymbolKind.Method, {
      signature,
      visibility,
      parentId
    });
  }

  private extractConversionOperator(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Find conversion type (implicit/explicit)
    const conversionType = node.children.find(c => c.text === 'implicit' || c.text === 'explicit');
    if (!conversionType) return null;

    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Find target type (after 'operator' keyword)
    const operatorKeywordIndex = node.children.findIndex(c => c.text === 'operator');
    const targetTypeNode = node.children.slice(operatorKeywordIndex + 1).find(c =>
      c.type === 'predefined_type' ||
      c.type === 'identifier' ||
      c.type === 'generic_name'
    );
    const targetType = targetTypeNode ? this.getNodeText(targetTypeNode) : 'unknown';

    const name = `${conversionType.text} operator ${targetType}`;

    // Get parameters
    const paramList = node.children.find(c => c.type === 'parameter_list');
    const params = paramList ? this.getNodeText(paramList) : '()';

    // Build signature
    const modifierStr = modifiers.length > 0 ? `${modifiers.join(' ')} ` : '';
    let signature = `${modifierStr}${conversionType.text} operator ${targetType}${params}`;

    // Handle expression-bodied operator
    const arrowClause = node.children.find(c => c.type === 'arrow_expression_clause');
    if (arrowClause) {
      signature += ' ' + this.getNodeText(arrowClause);
    }

    return this.createSymbol(node, name, SymbolKind.Method, {
      signature,
      visibility,
      parentId
    });
  }

  private extractIndexer(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const modifiers = this.extractModifiers(node);
    const visibility = this.determineVisibility(modifiers);

    // Find return type (Child 1)
    const returnTypeNode = node.children.find(c =>
      c.type === 'predefined_type' ||
      c.type === 'identifier' ||
      c.type === 'generic_name'
    );
    const returnType = returnTypeNode ? this.getNodeText(returnTypeNode) : 'object';

    // Get bracketed parameters (Child 3)
    const bracketedParams = node.children.find(c => c.type === 'bracketed_parameter_list');
    const params = bracketedParams ? this.getNodeText(bracketedParams) : '[object index]';

    const name = `this${params}`;

    // Build signature
    const modifierStr = modifiers.length > 0 ? `${modifiers.join(' ')} ` : '';
    const signature = `${modifierStr}${returnType} this${params}`;

    return this.createSymbol(node, name, SymbolKind.Property, {
      signature,
      visibility,
      parentId
    });
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];
    const symbolMap = new Map(symbols.map(s => [s.name, s]));

    const visitNode = (node: Parser.SyntaxNode) => {
      switch (node.type) {
        case 'class_declaration':
        case 'interface_declaration':
        case 'struct_declaration':
          this.extractInheritanceRelationships(node, symbols, relationships);
          break;
      }

      for (const child of node.children) {
        visitNode(child);
      }
    };

    visitNode(tree.rootNode);
    return relationships;
  }

  private extractInheritanceRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    // Find the current symbol (class/interface/struct)
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return;

    const currentSymbolName = this.getNodeText(nameNode);
    const currentSymbol = symbols.find(s => s.name === currentSymbolName);
    if (!currentSymbol) return;

    // Find base_list (inheritance/implementation)
    const baseList = node.children.find(c => c.type === 'base_list');
    if (!baseList) return;

    // Extract base types
    const baseTypes = baseList.children
      .filter(c => c.type !== ':' && c.type !== ',')
      .map(c => this.getNodeText(c));

    for (const baseTypeName of baseTypes) {
      const baseSymbol = symbols.find(s => s.name === baseTypeName);
      if (baseSymbol) {
        // Determine relationship kind based on base type
        const relationshipKind = baseSymbol.kind === SymbolKind.Interface
          ? RelationshipKind.Implements
          : RelationshipKind.Extends;

        // Create relationship with both old and new property names for compatibility
        const relationship: any = {
          fromSymbolId: currentSymbol.id,
          toSymbolId: baseSymbol.id,
          // Add aliases for test compatibility
          fromId: currentSymbol.id,
          toId: baseSymbol.id,
          kind: relationshipKind.toLowerCase(), // Tests expect lowercase
          filePath: this.filePath,
          lineNumber: node.startPosition.row + 1,
          confidence: 1.0
        };

        relationships.push(relationship);
      }
    }
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const typeMap = new Map<string, string>();

    for (const symbol of symbols) {
      let inferredType: string | null = null;

      switch (symbol.kind) {
        case SymbolKind.Method:
        case SymbolKind.Function:
          inferredType = this.inferMethodReturnType(symbol);
          break;
        case SymbolKind.Property:
          inferredType = this.inferPropertyType(symbol);
          break;
        case SymbolKind.Field:
        case SymbolKind.Constant:
          inferredType = this.inferFieldType(symbol);
          break;
        case SymbolKind.Variable:
          inferredType = this.inferVariableType(symbol);
          break;
      }

      if (inferredType) {
        typeMap.set(symbol.id, inferredType);
      }
    }

    return typeMap;
  }

  private inferMethodReturnType(symbol: Symbol): string | null {
    if (!symbol.signature) return null;

    // Parse method signature to extract return type
    // Examples:
    // "public string GetName() => "test""
    // "public Task<List<User>> GetUsersAsync() => null"
    // "public void ProcessData<T>(T data) where T : class { }"

    // Remove modifiers and method name to isolate return type
    const signature = symbol.signature;

    // Extract return type by finding the type that comes before the method name
    // Pattern: [modifiers] [returnType] [methodName][typeParams?]([params])

    // Split by whitespace and find the return type
    const parts = signature.split(/\s+/);
    let returnType: string | null = null;

    // Find the method name position
    const methodNameIndex = parts.findIndex(part => part.includes(symbol.name));

    if (methodNameIndex > 0) {
      // The return type is typically the part just before the method name
      // Skip modifiers like public, static, async, etc.
      const modifiers = ['public', 'private', 'protected', 'internal', 'static', 'virtual', 'override', 'abstract', 'async', 'sealed'];

      for (let i = methodNameIndex - 1; i >= 0; i--) {
        const part = parts[i];
        if (!modifiers.includes(part) && part !== '') {
          returnType = part;
          break;
        }
      }
    }

    return returnType;
  }

  private inferPropertyType(symbol: Symbol): string | null {
    if (!symbol.signature) return null;

    // Parse property signature to extract type
    // Example: "public string Name { get; set; }"
    const parts = symbol.signature.split(/\s+/);
    const modifiers = ['public', 'private', 'protected', 'internal', 'static', 'virtual', 'override', 'abstract'];

    // Find the first non-modifier part which should be the type
    for (const part of parts) {
      if (!modifiers.includes(part) && part !== '') {
        return part;
      }
    }

    return null;
  }

  private inferFieldType(symbol: Symbol): string | null {
    if (!symbol.signature) return null;

    // Parse field signature to extract type
    // Example: "private readonly string _name"
    const parts = symbol.signature.split(/\s+/);
    const modifiers = ['public', 'private', 'protected', 'internal', 'static', 'readonly', 'const', 'volatile'];

    // Find the first non-modifier part which should be the type
    for (const part of parts) {
      if (!modifiers.includes(part) && part !== '') {
        return part;
      }
    }

    return null;
  }

  private inferVariableType(symbol: Symbol): string | null {
    // For variables, we'd need more context from the AST
    // For now, return null as it's not covered in the test
    return null;
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
    // Find method name identifier - comes before parameter_list (may have type_parameter_list in between)
    const paramListIndex = node.children.findIndex(c => c.type === 'parameter_list');
    if (paramListIndex === -1) return null;

    // Look backwards from parameter_list to find the method name identifier
    let nameNode: Parser.SyntaxNode | null = null;
    for (let i = paramListIndex - 1; i >= 0; i--) {
      const child = node.children[i];
      if (child.type === 'identifier') {
        nameNode = child;
        break;
      }
    }

    if (!nameNode) return null;

    const nameIndex = node.children.indexOf(nameNode);
    // Look for return type, but exclude modifiers
    const returnTypeNode = node.children.slice(0, nameIndex).find(c =>
      c.type === 'predefined_type' ||
      c.type === 'identifier' ||
      c.type === 'qualified_name' ||
      c.type === 'generic_name' ||
      c.type === 'array_type' ||
      c.type === 'nullable_type' ||
      c.type === 'tuple_type'
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