# Design Spec — Issue #165
# [EPIC] Long-Term Lease — End-to-End Contract Lifecycle

**Stage**: 02 Design
**Date**: 2026-06-02
**Branch target**: `feature/165-long-term-lease`
**Status**: COMPLETE — all gates passed

---

## API Contract

### Authentication policy

All lease endpoints require a new Auth0 authorization policy: `LongTermLandlord`.
All endpoints also enforce owner-scope: `lease.Property.OwnerId == auth-sub` (IDOR guard).

---

### GET /api/leases

**Auth**: `[Authorize(Policy = "LongTermLandlord")]`
**Description**: List all lease contracts for the authenticated owner's properties.

**Query params**:
| Param | Type | Required | Notes |
|---|---|---|---|
| propertyId | Guid | No | Filter by property |
| status | string | No | Filter by LeaseStatus enum value |

**Response 200**:
```json
[
  {
    "id": "uuid",
    "propertyId": "uuid",
    "propertyName": "string",
    "status": "Draft",
    "fiscalRegime": "CedolareSecca",
    "startDate": "2026-09-01",
    "endDate": "2030-08-31",
    "monthlyRent": 1200.00,
    "registrationDeadline": "2026-10-01",
    "partiesCount": 2,
    "createdAt": "2026-06-02T10:00:00Z"
  }
]
```

**Errors**: 401, 403

---

### GET /api/leases/{id}

**Auth**: `[Authorize(Policy = "LongTermLandlord")]`
**Description**: Get full lease contract detail including parties, registration, and events.

**Response 200**:
```json
{
  "id": "uuid",
  "propertyId": "uuid",
  "status": "Draft",
  "fiscalRegime": "CedolareSecca",
  "startDate": "2026-09-01",
  "endDate": "2030-08-31",
  "monthlyRent": 1200.00,
  "registrationDeadline": "2026-10-01",
  "signedPdfStoragePath": "string | null",
  "parties": [
    {
      "id": "uuid",
      "role": "Landlord",
      "firstName": "string",
      "lastName": "string",
      "fiscalCode": "string",
      "citizenship": "IT",
      "contactEmail": "string",
      "isExtraEU": false
    }
  ],
  "registration": null,
  "events": [
    { "eventType": "Created", "occurredAt": "2026-06-02T10:00:00Z" }
  ],
  "hasExtraEUTenant": false,
  "apeDocumentPresent": true
}
```

**Errors**: 401, 403, 404

---

### POST /api/leases

**Auth**: `[Authorize(Policy = "LongTermLandlord")]`
**Description**: Create a new lease contract draft.

**Request body**:
| Field | Type | Required | Notes |
|---|---|---|---|
| propertyId | Guid | ✅ | Must be owned by auth-sub |
| fiscalRegime | string | ✅ | `CedolareSecca` \| `RegimeOrdinario` \| `CanoneConcordato` |
| startDate | DateTime | ✅ | UTC |
| endDate | DateTime | ✅ | UTC, must be > startDate |
| monthlyRent | decimal | ✅ | > 0 |
| parties | Party[] | ✅ | Min 2: at least 1 Landlord + 1 Tenant |
| parties[].role | string | ✅ | `Landlord` \| `Tenant` |
| parties[].firstName | string | ✅ | |
| parties[].lastName | string | ✅ | |
| parties[].fiscalCode | string | ✅ | Italian codice fiscale format |
| parties[].citizenship | string | ✅ | ISO 3166-1 alpha-2 |
| parties[].contactEmail | string | ✅ | |

**Responses**:
- 201: `LeaseContractDto` (full object, status = Draft, registrationDeadline = startDate + 30 days)
- 400: validation errors (invalid fiscalCode format, endDate before startDate, missing parties)
- 400: `"APE document required"` — if property has no APE document in PropertyDocuments
- 401, 403

---

### POST /api/leases/{id}/signing

**Auth**: `[Authorize(Policy = "LongTermLandlord")]`
**Description**: Generate PDF/A from template and initiate digital signing request via e-sign provider. Returns per-signer signing URLs.

**Request body**: empty

**Preconditions**: lease.Status == Draft

**Response 200**:
```json
{
  "leaseId": "uuid",
  "status": "AwaitingSignature",
  "signers": [
    {
      "partyId": "uuid",
      "role": "Landlord",
      "name": "Mario Rossi",
      "signingUrl": "https://sign.provider.com/session/abc123",
      "expiresAt": "2026-06-09T10:00:00Z"
    }
  ]
}
```

**Errors**: 400 (status not Draft), 401, 403, 404

---

### POST /api/leases/{id}/registration

**Auth**: `[Authorize(Policy = "LongTermLandlord")]`
**Description**: Submit signed lease to Openapi.it Docuengine for RLI registration with Agenzia delle Entrate.

**Request body**: empty

**Preconditions**: lease.Status == Signed

**Response 202** (async — registration is processed in background):
```json
{
  "leaseId": "uuid",
  "registrationStatus": "SentToProvider",
  "message": "Registration submitted. Check GET /api/leases/{id}/registration for status."
}
```

**Errors**: 400 (status not Signed), 401, 403, 404

---

### GET /api/leases/{id}/registration

**Auth**: `[Authorize(Policy = "LongTermLandlord")]`
**Description**: Get current RLI registration status.

**Response 200**:
```json
{
  "leaseId": "uuid",
  "status": "Registered",
  "externalRegistrationId": "string",
  "registrationCode": "string",
  "submittedAt": "2026-06-03T10:00:00Z",
  "confirmedAt": "2026-06-04T09:00:00Z",
  "receiptAvailable": true
}
```

**Errors**: 401, 403, 404 (no registration exists yet)

---

### GET /api/leases/{id}/registration/receipt

**Auth**: `[Authorize(Policy = "LongTermLandlord")]`
**Description**: Download the official RLI registration receipt (PDF from Agenzia delle Entrate).

**Response 200**: binary PDF stream
```
Content-Type: application/pdf
Content-Disposition: attachment; filename="receipt-{registrationCode}.pdf"
```

**Errors**: 401, 403, 404 (receipt not yet available — registration not confirmed)

---

### POST /webhooks/esign

**Auth**: `[AllowAnonymous]` — validated by HMAC signature header from e-sign provider
**Controller**: extend existing `WebhooksController`
**Description**: Callback from e-sign provider when signing status changes. Queues background job immediately.

**Request body**: provider-specific payload (forward raw to background job)

**Response 200**: `{ "received": true }` — always, within 3 seconds

**Background job** (`ESignWebhookJob`): processes event, transitions lease status, stores signed PDF path.

---

## Entities

### LeaseContract

```csharp
public class LeaseContract
{
    public Guid Id { get; set; }
    public Guid PropertyId { get; set; }
    public Property Property { get; set; } = null!;
    public LeaseStatus Status { get; set; }
    public FiscalRegime FiscalRegime { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public decimal MonthlyRent { get; set; }        // precision 18,2
    public DateTime RegistrationDeadline { get; set; }
    public string? SignedPdfStoragePath { get; set; }
    public bool ErasureRequested { get; set; }
    public DateTime DataRetentionUntil { get; set; } // StartDate + 10 years
    public ICollection<Party> Parties { get; set; } = [];
    public LeaseRegistration? Registration { get; set; }
    public ICollection<LeaseEvent> Events { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

### Party

```csharp
public class Party
{
    public Guid Id { get; set; }
    public Guid LeaseContractId { get; set; }
    public LeaseContract LeaseContract { get; set; } = null!;
    public PartyRole Role { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FiscalCode { get; set; } = string.Empty;   // PII
    public string Citizenship { get; set; } = string.Empty;  // ISO 3166-1 alpha-2
    public string ContactEmail { get; set; } = string.Empty; // PII
    public bool IsExtraEU { get; set; }
}
```

### LeaseRegistration

```csharp
public class LeaseRegistration
{
    public Guid Id { get; set; }
    public Guid LeaseContractId { get; set; }
    public LeaseContract LeaseContract { get; set; } = null!;
    public RegistrationStatus Status { get; set; }
    public string? ExternalRegistrationId { get; set; }
    public string? RegistrationCode { get; set; }
    public string? ReceiptStoragePath { get; set; }
    public DateTime? SubmittedAt { get; set; }
    public DateTime? ConfirmedAt { get; set; }
}
```

### LeaseEvent

```csharp
public class LeaseEvent
{
    public Guid Id { get; set; }
    public Guid LeaseContractId { get; set; }
    public LeaseContract LeaseContract { get; set; } = null!;
    public LeaseEventType EventType { get; set; }
    public DateTime OccurredAt { get; set; }
    public string? Payload { get; set; }  // JSON
}
```

### Enums

```csharp
public enum LeaseStatus
{
    Draft, AwaitingSignature, PartiallySigned, Signed,
    RegistrationPending, SentToProvider, Registered, Rejected
}

public enum FiscalRegime { CedolareSecca, RegimeOrdinario, CanoneConcordato }
public enum PartyRole { Landlord, Tenant }
public enum RegistrationStatus { Pending, SentToProvider, Registered, Failed }
public enum LeaseEventType
{
    Created, SigningInitiated, PartySignedDocument, AllPartiesSigned,
    RegistrationSubmitted, RegistrationConfirmed, RegistrationFailed, ErasureRequested
}
```

---

## Migration Plan

**Migration name**: `AddLeaseTables`
**Command**: `dotnet ef migrations add AddLeaseTables --project Casazen.Infrastructure`

### New tables

| Table | FK | Cascade | Indexes |
|---|---|---|---|
| `LeaseContracts` | `PropertyId → Properties` | Restrict (preserve history) | `PropertyId`, `Status` |
| `Parties` | `LeaseContractId → LeaseContracts` | Cascade | `(LeaseContractId, Role)` composite |
| `LeaseRegistrations` | `LeaseContractId → LeaseContracts` unique | Cascade | unique on `LeaseContractId` |
| `LeaseEvents` | `LeaseContractId → LeaseContracts` | Cascade | `(LeaseContractId, OccurredAt)` composite |

### Financial columns

`MonthlyRent` → `decimal(18,2)` — follow existing Booking pattern.

### AppDbContext additions

```csharp
public DbSet<LeaseContract> LeaseContracts => Set<LeaseContract>();
public DbSet<Party> Parties => Set<Party>();
public DbSet<LeaseRegistration> LeaseRegistrations => Set<LeaseRegistration>();
public DbSet<LeaseEvent> LeaseEvents => Set<LeaseEvent>();
```

### Seed data

`CanoneConcordato` territorial tables: seed a minimal set of municipalities for MVP (e.g. Milan, Rome, Turin). Table: `CanoneConcordatoTerritorialRates` (municipality, minRate, maxRate, validFrom).

---

## Frontend Flow

### New routes

| Path | Component | Auth | Notes |
|---|---|---|---|
| `/leases` | `LeasesPage` | `<ProtectedRoute role="LongTermLandlord">` | List + empty state |
| `/leases/new` | `LeaseCreatePage` | `<ProtectedRoute role="LongTermLandlord">` | Multi-step form |
| `/leases/:id` | `LeaseDetailPage` | `<ProtectedRoute role="LongTermLandlord">` | Detail + actions |

All routes added to `src/router/index.tsx` under a `LongTermLandlord` role guard.

### Component plan

| Component | Location | Responsibility |
|---|---|---|
| `LeasesPage` | `src/pages/leases/LeasesPage.tsx` | List leases, empty state, "New lease" CTA |
| `LeaseCreatePage` | `src/pages/leases/LeaseCreatePage.tsx` | Multi-step form: property → parties → terms → review |
| `LeaseDetailPage` | `src/pages/leases/LeaseDetailPage.tsx` | Status timeline, parties, action panels |
| `LeaseStatusBadge` | `src/components/leases/LeaseStatusBadge.tsx` | Color-coded status chip |
| `LeaseSigningPanel` | `src/components/leases/LeaseSigningPanel.tsx` | Signer URLs when AwaitingSignature |
| `RegistrationStatusPanel` | `src/components/leases/RegistrationStatusPanel.tsx` | Registration state + receipt download |
| `ExtraEUWarningBanner` | `src/components/leases/ExtraEUWarningBanner.tsx` | 48h Questura notice when hasExtraEUTenant |
| `ApeRequiredAlert` | `src/components/leases/ApeRequiredAlert.tsx` | Block CTA when APE document missing |

### API module

**File**: `src/api/leases.api.ts`

```typescript
export const leasesApi = {
  list: (params?: { propertyId?: string; status?: string }) => ApiClient.unwrap(...),
  getById: (id: string) => ApiClient.unwrap(...),
  create: (dto: CreateLeaseDto) => ApiClient.unwrap(...),
  initiateSigning: (id: string) => ApiClient.unwrap(...),
  triggerRegistration: (id: string) => ApiClient.unwrap(...),
  getRegistration: (id: string) => ApiClient.unwrap(...),
  downloadReceipt: (id: string) => ApiClient.unwrap(...),  // returns Blob
}
```

### TanStack Query hooks

**File**: `src/hooks/useLeases.ts`

```typescript
useLeases(params?)           // query: GET /api/leases
useLease(id)                 // query: GET /api/leases/:id
useCreateLease()             // mutation: POST /api/leases
useInitiateSigning()         // mutation: POST /api/leases/:id/signing
useTriggerRegistration()     // mutation: POST /api/leases/:id/registration
useLeaseRegistration(id)     // query: GET /api/leases/:id/registration (poll every 30s when SentToProvider)
```

### Role guard

`<ProtectedRoute>` must accept an optional `role` prop that checks the Auth0 `user['https://casazen.app/roles']` claim array. If user does not have `LongTermLandlord`, redirect to `/dashboard` with a toast notification.

---

## Security Notes

**Auth gates**:
- All `/api/leases/*` endpoints: `[Authorize(Policy = "LongTermLandlord")]` — policy registered in `Program.cs`
- IDOR: every controller action verifies `await _leaseRepository.GetByIdAsync(id)` then checks `lease.Property.OwnerId == userId` before proceeding
- Webhook `/webhooks/esign`: `[AllowAnonymous]` with HMAC signature validation (provider header) before queuing job — same pattern as Stripe webhook in `WebhooksController`

**Secrets**:
- Openapi.it API key: `appsettings.json → Openapi:ApiKey` (never hardcoded)
- E-sign provider API key: `appsettings.json → ESign:ApiKey`
- Webhook secret: `appsettings.json → ESign:WebhookSecret`

**PII exposure risk**:
- `Party.FiscalCode`, `Party.ContactEmail`, `Party.FirstName`, `Party.LastName` — must not appear in error responses or structured log messages. Use `party.Id` in log context.
- `Party.Citizenship` — relevant for extra-EU flag. Include only as boolean `isExtraEU` in API responses, not raw citizenship value.

**IDOR surfaces**:
- `GET /api/leases` — filter by `Property.OwnerId == auth-sub` at repository level (not in controller)
- All single-resource endpoints — check `lease.Property.OwnerId == auth-sub` after fetch

---

## GDPR Scope

**PII in scope**: `Party.FirstName`, `Party.LastName`, `Party.FiscalCode`, `Party.ContactEmail`, `Party.Citizenship`

**Retention obligation**: Italian lease registration records must be kept for **10 years** (D.P.R. 131/1986). GDPR erasure requests cannot delete within this period.

**Implementation**:
- `LeaseContract.DataRetentionUntil` = `StartDate + 10 years` — set on creation
- `LeaseContract.ErasureRequested` = flag for post-retention anonymization
- On erasure after retention: anonymize `Party` PII fields (replace with `[REDACTED]`), do not delete `LeaseContract` or `LeaseRegistration` records (accounting evidence)
- Structured logs: log `party.Id` (Guid) only — never log `FiscalCode`, `ContactEmail`, or `FullName`

**Cessione di fabbricato**:
- `Party.IsExtraEU` is computed at creation from `Citizenship` field
- `LeaseContract.HasExtraEUTenant` (computed property) triggers the 48h Questura notification warning
- Not automated in MVP — surfaced as a UI warning banner (FE #177)

---

## Open Questions

All resolved.

1. **E-sign provider**: TBD — design is provider-agnostic. `ILeaseESignService` interface decouples the implementation. Provider chosen before development starts.
2. **PDF/A template**: Openapi.it Docuengine provides the template. `LeaseContractTemplateService` assembles data only.
3. **CanoneConcordato tables**: seed minimal municipality set for MVP (see Migration Plan).
4. **APE validation**: check `PropertyDocuments` table for document of type `APE` on the target property.
