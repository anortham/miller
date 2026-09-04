---
id: ship-miller-v1-27-2-guidance-alignment-patch
title: Ship Miller v1.27.2 guidance-alignment patch
status: active
created: 2026-09-04T12:47:23.045Z
updated: 2026-09-04T12:47:23.045Z
tags:
  - release
  - v1.27.2
---

## Goal
Publish Miller v1.27.2, a docs/guidance patch on top of v1.27.1.

## Contents
- Source-final commit `ddb3b5ba`: every agent guidance channel (routing block, server core, `tests` tool description, skills, docs) teaches the same `workspace_id` and CT rules.
- Release prep: version 1.27.2 in Directory.Build.props and all five plugin manifests, `docs/release-notes/v1.27.2.md`, docs map pointer.

## Gates (docs/release-process.md)
1. Windows guest fast suite on the release commit.
2. `gh workflow run release.yml` with publish=false, wait green.
3. Promote by run id with publish=true.
4. `gh release edit v1.27.2 --notes-file docs/release-notes/v1.27.2.md`.
5. Record a verification finding, fill the notes Verification section, update docs/README.md.

## Constraints
- Push and publish need explicit user approval on the reported clean state.
- Never overwrite an existing release.
