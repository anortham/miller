@echo off
REM Cross-platform test runner for Razor WASM support (Windows)

echo 🌍 Miller Cross-Platform Razor Test Suite
echo ==========================================

REM Check if we're in the right directory
if not exist "scripts\test-razor-platform.js" (
    echo ❌ Error: Run this from the Miller project root directory
    exit /b 1
)

REM Test on Windows
echo Testing on Windows...
bun run scripts/test-razor-platform.js

if %errorlevel% equ 0 (
    echo.
    echo ✅ Windows test PASSED
    echo.
    echo 📋 Platform test commands:
    echo   Windows: bun run scripts/test-razor-platform.js
    echo   Linux:   bun run scripts/test-razor-platform.js
    echo   macOS:   bun run scripts/test-razor-platform.js
    echo.
    echo 💡 Copy this entire Miller directory to each platform and run the command above
) else (
    echo.
    echo ❌ Windows test FAILED
    exit /b 1
)