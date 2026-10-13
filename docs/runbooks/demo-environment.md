# Demo environment

Staff-only, and only outside Production. In Production `GET /api/admin/demo` reports `available: false` and the other calls answer 404 `demo_unavailable`.

## Turn it on

1. Open **Amministrazione → Altro → Ambiente demo** (`/app/admin/demo`).
2. Turn **Modalità demo** on.
3. Choose the organization.

## Publish the listings that are already there

**Completa conformità di test** writes, on every short-stay property except the showcase draft:

- a CIN in the official format whose random part contains `TEST` and whose ISTAT comune matches the property (Como `013075`, Milano `015146`, Roma `058091`, or the comune already chosen);
- a real one-page PDF for each document the region requires (`CinCertificate`, and `Ape` in Lombardy, `PropertyLicense` in Lazio), stored as a private file named `TEST-….pdf`;
- a safety checklist the activation accepts (no gas, one extinguisher, BDSR declaration, confirmation).

`Properties.IsTestData` and `PropertyDocuments.IsTestData` are set, and a row in `TestDataMarkers` (`origin = compliance`) records each invented row. The property is then activated, so the public site can list it. Payments still depend on Stripe: this does not invent a connected account.

## Show the product from a clean template

**Reimposta la vetrina** asks for the word `RESET`. It deletes only rows marked `origin = showcase` for that organization and creates them again from `DemoShowcaseCatalog`:

- Villa del Lago (Como) and Appartamento Brera (Milano), published, with the Lombardy documents;
- Casa Trastevere (Roma), published, with the Lazio documents;
- Bilocale Navigli, long-term, with invented cadastral data;
- Monolocale in attivazione, left unpublished so the activation wizard is visible;
- two direct bookings on the villa (one coming, one finished) with guests at `@example.com`.

The optional checkbox soft-deletes other unpublished short-stay listings of that organization that have no booking. It does not delete published host listings.

A showcase listing that has a lease cannot be reset (`demo_reset_blocked`): remove the lease first.

## When a feature is added

Update the catalog, the marker and `DemoShowcaseTests` in the same change. The requirement is in `Sessions/specs/_TEMPLATE.md` and `.claude/rules/demo-seed.md`.
