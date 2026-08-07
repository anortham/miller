#!/usr/bin/env python3
"""Render out/*.json into the markdown tables results.md cites.

Every number in results.md comes from here, so the tables and the raw JSON
cannot drift apart.

Usage: summarize.py <outdir>
"""

from __future__ import annotations

import json
import os
import sys


def mb(value) -> str:
    if value is None:
        return "-"
    return f"{value / 1e6:,.1f}"


def gb(value) -> str:
    if value is None:
        return "-"
    return f"{value / 1e9:,.3f}"


def pct(part, whole) -> str:
    if not whole:
        return "-"
    return f"{part / whole * 100:.1f}%"


def load(outdir: str, name: str):
    path = os.path.join(outdir, name)
    if not os.path.exists(path):
        return None
    with open(path) as handle:
        return json.load(handle)


def gc_section(data) -> list[str]:
    lines = ["## 1. GC: physical reclamation", ""]
    lines.append(
        f"Store: {data['file_versions']:,} file versions across "
        f"{data['distinct_paths']:,} paths x {data['generations_per_path']} generations, "
        f"{data['rows_per_file_version']} rows per file version, "
        f"page_size 4096, staged vacuum budget {data['vacuum_pages_per_stage']:,} pages."
    )
    lines.append("")
    lines.append(
        "| arm | auto_vacuum | delete pattern | built (MB) | after DELETE (MB) | "
        "freelist pages | after incremental_vacuum (MB) | reclaimed | vacuum s | "
        "after full VACUUM (MB) | full VACUUM s |"
    )
    lines.append("|---|---|---|---|---|---|---|---|---|---|---|")
    for arm in data["arms"]:
        built = arm["build"]["file_bytes"]
        after_delete = arm["delete"]["file_bytes"]
        iv = arm.get("incremental_vacuum") or arm.get("incremental_vacuum_attempt")
        after_iv = iv.get("file_bytes", iv.get("file_bytes_after"))
        iv_seconds = iv.get("total_seconds", iv.get("seconds"))
        lines.append(
            f"| `{arm['label']}` | {arm['auto_vacuum']} | {arm['delete_pattern']} | "
            f"{mb(built)} | {mb(after_delete)} | {arm['delete']['freelist_pages']:,} | "
            f"{mb(after_iv)} | {pct(built - after_iv, built)} | {iv_seconds} | "
            f"{mb(arm['full_vacuum']['file_bytes_after'])} | "
            f"{arm['full_vacuum']['seconds']} |"
        )
    lines.append("")
    for arm in data["arms"]:
        if arm["auto_vacuum"] == "INCREMENTAL":
            iv = arm["incremental_vacuum"]
            lines.append(
                f"- `{arm['label']}` staged vacuum: {iv['stages']} stages, "
                f"max stage {iv['max_stage_seconds']}s, mean stage "
                f"{iv['mean_stage_seconds']}s, freelist remaining "
                f"{iv['freelist_remaining']}."
            )
        else:
            att = arm["incremental_vacuum_attempt"]
            lines.append(
                f"- `{arm['label']}` incremental_vacuum on auto_vacuum=NONE: "
                f"raised={att['raised']}, freelist {att['freelist_before']:,} -> "
                f"{att['freelist_after']:,}, file "
                f"{mb(att['file_bytes_before'])} -> {mb(att['file_bytes_after'])} MB "
                f"in {att['seconds']}s."
            )
        lines.append(
            f"- `{arm['label']}` `PRAGMA secure_delete` on a fresh default "
            f"connection: {arm['secure_delete_on_plain_reopen']} "
            f"(persisted auto_vacuum: {arm['pragmas_on_reopen']['auto_vacuum']})."
        )
    lines.append("")

    sidecar = data["sidecar"]
    build = sidecar["build"]
    gcs = sidecar["gc"]
    lines.append("### FTS5 sidecar")
    lines.append("")
    lines.append(
        f"{build['versions']:,} versions / {build['docs']:,} documents, automerge "
        f"disabled during load, page-limited merge budget "
        f"{gcs['merge_pages_per_round']} pages."
    )
    lines.append("")
    lines.append("| step | file (MB) | symbols_fts segids | symbols_trigram segids | freelist |")
    lines.append("|---|---|---|---|---|")
    lines.append(
        f"| built | {mb(gcs['file_bytes_start'])} | "
        f"{gcs['segments_start']['symbols_fts']['segids']} | "
        f"{gcs['segments_start']['symbols_trigram']['segids']} | - |"
    )
    lines.append(
        f"| after DELETE of {gcs['deleted_docs']:,} docs | "
        f"{mb(gcs['file_bytes_after_delete'])} | "
        f"{gcs['segments_after_delete']['symbols_fts']['segids']} | "
        f"{gcs['segments_after_delete']['symbols_trigram']['segids']} | "
        f"{gcs['freelist_after_delete']:,} |"
    )
    lines.append(
        f"| after page-limited merge | {mb(gcs['file_bytes_after_merge'])} | "
        f"{gcs['segments_after_merge']['symbols_fts']['segids']} | "
        f"{gcs['segments_after_merge']['symbols_trigram']['segids']} | "
        f"{gcs['freelist_after_merge']:,} |"
    )
    lines.append(
        f"| after incremental_vacuum | {mb(gcs['file_bytes_after_vacuum'])} | - | - | "
        f"{gcs['incremental_vacuum']['freelist_remaining']} |"
    )
    lines.append("")
    lines.append(
        f"- merge rounds: {gcs['merge_rounds_total']} total, "
        f"{gcs['merge_rounds_with_work']} did work; "
        f"{gcs['merge_seconds_total']}s total, max round "
        f"{gcs['merge_max_round_seconds']}s, mean round "
        f"{gcs['merge_mean_round_seconds']}s."
    )
    opt = gcs["optimize_control"]
    lines.append(
        f"- `optimize` control on an identical clone: one call, "
        f"{opt['single_call_seconds']}s, "
        f"{opt['segments_after']['symbols_fts']['segids']} segid left, final file "
        f"{mb(opt['file_bytes_after_optimize_and_vacuum'])} MB."
    )
    lines.append(
        f"- FTS5 config after enabling secure-delete: {build['fts_config_after']} "
        f"(was {build['fts_config_before']})."
    )
    lines.append("")

    lines.append("### secure-delete matrix (sentinel byte scan of the file)")
    lines.append("")
    lines.append(
        "| FTS5 `secure-delete` | core `secure_delete` | hits before | after DELETE | "
        "after merge | after vacuum | fts config version |"
    )
    lines.append("|---|---|---|---|---|---|---|")
    for probe in data["secure_delete_probes"]:
        lines.append(
            f"| {probe['fts_secure_delete']} | {probe['core_secure_delete']} | "
            f"{probe['sentinel_hits_before_delete']['main']} | "
            f"{probe['sentinel_hits_after_delete']['main']} | "
            f"{probe['sentinel_hits_after_merge']['main']} | "
            f"{probe['sentinel_hits_after_vacuum']['main']} | "
            f"{probe['fts_config_on_reopen'].get('version')} |"
        )
    lines.append("")
    return lines


def reuse_efficiency(trial: dict, total_versions: int) -> float:
    in_flight = trial["kill_fraction"] * total_versions
    if in_flight <= 0:
        return 0.0
    return min(1.0, trial["reusable_versions_after_crash"] / in_flight)


def granularity_section(data) -> list[str]:
    lines = ["## 2. Transaction granularity", ""]
    lines.append(
        f"{data['versions_per_import']:,} file versions per import = "
        f"{data['rows_per_import']:,} rows, WAL journal mode, "
        f"{data['trials_per_mode']} SIGKILL trials per mode, chunk size "
        f"{data['chunk_versions']} versions, kill seed {data['kill_seed']}."
    )
    lines.append("")
    lines.append(
        "| mode | sync | autockpt pages | commits | rows/s | clean s | WAL peak (MB) | "
        "final db (MB) | reusable after SIGKILL (min/mean/max) | reuse efficiency | "
        "truncated after resume | quick_check |"
    )
    lines.append("|---|---|---|---|---|---|---|---|---|---|---|---|")
    total_versions = data["versions_per_import"]
    for mode in data["modes"]:
        s = mode["summary"]
        efficiencies = [
            reuse_efficiency(t, total_versions) for t in mode["trials"]
        ]
        mean_efficiency = sum(efficiencies) / len(efficiencies) if efficiencies else 0
        lines.append(
            f"| `{mode['mode']}` | {mode['synchronous']} | "
            f"{mode['autocheckpoint_pages']:,} | {s['commits']:,} | "
            f"{s['rows_per_second']:,.0f} | {mode['clean']['report']['seconds']} | "
            f"{mb(s['peak_wal_bytes'])} | {mb(s['final_db_bytes'])} | "
            f"{s['reusable_after_crash_min']} / {s['reusable_after_crash_mean']} / "
            f"{s['reusable_after_crash_max']} | {mean_efficiency:.0%} | "
            f"{s['truncated_after_resume_max']} | "
            f"{'ok' if s['all_quick_checks_ok'] else 'FAILED'} |"
        )
    lines.append("")
    lines.append(
        "Reuse efficiency = reusable versions / versions the importer had time to "
        "write before the kill (kill fraction x total). It normalises away the fact "
        "that a faster mode reaches a smaller fraction of the import in the same "
        "wall-clock slice."
    )
    lines.append("")
    lines.append("### per-trial detail")
    lines.append("")
    lines.append(
        "| mode | trial | kill at | versions in flight | marked complete | reusable | "
        "reuse efficiency | truncated | orphan child rows | resume skipped | "
        "resume imported | final truncated |"
    )
    lines.append("|---|---|---|---|---|---|---|---|---|---|---|---|")
    for mode in data["modes"]:
        for trial in mode["trials"]:
            ac = trial["after_crash"]
            in_flight = trial["kill_fraction"] * total_versions
            lines.append(
                f"| `{mode['mode']}` | {trial['trial']} | "
                f"{trial['kill_fraction']:.0%} | {in_flight:.0f} | "
                f"{ac['marked_complete']} | "
                f"{ac['reusable_complete_versions']} | "
                f"{reuse_efficiency(trial, total_versions):.0%} | "
                f"{ac['visible_but_truncated_versions']} | "
                f"{ac['orphan_child_rows']} | {trial['resume_skipped']} | "
                f"{trial['resume_imported']} | {trial['final_truncated_versions']} |"
            )
    lines.append("")
    return lines


def promotion_section(data) -> list[str]:
    lines = ["## 3. Promotion capacity", ""]
    lines.append(
        f"{data['file_versions']:,} file versions per generation, "
        f"{data['generations_per_path']} generations per path, retention drops the "
        f"{data['generations_dropped_by_retention']} oldest."
    )
    lines.append("")
    lines.append(
        "| arm | old gen (MB) | new gen (MB) | sidecars (MB) | WAL/temp peak (MB) | "
        "reader-retained (MB) | formula peak (MB) | measured peak (MB) | delta |"
    )
    lines.append("|---|---|---|---|---|---|---|---|---|")
    for arm in data["arms"]:
        t = arm["formula_terms"]
        lines.append(
            f"| `{arm['arm']}` | {mb(t['old_generation_bytes'])} | "
            f"{mb(t['new_generation_bytes'])} | {mb(t['sidecar_bytes'])} | "
            f"{mb(t['wal_temp_bytes'])} | {mb(t['reader_retained_bytes'])} | "
            f"{mb(arm['formula_predicted_peak_bytes'])} | "
            f"{mb(arm['peak_family_bytes'])} | {arm['delta_percent']:+.2f}% |"
        )
    lines.append("")
    for arm in data["arms"]:
        lines.append(
            f"- `{arm['arm']}`: baseline family {mb(arm['baseline_family_bytes'])} MB, "
            f"peak {mb(arm['peak_family_bytes'])} MB "
            f"({arm['peak_family_bytes'] / max(arm['baseline_family_bytes'], 1):.2f}x "
            f"baseline), at-promote {mb(arm['family_bytes_at_promote_moment'])} MB, "
            f"after release {mb(arm['family_bytes_after_release'])} MB, "
            f"{arm['samples']:,} samples over {arm['total_seconds']}s."
        )
        if arm["retention_sweep"]:
            sweep = arm["retention_sweep"]
            lines.append(
                f"  - retention sweep first: {mb(sweep['store_bytes_before'])} -> "
                f"{mb(sweep['store_bytes_after'])} MB "
                f"({sweep['delete_seconds']}s delete + "
                f"{sweep['incremental_vacuum_seconds']}s vacuum)."
            )
    lines.append("")
    return lines


DOTNET_RUNTIME_IDENTIFIERS = 12_860_000
DOTNET_RUNTIME_ARTIFACT_BYTES = 21.9e9
MILLER_ARTIFACT_IDENTIFIERS = 380_720
MILLER_ARTIFACT_BYTES = 808_751_104


def projection_section(gc, gran, promo) -> list[str]:
    """Scale the measured numbers to dotnet/runtime row counts.

    The cap on generated data keeps every arm well below dotnet/runtime scale,
    so the projections state their multiplier explicitly. The multiplier is
    taken from identifier counts, the row class the plan quantifies
    (12.86M identifiers at dotnet/runtime scale).
    """
    lines = ["## 4. Projection to dotnet/runtime scale", ""]
    lines.append(
        f"Live Miller artifact: {MILLER_ARTIFACT_IDENTIFIERS:,} identifiers in "
        f"{mb(MILLER_ARTIFACT_BYTES)} MB. Plan's dotnet/runtime benchmark: "
        f"{DOTNET_RUNTIME_IDENTIFIERS:,} identifiers, {gb(DOTNET_RUNTIME_ARTIFACT_BYTES)} GB "
        f"per worktree. Identifier ratio "
        f"{DOTNET_RUNTIME_IDENTIFIERS / MILLER_ARTIFACT_IDENTIFIERS:.1f}x, byte ratio "
        f"{DOTNET_RUNTIME_ARTIFACT_BYTES / MILLER_ARTIFACT_BYTES:.1f}x."
    )
    lines.append("")
    lines.append("| measurement | measured at | synthetic identifiers | multiplier to 12.86M | projected |")
    lines.append("|---|---|---|---|---|")

    if gc:
        arm = next(a for a in gc["arms"] if a["label"] == "inc_retention")
        ids = gc["file_versions"] * 269
        factor = DOTNET_RUNTIME_IDENTIFIERS / ids
        iv = arm["incremental_vacuum"]
        reclaimed = arm["build"]["file_bytes"] - iv["file_bytes"]
        lines.append(
            f"| staged incremental_vacuum, whole freelist | {iv['total_seconds']}s "
            f"reclaiming {mb(reclaimed)} MB | {ids:,} | {factor:.1f}x | "
            f"~{iv['total_seconds'] * factor:.0f}s reclaiming ~{gb(reclaimed * factor)} GB |"
        )
        lines.append(
            f"| one staged vacuum step ({iv['pages_per_stage']:,} pages) | "
            f"max {iv['max_stage_seconds']}s | {ids:,} | 1x (page-bounded) | "
            f"max {iv['max_stage_seconds']}s -- independent of store size |"
        )
        lines.append(
            f"| full VACUUM of the same store | {arm['full_vacuum']['seconds']}s, "
            f"peak needs ~2x {mb(arm['full_vacuum']['file_bytes_before'])} MB | {ids:,} | "
            f"{factor:.1f}x | ~{arm['full_vacuum']['seconds'] * factor:.0f}s, peak needs "
            f"~{gb(2 * arm['full_vacuum']['file_bytes_before'] * factor)} GB |"
        )

    if gran:
        ids = gran["versions_per_import"] * 269
        factor = DOTNET_RUNTIME_IDENTIFIERS / ids
        for mode in gran["modes"]:
            s = mode["summary"]
            seconds = mode["clean"]["report"]["seconds"]
            lines.append(
                f"| cold import, `{mode['mode']}` | {seconds}s, WAL peak "
                f"{mb(s['peak_wal_bytes'])} MB | {ids:,} | {factor:.1f}x | "
                f"~{seconds * factor / 60:.1f} min, WAL peak "
                f"~{gb(s['peak_wal_bytes'] * factor) if mode['mode'] == 'single' else mb(s['peak_wal_bytes'])}"
                f"{' GB' if mode['mode'] == 'single' else ' MB (bounded by the commit unit, not the import)'} |"
            )

    if promo:
        ids = promo["file_versions"] * 269
        factor = DOTNET_RUNTIME_IDENTIFIERS / ids
        for arm in promo["arms"]:
            lines.append(
                f"| promotion peak, `{arm['arm']}` | {mb(arm['peak_family_bytes'])} MB | "
                f"{ids:,} | {factor:.1f}x | ~{gb(arm['peak_family_bytes'] * factor)} GB |"
            )
    lines.append("")
    lines.append(
        "The WAL projection is the load-bearing one: `single` scales its WAL peak "
        "with the whole import, every per-commit-unit mode does not."
    )
    lines.append("")
    return lines


def main() -> int:
    outdir = sys.argv[1]
    lines = ["# Write-side mechanics: measured output", ""]

    gc = load(outdir, "gc.json")
    gran = load(outdir, "granularity.json")
    promo = load(outdir, "promotion.json")

    version_source = gc or gran or promo
    if version_source:
        lines.append(f"SQLite library: {version_source['sqlite_version']}")
        lines.append("")

    if gc:
        lines += gc_section(gc)
    if gran:
        lines += granularity_section(gran)
    if promo:
        lines += promotion_section(promo)
    if gc or gran or promo:
        lines += projection_section(gc, gran, promo)

    print("\n".join(lines))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
