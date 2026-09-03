'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const repoRoot = path.resolve(__dirname, '..', '..');

const ROUTING_BLOCK_PATH = 'hooks/miller-routing-block.md';
const INSTRUCTION_CORE_PATH = 'src/Miller.Server/MILLER_AGENT_INSTRUCTIONS.md';
const ROUTING_BLOCK_MAX_CHARS = 3000;
const BARE_NOT_SHOUTING = /\b(do NOT|Never use|MANDATORY|BLOCKED)\b/;

// The instruction core is budget-trimmed to fragments (ADR-0001; ≤1,900 chars), so each file keeps
// its own per-tool anchor: the block's stays the rich phrase, the core's is its fragment.
const ROUTING_CANARIES = [
  { tool: 'search', blockAnchor: 'auto may use semantics', coreAnchor: 'auto may use semantics' },
  { tool: 'inspect', blockAnchor: 'you can already NAME', coreAnchor: 'named file/symbol' },
  { tool: 'context', blockAnchor: 'FIRST call in an unfamiliar area', coreAnchor: 'unfamiliar areas' },
  { tool: 'trace', blockAnchor: 'exact refs', coreAnchor: 'refs, dependency paths, bridges' },
  { tool: 'impact', blockAnchor: 'impacted symbols plus likely tests', coreAnchor: 'affected symbols/tests' },
  { tool: 'edit', blockAnchor: 'diff preview and match proof', coreAnchor: 'indexed rewrite + preview' },
  { tool: 'patterns', blockAnchor: 'code-shape facts', coreAnchor: 'extracted routes/config/docs' },
  { tool: 'content', blockAnchor: 'large text', coreAnchor: 'external text' },
  { tool: 'tests', blockAnchor: 'their last verdict', coreAnchor: 'continuous verdicts' },
  { tool: 'workspace', blockAnchor: 'semantic-broker health', coreAnchor: 'semantic-broker health' },
];

function readNormalized(relativePath) {
  return fs.readFileSync(path.join(repoRoot, relativePath), 'utf8').replaceAll('\r\n', '\n');
}

test('routing block fits the hook context budget', () => {
  const block = readNormalized(ROUTING_BLOCK_PATH);

  assert.ok(block.length > 0, `${ROUTING_BLOCK_PATH} should not be empty`);
  assert.ok(
    block.length <= ROUTING_BLOCK_MAX_CHARS,
    `${ROUTING_BLOCK_PATH} is ${block.length} chars; the hook context budget is ${ROUTING_BLOCK_MAX_CHARS}`,
  );
});

test('routing block states the compact output default', () => {
  const block = readNormalized(ROUTING_BLOCK_PATH);

  assert.ok(
    block.includes('Use compact output by default. Request format=json only when you need machine-readable fields or chaining; extract only the fields you need.'),
    `${ROUTING_BLOCK_PATH} should state the compact output default`,
  );
});

test('routing block routes every tool the way the instruction core does', () => {
  const block = readNormalized(ROUTING_BLOCK_PATH);
  const core = readNormalized(INSTRUCTION_CORE_PATH);

  for (const { tool, blockAnchor, coreAnchor } of ROUTING_CANARIES) {
    const routingLine = `- ${tool} — `;

    assert.ok(
      core.includes(routingLine),
      `${INSTRUCTION_CORE_PATH} should carry a "${routingLine}" routing line`,
    );
    assert.ok(
      block.includes(routingLine),
      `${ROUTING_BLOCK_PATH} should carry a "${routingLine}" routing line`,
    );
    assert.ok(
      core.includes(coreAnchor),
      `${INSTRUCTION_CORE_PATH} no longer says "${coreAnchor}" for ${tool}; re-derive the ${tool} canary anchor from its current routing line`,
    );
    assert.ok(
      block.includes(blockAnchor),
      `${ROUTING_BLOCK_PATH} no longer says "${blockAnchor}" for ${tool}; re-derive the ${tool} canary anchor from its current routing line`,
    );
  }
});

test('routing block preserves exact-reference workflow guidance', () => {
  const block = readNormalized(ROUTING_BLOCK_PATH);

  // The budget-trimmed instruction core no longer carries workflow phrasing; the hook block is the
  // channel that must keep teaching the exact-reference workflow.
  for (const anchor of ['trace refs|path|bridge', 'use `inspect` for callers/callees', 'exact refs']) {
    assert.ok(block.includes(anchor), `${ROUTING_BLOCK_PATH} should say "${anchor}"`);
  }
});

test('routing block redirects affirmatively instead of shouting bare prohibitions', () => {
  const block = readNormalized(ROUTING_BLOCK_PATH);
  const match = block.match(BARE_NOT_SHOUTING);

  assert.equal(
    match,
    null,
    `${ROUTING_BLOCK_PATH} should name the better tool instead of shouting "${match?.[0]}"`,
  );
});
