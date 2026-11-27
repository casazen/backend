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

## Amendment 2026-11 (E06/F01/US02/T05): no agency-to-owner invoice in CasaZen

**Status of the owner-invoice decision: superseded.** CasaZen owner accounting produces a rendiconto (a non-fiscal, versioned management report of the
owner ledger) and the payment statement, not an agency-to-owner tax invoice. The guest fiscal issuance of the direct bookings (FiscalDocuments,
E06/F01/US01) is unchanged.

- **Retired generator.** `AgencyInvoiceService.GenerateInvoiceAsync` and `POST api/agency-invoices/generate` answer 410
  `owner_invoice_issuance_removed`. No agency-to-owner invoice, fiscal number or FatturaPA XML is created any more (AC-NF110-6). R4 (numbering) and
  R6 (XML) no longer apply to owners.
- **History.** The stored invoices are not deleted: their XML and courtesy copy stay readable, and every one is marked
  `ComplianceStatus = legacy_unverified` (no independently verifiable delivery or schema evidence is stored); none is relabelled as compliant.
- **External documents.** When the agency invoices its fee, its own external fiscal or accounting system issues the invoice. CasaZen links it to the
  statement version (`OwnerExternalDocumentReference`: provider, immutable external id, number and date, an https link and the SHA-256 of the issued
  file, `entered` or `verified`, author and time). The link has no amount, VAT, numbering or submission state of CasaZen and changes nothing of the
  statement; only `agency.owner.financial.manage` adds, verifies or removes it; a removal keeps the row with its reason; the owners read it in the
  portal only once the statement is published.
- **Rendiconto.** The statement files print «Rendiconto gestionale - non è una fattura», the version, the period, the totals, the payouts and the
  withholding evidence; they carry no fiscal series or number.

## Amendment 2026-11-14 (E11/F01/US01/T01): CasaZen issues again, also agency-to-owner invoices with lines (E14); the port serves every issuer

**The amendment of 2026-11 (above) is superseded** by the decisions of the product owner of 2026-11-14 (P1, P2; `fatturazione/analisi-tecnica.md` §5).

- **CasaZen is always the issuer.** It issues the document of every active booking of every source (direct, manual, the portals through Octorate) and,
  with epic E14, the invoice of the property manager to each owner, with lines (commission, services and recharges with VAT, advanced expenses outside
  the VAT), made from a version of the owner statement: a `FiscalDocument` with the purpose `OwnerInvoice`, the same engine, numbering, PDF and XML. The
  rendiconto stays a non-fiscal management report; the invoice to the owner is a document of its own, linked to the version.
- **Nobody issues outside CasaZen.** The issuer modes `Octorate` and `Accountant` are retired (kept as numbers for the stored rows, refused by the API);
  the accountant receives the exports only. A document Octorate issued is imported read-only and never locks the issuer of a booking; it is a document
  issued elsewhere all the same, so CasaZen issues for every active booking except one that already has such a document (409): never two documents for one
  stay.
- **The port serves every issuer.** `ISdiEInvoiceProvider` is the one port toward the SDI for the documents of every issuer CasaZen issues for (the
  organization today, the owners that issue in Italy with E17); the concrete adapter stays behind its own flag (`SdiSubmission`, E17) and, without
  credentials, a document stays `to_send`.
- **The issuer as it was.** Every issued document keeps the snapshot of its issuer and the version of the tax rule it was made with; Switzerland is a
  country of the issuer like Italy (MWSTG art. 26 document, no XML, no SDI, no stamp duty).
