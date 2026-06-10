# Spec — LTR Frontend over LeasesController (US-008)

## Overview

Complete and verify the **long-term rental frontend** over the existing `LeasesController`
workflow (create → e-sign → RLI register → receipt) and **add the recurring-rent UI**
(`spec-ltr-recurring-rent`) and the **assisted-RLI panels** (`spec-ltr-rli-registration`).

This is a **complete + verify** spec, NOT greenfield: lease pages and the long-term shell
**already exist**. Issue **#177** (closed) shipped lease pages at `/leases`, `/leases/new`,
`/leases/:id`; issue **#189** (closed) shipped the `LongTermAppShell` + `LongTermSidebar` +
dual-role **layer switcher** + `use-app-layer`. This spec fills the workflow/rent/RLI gaps and
hardens them — it does **not** re-implement lease CRUD or the shell.

Reference: **US-008** (Phase 1.5 — LTR Complete + Verify)
Entry stage: **Stage 02 Design**
Scope: **Frontend** (`casazen/frontend`) — React 19, feature-slice, TanStack Query v5, Auth0, React Router v7
Mode: **complete + verify over existing FE**

### What EXISTS vs what is NEW

| | Item |
|---|---|
| **EXISTS** | `src/features/leases/*` (list / create / detail — #177); `LongTermAppShell`, `LongTermSidebar`, `layer-switcher`, `use-app-layer` (#189); `<ProtectedRoute>`; Axios JWT interceptor; TanStack Query + RHF/Zod patterns |
| **NEW** | A complete **workflow stepper** (status-driven actions), **signers panel**, **rent schedule card + ledger table**, **cedolare decision panel**, **30-day RLI checklist + delega capture**, typed `leases.api.ts` + `use-leases.ts` covering every endpoint, status-badge i18n, PII masking |

---

## User Story

As a **long-rent landlord** in the long-term layer, I want a single lease workspace where I can
create a lease, send it for e-signature, trigger and track RLI registration, download the receipt,
and see/manage recurring rent — with clear Italian status labels and no exposed tenant PII — so I
can run the whole lease lifecycle from the UI.

---

## Acceptance Criteria

### Backend

- **AC1 (no new backend)**: The FE consumes the **existing** `LeasesController` endpoints —
  `GET /api/leases`, `GET /api/leases/{id}`, `POST /api/leases`, `POST /api/leases/{id}/signing`,
  `POST /api/leases/{id}/registration`, `GET /api/leases/{id}/registration`,
  `GET /api/leases/{id}/registration/receipt` — plus the rent endpoints from `spec-ltr-recurring-rent`
  and the assisted-RLI endpoints from `spec-ltr-rli-registration`. **No controller changes.** Verify the
  existing `AllowFrontend` CORS policy and the Axios Bearer interceptor already cover `/api/leases/*`.

### Frontend

- **AC2**: `src/api/leases.api.ts` — typed module mapping every endpoint above:
  `listLeases`, `getLease`, `createLease`, `initiateSigning`, `triggerRegistration`,
  `getRegistration`, `downloadReceipt`, `getRentSchedule`, `getRentLedger`,
  `enableRentSchedule`, `disableRentSchedule`. No component constructs URLs directly.

- **AC3**: `src/queries/use-leases.ts` — `useLeases`, `useLease`, `useCreateLease`,
  `useInitiateSigning`, `useTriggerRegistration`, `useLeaseRegistration`, `useRentSchedule`,
  `useRentLedger`, `useEnableRentSchedule`; mutations invalidate `['leases']` / `['leases', id]`
  and show Sonner toasts on success and error.

- **AC4**: Lease **list** page (`/leases`) renders a table with a `lease-status-badge` mapping all
  8 `LeaseStatus` values to Italian labels (`Draft`→"Bozza", `AwaitingSignature`→"In attesa di firma",
  `PartiallySigned`→"Firmato parzialmente", `Signed`→"Firmato", `RegistrationPending`→"Registrazione in attesa",
  `SentToProvider`→"Inviato all'Agenzia", `Registered`→"Registrato", `Rejected`→"Rifiutato") and renders
  inside `LongTermAppShell`.

- **AC5**: Lease **create** form (`/leases/new`) uses RHF + Zod (`lease.schema.ts`) mirroring
  `CreateLeaseDto`: `propertyId`, `fiscalRegime` (CedolareSecca / RegimeOrdinario / CanoneConcordato),
  `startDate`, `endDate`, `monthlyRent` (> 0), `parties[]` requiring **≥1 Landlord and ≥1 Tenant**,
  each with `fiscalCode` (16 chars), `citizenship` (2 chars), `contactEmail` (email). End date must be
  after start date (client-side mirror of the server rule).

- **AC6**: Lease **detail** page (`/leases/:id`) shows a **workflow stepper** whose available action
  reflects `Status`: `Draft` → **Initiate signing**; `AwaitingSignature`/`PartiallySigned` → show
  signer URLs (read-only); `Signed` → **Trigger RLI registration**; `SentToProvider` → registration
  status (auto-refetch); `Registered` → **Download receipt**; `Rejected` → error state. Disabled/hidden
  actions for non-current statuses are asserted in tests.

- **AC7**: The signing step renders the `SignerInfo[]` returned by `POST /signing`
  (`name`, `role`, `signingUrl`, `expiresAt`) — each `signingUrl` opens in a new tab; expired links are flagged.

- **AC8**: The registration step shows `RegistrationStatus` + `RegistrationCode`; the **receipt download**
  button is enabled **only** when status is `Registered`/`Registered` registration and triggers an
  authenticated blob download (never a raw cross-origin link).

- **AC9 (recurring rent)**: The detail page includes a **rent schedule card** (cadence, billing day,
  next run date, active toggle) and a **rent ledger table** (period, amount, status badge, paid date,
  receipt link) bound to `spec-ltr-recurring-rent` DTOs. Enable/disable calls the mutation + toasts.

- **AC10 (assisted RLI)**: The detail page surfaces the assisted-RLI affordances from
  `spec-ltr-rli-registration`: a **cedolare-secca decision panel** (with the "not tax advice"
  disclaimer), a **30-day deadline countdown** computed from `RegistrationDeadline`, a **checklist**,
  a **delega/authorization capture** entry point gating "Submit RLI", and an **extra-EU tenant banner**
  when the lease's `HasExtraEUTenant` is true. There is **no** unattended-filing affordance.

- **AC11**: All `/leases/*` routes stay wrapped in `<ProtectedRoute>` and remain inside the
  **LongTermLandlord** layer (EXISTS via #189). A `PropertyOwner`-only user navigating to `/leases` is
  redirected as already specified by #189 — **do not remove** these guards.

- **AC12 (GDPR)**: No raw tenant PII beyond what the landlord needs: `fiscalCode` is **masked** (e.g.
  show last 4) in list/summary views; no PII in toasts, console logs, or URLs/query strings; the receipt
  is fetched via the authenticated endpoint only.

---

## Technical Notes

### Frontend — Files to create / modify

| File | Action |
|---|---|
| `src/api/leases.api.ts` | **CREATE / VERIFY** — typed wrappers for all lease + rent + RLI endpoints |
| `src/queries/use-leases.ts` | **CREATE / VERIFY** — query + mutation hooks, `['leases']` invalidation |
| `src/types/lease.types.ts` | **CREATE / MODIFY** — `LeaseDto`, `PartyDto`, `SignerInfoDto`, `LeaseRegistrationDto`, `RentScheduleDto`, `RentLedgerEntryDto`, enums |
| `src/features/leases/leases-page.tsx` | **MODIFY** — list + status badges (EXISTS #177) |
| `src/features/leases/lease-create-page.tsx` | **MODIFY** — RHF/Zod parties array (EXISTS #177) |
| `src/features/leases/lease-detail-page.tsx` | **MODIFY** — workflow stepper + rent + assisted-RLI sections (EXISTS #177) |
| `src/features/leases/components/lease-status-badge.tsx` | **CREATE** — 8-status Italian i18n badge |
| `src/features/leases/components/lease-workflow-stepper.tsx` | **CREATE** — status-driven actions |
| `src/features/leases/components/lease-signers-panel.tsx` | **CREATE** — `SignerInfo` list |
| `src/features/leases/components/rent-schedule-card.tsx` | **CREATE** — schedule + active toggle |
| `src/features/leases/components/rent-ledger-table.tsx` | **CREATE** — period/amount/status/receipt |
| `src/features/leases/components/cedolare-decision-panel.tsx` | **CREATE** — advisory + disclaimer (data from `spec-ltr-rli-registration`) |
| `src/features/leases/components/rli-checklist.tsx` | **CREATE** — 30-day checklist + extra-EU item |
| `src/features/leases/components/delega-capture-dialog.tsx` | **CREATE** — landlord authorization gate before submit |
| `src/features/leases/schemas/lease.schema.ts` | **CREATE** — Zod create-lease schema |
| `src/components/layout/long-term-sidebar.tsx` | **MODIFY** — Leases (and rent) nav (EXISTS #189) |
| `src/lib/axios.ts` | **VERIFY** — Bearer interceptor covers `/api/leases/*` (EXISTS) |
| `src/features/leases/__tests__/*.test.tsx` | **CREATE** — Vitest: stepper actions per status, PII masking |
| `e2e/leases.spec.ts` | **CREATE** — Playwright happy path (demo mode) |

### Backend — Files to create / modify

| File | Action |
|---|---|
| (none) | **No backend changes** — consumes existing `LeasesController` + `spec-ltr-recurring-rent` + `spec-ltr-rli-registration` endpoints |

---

## Compliance

- **GDPR (tenant / `Party` PII)**: no raw PII in views — `fiscalCode` masked; no PII in logs, toasts, or URLs; receipts only via the authenticated, owner-scoped endpoint.
- **End-user UI strings in Italian**; IT regulatory terms preserved (RLI, cedolare secca, imposta di bollo, Agenzia delle Entrate).
- **No AI** in this surface ⇒ no EU AI Act disclosure needed.

---

## Dependencies

- **Requires**: `LeasesController` (EXISTS), `LeaseWorkflowService` (EXISTS), context `long-rent` (EXISTS), the #177 lease pages (EXISTS), the #189 long-term layer/shell/switcher (EXISTS), and `spec-ltr-recurring-rent` (rent endpoints/DTOs).
- **Blocks**: LTR general availability — this is the operator-facing surface for the whole lease lifecycle.
- **Related**: `spec-ltr-rli-registration` (supplies the cedolare panel + checklist + delega data), `spec-ltr-verification` (its Playwright E2E exercises this FE flow).
- **Does not modify**: the short-stay `AppShell`/`Sidebar` or short-stay nav (the #189 layer separation keeps them isolated); lease CRUD already shipped in #177.
