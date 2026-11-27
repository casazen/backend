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

### Two-frame head-turn check

The guest looks straight ahead, then turns their face about 30° towards the indicated side. Directions refer to the pixels received by the API. Both captures must meet the selfie quality rules and contain exactly one face. A weak-perspective estimate uses the nose offset along the eye line and the canonical ratio of nose depth to interocular distance (0.636267). The first estimate must be within 15° of frontal, the second must reach 20° on the requested side and the change must be at least 15°. Individual anatomy and landmark error affect these approximate angles. [MediaPipe canonical geometry](https://github.com/google-ai-edge/mediapipe/blob/master/mediapipe/modules/face_geometry/data/canonical_face_model.obj)

The bundled SFace 2021dec model uses a cosine similarity of at least 0.363 between the two camera frames. This operating point comes from the publisher's implementation and applies only with the shipped model hash and its RGB preprocessing. Other models use the configured organisation acceptance threshold. The document-to-neutral-selfie comparison separately enforces the organisation's thresholds and the existing liveness check. [OpenCV Zoo reference implementation](https://github.com/opencv/opencv_zoo/blob/main/models/face_recognition_sface/sface.py)

An isolated offline benchmark computed quality, pose and cosine scores once per capture. Within each corpus it evaluated every genuine pair, every same-person pair with the opposite direction, and every cross-person pair in the requested direction. Input frames were used at native resolution.

| Corpus and prespecified captures | Genuine turns accepted | Wrong directions accepted | Different-person pairs accepted |
| --- | ---: | ---: | ---: |
| Pointing'04 series 2, 15 participants, frontal and ±30° | 16/30 | 0/30 | 0/420 |
| Pointing'04 series 1, same participants and angles | 15/30 | 0/30 | 0/420 |
| CMU Face Images, 20 other participants, neutral expression and open eyes | 29/40 | 0/40 | 0/760 |
| MAPIR Faces, 16 other participants, centre and both horizontal grid extremes | 4/32 | 0/32 | 0/480 |

A separate run of the complete timed service on Pointing'04 series 2 accepted 15/30, including 9 left and 6 right turns. The existing selfie quality checks rejected 28/32 MAPIR pairs in the wide camera framing. Repeated comparisons reuse subjects. The results measure consistency in still images and give no estimate of resistance to presentation attacks. A guest needs recapture and operator review when the check fails.

Benchmark data are from [Gourier, Hall and Crowley (2004), distributed in FASSEG Multipose01 by Khan et al. (2017)](https://github.com/massimomauro/FASSEG-repository#dataset-descriptions), [T. Mitchell, CMU Face Images, UCI (1997), CC BY 4.0](https://doi.org/10.24432/C5JC79) and [S.-F. Baltanas, J.-R. Ruiz-Sarmiento and J. Gonzalez-Jimenez, MAPIR Faces (2020), CC BY 4.0](https://doi.org/10.5281/zenodo.4311765).

### Presence by challenge and sequence of frames (self-hosted flow, E72/F01/US01)

With `Features:CheckInIdentitySelfHosted` on (and `CheckInIdentityVerification`), the presence is a challenge and a sequence of frames, not two captures (ADR-013-R17, R22; NF-192). Every number is the policy's (`presence.*` of the file `IdentityPolicies/identity-policy-*.json`) or the section `IdentityVerification:Presence`; the few constants of the code are named below.

- **Challenge** (`POST api/public/checkin/{token}/presence-challenge`, rate limit `GuestIdentity`, never cached): `presence.challenge.steps` moves drawn with `RandomNumberGenerator` from `actions`, none after itself when `noImmediateRepeat` (4 x 3 x 3 = 36 sequences, about 5.2 bits, with the default policy), expiring after `lifetimeSeconds`. It is stored in the member `presence` of `GuestCheckInSession.DocumentCheckJson` (id, moves, times and attempt: never a frame or a score) under the row lock of the pose challenge; each call replaces it. The proof the guest gets is a Data Protection payload of the purpose `Casazen.CheckIn.Presence.v1` bound to the session, the token hash of its link, the attempt (attempts made + guided retries used + 1), the time of the consent and the challenge id. It answers 409 `presence_challenge_unavailable` (with the state) unless the identity step is opened and not closed, the guest did not choose a person (the assisted path), the consent is the current one, the document step is complete, no person has the verification, the policy is ready and the models are ready. The directions are those of the picture the server receives: `turn_right` moves the nose to the right of that picture, `look_up` raises it.
- **Intake** (`FrameSequenceReader`): JPEG parts (the bytes begin `FF D8 FF`, whatever type is declared) with a `timestamps` JSON array of strictly increasing non-negative integers, the first 0, one per frame. Limits `IdentityVerification:Presence:MaxFrames`, `MaxFrameBytes`, `MaxSequenceBytes`, `MaxFrameSide` and `Biometrics:MaxImagePixels`; a violation is `presence_sequence_invalid` or `presence_sequence_too_large` and stops the intake, zeroing what was read. The frames are decoded in memory (StbImageSharp, no ffmpeg, no native dependency) and never reach a disk, a log or a job.
- **Validation** (`PresenceSequenceValidator`), five checks in this order, every failure collected: timing (`presence_timing`, `presence_too_short`; a recording longer than the time between the challenge and its receipt is not correctable), one face per frame (`presence_single_face`), quality with the capture rules of the check-in (`presence_quality`), continuity (`presence_continuity`, never correctable: between two consecutive frames with one face, a jump of the centre of the face of more than `maxCenterJump` widths of the face (the mean width of the two boxes, not a share of the frame) or a change of its width of more than `maxSizeChange` of the first one; a still picture played again; two people), and the moves in their order (`presence_challenge_not_followed`). A time over `SequenceTimeoutMs`, a model that fails or no policy is the report of a sequence that could not be judged (check `processing`, result `unavailable`), never a pass. It runs no liveness and no match.
- **Pose** (`FacePose`): the yaw is the estimate of the two-frame check above; the pitch is read from where the nose tip stands between the line of the eyes (0) and the line of the mouth (1), `r`: `pitch = atan((0.495 - r) / 0.551)`, positive looking up. The two constants are proportions of a face, not thresholds: the alignment template of the recognition models (nose tip 0.495 of the way from the eyes to the mouth) and the nose depth of the yaw (0.636267 eye distances) in units of the distance from the eyes to the mouth. How far a head must go for a move to count is `presence.pose`, measured from the pose of the same person during the lead-in. Other constants of the code, fixed by the specification and not calibrations: the mean rate between half and twice `targetFps` and a window of half a step of tolerance around each step. Nothing else decides in the code: the mean difference of the pixels under which a sequence is a still picture played again (no frame farther from the first one than that, or all the frames the same bytes) is `presence.continuity.minPixelDifference`, in grey levels (1 in the committed file; above 0 and at most 255).

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
