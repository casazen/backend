## User Story
Come **proprietario**, voglio **registrare e gestire il Codice CIN delle mie proprietà**, in modo da **essere conforme all'obbligo normativo ed evitare sanzioni da €800 a €8.000 per immobile**.

## Contesto Normativo
- **Riferimento**: D.L. 145/2023 conv. L. 191/2023 (art. 13-ter), D.M. 03/09/2024
- **Scadenza**: **01/03/2026** per operatori esistenti (IMMINENTE!)
- **Sanzioni**: €800 - €8.000 per immobile

Il D.L. 145/2023 ha introdotto l'obbligo del Codice Identificativo Nazionale (CIN) per tutte le strutture destinate a locazioni brevi.

Il CIN deve essere:
- Ottenuto tramite la BDSR (Banca Dati Strutture Ricettive del Ministero Turismo)
- Esposto negli annunci su tutte le piattaforme OTA
- Esposto fisicamente all'esterno dell'immobile

## Stato Attuale (reconciled 2026-06-10)
**PARTIAL** — core field + validation + admin reporting exist; owner workflow and deadline alerts missing.

| Area | Status |
|---|---|
| `Property.CinCode` + `[CinCode]` validation (`IT-\d{5}-\d{10}`) | ✅ Present |
| Derived `CinStatus` (Valid/Missing/Invalid) in DTOs | ✅ Present |
| `UpdatePropertyRequest.CinCode` | ✅ Present (no dedicated endpoint) |
| Admin `GET /api/admin/cin-compliance` | ✅ Present |
| `PropertyCinBadge` on detail/search | ✅ Present |
| Owner CIN form in property UI | ❌ Missing |
| Owner `GET /api/properties/cin-compliance` | ❌ Missing |
| `PUT /api/properties/{id}/cin` dedicated endpoint | ❌ Missing |
| Deadline banner + countdown (01/03/2026) | ❌ Missing |
| `CinDeadlineAlertJob` (7-day email) | ❌ Missing |
| OTA CIN sync in adapters | ❌ Missing |
| BDSR live verification | ❌ Missing |
| Workflow enum (`NotRequested/Pending/...`) | ❌ Deferred — derived status sufficient for MVP |

## Acceptance Criteria

### Backend
- **AC1**: `GET /api/properties/cin-compliance` returns org-scoped properties with `cinStatus`, summary counts, and `daysUntilDeadline`.
- **AC2**: `PUT /api/properties/{id}/cin` saves valid CIN; returns 400 on invalid format; enforces owner authorization.
- **AC3**: `PUT /api/properties/{id}/cin` rejects duplicate CIN already assigned to another property.
- **AC4**: `CinDeadlineAlertJob` sends owner email when deadline ≤ 7 days and property has missing/invalid CIN.

### Frontend
- **AC5**: Owner properties list shows deadline banner with countdown to 01/03/2026 when any property is non-compliant.
- **AC6**: Property form includes CIN field with format hint and validation error display.
- **AC7**: Owner CIN compliance page (`/app/short-rent/cin`) lists properties with status badges and BDSR portal link.

### Deferred (follow-up issue)
- **AC-OTA**: CIN included in OTA sync payloads (Airbnb, Booking.com, etc.)
- **AC-BDSR**: Live BDSR API verification
- **AC-PDF**: Non-conformity PDF export

## Technical Notes

**Affected components**:
- `Casazen.Core/Entities/Property.cs` — uses existing `CinCode` (no new columns in MVP)
- `Casazen.Core/Validation/CinCodeAttribute.cs` — existing format validation
- `Casazen.Infrastructure/Services/PropertyService.cs` — add `GetOwnerCinComplianceAsync`, `UpdatePropertyCinAsync`
- `Casazen.Web/Controllers/PropertiesController.cs` — `GET cin-compliance`, `PUT {id}/cin`
- `Casazen.Web/BackgroundJobs/CinDeadlineAlertJob.cs` (new)
- `Casazen.Core/Services/INotificationService.cs` — `SendCinDeadlineAlertAsync`
- Frontend: `src/features/cin/`, `property-form.tsx`, `properties-page.tsx`, `route-manifest.ts`

**EF Core migration required**: No — MVP uses existing `Property.CinCode` column

**OTA platforms affected**: None in MVP (deferred)

**Background jobs**: Add `CinDeadlineAlertJob` (daily scan; alert when ≤ 7 days to 2026-03-01)

**External services**: SendGrid for deadline alert emails (stub logging until SendGrid wired)

**Complexity**: M — owner workflow + compliance dashboard on existing foundation

**MVP slice (Phase 1 pipeline scope)**:
1. Owner CIN entry form + dedicated API
2. Owner compliance dashboard with deadline countdown
3. Deadline alert job (7-day window)
4. Defer OTA sync, BDSR API, workflow enum, PDF export

## Riferimenti
- [D.L. 145/2023](https://www.gazzettaufficiale.it/eli/id/2023/10/18/23G00158/sg)
- [BDSR Ministero Turismo](https://bdsr.ministeroturismo.it)
- Regulation context: `.claude/context/regulations/cin.md`
