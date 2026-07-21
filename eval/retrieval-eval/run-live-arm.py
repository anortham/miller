#!/usr/bin/env python3
"""Run the LIVE production search arm over a retrieval-eval query set.

This is the sealed-acceptance runner (SEALED-SET-PROTOCOL.md handoff step 3): for every query it
invokes the real `miller search --json` — production routing, fusion, and encoder, no forced arm —
against the query's repo root and writes the retrieval-eval results JSONL. It works identically on
the dev set; nothing here knows which set it is running.

Protocol hygiene: progress output names query_ids only. Query text goes to the miller subprocess
argv and nowhere else; run this yourself for a sealed set — do not paste sealed queries anywhere.

Usage:
  python3 eval/retrieval-eval/run-live-arm.py \
    --queries <set>/queries.jsonl \
    --binary src/Miller.Server/bin/Release/net10.0/miller \
    --corpus miller=/path/to/frozen-miller --corpus julie=/path/to/frozen-julie \
    --out /path/to/results.jsonl [--limit 10]

Every corpus root must already be indexed (`miller workspace open --path <root> --full`) with a
converged vector artifact (run a `MILLER_SEMANTIC=on miller serve` round in the root until
`vectors.db` reports completed==target for both lanes). The runner sets MILLER_SEMANTIC=on for
each search; a query whose repo has no serving vector artifact still answers lexically through
production routing, exactly as the live server would.

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
            queries.append(row)
    ids = [q["query_id"] for q in queries]
    if len(ids) != len(set(ids)):
        sys.exit("duplicate query_id in query set")
    return queries


def run_search(binary, root, query, limit):
    env = dict(os.environ)
    env["MILLER_SEMANTIC"] = "on"
    completed = subprocess.run(
        [binary, "search", query, "--json", "--workspace", root, "--limit", str(limit)],
        capture_output=True,
        text=True,
        env=env,
        timeout=300,
    )
    if completed.returncode != 0:
        raise RuntimeError(
            f"miller search exited {completed.returncode}: {completed.stderr.strip()[:500]}")
    return json.loads(completed.stdout)


def ranked_docs(rows, limit):
    ranked = []
    seen = set()
    for row in rows:
        doc = row.get("file")
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
    missing_repos = sorted({q["repo"] for q in queries} - roots.keys())
    if missing_repos:
        sys.exit(f"query set references repos with no --corpus mapping: {', '.join(missing_repos)}")

    args.out.parent.mkdir(parents=True, exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as out:
        for index, query in enumerate(queries, start=1):
            try:
                rows = run_search(str(args.binary), roots[query["repo"]], query["query"], args.limit)
            except (RuntimeError, subprocess.TimeoutExpired, json.JSONDecodeError) as error:
                sys.exit(f"[{index}/{len(queries)}] {query['query_id']}: {error}")
            out.write(json.dumps(
                {"query_id": query["query_id"], "ranked": ranked_docs(rows, args.limit)},
                separators=(",", ":")) + "\n")
            print(f"[{index}/{len(queries)}] {query['query_id']}: {len(rows)} rows", file=sys.stderr)

    print(f"wrote {len(queries)} results rows to {args.out}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
