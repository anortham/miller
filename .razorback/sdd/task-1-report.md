## Task 1 Report: Vue Route-Reference Structural Facts

Status: verified and committed.

## Summary of Changes

- Added `vue.route_reference.v1` to the Vue web structural fact pattern list.
- Extended Vue template attribute scanning to retain the enclosing tag name so plain `to="/path"` only emits on `RouterLink` / `router-link`.
- Added route-ready Vue facts for:
  - `RouterLink to="/todos"`
  - `router-link to="/admin"`
  - bound literal `:to="'/projects'"`
  - literal router navigation expressions such as `$router.push('/settings')` on `v-on` / `@` handlers
- Preserved existing generic `vue.template_directive.v1` facts.
- Added focused TDD coverage in `vue_emits_route_reference_facts`.

## Miller Calls Used

- `workspace status` for `/Users/murphy/.config/razorback/worktrees/julie-extractors/web-stack-structural-facts-bridge`: confirmed Miller knows the worktree, the artifact is fresh, and queue is empty.
- `context` for the Vue route-reference task: identified `collect_vue_structural_facts`, `vue_template_directive_fact`, `parse_vue_directive`, `scan_markup_attributes`, and the Vue structural-facts test helpers as the relevant entry points.
- `inspect crates/julie-extractors/src/base/web_structural_facts.rs`: confirmed the existing web structural-facts symbols and Vue pattern list.
- `inspect crates/julie-extractors/src/tests/vue/structural_facts.rs`: confirmed existing test helpers and the current Vue directive test shape.
- `inspect collect_vue_structural_facts`, `vue_template_directive_fact`, `web_structural_fact_pattern_ids_for_language`, `parse_vue_directive`, `scan_markup_attributes`, `scan_tag_attributes`, `parse_markup_attribute_value`, and `base_metadata`: confirmed current call flow, metadata conventions, and that `base_metadata` supplies `pattern_version=1`.
- `search route_reference` / `search target_path verb route structural fact`: confirmed there was no existing route-reference structural fact implementation in this worktree.
- `trace web_structural_fact_pattern_ids_for_language`: confirmed the pattern-list helper feeds the feature-gated structural-fact registry check.
- `impact changed_paths` and `impact git=true`: reviewed the changed surface and likely tests; output was broad because common helper names collide across structural-facts modules, but it confirmed the changed web structural-facts path.
- `search vue.template_directive.v1` with `mode=all-text`: found `fixtures/extraction/capabilities.json` still claims only the two previous Vue structural facts, which explains the feature-gated concern below.
- `inspect vue_route_reference_fact` and `inspect vue_route_reference`: confirmed the final route-reference builder is called only from `collect_vue_structural_facts` and remains private to the web structural-facts module.

## Verification Ledger

| Scope | Invariant | Command | Commit SHA | Result | Timestamp |
| --- | --- | --- | --- | --- | --- |
| TDD red check | New test fails before implementation because no `vue.route_reference.v1` facts exist | `cargo test -p julie-extractors vue_emits_route_reference_facts -- --nocapture` | `3336baa90c914d8b8f31a8fd1baf87927dc6eba2` + test-only working tree | Failed as expected: observed `{}` route targets vs expected `/admin`, `/projects`, `/settings`, `/todos` | 2026-06-30 session |
| Worker scope | Vue emits route-ready `vue.route_reference.v1` facts with literal `target_path`, `verb=GET`, framework metadata, and no non-route directive false positives | `cargo test -p julie-extractors vue_emits_route_reference_facts -- --nocapture` | `b411b2fa24e27e46dac07cc02f7b0748083e7ee5` | Pass: 1 passed, 0 failed, 2475 filtered out | 2026-06-30T14:52:02Z |
| Non-weakening check | Existing generic Vue directive facts still emit | `cargo test -p julie-extractors vue_emits_sfc_section_and_template_directive_facts -- --nocapture` | pre-commit working tree matching `b411b2fa24e27e46dac07cc02f7b0748083e7ee5` | Pass: 1 passed, 0 failed, 2475 filtered out | 2026-06-30 session |
| Formatting | Rust formatting is clean | `cargo fmt --check` | pre-commit working tree matching `b411b2fa24e27e46dac07cc02f7b0748083e7ee5` | Pass, exit 0 | 2026-06-30 session |
| Diff hygiene | No whitespace errors | `git diff --check` | pre-commit working tree matching `b411b2fa24e27e46dac07cc02f7b0748083e7ee5` | Pass, exit 0 | 2026-06-30 session |
| Capability-matrix diagnostic | Registry claims match `fixtures/extraction/capabilities.json` | `cargo test -p julie-extractors --features test-capability-matrix capability_matrix_structural_fact_claims_match_registry -- --nocapture` | `b411b2fa24e27e46dac07cc02f7b0748083e7ee5` | Failed: Vue registry includes `vue.route_reference.v1`, but capabilities JSON still claims only `vue.sfc_section.v1` and `vue.template_directive.v1` | 2026-06-30T14:52:42Z |

## Acceptance Criteria Checklist

- [x] `vue.route_reference.v1` appears in `web_structural_fact_pattern_ids_for_language("vue")` through `VUE_WEB_PATTERN_IDS`.
- [x] `RouterLink` and `router-link` literal `to` attributes emit `target_path` and `verb="GET"`.
- [x] Bound `:to="'/todos'"`-style literal expressions emit `target_path` only when the expression is a string literal.
- [x] Non-route directives such as `v-if`, `v-model`, and `:class` do not emit route-reference facts.
- [x] Worker-scope verification passes.
- [x] Task 1 extractor changes are committed.

## Concerns or Plan Mismatches

- Resolved in fix round: `fixtures/extraction/capabilities.json` now claims `vue.route_reference.v1`, and the feature-gated capability-matrix registry check passes.
- No architecture mismatch found for the approved extractor-side shape: the change emits route-ready facts only, does not add resolved relationships, does not add route matching, and does not weaken `vue.template_directive.v1`.

## Commit

- `b411b2fa24e27e46dac07cc02f7b0748083e7ee5` - `Add Vue route reference structural facts`

## Fix Round Evidence

Lead review identified the capability-matrix concern as a Task 1 completion gap. Expanded fix ownership allowed `fixtures/extraction/capabilities.json`, so the Vue structural-facts claim list was updated to include `vue.route_reference.v1`.

| Scope | Invariant | Command | Commit SHA | Result | Timestamp |
| --- | --- | --- | --- | --- | --- |
| Capability-matrix fix gate | Vue structural-fact fixture claims match the registry after adding `vue.route_reference.v1` | `cargo test -p julie-extractors --features test-capability-matrix capability_matrix_structural_fact_claims_match_registry -- --nocapture` | `330877da48afeb33106d12b3317748d533570267` | Pass: 1 passed, 0 failed, 2512 filtered out | 2026-06-30T14:56:58Z |
| Worker scope rerun | Vue still emits route-ready `vue.route_reference.v1` facts | `cargo test -p julie-extractors vue_emits_route_reference_facts -- --nocapture` | `330877da48afeb33106d12b3317748d533570267` | Pass: 1 passed, 0 failed, 2475 filtered out | 2026-06-30T14:56:58Z |
| Diff hygiene | Fix diff had no whitespace errors before commit | `git diff --check` | pre-fix-commit working tree on `b411b2fa24e27e46dac07cc02f7b0748083e7ee5` | Pass, exit 0 | 2026-06-30T14:56:13Z |

Fix commit:

- `330877da48afeb33106d12b3317748d533570267` - `Claim Vue route reference capability`
