# Spec — Property Detail Page (Issue #152)

## Overview

Completare il dettaglio proprietà (BE + FE) in modo che l'owner possa vedere tutte le
informazioni dell'immobile in un'unica vista e accedere alla configurazione del prezzo
adattivo AI dalla stessa pagina.

Issue di riferimento: **#152** (OPEN — epica approvata, non ancora implementata)  
Stage di ingresso: **Stage 02 Design** (issue già approvata con acceptance criteria)

---

## User Story

Come property owner, voglio aprire il dettaglio di una mia proprietà e vedere in una
sola schermata: dati anagrafici, CIN status, capacità, tariffe, documenti caricati,
stato OTA, riepilogo prenotazioni, e un accesso diretto alla gestione del prezzo adattivo AI.

---

## Acceptance Criteria

### Backend

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

## Technical Notes

### Backend — File da modificare/creare

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

- **CIN (D.L. 145/2023)**: validazione formato `IT-XXXXX-XXXXXXXXXX` obbligatoria; indicatore visivo nel dettaglio
- **GDPR**: nessun dato guest esposto nel dettaglio (BookingsSummary contiene solo conteggi aggregati)
- **OTA keys**: mai nel body API né nella UI — test di regressione AC7 presidia questo requisito

---

## Dependencies

- **Richiede**: migration `AddPropertyDocuments` applicata a `casazen_test` prima del deploy test
- **Blocca**: Compliance Epic (CIN display), OTA Integration Epic
- **Collegato a**: Issue #158 — test di integrazione includono endpoint `/detail`
- **Non modifica**: issue #182 (layer separation già implementata)
