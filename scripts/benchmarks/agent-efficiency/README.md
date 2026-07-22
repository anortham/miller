# Miller/Julie agent-efficiency benchmark

This benchmark makes the Miller-versus-Julie decision from paired agent work, not retrieval-only scores. It
runs one fresh Codex and product process per task and arm, verifies the answer against frozen snapshot evidence,
and exports the exact aggregate scorer inputs.

## Frozen runtime

- Codex CLI: `codex-cli 0.145.0`
- Model: `gpt-5.6-sol`
- Reasoning: `medium`
- Tokenizer: `tiktoken==0.13.0`, `o200k_base`
- Per-run limits: 8 MCP calls, 12,000 tool-output tokens, 120 seconds

Use dedicated prepared snapshot clones. Their committed source must remain immutable; the only permitted
uncommitted trees are ordinary top-level `.miller` and `.julie` directories containing the prepared product
artifacts. Symlinks, nested Git metadata, any other dirty path, and any other product/benchmark artifact are
rejected. Do not point the benchmark at an active Julie, `julie-semantic-sidecar`, or developer checkout.
After preparing the artifacts, make each snapshot root non-writable while leaving its `.miller` and `.julie`
directories writable. This prevents read-path refreshes from seeding `.julieignore` into the frozen source tree.

## Runtime identity file

`--runtime-identity` takes JSON with exact process and readiness facts. Every `readiness_commands` entry runs
with that snapshot as its current directory and must print exactly the matching `readiness` object. The product
command must start an MCP stdio server from any listed snapshot root.
The readiness probe is an operator-prepared read-only adapter for the normalized identity object only; it does
not sit in the timed MCP path or alter either product's tools, instructions, or results.

```json
{
  "schema_version": 1,
  "products": {
    "miller": {
      "command": ["/opt/bench/miller", "serve"],
      "version_command": ["/opt/bench/miller", "version"],
      "version": "miller 1.14.0",
      "binary_path": "/opt/bench/miller.dll",
      "binary_sha256": "<sha256>",
      "commit": "<commit>",
      "environment": {
        "HOME": "/opt/bench/product-home",
        "MILLER_SEMANTIC": "on"
      },
      "readiness_commands": {
        "snapshot-001": ["/opt/bench/miller-readiness-probe", "--json"]
      },
      "readiness": {
        "snapshot-001": {
          "ready": true,
          "workspace_identity": "<exact identity>",
          "index_identity": "<exact identity>",
          "vector_identity": "<exact identity>",
          "model_identity": "<exact identity>"
        }
      }
    },
    "julie": {
      "command": ["/opt/bench/julie-server"],
      "version_command": ["/opt/bench/julie-server", "--version"],
      "version": "julie-server <version>",
      "binary_path": "/opt/bench/julie-server",
      "binary_sha256": "<sha256>",
      "commit": "<commit>",
      "environment": {
        "HOME": "/opt/bench/product-home",
        "JULIE_HOME": "/opt/bench/julie-home",
        "JULIE_EMBEDDING_CACHE_DIR": "/opt/bench/julie-cache"
      },
      "readiness_commands": {
        "snapshot-001": ["/opt/bench/julie-readiness-probe", "--json"]
      },
      "readiness": {
        "snapshot-001": {
          "ready": true,
          "workspace_identity": "<exact identity>",
          "index_identity": "<exact identity>",
          "vector_identity": "<exact identity>",
          "model_identity": "<exact identity>"
        }
      }
    }
  }
}
```

The exported identity manifest contains hashes of commands and workspace/index/vector/model identities, not
their raw values or source roots.
Product environments accept only `HOME`, `MILLER_SEMANTIC`, `JULIE_HOME`, and
`JULIE_EMBEDDING_CACHE_DIR`; the safe manifest records their names and a hash of the exact values.
Its top-level `environment_keys` comes directly from the concrete Codex runner's isolated child environment;
the runtime identity file cannot supply or override that policy.

## Run

Create the pinned environment, then preflight before spending an agent call:

```bash
python3 -m venv .venv-agent-efficiency
.venv-agent-efficiency/bin/pip install -r scripts/benchmarks/agent-efficiency/requirements.txt
.venv-agent-efficiency/bin/python scripts/bench-agent-efficiency.py \
  --manifest scripts/benchmarks/agent-efficiency/dev-tasks.json \
  --snapshots scripts/benchmarks/agent-efficiency/dev-snapshots.json \
  --snapshot-root goldfish=/opt/bench/snapshots/goldfish \
  --snapshot-root eros=/opt/bench/snapshots/eros \
  --snapshot-root razorback=/opt/bench/snapshots/razorback \
  --snapshot-root tree-sitter-razor=/opt/bench/snapshots/tree-sitter-razor \
  --snapshot-root tree-sitter-c-sharp=/opt/bench/snapshots/tree-sitter-c-sharp \
  --runtime-identity /opt/bench/runtime-identity.json \
  --arm both --out /opt/bench/runs/visible-001 --seed 731 \
  --model gpt-5.6-sol --reasoning medium --preflight-only
```

Remove `--preflight-only` to run. `--arm both` is mandatory because a decision requires paired arms. The
controller balances the seeded first-arm order without changing prompts, runs one repetition on initial
agreement, and runs exactly three repetitions per arm on initial disagreement. A harness fault voids the pair;
both arms restart under a new pair attempt and the reason remains in `void-ledger.jsonl`.

Completed task/arm/repetition directories are immutable. A rerun reuses them only after their identity and every
artifact digest match. Partial, corrupt, or mismatched state is refused. A completed export is likewise resumed
only after every evidence-manifest digest verifies.

## Output and scoring

`raw/` contains private Codex events, MCP trajectories, per-run results, completion manifests, and seeded arm
order. `exports/` contains the scorer JSONL, safe identity manifest, evidence manifest, and the copyable score
command. Raw sealed output must be outside every repository.

```bash
export AGENT_EFFICIENCY_EXPORT=/opt/bench/runs/visible-001/exports
sh "$AGENT_EFFICIENCY_EXPORT/agent-score-command.txt"
```

Only `aggregate.json` and the safe identity/evidence manifests may cross the sealed-run boundary. Follow
`SEALED-AGENT-PROTOCOL.md` for the one-time sealed decision run.
