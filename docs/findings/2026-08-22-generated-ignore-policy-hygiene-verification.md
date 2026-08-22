# Generated ignore-policy hygiene verification

Date: 2026-08-22
Platform: Linux
Branch: `plan/linux-dogfood-fixes`
Worktree: `/home/murphy/source/miller/.worktrees/linux-dogfood-fixes-plan`
Implementation HEAD at start of this packet: `98dbbb0a`

## Scope and outcome

The pinned `julie-extract` was exercised through the focused Scale fixture and the literal built Miller CLI.
Fresh plain roots now keep the checkout free of a generated `.julieignore`; Miller's deterministic
baseline/vendor policy is stored under the isolated Miller home and is consumed by full scan, single-file
update, watcher filtering, and real CLI onboarding/removal. Inherited linked-worktree policy, malformed
warning-only behavior, and user-authored root precedence remain covered by the live cases and takeover case.

The generated-policy WorktreeIgnore fixtures use roots shaped as `/tmp/miller-wt-ignore-<guid>`, an isolated
`<root>/miller-home` assigned through `MILLER_HOME`, and a generated policy at
`<root>/miller-home/.miller/ignore-policies/<workspace-id>.julieignore`. The CLI fixture uses
`/tmp/miller-cli-e2e-<guid>` with `/tmp/miller-cli-home-<guid>` passed to child processes as `MILLER_HOME`;
the entire CLI test collection also assigns that home to the parent process so its in-process
`JulieExtractRunner.Scan` cannot write to the live home. The collection disables parallelization, captures the
prior parent value, and restores it before cleanup. Both fixtures best-effort remove their temporary roots;
cleanup is not claimed as guaranteed. The focused tests used no user repository or live `~/.miller` state. The generated
policy is deterministic by workspace id and contains the baseline `*.log` rule plus the detected `libs/` vendor
rule; the tests validate its path and content rather than retaining a temporary hash.

## Focused live evidence before suite-home correction

Focused commands:

```text
dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~WorktreeIgnorePropagationScaleTests" --no-restore
dotnet test tests/Miller.Tests/Miller.Tests.csproj --filter "FullyQualifiedName~WorktreeIgnorePropagationScaleTests|FullyQualifiedName~CliBinarySubprocessTests" --no-restore
```

The direct-runner command produced 12 passed, 0 failed, 0 skipped. The combined correction command produced
15 passed, 0 failed, 0 skipped. These are historical pre-module-correction focused counts, not final branch
evidence: the CLI class was not rerun after the suite-wide home initializer was added.

The direct-runner WorktreeIgnore cases prove:

- A staged/committed plain Git fixture is clean before registration and remains clean after registration. After
  the scan its exact status is `?? .miller/`; no root `.julieignore` is present. The generated `libs/` vendor
  file is absent from the artifact while ordinary source remains.
- Rewriting the excluded vendor file and calling the real `JulieExtractRunner.Update` returns `unsupported`
  and does not reinsert the symbol. `WatchPathFilter` and `WorkspaceIgnorePolicy` reject the same path using
  the generated policy.
- Creating a user root `.julieignore` keeps its bytes unchanged, removes the generated policy from the scan
  ignore-file list, lets the formerly generated-excluded vendor symbol index, and makes watcher matching agree.
- Removing the registered workspace by path deletes the exact isolated global policy and the Miller-owned
  `.miller` state while leaving the user root policy byte-identical.
- Existing linked-worktree scan/update, inherited-copy, malformed warning-only, unreadable-source, nested pool,
  and user-root cases remain green in the same 12-test class.

The literal CLI case proves:

- A temporary Git repository is initialized and committed clean before `workspace open`; the real built
  subprocess then creates the index, leaves no root `.julieignore`, materializes the exact generated policy
  under its isolated Miller home, leaves the expected Git status `?? .miller/`, and answers a valid
  `workspace status --json` whose registered workspace id and root match the fixture.
- The same real CLI process pattern supports search, reopen, and full rebuild, and `workspace remove --path`
  removes the generated global policy and root `.miller` while leaving the root without `.julieignore`.

No extractor schema or producer change is involved; this is consumer-side policy storage and propagation.

## Live-home recovery and final delta

Before recovery, the exact live parent `/home/murphy/.miller/ignore-policies` contained only the four known
Task 3 policy files. Each was validated as a regular file with the Miller-generated header and SHA-256
`2c90a9dae11050e3864a602d92d3f65ad68beba242ebd1ba21ea9f8730167e28`, then moved recoverably to:

The invalidated full-suite run also produced exactly 84 recovered files matching the prefix
`miller-ignore-full-suite-recovery-20260822-<ordinal>-<sha256>.julieignore`. The sorted filename inventory had
84 entries and SHA-256 `28b31900c9b24aad52f89c3b90306906f0e6d1e80b411eed3c8390b240956196` (one newline per filename).

- `/home/murphy/.local/share/Trash/files/miller-ignore-policy-recovery-20260822-1-70c90abe7882a8473bb17442324a27008137247cf19abbf12285a78ed60681f2.julieignore`
- `/home/murphy/.local/share/Trash/files/miller-ignore-policy-recovery-20260822-2-05dbc2fa1a2902ff12cf1cc72d1fba4044455aa6e5a6a6aebb458707804ea38a.julieignore`
- `/home/murphy/.local/share/Trash/files/miller-ignore-policy-recovery-20260822-3-2aeee26cfb37ee4285b4c06a1d708db3a81ef1188a27ae769a56fc2244db192c.julieignore`
- `/home/murphy/.local/share/Trash/files/miller-ignore-policy-recovery-20260822-4-c601f957253085d26a6eb8274521d8d855f19ed008d99ee9ac30e222d97b6ca7.julieignore`

The live directory was empty immediately before the final combined focused run and remained empty immediately
after it. The sorted inventory was byte-identical empty in both snapshots, with SHA-256
`01ba4719c80b6fe911b091a7c05124b64eeece964e09c058ef8f9805daca546b`. The parent shell's `MILLER_HOME` was
unset after the run. No test child remained (`julie-extract`, semantic sidecar, and test-launched `dotnet`
processes were absent); unrelated resident `miller serve` processes were not touched.

## Pending branch evidence

The pre-correction combined Scale filter was 15 passed, 0 failed, 0 skipped; it is retained as historical
focused evidence only. The corrected exact-tree gate is recorded below. The final verification-map,
all-plan-checkbox, and serialized-commit criterion remains pending. Both affected Scale classes are now in the
Windows Scale smoke allowlist; verification awaits Windows CI and no Windows result is claimed here.

## Test-home isolation correction

The earlier branch gate reported 8,279 fast tests and 157 Scale tests passing, but those counts are invalidated:
the run exposed an 84-write live-policy leak into the user's Miller home. The leaked files were recovered to Trash
before this correction. The test assembly now installs a module initializer that preserves a caller-supplied
`MILLER_HOME`, otherwise creates one uniquely named absolute temp home (`miller-tests-home-<pid>-<guid>`), and
restores/removes only that exact validated path at process exit. The two existing in-process `MILLER_HOME`
mutators share one non-parallel collection and restore their prior value.

Focused correction evidence:

- `MillerHomeTests` plus `MillerTestHomeIsolationTests`: 12 passed, 0 failed, 0 skipped with `MILLER_HOME` unset.
- `MillerTestHomeIsolationTests` with caller `MILLER_HOME=/tmp/miller-caller-home-preserved`: 3 passed, 0 failed, 0 skipped.
- `MillerTestHomeIsolationTests` with caller `MILLER_HOME='   '`: 3 passed, 0 failed, 0 skipped.
- The isolation tests accept an ordinary owned tree and conservatively refuse nested reparse points and
  attribute/enumeration failures through the injectable validation seam; cleanup does not traverse a reparse tree.
- `dotnet build Miller.slnx -c Release --no-restore`: 0 warnings, 0 errors.
- `git diff --check` and Miller impact review passed; the Scale CLI subprocess class was compile-checked only.

The prior 8,279/157 branch counts were pending replacement; the corrected exact-final-tree rerun is recorded
below.

## Corrected exact-tree branch gate

The replacement gate ran after the test-home isolation correction on the exact source/test/configuration tree.
No source, test, or configuration changes followed that gate; this finding, plan, and ledger update is
documentation-only.

- `scripts/test.sh all`: fast 8,285 passed, 0 failed, 9 skipped; Scale 157 passed, 0 failed, 16 skipped.
- The wrapper build phase and explicit `dotnet build Miller.slnx -c Release --no-restore` both completed with
  0 warnings and 0 errors.
- The exact `/home/murphy/.miller/ignore-policies` inventory was count 0 before and after the gate, with the
  byte-identical empty-inventory hash `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`.
- No `/tmp/miller-tests-home-*` directory, fixture/test process, or parent `MILLER_HOME` override remained.
- The main worktree was clean; the task worktree was intentionally dirty with the approved task changes.

This replaces the invalidated 8,279-fast/157-Scale result. The earlier 84 leaked policy files remain
recoverable under the documented `miller-ignore-full-suite-recovery-20260822-` Trash prefix; the four-file recovery
from the pre-correction focused replay remains separately documented above. The Windows result is still
awaiting CI.
