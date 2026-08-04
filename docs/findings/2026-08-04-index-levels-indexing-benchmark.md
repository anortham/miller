# Index-levels indexing benchmark — 2026-08-04

Measures what `MILLER_INDEX_LEVELS=symbols` actually saves on a cold index, with the randomized order,
repetitions, and spread the earlier levels benchmark lacked (recorded as Out of Scope in
`docs/plans/2026-08-04-levels-guard-coverage.md`).

## Method

- Corpus: a full copy of the Miller checkout — 1,389 indexed files, 121,044 symbols
  (csharp 65,313 / json 43,896 / markdown 5,552 / python 3,497 / javascript 849).
- Command per run: `MILLER_SEMANTIC=off MILLER_INDEX_LEVELS=<mode> miller workspace open --path <repo>`,
  after `rm -rf <repo>/.miller` — every run is a cold, full extract.
- 5 repetitions per mode, **order alternated each repetition** (symbols-first on odd reps, full-first on
  even) so warm-cache drift cannot systematically favour either mode.
- `MILLER_SEMANTIC=off` throughout, so these numbers exclude embedding generation.
- Machine: darwin 25.6.0, Apple silicon. julie-extract 2.25.0.

## Result

| Mode | Wall median | Min–max | Spread | Extract phase (median) | Post-extract (median) |
|---|---|---|---|---|---|
| `symbols` | **9,696 ms** | 9,642–9,950 | 308 ms (3.2%) | 5,349 ms | 4,347 ms |
| `full` | **27,383 ms** | 27,212–27,645 | 433 ms (1.6%) | 16,804 ms | 10,579 ms |

**Symbols level is 2.82× faster end-to-end — 17.7 s saved on this corpus.**

The saving is not confined to the extractor. The julie-extract phase drops 3.14×, and the post-extract
phase (artifact write plus Miller's sidecar convergence) drops 2.43× on its own, because the emptied
tables are large: at full level this corpus carries 373,105 `identifiers`, 166,695 `source_regions`, and
59,941 `structural_facts`.

Symbol count is **identical** in both modes (121,044). Symbols level costs no definitions, no search
recall, and no relationship edges — only the per-usage identifier, region, and facts layers, which is
exactly the boundary every guard added in the levels-guard-coverage branch announces.

Spread is tight in both modes (≤3.2%), so the 2.82× ratio is not order or cache noise.

## Raw data

```
mode,rep,wall_ms,scan_duration_ms,duration_ms,symbols,level
symbols,1,9950,5392,9845,121044,symbols
full,1,27645,16909,27533,121044,full
full,2,27455,16804,27341,121044,full
symbols,2,9844,5471,9730,121044,symbols
symbols,3,9655,5349,9547,121044,symbols
full,3,27212,16878,27101,121044,full
full,4,27383,16723,27272,121044,full
symbols,4,9696,5349,9581,121044,symbols
symbols,5,9642,5225,9534,121044,symbols
full,5,27289,16628,27175,121044,full
```
