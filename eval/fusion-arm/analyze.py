#!/usr/bin/env python3
"""Fusion-v2 selection analysis over retrieval-eval sweep reports.

Applies the pre-registered T5 gates from
docs/plans/2026-07-21-encoder-comparison-fusion-v2-plan.md (Global Constraints):

  winner bar  = beats fusion-v1 overall cluster-unit nDCG@10 with paired-bootstrap
                95% CI excluding zero AND no regression > 0.02 nDCG on language
                macro-average, worst-language, docs_like view, or identifier
                diagnostic, for BOTH qwen3 and bge-small.
  pin rule    = bge-small takes the pin iff its fused overall nDCG is within 3%
                relative of qwen3 AND worst-language loss <= 0.02 absolute.

Selection statistic: mean of the two shippable encoders' overall cluster-unit
nDCG@10 (R5: one profile for all shippable encoders). Stability: leave-one-unit-out
re-selection (winner must be modal) + 10,000-resample paired bootstrap, seed recorded.
"""

import argparse
import json
import pathlib
import random
import sys

SHIPPABLE = ["qwen3-0.6b-f16", "bge-small-en-v1.5-f32"]
PROFILES = [f"k{k}-r{r}" for k in (20, 60, 120) for r in (1, 2, 3, 4)]
V1 = "k60-r2"  # fusion-v1: RankConstant 60, Conceptual (0.5, 1.0) == ratio 2:1
REGRESSION_BUDGET = 0.02
PIN_RELATIVE = 0.97
CLASS_GATES = ["docs_like", "identifier"]


def load_report(out_dir, candidate, arm):
    path = out_dir / candidate / arm / "report.json" if candidate else out_dir / arm / "report.json"
    with open(path) as f:
        return json.load(f)


def unit_ndcg(report):
    units = {u["unit_id"]: u["ndcg_at_k"] for u in report["units"]}
    if len(units) != report["evaluation_unit_count"]:
        sys.exit(f"unit rows ({len(units)}) != evaluation_unit_count ({report['evaluation_unit_count']})")
    return units


def selection_stat(reports, profile, drop_unit=None):
    per_encoder = []
    for cand in SHIPPABLE:
        units = reports[cand][profile]["_units"]
        scores = [v for k, v in units.items() if k != drop_unit]
        per_encoder.append(sum(scores) / len(scores))
    return sum(per_encoder) / len(per_encoder)


def selection_adjusted_p(reports, cand, winner, resamples, seed):
    """Max-statistic bootstrap p-value for winner-vs-v1 that accounts for selecting the best of all
    profiles on the same units (winner's-curse correction; the naive per-profile CI does not). Sharp
    null: every profile's per-unit diffs vs v1 are mean-centered, resampled, and the max profile mean
    per resample forms the null distribution of "best-looking profile by chance"."""
    ids = sorted(reports[cand][V1]["_units"])
    v1_units = reports[cand][V1]["_units"]
    diffs_by_profile = {}
    for p in PROFILES:
        if p == V1:
            continue
        units = reports[cand][p]["_units"]
        diffs = [units[i] - v1_units[i] for i in ids]
        mean = sum(diffs) / len(diffs)
        diffs_by_profile[p] = ([d - mean for d in diffs], mean)
    observed = diffs_by_profile[winner][1] if winner != V1 else 0.0
    rng = random.Random(seed + 1)
    n = len(ids)
    exceed = 0
    for _ in range(resamples):
        idx = [rng.randrange(n) for _ in range(n)]
        null_max = max(sum(centered[i] for i in idx) / n for centered, _ in diffs_by_profile.values())
        if null_max >= observed:
            exceed += 1
    return observed, exceed / resamples


def paired_bootstrap(winner_units, v1_units, resamples, seed):
    ids = sorted(winner_units)
    if sorted(v1_units) != ids:
        sys.exit("unit id mismatch between winner and v1 reports — pairing impossible")
    diffs = [winner_units[i] - v1_units[i] for i in ids]
    rng = random.Random(seed)
    n = len(diffs)
    means = sorted(sum(diffs[rng.randrange(n)] for _ in range(n)) / n for _ in range(resamples))
    lo = means[int(0.025 * resamples)]
    hi = means[int(0.975 * resamples) - 1]
    return sum(diffs) / n, lo, hi


def gate_checks(winner_rep, v1_rep):
    checks = {}
    checks["macro_ndcg"] = (
        winner_rep["language_macro_average"]["ndcg_at_k"],
        v1_rep["language_macro_average"]["ndcg_at_k"],
    )
    checks["worst_language_ndcg"] = (
        winner_rep["worst_language"]["ndcg_at_k"],
        v1_rep["worst_language"]["ndcg_at_k"],
    )
    for cls in CLASS_GATES:
        checks[f"{cls}_ndcg"] = (
            winner_rep["per_query_class"][cls]["ndcg_at_k"],
            v1_rep["per_query_class"][cls]["ndcg_at_k"],
        )
    return {
        name: {"winner": w, "v1": v, "delta": w - v, "pass": w >= v - REGRESSION_BUDGET}
        for name, (w, v) in checks.items()
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out-dir", type=pathlib.Path, required=True)
    ap.add_argument("--seed", type=int, required=True)
    ap.add_argument("--resamples", type=int, default=10_000)
    ap.add_argument("--json-out", type=pathlib.Path, required=True)
    args = ap.parse_args()

    reports = {}
    for cand in SHIPPABLE:
        reports[cand] = {}
        for profile in PROFILES:
            rep = load_report(args.out_dir, cand, profile)
            rep["_units"] = unit_ndcg(rep)
            reports[cand][profile] = rep

    table = [
        {
            "profile": p,
            **{c: round(reports[c][p]["overall"]["ndcg_at_k"], 6) for c in SHIPPABLE},
            "selection_stat": round(selection_stat(reports, p), 6),
        }
        for p in PROFILES
    ]
    table.sort(key=lambda r: -r["selection_stat"])
    winner = table[0]["profile"]

    unit_ids = sorted(reports[SHIPPABLE[0]][V1]["_units"])
    louo_wins = {}
    for u in unit_ids:
        best = max(PROFILES, key=lambda p: selection_stat(reports, p, drop_unit=u))
        louo_wins[best] = louo_wins.get(best, 0) + 1
    modal_profile = max(louo_wins, key=louo_wins.get)
    louo_modal = modal_profile == winner

    encoders = {}
    for cand in SHIPPABLE:
        mean_diff, ci_lo, ci_hi = paired_bootstrap(
            reports[cand][winner]["_units"], reports[cand][V1]["_units"], args.resamples, args.seed
        )
        observed, adj_p = selection_adjusted_p(reports, cand, winner, args.resamples, args.seed)
        encoders[cand] = {
            "overall_ndcg_winner": reports[cand][winner]["overall"]["ndcg_at_k"],
            "overall_ndcg_v1": reports[cand][V1]["overall"]["ndcg_at_k"],
            "paired_mean_diff": mean_diff,
            "bootstrap_ci95": [ci_lo, ci_hi],
            "ci_excludes_zero": ci_lo > 0,
            "selection_adjusted_p": adj_p,
            "selection_adjusted_significant": adj_p < 0.05,
            "regressions": gate_checks(reports[cand][winner], reports[cand][V1]),
        }

    winner_is_v1 = winner == V1
    bar_met = (
        not winner_is_v1
        and louo_modal
        and all(
            e["ci_excludes_zero"] and all(r["pass"] for r in e["regressions"].values())
            for e in encoders.values()
        )
    )
    selected = winner if bar_met else V1

    q = reports["qwen3-0.6b-f16"][selected]
    b = reports["bge-small-en-v1.5-f32"][selected]
    pin_quality = b["overall"]["ndcg_at_k"] >= q["overall"]["ndcg_at_k"] * PIN_RELATIVE
    pin_worst = b["worst_language"]["ndcg_at_k"] >= q["worst_language"]["ndcg_at_k"] - REGRESSION_BUDGET
    result = {
        "seed": args.seed,
        "resamples": args.resamples,
        "unit_count": len(unit_ids),
        "v1_control": V1,
        "selection_table": table,
        "sweep_winner": winner,
        "winner_is_v1": winner_is_v1,
        "louo": {"wins": louo_wins, "modal_profile": modal_profile, "winner_modal": louo_modal},
        "encoders": encoders,
        "winner_bar_met": bar_met,
        "selected_profile": selected,
        "pin_rule": {
            "bge_within_3pct_relative": pin_quality,
            "bge_worst_language_loss_ok": pin_worst,
            "pin": "bge-small-en-v1.5-f32" if pin_quality and pin_worst else "qwen3-0.6b-f16",
        },
    }
    args.json_out.write_text(json.dumps(result, indent=1))

    print(f"units={len(unit_ids)} seed={args.seed} resamples={args.resamples}")
    print(f"{'profile':>9} " + " ".join(f"{c:>22}" for c in SHIPPABLE) + f" {'stat':>10}")
    for row in table:
        mark = " <- winner" if row["profile"] == winner else (" (v1)" if row["profile"] == V1 else "")
        print(f"{row['profile']:>9} " + " ".join(f"{row[c]:>22.4f}" for c in SHIPPABLE) + f" {row['selection_stat']:>10.4f}{mark}")
    print(f"LOUO: winner modal={louo_modal} wins={louo_wins}")
    for cand, e in encoders.items():
        print(
            f"{cand}: dNDCG={e['paired_mean_diff']:+.4f} CI95=[{e['bootstrap_ci95'][0]:+.4f},{e['bootstrap_ci95'][1]:+.4f}] "
            f"excl0={e['ci_excludes_zero']} sel-adj-p={e['selection_adjusted_p']:.4f} "
            f"sig={e['selection_adjusted_significant']} regressions="
            + ", ".join(f"{k}:{v['delta']:+.4f}{'ok' if v['pass'] else 'FAIL'}" for k, v in e["regressions"].items())
        )
    print(f"winner_bar_met={bar_met} selected={selected} pin={result['pin_rule']['pin']}")


if __name__ == "__main__":
    main()
