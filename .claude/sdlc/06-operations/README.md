# Stage 06 — Operations

**Pattern**: hub-and-spoke
**When to run**: post-deploy monitoring + monthly compliance audit

## Purpose

Ensure the running system stays compliant with Italian regulations, detect incidents early, and produce audit evidence. This stage runs continuously after each release and on a monthly schedule.

## Council Composition

| Agent | Role | File |
|---|---|---|
| coordinator | Orchestrates audit, routes incidents, compiles report | `agents/coordinator.md` |
| regulatory-monitor | CIN validity, GDPR retention, Alloggiati Web sync, tourist tax rates | `agents/regulatory-monitor.md` |
| incident-responder | Error rates, failed background jobs, OTA sync failures, alerts | `agents/incident-responder.md` |

## Quality Harness

See [`harness.md`](./harness.md) for the full loop specification.

**Key gates**:
- All properties have valid CIN format (`IT-XXXXX-XXXXXXXXXX`)
- No GDPR records past `DataRetentionUntil` without `ErasureRequested`
- No failed Alloggiati Web background jobs older than 24 hours
- `TouristTaxRate` entity up to date (no hardcoded rates in code)
- Error rate < 1% (from structured logs)
- All OTA sync jobs completed within last 6 hours

## Exit Artifact

`Sessions/ops-report-<YYYY-MM-DD>.md` containing:
- Compliance status per regulation
- Incident log (if any)
- KPI snapshot (bookings, OTA sync, error rate)
- Action items for next sprint

## Chain

→ Loop back to **Stage 01: Planning** if action items require new features
