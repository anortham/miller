#!/usr/bin/env node
'use strict';

const childProcess = require('node:child_process');
const crypto = require('node:crypto');
const fs = require('node:fs');
const https = require('node:https');
const os = require('node:os');
const path = require('node:path');
const { fileURLToPath } = require('node:url');

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

function downloadFile(url, destination, redirects = 0) {
  if (redirects > 5) {
    return Promise.reject(new Error(`Too many redirects while downloading ${url}`));
  }

  fs.mkdirSync(path.dirname(destination), { recursive: true });
  const temporary = `${destination}.tmp-${process.pid}-${Date.now()}`;

  return new Promise((resolve, reject) => {
    const request = https.get(url, (response) => {
      if (response.statusCode >= 300 && response.statusCode < 400 && response.headers.location) {
        response.resume();
        downloadFile(new URL(response.headers.location, url).toString(), destination, redirects + 1)
          .then(resolve, reject);
        return;
      }

      if (response.statusCode !== 200) {
        response.resume();
        reject(new Error(`Failed to download ${url}: HTTP ${response.statusCode}`));
        return;
      }

      const output = fs.createWriteStream(temporary, { mode: 0o644 });
      output.on('error', reject);
      output.on('finish', () => {
        output.close((error) => {
          if (error) {
            reject(error);
            return;
          }

          fs.renameSync(temporary, destination);
          resolve();
        });
      });
      response.pipe(output);
    });

    request.on('error', reject);
  }).catch((error) => {
    fs.rmSync(temporary, { force: true });
    throw error;
  });
}

async function ensureDownloadedArchive(config, platformInfo, downloadDir) {
  const archiveName = releaseArchiveName(config.version, platformInfo.target, platformInfo.archiveExtension);
  const archivePath = path.join(downloadDir, archiveName);
  const sidecarPath = path.join(downloadDir, `${archiveName}.sha256`);
  const archiveUrl = buildReleaseUrl(config.repository, config.version, archiveName);
  const sidecarUrl = `${archiveUrl}.sha256`;

  await downloadFile(sidecarUrl, sidecarPath);
  const expectedHash = parseSha256Sidecar(fs.readFileSync(sidecarPath, 'utf8'));

  if (fs.existsSync(archivePath)) {
    const actualHash = await fileSha256(archivePath);
    if (actualHash === expectedHash) {
      return { archivePath, archiveName };
    }
  }

  await downloadFile(archiveUrl, archivePath);
  const actualHash = await fileSha256(archivePath);
  if (actualHash !== expectedHash) {
    throw new Error(`Checksum mismatch for ${archiveName}: expected ${expectedHash}, got ${actualHash}`);
  }

  return { archivePath, archiveName };
}

function extractArchive(archivePath, archiveExtension, destination) {
  const tarBinary = process.platform === 'win32'
    ? path.join(process.env.SystemRoot || 'C:\\Windows', 'System32', 'tar.exe')
    : 'tar';
  const args = archiveExtension === '.zip'
    ? ['-xf', archivePath, '-C', destination]
    : ['-xzf', archivePath, '-C', destination];
  const result = childProcess.spawnSync(tarBinary, args, { encoding: 'utf8' });

  if (result.status !== 0) {
    const detail = result.stderr || result.stdout || `exit ${result.status}`;
    throw new Error(`Failed to extract ${archivePath}: ${detail.trim()}`);
  }
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

function resolveLaunchCwd(env = process.env, currentDirectory = process.cwd()) {
  const candidates = [
    env.MILLER_WORKSPACE_ROOT,
    env.CLAUDE_PROJECT_DIR,
    env.CURSOR_WORKSPACE_ROOT,
    env.WORKSPACE_FOLDER,
    env.WORKSPACE_FOLDER_PATH,
    ...(env.WORKSPACE_FOLDER_PATHS ? pathCandidates(env.WORKSPACE_FOLDER_PATHS) : []),
    currentDirectory,
  ];

  for (const candidate of candidates) {
    const resolved = normalizeLaunchCwd(candidate);
    if (resolved) {
      return resolved;
    }
  }

  return path.resolve(currentDirectory);
}

async function ensureMillerPackage(config, platformInfo, cacheRoot = defaultCacheRoot()) {
  const targetRoot = path.join(cacheRoot, config.version, platformInfo.target);
  const packageDir = path.join(targetRoot, 'package');
  const binaryPath = path.join(packageDir, platformInfo.binaryName);
  if (fs.existsSync(binaryPath)) {
    return binaryPath;
  }

  fs.mkdirSync(targetRoot, { recursive: true });
  const downloadDir = path.join(targetRoot, 'downloads');
  const stageDir = path.join(targetRoot, `stage-${process.pid}-${Date.now()}`);
  fs.rmSync(stageDir, { recursive: true, force: true });
  fs.mkdirSync(stageDir, { recursive: true });

  let movedPackage = false;
  try {
    const { archivePath } = await ensureDownloadedArchive(config, platformInfo, downloadDir);
    extractArchive(archivePath, platformInfo.archiveExtension, stageDir);
    const stagedBinary = findBinary(stageDir, platformInfo.binaryName);
    const stagedPackageDir = path.dirname(stagedBinary);

    fs.rmSync(packageDir, { recursive: true, force: true });
    fs.renameSync(stagedPackageDir, packageDir);
    movedPackage = stagedPackageDir === stageDir;

    if (process.platform !== 'win32') {
      fs.chmodSync(path.join(packageDir, platformInfo.binaryName), 0o755);
    }
    clearMacQuarantine(packageDir);

    return path.join(packageDir, platformInfo.binaryName);
  } finally {
    if (!movedPackage) {
      fs.rmSync(stageDir, { recursive: true, force: true });
    }
  }
}

function runMiller(binaryPath, argv = process.argv.slice(2)) {
  const child = childProcess.spawn(binaryPath, ['serve', ...argv], {
    cwd: resolveLaunchCwd(),
    env: process.env,
    stdio: 'inherit',
  });

  for (const signal of ['SIGINT', 'SIGTERM']) {
    process.once(signal, () => child.kill(signal));
  }

  return new Promise((resolve) => {
    child.on('exit', (code) => resolve(code ?? 1));
    child.on('error', (error) => {
      console.error(error.message);
      resolve(1);
    });
  });
}

async function main() {
  if (process.env.MILLER_BINARY) {
    return runMiller(process.env.MILLER_BINARY);
  }

  const config = readPluginConfig();
  const platformInfo = detectPlatform();
  const binaryPath = await ensureMillerPackage(config, platformInfo);
  return runMiller(binaryPath);
}

module.exports = {
  buildReleaseUrl,
  defaultCacheRoot,
  detectPlatform,
  ensureMillerPackage,
  normalizeRepository,
  normalizeVersion,
  parseSha256Sidecar,
  releaseArchiveName,
  resolveLaunchCwd,
  readPluginConfig,
};

if (require.main === module) {
  main()
    .then((code) => {
      process.exitCode = code;
    })
    .catch((error) => {
      console.error(error.message);
      process.exitCode = 1;
    });
}
