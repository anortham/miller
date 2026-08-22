# ADR-0006: Generated ignore-policy ownership

## Status

Accepted for the generated-ignore hygiene slice.

## Decision

Miller keeps user-authored ignore policy in the workspace tree and gives every indexing consumer one
effective-policy descriptor:

- `user_root` is the existing `<workspace>/.julieignore`. It is authoritative, byte-identical, and is never
  passed as an external `--ignore-file`.
- `inherited_root_copy` is the existing exclusive in-tree snapshot used by a linked worktree when its main
  checkout has a user `.julieignore`. Its content, including malformed patterns, stays in-tree so julie-extract
  retains warning-only compatibility. It is never passed as an external file.
- `generated_global` is Miller's deterministic baseline/vendor policy. It is stored at
  `MillerHome.ResolveMillerDirectory()/ignore-policies/<canonical-workspace-id>.julieignore`, with a SHA-256
  content hash and a flag indicating whether this preparation wrote new bytes.

The generated policy is materialized with a same-directory temporary file and an atomic replace/move. Existing
bytes are compared before replacement, and process-local materializers serialize on the exact target path. With a
stable root, concurrent writers render identical bytes and produce the same deterministic result. If source
contents mutate concurrently, the winning publication is one complete point-in-time snapshot; atomic replacement
never exposes partial policy bytes. A root policy existence check is repeated after detection/rendering and before
generated materialization; a user-created root file wins the race. Miller never overwrites, deletes, or migrates a
root `.julieignore`.

## Consumer parity

Full scans prepare/materialize the descriptor. Direct updates use the bounded update resolver and reuse an existing
user, inherited-copy, or generated-global descriptor; if the linked worktree has only a main-checkout user policy,
the resolver may establish the exclusive in-tree inherited snapshot, but it never runs vendor detection or writes
generated-global policy. If generated policy is absent on an ordinary root, the update carries only the invariant
controls. Only `generated_global` is added to the external ignore-file list, before Miller's invariant file; user
and inherited policies remain in-tree. The watcher loads the generated file
with the workspace root as its pattern base. It attaches the generated-policy watcher only when the root has no
user policy and no linked-main inherited policy, creating the Miller-owned policy directory early enough to catch
the first atomic publication. Creating or changing a root policy therefore forces re-evaluation and disables
generated authority.

Rebind/bootstrap remains on the shared scan chokepoint, so it receives the same policy preparation as every other
whole-repository scan.

## Lifecycle boundary

The generated path is Miller-owned state under Miller home. Workspace removal is responsible for deleting only
the validated workspace-id path in a later lifecycle slice. Legacy root-generated files are not identified or
deleted by headers; content may have been edited and ownership cannot be proven safely.

## Consequences

Fresh ordinary workspaces no longer acquire a top-level generated `.julieignore`, while generated baseline/vendor
exclusions remain active for scan, update, and watcher paths. Linked-worktree malformed-policy behavior remains
compatible until julie-extract offers equivalent warning-only semantics for external files.
