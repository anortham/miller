#!/usr/bin/env bash
set -euo pipefail

fixture=""
mode=""
runs=""
output=""
while (($#)); do
  case "$1" in
    --fixture) fixture="${2:-}"; shift 2 ;;
    --mode) mode="${2:-}"; shift 2 ;;
    --runs) runs="${2:-}"; shift 2 ;;
    --output) output="${2:-}"; shift 2 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done

if [[ "$fixture" != "sqlite-synthetic" || "$mode" != "both" || ! "$runs" =~ ^[1-9][0-9]*$ || -z "$output" ]]; then
  echo "usage: $0 --fixture sqlite-synthetic --mode both --runs N --output PATH" >&2
  exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
output="$(realpath -m "$output")"
mkdir -p "$(dirname "$output")"
scratch="$(mktemp -d)"
trap 'rm -rf -- "$scratch"' EXIT

write_source_manifest() {
  local destination="$1"
  {
    for file in Directory.Build.props Directory.Build.targets global.json NuGet.config \
      scripts/bench-sidecar-convergence.sh scripts/julie-pins.json; do
      [[ -f "$repo_root/$file" ]] && printf '%s\n' "$repo_root/$file"
    done
    find "$repo_root/src/Miller.Core" "$repo_root/src/Miller.Indexing" \
      "$repo_root/src/Miller.Server" "$repo_root/tests/Miller.Tests" \
      -type f ! -path '*/bin/*' ! -path '*/obj/*' \
      \( -name '*.cs' -o -name '*.csproj' -o -name '*.props' -o -name '*.targets' -o -name '*.json' -o -name '*.resx' \)
  } | sort -u | while IFS= read -r absolute; do
    relative="${absolute#"$repo_root/"}"
    printf '%s  %s\n' "$(sha256sum "$absolute" | awk '{print $1}')" "$relative"
  done > "$destination"
}

write_runtime_manifest() {
  local destination="$1"
  local runtime="$repo_root/tests/Miller.Tests/bin/Release/net10.0"
  find "$runtime" -maxdepth 1 -type f \( -name '*.dll' -o -name '*.json' \) | sort | \
    while IFS= read -r absolute; do
      relative="${absolute#"$repo_root/"}"
      printf '%s  %s\n' "$(sha256sum "$absolute" | awk '{print $1}')" "$relative"
    done > "$destination"
}

commit="$(git -C "$repo_root" rev-parse HEAD)"
pin="$(sed -n 's/^[[:space:]]*"version": "\([^"]*\)",/\1/p' "$repo_root/scripts/julie-pins.json" | head -1)"
source_manifest="$scratch/source-manifest.txt"
write_source_manifest "$source_manifest"
source_hash_before="$(sha256sum "$source_manifest" | awk '{print $1}')"
dotnet_command="${MILLER_SIDECAR_BENCH_DOTNET:-dotnet}"

flock --close /tmp/miller-remaining-plans-dotnet.lock \
  "$dotnet_command" build "$repo_root/tests/Miller.Tests/Miller.Tests.csproj" -c Release --nologo >/dev/null

runtime_manifest="$scratch/runtime-manifest.txt"
write_runtime_manifest "$runtime_manifest"
runtime_hash_before="$(sha256sum "$runtime_manifest" | awk '{print $1}')"

for ((run = 1; run <= runs; run++)); do
  source_check="$scratch/source-check.txt"
  write_source_manifest "$source_check"
  if ! cmp -s "$source_manifest" "$source_check"; then
    echo "benchmark source changed after build" >&2
    exit 1
  fi
  measurement="$scratch/measurement-$run.json"
  rss="$scratch/rss-$run.txt"
  wall="$scratch/wall-$run.txt"
  flock --close /tmp/miller-remaining-plans-dotnet.lock \
    bash -c '
      set -euo pipefail
      start_ns="$(date +%s%N)"
      env MILLER_SIDECAR_BENCH_OUTPUT="$1" \
        /usr/bin/time -f "%M" -o "$2" \
        "$5" test "$4/tests/Miller.Tests/Miller.Tests.csproj" -c Release \
          --no-build --no-restore --nologo \
          --filter "FullyQualifiedName~SidecarConvergenceCostTests.Benchmark_fixture_emits_real_content_and_search_measurements" \
          >/dev/null
      end_ns="$(date +%s%N)"
      echo $(((end_ns - start_ns) / 1000000)) > "$3"
    ' _ "$measurement" "$rss" "$wall" "$repo_root" "$dotnet_command"
done

write_source_manifest "$scratch/source-final.txt"
if ! cmp -s "$source_manifest" "$scratch/source-final.txt"; then
  echo "benchmark source changed during measurement" >&2
  exit 1
fi
write_runtime_manifest "$scratch/runtime-final.txt"
if ! cmp -s "$runtime_manifest" "$scratch/runtime-final.txt"; then
  echo "benchmark runtime changed during measurement" >&2
  exit 1
fi

python3 - "$scratch" "$output" "$commit" "$source_hash_before" "$runtime_hash_before" "$pin" "$fixture" "$mode" "$runs" <<'PY'
import json
import pathlib
import sys

scratch, output, commit, source_hash, runtime_hash, pin, fixture, mode, runs = sys.argv[1:]
root = pathlib.Path(scratch)
def manifest(name):
    return [
        {"sha256": line[:64], "path": line[66:]}
        for line in (root / name).read_text().splitlines()
    ]
items = []
for number in range(1, int(runs) + 1):
    phases = json.loads((root / f"measurement-{number}.json").read_text())
    items.append({
        "run": number,
        "state": "fresh_process_fresh_fixture_os_cache_uncontrolled",
        "process_wall_ms": int((root / f"wall-{number}.txt").read_text()),
        "process_peak_rss_kb": int((root / f"rss-{number}.txt").read_text()),
        "phases": phases,
    })
report = {
    "schema_version": 1,
    "commit": commit,
    "relevant_source_sha256": source_hash,
    "runtime_manifest_sha256": runtime_hash,
    "relevant_source_manifest": manifest("source-manifest.txt"),
    "runtime_manifest": manifest("runtime-manifest.txt"),
    "julie_extract_pin": pin,
    "fixture": fixture,
    "mode": mode,
    "runs": int(runs),
    "build_in_measurement": False,
    "process_scope": "dotnet test and VSTest host overhead plus one real SQLite fixture and parity validation",
    "rss_scope": "peak RSS reported by GNU time for the measured dotnet test process tree",
    "results": items,
}
pathlib.Path(output).write_text(json.dumps(report, indent=2) + "\n")
PY

echo "$output"
