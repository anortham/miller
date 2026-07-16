'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { spawnSync } = require('node:child_process');

const repoRoot = path.resolve(__dirname, '..', '..');

const HOOK_SCRIPT_PATH = path.join(repoRoot, 'hooks', 'miller-session-hook.cjs');
const ROUTING_BLOCK_PATH = path.join(repoRoot, 'hooks', 'miller-routing-block.md');
const HOOKS_MANIFEST_PATH = path.join(repoRoot, 'hooks', 'claude-codex-hooks.json');
const ROUTING_BLOCK_FRAGMENT = 'One Miller call beats shell greps and full-file reads';
const HOOK_TIMEOUT_MS = 10000;

function runHook(args, env = {}) {
  const result = spawnSync(process.execPath, [HOOK_SCRIPT_PATH, ...args], {
    encoding: 'utf8',
    timeout: HOOK_TIMEOUT_MS,
    cwd: os.tmpdir(),
    env: { ...process.env, ...env },
  });

  assert.equal(result.signal, null, `hook was killed by ${result.signal} (timeout ${HOOK_TIMEOUT_MS}ms)`);
  return result;
}

test('session-start emits the routing block and exits 0', () => {
  const result = runHook(['session-start']);

  assert.equal(result.status, 0);
  assert.ok(
    result.stdout.includes(ROUTING_BLOCK_FRAGMENT),
    `hook stdout should carry the routing block; got: ${result.stdout.slice(0, 200)}`,
  );
});

test('session-start output conforms to the SessionStart additionalContext shape', () => {
  const result = runHook(['session-start']);
  const payload = JSON.parse(result.stdout);
  const block = fs.readFileSync(ROUTING_BLOCK_PATH, 'utf8').replaceAll('\r\n', '\n').trim();

  assert.equal(payload.hookSpecificOutput.hookEventName, 'SessionStart');
  assert.equal(payload.hookSpecificOutput.additionalContext, block);
});

test('MILLER_SESSION_HOOKS=0 exits 0 without emitting context', () => {
  const result = runHook(['session-start'], { MILLER_SESSION_HOOKS: '0' });

  assert.equal(result.status, 0);
  assert.equal(result.stdout, '');
});

test('MILLER_SESSION_HOOKS=false exits 0 without emitting context', () => {
  const result = runHook(['session-start'], { MILLER_SESSION_HOOKS: 'false' });

  assert.equal(result.status, 0);
  assert.equal(result.stdout, '');
});

test('a missing routing block exits 0 without emitting context', () => {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'miller-hook-'));
  const copiedScript = path.join(tempDir, 'miller-session-hook.cjs');
  fs.copyFileSync(HOOK_SCRIPT_PATH, copiedScript);

  try {
    const result = spawnSync(process.execPath, [copiedScript, 'session-start'], {
      encoding: 'utf8',
      timeout: HOOK_TIMEOUT_MS,
      cwd: os.tmpdir(),
    });

    assert.equal(result.signal, null);
    assert.equal(result.status, 0);
    assert.equal(result.stdout, '');
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});

test('an unknown event argument exits 0 without emitting context', () => {
  const result = runHook(['pre-tool-use']);

  assert.equal(result.status, 0);
  assert.equal(result.stdout, '');
});

test('a missing event argument exits 0 without emitting context', () => {
  const result = runHook([]);

  assert.equal(result.status, 0);
  assert.equal(result.stdout, '');
});

test('hooks manifest registers SessionStart against an existing script with an explicit event', () => {
  const manifest = JSON.parse(fs.readFileSync(HOOKS_MANIFEST_PATH, 'utf8'));

  assert.deepEqual(Object.keys(manifest.hooks), ['SessionStart']);

  for (const entry of manifest.hooks.SessionStart) {
    assert.equal(entry.matcher, 'startup|resume|clear|compact');
    assert.ok(entry.hooks.length > 0);

    for (const handler of entry.hooks) {
      assert.equal(handler.type, 'command');

      const scriptMatch = handler.command.match(/\$\{CLAUDE_PLUGIN_ROOT\}\/([^"']+\.cjs)/);
      assert.ok(scriptMatch, `command should invoke a plugin-root script: ${handler.command}`);
      assert.ok(
        fs.existsSync(path.join(repoRoot, scriptMatch[1])),
        `command references a missing script: ${scriptMatch[1]}`,
      );

      const finalToken = handler.command.trim().split(/\s+/).pop();
      assert.match(
        finalToken,
        /^[a-z][a-z-]*$/,
        `command should end with an explicit event argument: ${handler.command}`,
      );
    }
  }
});
