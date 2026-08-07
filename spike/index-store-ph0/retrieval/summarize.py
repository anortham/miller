"""Render the three instrument result files as the tables used in results.md."""

from __future__ import annotations

import json
import os
import sys


def load(work: str, name: str):
    path = os.path.join(work, name)
    return json.load(open(path)) if os.path.exists(path) else None


def fts_tables(report) -> None:
    print("\n### Recall-set equivalence vs a dedicated per-view index\n")
    print("| multiple | arm | window | prefilter | temp table | projection | post-filter starved |")
    print("|---|---|---|---|---|---|---|")
    arms = [
        ("word", "word (uncapped)", "n/a"),
        ("trigram_rank_window", "trigram", "rank"),
        ("trigram_density_window", "trigram", "density"),
        ("adversarial_rank_window", "trigram adversarial", "rank"),
        ("adversarial_density_window", "trigram adversarial", "density"),
    ]
    for multiple, entry in report["multiples"].items():
        for key, label, window in arms:
            arm = entry[key]
            starved = arm.get("post_filter_starved_queries", "n/a")
            print(
                f"| x{multiple} | {label} | {window} |"
                f" {'PASS' if arm['equivalent_prefilter'] else 'FAIL(' + str(arm['mismatch_counts']['prefilter']) + ')'} |"
                f" {'PASS' if arm['equivalent_temptable'] else 'FAIL(' + str(arm['mismatch_counts']['temptable']) + ')'} |"
                f" {'PASS' if arm['equivalent_projection'] else 'FAIL(' + str(arm['mismatch_counts']['projection']) + ')'} |"
                f" {starved}/{arm['queries']} |"
            )

    print("\n### Query cost, median ms\n")
    print("| multiple | arm | no visibility | manifest join | temp table | projection | dedicated |")
    print("|---|---|---|---|---|---|---|")
    for multiple, entry in report["multiples"].items():
        for key, label, _ in arms:
            t = entry[key]["timing"]
            print(
                f"| x{multiple} | {label} | {t['postfilter']['median_ms']} |"
                f" {t['prefilter']['median_ms']} | {t['temptable']['median_ms']} |"
                f" {t['projection']['median_ms']} | {t['dedicated']['median_ms']} |"
            )

    print("\n### Store shape and adversarial load\n")
    print("| multiple | store rows | visible | hidden | store MB | hidden trigram matches (median / max) |")
    print("|---|---|---|---|---|---|")
    for multiple, entry in report["multiples"].items():
        b = entry["build"]
        adv = entry["adversarial_rank_window"]
        print(
            f"| x{multiple} | {b['store_rows']:,} | {b['visible_rows']:,} | {b['hidden_rows']:,} |"
            f" {b['bytes'] / 1e6:.0f} | {adv['hidden_matches_median']:,.0f} / {adv['hidden_matches_max']:,} |"
        )

    probe = report.get("mechanism_probe")
    if probe:
        print("\n### Mechanism probe: is the FTS5 rank window corpus-independent?\n")
        print(f"{probe['visible_rows']} visible rows (phrase frequency {probe['phrase_frequency_range']}),"
              f" {probe['hidden_non_matching_rows']:,} long hidden non-matching rows.\n")
        print("| window rule | k | set equal | order equal | symmetric difference |")
        print("|---|---|---|---|---|")
        for rule, per_k in probe["results"].items():
            for k, v in per_k.items():
                print(
                    f"| {rule} | {k} | {v['set_equal']} | {v['order_equal']} |"
                    f" {v['symmetric_difference']} |"
                )


def vec_tables(report) -> None:
    print(f"\n### sqlite-vec {report['vec_version']}, lane {report['lane']},"
          f" k={report['top_k']}, {report['probes']} probes\n")
    print("| multiple | mechanism | supported | top-K matches dedicated | median ms | p95 ms |")
    print("|---|---|---|---|---|---|")
    for multiple, entry in report["multiples"].items():
        for name, m in entry["mechanisms"].items():
            if not m.get("supported"):
                print(f"| x{multiple} | {name} | NO | - | - | - |")
                continue
            print(
                f"| x{multiple} | {name} | yes | {m['matches_dedicated_top_k']} |"
                f" {m['median_ms']} | {m['p95_ms']} |"
            )
    print("\n| artifact | bytes |")
    print("|---|---|")
    print(f"| dedicated per-view vectors (1 view) | {report['dedicated_oracle']['bytes']:,} |")
    print(f"| partition-key table, 8 views | {report['partition_key_8_views']['bytes']:,} |")
    for multiple, entry in report["multiples"].items():
        print(f"| family-shared store x{multiple} | {entry['build']['bytes']:,} |")

    ceiling = report.get("ceiling_probe")
    if ceiling:
        print(f"\n### k-ceiling probe: {ceiling['hidden_nearer_vectors']:,} hidden vectors nearer"
              f" than any visible one, engine k ceiling {ceiling['engine_k_ceiling']}\n")
        print("| strategy | visible rows recovered of 500 | ms | error |")
        print("|---|---|---|---|")
        for name, r in ceiling["results"].items():
            print(f"| {name} | {r['visible_rows_recovered']} | {r['ms']} | {r['error'] or '-'} |")


def docid_tables(report) -> None:
    print("\n### Per-view DocId options\n")
    print("| option | median ms/query | p95 ms | bytes for 8 views | manifest-flip maintenance |")
    print("|---|---|---|---|---|")
    o = report["docid_options"]
    flip = report["manifest_flip"]
    print(
        f"| query-time ROW_NUMBER() | {o['query_time_row_number']['timing']['median_ms']} |"
        f" {o['query_time_row_number']['timing']['p95_ms']} | 0 | none |"
    )
    print(
        f"| materialised per-view mapping | {o['materialised_per_view_mapping']['timing']['median_ms']} |"
        f" {o['materialised_per_view_mapping']['timing']['p95_ms']} |"
        f" {o['materialised_per_view_mapping']['bytes_for_8_views']:,} |"
        f" {flip['contiguous_projection_full_rebuild_ms']} ms full rebuild |"
    )
    print(
        f"| stored sort key, no ordinal | {o['stored_sort_key_no_docid']['timing']['median_ms']} |"
        f" {o['stored_sort_key_no_docid']['timing']['p95_ms']} | 0 | none |"
    )

    print("\n### BM25 corpus statistics per view\n")
    print("| option | median ms/query | bytes for 8 views |")
    print("|---|---|---|")
    for name, s in report["bm25_statistics"].items():
        print(f"| {name} | {s['median_ms']} | {s.get('bytes_for_8_views', 0):,} |")

    d = report["docid_history_divergence"]
    print("\n### The two shipped DocId histories on one file replacement\n")
    print(f"- symbols before/after: {d['symbols_before']} / {d['symbols_after']}")
    print(f"- orders identical: **{d['orders_identical']}**")
    print(f"- first divergent position: {d['first_divergent_position']}")
    print(f"- positions differing: {d['positions_differing']}")


def main() -> int:
    work = sys.argv[1]
    for name, renderer in (
        ("fts_equivalence.json", fts_tables),
        ("vec_prefilter.json", vec_tables),
        ("docid_bm25.json", docid_tables),
    ):
        report = load(work, name)
        if report is None:
            print(f"\n(no {name})")
            continue
        renderer(report)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
