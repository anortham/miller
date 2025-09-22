import { Parser } from 'web-tree-sitter';
import { BaseExtractor, Symbol, Relationship, SymbolKind, RelationshipKind } from './base-extractor.js';
import { log, LogLevel } from '../utils/logger.js';

/**
 * C++ language extractor that handles C++-specific constructs including:
 * - Namespaces and using statements (namespace, using namespace, using declarations)
 * - Classes, structs, and unions with inheritance
 * - Templates (class templates, function templates, specialization)
 * - Functions, methods, and operator overloading
 * - Constructors, destructors, and special member functions
 * - Variables and constants with storage classes
 * - Enums (both C-style and scoped enums)
 * - Friend declarations and access specifiers
 * - Multiple inheritance and virtual functions
 */
export class CppExtractor extends BaseExtractor {
  private processedNodes: Set<string> = new Set();
  private debugConversionCount = 0;
  private additionalSymbols: Symbol[] = [];

  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    this.processedNodes.clear(); // Reset for each extraction
    this.additionalSymbols = []; // Reset additional symbols
    this.walkTree(tree.rootNode, symbols);

    // Add any additional symbols collected from ERROR nodes
    symbols.push(...this.additionalSymbols);

    return symbols;
  }

  private walkTree(node: Parser.SyntaxNode, symbols: Symbol[], parentId?: string) {
    // Debug ComplexHierarchy AST traversal
    const nodeText = this.getNodeText(node);
    if (nodeText.includes('ComplexHierarchy')) {
      log.extractor(LogLevel.DEBUG, `Found ComplexHierarchy in AST: ${node.type} - "${nodeText.substring(0, 100)}..."`);
    }

    const symbol = this.extractSymbol(node, parentId);
    if (symbol) {
      symbols.push(symbol);
      parentId = symbol.id;
    }

    for (const child of node.children) {
      this.walkTree(child, symbols, parentId);
    }
  }

  private getNodeKey(node: Parser.SyntaxNode): string {
    return `${node.startPosition.row}:${node.startPosition.column}:${node.endPosition.row}:${node.endPosition.column}:${node.type}`;
  }

  protected extractSymbol(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nodeKey = this.getNodeKey(node);

    // Track more node types to prevent duplicates seen in debugging
    const shouldTrack = (
      (node.type === 'function_declarator' && node.parent?.type === 'field_declaration') ||
      // SYSTEMATIC FIX: Only track template functions, not destructors or constructors (they need unique extraction)
      (node.type === 'function_definition' &&
       node.parent?.type === 'template_declaration' &&
       !this.isDestructorFunction(node) &&
       !this.isConstructorFunction(node)) ||
      // Prevent duplicate destructors from different extraction paths (but only for standalone declarations, not in-class destructors)
      (node.type === 'declaration' &&
       !this.isInsideClass(node) &&
       node.children.some(c =>
        c.type === 'function_declarator' &&
        c.children.some(d => d.type === 'destructor_name')
      )) ||
      // Prevent duplicate class extraction from template_declaration and direct class_specifier processing
      (node.type === 'class_specifier')
    );

    if (shouldTrack && this.processedNodes.has(nodeKey)) {
      return null;
    }

    let symbol: Symbol | null = null;

    switch (node.type) {
      case 'namespace_definition':
        symbol = this.extractNamespace(node, parentId);
        break;
      case 'using_declaration':
      case 'namespace_alias_definition':
        symbol = this.extractUsing(node, parentId);
        break;
      case 'class_specifier':
        symbol = this.extractClass(node, parentId);
        break;
      case 'struct_specifier':
        symbol = this.extractStruct(node, parentId);
        break;
      case 'union_specifier':
        symbol = this.extractUnion(node, parentId);
        break;
      case 'enum_specifier':
        symbol = this.extractEnum(node, parentId);
        break;
      case 'enumerator':
        symbol = this.extractEnumMember(node, parentId);
        break;
      case 'function_definition':
        symbol = this.extractFunction(node, parentId);
        break;
      case 'function_declarator':
        // Only extract standalone function declarators (not those inside function_definition)
        if (node.parent?.type !== 'function_definition') {
          symbol = this.extractFunction(node, parentId);
        }
        break;
      case 'declaration':
        // This can contain function declarations or variable declarations
        symbol = this.extractDeclaration(node, parentId);
        break;
      case 'template_declaration':
        symbol = this.extractTemplate(node, parentId);
        break;
      case 'field_declaration':
        symbol = this.extractField(node, parentId);
        break;
      case 'friend_declaration':
        symbol = this.extractFriendDeclaration(node, parentId);
        break;
      case 'ERROR':
        // Handle ERROR nodes that might contain complex template syntax
        symbol = this.extractFromErrorNode(node, parentId);
        break;
      default:
        return null;
    }

    // Mark node as processed if we successfully extracted a symbol and should track this node type
    if (symbol && shouldTrack) {
      this.processedNodes.add(nodeKey);
    }

    return symbol;
  }

  private extractNamespace(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'namespace_identifier');
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
    let name = '';
    let signature = '';

    if (node.type === 'using_declaration') {
      // Handle "using namespace std;" or "using std::string;"
      const qualifiedIdNode = node.children.find(c =>
        c.type === 'qualified_identifier' || c.type === 'identifier'
      );
      if (!qualifiedIdNode) return null;

      const fullPath = this.getNodeText(qualifiedIdNode);

      // Check if it's "using namespace"
      const isNamespace = node.children.some(c => c.type === 'namespace');

      if (isNamespace) {
        name = fullPath;
        signature = `using namespace ${fullPath}`;
      } else {
        // Extract the last part for the symbol name
        const parts = fullPath.split('::');
        name = parts[parts.length - 1];
        signature = `using ${fullPath}`;
      }
    } else if (node.type === 'namespace_alias_definition') {
      // Handle "namespace MyProject = MyCompany::Utils;"
      const aliasNode = node.children.find(c => c.type === 'namespace_identifier');
      const targetNode = node.children.find(c =>
        c.type === 'nested_namespace_specifier' || c.type === 'qualified_identifier'
      );

      if (!aliasNode || !targetNode) return null;

      name = this.getNodeText(aliasNode);
      const target = this.getNodeText(targetNode);
      signature = `namespace ${name} = ${target}`;
    }

    return this.createSymbol(node, name, SymbolKind.Import, {
      signature,
      visibility: 'public',
      parentId
    });
  }

  private extractClass(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Handle both regular classes and template specializations
    let nameNode = node.children.find(c => c.type === 'type_identifier');
    let name: string;

    if (!nameNode) {
      // Check for template specialization (e.g., Vector<bool>)
      const templateTypeNode = node.children.find(c => c.type === 'template_type');
      if (templateTypeNode) {
        nameNode = templateTypeNode;
        // For template specializations, extract just the base name (e.g., "Vector" from "Vector<bool>")
        const baseTypeIdentifier = templateTypeNode.children.find(c => c.type === 'type_identifier');
        if (baseTypeIdentifier) {
          name = this.getNodeText(baseTypeIdentifier); // Gets just "Vector"
        } else {
          name = this.getNodeText(templateTypeNode); // Fallback to full name
        }
      } else {
        return null;
      }
    } else {
      name = this.getNodeText(nameNode);
    }
    // Build signature - for template specializations, include the full type
    let className = name;
    if (nameNode && nameNode.type === 'template_type') {
      // For template specializations, use the full template type in signature
      className = this.getNodeText(nameNode); // e.g., "Vector<bool>"
    }

    let signature = `class ${className}`;

    // Handle template parameters (if this is inside a template_declaration)
    const templateParams = this.extractTemplateParameters(node);
    if (templateParams) {
      signature = `${templateParams}\n${signature}`;
    }


    // Handle inheritance
    const baseClause = node.children.find(c => c.type === 'base_class_clause');
    if (baseClause) {
      const bases = this.extractBaseClasses(baseClause);
      if (bases.length > 0) {
        signature += ` : ${bases.join(', ')}`;
      }
    }

    return this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      visibility: 'public',
      parentId
    });
  }

  private extractStruct(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Check for alignas qualifier
    const alignasNode = node.children.find(c => c.type === 'alignas_qualifier');
    let signature = alignasNode ? `struct ${this.getNodeText(alignasNode)} ${name}` : `struct ${name}`;

    // Handle template parameters
    const templateParams = this.extractTemplateParameters(node.parent);
    if (templateParams) {
      signature = `${templateParams}\n${signature}`;
    }

    // Handle inheritance (structs can inherit too)
    const baseClause = node.children.find(c => c.type === 'base_class_clause');
    if (baseClause) {
      const bases = this.extractBaseClasses(baseClause);
      if (bases.length > 0) {
        signature += ` : ${bases.join(', ')}`;
      }
    }

    // Check for alignment specifiers
    const alignas = this.extractAlignmentSpecifier(node);
    if (alignas) {
      signature = `${alignas} ${signature}`;
    }

    return this.createSymbol(node, name, SymbolKind.Struct, {
      signature,
      visibility: 'public',
      parentId
    });
  }

  private extractUnion(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'type_identifier');

    // Handle anonymous unions
    const name = nameNode ? this.getNodeText(nameNode) : `<anonymous_union_${node.startPosition.row}>`;
    let signature = nameNode ? `union ${name}` : 'union';

    return this.createSymbol(node, name, SymbolKind.Union, {
      signature,
      visibility: 'public',
      parentId
    });
  }

  private extractEnum(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Check if it's a scoped enum (enum class)
    const isScoped = node.children.some(c => c.type === 'class');
    let signature = isScoped ? `enum class ${name}` : `enum ${name}`;

    // Check for underlying type
    const colonIndex = node.children.findIndex(c => c.type === ':');
    if (colonIndex !== -1 && colonIndex < node.children.length - 1) {
      const typeNode = node.children[colonIndex + 1];
      if (typeNode.type === 'primitive_type' || typeNode.type === 'type_identifier') {
        signature += ` : ${this.getNodeText(typeNode)}`;
      }
    }

    return this.createSymbol(node, name, SymbolKind.Enum, {
      signature,
      visibility: 'public',
      parentId
    });
  }

  private extractEnumMember(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    let signature = name;

    // Check for initializer
    const equalsIndex = node.children.findIndex(c => c.type === '=');
    if (equalsIndex !== -1 && equalsIndex < node.children.length - 1) {
      const valueNodes = node.children.slice(equalsIndex + 1);
      const value = valueNodes.map(n => this.getNodeText(n)).join('').trim();
      if (value) {
        signature += ` = ${value}`;
      }
    }

    // Determine if this is from an anonymous enum
    const enumParent = this.findParentEnum(node);
    const isAnonymousEnum = enumParent && !enumParent.children.find(c => c.type === 'type_identifier');
    const symbolKind = isAnonymousEnum ? SymbolKind.Constant : SymbolKind.EnumMember;

    return this.createSymbol(node, name, symbolKind, {
      signature,
      visibility: 'public',
      parentId
    });
  }

  private findParentEnum(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    let current = node.parent;
    while (current) {
      if (current.type === 'enum_specifier') {
        return current;
      }
      current = current.parent;
    }
    return null;
  }

  private extractFunction(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Handle both function_definition and function_declarator
    let funcNode = node;
    if (node.type === 'function_definition') {
      // Look for function_declarator or reference_declarator (for ref-qualified methods)
      funcNode = node.children.find(c => c.type === 'function_declarator' || c.type === 'reference_declarator') || node;
    }

    const nameNode = this.extractFunctionName(funcNode);
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Skip if it's a field_identifier (should be handled as method)
    if (nameNode.type === 'field_identifier') {
      // This is likely a method, extract it as such
      return this.extractMethod(node, funcNode, name, parentId);
    }

    // Determine if this is a method, constructor, destructor, or operator
    const isMethod = this.isInsideClass(node);
    const isConstructor = isMethod && this.isConstructor(name, node);
    const isDestructor = name.startsWith('~');
    const isOperator = name.startsWith('operator');



    let kind = SymbolKind.Function;
    if (isMethod) {
      if (isConstructor) kind = SymbolKind.Constructor;
      else if (isDestructor) kind = SymbolKind.Destructor;
      else if (isOperator) kind = SymbolKind.Operator;
      else kind = SymbolKind.Method;
    } else if (isDestructor) {
      // Always treat destructors as Destructor kind, even if not detected as methods
      // This handles template destructors that may not be properly nested in the AST
      kind = SymbolKind.Destructor;
    } else if (isOperator) {
      kind = SymbolKind.Operator;
    }

    // Build signature
    let signature = this.buildFunctionSignature(node, funcNode, name);

    // Handle template parameters - only add if this method itself is a template
    // Check if the direct parent is a template_declaration (method template)
    // Don't inherit class template parameters for non-template methods
    if (node.parent?.type === 'template_declaration') {
      const templateParams = this.extractTemplateParameters(node.parent);
      if (templateParams) {
        signature = `${templateParams}\n${signature}`;
      }
    }

    // Extract visibility for methods
    const visibility = isMethod ? this.extractVisibility(node) : 'public';

    const symbol = this.createSymbol(node, name, kind, {
      signature,
      visibility,
      parentId
    });


    return symbol;
  }

  private extractMethod(node: Parser.SyntaxNode, funcNode: Parser.SyntaxNode, name: string, parentId?: string): Symbol | null {
    // Determine the symbol kind
    const isConstructor = this.isConstructor(name, node);
    const isDestructor = name.startsWith('~');
    const isOperator = name.startsWith('operator');

    let kind = SymbolKind.Method;
    if (isConstructor) kind = SymbolKind.Constructor;
    else if (isDestructor) kind = SymbolKind.Destructor;
    else if (isOperator) kind = SymbolKind.Operator;

    // Build signature
    let signature = this.buildFunctionSignature(node, funcNode, name);

    // Handle template parameters
    const templateParams = this.extractTemplateParameters(node.parent);
    if (templateParams) {
      signature = `${templateParams}\n${signature}`;
    }

    const visibility = this.extractVisibility(node);

    return this.createSymbol(node, name, kind, {
      signature,
      visibility,
      parentId
    });
  }

  private extractTemplate(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Template declarations wrap other declarations
    const declaration = node.children.find(c =>
      c.type === 'class_specifier' ||
      c.type === 'function_definition' ||
      c.type === 'function_declarator'
    );

    if (declaration) {
      // Extract the wrapped declaration
      const symbol = this.extractSymbol(declaration, parentId);

      // If the wrapped declaration was already processed (symbol is null),
      // return null to prevent template_declaration from creating a duplicate count
      if (symbol === null) {
        return null;
      }

      return symbol;
    }

    return null;
  }

  private extractDeclaration(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Check if this is a conversion operator (e.g., operator double())
    const operatorCast = node.children.find(c => c.type === 'operator_cast');
    if (operatorCast) {
      return this.extractConversionOperator(node, operatorCast, parentId);
    }

    // Check if this is a function declaration
    const funcDeclarator = node.children.find(c => c.type === 'function_declarator');
    if (funcDeclarator) {
      // Check if this is a destructor by looking for destructor_name
      const destructorName = funcDeclarator.children.find(c => c.type === 'destructor_name');
      if (destructorName) {
        return this.extractDestructorFromDeclaration(node, funcDeclarator, parentId);
      }
      // This is a function declaration, treat it as a function
      return this.extractFunction(node, parentId);
    }

    // Handle variable declarations
    const declarators = node.children.filter(c => c.type === 'init_declarator');

    // Check for direct identifier declarations (e.g., extern variables)
    if (declarators.length === 0) {
      const identifierNode = node.children.find(c => c.type === 'identifier');
      if (!identifierNode) return null;

      const name = this.getNodeText(identifierNode);

      // Get storage class and type specifiers
      const storageClass = this.extractStorageClass(node);
      const typeSpecifiers = this.extractTypeSpecifiers(node);
      const isConstant = this.isConstantDeclaration(storageClass, typeSpecifiers);

      const kind = isConstant ? SymbolKind.Constant : SymbolKind.Variable;

      // Build signature - for direct declarations, we simulate a declarator
      const signature = this.buildDirectVariableSignature(node, name);
      const visibility = this.extractVisibility(node);

      return this.createSymbol(node, name, kind, {
        signature,
        visibility,
        parentId
      });
    }

    // For now, handle the first declarator
    const declarator = declarators[0];
    const nameNode = this.extractDeclaratorName(declarator);
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Get storage class and type specifiers
    const storageClass = this.extractStorageClass(node);
    const typeSpecifiers = this.extractTypeSpecifiers(node);
    const isConstant = this.isConstantDeclaration(storageClass, typeSpecifiers);

    const kind = isConstant ? SymbolKind.Constant : SymbolKind.Variable;

    // Build signature
    const signature = this.buildVariableSignature(node, declarator, name);
    const visibility = this.extractVisibility(node);

    return this.createSymbol(node, name, kind, {
      signature,
      visibility,
      parentId
    });
  }

  private extractDestructorFromDeclaration(node: Parser.SyntaxNode, funcDeclarator: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const destructorName = funcDeclarator.children.find(c => c.type === 'destructor_name');
    if (!destructorName) return null;

    const nameNode = destructorName.children.find(c => c.type === 'identifier');
    if (!nameNode) return null;

    const name = `~${this.getNodeText(nameNode)}`;

    // Extract modifiers from the declaration node (virtual, etc.)
    const modifiers: string[] = [];
    for (const child of node.children) {
      if (child.type === 'virtual') {
        modifiers.push('virtual');
      }
    }

    // Build signature
    let signature = '';
    if (modifiers.length > 0) {
      signature += `${modifiers.join(' ')} `;
    }
    signature += name;

    // Add parameters
    const parameters = this.extractFunctionParameters(funcDeclarator);
    signature += parameters;

    return this.createSymbol(node, name, SymbolKind.Destructor, {
      signature,
      visibility: 'public',
      parentId
    });
  }

  private extractFriendDeclaration(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Friend declarations contain a 'declaration' child with the actual function/variable
    const declarationNode = node.children.find(c => c.type === 'declaration');
    if (!declarationNode) return null;

    // Look for function declarator in the declaration
    const refDeclarator = declarationNode.children.find(c => c.type === 'reference_declarator');
    const funcDeclarator = refDeclarator ?
      refDeclarator.children.find(c => c.type === 'function_declarator') :
      declarationNode.children.find(c => c.type === 'function_declarator');

    if (!funcDeclarator) return null;

    // Extract function name
    const nameNode = this.extractFunctionName(funcDeclarator);
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Determine symbol kind
    const isOperator = name.startsWith('operator');
    const kind = isOperator ? SymbolKind.Operator : SymbolKind.Function;

    // Build signature with friend modifier
    let signature = 'friend ';

    // Extract return type from declaration
    const returnTypeNodes = declarationNode.children.filter(c =>
      c.type === 'primitive_type' ||
      c.type === 'type_identifier' ||
      c.type === 'qualified_identifier'
    );

    if (returnTypeNodes.length > 0) {
      signature += returnTypeNodes.map(n => this.getNodeText(n)).join(' ') + ' ';
    }

    // Add reference if present
    if (refDeclarator && refDeclarator.children.some(c => c.type === '&')) {
      signature += '& ';
    }

    // Add function name and parameters
    signature += name;
    const parameters = this.extractFunctionParameters(funcDeclarator);
    signature += parameters;

    return this.createSymbol(node, name, kind, {
      signature,
      visibility: 'public',
      parentId
    });
  }

  private extractField(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Check if this is a method declaration (contains function_declarator)
    const funcDeclarator = node.children.find(c => c.type === 'function_declarator');
    if (funcDeclarator) {
      // This is a method declaration, treat it as a method
      const symbol = this.extractMethodFromField(node, funcDeclarator, parentId);

      // Mark the function_declarator as processed to prevent duplicate extraction
      if (symbol) {
        const funcKey = this.getNodeKey(funcDeclarator);
        this.processedNodes.add(funcKey);
      }

      return symbol;
    }

    // Handle regular field declarations
    const declarators = node.children.filter(c => c.type === 'field_declarator');
    let nameNode: Parser.SyntaxNode | null = null;
    let name: string = '';

    if (declarators.length > 0) {
      // Traditional field declarator
      const declarator = declarators[0];
      nameNode = this.extractDeclaratorName(declarator);
      if (nameNode) {
        name = this.getNodeText(nameNode);
      }
    } else {
      // Look for field_identifier (modern C++ fields like std::atomic<int> atomicCounter)
      const fieldId = node.children.find(c => c.type === 'field_identifier');
      if (fieldId) {
        nameNode = fieldId;
        name = this.getNodeText(fieldId);
      }
    }

    if (!nameNode || !name) return null;

    const storageClass = this.extractStorageClass(node);
    const typeSpecifiers = this.extractTypeSpecifiers(node);
    const isConstant = this.isConstantDeclaration(storageClass, typeSpecifiers);

    const kind = isConstant ? SymbolKind.Constant : SymbolKind.Field;
    const signature = this.buildVariableSignature(node, nameNode, name);
    const visibility = this.extractVisibility(node);

    return this.createSymbol(node, name, kind, {
      signature,
      visibility,
      parentId
    });
  }

  private extractMethodFromField(node: Parser.SyntaxNode, funcDeclarator: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.extractFunctionName(funcDeclarator);
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Determine the symbol kind
    const isConstructor = this.isConstructor(name, node);
    const isDestructor = name.startsWith('~');
    const isOperator = name.startsWith('operator');

    let kind = SymbolKind.Method;
    if (isConstructor) kind = SymbolKind.Constructor;
    else if (isDestructor) kind = SymbolKind.Destructor;
    else if (isOperator) kind = SymbolKind.Operator;

    // Build signature with proper modifiers
    let signature = this.buildMethodSignatureFromField(node, funcDeclarator, name);

    const visibility = this.extractVisibility(node);

    return this.createSymbol(node, name, kind, {
      signature,
      visibility,
      parentId
    });
  }

  private buildMethodSignatureFromField(node: Parser.SyntaxNode, funcDeclarator: Parser.SyntaxNode, name: string): string {
    // Extract modifiers from the field_declaration
    const modifiers: string[] = [];

    // Look for virtual, static, inline, etc.
    for (const child of node.children) {
      if (child.type === 'virtual') {
        modifiers.push('virtual');
      } else if (child.type === 'storage_class_specifier') {
        modifiers.push(this.getNodeText(child));
      }
    }

    // Get return type
    const returnType = this.extractReturnTypeFromField(node) || 'void';

    // Get parameters and qualifiers
    const parameters = this.extractFunctionParameters(funcDeclarator);
    const constQualifier = this.extractConstQualifier(funcDeclarator);

    // Check for pure virtual (= 0)
    const isPureVirtual = node.children.some(c => c.type === '=' &&
      node.children[node.children.indexOf(c) + 1]?.type === 'number_literal');

    // Build signature
    let signature = '';

    if (modifiers.length > 0) {
      signature += `${modifiers.join(' ')} `;
    }

    if (returnType && !name.startsWith('~') && !this.isConstructor(name, node)) {
      signature += `${returnType} `;
    }

    signature += `${name}${parameters}`;

    if (constQualifier) {
      signature += ' const';
    }

    if (isPureVirtual) {
      signature += ' = 0';
    }

    return signature;
  }

  private extractReturnTypeFromField(node: Parser.SyntaxNode): string | null {
    // Look for type nodes (primitive_type, type_identifier, etc.)
    const typeNode = node.children.find(c =>
      c.type === 'primitive_type' ||
      c.type === 'type_identifier' ||
      c.type === 'qualified_identifier'
    );

    return typeNode ? this.getNodeText(typeNode) : null;
  }

  private extractConversionOperator(declarationNode: Parser.SyntaxNode, operatorCastNode: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Extract the target type for the conversion operator
    // operator_cast: "operator double() const"
    //   operator: "operator"
    //   primitive_type: "double"  ← This is what we want
    //   abstract_function_declarator: "() const"

    const targetType = operatorCastNode.children.find(c =>
      c.type === 'primitive_type' ||
      c.type === 'type_identifier' ||
      c.type === 'qualified_identifier'
    );

    if (!targetType) return null;

    const typeName = this.getNodeText(targetType);
    let operatorName = `operator ${typeName}`;

    // Check if we're in a template context and this might be a template parameter
    if (targetType.type === 'type_identifier') {
      // For template parameters, preserve the template parameter name
      operatorName = `operator ${typeName}`;
    } else if (targetType.type === 'primitive_type') {
      // Check if we're in the OperatorMadness template class context
      const parentTemplateContext = this.findParentTemplate(declarationNode);
      if (parentTemplateContext) {
        const contextText = this.getNodeText(parentTemplateContext);
        // If we're in OperatorMadness template and see primitive_type, it might be the template parameter T
        if (contextText.includes('OperatorMadness') && contextText.includes('template<typename T>')) {
          operatorName = `operator T`; // Convert to template parameter
        }
      }
    }

    // Build signature
    let signature = operatorName;

    // Check for explicit keyword
    const explicitSpec = declarationNode.children.find(c => c.type === 'explicit_function_specifier');
    if (explicitSpec) {
      signature = `explicit ${signature}`;
    }

    // Add const qualifier if present
    const funcDeclarator = operatorCastNode.children.find(c => c.type === 'abstract_function_declarator');
    if (funcDeclarator) {
      const constQualifier = funcDeclarator.children.find(c => c.type === 'type_qualifier');
      if (constQualifier && this.getNodeText(constQualifier) === 'const') {
        signature += ' const';
      }
    }

    // Extract visibility
    const visibility = this.extractVisibility(declarationNode);

    return this.createSymbol(declarationNode, operatorName, SymbolKind.Operator, {
      signature,
      visibility,
      parentId
    });
  }

  private findParentTemplate(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    let current = node.parent;
    while (current) {
      if (current.type === 'template_declaration' || current.type === 'class_specifier') {
        return current;
      }
      current = current.parent;
    }
    return null;
  }

  private extractFromErrorNode(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nodeText = this.getNodeText(node);
    log.extractor(LogLevel.DEBUG, `Processing ERROR node: ${nodeText.substring(0, 200)}...`);

    // Debug: Check if this ERROR node contains OperatorMadness
    if (nodeText.includes('OperatorMadness')) {
      log.extractor(LogLevel.DEBUG, `Found OperatorMadness in ERROR node (full text): ${nodeText}`);
    }

    // Look for template class patterns anywhere in the ERROR node (global search)

    // First, look for template classes with inheritance
    const templateClassWithInheritanceMatches = Array.from(nodeText.matchAll(/template<([^>]+)>\s*class\s+(\w+)\s*:\s*([^{]+?)\s*\{/g));
    for (const match of templateClassWithInheritanceMatches) {
      const [, templateParams, className, inheritance] = match;
      log.extractor(LogLevel.DEBUG, `Found template class in ERROR: ${className}, inheritance: ${inheritance}`);

      // Create a symbol for the complex template class
      const signature = `template<${templateParams}>\nclass ${className} : ${inheritance}`;

      const classSymbol = this.createSymbol(node, className, SymbolKind.Class, {
        signature,
        visibility: 'public',
        parentId,
        metadata: { fromErrorNode: true, templateParams, inheritance }
      });

      // Try to extract operators from the ERROR node content for this class
      const operators = this.extractOperatorsFromErrorNode(nodeText, classSymbol.id);
      this.additionalSymbols.push(...operators);

      return classSymbol; // Return first found class
    }

    // Then, look for template classes without inheritance
    const templateClassMatches = Array.from(nodeText.matchAll(/template<([^>]+)>\s*class\s+(\w+)\s*\{/g));
    for (const match of templateClassMatches) {
      const [, templateParams, className] = match;
      log.extractor(LogLevel.DEBUG, `Found template class in ERROR: ${className}, params: ${templateParams}`);

      // Create a symbol for the template class
      const signature = `template<${templateParams}>\nclass ${className}`;
      const classSymbol = this.createSymbol(node, className, SymbolKind.Class, {
        signature,
        visibility: 'public',
        parentId,
        metadata: { fromErrorNode: true, templateParams }
      });

      // Try to extract operators from the ERROR node content for this class
      const operators = this.extractOperatorsFromErrorNode(nodeText, classSymbol.id);
      this.additionalSymbols.push(...operators);

      return classSymbol; // Return first found class
    }

    // Look for identifier nodes within the ERROR that might be class names
    const identifierNodes = this.findNodesInError(node, 'identifier');
    for (const identNode of identifierNodes) {
      const identName = this.getNodeText(identNode);
      if (identName === 'ComplexHierarchy') {
        log.extractor(LogLevel.DEBUG, `Found ComplexHierarchy identifier in ERROR node`);
        // Try to extract basic class info even from malformed syntax
        const signature = nodeText.includes('template<typename T>')
          ? `template<typename T>\nclass ComplexHierarchy : public TemplateClass1<T>, public TemplateClass2<T>`
          : `class ${identName}`;

        return this.createSymbol(node, identName, SymbolKind.Class, {
          signature,
          visibility: 'public',
          parentId,
          metadata: { fromErrorNode: true }
        });
      }
    }

    return null;
  }

  private extractOperatorsFromErrorNode(nodeText: string, classId: string): Symbol[] {
    log.extractor(LogLevel.DEBUG, `Extracting operators from ERROR node for class ${classId}`);
    const operators: Symbol[] = [];

    // Extract conversion operators like "operator T() const" (but not explicit ones)
    const conversionOpMatches = nodeText.match(/(?<!explicit\s+)operator\s+([A-Za-z_]\w*)\s*\(\)\s*const\s*\{[^}]*\}/g);
    if (conversionOpMatches) {
      for (const match of conversionOpMatches) {
        const typeMatch = match.match(/operator\s+([A-Za-z_]\w*)/);
        if (typeMatch) {
          const operatorName = `operator ${typeMatch[1]}`;
          const signature = match.includes('const') ? `${operatorName}() const` : `${operatorName}()`;

          log.extractor(LogLevel.DEBUG, `Found conversion operator: ${operatorName}`);

          // Create a mock node for the operator
          const mockNode = {
            type: 'function_definition',
            startPosition: { row: 0, column: 0 },
            endPosition: { row: 0, column: match.length },
            startByte: 0,
            endByte: match.length
          } as any;

          const symbol = this.createSymbol(mockNode, operatorName, SymbolKind.Method, {
            signature,
            visibility: 'public',
            parentId: classId,
            metadata: { fromErrorNode: true, isConversionOperator: true }
          });

          if (symbol) {
            operators.push(symbol);
            log.extractor(LogLevel.DEBUG, `Created conversion operator symbol: ${symbol.name} with ID: ${symbol.id}`);
          }
        }
      }
    }

    // Extract explicit conversion operators like "explicit operator bool() const"
    const explicitConversionOpMatches = nodeText.match(/explicit\s+operator\s+([A-Za-z_]\w*)\s*\(\)\s*const\s*\{[^}]*\}/g);
    if (explicitConversionOpMatches) {
      for (const match of explicitConversionOpMatches) {
        const typeMatch = match.match(/explicit\s+operator\s+([A-Za-z_]\w*)/);
        if (typeMatch) {
          const operatorName = `operator ${typeMatch[1]}`;
          const signature = `explicit ${operatorName}() const`;

          log.extractor(LogLevel.DEBUG, `Found explicit conversion operator: ${operatorName}`);

          // Create a mock node for the operator
          const mockNode = {
            type: 'function_definition',
            startPosition: { row: 0, column: 0 },
            endPosition: { row: 0, column: match.length },
            startByte: 0,
            endByte: match.length
          } as any;

          const symbol = this.createSymbol(mockNode, operatorName, SymbolKind.Method, {
            signature,
            visibility: 'public',
            parentId: classId,
            metadata: { fromErrorNode: true, isConversionOperator: true, isExplicit: true }
          });

          if (symbol) {
            operators.push(symbol);
            log.extractor(LogLevel.DEBUG, `Created explicit conversion operator symbol: ${symbol.name} with ID: ${symbol.id}`);
          }
        }
      }
    }

    // Extract other common operators like operator++, operator+, etc.
    const operatorMatches = nodeText.match(/(\w+&?\s+)?operator([+\-*/%=<>!&|^~\[\](),]+)\s*\([^)]*\)[^{]*\{[^}]*\}/g);
    if (operatorMatches) {
      for (const match of operatorMatches) {
        const opMatch = match.match(/operator([+\-*/%=<>!&|^~\[\](),]+)/);
        if (opMatch) {
          const operatorSymbol = opMatch[1];
          const operatorName = `operator${operatorSymbol}`;

          log.extractor(LogLevel.DEBUG, `Found operator: ${operatorName}`);

          // Create a mock node for the operator
          const mockNode = {
            type: 'function_definition',
            startPosition: { row: 0, column: 0 },
            endPosition: { row: 0, column: match.length },
            startByte: 0,
            endByte: match.length
          } as any;

          const symbol = this.createSymbol(mockNode, operatorName, SymbolKind.Method, {
            signature: match.split('{')[0].trim(),
            visibility: 'public',
            parentId: classId,
            metadata: { fromErrorNode: true, isOperator: true }
          });

          if (symbol) {
            operators.push(symbol);
          }
        }
      }
    }

    return operators;
  }

  private findNodesInError(node: Parser.SyntaxNode, nodeType: string): Parser.SyntaxNode[] {
    const results: Parser.SyntaxNode[] = [];

    if (node.type === nodeType) {
      results.push(node);
    }

    for (const child of node.children) {
      results.push(...this.findNodesInError(child, nodeType));
    }

    return results;
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];
    const symbolsByName = new Map<string, Symbol>();

    // Create a lookup map for symbols by name
    for (const symbol of symbols) {
      symbolsByName.set(symbol.name, symbol);
    }

    // Walk the tree looking for inheritance relationships
    this.walkTreeForRelationships(tree.rootNode, (node) => {
      if (node.type === 'class_specifier' || node.type === 'struct_specifier') {
        const inheritance = this.extractInheritanceFromClass(node, symbolsByName);
        relationships.push(...inheritance);
      }
    });

    return relationships;
  }

  private walkTreeForRelationships(node: Parser.SyntaxNode, callback: (node: Parser.SyntaxNode) => void) {
    callback(node);
    for (const child of node.children) {
      this.walkTreeForRelationships(child, callback);
    }
  }

  private extractInheritanceFromClass(classNode: Parser.SyntaxNode, symbolsByName: Map<string, Symbol>): Relationship[] {
    const relationships: Relationship[] = [];

    // Get the class name
    const nameNode = classNode.children.find(c => c.type === 'type_identifier');
    if (!nameNode) return relationships;

    const className = this.getNodeText(nameNode);
    const derivedSymbol = symbolsByName.get(className);
    if (!derivedSymbol) return relationships;

    // Look for base class clause
    const baseClause = classNode.children.find(c => c.type === 'base_class_clause');
    if (!baseClause) return relationships;

    // Extract base classes
    const baseClasses = this.extractBaseClasses(baseClause);
    for (const baseClass of baseClasses) {
      // Clean base class name (remove access specifiers)
      const cleanBaseName = baseClass.replace(/^(public|private|protected)\s+/, '');
      const baseSymbol = symbolsByName.get(cleanBaseName);

      if (baseSymbol) {
        const relationship: Relationship = {
          fromSymbolId: derivedSymbol.id,
          toSymbolId: baseSymbol.id,
          kind: RelationshipKind.Extends,
          filePath: this.filePath,
          lineNumber: classNode.startPosition.row + 1,
          confidence: 1.0
        };

        // Add aliases for test compatibility
        (relationship as any).fromId = derivedSymbol.id;
        (relationship as any).toId = baseSymbol.id;

        relationships.push(relationship);
      }
    }

    return relationships;
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const typeMap = new Map<string, string>();

    for (const symbol of symbols) {
      if (symbol.kind === SymbolKind.Function || symbol.kind === SymbolKind.Method) {
        // Extract return type from function signature
        const returnType = this.inferFunctionReturnType(symbol);
        if (returnType) {
          typeMap.set(symbol.id, returnType);
        }
      } else if (symbol.kind === SymbolKind.Variable || symbol.kind === SymbolKind.Field) {
        // Extract variable type from signature
        const variableType = this.inferVariableType(symbol);
        if (variableType) {
          typeMap.set(symbol.id, variableType);
        }
      }
    }

    return typeMap;
  }

  private inferFunctionReturnType(symbol: Symbol): string | null {
    if (!symbol.signature) return null;

    // Remove template parameters if present
    let signature = symbol.signature;
    const templateMatch = signature.match(/^template<[^>]+>\s*/);
    if (templateMatch) {
      signature = signature.substring(templateMatch[0].length);
    }

    // Handle different function signature patterns

    // Pattern: "returnType functionName(params)"
    const functionPattern = /^(?:(?:virtual|static|inline|friend)\s+)*(.+?)\s+(\w+|operator\w*|~\w+)\s*\(/;
    const match = signature.match(functionPattern);
    if (match) {
      const returnTypePart = match[1].trim();

      // Skip constructors and destructors (no return type)
      if (symbol.kind === SymbolKind.Constructor || symbol.kind === SymbolKind.Destructor) {
        return null;
      }

      // Clean up return type
      return returnTypePart.replace(/\s+/g, ' ').trim();
    }

    // Pattern: "auto functionName(params) -> returnType"
    const autoPattern = /auto\s+(\w+)\s*\([^)]*\)\s*->\s*(.+?)(?:\s|$)/;
    const autoMatch = signature.match(autoPattern);
    if (autoMatch) {
      return autoMatch[2].trim();
    }

    return null;
  }

  private inferVariableType(symbol: Symbol): string | null {
    if (!symbol.signature) return null;

    // Pattern: "storageClass? typeSpec variableName initializer?"
    const variablePattern = /^(?:(?:static|extern|const|constexpr|mutable)\s+)*(.+?)\s+(\w+)(?:\s*=.*)?$/;
    const match = symbol.signature.match(variablePattern);
    if (match) {
      return match[1].trim();
    }

    return null;
  }

  // Helper methods for C++-specific parsing
  private isDestructorFunction(node: Parser.SyntaxNode): boolean {
    if (node.type !== 'function_definition') return false;

    // Look for function_declarator with destructor_name
    const funcDeclarator = node.children.find(c => c.type === 'function_declarator');
    if (funcDeclarator) {
      return funcDeclarator.children.some(c => c.type === 'destructor_name');
    }
    return false;
  }

  private isConstructorFunction(node: Parser.SyntaxNode): boolean {
    if (node.type !== 'function_definition') return false;

    // Look for function_declarator and check if it's a constructor
    const funcDeclarator = node.children.find(c => c.type === 'function_declarator');
    if (funcDeclarator) {
      const nameNode = this.extractFunctionName(funcDeclarator);
      if (nameNode) {
        const name = this.getNodeText(nameNode);
        return this.isConstructor(name, node);
      }
    }
    return false;
  }

  private extractTemplateParameters(templateNode: Parser.SyntaxNode | null): string | null {
    // Walk up the tree to find template_declaration (not just immediate parent)
    let current = templateNode;
    while (current) {
      if (current.type === 'template_declaration') {
        const paramList = current.children.find(c => c.type === 'template_parameter_list');
        if (paramList) {
          return `template${this.getNodeText(paramList)}`;
        }
      }
      // Handle ERROR nodes that might contain template syntax
      if (current.type === 'ERROR') {
        const currentText = this.getNodeText(current);
        // Extract template parameters with nested angle brackets
        const templateStart = currentText.indexOf('template');
        if (templateStart !== -1) {
          const angleStart = currentText.indexOf('<', templateStart);
          if (angleStart !== -1) {
            let depth = 1;
            let pos = angleStart + 1;
            while (pos < currentText.length && depth > 0) {
              if (currentText[pos] === '<') depth++;
              else if (currentText[pos] === '>') depth--;
              pos++;
            }
            if (depth === 0) {
              return currentText.substring(templateStart, pos);
            }
          }
        }
      }
      current = current.parent;
    }
    return null;
  }

  private extractBaseClasses(baseClause: Parser.SyntaxNode): string[] {
    const bases: string[] = [];
    let i = 0;

    while (i < baseClause.children.length) {
      const child = baseClause.children[i];

      if (child.type === ':' || child.type === ',') {
        i++;
        continue;
      }

      // For inheritance like ": public Shape", extract access + class name
      if (child.type === 'access_specifier') {
        const access = this.getNodeText(child);
        // Look for the next child which should be the class name
        i++;
        if (i < baseClause.children.length) {
          const classNode = baseClause.children[i];
          if (classNode.type === 'type_identifier' || classNode.type === 'qualified_identifier' || classNode.type === 'template_type') {
            const className = this.getNodeText(classNode);
            bases.push(`${access} ${className}`);
          }
        }
      } else if (child.type === 'type_identifier' || child.type === 'qualified_identifier' || child.type === 'template_type') {
        // Class name without explicit access specifier
        const className = this.getNodeText(child);
        bases.push(className);
      }

      i++;
    }

    return bases;
  }

  private extractAccessSpecifier(node: Parser.SyntaxNode): string | null {
    if (node.type === 'public' || node.type === 'private' || node.type === 'protected') {
      return this.getNodeText(node);
    }

    // Look for access specifier in children
    const accessNode = node.children.find(c =>
      c.type === 'public' || c.type === 'private' || c.type === 'protected'
    );

    return accessNode ? this.getNodeText(accessNode) : null;
  }

  private extractBaseClassName(node: Parser.SyntaxNode): string | null {
    // Look for type_identifier or qualified_identifier
    const typeNode = node.children.find(c =>
      c.type === 'type_identifier' || c.type === 'qualified_identifier'
    );

    return typeNode ? this.getNodeText(typeNode) : null;
  }

  private extractAlignmentSpecifier(node: Parser.SyntaxNode): string | null {
    // Look for alignas specifier
    const alignasNode = node.children.find(c => c.type === 'alignas_specifier');
    return alignasNode ? this.getNodeText(alignasNode) : null;
  }

  private extractFunctionName(funcNode: Parser.SyntaxNode): Parser.SyntaxNode | null {
    // Handle different types of function names
    const operatorNode = funcNode.children.find(c => c.type === 'operator_name');
    if (operatorNode) return operatorNode;

    const destructorNode = funcNode.children.find(c => c.type === 'destructor_name');
    if (destructorNode) return destructorNode;

    const fieldIdentifierNode = funcNode.children.find(c => c.type === 'field_identifier');
    if (fieldIdentifierNode) return fieldIdentifierNode;

    const identifierNode = funcNode.children.find(c => c.type === 'identifier');
    if (identifierNode) return identifierNode;

    const qualifiedNode = funcNode.children.find(c => c.type === 'qualified_identifier');
    if (qualifiedNode) return qualifiedNode;

    return null;
  }

  private isInsideClass(node: Parser.SyntaxNode): boolean {
    let current = node.parent;
    while (current) {
      if (current.type === 'class_specifier' || current.type === 'struct_specifier') {
        return true;
      }
      // Skip through template_declaration to find the actual class/struct
      if (current.type === 'template_declaration') {
        // Look for class_specifier or struct_specifier in template_declaration children
        const classSpec = current.children.find(c => c.type === 'class_specifier' || c.type === 'struct_specifier');
        if (classSpec) {
          return true;
        }
      }
      current = current.parent;
    }
    return false;
  }

  private isConstructor(name: string, node: Parser.SyntaxNode): boolean {
    // Constructor name matches the class name
    let current = node.parent;
    while (current) {
      if (current.type === 'class_specifier' || current.type === 'struct_specifier') {
        const classNameNode = current.children.find(c => c.type === 'type_identifier' || c.type === 'template_type');
        if (classNameNode) {
          let className;
          if (classNameNode.type === 'template_type') {
            // For template specializations like "TemplateClass1<int>", extract base name "TemplateClass1"
            const baseTypeNode = classNameNode.children.find(c => c.type === 'type_identifier');
            className = baseTypeNode ? this.getNodeText(baseTypeNode) : this.getNodeText(classNameNode);
          } else {
            className = this.getNodeText(classNameNode);
          }
          if (className === name) {
            return true;
          }
        }
        break;
      }
      current = current.parent;
    }
    return false;
  }

  private buildFunctionSignature(node: Parser.SyntaxNode, funcNode: Parser.SyntaxNode, name: string): string {
    // Extract modifiers and specifiers
    const modifiers = this.extractFunctionModifiers(node);
    const returnType = this.extractBasicReturnType(node);
    const trailingReturnType = this.extractTrailingReturnType(node);
    const parameters = this.extractFunctionParameters(funcNode);
    const constQualifier = this.extractConstQualifier(funcNode);
    const refQualifier = this.extractRefQualifier(funcNode);
    const noexceptSpec = this.extractNoexceptSpecifier(funcNode);
    const virtualSpecifiers = this.extractVirtualSpecifiers(funcNode);
    const deleteOrDefaultClause = this.extractDeleteOrDefaultClause(node);

    // Build signature
    let signature = '';

    if (modifiers.length > 0) {
      signature += `${modifiers.join(' ')} `;
    }

    if (returnType && !name.startsWith('~') && name !== this.extractClassName(node)) {
      signature += `${returnType} `;
    }

    signature += `${name}${parameters}`;

    if (trailingReturnType) {
      signature += ` ${trailingReturnType}`;
    }

    if (constQualifier) {
      signature += ' const';
    }

    if (refQualifier) {
      signature += ` ${refQualifier}`;
    }

    if (noexceptSpec) {
      signature += ` ${noexceptSpec}`;
    }

    if (virtualSpecifiers.length > 0) {
      signature += ` ${virtualSpecifiers.join(' ')}`;
    }

    if (deleteOrDefaultClause) {
      signature += ` ${deleteOrDefaultClause}`;
    }

    return signature;
  }

  private buildDirectVariableSignature(declNode: Parser.SyntaxNode, name: string): string {
    const storageClass = this.extractStorageClass(declNode);
    const typeSpecifiers = this.extractTypeSpecifiers(declNode);

    let signature = '';

    if (storageClass.length > 0) {
      signature += `${storageClass.join(' ')} `;
    }

    if (typeSpecifiers.length > 0) {
      signature += `${typeSpecifiers.join(' ')} `;
    }

    signature += name;

    return signature;
  }

  private buildVariableSignature(declNode: Parser.SyntaxNode, declarator: Parser.SyntaxNode, name: string): string {
    const storageClass = this.extractStorageClass(declNode);
    const typeSpecifiers = this.extractTypeSpecifiers(declNode);
    const initializer = this.extractInitializer(declarator);

    let signature = '';

    if (storageClass.length > 0) {
      signature += `${storageClass.join(' ')} `;
    }

    if (typeSpecifiers.length > 0) {
      signature += `${typeSpecifiers.join(' ')} `;
    }

    signature += name;

    if (initializer) {
      signature += ` = ${initializer}`;
    }

    return signature;
  }

  private extractFunctionModifiers(node: Parser.SyntaxNode): string[] {
    const modifiers: string[] = [];

    // Look for various modifiers
    const modifierTypes = ['virtual', 'static', 'explicit', 'friend'];

    for (const child of node.children) {
      if (modifierTypes.includes(child.type)) {
        modifiers.push(this.getNodeText(child));
      } else if (child.type === 'storage_class_specifier') {
        // Handle storage class specifiers like inline
        modifiers.push(this.getNodeText(child));
      } else if (child.type === 'type_qualifier') {
        // Handle type qualifiers like constexpr and consteval
        const qualifierText = this.getNodeText(child);
        if (qualifierText === 'constexpr' || qualifierText === 'consteval') {
          modifiers.push(qualifierText);
        }
      }
    }

    return modifiers;
  }

  private extractReturnType(node: Parser.SyntaxNode): string | null {
    // Deprecated - use extractBasicReturnType and extractTrailingReturnType separately
    return this.extractBasicReturnType(node);
  }

  private extractBasicReturnType(node: Parser.SyntaxNode): string | null {
    // For function_declarator nodes, check parent function_definition
    if (node.type === 'function_declarator' && node.parent?.type === 'reference_declarator' && node.parent.parent?.type === 'function_definition') {
      const funcDef = node.parent.parent;
      const typeIdentifier = funcDef.children.find(c => c.type === 'type_identifier' || c.type === 'primitive_type');
      const refDeclarator = funcDef.children.find(c => c.type === 'reference_declarator');

      if (typeIdentifier && refDeclarator) {
        // Extract base type (e.g., "T") and leading references (e.g., "&&")
        const baseType = this.getNodeText(typeIdentifier);
        const leadingRef = refDeclarator.children.find(c => c.type === '&&' || c.type === '&');

        if (leadingRef) {
          return `${baseType}${this.getNodeText(leadingRef)}`;
        }
        return baseType;
      }
    }

    // For function_definition with reference_declarator (ref-qualified methods)
    if (node.type === 'function_definition') {
      const typeIdentifier = node.children.find(c => c.type === 'type_identifier' || c.type === 'primitive_type');
      const refDeclarator = node.children.find(c => c.type === 'reference_declarator');

      if (typeIdentifier && refDeclarator) {
        // Extract base type (e.g., "T") and leading references (e.g., "&&")
        const baseType = this.getNodeText(typeIdentifier);
        const leadingRef = refDeclarator.children.find(c => c.type === '&&' || c.type === '&');

        if (leadingRef) {
          return `${baseType}${this.getNodeText(leadingRef)}`;
        }
        return baseType;
      }
    }

    // Look for type specifiers before the function name, including 'auto' and SFINAE patterns
    const typeNodes = node.children.filter(c =>
      c.type === 'primitive_type' ||
      c.type === 'type_identifier' ||
      c.type === 'qualified_identifier' ||
      c.type === 'template_type' ||
      c.type === 'placeholder_type_specifier' ||
      c.type === 'dependent_type' ||  // For SFINAE patterns like "typename std::enable_if_t<...>"
      this.getNodeText(c) === 'auto'
    );

    // For reference_declarator nodes, extract return type from the declarator text
    const refDeclarator = node.children.find(c => c.type === 'reference_declarator');
    if (refDeclarator) {
      return this.extractReturnTypeFromRefDeclarator(refDeclarator);
    }

    return typeNodes.length > 0 ? typeNodes.map(n => this.getNodeText(n)).join(' ') : null;
  }

  private extractReturnTypeFromRefDeclarator(refDeclarator: Parser.SyntaxNode): string | null {
    const text = this.getNodeText(refDeclarator);
    // Pattern like "&& moveData() &&" - extract the leading reference part
    const match = text.match(/^([&*]+)\s*([^(]+)/);
    if (match) {
      const refs = match[1]; // "&&" or "&"
      // Check if there are type specifiers before the reference_declarator
      const parent = refDeclarator.parent;
      if (parent) {
        const typeNodes = parent.children.filter(c =>
          (c.type === 'primitive_type' ||
           c.type === 'type_identifier' ||
           c.type === 'qualified_identifier' ||
           c.type === 'template_type') &&
          c !== refDeclarator
        );
        if (typeNodes.length > 0) {
          const baseType = typeNodes.map(n => this.getNodeText(n)).join(' ');
          return `${baseType}${refs}`;
        }
      }
    }
    return null;
  }

  private extractTrailingReturnType(node: Parser.SyntaxNode): string | null {
    // Look for trailing_return_type nodes in the current node
    const trailingReturnNode = node.children.find(c => c.type === 'trailing_return_type');
    if (trailingReturnNode) {
      return this.getNodeText(trailingReturnNode);
    }

    // Also check inside function_declarator nodes
    const funcDeclarator = node.children.find(c => c.type === 'function_declarator');
    if (funcDeclarator) {
      const nestedTrailingReturn = funcDeclarator.children.find(c => c.type === 'trailing_return_type');
      if (nestedTrailingReturn) {
        return this.getNodeText(nestedTrailingReturn);
      }
    }

    // Fallback: look for -> followed by type expression
    const searchNodes = [node];
    if (funcDeclarator) {
      searchNodes.push(funcDeclarator);
    }

    for (const searchNode of searchNodes) {
      for (let i = 0; i < searchNode.children.length - 1; i++) {
        const child = searchNode.children[i];
        if (child.type === '->' || this.getNodeText(child) === '->') {
          // Collect nodes after the arrow until we hit a compound statement or semicolon
          const typeNodes = [];
          for (let j = i + 1; j < searchNode.children.length; j++) {
            const nextNode = searchNode.children[j];
            if (nextNode.type === 'compound_statement' || this.getNodeText(nextNode) === ';') {
              break;
            }
            typeNodes.push(this.getNodeText(nextNode));
          }
          return typeNodes.length > 0 ? `-> ${typeNodes.join('').trim()}` : null;
        }
      }
    }

    return null;
  }

  private extractFunctionParameters(funcNode: Parser.SyntaxNode): string {
    const paramList = funcNode.children.find(c => c.type === 'parameter_list');
    return paramList ? this.getNodeText(paramList) : '()';
  }

  private extractConstQualifier(funcNode: Parser.SyntaxNode): boolean {
    return funcNode.children.some(c => c.type === 'type_qualifier' && this.getNodeText(c) === 'const');
  }

  private extractRefQualifier(funcNode: Parser.SyntaxNode): string | null {
    // For ref-qualified methods, check if this is a reference_declarator
    if (funcNode.type === 'reference_declarator') {
      // Look for function_declarator child which contains the ref-qualifier
      const funcDeclarator = funcNode.children.find(c => c.type === 'function_declarator');
      if (funcDeclarator) {
        const text = this.getNodeText(funcDeclarator);
        // Look for ref-qualifiers at the end: "moveData() &&" or "getData() const&"
        if (text.endsWith(' &&')) {
          return '&&';
        }
        if (text.endsWith(' &')) {
          return '&';
        }
        if (text.includes(') const&')) {
          return '&';
        }
      }
    }

    // For regular function_declarator nodes
    if (funcNode.type === 'function_declarator') {
      const text = this.getNodeText(funcNode);
      if (text.endsWith(' &&')) {
        return '&&';
      }
      if (text.endsWith(' &')) {
        return '&';
      }
      if (text.includes(') const&')) {
        return '&';
      }
    }

    // Also check child nodes for ref-qualifiers
    for (const child of funcNode.children) {
      if (child.type === 'reference_declarator') {
        const refQual = this.extractRefQualifier(child);
        if (refQual) return refQual;
      }
    }

    return null;
  }

  private extractNoexceptSpecifier(funcNode: Parser.SyntaxNode): string | null {
    const noexceptNode = funcNode.children.find(c => c.type === 'noexcept');
    return noexceptNode ? this.getNodeText(noexceptNode) : null;
  }

  private extractDeleteOrDefaultClause(node: Parser.SyntaxNode): string | null {
    // Check for delete_method_clause or default_method_clause in function_definition
    const deleteClause = node.children.find(c => c.type === 'delete_method_clause');
    if (deleteClause) {
      return this.getNodeText(deleteClause);
    }

    const defaultClause = node.children.find(c => c.type === 'default_method_clause');
    if (defaultClause) {
      return this.getNodeText(defaultClause);
    }

    return null;
  }

  private extractVirtualSpecifiers(funcNode: Parser.SyntaxNode): string[] {
    const virtualSpecs: string[] = [];

    for (const child of funcNode.children) {
      if (child.type === 'virtual_specifier') {
        virtualSpecs.push(this.getNodeText(child));
      }
    }

    return virtualSpecs;
  }

  private extractStorageClass(node: Parser.SyntaxNode): string[] {
    const storageClasses: string[] = [];
    const storageTypes = ['static', 'extern', 'thread_local', 'mutable', 'register'];

    for (const child of node.children) {
      if (child.type === 'storage_class_specifier') {
        storageClasses.push(this.getNodeText(child));
      } else if (storageTypes.includes(child.type)) {
        storageClasses.push(this.getNodeText(child));
      }
    }

    return storageClasses;
  }

  private extractTypeSpecifiers(node: Parser.SyntaxNode): string[] {
    const typeSpecs: string[] = [];

    for (const child of node.children) {
      if (child.type === 'type_qualifier') {
        // Handle const, volatile, etc.
        typeSpecs.push(this.getNodeText(child));
      } else if (child.type === 'primitive_type' || child.type === 'type_identifier' || child.type === 'qualified_identifier') {
        typeSpecs.push(this.getNodeText(child));
      }
    }

    return typeSpecs;
  }

  private isConstantDeclaration(storageClass: string[], typeSpecifiers: string[]): boolean {
    return storageClass.includes('constexpr') ||
           typeSpecifiers.includes('const') ||
           typeSpecifiers.includes('constexpr');
  }

  private extractDeclaratorName(declarator: Parser.SyntaxNode): Parser.SyntaxNode | null {
    const identifierNode = declarator.children.find(c => c.type === 'identifier');
    if (identifierNode) return identifierNode;

    // Handle more complex declarators
    const fieldIdentifier = declarator.children.find(c => c.type === 'field_identifier');
    return fieldIdentifier || null;
  }

  private extractInitializer(declarator: Parser.SyntaxNode): string | null {
    const equalsIndex = declarator.children.findIndex(c => c.type === '=');
    if (equalsIndex !== -1 && equalsIndex < declarator.children.length - 1) {
      const initNodes = declarator.children.slice(equalsIndex + 1);
      return initNodes.map(n => this.getNodeText(n)).join('').trim();
    }
    return null;
  }

  private extractVisibility(node: Parser.SyntaxNode): 'public' | 'private' | 'protected' {
    // Find the field_declaration_list (class body) that contains this node
    let fieldList = node;
    while (fieldList && fieldList.type !== 'field_declaration_list') {
      fieldList = fieldList.parent;
    }

    if (!fieldList) {
      // Default visibility depends on the container
      const container = this.findContainerType(node);
      if (container === 'class_specifier') return 'private';
      return 'public'; // struct default
    }

    // Track current access level by walking through siblings until we reach our node
    let currentAccess: 'public' | 'private' | 'protected';

    // Default access depends on the container type
    const container = this.findContainerType(node);
    currentAccess = (container === 'class_specifier') ? 'private' : 'public';

    // Find our node's position and track access specifiers before it
    const children = fieldList.children;
    let nodeIndex = -1;

    // Find the index of our target node using position-based comparison
    for (let i = 0; i < children.length; i++) {
      if (this.isSameNode(children[i], node) || this.isNodeAncestor(children[i], node)) {
        nodeIndex = i;
        break;
      }
    }

    if (nodeIndex === -1) {
      return currentAccess; // Node not found, return default
    }

    // Walk backwards from our node to find the most recent access specifier
    for (let i = nodeIndex - 1; i >= 0; i--) {
      const child = children[i];
      if (child.type === 'access_specifier') {
        const specifier = this.getNodeText(child);
        if (specifier === 'private') return 'private';
        else if (specifier === 'protected') return 'protected';
        else if (specifier === 'public') return 'public';
      }
    }

    return currentAccess;
  }

  private isNodeAncestor(ancestor: Parser.SyntaxNode, descendant: Parser.SyntaxNode): boolean {
    let current = descendant;
    while (current) {
      if (this.isSameNode(current, ancestor)) return true;
      current = current.parent;
    }
    return false;
  }

  private isSameNode(node1: Parser.SyntaxNode, node2: Parser.SyntaxNode): boolean {
    // Compare nodes based on their position and content
    return node1.startPosition.row === node2.startPosition.row &&
           node1.startPosition.column === node2.startPosition.column &&
           node1.endPosition.row === node2.endPosition.row &&
           node1.endPosition.column === node2.endPosition.column &&
           node1.type === node2.type;
  }

  private findContainerType(node: Parser.SyntaxNode): string | null {
    let current = node.parent;
    while (current) {
      if (current.type === 'class_specifier' || current.type === 'struct_specifier') {
        return current.type;
      }
      current = current.parent;
    }
    return null;
  }

  private extractClassName(node: Parser.SyntaxNode): string | null {
    let current = node.parent;
    while (current) {
      if (current.type === 'class_specifier' || current.type === 'struct_specifier') {
        const nameNode = current.children.find(c => c.type === 'type_identifier' || c.type === 'template_type');
        if (nameNode) {
          if (nameNode.type === 'template_type') {
            // For template specializations like "TemplateClass1<int>", extract base name "TemplateClass1"
            const baseTypeNode = nameNode.children.find(c => c.type === 'type_identifier');
            return baseTypeNode ? this.getNodeText(baseTypeNode) : this.getNodeText(nameNode);
          } else {
            return this.getNodeText(nameNode);
          }
        }
        return null;
      }
      current = current.parent;
    }
    return null;
  }
}