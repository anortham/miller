# Razorback SDD ledger — plan: docs/plans/2026-09-01-ct-providers-jvm-ruby-php-gdscript-implementation-plan.md

| Scope | Invariant | Command | Commit | Result | Time |
|---|---|---|---|---|---|
| baseline | Held release tree starts green before provider work | `MILLER_ALLOW_MISSING_JULIE_EXTRACT=1 MILLER_ALLOW_MISSING_SEMANTIC=1 dotnet test` | `43b2fa07` | 9,456 passed, 0 failed, 9 skipped | 2026-09-02 |

Task 1: fix round 1 (1 addressed, 0 open — explicit product support floors; commits 4c58678b..ac156ea7)
Task 1: complete (commits be1f8318..980e0f91, Lead inline review clean)
Task 2: complete (commit c87d7223, 16 focused tests passed, Lead inline review clean)
Task 3: fix round 1 (2 addressed, 0 open — linear result attribution and partial-run ownership; commits 56f1e405..3cbd984a)
Task 3: fix round 2 (1 addressed, 0 open — restored positive artifact-mapping coverage; commits 3cbd984a..60262e0c)
Task 3: complete (commits 56f1e405..60262e0c, Lead inline review clean)
Task 4: fix round 1 (2 addressed, 0 open — real PHPUnit listing schemas and exact filters; commits daf17b22..519e71c1)
Task 4: fix round 2 (2 addressed, 0 open — linear duplicate detection and malformed-container refusal; commits 519e71c1..0eccb751)
Task 4: fix round 3 (1 addressed, 0 open — workspace-relative typed PHP source identity; commits 0eccb751..927e6d0d)
Task 4: complete (commits daf17b22..927e6d0d, Lead inline review clean)
Task 5: fix round 1 (6 addressed, 0 open — bounded reports, whole-suite attribution, status and inventory hardening; commits fbe6ed7d..e71e7160)
Task 5: fix round 2 (1 addressed, 0 open — final Gradle output isolation and build-identity hashing; commits e71e7160..d7f2d1d3)
Task 5: complete (commits fbe6ed7d..d7f2d1d3, Lead inline review clean)
Task 6: fix round 1 (4 addressed, 0 open — Maven config preservation, whole-suite identity validation, bounded reports, compiler-aware Scale guard; commits 98e9b540..9ea26a8e)
Task 6: complete (commits 98e9b540..8262656e, 18 focused tests passed, Release build clean, Lead inline review clean)
Task 7: design blocker (official sbt 1.13.0 probe writes workspace target/ and project/target/ during build load despite cache and session-target redirection; runnable support needs an approved generation-owned source/build shadow or release-scope change)
Task 7: blocker resolved (approved build-root shadow design `2670cb7f`; executable child plan `1d65331b`)
Task 7: fix round 1 (child Task 1: 4 filesystem safety findings; commits 850e651d..b6d0d56c)
Task 7: fix round 2 (child Task 1: 3 remaining portability/safety findings; commits b6d0d56c..29cbefe8)
Task 7: fix round 3 (child Task 2: 7 command/identity/cache/report/evidence findings; commits 01d24827..c02c2d9e)
| affected-change | Completed sbt provider keeps the default fast suite green | `MILLER_ALLOW_MISSING_JULIE_EXTRACT=1 MILLER_ALLOW_MISSING_SEMANTIC=1 dotnet test` | c02c2d9e | PASS — 9,593 passed, 0 failed, 9 skipped | 2026-09-02T15:46:00Z |
Task 7: complete (child commits 1d65331b..c02c2d9e, 31 shadow + 19 backend + 7 factory + 1 convention focused tests, Release build clean, exact Scale skip recorded, Lead inline review clean)
Task 8: complete (child commits fa726bd7..a5519146, real Godot 4.7.2/GUT 9.7.1 Scale passed, Release build clean, Lead inline review clean)
Task 9: fix round 1 (1 addressed, 0 open — corrected F# project execution versus missing extractor-backed source-impact mapping; commits d3a997cc..206cab82)
Task 9: documentation slice complete (commits a5519146..206cab82, focused site test and diff check green, Lead scoped re-review clean; branch gate pending)
Release audit correction: fix round 1 (parity oracle now models same-parent overload fallback at 0.4 with self-exclusion; synthetic RED/GREEN and 33 Reader + 12 Graph + 106 Trace tests passed; commits 206cab82..48a83fa1)
Release audit correction: fix round 2 (self-exclusion mutation now covered by literal sibling/self assertions; 33 Reader tests passed; commits 48a83fa1..151ecace, Lead scoped re-review clean)
Release audit correction: preservation branch reconciled path-by-path and five unique memory files restored; producer retry benchmark isolation now copies `.miller/invariant.julieignore`; focused test passed; lead commit b92ec7a7
