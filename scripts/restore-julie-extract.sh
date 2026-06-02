#!/usr/bin/env bash
#
# restore-julie-extract.sh — restore the pinned julie-extract binary into .tools/.
#
# Reads scripts/julie-pins.json for the version, per-triple asset name + sha256, and the URL template.
# Detects the host platform, downloads the matching release archive from anortham/julie-extractors,
# VERIFIES its sha256 against the pin (julie-extractors publishes no checksum assets — these were
# download-verified and committed), stages the archive and installs only the julie-extract binary, sets the
# exec bit, clears the macOS quarantine xattr, and removes the archive. Fails loudly on unsupported
# platforms.
#
# While a contract bump is staged before GitHub release assets publish, pass --from-source or set
# MILLER_JULIE_SOURCE=/path/to/julie-extractors to build julie-extract from that checkout and copy it
# into .tools/.
#
# Only four triples exist upstream: aarch64-apple-darwin, x86_64-apple-darwin, x86_64-unknown-linux-gnu,
# x86_64-pc-windows-msvc. There is NO linux-arm64 and NO windows-arm64 asset.
#
# Usage:
#   bash scripts/restore-julie-extract.sh
#   MILLER_JULIE_SOURCE=~/source/julie-extractors bash scripts/restore-julie-extract.sh --from-source
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
PINS="${SCRIPT_DIR}/julie-pins.json"
TOOLS_DIR="${REPO_ROOT}/.tools"

if [[ ! -f "${PINS}" ]]; then
  echo "error: pins file not found at ${PINS}" >&2
  exit 1
fi

# --- JSON reader (prefer jq; fall back to python3 — both avoid a hard single-dep) ---
read_pin() {
  # $1 = jq path expression (leading dot), e.g. .version  or  .assets["<triple>"].name
  local expr="$1"
  if command -v jq >/dev/null 2>&1; then
    jq -r "${expr}" "${PINS}"
  elif command -v python3 >/dev/null 2>&1; then
    python3 - "$expr" "${PINS}" <<'PY'
import json, sys
expr, path = sys.argv[1], sys.argv[2]
with open(path) as f:
    data = json.load(f)
# Translate the tiny subset of jq syntax we use into Python lookups.
expr = expr.lstrip(".")
if expr == "version":
    val = data["version"]
elif expr == "urlTemplate":
    val = data["urlTemplate"]
elif expr == "archiveInnerPathTemplate":
    val = data.get("archiveInnerPathTemplate", "")
elif expr.startswith('assets["'):
    triple = expr[len('assets["'):].split('"]')[0]
    field = expr.rsplit(".", 1)[1]
    val = data["assets"].get(triple, {}).get(field, "")
else:
    val = ""
print(val if val is not None else "")
PY
  else
    echo "error: need either jq or python3 to read ${PINS}" >&2
    exit 1
  fi
}

VERSION="$(read_pin .version)"

FROM_SOURCE=""
SOURCE_REQUESTED=0
if [[ "${1:-}" == "--from-source" ]]; then
  SOURCE_REQUESTED=1
  shift
  FROM_SOURCE="${1:-${MILLER_JULIE_SOURCE:-}}"
  if [[ $# -gt 0 ]]; then
    shift
  fi
elif [[ -n "${MILLER_JULIE_SOURCE:-}" ]]; then
  FROM_SOURCE="${MILLER_JULIE_SOURCE}"
fi

if [[ "${SOURCE_REQUESTED}" == "1" && -z "${FROM_SOURCE}" ]]; then
  echo "error: --from-source requires a path argument or MILLER_JULIE_SOURCE" >&2
  exit 1
fi

if [[ -n "${FROM_SOURCE}" ]]; then
  SOURCE_ROOT="$(cd "${FROM_SOURCE}" && pwd)"
  SOURCE_MANIFEST="${SOURCE_ROOT}/Cargo.toml"
  if [[ ! -f "${SOURCE_MANIFEST}" ]]; then
    echo "error: from-source path is not a Julie checkout: ${SOURCE_ROOT}" >&2
    exit 1
  fi
  if ! command -v cargo >/dev/null 2>&1; then
    echo "error: cargo is required for --from-source restore" >&2
    exit 1
  fi

  mkdir -p "${TOOLS_DIR}"
  BINARY="${TOOLS_DIR}/julie-extract"
  SOURCE_BINARY="${SOURCE_ROOT}/target/release/julie-extract"

  echo "Building julie-extract v${VERSION} from source: ${SOURCE_ROOT}"
  cargo build --manifest-path "${SOURCE_MANIFEST}" --release -p julie-extract-cli --bin julie-extract
  if [[ ! -f "${SOURCE_BINARY}" ]]; then
    echo "error: expected build output not found: ${SOURCE_BINARY}" >&2
    exit 1
  fi

  cp "${SOURCE_BINARY}" "${BINARY}"
  chmod +x "${BINARY}"
  VERSION_OUTPUT="$("${BINARY}" --version 2>/dev/null || true)"
  if [[ "${VERSION_OUTPUT}" != julie-extract* ]]; then
    echo "error: restored binary does not self-identify as julie-extract" >&2
    echo "  actual: ${VERSION_OUTPUT:-"(no --version output)"}" >&2
    exit 1
  fi

  echo "Installed: ${BINARY}"
  "${BINARY}" --version 2>/dev/null || true
  exit 0
fi

# --- detect platform -> triple ---
OS="$(uname -s)"
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
  echo "error: unsupported platform '${OS}/${ARCH}'. julie-extract v$(read_pin .version) publishes only:" >&2
  echo "  macOS arm64 (aarch64-apple-darwin), macOS x64 (x86_64-apple-darwin)," >&2
  echo "  Linux x64 (x86_64-unknown-linux-gnu), Windows x64 (x86_64-pc-windows-msvc, via the .ps1 script)." >&2
  echo "  No linux-arm64 / windows-arm64 prebuilt asset exists; build from source with cargo." >&2
  exit 1
fi

ASSET="$(read_pin ".assets[\"${TRIPLE}\"].name")"
ASSET="${ASSET/\{VER\}/${VERSION}}"
SHA256="$(read_pin ".assets[\"${TRIPLE}\"].sha256")"
URL_TEMPLATE="$(read_pin .urlTemplate)"

if [[ -z "${ASSET}" || -z "${SHA256}" ]]; then
  echo "error: no published asset pin for julie-extract v${VERSION} / ${TRIPLE} in ${PINS}" >&2
  echo "  Until release assets publish, run:" >&2
  echo "  MILLER_JULIE_SOURCE=/path/to/julie-extractors bash scripts/restore-julie-extract.sh --from-source" >&2
  exit 1
fi

URL="${URL_TEMPLATE/\{VER\}/${VERSION}}"
URL="${URL/\{asset\}/${ASSET}}"

mkdir -p "${TOOLS_DIR}"
ARCHIVE="${TOOLS_DIR}/${ASSET}"
BINARY="${TOOLS_DIR}/julie-extract"

echo "Restoring julie-extract v${VERSION} for ${TRIPLE}"
echo "  url:    ${URL}"
echo "  sha256: ${SHA256}"

# --- download ---
curl -fsSL "${URL}" -o "${ARCHIVE}"

# --- verify sha256 (shasum on macOS, sha256sum on Linux) ---
verify_sha() {
  local file="$1" expected="$2" actual=""
  if command -v shasum >/dev/null 2>&1; then
    actual="$(shasum -a 256 "${file}" | awk '{print $1}')"
  elif command -v sha256sum >/dev/null 2>&1; then
    actual="$(sha256sum "${file}" | awk '{print $1}')"
  else
    echo "error: need shasum or sha256sum to verify the download" >&2
    exit 1
  fi
  if [[ "${actual}" != "${expected}" ]]; then
    echo "error: sha256 mismatch for ${file}" >&2
    echo "  expected: ${expected}" >&2
    echo "  actual:   ${actual}" >&2
    rm -f "${file}"
    exit 1
  fi
}
verify_sha "${ARCHIVE}" "${SHA256}"
echo "  sha256 OK"

# --- extract julie-extract from the archive (v1 archives nest under dist/{triple}/) ---
STAGING="$(mktemp -d "${TOOLS_DIR}/julie-extract-stage.XXXXXX")"
cleanup_staging() {
  rm -rf "${STAGING}"
}
trap cleanup_staging EXIT

tar -xzf "${ARCHIVE}" -C "${STAGING}"
FOUND="$(find "${STAGING}" -type f -path "*/dist/${TRIPLE}/julie-extract" -print -quit)"
if [[ -z "${FOUND}" ]]; then
  echo "error: julie-extract not found under dist/${TRIPLE}/ in ${ASSET}" >&2
  exit 1
fi
mv "${FOUND}" "${BINARY}"

# --- exec bit + clear macOS quarantine (ignore if absent) ---
chmod +x "${BINARY}"
if [[ "${OS}" == "Darwin" ]]; then
  xattr -d com.apple.quarantine "${BINARY}" 2>/dev/null || true
fi

# --- cleanup ---
rm -f "${ARCHIVE}"
cleanup_staging
trap - EXIT

echo "Installed: ${BINARY}"
"${BINARY}" --version 2>/dev/null || true
