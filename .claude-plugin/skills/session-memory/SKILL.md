---
name: session-memory
description: Automatically restore session context from persistent memory at session start
allowed-tools: mcp__codesearch__memory, Read
---

# Session Memory Skill

When a session starts, automatically restore context from the most recent checkpoint.

## When This Activates

This skill activates at session start (including resume, clear, compact events).

## What To Do

1. **Check for recent checkpoints** by calling the memory tool:
   ```
   memory(operation="recall", type="checkpoint", days=7, limit=3)
   ```

2. **If checkpoints found**, present a brief summary:
   - What was being worked on
   - Key decisions made
   - Any pending work

3. **If no checkpoints**, note that this appears to be a fresh session.

## Example Output

"Restoring context from last session (2 hours ago):
- Working on: User authentication feature
- Last completed: JWT token generation
- Pending: Password reset flow
- Key decision: Using refresh tokens for session extension"

## Notes

- Only restore context, don't take any actions
- Keep the summary concise (3-5 bullet points max)
- If multiple checkpoints exist, focus on the most recent
