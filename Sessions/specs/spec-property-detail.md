# Spec — Property Detail Page (Issue #152)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Completare il dettaglio proprietà (BE + FE) in modo che l'owner possa vedere tutte le
informazioni dell'immobile in un'unica vista e accedere alla configurazione del prezzo
adattivo AI dalla stessa pagina.

Issue di riferimento: **#152** (OPEN — epica approvata, non ancora implementata)  
Stage di ingresso: **Stage 02 Design** (issue già approvata con acceptance criteria)

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Come property owner, voglio aprire il dettaglio di una mia proprietà e vedere in una
sola schermata: dati anagrafici, CIN status, capacità, tariffe, documenti caricati,
stato OTA, riepilogo prenotazioni, e un accesso diretto alla gestione del prezzo adattivo AI.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: `GET /api/properties/{id}/detail` restituisce `PropertyDetailResponse` che include:
  - Tutti i campi di `Property` (incluso `CinCode`, `Timezone`, amenities, `IsActive`)
  - `CinStatus`: `"Valid" | "Missing" | "Invalid"` (calcolato da formato `IT-XXXXX-XXXXXXXXXX`)
  - `Documents`: lista di `PropertyDocumentDto` — campi: `{ id, fileName, fileType, uploadedAt, downloadUrl }`
  - `OtaIntegrations`: lista di `OtaIntegrationSummaryDto` — campi: `{ platform, syncStatus, lastSyncAt }` — **MAI apiKey**
  - `BookingsSummary`: `{ totalBookings, upcomingBookings, activeBookings, nextCheckIn, nextCheckOut }`
  - `PricingAdapterSummary`: `{ isEnabled, lastAdaptedAt, nextScheduledRunAt }` — entrypoint verso AI pricing

- **AC2**: `GET /api/properties/{id}/documents` — lista documenti (200)

- **AC3**: `POST /api/properties/{id}/documents` — upload documento con multipart/form-data
  - Formati accettati: PDF, DOC, DOCX, JPG, PNG
  - Dimensione max: 10 MB
  - Solo owner, PropertyManager, Admin — 403 altrimenti

- **AC4**: `DELETE /api/properties/{id}/documents/{docId}` — elimina documento (204)
  - Solo owner, PropertyManager, Admin — 403 altrimenti

- **AC5**: `PUT /api/properties/{id}` — estendere RBAC: Admin + PropertyManager possono modificare anche senza essere owner

- **AC6**: Aggiungere policy `"PropertyManagerOrAdmin"` in `ServiceCollectionExtensions.cs`

- **AC7 (Regressione)**: La risposta di `GET /detail` non include mai il campo `apiKey` in nessuna OTA integration
  (verificare case-insensitive nel body serializzato)

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC8**: `property-detail-page.tsx` rifattorizzato con le seguenti sezioni:
  - **Header**: carousel foto, nome proprietà, città, CIN badge (verde/giallo/rosso)
  - **Info card**: bedrooms, bathrooms, maxGuests, nightlyRate, cleaningFee, damageDeposit, timezone
  - **Amenities grid**: icone + label per ogni amenity (se presente)
  - **Documents section**: lista file con link download + pulsante "Carica documento"
  - **OTA Integrations**: card per piattaforma con nome, icona stato sync, lastSyncAt
  - **Bookings Summary**: 4 KPI card — totale, upcoming, active, prossimo check-in
  - **AI Pricing card**: badge isEnabled (ON/OFF), lastAdaptedAt, nextScheduledRunAt, link `→ Gestisci prezzi AI` → `/properties/:id/pricing`

- **AC9**: CIN badge cliccabile apre tooltip con spiegazione normativa (D.L. 145/2023)

- **AC10**: Upload documento via dialog modale — drag & drop o file picker

- **AC11**: `<ProtectedRoute>` su tutte le rotte `/properties/*` (già presente — non rimuovere)

- **AC12**: Nessun campo `apiKey` visibile nella UI OTA

---


## UX / UI Quality



**Required** (Frontend ACs present). Testable bar for Stage 03.



| Criterion | Required | How to verify |

|---|---|---|

| Primary path clear | User completes happy path without guessing | L3 scripted flow below |

| Language | End-user strings Italian | L2/L3 assert Italian primary labels |

| Empty state | No blank dead-end when data length = 0 | L2 empty fixture |

| Error state | 4xx/5xx as human Italian message | L2/L3 forced error |

| Destructive / legal copy | Confirmations/disclaimers as in ACs | Assert documented phrases |



**Happy-path script:**



1. Enter the primary route for `property-detail`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---

## Export / Report Criteria



**Required** (export / feed / report ACs present).



### Feed / file



| Requirement | Required |

|---|---|

| Declared Content-Type matches payload (e.g. text/calendar, text/csv, application/pdf) | yes |

| Non-empty body when seed data exists | yes |

| No CF / P.IVA / secrets in filename or URL | yes |

| Documented columns/fields or VEVENT shape in AC / design | yes |



### PDF (when applicable)



| Requirement | Required |

|---|---|

| Real PDF bytes (%PDF) - not empty stub | yes |

| Readable labeled content for the intended audience | yes |

---

## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 + L2 + L3 | `GET /api/properties/{id}/detail` restituisce `PropertyDetailResponse` che include: | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC2 | L1 | `GET /api/properties/{id}/documents` — lista documenti (200) | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | `POST /api/properties/{id}/documents` — upload documento con multipart/form-data | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | `DELETE /api/properties/{id}/documents/{docId}` — elimina documento (204) | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | `PUT /api/properties/{id}` — estendere RBAC: Admin + PropertyManager possono modificare anche senza essere owner | Outcome not met; wrong status; silent no-op |
| AC6 | L1 | Aggiungere policy `"PropertyManagerOrAdmin"` in `ServiceCollectionExtensions.cs` | Outcome not met; wrong status; silent no-op |
| AC7 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC8 | L2 + L3 | `property-detail-page.tsx` rifattorizzato con le seguenti sezioni: | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC9 | L2 + L3 | CIN badge cliccabile apre tooltip con spiegazione normativa (D.L. 145/2023) | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC10 | L2 + L3 | Upload documento via dialog modale — drag & drop o file picker | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC11 | L2 + L3 | `<ProtectedRoute>` su tutte le rotte `/properties/*` (già presente — non rimuovere) | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC12 | L2 + L3 | Nessun campo `apiKey` visibile nella UI OTA | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend — File da modificare/creare

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Azione |
|---|---|
| `Casazen.Core/DTOs/PropertyDetailResponse.cs` | Aggiungere campo `PricingAdapterSummary` |
| `Casazen.Core/Entities/PropertyDocument.cs` | Creare entità (`Id`, `PropertyId FK`, `FileName`, `FileType`, `FilePath`, `UploadedAt`) |
| `Casazen.Infrastructure/Migrations/` | Aggiungere migration `AddPropertyDocuments` |
| `Casazen.Core/Repositories/IPropertyDocumentRepository.cs` | Creare interfaccia |
| `Casazen.Infrastructure/Repositories/PropertyDocumentRepository.cs` | Implementazione EF Core |
| `Casazen.Core/Services/IPropertyDocumentService.cs` | Creare interfaccia |
| `Casazen.Infrastructure/Services/PropertyDocumentService.cs` | Upload/delete/list con storage locale o blob |
| `Casazen.Web/Controllers/PropertiesController.cs` | Aggiungere endpoints documenti + `/detail` con `PricingAdapterSummary` |
| `Casazen.Infrastructure/Services/PropertyAuthorizationService.cs` | Aggiungere check `PropertyManager` role |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | Policy `"PropertyManagerOrAdmin"` |

### Frontend — File da modificare/creare

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Azione |
|---|---|
| `src/features/properties/property-detail-page.tsx` | Refactor completo |
| `src/features/properties/components/property-cin-badge.tsx` | Nuovo componente |
| `src/features/properties/components/property-documents-section.tsx` | Nuovo componente |
| `src/features/properties/components/property-ota-summary.tsx` | Nuovo componente |
| `src/features/properties/components/property-bookings-kpi.tsx` | Nuovo componente |
| `src/features/properties/components/property-pricing-summary-card.tsx` | Nuovo componente |
| `src/queries/use-properties.ts` | `usePropertyDetail(propertyId)` → `GET /api/properties/{id}/detail` |
| `src/api/properties.api.ts` | Aggiungere `getPropertyDetail`, `uploadDocument`, `deleteDocument` |
| `src/types/property.types.ts` | `PropertyDetailDto`, `PropertyDocumentDto`, `OtaIntegrationSummaryDto`, `PricingAdapterSummaryDto` |

---

## Compliance

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **CIN (D.L. 145/2023)**: validazione formato `IT-XXXXX-XXXXXXXXXX` obbligatoria; indicatore visivo nel dettaglio
- **GDPR**: nessun dato guest esposto nel dettaglio (BookingsSummary contiene solo conteggi aggregati)
- **OTA keys**: mai nel body API né nella UI — test di regressione AC7 presidia questo requisito

---

## Dependencies

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Richiede**: migration `AddPropertyDocuments` applicata a `casazen_test` prima del deploy test
- **Blocca**: Compliance Epic (CIN display), OTA Integration Epic
- **Collegato a**: Issue #158 — test di integrazione includono endpoint `/detail`
- **Non modifica**: issue #182 (layer separation già implementata)

## Test expectations (process contract)



| Layer | Allowed | Forbidden as sole proof |

|---|---|---|

| L1 | xUnit unit/integration asserting AC outcomes | Compile-only |

| L2 | Playwright demo + page.route OK; titled test per AC | One smoke for all ACs; visibility-only for exports |

| L3 | Real API local/staging; titled test per UI AC | Mocking path under test; AC map without titled tests |



Design Stage 02 must produce ## AC Test Map with one row per AC. Stage 03/04 gate check-ac-depth.ps1 -RequireTests enforces titled tests + export depth.

## Regulatory / Legal Gates

- None

## Out of Scope

- See Acceptance Criteria non-goals / PLANNING freeze list

## Open Questions

- None (or list with owner/date before Stage 03)
