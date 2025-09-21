import { BaseExtractor, Symbol, Relationship, SymbolKind } from './base-extractor.js';
import { Parser } from 'web-tree-sitter';

interface VueSection {
  type: 'template' | 'script' | 'style';
  content: string;
  startLine: number;
  endLine: number;
  lang?: string; // e.g., 'ts', 'scss'
}


/**
 * Vue Single File Component (SFC) Extractor
 *
 * Parses .vue files by extracting template, script, and style sections
 * and delegating to appropriate existing parsers.
 */
export class VueExtractor extends BaseExtractor {
  constructor(language: string, filePath: string, content: string) {
    super(language, filePath, content);
  }

  extractSymbols(tree: Parser.Tree | null): Symbol[] {
    const symbols: Symbol[] = [];

    try {
      // Parse Vue SFC structure
      const sections = this.parseVueSFC(this.content);

      for (const section of sections) {
        const sectionSymbols = this.extractSectionSymbols(section);
        symbols.push(...sectionSymbols);
      }

      // Add component-level symbol
      const componentName = this.extractComponentName(sections);
      if (componentName) {
        symbols.push(this.createSymbolManual(
          componentName,
          SymbolKind.Class,
          1,
          1,
          this.content.split('\n').length,
          1,
          `<${componentName} />`,
          `Vue Single File Component: ${componentName}`,
          {
            type: 'vue-sfc',
            sections: sections.map(s => s.type)
          }
        ));
      }

    } catch (error) {
      console.warn(`Error extracting Vue symbols from ${this.filePath}:`, error.message);
    }

    return symbols;
  }

  extractRelationships(tree: Parser.Tree | null, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];

    try {
      const sections = this.parseVueSFC(this.content);

      // Extract relationships from script sections
      for (const section of sections) {
        if (section.type === 'script') {
          const sectionRels = this.extractScriptRelationships(section, symbols);
          relationships.push(...sectionRels);
        }
      }

    } catch (error) {
      console.warn(`Error extracting Vue relationships from ${this.filePath}:`, error.message);
    }

    return relationships;
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();

    try {
      const sections = this.parseVueSFC(this.content);

      // Infer types from script sections
      for (const section of sections) {
        if (section.type === 'script') {
          const sectionTypes = this.extractScriptTypes(section, symbols);
          sectionTypes.forEach((type, symbolId) => {
            types.set(symbolId, type);
          });
        }
      }

    } catch (error) {
      console.warn(`Error extracting Vue types from ${this.filePath}:`, error.message);
    }

    return types;
  }


  /**
   * Parse Vue SFC structure to extract template, script, and style sections
   */
  private parseVueSFC(content: string): VueSection[] {
    const sections: VueSection[] = [];
    const lines = content.split('\n');

    let currentSection: Partial<VueSection> | null = null;
    let sectionContent: string[] = [];

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      const trimmed = line.trim();

      // Check for section start
      const templateMatch = trimmed.match(/^<template(\s+[^>]*)?>/i);
      const scriptMatch = trimmed.match(/^<script(\s+[^>]*)?>/i);
      const styleMatch = trimmed.match(/^<style(\s+[^>]*)?>/i);

      if (templateMatch || scriptMatch || styleMatch) {
        // End previous section
        if (currentSection && sectionContent.length > 0) {
          sections.push({
            ...currentSection,
            content: sectionContent.join('\n'),
            endLine: i - 1
          } as VueSection);
        }

        // Start new section
        const type = templateMatch ? 'template' : scriptMatch ? 'script' : 'style';
        const attrs = templateMatch?.[1] || scriptMatch?.[1] || styleMatch?.[1] || '';
        const langMatch = attrs.match(/lang=["']?([^"'\s>]+)/i);

        currentSection = {
          type,
          startLine: i + 1,
          lang: langMatch?.[1] || (type === 'script' ? 'js' : type === 'style' ? 'css' : 'html')
        };
        sectionContent = [];
        continue;
      }

      // Check for section end
      if (trimmed.match(/^<\/(template|script|style)>/i)) {
        if (currentSection) {
          sections.push({
            ...currentSection,
            content: sectionContent.join('\n'),
            endLine: i - 1
          } as VueSection);
          currentSection = null;
          sectionContent = [];
        }
        continue;
      }

      // Add content to current section
      if (currentSection) {
        sectionContent.push(line);
      }
    }

    // Handle unclosed section
    if (currentSection && sectionContent.length > 0) {
      sections.push({
        ...currentSection,
        content: sectionContent.join('\n'),
        endLine: lines.length - 1
      } as VueSection);
    }

    return sections;
  }

  /**
   * Helper to create symbols manually (without Parser.SyntaxNode)
   */
  private createSymbolManual(
    name: string,
    kind: SymbolKind,
    startLine: number,
    startColumn: number,
    endLine: number,
    endColumn: number,
    signature?: string,
    documentation?: string,
    metadata?: any
  ): Symbol {
    const id = this.generateId(name, startLine, startColumn);

    return {
      id,
      name,
      kind,
      language: this.language,
      filePath: this.filePath,
      startLine,
      startColumn,
      endLine,
      endColumn,
      startByte: 0, // Not available without tree-sitter node
      endByte: 0,   // Not available without tree-sitter node
      signature,
      docComment: documentation,
      visibility: 'public',
      metadata
    };
  }

  /**
   * Extract symbols from a specific section using appropriate parser
   */
  private extractSectionSymbols(section: VueSection): Symbol[] {
    const symbols: Symbol[] = [];

    try {
      if (section.type === 'script') {
        // Use TypeScript extractor for script sections
        const isTypeScript = section.lang === 'ts' || section.lang === 'typescript';
        // We would need to create a temporary tree here
        // For now, let's extract basic Vue component structure
        symbols.push(...this.extractScriptSymbolsBasic(section));
      } else if (section.type === 'template') {
        // Extract template symbols (components, directives, etc.)
        symbols.push(...this.extractTemplateSymbols(section));
      } else if (section.type === 'style') {
        // Extract CSS class names, etc.
        symbols.push(...this.extractStyleSymbols(section));
      }
    } catch (error) {
      console.warn(`Error extracting symbols from ${section.type} section:`, error.message);
    }

    return symbols;
  }

  /**
   * Basic script symbol extraction (without full tree-sitter parsing)
   */
  private extractScriptSymbolsBasic(section: VueSection): Symbol[] {
    const symbols: Symbol[] = [];
    const lines = section.content.split('\n');

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      const actualLine = section.startLine + i;

      // Extract Vue component options
      const dataMatch = line.match(/^\s*data\s*\(\s*\)\s*\{/);
      const methodsMatch = line.match(/^\s*methods\s*:\s*\{/);
      const computedMatch = line.match(/^\s*computed\s*:\s*\{/);
      const propsMatch = line.match(/^\s*props\s*:\s*\{/);

      if (dataMatch) {
        symbols.push(this.createSymbolManual('data', SymbolKind.Function, actualLine, 1, actualLine, 5, 'data()', 'Vue component data'));
      } else if (methodsMatch) {
        symbols.push(this.createSymbolManual('methods', SymbolKind.Property, actualLine, 1, actualLine, 8, 'methods: {}', 'Vue component methods'));
      } else if (computedMatch) {
        symbols.push(this.createSymbolManual('computed', SymbolKind.Property, actualLine, 1, actualLine, 9, 'computed: {}', 'Vue computed properties'));
      } else if (propsMatch) {
        symbols.push(this.createSymbolManual('props', SymbolKind.Property, actualLine, 1, actualLine, 6, 'props: {}', 'Vue component props'));
      }

      // Extract function definitions
      const funcMatch = line.match(/^\s*([a-zA-Z_$][a-zA-Z0-9_$]*)\s*\([^)]*\)\s*\{/);
      if (funcMatch) {
        const funcName = funcMatch[1];
        const startCol = line.indexOf(funcName) + 1;
        symbols.push(this.createSymbolManual(funcName, SymbolKind.Method, actualLine, startCol, actualLine, startCol + funcName.length, `${funcName}()`, `Vue component method`));
      }
    }

    return symbols;
  }

  /**
   * Extract template symbols (component usage, directives, etc.)
   */
  private extractTemplateSymbols(section: VueSection): Symbol[] {
    const symbols: Symbol[] = [];
    const lines = section.content.split('\n');

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      const actualLine = section.startLine + i;

      // Extract component usage
      const componentMatches = line.matchAll(/<([A-Z][a-zA-Z0-9-]*)/g);
      for (const match of componentMatches) {
        const componentName = match[1];
        const startCol = match.index! + 1;
        symbols.push(this.createSymbolManual(componentName, SymbolKind.Class, actualLine, startCol, actualLine, startCol + componentName.length, `<${componentName}>`, 'Vue component usage'));
      }

      // Extract directives
      const directiveMatches = line.matchAll(/\s(v-[a-zA-Z-]+)=/g);
      for (const match of directiveMatches) {
        const directiveName = match[1];
        const startCol = match.index! + 1;
        symbols.push(this.createSymbolManual(directiveName, SymbolKind.Property, actualLine, startCol, actualLine, startCol + directiveName.length, directiveName, 'Vue directive'));
      }
    }

    return symbols;
  }

  /**
   * Extract style symbols (class names, etc.)
   */
  private extractStyleSymbols(section: VueSection): Symbol[] {
    const symbols: Symbol[] = [];
    const lines = section.content.split('\n');

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i];
      const actualLine = section.startLine + i;

      // Extract CSS class names
      const classMatches = line.matchAll(/\.([a-zA-Z_-][a-zA-Z0-9_-]*)\s*\{/g);
      for (const match of classMatches) {
        const className = match[1];
        const startCol = match.index! + 1;
        symbols.push(this.createSymbolManual(className, SymbolKind.Property, actualLine, startCol, actualLine, startCol + className.length, `.${className}`, 'CSS class'));
      }
    }

    return symbols;
  }


  /**
   * Extract component name from file path or script content
   */
  private extractComponentName(sections: VueSection[]): string {
    // Try to extract from script section first
    for (const section of sections) {
      if (section.type === 'script') {
        const nameMatch = section.content.match(/name\s*:\s*['"`]([^'"`]+)['"`]/);
        if (nameMatch) {
          return nameMatch[1];
        }
      }
    }

    // Fall back to file name
    const fileName = this.filePath.split('/').pop()?.replace('.vue', '');
    if (fileName) {
      // Convert kebab-case to PascalCase
      return fileName.split('-').map(part =>
        part.charAt(0).toUpperCase() + part.slice(1)
      ).join('');
    }

    return 'VueComponent';
  }

  /**
   * Extract relationships from script sections
   */
  private extractScriptRelationships(section: VueSection, symbols: Symbol[]): Relationship[] {
    // This would delegate to TypeScript extractor if we had a proper tree
    // For now, return basic relationships
    return [];
  }

  /**
   * Extract types from script sections
   */
  private extractScriptTypes(section: VueSection, symbols: Symbol[]): Map<string, string> {
    // This would delegate to TypeScript extractor if we had a proper tree
    // For now, return basic types
    return new Map<string, string>();
  }

}