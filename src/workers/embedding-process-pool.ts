/**
 * EmbeddingProcessPool - Non-blocking embedding generation using child processes
 *
 * Solves the UI blocking issue by moving CPU-intensive transformers.js processing
 * to isolated child processes, allowing the main thread to remain responsive.
 */

import { spawn, type Subprocess } from 'bun';
import type { EmbeddingResult, CodeContext } from '../embeddings/miller-embedder.js';
import { log, LogLevel } from '../utils/logger.js';

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

export interface EmbeddingTask {
  id: string;
  symbolId: string;
  code: string;
  context?: CodeContext;
  priority: 'low' | 'normal' | 'high';
  timestamp: number;
  resolve: (result: EmbeddingResult) => void;
  reject: (error: Error) => void;
}

export interface ProcessPoolConfig {
  processCount?: number;
  maxQueueSize?: number;
  processTimeout?: number;
  retryAttempts?: number;
  onEmbeddingComplete?: (symbolId: string, embedding: EmbeddingResult) => Promise<void>;
  onProgress?: (completed: number, total: number, queueSize: number) => void;
  onError?: (error: Error, task?: EmbeddingTask) => void;
}

export interface ProcessPoolStats {
  activeProcesses: number;
  queueSize: number;
  completed: number;
  failed: number;
  averageTime: number;
  throughput: number; // embeddings per second
}

interface ManagedProcess {
  process: Subprocess;
  id: number;
  isInitialized: boolean;
  isBusy: boolean;
  currentTask?: EmbeddingTask;
  completedTasks: number;
  failedTasks: number;
  startTime: number;
}

export class EmbeddingProcessPool {
  private processes: ManagedProcess[] = [];
  private taskQueue: EmbeddingTask[] = [];
  private activeTasks = new Map<string, EmbeddingTask>();
  private stats: ProcessPoolStats;
  private config: Required<ProcessPoolConfig>;
  private isInitialized = false;
  private isShuttingDown = false;
  private taskIdCounter = 0;
  private embeddingTimes: number[] = [];

  constructor(config: ProcessPoolConfig = {}) {
    this.config = {
      processCount: config.processCount || Math.min(navigator?.hardwareConcurrency || 4, 4), // Limit to 4 for stability
      maxQueueSize: config.maxQueueSize || 5000,
      processTimeout: config.processTimeout || 30000,
      retryAttempts: config.retryAttempts || 2,
      onEmbeddingComplete: config.onEmbeddingComplete || (async () => {}),
      onProgress: config.onProgress || (() => {}),
      onError: config.onError || (() => {})
    };

    this.stats = {
      activeProcesses: 0,
      queueSize: 0,
      completed: 0,
      failed: 0,
      averageTime: 0,
      throughput: 0
    };

    log.engine(LogLevel.INFO, `🏭 Creating embedding process pool with ${this.config.processCount} processes`);
  }

  /**
   * Initialize the process pool
   */
  async initialize(): Promise<void> {
    if (this.isInitialized) {
      return;
    }

    log.engine(LogLevel.INFO, `🔄 Initializing embedding process pool...`);

    try {
      // Spawn child processes
      const initPromises: Promise<void>[] = [];

      for (let i = 0; i < this.config.processCount; i++) {
        const processInfo = await this.spawnProcess(i);
        this.processes.push(processInfo);

        // Initialize each process
        const initPromise = this.initializeProcess(processInfo);
        initPromises.push(initPromise);
      }

      // Wait for all processes to initialize
      await Promise.all(initPromises);

      this.stats.activeProcesses = this.processes.length;
      this.isInitialized = true;

      log.engine(LogLevel.INFO, `✅ Embedding process pool ready! ${this.processes.length} processes initialized`);

    } catch (error) {
      log.engine(LogLevel.ERROR, `❌ Failed to initialize process pool:`, error);
      await this.terminate();
      throw error;
    }
  }

  /**
   * Spawn a single child process
   */
  private async spawnProcess(id: number): Promise<ManagedProcess> {
    log.engine(LogLevel.DEBUG, `🚀 Spawning embedding process ${id}...`);

    const process = spawn({
      cmd: ['bun', 'run', `${import.meta.dir}/embedding-process.ts`],
      ipc: true,
      stdio: ['pipe', 'pipe', 'pipe']
    });

    const processInfo: ManagedProcess = {
      process,
      id,
      isInitialized: false,
      isBusy: false,
      completedTasks: 0,
      failedTasks: 0,
      startTime: Date.now()
    };

    // Set up message handling
    process.on('message', (message: ProcessResponse) => {
      this.handleProcessMessage(message, processInfo);
    });

    process.on('error', (error) => {
      log.engine(LogLevel.ERROR, `❌ Process ${id} error:`, error);
      this.handleProcessError(error, processInfo);
    });

    process.on('exit', (code) => {
      log.engine(LogLevel.WARN, `🔚 Process ${id} exited with code ${code}`);
      this.handleProcessExit(code, processInfo);
    });

    return processInfo;
  }

  /**
   * Initialize a single process
   */
  private async initializeProcess(processInfo: ManagedProcess): Promise<void> {
    return new Promise((resolve, reject) => {
      const timeoutId = setTimeout(() => {
        reject(new Error(`Process ${processInfo.id} initialization timeout`));
      }, this.config.processTimeout);

      const messageHandler = (message: ProcessResponse) => {
        if (message.type === 'initialized') {
          clearTimeout(timeoutId);
          processInfo.process.off('message', messageHandler);
          processInfo.isInitialized = true;
          resolve();
        } else if (message.type === 'error') {
          clearTimeout(timeoutId);
          processInfo.process.off('message', messageHandler);
          reject(new Error(message.payload.message));
        }
      };

      processInfo.process.on('message', messageHandler);

      // Send initialization message
      processInfo.process.send({
        id: `init-${processInfo.id}`,
        type: 'init',
        payload: { modelType: 'fast' }
      } as ProcessMessage);
    });
  }

  /**
   * Queue an embedding task
   */
  async embed(symbolId: string, code: string, context?: CodeContext, priority: 'low' | 'normal' | 'high' = 'normal'): Promise<EmbeddingResult> {
    if (!this.isInitialized) {
      throw new Error('Process pool not initialized');
    }

    if (this.isShuttingDown) {
      throw new Error('Process pool is shutting down');
    }

    if (this.taskQueue.length >= this.config.maxQueueSize) {
      throw new Error('Task queue is full');
    }

    return new Promise((resolve, reject) => {
      const task: EmbeddingTask = {
        id: `task-${++this.taskIdCounter}`,
        symbolId,
        code,
        context,
        priority,
        timestamp: Date.now(),
        resolve,
        reject
      };

      // Add to queue with priority
      if (priority === 'high') {
        this.taskQueue.unshift(task);
      } else {
        this.taskQueue.push(task);
      }

      this.stats.queueSize = this.taskQueue.length;

      // Try to assign task immediately
      this.processQueue();
    });
  }

  /**
   * Process the task queue
   */
  private processQueue(): void {
    if (this.taskQueue.length === 0) {
      return;
    }

    // Find available processes
    const availableProcesses = this.processes.filter(p =>
      p.isInitialized && !p.isBusy && !this.isShuttingDown
    );

    if (availableProcesses.length === 0) {
      return; // All processes busy
    }

    // Assign tasks to available processes
    for (const processInfo of availableProcesses) {
      if (this.taskQueue.length === 0) break;

      const task = this.taskQueue.shift()!;
      this.assignTaskToProcess(task, processInfo);
    }

    this.stats.queueSize = this.taskQueue.length;
  }

  /**
   * Assign a task to a specific process
   */
  private assignTaskToProcess(task: EmbeddingTask, processInfo: ManagedProcess): void {
    processInfo.isBusy = true;
    processInfo.currentTask = task;
    this.activeTasks.set(task.id, task);

    const message: ProcessMessage = {
      id: task.id,
      type: 'embed',
      payload: {
        code: task.code,
        context: task.context
      }
    };

    processInfo.process.send(message);
    log.engine(LogLevel.DEBUG, `📤 Assigned task ${task.id} to process ${processInfo.id}`);
  }

  /**
   * Handle messages from child processes
   */
  private async handleProcessMessage(message: ProcessResponse, processInfo: ManagedProcess): Promise<void> {
    const { id, type, payload } = message;

    switch (type) {
      case 'embedded': {
        const task = this.activeTasks.get(id);
        if (!task) {
          log.engine(LogLevel.WARN, `⚠️ Received result for unknown task ${id}`);
          return;
        }

        try {
          // Mark process as available
          processInfo.isBusy = false;
          processInfo.currentTask = undefined;
          processInfo.completedTasks++;
          this.activeTasks.delete(id);

          // Update stats
          this.stats.completed++;
          this.embeddingTimes.push(payload.duration);
          this.updatePerformanceStats();

          // Resolve the promise with the embedding result
          task.resolve(payload.embedding);

          // Call completion callback
          await this.config.onEmbeddingComplete(task.symbolId, payload.embedding);

          // Update progress
          this.config.onProgress(this.stats.completed, this.stats.completed + this.stats.queueSize, this.stats.queueSize);

          log.engine(LogLevel.DEBUG, `✅ Task ${id} completed by process ${processInfo.id} in ${payload.duration}ms`);

          // Process next tasks in queue
          this.processQueue();

        } catch (error) {
          log.engine(LogLevel.ERROR, `❌ Error handling embedded result for task ${id}:`, error);
          this.handleTaskError(task, error);
        }
        break;
      }

      case 'error': {
        const task = this.activeTasks.get(id);
        if (task) {
          this.handleTaskError(task, new Error(payload.message));
        }
        break;
      }

      case 'health_ok': {
        log.engine(LogLevel.DEBUG, `💓 Health check OK for process ${processInfo.id}`);
        break;
      }

      default:
        log.engine(LogLevel.WARN, `⚠️ Unknown message type from process ${processInfo.id}: ${type}`);
    }
  }

  /**
   * Handle task errors
   */
  private handleTaskError(task: EmbeddingTask, error: Error): void {
    // Mark process as available
    const processInfo = this.processes.find(p => p.currentTask?.id === task.id);
    if (processInfo) {
      processInfo.isBusy = false;
      processInfo.currentTask = undefined;
      processInfo.failedTasks++;
    }

    this.activeTasks.delete(task.id);
    this.stats.failed++;

    // Call error callback
    this.config.onError(error, task);

    // Reject the promise
    task.reject(error);

    log.engine(LogLevel.ERROR, `❌ Task ${task.id} failed:`, error);

    // Continue processing queue
    this.processQueue();
  }

  /**
   * Handle process errors
   */
  private handleProcessError(error: Error, processInfo: ManagedProcess): void {
    log.engine(LogLevel.ERROR, `💥 Process ${processInfo.id} error:`, error);

    // Mark process as failed and handle current task
    if (processInfo.currentTask) {
      this.handleTaskError(processInfo.currentTask, error);
    }

    // TODO: Implement process restart logic here if needed
  }

  /**
   * Handle process exit
   */
  private handleProcessExit(code: number | null, processInfo: ManagedProcess): void {
    log.engine(LogLevel.WARN, `🔚 Process ${processInfo.id} exited with code ${code}`);

    // Handle current task if any
    if (processInfo.currentTask) {
      this.handleTaskError(processInfo.currentTask, new Error(`Process exited with code ${code}`));
    }

    // Remove from active processes
    const index = this.processes.indexOf(processInfo);
    if (index > -1) {
      this.processes.splice(index, 1);
      this.stats.activeProcesses = this.processes.length;
    }

    // TODO: Implement process restart logic here if needed
  }

  /**
   * Update performance statistics
   */
  private updatePerformanceStats(): void {
    if (this.embeddingTimes.length > 0) {
      this.stats.averageTime = this.embeddingTimes.reduce((a, b) => a + b, 0) / this.embeddingTimes.length;

      // Calculate throughput (embeddings per second)
      const recentTimes = this.embeddingTimes.slice(-100); // Last 100 embeddings
      if (recentTimes.length > 10) {
        this.stats.throughput = 1000 / this.stats.averageTime;
      }
    }
  }

  /**
   * Get pool statistics
   */
  getStats(): ProcessPoolStats {
    return { ...this.stats };
  }

  /**
   * Gracefully terminate the process pool
   */
  async terminate(): Promise<void> {
    if (this.isShuttingDown) {
      return;
    }

    this.isShuttingDown = true;
    log.engine(LogLevel.INFO, `🛑 Terminating embedding process pool...`);

    // Reject all pending tasks
    for (const task of this.activeTasks.values()) {
      task.reject(new Error('Process pool is shutting down'));
    }
    for (const task of this.taskQueue) {
      task.reject(new Error('Process pool is shutting down'));
    }

    this.activeTasks.clear();
    this.taskQueue.length = 0;

    // Terminate all processes
    const terminationPromises = this.processes.map(async (processInfo) => {
      try {
        // Send shutdown message
        processInfo.process.send({
          id: `shutdown-${processInfo.id}`,
          type: 'shutdown',
          payload: {}
        } as ProcessMessage);

        // Wait a bit for graceful shutdown
        await new Promise(resolve => setTimeout(resolve, 1000));

        // Force kill if still running
        processInfo.process.kill();
      } catch (error) {
        log.engine(LogLevel.WARN, `⚠️ Error terminating process ${processInfo.id}:`, error);
      }
    });

    await Promise.all(terminationPromises);

    this.processes.length = 0;
    this.stats.activeProcesses = 0;
    this.isInitialized = false;

    log.engine(LogLevel.INFO, `✅ Process pool terminated`);
  }
}