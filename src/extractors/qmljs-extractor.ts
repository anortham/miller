import { Parser } from 'web-tree-sitter';
import {
  BaseExtractor,
  Symbol,
  SymbolKind,
  Relationship,
  RelationshipKind
} from './base-extractor.js';

export class QMLJSExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    const visitNode = (node: Parser.SyntaxNode, parentId?: string) => {
      let symbol: Symbol | null = null;

      switch (node.type) {
        // QML components (Rectangle, Item, etc.) parsed as identifiers
        case 'identifier':
          if (this.isQmlComponentIdentifier(node)) {
            symbol = this.extractQmlComponentFromIdentifier(node, parentId);
          }
          break;

        // QML labeled statements (id:, property:, signal:)
        case 'labeled_statement':
          symbol = this.extractFromLabeledStatement(node, parentId);
          break;

        // ERROR nodes often contain QML content when imports confuse the parser
        case 'ERROR':
          symbol = this.extractFromErrorNode(node, parentId);
          break;

        // JavaScript function declarations in QML context
        case 'function_declaration':
        case 'function_expression':
        case 'arrow_function':
          symbol = this.extractJavaScriptFunction(node, parentId);
          break;

        // Import statements
        case 'import_statement':
        case 'import_declaration':
          symbol = this.extractQmlImport(node, parentId);
          break;
      }

      if (symbol) {
        symbols.push(symbol);
        parentId = symbol.id;
      }

      // Recursively visit children
      for (const child of node.children) {
        visitNode(child, parentId);
      }
    };

    visitNode(tree.rootNode);

    // Add any additional symbols from multi-symbol ERROR nodes
    if (this.additionalSymbols.length > 0) {
      symbols.push(...this.additionalSymbols);
      this.additionalSymbols = []; // Clear for next extraction
    }

    // Post-process: Associate component IDs with their components
    this.associateComponentIds(symbols);

    return symbols;
  }

  // Track IDs for component association
  private componentIds = new Map<string, string>(); // componentType -> id
  private additionalSymbols: Symbol[] = []; // Store multiple symbols from single ERROR nodes

  private associateComponentIds(symbols: Symbol[]): void {
    // Find all id declarations and their associated component types
    const idSymbols = symbols.filter(s => s.metadata?.qmlId);
    const componentSymbols = symbols.filter(s => s.metadata?.isQmlComponent);

    // For each ID, find the nearest component and update its name
    for (const idSymbol of idSymbols) {
      const idValue = idSymbol.metadata?.idValue;
      if (!idValue) continue;

      // Find the component that comes just before or after this ID in the file
      const nearestComponent = this.findNearestComponent(idSymbol, componentSymbols);
      if (nearestComponent) {
        // Update the component's name and signature
        nearestComponent.name = idValue;
        nearestComponent.signature = `${nearestComponent.metadata?.qmlType} { id: ${idValue} }`;
      }
    }
  }

  private findNearestComponent(idSymbol: Symbol, componentSymbols: Symbol[]): Symbol | null {
    // Find the component that's closest to this ID symbol by line number
    let nearestComponent: Symbol | null = null;
    let minDistance = Infinity;

    for (const component of componentSymbols) {
      // Calculate distance (prefer components just before the ID)
      const distance = Math.abs(component.startLine - idSymbol.startLine);

      // Prefer components that come before the ID or are very close
      if (distance < minDistance && component.startLine <= idSymbol.startLine + 2) {
        minDistance = distance;
        nearestComponent = component;
      }
    }

    return nearestComponent;
  }

  private extractFromErrorNode(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const text = this.getNodeText(node);

    // Parse multiple declarations from this ERROR node
    const symbols: Symbol[] = [];

    // First, try to extract QML component with ID as a single unit
    const componentWithIdSymbol = this.extractQmlComponentWithId(text, node, parentId);
    if (componentWithIdSymbol) {
      symbols.push(componentWithIdSymbol);
    } else {
      // Extract ID first (for component association)
      if (text.includes('id:')) {
        const idSymbol = this.extractIdFromErrorText(text, node, parentId);
        if (idSymbol) symbols.push(idSymbol);
      }
    }

    // Extract properties
    const propertyMatches = [...text.matchAll(/(?:readonly\s+)?property\s+\w+\s+\w+/g)];
    for (const match of propertyMatches) {
      const propertySymbol = this.extractPropertyFromErrorText(match[0], node, parentId);
      if (propertySymbol) symbols.push(propertySymbol);
    }

    // Extract signals
    const signalMatches = [...text.matchAll(/signal\s+\w+\s*\([^)]*\)/g)];
    for (const match of signalMatches) {
      const signalSymbol = this.extractSignalFromErrorText(match[0], node, parentId);
      if (signalSymbol) symbols.push(signalSymbol);
    }

    // Extract property assignments
    if (this.isQmlPropertyAssignment(text)) {
      const assignmentSymbol = this.extractPropertyAssignmentFromErrorText(text, node, parentId);
      if (assignmentSymbol) symbols.push(assignmentSymbol);
    }

    // Store additional symbols for later processing
    if (symbols.length > 1) {
      this.additionalSymbols = this.additionalSymbols || [];
      this.additionalSymbols.push(...symbols.slice(1));
    }

    // Return the first symbol (or null if none)
    return symbols[0] || null;
  }

  private extractPropertyFromErrorText(text: string, node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Parse "property string title" or "readonly property string version"
    const readonlyMatch = text.match(/readonly\s+property\s+(\w+)\s+(\w+)/);
    const propertyMatch = text.match(/property\s+(\w+)\s+(\w+)/);

    if (readonlyMatch) {
      const [, type, name] = readonlyMatch;
      return this.createSymbol(node, name, SymbolKind.Property, {
        signature: `readonly property ${type} ${name}`,
        parentId,
        metadata: {
          qmlProperty: true,
          propertyType: type,
          isReadonly: true
        }
      });
    }

    if (propertyMatch) {
      const [, type, name] = propertyMatch;
      return this.createSymbol(node, name, SymbolKind.Property, {
        signature: `property ${type} ${name}`,
        parentId,
        metadata: {
          qmlProperty: true,
          propertyType: type
        }
      });
    }

    return null;
  }

  private extractSignalFromErrorText(text: string, node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Parse "signal pageChanged(int newPage)"
    const signalMatch = text.match(/signal\s+(\w+)\s*\(([^)]*)\)/);
    if (signalMatch) {
      const [, name, paramString] = signalMatch;
      const parameters = paramString ? paramString.split(',').map(p => p.trim()).filter(p => p) : [];

      return this.createSymbol(node, name, SymbolKind.Function, {
        signature: `signal ${name}(${parameters.join(', ')})`,
        parentId,
        metadata: {
          qmlSignal: true,
          parameters: parameters,
          isEvent: true
        }
      });
    }

    return null;
  }

  private extractIdFromErrorText(text: string, node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Parse "id: mainWindow"
    const idMatch = text.match(/id:\s*(\w+)/);
    if (idMatch) {
      const idValue = idMatch[1];
      this.componentIds.set('current', idValue);

      return this.createSymbol(node, 'id', SymbolKind.Property, {
        signature: `id: ${idValue}`,
        parentId,
        metadata: {
          qmlId: true,
          idValue: idValue
        }
      });
    }

    return null;
  }

  private extractQmlComponentWithId(text: string, node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Look for patterns like "Text {\n    id: titleText" or "Button {\n    id: actionButton"
    // Make the regex more flexible to handle newlines and whitespace
    const componentWithIdMatch = text.match(/(Rectangle|Item|Text|Button|Column|Row|Window|ApplicationWindow)\s*\{[\s\S]*?id:\s*(\w+)/);
    if (componentWithIdMatch) {
      const [, componentType, idValue] = componentWithIdMatch;
      return this.createSymbol(node, idValue, SymbolKind.Class, {
        signature: `${componentType} { id: ${idValue} }`,
        parentId,
        metadata: {
          qmlType: componentType,
          isQmlComponent: true,
          originalType: componentType,
          hasExplicitId: true
        }
      });
    }

    // Also try simpler patterns for nested components
    const nestedComponentMatch = text.match(/(Text|Button)\s*\{[^}]*id:\s*(\w+)/);
    if (nestedComponentMatch) {
      const [, componentType, idValue] = nestedComponentMatch;
      return this.createSymbol(node, idValue, SymbolKind.Class, {
        signature: `${componentType} { id: ${idValue} }`,
        parentId,
        metadata: {
          qmlType: componentType,
          isQmlComponent: true,
          originalType: componentType,
          hasExplicitId: true
        }
      });
    }

    return null;
  }

  private isQmlPropertyAssignment(text: string): boolean {
    // Check for common QML property patterns
    const propertyPatterns = [
      /\b(width|height|color|visible|enabled|opacity|x|y|z|rotation)\s*:/,
      /\b(text|font|anchors)\s*:/,
      /\b(margins|fill|centerIn)\s*:/
    ];

    return propertyPatterns.some(pattern => pattern.test(text));
  }

  private extractPropertyAssignmentFromErrorText(text: string, node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Extract property assignments like "width: 800" or "text: 'Hello'"
    const assignmentMatch = text.match(/(\w+)\s*:\s*([^,\n]+)/);
    if (assignmentMatch) {
      const [, name, value] = assignmentMatch;

      // Don't extract IDs as property assignments - they should be handled by ID-specific methods
      if (name === 'id') {
        return null;
      }

      const cleanValue = value.trim();

      return this.createSymbol(node, name, SymbolKind.Property, {
        signature: `${name}: ${cleanValue}`,
        parentId,
        metadata: {
          qmlProperty: true,
          propertyType: this.inferPropertyType(cleanValue),
          defaultValue: cleanValue
        }
      });
    }

    return null;
  }

  private extractFromLabeledStatement(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const labelNode = node.children.find(child => child.type === 'statement_identifier');
    if (!labelNode) return null;

    const label = this.getNodeText(labelNode);

    switch (label) {
      case 'id':
        return this.extractIdFromLabeledStatement(node, parentId);
      case 'property':
        return this.extractPropertyFromLabeledStatement(node, parentId);
      case 'signal':
        return this.extractSignalFromLabeledStatement(node, parentId);
      default:
        return null;
    }
  }

  private extractIdFromLabeledStatement(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Find the identifier after the colon
    const expressionStatement = node.children.find(child => child.type === 'expression_statement');
    if (expressionStatement) {
      const identifier = expressionStatement.children.find(child => child.type === 'identifier');
      if (identifier) {
        const idValue = this.getNodeText(identifier);


        // Store this ID for later component association
        this.componentIds.set('current', idValue);

        return this.createSymbol(node, 'id', SymbolKind.Property, {
          signature: `id: ${idValue}`,
          parentId,
          metadata: {
            qmlId: true,
            idValue: idValue
          }
        });
      }
    }
    return null;
  }

  private extractPropertyFromLabeledStatement(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Find ERROR node containing type and name, or parse from structure
    const errorNode = node.children.find(child => child.type === 'ERROR');
    const valueStatement = node.children.find(child => child.type === 'expression_statement');

    let propertyType = 'var';
    let propertyName = 'unknownProperty';
    let propertyValue = '';

    if (errorNode) {
      // Extract type and name from ERROR node children
      const identifiers = errorNode.children.filter(child => child.type === 'identifier');
      if (identifiers.length >= 2) {
        propertyType = this.getNodeText(identifiers[0]);
        propertyName = this.getNodeText(identifiers[1]);
      }
    }

    if (valueStatement) {
      const valueNode = valueStatement.children[0];
      if (valueNode) {
        propertyValue = this.getNodeText(valueNode);
      }
    }

    return this.createSymbol(node, propertyName, SymbolKind.Property, {
      signature: `property ${propertyType} ${propertyName}: ${propertyValue}`,
      parentId,
      metadata: {
        qmlProperty: true,
        propertyType: propertyType,
        defaultValue: propertyValue
      }
    });
  }

  private extractSignalFromLabeledStatement(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // For signal declarations in labeled statements
    const errorNode = node.children.find(child => child.type === 'ERROR');

    let signalName = 'unknownSignal';
    let parameters: string[] = [];

    if (errorNode) {
      const identifiers = errorNode.children.filter(child => child.type === 'identifier');
      if (identifiers.length >= 1) {
        signalName = this.getNodeText(identifiers[0]);
      }

      // Look for parentheses with parameters
      const text = this.getNodeText(errorNode);
      const matches = text.match(/\(([^)]*)\)/);
      if (matches && matches[1]) {
        parameters = matches[1].split(',').map(p => p.trim()).filter(p => p);
      }
    }

    return this.createSymbol(node, signalName, SymbolKind.Function, {
      signature: `signal ${signalName}(${parameters.join(', ')})`,
      parentId,
      metadata: {
        qmlSignal: true,
        parameters: parameters,
        isEvent: true
      }
    });
  }

  // New JavaScript AST-based extraction methods for QML
  private isQmlComponentIdentifier(node: Parser.SyntaxNode): boolean {
    const text = this.getNodeText(node);
    const qmlComponents = ['Rectangle', 'Item', 'Text', 'Button', 'Column', 'Row', 'Window', 'ApplicationWindow'];
    return qmlComponents.includes(text);
  }

  private isPropertyName(node: Parser.SyntaxNode): boolean {
    const text = this.getNodeText(node);
    const propertyNames = ['property', 'readonly', 'signal', 'id', 'width', 'height', 'color', 'title', 'currentPage', 'scaleFactor', 'isVisible', 'version', 'text', 'anchors'];
    return propertyNames.includes(text);
  }

  private isIdAssignment(node: Parser.SyntaxNode): boolean {
    const text = this.getNodeText(node);
    return text === 'id';
  }

  private extractPropertyFromContext(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const text = this.getNodeText(node);

    if (text === 'id') {
      return this.extractIdProperty(node, parentId);
    }

    if (text === 'property' || text === 'readonly') {
      return this.extractQmlPropertyDeclaration(node, parentId);
    }

    if (text === 'signal') {
      return this.extractSignalDeclaration(node, parentId);
    }

    // Handle other property assignments
    return this.extractRegularProperty(node, parentId);
  }

  private extractIdProperty(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Look for the next sibling that should be the id value
    const parent = node.parent;
    if (parent && parent.type === 'expression_statement') {
      const siblings = parent.parent?.children || [];
      const currentIndex = siblings.indexOf(parent);

      for (let i = currentIndex + 1; i < siblings.length; i++) {
        const sibling = siblings[i];
        if (sibling.type === 'identifier') {
          const idValue = this.getNodeText(sibling);
          return this.createSymbol(node, 'id', SymbolKind.Property, {
            signature: `id: ${idValue}`,
            parentId,
            metadata: {
              qmlId: true,
              idValue: idValue
            }
          });
        }
      }
    }
    return null;
  }

  private extractQmlPropertyDeclaration(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // QML property declarations like "property string title: 'value'"
    const parent = node.parent;
    if (!parent) return null;

    const siblings = parent.children;
    const nodeIndex = siblings.indexOf(node);

    // Look for pattern: property type name : value
    let propertyType = 'var';
    let propertyName = 'unknownProperty';
    let propertyValue = '';

    // Get type (next identifier after 'property')
    if (nodeIndex + 1 < siblings.length && siblings[nodeIndex + 1].type === 'identifier') {
      propertyType = this.getNodeText(siblings[nodeIndex + 1]);
    }

    // Get name (identifier after type)
    if (nodeIndex + 2 < siblings.length && siblings[nodeIndex + 2].type === 'identifier') {
      propertyName = this.getNodeText(siblings[nodeIndex + 2]);
    }

    // Get value (after colon)
    for (let i = nodeIndex + 3; i < siblings.length; i++) {
      if (siblings[i].type === 'string' || siblings[i].type === 'number' || siblings[i].type === 'string_literal') {
        propertyValue = this.getNodeText(siblings[i]);
        break;
      }
    }

    const isReadonly = this.getNodeText(node) === 'readonly';

    return this.createSymbol(node, propertyName, SymbolKind.Property, {
      signature: `${isReadonly ? 'readonly ' : ''}property ${propertyType} ${propertyName}: ${propertyValue}`,
      parentId,
      metadata: {
        qmlProperty: true,
        propertyType: propertyType,
        defaultValue: propertyValue,
        isReadonly: isReadonly
      }
    });
  }

  private extractSignalDeclaration(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // QML signal declarations like "signal pageChanged(int newPage)"
    const parent = node.parent;
    if (!parent) return null;

    const siblings = parent.children;
    const nodeIndex = siblings.indexOf(node);

    let signalName = 'unknownSignal';
    let parameters: string[] = [];

    // Get signal name (next identifier after 'signal')
    if (nodeIndex + 1 < siblings.length && siblings[nodeIndex + 1].type === 'identifier') {
      signalName = this.getNodeText(siblings[nodeIndex + 1]);
    }

    // Look for parameters in parentheses
    for (let i = nodeIndex + 2; i < siblings.length; i++) {
      if (siblings[i].type === 'formal_parameters' || siblings[i].text.includes('(')) {
        const paramText = this.getNodeText(siblings[i]);
        // Simple parameter extraction
        const matches = paramText.match(/\(([^)]*)\)/);
        if (matches && matches[1]) {
          parameters = matches[1].split(',').map(p => p.trim()).filter(p => p);
        }
        break;
      }
    }

    return this.createSymbol(node, signalName, SymbolKind.Function, {
      signature: `signal ${signalName}(${parameters.join(', ')})`,
      parentId,
      metadata: {
        qmlSignal: true,
        parameters: parameters,
        isEvent: true
      }
    });
  }

  private extractRegularProperty(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Handle regular property assignments like "width: 800"
    const parent = node.parent;
    if (!parent) return null;

    const propertyName = this.getNodeText(node);

    // Don't extract IDs as regular properties - they should be handled by ID-specific methods
    if (propertyName === 'id') {
      return null;
    }

    let propertyValue = '';

    // Look for colon and value
    const siblings = parent.children;
    const nodeIndex = siblings.indexOf(node);

    for (let i = nodeIndex + 1; i < siblings.length; i++) {
      const sibling = siblings[i];
      if (sibling.type === 'number' || sibling.type === 'string' || sibling.type === 'string_literal' || sibling.type === 'identifier') {
        propertyValue = this.getNodeText(sibling);
        break;
      }
    }

    if (propertyValue) {
      const propertyType = this.inferPropertyType(propertyValue);
      return this.createSymbol(node, propertyName, SymbolKind.Property, {
        signature: `${propertyName}: ${propertyValue}`,
        parentId,
        metadata: {
          qmlProperty: true,
          propertyType: propertyType,
          defaultValue: propertyValue
        }
      });
    }

    return null;
  }

  private extractQmlComponentFromIdentifier(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const componentType = this.getNodeText(node);

    // Look for an ID declaration in the component's context
    const componentId = this.findComponentId(node);
    let componentName = componentId || `Anonymous${componentType}`;


    return this.createSymbol(node, componentName, SymbolKind.Class, {
      signature: `${componentType} { id: ${componentName} }`,
      parentId,
      metadata: {
        qmlType: componentType,
        isQmlComponent: true,
        originalType: componentType,
        hasExplicitId: !!componentId
      }
    });
  }

  private findComponentId(componentNode: Parser.SyntaxNode): string | null {
    // Look in the component's parent context for id: declarations
    let current = componentNode.parent;

    // Traverse up to find a block or statement that might contain the ID
    while (current) {
      // Look for 'id' identifier followed by a value
      for (const child of current.children) {
        if (child.type === 'identifier' && this.getNodeText(child) === 'id') {
          // Look for the next sibling that contains the ID value
          const idValue = this.findIdValue(child);
          if (idValue) {
            return idValue;
          }
        }

        // Also check ERROR nodes for id: patterns
        if (child.type === 'ERROR') {
          const errorText = this.getNodeText(child);
          const idMatch = errorText.match(/id:\s*(\w+)/);
          if (idMatch) {
            return idMatch[1];
          }
        }

        // Check labeled statements for ID declarations
        if (child.type === 'labeled_statement') {
          const labelNode = child.children.find(c => c.type === 'statement_identifier');
          if (labelNode && this.getNodeText(labelNode) === 'id') {
            const expressionStatement = child.children.find(c => c.type === 'expression_statement');
            if (expressionStatement) {
              const identifier = expressionStatement.children.find(c => c.type === 'identifier');
              if (identifier) {
                return this.getNodeText(identifier);
              }
            }
          }
        }
      }

      current = current.parent;

      // Don't go too far up the tree
      if (current && (current.type === 'program' || current.type === 'source_file')) {
        break;
      }
    }

    return null;
  }

  private findIdValue(idNode: Parser.SyntaxNode): string | null {
    // Look for patterns after 'id' keyword
    const parent = idNode.parent;
    if (!parent) return null;

    const siblings = parent.children;
    const idIndex = siblings.indexOf(idNode);

    // Look for the value after id (could be after colon or directly)
    for (let i = idIndex + 1; i < siblings.length; i++) {
      const sibling = siblings[i];
      if (sibling.type === 'identifier') {
        return this.getNodeText(sibling);
      }
    }

    return null;
  }

  private extractPropertyFromIdentifier(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const propertyName = this.getNodeText(node);
    const propertyValue = this.findPropertyValue(node);
    const propertyType = this.inferPropertyType(propertyValue);

    return this.createSymbol(node, propertyName, SymbolKind.Property, {
      signature: `property ${propertyType} ${propertyName}: ${propertyValue || 'undefined'}`,
      parentId,
      metadata: {
        qmlProperty: true,
        propertyType: propertyType,
        defaultValue: propertyValue
      }
    });
  }

  private extractFromExpressionStatement(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Look for assignment expressions that represent property bindings
    const expression = node.children[0];
    if (expression?.type === 'assignment_expression') {
      return this.extractPropertyAssignment(expression, parentId);
    }
    return null;
  }

  private extractQmlObjectStructure(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // This represents the body of a QML component
    // We'll extract properties from the object's members
    return null; // For now, let child processing handle individual properties
  }

  private extractPropertyAssignment(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const left = node.childForFieldName('left');
    const right = node.childForFieldName('right');

    if (left && right) {
      const propertyName = this.getNodeText(left);
      const propertyValue = this.getNodeText(right);
      const propertyType = this.inferTypeFromValue(right);

      return this.createSymbol(node, propertyName, SymbolKind.Property, {
        signature: `${propertyName}: ${propertyValue}`,
        parentId,
        metadata: {
          qmlProperty: true,
          propertyType: propertyType,
          defaultValue: propertyValue
        }
      });
    }
    return null;
  }

  private findAssociatedId(node: Parser.SyntaxNode): string | null {
    // Look for id property in the same scope
    let current = node.parent;
    while (current) {
      // Look for assignment to 'id' property
      for (const child of current.children) {
        if (child.type === 'assignment_expression') {
          const left = child.childForFieldName('left');
          if (left && this.getNodeText(left) === 'id') {
            const right = child.childForFieldName('right');
            return right ? this.getNodeText(right) : null;
          }
        }
      }
      current = current.parent;
    }
    return null;
  }

  private findPropertyValue(node: Parser.SyntaxNode): string | null {
    const parent = node.parent;
    if (parent?.type === 'assignment_expression') {
      const right = parent.childForFieldName('right');
      return right ? this.getNodeText(right) : null;
    }
    return null;
  }

  private inferPropertyType(value: string | null): string {
    if (!value) return 'var';

    if (value.startsWith('"') || value.startsWith("'")) return 'string';
    if (/^\d+$/.test(value)) return 'int';
    if (/^\d+\.\d+$/.test(value)) return 'real';
    if (value === 'true' || value === 'false') return 'bool';

    return 'var';
  }

  private extractQmlComponent(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // Try to get component type name
    const typeNode = this.findComponentType(node);
    const typeName = typeNode ? this.getNodeText(typeNode) : 'QmlObject';

    // Try to get component ID
    const idNode = this.findComponentId(node);
    const name = idNode ? this.getNodeText(idNode) : `Anonymous${typeName}`;

    const signature = this.buildComponentSignature(node, typeName);
    const docComment = this.findDocComment(node);

    return this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      docComment,
      metadata: {
        qmlType: typeName,
        isQmlComponent: true
      }
    });
  }

  private extractQmlProperty(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const propertyName = this.findPropertyName(node);
    const propertyType = this.findPropertyType(node);
    const propertyValue = this.findPropertyValue(node);

    const name = propertyName || 'unknownProperty';
    const signature = this.buildPropertySignature(propertyName, propertyType, propertyValue);

    return this.createSymbol(node, name, SymbolKind.Property, {
      signature,
      parentId,
      metadata: {
        qmlProperty: true,
        propertyType: propertyType,
        defaultValue: propertyValue,
        isBinding: this.isPropertyBinding(node)
      }
    });
  }

  private extractQmlSignal(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const signalName = this.findSignalName(node);
    const parameters = this.findSignalParameters(node);

    const name = signalName || 'unknownSignal';
    const signature = this.buildSignalSignature(signalName, parameters);

    return this.createSymbol(node, name, SymbolKind.Function, {
      signature,
      parentId,
      metadata: {
        qmlSignal: true,
        parameters: parameters,
        isEvent: true
      }
    });
  }

  private extractJavaScriptFunction(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'anonymousFunction';

    const parameters = this.extractFunctionParameters(node);
    const signature = this.buildJavaScriptFunctionSignature(name, parameters);
    const docComment = this.findDocComment(node);

    return this.createSymbol(node, name, SymbolKind.Function, {
      signature,
      docComment,
      parentId,
      metadata: {
        jsFunction: true,
        parameters: parameters,
        isAsync: this.isFunctionAsync(node)
      }
    });
  }

  private extractQmlImport(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const importPath = this.findImportPath(node);
    const importAlias = this.findImportAlias(node);
    const importVersion = this.findImportVersion(node);

    const name = importAlias || importPath || 'unknownImport';
    const signature = this.buildImportSignature(importPath, importVersion, importAlias);

    return this.createSymbol(node, name, SymbolKind.Import, {
      signature,
      metadata: {
        qmlImport: true,
        importPath: importPath,
        version: importVersion,
        alias: importAlias
      }
    });
  }

  private extractJavaScriptVariable(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = this.findVariableName(node);
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownVariable';

    const varType = this.findVariableType(node);
    const initialValue = this.findVariableInitializer(node);
    const isConst = this.isConstantVariable(node);

    const signature = this.buildVariableSignature(name, varType, initialValue, isConst);

    return this.createSymbol(node, name, isConst ? SymbolKind.Constant : SymbolKind.Variable, {
      signature,
      parentId,
      metadata: {
        jsVariable: true,
        variableType: varType,
        initialValue: initialValue,
        isConstant: isConst
      }
    });
  }

  private extractQmlState(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const stateName = this.findStateName(node);
    const name = stateName || 'unknownState';

    const signature = `State { name: "${name}" }`;

    return this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      parentId,
      metadata: {
        qmlState: true,
        isStateOrTransition: true
      }
    });
  }

  private extractComponentId(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const name = this.getNodeText(node);

    return this.createSymbol(node, name, SymbolKind.Variable, {
      signature: `id: ${name}`,
      parentId,
      metadata: {
        qmlId: true,
        isComponentId: true
      }
    });
  }

  // Helper methods for finding specific parts of QML/JS nodes

  private findComponentType(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    // Look for UiQualifiedId or similar type identifier
    for (const child of node.children) {
      if (child.type === 'UiQualifiedId' || child.type === 'identifier') {
        return child;
      }
    }
    return null;
  }

  private findComponentId(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    // Look for id property binding
    for (const child of node.children) {
      if (child.type === 'UiObjectInitializer') {
        for (const binding of child.children) {
          if (binding.type === 'UiScriptBinding') {
            const qualifiedId = binding.childForFieldName('qualifiedId');
            if (qualifiedId && this.getNodeText(qualifiedId) === 'id') {
              return binding.childForFieldName('statement');
            }
          }
        }
      }
    }
    return null;
  }

  private findPropertyName(node: Parser.SyntaxNode): string | null {
    const qualifiedId = node.childForFieldName('qualifiedId');
    return qualifiedId ? this.getNodeText(qualifiedId) : null;
  }

  private findPropertyType(node: Parser.SyntaxNode): string | null {
    // Try to infer type from value or explicit type annotation
    const statement = node.childForFieldName('statement');
    if (statement) {
      return this.inferTypeFromValue(statement);
    }
    return null;
  }

  private findPropertyValue(node: Parser.SyntaxNode): string | null {
    const statement = node.childForFieldName('statement');
    return statement ? this.getNodeText(statement) : null;
  }

  private findSignalName(node: Parser.SyntaxNode): string | null {
    const nameNode = node.childForFieldName('name');
    return nameNode ? this.getNodeText(nameNode) : null;
  }

  private findSignalParameters(node: Parser.SyntaxNode): string[] {
    const parameters: string[] = [];
    const paramList = node.childForFieldName('parameters');
    if (paramList) {
      for (const param of paramList.children) {
        if (param.type === 'identifier' || param.type === 'formal_parameter') {
          parameters.push(this.getNodeText(param));
        }
      }
    }
    return parameters;
  }

  private extractFunctionParameters(node: Parser.SyntaxNode): string[] {
    const parameters: string[] = [];
    const paramList = node.childForFieldName('parameters');
    if (paramList) {
      for (const param of paramList.children) {
        if (param.type === 'identifier' || param.type === 'formal_parameter') {
          parameters.push(this.getNodeText(param));
        }
      }
    }
    return parameters;
  }

  private findImportPath(node: Parser.SyntaxNode): string | null {
    for (const child of node.children) {
      if (child.type === 'string_literal' || child.type === 'UiQualifiedId') {
        return this.getNodeText(child);
      }
    }
    return null;
  }

  private findImportAlias(node: Parser.SyntaxNode): string | null {
    // Look for 'as Alias' pattern
    const children = node.children;
    for (let i = 0; i < children.length - 1; i++) {
      if (this.getNodeText(children[i]) === 'as') {
        return this.getNodeText(children[i + 1]);
      }
    }
    return null;
  }

  private findImportVersion(node: Parser.SyntaxNode): string | null {
    // Look for version number in QML imports
    for (const child of node.children) {
      if (child.type === 'number') {
        return this.getNodeText(child);
      }
    }
    return null;
  }

  private findVariableName(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    const nameNode = node.childForFieldName('name');
    if (nameNode) return nameNode;

    // Fallback: look for identifier children
    for (const child of node.children) {
      if (child.type === 'identifier') {
        return child;
      }
    }
    return null;
  }

  private findVariableType(node: Parser.SyntaxNode): string | null {
    const typeNode = node.childForFieldName('type');
    return typeNode ? this.getNodeText(typeNode) : null;
  }

  private findVariableInitializer(node: Parser.SyntaxNode): string | null {
    const initNode = node.childForFieldName('value');
    return initNode ? this.getNodeText(initNode) : null;
  }

  // Utility methods for building signatures

  private buildComponentSignature(node: Parser.SyntaxNode, typeName: string): string {
    const idNode = this.findComponentId(node);
    const id = idNode ? this.getNodeText(idNode) : '';
    return id ? `${typeName} { id: ${id} }` : `${typeName} { }`;
  }

  private buildPropertySignature(name: string | null, type: string | null, value: string | null): string {
    const propName = name || 'property';
    const propType = type ? `: ${type}` : '';
    const propValue = value ? ` = ${value}` : '';
    return `property${propType} ${propName}${propValue}`;
  }

  private buildSignalSignature(name: string | null, parameters: string[]): string {
    const signalName = name || 'signal';
    const params = parameters.length > 0 ? `(${parameters.join(', ')})` : '()';
    return `signal ${signalName}${params}`;
  }

  private buildJavaScriptFunctionSignature(name: string, parameters: string[]): string {
    const params = parameters.join(', ');
    return `function ${name}(${params})`;
  }

  private buildImportSignature(path: string | null, version: string | null, alias: string | null): string {
    let sig = `import ${path || 'unknown'}`;
    if (version) sig += ` ${version}`;
    if (alias) sig += ` as ${alias}`;
    return sig;
  }

  private buildVariableSignature(name: string, type: string | null, initialValue: string | null, isConst: boolean): string {
    const keyword = isConst ? 'const' : 'var';
    const varType = type ? `: ${type}` : '';
    const value = initialValue ? ` = ${initialValue}` : '';
    return `${keyword} ${name}${varType}${value}`;
  }

  // Type inference and checking methods

  private inferTypeFromValue(node: Parser.SyntaxNode): string {
    const text = this.getNodeText(node);

    // Basic type inference from literal values
    if (text.startsWith('"') || text.startsWith("'")) return 'string';
    if (/^\d+$/.test(text)) return 'int';
    if (/^\d+\.\d+$/.test(text)) return 'real';
    if (text === 'true' || text === 'false') return 'bool';
    if (text.startsWith('[') && text.endsWith(']')) return 'list';
    if (text.startsWith('{') && text.endsWith('}')) return 'object';

    return 'var';
  }

  private isPropertyBinding(node: Parser.SyntaxNode): boolean {
    const statement = node.childForFieldName('statement');
    if (!statement) return false;

    const text = this.getNodeText(statement);
    // Simple heuristic: if it contains property access or function calls, it's likely a binding
    return text.includes('.') || text.includes('(') || /^[a-zA-Z_][a-zA-Z0-9_]*$/.test(text);
  }

  private isFunctionAsync(node: Parser.SyntaxNode): boolean {
    // Check if function is marked as async
    const parent = node.parent;
    if (parent) {
      const text = this.getNodeText(parent);
      return text.includes('async');
    }
    return false;
  }

  private isConstantVariable(node: Parser.SyntaxNode): boolean {
    const parent = node.parent;
    if (parent && parent.type === 'lexical_declaration') {
      const kindNode = parent.childForFieldName('kind');
      if (kindNode && this.getNodeText(kindNode) === 'const') {
        return true;
      }
    }
    return false;
  }

  private isStatesOrTransitions(node: Parser.SyntaxNode): boolean {
    const qualifiedId = node.childForFieldName('qualifiedId');
    if (qualifiedId) {
      const text = this.getNodeText(qualifiedId);
      return text === 'states' || text === 'transitions';
    }
    return false;
  }

  private isComponentId(node: Parser.SyntaxNode): boolean {
    const parent = node.parent;
    if (parent && parent.type === 'UiScriptBinding') {
      const qualifiedId = parent.childForFieldName('qualifiedId');
      if (qualifiedId && this.getNodeText(qualifiedId) === 'id') {
        return true;
      }
    }
    return false;
  }

  private findStateName(node: Parser.SyntaxNode): string | null {
    // Look for name property in State objects
    for (const child of node.children) {
      if (child.type === 'UiObjectInitializer') {
        for (const binding of child.children) {
          if (binding.type === 'UiScriptBinding') {
            const qualifiedId = binding.childForFieldName('qualifiedId');
            if (qualifiedId && this.getNodeText(qualifiedId) === 'name') {
              const statement = binding.childForFieldName('statement');
              return statement ? this.getNodeText(statement).replace(/['"]/g, '') : null;
            }
          }
        }
      }
    }
    return null;
  }

  // Abstract method implementations

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];

    // Build a map of symbols for quick lookup
    const symbolMap = new Map<string, Symbol>();
    symbols.forEach(symbol => symbolMap.set(symbol.name, symbol));

    // Extract relationships between QML components and their children
    const visitNode = (node: Parser.SyntaxNode) => {
      if (node.type === 'UiObjectDefinition' || node.type === 'QmlObject') {
        const parentSymbol = this.findSymbolForNode(node, symbols);

        if (parentSymbol) {
          // Find child components
          for (const child of node.children) {
            if (child.type === 'UiObjectInitializer') {
              for (const nestedObject of child.children) {
                if (nestedObject.type === 'UiObjectDefinition') {
                  const childSymbol = this.findSymbolForNode(nestedObject, symbols);
                  if (childSymbol) {
                    relationships.push({
                      fromSymbolId: parentSymbol.id,
                      toSymbolId: childSymbol.id,
                      kind: RelationshipKind.Contains,
                      filePath: this.filePath,
                      lineNumber: nestedObject.startPosition.row + 1,
                      confidence: 0.9
                    });
                  }
                }
              }
            }
          }
        }
      }

      // Recursively visit children
      for (const child of node.children) {
        visitNode(child);
      }
    };

    visitNode(tree.rootNode);
    return relationships;
  }

  private findSymbolForNode(node: Parser.SyntaxNode, symbols: Symbol[]): Symbol | null {
    const nodeStartLine = node.startPosition.row + 1;
    const nodeStartColumn = node.startPosition.column;

    return symbols.find(symbol =>
      symbol.startLine === nodeStartLine &&
      symbol.startColumn === nodeStartColumn
    ) || null;
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const typeMap = new Map<string, string>();

    symbols.forEach(symbol => {
      if (symbol.metadata?.qmlProperty) {
        // Use the inferred property type
        typeMap.set(symbol.name, symbol.metadata.propertyType || 'var');
      } else if (symbol.metadata?.jsFunction) {
        // Functions return type (could be enhanced with return type analysis)
        typeMap.set(symbol.name, 'function');
      } else if (symbol.metadata?.qmlSignal) {
        // Signals are event functions
        typeMap.set(symbol.name, 'signal');
      } else if (symbol.metadata?.isQmlComponent) {
        // QML components are their QML type
        typeMap.set(symbol.name, symbol.metadata.qmlType || 'QmlObject');
      } else if (symbol.metadata?.jsVariable) {
        // JavaScript variables
        typeMap.set(symbol.name, symbol.metadata.variableType || 'var');
      } else if (symbol.kind === SymbolKind.Import) {
        // Imports
        typeMap.set(symbol.name, 'module');
      }
    });

    return typeMap;
  }
}