# Sidecar convergence reliability verification

Date: 2026-08-22
Platform: Linux
Branch: `plan/linux-dogfood-fixes`
Worktree: `/home/murphy/source/miller/.worktrees/linux-dogfood-fixes-plan`
Branch HEAD during this packet: `219c51d4`

## Scope and outcome

The unchanged-refresh and semantic-off paths were replayed against an isolated copy of
more-itertools. The source checkout and its live `~/.miller/live` sidecars were not modified. The
replay proved target-aware search repair, truthful refresh/health output, semantic-off zero work,
and lexical output identity. A live resident vector completion was not claimed: the isolated
one-shot process had no prepared semantic model and therefore correctly reported that a resident
leader was required.

The isolated full open reported source revision `56` and family id
`31181f00-61ae-4959-8dd1-0ad8220b9d35`. The only sidecar removed was the validated temporary search
database under the temporary Miller home. The temporary root was moved recoverably to
`/home/murphy/.local/share/Trash/files/miller-sidecar-dogfood.EeBgjS-20260822`; no matching
temporary process remained, and the live repository sidecars were untouched.

## Unchanged refresh and stale search

The initial isolated status showed content and search current at target sequence `56`. Vector was
`leader_required`, with `did_work=false`, `pending=true`, and the one-shot note that no embedding
was performed. After deleting only the validated temporary search sidecar, the next unchanged
refresh reported:

- `scanned=true`, with source revision and target sequence still `56`;
- search `repaired`, content `current`, and vector `leader_required`;
- `did_work=true`, with the exact target sequence still `56`.

The `scanned=true` field records that Miller ran refresh/convergence work; it does not mean the
source extractor changed the revision. No source extraction or source-tree change occurred in this
replay. A second unchanged refresh reported search `current` and `did_work=false`, showing that the
repair did not become a retry loop.

## Health and semantic-off behavior

After the missing-vector-completeness state was reproduced, health was `degraded` and the first
recommended action was:

> open or keep a resident Miller leader running so vector convergence can complete

No recommended action told the operator to run a generic workspace refresh. Health reported vector
unavailable because completeness was missing, while the semantic broker was `not_started`.

With semantic retrieval disabled, status showed no vectors, broker `off`, and current search and
content. An unchanged refresh reported target sequence `56`, vector `disabled`, `pending=false`,
and `leader_required=false`. The vector database row count was `0`, and the semantic directory was
absent. No embedding or broker work was performed.

The explicit-arm lexical JSON query `chunked` returned 6 hits and 1,445 bytes after shell newline
normalization. Default and semantic-off output was byte-identical with SHA-256
`79219f4c5e3adb01bbb6b25132845730b55ba4528aca05c38e5c8f1a15f22771`.

## Deterministic evidence

| Scope | Evidence | Result |
| --- | --- | --- |
| Task 1 | Typed convergence outcomes and isolation | 76/76; Release build 0 warnings/errors |
| Task 2 | Idle production path, 5/15/30 retry bounds, changed-target immediate retry, success clear, nonleader/store-off zero work | 259/259 |
| Task 3 | Startup missing stamp, shadow wake, current restart zero work, held exact suppression, semantic-off | 82/82 |
| Task 4 | Action/priority rendering before correction; priority-correction regression scope | 287/287, then 188/188; builds 0 warnings/errors |

## Remaining gates

The isolated one-shot replay did not generate real resident vector completeness because its semantic
model was unprepared. The resident semantic-broker and branch gates nevertheless passed on this
exact tree:

- `scripts/test.sh`: 8,255 passed, 0 failed, 9 skipped;
- `scripts/test.sh scale`: 154 passed, 0 failed, 16 skipped;
- `scripts/semantic-broker-soak.sh`: exit 0 and verified; 1,800 seconds, 17/17 normal probes,
  0 hung, 0 failed, recovery broker 0.663s / owner 0.659s, expected kills 2/2,
  `finalBrokerCount=0`, and `noOrphanBroker=true`;
- GPU memory was unavailable (`null`), which was reported and was not a failure;
- `dotnet build Miller.slnx -c Release --no-restore`: passed with 0 warnings and 0 errors;
- the post-soak process check found no isolated `/tmp` soak process (only the check command itself).

These gates complete the no-retry-loop, broker-leak, and sidecar-lease-after-shutdown criterion.
The final documentation/commit criterion remains pending only because the lead has not yet recorded
the serialized Task 5 commit and final state.
