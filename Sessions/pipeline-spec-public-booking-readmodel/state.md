# Pipeline: Public Booking Read-Model (US-001)

## Status
- status: completed
- current_stage: 03-development
- started: 2026-06-09T15:15:00+02:00
- last_updated: 2026-06-09T17:35:00+02:00

## Input
- description: Introduce public read-model DTOs for anonymous property search and single-property public endpoint; data-minimized whitelist, no ownerId leak (GDPR Art. 5(1)(c)). Per Sessions/specs/spec-public-booking-readmodel.md (US-001).
- type: feat
- priority: high
- source_spec: Sessions/specs/spec-public-booking-readmodel.md
- pipeline_set: Phase 1 (2 of 7)

## Artifacts
- issue: 212
- branch: feature/212-public-booking-readmodel
- design_spec: Sessions/design-212.md
- pr_backend: 213
- pr_backend_url: https://github.com/casazen/backend/pull/213
- pr_frontend: 112
- pr_frontend_url: https://github.com/casazen/frontend/pull/112
- release_report: (pending)
- tag: (pending)
- release_url: (pending)
- ops_report: (pending)

## Stage History

| Stage | Status | Iterations | Gates | Artifact |
|---|---|---|---|---|
| 01-planning | completed | 1 | G1-G5 pass | #212 |
| 02-design | completed | 1 | G1-G8 pass | Sessions/design-212.md |
| 03-development | completed | 1 | G1-G13 partial (G7 lint pre-existing fail on frontend) | PR #213 / PR #112 |
| 04-review | (pending) | - | - | - |
| 05-release | (pending) | - | - | - |
| 06-operations | (pending) | - | - | - |

## Gate Summary (Iteration 1/3)

### Backend (casazen/backend)
| Gate | Command | Status | Notes |
|---|---|---|---|
| G1 | dotnet test | pass | 463 passed, 0 failed |
| G2 | dotnet format --verify-no-changes | pass | |
| G3 | dotnet build /warnaserror | pass | |
| G4 | dotnet ef migrations script | N/A | No schema change |
| G10 | CinCode filter | N/A | Property entity unchanged |
| G11 | secrets check | pass | No secrets staged |
| G12 | GDPR Guest fields | N/A | Guest untouched |
| G13 | tourist tax hardcode | N/A | |

### Frontend (casazen/frontend)
| Gate | Command | Status | Notes |
|---|---|---|---|
| G5 | npm test | pass | 112 passed |
| G6 | tsc -b --noEmit | pass | |
| G7 | npm run lint | fail | 47 pre-existing errors on develop (not introduced by #212) |
| G8 | npm run build | pass | |
| G9 | npm run test:e2e | pass | public-booking-readmodel.spec.ts 3/3 |
| G11 | secrets check | pass | |

## Blockers
- G7 frontend lint: pre-existing repo-wide eslint debt (47 errors). New/changed files lint clean. Recommend Stage 04 waiver or separate lint cleanup issue.
