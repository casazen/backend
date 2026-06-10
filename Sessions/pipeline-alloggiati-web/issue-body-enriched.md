## User Story
Come **proprietario**, voglio **comunicare automaticamente i dati degli ospiti alla Questura entro 24 ore dall'arrivo**, in modo da **adempiere all'obbligo di legge ed evitare sanzioni penali**.

## Contesto Normativo
- **Riferimento**: Art. 109 TULPS, D.M. 07/01/2013
- **Scadenza**: Obbligo permanente (entro 24h dall'arrivo)
- **Sanzioni**: **PENALI** (contravvenzione) - responsabilità personale del gestore

L'art. 109 TULPS impone a tutti i gestori di strutture ricettive (inclusi affitti brevi) di comunicare alla Questura i dati di tutti gli ospiti entro 24 ore dall'arrivo. L'omessa o tardiva comunicazione comporta sanzioni penali a carico personale del gestore.

La comunicazione avviene tramite:
- Portale nazionale **Alloggiati Web** (Polizia di Stato)
- Portali regionali alternativi (Toscana, Veneto, Puglia, etc.)

## Stato Attuale (reconciled 2026-06-10)
**PARTIAL** — core scaffolding exists; production integration and guest-facing flows missing.

| Area | Status |
|---|---|
| `Guest` entity Alloggiati fields | ✅ Present (`DateOfBirth`, `PlaceOfBirth`, `Nationality`, `DocumentType`, `DocumentNumber`, `Gender`, GDPR fields) |
| `AlloggiatiWebReport` tracking | ✅ Present (`Pending/Submitted/Confirmed/Failed`, `ConfirmationNumber`, `RetryCount`) |
| `AlloggiatiWebService` | ⚠️ Stub — validates data, logs submission; **no HTTP call to Questura API** (TODO in code) |
| `AlloggiatiWebReportJob` | ✅ Hangfire job registered; triggers on check-in |
| Guest self check-in portal | ❌ Missing |
| Document scan upload (encrypted) | ❌ Missing |
| Questura credentials storage | ❌ Missing |
| Regional portal connectors | ❌ Missing |
| Compliance dashboard + 24h alerts | ❌ Missing |
| Pre-arrival email/SMS workflow | ❌ Missing |

## Acceptance Criteria

### Backend
- **AC1**: `POST /api/checkin/guest-data` saves validated guest Alloggiati fields for a booking; returns 400 if required fields missing.
- **AC2**: `POST /api/checkin/document-upload` stores encrypted document scan; returns secure reference URL.
- **AC3**: `GET /api/alloggiati/status/{bookingId}` returns report status (`Pending/Submitted/Confirmed/Failed`) + `ConfirmationNumber`.
- **AC4**: `POST /api/alloggiati/send/{bookingId}` triggers manual/fallback submission when auto-send fails.
- **AC5**: On check-in, `AlloggiatiWebReportJob` submits guest data when complete; records protocol on success.
- **AC6**: Incomplete guest data < 24h before check-in triggers urgent owner alert (email + dashboard flag).
- **AC7**: Dashboard lists bookings with missing/overdue Alloggiati communications.

### Frontend
- **AC8**: Guest self check-in page (tokenized link) collects Alloggiati fields + document upload with Italian copy.
- **AC9**: Owner dashboard shows per-booking Alloggiati status badge and 24h deadline countdown.
- **AC10**: Owner can trigger manual resend from booking detail when status is `Failed`.

### Compliance
- **AC11**: GDPR consent captured before guest PII collection; retention aligned with `Guest.DataRetentionUntil`.
- **AC12**: Questura credentials stored encrypted (Key Vault / config secret); never logged.

## Technical Notes

**Affected components**:
- `Casazen.Core/Entities/Guest.cs` — add `DocumentScanUrl`, `Citizenship` alias if needed
- `Casazen.Core/Entities/AlloggiatiWebReport.cs` — extend statuses if needed
- `Casazen.Core/Entities/PropertyQuesturaCredentials.cs` (new) — encrypted Questura creds per property/org
- `Casazen.Infrastructure/External/AlloggiatiWebService.cs` — implement real HTTP connector
- `Casazen.Web/Controllers/GuestCheckInController.cs` (new)
- `Casazen.Web/Controllers/AlloggiatiController.cs` (new)
- `Casazen.Web/BackgroundJobs/AlloggiatiWebReportJob.cs` — wire alerts
- Frontend: `src/features/checkin/`, `src/features/alloggiati/`

**EF Core migration required**: Yes — `DocumentScanUrl` on Guest, `PropertyQuesturaCredentials` table, optional `AlloggiatiWebReport.ManuallyCompleted` flag

**OTA platforms affected**: None (Alloggiati is post-booking domestic compliance)

**Background jobs**: Modify `AlloggiatiWebReportJob`; add `AlloggiatiDeadlineAlertJob` (scan bookings approaching 24h deadline)

**External services**: Blob storage for encrypted document scans; Key Vault for Questura credentials; SendGrid for pre-arrival + alert emails

**Complexity**: XL — real Alloggiati API integration + guest portal + compliance monitoring

**Technical risks**: Questura credential provisioning per property; regional portal variance; no public sandbox for Alloggiati Web API

**MVP slice (Phase 1 pipeline scope)**:
1. Guest self check-in + document upload
2. Real Alloggiati connector behind feature flag (stub fallback when creds missing)
3. Owner status dashboard + 24h alerts
4. Defer regional portals to follow-up issue

## Epic Decomposition
1. **Guest Data Collection** (self check-in + document upload) — Phase 1
2. **Alloggiati Integration** (connector + auto-send + manual fallback) — Phase 1
3. **Compliance Monitoring** (dashboard + 24h alerts) — Phase 1
4. **Regional portals** — Phase 2 follow-up

## Riferimenti
- [Art. 109 TULPS](https://www.normattiva.it/uri-res/N2Ls?urn:nir:stato:regio.decreto:1931-06-18;773)
- [Alloggiati Web](https://alloggiatiweb.poliziadistato.it/)
- [CheckInFacile - Sanzioni](https://checkinfacile.com/blog/multa-alloggiati-web-sanzioni.html)
- Gap Analysis Report: `.claude/context/gap_analysis_report_2026-03-27.md`
- Regulation context: `.claude/context/regulations/alloggiati.md`

---
_Issue enriched by SDLC pipeline Stage 01 (2026-06-10) — reconciled with existing codebase scaffolding._
