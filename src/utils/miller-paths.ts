import { mkdir } from 'fs/promises';
import path from 'path';
import { existsSync } from 'fs';

export class MillerPaths {
  private workspaceRoot: string;
  private millerDir: string;

  constructor(workspaceRoot: string) {
    this.workspaceRoot = path.resolve(workspaceRoot);
    this.millerDir = path.join(this.workspaceRoot, '.miller');
  }

  // Main .miller directory
  getMillerDir(): string {
    return this.millerDir;
  }

  // Data storage directory
  getDataDir(): string {
    return path.join(this.millerDir, 'data');
  }

  // Logs directory
  getLogsDir(): string {
    return path.join(this.millerDir, 'logs');
  }

  // Cache directory (for temporary files, search indexes, etc.)
  getCacheDir(): string {
    return path.join(this.millerDir, 'cache');
  }

  // Config directory (for user settings, ignore patterns, etc.)
  getConfigDir(): string {
    return path.join(this.millerDir, 'config');
  }

  // Specific file paths
  getDatabasePath(): string {
    return path.join(this.getDataDir(), 'code-intel.db');
  }

  getMainLogPath(): string {
    return path.join(this.getLogsDir(), 'miller.log');
  }

  getErrorLogPath(): string {
    return path.join(this.getLogsDir(), 'errors.log');
  }

  getDebugLogPath(): string {
    return path.join(this.getLogsDir(), 'debug.log');
  }

  getParserLogPath(): string {
    return path.join(this.getLogsDir(), 'parser.log');
  }


  getSearchIndexPath(): string {
    return path.join(this.getCacheDir(), 'search-index.json');
  }

  getConfigPath(): string {
    return path.join(this.getConfigDir(), 'miller.json');
  }

  // Initialize all directories
  async ensureDirectories(): Promise<void> {
    const dirs = [
      this.getMillerDir(),
      this.getDataDir(),
      this.getLogsDir(),
      this.getCacheDir(),
      this.getConfigDir()
    ];

    for (const dir of dirs) {
      if (!existsSync(dir)) {
        await mkdir(dir, { recursive: true });
      }
    }
  }

  // Check if .miller directory exists
  exists(): boolean {
    return existsSync(this.millerDir);
  }

  // Get workspace info
  getWorkspaceRoot(): string {
    return this.workspaceRoot;
  }

  // Create a relative path from workspace root
  getRelativePath(absolutePath: string): string {
    return path.relative(this.workspaceRoot, absolutePath);
  }

  // Get absolute path from relative workspace path
  getAbsolutePath(relativePath: string): string {
    return path.resolve(this.workspaceRoot, relativePath);
  }

  // Get today's log file with rotation
  getTodayLogPath(type: string = 'main'): string {
    const today = new Date().toISOString().split('T')[0]; // YYYY-MM-DD
    const baseDir = this.getLogsDir();

    switch (type) {
      case 'error':
        return path.join(baseDir, `errors-${today}.log`);
      case 'debug':
        return path.join(baseDir, `debug-${today}.log`);
      case 'parser':
        return path.join(baseDir, `parser-${today}.log`);
      default:
        return path.join(baseDir, `miller-${today}.log`);
    }
  }

  // Create .gitignore for .miller directory
  async createGitignore(): Promise<void> {
    const gitignorePath = path.join(this.millerDir, '.gitignore');
    const content = `# Miller data directory - ignore all contents
*
!.gitignore

# This ensures the .miller directory structure is preserved
# but none of the generated data/logs/cache is committed
`;

    await Bun.write(gitignorePath, content);
  }
}