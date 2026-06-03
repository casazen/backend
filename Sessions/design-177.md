# Design Spec — Issue #177
# [FE] Long-Term Lease — UI section (draft form, signing flow, registration status, receipt)

**Stage**: 02 Design  
**Date**: 2026-06-02  
**Issue**: https://github.com/casazen/backend/issues/177  
**Planning ref**: `Sessions/planning-177.md`  
**API ref (epic)**: `Sessions/design-165.md`  
**Branch target (Stage 03)**: `feature/177-lease-ui` in `casazen/frontend`  
**Status**: COMPLETE — all gates passed

---

## Scope

Frontend-only feature. No new backend endpoints — consumes existing `LeasesController` and `PropertiesController` document endpoints implemented under epic #165 / issue #174.

---

## API Contract

All endpoints below are **consumed by the frontend** (already implemented in backend). Auth policy: `[Authorize(Policy = "LongTermLandlord")]` on controller class.

Owner-scope IDOR guard: `lease.Property.OwnerId == auth-sub` enforced server-side on every lease action.

### Authentication policy (frontend)

| Concern | Implementation |
|---|---|
| JWT | Axios interceptor attaches Bearer token (`src/lib/axios.ts`) |
| Role claim | Auth0 namespace `https://casazen.app/roles` — array containing `LongTermLandlord` |
| Route guard | `<ProtectedRoute role="LongTermLandlord">` on all `/leases/*` routes |
| Demo mode | `VITE_DEMO_MODE=true` bypasses Auth0; demo user granted `LongTermLandlord` in `demo.config.ts` |
| 401/403 handling | Redirect to `/login` (401) or `/` with toast (403 missing role) |

---

### GET /api/leases

| | |
|---|---|
| **Method / Path** | `GET /api/leases` |
| **Auth** | `[Authorize(Policy = "LongTermLandlord")]` |
| **Query params** | `propertyId?: Guid` |
| **Response 200** | `LeaseContract[]` — includes nested `property`, `parties` (from EF Include) |
| **Errors** | 401, 403 |

**Frontend usage**: `leasesApi.getAll()` → `useLeases()`

---

### GET /api/leases/{id}

| | |
|---|---|
| **Method / Path** | `GET /api/leases/{id}` |
| **Auth** | `[Authorize(Policy = "LongTermLandlord")]` |
| **Response 200** | Full `LeaseContract` with `parties`, `registration`, `events`, computed `hasExtraEUTenant` |
| **Errors** | 401, 403, 404 |

**Frontend usage**: `leasesApi.getById(id)` → `useLease(id)`

---

### POST /api/leases

| | |
|---|---|
| **Method / Path** | `POST /api/leases` |
| **Auth** | `[Authorize(Policy = "LongTermLandlord")]` |
| **Request body** | `CreateLeaseDto` |

```typescript
interface CreateLeaseDto {
  propertyId: string;
  fiscalRegime: 'CedolareSecca' | 'RegimeOrdinario' | 'CanoneConcordato';
  startDate: string;       // ISO date
  endDate: string;
  monthlyRent: number;
  parties: CreateLeasePartyDto[];
}

interface CreateLeasePartyDto {
  role: 'Landlord' | 'Tenant';
  firstName: string;
  lastName: string;
  fiscalCode: string;
  citizenship: string;     // ISO 3166-1 alpha-2
  contactEmail: string;
}
```

| **Response 201** | `LeaseContract` (status = `Draft`) |
| **Errors** | 400 `{ error: string }` (validation, missing APE), 401, 403 |

**Frontend usage**: `leasesApi.create(dto)` → `useCreateLease()`  
**Pre-call guard (AC7)**: Client validates APE via `GET /api/properties/{id}/documents` before submit.

---

### POST /api/leases/{id}/signing

| | |
|---|---|
| **Method / Path** | `POST /api/leases/{id}/signing` |
| **Auth** | `[Authorize(Policy = "LongTermLandlord")]` |
| **Request body** | empty |
| **Precondition** | `lease.status === 'Draft'` |
| **Response 200** | `SigningInitiatedResult` |

```typescript
interface SigningInitiatedResult {
  leaseId: string;
  status: 'AwaitingSignature';
  signers: SignerInfo[];
}

interface SignerInfo {
  partyId: string;
  role: 'Landlord' | 'Tenant';
  name: string;
  signingUrl: string;
  expiresAt: string;
}
```

| **Errors** | 400 (wrong status), 401, 403, 404 |

**Frontend usage**: `leasesApi.initiateSigning(id)` → `useInitiateSigning()`  
**Note**: Signing URLs are **not** persisted on lease entity — store mutation result in component state.

---

### POST /api/leases/{id}/registration

| | |
|---|---|
| **Method / Path** | `POST /api/leases/{id}/registration` |
| **Auth** | `[Authorize(Policy = "LongTermLandlord")]` |
| **Request body** | empty |
| **Precondition** | `lease.status === 'Signed'` |
| **Response 202** | `{ leaseId, registrationStatus: 'SentToProvider', message }` |
| **Errors** | 400, 401, 403, 404 |

**Frontend usage**: `leasesApi.triggerRegistration(id)` → `useTriggerRegistration()`

---

### GET /api/leases/{id}/registration

| | |
|---|---|
| **Method / Path** | `GET /api/leases/{id}/registration` |
| **Auth** | `[Authorize(Policy = "LongTermLandlord")]` |
| **Response 200** | `LeaseRegistration` entity |
| **Errors** | 401, 403, 404 (no registration yet) |

**Frontend usage**: `leasesApi.getRegistration(id)` → `useLeaseRegistration(id, leaseStatus)`  
**Polling**: `refetchInterval: 30_000` when `leaseStatus === 'SentToProvider' | 'RegistrationPending'`

---

### GET /api/leases/{id}/registration/receipt

| | |
|---|---|
| **Method / Path** | `GET /api/leases/{id}/registration/receipt` |
| **Auth** | `[Authorize(Policy = "LongTermLandlord")]` |
| **Response 200** | `application/pdf` binary stream |
| **Errors** | 401, 403, 404 |

**Frontend usage**: `leasesApi.downloadReceipt(id)` via raw axios `responseType: 'blob'`  
**UI gate**: Button visible only when `lease.status === 'Registered'` (AC6)

---

### GET /api/properties/{id}/documents

| | |
|---|---|
| **Method / Path** | `GET /api/properties/{id}/documents` |
| **Auth** | `[Authorize]` — standard property owner policy |
| **Response 200** | `PropertyDocument[]` |
| **Errors** | 401, 403, 404 |

**Frontend usage**: `propertiesApi.getDocuments(propertyId)` — queried on property select in create form  
**APE check**: `documents.some(d => d.documentType === 'Ape')`

---

## Frontend Flow

### User journey

```mermaid
flowchart TD
    A[Sidebar: Leases] --> B{LongTermLandlord?}
    B -->|No| C[Redirect / + toast]
    B -->|Yes| D[/leases list]
    D --> E{Any leases?}
    E -->|No| F[EmptyState + Create CTA]
    E -->|Yes| G[Lease cards]
    F --> H[/leases/new]
    G --> I[/leases/:id]
    H --> J{APE present?}
    J -->|No| K[Inline validation error]
    J -->|Yes| L[POST /api/leases]
    L --> I
    I --> M{Status?}
    M -->|Draft| N[Initiate signing]
    N --> O[Show signer URLs]
    M -->|Signed| P[Register button]
    P --> Q[SentToProvider + poll]
    M -->|Registered| R[Download receipt]
    I --> S{hasExtraEUTenant?}
    S -->|Yes| T[Questura warning banner]
```

### Lease status state machine (UI actions)

| Status | Available actions |
|---|---|
| `Draft` | Initiate signing |
| `AwaitingSignature` / `PartiallySigned` | Show signing panel if URLs in local state; no re-initiate unless back to Draft |
| `Signed` | Trigger registration |
| `SentToProvider` / `RegistrationPending` | Poll registration; read-only |
| `Registered` | Download receipt |
| `Rejected` | Read-only; show failed registration badge |

---

### New / Modified Routes

| Path | Component | Auth | Notes |
|---|---|---|---|
| `/leases` | `LeasesPage` | `<ProtectedRoute role="LongTermLandlord">` | List + empty state |
| `/leases/new` | `LeaseCreatePage` | `<ProtectedRoute role="LongTermLandlord">` | Single-page form (property + parties + terms) |
| `/leases/:id` | `LeaseDetailPage` | `<ProtectedRoute role="LongTermLandlord">` | Detail + action panels |

Routes registered in `src/routes/index.tsx`.

**ProtectedRoute extension**:

```typescript
interface ProtectedRouteProps {
  children: React.ReactNode;
  role?: string;  // e.g. 'LongTermLandlord'
}
```

- If `role` omitted: existing behaviour (auth only)
- If `role` set: check `user['https://casazen.app/roles']` includes role
- Missing role → `<Navigate to="/" replace />` + `toast.error('You do not have access to this section')`
- Demo mode: treat demo user as having all roles when `demoUser.roles` includes the required role

---

### Component Plan

| Component | Status | Location | Responsibility |
|---|---|---|---|
| `LeasesPage` | new | `src/features/leases/leases-page.tsx` | Fetch list, empty state, navigate to create/detail |
| `LeaseCreatePage` | new | `src/features/leases/lease-create-page.tsx` | Page shell + submit handler |
| `LeaseDetailPage` | new | `src/features/leases/lease-detail-page.tsx` | Detail layout, action orchestration |
| `LeaseCreateForm` | new | `src/features/leases/components/lease-create-form.tsx` | RHF + Zod; APE pre-check; party fields |
| `LeaseStatusBadge` | new | `src/features/leases/components/lease-status-badge.tsx` | Color-coded status chip |
| `LeaseSigningPanel` | new | `src/features/leases/components/lease-signing-panel.tsx` | Per-signer external links |
| `RegistrationStatusPanel` | new | `src/features/leases/components/registration-status-panel.tsx` | Registration status + register/receipt actions |
| `ExtraEUWarningBanner` | new | `src/features/leases/components/extra-eu-warning-banner.tsx` | Questura 48h notice (no PII) |
| `ProtectedRoute` | modified | `src/components/auth/protected-route.tsx` | Add optional `role` prop |
| `Sidebar` | modified | `src/components/layout/sidebar.tsx` | "Leases" nav item, visible when role present |
| `EmptyState` | reused | `src/components/shared/empty-state.tsx` | Empty lease list |
| `AppShell` | reused | `src/components/layout/app-shell.tsx` | Page layout |
| `PageHeader` | reused | `src/components/layout/page-header.tsx` | Title + CTA |

**Zod schema**: `src/features/leases/schemas/lease.schema.ts`

---

### State & API

| Data | Query key | Hook | API module | Notes |
|---|---|---|---|---|
| Lease list | `['leases', params]` | `useLeases` | `leases.api.ts` | Invalidate on create |
| Lease detail | `['leases', id]` | `useLease` | `leases.api.ts` | |
| Registration | `['leases', id, 'registration']` | `useLeaseRegistration` | `leases.api.ts` | Poll 30s when pending |
| Create lease | — | `useCreateLease` | `leases.api.ts` | Toast + invalidate list |
| Initiate signing | — | `useInitiateSigning` | `leases.api.ts` | Return signers to page state |
| Trigger registration | — | `useTriggerRegistration` | `leases.api.ts` | Invalidate detail + registration |
| Property documents | `['properties', id, 'documents']` | inline `useQuery` in form | `properties.api.ts` | APE gate |
| Properties (select) | `['properties']` | `useProperties` | `properties.api.ts` | Reused |

**No Zustand** — all server state via TanStack Query; signing URLs in `useState` on detail page.

---

### TypeScript types

**File**: `src/types/lease.types.ts` — export via `src/types/index.ts`

Key enums (PascalCase, match backend JSON serialization):

```typescript
type LeaseStatus = 'Draft' | 'AwaitingSignature' | 'PartiallySigned' | 'Signed'
  | 'RegistrationPending' | 'SentToProvider' | 'Registered' | 'Rejected';
type FiscalRegime = 'CedolareSecca' | 'RegimeOrdinario' | 'CanoneConcordato';
type PartyRole = 'Landlord' | 'Tenant';
type RegistrationStatus = 'Pending' | 'SentToProvider' | 'Registered' | 'Failed';
```

---

### Error & toast handling

| Scenario | UX |
|---|---|
| 400 on create (APE missing server-side) | Generic toast: "Unable to create lease. Check property documents." |
| 400 on signing (wrong status) | Generic toast: "Signing cannot be initiated for this lease" |
| 403 on any lease call | Toast + redirect to `/` |
| 404 on detail | "Lease not found" empty state |
| Receipt 404 | Toast: "Receipt is not available yet" |
| APE missing (client) | Inline alert on form — block submit |

Never display raw API `{ error }` body if it may contain PII.

---

## Security Notes

**Auth gates**:

| Surface | Requirement |
|---|---|
| All `/leases/*` routes | `<ProtectedRoute role="LongTermLandlord">` |
| All `/api/leases/*` calls | JWT Bearer + backend `LongTermLandlord` policy |
| `/api/properties/{id}/documents` | Standard owner auth (existing) |
| Signing URLs | External provider links — `rel="noopener noreferrer"`, open in new tab |

**IDOR risk**: Mitigated server-side — FE must handle 403/404 gracefully without leaking whether lease ID exists for another owner.

**Secrets**: N/A on frontend — no API keys in FE bundle. E-sign and Openapi.it keys remain in backend `appsettings.json`.

**OTA keys**: N/A — no OTA integration in this feature.

**Stripe webhooks**: N/A.

**STRIDE summary**:

| Threat | Surface | Mitigation |
|---|---|---|
| Spoofing | Lease API | JWT + LongTermLandlord role policy |
| Tampering | Create/update lease | Owner-scope check server-side |
| Information disclosure | Party PII in UI | Show only to authenticated owner; no PII in toasts/logs |
| Information disclosure | Error responses | Generic client messages; backend returns `{ error }` without PII per #165 |
| Elevation of privilege | Route access | `ProtectedRoute role` + sidebar gating |

**PII exposure risk (frontend)**:

| Field | Display | Must NOT |
|---|---|---|
| `fiscalCode` | Detail page, form | Appear in console.log, error toasts, analytics |
| `contactEmail` | Detail page, form | Same |
| `citizenship` | Form only | Surfaced in Questura banner (use `isExtraEU` flag only) |

---

## Migration Plan

N/A — no schema changes. Frontend-only issue.

---

## GDPR Scope

**Guest entity**: N/A — short-stay `Guest` records are not involved.

**Party PII in scope** (lease contract parties, not Guest):

| Field | UI exposure | Retention |
|---|---|---|
| `firstName`, `lastName` | Form + detail | Backend: 10 years (`DataRetentionUntil`) |
| `fiscalCode` | Form + detail | Same |
| `contactEmail` | Form + detail | Same |
| `citizenship` | Form input only | Backend computes `isExtraEU`; banner uses flag only |

**Frontend obligations**:

- Do not persist party PII in `localStorage` / `sessionStorage`
- Do not include PII in URL query params
- Form state cleared on unmount after successful create
- `ErasureRequested` / `DataRetentionUntil`: enforced backend-side — FE shows read-only/anonymized data if backend returns redacted values (future)

**Cessione di fabbricato**: `ExtraEUWarningBanner` when `lease.hasExtraEUTenant === true` or any tenant party has `isExtraEU === true`. Informational only — no automated filing in MVP.

---

## Acceptance Criteria Traceability

| AC | Design element |
|---|---|
| AC1 | Routes + `ProtectedRoute role="LongTermLandlord"` |
| AC2 | `LeasesPage` + `EmptyState` |
| AC3 | `LeaseCreateForm` + `useCreateLease` |
| AC4 | `LeaseDetailPage` Draft panel + `LeaseSigningPanel` |
| AC5 | `RegistrationStatusPanel` register action |
| AC6 | Receipt download in `RegistrationStatusPanel` |
| AC7 | APE check via `propertiesApi.getDocuments` |
| AC8 | `ExtraEUWarningBanner` on detail page |

---

## Open Questions

All resolved.

1. **Feature folder vs pages/**: Use `src/features/leases/` per `FRONTEND-PROJECT.md` (not `src/pages/leases/` from epic draft).
2. **Hook location**: `src/queries/use-leases.ts` (not `src/hooks/useLeases.ts`).
3. **Multi-step vs single-page form**: Single-page form for MVP (property + landlord + tenant + terms) — reduces navigation complexity within M effort.
4. **Redirect on missing role**: `/` (dashboard) not `/dashboard` — matches existing router.
5. **Signing URL persistence**: Component state only; user must re-initiate if page refreshed during signing.

---

## Harness Gate Status

| Gate | Status | Notes |
|---|---|---|
| G1: Spec file exists | ✅ | `Sessions/design-177.md` |
| G2: API contract complete | ✅ | 7 consumed endpoints documented |
| G3: Auth on every endpoint | ✅ | All `[Authorize]`; documents endpoint owner-scoped |
| G4: Frontend flow defined | ✅ | Routes, components, journey diagram |
| G5: ProtectedRoute specified | ✅ | All 3 lease routes |
| G6: Security notes | ✅ | Auth, IDOR, PII, STRIDE |
| G7: Migration plan | ✅ | N/A — no schema changes |
| G8: GDPR scope | ✅ | Party PII documented |

**Handoff → Stage 03**: Issue `#177`, spec `Sessions/design-177.md`, branch `feature/177-lease-ui`.
