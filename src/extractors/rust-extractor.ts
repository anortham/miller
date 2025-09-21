import { Parser } from 'web-tree-sitter';
import { BaseExtractor, Symbol, Relationship, SymbolKind, RelationshipKind } from './base-extractor.js';

/**
 * Rust language extractor that handles Rust-specific constructs including:
 * - Structs and enums
 * - Traits and implementations (impl blocks)
 * - Functions with ownership patterns (&self, &mut self, etc.)
 * - Modules (mod)
 * - Macros (macro_rules!)
 * - Use statements (imports)
 * - Constants and statics
 * - Type definitions
 */
export class RustExtractor extends BaseExtractor {
  private implBlocks?: Array<{ node: Parser.SyntaxNode; typeName: string; parentId?: string }>;
  private isProcessingImplBlocks = false;

  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    this.implBlocks = []; // Reset impl blocks for new extraction
    this.isProcessingImplBlocks = false;
    this.walkTree(tree.rootNode, symbols);

    // Process impl blocks after all symbols are extracted
    this.isProcessingImplBlocks = true;
    this.processImplBlocks(symbols);

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
      case 'struct_item':
        return this.extractStruct(node, parentId);
      case 'enum_item':
        return this.extractEnum(node, parentId);
      case 'trait_item':
        return this.extractTrait(node, parentId);
      case 'impl_item':
        return this.extractImpl(node, parentId);
      case 'function_item':
        return this.extractFunction(node, parentId);
      case 'function_signature_item':
        return this.extractFunctionSignature(node, parentId);
      case 'associated_type':
        return this.extractAssociatedType(node, parentId);
      case 'union_item':
        return this.extractUnion(node, parentId);
      case 'macro_invocation':
        return this.extractMacroInvocation(node, parentId);
      case 'mod_item':
        return this.extractModule(node, parentId);
      case 'use_declaration':
        return this.extractUse(node, parentId);
      case 'const_item':
        return this.extractConst(node, parentId);
      case 'static_item':
        return this.extractStatic(node, parentId);
      case 'macro_definition':
        return this.extractMacro(node, parentId);
      case 'type_item':
        return this.extractTypeAlias(node, parentId);
      default:
        return null;
    }
  }

  protected findDocComment(node: Parser.SyntaxNode): string | undefined {
    // Look for preceding doc comments (///, /** */)
    const prevSibling = this.getPreviousSibling(node);
    if (prevSibling?.type === 'line_comment') {
      const commentText = this.getNodeText(prevSibling);
      // Rust doc comments start with ///
      if (commentText.startsWith('///')) {
        return commentText.substring(3).trim();
      }
    }

    // Look for attribute doc comments like #[doc = "..."]
    const attributes = this.getPrecedingAttributes(node);
    for (const attr of attributes) {
      const docComment = this.extractDocFromAttribute(attr);
      if (docComment) return docComment;
    }

    return undefined;
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const typeMap = new Map<string, string>();

    for (const symbol of symbols) {
      if (symbol.signature) {
        const type = this.inferTypeFromSignature(symbol.signature, symbol.kind);
        if (type) {
          typeMap.set(symbol.id, type);
        }
      }
    }

    return typeMap;
  }

  private extractStruct(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    // Extract visibility and attributes
    const visibility = this.extractVisibility(node);
    const attributes = this.getPrecedingAttributes(node);
    const derivedTraits = this.extractDerivedTraits(attributes);

    // Extract generic type parameters
    const typeParamsNode = node.children.find(c => c.type === 'type_parameters');
    const typeParams = typeParamsNode ? this.getNodeText(typeParamsNode) : '';

    // Build signature
    let signature = `${visibility}struct ${name}${typeParams}`;
    if (derivedTraits.length > 0) {
      signature = `#[derive(${derivedTraits.join(', ')})] ${signature}`;
    }

    const visibilityForSymbol = visibility.trim() || 'private';

    return this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      visibility: visibilityForSymbol,
      parentId
    });
  }

  private extractEnum(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    const visibility = this.extractVisibility(node);
    const attributes = this.getPrecedingAttributes(node);
    const derivedTraits = this.extractDerivedTraits(attributes);

    // Extract generic type parameters
    const typeParamsNode = node.children.find(c => c.type === 'type_parameters');
    const typeParams = typeParamsNode ? this.getNodeText(typeParamsNode) : '';

    let signature = `${visibility}enum ${name}${typeParams}`;
    if (derivedTraits.length > 0) {
      signature = `#[derive(${derivedTraits.join(', ')})] ${signature}`;
    }

    const visibilityForSymbol = visibility.trim() || 'private';

    return this.createSymbol(node, name, SymbolKind.Class, {
      signature,
      visibility: visibilityForSymbol,
      parentId
    });
  }

  private extractTrait(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    const visibility = this.extractVisibility(node);

    // Extract generic type parameters
    const typeParamsNode = node.children.find(c => c.type === 'type_parameters');
    const typeParams = typeParamsNode ? this.getNodeText(typeParamsNode) : '';

    // Extract trait bounds
    const traitBoundsNode = node.children.find(c => c.type === 'trait_bounds');
    const traitBounds = traitBoundsNode ? this.getNodeText(traitBoundsNode) : '';

    // Extract associated types from declaration_list
    const declarationList = node.children.find(c => c.type === 'declaration_list');
    const associatedTypes: string[] = [];
    if (declarationList) {
      for (const child of declarationList.children) {
        if (child.type === 'associated_type') {
          associatedTypes.push(this.getNodeText(child).replace(/;$/, ''));
        }
      }
    }

    // Build signature
    let signature = `${visibility}trait ${name}${typeParams}${traitBounds}`;
    if (associatedTypes.length > 0) {
      signature += ` { ${associatedTypes.join('; ')} }`;
    }

    const visibilityForSymbol = visibility.trim() || 'private';

    return this.createSymbol(node, name, SymbolKind.Interface, {
      signature,
      visibility: visibilityForSymbol,
      parentId
    });
  }

  private extractImpl(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // For impl blocks, we need to extract methods and link them to the struct
    // Store the impl info in instance variable to link later
    const typeNode = node.children.find(c => c.type === 'type_identifier');
    const typeName = typeNode ? this.getNodeText(typeNode) : 'anonymous';

    // Store impl context for post-processing
    if (!this.implBlocks) this.implBlocks = [];
    this.implBlocks.push({ node, typeName, parentId });

    return null;
  }

  // Process impl blocks after all symbols are extracted
  private processImplBlocks(symbols: Symbol[]): void {
    if (!this.implBlocks) return;

    for (const implBlock of this.implBlocks) {
      // Find the struct/enum this impl is for
      const structSymbol = symbols.find(s => s.name === implBlock.typeName &&
        (s.kind === SymbolKind.Class || s.kind === SymbolKind.Interface));

      if (structSymbol) {
        // Extract methods with correct parentId
        const declarationList = implBlock.node.children.find(c => c.type === 'declaration_list');
        if (declarationList) {
          for (const child of declarationList.children) {
            if (child.type === 'function_item') {
              const methodSymbol = this.extractFunction(child, structSymbol.id);
              if (methodSymbol) {
                methodSymbol.kind = SymbolKind.Method;
                symbols.push(methodSymbol);
              }
            }
          }
        }
      }
    }
  }

  private extractFunction(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'anonymous';

    // Determine if this is a method (inside impl block) or standalone function
    const isMethod = this.isInsideImpl(node);
    const kind = isMethod ? SymbolKind.Method : SymbolKind.Function;

    // Skip extraction if inside impl block during normal tree walk (will be handled in post-processing)
    if (isMethod && !this.isProcessingImplBlocks) {
      return null;
    }

    // Extract function signature components
    const visibility = this.extractVisibility(node);
    const asyncKeyword = this.hasAsyncKeyword(node);
    const unsafeKeyword = this.hasUnsafeKeyword(node);
    const externModifier = this.extractExternModifier(node);
    const params = this.extractFunctionParameters(node);
    const returnType = this.extractReturnType(node);

    // Extract generic type parameters
    const typeParamsNode = node.children.find(c => c.type === 'type_parameters');
    const typeParams = typeParamsNode ? this.getNodeText(typeParamsNode) : '';

    // Extract where clause
    const whereClauseNode = node.children.find(c => c.type === 'where_clause');
    const whereClause = whereClauseNode ? ` ${this.getNodeText(whereClauseNode)}` : '';

    // Build signature
    let signature = '';
    if (visibility) signature += visibility;
    if (externModifier) signature += `${externModifier} `;
    if (unsafeKeyword) signature += 'unsafe ';
    if (asyncKeyword) signature += 'async ';
    signature += `fn ${name}${typeParams}(${params.join(', ')})`;
    if (returnType) signature += ` -> ${returnType}`;
    signature += whereClause;

    const visibilityForSymbol = visibility.trim() || 'private';

    return this.createSymbol(node, name, kind, {
      signature,
      visibility: visibilityForSymbol,
      parentId
    });
  }

  private extractFunctionSignature(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // Extract FFI function signatures from extern blocks
    const nameNode = node.children.find(c => c.type === 'identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'anonymous';

    // Extract parameters
    const paramsNode = node.children.find(c => c.type === 'parameters');
    const params = paramsNode ? this.getNodeText(paramsNode) : '()';

    // Extract return type (after -> token)
    const arrowIndex = node.children.findIndex(c => c.text === '->');
    const returnTypeNode = arrowIndex >= 0 && arrowIndex + 1 < node.children.length
      ? node.children[arrowIndex + 1]
      : null;
    const returnType = returnTypeNode ? ` -> ${this.getNodeText(returnTypeNode)}` : '';

    // Build signature for extern function
    const signature = `fn ${name}${params}${returnType}`;

    return this.createSymbol(node, name, SymbolKind.Function, {
      signature,
      visibility: 'public', // extern functions are typically public
      parentId
    });
  }

  private extractAssociatedType(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // Extract associated types from trait declarations
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'anonymous';

    // Extract trait bounds (: Debug + Clone, etc.)
    const traitBoundsNode = node.children.find(c => c.type === 'trait_bounds');
    const traitBounds = traitBoundsNode ? this.getNodeText(traitBoundsNode) : '';

    // Build signature
    const signature = `type ${name}${traitBounds}`;

    return this.createSymbol(node, name, SymbolKind.Type, {
      signature,
      visibility: 'public', // associated types in traits are public
      parentId
    });
  }

  private extractUnion(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // Extract union types (similar to struct but with union keyword)
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'Anonymous';

    const visibility = this.extractVisibility(node);

    // Build signature
    let signature = `${visibility}union ${name}`;

    const visibilityForSymbol = visibility.trim() || 'private';

    return this.createSymbol(node, name, SymbolKind.Union, {
      signature,
      visibility: visibilityForSymbol,
      parentId
    });
  }

  private extractMacroInvocation(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Extract symbols from macro invocations like generate_struct!(MacroStruct1)
    const macroNameNode = node.children.find(c => c.type === 'identifier');
    const macroName = macroNameNode ? this.getNodeText(macroNameNode) : '';

    // Look for struct-generating macros or known patterns
    if (macroName.includes('struct') || macroName.includes('generate')) {
      const tokenTreeNode = node.children.find(c => c.type === 'token_tree');
      if (tokenTreeNode) {
        // Extract the first identifier from the token tree as the struct name
        const structNameNode = tokenTreeNode.children.find(c => c.type === 'identifier');
        if (structNameNode) {
          const structName = this.getNodeText(structNameNode);

          // Create a struct symbol from the macro invocation
          const signature = `struct ${structName}`;

          return this.createSymbol(node, structName, SymbolKind.Class, {
            signature,
            visibility: 'public', // assume macro-generated types are public
            parentId
          });
        }
      }
    }

    return null;
  }

  private extractModule(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'anonymous';

    const visibility = this.extractVisibility(node);
    const signature = `${visibility}mod ${name}`;
    const visibilityForSymbol = visibility.trim() || 'private';

    return this.createSymbol(node, name, SymbolKind.Namespace, {
      signature,
      visibility: visibilityForSymbol,
      parentId
    });
  }

  private extractUse(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Extract the import path and alias
    const useText = this.getNodeText(node);

    // Simple pattern matching for common use cases
    if (useText.includes(' as ')) {
      // use std::collections::HashMap as Map;
      const parts = useText.split(' as ');
      if (parts.length === 2) {
        const alias = parts[1].replace(';', '').trim();
        return this.createSymbol(node, alias, SymbolKind.Import, {
          signature: useText,
          visibility: 'public',
          parentId
        });
      }
    } else {
      // use std::collections::HashMap;
      const match = useText.match(/use\s+(?:.*::)?(\w+)\s*;/);
      if (match) {
        const name = match[1];
        return this.createSymbol(node, name, SymbolKind.Import, {
          signature: useText,
          visibility: 'public',
          parentId
        });
      }
    }

    return null;
  }

  private extractConst(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'anonymous';

    const visibility = this.extractVisibility(node);
    const typeNode = node.childForFieldName('type');
    const valueNode = node.childForFieldName('value');

    let signature = `${visibility}const ${name}`;
    if (typeNode) signature += `: ${this.getNodeText(typeNode)}`;
    if (valueNode) signature += ` = ${this.getNodeText(valueNode)}`;

    const visibilityForSymbol = visibility.trim() || 'private';

    return this.createSymbol(node, name, SymbolKind.Constant, {
      signature,
      visibility: visibilityForSymbol,
      parentId
    });
  }

  private extractStatic(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'anonymous';

    const visibility = this.extractVisibility(node);
    const isMutable = node.children.some(c => c.type === 'mutable_specifier');
    const typeNode = node.childForFieldName('type');
    const valueNode = node.childForFieldName('value');

    let signature = `${visibility}static `;
    if (isMutable) signature += 'mut ';
    signature += name;
    if (typeNode) signature += `: ${this.getNodeText(typeNode)}`;
    if (valueNode) signature += ` = ${this.getNodeText(valueNode)}`;

    const visibilityForSymbol = visibility.trim() || 'private';

    return this.createSymbol(node, name, SymbolKind.Variable, {
      signature,
      visibility: visibilityForSymbol,
      parentId
    });
  }

  private extractMacro(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const nameNode = node.childForFieldName('name');
    const name = nameNode ? this.getNodeText(nameNode) : 'anonymous';

    const signature = `macro_rules! ${name}`;

    return this.createSymbol(node, name, SymbolKind.Function, {
      signature,
      visibility: 'public',
      parentId
    });
  }

  private extractTypeAlias(node: Parser.SyntaxNode, parentId?: string): Symbol {
    // Extract name from type_identifier child
    const nameNode = node.children.find(c => c.type === 'type_identifier');
    const name = nameNode ? this.getNodeText(nameNode) : 'anonymous';

    const visibility = this.extractVisibility(node);

    // Extract generic type parameters
    const typeParamsNode = node.children.find(c => c.type === 'type_parameters');
    const typeParams = typeParamsNode ? this.getNodeText(typeParamsNode) : '';

    // Extract the type definition (after =)
    // Find the = token first, then get the next meaningful node
    const equalsIndex = node.children.findIndex(c => c.text === '=');
    const typeDefNode = equalsIndex >= 0 && equalsIndex + 1 < node.children.length
      ? node.children[equalsIndex + 1]
      : null;
    const typeDef = typeDefNode ? ` = ${this.getNodeText(typeDefNode)}` : '';

    // Build signature
    let signature = `${visibility}type ${name}${typeParams}${typeDef}`;

    const visibilityForSymbol = visibility.trim() || 'private';

    return this.createSymbol(node, name, SymbolKind.Type, {
      signature,
      visibility: visibilityForSymbol,
      parentId
    });
  }

  // Helper methods for Rust-specific parsing

  private extractVisibility(node: Parser.SyntaxNode): string {
    const visibilityNode = node.children.find(c => c.type === 'visibility_modifier');
    if (!visibilityNode) return '';

    const visText = this.getNodeText(visibilityNode);
    if (visText === 'pub') return 'pub ';
    if (visText.startsWith('pub(')) return `${visText} `;
    return '';
  }

  private getPrecedingAttributes(node: Parser.SyntaxNode): Parser.SyntaxNode[] {
    const attributes: Parser.SyntaxNode[] = [];

    if (!node.parent) return attributes;

    // Find this node's index in its parent's children
    const siblings = node.parent.children;
    const nodeIndex = siblings.indexOf(node);

    // Look backwards for attribute_item nodes
    for (let i = nodeIndex - 1; i >= 0; i--) {
      const sibling = siblings[i];
      if (sibling.type === 'attribute_item') {
        attributes.unshift(sibling);
      } else {
        // Stop at the first non-attribute
        break;
      }
    }

    return attributes;
  }

  private extractDerivedTraits(attributes: Parser.SyntaxNode[]): string[] {
    const traits: string[] = [];

    for (const attr of attributes) {
      // Look for derive attribute
      const attributeNode = attr.children.find(c => c.type === 'attribute');
      if (!attributeNode) continue;

      const identifierNode = attributeNode.children.find(c => c.type === 'identifier');
      if (!identifierNode || this.getNodeText(identifierNode) !== 'derive') continue;

      // Find the token tree with the trait list
      const tokenTree = attributeNode.children.find(c => c.type === 'token_tree');
      if (!tokenTree) continue;

      // Extract identifiers from the token tree
      for (const child of tokenTree.children) {
        if (child.type === 'identifier') {
          traits.push(this.getNodeText(child));
        }
      }
    }

    return traits;
  }

  private extractDocFromAttribute(node: Parser.SyntaxNode): string | undefined {
    const attrText = this.getNodeText(node);
    const match = attrText.match(/#\[doc\s*=\s*"([^"]+)"\]/);
    return match ? match[1] : undefined;
  }

  private isInsideImpl(node: Parser.SyntaxNode): boolean {
    let parent = node.parent;
    while (parent) {
      if (parent.type === 'impl_item') return true;
      parent = parent.parent;
    }
    return false;
  }

  private hasAsyncKeyword(node: Parser.SyntaxNode): boolean {
    return node.children.some(c => c.type === 'async' || this.getNodeText(c) === 'async');
  }

  private hasUnsafeKeyword(node: Parser.SyntaxNode): boolean {
    return node.children.some(c => c.type === 'unsafe' || this.getNodeText(c) === 'unsafe');
  }

  private extractExternModifier(node: Parser.SyntaxNode): string | null {
    // Look for function_modifiers containing extern_modifier
    const functionModifiersNode = node.children.find(c => c.type === 'function_modifiers');
    if (functionModifiersNode) {
      const externModifierNode = functionModifiersNode.children.find(c => c.type === 'extern_modifier');
      if (externModifierNode) {
        return this.getNodeText(externModifierNode);
      }
    }
    return null;
  }

  private extractFunctionParameters(node: Parser.SyntaxNode): string[] {
    const parameters: string[] = [];
    const paramList = node.childForFieldName('parameters');

    if (!paramList) return parameters;

    for (const child of paramList.children) {
      if (child.type === 'parameter') {
        const paramText = this.getNodeText(child);
        parameters.push(paramText);
      } else if (child.type === 'self_parameter') {
        // Handle &self, &mut self, self, etc.
        const selfText = this.getNodeText(child);
        parameters.push(selfText);
      }
    }

    return parameters;
  }

  private extractReturnType(node: Parser.SyntaxNode): string | undefined {
    const returnTypeNode = node.childForFieldName('return_type');
    if (!returnTypeNode) return undefined;

    // Skip the -> token and get the actual type
    const typeNodes = returnTypeNode.children.filter(c => c.type !== '->' && c.text !== '->');
    if (typeNodes.length > 0) {
      // Special handling for reference types with lifetimes (& + lifetime + type pattern)
      if (typeNodes.length >= 3 &&
          typeNodes[0].type === '&' &&
          typeNodes[1].type === 'lifetime') {
        return this.extractReferenceTypeTokensWithSpacing(typeNodes);
      }
      return typeNodes.map(n => this.getNodeText(n)).join('');
    }

    return undefined;
  }

  private extractReferenceTypeTokensWithSpacing(typeNodes: Parser.SyntaxNode[]): string {
    // Handle & + lifetime + type pattern with proper spacing
    const parts: string[] = [];

    for (let i = 0; i < typeNodes.length; i++) {
      const node = typeNodes[i];

      if (node.type === '&') {
        parts.push('&');
      } else if (node.type === 'lifetime') {
        parts.push(this.getNodeText(node));
      } else if (i === 2) {
        // Add space before the type after lifetime
        parts.push(' ' + this.getNodeText(node));
      } else {
        parts.push(this.getNodeText(node));
      }
    }

    return parts.join('');
  }

  private extractReferenceTypeWithSpacing(node: Parser.SyntaxNode): string {
    // For reference types like &'static str, ensure proper spacing
    const parts: string[] = [];

    for (const child of node.children) {
      if (child.type === '&') {
        parts.push('&');
      } else if (child.type === 'lifetime') {
        parts.push(this.getNodeText(child));
      } else {
        // Add space before the type after lifetime
        parts.push(' ' + this.getNodeText(child));
      }
    }

    return parts.join('');
  }

  private inferTypeFromSignature(signature: string, kind: SymbolKind): string | null {
    // Extract return types from function signatures
    if (kind === SymbolKind.Function || kind === SymbolKind.Method) {
      const returnTypeMatch = signature.match(/-> (.+)$/);
      if (returnTypeMatch) {
        return returnTypeMatch[1].trim();
      }
    }

    // Extract types from const/static declarations
    if (kind === SymbolKind.Constant || kind === SymbolKind.Variable) {
      const typeMatch = signature.match(/:\s*([^=]+)\s*=/);
      if (typeMatch) {
        return typeMatch[1].trim();
      }
    }

    return null;
  }

  private getPreviousSibling(node: Parser.SyntaxNode): Parser.SyntaxNode | null {
    if (!node.parent) return null;

    const siblings = node.parent.children;
    const index = siblings.indexOf(node);
    return index > 0 ? siblings[index - 1] : null;
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];
    const symbolMap = new Map<string, Symbol>();

    // Build symbol lookup map
    symbols.forEach(symbol => {
      symbolMap.set(symbol.name, symbol);
    });

    // Extract relationships from the AST
    this.walkTreeForRelationships(tree.rootNode, symbolMap, relationships);

    return relationships;
  }

  private walkTreeForRelationships(
    node: Parser.SyntaxNode,
    symbolMap: Map<string, Symbol>,
    relationships: Relationship[]
  ) {
    // Handle trait implementations
    if (node.type === 'impl_item') {
      this.extractImplRelationships(node, symbolMap, relationships);
    }

    // Handle struct/enum field relationships
    if (node.type === 'struct_item' || node.type === 'enum_item') {
      this.extractTypeRelationships(node, symbolMap, relationships);
    }

    // Handle function call relationships
    if (node.type === 'call_expression') {
      this.extractCallRelationships(node, symbolMap, relationships);
    }

    // Recursively process children
    for (const child of node.children) {
      this.walkTreeForRelationships(child, symbolMap, relationships);
    }
  }

  private extractImplRelationships(
    node: Parser.SyntaxNode,
    symbolMap: Map<string, Symbol>,
    relationships: Relationship[]
  ) {
    // Look for "impl TraitName for TypeName" pattern
    const children = node.children;
    let traitName = '';
    let typeName = '';
    let foundFor = false;

    for (const child of children) {
      if (child.type === 'type_identifier') {
        if (!foundFor) {
          traitName = this.getNodeText(child);
        } else {
          typeName = this.getNodeText(child);
          break;
        }
      } else if (child.type === 'for') {
        foundFor = true;
      }
    }

    // If we found both trait and type, create implements relationship
    if (traitName && typeName) {
      const traitSymbol = symbolMap.get(traitName);
      const typeSymbol = symbolMap.get(typeName);

      if (traitSymbol && typeSymbol) {
        relationships.push({
          fromSymbolId: typeSymbol.id,
          toSymbolId: traitSymbol.id,
          kind: RelationshipKind.Implements,
          filePath: this.filePath,
          lineNumber: node.startPosition.row + 1,
          confidence: 0.95
        });
      }
    }
  }

  private extractTypeRelationships(
    node: Parser.SyntaxNode,
    symbolMap: Map<string, Symbol>,
    relationships: Relationship[]
  ) {
    const nameNode = node.childForFieldName('name');
    if (!nameNode) return;

    const typeName = this.getNodeText(nameNode);
    const typeSymbol = symbolMap.get(typeName);
    if (!typeSymbol) return;

    // Look for field types that reference other symbols
    const declarationList = node.children.find(c =>
      c.type === 'field_declaration_list' || c.type === 'enum_variant_list'
    );

    if (declarationList) {
      for (const field of declarationList.children) {
        if (field.type === 'field_declaration' || field.type === 'enum_variant') {
          this.extractFieldTypeReferences(field, typeSymbol, symbolMap, relationships);
        }
      }
    }
  }

  private extractFieldTypeReferences(
    fieldNode: Parser.SyntaxNode,
    containerSymbol: Symbol,
    symbolMap: Map<string, Symbol>,
    relationships: Relationship[]
  ) {
    // Find type references in field declarations
    for (const child of fieldNode.children) {
      if (child.type === 'type_identifier') {
        const referencedTypeName = this.getNodeText(child);
        const referencedSymbol = symbolMap.get(referencedTypeName);

        if (referencedSymbol && referencedSymbol.id !== containerSymbol.id) {
          relationships.push({
            fromSymbolId: containerSymbol.id,
            toSymbolId: referencedSymbol.id,
            kind: RelationshipKind.Uses,
            filePath: this.filePath,
            lineNumber: fieldNode.startPosition.row + 1,
            confidence: 0.8
          });
        }
      }
    }
  }

  private extractCallRelationships(
    node: Parser.SyntaxNode,
    symbolMap: Map<string, Symbol>,
    relationships: Relationship[]
  ) {
    // Extract function/method call relationships
    const functionNode = node.childForFieldName('function');
    if (!functionNode) return;

    // Handle method calls (receiver.method())
    if (functionNode.type === 'field_expression') {
      const methodNode = functionNode.childForFieldName('field');
      if (methodNode) {
        const methodName = this.getNodeText(methodNode);
        const calledSymbol = symbolMap.get(methodName);

        if (calledSymbol) {
          // Find the calling function context
          const callingFunction = this.findContainingFunction(node);
          if (callingFunction) {
            const callerSymbol = symbolMap.get(callingFunction);
            if (callerSymbol) {
              relationships.push({
                fromSymbolId: callerSymbol.id,
                toSymbolId: calledSymbol.id,
                kind: RelationshipKind.Calls,
                filePath: this.filePath,
                lineNumber: node.startPosition.row + 1,
                confidence: 0.9
              });
            }
          }
        }
      }
    }
    // Handle direct function calls
    else if (functionNode.type === 'identifier') {
      const functionName = this.getNodeText(functionNode);
      const calledSymbol = symbolMap.get(functionName);

      if (calledSymbol) {
        const callingFunction = this.findContainingFunction(node);
        if (callingFunction) {
          const callerSymbol = symbolMap.get(callingFunction);
          if (callerSymbol) {
            relationships.push({
              fromSymbolId: callerSymbol.id,
              toSymbolId: calledSymbol.id,
              kind: RelationshipKind.Calls,
              filePath: this.filePath,
              lineNumber: node.startPosition.row + 1,
              confidence: 0.9
            });
          }
        }
      }
    }
  }

  private findContainingFunction(node: Parser.SyntaxNode): string | null {
    let parent = node.parent;

    while (parent) {
      if (parent.type === 'function_item') {
        const nameNode = parent.childForFieldName('name');
        return nameNode ? this.getNodeText(nameNode) : null;
      }
      parent = parent.parent;
    }

    return null;
  }
}