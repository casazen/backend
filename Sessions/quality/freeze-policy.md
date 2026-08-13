# Release freeze policy

## Trigger

Any row in `ac-matrix-mvp.md` with `priority: P0` and `status: fail`.

## Effect

- Stage 05 **must not** promote unrelated feature work `develop` → `main`.
- Allowed: hotfixes / quality remediation PRs that flip those rows to `pass` or explicit `stub` (with `status:stub` issue label and PLANNING out-of-scope update).
- New product pipelines may open PRs to `develop` but release Phase C is blocked until P0 fails are cleared.

## Clear freeze

1. L3 staging (or local) green for failing ACs.
2. Update matrix rows to `pass`.
3. Document in `Sessions/release-<N>.md` under **Freeze clearance**.
