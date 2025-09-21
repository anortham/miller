#!/bin/bash

# Miller - Tree-sitter WASM Parser Build Script
# This script builds all WASM parsers with ABI 14 for maximum compatibility
#
# Usage:
#   ./scripts/build-parsers.sh              # Build all parsers
#   ./scripts/build-parsers.sh ruby html    # Build specific parsers
#   ./scripts/build-parsers.sh --clean      # Clean and rebuild all

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Configuration
TEMP_DIR="/tmp/miller-parsers-build"
WASM_DIR="$(pwd)/wasm"
ABI_VERSION="14"

# Parser configurations
# Format: "name:repo_path:subdirectory"
declare -A PARSERS=(
    ["ruby"]="tree-sitter/tree-sitter-ruby:"
    ["html"]="tree-sitter/tree-sitter-html:"
    ["javascript"]="tree-sitter/tree-sitter-javascript:"
    ["typescript"]="tree-sitter/tree-sitter-typescript:typescript"
    ["css"]="tree-sitter/tree-sitter-css:"
    ["php"]="tree-sitter/tree-sitter-php:php"
    ["python"]="tree-sitter/tree-sitter-python:"
    ["rust"]="tree-sitter/tree-sitter-rust:"
    ["go"]="tree-sitter/tree-sitter-go:"
    ["java"]="tree-sitter/tree-sitter-java:"
    ["c_sharp"]="tree-sitter/tree-sitter-c-sharp:"
    ["c"]="tree-sitter/tree-sitter-c:"
    ["cpp"]="tree-sitter/tree-sitter-cpp:"
    ["sql"]="DerekStride/tree-sitter-sql:"
    ["zig"]="tree-sitter-grammars/tree-sitter-zig:"
    ["dart"]="UserNobody14/tree-sitter-dart:"
)

print_header() {
    echo -e "${BLUE}======================================${NC}"
    echo -e "${BLUE}  Miller Tree-sitter Parser Builder${NC}"
    echo -e "${BLUE}======================================${NC}"
    echo ""
}

print_status() {
    echo -e "${GREEN}[INFO]${NC} $1"
}

print_warning() {
    echo -e "${YELLOW}[WARN]${NC} $1"
}

print_error() {
    echo -e "${RED}[ERROR]${NC} $1"
}

check_dependencies() {
    print_status "Checking dependencies..."

    if ! command -v tree-sitter &> /dev/null; then
        print_error "tree-sitter CLI not found. Please install it first."
        exit 1
    fi

    if ! command -v emcc &> /dev/null; then
        print_error "emscripten (emcc) not found. Please install it first."
        exit 1
    fi

    if ! command -v git &> /dev/null; then
        print_error "git not found. Please install it first."
        exit 1
    fi

    print_status "✓ All dependencies found"
    echo "  - tree-sitter: $(tree-sitter --version)"
    echo "  - emcc: $(emcc --version | head -1)"
    echo ""
}

clean_build_dir() {
    if [ -d "$TEMP_DIR" ]; then
        print_status "Cleaning existing build directory..."
        rm -rf "$TEMP_DIR"
    fi
    mkdir -p "$TEMP_DIR"
}

build_parser() {
    local name=$1
    local config=${PARSERS[$name]}

    if [ -z "$config" ]; then
        print_error "Unknown parser: $name"
        return 1
    fi

    IFS=':' read -r repo subdir <<< "$config"
    local repo_name=$(basename "$repo")
    local output_name="tree-sitter-${name//_/-}.wasm"

    print_status "Building $name parser..."

    # Clone repository
    cd "$TEMP_DIR"
    if [ ! -d "$repo_name" ]; then
        print_status "  Cloning $repo..."
        git clone "https://github.com/$repo.git" --quiet
    fi

    # Navigate to parser directory
    local build_dir="$TEMP_DIR/$repo_name"
    if [ -n "$subdir" ]; then
        build_dir="$build_dir/$subdir"
    fi

    cd "$build_dir"

    # Install npm dependencies if package.json exists
    if [ -f "package.json" ]; then
        print_status "  Installing dependencies..."
        npm install --silent --no-progress 2>/dev/null || {
            print_warning "  npm install failed, continuing anyway..."
        }
    fi

    # For TypeScript, we need to install dependencies in parent directory too
    if [ "$name" = "typescript" ] && [ -f "../package.json" ]; then
        print_status "  Installing parent dependencies..."
        cd ..
        npm install --silent --no-progress 2>/dev/null || {
            print_warning "  Parent npm install failed, continuing anyway..."
        }
        cd "$subdir"
    fi

    # Generate parser with ABI 14
    print_status "  Generating parser with ABI $ABI_VERSION..."
    tree-sitter generate --abi="$ABI_VERSION" --quiet || {
        print_error "  Failed to generate parser for $name"
        return 1
    }

    # Build WASM
    print_status "  Building WASM..."
    tree-sitter build --wasm --quiet -o "$WASM_DIR/$output_name" 2>/dev/null || {
        print_error "  Failed to build WASM for $name"
        return 1
    }

    # Verify output
    if [ -f "$WASM_DIR/$output_name" ]; then
        local size=$(ls -lh "$WASM_DIR/$output_name" | awk '{print $5}')
        print_status "  ✓ Built $output_name ($size)"
    else
        print_error "  Failed to create $output_name"
        return 1
    fi

    return 0
}

show_usage() {
    echo "Usage: $0 [options] [parser_names...]"
    echo ""
    echo "Options:"
    echo "  --clean, -c     Clean build directory and rebuild all"
    echo "  --help, -h      Show this help message"
    echo ""
    echo "Available parsers:"
    for parser in "${!PARSERS[@]}"; do
        echo "  - $parser"
    done | sort
    echo ""
    echo "Examples:"
    echo "  $0                    # Build all parsers"
    echo "  $0 ruby html          # Build only Ruby and HTML parsers"
    echo "  $0 --clean            # Clean and rebuild all parsers"
}

main() {
    local build_parsers=()
    local clean_mode=false

    # Parse arguments
    while [[ $# -gt 0 ]]; do
        case $1 in
            --clean|-c)
                clean_mode=true
                shift
                ;;
            --help|-h)
                show_usage
                exit 0
                ;;
            *)
                build_parsers+=("$1")
                shift
                ;;
        esac
    done

    # If no parsers specified, build all
    if [ ${#build_parsers[@]} -eq 0 ]; then
        build_parsers=($(printf '%s\n' "${!PARSERS[@]}" | sort))
    fi

    # Validate parser names
    for parser in "${build_parsers[@]}"; do
        if [ -z "${PARSERS[$parser]:-}" ]; then
            print_error "Unknown parser: $parser"
            echo ""
            show_usage
            exit 1
        fi
    done

    print_header
    check_dependencies

    # Create wasm directory if it doesn't exist
    mkdir -p "$WASM_DIR"

    # Clean if requested
    if [ "$clean_mode" = true ]; then
        clean_build_dir
    else
        mkdir -p "$TEMP_DIR"
    fi

    # Build parsers
    local total=${#build_parsers[@]}
    local success=0
    local failed=()

    print_status "Building $total parser(s) with ABI $ABI_VERSION..."
    echo ""

    for parser in "${build_parsers[@]}"; do
        if build_parser "$parser"; then
            ((success++))
        else
            failed+=("$parser")
        fi
        echo ""
    done

    # Summary
    echo -e "${BLUE}======================================${NC}"
    echo -e "${GREEN}✓ Successfully built: $success/$total parsers${NC}"

    if [ ${#failed[@]} -gt 0 ]; then
        echo -e "${RED}✗ Failed parsers: ${failed[*]}${NC}"
    fi

    echo ""
    print_status "WASM files are in: $WASM_DIR"

    if [ "$clean_mode" = true ]; then
        print_status "Cleaning up build directory..."
        rm -rf "$TEMP_DIR"
    else
        print_status "Build directory preserved: $TEMP_DIR"
    fi

    echo ""

    # Exit with error if any builds failed
    if [ ${#failed[@]} -gt 0 ]; then
        exit 1
    fi
}

# Run main function with all arguments
main "$@"