"""Synthetic row shapes for the Ph0 write-mechanics instrument.

Every constant here is sampled read-only from the live Miller artifact at
``<repo>/.miller/symbols.db``. ``sample_row_shapes.sh`` re-runs the sampling
queries and writes ``out/row-shapes.txt``; the numbers below must match it.
"""

from __future__ import annotations

import hashlib
import random

ARTIFACT_FILES = 1417
ARTIFACT_IDENTIFIERS = 380_720
ARTIFACT_REFERENCE_SITES = 478_283
ARTIFACT_SYMBOLS = 122_707

IDENTIFIERS_PER_FILE = 269
REFERENCE_SITES_PER_FILE = 338
SYMBOLS_PER_FILE = 87
ROWS_PER_FILE_VERSION = (
    IDENTIFIERS_PER_FILE + REFERENCE_SITES_PER_FILE + SYMBOLS_PER_FILE
)

ARTIFACT_TABLE_BYTES_PER_ROW = {
    "identifiers": 127_832_064 / ARTIFACT_IDENTIFIERS,
    "reference_sites": 104_448_000 / ARTIFACT_REFERENCE_SITES,
    "symbols": 53_501_952 / ARTIFACT_SYMBOLS,
}

ARTIFACT_TABLE_PLUS_INDEX_BYTES_PER_ROW = {
    "identifiers": (127_832_064 + 103_079_936) / ARTIFACT_IDENTIFIERS,
    "reference_sites": (104_448_000 + 78_479_360) / ARTIFACT_REFERENCE_SITES,
    "symbols": (53_501_952 + 33_239_040) / ARTIFACT_SYMBOLS,
}

IDENTIFIER_ID_LEN = 32
REFERENCE_SITE_ID_LEN = 47
FILE_ID_LEN = 37
SYMBOL_ID_LEN = 32
IDENTIFIER_PATH_LEN = 47
SYMBOL_PATH_LEN = 57
IDENTIFIER_NAME_LEN = 9
SYMBOL_NAME_LEN = 13
IDENTIFIER_KIND_LEN = 10
SYMBOL_KIND_LEN = 8
SYMBOL_SIGNATURE_LEN = 45
SYMBOL_DOC_COMMENT_LEN = 49
SYMBOL_METADATA_LEN = 55
IDENTIFIER_METADATA_LEN = 8
BODY_HASH_LEN = 12
TARGET_SYMBOL_PRESENT_FRACTION = 13.2 / 32.0
CONTAINING_SYMBOL_PRESENT_FRACTION = 31.9 / 32.0
REFERENCE_SITE_EXACT_FRACTION = 0.796

LANGUAGES = ("csharp", "rust", "python", "tsx", "razor", "kotlin")
IDENTIFIER_KINDS = ("call", "member_access", "type_usage", "variable_ref", "import")
SYMBOL_KINDS = ("method", "class", "field", "property", "function", "interface")
VISIBILITIES = ("public", "private", "internal", "protected")

_ALPHABET = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
WORDS = (
    "index store version manifest resolve symbol identifier reference site "
    "workspace artifact extractor fingerprint promote generation sidecar "
    "vacuum merge segment transaction commit durable marker complete cohort "
    "retention purge reclaim capacity preflight throughput latency budget "
    "search rank lexical semantic vector broker leader follower converge"
).split()


def _hex(seed: str, length: int) -> str:
    out = ""
    counter = 0
    while len(out) < length:
        out += hashlib.sha256(f"{seed}:{counter}".encode()).hexdigest()
        counter += 1
    return out[:length]


def _token(rng: random.Random, length: int) -> str:
    return "".join(rng.choice(_ALPHABET) for _ in range(length))


def _prose(rng: random.Random, length: int) -> str:
    out = []
    total = 0
    while total < length:
        word = rng.choice(WORDS)
        out.append(word)
        total += len(word) + 1
    return " ".join(out)[:length]


def content_hash(path: str, generation: int) -> str:
    return "blake3:" + _hex(f"{path}@{generation}", 40)


def version_path(file_index: int) -> str:
    directory = file_index // 40
    return f"src/Miller.Component{directory:03d}/Area/Unit{file_index:06d}.cs"


class VersionRowFactory:
    """Deterministic per-file-version row generator.

    The same ``(path, generation)`` always yields byte-identical rows, so a
    resumed import can compare what a crashed run left behind against what it
    would have written.
    """

    def __init__(self, path: str, generation: int, extractor_fp: str):
        self.path = path
        self.generation = generation
        self.extractor_fp = extractor_fp
        self.content_hash = content_hash(path, generation)
        self.rng = random.Random(f"{path}@{generation}")
        self.file_id = _hex(f"file:{path}", FILE_ID_LEN)
        self.language = self.rng.choice(LANGUAGES)
        self._symbol_ids = [
            _hex(f"sym:{path}@{generation}:{i}", SYMBOL_ID_LEN)
            for i in range(SYMBOLS_PER_FILE)
        ]
        self._reference_site_ids = [
            _hex(f"rs:{path}@{generation}:{i}", REFERENCE_SITE_ID_LEN)
            for i in range(REFERENCE_SITES_PER_FILE)
        ]

    def symbols(self, version_id: int):
        rng = self.rng
        symbol_path = (self.path + "                                         ")[
            :SYMBOL_PATH_LEN
        ]
        for i, symbol_id in enumerate(self._symbol_ids):
            parent = self._symbol_ids[rng.randrange(i)] if i else None
            yield (
                version_id,
                symbol_id,
                self.file_id,
                symbol_path,
                self.language,
                _token(rng, SYMBOL_NAME_LEN),
                rng.choice(SYMBOL_KINDS),
                _prose(rng, SYMBOL_SIGNATURE_LEN),
                _prose(rng, SYMBOL_DOC_COMMENT_LEN),
                rng.choice(VISIBILITIES),
                parent,
                i * 12 + 1,
                4,
                i * 12 + 9,
                5,
                i * 400,
                i * 400 + 380,
                _hex(f"body:{self.path}@{self.generation}:{i}", BODY_HASH_LEN),
                0 if rng.random() > 0.1 else 1,
                _prose(rng, SYMBOL_METADATA_LEN),
            )

    def reference_sites(self, version_id: int):
        rng = self.rng
        for i, reference_site_id in enumerate(self._reference_site_ids):
            exact = rng.random() < REFERENCE_SITE_EXACT_FRACTION
            containing = (
                self._symbol_ids[rng.randrange(SYMBOLS_PER_FILE)]
                if rng.random() < CONTAINING_SYMBOL_PRESENT_FRACTION
                else None
            )
            yield (
                version_id,
                reference_site_id,
                self.file_id,
                self.path,
                self.language,
                containing,
                (i * 3 + 1) if exact else None,
                8 if exact else None,
                (i * 3 + 1) if exact else None,
                24 if exact else None,
                (i * 90) if exact else None,
                (i * 90 + 16) if exact else None,
                1 if exact else 0,
                "target_token" if exact else "spanless",
            )

    def identifiers(self, version_id: int):
        rng = self.rng
        for i in range(IDENTIFIERS_PER_FILE):
            yield (
                version_id,
                _hex(f"id:{self.path}@{self.generation}:{i}", IDENTIFIER_ID_LEN),
                self._reference_site_ids[rng.randrange(REFERENCE_SITES_PER_FILE)],
                self.file_id,
                self.path,
                self.language,
                _token(rng, IDENTIFIER_NAME_LEN),
                rng.choice(IDENTIFIER_KINDS),
                self._symbol_ids[rng.randrange(SYMBOLS_PER_FILE)]
                if rng.random() < CONTAINING_SYMBOL_PRESENT_FRACTION
                else None,
                self._symbol_ids[rng.randrange(SYMBOLS_PER_FILE)]
                if rng.random() < TARGET_SYMBOL_PRESENT_FRACTION
                else None,
                i // 4 + 1,
                (i * 7) % 90,
                i // 4 + 1,
                (i * 7) % 90 + 9,
                i * 40,
                i * 40 + 9,
                0.9,
                _prose(rng, IDENTIFIER_METADATA_LEN),
            )

    def fts_documents(self, version_id: int):
        rng = random.Random(f"fts:{self.path}@{self.generation}")
        for symbol_id in self._symbol_ids:
            yield (
                version_id,
                symbol_id,
                _prose(rng, SYMBOL_SIGNATURE_LEN + SYMBOL_DOC_COMMENT_LEN),
                _token(rng, SYMBOL_NAME_LEN).lower(),
            )
