# Quokka360 happy-path demo — TEST ONLY

Prepared 17 November 2026 in casazen_test, organization ad979b3b-e739-49d6-84a4-c2c1e4f8f1e8.

## Result

36 bookings; 57 simulated completed payment entries, all booking totals reconciled. 36 issued guest fiscal documents and 3 issued property-management invoices. Native owner accounting generated 210 ledger entries and three approved/published monthly statements for October, November and December 2026. Statement PDFs are stored privately with native SHA-256 metadata. Seven owner documents (three statements, three invoice facsimiles, mandate) have verified private objects. Three property certificate facsimiles are explicitly marked TEST and have no legal/certification value.

21 manual proof records (including ten legacy cockpit mappings of existing Swiss police receipts) and 14 Swiss receipts. No overdue open internal activities at verification. Future operational activities remain planned. No external fiscal submission identifiers created.

## Execution

1. 01-preparation.sql completes synthetic payments/guest fiscal documents and inserts clearly simulated FX rates.
2. 02-proof-compatibility.sql maps existing Swiss evidence for the legacy cockpit and resolves only those completed compliance todos.
3. Native OwnerBulkCloseJob actions Post, Generate, Approve and Publish for months 10/11/12; job IDs and outcomes recorded in manifest. These invoke the existing ledger, validation, statement hash and native PDF services under the organization scope.
4. 03-owner-invoices.sql creates the three issued synthetic PM invoices linked to native statement versions before publication.
5. Additional synthetic PDF assets uploaded to casazen-test-private via a fixed-allowlist authenticated one-off function, downloaded and SHA-256 checked. The loader is disabled (JWT required + HTTP410). No secrets are committed.
6. 04-documents.sql registers those verified private files and marks prepaid tourist taxes collected in simulation.
7. 05-safety.sql applies the same synthetic completed safety facts as the native demo fixture to the Italian test property; these are not physical inspection attestations.
8. Test backend reused existing successful build to apply native encryption of legacy fixture plaintext. All 39 fiscal recipients verified encrypted. No production deploy or schema changes.

SQL files are an audit of mutations, not an automatic production migration. Do not run against another schema or organization. Native jobs should not be replayed without inspecting existing period/version state.

## Demo navigation

Select Quokka360 in the test app. Review bookings/guests, payments, compliance receipts, owner accounting monthly statements, issued fiscal documents and owner document archive. Native fiscal document PDF endpoints render guest documents from their issued records. The additional owner invoice PDFs are explicitly labelled facsimiles.

## Deliberate boundaries

- Synthetic demonstration, no real submissions, legal attestations or financial transfers.
- November and December statements include future stays; periods remain open.
- CHF/EUR demo rate 1.04 is synthetic.
- Swiss police receipts mapped to legacy generic manual-proof slot for cockpit compatibility; no Italian Alloggiati reports created for Switzerland.
- Chiara must be invited manually Thursday 19 November; no invitation sent.
- Owner Hotmail account not linked to owner portal because existing host/admin roles conflict; agency console reports available.
