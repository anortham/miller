# julie-extract 2.32.0 Dogfood — Blocked Findings

## Result

Miller's released-producer compatibility tests pass, but live dogfood is not release-ready. The run found one
Miller recovery defect, now fixed and focused-green, plus two julie-extract 2.32.0 producer blockers. The failed
Julie workspace is still unreadable and the Miller workspace is still resolving; the remaining public-surface,
legacy-off, package, and Windows gates must be repeated after the producer fixes.

## Why partial resolution persisted

- The live Miller leader is `1.18.1+470cfc1d` and records bundled extractor `2.31.4`, while the artifact records
  `2.32.0`. It remained the leader after the copied tool was replaced and launched the new binary from the old host.
- Resolver PID 1729619 is conclusively 2.32.0: `/proc/1729619/exe` SHA-256
  `5a5a32a60f6e060d2bc583e1f81968ee227deaf827f4fa86c9aae3655fa26877`, identical to both restored binaries.
- One changed file, `tests/Miller.Tests/Indexing/MillerExtractContractTests.cs`, recorded 86 touched names. The scoped
  scratch database expanded that to 189,880 and then 253,882 identifier resolutions; phase 1 and phase 3 were ready
  while phase 2 never became ready. At the last bounded observation the process had run 17m20s at 98% CPU, about
  27 MB RSS, with no terminal result. This exceeds the prior 2.31.4 full-corpus baseline of about 2m24s.
- Store/view/request IDs: family `a271f2bd-7368-4da6-b5aa-24ffad69fb1f`, view
  `7857a50b-4b5a-47ba-8c45-d4df703cc79e`, request `6a4c1fc90bfb47a8871177daa21f7398`.

## Julie workspace recovery

Before recovery, `/home/murphy/source/julie-extractors/.miller/store.json` pointed to a missing family root and the
preserved 2.31.3 legacy artifact reported `reference_resolution_status=partial`. Its dead 1.17.0/2.29.0 leader
journal recorded 87 failed `RootRebind` attempts.

The first current-candidate `workspace open --path /home/murphy/source/julie-extractors --json` exposed a Miller
defect: a valid pointer to an absent store was refused as `ineligible_extractor` before RootRebind. A real-producer
Scale regression failed `Expected: Refreshed; Actual: IneligibleExtractor`. The fix allows legacy version evidence
only for this precise absent-store-root case and selects `ScanIntent.RootRebind`; malformed pointers and corrupt
existing stores still refuse leadership. The exact Scale regression passed, followed by 84 affected pure tests.

With that fix, the public recovery reached 2.32.0 `store import --from-artifact`, then failed after 17.62s with
`store-writer lease fencing check failed` and peak RSS 1,024,140 KB. Request
`ddc314ef493946158b7a9d013465af67` remains claimed by exited PID 1738073, with no terminal log/result. The import
created the store and loaded data, but a likely un-heartbeated 15-second writer lease expired before publication.
A bounded retry created no new request or mutation and was safely refused against the unreadable incomplete store.

## Before-state facts

- Candidate: Miller `1.18.1+720fb88d652a`, pinned julie-extract `2.32.0`, clean Release rebuild.
- Miller store: 2,681,700,480 bytes; `store.db` 1,871,900,672; search 358,821,888; content 128,643,072;
  vector 16,613,376. Cursor 8327, resolution converging, search/content stale, vectors unstamped.
- Semantic broker was honestly ready on Vulkan with the configured model and accelerator lease.
- Julie legacy `.miller`: 1,450,540,588 bytes; `symbols.db` 1,029,136,384; search 314,146,816;
  content 90,804,224; vectors 14,540,800.
- Candidate status/health JSON parsed successfully. Status took 0.33s and health 0.48s for Miller; Julie status
  took 0.17s and health 0.18s.

## Gate disposition

- Hard gate passed: bundled/version/capability contract and focused consumer equivalence from Task 3.
- Hard gate passed: Miller dangling-pointer RootRebind recovery regression and affected pure tests.
- Hard gate failed: live Julie workspace exact recovery (`store-writer lease fencing check failed`).
- Hard gate failed: live Miller exact convergence and sidecar readiness (2.32 scoped resolution CPU regression).
- Blocked behind those failures: repeatable context/search performance, all public read/edit surfaces,
  legacy-off compatibility, Windows asset/CI proof, and Miller release preparation.

No push, tag, publish, release, process signal, store deletion, or stale legacy fallback occurred.
