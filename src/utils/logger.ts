import { appendFile, mkdir } from 'fs/promises';
import { existsSync } from 'fs';
import path from 'path';
import { MillerPaths } from './miller-paths.js';

export enum LogLevel {
  DEBUG = 0,
  INFO = 1,
  WARN = 2,
  ERROR = 3
}

export interface LogEntry {
  timestamp: string;
  level: string;
  category: string;
  message: string;
  data?: any;
}

export class MillerLogger {
  private paths: MillerPaths;
  private logLevel: LogLevel;
  private writeQueue: Promise<void> = Promise.resolve();

  constructor(paths: MillerPaths, logLevel: LogLevel = LogLevel.INFO) {
    this.paths = paths;
    this.logLevel = logLevel;
  }

  private async ensureLogDir(): Promise<void> {
    const logDir = this.paths.getLogsDir();
    if (!existsSync(logDir)) {
      await mkdir(logDir, { recursive: true });
    }
  }

  private formatLogEntry(level: LogLevel, category: string, message: string, data?: any): string {
    const timestamp = new Date().toISOString();
    const levelName = LogLevel[level];

    let logLine = `[${timestamp}] [${levelName}] [${category}] ${message}`;

    if (data !== undefined) {
      if (typeof data === 'object') {
        logLine += ` ${JSON.stringify(data)}`;
      } else {
        logLine += ` ${data}`;
      }
    }

    return logLine + '\n';
  }

  private async writeToFile(filePath: string, content: string): Promise<void> {
    // Queue writes to prevent race conditions
    this.writeQueue = this.writeQueue.then(async () => {
      try {
        await this.ensureLogDir();
        await appendFile(filePath, content, 'utf8');
      } catch (error) {
        // Fallback to stderr only if file logging fails completely
        // This is risky for MCP but better than losing critical errors
        if (error instanceof Error && error.message.includes('ENOENT')) {
          process.stderr.write(`LOGGER ERROR: Failed to write to ${filePath}: ${error.message}\n`);
        }
      }
    });

    return this.writeQueue;
  }

  async debug(category: string, message: string, data?: any): Promise<void> {
    if (this.logLevel <= LogLevel.DEBUG) {
      const logEntry = this.formatLogEntry(LogLevel.DEBUG, category, message, data);
      await this.writeToFile(this.paths.getTodayLogPath('debug'), logEntry);
      await this.writeToFile(this.paths.getTodayLogPath('main'), logEntry);
    }
  }

  async info(category: string, message: string, data?: any): Promise<void> {
    if (this.logLevel <= LogLevel.INFO) {
      const logEntry = this.formatLogEntry(LogLevel.INFO, category, message, data);
      await this.writeToFile(this.paths.getTodayLogPath('main'), logEntry);
    }
  }

  async warn(category: string, message: string, data?: any): Promise<void> {
    if (this.logLevel <= LogLevel.WARN) {
      const logEntry = this.formatLogEntry(LogLevel.WARN, category, message, data);
      await this.writeToFile(this.paths.getTodayLogPath('main'), logEntry);
      await this.writeToFile(this.paths.getTodayLogPath('error'), logEntry);
    }
  }

  async error(category: string, message: string, data?: any): Promise<void> {
    if (this.logLevel <= LogLevel.ERROR) {
      const logEntry = this.formatLogEntry(LogLevel.ERROR, category, message, data);
      await this.writeToFile(this.paths.getTodayLogPath('main'), logEntry);
      await this.writeToFile(this.paths.getTodayLogPath('error'), logEntry);
    }
  }

  // Specialized logging methods for different components
  async parser(level: LogLevel, message: string, data?: any): Promise<void> {
    const logEntry = this.formatLogEntry(level, 'PARSER', message, data);
    await this.writeToFile(this.paths.getTodayLogPath('parser'), logEntry);

    // Also log to main if it's important enough
    if (level >= LogLevel.WARN) {
      await this.writeToFile(this.paths.getTodayLogPath('main'), logEntry);
    }
  }

  async database(level: LogLevel, message: string, data?: any): Promise<void> {
    if (level >= this.logLevel) {
      await this[LogLevel[level].toLowerCase() as 'debug' | 'info' | 'warn' | 'error']('DATABASE', message, data);
    }
  }

  async search(level: LogLevel, message: string, data?: any): Promise<void> {
    if (level >= this.logLevel) {
      await this[LogLevel[level].toLowerCase() as 'debug' | 'info' | 'warn' | 'error']('SEARCH', message, data);
    }
  }

  async mcp(level: LogLevel, message: string, data?: any): Promise<void> {
    if (level >= this.logLevel) {
      await this[LogLevel[level].toLowerCase() as 'debug' | 'info' | 'warn' | 'error']('MCP', message, data);
    }
  }

  async engine(level: LogLevel, message: string, data?: any): Promise<void> {
    if (level >= this.logLevel) {
      await this[LogLevel[level].toLowerCase() as 'debug' | 'info' | 'warn' | 'error']('ENGINE', message, data);
    }
  }

  async watcher(level: LogLevel, message: string, data?: any): Promise<void> {
    if (level >= this.logLevel) {
      await this[LogLevel[level].toLowerCase() as 'debug' | 'info' | 'warn' | 'error']('WATCHER', message, data);
    }
  }

  // Performance logging
  async performance(operation: string, duration: number, data?: any): Promise<void> {
    await this.info('PERF', `${operation} took ${duration}ms`, data);
  }

  // Tool execution logging
  async tool(toolName: string, success: boolean, duration?: number, error?: Error): Promise<void> {
    const status = success ? 'SUCCESS' : 'FAILED';
    const durationStr = duration ? ` (${duration}ms)` : '';

    if (success) {
      await this.info('TOOL', `${toolName} ${status}${durationStr}`);
    } else {
      await this.error('TOOL', `${toolName} ${status}${durationStr}`, error?.message);
    }
  }

  // Startup/shutdown logging
  async lifecycle(event: 'startup' | 'shutdown' | 'ready', message: string, data?: any): Promise<void> {
    await this.info('LIFECYCLE', `${event.toUpperCase()}: ${message}`, data);
  }

  // Set log level dynamically
  setLogLevel(level: LogLevel): void {
    this.logLevel = level;
  }

  // Get current log level
  getLogLevel(): LogLevel {
    return this.logLevel;
  }

  // Flush all pending writes (useful for shutdown)
  async flush(): Promise<void> {
    await this.writeQueue;
  }

  // Get log file paths for external access
  getLogPaths() {
    return {
      main: this.paths.getTodayLogPath('main'),
      error: this.paths.getTodayLogPath('error'),
      debug: this.paths.getTodayLogPath('debug'),
      parser: this.paths.getTodayLogPath('parser')
    };
  }
}

// Global logger instance (initialized by MillerServer)
let globalLogger: MillerLogger | null = null;

// Initialize global logger
export function initializeLogger(paths: MillerPaths, logLevel: LogLevel = LogLevel.INFO): MillerLogger {
  globalLogger = new MillerLogger(paths, logLevel);
  return globalLogger;
}

// Get global logger instance
export function getLogger(): MillerLogger {
  if (!globalLogger) {
    throw new Error('Logger not initialized. Call initializeLogger() first.');
  }
  return globalLogger;
}

// Convenience functions for global logger
export const log = {
  debug: (category: string, message: string, data?: any) => getLogger().debug(category, message, data),
  info: (category: string, message: string, data?: any) => getLogger().info(category, message, data),
  warn: (category: string, message: string, data?: any) => getLogger().warn(category, message, data),
  error: (category: string, message: string, data?: any) => getLogger().error(category, message, data),

  parser: (level: LogLevel, message: string, data?: any) => getLogger().parser(level, message, data),
  database: (level: LogLevel, message: string, data?: any) => getLogger().database(level, message, data),
  search: (level: LogLevel, message: string, data?: any) => getLogger().search(level, message, data),
  mcp: (level: LogLevel, message: string, data?: any) => getLogger().mcp(level, message, data),
  engine: (level: LogLevel, message: string, data?: any) => getLogger().engine(level, message, data),
  watcher: (level: LogLevel, message: string, data?: any) => getLogger().watcher(level, message, data),

  performance: (operation: string, duration: number, data?: any) => getLogger().performance(operation, duration, data),
  tool: (toolName: string, success: boolean, duration?: number, error?: Error) => getLogger().tool(toolName, success, duration, error),
  lifecycle: (event: 'startup' | 'shutdown' | 'ready', message: string, data?: any) => getLogger().lifecycle(event, message, data),

  flush: () => getLogger().flush()
};