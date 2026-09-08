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

function runHook(args, env = {}, input = undefined) {
  const result = spawnSync(process.execPath, [HOOK_SCRIPT_PATH, ...args], {
    encoding: 'utf8',
    timeout: HOOK_TIMEOUT_MS,
    cwd: os.tmpdir(),
    env: { ...process.env, ...env },
    input,
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

const CURSOR_HOOKS_MANIFEST_PATH = path.join(repoRoot, 'hooks', 'cursor-hooks.json');

test('cursor-session-start emits the routing block and exits 0', () => {
  const result = runHook(['cursor-session-start']);

  assert.equal(result.status, 0);
  assert.ok(
    result.stdout.includes(ROUTING_BLOCK_FRAGMENT),
    `hook stdout should carry the routing block; got: ${result.stdout.slice(0, 200)}`,
  );
});

test('cursor-session-start output conforms to the additional_context shape', () => {
  const result = runHook(['cursor-session-start']);
  const payload = JSON.parse(result.stdout);
  const block = fs.readFileSync(ROUTING_BLOCK_PATH, 'utf8').replaceAll('\r\n', '\n').trim();

  assert.equal(payload.hookSpecificOutput, undefined);
  assert.equal(payload.additional_context, block);
});

test('cursor-session-start includes worktree cleanup guidance', () => {
  const result = runHook(['cursor-session-start']);

  assert.ok(
    result.stdout.includes(WORKTREE_CLEANUP_GUIDANCE),
    'cursor-session-start hook output should include the worktree cleanup rule',
  );
});

test('cursor-session-start honours MILLER_SESSION_HOOKS=0', () => {
  const result = runHook(['cursor-session-start'], { MILLER_SESSION_HOOKS: '0' });

  assert.equal(result.status, 0);
  assert.equal(result.stdout, '');
});

test('cursor-session-start honours MILLER_SESSION_HOOKS=false', () => {
  const result = runHook(['cursor-session-start'], { MILLER_SESSION_HOOKS: 'false' });

  assert.equal(result.status, 0);
  assert.equal(result.stdout, '');
});

test('cursor-session-start exits 0 without emitting context when the routing block is missing', () => {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'miller-hook-'));
  const copiedScript = path.join(tempDir, 'miller-session-hook.cjs');
  fs.copyFileSync(HOOK_SCRIPT_PATH, copiedScript);

  try {
    const result = spawnSync(process.execPath, [copiedScript, 'cursor-session-start'], {
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

test('Cursor hooks manifest registers sessionStart against miller-session-hook.cjs with cursor-session-start', () => {
  const manifest = JSON.parse(fs.readFileSync(CURSOR_HOOKS_MANIFEST_PATH, 'utf8'));

  assert.equal(manifest.version, 1);
  assert.ok(manifest.hooks && manifest.hooks.sessionStart);

  for (const entry of manifest.hooks.sessionStart) {
    const scriptMatch = entry.command.match(/\$\{CURSOR_PLUGIN_ROOT\}\/([^"']+\.cjs)/);
    assert.ok(scriptMatch, `command should invoke a plugin-root script: ${entry.command}`);
    assert.ok(
      fs.existsSync(path.join(repoRoot, scriptMatch[1])),
      `command references a missing script: ${scriptMatch[1]}`,
    );
    assert.equal(
      entry.command.trim().split(/\s+/).pop(),
      'cursor-session-start',
      'sessionStart command should pass cursor-session-start',
    );
  }
});

test('session-start appends candidate workspace root from stdin cwd', () => {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'miller-candidate-'));
  try {
    const result = runHook(['session-start'], {}, JSON.stringify({ cwd: tempDir }));
    assert.equal(result.status, 0);
    const payload = JSON.parse(result.stdout);
    const context = payload.hookSpecificOutput.additionalContext;

    assert.ok(context.includes('## Host Session Context'));
    assert.ok(context.includes(`- Candidate workspace root: ${JSON.stringify(tempDir)}`));
    assert.ok(context.includes(`workspace operation=open path=${JSON.stringify(tempDir)}`));
    assert.ok(context.includes('workspace operation=list'));
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});

test('cursor-session-start appends candidate workspace root from stdin workspace_roots', () => {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'miller-candidate-cursor-'));
  try {
    const result = runHook(['cursor-session-start'], {}, JSON.stringify({ workspace_roots: [tempDir] }));
    assert.equal(result.status, 0);
    const payload = JSON.parse(result.stdout);
    const context = payload.additional_context;

    assert.ok(context.includes('## Host Session Context'));
    assert.ok(context.includes(`- Candidate workspace root: ${JSON.stringify(tempDir)}`));
    assert.ok(context.includes(`workspace operation=open path=${JSON.stringify(tempDir)}`));
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});

test('subagent-start isolates subagents and omits candidate workspace root', () => {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'miller-candidate-sub-'));
  try {
    const result = runHook(['subagent-start'], {}, JSON.stringify({ cwd: tempDir }));
    assert.equal(result.status, 0);
    const payload = JSON.parse(result.stdout);
    const context = payload.hookSpecificOutput.additionalContext;

    assert.ok(!context.includes('## Host Session Context'));
    assert.ok(!context.includes(tempDir));
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
});

test('session hook ignores ambiguous multiple workspace_roots', () => {
  const tempDir1 = fs.mkdtempSync(path.join(os.tmpdir(), 'miller-multi-1-'));
  const tempDir2 = fs.mkdtempSync(path.join(os.tmpdir(), 'miller-multi-2-'));
  try {
    const result = runHook(['session-start'], {}, JSON.stringify({ workspace_roots: [tempDir1, tempDir2] }));
    assert.equal(result.status, 0);
    const payload = JSON.parse(result.stdout);
    const context = payload.hookSpecificOutput.additionalContext;

    assert.ok(!context.includes('## Host Session Context'));
  } finally {
    fs.rmSync(tempDir1, { recursive: true, force: true });
    fs.rmSync(tempDir2, { recursive: true, force: true });
  }
});

test('session hook rejects sensitive root /', () => {
  const result = runHook(['session-start'], {}, JSON.stringify({ cwd: '/' }));
  assert.equal(result.status, 0);
  const payload = JSON.parse(result.stdout);
  const context = payload.hookSpecificOutput.additionalContext;

  assert.ok(!context.includes('## Host Session Context'));
});

test('session hook rejects user home directory', () => {
  const result = runHook(['session-start'], {}, JSON.stringify({ cwd: os.homedir() }));
  assert.equal(result.status, 0);
  const payload = JSON.parse(result.stdout);
  const context = payload.hookSpecificOutput.additionalContext;

  assert.ok(!context.includes('## Host Session Context'));
});

test('session hook rejects non-existent directory', () => {
  const nonExistent = path.join(os.tmpdir(), 'definitely-does-not-exist-1234567890');
  const result = runHook(['session-start'], {}, JSON.stringify({ cwd: nonExistent }));
  assert.equal(result.status, 0);
  const payload = JSON.parse(result.stdout);
  const context = payload.hookSpecificOutput.additionalContext;

  assert.ok(!context.includes('## Host Session Context'));
});

test('session hook rejects relative path', () => {
  const result = runHook(['session-start'], {}, JSON.stringify({ cwd: 'relative/path/only' }));
  assert.equal(result.status, 0);
  const payload = JSON.parse(result.stdout);
  const context = payload.hookSpecificOutput.additionalContext;

  assert.ok(!context.includes('## Host Session Context'));
});

test('session hook handles malformed JSON without crashing', () => {
  const result = runHook(['session-start'], {}, '{ broken json');
  assert.equal(result.status, 0);
  const payload = JSON.parse(result.stdout);
  const context = payload.hookSpecificOutput.additionalContext;

  assert.ok(!context.includes('## Host Session Context'));
});

test('session hook handles large stdin safely', () => {
  const bigInput = 'a'.repeat(70000);
  const result = runHook(['session-start'], {}, bigInput);
  assert.equal(result.status, 0);
  const payload = JSON.parse(result.stdout);
  const context = payload.hookSpecificOutput.additionalContext;

  assert.ok(context.includes(ROUTING_BLOCK_FRAGMENT));
  assert.ok(!context.includes('## Host Session Context'));
});
