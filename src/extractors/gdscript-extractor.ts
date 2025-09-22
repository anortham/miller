import { Parser } from 'web-tree-sitter';
import { BaseExtractor, Symbol, SymbolKind, Relationship, RelationshipKind } from './base-extractor.js';
import { log, LogLevel } from '../utils/logger.js';

export class GDScriptExtractor extends BaseExtractor {
  private symbols: Symbol[] = [];
  private relationships: Relationship[] = [];
  private pendingInheritance: Map<string, string> = new Map(); // className -> baseClassName
  private processedNodes: Set<Parser.SyntaxNode> = new Set(); // Track processed nodes

  constructor(language: string, filePath: string, content: string) {
    super(language, filePath, content);
  }

  extractSymbols(tree: Parser.Tree): Symbol[] {
    this.symbols = [];
    this.relationships = [];
    this.pendingInheritance.clear();
    this.processedNodes.clear();

    if (tree && tree.rootNode) {
      // First pass: collect inheritance information
      this.collectInheritanceInfo(tree.rootNode);

      // Check for top-level extends statement (creates implicit class)
      let implicitClassId: string | null = null;
      const children = tree.rootNode.children;

      for (let i = 0; i < children.length; i++) {
        if (children[i].type === 'extends' && i + 1 < children.length) {
          const typeNode = children[i + 1];
          if (typeNode.type === 'type') {
            const identifierNode = this.findChildByType(typeNode, 'identifier');
            if (identifierNode) {
              const baseClassName = this.getNodeText(identifierNode);

              // Create implicit class based on file name
              const fileName = this.filePath.split('/').pop()?.replace('.gd', '') || 'ImplicitClass';
              const implicitClass = this.createSymbol(children[i], fileName, SymbolKind.Class, {
                signature: `extends ${baseClassName}`,
                parentId: null,
                visibility: 'public',
                metadata: { baseClass: baseClassName }
              });

              this.symbols.push(implicitClass);
              implicitClassId = implicitClass.id;
              break;
            }
          }
        }
      }

      // Second pass: extract symbols with implicit class context
      this.traverseNode(tree.rootNode, implicitClassId);
    }

    return this.symbols;
  }

  private collectInheritanceInfo(node: Parser.SyntaxNode): void {
    // Look for adjacent class_name_statement and extends_statement pairs
    const children = node.children;

    for (let i = 0; i < children.length - 1; i++) {
      const currentChild = children[i];
      const nextChild = children[i + 1];

      // Check for class_name followed by extends
      if (currentChild.type === 'class_name_statement' && nextChild.type === 'extends_statement') {
        const nameNode = this.findChildByType(currentChild, 'name');
        const typeNode = this.findChildByType(nextChild, 'type');

        if (nameNode && typeNode) {
          const className = this.getNodeText(nameNode);
          const identifierNode = this.findChildByType(typeNode, 'identifier');

          if (identifierNode) {
            const baseClassName = this.getNodeText(identifierNode);
            this.pendingInheritance.set(className, baseClassName);
            // Debug: Collected inheritance tracking
          }
        }
      }
    }

    // Recursively collect from children
    for (const child of node.children) {
      this.collectInheritanceInfo(child);
    }
  }

  protected traverseNode(node: Parser.SyntaxNode, parentId: string | null): void {
    // Prevent double processing of the same node
    if (this.processedNodes.has(node)) {
      return;
    }
    this.processedNodes.add(node);


    let symbol: Symbol | null = null;

    switch (node.type) {
      case 'class_name_statement':
        symbol = this.extractClassNameStatement(node, parentId);
        break;
      case 'class':
        symbol = this.extractClassDefinition(node, parentId);
        break;
      case 'extends_statement':
        // Inheritance is handled in the two-pass approach via collectInheritanceInfo
        break;
      case 'function_definition':
        symbol = this.extractFunctionDefinition(node, parentId);
        break;
      case 'func':
        symbol = this.extractFunctionDefinition(node, parentId);
        break;
      case 'constructor_definition':
        symbol = this.extractConstructorDefinition(node, parentId);
        break;
      case 'var':
        symbol = this.extractVariableStatement(node, parentId);
        break;
      case 'const':
        symbol = this.extractConstantStatement(node, parentId);
        break;
      case 'enum_definition':
        symbol = this.extractEnumDefinition(node, parentId);
        break;
      case 'signal_statement':
        symbol = this.extractSignalStatement(node, parentId);
        break;
      case 'signal':
        symbol = this.extractSignalStatement(node, parentId);
        break;
      default:
        // Continue traversing
        break;
    }

    // Traverse children with current symbol as parent
    const currentParentId = symbol?.id || parentId;


    for (const child of node.children) {
      this.traverseNode(child, currentParentId);
    }
  }

  private extractClassNameStatement(node: Parser.SyntaxNode, parentId: string | null): Symbol | null {
    const nameNode = this.findChildByType(node, 'name');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    // Include preceding annotations in signature
    const parent = node.parent;
    let signature = this.getNodeText(node);
    if (parent) {
      const children = parent.children;

      // Find the position by comparing the node's text content
      let nodeIndex = -1;
      for (let i = 0; i < children.length; i++) {
        if (children[i].type === 'class_name_statement' &&
            this.getNodeText(children[i]) === this.getNodeText(node)) {
          nodeIndex = i;
          break;
        }
      }

      // Look for annotations before this class_name_statement
      if (nodeIndex > 0) {
        for (let i = nodeIndex - 1; i >= 0; i--) {
          if (children[i].type === 'annotation') {
            const annotationText = this.getNodeText(children[i]);
            signature = `${annotationText}\n${signature}`;
            break; // Only take the closest preceding annotation
          }
          // Stop if we hit another class or significant structure
          if (children[i].type === 'class_name_statement') {
            break;
          }
        }
      }
    }

    const symbol = this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      parentId,
      visibility: 'public'
    });

    // Apply inheritance info if available
    const baseClassName = this.pendingInheritance.get(name);
    if (baseClassName) {
      (symbol as any).baseClass = baseClassName;
      // Debug: Applied inheritance relationship
    }

    // Debug: Created class symbol
    this.symbols.push(symbol);
    return symbol;
  }

  private extractClassDefinition(node: Parser.SyntaxNode, parentId: string | null): Symbol | null {
    // For `class` nodes, look for the name node in the parent's children (sibling)
    const parent = node.parent;
    if (!parent) return null;

    let nameNode: Parser.SyntaxNode | null = null;
    const children = parent.children;
    const classIndex = children.indexOf(node);


    // Look for 'name' node after the 'class' node
    for (let i = classIndex + 1; i < children.length; i++) {
      if (children[i].type === 'name') {
        nameNode = children[i];
        break;
      }
    }

    if (!nameNode) {
      return null;
    }

    const name = this.getNodeText(nameNode);
    const signature = `class ${name}:`;

    const symbol = this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      parentId,
      visibility: 'public'
    });

    this.symbols.push(symbol);

    // Also traverse the body node that contains the class contents
    const bodyIndex = children.findIndex(c => c.type === 'body');
    if (bodyIndex !== -1) {
      const bodyNode = children[bodyIndex];
      // Manually traverse the body with this class as parent
      for (const bodyChild of bodyNode.children) {
        this.traverseNode(bodyChild, symbol.id);
      }
    }

    return symbol;
  }

  private extractFunctionDefinition(node: Parser.SyntaxNode, parentId: string | null): Symbol | null {
    // For `func` nodes, look for the name node in the parent's children (sibling)
    const parent = node.parent;
    if (!parent) return null;

    let nameNode: Parser.SyntaxNode | null = null;
    const children = parent.children;
    const funcIndex = children.indexOf(node);

    // Look for 'name' node after the 'func' node
    for (let i = funcIndex + 1; i < children.length; i++) {
      if (children[i].type === 'name') {
        nameNode = children[i];
        break;
      }
    }

    if (!nameNode) {
      return null;
    }

    const name = this.getNodeText(nameNode);
    const signature = this.getNodeText(parent); // Use parent to get full declaration


    // Determine visibility based on naming convention
    const visibility = name.startsWith('_') ? 'private' : 'public';

    // Determine symbol kind based on context and name (Python-like semantics)
    let kind: SymbolKind;
    if (name === '_init') {
      // GDScript constructor (like Python's __init__)
      kind = SymbolKind.Constructor;
    } else if (parentId) {
      // Method inside a class (including implicit class)
      kind = SymbolKind.Method;
    } else {
      // Top-level function
      kind = SymbolKind.Function;
    }

    const symbol = this.createSymbol(node, name, kind, {
      signature,
      parentId,
      visibility
    });


    this.symbols.push(symbol);
    return symbol;
  }

  private extractConstructorDefinition(node: Parser.SyntaxNode, parentId: string | null): Symbol | null {
    const signature = this.getNodeText(node);

    const symbol = this.createSymbol(node, '_init', SymbolKind.Constructor, {
      signature,
      parentId,
      visibility: 'public'
    });

    this.symbols.push(symbol);
    return symbol;
  }

  private extractVariableStatement(node: Parser.SyntaxNode, parentId: string | null): Symbol | null {
    // For `var` nodes, look for the name node in the parent's children (sibling)
    const parent = node.parent;
    if (!parent) return null;

    let nameNode: Parser.SyntaxNode | null = null;
    const children = parent.children;
    const varIndex = children.indexOf(node);

    // Look for 'name' node after the 'var' node
    for (let i = varIndex + 1; i < children.length; i++) {
      if (children[i].type === 'name') {
        nameNode = children[i];
        break;
      }
    }

    if (!nameNode) {
      return null;
    }

    const name = this.getNodeText(nameNode);
    const signature = this.getNodeText(parent); // Use parent to get full declaration


    // Check for annotations in sibling structure (like we did for class annotations)
    let annotations: string[] = [];

    // Look for annotations before this var in the parent's children
    // Find the var node index by comparing content (indexOf doesn't work with node references)
    let nodeIndex = -1;
    for (let i = 0; i < children.length; i++) {
      if (children[i].type === 'var' && children[i] === node) {
        nodeIndex = i;
        break;
      }
    }

    // If we can't find by reference, find by position (var nodes are unique enough)
    if (nodeIndex === -1) {
      for (let i = 0; i < children.length; i++) {
        if (children[i].type === 'var') {
          nodeIndex = i;
          break; // Take the first var node - this works for most cases
        }
      }
    }


    if (nodeIndex > 0) {
      for (let i = nodeIndex - 1; i >= 0; i--) {
        if (children[i].type === 'annotations') {
          // Get all annotation children
          const annotationsNode = children[i];


          for (const annotationChild of annotationsNode.children) {
            if (annotationChild.type === 'annotation') {
              annotations.push(this.getNodeText(annotationChild));
            }
          }
          break;
        }
        // Stop if we hit another variable or significant structure
        if (children[i].type === 'var') {
          break;
        }
      }
    }

    const isExported = annotations.some(a => a.startsWith('@export'));
    const isOnReady = annotations.some(a => a.startsWith('@onready'));



    // Determine data type from sibling structure: var name : type = value
    let dataType = 'unknown';

    // Look for type annotation as sibling after the name
    const nameIndex = children.indexOf(nameNode);
    for (let i = nameIndex + 1; i < children.length; i++) {
      if (children[i].type === 'type') {
        const typeNode = children[i];

        const identifierNode = this.findChildByType(typeNode, 'identifier');
        if (identifierNode) {
          dataType = this.getNodeText(identifierNode);
        } else {
          // Handle complex types (e.g., Array[String])
          dataType = this.getNodeText(typeNode).trim();
        }
        break;
      }
    }

    // If no explicit type, try to infer from assignment
    if (dataType === 'unknown') {
      for (let i = nameIndex + 1; i < children.length; i++) {
        if (children[i].type === '=' && i + 1 < children.length) {
          const valueNode = children[i + 1];
          dataType = this.inferTypeFromExpression(valueNode);
          break;
        }
      }
    }

    // Determine visibility
    const visibility = isExported ? 'public' : 'private';


    const symbol = this.createSymbol(node, name, SymbolKind.Field, {
      signature,
      parentId,
      visibility,
      metadata: {
        dataType,
        annotations,
        isExported,
        isOnReady
      }
    });

    // Manually add dataType as direct property (test expects this)
    (symbol as any).dataType = dataType;


    this.symbols.push(symbol);
    return symbol;
  }

  private extractConstantStatement(node: Parser.SyntaxNode, parentId: string | null): Symbol | null {
    // For `const` nodes, similar to var but create as Constant
    const parent = node.parent;
    if (!parent) return null;

    let nameNode: Parser.SyntaxNode | null = null;
    const children = parent.children;

    // Find the const node index (same fix as for variables)
    let nodeIndex = -1;
    for (let i = 0; i < children.length; i++) {
      if (children[i].type === 'const') {
        nodeIndex = i;
        break;
      }
    }

    // Look for 'name' node after the 'const' node
    for (let i = nodeIndex + 1; i < children.length; i++) {
      if (children[i].type === 'name') {
        nameNode = children[i];
        break;
      }
    }

    if (!nameNode) {
      return null;
    }

    const name = this.getNodeText(nameNode);
    const signature = this.getNodeText(parent);

    // Get type annotation (same logic as variables)
    let dataType = 'unknown';
    const nameIndex = children.indexOf(nameNode);
    for (let i = nameIndex + 1; i < children.length; i++) {
      if (children[i].type === 'type') {
        const typeNode = children[i];
        const identifierNode = this.findChildByType(typeNode, 'identifier');
        if (identifierNode) {
          dataType = this.getNodeText(identifierNode);
        } else {
          dataType = this.getNodeText(typeNode).trim();
        }
        break;
      }
    }

    const symbol = this.createSymbol(node, name, SymbolKind.Constant, {
      signature,
      parentId,
      visibility: 'public' // Constants are typically public
    });

    // Add dataType as direct property
    (symbol as any).dataType = dataType;

    this.symbols.push(symbol);
    return symbol;
  }

  private extractSignalStatement(node: Parser.SyntaxNode, parentId: string | null): Symbol | null {
    const nameNode = this.findChildByType(node, 'name');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);
    const signature = this.getNodeText(node);

    const symbol = this.createSymbol(node, name, SymbolKind.Event, {
      signature,
      parentId,
      visibility: 'public'
    });

    this.symbols.push(symbol);
    return symbol;
  }

  private extractAnnotations(node: Parser.SyntaxNode): string[] {
    const annotations: string[] = [];
    const annotationsNode = this.findChildByType(node, 'annotations');

    if (annotationsNode) {
      const annotationNodes = this.findChildrenByType(annotationsNode, 'annotation');
      for (const annotationNode of annotationNodes) {
        annotations.push(this.getNodeText(annotationNode));
      }
    }

    return annotations;
  }

  private inferTypeFromExpression(node: Parser.SyntaxNode): string {
    switch (node.type) {
      case 'string':
        return 'String';
      case 'integer':
      case 'float':
        return 'Number';
      case 'true':
      case 'false':
        return 'bool';
      case 'null':
        return 'null';
      case 'identifier':
        // For node references like $Sprite2D, try to infer the type
        const text = this.getNodeText(node);
        if (text.startsWith('$') || text.includes('Node')) {
          return text.replace('$', '');
        }
        return 'unknown';
      case 'call_expression':
        // Handle constructor calls or method calls
        const calleeNode = this.findChildByType(node, 'identifier');
        if (calleeNode) {
          const calleeText = this.getNodeText(calleeNode);
          // Common Godot constructors
          if (['Vector2', 'Vector3', 'Color', 'Rect2', 'Transform2D'].includes(calleeText)) {
            return calleeText;
          }
        }
        return 'unknown';
      default:
        return 'unknown';
    }
  }

  private extractEnumDefinition(node: Parser.SyntaxNode, parentId: string | null): Symbol | null {
    // For enum_definition nodes, find the identifier child directly
    let nameNode = this.findChildByType(node, 'identifier');

    let name: string;
    if (!nameNode) {
      // Try to extract name from the text pattern: "enum Name { ... }"
      const text = this.getNodeText(node);
      const match = text.match(/enum\s+(\w+)\s*\{/);
      if (match) {
        name = match[1];
      } else {
        return null;
      }
    } else {
      name = this.getNodeText(nameNode);
    }

    const signature = this.getNodeText(node);

    const symbol = this.createSymbol(node, name, SymbolKind.Enum, {
      signature,
      parentId,
      visibility: 'public' // Enums are typically public
    });

    this.symbols.push(symbol);
    return symbol;
  }

  getRelationships(): Relationship[] {
    return this.relationships;
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    // Return the relationships already collected during symbol extraction
    return this.relationships;
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    // Basic type inference for GDScript symbols
    const typeMap = new Map<string, string>();

    for (const symbol of symbols) {
      switch (symbol.kind) {
        case 'class':
          typeMap.set(symbol.id, 'class');
          break;
        case 'function':
        case 'method':
          typeMap.set(symbol.id, 'function');
          break;
        case 'variable':
          // Try to infer from signature or default to 'var'
          if (symbol.signature?.includes('int')) {
            typeMap.set(symbol.id, 'int');
          } else if (symbol.signature?.includes('float')) {
            typeMap.set(symbol.id, 'float');
          } else if (symbol.signature?.includes('String')) {
            typeMap.set(symbol.id, 'String');
          } else {
            typeMap.set(symbol.id, 'var');
          }
          break;
        case 'constant':
          typeMap.set(symbol.id, 'const');
          break;
        case 'enum':
          typeMap.set(symbol.id, 'enum');
          break;
        default:
          typeMap.set(symbol.id, 'unknown');
      }
    }

    return typeMap;
  }
}