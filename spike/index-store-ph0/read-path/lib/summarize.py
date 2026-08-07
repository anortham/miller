#!/usr/bin/env python3
"""Turns the captured JSON evidence in out/ into the headline tables and the PASS/FAIL call."""

from __future__ import annotations

import json
import os
import sys

TARGET_RATIO = 1.2
MB = 1048576.0


def load(out: str, name: str):
    path = os.path.join(out, name)
    if not os.path.exists(path):
        return None
    with open(path) as fh:
        return json.load(fh)


def obj_bytes(report, name):
    for o in report["objects"]:
        if o["name"] == name:
            return o["bytes"]
    return 0


def table_group(report, table):
    """Table pages + the pages of every index that belongs to it."""
    total = 0
    for o in report["objects"]:
        n = o["name"]
        if n == table or n.startswith(f"sqlite_autoindex_{table}_") or (
            n.startswith("idx_") and n.endswith(table)
        ):
            total += o["bytes"]
    return total


def main() -> int:
    out = sys.argv[1]
    single = load(out, "bytes-single.json")
    keepfile = load(out, "bytes-keepfile.json")
    v4single = load(out, "bytes-v4single.json")
    store = load(out, "bytes-store.json")
    stress = load(out, "bytes-store-stress.json")
    inflated = load(out, "bytes-store-inflated.json")
    ded8 = load(out, "bytes-dedicated-view8.json")
    views = load(out, "store-build.json")

    lines = []
    add = lines.append

    add("## A. Composite-key amplification (base data only, one view's worth of rows)\n")
    add("| object group | today single-key | composite + file_id kept | v4 composite "
        "(version_id replaces file_id) |")
    add("|---|---:|---:|---:|")
    pairs = [
        ("symbols", "symbols"),
        ("identifiers", "identifiers"),
        ("reference_sites", "reference_sites"),
        ("resolution rows", None),
        ("files / file_versions", None),
    ]
    for label, table in pairs:
        if table:
            a, b, c = (table_group(single, table), table_group(keepfile, table),
                       table_group(v4single, table))
        elif label.startswith("resolution"):
            a = table_group(single, "resolutions")
            b = table_group(keepfile, "resolution_base_entries")
            c = table_group(v4single, "resolution_base_entries")
        else:
            a = table_group(single, "files")
            b = table_group(keepfile, "file_versions")
            c = table_group(v4single, "file_versions")
        add(f"| {label} | {a/MB:.1f} MB | {b/MB:.1f} MB ({100*(b-a)/a:+.1f}%) | "
            f"{c/MB:.1f} MB ({100*(c-a)/a:+.1f}%) |")
    st, kt, vt = (single["physical_bytes"], keepfile["physical_bytes"],
                  v4single["physical_bytes"])
    add(f"| **total physical** | **{st/MB:.1f} MB** | **{kt/MB:.1f} MB "
        f"({100*(kt-st)/st:+.1f}%)** | **{vt/MB:.1f} MB ({100*(vt-st)/st:+.1f}%)** |")

    add("\n## B. Eight-view family store vs a single index\n")
    rows = [("single index today (single-key schema)", st, 1.0)]
    if store:
        rows.append(("8-view store, sampled divergence", store["physical_bytes"],
                     store["physical_bytes"] / st))
    if stress:
        rows.append(("8-view store, p90 divergence every view", stress["physical_bytes"],
                     stress["physical_bytes"] / st))
    if inflated:
        rows.append(("8-view store + 2 retained history generations",
                     inflated["physical_bytes"], inflated["physical_bytes"] / st))
    if ded8:
        rows.append(("one dedicated copy of diverged view 8", ded8["physical_bytes"],
                     ded8["physical_bytes"] / st))
        rows.append(("8 dedicated copies (view1 + 7x view8, measured)",
                     st + 7 * ded8["physical_bytes"], (st + 7 * ded8["physical_bytes"]) / st))
    add("| configuration | physical bytes | x single index |")
    add("|---|---:|---:|")
    for label, b, r in rows:
        add(f"| {label} | {b/MB:.1f} MB | {r:.3f}x |")

    verdict = None
    if store:
        ratio = store["physical_bytes"] / st
        verdict = "PASS" if ratio <= TARGET_RATIO else "FAIL"
        add(f"\n**GATE (8 views at sampled task-branch divergence vs 1.2x): {ratio:.3f}x "
            f"-> {verdict}**")
    if stress:
        r2 = stress["physical_bytes"] / st
        add(f"\nStress configuration (every view at the p90 divergence of the sampled history): "
            f"{r2:.3f}x -> {'PASS' if r2 <= TARGET_RATIO else 'FAIL'}")

    if views:
        add("\n### View divergence actually built\n")
        add("| view | target % | changed files | actual % | resolution delta rows |")
        add("|---:|---:|---:|---:|---:|")
        add(f"| 1 | 0.000 | 0 | 0.000 | 0 |")
        for v in views["views"]:
            add(f"| {v['view_id']} | {v['target_divergence_pct']:.3f} | {v['changed_files']} | "
                f"{v['actual_divergence_pct']:.3f} | {v['resolution_delta_rows']} |")
        if store:
            rc = store["row_counts"]
            add(f"\nStore rows: file_versions {rc['file_versions']}, symbols {rc['symbols']}, "
                f"identifiers {rc['identifiers']}, reference_sites {rc['reference_sites']}, "
                f"resolution base {rc['resolution_base_entries']}, resolution deltas "
                f"{rc['resolution_deltas']}, manifest entries {rc['view_manifest']}.")

    add("\n## C. Result-set equivalence (both visibility shapes vs the dedicated copy)\n")
    add("| view | query class | keys | rows compared | mismatches |")
    add("|---|---|---:|---:|---:|")
    for v in (1, 8):
        ver = load(out, f"verify-view{v}.json")
        if not ver:
            continue
        for klass, c in ver["classes"].items():
            add(f"| {v} | {klass} | {c['keys']} | {c['rows_compared']} | {c['mismatches']} |")

    add("\n## D. Read overhead per query class\n")
    for fname, title in (("reads-view1.json", "view 1 (base manifest, no divergence)"),
                         ("reads-view8.json", "view 8 (most diverged view)"),
                         ("reads-inflated.json", "view 1, store inflated with retained history")):
        m = load(out, fname)
        if not m:
            continue
        add(f"### {title} — {m['passes']} interleaved passes, "
            f"{m['keys_per_class']['name_lookup']} keys per class, "
            f"harness floor {m['harness_floor_us_per_query']} us/query\n")
        add("| query class | shape | median ms/sweep | us/query | rows | vs dedicated | "
            "vs v4 no-visibility | VDBE steps |")
        add("|---|---|---:|---:|---:|---:|---:|---:|")
        for klass, shapes in m["classes"].items():
            for shape, s in shapes.items():
                add(f"| {klass} | {shape} | {s['median_ms_per_sweep']:.2f} | "
                    f"{s['us_per_query']:.1f} | {s['rows_returned']} | "
                    f"{s.get('overhead_pct_vs_dedicated', 0):+.1f}% | "
                    f"{s.get('overhead_pct_vs_v4_novis', 0):+.1f}% | "
                    f"{s['vdbe_steps_per_sweep']:,} |")
        add("")
    print("\n".join(lines))
    return 0 if verdict != "FAIL" else 0


if __name__ == "__main__":
    sys.exit(main())
