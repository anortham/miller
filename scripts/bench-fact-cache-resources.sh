#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

fixture="sqlite-synthetic"
workspaces=2
revisions=2
budget_mb=256
runs=5
output=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --fixture|--workspaces|--revisions|--budget-mb|--runs|--output)
      if [[ $# -lt 2 || -z "${2-}" || "${2-}" == --* ]]; then
        echo "Error: $1 requires a value." >&2
        exit 1
      fi
      ;;
  esac
  case "$1" in
    --fixture)
      fixture="$2"
      shift 2
      ;;
    --workspaces)
      workspaces="$2"
      shift 2
      ;;
    --revisions)
      revisions="$2"
      shift 2
      ;;
    --budget-mb)
      budget_mb="$2"
      shift 2
      ;;
    --runs)
      runs="$2"
      shift 2
      ;;
    --output)
      output="$2"
      shift 2
      ;;
    -h|--help)
      cat <<USAGE
Usage: scripts/bench-fact-cache-resources.sh [options]

Options:
  --fixture <name>        Fixture type (default: sqlite-synthetic)
  --workspaces <count>    Number of workspaces (default: 2)
  --revisions <count>     Number of revisions per workspace (default: 2)
  --budget-mb <megabytes> Cache budget in MB (default: 256)
  --runs <count>          Number of benchmark runs (default: 5)
  --output <path>         Output JSON path
USAGE
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      exit 1
      ;;
  esac
done

if [[ "$fixture" != sqlite-synthetic ]]; then
  echo "Error: --fixture must be sqlite-synthetic." >&2
  exit 1
fi

validate_positive_integer() {
  local option="$1" value="$2" maximum="$3" normalized
  local LC_ALL=C
  # Compare decimal strings before arithmetic, so oversized input cannot wrap in Bash.
  normalized="${value#"${value%%[!0]*}"}"
  if [[ ! "$value" =~ ^[0-9]+$ || -z "$normalized" ]] \
    || [[ ${#normalized} -gt ${#maximum} ]] \
    || { [[ ${#normalized} -eq ${#maximum} ]] && [[ "$normalized" > "$maximum" ]]; }; then
    echo "Error: $option must be a positive integer no greater than $maximum." >&2
    exit 1
  fi
}

# Match the benchmark's loop/index bounds and checked MiB-to-byte conversion.
validate_positive_integer --runs "$runs" 2147483646
validate_positive_integer --workspaces "$workspaces" 2147483646
validate_positive_integer --revisions "$revisions" 2147483637
validate_positive_integer --budget-mb "$budget_mb" 8796093022207

if [[ -z "$output" ]]; then
  output="$ROOT/artifacts/fact-cache-benchmark/results-$(date -u +%Y%m%dT%H%M%SZ).json"
elif [[ "$output" != /* ]]; then
  output="$(pwd)/$output"
fi

if [[ -d "$output" ]]; then
  echo "Error: --output must name a file, not a directory: $output" >&2
  exit 1
fi

mkdir -p "$(dirname "$output")"
benchmark_tmp="$(mktemp -d "$(dirname "$output")/.fact-cache-benchmark.XXXXXX")"
trap 'rm -f -- "$benchmark_tmp/results.json"; rmdir -- "$benchmark_tmp"' EXIT

export BENCH_FACT_CACHE_FIXTURE="$fixture"
export BENCH_FACT_CACHE_WORKSPACES="$workspaces"
export BENCH_FACT_CACHE_REVISIONS="$revisions"
export BENCH_FACT_CACHE_BUDGET_MB="$budget_mb"
export BENCH_FACT_CACHE_RUNS="$runs"
export BENCH_FACT_CACHE_OUTPUT="$benchmark_tmp/results.json"

echo "Running fact cache resource benchmark..."
echo "  Fixture:    $fixture"
echo "  Workspaces: $workspaces"
echo "  Revisions:  $revisions"
echo "  Budget:     ${budget_mb} MB"
echo "  Runs:       $runs"
echo "  Output:     $output"

dotnet test "$ROOT/tests/Miller.Tests/Miller.Tests.csproj" \
  --filter "FullyQualifiedName~FactCacheResourceAccountingTests.Benchmark_FactCacheResources" \
  -c Release \
  -v q \
  --nologo

if [[ -s "$BENCH_FACT_CACHE_OUTPUT" ]]; then
  mv -- "$BENCH_FACT_CACHE_OUTPUT" "$output"
  echo ""
  echo "Benchmark completed successfully. Output written to $output:"
  cat "$output"
  echo ""
else
  echo "Error: Output file $output was not generated." >&2
  exit 1
fi
