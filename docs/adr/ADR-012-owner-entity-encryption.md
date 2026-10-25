# ADR-012: Owner Entity Encryption and GDPR

**Status:** Accepted  
**Date:** 2026-10-24  
**Issue:** none (run R20261024-1925)  
**Informs:** NF-03

## Context

Agencies store property owner financial data (IBAN, fiscal code) for statement payments and withholding tax. This data requires encryption at rest, access control, and GDPR compliance (export, deletion with fiscal retention).

**Existing code** (from `kb/gap/gap-analysis.md`):
- Encryption pattern at `Casazen.Infrastructure/Data/Encryption` uses ASP.NET Data Protection
- Existing purposes: `OtaSecretsPurpose`, `GuestDocumentPurpose`
- DataProtection key ring in database, certificate-encrypted in production

**Repo scope:** Backend (PropertyOwner entity, encryption, GDPR integration), Frontend (owner registry CRUD, IBAN masking)

## Decision

### PropertyOwner Entity

Create `PropertyOwner` entity for the owner registry (separate from OwnerUser which is for portal access):

```csharp
public class PropertyOwner : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string Email { get; set; }
    public string Phone { get; set; }
    public string FiscalCode { get; set; }  // Encrypted
    public string Iban { get; set; }  // Encrypted
    public OwnerTaxRegime TaxRegime { get; set; }  // Private | VatHolder
    public DateTime CreatedAt { get; set; }
    public DateTime? AnonymizedAt { get; set; }
}
```

### Separate Encryption Purpose

Per security seat proposal, use separate purpose string for financial data:

- `Casazen.PropertyOwner.Financial` for IBAN and fiscal code
- NOT shared with `GuestDocumentPurpose` or `OtaSecretsPurpose`
- Allows independent key rotation

### IBAN Access Control

- GET /api/owners returns `ibanMasked` (last 4 chars only): `"****1234"`
- GET /api/owners/{id}/iban requires permission `agency.owner.financial.read`
- IBAN access MUST log to audit table: `OwnerIbanAccessed` event with user, timestamp, ownerId

### IBAN Never in Logs

Architecture constraint: IBAN and fiscal code MUST NEVER appear in:
- ILogger calls
- Error responses
- OrgActivityLog
- GDPR exports (IBAN appears only in export, never elsewhere)

### PropertyOwnership Shares

Property can have multiple co-owners:

```csharp
public class PropertyOwnership : ITenantOwned
{
    // ... fields from ADR-011 ...
    public decimal SharePercent { get; set; }  // 0.00 to 100.00
}
```

Sum of shares for a property MUST equal 100.00 (validation).

### GDPR Compliance

- **Export (art. 15):** Includes owner data (name, fiscal, IBAN encrypted → plaintext in export JSON)
- **Deletion (art. 17):** If mandate active or ended < 10 years (fiscal retention D.P.R. 600/1973 art. 22): retain fiscal data, set `AnonymizedAt`, log `fiscal_retention_active`
- **After retention:** Set Iban NULL, FiscalCode NULL

### Rejected Alternatives

- **Shared encryption key:** Cross-leak risk if one purpose compromised
- **Reuse Party entity:** Party is for lease contracts, PropertyOwner is distinct domain

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-012-R1 | P0 | PropertyOwner.Iban and PropertyOwner.FiscalCode MUST use separate encryption purpose `Casazen.PropertyOwner.Financial`, never shared with GuestDocumentPurpose or OtaSecretsPurpose |
| ADR-012-R2 | P0 | IBAN and fiscal code MUST NEVER appear in logs (ILogger calls), error responses, OrgActivityLog, or exports without explicit GDPR export request |
| ADR-012-R3 | P0 | GET /api/owners returns IBAN masked (`****1234` last 4 chars). GET /api/owners/{id}/iban requires permission `agency.owner.financial.read` AND logs access (user, timestamp, ownerId) |
| ADR-012-R4 | P0 | PropertyOwnership.SharePercent MUST be decimal(5,2). Sum of shares for a property MUST equal 100.00; validation returns 422 `shares_not_hundred` if not |
| ADR-012-R5 | P0 | GDPR export (art. 15) MUST include owner data (name, fiscal, IBAN plaintext in export JSON) |
| ADR-012-R6 | P0 | GDPR deletion (art. 17) MUST respect fiscal retention: if mandate ended < 10 years, retain fiscal data, set AnonymizedAt, log `fiscal_retention_active`. After retention: set Iban NULL, FiscalCode NULL |
| ADR-012-R7 | P1 | PropertyOwner entity MUST be separate from Party (Party for leases per existing code) |
| ADR-012-R8 | P1 | Mandate PDF upload stores URL in PropertyOwnerMandate.MandatePdfUrl, no e-signature validation (mandate signed outside CasaZen) |

## Consequences

**Follow-on work:**
- Backend: PropertyOwner entity + encryption, PropertyOwnership with shares validation, OwnerMandate entity, GDPR integration
- Frontend: Owner registry CRUD, ownership UI with shares validation, mandate configuration + PDF upload, IBAN masking + permission check
- Tests: Encryption proof test `PropertyOwner_Iban_EncryptedAtRest`, GDPR proof test `OwnerGdprDeletion_FiscalData_RetainedPerPolicy`

**Risks:**
- Key rotation requires migration job to re-encrypt all IBANs/fiscal with new purpose
- Float precision: shares stored as decimal(5,2), sum validation must be exact

**CI cost:** +5s for encryption and GDPR proof tests
