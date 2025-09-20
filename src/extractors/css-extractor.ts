import { Parser } from 'web-tree-sitter';
import {
  BaseExtractor,
  Symbol,
  SymbolKind,
  Relationship,
  RelationshipKind
} from './base-extractor.js';

export class CSSExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    const visitNode = (node: Parser.SyntaxNode, parentId?: string) => {
      let symbol: Symbol | null = null;

      switch (node.type) {
        case 'rule_set':
          symbol = this.extractRule(node, parentId);
          break;
        case 'at_rule':
        case 'import_statement':
        case 'charset_statement':
        case 'namespace_statement':
          symbol = this.extractAtRule(node, parentId);
          break;
        case 'keyframes_statement':
          symbol = this.extractKeyframesRule(node, parentId);
          // Also extract individual keyframes
          this.extractKeyframes(node, symbols, parentId);
          break;
        case 'keyframe_block_list':
          // Handle keyframes content
          this.extractKeyframes(node, symbols, parentId);
          break;
        case 'media_statement':
          symbol = this.extractMediaRule(node, parentId);
          break;
        case 'supports_statement':
          symbol = this.extractSupportsRule(node, parentId);
          break;
        case 'property_name':
          // CSS custom properties (variables)
          if (this.getNodeText(node).startsWith('--')) {
            symbol = this.extractCustomProperty(node, parentId);
          }
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
    return symbols;
  }

  private extractRule(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const selectorsNode = node.children.find(c => c.type === 'selectors');
    const declarationBlock = node.children.find(c => c.type === 'block');

    const selectorText = selectorsNode ? this.getNodeText(selectorsNode) : 'unknown';
    const signature = this.buildRuleSignature(node, selectorText);

    return this.createSymbol(node, selectorText, SymbolKind.Variable, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'css-rule',
        selector: selectorText,
        properties: this.extractProperties(declarationBlock)
      }
    });
  }

  private extractAtRule(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const ruleName = this.extractAtRuleName(node);
    const signature = this.getNodeText(node);

    // Determine symbol kind based on at-rule type
    let symbolKind = SymbolKind.Variable;
    if (ruleName === '@keyframes') {
      symbolKind = SymbolKind.Function; // Animations as functions
    } else if (ruleName === '@import') {
      symbolKind = SymbolKind.Import;
    }

    return this.createSymbol(node, ruleName, symbolKind, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'at-rule',
        ruleName,
        atRuleType: ruleName.substring(1) // Remove @
      }
    });
  }

  private extractMediaRule(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const mediaQuery = this.extractMediaQuery(node);
    const signature = this.getNodeText(node);

    return this.createSymbol(node, mediaQuery, SymbolKind.Variable, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'media-rule',
        query: mediaQuery
      }
    });
  }

  private extractKeyframesRule(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const keyframesName = this.extractKeyframesName(node);
    const signature = this.getNodeText(node);

    return this.createSymbol(node, `@keyframes ${keyframesName}`, SymbolKind.Function, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'keyframes',
        animationName: keyframesName
      }
    });
  }

  private extractSupportsRule(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const condition = this.extractSupportsCondition(node);
    const signature = this.getNodeText(node);

    return this.createSymbol(node, condition, SymbolKind.Variable, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'supports-rule',
        condition
      }
    });
  }

  private extractKeyframes(node: Parser.SyntaxNode, symbols: Symbol[], parentId?: string) {
    // Extract individual keyframe percentages
    for (const child of node.children) {
      if (child.type === 'keyframe_block') {
        const keyframeSelector = child.children.find(c => c.type === 'from' || c.type === 'to' || c.type === 'percentage');
        if (keyframeSelector) {
          const selectorText = this.getNodeText(keyframeSelector);
          const signature = this.getNodeText(child);

          const symbol = this.createSymbol(child, selectorText, SymbolKind.Variable, {
            signature,
            visibility: 'public',
            parentId,
            metadata: {
              type: 'keyframe',
              selector: selectorText
            }
          });

          symbols.push(symbol);
        }
      }
    }
  }

  private extractCustomProperty(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const propertyName = this.getNodeText(node);
    const valueNode = this.findPropertyValue(node);
    const value = valueNode ? this.getNodeText(valueNode) : '';

    const signature = `${propertyName}: ${value}`;

    return this.createSymbol(node, propertyName, SymbolKind.Property, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'custom-property',
        property: propertyName,
        value: value
      }
    });
  }

  private buildRuleSignature(node: Parser.SyntaxNode, selector: string): string {
    const declarationBlock = node.children.find(c => c.type === 'block');
    if (!declarationBlock) return selector;

    const properties = this.extractKeyProperties(declarationBlock, selector);
    const keyProps = properties.slice(0, 20); // More space for :root with many CSS variables

    if (keyProps.length > 0) {
      return `${selector} { ${keyProps.join('; ')} }`;
    }

    return selector;
  }

  private extractProperties(declarationBlock: Parser.SyntaxNode | undefined): string[] {
    if (!declarationBlock) return [];

    const properties: string[] = [];
    for (const child of declarationBlock.children) {
      if (child.type === 'declaration') {
        const prop = this.getNodeText(child);
        properties.push(prop);
      }
    }

    return properties;
  }

  private extractKeyProperties(declarationBlock: Parser.SyntaxNode, selector?: string): string[] {
    if (!declarationBlock) return [];

    const keyProperties: string[] = [];
    const importantProperties = [
      'display', 'position', 'background', 'color', 'font-family', 'font-weight',
      'grid-template', 'grid-area', 'flex', 'margin', 'padding', 'width', 'height',
      'transform', 'text-decoration', 'box-shadow', 'border', 'backdrop-filter',
      'linear-gradient', 'max-width', 'text-align', 'cursor', 'opacity', 'content'
    ];

    // Collect all properties first
    const allProps: string[] = [];
    const customProps: string[] = [];
    const importantProps: string[] = [];
    const uniqueProps: string[] = [];

    for (const child of declarationBlock.children) {
      if (child.type === 'declaration') {
        const propText = this.getNodeText(child).trim();

        // Remove any trailing semicolon if present
        const cleanProp = propText.endsWith(';') ? propText.slice(0, -1) : propText;
        allProps.push(cleanProp);

        // Categorize properties
        if (cleanProp.startsWith('--')) {
          // CSS custom properties (variables)
          customProps.push(cleanProp);
        } else if (importantProperties.some(prop => cleanProp.startsWith(prop))) {
          importantProps.push(cleanProp);
        } else if (this.isUniqueProperty(cleanProp)) {
          // Properties that are unique/interesting (calc, attr, var, etc.)
          uniqueProps.push(cleanProp);
        }
      }
    }

    // Special handling for :root selector - include all CSS custom properties
    if (selector === ':root' && customProps.length > 0) {
      keyProperties.push(...customProps); // Include ALL CSS variables for :root
      keyProperties.push(...importantProps.slice(0, 3));
      keyProperties.push(...uniqueProps.slice(0, 2));
    } else {
      // Build final list with priorities:
      // 1. CSS custom properties (variables)
      // 2. Important properties
      // 3. Unique/interesting properties
      // 4. Other properties

      keyProperties.push(...customProps.slice(0, 12)); // More space for CSS variables
      keyProperties.push(...importantProps.slice(0, 5));
      keyProperties.push(...uniqueProps.slice(0, 3));

      // Fill remaining space with other properties
      for (const prop of allProps) {
        if (!keyProperties.includes(prop) && keyProperties.length < 12) {
          keyProperties.push(prop);
        }
      }
    }

    return keyProperties;
  }

  private isUniqueProperty(property: string): boolean {
    // Properties with unique/interesting values
    return property.includes('calc(') ||
           property.includes('var(') ||
           property.includes('attr(') ||
           property.includes('url(') ||
           property.includes('linear-gradient') ||
           property.includes('radial-gradient') ||
           property.includes('rgba(') ||
           property.includes('hsla(') ||
           property.includes('repeat(') ||
           property.includes('minmax(') ||
           property.includes('clamp(') ||
           property.includes('min(') ||
           property.includes('max(') ||
           property.startsWith('grid-') ||
           property.startsWith('flex-') ||
           property.includes('transform') ||
           property.includes('animation') ||
           property.includes('transition');
  }

  private extractAtRuleName(node: Parser.SyntaxNode): string {
    // Look for at-keyword or first child that starts with @
    for (const child of node.children) {
      if (child.type === 'at_keyword') {
        return this.getNodeText(child);
      }
      const text = this.getNodeText(child);
      if (text.startsWith('@')) {
        return text.split(/\s+/)[0]; // Get just the @rule part
      }
    }

    return '@unknown';
  }

  private extractMediaQuery(node: Parser.SyntaxNode): string {
    // Look for the media query part
    const mediaKeyword = node.children.find(c => this.getNodeText(c) === '@media');
    if (mediaKeyword) {
      const mediaIndex = node.children.indexOf(mediaKeyword);
      const queryParts: string[] = [];

      // Get the query parts after @media
      for (let i = mediaIndex + 1; i < node.children.length; i++) {
        const child = node.children[i];
        if (child.type === 'block') break; // Stop at the rule block
        queryParts.push(this.getNodeText(child).trim());
      }

      return `@media ${queryParts.join(' ').trim()}`;
    }

    return '@media';
  }

  private findPropertyValue(propertyNode: Parser.SyntaxNode): Parser.SyntaxNode | null {
    // Look for sibling value node
    const parent = propertyNode.parent;
    if (parent && parent.type === 'declaration') {
      return parent.children.find(c => c.type === 'property_value' || c.type === 'integer_value' || c.type === 'plain_value') || null;
    }

    return null;
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];

    const visitNode = (node: Parser.SyntaxNode) => {
      switch (node.type) {
        case 'call_expression':
          this.extractCSSFunctionRelationships(node, symbols, relationships);
          break;
        case 'import_statement':
          this.extractImportRelationships(node, symbols, relationships);
          break;
        case 'property_value':
          this.extractPropertyValueRelationships(node, symbols, relationships);
          break;
      }

      for (const child of node.children) {
        visitNode(child);
      }
    };

    visitNode(tree.rootNode);
    return relationships;
  }

  private extractCSSFunctionRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    // Extract relationships for CSS functions like calc(), var(), etc.
    const functionName = this.getFieldText(node, 'function');
    if (functionName) {
      // Find symbols that use this function
      const containingSymbol = this.findContainingSymbol(node, symbols);
      if (containingSymbol) {
        relationships.push({
          fromSymbolId: containingSymbol.id,
          toSymbolId: `css-function:${functionName}`,
          kind: RelationshipKind.Uses,
          filePath: this.filePath,
          lineNumber: node.startPosition.row + 1,
          confidence: 0.9,
          metadata: { functionName }
        });
      }
    }
  }

  private extractImportRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    // Extract CSS @import relationships
    const importPath = this.extractImportPath(node);
    if (importPath) {
      relationships.push({
        fromSymbolId: `file:${this.filePath}`,
        toSymbolId: `module:${importPath}`,
        kind: RelationshipKind.Imports,
        filePath: this.filePath,
        lineNumber: node.startPosition.row + 1,
        confidence: 1.0,
        metadata: { importPath }
      });
    }
  }

  private extractPropertyValueRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    // Extract relationships for CSS custom property usage: var(--property-name)
    const text = this.getNodeText(node);
    const varMatch = text.match(/var\(--([^)]+)\)/g);

    if (varMatch) {
      const containingSymbol = this.findContainingSymbol(node, symbols);
      if (containingSymbol) {
        for (const varUsage of varMatch) {
          const propName = varUsage.match(/var\(--(.*?)\)/)?.[1];
          if (propName) {
            const customProperty = symbols.find(s => s.name === `--${propName}`);
            if (customProperty) {
              relationships.push(this.createRelationship(
                containingSymbol.id,
                customProperty.id,
                RelationshipKind.Uses,
                node
              ));
            }
          }
        }
      }
    }
  }

  private extractImportPath(node: Parser.SyntaxNode): string | null {
    // Extract path from @import statement
    const text = this.getNodeText(node);
    const urlMatch = text.match(/url\(['"]?([^'"]+)['"]?\)/);
    if (urlMatch) {
      return urlMatch[1];
    }

    const quotedMatch = text.match(/['"]([^'"]+)['"]/);
    if (quotedMatch) {
      return quotedMatch[1];
    }

    return null;
  }

  private extractKeyframesName(node: Parser.SyntaxNode): string {
    // Look for the animation name after @keyframes
    const text = this.getNodeText(node);
    const match = text.match(/@keyframes\s+([^\s{]+)/);
    return match ? match[1] : 'unknown';
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();
    for (const symbol of symbols) {
      if (symbol.metadata?.type) {
        types.set(symbol.id, symbol.metadata.type);
      } else if (symbol.kind === SymbolKind.Class) {
        types.set(symbol.id, 'css:selector');
      } else if (symbol.kind === SymbolKind.Property) {
        types.set(symbol.id, 'css:property');
      } else if (symbol.kind === SymbolKind.Variable) {
        types.set(symbol.id, 'css:variable');
      }
    }
    return types;
  }

  private extractSupportsCondition(node: Parser.SyntaxNode): string {
    // Extract the condition from @supports rule
    const text = this.getNodeText(node);
    const match = text.match(/@supports\s+([^{]+)/);
    return match ? `@supports ${match[1].trim()}` : '@supports';
  }
}