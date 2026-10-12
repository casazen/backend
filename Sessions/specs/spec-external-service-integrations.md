---
id:
slug: external-service-integrations
title: External service integrations beside the CasaZen supplier marketplace
phase: 1
type: feature
priority: P1
status: specced
issue:
depends_on: [micro-marketplace-v0, supplier-console-web]
blocks: []
exit_contributes_to: Host can obtain a turnover service where no Active CasaZen supplier covers the property comune, without displacing CasaZen suppliers where they exist
last_reviewed: 2026-10-08
---

# Spec — External service integrations (beside the supplier marketplace)

## Overview

The host marketplace at `/app/short-rent/marketplace` lists Active CasaZen suppliers for the selected property. Where that network does not cover the property's comune, the host has no in-product way to keep turnover moving. This spec adds a second section on the same page: a catalog of external providers that already run cleaning, laundry, linen, maintenance, or check-in operations (the same kind of marketplace Smoobu shows next to its own tools).

The catalog does not replace CasaZen suppliers. Precedence is fixed:

1. CasaZen suppliers are the default channel whenever at least one Active supplier covers the property comune for the requested category.
2. An integration becomes the default only when the host explicitly sets it. CasaZen suppliers stay listed and can still be chosen for a single request.
3. When no Active CasaZen supplier covers that comune and category, the integration catalog is the alternative. Nothing is auto-selected.

v1 records the host's choice and tracks an external handoff inside CasaZen. It does not call partner APIs, store partner credentials, or take a fee. The full Connect marketplace (`spec-supplier-marketplace`, frozen) stays untouched.

**Phase:** 1 — MVP · **Type:** feature · **Status:** specced · **Issue:** none yet

Design: none yet. ADRs: none.

---

## User Story

As a host, I want to keep using CasaZen suppliers when they cover my property, and to choose an external provider that already does the same work when they do not — or when I explicitly prefer that provider — so that turnover does not stop in a territory where the CasaZen network is not developed.

---

## Acceptance Criteria

### Precedence

- **AC1**: For a property and a `ServiceCategories` code, the resolved channel is exactly one of `casazen`, `external`, `fallback`.
  - `external` when the host org has an active default integration for that property and that category.
  - Otherwise `casazen` when `GetActiveByComune` (same comune match as today's supplier picker: Active profile, category, property comune) returns at least one supplier.
  - Otherwise `fallback`.
  - Pending, suspended, or inactive profiles do not count as coverage. A supplier that covers a different comune does not count.

- **AC2**: Setting a default never deletes, hides, or suspends CasaZen suppliers. Clearing the default returns the channel to `casazen` when AC1 coverage exists, and to `fallback` when it does not. There is no org-wide default in v1: the choice is stored per host org, per property, per category.

- **AC3**: A service request that names a CasaZen `supplierOrgId` is created on the existing CasaZen path even when the channel is `external`. The supplier inbox receives it. The default integration is left unchanged.

### Backend

- **AC4**: Platform catalog `ExternalServiceProvider`, not tenant-owned. Fields: stable `Code` (lowercase, unique), `DisplayName`, Italian `Summary` (max 200 characters), `Categories` (subset of `ServiceCategories.All`), `LearnMoreUrl` (absolute `https` URL), `IsListed` (bool). No API keys, secrets, or webhook URLs.

- **AC5**: Seed, all `IsListed = true`, coverage for the fallback is any Italian property (v1 does not filter the catalog by comune). Codes and categories:

  | Code | Display name | Categories | Summary (Italian, exact) |
  |---|---|---|---|
  | `operto` | Operto | `check-in` | Tecnologia per gli ospiti che riduce i costi di gestione. |
  | `turno` | Turno | `cleaning` | Trova e gestisci servizi di pulizia per affitti brevi. |
  | `properly` | Properly | `cleaning` | Piattaforma di controllo qualità degli interventi. |
  | `doinn` | Doinn | `cleaning` | Trova e gestisci servizi di pulizia per affitti brevi. |
  | `tidy` | TIDY | `cleaning`, `maintenance` | Automazione di pulizia e manutenzione dell'immobile. |
  | `sali-mietwaesche` | Sali Mietwaesche | `laundry`, `linen` | Biancheria pulita, consegnata a domicilio. |

  `LearnMoreUrl` values are the providers' public homepages, checked at implementation time and stored in the seed. A missing or non-https URL fails the seed test. These rows are a catalog the host can choose. They are not a partnership, a reseller agreement, or a claim that CasaZen operates the provider.

- **AC6**: Tenant-owned `PropertyServiceIntegration`: `Id`, `OrgId` (host), `PropertyId`, `Category`, `ProviderCode`, `IsDefault` (always `true` while the row exists), `CreatedAt`, `UpdatedAt`. Unique on (`OrgId`, `PropertyId`, `Category`). The property must belong to `OrgId`. `ProviderCode` must exist, be listed, and include `Category`.

- **AC7**: `GET /api/properties/{propertyId}/service-integrations?category={code}` (host with access to the property: `PropertyOwner`, `Admin`, or `PropertyManager`). Returns `resolution` (`casazen` | `external` | `fallback`), `casazenSupplierCount` (int), `defaultProvider` (catalog row or null), and `catalog` (listed providers whose categories include `category`, when `category` is present; otherwise every listed provider). Another org's property returns 404. An unknown category returns 422 `invalid_service_category`.

- **AC8**: `PUT /api/properties/{propertyId}/service-integrations/{providerCode}` with body `{ "category": "<code>" }` upserts the default for that property and category (replacing a previous provider for the same pair). 201 on first insert, 200 on replace. Unknown provider: 404 `integration_provider_unknown`. Provider not listed or category not on that provider: 422 `integration_category_unsupported`. Property of another org: 404.

- **AC9**: `DELETE /api/properties/{propertyId}/service-integrations?category={code}` removes the default. 204 when a row was deleted. 404 `integration_default_missing` when none exists. After a successful delete, AC7 follows AC1 without that default.

- **AC10**: External handoff. `POST /api/service-requests` gains an optional `externalProviderCode`. Rules:
  - `supplierOrgId` set and `externalProviderCode` empty: unchanged CasaZen create (AC3). `SupplierOrgId` stays required on that row.
  - `externalProviderCode` set and `supplierOrgId` empty: creates a handoff only if that provider is the current default for the property and category (AC8). Response 201. The row stores `ExternalProviderCode`, `SupplierOrgId` null, status `InoltratoEsterno`. It is not visible in the supplier inbox and does not send the supplier "new request" email.
  - Both set, or neither set: 422 `service_channel_required`.
  - `externalProviderCode` set when that provider is not the current default: 422 `integration_not_default`.
  - Supplier take, complete, reject, and mark-paid on an `InoltratoEsterno` row return 409 `external_handoff_no_supplier_transition`.

- **AC11**: Existing CasaZen requests are unchanged by the migration: `SupplierOrgId` remains set, `ExternalProviderCode` null, status values unchanged. Supplier inbox queries exclude rows with a non-null `ExternalProviderCode`.

### Frontend

- **AC12**: On `/app/short-rent/marketplace`, after the property (and optional category) is chosen, the page shows two stacked sections. First heading, exact: **Fornitori CasaZen**. Second heading, exact: **Integrazioni**. The existing supplier grid, supplier detail, and "Richiedi fornitore" flow stay in the first section. Nav item and route do not change. No second marketplace route.

- **AC13**: Channel copy, exact Italian strings:
  - `casazen`: under the first heading, **I fornitori CasaZen hanno la precedenza.** The integration section intro is **Un'integrazione diventa la scelta predefinita solo se la selezioni.**
  - `external`: the matching integration card shows the badge **Predefinita**. Intro: **Le nuove richieste di questa categoria usano {DisplayName}. Puoi ancora richiedere un fornitore CasaZen per un singolo intervento.**
  - `fallback`: the CasaZen section empty state is **In questo territorio non c'è ancora una rete CasaZen per questa categoria.** The integration intro is **Puoi scegliere un'integrazione.**
  - Zero catalog rows (category with no seeded provider, e.g. `plumbing`): **Nessuna integrazione disponibile per questa categoria.** This empty state is still visible. It does not hide the CasaZen section.

- **AC14**: Each listed provider is a card with `DisplayName`, the Italian `Summary`, one badge per category (existing category labels), and two actions: **Scopri** (opens `LearnMoreUrl` in a new tab, `rel="noopener noreferrer"`) and **Imposta come predefinita**. The card that is the current default replaces the second action with **Rimuovi predefinita**.

- **AC15**: **Imposta come predefinita** while `resolution` is `casazen` opens a confirm dialog before the PUT. Title: **Impostare {DisplayName} come predefinita?** Body: **I fornitori CasaZen non vengono rimossi. Le nuove richieste di questa categoria useranno {DisplayName}, finché non togli la predefinita.** Confirm button: **Imposta predefinita**. Cancel leaves the channel on `casazen` and does not call the API. When `resolution` is already `fallback` or `external`, the PUT runs without that dialog (replacing the previous default does not need a second confirm).

- **AC16**: After a successful PUT, AC7 is refetched and the card shows **Predefinita**. The CasaZen grid is still rendered. **Rimuovi predefinita** calls AC9, then the copy returns to the `casazen` or `fallback` strings in AC13. A failed PUT or DELETE shows the ProblemDetails message in Italian (no stack, no raw JSON). The card does not show **Predefinita** after a failed PUT.

- **AC17**: On the default integration card, **Usa questa integrazione** opens the existing request form with the property, the category, and `externalProviderCode` set to that provider. Submit calls AC10 and the new row appears in **Le tue richieste** with the provider `DisplayName` (not a supplier legal name) and status label **Inoltrata al provider**. The supplier inbox does not list it. **Richiedi fornitore** on a CasaZen card still submits `supplierOrgId` and no `externalProviderCode`.

### Cross-cutting

- **AC18**: Catalog and integration payloads contain no partner credentials, no supplier bank data, and no guest PII. Creating a handoff does not send the guest name, email, phone, or document data to the provider URL. **Scopri** is a browser navigation to `LearnMoreUrl` with no query string appended by CasaZen.

- **AC19**: Host without access to the property receives 404 on AC7, AC8, and AC9. Unauthenticated requests receive 401. The marketplace page stays behind the existing protected route.

---

## Verifiable Outcomes

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | Given suppliers and defaults, resolution is `external` if a default row exists, else `casazen` if the active-comune count is ≥ 1, else `fallback`. A pending supplier in the same comune yields `fallback` when no default exists. | Default ignored; pending supplier counted; wrong comune counted |
| AC2 | L1 | DELETE removes the row. A following resolve call is `casazen` when an Active covering supplier exists and `fallback` when not. Supplier profile rows are unchanged. | Suppliers deleted or hidden; default survives the delete; org-wide row affects another property |
| AC3 | L1 | POST with `supplierOrgId` and empty `externalProviderCode` creates `Richiesto` assigned to that supplier while a default integration exists for the same property and category. Inbox contains that id. Default row still present. | Request forced onto the integration; inbox missing the row; default cleared |
| AC4 | L1 | Catalog entity rejects a secret/API-key column. Listed query returns code, display name, summary, categories, https learn-more URL. | Secret field present; relative URL accepted |
| AC5 | L1 | Seed contains the six codes, exact Italian summaries, and category sets in the AC5 table. Each `LearnMoreUrl` is absolute https. | Missing code; summary drift; `tidy` without `maintenance`; http URL |
| AC6 | L1 | Second insert for the same org, property, and category fails unique constraint or is an update of `ProviderCode`. Provider without that category is rejected. Property of another org is rejected. | Two defaults for one category; cross-org property id accepted |
| AC7 | L1 | GET returns `resolution`, `casazenSupplierCount`, `defaultProvider`, and `catalog` filtered by category. Other org: 404. Bad category: 422 `invalid_service_category`. | Count includes inactive suppliers; catalog ignores category; 200 for another org |
| AC8 | L1 | First PUT returns 201 and GET then returns `resolution=external` and that provider. Second PUT with another valid provider returns 200 and replaces the code. Unknown code: 404 `integration_provider_unknown`. Wrong category: 422 `integration_category_unsupported`. | 201 on replace; two rows left; Operto accepted for `cleaning` |
| AC9 | L1 | DELETE returns 204 and the next GET is not `external`. Second DELETE returns 404 `integration_default_missing`. | 204 when nothing was stored; channel stays `external` |
| AC10 | L1 | Handoff POST returns 201, `supplierOrgId` null, `externalProviderCode` set, status `InoltratoEsterno`. Supplier inbox GET does not include it. No supplier email is sent. Both ids or neither: 422 `service_channel_required`. Provider that is not the default: 422 `integration_not_default`. Take/complete/reject/mark-paid: 409 `external_handoff_no_supplier_transition`. | Inbox shows the handoff; email sent; status `Richiesto`; supplier transition succeeds |
| AC11 | L1 | Migration of a pre-existing `ServiceRequest` keeps its `SupplierOrgId` and status. Inbox still returns it. | Existing row nulled; existing row flipped to `InoltratoEsterno` |
| AC12 | L2 + L3 | With a property selected, the page shows headings **Fornitori CasaZen** and **Integrazioni** in that order. Supplier cards and **Richiedi fornitore** still work. URL remains `/app/short-rent/marketplace`. | Integrations replace the supplier grid; new nav route only |
| AC13 | L2 + L3 | Fixture with an Active covering supplier and no default shows **I fornitori CasaZen hanno la precedenza.** Fixture with zero Active suppliers shows **In questo territorio non c'è ancora una rete CasaZen per questa categoria.** and **Puoi scegliere un'integrazione.** Category `plumbing` shows **Nessuna integrazione disponibile per questa categoria.** | English empty state; blank panel; CasaZen section removed in fallback |
| AC14 | L2 + L3 | Card shows display name, exact summary, category badges, **Scopri**, and **Imposta come predefinita**. **Scopri** target is the seeded https URL with `noopener`. Default card shows **Rimuovi predefinita** and badge **Predefinita**. | Learn-more button posts a secret; default card still offers set-default only |
| AC15 | L2 + L3 | From `casazen`, the confirm body contains **I fornitori CasaZen non vengono rimossi.** Cancel does not change the badge. Confirm results in **Predefinita** on that card. From `fallback`, no dialog and the badge appears after success. | PUT fired on cancel; dialog skipped while CasaZen suppliers exist |
| AC16 | L2 + L3 | After PUT, refetch shows **Predefinita** and the CasaZen cards remain. After DELETE, AC13 copy matches the new resolution. Forced 422 shows the Italian problem message and the badge is absent. | Badge stuck after error; supplier grid unmounted |
| AC17 | L2 + L3 | **Usa questa integrazione** submits `externalProviderCode` and the requests list shows the display name plus **Inoltrata al provider**. **Richiedi fornitore** submits `supplierOrgId` only. Supplier inbox fixture does not list the handoff. | Handoff labeled with a supplier legal name; both ids sent; inbox contains the handoff |
| AC18 | L1 + L2 | Handoff response and catalog JSON have no guest email, phone, or document fields. **Scopri** href equals `LearnMoreUrl` with an empty query string. | Guest email appended to the provider URL; credential field in JSON |
| AC19 | L1 + L2 | Logged-out visit to the marketplace redirects to login. A host without property access gets 404 from the integrations GET and the page shows the Italian load error, not another org's catalog. | 200 with another org's default; blank 401 page |

Rules:
- UI ACs need L2 and L3 outcomes.
- Non-UI ACs may be L1-only.
- Visibility-only asserts are not enough for PUT, DELETE, or the request submit.

---

## UX / UI Quality

| Criterion | Required | How to verify |
|---|---|---|
| Primary path clear | Host selects a property, sees CasaZen first, and can set or skip an integration without leaving the page | L3 script below, two resolutions |
| Language | Primary headings, empty states, and buttons are the Italian strings in AC13–AC17 | L2/L3 assert those strings |
| Empty state | No CasaZen supplier and no catalog row each show the documented sentence | L2 fixtures: zero suppliers; category `plumbing` |
| Error state | Failed PUT/DELETE shows the Italian ProblemDetails message | L2 route abort / 422 |
| Destructive / legal copy | Confirm before overriding CasaZen precedence, with the AC15 sentences | L2 assert dialog, then cancel and confirm |

**Happy-path script:**

1. Open `/app/short-rent/marketplace`.
2. Select a property in a comune with an Active CasaZen cleaning supplier and no default integration. The first section shows that supplier and **I fornitori CasaZen hanno la precedenza.**
3. On Turno, choose **Imposta come predefinita**, read the confirm copy, confirm. The Turno card shows **Predefinita**. The CasaZen supplier card is still there.
4. **Richiedi fornitore** on the CasaZen card still creates a CasaZen request.
5. **Usa questa integrazione** on Turno creates a row **Inoltrata al provider** named Turno.
6. **Rimuovi predefinita**. Copy returns to the CasaZen precedence sentence.
7. Select a property whose comune has no Active supplier for `cleaning`. Empty state is **In questo territorio non c'è ancora una rete CasaZen per questa categoria.** Set Turno as default with no confirm dialog.

Done when steps 3, 4, 5, and 7 match the Verifiable Outcomes for AC13, AC15, AC3, and AC17.

---

## Technical Notes

| File | Action |
|---|---|
| `Casazen.Core/Entities/ExternalServiceProvider.cs` | Create — catalog row, no secrets |
| `Casazen.Core/Entities/PropertyServiceIntegration.cs` | Create — host org, property, category, provider code |
| `Casazen.Core/Entities/ServiceRequest.cs` | Modify — `SupplierOrgId` nullable, `ExternalProviderCode` nullable, status `InoltratoEsterno` |
| `Casazen.Infrastructure/Data/AppDbContext.cs` | Modify — DbSets, unique index, tenant filter on the integration only |
| `Casazen.Infrastructure/Migrations/` | Create — tables, seed, backfill nothing (existing requests keep supplier ids) |
| `Casazen.Web/Controllers/PropertyServiceIntegrationsController.cs` | Create — AC7–AC9 |
| `Casazen.Web/Controllers/ServiceRequestsController.cs` | Modify — AC10 |
| `Casazen.Infrastructure/Services/SupplierMatchService.cs` | No change. Precedence is the integrations endpoint plus create, not the AI match fallback |
| `frontend/src/features/marketplace/marketplace-page.tsx` | Modify — two sections, AC12–AC17 |
| `frontend/src/i18n/locales/it.json` and `en.json` | Modify — keys for the exact Italian strings; English is a translation, tests assert Italian |

**Complexity:** M  
**Migration:** yes — new tables; `ServiceRequests.SupplierOrgId` becomes nullable. Existing rows stay non-null.  
**Dependencies:** `spec-micro-marketplace-v0`, `spec-supplier-console-web`  
**Repos:** BE and FE. Mobile does not gain a screen in v1.

Catalog seed URLs are data, reviewed when the migration is written. Do not invent a partnership status in the UI.

---

## Test expectations

| Layer | Allowed | Forbidden as sole proof |
|---|---|---|
| L1 | xUnit tests that assert resolution, HTTP codes, `code` values, seed rows, inbox exclusion, and unchanged legacy requests | Compiles; a single test that only checks 200 |
| L2 | Playwright with `page.route`, one titled `test('ACn: …')` per UI AC | One smoke covering AC12–AC17; visibility-only on PUT |
| L3 | Real local API for the happy-path script | Mocking the integrations API in the L3 spec |

---

## Regulatory / Legal Gates

- None. v1 does not transmit guest PII to the external provider and does not collect partner credentials.
- The catalog names third-party brands so the host can recognize them. UI copy must not say CasaZen is the provider or that a contract with that provider exists.

---

## Out of Scope

- Partner API clients, OAuth, webhooks, job status sync, or stored API keys for Operto, Turno, Properly, Doinn, TIDY, Sali Mietwaesche, or any later provider.
- Stripe Connect, platform take-rate, and payouts (`spec-supplier-marketplace` stays frozen).
- Hiding, ranking below, or auto-declining CasaZen suppliers when an integration is default.
- Org-wide or account-wide default. Per-request CasaZen choice does not change the default.
- Automatic creation of a request after checkout.
- AI supplier discovery (`AiSupplierDiscovery`): left as it is. It is not the fallback this spec adds.
- Admin UI to edit the catalog. v1 catalog changes are a seed/migration change.
- Mobile marketplace screen.
- Categories with no seeded provider are a documented empty state, not a prompt to add a provider in the product.

---

## Open Questions

- None for the precedence rules. The seed list can be replaced by another migration before implementation without changing AC1–AC3 or AC12–AC17; the AC5 table is the list until that happens.
