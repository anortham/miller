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

    this.traverseTree(tree.rootNode, (node) => {
      try {
        switch (node.type) {
          case 'class_definition':
            this.extractClass(node, symbols);
            break;
          case 'function_signature':
          case 'function_declaration':
            this.extractFunction(node, symbols);
            break;
          case 'method_signature':
          case 'method_declaration':
            this.extractMethod(node, symbols);
            break;
          case 'enum_declaration':
            this.extractEnum(node, symbols);
            break;
          case 'mixin_declaration':
            this.extractMixin(node, symbols);
            break;
          case 'extension_declaration':
            this.extractExtension(node, symbols);
            break;
          case 'constructor_signature':
            this.extractConstructor(node, symbols);
            break;
          case 'top_level_variable_declaration':
          case 'initialized_variable_definition':
            this.extractVariable(node, symbols);
            break;
          default:
            // Handle other Dart constructs
            break;
        }
      } catch (error) {
        console.warn(`Error extracting Dart symbol from ${node.type}:`, error);
      }
    });

    return symbols;
  }

  private extractClass(node: Parser.SyntaxNode, symbols: Symbol[]): void {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return;

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
      parentId: undefined,
      visibility: 'public', // Dart classes are generally public unless private (_)
      documentation: this.extractDocumentation(node)
    };

    // Add Flutter widget annotation in documentation
    if (isWidget) {
      classSymbol.documentation = (classSymbol.documentation || '') + ' [Flutter Widget]';
    }

    symbols.push(classSymbol);

    // Extract class members
    this.extractClassMembers(node, symbols, classSymbol.id);
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

  private extractFunction(node: Parser.SyntaxNode, symbols: Symbol[], parentId?: string): void {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return;

    const name = this.getNodeText(nameNode);
    const isAsync = this.isAsyncFunction(node);
    const isPrivate = name.startsWith('_');

    const functionSymbol: Symbol = {
      id: this.generateId(name, node.startPosition),
      name,
      kind: SymbolKind.Function,
      signature: this.extractFunctionSignature(node),
      startLine: node.startPosition.row,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row,
      endColumn: node.endPosition.column,
      filePath: this.filePath,
      language: this.language,
      parentId,
      visibility: isPrivate ? 'private' : 'public',
      documentation: this.extractDocumentation(node)
    };

    // Add async annotation
    if (isAsync) {
      functionSymbol.documentation = (functionSymbol.documentation || '') + ' [Async]';
    }

    symbols.push(functionSymbol);

    // Extract function parameters
    this.extractFunctionParameters(node, symbols, functionSymbol.id);
  }

  private extractMethod(node: Parser.SyntaxNode, symbols: Symbol[], parentId?: string): void {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return;

    const name = this.getNodeText(nameNode);
    const isAsync = this.isAsyncFunction(node);
    const isStatic = this.isStaticMethod(node);
    const isPrivate = name.startsWith('_');
    const isOverride = this.isOverrideMethod(node);

    // Check for Flutter lifecycle methods
    const isFlutterLifecycle = this.isFlutterLifecycleMethod(name);

    const methodSymbol: Symbol = {
      id: this.generateId(name, node.startPosition),
      name,
      kind: SymbolKind.Method,
      signature: this.extractFunctionSignature(node),
      startLine: node.startPosition.row,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row,
      endColumn: node.endPosition.column,
      filePath: this.filePath,
      language: this.language,
      parentId,
      visibility: isPrivate ? 'private' : 'public',
      documentation: this.extractDocumentation(node)
    };

    // Add method annotations
    const annotations: string[] = [];
    if (isAsync) annotations.push('Async');
    if (isStatic) annotations.push('Static');
    if (isOverride) annotations.push('Override');
    if (isFlutterLifecycle) annotations.push('Flutter Lifecycle');

    if (annotations.length > 0) {
      methodSymbol.documentation = (methodSymbol.documentation || '') + ` [${annotations.join(', ')}]`;
    }

    symbols.push(methodSymbol);

    // Extract method parameters
    this.extractFunctionParameters(node, symbols, methodSymbol.id);
  }

  private extractConstructor(node: Parser.SyntaxNode, symbols: Symbol[], parentId?: string): void {
    const nameNode = this.findChildByType(node, 'identifier');
    const className = parentId ? symbols.find(s => s.id === parentId)?.name : 'Unknown';

    let constructorName = className || 'Constructor';
    if (nameNode) {
      const explicitName = this.getNodeText(nameNode);
      if (explicitName !== className) {
        constructorName = `${className}.${explicitName}`;
      }
    }

    const isFactory = this.isFactoryConstructor(node);
    const isConst = this.isConstConstructor(node);

    const constructorSymbol: Symbol = {
      id: this.generateId(constructorName, node.startPosition),
      name: constructorName,
      kind: SymbolKind.Constructor,
      signature: this.extractConstructorSignature(node, className || ''),
      startLine: node.startPosition.row,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row,
      endColumn: node.endPosition.column,
      filePath: this.filePath,
      language: this.language,
      parentId,
      visibility: 'public',
      documentation: this.extractDocumentation(node)
    };

    // Add constructor annotations
    const annotations: string[] = [];
    if (isFactory) annotations.push('Factory');
    if (isConst) annotations.push('Const');

    if (annotations.length > 0) {
      constructorSymbol.documentation = (constructorSymbol.documentation || '') + ` [${annotations.join(', ')}]`;
    }

    symbols.push(constructorSymbol);

    // Extract constructor parameters
    this.extractFunctionParameters(node, symbols, constructorSymbol.id);
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

  private extractEnum(node: Parser.SyntaxNode, symbols: Symbol[]): void {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return;

    const name = this.getNodeText(nameNode);

    const enumSymbol: Symbol = {
      id: this.generateId(name, node.startPosition),
      name,
      kind: SymbolKind.Enum,
      signature: `enum ${name}`,
      startLine: node.startPosition.row,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row,
      endColumn: node.endPosition.column,
      filePath: this.filePath,
      language: this.language,
      parentId: undefined,
      visibility: 'public',
      documentation: this.extractDocumentation(node)
    };

    symbols.push(enumSymbol);

    // Extract enum values
    this.extractEnumValues(node, symbols, enumSymbol.id);
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

  private extractMixin(node: Parser.SyntaxNode, symbols: Symbol[]): void {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return;

    const name = this.getNodeText(nameNode);

    const mixinSymbol: Symbol = {
      id: this.generateId(name, node.startPosition),
      name,
      kind: SymbolKind.Interface, // Mixins are like interfaces
      signature: `mixin ${name}`,
      startLine: node.startPosition.row,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row,
      endColumn: node.endPosition.column,
      filePath: this.filePath,
      language: this.language,
      parentId: undefined,
      visibility: 'public',
      documentation: this.extractDocumentation(node) + ' [Mixin]'
    };

    symbols.push(mixinSymbol);

    // Extract mixin members
    this.extractClassMembers(node, symbols, mixinSymbol.id);
  }

  private extractExtension(node: Parser.SyntaxNode, symbols: Symbol[]): void {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return;

    const name = this.getNodeText(nameNode);

    const extensionSymbol: Symbol = {
      id: this.generateId(name, node.startPosition),
      name,
      kind: SymbolKind.Module, // Extensions are like modules
      signature: `extension ${name}`,
      startLine: node.startPosition.row,
      startColumn: node.startPosition.column,
      endLine: node.endPosition.row,
      endColumn: node.endPosition.column,
      filePath: this.filePath,
      language: this.language,
      parentId: undefined,
      visibility: 'public',
      documentation: this.extractDocumentation(node) + ' [Extension]'
    };

    symbols.push(extensionSymbol);

    // Extract extension members
    this.extractClassMembers(node, symbols, extensionSymbol.id);
  }

  private extractVariable(node: Parser.SyntaxNode, symbols: Symbol[]): void {
    this.traverseTree(node, (varNode) => {
      if (varNode.type === 'initialized_variable_definition') {
        const nameNode = this.findChildByType(varNode, 'identifier');
        if (!nameNode) return;

        const name = this.getNodeText(nameNode);
        const isPrivate = name.startsWith('_');
        const isFinal = this.isFinalVariable(varNode);
        const isConst = this.isConstVariable(varNode);

        const variableSymbol: Symbol = {
          id: this.generateId(name, varNode.startPosition),
          name,
          kind: (isFinal || isConst) ? SymbolKind.Constant : SymbolKind.Variable,
          signature: this.extractVariableSignature(varNode),
          startLine: varNode.startPosition.row,
          startColumn: varNode.startPosition.column,
          endLine: varNode.endPosition.row,
          endColumn: varNode.endPosition.column,
          filePath: this.filePath,
          language: this.language,
          parentId: undefined,
          visibility: isPrivate ? 'private' : 'public'
        };

        symbols.push(variableSymbol);
      }
    });
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

    const extendsClause = this.findChildByType(node, 'superclass');
    const extendsText = extendsClause ? ` extends ${this.getNodeText(extendsClause)}` : '';

    const implementsClause = this.findChildByType(node, 'interfaces');
    const implementsText = implementsClause ? ` implements ${this.getNodeText(implementsClause)}` : '';

    return `class ${name}${extendsText}${implementsText}`;
  }

  private extractFunctionSignature(node: Parser.SyntaxNode): string {
    const nameNode = this.findChildByType(node, 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknown';

    // This is a simplified signature - could be enhanced
    return `${name}()`;
  }

  private extractConstructorSignature(node: Parser.SyntaxNode, className: string): string {
    const nameNode = this.findChildByType(node, 'identifier');
    if (nameNode && this.getNodeText(nameNode) !== className) {
      return `${className}.${this.getNodeText(nameNode)}()`;
    }
    return `${className}()`;
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