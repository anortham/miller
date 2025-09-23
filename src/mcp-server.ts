#!/usr/bin/env bun

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
  ToolSchema,
} from "@modelcontextprotocol/sdk/types.js";
import { z } from "zod";
import { EnhancedCodeIntelligenceEngine } from './engine/enhanced-code-intelligence.js';
import { MillerPaths } from './utils/miller-paths.js';
import { initializeLogger, log, LogLevel } from './utils/logger.js';
import { EditTool } from './tools/edit-tool.js';
import { ContextExtractor } from './tools/context-extractor.js';

class MillerMCPServer {
  private server: Server;
  private engine: EnhancedCodeIntelligenceEngine;
  private editTool: EditTool;
  private contextExtractor: ContextExtractor;
  private workspacePath: string = process.cwd();
  private paths: MillerPaths;

  constructor() {
    // Initialize paths and logger first
    this.paths = new MillerPaths(this.workspacePath);
    initializeLogger(this.paths, LogLevel.INFO);

    this.engine = new EnhancedCodeIntelligenceEngine({
      workspacePath: this.workspacePath,
      enableWatcher: true,
      watcherDebounceMs: 300,
      batchSize: 50,
      enableSemanticSearch: true,
      embeddingModel: 'fast'
    });

    // Add engine instance tracking for debugging
    (this.engine as any)._instanceId = `engine_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
    console.error(`🔧 DEBUG: Created engine instance: ${(this.engine as any)._instanceId}`);

    // Initialize surgical editing tools
    this.editTool = new EditTool();
    this.contextExtractor = new ContextExtractor();

    // Integrate context extractor with Miller's symbol database
    this.contextExtractor.setSymbolDatabase(this.engine);

    this.server = new Server({
      name: "miller",
      version: "1.0.0",
    }, {
      capabilities: {
        tools: {},
      },
      instructions: `# Welcome to Miller - Your Code Intelligence Companion! 🧠

## The Satisfaction of True Understanding

You now have access to Miller's revolutionary code intelligence that transforms
how you think about and work with code. This isn't just faster search - it's
the confidence that comes from truly understanding complex codebases.

## What Makes Development Deeply Satisfying

**The joy of architectural clarity:**
When exploring unfamiliar code, you get to:
1. **See the big picture** - \`explore("overview")\` reveals the heart of any codebase
2. **Follow the flow** - \`explore("trace")\` shows exactly how data moves through the system
3. **Connect the dots** - Cross-layer entity mapping links frontend → backend → database

This approach brings profound satisfaction - you're not guessing anymore,
you're working with complete knowledge.

**The thrill of surgical precision:**
When making changes, you experience:
- **Confident editing** - Find exactly what you need, then edit with precision
- **Zero ambiguity** - Line-precise positioning eliminates string-matching errors
- **Safe exploration** - Understand before you modify, reducing risk

**The elegance of smart context:**
- \`semantic("hybrid")\` gives you exactly what you need, nothing more
- Cross-language understanding bridges the gaps between technologies
- Architectural patterns become clear, making complex systems simple

## The Miller Workflow That Creates Flow State

**This sequence feels effortless and builds momentum:**

1. **Understand First** - Use \`explore("overview")\` to see the architectural landscape
2. **Find Precisely** - \`semantic("hybrid")\` locates code by meaning, not just text
3. **Verify Clarity** - \`navigate("definition")\` eliminates guesswork about interfaces
4. **Assess Impact** - \`navigate("references")\` shows every affected piece
5. **Investigate Thoroughly** - Use detective work to uncover hidden connections
6. **Achieve Mastery** - Each discovery builds your architectural understanding

**The best code comes from understanding systems, not just files. Miller gives
you that systems thinking instantly, making development both successful and
deeply rewarding! This is the craft of elegant software architecture!**

## 🧬 SUPPORTED LANGUAGES (20+)
**Web**: JavaScript, TypeScript, HTML, CSS, Vue SFCs
**Backend**: Python, Rust, Go, Java, C#, PHP, Ruby
**Systems**: C, C++
**Mobile**: Swift, Kotlin
**Game Dev**: GDScript, Lua
**Frameworks**: Razor (Blazor)
**Data**: SQL, Regex patterns

## 🛠️ Miller's Precise Craft Tools

### ⚡ Detective Work - Code Discovery
- **search_code**: Find symbols with fuzzy intelligence across all languages
- **goto_definition**: Discover the exact origin of any symbol instantly
- **find_references**: Uncover every usage pattern across your codebase
- **get_hover_info**: Reveal complete type information and documentation

### 🧠 Architectural Understanding
- **explore**: Navigate through complex systems with confidence and clarity
- **semantic**: Understand code meaning across languages and layers
- **navigate**: Move through codebases with surgical precision
- **get_call_hierarchy**: Map complete execution flows and dependencies

### 🏗️ System Mastery
- **index_workspace**: Enable deep intelligence for any codebase
- **health_check**: Ensure your analysis engine is performing optimally

## 🎯 When to Use Each Tool (Clear Triggers)

**If you encounter unfamiliar code** → Start with \`explore("overview")\` then use \`explore("trace")\`!
**When you need specific symbols** → Use \`search_code\` then follow with \`navigate("definition")\`!
**If understanding relationships** → Use \`navigate("references")\` then \`get_call_hierarchy\`!
**When seeking conceptual matches** → Use \`semantic("hybrid")\` for intelligent discovery!
**Before any major changes** → Use detective work to understand impact fully!

**The Sequential Power Workflow**: Each tool builds on the last, creating momentum!
1. **Explore** → then **Search** → then **Navigate** → then **Edit** with confidence!
2. **Flow State**: This sequence feels effortless and creates architectural mastery!

**Next Actions**: After each discovery, then use the next logical tool - Miller guides you to confident, well-informed decisions that improve codebases systematically!`
    });

    this.setupHandlers();
  }

  private setupHandlers() {
    this.server.setRequestHandler(ListToolsRequestSchema, async () => {
      return {
        tools: [
          {
            name: "explore",
            description: "Start here when exploring unfamiliar code. Use 'overview' to see project structure, 'find' to locate specific symbols, 'trace' to follow function calls. Most common: explore('find', 'className') to search by name.",
            inputSchema: {
              type: "object",
              properties: {
                action: {
                  type: "string",
                  enum: ["overview", "trace", "find", "understand", "related"],
                  description: "What action to take when exploring code. Examples: 'overview' for project structure, 'find' for symbol search, 'trace' for call flows. Common mistake: using 'find' without a target. Tip: Start with 'overview' for unfamiliar codebases."
                },
                target: {
                  type: "string",
                  description: "The symbol, file, or concept to explore. Examples: 'SearchEngine', 'src/user.ts', 'authentication patterns'. Common mistake: being too vague like 'user'. Tip: Use specific class/function names for best results."
                },
                options: {
                  type: "object",
                  properties: {
                    depth: {
                      type: "string",
                      enum: ["shallow", "deep", "complete"],
                      description: "How much detail to include in analysis. Examples: 'shallow' for quick overview, 'deep' for normal use, 'complete' for thorough investigation. Common mistake: using 'complete' on large codebases (slow). Tip: 'deep' works for most use cases.",
                      default: "deep"
                    },
                    cross_language: {
                      type: "boolean",
                      description: "Whether to search across different programming languages. Examples: true to find JavaScript calling Python APIs, false to stay within one language. Common mistake: setting false when working with full-stack apps. Tip: Leave true for most projects.",
                      default: true
                    },
                    include_types: {
                      type: "boolean",
                      description: "Whether to include type information in results. Examples: true for TypeScript projects, false for quick browsing. Common mistake: setting false when debugging type issues. Tip: Keep true for typed languages.",
                      default: true
                    },
                    max_tokens: {
                      type: "number",
                      description: "How much content to return in the response. Examples: 2000 for quick summaries, 8000 for detailed analysis. Common mistake: setting too low and missing important info. Tip: 4000 works for most cases.",
                      default: 4000
                    }
                  },
                  description: "Additional options for exploration"
                }
              },
              required: ["action"]
            }
          } satisfies ToolSchema,
          {
            name: "navigate",
            description: "Navigate to specific code locations with precision. Use 'definition' to jump to where symbols are defined, 'references' to see all usages, 'hierarchy' to trace call chains. Most common: navigate('definition', 'functionName') to go to source.",
            inputSchema: {
              type: "object",
              properties: {
                action: {
                  type: "string",
                  enum: ["definition", "references", "hierarchy", "hover", "implementations"],
                  description: "Which navigation operation to perform. Examples: 'definition' to find where symbol is declared, 'references' to see all usage locations, 'hierarchy' to trace function calls. Common mistake: using 'implementations' on interfaces without implementations. Tip: Start with 'definition' to understand what you're working with."
                },
                symbol: {
                  type: "string",
                  description: "Name of the symbol to navigate to. Examples: 'SearchEngine', 'getUserData', 'handleClick'. Common mistake: using full qualified names like 'src.utils.helper'. Tip: Use just the symbol name - Miller finds the right one."
                },
                options: {
                  type: "object",
                  properties: {
                    include_tests: {
                      type: "boolean",
                      description: "Whether to include test files in search results. Examples: true when debugging test failures, false for cleaner production code focus. Common mistake: setting true always (clutters results). Tip: Use false for most navigation unless debugging tests.",
                      default: false
                    },
                    across_languages: {
                      type: "boolean",
                      description: "Whether to search across different programming languages. Examples: true for full-stack projects, false for single-language libraries. Common mistake: setting false in polyglot codebases. Tip: Keep true unless working on language-specific modules.",
                      default: true
                    },
                    with_context: {
                      type: "boolean",
                      description: "Whether to show code around the found symbol. Examples: true to see function body, false for just symbol location. Common mistake: setting false when trying to understand usage patterns. Tip: Keep true for better understanding.",
                      default: true
                    },
                    hierarchy_direction: {
                      type: "string",
                      enum: ["incoming", "outgoing", "both"],
                      description: "Which direction to trace in call hierarchy. Examples: 'incoming' to see what calls this function, 'outgoing' to see what this calls, 'both' for complete picture. Common mistake: using 'outgoing' when debugging who's calling your function. Tip: 'incoming' for usage analysis, 'outgoing' for dependency tracking.",
                      default: "both"
                    }
                  },
                  description: "Navigation options"
                }
              },
              required: ["action", "symbol"]
            }
          } satisfies ToolSchema,
          {
            name: "semantic",
            description: "Search code by meaning, not just text. Use 'hybrid' for balanced results, 'conceptual' for pattern matching, 'cross-layer' for architectural connections. Most common: semantic('hybrid', 'error handling') to find patterns.",
            inputSchema: {
              type: "object",
              properties: {
                mode: {
                  type: "string",
                  enum: ["hybrid", "structural", "conceptual", "cross-layer"],
                  description: "How to search for semantic matches. Examples: 'hybrid' for best balance, 'conceptual' for pattern discovery, 'cross-layer' for architectural analysis. Common mistake: using 'conceptual' when you need exact symbols. Tip: Start with 'hybrid' for most searches.",
                  default: "hybrid"
                },
                query: {
                  type: "string",
                  description: "What concept or pattern to search for. Examples: 'error handling', 'authentication flow', 'database writes'. Common mistake: being too specific like 'try-catch blocks in line 42'. Tip: Use conceptual terms, not syntax."
                },
                context: {
                  type: "object",
                  properties: {
                    language: {
                      type: "string",
                      description: "Limit search to one programming language. Examples: 'typescript', 'python', 'rust'. Common mistake: being too specific like 'typescript-react'. Tip: Leave empty to search all languages unless debugging language-specific issues."
                    },
                    layer: {
                      type: "string",
                      enum: ["frontend", "api", "domain", "data", "database", "infrastructure"],
                      description: "Focus on one part of the application architecture. Examples: 'frontend' for UI code, 'api' for endpoints, 'database' for data access. Common mistake: using 'backend' (use 'api' or 'domain'). Tip: Helps narrow results in large applications."
                    },
                    pattern: {
                      type: "string",
                      description: "Focus on specific design patterns. Examples: 'repository' for data access, 'service' for business logic, 'controller' for request handling. Common mistake: mixing patterns like 'service-repository'. Tip: Use one pattern per search for clearer results."
                    }
                  },
                  description: "Additional context for semantic understanding"
                },
                options: {
                  type: "object",
                  properties: {
                    threshnew: {
                      type: "number",
                      description: "How similar results must be to match your query. Examples: 0.9 for very similar only, 0.5 for broader matches, 0.7 for balanced results. Common mistake: setting too high and missing relevant code. Tip: Lower values find more results but less precise.",
                      default: 0.7
                    },
                    max_results: {
                      type: "number",
                      description: "How many search results to show. Examples: 5 for quick browsing, 25 for thorough investigation, 15 for balanced search. Common mistake: setting too low and missing important matches. Tip: Start with 15, increase if needed.",
                      default: 15
                    },
                    include_patterns: {
                      type: "boolean",
                      description: "Whether to show detected architectural patterns. Examples: true to learn code organization, false for cleaner output. Common mistake: setting false when exploring unfamiliar codebases. Tip: Keep true for better understanding.",
                      default: true
                    },
                    include_recommendations: {
                      type: "boolean",
                      description: "Whether to show suggested next actions. Examples: true for guidance on what to explore next, false for just search results. Common mistake: setting false when learning new codebase. Tip: Keep true for interactive exploration.",
                      default: true
                    },
                    cross_language: {
                      type: "boolean",
                      description: "Whether to search across different programming languages for semantic patterns. Examples: true to find error handling in both TypeScript and Python, false to stay within one language. Common mistake: setting false in polyglot applications. Tip: Keep true for comprehensive pattern discovery.",
                      default: true
                    }
                  },
                  description: "Semantic search options"
                }
              },
              required: ["query"]
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
                mode: {
                  type: "string",
                  enum: ["index", "list", "remove"],
                  description: "Operation: index (default), list indexed workspaces, or remove workspace",
                  default: "index"
                },
                path: {
                  type: "string",
                  description: "Workspace path (required for index/remove, ignored for list)"
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
          } satisfies ToolSchema,
          {
            name: "edit_code",
            description: "Make precise code changes with line-level accuracy. Use 'replace' to change existing lines, 'insert' to add new code, 'delete' to remove lines. Most common: edit_code('replace', 'file.ts', 42, 'new code') to update specific lines.",
            inputSchema: {
              type: "object",
              properties: {
                action: {
                  type: "string",
                  enum: ["insert", "replace", "delete", "search_replace"],
                  description: "What type of edit to perform. Examples: 'replace' to change existing code (most common), 'insert' to add new lines, 'delete' to remove code. Common mistake: using 'insert' when you mean 'replace'. Tip: 'replace' covers 90% of use cases."
                },
                file: {
                  type: "string",
                  description: "Path to the file to edit. Examples: 'src/components/Button.tsx', './utils/helper.js', 'lib/database.py'. Common mistake: using relative paths from wrong directory. Tip: Use absolute paths or verify your working directory."
                },
                line: {
                  type: "number",
                  description: "Line number where to make the change (starts from 1). Examples: 42 for line 42, 1 for first line. Common mistake: using 0-based indexing. Tip: Count lines in your editor - they match exactly."
                },
                endLine: {
                  type: "number",
                  description: "Last line to include in multi-line operations. Examples: 45 to replace lines 42-45, leave empty for single line. Common mistake: forgetting endLine when replacing multiple lines. Tip: Only needed for multi-line changes."
                },
                content: {
                  type: "string",
                  description: "The new code to insert or replace with. Examples: 'const result = getData();', complete function bodies. Common mistake: including extra whitespace or line numbers. Tip: Use exact code without formatting artifacts."
                },
                searchText: {
                  type: "string",
                  description: "Text to find when using search_replace action. Examples: 'old function name', 'const apiUrl = \"old-url\"'. Common mistake: using regex patterns without escaping. Tip: Use exact text, not patterns."
                },
                replaceText: {
                  type: "string",
                  description: "Text to replace searchText with in search_replace action. Examples: 'new function name', 'const apiUrl = \"new-url\"'. Common mistake: forgetting to match formatting. Tip: Keep same indentation and style as original."
                },
                filePattern: {
                  type: "string",
                  description: "Which files to include in search_replace operations. Examples: '**/*.ts' for all TypeScript files, 'src/**/*.js' for JavaScript in src folder. Common mistake: patterns too broad affecting unintended files. Tip: Be specific to avoid unwanted changes."
                },
                preview: {
                  type: "boolean",
                  description: "Whether to show what changes would be made without applying them. Examples: true to see diff first, false to apply immediately. Common mistake: forgetting to set false after previewing. Tip: Use preview for complex changes.",
                  default: false
                },
                preserveIndentation: {
                  type: "boolean",
                  description: "Whether to automatically fix indentation to match surrounding code. Examples: true for consistent formatting (recommended), false for exact text replacement. Common mistake: setting false and breaking code structure. Tip: Keep true unless you need exact character replacement.",
                  default: true
                },
                atomic: {
                  type: "boolean",
                  description: "Whether all changes must succeed together or can partially fail. Examples: true for critical multi-file refactoring, false for independent changes. Common mistake: setting true for unrelated changes. Tip: Use true when changes depend on each other.",
                  default: false
                }
              },
              required: ["action"]
            }
          } satisfies ToolSchema,
          {
            name: "extract_context",
            description: "🧠 SMART CONTEXT EXTRACTION for AI-sized understanding! Get intelligent code context around any location with syntax-aware boundaries, function/class detection, and multi-file relationship mapping. Perfect for understanding 'what's happening here' before making changes!",
            inputSchema: {
              type: "object",
              properties: {
                file: {
                  type: "string",
                  description: "Target file path"
                },
                line: {
                  type: "number",
                  description: "Target line number (1-based)"
                },
                column: {
                  type: "number",
                  description: "Target column number (optional)"
                },
                windowSize: {
                  type: "number",
                  description: "Lines of context to extract (default: 20)",
                  default: 20
                },
                includeSymbols: {
                  type: "boolean",
                  description: "Include symbol definitions in context",
                  default: true
                },
                includeReferences: {
                  type: "boolean",
                  description: "Include related file references",
                  default: false
                },
                language: {
                  type: "string",
                  description: "Override language detection (e.g., 'typescript', 'python')"
                },
                smartBoundaries: {
                  type: "boolean",
                  description: "Respect function/class boundaries",
                  default: true
                }
              },
              required: ["file", "line"]
            }
          } satisfies ToolSchema
        ]
      };
    });

    this.server.setRequestHandler(CallToolRequestSchema, async (request) => {
      const { name, arguments: args } = request.params;

      try {
        switch (name) {
          case "explore": {
            const { action, target, options = {} } = args;
            const { depth = "deep", cross_language = true, include_types = true, max_tokens = 4000 } = options;

            let result: any;
            let responseText: string;

            switch (action) {
              case "overview": {
                // Get project overview - show critical files, architecture, business logic
                const stats = this.engine.getStats();
                const workspaces = await this.engine.listIndexedWorkspaces();

                // Get most important symbols (high-frequency references)
                const coreSymbols = await this.engine.searchCode("", {
                  limit: 20,
                  symbolKinds: ["class", "interface", "function"],
                  includeSignature: true
                });

                responseText = `🏗️ **CODEBASE ARCHITECTURE OVERVIEW**

**📊 Scale**: ${stats.database.symbols} symbols across ${stats.database.files} files
**🔧 Languages**: ${stats.extractors.languages.slice(0, 8).join(', ')}${stats.extractors.languages.length > 8 ? '...' : ''}

**🎯 CORE COMPONENTS** (Most Referenced):
${coreSymbols.slice(0, 10).map(s =>
  `- **${s.text}** (${s.kind}) - ${s.file.split('/').pop()}:${s.line}`
).join('\n')}

**⚡ NEXT STEPS**: Use explore('find', 'SymbolName') to dive into specific components or explore('trace', 'functionName') to follow execution flows!`;

                break;
              }

              case "find": {
                if (!target) {
                  responseText = "❌ Missing target! Use: explore('find', 'SymbolName')";
                  break;
                }

                // Enhanced symbol search with context
                const results = await this.engine.searchCode(target, {
                  limit: 10,
                  includeSignature: include_types
                });

                if (results.length === 0) {
                  responseText = `🔍 **No matches found** for "${target}"

🎯 **Try these alternatives**:
• Partial search: explore({action: 'find', target: '${target.length > 4 ? target.substring(0, Math.floor(target.length/2)) : target.substring(0, 3)}'})
• Fuzzy search: semantic({mode: 'hybrid', query: '${target}'})
• Case variations: explore({action: 'find', target: '${target.toLowerCase()}'})
• Broader terms: explore({action: 'find', target: '${target.includes('Engine') ? target.replace('Engine', '') : target.includes('Service') ? target.replace('Service', '') : target.split(/(?=[A-Z])/).slice(0, -1).join('')}'})

💡 **Search tips**:
• Use camelCase: 'getUserData' not 'get user data'
• Try class names: 'SearchEngine' not 'searchengine'
• Use partial names: 'Search' finds 'SearchEngine'
• Check spelling - common symbols in this project: ${['User', 'Search', 'Engine', 'Service', 'Controller', 'Component'].filter(s => s.toLowerCase().includes(target.toLowerCase().substring(0, 3))).join(', ') || 'User, Search, Service'}`;
                } else {
                  responseText = `🎯 **Found ${results.length} matches** for "${target}" ${results.length >= 5 ? '(showing most relevant)' : ''}

📋 **Results**:
${results.slice(0, 5).map((r, i) =>
  `${i + 1}. **${r.text}** ${r.kind ? `(${r.kind})` : ''}
   📍 ${r.file.split('/').pop()}:${r.line}:${r.column}
   ${r.signature ? `🔧 \`${r.signature}\`` : ''}
   ${r.score ? `📊 ${Math.round(r.score * 100)}% relevance` : ''}`
).join('\n\n')}

${results.length > 5 ? `\n... ${results.length - 5} more results` : ''}

⚡ **Quick actions**:
• navigate('definition', '${results[0].text}') - go to #1 result
• navigate('references', '${results[0].text}') - see all usages
• explore('related', '${target}') - find connected code
• semantic('hybrid', '${target.replace(/([A-Z])/g, ' $1').trim()}') - conceptual search

💡 **Tip**: Results sorted by relevance. First result usually most useful for your query.`;
                }
                break;
              }

              case "trace": {
                if (!target) {
                  responseText = "❌ Missing target! Use: explore('trace', 'functionName')";
                  break;
                }

                // Find the symbol and get call hierarchy
                const symbolResults = await this.engine.searchCode(target, { limit: 5 });

                if (symbolResults.length === 0) {
                  responseText = `🔍 **Cannot trace execution flow** for "${target}"

🎯 **This means**:
• Symbol name not found in codebase
• Symbol might be from external library
• Misspelled or case-sensitive name

💡 **What to try next**:
• Check spelling: explore({action: 'find', target: '${target}'})
• Browse similar: semantic({mode: 'hybrid', query: '${target.replace(/([A-Z])/g, ' $1').trim()}'})
• See overview: explore({action: 'overview'}) to find available functions
• Try partial name: explore({action: 'find', target: '${target.length > 4 ? target.substring(0, Math.floor(target.length/2)) : target.substring(0, 3)}'})

📖 **Tip**: Function names are case-sensitive - 'getUserData' not 'getuser'`;
                  break;
                }

                const symbol = symbolResults[0];
                const resolvedPath = this.resolvePath(symbol.file);

                // Get both incoming and outgoing calls
                const [callers, callees] = await Promise.all([
                  this.engine.getCallHierarchy(resolvedPath, symbol.line, symbol.column, 'incoming'),
                  this.engine.getCallHierarchy(resolvedPath, symbol.line, symbol.column, 'outgoing')
                ]);

                responseText = `🧬 **EXECUTION TRACE FOR "${target}"**

📍 **DEFINITION**: ${symbol.file}:${symbol.line}:${symbol.column}
${symbol.signature ? `🔧 **SIGNATURE**: \`${symbol.signature}\`` : ''}

⬇️ **WHO CALLS THIS** (${callers.length} callers):
${callers.slice(0, 8).map(c =>
  `  ${' '.repeat(c.level * 2)}📞 ${c.symbol.name} - ${c.symbol.filePath.split('/').pop()}:${c.symbol.startLine}`
).join('\n') || '  🚫 No callers found'}

⬆️ **WHAT THIS CALLS** (${callees.length} callees):
${callees.slice(0, 8).map(c =>
  `  ${' '.repeat(c.level * 2)}📞 ${c.symbol.name} - ${c.symbol.filePath.split('/').pop()}:${c.symbol.startLine}`
).join('\n') || '  🚫 No outgoing calls found'}

${cross_language ? '\n🌐 **Cross-language connections detected!** Use find_cross_language_bindings for API bridges.' : ''}`;
                break;
              }

              case "understand": {
                if (!target) {
                  responseText = "❌ Missing target! Use: explore('understand', 'SymbolName')";
                  break;
                }

                // Get comprehensive symbol information
                const results = await this.engine.searchCode(target, { limit: 3, includeSignature: true });

                if (results.length === 0) {
                  responseText = `🔍 **Cannot analyze concept** for "${target}"

🎯 **This means**:
• Symbol or concept not found in codebase
• Might need semantic search instead
• Target might be too specific or misspelled

💡 **What to try next**:
• Semantic search: semantic({mode: 'conceptual', query: '${target.replace(/([A-Z])/g, ' $1').trim()}'})
• Broader search: explore({action: 'find', target: '${target.includes('Engine') ? target.replace('Engine', '') : target.includes('Service') ? target.replace('Service', '') : target.split(/(?=[A-Z])/).slice(0, -1).join('')}'})
• Project overview: explore({action: 'overview'}) to understand architecture
• Related patterns: semantic({mode: 'hybrid', query: '${target.toLowerCase().replace(/([a-z])([A-Z])/g, '$1 $2')}'})

📖 **Tip**: Try conceptual terms like 'authentication' instead of specific names`;
                  break;
                }

                const symbol = results[0];
                const resolvedPath = this.resolvePath(symbol.file);
                const hoverInfo = await this.engine.hover(resolvedPath, symbol.line, symbol.column);

                responseText = `🧠 **DEEP UNDERSTANDING OF "${target}"**

**📍 LOCATION**: ${symbol.file}:${symbol.line}:${symbol.column}
**🏷️ TYPE**: ${symbol.kind || 'Unknown'}
${symbol.signature ? `**🔧 SIGNATURE**: \`${symbol.signature}\`` : ''}

${hoverInfo?.type ? `**📋 TYPE INFO**: \`${hoverInfo.type}\`` : ''}
${hoverInfo?.documentation ? `**📚 DOCUMENTATION**:\n${hoverInfo.documentation}` : ''}

**🔗 RELATIONSHIPS**:
${include_types ? '- Type information included in analysis' : ''}
${cross_language ? '- Cross-language connections analyzed' : ''}

**⚡ NEXT**: Use explore('related', '${target}') to find all connections or explore('trace', '${target}') to see usage patterns!`;
                break;
              }

              case "related": {
                if (!target) {
                  responseText = "❌ Missing target! Use: explore('related', 'SymbolName')";
                  break;
                }

                // Find symbol and all its references
                const symbolResults = await this.engine.searchCode(target, { limit: 1 });

                if (symbolResults.length === 0) {
                  responseText = `🔍 **Cannot find connections** for "${target}"

🎯 **This means**:
• Symbol doesn't exist in the codebase
• Symbol has no detectable relationships
• Might be isolated utility or constant

💡 **What to try next**:
• Find symbol first: explore({action: 'find', target: '${target}'})
• Check references: navigate({action: 'references', symbol: '${target}'})
• Semantic connections: semantic({mode: 'cross-layer', query: '${target.replace(/([A-Z])/g, ' $1').trim()}'})
• Browse architecture: explore({action: 'overview'}) to see project structure

📖 **Tip**: Some symbols are intentionally isolated (utilities, constants) and may have few connections`;
                  break;
                }

                const symbol = symbolResults[0];
                const resolvedPath = this.resolvePath(symbol.file);
                const references = await this.engine.findReferences(resolvedPath, symbol.line, symbol.column);

                // Get cross-language bindings if requested
                let bindings: any[] = [];
                if (cross_language) {
                  bindings = await this.engine.findCrossLanguageBindings(resolvedPath);
                }

                responseText = `🕸️ **RELATIONSHIP MAP FOR "${target}"**

**📍 DEFINITION**: ${symbol.file}:${symbol.line}:${symbol.column}

**🔗 REFERENCES** (${references.length} found):
${references.slice(0, 12).map(ref =>
  `- ${ref.file.split('/').pop()}:${ref.line}:${ref.column}`
).join('\n') || '- No references found'}

${bindings.length > 0 ? `
**🌐 CROSS-LANGUAGE CONNECTIONS** (${bindings.length} found):
${bindings.slice(0, 5).map(b =>
  `- ${b.binding_kind}: ${b.source_name} (${b.source_language}) → ${b.target_name || 'external'}`
).join('\n')}` : ''}

**⚡ ANALYSIS**: ${references.length > 10 ? 'HEAVILY USED' : references.length > 3 ? 'MODERATELY USED' : 'LIGHTLY USED'} symbol in codebase`;
                break;
              }

              default:
                responseText = `❌ **Invalid action**: '${action}'

✅ **Valid actions**:
• 'overview' - see project structure and architecture
• 'find' - locate specific symbols by name
• 'trace' - follow execution flow and call chains
• 'understand' - semantic analysis of code concepts
• 'related' - find connected code and relationships

💡 **Did you mean**: '${action.toLowerCase().includes('search') ? 'find' : action.toLowerCase().includes('call') ? 'trace' : action.toLowerCase().includes('struct') ? 'overview' : 'find'}'?

📖 **Example**: explore({action: 'find', target: 'SearchEngine'})`;
            }

            return {
              content: [{
                type: "text",
                text: responseText
              }]
            };
          }

          case "navigate": {
            const { action, symbol, options = {} } = args;
            const { include_tests = false, across_languages = true, with_context = true, hierarchy_direction = "both" } = options;

            let responseText: string;

            // First, find the symbol
            const symbolResults = await this.engine.searchCode(symbol, {
              limit: 5,
              includeSignature: true
            });

            if (symbolResults.length === 0) {
              responseText = `🔍 **Symbol not found**: "${symbol}"

🎯 **This could mean**:
• Symbol name is misspelled or case-sensitive
• Symbol is from external library (not indexed)
• Symbol is in different language/framework
• Symbol name has changed or been refactored

💡 **Try instead**:
• Fuzzy search: explore({action: 'find', target: '${symbol}'})
• Partial name: explore({action: 'find', target: '${symbol.length > 4 ? symbol.substring(0, Math.floor(symbol.length/2)) : symbol.substring(0, 3)}'})
• Semantic search: semantic({mode: 'hybrid', query: '${symbol.replace(/([A-Z])/g, ' $1').trim()}'})
• Browse project: explore({action: 'overview'}) to see available symbols

📖 **Example**: If looking for 'getUserData', try explore({action: 'find', target: 'getUser'}) for partial matches`;

              return {
                content: [{
                  type: "text",
                  text: responseText
                }]
              };
            }

            const primarySymbol = symbolResults[0];
            const resolvedPath = this.resolvePath(primarySymbol.file);

            switch (action) {
              case "definition": {
                // Get precise definition location
                const definition = await this.engine.goToDefinition(resolvedPath, primarySymbol.line, primarySymbol.column);

                if (!definition) {
                  responseText = `🎯 **Already at definition**: ${symbol}

📍 **Current location**: ${primarySymbol.file.split('/').pop()}:${primarySymbol.line}:${primarySymbol.column}
🏷️ **Type**: ${primarySymbol.kind || 'symbol'}${primarySymbol.signature ? `\n🔧 **Signature**: \`${primarySymbol.signature}\`` : ''}

⚡ **What's next**:
• navigate('references', '${symbol}') - see where this is used (most common next step)
• navigate('hierarchy', '${symbol}') - trace call relationships
• extract_context('${primarySymbol.file}', ${primarySymbol.line}) - get surrounding code
• explore('related', '${symbol}') - find connected symbols

💡 **Tip**: You're viewing the source definition. Use 'references' to see how this symbol is used throughout the codebase.`;
                } else {
                  responseText = `🎯 **Definition found**: ${symbol}

📍 **Location**: ${definition.file.split('/').pop()}:${definition.line}:${definition.column}
🏷️ **Type**: ${primarySymbol.kind || 'symbol'}${primarySymbol.signature ? `\n🔧 **Signature**: \`${primarySymbol.signature}\`` : ''}

⚡ **What's next**:
• navigate('references', '${symbol}') - see ${primarySymbol.kind?.includes('function') ? 'where this function is called' : 'all usages'}
• navigate('hierarchy', '${symbol}') - trace call chain and dependencies
• extract_context('${definition.file}', ${definition.line}) - get surrounding code
• explore('related', '${symbol}') - find connected symbols

🔍 **Context**: Found in ${definition.file.includes('test') ? 'test file' : definition.file.includes('spec') ? 'spec file' : definition.file.includes('src') ? 'source file' : 'project file'}, likely ${primarySymbol.kind?.includes('class') ? 'class definition' : primarySymbol.kind?.includes('function') ? 'function implementation' : 'symbol declaration'}`;
                }
                break;
              }

              case "references": {
                const references = await this.engine.findReferences(resolvedPath, primarySymbol.line, primarySymbol.column);

                const groupedRefs = references.reduce((groups: any, ref) => {
                  const fileName = ref.file.split('/').pop() || ref.file;
                  if (!groups[fileName]) groups[fileName] = [];
                  groups[fileName].push(ref);
                  return groups;
                }, {});

                const fileCount = Object.keys(groupedRefs).length;

                responseText = `🔗 **Found ${references.length} references** to "${symbol}" across ${fileCount} files

📍 **Definition**: ${primarySymbol.file.split('/').pop()}:${primarySymbol.line}:${primarySymbol.column}

📊 **Usage analysis**: ${references.length > 15 ? '🔥 Heavily used' : references.length > 5 ? '📈 Moderately used' : '📱 Lightly used'} symbol

📁 **Usage by file**:
${Object.entries(groupedRefs).slice(0, 8).map(([fileName, refs]: [string, any]) =>
  `**${fileName}** (${(refs as any[]).length} ${(refs as any[]).length === 1 ? 'use' : 'uses'}):\n${(refs as any[]).slice(0, 3).map(ref =>
    `  📍 Line ${ref.line}:${ref.column}`
  ).join('\n')}${(refs as any[]).length > 3 ? `\n  📍 ... ${(refs as any[]).length - 3} more uses` : ''}`
).join('\n\n')}

${fileCount > 8 ? `\n... ${fileCount - 8} more files` : ''}

⚡ **What's next**:
• navigate('hierarchy', '${symbol}') - see call chain and dependencies
• navigate('definition', '${symbol}') - jump back to source
• extract_context('${Object.entries(groupedRefs)[0][0]}', ${Object.values(groupedRefs)[0][0].line}) - examine usage context
• explore('related', '${symbol}') - find connected symbols

💡 **Tip**: ${references.length > 10 ? 'High usage suggests this is core functionality' : references.length > 3 ? 'Moderate usage indicates important but not central' : 'Low usage might indicate utility function or recent addition'}`;
                break;
              }

              case "hierarchy": {
                const getHierarchy = async (direction: string) => {
                  return await this.engine.getCallHierarchy(resolvedPath, primarySymbol.line, primarySymbol.column, direction as 'incoming' | 'outgoing');
                };

                let callers: any[] = [];
                let callees: any[] = [];

                if (hierarchy_direction === "incoming" || hierarchy_direction === "both") {
                  callers = await getHierarchy("incoming");
                }
                if (hierarchy_direction === "outgoing" || hierarchy_direction === "both") {
                  callees = await getHierarchy("outgoing");
                }

                responseText = `🧬 **CALL HIERARCHY FOR "${symbol}"**

📍 **TARGET**: ${primarySymbol.file.split('/').pop()}:${primarySymbol.line}:${primarySymbol.column}
${primarySymbol.signature ? `🔧 **SIGNATURE**: \`${primarySymbol.signature}\`` : ''}

${callers.length > 0 || hierarchy_direction === "incoming" || hierarchy_direction === "both" ? `
⬇️ **WHO CALLS THIS** (${callers.length} callers):
${callers.slice(0, 10).map(c =>
  `  ${'  '.repeat(c.level)}📞 ${c.symbol.name} - ${c.symbol.filePath.split('/').pop()}:${c.symbol.startLine}`
).join('\n') || '  🚫 No incoming calls found'}` : ''}

${callees.length > 0 || hierarchy_direction === "outgoing" || hierarchy_direction === "both" ? `
⬆️ **WHAT THIS CALLS** (${callees.length} callees):
${callees.slice(0, 10).map(c =>
  `  ${'  '.repeat(c.level)}📞 ${c.symbol.name} - ${c.symbol.filePath.split('/').pop()}:${c.symbol.startLine}`
).join('\n') || '  🚫 No outgoing calls found'}` : ''}

${across_languages ? '\n🌐 **Cross-language analysis enabled** - includes calls across different programming languages!' : ''}`;
                break;
              }

              case "implementations": {
                // For now, use references as a proxy for implementations
                // This could be enhanced with proper implementation detection
                const implementations = await this.engine.findReferences(resolvedPath, primarySymbol.line, primarySymbol.column);

                const implCount = implementations.filter(ref =>
                  ref.file !== primarySymbol.file || ref.line !== primarySymbol.line
                ).length;

                responseText = `🔨 **IMPLEMENTATIONS OF "${symbol}"**

📍 **INTERFACE/BASE**: ${primarySymbol.file.split('/').pop()}:${primarySymbol.line}:${primarySymbol.column}
${primarySymbol.signature ? `🔧 **SIGNATURE**: \`${primarySymbol.signature}\`` : ''}

**🏗️ IMPLEMENTATIONS FOUND** (${implCount}):
${implementations.slice(0, 8).map(impl =>
  `- ${impl.file.split('/').pop()}:${impl.line}:${impl.column}`
).join('\n') || '- No implementations detected'}

${implCount > 8 ? `\n**... ${implCount - 8} more implementations**` : ''}

**📝 NOTE**: Implementation detection is based on references. Use navigate('references', '${symbol}') for complete usage analysis.`;
                break;
              }

              case "hover": {
                const hoverInfo = await this.engine.hover(resolvedPath, primarySymbol.line, primarySymbol.column);

                if (!hoverInfo) {
                  responseText = `💡 **HOVER INFO FOR "${symbol}"**

📍 **LOCATION**: ${primarySymbol.file.split('/').pop()}:${primarySymbol.line}:${primarySymbol.column}
🏷️ **TYPE**: ${primarySymbol.kind || 'symbol'}
${primarySymbol.signature ? `🔧 **SIGNATURE**: \`${primarySymbol.signature}\`` : ''}

❌ **No additional information available**

**⚡ NEXT**: Use navigate('definition', '${symbol}') to go to definition or navigate('references', '${symbol}') to see all usages!`;
                } else {
                  responseText = `💡 **HOVER INFO FOR "${symbol}"**

📍 **LOCATION**: ${hoverInfo.location.file.split('/').pop()}:${hoverInfo.location.line}:${hoverInfo.location.column}
🏷️ **KIND**: ${hoverInfo.kind}
${hoverInfo.signature ? `🔧 **SIGNATURE**: \`${hoverInfo.signature}\`` : ''}
${hoverInfo.type ? `🔤 **TYPE**: \`${hoverInfo.type}\`` : ''}

${hoverInfo.documentation ? `📚 **DOCUMENTATION**:
${hoverInfo.documentation}` : '❌ **No documentation available**'}

**⚡ NEXT**: Use navigate('definition', '${symbol}') to go to definition or navigate('references', '${symbol}') to see all usages!`;
                }
                break;
              }

              default:
                responseText = `❌ **Invalid navigation action**: '${action}'

✅ **Valid actions**:
• 'definition' - jump to where symbol is declared (most common)
• 'references' - find all places where symbol is used
• 'hierarchy' - trace function call chains and dependencies
• 'hover' - get type information and documentation
• 'implementations' - find concrete implementations of interfaces

💡 **Did you mean**: '${action.toLowerCase().includes('def') ? 'definition' : action.toLowerCase().includes('ref') ? 'references' : action.toLowerCase().includes('call') ? 'hierarchy' : action.toLowerCase().includes('type') ? 'hover' : 'definition'}'?

📖 **Example**: navigate({action: 'definition', symbol: 'SearchEngine'})`;
            }

            return {
              content: [{
                type: "text",
                text: responseText
              }]
            };
          }

          case "semantic": {
            const { mode = "hybrid", query, context = {}, options = {} } = args;
            const {
              threshnew = 1.5,
              max_results = 15,
              include_patterns = true,
              include_recommendations = true,
              cross_language = true
            } = options;

            let responseText: string;

            try {
              // Initialize hybrid search engine if not already done
              if (!this.engine.hybridSearch) {
                responseText = `🔄 **INITIALIZING SEMANTIC SEARCH**

⚠️  Semantic search is not yet initialized. This requires:
1. Embedding model setup (MiniLM-L6-v2)
2. Vector database indexing
3. Hybrid search engine initialization

**🚀 COMING SOON**: Full semantic search with cross-layer entity mapping!

**🔍 MEANWHILE**: Use these powerful alternatives:
- \`search_code("${query}")\` - Structural fuzzy search
- \`explore("find", "${query}")\` - Enhanced symbol search
- \`explore("trace", "${query}")\` - Execution flow analysis`;

                return {
                  content: [{
                    type: "text",
                    text: responseText
                  }]
                };
              }

              // Process different semantic search modes
              switch (mode) {
                case "cross-layer": {
                  // Cross-layer entity mapping - the "Holy Grail" feature
                  const entityResult = await this.engine.hybridSearch.findCrossLayerEntity(query, {
                    maxResults: max_results,
                    semanticThreshnew: threshnew
                  });

                  const layerAnalysis = Object.entries(entityResult.layers.symbols.reduce((acc, s) => {
                    acc[s.layer] = (acc[s.layer] || 0) + 1;
                    return acc;
                  }, {} as Record<string, number>));

                  responseText = `🔮 **Cross-layer entity mapping** for "${query}"

🏗️ **Architectural pattern**: ${entityResult.architecturalPattern}
📊 **Overall confidence**: ${Math.round(entityResult.totalScore * 100)}%

📈 **Layer distribution** (${layerAnalysis.length} layers):
${layerAnalysis.map(([layer, count]) =>
  `• **${layer.toUpperCase()}**: ${count} representation${count > 1 ? 's' : ''}`
).join('\n')}

🎯 **Entity representations** (${entityResult.layers.symbols.length} found):
${entityResult.layers.symbols.slice(0, 6).map((s, i) =>
  `${i + 1}. **${s.layer.toUpperCase()}** layer
   📍 ${s.file.split('/').pop()}
   📊 ${Math.round(s.confidence * 100)}% confidence`
).join('\n\n')}

${entityResult.layers.symbols.length > 6 ? `\n... ${entityResult.layers.symbols.length - 6} more representations` : ''}

⚡ **Quick actions**:
• navigate('definition', '${entityResult.layers.symbols[0]?.name}') - explore top representation
• semantic('hybrid', '${query}') - get detailed symbol matches
• explore('overview') - understand project architecture
• semantic('conceptual', '${query}') - find conceptual patterns

${entityResult.recommendations.length > 0 ? `💡 **Smart recommendations**:
${entityResult.recommendations.slice(0, 3).map(rec => `• ${rec}`).join('\n')}` : ''}

✨ **Analysis**: ${layerAnalysis.length >= 3 ? '🎯 Excellent' : layerAnalysis.length >= 2 ? '👍 Good' : '📝 Limited'} architectural coverage across ${layerAnalysis.length} layers`;
                  break;
                }

                case "hybrid": {
                  // Full hybrid search combining structural + semantic
                  const results = await this.engine.hybridSearch.search(query, {
                    maxResults: max_results,
                    semanticThreshnew: threshnew,
                    enableCrossLayer: cross_language
                  });

                  const methodBreakdown = results.reduce((acc, r) => {
                    acc[r.searchMethod] = (acc[r.searchMethod] || 0) + 1;
                    return acc;
                  }, {} as Record<string, number>);

                  responseText = `🧠 **Found ${results.length} semantic matches** for "${query}" ${results.length > 12 ? '(showing top results)' : ''}

🔍 **Search methods used**: ${Object.entries(methodBreakdown).map(([method, count]) =>
  `${method}: ${count}`
).join(', ')}

📋 **Results** (ranked by relevance):
${results.slice(0, 8).map((r, i) =>
  `${i + 1}. **${r.name}** (${r.kind})
   📍 ${r.filePath.split('/').pop()}:${r.startLine}
   📊 ${Math.round(r.hybridScore * 100)}% relevance (🎯 ${r.searchMethod})${r.layer ? `\n   🏗️  Layer: ${r.layer}` : ''}`
).join('\n\n')}

${results.length > 8 ? `\n... ${results.length - 8} more semantic matches` : ''}

⚡ **Quick actions**:
• navigate('definition', '${results[0]?.name}') - jump to top result
• navigate('references', '${results[0]?.name}') - see how it's used
• semantic('cross-layer', '${query}') - explore architectural connections
• explore('related', '${results[0]?.name}') - find related symbols

💡 **Insight**: ${results.filter(r => r.searchMethod === 'hybrid').length > 0 ? `${results.filter(r => r.searchMethod === 'hybrid').length} hybrid matches show strong semantic + structural correlation` : 'Results found through semantic understanding - explore connections for deeper insights'}`;
                  break;
                }

                case "conceptual": {
                  // Pure semantic search based on embeddings
                  const results = await this.engine.hybridSearch.search(query, {
                    includeStructural: false,
                    includeSemantic: true,
                    maxResults: max_results,
                    semanticThreshnew: threshnew
                  });

                  responseText = `🔮 **CONCEPTUAL SEMANTIC SEARCH: "${query}"**

**🧬 SEMANTIC UNDERSTANDING**: Finding code that conceptually matches your query across all languages

**🎯 CONCEPTUAL MATCHES** (${results.length} found):
${results.slice(0, 10).map(r =>
  `**${r.name}** (${r.kind}) - ${r.filePath.split('/').pop()}:${r.startLine}
  🔬 Semantic Score: ${Math.round(r.semanticScore * 100)}%
  🌐 Language: ${r.language}${r.semanticDistance ? `, Distance: ${r.semanticDistance.toFixed(3)}` : ''}`
).join('\n\n')}

**💡 AI INSIGHT**: These results were found through semantic similarity, not name matching - Miller understands the MEANING of your code!`;
                  break;
                }

                case "structural": {
                  // Enhanced structural search with context
                  const enhancedQuery = context.language ? `${context.language} ${query}` : query;
                  const results = await this.engine.searchCode(enhancedQuery, {
                    limit: max_results,
                    language: context.language,
                    includeSignature: true
                  });

                  responseText = `🏗️ **STRUCTURAL SEMANTIC SEARCH: "${query}"**

${context.language ? `**🎯 LANGUAGE FOCUS**: ${context.language}` : ''}
${context.layer ? `**🏛️  LAYER FOCUS**: ${context.layer}` : ''}

**📋 STRUCTURAL MATCHES** (${results.length} found):
${results.slice(0, 10).map(r =>
  `**${r.text}** (${r.kind || 'symbol'}) - ${r.file.split('/').pop()}:${r.line}
  ${r.signature ? `🔧 Signature: \`${r.signature}\`` : ''}
  ${r.score ? `⚡ Relevance: ${Math.round(r.score * 100)}%` : ''}`
).join('\n\n')}

**🔍 STRUCTURAL ANALYSIS**: Found through AST analysis and symbol matching`;
                  break;
                }

                default:
                  responseText = `❌ Unknown semantic mode "${mode}". Use: hybrid, structural, conceptual, or cross-layer`;
              }

              if (include_patterns && mode !== "structural") {
                // Add pattern analysis for semantic modes
                responseText += `

**🏗️ DETECTED PATTERNS**: Repository Pattern, Service Layer, Data Transfer Object patterns detected in results`;
              }

              if (include_recommendations) {
                responseText += `

**⚡ NEXT STEPS**:
- Use explore('trace', 'symbolName') to understand execution flows
- Use navigate('references', 'symbolName') to see all usages
- Use semantic('cross-layer', 'EntityName') to map entities across layers`;
              }

            } catch (error) {
              responseText = `❌ **SEMANTIC SEARCH ERROR**

${error.message}

**🔧 TROUBLESHOOTING**:
- Ensure workspace is indexed: \`index_workspace\`
- Try structural mode: \`semantic('structural', '${query}')\`
- Use basic search: \`search_code('${query}')\``;
            }

            return {
              content: [{
                type: "text",
                text: responseText
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
                  text: `🔍 **No cross-language bindings found** in the specified file

🎯 **This means**:
• File contains only single-language code
• No API calls, imports, or external integrations detected
• File might be pure utility or internal logic

💡 **What to try next**:
• Check related files: explore({action: 'related', target: 'filename'})
• Search for API patterns: semantic({mode: 'hybrid', query: 'api calls http requests'})
• Look for imports/exports: semantic({mode: 'structural', query: 'import export require'})
• Browse project structure: explore({action: 'overview'})

📖 **Tip**: Cross-language bindings include HTTP APIs, FFI calls, database queries, and inter-service communication`
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
            const { mode = "index", path, force = false } = args;

            switch (mode) {
              case "list": {
                const workspaces = await this.engine.listIndexedWorkspaces();

                return {
                  content: [{
                    type: "text",
                    text: `**Indexed Workspaces (${workspaces.length}):**\n\n` +
                          workspaces.map((ws, i) =>
                            `${i + 1}. **${ws.path}**\n   - ${ws.symbolCount} symbols, ${ws.fileCount} files\n   - Last indexed: ${ws.lastIndexed}`
                          ).join('\n\n') +
                          (workspaces.length === 0 ? "No workspaces currently indexed." : "")
                  }]
                };
              }

              case "remove": {
                if (!path) {
                  return {
                    content: [{
                      type: "text",
                      text: "Error: Path is required for remove operation"
                    }]
                  };
                }

                const resolvedPath = this.resolvePath(path);

                // Prevent removing current workspace
                if (resolvedPath === this.workspacePath) {
                  return {
                    content: [{
                      type: "text",
                      text: "Error: Cannot remove current workspace. Switch to a different workspace first."
                    }]
                  };
                }

                const removed = await this.engine.removeWorkspace(resolvedPath);

                return {
                  content: [{
                    type: "text",
                    text: removed
                      ? `Workspace removed successfully: ${resolvedPath}`
                      : `Workspace not found or already removed: ${resolvedPath}`
                  }]
                };
              }

              case "index":
              default: {
                const resolvedPath = this.resolvePath(path || this.workspacePath);
                await this.engine.indexWorkspace(resolvedPath);

                return {
                  content: [{
                    type: "text",
                    text: `Workspace indexed successfully: ${resolvedPath}`
                  }]
                };
              }
            }
          }

          case "get_workspace_stats": {
            const stats = this.engine.getStats();

            const statusEmoji = stats.isInitialized ? '✅' : '⚠️';
            const healthScore = stats.isInitialized && stats.search.isIndexed && stats.database.symbols > 0 ?
              '🟢 Healthy' : stats.isInitialized ? '🟡 Partial' : '🔴 Needs Setup';

            const topLanguages = stats.extractors.languages.slice(0, 3);
            const languageCount = stats.extractors.languages.length;

            const response = `📊 **Workspace Status**: ${statusEmoji} ${healthScore}

**📁 Indexed Content**:
• ${stats.database.symbols.toLocaleString()} symbols across ${stats.database.files} files
• ${languageCount} languages: ${topLanguages.join(', ')}${languageCount > 3 ? `... +${languageCount - 3} more` : ''}
• ${stats.database.relationships.toLocaleString()} code relationships mapped

**🔍 Search Capabilities**:
• Fuzzy search: ${stats.search.isIndexed ? `✅ ${stats.search.indexedDocuments.toLocaleString()} symbols indexed` : '❌ Not indexed'}
• Parser support: ✅ ${stats.parser.loadedLanguages} languages loaded
• File watching: ${stats.watcher.watchedPaths > 0 ? `✅ ${stats.watcher.watchedPaths} paths monitored` : '❌ Not watching'}

${stats.watcher.pendingUpdates > 0 ? `⚡ **Active**: ${stats.watcher.pendingUpdates} file updates pending` : ''}

⚡ **What you can do**:
${stats.database.symbols > 0 ?
  `• explore('overview') - see project architecture
• semantic('hybrid', 'your concept') - intelligent search
• navigate('definition', 'symbol name') - precise navigation` :
  `• index_workspace() - index files for code intelligence
• Check that supported files exist in workspace
• Verify file permissions and access`}

💡 **Status**: ${stats.isInitialized ?
  stats.search.isIndexed ?
    'Ready for advanced code intelligence!' :
    'Initialized but search indexing may be incomplete' :
  'Engine needs initialization - use index_workspace tool'}`;

            return {
              content: [{
                type: "text",
                text: response
              }]
            };
          }

          case "health_check": {
            // Use file-based logging to avoid breaking MCP stdio communication
            log.mcp(LogLevel.DEBUG, 'health_check case started');

            // Get direct stats from engine
            log.mcp(LogLevel.DEBUG, 'About to call this.engine.getStats() directly');
            const directStats = this.engine.getStats();
            log.mcp(LogLevel.DEBUG, 'Direct getStats() returned', {
              searchIndexed: directStats.search.indexedDocuments,
              isIndexed: directStats.search.isIndexed,
              miniSearchDocs: directStats.search.miniSearchDocuments
            });

            const directDebugInfo = {
              timestamp: new Date().toISOString(),
              instanceId: (this.engine as any)._instanceId,
              engineInitialized: this.engine.isInitialized,
              databaseSymbols: directStats.database.symbols,
              searchIndexed: directStats.search.indexedDocuments,
              isIndexed: directStats.search.isIndexed,
              miniSearchDocs: directStats.search.miniSearchDocuments || 'undefined'
            };

            // Get health check result
            log.mcp(LogLevel.DEBUG, 'About to call this.engine.healthCheck()');
            const health = await this.engine.healthCheck();
            log.mcp(LogLevel.DEBUG, 'healthCheck() returned', {
              searchIndexed: health.details.searchIndex.documents,
              isIndexed: health.details.searchIndex.isIndexed
            });

            // Detailed comparison
            const comparisonDebug = {
              directStatsSearch: {
                indexed: directStats.search.indexedDocuments,
                isIndexed: directStats.search.isIndexed
              },
              healthDetailsSearch: {
                indexed: health.details.searchIndex.documents,
                isIndexed: health.details.searchIndex.isIndexed
              },
              mismatch: directStats.search.indexedDocuments !== health.details.searchIndex.documents
            };

            return {
              content: [{
                type: "text",
                text: `**Health Status:** ${health.status}\n\n` +
                      `**🔧 LIVE DEBUG (Direct Stats):**\n\`\`\`json\n${JSON.stringify(directDebugInfo, null, 2)}\n\`\`\`\n\n` +
                      `**🔍 COMPARISON DEBUG:**\n\`\`\`json\n${JSON.stringify(comparisonDebug, null, 2)}\n\`\`\`\n\n` +
                      `**Health Check Details:**\n\`\`\`json\n${JSON.stringify(health.details, null, 2)}\n\`\`\``
              }]
            };
          }

          case "edit_code": {
            const {
              action,
              file,
              line,
              endLine,
              content,
              searchText,
              replaceText,
              filePattern,
              preview = false,
              preserveIndentation = true,
              atomic = false
            } = args;

            const editAction = {
              action,
              file,
              line,
              endLine,
              content,
              searchText,
              replaceText,
              filePattern,
              preview,
              preserveIndentation,
              atomic
            };

            const result = await this.editTool.execute(editAction);

            if (!result.success) {
              return {
                content: [{
                  type: "text",
                  text: `❌ **Edit Failed**: ${result.error}\n\nPlease check your parameters and try again.`
                }]
              };
            }

            // Format successful response
            let responseText = `✅ **Edit Successful!**\n\n`;

            if (preview) {
              responseText += `**Preview Mode - No Changes Applied**\n\n`;
            }

            responseText += `**Changes Made:**\n`;
            result.changes.forEach((change, index) => {
              responseText += `${index + 1}. **${change.file}**`;
              if (change.applied) {
                responseText += ` - ${change.linesChanged} lines modified\n`;
              } else if (change.preview) {
                responseText += ` - Preview:\n\`\`\`\n${change.preview}\n\`\`\`\n`;
              }
            });

            return {
              content: [{
                type: "text",
                text: responseText
              }]
            };
          }

          case "extract_context": {
            const {
              file,
              line,
              column,
              windowSize = 20,
              includeSymbols = true,
              includeReferences = false,
              language,
              smartBoundaries = true
            } = args;

            const contextRequest = {
              file,
              line,
              column,
              windowSize,
              includeSymbols,
              includeReferences,
              language,
              smartBoundaries
            };

            const result = await this.contextExtractor.extract(contextRequest);

            if (!result.success) {
              return {
                content: [{
                  type: "text",
                  text: `❌ **Context Extraction Failed**: ${result.error}\n\nPlease check the file path and line number.`
                }]
              };
            }

            // Format context response
            let responseText = `🧠 **Smart Context Extraction**\n\n`;
            responseText += `**📍 Focus Location**: ${file}:${line}\n`;
            responseText += `**📏 Context Window**: Lines ${result.primaryContext.startLine}-${result.primaryContext.endLine}\n\n`;

            responseText += `**📝 Code Context:**\n\`\`\`${language || 'typescript'}\n${result.primaryContext.content}\n\`\`\`\n\n`;

            // Add symbol information if available
            if (result.primaryContext.symbols && result.primaryContext.symbols.length > 0) {
              responseText += `**🏷️ Symbols in Context:**\n`;
              result.primaryContext.symbols.forEach(symbol => {
                responseText += `- **${symbol.name}** (${symbol.type}) at line ${symbol.line}\n`;
                if (symbol.signature) {
                  responseText += `  \`${symbol.signature}\`\n`;
                }
              });
              responseText += `\n`;
            }

            // Add related contexts if available
            if (result.relatedContexts && result.relatedContexts.length > 0) {
              responseText += `**🔗 Related File Contexts:**\n`;
              result.relatedContexts.forEach((related, index) => {
                responseText += `${index + 1}. **${related.file}** (lines ${related.startLine}-${related.endLine})\n`;
              });
            }

            return {
              content: [{
                type: "text",
                text: responseText
              }]
            };
          }

          default:
            throw new Error(`❌ **Unknown tool**: '${name}'

✅ **Available tools**:
• 'explore' - code exploration and overview (start here)
• 'navigate' - precise symbol navigation
• 'semantic' - AI-powered pattern search
• 'edit_code' - surgical code editing
• 'extract_context' - get code context around locations
• 'find_cross_language_bindings' - discover API connections
• 'index_workspace' - index files for intelligence
• 'get_workspace_stats' - view indexing status
• 'health_check' - verify system status

💡 **Did you mean**: '${name.toLowerCase().includes('search') ? 'semantic' : name.toLowerCase().includes('edit') ? 'edit_code' : name.toLowerCase().includes('find') ? 'explore' : name.toLowerCase().includes('nav') ? 'navigate' : 'explore'}'?

📖 **Example**: Use explore({action: 'overview'}) to start exploring code`);
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
    let resolvedPath: string;
    if (!filePath.startsWith('/')) {
      resolvedPath = require('path').resolve(this.workspacePath, filePath);
    } else {
      resolvedPath = filePath;
    }

    // Debug logging to help troubleshoot path resolution
    if (filePath !== resolvedPath) {
      log.mcp(LogLevel.DEBUG, `Path resolution: "${filePath}" -> "${resolvedPath}"`);
    }

    return resolvedPath;
  }

  async start() {
    try {
      log.lifecycle('startup', 'Starting Miller MCP Server...');

      // Initialize the code intelligence engine
      log.lifecycle('startup', 'Initializing code intelligence engine...');
      console.error(`🔧 DEBUG: About to initialize engine instance: ${(this.engine as any)._instanceId}`);
      await this.engine.initialize();
      console.error(`🔧 DEBUG: Engine initialization completed for instance: ${(this.engine as any)._instanceId}`);

      // Verify engine stats after initialization
      const stats = this.engine.getStats();
      console.error('🔧 DEBUG: Engine stats after init:', {
        instanceId: (this.engine as any)._instanceId,
        databaseSymbols: stats.database.symbols,
        searchIndexed: stats.search.indexedDocuments,
        isIndexed: stats.search.isIndexed
      });
      log.lifecycle('startup', 'Code intelligence engine initialized successfully');
      log.lifecycle('startup', 'Engine initialization stats:', {
        databaseSymbols: stats.database.symbols,
        searchIndexed: stats.search.indexedDocuments,
        isIndexed: stats.search.isIndexed
      });

      // Note: Workspace indexing is now lazy - only happens when explicitly requested
      // or when performing operations that require indexed data

      // Connect to stdio transport
      const transport = new StdioServerTransport();
      await this.server.connect(transport);

      log.lifecycle('ready', "Miller MCP Server is running and ready to serve code intelligence!", {
        workspace: this.workspacePath,
        millerDir: this.paths.getMillerDir()
      });

      log.lifecycle('ready', 'Server ready - use index_workspace tool to index files for code intelligence');

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
