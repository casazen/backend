# CasaZen Quality System

Production Definition of Done uses three test levels and a living AC matrix.

## Levels

| Level | Meaning | Typical command |
|---|---|---|
| L1 | Unit / integration | `dotnet test`, `npm test` |
| L2 | UI contract (demo mocks OK) | `npm run test:e2e` |
| L3 | Real API (local InMemory or staging) | `.\scripts\quality\run-l3-local.ps1` / `E2E_STAGING=1` |

## Artifacts

| File | Purpose |
|---|---|
| [ac-matrix-mvp.md](./ac-matrix-mvp.md) | Phase-1 AC inventory: `pass` / `fail` / `stub` / `missing-test` |
| [freeze-policy.md](./freeze-policy.md) | When P0 fails block unrelated promotes |
| [requirements.json](./requirements.json) | Machine-checkable ADR/spec requirements |
| [gap-backlog.md](./gap-backlog.md) | Ordered open P0 gaps for the reliability loop |
| `nightly-<date>.md` | Nightly L2+L3 reports |

## Reliability loop

Canonical process: `.claude/process/sdlc-reliability-loop/PROCESS.md`  
Loop state: `Sessions/loop/` · entry skill: `sdlc-loop-tick`

## Executable gates

From backend repo root:

```powershell
.\scripts\quality\extract-requirements.ps1
.\scripts\quality\check-spec-coverage.ps1
.\scripts\quality\check-ac-matrix.ps1 -DesignPath Sessions/design-NNN.md
.\scripts\quality\check-no-shipped-stubs.ps1
.\scripts\quality\run-l3-local.ps1
```

Nightly CI hard-fails on matrix `` `fail` `` rows and on open P0 coverage gaps.
