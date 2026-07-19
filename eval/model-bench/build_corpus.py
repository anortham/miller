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

BUILD_EXCLUSIONS = (".miller/", "node_modules/", "target/", "docs/site/")
BUILD_PATH_FRAGMENTS = ("/obj/", "/bin/", "/node_modules/", "/target/")

COMMENT_MARKERS = re.compile(r"^\s*(///+|//+|\*+/?|#+|<!--|-->|/\*+)\s?", re.MULTILINE)
XML_DOC_TAGS = re.compile(r"</?(summary|remarks|param|returns|typeparam|see|paramref|c|code)[^>]*>")
WHITESPACE = re.compile(r"\s+")


def excluded(path: str) -> bool:
    if path.startswith(GOLDEN_SET_EXCLUSIONS) or path.startswith(BUILD_EXCLUSIONS):
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
        if row["kind"] in INELIGIBLE_KINDS or excluded(row["path"]):
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
        if not path or excluded(path):
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
