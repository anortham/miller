---
description: Generate a standup report of recent activity
---

# Standup

Generate a standup report showing recent activity across all registered projects.

Use the codesearch memory tool with the standup operation:

Call the memory tool with:
- operation: "standup"
- days: Number of days to include (default: 1)
- limit: Maximum entries (default: 50)

Examples:

**Today's standup:**
```
memory(operation="standup", days=1)
```

**Weekly summary:**
```
memory(operation="standup", days=7, limit=100)
```

The report groups activity by project, showing:
- Project name and path
- Timestamps and memory types
- First line of each memory as summary
