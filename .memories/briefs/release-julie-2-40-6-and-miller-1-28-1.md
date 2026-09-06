---
id: release-julie-2-40-6-and-miller-1-28-1
title: Release Julie 2.40.6 and Miller 1.28.1
status: active
created: 2026-09-06T22:11:29.427Z
updated: 2026-09-06T22:36:44.768Z
tags: []
---

## Goal
Publish julie-extract2.40.6 for the redundant SQLite setup-write fix, then publish Miller1.28.1 pinned to its verified public archives.

## Authorization
User explicitly approved both patch releases and pushes. Continue through verification, publication, notes, asset checks, and source-control closeout without re-asking.

## Constraints
Preserve reader protection and FULL durability. Do not present the10% median improvement as a complete tail-latency fix. Qualify reader capability against the published producer. Required local Windows gate and validate-then-promote Miller packaging. Preserve immutable tags and unrelated worktree changes.

## Success criteria
Both stable releases published with matching notes and verified archives; Miller pins the published hashes; Linux/Windows and new-store adoption gates pass; primary checkouts clean and pushed with evidence.

## References
- docs/release-process.md
- docs/findings/2026-09-06-v1.28.1-release-verification.md
- /home/murphy/source/julie-extractors/docs/release-evidence/2026-09-06-v2-40-6-release.md

## Status
Julie2.40.6 published and public archives verified, sourcecloseoutpushed. Miller published pin restored; local qualification in progress before Windows and package validation/promotion.
