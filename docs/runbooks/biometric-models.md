# Runbook: biometric models (delivery kit, health and status)

E22/F01/US01/T01 (operations plan of 2026-11-14, decision O7). ADR-013 (R8: models pinned by SHA-256, R14: provenance), NF-24, NF-50. What the
models do, and what the identity verification does with them: [`TECHNICAL.md`](../TECHNICAL.md) (Identity verification models, Identity document
reading). The variables: [`deploy-checklist.md`](deploy-checklist.md) § 2.10.

## Where we are

**No model file is committed, downloaded or exported, and no hash is pinned.** Downloading them, fixing their SHA-256 and committing the small ones
needs the permission of the product owner (decision O7); this task only prepared the delivery so that it is one command once he says yes.
Until then, with `Features__CheckInIdentityVerification` on, **every selfie verification is the operator path** (`Escalate`) and the document reading
reads nothing (the guest types the data, guided): nothing breaks, the state is visible.

| Piece | Where | What it does |
|---|---|---|
| Manifest | `Casazen.Web/MlModels/models.manifest.json` | one entry per model: target path under `MlModels/`, source with its revision, licence, approximate size, `sha256` (null), the variables to pin |
| Fetch and verify | `scripts/biometrics/fetch-models.mjs` (Node 20, no dependency) | downloads the models you choose, refuses a size off by more than 20 %, computes the SHA-256, prints the Railway variable lines, with `--write-manifest` writes the hashes into the manifest. Never runs in CI (it refuses when `CI` is set) |
| MiniFASNet recipe | `scripts/biometrics/export_minifasnet.py` | exports MiniFASNetV2 from its PyTorch release to ONNX (input `1x3x80x80`, output `1x3`, class 1 is the real face), with the pip versions in a comment. Run by nobody automatically |
| Health | the `biometrics` entry of `GET /api/health/ready` | `healthy` when the flag is off or every model is ready; `degraded` with the failure code of each model that cannot be used; **never `unhealthy`** |
| Status | `GET /api/admin/biometrics/status` (platform administrators) | per model: role, name, version, licence, state, code and whether its SHA-256 is pinned. No path, no hash, no address |

## The models

| Id | What | File (under `MlModels/`) | Source | Licence | Size | Delivery | Variables |
|---|---|---|---|---|---|---|---|
| `yunet` | face detector and landmarks | `biometrics/face_detection_yunet_2023mar.onnx` | OpenCV Zoo `models/face_detection_yunet` | MIT | ~230 KB | committed | `Biometrics__FaceDetector__Sha256` |
| `minifasnet` | anti-spoofing (liveness) | `biometrics/2.7_80x80_MiniFASNetV2.onnx` | Silent-Face-Anti-Spoofing `resources/anti_spoof_models`, exported with the recipe | Apache-2.0 | ~2 MB | committed | `Biometrics__AntiSpoofing__Sha256` |
| `auraface` | face recognition (more accurate) | `biometrics/glintr100.onnx` | Hugging Face `fal/AuraFace-v1` | Apache-2.0 | ~260 MB | **external** (never committed) | `Biometrics__FaceRecognition__Sha256`, `Biometrics__FaceRecognition__DownloadUrl` |
| `sface` | face recognition, the documented alternative (small, less accurate) | `biometrics/face_recognition_sface_2021dec.onnx` | OpenCV Zoo `models/face_recognition_sface` | Apache-2.0 | ~38 MB | committed | `Biometrics__FaceRecognition__Sha256` and the ones of `config.set` in the manifest (`Name`, `Version`, `Path`, `Biometrics__RecognitionMean=0`, `Biometrics__RecognitionStd=1`) |
| `ocrb` | OCR-B language data for the MRZ lines | `ocr/ocrb.traineddata` | Tesseract community language data | Apache-2.0 | a few MB | committed | `MrzOcr__Mrz__Sha256` |

The sizes are the ones of the README of `MlModels` (the OCR-B file only says «a few MB», so its entry has a wide tolerance). The licence and the note on the
training data of each model are in `BiometricModelOptions` / `appsettings.json`; the training data of MiniFASNet is not published and AuraFace-v1 is
trained on a dataset the authors license for commercial use: both are open points of the DPIA gate of ADR-013, not closed by this delivery.

### `revision: null`

The README names no commit or revision for any model, so the manifest says `"revision": null` for all of them and the script downloads the
`defaultRevision` (a moving branch: `main`, `master`) **with a warning**. What makes a file safe is the SHA-256 you pin: the API never uses a file whose
hash is not pinned or does not match (`ModelFileProvisioner`). When you pin, also write the commit you downloaded from
(`--revision <id>=<commit>` together with `--write-manifest`) so the next download is of the same bytes, and check that the repository addresses of the
manifest are the ones you mean (the OCR-B one in particular: the README gives none).

## AuraFace-v1 or SFace

| | AuraFace-v1 | SFace |
|---|---|---|
| Accuracy | better (the reason it is the default of `BiometricModelOptions`) | lower: expect more reviews by an operator |
| Size | ~260 MB: over the **100 MB limit of a file on GitHub**, so it cannot be committed (and the repository is public) | ~38 MB: can be committed in `MlModels/biometrics/` |
| How it reaches the API | downloaded at start to `Biometrics__FaceRecognition__Path` from `Biometrics__FaceRecognition__DownloadUrl` and kept only when its SHA-256 matches; **or** kept on a Railway volume | shipped in the image like the small models |
| Cost | the disk of the container is not kept between deploys: without a volume every start downloads 260 MB (`DownloadTimeoutMinutes`, 30 by default) and the models are not ready until it ends (`biometrics` is `degraded`, the verifications go to the operator) | none |

**Download at start.** Set `Biometrics__FaceRecognition__DownloadUrl` to the address the script printed (a pinned revision) and the hash; nothing else.

**Railway volume.** Mount a volume on the service of each environment at a folder of its own, for example `/data/models` (not on
`/app/MlModels/biometrics`: a volume hides the small models committed in that folder), set `Biometrics__FaceRecognition__Path=/data/models/glintr100.onnx`
(an absolute path is allowed) and the download address and hash as above: the first start downloads into the volume, the next ones only hash the file.

**SFace instead.** `fetch-models.mjs --only sface`, commit the file, and set the variables the script prints (`Name`, `Version`, `Path`, the hash and
`Biometrics__RecognitionMean=0`, `Biometrics__RecognitionStd=1`): only these settings change, the code is the same.

## The exact steps once the product owner says yes

Do the first six on a machine of yours, never in CI, and read the licence and the training-data note of each model before the first one (ADR-013 gates).

1. `node scripts/biometrics/fetch-models.mjs --list`, then `--dry-run`: no network, it shows what would be downloaded from where.
2. `node scripts/biometrics/fetch-models.mjs --only yunet,ocrb,auraface` (or `sface` instead of `auraface`). It refuses a size off by more than 20 %
   (`--force` accepts it, after you have looked), computes the SHA-256 and prints the Railway variable lines.
3. Export MiniFASNetV2 with `scripts/biometrics/export_minifasnet.py` (the steps are in its header: clone the repository at the commit you pin, install the
   pip versions of the comment, run it), then `fetch-models.mjs --only minifasnet`.
4. Rerun with `--write-manifest` and `--revision <id>=<commit>` for each model: the hashes and the revisions are in the manifest, in the same commit as the files.
5. Commit the small files (`yunet`, `minifasnet`, `ocrb`, or `sface`) under `Casazen.Web/MlModels/`: each is far under the 100 MB limit. **Never AuraFace.**
6. Open the pull request. `ModelManifestTests` checks that the manifest still agrees with the options and with this checklist.
7. Set the variables the script printed on Railway **test**, deploy, then check `GET /api/admin/biometrics/status`: every model `ready` (the OCR-B file `present`)
   and the `biometrics` entry `healthy`. A model that is not is named with its code (below).
8. Turn `Features__CheckInIdentityVerification` on in test only when the thresholds are calibrated on real data and the assessment of the biometric data
   (art. 9 GDPR) is done (`deploy-checklist.md`); the legal texts are approved by the lawyer. Production only after the test runs.

## Health and status: the codes

| Code | Where | Meaning | Fix |
|---|---|---|---|
| `hash_not_configured` | any model | its SHA-256 is not pinned (the committed value is empty): the file is never used and never downloaded | set the `...__Sha256` variable (step 7) |
| `file_missing` | any model | no file at the path, and no download address (or no way to download) | commit the file, or set `Biometrics__FaceRecognition__DownloadUrl` for AuraFace |
| `hash_mismatch` | store models | the file is not the one pinned (a download that does not match is deleted) | pin the hash of the file you verified, or supply the right file |
| `download_failed` | recognition model | the download did not end (network, status, size, timeout) | check the address and `Biometrics__DownloadTimeoutMinutes`, use a volume |
| `path_invalid` | any model | an empty path, or one that leaves the application folder | fix `...__Path` |
| `model_incompatible`, `load_failed` | store models | the file is not a model the pipeline can run | supply the right file (for MiniFASNet: input `1x3x80x80`, output `1x3`) |
| `config_invalid` | the set | a value of the section `Biometrics` is out of range (the log names the setting, never a value) | fix the variable |
| `not_loaded` | store models | the flag was switched on after the process started: the models are loaded at start | restart the service |

The OCR-B file (`mrz_ocr`) is only checked for being pinned and on disk (`present`): its hash is verified the first time a document is read, because
the status runs on every health probe and never hashes a file. The Italian and English language files of the printed fields come from the system packages
of the image and are not part of this delivery.

## Rollback

Empty the `...__Sha256` variable (or switch `Features__CheckInIdentityVerification` off): the models are not used, every verification is the operator path,
and `biometrics` is `degraded` (flag on) or `healthy` (flag off). Nothing else changes, because the operator path never needed the models.
