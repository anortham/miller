# QML adversarial review

Date: 2026-08-25

## Review scope

The ordinary review workflow sent the full QML branch range `6f0cf9c6..1734f50d` to
Grok `grok-4.6` through xAI. The review used a medium severity floor and covered
correctness, security, concurrency, cross-platform behavior, and contracts/tests. It
allowed one external invocation and at most two rounds.

**No external-model policy was declared — diff sent to xai.**

## Accepted findings and fixes

- High: CTest nonzero exits discarded usable JUnit failures — fixed at `c4754dd1`.
- High: a supplied `ProjectPath` bypassed QML changed-path scoping — fixed at `1a9d956c`.
- High: `import "."` could duplicate one target and report false ambiguity — fixed at
  `12b4db20`.
- High: `tst_*` C/C++ Quick Test harness changes were missed by QML selection — fixed at
  `1a9d956c`.
- High: a missing structural qmltypes model dropped the qmldir manifest — fixed at
  `12b4db20`.
- Medium: a qmldir export alias whose `TypeName` differed from the file symbol failed to
  resolve — fixed at `12b4db20`.

## Closure

Round 2 was lead-only confirmation after the fixes; it made no second external call and
found no new finding at or above medium severity. The terminal review result is
`clean/cross-model-reviewed` after fixes. The exact-tree verification and the honest
missing-Qt-toolchain record are in
[`qml-continuous-testing-verification.md`](2026-08-24-qml-continuous-testing-verification.md).
