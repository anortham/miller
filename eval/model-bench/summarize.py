#!/usr/bin/env python3
"""Collect retrieval-eval reports into one comparison table."""

import argparse
import json
from pathlib import Path


def row(report: dict) -> dict:
    ident = report.get("per_query_class", {}).get("identifier", {})
    worst = report.get("worst_language", {}) or {}
    macro = report.get("language_macro_average", {}) or {}
    clusters = report.get("intent_cluster_summary", {}) or {}
    neg = report.get("negatives", {}) or {}
    overall = report.get("overall", {}) or {}
    per_query = report.get("overall_per_query", {}) or {}
    cluster_max = report.get("overall_cluster_max", {}) or {}
    prose = report.get("per_query_class", {}).get("prose", {})
    return {
        "recall": overall.get("recall_at_k"),
        "ndcg": overall.get("ndcg_at_k"),
        "pq_recall": per_query.get("recall_at_k"),
        "cmax_recall": cluster_max.get("recall_at_k"),
        "macro_recall": macro.get("recall_at_k"),
        "macro_ndcg": macro.get("ndcg_at_k"),
        "worst_lang": worst.get("language"),
        "worst_ndcg": worst.get("ndcg_at_k"),
        "prose_recall": prose.get("recall_at_k"),
        "ident_recall": ident.get("recall_at_k"),
        "ident_ndcg": ident.get("ndcg_at_k"),
        "clusters_hit": clusters.get("cluster_hit_count"),
        "clusters_total": clusters.get("cluster_count"),
        "neg_fp": neg.get("false_positive_rate"),
    }


def fmt(v, nd=4):
    return "—" if v is None else (f"{v:.{nd}f}" if isinstance(v, float) else str(v))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--reports", type=Path, required=True)
    ap.add_argument("--out", type=Path, required=True)
    args = ap.parse_args()

    rows = {}
    for path in sorted(args.reports.glob("*.json")):
        rows[path.stem] = row(json.loads(path.read_text()))

    note = ("Primary metrics (`recall@10`, `nDCG@10`, macro, worst lang) are **cluster units**: an intent\n"
            "cluster is one unit scored as the mean over its paraphrases (design §8). `pq recall` is the\n"
            "per-query view and `cmax recall` the cluster-best view, both secondary. `ident`/`prose` are\n"
            "per-query by construction — query classes cut across clusters.\n")
    header = ("| arm | recall@10 | nDCG@10 | pq recall | cmax recall | macro recall | macro nDCG "
              "| worst lang | worst nDCG | prose recall | ident recall | ident nDCG | clusters | neg FP |")
    sep = "| " + " | ".join(["---"] * 14) + " |"
    lines = [note, header, sep]
    for arm, r in rows.items():
        lines.append(
            f"| `{arm}` | {fmt(r['recall'])} | {fmt(r['ndcg'])} | {fmt(r['pq_recall'])} | "
            f"{fmt(r['cmax_recall'])} | {fmt(r['macro_recall'])} | "
            f"{fmt(r['macro_ndcg'])} | {r['worst_lang'] or '—'} | {fmt(r['worst_ndcg'])} | "
            f"{fmt(r['prose_recall'])} | {fmt(r['ident_recall'])} | {fmt(r['ident_ndcg'])} | "
            f"{r['clusters_hit']}/{r['clusters_total']} | {fmt(r['neg_fp'])} |"
        )
    table = "\n".join(lines) + "\n"
    args.out.write_text(table)
    print(table)


if __name__ == "__main__":
    main()
