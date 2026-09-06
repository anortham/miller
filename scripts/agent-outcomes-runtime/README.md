# Agent outcomes runtime image

This recipe builds the local, immutable Podman image used to qualify native-agent runs. Image construction may use the public Fedora package repositories. Agent and verifier execution use the frozen campaign network policy separately.

The image contains Codex CLI 0.153.4, a specific Miller Release directory, and the Python, Node.js, Go, Rust, .NET, and Ruby toolchains required by the six-repository corpus. It contains no credentials, user configuration, repository snapshots, verifier labels, or hidden tests.

```sh
python3 -B scripts/agent-outcomes-runtime/prepare_runtime.py \
  --codex-binary /absolute/path/to/native/codex \
  --miller-directory /absolute/path/to/Miller.Server/bin/Release/net10.0 \
  --evidence-directory /absolute/private/evidence/runtime
```

The evidence directory must be new and outside the repository. The deterministic runtime manifest records the complete Miller file manifest, Codex hash, RPM inventory, pinned base image, and resulting local image digest. A separate setup record contains the measured build duration and nullable download fields.

Prepare dependency-only artifacts for all six frozen corpus repositories and derive a second immutable image:

```sh
python3 -B scripts/agent-outcomes-runtime/prepare_dependencies.py \
  --repositories scripts/benchmarks/agent-outcomes/repositories.json \
  --base-image localhost/miller-agent-outcomes@sha256:<runtime-digest> \
  --evidence-directory /absolute/private/evidence/prepared
```

The resulting external binding records both the embedded content-manifest hash and the derived image digest. It does not copy repository source, patches, prompts, or verifier labels into the image.

Run the no-network OS probes for every repository, arm, and workspace mode with the binding:

```sh
python3 -B scripts/agent-outcomes-runtime/qualify_runtime.py \
  --runtime-manifest /absolute/private/evidence/runtime/runtime-manifest-<sha256>.json \
  --prepared-binding /absolute/private/evidence/prepared/prepared-binding-<sha256>.json \
  --evidence-root /absolute/private/evidence/qualification
```

Replay all frozen mutation verifiers without a provider or network:

```sh
python3 -B scripts/agent-outcomes-runtime/replay_prepared.py \
  --prepared-binding /absolute/private/evidence/prepared/prepared-binding-<sha256>.json \
  --image-reference localhost/miller-agent-outcomes@sha256:<prepared-image-digest> \
  --evidence-root /absolute/private/evidence/replay
```

These checks never configure a provider or invoke a model. Passing them does not authorize a paid campaign. Per-run caches and build outputs remain under `/runtime`.
