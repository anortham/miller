## A. Composite-key amplification (base data only, one view's worth of rows)

| object group | today single-key | composite + file_id kept | v4 composite (version_id replaces file_id) |
|---|---:|---:|---:|
| symbols | 55.6 MB | 56.5 MB (+1.6%) | 51.8 MB (-7.0%) |
| identifiers | 115.8 MB | 118.3 MB (+2.2%) | 103.4 MB (-10.7%) |
| reference_sites | 128.9 MB | 132.1 MB (+2.5%) | 113.8 MB (-11.7%) |
| resolution rows | 44.1 MB | 48.2 MB (+9.4%) | 48.2 MB (+9.4%) |
| files / file_versions | 0.4 MB | 0.5 MB (+29.9%) | 0.5 MB (+29.9%) |
| **total physical** | **497.3 MB** | **519.4 MB (+4.4%)** | **440.9 MB (-11.3%)** |

## B. Eight-view family store vs a single index

| configuration | physical bytes | x single index |
|---|---:|---:|
| single index today (single-key schema) | 497.3 MB | 1.000x |
| 8-view store, sampled divergence | 510.8 MB | 1.027x |
| 8-view store, p90 divergence every view | 622.5 MB | 1.252x |
| 8-view store + 2 retained history generations | 1274.7 MB | 2.563x |
| one dedicated copy of diverged view 8 | 497.3 MB | 1.000x |
| 8 dedicated copies (view1 + 7x view8, measured) | 3978.3 MB | 8.000x |

**GATE (8 views at sampled task-branch divergence vs 1.2x): 1.027x -> PASS**

Stress configuration (every view at the p90 divergence of the sampled history): 1.252x -> FAIL

### View divergence actually built

| view | target % | changed files | actual % | resolution delta rows |
|---:|---:|---:|---:|---:|
| 1 | 0.000 | 0 | 0.000 | 0 |
| 2 | 0.423 | 6 | 0.423 | 1030 |
| 3 | 0.565 | 8 | 0.565 | 1253 |
| 4 | 0.847 | 12 | 0.847 | 4249 |
| 5 | 1.200 | 17 | 1.200 | 4929 |
| 6 | 3.035 | 44 | 3.105 | 13130 |
| 7 | 3.317 | 47 | 3.317 | 13772 |
| 8 | 6.069 | 89 | 6.281 | 24401 |

Store rows: file_versions 1640, symbols 137863, identifiers 440830, reference_sites 554295, resolution base 380720, resolution deltas 62764, manifest entries 11336.

## C. Result-set equivalence (both visibility shapes vs the dedicated copy)

| view | query class | keys | rows compared | mismatches |
|---|---|---:|---:|---:|
| 1 | name_lookup | 300 | 43520 | 0 |
| 1 | file_symbols | 300 | 28685 | 0 |
| 1 | refs_by_symbol | 300 | 10085 | 0 |
| 8 | name_lookup | 300 | 43520 | 0 |
| 8 | file_symbols | 300 | 28685 | 0 |
| 8 | refs_by_symbol | 300 | 9918 | 0 |

## D. Read overhead per query class

### view 1 (base manifest, no divergence) — 15 interleaved passes, 300 keys per class, harness floor 0.78 us/query

| query class | shape | median ms/sweep | us/query | rows | vs dedicated | vs v4 no-visibility | VDBE steps |
|---|---|---:|---:|---:|---:|---:|---:|
| name_lookup | dedicated | 48.51 | 161.7 | 43520 | +0.0% | +0.0% | 351,200 |
| name_lookup | v4_novis | 48.74 | 162.5 | 43520 | +0.5% | +0.0% | 351,200 |
| name_lookup | manifest_join | 57.14 | 190.5 | 43520 | +17.8% | +17.2% | 689,100 |
| name_lookup | temp_vis | 49.66 | 165.6 | 43520 | +2.4% | +1.9% | 459,500 |
| file_symbols | dedicated | 24.89 | 83.0 | 28685 | +0.0% | +0.0% | 462,900 |
| file_symbols | v4_novis | 25.24 | 84.1 | 28685 | +1.4% | +0.0% | 462,900 |
| file_symbols | manifest_join | 24.78 | 82.6 | 28685 | -0.5% | -1.8% | 465,300 |
| file_symbols | temp_vis | 25.27 | 84.2 | 28685 | +1.5% | +0.1% | 527,900 |
| refs_by_symbol | dedicated | 32.20 | 107.3 | 10085 | +0.0% | +0.0% | 135,000 |
| refs_by_symbol | v4_novis | 29.70 | 99.0 | 10085 | -7.8% | +0.0% | 237,100 |
| refs_by_symbol | manifest_join | 34.17 | 113.9 | 10085 | +6.1% | +15.1% | 463,700 |
| refs_by_symbol | temp_vis | 32.25 | 107.5 | 10085 | +0.1% | +8.6% | 414,200 |

### view 8 (most diverged view) — 15 interleaved passes, 300 keys per class, harness floor 0.71 us/query

| query class | shape | median ms/sweep | us/query | rows | vs dedicated | vs v4 no-visibility | VDBE steps |
|---|---|---:|---:|---:|---:|---:|---:|
| name_lookup | dedicated | 47.48 | 158.3 | 43520 | +0.0% | +0.0% | 351,200 |
| name_lookup | v4_novis | 47.03 | 156.8 | 43520 | -0.9% | +0.0% | 351,200 |
| name_lookup | manifest_join | 55.36 | 184.5 | 43520 | +16.6% | +17.7% | 689,100 |
| name_lookup | temp_vis | 48.44 | 161.5 | 43520 | +2.0% | +3.0% | 459,500 |
| file_symbols | dedicated | 24.08 | 80.3 | 28685 | +0.0% | +0.0% | 462,900 |
| file_symbols | v4_novis | 23.78 | 79.3 | 28685 | -1.2% | +0.0% | 462,900 |
| file_symbols | manifest_join | 23.73 | 79.1 | 28685 | -1.4% | -0.2% | 465,300 |
| file_symbols | temp_vis | 24.12 | 80.4 | 28685 | +0.2% | +1.4% | 527,900 |
| refs_by_symbol | dedicated | 31.45 | 104.8 | 9918 | +0.0% | +0.0% | 132,800 |
| refs_by_symbol | v4_novis | 29.13 | 97.1 | 9918 | -7.4% | +0.0% | 233,200 |
| refs_by_symbol | manifest_join | 35.26 | 117.5 | 9918 | +12.1% | +21.0% | 462,300 |
| refs_by_symbol | temp_vis | 32.93 | 109.8 | 9918 | +4.7% | +13.0% | 410,300 |

### view 1, store inflated with retained history — 15 interleaved passes, 300 keys per class, harness floor 0.74 us/query

| query class | shape | median ms/sweep | us/query | rows | vs dedicated | vs v4 no-visibility | VDBE steps |
|---|---|---:|---:|---:|---:|---:|---:|
| name_lookup | dedicated | 48.19 | 160.6 | 43520 | +0.0% | +0.0% | 351,200 |
| name_lookup | v4_novis | 48.07 | 160.2 | 43520 | -0.2% | +0.0% | 351,200 |
| name_lookup | manifest_join | 68.96 | 229.9 | 43520 | +43.1% | +43.5% | 1,385,500 |
| name_lookup | temp_vis | 56.73 | 189.1 | 43520 | +17.7% | +18.0% | 894,700 |
| file_symbols | dedicated | 24.74 | 82.5 | 28685 | +0.0% | +0.0% | 462,900 |
| file_symbols | v4_novis | 25.00 | 83.3 | 28685 | +1.1% | +0.0% | 462,900 |
| file_symbols | manifest_join | 24.52 | 81.8 | 28685 | -0.9% | -1.9% | 465,300 |
| file_symbols | temp_vis | 30.49 | 101.6 | 28685 | +23.2% | +21.9% | 814,700 |
| refs_by_symbol | dedicated | 33.06 | 110.2 | 10085 | +0.0% | +0.0% | 135,000 |
| refs_by_symbol | v4_novis | 29.89 | 99.6 | 10085 | -9.6% | +0.0% | 237,100 |
| refs_by_symbol | manifest_join | 34.51 | 115.0 | 10085 | +4.4% | +15.5% | 463,700 |
| refs_by_symbol | temp_vis | 32.92 | 109.7 | 10085 | -0.4% | +10.1% | 414,200 |

