# Agent-outcomes harness dry qualification

Status: **harness-qualified; campaign-not-run**.

No provider, model, authentication, or paid API call occurred. The controller dry replay ran with network denied. Earlier runtime and dependency preparation used recorded public package downloads; those setup operations are separate from the measured attempt phase. The generated outcomes are synthetic controller fixtures. They are not evidence that either arm is correct, faster, cheaper, or equivalent.

## Frozen dry simulation and proposed development pilot

The full-corpus dry simulation contains all 36 frozen corpus tasks, two arms, and five predeclared repetitions: **360 synthetic attempts**. It exists to exercise the controller, ledger, and scorer. It is not the proposed paid pilot.

The proposed paid pilot is limited to the 24 development tasks from Flask, Express, Chi, and ripgrep. With two arms and five predeclared repetitions, its exact run ceiling is **240 dispatched attempts**. The 12 command-line-api and Rake holdout tasks remain excluded until the pilot fixes a final repetition count and receives a separate budget approval. Each task permits at most 300 process seconds and reports a completion-only 6,000-token ceiling. The pilot's nominal token-ceiling sum is 1,440,000 tokens, but it is not a hard aggregate maximum. An individual completion can report an overshoot, which the controller retains before stopping subsequent dispatch.

The proposed paid model, provider transport, token prices, and money ceiling are unavailable and unapproved. The frozen dry artifact records model ID `UNAVAILABLE-REQUIRES-PAID-CAMPAIGN-APPROVAL`, denied network, null pricing, null money ceiling, and null provider transport. Therefore it cannot authorize a paid run. The exact paid dollar maximum is **unknown**, not zero. Actual model/provider spend was **$0.00** because the dry replay invoked neither. Its synthetic cost fields are controller fixtures, not billing records.

- Proposed development-pilot config: [`agent-outcomes/development-pilot.config.json`](agent-outcomes/development-pilot.config.json), SHA-256 `0d809b03594a0c7b55a23fcded804f19f48051124ce686935e168382e182e472`.
- Proposed development-pilot frozen envelope: [`agent-outcomes/development-pilot.frozen.json`](agent-outcomes/development-pilot.frozen.json), file SHA-256 `33d609c59f1868895dd42752e33a6045637109e955e0c1e31c082691d15b6312`.
- Development-pilot campaign SHA-256: `acd75d8a5c2c0eb87b89a58dc08680c9ce0a1add2e3dae303b170e19ab0d976d`.
- Development-pilot execution-envelope SHA-256: `f2be1444ac6b35433743c128d7e9c82b121e38bc9be62f7b6f10a037f54dc973`.
- Development-pilot budget status: [`agent-outcomes/development-pilot-budget-status.json`](agent-outcomes/development-pilot-budget-status.json), SHA-256 `74f357dbd484e7d99b94c8b00767d4e5864cacbc7284b3b08492d8f02a625f12`.
- Full-corpus dry config: [`agent-outcomes/full-corpus-dry.config.json`](agent-outcomes/full-corpus-dry.config.json), SHA-256 `199f4cefdbf5e0747f769bc4a1d7bf36157dff748b44fc25b0cf6358f409602b`.
- Full-corpus dry frozen envelope: [`agent-outcomes/full-corpus-dry.frozen.json`](agent-outcomes/full-corpus-dry.frozen.json), file SHA-256 `cbcd846c6fe7b31faace7a721c49adf6f4f5d22cd90dd1208432078d6b5699ed`.
- Full-corpus dry campaign SHA-256: `2a261171708ce0612866829af1fd9d1ffde8777f6cf94391f8c8b9ed6116fe86`.
- Full-corpus dry execution-envelope SHA-256: `edae53d4e662ee9fdb28a012c26ab169673dd2744d33129299f9a13825e036d9`.

## Exact dry replay

These commands completed successfully from the feature worktree:

```bash
python3 scripts/bench-agent-outcomes.py validate --tasks scripts/benchmarks/agent-outcomes/tasks.jsonl
python3 scripts/bench-agent-outcomes.py freeze --config docs/findings/agent-outcomes/full-corpus-dry.config.json --output docs/findings/agent-outcomes/full-corpus-dry.frozen.json
python3 scripts/bench-agent-outcomes.py run --campaign docs/findings/agent-outcomes/full-corpus-dry.frozen.json --dry-run --output docs/findings/agent-outcomes/run-dry
python3 scripts/bench-agent-outcomes.py score --run docs/findings/agent-outcomes/run-dry --output docs/findings/agent-outcomes/report-dry.json
```

The dry ledger contains all 360 scheduled attempts: 72 synthetic correct, 72 incorrect, 72 infrastructure void, 72 product error, and 72 timeout outcomes. This deliberate mixture tests accounting and preservation; none is an efficacy observation. Both arms have the same synthetic correctness rate, so the clustered result says `inconclusive`, not equivalent. Synthetic token and wall values are also fixtures and must not be interpreted.

| Artifact | SHA-256 |
|---|---|
| `run-dry/run-metadata.json` | `ad0e02c4142020f321605aee86ed8e6460e0fe5a2547573e09511fec33eea278` |
| `run-dry/attempts.jsonl` | `18a8dcd7d601b134e5485d1185c33e71e9249f8f0bb7c42004f1be4e9ec55290` |
| `run-dry/attempt-ledger.json` | `e0027ebaaad17f85dcf3a90da7879f8a4b64c795d085a1ab3a054a5066008d4b` |
| `report-dry.json` | `d0a9822bdb12064af50b6540a2a5033776da7f1b4294ff0de5c9891c6abff93e` |

The public score says `dry_run: true`, `synthetic: true`, `run_status: completed`, synthetic total cost `0.0`, and 72 typed `synthetic_dry_run` void reasons. Separately, actual model/provider spend was $0 because no model/provider ran. The S1 join projection refuses this report. Public projection tests reject source roots, private verifier paths, prompts, credentials, hidden labels, arbitrary nested fields, and synthetic S1 joins.

## Corpus and verifier evidence

The run binds the accepted six-repository, six-language, 36-task corpus. Canonical source copies are retained outside Git at `/home/murphy/source/miller-evidence/agent-outcomes-task6/snapshots`; every copied inventory hash was revalidated during freeze. The six repair tasks use their explicit seeded snapshots. They are benchmark fixtures, not claims of upstream defects. The command-line-api qualification remains limited to the focused SplitCommandLine suite because the upstream full suite has the documented host signal-code mismatch.

| Input | SHA-256 / result |
|---|---|
| `repositories.json` | `395892a66165b88a7ef367a50367e5f886e3ef98478b88500f7fdc671c48dde7` |
| `tasks.jsonl` | `489f55a1f2956f37179a417a4f02e84ec88bd65228d2c775dcab8a47677ac104` |
| `verifiers/verifiers.json` | `29cb6ccca44e8a964b51c59ab07ec59041cfec5d491cb8bed11a0f8017b4e25f` |
| `verifiers/evidence.json` | `3fe3e8e1de433ca932a22251403d230543e1993162939ad20b9de74cc449fc8a` |
| `verifiers/execution-contracts.json` | `1ff04680c1aafdc920b16fb28ebbf166d2d15fb2e18ff96b19ed6c11a82a83ec` |
| `verifiers/prepared-environments.json` | `526bd840be7b506e85582d9413abb78c5cead0046a5c3ac6d24c2d404037ff41` |
| `verifiers/external-evidence.json` | `48265d8d012899f1229b81c3993eb00524ef9fb9c81f3ebefc0326fb7365af07` |
| Neutral grading replay | 36/36 positive results correct; 36/36 plausible negatives incorrect |
| Mutation replay | 12/12 baseline failures, reference passes, and plausible-wrong failures recorded |

The full raw replay evidence remains outside agent mounts under `/home/murphy/source/miller-evidence/agent-outcomes-task4-round1`. Hidden labels, reference/wrong patches, and native-runner inventories are not copied into task snapshots or the dependency image.

## Fake command and event contract

For the first read-only task, native and lexical prompts are byte-identical, prompt SHA-256 `fb1688c73588dbace8ab98d0e5932a3b92f1c7b4f8b3f1af5912171b2315d48e`. The native agent argv SHA-256 is `aa5a73fef807d15804a185b1da5f1af1e344709f7b0f980d78df54e1fe511bec`; the lexical argv SHA-256 is `148398cb17f1f2d9b6c56b23e5e2fcbe932b0812fd3d7f90675146bf13d4bf68`. The only treatment additions configure Miller and set both `MILLER_SEMANTIC=off` and `MILLER_CT=off`. Native masks Miller.

The first paired synthetic raw-event identities are `2d033a6b19962e7276604ed95b9d774089a492db73cab40d9d4598c030ab24df` and `da5f8678996c83f85dcb7190d8021236c20361f10b72258c2b67fc26af1c2d26`. The durable journal writes a dispatch intent before each completion, binds returned campaign/task/arm/repetition/order identities, and preserves unresolved intents as cost-unknown voids.

Replayable safe fixtures are [`agent-outcomes/fake-run-commands.json`](agent-outcomes/fake-run-commands.json), file SHA-256 `bf9d590a04171c819dd4dd6baf838c62fd615977ec05f1339522103e35db2977`, and [`agent-outcomes/fake-run-events.jsonl`](agent-outcomes/fake-run-events.jsonl), SHA-256 `992ba351ea08d419ad10e35505d95b93626a884929cec2b83f8a5a709430a4db`. The accepted parser reads one native command, no Miller call, model usage `(100 input, 40 cached subset, 10 output)`, four reasoning tokens, no unsupported event, and no failed lifecycle. These files contain no auth, private paths, verifier labels, or hidden answer values.

## Runtime and isolation boundary

The frozen prepared binding is retained outside Git at `/home/murphy/source/miller-evidence/agent-outcomes-task6/runtime/prepared8/`:

- Image: `localhost/miller-agent-outcomes@sha256:f3e39a672f0a345d555d42e22483a72156cd2b3d303b30f31527ae0f4a8d07b5`.
- Prepared image binding SHA-256: `e9523ca4ff8e781c09c570b20b53feb4441c05191bed5f9038add411dba444f1`.
- Canonical prepared content manifest SHA-256: `6c2833c8a9728dd1c21c3c60748eea60536584d3e98eb4dfb033381981bb05bd`.
- Setup record SHA-256: `caad9ab79a3d1968cd3ad9269e4009416d7c93e565a732760563e0c52ab7aa16`.

Earlier four-probe isolation evidence belongs to older images `58c438…` and `2b5ef6…`; it remains historical and does not qualify `f3e39a…`. The final image passed 24/24 fresh repository/arm/mount-shape isolation probes. The durable [isolation projection](agent-outcomes/prepared-isolation-summary.json), SHA-256 `f0add70d6b5eee0928f6a82f2b3ed84cb920fca7754fb3891ce7a8260278f985`, retains the raw summary path and SHA-256 `cb37e1e82ea697e1c4ba2c102f06ed7dfa7c0814d3c9e33e5bbe0a1581bbbb47`. Its offline second-container verifier replay passed all 36 expected states (12 baseline failures, 12 reference passes, 12 plausible-wrong failures). The durable [offline replay projection](agent-outcomes/offline-verifier-summary.json), SHA-256 `32dfdff612a9034f549628d74735f9f11a920a91d0976a064d85c3eaebe383a5`, retains the raw summary path and SHA-256 `954c60323abe8e7cc9f83e72f70ac8ecbc54c9eb01be2747ab02ede9db1f93dd`. The binding contains dependency artifacts only: no repository source, hidden verifier, reference patch, wrong patch, expected answer, provider credential, or host config is available inside it.

The exact-image physical gates now have these results:

1. Same-CID CT passed on the Chi qualification fixture after a host-only qualification mapped the neutral native case to the opaque provider CT ID. The durable [mapping evidence](agent-outcomes/ct-case-mapping-qualification.json), SHA-256 `25880720162ba137b09b6191b2ba25d930322108a2646948a5476674bd2ecd95`, also freezes the expected green baseline and empty baseline failure set. The final [paired smoke](agent-outcomes/ct-paired-smoke.json), SHA-256 `20dec2eaf6b1f39e521148dc0016ac85181802f54bd035519c43a491d1e03c00`, shows both arms measured changed hash `40cca63d…`; native performed zero CT commands; lexical warmed 127 root-project cases, matched the green baseline, advanced revision 293 to 299, observed the exact frozen affected case red, and cleaned up; both containers were removed. This proves the lifecycle on Chi only. Public task answers and correctness grading remain native-ID-only; opaque CT IDs are private qualification data.
   The earlier mapping and mechanics proof remain as [initial mapping](agent-outcomes/ct-case-mapping-qualification-initial.json), SHA-256 `8e73942b25929e38600e2ab3afa29f130d71c9af3e829f30bc5971357355fea2`, and [initial paired smoke](agent-outcomes/ct-paired-smoke-initial.json), SHA-256 `e974915967b01316c52a9391fa6dd9ce53f48131cab21497aff2b277b4ea47e2`. They were superseded because they did not freeze or compare the baseline CT verdict and failure set.
2. The final Python branch gate ran 436 tests: 433 passed, 3 skipped, 0 failed in 27.236 seconds. Evidence is `/tmp/miller-final-verification.eC3qbs/unittest-discovery-green-retry.log`, SHA-256 `fc510d3a68500b9a5cb987b37f95a5a205533b969ca7898f47e38cd7a4030ac4`. One skip needs unavailable local tree-sitter-c-sharp and tree-sitter-razor checkouts; two cover Windows-only path behavior. Before/after source inventories were identical (`6bbeb8ba…`). Ruff, formatting, compilation, links, and diff checks passed; the five CLI artifacts remained byte-identical.

## Semantic qualification

Available S1 records qualify CPU stdio source runtimes, not the portable runtime bytes in the current prepared image. BGE qualification file SHA-256 is `9857359b5ba3c5db65c1efd2d273491108a972df3e33edfb76fcb6bbed57e8b3`; Qwen is `e478fd0fe994392c11f929ee70ab3b1e1bb22740474231d375c33fc0165c1afa`. Neither can authorize this image by version string. The image lacks a matching model/runtime observation, so secondary live admission refuses and **no S1 join exists**.

## Approval and exact resume boundary

Before any paid execution, an approver must choose an exact supported model and provider transport, record current input/cached-input/output prices, choose a positive money ceiling, and bind the approval to newly frozen campaign and execution-envelope digests plus one absolute run root. Completion-only usage makes the dollar ceiling soft for an in-flight attempt; the harness records overshoot and refuses the next dispatch. Therefore no truthful hard paid maximum exists without the missing pricing and approval inputs.

After those values are reviewed and written to `development-pilot.approved.config.json`, the safe resume sequence is:

```bash
python3 scripts/bench-agent-outcomes.py freeze --config docs/findings/agent-outcomes/development-pilot.approved.config.json --output docs/findings/agent-outcomes/development-pilot.approved.frozen.json
python3 scripts/bench-agent-outcomes.py run --campaign docs/findings/agent-outcomes/development-pilot.approved.frozen.json --approval /ABSOLUTE/PATH/TO/EXPLICITLY-APPROVED-RECORD.json --output /ABSOLUTE/RUN/ROOT/FROM/THAT/APPROVAL
python3 scripts/bench-agent-outcomes.py score --run /ABSOLUTE/RUN/ROOT/FROM/THAT/APPROVAL --output docs/findings/agent-outcomes/development-pilot.measured.json
```

The current `development-pilot.frozen.json` is dry-only and must not be reused for a paid run. The 36-task dry envelope is also not a paid-pilot schedule. No campaign result or product recommendation exists yet.
