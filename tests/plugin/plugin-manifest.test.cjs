'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const repoRoot = path.resolve(__dirname, '..', '..');

function readJson(relativePath) {
  return JSON.parse(fs.readFileSync(path.join(repoRoot, relativePath), 'utf8'));
}

function listSkillFiles(root) {
  const absoluteRoot = path.join(repoRoot, root);
  const files = [];

  function visit(dir) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        visit(full);
      } else if (entry.isFile()) {
        files.push(path.relative(absoluteRoot, full).replaceAll(path.sep, '/'));
      }
    }
  }

  visit(absoluteRoot);
  files.sort();
  return files;
}

test('Claude and Codex plugin manifests point at the release launcher', () => {
  const config = readJson('miller-plugin.json');
  const claude = readJson('.claude-plugin/plugin.json');
  const codex = readJson('.codex-plugin/plugin.json');
  const codexMcp = readJson('.mcp.json');

  assert.equal(claude.name, 'miller');
  assert.equal(codex.name, 'miller');
  assert.equal(claude.version, config.version);
  assert.equal(codex.version, config.version);
  assert.equal(claude.skills, './skills/');
  assert.equal(codex.skills, './skills/');
  assert.equal(claude.mcpServers.miller.command, 'node');
  assert.deepEqual(claude.mcpServers.miller.args, [
    '${CLAUDE_PLUGIN_ROOT}/bin/miller-plugin-launcher.cjs',
  ]);
  assert.equal(codex.mcpServers, './.mcp.json');
  assert.ok(codex.interface.longDescription.length > codex.interface.shortDescription.length);
  assert.deepEqual(codex.interface.capabilities, [
    'MCP',
    'Code search',
    'Impact analysis',
  ]);
  assert.equal(codexMcp.mcpServers.miller.command, 'node');
  assert.deepEqual(codexMcp.mcpServers.miller.args, ['./bin/miller-plugin-launcher.cjs']);
  assert.equal(codexMcp.mcpServers.miller.cwd, '.');
});

test('repo marketplace metadata exposes the local plugin without auto-installing it', () => {
  const marketplace = readJson('.agents/plugins/marketplace.json');
  const plugin = marketplace.plugins.find((entry) => entry.name === 'miller');

  assert.equal(marketplace.name, 'miller');
  assert.ok(plugin, 'miller marketplace entry should exist');
  assert.deepEqual(plugin.source, { source: 'url', url: './' });
  assert.equal(plugin.policy.installation, 'AVAILABLE');
  assert.equal(plugin.policy.authentication, 'ON_INSTALL');
  assert.equal(plugin.category, 'Developer Tools');
});

test('plugin skills are a byte-for-byte mirror of the repo agent skills', () => {
  const agentFiles = listSkillFiles('.agents/skills');
  const pluginFiles = listSkillFiles('skills');

  assert.deepEqual(pluginFiles, agentFiles);
  for (const file of agentFiles) {
    const agentContent = fs.readFileSync(path.join(repoRoot, '.agents/skills', file), 'utf8');
    const pluginContent = fs.readFileSync(path.join(repoRoot, 'skills', file), 'utf8');
    assert.equal(pluginContent, agentContent, `${file} should be synced`);
  }
});

test('web research skill fetches through browser39 and imports through Miller content', () => {
  const skill = fs.readFileSync(
    path.join(repoRoot, '.agents/skills/miller-web-research/SKILL.md'),
    'utf8',
  );

  assert.match(skill, /command -v browser39/);
  assert.match(skill, /cargo install browser39/);
  assert.match(skill, /browser39 batch/);
  assert.match(skill, /miller content add-markdown/);
  assert.match(skill, /--kind web/);
  assert.doesNotMatch(skill, /docs\/web/);
});

test('text audit skill uses cross-workspace content search and bounded reads', () => {
  const skill = fs.readFileSync(
    path.join(repoRoot, '.agents/skills/miller-text-audit/SKILL.md'),
    'utf8',
  );

  assert.match(skill, /content search/);
  assert.match(skill, /--workspace-id all/);
  assert.match(skill, /--kind source/);
  assert.match(skill, /content read/);
  assert.match(skill, /context remains opt-in/i);
});
