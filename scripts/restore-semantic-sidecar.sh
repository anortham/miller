#!/usr/bin/env bash
#
# restore-semantic-sidecar.sh — restore the pinned julie-semantic-sidecar binary and the pinned
# sqlite-vec loadable extension into .tools/.
#
# Reads scripts/semantic-pins.json for both pins: `sidecar` (keyed by rust target triple) and
# `sqliteVec` (keyed by .NET RID). Detects the host platform, downloads each asset into a temp
# staging directory OUTSIDE the repo, VERIFIES its sha256 against the pin BEFORE extracting, then
# installs .tools/julie-semantic-sidecar and .tools/vec0.<ext>. A sha256 mismatch aborts before
# .tools/ is touched at all.
#
# Semantic retrieval is OPTIONAL (ADR-0003): a machine that never runs this script still builds and
# still runs — semantic simply fails open with a reason. Only a STALE restored sidecar fails the build.
#
# Sidecar asset names carry NO leading `v` before the version (julie-semantic-sidecar-{VER}-<triple>)
# and the binary sits at the ARCHIVE ROOT — both differ from restore-julie-extract.sh.
#
# While a contract bump is staged before GitHub release assets publish, pass --from-source or set
# MILLER_SEMANTIC_SIDECAR_SOURCE=/path/to/julie-semantic-sidecar to build from that checkout.
#
# Only four sidecar triples exist upstream: aarch64-apple-darwin, x86_64-apple-darwin,
# x86_64-unknown-linux-gnu, x86_64-pc-windows-msvc. There is NO linux-arm64 and NO windows-arm64 asset.
#
# Usage:
#   bash scripts/restore-semantic-sidecar.sh
#   MILLER_SEMANTIC_SIDECAR_SOURCE=~/source/julie-semantic-sidecar bash scripts/restore-semantic-sidecar.sh --from-source
#   MILLER_SEMANTIC_PINS=/tmp/alt-pins.json bash scripts/restore-semantic-sidecar.sh   # pin-file override (testing)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
PINS="${MILLER_SEMANTIC_PINS:-${SCRIPT_DIR}/semantic-pins.json}"
TOOLS_DIR="${REPO_ROOT}/.tools"

if [[ ! -f "${PINS}" ]]; then
  echo "error: pins file not found at ${PINS}" >&2
  exit 1
fi

# --- JSON reader (prefer jq; fall back to python3 — both avoid a hard single-dep) ---
read_pin() {
  # $1 = jq path expression (leading dot), e.g. .sidecar.version or .sqliteVec.assets["osx-arm64"].member
  local expr="$1"
  if command -v jq >/dev/null 2>&1; then
    jq -r "${expr} // empty" "${PINS}"
  elif command -v python3 >/dev/null 2>&1; then
    python3 - "$expr" "${PINS}" <<'PY'
import json, re, sys
expr, path = sys.argv[1], sys.argv[2]
with open(path) as f:
    val = json.load(f)
for key in re.findall(r'\["([^"]+)"\]|\.([A-Za-z0-9_]+)', expr):
    if not isinstance(val, dict):
        val = None
        break
    val = val.get(key[0] or key[1])
print("" if val is None else val)
PY
  else
    echo "error: need either jq or python3 to read ${PINS}" >&2
    exit 1
  fi
}

VERSION="$(read_pin .sidecar.version)"
if [[ -z "${VERSION}" ]]; then
  echo "error: ${PINS} has no .sidecar.version" >&2
  exit 1
fi

FROM_SOURCE=""
SOURCE_REQUESTED=0
if [[ "${1:-}" == "--from-source" ]]; then
  SOURCE_REQUESTED=1
  shift
  FROM_SOURCE="${1:-${MILLER_SEMANTIC_SIDECAR_SOURCE:-}}"
  if [[ $# -gt 0 ]]; then
    shift
  fi
elif [[ -n "${MILLER_SEMANTIC_SIDECAR_SOURCE:-}" ]]; then
  FROM_SOURCE="${MILLER_SEMANTIC_SIDECAR_SOURCE}"
fi

if [[ "${SOURCE_REQUESTED}" == "1" && -z "${FROM_SOURCE}" ]]; then
  echo "error: --from-source requires a path argument or MILLER_SEMANTIC_SIDECAR_SOURCE" >&2
  exit 1
fi

OS="$(uname -s)"

# --- shared helpers ---
sha256_of() {
  if command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | awk '{print $1}'
  elif command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | awk '{print $1}'
  else
    echo "error: need shasum or sha256sum to verify downloads" >&2
    exit 1
  fi
}

verify_sha() {
  local file="$1" expected="$2" actual
  actual="$(sha256_of "${file}")"
  if [[ "${actual}" != "${expected}" ]]; then
    echo "error: sha256 mismatch for $(basename "${file}")" >&2
    echo "  expected: ${expected}" >&2
    echo "  actual:   ${actual}" >&2
    echo "  nothing was installed into ${TOOLS_DIR}" >&2
    exit 1
  fi
}

install_exec_bits() {
  local target="$1"
  chmod +x "${target}"
  if [[ "${OS}" == "Darwin" ]]; then
    xattr -d com.apple.quarantine "${target}" 2>/dev/null || true
  fi
}

if [[ -n "${FROM_SOURCE}" ]]; then
  SOURCE_ROOT="$(cd "${FROM_SOURCE}" && pwd)"
  SOURCE_MANIFEST="${SOURCE_ROOT}/Cargo.toml"
  if [[ ! -f "${SOURCE_MANIFEST}" ]]; then
    echo "error: from-source path is not a julie-semantic-sidecar checkout: ${SOURCE_ROOT}" >&2
    exit 1
  fi
  if ! command -v cargo >/dev/null 2>&1; then
    echo "error: cargo is required for --from-source restore" >&2
    exit 1
  fi

  mkdir -p "${TOOLS_DIR}"
  BINARY="${TOOLS_DIR}/julie-semantic-sidecar"
  SOURCE_BINARY="${SOURCE_ROOT}/target/release/julie-semantic-sidecar"

  echo "Building julie-semantic-sidecar v${VERSION} from source: ${SOURCE_ROOT}"
  cargo build --manifest-path "${SOURCE_MANIFEST}" --release --bin julie-semantic-sidecar
  if [[ ! -f "${SOURCE_BINARY}" ]]; then
    echo "error: expected build output not found: ${SOURCE_BINARY}" >&2
    exit 1
  fi

  cp "${SOURCE_BINARY}" "${BINARY}"
  install_exec_bits "${BINARY}"
  VERSION_OUTPUT="$("${BINARY}" --version 2>/dev/null || true)"
  if [[ "${VERSION_OUTPUT}" != julie-semantic-sidecar* ]]; then
    echo "error: restored binary does not self-identify as julie-semantic-sidecar" >&2
    echo "  actual: ${VERSION_OUTPUT:-"(no --version output)"}" >&2
    exit 1
  fi

  echo "Installed: ${BINARY}"
  "${BINARY}" --version 2>/dev/null || true
  echo "note: --from-source restores the sidecar only; re-run without it to restore vec0.*"
  exit 0
fi

# --- detect platform -> triple ---
ARCH="$(uname -m)"
TRIPLE=""
case "${OS}" in
  Darwin)
    case "${ARCH}" in
      arm64|aarch64) TRIPLE="aarch64-apple-darwin" ;;
      x86_64)        TRIPLE="x86_64-apple-darwin" ;;
    esac
    ;;
  Linux)
    case "${ARCH}" in
      x86_64) TRIPLE="x86_64-unknown-linux-gnu" ;;
      # NO linux-arm64 asset exists upstream — fail loudly below.
    esac
    ;;
esac

if [[ -z "${TRIPLE}" ]]; then
  echo "error: unsupported platform '${OS}/${ARCH}'. julie-semantic-sidecar v${VERSION} publishes only:" >&2
  echo "  macOS arm64 (aarch64-apple-darwin), macOS x64 (x86_64-apple-darwin)," >&2
  echo "  Linux x64 (x86_64-unknown-linux-gnu), Windows x64 (x86_64-pc-windows-msvc, via the .ps1 script)." >&2
  echo "  No linux-arm64 / windows-arm64 prebuilt asset exists; build from source with cargo." >&2
  exit 1
fi

RID="$(read_pin ".sidecar.ridByTriple[\"${TRIPLE}\"]")"
if [[ -z "${RID}" ]]; then
  echo "error: no sqlite-vec RID mapping for ${TRIPLE} in ${PINS}" >&2
  exit 1
fi

SIDECAR_ASSET="$(read_pin ".sidecar.assets[\"${TRIPLE}\"].name")"
SIDECAR_ASSET="${SIDECAR_ASSET/\{VER\}/${VERSION}}"
SIDECAR_SHA="$(read_pin ".sidecar.assets[\"${TRIPLE}\"].sha256")"
SIDECAR_URL_TEMPLATE="$(read_pin .sidecar.urlTemplate)"

if [[ -z "${SIDECAR_ASSET}" || -z "${SIDECAR_SHA}" ]]; then
  echo "error: no published asset pin for julie-semantic-sidecar v${VERSION} / ${TRIPLE} in ${PINS}" >&2
  echo "  Until release assets publish, run:" >&2
  echo "  MILLER_SEMANTIC_SIDECAR_SOURCE=/path/to/julie-semantic-sidecar bash scripts/restore-semantic-sidecar.sh --from-source" >&2
  exit 1
fi

VEC_VERSION="$(read_pin .sqliteVec.version)"
VEC_ASSET="$(read_pin ".sqliteVec.assets[\"${RID}\"].name")"
VEC_ASSET="${VEC_ASSET/\{VER\}/${VEC_VERSION}}"
VEC_MEMBER="$(read_pin ".sqliteVec.assets[\"${RID}\"].member")"
VEC_SHA="$(read_pin ".sqliteVec.assets[\"${RID}\"].sha256")"
VEC_URL_TEMPLATE="$(read_pin .sqliteVec.urlTemplate)"

if [[ -z "${VEC_ASSET}" || -z "${VEC_MEMBER}" || -z "${VEC_SHA}" ]]; then
  echo "error: no sqlite-vec pin for RID ${RID} in ${PINS}" >&2
  exit 1
fi

SIDECAR_URL="${SIDECAR_URL_TEMPLATE/\{VER\}/${VERSION}}"
SIDECAR_URL="${SIDECAR_URL/\{asset\}/${SIDECAR_ASSET}}"
VEC_URL="${VEC_URL_TEMPLATE/\{VER\}/${VEC_VERSION}}"
VEC_URL="${VEC_URL/\{asset\}/${VEC_ASSET}}"

# Staging lives OUTSIDE the repo so a failed verification cannot leave debris in .tools/.
STAGING="$(mktemp -d "${TMPDIR:-/tmp}/miller-semantic-restore.XXXXXX")"
cleanup_staging() {
  rm -rf "${STAGING}"
}
trap cleanup_staging EXIT

echo "Restoring julie-semantic-sidecar v${VERSION} for ${TRIPLE}"
echo "  url:    ${SIDECAR_URL}"
echo "  sha256: ${SIDECAR_SHA}"
curl -fsSL "${SIDECAR_URL}" -o "${STAGING}/${SIDECAR_ASSET}"
verify_sha "${STAGING}/${SIDECAR_ASSET}" "${SIDECAR_SHA}"
echo "  sha256 OK"

echo "Restoring sqlite-vec v${VEC_VERSION} (${VEC_MEMBER}) for ${RID}"
echo "  url:    ${VEC_URL}"
echo "  sha256: ${VEC_SHA}"
curl -fsSL "${VEC_URL}" -o "${STAGING}/${VEC_ASSET}"
verify_sha "${STAGING}/${VEC_ASSET}" "${VEC_SHA}"
echo "  sha256 OK"

# --- extract (sidecar binary sits at the ARCHIVE ROOT; vec0 member likewise) ---
SIDECAR_EXTRACT="${STAGING}/sidecar"
mkdir -p "${SIDECAR_EXTRACT}"
tar -xzf "${STAGING}/${SIDECAR_ASSET}" -C "${SIDECAR_EXTRACT}"
FOUND_SIDECAR="$(find "${SIDECAR_EXTRACT}" -type f -name julie-semantic-sidecar -print -quit)"
if [[ -z "${FOUND_SIDECAR}" ]]; then
  echo "error: julie-semantic-sidecar not found inside ${SIDECAR_ASSET}" >&2
  exit 1
fi

VEC_EXTRACT="${STAGING}/vec"
mkdir -p "${VEC_EXTRACT}"
tar -xzf "${STAGING}/${VEC_ASSET}" -C "${VEC_EXTRACT}" "${VEC_MEMBER}"
if [[ ! -f "${VEC_EXTRACT}/${VEC_MEMBER}" ]]; then
  echo "error: ${VEC_MEMBER} missing from ${VEC_ASSET}" >&2
  exit 1
fi

# --- install: every download verified and extracted before .tools/ is touched ---
mkdir -p "${TOOLS_DIR}"
SIDECAR_BINARY="${TOOLS_DIR}/julie-semantic-sidecar"
VEC_LIBRARY="${TOOLS_DIR}/${VEC_MEMBER}"
mv -f "${FOUND_SIDECAR}" "${SIDECAR_BINARY}"
mv -f "${VEC_EXTRACT}/${VEC_MEMBER}" "${VEC_LIBRARY}"

install_exec_bits "${SIDECAR_BINARY}"
install_exec_bits "${VEC_LIBRARY}"

cleanup_staging
trap - EXIT

echo "Installed: ${SIDECAR_BINARY}"
"${SIDECAR_BINARY}" --version 2>/dev/null || true
echo "Installed: ${VEC_LIBRARY} (sqlite-vec ${VEC_VERSION})"
