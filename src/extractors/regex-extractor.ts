import { Parser } from 'web-tree-sitter';
import {
  BaseExtractor,
  Symbol,
  SymbolKind,
  Relationship,
  RelationshipKind
} from './base-extractor.js';

export class RegexExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    const visitNode = (node: Parser.SyntaxNode, parentId?: string) => {
      let symbol: Symbol | null = null;

      switch (node.type) {
        case 'pattern':
        case 'regex':
        case 'expression':
          // Extract regex patterns
          symbol = this.extractPattern(node, parentId);
          break;
        case 'character_class':
          symbol = this.extractCharacterClass(node, parentId);
          break;
        case 'group':
        case 'capturing_group':
        case 'non_capturing_group':
        case 'named_capturing_group':
          symbol = this.extractGroup(node, parentId);
          break;
        case 'quantifier':
        case 'quantified_expression':
          symbol = this.extractQuantifier(node, parentId);
          break;
        case 'anchor':
        case 'start_assertion':
        case 'end_assertion':
        case 'word_boundary_assertion':
          symbol = this.extractAnchor(node, parentId);
          break;
        case 'lookahead_assertion':
        case 'lookbehind_assertion':
        case 'positive_lookahead':
        case 'negative_lookahead':
        case 'positive_lookbehind':
        case 'negative_lookbehind':
          symbol = this.extractLookaround(node, parentId);
          break;
        case 'alternation':
        case 'disjunction':
          symbol = this.extractAlternation(node, parentId);
          break;
        case 'character_escape':
        case 'predefined_character_class':
          symbol = this.extractPredefinedClass(node, parentId);
          break;
        case 'unicode_property':
        case 'unicode_category':
          symbol = this.extractUnicodeProperty(node, parentId);
          break;
        case 'backreference':
          symbol = this.extractBackreference(node, parentId);
          break;
        case 'conditional':
          symbol = this.extractConditional(node, parentId);
          break;
        case 'atomic_group':
          symbol = this.extractAtomicGroup(node, parentId);
          break;
        case 'comment':
          symbol = this.extractComment(node, parentId);
          break;
        case 'literal':
        case 'character':
          symbol = this.extractLiteral(node, parentId);
          break;
      }

      // If no specific handler, try to extract as generic pattern
      if (!symbol && this.isRegexPattern(node)) {
        symbol = this.extractGenericPattern(node, parentId);
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

    // Also extract patterns from text content directly
    this.extractPatternsFromText(this.content, symbols);

    visitNode(tree.rootNode);
    return symbols;
  }

  private extractPattern(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const patternText = this.getNodeText(node);
    const signature = this.buildPatternSignature(patternText);
    const symbolKind = this.determinePatternKind(patternText);

    return this.createSymbol(node, patternText, symbolKind, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'regex-pattern',
        pattern: patternText,
        complexity: this.calculateComplexity(patternText)
      }
    });
  }

  private extractCharacterClass(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const classText = this.getNodeText(node);
    const signature = this.buildCharacterClassSignature(classText);

    return this.createSymbol(node, classText, SymbolKind.Class, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'character-class',
        pattern: classText,
        negated: classText.startsWith('[^')
      }
    });
  }

  private extractGroup(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const groupText = this.getNodeText(node);
    const signature = this.buildGroupSignature(groupText);

    return this.createSymbol(node, groupText, SymbolKind.Class, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'group',
        pattern: groupText,
        capturing: this.isCapturingGroup(groupText),
        named: this.extractGroupName(groupText)
      }
    });
  }

  private extractQuantifier(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const quantifierText = this.getNodeText(node);
    const signature = this.buildQuantifierSignature(quantifierText);

    return this.createSymbol(node, quantifierText, SymbolKind.Function, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'quantifier',
        pattern: quantifierText,
        lazy: quantifierText.includes('?'),
        possessive: quantifierText.includes('+')
      }
    });
  }

  private extractAnchor(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const anchorText = this.getNodeText(node);
    const signature = this.buildAnchorSignature(anchorText);

    return this.createSymbol(node, anchorText, SymbolKind.Constant, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'anchor',
        pattern: anchorText,
        position: this.getAnchorType(anchorText)
      }
    });
  }

  private extractLookaround(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const lookaroundText = this.getNodeText(node);
    const signature = this.buildLookaroundSignature(lookaroundText);

    return this.createSymbol(node, lookaroundText, SymbolKind.Method, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'lookaround',
        pattern: lookaroundText,
        direction: this.getLookaroundDirection(lookaroundText),
        positive: this.isPositiveLookaround(lookaroundText)
      }
    });
  }

  private extractAlternation(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const alternationText = this.getNodeText(node);
    const signature = this.buildAlternationSignature(alternationText);

    return this.createSymbol(node, alternationText, SymbolKind.Variable, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'alternation',
        pattern: alternationText,
        options: this.extractAlternationOptions(alternationText)
      }
    });
  }

  private extractPredefinedClass(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const classText = this.getNodeText(node);
    const signature = this.buildPredefinedClassSignature(classText);

    return this.createSymbol(node, classText, SymbolKind.Constant, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'predefined-class',
        pattern: classText,
        category: this.getPredefinedClassCategory(classText)
      }
    });
  }

  private extractUnicodeProperty(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const propertyText = this.getNodeText(node);
    const signature = this.buildUnicodePropertySignature(propertyText);

    return this.createSymbol(node, propertyText, SymbolKind.Constant, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'unicode-property',
        pattern: propertyText,
        property: this.extractUnicodePropertyName(propertyText)
      }
    });
  }

  private extractBackreference(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const backrefText = this.getNodeText(node);
    const signature = this.buildBackreferenceSignature(backrefText);

    return this.createSymbol(node, backrefText, SymbolKind.Variable, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'backreference',
        pattern: backrefText,
        groupNumber: this.extractGroupNumber(backrefText),
        groupName: this.extractBackrefGroupName(backrefText)
      }
    });
  }

  private extractConditional(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const conditionalText = this.getNodeText(node);
    const signature = this.buildConditionalSignature(conditionalText);

    return this.createSymbol(node, conditionalText, SymbolKind.Method, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'conditional',
        pattern: conditionalText,
        condition: this.extractCondition(conditionalText)
      }
    });
  }

  private extractAtomicGroup(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const atomicText = this.getNodeText(node);
    const signature = this.buildAtomicGroupSignature(atomicText);

    return this.createSymbol(node, atomicText, SymbolKind.Class, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'atomic-group',
        pattern: atomicText,
        possessive: true
      }
    });
  }

  private extractComment(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const commentText = this.getNodeText(node);
    const cleanComment = commentText.replace(/^\(\?\#|^\#/, '').replace(/\)$/, '').trim();

    return this.createSymbol(node, commentText, SymbolKind.Property, {
      signature: commentText,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'comment',
        content: cleanComment
      }
    });
  }

  private extractLiteral(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const literalText = this.getNodeText(node);
    const signature = this.buildLiteralSignature(literalText);

    return this.createSymbol(node, literalText, SymbolKind.Variable, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'literal',
        pattern: literalText,
        escaped: this.isEscapedLiteral(literalText)
      }
    });
  }

  private extractGenericPattern(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const patternText = this.getNodeText(node);
    const signature = this.buildGenericSignature(patternText);
    const symbolKind = this.determinePatternKind(patternText);

    return this.createSymbol(node, patternText, symbolKind, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'generic-pattern',
        pattern: patternText,
        nodeType: node.type
      }
    });
  }

  private extractPatternsFromText(text: string, symbols: Symbol[]): void {
    // Extract patterns from text content directly (for files without tree-sitter structure)
    const lines = text.split('\n');

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i].trim();

      // Skip comments and empty lines
      if (!line || line.startsWith('//') || line.startsWith('#')) {
        continue;
      }

      // Clean the line - remove comments and extra whitespace
      const cleanLine = this.cleanRegexLine(line);
      if (!cleanLine) continue;

      // Extract meaningful regex patterns
      if (this.isValidRegexPattern(cleanLine)) {
        const symbolKind = this.determinePatternKind(cleanLine);
        const signature = this.buildPatternSignature(cleanLine);

        // Create a mock node for text-based patterns
        const mockNode = {
          startPosition: { row: i, column: 0 },
          endPosition: { row: i, column: cleanLine.length },
          startIndex: 0,
          endIndex: cleanLine.length
        } as any;

        const symbol = this.createSymbol(
          mockNode,
          cleanLine,
          symbolKind,
          {
            signature,
            visibility: 'public',
            metadata: {
              type: 'text-pattern',
              pattern: cleanLine,
              lineNumber: i + 1,
              complexity: this.calculateComplexity(cleanLine)
            }
          }
        );
        symbols.push(symbol);
      }
    }
  }

  private isRegexPattern(node: Parser.SyntaxNode): boolean {
    const regexNodeTypes = [
      'pattern', 'regex', 'expression', 'character_class', 'group',
      'quantifier', 'anchor', 'lookahead', 'lookbehind', 'alternation',
      'character_escape', 'unicode_property', 'backreference', 'conditional'
    ];
    return regexNodeTypes.includes(node.type);
  }

  private cleanRegexLine(line: string): string {
    // Remove inline comments (// or #)
    let cleaned = line.replace(/\s+(?:\/\/|#).*$/, '').trim();

    // Remove excessive whitespace
    cleaned = cleaned.replace(/\s+/g, ' ').trim();

    return cleaned;
  }

  private isValidRegexPattern(text: string): boolean {
    // Skip very short patterns or obvious non-regex content
    if (text.length < 1) return false;

    // Allow simple literals (letters, numbers, basic words)
    if (/^[a-zA-Z0-9]+$/.test(text)) {
      return true;
    }

    // Allow single character regex metacharacters
    if (text === '.' || text === '^' || text === '$') {
      return true;
    }

    // Allow simple groups and common patterns
    if (/^\(.*\)$/.test(text) || /^.*\*$/.test(text) || /^\*\*$/.test(text)) {
      return true;
    }

    // Check for regex-specific characters or patterns
    const regexIndicators = [
      /[\[\](){}*+?^$|\\]/,  // Special regex characters
      /\\[dwsWDSnrtfve]/,    // Escape sequences
      /\(\?\<?[!=]/,         // Lookarounds
      /\(\?\w+\)/,           // Groups with modifiers
      /\\p\{/,               // Unicode properties
      /\[\^/,                // Negated character classes
      /\{[\d,]+\}/,          // Quantifiers
    ];

    return regexIndicators.some(pattern => pattern.test(text));
  }

  private determinePatternKind(pattern: string): SymbolKind {
    // Lookarounds (check first, before groups)
    if (pattern.match(/\(\?\<?[!=]/)) {
      return SymbolKind.Method;
    }

    // Character classes
    if (pattern.match(/^\[.*\]$/)) {
      return SymbolKind.Class;
    }

    // Groups (but not lookarounds)
    if (pattern.match(/^\(.*\)$/) && !pattern.match(/\(\?\<?[!=]/)) {
      return SymbolKind.Class;
    }

    // Quantifiers
    if (pattern.match(/[*+?]\??$/) || pattern.match(/\{[\d,]+\}\??$/)) {
      return SymbolKind.Function;
    }

    // Anchors and predefined classes
    if (pattern.match(/^[\^$]$/) || pattern.match(/^\\[bBdDwWsS]$/) || pattern === '.') {
      return SymbolKind.Constant;
    }

    // Unicode properties
    if (pattern.match(/\\[pP]\{/)) {
      return SymbolKind.Constant;
    }

    // Default to Variable for basic patterns
    return SymbolKind.Variable;
  }

  private buildPatternSignature(pattern: string): string {
    if (pattern.length <= 100) {
      return pattern;
    }
    return pattern.substring(0, 97) + '...';
  }

  private buildCharacterClassSignature(classText: string): string {
    return `Character class: ${classText}`;
  }

  private buildGroupSignature(groupText: string): string {
    const groupName = this.extractGroupName(groupText);
    if (groupName) {
      return `Named group '${groupName}': ${groupText}`;
    }
    return `Group: ${groupText}`;
  }

  private buildQuantifierSignature(quantifierText: string): string {
    return `Quantifier: ${quantifierText}`;
  }

  private buildAnchorSignature(anchorText: string): string {
    const anchorType = this.getAnchorType(anchorText);
    return `Anchor (${anchorType}): ${anchorText}`;
  }

  private buildLookaroundSignature(lookaroundText: string): string {
    const direction = this.getLookaroundDirection(lookaroundText);
    const polarity = this.isPositiveLookaround(lookaroundText) ? 'positive' : 'negative';
    return `${polarity} ${direction}: ${lookaroundText}`;
  }

  private buildAlternationSignature(alternationText: string): string {
    return `Alternation: ${alternationText}`;
  }

  private buildPredefinedClassSignature(classText: string): string {
    const category = this.getPredefinedClassCategory(classText);
    return `Predefined class (${category}): ${classText}`;
  }

  private buildUnicodePropertySignature(propertyText: string): string {
    const property = this.extractUnicodePropertyName(propertyText);
    return `Unicode property (${property}): ${propertyText}`;
  }

  private buildBackreferenceSignature(backrefText: string): string {
    const groupName = this.extractBackrefGroupName(backrefText);
    const groupNumber = this.extractGroupNumber(backrefText);

    if (groupName) {
      return `Named backreference to '${groupName}': ${backrefText}`;
    } else if (groupNumber) {
      return `Backreference to group ${groupNumber}: ${backrefText}`;
    }
    return `Backreference: ${backrefText}`;
  }

  private buildConditionalSignature(conditionalText: string): string {
    const condition = this.extractCondition(conditionalText);
    return `Conditional (${condition}): ${conditionalText}`;
  }

  private buildAtomicGroupSignature(atomicText: string): string {
    return `Atomic group: ${atomicText}`;
  }

  private buildLiteralSignature(literalText: string): string {
    return `Literal: ${literalText}`;
  }

  private buildGenericSignature(patternText: string): string {
    return patternText;
  }

  private isCapturingGroup(groupText: string): boolean {
    return !groupText.startsWith('(?:') && !groupText.startsWith('(?<') && !groupText.startsWith('(?P<');
  }

  private extractGroupName(groupText: string): string | null {
    const namedMatch = groupText.match(/\(\?<(\w+)>/) || groupText.match(/\(\?P<(\w+)>/);
    return namedMatch ? namedMatch[1] : null;
  }

  private getAnchorType(anchorText: string): string {
    const anchorTypes: Record<string, string> = {
      '^': 'start',
      '$': 'end',
      '\\b': 'word-boundary',
      '\\B': 'non-word-boundary',
      '\\A': 'string-start',
      '\\Z': 'string-end',
      '\\z': 'absolute-end'
    };
    return anchorTypes[anchorText] || 'unknown';
  }

  private getLookaroundDirection(lookaroundText: string): string {
    if (lookaroundText.includes('(?<=') || lookaroundText.includes('(?<!')) {
      return 'lookbehind';
    }
    return 'lookahead';
  }

  private isPositiveLookaround(lookaroundText: string): boolean {
    return lookaroundText.includes('(?=') || lookaroundText.includes('(?<=');
  }

  private extractAlternationOptions(alternationText: string): string[] {
    return alternationText.split('|').map(opt => opt.trim());
  }

  private getPredefinedClassCategory(classText: string): string {
    const categories: Record<string, string> = {
      '\\d': 'digit',
      '\\D': 'non-digit',
      '\\w': 'word',
      '\\W': 'non-word',
      '\\s': 'whitespace',
      '\\S': 'non-whitespace',
      '.': 'any-character',
      '\\n': 'newline',
      '\\r': 'carriage-return',
      '\\t': 'tab',
      '\\v': 'vertical-tab',
      '\\f': 'form-feed',
      '\\a': 'bell',
      '\\e': 'escape'
    };
    return categories[classText] || 'other';
  }

  private extractUnicodePropertyName(propertyText: string): string {
    const match = propertyText.match(/\\[pP]\{([^}]+)\}/);
    return match ? match[1] : 'unknown';
  }

  private extractGroupNumber(backrefText: string): string | null {
    const numberMatch = backrefText.match(/\\(\d+)/);
    return numberMatch ? numberMatch[1] : null;
  }

  private extractBackrefGroupName(backrefText: string): string | null {
    const namedMatch = backrefText.match(/\\k<(\w+)>/) || backrefText.match(/\(\?P=(\w+)\)/);
    return namedMatch ? namedMatch[1] : null;
  }

  private extractCondition(conditionalText: string): string {
    const condMatch = conditionalText.match(/\(\?\(([^)]+)\)/);
    return condMatch ? condMatch[1] : 'unknown';
  }

  private isEscapedLiteral(literalText: string): boolean {
    return literalText.startsWith('\\');
  }

  private calculateComplexity(pattern: string): number {
    let complexity = 0;

    // Basic complexity indicators
    complexity += (pattern.match(/[*+?]/g) || []).length; // Quantifiers
    complexity += (pattern.match(/[\[\](){}]/g) || []).length; // Grouping constructs
    complexity += (pattern.match(/\(\?\<?[!=]/g) || []).length * 2; // Lookarounds
    complexity += (pattern.match(/\\[pP]\{/g) || []).length; // Unicode properties
    complexity += (pattern.match(/\|/g) || []).length; // Alternations

    return complexity;
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];

    const visitNode = (node: Parser.SyntaxNode) => {
      switch (node.type) {
        case 'backreference':
          this.extractBackreferenceRelationships(node, symbols, relationships);
          break;
        case 'named_capturing_group':
          this.extractNamedGroupRelationships(node, symbols, relationships);
          break;
        case 'group':
          this.extractGroupRelationships(node, symbols, relationships);
          break;
      }

      for (const child of node.children) {
        visitNode(child);
      }
    };

    visitNode(tree.rootNode);
    return relationships;
  }

  private extractBackreferenceRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const backrefText = this.getNodeText(node);
    const groupNumber = this.extractGroupNumber(backrefText);
    const groupName = this.extractBackrefGroupName(backrefText);

    const backrefSymbol = this.findBackreferenceSymbol(node, symbols);
    if (backrefSymbol) {
      let targetSymbol: Symbol | null = null;

      if (groupName) {
        targetSymbol = symbols.find(s =>
          s.metadata?.groupName === groupName ||
          s.name?.includes(`(?<${groupName}>`)
        ) || null;
      } else if (groupNumber) {
        targetSymbol = symbols.find(s =>
          s.metadata?.groupNumber === groupNumber ||
          s.metadata?.type === 'group'
        ) || null;
      }

      if (targetSymbol) {
        relationships.push({
          fromSymbolId: backrefSymbol.id,
          toSymbolId: targetSymbol.id,
          kind: RelationshipKind.References,
          filePath: this.filePath,
          lineNumber: node.startPosition.row + 1,
          confidence: 0.95,
          metadata: {
            type: 'backreference',
            groupNumber,
            groupName
          }
        });
      }
    }
  }

  private extractNamedGroupRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const groupText = this.getNodeText(node);
    const groupName = this.extractGroupName(groupText);

    if (groupName) {
      const groupSymbol = this.findGroupSymbol(node, symbols);
      if (groupSymbol) {
        // Find backreferences to this named group
        const backreferences = symbols.filter(s =>
          s.metadata?.type === 'backreference' &&
          s.metadata?.groupName === groupName
        );

        for (const backref of backreferences) {
          relationships.push({
            fromSymbolId: backref.id,
            toSymbolId: groupSymbol.id,
            kind: RelationshipKind.References,
            filePath: this.filePath,
            lineNumber: groupSymbol.lineNumber,
            confidence: 1.0,
            metadata: {
              type: 'named-group-reference',
              groupName
            }
          });
        }
      }
    }
  }

  private extractGroupRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    // Extract relationships between groups and their contents
    const groupSymbol = this.findGroupSymbol(node, symbols);
    if (groupSymbol) {
      // Find child patterns within this group
      const childSymbols = symbols.filter(s => s.parentId === groupSymbol.id);

      for (const child of childSymbols) {
        relationships.push({
          fromSymbolId: groupSymbol.id,
          toSymbolId: child.id,
          kind: RelationshipKind.Contains,
          filePath: this.filePath,
          lineNumber: child.lineNumber,
          confidence: 1.0,
          metadata: { type: 'group-contains' }
        });
      }
    }
  }

  private findBackreferenceSymbol(node: Parser.SyntaxNode, symbols: Symbol[]): Symbol | null {
    const backrefText = this.getNodeText(node);
    return symbols.find(s =>
      s.name === backrefText &&
      s.metadata?.type === 'backreference'
    ) || null;
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();
    for (const symbol of symbols) {
      if (symbol.metadata?.type) {
        types.set(symbol.id, `regex:${symbol.metadata.type}`);
      } else if (symbol.kind === SymbolKind.Variable) {
        types.set(symbol.id, 'regex:pattern');
      }
    }
    return types;
  }

  private findGroupSymbol(node: Parser.SyntaxNode, symbols: Symbol[]): Symbol | null {
    const groupText = this.getNodeText(node);
    return symbols.find(s =>
      s.name === groupText &&
      s.metadata?.type === 'group'
    ) || null;
  }
}