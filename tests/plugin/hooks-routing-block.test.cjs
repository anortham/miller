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

const ROUTING_CANARIES = [
  { tool: 'search', anchor: 'source-body text' },
  { tool: 'inspect', anchor: 'you can already NAME' },
  { tool: 'context', anchor: 'FIRST call in an unfamiliar area' },
  { tool: 'trace', anchor: 'shortest dependency paths' },
  { tool: 'impact', anchor: 'impacted symbols plus likely tests' },
  { tool: 'edit', anchor: 'diff preview and match proof' },
  { tool: 'patterns', anchor: 'code-shape facts' },
  { tool: 'content', anchor: 'without full-file reads' },
  { tool: 'workspace', anchor: 'index lifecycle' },
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

test('routing block routes every tool the way the instruction core does', () => {
  const block = readNormalized(ROUTING_BLOCK_PATH);
  const core = readNormalized(INSTRUCTION_CORE_PATH);

  for (const { tool, anchor } of ROUTING_CANARIES) {
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
      core.includes(anchor),
      `${INSTRUCTION_CORE_PATH} no longer says "${anchor}" for ${tool}; re-derive the ${tool} canary anchor from its current routing line`,
    );
    assert.ok(
      block.includes(anchor),
      `${ROUTING_BLOCK_PATH} should keep ${tool}'s routing anchor "${anchor}" so it never routes differently from the core`,
    );
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
