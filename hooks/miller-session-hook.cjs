#!/usr/bin/env node
'use strict';

const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const ROUTING_BLOCK_FILE = 'miller-routing-block.md';
const MAX_STDIN_BYTES = 65536;

const EVENT_CONFIG = {
  'session-start': {
    envelope: 'claude-codex',
    hookEventName: 'SessionStart',
    allowCandidate: true,
  },
  'subagent-start': {
    envelope: 'claude-codex',
    hookEventName: 'SubagentStart',
    allowCandidate: false,
  },
  'cursor-session-start': {
    envelope: 'cursor',
    hookEventName: 'sessionStart',
    allowCandidate: true,
  },
};

function sessionHooksDisabled() {
  const flag = (process.env.MILLER_SESSION_HOOKS ?? '').trim().toLowerCase();
  return flag === '0' || flag === 'false';
}

function readRoutingBlock() {
  const blockPath = path.join(__dirname, ROUTING_BLOCK_FILE);
  if (!fs.existsSync(blockPath)) return null;
  const content = fs.readFileSync(blockPath, 'utf8').replaceAll('\r\n', '\n').trim();
  return content.length > 0 ? content : null;
}

function readBoundedStdin(maxBytes = MAX_STDIN_BYTES) {
  if (process.stdin.isTTY) return '';
  try {
    const buf = Buffer.alloc(maxBytes);
    let bytesRead = 0;
    try {
      bytesRead = fs.readSync(0, buf, 0, maxBytes, null);
    } catch {
      return '';
    }
    if (bytesRead <= 0) return '';
    return buf.toString('utf8', 0, bytesRead);
  } catch {
    return '';
  }
}

function parseStdinJson(raw) {
  if (!raw || typeof raw !== 'string' || raw.trim().length === 0) return null;
  try {
    return JSON.parse(raw);
  } catch {
    return null;
  }
}

function extractCandidatePath(payload) {
  if (!payload || typeof payload !== 'object') return null;

  if (Array.isArray(payload.workspace_roots)) {
    if (payload.workspace_roots.length === 1 && typeof payload.workspace_roots[0] === 'string') {
      return payload.workspace_roots[0].trim();
    }
    return null;
  }

  if (typeof payload.cwd === 'string' && payload.cwd.trim().length > 0) {
    return payload.cwd.trim();
  }

  if (typeof payload.workspace_root === 'string' && payload.workspace_root.trim().length > 0) {
    return payload.workspace_root.trim();
  }

  if (typeof payload.project_path === 'string' && payload.project_path.trim().length > 0) {
    return payload.project_path.trim();
  }

  return null;
}

function isSensitiveRoot(candidate) {
  try {
    const resolved = path.resolve(candidate);
    const parsed = path.parse(resolved);

    // 1. Filesystem/drive root (e.g. '/' on POSIX or 'C:\' on Windows)
    if (parsed.root === resolved) return true;

    // 2. User home directory
    const home = os.homedir();
    if (home && path.resolve(home) === resolved) return true;

    // 3. Sensitive system directories
    const sensitive = [
      '/home',
      '/root',
      '/Users',
      '/var/root',
      '/private/var/root',
    ];

    if (process.platform === 'win32') {
      const systemDrive = (process.env.SystemDrive || 'C:').toUpperCase();
      const driveRoot = systemDrive.endsWith('\\') ? systemDrive : systemDrive + '\\';
      sensitive.push(
        path.join(driveRoot, 'Users'),
        path.join(driveRoot, 'Windows'),
        path.join(driveRoot, 'Windows', 'System32'),
        path.join(driveRoot, 'Program Files'),
        path.join(driveRoot, 'Program Files (x86)'),
        path.join(driveRoot, 'ProgramData'),
      );
      if (process.env.SystemRoot) sensitive.push(process.env.SystemRoot);
      if (process.env.ProgramFiles) sensitive.push(process.env.ProgramFiles);
      if (process.env['ProgramFiles(x86)']) sensitive.push(process.env['ProgramFiles(x86)']);
      if (process.env.ProgramData) sensitive.push(process.env.ProgramData);
    }

    const isWindowsOrMac = process.platform === 'win32' || process.platform === 'darwin';
    for (const s of sensitive) {
      const normS = path.resolve(s);
      if (isWindowsOrMac) {
        if (resolved.toLowerCase() === normS.toLowerCase()) return true;
      } else {
        if (resolved === normS) return true;
      }
    }

    return false;
  } catch {
    return true;
  }
}

function validateCandidateDirectory(candidate) {
  if (!candidate || typeof candidate !== 'string') return null;
  const trimmed = candidate.trim();
  if (trimmed.length === 0) return null;
  if (!path.isAbsolute(trimmed)) return null;
  if (isSensitiveRoot(trimmed)) return null;
  try {
    const stat = fs.statSync(trimmed);
    if (!stat.isDirectory()) return null;
    return trimmed;
  } catch {
    return null;
  }
}

function formatCandidateAppendix(candidate) {
  const jsonPath = JSON.stringify(path.resolve(candidate));
  return `\n\n## Host Session Context\n- Candidate workspace root: ${jsonPath}\n- If this matches your project and is not yet registered, register with: workspace operation=open path=${jsonPath}\n- If already registered, find its ID with: workspace operation=list`;
}

function main() {
  if (sessionHooksDisabled()) return;

  const eventArg = process.argv[2];
  const config = EVENT_CONFIG[eventArg];
  if (!config) return;

  const routingBlock = readRoutingBlock();
  if (!routingBlock) return;

  let contextText = routingBlock;
  if (config.allowCandidate) {
    const rawStdin = readBoundedStdin();
    const payload = parseStdinJson(rawStdin);
    const candidate = extractCandidatePath(payload);
    const validated = validateCandidateDirectory(candidate);
    if (validated) {
      contextText += formatCandidateAppendix(validated);
    }
  }

  if (config.envelope === 'cursor') {
    process.stdout.write(JSON.stringify({
      additional_context: contextText,
    }));
  } else {
    process.stdout.write(JSON.stringify({
      hookSpecificOutput: {
        hookEventName: config.hookEventName,
        additionalContext: contextText,
      },
    }));
  }
}

try {
  main();
} catch {
  // Fail open: guidance is an optimization, so a hook fault must never disturb the host session.
}
