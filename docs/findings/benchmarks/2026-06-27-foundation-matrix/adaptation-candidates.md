# Foundation Matrix Adaptation Candidates

These candidates are product inputs, not parity requirements. Julie remains a baseline and source of lessons; Miller should adapt useful behavior through existing tools, CLI/export contracts, skills, and dashboard presentation before adding any MCP surface.

| rank | category | candidate | evidence source | impact | implementation locality | recommended goal or action |
|---:|---|---|---|---|---|---|
| 1 | route recovery | Improve first-call routing and recovery for source/text and ambiguous lookup intent inside existing `search` and `inspect` output. | Task 3 top-vs-present gaps; original search/inspect route-recovery finding; Task 6 search default/source/content misses. | High: agents can reach existing Miller data with fewer failed first calls, without adding an MCP tool. | `SearchTool`/`SearchRoutePlanner` renderers; inspect target resolution and ambiguity rendering; benchmark manifest rows. | Implemented 2026-06-28 for `search auto` bounded source/docs-config rescue, inspect scoped rerun examples, and generic resolver path penalties. |
| 2 | ambiguity guidance | Make inspect ambiguity explicit when multiple packages, versions, tests, or generated definitions match the same target. | Task 3 Zod inspect top misses; original Flask/Zod ambiguity notes; Task 4 Zod trace refs `needs-search` outcome. | High: avoids agents editing the first plausible but wrong definition. | `InspectTool` target resolution, symbol ranking, and compact candidate rendering. | Inspect candidate guidance and resolver ranking are implemented for search/inspect; trace-specific ambiguity guidance remains in the graph workflow candidate. |
| 3 | output usefulness | Promote compact edit-orientation output before full-body reads. | Original search/inspect finding shows Julie `deep_dive overview` at 1129 median chars versus Miller `inspect full` at 7129 chars; Task 6 shows `inspect full` is used far more than `overview`. | Medium-high: reduces token cost and makes the common inspect path easier to act on. | `MILLER_AGENT_INSTRUCTIONS`, inspect CLI/MCP examples, docs/skills guidance, and possibly default depth guidance. | Update guidance and examples to route first reads through `inspect overview`, preserving `full` for complete bodies. |
| 4 | graph workflow | Improve graph workflow fallback text for `needs-search`, `no-path`, and unsupported bridge outcomes. | Task 4 report-only rows captured Zod trace refs `needs-search`, Miller trace path `no-path`, and Flask bridge unsupported outcomes; Task 6 trace default has high empty rate and low use. | Medium: preserves current graph behavior while helping agents recover to search, inspect, or bridge-specific routes. | `TraceTool` rendering, workflow summary text, and agent instructions; no extractor or graph schema change required. | Add outcome-specific next-call hints to trace output and docs, then rerun Task 4 workflow rows. |
| 5 | Eros contract | Keep Eros foundation contracts as CLI/export gates, not MCP surface expansion. | Task 5 `contract.cli.json` and `contract.cli.jsonl` rows passed 15/15; `docs/contracts/cli-eros-v1.md` is the public contract. | Medium: protects Eros integration while keeping Miller's MCP surface small. | Benchmark manifest contract rows, `docs/contracts/cli-eros-v1.md`, release gates. | Promote the Task 5 contract regression command into future branch/release gate guidance. |
| 6 | adoption guidance | Use telemetry and onboarding to improve existing-tool discovery rather than judging quality by raw usage volume. | Task 6 parseability gate passed; usage interpretation shows trace at 2.3%, impact at 2.7%, and common misses for search/inspect/content. | Medium: turns real local friction into better starter commands without storing raw queries or adding tools. | Telemetry onboarding reader/rendering, docs/README guidance, and agent instructions. | Add onboarding hints for common empty states and low-use deterministic tools; keep usage interpretation report-only. |

## First Implementation Goal

Implemented 2026-06-28 as a narrow `search`/`inspect` recovery slice:

- keep the MCP tool set unchanged;
- add a bounded source/docs-config rescue block when `search auto` has empty or weak primary hits but source/docs/config evidence exists;
- preserve exact concrete symbol hits without unnecessary text-provider work;
- render scoped rerun examples for ambiguous inspect targets spanning files;
- keep same-file ambiguity guidance on more specific targets instead of implying file scope can solve it;
- cover the slice with focused xUnit tests plus Miller hard-gate rows in the foundation matrix manifest.

Evidence: [search/inspect recovery hardening summary](search-inspect-recovery-hardening/summary.md), [CSV](search-inspect-recovery-hardening/results.csv), [JSON](search-inspect-recovery-hardening/results.json).
