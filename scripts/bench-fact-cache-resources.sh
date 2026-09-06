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

if [[ -z "$output" ]]; then
  output="$ROOT/artifacts/fact-cache-benchmark/results-$(date -u +%Y%m%dT%H%M%SZ).json"
elif [[ "$output" != /* ]]; then
  output="$(pwd)/$output"
fi

mkdir -p "$(dirname "$output")"

export BENCH_FACT_CACHE_FIXTURE="$fixture"
export BENCH_FACT_CACHE_WORKSPACES="$workspaces"
export BENCH_FACT_CACHE_REVISIONS="$revisions"
export BENCH_FACT_CACHE_BUDGET_MB="$budget_mb"
export BENCH_FACT_CACHE_RUNS="$runs"
export BENCH_FACT_CACHE_OUTPUT="$output"

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

if [[ -f "$output" ]]; then
  echo ""
  echo "Benchmark completed successfully. Output written to $output:"
  cat "$output"
  echo ""
else
  echo "Error: Output file $output was not generated." >&2
  exit 1
fi
