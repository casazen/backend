# Fix plan — canone concordato attempt 1

Fix-planner only. Dependency order. No patches.

1. **D-AC8-EXPORT** (backend) — register `IComuneImuNotificationService`, add `GET /api/leases/{id}/canone-concordato/imu-notification/export` on `LeasesController` (`lease.read`, owner 404, not-Registered 409), add `ComuneImuNotificationServiceTests` (PDF `%PDF`, Seveso channel uncertainty, Cesano `valore derivato`, pre-registration throw). Unlocks AC8 VO and the export half of AC10.
2. **D-AC9-MARKSENT** (backend) — add `POST .../mark-sent` (`lease.register`), tests that `ImuNotificationExported` fires on export and `ImuNotificationMarkedSent` only on mark-sent. Completes AC9 + remaining AC10.
3. **D-AC10-IMU-RBAC** — verify-only after 1–2 (policies + 404). No separate product change unless review finds a missing gate.
4. **D-AC13-I18N-EXPORT** (frontend) — add `leases.canoneConcordato.*` to `it.json`; add authenticated blob download + mark-sent on `canone-concordato.api.ts` (same pattern as `leasesApi.downloadReceipt`). Italian copy must exist before the button ships.
5. **D-AC12-IMU-BTN** (frontend) — create `imu-notification-export-button.tsx`, mount on `lease-detail-page.tsx`, enable only when `lease.status === 'Registered'`.

Do not reopen AC1–AC7/AC11 (PASS). Do not wire AC6 into `LeaseContractTemplateService` (sibling SPEC-ONLY).
