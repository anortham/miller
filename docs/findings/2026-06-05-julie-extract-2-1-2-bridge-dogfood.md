# julie-extract 2.1.2 Bridge Dogfood

Date: 2026-06-05

## Scope

Validated Miller against `julie-extract` 2.1.2 after the TypeScript URL literal persistence fix for
awaited generic client calls such as `axios.get<T>(...)` and `axios.put<T>(...)`.

## Release Verification

GitHub release:

```text
tag: v2.1.2
commit: d62f91d647805e8dd9d1b850339e7eb7917e28eb
published: 2026-06-05T15:39:40Z
```

The release notes state that CLI commands, exit codes, SQLite schema version, JSONL schema version,
JSON report schema version, and artifact integer `extract_contract_version` remain unchanged.

The published asset digests used in `scripts/julie-pins.json` are:

| target | sha256 |
| --- | --- |
| `aarch64-apple-darwin` | `520193990e5b4da1dc5bb2715a5e306ef252e83a3e0afbb91c36e0e08ae9fb35` |
| `x86_64-apple-darwin` | `193691791afca855008fd84a33666e7c06cd4e91bed1b4423c67cee4eb900ddd` |
| `x86_64-unknown-linux-gnu` | `9f244d706ccc1ddbf0f5037af5317c85c30e9deabb3ea6fa80ab4c093cd59c52` |
| `x86_64-pc-windows-msvc` | `6330caf185db09ddde71cb9f91cbdb4a739919d7e43f6e9cdb234dfc862278f9` |

## Restore Evidence

```text
Restoring julie-extract v2.1.2 for aarch64-apple-darwin
sha256 OK
Installed: /Users/murphy/source/miller/.tools/julie-extract
julie-extract 2.1.2
```

The Release build output also bundled the restored extractor:

```text
src/Miller.Server/bin/Release/net10.0/.tools/julie-extract --version
julie-extract 2.1.2
```

## MyraNext Dogfood

Rebuilt the registered MyraNext workspace with the Release CLI:

```text
# workspace full
workspace_id: 29cad844b05f10a274066e522356888036d6c4e590d9c01a8c0e8dcd771d1e63
root: /Users/murphy/source/MyraNext
status: refreshed
scanned: yes
swapped: no
revision: 2
```

The fresh artifact records the expected extractor version and unchanged contract versions:

```text
binary_version|2.1.2
extract_contract_version|2
sqlite_schema_version|2
```

The previously missing typed TypeScript route is now persisted as a URL literal:

```text
/api/messages/active|url|1447811afe4819b0b18602c62002ca34
```

`getActiveMessages` now bridge-traces to the ASP.NET endpoint:

```text
# trace bridge getActiveMessages (1 link(s))
getActiveMessages  --route-->  GetActive  0.90 (High)
    signals: RouteVerbMatch=present
```

## Fixture Proof

`LiveBridgeTraceTests` now uses this exact shape in the disciplined TypeScript fixture:

```ts
const res = await axios.get<AppSettingDto>(`/api/appsettings/${id}`);
```

Focused scale verification:

```text
LiveBridgeTraceTests: 2 passed
```

## Result

Miller can now consume the extractor-side TypeScript generic client-call fix without schema or contract
changes. The dotnet-web bridge has both real-workspace dogfood evidence and live extractor fixture
coverage for the route leg that had been blocked on `julie-extract` 2.1.2.
