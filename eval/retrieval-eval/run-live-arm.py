#!/usr/bin/env python3
"""Run a live Miller search arm over a retrieval-eval query set.

For every query, this invokes the real `miller search --json` against the query's repo root and
writes retrieval-eval results JSONL. The default `production` arm uses normal routing, fusion, and
encoder behavior with no forced arm. The explicit arms are intended for development evaluation;
semantic/hybrid labels use normal production routing for content and source because Miller exposes
forced arms only for symbol routes. The sealed-acceptance protocol must use the production arm.

Protocol hygiene: progress output names query_ids only. Query text goes to the miller subprocess
argv and nowhere else; run this yourself for a sealed set — do not paste sealed queries anywhere.

Usage:
  python3 eval/retrieval-eval/run-live-arm.py \
    --queries <set>/queries.jsonl \
    --binary src/Miller.Server/bin/Release/net10.0/miller \
    --corpus miller=/path/to/frozen-miller --corpus julie=/path/to/frozen-julie \
    --out /path/to/results.jsonl [--limit 10] [--arm production|lexical|semantic|hybrid] \
    [--latency-out /path/to/latency.jsonl]

Every corpus root must already be indexed (`miller workspace open --path <root> --full`). Semantic,
hybrid, and production evaluation also require a converged vector artifact (run a
`MILLER_SEMANTIC=on miller serve` round in the root until `vectors.db` reports completed==target
for both lanes). Lexical evaluation sets `MILLER_SEMANTIC=off`; all other arms set it on. A
production query whose repo has no serving vector artifact still answers lexically, exactly as the
live server would. The runner disables randomized canary assignment and clears any model override,
so the production arm uses the binary's pinned default deterministically.

One results row is written per query — an empty `ranked` list is the arm's honest "nothing shown"
(what the negatives metric needs), never a silently missing row. A miller invocation that FAILS
(nonzero exit) aborts the run loudly instead of scoring a broken arm.
"""

import argparse
import json
import os
import pathlib
import subprocess
import sys
import time


SEARCH_MODES = ("auto", "symbol", "file", "content", "source")
FORCED_ARM_SEARCH_MODES = ("auto", "symbol", "file")


def read_queries(path):
    queries = []
    with open(path, encoding="utf-8") as handle:
        for line_number, line in enumerate(handle, start=1):
            text = line.strip()
            if not text or text.startswith("#"):
                continue
            row = json.loads(text)
            for field in ("query_id", "query", "repo"):
                if field not in row:
                    sys.exit(f"{path}:{line_number}: query row missing '{field}'")
            search_mode = row.setdefault("search_mode", "auto")
            if search_mode not in SEARCH_MODES:
                sys.exit(
                    f"{path}:{line_number}: search_mode '{search_mode}' is not in the enum "
                    f"({'|'.join(SEARCH_MODES)})"
                )
            queries.append(row)
    ids = [q["query_id"] for q in queries]
    if len(ids) != len(set(ids)):
        sys.exit("duplicate query_id in query set")
    return queries


def run_search(binary, root, query, search_mode, limit, arm):
    env = dict(os.environ)
    env["MILLER_SEMANTIC"] = "off" if arm == "lexical" else "on"
    env["MILLER_SEMANTIC_CANARY"] = "off"
    env.pop("MILLER_SEMANTIC_MODEL", None)
    command = [
        binary,
        "search",
        query,
        "--mode",
        search_mode,
        "--json",
        "--workspace",
        root,
        "--limit",
        str(limit),
    ]
    if arm != "production" and (arm == "lexical" or search_mode in FORCED_ARM_SEARCH_MODES):
        command.extend(["--arm", arm])
    started = time.perf_counter()
    completed = subprocess.run(
        command,
        capture_output=True,
        text=True,
        env=env,
        timeout=300,
    )
    duration_ms = round((time.perf_counter() - started) * 1000, 3)
    if completed.returncode != 0:
        raise RuntimeError(
            f"miller search exited {completed.returncode}: {completed.stderr.strip()[:500]}")
    return json.loads(completed.stdout), duration_ms


def ranked_docs(rows, limit):
    ranked = []
    seen = set()
    for row in rows:
        doc = row.get("file") or row.get("path")
        if not doc or doc in seen:
            continue
        seen.add(doc)
        ranked.append(doc)
        if len(ranked) >= limit:
            break
    return ranked


def main():
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--queries", type=pathlib.Path, required=True)
    ap.add_argument("--binary", type=pathlib.Path, required=True)
    ap.add_argument("--corpus", action="append", required=True,
                    metavar="REPO=ROOT", help="repeatable; maps a query-set repo to its indexed root")
    ap.add_argument("--out", type=pathlib.Path, required=True)
    ap.add_argument("--limit", type=int, default=10)
    ap.add_argument("--arm", choices=("production", "lexical", "semantic", "hybrid"),
                    default="production")
    ap.add_argument("--repo", help="run only query rows for this repo; useful for staged corpus replay")
    ap.add_argument("--latency-out", type=pathlib.Path,
                    help="optional JSONL of per-query CLI wall time; contains query_id, arm, duration_ms")
    args = ap.parse_args()

    roots = {}
    for pair in args.corpus:
        repo, sep, root = pair.partition("=")
        if not sep or not repo or not root:
            sys.exit(f"--corpus must be REPO=ROOT, got '{pair}'")
        if not pathlib.Path(root).is_dir():
            sys.exit(f"corpus root for '{repo}' is not a directory: {root}")
        roots[repo] = root

    queries = read_queries(args.queries)
    if args.repo:
        queries = [query for query in queries if query["repo"] == args.repo]
        if not queries:
            sys.exit(f"query set contains no rows for repo '{args.repo}'")
    missing_repos = sorted({q["repo"] for q in queries} - roots.keys())
    if missing_repos:
        sys.exit(f"query set references repos with no --corpus mapping: {', '.join(missing_repos)}")

    args.out.parent.mkdir(parents=True, exist_ok=True)
    latency_rows = []
    with open(args.out, "w", encoding="utf-8") as out:
        for index, query in enumerate(queries, start=1):
            try:
                rows, duration_ms = run_search(
                    str(args.binary),
                    roots[query["repo"]],
                    query["query"],
                    query["search_mode"],
                    args.limit,
                    args.arm,
                )
            except (RuntimeError, subprocess.TimeoutExpired, json.JSONDecodeError) as error:
                sys.exit(f"[{index}/{len(queries)}] {query['query_id']}: {error}")
            out.write(json.dumps(
                {"query_id": query["query_id"], "ranked": ranked_docs(rows, args.limit)},
                separators=(",", ":")) + "\n")
            latency_rows.append({
                "query_id": query["query_id"],
                "arm": args.arm,
                "duration_ms": duration_ms,
            })
            print(f"[{index}/{len(queries)}] {query['query_id']}: {len(rows)} rows", file=sys.stderr)

    if args.latency_out:
        args.latency_out.parent.mkdir(parents=True, exist_ok=True)
        with open(args.latency_out, "w", encoding="utf-8") as latency_out:
            for row in latency_rows:
                latency_out.write(json.dumps(row, separators=(",", ":")) + "\n")

    print(f"wrote {len(queries)} results rows to {args.out}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
