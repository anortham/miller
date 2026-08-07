# Write-side mechanics: measured output

SQLite library: 3.53.4

## 1. GC: physical reclamation

Store: 6,100 file versions across 1,220 paths x 5 generations, 694 rows per file version, page_size 4096, staged vacuum budget 2,000 pages.

| arm | auto_vacuum | delete pattern | built (MB) | after DELETE (MB) | freelist pages | after incremental_vacuum (MB) | reclaimed | vacuum s | after full VACUUM (MB) | full VACUUM s |
|---|---|---|---|---|---|---|---|---|---|---|
| `inc_retention` | INCREMENTAL | retention_scatter | 2,016.2 | 2,016.2 | 156,755 | 1,373.3 | 31.9% | 4.902 | 1,152.9 | 5.82 |
| `inc_epoch` | INCREMENTAL | epoch_contiguous | 2,016.2 | 2,016.2 | 164,586 | 1,341.2 | 33.5% | 3.905 | 1,153.3 | 7.7 |
| `none_retention` | NONE | retention_scatter | 2,013.7 | 2,013.7 | 156,755 | 2,013.7 | 0.0% | 0.0 | 1,151.5 | 6.77 |

- `inc_retention` staged vacuum: 79 stages, max stage 0.1035s, mean stage 0.062s, freelist remaining 0.
- `inc_retention` `PRAGMA secure_delete` on a fresh default connection: 0 (persisted auto_vacuum: 2).
- `inc_epoch` staged vacuum: 83 stages, max stage 0.0571s, mean stage 0.047s, freelist remaining 0.
- `inc_epoch` `PRAGMA secure_delete` on a fresh default connection: 0 (persisted auto_vacuum: 2).
- `none_retention` incremental_vacuum on auto_vacuum=NONE: raised=None, freelist 156,755 -> 156,755, file 2,013.7 -> 2,013.7 MB in 0.0s.
- `none_retention` `PRAGMA secure_delete` on a fresh default connection: 0 (persisted auto_vacuum: 0).

### FTS5 sidecar

2,033 versions / 176,871 documents, automerge disabled during load, page-limited merge budget 64 pages.

| step | file (MB) | symbols_fts segids | symbols_trigram segids | freelist |
|---|---|---|---|---|
| built | 83.1 | 23 | 23 | - |
| after DELETE of 70,818 docs | 83.1 | 23 | 23 | 3,873 |
| after page-limited merge | 83.1 | 2 | 2 | 6,529 |
| after incremental_vacuum | 56.3 | - | - | 0 |

- merge rounds: 58 total, 56 did work; 0.251s total, max round 0.0148s, mean round 0.0043s.
- `optimize` control on an identical clone: one call, 0.215s, 1 segid left, final file 56.2 MB.
- FTS5 config after enabling secure-delete: {'automerge': 0, 'secure-delete': 1, 'version': 4} (was {'version': 4}).

### secure-delete matrix (sentinel byte scan of the file)

| FTS5 `secure-delete` | core `secure_delete` | hits before | after DELETE | after merge | after vacuum | fts config version |
|---|---|---|---|---|---|---|
| False | False | 3 | 4 | 4 | 4 | 4 |
| True | False | 3 | 2 | 2 | 2 | 5 |
| False | True | 2 | 2 | 2 | 2 | 4 |
| True | True | 2 | 0 | 0 | 0 | 5 |

## 2. Transaction granularity

2,000 file versions per import = 1,388,000 rows, WAL journal mode, 3 SIGKILL trials per mode, chunk size 100 versions, kill seed 20260806.

| mode | sync | autockpt pages | commits | rows/s | clean s | WAL peak (MB) | final db (MB) | reusable after SIGKILL (min/mean/max) | reuse efficiency | truncated after resume | quick_check |
|---|---|---|---|---|---|---|---|---|---|---|---|
| `single` | NORMAL | 1,000 | 1 | 93,360 | 14.867 | 664.6 | 660.5 | 0 / 0.0 / 0 | 0% | 0 | ok |
| `per_chunk` | NORMAL | 1,000 | 21 | 70,121 | 19.794 | 142.0 | 660.5 | 1400 / 1500.0 / 1600 | 100% | 0 | ok |
| `per_version` | NORMAL | 1,000 | 2,000 | 17,589 | 78.912 | 8.6 | 660.5 | 1221 / 1414.3 / 1612 | 100% | 0 | ok |
| `per_version_nomarker` | NORMAL | 1,000 | 4,000 | 18,524 | 74.931 | 8.4 | 660.5 | 1130 / 1372.0 / 1526 | 100% | 1 | ok |
| `per_version_wal_headroom` | NORMAL | 8,000 | 2,000 | 29,694 | 46.744 | 38.5 | 660.5 | 656 / 933.3 / 1187 | 100% | 0 | ok |
| `per_version_sync_full` | FULL | 1,000 | 2,000 | 19,855 | 69.907 | 8.6 | 660.5 | 918 / 1144.0 / 1498 | 100% | 0 | ok |

Reuse efficiency = reusable versions / versions the importer had time to write before the kill (kill fraction x total). It normalises away the fact that a faster mode reaches a smaller fraction of the import in the same wall-clock slice.

### per-trial detail

| mode | trial | kill at | versions in flight | marked complete | reusable | reuse efficiency | truncated | orphan child rows | resume skipped | resume imported | final truncated |
|---|---|---|---|---|---|---|---|---|---|---|---|
| `single` | 1 | 42% | 838 | 0 | 0 | 0% | 0 | 0 | 0 | 2000 | 0 |
| `single` | 2 | 45% | 898 | 0 | 0 | 0% | 0 | 0 | 0 | 2000 | 0 |
| `single` | 3 | 22% | 430 | 0 | 0 | 0% | 0 | 0 | 0 | 2000 | 0 |
| `per_chunk` | 1 | 63% | 1256 | 1400 | 1400 | 100% | 0 | 0 | 1400 | 600 | 0 |
| `per_chunk` | 2 | 74% | 1490 | 1600 | 1600 | 100% | 0 | 0 | 1600 | 400 | 0 |
| `per_chunk` | 3 | 66% | 1328 | 1500 | 1500 | 100% | 0 | 0 | 1500 | 500 | 0 |
| `per_version` | 1 | 55% | 1102 | 1221 | 1221 | 100% | 0 | 0 | 1221 | 779 | 0 |
| `per_version` | 2 | 74% | 1486 | 1612 | 1612 | 100% | 0 | 0 | 1612 | 388 | 0 |
| `per_version` | 3 | 60% | 1208 | 1410 | 1410 | 100% | 0 | 0 | 1410 | 590 | 0 |
| `per_version_nomarker` | 1 | 72% | 1432 | 1460 | 1460 | 100% | 0 | 0 | 1460 | 540 | 0 |
| `per_version_nomarker` | 2 | 74% | 1486 | 1526 | 1526 | 100% | 0 | 0 | 1526 | 474 | 0 |
| `per_version_nomarker` | 3 | 45% | 908 | 1131 | 1130 | 100% | 1 | 0 | 1131 | 869 | 1 |
| `per_version_wal_headroom` | 1 | 52% | 1034 | 1187 | 1187 | 100% | 0 | 0 | 1187 | 813 | 0 |
| `per_version_wal_headroom` | 2 | 36% | 720 | 957 | 957 | 100% | 0 | 0 | 957 | 1043 | 0 |
| `per_version_wal_headroom` | 3 | 23% | 452 | 656 | 656 | 100% | 0 | 0 | 656 | 1344 | 0 |
| `per_version_sync_full` | 1 | 40% | 796 | 918 | 918 | 100% | 0 | 0 | 918 | 1082 | 0 |
| `per_version_sync_full` | 2 | 68% | 1354 | 1498 | 1498 | 100% | 0 | 0 | 1498 | 502 | 0 |
| `per_version_sync_full` | 3 | 42% | 850 | 1016 | 1016 | 100% | 0 | 0 | 1016 | 984 | 0 |

## 3. Promotion capacity

2,500 file versions per generation, 5 generations per path, retention drops the 2 oldest.

| arm | old gen (MB) | new gen (MB) | sidecars (MB) | WAL/temp peak (MB) | reader-retained (MB) | formula peak (MB) | measured peak (MB) | delta |
|---|---|---|---|---|---|---|---|---|
| `no_reader` | 825.8 | 825.8 | 200.8 | 117.0 | 0.0 | 1,969.4 | 1,968.8 | -0.03% |
| `pinned_reader` | 825.8 | 825.8 | 200.8 | 117.0 | 926.2 | 2,895.6 | 2,895.0 | -0.02% |
| `retention_first` | 562.5 | 495.2 | 162.0 | 466.0 | 0.0 | 1,685.7 | 1,393.2 | -17.35% |

- `no_reader`: baseline family 926.2 MB, peak 1,968.8 MB (2.13x baseline), at-promote 1,852.4 MB, after release 926.2 MB, 658 samples over 37.1s.
- `pinned_reader`: baseline family 1,852.5 MB, peak 2,895.0 MB (1.56x baseline), at-promote 2,778.7 MB, after release 926.2 MB, 655 samples over 37.12s.
- `retention_first`: baseline family 926.2 MB, peak 1,393.2 MB (1.50x baseline), at-promote 1,219.7 MB, after release 556.8 MB, 411 samples over 23.22s.
  - retention sweep first: 825.8 -> 562.5 MB (4.16s delete + 1.2s vacuum).

## 4. Projection to dotnet/runtime scale

Live Miller artifact: 380,720 identifiers in 808.8 MB. Plan's dotnet/runtime benchmark: 12,860,000 identifiers, 21.900 GB per worktree. Identifier ratio 33.8x, byte ratio 27.1x.

| measurement | measured at | synthetic identifiers | multiplier to 12.86M | projected |
|---|---|---|---|---|
| staged incremental_vacuum, whole freelist | 4.902s reclaiming 642.9 MB | 1,640,900 | 7.8x | ~38s reclaiming ~5.038 GB |
| one staged vacuum step (2,000 pages) | max 0.1035s | 1,640,900 | 1x (page-bounded) | max 0.1035s -- independent of store size |
| full VACUUM of the same store | 5.82s, peak needs ~2x 1,373.3 MB | 1,640,900 | 7.8x | ~46s, peak needs ~21.526 GB |
| cold import, `single` | 14.867s, WAL peak 664.6 MB | 538,000 | 23.9x | ~5.9 min, WAL peak ~15.886 GB |
| cold import, `per_chunk` | 19.794s, WAL peak 142.0 MB | 538,000 | 23.9x | ~7.9 min, WAL peak ~142.0 MB (bounded by the commit unit, not the import) |
| cold import, `per_version` | 78.912s, WAL peak 8.6 MB | 538,000 | 23.9x | ~31.4 min, WAL peak ~8.6 MB (bounded by the commit unit, not the import) |
| cold import, `per_version_nomarker` | 74.931s, WAL peak 8.4 MB | 538,000 | 23.9x | ~29.9 min, WAL peak ~8.4 MB (bounded by the commit unit, not the import) |
| cold import, `per_version_wal_headroom` | 46.744s, WAL peak 38.5 MB | 538,000 | 23.9x | ~18.6 min, WAL peak ~38.5 MB (bounded by the commit unit, not the import) |
| cold import, `per_version_sync_full` | 69.907s, WAL peak 8.6 MB | 538,000 | 23.9x | ~27.9 min, WAL peak ~8.6 MB (bounded by the commit unit, not the import) |
| promotion peak, `no_reader` | 1,968.8 MB | 672,500 | 19.1x | ~37.649 GB |
| promotion peak, `pinned_reader` | 2,895.0 MB | 672,500 | 19.1x | ~55.361 GB |
| promotion peak, `retention_first` | 1,393.2 MB | 672,500 | 19.1x | ~26.641 GB |

The WAL projection is the load-bearing one: `single` scales its WAL peak with the whole import, every per-commit-unit mode does not.

