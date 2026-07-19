#!/usr/bin/env bash
# P0 HARD GATE probe: does the pinned sqlite-vec loadable extension load and function under a
# Native-AOT-published .NET binary?
#
#   scripts/spike-sqlite-vec.sh [--rid <rid>]
#
# Detects the RID, downloads the pinned sqlite-vec asset, verifies its sha256 against
# scripts/spike-pins.json (fails loud on mismatch), publishes spike/SqliteVec.AotSpike with
# Native AOT, RUNS THE PUBLISHED BINARY (not `dotnet run` — the gate is the AOT artifact),
# and echoes the verdict. Exit 0 = PASS.
#
# Downloads are cached outside the repo (SPIKE_CACHE_DIR, default under TMPDIR) so no binary
# ever lands in the working tree. The spike is NOT part of Miller.slnx.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PINS="$REPO_ROOT/scripts/spike-pins.json"
PROJECT="$REPO_ROOT/spike/SqliteVec.AotSpike/SqliteVec.AotSpike.csproj"

RID=""
while [ $# -gt 0 ]; do
  case "$1" in
    --rid) RID="${2:?--rid needs a value}"; shift 2 ;;
    -h|--help) sed -n '2,13p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 64 ;;
  esac
done

detect_rid() {
  local os arch
  os="$(uname -s)"
  arch="$(uname -m)"
  case "$os" in
    Darwin) case "$arch" in
              arm64|aarch64) echo "osx-arm64" ;;
              x86_64) echo "osx-x64" ;;
              *) return 1 ;;
            esac ;;
    Linux)  case "$arch" in
              x86_64) echo "linux-x64" ;;
              *) return 1 ;;
            esac ;;
    MINGW*|MSYS*|CYGWIN*) echo "win-x64" ;;
    *) return 1 ;;
  esac
}

if [ -z "$RID" ]; then
  RID="$(detect_rid)" || { echo "ERROR: unsupported host $(uname -s)/$(uname -m); pass --rid explicitly" >&2; exit 1; }
fi

# jq is preinstalled on every GitHub-hosted runner image; python3 is the local fallback because a
# Windows Git Bash `python3` can be the non-functional Store shim.
if command -v jq >/dev/null 2>&1; then
  pin_field() {
    jq -er --arg rid "$RID" --arg field "$1" '
      .version as $ver
      | .urlTemplate as $tpl
      | (.assets[$rid] // ("no sqlite-vec pin for RID " + $rid | halt_error)) as $a
      | ($a.name | gsub("\\{VER\\}"; $ver)) as $name
      | {version: $ver,
         member: $a.member,
         sha256: $a.sha256,
         name: $name,
         url: ($tpl | gsub("\\{VER\\}"; $ver) | gsub("\\{asset\\}"; $name))}
      | .[$field]' "$PINS"
  }
elif command -v python3 >/dev/null 2>&1; then
  pin_field() {
    python3 -c "
import json,sys
pins = json.load(open(sys.argv[1]))
rid = sys.argv[2]
if rid not in pins['assets']:
    sys.exit('ERROR: no sqlite-vec pin for RID ' + rid)
entry = dict(pins['assets'][rid])
entry['version'] = pins['version']
entry['name'] = entry['name'].replace('{VER}', pins['version'])
entry['url'] = pins['urlTemplate'].replace('{VER}', pins['version']).replace('{asset}', entry['name'])
print(entry[sys.argv[3]])
" "$PINS" "$RID" "$1"
  }
else
  echo "ERROR: need jq or python3 to read $PINS" >&2
  exit 1
fi

VERSION="$(pin_field version)"
ASSET="$(pin_field name)"
MEMBER="$(pin_field member)"
EXPECTED_SHA="$(pin_field sha256)"
URL="$(pin_field url)"

CACHE_DIR="${SPIKE_CACHE_DIR:-${TMPDIR:-/tmp}/miller-sqlite-vec-spike/$VERSION/$RID}"
mkdir -p "$CACHE_DIR"
ARCHIVE="$CACHE_DIR/$ASSET"
EXTENSION="$CACHE_DIR/$MEMBER"

echo "sqlite-vec Native-AOT spike"
echo "  rid       : $RID"
echo "  version   : $VERSION"
echo "  asset     : $ASSET"
echo "  cache     : $CACHE_DIR"
echo

sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
  else shasum -a 256 "$1" | awk '{print $1}'
  fi
}

if [ ! -f "$ARCHIVE" ]; then
  echo "==> downloading $URL"
  curl --fail --silent --show-error --location --output "$ARCHIVE.tmp" "$URL"
  mv "$ARCHIVE.tmp" "$ARCHIVE"
fi

ACTUAL_SHA="$(sha256_of "$ARCHIVE")"
if [ "$ACTUAL_SHA" != "$EXPECTED_SHA" ]; then
  echo "::error::sha256 MISMATCH for $ASSET" >&2
  echo "  expected: $EXPECTED_SHA" >&2
  echo "  actual  : $ACTUAL_SHA" >&2
  echo "  archive : $ARCHIVE (left in place for inspection)" >&2
  exit 1
fi
echo "==> sha256 verified: $ACTUAL_SHA"

rm -f "$EXTENSION"
tar -xzf "$ARCHIVE" -C "$CACHE_DIR" "$MEMBER"
[ -f "$EXTENSION" ] || { echo "ERROR: $MEMBER missing from $ASSET" >&2; exit 1; }
chmod +x "$EXTENSION" 2>/dev/null || true
echo "==> extracted $MEMBER"

PUBLISH_DIR="$CACHE_DIR/publish"
rm -rf "$PUBLISH_DIR"
echo "==> dotnet publish -c Release -r $RID (PublishAot=true)"
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true -o "$PUBLISH_DIR"

BINARY="$PUBLISH_DIR/sqlite-vec-aot-spike"
[ "$RID" = "win-x64" ] && BINARY="$BINARY.exe"
[ -x "$BINARY" ] || { echo "ERROR: published AOT binary not found at $BINARY" >&2; exit 1; }

BINARY_BYTES="$(wc -c < "$BINARY" | tr -d ' ')"
echo "==> published AOT binary: $BINARY ($BINARY_BYTES bytes)"
echo

set +e
"$BINARY" "$EXTENSION"
STATUS=$?
set -e

echo
if [ "$STATUS" -eq 0 ]; then
  echo "SPIKE VERDICT [$RID]: PASS (aot binary $BINARY_BYTES bytes, sqlite-vec $VERSION)"
else
  echo "::error::SPIKE VERDICT [$RID]: FAIL (exit $STATUS) — sqlite-vec does not work under Native AOT on this RID"
fi
exit "$STATUS"
