# Workspace Safety Design — Path-Class Policy and the Registration Gate

**Status:** decision paper. Nothing is implemented. The user chooses; the options below are written so a
choice can be executed without another round of design.

**Covers two TODO backlog items:**

- Workspace blacklist / `.julieignore` sufficiency (station incident 2026-08-10).
- Explicit workspace registration gate.

They are one mechanism with two questions: **which roots are suspect** (item a) and **what happens when a
suspect root shows up** (item b). This doc keeps them separate so each can be answered on its own, and names
where they join.

---

## 1. The incident

The Hermes CLI ran against `~/.hermes/hermes-agent`. Miller bound that directory as a workspace, registered it
with the display id `hermes-agent-<hash8>`, and built a full index: ~7.8k files, ~543k symbols, ~4.3GB under
`.miller/`. After that, the query path sat at ~3.5s for `search` and ~6.5s for `inspect`. One cold
`ensure_fresh` open took ~254s.

Nothing failed. Every guard did what it was written to do. The tree was simply never a project, and no code
path was ever asked whether it should be one.

---

## 2. What the code does today

### 2.1 What `WorkspaceRootSafety` already refuses

[`src/Miller.Server/Tools/WorkspaceRootSafety.cs`](../../src/Miller.Server/Tools/WorkspaceRootSafety.cs)
refuses a candidate root that is:

- a filesystem or drive root (`/`, `C:\`) — detected as "no parent directory" (`IsSensitiveRoot`, line 71);
- the user's home directory itself;
- macOS: `/Users`, `/var/root`, `/private/var/root`;
- Linux: `/home`, `/root`;
- Windows: `<drive>\Users`, `\Windows`, `\Windows\System32`, `\Program Files`, `\Program Files (x86)`,
  `\ProgramData`, plus the values of `SystemRoot`, `ProgramFiles`, `ProgramFiles(x86)`, `ProgramW6432`,
  `ProgramData`, `PUBLIC`.

Two properties decide the incident:

1. **Only EXACT roots are refused, never their children.** The class doc states this deliberately: "a project
   under the home dir (e.g. `~/src/app`) is fine; `~` itself is not." `~/.hermes/hermes-agent` is a child of
   home, so it passes.
2. **The set is a fixed list of names, not a shape.** There is no rule of the form "a hidden directory under
   home", so no third-party tool's private directory is covered.

This is julie's heritage, ported one-for-one from `workspace/root_safety.rs` so Miller, julie, and eros reject
the same set. Julie had the same two properties, so julie would have indexed `~/.hermes/hermes-agent` too. The
heritage is not an argument for keeping the set as it is — it is the reason the gap exists in all three.

### 2.2 The one path-class rule that already exists

[`WorkspaceBindingResolver.IsPluginInstallRoot`](../../src/Miller.Server/Hosting/WorkspaceBindingResolver.cs)
(line 100) refuses `~/.claude/plugins`, `~/.codex/plugins`, `~/.cursor/plugins`, `~/.miller/plugin-cache`, and
any `*_PLUGIN_ROOT` value — **and their children**, via `PathContains`. So Miller already has the exact concept
this design needs: a path class that is not sensitive, is not a filesystem root, and still must never be a
workspace.

Three limits make it unable to catch the incident:

- It names four directories, all Miller/agent plugin caches. It knows nothing about other tools' private trees.
- It runs **only on the cwd arm** of binding. `IsUsableFallbackRoot` is called from the cwd branch of
  `TryResolveStartup` (line 34) and `TryResolve` (line 61). Env and MCP roots skip it entirely.
- It is compiled in. A user cannot add `~/.hermes` to it.

### 2.3 How a directory becomes a registered, indexed workspace

There are exactly three entry points that write a registry row (`WorkspaceRegistry.UpsertSeen`,
[`src/Miller.Indexing/WorkspaceRegistry.cs:189`](../../src/Miller.Indexing/WorkspaceRegistry.cs)):

**(1) Server binding — the implicit path, and the one the incident used.**

- `Program.cs:45` resolves a startup root through `WorkspaceBindingResolver.TryResolveStartup`
  (env `MILLER_WORKSPACE_ROOT` > cwd).
- `Program.cs:51-55` applies the sensitive-root guard **only when the source is cwd**. The design note
  [`docs/plans/2026-06-25-mcp-roots-workspace-binding-design.md`](2026-06-25-mcp-roots-workspace-binding-design.md)
  states the rule outright: "Sensitive-root refusal applies **only** when cwd is the last resort. Env and roots
  sources are trusted explicit client/operator intent."
- At request time, `WorkspaceBindingResolver.TryResolve` (line 45) applies env > MCP roots > cwd. A `file://`
  root from the client is accepted with no safety check at all.
- `IndexBootstrapService.RegisterBootstrapWorkspace` (line 2157) then writes the row, and the initial scan runs
  in the background.

No confirmation, no allowlist, no record that anybody chose this. A gateway that spawns Miller with its own
working directory — which is what the Hermes CLI does — registers that directory.

**(2) `workspace open` — the explicit path.**

- CLI: `CliDispatch.WorkspaceOpen` (line 3511) canonicalizes first, calls
  `WorkspaceRootSafety.IsSensitiveRoot`, refuses with exit 2, then registers `Refreshing` and drives a refresh
  with `bypassBackoff: true`.
- MCP: `WorkspaceTool.Open` (line 1206) does the same guard, returns
  `ToolDiagnostic.Refusal("workspace_open_refused", …)` on a sensitive path, then calls
  `_registry.RegisterRefreshing` and queues a background prime.

Both check the same exact-match set, so both would have accepted `~/.hermes/hermes-agent`.

**(3) A read tool with a `workspace_id` selector — this does NOT register anything today.**

This corrects the TODO wording. `WorkspaceIndexProvider.Resolve` (line 157) routes a selector to
`ResolveRegistered` → `ResolveRegisteredState` (line 1017) → `WorkspaceRegistrySelector.Resolve`
([`WorkspaceRegistrySelector.cs:51`](../../src/Miller.Server/Workspaces/WorkspaceRegistrySelector.cs)), which
throws `KeyNotFoundException` for anything it cannot match to an existing row. The CLI read path
(`CliDispatch.TryResolveReadContext`, line 3993) prints that message and returns false. **An unregistered path
selector fails; it never creates a workspace.**

What a selector *does* do is trigger the build. `ResolveRegisteredState` line 1025 turns a serve-then-refresh
read into a **blocking foreground** refresh when the row has no readable index yet. That is the ~254s cold
`ensure_fresh` open in the incident report: the row already existed from the bind, and the first read paid for
the whole index.

So the sequence was: gateway working directory → bind → registry row → background scan → first read blocks on
it. The selector was a symptom. **The gate belongs at the bind and at `workspace open`, not at the selector.**

### 2.4 What `.julieignore` can and cannot do

`.julieignore` is a per-root exclusion file
([`JulieIgnoreSeeder.WorkspaceIgnoreFileName`](../../src/Miller.Indexing/JulieIgnoreSeeder.cs), line 67). Miller
seeds generated baseline and vendor rules — `.miller/`, `*.log`
([`VendorScan.BaselinePatterns`](../../src/Miller.Indexing/VendorScan.cs)), plus detected vendor directories —
and since the 2026-08-22 hygiene plan those generated rules live at
`$MILLER_HOME/.miller/ignore-policies/<canonical-workspace-id>.julieignore` rather than in the user's checkout.

Note what that path already proves: **Miller-owned global policy state keyed by workspace already exists.** A
deny list would sit beside it, not invent a new place to live.

But every one of those rules is evaluated **after a root is chosen**. Nothing in the ignore layer can decline a
root. Applied to the incident, a perfect `.julieignore` would have produced a smaller wrong index in a place no
index belonged. The TODO's own note is correct and is the finding: `.julieignore` is not sufficient, and no
amount of tuning makes it sufficient, because it operates one layer too late.

### 2.5 Where a refusal or a pending state would surface

The shapes already exist; nothing new has to be invented for the output side.

- **MCP refusal:** `ToolDiagnostic.Refusal(code, message, [ToolDiagnosticAction(call, why)])` — the exact shape
  `workspace_open_refused` already uses (`WorkspaceTool.cs:1244`). An action entry can name the one call that
  overrides, so an agent reads the refusal and the remedy together.
- **Registry state:** `WorkspaceRegistryState` is `Current | Ready | LoadedExisting | Stale | Refreshing |
  Missing | Error` ([`WorkspaceRegistryRow.cs:5`](../../src/Miller.Indexing/WorkspaceRegistryRow.cs)). A pending
  state would be a new value. Consumers filter on `Current | Ready | LoadedExisting` (see the
  `ReconcileOpenedRegistryRow` comment, `CliDispatch.cs:3584`), so a new value is invisible to them by default —
  which is the correct default for a workspace nobody approved, but it must be documented in
  [`docs/contracts/cli-eros-v1.md`](../contracts/cli-eros-v1.md) before Eros sees it.
- **Health:** `HealthWarning(Code, Severity, Message)`
  ([`WorkspaceHealthFacts.cs:17`](../../src/Miller.Server/Tools/WorkspaceHealthFacts.cs)) already carries coded
  warnings into `workspace health --json`. An existing oversized or unapproved workspace reports there.
- **Dashboard:** `DashboardEndpoints` already has antiforgery-protected POSTs for remove, prune, refresh, and
  open-folder (lines 61-272), all backed by shared cores per ADR-0002. An approve button is one more POST over a
  shared core — no new pattern.

---

## 3. Item (a) — path-class policy

**Question:** is `.julieignore` enough to keep install and home-config trees out of the index, or is
path-class policy required?

**Finding from §2.4:** `.julieignore` cannot answer this question at all. The real choice is what shape the
path-class policy takes.

### Option A1 — Ignore-only: leave root selection alone, improve exclusions and docs

Keep the exact-match forbidden set. Improve `.julieignore` seeding, document the risk, and let a bad root
produce a smaller index.

- **For:** zero risk, zero migration, no new state.
- **Against:** does not address the incident. `~/.hermes/hermes-agent` still becomes a registered workspace and
  still costs a multi-GB index and seconds-slow queries. It answers "how big is the mistake" instead of "was it
  a mistake".

### Option A2 — Deny classes: a path-class deny list at every entry point (recommended)

Add a deny rule that matches by **shape**, not by name, and apply it at every point that can register a root.

- Rule set (defaults): any **hidden directory under the home directory** and everything beneath it
  (`~/.hermes/**`, `~/.cache/**`, `~/.local/**`, `~/.config/**`, `~/.claude/**`, `~/.npm/**`, `~/.cargo/**`,
  `~/.nuget/**`, …); the existing plugin-cache roots; system temp (`/tmp`, `/var/tmp`, `%TEMP%`). The hidden-dir
  rule is what catches the incident, and it catches the next tool's private tree without an update.
- User-editable: a `deny` and `allow` list in a file under `~/.miller` (beside the existing
  `ignore-policies/` directory). `allow` wins over `deny`, so `~/.config/nvim` or a dotfiles repo stays
  indexable by writing one line.
- Applied at **all** of: startup bind, request-time bind (including the MCP roots arm and the env arm), CLI
  `workspace open`, MCP `workspace operation=open`, and cross-workspace refresh of an unknown root — not just
  the cwd arm as today.
- Override without editing a file: `workspace open --force` (CLI) / `force=true` (MCP), which records an allow
  entry so it is not needed twice. An agent can do this in one call; no human click.
- Off switch: `MILLER_ROOT_POLICY=off` is a permanent zero-behavior-change state, matching the repo's existing
  posture for `MILLER_SEMANTIC=off` and `MILLER_INDEX_STORE=off`.

- **For:** stops the bad root at the moment of registration, which is the only moment that can stop it. Ordinary
  repos under `~/source`, `~/dev`, `/work`, `C:\repos` are untouched — the rule keys on "hidden" and on named
  cache trees, not on "under home". Extends a mechanism that already exists (`IsPluginInstallRoot`) rather than
  inventing one. Needs no human in the loop.
- **Against:** a deny list is never complete — a tool that stores a project-looking tree in a non-hidden
  directory under home still passes. New state to store, migrate, and document. A wrong deny entry blocks a
  legitimate workspace, so the refusal message must always name the override.

### Option A3 — Allow classes: deny by default, allow only approved locations

Invert it: nothing is indexable unless it sits under an allowed prefix (`~/source/**`, `~/dev/**`, plus what
the user adds).

- **For:** complete. No unknown tree is ever indexed.
- **Against:** breaks every existing user whose repositories live somewhere else, on the first run after
  upgrade, with no warning that this is what happened. It is really the registration gate (item b) wearing a
  path-policy costume — the approval decision moves to "which folders", which is the harder version of the same
  question. If the user wants this, it should be reached through B2's approval flow, not through a prefix list
  they have to author up front.

### Recommendation for item (a): **Option A2**

A deny list of path classes, checked at every entry point that can register a root, user-editable under
`~/.miller`, with a one-call override and a full off switch.

It is the only option that addresses the incident, and unlike A3 it does not require the user to describe their
whole filesystem before Miller works.

---

## 4. Item (b) — the explicit registration gate

**Question:** should Miller require an explicit act before it builds a new index, so "current workspace" is a
choice rather than a side effect?

**Scope correction from §2.3:** the side effect is the **bind**, not the first `search` with a selector. A
selector against an unregistered path already fails. The gate must sit on the bind path
(`IndexBootstrapService.RegisterBootstrapWorkspace`) and on `workspace open`.

### Option B1 — Warn only

Register and index as today, but a **new** root emits a warning in `workspace status`/`health` and one line in
the log naming what was registered and why.

- **For:** no behavior change, no regression risk, useful the day someone investigates a surprise index.
- **Against:** the incident produced a 4.3GB index before anyone read a warning. It documents the mistake
  rather than preventing it.

### Option B2 — Pending state for flagged roots only (recommended)

A new root that **trips a deny class from item (a)** is registered in a new `pending` state and **is not
indexed**. Anything else binds and indexes exactly as today.

- Reads against a pending workspace return a refusal that names the single promoting call:
  `workspace open --register <path>` (CLI) / `workspace(operation="open", path="…", register=true)` (MCP), as a
  `ToolDiagnosticAction`.
- The promoting call can be made by an **agent** as well as a person. The gate demands an explicit act, not a
  human click.
- The dashboard shows pending workspaces with an approve button (one antiforgery POST over the shared core) and
  a remove button, for the person who prefers to decide there.
- Approval is stored user-globally under `~/.miller`, keyed by canonical root, so one approval covers every
  Miller process, every agent in a swarm, and every future session.

- **For:** joins (a) and (b) into one mechanism with one decision point. Zero cost and zero prompts on the
  normal path — open a project in Cursor, first tool call binds, nothing changes. The expensive work (the scan)
  never starts for a tree nobody chose. Recovery from a wrong deny entry is one call.
- **Against:** a new registry state to thread through list/status/health rendering and the Eros contract. A tool
  whose private tree is denied now needs one approval before it works — a real, if small, first-run friction for
  the person who genuinely wants that tree indexed.

### Option B3 — Confirm every new workspace

Every root Miller has never seen requires explicit approval before any index is built, deny classes or not.

- **For:** the strongest version of "conscious choice". Nothing is ever indexed by accident.
- **Against:** this is the regression the task asks to weigh, and it is a real one. Every fresh clone, every new
  `git worktree add`, and every agent in a swarm hitting a new worktree stops on an approval. The plugin promise
  — install it, open a project, ask a question — becomes install, open, ask, get refused, approve, ask again. If
  approval is human-only it breaks unattended agent runs outright; if an agent may self-approve, the gate stops
  almost nothing, because the agent will approve whatever it was pointed at, which is exactly what happened at
  `~/.hermes/hermes-agent`.

  B3 is only worth taking if the user wants Miller to index **nothing** they did not name. It should then be
  offered as a setting value (`MILLER_ROOT_POLICY=confirm-new`), not as the default.

### Recommendation for item (b): **Option B2**

Gate only the roots that item (a) flags. Make the promoting act one call that an agent can make, and store the
approval user-globally so a swarm approves once.

This keeps the plugin's zero-click first run intact — which is the property most worth protecting — while making
the specific class of mistake that caused the incident impossible to make silently.

---

## 5. Migration and compatibility

**Existing registries are grandfathered.** Any row already in `~/.miller/workspaces.db` keeps working with no
approval. Retro-judging existing rows would demand approval for workspaces people use every day, which is a
worse regression than the problem. Only registrations made **after** the change are judged.

**Existing bad rows are reported, never auto-removed.** A registered workspace whose root now trips a deny class
gets a `HealthWarning` in `workspace health --json` and a line in `workspace list`, naming
`workspace remove --id <id>` as the remedy. Removal already reclaims the shared-store sidecars through
`StoreSidecarReclaim`, so the ~4.3GB case is recoverable through the existing path — the user runs it, Miller
does not.

**Registry state addition.** A `pending` value must be added to `WorkspaceRegistryState` and its storage string.
Consumers filtering `Current | Ready | LoadedExisting` skip it correctly by default. `workspace list`,
`workspace status --json`, and `workspace health --json` need to render it, and
[`docs/contracts/cli-eros-v1.md`](../contracts/cli-eros-v1.md) must document the new value before Eros can see
it.

**Agent swarms.** Approval and allow entries live user-globally under `~/.miller`, keyed by canonical root. N
worktree agents therefore approve once, not N times. A **linked worktree of an approved main checkout inherits
the approval through the git link**, using the same no-subprocess `GitWorktreeLayout` resolution the CT opt-in
inheritance already uses — otherwise every `git worktree add` in an approved repository would demand a fresh
approval, which is the swarm regression in a different costume.

**Plugin auto-open UX.** Under B2 the normal path is unchanged: project folder → MCP roots → bind → index, zero
clicks, zero extra calls. Only a deny-class root pauses, and it pauses with a refusal that names one call an
agent can make itself. Under B3 this property is lost; that is the main reason B3 is not recommended.

**`MILLER_WORKSPACE_ROOT`.** Today an env override is trusted as explicit operator intent and skips even the
sensitive-root guard. Whether it should also skip the new policy is a genuine decision, not an implementation
detail — see question 5. The suggested default: the env override satisfies the **gate** (setting it is an
explicit act) but the **deny classes** still apply, with the refusal naming the override. That way a stale
exported variable in a gateway's environment cannot quietly re-create the incident.

**Effort.** For an AI coding agent: roughly 2-3 sessions for A2 + B2 — one for the policy core plus its
entry-point wiring, one for the pending state through registry/CLI/MCP/dashboard rendering, and one for the
contract doc, the health warning, and the tests. Nothing here needs a human beyond the decisions below.

---

## 6. Deliberately not proposed

- **A size or file-count budget** ("refuse a tree over N files without confirmation"). The Hermes tree was only
  ~7.8k files. Size was not what made it wrong; nobody choosing it was. A budget would refuse large legitimate
  monorepos and still accept small illegitimate trees. It can be added later as a warning if a real case asks
  for it.
- **A new MCP tool.** Every surface above is an existing tool: `workspace open` gains a flag, refusals ride the
  existing `ToolDiagnostic` shape, and the dashboard gains one POST. The MCP tool count does not move.
- **Changing what `.julieignore` does.** The ignore layer is correct at its layer. This design adds a layer
  above it and leaves it alone.

---

## 7. Decision questions

Plain English. Each one changes what gets built.

1. **Should Miller refuse to index hidden folders inside your home directory by default?**
   That means folders whose name starts with a dot, like `~/.hermes`, `~/.cache`, `~/.claude`, `~/.config`, plus
   the system temp folders. Ordinary project folders such as `~/source/miller` are not affected.
   *Recommended: yes.*

2. **When Miller refuses a folder, what should it take to use it anyway?**
   - one command, such as `workspace open --force <path>`, which an agent can also run; or
   - editing an allow list file in `~/.miller`; or
   - both.
   *Recommended: both, with the one command writing the allow entry for you.*

3. **Should the approval step apply only to refused folders, or to every folder Miller has not seen before?**
   Only refused folders keeps the plugin's first run click-free. Every new folder stops every fresh clone and
   every new worktree until someone approves it.
   *Recommended: only refused folders.*

4. **If a folder needs approval, may an agent approve it on its own with one extra call, or must a person do
   it?**
   A person-only rule breaks unattended agent runs. An agent-allowed rule means the approval proves someone
   asked for this folder on purpose, not that a human reviewed it.
   *Recommended: an agent may approve, and the approval is written down and logged.*

5. **Should setting `MILLER_WORKSPACE_ROOT` count as approval?**
   Today it skips every safety check. It could keep skipping everything, or it could satisfy the approval step
   while the refused-folder list still applies.
   *Recommended: it satisfies the approval step, but a refused folder is still refused.*

6. **Should the folders already in your registry keep working with no approval?**
   Saying no would demand approval for workspaces you use every day.
   *Recommended: yes — existing workspaces keep working, and a bad one is reported in `workspace health` with
   the command to remove it.*

7. **Do you want a full off switch?**
   `MILLER_ROOT_POLICY=off` would restore exactly today's behavior for anyone who wants it.
   *Recommended: yes, matching how `MILLER_SEMANTIC=off` works.*
