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

## Amendment 2026-11 (E10/F02/US02/T01): the registry facts of an owner

The owner import (NF-109, ONB-06) needs a place for the facts an agency knows about an owner before any accounting policy exists. They are columns of
`PropertyOwner`, all nullable (an owner of before the import has none):

| Column | Meaning | Stored |
|---|---|---|
| `ExternalKey` | the code the agency gives the owner; the first thing the import matches on | clear, filtered unique index `UIX_PropertyOwners_OrgId_ExternalKey` (`OrgId`, `ExternalKey`) where the key is set |
| `LegalName`, `EntityKind` (`NaturalPerson` 1, `LegalEntity` 2) | the name on the fiscal documents and the kind of the subject; `FirstName`/`LastName` stay the display name of the older screens | clear |
| `TaxResidenceCountry`, `SettlementCurrency` | ISO 3166-1 alpha-2 and ISO 4217 | clear |
| `BillingStreet`, `BillingCivic`, `BillingPostalCode`, `BillingCity`, `BillingProvince`, `BillingCountry` | the structured billing address | **encrypted** with `Casazen.PropertyOwner.Financial` since the amendment E13/F01/US01/T01 below (it was clear, like the e-mail and the phone) |
| `VatNumber`, `Bic` | VAT number and bank identifier | **encrypted** with `Casazen.PropertyOwner.Financial`, with `FiscalCode` and `Iban` (ADR-012-R1) |

- **Matching never uses a hash column.** The fiscal code and the VAT number are decrypted for the owners of one org (hundreds, not millions) and
  compared normalized in memory; no keyed index of identifiers exists, so there is no new secret and no second copy of an identifier. An e-mail address
  never identifies an owner (two owners may share one).
- **Nothing is overwritten.** The import fills an empty field and reports a different value as a conflict; the IBAN, the BIC, the fiscal code, the VAT
  number and the e-mail included.
- **`TaxRegime` is not inferred.** A new owner keeps the default; the regime and the withholding applicable to it are configured and approved by the
  accountant (NF-110), never concluded from the kind or the country here.
- **This is not the fiscal profile of E06/F05/US02/T01.** That task adds the effective-dated `OwnerFiscalProfile` and the approved rule sets; these
  columns are the registry facts the readiness checks (`registry`, `fiscalProfile`, `payment`) and the import fills, and the profile may take them over.
- The ownership period is also the mandate period of the agency (ADR-011: the end of the mandate ends the ownership). A stored `OwnerMandate` must stay
  inside the period of its ownership; the import refuses a row that would leave it outside.
- Log and answer rules of ADR-012-R2 hold for the new columns: the readiness answers codes and field names, the import answers codes and ids, the
  activity log gets counts and a hash of the file.

## Amendment 2026-11 (E06/F05/US02/T01): the structured fiscal profile of an owner

**Decision.** The fiscal identity an owner has for the statements, the payouts and the tax evidence is an effective-dated `OwnerFiscalProfile`, apart
from the registry facts of `PropertyOwner`, with the approved rule set it follows (`OwnerAccountingPolicy`). It is not an invoice-recipient profile:
CasaZen issues no agency-to-owner invoice (ADR-019 amended), so there is no SDI code or PEC.

| Column | Meaning | Storage |
|---|---|---|
| `LegalName`, `LegalAddress` | the name and the legal address the evidence carries | **encrypted** with `Casazen.OwnerFiscalProfile` |
| `FiscalCode`, `VatNumber`, `ForeignTaxId` | the identifiers the applicable reporting needs (an Italian fiscal code, a VAT number, a Swiss or other tax id) | **encrypted** with `Casazen.OwnerFiscalProfile` |
| `BankAccountReference` | the account the owner is paid on | **encrypted** with `Casazen.OwnerFiscalProfile` |
| `TaxResidenceCountry`, `TaxJurisdiction`, `SettlementCurrency`, the mandate role and dates, the recognition, expense and working-capital rules | the facts the rule set is chosen and applied with | clear |

- **Purpose of its own.** `Casazen.OwnerFiscalProfile` is never shared with `Casazen.PropertyOwner.Financial`: a payload of one never reads as the other.
- **Least privilege.** Only `agency.owner.financial.manage` (owner and administrators of the agency) reads the full values and changes a profile or a rule
  set; `agency.owner.view` gets the values masked (the last four characters). A read of the full values is logged with the ids of the owner and of the
  user, never a value (ADR-012-R2, R3).
- **No inference.** Neither the country nor the kind of the owner selects a liability: a profile names its jurisdiction and its rule set, the rule set
  must be approved by a qualified accountant and of the same jurisdiction, and anything missing blocks the financial operations with a precise code.
- **History.** Profiles never overlap and are never edited after the fact: a change is a new profile from a date; the documents keep the rule version
  they used.

## Amendment 2026-11 (E13/F01/US01/T01): the billing address of an owner is encrypted

**Decision.** The structured billing address of a `PropertyOwner` (`BillingStreet`, `BillingCivic`, `BillingPostalCode`, `BillingCity`, `BillingProvince`
and `BillingCountry`) is personal data of a person, and the same treatment is given to a company. It is **encrypted** with `Casazen.PropertyOwner.Financial`
(`EncryptedColumns`), like the IBAN and the fiscal code: this supersedes the «clear» of the amendment E10/F02/US02/T01 above for these columns, which the
decisions of 2026-11-14 for the owner invoice, the QR-bill and the pain.001.09 transfer (the address is the recipient and the creditor) require.

- **Columns.** They are `text` (a payload is longer than the value); the lengths of the clear text are checked on input by the import and by
  `PUT api/owners/{id}/address`. The values stored in clear before are encrypted by the startup step (`EncryptedColumns.EncryptLegacyPlaintextAsync`).
- **Least privilege.** `GET`/`PUT api/owners/{id}/address` need `agency.owner.financial.read` / `agency.owner.financial.manage` and an owner inside the
  reach of the caller; the address is in no list and no log (ADR-012-R2: the logs carry the ids of the owner and of the user).
- **Never searched.** The address is read through EF only; no query filters on it.
