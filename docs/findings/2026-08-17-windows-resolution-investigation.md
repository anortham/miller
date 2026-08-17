# Windows resolution slowness — investigation charter

- **Date:** 2026-08-17
- **Host:** native Windows
- **Workspace:** `C:\source\miller` (`miller-6662d0bd90fe`)
- **Binary:** published `1.19.4+10db3160e82b`
- **Producer:** pinned `julie-extract 2.33.5`
- **Source timings:** [`2026-08-17-windows-dogfood-1.19.4.md`](2026-08-17-windows-dogfood-1.19.4.md)
- **Owning plan:** [`plans/2026-08-13-miller-performance-recovery-plan.md`](../plans/2026-08-13-miller-performance-recovery-plan.md)

This is a follow-up investigation charter. It is not a resolver implementation.
**There is no resolver SQL or pin bump in the read-availability plan.**

## Ownership

Resolution wall time stays on the August 13 recovery plan. The
[`2026-08-17-windows-dogfood-read-availability-plan.md`](../plans/2026-08-17-windows-dogfood-read-availability-plan.md)
records these Windows numbers and keeps search and named inspect serving
while resolve runs. It does not change resolver SQL, crossover, base
rotation, or the julie-extract pin.

Task 1 of the read-availability plan is the user-visible relief: last-good
`search` and named `inspect` while the store is `converging` or `unbound`.
That work does not make resolve faster.

## Windows dogfood phases versus August 13 budgets

First `workspace refresh` on this box, then warm CLI reads after settle.
Phase times come from `indexer_phase_record` on that refresh.

| Work | Measured | August 13 Windows budget | Verdict |
|---|---:|---:|---|
| import | 23 s (`23348` ms) | none as a stand-alone gate | Record only. Import is not the slow arm. |
| resolve | 97 s (`96915` ms) | full resolution 120 s | Inside the full-resolution budget. Still too long to sit on the user-visible tool path. |
| coordinator | 121 s (`121570` ms) | none as a stand-alone gate | Import + resolve plus coordinator wrap. Same session as the 97 s resolve. |
| sidecar | 56 s (`56701` ms) | none as a stand-alone gate | Ran after the tool returned `queued`. Separate from resolve SQL. |
| one-file resolve | not measured here | 10 s | This refresh was a whole-workspace resolve, not the one-file gate. |
| warm inspect | 426 ms | 2 s | Met. |
| warm impact | 7234 ms | 5 s | Missed after settle. |
| warm context | 9501 ms | 5 s | Missed after settle. |
| warm trace | 1897 ms | 5 s | Met. |

The 97 s resolve is inside the 120 s constrained-Windows full-resolution
budget. It is still too long to block `search` or named `inspect`. The
read-availability plan already treats that wait as a serve-last-good
problem, not a resolver rewrite.

Warm `impact` (7234 ms) and `context` (9501 ms) missed the 5 s Windows
warm budgets after the index had settled. Those misses belong to the
August 13 relationship-query work (context evidence batching and impact
graph/SQL). They are not a producer resolve defect and they are not in
scope for the read-availability plan.

## What this plan must not do

- No resolver SQL or pin bump in the read-availability plan.
- Do not change crossover, base rotation, or identifier-resolution SQL.
- Do not raise the 4000 ms coordinator quantum as a Miller fix.
- Do not treat the 56 s sidecar converge as a reason to rewrite the
  producer resolver.

## Next owner

Keep new resolver work on
[`plans/2026-08-13-miller-performance-recovery-plan.md`](../plans/2026-08-13-miller-performance-recovery-plan.md):

1. Full real-Miller resolution versus the 60 s Linux / 120 s Windows
   budgets. This box's 97 s resolve is a new native-Windows data point
   inside the Windows budget, not a close-out.
2. One-file resolution versus the 5 s Linux / 10 s Windows budgets. This
   dogfood session did not run that gate.
3. Warm `context` and `impact` versus the 2 s Linux / 5 s Windows budgets.
   Use the 9501 ms and 7234 ms figures as the current native-Windows miss.

A later pin bump needs its own approval. This charter does not grant it.

## Related notes

- Parent dogfood list: [`2026-08-17-windows-dogfood-1.19.4.md`](2026-08-17-windows-dogfood-1.19.4.md) §2 and the post-settle table
- Separate stall, not slowness: [`2026-08-17-julie-extractors-windows-lock.md`](2026-08-17-julie-extractors-windows-lock.md)
- Linux full-resolve baseline still over its 60 s budget:
  [`2026-08-14-performance-recovery-baseline.md`](2026-08-14-performance-recovery-baseline.md)
