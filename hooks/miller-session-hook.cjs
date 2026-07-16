#!/usr/bin/env node
'use strict';

const fs = require('node:fs');
const path = require('node:path');

const ROUTING_BLOCK_FILE = 'miller-routing-block.md';

const HOOK_EVENT_NAMES = new Map([
  ['session-start', 'SessionStart'],
  ['subagent-start', 'SubagentStart'],
]);

function sessionHooksDisabled() {
  const flag = (process.env.MILLER_SESSION_HOOKS ?? '').trim().toLowerCase();
  return flag === '0' || flag === 'false';
}

function emitRoutingBlock(hookEventName) {
  const blockPath = path.join(__dirname, ROUTING_BLOCK_FILE);
  const additionalContext = fs.readFileSync(blockPath, 'utf8').replaceAll('\r\n', '\n').trim();
  if (additionalContext.length === 0) return;

  process.stdout.write(JSON.stringify({
    hookSpecificOutput: { hookEventName, additionalContext },
  }));
}

function main() {
  if (sessionHooksDisabled()) return;

  const hookEventName = HOOK_EVENT_NAMES.get(process.argv[2]);
  if (hookEventName === undefined) return;

  emitRoutingBlock(hookEventName);
}

try {
  main();
} catch {
  // Fail open: guidance is an optimization, so a hook fault must never disturb the host session.
}
