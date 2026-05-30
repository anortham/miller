# Miller

A fast, token-thrifty, local code-intelligence MCP server for AI coding assistants, built in .NET 10.

Miller indexes a codebase and answers structural questions about it (find, inspect, trace references, assess
change impact) over the Model Context Protocol, so an agent spends tokens on reasoning instead of grepping and
re-reading files. Its differentiator is a **deterministic cross-language structural resolver** that links code
across language boundaries (e.g. a C# entity to its EF table, a TypeScript call to the C# route that serves it)
without embeddings.

> **Status: early.** This repo is at milestone **M0** (solution scaffold + CI). The read core (M1), the MCP tool
> surface (M2), freshness (M3), and the cross-language resolver (M4) are being built per
> [docs/miller-mvp-plan.md](docs/miller-mvp-plan.md). It is not yet usable day-to-day; first dogfood lands at M2.

## How it works

Miller does **not** parse source code itself and does **not** use embeddings. Extraction is delegated to a
prebuilt `julie-server extract` binary (Rust + tree-sitter) that writes symbols, identifiers, files, and
relationships into a SQLite database. Miller is the pure-.NET host on top:

```
┌───────────────────────────┐     stdio / MCP
│  Claude Code / MCP client  │◀──────────────────┐
└───────────────────────────┘                    │
                                       ┌──────────────────────┐
                                       │   Miller.Server       │  MCP host + 7 tools + telemetry ledger
                                       └──────────────────────┘
                                                  │
                          ┌───────────────────────┼───────────────────────┐
                          ▼                                                 ▼
              ┌──────────────────────┐                        ┌──────────────────────┐
              │     Miller.Core       │                        │   Miller.Indexing     │
              │  (pure logic, no I/O) │                        │  (infrastructure)     │
              │  • in-memory index    │                        │  • julie-server       │
              │    + BM25 ranking     │◀──── populated from ───│    extract subprocess │
              │  • cross-lang resolver│                        │  • SQLite (WAL) read  │
              └──────────────────────┘                        │  • watcher / indexer  │
                                                               └──────────────────────┘
                                                                          │
                                                              ┌──────────────────────┐
                                                              │  SQLite extract DB    │
                                                              │  (from julie-server)  │
                                                              └──────────────────────┘
```

Design choices that follow from this:
- **No embeddings in the default path** — search is BM25 over an in-memory inverted index rebuilt from SQLite at
  startup. Indexes are small and rebuild in seconds.
- **No custom daemon** — SQLite WAL is the read-concurrency primitive, so many reader instances (agent teams, git
  worktrees, the dashboard) share one index. The writer/indexer is a separate, optional process whose death
  degrades freshness, not reads.
- **Hard logic↔infrastructure seam** — `Miller.Core` has zero I/O dependencies, so the ranking and the resolver
  are unit-tested in milliseconds with no live DB, subprocess, or transport. This keeps the default test suite fast.

## Project structure

```
src/
  Miller.Core/       pure logic, ZERO I/O deps: contract record types, in-memory index + BM25, the resolver
  Miller.Indexing/   infrastructure: julie-server extract subprocess, SQLite (WAL) read layer, watcher/indexer
  Miller.Server/     MCP stdio host, the 7 tools, the telemetry interceptor + ledger
tests/
  Miller.Tests/      unit (Core, fast) + contract (against a committed extract-DB fixture) + tagged scale set
docs/
  miller-mvp-plan.md           milestones M0–M7
  findings/                    the investigation this design was mined from
```

## The tool surface (target)

Seven tools, each with smart defaults so the common path is the simplest call: `search`, `inspect`, `context`,
`trace`, `impact`, `edit`, `workspace`. Targets are smart strings, not JSON objects. See
[docs/findings/miller-toolbox.md](docs/findings/miller-toolbox.md).

## Build & test

```bash
dotnet build Miller.slnx -c Release
dotnet test  Miller.slnx -c Release           # fast suite only — Scale tests excluded by default
```

The test suite is split in two so the dev loop stays fast (the lesson from julie, whose suite grew to
30+ minutes once slow integration tests ran on every change):

- **fast** (`Category!=Scale`) — pure logic + contract tests, no `julie-server` subprocess. Target <10s.
  This is the default: a bare `dotnet test` runs only this suite (the test project sets
  `VSTestTestCaseFilter=Category!=Scale`, the MSBuild default for `--filter`; a command-line `--filter`
  overrides it).
- **scale** (`Category=Scale`) — live tests that spawn the real pinned `julie-server` or build large
  fixtures. Run before a commit/PR. They **skip** (not fail) if `.tools/julie-server` is absent.

The friendly wrapper sets a wall-clock budget tripwire on the fast suite and handles the filters:

```bash
scripts/test.sh            # fast suite (default), with a <30s budget tripwire
scripts/test.sh scale      # scale suite only (needs .tools/julie-server — see restore script)
scripts/test.sh all        # both suites
```

Two guards keep the split honest: a convention test
([`ScaleTraitConventionTests`](tests/Miller.Tests/Conventions/ScaleTraitConventionTests.cs)) fails the
build if any julie-spawning test is missing `[Trait("Category","Scale")]`, and CI time-budgets the fast
suite. To enable the scale suite locally:

```bash
bash scripts/restore-julie-server.sh   # downloads the pinned julie-server into .tools/
```

Requires the .NET 10 SDK. Warnings are errors (`Directory.Build.props`).

## License

MIT
