#!/bin/bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"

# Ensure cargo binaries are in PATH
export PATH="$HOME/.cargo/bin:$PATH"

echo "Building Rust FFI library..."
cd "$PROJECT_ROOT/rust"
cargo build --release -p codesearch-ffi

echo "Generating C# bindings..."
uniffi-bindgen-cs \
    --library target/release/libcodesearch_ffi.dylib \
    --out-dir "$PROJECT_ROOT/src/Codesearch.Interop/Generated"

echo "Bindings generated at src/Codesearch.Interop/Generated/"
