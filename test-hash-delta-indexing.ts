import { EnhancedCodeIntelligenceEngine } from './src/engine/enhanced-code-intelligence.js';
import { MillerPaths } from './src/utils/miller-paths.js';
import { initializeLogger } from './src/utils/logger.js';
import { writeFile, readFile, mkdtemp, rm } from 'fs/promises';
import path from 'path';
import os from 'os';

async function testHashDeltaIndexing() {
  console.log('🧪 Testing Hash-based Delta Indexing...\n');

  // Create temp directory
  const tempDir = await mkdtemp(path.join(os.tmpdir(), 'miller-hash-test-'));
  console.log(`📁 Using temp directory: ${tempDir}`);

  try {
    // Initialize Miller with temp workspace
    const paths = new MillerPaths(tempDir);
    await paths.ensureDirectories();
    initializeLogger(paths);

    const engine = new EnhancedCodeIntelligenceEngine({
      workspacePath: tempDir,
      enableWatcher: true,
      watcherDebounceMs: 100,
      enableSemanticSearch: false, // Disable for faster testing
      embeddingModel: 'fast'
    });

    await engine.initialize();

    // Test file
    const testFile = path.join(tempDir, 'test.ts');
    const originalContent = `
function hello() {
  console.log("Hello World");
}
    `.trim();

    // First indexing - should process file
    console.log('📝 Writing initial file...');
    await writeFile(testFile, originalContent);
    await engine.indexFile(testFile);

    const stats1 = engine.getStats();
    console.log(`✅ Initial indexing: ${stats1.database.symbols} symbols created`);

    // Second indexing with same content - should skip due to hash
    console.log('\n📝 Attempting re-index with same content...');
    await engine.indexFile(testFile);

    const stats2 = engine.getStats();
    console.log(`⚡ Same content re-index: ${stats2.database.symbols} symbols (should be same)`);

    // Third indexing with different content - should process
    const modifiedContent = `
function hello() {
  console.log("Hello World!");
  console.log("Modified!");
}
    `.trim();

    console.log('\n📝 Writing modified content...');
    await writeFile(testFile, modifiedContent);
    await engine.indexFile(testFile);

    const stats3 = engine.getStats();
    console.log(`✅ Modified content indexing: ${stats3.database.symbols} symbols`);

    // Fourth attempt with same modified content - should skip
    console.log('\n📝 Attempting re-index with same modified content...');
    await engine.indexFile(testFile);

    const stats4 = engine.getStats();
    console.log(`⚡ Same modified content re-index: ${stats4.database.symbols} symbols (should be same)`);

    // Test manual hash checking
    console.log('\n🔍 Testing hash checking manually...');

    // Create a private method to access checkIfNeedsReindex
    const needsReindex1 = await (engine as any).checkIfNeedsReindex(testFile, modifiedContent);
    console.log(`📋 Same content needs reindex: ${needsReindex1} (should be false)`);

    const needsReindex2 = await (engine as any).checkIfNeedsReindex(testFile, originalContent);
    console.log(`📋 Different content needs reindex: ${needsReindex2} (should be true)`);

    console.log('\n✅ Hash checking test completed');

    await engine.terminate();

    console.log('\n🎉 Hash-based Delta Indexing Test Complete!');
    console.log('\n📊 Expected behavior:');
    console.log('  - Initial indexing creates symbols');
    console.log('  - Re-indexing same content should skip (hash match)');
    console.log('  - Indexing different content should process (hash mismatch)');
    console.log('  - File watcher should use hash checking too');

  } catch (error) {
    console.error('❌ Test failed:', error);
  } finally {
    // Cleanup
    await rm(tempDir, { recursive: true, force: true });
    console.log(`🧹 Cleaned up temp directory: ${tempDir}`);
  }
}

testHashDeltaIndexing().catch(console.error);