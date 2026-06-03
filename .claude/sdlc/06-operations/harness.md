# Stage 06: Operations — Quality Harness

## Entry Criteria

- New release `vX.Y.Z` deployed (post Stage 05), OR
- Monthly compliance audit due (first Monday of each month)

## Council Run

Coordinator spawns: `regulatory-monitor`, `incident-responder`

Topic handed to council:
> "Run post-deploy compliance and operations audit for [date/release]. Check Italian regulatory compliance, background job health, and system KPIs. Produce ops report at Sessions/ops-report-<YYYY-MM-DD>.md."

## Quality Gates

| # | Gate | How to check | Pass condition |
|---|---|---|---|
| G1 | CIN format valid | Query: `SELECT COUNT(*) FROM Properties WHERE CIN NOT LIKE 'IT-_____-__________'` | 0 properties with invalid CIN |
| G2 | GDPR retention clean | Query: `SELECT COUNT(*) FROM Guests WHERE DataRetentionUntil < GETUTCDATE() AND ErasureRequested = 0` | 0 overdue records |
| G3 | Alloggiati Web jobs healthy | Check Hangfire dashboard or `SELECT * FROM HangfireJobs WHERE State='Failed' AND JobType LIKE '%Alloggiati%'` | No failed jobs older than 24 hours |
| G4 | Tourist tax rates current | Query: `SELECT * FROM TouristTaxRates WHERE LastUpdated < DATEADD(MONTH, -6, GETUTCDATE())` | No rates older than 6 months without review |
| G5 | Error rate acceptable | Check application logs for last 24h | Error rate < 1% of all requests |
| G6 | OTA sync current | Query: `SELECT * FROM OtaIntegrations WHERE LastSyncAt < DATEADD(HOUR, -6, GETUTCDATE())` | All integrations synced within last 6 hours |

## Harness Loop

```
iteration = 0
max_iterations = 3

WHILE (any gate in G1–G6 fails) AND (iteration < max_iterations):
  1. Coordinator classifies failures: regulatory → regulatory-monitor, operational → incident-responder
  2. G1 fails → regulatory-monitor identifies properties with invalid CIN, creates GitHub Issue for correction
  3. G2 fails → regulatory-monitor triggers GDPR erasure workflow for overdue records
  4. G3 fails → incident-responder investigates Alloggiati Web failures, retries or creates P1 incident
  5. G4 fails → regulatory-monitor creates GitHub Issue to update TouristTaxRate entity
  6. G5 fails → incident-responder investigates error spike, creates incident report
  7. G6 fails → incident-responder triggers OTA sync retry, escalates if sync continues to fail
  8. Re-check failed gates
  9. iteration++

IF iteration == max_iterations AND regulatory gates (G1–G4) still failing:
  ESCALATE: create P0 compliance incident, notify team immediately
  Human decision required
```

## Exit Artifact

`Sessions/ops-report-<YYYY-MM-DD>.md` with:

```markdown
# Operations Report — YYYY-MM-DD (vX.Y.Z)

## Compliance Status
| Regulation | Status | Notes |
|---|---|---|
| CIN (D.L. 145/2023) | ✅/⚠️/❌ | ... |
| GDPR (Article 17) | ✅/⚠️/❌ | ... |
| Alloggiati Web | ✅/⚠️/❌ | ... |
| Tourist Tax | ✅/⚠️/❌ | ... |

## Incident Log
- None / [incident description + resolution]

## KPI Snapshot
- Error rate: X%
- OTA sync coverage: X/6 platforms
- Active bookings: N

## Action Items
- [ ] [item] → Stage 01 issue: #M
```

## Chain

→ Each action item becomes input to **Stage 01: Planning** in the next sprint
