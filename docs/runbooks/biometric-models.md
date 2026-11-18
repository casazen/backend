# Runbook: biometric models (delivery kit, health and status)

E22/F01/US01/T01 (operations plan of 2026-11-14, decision O7). ADR-013 (R8: models pinned by SHA-256, R14: provenance), NF-24, NF-50. What the
models do, and what the identity verification does with them: [`TECHNICAL.md`](../TECHNICAL.md) (Identity verification models, Identity document
reading). The variables: [`deploy-checklist.md`](deploy-checklist.md) § 2.10.

## Bundled models

The API publishes YuNet 2023mar for detection, MiniFASNetV2 for liveness, SFace 2021dec for comparison and OCR-B for machine-readable document lines. The manifest records the exact source revisions, byte lengths and SHA-256 digests. SFace uses RGB inputs with mean 0 and divisor 1. Loading uses the hashes in `Casazen.Web/appsettings.json`. An environment override takes precedence, including an empty value or a path left over from an AuraFace installation.

The visual-zone reader uses the bundled Italian and English data from `tesseract-ocr/tessdata_fast` revision `87416418657359cb625c412a48b6e1d6d41c29bd`. Their hashes are in the same settings file. License notices for all bundled files ship in `MlModels/licenses`.

## Verification and replacement

1. Run `node scripts/biometrics/fetch-models.mjs` on the checkout to verify the four manifest entries. Re-export MiniFASNet with the pinned upstream revision and the Python versions in the manifest when replacing it. The exporter compares ONNX and PyTorch outputs.
2. Publish the API and check that the `MlModels` tree is present. The detector, anti-spoofing and recognizer load at startup when `Features__CheckInIdentityVerification` is enabled. Admins can inspect each model at `GET /api/admin/biometrics/status`. The readiness endpoint also reports model health.
3. Use the deployment checklist for calibration and the consent text registry for production approval. Model delivery alone does not approve a consent text or calibrate a property's thresholds.

AuraFace remains an external alternative. To use it, supply its model path, name, version, verified hash and provenance together, and restore normalization to mean and divisor 127.5. For a download, set `Biometrics__FaceRecognition__DownloadUrl` to a revision-pinned address.

## Health and status: the codes

| Code | Where | Meaning | Fix |
|---|---|---|---|
| `hash_not_configured` | any model | its SHA-256 is not pinned (the committed value is empty): the file is never used and never downloaded | set the matching `...__Sha256` value |
| `file_missing` | any model | no file at the path, and no download address (or no way to download) | commit the file, or set `Biometrics__FaceRecognition__DownloadUrl` for AuraFace |
| `hash_mismatch` | store models | the file is not the one pinned (a download that does not match is deleted) | pin the hash of the file you verified, or supply the right file |
| `download_failed` | recognition model | the download did not end (network, status, size, timeout) | check the address and `Biometrics__DownloadTimeoutMinutes`, use a volume |
| `path_invalid` | any model | an empty path, or one that leaves the application folder | fix `...__Path` |
| `model_incompatible`, `load_failed` | store models | the file is not a model the pipeline can run | supply the right file (for MiniFASNet: input `1x3x80x80`, output `1x3`) |
| `config_invalid` | the set | a value of the section `Biometrics` is out of range (the log names the setting, never a value) | fix the variable |
| `not_loaded` | store models | the flag was switched on after the process started: the models are loaded at start | restart the service |

The OCR-B file (`mrz_ocr`) is only checked for being pinned and on disk (`present`): its hash is verified the first time a document is read, because
the status runs on every health probe and never hashes a file. The visual-zone reader also verifies the bundled Italian and English files on first use.

## Rollback

Empty the `...__Sha256` variable (or switch `Features__CheckInIdentityVerification` off): the models are not used, every verification is the operator path,
and `biometrics` is `degraded` (flag on) or `healthy` (flag off). Nothing else changes, because the operator path never needed the models.

## Thresholds per organization and the operator video call (E22/F01/US01/T03)

These two do not need the models: they work with the flag on and the models absent (every verification is then the operator path, and the video call is
one way for the operator to decide).

### Thresholds (`GET/PUT api/settings/biometrics`)

| | |
|---|---|
| Who | the administrators of the organization (the org policy `OrgBillingAdmin`), only for their own org: the route has no organization in it. Behind `Features__CheckInIdentityVerification` |
| What | `enabled`, `acceptThreshold` (from this similarity the face is accepted without a person), `escalateThreshold` (between it and the accept threshold an operator decides), `maxAttempts`. A member left out of the `PUT` keeps its value |
| Bounds | 0.50 <= escalate < accept <= 0.95, attempts 1 to 5, checked on the whole result after rounding to four decimals; 422 `biometric_thresholds_out_of_range`, `biometric_thresholds_order_invalid` or `biometric_attempts_out_of_range` and nothing is saved |
| No row | today's defaults: 0.85, 0.70 and 3 attempts, enabled on the Agency plan only (ADR-013-R13), provisional (`isDefault` is true) |
| Provisional | the entity has no flag: a row is provisional while `CalibratedAt` is null. **Every change by the organization sets it back to null** (a calibration does not outlive the values it was made on); the same values again change nothing |
| Calibration | only a platform administrator writes it: `PUT api/admin/biometrics/{orgId}/calibrated` (policy `AdminOnly`, not behind the flag), after a calibration on real data (the thresholds must be calibrated before the flag goes on in production). An org that never saved anything gets a row with today's defaults, calibrated. 404 for an org that does not exist |
| Audit | one `OrgSettingsChanged` line in the activity log of the org for every real change and every calibration (the actor is the administrator, or the staff user; no values), and one line in the application log with the new numbers and who changed them; nothing for a repeat |

The first save of an organization that has no row writes the plan's default of `enabled` into it when the administrator did not send it: from then on the row, not the plan, decides.
The table has no unique index on the organization (the model is not changed by this task): two first saves in the same instant could write two rows, and every reader takes the oldest.

### Video call (`POST api/verification/{bookingId}/video-call`)

1. The operator creates a meeting with any provider (Meet, Teams, Zoom, Jitsi...) and pastes its address.
2. CasaZen checks its shape only (absolute https, at most 500 characters, no spaces, no credentials in it; 422 `video_call_url_invalid`): **it never opens the address**.
3. The guest gets the e-mail «Videochiamata per il check-in» with the link, in the language chosen on the check-in page (IT, EN, DE, FR, ES; Italian when none). The booking must be one the
   operator reaches whose verification waits for an operator (404 `no_pending_verification` otherwise).
4. **The link is in no table and no log of CasaZen.** What is kept is that a call was proposed, and when, on the open "To do" of the verification: its reason changes to «video call proposed» and a line of its notes
   says when (in the language of the org, without the operator); the log of the application names the booking and the operator, never the address. **It does stay in the body of the queued e-mail**: the Hangfire job
   of `EmailDeliveryJob` holds it until the job expires, 24 hours by default after a successful send (the lifetime of the jobs is not changed here); the product owner decides whether that is acceptable.
5. A guest with no e-mail address (an OTA guest) answers 409 `guest_unreachable`: nothing is sent, and the "To do" says to propose the call from the chat of the portal the booking came from. If the e-mail
   cannot be queued (no provider, queue down) the answer is 409 `video_call_not_sent` and nothing is recorded.
6. After the call the operator decides with the existing approval or rejection (`POST api/verification/{bookingId}/verify` or `reject`) with the note «videochiamata»: an operator decision
   (`Method = Operator`), which releases the lock code only on an approval, as ever.
