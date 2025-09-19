#!/bin/bash
# Cross-platform test runner for Razor WASM support

echo "🌍 Miller Cross-Platform Razor Test Suite"
echo "=========================================="

# Check if we're in the right directory
if [ ! -f "scripts/test-razor-platform.js" ]; then
    echo "❌ Error: Run this from the Miller project root directory"
    exit 1
fi

# Test on current platform
echo "Testing on current platform..."
bun run scripts/test-razor-platform.js

if [ $? -eq 0 ]; then
    echo ""
    echo "✅ Current platform test PASSED"
    echo ""
    echo "📋 To test on other platforms:"
    echo "  Windows: bun run scripts/test-razor-platform.js"
    echo "  Linux:   bun run scripts/test-razor-platform.js"
    echo "  macOS:   bun run scripts/test-razor-platform.js"
    echo ""
    echo "💡 Copy this entire Miller directory to each platform and run the command above"
else
    echo ""
    echo "❌ Current platform test FAILED"
    exit 1
fi