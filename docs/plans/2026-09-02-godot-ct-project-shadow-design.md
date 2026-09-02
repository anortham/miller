# Godot/GUT continuous-testing project-shadow design

- Status: accepted
- Date: 2026-09-02
- Scope: Task 8 of the JVM, Ruby, PHP, and GDScript CT provider plan

## Problem

Task 8 originally required GUT to run from the selected Godot project while suppressing editor
imports. Godot 4 has no command-line switch that suppresses ordinary project imports. Its documented
import operation writes the project cache under `res://.godot/`, and asset import metadata may be
written beside project assets. GUT and project code can also write through `user://`. Running either
operation against the user's project root or normal home directories would therefore violate Miller's
rule that continuous-test providers write only under supervised CT paths.

GUT still requires a real Godot project root. Its supported command-line surface runs
`addons/gut/gut_cmdln.gd` with `-s` and `--path <project-root>`, accepts exact script paths through one
`-gtest=<comma-delimited-res-paths>` argument, and writes JUnit XML through
`-gjunit_xml_file=<path>`. Miller uses GUT's exact `tests` config array instead of `-gtest` so large
focused selections are not bounded by the Windows command-line limit.

## Decision

Miller will run Godot imports and GUT only from a persistent project-stable mirror of the directory
containing the selected `project.godot`. The mirror and a separate Godot home directory will live
inside the project's supervised CT cache. Source reconciliation will preserve Godot's mirror-owned
`.godot` cache so unchanged runs stay warm. A bounded `--import` phase prepares that cache after a
source change or when the cache is absent; an unchanged warm run skips the import phase.

The hardened filesystem algorithm currently inside `SbtWorkspaceShadow` will move into one shared
internal `CtWorkspaceMirror` module. `SbtWorkspaceShadow` remains as a thin sbt policy adapter with its
existing result contract. A new `GodotProjectShadow` adapter supplies the Godot policy. No public API,
MCP tool, CT database schema, case identity, or provider interface changes.

## Approaches considered

### Shared internal mirror — selected

Move the proven reconciliation, path containment, link safety, manifest, metadata, path-budget, and
size-measurement behavior into a provider-shared internal module. Keep provider-specific exclusions,
build-owned paths, cache names, and diagnostics in small policy adapters.

This changes a security-sensitive internal boundary, but a second real consumer now proves the seam.
It avoids maintaining two large copies of the same filesystem safety rules and keeps fixes consistent
across sbt and Godot.

### Godot-specific copy — rejected

Copying the roughly 800-line sbt implementation into `Providers/Godot` would isolate sbt from the
refactor. It would also duplicate path traversal, symlink, special-file, manifest ownership, metadata,
and Windows path rules. Those rules already needed multiple review rounds. Future safety fixes could
drift between providers, so the smaller immediate blast radius does not justify the long-term risk.

### Per-generation full copy — rejected

A fresh copy for every discovery and run is simpler, but it forces repeated full project copies and
Godot imports. It defeats the requested performance gate and makes unchanged automatic CT work
materially slower.

### Run in the source tree — rejected

Godot has no supported import-suppression switch. Ignoring the writes, relying on `.gitignore`, or
deleting generated source-tree files afterward would not satisfy Miller's source-immutability
contract.

## Architecture quality

**Affected modules:** `Providers/Shared` gains the mirror engine; `Providers/Jvm` keeps an sbt adapter;
`Providers/Godot` gains the Godot adapter, GUT tooling, and provider. Factory, inventory, framework
support, language-family mapping, and test-tool support gain the existing Task 8 registrations.

**Caller-facing interface:** CT callers continue to use `IContinuousTestProvider`. The shared mirror
has two internal entry points: synchronize a mirror and measure a candidate without following reparse
points. Provider adapters translate their existing workspace and project facts into a closed policy
value and return provider-specific paths. The policy selects one of two closed integrity modes; callers
cannot inject traversal or copy behavior.

**Depth/locality check:** Filesystem traversal, validation, reconciliation, and manifest ownership stay
inside the shared mirror. Provider adapters own only names and mapping. The Godot provider owns project
detection, script discovery, argv, execution, result attribution, and report copying.

**Test surface:** Shared filesystem invariants are tested through `CtWorkspaceMirror.Sync`; sbt adapter
tests preserve the already-approved sbt contract; Godot behavior is tested through
`GodotTestProvider` and `GodotProjectShadow`. Factory and inventory tests exercise the same interfaces
used by production callers.

**Seams/adapters:** The mirror policy seam has two production adapters, sbt and Godot. It remains
internal and uses the real filesystem; there is no generic filesystem abstraction or public extension
point.

**Rejected shortcuts:** duplicated reconciler, per-run cold copy, source-tree execution, global Godot
home directories, hardlinks, platform-specific junctions, and a configurable plugin-style policy
interface.

**Architecture risk:** high. The implementation moves security-sensitive sbt code and adds a provider
that executes project code. It requires the full existing sbt shadow suite, new Godot shadow tests,
provider tests, Scale coverage, Release build, Windows fast suite, and performance regression checks.

## Shared mirror contract

`CtWorkspaceMirror` owns synchronous `Sync` and `MeasureCandidateBytes` operations because CT provider
work already runs under the operation lease and the mirror also serializes concurrent in-process
callers by candidate root. The input to `Sync` is a source root plus a closed
`CtWorkspaceMirrorPolicy` value. The result contains the candidate root, mirror root, and bounded
synchronization metrics already reported by the sbt Scale test: scanned, copied, updated, deleted,
hash fallbacks, copied bytes, candidate bytes, and elapsed time. It additionally reports files and
bytes hashed, a metadata-only source digest, and whether source-owned state changed.
`MeasureCandidateBytes` reuses the same no-follow traversal after a provider process has changed
build-owned state.

The policy contains only data:

- diagnostic/provider name;
- project-stable cache name;
- mirror-directory name;
- source entry names excluded at every depth;
- destination entry names owned by the build at every depth;
- whether the isolated unborn Git barrier is required; and
- integrity mode: `StrictHash` or `MetadataFastPath`.

It does not accept callbacks, an `IFileSystem`, arbitrary strategy objects, or caller-defined copy
behavior. The small closed shape keeps the shared safety algorithm authoritative.

Every profile inherits common rules:

- reject case-colliding paths before writing;
- reject escaping, external, looping, or ancestor-pointing links;
- reject special files and unsupported link types;
- copy regular files rather than hardlinking user content;
- preserve executable and writable metadata consistently with the existing sbt contract;
- use ordinary canonical paths for Miller-owned and child-process operations;
- refuse a mirrored path that exceeds the documented build-root bound;
- write the ownership manifest atomically only after a stable source snapshot;
- validate every manifest path before deletion;
- never traverse reparse points while measuring or deleting; and
- create build-owned directories without adding them to the source-owned manifest.

`StrictHash` preserves the existing sbt behavior: it hashes source and mirror files on a warm sync and
repairs destination content changed without a metadata change. `MetadataFastPath` compares path, kind,
length, last-write time, mode, and read-only state against the prior manifest and destination. It hashes
and verifies files only when copying them. Godot uses the metadata fast path so a warm project with
large binary assets does not reread every byte before each run. A same-length source or destination
mutation whose timestamp and permissions are deliberately restored is outside the Godot v1 fast-path
contract; ordinary editor, generator, and Godot writes update the compared metadata. The deterministic
metrics make this trade visible: an unchanged Godot sync must report zero copied and zero hashed bytes.

## Provider policies

### sbt adapter

`SbtWorkspaceShadow` keeps cache names `sbt-workspace` and `sbt-deps`, mirror directory `build`, and its
existing result type. Its mirror policy excludes `.git`, `.miller`, and `target` at every depth. The
mirror owns `.git` and `target` at every depth and creates the isolated Git barrier. Dependency-cache
creation and reporting remain sbt-specific and outside the shared mirror engine. The adapter creates
`sbt-deps` only after mirror enumeration, validation, and source-budget checks succeed, preserving the
existing refusal behavior.

The refactor must be behavior-preserving: the existing 31-test shadow contract, backend command shape,
warm zero-copy metric, and candidate split remain unchanged.

### Godot adapter

`GodotProjectShadow` uses cache name `godot-workspace` and mirror directory `project`. The source root
is the directory containing the selected `project.godot`; Miller does not copy the outer workspace or
another Godot project.

The policy excludes `.git`, `.miller`, `.godot`, and `.miller-gut-results` at every depth. The mirror
owns `.git`, `.godot`, and `.miller-gut-results` at every depth and creates the isolated Git barrier.
Committed `<asset>.import` files, `.gutconfig.json`, project assets, addons, untracked files, executable
scripts, and contained relative links remain source-owned and are mirrored normally. If Godot changes a
source-owned file in the mirror, the next synchronization restores the source version.

The result maps the original `project.godot` and every selected source-relative test script to the
equivalent path under the mirror. Mapping is containment-checked and refuses paths outside the selected
project root. When the build output root is inside the selected Godot project, the adapter writes a
`.gdignore` at the build output root before materializing the mirror so the user's editor does not scan
the nested CT project.

## Cache budget and janitor behavior

The Godot source mirror and its `.godot` import cache form one `godot-workspace` janitor candidate. A
second `godot-home` candidate contains all process-global data, configuration, user data, and temporary
files. Godot fixes `.godot` under the project root, and splitting it with symlinks or Windows junctions
would add a platform privilege requirement and weaken containment. The portable v1 design therefore
keeps the project and import cache in one candidate while allowing user-home data to be reaped
independently.

Before writing, synchronization measures the source file set against the existing 2 GiB workspace
budget and refuses a source tree that cannot fit by itself. After synchronization and after each Godot
process, Miller measures and reports both candidates. If the project candidate exceeds the workspace
budget after import, the provider atomically writes a small `godot-workspace.over-budget.json` marker
beside the cache directory containing the metadata source digest and measured size, then fails with an
actionable limit message. If the janitor reclaims the candidate, later operations with the same source
digest refuse before recopying or reimporting it. A source metadata change clears the marker and allows
one new attempt. This avoids a cold-copy/import loop while preserving the existing budget.

The adapter atomically replaces a build-owned `.last-used` marker at both candidate roots after every
Godot process so directory activity is visible to the janitor. The markers are not part of the source
manifest.

## Project detection and framework support

Inventory treats `project.godot` as a candidate file only when one supported addon marker exists beside
it:

- `addons/gut/plugin.cfg` produces runnable framework `gut` and registration key
  `ct-provider:godot`;
- `addons/gdUnit4/plugin.cfg` produces recognized-but-refused framework `gdunit4` with remedy
  `run it with its own runner; CT support is planned`;
- a bare `project.godot` is ignored.

Runnable GUT support also requires `config_version=5` in `project.godot`, GUT major version 9 from
`addons/gut/plugin.cfg`, and a Godot executable whose bounded `--version` probe reports major version
4. Godot 3/GUT 7 projects are recognized but refused with the Godot 4/GUT 9 remedy; missing or
unparseable version evidence also refuses enable rather than guessing.

When both addon markers exist, `gut` is runnable and `gdunit4` remains visible as refused inventory
evidence; it does not suppress the runnable GUT project. Reversing a prior project opt-out remains
governed by the existing framework-support rules.

`.gd` maps to language family `gdscript`. No other language joins that family in this change.

## GUT case identity and discovery

GUT v1 case granularity is one project-relative test script. The stable case name is the normalized
`res://` path, including the `.gd` suffix. Discovery never uses a method-name or substring identity.

Discovery runs from the synchronized mirror and reads the documented `.gutconfig.json` fields `dirs`,
`tests`, `include_subdirs`, `prefix`, and `suffix`. The parser accepts a strict JSON object and the
trailing comma used by GUT's published sample. Missing values use Miller's explicit v1 defaults:
`dirs=[]`, `tests=[]`, `include_subdirs=false`, `prefix="test_"`, and `suffix=".gd"`; the derived config
writes every value, so runner defaults cannot change the executed inventory. The `tests` list
contributes exact scripts; every configured directory contributes matching files at its configured
recursion depth. Duplicate paths collapse to one case. Invalid JSON, unsupported value types, escaping
paths, missing configured paths, ambiguous case, case collisions, and non-file test paths fail closed.

Discovery is side-effect free with respect to the user project and starts no Godot process. The
produced inventory is derived only from contained mirrored scripts and is stable across warm
synchronizations. A project with no effective configured scripts or directories has zero discovered
cases rather than an invented conventional test directory.

## Command and result flow

Tool resolution checks a non-empty `GODOT` environment value first, then resolves `godot`,
`godot4`, and platform executable variants from `PATH` in a deterministic documented order. The
selected executable is version-probed with the existing bounded process runner. Missing or unsupported
Godot/GUT versions refuse enable without writing opt-in state.

Every Godot process receives an overlay that keeps all writable global paths inside the
project-stable `godot-home` candidate. Unix sets `HOME`, `XDG_DATA_HOME`, `XDG_CONFIG_HOME`,
`XDG_CACHE_HOME`, `TMPDIR`, `TEMP`, and `TMP`. Windows sets `USERPROFILE`, `APPDATA`, `LOCALAPPDATA`,
`HOME`, `TEMP`, and `TMP`. macOS uses the isolated `HOME` for its Library paths and also receives the
XDG keys. Each value is an absolute contained child of the CT build root. Miller preserves unrelated
environment values, including `PATH`; it never inherits a writable Godot data, config, cache, or temp
path from the user's session.

Discovery follows this flow and starts no Godot process:

1. Synchronize the selected Godot project into the project-stable mirror.
2. Read and validate the mirrored GUT config.
3. Enumerate and return the contained script cases.

Each run follows this flow:

1. Synchronize the selected Godot project into the project-stable mirror.
2. When source-owned state changed or `.godot` is absent, start one bounded
   `godot --headless --path <mirror> --import` process with the isolated environment. Require exit 0,
   record import time, and reject an over-budget candidate before GUT starts.
3. Clear only the mirror-owned `.miller-gut-results` directory.
4. Write a mirror-owned derived GUT config that preserves user settings but replaces `dirs`, `tests`,
   selection, exit, colors, and JUnit output with Miller-owned values. The `tests` array is the exact
   freshly discovered focused selection or whole-suite inventory; using the file avoids platform
   command-line limits.
5. Start one bounded Godot process with the mirror as both cwd and `--path`.
6. Pass `--headless`, `-s`, `addons/gut/gut_cmdln.gd`, `-gexit`, disabled colors,
   `-gconfig=res://.miller-gut-results/miller.gutconfig.json`, and a contained
   `res://.miller-gut-results/<phase>.xml` JUnit path.
7. Require the expected contained JUnit file, copy it atomically into the immutable generation result
   directory, and parse only the copy with `JUnitXmlResultParser`.
8. Attribute every test row to exactly one selected script. Missing, unexpected, duplicate,
   malformed, or unattributed evidence fails closed.
9. Touch both candidate activity markers, measure both candidates without following reparse points, and
   report mirror/cache/run metrics.

An empty focused selection throws before any process starts. An empty whole-suite inventory returns no
results without starting a process. Focused and whole-suite modes therefore use the same exact script
identity and never depend on GUT's substring selectors or the process command-line length.

Godot exit `0` means GUT reported no failures and exit `1` is accepted only when valid JUnit evidence
contains failures. Other exit codes, timeouts, silence kills, or missing reports fail the run. Pending
GUT tests map through the existing skipped verdict semantics.

The provider never reads JUnit through a symlink or reparse point and never accepts a report outside the
mirror-owned result directory.

## Error handling

All safety and attribution failures are explicit provider failures. Error messages name the project or
relative entry, never copy file contents, environment values, or process secrets. A failed or cancelled
sync does not publish a new manifest. A failed run does not reuse a stale report. Partial result copies
are written to temporary files and atomically promoted only after validation.

The existing CT process-silence timeout, cancellation, operation lease, run commit, failure journal,
and automatic-run pause semantics remain authoritative. This design adds no retry timer or kill policy.

## Verification

### Shared mirror

- Move the complete sbt filesystem safety suite to the shared mirror interface or keep equivalent
  coverage through the sbt adapter; do not weaken or delete any accepted scenario.
- Add policy tests proving sbt and Godot exclusions/build-owned paths diverge only as specified.
- Prove the sbt adapter retains its existing paths, dependency candidate, Git barrier, metrics, warm
  zero-copy result, and no-follow behavior.

### Godot fast tests

- Initial, warm, changed, deleted, cancelled, concurrent, collision, traversal, link, special-file,
  metadata, path-length, and manifest-recovery mirror cases.
- Inventory: GUT runnable, gdUnit4 refused with the exact remedy, both addons represented correctly,
  and bare Godot ignored.
- Tool resolution: `GODOT` before deterministic `PATH` names, project/GUT/executable version floors,
  and missing or malformed version evidence.
- Discovery: default and configured script sets, normalized `res://` identities, collision and escape
  refusal.
- Commands: isolated import phase, warm import skip, headless mirror cwd/path, derived exact-script
  config for focused and whole-suite runs, empty selection, report path, and no original project root
  or inherited writable home/temp path in argv or environment.
- Results: green, red, skipped, exit-code/report disagreement, missing, malformed, duplicate,
  unexpected, and symlinked report evidence.
- Factory, framework support, language-family, Scale-trait convention, and whole-suite contract tests.

### Scale and performance

- A Scale fixture contains `project.godot`, a real GUT 9 addon, two test scripts including an inner
  class, one `class_name` dependency, and one imported asset.
- The release gate must execute this smoke on a real Godot 4/GUT 9 installation; a missing runtime is
  still an honest test skip during ordinary development but does not satisfy release verification.
- The first run proves the isolated import phase, exact one-script selection, whole-suite execution,
  JUnit attribution, inner-class attribution, and source-tree immutability. It records every file path
  changed outside the disposable fixture and must find none outside the supervised CT root.
- It records cold and warm mirror sync time, copied and hashed entries/bytes, project/home candidate
  bytes, import time, GUT process time, and report-copy time.
- A warm no-change sync must copy and hash zero entries and bytes and skip import. A fixed large binary
  asset guards the metadata fast path. Performance comparison uses the existing branch baseline
  protocol; any material regression blocks release.

### Branch gates

- Focused shared-mirror, sbt backend, Godot provider, inventory, factory, framework, language-family,
  convention, and whole-suite tests.
- One bare fast suite after the complete task lands.
- `dotnet build Miller.slnx -c Release` with zero warnings and errors.
- `scripts/test.sh scale`, including a required real Godot/GUT smoke for release; other unavailable
  toolchains retain their documented honest skips.
- Required local Windows fast suite through `win-test`.
- Existing CT performance regression suite and the new cold/warm Godot measurements.

## Documentation and durable decision

Task 8 implementation will update the parent plan to replace the false import-suppression requirement
with this project-shadow contract. Task 9 will document the Godot/GUT boundary, Godot 4/GUT 9 floor,
exact per-script selection, source immutability, cache-budget limit, and gdUnit4 refusal.

Because this introduces a shared internal filesystem seam and reopens the provider-local sbt placement,
the implementation will add an ADR recording the shared mirror decision and its two policy adapters.

## External evidence

- [Godot 4.4 command-line reference](https://docs.godotengine.org/en/4.4/tutorials/editor/command_line_tutorial.html):
  `--import` performs editor import and exits; `--headless`, `--path`, and `-s` are supported process
  surfaces.
- [Godot import-process reference](https://docs.godotengine.org/en/stable/tutorials/assets_pipeline/import_process.html):
  imported artifacts live in `.godot/imported`, with `<asset>.import` metadata beside source assets.
- [Godot 4.4 data-path reference](https://docs.godotengine.org/en/4.4/tutorials/io/data_paths.html):
  `user://`, editor data, settings, and cache normally resolve into user-global platform directories;
  XDG overrides are supported on Linux/BSD.
- [GUT 9.3.1 command-line reference](https://gut.readthedocs.io/en/9.3.1/Command-Line.html):
  `-gconfig`, exact script lists, `-gexit`, disabled colors, JUnit export, and exit 0/1 behavior.
- [GUT version matrix](https://github.com/bitwes/Gut): GUT 9 supports Godot 4; GUT 7 supports
  Godot 3.

The required real Scale run remains the acceptance evidence for actual import writes, config parsing,
inner-class JUnit attribution, and exit/report agreement. Documentation-derived behavior alone does not
close those runtime checks.

## Acceptance criteria

- [x] User approves this written design.
- [ ] A shared internal mirror preserves every accepted sbt shadow invariant and metric.
- [ ] GUT discovery and execution operate only inside the supervised project mirror.
- [ ] The user's Godot project retains identical tree shape, file bytes, permissions, and last-write
      metadata in fast and Scale tests.
- [ ] Focused and whole-suite runs select exact `res://` scripts through the derived config without a
      command-line-length bound.
- [ ] GUT JUnit results are contained, copied atomically, and attributed without ambiguity.
- [ ] Inventory, refusal, factory, language-family, whole-suite, and Scale conventions are complete.
- [ ] Warm no-change synchronization copies and hashes zero files and bytes, skips import, and passes
      the performance gate.
- [ ] All focused, fast, Release, Scale, Windows, security, and performance release gates pass.
