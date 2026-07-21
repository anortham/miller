#!/usr/bin/env python3
"""Build the benchmark retrieval corpus from a workspace's Miller artifacts.

Throwaway bench tooling, not product code. Reads `.miller/symbols.db` and
`.miller/content.db` read-only and emits one JSONL row per embeddable unit:

    {"unit_id", "doc_id", "repo", "kind", "language", "text"}

`doc_id` is the repo-relative file path, matching the dev golden set's doc_id
vocabulary (verified: the dev set carries zero `#Symbol` suffixes, so ranking is
file-granular and many units collapse onto one doc_id).

Symbol card text follows design §5.2 v1:
    {kind} {qualified name} {signature first line} {doc excerpt <=300} in: {container} {path}
"""

import argparse
import json
import re
import sqlite3
import sys
from pathlib import Path

CARD_BUDGET = 1200
DOC_EXCERPT_BUDGET = 300
CHUNK_BUDGET = 1200
CHUNK_OVERLAP = 200

INELIGIBLE_KINDS = {"import", "enum_member"}

# Design §5.2: card eligibility is symbol-kind/data-driven, NOT a language
# blocklist. A language earns symbol cards only if a real extract shows it emits
# at least one *code* kind. Languages that emit only data-structure kinds
# (json config keys, markdown headings, toml/yaml tables) produce cards that
# restate a path — their text is already covered by the doc/config chunk corpus.
# The resulting matrix is published in the corpus manifest as evidence.
CODE_KINDS = {
    "function", "method", "class", "struct", "interface", "enum",
    "constructor", "delegate", "type", "trait", "union",
}

# The golden set lives inside the miller workspace and names its own answer
# paths. A corpus containing it would retrieve the ground truth and inflate
# every arm. These prefixes are excluded from every corpus, unconditionally.
GOLDEN_SET_EXCLUSIONS = ("eval/", ".razorback/", ".claude/")

# Benchmark-derived docs that name the dev set's graded answer paths. A plan or
# findings doc that enumerates the answer files is a leaked cheat sheet: a
# semantic arm can match a query to that doc's chunk, so the answer key must not
# sit in the corpus. This list was frozen at miller main HEAD 59c2c79 (spec R1)
# by grepping every graded `doc_id` from
# `eval/retrieval-eval/sets/dev/queries.jsonl` against `docs/` — the five
# design/findings docs the plan names, plus every other `docs/` file whose text
# contains at least one graded doc_id. miller-only: julie's graded answer docs
# live under the same relative paths in a different repo, so these apply to the
# miller corpus alone (guarded by repo in `excluded`). Regenerate and re-freeze
# if the frozen SHA or the dev set changes.
BENCHMARK_DOC_EXCLUSIONS = (
    "docs/contracts/canary-telemetry-v1.md",
    "docs/contracts/semantic-sidecar-protocol-v1.md",
    "docs/contracts/vectors-v1.md",
    "docs/findings/2026-06-05-julie-side-by-side-audit.md",
    "docs/findings/2026-06-05-tool-output-token-savings.md",
    "docs/findings/2026-06-23-1.0-readiness-review.md",
    "docs/findings/2026-07-07-dead-code-candidates-dogfood.md",
    "docs/findings/2026-07-19-model-benchmark.md",
    "docs/findings/2026-07-21-fused-arm-encoder-benchmark.md",
    "docs/findings/benchmarks/2026-06-27-foundation-matrix/final-baseline/results.csv",
    "docs/findings/benchmarks/2026-06-27-foundation-matrix/final-baseline/results.json",
    "docs/findings/benchmarks/2026-06-27-foundation-matrix/search-inspect-recovery-hardening/results.csv",
    "docs/findings/benchmarks/2026-06-27-foundation-matrix/search-inspect-recovery-hardening/results.json",
    "docs/findings/benchmarks/2026-06-27-foundation-matrix/task3-retrieval-inspect-ambiguity/results.csv",
    "docs/findings/benchmarks/2026-06-27-foundation-matrix/task3-retrieval-inspect-ambiguity/results.json",
    "docs/plans/2026-05-31-workspace-registry-freshness-plan.md",
    "docs/plans/2026-06-01-julie-extractors-migration-plan.md",
    "docs/plans/2026-06-04-cli-workspace-open-remove-design.md",
    "docs/plans/2026-06-04-symbol-search-collapsed-trigram-design.md",
    "docs/plans/2026-06-05-source-regions-pillar3-implementation-plan.md",
    "docs/plans/2026-06-07-content-corpus-fts5-search-plan.md",
    "docs/plans/2026-06-07-incremental-search-sidecar.md",
    "docs/plans/2026-06-09-miller-data-opportunities-plan.md",
    "docs/plans/2026-06-09-miller-quality-review-goal-implementation-plan.md",
    "docs/plans/2026-06-09-patterns-tool-implementation-plan.md",
    "docs/plans/2026-06-09-reference-aware-context-design.md",
    "docs/plans/2026-06-10-review-findings-fixes.md",
    "docs/plans/2026-06-11-version-aware-leadership-design.md",
    "docs/plans/2026-06-11-version-aware-leadership.md",
    "docs/plans/2026-06-23-telemetry-workspace-onboarding-implementation-plan.md",
    "docs/plans/2026-06-27-search-inspect-effectiveness-implementation-plan.md",
    "docs/plans/2026-06-27-search-no-results-recall-plan.md",
    "docs/plans/2026-07-02-guidance-delivery-design.md",
    "docs/plans/2026-07-02-guidance-delivery-implementation.md",
    "docs/plans/2026-07-02-tool-output-compaction.md",
    "docs/plans/2026-07-05-rust-ct-impact-single-release.md",
    "docs/plans/2026-07-06-background-bootstrap-design.md",
    "docs/plans/2026-07-06-background-bootstrap-implementation-plan.md",
    "docs/plans/2026-07-07-dead-code-candidates-implementation-plan.md",
    "docs/plans/2026-07-07-metric-history-implementation-plan.md",
    "docs/plans/2026-07-08-dashboard-registry-hygiene.md",
    "docs/plans/2026-07-09-impact-traversal-evidence-implementation-plan.md",
    "docs/plans/2026-07-12-telemetry-diagnosis-hardening.md",
    "docs/plans/2026-07-16-agent-interaction-improvements.md",
    "docs/plans/2026-07-17-julie-extract-2.15.0-adoption.md",
    "docs/plans/2026-07-19-miller-semantic-integration-design.md",
    "docs/plans/2026-07-19-p0-governance-and-gates-plan.md",
    "docs/plans/2026-07-19-p1-freeze-and-conformance-plan.md",
    "docs/plans/2026-07-20-p2-miller-lanes-plan.md",
    "docs/plans/2026-07-20-p3-integration-plan.md",
    "docs/plans/2026-07-20-p3-track1-sidecar-pins-plan.md",
    "docs/plans/2026-07-20-semantic-p4-shadow-rollout.md",
    "docs/plans/2026-07-21-encoder-comparison-fusion-v2-design.md",
    "docs/plans/2026-07-21-semantic-p5-canary-plan.md",
    "docs/release-notes/v0.1.0-beta.1.md",
    "docs/release-notes/v1.4.0.md",
)

BUILD_EXCLUSIONS = (".miller/", "node_modules/", "target/", "docs/site/")
BUILD_PATH_FRAGMENTS = ("/obj/", "/bin/", "/node_modules/", "/target/")

COMMENT_MARKERS = re.compile(r"^\s*(///+|//+|\*+/?|#+|<!--|-->|/\*+)\s?", re.MULTILINE)
XML_DOC_TAGS = re.compile(r"</?(summary|remarks|param|returns|typeparam|see|paramref|c|code)[^>]*>")
WHITESPACE = re.compile(r"\s+")


def excluded(path: str, repo: str = None) -> bool:
    if path.startswith(GOLDEN_SET_EXCLUSIONS) or path.startswith(BUILD_EXCLUSIONS):
        return True
    if repo == "miller" and path.startswith(BENCHMARK_DOC_EXCLUSIONS):
        return True
    return any(frag in "/" + path for frag in BUILD_PATH_FRAGMENTS)


def truncate_on_word(text: str, budget: int) -> str:
    if len(text) <= budget:
        return text
    cut = text[:budget]
    space = cut.rfind(" ")
    return (cut[:space] if space > budget * 0.6 else cut).rstrip()


def clean_doc(doc: str) -> str:
    if not doc:
        return ""
    stripped = COMMENT_MARKERS.sub("", doc)
    stripped = XML_DOC_TAGS.sub(" ", stripped)
    return truncate_on_word(WHITESPACE.sub(" ", stripped).strip(), DOC_EXCERPT_BUDGET)


def first_signature_line(signature: str) -> str:
    if not signature:
        return ""
    return WHITESPACE.sub(" ", signature.splitlines()[0]).strip()


def card_eligible_languages(conn) -> dict:
    """Per-language eligibility matrix, derived from the real extract."""
    matrix = {}
    for lang, kind, n in conn.execute(
        "SELECT language, kind, COUNT(*) FROM symbols GROUP BY 1, 2"
    ):
        entry = matrix.setdefault(lang, {"code_kinds": {}, "other_kinds": {}, "eligible": False})
        bucket = "code_kinds" if kind in CODE_KINDS else "other_kinds"
        entry[bucket][kind] = n
    for entry in matrix.values():
        entry["eligible"] = bool(entry["code_kinds"])
    return matrix


def build_symbol_cards(db: Path, repo: str):
    conn = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    eligibility = card_eligible_languages(conn)
    rows = conn.execute(
        """
        SELECT s.symbol_id, s.path, s.language, s.name, s.kind, s.signature,
               s.doc_comment, s.is_test, p.name AS container_name, p.kind AS container_kind
        FROM symbols s
        LEFT JOIN symbols p ON p.symbol_id = s.parent_symbol_id
        """
    ).fetchall()
    conn.close()

    for row in rows:
        if row["kind"] in INELIGIBLE_KINDS or excluded(row["path"], repo):
            continue
        if not eligibility.get(row["language"], {}).get("eligible"):
            continue
        qualified = f"{row['container_name']}.{row['name']}" if row["container_name"] else row["name"]
        container = row["container_name"] or Path(row["path"]).parent.as_posix()

        parts = [row["kind"], qualified]
        sig = first_signature_line(row["signature"])
        if sig and sig != qualified:
            parts.append(sig)
        doc = clean_doc(row["doc_comment"])
        if doc:
            parts.append(doc)
        parts.append(f"in: {container} {row['path']}")

        text = truncate_on_word(" ".join(p for p in parts if p), CARD_BUDGET)
        yield {
            "unit_id": f"{repo}:sym:{row['symbol_id']}",
            "doc_id": row["path"],
            "repo": repo,
            "kind": "symbol_card",
            "language": row["language"],
            "is_test": int(row["is_test"]),
            "text": text,
        }


def window(text: str):
    """Split long docs into overlapping windows; whole-file chunks exceed every
    candidate's context (docs chunks average ~7KB in these workspaces)."""
    text = text.strip()
    if len(text) <= CHUNK_BUDGET:
        return [text] if text else []
    out, start = [], 0
    while start < len(text):
        piece = truncate_on_word(text[start : start + CHUNK_BUDGET], CHUNK_BUDGET)
        if piece:
            out.append(piece)
        step = max(len(piece) - CHUNK_OVERLAP, CHUNK_BUDGET // 2)
        start += step
    return out


def build_doc_chunks(db: Path, repo: str):
    if not db.exists():
        return
    conn = sqlite3.connect(f"file:{db}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row
    rows = conn.execute(
        """
        SELECT chunk_id, path, display_path, language, raw_text
        FROM content_chunks
        WHERE content_kind IN ('workspace_docs', 'workspace_config')
        """
    ).fetchall()
    conn.close()

    for row in rows:
        path = row["path"] or row["display_path"]
        if not path or excluded(path, repo):
            continue
        header = f"{Path(path).name} {path}"
        for i, piece in enumerate(window(row["raw_text"])):
            yield {
                "unit_id": f"{repo}:doc:{row['chunk_id']}:{i}",
                "doc_id": path,
                "repo": repo,
                "kind": "doc_chunk",
                "language": row["language"],
                "is_test": 0,
                "text": truncate_on_word(f"{header} {piece}", CARD_BUDGET),
            }


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--repo", action="append", required=True, metavar="NAME=DIR")
    ap.add_argument("--out", required=True, type=Path)
    ap.add_argument("--manifest", required=True, type=Path)
    args = ap.parse_args()

    units, stats = [], {}
    for spec in args.repo:
        name, _, root = spec.partition("=")
        root = Path(root)
        symbols_db, content_db = root / ".miller/symbols.db", root / ".miller/content.db"
        if not symbols_db.exists():
            print(f"error: {symbols_db} not found", file=sys.stderr)
            return 1
        cards = list(build_symbol_cards(symbols_db, name))
        chunks = list(build_doc_chunks(content_db, name))
        probe = sqlite3.connect(f"file:{symbols_db}?mode=ro", uri=True)
        matrix = card_eligible_languages(probe)
        probe.close()
        units.extend(cards)
        units.extend(chunks)
        stats[name] = {
            "root": str(root),
            "symbol_cards": len(cards),
            "doc_chunks": len(chunks),
            "distinct_doc_ids": len({u["doc_id"] for u in cards + chunks}),
            "by_language": _count(cards + chunks, "language"),
            "card_eligibility_matrix": {
                lang: {
                    "eligible": e["eligible"],
                    "code_kinds": e["code_kinds"],
                    "other_kinds": e["other_kinds"],
                }
                for lang, e in sorted(matrix.items())
            },
        }

    args.out.parent.mkdir(parents=True, exist_ok=True)
    with args.out.open("w") as fh:
        for unit in units:
            fh.write(json.dumps(unit, ensure_ascii=False) + "\n")

    leaked = sorted({u["doc_id"] for u in units if u["doc_id"].startswith(GOLDEN_SET_EXCLUSIONS)})
    manifest = {
        "corpus_version": "v1",
        "card_template": "{kind} {qualified name} {signature first line} {doc excerpt <=300} in: {container} {path}",
        "card_budget_chars": CARD_BUDGET,
        "chunk_budget_chars": CHUNK_BUDGET,
        "chunk_overlap_chars": CHUNK_OVERLAP,
        "ineligible_kinds": sorted(INELIGIBLE_KINDS),
        "excluded_prefixes": list(GOLDEN_SET_EXCLUSIONS + BUILD_EXCLUSIONS),
        "excluded_path_fragments": list(BUILD_PATH_FRAGMENTS),
        "benchmark_doc_exclusions": {
            "repo": "miller",
            "frozen_sha": "59c2c79e8633940de5d394f73235f10acbe2c2b8",
            "count": len(BENCHMARK_DOC_EXCLUSIONS),
            "paths": list(BENCHMARK_DOC_EXCLUSIONS),
        },
        "total_units": len(units),
        "per_repo": stats,
        "golden_set_leak_check": {
            "excluded_prefixes": list(GOLDEN_SET_EXCLUSIONS),
            "leaked_doc_ids": leaked,
            "passed": not leaked,
        },
    }
    args.manifest.write_text(json.dumps(manifest, indent=2) + "\n")

    print(f"corpus: {len(units)} units -> {args.out}")
    for name, s in stats.items():
        print(f"  {name}: {s['symbol_cards']} cards + {s['doc_chunks']} chunks, {s['distinct_doc_ids']} doc_ids")
    if leaked:
        print(f"FAIL: {len(leaked)} golden-set paths leaked into corpus", file=sys.stderr)
        return 2
    print("golden-set leak check: PASS (0 eval/.razorback/.claude paths)")
    return 0


def _count(units, field):
    out = {}
    for u in units:
        out[u[field]] = out.get(u[field], 0) + 1
    return dict(sorted(out.items(), key=lambda kv: -kv[1]))


if __name__ == "__main__":
    sys.exit(main())
