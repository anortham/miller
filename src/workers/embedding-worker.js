/**
 * Embedding Worker - TF-IDF implementation for Web Workers
 *
 * This worker handles CPU-intensive TF-IDF embedding generation without blocking
 * the main thread. Uses pure JavaScript with no native dependencies, solving
 * the transformers.js compatibility issues in Bun Web Workers.
 */

// Import our TF-IDF embedder
import { TFIDFEmbedder } from '../embeddings/tfidf-embedder.js';

// Worker state
let embedder = null;
let isInitialized = false;

// TF-IDF Embedding worker ready for initialization

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

      case 'addDocument':
        await handleAddDocument(id, payload);
        break;

      case 'buildVocabulary':
        await handleBuildVocabulary(id, payload);
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
 * Initialize the TF-IDF embedder
 */
async function handleInit(id, payload) {
  try {
    if (isInitialized) {
      sendResponse(id, 'initialized', {
        message: 'Already initialized',
        stats: embedder.getStats()
      });
      return;
    }

    // Initializing TF-IDF embedder for code search

    // Create embedder with configuration
    const config = payload || {};
    embedder = new TFIDFEmbedder({
      maxFeatures: config.maxFeatures || 1000,
      minDocFreq: config.minDocFreq || 2,
      maxDocFreq: config.maxDocFreq || 0.8
    });

    isInitialized = true;

    sendResponse(id, 'initialized', {
      message: 'TF-IDF embedder initialized successfully',
      model: 'tfidf-code-v1',
      stats: embedder.getStats()
    });

    // TF-IDF embedder ready for code embeddings

  } catch (error) {
    console.error('❌ Failed to initialize TF-IDF embedder:', error);
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

    // Generating TF-IDF embedding

    const startTime = Date.now();
    const embedding = embedder.embed(code);
    const duration = Date.now() - startTime;

    sendResponse(id, 'embedded', {
      embedding,
      duration,
      codeLength: code.length,
      confidence: embedding.confidence
    });

    // TF-IDF embedding generated

  } catch (error) {
    console.error('❌ TF-IDF embedding generation failed:', error);
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

    // Processing TF-IDF batch

    const startTime = Date.now();
    const results = [];

    for (let i = 0; i < codeSnippets.length; i++) {
      const { code, context } = codeSnippets[i];

      try {
        const embedding = embedder.embed(code);
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
      if (i % 50 === 0 && i > 0) {
        sendResponse(id, 'batch_progress', {
          completed: i,
          total: codeSnippets.length,
          percentage: Math.round((i / codeSnippets.length) * 100)
        });
      }

      // Yield periodically to prevent blocking
      if (i % 10 === 0) {
        await new Promise(resolve => setTimeout(resolve, 1));
      }
    }

    const duration = Date.now() - startTime;

    sendResponse(id, 'batch_complete', {
      results,
      duration,
      totalSnippets: codeSnippets.length,
      successful: results.filter(r => r.success).length,
      failed: results.filter(r => !r.success).length,
      averageTime: duration / codeSnippets.length
    });

    // TF-IDF batch complete

  } catch (error) {
    console.error('❌ TF-IDF batch processing failed:', error);
    sendError(id, `Batch processing failed: ${error.message}`, error);
  }
}

/**
 * Add document to corpus for vocabulary building
 */
async function handleAddDocument(id, payload) {
  if (!isInitialized || !embedder) {
    sendError(id, 'Embedder not initialized');
    return;
  }

  try {
    const { docId, code } = payload;

    if (!docId || !code) {
      sendError(id, 'Document ID and code are required');
      return;
    }

    embedder.addDocument(docId, code);

    sendResponse(id, 'document_added', {
      docId,
      stats: embedder.getStats()
    });

  } catch (error) {
    console.error('❌ Failed to add document:', error);
    sendError(id, `Failed to add document: ${error.message}`, error);
  }
}

/**
 * Build vocabulary from added documents
 */
async function handleBuildVocabulary(id, payload) {
  if (!isInitialized || !embedder) {
    sendError(id, 'Embedder not initialized');
    return;
  }

  try {
    const startTime = Date.now();
    embedder.buildVocabulary();
    const duration = Date.now() - startTime;

    const stats = embedder.getStats();

    sendResponse(id, 'vocabulary_built', {
      stats,
      duration,
      message: `Vocabulary built: ${stats.vocabularySize} terms from ${stats.totalDocuments} documents`
    });

  } catch (error) {
    console.error('❌ Failed to build vocabulary:', error);
    sendError(id, `Failed to build vocabulary: ${error.message}`, error);
  }
}

/**
 * Health check - verify worker is responsive
 */
function handleHealth(id) {
  const stats = isInitialized && embedder ? embedder.getStats() : null;

  sendResponse(id, 'health_ok', {
    initialized: isInitialized,
    model: 'tfidf-code-v1',
    stats,
    timestamp: Date.now(),
    workerId: getWorkerId()
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
  console.error(`❌ Worker error: ${message}`, error);

  self.postMessage({
    id,
    type: 'error',
    payload: {
      message,
      stack: error?.stack,
      name: error?.name,
      workerId: getWorkerId()
    },
    workerId: getWorkerId(),
    timestamp: Date.now()
  });
}

/**
 * Get worker identifier
 */
function getWorkerId() {
  return `tfidf-worker-${Math.floor(Math.random() * 1000)}`;
}

/**
 * Handle worker errors
 */
self.onerror = function(error) {
  console.error('❌ Worker error:', error);
  sendError('worker-error', `Worker error: ${error.message}`, error);
};

/**
 * Handle unhandled promise rejections
 */
self.onunhandledrejection = function(event) {
  console.error('❌ Worker unhandled rejection:', event.reason);
  sendError('worker-rejection', `Unhandled rejection: ${event.reason}`, event.reason);
};

// TF-IDF Embedding worker initialized and ready