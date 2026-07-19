#!/usr/bin/env python3
"""BM25 baseline arm — Miller's real lexical search over the dev golden set.

Throwaway bench tooling, not product code.

Shells out to the built `miller` CLI (`search --json`) once per query, one
invocation per query against the query's own repo, and maps hits into the
golden set's doc_id space (the CLI's `file` field is already the repo-relative
path, so the mapping is identity after exclusion filtering).

**Exclusion parity is load-bearing.** The live miller index covers
`.claude/worktrees/**`, which contains both the golden set and this benchmark's
own source. Without the same exclusions the semantic corpus applies, the
baseline retrieves the answer key (observed: the top hit for a promotion query
was this harness's own SANITY_PAIRS literal). Both arms must compete over the
same doc space or the comparison is meaningless.
"""

import argparse
import json
import subprocess
import sys
from pathlib import Path

EXCLUDED_PREFIXES = ("eval/", ".razorback/", ".claude/")
EXCLUDED_FRAGMENTS = ("/obj/", "/bin/", "/node_modules/", "/target/")
EXCLUDED_DIR_PREFIXES = (".miller/", "node_modules/", "target/", "docs/site/")


def excluded(path: str) -> bool:
    if path.startswith(EXCLUDED_PREFIXES) or path.startswith(EXCLUDED_DIR_PREFIXES):
        return True
    return any(frag in "/" + path for frag in EXCLUDED_FRAGMENTS)


def read_queries(path: Path):
    out = []
    for line in path.read_text().splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            out.append(json.loads(line))
    return out


def search(miller: Path, workspace: str, query: str, mode: str, limit: int):
    proc = subprocess.run(
        [str(miller), "search", query, "--workspace", workspace,
         "--mode", mode, "--limit", str(limit), "--json"],
        capture_output=True, text=True, timeout=180,
    )
    if proc.returncode != 0:
        print(f"  warn: search failed ({proc.returncode}): {proc.stderr.strip()[:200]}", file=sys.stderr)
        return []
    body = proc.stdout.strip()
    if not body:
        return []
    try:
        hits = json.loads(body)
    except json.JSONDecodeError:
        print(f"  warn: unparseable JSON for query: {query[:60]}", file=sys.stderr)
        return []
    return hits if isinstance(hits, list) else hits.get("results", [])


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--miller", type=Path, required=True)
    ap.add_argument("--queries", type=Path, required=True)
    ap.add_argument("--repo", action="append", required=True, metavar="NAME=DIR")
    ap.add_argument("--out", type=Path, required=True)
    ap.add_argument("--mode", default="symbol")
    ap.add_argument("--k", type=int, default=10)
    # Over-fetch so exclusion filtering still leaves k candidates.
    ap.add_argument("--fetch", type=int, default=60)
    ap.add_argument("--ratio", type=float, default=0.0,
                    help="keep hits scoring >= ratio * top hit (0 disables; the CLI's "
                         "ranked top-k is already what Miller would show a user)")
    args = ap.parse_args()

    roots = dict(spec.partition("=")[::2] for spec in args.repo)
    queries = read_queries(args.queries)

    rows, stats = [], {"empty": 0, "filtered_hits": 0, "total_hits": 0}
    for q in queries:
        workspace = roots[q["repo"]]
        hits = search(args.miller, workspace, q["query"], args.mode, args.fetch)
        stats["total_hits"] += len(hits)

        best = {}
        for hit in hits:
            path = hit.get("file")
            if not path or excluded(path):
                stats["filtered_hits"] += 1
                continue
            score = float(hit.get("score", 0.0))
            if score > best.get(path, float("-inf")):
                best[path] = score

        ordered = sorted(best.items(), key=lambda kv: (-kv[1], kv[0]))[: args.k]
        if ordered and args.ratio > 0:
            top = ordered[0][1]
            ordered = [(d, s) for d, s in ordered if s >= top * args.ratio]
        ranked = [d for d, _ in ordered]
        if not ranked:
            stats["empty"] += 1
        rows.append({"query_id": q["query_id"], "ranked": ranked})

    args.out.parent.mkdir(parents=True, exist_ok=True)
    with args.out.open("w") as fh:
        for r in rows:
            fh.write(json.dumps(r) + "\n")

    print(f"{args.out.name}: {len(rows)} queries, mode={args.mode}, "
          f"{stats['filtered_hits']}/{stats['total_hits']} hits filtered by exclusions, "
          f"{stats['empty']} empty")
    return 0


if __name__ == "__main__":
    sys.exit(main())
