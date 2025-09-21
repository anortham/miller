# Miller Build Scripts

This directory contains maintenance and build scripts for the Miller code intelligence engine.

## `build-parsers.sh`

Automated script for building Tree-sitter WASM parsers with consistent ABI compatibility.

### Features

- **ABI 14 Compatibility**: Builds all parsers with ABI 14 for maximum compatibility
- **Dependency Management**: Automatically handles npm dependencies for complex parsers
- **Error Handling**: Robust error handling with detailed status reporting
- **Selective Building**: Build all parsers or specify individual ones
- **Clean Mode**: Option to clean and rebuild from scratch

### Usage

```bash
# Build all parsers
./scripts/build-parsers.sh

# Build specific parsers
./scripts/build-parsers.sh ruby html javascript

# Clean and rebuild all
./scripts/build-parsers.sh --clean

# Show help
./scripts/build-parsers.sh --help
```

### Supported Parsers

- **Web Languages**: javascript, typescript, css, html, php
- **Systems Languages**: c, cpp, c_sharp, rust, go
- **Scripting Languages**: python, ruby, java

### Prerequisites

- `tree-sitter` CLI (v0.25+)
- `emscripten` (emcc)
- `git`
- `npm` (for parsers with dependencies)

### Build Process

For each parser, the script:

1. Clones the official Tree-sitter repository
2. Installs npm dependencies (if needed)
3. Generates parser with `tree-sitter generate --abi=14`
4. Builds WASM with `tree-sitter build --wasm`
5. Outputs to `./wasm/tree-sitter-{name}.wasm`

### Output

All WASM files are placed in the `./wasm/` directory with standardized naming:

```
wasm/
├── tree-sitter-c.wasm
├── tree-sitter-cpp.wasm
├── tree-sitter-c-sharp.wasm  # Note: underscore for C#
├── tree-sitter-go.wasm
├── tree-sitter-html.wasm
├── tree-sitter-java.wasm
├── tree-sitter-javascript.wasm
├── tree-sitter-php.wasm
├── tree-sitter-python.wasm
├── tree-sitter-ruby.wasm
├── tree-sitter-rust.wasm
└── tree-sitter-typescript.wasm
```

### Troubleshooting

**Build Failures**:
- Check that emscripten is properly installed
- Ensure tree-sitter CLI is v0.25+
- Some parsers (TypeScript, C++) require npm dependencies

**Large File Warnings**:
- WASM files can be 1-10MB each (normal)
- C#, C++, and Razor tend to be largest

**Permission Errors**:
- Ensure script is executable: `chmod +x scripts/build-parsers.sh`
- Check write permissions to `./wasm/` directory

### Maintenance

Run this script when:
- Updating to new Tree-sitter versions
- Adding support for new languages
- Fixing compatibility issues
- Setting up new development environments

The script is designed to be idempotent - safe to run multiple times.