/**
 * Embedding Process - Standalone Bun script for CPU-intensive embedding generation
 *
 * This child process runs transformers.js in complete isolation from the main thread,
 * preventing UI lockups during semantic indexing. Communicates via IPC.
 */

import MillerEmbedder from '../embeddings/miller-embedder.js';
import type { EmbeddingResult, CodeContext } from '../embeddings/miller-embedder.js';

interface ProcessMessage {
  id: string;
  type: 'init' | 'embed' | 'health' | 'shutdown';
  payload: any;
}

interface ProcessResponse {
  id: string;
  type: 'initialized' | 'embedded' | 'health_ok' | 'error' | 'shutdown_complete';
  payload: any;
  processId: number;
}

// Process state
let embedder: MillerEmbedder | null = null;
let isInitialized = false;
let isShuttingDown = false;
const processId = process.pid || Math.random();

console.log(`🧵 Embedding process ${processId} starting...`);

// Handle IPC messages from parent process
process.on('message', async (message: ProcessMessage) => {
  if (isShuttingDown) {
    return; // Ignore messages during shutdown
  }

  try {
    switch (message.type) {
      case 'init':
        await handleInit(message.id, message.payload);
        break;

      case 'embed':
        await handleEmbed(message.id, message.payload);
        break;

      case 'health':
        handleHealth(message.id);
        break;

      case 'shutdown':
        await handleShutdown(message.id);
        break;

      default:
        sendError(message.id, `Unknown message type: ${message.type}`);
    }
  } catch (error) {
    sendError(message.id, error.message, error);
  }
});

/**
 * Initialize the embedder with specified model
 */
async function handleInit(id: string, payload: { modelType?: string }) {
  try {
    if (isInitialized) {
      sendResponse(id, 'initialized', {
        message: 'Already initialized',
        processId
      });
      return;
    }

    console.log(`🔄 Process ${processId}: Initializing embedder...`);

    // Initialize embedder
    embedder = new MillerEmbedder();
    await embedder.initialize(payload.modelType || 'fast');

    isInitialized = true;

    sendResponse(id, 'initialized', {
      message: 'Embedder initialized successfully',
      modelInfo: embedder.getModelInfo(),
      processId
    });

    console.log(`✅ Process ${processId}: Embedder ready`);

  } catch (error) {
    console.error(`❌ Process ${processId}: Failed to initialize:`, error);
    sendError(id, `Failed to initialize embedder: ${error.message}`, error);
  }
}

/**
 * Generate embedding for a code snippet
 */
async function handleEmbed(id: string, payload: { code: string; context?: CodeContext }) {
  if (!isInitialized || !embedder) {
    sendError(id, 'Embedder not initialized');
    return;
  }

  try {
    const { code, context } = payload;

    if (!code || !code.trim()) {
      sendError(id, 'Empty code provided');
      return;
    }

    console.log(`🔍 Process ${processId}: Embedding ${code.length} chars...`);

    const startTime = Date.now();
    const embedding = await embedder.embedCode(code, context);
    const duration = Date.now() - startTime;

    sendResponse(id, 'embedded', {
      embedding,
      duration,
      processId
    });

    console.log(`✅ Process ${processId}: Embedded in ${duration}ms`);

  } catch (error) {
    console.error(`❌ Process ${processId}: Embedding failed:`, error);
    sendError(id, `Failed to generate embedding: ${error.message}`, error);
  }
}

/**
 * Health check
 */
function handleHealth(id: string) {
  sendResponse(id, 'health_ok', {
    processId,
    isInitialized,
    uptime: process.uptime(),
    memoryUsage: process.memoryUsage()
  });
}

/**
 * Graceful shutdown
 */
async function handleShutdown(id: string) {
  console.log(`🛑 Process ${processId}: Shutting down...`);

  isShuttingDown = true;

  try {
    // Clear embedder cache if possible
    if (embedder) {
      embedder.clearCache();
    }

    sendResponse(id, 'shutdown_complete', {
      processId,
      message: 'Process shutdown complete'
    });

    console.log(`✅ Process ${processId}: Shutdown complete`);

    // Exit after a brief delay to ensure message is sent
    setTimeout(() => {
      process.exit(0);
    }, 100);

  } catch (error) {
    console.error(`❌ Process ${processId}: Shutdown error:`, error);
    process.exit(1);
  }
}

/**
 * Send successful response to parent process
 */
function sendResponse(id: string, type: ProcessResponse['type'], payload: any) {
  if (!process.send) {
    console.error(`❌ Process ${processId}: No IPC channel available`);
    return;
  }

  const response: ProcessResponse = {
    id,
    type,
    payload,
    processId
  };

  process.send(response);
}

/**
 * Send error response to parent process
 */
function sendError(id: string, message: string, error?: Error) {
  if (!process.send) {
    console.error(`❌ Process ${processId}: No IPC channel available`);
    return;
  }

  const response: ProcessResponse = {
    id,
    type: 'error',
    payload: {
      message,
      stack: error?.stack,
      name: error?.name,
      processId
    },
    processId
  };

  process.send(response);
}

/**
 * Handle process cleanup on exit
 */
process.on('SIGTERM', () => {
  console.log(`🛑 Process ${processId}: Received SIGTERM, exiting...`);
  process.exit(0);
});

process.on('SIGINT', () => {
  console.log(`🛑 Process ${processId}: Received SIGINT, exiting...`);
  process.exit(0);
});

process.on('uncaughtException', (error) => {
  console.error(`💥 Process ${processId}: Uncaught exception:`, error);
  process.exit(1);
});

process.on('unhandledRejection', (reason, promise) => {
  console.error(`💥 Process ${processId}: Unhandled rejection:`, reason);
  process.exit(1);
});

console.log(`🧵 Process ${processId}: Ready for messages`);