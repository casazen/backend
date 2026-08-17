STATO: APPROVED

Slice: CanoneConcordato contract PDF (AC6 feed + RLI AC3 approved path)

- `GeneratePdfAsync` no longer returns a UTF-8 placeholder.
- Approved `CanoneConcordato` emits `%PDF` with BOZZA, L. 431/1998, comune, monthly rent, parties, template version.
- Approved `CedolareSecca` also emits `%PDF` so e-sign is not sent a text stub.
- Unapproved regime still throws before bytes.
- Missing Property/Parties still returns `%PDF` with BOZZA (no throw).
- L1: `LeaseContractTemplateServiceTests` (4) + IMU/RLI PDF tests still green (19 total in combined filter).
