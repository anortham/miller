---
id: live-test-engine-agent-friendly-continuous-testing
title: Live-test engine (agent-friendly continuous testing) — parked design
status: active
created: 2026-05-30T18:29:46.690Z
updated: 2026-05-30T18:29:46.690Z
tags: []
---

PARKED design idea (behind M4). Full spec: docs/plans/2026-05-30-live-test-engine-design.md.

WHAT: a Wallaby/NCrunch-style continuous-testing engine *for agents*, so an agent asks "did my edits break anything I have a test for, and what did I leave unverified?" instead of compulsively re-running the whole suite after every trivial edit.

THE REFRAME (the strongest idea): it's a SAFETY tool, not a status tool. Headline = a negative claim ("nothing you changed is covered by a now-failing test") because that's the common case, cheaper to make trustworthy (only re-run tests covering changed lines, not whole suite), and exactly what an agent acts on. TRAP: a negative claim is false-clear if the code has NO test. So it's a DUAL claim — "broke" (confirmed-red) AND "uncovered" (co-headline, the add-a-test signal).

WHY IT'S NOT JUST A TOOL: goal is behavior change, not tool existence. Agents have a verification prior + explicit "run the tests" instructions. So deliverable = engine + output-as-argument (evidence: what ran/when/which hash + explicit "you can skip") + a behavioral skill stanza (same pattern as Miller's "use julie not grep"). Honest success bar: kill COMPULSIVE re-runs (80%), not all runs.

KEY DECISIONS (locked in brainstorm): per-file coverage MVP but true per-method NCrunch parity is the north star; lives IN Miller via existing MCP server; .NET runner first (dogfood Miller's suite); primary tool = check_changes (dual claim), secondary = get_test_status + run_tests; trust state machine green|red|running|stale|uncovered|unknown with invariant "never report clear for changed-but-not-rerun unit"; flake = confirm-before-report (trust-critical); ITestRunner plugin seam (discovery via julie is_test = language-agnostic, execution per-runner = the honest exception to don't-hardcode-languages); separate ct.db keyed by content hashes.

STRATEGIC: consumes M4 impact analysis as the cheap pre-filter → makes M4 MORE valuable, lands AFTER M4.

#1 RISK / SPIKE FIRST: can MTP + a coverage data collector give per-test (per-method) attribution in ONE warm-host run (no process-per-test)? Run against Miller.Tests before locking the parity architecture. Per-file MVP under-delivers on the headline's precision, so the parity path is where the safety value actually lands.

OUT OF SCOPE: live inline values, time-travel debug, gutter overlays (human-IDE features, no agent analog).

STATUS: design captured + committed; NOT scheduled. M4 remains the real next work (blocked on julie enrichment).
