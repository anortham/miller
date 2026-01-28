---
name: progress-tracking
description: Automatically save checkpoints at key moments during development
allowed-tools: mcp__codesearch__memory
---

# Progress Tracking Skill

Automatically save checkpoints to preserve work progress.

## When To Checkpoint

Save a checkpoint when ANY of these occur:

1. **Tests pass** after implementing a feature
2. **Significant code changes** are committed
3. **Important decision** is made (architecture, library choice, etc.)
4. **Blocker encountered** that stops progress
5. **Before context switch** (moving to different task/file)

## How To Checkpoint

Call the memory tool with appropriate type:

**For regular progress:**
```
memory(operation="remember", type="checkpoint", content="...", tags="...")
```

**For decisions:**
```
memory(operation="remember", type="decision", content="...", title="...", tags="...")
```

**For learnings:**
```
memory(operation="remember", type="learning", content="...", tags="...")
```

## Content Guidelines

Include in every checkpoint:
- **What**: What was accomplished or discovered
- **Why**: Reasoning behind decisions (if applicable)
- **Next**: What comes next or what's blocking

Example:
```
memory(
  operation="remember",
  type="checkpoint",
  content="Implemented JWT auth middleware. Chose RS256 over HS256 for better security with microservices. Tests passing (8/8). Next: add refresh token rotation.",
  tags="auth,jwt,middleware"
)
```

## Important

- Don't over-checkpoint (aim for 2-5 per significant task)
- Always include context that would help future sessions
- Tag consistently for easy recall
