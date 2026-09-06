# Third-Party Notices

Miller is licensed under the MIT License (see [LICENSE](LICENSE)). It depends on, and in some cases
bundles, third-party components. Some of these ship inside Miller's release archives (the self-contained
per-platform packages built by the release workflow), some are restored from NuGet at build time, and a
few JavaScript assets are vendored directly into the repository. The notices below cover those components.
Each upstream project retains its own copyright and license; this file is provided for attribution and does
not modify those licenses.

If you redistribute Miller (source or a release archive), keep this file alongside the binaries.

## .NET / NuGet dependencies

| Component | Version | License | Notes |
| --- | --- | --- | --- |
| ModelContextProtocol (MCP C# SDK) | 1.4.0 | MIT | Model Context Protocol server/stdio implementation Miller uses to speak MCP. |
| Microsoft.Extensions.Hosting | 10.0.9 | MIT | .NET Generic Host that runs Miller's server and hosted services. |
| Microsoft.Data.Sqlite | 10.0.9 | MIT | ADO.NET provider Miller uses to read `julie-extract` artifacts and its own SQLite sidecars. |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.3 | Apache-2.0 / SQLite public domain | Native SQLite provider bundle used by `Microsoft.Data.Sqlite`. |
| Serilog.Extensions.Hosting | 10.0.0 | Apache-2.0 | Serilog integration with the .NET Generic Host. |
| Serilog.Sinks.Console | 6.1.1 | Apache-2.0 | Serilog console sink. |
| Serilog.Sinks.File | 7.0.0 | Apache-2.0 | Serilog file sink (the shared daily `.log` file). |
| Serilog.Formatting.Compact | 3.0.0 | Apache-2.0 | Compact JSON formatter for the machine-readable `.jsonl` log. |
| Blake3 (`Blake3`) | 2.2.1 | BSD-2-Clause | .NET binding by Alexandre Mutel (xoofx) used for `blake3:` content hashing. See note below on the bundled native library. |

### SQLite native provider (SQLitePCLRaw)

`Microsoft.Data.Sqlite` uses the `SQLitePCLRaw.*` family, including the `bundle_e_sqlite3` package that
ships the native `e_sqlite3` provider. Miller pins the bundle explicitly so NuGet audit resolves the current
non-vulnerable native SQLite package. SQLitePCLRaw (maintained by Eric Sink) is licensed under the
**Apache License 2.0**. The native bundle embeds the **SQLite** engine itself, which is released into the
**public domain** by its authors. These native binaries ship inside Miller's release archives.

### Blake3 native library

The `Blake3` NuGet package is licensed **BSD-2-Clause** (Copyright (c) 2020, Alexandre Mutel). It ships a
native `blake3_dotnet` library built from the BLAKE3 reference implementation
(<https://github.com/BLAKE3-team/BLAKE3>), which is dual-licensed: released into the **public domain via
CC0 1.0**, or alternatively under the **Apache License 2.0**. The native `libblake3_dotnet` binary ships
inside Miller's release archives.

## Vendored web assets

| Component | Version | License | Notes |
| --- | --- | --- | --- |
| htmx | 2.0.4 | Zero-Clause BSD (0BSD) | Vendored at `src/Miller.Dashboard/wwwroot/lib/htmx/htmx.min.js`; powers the loopback dashboard UI. |

## Bundled tooling

### julie-extract

Miller delegates all source extraction to the pinned **`julie-extract`** binary
    (<https://github.com/anortham/julie-extractors>), currently pinned at version **2.40.5**
(see [`scripts/julie-pins.json`](scripts/julie-pins.json)). It is the same author's own project (Alan
Northam) and is shipped inside Miller's release archives under `.tools/julie-extract`. Refer to the
julie-extractors repository for its license terms and for the licenses of the tree-sitter grammars it
embeds.

### julie-semantic-sidecar

Miller's default-on, off-switchable local semantic retrieval (ADR-0003) generates embeddings through the pinned
**`julie-semantic-sidecar`** binary (<https://github.com/anortham/julie-semantic-sidecar>), currently
pinned at version **0.1.0** (see [`scripts/semantic-pins.json`](scripts/semantic-pins.json)). It is
shipped inside Miller's release archives under `.tools/julie-semantic-sidecar-runtime`. That runtime
directory contains `package-manifest.json` schema 2, the platform executable
(`julie-semantic-sidecar` or `julie-semantic-sidecar.exe`), `LICENSE`, `README.md`, and
`THIRD_PARTY-LICENSES.html`. The manifest declares the four payload files exactly, including one
`THIRD_PARTY-LICENSES.html` entry with role `third_party_licenses`. The sidecar is the same author's own
project (Alan Northam) and is licensed **MIT** (Copyright (c) 2026 Alan Northam).

The sidecar binary is statically linked, so redistributing it also redistributes its native embedding
engine. It embeds via the `llama-cpp-2` / `llama-cpp-sys-2` Rust crates (pinned at `=0.1.151`, both
licensed **MIT OR Apache-2.0**), which vendor and compile **llama.cpp** and its **ggml** tensor library
(<https://github.com/ggml-org/llama.cpp>) directly into the binary. llama.cpp and ggml are licensed
**MIT** (Copyright (c) 2023-2026 The ggml authors). Miller does not build these itself; refer to the
julie-semantic-sidecar repository for its full dependency manifest.

### sqlite-vec

The semantic vector store `<workspace>/.miller/vectors.db` is served by the **sqlite-vec** loadable SQLite
extension (<https://github.com/asg017/sqlite-vec>), currently pinned at version **0.1.9** (see
[`scripts/semantic-pins.json`](scripts/semantic-pins.json)). The prebuilt loadable library ships inside
Miller's release archives as `.tools/vec0.dylib`, `.tools/vec0.so`, or `.tools/vec0.dll` depending on
platform. sqlite-vec is dual-licensed **Apache-2.0 OR MIT** at the user's option (Copyright (c) 2024 Alex
Garcia); the upstream repository carries both `LICENSE-APACHE` and `LICENSE-MIT` at the pinned `v0.1.9`
tag.
