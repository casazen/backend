# Pipeline: feat(MVP F1): custom domain + subdomain booking (US-024)

## Status
- status: running
- current_stage: 03-development
- started: 2026-07-14T13:10:00Z
- last_updated: 2026-07-15T12:30:00Z

## Input
- description: Custom domain + subdomain booking (US-024) — Wave 5 of MVP F1 epic #291. Hosts publish on {slug}.casazen.it, /book/{slug}, or verified custom CNAME; edge resolves Host → Org.
- type: feat
- priority: high
- issue_ref: "#298"
- epic: "#291"

## Artifacts
- issue: "#298"
- issue_url: https://github.com/casazen/backend/issues/298
- branch: feature/298-custom-domain-booking
- design_spec: Sessions/design-298.md
- pr_backend: (pending)
- pr_backend_url: (pending)
- pr_frontend: (pending)
- pr_frontend_url: (pending)
- release_report: (pending)
- tag: (pending)
- release_url: (pending)
- ops_report: (pending)

## Related
- epic_pipeline: Sessions/pipeline-mvp-f1-epic/state.md
- spec: Sessions/specs/spec-custom-domain-booking.md
- adr: docs/adr/ADR-001-custom-domain-booking.md
- predecessor_wave: #295 compliance (shipped on main; issue close pending)
- depends_on: #297 public-site DS (closed), #271 onboarding PLG (open — domain step may land lightly)

## Stage History

| Stage | Status | Iterations | Gates | Artifact |
|---|---|---|---|---|
| 01-planning | completed | 1 | G1–G5 ✅ (existing #298) | #298 |
| 02-design | completed | 1 | G1–G8 ✅ | Sessions/design-298.md |
| 03-development | in_progress | 1 | BE G1-G4 ✅ FE G6-G8 ✅ E2E pending CI | branch ready for PR |
| 04-review | (pending) | - | - | - |
| 05-release | (pending) | - | - | - |
| 06-operations | (pending) | - | - | - |
