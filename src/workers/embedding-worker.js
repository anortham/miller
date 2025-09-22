/**
 * Embedding Worker - Runs MillerEmbedder in a Web Worker thread
 *
 * This worker handles CPU-intensive embedding generation without blocking
 * the main thread, enabling Miller to maintain fast indexing performance
 * while building semantic search capabilities in the background.
 */

// Import MillerEmbedder (using dynamic import for worker compatibility)
let MillerEmbedder;
let embedder = null;
let isInitialized = false;

// Worker message handling
self.onmessage = async function(event) {
  const { id, type, payload } = event.data;

  try {
    switch (type) {
      case 'init':
        await handleInit(id, payload);
        break;

      case 'embed':
        await handleEmbed(id, payload);
        break;

      case 'batch':
        await handleBatch(id, payload);
        break;

      case 'health':
        handleHealth(id);
        break;

      default:
        sendError(id, `Unknown message type: ${type}`);
    }
  } catch (error) {
    sendError(id, error.message, error);
  }
};

/**
 * Initialize the embedder with specified model
 */
async function handleInit(id, payload) {
  try {
    if (isInitialized) {
      sendResponse(id, 'initialized', { message: 'Already initialized' });
      return;
    }

    // Dynamic import for worker compatibility
    const module = await import('../embeddings/miller-embedder.js');
    MillerEmbedder = module.default || module.MillerEmbedder;

    // Initialize embedder
    embedder = new MillerEmbedder();
    await embedder.initialize(payload.modelType || 'fast');

    isInitialized = true;

    sendResponse(id, 'initialized', {
      message: 'Embedder initialized successfully',
      modelInfo: embedder.getModelInfo()
    });

  } catch (error) {
    sendError(id, `Failed to initialize embedder: ${error.message}`, error);
  }
}

/**
 * Generate embedding for a single code snippet
 */
async function handleEmbed(id, payload) {
  if (!isInitialized || !embedder) {
    sendError(id, 'Embedder not initialized');
    return;
  }

  try {
    const { code, context } = payload;

    if (!code || typeof code !== 'string') {
      sendError(id, 'Invalid code provided');
      return;
    }

    const startTime = Date.now();
    const embedding = await embedder.embedCode(code, context);
    const duration = Date.now() - startTime;

    sendResponse(id, 'embedded', {
      embedding,
      duration,
      codeLength: code.length
    });

  } catch (error) {
    sendError(id, `Embedding generation failed: ${error.message}`, error);
  }
}

/**
 * Generate embeddings for multiple code snippets
 */
async function handleBatch(id, payload) {
  if (!isInitialized || !embedder) {
    sendError(id, 'Embedder not initialized');
    return;
  }

  try {
    const { codeSnippets } = payload;

    if (!Array.isArray(codeSnippets)) {
      sendError(id, 'Invalid batch data provided');
      return;
    }

    const startTime = Date.now();
    const results = [];

    for (let i = 0; i < codeSnippets.length; i++) {
      const { code, context } = codeSnippets[i];

      try {
        const embedding = await embedder.embedCode(code, context);
        results.push({
          index: i,
          embedding,
          success: true
        });
      } catch (error) {
        results.push({
          index: i,
          error: error.message,
          success: false
        });
      }

      // Send progress updates for large batches
      if (i % 10 === 0 && i > 0) {
        sendResponse(id, 'batch_progress', {
          completed: i,
          total: codeSnippets.length,
          percentage: Math.round((i / codeSnippets.length) * 100)
        });
      }
    }

    const duration = Date.now() - startTime;

    sendResponse(id, 'batch_complete', {
      results,
      duration,
      totalSnippets: codeSnippets.length,
      successful: results.filter(r => r.success).length,
      failed: results.filter(r => !r.success).length
    });

  } catch (error) {
    sendError(id, `Batch processing failed: ${error.message}`, error);
  }
}

/**
 * Health check - verify worker is responsive
 */
function handleHealth(id) {
  sendResponse(id, 'health_ok', {
    initialized: isInitialized,
    modelInfo: isInitialized ? embedder?.getModelInfo() : null,
    timestamp: Date.now()
  });
}

/**
 * Send successful response to main thread
 */
function sendResponse(id, type, payload) {
  self.postMessage({
    id,
    type,
    payload,
    workerId: getWorkerId(),
    timestamp: Date.now()
  });
}

/**
 * Send error response to main thread
 */
function sendError(id, message, error = null) {
  self.postMessage({
    id,
    type: 'error',
    payload: {
      message,
      stack: error?.stack,
      name: error?.name
    },
    workerId: getWorkerId(),
    timestamp: Date.now()
  });
}

/**
 * Get worker identifier (simple implementation)
 */
function getWorkerId() {
  // In a real implementation, this could be passed during init
  // For now, use a simple approach
  return Math.floor(Math.random() * 1000);
}

/**
 * Handle worker errors
 */
self.onerror = function(error) {
  console.error('Worker error:', error);
  sendError('worker-error', `Worker error: ${error.message}`, error);
};

/**
 * Handle unhandled promise rejections
 */
self.onunhandledrejection = function(event) {
  console.error('Worker unhandled rejection:', event.reason);
  sendError('worker-rejection', `Unhandled rejection: ${event.reason}`, event.reason);
};

// Log that worker is ready
console.log('🧵 Embedding worker ready for initialization');