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
- two direct bookings on the villa (one coming, one finished) with guests at `@example.com`;
- the demo guest of the mobile app (`DemoShowcaseCatalog.GuestAppEmail`, `ospite.app.demo@example.com`, scenario `guest-app-account`): three stays of the public checkout on the villa (`Source = Direct`: one coming and confirmed, one finished, one cancelled by the guest), paid at the property, so nothing reaches Stripe. They are what «Le mie prenotazioni» of the app shows to the store reviewers. The Auth0 account with that email is created **by hand**, with the email marked as verified: [auth0.md](auth0.md) § 12. The showcase exists only outside Production, so these bookings exist on the test API only.
- one saved wizard draft of the demo host (scenario `wizard-drafts`, ADR-024): a manual booking left half way at the guest step, the guest invented (`@example.com`, the name says TEST), saved two days ago so it is inside the 30 days a draft is kept. The reset splits it as the service does, so the guest's name, e-mail and phone are stored encrypted and the rest in clear; it is skipped if the host already has a draft of that wizard of its own. The reset deletes it with the other showcase rows (it finds it by its marker, `TestDataEntities.WizardDraft`).
- two popup alerts of the demo host (scenario `popup-alerts`, ADR-025): one open, about the stay of the villa that arrives in two weeks (a check-in to complete, pending as soon as the host opens the app), and one snoozed for three hours, about the house still in draft (a compliance issue that comes back later). They carry a type, a reference and an urgency and no text, and are written for the owner of the org; they are skipped if the owner has no account row. The reset deletes them before the stays and houses they point at (it finds them by their marker, `TestDataEntities.PopupAlert`).
- the tourist tax of Milano (scenario `tourist-tax-paid`, NF-100): three finished stays at Appartamento Brera, two with the tourist tax the engine records (18.00 and 12.00 EUR, invented) and one from a channel with none, and one payment of 18.00 EUR to the comune for the days from 90 to 60 before the reset, with a receipt that is an invented one-page PDF (`DOCUMENTO DI TEST`) in the private bucket. The summary of the last three months then shows 30.00 owed, 18.00 paid, 12.00 to pay, the payment with its receipt and one stay counted apart. The reset deletes the payment and its file before the houses (it finds them by their marker, `TestDataEntities.TouristTaxRemittance`); a real payment has no marker and is never touched.
- the marketplace (scenarios `marketplace-partners-visible` and `marketplace-activation-declared`, E09/F01, NF-54, NF-56): one test partner in the Partners group («Partner di prova (TEST)», an example address, texts in Italian and English that say they are a test, visible to every plan and country), and one activation of it that the demo host declared a few days ago, so the Active tab shows an integration whose status was declared and not proven. The partner is platform data shared by the whole installation: the reset creates it only if the catalog has no partner with that code, and deletes it with the activation (it finds both by their markers, `TestDataEntities.MarketplacePartner`, `MarketplacePartnerText` and `MarketplaceActivation`); a partner or an activation a person made has no marker and is never touched.
- the CRM profiles (scenario `crm-profiles`, E07/F01/US02, NF-88, NF-89): seven bookings with invented guests (`@example.com`, texts versioned `DEMO-TEST-1`) — a returning guest with a stay that is over at the villa and one to come at Casa Trastevere, a Booking.com booking at Attico Firenze (its address becomes an OTA forward, service only), a direct customer at Loft Torino who saw the soft opt-in notice, a guest at Villa Amalfi with a confirmed marketing consent from the online check-in, and two guests at Appartamento Napoli who left the same phone number (a merge proposed to a person, not done). The profiles, contacts and proposed merges are made by the CRM sync itself, run on every showcase booking in a scope under the demo org, and marked (`TestDataEntities.CrmProfile`, `CrmContact`, `CrmMergeCandidate`); the reset deletes them before the guests and houses, and a profile of a real guest has no marker and is never touched. They exist only when the CRM sync is open in the environment, that is `Features__CrmMarketing` on **and** `Crm__ContactIndexKey` set (otherwise the seven bookings are there and no CRM profile is made; [crm-profiles.md § 4](crm-profiles.md#4-the-sync-with-the-bookings-t03)).
- the invoice data of a booking (scenario `invoice-data`, E06/F02/US01, NF-81): a finished direct stay at Appartamento Brera (`fattura-azienda`), paid at the property, for which the guest said yes to «Ti serve la fattura?» and the host completed the recipient: a company named «Demo Viaggi S.r.l. (TEST)» with a VAT number that passes its check digit, an address in Milano, a recipient code and a PEC at `example.com`, source `Host`. Written with the booking, encrypted at rest, marked as showcase data (`TestDataEntities.BookingInvoiceData`) and gone with the booking at the next reset (the table cascades); a real row has no marker and is never touched.
- the profile of the issuer of the documents (scenario `fiscal-issuer-profile`, E06/F02/US02, FIS-02, NF-82): one complete profile for the demo organization (`emittente-demo`, «Casa Demo S.r.l. (TEST)», via Brera 18, 20121 Milano MI, regime with VAT in the ordinary regime, regime code `RF01`, wizard completed five days ago) with the choice «the accountant takes care of the documents», so it needs neither `Features__FiscalDocumentIssuance` nor an Octorate connection. There is one profile for each organization: the reset writes it only when the org has none of its own (a real profile has no marker, is never touched and is never replaced), marks it (`TestDataEntities.FiscalIssuerProfile`) and deletes it at the next reset.
- the welcome link of a house (scenario `crm-welcome-link`, E07/F04/US01/T03, NF-90): the lake villa (`villa-lago`) has an active welcome link with the fixed test token `TEST-benvenuto-villa-lago-demo-01`, so the host sees the QR code of a house that has one and anyone can open the public welcome page `/benvenuto/TEST-benvenuto-villa-lago-demo-01` (`GET api/public/welcome/{token}`) after every reset. The link is marked (`TestDataEntities.WelcomeLink`) and follows its house; it is made only when no other org holds the token (the token is unique among all the links). The page needs `Gdpr__MarketingConsentVersion` to offer the consent ([crm-consent.md](crm-consent.md)).

The optional checkbox soft-deletes other unpublished short-stay listings of that organization that have no booking. It does not delete published host listings.

A showcase listing that has a lease cannot be reset (`demo_reset_blocked`): remove the lease first.

## When a feature is added

Update the catalog, the marker and `DemoShowcaseTests` in the same change. The requirement is in specifica rimossa (storico in git) and `.claude/rules/demo-seed.md`.
