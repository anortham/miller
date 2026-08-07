#!/usr/bin/env python3
"""Store growth model for the versioned index store Ph0 gate.

Counts unique (path, blob-hash) file versions per retention window from real git
history, converts them to store bytes using bytes-per-version measured on a real
julie-extract artifact, and projects dotnet/runtime by file-count scaling.

Method (stated so the numbers are reproducible):

  versions(W) = { (path, blob) in the tree at the window's start commit }
              U { (path, post-image blob) for every ACM change in the window }

  The window walks HEAD's full history (merged branch commits included, merge
  commits contribute no diff of their own), `--no-renames` so a rename reads as
  add + delete, and only paths whose extension julie-extract indexes are counted.
  The baseline tree is the union's floor: a store must hold one version of every
  indexed file just to serve the oldest checkout in the window.
"""

import argparse
import json
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

WINDOWS_WEEKS = (1, 2, 4, 8)

# Mirrors julie-extract's discovery filter so the version count models what the
# extractor actually indexes (`julie-extract-cli/src/discovery.rs:570-594`). The
# 1 MiB `MAX_SOURCE_FILE_BYTES` cap is NOT modelled: blob sizes are not in the
# `--raw` log output, and no tracked markdown/source file in either repo reaches it.
HARD_EXCLUDE_DIRS = {".git", ".hg", ".svn", ".julie", ".memories", "node_modules",
                     "vendor", "target", "dist", "build", ".cache", "obj", "TestResults"}
HARD_EXCLUDE_SUFFIXES = (".min.js", ".bundle.js", ".generated.js", ".generated.jsx",
                         ".generated.ts", ".generated.tsx", ".generated.d.ts")


def indexed(path, exts):
    if Path(path).suffix.lower() not in exts:
        return False
    if any(path.endswith(suffix) for suffix in HARD_EXCLUDE_SUFFIXES):
        return False
    return not any(part in HARD_EXCLUDE_DIRS for part in Path(path).parts[:-1])


# Measured dotnet/runtime facts from docs/findings/2026-08-03-dotnet-runtime-v2231-baseline.md
# (@ a2f953fe266, julie-extract 2.24.0 production validation): 41,406 indexed files,
# 20.41 GiB artifact. Anchoring the projection on a real artifact beats extrapolating
# miller's per-version bytes across a 30x file-count gap.
DOTNET_RUNTIME_INDEXED_FILES = 41406
DOTNET_RUNTIME_ARTIFACT_BYTES = int(20.41 * (1024 ** 3))


def git(repo, *argv, check=True):
    proc = subprocess.run(["git", "-C", str(repo), *argv], capture_output=True, text=True)
    if check and proc.returncode != 0:
        raise RuntimeError(f"git {' '.join(argv)} failed: {proc.stderr[:500]}")
    return proc.stdout


def head_datetime(repo, rev):
    raw = git(repo, "log", "-1", "--format=%cI", rev).strip()
    return datetime.fromisoformat(raw)


def tree_versions(repo, commit, exts):
    out = git(repo, "ls-tree", "-r", "--format=%(objectname) %(path)", commit)
    versions = set()
    for line in out.splitlines():
        if not line:
            continue
        blob, _, path = line.partition(" ")
        if indexed(path, exts):
            versions.add((path, blob))
    return versions


def introduced_versions(repo, since_iso, exts, rev):
    """(path, post-image blob) for every add/copy/modify/rename in the window."""
    out = git(repo, "log", "--since", since_iso, "--no-renames", "--diff-filter=ACMR",
              "--raw", "--no-abbrev", "--pretty=format:%H", rev)
    versions = set()
    commits = 0
    for line in out.splitlines():
        if not line:
            continue
        if not line.startswith(":"):
            commits += 1
            continue
        meta, _, path = line.partition("\t")
        fields = meta.split()
        if len(fields) < 5:
            continue
        dst_blob = fields[3]
        if dst_blob.strip("0") == "":
            continue
        path = path.split("\t")[-1]
        if indexed(path, exts):
            versions.add((path, dst_blob))
    return versions, commits


def branch_divergence(repo, exts, rev, limit=40):
    """Indexed files a merged task branch touched, per merge commit.

    Locates where a real sibling view sits on the binding-cost curve: for each
    merge, diff its merge-base against the branch tip.
    """
    merges = git(repo, "log", "--merges", "-n", str(limit), "--format=%H %P", rev).splitlines()
    samples = []
    for line in merges:
        parts = line.split()
        if len(parts) < 3:
            continue
        merge_sha, p1, p2 = parts[0], parts[1], parts[2]
        base = git(repo, "merge-base", p1, p2, check=False).strip()
        if not base:
            continue
        names = git(repo, "diff", "--name-only", "--no-renames", base, p2, check=False).splitlines()
        changed = [n for n in names if indexed(n, exts)]
        commits = git(repo, "rev-list", "--count", f"{base}..{p2}", check=False).strip() or "0"
        samples.append({"merge": merge_sha[:8], "branch_commits": int(commits),
                        "changed_indexed_files": len(changed)})
    counts = sorted(s["changed_indexed_files"] for s in samples)
    def pct(p):
        if not counts:
            return 0
        return counts[min(len(counts) - 1, int(round((len(counts) - 1) * p)))]
    return {
        "merges_sampled": len(samples),
        "median_changed_indexed_files": pct(0.5),
        "p90_changed_indexed_files": pct(0.9),
        "max_changed_indexed_files": counts[-1] if counts else 0,
        "samples": samples,
    }


def window_report(repo, exts, weeks, head_dt, head_paths, rev):
    start_dt = head_dt - timedelta(weeks=weeks)
    since_iso = start_dt.astimezone(timezone.utc).isoformat()
    base_commit = git(repo, "rev-list", "-1", f"--before={since_iso}", rev).strip()
    baseline = tree_versions(repo, base_commit, exts) if base_commit else set()
    introduced, commits = introduced_versions(repo, since_iso, exts, rev)
    union = baseline | introduced
    new_versions = union - baseline
    baseline_paths = {p for p, _ in baseline}
    union_paths = {p for p, _ in union}
    return {
        "weeks": weeks,
        "since": since_iso,
        "baseline_commit": base_commit,
        "commits_in_window": commits,
        "baseline_versions": len(baseline),
        "baseline_paths": len(baseline_paths),
        "head_indexed_paths": head_paths,
        "new_versions_in_window": len(new_versions),
        "total_versions": len(union),
        "distinct_paths_touched": len(union_paths),
        "versions_per_path": (len(union) / len(union_paths)) if union_paths else 0.0,
        "new_versions_per_week": len(new_versions) / weeks,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--repo", action="append", required=True, metavar="NAME=PATH")
    ap.add_argument("--rev", action="append", default=[], metavar="NAME=SHA",
                    help="pin a repo's analysed tip (default HEAD); keeps the model reproducible "
                         "on a branch other workers are still committing to")
    ap.add_argument("--binding-results", required=True)
    ap.add_argument("--julie", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--projection-files", type=int, default=58500,
                    help="file count of the projected repo (dotnet/runtime)")
    args = ap.parse_args()

    binding = json.loads(Path(args.binding_results).read_text())
    bpv_full = binding["bytes_per_version_full"]
    bpv_l1 = binding["bytes_per_version_level_symbols"]
    bpv_cs = binding["bytes_per_version_csharp_only"]
    l1_share = bpv_l1 / bpv_full
    ref_files = binding["base_artifact"]["files"]
    extra_bpv = {name: facts["bytes_per_version"]
                 for name, facts in binding.get("extra_root_artifacts", {}).items()}

    caps = json.loads(subprocess.run([args.julie, "languages", "--json"],
                                     capture_output=True, text=True, check=True).stdout)
    exts = {"." + ext.lower() for lang in caps["languages"]["languages"] for ext in lang["extensions"]}

    result = {
        "bytes_per_version_full": bpv_full,
        "bytes_per_version_level_symbols": bpv_l1,
        "bytes_per_version_csharp_only": bpv_cs,
        "bytes_per_version_by_repo": extra_bpv,
        "reference_artifact_files": ref_files,
        "reference_artifact_bytes": binding["base_artifact"]["bytes"],
        "indexed_extensions": sorted(exts),
        "projection_files": args.projection_files,
        "projection_bytes_per_version": bpv_cs,
        "projection_bytes_per_version_basis": "C#-only artifact measured on this repo's .cs subset",
        "repos": {},
    }

    revs = dict(spec.partition("=")[::2] for spec in args.rev)

    for spec in args.repo:
        name, _, path = spec.partition("=")
        repo = Path(path)
        rev = revs.get(name, "HEAD")
        head_dt = head_datetime(repo, rev)
        repo_bpv_full = extra_bpv.get(name, result["bytes_per_version_full"])
        entry = {
            "bytes_per_version_full": repo_bpv_full,
            "bytes_per_version_l1": repo_bpv_full * l1_share,
            "bytes_per_version_basis": ("measured on this repo" if name in extra_bpv
                                        else "measured on the miller artifact"),
            "path": str(repo),
            "analysed_rev": git(repo, "rev-parse", rev).strip(),
            "head": git(repo, "rev-parse", "--short", rev).strip(),
            "head_date": head_dt.isoformat(),
            "branch_divergence": branch_divergence(repo, exts, rev),
            "windows": [],
        }
        head_paths = len({p for p, _ in tree_versions(repo, rev, exts)})
        entry["head_indexed_paths"] = head_paths
        for weeks in WINDOWS_WEEKS:
            w = window_report(repo, exts, weeks, head_dt, head_paths, rev)
            w["store_bytes_full"] = w["total_versions"] * repo_bpv_full
            w["store_bytes_l1"] = w["total_versions"] * repo_bpv_full * l1_share
            w["single_index_bytes_full"] = head_paths * repo_bpv_full
            w["overhead_x_full"] = (w["total_versions"] / head_paths) if head_paths else 0.0
            overhead = w["overhead_x_full"]
            # Retention priced two ways. `all_full` keeps every retained version at
            # L1+L2+L3. `l1_history` keeps the live view's versions at full level and
            # every older retained version at L1 only, which is what the levels fold
            # makes possible.
            w["retention_cost_x_all_full"] = overhead
            w["retention_cost_x_l1_history"] = 1.0 + (overhead - 1.0) * l1_share
            dn_bpv = DOTNET_RUNTIME_ARTIFACT_BYTES / DOTNET_RUNTIME_INDEXED_FILES
            w["projection"] = {
                "method": "apply this repo's measured versions-per-indexed-file multiplier "
                          "to a target repo's single-index bytes",
                "overhead_x": overhead,
                "dotnet_runtime_measured": {
                    "indexed_files": DOTNET_RUNTIME_INDEXED_FILES,
                    "bytes_per_version": dn_bpv,
                    "single_index_bytes": DOTNET_RUNTIME_ARTIFACT_BYTES,
                    "store_bytes_all_full": DOTNET_RUNTIME_ARTIFACT_BYTES * overhead,
                    "store_bytes_l1_history": DOTNET_RUNTIME_ARTIFACT_BYTES
                                              * w["retention_cost_x_l1_history"],
                },
                "task_spec_file_count_scaling": {
                    "indexed_files": args.projection_files,
                    "bytes_per_version": bpv_cs,
                    "scale_factor": args.projection_files / head_paths if head_paths else 0.0,
                    "projected_total_versions": w["total_versions"]
                                                * (args.projection_files / head_paths if head_paths else 0.0),
                    "single_index_bytes": args.projection_files * bpv_cs,
                    "store_bytes_all_full": args.projection_files * bpv_cs * overhead,
                    "store_bytes_l1_history": args.projection_files * bpv_cs
                                              * w["retention_cost_x_l1_history"],
                },
            }
            entry["windows"].append(w)
        result["repos"][name] = entry

    Path(args.out).write_text(json.dumps(result, indent=1))

    def gib(n):
        return n / (1024 ** 3)

    b = binding
    print(f"\nbytes/version, miller full artifact  = {b['bytes_per_version_full']:,.0f} B "
          f"({b['base_artifact']['bytes']:,} B / {ref_files:,} files)")
    print(f"bytes/version, --level symbols       = {bpv_l1:,.0f} B "
          f"({b['level_symbols_artifact']['bytes']:,} B / {b['level_symbols_artifact']['files']:,} files) "
          f"= {l1_share:.1%} of full")
    print(f"bytes/version, C#-only corpus        = {bpv_cs:,.0f} B "
          f"({b['csharp_only_artifact']['bytes']:,} B / {b['csharp_only_artifact']['files']:,} files)")
    for name, facts in b.get("extra_root_artifacts", {}).items():
        print(f"bytes/version, {name:<22}= {facts['bytes_per_version']:,.0f} B "
              f"({facts['bytes']:,} B / {facts['files']:,} files)")
    for name, entry in result["repos"].items():
        repo_bpv_full = entry["bytes_per_version_full"]
        repo_bpv_l1 = entry["bytes_per_version_l1"]
        print(f"\n== {name} @ {entry['head']} ({entry['head_date']}) "
              f"[{repo_bpv_full:,.0f} B/version, {entry['bytes_per_version_basis']}] ==")
        bd = entry["branch_divergence"]
        print(f"  sibling-branch divergence over {bd['merges_sampled']} merges: "
              f"median {bd['median_changed_indexed_files']} / p90 {bd['p90_changed_indexed_files']} / "
              f"max {bd['max_changed_indexed_files']} indexed files changed")
        print(f"  indexed files at HEAD: {entry['head_indexed_paths']} "
              f"(1 index = {gib(entry['head_indexed_paths'] * repo_bpv_full):.2f} GiB full / "
              f"{gib(entry['head_indexed_paths'] * repo_bpv_l1):.2f} GiB L1)")
        hdr = (f"{'win':>4} {'commits':>8} {'versions':>9} {'new':>7} {'store_GiB':>10} "
               f"{'allfull_x':>10} {'l1hist_x':>9}")
        print(hdr)
        print("-" * len(hdr))
        for w in entry["windows"]:
            print(f"{w['weeks']:>3}w {w['commits_in_window']:>8} "
                  f"{w['total_versions']:>9} {w['new_versions_in_window']:>7} "
                  f"{gib(w['store_bytes_full']):>10.2f} "
                  f"{w['retention_cost_x_all_full']:>9.2f}x {w['retention_cost_x_l1_history']:>8.2f}x")
        print("  projection — dotnet/runtime measured baseline "
              f"({DOTNET_RUNTIME_INDEXED_FILES:,} indexed files, "
              f"{gib(DOTNET_RUNTIME_ARTIFACT_BYTES):.2f} GiB single index):")
        for w in entry["windows"]:
            d = w["projection"]["dotnet_runtime_measured"]
            print(f"    {w['weeks']:>2}w  all-full {gib(d['store_bytes_all_full']):>7.1f} GiB   "
                  f"L1-history {gib(d['store_bytes_l1_history']):>7.1f} GiB")
        print(f"  projection — task-spec file-count scaling to {args.projection_files:,} files "
              f"at {bpv_cs:,.0f} B/version (C#-only corpus):")
        for w in entry["windows"]:
            t = w["projection"]["task_spec_file_count_scaling"]
            print(f"    {w['weeks']:>2}w  x{t['scale_factor']:>6.1f}  "
                  f"versions {t['projected_total_versions']:>12,.0f}  "
                  f"all-full {gib(t['store_bytes_all_full']):>7.1f} GiB   "
                  f"L1-history {gib(t['store_bytes_l1_history']):>7.1f} GiB   "
                  f"(1 index = {gib(t['single_index_bytes']):.1f} GiB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
