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
const WORKTREE_CLEANUP_GUIDANCE = 'A deleted worktree leaves a dead registry row, however it went — `git worktree remove`, `rm -rf`, or a harness/CI teardown. Call Miller `workspace remove path=<exact old path>`; it works after the directory is gone. At session end run `workspace prune dry_run=true`, and apply it once the preview lists only roots you know are gone.';
const HOOK_TIMEOUT_MS = 10000;

const EMITTING_EVENTS = [
  { argument: 'session-start', hookEventName: 'SessionStart', matcher: 'startup|resume|clear|compact' },
  { argument: 'subagent-start', hookEventName: 'SubagentStart', matcher: undefined },
];

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

for (const { argument, hookEventName } of EMITTING_EVENTS) {
  test(`${argument} emits the routing block and exits 0`, () => {
    const result = runHook([argument]);

    assert.equal(result.status, 0);
    assert.ok(
      result.stdout.includes(ROUTING_BLOCK_FRAGMENT),
      `hook stdout should carry the routing block; got: ${result.stdout.slice(0, 200)}`,
    );
  });

  test(`${argument} output conforms to the ${hookEventName} additionalContext shape`, () => {
    const result = runHook([argument]);
    const payload = JSON.parse(result.stdout);
    const block = fs.readFileSync(ROUTING_BLOCK_PATH, 'utf8').replaceAll('\r\n', '\n').trim();

    assert.equal(payload.hookSpecificOutput.hookEventName, hookEventName);
    assert.equal(payload.hookSpecificOutput.additionalContext, block);
  });

  test(`${argument} includes worktree cleanup guidance`, () => {
    const result = runHook([argument]);

    assert.ok(
      result.stdout.includes(WORKTREE_CLEANUP_GUIDANCE),
      `${argument} hook output should include the worktree cleanup rule`,
    );
  });

  test(`${argument} honours MILLER_SESSION_HOOKS=0`, () => {
    const result = runHook([argument], { MILLER_SESSION_HOOKS: '0' });

    assert.equal(result.status, 0);
    assert.equal(result.stdout, '');
  });

  test(`${argument} honours MILLER_SESSION_HOOKS=false`, () => {
    const result = runHook([argument], { MILLER_SESSION_HOOKS: 'false' });

    assert.equal(result.status, 0);
    assert.equal(result.stdout, '');
  });

  test(`${argument} exits 0 without emitting context when the routing block is missing`, () => {
    const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'miller-hook-'));
    const copiedScript = path.join(tempDir, 'miller-session-hook.cjs');
    fs.copyFileSync(HOOK_SCRIPT_PATH, copiedScript);

    try {
      const result = spawnSync(process.execPath, [copiedScript, argument], {
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
}

test('each supported event emits a distinct hookEventName', () => {
  const emitted = EMITTING_EVENTS.map(({ argument }) =>
    JSON.parse(runHook([argument]).stdout).hookSpecificOutput.hookEventName);

  assert.deepEqual(emitted, EMITTING_EVENTS.map((event) => event.hookEventName));
  assert.equal(new Set(emitted).size, emitted.length);
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

test('hooks manifest registers each supported event against an existing script with its own explicit event argument', () => {
  const manifest = JSON.parse(fs.readFileSync(HOOKS_MANIFEST_PATH, 'utf8'));

  assert.deepEqual(Object.keys(manifest.hooks), EMITTING_EVENTS.map((event) => event.hookEventName));

  for (const { argument, hookEventName, matcher } of EMITTING_EVENTS) {
    for (const entry of manifest.hooks[hookEventName]) {
      assert.equal(entry.matcher, matcher, `${hookEventName} matcher should be ${matcher ?? 'omitted (every agent type)'}`);
      assert.ok(entry.hooks.length > 0);

      for (const handler of entry.hooks) {
        assert.equal(handler.type, 'command');

        const scriptMatch = handler.command.match(/\$\{CLAUDE_PLUGIN_ROOT\}\/([^"']+\.cjs)/);
        assert.ok(scriptMatch, `command should invoke a plugin-root script: ${handler.command}`);
        assert.ok(
          fs.existsSync(path.join(repoRoot, scriptMatch[1])),
          `command references a missing script: ${scriptMatch[1]}`,
        );

        assert.equal(
          handler.command.trim().split(/\s+/).pop(),
          argument,
          `${hookEventName} command should pass the explicit ${argument} argument`,
        );
      }
    }
  }
});
