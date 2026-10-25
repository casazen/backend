# ADR-019: Agency Invoice Provider Generalization

**Status:** Accepted  
**Date:** 2026-10-24  
**Issue:** none (run R20261024-1925)  
**Informs:** NF-20

## Context

Agencies need to issue invoices (FatturaPA) to property owners for their monthly commission. The existing ISdiEInvoiceProvider is CasaZen-platform-as-issuer only and needs generalization.

**Existing code** (from `kb/gap/gap-analysis.md`):
- `ISdiEInvoiceProvider` at `Casazen.Core/Services/ISdiEInvoiceProvider.cs` for platform invoices
- `UnconfiguredSdiEInvoiceProvider` stub (no provider configured in phase 1)
- FatturaPA XML generation exists for platform billing

**Repo scope:** Backend (invoice entity, provider generalization, FatturaPA), Frontend (invoice CRUD, download)

## Decision

### ISdiEInvoiceProvider Generalization

Extend interface to accept issuer context:

```csharp
public enum InvoiceIssuerContext { Platform, Agency }

public interface ISdiEInvoiceProvider
{
    Task<InvoiceResult> IssueInvoiceAsync(
        InvoiceIssuerContext context,
        Guid issuerOrgId,  // NEW: agency org when context=Agency
        InvoiceData data);
}
```

Platform invoicing unchanged (CasaZen platform org as issuer).

### AgencyInvoice Entity

```csharp
public class AgencyInvoice : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }  // Issuer agency
    public Guid PropertyOwnerId { get; set; }  // Recipient
    public int Year { get; set; }
    public int Month { get; set; }
    public string InvoiceNumber { get; set; }
    public string Series { get; set; }
    public decimal Subtotal { get; set; }
    public decimal VatRate { get; set; }
    public string VatExemptionCode { get; set; }
    public decimal StampDuty { get; set; }
    public decimal Total { get; set; }
    public string FatturaPaXmlUrl { get; set; }
    public string PdfUrl { get; set; }
    public DateTime IssuedAt { get; set; }
    public AgencyInvoiceStatus Status { get; set; }  // ToBeIssued | Issued
}

public enum AgencyInvoiceStatus { ToBeIssued, Issued }
```

### Invoice Numbering

Per-agency numbering with no gaps or duplicates:
- Row lock on `AgencyInvoiceConfig.NextInvoiceNumber` during creation
- Increment, commit atomically
- Unique index `(OrgId, Series, InvoiceNumber)`

### Structured Address on Org

FatturaPA requires structured address:

```csharp
// Add to Org entity
public string StructuredAddressStreet { get; set; }
public string StructuredAddressNumber { get; set; }
public string StructuredAddressCity { get; set; }
public string StructuredAddressPostalCode { get; set; }
public string StructuredAddressProvince { get; set; }
public string StructuredAddressCountry { get; set; }
public string TaxRegime { get; set; }  // RF01-RF19
```

Per Q-A44 answer, nullable until manually entered. Invoice creation blocked if null.

### Tax Regime

Per Q-A43 answer, RF01-RF19 values per FatturaPA specs:
- RF01 Ordinario
- RF02 Contribuenti minimi
- RF04 Agricoltura
- ... (19 values total)

### Single Supplier Per Invoice

Per art. 21 DPR 633/72, one invoice per supplier:
- Validation checks all statement lines reference same PropertyOwnerId
- Return 422 `invoice_multiple_suppliers` if not

### FatturaPA XSD Validation

Generated XML MUST validate against official AdE XSD before save.

### Phase 1: Manual Issuance

Per existing `UnconfiguredSdiEInvoiceProvider`:
- Status = ToBeIssued
- No real SDI transmission
- Agency downloads XML/PDF, submits manually

### Swiss Agency

Per D-13, Swiss agency (Org.Country = "CH"):
- Skip SDI transmission
- Status remains ToBeIssued
- Manual issuance

### Rejected Alternatives

- **Separate invoice provider per context:** Over-engineering; single provider with context parameter
- **Auto-parse RegisteredOffice:** Data quality risk; nullable structured address + admin form safer

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-019-R1 | P0 | ISdiEInvoiceProvider.IssueInvoiceAsync MUST accept IssuerContext enum (Platform | Agency) parameter |
| ADR-019-R2 | P0 | AgencyInvoice table (OrgId issuer, OwnerId recipient, Month, InvoiceNumber, Series, Subtotal, VatRate, VatExemptionCode, StampDuty, Total, FatturaPaXmlUrl, PdfUrl, IssuedAt, Status, ITenantOwned) |
| ADR-019-R3 | P0 | Org MUST add columns StructuredAddressStreet, StructuredAddressNumber, StructuredAddressCity, StructuredAddressPostalCode, StructuredAddressProvince, StructuredAddressCountry, TaxRegime (RF01-RF19) |
| ADR-019-R4 | P0 | Invoice numbering MUST be per-agency (scoped by OrgId issuer), series configurable per agency, no gaps/duplicates enforced by unique index and row lock |
| ADR-019-R5 | P0 | Single supplier validation MUST check all statement lines reference same PropertyOwnerId, else 422 `invoice_multiple_suppliers` |
| ADR-019-R6 | P0 | FatturaPA XML MUST validate against official AdE XSD before save |
| ADR-019-R7 | P0 | Phase 1: UnconfiguredSdiEInvoiceProvider MUST return Status = ToBeIssued (no real SDI transmission) |
| ADR-019-R8 | P0 | Swiss agency (Org.Country = "CH") MUST skip SDI transmission, status remains ToBeIssued (manual issuance per D-13) |
| ADR-019-R9 | P0 | Invoice creation MUST require structured address on Org; missing fields = 422 `structured_address_required` |
| ADR-019-R10 | P1 | Structured address migration for existing orgs: leave nullable, admin fills before first invoice |

## Consequences

**Follow-on work:**
- Backend: ISdiEInvoiceProvider generalization, AgencyInvoice entity + numbering, FatturaPA XML builder reuse, Org structured address fields, PDF generation, owner portal integration
- Frontend: Agency invoice creation UI, list and detail, download buttons, manual issue action, org settings structured address + tax regime inputs, numbering/series config
- Tests: FatturaPA XSD validation proof test, numbering without gaps proof test

**Future work:**
- Real SDI provider integration (phase 2)

**Risks:**
- ISdiEInvoiceProvider interface change must be backward compatible for platform invoices
- Structured address migration: existing orgs have RegisteredOffice (single line), structured fields null until filled
- Tax regime values require legal review

**CI cost:** +10s for XSD validation and numbering proof tests
