# ADR-013: OCR and Biometric Processing

**Status:** Accepted  
**Date:** 2026-10-24  
**Issue:** none (run R20261024-1925)  
**Informs:** NF-10, NF-11

## Context

Guest check-in requires document scanning (passport, ID card, residence permit) with MRZ/OCR extraction and optional biometric verification (selfie vs document photo). Ticino cantonal law RL 942.120 art. 6 requires zero retention for Ticino properties.

**Operator decision (Q-A13):** Server processing chosen for full control, immediate deletion, and server-side validation.

**Existing code** (from `kb/gap/gap-analysis.md`):
- `ExtractLeaseDocumentDataJob` for lease OCR (ADR-009)
- No biometric infrastructure exists
- GuestCheckInSession exists for check-in flow

**Repo scope:** Backend (OCR/biometric services, model hosting), Frontend (document upload, selfie capture), Mobile (verification status display only)

## Decision

### Server-Side Processing

Per Q-A13 answer, all OCR and biometric processing happens on the backend:

- Frontend uploads images via `POST /api/public/checkin/{token}/document` and `POST /api/public/checkin/{token}/selfie`
- Backend processes synchronously and returns results
- Frontend displays outcome

### Synchronous Biometric Processing

Per O-adr-lead-be-2 resolution, biometric verification MUST run synchronously in the request (NOT via Hangfire):

- Hangfire job args stored in DB would violate immediate deletion guarantee
- POST /api/public/checkin/{token}/selfie returns outcome within 3s
- Only outcome/log written to database; images deleted before response

### Model Selection

- **Face Detection:** YuNet (MIT license, ~74ms CPU)
- **Anti-spoofing:** MiniFASNet (Apache-2.0)
- **Face Recognition:** AuraFace-v1 or SFace (Apache-2.0)
- **MRZ/OCR:** PassportEye (Apache-2.0) or Tesseract (Apache-2.0)

Model weights: if <100MB, commit to git; if >100MB, external S3 with SHA-256 hash verification on startup.

### Three-Level Verification Outcome

```csharp
public enum BiometricOutcome
{
    Accept,      // similarity >= acceptThreshold
    Escalate,    // rejectThreshold < similarity < acceptThreshold
    Reject       // similarity <= rejectThreshold
}
```

### Max Attempts

Per Q-A16 answer, maximum 3 biometric attempts:
- GuestCheckInSession tracks `BiometricAttempts` and `BiometricOutcome`
- After 3 failed attempts, auto-escalate to operator queue

### Immediate Deletion Guarantee

Selfie image, cropped face, and face vectors:
- In-memory only during processing
- Deleted in `finally` block before response
- NEVER written to disk, logs, queue, or database
- NEVER passed to Hangfire jobs

### Ticino Zero Retention

Properties with `Property.CantonCode == "TI"`:
- Document images processed in-memory only
- NEVER write DocumentImage row
- NEVER write to object storage
- Comply with RL 942.120 art. 6

### Document Image Retention (Non-Ticino)

For non-Ticino properties:
- DocumentImage stored encrypted (GuestDocumentPurpose)
- Deleted when Alloggiati receipt received (NF-14 integration)
- Retention per GDPR config `Gdpr:Retention:DocumentScans`

### Biometric Consent

Per GDPR art. 7:
- Consent MUST NOT be mandatory
- Refusal escalates to operator queue (NF-12)
- Minor (DOB < 18 years) excluded from biometric

### Frontend Selfie Capture

- Camera only (no file upload per NF-11 AC-NF11-4)
- `navigator.mediaDevices.getUserMedia({ video: { facingMode: 'user' } })`
- Resize to max 1920px width before upload
- On camera permission denied: fallback to file picker

### Rejected Alternatives

- **Browser processing (WASM/JS):** Can't verify server-side, rejected per NF-11 AC-NF11-5
- **Separate microservice:** Deployment complexity, unnecessary for phase 1
- **Hangfire for biometric:** Violates immediate deletion (job args in DB)

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-013-R1 | P0 | Biometric verification MUST run synchronously in POST /api/public/checkin/{token}/selfie (not Hangfire), return outcome within 3s |
| ADR-013-R2 | P0 | Selfie image/crop/embedding MUST be in-memory only, deleted in `finally` block before response, NEVER passed to Hangfire, NEVER in logs |
| ADR-013-R3 | P0 | BiometricVerificationLog table (BookingId, GuestId, Outcome enum, Attempts, ModelName, ModelVersion, Thresholds JSON, NO images) MUST be ITenantOwned |
| ADR-013-R4 | P0 | BiometricOutcome enum MUST have Accept (similarity >= acceptThreshold), Escalate (rejectThreshold < similarity < acceptThreshold), Reject (similarity <= rejectThreshold) |
| ADR-013-R5 | P0 | GuestCheckInSession MUST add BiometricAttempts int, BiometricOutcome enum nullable, max 3 attempts then auto-escalate |
| ADR-013-R6 | P0 | Ticino properties (Property.CantonCode == "TI") MUST NOT write DocumentImage row, MUST process in-memory only per RL 942.120 art. 6 |
| ADR-013-R7 | P0 | Alloggiati receipt arrival MUST enqueue DocumentDeletionJob, delete DocumentImage row + object storage |
| ADR-013-R8 | P0 | Model files >100MB MUST be external (presigned URL + SHA-256 hash verification on startup), <=100MB MAY be in repo |
| ADR-013-R9 | P0 | Biometric consent MUST NOT be mandatory (GDPR art. 7). Refusal escalates to operator queue. Minor (DOB < 18) excluded |
| ADR-013-R10 | P0 | MRZ extraction MUST support passport (TD-3), ID card (TD-1), residence permit with ICAO 9303 check digit validation |
| ADR-013-R11 | P0 | Selfie capture component MUST use `navigator.mediaDevices.getUserMedia({ video: { facingMode: 'user' } })` (camera only, no file input) |
| ADR-013-R12 | P0 | Frontend MUST resize selfie to max 1920px width, 80% JPEG quality before upload |
| ADR-013-R13 | P1 | BiometricSettings table (OrgId, Enabled, AcceptThreshold, EscalateThreshold, MaxAttempts) MUST default Agency tier Enabled=true, B2C Enabled=false |
| ADR-013-R14 | P1 | Model provenance table (ModelName, Version, License, TrainingDataProvenance, WeightHash) for audit |
| ADR-013-R15 | P1 | Ticino property check-in MUST show notice "Il documento non sara archiviato" |
| ADR-013-R16 | P1 | Mobile app booking detail MUST show verification status badge (passed=success, escalated=warning, rejected=danger) |

## Consequences

**Follow-on work:**
- Backend: OCR/MRZ service + encryption, biometric service + model loading, immediate deletion triggers + Ticino in-memory, verification queue integration
- Frontend: Document upload UI, selfie capture UI + camera permission, biometric consent screen, extracted data review screen
- Mobile: Verification status display in booking detail
- Tests: Privacy proof tests `BiometricSelfie_ImmediatelyDeleted_NeverStored`, `TicinoProperty_DocumentImage_InMemoryOnly`

**Pre-launch gates:**
- DPIA with legal counsel
- Threshold calibration on real data
- Model license legal review (YuNet training data provenance)

**Risks:**
- Model weight size (git bloat if >100MB)
- Ticino zero retention requires Property.CantonCode check in every path
- YuNet training data provenance not verified (DPIA notes risk)

**CI cost:** +15s for proof tests with real images
