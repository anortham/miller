import { Parser } from 'web-tree-sitter';
import {
  BaseExtractor,
  Symbol,
  SymbolKind,
  Relationship,
  RelationshipKind
} from './base-extractor.js';

export class SwiftExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    const visitNode = (node: Parser.SyntaxNode, parentId?: string) => {
      if (!node || !node.type) {
        return; // Skip invalid nodes
      }

      let symbol: Symbol | null = null;

      switch (node.type) {
        case 'class_declaration':
          symbol = this.extractClass(node, parentId);
          break;
        case 'struct_declaration':
          symbol = this.extractStruct(node, parentId);
          break;
        case 'protocol_declaration':
          symbol = this.extractProtocol(node, parentId);
          break;
        case 'enum_declaration':
          symbol = this.extractEnum(node, parentId);
          break;
        case 'enum_case_declaration':
          this.extractEnumCases(node, symbols, parentId);
          break;
        case 'enum_entry':
          symbol = this.extractEnumCase(node, parentId);
          break;
        case 'function_declaration':
          symbol = this.extractFunction(node, parentId);
          break;
        case 'protocol_function_declaration':
          symbol = this.extractProtocolFunction(node, parentId);
          break;
        case 'protocol_property_declaration':
          symbol = this.extractProtocolProperty(node, parentId);
          break;
        case 'associatedtype_declaration':
          symbol = this.extractAssociatedType(node, parentId);
          break;
        case 'subscript_declaration':
          symbol = this.extractSubscript(node, parentId);
          break;
        case 'init_declaration':
          symbol = this.extractInitializer(node, parentId);
          break;
        case 'deinit_declaration':
          symbol = this.extractDeinitializer(node, parentId);
          break;
        case 'variable_declaration':
          symbol = this.extractVariable(node, parentId);
          break;
        case 'property_declaration':
          symbol = this.extractProperty(node, parentId);
          break;
        case 'extension_declaration':
          symbol = this.extractExtension(node, parentId);
          break;
        case 'import_declaration':
          symbol = this.extractImport(node, parentId);
          break;
        case 'typealias_declaration':
          symbol = this.extractTypeAlias(node, parentId);
          break;
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
      console.warn('Swift parsing failed:', error);
    }
    return symbols;
  }

  private extractClass(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // Swift parser uses class_declaration for classes, enums, structs, and extensions
    const nameNode = node.children.find(c => c.type === 'type_identifier' || c.type === 'user_type');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownClass';

    // Check what type this actually is
    const isEnum = node.children.some(c => c.type === 'enum');
    const isStruct = node.children.some(c => c.type === 'struct');
    const isExtension = node.children.some(c => c.type === 'extension');

    const modifiers = this.extractModifiers(node);
    const genericParams = this.extractGenericParameters(node);
    const inheritance = this.extractInheritance(node);

    // Determine the correct keyword and symbol kind
    let keyword = 'class';
    let symbolKind = SymbolKind.Class;

    if (isEnum) {
      keyword = 'enum';
      symbolKind = SymbolKind.Enum;

      // Check for indirect modifier for enums (special case)
      const isIndirect = node.children.some(c => c.type === 'indirect');
      if (isIndirect) {
        keyword = 'indirect enum';
      }
    } else if (isStruct) {
      keyword = 'struct';
      symbolKind = SymbolKind.Struct;
    } else if (isExtension) {
      keyword = 'extension';
      symbolKind = SymbolKind.Class; // Extensions extend existing classes, so keep as Class kind
    }

    let signature = `${keyword} ${name}`;

    // For enums with indirect modifier, don't add modifiers again
    const isEnumWithIndirect = isEnum && keyword.includes('indirect');
    if (modifiers.length > 0 && !isEnumWithIndirect) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (genericParams) {
      signature += genericParams;
    }

    if (inheritance) {
      signature += `: ${inheritance}`;
    }

    // Add where clause if present
    const whereClause = this.extractWhereClause(node);
    if (whereClause) {
      signature += ` ${whereClause}`;
    }

    return this.createSymbol(node, name, symbolKind, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'class',
        modifiers,
        genericParameters: genericParams,
        inheritance: inheritance
      }
    });
  }

  private extractStruct(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownStruct';

    const modifiers = this.extractModifiers(node);
    const genericParams = this.extractGenericParameters(node);
    const conformance = this.extractInheritance(node);

    let signature = `struct ${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (genericParams) {
      signature += genericParams;
    }

    if (conformance) {
      signature += `: ${conformance}`;
    }

    return this.createSymbol(node, name, SymbolKind.Struct, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'struct',
        modifiers,
        genericParameters: genericParams,
        conformance: conformance
      }
    });
  }

  private extractProtocol(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownProtocol';

    const modifiers = this.extractModifiers(node);
    const inheritance = this.extractInheritance(node);

    let signature = `protocol ${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (inheritance) {
      signature += `: ${inheritance}`;
    }

    return this.createSymbol(node, name, SymbolKind.Interface, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'protocol',
        modifiers,
        inheritance: inheritance
      }
    });
  }

  private extractEnum(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownEnum';

    const modifiers = this.extractModifiers(node);
    const genericParams = this.extractGenericParameters(node);
    const inheritance = this.extractInheritance(node);

    let signature = `enum ${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (genericParams) {
      signature += genericParams;
    }

    if (inheritance) {
      signature += `: ${inheritance}`;
    }

    return this.createSymbol(node, name, SymbolKind.Enum, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'enum',
        modifiers,
        inheritance: inheritance
      }
    });
  }

  private extractEnumCases(node: Parser.SyntaxNode, symbols: Symbol[], parentId?: string) {
    for (const child of node.children) {
      if (child.type === 'enum_case_element') {
        const nameNode = child.children.find(c => c.type === 'pattern' || c.type === 'type_identifier');
        if (nameNode) {
          const name = this.getNodeText(nameNode);
          const associatedValues = child.children.find(c => c.type === 'enum_case_parameters');

          let signature = name;
          if (associatedValues) {
            signature += this.getNodeText(associatedValues);
          }

          const symbol = this.createSymbol(child, name, SymbolKind.EnumMember, {
            signature,
            visibility: 'public',
            parentId,
            metadata: {
              type: 'enum-case',
              associatedValues: associatedValues ? this.getNodeText(associatedValues) : null
            }
          });
          symbols.push(symbol);
        }
      }
    }
  }

  private extractEnumCase(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // enum_entry structure: case keyword + simple_identifier + optional associated values/raw values
    const nameNode = node.children.find(c => c.type === 'simple_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownCase';

    let signature = name;

    // Check for associated values (enum_type_parameters for associated values)
    const associatedValues = node.children.find(c => c.type === 'enum_type_parameters');
    if (associatedValues) {
      signature += this.getNodeText(associatedValues);
    }

    // Check for raw values (= followed by literal)
    const equalIndex = node.children.findIndex(c => c.type === '=');
    if (equalIndex !== -1 && equalIndex + 1 < node.children.length) {
      const rawValueNode = node.children[equalIndex + 1];
      if (rawValueNode) {
        signature += ` = ${this.getNodeText(rawValueNode)}`;
      }
    }

    return this.createSymbol(node, name, SymbolKind.EnumMember, {
      signature,
      visibility: 'public', // Enum cases are implicitly public
      parentId,
      metadata: {
        type: 'enum-case',
        associatedValues: associatedValues ? this.getNodeText(associatedValues) : null,
        rawValue: equalIndex !== -1 ? this.getNodeText(node.children[equalIndex + 1]) : null
      }
    });
  }

  private extractFunction(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'simple_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownFunction';

    const modifiers = this.extractModifiers(node);
    const genericParams = this.extractGenericParameters(node);
    const parameters = this.extractParameters(node);
    const returnType = this.extractReturnType(node);

    let signature = `func ${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (genericParams) {
      signature += genericParams;
    }

    signature += parameters || '()';

    if (returnType) {
      signature += ` -> ${returnType}`;
    }

    // Functions inside classes/structs are methods
    const symbolKind = parentId ? SymbolKind.Method : SymbolKind.Function;

    return this.createSymbol(node, name, symbolKind, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'function',
        modifiers,
        parameters: parameters,
        returnType: returnType
      }
    });
  }

  private extractInitializer(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // init_declaration structure: [modifiers] + init + ( + parameter + ) + function_body
    const name = 'init';

    // Extract modifiers (like convenience, required, etc.)
    const modifiers = this.extractModifiers(node);

    // Extract parameters for signature
    const parameters = this.extractInitializerParameters(node);

    let signature = `init${parameters || '()'}`;

    // Add modifiers to signature
    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    return this.createSymbol(node, name, SymbolKind.Constructor, {
      signature,
      visibility: 'public', // Initializers are typically public unless specified
      parentId,
      metadata: {
        type: 'initializer',
        parameters: parameters,
        modifiers: modifiers
      }
    });
  }

  private extractDeinitializer(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // deinit_declaration structure: deinit + function_body
    const name = 'deinit';
    const signature = 'deinit';

    return this.createSymbol(node, name, SymbolKind.Destructor, {
      signature,
      visibility: 'public', // Deinitializers are implicitly public
      parentId,
      metadata: {
        type: 'deinitializer'
      }
    });
  }

  private extractInitializerParameters(node: Parser.SyntaxNode): string | null {
    // Look for parameter nodes between ( and )
    const parameterNode = node.children.find(c => c.type === 'parameter');
    if (parameterNode) {
      return `(${this.getNodeText(parameterNode)})`;
    }

    // Check if there are parentheses but no parameters
    const hasParens = node.children.some(c => c.type === '(');
    return hasParens ? '()' : null;
  }

  private extractVariable(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const bindingNode = node.children.find(c => c.type === 'property_binding_pattern' || c.type === 'pattern_binding');
    if (!bindingNode) return null;

    const nameNode = bindingNode.children.find(c => c.type === 'simple_identifier' || c.type === 'pattern');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownVariable';

    const modifiers = this.extractModifiers(node);
    const type = this.extractVariableType(node);
    const isLet = node.children.some(c => c.type === 'let');
    const isVar = node.children.some(c => c.type === 'var');

    let signature = `${isLet ? 'let' : isVar ? 'var' : 'var'} ${name}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (type) {
      signature += `: ${type}`;
    }

    return this.createSymbol(node, name, SymbolKind.Variable, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'variable',
        modifiers,
        variableType: type,
        isLet: isLet,
        isVar: isVar
      }
    });
  }

  private extractProperty(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // Swift property structure: property_declaration has a 'pattern' child containing the name
    const nameNode = node.children.find(c => c.type === 'pattern');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownProperty';

    const modifiers = this.extractModifiers(node);
    const type = this.extractPropertyType(node);

    // Extract the actual property keyword (var or let) from value_binding_pattern
    const bindingPattern = node.children.find(c => c.type === 'value_binding_pattern');
    let keyword = 'var'; // default fallback
    if (bindingPattern) {
      const keywordNode = bindingPattern.children.find(c => c.type === 'var' || c.type === 'let');
      if (keywordNode) {
        keyword = this.getNodeText(keywordNode);
      }
    }

    // Build signature with non-visibility modifiers (visibility modifiers go in the visibility field)
    const nonVisibilityModifiers = modifiers.filter(m =>
      !['public', 'private', 'internal', 'fileprivate', 'open'].includes(m)
    );

    let signature = '';
    if (nonVisibilityModifiers.length > 0) {
      signature = `${nonVisibilityModifiers.join(' ')} ${keyword} ${name}`;
    } else {
      signature = `${keyword} ${name}`;
    }

    if (type) {
      signature += `: ${type}`;
    }

    return this.createSymbol(node, name, SymbolKind.Property, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'property',
        modifiers,
        propertyType: type,
        keyword
      }
    });
  }

  private extractProtocolFunction(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // protocol_function_declaration structure: func + simple_identifier + parameters + return_type
    const nameNode = node.children.find(c => c.type === 'simple_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownFunction';

    // Extract parameters and return type for protocol function requirements
    const parameters = this.extractParameters(node);
    const returnType = this.extractReturnType(node);

    let signature = `func ${name}`;
    signature += parameters || '()';

    if (returnType) {
      signature += ` -> ${returnType}`;
    }

    return this.createSymbol(node, name, SymbolKind.Method, {
      signature,
      visibility: 'public', // Protocol requirements are implicitly public
      parentId,
      metadata: {
        type: 'protocol-requirement',
        parameters: parameters,
        returnType: returnType
      }
    });
  }

  private extractProtocolProperty(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // protocol_property_declaration structure: [modifiers] + pattern + type_annotation + protocol_property_requirements
    const patternNode = node.children.find(c => c.type === 'pattern');
    let name = 'unknownProperty';

    if (patternNode) {
      const nameNode = patternNode.children.find(c => c.type === 'simple_identifier');
      if (nameNode) {
        name = this.getNodeText(nameNode);
      }
    }

    // Check for static modifier (inside modifiers node)
    const modifiersNode = node.children.find(c => c.type === 'modifiers');
    const isStatic = modifiersNode ? modifiersNode.children.some(c => c.type === 'property_modifier' && this.getNodeText(c) === 'static') : false;

    // Extract property type
    const type = this.extractPropertyType(node);

    // Extract getter/setter requirements
    const protocolRequirements = node.children.find(c => c.type === 'protocol_property_requirements');
    let accessors = '';
    if (protocolRequirements) {
      accessors = ` ${this.getNodeText(protocolRequirements)}`;
    }

    // Build signature
    let signature = isStatic ? 'static var ' : 'var ';
    signature += name;

    if (type) {
      signature += `: ${type}`;
    }

    if (accessors) {
      signature += accessors;
    }

    return this.createSymbol(node, name, SymbolKind.Property, {
      signature,
      visibility: 'public', // Protocol requirements are implicitly public
      parentId,
      metadata: {
        type: 'protocol-requirement',
        propertyType: type,
        accessors: accessors,
        isStatic: isStatic
      }
    });
  }

  private extractAssociatedType(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // associatedtype_declaration structure: [modifiers] + associatedtype + type_identifier + [type_inheritance_clause]
    const nameNode = node.children.find(c => c.type === 'type_identifier' || c.type === 'simple_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownType';

    let signature = `associatedtype ${name}`;

    // Check for type constraints (inheritance clause)
    const inheritance = this.extractInheritance(node);
    if (inheritance) {
      signature += `: ${inheritance}`;
    }

    return this.createSymbol(node, name, SymbolKind.Type, {
      signature,
      visibility: 'public', // Associated types are implicitly public
      parentId,
      metadata: {
        type: 'associatedtype',
        constraints: inheritance
      }
    });
  }

  private extractSubscript(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const name = 'subscript'; // Subscripts always have the name "subscript"

    // Extract parameters (indices)
    const parameters = this.extractParameters(node) || '()';

    // Extract return type
    const returnType = this.extractReturnType(node);

    // Extract any modifiers
    const modifiers = this.extractModifiers(node);

    // Build signature
    let signature = 'subscript';

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    signature += parameters;

    if (returnType) {
      signature += ` -> ${returnType}`;
    }

    // Check for accessor requirements (for protocol subscripts)
    const accessorReqs = node.children.find(c => c.type === 'getter_setter_block' || c.type === 'protocol_property_requirements');
    if (accessorReqs) {
      signature += ` ${this.getNodeText(accessorReqs)}`;
    }

    return this.createSymbol(node, name, SymbolKind.Method, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'subscript',
        parameters: parameters,
        returnType: returnType,
        modifiers
      }
    });
  }

  private extractExtension(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const typeNode = node.children.find(c => c.type === 'type_identifier');
    const name = typeNode ? this.getNodeText(typeNode) : 'UnknownExtension';

    const modifiers = this.extractModifiers(node);
    const conformance = this.extractInheritance(node);

    let signature = `extension ${typeNode ? this.getNodeText(typeNode) : 'Unknown'}`;

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (conformance) {
      signature += `: ${conformance}`;
    }

    return this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'extension',
        modifiers,
        extendedType: typeNode ? this.getNodeText(typeNode) : null
      }
    });
  }

  private extractImport(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownImport';

    return this.createSymbol(node, name, SymbolKind.Import, {
      signature: `import ${name}`,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'import'
      }
    });
  }

  private extractTypeAlias(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // typealias_declaration structure: typealias + type_identifier + = + type
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownTypeAlias';

    // Find the type that the alias refers to
    let aliasedType = '';
    const equalIndex = node.children.findIndex(c => this.getNodeText(c) === '=');
    if (equalIndex !== -1 && equalIndex + 1 < node.children.length) {
      const typeNode = node.children[equalIndex + 1];
      aliasedType = this.getNodeText(typeNode);
    }

    const modifiers = this.extractModifiers(node);
    const genericParams = this.extractGenericParameters(node);

    let signature = `typealias ${name}`;

    if (genericParams) {
      signature += genericParams;
    }

    if (modifiers.length > 0) {
      signature = `${modifiers.join(' ')} ${signature}`;
    }

    if (aliasedType) {
      signature += ` = ${aliasedType}`;
    }

    return this.createSymbol(node, name, SymbolKind.Type, {
      signature,
      visibility: this.determineVisibility(modifiers),
      parentId,
      metadata: {
        type: 'typealias',
        aliasedType: aliasedType,
        modifiers
      }
    });
  }

  private extractModifiers(node: Parser.SyntaxNode): string[] {
    const modifiers: string[] = [];
    const modifiersList = node.children.find(c => c.type === 'modifiers');

    if (modifiersList) {
      for (const child of modifiersList.children) {
        // Swift modifiers have specific node types, extract their text content
        if (child.type === 'visibility_modifier' ||
            child.type === 'mutation_modifier' ||
            child.type === 'declaration_modifier' ||
            child.type === 'access_level_modifier' ||
            child.type === 'property_modifier' ||
            child.type === 'member_modifier') {
          modifiers.push(this.getNodeText(child));
        }
        // Also handle direct modifier keywords (fallback)
        else if (['public', 'private', 'internal', 'fileprivate', 'open', 'final', 'static', 'class', 'override', 'lazy', 'weak', 'unowned', 'required', 'convenience', 'dynamic'].includes(child.type)) {
          modifiers.push(this.getNodeText(child));
        }
        // Check for attributes inside modifiers
        else if (child.type === 'attribute') {
          const attrText = this.getNodeText(child);
          modifiers.push(attrText);
        }
      }
    }

    // Also check for direct modifier nodes outside of modifiers list (alternative pattern)
    for (const child of node.children) {
      if (child.type === 'lazy' || this.getNodeText(child) === 'lazy') {
        modifiers.push('lazy');
      }
    }

    // Check for attributes like @propertyWrapper
    for (const child of node.children) {
      if (child.type === 'attribute') {
        const attrText = this.getNodeText(child);
        modifiers.push(attrText);
      }
    }

    return modifiers;
  }

  private extractGenericParameters(node: Parser.SyntaxNode): string | null {
    const genericParams = node.children.find(c => c.type === 'type_parameters');
    return genericParams ? this.getNodeText(genericParams) : null;
  }

  private extractInheritance(node: Parser.SyntaxNode): string | null {
    // First try the standard type_inheritance_clause (for classes/protocols)
    const inheritance = node.children.find(c => c.type === 'type_inheritance_clause');
    if (inheritance) {
      const types = inheritance.children.filter(c => c.type === 'type_identifier' || c.type === 'type');
      return types.map(t => this.getNodeText(t)).join(', ');
    }

    // For Swift enums, inheritance is represented as direct inheritance_specifier nodes
    const inheritanceSpecifiers = node.children.filter(c => c.type === 'inheritance_specifier');
    if (inheritanceSpecifiers.length > 0) {
      const types = inheritanceSpecifiers.map(spec => {
        // inheritance_specifier usually contains a user_type or type_identifier
        const typeNode = spec.children.find(c => c.type === 'user_type' || c.type === 'type_identifier' || c.type === 'type');
        return typeNode ? this.getNodeText(typeNode) : this.getNodeText(spec);
      });
      return types.join(', ');
    }

    return null;
  }

  private extractWhereClause(node: Parser.SyntaxNode): string | null {
    // Look for where clause in class/function declarations
    const whereClause = node.children.find(c =>
      c.type === 'where_clause' ||
      c.type === 'generic_where_clause' ||
      c.type === 'type_constraints' ||
      (c.type === 'where' || this.getNodeText(c).startsWith('where'))
    );

    if (whereClause) {
      return this.getNodeText(whereClause);
    }

    // Fallback: scan for any child containing "where"
    for (const child of node.children) {
      const text = this.getNodeText(child);
      if (text.includes('where ')) {
        // Extract just the where clause part
        const whereMatch = text.match(/where\s+[^{]+/);
        if (whereMatch) {
          return whereMatch[0].trim();
        }
      }
    }

    return null;
  }

  private extractParameters(node: Parser.SyntaxNode): string | null {
    // First try parameter_clause (for some Swift constructs)
    const paramClause = node.children.find(c => c.type === 'parameter_clause');
    if (paramClause) {
      return this.getNodeText(paramClause);
    }

    // For Swift functions, parameters are individual nodes between ( and )
    const parameters = node.children.filter(c => c.type === 'parameter');
    if (parameters.length > 0) {
      const paramStrings = parameters.map(p => this.getNodeText(p));
      return `(${paramStrings.join(', ')})`;
    }

    // Check if there are parentheses (indicating a function with no parameters)
    const hasParens = node.children.some(c => c.type === '(');
    return hasParens ? '()' : null;
  }

  private extractReturnType(node: Parser.SyntaxNode): string | null {
    // Try function_type first (for regular functions)
    const returnClause = node.children.find(c => c.type === 'function_type');
    if (returnClause) {
      const typeNode = returnClause.children.find(c => c.type === 'type');
      return typeNode ? this.getNodeText(typeNode) : null;
    }

    // Try type_annotation (for some Swift constructs like subscripts)
    const typeAnnotation = node.children.find(c => c.type === 'type_annotation');
    if (typeAnnotation) {
      const typeNode = typeAnnotation.children.find(c => c.type === 'type' || c.type === 'type_identifier' || c.type === 'user_type');
      return typeNode ? this.getNodeText(typeNode) : null;
    }

    // Try direct type nodes (for simple cases)
    const directType = node.children.find(c => c.type === 'type' || c.type === 'type_identifier' || c.type === 'user_type');
    if (directType) {
      // Make sure it's not just a parameter type by checking position
      const nodeIndex = node.children.indexOf(directType);
      const hasArrow = node.children.some((child, index) =>
        index < nodeIndex && this.getNodeText(child).includes('->'));
      if (hasArrow) {
        return this.getNodeText(directType);
      }
    }

    return null;
  }

  private extractVariableType(node: Parser.SyntaxNode): string | null {
    const typeAnnotation = node.children.find(c => c.type === 'type_annotation');
    if (typeAnnotation) {
      // Swift type annotations can have various type nodes
      const typeNode = typeAnnotation.children.find(c =>
        c.type === 'type' ||
        c.type === 'user_type' ||
        c.type === 'primitive_type' ||
        c.type === 'optional_type' ||
        c.type === 'function_type' ||
        c.type === 'tuple_type' ||
        c.type === 'dictionary_type' ||
        c.type === 'array_type'
      );
      return typeNode ? this.getNodeText(typeNode) : null;
    }
    return null;
  }

  private extractPropertyType(node: Parser.SyntaxNode): string | null {
    const typeAnnotation = node.children.find(c => c.type === 'type_annotation');
    if (typeAnnotation) {
      // Swift type annotations can have various type nodes
      const typeNode = typeAnnotation.children.find(c =>
        c.type === 'type' ||
        c.type === 'user_type' ||
        c.type === 'primitive_type' ||
        c.type === 'optional_type' ||
        c.type === 'function_type' ||
        c.type === 'tuple_type' ||
        c.type === 'dictionary_type' ||
        c.type === 'array_type'
      );
      return typeNode ? this.getNodeText(typeNode) : null;
    }
    return null;
  }

  private determineVisibility(modifiers: string[]): 'public' | 'private' | 'protected' {
    if (modifiers.includes('private') || modifiers.includes('fileprivate')) return 'private';
    if (modifiers.includes('internal')) return 'protected';
    return 'public'; // Swift defaults to internal, but we'll treat it as public
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];

    const visitNode = (node: Parser.SyntaxNode) => {
      switch (node.type) {
        case 'class_declaration':
        case 'struct_declaration':
        case 'extension_declaration':
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
    const typeSymbol = this.findTypeSymbol(node, symbols);
    if (!typeSymbol) return;

    // Try type_inheritance_clause first (some Swift constructs)
    const inheritance = node.children.find(c => c.type === 'type_inheritance_clause');
    if (inheritance) {
      for (const child of inheritance.children) {
        if (child.type === 'type_identifier' || child.type === 'type') {
          const baseTypeName = this.getNodeText(child);
          this.addInheritanceRelationship(typeSymbol, baseTypeName, symbols, relationships, node);
        }
      }
    }

    // Also handle direct inheritance_specifier nodes (common pattern)
    const inheritanceSpecifiers = node.children.filter(c => c.type === 'inheritance_specifier');
    for (const spec of inheritanceSpecifiers) {
      // inheritance_specifier contains user_type or type_identifier
      const typeNode = spec.children.find(c => c.type === 'user_type' || c.type === 'type_identifier' || c.type === 'type');
      if (typeNode) {
        let baseTypeName = '';
        if (typeNode.type === 'user_type') {
          // user_type contains type_identifier
          const innerTypeNode = typeNode.children.find(c => c.type === 'type_identifier');
          baseTypeName = innerTypeNode ? this.getNodeText(innerTypeNode) : this.getNodeText(typeNode);
        } else {
          baseTypeName = this.getNodeText(typeNode);
        }
        this.addInheritanceRelationship(typeSymbol, baseTypeName, symbols, relationships, node);
      }
    }
  }

  private addInheritanceRelationship(
    typeSymbol: Symbol,
    baseTypeName: string,
    symbols: Symbol[],
    relationships: Relationship[],
    node: Parser.SyntaxNode
  ) {
    // Find the actual base type symbol
    const baseTypeSymbol = symbols.find(s =>
      s.name === baseTypeName &&
      (s.kind === SymbolKind.Class || s.kind === SymbolKind.Interface || s.kind === SymbolKind.Struct)
    );

    if (baseTypeSymbol) {
      // Determine relationship kind: classes extend, protocols implement
      const relationshipKind = baseTypeSymbol.kind === SymbolKind.Interface
        ? RelationshipKind.Implements
        : RelationshipKind.Extends;

      relationships.push({
        fromSymbolId: typeSymbol.id,
        toSymbolId: baseTypeSymbol.id,
        kind: relationshipKind,
        filePath: this.filePath,
        lineNumber: node.startPosition.row + 1,
        confidence: 1.0,
        metadata: { baseType: baseTypeName }
      });
    }
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();
    for (const symbol of symbols) {
      // For functions/methods, prefer returnType over generic type
      if (symbol.metadata?.returnType && (symbol.kind === SymbolKind.Function || symbol.kind === SymbolKind.Method)) {
        types.set(symbol.id, symbol.metadata.returnType);
      }
      // For properties/variables, prefer propertyType or variableType over generic type
      else if (symbol.metadata?.propertyType && (symbol.kind === SymbolKind.Property || symbol.kind === SymbolKind.Variable)) {
        types.set(symbol.id, symbol.metadata.propertyType);
      }
      else if (symbol.metadata?.variableType && symbol.kind === SymbolKind.Variable) {
        types.set(symbol.id, symbol.metadata.variableType);
      }
      else if (symbol.metadata?.type) {
        types.set(symbol.id, symbol.metadata.type);
      }
      else if (symbol.metadata?.returnType) {
        types.set(symbol.id, symbol.metadata.returnType);
      }
    }
    return types;
  }

  private findTypeSymbol(node: Parser.SyntaxNode, symbols: Symbol[]): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    const typeName = nameNode ? this.getNodeText(nameNode) : null;

    return symbols.find(s =>
      s.name === typeName &&
      (s.kind === SymbolKind.Class || s.kind === SymbolKind.Struct || s.kind === SymbolKind.Interface) &&
      s.filePath === this.filePath
    ) || null;
  }
}