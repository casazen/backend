# Pipeline: Stripe Connect Onboarding (US-002 / US-007 enabler)

## Status
- status: completed
- current_stage: 06-operations
- started: 2026-06-10T00:00:00Z
- last_updated: 2026-06-10T10:45:00Z

## Input
- description: Stripe Connect Express onboarding for operators/landlords — create connected account, Stripe-hosted KYC, track charges_enabled before checkout/publish. Enables spec-direct-checkout, spec-ltr-recurring-rent, spec-branded-booking-site publish gate.
- type: feat
- priority: critical
- spec_file: Sessions/specs/spec-connect-onboarding.md

## Artifacts
- issue: #224 — https://github.com/casazen/backend/issues/224
- branch: feature/224-stripe-connect-onboarding
- design_spec: Sessions/design-224.md
- pr_backend: #223
- pr_backend_url: https://github.com/casazen/backend/pull/223
- pr_frontend: #117
- pr_frontend_url: https://github.com/casazen/frontend/pull/117
- release_report: Sessions/release-224.md
- tag: v1.1.10
- release_url: https://github.com/casazen/backend/releases/tag/v1.1.10
- ops_report: Sessions/ops-report-2026-06-10-v1.1.10-connect-onboarding.md

## Stage History

| Stage | Status | Iterations | Gates | Artifact |
|---|---|---|---|---|
| 01-planning | completed | 1 | G1-G5 pass | #224 |
| 02-design | completed | 1 | G1-G8 pass | Sessions/design-224.md |
| 03-development | completed | 1 | G1-G4 BE pass (connect tests); G5-G8 FE pass; PRs open | BE #223, FE #117 |
| 04-review | completed | 1 | G1-G10 pass, 0 critical | Sessions/review-224.md |
| 05-release | completed | 1 | Phase A-D pass | v1.1.10, Sessions/release-224.md |
| 06-operations | completed | 1 | G1-G2,G7,G9 pass | Sessions/ops-report-2026-06-10-v1.1.10-connect-onboarding.md |
