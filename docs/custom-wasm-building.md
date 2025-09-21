# Custom WASM Parser Building Guide

This document explains Miller's approach to building custom Tree-sitter WASM parsers for maximum compatibility and control.

## Why Build Our Own WASM Parsers?

### Problems with Pre-built Parsers
- **Microsoft's @vscode/tree-sitter-wasm is "on pause indefinitely"**
- **ABI version mismatches** between parsers and web-tree-sitter versions
- **Compatibility issues** with different runtime environments
- **No control over quality** or update timing

### Benefits of Custom Building
- ✅ **Full control** over ABI version (we use ABI 14 for compatibility)
- ✅ **Consistent quality** across all language parsers
- ✅ **Predictable compatibility** with web-tree-sitter 0.25.9
- ✅ **Future-proof** - can update parsers when needed
- ✅ **Debuggable** - we understand the entire pipeline

## Our WASM Building Process

### Prerequisites
- `tree-sitter` CLI v0.25+
- `emscripten` (emcc) for WASM compilation
- `git` for cloning parser repositories
- `npm` for parsers with dependencies (TypeScript, C++)

### Standard Build Process

```bash
# 1. Clone parser repository
git clone https://github.com/tree-sitter/tree-sitter-[language].git
cd tree-sitter-[language]

# 2. Install dependencies (if needed)
npm install  # Only for parsers with package.json

# 3. Generate parser with ABI 14 for compatibility
tree-sitter generate --abi=14

# 4. Build WASM with our naming convention
tree-sitter build --wasm -o /path/to/miller/wasm/tree-sitter-[language].wasm
```

### Parser-Specific Variations

#### Multi-Grammar Parsers
Some parsers have subdirectories for different grammars:

```bash
# TypeScript has typescript/ and tsx/ subdirectories
cd tree-sitter-typescript/typescript
tree-sitter generate --abi=14
tree-sitter build --wasm -o tree-sitter-typescript.wasm

# PHP has php/ and php_only/ subdirectories
cd tree-sitter-php/php
tree-sitter generate --abi=14
tree-sitter build --wasm -o tree-sitter-php.wasm
```

#### Complex Dependencies
Some parsers require dependency resolution:

```bash
# C++ depends on tree-sitter-c
cd tree-sitter-cpp
npm install  # Installs tree-sitter-c dependency
tree-sitter generate --abi=14
tree-sitter build --wasm -o tree-sitter-cpp.wasm
```

#### Missing tree-sitter.json
Some parsers need a tree-sitter.json file for ABI 15+ compatibility:

```json
{
  "$schema": "https://tree-sitter.github.io/tree-sitter/assets/schemas/config.schema.json",
  "grammars": [
    {
      "name": "dart",
      "camelcase": "Dart",
      "scope": "source.dart",
      "file-types": ["dart"]
    }
  ],
  "metadata": {
    "version": "1.0.0",
    "license": "MIT",
    "description": "Dart grammar for tree-sitter",
    "authors": [{"name": "UserNobody14"}],
    "links": {
      "repository": "https://github.com/UserNobody14/tree-sitter-dart"
    }
  },
  "bindings": {
    "c": false,
    "go": false,
    "node": true,
    "python": false,
    "rust": false,
    "swift": false
  }
}
```

## Automated Building with scripts/build-parsers.sh

Our build script automates this process:

```bash
# Build all parsers
./scripts/build-parsers.sh

# Build specific parsers
./scripts/build-parsers.sh ruby dart sql

# Clean and rebuild everything
./scripts/build-parsers.sh --clean
```

### Parser Configuration

The script uses a configuration map:

```bash
declare -A PARSERS=(
    ["ruby"]="tree-sitter/tree-sitter-ruby:"
    ["typescript"]="tree-sitter/tree-sitter-typescript:typescript"
    ["php"]="tree-sitter/tree-sitter-php:php"
    ["sql"]="DerekStride/tree-sitter-sql:"
    ["zig"]="tree-sitter-grammars/tree-sitter-zig:"
    ["dart"]="UserNobody14/tree-sitter-dart:"
)
```

Format: `["name"]="github-repo:subdirectory"`

## Troubleshooting Common Issues

### 1. Grammar Generation Failures

**Problem**: `Failed to load grammar.js`
```
Error when generating parser
Caused by: Failed to load grammar.js -- No such file or directory
```

**Solution**: Parser may have subdirectories. Check for `typescript/`, `php/`, etc.

### 2. Missing Dependencies

**Problem**: `Cannot find module 'tree-sitter-javascript/grammar'`

**Solution**: Install npm dependencies:
```bash
cd tree-sitter-[language]
npm install
```

### 3. Missing tree-sitter.json

**Problem**: `Failed to locate a tree-sitter.json file`

**Solution**: Create minimal tree-sitter.json (see example above)

### 4. ABI Compatibility

**Problem**: `WebAssembly.Module doesn't parse at byte X`

**Solution**: Rebuild with ABI 14:
```bash
tree-sitter generate --abi=14
```

### 5. Emscripten Warnings

**Warning**: `unexpected binaryen version: 123 (expected 124)`

**Status**: Safe to ignore - parser still builds correctly

## Quality Assurance

### Testing New Parsers

1. **Load Test**: Verify WASM loads in web-tree-sitter
```javascript
const language = await Language.load('./wasm/tree-sitter-[lang].wasm');
```

2. **Parse Test**: Test with sample code
```javascript
const parser = new Parser();
parser.setLanguage(language);
const tree = parser.parse(sampleCode);
```

3. **Extractor Test**: Verify symbol extraction works
```bash
bun test src/__tests__/parser/[lang]-extractor.test.ts
```

### Debug Scripts

We maintain debug scripts for each parser:

```bash
bun run debug/debug-[language]-test.js
```

These scripts test:
- WASM loading
- Basic parsing
- Language-specific constructs
- Error handling

## Current Parser Inventory

### Production Ready (ABI 14, Tested)
- **Web**: JavaScript, TypeScript, CSS, HTML
- **Backend**: Python, Rust, Go, Java, C#, C, C++, PHP, Ruby
- **Mobile**: Swift, Kotlin, Dart (Flutter)
- **Systems**: Zig (Bun team attention!)
- **Database**: SQL
- **Special**: Vue, Razor, Regex

### Build Status
- ✅ **18 languages** with custom WASM parsers
- ✅ **All ABI 14 compatible** with web-tree-sitter 0.25.9
- ✅ **Automated build pipeline** with scripts/build-parsers.sh
- ✅ **Comprehensive testing** with debug scripts

## Future Parser Additions

### High-Priority Candidates
- **Bash** - DevOps/deployment scripts
- **Lua** - Embedded scripting
- **Dockerfile** - Container definitions
- **YAML** - Configuration files (if needed)

### Adding New Parsers

1. **Research**: Find official tree-sitter grammar
2. **Configure**: Add to scripts/build-parsers.sh
3. **Build**: Use automated script or manual process
4. **Extract**: Create language-specific extractor
5. **Test**: Add debug script and unit tests
6. **Document**: Update this guide

## Maintenance

### Regular Updates
- **Monitor** tree-sitter releases for new features
- **Update** web-tree-sitter when stable versions release
- **Rebuild** parsers when language grammars improve
- **Test** compatibility after any updates

### Version Management
- **Pin** web-tree-sitter to stable versions (currently 0.25.9)
- **Use** ABI 14 until ecosystem stabilizes on ABI 15
- **Track** parser repository updates for bug fixes
- **Document** any custom patches or modifications

This approach gives Miller complete control over its parser infrastructure while maintaining compatibility and quality across all supported languages.