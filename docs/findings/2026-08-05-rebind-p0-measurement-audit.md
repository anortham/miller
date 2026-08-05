# Rebind P0 — measurement audit and clone-cost result — 2026-08-05

**Status:** P0 of the [worktree delta-rebind program](../plans/2026-08-02-worktree-delta-rebind-program.md)
started (user-approved 2026-08-05). This doc records the one measurement runnable today, inventories which
P0 items prior findings already satisfy, and names what remains with its prerequisites. The decisive input
for P1's v1-shape decision is in hand.

## New measurement — artifact clone cost (macOS/APFS)

Real artifact, not a fixture: this repo's live `symbols.db` (755 MiB). Apple M-series, APFS, NVMe.
File recently read, so the full-copy number is warm-cache — a best case, stated as such.

| Method | Wall time | Notes |
|---|---:|---|
| APFS `clonefile` (`cp -c`) | **0.00 s** | Metadata-only; no data blocks copied. Cost is independent of size. |
| Full byte copy (`cp`) | 0.25 s | ~3 GB/s warm-cache; cold NVMe would be slower. |

Extrapolation to the 22.84 GiB dotnet/runtime artifact
([v2.23.1 baseline](2026-08-03-dotnet-runtime-v2231-baseline.md)): `clonefile` stays effectively instant;
a full copy is tens of seconds warm and low minutes cold.

**Implication for P1:** on APFS the copy in copy-and-rebind is free, which removes base+overlay's main
advantage (disk/latency) on the primary dev platform. Linux reflink (btrfs/XFS) is expected to behave like
`clonefile` but is **unmeasured** (needs a Linux box); ext4 and Windows always pay the full copy.
Copy-and-rebind remains the recommended v1 shape; base+overlay stays a documented future option.

## P0 items already satisfied by prior findings

- **Clean, crash-free timed extract of dotnet/runtime @ pinned commit** (P0 acceptance box 2):
  satisfied by [`2026-08-03-dotnet-runtime-v2231-baseline.md`](2026-08-03-dotnet-runtime-v2231-baseline.md)
  — 76.3 min wall at v2.23.1, 22.84 GiB artifact, phase timings, plus the validated 18.8 min result with
  the bulk-cache and savepoint fixes that now ship in the pinned 2.25.0.
- **WAL / spool / peak-RSS at `--jobs 4`, 74k files:** measured in
  [`2026-08-02-w10-scale-repro-wal-measurement.md`](2026-08-02-w10-scale-repro-wal-measurement.md)
  (31.7 GiB peak RSS, resolution/artifact-write dominated). **Caveat:** measured on julie-extract 2.21.0;
  the 2.25.0 resolution fixes changed exactly the phase that dominated RSS, so these numbers bound the old
  binary, not the current one.

## P0 items remaining, with prerequisites

1. **RSS-per-`--jobs` curve at 2.25.0** (drives future memory-aware admission). Blocked on a scan target:
   the W10 74k fixture was generated in a session scratchpad and not checked in (its spec survives in the
   W10 doc), and the dotnet/runtime clone used by the baseline is no longer on disk. Either regenerate the
   fixture from the spec or re-clone `dotnet/runtime @ a2f953fe266`; the sweep itself is hours of
   machine time and should run when the workstation is idle.
2. **Post-scan vector convergence stacking across N workspaces** (convergence runs outside the scan
   governor). Needs a semantic-on multi-workspace run; pairs naturally with the sweep above.
3. **Linux inotify watch consumption across N worktree workspaces.** Not measurable on this Mac; needs a
   Linux box or CI runner.
4. **Linux reflink / Windows copy cost.** Same platform gap as 3.

## Verdict

P1 (rebind contract design doc) is unblocked: its shape decision needed the clone-cost data, which is now
measured on the primary platform and directionally clear for the others. The remaining P0 items inform the
governor's future memory-aware admission, not the contract, and can land in parallel with P1.
