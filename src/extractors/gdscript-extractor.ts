import { Parser } from 'web-tree-sitter';
import { BaseExtractor, Symbol, SymbolKind, Relationship, RelationshipKind } from './base-extractor.js';
import { log, LogLevel } from '../utils/logger.js';

export class GDScriptExtractor extends BaseExtractor {
  private symbols: Symbol[] = [];
  private relationships: Relationship[] = [];
  private pendingInheritance: Map<string, string> = new Map(); // className -> baseClassName
  private processedPositions: Set<string> = new Set(); // Track processed node positions

  constructor(language: string, filePath: string, content: string) {
    super(language, filePath, content);
  }

  extractSymbols(tree: Parser.Tree): Symbol[] {
    this.symbols = [];
    this.relationships = [];
    this.pendingInheritance.clear();
    this.processedPositions.clear();

    if (tree && tree.rootNode) {
      // First pass: collect inheritance information
      this.collectInheritanceInfo(tree.rootNode);

      // Check for top-level extends statement (creates implicit class)
      let implicitClassId: string | null = null;
      const children = tree.rootNode.children;

      for (let i = 0; i < children.length; i++) {
        if (children[i].type === 'extends_statement') {
          const extendsNode = children[i];
          const typeNode = this.findChildByType(extendsNode, 'type');
          if (typeNode) {
            const baseClassName = this.getNodeText(typeNode);

            // Create implicit class based on file name
            const fileName = this.filePath.split('/').pop()?.replace('.gd', '') || 'ImplicitClass';
            const implicitClass = this.createSymbol(extendsNode, fileName, SymbolKind.Class, {
              signature: `extends ${baseClassName}`,
              parentId: null,
              visibility: 'public',
              metadata: { baseClass: baseClassName }
            });

            // Set baseClass property for tests
            (implicitClass as any).baseClass = baseClassName;

            this.symbols.push(implicitClass);
            implicitClassId = implicitClass.id;
            break;
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
          }
        }
      }

      // Check for extends followed by class_name (reverse order)
      if (currentChild.type === 'extends_statement' && nextChild.type === 'class_name_statement') {
        const typeNode = this.findChildByType(currentChild, 'type');
        const nameNode = this.findChildByType(nextChild, 'name');

        if (nameNode && typeNode) {
          const className = this.getNodeText(nameNode);
          const identifierNode = this.findChildByType(typeNode, 'identifier');

          if (identifierNode) {
            const baseClassName = this.getNodeText(identifierNode);
            this.pendingInheritance.set(className, baseClassName);
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
    // Create position-based key to prevent double processing of the same node
    const positionKey = `${node.startPosition.row}:${node.startPosition.column}:${node.type}`;

    if (this.processedPositions.has(positionKey)) {
      return;
    }
    this.processedPositions.add(positionKey);


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
        // Skip if this func node is part of a function_definition (to avoid duplicates)
        if (node.parent?.type === 'function_definition') {
          return; // Skip processing
        }
        symbol = this.extractFunctionDefinition(node, parentId);
        break;
      case 'constructor_definition':
        symbol = this.extractConstructorDefinition(node, parentId);
        break;
      case 'var':
        // Skip if this var node is part of a variable_statement (to avoid duplicates)
        if (node.parent?.type === 'variable_statement') {
          return; // Skip processing
        }
        symbol = this.extractVariableStatement(node, parentId);
        break;
      case 'variable_statement':
        symbol = this.extractVariableFromStatement(node, parentId);
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
    let nameNode: Parser.SyntaxNode | null = null;
    let funcNode: Parser.SyntaxNode | null = null;
    let parentNode: Parser.SyntaxNode | null = null;

    if (node.type === 'function_definition') {
      // Processing function_definition node - find child nodes
      const children = node.children;
      funcNode = children.find(c => c.type === 'func') || null;
      nameNode = children.find(c => c.type === 'name') || null;
      parentNode = node;
    } else if (node.type === 'func') {
      // Processing func node - look for sibling name node
      const parent = node.parent;
      if (!parent) return null;

      const children = parent.children;
      const funcIndex = children.indexOf(node);

      // Look for 'name' node after the 'func' node
      for (let i = funcIndex + 1; i < children.length; i++) {
        if (children[i].type === 'name') {
          nameNode = children[i];
          break;
        }
      }
      funcNode = node;
      parentNode = parent;
    }

    if (!nameNode || !funcNode || !parentNode) {
      return null;
    }

    const name = this.getNodeText(nameNode);
    const signature = this.getNodeText(parentNode); // Use parent to get full declaration


    // Determine visibility based on naming convention
    const visibility = name.startsWith('_') ? 'private' : 'public';

    // Determine symbol kind based on context and name
    let kind: SymbolKind;
    if (name === '_init') {
      // GDScript constructor
      kind = SymbolKind.Constructor;
    } else if (parentId) {
      // Find the actual parent, preferring explicit classes over implicit ones
      let parentSymbol = this.symbols.find(s => s.id === parentId);

      // If parent is an implicit class, check if there's an explicit class to use instead
      if (parentSymbol && parentSymbol.kind === SymbolKind.Class) {
        const isImplicitClass = parentSymbol.signature?.includes('extends') &&
                               !parentSymbol.signature?.includes('class_name');

        if (isImplicitClass) {
          // Look for an explicit class (with class_name) to use as parent instead
          const explicitClass = this.symbols.find(s =>
            s.kind === SymbolKind.Class &&
            s.signature?.includes('class_name')
          );

          if (explicitClass) {
            // Use explicit class as parent and update the symbol
            parentSymbol = explicitClass;
            parentId = explicitClass.id;
          }
        }

        // Now determine kind based on the final parent class
        const finalIsImplicitClass = parentSymbol.signature?.includes('extends') &&
                                    !parentSymbol.signature?.includes('class_name');
        const finalIsExplicitClass = parentSymbol.signature?.includes('class_name');

        if (finalIsImplicitClass) {
          // In implicit classes, lifecycle callbacks are methods, custom functions remain functions
          const isLifecycleCallback = name.startsWith('_') && [
            '_ready', '_enter_tree', '_exit_tree', '_process', '_physics_process',
            '_input', '_unhandled_input', '_unhandled_key_input', '_notification',
            '_draw', '_on_', '_handle_'
          ].some(prefix => name.startsWith(prefix));

          kind = isLifecycleCallback ? SymbolKind.Method : SymbolKind.Function;
        } else if (finalIsExplicitClass) {
          // In explicit classes (class_name), all functions are methods
          kind = SymbolKind.Method;
        } else {
          // Fallback: default to method for class context
          kind = SymbolKind.Method;
        }
      } else {
        // Inside a function or other non-class context - this is still a function
        kind = SymbolKind.Function;
      }
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


    // Check for annotations - they can be children or siblings
    let annotations: string[] = [];
    let fullSignature = this.getNodeText(parent);

    // First check for annotations as children of variable_statement (most common case)
    if (parent.type === 'variable_statement') {
      for (const child of children) {
        if (child.type === 'annotations') {
          // Get all annotation children
          for (const annotationChild of child.children) {
            if (annotationChild.type === 'annotation') {
              const annotationText = this.getNodeText(annotationChild);
              annotations.push(annotationText);
            }
          }
        }
      }
    }

    // Also look for sibling annotations at source level (like @export_category)
    if (parent.type === 'variable_statement') {
      const grandParent = parent.parent;
      if (grandParent) {
        const searchChildren = grandParent.children;

        // Find the index of the variable_statement by position (more reliable than reference)
        let nodeIndex = -1;
        for (let i = 0; i < searchChildren.length; i++) {
          if (searchChildren[i].type === 'variable_statement' &&
              searchChildren[i].startPosition.row === parent.startPosition.row &&
              searchChildren[i].startPosition.column === parent.startPosition.column) {
            nodeIndex = i;
            break;
          }
        }

        // Collect all preceding sibling annotations
        if (nodeIndex > 0) {
          let annotationTexts: string[] = [];

          for (let i = nodeIndex - 1; i >= 0; i--) {
            if (searchChildren[i].type === 'annotations') {
              // Get all annotation children
              const annotationsNode = searchChildren[i];
              for (const annotationChild of annotationsNode.children) {
                if (annotationChild.type === 'annotation') {
                  const annotationText = this.getNodeText(annotationChild);
                  annotations.push(annotationText);
                  annotationTexts.unshift(annotationText); // Add to beginning
                }
              }
            }
            // Also check for individual annotation nodes
            else if (searchChildren[i].type === 'annotation') {
              const annotationText = this.getNodeText(searchChildren[i]);
              annotations.push(annotationText);
              annotationTexts.unshift(annotationText);
            }
            // Stop if we hit another variable or significant structure
            else if (searchChildren[i].type === 'variable_statement' || searchChildren[i].type === 'var') {
              break;
            }
          }

          // Build full signature with annotations
          if (annotationTexts.length > 0) {
            fullSignature = annotationTexts.join('\n') + '\n' + this.getNodeText(parent);
          }
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
      signature: fullSignature,
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

    // Extract enum members
    this.extractEnumMembers(node, symbol.id);

    return symbol;
  }

  private extractEnumMembers(enumNode: Parser.SyntaxNode, enumId: string): void {
    // Look for enumerator_list and extract members
    const enumeratorList = this.findChildByType(enumNode, 'enumerator_list');
    if (!enumeratorList) {
      return;
    }

    // Extract enumerator nodes from enumerator list
    for (const child of enumeratorList.children) {
      if (child.type === 'enumerator') {
        // For simple enumerators like "IDLE", the text is the name
        const memberName = this.getNodeText(child);
        const memberSymbol = this.createSymbol(child, memberName, SymbolKind.EnumMember, {
          signature: memberName,
          parentId: enumId,
          visibility: 'public'
        });
        this.symbols.push(memberSymbol);
      }
    }
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

  private extractVariableFromStatement(node: Parser.SyntaxNode, parentId: string | null): Symbol | null {
    // For variable_statement nodes, find the var child and extract from there
    const varNode = this.findChildByType(node, 'var');
    if (!varNode) return null;

    // Check if we should use class_name class as parent instead of implicit class
    let actualParentId = parentId;

    // If at source level, find the closest preceding class_name statement
    if (node.parent?.type === 'source') {
      // Find all class_name classes in the same file, sorted by position
      const classNameClasses = this.symbols
        .filter(s =>
          s.kind === SymbolKind.Class &&
          s.signature?.includes('class_name') &&
          s.parentId === parentId // Same implicit parent means it's in the same file
        )
        .sort((a, b) => {
          // Sort by line number (we need position info for this)
          // For now, find the one that comes right before this variable
          return 0; // Placehnewer sort
        });

      // Find the closest preceding class_name by checking source children order
      if (node.parent && classNameClasses.length > 0) {
        const sourceChildren = node.parent.children;

        // Find the variable's position by comparing start positions
        let varIndex = -1;
        for (let i = 0; i < sourceChildren.length; i++) {
          if (sourceChildren[i].type === 'variable_statement' &&
              sourceChildren[i].startPosition.row === node.startPosition.row &&
              sourceChildren[i].startPosition.column === node.startPosition.column) {
            varIndex = i;
            break;
          }
        }

        // Find the last class_name_statement before this variable
        let closestClassNameNode: any = null;
        if (varIndex >= 0) {
          for (let i = varIndex - 1; i >= 0; i--) {
            if (sourceChildren[i].type === 'class_name_statement') {
              closestClassNameNode = sourceChildren[i];
              break;
            }
          }
        }

        if (closestClassNameNode) {
          // Find the corresponding class symbol by matching the name
          const nameNode = this.findChildByType(closestClassNameNode, 'name');
          if (nameNode) {
            const className = this.getNodeText(nameNode);
            const matchingClass = classNameClasses.find(c => c.name === className);
            if (matchingClass) {
              actualParentId = matchingClass.id;
            }
          }
        }
      }
    }

    return this.extractVariableStatement(varNode, actualParentId);
  }
}