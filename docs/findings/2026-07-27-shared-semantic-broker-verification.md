# Shared semantic broker verification

Date: 2026-07-28

This ledger covers the Task 8 process, crash, model-isolation, and accelerator proof for the shared semantic
broker. It is pre-release evidence from the pinned Miller worktree at `687011f5` and the sidecar worktree at
`741850a`; it is not rc.5 release evidence.

## Candidate

- Source-built sidecar reports `julie-semantic-sidecar 0.1.0-rc.4` because the rc.5 version bump belongs to
  Task 9.
- Tested macOS binary SHA-256:
  `6d2fa03c08d051d9be28bd32570d06e233492987310352a26ee52ac9f10d21b9`.
- Both pinned models were prepared and checksum-verified by the sidecar before the run.
- Miller's published pin remains rc.4 and the repo-local runtime was absent. The Scale harness skips that path
  with an actionable instruction to restore rc.5 or supply a from-source tools root. It must not be counted as
  broker-capable evidence.

## RED evidence

The new sidecar test initially failed to compile with three `E0425` errors for the deliberately missing
same-model, multi-model, and recovery scenario drivers. A later false-pass review added `E0609` RED assertions
for measured loser exits, concurrent query/batch traffic, endpoint model identity, in-flight unblock, and
post-cleanup live processes. After those test-owned drivers were implemented, the focused test passed four
tests.

The final GPU false-pass test failed RED with `CS0117` because the shared validator did not yet expose recorded
NVIDIA evidence validation. The added contract requires `gpu.pass=true`, an accelerated warm model, exactly one
warm broker, a warm delta of at least 64 MiB, and a many-session delta no greater than warm plus 256 MiB.

The first macOS soak exposed a real harness defect. `mktemp` used the long `/var/folders/...` macOS temporary
root, making the Unix socket exceed macOS's 104-character limit. Miller failed before spawning and reported the
exact invalid path. The runner now deliberately uses a short `/tmp/miller-semantic-soak.*` home on Unix.

## macOS production-connector result

The corrected and false-pass-hardened short run used the production `SharedSemanticBrokerConnectionFactory` and
`SemanticEmbeddingSession`, not a test connector:

| Check | Result |
|---|---|
| Warm model-loaded broker count | 1 |
| Eight same-model client broker count | 1 |
| Query and batch traffic | 0 hung, 0 failed, 0 failed terminal events |
| Normal probe lifecycle | 17 expected, 17 completed, every exit code 0 |
| Expected killed clients | 2 expected, 2 observed without a terminal completion |
| Broker kill recovery | Post-kill recovery event after 0.789 seconds |
| Owner Miller kill recovery | Post-kill recovery event after 0.880 seconds |
| Old/new endpoint identities | `90bf0aac063d5036` / `a5d53c7dd92b2107` |
| Accelerated brokers | 0 on this CPU run, therefore at most 1 |
| Broker processes after completion | 0 |
| Configured/observed soak duration | 5 seconds / 5.070 seconds |

The JSONL records include candidate identity, checksum, process ID, endpoint, owner PID, model/hash, backend,
accelerator/degraded facts, reconnect/spawn counts, request totals, wall-clock event times, and observed traffic
duration. Recovery counts only a `recovered` event whose wall-clock time is later than the recorded kill; a
missing event is a failure, never a process-lifetime estimate. The shared validator rejects failed events,
missing terminal completions, nonzero normal exits, shortened soaks, and any non-null false acceptance row.
It also rejects `acceptance.gpuEffectivelyConstant=true` unless `gpu.pass=true`, and requires the two fields to
agree when GPU proof is available or explicitly unavailable.
The process tree and global GPU snapshots are separate artifacts. Probe output never contains generated query
or batch text.

## Acceptance matrix

| Row | State | Evidence or remaining gate |
|---|---|---|
| Same-model N sessions use one model-loaded broker | Passed on macOS | Production-connector short soak and sidecar process test |
| Different model identities use separate endpoints | Passed on macOS | Two distinct endpoint identities |
| At most one model owns acceleration | Passed deterministically | Sidecar fake-engine process test shares the production lease/serve seam; this is lifecycle evidence, not model or VRAM evidence |
| Client/owner/broker death unblocks and recovers within 30 seconds | Passed on macOS | No hung requests; post-kill recovery events at 0.789 and 0.880 seconds |
| No orphan broker after owner termination | Passed on macOS | Candidate-scoped final process count was zero |
| GPU residency remains effectively constant | Pending release gate | Run on the 6GB NVIDIA Windows laptop; warm must be accelerated with one broker and at least a 64 MiB global delta, then many-session delta must be no more than warm plus 256 MiB |
| Linux process and portable accelerator behavior | Pending release gate | Run the same script on the release Linux candidate |
| Windows rapid reconnect and sleep/resume | Pending release gate | PowerShell runs eight rapid reconnects; pass `-SleepResumeWindowSeconds` and sleep/resume the laptop during that window |
| Windows Job Object cleanup | Pending release gate | Confirm candidate-scoped final broker count is zero |
| Thirty-minute soak | Pending release gate | Default runner duration is 30 minutes; extend overnight if any recovery row fails once |
| PowerShell runner parse/execution | Pending Windows gate | `pwsh` was not installed on this macOS host |

WDDM per-process `N/A` is not accepted for the NVIDIA row. The PowerShell runner records global
`nvidia-smi --query-gpu=memory.used` totals, broker count, and the warm-versus-many delta. If warm acceleration
or the 64 MiB proof floor is absent, both GPU proof and GPU acceptance remain `null` and pending rather than
passing.
