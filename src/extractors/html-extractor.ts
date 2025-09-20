import { Parser } from 'web-tree-sitter';
import {
  BaseExtractor,
  Symbol,
  SymbolKind,
  Relationship,
  RelationshipKind
} from './base-extractor.js';

export class HTMLExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];
    const visitNode = (node: Parser.SyntaxNode, parentId?: string) => {
      if (!node || !node.type) {
        return; // Skip invalid nodes
      }

      let symbol: Symbol | null = null;

      switch (node.type) {
        case 'element':
          symbol = this.extractElement(node, parentId);
          break;
        case 'script_element':
          symbol = this.extractScriptElement(node, parentId);
          break;
        case 'style_element':
          symbol = this.extractStyleElement(node, parentId);
          break;
        case 'doctype':
          symbol = this.extractDoctype(node, parentId);
          break;
        case 'comment':
          symbol = this.extractComment(node, parentId);
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
      // If parsing fails completely, return empty symbols array
      console.warn('HTML parsing failed:', error);
    }
    return symbols;
  }

  private extractElement(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const tagName = this.extractTagName(node);
    const attributes = this.extractAttributes(node);
    const textContent = this.extractElementTextContent(node);
    const signature = this.buildElementSignature(tagName, attributes, textContent);

    // Determine symbol kind based on element type
    const symbolKind = this.getSymbolKindForElement(tagName, attributes);

    return this.createSymbol(node, tagName, symbolKind, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'html-element',
        tagName,
        attributes,
        textContent,
        isVoid: this.isVoidElement(tagName),
        isSemantic: this.isSemanticElement(tagName)
      }
    });
  }

  private extractScriptElement(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const attributes = this.extractAttributes(node);
    const content = this.extractTextContent(node);
    const signature = this.buildElementSignature('script', attributes, content);

    // Determine symbol kind based on src attribute
    const symbolKind = attributes.src ? SymbolKind.Import : SymbolKind.Variable;

    return this.createSymbol(node, 'script', symbolKind, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'script-element',
        attributes,
        content: content ? content.substring(0, 100) + '...' : null,
        isInline: !attributes.src,
        scriptType: attributes.type || 'text/javascript'
      }
    });
  }

  private extractStyleElement(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const attributes = this.extractAttributes(node);
    const content = this.extractTextContent(node);
    const signature = this.buildElementSignature('style', attributes, content);

    return this.createSymbol(node, 'style', SymbolKind.Variable, {
      signature,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'style-element',
        attributes,
        content: content ? content.substring(0, 100) + '...' : null,
        isInline: true
      }
    });
  }

  private extractDoctype(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const doctypeText = this.getNodeText(node);

    return this.createSymbol(node, 'DOCTYPE', SymbolKind.Variable, {
      signature: doctypeText,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'doctype',
        declaration: doctypeText
      }
    });
  }

  private extractComment(node: Parser.SyntaxNode, parentId?: string): Symbol {
    const commentText = this.getNodeText(node);
    const cleanComment = commentText.replace(/<!--\s*|\s*-->/g, '').trim();

    // Only extract meaningful comments (not empty or very short)
    if (cleanComment.length < 3) return null;

    return this.createSymbol(node, 'comment', SymbolKind.Property, {
      signature: `<!-- ${cleanComment} -->`,
      visibility: 'public',
      parentId,
      metadata: {
        type: 'comment',
        content: cleanComment
      }
    });
  }

  private extractTagName(node: Parser.SyntaxNode): string {
    // Look for start_tag child and extract tag name
    const startTag = node.children.find(c => c.type === 'start_tag');
    if (startTag) {
      const tagNameNode = startTag.children.find(c => c.type === 'tag_name');
      if (tagNameNode) {
        return this.getNodeText(tagNameNode);
      }
    }

    // Fallback: look for any tag_name child
    const tagNameNode = node.children.find(c => c.type === 'tag_name');
    if (tagNameNode) {
      return this.getNodeText(tagNameNode);
    }

    return 'unknown';
  }

  private extractAttributes(node: Parser.SyntaxNode): Record<string, string> {
    const attributes: Record<string, string> = {};

    const startTag = node.children.find(c => c.type === 'start_tag');
    const attributesContainer = startTag || node;

    for (const child of attributesContainer.children) {
      if (child.type === 'attribute') {
        const attrName = this.extractAttributeName(child);
        const attrValue = this.extractAttributeValue(child);
        if (attrName) {
          // For boolean attributes without values, store empty string
          attributes[attrName] = attrValue !== null ? attrValue : '';
        }
      }
    }

    return attributes;
  }

  private extractAttributeName(attrNode: Parser.SyntaxNode): string | null {
    const nameNode = attrNode.children.find(c => c.type === 'attribute_name');
    return nameNode ? this.getNodeText(nameNode) : null;
  }

  private extractAttributeValue(attrNode: Parser.SyntaxNode): string | null {
    const valueNode = attrNode.children.find(c =>
      c.type === 'attribute_value' ||
      c.type === 'quoted_attribute_value'
    );

    if (valueNode) {
      const text = this.getNodeText(valueNode);
      // Remove quotes if present
      return text.replace(/^["']|["']$/g, '');
    }

    return null;
  }

  private extractTextContent(node: Parser.SyntaxNode): string | null {
    // Extract text content from script or style elements
    const textNode = node.children.find(c => c.type === 'text' || c.type === 'raw_text');
    return textNode ? this.getNodeText(textNode).trim() : null;
  }

  private buildElementSignature(tagName: string, attributes: Record<string, string>, textContent?: string): string {
    let signature = `<${tagName}`;

    // Include important attributes in signature
    const importantAttrs = this.getImportantAttributes(tagName, attributes);
    for (const [name, value] of importantAttrs) {
      if (value) {
        signature += ` ${name}="${value}"`;
      } else {
        // Boolean attributes like 'novalidate', 'disabled', etc.
        signature += ` ${name}`;
      }
    }

    signature += '>';

    // Include text content for certain elements
    if (textContent && this.shouldIncludeTextContent(tagName)) {
      const truncatedContent = textContent.length > 100 ? textContent.substring(0, 100) + '...' : textContent;
      signature += truncatedContent;
    }

    return signature;
  }

  private getImportantAttributes(tagName: string, attributes: Record<string, string>): [string, string][] {
    const important: [string, string][] = [];
    const priorityAttrs = this.getPriorityAttributesForTag(tagName);

    // Add priority attributes first
    for (const attrName of priorityAttrs) {
      if (attrName in attributes) {
        important.push([attrName, attributes[attrName]]);
      }
    }

    // Add other interesting attributes
    for (const [name, value] of Object.entries(attributes)) {
      if (!priorityAttrs.includes(name) && this.isInterestingAttribute(name) && important.length < 8) {
        important.push([name, value]);
      }
    }

    return important;
  }

  private getPriorityAttributesForTag(tagName: string): string[] {
    const commonPriority = ['id', 'class', 'role'];

    const tagSpecific: Record<string, string[]> = {
      'html': ['lang', 'dir', 'data-theme'],
      'meta': ['name', 'property', 'content', 'charset'],
      'link': ['rel', 'href', 'type', 'as'],
      'script': ['src', 'type', 'async', 'defer'],
      'img': ['src', 'alt', 'width', 'height'],
      'a': ['href', 'target', 'rel'],
      'form': ['action', 'method', 'enctype', 'novalidate'],
      'input': ['type', 'name', 'value', 'placeholder', 'required', 'disabled', 'autocomplete', 'pattern', 'min', 'max', 'step', 'accept'],
      'select': ['name', 'id', 'multiple', 'required', 'disabled'],
      'textarea': ['name', 'placeholder', 'required', 'disabled', 'maxlength', 'minlength', 'rows', 'cols'],
      'time': ['datetime'],
      'details': ['open'],
      'button': ['type', 'data-action', 'disabled'],
      'iframe': ['src', 'title', 'width', 'height'],
      'video': ['src', 'controls', 'autoplay'],
      'audio': ['src', 'controls'],
      'body': ['class', 'data-theme'],
      'div': ['class', 'role'],
      'span': ['class', 'role'],
    };

    return [...commonPriority, ...(tagSpecific[tagName] || [])];
  }

  private isInterestingAttribute(name: string): boolean {
    return name.startsWith('data-') ||
           name.startsWith('aria-') ||
           name.startsWith('on') ||
           ['title', 'alt', 'placeholder', 'value', 'href', 'src', 'target', 'rel', 'multiple', 'required', 'disabled', 'readonly', 'checked', 'selected', 'autocomplete', 'datetime', 'pattern', 'maxlength', 'minlength', 'rows', 'cols', 'accept', 'open', 'class', 'role', 'novalidate'].includes(name);
  }

  private getSymbolKindForElement(tagName: string, attributes: Record<string, string>): SymbolKind {
    // Meta elements are properties
    if (tagName === 'meta') {
      return SymbolKind.Property;
    }

    // Link elements with stylesheet are imports
    if (tagName === 'link' && attributes.rel === 'stylesheet') {
      return SymbolKind.Import;
    }

    // Script and style elements are handled by specific extractors
    // so they shouldn't reach this method
    if (tagName === 'script' || tagName === 'style') {
      return SymbolKind.Variable; // Fallback, shouldn't be used
    }

    // Form input elements are fields
    if (this.isFormField(tagName)) {
      return SymbolKind.Field;
    }

    // All other HTML elements are classes
    return SymbolKind.Class;
  }

  private isVoidElement(tagName: string): boolean {
    const voidElements = [
      'area', 'base', 'br', 'col', 'embed', 'hr', 'img', 'input',
      'link', 'meta', 'param', 'source', 'track', 'wbr'
    ];
    return voidElements.includes(tagName);
  }

  private isSemanticElement(tagName: string): boolean {
    const semanticElements = [
      'article', 'aside', 'details', 'figcaption', 'figure', 'footer',
      'header', 'main', 'nav', 'section', 'summary', 'time'
    ];
    return semanticElements.includes(tagName);
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];

    const visitNode = (node: Parser.SyntaxNode) => {
      switch (node.type) {
        case 'element':
          this.extractElementRelationships(node, symbols, relationships);
          break;
        case 'script_element':
          this.extractScriptRelationships(node, symbols, relationships);
          break;
      }

      for (const child of node.children) {
        visitNode(child);
      }
    };

    visitNode(tree.rootNode);
    return relationships;
  }

  private extractElementRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const attributes = this.extractAttributes(node);

    // Extract href relationships (links)
    if (attributes.href) {
      const element = this.findElementSymbol(node, symbols);
      if (element) {
        relationships.push({
          fromSymbolId: element.id,
          toSymbolId: `url:${attributes.href}`,
          kind: RelationshipKind.References,
          filePath: this.filePath,
          lineNumber: node.startPosition.row + 1,
          confidence: 1.0,
          metadata: { href: attributes.href }
        });
      }
    }

    // Extract src relationships (images, scripts, etc.)
    if (attributes.src) {
      const element = this.findElementSymbol(node, symbols);
      if (element) {
        relationships.push({
          fromSymbolId: element.id,
          toSymbolId: `resource:${attributes.src}`,
          kind: RelationshipKind.Uses,
          filePath: this.filePath,
          lineNumber: node.startPosition.row + 1,
          confidence: 1.0,
          metadata: { src: attributes.src }
        });
      }
    }

    // Extract form relationships
    if (attributes.action) {
      const element = this.findElementSymbol(node, symbols);
      if (element) {
        relationships.push({
          fromSymbolId: element.id,
          toSymbolId: `endpoint:${attributes.action}`,
          kind: RelationshipKind.Calls,
          filePath: this.filePath,
          lineNumber: node.startPosition.row + 1,
          confidence: 1.0,
          metadata: { action: attributes.action, method: attributes.method || 'GET' }
        });
      }
    }
  }

  private extractScriptRelationships(
    node: Parser.SyntaxNode,
    symbols: Symbol[],
    relationships: Relationship[]
  ) {
    const attributes = this.extractAttributes(node);

    if (attributes.src) {
      const scriptSymbol = symbols.find(s => s.metadata?.type === 'script-element');
      if (scriptSymbol) {
        relationships.push({
          fromSymbolId: scriptSymbol.id,
          toSymbolId: `script:${attributes.src}`,
          kind: RelationshipKind.Imports,
          filePath: this.filePath,
          lineNumber: node.startPosition.row + 1,
          confidence: 1.0,
          metadata: { src: attributes.src, type: attributes.type }
        });
      }
    }
  }

  private findElementSymbol(node: Parser.SyntaxNode, symbols: Symbol[]): Symbol | null {
    // Find the symbol that corresponds to this element node
    const tagName = this.extractTagName(node);
    return symbols.find(s =>
      s.name === tagName &&
      s.filePath === this.filePath &&
      Math.abs(s.lineNumber - (node.startPosition.row + 1)) < 2
    ) || null;
  }

  private extractElementTextContent(node: Parser.SyntaxNode): string | null {
    // Extract text content from HTML elements
    for (const child of node.children) {
      if (child.type === 'text') {
        const text = this.getNodeText(child).trim();
        return text || null;
      }
    }
    return null;
  }

  private shouldIncludeTextContent(tagName: string): boolean {
    // Elements where text content is meaningful for the signature
    const textContentElements = [
      'title', 'h1', 'h2', 'h3', 'h4', 'h5', 'h6',
      'p', 'span', 'a', 'button', 'label', 'option',
      'th', 'td', 'dt', 'dd', 'figcaption', 'summary',
      'script', 'style' // Include content for inline scripts and styles
    ];
    return textContentElements.includes(tagName);
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();
    for (const symbol of symbols) {
      if (symbol.metadata?.type) {
        types.set(symbol.id, symbol.metadata.type);
      } else if (symbol.metadata?.tagName) {
        types.set(symbol.id, `html:${symbol.metadata.tagName}`);
      }
    }
    return types;
  }

  private isFormField(tagName: string): boolean {
    const formFieldElements = [
      'input', 'textarea', 'select', 'button', 'fieldset', 'legend', 'label'
    ];
    return formFieldElements.includes(tagName);
  }
}