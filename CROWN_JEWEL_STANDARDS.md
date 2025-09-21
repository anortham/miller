# 👑 Miller Extractor Crown Jewel Standards

## 🎯 Quality Standards for Miller's WASM Parser + Extractor System

This document establishes the **crown jewel quality standards** for Miller's multi-language code intelligence system - the most critical and widely-examined component that other projects will learn from.

## 📊 Current Status

### ✅ Working Extractors (High Quality)
- **PowerShell**: 71% (5/7 tests) - Near production ready
- **Bash**: 57% (4/7 tests) - Core functionality solid
- **Ruby**: ~86% (6/7 tests) - Minor relationship issues
- **Kotlin, Java, Rust, C#, etc.**: 80-95% success rates

### 🔧 Needs Refactoring
- **Dart**: Method signature pattern mismatch (comprehensive tests ready)
- **SQL**: 18 traverseTree() calls need updating (comprehensive tests ready)
- **Zig**: 16 traverseTree() calls need updating (comprehensive tests ready)

### ✅ API Foundations Fixed
- **BaseExtractor Enhanced**: Added missing helper methods
- **Helper Methods**: `traverseTree()`, `findChildByType()`, `extractDocumentation()`
- **Consistency**: Variable naming standardized (`result` not `parseResult`)

## 🎯 Gold Standard Patterns

### 1. Test Structure Template
```typescript
import { describe, it, expect, beforeAll } from 'bun:test';
import { ParserManager } from '../../parser/parser-manager.js';
import { LanguageExtractor } from '../../extractors/language-extractor.js';
import { SymbolKind, RelationshipKind } from '../../extractors/base-extractor.js';

describe('LanguageExtractor', () => {
  let parserManager: ParserManager;

  beforeAll(async () => {
    parserManager = new ParserManager();
    await parserManager.initialize();
  });

  describe('Core Language Features', () => {
    it('should extract classes, functions, and basic constructs', async () => {
      const code = \`
// Comprehensive real-world code examples
class ExampleClass {
  method() { return "test"; }
}
\`;

      const result = await parserManager.parseFile('test.ext', code);
      const extractor = new LanguageExtractor('language', 'test.ext', code);
      const symbols = extractor.extractSymbols(result.tree);

      // Comprehensive assertions
      const classSymbol = symbols.find(s => s.name === 'ExampleClass');
      expect(classSymbol).toBeDefined();
      expect(classSymbol?.kind).toBe(SymbolKind.Class);
      expect(classSymbol?.signature).toContain('class ExampleClass');
    });
  });

  describe('Advanced Features', () => {
    it('should extract complex language-specific patterns', async () => {
      // Language-specific advanced features
    });
  });

  describe('Type Inference and Relationships', () => {
    it('should infer types and extract relationships', async () => {
      const code = \`/* relationship code */\`;
      const result = await parserManager.parseFile('test.ext', code);
      const extractor = new LanguageExtractor('language', 'test.ext', code);

      const symbols = extractor.extractSymbols(result.tree);
      const relationships = extractor.extractRelationships(result.tree, symbols);
      const types = extractor.inferTypes(symbols);

      expect(relationships.length).toBeGreaterThan(0);
      expect(types.size).toBeGreaterThan(0);
    });
  });
});
```

### 2. Extractor Implementation Template
```typescript
import { Parser } from 'web-tree-sitter';
import { BaseExtractor, Symbol, SymbolKind, Relationship, RelationshipKind } from './base-extractor.js';

export class LanguageExtractor extends BaseExtractor {
  extractSymbols(tree: Parser.Tree): Symbol[] {
    const symbols: Symbol[] = [];

    const visitNode = (node: Parser.SyntaxNode, parentId?: string) => {
      if (!node || !node.type) {
        return; // Skip invalid nodes
      }

      let symbol: Symbol | null = null;

      try {
        switch (node.type) {
          case 'class_declaration':
            symbol = this.extractClass(node, parentId);
            break;
          case 'function_declaration':
            symbol = this.extractFunction(node, parentId);
            break;
          // Add more cases...
        }
      } catch (error) {
        console.warn(\`Error extracting symbol from \${node.type}:\`, error);
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
            continue; // Skip problematic child nodes
          }
        }
      }
    };

    try {
      visitNode(tree.rootNode);
    } catch (error) {
      console.warn('Language parsing failed:', error);
    }

    return symbols;
  }

  extractRelationships(tree: Parser.Tree, symbols: Symbol[]): Relationship[] {
    const relationships: Relationship[] = [];
    const symbolMap = new Map(symbols.map(s => [s.id, s]));

    // Use traverseTree helper for consistent pattern
    this.traverseTree(tree.rootNode, (node) => {
      // Extract relationships based on node types
    });

    return relationships;
  }

  inferTypes(symbols: Symbol[]): Map<string, string> {
    const types = new Map<string, string>();

    // Language-specific type inference logic

    return types;
  }

  // Private extraction methods that return Symbol | null
  private extractClass(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    const nameNode = this.findChildByType(node, 'identifier');
    if (!nameNode) return null;

    const name = this.getNodeText(nameNode);

    return this.createSymbol(node, name, SymbolKind.Class, {
      signature: this.extractClassSignature(node),
      visibility: 'public',
      parentId,
      docComment: this.extractDocumentation(node)
    });
  }

  private extractFunction(node: Parser.SyntaxNode, parentId?: string): Symbol | null {
    // Similar pattern...
  }

  // Helper methods using BaseExtractor utilities
  private extractClassSignature(node: Parser.SyntaxNode): string {
    // Use this.getNodeText(), this.findChildByType(), etc.
  }
}
```

### 3. BaseExtractor Helper Methods
```typescript
// ✅ Available Helper Methods (USE THESE!)
protected getNodeText(node: Parser.SyntaxNode): string
protected findDocComment(node: Parser.SyntaxNode): string | undefined
protected generateId(name: string, line: number, column: number): string
protected createSymbol(node, name, kind, options): Symbol
protected createRelationship(fromId, toId, kind, node, confidence?, metadata?): Relationship

// ✅ New Standard Helper Methods
protected traverseTree(node: Parser.SyntaxNode, callback: (node) => void): void
protected findChildByType(node: Parser.SyntaxNode, type: string): Parser.SyntaxNode | null
protected findChildrenByType(node: Parser.SyntaxNode, type: string): Parser.SyntaxNode[]
protected findChildByTypes(node: Parser.SyntaxNode, types: string[]): Parser.SyntaxNode | null
protected extractDocumentation(node: Parser.SyntaxNode): string | undefined
```

## 🚀 Implementation Strategy

### Phase 1: Foundation (✅ COMPLETE)
- [x] BaseExtractor API enhanced with helper methods
- [x] Test structure consistency audit
- [x] Variable naming standardization

### Phase 2: Working Extractor Optimization (IN PROGRESS)
- [ ] Push PowerShell from 71% → 85%+
- [ ] Push Bash from 57% → 75%+
- [ ] Fix Ruby relationships for 95%+
- [ ] Optimize existing high-performing extractors

### Phase 3: Systematic Refactoring (PLANNED)
- [ ] Refactor Dart extractor as template (comprehensive tests → working implementation)
- [ ] Apply template to SQL extractor
- [ ] Apply template to Zig extractor
- [ ] All extractors follow gold standard pattern

### Phase 4: Real-World Validation (PLANNED)
- [ ] Test against Serena project files (multi-language real-world codebase)
- [ ] Performance benchmarking across all languages
- [ ] Documentation and best practices guide

### Phase 5: Crown Jewel Completion (PLANNED)
- [ ] 90%+ test success rates across all extractors
- [ ] Comprehensive documentation for other projects
- [ ] GitHub showcase-ready quality

## 📋 Quality Checklist

### ✅ Test Quality
- [ ] Comprehensive real-world code examples
- [ ] All three extractor methods tested (symbols, relationships, types)
- [ ] Edge cases and error handling covered
- [ ] Language-specific advanced features included

### ✅ Code Quality
- [ ] Follows gold standard patterns
- [ ] Uses BaseExtractor helper methods consistently
- [ ] Error handling with graceful degradation
- [ ] Clear, documented method signatures

### ✅ Performance
- [ ] Sub-second extraction for typical files
- [ ] Memory efficient symbol storage
- [ ] Graceful handling of large files

### ✅ Documentation
- [ ] Comprehensive inline documentation
- [ ] Clear examples for other projects
- [ ] Best practices guide maintained

## 🎯 Success Metrics

- **Test Coverage**: 90%+ passing tests per extractor
- **Real-World Validation**: Successfully handles Serena project files
- **Performance**: <1s extraction time for typical files
- **Documentation**: Complete guide for adding new extractors
- **Community**: GitHub showcase quality that other projects can learn from

## 🏆 Crown Jewel Vision

Miller's WASM parser + extractor system will be:
- **The definitive example** of multi-language code intelligence
- **Production-ready** for any project needing code analysis
- **Educational resource** for implementing Tree-sitter parsing
- **Performance benchmark** for WASM-based parsing solutions
- **Open source showcase** demonstrating TypeScript/Bun excellence

This system will enable Miller to analyze any modern codebase with LSP-quality intelligence, and serve as the foundation for advanced AI-powered code understanding.