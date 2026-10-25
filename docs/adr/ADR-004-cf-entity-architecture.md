# ADR-004: ConcordatoFacile Entity Architecture

**Status:** Superseded come riferimento del processo ConcordatoFacile (25 ottobre 2026; era Accepted)  
**Date:** 2026-10-23  
**Issue:** none (run R20261023-1107)  
**Informs:** NF-01, NF-02, NF-03, NF-05, NF-08 (ConcordatoFacile integration)

> **SUPERATO come riferimento del processo ConcordatoFacile (25 ottobre 2026).** Superato dal documento `docs/integrations/concordatofacile/README.md`: non usare come riferimento. Il file resta nel repository perché i commenti del codice e dei test citano gli identificativi delle sue regole (`ADR-004-Rn`): sono etichette, non requisiti validi. I fatti sul processo sono in quel documento, i requisiti in `Sessions/business-analysis-canone-concordato.md`.
>
> Punti che non valgono più o non sono attuati:
>
> - Entità e indici (R1–R5) sono attuati (`ConcordatoFacilePurchases`, `ConcordatoFacileGifts`); lo snapshot di stato resta nella memoria del processo e non ha una tabella.
> - R6 (consumare l'omaggio solo per un contratto dell'organizzazione) **non è attuata**: la correzione è in corso (task CF-FIX2).

## Context

CasaZen integrates with ConcordatoFacile (CF), an external partner service that assists landlords with "canone concordato" (rent-controlled) lease agreements. The integration spans multiple features:

- **NF-01**: Minimal draft creation with CF marker
- **NF-02**: One-time payment (EUR 119,00) for CF service access
- **NF-03**: Biennial subscription gift (one free practice)
- **NF-05**: HMAC-signed link generation to CF portal
- **NF-08**: Practice status polling from CF API

**Existing code** (from `kb/gap/gap-analysis.md`):
- `LeaseContract` entity (`work/base/backend/Casazen.Core/Entities/LeaseContract.cs`) tracks lease state
- `ProcessedStripeEvent` (`work/base/backend/Casazen.Core/Entities/ProcessedStripeEvent.cs:7-19`) provides webhook idempotency
- `ITenantOwned` interface (`work/base/backend/Casazen.Core/Entities/LeaseContract.cs:11`) ensures tenant isolation
- PostgreSQL advisory locks in `UserRepository` (`work/base/backend/Casazen.Infrastructure/Repositories/UserRepository.cs:144-147`) handle concurrency

**Repo scope:** Backend (entities stored in backend database, consumed by frontend via API)

**Mobile:** NOT-APPLICABLE - mobile app intentionally excludes lease/contract management per ADR-003

## Decision

### Separate Entities (Option A)

Introduce three dedicated entities for CF integration, rather than embedding columns in LeaseContract or Org:

1. **ConcordatoFacilePurchase**: Tracks one-time payment state per lease
2. **ConcordatoFacileGift**: Tracks biennial subscription gift per organization (one gift per org)
3. **ConcordatoFacileStatusSnapshot**: In-memory cache for CF practice status (persistent storage deferred)

**Rationale:**
- Clean separation allows independent audit, testing, and evolution
- Payment idempotency via `ProcessedStripeEvent` requires separate purchase entity with `StripeCheckoutSessionId` FK
- Gift concurrency via advisory lock demands atomic state in dedicated entity
- Status snapshot can start in-memory (`IMemoryCache`) and migrate to persistent storage if offline viewing proves necessary

### Entity Schemas

**ConcordatoFacilePurchase:**
```csharp
[Table("ConcordatoFacilePurchases")]
public class ConcordatoFacilePurchase : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }  // ITenantOwned
    public Guid LeaseContractId { get; set; }
    public string StripeCheckoutSessionId { get; set; }
    public string? StripePaymentIntentId { get; set; }
    public int AmountCents { get; set; }  // 11900
    public string Currency { get; set; }  // "eur"
    public ConcordatoFacilePurchaseStatus Status { get; set; }  // Pending/Completed/Failed/Refunded
    public DateTime CreatedAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime? RefundedAt { get; set; }
}
```

**ConcordatoFacileGift:**
```csharp
[Table("ConcordatoFacileGifts")]
public class ConcordatoFacileGift : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }  // ITenantOwned, unique constraint
    public string GrantedBySubscriptionId { get; set; }
    public DateTime GrantedAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
    public Guid? ConsumedForLeaseId { get; set; }
    public ConcordatoFacileGiftStatus Status { get; set; }  // Available/Consumed
}
```

### Rejected Alternatives

- **Embedded columns in LeaseContract/Org (Option B)**: Couples unrelated concerns, complicates testing, harder to evolve
- **Hybrid with embedded purchase (Option C)**: Still couples purchase state to LeaseContract lifecycle

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-004-R1 | P0 | `ConcordatoFacilePurchase` entity MUST implement `ITenantOwned` with `OrgId` NOT NULL FK and include `LeaseContractId` FK, `StripeCheckoutSessionId`, `StripePaymentIntentId` nullable, `AmountCents`, `Currency`, `Status` enum (Pending/Completed/Failed/Refunded), `CreatedAt`, `PaidAt` nullable, `RefundedAt` nullable |
| ADR-004-R2 | P0 | `ConcordatoFacileGift` entity MUST implement `ITenantOwned` with `OrgId` FK and UNIQUE constraint (one gift per org), `GrantedBySubscriptionId`, `GrantedAt`, `ConsumedAt` nullable, `ConsumedForLeaseId` FK nullable, `Status` enum (Available/Consumed) |
| ADR-004-R3 | P0 | All CF entities MUST use `Guid` PK with `[Table("...")]` attribute and follow naming convention `ConcordatoFacile*` prefix |
| ADR-004-R4 | P0 | Migration `AddConcordatoFacileEntities` MUST create indexes: compound `(OrgId, LeaseContractId)` on ConcordatoFacilePurchase for lookup, `(StripePaymentIntentId)` for webhook, unique `(OrgId)` on ConcordatoFacileGift |
| ADR-004-R5 | P0 | All CF entity reads MUST scope by `query.Where(p => p.OrgId == userOrgId)` pattern for row-level tenant isolation |
| ADR-004-R6 | P0 | Gift consumption MUST verify `LeaseContract.OrgId == gift.OrgId` before assignment |
| ADR-004-R7 | P0 | Status snapshot cache MUST NOT store personal data; practiceId, status enum, dates only (no names, emails, addresses from future CF API fields) |
| ADR-004-R8 | P1 | `ConcordatoFacileStatusSnapshot` (if persistent) MUST have `LeaseContractId` FK, `FetchedAt`, `ExpiresAt`, `Practices` jsonb, `LastError` nullable, index `(LeaseContractId, FetchedAt DESC)` |

## Consequences

**Follow-on work:**
- Backend: EF migration `AddConcordatoFacileEntities` creates both tables with indexes
- Backend: Entity configuration classes in `Casazen.Infrastructure/EntityConfigurations/`
- Backend: DTOs for API responses (do not expose entity names to client)
- Tests: PostgresFact integration tests verify FK integrity and unique constraint
- Demo: Add CF scenarios to `DemoShowcaseCatalog.cs` with `TestDataMarkers`

**Risks:**
- Migration ordering: entities must be created before services that consume them
- Entity nullability must match spec (gift consumption is optional, purchase refund is optional)

**CI cost:** 3 new entities = ~10s added to migration replay test time

**Deferred:** Persistent `ConcordatoFacileStatusSnapshot` entity (start with IMemoryCache, migrate if offline viewing needed)
