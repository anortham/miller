# Miller Pre-Release Readiness Review

- **Date:** 2026-06-06
- **Scope:** Whole project, "should we cut the first public release and invite testers?"
- **Method:** Empirical build/test/AOT verification (run locally) + an 11-dimension static
  review with adversarial verification of every material finding (32 agents) + a completeness
  critic. Headline findings re-verified by hand against real code before inclusion.
- **Reviewed commit:** `238b7e4` (`0.1.0+238b7e41a9bb`)

## Verdict

**Conditional GO.** The engineering core is genuinely solid and the scariest release risk
(Native AOT) is verified working end-to-end. There are **no code-correctness blockers**. The
release is gated only by a small set of **packaging / licensing / docs** items, and the exact
blocker list depends on **which release shape you ship first** (see the fork below).

- Ship **source-checkout first** → fix the 5 "must-fix (any release)" items, then GO.
- Ship **binary archives** → also fix the 3 "must-fix (binary archives)" items first.

## What I verified empirically (all green)

| Check | Result |
|---|---|
| `dotnet build Miller.slnx -c Release` | 0 warnings / 0 errors |
| Fast suite (`Category!=Scale`) | 1644 passed, 9s |
| Scale suite (`Category=Scale`, real julie-extract) | 25 passed |
| **AOT publish** (`-r osx-arm64 -p:PublishAot=true -p:JsonSerializerIsReflectionEnabledByDefault=false`) | succeeds, **0 IL/trim warnings**, native Mach-O (no managed DLLs), ships `libe_sqlite3` + `libblake3` + `.tools/julie-extract` |
| AOT CLI smoke | `version`, `search`, `inspect` incl. `--json` all correct |
| **AOT MCP `serve` smoke** | `initialize` + `tools/list` (all 7 schemas) + a real `tools/call` for `search` → `IsError=False`; stdout protocol purity preserved (logs to stderr); leader election idled as reader |

The README still calls Native AOT "deferred beta hardening work" — that is now **factually wrong**;
the AOT path works front-to-back. This undersells the project and is a doc fix, not a code gap.

## Verified-clean (confidence boosters — do not re-litigate)

- **JSON is AOT-safe**: every `System.Text.Json` call uses a source-gen `JsonSerializerContext`
  or hand-written `Utf8JsonWriter` (`ServerJson.cs`, `IndexingJsonContexts.cs`,
  `JulieExtractRunner.cs:174`, `BridgeProviderSelection.cs:19`). No reflection serialization.
- **MCP SDK is AOT-safe**: tools registered via the generic per-type `.WithTools<T>()` overload
  (`Program.cs:88-97`), not assembly scanning. SQLite native bundle ships and loads under AOT.
- **Host lifecycle, CLI/stdout purity, telemetry interceptor** all hold as documented.
- **Search-sidecar self-heal** is correct — cannot silently return wrong/empty results.
- **Core feature language parity is genuinely fixed**: symbol / source_regions / relationship
  extraction are all-language; the parity concern is isolated to the bridge (below).
- **Supply chain**: julie-extract download is pinned + SHA-256-verified, fails closed.
- **Secret hygiene**: `.gitignore` correct; no committed secrets; dashboard binds loopback only.

---

## Must-fix before ANY release (5)

1. **LICENSE file is missing.** `README.md:309-311` says "MIT" but there is **no `LICENSE` at the
   repo root** (confirmed). Add an MIT `LICENSE` with a copyright line. A first public release that
   states a license it does not ship is unenforceable and reads as careless.

2. **Third-party attribution / NOTICE.** Release archives + the repo bundle third-party code with
   no attribution: **Serilog (Apache-2.0 — requires a NOTICE)**, the **ModelContextProtocol SDK**,
   and **vendored `src/Miller.Dashboard/wwwroot/lib/htmx/htmx.min.js`**. Add a `THIRD-PARTY-NOTICES`
   file and include it in archives. (`julie-extract` is the author's own `anortham/julie-extractors`,
   so its bundling is lower-risk, but ship its license text in `.tools/` too.)

3. **README AOT drift.** `README.md:30-31` and `:297` say AOT is "deferred"/"not a beta blocker";
   CI, the site, and the release notes all say AOT ships. Update to: the main `miller` binary
   publishes with Native AOT; only the dashboard helper stays non-AOT (Razor Components limitation).

4. **`edit` can write outside the workspace.** `EditService.ToAbsolute` (`EditService.cs:611-614`)
   passes a rooted path through unchanged with **no workspace-containment check**; combined with
   `allow_stale` (bypasses freshness) and `apply=true`, an absolute path outside the workspace is
   overwritten. This is the one mutating tool — reject resolved targets that canonicalize outside
   the workspace root, independent of `allow_stale`. Add a regression test.

5. **Dashboard hard-requires `python3` (Unix).** `DashboardCliLauncher.cs:408` shells out to
   `python3` to detach the dashboard; there is no fallback. On a minimal Linux image (no python) it
   fails; on macOS invoking `python3` can trigger an Xcode CLT install prompt. This hits
   *source-checkout users too*. Either document `python3` as a prerequisite for `miller dashboard`,
   or replace the shim with a native detached spawn (`setsid`/`posix_spawn`).

## Also must-fix IF the first release is binary archives (3)

6. **macOS binaries are unsigned / un-notarized.** No codesign/notarize step in `release.yml`. Every
   macOS archive user is Gatekeeper-blocked on first run (download quarantine xattr) with no
   documented workaround. Either sign+notarize, or document the `xattr -dr com.apple.quarantine`
   workaround prominently. (2 of 4 targets are macOS.)

7. **Published `miller version` ignores the release tag.** `release.yml:93` builds with the version
   hard-pinned in `Directory.Build.props`; it matches `v0.1.0` only by coincidence. The next tag
   produces `miller-0.2.0-*.tar.gz` whose `miller version` still says `0.1.0`. Pass
   `-p:Version=$RELEASE_VERSION` into both `dotnet publish` calls (or fail when they disagree).

8. **No binary-release install docs.** README/site document only source-checkout. A user who
   downloads an archive has no guidance on extract → place → point an MCP client at the absolute
   `miller` path (no `dotnet run`, no SDK). Add an "Install from a release archive" section, or
   clearly mark archives as not-yet-the-supported-path.

---

## Should-fix (medium — correctness / safety / polish)

- **`workspace open` checks sensitive-root before canonicalization** (`WorkspaceTool.cs:382,408`):
  a symlink whose target is a sensitive root (e.g. `~`) bypasses the lexical guard. The CLI does it
  in the right order — route both through one helper. (security)
- **UTF-8 BOM silently stripped on first edit** (`EditService.cs:625`, `EditApplier.cs:144`): first
  edit to any BOM-bearing file removes the BOM. Sniff + preserve. (edit fidelity)
- **`workspace_id` hash is case-sensitive but path equality folds case** (`WorkspaceId.cs:10-14`):
  on macOS/Windows, launching from a differently-cased path mints a second identity → duplicate
  index. Case-fold the canonical root on case-insensitive platforms before hashing.
- **69 MB `miller.dSYM` ships in both macOS archives** (`release.yml:175` strips only `*.pdb/*.dbg`).
  Add `-name '*.dSYM' -prune -exec rm -rf` or `-p:StripSymbols=true`.
- **GitHub Pages footer links a `-draft` release-notes file** (`docs/site/index.html:184`) as the
  canonical notes. Finalize/rename before going public.
- **Torn/corrupt `symbols.db` hard-fails startup** (`ExtractReader.cs:284-290`,
  `IndexBootstrapService.cs:355-365`): a writer killed mid-write leaves the next `serve` throwing
  instead of auto-rebuilding (the `IncompatibleExtract` path already auto-rebuilds — broaden the
  catch to `SqliteException` corruption). (reliability)

## Notable lows worth a look

- **`dry_run` flag is completely ignored** (`EditService.cs:600`, `EditTool.cs:62`): `apply=true`
  writes even with `dry_run=true`. Two write-control flags where only one works is a footgun on the
  mutating tool — honor it or remove it.
- **Release re-run unconditionally clobbers an existing release** (`release.yml:250`) — conflicts
  with the repo's own no-overwrite rule. Gate `--clobber` behind an explicit force input.
- **Bridge is dotnet-web-only but presented as general** in `MILLER_AGENT_INSTRUCTIONS.md:42-43`
  and README — a user on Python/Go/Rust gets empty `trace mode=bridge` with no hint it's scoped.
  Mirror the MCP tool description's scoping into the agent instructions + README. (language-parity)
- **CI gaps**: no `git diff --check`; AGENTS.md↔CLAUDE.md sync only enforced by an opt-in local
  hook, not CI; no AOT lane (AOT first runs at tag time). Add an AOT publish + `serve`/tools-call
  smoke to the release job so an AOT regression goes red instead of shipping.
- **Polish**: `Thread.Sleep` busy-wait on a request thread (`CrossWorkspaceRefreshService.cs:176`);
  read-only SQLite layer writes a probe file (`SqliteReadOnlyAccess.cs:69`); no `File.Move` retry on
  Windows (`SearchIndexWriter.cs:114`); in-flight `julie-extract` not killed on Ctrl-C
  (`JulieExtractRunner.cs`); no per-file log size cap; `miller workspace --help` runs `status`.

## Recommended sequence

1. Decide release shape (source-checkout vs binary archives) — this sets the blocker list.
2. Fix must-fix #1-#5 (small, mostly mechanical except the edit-containment guard which needs a test).
3. If binary: fix #6-#8.
4. Add the AOT `serve`+tools/call smoke to the release job (cheap insurance against a silent AOT
   regression).
5. Re-run the final beta-candidate gate (`scripts/test.sh all`, `dotnet build -c Release`,
   `git diff --check`) on the exact release commit.
