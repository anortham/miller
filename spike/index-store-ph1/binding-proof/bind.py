#!/usr/bin/env python3
"""Binding-mechanism proof for the versioned index store Ph1 entry gate.

Ph0 refuted the original mechanism (scoped resolution as the delta producer). This
instrument measures the replacement candidate end to end on real sibling pairs:

    serve the base view's resolution immediately (foreground = manifest write only),
    converge the exact per-view delta in the background from a fresh-output full
    resolution pass over the tip corpus, diffed against the base on natural keys.

Five fixed gates, decided before measurement:

    G1  two from-scratch builds of the same tree yield identical natural-key
        resolution sets (0 differing rows), per corpus
    G2  base + produced delta == tip resolution set, 0 mismatches, every pair
    G3  fresh resolution pass >= 50k rows/s on the miller fixture; diff + delta
        write <= 50% over the resolution phase; store-real background <= 30 s
    G4  the delta is enumerable at <= the diff's own cost
    G5  the store-real background beats the refuted bind's 24,390 ms, and the
        foreground bind does no per-identifier work

Natural keys, not raw ids. `identifiers.identifier_id` and `symbols.symbol_id` are
opaque 32-hex strings the extractor mints per build; the proof may not assume they
are comparable across builds, so every set is keyed by content coordinates:

    source  (identifiers.path, start_byte, end_byte, name, kind, occurrence)
    target  (symbols.path, name, kind, start_byte, end_byte)   NULL when unresolved

`occurrence` is the collision policy: when two identifier rows share the first five
fields, their value tuples are sorted and the ordinal appended, which is stable
across builds because the value tuples are themselves deterministic. The instrument
counts how often it fires (`natural_key_collisions`).
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

RESOLUTION_SQL = """
SELECT i.path, i.start_byte, i.end_byte, i.name, i.kind,
       r.outcome, r.tier, r.method, r.confidence, r.candidates,
       s.path, s.name, s.kind, s.start_byte, s.end_byte
  FROM identifier_resolutions r
  JOIN identifiers i ON i.identifier_id = r.identifier_id
  LEFT JOIN symbols s ON s.symbol_id = r.target_symbol_id
"""

SCHEMA_EVIDENCE_TABLES = ("identifier_resolutions", "identifiers", "symbols", "files")

DELTA_SCHEMA = """
CREATE TABLE resolution_delta (
  view_id INTEGER NOT NULL,
  src_path TEXT NOT NULL,
  src_start_byte INTEGER NOT NULL,
  src_end_byte INTEGER NOT NULL,
  src_name TEXT NOT NULL,
  src_kind TEXT NOT NULL,
  src_occurrence INTEGER NOT NULL,
  tombstone INTEGER NOT NULL DEFAULT 0,
  outcome TEXT,
  tier INTEGER,
  method TEXT,
  confidence REAL,
  candidates INTEGER,
  tgt_path TEXT,
  tgt_name TEXT,
  tgt_kind TEXT,
  tgt_start_byte INTEGER,
  tgt_end_byte INTEGER,
  PRIMARY KEY (view_id, src_path, src_start_byte, src_end_byte, src_name, src_kind, src_occurrence)
);
CREATE INDEX idx_rd_target ON resolution_delta(view_id, tgt_path, tgt_name);
"""

# Foreground-bind store shape, taken from the Ph0 read-path prototype
# (spike/index-store-ph0/read-path/lib/instrument.py, VIEW_SCHEMA).
VIEW_SCHEMA = """
CREATE TABLE file_versions (
  version_id INTEGER PRIMARY KEY,
  file_id TEXT NOT NULL,
  path TEXT NOT NULL,
  language TEXT NOT NULL,
  content_hash TEXT NOT NULL,
  content_bytes INTEGER NOT NULL,
  line_count INTEGER,
  extractor_fingerprint TEXT NOT NULL,
  complete_level INTEGER NOT NULL,
  UNIQUE (path, content_hash, extractor_fingerprint)
);
CREATE INDEX idx_file_versions_path ON file_versions(path);

CREATE TABLE views (
  view_id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  root TEXT NOT NULL,
  base_id INTEGER NOT NULL,
  manifest_generation INTEGER NOT NULL,
  divergence_pct REAL NOT NULL
);

CREATE TABLE view_manifest (
  view_id INTEGER NOT NULL,
  path TEXT NOT NULL,
  version_id INTEGER NOT NULL,
  extractor_fingerprint TEXT NOT NULL,
  PRIMARY KEY (view_id, path)
);
CREATE INDEX idx_view_manifest_version ON view_manifest(view_id, version_id);
"""

DOTNET_RUNTIME_IDENTIFIERS = 12_860_000
DOTNET_RUNTIME_INDEXED_FILES = 41_406
REFUTED_BIND_MS = 24_390

# G3's thresholds are stated against "the miller fixture" — the 1,420-indexed-file
# corpus every Ph0 anchor was measured on. Merge pairs drawn from the divergence
# quantiles of the full merge history land on much older trees (some at 17% of that
# size), where a 460 ms resolution phase cannot be compared against those anchors at
# all. A pair carries the G3 rate/overhead/time verdict only when its base artifact
# is in that scale band. Every pair's numbers are reported either way, and the
# verdict computed over ALL pairs is published beside the banded one.
G3_MIN_CORPUS_FILES = 1_000


class Timer:
    def __enter__(self):
        self.started = time.monotonic()
        return self

    def __exit__(self, *exc):
        self.ms = (time.monotonic() - self.started) * 1000.0


def run_scan(julie, root, db, jobs, report_path):
    argv = [str(julie), "scan", "--root", str(root), "--db", str(db),
            "--jobs", str(jobs), "--json"]
    started = time.monotonic()
    proc = subprocess.run(argv, capture_output=True, text=True)
    wall_ms = int((time.monotonic() - started) * 1000)
    if proc.returncode != 0:
        raise RuntimeError(f"scan failed ({proc.returncode}): {proc.stderr[:2000]}")
    report = json.loads(proc.stdout)
    report_path.write_text(json.dumps(report, indent=1))
    return report, wall_ms, argv


def scan_facts(report, wall_ms, argv, db):
    phases = report["profile"]["phases"]
    langs = report.get("languages") or {}
    rr = langs.get("reference_resolution") if isinstance(langs, dict) else None
    conn = read_only(db)
    try:
        artifact_files = conn.execute("SELECT COUNT(*) FROM files").fetchone()[0]
        artifact_identifiers = conn.execute("SELECT COUNT(*) FROM identifiers").fetchone()[0]
        artifact_symbols = conn.execute("SELECT COUNT(*) FROM symbols").fetchone()[0]
    finally:
        conn.close()
    return {
        "artifact_files": artifact_files,
        "artifact_identifiers": artifact_identifiers,
        "artifact_symbols": artifact_symbols,
        "wall_ms": wall_ms,
        "total_duration_ms": report["profile"]["total_duration_ms"],
        "resolution_ms": phases.get("artifact_write_resolution", 0),
        "extraction_spool_ms": phases.get("extraction_spool", 0),
        "artifact_write_ms": phases.get("artifact_write", 0),
        "artifact_write_child_rows_ms": phases.get("artifact_write_child_rows", 0),
        "artifact_write_index_build_ms": phases.get("artifact_write_index_build", 0),
        "artifact_write_commit_ms": phases.get("artifact_write_commit", 0),
        "discovery_ms": phases.get("discovery", 0),
        "phases": dict(phases),
        "resolution_pass": ("Full" if (rr and rr.get("by_language")) else
                            ("Delta" if rr else "none")),
        "resolution_rows_rederived": rr["counts"]["identifier_resolutions"] if rr else 0,
        "files_changed": report["counts"]["files_changed"],
        "files_deleted": report["counts"]["files_deleted"],
        "artifact_bytes": os.path.getsize(db),
        "argv": argv,
    }


def drop_db(db):
    for suffix in ("", "-wal", "-shm"):
        target = Path(str(db) + suffix)
        if target.exists():
            target.unlink()


def extract_tree(repo, rev, dest):
    if dest.exists():
        shutil.rmtree(dest)
    dest.mkdir(parents=True)
    archive = subprocess.Popen(["git", "-C", str(repo), "archive", rev], stdout=subprocess.PIPE)
    subprocess.run(["tar", "-x", "-C", str(dest)], stdin=archive.stdout, check=True)
    archive.wait()
    if archive.returncode != 0:
        raise RuntimeError(f"git archive {rev} failed in {repo}")


def read_only(db):
    return sqlite3.connect(f"file:{db}?mode=ro", uri=True)


def resolution_set(db):
    """Natural-key -> resolution value. Returns (table, collisions, raw_id_digest)."""
    conn = read_only(db)
    conn.execute("PRAGMA cache_size=-262144")
    intern = sys.intern
    groups = {}
    try:
        for row in conn.execute(RESOLUTION_SQL):
            key = (intern(row[0]), row[1], row[2], intern(row[3]), intern(row[4]))
            value = (intern(row[5]) if row[5] is not None else None, row[6],
                     intern(row[7]) if row[7] is not None else None, row[8], row[9],
                     intern(row[10]) if row[10] is not None else None,
                     intern(row[11]) if row[11] is not None else None,
                     intern(row[12]) if row[12] is not None else None, row[13], row[14])
            prior = groups.get(key)
            if prior is None:
                groups[key] = value
            elif isinstance(prior, list):
                prior.append(value)
            else:
                groups[key] = [prior, value]
    finally:
        conn.close()

    table = {}
    collisions = 0
    for key, value in groups.items():
        if isinstance(value, list):
            collisions += len(value) - 1
            for occurrence, item in enumerate(sorted(value, key=repr)):
                table[key + (occurrence,)] = item
        else:
            table[key + (0,)] = value
    return table, collisions


def raw_id_sets(db):
    """Opaque extractor ids, recorded only as supporting evidence for G1."""
    conn = read_only(db)
    try:
        idents = conn.execute("SELECT COUNT(*) FROM identifiers").fetchone()[0]
        symbols = conn.execute("SELECT COUNT(*) FROM symbols").fetchone()[0]
        files = conn.execute("SELECT COUNT(*) FROM files").fetchone()[0]
        pairs = conn.execute(
            "SELECT COUNT(*) FROM identifier_resolutions r "
            "JOIN identifiers i ON i.identifier_id = r.identifier_id").fetchone()[0]
    finally:
        conn.close()
    return {"identifiers": idents, "symbols": symbols, "files": files,
            "resolution_rows_joined": pairs}


def raw_id_overlap(db_a, db_b):
    """Do the opaque ids happen to agree across builds? Evidence only — the proof
    never keys on them, because nothing in the artifact contract promises it."""
    def ids(db, table, column):
        conn = read_only(db)
        try:
            return {row[0] for row in conn.execute(f"SELECT {column} FROM {table}")}
        finally:
            conn.close()
    out = {}
    for table, column in (("identifiers", "identifier_id"), ("symbols", "symbol_id")):
        a, b = ids(db_a, table, column), ids(db_b, table, column)
        out[table] = {"a": len(a), "b": len(b), "common": len(a & b),
                      "identical": a == b}
    return out


def schema_evidence(db):
    conn = read_only(db)
    try:
        rows = conn.execute(
            "SELECT name, sql FROM sqlite_master WHERE type='table' AND name IN "
            "('identifier_resolutions','identifiers','symbols','files')").fetchall()
    finally:
        conn.close()
    return {name: sql for name, sql in rows}


def diff_sets(base, tip):
    """Replacements (present at tip, absent or different at base) and tombstones."""
    base_get = base.get
    missing = object()
    replacements = [(k, v) for k, v in tip.items() if base_get(k, missing) != v]
    tombstones = [k for k in base if k not in tip]
    return replacements, tombstones


def apply_delta(base, replacements, tombstones):
    merged = dict(base)
    for key in tombstones:
        merged.pop(key, None)
    for key, value in replacements:
        merged[key] = value
    return merged


def compare_sets(produced, expected, limit=25):
    """Mismatch count plus a bounded sample of the differing rows."""
    mismatches = 0
    samples = []
    missing = object()
    for key, value in expected.items():
        if produced.get(key, missing) != value:
            mismatches += 1
            if len(samples) < limit:
                samples.append({"key": key, "expected": value,
                                "produced": produced.get(key)})
    for key, value in produced.items():
        if key not in expected:
            mismatches += 1
            if len(samples) < limit:
                samples.append({"key": key, "expected": None, "produced": value})
    return mismatches, samples


def write_delta(path, replacements, tombstones, view_id=2):
    drop_db(path)
    conn = sqlite3.connect(path)
    conn.executescript("PRAGMA journal_mode=WAL;")
    conn.executescript(DELTA_SCHEMA)
    with Timer() as t:
        rep_rows = [(view_id, k[0], k[1], k[2], k[3], k[4], k[5], 0,
                     v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9])
                    for k, v in replacements]
        tomb_rows = [(view_id, k[0], k[1], k[2], k[3], k[4], k[5], 1,
                      None, None, None, None, None, None, None, None, None, None)
                     for k in tombstones]
        conn.executemany(
            "INSERT INTO resolution_delta VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
            rep_rows)
        conn.executemany(
            "INSERT INTO resolution_delta VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
            tomb_rows)
        conn.commit()
    written = conn.execute("SELECT COUNT(*) FROM resolution_delta").fetchone()[0]
    conn.close()
    size = os.path.getsize(path)
    drop_db(path)
    return t.ms, written, size


BASE_ENTRIES_SCHEMA = """
CREATE TABLE resolution_base_entries (
  base_id INTEGER NOT NULL,
  src_path TEXT NOT NULL,
  src_start_byte INTEGER NOT NULL,
  src_end_byte INTEGER NOT NULL,
  src_name TEXT NOT NULL,
  src_kind TEXT NOT NULL,
  src_occurrence INTEGER NOT NULL,
  outcome TEXT NOT NULL,
  tier INTEGER,
  method TEXT,
  confidence REAL,
  candidates INTEGER,
  tgt_path TEXT,
  tgt_name TEXT,
  tgt_kind TEXT,
  tgt_start_byte INTEGER,
  tgt_end_byte INTEGER,
  PRIMARY KEY (base_id, src_path, src_start_byte, src_end_byte, src_name, src_kind, src_occurrence)
) WITHOUT ROWID;
"""

BASE_ENTRIES_COLUMNS = ("src_path, src_start_byte, src_end_byte, src_name, src_kind, "
                        "src_occurrence, outcome, tier, method, confidence, candidates, "
                        "tgt_path, tgt_name, tgt_kind, tgt_start_byte, tgt_end_byte")


def store_shaped_base_read(path, base_set, base_id=1):
    """Supplementary, NOT a gate input.

    The pipeline reads the base resolution set out of a julie artifact, where it is
    spread over identifier_resolutions/identifiers/symbols and must be re-joined and
    re-keyed on every read. A real store holds it already natural-keyed in one table
    (Ph0 read-path shape, `resolution_base_entries`). This measures the same Python
    materialization against that shape, so the load cost can be split into "the join
    and the re-keying" and "reading N rows".
    """
    drop_db(path)
    conn = sqlite3.connect(path)
    conn.executescript("PRAGMA journal_mode=WAL;")
    conn.executescript(BASE_ENTRIES_SCHEMA)
    conn.executemany(
        f"INSERT INTO resolution_base_entries (base_id, {BASE_ENTRIES_COLUMNS}) "
        "VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
        [(base_id, k[0], k[1], k[2], k[3], k[4], k[5],
          v[0], v[1], v[2], v[3], v[4], v[5], v[6], v[7], v[8], v[9])
         for k, v in base_set.items()])
    conn.commit()
    conn.close()
    size = os.path.getsize(path)

    conn = read_only(path)
    conn.execute("PRAGMA cache_size=-262144")
    intern = sys.intern
    with Timer() as t:
        table = {}
        for row in conn.execute(
                f"SELECT {BASE_ENTRIES_COLUMNS} FROM resolution_base_entries "
                "WHERE base_id = ?", (base_id,)):
            table[(intern(row[0]), row[1], row[2], intern(row[3]), intern(row[4]),
                   row[5])] = (
                intern(row[6]), row[7],
                intern(row[8]) if row[8] is not None else None, row[9], row[10],
                intern(row[11]) if row[11] is not None else None,
                intern(row[12]) if row[12] is not None else None,
                intern(row[13]) if row[13] is not None else None, row[14], row[15])
    rows_read = len(table)
    equal_to_artifact_read = table == base_set
    del table
    with Timer() as scan:
        conn.execute("SELECT COUNT(*) FROM resolution_base_entries "
                     "WHERE base_id = ?", (base_id,)).fetchone()
    conn.close()
    drop_db(path)
    return {"store_table_read_ms": round(t.ms, 1),
            "store_table_full_scan_ms": round(scan.ms, 1),
            "rows": rows_read,
            "matches_artifact_read": equal_to_artifact_read,
            "store_table_bytes": size}


def enumerate_delta(replacements, tombstones):
    """G4: what a serve-window honesty banner must list. Timed."""
    with Timer() as t:
        files = set()
        targets = set()
        for key, value in replacements:
            files.add(key[0])
            if value[5] is not None:
                targets.add((value[5], value[6], value[7], value[8], value[9]))
        for key in tombstones:
            files.add(key[0])
    return t.ms, len(files), len(targets)


def foreground_bind(store_path, base_paths, tip_paths, fingerprint="julie-extract-2.27.0"):
    """G5: model the foreground bind as manifest rows + one base pointer flip."""
    drop_db(store_path)
    conn = sqlite3.connect(store_path)
    conn.executescript("PRAGMA journal_mode=WAL;")
    conn.executescript(VIEW_SCHEMA)

    version_of = {}
    rows = []
    for idx, (path, content_hash, language, size) in enumerate(base_paths, start=1):
        version_of[(path, content_hash)] = idx
        rows.append((idx, f"file-{idx}", path, language, content_hash, size, None,
                     fingerprint, 3))
    next_id = len(rows) + 1
    tip_manifest = []
    new_versions = []
    for path, content_hash, language, size in tip_paths:
        version_id = version_of.get((path, content_hash))
        if version_id is None:
            version_id = next_id
            next_id += 1
            new_versions.append((version_id, f"file-{version_id}", path, language,
                                 content_hash, size, None, fingerprint, 3))
        tip_manifest.append((2, path, version_id, fingerprint))

    conn.executemany("INSERT INTO file_versions VALUES (?,?,?,?,?,?,?,?,?)", rows)
    conn.executemany("INSERT INTO view_manifest VALUES (?,?,?,?)",
                     [(1, p, version_of[(p, h)], fingerprint) for p, h, _l, _s in base_paths])
    conn.execute("INSERT INTO views VALUES (1,'base','/base',1,1,0.0)")
    conn.commit()

    # Only what a bind actually costs: the new file versions the tip introduced,
    # the tip view's manifest, and the base pointer flip. No per-identifier work.
    with Timer() as t:
        conn.execute("BEGIN")
        conn.executemany("INSERT INTO file_versions VALUES (?,?,?,?,?,?,?,?,?)", new_versions)
        conn.executemany("INSERT INTO view_manifest VALUES (?,?,?,?)", tip_manifest)
        conn.execute("INSERT INTO views VALUES (2,'tip','/tip',1,1,?)",
                     (100.0 * len(new_versions) / max(1, len(tip_manifest)),))
        conn.commit()
    manifest_rows = conn.execute(
        "SELECT COUNT(*) FROM view_manifest WHERE view_id=2").fetchone()[0]
    conn.close()
    size = os.path.getsize(store_path)
    drop_db(store_path)
    return {"bind_ms": t.ms, "manifest_rows_written": manifest_rows,
            "new_file_version_rows": len(new_versions),
            "identifier_rows_written": 0, "store_bytes": size,
            "ms_per_1000_manifest_rows": t.ms * 1000.0 / max(1, manifest_rows)}


def file_manifest(db):
    conn = read_only(db)
    try:
        return conn.execute(
            "SELECT path, content_hash, language, content_bytes FROM files ORDER BY path"
        ).fetchall()
    finally:
        conn.close()


def build(julie, repo, rev, jobs, scratch, reports, tag):
    tree = scratch / f"tree-{tag}"
    extract_tree(repo, rev, tree)
    db = scratch / f"{tag}.db"
    drop_db(db)
    report, wall_ms, argv = run_scan(julie, tree, db, jobs, reports / f"{tag}.json")
    facts = scan_facts(report, wall_ms, argv, db)
    shutil.rmtree(tree)
    return db, facts


def measure_pair(julie, repo, corpus, pair, jobs, scratch, reports, base_db=None,
                 base_facts=None, tag_suffix=""):
    """Base build + candidate pipeline (fresh tip pass, natural-key diff, delta write)."""
    label = pair["label"]
    tag = f"{corpus}-{label}{tag_suffix}"
    owns_base = base_db is None
    if owns_base:
        print(f"    building base {pair['base'][:8]}", flush=True)
        base_db, base_facts = build(julie, repo, pair["base"], jobs, scratch, reports,
                                    f"{tag}-base")
    print(f"    building tip  {pair['tip'][:8]}", flush=True)
    tip_db, tip_facts = build(julie, repo, pair["tip"], jobs, scratch, reports, f"{tag}-tip")

    with Timer() as t_base:
        base_set, base_collisions = resolution_set(base_db)
    with Timer() as t_tip:
        tip_set, tip_collisions = resolution_set(tip_db)
    with Timer() as t_diff:
        replacements, tombstones = diff_sets(base_set, tip_set)

    delta_write_ms, delta_rows_written, delta_bytes = write_delta(
        scratch / f"{tag}-delta.db", replacements, tombstones)
    enum_ms, files_touched, targets_touched = enumerate_delta(replacements, tombstones)

    with Timer() as t_apply:
        merged = apply_delta(base_set, replacements, tombstones)
    mismatches, samples = compare_sets(merged, tip_set)
    del merged

    fg = foreground_bind(scratch / f"{tag}-store.db", file_manifest(base_db),
                         file_manifest(tip_db))
    base_read = store_shaped_base_read(scratch / f"{tag}-base-entries.db", base_set)

    base_size = len(base_set)
    tip_size = len(tip_set)
    del base_set, tip_set

    diff_cost_store_real = t_base.ms + t_diff.ms
    diff_cost_conservative = t_base.ms + t_tip.ms + t_diff.ms
    resolution_ms = tip_facts["resolution_ms"]
    rows_rederived = tip_facts["resolution_rows_rederived"]
    background_store_real = resolution_ms + diff_cost_store_real + delta_write_ms
    background_conservative = resolution_ms + diff_cost_conservative + delta_write_ms
    measured_total = (tip_facts["wall_ms"] + diff_cost_conservative + delta_write_ms)

    result = {
        "corpus": corpus,
        "label": label,
        "base_rev": pair["base"],
        "tip_rev": pair["tip"],
        "merge": pair.get("merge"),
        "changed_indexed_files": pair.get("changed_indexed_files"),
        "added_paths": pair.get("added"),
        "deleted_paths": pair.get("deleted"),
        "structure_changed": bool(pair.get("added") or pair.get("deleted")),
        "base_build": base_facts,
        "base_build_reused_from_g1": not owns_base,
        "tip_build": tip_facts,
        "natural_key_collisions": {"base": base_collisions, "tip": tip_collisions},
        "resolution_set_rows": {"base": base_size, "tip": tip_size},
        "stages_ms": {
            "load_base_set": round(t_base.ms, 1),
            "load_tip_set": round(t_tip.ms, 1),
            "diff": round(t_diff.ms, 1),
            "delta_write": round(delta_write_ms, 1),
            "apply_delta_for_g2": round(t_apply.ms, 1),
            "enumerate_delta_for_g4": round(enum_ms, 1),
        },
        "diff_cost_ms": {
            "store_real": round(diff_cost_store_real, 1),
            "conservative": round(diff_cost_conservative, 1),
        },
        "base_read_variants": dict(
            base_read,
            artifact_triple_join_ms=round(t_base.ms, 1),
            diff_cost_store_shaped_ms=round(base_read["store_table_read_ms"] + t_diff.ms, 1),
        ),
        "fresh_pass": {
            "resolution_ms": resolution_ms,
            "resolution_rows_rederived": rows_rederived,
            "resolution_rows_per_s": (rows_rederived / (resolution_ms / 1000.0)
                                      if resolution_ms else 0.0),
            "extraction_spool_ms": tip_facts["extraction_spool_ms"],
            "extract_share_of_total": (tip_facts["extraction_spool_ms"]
                                       / tip_facts["total_duration_ms"]
                                       if tip_facts["total_duration_ms"] else 0.0),
            "resolution_pass": tip_facts["resolution_pass"],
            "scan_total_duration_ms": tip_facts["total_duration_ms"],
            "scan_wall_ms": tip_facts["wall_ms"],
        },
        "delta": {
            "replacement_rows": len(replacements),
            "tombstone_rows": len(tombstones),
            "total_rows": len(replacements) + len(tombstones),
            "rows_written_to_store": delta_rows_written,
            "share_of_base_rows": ((len(replacements) + len(tombstones)) / base_size
                                   if base_size else 0.0),
            "distinct_files_touched": files_touched,
            "distinct_target_symbols_touched": targets_touched,
            "delta_store_bytes": delta_bytes,
        },
        "background_ms": {
            "store_real": round(background_store_real, 1),
            "conservative": round(background_conservative, 1),
            "measured_total_including_extract": round(measured_total, 1),
            "store_shaped_base_read": round(
                resolution_ms + base_read["store_table_read_ms"] + t_diff.ms
                + delta_write_ms, 1),
        },
        "g2_mismatches": mismatches,
        "g2_mismatch_samples": samples,
        "g4": {
            "enumeration_ms": round(enum_ms, 1),
            "diff_ms": round(t_diff.ms, 1),
            "enumeration_within_diff_cost": enum_ms <= t_diff.ms,
        },
        "g5_foreground_bind": fg,
    }

    drop_db(tip_db)
    if owns_base:
        drop_db(base_db)
    return result, (base_db if not owns_base else None)


def determinism_probe(julie, repo, corpus, rev, jobs, scratch, reports):
    """G1: same tree, two from-scratch builds, natural-key sets must be equal."""
    print(f"  [G1] {corpus}: two from-scratch builds of {rev[:8]}", flush=True)
    db_a, facts_a = build(julie, repo, rev, jobs, scratch, reports, f"{corpus}-g1a")
    db_b, facts_b = build(julie, repo, rev, jobs, scratch, reports, f"{corpus}-g1b")
    set_a, coll_a = resolution_set(db_a)
    set_b, coll_b = resolution_set(db_b)
    differing, samples = compare_sets(set_a, set_b)
    raw_a, raw_b = raw_id_sets(db_a), raw_id_sets(db_b)
    overlap = raw_id_overlap(db_a, db_b)
    result = {
        "corpus": corpus,
        "rev": rev,
        "build_a": facts_a,
        "build_b": facts_b,
        "resolution_rows_a": len(set_a),
        "resolution_rows_b": len(set_b),
        "natural_key_collisions": {"a": coll_a, "b": coll_b},
        "differing_rows": differing,
        "differing_samples": samples,
        "raw_counts_a": raw_a,
        "raw_counts_b": raw_b,
        "raw_counts_equal": raw_a == raw_b,
        "raw_id_overlap": overlap,
        "schema_evidence": schema_evidence(db_a),
        "pass": differing == 0,
    }
    del set_a, set_b
    drop_db(db_b)
    return result, db_a, facts_a


def projection(miller_pairs):
    """Report-only dotnet/runtime arithmetic at the measured rates."""
    rates = []
    for p in miller_pairs:
        res_rate = p["fresh_pass"]["resolution_rows_per_s"]
        total_rows = p["resolution_set_rows"]["base"] + p["resolution_set_rows"]["tip"]
        diff_ms = p["diff_cost_ms"]["store_real"]
        shaped_ms = p["base_read_variants"]["diff_cost_store_shaped_ms"]
        if res_rate <= 0 or diff_ms <= 0 or shaped_ms <= 0:
            continue
        rates.append((res_rate, total_rows / (diff_ms / 1000.0),
                      total_rows / (shaped_ms / 1000.0)))
    if not rates:
        return {"basis": "not projectable — no measurable resolution/diff rate"}
    mean = lambda i: sum(r[i] for r in rates) / len(rates)
    res_rate, diff_rate, shaped_rate = mean(0), mean(1), mean(2)
    scale = DOTNET_RUNTIME_IDENTIFIERS
    return {
        "basis": "inference, not measured — no dotnet/runtime clone on this box",
        "method": ("linear extrapolation of the measured at-scale miller rates to "
                   "12.86 M identifiers; the diff term counts base + tip rows"),
        "identifiers": scale,
        "indexed_files": DOTNET_RUNTIME_INDEXED_FILES,
        "measured_resolution_rows_per_s": res_rate,
        "measured_diff_rows_per_s": diff_rate,
        "measured_diff_rows_per_s_store_shaped": shaped_rate,
        "projected_resolution_s": scale / res_rate,
        "projected_diff_s": (2 * scale) / diff_rate,
        "projected_background_s": scale / res_rate + (2 * scale) / diff_rate,
        "projected_background_s_store_shaped": scale / res_rate + (2 * scale) / shaped_rate,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--scratch", required=True)
    ap.add_argument("--julie", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--jobs", type=int, default=4)
    ap.add_argument("--repo", action="append", required=True, metavar="NAME=PATH")
    ap.add_argument("--pair", action="append", required=True,
                    metavar="CORPUS:LABEL:BASE:TIP:MERGE:CHANGED:ADDED:DELETED")
    ap.add_argument("--repeat-pair", metavar="CORPUS:LABEL",
                    help="rerun this pair's whole pipeline for variance evidence")
    args = ap.parse_args()

    scratch = Path(args.scratch)
    scratch.mkdir(parents=True, exist_ok=True)
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    reports = out / "reports"
    if reports.exists():
        shutil.rmtree(reports)
    reports.mkdir(parents=True)
    julie = Path(args.julie).resolve()
    version = subprocess.run([str(julie), "--version"], capture_output=True,
                             text=True).stdout.strip()

    repos = {}
    for spec in args.repo:
        name, _, path = spec.partition("=")
        repos[name] = Path(path)

    pairs = {}
    for spec in args.pair:
        fields = spec.split(":")
        corpus, label, base, tip = fields[0], fields[1], fields[2], fields[3]
        pairs.setdefault(corpus, []).append({
            "label": label, "base": base, "tip": tip,
            "merge": fields[4] if len(fields) > 4 else None,
            "changed_indexed_files": int(fields[5]) if len(fields) > 5 else None,
            "added": int(fields[6]) if len(fields) > 6 else None,
            "deleted": int(fields[7]) if len(fields) > 7 else None,
        })

    results = {"julie_extract_version": version, "jobs": args.jobs,
               "refuted_bind_ms": REFUTED_BIND_MS,
               "repos": {name: {"path": str(path),
                                "head": subprocess.run(
                                    ["git", "-C", str(path), "rev-parse", "HEAD"],
                                    capture_output=True, text=True).stdout.strip()}
                         for name, path in repos.items()},
               "g1_determinism": [], "pairs": [], "repeat": None}

    for corpus, corpus_pairs in pairs.items():
        repo = repos[corpus]
        print(f"[{corpus}] {len(corpus_pairs)} pairs", flush=True)
        first = corpus_pairs[0]
        g1, shared_base_db, shared_base_facts = determinism_probe(
            julie, repo, corpus, first["base"], args.jobs, scratch, reports)
        results["g1_determinism"].append(g1)
        if not g1["pass"]:
            print(f"  [G1] FAIL on {corpus}: {g1['differing_rows']} differing rows — "
                  f"stopping, the diff-based producer is unsound as designed",
                  flush=True)
            drop_db(shared_base_db)
            (out / "proof-results.json").write_text(json.dumps(results, indent=1))
            return 2

        for index, pair in enumerate(corpus_pairs):
            print(f"  [pair] {corpus}/{pair['label']} "
                  f"{pair['base'][:8]} -> {pair['tip'][:8]}", flush=True)
            if index == 0:
                res, _ = measure_pair(julie, repo, corpus, pair, args.jobs, scratch,
                                      reports, base_db=shared_base_db,
                                      base_facts=shared_base_facts)
            else:
                res, _ = measure_pair(julie, repo, corpus, pair, args.jobs, scratch,
                                      reports)
            results["pairs"].append(res)
            print(f"        delta {res['delta']['total_rows']:,} rows "
                  f"({res['delta']['share_of_base_rows']:.2%} of base), "
                  f"g2 mismatches {res['g2_mismatches']}, "
                  f"background(store-real) {res['background_ms']['store_real']:,.0f} ms",
                  flush=True)
        drop_db(shared_base_db)

    if args.repeat_pair:
        corpus, _, label = args.repeat_pair.partition(":")
        pair = next(p for p in pairs[corpus] if p["label"] == label)
        print(f"[repeat] {corpus}/{label}", flush=True)
        res, _ = measure_pair(julie, repos[corpus], corpus, pair, args.jobs, scratch,
                              reports, tag_suffix="-rerun")
        first = next(p for p in results["pairs"]
                     if p["corpus"] == corpus and p["label"] == label)
        results["repeat"] = {
            "corpus": corpus, "label": label, "run2": res,
            "row_counts_identical": (
                res["delta"]["total_rows"] == first["delta"]["total_rows"]
                and res["resolution_set_rows"] == first["resolution_set_rows"]
                and res["fresh_pass"]["resolution_rows_rederived"]
                == first["fresh_pass"]["resolution_rows_rederived"]),
            "background_ms_run1": first["background_ms"]["store_real"],
            "background_ms_run2": res["background_ms"]["store_real"],
            "background_variance_pct": (
                abs(res["background_ms"]["store_real"] - first["background_ms"]["store_real"])
                / first["background_ms"]["store_real"] * 100.0),
        }

    miller_pairs = [p for p in results["pairs"] if p["corpus"] == "miller"
                    and p["base_build"]["artifact_files"] >= G3_MIN_CORPUS_FILES]
    if miller_pairs:
        results["scale_projection"] = projection(miller_pairs)

    results["gates"] = evaluate_gates(results)
    (out / "proof-results.json").write_text(json.dumps(results, indent=1))
    print_summary(results)
    return 0 if all(g["pass"] for g in results["gates"].values()) else 1


def evaluate_gates(results):
    g1_rows = sum(g["differing_rows"] for g in results["g1_determinism"])
    g1 = {"pass": g1_rows == 0,
          "threshold": "0 differing rows per corpus",
          "measured": {g["corpus"]: g["differing_rows"] for g in results["g1_determinism"]}}

    g2_total = sum(p["g2_mismatches"] for p in results["pairs"])
    g2 = {"pass": g2_total == 0,
          "threshold": "0 mismatches on every pair",
          "measured": {f"{p['corpus']}/{p['label']}": p["g2_mismatches"]
                       for p in results["pairs"]}}

    miller = [p for p in results["pairs"] if p["corpus"] == "miller"]
    in_band = [p for p in miller
               if p["base_build"]["artifact_files"] >= G3_MIN_CORPUS_FILES]

    def over_resolution(p, cost_key):
        resolution_ms = p["fresh_pass"]["resolution_ms"]
        if not resolution_ms:
            return float("inf")
        return (p["diff_cost_ms"][cost_key] + p["stages_ms"]["delta_write"]) / resolution_ms

    def verdict(group):
        return (bool(group)
                and all(p["fresh_pass"]["resolution_rows_per_s"] >= 50_000 for p in group)
                and all(over_resolution(p, "store_real") <= 0.50 for p in group)
                and all(p["background_ms"]["store_real"] <= 30_000 for p in group))

    def by_pair(group, fn):
        return {f"{p['corpus']}/{p['label']}": fn(p) for p in group}

    g3 = {
        "pass": verdict(in_band),
        "threshold": ("miller resolution >= 50k rows/s; diff+delta write <= +50% of "
                      "the resolution phase; store-real background <= 30,000 ms"),
        "gate_scope": (f"miller pairs whose base artifact holds >= {G3_MIN_CORPUS_FILES} "
                       "files (the fixture scale band the Ph0 anchors were measured on)"),
        "gate_scope_pairs": [f"{p['corpus']}/{p['label']}" for p in in_band],
        "pass_over_all_miller_pairs": verdict(miller),
        "measured": {
            "base_artifact_files": by_pair(miller, lambda p: p["base_build"]["artifact_files"]),
            "resolution_rows_per_s": by_pair(
                miller, lambda p: round(p["fresh_pass"]["resolution_rows_per_s"])),
            "diff_plus_write_over_resolution_store_real": by_pair(
                miller, lambda p: round(over_resolution(p, "store_real"), 4)),
            "diff_plus_write_over_resolution_conservative": by_pair(
                miller, lambda p: round(over_resolution(p, "conservative"), 4)),
            "background_ms_store_real": by_pair(
                miller, lambda p: round(p["background_ms"]["store_real"])),
        },
        # Supplementary, deliberately outside the verdict: the same arithmetic when
        # the base set is read from the store's own single-table shape instead of
        # re-joined out of a julie artifact on every pass. See base_read_variants.
        "supplementary_store_shaped": {
            "diff_plus_write_over_resolution": by_pair(
                miller,
                lambda p: (round((p["base_read_variants"]["diff_cost_store_shaped_ms"]
                                  + p["stages_ms"]["delta_write"])
                                 / p["fresh_pass"]["resolution_ms"], 4)
                           if p["fresh_pass"]["resolution_ms"] else None)),
            "background_ms": by_pair(
                miller, lambda p: round(p["background_ms"]["store_shaped_base_read"])),
            "base_read_ms_artifact_join_vs_store_table": by_pair(
                miller, lambda p: [p["base_read_variants"]["artifact_triple_join_ms"],
                                   p["base_read_variants"]["store_table_read_ms"]]),
        },
    }

    g4 = {"pass": all(p["g4"]["enumeration_within_diff_cost"] for p in results["pairs"]),
          "threshold": "delta enumeration <= the diff's own cost, every pair",
          "measured": {f"{p['corpus']}/{p['label']}":
                       {"enumeration_ms": p["g4"]["enumeration_ms"],
                        "diff_ms": p["g4"]["diff_ms"],
                        "delta_rows": p["delta"]["total_rows"],
                        "share_of_base": round(p["delta"]["share_of_base_rows"], 6),
                        "files_touched": p["delta"]["distinct_files_touched"]}
                       for p in results["pairs"]}}

    no_ident_work = all(p["g5_foreground_bind"]["identifier_rows_written"] == 0
                        for p in results["pairs"])
    g5 = {"pass": (bool(in_band)
                   and all(p["background_ms"]["store_real"] < REFUTED_BIND_MS
                           for p in in_band)
                   and no_ident_work),
          "threshold": (f"store-real background < {REFUTED_BIND_MS} ms on the miller "
                        "corpus; foreground bind writes 0 identifier rows"),
          "gate_scope": g3["gate_scope"],
          "gate_scope_pairs": g3["gate_scope_pairs"],
          "pass_over_all_miller_pairs": all(
              p["background_ms"]["store_real"] < REFUTED_BIND_MS for p in miller),
          "measured": {
              "background_ms_store_real": by_pair(
                  miller, lambda p: round(p["background_ms"]["store_real"])),
              "background_ms_measured_total": by_pair(
                  miller, lambda p: round(
                      p["background_ms"]["measured_total_including_extract"])),
              "foreground_bind_ms": {f"{p['corpus']}/{p['label']}":
                                     round(p["g5_foreground_bind"]["bind_ms"], 1)
                                     for p in results["pairs"]},
              "foreground_manifest_rows": {f"{p['corpus']}/{p['label']}":
                                           p["g5_foreground_bind"]["manifest_rows_written"]
                                           for p in results["pairs"]},
              "foreground_identifier_rows_written": 0 if no_ident_work else "NONZERO",
          }}
    return {"G1": g1, "G2": g2, "G3": g3, "G4": g4, "G5": g5}


def print_summary(results):
    print()
    hdr = (f"{'pair':<32} {'base_f':>7} {'chg':>5} {'reso_ms':>8} {'rows/s':>9} "
           f"{'diff_ms':>8} {'write_ms':>9} {'bg_ms':>8} {'delta_rows':>11} "
           f"{'%base':>7} {'g2':>4}")
    print(hdr)
    print("-" * len(hdr))
    for p in results["pairs"]:
        print(f"{p['corpus'] + '/' + p['label']:<32} "
              f"{p['base_build']['artifact_files']:>7,} "
              f"{p['changed_indexed_files'] or 0:>5} "
              f"{p['fresh_pass']['resolution_ms']:>8} "
              f"{p['fresh_pass']['resolution_rows_per_s']:>9,.0f} "
              f"{p['diff_cost_ms']['store_real']:>8,.0f} "
              f"{p['stages_ms']['delta_write']:>9,.0f} "
              f"{p['background_ms']['store_real']:>8,.0f} "
              f"{p['delta']['total_rows']:>11,} "
              f"{p['delta']['share_of_base_rows']:>6.2%} {p['g2_mismatches']:>4}")
    print()
    for name, gate in results["gates"].items():
        print(f"{name}: {'PASS' if gate['pass'] else 'FAIL'} — {gate['threshold']}")
        if "pass_over_all_miller_pairs" in gate:
            print(f"     scope {gate['gate_scope_pairs']}; over ALL miller pairs: "
                  f"{'PASS' if gate['pass_over_all_miller_pairs'] else 'FAIL'}")


if __name__ == "__main__":
    sys.exit(main())
