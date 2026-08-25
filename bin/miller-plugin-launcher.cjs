#!/usr/bin/env node
'use strict';

const childProcess = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const http = require('node:http');
const https = require('node:https');
const os = require('node:os');
const path = require('node:path');
const { pipeline } = require('node:stream/promises');
const { fileURLToPath } = require('node:url');

const CONNECT_TIMEOUT_MS = 30000;
// Shorter than the client's MCP start budget (MCP_TIMEOUT, default 30000ms) ON PURPOSE: a stalled body must
// be reported and retried while the client is still waiting, not after it has already given up.
const IDLE_TIMEOUT_MS = 15000;
const DOWNLOAD_ATTEMPTS = 3;
const LEFTOVER_AGE_MS = 10 * 60 * 1000;
// Besides the version being installed, how many other cached versions survive a prune. Each costs ~430MB
// (the retained archive plus the extracted package), and nothing else ever reclaimed them.
const RETAINED_CACHED_VERSIONS = 1;
const LAST_USED_MARKER = '.last-used';
const PROGRESS_INTERVAL_MS = 3000;
const LOG_RETENTION_DAYS = 14;
const RENAME_RETRY_MS = 5000;

function millerHome(env = process.env) {
  const configured = String(env.MILLER_HOME || '').trim();
  return configured ? path.resolve(configured) : os.homedir();
}

function dateStamp(now) {
  const iso = now.toISOString();
  return `${iso.slice(0, 4)}${iso.slice(5, 7)}${iso.slice(8, 10)}`;
}

function launcherLogPath(env = process.env, now = new Date()) {
  return path.join(millerHome(env), '.miller', 'logs', `launcher-${dateStamp(now)}.log`);
}

// The only record of the pre-server phase. It must never throw: a launcher that dies while reporting a
// problem is the exact failure this file exists to prevent.
function launcherLog(message, env = process.env, now = new Date()) {
  const line = `${now.toISOString()} pid=${process.pid} ${String(message).replace(/[\r\n]+/g, ' ')}`;
  try {
    process.stderr.write(`miller-plugin-launcher: ${line}\n`);
  } catch {}

  try {
    const target = launcherLogPath(env, now);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.appendFileSync(target, `${line}\n`);
  } catch {}
}

function sweepLauncherLogs(env = process.env, now = new Date()) {
  try {
    const logsDir = path.dirname(launcherLogPath(env, now));
    const cutoff = now.getTime() - LOG_RETENTION_DAYS * 24 * 60 * 60 * 1000;
    for (const entry of fs.readdirSync(logsDir, { withFileTypes: true })) {
      if (!entry.isFile() || !/^launcher-\d{8}\.log$/.test(entry.name)) {
        continue;
      }

      const full = path.join(logsDir, entry.name);
      if (fs.statSync(full).mtimeMs < cutoff) {
        fs.rmSync(full, { force: true });
      }
    }
  } catch {}
}

// Loopback http: is the test seam. A redirect may never downgrade a release download off https.
function httpClientFor(url) {
  const parsed = new URL(url);
  if (parsed.protocol === 'https:') {
    return https;
  }

  const loopback = parsed.hostname === '127.0.0.1' || parsed.hostname === '::1' || parsed.hostname === 'localhost';
  if (parsed.protocol === 'http:' && loopback) {
    return http;
  }

  throw new Error(`Refusing to download Miller over ${parsed.protocol}//${parsed.hostname}`);
}

// A killed attempt leaves its stage directory and partial download behind: the cleanup lives in a finally
// block and a catch handler, and neither runs when the process is terminated. Both names carry the owning pid
// and the epoch milliseconds it started, so age alone separates a dead attempt's leftovers from a live
// sibling's without probing processes.
function sweepStaleInstallLeftovers(targetRoot, now = Date.now()) {
  const removed = [];
  const sweep = (directory, pattern) => {
    let entries;
    try {
      entries = fs.readdirSync(directory, { withFileTypes: true });
    } catch {
      return;
    }

    for (const entry of entries) {
      const match = pattern.exec(entry.name);
      if (!match || now - Number(match[1]) < LEFTOVER_AGE_MS) {
        continue;
      }

      const full = path.join(directory, entry.name);
      try {
        fs.rmSync(full, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
        removed.push(entry.name);
      } catch {}
    }
  };

  sweep(targetRoot, /^stage-\d+-(\d+)$/);
  sweep(path.join(targetRoot, 'downloads'), /\.tmp-\d+-(\d+)$/);

  if (removed.length > 0) {
    launcherLog(`swept ${removed.length} leftover(s) from killed installs: ${removed.join(' ')}`);
  }
}

function markVersionUsed(targetRoot, now = Date.now()) {
  try {
    fs.writeFileSync(path.join(targetRoot, LAST_USED_MARKER), `${now}\n`);
  } catch {}
}

function lastUsedAt(versionTargetRoot) {
  for (const candidate of [path.join(versionTargetRoot, LAST_USED_MARKER), versionTargetRoot]) {
    try {
      return fs.statSync(candidate).mtimeMs;
    } catch {}
  }

  return 0;
}

// Every version installs into its own directory and nothing ever removed the old ones, so a machine that had
// followed a few releases carried gigabytes it could not use. Pruning runs only on a cache MISS — once per
// version bump — so a warm start pays nothing. A version another client still launches keeps its marker
// fresh and survives; a version whose files are locked (a live miller.exe on Windows holds its own image)
// fails to delete, and that is reported rather than retried.
function pruneOldCachedVersions(cacheRoot, currentVersion, platformInfo, keep = RETAINED_CACHED_VERSIONS) {
  let versions;
  try {
    versions = fs.readdirSync(cacheRoot, { withFileTypes: true })
      .filter((entry) => entry.isDirectory() && entry.name !== currentVersion)
      .map((entry) => ({
        name: entry.name,
        usedAt: lastUsedAt(path.join(cacheRoot, entry.name, platformInfo.target)),
      }));
  } catch {
    return [];
  }

  versions.sort((left, right) => right.usedAt - left.usedAt);
  const removed = [];
  for (const version of versions.slice(keep)) {
    const full = path.join(cacheRoot, version.name);
    try {
      fs.rmSync(full, { recursive: true, force: true, maxRetries: 5, retryDelay: 100 });
      removed.push(version.name);
    } catch (error) {
      launcherLog(`cache prune skipped ${version.name}: ${error.code || error.message}`);
    }
  }

  if (removed.length > 0) {
    launcherLog(`cache prune removed ${removed.length} old version(s): ${removed.join(' ')}`);
  }

  return removed;
}

// A download that stalls or resets wastes everything it had, because the temp name carries this process's pid
// and is never resumed. Retrying here rather than leaving it to the user matters: the client is holding a
// start budget open, and a fresh attempt on a healthy link costs seconds.
async function downloadWithRetry(url, destination, options = {}, attempts = DOWNLOAD_ATTEMPTS) {
  let lastError;
  for (let attempt = 1; attempt <= attempts; attempt++) {
    try {
      return await downloadFile(url, destination, options);
    } catch (error) {
      lastError = error;
      if (attempt < attempts) {
        launcherLog(`download attempt ${attempt}/${attempts} failed, retrying: ${error.message}`);
      }
    }
  }

  throw lastError;
}

function renameWithRetry(from, to, deadlineMs = RENAME_RETRY_MS) {
  const deadline = Date.now() + deadlineMs;
  for (;;) {
    try {
      fs.renameSync(from, to);
      return;
    } catch (error) {
      const retryable = error.code === 'EPERM' || error.code === 'EBUSY' || error.code === 'EACCES';
      if (!retryable || Date.now() >= deadline) {
        throw error;
      }

      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 100);
    }
  }
}

function normalizeVersion(version) {
  const text = String(version || '').trim();
  if (!text) {
    throw new Error('Miller plugin version is required.');
  }

  return text.startsWith('v') ? text.slice(1) : text;
}

function normalizeRepository(repository) {
  let text = String(repository || '').trim();
  text = text.replace(/^https:\/\/github\.com\//, '').replace(/\.git$/, '').replace(/\/$/, '');
  if (!/^[^/]+\/[^/]+$/.test(text)) {
    throw new Error(`Invalid Miller GitHub repository: ${repository}`);
  }

  return text;
}

function detectPlatform(platform = os.platform(), arch = os.arch()) {
  const key = `${platform}:${arch}`;
  const targets = {
    'darwin:arm64': {
      target: 'aarch64-apple-darwin',
      archiveExtension: '.tar.gz',
      binaryName: 'miller',
    },
    'darwin:x64': {
      target: 'x86_64-apple-darwin',
      archiveExtension: '.tar.gz',
      binaryName: 'miller',
    },
    'linux:x64': {
      target: 'x86_64-unknown-linux-gnu',
      archiveExtension: '.tar.gz',
      binaryName: 'miller',
    },
    'win32:x64': {
      target: 'x86_64-pc-windows-msvc',
      archiveExtension: '.zip',
      binaryName: 'miller.exe',
    },
  };

  const result = targets[key];
  if (!result) {
    throw new Error(`Unsupported Miller plugin platform: ${platform} ${arch}`);
  }

  return result;
}

function releaseArchiveName(version, target, archiveExtension) {
  return `miller-${normalizeVersion(version)}-${target}${archiveExtension}`;
}

function buildReleaseUrl(repository, version, assetName) {
  return `https://github.com/${normalizeRepository(repository)}/releases/download/v${normalizeVersion(version)}/${encodeURIComponent(assetName)}`;
}

function parseSha256Sidecar(text) {
  const match = String(text).match(/\b([a-fA-F0-9]{64})\b/);
  if (!match) {
    throw new Error('Invalid Miller SHA-256 sidecar.');
  }

  return match[1].toLowerCase();
}

function validateArchiveEntryNames(entries) {
  for (const rawEntry of entries) {
    const entry = String(rawEntry || '').trim();
    const normalized = entry.replace(/\\/g, '/').replace(/^\.\/+/, '');
    const parts = normalized.split('/').filter(Boolean);

    if (
      !normalized ||
      normalized.startsWith('/') ||
      normalized.startsWith('//') ||
      /^[A-Za-z]:\//.test(normalized) ||
      parts.includes('..')
    ) {
      throw new Error(`Miller package archive contains unsafe entry path: ${entry || '<empty>'}`);
    }
  }
}

function pluginRoot() {
  return path.resolve(__dirname, '..');
}

function readPluginConfig(env = process.env) {
  const root = env.MILLER_PLUGIN_ROOT ? path.resolve(env.MILLER_PLUGIN_ROOT) : pluginRoot();
  const configPath = path.join(root, 'miller-plugin.json');
  const config = JSON.parse(fs.readFileSync(configPath, 'utf8'));

  return {
    root,
    version: normalizeVersion(env.MILLER_PLUGIN_VERSION || config.version),
    repository: normalizeRepository(env.MILLER_PLUGIN_REPOSITORY || config.repository),
  };
}

function defaultCacheRoot(env = process.env) {
  if (env.MILLER_PLUGIN_CACHE) {
    return path.resolve(env.MILLER_PLUGIN_CACHE);
  }

  return path.join(os.homedir(), '.miller', 'plugin-cache');
}

function fileSha256(filePath) {
  return new Promise((resolve, reject) => {
    const hash = crypto.createHash('sha256');
    fs.createReadStream(filePath)
      .on('data', (chunk) => hash.update(chunk))
      .on('error', reject)
      .on('end', () => resolve(hash.digest('hex')));
  });
}

function downloadFile(url, destination, options = {}) {
  const {
    redirects = 0,
    connectTimeoutMs = CONNECT_TIMEOUT_MS,
    idleTimeoutMs = IDLE_TIMEOUT_MS,
    onProgress = null,
  } = options;

  if (redirects > 5) {
    return Promise.reject(new Error(`Too many redirects while downloading ${url}`));
  }

  fs.mkdirSync(path.dirname(destination), { recursive: true });
  const temporary = `${destination}.tmp-${process.pid}-${Date.now()}`;

  return new Promise((resolve, reject) => {
    let idleTimer = null;
    const clearIdle = () => {
      if (idleTimer) {
        clearTimeout(idleTimer);
        idleTimer = null;
      }
    };

    const request = httpClientFor(url).get(url, { timeout: connectTimeoutMs }, (response) => {
      if (response.statusCode >= 300 && response.statusCode < 400 && response.headers.location) {
        response.resume();
        const next = new URL(response.headers.location, url).toString();
        downloadFile(next, destination, { ...options, redirects: redirects + 1 }).then(resolve, reject);
        return;
      }

      if (response.statusCode !== 200) {
        response.resume();
        request.destroy();
        reject(new Error(`Failed to download ${url}: HTTP ${response.statusCode}`));
        return;
      }

      const total = Number(response.headers['content-length']) || 0;
      let received = 0;
      const armIdle = () => {
        clearIdle();
        idleTimer = setTimeout(() => {
          request.destroy(new Error(
            `Download stalled for ${idleTimeoutMs}ms after ${received} of ${total || '?'} bytes: ${url}`));
        }, idleTimeoutMs);
      };

      response.on('data', (chunk) => {
        received += chunk.length;
        armIdle();
        if (onProgress) {
          onProgress(received, total);
        }
      });
      armIdle();

      const output = fs.createWriteStream(temporary, { mode: 0o644 });
      pipeline(response, output)
        .then(() => {
          clearIdle();
          renameWithRetry(temporary, destination);
          resolve(received);
        })
        .catch((error) => {
          clearIdle();
          reject(error);
        });
    });

    request.on('timeout', () => {
      request.destroy(new Error(`No response within ${connectTimeoutMs}ms: ${url}`));
    });
    request.on('error', (error) => {
      clearIdle();
      reject(error);
    });
  }).catch((error) => {
    fs.rmSync(temporary, { force: true });
    throw error;
  });
}

async function ensureDownloadedArchive(config, platformInfo, downloadDir, options = {}) {
  const archiveName = releaseArchiveName(config.version, platformInfo.target, platformInfo.archiveExtension);
  const archivePath = path.join(downloadDir, archiveName);
  const sidecarPath = path.join(downloadDir, `${archiveName}.sha256`);
  // baseUrl is the test seam: it points the download at a loopback server instead of the GitHub release.
  const archiveUrl = options.baseUrl
    ? `${options.baseUrl}/${encodeURIComponent(archiveName)}`
    : buildReleaseUrl(config.repository, config.version, archiveName);
  const sidecarUrl = `${archiveUrl}.sha256`;

  launcherLog(`sidecar download begin ${sidecarUrl}`);
  await downloadWithRetry(sidecarUrl, sidecarPath, options);
  const expectedHash = parseSha256Sidecar(fs.readFileSync(sidecarPath, 'utf8'));
  launcherLog(`sidecar download end sha256=${expectedHash}`);

  if (fs.existsSync(archivePath)) {
    launcherLog(`archive cached bytes=${fs.statSync(archivePath).size} hash begin`);
    const cachedHash = await fileSha256(archivePath);
    if (cachedHash === expectedHash) {
      launcherLog('archive cache hit; skipping download');
      return { archivePath, archiveName };
    }

    launcherLog(`archive cache stale expected=${expectedHash} got=${cachedHash}`);
  }

  const startedAt = Date.now();
  launcherLog(`archive download begin ${archiveUrl}`);
  let announcedAt = startedAt;
  await downloadWithRetry(archiveUrl, archivePath, {
    ...options,
    onProgress: (received, total) => {
      const now = Date.now();
      if (now - announcedAt < PROGRESS_INTERVAL_MS) {
        return;
      }

      announcedAt = now;
      const megabytes = (received / 1048576).toFixed(1);
      const of = total ? ` of ${(total / 1048576).toFixed(1)}MB` : '';
      const rate = (received / 1048576 / Math.max(0.001, (now - startedAt) / 1000)).toFixed(1);
      launcherLog(`archive download progress ${megabytes}MB${of} at ${rate}MB/s`);
    },
  });
  const downloadSeconds = ((Date.now() - startedAt) / 1000).toFixed(1);
  launcherLog(`archive download end bytes=${fs.statSync(archivePath).size} seconds=${downloadSeconds}`);

  const actualHash = await fileSha256(archivePath);
  if (actualHash !== expectedHash) {
    throw new Error(`Checksum mismatch for ${archiveName}: expected ${expectedHash}, got ${actualHash}`);
  }

  return { archivePath, archiveName };
}

function spawnFailureDetail(result) {
  if (result.error) {
    return `${result.error.code || result.error.name}: ${result.error.message}`;
  }

  const output = (result.stderr || result.stdout || '').trim();
  return output || `exit ${result.status === null ? `signal ${result.signal}` : result.status}`;
}

function resolveTarBinary() {
  if (process.platform !== 'win32') {
    return 'tar';
  }

  const tarBinary = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'tar.exe');
  if (!fs.existsSync(tarBinary)) {
    throw new Error(
      `Miller needs ${tarBinary} to unpack its release archive. It ships with Windows 10 build 17063 and later. `
      + 'Update Windows, or install Miller from a release archive by hand (see docs/install.md).');
  }

  return tarBinary;
}

function extractArchive(archivePath, archiveExtension, destination) {
  const tarBinary = resolveTarBinary();
  const listArgs = archiveExtension === '.zip'
    ? ['-tf', archivePath]
    : ['-tzf', archivePath];
  launcherLog(`list begin ${archivePath}`);
  const list = childProcess.spawnSync(tarBinary, listArgs, { encoding: 'utf8' });
  if (list.status !== 0 || list.error) {
    throw new Error(`Failed to list ${archivePath}: ${spawnFailureDetail(list)}`);
  }

  const entries = list.stdout.split(/\r?\n/).filter(Boolean);
  validateArchiveEntryNames(entries);
  launcherLog(`list end entries=${entries.length}`);

  const args = archiveExtension === '.zip'
    ? ['-xf', archivePath, '-C', destination]
    : ['-xzf', archivePath, '-C', destination];
  launcherLog(`extract begin ${destination}`);
  const startedAt = Date.now();
  const result = childProcess.spawnSync(tarBinary, args, { encoding: 'utf8' });

  if (result.status !== 0 || result.error) {
    throw new Error(`Failed to extract ${archivePath}: ${spawnFailureDetail(result)}`);
  }

  launcherLog(`extract end seconds=${((Date.now() - startedAt) / 1000).toFixed(1)}`);
}

function findBinary(root, binaryName) {
  const pending = [root];
  while (pending.length > 0) {
    const current = pending.pop();
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) {
        pending.push(full);
      } else if (entry.isFile() && entry.name === binaryName) {
        return full;
      }
    }
  }

  throw new Error(`Extracted Miller package did not contain ${binaryName}.`);
}

function clearMacQuarantine(packageDir) {
  if (process.platform !== 'darwin') {
    return;
  }

  childProcess.spawnSync('xattr', ['-dr', 'com.apple.quarantine', packageDir], { stdio: 'ignore' });
}

function isUnresolvedPlaceholder(value) {
  return /\$\{[^}]+}/.test(value);
}

function normalizeComparablePath(value) {
  const text = String(value || '').trim();
  if (!text) {
    return null;
  }

  let full;
  try {
    full = path.resolve(text);
  } catch {
    return null;
  }

  const trimmed = full.replace(/[\\/]+$/, '');
  return trimmed || full;
}

function pathEquals(left, right) {
  const a = normalizeComparablePath(left);
  const b = normalizeComparablePath(right);
  if (!a || !b) {
    return false;
  }

  return process.platform === 'win32' || process.platform === 'darwin'
    ? a.toLowerCase() === b.toLowerCase()
    : a === b;
}

function pathContains(root, candidate) {
  const normalizedRoot = normalizeComparablePath(root);
  const normalizedCandidate = normalizeComparablePath(candidate);
  if (!normalizedRoot || !normalizedCandidate) {
    return false;
  }

  const rootPrefix = normalizedRoot.endsWith(path.sep)
    ? normalizedRoot
    : `${normalizedRoot}${path.sep}`;

  if (process.platform === 'win32' || process.platform === 'darwin') {
    const rootLower = normalizedRoot.toLowerCase();
    const prefixLower = rootPrefix.toLowerCase();
    const candidateLower = normalizedCandidate.toLowerCase();
    return candidateLower === rootLower || candidateLower.startsWith(prefixLower);
  }

  return normalizedCandidate === normalizedRoot || normalizedCandidate.startsWith(rootPrefix);
}

function homeDirCandidates(env = process.env) {
  return [
    env.HOME,
    env.USERPROFILE,
    os.homedir(),
  ].filter(Boolean);
}

function windowsSensitiveRoots(env = process.env) {
  const systemDrive = String(env.SystemDrive || 'C:').replace(/[\\/]$/, '');
  const driveRoot = `${systemDrive}\\`;
  const roots = [
    `${driveRoot}Users`,
    `${driveRoot}Windows`,
    `${driveRoot}Windows\\System32`,
    `${driveRoot}Program Files`,
    `${driveRoot}Program Files (x86)`,
    `${driveRoot}ProgramData`,
  ];

  for (const key of ['SystemRoot', 'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432', 'ProgramData', 'PUBLIC']) {
    if (env[key]) {
      roots.push(env[key]);
    }
  }

  return roots;
}

function sensitiveRootCandidates(env = process.env) {
  const roots = [];
  roots.push(...homeDirCandidates(env));

  if (process.platform === 'darwin') {
    roots.push('/Users', '/var/root', '/private/var/root');
  } else if (process.platform === 'linux') {
    roots.push('/home', '/root');
  } else if (process.platform === 'win32') {
    roots.push(...windowsSensitiveRoots(env));
  }

  return roots;
}

function pluginInstallRootCandidates(env = process.env) {
  const roots = [];
  for (const home of homeDirCandidates(env)) {
    // Whole plugin trees, not just cache/local subdirs: marketplace clones
    // (e.g. ~/.claude/plugins/marketplaces/miller) are full repo checkouts and
    // would otherwise pass the directory check.
    roots.push(
      path.join(home, '.claude', 'plugins'),
      path.join(home, '.codex', 'plugins'),
      path.join(home, '.cursor', 'plugins'),
      path.join(home, '.miller', 'plugin-cache'),
    );
  }

  for (const key of ['CLAUDE_PLUGIN_ROOT', 'CODEX_PLUGIN_ROOT', 'CURSOR_PLUGIN_ROOT', 'MILLER_PLUGIN_ROOT']) {
    if (env[key]) {
      roots.push(env[key]);
    }
  }

  return roots;
}

function isSensitiveLaunchCwd(candidate, env = process.env) {
  const resolved = normalizeComparablePath(candidate);
  if (!resolved) {
    return false;
  }

  const parsed = path.parse(resolved);
  if (pathEquals(resolved, parsed.root)) {
    return true;
  }

  return sensitiveRootCandidates(env).some((root) => pathEquals(resolved, root)) ||
    pluginInstallRootCandidates(env).some((root) => pathContains(root, resolved));
}

function pathCandidates(value) {
  const text = String(value || '').trim();
  if (!text || isUnresolvedPlaceholder(text)) {
    return [];
  }

  if (text.startsWith('[')) {
    try {
      const parsed = JSON.parse(text);
      if (Array.isArray(parsed)) {
        return parsed.flatMap(pathCandidates);
      }
    } catch {
      return [text];
    }
  }

  if (text.includes('\n')) {
    return text.split(/\r?\n/).flatMap(pathCandidates);
  }

  if (!text.startsWith('file:') && text.includes(path.delimiter)) {
    return text.split(path.delimiter).flatMap(pathCandidates);
  }

  return [text];
}

function normalizeLaunchCwd(candidate) {
  if (!candidate) {
    return null;
  }

  let text = String(candidate).trim();
  if (!text || isUnresolvedPlaceholder(text)) {
    return null;
  }

  if (text.startsWith('file:')) {
    try {
      text = fileURLToPath(text);
    } catch {
      return null;
    }
  }

  const resolved = path.resolve(text);
  try {
    return fs.statSync(resolved).isDirectory() ? resolved : null;
  } catch {
    return null;
  }
}

function resolveSpawnCwd(env = process.env) {
  if (!env.MILLER_WORKSPACE_ROOT) {
    return undefined;
  }

  const resolved = normalizeLaunchCwd(env.MILLER_WORKSPACE_ROOT);
  if (resolved && !isSensitiveLaunchCwd(resolved, env)) {
    return resolved;
  }

  return undefined;
}

function resolveLaunchCwd(env = process.env, currentDirectory = process.cwd(), fallbackPluginRoot = pluginRoot()) {
  const candidates = [
    env.MILLER_WORKSPACE_ROOT,
    env.CLAUDE_PROJECT_DIR,
    env.CURSOR_WORKSPACE_ROOT,
    env.WORKSPACE_FOLDER,
    env.WORKSPACE_FOLDER_PATH,
    ...(env.WORKSPACE_FOLDER_PATHS ? pathCandidates(env.WORKSPACE_FOLDER_PATHS) : []),
    currentDirectory,
    fallbackPluginRoot,
  ];

  for (const candidate of candidates) {
    const resolved = normalizeLaunchCwd(candidate);
    if (resolved && !isSensitiveLaunchCwd(resolved, env)) {
      return resolved;
    }
  }

  throw new Error(
    'Could not determine a Miller workspace root. Open a project window or set MILLER_WORKSPACE_ROOT; refusing to use a Miller plugin/cache directory as the workspace.',
  );
}

async function ensureMillerPackage(config, platformInfo, cacheRoot = defaultCacheRoot(), options = {}) {
  const targetRoot = path.join(cacheRoot, config.version, platformInfo.target);
  const packageDir = path.join(targetRoot, 'package');
  const binaryPath = path.join(packageDir, platformInfo.binaryName);
  if (fs.existsSync(binaryPath)) {
    launcherLog(`cache hit binary=${binaryPath}`);
    markVersionUsed(targetRoot);
    return binaryPath;
  }

  launcherLog(`cache miss version=${config.version} target=${platformInfo.target}; installing into ${targetRoot}`);
  launcherLog('the first launch after a version change downloads the release archive; '
    + 'raise MCP_TIMEOUT (milliseconds, default 30000) if your client gives up before it finishes');

  fs.mkdirSync(targetRoot, { recursive: true });
  sweepStaleInstallLeftovers(targetRoot);
  const downloadDir = path.join(targetRoot, 'downloads');
  const stageDir = path.join(targetRoot, `stage-${process.pid}-${Date.now()}`);
  fs.rmSync(stageDir, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
  fs.mkdirSync(stageDir, { recursive: true });

  let movedPackage = false;
  try {
    const { archivePath } = await ensureDownloadedArchive(config, platformInfo, downloadDir, options);
    extractArchive(archivePath, platformInfo.archiveExtension, stageDir);
    const stagedBinary = findBinary(stageDir, platformInfo.binaryName);
    const stagedPackageDir = path.dirname(stagedBinary);

    launcherLog(`promote begin ${packageDir}`);
    fs.rmSync(packageDir, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
    renameWithRetry(stagedPackageDir, packageDir);
    movedPackage = stagedPackageDir === stageDir;

    if (process.platform !== 'win32') {
      fs.chmodSync(path.join(packageDir, platformInfo.binaryName), 0o755);
    }
    clearMacQuarantine(packageDir);
    markVersionUsed(targetRoot);
    launcherLog(`promote end binary=${binaryPath}`);
    pruneOldCachedVersions(cacheRoot, config.version, platformInfo);

    return binaryPath;
  } finally {
    if (!movedPackage) {
      fs.rmSync(stageDir, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
    }
  }
}

function runMiller(binaryPath, argv = process.argv.slice(2)) {
  const cwd = resolveSpawnCwd();
  launcherLog(`spawn binary=${binaryPath} cwd=${cwd || process.cwd()}`);
  const child = childProcess.spawn(binaryPath, ['serve', ...argv], {
    ...(cwd ? { cwd } : {}),
    env: process.env,
    stdio: 'inherit',
  });

  for (const signal of ['SIGINT', 'SIGTERM']) {
    process.once(signal, () => child.kill(signal));
  }

  return new Promise((resolve) => {
    child.on('exit', (code, signal) => {
      launcherLog(`child exit code=${code} signal=${signal}`);
      resolve(code ?? 1);
    });
    child.on('error', (error) => {
      launcherLog(`error stage=spawn ${error.stack || error.message}`);
      resolve(1);
    });
  });
}

async function main() {
  sweepLauncherLogs();
  launcherLog(`start node=${process.version} platform=${process.platform} arch=${process.arch}`);

  if (process.env.MILLER_BINARY) {
    launcherLog(`MILLER_BINARY override ${process.env.MILLER_BINARY}`);
    return runMiller(process.env.MILLER_BINARY);
  }

  const config = readPluginConfig();
  const platformInfo = detectPlatform();
  launcherLog(`resolved version=${config.version} repository=${config.repository} target=${platformInfo.target}`);
  const binaryPath = await ensureMillerPackage(config, platformInfo);
  return runMiller(binaryPath);
}

module.exports = {
  buildReleaseUrl,
  defaultCacheRoot,
  detectPlatform,
  downloadFile,
  ensureDownloadedArchive,
  ensureMillerPackage,
  httpClientFor,
  launcherLog,
  launcherLogPath,
  markVersionUsed,
  millerHome,
  normalizeRepository,
  pruneOldCachedVersions,
  normalizeVersion,
  parseSha256Sidecar,
  releaseArchiveName,
  resolveLaunchCwd,
  resolveSpawnCwd,
  readPluginConfig,
  sweepLauncherLogs,
  sweepStaleInstallLeftovers,
  validateArchiveEntryNames,
};

if (require.main === module) {
  main()
    .then((code) => {
      process.exitCode = code;
    })
    .catch((error) => {
      launcherLog(`error stage=install ${error.stack || error.message}`);
      // An open socket keeps the loop alive, so process.exitCode alone would not end the process.
      process.exit(1);
    });
}
