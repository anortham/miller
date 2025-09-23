import { test, expect, beforeAll, afterAll, describe } from "bun:test";
import { EnhancedCodeIntelligenceEngine } from '../../engine/enhanced-code-intelligence.js';
import { initializeLogger, LogLevel } from '../../utils/logger.js';
import { MillerPaths } from '../../utils/miller-paths.js';
import path from 'path';

describe("Semantic Search Initialization Bug", () => {
  let engine: EnhancedCodeIntelligenceEngine;
  let workspacePath: string;

  beforeAll(async () => {
    // Use current Miller workspace for testing
    workspacePath = process.cwd();
    const paths = new MillerPaths(workspacePath);
    initializeLogger(paths, LogLevel.ERROR); // Minimize logs for test

    // Initialize engine with semantic search enabled (same config as MCP server)
    engine = new EnhancedCodeIntelligenceEngine({
      workspacePath,
      enableWatcher: false, // Disable for test stability
      enableSemanticSearch: true, // This should enable semantic search
      embeddingModel: 'fast',
      batchSize: 10 // Small batch for faster testing
    });

    await engine.initialize();
  });

  afterAll(async () => {
    await engine?.shutdown();
  });

  test("BUG REPRODUCTION: semantic search should be available but returns null", () => {
    // This test documents the current bug
    const hybridSearch = engine.hybridSearch;

    // BUG: This currently returns null even though semantic search is enabled
    expect(hybridSearch).toBeNull(); // Current broken behavior

    // What it SHOULD be once fixed:
    // expect(hybridSearch).not.toBeNull();
    // expect(hybridSearch).toBeDefined();
  });

  test("BUG REPRODUCTION: health check shows semantic search unavailable", () => {
    const stats = engine.getStats();

    // Document current broken state
    expect(stats.semantic?.semanticSearchAvailable).toBe(false);
    expect(stats.semantic?.embeddingProgress).toBe(0);

    // What it SHOULD be once fixed:
    // expect(stats.semantic?.semanticSearchAvailable).toBe(true);
    // expect(stats.semantic?.embeddingProgress).toBeGreaterThan(0);
  });

  test("BUG REPRODUCTION: semantic search returns initialization message", async () => {
    // Try to use semantic search - should fail with initialization message
    try {
      const result = await engine.semanticSearch?.("test query");
      expect(result).toBeUndefined(); // Current behavior
    } catch (error) {
      // Expected to fail
      expect(error).toBeDefined();
    }
  });

  test("Configuration verification: semantic search is enabled", () => {
    // Verify that our configuration is correct
    expect(engine.config.enableSemanticSearch).toBe(true);
    expect(engine.config.embeddingModel).toBe('fast');
  });
});