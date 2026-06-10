# Pipeline: Direct Checkout (Stripe Connect, Operator = MoR) (US-002)

## Status
- status: completed
- current_stage: 06-operations
- started: 2026-06-10T12:00:00Z
- last_updated: 2026-06-10T10:50:00Z

## Input
- description: Public direct booking + Stripe Connect checkout for anonymous guests
- type: feat
- priority: critical
- spec_file: Sessions/specs/spec-direct-checkout.md

## Artifacts
- issue: #226 — https://github.com/casazen/backend/issues/226
- branch: feature/226-direct-checkout
- design_spec: Sessions/design-226.md
- pr_backend: #227
- pr_backend_url: https://github.com/casazen/backend/pull/227
- pr_frontend: #119
- pr_frontend_url: https://github.com/casazen/frontend/pull/119
- release_report: Sessions/release-226.md
- tag: v1.1.11
- release_url: https://github.com/casazen/backend/releases/tag/v1.1.11
- ops_report: Sessions/ops-report-2026-06-10-v1.1.11-direct-checkout.md

## Stage History

| Stage | Status | Iterations | Gates | Artifact |
|---|---|---|---|---|
| 01-planning | completed | 1 | G1-G5 pass | #226 |
| 02-design | completed | 1 | G1-G8 pass | Sessions/design-226.md |
| 03-development | completed | 1 | G1-G13 pass | BE #227, FE #119 |
| 04-review | completed | 1 | G1-G10 pass, 0 critical | Sessions/review-226.md |
| 05-release | completed | 1 | Phase A-D pass | v1.1.11, Sessions/release-226.md |
| 06-operations | completed | 1 | G1-G2,G7,G9 pass | Sessions/ops-report-2026-06-10-v1.1.11-direct-checkout.md |
