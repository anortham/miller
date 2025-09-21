import { Parser } from 'web-tree-sitter';
import { BaseExtractor, Symbol, Relationship, SymbolKind, RelationshipKind } from './base-extractor.js';

/**
 * Dart language extractor that handles Dart-specific constructs including Flutter:
 * - Classes and their members
 * - Functions and methods
 * - Properties and fields
 * - Enums and their values
 * - Mixins and extensions
 * - Constructors (named, factory, const)
 * - Async/await patterns
 * - Generics and type parameters
 * - Flutter widgets and StatefulWidget patterns
 * - Imports and library dependencies
 *
 * Special focus on Flutter patterns since this enables mobile app development.
 */
export class DartExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];

    const visitNode = (node: Parser.SyntaxNode, parentId?: string) => {
      if (!node || !node.type) {
        return; // Skip invalid nodes
      }

      let symbol: Symbol | null = null;

      try {
        switch (node.type) {
          case 'class_definition':
            symbol = this.extractClass(node, parentId);
            break;
          case 'function_signature':
          case 'function_declaration':
            symbol = this.extractFunction(node, parentId);
            break;
          case 'method_signature':
          case 'method_declaration':
            symbol = this.extractMethod(node, parentId);
            break;
          case 'enum_declaration':
            symbol = this.extractEnum(node, parentId);
            break;
          case 'mixin_declaration':
            symbol = this.extractMixin(node, parentId);
            break;
          case 'extension_declaration':
            symbol = this.extractExtension(node, parentId);
            break;
          case 'constructor_signature':
          case 'factory_constructor_signature':
          case 'constant_constructor_signature':
            symbol = this.extractConstructor(node, parentId);
            break;
          case 'getter_signature':
            symbol = this.extractGetter(node, parentId);
            break;
          case 'setter_signature':
            symbol = this.extractSetter(node, parentId);
            break;
          case 'top_level_variable_declaration':
          case 'initialized_variable_definition':
            symbol = this.extractVariable(node, parentId);
            break;
          default:
            // Handle other Dart constructs
            break;
        }
      } catch (error) {
        console.warn(`Error extracting Dart symbol from ${node.type}:`, error);
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
      console.warn('Dart parsing failed:', error);
    }

    return symbols;
  }

  private extractClass(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Check if it's a Flutter widget (extends StatelessWidget, StatefulWidget, etc.)
    const isWidget = this.isFlutterWidget(node);
    const isAbstract = this.isAbstractClass(node);

    const classSymbol: Symbol = {
      id: this.generateId(name, node.startPosition),
      name,
      kind: isWidget ? SymbolKind.Class : SymbolKind.Class, // Could differentiate widgets later
      signature: this.extractClassSignature(node),
      startLine: node.startPosition.row,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row,
      endColumn: node.endPosition.column,
      filePath: this.filePath,
      language: this.language,
      parentId,
      visibility: 'public', // Dart classes are generally public unless private (_)
      documentation: this.extractDocumentation(node)
    };

    // Add Flutter widget annotation in documentation
    if (isWidget) {
      classSymbol.documentation = (classSymbol.documentation || '') + ' [Flutter Widget]';
    }

    return classSymbol;
  }

  private extractClassMembers(classNode: Parser.SyntaxNode, symbols: Symbol[], parentId: string): void {
    this.traverseTree(classNode, (node) => {
      try {
        switch (node.type) {
          case 'method_signature':
          case 'method_declaration':
            this.extractMethod(node, symbols, parentId);
            break;
          case 'constructor_signature':
            this.extractConstructor(node, symbols, parentId);
            break;
          case 'getter_signature':
            this.extractGetter(node, symbols, parentId);
            break;
          case 'setter_signature':
            this.extractSetter(node, symbols, parentId);
            break;
          case 'field_declaration':
            this.extractField(node, symbols, parentId);
            break;
        }
      } catch (error) {
        console.warn(`Error extracting Dart class member from ${node.type}:`, error);
      }
    });
  }

  private extractFunction(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const isAsync = this.isAsyncFunction(node);
    const isPrivate = name.startsWith('_');

    // Use Method kind if inside a class (has parentId), otherwise Function
    const symbolKind = parentId ? SymbolKind.Method : SymbolKind.Function;

    const functionSymbol = this.createSymbol(node, name, symbolKind, {
      signature: this.extractFunctionSignature(node),
      visibility: isPrivate ? 'private' : 'public',
      parentId,
      docComment: this.extractDocumentation(node)
    });

    // Add async annotation
    if (isAsync) {
      functionSymbol.metadata = { ...functionSymbol.metadata, isAsync: true };
    }

    return functionSymbol;
  }

  private extractMethod(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const isAsync = this.isAsyncFunction(node);
    const isStatic = this.isStaticMethod(node);
    const isPrivate = name.startsWith('_');
    const isOverride = this.isOverrideMethod(node);
    const isFlutterLifecycle = this.isFlutterLifecycleMethod(name);

    const methodSymbol = this.createSymbol(node, name, SymbolKind.Method, {
      signature: this.extractFunctionSignature(node),
      visibility: isPrivate ? 'private' : 'public',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: {
        isAsync,
        isStatic,
        isOverride,
        isFlutterLifecycle
      }
    });

    return methodSymbol;
  }

  private extractConstructor(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Extract constructor name more precisely
    let constructorName = 'Constructor';

    // For different constructor types, extract names differently
    if (node.type === 'factory_constructor_signature') {
      // Factory constructor: factory ClassName.methodName
      const identifiers = [];
      let skipNext = false;
      this.traverseTree(node, (child) => {
        if (child.type === 'identifier' && !skipNext) {
          identifiers.push(this.getNodeText(child));
          if (identifiers.length >= 2) skipNext = true; // Only take first 2 identifiers
        }
      });
      constructorName = identifiers.slice(0, 2).join('.');
    } else if (node.type === 'constant_constructor_signature') {
      // Const constructor: const ClassName.methodName
      const identifiers = [];
      let skipNext = false;
      this.traverseTree(node, (child) => {
        if (child.type === 'identifier' && !skipNext) {
          identifiers.push(this.getNodeText(child));
          if (identifiers.length >= 2) skipNext = true; // Only take first 2 identifiers
        }
      });
      constructorName = identifiers.slice(0, 2).join('.');
    } else {
      // Regular constructor or named constructor
      const directChildren = node.children?.filter(child => child.type === 'identifier') || [];
      if (directChildren.length === 1) {
        // Default constructor: ClassName()
        constructorName = this.getNodeText(directChildren[0]);
      } else if (directChildren.length >= 2) {
        // Named constructor: ClassName.namedConstructor()
        constructorName = directChildren.slice(0, 2).map(child => this.getNodeText(child)).join('.');
      }
    }

    const isFactory = this.isFactoryConstructor(node);
    const isConst = this.isConstConstructor(node);

    const constructorSymbol = this.createSymbol(node, constructorName, SymbolKind.Constructor, {
      signature: this.extractConstructorSignature(node),
      visibility: 'public',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: {
        isFactory,
        isConst
      }
    });

    return constructorSymbol;
  }

  private extractField(node: Parser.SyntaxNode, symbols: Symbol[], parentId: string): void {
    this.traverseTree(node, (fieldNode) => {
      if (fieldNode.type === 'initialized_variable_definition') {
        const nameNode = this.findChildByType(fieldNode, 'identifier');
        if (!nameNode) return;

        const fieldName = this.getNodeText(nameNode);
        const isPrivate = fieldName.startsWith('_');
        const isFinal = this.isFinalField(fieldNode);
        const isStatic = this.isStaticField(fieldNode);

        const fieldSymbol: Symbol = {
          id: this.generateId(`${fieldName}_field`, fieldNode.startPosition),
          name: fieldName,
          kind: SymbolKind.Property,
          signature: this.extractFieldSignature(fieldNode),
          startLine: fieldNode.startPosition.row,
          startColumn: fieldNode.startPosition.column,
          endLine: fieldNode.endPosition.row,
          endColumn: fieldNode.endPosition.column,
          filePath: this.filePath,
          language: this.language,
          parentId,
          visibility: isPrivate ? 'private' : 'public'
        };

        // Add field annotations
        const annotations: string[] = [];
        if (isFinal) annotations.push('Final');
        if (isStatic) annotations.push('Static');

        if (annotations.length > 0) {
          fieldSymbol.documentation = `[${annotations.join(', ')}]`;
        }

        symbols.push(fieldSymbol);
      }
    });
  }

  private extractGetter(node: Parser.SyntaxNode, symbols: Symbol[], parentId: string): void {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return;

    const name = this.getNodeText(nameNode);
    const isPrivate = name.startsWith('_');

    const getterSymbol: Symbol = {
      id: this.generateId(`${name}_get`, node.startPosition),
      name,
      kind: SymbolKind.Property,
      signature: `get ${name}`,
      startLine: node.startPosition.row,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row,
      endColumn: node.endPosition.column,
      filePath: this.filePath,
      language: this.language,
      parentId,
      visibility: isPrivate ? 'private' : 'public',
      documentation: '[Getter]'
    };

    symbols.push(getterSymbol);
  }

  private extractSetter(node: Parser.SyntaxNode, symbols: Symbol[], parentId: string): void {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return;

    const name = this.getNodeText(nameNode);
    const isPrivate = name.startsWith('_');

    const setterSymbol: Symbol = {
      id: this.generateId(`${name}_set`, node.startPosition),
      name,
      kind: SymbolKind.Property,
      signature: `set ${name}`,
      startLine: node.startPosition.row,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row,
      endColumn: node.endPosition.column,
      filePath: this.filePath,
      language: this.language,
      parentId,
      visibility: isPrivate ? 'private' : 'public',
      documentation: '[Setter]'
    };

    symbols.push(setterSymbol);
  }

  private extractEnum(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    const enumSymbol = this.createSymbol(node, name, SymbolKind.Enum, {
      signature: `enum ${name}`,
      visibility: 'public',
      parentId,
      docComment: this.extractDocumentation(node)
    });

    return enumSymbol;
  }

  private extractEnumValues(enumNode: Parser.SyntaxNode, symbols: Symbol[], parentId: string): void {
    this.traverseTree(enumNode, (node) => {
      if (node.type === 'enum_constant') {
        const nameNode = this.findChildByType(node, 'identifier');
        if (!nameNode) return;

        const valueName = this.getNodeText(nameNode);

        const valueSymbol: Symbol = {
          id: this.generateId(`${valueName}_enum_value`, node.startPosition),
          name: valueName,
          kind: SymbolKind.EnumMember,
          signature: valueName,
          startLine: node.startPosition.row,
          startColumn: node.startPosition.column,
          endLine: node.endPosition.row,
          endColumn: node.endPosition.column,
          filePath: this.filePath,
          language: this.language,
          parentId,
          visibility: 'public'
        };

        symbols.push(valueSymbol);
      }
    });
  }

  private extractMixin(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    const mixinSymbol = this.createSymbol(node, name, SymbolKind.Interface, {
      signature: `mixin ${name}`,
      visibility: 'public',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isMixin: true }
    });

    return mixinSymbol;
  }

  private extractExtension(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    const extensionSymbol = this.createSymbol(node, name, SymbolKind.Module, {
      signature: `extension ${name}`,
      visibility: 'public',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isExtension: true }
    });

    return extensionSymbol;
  }

  private extractVariable(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Look for the first variable definition in this node
    let foundVariable: Symbol | null = null;

    this.traverseTree(node, (varNode) => {
      if (foundVariable) return; // Already found one

      if (varNode.type === 'initialized_variable_definition') {
        const nameNode = this.findChildByType(varNode, 'identifier');
        if (!nameNode) return;

        const name = this.getNodeText(nameNode);
        const isPrivate = name.startsWith('_');
        const isFinal = this.isFinalVariable(varNode);
        const isConst = this.isConstVariable(varNode);

        foundVariable = this.createSymbol(varNode, name,
          (isFinal || isConst) ? SymbolKind.Constant : SymbolKind.Variable, {
          signature: this.extractVariableSignature(varNode),
          visibility: isPrivate ? 'private' : 'public',
          parentId,
          metadata: { isFinal, isConst }
        });
      }
    });

    return foundVariable;
  }

  // Flutter-specific helper methods
  private isFlutterWidget(classNode: Parser.SyntaxNode): boolean {
    const extendsClause = this.findChildByType(classNode, 'superclass');
    if (!extendsClause) return false;

    const superclassName = this.getNodeText(extendsClause);
    const flutterWidgets = [
      'StatelessWidget', 'StatefulWidget', 'Widget', 'PreferredSizeWidget',
      'RenderObjectWidget', 'SingleChildRenderObjectWidget', 'MultiChildRenderObjectWidget'
    ];

    return flutterWidgets.some(widget => superclassName.includes(widget));
  }

  private isFlutterLifecycleMethod(methodName: string): boolean {
    const lifecycleMethods = [
      'initState', 'dispose', 'build', 'didChangeDependencies',
      'didUpdateWidget', 'deactivate', 'setState'
    ];
    return lifecycleMethods.includes(methodName);
  }

  // Dart language helper methods
  private isAbstractClass(node: Parser.SyntaxNode): boolean {
    return this.getNodeText(node).includes('abstract');
  }

  private isAsyncFunction(node: Parser.SyntaxNode): boolean {
    return this.getNodeText(node).includes('async');
  }

  private isStaticMethod(node: Parser.SyntaxNode): boolean {
    return this.getNodeText(node).includes('static');
  }

  private isOverrideMethod(node: Parser.SyntaxNode): boolean {
    return this.getNodeText(node).includes('@override');
  }

  private isFactoryConstructor(node: Parser.SyntaxNode): boolean {
    return this.getNodeText(node).includes('factory');
  }

  private isConstConstructor(node: Parser.SyntaxNode): boolean {
    return this.getNodeText(node).includes('const');
  }

  private isFinalField(node: Parser.SyntaxNode): boolean {
    return this.getNodeText(node).includes('final');
  }

  private isStaticField(node: Parser.SyntaxNode): boolean {
    return this.getNodeText(node).includes('static');
  }

  private isFinalVariable(node: Parser.SyntaxNode): boolean {
    return this.getNodeText(node).includes('final');
  }

  private isConstVariable(node: Parser.SyntaxNode): boolean {
    return this.getNodeText(node).includes('const');
  }

  private extractFunctionParameters(funcNode: Parser.SyntaxNode, symbols: Symbol[], parentId: string): void {
    const paramList = this.findChildByType(funcNode, 'formal_parameter_list');
    if (!paramList) return;

    this.traverseTree(paramList, (node) => {
      if (node.type === 'normal_formal_parameter' || node.type === 'default_formal_parameter') {
        const nameNode = this.findChildByType(node, 'identifier');
        if (!nameNode) return;

        const paramName = this.getNodeText(nameNode);

        const paramSymbol: Symbol = {
          id: this.generateId(`${paramName}_param`, node.startPosition),
          name: paramName,
          kind: SymbolKind.Variable,
          signature: `${paramName}`,
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

  // Signature extraction methods
  private extractClassSignature(node: Parser.SyntaxNode): string {
    const nameNode = this.findChildByType(node, 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'Unknown';

    const isAbstract = this.isAbstractClass(node);
    const abstractPrefix = isAbstract ? 'abstract ' : '';

    const extendsClause = this.findChildByType(node, 'superclass');
    const extendsText = extendsClause ? ` extends ${this.getNodeText(extendsClause)}` : '';

    const implementsClause = this.findChildByType(node, 'interfaces');
    const implementsText = implementsClause ? ` implements ${this.getNodeText(implementsClause)}` : '';

    return `${abstractPrefix}class ${name}${extendsText}${implementsText}`;
  }

  private extractFunctionSignature(node: Parser.SyntaxNode): string {
    const nameNode = this.findChildByType(node, 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknown';

    // This is a simplified signature - could be enhanced
    return `${name}()`;
  }

  private extractConstructorSignature(node: Parser.SyntaxNode): string {
    const isFactory = node.type === 'factory_constructor_signature';
    const isConst = node.type === 'constant_constructor_signature';

    // Extract constructor name (class name or named constructor)
    const identifiers = [];
    this.traverseTree(node, (child) => {
      if (child.type === 'identifier') {
        identifiers.push(this.getNodeText(child));
      }
    });

    let constructorName = identifiers.length > 0 ? identifiers.join('.') : 'Constructor';

    // Add prefixes
    const factoryPrefix = isFactory ? 'factory ' : '';
    const constPrefix = isConst ? 'const ' : '';

    return `${factoryPrefix}${constPrefix}${constructorName}()`;
  }

  private extractFieldSignature(node: Parser.SyntaxNode): string {
    const nameNode = this.findChildByType(node, 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknown';
    return name;
  }

  private extractVariableSignature(node: Parser.SyntaxNode): string {
    const nameNode = this.findChildByType(node, 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknown';
    return name;
  }

  private extractGetter(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const isPrivate = name.startsWith('_');

    const getterSymbol = this.createSymbol(node, name, SymbolKind.Property, {
      signature: `get ${name}`,
      visibility: isPrivate ? 'private' : 'public',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isGetter: true }
    });

    return getterSymbol;
  }

  private extractSetter(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const isPrivate = name.startsWith('_');

    const setterSymbol = this.createSymbol(node, name, SymbolKind.Property, {
      signature: `set ${name}`,
      visibility: isPrivate ? 'private' : 'public',
      parentId,
      docComment: this.extractDocumentation(node),
      metadata: { isSetter: true }
    });

    return setterSymbol;
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];

    this.traverseTree(tree.rootNode, (node) => {
      try {
        switch (node.type) {
          case 'class_definition':
            this.extractClassRelationships(node, symbols, relationships);
            break;
          case 'method_invocation':
            this.extractMethodCallRelationships(node, symbols, relationships);
            break;
        }
      } catch (error) {
        console.warn(`Error extracting Dart relationship from ${node.type}:`, error);
      }
    });

    return relationships;
  }

  private extractClassRelationships(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    const className = this.findChildByType(node, 'identifier');
    if (!className) return;

    const classSymbol = symbols.find(s => s.name === this.getNodeText(className) && s.kind === SymbolKind.Class);
    if (!classSymbol) return;

    // Extract inheritance relationships
    const extendsClause = this.findChildByType(node, 'superclass');
    if (extendsClause) {
      const superclassName = this.getNodeText(extendsClause);
      const superclassSymbol = symbols.find(s => s.name === superclassName && s.kind === SymbolKind.Class);

      if (superclassSymbol) {
        relationships.push({
          id: this.generateId(`extends_${superclassName}`, node.startPosition),
          sourceId: classSymbol.id,
          targetId: superclassSymbol.id,
          kind: RelationshipKind.Extends,
          filePath: this.filePath,
          startLine: node.startPosition.row,
          startColumn: node.startPosition.column,
          endLine: node.endPosition.row,
          endColumn: node.endPosition.column
        });
      }
    }
  }

  private extractMethodCallRelationships(node: Parser.SyntaxNode, symbols: Symbol[], relationships: Relationship[]): void {
    // Extract method call relationships for cross-method dependencies
    // This could be expanded for more detailed call graph analysis
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();

    // Simple type inference based on symbol metadata and signatures
    for (const symbol of symbols) {
      if (symbol.signature) {
        // Extract type from signatures like "int counter = 0" or "String name"
        const typeMatch = symbol.signature.match(/^(\w+)\s+\w+/);
        if (typeMatch) {
          types.set(symbol.name, typeMatch[1]);
        }
      }

      // Use metadata for final/const detection
      if (symbol.metadata?.isFinal) {
        types.set(symbol.name, types.get(symbol.name) || 'final');
      }
      if (symbol.metadata?.isConst) {
        types.set(symbol.name, types.get(symbol.name) || 'const');
      }
    }

    return types;
  }

  extractTypes(tree: Parser.Tree): Map<string, string> {
    const types = new Map<string, string>();

    this.traverseTree(tree.rootNode, (node) => {
      if (node.type === 'initialized_variable_definition') {
        const nameNode = this.findChildByType(node, 'identifier');
        const typeNode = this.findChildByType(node, 'type_annotation');

        if (nameNode && typeNode) {
          const varName = this.getNodeText(nameNode);
          const varType = this.getNodeText(typeNode);
          types.set(varName, varType);
        }
      }
    });

    return types;
  }
}