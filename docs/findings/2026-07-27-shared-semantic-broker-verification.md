# Shared semantic broker verification

Date: 2026-07-28

This ledger covers the Task 8 process, crash, model-isolation, and accelerator proof for the shared semantic
broker, followed by the published RC5 and Miller default-on closure evidence. The original process proof used
the Miller worktree at `687011f5` and sidecar worktree at `741850a`; the release dependency is the published
sidecar commit `13fff87bcaa9cc93feac465141756f4fc36183f5`.

## Candidate

- The original Task 8 source-built candidate reported `julie-semantic-sidecar 0.1.0-rc.4`; version publication
  belonged to Task 9.
- Tested macOS binary SHA-256:
  `6d2fa03c08d051d9be28bd32570d06e233492987310352a26ee52ac9f10d21b9`.
- Both pinned models were prepared and checksum-verified by the sidecar before the run.
- The original Task 8 Miller pin remained RC4 and the repo-local runtime was absent. That skip remains historical
  pre-release evidence and is not counted as broker-capable proof.

## Published RC5 dependency

- GitHub release: <https://github.com/anortham/julie-semantic-sidecar/releases/tag/v0.1.0-rc.5>
- Target commit: `13fff87bcaa9cc93feac465141756f4fc36183f5`
- Protected package runs:
  [30360072357](https://github.com/anortham/julie-semantic-sidecar/actions/runs/30360072357) and
  [30360964021](https://github.com/anortham/julie-semantic-sidecar/actions/runs/30360964021)
- Both runs produced byte-identical archives for all four targets. The second run passed every lane, including
  the exact Apple arm64 checksum guard and Linux/Windows Vulkan half-float zero-store verification.
- Fresh public downloads matched their SHA-256 sidecars and the retained successful-run archives. Every unpacked
  manifest verified the same v4 native-build identity.
- The public Apple arm64 package passed artifact validation and a direct broker lifecycle smoke: current-user
  Unix bind, frozen health request/response, owner-stdin EOF shutdown, empty stdout, and endpoint removal.

Miller now pins those public assets exactly:

| Target | SHA-256 |
|---|---|
| Apple arm64 | `4c62e729124ba30640a0b3a8c0f8a4d9f5b8cc4e02a6de640b5baa9039ff2ddc` |
| Apple x64 | `959ab0e1869f0eeb68f237f1ca1266f0440f33a1b42ebbfe370d2e3fb8be8a6e` |
| Linux x64 Vulkan | `a2f0bcd0135cc056465d12353572462f877e9ae7ca5a988a0012de1038a4a36f` |
| Windows x64 Vulkan | `47f9b1bcc149c781d6d95d74e3e0207142d3f587210872758e5b208fef3b091a` |

## Miller default-on production dogfood

Two real Miller MCP servers were started from the Release build with `MILLER_SEMANTIC` absent and a shared,
isolated Miller home. Both completed explicit semantic searches with ten results. Status from the two live
sessions reported:

- the same endpoint identity, `90bf0aac063d5036`;
- exactly the complementary `owner` and `non_owner` roles;
- the same `bge-small-en-v1.5-f32` model and `metal` backend;
- one accelerated broker identity; and
- `ready` state for both sessions.

The focused RC5 package/broker gate passed 12 tests with two hardware-recording skips. The Scale harness's
eight same-model real-process test passed against the restored public RC5 package.

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
| Same-model N sessions use one model-loaded broker | Passed on macOS | Production-connector short soak, sidecar process test, and two real default-on Miller MCP sessions |
| Different model identities use separate endpoints | Passed on macOS | Two distinct endpoint identities |
| At most one model owns acceleration | Passed deterministically | Sidecar fake-engine process test shares the production lease/serve seam; this is lifecycle evidence, not model or VRAM evidence |
| Client/owner/broker death unblocks and recovers within 30 seconds | Passed on macOS | No hung requests; post-kill recovery events at 0.789 and 0.880 seconds |
| No orphan broker after owner termination | Passed on macOS | Candidate-scoped final process count was zero |
| GPU residency remains effectively constant | Pending 6GB NVIDIA hardware gate | Warm must be accelerated with one broker and at least a 64 MiB global delta, then many-session delta must be no more than warm plus 256 MiB |
| Linux process and portable accelerator behavior | RC5 package guard passed; process soak pending | Protected Linux Vulkan package lane passed; run the same process script on a Linux host |
| Windows rapid reconnect and sleep/resume | Pending release gate | PowerShell runs eight rapid reconnects; pass `-SleepResumeWindowSeconds` and sleep/resume the laptop during that window |
| Windows Job Object cleanup | Pending release gate | Confirm candidate-scoped final broker count is zero |
| Thirty-minute soak | Passed on macOS | 1,800.012 seconds of traffic; zero hangs/failures; 17/17 normal completions; broker/owner recovery in 0.818/0.967 seconds; final broker count zero |
| PowerShell runner parse/execution | Pending Windows gate | `pwsh` was not installed on this macOS host |

WDDM per-process `N/A` is not accepted for the NVIDIA row. The PowerShell runner records global
`nvidia-smi --query-gpu=memory.used` totals, broker count, and the warm-versus-many delta. If warm acceleration
or the 64 MiB proof floor is absent, both GPU proof and GPU acceptance remain `null` and pending rather than
passing.
