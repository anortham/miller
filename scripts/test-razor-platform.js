#!/usr/bin/env bun
/**
 * Cross-platform Razor WASM test script
 * Tests that the Razor parser loads and can parse basic Razor syntax
 */

import { CodeIntelligenceEngine } from '../src/engine/code-intelligence.js';
import { MillerPaths } from '../src/utils/miller-paths.js';
import { initializeLogger } from '../src/utils/logger.js';
import { writeFile, mkdir, rm } from 'fs/promises';
import { existsSync } from 'fs';
import path from 'path';
import os from 'os';

async function main() {
  console.log('🧪 Miller Razor WASM Cross-Platform Test');
  console.log('=======================================');
  console.log(`Platform: ${os.platform()}`);
  console.log(`Architecture: ${os.arch()}`);
  console.log(`Node version: ${process.version}`);
  console.log(`Bun version: ${Bun.version}\n`);

  const testDir = path.join(os.tmpdir(), `miller-razor-test-${Date.now()}`);

  try {
    // 1. Setup test environment
    console.log('📁 Setting up test environment...');
    await mkdir(testDir, { recursive: true });

    const paths = new MillerPaths(testDir);
    await paths.ensureDirectories();
    initializeLogger(paths);

    // 2. Initialize Miller engine
    console.log('🔧 Initializing Miller engine...');
    const engine = new CodeIntelligenceEngine({ workspacePath: testDir });
    await engine.initialize();

    // 3. Check if Razor parser loaded
    console.log('🔍 Checking parser status...');
    const health = await engine.healthCheck();
    const parsers = health.details.parsers;

    if (parsers.loaded.includes('razor')) {
      console.log('✅ Razor parser loaded successfully!');
    } else {
      console.log('❌ Razor parser failed to load');
      console.log('Available parsers:', parsers.loaded);
      return false;
    }

    // 4. Create test Razor file
    console.log('📝 Creating test Razor file...');
    const razorContent = `@page "/test"
@model TestModel

<h1>Hello @Model.Name!</h1>

@if (Model.IsValid)
{
    <p>Welcome to Miller testing!</p>
    <div class="container">
        @foreach (var item in Model.Items)
        {
            <span>@item.Value</span>
        }
    </div>
}

@{
    var timestamp = DateTime.Now;
    var message = "Cross-platform test successful";
}

<footer>
    <p>Generated: @timestamp</p>
    <p>@message</p>
</footer>`;

    const testFile = path.join(testDir, 'test.razor');
    await writeFile(testFile, razorContent);

    // 5. Test parsing
    console.log('⚡ Testing Razor file parsing...');
    await engine.indexWorkspace(testDir);

    // 6. Verify results
    console.log('📊 Checking results...');
    const stats = await engine.getWorkspaceStats();

    console.log(`  Total files indexed: ${stats.totalFiles}`);
    console.log(`  Total symbols found: ${stats.totalSymbols}`);
    console.log(`  Languages detected: ${stats.languages.join(', ')}`);

    if (stats.languages.includes('razor')) {
      console.log('✅ Razor language detected in workspace!');
    } else {
      console.log('❌ Razor language not detected in workspace');
      return false;
    }

    // 7. Test search functionality
    console.log('🔎 Testing search functionality...');
    const searchResults = await engine.searchCode('Model', { limit: 5 });
    console.log(`  Search results for "Model": ${searchResults.length}`);

    const razorSearch = await engine.searchCode('razor', { limit: 5 });
    console.log(`  Search results mentioning "razor": ${razorSearch.length}`);

    // 8. Cleanup
    await engine.dispose();

    console.log('\n🎉 PLATFORM TEST PASSED!');
    console.log(`✅ Razor WASM works correctly on ${os.platform()}`);
    return true;

  } catch (error) {
    console.log('\n💥 PLATFORM TEST FAILED!');
    console.error(`❌ Error on ${os.platform()}:`, error.message);
    return false;
  } finally {
    // Cleanup test directory
    try {
      if (existsSync(testDir)) {
        await rm(testDir, { recursive: true, force: true });
      }
    } catch (cleanupError) {
      console.warn('Warning: Could not cleanup test directory:', cleanupError.message);
    }
  }
}

// Run the test
main().then(success => {
  process.exit(success ? 0 : 1);
}).catch(error => {
  console.error('Fatal error:', error);
  process.exit(1);
});