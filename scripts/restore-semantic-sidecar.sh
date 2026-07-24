#!/usr/bin/env bash
#
# restore-semantic-sidecar.sh — restore the pinned julie-semantic-sidecar binary and the pinned
# sqlite-vec loadable extension into .tools/.
#
# Reads scripts/semantic-pins.json for both pins: `sidecar` (keyed by rust target triple) and
# `sqliteVec` (keyed by .NET RID). Detects the host platform, downloads each asset into a temp
# staging directory OUTSIDE the repo, VERIFIES its sha256 against the pin BEFORE extracting, then
# installs .tools/julie-semantic-sidecar-runtime/ and .tools/vec0.<ext>. A sha256 mismatch aborts before
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
RUNTIME_DIR="${TOOLS_DIR}/julie-semantic-sidecar-runtime"

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

read_package_field() {
  local manifest="$1" expr="$2"
  if command -v jq >/dev/null 2>&1; then
    jq -r "${expr} // empty" "${manifest}"
  else
    python3 - "$expr" "${manifest}" <<'PY'
import json, sys
expr, path = sys.argv[1], sys.argv[2]
with open(path) as f:
    value = json.load(f)
for key in expr.lstrip(".").split("."):
    value = value.get(key) if isinstance(value, dict) else None
print("" if value is None else value)
PY
  fi
}

verify_package_manifest() {
  local root="$1" expected_triple="$2" manifest="${1}/package-manifest.json"
  if [[ ! -f "${manifest}" ]]; then
    echo "error: package-manifest.json missing from sidecar package" >&2
    exit 1
  fi
  if [[ "$(read_package_field "${manifest}" .sidecar_version)" != "${VERSION}" ]]; then
    echo "error: sidecar package manifest version does not match v${VERSION}" >&2
    exit 1
  fi
  if [[ "$(read_package_field "${manifest}" .rust_target)" != "${expected_triple}" ]]; then
    echo "error: sidecar package manifest target does not match ${expected_triple}" >&2
    exit 1
  fi
  if find "${root}" -mindepth 1 -type d -print -quit | grep -q .; then
    echo "error: sidecar package contains an undeclared directory" >&2
    exit 1
  fi

  local rows
  if command -v jq >/dev/null 2>&1; then
    rows="$(jq -r '.files[] | [.path, .sha256, (.size | tostring), .role] | @tsv' "${manifest}")"
  else
    rows="$(python3 - "${manifest}" <<'PY'
import json, sys
with open(sys.argv[1]) as f:
    manifest = json.load(f)
for item in manifest.get("files", []):
    print("\t".join((item["path"], item["sha256"], str(item["size"]), item["role"])))
PY
)"
  fi

  local expected_list="${root}/.manifest-files" executable_count=0
  : > "${expected_list}"
  while IFS=$'\t' read -r path expected_sha expected_size role; do
    if [[ -z "${path}" || "${path}" == */* || "${path}" == "." || "${path}" == ".." ]]; then
      echo "error: unsafe sidecar package path '${path}'" >&2
      exit 1
    fi
    local file="${root}/${path}"
    if [[ ! -f "${file}" ]]; then
      echo "error: manifest file missing from sidecar package: ${path}" >&2
      exit 1
    fi
    verify_sha "${file}" "${expected_sha}"
    if [[ "$(wc -c < "${file}" | tr -d ' ')" != "${expected_size}" ]]; then
      echo "error: size mismatch for sidecar package file ${path}" >&2
      exit 1
    fi
    if [[ "${role}" == "executable" ]]; then
      executable_count=$((executable_count + 1))
      if [[ "${path}" != "julie-semantic-sidecar" ]]; then
        echo "error: unexpected sidecar executable path '${path}'" >&2
        exit 1
      fi
    fi
    printf '%s\n' "${path}" >> "${expected_list}"
  done <<< "${rows}"

  if [[ "${executable_count}" != "1" ]]; then
    echo "error: sidecar package manifest must declare exactly one executable" >&2
    exit 1
  fi

  local actual_list="${root}/.actual-files"
  find "${root}" -maxdepth 1 -type f \
    ! -name package-manifest.json \
    ! -name .manifest-files \
    ! -name .actual-files \
    -exec basename {} \; | LC_ALL=C sort > "${actual_list}"
  LC_ALL=C sort -o "${expected_list}" "${expected_list}"
  if [[ -n "$(uniq -d "${expected_list}")" ]]; then
    echo "error: sidecar package manifest contains duplicate paths" >&2
    exit 1
  fi
  if ! cmp -s "${expected_list}" "${actual_list}"; then
    echo "error: sidecar package contents do not match package-manifest.json" >&2
    exit 1
  fi
  rm -f "${expected_list}" "${actual_list}"
}

if [[ "${1:-}" == "--verify-package" ]]; then
  if [[ $# -ne 3 ]]; then
    echo "error: --verify-package requires PACKAGE_ROOT and RUST_TARGET" >&2
    exit 64
  fi
  verify_package_manifest "$2" "$3"
  echo "Verified sidecar package: $2"
  exit 0
fi

install_runtime_directory() {
  local source="$1"
  local candidate="${TOOLS_DIR}/.julie-semantic-sidecar-runtime.candidate.$$"
  local backup="${TOOLS_DIR}/.julie-semantic-sidecar-runtime.backup.$$"
  rm -rf "${candidate}" "${backup}"
  cp -R "${source}" "${candidate}"
  if [[ -e "${RUNTIME_DIR}" ]]; then
    mv "${RUNTIME_DIR}" "${backup}"
  fi
  if ! mv "${candidate}" "${RUNTIME_DIR}"; then
    [[ ! -e "${RUNTIME_DIR}" && -e "${backup}" ]] && mv "${backup}" "${RUNTIME_DIR}"
    echo "error: failed to install semantic sidecar runtime" >&2
    exit 1
  fi
  rm -rf "${backup}"
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
  SOURCE_RUNTIME="${TOOLS_DIR}/.julie-semantic-sidecar-source.$$"
  BINARY="${RUNTIME_DIR}/julie-semantic-sidecar"
  SOURCE_BINARY="${SOURCE_ROOT}/target/release/julie-semantic-sidecar"

  echo "Building julie-semantic-sidecar v${VERSION} from source: ${SOURCE_ROOT}"
  cargo build --manifest-path "${SOURCE_MANIFEST}" --release --bin julie-semantic-sidecar
  if [[ ! -f "${SOURCE_BINARY}" ]]; then
    echo "error: expected build output not found: ${SOURCE_BINARY}" >&2
    exit 1
  fi

  VERSION_OUTPUT="$("${SOURCE_BINARY}" --version 2>/dev/null || true)"
  if [[ "${VERSION_OUTPUT}" != julie-semantic-sidecar*"${VERSION}"* ]]; then
    echo "error: built binary does not report pinned sidecar version ${VERSION}" >&2
    echo "  actual: ${VERSION_OUTPUT:-"(no --version output)"}" >&2
    exit 1
  fi

  rm -rf "${SOURCE_RUNTIME}"
  mkdir -p "${SOURCE_RUNTIME}"
  cp "${SOURCE_BINARY}" "${SOURCE_RUNTIME}/julie-semantic-sidecar"
  install_runtime_directory "${SOURCE_RUNTIME}"
  rm -rf "${SOURCE_RUNTIME}"
  install_exec_bits "${BINARY}"
  rm -f "${TOOLS_DIR}/julie-semantic-sidecar" "${TOOLS_DIR}/julie-semantic-sidecar.exe"

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

# --- extract ---
SIDECAR_EXTRACT="${STAGING}/sidecar"
mkdir -p "${SIDECAR_EXTRACT}"
tar -xzf "${STAGING}/${SIDECAR_ASSET}" -C "${SIDECAR_EXTRACT}"
verify_package_manifest "${SIDECAR_EXTRACT}" "${TRIPLE}"

VEC_EXTRACT="${STAGING}/vec"
mkdir -p "${VEC_EXTRACT}"
tar -xzf "${STAGING}/${VEC_ASSET}" -C "${VEC_EXTRACT}" "${VEC_MEMBER}"
if [[ ! -f "${VEC_EXTRACT}/${VEC_MEMBER}" ]]; then
  echo "error: ${VEC_MEMBER} missing from ${VEC_ASSET}" >&2
  exit 1
fi

# --- install: every download verified and extracted before .tools/ is touched ---
mkdir -p "${TOOLS_DIR}"
SIDECAR_BINARY="${RUNTIME_DIR}/julie-semantic-sidecar"
VEC_LIBRARY="${TOOLS_DIR}/${VEC_MEMBER}"
install_runtime_directory "${SIDECAR_EXTRACT}"
mv -f "${VEC_EXTRACT}/${VEC_MEMBER}" "${VEC_LIBRARY}"

install_exec_bits "${SIDECAR_BINARY}"
install_exec_bits "${VEC_LIBRARY}"
rm -f "${TOOLS_DIR}/julie-semantic-sidecar" "${TOOLS_DIR}/julie-semantic-sidecar.exe"

cleanup_staging
trap - EXIT

echo "Installed: ${SIDECAR_BINARY}"
"${SIDECAR_BINARY}" --version 2>/dev/null || true
echo "Installed: ${VEC_LIBRARY} (sqlite-vec ${VEC_VERSION})"
