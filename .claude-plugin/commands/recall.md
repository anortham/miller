---
description: Search and retrieve saved memories
---

# Recall

Search through saved memories to restore context or find past decisions.

Use the codesearch memory tool to search:

1. Search by tags to find related work
2. Search by time to see recent activity
3. Use type filter for specific memory types (checkpoint, decision, plan, learning)

Call the memory tool with:
- operation: "recall"
- days: Time range to search (default: 7)
- tags: Filter by tags (optional)
- type: Filter by type (optional)
- workspace: "current" or "all" for cross-project

Examples:

**Recent checkpoints:**
```
memory(operation="recall", days=3)
```

**Find authentication work:**
```
memory(operation="recall", tags="auth", days=30)
```

**All decisions across projects:**
```
memory(operation="recall", type="decision", workspace="all", days=14)
```
