# `miller rules` — instruction-tier routing block (rules-v1)

**Status:** current
**Verb:** `miller rules [--harness cursor|windsurf|cline|kiro|copilot|agents]`
**Verified:** 2026-07-16

## What it is

`miller rules` prints Miller's routing block — the same guidance the Claude Code / Codex plugins deliver through
a `SessionStart` hook — so harnesses **without** plugin or hook support can carry it in the instruction file they
already load on every request. This is the *instruction tier* of guidance delivery.

The block is embedded into the `miller` binary from `hooks/miller-routing-block.md` at build time
(`<EmbeddedResource>` in `Miller.Server.csproj`, the same mechanism as `MILLER_AGENT_INSTRUCTIONS.md`). A release
archive ships no repo checkout, so the verb reads the compiled assembly and never the filesystem.

## Output contract

| Stream | Content |
| --- | --- |
| **stdout** | The file content, and nothing else. |
| **stderr** | One `write to: <path> — <note>` line, when `--harness` is given. |
| **exit 0** | Rendered. |
| **exit 2** | Unknown harness, `--harness` with no value, or a positional argument. |

The target path is a **stderr** note rather than a stdout header specifically so that redirection produces a
usable file:

```bash
miller rules --harness cursor > .cursor/rules/miller.mdc
miller rules --harness agents >> AGENTS.md
```

`miller rules` with no `--harness` prints the bare block for pasting anywhere.

**Print-only.** Miller never writes into a user's project; the user redirects or pastes.

`rules` is a `version`/`help`-class verb: it dispatches above every index-loading verb and never hydrates a
workspace.

## Supported harnesses

Each format below was verified against that harness's **official** documentation on 2026-07-16. Formats that
could not be verified from official docs are dropped rather than guessed — see *Dropped* below.

| `--harness` | Target path | Framing | Verified from |
| --- | --- | --- | --- |
| `cursor` | `.cursor/rules/miller.mdc` | `---`<br>`alwaysApply: true`<br>`---` | https://cursor.com/docs/rules |
| `windsurf` | `.windsurf/rules/miller.md` | `---`<br>`trigger: always_on`<br>`---` | https://docs.devin.ai/desktop/cascade/memories |
| `cline` | `.clinerules/miller.md` | none (plain markdown) | https://docs.cline.bot/customization/cline-rules |
| `kiro` | `.kiro/steering/miller.md` | `---`<br>`inclusion: always`<br>`---` | https://kiro.dev/docs/steering/ |
| `copilot` | `.github/copilot-instructions.md` | none (plain markdown) | https://docs.github.com/en/copilot/how-tos/configure-custom-instructions/add-repository-instructions |
| `agents` | `AGENTS.md` | none (plain markdown) | https://agents.md/ |

### Per-harness verification notes

**cursor** — *"Project rules live in `.cursor/rules` as `.mdc` files"*; *"Project rules must use the `.mdc`
extension. A plain `.md` file in `.cursor/rules` is ignored… because it has no frontmatter."* On always-apply:
*"If alwaysApply is true, the rule will be applied to every chat session."* The docs' own always-applied example
carries **only** `alwaysApply: true` — with it true, *"Globs and description are ignored"* — so Miller emits only
that key. Note: the older `docs.cursor.com/context/rules` URL is dead (301s to the docs index).

**windsurf** — **The product is now documented as "Devin Desktop" (Cognition);** `docs.windsurf.com` 308-redirects
to `docs.devin.ai`. Activation modes are declared *"in its frontmatter via the `trigger` field"*, and the
documented mode table maps Always On → `always_on`: *"Full rule content is included in the system prompt on every
message."* Path status: `.devin/rules/` is preferred on current builds, `.windsurf/rules/` is the documented
backward-compatible fallback, and legacy `.windsurfrules` is *"still read"*. **Miller targets
`.windsurf/rules/miller.md`** because current builds read it *and* older Windsurf builds read only it — the
widest-compatibility choice for a print-only recommendation.
**Verification gap (recorded deliberately):** the `trigger` field and the `always_on` value are each documented
verbatim in the activation-mode table, but the docs publish **no complete `always_on` example file** (the only
frontmatter example is a `glob` rule). Miller's rendering composes two documented parts rather than copying a
published example. Documented size limits: workspace rule files **12,000 characters**, global **6,000** — the
block is ~2.4KB, well inside both. Fully-verified alternative for this harness: a root-level `AGENTS.md` is
documented as always-on with no frontmatter, so `--harness agents` also reaches it.

**cline** — `.clinerules/` (directory) is the *"Primary rule format"*; Cline reads all `.md`/`.txt` files in it.
On always-active: *"**No frontmatter**: Rules without frontmatter are always active."* So Miller emits plain
markdown. Note: the official docs conflict with themselves — `/getting-started/config` documents `.cline/rules/`
while `/customization/cline-rules` documents `.clinerules/` and never mentions the former; neither states
precedence. Miller targets `.clinerules/` because it is what the Rules page, the `/newrule` command, and the VS
Code extension all use today.

**kiro** — *"Workspace steering files reside in your workspace root folder under `.kiro/steering/`."* Inclusion is
configured by *"front matter… at the very beginning of the file, enclosed by triple dashes"*, and
`inclusion: always` is documented verbatim as a code block. A file with **no** frontmatter is always-included by
default (the docs head that mode *"Always included (default)"*); Miller emits the explicit key so the intent
survives an edit. Caveat: the Kiro **CLI** docs never mention inclusion modes — frontmatter support there rests
on the Web docs' *"Steering files work the same way across Kiro IDE, Kiro CLI, and Kiro Web"*. Also: *"When using
custom agents, steering files are not automatically included."*

**copilot** — `.github/copilot-instructions.md`, plain markdown; *"Instructions are automatically added to
requests that you submit to Copilot."* Copilot also supports path-specific `.github/instructions/NAME.instructions.md`
with an `applyTo` glob frontmatter key — not used here, since the routing block is repo-wide. Copilot reads a
root `AGENTS.md` too, so `--harness agents` is an alternative.

**agents** — *"AGENTS.md is just standard Markdown. Use any headings you like; the agent simply parses the text
you provide."* No frontmatter or schema. Lives at the repository root; nested files are supported, where *"the
closest one takes precedence"*. Stewarded by the Agentic AI Foundation under the Linux Foundation; agents.md is
the authoritative reference — there is no separate formal spec document.

### Dropped

**None.** All six candidates in the plan (cursor, windsurf, cline, kiro, copilot-instructions, generic AGENTS.md)
verified against official docs. If a future harness's format cannot be verified, drop it and record it here
rather than guessing.

## Stability

`rules-v1` pins the **output contract** (stdout = file content, stderr = target path, exit codes), not the block's
prose — the block text tracks `hooks/miller-routing-block.md`, whose wording is gated by
`tests/plugin/hooks-routing-block.test.cjs` against `MILLER_AGENT_INSTRUCTIONS.md`.

The harness list is pinned at implementation time. Harness vendors change formats (Cursor's rules URL moved;
Windsurf became Devin Desktop; Cline's own docs disagree), so **re-verify against official docs before adding a
harness or changing a target path**, and update the table's `Verified from` URL in the same change.
