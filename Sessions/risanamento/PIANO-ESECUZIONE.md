# Piano di esecuzione del risanamento

> Generato dal registro [`tasks.json`](./tasks.json). Ogni difetto dell'audit ([`../audit-2026-09-23/`](../audit-2026-09-23/README.md)) è assegnato ad almeno un task. Le decisioni del product owner sono in [`DECISIONI.md`](./DECISIONI.md); il protocollo seguito da ogni agente è in [`AGENT-PROTOCOL.md`](./AGENT-PROTOCOL.md).

**166 task** coprono **335 difetti** (46 P0, 149 P1, 140 P2), più i punti pianificati mai sviluppati (soggiorno OTA da iCal, ISTAT, portali regionali, generazione Alloggiati).

## Come viene eseguito

1. **Un agente per task**, ciascuno in un proprio git worktree sul branch `fix/<ID>`, partendo dall'ultimo stato del branch di integrazione `claude/app-analysis-fixes-plan-p0mx0a`.
2. Un task parte **solo quando le sue dipendenze sono integrate**. Al massimo 5 agenti lavorano in contemporanea (limite di CPU della macchina).
3. Prima di integrare, ogni agente riporta nel proprio branch l'ultimo stato di integrazione, risolve i conflitti e riesegue **build e test completi**. Pubblica (fast-forward e push) **solo se tutto è verde**.
4. Commit con scope = ID del task (es. `fix(PC-01): …`): la storia git è il registro di avanzamento.
5. Le domande di prodotto emerse durante i task non vengono decise dagli agenti: vengono raccolte e poste al product owner.

## Ordine di esecuzione

Il livello indica la profondità nel grafo delle dipendenze: i task dello stesso livello possono procedere in parallelo.

| Livello | Task |
|---|---|
| 0 | PR-1, PR-2, PR-3, RS-1, RS-2, RS-3, RS-4, RS-5, RS-6, RS-7, RS-8, RS-9, FD-03, FD-08, PL-08, CO-04 |
| 1 | FD-01, FD-02, FD-04, FD-05, FD-06, FD-07, FD-10, FD-11, FD-14, FD-15, FD-16, FD-18, FD-19, FD-20, BK-11, BK-21, BK-19, SU-16, CO-01, CO-07, CO-18, MO-02, LT-01, LT-03, LT-05, LT-07, LT-08, LT-09, LT-10, LT-13 |
| 2 | FD-09, FD-12, FD-13, FD-17, FD-21, TN-1, TN-4, PL-01, PL-03, PL-07, PL-09, PL-10, PL-16, PC-01, PC-02, PC-05, PC-08, PC-10, PC-15, BK-03, SU-03, SU-04, SU-10, SU-14, SU-15, CO-02, CO-03, CO-06, CO-11, CO-19, MO-01, MO-03, MO-07, LT-02, LT-04, SE-01 |
| 3 | TN-2, PL-02, PL-04, PL-05, PL-06, PL-11, PL-13, PC-03, PC-04, PC-06, PC-07, PC-09, PC-11, PC-12, PC-14, PC-16, BK-01, BK-05, BK-06, BK-08, BK-18, SU-01, SU-05, SU-11, CO-05, CO-09, CO-10, CO-12, CO-14, CO-15, CO-16, CO-20, CO-22, MO-04, MO-05, MO-06, LT-11, LT-15, SE-02, SE-05 |
| 4 | TN-3, PL-12, PL-14, PL-15, PC-13, BK-07, BK-12, SU-02, SU-06, SU-12, SU-13, CO-08, CO-13, MO-11, MO-13, LT-12, LT-14, SE-03 |
| 5 | BK-02, BK-09, BK-13, BK-14, BK-15, BK-16, BK-20, SU-07, CO-21, MO-08, MO-12, SE-04 |
| 6 | BK-04, BK-10, BK-17, SU-08, SU-09, CO-17, MO-10, LT-06 |
| 7 | MO-09, FN-03 |
| 8 | FN-01, FN-02, FN-04, FN-05 |

## PR — Integrazione PR Cursor

| ID | Task | Difetti | Sev. | Repo | Dipende da |
|---|---|---|---|---|---|
| **PR-1** | Integrare PR Cursor booking/Stripe (#424 #438 #441 #442 #443 #444 #446 #447 #448 #450)<br><sub>Rivedere ciascuna PR (branch origin/cursor/*), integrarla se corretta, risolvere i conflitti (ordine suggerito 442,448,438,447,450,443,446,441,444,424). Copre parzialmente A1-09 A3-03 A3-13 A3-14 A2-01 A3-04 A2-08: non chiuderli qui, solo integrare.</sub> | — | — | backend | — |
| **PR-2** | Integrare PR Cursor LTR (#427 #429 #430 #432 #433 #434 #435 #436 #439 #440 #445 #449)<br><sub>LTR resta feature attiva (decisione D1): integrare dopo review. Conflitti attesi su LeaseWorkflowService.</sub> | — | — | backend | — |
| **PR-3** | Integrare PR Cursor compliance/auth (#425 #426 #428 #431 #437)<br><sub>#431 (snapshot ospite) e' fix provvisorio di A2-02; la fix definitiva e' TN-1.</sub> | — | — | backend | — |

## RS — Ricerche su fonti ufficiali

| ID | Task | Difetti | Sev. | Repo | Dipende da |
|---|---|---|---|---|---|
| **RS-1** | Ricerca specifiche ufficiali Alloggiati Web (tracciato, tabelle codici, web service)<br><sub>Solo fonti ufficiali (alloggiatiweb.poliziadistato.it, manuali PDF). Esito in .claude/context/regulations/alloggiati.md con link e data. Se tabelle/tracciato/WS non sono pubblicamente disponibili: scriverlo esplicitamente.</sub> | — | — | backend | — |
| **RS-2** | Ricerca formato ufficiale del CIN (D.L. 145/2023, BDSR Ministero del Turismo)<br><sub>Aggiornare .claude/context/regulations/cin.md e .claude/rules/compliance.md con formato verificato + fonti. Se non trovato: dichiararlo e proporre il formato permissivo IT+6 cifre ISTAT+10 alfanumerici.</sub> | — | — | backend | — |
| **RS-3** | Ricerca obblighi di sicurezza D.L. 145/2023 art. 13-ter (rilevatori gas/CO, estintori)<br><sub>Aggiornare .claude/context/regulations/sicurezza.md con testo di legge e fonti (Normattiva/Gazzetta Ufficiale).</sub> | — | — | backend | — |
| **RS-4** | Ricerca API pubbliche Openapi.it (registrazione RLI) e provider firma elettronica<br><sub>Documentazione pubblica, sandbox, autenticazione. Esito: fattibile/non fattibile senza contratto. Scrivere docs/integrations/rli-esign.md.</sub> | — | — | backend | — |
| **RS-5** | Ricerca regole fiscali: IVA OSS e Stripe Tax, SDI, cedolare/ritenuta OTA con P.IVA, soglia 3 immobili, imposta di registro LTR<br><sub>Fonti ufficiali (Agenzia Entrate, EUR-Lex, docs Stripe). Aggiornare .claude/context/regulations/fiscale.md.</sub> | — | — | backend | — |
| **RS-6** | Dataset ufficiale comuni ISTAT (codice ISTAT, codice catastale, provincia, regione)<br><sub>Scaricare l'elenco comuni da istat.it (CSV ufficiale) e salvarlo in Casazen.Infrastructure/Data/Seeds/ con fonte e data. Nessun dato scritto a mano.</sub> | — | — | backend | — |
| **RS-7** | Tariffe imposta di soggiorno dei comuni pilota da fonti ufficiali<br><sub>Comuni presenti nel registro/seed SEO (Milano, Roma, Como, Firenze, Napoli, Torino, Venezia, Bologna, Seveso, Cesano Maderno...): tariffe per categoria extralberghiera/locazione breve, esenzioni per eta', tetto notti, con URL e data. File dati in Seeds + imposta_soggiorno.md.</sub> | — | — | backend | — |
| **RS-8** | Verifica accordi territoriali canone concordato Seveso e Cesano Maderno<br><sub>Confrontare Sessions/research-canone-concordato-mb.md con i testi ufficiali degli accordi: fasce, coefficienti, tetti, durate. Aggiornare canone_concordato.md con fonti.</sub> | — | — | backend | — |
| **RS-9** | Ricerca ISTAT movimentazione turistica (#6) e portali regionali/CIR (#8)<br><sub>Fattibilita': formati e canali ufficiali per regione pilota (Lombardia). Esito documentato; implementazione solo se c'e' uno standard pubblico.</sub> | — | — | backend | — |

## FD — Fondamenta tecniche

| ID | Task | Difetti | Sev. | Repo | Dipende da |
|---|---|---|---|---|---|
| **FD-01** | CI frontend reale: lint/typecheck/vitest/build obbligatori, e2e.yml valido, eslint a 0 errori<br><sub>Anche parte FE di A9-24 e A6-04 (rimuovere continue-on-error e job echo Maestro).</sub> | A9-23 | P1 | frontend | PR-1 |
| **FD-02** | CI backend: gate pacchetti vulnerabili, aggiornamento pacchetti, rimozione SendGrid e gate echo<br><sub>Parte BE di A9-24 (e2e-golden-journey.yml echo).</sub> | A9-26 | P1 | backend | PR-1, PR-2, PR-3 |
| **FD-03** | CI mobile (tsc, eslint configurato, expo export)<br><sub>Anche la parte CI di A6-30.</sub> | A9-24 | P1 | mobile | — |
| **FD-04** | Test d'integrazione su PostgreSQL reale + test migrazioni<br><sub>Factory usa TEST_POSTGRES_CONNECTION (locale/CI service) o Testcontainers; isolamento per classe; test apply-all + HasPendingModelChanges; aggiungere servizio postgres a ci-cd.yml.</sub> | A9-11 A9-35 A2-37 A1-43 | P1 | backend | PR-1, PR-2, PR-3 |
| **FD-05** | Contratto errori (ProblemDetails + code) e localizzazione backend funzionante<br><sub>InvalidOperationException non piu' 503 con messaggio interno; UnauthorizedAccessException -> 403; DomainException/NotFoundException; fix percorso SharedResources; helper FE per ProblemDetails e' in FD-08.</sub> | R-07 A9-07 A9-08 A1-24 A7-19 A9-09 | P1 | backend | PR-1, PR-2, PR-3 |
| **FD-06** | Date e fusi: normalizzazione UTC globale, 'oggi' in Europe/Rome | R-01 A9-12 A2-15 A5-17 A7-05 | P0 | backend | PR-1, PR-2, PR-3 |
| **FD-07** | Storage oggetti Supabase (S3) + chiavi DataProtection persistite + download documenti autenticato<br><sub>Decisione D9: Supabase Storage via API S3; bucket pubblico foto, privato documenti con URL firmati. Fallback filesystem SOLO in Development. Runbook docs/runbooks/storage.md.</sub> | A2-03 A9-04 A2-31 | P0 | backend, frontend | PR-1, PR-2, PR-3 |
| **FD-08** | Client HTTP frontend unico: flag public, 401/403, retry, parser ProblemDetails, niente fetch relativi<br><sub>Per A4-16 solo la parte fetch relativo (supplier-showcase); supplier-check-in viene rimosso in SU-11.</sub> | A9-20 A9-21 A9-22 A4-16 | P1 | frontend | — |
| **FD-09** | i18n frontend: parita' it/en, plurali, messaggi Zod tradotti, test di parita' + lint<br><sub>Anche parte FE di A3-31, A5-32, A4-27 (le stringhe dei moduli toccati da altri task verranno ricontrollate da FN-02).</sub> | A9-25 R-08 A2-28 A7-25 A1-25 | P1 | frontend | FD-01 |
| **FD-10** | Rate limiting per IP reale + UseForwardedHeaders (#273) | A1-12 A3-12 A3-30 A5-10 A9-10 A8-22 A3-41 | P1 | backend | PR-1, PR-2, PR-3 |
| **FD-11** | Hangfire: schema per ambiente + DisableConcurrentExecution sui job critici<br><sub>Runbook: verifica tabella hangfire.server su Supabase.</sub> | A9-03 | P0 | backend | PR-1, PR-2, PR-3 |
| **FD-12** | Health check reali + validazione configurazione all'avvio + documentazione variabili Stripe | A9-19 A3-24 | P1 | backend | FD-05 |
| **FD-13** | Servizio email unico: opzioni validate, niente mittente resend.dev forzato, template IT/EN con encoding, invio accodato<br><sub>Rimuovere HttpEmailService/SmtpEmailService/EmailQueueProcessor inutilizzati se non servono; link sempre da App:PublicSiteBaseUrl (dominio configurabile, D3).</sub> | A9-06 A9-16 A4-06 A4-07 A4-08 A5-33 A4-20 | P0 | backend | FD-05 |
| **FD-14** | Auth0 Management affidabile: token client-credentials in cache, ruoli solo additivi, esito propagato, membership DB, cache claim<br><sub>Runbook docs/runbooks/auth0.md (M2M, Action con email/name/email_verified, client Native).</sub> | A1-02 A4-01 A1-29 A4-30 A9-31 | P0 | backend | PR-1, PR-2, PR-3 |
| **FD-15** | Sanitizzazione HTML ad allowlist (BE HtmlSanitizer + FE DOMPurify)<br><sub>A9-29: solo la parte sanitizer; header in FD-17.</sub> | A8-08 A9-29 | P1 | backend, frontend | PR-1 |
| **FD-16** | Fetch sicuro di URL esterni (anti-SSRF) per iCal property e fornitore | A2-21 A4-10 A9-32 | P1 | backend | PR-1 |
| **FD-17** | CORS ristretto, header di sicurezza (HSTS, CSP, frame-ancestors), niente PII/token nei log<br><sub>A9-29 parte header; A2-32 solo la parte log dei claim (DTO GetById in PC-02).</sub> | A3-29 A9-28 A1-34 A9-36 A2-32 | P2 | backend, frontend | FD-10 |
| **FD-18** | Gating piani (#274): upgrade solo con subscription, tier effettivo ovunque | A1-03 A3-07 A9-02 A3-37 A1-41 | P0 | backend, frontend | PR-1, PR-2, PR-3 |
| **FD-19** | Pulizia file spazzatura e codice morto evidente (.bak, .idea, playwright-report, classi vuote) | A9-34 A3-36 | P2 | backend, frontend | PR-1, PR-2, PR-3 |
| **FD-20** | Feature flag + API OTA partner nascoste (menu, rotte, job, endpoint)<br><sub>Decisione approvata. Anche parte OTA di A8-16 e A9-15 (job ota-sync-all/booking-pull-all). R-10: parte webhook OTA (e availability 404 in BK-05).</sub> | A2-09 A9-17 R-10 A8-16 | P1 | backend, frontend | PR-1, PR-2, PR-3 |
| **FD-21** | Discovery AI fornitori spenta + guard budget/rate limit AI + GDPR prompt<br><sub>Decisione approvata: flag OFF. Anche parte AI di A8-16.</sub> | A8-01 A8-07 A8-14 A8-15 A4-26 A8-26 | P0 | backend, frontend | FD-20 |

## TN — Confine tenant e autorizzazione

| ID | Task | Difetti | Sev. | Repo | Dipende da |
|---|---|---|---|---|---|
| **TN-1** | Ospite per tenant: Guest.OrgId, migrazione che separa gli ospiti condivisi, filtro, DTO | A9-01 A2-02 A5-07 A2-35 A1-28 | P0 | backend, frontend | FD-04, FD-05 |
| **TN-2** | Filtro tenant automatico (ITenantOwned) + OrgId sulle entita' figlie esposte<br><sub>Parte residua di A1-28 e sezione 1 di A9 (12 entita' senza filtro). Test architetturale su ogni DbSet.</sub> | — | — | backend | TN-1 |
| **TN-3** | Autorizzazione resource-based e policy di contesto uniformi (Guests, Gdpr, PricingAdapter, Payments, ServiceRequests)<br><sub>Rinominare policy PropertyOwner (oggi = autenticato).</sub> | A3-38 | P2 | backend | TN-2 |
| **TN-4** | TenantContext asincrono + SetOrgId dopo provisioning + race primo accesso + slot piano atomico | A1-20 A1-14 A1-21 | P2 | backend | FD-04 |

## PL — Piattaforma: accesso, onboarding, billing, admin, legale

| ID | Task | Difetti | Sev. | Repo | Dipende da |
|---|---|---|---|---|---|
| **PL-01** | Onboarding senza vicoli ciechi (admin, ruoli senza org, errore transitorio) + demo mode | A1-01 A1-19 A1-18 A9-38 | P0 | frontend, backend | FD-05, FD-14 |
| **PL-02** | Consensi imposti dal backend (UserRole.None, 403 onboarding_required) + gating mobile<br><sub>A1-33: parte gating; refresh token in MO-05.</sub> | A1-05 A1-33 | P1 | backend, mobile, frontend | PL-01, TN-4 |
| **PL-03** | Utente disattivato davvero bloccato (API + Auth0 + pagina FE) | A1-04 | P1 | backend, frontend | FD-14 |
| **PL-04** | Impostazioni organizzazione: nome, slug leggibile modificabile, email contatto pubblica opt-in | A1-22 A1-23 | P1 | backend, frontend | FD-14, TN-4 |
| **PL-05** | Org fornitore separata dall'org host | A1-40 | P1 | backend | TN-4 |
| **PL-06** | Modifica tipo operatore, step consensi con retry, pagina piano senza promise non gestite | A1-15 A1-39 A1-38 | P1 | frontend | PL-01 |
| **PL-07** | Validazione enum (niente 500 su valori numerici) | A1-35 A7-31 | P2 | backend | FD-05 |
| **PL-08** | Rimuovere POST /api/auth/register stub | A1-30 A9-27 | P2 | backend, frontend | — |
| **PL-09** | Admin: rotta audit CIN, multi-ruolo con audit, stati errore, paginazione SQL | A1-16 A1-17 A1-26 A1-27 | P1 | backend, frontend | FD-14 |
| **PL-10** | Webhook Stripe idempotenti (platform e Connect) + billing: niente subscription duplicate, stati incomplete fail-closed | A1-09 A3-03 A1-10 A1-11 | P0 | backend | PR-1, FD-18 |
| **PL-11** | Billing: configurazione ambienti (Staging, URL ritorno, price id, allow-list) + tenant Auth0 separati (runbook) | A1-31 A1-32 | P2 | backend | FD-12 |
| **PL-12** | Billing frontend: piani, checkout, portale, paese e P.IVA, stato | A1-07 | P1 | frontend | PL-10, PL-11 |
| **PL-13** | IVA/OSS corretta con Stripe Tax + provider SDI configurabile<br><sub>Nessuna aliquota scritta a mano: Stripe Tax o tabella da fonte ufficiale RS-5.</sub> | A1-08 | P1 | backend | PL-10, RS-5 |
| **PL-14** | Documenti legali: pagine /legale, versioni, ri-accettazione, sub-responsabili veritieri<br><sub>Testi ToS/Privacy/DPA: NON inventarli. Infrastruttura che li legge da file/config; finche' mancano la pagina mostra 'in preparazione' e il consenso usa la versione dichiarata. L'elenco sub-responsabili va reso veritiero dal codice (Resend, Auth0 con regione reale del tenant, Expo, DeepSeek se attivo, Vercel, Railway, Supabase, Stripe).</sub> | A1-06 A9-40 | P1 | backend, frontend | PL-02 |
| **PL-15** | Checklist di attivazione PLG + flag 'sito pubblicato' reale (anche chargesEnabled)<br><sub>Anche PLG-AC10.</sub> | A1-37 A3-26 | P2 | backend, frontend | PL-04 |
| **PL-16** | Piano e billing gestibili dai landlord solo-LTR | A1-36 | P2 | backend, frontend | FD-18 |

## PC — Proprietà, calendario, iCal, prezzi

| ID | Task | Difetti | Sev. | Repo | Dipende da |
|---|---|---|---|---|---|
| **PC-01** | Prenotazioni manuali host: Confirmed + BookingSource.Manual, mai auto-annullate | A2-01 | P0 | backend, frontend | PR-1, FD-04 |
| **PC-02** | Property: PUT con semantica PATCH, campi mancanti nel form, tipi allineati, DTO GetById leggero | A2-04 A2-27 A2-32 | P1 | backend, frontend | FD-05 |
| **PC-03** | Pausa/attiva property con endpoint dedicato e lista che mostra le proprieta' in pausa | A2-05 | P1 | backend, frontend | PC-02 |
| **PC-04** | Galleria foto property (upload, ordine, cancellazione) su storage oggetti | A2-26 | P1 | frontend, backend | FD-07, PC-02 |
| **PC-05** | Soft delete property con vincoli su dati fiscali e prenotazioni future | A2-18 | P1 | backend | FD-04 |
| **PC-06** | Indice indirizzo per org con interno + precisione coordinate | A2-19 A2-33 | P1 | backend, frontend | FD-04, PC-02 |
| **PC-07** | Ciclo di vita prenotazione da web: DTO di modifica, conferma, annulla, CTA, filtro per proprieta'<br><sub>Il check-in ('Registra arrivo') e' in CO-08. L'annullo con rimborso usa BK-02.</sub> | A2-07 A2-08 A2-30 | P1 | backend, frontend | PC-01, TN-1 |
| **PC-08** | Calendario web: range corretto e refetch alla navigazione | A2-06 | P1 | frontend | FD-06 |
| **PC-09** | Blocchi manuali di date (owner, manutenzione) | A2-25 | P1 | backend, frontend | PC-08 |
| **PC-10** | iCal robusto: feed vuoto valido, errori isolati per feed, parser fuori da 'Spike', STATUS/RRULE/all-day | A2-10 A2-12 A2-23 A9-13 | P1 | backend | FD-16 |
| **PC-11** | iCal multi-feed per proprieta' (Airbnb + Booking) con URL cifrata | A2-11 A2-20 | P1 | backend, frontend | PC-10, FD-07 |
| **PC-12** | Export iCal corretto (VALUE=DATE, niente eco, niente SUMMARY sensibili, token rigenerabile) | A2-22 | P2 | backend | PC-10 |
| **PC-13** | UI iCal: errori leggibili, sincronizza ora, scollega | A2-24 | P2 | frontend | PC-11 |
| **PC-14** | Performance liste prenotazioni e ospiti (niente N+1) | A2-17 | P1 | backend | TN-1 |
| **PC-15** | Prezzi: 'Suggerimenti stagionali' senza AI finta, basati sul prezzo reale, scheduling corretto<br><sub>Decisione approvata: rinomina.</sub> | A2-14 A2-34 A8-17 | P1 | backend, frontend | FD-06 |
| **PC-16** | Dashboard host: KPI reali per periodo e widget iCal al posto di OTA | A2-29 A2-36 | P2 | backend, frontend | FD-20, PC-10 |

## BK — Prenotazione diretta, pagamenti, siti pubblici, domini

| ID | Task | Difetti | Sev. | Repo | Dipende da |
|---|---|---|---|---|---|
| **BK-01** | Checkout pubblico: passaggio widget->checkout, campi data, precompilazione, validazioni, parametri deep link | A3-01 R-04 A3-34 A3-28 | P0 | frontend | FD-09 |
| **BK-02** | Rimborsi e cancellazioni reali su Stripe Connect (#51), niente 'process payment' finto<br><sub>A9-15: parte pagamenti. Policy di cancellazione -> importo rimborsabile.</sub> | A3-05 A9-15 | P0 | backend, frontend | PR-1, TN-3 |
| **BK-03** | Tassa di soggiorno unica su TouristTaxRate + endpoint preventivo + esenzioni + calcolatore pubblico | A3-02 R-05 A5-16 A8-12 A8-23 | P0 | backend, frontend | FD-06, RS-7 |
| **BK-04** | Pagamento arrivato su prenotazione cancellata: riconferma o rimborso automatico | A3-04 | P0 | backend | PR-1, BK-02 |
| **BK-05** | Disponibilita' pubblica per id con blocchi iCal e 404 su property non pubblicate | A3-09 A2-13 R-03 A9-39 | P1 | backend, frontend | PC-10 |
| **BK-06** | 'Paga in struttura' sempre soggetta ad approvazione dell'host<br><sub>Decisione: la prenotazione OnSite resta 'in attesa di approvazione' finche' l'host non accetta; se accetta e' valida. Dubbio da non inventare: durata dell'attesa prima della scadenza -> renderla configurabile e segnalarla.</sub> | A3-06 R-11 | P0 | backend, frontend | PC-01, FD-13 |
| **BK-07** | Esito checkout reale, gestione redirect Stripe, errori per codice, opzioni di pagamento corrette | A3-15 A3-16 | P1 | frontend, backend | BK-01, BK-03 |
| **BK-08** | Addebito differito robusto (OffSession, stato PI, notifiche, kind nel webhook) | A3-14 | P1 | backend | PR-1, FD-13 |
| **BK-09** | Stripe Connect: niente reset su errori transitori, idempotency key, policy admin, URL server-side | A3-19 A3-42 | P1 | backend, frontend | TN-3 |
| **BK-10** | Email transazionali prenotazione (#58): conferma, ricevuta, cancellazione, rimborso + push host | A3-11 | P1 | backend | FD-13, BK-02 |
| **BK-11** | 'Le mie prenotazioni' con codice prenotazione | A3-10 R-06 | P1 | frontend, backend | FD-08 |
| **BK-12** | Branding del sito: API e pagina 'Aspetto sito' (logo, hero, colori, tagline, tema) | A3-17 | P1 | backend, frontend | FD-07, PL-04 |
| **BK-13** | Temi reali, font, contrasto AA, stati d'errore della landing | A3-18 A3-32 A3-35 | P2 | frontend | BK-12 |
| **BK-14** | Pagine privacy e termini dell'operatore sul sito pubblico | A3-21 | P1 | frontend, backend | PL-14, BK-12 |
| **BK-15** | SEO indicizzabile per /book e /p: prerender, meta, JSON-LD, sitemap per org, robots | A3-20 A8-29 A8-09 A8-20 | P1 | frontend, backend | BK-12, SE-02 |
| **BK-16** | Sottodominio e dominio custom: route pubblica/middleware e CORS dinamico | A3-08 | P1 | frontend, backend | FD-17, BK-12 |
| **BK-17** | Domini: Vercel Domains API, verifica CNAME, ricontrollo, step onboarding, runbook | A3-25 | P1 | backend, frontend | BK-16 |
| **BK-18** | Niente ospiti orfani con PII sui tentativi falliti | A3-33 | P2 | backend | TN-1 |
| **BK-21** | Job di scadenza delle prenotazioni Pending (annulla PI/SI) + esclusione scaduti da disponibilita' ed export iCal | A3-13 | P1 | backend | PR-1 |
| **BK-19** | Webhook tolleranti alla API version + niente ApplicationFeeAmount=0 | A3-39 A3-40 | P2 | backend | PR-1 |
| **BK-20** | Ricerca pubblica /search funzionante (link, NaN, shell pubblica) | A3-27 A8-13 | P1 | frontend, backend | BK-12 |

## SU — Fornitori e micro-marketplace

| ID | Task | Difetti | Sev. | Repo | Dipende da |
|---|---|---|---|---|---|
| **SU-01** | Invito fornitore verso la SPA + registrazione self-serve (token legato all'email, comuni pilota, rate limit) | A4-03 A4-04 A4-21 A4-24 | P0 | backend, frontend | FD-13, FD-10 |
| **SU-02** | Claim del profilo fornitore dopo il login + onboarding fornitore; collegamento solo con email verificata o invito | A4-02 A4-23 A1-13 | P0 | backend, frontend | SU-01, FD-14, PL-01 |
| **SU-03** | Tassonomia unica delle categorie di servizio (BE, web, app) + migrazione dati | A4-05 A6-03 | P0 | backend, frontend, mobile | FD-04 |
| **SU-04** | Anagrafica comuni ISTAT completa + picker + ComuneIstatCode/RegionCode su property e fornitore | A4-12 A5-34 A8-24 A5-19 | P1 | backend, frontend | RS-6, FD-04 |
| **SU-05** | Attivazione fornitore con requisiti reali + wizard 5 step persistito + ToS versionata | A4-09 A4-31 | P1 | backend, frontend | SU-03, SU-04 |
| **SU-06** | Pagine fornitore: stati d'errore e i18n | A4-25 A4-27 | P2 | frontend, backend | SU-05 |
| **SU-07** | Richiesta fornitore legata alla prenotazione (STR) o alla proprieta' (LTR), parita' web/app (#340)<br><sub>Decisione D2: affitti brevi -> bookingId obbligatorio quando c'e' un soggiorno; affitti lunghi -> proprieta'. Aggiornare #340.</sub> | A4-13 A4-33 | P1 | backend, frontend, mobile | SU-03, TN-3 |
| **SU-08** | Fornitore vede indirizzo, data e contatto + pagina dettaglio incarico + storico | A4-14 | P1 | backend, frontend | SU-07 |
| **SU-09** | Timeline host completa, conferma 'Segna pagato', motivo rifiuto, richiedi ad altro fornitore | A4-28 | P2 | frontend, backend | SU-07, FD-13 |
| **SU-10** | ServiceRequest: concorrenza (xmin), validazione DTO, niente 500 evitabili | A4-17 A4-18 A4-19 | P2 | backend | FD-05, FD-04 |
| **SU-11** | Eliminare SupplierJob e check-in QR; KPI fornitore su ServiceRequest<br><sub>Decisione approvata.</sub> | A4-15 | P1 | backend, frontend | SU-10 |
| **SU-12** | Admin fornitori: elenco, sospensione, inviti; transizioni solo per fornitori Active | A4-29 | P2 | backend, frontend | SU-01 |
| **SU-13** | Vetrina pubblica fornitore v0 (slug generato, shell pubblica, anteprima)<br><sub>#303 minimo; parte fetch gia' in FD-08.</sub> | A4-16 | P1 | backend, frontend | FD-08, SU-05 |
| **SU-14** | fix-orphaned sicuro + email fornitore univoca | A4-22 | P2 | backend | FD-04 |
| **SU-15** | Sync calendario fornitore via Hangfire invece di Task.Run | A4-11 A9-14 | P1 | backend | FD-16 |
| **SU-16** | Codice morto supplier-shell + pagina aiuto iCal dentro la console (#327) | A4-32 | P2 | frontend | FD-08 |

## CO — Compliance: CIN, check-in, Alloggiati, GDPR, fiscale

| ID | Task | Difetti | Sev. | Repo | Dipende da |
|---|---|---|---|---|---|
| **CO-01** | Formato CIN ufficiale unico (BE+FE), migrazione dati, regole di progetto | A5-05 R-02 | P0 | backend, frontend | RS-2 |
| **CO-02** | Portale ospite: campo sesso, errori per campo, prefill mascherato dopo il completamento | A5-04 A5-28 | P0 | frontend, backend | FD-05 |
| **CO-03** | Step tassa di soggiorno come avviso + create tariffe admin + seed da fonti ufficiali | A5-06 A8-28 | P0 | backend, frontend | FD-06, RS-7 |
| **CO-04** | Link del cockpit verso rotte esistenti | A5-09 | P1 | backend, frontend | — |
| **CO-05** | Wizard attivazione property: checklist precompilata, blocker del 409 mostrati, step navigabili | A5-18 | P1 | frontend | CO-01, CO-03 |
| **CO-06** | Rivalutazione dello stato compliance + Suspended + ricalcolo property storiche | A5-20 A5-36 | P1 | backend | CO-01 |
| **CO-07** | Checklist sicurezza conforme a D.L. 145/2023 (con 'non applicabile') | A5-21 | P1 | backend, frontend | RS-3 |
| **CO-08** | 'Registra arrivo' (check-in) da web e app + start/complete del check-out allineati | A5-08 | P0 | backend, frontend, mobile | PC-07 |
| **CO-09** | Fallback host per i dati ospite: form Alloggiati, link copiabile, invio link sempre possibile, scadenza sessioni | A5-26 A5-27 | P1 | backend, frontend | CO-02 |
| **CO-10** | Alert deduplicati + reminder check-out via email | A5-11 A6-07 A5-25 | P1 | backend | FD-13 |
| **CO-11** | Alloggiati: stato onesto, schedulazione all'arrivo (Europe/Rome), idempotenza, niente reinvio doppio<br><sub>A5-35: riscrivere i test compliance che certificano simulazioni o mock divergenti. Decisione: se RS-1 trova specifiche pubbliche implementare file/WS in CO-13; qui solo stato onesto + riepilogo dati per ospite.</sub> | A5-01 A5-03 A5-37 A9-05 A5-35 | P0 | backend, frontend | FD-06, RS-1 |
| **CO-12** | Modello a N ospiti per soggiorno (StayGuest) + tabelle codici<br><sub>Tabelle codici solo da fonte ufficiale RS-1; altrimenti campi strutturati + validazione e segnalazione.</sub> | A5-02 | P0 | backend, frontend | TN-1, RS-1 |
| **CO-13** | Alloggiati: generazione file tracciato e/o client WS se le specifiche ufficiali sono disponibili<br><sub>Solo se RS-1 conferma specifiche pubbliche; altrimenti chiudere documentando il motivo.</sub> | — | — | backend, frontend | CO-11, CO-12, CO-14 |
| **CO-14** | Cifratura PII ospite e credenziali Questura (con UI credenziali in sola scrittura) | A5-30 | P1 | backend, frontend | FD-07, TN-1 |
| **CO-15** | GDPR: anonimizzazione ed export completi, retention per categoria, consensi con versione, niente consenso marketing dall'host | A5-12 A5-13 A5-14 A5-15 A9-18 | P1 | backend, frontend | TN-1, FD-07 |
| **CO-16** | Rimuovere il portale check-in legacy /api/checkin | A5-29 A9-30 | P2 | backend | CO-02 |
| **CO-17** | Check-out wizard a 5 step (fornitore pulizie, tassa riscossa, property pronta) + cockpit su stati reali | A5-24 | P1 | backend, frontend | CO-08, SU-07, CO-11 |
| **CO-18** | Regole fiscali STR corrette (soglia per contribuente, ritenuta vs P.IVA, IRPEF ordinaria) | A5-22 | P1 | backend | RS-5 |
| **CO-19** | UI fiscale completa + report e PDF tabellari in italiano | A5-23 | P1 | frontend, backend | CO-18, LT-09 |
| **CO-20** | Alert CIN reale e messaggi dopo la scadenza | A5-31 | P2 | backend, frontend | FD-13, CO-01 |
| **CO-21** | Soggiorno OTA creato dall'host da un blocco iCal (check-in ospite, Alloggiati, cockpit)<br><sub>Decisione: si'. Copre GC-AC9 (punto pianificato mai sviluppato).</sub> | — | — | backend, frontend | PC-11, CO-08, TN-1 |
| **CO-22** | ISTAT (#6) e portali regionali (#8): implementazione solo se RS-9 trova standard pubblici | — | — | backend, frontend | RS-9, SU-04 |

## MO — App mobile host

| ID | Task | Difetti | Sev. | Repo | Dipende da |
|---|---|---|---|---|---|
| **MO-01** | Login Auth0 nativo (client Native), rimozione iniezione token e2e, demo solo in __DEV__<br><sub>Runbook Auth0 client Native.</sub> | A6-01 A6-21 A6-31 | P0 | mobile | FD-14 |
| **MO-02** | Configurazione EAS per profilo, niente fallback localhost, asset e icone | A6-02 A9-37 A6-30 | P0 | mobile | FD-03 |
| **MO-03** | Push: registrazione reale, deviceId stabile, cold start dal tap | A6-05 A6-06 A6-19 | P1 | mobile, backend | MO-02 |
| **MO-04** | Push dal backend: notifiche mancanti (rifiuto, nuova prenotazione, nuova richiesta), invio asincrono a batch | A6-08 A6-29 | P1 | backend | FD-13 |
| **MO-05** | Sessione app: refresh token, logout completo, 401 vs 403<br><sub>Anche la parte refresh di A1-33.</sub> | A6-14 A6-15 | P1 | mobile, backend | MO-01, FD-05 |
| **MO-06** | Calendario app: selettore proprieta', blocchi iCal, range corretto, navigazione | A6-10 A6-11 A6-12 A2-16 | P1 | mobile | PC-10 |
| **MO-07** | App: errori espliciti, ProblemDetails, offline graceful | A6-13 A6-26 A6-25 | P1 | mobile | FD-05 |
| **MO-08** | Dettaglio prenotazione app: stato check-in ospite, badge compliance, pull-to-refresh, riepilogo compliance<br><sub>Anche CW-AC15.</sub> | A6-17 | P1 | mobile, backend | CO-08 |
| **MO-09** | Check-out rapido app con start e riepilogo | A6-16 | P1 | mobile | CO-17 |
| **MO-10** | Richiesta fornitore da app con scelta del fornitore | A6-18 | P1 | mobile | SU-07 |
| **MO-11** | Share link con slug dell'org | A6-09 A3-22 | P1 | mobile, backend | PL-04 |
| **MO-12** | Proprieta' visibili per ruolo/org (property manager) | A6-20 | P1 | backend | TN-3 |
| **MO-13** | App: i18n IT/EN, accessibilita', testID, testi | A6-27 A6-28 A6-32 | P2 | mobile | MO-06, MO-07 |

## LT — Affitti lunghi (LTR)

| ID | Task | Difetti | Sev. | Repo | Dipende da |
|---|---|---|---|---|---|
| **LT-01** | Registrazione RLI: integrazione reale se documentata (RS-4), altrimenti flusso manuale onesto; atomicita' | A7-01 A7-21 | P0 | backend, frontend | PR-2, RS-4 |
| **LT-02** | Firma contratto: provider reale se documentato, altrimenti firma offline con upload; link firmatari persistiti; webhook con controllo stato | A7-02 A7-16 A7-20 | P0 | backend, frontend | PR-2, RS-4, FD-07 |
| **LT-03** | Template contratto: gate di approvazione reale (Approved=false di default) e contenuto da fonte legale<br><sub>Non scrivere clausole legali: struttura + segnaposto non pubblicabili finche' non forniti.</sub> | A7-03 | P0 | backend | PR-2 |
| **LT-04** | Scadenza RLI corretta (stipula/decorrenza) + promemoria a intervalli su tutti gli stati | A7-04 | P0 | backend | PR-2, RS-5, FD-06 |
| **LT-05** | Landlord solo-LTR: accesso agli immobili e all'APE nel contesto long-rent<br><sub>Verificare se #434 (PR-2) lo chiude gia'.</sub> | A7-06 | P0 | backend, frontend | PR-2 |
| **LT-06** | Canone ricorrente (#269): scadenziario, job, UI e incasso su Stripe Connect | A7-07 | P1 | backend, frontend | BK-02, LT-09 |
| **LT-07** | Checklist Questura extra-UE con azione esplicita e termine 48h | A7-08 | P1 | backend, frontend | PR-2 |
| **LT-08** | Advisory fiscale LTR corretto (ATA, base 70%, minimo imposta di registro) | A7-09 | P1 | backend | RS-5, RS-8 |
| **LT-09** | Libreria PDF (A4, a capo, Unicode) per contratti e report | A7-14 | P1 | backend | PR-2 |
| **LT-10** | Canone concordato: fasce contigue, tetti coefficienti, durata per tipologia, range validato lato server, dati Partial segnalati | A7-10 A7-11 A7-12 A7-13 A7-23 | P1 | backend, frontend | RS-8, PR-2 |
| **LT-11** | LTR: etichette stati, errori, DTO senza PII, stringhe tradotte | A7-15 A7-17 A7-26 A7-27 | P2 | backend, frontend | PR-2, FD-09 |
| **LT-12** | Anonimizzazione Party su lease scaduti (#179) + retention su data di fine | A7-18 | P1 | backend | CO-15 |
| **LT-13** | Dati normativi LTR su DB (destinatari IMU, soglie) + pulsante IMU solo quando pertinente | A7-22 A7-24 | P2 | backend, frontend | RS-8, PR-2 |
| **LT-14** | Contratti con piu' locatori/conduttori + validazione codice fiscale | A7-28 | P2 | backend, frontend | LT-11 |
| **LT-15** | Test LTR: flusso reale, matrice coefficienti, E2E lease (FE #83) | A7-29 | P2 | backend, frontend | LT-10, LT-01, LT-02 |

## SE — SEO e funnel

| ID | Task | Difetti | Sev. | Repo | Dipende da |
|---|---|---|---|---|---|
| **SE-01** | SEO: revisione legale reale (niente auto-approve), rigenerazione in Draft, contenuti stub ritirati, UX admin | A8-04 A8-05 A8-06 R-09 A8-21 | P1 | backend, frontend | FD-15 |
| **SE-02** | Dominio canonico configurabile: canonical, sitemap e robots sul dominio del frontend<br><sub>Decisione D3: nessun dominio hardcoded, solo variabili d'ambiente.</sub> | A8-02 | P0 | backend, frontend | FD-12 |
| **SE-03** | Funnel: rotta /signup con attribuzione (comune, UTM) e CTA funzionanti | A8-03 | P0 | frontend, backend | SE-02, PL-01 |
| **SE-04** | Immobili in evidenza per comune + SeoEvent senza PII + widget admin (#300 AC2 AC3 AC8 AC9) | A8-10 A8-11 A8-18 | P1 | backend, frontend | SE-03, BK-12 |
| **SE-05** | SEO: i18n, cache AI limitata, avviso trasparenza AI Act montato | A8-19 A8-25 A8-27 | P2 | frontend, backend | SE-01 |

## FN — Chiusura: sweep finali, Golden Journey in CI, documentazione

| ID | Task | Difetti | Sev. | Repo | Dipende da |
|---|---|---|---|---|---|
| **FN-01** | Messaggi backend residui su IStringLocalizer (.resx IT/EN)<br><sub>Anche parte BE di A1-25 e A4-27: sweep finale dopo i task di feature.</sub> | A3-31 A5-32 | P2 | backend | tutti gli altri task |
| **FN-02** | Rimozione componenti FE morti + knip in CI | A9-33 | P2 | frontend | tutti gli altri task |
| **FN-03** | Golden Journey L3 da UI in CI su stack effimero (host, fornitore, guest distinti; Stripe test) + F1-F2 | A6-04 A6-23 A6-24 A3-23 | P0 | frontend, backend | CO-17, SU-09, BK-07 |
| **FN-04** | Maestro reale (login, push, assert per ID) + seed app + job CI su emulatore | A6-22 | P1 | mobile | FN-03, MO-03 |
| **FN-05** | Registro spec, PLANNING e documentazione allineati allo stato reale | A7-30 A8-30 | P2 | backend | tutti gli altri task |

## Indice: difetto → task

| Difetto | Sev. | Descrizione | Task |
|---|---|---|---|
| A1-01 | P0 | Vicolo cieco nell'onboarding per chi ha ruoli Auth0 ma nessuna org (admin inclusi) | PL-01 |
| A1-02 | P0 | Sincronizzazione ruoli Auth0 con token M2M statico ed errori silenziosi | FD-14 |
| A1-03 | P1 | Upgrade del piano gratuito (#274) e piano scelto all'onboarding senza pagamento | FD-18 |
| A1-04 | P1 | Utente disattivato ancora operativo | PL-03 |
| A1-05 | P1 | Consensi ToS, Privacy e DPA aggirabili | PL-02 |
| A1-06 | P1 | Documenti legali inesistenti ed elenco sub-responsabili non veritiero | PL-14 |
| A1-07 | P1 | Frontend del SaaS billing completamente assente | PL-12 |
| A1-08 | P1 | IVA e OSS calcolate male, IVA non incassata | PL-13 |
| A1-09 | P1 | Idempotenza del webhook Stripe che perde eventi | PL-10 |
| A1-10 | P1 | Checkout che crea subscription duplicate | PL-10 |
| A1-11 | P1 | Mapping degli stati subscription fail-open | PL-10 |
| A1-12 | P1 | X-Forwarded-For falsificabile | FD-10 |
| A1-13 | P1 | Collegamento account tramite email non verificata | SU-02 |
| A1-14 | P2 | Race sul primo accesso. UserService.cs:127-141 e OrgService.cs:59-86 fanno check-then-insert. Due richieste pa… | TN-4 |
| A1-15 | P1 | Modifica del tipo operatore: piano ignorato in silenzio e step saltati | PL-06 |
| A1-16 | P1 | Pagina Admin CIN irraggiungibile e test e2e vuoto | PL-09 |
| A1-17 | P1 | Cambio ruolo admin distruttivo | PL-09 |
| A1-18 | P1 | Demo mode rotta fuori da Playwright | PL-01 |
| A1-19 | P2 | Errore transitorio su /users/me rimanda all'onboarding. In onboarding-guard.tsx:19-21 un 5xx o un timeout port… | PL-01 |
| A1-20 | P2 | TenantContext mette in cache OrgId=null prima del provisioning. TenantContext.cs:33-52 e OrgContextResolver.cs… | TN-4 |
| A1-21 | P2 | ReservePropertySlotAsync non riserva nulla. EntitlementService.cs:37-66 apre una transazione serializable, con… | TN-4 |
| A1-22 | P1 | Identità org e utente vuota, nessuna impostazione org | PL-04 |
| A1-23 | P2 | Slug dell'org derivato dal sub Auth0 e non modificabile. Con OrgService.cs:142-157 l'URL pubblico diventa /boo… | PL-04 |
| A1-24 | P2 | Il middleware errori espone dettagli interni. ErrorHandlingMiddleware.cs:84-97 restituisce ex.Message per Inva… | FD-05 |
| A1-25 | P2 | Violazioni i18n. | FD-09 |
| A1-26 | P2 | Pagine admin senza stati di errore e con filtri incompleti. admin-users-page.tsx:18 ignora isError; admin-jobs… | PL-09 |
| A1-27 | P2 | Statistiche e CIN admin caricano tutte le proprietà in memoria. AdminService.cs:45,105 usano ToListAsync() sul… | PL-09 |
| A1-28 | P2 | Invariante RF1 violata o parziale. Guest non ha OrgId; GuestsController.cs:28-37 carica tutti gli ospiti della… | TN-1 |
| A1-29 | P2 | Membership legacy mai revocate e LastUsedContextKey mai scritto. Il seed in 20260604193911_AddContextAuthoriza… | FD-14 |
| A1-30 | P2 | POST /api/auth/register anonimo e stub. AuthController.cs:13-46 crea utenti fantasma (Id casuale, password ign… | PL-08 |
| A1-31 | P2 | Configurazione billing inutilizzabile in test e produzione. | PL-11 |
| A1-32 | P2 | Stesso tenant Auth0 per preview e produzione. secrets/vercel.variables.example.json:5-7: un ruolo assegnato da… | PL-11 |
| A1-33 | P2 | Mobile senza refresh token e senza gating. MOB/src/auth/AuthProvider.tsx:99-127 salva il refresh token ma non … | PL-02 |
| A1-34 | P2 | Hygiene di sicurezza FE e Hangfire. FE/lib/axios.ts:50-56 logga il prefisso del token e fa debug verboso in pr… | FD-17 |
| A1-35 | P2 | Validazione degli enum. Enum.TryParse accetta stringhe numeriche (UsersController.cs:129,171): rentalType:"7" … | PL-07 |
| A1-36 | P2 | Piano non gestibile per i landlord LTR-only. Il badge punta a una route short-rent (org-badge.tsx:19, entitlem… | PL-16 |
| A1-37 | P2 | Milestone sitePublished sempre vera. OnboardingService.cs:119-122: basta una proprietà attiva, perché Org.IsAc… | PL-15 |
| A1-38 | P2 | Pagina piano: promise non gestita e 409 generico. In plan-settings-page.tsx:19-23 un errore di mutateAsync las… | PL-06 |
| A1-39 | P2 | Step consensi senza retry e versioni obsolete non gestite. consents-step.tsx:30-38: se un documento legale non… | PL-06 |
| A1-40 | P1 | Utente fornitore che diventa host: proprietà create nell'org fornitore | PL-05 |
| A1-41 | P2 | PATCH piano admin ignora Stripe. AdminController.cs:107-130 modifica il piano anche con una subscription attiv… | FD-18 |
| A1-43 | P2 | Test che non verificano nulla di significativo. | FD-04 |
| A2-01 | P0 | La prenotazione creata dall'host nasce Status=Pending, Source=Direct e non esiste un endpoint per confermarla.… | PC-01 |
| A2-02 | P0 | Guest non è per tenant: una prenotazione manuale riusa il record Guest di un'altra org trovato per email. | TN-1 |
| A2-03 | P0 | Foto e documenti finiscono sul filesystem del container Railway (effimero; nessun volume documentato in docs/I… | FD-07 |
| A2-04 | P1 | Il PUT sovrascrive tutti i campi. Il form FE non invia cleaningFee, damageDeposit, houseRules, timezone, cance… | PC-02 |
| A2-05 | P1 | 1) "Pausa/Attiva" invia solo {isActive} e il BE risponde 400 (Name/Address/City obbligatori): il pulsante non … | PC-03 |
| A2-06 | P1 | Il range è calcolato una sola volta (mese corrente) e react-big-calendar non rifà la query quando si naviga. t… | PC-08 |
| A2-07 | P1 | Il PUT fa il binding dell'entità EF. Con Nullable enable (Casazen.Core.csproj:6) le navigation Property/Org/Gu… | PC-07 |
| A2-08 | P1 | Da web l'host non può confermare, fare il check-in né annullare. L'API di check-in fallisce se usata dopo il g… | PC-07 |
| A2-09 | P1 | Le API OTA partner (#31-35, congelate) sono raggiungibili e rotte. Il FE chiama /ota, /ota/sync/all, /ota/sync… | FD-20 |
| A2-10 | P1 | Un feed valido senza eventi viene marcato Failure e l'import termina prima di rimuovere i blocchi orfani. | PC-10 |
| A2-11 | P1 | Un solo URL di import per proprietà. | PC-11 |
| A2-12 | P1 | Un DbUpdateException (SUMMARY >500 caratteri, UID duplicato nel feed, due run sovrapposti) fa fallire di nuovo… | PC-10 |
| A2-13 | P1 | La disponibilità pubblica considera solo le prenotazioni, non i CalendarBlock. Il FE le passa lo slug mentre l… | BK-05 |
| A2-14 | P1 | Il "prezzo AI" è uno stub: moltiplicatori fissi per mese e festivi; lo storico registra prezzi inventati; ness… | PC-15 |
| A2-15 | P1 | Npgsql ≥6 rifiuta i parametri DateTime con Kind Unspecified sulle colonne timestamptz. Stesso pattern latente … | FD-06 |
| A2-16 | P1 | L'app mescola blocchi e prenotazioni e mostra una sola proprietà. | MO-06 |
| A2-17 | P1 | GET /api/bookings (lista e dashboard) esegue una query per prenotazione e ciascuna carica la proprietà con tut… | PC-14 |
| A2-18 | P1 | DELETE /api/properties/{id} è una cancellazione fisica. Non esposta in UI ma pubblica via API. | PC-05 |
| A2-19 | P1 | Unicità dell'indirizzo su tutta la piattaforma e senza campo interno/scala. | PC-06 |
| A2-20 | P2 | ImportUrl in chiaro (la spec AC2 chiede cifratura). Gli URL Airbnb contengono un token segreto che dà accesso … | PC-11 |
| A2-21 | P2 | Fetch sincrono di un URL https arbitrario dentro la richiesta, senza limite di dimensione né blocco delle reti… | FD-16 |
| A2-22 | P2 | L'export non usa VALUE=DATE, riesporta i blocchi importati (effetto eco) con il loro SUMMARY su un URL pubblic… | PC-12 |
| A2-23 | P2 | Il parser ignora STATUS:CANCELLED e TRANSP:TRANSPARENT e non espande RRULE. Le date all-day passano da AsUtc e… | PC-10 |
| A2-24 | P2 | Mutation senza onError (rejection non gestita, nessun toast); il campo ricade su status.importUrl e non si svu… | PC-13 |
| A2-25 | P1 | Nessun blocco manuale di date (soggiorno del proprietario, manutenzione). | PC-09 |
| A2-26 | P1 | Upload, cancellazione e riordino foto hanno gli endpoint (PropertiesController.cs:338-488) ma nessuna UI. | PC-04 |
| A2-27 | P2 | Bagni con step 0,5 contro int nel BE (1,5 → 400 "JSON value could not be converted"); nessun monolocale (camer… | PC-02 |
| A2-28 | P2 | I messaggi di validazione dichiarati come chiavi i18n arrivano grezzi. | FD-09 |
| A2-29 | P2 | L'"occupazione" è calcolata come prenotazioni in check-in diviso numero di proprietà; il ricavo è cumulativo d… | PC-16 |
| A2-30 | P2 | Il link "Prenotazioni di questa proprietà" mostra tutte le prenotazioni; nessuna CTA di creazione; "Annulla" d… | PC-07 |
| A2-31 | P2 | I documenti della proprietà sono scaricabili senza autenticazione con URL permanenti. | FD-07 |
| A2-32 | P2 | PII (email, nome) nei log a ogni lista; payload GET /properties/{id} pesante con tutte le prenotazioni, token … | FD-17, PC-02 |
| A2-33 | P2 | Coordinate arrotondate a 0,01° (circa 1 km). | PC-06 |
| A2-34 | P2 | NextScheduledRunAt = now+1g e filtro <= now alla run successiva (sempre alle 02:00): basta un jitter per salta… | PC-15 |
| A2-35 | P2 | POST /api/guests risponde 409 "Guest with email X already exists" per email di qualsiasi org (enumerazione); l… | TN-1 |
| A2-36 | P2 | KPI prenotazioni: nextCheckIn conta le cancellate, gli arrivi di oggi (00:00Z) non sono né "upcoming" né "acti… | PC-16 |
| A2-37 | P2 | I test non possono rilevare A2-01, 07, 08, 12, 15, 18, 19. Nessun E2E importa un .ics reale né verifica AC13. | FD-04 |
| A3-01 | P0 | Il widget naviga a /checkout??checkIn=…&checkOut=…. URLSearchParams legge la chiave "?checkIn", quindi checkIn… | BK-01 |
| A3-02 | P0 | Il checkout legge la tabella TaxRates, che nessun codice, seed o migration popola (grep: nessun writer). L'adm… | BK-03 |
| A3-03 | P0 | L'evento è "claimato" (INSERT committato) prima della logica di business. Se la logica lancia un'eccezione, il… | PL-10 |
| A3-04 | P0 | Il Pending scade dopo 15' (appsettings.json DirectBooking:PendingTtlMinutes), ma il PaymentIntent resta valido… | BK-04 |
| A3-05 | P0 | RefundPaymentAsync aggiorna solo il DB ("TODO: Implement actual Stripe refund"). ProcessPaymentAsync "simula" … | BK-02 |
| A3-06 | P0 | L'opzione "Paga in struttura" è sempre offerta e conferma subito, senza carta a garanzia, senza verifica email… | BK-06 |
| A3-07 | P0 | PUT /api/orgs/me/plan permette a qualsiasi utente autenticato senza abbonamento di impostare Pro/Scale. Con Su… | FD-18 |
| A3-08 | P1 | Sottodominio e dominio custom non funzionano: (a) la root / è dentro ProtectedRoute; (b) *.casazen.it è consid… | BK-16 |
| A3-09 | P1 | La disponibilità è richiesta con lo slug (gli URL usano lo slug per default: org-landing-page.tsx:26,33) ma la… | BK-05 |
| A3-10 | P1 | Il FE invia solo {email}, la BE richiede anche bookingId ([Required]) e risponde 400. | BK-11 |
| A3-11 | P1 | Nessuna email al guest o all'host alla conferma (#58): sono stub con Task.Delay. Il template esistente non è m… | BK-10 |
| A3-12 | P1 | AddFixedWindowLimiter("PublicBookingCreate") non è partizionato: 10 richieste al minuto in totale su tutta la … | FD-10 |
| A3-13 | P1 | La scadenza dei Pending avviene solo quando qualcuno tenta di prenotare la stessa property. Non esiste un job … | BK-21 |
| A3-14 | P1 | Addebito differito: (a) il pagamento viene marcato Completed senza controllare paymentIntent.Status (requires_… | BK-08 |
| A3-15 | P1 | (a) "Prenotazione confermata!" è mostrata subito dopo confirmPayment; il polling di stato non aggiorna la UI e… | BK-07 |
| A3-16 | P1 | L'opzione "paga alla scadenza" è mostrata se nights > 7 invece che se l'arrivo è tra più di 7 giorni. "Cancell… | BK-07 |
| A3-17 | P1 | Branding non configurabile: nessun endpoint scrive LogoUrl, ThemeColor, HeroImageUrl, Tagline, PublicThemeId, … | BK-12 |
| A3-18 | P2 | I tre temi hanno token identici; font Fraunces/Inter dichiarati ma mai caricati (fallback Georgia/system-ui). | BK-13 |
| A3-19 | P1 | Qualsiasi StripeException in GetAccountAsync (rate limit, rete, chiave) viene trattata come account "stale": i… | BK-09 |
| A3-20 | P1 | SEO assente sui siti host: title "temp-vite", lang="en", nessun meta, OG, canonical o JSON-LD, nessuna sitemap… | BK-15 |
| A3-21 | P1 | I link Privacy e Termini puntano a https://casazen.app/privacy e /terms, che non esistono nella SPA: CatchAllR… | BK-14 |
| A3-22 | P1 | Il link di condivisione è ${webUrl}/book/${property.slug}/${property.id}: lo slug della property viene usato c… | MO-11 |
| A3-23 | P1 | Lo step 4 del GJ L3 usa POST /api/bookings autenticato dall'host: il checkout guest non è mai testato contro A… | FN-03 |
| A3-24 | P1 | INFRA elenca solo Stripe__SecretKey e Stripe__WebhookSecret. Mancano Stripe__ConnectWebhookSecret (senza, /web… | FD-12 |
| A3-25 | P1 | Solo verifica TXT: il CNAME non è verificato e il dominio non viene mai aggiunto al progetto Vercel (nessuna A… | BK-17 |
| A3-26 | P2 | Il sito è "pubblicato" appena esiste una property attiva, anche se ConnectChargesEnabled=false; l'avviso compa… | PL-15 |
| A3-27 | P2 | /search è un vicolo cieco (console.log) e usa AppShell della console. | BK-20 |
| A3-28 | P2 | Parametri checkIn/checkOut invece di checkin/checkout (contratto AC15/GVR). | BK-01 |
| A3-29 | P2 | Il CORS ammette qualsiasi https://*.vercel.app con AllowCredentials. | FD-17 |
| A3-30 | P2 | Il rate limit di resolve-host usa il primo valore di X-Forwarded-For (falsificabile). ConsentIpAddress salva l… | FD-10 |
| A3-31 | P2 | Violazione della regola i18n (IStringLocalizer/t()). Plurale costruito con suffisso "i": "3 nottei" in IT, "3 … | FN-01 |
| A3-32 | P2 | Contrasto della CTA 2.95:1 (bianco su #e07a5f), sotto WCAG AA. | BK-13 |
| A3-33 | P2 | Il guest con dati personali e consenso viene creato prima della validazione (date passate) e non viene cancell… | BK-18 |
| A3-34 | P2 | Adulti di default a 2, scollegati dal widget; paese fisso IT; nessuna validazione client di maxGuests, email o… | BK-01 |
| A3-35 | P2 | Un errore dell'API proprietà viene mostrato come "nessuna struttura pubblicata". Hero caricato in lazy chunk (… | BK-13 |
| A3-36 | P2 | Copia obsoleta con logica di idempotenza diversa (solo Platform). | FD-19 |
| A3-37 | P2 | ShowPoweredBy usa org.PlanTier salvato, non il tier effettivo (disdetta o past due oltre grace). | FD-18 |
| A3-38 | P2 | process e refund richiedono solo un utente autenticato (manca la policy payment.write). GetAll senza propertyI… | TN-3 |
| A3-39 | P2 | EventUtility.ConstructEvent con throwOnApiVersionMismatch=true (default Stripe.net 50.1). | BK-19 |
| A3-40 | P2 | ApplicationFeeAmount = 0 esplicito sulle direct charge: inutile e da verificare in test mode, perché Stripe po… | BK-19 |
| A3-41 | P2 | Nessun rate limit sulle GET pubbliche (org, properties, availability, status, search); l'availability rivela l… | FD-10 |
| A3-42 | P2 | Policy property.write invece di Org admin; returnUrl/refreshUrl scelti dal client. | BK-09 |
| A4-01 | P0 | AssignRoleAsync legge i ruoli Auth0 dell'utente e li rimuove tutti prima di aggiungere Supplier. | FD-14 |
| A4-02 | P0 | Registrazione fornitore anonima (sia pagina FE sia pagina BE) → l'utente fa signup su Auth0 → /api/users/me re… | SU-02 |
| A4-03 | P0 | Il link d'invito punta alla pagina HTML del backend ({ApiBaseUrl}/register) perché App:ApiBaseUrl ha la preced… | SU-01 |
| A4-04 | P1 | Self-serve impossibile: email e comune sono disabled e presi solo dall'URL. Il token d'invito non è legato all… | SU-01 |
| A4-05 | P0 | Tre tassonomie di categorie incompatibili. Il fornitore salva etichette italiane, host web e mobile filtrano p… | SU-03 |
| A4-06 | P1 | ShouldSendEmail controlla SendGrid:ApiKey/Email:ApiKey, chiavi che non esistono. Il provider registrato è Rese… | FD-13 |
| A4-07 | P1 | Link della console costruito da App:FrontendBaseUrl, chiave non configurata in nessun appsettings* (grep = 0),… | FD-13 |
| A4-08 | P1 | request.Notes, Category, property.Name e supplier.LegalName vengono interpolati in HTML senza WebUtility.HtmlE… | FD-13 |
| A4-09 | P1 | L'attivazione richiede solo la ToS e ignora gli step 2–5 (violazione di SC-AC5 e della tabella wizard in PLANN… | SU-05 |
| A4-10 | P1 | URL del feed iCal fetchato lato server senza validazione di schema/host (SSRF). Il messaggio d'eccezione viene… | FD-16 |
| A4-11 | P2 | Task.Run(() => calendarSyncService.SyncIcalFeedAsync(...)) usa un service scoped (e il suo DbContext) dopo la … | SU-15 |
| A4-12 | P1 | Il codice catastale F205 è mappato a Firenze, ma F205 è Milano (Firenze = D612). Il registry conosce solo 12 c… | SU-04 |
| A4-13 | P1 | Chiavi di correlazione diverse tra web e app. Una richiesta creata dal web marketplace non ha bookingId, quind… | SU-07 |
| A4-14 | P1 | Il fornitore riceve solo nome property, categoria, urgenza e note. Mancano indirizzo, data/finestra (check-out… | SU-08 |
| A4-15 | P1 | Duplicazione concettuale SupplierJob vs ServiceRequest. SupplierJob (con QR check-in e Price) non ha alcun pun… | SU-11 |
| A4-16 | P1 | fetch('/api/…') relativo invece del client axios con VITE_API_BASE_URL: su Vercel ritorna index.html, quindi r… | FD-08, SU-13 |
| A4-17 | P2 | MarkPaid non intercetta InvalidOperationException("Richiesta non trovata"). | SU-10 |
| A4-18 | P2 | Nessun [Required]/[MaxLength] su Category (entity 100), Notes (1000), Reason (500). SupplierOrgId/PropertyId p… | SU-10 |
| A4-19 | P2 | Nessun controllo di concorrenza sulle transizioni. | SU-10 |
| A4-20 | P2 | Email e push vengono inviati dopo SaveChanges e non sono isolati: se lanciano un'eccezione il client riceve un… | FD-13 |
| A4-21 | P2 | Guid.Parse(inviteToken) dentro la query LINQ genera una FormatException non gestita (il controller cattura sol… | SU-01 |
| A4-22 | P2 | fix-orphaned cancella le org "duplicate" senza verificare che non abbiano ServiceRequests (FK Restrict) né ute… | SU-14 |
| A4-23 | P2 | Collegamento dell'utente al profilo fornitore per email, senza controllare email_verified. | SU-02 |
| A4-24 | P2 | Il testo "il tuo profilo fornitore verrà attivato automaticamente" è falso (resta Pending). ToLocalTime() sul … | SU-01 |
| A4-25 | P2 | Nessuna pagina gestisce isError: lo spinner resta infinito, oppure l'errore appare come stato vuoto. | SU-06 |
| A4-26 | P2 | Il flusso "AI match" e i suggerimenti esterni (Google/LLM) sono codice morto nel web. webSearch.SearchAsync è … | FD-21 |
| A4-27 | P2 | Stringhe hardcoded (IT/EN), defaultValue vietato, stati Active/PresoInCarico e categorie cleaning mostrati gre… | SU-06 |
| A4-28 | P2 | La timeline non mostra takenAt, paidAt né rejectionReason. "Segna pagato" sul web non chiede conferma (su mobi… | SU-09 |
| A4-29 | P2 | L'admin non può vedere i fornitori, reinviare o revocare inviti, né sospendere un fornitore scorretto. Un forn… | SU-12 |
| A4-30 | P2 | Per ogni richiesta autenticata di utenti senza ruolo Supplier, OnTokenValidated esegue una query DB su Users. | FD-14 |
| A4-31 | P2 | La checkbox "Accetto i termini di servizio fornitore CasaZen" non ha alcun link al testo dei termini. TosAccep… | SU-05 |
| A4-32 | P2 | Codice morto (SupplierShell con stringhe hardcoded). La pagina di aiuto iCal è fuori dalla shell fornitore. | SU-16 |
| A4-33 | P2 | Test che non verificano ciò che dichiarano. Create_WithBookingId_Returns400 passa per un booking inesistente, … | SU-07 |
| A5-01 | P0 | Alloggiati Web è simulato. Entrambi i rami impostano Status=Submitted. Con Alloggiati:Enabled=true c'è solo un… | CO-11 |
| A5-02 | P0 | Il modello dati non basta per la schedina. C'è un solo ospite per prenotazione: niente accompagnatori o minori… | CO-12 |
| A5-03 | P0 | Invio anticipato e duplicato. Il job Alloggiati parte appena l'ospite invia il form, anche giorni prima dell'a… | CO-11 |
| A5-04 | P0 | Il portale ospite non può mai completare l'invio. Il BE richiede gender; il form FE non ha il campo (ultima mo… | CO-02 |
| A5-05 | P0 | Formato CIN sbagliato. ^IT-\d{5}-\d{10}$ non corrisponde al CIN rilasciato dalla BDSR: 18 caratteri senza trat… | CO-01 |
| A5-06 | P0 | Nessuna property può essere attivata. Lo step tassa è bloccante mentre il PLANNING lo vuole come warning. La t… | CO-03 |
| A5-07 | P0 | Fuga di PII fra tenant. Una prenotazione creata dall'host riusa *globalmente* il Guest che ha la stessa email.… | TN-1 |
| A5-08 | P0 | Check-out e turnover irraggiungibili. Una prenotazione arriva a CheckedIn solo via API. start accetta Confirme… | CO-08 |
| A5-09 | P1 | Tutti i link del cockpit sono rotti. Il BE restituisce /properties/{id}/compliance/activation, /bookings/{id}/… | CO-04 |
| A5-10 | P1 | Rate limiter globale. AddFixedWindowLimiter senza partizione significa 10 GET e 3 submit al minuto per tutta l… | FD-10 |
| A5-11 | P1 | Spam di alert. Nessuna deduplica: finestra da 24h prima a +8 giorni, e withinAlertWindow è vero per tutte le d… | CO-10 |
| A5-12 | P1 | Anonimizzazione e retention incomplete. Restano data di nascita, cittadinanza, tipo, emissione e scadenza del … | CO-15 |
| A5-13 | P1 | Export GDPR (artt. 15/20) incompleto. Omette data e luogo di nascita, dati del documento, cittadinanza, prenot… | CO-15 |
| A5-14 | P1 | L'host può attivare il consenso marketing dell'ospite. | CO-15 |
| A5-15 | P1 | Base giuridica sbagliata e consenso non dimostrabile. Il trattamento Alloggiati si basa su un obbligo di legge… | CO-15 |
| A5-16 | P1 | Tassa di soggiorno sempre 0 in prenotazione. Esistono due sorgenti: TaxRates per le prenotazioni, che nessuno … | BK-03 |
| A5-17 | P1 | Date senza fuso → 500 su Postgres. Le date "YYYY-MM-DD" arrivano come Kind=Unspecified e Npgsql rifiuta timest… | FD-06 |
| A5-18 | P1 | Bug del wizard FE. (a) La checklist di sicurezza non viene inizializzata dal server ma viene sempre inviata a … | CO-05 |
| A5-19 | P1 | Documenti regionali mai applicati. Le chiavi LOM/LAZ vengono confrontate con la città; la regione calcolata no… | SU-04 |
| A5-20 | P1 | Lo stato non viene rivalutato. Si può cancellare il CIN (PUT /cin con null) o i documenti di una property Acti… | CO-06 |
| A5-21 | P1 | Checklist di sicurezza normativamente dubbia. Chiede rilevatore di fumo, estintore e certificato gas. Per quan… | CO-07 |
| A5-22 | P1 | Regole fiscali sbagliate. (a) La ritenuta OTA del 21% viene applicata automaticamente anche con P.IVA, mentre … | CO-18 |
| A5-23 | P1 | Area fiscale FE incompleta. Nessuna UI per Ordinario/Forfettario. Con 1 immobile non c'è alcuna azione. Report… | CO-19 |
| A5-24 | P1 | Il check-out "wizard" non è un wizard. Un solo step, supplierOrgId: null fisso, /start mai chiamato, propertyR… | CO-17 |
| A5-25 | P2 | Il reminder di check-out viene schedulato solo dopo un check-in manuale ed è solo push (la spec chiede email +… | CO-10 |
| A5-26 | P1 | Nessun fallback per l'host. Se l'email non parte il token viene messo Scaduto e la query lo esclude: nessuna s… | CO-09 |
| A5-27 | P2 | Una sessione scaduta per tempo non passa mai a Scaduto e blocca il job, che salta le prenotazioni con sessioni… | CO-09 |
| A5-28 | P2 | Dopo Completo il GET pubblico restituisce ancora il prefill con data di nascita e numero documento, fino alla … | CO-02 |
| A5-29 | P2 | Il portale legacy anonimo /api/checkin/{guid} è ancora attivo: token in chiaro sulla Booking, riusabile fino a… | CO-16 |
| A5-30 | P1 | PII ospite e documenti in chiaro nel DB. Il modello delle credenziali Questura ha WsKey in chiaro e PasswordEn… | CO-14 |
| A5-31 | P2 | Alert CIN finto (solo log). Dopo l'1/3/2026 i giorni alla scadenza valgono 0 per sempre: il job logga ogni gio… | CO-20 |
| A5-32 | P2 | Violazione della regola i18n: circa 50 chiavi compliance.* assenti in inglese; messaggi BE misti italiano/ingl… | FN-01 |
| A5-33 | P2 | HTML delle email costruito con interpolazione, senza encoding, e inline invece che con template (contro .claud… | FD-13 |
| A5-34 | P2 | Torino ha codice ISTAT 010025, che è Genova; Torino è 001272. Registro fisso di 12 comuni. | SU-04 |
| A5-35 | P1 | Test che non verificano nulla. Certificano come riusciti la simulazione e un CIN inventato; l'E2E usa mock div… | CO-11 |
| A5-36 | P2 | Il backfill porta ad Active le property con un CIN qualsiasi, senza documenti né checklist. | CO-06 |
| A5-37 | P2 | Scadenza delle 24h calcolata da CheckInDate a mezzanotte UTC, non dall'orario reale d'arrivo né dal fuso della… | CO-11 |
| A6-01 | P0 | Login nativo Auth0 non funzionante: makeRedirectUri({scheme:'casazen'}) non è tra le Allowed Callback URLs del… | MO-01 |
| A6-02 | P0 | La build di release punta a http://localhost:5000 e al tenant Auth0 dev (valori di fallback). .env è gitignore… | MO-02 |
| A6-03 | P0 | Tassonomia delle categorie incoerente. Il fornitore salva etichette italiane, l'host filtra per codici inglesi… | SU-03 |
| A6-04 | P0 | Il Golden Journey non è un gate. (1) Su PR/push/nightly gira solo il ramo L2 in demo mode con page.route, con … | FN-03 |
| A6-05 | P1 | La registrazione push non può riuscire. getExpoPushTokenAsync() legge il projectId placeholder; manca android.… | MO-03 |
| A6-06 | P1 | deviceId = Device.osInternalBuildId ?? Device.modelId: è l'ID di build del sistema operativo, uguale per tutti… | MO-03 |
| A6-07 | P1 | Push ed email "Check-in incompleto" inviati ogni ora, senza deduplica, per ogni booking con dati ospite incomp… | CO-10 |
| A6-08 | P1 | Nessuna notifica quando il fornitore rifiuta; nessuna push al fornitore su nuova richiesta; nessuna push all'h… | MO-04 |
| A6-09 | P1 | Il link di condivisione usa lo slug della proprietà come slug dell'org. Senza slug genera /book/{propertyId}. | MO-11 |
| A6-10 | P1 | I blocchi iCal (type:'ical-block', senza guestName) sono resi come prenotazioni "Ospite". Il tap chiama router… | MO-06 |
| A6-11 | P1 | monthRange usa toISOString() su mezzanotte locale: in Italia (UTC+2) start = ultimo giorno del mese precedente… | MO-06 |
| A6-12 | P1 | Calendario limitato alla prima proprietà e al mese corrente; nessun selettore di proprietà né navigazione; vis… | MO-06 |
| A6-13 | P1 | Errori mascherati da stato vuoto: calendarQuery.isError, servicesQuery.isError e suppliersQuery.isError non so… | MO-07 |
| A6-14 | P1 | Il refresh token (offline_access) viene salvato ma non usato mai. Qualunque 401 cancella i token e manda al lo… | MO-05 |
| A6-15 | P1 | Nessun logout nell'interfaccia; logout() è chiamato solo dal gestore 401. Non c'è deregistrazione del device, … | MO-05 |
| A6-16 | P1 | Il check-out rapido chiama complete saltando start: nessun riepilogo compliance (AC8, M7, G7). Richiede Checke… | MO-09 |
| A6-17 | P1 | Il dettaglio non mostra stato check-in ospite né badge compliance (AC5); nessun RefreshControl, quindi AC13 (5… | MO-08 |
| A6-18 | P1 | L'app sceglie in automatico items[0] da GET /suppliers senza ordinamento, senza mostrare alternative, urgenza … | MO-10 |
| A6-19 | P1 | Il listener di risposta è registrato solo dopo isAuthenticated; non c'è getLastNotificationResponseAsync()/use… | MO-03 |
| A6-20 | P1 | GET /api/properties restituisce solo le proprietà di cui l'utente è owner, ma le push vanno anche ad Admin e P… | MO-12 |
| A6-21 | P2 | EXPO_PUBLIC_E2E_DEMO non è protetto da __DEV__: se vale 1 al build, anche "Continua con Auth0" salta Auth0 e s… | MO-01 |
| A6-22 | P1 | Le asserzioni Maestro non distinguono un'app funzionante da una rotta. M3 senza push. M5 verifica solo .*Pagat… | FN-04 |
| A6-23 | P1 | GJ web L3 fedele solo a metà. Step 1–10 via API. Il fornitore dello step 1 viene scartato. L'host fa anche il … | FN-03 |
| A6-24 | P1 | F1–F2 skippato fuori da E2E_LOCAL; si auto-attiva il fornitore via API; verifica "host reflects" via API invec… | FN-03 |
| A6-25 | P2 | Offline non "graceful": nessun persistQueryClient, onlineManager non collegato a NetInfo (niente refetch alla … | MO-07 |
| A6-26 | P2 | L'estrazione del messaggio ignora ProblemDetails.detail/title, che il BE usa per i 409. Alcune risposte BE son… | MO-07 |
| A6-27 | P2 | Nessuna i18n; enum e codici inglesi mostrati all'utente (CheckedIn, PresoInCarico, Pending, cleaning, linen). … | MO-13 |
| A6-28 | P2 | Contrasto sotto WCAG AA (4,5:1); tab senza icone; indicatori di caricamento senza label; nessun testID utile a… | MO-13 |
| A6-29 | P2 | Invio push sincrono e seriale dentro la richiesta del fornitore (latenza); nessun batching (≤100 messaggi); ne… | MO-04 |
| A6-30 | P2 | Nessuna CI mobile (typecheck mai eseguito automaticamente), nessun test unitario, nessuna icona/splash/icona n… | MO-02 |
| A6-31 | P2 | In __DEV__ qualunque sorgente può aprire casazen://e2e-auth?access_token=… e iniettare una sessione (login CSR… | MO-01 |
| A6-32 | P2 | Testo errato "Il check-in è stato chiuso" dopo il check-out. Due bottoni per la stessa azione "Segna pagato". … | MO-13 |
| A7-01 | P0 | Registrazione RLI finta presentata come reale. SubmitRegistrationAsync restituisce RLI-STUB-{id} senza chiamar… | LT-01 |
| A7-02 | P0 | Firma elettronica stub. Il PDF non viene inviato a nessun provider. I link sono https://sign.provider.example.… | LT-02 |
| A7-03 | P0 | Il gate "template approvato da un legale" è aggirato di default: il config base (valido anche in produzione) m… | LT-03 |
| A7-04 | P0 | La scadenza RLI è calcolata male: StartDate + 30. Per legge sono 30 giorni dalla stipula, o dalla decorrenza s… | LT-04 |
| A7-05 | P0 | La creazione del lease dall'UI fallisce probabilmente su PostgreSQL. L'FE invia "2026-09-01", che diventa un D… | FD-06 |
| A7-06 | P0 | Un utente solo-LTR non può creare contratti: il contesto long-rent non ha property.* e GET /api/properties e /… | LT-05 |
| A7-07 | P1 | Canone ricorrente (#269) assente. Esistono tabelle e routing webhook, ma il servizio è Null (i metodi lanciano… | LT-06 |
| A7-08 | P1 | La voce di checklist "Comunicazione Questura (art. 7 D.Lgs 286/1998)" risulta fatta quando CasaZen invia un'em… | LT-07 |
| A7-09 | P1 | Advisory fiscale numericamente errato: (1) il 10% viene applicato a ogni lease CanoneConcordato, ignorando che… | LT-08 |
| A7-10 | P1 | Buchi tra le fasce di superficie: le bande intere 0–50, 51–74, 75–99, 100+ lasciano scoperti i valori decimali… | LT-10 |
| A7-11 | P1 | I coefficienti ignorano tetti e pavimenti previsti dall'accordo (ricerca :153-155: "+20% (tetto a 40 mq)", "+1… | LT-10 |
| A7-12 | P1 | Il range del canone concordato è verificato solo lato client ed è scollegato dai dati del contratto: gli anni … | LT-10 |
| A7-13 | P1 | La durata non è validata per tipologia (libero 4+4 ≥ 4 anni, concordato 3+2 ≥ 3 anni, transitorio 1–18 mesi no… | LT-10 |
| A7-14 | P1 | Writer PDF minimale: una sola pagina in formato Letter (non A4), nessun a-capo né paginazione, troncamento a 4… | LT-09 |
| A7-15 | P2 | 4 stati del lease su 8 senza etichetta (PartiallySigned, RegistrationPending, SentToProvider, Rejected); in co… | LT-11 |
| A7-16 | P2 | I link dei firmatari vivono solo in useState (non c'è un GET dei firmatari): dopo un refresh un lease Awaiting… | LT-02 |
| A7-17 | P2 | L'API restituisce entità EF (Party con CF ed email in chiaro, Property completa con OwnerId e SafetyChecklistJ… | LT-11 |
| A7-18 | P1 | #179 non implementata: nessuna anonimizzazione della PII Party; ErasureRequested non viene mai scritto. In più… | LT-12 |
| A7-19 | P2 | Stessa classe di bug del commit e611233: ogni InvalidOperationException non gestita diventa 503, ogni Unauthor… | FD-05 |
| A7-20 | P2 | Il webhook e-sign non controlla lo stato: un evento "allSigned" su un lease Registered lo riporta a Signed, e … | LT-02 |
| A7-21 | P2 | TriggerRegistrationAsync non è atomico: delega ed evento vengono salvati prima della chiamata al provider; reg… | LT-01 |
| A7-22 | P2 | Dati normativi nel codice: destinatari, PEC e aliquota IMU di Seveso e Cesano sono letterali; le soglie delle … | LT-13 |
| A7-23 | P1 | I range vengono calcolati con dati Partial (non verificati da un legale: vigenza dell'accordo e ricezione di S… | LT-10 |
| A7-24 | P2 | Calcolatore, guida e pulsante IMU compaiono per qualunque regime; il pulsante IMU è abilitato su lease Registe… | LT-13 |
| A7-25 | P2 | Le chiavi i18n sono usate come messaggio Zod a livello di schema. In Zod v4 l'errore di schema ha precedenza s… | FD-09 |
| A7-26 | P2 | Stringhe non tradotte o hardcoded: ruolo della parte grezzo ("Landlord"), eventType grezzo nella timeline ("Re… | LT-11 |
| A7-27 | P2 | Stati di errore mancanti: qualunque errore (500, 403, rete) nel dettaglio viene mostrato come "contratto non t… | LT-11 |
| A7-28 | P2 | Solo 1 locatore e 1 conduttore (comproprietari e coniugi, casi molto frequenti, non sono rappresentabili). CF … | LT-14 |
| A7-29 | P2 | Test deboli: l'happy path passa grazie a un fake che conferma (in produzione non conferma mai), su InMemory e … | LT-15 |
| A7-30 | P2 | Processo e registro incoerenti: specs "frozen" ma codice mergiato; #269 chiusa come "completed" senza implemen… | FN-05 |
| A7-31 | P2 | Enum.TryParse<RentalType> accetta stringhe numeriche ("7"); il mapping lancia ArgumentOutOfRangeException → 50… | PL-07 |
| A8-01 | P0 | Denial-of-wallet AI. POST /api/service-requests/match-supplier è aperto a ogni utente autenticato, senza rate … | FD-21 |
| A8-02 | P0 | Le pagine SEO non sono scopribili sul dominio reale. Canonical e sitemap puntano a www.casazen.it, che non è l… | SE-02 |
| A8-03 | P0 | Il funnel CTA è rotto. /signup e /tools/verifica-conformita non esistono. Il catch-all porta a /, che sta dent… | SE-03 |
| A8-04 | P1 | Il gate di revisione legale è un timbro automatico. Al primo deploy il bootstrap genera e auto-approva le pagi… | SE-01 |
| A8-05 | P1 | La rigenerazione ripubblica testo mai revisionato. L'upsert non rimette la pagina in Draft. La nuova revisione… | SE-01 |
| A8-06 | P1 | Contenuti stub, vuoti o fuori controllo pubblicati. Senza la sezione Ai il provider è Stub: una frase del tipo… | SE-01 |
| A8-07 | P1 | Il budget AI piattaforma non ha effetto. DeepSeek restituisce PromptTokens=0 e CompletionTokens=0, quindi Toke… | FD-21 |
| A8-08 | P1 | XSS latente. Il sanitizer è una blacklist regex. Verificato eseguendo la regex FE: <img src="x"/onerror="alert… | FD-15 |
| A8-09 | P1 | Le pagine SEO sono solo client-side, con metadati minimi. L'HTML iniziale ha lang="en", titolo temp-vite, ness… | BK-15 |
| A8-10 | P1 | AC2 assente. Nessuna lista di immobili in evidenza per comune. Il DTO pubblico non ha OrgSlug né lo slug host,… | SE-04 |
| A8-11 | P1 | AC3, AC8 e AC9 assenti: niente SeoEvent, endpoint eventi, analytics o widget admin. | SE-04 |
| A8-12 | P1 | AC6: la tariffa mancante non è gestita. Il calcolatore resta visibile e fallisce con "Verifica i dati inseriti… | BK-03 |
| A8-13 | P1 | La ricerca pubblica è rotta. Con valueAsNumber un input vuoto vale NaN e z.number().optional() lo rifiuta. L'e… | BK-20 |
| A8-14 | P1 | Dati LLM presentati come "più votati su Google". Il testo dice "Ecco le attività locali più votate su Google" … | FD-21 |
| A8-15 | P1 | Dati verso un provider AI extra-UE non dichiarato. Le note libere dell'host (che possono contenere nomi o orar… | FD-21 |
| A8-16 | P1 | Feature in freeze esposte. Il menu OTA chiede API key e secret di Airbnb/Booking (API partner-only, freeze #31… | FD-20 |
| A8-17 | P2 | "AI Dynamic Pricing" è un moltiplicatore fisso (×1.5 festivi, ×1.3 estate, ×0.8 inverno). | PC-15 |
| A8-18 | P2 | Test deboli o assenti. L'E2E SEO dedicato è stato cancellato. L'E2E sulla tassa non mocka GET /api/public/cont… | SE-04 |
| A8-19 | P2 | i18n. Nel FE sono hardcoded "/persona/notte" e "max N notti". Il tipo pagina è mostrato come enum grezzo ("Com… | SE-05 |
| A8-20 | P2 | La classe prose non ha effetto: l'HTML del contenuto non ha stili (h2 e ul appiattiti dal preflight). | BK-15 |
| A8-21 | P2 | UX admin SEO. Mancano anteprima, link alla pagina pubblica e azione "Ritira" (torna Draft). Paginazione fissa … | SE-01 |
| A8-22 | P2 | Il rate limiter PublicTouristTaxCalc è globale (AddFixedWindowLimiter), non per IP: 30 richieste al minuto per… | FD-10 |
| A8-23 | P2 | Calcolo tassa impreciso. I bambini sono sempre esenti a prescindere da MinimumAge e la UI non chiede l'età. Il… | BK-03 |
| A8-24 | P2 | Registry dei comuni hardcoded (12) con codici ISTAT dubbi. Torino ha 010025, che è il codice di Genova (Torino… | SU-04 |
| A8-25 | P2 | Cache AI statiche illimitate e con semantica sbagliata. La cache del provider è permanente, senza TTL né limit… | SE-05 |
| A8-26 | P2 | Config morta o duplicata. Seo:AiProvider non viene mai letto (il provider reale è Ai:Provider). AddCasazenAiPr… | FD-21 |
| A8-27 | P2 | Il componente di trasparenza AI Act non è montato in nessuna pagina (motivazioni AI, discovery, contenuti SEO)… | SE-05 |
| A8-28 | P2 | Lo step tassa del wizard linka una rotta admin (/app/admin/compliance/tax-rates) inaccessibile all'host, invec… | CO-03 |
| A8-29 | P1 | La destinazione del funnel non è indicizzabile. AC12-14 (JSON-LD VacationRental, meta/og, sitemap e robots per… | BK-15 |
| A8-30 | P2 | Documentazione incoerente. La spec ha status: specced e issue: vuoto, mentre il registro indica planned #300. … | FN-05 |
| A9-01 | P0 | Ospite = entità globale condivisa fra tenant. La prenotazione manuale riusa l'ospite trovato per email in tutt… | TN-1 |
| A9-02 | P0 | Upgrade del piano gratuito. PUT /api/orgs/me/plan accetta qualsiasi tier (Starter/Pro/Scale) per ogni utente a… | FD-18 |
| A9-03 | P0 | Coda Hangfire condivisa fra test e produzione. EF usa SearchPath=casazen_test | FD-11 |
| A9-04 | P0 | Perdita dati a ogni redeploy. (a) Le chiavi Data Protection vivono nel filesystem effimero del container → dop… | FD-07 |
| A9-05 | P0 | Stub in produzione che dichiara successo. Con Alloggiati:Enabled true o false, report.Status = Submitted e nes… | CO-11 |
| A9-06 | P0 | Se FromAddress contiene "casazen.app" il mittente viene forzato a onboarding@resend.dev, il dominio di test Re… | FD-13 |
| A9-07 | P1 | Il middleware mappa ogni InvalidOperationException a 503 "OperationFailed" con Detail = ex.Message, e fa Unwra… | FD-05 |
| A9-08 | P1 | Localizzazione backend rotta. Il middleware cerca Resources/Middleware/ErrorHandlingMiddleware.resx, che non e… | FD-05 |
| A9-09 | P1 | Nessun contratto d'errore unico. Il FE non legge mai il corpo dell'errore (1 solo uso di response?.data?.error… | FD-05 |
| A9-10 | P1 | I limiter sono globali, non per IP. GuestCheckInSubmit = 3 invii al minuto per tutta la piattaforma, PublicBoo… | FD-10 |
| A9-11 | P1 | I test d'integrazione girano su EF InMemory, che non applica FK, unique index filtrati, timestamptz né transaz… | FD-04 |
| A9-12 | P1 | I DateTime dal client (yyyy-MM-dd → Kind=Unspecified) scritti o confrontati su colonne timestamptz fanno lanci… | FD-06 |
| A9-13 | P1 | IsValidExportFeed restituisce false se il feed ha 0 eventi. Il sync segna Failure ed esce senza rimuovere i bl… | PC-10 |
| A9-14 | P1 | _ = Task.Run(() => calendarSyncService.SyncIcalFeedAsync(...)) usa un servizio scoped (DbContext della richies… | SU-15 |
| A9-15 | P1 | Funzionalità "finte" esposte come reali. POST /payments/{id}/process imposta Completed senza Stripe. refund ag… | BK-02 |
| A9-16 | P1 | ShouldSendEmail() in Production invia solo se c'è SendGrid:ApiKey o Email:ApiKey, ma il provider reale legge E… | FD-13 |
| A9-17 | P1 | sync-platform accoda un job per platform/externalId arbitrari senza controllo di ownership (qualsiasi utente a… | FD-20 |
| A9-18 | P1 | L'anonimizzazione e l'erasure lasciano DateOfBirth, Nationality, DocumentType, DocumentIssueDate/ExpiryDate, D… | CO-15 |
| A9-19 | P1 | Health statico ({status:"healthy"}), nessun AddHealthChecks (issue #16). CI verify-test: sleep 90 e poi curl /… | FD-12 |
| A9-20 | P1 | Lista "endpoint pubblici" per substring: '/checkin/' fa match anche su /bookings/{id}/checkin/resend-link (api… | FD-08 |
| A9-21 | P1 | fetch('/api/public/...') relativo all'origine Vercel, e Vercel riscrive tutto su /index.html. | FD-08 |
| A9-22 | P1 | Nessuna gestione di 401 e 403 (solo console.error). Nessun retry dopo il refresh del token. Se getAccessTokenS… | FD-08 |
| A9-23 | P1 | Il workflow è invalido: il contesto secrets non è ammesso in steps.if. GitHub lo rifiuta al parsing, quindi la… | FD-01 |
| A9-24 | P1 | I gate "Golden Journey" sono finti: il job BE fa solo echo, il job app Maestro FE fa solo echo "placeholder", … | FD-03 |
| A9-25 | P1 | 78 chiavi presenti in it.json mancano in en.json (tutte compliance.activation.*, compliance.checkout.*). 56 ch… | FD-09 |
| A9-26 | P1 | Dipendenze con CVE note. La CI non fallisce sulle vulnerabilità. | FD-02 |
| A9-27 | P2 | POST /api/auth/register anonimo, senza RL, crea Users con Id GUID casuale ("In production, this would create u… | PL-08 |
| A9-28 | P2 | CORS: qualunque *.vercel.app è ammesso con AllowCredentials. | FD-17 |
| A9-29 | P2 | Niente HSTS e niente CSP. La SPA non ha X-Frame-Options né CSP. Il sanitizer a regex è aggirabile (<svg/onload… | FD-15 |
| A9-30 | P2 | Seconda implementazione del check-in ospite, non usata da FE e mobile (FE usa /public/checkin, api/checkin.api… | CO-16 |
| A9-31 | P2 | Per ogni richiesta autenticata: query sulla membership Supplier, Users (sync, scope separato), Users + UserCon… | FD-14 |
| A9-32 | P2 | SSRF cieca: l'URL di import iCal viene scaricato lato server. Per le property si controlla solo https, per i f… | FD-16 |
| A9-33 | P2 | Codice morto che contiene anche le stringhe hardcoded e gli stub. | FN-02 |
| A9-34 | P2 | File spazzatura e codice morto. | FD-19 |
| A9-35 | P2 | Migrazioni scritte a mano senza .Designer.cs. Ho verificato che hanno [DbContext] e [Migration] (quindi vengon… | FD-04 |
| A9-36 | P2 | Email in chiaro nei log (contro security.md: "never log PII"). Log di debug del token in produzione. | FD-17 |
| A9-37 | P2 | Una build production EAS senza .env punta a localhost. Sessione persa alla scadenza dell'access token. | MO-02 |
| A9-38 | P2 | build:demo: cross-env VITE_DEMO_MODE=true tsc -b && vite build → la variabile vale solo per tsc, vite build pr… | PL-01 |
| A9-39 | P2 | availability restituisce 200 con bookedDates vuoto per property inesistenti o non pubblicate (sembra tutto lib… | BK-05 |
| A9-40 | P2 | L'elenco dei sub-responsabili pubblicato agli utenti (GDPR art. 28) è falso: manca Resend e la regione di Auth… | PL-14 |
| R-01 | P0 | POST /api/public/tourist-tax/calculate {"comuneSlug":"milano","numberOfAdults":2,"checkInDate":"2026-10-01","c… | FD-06 |
| R-02 | P0 | Regex ^IT-\d{5}-\d{10}$ in 5 punti (CinComplianceRules.cs:8, CinCodeAttribute.cs:12, AdminService.cs:17, Prope… | CO-01 |
| R-03 | P1 | Il FE chiama GET /api/public/bookings/property/{slug}/availability; l'API accetta solo GUID → 400. Con propert… | BK-05 |
| R-04 | P1 | Il bottone "Procedi al checkout" naviga a /checkout??checkIn=…&checkOut=… (doppio ?): check-in e ospiti persi;… | BK-01 |
| R-05 | P0 | Checkout mostra "Tassa di soggiorno 12,00 € — Totale 412,00 €" (hardcoded FE), il backend crea il booking con … | BK-03 |
| R-06 | P1 | FE invia {"email":…}, BE richiede BookingId → 400; la UI mostra "Nessuna prenotazione trovata" + "Errore nella… | BK-11 |
| R-07 | P1 | Tutte le ProblemDetails mostrano chiavi grezze ("detail":"UnauthorizedDetail", "InternalServerErrorDetail"): S… | FD-05 |
| R-08 | P2 | "3 nottei" (plurale rotto) nel widget e nel checkout. | FD-09 |
| R-09 | P2 | Pagine comune con corpo segnaposto "Milano: contenuto generato per affitti brevi, CIN e tassa di soggiorno." | SE-01 |
| R-10 | P2 | GET /api/public/bookings/property/{guid-inesistente}/availability → 200 con lista vuota invece di 404. POST /w… | FD-20 |
| R-11 | P2 | Con Connect non configurato il FE mostra solo "Impossibile avviare il checkout" (messaggio BE inglese non prop… | BK-06 |
