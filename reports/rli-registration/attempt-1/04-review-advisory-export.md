STATO: APPROVED

Slice: AC3 / AC4 / AC5 — templates, advisory, export

- Per-regime `LeaseTemplates:Variants` `VersionId` + `Approved`. Unapproved `GeneratePdfAsync` throws (blocks `InitiateSigningAsync` PDF path). Dev ships `dev-stub` / `Approved: true`.
- `CedolareAdvisoryService` rates and disclaimer come from `CedolareAdvisory` config; service has no hardcoded 0.21. Disclaimer includes "non consulenza fiscale".
- `GET /api/leases/{id}/rli/advisory` is owner-scoped (`lease.read`).
- `GET /api/leases/{id}/rli/export` gated `lease.register`; real `%PDF` via `FiscalPdfWriter`; filename `rli-prefill-{leaseId:N}.pdf` (no CF / P.IVA). Does not call submit. Emits `RliExported`.
- L1: `LeaseContractTemplateServiceTests`, `CedolareAdvisoryServiceTests`, `RliExportServiceTests`.
