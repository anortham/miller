#!/usr/bin/env bun

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
  ToolSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { z } from "zod";
import { CodeIntelligenceEngine } from './engine/code-intelligence.js';
import { MillerPaths } from './utils/miller-paths.js';
import { initializeLogger, log, LogLevel } from './utils/logger.js';

class MillerMCPServer {
  private server: Server;
  private engine: CodeIntelligenceEngine;
  private workspacePath: string = process.cwd();
  private paths: MillerPaths;

  constructor() {
    // Initialize paths and logger first
    this.paths = new MillerPaths(this.workspacePath);
    initializeLogger(this.paths, LogLevel.INFO);

    this.engine = new CodeIntelligenceEngine({
      workspacePath: this.workspacePath,
      enableWatcher: true,
      watcherDebounceMs: 300,
      batchSize: 50
    });

    this.server = new Server({
      name: "miller",
      version: "1.0.0",
    }, {
      capabilities: {
        tools: {},
      },
      instructions: `Miller is a high-performance code intelligence server supporting 17 programming languages with LSP-like capabilities.

## Supported Languages (17)
**Web**: JavaScript, TypeScript, HTML, CSS, Vue SFCs
**Backend**: Python, Rust, Go, Java, C#, PHP, Ruby
**Systems**: C, C++
**Mobile**: Swift, Kotlin
**Frameworks**: Razor (Blazor)
**Utilities**: Regex patterns

## Core Capabilities

### Code Search & Navigation
- **search_code**: Fuzzy and exact search across all languages with symbol filtering
- **goto_definition**: Jump to symbol declarations with precise location
- **find_references**: Locate all symbol usages across the codebase
- **get_hover_info**: Get type information and documentation for symbols

### Advanced Analysis
- **get_call_hierarchy**: Explore caller/callee relationships
- **find_cross_language_bindings**: Track API calls between different languages
- **get_workspace_stats**: View indexing statistics and language breakdown

### Workspace Management
- **index_workspace**: Index or reindex a directory for code intelligence
- **health_check**: Verify server status and performance metrics

## Usage Patterns

**For Search**: Use search_code with specific queries, enable includeSignature for detailed results
**For Navigation**: Use goto_definition and find_references for code exploration
**For Analysis**: Use call_hierarchy for understanding code flow
**For Setup**: Always run index_workspace on new directories before using other features

## Performance
- Supports large codebases (100k+ symbols)
- Sub-second search (<10ms fuzzy, <50ms exact)
- Real-time incremental updates via file watching
- Memory efficient (~100MB for 10k files)

Miller automatically indexes supported files and provides intelligent code analysis across language boundaries.`
    });

    this.setupHandlers();
  }

  private setupHandlers() {
    this.server.setRequestHandler(ListToolsRequestSchema, async () => {
      return {
        tools: [
          {
            name: "search_code",
            description: "Search for code symbols, functions, classes, etc. using fuzzy matching",
            inputSchema: {
              type: "object",
              properties: {
                query: {
                  type: "string",
                  description: "Search query (symbol name, partial name, etc.)"
                },
                type: {
                  type: "string",
                  enum: ["fuzzy", "exact", "type"],
                  description: "Search type: fuzzy (default), exact pattern, or by type name",
                  default: "fuzzy"
                },
                limit: {
                  type: "number",
                  description: "Maximum number of results (default: 50)",
                  default: 50
                },
                language: {
                  type: "string",
                  description: "Filter by programming language (optional)"
                },
                symbolKinds: {
                  type: "array",
                  items: { type: "string" },
                  description: "Filter by symbol kinds (class, function, variable, etc.)"
                },
                includeSignature: {
                  type: "boolean",
                  description: "Include function/method signatures in results",
                  default: true
                },
                path: {
                  type: "string",
                  description: "Workspace path to search (default: current workspace, 'all' for all indexed workspaces)"
                }
              },
              required: ["query"]
            }
          } satisfies ToolSchema,
          {
            name: "goto_definition",
            description: "Find the definition of a symbol at a specific location",
            inputSchema: {
              type: "object",
              properties: {
                file: {
                  type: "string",
                  description: "File path (absolute or relative to workspace)"
                },
                line: {
                  type: "number",
                  description: "Line number (1-based)"
                },
                column: {
                  type: "number",
                  description: "Column number (0-based)"
                }
              },
              required: ["file", "line", "column"]
            }
          } satisfies ToolSchema,
          {
            name: "find_references",
            description: "Find all references to a symbol at a specific location",
            inputSchema: {
              type: "object",
              properties: {
                file: {
                  type: "string",
                  description: "File path (absolute or relative to workspace)"
                },
                line: {
                  type: "number",
                  description: "Line number (1-based)"
                },
                column: {
                  type: "number",
                  description: "Column number (0-based)"
                }
              },
              required: ["file", "line", "column"]
            }
          } satisfies ToolSchema,
          {
            name: "get_hover_info",
            description: "Get detailed information about a symbol (type, documentation, signature)",
            inputSchema: {
              type: "object",
              properties: {
                file: {
                  type: "string",
                  description: "File path (absolute or relative to workspace)"
                },
                line: {
                  type: "number",
                  description: "Line number (1-based)"
                },
                column: {
                  type: "number",
                  description: "Column number (0-based)"
                }
              },
              required: ["file", "line", "column"]
            }
          } satisfies ToolSchema,
          {
            name: "get_call_hierarchy",
            description: "Get incoming or outgoing call hierarchy for a function/method",
            inputSchema: {
              type: "object",
              properties: {
                file: {
                  type: "string",
                  description: "File path (absolute or relative to workspace)"
                },
                line: {
                  type: "number",
                  description: "Line number (1-based)"
                },
                column: {
                  type: "number",
                  description: "Column number (0-based)"
                },
                direction: {
                  type: "string",
                  enum: ["incoming", "outgoing"],
                  description: "Direction: incoming (callers) or outgoing (callees)"
                }
              },
              required: ["file", "line", "column", "direction"]
            }
          } satisfies ToolSchema,
          {
            name: "find_cross_language_bindings",
            description: "Find API calls and cross-language bindings in a file",
            inputSchema: {
              type: "object",
              properties: {
                file: {
                  type: "string",
                  description: "File path to analyze for cross-language bindings"
                }
              },
              required: ["file"]
            }
          } satisfies ToolSchema,
          {
            name: "index_workspace",
            description: "Index or reindex a workspace directory for code intelligence",
            inputSchema: {
              type: "object",
              properties: {
                path: {
                  type: "string",
                  description: "Workspace path to index (default: current directory)"
                },
                force: {
                  type: "boolean",
                  description: "Force reindexing even if files haven't changed",
                  default: false
                }
              }
            }
          } satisfies ToolSchema,
          {
            name: "get_workspace_stats",
            description: "Get statistics about the indexed workspace (files, symbols, etc.)",
            inputSchema: {
              type: "object",
              properties: {}
            }
          } satisfies ToolSchema,
          {
            name: "health_check",
            description: "Check the health status of the code intelligence engine",
            inputSchema: {
              type: "object",
              properties: {}
            }
          } satisfies ToolSchema
        ]
      };
    });

    this.server.setRequestHandler(CallToolRequestSchema, async (request) => {
      const { name, arguments: args } = request.params;

      try {
        switch (name) {
          case "search_code": {
            const { query, type = "fuzzy", limit = 50, language, symbolKinds, includeSignature = true, path } = args;

            // Handle path filtering - default to current workspace
            const searchPath = path === "all" ? undefined : path || this.workspacePath;

            let results;
            if (type === 'exact') {
              results = await this.engine.searchExact(query, { limit, language, symbolKinds, path: searchPath });
            } else if (type === 'type') {
              results = await this.engine.searchByType(query, { limit, language, path: searchPath });
            } else {
              results = await this.engine.searchCode(query, {
                limit,
                language,
                symbolKinds,
                includeSignature,
                path: searchPath
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
          }

          case "goto_definition": {
            const { file, line, column } = args;
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
          }

          case "find_references": {
            const { file, line, column } = args;
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
          }

          case "get_hover_info": {
            const { file, line, column } = args;
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
          }

          case "get_call_hierarchy": {
            const { file, line, column, direction } = args;
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
          }

          case "find_cross_language_bindings": {
            const { file } = args;
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
          }

          case "index_workspace": {
            const { path = this.workspacePath, force = false } = args;
            const resolvedPath = this.resolvePath(path);

            await this.engine.indexWorkspace(resolvedPath);

            return {
              content: [{
                type: "text",
                text: `Workspace indexed successfully: ${resolvedPath}`
              }]
            };
          }

          case "get_workspace_stats": {
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
          }

          case "health_check": {
            const health = await this.engine.healthCheck();

            return {
              content: [{
                type: "text",
                text: `**Health Status:** ${health.status}\n\n` +
                      `**Details:**\n${JSON.stringify(health.details, null, 2)}`
              }]
            };
          }

          default:
            throw new Error(`Unknown tool: ${name}`);
        }
      } catch (error) {
        log.tool(name, false, undefined, error instanceof Error ? error : new Error(String(error)));

        return {
          content: [{
            type: "text",
            text: `Error: ${error instanceof Error ? error.message : 'Unknown error occurred'}`
          }]
        };
      }
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
      log.lifecycle('startup', 'Starting Miller MCP Server...');

      // Initialize the code intelligence engine
      await this.engine.initialize();

      // Auto-index current workspace on startup
      log.lifecycle('startup', `Auto-indexing workspace: ${this.workspacePath}`);
      await this.engine.indexWorkspace(this.workspacePath);

      // Connect to stdio transport
      const transport = new StdioServerTransport();
      await this.server.connect(transport);

      log.lifecycle('ready', "Miller MCP Server is running and ready to serve code intelligence!", {
        workspace: this.workspacePath,
        millerDir: this.paths.getMillerDir()
      });

      // Log initial stats
      const stats = this.engine.getStats();
      log.lifecycle('ready', `Indexed ${stats.database.symbols} symbols from ${stats.database.files} files`, stats);

    } catch (error) {
      log.error('MCP', 'Failed to start Miller MCP Server', error);
      process.exit(1);
    }
  }

  async shutdown() {
    log.lifecycle('shutdown', 'Shutting down Miller MCP Server...');
    await this.engine.dispose();
    await log.flush(); // Ensure all logs are written before shutdown
    log.lifecycle('shutdown', 'Miller MCP Server shutdown complete');
  }
}

// Handle graceful shutdown
process.on('SIGINT', async () => {
  log.lifecycle('shutdown', 'Received SIGINT, shutting down gracefully...');
  if (globalThis.server) {
    await globalThis.server.shutdown();
  }
  process.exit(0);
});

process.on('SIGTERM', async () => {
  log.lifecycle('shutdown', 'Received SIGTERM, shutting down gracefully...');
  if (globalThis.server) {
    await globalThis.server.shutdown();
  }
  process.exit(0);
});

// Start the server
const server = new MillerMCPServer();
globalThis.server = server;

server.start().catch(error => {
  log.error('MCP', 'Fatal error during server startup', error);
  process.exit(1);
});
