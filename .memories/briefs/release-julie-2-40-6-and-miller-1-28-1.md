---
id: release-julie-2-40-6-and-miller-1-28-1
title: Release Julie 2.40.6 and Miller 1.28.1
status: completed
created: 2026-09-06T22:11:29.427Z
updated: 2026-09-06T23:41:01.649Z
tags: []
---

## Goal
Publish Julie 2.40.6 for the redundant SQLite setup-write fix, then publish Miller 1.28.1 pinned to its verified public archives.

## Authorization
The user approved both patch releases and pushes, including verification and release closeout.

## Constraints
Reader protection and FULL durability stay intact. The measured 10% median improvement is not a complete tail-latency fix. Preserve immutable release tags and unrelated worktree changes.

## Result
Both stable releases are published. Julie tag4316d1ad and Miller tag39a6944e remain immutable. Miller validation34066733674 and promotion34067210925 passed. All public archives, checksums, bundled producer binaries, and release notes verified. Linux and Windows release gates passed. Publication records are documentation-only follow-ups.

## References
- docs/findings/2026-09-06-v1.28.1-release-verification.md
- docs/release-notes/v1.28.1.md
- /home/murphy/source/julie-extractors/docs/release-evidence/2026-09-06-v2-40-6-release.md
