# ADR-009: ConcordatoFacile Document Extraction Pipeline

**Status:** Accepted (amended 2026-10-24)  
**Date:** 2026-10-23  
**Issue:** none (run R20261023-1107)  
**Informs:** NF-07 (ConcordatoFacile document extraction)

## Context

When landlords upload lease contract documents (PDF, photos), CasaZen extracts data (parties, rent, dates) via OCR to pre-populate lease fields. Multi-page contracts require async extraction to avoid blocking upload.

**Requirements** (from `kb/intake/requirements.md` NF-07):
- OCR extraction from contract, attestation, and registration documents
- Extraction populates LeaseContract fields (parties, addresses, rent, deposit, dates)
- High-confidence values auto-populated; uncertain values flagged for manual review
- Do not silently overwrite confirmed user-entered data
- Duplicate documents (same hash) do not re-run extraction

**HITL answers:**
- Q-A09: Asynchronous Hangfire job
- Q-A10: Verify ApeDocumentInspector multi-page support first, Tesseract if unsupported
- Q-A11 (default): LeaseDocument columns for metadata
- Q-A12 (default): Hardcoded coherence validation rules

**Existing code** (from `kb/gap/gap-analysis.md`):
- `IApeDocumentInspector` (`work/base/backend/Casazen.Core/Services/IApeDocumentInspector.cs:19-24`) for APE OCR
- `LeaseDocumentService.UploadAsync` for document upload
- Hangfire for background jobs

**Repo scope:** Backend (extraction job, OCR integration), Frontend (extraction status display)

**Mobile:** NOT-APPLICABLE - mobile has no document upload or lease management

## Decision

### Asynchronous Extraction with Hangfire

`LeaseDocumentService.UploadAsync` enqueues `ExtractLeaseDocumentDataJob` after upload completes:

- Upload returns immediately with `ExtractionStatus=Pending`
- User sees "Elaborazione in corso" status
- Job processes in background (may take 10-30s for multi-page PDF)

### OCR Library Strategy

Per Q-A10 answer:
1. Verify existing `ApeDocumentInspector` supports multi-page PDFs
2. If unsupported, replace with Tesseract:
   - Add `Tesseract` NuGet package
   - Add `ita.traineddata` language file (~10MB)
   - Dockerfile: `apt-get install tesseract-ocr tesseract-ocr-ita`

### Extraction Metadata in LeaseDocument Columns

Per Q-A11 default, add columns to `LeaseDocument` (simpler than separate entity):
- `ExtractionStatus` enum: NotStarted, Processing, Completed, NeedsReview, Failed
- `ExtractionStartedAt`, `ExtractionCompletedAt` datetime nullable
- `ExtractionResult` jsonb: `{fields: [{name, value, confidence}], conflicts: [{field, expected, extracted, reason}]}`
- `ExtractionError` text nullable
- `DocumentHash` varchar(64) SHA-256 for duplicate detection

### Coherence Validation Rules

Per Q-A12 default, hardcoded domain logic rules:
1. Contract has two parties with roles Landlord and Tenant
2. StartDate < EndDate
3. Extracted rent within +/-50% of property.MonthlyRent if set
4. Extracted property address contains property.City

Conflicts flagged in `ExtractionResult.conflicts`, document set to `NeedsReview`.

### Field Population Policy

- High-confidence extraction (all fields >80%): `ExtractionStatus=Completed`, fields populated
- Uncertain values or conflicts: `ExtractionStatus=NeedsReview`, fields NOT auto-populated
- Confirmed user-entered data: extraction compares, mismatch flags conflict, does NOT silently overwrite

### Frontend Extraction Status

- Document list shows extraction status badge per document
- Badge states: Pending="Caricato" (gray), Processing="Elaborazione in corso" (yellow), Completed="Dati acquisiti" (green), NeedsReview="Da verificare manualmente" (orange)
- Lease detail populated fields show source indicator: "Da contratto caricato"

### Rejected Alternatives

- **Synchronous extraction (Option B)**: Blocks upload for 10-30s, poor UX
- **Separate LeaseDocumentExtraction entity (Option C)**: More complex, single extraction per document sufficient

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-009-R1 | P0 | `LeaseDocumentService.UploadAsync` MUST enqueue Hangfire job `ExtractLeaseDocumentDataJob.EnqueueAsync(documentId)` after document upload completes, set `LeaseDocument.ExtractionStatus=Pending`, return upload response immediately without waiting for extraction |
| ADR-009-R2 | P0 | `ExtractLeaseDocumentDataJob` MUST use `[DisableConcurrentExecution(timeoutInSeconds: 300)]` attribute to prevent duplicate extraction jobs for same document; MUST verify `LeaseContract.OrgId` matches upload context (tenant isolation) |
| ADR-009-R3 | P0 | LeaseDocument migration MUST add columns: `ExtractionStatus` enum (NotStarted/Processing/Completed/NeedsReview/Failed) default NotStarted, `ExtractionStartedAt` datetime nullable, `ExtractionCompletedAt` datetime nullable, `ExtractionResult` jsonb nullable, `ExtractionError` text nullable, `DocumentHash` varchar(64) SHA-256 of file content |
| ADR-009-R4 | P0 | OCR library MUST support multi-page PDFs and extract text from all pages; PROOF TEST with multi-page test PDF verifies content extracted from pages 1, 3, 5 |
| ADR-009-R5 | P0 | Extraction result MUST populate `ExtractionResult` jsonb with structure: `{fields: [{name, value, confidence}], conflicts: [{field, expected, extracted, reason}]}`; set `ExtractionStatus=Completed` if all fields >80% confidence, else `ExtractionStatus=NeedsReview` |
| ADR-009-R6 | P0 | Extraction MUST populate LeaseContract fields (parties, rent, deposit, dates) with high-confidence values; PROOF TEST with real contract PDF containing "Mario Rossi TEST" landlord, "Giulia Bianchi TEST" tenant, rent 1200 EUR verifies fields populated |
| ADR-009-R7 | P0 | Duplicate detection MUST query `SELECT Id FROM LeaseDocuments WHERE DocumentHash = @hash AND LeaseContractId = @leaseId` before extraction; if exists, skip extraction and set `ExtractionStatus=Skipped` referencing original document |
| ADR-009-R8 | P0 | Coherence validation MUST check: (1) contract has parties with roles Landlord and Tenant, (2) StartDate < EndDate, (3) extracted rent within +/-50% of property.MonthlyRent if set, (4) extracted address contains property.City; conflicts flagged in ExtractionResult.conflicts |
| ADR-009-R9 | P0 | Extraction MUST NOT silently overwrite confirmed user data: if LeaseContract field already populated, compare extracted vs existing; if mismatch, flag conflict in ExtractionResult.conflicts, set ExtractionStatus=NeedsReview, do NOT overwrite |
| ADR-009-R10 | P0 | OCR extraction errors MUST NOT log document content, extracted names, tax codes, or addresses; log leaseId, documentId, error type only |
| ADR-009-R11 | P0 | LeaseDocument extraction metadata MUST be included in GDPR data export (GdprDataExportService pattern) |
| ADR-009-R12 | P0 | `POST /api/leases/{leaseId}/documents` upload MUST authorize with LeaseCreate policy |
| ADR-009-R13 | P0 | If ApeDocumentInspector does not support multi-page PDFs, MUST replace with Tesseract: add `Tesseract` NuGet, `ita.traineddata` language file, Dockerfile `apt-get install tesseract-ocr tesseract-ocr-ita` |
| ADR-009-R14 | P0 | Photo merge MUST accept multiple photo uploads, merge to single PDF, OCR extract from merged PDF pages, store merged PDF as LeaseDocument |
| ADR-009-R15 | P0 | Frontend LeaseDocument type MUST extend with `extractionStatus`, `extractedAt`, `extractionError` |
| ADR-009-R16 | P0 | Frontend document list MUST display extraction status badge: Pending="Caricato" (gray), Processing="Elaborazione" (yellow spinner), Completed="Dati acquisiti" (green), NeedsReview="Da verificare" (orange) |
| ADR-009-R17 | P0 | Frontend lease detail populated fields MUST show source indicator "Da contratto caricato" / "Da attestazione" / "Da registrazione" |
| ADR-009-R18 | P1 | Manual review endpoints: `POST /api/leases/{leaseId}/documents/{documentId}/confirm-extraction` accepts confirmed fields, populates LeaseContract, sets ExtractionStatus=Completed; `POST .../retry-extraction` re-enqueues job |
| ADR-009-R19 | P1 | i18n keys: `leases.extraction.status.{uploaded, processing, completed, needsReview}`, `leases.extraction.{sourceContract, sourceAttestation, sourceRegistration, conflictWarning}` |
| ADR-009-R20 | P1 | data-testid: `doc-extraction-status`, `lease-field-source`, `extraction-conflict` |

## Consequences

**Follow-on work:**
- Backend: `ExtractLeaseDocumentDataJob` Hangfire job
- Backend: OCR integration (verify ApeDocumentInspector, add Tesseract if needed)
- Backend: LeaseDocument migration with extraction columns
- Backend: Coherence validation rules
- Frontend: Extraction status badges, source indicators
- Tests: PROOF TEST for data rule (personal data extraction)
- Demo: Add test contract PDF with "DOCUMENTO DI TEST" marker per demo-seed.md

**Risks:**
- ApeDocumentInspector multi-page support unknown (R13 verify first)
- Tesseract accuracy lower than commercial OCR; NeedsReview status mitigates
- Extraction timeout: Hangfire job 300s limit (5 min), sufficient for 10-page contract

**GDPR:** OCR extracts personal data (names, tax codes, addresses); 10-year fiscal retention applies; extraction metadata included in GDPR export

**CI cost:** +15s for PROOF tests with real PDF

**Cross-repo contract:**
- Backend: Document upload returns `{id, extractionStatus: 'Pending'}`
- Backend: Document list includes extraction metadata
- Frontend: Displays status badges, refetches on page mount

## Amendment 2026-10-24 (run R20261024-1925)

**Why:** NF-10 extends document extraction from lease contracts to ID documents (passport, ID card, residence permit) for guest check-in. MRZ reading and check digit validation required.

**Decision delta:**

### ID Document Support

Extend OCR pipeline for identity documents:
- Passport (TD-3 MRZ)
- ID card (TD-1 MRZ)
- Residence permit

### MRZ Reading

Machine Readable Zone extraction per ICAO 9303:
- Use PassportEye (Apache-2.0) or Tesseract with MRZ-specific patterns
- Extract: Name, Surname, DOB, Nationality, Document Number, Expiration Date

### Check Digit Validation

ICAO 9303 mod-10 weighted sum:
- Validate document number check digit
- Validate DOB check digit
- Validate expiration check digit
- Flag invalid documents for manual review

### DocumentImage Entity

For guest check-in documents:

```csharp
public class DocumentImage : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid GuestId { get; set; }
    public Guid BookingId { get; set; }
    public string FrontImageUrl { get; set; }  // Encrypted
    public string BackImageUrl { get; set; }  // Encrypted, nullable
    public DateTime UploadedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
```

### Ticino Zero Retention

Per ADR-013, Ticino properties (Property.CantonCode == "TI"):
- Process in-memory only
- NEVER write DocumentImage row
- Comply with RL 942.120 art. 6

### Alloggiati Receipt Deletion Trigger

When Alloggiati receipt received (ADR-015 integration):
- Enqueue `DocumentDeletionJob(documentId)`
- Delete DocumentImage row + object storage
- Per D-07 requirement

### Extraction Populates StayGuest

Extracted data populates StayGuest fields:
- DocumentType, DocumentNumber, IssuePlace, Nationality, DOB, ExpirationDate

| ID | Priority | Requirement |
|---|---|---|
| ADR-009-R21 | P0 | DocumentImage table (GuestId, BookingId, FrontImageUrl encrypted, BackImageUrl encrypted nullable, UploadedAt, ProcessedAt, DeletedAt, ITenantOwned) for ID documents |
| ADR-009-R22 | P0 | MRZ extraction MUST support passport (TD-3), ID card (TD-1), residence permit, output Name/Surname/DOB/Nationality/DocumentNumber/ExpirationDate |
| ADR-009-R23 | P0 | Check digit validation MUST use ICAO 9303 algorithm (mod-10 weighted sum), flag invalid documents for manual review |
| ADR-009-R24 | P0 | Ticino properties (Property.CantonCode == "TI") MUST process in-memory only, NEVER write DocumentImage row per RL 942.120 art. 6 |
| ADR-009-R25 | P0 | Alloggiati receipt arrival MUST enqueue DocumentDeletionJob, delete DocumentImage row + object storage per D-07 |
| ADR-009-R26 | P0 | OCR library MUST be PassportEye (Apache-2.0) or Tesseract (Apache-2.0), license verified before use |
| ADR-009-R27 | P0 | Extraction result MUST populate StayGuest fields (DocumentType, DocumentNumber, IssuePlace, Nationality, DOB, ExpirationDate) |
| ADR-009-R28 | P1 | Manual entry always possible (guest can skip OCR, fill form directly) |

**Consequences delta:**
- Backend: DocumentImage entity + encryption, MRZ extraction service, check digit validation, Ticino in-memory processing, Alloggiati receipt deletion trigger
- Frontend: Document upload UI in check-in, extracted data review screen
- Tests: MRZ proof test with ICAO 9303 test vectors, Ticino in-memory proof test
- License: PassportEye Apache-2.0, Tesseract Apache-2.0 verified
