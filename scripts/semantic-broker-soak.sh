#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
tools_root="$repo_root/.tools"
miller_home=""
output_dir=""
duration_seconds=1800
timeout_seconds=120

while (($#)); do
  case "$1" in
    --tools-root) tools_root="$2"; shift 2 ;;
    --miller-home) miller_home="$2"; shift 2 ;;
    --output-dir) output_dir="$2"; shift 2 ;;
    --duration-seconds) duration_seconds="$2"; shift 2 ;;
    --duration-minutes) duration_seconds=$((10#$2 * 60)); shift 2 ;;
    --timeout-seconds) timeout_seconds="$2"; shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

command -v jq >/dev/null || {
  echo "semantic broker soak requires jq" >&2
  exit 2
}

if [[ -z "$miller_home" ]]; then
  miller_home="$(mktemp -d "/tmp/miller-semantic-soak.XXXXXX")"
fi
if [[ -z "$output_dir" ]]; then
  output_dir="$repo_root/artifacts/semantic-broker-soak/$(date -u +%Y%m%dT%H%M%SZ)"
fi
mkdir -p "$output_dir" "$miller_home"

candidate="$tools_root/julie-semantic-sidecar-runtime/julie-semantic-sidecar"
[[ "$(uname -s)" == MINGW* || "$(uname -s)" == MSYS* ]] &&
  candidate="${candidate}.exe"
summary="$output_dir/summary.json"

write_skip() {
  jq -n \
    --arg status skipped \
    --arg reason "$1" \
    --arg candidate "$candidate" \
    '{status:$status,reason:$reason,candidate:$candidate,acceptance:{releaseGate:true}}' >"$summary"
  echo "$1" >&2
  exit 77
}

[[ -x "$candidate" ]] ||
  write_skip "Broker-capable julie-semantic-sidecar not found at $candidate. Restore the pinned package with scripts/restore-semantic-sidecar.sh."

capability_error="$output_dir/broker-capability.stderr"
set +e
"$candidate" broker >/dev/null 2>"$capability_error"
capability_exit=$?
set -e
if [[ $capability_exit -ne 2 ]] || ! grep -q "broker requires --model" "$capability_error"; then
  write_skip "The candidate does not expose the shared broker CLI. Restore the pinned package with scripts/restore-semantic-sidecar.sh."
fi

dotnet build "$repo_root/scripts/Miller.SemanticBrokerProbe/Miller.SemanticBrokerProbe.csproj" \
  -c Release --nologo >"$output_dir/probe-build.log"
probe="$repo_root/scripts/Miller.SemanticBrokerProbe/bin/Release/net10.0/miller-semantic-broker-probe.dll"
candidate_sha256="$(shasum -a 256 "$candidate" | awk '{print $1}')"
candidate_version="$("$candidate" --version | tr -d '\r')"
default_model="bge-small-en-v1.5-f32"
fallback_model="qwen3-0.6b-f16"
pids=()
normal_exit_codes=()
normal_expected_count=0
normal_complete_count=0
observed_expected_kill_count=0

cleanup() {
  for pid in "${pids[@]:-}"; do
    task8_command="$(ps -p "$pid" -o command= 2>/dev/null || true)"
    if [[ "$task8_command" == *"$probe"* ]]; then
      kill "$pid" 2>/dev/null || true
    fi
  done
  while read -r broker_pid; do
    [[ -n "$broker_pid" ]] && kill "$broker_pid" 2>/dev/null || true
  done < <(
    ps -Ao pid=,command= |
      awk -v candidate="$candidate" -v home="$miller_home" \
        'index($0, candidate) && index($0, home "/semantic/") && / broker / {print $1}'
  )
}
trap cleanup EXIT INT TERM

gpu_memory() {
  if command -v nvidia-smi >/dev/null; then
    nvidia-smi --query-gpu=memory.used --format=csv,noheader,nounits 2>/dev/null |
      awk '{sum += $1} END {print sum + 0}'
  else
    echo null
  fi
}

broker_tree() {
  ps -Ao pid=,ppid=,command= |
    awk -v candidate="$candidate" -v home="$miller_home" \
      'index($0, candidate) && index($0, home "/semantic/") && / broker / && !/awk/ {print}' >"$1"
}

broker_count() {
  ps -Ao command= |
    awk -v candidate="$candidate" -v home="$miller_home" \
      'index($0, candidate) && index($0, home "/semantic/") && / broker / && !/awk/ {count++} END {print count + 0}'
}

start_probe() {
  local label="$1"
  local run_seconds="$2"
  local model="${3:-$default_model}"
  local out="$output_dir/$label.jsonl"
  local err="$output_dir/$label.stderr"
  dotnet "$probe" \
    --tools-root "$tools_root" \
    --miller-home "$miller_home" \
    --model "$model" \
    --label "$label" \
    --duration-seconds "$run_seconds" \
    --startup-timeout-seconds "$timeout_seconds" \
    --request-timeout-seconds 30 \
    --grace-seconds 30 \
    --interval-ms 50 \
    --batch-size 8 >"$out" 2>"$err" &
  STARTED_PID=$!
  pids+=("$STARTED_PID")
}

wait_ready() {
  local file="$1"
  local deadline=$((SECONDS + timeout_seconds))
  until grep -q '"event":"ready"' "$file" 2>/dev/null; do
    ((SECONDS < deadline)) || return 1
    sleep 0.1
  done
}

wait_process() {
  local pid="$1"
  local process_timeout="${2:-$timeout_seconds}"
  local deadline=$((SECONDS + process_timeout))
  while kill -0 "$pid" 2>/dev/null; do
    ((SECONDS < deadline)) || {
      kill "$pid" 2>/dev/null || true
      return 124
    }
    sleep 0.1
  done
  wait "$pid"
}

record_normal() {
  local label="$1"
  local pid="$2"
  local process_timeout="${3:-$timeout_seconds}"
  local exit_code
  set +e
  wait_process "$pid" "$process_timeout"
  exit_code=$?
  set -e
  normal_exit_codes+=("$exit_code")
  normal_expected_count=$((normal_expected_count + 1))
  if grep -q '"event":"complete"' "$output_dir/$label.jsonl" 2>/dev/null; then
    normal_complete_count=$((normal_complete_count + 1))
  fi
}

gpu_before="$(gpu_memory)"
broker_tree "$output_dir/process-tree-before.txt"

start_probe warm 2
warm_pid=$STARTED_PID
wait_ready "$output_dir/warm.jsonl"
warm_brokers="$(broker_count)"
gpu_warm="$(gpu_memory)"
record_normal warm "$warm_pid"

same_pids=()
start_probe "same-1" 12
same_pids+=("$STARTED_PID")
wait_ready "$output_dir/same-1.jsonl"
for index in $(seq 2 8); do
  start_probe "same-$index" 5
  same_pids+=("$STARTED_PID")
done
for index in $(seq 2 8); do
  wait_ready "$output_dir/same-$index.jsonl"
done
same_model_brokers="$(broker_count)"
gpu_many="$(gpu_memory)"
broker_tree "$output_dir/process-tree-eight.txt"
for index in $(seq 2 8); do
  record_normal "same-$index" "${same_pids[$((index - 1))]}"
done
record_normal same-1 "${same_pids[0]}"

start_probe non-owner-keeper 10
non_owner_keeper_pid=$STARTED_PID
wait_ready "$output_dir/non-owner-keeper.jsonl"
start_probe non-owner-crash 20
non_owner_pid=$STARTED_PID
wait_ready "$output_dir/non-owner-crash.jsonl"
kill -9 "$non_owner_pid"
set +e
wait "$non_owner_pid" 2>/dev/null
non_owner_kill_exit=$?
set -e
if [[ $non_owner_kill_exit -ne 0 ]] &&
  ! grep -q '"event":"complete"' "$output_dir/non-owner-crash.jsonl" 2>/dev/null; then
  observed_expected_kill_count=$((observed_expected_kill_count + 1))
fi
record_normal non-owner-keeper "$non_owner_keeper_pid"
non_owner_keeper_failed="$(jq -s '[.[] | select(.event=="complete") | .failedCount + .hungCount] | add // 1' \
  "$output_dir/non-owner-keeper.jsonl")"

crash_pids=()
start_probe "broker-crash-1" 15
crash_pids+=("$STARTED_PID")
wait_ready "$output_dir/broker-crash-1.jsonl"
for index in 2 3; do
  start_probe "broker-crash-$index" 10
  crash_pids+=("$STARTED_PID")
done
for index in 2 3; do
  wait_ready "$output_dir/broker-crash-$index.jsonl"
done
broker_pid="$(jq -r 'select(.event=="ready" and .ownerProcessId != null) | .ownerProcessId' \
  "$output_dir"/broker-crash-*.jsonl | head -1)"
broker_kill_unix_ms="$(python3 -c 'import time; print(time.time_ns() // 1_000_000)')"
kill -9 "$broker_pid"
for index in 1 2 3; do
  record_normal "broker-crash-$index" "${crash_pids[$((index - 1))]}"
done
broker_recovery_unix_ms="$(jq -s --argjson killed "$broker_kill_unix_ms" \
  '[.[] | select(.event=="recovered" and .unixTimeMilliseconds > $killed) | .unixTimeMilliseconds] | min // null' \
  "$output_dir"/broker-crash-*.jsonl)"
broker_recovery_seconds=null
if [[ "$broker_recovery_unix_ms" != null ]]; then
  broker_recovery_seconds="$(awk -v recovered="$broker_recovery_unix_ms" -v killed="$broker_kill_unix_ms" \
    'BEGIN {printf "%.3f", (recovered - killed) / 1000}')"
fi

start_probe owner-short 20
owner_short_pid=$STARTED_PID
wait_ready "$output_dir/owner-short.jsonl"
start_probe owner-survivor 10
survivor_pid=$STARTED_PID
wait_ready "$output_dir/owner-survivor.jsonl"
owner_client_pid="$(jq -r 'select(.event=="ready" and .isOwner==true) | .processId' \
  "$output_dir/owner-short.jsonl" | tail -1)"
owner_kill_unix_ms="$(python3 -c 'import time; print(time.time_ns() // 1_000_000)')"
kill -9 "$owner_client_pid"
set +e
wait "$owner_client_pid" 2>/dev/null
owner_kill_exit=$?
set -e
if [[ $owner_kill_exit -ne 0 ]] &&
  ! grep -q '"event":"complete"' "$output_dir/owner-short.jsonl" 2>/dev/null; then
  observed_expected_kill_count=$((observed_expected_kill_count + 1))
fi
record_normal owner-survivor "$survivor_pid"
owner_recovery_unix_ms="$(jq -s --argjson killed "$owner_kill_unix_ms" \
  '[.[] | select(.event=="recovered" and .unixTimeMilliseconds > $killed) | .unixTimeMilliseconds] | min // null' \
  "$output_dir/owner-survivor.jsonl")"
owner_recovery_seconds=null
if [[ "$owner_recovery_unix_ms" != null ]]; then
  owner_recovery_seconds="$(awk -v recovered="$owner_recovery_unix_ms" -v killed="$owner_kill_unix_ms" \
    'BEGIN {printf "%.3f", (recovered - killed) / 1000}')"
fi

start_probe model-old 5 "$default_model"
old_pid=$STARTED_PID
start_probe model-new 5 "$fallback_model"
new_pid=$STARTED_PID
wait_ready "$output_dir/model-old.jsonl" || true
wait_ready "$output_dir/model-new.jsonl" || true
old_endpoint="$(jq -r 'select(.event=="ready") | .endpointIdentity' "$output_dir/model-old.jsonl" | tail -1)"
new_endpoint="$(jq -r 'select(.event=="ready") | .endpointIdentity' "$output_dir/model-new.jsonl" | tail -1)"
accelerated_brokers="$(jq -s '[.[] | select(.event=="ready" and .accelerated==true)] | length' \
  "$output_dir/model-old.jsonl" "$output_dir/model-new.jsonl")"
record_normal model-old "$old_pid"
record_normal model-new "$new_pid"

start_probe soak "$duration_seconds"
soak_pid=$STARTED_PID
record_normal soak "$soak_pid" "$((duration_seconds + timeout_seconds + 60))"

gpu_after="$(gpu_memory)"
broker_tree "$output_dir/process-tree-after.txt"
final_brokers="$(broker_count)"
hung_requests="$(jq -s '[.[] | select(.event=="complete") | .hungCount] | add // 0' \
  "$output_dir"/*.jsonl)"
failed_requests="$(jq -s '[.[] | select(.event=="complete") | .failedCount] | add // 0' \
  "$output_dir"/*.jsonl)"
failed_event_count="$(jq -s '[.[] | select(.event=="failed")] | length' "$output_dir"/*.jsonl)"
normal_exit_codes_json="$(printf '%s\n' "${normal_exit_codes[@]}" | jq -s 'map(tonumber)')"
soak_observed_traffic_ms="$(jq -s '[.[] | select(.event=="complete") | .trafficElapsedMilliseconds] | last // 0' \
  "$output_dir/soak.jsonl")"
warm_accelerated="$(jq -s '[.[] | select(.event=="ready") | .accelerated] | last // false' \
  "$output_dir/warm.jsonl")"
gpu_pass=null
one_delta=null
many_delta=null
if [[ "$gpu_before" != null && "$gpu_warm" != null && "$gpu_many" != null ]]; then
  one_delta=$((gpu_warm - gpu_before))
  many_delta=$((gpu_many - gpu_before))
  if [[ "$warm_accelerated" == true && $warm_brokers -eq 1 && $one_delta -ge 64 ]]; then
    [[ $many_delta -le $((one_delta + 256)) ]] && gpu_pass=true || gpu_pass=false
  fi
fi

jq -n \
  --arg candidate "$candidate" \
  --arg candidateSha256 "$candidate_sha256" \
  --arg candidateVersion "$candidate_version" \
  --argjson warmBrokerCount "$warm_brokers" \
  --argjson sameModelBrokerCount "$same_model_brokers" \
  --argjson acceleratedBrokerCount "$accelerated_brokers" \
  --arg oldEndpoint "$old_endpoint" \
  --arg newEndpoint "$new_endpoint" \
  --argjson brokerRecoverySeconds "$broker_recovery_seconds" \
  --argjson ownerRecoverySeconds "$owner_recovery_seconds" \
  --argjson hungRequests "$hung_requests" \
  --argjson failedRequests "$failed_requests" \
  --argjson failedEventCount "$failed_event_count" \
  --argjson normalProbeExitCodes "$normal_exit_codes_json" \
  --argjson normalProbeExpectedCount "$normal_expected_count" \
  --argjson normalProbeCompleteCount "$normal_complete_count" \
  --argjson expectedKillCount 2 \
  --argjson observedExpectedKillCount "$observed_expected_kill_count" \
  --argjson finalBrokerCount "$final_brokers" \
  --argjson brokerKillUnixTimeMilliseconds "$broker_kill_unix_ms" \
  --argjson brokerRecoveryUnixTimeMilliseconds "$broker_recovery_unix_ms" \
  --argjson ownerKillUnixTimeMilliseconds "$owner_kill_unix_ms" \
  --argjson ownerRecoveryUnixTimeMilliseconds "$owner_recovery_unix_ms" \
  --argjson configuredSoakSeconds "$duration_seconds" \
  --argjson observedSoakTrafficMilliseconds "$soak_observed_traffic_ms" \
  --argjson gpuBefore "$gpu_before" \
  --argjson gpuWarm "$gpu_warm" \
  --argjson gpuMany "$gpu_many" \
  --argjson gpuAfter "$gpu_after" \
  --argjson oneSessionGpuDeltaMiB "$one_delta" \
  --argjson manySessionGpuDeltaMiB "$many_delta" \
  --argjson gpuPass "$gpu_pass" \
  --argjson warmAccelerated "$warm_accelerated" \
  --argjson nonOwnerKeeperFailed "$non_owner_keeper_failed" \
  --argjson soakSeconds "$duration_seconds" \
  '{
    status:"complete",
    candidate:$candidate,
    candidateSha256:$candidateSha256,
    candidateVersion:$candidateVersion,
    soakSeconds:$soakSeconds,
    warmBrokerCount:$warmBrokerCount,
    sameModelBrokerCount:$sameModelBrokerCount,
    oldEndpoint:$oldEndpoint,
    newEndpoint:$newEndpoint,
    acceleratedBrokerCount:$acceleratedBrokerCount,
    brokerRecoverySeconds:$brokerRecoverySeconds,
    ownerRecoverySeconds:$ownerRecoverySeconds,
    hungRequests:$hungRequests,
    failedRequests:$failedRequests,
    failedEventCount:$failedEventCount,
    normalProbeExitCodes:$normalProbeExitCodes,
    normalProbeExpectedCount:$normalProbeExpectedCount,
    normalProbeCompleteCount:$normalProbeCompleteCount,
    expectedKillCount:$expectedKillCount,
    observedExpectedKillCount:$observedExpectedKillCount,
    finalBrokerCount:$finalBrokerCount,
    brokerCrash:{
      killUnixTimeMilliseconds:$brokerKillUnixTimeMilliseconds,
      recoveryUnixTimeMilliseconds:$brokerRecoveryUnixTimeMilliseconds
    },
    ownerCrash:{
      killUnixTimeMilliseconds:$ownerKillUnixTimeMilliseconds,
      recoveryUnixTimeMilliseconds:$ownerRecoveryUnixTimeMilliseconds
    },
    soak:{
      configuredDurationSeconds:$configuredSoakSeconds,
      observedTrafficMilliseconds:$observedSoakTrafficMilliseconds
    },
    gpu:{
      source:"nvidia-smi global memory.used",
      beforeMiB:$gpuBefore,
      warmMiB:$gpuWarm,
      manyMiB:$gpuMany,
      afterMiB:$gpuAfter,
      oneSessionDeltaMiB:$oneSessionGpuDeltaMiB,
      manySessionDeltaMiB:$manySessionGpuDeltaMiB,
      thresholdMiB:256,
      warmBrokerCount:$warmBrokerCount,
      warmAccelerated:$warmAccelerated,
      pass:$gpuPass
    },
    acceptance:{
      sameModelOneBroker:($sameModelBrokerCount==1),
      separateModelEndpoints:($oldEndpoint!="" and $newEndpoint!="" and $oldEndpoint!=$newEndpoint),
      atMostOneAccelerated:($acceleratedBrokerCount<=1),
      brokerRecoveryWithinThirtySeconds:($brokerRecoverySeconds != null and $brokerRecoverySeconds<=30),
      ownerRecoveryWithinThirtySeconds:($ownerRecoverySeconds != null and $ownerRecoverySeconds<=30),
      zeroHung:($hungRequests==0),
      zeroFailed:($failedRequests==0 and $failedEventCount==0),
      normalProbeExitCodes:([$normalProbeExitCodes[] | select(. != 0)] | length == 0),
      normalProbeCompletions:($normalProbeCompleteCount==$normalProbeExpectedCount),
      expectedKills:($observedExpectedKillCount==$expectedKillCount),
      nonOwnerKeeper:($nonOwnerKeeperFailed==0),
      soakDuration:($observedSoakTrafficMilliseconds >= ($configuredSoakSeconds * 1000)),
      noOrphanBroker:($finalBrokerCount==0),
      gpuEffectivelyConstant:$gpuPass
    }
  }' >"$summary"

trap - EXIT INT TERM
cleanup
jq . "$summary"
dotnet "$probe" --verify-summary "$summary"
