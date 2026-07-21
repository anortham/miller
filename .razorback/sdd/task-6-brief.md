### Task 6: Real-artifact cost (parallel with Task 4)

**Files:**
- Modify: findings doc cost-table section only.

**Interfaces:**
- Consumes: Task 2 frozen-miller worktree (its own `.miller/`, own leader — no contention with the live session).
- Produces: cost table: end-to-end clean initial vector build through BOTH cursors (symbol + chunk), download size, cold session load, warm embed latency, peak RSS, `vectors.db` size; ≥2 runs, median/range; qwen3 + bge-small.

**Contract inputs:** `MILLER_SEMANTIC=shadow MILLER_SEMANTIC_MODEL=<id>` on a serve process rooted at the frozen worktree; converge throughput lines (commit 59c2c79) + wall-clock around the whole build; `/usr/bin/time -l` for peak RSS. Between runs delete `<frozen>/.miller/vectors.db*` and retained generations. Models already cached — record download sizes from `~/.cache/julie-semantic` file sizes, do not re-download.

**File ownership:** Owns frozen-worktree `.miller/` state + findings cost-table section (distinct section from Task 5)

**Serialization required:** No

**Dependency reason:** None - safe parallel batch (Batch B, alongside Task 4; distinct findings sections prevent write conflicts — lead merges).

**What to build:** The adopter-cost evidence for the pin decision.

**Acceptance criteria:**
- [ ] Cost table with both cursors end-to-end, ≥2 runs each model, median/range, peak RSS, artifact sizes.
- [ ] Bench-lane wall-clock for research arms labeled harness-not-engine.

