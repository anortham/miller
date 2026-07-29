# Sealed paired takeover gate — disposition: cancelled as superseded

Date: 2026-07-28. Decision recorded per the approved 2026-07-28 strategy plan (Step 1), which closes the
Julie takeover program formally.

## Decision

The sealed paired Julie-vs-Miller evaluation (the final gate named in the takeover brief
`miller-semantic-integration-program-p0-p6` and in `docs/migration-from-julie.md`) is **cancelled, not
run**. The takeover program is closed with v1.14.0 as shipped.

## Rationale

1. **The comparison no longer informs a decision.** The sealed gate existed to authorize retiring Julie.
   Julie has been in declared maintenance mode since 2026-06-06, is frozen at v7.17.0 (2026-07-22), and is
   strictly behind on extraction (julie-extractors 2.16.0 vs Miller's pinned 2.19.0; 34 vs 36 languages).
   Miller v1.14.0 shipped 2026-07-28 on tool-level review evidence
   (`docs/findings/2026-07-26-phase10-broad-final-review.md`: no known P0/P1 blocker). Running a paid
   sealed comparison now would measure a retired baseline.
2. **The wrong baseline.** Every recorded head-to-head compares Miller to Julie. The decisive product
   question — what Miller adds over a bare agent with shell tools — has never been measured. That
   measurement (Miller-on vs Miller-off visible calibration) replaces the sealed gate as the next
   evaluation spend; see the strategy plan Step 2. Deliverable:
   `docs/findings/<date>-miller-vs-bare-agent-calibration.md`.

## What this forfeits, honestly

The last recorded Julie-vs-Miller numbers are the **pre-remediation** 2026-07-23 visible calibration
(`docs/findings/2026-07-23-miller-julie-takeover-v1-visible-calibration.md`), in which Julie led
(3/15 vs 2/15 correct tasks; recall@6 15.38% vs 7.69%). Cancelling the sealed gate means no
post-remediation Julie comparison will exist. This is accepted: the remediation's per-tool review and
gate evidence is the basis for the takeover claim, and future evaluation spend goes to the
bare-agent baseline, which is the operative question.

## Effects

- `docs/migration-from-julie.md` is now operative for v1.14.0; its activation condition no longer
  references the sealed gate.
- Julie's retirement note (support window, final release) is published in the Julie repository README;
  Julie's open TODO bugs are wontfix by policy.
- The takeover brief is completed; the active brief is the 2026-07-28 strategy brief.
