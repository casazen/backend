# Demo seed

Every feature that adds or changes something a host, a guest, a supplier or a staff user can see updates the demo showcase in the same change.

- Extend `Casazen.Core/Demo/DemoShowcaseCatalog.cs`: a new scenario id in `Scenarios`, and template data that exercises the flow.
- Data is invented and obviously test (`TEST` in the CIN, `DOCUMENTO DI TEST` in the PDF, `@example.com` guests) and still valid for the real rules. Do not bypass CIN, document or safety checks.
- Mark new rows with `TestDataMarkers` and, for a new table, add a constant to `TestDataEntities`. Reset deletes only showcase markers.
- Extend `Casazen.Tests/Unit/Demo/DemoShowcaseTests.cs` so a scenario without template data fails.
- The spec template section is specifica rimossa (storico in git) (“Demo seed”). The runbook is `docs/runbooks/demo-environment.md`.
