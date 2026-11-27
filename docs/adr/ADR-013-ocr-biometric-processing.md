# ADR-013: OCR and Biometric Processing

**Status:** Accepted (amended 2026-11-27)  
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

## Amendment 2026-11: self-hosted identity verification

**Date:** 2026-11-27 (run R20261103-1506, task E70/F01/US01/T01) · **Informs:** NF-188 to NF-199 (epics E70-E74)

**Why:** the identity check of the guest at the check-in becomes entirely self-hosted and policy-driven (specification `analisi-identificazione-self-hosted.md`, product decisions D1-D14 of 2026-11-27): no image, video frame or face representation goes to an external inference service, a versioned and frozen policy decides, and a person decides wherever the automation is not qualified. Before this amendment the presence is two still frames with a challenge of one bit (R1, R2, R12), the outcome comes from the thresholds of the organization (R4, R5), nothing is kept for the reviewer (R2), and the prefill `POST /api/public/checkin/{token}/read-document` may send the photo of the document to an AI provider.

**Decision delta:**

- **Three flags, all off by default** (`Casazen.Core/Features/FeatureFlags.cs`): `CheckInIdentitySelfHosted` (the self-hosted flow, only together with `CheckInIdentityVerification`), `CheckInIdentityAutoApproval` (the platform-wide permission to approve automatically inside a qualified segment) and `CheckInIdentityReviewEvidence` (the minimal encrypted evidence for the reviewer). With them off the path of R1-R16 is unchanged; no task of the plan turns one on or edits a legal text.
- **Presence by challenge and frame sequence** (R17, R22): the server issues an unpredictable challenge of several moves (more than one bit), with an expiry, bound to the session and the attempt; the browser sends a dense sequence of timestamped JPEG frames; the server validates it (one face per frame, quality, plausible timing, continuity, the moves in their order), chooses the frames itself and runs MiniFASNetV2 on every frame it chose. No video is decoded on the server and no native dependency is added.
- **Decision by a versioned policy** (R18, R19): one policy file holds every threshold and the aggregation rule, pinned by a canonical SHA-256; presence and similarity are independent conditions (no compensation); the closure is atomic and idempotent (header `Idempotency-Key`). Automatic approval only in a segment (property × format × channel) qualified for the policy and the weights in force, with `CheckInIdentityAutoApproval` on; everywhere else the decision of the policy is the shadow decision of the log row and a person decides. A change of the policy or of the weights brings a segment back to observation; a segment is suspended by configuration.
- **Evidence for the reviewer** (R20): the one exception to R2, only with `CheckInIdentityReviewEvidence` on.
- **No outflow** (R21): the prefill reads locally, whatever the flags; architecture tests guard the whole identity path.
- **Configuration**: section `IdentityVerification` (`Casazen.Core/Options/IdentityVerificationOptions.cs`): the policy file and its hash, the folder of the formats, the transport limits of the presence, the evidence and the retention. They are limits, never decision thresholds.

| ID | Priority | Requirement |
|---|---|---|
| ADR-013-R17 | P0 | The presence of the self-hosted flow MUST be a server challenge (several random moves, more than one bit, expiring, bound to the session and the attempt) plus a dense timestamped frame sequence sent to `POST /api/public/checkin/{token}/presence`, validated and decided synchronously in the request (not Hangfire) within `IdentityVerification:Presence:SequenceTimeoutMs`; with `CheckInIdentitySelfHosted` on, the legacy `selfie` routes MUST refuse (`presence_sequence_required`) |
| ADR-013-R18 | P0 | Every decision of the self-hosted flow MUST come from a versioned policy file pinned by SHA-256 (`IdentityVerification:PolicyFile`, `IdentityVerification:PolicySha256`) whose outcomes are automatic approval, one guided retry for a correctable defect, or review with reasons, never an automatic reject; a model fault, a timeout, or a policy file that is missing or does not match its hash MUST never approve; R4 and R5 stay for the legacy path |
| ADR-013-R19 | P0 | Automatic approval MUST happen only in a segment (property × format × channel) qualified for the active policy version, policy hash and weights hash, with `CheckInIdentityAutoApproval` on; otherwise the segment is in observation: the decision of the policy MUST be recorded as the shadow decision of the log row and a person decides |
| ADR-013-R20 | P0 | Exception to R2, only with `CheckInIdentityReviewEvidence` on: the crop of the portrait of the document and at most `IdentityVerification:Evidence:MaxFrames` frames chosen by the server MAY be kept for the reviewer, encrypted in the private storage, with a time to live (`IdentityVerification:Evidence:RetentionHours`), read only with the verification permission on a reachable property and audited, deleted at the decision or at the expiry; never an embedding, never in a log or a queue, never for a Ticino property (R6) |
| ADR-013-R21 | P0 | No image, OCR value or face representation of the identity path MUST reach an inference provider, whatever the flags: `POST /api/public/checkin/{token}/read-document` MUST read locally, and architecture tests MUST forbid AI providers and HTTP clients in the classes of the identity path (flow, pipeline, OCR, readers, the namespace `Casazen.Infrastructure.Identity`) |
| ADR-013-R22 | P1 | The frames of a presence sequence MUST be JPEG with a longest side of at most `IdentityVerification:Presence:MaxFrameSide` pixels; R12 stays for the legacy selfie |

**Replaces:** none. R1, R2, R4, R5 and R12 stay in force for the legacy path (`CheckInIdentitySelfHosted` off); R20 is the one exception to R2 and R6 applies to it; R17 and R22 apply to the self-hosted path only.

**Consequences delta:**
- Follow-on work: the epics E70-E74 (the session bound to one guest, the registry of the formats with the Italian electronic identity card first, the guided capture and the local reading by zones, the presence, the match on frames chosen by the server, the policy and the segments, the evidence with its time to live and its purge, the counters and the export of the pilot, the offline qualification toolkit), all behind the flags above.
- Schema: one migration for the whole plan, `AddSelfHostedIdentityVerification` (E70/F01/US01/T02): the identity columns of `GuestCheckInSessions` and `BiometricVerificationLogs`, and the tables `IdentitySegmentSettings` and `IdentityReviewEvidences`; keys, hashes, codes and times only, never an image or a vector.
- Pre-launch gates (manual, NF-199): real data collected with explicit rights and the qualification by a separate QA, the pilot of 500 check-ins with a parallel human verification, the final numeric criteria, the DPIA and the legal texts, the licences of the weights, real devices, hosting with the network egress disabled.
- Risk: a generic browser cannot prove the physical origin of the video; it is qualified before any sale (NF-192).
