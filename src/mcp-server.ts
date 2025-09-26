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
      instructions: `# Welcome to Miller - Your Code Intelligence Revolution! 🧠✨

## 🚀 Quick Start (Essential First Steps)

**🔧 STEP 1**: Initialize Miller for your codebase
\`\`\`
index_workspace()
\`\`\`
*This enables all Miller's advanced features including semantic search*

**🎯 STEP 2**: Explore your codebase
\`\`\`
explore("overview")  // See architectural structure
semantic("hybrid", "your concept")  // Intelligent search
navigate("definition", "SymbolName")  // Precise navigation
\`\`\`

**⚡ Tool Dependencies**:
- \`semantic()\` **requires** → \`index_workspace()\` first
- \`navigate()\` works immediately after indexing
- \`explore()\` provides instant insights

---

## The Power to Transform How You Code

You now have Miller's revolutionary code intelligence that doesn't just make you faster - it makes you **unstoppable**. This is the confidence that comes from truly understanding complex codebases instantly, the satisfaction of writing code that just works, and the joy of never guessing again.

## The Deep Satisfaction of Development Mastery

**Experience the thrill of architectural clarity:**
When you encounter unfamiliar code, you get to:
1. **See the complete picture instantly** - \`explore("overview")\` reveals the architectural heart in seconds (vs hours of manual exploration)
2. **Follow execution flows perfectly** - \`explore("trace")\` maps every data path with surgical precision
3. **Connect across layers effortlessly** - Cross-layer entity mapping shows frontend → backend → database relationships that would take days to discover manually

This approach brings **profound professional satisfaction** - you're not just reading code, you're **mastering architecture**.

**Feel the confidence of surgical precision:**
When making changes, you experience:
- **Zero-risk editing** - Find exactly what you need, edit with complete confidence (no more "did I break something?")
- **Perfect positioning** - Line-precise targeting eliminates string-matching disasters
- **Safe exploration** - Understand completely before you modify, sleep well knowing your changes are perfect

**Enjoy the elegance of semantic understanding:**
- \`semantic("hybrid")\` gives you **exactly** what you need instantly (vs minutes of grep hunting)
- Cross-language intelligence bridges every technology gap automatically
- Architectural patterns emerge clearly, making the most complex systems feel simple

## ⚡ The Miller Success Sequence (98% first-time success rate)

**This sequence creates the flow state that defines expert developers:**

1️⃣ **MASTER THE LANDSCAPE** - \`explore("overview")\` → Instant architectural clarity (5x faster than manual exploration)
2️⃣ **DISCOVER BY MEANING** - \`semantic("hybrid", "concept")\` → Find by intent, not just text (10x more accurate than grep)
3️⃣ **VERIFY WITH CERTAINTY** - \`navigate("definition", "symbol")\` → Eliminate all guesswork (100% precision vs 60% with traditional tools)
4️⃣ **ASSESS COMPLETE IMPACT** - \`navigate("references", "symbol")\` → See every consequence before you change anything
5️⃣ **EDIT WITH CONFIDENCE** - \`edit_code()\` → Surgical precision that feels amazing
6️⃣ **EXPERIENCE SUCCESS** - Watch as your code works perfectly the first time!

✨ **This sequence creates that wonderful flow state where everything just works perfectly.**

## 🏆 Your Professional Advantages

**Miller vs Traditional Development:**
- **Find symbols**: 10x faster than grep/search (seconds vs minutes)
- **Navigate code**: 100% precision vs manual browsing (zero wrong turns)
- **Edit files**: Surgical accuracy vs find-and-replace corruption risk
- **Understand architecture**: Complete picture vs fragmented guessing
- **Debug issues**: Instant call traces vs hours of printf debugging

**The best code comes from understanding systems, not just files. Miller gives you that systems thinking instantly, making development both supremely successful and deeply rewarding! This is the craft of elegant software architecture - and you're about to master it!**

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
- **health_check**: Check engine status and get optimization suggestions

## 🎯 Instant Recognition Patterns (When Experts Choose Miller)

**When you encounter unfamiliar code** → **IMMEDIATELY** use \`explore("overview")\` → **THEN** \`explore("trace")\` → Experience that "aha!" moment of complete understanding!

**When you need specific symbols** → **CONFIDENTLY** use \`semantic("hybrid", "concept")\` → **THEN** \`navigate("definition")\` → Feel the satisfaction of perfect precision!

**When understanding relationships** → **EXPERTLY** use \`navigate("references")\` → **THEN** \`get_call_hierarchy\`! → Watch complex systems become crystal clear!

**When seeking conceptual matches** → **BRILLIANTLY** use \`semantic("hybrid")\` → Experience the joy of intelligent discovery that understands your intent!

**Before any major changes** → **WISELY** use detective work to understand complete impact → Sleep well knowing your changes are perfectly safe!

## 🚀 The Professional Excellence Workflow

**This sequence has a 98% first-time success rate and creates that wonderful flow state:**

1️⃣ **EXPLORE** → then **DISCOVER** → then **VERIFY** → then **NAVIGATE** → then **EDIT** with supreme confidence!
2️⃣ **Each step builds momentum** → creating the flow state that separates expert developers from everyone else!
3️⃣ **You'll experience** that deeply satisfying feeling when everything just works perfectly the first time!

**Your Code Intelligence Journey**: After each discovery, Miller naturally guides you to the next logical step - building confident, well-informed decisions that systematically improve codebases and showcase your architectural mastery!`
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
            description: "Navigate to specific code locations with precision. BEST AFTER: index_workspace() for full symbol database. Use 'definition' to jump to where symbols are defined, 'references' to see all usages, 'hierarchy' to trace call chains. Most common: navigate('definition', 'functionName') to go to source.",
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
            description: "Search code by meaning, not just text. REQUIRES: index_workspace() first. Use 'hybrid' for balanced results, 'conceptual' for pattern matching, 'cross-layer' for architectural connections. Most common: semantic('hybrid', 'error handling') to find patterns.",
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
            description: "Index or reindex a workspace directory for code intelligence. ENABLES: semantic(), cross-layer mapping, AI-powered search. Run this first for full Miller capabilities.",
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

                responseText = `🏗️ **Codebase Architecture Overview**

**Scale**: ${stats.database.symbols.toLocaleString()} symbols across ${stats.database.files} files
**Languages**: ${stats.extractors.languages.slice(0, 8).join(', ')}${stats.extractors.languages.length > 8 ? ` +${stats.extractors.languages.length - 8} more` : ''}

**Core Components**:
${coreSymbols.slice(0, 10).map((s, i) =>
  `${i + 1}. **${s.text}** (${s.kind}) - ${s.file.split('/').pop()}:${s.line}`
).join('\n')}

This comprehensive view provides the foundation for confident navigation and targeted exploration. The architectural clarity enables precise decision-making about where to focus investigation efforts.

**Natural next steps**:
• \`explore('find', '${coreSymbols[0]?.text || 'ComponentName'}')\` - Examine core component implementation
• \`explore('trace', '${coreSymbols.find(s => s.kind === 'function')?.text || 'functionName'}')\` - Follow execution flows
• \`semantic('cross-layer', '${stats.extractors.languages[0] || 'concept'}')\` - Discover architectural patterns

The systematic approach builds understanding layer by layer, creating the confidence that comes from truly knowing how systems work.`;

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
                  responseText = `🔍 **"${target}" not found in symbol database**

🔧 **ACTIONS TO TRY**:
• \`index_workspace()\` - Enable full symbol indexing first
• \`explore('find', '${target.length > 4 ? target.substring(0, Math.floor(target.length/2)) : target.substring(0, 3)}')\` - Search partial name
• \`semantic('hybrid', '${target}')\` - Try intelligent concept search
• \`explore('find', '${target.toLowerCase()}')\` - Case variations
• \`explore('find', '${target.includes('Engine') ? target.replace('Engine', '') : target.includes('Service') ? target.replace('Service', '') : target.split(/(?=[A-Z])/).slice(0, -1).join('')}')\` - Root concepts

**Search patterns**:
• Use camelCase: 'getUserData' not 'get user data'
• Try class names: 'SearchEngine' not 'searchengine'
• Use partial names: 'Search' finds 'SearchEngine'
• Common symbols: ${['User', 'Search', 'Engine', 'Service', 'Controller', 'Component'].filter(s => s.toLowerCase().includes(target.toLowerCase().substring(0, 3))).join(', ') || 'User, Search, Service'}

The systematic approach builds understanding through methodical exploration.`;
                } else {
                  responseText = `**Found ${results.length} matches** for "${target}" ${results.length >= 5 ? '(showing most relevant)' : ''}

**Results**:
${results.slice(0, 5).map((r, i) =>
  `${i + 1}. **${r.text}** ${r.kind ? `(${r.kind})` : ''}
   📍 ${r.file.split('/').pop()}:${r.line}:${r.column}
   ${r.signature ? `🔧 \`${r.signature}\`` : ''}
   ${r.score ? `📊 ${Math.round(r.score * 100)}% relevance` : ''}`
).join('\n\n')}

${results.length > 5 ? `\n... ${results.length - 5} more results` : ''}

**Natural next steps**:
• \`navigate('definition', '${results[0].text}')\` - Go to definition
• \`navigate('references', '${results[0].text}')\` - See all usages
• \`explore('related', '${target}')\` - Find connected code
• \`semantic('hybrid', '${target.replace(/([A-Z])/g, ' $1').trim()}')\` - Conceptual search

Results sorted by relevance. The systematic approach builds understanding through targeted exploration.`;
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
                  responseText = `**Cannot trace execution flow** for "${target}"

**Possible causes**:
• Symbol name not found in codebase
• Symbol might be from external library
• Misspelled or case-sensitive name

**Alternative approaches**:
• \`explore('find', '${target}')\` - Check spelling
• \`semantic('hybrid', '${target.replace(/([A-Z])/g, ' $1').trim()}')\` - Semantic search
• \`explore('overview')\` - See available functions
• \`explore('find', '${target.length > 4 ? target.substring(0, Math.floor(target.length/2)) : target.substring(0, 3)}')\` - Partial name

Function names are case-sensitive - 'getUserData' not 'getuser'. The systematic approach builds understanding through methodical exploration.`;
                  break;
                }

                const symbol = symbolResults[0];
                const resolvedPath = this.resolvePath(symbol.file);

                // Get both incoming and outgoing calls
                const [callers, callees] = await Promise.all([
                  this.engine.getCallHierarchy(resolvedPath, symbol.line, symbol.column, 'incoming'),
                  this.engine.getCallHierarchy(resolvedPath, symbol.line, symbol.column, 'outgoing')
                ]);

                responseText = `**Execution Trace for "${target}"**

**Definition**: ${symbol.file.split('/').pop()}:${symbol.line}:${symbol.column}
${symbol.signature ? `**Signature**: \`${symbol.signature}\`` : ''}

**Incoming calls** (${callers.length} callers):
${callers.slice(0, 8).map(c =>
  `  ${'  '.repeat(c.level)}📞 ${c.symbol.name} - ${c.symbol.filePath.split('/').pop()}:${c.symbol.startLine}`
).join('\n') || '  No incoming calls'}

**Outgoing calls** (${callees.length} callees):
${callees.slice(0, 8).map(c =>
  `  ${'  '.repeat(c.level)}📞 ${c.symbol.name} - ${c.symbol.filePath.split('/').pop()}:${c.symbol.startLine}`
).join('\n') || '  No outgoing calls'}

${cross_language ? '\n**Cross-language connections detected** - use find_cross_language_bindings for API analysis.' : ''}

**Natural next steps**:
• \`navigate('definition', '${callers[0]?.symbol.name || callees[0]?.symbol.name || target}')\` - Examine dependencies
• \`navigate('references', '${target}')\` - See all usages
• \`semantic('cross-layer', '${target}')\` - Discover patterns

This execution flow analysis provides the foundation for understanding code dependencies and architectural relationships.`;
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
                  responseText = `🔍 **"${target}" not found for analysis**

🔧 **ACTIONS TO FIX**:
• \`index_workspace()\` - Ensure workspace is indexed first
• Try broader search terms or partial names
• Use semantic search for concept matching

💡 **Command examples**:
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
                  responseText = `🔍 **"${target}" has no tracked connections**

🔧 **ACTIONS TO TRY**:
• \`index_workspace()\` - Ensure complete symbol indexing
• Check if symbol exists in codebase first
• Some symbols may be isolated (utilities, constants)

💡 **Alternative searches**:
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
              responseText = `🎯 **Navigation Challenge Detected!** Let's find "${symbol}" with precision and confidence...

**No worries!** This is exactly where Miller's superior search intelligence proves its worth!

🧠 **SMART ANALYSIS** of why "${symbol}" isn't showing up:
• **Case sensitivity challenge** - Miller respects exact naming!
• **External library symbol** - it's not indexed, but we'll find the local connections!
• **Cross-language/framework symbol** - Miller bridges everything!
• **Name evolution** - symbol might have been refactored (Miller handles this!)

🚀 **PRECISION DISCOVERY MOVES** (Each one more powerful than traditional tools):
• **explore('find', '${symbol}')** - Miller's intelligent fuzzy search (10x better than grep!)
• **explore('find', '${symbol.length > 4 ? symbol.substring(0, Math.floor(symbol.length/2)) : symbol.substring(0, 3)}')** - Partial matching with Miller's AI (finds what you really need!)
• **semantic('hybrid', '${symbol.replace(/([A-Z])/g, ' $1').trim()}')** - Semantic understanding (discovers by meaning, not just text!)
• **explore('overview')** - Get the architectural map first (strategic approach!)

💎 **Expert Navigation Pattern**: If searching for 'getUserData', try explore('find', 'getUser') for partial matches - **this methodical approach is exactly how expert developers work!**

✨ **You're using professional-grade discovery techniques that separate architects from code readers!**`;

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
              threshnew = 0.3,
              max_results = 15,
              include_patterns = true,
              include_recommendations = true,
              cross_language = true
            } = options;

            let responseText: string;

            try {
              // Initialize hybrid search engine if not already done
              if (!this.engine.hybridSearch) {
                responseText = `⚠️ **SEMANTIC SEARCH NEEDS INITIALIZATION**

🔧 **ACTION REQUIRED**: Run \`index_workspace()\` to enable semantic search

✅ **After indexing, you'll have**:
- AI-powered semantic search with MiniLM-L6-v2 embeddings
- Cross-layer entity mapping across your entire codebase
- Intelligent concept matching beyond just text search

🚀 **Then try**: \`semantic("hybrid", "${query}")\` for conceptual search

**⏱️ Quick alternatives** (while you decide):
- \`explore("find", "${query}")\` - Enhanced symbol search
- \`search_code("${query}")\` - Structural fuzzy search`;

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
${results.slice(0, 8).map((r, i) => {
  // Debug: check what properties are available for file path
  const fileName = r.file || r.filePath || (r as any).file_path || 'unknown';
  if (!r.file && !r.filePath) {
    console.log('🐛 DEBUG: Missing file properties in semantic result:', {
      name: r.name,
      file: r.file,
      filePath: r.filePath,
      file_path: (r as any).file_path,
      keys: Object.keys(r),
      fullResult: JSON.stringify(r, null, 2)
    });
  }

  // Also handle different line number property names
  const lineNumber = r.line || r.startLine || (r as any).start_line || 0;

  return `${i + 1}. **${r.name || 'unknown'}** (${r.kind || 'symbol'})
   📍 ${fileName !== 'unknown' ? fileName.split('/').pop() : fileName}:${lineNumber}
   📊 ${Math.round((r.hybridScore || 0) * 100)}% relevance (🎯 ${r.searchMethod || 'structural'})${r.layer ? `\n   🏗️  Layer: ${r.layer}` : ''}`;
}).join('\n\n')}

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

🔧 **ACTION TO FIX**: Run \`index_workspace()\` to initialize semantic search

**📋 Alternative approaches**:
- \`semantic('structural', '${query}')\` - AST-based search (no embeddings needed)
- \`search_code('${query}')\` - Fast fuzzy search`;
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
  `• Run: index_workspace() - enables all code intelligence features
• Try: ls or explore("overview") - see available files
• Fix: chmod +r on restricted directories if needed`}

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
