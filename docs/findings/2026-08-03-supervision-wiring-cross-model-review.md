# 2026-08-03 — Cross-model review of the Miller supervision wiring

The delta this covers is `2d5440bf..HEAD` on `fleet-safety`: the W4/W5/W6 Miller
wiring, the `julie-extract` 2.22.0 pin bump, and the plan closeout — 16 code and
test files. The branch's earlier commits were reviewed separately
([2026-08-02](2026-08-02-fleet-safety-branch-cross-model-review.md)).

Two reviewers ran independently on the same diff and the same five named attack
surfaces, neither seeing the other's output: **Codex** `gpt-5.1-codex-max` and
**Grok** `grok-4.5`, both read-only.

| Reviewer | Verdict | Findings |
|---|---|---|
| Grok | approve | 0 |
| Codex | needs-attention | 2 (both medium) |
| Lead's own sweep | — | 2 (one shared with Codex, one it did not raise) |

Grok spent 25.5k reasoning tokens and enumerated what it attacked per surface,
so the clean result is a substantiated one rather than a rubber stamp. It
independently named the Windows job object as a *documented platform gap* while
clearing the dispose-after-exit design — the same residual the wiring findings
doc already records.

## Fixed

**1. Windows containment failures were silently discarded** (Codex 0.97, and the
lead's own sweep — the only convergent finding, which is the pattern the earlier
julie review showed too).

`WindowsKillOnCloseJob.Attach` returns a structured `Failed(reason)` for a failed
`CreateJobObject`, `SetInformationJobObject`, or `AssignProcessToJobObject`, and
`Run` kept only `.Job`. The scan then continued **uncontained and unannounced**,
on the one platform where nothing else covers it — `--parent-pid` is Unix-only.
That is precisely the failure the change exists to prevent.

`JulieExtractRunner` now takes an optional `Action<string>` sink, following the
pattern `RepositoryIndexLoader` already documents (`Miller.Indexing` is
logger-free by design, so the caller holding an `ILogger` supplies the sink).
`IndexerService` and `IndexBootstrapService` wire it to a warning; the CLI and
dashboard runners leave it unwired and therefore still silent, which is a
deliberate stopping point rather than an oversight. An internal constructor makes
the attach function injectable, so a test can force the failure that is
unreachable off Windows and unreproducible on it.

**2. An unterminated trailing line was consumed as a record** (Codex 0.99, not
raised by Grok or by the lead).

The progress-file v1 contract is explicit: *"A trailing line without a
terminating newline is an incomplete tail. Parsers must drop it and read it again
on the next poll."* `LastIn` parsed the final split segment regardless.

Codex's stated consequence — "false phase and counter diagnostics" — **does not
hold**, and the record says so rather than inheriting the reviewer's framing. Any
strict prefix of a JSON object fails to parse, so the only tail that both parses
and lacks its newline is one whose object was written whole; its counters are
genuine, and dropping it actually reports staler data. Miller also reads this
file only after the child is dead, so the "still being extended" hazard the
contract guards cannot arise at today's single call site.

It was fixed anyway, for the reason that does hold: Miller is the consumer of a
written contract, and a consumer that quietly diverges is the thing that breaks
when the read site moves. Surfacing live scan progress in `workspace status` is
an obvious next step, and it would have inherited a subtly wrong parser.

Both fixes are mutation-proved: restoring `lines.Length - 1` and re-discarding
the failure reason fails three tests; reverting returns the suite to green
(fast 5,762 passed, Scale 90 passed).

## Not acted on

- **Codex could not run the tests** — its read-only sandbox blocked MSBuild from
  creating a temp directory, so its findings are static-analysis only. Both were
  verified against the code by the lead before being accepted, and the contract
  claim was checked against `progress-file-v1.md` rather than taken on trust.
- **Codex's "strict-containment option"** (refuse the scan rather than run
  uncontained) — out of scope and against the class's own rule that containment
  hygiene must never break the work it protects. Parked.
- **`WorktreeIgnorePropagationScaleTests` returns instead of `Assert.Skip` on
  Windows**, so a POSIX-only test reports as passed there. Pre-existing, not
  introduced by this branch, and not touched.

## Lead-found, fixed before the reviews returned

The `supervision` doc paragraph on `BuildScanArgs` sat between `</summary>` and
the first `<exception>`, making it loose top-level text that would not render
alongside the `jobs` and `ignoreFiles` paragraphs it was written to join
(`06aaf81b`).

## Refuted during the lead's sweep

If julie-extract wrote its heartbeat on a timer rather than on counter advance,
`ProgressStamp` could never detect a wedged extractor — the stall kill would be
dead. Refuted by reading `progress.rs`: `advance()` returns early when `by == 0`,
and `enter_phase` fires only on the six phase transitions.
