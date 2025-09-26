/**
 * EmbeddingWorkerPool - Non-blocking embedding generation using Web Workers
 *
 * This solves the integration challenge by moving CPU-intensive embedding generation
 * off the main thread, allowing Miller's fast indexing to continue uninterrupted
 * while progressively building semantic search capabilities in the background.
 */

import type { Symbol } from '../database/schema.js';
import type { EmbeddingResult, CodeContext } from '../embeddings/miller-embedder.js';

export interface WorkerMessage {
  id: string;
  type: 'embed' | 'init' | 'batch' | 'health';
  payload: any;
}

export interface WorkerResponse {
  id: string;
  type: 'embedded' | 'initialized' | 'batch_complete' | 'health_ok' | 'error';
  payload: any;
  workerId: number;
}

export interface EmbeddingTask {
  id: string;
  symbolId: number;
  code: string;
  context?: CodeContext;
  priority: 'low' | 'normal' | 'high';
  timestamp: number;
}

export interface WorkerPoolConfig {
  workerCount?: number;
  maxQueueSize?: number;
  batchSize?: number;
  workerTimeout?: number;
  retryAttempts?: number;
  onEmbeddingComplete?: (symbolId: number, embedding: EmbeddingResult) => Promise<void>;
  onBatchComplete?: (results: Array<{symbolId: number; embedding: EmbeddingResult}>) => Promise<void>;
  onError?: (error: Error, task?: EmbeddingTask) => void;
  onProgress?: (completed: number, total: number, queueSize: number) => void;
}

export interface WorkerPoolStats {
  activeWorkers: number;
  queueSize: number;
  completed: number;
  failed: number;
  averageTime: number;
  throughput: number; // embeddings per second
}

export class EmbeddingWorkerPool {
  private workers: Worker[] = [];
  private taskQueue: EmbeddingTask[] = [];
  private activeTasks = new Map<string, EmbeddingTask>();
  private workerBusy = new Map<number, boolean>();
  private stats: WorkerPoolStats;
  private config: Required<WorkerPoolConfig>;
  private isInitialized = false;
  private initPromise: Promise<void> | null = null;

  constructor(config: WorkerPoolConfig = {}) {
    this.config = {
      workerCount: config.workerCount || Math.min(typeof navigator !== 'undefined' ? navigator.hardwareConcurrency || 4 : 4, 8),
      maxQueueSize: config.maxQueueSize || 10000,
      batchSize: config.batchSize || 10,
      workerTimeout: config.workerTimeout || 30000,
      retryAttempts: config.retryAttempts || 3,
      onEmbeddingComplete: config.onEmbeddingComplete || (async () => {}),
      onBatchComplete: config.onBatchComplete || (async () => {}),
      onError: config.onError || (() => {}),
      onProgress: config.onProgress || (() => {})
    };

    this.stats = {
      activeWorkers: 0,
      queueSize: 0,
      completed: 0,
      failed: 0,
      averageTime: 0,
      throughput: 0
    };
  }

  /**
   * Initialize the worker pool with embedding capability
   */
  async initialize(): Promise<void> {
    if (this.isInitialized) {
      return;
    }

    if (this.initPromise) {
      return this.initPromise;
    }

    this.initPromise = this.doInitialize();
    return this.initPromise;
  }

  private async doInitialize(): Promise<void> {
    console.log(`🔄 Initializing embedding worker pool with ${this.config.workerCount} workers...`);

    const workerUrl = new URL('./embedding-worker.js', import.meta.url);
    const initPromises: Promise<void>[] = [];

    for (let i = 0; i < this.config.workerCount; i++) {
      const worker = new Worker(workerUrl, { type: 'module' });
      this.workers.push(worker);
      this.workerBusy.set(i, false);

      // Set up message handling for this worker
      worker.onmessage = (event) => this.handleWorkerMessage(event, i);
      worker.onerror = (error) => this.handleWorkerError(error, i);

      // Initialize worker with embedding model
      const initPromise = new Promise<void>((resolve, reject) => {
        const timeoutId = setTimeout(() => {
          reject(new Error(`Worker ${i} initialization timeout`));
        }, this.config.workerTimeout);

        const messageHandler = (event: MessageEvent<WorkerResponse>) => {
          if (event.data.type === 'initialized') {
            clearTimeout(timeoutId);
            worker.removeEventListener('message', messageHandler);
            resolve();
          } else if (event.data.type === 'error') {
            clearTimeout(timeoutId);
            worker.removeEventListener('message', messageHandler);
            reject(new Error(event.data.payload.message));
          }
        };

        worker.addEventListener('message', messageHandler);
        worker.postMessage({
          id: `init-${i}`,
          type: 'init',
          payload: {
            maxFeatures: 1000,
            minDocFreq: 2,
            maxDocFreq: 0.8
          }
        } as WorkerMessage);
      });

      initPromises.push(initPromise);
    }

    try {
      await Promise.all(initPromises);
      this.stats.activeWorkers = this.workers.length;
      this.isInitialized = true;
      console.log(`✅ Embedding worker pool ready! ${this.workers.length} workers initialized`);
    } catch (error) {
      console.error('❌ Failed to initialize worker pool:', error);
      await this.terminate();
      throw error;
    }
  }

  /**
   * Queue a single embedding task
   */
  async queueEmbedding(
    symbolId: number,
    code: string,
    context?: CodeContext,
    priority: 'low' | 'normal' | 'high' = 'normal'
  ): Promise<void> {
    if (!this.isInitialized) {
      await this.initialize();
    }

    if (this.taskQueue.length >= this.config.maxQueueSize) {
      throw new Error(`Task queue full (${this.config.maxQueueSize} tasks)`);
    }

    const task: EmbeddingTask = {
      id: `${symbolId}-${Date.now()}-${Math.random().toString(36).slice(2)}`,
      symbolId,
      code,
      context,
      priority,
      timestamp: Date.now()
    };

    // Insert based on priority
    if (priority === 'high') {
      this.taskQueue.unshift(task);
    } else {
      this.taskQueue.push(task);
    }

    this.stats.queueSize = this.taskQueue.length;
    this.processQueue();
  }

  /**
   * Queue multiple embeddings as a batch
   */
  async queueBatch(
    symbols: Array<{ symbolId: number; code: string; context?: CodeContext }>,
    priority: 'low' | 'normal' | 'high' = 'normal'
  ): Promise<void> {
    const batchPromises = symbols.map(({ symbolId, code, context }) =>
      this.queueEmbedding(symbolId, code, context, priority)
    );

    await Promise.all(batchPromises);
    console.log(`📦 Queued batch of ${symbols.length} embeddings`);
  }

  /**
   * Process the task queue by assigning tasks to available workers
   */
  private processQueue(): void {
    if (this.taskQueue.length === 0) {
      return;
    }

    // Find available workers
    const availableWorkers = this.workers
      .map((worker, index) => ({ worker, index }))
      .filter(({ index }) => !this.workerBusy.get(index));

    if (availableWorkers.length === 0) {
      return; // All workers busy
    }

    // Assign tasks to available workers
    for (const { worker, index } of availableWorkers) {
      if (this.taskQueue.length === 0) break;

      const task = this.taskQueue.shift()!;
      this.workerBusy.set(index, true);
      this.activeTasks.set(task.id, task);
      this.stats.queueSize = this.taskQueue.length;

      // Send task to worker
      worker.postMessage({
        id: task.id,
        type: 'embed',
        payload: {
          code: task.code,
          context: task.context
        }
      } as WorkerMessage);
    }

    // Update progress
    this.config.onProgress(
      this.stats.completed,
      this.stats.completed + this.activeTasks.size + this.taskQueue.length,
      this.taskQueue.length
    );
  }

  /**
   * Handle messages from workers
   */
  private async handleWorkerMessage(event: MessageEvent<WorkerResponse>, workerId: number): Promise<void> {
    const { id, type, payload } = event.data;

    switch (type) {
      case 'embedded': {
        const task = this.activeTasks.get(id);
        if (!task) return;

        this.activeTasks.delete(id);
        this.workerBusy.set(workerId, false);
        this.stats.completed++;

        // Update average time
        const taskTime = Date.now() - task.timestamp;
        this.stats.averageTime = (this.stats.averageTime * (this.stats.completed - 1) + taskTime) / this.stats.completed;

        // Update throughput (embeddings per second over last 10 seconds)
        this.updateThroughput();

        try {
          await this.config.onEmbeddingComplete(task.symbolId, payload.embedding);
        } catch (error) {
          console.error(`❌ Error in onEmbeddingComplete callback:`, error);
          this.config.onError(error as Error, task);
        }

        // Process more tasks
        this.processQueue();
        break;
      }

      case 'error': {
        const task = this.activeTasks.get(id);
        if (task) {
          this.activeTasks.delete(id);
          this.workerBusy.set(workerId, false);
          this.stats.failed++;

          console.error(`❌ Worker ${workerId} embedding failed for task ${id}:`, payload.message);
          this.config.onError(new Error(payload.message), task);

          // Retry logic could go here
        }

        // Process more tasks
        this.processQueue();
        break;
      }

      case 'health_ok': {
        // Worker health check response
        break;
      }
    }
  }

  /**
   * Handle worker errors
   */
  private handleWorkerError(error: ErrorEvent, workerId: number): void {
    console.error(`❌ Worker ${workerId} error:`, error);
    this.workerBusy.set(workerId, false);

    // Remove any active tasks for this worker
    for (const [taskId, task] of this.activeTasks.entries()) {
      // Simple heuristic: if task is new, it might be from the failed worker
      if (Date.now() - task.timestamp > this.config.workerTimeout) {
        this.activeTasks.delete(taskId);
        this.stats.failed++;
        this.config.onError(new Error(`Worker ${workerId} failed`), task);
      }
    }

    // Continue processing
    this.processQueue();
  }

  /**
   * Update throughput calculation
   */
  private updateThroughput(): void {
    // Simple throughput calculation - could be more sophisticated
    if (this.stats.averageTime > 0) {
      this.stats.throughput = 1000 / this.stats.averageTime; // embeddings per second
    }
  }

  /**
   * Embed a query immediately (for hybrid search)
   * This provides synchronous query embedding for search operations
   */
  async embedQuery(query: string, context?: CodeContext): Promise<EmbeddingResult> {
    if (!this.isInitialized) {
      await this.initialize();
    }

    return new Promise<EmbeddingResult>((resolve, reject) => {
      // Find an available worker
      const availableWorkerIndex = this.workers.findIndex((_, index) => !this.workerBusy.get(index));

      if (availableWorkerIndex === -1) {
        // If no workers available, reject - queries should be fast
        reject(new Error('No workers available for immediate query embedding'));
        return;
      }

      const worker = this.workers[availableWorkerIndex];
      const queryId = `query-${Date.now()}-${Math.random().toString(36).slice(2)}`;

      // Set up one-time message handler for this query
      const messageHandler = (event: MessageEvent<WorkerResponse>) => {
        if (event.data.id === queryId) {
          worker.removeEventListener('message', messageHandler);

          if (event.data.type === 'embedded') {
            resolve(event.data.payload.embedding);
          } else if (event.data.type === 'error') {
            reject(new Error(event.data.payload.message));
          }
        }
      };

      // Add timeout for query
      const timeout = setTimeout(() => {
        worker.removeEventListener('message', messageHandler);
        reject(new Error('Query embedding timeout'));
      }, 10000);

      worker.addEventListener('message', messageHandler);

      // Send immediate embedding request
      worker.postMessage({
        id: queryId,
        type: 'embed',
        payload: {
          code: query,
          context: context
        }
      } as WorkerMessage);

      // Clear timeout when resolved
      resolve = ((originalResolve) => (result: EmbeddingResult) => {
        clearTimeout(timeout);
        originalResolve(result);
      })(resolve);

      reject = ((originalReject) => (error: Error) => {
        clearTimeout(timeout);
        originalReject(error);
      })(reject);
    });
  }

  /**
   * Get current statistics
   */
  getStats(): WorkerPoolStats {
    return { ...this.stats, queueSize: this.taskQueue.length };
  }

  /**
   * Wait for all current tasks to complete
   */
  async waitForCompletion(): Promise<void> {
    while (this.taskQueue.length > 0 || this.activeTasks.size > 0) {
      await new Promise(resolve => setTimeout(resolve, 100));
    }
  }

  /**
   * Check if workers are healthy
   */
  async healthCheck(): Promise<boolean> {
    if (!this.isInitialized) {
      return false;
    }

    const healthPromises = this.workers.map((worker, index) => {
      return new Promise<boolean>((resolve) => {
        const timeout = setTimeout(() => resolve(false), 5000);

        const messageHandler = (event: MessageEvent<WorkerResponse>) => {
          if (event.data.type === 'health_ok') {
            clearTimeout(timeout);
            worker.removeEventListener('message', messageHandler);
            resolve(true);
          }
        };

        worker.addEventListener('message', messageHandler);
        worker.postMessage({
          id: `health-${index}`,
          type: 'health',
          payload: {}
        } as WorkerMessage);
      });
    });

    const results = await Promise.all(healthPromises);
    return results.every(healthy => healthy);
  }

  /**
   * Terminate all workers and clean up
   */
  async terminate(): Promise<void> {
    console.log('🛑 Terminating embedding worker pool...');

    for (const worker of this.workers) {
      worker.terminate();
    }

    this.workers = [];
    this.taskQueue = [];
    this.activeTasks.clear();
    this.workerBusy.clear();
    this.isInitialized = false;
    this.initPromise = null;

    this.stats = {
      activeWorkers: 0,
      queueSize: 0,
      completed: 0,
      failed: 0,
      averageTime: 0,
      throughput: 0
    };

    console.log('✅ Worker pool terminated');
  }
}

export default EmbeddingWorkerPool;