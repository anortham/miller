---
description: Save a checkpoint of current work progress
---

# Remember

Save a checkpoint capturing what you've accomplished and current state.

Use the codesearch memory tool to save a checkpoint:

1. Summarize what was just accomplished
2. Note any decisions made and why
3. List any pending work or blockers

Call the memory tool with:
- operation: "remember"
- type: "checkpoint"
- content: Your summary of current state
- tags: Relevant tags (feature name, task type, etc.)

Example:
```
memory(operation="remember", type="checkpoint", content="Implemented user authentication with JWT tokens. Added login/logout endpoints. Tests passing. Next: add password reset flow.", tags="auth,api")
```
