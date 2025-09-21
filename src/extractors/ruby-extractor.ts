import { Parser } from 'web-tree-sitter';
import {
  BaseExtractor,
  Symbol,
  SymbolKind,
  Relationship,
  RelationshipKind
} from './base-extractor.js';

export class RubyExtractor extends BaseExtractor {
  private currentVisibility: 'public' | 'private' | 'protected' = 'public';

  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    this.currentVisibility = 'public'; // Reset for each file

    const visitNode = (node: Parser.SyntaxNode, parentId?: string, currentVis?: string) => {
      if (!node || !node.type) {
        return; // Skip invalid nodes
      }

      let symbol: Symbol | null = null;
      let newVisibility = currentVis || this.currentVisibility;

      switch (node.type) {
        case 'call':
          symbol = this.extractCall(node, parentId);
          break;
        case 'module':
          symbol = this.extractModule(node, parentId);
          break;
        case 'class':
          symbol = this.extractClass(node, parentId);
          break;
        case 'method':
          symbol = this.extractMethod(node, parentId, newVisibility);
          break;
        case 'singleton_method':
          symbol = this.extractSingletonMethod(node, parentId, newVisibility);
          break;
        case 'assignment':
          symbol = this.extractAssignment(node, parentId);
          break;
        case 'constant':
          symbol = this.extractConstant(node, parentId);
          break;
        case 'alias':
          symbol = this.extractAlias(node, parentId);
          break;
        case 'identifier':
          // Check for visibility modifiers
          const text = this.getNodeText(node);
          if (['private', 'protected', 'public'].includes(text)) {
            newVisibility = text as 'public' | 'private' | 'protected';
            this.currentVisibility = newVisibility;
          }
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
            visitNode(child, parentId, newVisibility);
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
      console.warn('Ruby parsing failed, attempting basic extraction:', error);
      return this.extractBasicStructure(tree);
    }

    // If we only extracted error symbols, try basic structure fallback
    const hasOnlyErrors = symbols.length > 0 && symbols.every(s =>
      s.metadata?.isError || s.metadata?.type === 'parse-error'
    );

    if (hasOnlyErrors || symbols.length === 0) {
      console.warn('Ruby extraction produced only errors or no symbols, using basic structure fallback');
      return this.extractBasicStructure(tree);
    }

    return symbols;
  }

  private extractCall(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const methodNode = node.children.find(c => c.type === 'identifier');
    if (!methodNode) return null;

    const methodName = this.getNodeText(methodNode);

    // Handle require/require_relative
    if (methodName === 'require' || methodName === 'require_relative') {
      const argNode = node.children.find(c => c.type === 'argument_list');
      if (argNode) {
        const stringNode = argNode.children.find(c => c.type === 'string');
        if (stringNode) {
          const requirePath = this.getNodeText(stringNode).replace(/['"]/g, '');
          const moduleName = requirePath.split('/').pop() || requirePath;

          return this.createSymbol(node, moduleName, SymbolKind.Import, {
            signature: `${methodName} ${this.getNodeText(stringNode)}`,
            visibility: 'public',
            parentId,
            metadata: {
              type: 'require',
              method: methodName,
              path: requirePath
            }
          });
        }
      }
    }

    // Handle attr_reader, attr_writer, attr_accessor
    if (['attr_reader', 'attr_writer', 'attr_accessor'].includes(methodName)) {
      const argNode = node.children.find(c => c.type === 'argument_list');
      if (argNode) {
        const symbols: Symbol[] = [];
        const symbolNodes = argNode.children.filter(c => c.type === 'simple_symbol' || c.type === 'symbol');

        for (const symbolNode of symbolNodes) {
          const attrName = this.getNodeText(symbolNode).replace(':', '');
          symbols.push(this.createSymbol(node, attrName, SymbolKind.Property, {
            signature: `${methodName} :${attrName}`,
            visibility: 'public',
            parentId,
            metadata: {
              type: 'attribute',
              accessor: methodName,
              attributeName: attrName
            }
          }));
        }

        return symbols[0] || null; // Return first symbol for now
      }
    }

    // Handle include/extend
    if (['include', 'extend'].includes(methodName)) {
      const argNode = node.children.find(c => c.type === 'argument_list');
      if (argNode) {
        const moduleNode = argNode.children.find(c => c.type === 'constant' || c.type === 'identifier');
        if (moduleNode) {
          const moduleName = this.getNodeText(moduleNode);
          return this.createSymbol(node, `${methodName}_${moduleName}`, SymbolKind.Import, {
            signature: `${methodName} ${moduleName}`,
            visibility: 'public',
            parentId,
            metadata: {
              type: methodName,
              module: moduleName
            }
          });
        }
      }
    }

    return null;
  }

  private extractModule(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'constant');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownModule';

    // Look for include/extend statements in the module body
    const includes = this.findIncludesAndExtends(node);
    let signature = `module ${name}`;
    if (includes.length > 0) {
      signature += `\n  ${includes.join('\n  ')}`;
    }

    return this.createSymbol(node, name, SymbolKind.Module, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'module',
        includes: includes.filter(i => i.startsWith('include')),
        extends: includes.filter(i => i.startsWith('extend'))
      }
    });
  }

  private extractClass(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.children.find(c => c.type === 'constant');
    const name = nameNode ? this.getNodeText(nameNode) : 'UnknownClass';

    // Check for inheritance
    const superclassNode = node.children.find(c => c.type === 'superclass');
    let signature = `class ${name}`;

    if (superclassNode) {
      const superclassName = this.getNodeText(superclassNode).replace('<', '').trim();
      signature += ` < ${superclassName}`;
    }

    // Look for include/extend statements
    const includes = this.findIncludesAndExtends(node);
    if (includes.length > 0) {
      signature += `\n  ${includes.join('\n  ')}`;
    }

    return this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'class',
        superclass: superclassNode ? this.getNodeText(superclassNode).replace('<', '').trim() : null,
        includes: includes.filter(i => i.startsWith('include')),
        extends: includes.filter(i => i.startsWith('extend'))
      }
    });
  }

  private extractMethod(node: Parser.SyntaxNode, parentId?: string, visibility: string = 'public'): Symbol {
    const nameNode = node.children.find(c => c.type === 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownMethod';

    const parametersNode = node.children.find(c => c.type === 'method_parameters');
    let signature = `def ${name}`;

    if (parametersNode) {
      signature += this.getNodeText(parametersNode);
    } else {
      signature += '()';
    }

    // Determine symbol kind
    const symbolKind = name === 'initialize' ? SymbolKind.Constructor : SymbolKind.Method;

    return this.createSymbol(node, name, symbolKind, {
      signature,
      visibility: visibility as 'public' | 'private' | 'protected',
      parentId,
      metadata: {
        type: symbolKind === SymbolKind.Constructor ? 'constructor' : 'method',
        parameters: parametersNode ? this.getNodeText(parametersNode) : '()',
        isInstance: true
      }
    });
  }

  private extractSingletonMethod(node: Parser.SyntaxNode, parentId?: string, visibility: string = 'public'): Symbol {
    const nameNode = node.children.find(c => c.type === 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'unknownMethod';

    const parametersNode = node.children.find(c => c.type === 'method_parameters');
    let signature = `def self.${name}`;

    if (parametersNode) {
      signature += this.getNodeText(parametersNode);
    } else {
      signature += '()';
    }

    return this.createSymbol(node, name, SymbolKind.Method, {
      signature,
      visibility: visibility as 'public' | 'private' | 'protected',
      parentId,
      metadata: {
        type: 'class_method',
        parameters: parametersNode ? this.getNodeText(parametersNode) : '()',
        isClass: true
      }
    });
  }

  private extractAssignment(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const leftNode = node.children[0];
    if (!leftNode) return null;

    const name = this.getNodeText(leftNode);
    // Find the value node - it's typically the last child after the '=' operator
    const rightNode = node.children.length >= 2 ? node.children[node.children.length - 1] : null;
    const value = rightNode ? this.getNodeText(rightNode) : '';

    // Class variables (@@var)
    if (name.startsWith('@@')) {
      return this.createSymbol(node, name, SymbolKind.Variable, {
        signature: `${name} = ${value}`,
        visibility: 'public',
        parentId,
        metadata: {
          type: 'class_variable',
          value
        }
      });
    }

    // Instance variables (@var)
    if (name.startsWith('@') && !name.startsWith('@@')) {
      return this.createSymbol(node, name, SymbolKind.Variable, {
        signature: `${name} = ${value}`,
        visibility: 'private',
        parentId,
        metadata: {
          type: 'instance_variable',
          value
        }
      });
    }

    // Constants (UPPERCASE)
    if (name.match(/^[A-Z][A-Z0-9_]*$/)) {
      return this.createSymbol(node, name, SymbolKind.Constant, {
        signature: `${name} = ${value}`,
        visibility: 'public',
        parentId,
        metadata: {
          type: 'constant',
          value
        }
      });
    }

    return null;
  }

  private extractConstant(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const name = this.getNodeText(node);

    // Only create symbols for standalone constants, not those in other contexts
    if (node.parent?.type === 'assignment') {
      return null; // Will be handled by extractAssignment
    }

    return this.createSymbol(node, name, SymbolKind.Constant, {
      signature: name,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'constant_reference'
      }
    });
  }

  private extractAlias(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const newNameNode = node.children.find(c => c.type === 'identifier' || c.type === 'simple_symbol');
    const oldNameNode = node.children.find((c, i) =>
      (c.type === 'identifier' || c.type === 'simple_symbol') &&
      i > node.children.indexOf(newNameNode!)
    );

    if (!newNameNode || !oldNameNode) return null;

    const newName = this.getNodeText(newNameNode).replace(':', '');
    const oldName = this.getNodeText(oldNameNode).replace(':', '');

    return this.createSymbol(node, newName, SymbolKind.Method, {
      signature: `alias ${newName} ${oldName}`,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'alias',
        originalMethod: oldName
      }
    });
  }

  private findIncludesAndExtends(node: Parser.SyntaxNode): string[] {
    const includes: string[] = [];

    const findInChildren = (n: Parser.SyntaxNode) => {
      if (n.type === 'call') {
        const methodNode = n.children.find(c => c.type === 'identifier');
        if (methodNode) {
          const methodName = this.getNodeText(methodNode);
          if (['include', 'extend'].includes(methodName)) {
            const argNode = n.children.find(c => c.type === 'argument_list');
            if (argNode) {
              const moduleNode = argNode.children.find(c => c.type === 'constant');
              if (moduleNode) {
                const moduleName = this.getNodeText(moduleNode);
                includes.push(`${methodName} ${moduleName}`);
              }
            }
          }
        }
      }

      // Only search immediate children for includes/extends
      for (const child of n.children || []) {
        if (child.type === 'call') {
          findInChildren(child);
        }
      }
    };

    findInChildren(node);
    return includes;
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];

    // In fallback mode, extract relationships from the content
    const content = this.getNodeText(tree.rootNode);

    // Extract inheritance relationships
    const classMatches = content.matchAll(/^(\s*)class\s+(\w+)(?:\s*<\s*(\w+))?/gm);
    for (const match of classMatches) {
      const className = match[2];
      const superclass = match[3];

      if (superclass) {
        const classSymbol = symbols.find(s => s.name === className && s.kind === SymbolKind.Class);
        if (classSymbol) {
          relationships.push({
            fromSymbolId: classSymbol.id,
            toSymbolId: `ruby-class:${superclass}`,
            kind: RelationshipKind.Extends,
            filePath: this.filePath,
            lineNumber: 1,
            confidence: 1.0,
            metadata: { superclass }
          });
        }
      }
    }

    // Extract include/extend relationships
    const includeMatches = content.matchAll(/^\s*(include|extend|prepend)\s+(\w+)/gm);
    for (const match of includeMatches) {
      const method = match[1];
      const moduleName = match[2];

      // Find the containing class/module by looking for the nearest preceding class/module definition
      const beforeMatch = content.substring(0, match.index);
      const containerMatch = beforeMatch.match(/(?:^|\n)\s*(?:class|module)\s+(\w+)[^\n]*$/);

      if (containerMatch) {
        const containerName = containerMatch[1];
        const containerSymbol = symbols.find(s => s.name === containerName &&
          (s.kind === SymbolKind.Class || s.kind === SymbolKind.Module));

        if (containerSymbol) {
          const kind = method === 'include' ? RelationshipKind.Includes : RelationshipKind.Uses;
          relationships.push({
            fromSymbolId: containerSymbol.id,
            toSymbolId: `ruby-module:${moduleName}`,
            kind,
            filePath: this.filePath,
            lineNumber: 1,
            confidence: 1.0,
            metadata: { module: moduleName, method }
          });
        }
      }
    }

    return relationships;
  }

  private extractClassRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const classSymbol = this.findClassSymbol(node, symbols);
    if (!classSymbol) return;

    // Inheritance relationship
    const superclassNode = node.children.find(c => c.type === 'superclass');
    if (superclassNode) {
      const superclassName = this.getNodeText(superclassNode).replace('<', '').trim();
      relationships.push({
        fromSymbolId: classSymbol.id,
        toSymbolId: `ruby-class:${superclassName}`,
        kind: RelationshipKind.Extends,
        filePath: this.filePath,
        lineNumber: node.startPosition.row + 1,
        confidence: 1.0,
        metadata: { superclass: superclassName }
      });
    }
  }

  private extractCallRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const methodNode = node.children.find(c => c.type === 'identifier');
    if (!methodNode) return;

    const methodName = this.getNodeText(methodNode);

    // Include/extend relationships
    if (['include', 'extend'].includes(methodName)) {
      const argNode = node.children.find(c => c.type === 'argument_list');
      if (argNode) {
        const moduleNode = argNode.children.find(c => c.type === 'constant');
        if (moduleNode) {
          const moduleName = this.getNodeText(moduleNode);
          const kind = methodName === 'include' ? RelationshipKind.Includes : RelationshipKind.Uses;

          // Find the containing class/module
          let current = node.parent;
          while (current && !['class', 'module'].includes(current.type)) {
            current = current.parent;
          }

          if (current) {
            const containingSymbol = this.findClassOrModuleSymbol(current, symbols);
            if (containingSymbol) {
              relationships.push({
                fromSymbolId: containingSymbol.id,
                toSymbolId: `ruby-module:${moduleName}`,
                kind,
                filePath: this.filePath,
                lineNumber: node.startPosition.row + 1,
                confidence: 1.0,
                metadata: { module: moduleName, method: methodName }
              });
            }
          }
        }
      }
    }
  }

  private findClassSymbol(node: Parser.SyntaxNode, symbols: Symbol[]): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'constant');
    const className = nameNode ? this.getNodeText(nameNode) : null;

    return symbols.find(s =>
      s.name === className &&
      s.kind === SymbolKind.Class &&
      s.filePath === this.filePath
    ) || null;
  }

  private findClassOrModuleSymbol(node: Parser.SyntaxNode, symbols: Symbol[]): Symbol | null {
    const nameNode = node.children.find(c => c.type === 'constant');
    const name = nameNode ? this.getNodeText(nameNode) : null;

    return symbols.find(s =>
      s.name === name &&
      (s.kind === SymbolKind.Class || s.kind === SymbolKind.Module) &&
      s.filePath === this.filePath
    ) || null;
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();
    for (const symbol of symbols) {
      if (symbol.metadata?.type) {
        types.set(symbol.id, symbol.metadata.type);
      }
    }
    return types;
  }

  private extractBasicStructure(tree: Parser.Tree): Symbol[] {
    // Fallback extraction when tree-sitter parsing fails - process line by line
    const symbols: Symbol[] = [];
    const content = this.getNodeText(tree.rootNode);
    const symbolMap = new Map<string, Symbol>(); // Track symbols for parent relationships

    // Split content into lines to track indentation and context sequentially
    const lines = content.split('\n');
    const contextStack: { name: string; symbol: Symbol; indentLevel: number; lineNumber: number }[] = [];

    // Track visibility as we process lines
    let currentVisibility: 'public' | 'private' | 'protected' = 'public';

    // Process each line sequentially to maintain proper context
    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      const trimmedLine = line.trim();

      if (!trimmedLine || trimmedLine.startsWith('#')) {
        continue; // Skip empty lines and comments
      }

      const indentLevel = line.length - line.trimStart().length;

      // Check for visibility modifiers
      if (trimmedLine === 'private') {
        currentVisibility = 'private';
        continue;
      } else if (trimmedLine === 'protected') {
        currentVisibility = 'protected';
        continue;
      } else if (trimmedLine === 'public') {
        currentVisibility = 'public';
        continue;
      }

      // Extract require statements
      const requireMatch = line.match(/^(require(?:_relative)?)\s+['"](.*?)['"]$/);
      if (requireMatch) {
        const method = requireMatch[1];
        const path = requireMatch[2];
        const moduleName = path.split('/').pop() || path;

        const symbol = this.createSymbol(tree.rootNode, moduleName, SymbolKind.Import, {
          signature: `${method} '${path}'`,
          visibility: 'public',
          metadata: {
            type: 'require',
            method,
            path,
            isFallback: true
          }
        });
        symbols.push(symbol);
        symbolMap.set(moduleName, symbol);
        continue;
      }

      // Extract modules
      const moduleMatch = line.match(/^(\s*)module\s+(\w+)/);
      if (moduleMatch) {
        const moduleName = moduleMatch[2];

        // Find parent based on indentation
        const parent = this.findParentByIndentation(contextStack, indentLevel);

        // Look ahead for include/extend statements in this module
        const includes = this.findIncludesInBlock(lines, i, indentLevel);
        let signature = `module ${moduleName}`;
        if (includes.length > 0) {
          signature += `\n  ${includes.join('\n  ')}`;
        }

        const symbol = this.createSymbol(tree.rootNode, moduleName, SymbolKind.Module, {
          signature,
          visibility: 'public',
          parentId: parent?.id,
          metadata: {
            type: 'module',
            includes: includes.filter(i => i.startsWith('include')),
            extends: includes.filter(i => i.startsWith('extend')),
            isFallback: true
          }
        });
        symbols.push(symbol);
        symbolMap.set(moduleName, symbol);

        // Update context stack
        this.updateContextStack(contextStack, {
          name: moduleName,
          symbol,
          indentLevel,
          lineNumber: i + 1
        });
        continue;
      }

      // Extract classes with optional inheritance
      const classMatch = line.match(/^(\s*)class\s+(\w+)(?:\s*<\s*(\w+))?/);
      if (classMatch) {
        const className = classMatch[2];
        const superclass = classMatch[3];

        // Find parent based on indentation
        const parent = this.findParentByIndentation(contextStack, indentLevel);

        // Look ahead for include/extend statements in this class
        const includes = this.findIncludesInBlock(lines, i, indentLevel);
        let signature = `class ${className}`;
        if (superclass) {
          signature += ` < ${superclass}`;
        }
        if (includes.length > 0) {
          signature += `\n  ${includes.join('\n  ')}`;
        }

        const symbol = this.createSymbol(tree.rootNode, className, SymbolKind.Class, {
          signature,
          visibility: 'public',
          parentId: parent?.id,
          metadata: {
            type: 'class',
            superclass,
            includes: includes.filter(i => i.startsWith('include')),
            extends: includes.filter(i => i.startsWith('extend')),
            isFallback: true
          }
        });
        symbols.push(symbol);
        symbolMap.set(className, symbol);

        // Update context stack
        this.updateContextStack(contextStack, {
          name: className,
          symbol,
          indentLevel,
          lineNumber: i + 1
        });
        continue;
      }

      // Extract methods (including operator methods and singleton methods on objects)
      const methodMatch = line.match(/^(\s*)def\s+((?:self\.|[\w@$]+\.)?)?([a-zA-Z_]\w*[?!]?|[<>=]+|[+\-*\/%]|\[\]|<<|>>|&|\||\^|~|`|===|<=>|!=|<=|>=|==|=~|!~)(\([^)]*\))?/);
      if (methodMatch) {
        const objectPrefix = methodMatch[2] || '';
        const methodName = methodMatch[3];
        const params = methodMatch[4] || '()';

        // Determine if this is a class method or singleton method
        const isClassMethod = objectPrefix === 'self.';
        const isSingletonMethod = objectPrefix && objectPrefix !== 'self.' && objectPrefix.endsWith('.');

        // Find parent based on indentation
        const parent = this.findParentByIndentation(contextStack, indentLevel);

        let signature = `def ${objectPrefix}${methodName}${params}`;
        const symbolKind = methodName === 'initialize' ? SymbolKind.Constructor : SymbolKind.Method;

        const symbol = this.createSymbol(tree.rootNode, methodName, symbolKind, {
          signature,
          visibility: currentVisibility,
          parentId: parent?.id,
          metadata: {
            type: symbolKind === SymbolKind.Constructor ? 'constructor' :
                  isSingletonMethod ? 'singleton_method' : 'method',
            isClass: isClassMethod,
            isInstance: !isClassMethod && !isSingletonMethod,
            isSingleton: isSingletonMethod,
            objectTarget: isSingletonMethod ? objectPrefix.replace('.', '') : undefined,
            isFallback: true
          }
        });
        symbols.push(symbol);
        symbolMap.set(`${parent?.name || 'global'}::${methodName}`, symbol);
        continue;
      }

      // Extract dynamic method definitions (broader pattern)
      const defineMethodMatch = line.match(/.*define_method\s*[:\(]?\s*[:'"]?(\w+)[:'"]?\s*[\),]?/);
      if (defineMethodMatch && trimmedLine.includes('define_method')) {
        const methodName = defineMethodMatch[1];

        // Find parent based on indentation
        const parent = this.findParentByIndentation(contextStack, indentLevel);

        // Check if this is the definition or just a symbol being created
        const isDefinition = trimmedLine.includes('define_method');
        const signatureText = isDefinition ? `define_method :${methodName}` : methodName;

        const symbol = this.createSymbol(tree.rootNode, methodName, SymbolKind.Method, {
          signature: signatureText,
          visibility: currentVisibility,
          parentId: parent?.id,
          metadata: {
            type: 'dynamic_method',
            isFallback: true
          }
        });
        symbols.push(symbol);
        symbolMap.set(`${parent?.name || 'global'}::${methodName}`, symbol);
        continue;
      }

      // Extract singleton classes (class << self)
      const singletonClassMatch = line.match(/^(\s*)class\s*<<\s*(self|\w+)/);
      if (singletonClassMatch) {
        const target = singletonClassMatch[2];

        // Find parent based on indentation
        const parent = this.findParentByIndentation(contextStack, indentLevel);

        const singletonName = `singleton_${target}`;
        const signature = `class << ${target}`;

        const symbol = this.createSymbol(tree.rootNode, singletonName, SymbolKind.Class, {
          signature,
          visibility: 'public',
          parentId: parent?.id,
          metadata: {
            type: 'singleton_class',
            target,
            isFallback: true
          }
        });
        symbols.push(symbol);
        symbolMap.set(singletonName, symbol);

        // Update context stack
        this.updateContextStack(contextStack, {
          name: singletonName,
          symbol,
          indentLevel,
          lineNumber: i + 1
        });
        continue;
      }

      // Extract nested modules/classes (Module::Class syntax)
      const nestedModuleMatch = line.match(/^(\s*)(module|class)\s+(\w+::)?(\w+)(?:\s*<\s*(\w+))?/);
      if (nestedModuleMatch) {
        const kindText = nestedModuleMatch[2];
        const namespace = nestedModuleMatch[3];
        const name = nestedModuleMatch[4];
        const superclass = nestedModuleMatch[5];

        // Find parent based on indentation
        const parent = this.findParentByIndentation(contextStack, indentLevel);

        let signature = `${kindText} ${namespace || ''}${name}`;
        if (superclass) {
          signature += ` < ${superclass}`;
        }

        const symbolKind = kindText === 'module' ? SymbolKind.Module : SymbolKind.Class;

        const symbol = this.createSymbol(tree.rootNode, name, symbolKind, {
          signature,
          visibility: 'public',
          parentId: parent?.id,
          metadata: {
            type: kindText,
            namespace: namespace?.replace('::', ''),
            superclass,
            isFallback: true
          }
        });
        symbols.push(symbol);
        symbolMap.set(name, symbol);

        // Update context stack
        this.updateContextStack(contextStack, {
          name,
          symbol,
          indentLevel,
          lineNumber: i + 1
        });
        continue;
      }

      // Extract instance variables (@var = value)
      const instanceVarMatch = line.match(/^\s*(@\w+)\s*=\s*(.+)$/);
      if (instanceVarMatch) {
        const varName = instanceVarMatch[1];
        const value = instanceVarMatch[2];

        // Find parent based on indentation
        const parent = this.findParentByIndentation(contextStack, indentLevel);

        const symbol = this.createSymbol(tree.rootNode, varName, SymbolKind.Variable, {
          signature: `${varName} = ${value}`,
          visibility: 'private',
          parentId: parent?.id,
          metadata: {
            type: 'instance_variable',
            value,
            isFallback: true
          }
        });
        symbols.push(symbol);
        symbolMap.set(`${parent?.name || 'global'}::${varName}`, symbol);
        continue;
      }

      // Extract local variables and Proc/lambda assignments
      const localVarMatch = line.match(/^\s*([a-z_]\w*)\s*=\s*(.*)/);
      if (localVarMatch) {
        const varName = localVarMatch[1];
        const value = localVarMatch[2];

        // Skip if it looks like a method parameter or other construct
        if (!varName.includes('def') && !varName.includes('class') && !varName.includes('module') &&
            !trimmedLine.startsWith('end')) {
          // Find parent based on indentation
          const parent = this.findParentByIndentation(contextStack, indentLevel);

          const symbol = this.createSymbol(tree.rootNode, varName, SymbolKind.Variable, {
            signature: `${varName} = ${value}`,
            visibility: 'public',
            parentId: parent?.id,
            metadata: {
              type: 'local_variable',
              value,
              isFallback: true
            }
          });
          symbols.push(symbol);
          symbolMap.set(`${parent?.name || 'global'}::${varName}`, symbol);
          continue;
        }
      }

      // Extract attr_* declarations
      const attrMatch = line.match(/^\s*(attr_(?:reader|writer|accessor))\s+(:\w+(?:\s*,\s*:\w+)*)/);
      if (attrMatch) {
        const attrType = attrMatch[1];
        const symbolList = attrMatch[2];
        const symbolNames = symbolList.split(',').map(s => s.trim().replace(':', ''));

        // Find parent based on indentation
        const parent = this.findParentByIndentation(contextStack, indentLevel);

        for (const attrName of symbolNames) {
          const symbol = this.createSymbol(tree.rootNode, attrName, SymbolKind.Property, {
            signature: `${attrType} :${attrName}`,
            visibility: 'public',
            parentId: parent?.id,
            metadata: {
              type: 'attribute',
              accessor: attrType,
              isFallback: true
            }
          });
          symbols.push(symbol);
          symbolMap.set(`${parent?.name || 'global'}::${attrName}`, symbol);
        }
        continue;
      }

      // Extract constants (UPPERCASE assignments)
      const constantMatch = line.match(/^\s*([A-Z][A-Z0-9_]*)\s*=\s*(.+)$/);
      if (constantMatch) {
        const constantName = constantMatch[1];
        const value = constantMatch[2];

        // Find parent based on indentation
        const parent = this.findParentByIndentation(contextStack, indentLevel);

        const symbol = this.createSymbol(tree.rootNode, constantName, SymbolKind.Constant, {
          signature: `${constantName} = ${value}`,
          visibility: 'public',
          parentId: parent?.id,
          metadata: {
            type: 'constant',
            value,
            isFallback: true
          }
        });
        symbols.push(symbol);
        symbolMap.set(`${parent?.name || 'global'}::${constantName}`, symbol);
        continue;
      }

      // Extract class variables (@@var = value) - enhanced pattern
      const classVarMatch = line.match(/^\s*(@@[a-zA-Z_]\w*)\s*[=||\+=|\-=|\*=|\/=]\s*(.+)$/);
      if (classVarMatch) {
        const varName = classVarMatch[1];
        const value = classVarMatch[2];

        // Find parent based on indentation
        const parent = this.findParentByIndentation(contextStack, indentLevel);

        const symbol = this.createSymbol(tree.rootNode, varName, SymbolKind.Variable, {
          signature: `${varName} = ${value}`,
          visibility: 'public',
          parentId: parent?.id,
          metadata: {
            type: 'class_variable',
            value,
            isFallback: true
          }
        });
        symbols.push(symbol);
        symbolMap.set(`${parent?.name || 'global'}::${varName}`, symbol);
        continue;
      }

      // Extract simple class variable references or declarations
      const classVarRefMatch = line.match(/^\s*(@@[a-zA-Z_]\w*)/);
      if (classVarRefMatch && !symbols.find(s => s.name === classVarRefMatch[1])) {
        const varName = classVarRefMatch[1];

        // Find parent based on indentation
        const parent = this.findParentByIndentation(contextStack, indentLevel);

        const symbol = this.createSymbol(tree.rootNode, varName, SymbolKind.Variable, {
          signature: varName,
          visibility: 'public',
          parentId: parent?.id,
          metadata: {
            type: 'class_variable',
            isFallback: true
          }
        });
        symbols.push(symbol);
        symbolMap.set(`${parent?.name || 'global'}::${varName}`, symbol);
        continue;
      }

      // Extract def_delegator and other delegation calls
      const delegatorMatch = line.match(/^\s*(def_delegator|def_delegators)\s+(.+)/);
      if (delegatorMatch) {
        const method = delegatorMatch[1];
        const args = delegatorMatch[2];

        // Find parent based on indentation
        const parent = this.findParentByIndentation(contextStack, indentLevel);

        const symbol = this.createSymbol(tree.rootNode, method, SymbolKind.Method, {
          signature: `${method} ${args}`,
          visibility: 'public',
          parentId: parent?.id,
          metadata: {
            type: 'delegation',
            delegationType: method,
            args,
            isFallback: true
          }
        });
        symbols.push(symbol);
        symbolMap.set(`${parent?.name || 'global'}::${method}`, symbol);
        continue;
      }

      // Extract alias statements (alias new_name old_name)
      const aliasMatch = line.match(/^\s*alias\s+([a-zA-Z_]\w*[?!]?)\s+([a-zA-Z_]\w*[?!]?)/);
      if (aliasMatch) {
        const newName = aliasMatch[1];
        const oldName = aliasMatch[2];

        // Find parent based on indentation
        const parent = this.findParentByIndentation(contextStack, indentLevel);

        const symbol = this.createSymbol(tree.rootNode, newName, SymbolKind.Method, {
          signature: `alias ${newName} ${oldName}`,
          visibility: currentVisibility,
          parentId: parent?.id,
          metadata: {
            type: 'alias',
            originalMethod: oldName,
            isFallback: true
          }
        });
        symbols.push(symbol);
        symbolMap.set(`${parent?.name || 'global'}::${newName}`, symbol);
        continue;
      }

      // Extract alias_method calls
      const aliasMethodMatch = line.match(/^\s*alias_method\s+[:'"]([^'"]+)['"]\s*,\s*[:'"]([^'"]+)['"]/);
      if (aliasMethodMatch) {
        const newName = aliasMethodMatch[1];
        const oldName = aliasMethodMatch[2];

        // Find parent based on indentation
        const parent = this.findParentByIndentation(contextStack, indentLevel);

        const symbol = this.createSymbol(tree.rootNode, newName, SymbolKind.Method, {
          signature: `alias_method :${newName}, :${oldName}`,
          visibility: currentVisibility,
          parentId: parent?.id,
          metadata: {
            type: 'alias_method',
            originalMethod: oldName,
            isFallback: true
          }
        });
        symbols.push(symbol);
        symbolMap.set(`${parent?.name || 'global'}::${newName}`, symbol);
        continue;
      }

      // Extract other method calls that should be tracked as symbols
      const methodCallMatch = line.match(/^\s*([a-z_]\w*)\s+(.+)/);
      if (methodCallMatch && !trimmedLine.includes('=')) {
        const methodName = methodCallMatch[1];
        const args = methodCallMatch[2];

        // Only track specific known Ruby metaprogramming methods
        if (['module_function', 'extend_object', 'append_features', 'included', 'extended', 'prepended'].includes(methodName)) {
          // Find parent based on indentation
          const parent = this.findParentByIndentation(contextStack, indentLevel);

          const symbol = this.createSymbol(tree.rootNode, methodName, SymbolKind.Method, {
            signature: `${methodName} ${args}`,
            visibility: 'public',
            parentId: parent?.id,
            metadata: {
              type: 'meta_method',
              methodType: methodName,
              args,
              isFallback: true
            }
          });
          symbols.push(symbol);
          symbolMap.set(`${parent?.name || 'global'}::${methodName}`, symbol);
          continue;
        }
      }
    }

    return symbols;
  }

  private findParentByIndentation(
    contextStack: { name: string; symbol: Symbol; indentLevel: number; lineNumber: number }[],
    currentIndentLevel: number
  ): Symbol | null {
    // Find the most recent context with less indentation (closer to root)
    for (let i = contextStack.length - 1; i >= 0; i--) {
      const context = contextStack[i];
      if (context.indentLevel < currentIndentLevel) {
        return context.symbol;
      }
    }
    return null;
  }

  private updateContextStack(
    contextStack: { name: string; symbol: Symbol; indentLevel: number; lineNumber: number }[],
    newContext: { name: string; symbol: Symbol; indentLevel: number; lineNumber: number }
  ): void {
    // Remove contexts with equal or greater indentation (same level or more nested)
    while (contextStack.length > 0 && contextStack[contextStack.length - 1].indentLevel >= newContext.indentLevel) {
      contextStack.pop();
    }
    // Add the new context
    contextStack.push(newContext);
  }

  private getLineNumber(content: string, index: number): number {
    const upToIndex = content.substring(0, index);
    return upToIndex.split('\n').length;
  }

  private findIncludesInBlock(lines: string[], startIndex: number, blockIndentLevel: number): string[] {
    const includes: string[] = [];

    // Look ahead from the current line to find include/extend statements
    for (let i = startIndex + 1; i < lines.length; i++) {
      const line = lines[i];
      const trimmedLine = line.trim();

      if (!trimmedLine || trimmedLine.startsWith('#')) {
        continue; // Skip empty lines and comments
      }

      const indentLevel = line.length - line.trimStart().length;

      // If we've reached a line at the same or lower indentation level, we've left the block
      if (indentLevel <= blockIndentLevel && trimmedLine !== 'end') {
        break;
      }

      // Look for include/extend statements at the immediate child level
      const includeMatch = line.match(/^\s*(include|extend|prepend)\s+(\w+)/);
      if (includeMatch && indentLevel === blockIndentLevel + 2) { // Typical Ruby indentation
        const method = includeMatch[1];
        const moduleName = includeMatch[2];
        includes.push(`${method} ${moduleName}`);
      }
    }

    return includes;
  }
}