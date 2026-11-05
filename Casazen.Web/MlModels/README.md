# MlModels: model files of the identity verification (E03/F04/US01)

The face check and the document reading of the guest check-in (behind `Features__CheckInIdentityVerification`, off by default) run models in the
API process, from memory. The small model files ship with the application from this folder; the large one is downloaded. **A file is used only
when its SHA-256 equals the one pinned in the configuration**: a missing file, a missing hash or a different one means "not available", and the
check-in goes to the operator or to the guided manual entry. Reference: `docs/TECHNICAL.md` (Identity verification models, Identity document
reading) and `docs/runbooks/deploy-checklist.md` (sections `Biometrics` and `MrzOcr`).

**No model file is committed yet and no hash is pinned.** The files below must be added by the operator after reading their licence and
training-data note (ADR-013 gates: model licence review, DPIA).

| Path | What | Source | Licence | Pin | Size |
|---|---|---|---|---|---|
| `biometrics/face_detection_yunet_2023mar.onnx` | YuNet face detector | OpenCV Zoo, `models/face_detection_yunet` | MIT | `Biometrics__FaceDetector__Sha256` | ~230 KB |
| `biometrics/2.7_80x80_MiniFASNetV2.onnx` | MiniFASNetV2 anti-spoofing, ONNX export of the PyTorch weights `2.7_80x80_MiniFASNetV2.pth` | Silent-Face-Anti-Spoofing (minivision-ai), `resources/anti_spoof_models`; export with `torch.onnx.export`, input `1x3x80x80`, output `1x3` (class 1 is the real face) | Apache-2.0 | `Biometrics__AntiSpoofing__Sha256` | ~2 MB |
| `biometrics/glintr100.onnx` | AuraFace-v1 face recognition (over 100 MB: **not committed**, downloaded to this path) | Hugging Face `fal/AuraFace-v1`; set `Biometrics__FaceRecognition__DownloadUrl` to a pinned revision | Apache-2.0 | `Biometrics__FaceRecognition__Sha256` | ~260 MB |
| `ocr/ocrb.traineddata` | OCR-B language data of Tesseract for the MRZ lines (`mrz.traineddata` of tesseractMRZ works with `MrzOcr__MrzLanguage=mrz`) | Tesseract community language data | Apache-2.0 | `MrzOcr__Mrz__Sha256` | a few MB |

The Italian and English language files of the printed fields (`MrzOcr__VisualZoneItalian__*`, `MrzOcr__VisualZoneEnglish__*`) are the ones of the
`tesseract-ocr-ita` and `tesseract-ocr-eng` packages of the image (`/usr/share/tesseract-ocr/5/tessdata`): pin their hashes the same way.

## Adding a file

1. Download it from its official source and read the licence and the training-data note; record them in `Biometrics__<Model>__License` and
   `Biometrics__<Model>__Provenance` if they differ from the committed ones.
2. `sha256sum <file>` (or `Get-FileHash -Algorithm SHA256 <file>`): 64 hexadecimal characters.
3. Put the hash in the configuration (`Casazen.Web/appsettings.json` or the Railway variable) and, for the small files, the file in this folder
   (a file of 100 MB or less may be in the repository, ADR-013-R8). The large file is never committed.
4. Deploy. The start of the API logs the name, version, licence, training-data provenance and weight hash of every model it loaded (ADR-013-R14).

SFace (OpenCV Zoo `face_recognition_sface_2021dec.onnx`, Apache-2.0) is the documented fallback for the recognition model: set
`Biometrics__FaceRecognition__Name=SFace`, `__Path`, `__Sha256`, `Biometrics__RecognitionMean=0` and `Biometrics__RecognitionStd=1`.
