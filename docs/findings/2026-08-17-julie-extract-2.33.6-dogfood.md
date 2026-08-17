# julie-extract 2.33.6 native Windows dogfood

- **Date:** 2026-08-17
- **Host:** native Windows, Grok Build TUI, Miller MCP pointed at the local Release `miller.exe`
- **Binary:** `1.19.4+7d4a6905d7b2` (`deps: pin julie-extract 2.33.6`)
- **Producer:** pinned `julie-extract 2.33.6`
- **Workspace:** `C:\source\miller` (`miller-6662d0bd90fe`)
- **Store:** family `9f173abc-…9386`, view `e32fd74f-…eff4`

This is the first live pass after the 2.33.6 pin. It does not change product behavior.

## Live identity

After rebuild and MCP restart:

- Leader pid `9404`, reader pid `18128`, both `1.19.4+7d4a6905d7b2`
- `own_extractor_version` and `artifact_extractor_version` both `2.33.6`
- Eligibility: `extractor 2.33.6 matches the index artifact 2.33.6`

## Reads while resolve ran

The first post-restart cycle imported two files, then ran `store resolve` for 75.2 s.

| Call | While `resolution_state=converging` | Result |
| --- | --- | --- |
| `search PinnedJulieExtractVersion` (lexical) | last-good sidecar still showed `2.33.5` | served |
| `inspect` file `MillerExtractContract.cs` | listing showed pin `2.33.6` | served |
| `inspect MillerExtractContract` `depth=summary` | named type | served |
| `inspect MillerExtractContract` `depth=overview` | `resolution_converging` | expected empty |

Overview/full inspect stays gated until the identifier layer is exact. That matches
`InspectTool` (`parsedDepth is not Summary` plus `ResolutionLayerConverging`).

## Phase times

Same log file, same machine, same family store.

| When | Producer | Phase | ms | Notes |
| --- | --- | --- | --- | --- |
| 14:49:36 (prior session) | 2.33.5 | resolve | 186174 | last large 2.33.5 resolve |
| 14:50:07–14:50:43 | 2.33.5 | resolve | 10762–25704 | later 2.33.5 one-file-ish resolves |
| 14:52:43 | 2.33.6 | import | 10068 | two-file import after restart |
| 14:53:58 | 2.33.6 | resolve | 75170 | first 2.33.6 resolve (cold after restart) |
| 14:54:00 | 2.33.6 | coordinator_total | 87948 | import + resolve |
| 14:54:09 | 2.33.6 | sidecar_total / startup_total | 8044 / 99240 | content 3.1 s, search 4.5 s |
| 14:54:11 | 2.33.6 | import | 2381 | leader-upgrade follow-up |
| 14:54:49 | 2.33.6 | resolve | 38011 | second 2.33.6 resolve |
| 14:54:49 | 2.33.6 | coordinator_total | 40407 | |
| 14:54:53 | 2.33.6 | sidecar_total | 4043 | |

Neither 2.33.6 resolve was a whole-repo extract. The first followed a two-file import. The
second followed a 2.4 s upgrade import. Compare them to the 2.33.5 186 s spike and the earlier
~97 s full-store profile, not to a 10 s one-file save.

## Steady state after the second cycle

- `store.state=ready`, `index_level=full`, `resolution_state=exact`
- `index_fresh=true`, `freshness_status=current`
- search sidecar current at revision 21882 (106499 documents)
- content corpus current
- vectors ready
- `inspect MillerExtractContract depth=overview` returned the live pin `2.33.6`

## What this does not prove

- One-file save time after 2.33.6 (the 5–7 s extract/claim/publish path)
- A cold full-store resolve of the same size as the 97 s profile copy
- A Miller marketplace/GitHub release (plugin stays 1.19.4)
