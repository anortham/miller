* Add support for qmljs https://github.com/yuja/tree-sitter-qmljs

* Web worker additions for performance: 

Bun is **single-threaded for JavaScript execution** (like Node.js) but uses **multiple threads internally** for I/O operations and some native operations.

## Bun's Threading Model

### Main Thread (JavaScript)
- Your JavaScript code runs on a single thread with an event loop
- This is the V8/JavaScriptCore model - single-threaded JS execution

### Internal Thread Pool
- Bun uses multiple threads under the hood for:
  - File system operations
  - Network I/O  
  - SQLite operations
  - Native module operations

### Worker Threads
Bun supports Web Workers and Worker Threads:
```javascript
// Web Worker API
const worker = new Worker("worker.js");

// Node.js-compatible worker_threads
import { Worker } from "worker_threads";
```

## For Your TreeSitter Project

This is actually relevant for your use case! If you need parallel parsing:

```javascript
// You could spawn workers for parallel parsing
const files = await getAllFiles();
const workers = [];

// Create a worker pool
for (let i = 0; i < navigator.hardwareConcurrency; i++) {
  workers.push(new Worker("./parser-worker.js"));
}

// Distribute parsing work across workers
// Each worker can parse different files simultaneously
```

### Native Module Threading
If you're using native TreeSitter bindings, those can use multiple threads internally while appearing synchronous to JavaScript.

## Practical Implications

For your MCP server parsing thousands of hospital SQL files:
- **Single-threaded JS**: Your main logic is single-threaded
- **Parallel I/O**: File reads are automatically parallel
- **Worker option**: You can spawn workers for CPU-intensive parsing
- **Native threading**: TreeSitter WASM might use SharedArrayBuffer for parallelism

Want to benchmark parallel vs serial parsing for your codebase? Workers could significantly speed up the initial indexing phase.
