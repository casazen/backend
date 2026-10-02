# Riepilogo finale del risanamento CasaZen (2026-10-02)

Chiusura del piano di risanamento nato dall'audit del 2026-09-23 ([`../audit-2026-09-23/`](../audit-2026-09-23/README.md)). Documento scritto dal task FN-05 sullo stato registrato in [`stato/status.json`](./stato/status.json), [`stato/notes.log`](./stato/notes.log) e nella storia git dei tre repo. Ogni affermazione cita un task, un commit (`be@` backend, `fe@` frontend, `mo@` mobile), un runbook (in `docs/runbooks/`) o un test; dove non c'è una prova, lo dice.

**Come leggere "fatto".** Significa: codice e test sul branch di integrazione `claude/sleepy-edison-oil8ru` (o già su `develop` per la prima ondata). **Non** significa "in produzione" né "provato su un ambiente reale": le configurazioni dei servizi esterni (Auth0, Stripe, Supabase, Resend, Vercel, EAS) non sono state applicate da nessun agente (decisione D9) e sono elencate in [`DOMANDE-APERTE.md`](./DOMANDE-APERTE.md) § 1 e in `docs/runbooks/deploy-checklist.md`.

Documenti collegati: stato per difetto in [`../PIANO-RISANAMENTO-2026-09.md`](../PIANO-RISANAMENTO-2026-09.md) § 9, stato per task in [`PIANO-ESECUZIONE.md`](./PIANO-ESECUZIONE.md), stato per spec in [`../specs/README.md`](../specs/README.md), indice dei runbook in [`../../docs/runbooks/index.md`](../../docs/runbooks/index.md).

---

## 1. Numeri

| | |
|---|---|
| Difetti dell'audit | 335 (46 P0, 149 P1, 140 P2): **327 chiusi, 8 parziali, 0 aperti** (A7-30 e A8-30 si chiudono con questo task) |
| Task del piano | 166: 163 `done` (FN-05 incluso), 3 `partial` (RS-7, CO-13, CO-22) |
| Task aggiunti in corso d'opera | `PL14-SUBP`, `LEGAL-TEXTS`, `FIX-DEPLOYCHK`: `done` |
| Wave 8 (assistenti AI) | 12 task `AI-01..AI-12`, tutti `pending` (solo pianificati) |
| Prima ondata (24-25 settembre) | già su `develop`: PR casazen/backend#451, casazen/frontend#209, casazen/mobile#5 |
| Seconda ondata (questa sessione, dal 2026-10-01) | solo su `claude/sleepy-edison-oil8ru`: backend 120 commit non-merge (43 task), frontend 60 (30 task), mobile 7 (6 task) rispetto a `origin/develop` |

Il conteggio dei commit è `git log --no-merges origin/develop..HEAD` nei tre repo al 2026-10-02.

---

## 2. Cosa è stato fatto, per area

### Integrazione e ricerche (PR, RS)
- **PR-1, PR-2, PR-3**: integrate le PR Cursor su booking/Stripe, LTR, compliance/auth, risolvendo i conflitti.
- **RS-1..RS-9**: ricerche su fonti ufficiali con esito in `.claude/context/regulations/` (CIN, Alloggiati, sicurezza D.L. 145/2023, Openapi.it/firma elettronica in `docs/integrations/rli-esign.md`, regole fiscali e OSS, canone concordato, ISTAT). Il dataset dei comuni ISTAT (21/02/2026, 7.894 comuni) lo ha fornito il PO. RS-7 (tariffe imposta di soggiorno) è parziale, vedi § 3.

### Fondamenta tecniche (FD) e confine tenant (TN)
- **CI vera**: frontend con lint, typecheck, vitest e build obbligatori (FD-01, `ci-frontend.md`); backend con gate sui pacchetti vulnerabili e rimozione di SendGrid (FD-02, `ci-backend.md`); mobile con tsc ed export (FD-03); test d'integrazione su PostgreSQL reale (FD-04, nota in `AGENT-PROTOCOL.md`).
- **Contratto errori e localizzazione** (FD-05, FN-01): ProblemDetails con `code` stabile e messaggi da `.resx` IT/EN; il test `SharedResourcesLocalizationTests` fa fallire una chiave mancante.
- **Date** (FD-06): UTC normalizzato dall'infrastruttura, "oggi" in Europe/Rome (`RomeCalendar`, `CalendarTodayArchitectureTests`).
- **File e chiavi** (FD-07): Supabase Storage con bucket pubblico e privato, chiavi DataProtection persistite (`storage.md`).
- **Altro**: client HTTP frontend unico (FD-08), i18n con test di parità (FD-09), rate limiting per IP reale (FD-10, `proxy-ip.md`), Hangfire con schema per ambiente (FD-11, `hangfire.md`), health check reali con SHA del commit (FD-12, `health-checks.md`), servizio email unico su Resend (FD-13, `email.md`), Auth0 Management affidabile (FD-14, `auth0.md`), sanitizzazione HTML (FD-15), fetch anti-SSRF (FD-16, `external-fetch.md`), CORS e header di sicurezza (FD-17, `cors-security-headers.md`), gating dei piani (FD-18, #274), pulizia file di scarto (FD-19), feature flag (FD-20, `feature-flags.md`: API OTA partner e discovery AI spente, D10 e D11), discovery AI e budget (FD-21, `ai.md`).
- **Tenant** (TN-1..TN-4): ospite per org con migrazione (`guest-tenant-migration.md`), filtro automatico `ITenantOwned` con test architetturale (`tenant-child-orgid-migration.md`), autorizzazione per risorsa e policy uniformi, contesto asincrono e slot del piano atomici.

### Piattaforma (PL)
Onboarding senza vicoli ciechi (PL-01), consensi imposti dal backend (PL-02, `onboarding-consents.md`), utente disattivato davvero bloccato (PL-03), impostazioni org con slug e email pubblica (PL-04), org fornitore separata dall'org host (PL-05), tipo operatore e step consensi (PL-06), enum senza 500 (PL-07), rimosso lo stub `/api/auth/register` (PL-08), admin con audit CIN, multi-ruolo e paginazione SQL (PL-09), webhook Stripe idempotenti (PL-10, `stripe.md`), configurazione billing per ambiente (PL-11), frontend del billing (PL-12), IVA con Stripe Tax e provider SDI configurabile (PL-13, `billing-tax.md`), documenti legali e sub-responsabili veritieri (PL-14 e `PL14-SUBP`, `legal-documents.md`), checklist di attivazione con "sito pubblicato" reale (PL-15, `activation-checklist.md`), piano per i locatori solo-LTR (PL-16). `LEGAL-TEXTS`: bozze di ToS, Privacy e DPA in IT/EN, versione `2026-10-v1`, **non attive** e da far rivedere a un legale.

### Proprietà, calendario, prezzi (PC)
Prenotazioni manuali dell'host confermate (PC-01), PUT con semantica PATCH (PC-02), pausa/attiva (PC-03), galleria foto su storage (PC-04), soft delete (PC-05), indirizzo univoco per org (PC-06, `property-address.md`), ciclo di vita prenotazione da web (PC-07), calendario con range corretto (PC-08), blocchi manuali (PC-09), iCal robusto con parser fuori da "Spike" (PC-10), multi-feed con URL cifrata (PC-11), export corretto (PC-12), UI iCal (PC-13), liste senza N+1 (PC-14), "Suggerimenti stagionali" al posto dei "Prezzi AI" (PC-15, D4), KPI reali della dashboard (PC-16). Runbook: `ical.md`, `seasonal-suggestions.md`.

### Prenotazione diretta, pagamenti, siti pubblici (BK)
Checkout con esito reale (BK-01, BK-07), rimborsi e cancellazioni reali su Connect (BK-02), tassa di soggiorno unica su `TouristTaxRate` con calcolatore pubblico (BK-03, `tourist-tax-rates.md`), pagamento su prenotazione cancellata (BK-04), disponibilità pubblica (BK-05), "Paga in struttura" sempre approvata dall'host (BK-06, D5, `direct-booking.md`), addebito differito (BK-08), Connect senza reset (BK-09), email transazionali (BK-10), "Le mie prenotazioni" (BK-11), branding, temi e contrasto AA (BK-12, BK-13), privacy e termini dell'operatore (BK-14), SEO indicizzabile di `/book` e `/p` (BK-15), sottodominio e dominio custom con Vercel Domains API (BK-16, BK-17, `seo-domain.md`), nessun ospite orfano (BK-18), webhook tolleranti alla versione API (BK-19), ricerca pubblica (BK-20), scadenza delle prenotazioni Pending (BK-21).

### Fornitori (SU)
Invito e registrazione self-serve (SU-01), claim solo con email verificata (SU-02), tassonomia unica (SU-03, `service-categories.md`), anagrafica comuni ISTAT con picker (SU-04, `comuni-istat.md`), attivazione con requisiti reali e ToS versionata (SU-05), stati d'errore e i18n (SU-06), richiesta legata a prenotazione o proprietà (SU-07, D2), dettaglio incarico (SU-08), timeline host completa (SU-09), concorrenza e DTO (SU-10), eliminati `SupplierJob` e check-in QR (SU-11, D12), admin fornitori (SU-12), vetrina v0 (SU-13), email univoca (SU-14), sync calendario su Hangfire (SU-15), codice morto (SU-16). Runbook: `suppliers.md`.

### Compliance (CO)
Formato CIN ufficiale unico (CO-01, `cin-format.md`), portale ospite (CO-02), tassa di soggiorno nel wizard (CO-03), cockpit con link veri (CO-04), wizard di attivazione (CO-05), stato compliance rivalutato (CO-06, `compliance.md`), checklist D.L. 145/2023 (CO-07), "Registra arrivo" (CO-08), fallback host (CO-09), alert deduplicati (CO-10), Alloggiati con stato onesto e N ospiti per soggiorno (CO-11, CO-12, `alloggiati.md`), file tracciato (CO-13, parziale), cifratura PII (CO-14, `encryption.md`), GDPR (CO-15, `gdpr.md`), rimosso `/api/checkin` (CO-16), check-out a 5 step (CO-17), regole fiscali STR (CO-18), UI fiscale e PDF (CO-19), alert CIN (CO-20), soggiorno OTA da blocco iCal (CO-21, D7).

### App mobile (MO)
Login Auth0 nativo (MO-01, `auth0.md`), EAS per profilo (MO-02, `mobile-release.md`), push reali (MO-03, MO-04), sessione con refresh (MO-05), calendario (MO-06), errori espliciti (MO-07), dettaglio prenotazione (MO-08), check-out rapido (MO-09), richiesta fornitore (MO-10), share link (MO-11), proprietà per ruolo (MO-12), i18n e testID (MO-13).

### Affitti lunghi (LT, decisione D1: feature attiva)
Registrazione RLI manuale onesta con provider spento (LT-01), firma offline con upload (LT-02), gate di approvazione dei template (LT-03, `lease-contract-templates.md`), scadenza RLI corretta (LT-04, `rli.md`), accesso dei locatori solo-LTR (LT-05), canone ricorrente su Stripe Connect (LT-06), checklist Questura (LT-07), advisory fiscale (LT-08), libreria PDF (LT-09), canone concordato (LT-10, LT-13, `canone-concordato.md`), stati e DTO senza PII (LT-11), anonimizzazione (LT-12), più parti per contratto (LT-14), test del flusso reale (LT-15).

### SEO (SE)
Revisione legale reale (SE-01), dominio canonico configurabile (SE-02, `seo-domain.md`), funnel con attribuzione (SE-03), immobili in evidenza ed eventi senza PII (SE-04, `seo-funnel.md`), i18n, cache e avviso AI Act (SE-05).

### Chiusura (FN)
Messaggi backend su `.resx` (FN-01), rimozione di 26 file frontend morti e `knip` in CI (FN-02), Golden Journey L3 da UI con attori distinti (FN-03, `golden-journey-l3.md`), suite Maestro con job su emulatore (FN-04, `mobile-e2e.md`), registro spec e documenti allineati (FN-05, questo documento). Checklist di deploy con test di coerenza `DeployChecklistConsistencyTests` (task DEPLOY-CFG, commit `docs(DEPLOY-CFG)`, es. be@3ee4dc64; `deploy-checklist.md`). `FIX-DEPLOYCHK` è `done` in `status.json` ma non ha descrizione nel registro: dal nome è una correzione di questo controllo.

---

## 3. Cosa resta aperto e perché

### Difetti dell'audit non chiusi del tutto (8, tutti parziali)
| Difetto | Perché |
|---|---|
| **A1-22** (P1) | PL-04 ha consegnato impostazioni org, slug e email pubblica. Manca lo step "Nome attività" nell'onboarding e l'aggiornamento del customer Stripe già creato. Non fatto per scelta prudente (domanda in DOMANDE-APERTE, PL-04). |
| **A2-17** (P1) | PC-14 ha eliminato l'N+1 (una query filtrata per HostScope). La paginazione server-side di `GET /api/bookings` non c'è: sposterebbe filtri e KPI sul server. |
| **A7-29** (P1) | LT-15 ha consegnato flusso reale su Postgres, matrice dei coefficienti ed e2e dei contratti. La copertura con coverlet e le soglie non sono state aggiunte (FN-05 è solo documentazione). |
| **A6-04, A6-23, A6-24, A3-23** (P0/P1) | FN-03: il Golden Journey L3 con host, fornitore, ospite e admin distinti è stato provato **in locale 5 volte** (circa 55 s), solo nella variante "paga in struttura". Il job GitHub Actions e la variante con carta Stripe di test **non sono mai girati**: servono i segreti GitHub `BACKEND_REPO_TOKEN`, `FRONTEND_REPO_TOKEN`, `STRIPE_TEST_SECRET_KEY`, `STRIPE_TEST_PUBLISHABLE_KEY` (`golden-journey-l3.md`). Finché non è verde in CI non è un gate. |
| **A6-22** (P1) | FN-04: i flow Maestro (login reale, schermate per testID) e il job su emulatore Android **non sono mai stati eseguiti** (qui non c'è emulatore). Verificati in locale solo `npm run e2e:check`, tsc, eslint, jest, actionlint, `expo prebuild`. Maestro `1.41.0` è una versione non verificata; push e login Auth0 reale restano controlli manuali (`mobile-e2e.md`). |

### Punti pianificati non svolti (non sono difetti dell'audit)
- **CO-13 — web service Alloggiati non implementato (partial).** Il file tracciato è generato (be@c0f81730, fe@ee5e034); manca il client SOAP. Motivi: invio irreversibile e senza sandbox; decisioni di prodotto mancanti (stato "inviato, ricevuta in attesa", duplicati, invio automatico); il "File Unico" non è generato perché il manuale non specifica il padding. Le specifiche vengono da **copie su GitHub** dei manuali Rev. 01 del 24/01/2022 (il portale della Polizia è bloccato dalla rete): confrontare l'SHA-256 con un download ufficiale prima dell'uso (`alloggiati.md`). L'invio resta manuale (D6).
- **CO-22 — ISTAT (#6) e Ross1000/portali regionali (#8): bloccato.** Le specifiche ufficiali non sono verificabili (`flussituristici.servizirl.it`, `regione.lombardia.it` bloccati, WSDL compreso). Nessun codice scritto, per non inventare tracciati. Cosa è noto e cosa manca: `ross1000.md` (be@0096dcec). Servono i PDF ufficiali o lo sblocco dei domini, più prerequisiti nel modello (CIR separato, residenza codificata, capogruppo, disponibilità giornaliera) e un parere sul ruolo GDPR per le credenziali.
- **RS-7 — tariffe dell'imposta di soggiorno non verificate alla fonte (partial).** I domini dei comuni sono bloccati: i dati vengono da estratti di ricerca, livello U/D/T riga per riga in `.claude/context/regulations/imposta_soggiorno.md` e nel CSV `Casazen.Infrastructure/Data/Seeds/tourist-tax/rates.csv`. Per Seveso e Cesano Maderno non è stata trovata alcuna imposta; Torino è da fonte terza; Bologna (percentuale) e Venezia (per gruppo catastale) richiedono il modello esteso di BK-03. Aprire i PDF ufficiali da una rete libera e confermare ogni riga prima di presentarle come ufficiali.
- **Assistenti AI (AI-01..AI-12)**: solo piano (`docs/integrations/ai-assistants-plan.md`); partono dopo le risposte del PO alle domande della sezione 8 di DOMANDE-APERTE. Nota: i fatti OpenAI e Auth0 del piano sono estratti di ricerca non riverificati sulle pagine ufficiali (rete bloccata).

### Dipendenze esterne e decisioni che bloccano il go-live
- **Hosting.** Il PO ha cancellato Railway (2026-10-02). Il database resta su Supabase (decisione: non SQLite/Turso, troppe dipendenze da PostgreSQL); l'analisi dell'hosting gratuito del backend è del task HOSTING (`docs/runbooks/free-hosting-analysis.md`, non ancora presente). Tutti i riferimenti a Railway in `docs/INFRA.md` e nei runbook sono storici; il merge su `develop` attiva i deploy nativi del provider, che oggi potrebbe non esistere.
- **Fattura elettronica (SDI)**: nessun provider integrato; le fatture restano `manual_required` e il checkout si apre solo con `Billing__VatNumber` e `Sdi__ManualIssuanceAccepted=true` (`billing-tax.md`).
- **Testi legali**: le bozze `2026-10-v1` non sono attive; mancano i dati societari (`Legal__Controller__*`, `Legal__Terms__GoverningCourt`) e la revisione di un legale; i dati dei sub-responsabili vanno riconfrontati con le pagine ufficiali (`legal-documents.md`). Periodi di conservazione dei dati ancora "da definire" (anche `Gdpr__Retention__LeaseParties__Years`, spenta: nessuna scadenza automatica).
- **Provider RLI e firma elettronica**: spenti dietro `RliProvider` e `ESignProvider` (D15), nessun client reale; il flusso manuale è quello di default.
- **Verifica della suite**: la suite backend completa non è stata rieseguita su DEPLOY-CFG (tre tentativi interrotti dal limite di 30 minuti con la macchina sotto carico, nota VERIFICA in DOMANDE-APERTE). Va lanciata una volta sul branch di integrazione prima del merge.
- **Frontend**: `npm run knip` è ora bloccante in CI; `tsc -p tsconfig.e2e.json` ha 14 errori preesistenti non legati a FN-02.
- **Residui di localizzazione**: circa 370 attributi DataAnnotations (in gran parte entità Core non esposte) usano ancora i messaggi di default del framework; i messaggi dei webhook OTA restano in inglese perché rispondono a macchine (FN-01).

### Migrazioni e operazioni che toccano dati (verificare prima del deploy)
`SupplierJobs` viene eliminata (SU-11: conteggio ed export in `suppliers.md` § 10.3); PL-05 converte sul posto le org con dati host e separa il lato fornitore (`suppliers.md` § 13); PC-06 rinomina i duplicati d'indirizzo (`dup-<id>`) e azzera le coordinate fuori range (`property-address.md`); TN-1 e TN-2 separano gli ospiti condivisi e riempiono `OrgId` (`guest-tenant-migration.md`, `tenant-child-orgid-migration.md`); SU-03 migra le categorie (`service-categories.md`); CO-14 cifra colonne (`encryption.md`, serve `DataProtection__CertificatePfxBase64`). Eliminare a fine lavori i DB `it_*` residui degli esperimenti.

---

## 4. Decisioni e domande aperte

**Decisioni vincolanti del PO**: D1-D15 in [`DECISIONI.md`](./DECISIONI.md), con l'aggiornamento di D14 del 2026-10-01 (bozze dei testi legali delegate a un agente, da far validare). Decisioni successive: SendGrid sostituito da Resend nelle regole di progetto `.claude/rules/integrations.md` e `infra.md` (OK del 2026-10-01); riferimenti a SendGrid restano solo in `.claude/skills/council-*` e `.claude/agents/platform-launch/`, non toccati; hosting come sopra.

**Domande aperte**: la fonte unica è [`DOMANDE-APERTE.md`](./DOMANDE-APERTE.md), non duplicata qui:
- § 1 azioni da fare prima del deploy (variabili, Auth0, Supabase, Hangfire, tabelle Alloggiati, CI);
- § 2 domande per il commercialista, § 3 domande legali;
- § 4 decisioni di prodotto per area (check-in, prenotazioni, calendario, LTR, fornitori, piattaforma, mobile, SEO);
- § 8 domande emerse nelle sessioni di ripresa (una voce per task: PL-04, PL-05, PL-14, BK-12, BK-13, BK-14, PC-03, PC-05, PC-06, PL-13, CO-13, CO-22, FN-03, FN-04, SU-05, SU-13, FN-01, …) e le domande bloccanti AI-*.

Le più rilevanti per sbloccare il rilascio: chiavi Stripe live e `Billing__*`, dati societari e revisione legale, periodi di conservazione GDPR, regione dei dati (tenant Auth0 EU o US), provider SDI, conferma della conversione delle org nel passaggio PL-05 e dei dati modificati da PC-06, decisione sul client Alloggiati, sblocco o PDF ufficiali per Ross1000, hosting del backend.

---

## 5. Come portare tutto su `develop`

Regole: [`.claude/rules/github-flow-mandatory.md`](../../.claude/rules/github-flow-mandatory.md). **Nessun push o merge diretto su `develop` o `main`; le PR si aprono verso `develop` e si aspetta l'approvazione.** Questo documento non apre PR: elenca i passi.

Il lavoro della seconda ondata sta nei tre repo sul branch `claude/sleepy-edison-oil8ru` (già pubblicato su `origin` dagli script di integrazione). Il branch precedente `claude/app-analysis-fixes-plan-p0mx0a` è già su `develop`.

### Prima di aprire le PR
1. Sul branch di integrazione del backend, con `source /home/user/wt/bin/env.sh` (PostgreSQL locale): `dotnet build Casazen.sln -c Release`, `dotnet test Casazen.sln -c Release` (una sola volta, 9+ minuti), `dotnet format --verify-no-changes`. Frontend: `npx tsc -b --noEmit`, `npm run lint`, `npm run knip`, `npx vitest run`, `npx vite build`. Mobile: `npx tsc --noEmit`, lint, jest.
2. Creare nei repo i segreti GitHub per i job nuovi (FN-03: `BACKEND_REPO_TOKEN` nel frontend, `FRONTEND_REPO_TOKEN` nel backend, chiavi Stripe di test in entrambi; FN-04: `BACKEND_REPO_TOKEN` e `FRONTEND_REPO_TOKEN` in mobile). Senza token i job falliscono in modo esplicito; senza chiavi Stripe la variante è saltata con avviso. Non rendere i due check obbligatori finché non sono verdi (`golden-journey-l3.md`, `mobile-e2e.md`).
3. Decidere l'hosting del test (task HOSTING): un push su `develop` fa partire i deploy nativi del provider.

### Ordine delle PR: backend, poi frontend, poi mobile
Il frontend e il mobile usano endpoint e codici errore nuovi del backend, e le migrazioni EF vengono applicate dal backend: il backend deve essere su `develop` per primo.

```bash
# 1. backend (casazen/backend)
git fetch origin && git checkout claude/sleepy-edison-oil8ru
gh pr create --base develop --head claude/sleepy-edison-oil8ru \
  --title "feat: remediation wave 2 (backend)" --body-file <corpo, vedi sotto>
# STOP: attendere approvazione e CI verde, poi merge della PR (mai push diretto)

# 2. frontend (casazen/frontend): dopo il merge del backend
gh pr create --base develop --head claude/sleepy-edison-oil8ru ...

# 3. mobile (casazen/mobile): dopo il merge del frontend
gh pr create --base develop --head claude/sleepy-edison-oil8ru ...
```

Corpo di ogni PR (requisiti del flusso): **Summary** (cosa e perché: i task della seconda ondata di quel repo, elencati nel § 1), **Test Plan** (comandi sopra e verifiche manuali del § 3), `Closes #X`. Usare `Closes` solo per le issue davvero chiuse e `Refs` per le altre: per esempio #274, #273, #51, #58, #15, #16 sono coperte; #300 (SEO), #269 (canone ricorrente LTR), #230 (billing, SDI manuale), #6 e #8 (CO-22) restano parziali o bloccate e **non** vanno chiuse.

### Dopo il merge sul backend
Verificare CI e `GET /api/health/ready` dell'ambiente di test (campo `commit` = SHA del merge), applicare le migrazioni, eseguire le query pre e post-deploy dei runbook citati nel § 3. Per i job nuovi: dopo alcuni run verdi, aggiungere come required i check del Golden Journey e della suite Maestro.

### Rilascio in produzione
Solo con una release PR `develop` → `main` dopo la verifica sull'ambiente di test di tutte le funzionalità dell'epica e la conferma esplicita `confirm release vX.Y.Z` con CI verde; il tag `vX.Y.Z` sul `main` serve solo al changelog e non fa deploy (`.claude/rules/github-flow-mandatory.md`, `.claude/rules/infra.md`). Backend e frontend di una funzionalità si promuovono insieme (release bundle in `Sessions/bundle-<epic>.md`). Prima del `main`: tutto il § 1 di DOMANDE-APERTE e `docs/runbooks/deploy-checklist.md`.
