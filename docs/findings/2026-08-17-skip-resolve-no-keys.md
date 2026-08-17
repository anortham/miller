# Skip store resolve when a save has no resolve keys

- **Date:** 2026-08-17
- **Binary under test:** local `main` after idle-quiet (`1.19.4+d125277f`) plus this change
- **Pinned extractor:** `julie-extract 2.33.6`

## Problem

A file save that created a Full-level manifest always started `store resolve`.
A markdown save has an empty `touched_names` journal, but julie-extract still
clears the exact fence. Miller then paid a family-store resolve (33 s to 221 s
in today's dogfood) and grew `store.db-wal` by gigabytes.

## Fix

After a Full-level update or delete, Miller still skips resolve when the
producer already reports exact at the new generation.

When the view is unbound, Miller reads the latest resolution-scope journal
batch. If that batch is usable and every `touched_names` list is empty, Miller:

1. Restores `views.resolution_state=exact` with the preserved base and delta
2. Sets `resolution_exact_at` to the new generation
3. Advances `resolution_scope_state` predecessor to that generation
4. Skips `store resolve`

A C# save still has public names in the journal, so it still resolves. One-file
scoped resolve from 2.33.5/2.33.6 is unchanged.

If the store is missing, busy, or the journal has any name, Miller fails closed
and runs resolve as before.

## Tests

Focused `StoreResolutionCarryTests` and `StoreWorkspaceCoordinatorTests`: 55
passed in 511 ms.
