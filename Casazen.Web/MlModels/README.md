# Check-in models

The API publishes YuNet, MiniFASNetV2, SFace, OCR-B and the Italian and English Tesseract visual-zone models alongside their licenses. Each model is checked against its SHA-256 pin before use. SFace runs locally and takes RGB pixels in the range 0–255.

The [manifest](models.manifest.json) records the face and MRZ models’ sources and export details. The [runbook](../../docs/runbooks/biometric-models.md) describes verification, the visual-zone model source, environment overrides and health reporting.
