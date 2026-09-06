'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const test = require('node:test');
// This entry point is a POSIX shell script; .NET benchmark validation runs on every OS.
const shellTest = process.platform === 'win32' ? test.skip : test;

const script = path.resolve(__dirname, '../../scripts/bench-fact-cache-resources.sh');

function runScript(args, behavior = 'none') {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'bench-fact-cache-script-'));
  const output = path.join(root, 'existing report.json');
  const invoked = path.join(root, 'invoked');
  try {
    fs.writeFileSync(output, 'old report');
    // Isolate the shell wrapper from a nested .NET build/test run. The real benchmark
    // and its input/report contract are covered by FactCacheResourceAccountingTests.
    fs.writeFileSync(path.join(root, 'dotnet'), `#!/usr/bin/env bash
touch "$BENCH_TEST_INVOKED"
if [[ "$BENCH_TEST_BEHAVIOR" != none ]]; then
  printf '%s' '{"fresh":true}' > "$BENCH_FACT_CACHE_OUTPUT"
fi
[[ "$BENCH_TEST_BEHAVIOR" != fail ]]
`, { mode: 0o755 });
    const result = spawnSync('bash', [script, '--output', output, ...args], {
      encoding: 'utf8',
      timeout: 10000,
      cwd: root,
      env: {
        ...process.env,
        PATH: `${root}${path.delimiter}${process.env.PATH}`,
        BENCH_TEST_INVOKED: invoked,
        BENCH_TEST_BEHAVIOR: behavior,
      },
    });
    assert.ifError(result.error);
    return { ...result, output: fs.readFileSync(output, 'utf8'), invoked: fs.existsSync(invoked), files: fs.readdirSync(root) };
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
}

shellTest('unsupported fixture is rejected before invocation and preserves existing output', () => {
  const result = runScript(['--fixture', 'does-not-exist']);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /fixture.*sqlite-synthetic/i);
  assert.equal(result.invoked, false);
  assert.equal(result.output, 'old report');
});

shellTest('invalid numeric options fail explicitly before invocation', () => {
  for (const option of ['--runs', '--workspaces', '--revisions', '--budget-mb']) {
    for (const value of ['0', '-1', 'garbage', '9223372036854775808']) {
      const result = runScript([option, value]);
      assert.notEqual(result.status, 0, `${option} ${value}`);
      assert.ok(result.stderr.includes(option), result.stderr);
      assert.equal(result.invoked, false);
      assert.equal(result.output, 'old report');
    }
  }
  const overflow = runScript(['--budget-mb', '8796093022208']);
  assert.notEqual(overflow.status, 0);
  assert.equal(overflow.invoked, false);
});

shellTest('missing option value identifies the option', () => {
  const result = runScript(['--runs']);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /--runs/);
  assert.equal(result.invoked, false);
});

shellTest('directory output target is rejected before invocation', () => {
  const result = runScript(['--output', '.'], 'write');
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /output.*directory/i);
  assert.equal(result.invoked, false);
  assert.equal(result.output, 'old report');
  assert.deepEqual(result.files.sort(), ['dotnet', 'existing report.json']);
});

shellTest('successful runner without a new report cannot reuse stale output', () => {
  const result = runScript([]);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /not generated/i);
  assert.equal(result.output, 'old report');
});

shellTest('failed runner preserves the prior report even if it wrote partial output', () => {
  const result = runScript([], 'fail');
  assert.notEqual(result.status, 0);
  assert.equal(result.output, 'old report');
  assert.deepEqual(result.files.sort(), ['dotnet', 'existing report.json', 'invoked']);
});

shellTest('valid options publish the newly generated report', () => {
  const result = runScript(['--fixture', 'sqlite-synthetic', '--runs', '01', '--workspaces', '1', '--revisions', '1', '--budget-mb', '1'], 'write');
  assert.equal(result.status, 0, result.stderr);
  assert.equal(result.output, '{"fresh":true}');
  assert.deepEqual(result.files.sort(), ['dotnet', 'existing report.json', 'invoked']);
});
