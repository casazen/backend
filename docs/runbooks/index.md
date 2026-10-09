# Indice dei runbook

Un runbook per tema (48 file, oltre a questo indice), creato o aggiornato dai task del piano di risanamento (decisione D9: codice più runbook, la configurazione dei servizi esterni la applica il product owner). Nessun runbook esisteva alla base dell'audit (`develop@4cbaeaa`, 2026-09-23): tutti i file qui sotto, tranne `free-hosting-analysis.md` (task HOSTING, 2026-10-02), `org-team.md` (task AM-01, AM-02 e AM-03 della wave di redesign, 2026-10-08/09), `property-rental-mode.md` (task PM-01 della wave di redesign, 2026-10-08), `open-access.md` (task BL-01 della wave di redesign, 2026-10-09) e `long-rent-aggregates.md` (task LR-01 della wave di redesign, 2026-10-09), sono stati introdotti dal piano. La colonna *Task* elenca i task che citano il runbook (dettaglio in [`Sessions/risanamento/PIANO-ESECUZIONE.md`](../../Sessions/risanamento/PIANO-ESECUZIONE.md)).

> **Hosting in revisione (2026-10-02):** il PO ha cancellato Railway. Nei runbook "Railway" indica l'host del backend così com'era configurato: va riletto alla luce dell'analisi del task HOSTING ([`free-hosting-analysis.md`](free-hosting-analysis.md); raccomandazione: VM Oracle Cloud Always Free, decisione del PO ancora da prendere). Il database resta su Supabase. Le variabili e le procedure non legate all'host restano valide.

Il punto di partenza per un deploy è [`deploy-checklist.md`](deploy-checklist.md): ogni variabile, dove si imposta e cosa succede se manca; `DeployChecklistConsistencyTests` la tiene allineata al codice.

## Piattaforma, accesso e billing

| Runbook | Argomento | Task |
|---|---|---|
| [`auth0.md`](auth0.md) | Auth0 (tenants, Management API, Action, mobile client) | FD-05, FD-14, FN-04, MO-01, MO-03, MO-05, PL-01, PL-02 |
| [`onboarding-consents.md`](onboarding-consents.md) | onboarding gate and legal consents | PL-02, PL-03, PL-14 |
| [`org-team.md`](org-team.md) | org membership (`OrgMember`), org roles, the `account` context, owner backfill and reconcile, `member_inactive`, flag `OrgTeam`; invitations, members, seats, change of org, maintenance job (AM-02); the properties each member reaches ("Solo alcuni"), the fine permissions, who is told, the holder for RLI and IMU (AM-03); the activity log (events, no personal data, CSV, 12-month retention) and the requests for access to the administrators (AM-02b) | AM-01, AM-02, AM-03, AM-02b |
| [`activation-checklist.md`](activation-checklist.md) | activation checklist and "site published" flag | BK-07, BK-13, BK-16, PC-03, PC-05, PL-04, PL-15 |
| [`stripe.md`](stripe.md) | Stripe webhooks and platform billing (idempotency, one subscription per org) | BK-02, BK-04, BK-07, BK-08, BK-09, BK-10, BK-19, BK-21 |
| [`billing-tax.md`](billing-tax.md) | VAT on the CasaZen subscriptions (Stripe Tax) and Italian e-invoices (SDI) | PL-10, PL-11, PL-13, RS-5 |
| [`legal-documents.md`](legal-documents.md) | legal documents and subprocessors | BK-14, CO-13, CO-16, PL-02, PL-13, PL-14 |
| [`demo-mode.md`](demo-mode.md) | web demo mode (no login) | PL-01 |
| [`feature-flags.md`](feature-flags.md) | feature flags (including `UiRedesign`, the rollout of the new interface) | BL-01, FD-20, FD-21, LT-01, LT-02 |
| [`open-access.md`](open-access.md) | "accesso aperto": every org served as a chosen plan at least, from configuration (`Entitlement__OpenAccess__*`), what changes and how to go back to the paid plans | BL-01 |

## Fondamenta tecniche e CI

| Runbook | Argomento | Task |
|---|---|---|
| [`ci-backend.md`](ci-backend.md) | backend CI (casazen/backend) | FD-02, FD-04, FD-12, FD-13, FN-03 |
| [`ci-frontend.md`](ci-frontend.md) | frontend CI (casazen/frontend, casazen/mobile) | FD-01, FD-03, FN-02, FN-03, FN-04 |
| [`health-checks.md`](health-checks.md) | health checks, startup validation and deploy verification | BK-17, FD-07, FD-11, FD-12, FD-13, PL-11, PL-13, PL-14 |
| [`hangfire.md`](hangfire.md) | Hangfire (one schema per environment, no overlapping runs) | BK-02, BK-04, BK-06, BK-17, BK-21, CO-06, CO-09, CO-10 |
| [`email.md`](email.md) | transactional email (Resend) | BK-03, BK-04, BK-06, BK-07, BK-08, BK-10, BK-12, CO-06 |
| [`storage.md`](storage.md) | object storage (Supabase Storage) and Data Protection keys | BK-12, CO-02, CO-14, CO-15, CO-16, FD-07, FD-17, LT-01 |
| [`encryption.md`](encryption.md) | encrypted database columns (guest documents, Questura credentials, secrets) | CO-09, CO-12, CO-13, CO-14, FD-07, FD-20, PC-11, TN-1 |
| [`external-fetch.md`](external-fetch.md) | downloading user-chosen URLs (iCal import) without SSRF | FD-16, PC-10 |
| [`cors-security-headers.md`](cors-security-headers.md) | CORS, security headers and logs without personal data | BK-16, FD-17, SE-02, SU-11 |
| [`proxy-ip.md`](proxy-ip.md) | client IP behind the Railway proxy and rate limiting | BK-06, BK-07, BK-11, BK-15, FD-10, SE-04, SU-01 |
| [`ai.md`](ai.md) | AI provider, budget, rate limits, GDPR | FD-21, PL-14, SE-01, SE-05 |

## Deploy e rilascio

| Runbook | Argomento | Task |
|---|---|---|
| [`deploy-checklist.md`](deploy-checklist.md) | deploy checklist (every variable, where it goes, what happens without it) | BK-06, BK-08, BK-11, BK-17, BK-21, CO-06, CO-09, CO-10 |
| [`free-hosting-analysis.md`](free-hosting-analysis.md) | Analisi: hosting gratuito del backend dopo la chiusura di Railway | HOSTING |
| [`mobile-release.md`](mobile-release.md) | mobile app release (EAS Build) | BK-10, CO-10, FD-13, MO-02, MO-03, MO-04, MO-05, MO-11 |

## Proprietà, calendario e prenotazioni

| Runbook | Argomento | Task |
|---|---|---|
| [`property-address.md`](property-address.md) | property address uniqueness, unit and coordinates | PC-03, PC-05, PC-06 |
| [`property-rental-mode.md`](property-rental-mode.md) | rental mode of a property (short or long): rules, backfill with its dry run, predicates that ignore long-term properties, the scheduled change between the two modes (preview, daily job, calendar block, e-mails, flag), rollback | PM-01, PM-02 |
| [`ical.md`](ical.md) | iCal import and export (property OTA calendars, supplier calendars) | BK-02, BK-04, BK-05, BK-06, BK-21, CO-04, CO-08, CO-09 |
| [`seasonal-suggestions.md`](seasonal-suggestions.md) | Suggerimenti stagionali (seasonal price suggestions) | BK-03, BK-07, FD-20, PC-15 |
| [`direct-booking.md`](direct-booking.md) | direct booking — "Paga in struttura" requests, checkout outcome page, payment options | BK-02, BK-03, BK-04, BK-06, BK-07, BK-08, BK-10, BK-11 |
| [`tourist-tax-rates.md`](tourist-tax-rates.md) | tourist tax rates (seed, admin page, activation wizard, checkout, calculator) | BK-03, CO-03, CO-19, RS-7, SU-04 |
| [`tenant-child-orgid-migration.md`](tenant-child-orgid-migration.md) | automatic tenant filter and OrgId on child rows (TN-2) | PL-05, TN-2 |
| [`guest-tenant-migration.md`](guest-tenant-migration.md) | guests per tenant (TN-1) | TN-1 |

## Fornitori

| Runbook | Argomento | Task |
|---|---|---|
| [`suppliers.md`](suppliers.md) | supplier invites, self-serve registration, claim and service requests | BK-15, FD-05, FD-10, FD-14, LT-05, MO-07, MO-10, PL-01 |
| [`service-categories.md`](service-categories.md) | service categories (one taxonomy) and data migration | FD-21, SU-03 |

## Compliance e normativa

| Runbook | Argomento | Task |
|---|---|---|
| [`cin-format.md`](cin-format.md) | CIN format and data migration | CO-01, CO-06, CO-20, FD-13, RS-2, SU-04 |
| [`compliance.md`](compliance.md) | compliance status of the properties (suspension, nightly check, historic recalculation) | BK-05, CO-01, CO-06, CO-07, CO-17, FD-13, SU-04 |
| [`alloggiati.md`](alloggiati.md) | Alloggiati Web, honest status and manual submission | CO-02, CO-09, CO-10, CO-11, CO-12, CO-13, CO-14, CO-21 |
| [`gdpr.md`](gdpr.md) | guest data rights, consents and retention (GDPR) | CO-12, CO-15, FD-11, LT-12, TN-1 |
| [`comuni-istat.md`](comuni-istat.md) | official ISTAT list of the comuni | CO-12, RS-6, SU-04 |
| [`ross1000.md`](ross1000.md) | flussi turistici (Ross1000 Lombardia, ISTAT) e portali regionali | CO-12, CO-13, CO-22, RS-9, SU-04 |

## Affitti lunghi (LTR)

| Runbook | Argomento | Task |
|---|---|---|
| [`rli.md`](rli.md) | RLI registration of lease contracts | FD-06, FD-07, LT-01, LT-02, LT-03, LT-04, LT-07, LT-08 |
| [`lease-contract-templates.md`](lease-contract-templates.md) | lease contract templates (approval gate, clause texts from the product owner, draft templates 2026-11 not approved) | FD-02, LG-02, LT-02, LT-03, LT-09, LT-10 |
| [`canone-concordato.md`](canone-concordato.md) | Canone concordato: range, dati dell'accordo, tipo di contratto (LT-10, LT-13) | LT-08, LT-10, LT-13 |
| [`long-rent-aggregates.md`](long-rent-aggregates.md) | elenco contratti con viste e ricerca, registro dei canoni con i numeri del mese e i solleciti e-mail (limite di frequenza, nota, link Stripe), scadenze e panoramica d'area, scope per immobile in SQL | LR-01 |

## SEO e siti pubblici

| Runbook | Argomento | Task |
|---|---|---|
| [`seo-domain.md`](seo-domain.md) | public domain, canonical URLs, sitemap, robots.txt and signup funnel | BK-15, BK-16, BK-17, FD-13, FD-15, PL-11, PL-14, SE-01 |
| [`seo-funnel.md`](seo-funnel.md) | SEO funnel — featured properties, events without personal data, admin widget | SE-03, SE-04, SU-04 |

## Verifica end-to-end

| Runbook | Argomento | Task |
|---|---|---|
| [`golden-journey-l3.md`](golden-journey-l3.md) | Golden Journey L3 from the UI on an ephemeral stack | BK-06, CO-17, FN-03, FN-04, SU-04 |
| [`mobile-e2e.md`](mobile-e2e.md) | host app E2E (Maestro on an Android emulator, ephemeral stack) | FN-03, FN-04, MO-09, MO-10, MO-11, PC-09, SU-05 |

## Altri documenti operativi

- `docs/integrations/rli-esign.md`: provider RLI e firma elettronica (ricerca RS-4, fonti e limiti).
- `docs/integrations/ai-assistants-plan.md`: piano dell'integrazione con ChatGPT e Claude (Wave 8, nessun codice).
- `.claude/context/regulations/`: fonti normative verificate (indice `_index.md`).
- `Sessions/risanamento/DOMANDE-APERTE.md`: domande aperte e azioni da fare prima del deploy.
