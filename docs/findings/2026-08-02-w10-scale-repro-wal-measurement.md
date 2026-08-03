# 2026-08-02 — W10: 74k-file scale repro, WAL and memory measurement

**Status:** measured. This is the P2 gate from
[`docs/plans/2026-08-01-multi-worktree-fleet-safety-plan.md`](../plans/2026-08-01-multi-worktree-fleet-safety-plan.md)
— the plan deferred any WAL-chunking decision until a healthy force scan on a 74k-file root was
measured. It has been. The plan's stated trigger condition ("multi-GB WAL") is met, and the
measurement also moves the primary suspect for the field report's exit-137 kills from extraction
parallelism to the artifact-write phase, which `--jobs` does not bound.

**What was measured:** the pinned `julie-extract 2.21.0` (the version Miller ships today), driven
both directly and through `miller workspace open --full`.

**Hardware:** Apple M-series, 24 logical cores, 64 GiB RAM, APFS. `MILLER_SEMANTIC=off` for the
fleet run, so every number below is the lexical/extraction path only.

**Contamination note:** an unrelated concurrent `julie-extract` run (another agent session on the same
machine, `--jobs 4`) was live throughout, and the confirmation run also overlapped eight concurrent
review processes. Every file-size number is read from this run's own paths and every process number is
scoped to this run's own PID tree, so those are unaffected — and the WAL, spool, and artifact peaks
came back **byte-identical** across the two runs, which is the check that matters. **Only wall-clock
time is affected, and it is an upper bound**: the same scan took 3,677 s and 4,907 s under the two
load levels.

---

## 1. The fixture

A generated polyglot repository, because the reporter's actual repo is not available and a fixture
of trivially small files would understate exactly the artifact volume being measured.

| | |
|---|---|
| Files | 74,000 |
| Source bytes | 289 MB |
| Languages | TypeScript 22k, C# 18k, Python 12k, JavaScript 8k, Go 6k, Rust 4k, Java 2k, Markdown/JSON/YAML 2k |
| Per file | 40–140 lines, 4–15 top-level symbols, doc comments, string literals, imports naming sibling modules |

The resulting artifact is representative of a real large repo, not of a synthetic best case:

| Artifact contents | |
|---|---|
| `files` | 74,000 |
| `symbols` | 1,623,355 |
| `identifiers` | 3,961,729 |
| `relationships` | 36,039 |
| Final `symbols.db` | **9.2 GB** |

---

## 2. Headline result — a healthy force scan produces a multi-GB WAL

`julie-extract scan --root <74k> --db <fresh> --force --jobs 4`, run to a clean exit 0.

Run twice: once summing every julie-extract on the machine, then again scoped to the launched PID tree
and wrapped in `/usr/bin/time -l`. **The disk figures are byte-identical across both runs.**

| Metric | Value |
|---|---|
| Wall time | **3,677 s (61.3 min)** idle-ish; 4,907 s (81.8 min) under load — see the contamination note |
| CPU consumed | 2,371 s user + 336 s sys over 4,907 s wall — **~0.55 cores average** |
| Peak spool (temp `.jsonl`) | **5,300.7 MB** (both runs) |
| Peak `symbols.db-wal` | **9,280.3 MB** (both runs, to the byte) |
| `symbols.db` size during the scan | **4,096 bytes** — for 3,582 of 3,677 s |
| Final `symbols.db` | **9,225.2 MB** (both runs) |
| Peak RSS, kernel-reported for the extractor alone | **31.7 GiB** (`maximum resident set size` 34,012,446,720) |
| Peak memory footprint | **33.9 GiB** (36,433,128,136) |
| Peak transient disk, all three live at once | **23.1 GB for a 289 MB source tree (82×)** — measured at t=3,638.8 s: 5.3 GB spool + 9.28 GB WAL + 9.1 GB db, during the checkpoint |
| Leftover spool after a clean exit | 0 |

That ~0.55-core average over 82 minutes is worth reading twice: `--jobs 4` was in effect, and the scan
still spent nearly all of its wall time barely using one core. Whatever the artifact-write phase is
bound by, it is not extraction parallelism.

Phase boundaries, from the 250 ms timeline:

| t | Event |
|---|---|
| 0 – 45 s | Extraction only. No artifact file exists yet; the spool grows to ~5.2 GB. |
| 45 s | Artifact opened. `symbols.db` created at 4,096 bytes; WAL begins. |
| 84 s / 102 s / 191 s | WAL passes 1 GB / 2 GB / 4 GB. |
| 860 s | WAL passes 8 GB. |
| 45 – 3,627 s | **`symbols.db` stays at 4,096 bytes.** One transaction; the entire artifact lives in the WAL. |
| 3,627 – 3,677 s | Commit, then checkpoint: `symbols.db` → 9.2 GB, WAL → 0. |

**This corroborates the field report.** The reporter said a two-worker run "approached 50 minutes and
produced roughly 14 GB of WAL". A four-worker run here took 61 minutes and produced 9.28 GB of WAL on
a synthetic repo of the same file count — the same order of magnitude and the same shape. The claim
was accurate.

**The plan's trigger condition is met**: a healthy force scan does show a multi-GB WAL. See §6 for
what that does and does not justify.

---

## 3. The memory blowup is the artifact write, not extraction

This is the measurement that changes the diagnosis. Two SIGKILL runs on the same fixture, each
scoped to its own process tree:

| Killed during | at t | peak RSS of the extractor | spool | WAL |
|---|---|---|---|---|
| **Extraction** | 60 s | **135 MB** | 5.2 GB | none (no artifact yet) |
| **Artifact write** | 129 s | 691 MB and climbing | 5.3 GB | 3.0 GB |
| (healthy run, whole scan) | — | **31.7 GiB** | 5.3 GB | 9.28 GB |

Extraction — the phase `--jobs` bounds — peaks at **135 MB**, and finishes in ~62 s. Everything above
that is the artifact write and the identifier-resolution work that follows it: **235× the memory, and
`--jobs` does not bound it at all.**

**Consequences for the program:**

- **W2 (`--jobs` cap) is still correct, but it is not the OOM fix.** It bounds extraction CPU and
  the rayon all-core thundering herd that made N concurrent worktree agents unusable. It does not
  reduce the 31.7 GiB peak, because that peak happens after extraction ends.
- **W3 (the machine-wide scan governor) is the OOM fix.** One extractor at a time, machine-wide, is
  what keeps a 31.7 GiB peak from being an N × 31.7 GiB peak. On a 64 GiB machine, two concurrent
  74k-file scans do not fit. That is the whole exit-137 story.
- A deliberate N-concurrent OOM was **not** run. Driving a 64 GiB machine into swap death would have
  taken out unrelated work on it, and the arithmetic (31.7 GiB × 2 > 64 GiB, before anything else on
  the desktop) does not need a demonstration.

---

## 4. What a SIGKILL leaves behind

Measured on the kill-during-artifact-write run:

| Leftover | Size |
|---|---|
| Spool `.jsonl` in `$TMPDIR` | **5.3 GB** |
| `symbols.db-wal` | **3.0 GB** |
| `symbols.db-shm` | 5.8 MB |
| `symbols.db` | 4,096 bytes |
| **Total** | **≈ 8.3 GB per killed scan** |

The WAL half is self-healing: the next **read-write** open recovers it — `symbols.db` grew to 450 KB
(schema only) and the WAL truncated to 0. A read-only open does not reclaim it, so the WAL survives
as long as only readers touch the artifact.

The spool half is not self-healing. `impl Drop for SpooledExtractedFiles` never runs on SIGKILL and
there is no reaper, so every killed scan leaks its spool permanently. **This machine already
demonstrated the accumulation independently:** before this measurement began, `$TMPDIR` held **48
orphaned spool files totalling 680 MB**, none with a live owning PID, accumulated over four days of
ordinary Scale-test runs by one agent on one machine. The field report's ~130 GB over two months
across a fleet is the same mechanism at fleet scale, and is entirely plausible.

**This half of program success criterion 2 is NOT met by the currently pinned extractor.** "At most
one dead-PID spool, reaped on the next scan" requires the W4 `--spool-dir` + reaper work, which is
written but unreleased. With `julie-extract 2.21.0` pinned, every killed scan still leaks.

---

## 5. Post-kill artifact state, and why the W1 gate matters

The artifact left by the kill-during-write run, after SQLite recovery:

```
artifact_metadata rows : 11   (root_path, binary_version 2.21.0, schema_version 5, artifact_id, …)
extraction_revisions   : 0
files                  : 0
symbols                : 0
```

`root_path` matches the canonical root exactly. So an OOM-killed scan leaves an artifact that is
**root-matching, metadata-complete, and completely empty** — which is precisely the state
`IndexBootstrapService.DecideBootstrapScan` would have read as "reuse this artifact, do not scan"
before the `hasCommittedRevision` gate was added.

Running the production query against both real artifacts:

| Artifact | `SELECT MAX(revision_id) FROM extraction_revisions` | Gate answer |
|---|---|---|
| Killed mid-write | **0** | no committed revision → scan |
| Healthy, completed | 1 | reuse |

The W1 fix was found by cross-model review as a hypothetical. It is not hypothetical: this is the
state an OOM-killed fleet machine is actually left in, and it is the state the reporter's machines
were in after every exit 137.

---

## 6. WAL chunking — the decision this measurement was the gate for

The plan: *"Only if a healthy force-to-`.rebuild` shows multi-GB WAL do we consider chunked commits."*
It does — 9.28 GB. So chunking is now on the table rather than speculative. But the measurement
argues for scoping it narrowly, and against treating it as the fleet fix:

- **Chunking would not touch the 31.7 GiB RSS.** Peak memory is in resolution work held in the process,
  not in uncommitted WAL pages. A repo that OOMs today would still OOM with chunked commits.
- **What chunking does buy** is bounded transient disk (9.28 GB of WAL is a second copy of a 9.1 GB
  artifact, and during the checkpoint both are on disk at once alongside a 5.3 GB spool — 23.1 GB
  measured) and a kill that costs the last chunk instead of the whole scan. On a 61-minute scan that
  is a real operational difference.
- **It must stay behind an explicit building/ready marker**, exactly as the plan says. A partially
  committed artifact is readable, and today's "unreadable until done" property — `symbols.db` at
  4,096 bytes for nearly the whole scan — is the only thing that currently makes a mid-scan artifact
  obviously not-ready — the artifact is absent or 4,096 bytes for 3,627 of 3,677 s (98.6%). Chunking
  removes that accident of safety and must replace it deliberately.
- **The cheaper win first:** the spool is 5.4 GB of the ~24 GB transient peak and 100% of the
  permanent leak. The W4 reaper is a smaller change with a larger operational payoff than chunking.

**Recommendation:** this is a `julie-extractors` decision, not Miller's, and Miller should not
speculate further ahead of it. Land W4/W5/W6 first (they are written), then revisit chunking with
these numbers. Miller needs no change either way — a chunked rebuild still promotes atomically
through `FullRebuildPromotion`.

---

## 6a. Miller's own watchdog was smaller than a healthy scan

The measurement was aimed at julie-extractors. It landed on Miller.

`JulieExtractRunner` bounds every extract with a progress-aware wait: kill after a **10-minute**
no-progress stall window, with an absolute backstop of `ExtractWaitPolicy.HardTimeoutFor` = stall ×
6 = **60 minutes**. The healthy 74k-file scan above took **61.3 minutes**.

So Miller killed it 77 seconds before it finished — with the message *"was still making progress but
timed out at the 3600s hard cap"* — and would have done so on every retry, forever. Under the fleet
load this program exists to survive, the same scan took 4,907 s, so the cap is not merely close: it is
beaten by a factor of 1.4. A cap a real
workload cannot beat is not a backstop against a runaway extractor; it makes the largest repositories
permanently unindexable. It is a fair mechanical reading of the field report's *"never could
converge"*: even with one worktree, no OOM, and no contention, the scan could not outlast the
watchdog.

The 10-minute stall window, by contrast, measured **safe with wide margin**:

| | Measured | Budget |
|---|---|---|
| Longest window with a completely unchanged progress stamp | **175 s** | 600 s |
| Next four longest | 81 s, 77 s, 45 s, 36 s | 600 s |
| Extraction phase with no artifact file at all (stamp is output-only) | 45 s | 600 s |

The stamp counts `db + -wal + -shm` bytes plus output activity, and the WAL never stops moving, which
is what keeps the long artifact-write phase visibly alive. W5's `--progress-file` still closes a real
blind spot — the pre-artifact extraction phase has no file to watch — but on this fixture that phase
is 45 s against a 600 s budget, so it is not what bit the reporter.

**Fixed on this branch** (`ExtractWaitPolicy.HardCapMultiplier` 6 → 24 = 4 hours at the default
window, plus a `MILLER_EXTRACT_HARD_CAP` override). This is the separable half of W5's Miller-side
work and needs no new extractor flag, so it did not wait on the pin bump.

---

## 7. Fleet end-to-end: 3 concurrent sibling worktrees

Real `miller workspace open --path <wt> --full`, three processes launched simultaneously against
three linked `git worktree` checkouts of one 6,000-file repo (each `.git` a **file**, the W9 case),
sharing one isolated `HOME` so the registry and the governor are machine-shared as they are in a
real fleet.

| Observation | Result |
|---|---|
| Max concurrent `julie-extract` processes (own PID tree, 383 samples @ 250 ms) | **1** |
| Concurrency histogram | `{0: 72, 1: 311}` |
| Extractors spawned | 3 — one per worktree |
| `--jobs` on every spawned extractor | **4** on all 3 |
| Exit codes | 0, 0, 0 |
| Registry state | all three `ready`, `last_revision = 1` |
| `.julieignore` seeded per worktree | 10 lines in all 3 |
| Leftover spool after clean exits | 0 |
| Per-worktree `duration_ms` | 34,121 / 69,250 / 104,724 — a ~35 s staircase, i.e. serialized admission |

**Program success criterion 1 is met**: N concurrent sibling-worktree `workspace open --full` runs
one extractor at a time, each with bounded `--jobs`, and all N end ready. The `duration_ms` staircase
against a flat ~28 s `scan_duration_ms` shows the queueing directly — each worktree waited for the
previous one's admission rather than competing with it.

---

## 8. Scorecard against the program's success criteria

| Criterion | Verdict |
|---|---|
| N concurrent sibling-worktree `open --full`: ≤1 extractor at a time, bounded `--jobs`, all N ready | **MET** — §7, measured end to end |
| SIGKILL leaves no unbounded orphan extractor and at most one dead-PID spool, reaped on the next scan | **NOT MET** — §4. Needs the unreleased W4 reaper; today every kill leaks its spool permanently |
| A fresh worktree without a local `.julieignore` still applies the main checkout's rules | **MET** — §7, seeded in all three worktrees |
| No automatic path immediately re-forces after a force failure | **MET after a fix this measurement prompted** — reviewing W8 alongside these numbers surfaced that a *successful* weaker scan left the elapsed throttle in place, so every later automatic read was admitted. Now re-spaced at the same streak |
| (new) A healthy scan of this size survives Miller's own watchdog | **MET after a fix** — §6a; it did not before |

## 9. Reproducing

The harness is not committed: it is a one-off measurement rig, and the expensive part (a 61-minute
scan) is not something CI or a Scale test should ever run. What it does is small enough to restate:

1. Generate the fixture described in §1.
2. `julie-extract scan --root <fixture> --db <fresh> --force --jobs 4`, sampling every 250 ms:
   RSS of the launched PID tree, total size of the spool files that tree owns, and the sizes of
   `symbols.db`, `-wal`, `-shm`, and the `.rebuild` siblings.
3. For the kill runs, SIGKILL the tree at a fixed elapsed time (extraction) or at a WAL size
   threshold (artifact write), then inventory what remains.
4. For §7, `git worktree add` three checkouts and launch three `miller workspace open --full`
   processes at once with a shared temp `HOME`, sampling live extractor count and argv.

Two harness mistakes are worth repeating so they are not repeated: matching processes by name alone
picked up an unrelated session's `julie-extract` and reported 3 concurrent scans where the governor
had in fact allowed 1; and summing every spool file in `$TMPDIR` attributed a foreign scan's spool to
this one. Both were fixed by scoping to the launched PID tree — every number in this document comes
from the scoped harness.
