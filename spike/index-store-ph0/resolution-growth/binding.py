#!/usr/bin/env python3
"""Resolution binding-cost curve for the versioned index store Ph0 gate.

Measures whole-repo `julie-extract scan` cost at a range of changed-file counts
against a fixed base artifact, and compares each against a full pass. Every row
records julie-extract's own `profile.phases` plus wall clock.

Full-vs-Delta discriminator: `languages.reference_resolution.by_language` in the
scan report. julie computes that workspace-wide aggregate only on a pass that
re-derived the whole workspace (`resolution.rs:1707`), so a populated value means
Full and a null means the scoped Delta branch ran. The artifact metadata key
`reference_resolution_last_full_revision` CANNOT be used: every whole-repo scan
sets `whole_corpus: true`, which makes `corpus_current` true and stamps the
current revision on a scoped pass too (`resolution.rs:1718`, `:1733`).

`reference_resolution.counts.identifier_resolutions` is the pass's re-derived row
count — the honest measure of how far the delta scope widened, independent of
wall clock.
"""

import argparse
import json
import os
import shutil
import sqlite3
import subprocess
import sys
import time
from pathlib import Path

INDEXED_EXTS_FALLBACK = {".cs", ".md", ".json", ".py", ".razor", ".yaml", ".yml", ".sh", ".ps1", ".js", ".css", ".html"}


def run_scan(julie, root, db, jobs, report_path, force=False, level=None):
    argv = [str(julie), "scan", "--root", str(root), "--db", str(db), "--jobs", str(jobs), "--json"]
    if force:
        argv.append("--force")
    if level:
        argv += ["--level", level]
    started = time.monotonic()
    proc = subprocess.run(argv, capture_output=True, text=True)
    wall_ms = int((time.monotonic() - started) * 1000)
    if proc.returncode != 0:
        raise RuntimeError(f"scan failed ({proc.returncode}): {proc.stderr[:2000]}")
    report = json.loads(proc.stdout)
    report_path.write_text(json.dumps(report, indent=1))
    return report, wall_ms, argv


def artifact_facts(db):
    conn = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    try:
        meta = dict(conn.execute("SELECT key, value FROM artifact_metadata").fetchall())
        rev = conn.execute("SELECT MAX(revision_id) FROM extraction_revisions").fetchone()[0]
        files = conn.execute("SELECT COUNT(*) FROM files").fetchone()[0]
        idents = conn.execute("SELECT COUNT(*) FROM identifiers").fetchone()[0]
        symbols = conn.execute("SELECT COUNT(*) FROM symbols").fetchone()[0]
    finally:
        conn.close()
    return {
        "latest_revision": rev,
        "last_full_revision": int(meta.get("reference_resolution_last_full_revision", -1)),
        "resolution_status": meta.get("reference_resolution_status"),
        "resolution_version": meta.get("reference_resolution_version"),
        "artifact_id": meta.get("artifact_id"),
        "files": files,
        "identifiers": idents,
        "symbols": symbols,
        "bytes": os.path.getsize(db),
    }


def indexed_paths(db):
    conn = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    try:
        rows = conn.execute("SELECT path, language FROM files ORDER BY path").fetchall()
    finally:
        conn.close()
    return rows


def table_bytes(db):
    conn = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    try:
        rows = conn.execute("SELECT name, SUM(pgsize) FROM dbstat GROUP BY name ORDER BY 2 DESC").fetchall()
        page_size = conn.execute("PRAGMA page_size").fetchone()[0]
        page_count = conn.execute("PRAGMA page_count").fetchone()[0]
    finally:
        conn.close()
    return {"tables": rows, "page_size": page_size, "page_count": page_count}


def touch(fixture, rel_paths):
    for rel in rel_paths:
        with open(fixture / rel, "ab") as handle:
            handle.write(b"\n")


def restore(fixture, pristine, rel_paths):
    for rel in rel_paths:
        shutil.copyfile(pristine / rel, fixture / rel)


def clone_db(base, work):
    for suffix in ("", "-wal", "-shm"):
        target = Path(str(work) + suffix)
        if target.exists():
            target.unlink()
    # APFS clonefile when available; plain copy otherwise.
    if subprocess.run(["cp", "-c", str(base), str(work)], capture_output=True).returncode != 0:
        shutil.copyfile(base, work)


def drop_db(work):
    for suffix in ("", "-wal", "-shm"):
        target = Path(str(work) + suffix)
        if target.exists():
            target.unlink()


def extract_tree(repo, rev, dest):
    if dest.exists():
        shutil.rmtree(dest)
    dest.mkdir(parents=True)
    archive = subprocess.Popen(["git", "-C", str(repo), "archive", rev], stdout=subprocess.PIPE)
    subprocess.run(["tar", "-x", "-C", str(dest)], stdin=archive.stdout, check=True)
    archive.wait()


def pass_kind(report):
    langs = report.get("languages") or {}
    rr = langs.get("reference_resolution") if isinstance(langs, dict) else None
    if rr is None:
        return "none", 0
    return ("Full" if rr.get("by_language") else "Delta"), rr["counts"]["identifier_resolutions"]


def sibling_bind(julie, repo, base_rev, tip_rev, scratch, jobs, reports):
    """Bind a base view's artifact to a real sibling branch tip and time it.

    This is the program's central claim under test: an artifact built at the merge
    base, retargeted at the branch tip by one whole-repo scan. Compared against
    building the tip from scratch.
    """
    root = scratch / "sibling"
    extract_tree(repo, base_rev, root)
    base_db = scratch / "sibling-base.db"
    drop_db(base_db)
    base_report, base_wall, base_argv = run_scan(julie, root, base_db, jobs,
                                                 reports / "sibling_base_full.json")
    base_facts = artifact_facts(base_db)

    changed = subprocess.run(["git", "-C", str(repo), "diff", "--name-status", "--no-renames",
                              base_rev, tip_rev], capture_output=True, text=True, check=True).stdout

    extract_tree(repo, tip_rev, scratch / "sibling-tip")
    subprocess.run(["rsync", "-a", "--delete", f"{scratch / 'sibling-tip'}/", f"{root}/"], check=True)

    work = scratch / "sibling-work.db"
    clone_db(base_db, work)
    report, wall, argv = run_scan(julie, root, work, jobs, reports / "sibling_bind_delta.json")
    kind, res_rows = pass_kind(report)
    bind_facts = artifact_facts(work)
    drop_db(work)

    scratch_db = scratch / "sibling-tip.db"
    drop_db(scratch_db)
    tip_report, tip_wall, tip_argv = run_scan(julie, root, scratch_db, jobs,
                                              reports / "sibling_tip_full.json")
    tip_kind, tip_rows = pass_kind(tip_report)
    tip_facts = artifact_facts(scratch_db)
    drop_db(scratch_db)
    drop_db(base_db)
    shutil.rmtree(root)
    shutil.rmtree(scratch / "sibling-tip")

    base_kind, base_rows = pass_kind(base_report)
    return {
        "base_rev": base_rev,
        "tip_rev": tip_rev,
        "changed_paths_in_diff": len([l for l in changed.splitlines() if l.strip()]),
        "base_full_build": {"total_duration_ms": base_report["profile"]["total_duration_ms"],
                            "resolution_ms": base_report["profile"]["phases"].get("artifact_write_resolution", 0),
                            "wall_ms": base_wall, "files": base_facts["files"],
                            "identifiers": base_facts["identifiers"], "bytes": base_facts["bytes"],
                            "resolution_pass": base_kind, "resolution_rows_rederived": base_rows,
                            "argv": base_argv},
        "bind_delta": {"total_duration_ms": report["profile"]["total_duration_ms"],
                       "resolution_ms": report["profile"]["phases"].get("artifact_write_resolution", 0),
                       "extraction_spool_ms": report["profile"]["phases"].get("extraction_spool", 0),
                       "wall_ms": wall, "files_changed": report["counts"]["files_changed"],
                       "files_deleted": report["counts"]["files_deleted"],
                       "resolution_pass": kind, "resolution_rows_rederived": res_rows,
                       "identifiers": bind_facts["identifiers"], "bytes": bind_facts["bytes"],
                       "argv": argv},
        "tip_full_build": {"total_duration_ms": tip_report["profile"]["total_duration_ms"],
                           "resolution_ms": tip_report["profile"]["phases"].get("artifact_write_resolution", 0),
                           "wall_ms": tip_wall, "files": tip_facts["files"],
                           "identifiers": tip_facts["identifiers"], "bytes": tip_facts["bytes"],
                           "resolution_pass": tip_kind, "resolution_rows_rederived": tip_rows,
                           "argv": tip_argv},
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--scratch", required=True)
    ap.add_argument("--julie", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--jobs", type=int, default=4)
    ap.add_argument("--repo-head", default="unknown")
    ap.add_argument("--extra-root", action="append", default=[], metavar="NAME=PATH",
                    help="extra source root to scan for a corpus-specific bytes-per-version")
    ap.add_argument("--repo-path", help="repo to take the real sibling-branch bind pair from")
    ap.add_argument("--sibling-base", help="merge-base commit — the base view's tree")
    ap.add_argument("--sibling-tip", help="branch tip commit — the new view's tree")
    args = ap.parse_args()

    scratch = Path(args.scratch)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    reports = out / "reports"
    if reports.exists():
        shutil.rmtree(reports)
    reports.mkdir(parents=True)
    fixture = scratch / "fixture"
    pristine = scratch / "pristine"
    julie = Path(args.julie).resolve()

    version = subprocess.run([str(julie), "--version"], capture_output=True, text=True).stdout.strip()
    rows = []

    base_db = scratch / "base.db"
    drop_db(base_db)
    print("[1/n] full pass — from-scratch build (bulk path)", flush=True)
    report, wall, argv = run_scan(julie, fixture, base_db, args.jobs, reports / "full-from-scratch.json")
    facts = artifact_facts(base_db)
    rows.append({"row": "full_from_scratch", "changed": facts["files"], "report": report, "wall_ms": wall,
                 "facts": facts, "argv": argv})
    base_facts = dict(facts)
    total_files = facts["files"]

    all_indexed = [p for p, _lang in indexed_paths(base_db)]
    cs_indexed = [p for p in all_indexed if p.endswith(".cs")]
    md_indexed = [p for p in all_indexed if p.endswith(".md")]
    crossover_at = total_files * 0.7

    plan = []
    for n in (0, 1, 5, 25, 120):
        plan.append(("delta_cs_%d" % n, cs_indexed[:n]))
    plan.append(("delta_cs_1_repeat", cs_indexed[:1]))
    plan.append(("delta_md_1", md_indexed[:1]))
    plan.append(("delta_cs_all_%d" % len(cs_indexed), cs_indexed))
    below = int(crossover_at) if int(crossover_at) < crossover_at else int(crossover_at) - 1
    plan.append(("delta_mixed_%d_below_crossover" % below, all_indexed[:below]))
    plan.append(("delta_mixed_%d_at_crossover" % (below + 1), all_indexed[:below + 1]))

    for idx, (label, paths) in enumerate(plan, start=2):
        print(f"[{idx}/{len(plan) + 3}] {label} ({len(paths)} changed)", flush=True)
        work = scratch / "work.db"
        clone_db(base_db, work)
        touch(fixture, paths)
        try:
            report, wall, argv = run_scan(julie, fixture, work, args.jobs, reports / f"{label}.json")
            facts = artifact_facts(work)
        finally:
            restore(fixture, pristine, paths)
        rows.append({"row": label, "changed": len(paths), "report": report, "wall_ms": wall,
                     "facts": facts, "argv": argv})
        drop_db(work)

    print("[n-2] full pass — --force on the populated base clone", flush=True)
    work = scratch / "work.db"
    clone_db(base_db, work)
    report, wall, argv = run_scan(julie, fixture, work, args.jobs, reports / "full_force_populated.json", force=True)
    facts = artifact_facts(work)
    rows.append({"row": "full_force_populated", "changed": facts["files"], "report": report, "wall_ms": wall,
                 "facts": facts, "argv": argv})
    drop_db(work)

    print("[n-1] structure change — 1 rewrite + 1 added file", flush=True)
    work = scratch / "work.db"
    clone_db(base_db, work)
    added = fixture / "ph0_added_probe.cs"
    added.write_text("namespace Ph0Probe;\npublic static class Ph0Added { public static int Value => 1; }\n")
    touch(fixture, cs_indexed[:1])
    try:
        report, wall, argv = run_scan(julie, fixture, work, args.jobs, reports / "delta_structure_change.json")
        facts = artifact_facts(work)
    finally:
        restore(fixture, pristine, cs_indexed[:1])
        added.unlink(missing_ok=True)
    rows.append({"row": "delta_structure_change_1rewrite_1add", "changed": 2, "report": report, "wall_ms": wall,
                 "facts": facts, "argv": argv})
    drop_db(work)

    sibling = None
    if args.repo_path and args.sibling_base and args.sibling_tip:
        print(f"[n-0.7] real sibling bind — {args.sibling_base[:8]} -> {args.sibling_tip[:8]}", flush=True)
        sibling = sibling_bind(julie, Path(args.repo_path), args.sibling_base, args.sibling_tip,
                               scratch, args.jobs, reports)

    print("[n-0.5] C#-only bytes probe — from-scratch build over the .cs subset", flush=True)
    cs_root = scratch / "fixture-cs"
    if cs_root.exists():
        shutil.rmtree(cs_root)
    for rel in cs_indexed:
        target = cs_root / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(fixture / rel, target)
    cs_db = scratch / "cs.db"
    drop_db(cs_db)
    cs_report, cs_wall, cs_argv = run_scan(julie, cs_root, cs_db, args.jobs, reports / "full_csharp_only.json")
    cs_facts = artifact_facts(cs_db)
    cs_facts["total_duration_ms"] = cs_report["profile"]["total_duration_ms"]
    cs_facts["argv"] = cs_argv
    drop_db(cs_db)
    shutil.rmtree(cs_root)

    extra = {}
    for spec in args.extra_root:
        name, _, path = spec.partition("=")
        root = Path(path)
        if not root.exists():
            print(f"  skipping extra root {name}: {root} missing", flush=True)
            continue
        print(f"[n-0.4] corpus bytes probe — {name} ({root})", flush=True)
        edb = scratch / f"extra-{name}.db"
        drop_db(edb)
        ereport, ewall, eargv = run_scan(julie, root, edb, args.jobs, reports / f"full_extra_{name}.json")
        efacts = artifact_facts(edb)
        efacts["root"] = str(root)
        efacts["total_duration_ms"] = ereport["profile"]["total_duration_ms"]
        efacts["wall_ms"] = ewall
        efacts["argv"] = eargv
        efacts["bytes_per_version"] = efacts["bytes"] / efacts["files"]
        extra[name] = efacts
        drop_db(edb)

    print("[n] L1 bytes probe — from-scratch build at --level symbols", flush=True)
    l1_db = scratch / "l1.db"
    drop_db(l1_db)
    l1_report, l1_wall, l1_argv = run_scan(julie, fixture, l1_db, args.jobs, reports / "full_level_symbols.json",
                                           level="symbols")
    l1_facts = artifact_facts(l1_db)
    l1_facts["total_duration_ms"] = l1_report["profile"]["total_duration_ms"]
    l1_facts["wall_ms"] = l1_wall
    l1_facts["argv"] = l1_argv
    l1_bytes_facts = table_bytes(l1_db)
    drop_db(l1_db)

    bytes_facts = table_bytes(base_db)

    summary = {
        "julie_extract_version": version,
        "jobs": args.jobs,
        "fixture_repo_head": args.repo_head,
        "fixture_root": str(fixture),
        "total_indexed_files": total_files,
        "cs_indexed_files": len(cs_indexed),
        "crossover_ratio": 0.7,
        "crossover_files": crossover_at,
        "base_artifact": base_facts,
        "base_artifact_table_bytes": bytes_facts,
        "level_symbols_artifact": l1_facts,
        "level_symbols_table_bytes": l1_bytes_facts,
        "sibling_bind": sibling,
        "csharp_only_artifact": cs_facts,
        "extra_root_artifacts": extra,
        "bytes_per_version_full": base_facts["bytes"] / base_facts["files"],
        "bytes_per_version_level_symbols": l1_facts["bytes"] / l1_facts["files"],
        "bytes_per_version_csharp_only": cs_facts["bytes"] / cs_facts["files"],
        "rows": [],
    }
    total_identifiers = base_facts["identifiers"]
    for row in rows:
        phases = row["report"]["profile"]["phases"]
        counts = row["report"]["counts"]
        langs = row["report"].get("languages") or {}
        rr = langs.get("reference_resolution") if isinstance(langs, dict) else None
        if rr is None:
            pass_kind, res_rows = "none", 0
        else:
            pass_kind = "Full" if rr.get("by_language") else "Delta"
            res_rows = rr["counts"]["identifier_resolutions"]
        summary["rows"].append({
            "row": row["row"],
            "changed_files_requested": row["changed"],
            "files_changed_reported": counts["files_changed"],
            "wall_ms": row["wall_ms"],
            "total_duration_ms": row["report"]["profile"]["total_duration_ms"],
            "resolution_ms": phases.get("artifact_write_resolution", 0),
            "extraction_spool_ms": phases.get("extraction_spool", 0),
            "artifact_write_ms": phases.get("artifact_write", 0),
            "child_rows_ms": phases.get("artifact_write_child_rows", 0),
            "index_build_ms": phases.get("artifact_write_index_build", 0),
            "commit_ms": phases.get("artifact_write_commit", 0),
            "latest_revision": row["facts"]["latest_revision"],
            "resolution_pass": pass_kind,
            "resolution_rows_rederived": res_rows,
            "resolution_rows_share": res_rows / total_identifiers if total_identifiers else 0.0,
            "artifact_bytes": row["facts"]["bytes"],
            "argv": row["argv"],
        })

    (out / "binding-results.json").write_text(json.dumps(summary, indent=1))

    hdr = (f"{'row':<38} {'chg':>5} {'total_ms':>9} {'reso_ms':>8} {'extr_ms':>8} "
           f"{'pass':>5} {'rederived':>10} {'share':>7} {'vs_full':>8}")
    print()
    print(hdr)
    print("-" * len(hdr))
    full_ms = summary["rows"][0]["total_duration_ms"]
    for r in summary["rows"]:
        ratio = r["total_duration_ms"] / full_ms
        print(f"{r['row']:<38} {r['changed_files_requested']:>5} {r['total_duration_ms']:>9} "
              f"{r['resolution_ms']:>8} {r['extraction_spool_ms']:>8} {r['resolution_pass']:>5} "
              f"{r['resolution_rows_rederived']:>10,} {r['resolution_rows_share']:>6.1%} {ratio:>7.2f}x")

    if sibling:
        s = sibling
        print(f"\nreal sibling bind {s['base_rev'][:8]} -> {s['tip_rev'][:8]} "
              f"({s['changed_paths_in_diff']} paths in the git diff)")
        for label in ("base_full_build", "bind_delta", "tip_full_build"):
            e = s[label]
            print(f"  {label:<16} total {e['total_duration_ms']:>7} ms  "
                  f"resolution {e['resolution_ms']:>7} ms  {e['resolution_pass']:>5}  "
                  f"rederived {e['resolution_rows_rederived']:>10,}")
        saving = 1 - s["bind_delta"]["total_duration_ms"] / s["tip_full_build"]["total_duration_ms"]
        print(f"  bind vs building the tip from scratch: {saving:+.1%}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
