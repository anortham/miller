#!/usr/bin/env bun

import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { CodeIntelligenceEngine } from './engine/code-intelligence.js';

class MillerMCPServer {
  private server: McpServer;
  private engine: CodeIntelligenceEngine;
  private workspacePath: string = process.cwd();

  constructor() {
    this.engine = new CodeIntelligenceEngine({
      enableWatcher: true,
      watcherDebounceMs: 300,
      batchSize: 10
    });

    this.server = new McpServer({
      name: "miller",
      version: "1.0.0"
    });

    this.setupTools();
  }

  private setupTools() {
    // Search for code symbols with fuzzy matching
    this.server.registerTool("search_code", {
      title: "Code Search",
      description: "Search for code symbols, functions, classes, etc. using fuzzy matching",
      inputSchema: {
        query: z.string().describe("Search query (symbol name, partial name, etc.)"),
        type: z.enum(["fuzzy", "exact", "type"]).default("fuzzy").describe("Search type"),
        limit: z.number().default(50).describe("Maximum number of results"),
        language: z.string().optional().describe("Filter by programming language"),
        symbolKinds: z.array(z.string()).optional().describe("Filter by symbol kinds"),
        includeSignature: z.boolean().default(true).describe("Include function/method signatures")
      }
    }, async ({ query, type = "fuzzy", limit = 50, language, symbolKinds, includeSignature = true }) => {
      let results;
      if (type === 'exact') {
        results = await this.engine.searchExact(query, { limit, language, symbolKinds });
      } else if (type === 'type') {
        results = await this.engine.searchByType(query, { limit, language });
      } else {
        results = await this.engine.searchCode(query, {
          limit,
          language,
          symbolKinds,
          includeSignature
        });
      }

      return {
        content: [{
          type: "text",
          text: `Found ${results.length} results:\n\n` +
                results.map(r =>
                  `**${r.text}** (${r.kind || 'unknown'}) - ${r.file}:${r.line}:${r.column}` +
                  (r.signature ? `\n  Signature: \`${r.signature}\`` : '') +
                  (r.score ? `\n  Score: ${r.score.toFixed(2)}` : '')
                ).join('\n\n')
        }]
      };
    });

    // Go to definition
    this.server.registerTool("goto_definition", {
      title: "Go to Definition",
      description: "Find the definition of a symbol at a specific location",
      inputSchema: {
        file: z.string().describe("File path (absolute or relative to workspace)"),
        line: z.number().describe("Line number (1-based)"),
        column: z.number().describe("Column number (0-based)")
      }
    }, async ({ file, line, column }) => {
      const resolvedPath = this.resolvePath(file);
      const result = await this.engine.goToDefinition(resolvedPath, line, column);

      if (!result) {
        return {
          content: [{
            type: "text",
            text: "No definition found at the specified location."
          }]
        };
      }

      return {
        content: [{
          type: "text",
          text: `Definition found at: **${result.file}:${result.line}:${result.column}**`
        }]
      };
    });

    // Find references
    this.server.registerTool("find_references", {
      title: "Find References",
      description: "Find all references to a symbol at a specific location",
      inputSchema: {
        file: z.string().describe("File path (absolute or relative to workspace)"),
        line: z.number().describe("Line number (1-based)"),
        column: z.number().describe("Column number (0-based)")
      }
    }, async ({ file, line, column }) => {
      const resolvedPath = this.resolvePath(file);
      const references = await this.engine.findReferences(resolvedPath, line, column);

      if (references.length === 0) {
        return {
          content: [{
            type: "text",
            text: "No references found for the symbol at the specified location."
          }]
        };
      }

      return {
        content: [{
          type: "text",
          text: `Found ${references.length} references:\n\n` +
                references.map(ref =>
                  `- ${ref.file}:${ref.line}:${ref.column}`
                ).join('\n')
        }]
      };
    });

    // Get hover info
    this.server.registerTool("get_hover_info", {
      title: "Hover Information",
      description: "Get detailed information about a symbol (type, documentation, signature)",
      inputSchema: {
        file: z.string().describe("File path (absolute or relative to workspace)"),
        line: z.number().describe("Line number (1-based)"),
        column: z.number().describe("Column number (0-based)")
      }
    }, async ({ file, line, column }) => {
      const resolvedPath = this.resolvePath(file);
      const info = await this.engine.hover(resolvedPath, line, column);

      if (!info) {
        return {
          content: [{
            type: "text",
            text: "No information available for the symbol at the specified location."
          }]
        };
      }

      let response = `**${info.name}** (${info.kind})\n`;
      if (info.signature) response += `\nSignature: \`${info.signature}\``;
      if (info.type) response += `\nType: \`${info.type}\``;
      if (info.documentation) response += `\n\nDocumentation:\n${info.documentation}`;
      if (info.location) response += `\n\nDefined at: ${info.location.file}:${info.location.line}:${info.location.column}`;

      return {
        content: [{
          type: "text",
          text: response
        }]
      };
    });

    // Get call hierarchy
    this.server.registerTool("get_call_hierarchy", {
      title: "Call Hierarchy",
      description: "Get incoming or outgoing call hierarchy for a function/method",
      inputSchema: {
        file: z.string().describe("File path (absolute or relative to workspace)"),
        line: z.number().describe("Line number (1-based)"),
        column: z.number().describe("Column number (0-based)"),
        direction: z.enum(["incoming", "outgoing"]).describe("Direction: incoming (callers) or outgoing (callees)")
      }
    }, async ({ file, line, column, direction }) => {
      const resolvedPath = this.resolvePath(file);
      const hierarchy = await this.engine.getCallHierarchy(resolvedPath, line, column, direction);

      if (hierarchy.length === 0) {
        return {
          content: [{
            type: "text",
            text: `No ${direction} calls found for the symbol at the specified location.`
          }]
        };
      }

      return {
        content: [{
          type: "text",
          text: `${direction === 'incoming' ? 'Callers' : 'Callees'} (${hierarchy.length}):\n\n` +
                hierarchy.map(item =>
                  `${'  '.repeat(item.level)}${item.symbol.name} (${item.symbol.kind}) - ${item.symbol.filePath}:${item.symbol.startLine}`
                ).join('\n')
        }]
      };
    });

    // Find cross-language bindings
    this.server.registerTool("find_cross_language_bindings", {
      title: "Cross-Language Bindings",
      description: "Find API calls and cross-language bindings in a file",
      inputSchema: {
        file: z.string().describe("File path to analyze for cross-language bindings")
      }
    }, async ({ file }) => {
      const resolvedPath = this.resolvePath(file);
      const bindings = await this.engine.findCrossLanguageBindings(resolvedPath);

      if (bindings.length === 0) {
        return {
          content: [{
            type: "text",
            text: "No cross-language bindings found in the specified file."
          }]
        };
      }

      return {
        content: [{
          type: "text",
          text: `Found ${bindings.length} cross-language bindings:\n\n` +
                bindings.map(binding =>
                  `- ${binding.binding_kind}: ${binding.source_name} (${binding.source_language}) → ${binding.target_name || 'external'} (${binding.target_language || 'unknown'})`
                ).join('\n')
        }]
      };
    });

    // Index workspace
    this.server.registerTool("index_workspace", {
      title: "Index Workspace",
      description: "Index or reindex a workspace directory for code intelligence",
      inputSchema: {
        path: z.string().optional().describe("Workspace path to index (default: current directory)"),
        force: z.boolean().default(false).describe("Force reindexing even if files haven't changed")
      }
    }, async ({ path = this.workspacePath, force = false }) => {
      const resolvedPath = this.resolvePath(path);
      await this.engine.indexWorkspace(resolvedPath);

      return {
        content: [{
          type: "text",
          text: `Workspace indexed successfully: ${resolvedPath}`
        }]
      };
    });

    // Get workspace stats
    this.server.registerTool("get_workspace_stats", {
      title: "Workspace Statistics",
      description: "Get statistics about the indexed workspace (files, symbols, etc.)",
      inputSchema: {}
    }, async () => {
      const stats = this.engine.getStats();

      const response = `**Workspace Statistics**

**Database:**
- Symbols: ${stats.database.symbols}
- Files: ${stats.database.files}
- Relationships: ${stats.database.relationships}

**Parser:**
- Initialized: ${stats.parser.initialized}
- Loaded Languages: ${stats.parser.loadedLanguages}
- Supported Extensions: ${stats.parser.supportedExtensions}

**Search:**
- Total Symbols: ${stats.search.totalSymbols}
- Indexed Documents: ${stats.search.indexedDocuments}
- Is Indexed: ${stats.search.isIndexed}

**File Watcher:**
- Watched Paths: ${stats.watcher.watchedPaths}
- Pending Updates: ${stats.watcher.pendingUpdates}
- Processing Files: ${stats.watcher.processingFiles}

**Extractors:**
- Registered: ${stats.extractors.registered}
- Languages: ${stats.extractors.languages.join(', ')}

**Engine Status:** ${stats.isInitialized ? 'Initialized' : 'Not Initialized'}`;

      return {
        content: [{
          type: "text",
          text: response
        }]
      };
    });

    // Health check
    this.server.registerTool("health_check", {
      title: "Health Check",
      description: "Check the health status of the code intelligence engine",
      inputSchema: {}
    }, async () => {
      const health = await this.engine.healthCheck();

      return {
        content: [{
          type: "text",
          text: `**Health Status:** ${health.status}\n\n` +
                `**Details:**\n${JSON.stringify(health.details, null, 2)}`
        }]
      };
    });
  }

  private resolvePath(filePath: string): string {
    // Convert relative paths to absolute paths relative to workspace
    if (!filePath.startsWith('/')) {
      return require('path').resolve(this.workspacePath, filePath);
    }
    return filePath;
  }

  async start() {
    try {
      console.log('Starting Miller MCP Server...');

      // Initialize the code intelligence engine
      await this.engine.initialize();

      // Auto-index current workspace on startup
      console.log(`Auto-indexing workspace: ${this.workspacePath}`);
      await this.engine.indexWorkspace(this.workspacePath);

      // Connect to stdio transport
      const transport = new StdioServerTransport();
      await this.server.connect(transport);

      console.log("Miller MCP Server is running and ready to serve code intelligence!");
      console.log(`Workspace: ${this.workspacePath}`);

      // Log initial stats
      const stats = this.engine.getStats();
      console.log(`Indexed ${stats.database.symbols} symbols from ${stats.database.files} files`);

    } catch (error) {
      console.error('Failed to start Miller MCP Server:', error);
      process.exit(1);
    }
  }

  async shutdown() {
    console.log('Shutting down Miller MCP Server...');
    await this.engine.dispose();
    console.log('Miller MCP Server shutdown complete');
  }
}

// Handle graceful shutdown
process.on('SIGINT', async () => {
  console.log('\nReceived SIGINT, shutting down gracefully...');
  if (globalThis.server) {
    await globalThis.server.shutdown();
  }
  process.exit(0);
});

process.on('SIGTERM', async () => {
  console.log('Received SIGTERM, shutting down gracefully...');
  if (globalThis.server) {
    await globalThis.server.shutdown();
  }
  process.exit(0);
});

// Start the server
const server = new MillerMCPServer();
globalThis.server = server;

server.start().catch(error => {
  console.error('Fatal error:', error);
  process.exit(1);
});