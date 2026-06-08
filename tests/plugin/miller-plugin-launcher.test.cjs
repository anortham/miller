'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const repoRoot = path.resolve(__dirname, '..', '..');
const launcher = require(path.join(repoRoot, 'bin', 'miller-plugin-launcher.cjs'));

test('detectPlatform maps supported Node platforms to release targets', () => {
  assert.deepEqual(launcher.detectPlatform('darwin', 'arm64'), {
    target: 'aarch64-apple-darwin',
    archiveExtension: '.tar.gz',
    binaryName: 'miller',
  });
  assert.deepEqual(launcher.detectPlatform('darwin', 'x64'), {
    target: 'x86_64-apple-darwin',
    archiveExtension: '.tar.gz',
    binaryName: 'miller',
  });
  assert.deepEqual(launcher.detectPlatform('linux', 'x64'), {
    target: 'x86_64-unknown-linux-gnu',
    archiveExtension: '.tar.gz',
    binaryName: 'miller',
  });
  assert.deepEqual(launcher.detectPlatform('win32', 'x64'), {
    target: 'x86_64-pc-windows-msvc',
    archiveExtension: '.zip',
    binaryName: 'miller.exe',
  });
});

test('detectPlatform rejects unsupported platforms with a clear error', () => {
  assert.throws(
    () => launcher.detectPlatform('linux', 'arm64'),
    /Unsupported Miller plugin platform: linux arm64/,
  );
});

test('release archive names match the GitHub release workflow convention', () => {
  assert.equal(
    launcher.releaseArchiveName('0.1.0-beta.1', 'aarch64-apple-darwin', '.tar.gz'),
    'miller-0.1.0-beta.1-aarch64-apple-darwin.tar.gz',
  );
  assert.equal(
    launcher.releaseArchiveName('0.1.0-beta.1', 'x86_64-pc-windows-msvc', '.zip'),
    'miller-0.1.0-beta.1-x86_64-pc-windows-msvc.zip',
  );
});

test('buildReleaseUrl points at the matching GitHub release asset', () => {
  assert.equal(
    launcher.buildReleaseUrl(
      'anortham/miller',
      '0.1.0-beta.1',
      'miller-0.1.0-beta.1-aarch64-apple-darwin.tar.gz',
    ),
    'https://github.com/anortham/miller/releases/download/v0.1.0-beta.1/miller-0.1.0-beta.1-aarch64-apple-darwin.tar.gz',
  );
});

test('parseSha256Sidecar extracts the lowercase checksum from standard sidecars', () => {
  const checksum = 'A'.repeat(64);

  assert.equal(
    launcher.parseSha256Sidecar(`${checksum}  miller-0.1.0-beta.1-aarch64-apple-darwin.tar.gz\n`),
    checksum.toLowerCase(),
  );
});

test('parseSha256Sidecar rejects malformed sidecars', () => {
  assert.throws(
    () => launcher.parseSha256Sidecar('not a sha256 sidecar\n'),
    /Invalid Miller SHA-256 sidecar/,
  );
});

test('resolveLaunchCwd prefers explicit Miller workspace env over process cwd', () => {
  const cwd = fs.mkdtempSync(path.join(os.tmpdir(), 'miller-launcher-cwd-'));
  const workspace = fs.mkdtempSync(path.join(os.tmpdir(), 'miller-launcher-workspace-'));

  assert.equal(
    launcher.resolveLaunchCwd({ MILLER_WORKSPACE_ROOT: workspace }, cwd),
    path.resolve(workspace),
  );
});

test('resolveLaunchCwd falls back to cwd when Cursor leaves workspace placeholder unresolved', () => {
  const cwd = fs.mkdtempSync(path.join(os.tmpdir(), 'miller-launcher-cwd-'));

  assert.equal(
    launcher.resolveLaunchCwd({ MILLER_WORKSPACE_ROOT: '${workspaceFolder}' }, cwd),
    path.resolve(cwd),
  );
});

test('resolveLaunchCwd accepts Claude and Cursor workspace env fallbacks', () => {
  const cwd = fs.mkdtempSync(path.join(os.tmpdir(), 'miller-launcher-cwd-'));
  const claudeWorkspace = fs.mkdtempSync(path.join(os.tmpdir(), 'miller-launcher-claude-'));
  const cursorWorkspace = fs.mkdtempSync(path.join(os.tmpdir(), 'miller-launcher-cursor-'));

  assert.equal(
    launcher.resolveLaunchCwd({ CLAUDE_PROJECT_DIR: claudeWorkspace }, cwd),
    path.resolve(claudeWorkspace),
  );
  assert.equal(
    launcher.resolveLaunchCwd({ WORKSPACE_FOLDER_PATHS: JSON.stringify([cursorWorkspace]) }, cwd),
    path.resolve(cursorWorkspace),
  );
});
