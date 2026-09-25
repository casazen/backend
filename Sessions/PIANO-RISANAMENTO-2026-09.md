# CasaZen: piano di risanamento (settembre 2026)

> **Data audit:** 2026-09-23
> **Base analizzata:** backend `develop@4cbaeaa`, frontend `develop@0b91e3c`, mobile `develop@4b20215`
> **Report di dettaglio (con file:riga per ogni difetto):** [`audit-2026-09-23/`](./audit-2026-09-23/README.md)
> **Rapporto con `PLANNING.md`:** la visione e il Golden Journey restano validi. Questo documento dice **cosa manca e cosa è rotto** e **in che ordine sistemarlo**. Fino alla chiusura della Fase 2, qui sotto, non si aggiungono feature nuove.

---

## 1. Sintesi

**Il registry delle spec dichiara "shipped" 20 funzionalità. Nessuna supera una verifica end-to-end fatta da UI con utenti reali distinti (host, fornitore, ospite).** Il completamento reale del perimetro MVP è stimato attorno al **35-40%**. Il codice c'è quasi ovunque (entità, migrazioni, endpoint, pagine), ma i percorsi che l'utente fa davvero si interrompono in molti punti.

Numeri dell'audit (9 agenti specializzati, più una verifica runtime su PostgreSQL e browser reali):
- circa **320 difetti** documentati con file:riga, scenario di fallimento e fix proposto;
- circa 45 P0, 145 P1 e 135 P2, contando anche i duplicati tra aree. I **P0 unici sono circa 30**;
- solo 2 dei 12 step del Golden Journey (8 e 9) funzionano da UI con utenti reali; 4 sono parziali e 6 rotti o finti (dettaglio in §3).

**Perché sembrava tutto "fatto": tre cause radice**
1. **Verifiche finte.**
   - I test d'integrazione girano su EF InMemory, non su PostgreSQL.
   - 27 spec E2E su 36 usano mock (`page.route`, demo mode).
   - Il workflow `e2e.yml` è invalido e non è mai partito: 354 run fallite con 0 job.
   - I gate "Golden Journey" di backend e app sono `echo`.
   - Il Golden Journey "L3" usa **lo stesso utente** come host e come fornitore e salta la UI agli step 1-4, 6 e 11.
   - Alcuni test **codificano i bug come comportamento atteso**: upgrade gratuito del piano, attivazione del fornitore con la sola ToS, pagamento su prenotazione cancellata, Alloggiati simulato.
   - Il check M7 dell'app è passato su un badge "Nessun badge critico" scritto a mano.
2. **Stub che dichiarano successo.** Alloggiati Web (`Submitted` senza alcuna chiamata alla Questura), registrazione RLI, firma elettronica, rimborsi, "process payment", sync OTA, notifiche di conferma, alert CIN e "prezzo AI" mostrano all'utente un esito positivo che non è avvenuto.
3. **Deriva di scope e fondamenta mancanti.**
   - Feature in freeze sviluppate o esposte: LTR, API OTA, discovery AI.
   - P0 del MVP mai iniziati: frontend billing, funnel SEO #300.
   - Lacune infrastrutturali trasversali: ospite condiviso tra tenant, file e chiavi di cifratura persi a ogni deploy, coda Hangfire condivisa test/prod, date non-UTC che danno 500 su Postgres, localizzazione backend rotta, email che non partono in produzione.

**Cosa funziona davvero** (da preservare):
- JWT su tutti i 164 endpoint non pubblici; firme dei webhook Stripe, OTA ed e-sign verificate.
- Filtro tenant EF su 10 entità core e migrazione OrgId in 3 step fatta bene.
- Read-model pubblico senza leak di `OwnerId`.
- Advisory lock anti-overbooking sul direct booking.
- PaymentIntent sul connected account con fee 0 (take-rate zero rispettato).
- Macchina a stati `ServiceRequest` (ownership e 409 corretti, se guidata via API).
- Import iCal nel caso semplice; pagine SEO renderizzate.
- Build verde e migrazioni pulite su Postgres vuoto (33/33, nessuna deriva del modello).
- 908 test backend e 166 test frontend verdi. Non intercettano però i problemi descritti sopra.

---

## 2. Piano vs realtà, per spec

Legenda: ✅ funziona · 🟡 parziale · 🔴 rotto per l'utente · ⚪ stub/finto · ⛔ non iniziato · ❄️ freeze violato

| Spec / issue | Registry | Reale | Stato | Motivo principale (ID difetto) |
|---|---|---|---|---|
| US-001 public-booking-readmodel #212 | shipped | ~90% | ✅ | Solo difetti minori (`/search` vicolo cieco, A3 RM-AC11) |
| US-004 tenant-boundary #202 | shipped | ~75% | 🟡 | `Guest` globale tra tenant: un host legge o anonimizza gli ospiti di un altro (A9-01, A2-02, A5-07); 12 entità senza filtro |
| role-onboarding #198 | shipped | ~55% | 🔴 | Onboarding senza uscita per admin e utenti con ruoli ma senza org (A1-01); ruoli Auth0 scritti con token statico che scade (A1-02) |
| US-006 onboarding-plg #271 | in-dev | ~50% | 🟡 | Consensi aggirabili via API/app (A1-05); documenti legali senza testo e sub-responsabili falsi (A1-06); checklist di attivazione mancante |
| US-005 saas-billing #230 | shipped | ~25% | 🔴 | Nessuna pagina FE; upgrade gratuito del piano (#274, A1-03); IVA/OSS errata (A1-08); SDI stub |
| admin-backend #11 | shipped | ~65% | 🟡 | Pagina CIN irraggiungibile (A1-16); cambio ruolo cancella tutti i ruoli (A1-17) |
| property-detail #152 | shipped | ~65% | 🔴 | Foto e documenti su disco effimero del container (A2-03); nessuna UI upload foto (A2-26); PUT azzera pulizia, deposito e policy (A2-04) |
| US-018 ical-calendar-sync #294 | shipped | ~50% | 🟡 | Un solo feed per proprietà (A2-11); feed vuoto = blocchi eterni (A2-10); sito pubblico ignora i blocchi (A2-13) |
| pricing-adapter | shipped | stub | ⚪ | Prezzo base fisso 100 €, "confidenza AI 85%" fissa, nessun effetto su prenotazioni (A2-14) |
| connect-onboarding #224 | shipped | ~70% | 🟡 | Un errore Stripe transitorio sostituisce l'account verificato (A3-19) |
| US-002 direct-checkout #226 | shipped | ~40% | 🔴 | Widget → checkout perde il check-in (A3-01, **verificato**); tassa di soggiorno sempre 0 ma mostrata al guest (A3-02, **verificato**); webhook perde eventi (A3-03); addebito senza prenotazione (A3-04); rimborsi finti (A3-05) |
| US-003 branded-booking-site #215 | shipped | ~45% | 🔴 | Disponibilità 400 con lo slug (A3-09, **verificato**); "Le mie prenotazioni" 400 (A3-10, **verificato**); branding non configurabile; email di login esposta |
| US-023 public-site-design-system #297 | shipped | ~35% | 🟡 | I 3 temi sono identici; contrasto CTA 2,95:1; SEO assente (title "temp-vite") |
| US-024 custom-domain-booking #298 | shipped | ~25% | 🔴 | Dominio e sottodominio portano a `/login` (A3-08); CORS; nessuna Vercel Domains API (A3-25) |
| US-022 supplier-console-web #292 | shipped | ~35% | 🔴 | Link d'invito rotto (A4-03); il fornitore finisce nell'onboarding host (A4-02); **ogni visita alla console cancella i ruoli Auth0 degli utenti con doppio ruolo** (A4-01) |
| US-021 micro-marketplace-v0 #293 | shipped | ~50% | 🔴 | 3 tassonomie di categorie incompatibili: i fornitori configurati da UI non si trovano mai (A4-05, A6-03); email "nuova richiesta" mai inviate (A4-06); web e app vedono richieste diverse (A4-13) |
| US-019 compliance-wizards #295 | shipped | ~30% | 🔴 | CIN reale rifiutato (R-02/A5-05, **verificato**); step tassa bloccante senza tariffe inseribili (A5-06); check-out irraggiungibile da UI (A5-08); link cockpit tutti rotti (A5-09) |
| US-020 guest-check-in-portal #296 | shipped | ~30% | 🔴 | Il form ospite riceve sempre 400 (campo sesso, A5-04); **Alloggiati simulato** (A5-01) |
| regime-fiscale-2026 #3 | aperta | ~55% | 🟡 | Ritenuta 21% applicata anche con P.IVA; soglia per org invece che per contribuente (A5-22); UI e PDF incompleti (A5-23) |
| US-025 native-host-app #299 | shipped | ~40% | 🔴 | Login Auth0 nativo non funziona (A6-01); la build di release punta a localhost (A6-02); push mai registrate (A6-05) |
| GJ-001 golden-journey-e2e #301 | shipped | ~15% | ⚪ | I gate sono `echo`, `e2e.yml` è invalido, mock e un solo utente (A6-04, A9-23/24) |
| US-026 seo-funnel #300 | planned | ~20% | ⛔ | CTA verso rotte inesistenti; pagine non indicizzabili; nessun tracciamento (A8-02/03) |
| US-027 supplier-public-site #303 | F2 | ~5% | 🔴 | Slug mai generato; `fetch` relativo che su Vercel riceve `index.html` (A4-16) |
| US-028 native-supplier-app #304 | F2 | 0% | ⛔ | — |
| LTR (US-007…010, canone concordato) | **frozen** | ~25% | ❄️ | Sviluppato ad agosto nonostante il freeze; RLI ed e-sign stub presentati come reali (A7-01/02); creazione lease probabilmente in 500 su Postgres (A7-05) |
| API OTA #31-35 | **frozen** | — | ❄️ | Voce di menu attiva verso endpoint inesistenti; 2 job ricorrenti a vuoto con `Guid.Empty` (A2-09) |
| Discovery AI fornitori | (US-014 frozen) | — | ❄️ | Spesa AI illimitata per qualsiasi utente (A8-01); dati LLM presentati come "Google" (A8-14) |
| #4 imposta di soggiorno | aperta | — | 🔴 | Due tabelle tariffe; quella del checkout non viene mai popolata; calcolatore pubblico in 500 (R-01) |
| #5 / #179 / FE #145 GDPR | aperte | — | 🟡 | Anonimizzazione ed export incompleti; l'host può attivare il consenso marketing dell'ospite; Party mai anonimizzate (A5-12…15) |
| #6 ISTAT, #8 regionale | aperte | — | ⛔ | Nessun codice |
| #15 RFC 7807, #16 health check | aperte | — | 🟡/⛔ | Formati d'errore misti; health statico (A9-09, A9-19) |
| #51 rimborsi, #58 email conferma | aperte | — | ⚪ | Stub con `TODO` e `Task.Delay` (A3-05, A3-11) |
| #273 X-Forwarded-For, #274 upgrade piano | aperte | — | ⛔ | Non implementate (A1-12, A1-03) |
| #340 marketplace, #327 aiuto iCal | aperte | — | 🟡 | AC3 non implementato e test che non lo verifica (A4); pagina aiuto fuori dalla console |

---

## 3. Golden Journey: stato reale step per step

| Step | Cosa deve succedere | Da UI con utenti reali | Blocchi principali |
|---|---|---|---|
| 1 | Creazione fornitore (invito o signup) | 🔴 | Link d'invito verso la pagina HTML del backend con callback Auth0 errata (A4-03); dopo il signup il fornitore finisce nell'onboarding host (A4-02) |
| 2 | Wizard fornitore → `Active` | 🟡 | 2 step invece di 5; `Active` con la sola ToS (A4-09); categorie "Pulizie" introvabili (A4-05) |
| 3 | Host: onboarding, property, iCal, sito | 🔴 | Onboarding senza uscita (A1-01); CIN reale rifiutato e tassa bloccante: la property non diventa mai `Active` (A5-05/06); niente foto (A2-26); branding non configurabile (A3-17) |
| 4 | Guest: prenotazione diretta + pagamento | 🔴 | Checkout perde le date (A3-01); disponibilità 400 (A3-09); tassa mostrata ≠ tassa incassata (A3-02); config Stripe Connect non documentata (A3-24) |
| 5 | Calendario coerente (booking + iCal) | 🟡 | Mese troncato di 1-2 giorni e navigazione senza refetch (A2-06); le prenotazioni manuali dell'host vengono auto-annullate dal primo checkout pubblico → overbooking (A2-01) |
| 6 | Check-in ospite + Alloggiati | 🔴 | Form ospite sempre 400 (A5-04); Alloggiati simulato (A5-01); invio anticipato e duplicato (A5-03) |
| 7 | Host richiede fornitore | 🟡 | Categorie incompatibili (A4-05); da app viene scelto automaticamente il primo fornitore (A6-18) |
| 8 | Fornitore prende in carico | ✅/🟡 | Funziona, ma il fornitore non vede né indirizzo né data (A4-14) |
| 9 | Fornitore completa | ✅ | — |
| 10 | Host segna pagato | 🟡 | Solo un flag; parità web↔app rotta (A4-13) |
| 11 | Check-out wizard + pulizie | 🔴 | Nessuna azione di check-in da UI, quindi check-out irraggiungibile (A5-08); wizard a 1 step senza fornitore (A5-24) |
| 12 | Cockpit verde / property pronta | ⚪ | `propertyReady` hardcoded a `true`; semafori che contano Alloggiati simulato come inviato; link rotti (A5-09) |

**App host (M1-M7):** non utilizzabile fuori dall'ambiente dello sviluppatore (login, configurazione build, push). **Fornitore da mobile (F1-F2):** le CTA ci sono, ma le pagine non gestiscono gli errori e manca il dettaglio.

---

## 4. Regole del risanamento (nuova Definition of Done)

Valgono per chiunque sviluppi, persone e agenti AI (Cursor, Claude, ecc.).

1. **"Fatto" = test L3 verde da UI.** Stack reale (PostgreSQL, API, FE), utenti Auth0 di test **distinti** per host, fornitore e guest, Stripe in test mode. Niente `page.route`, niente demo mode, niente chiamate API al posto dei click (le API servono solo come oracolo degli assert).
2. **Nessuno stub in un percorso di produzione.** Ogni funzione non implementata risponde `501` / "non disponibile" e ha un badge in UI, oppure resta nascosta dietro feature flag. **Mai** uno stato di successo senza effetto reale.
3. **Un test che codifica un bug è un bug.** Va riscritto quando si sistema il comportamento.
4. **Il registry diventa "shipped" solo con evidenza L3 su staging** (link al run CI), non alla chiusura dell'issue.
5. **i18n parte del DoD:** chiavi in `it.json` **e** `en.json`, messaggi backend via `IStringLocalizer`, nessun `defaultValue`.
6. **Stati di caricamento, errore e vuoto obbligatori** in ogni pagina. Un errore API non si mostra mai come "lista vuota".
7. **Freeze rispettato.** Nessuna PR su aree in freeze finché questo piano non arriva alla Fase 3, salvo le fix che spengono o rendono onesto il codice esistente.
8. **Le PR automatiche seguono il piano.** Il bot che apre una PR draft al giorno ("critical-bug-management") va messo in pausa o indirizzato ai punti di questo piano: oggi produce PR più in fretta di quanto vengano revisionate.

---

## 5. Piano di fix punto per punto

Effort in giorni-persona (g). Gli ID tra parentesi rimandano ai report in `audit-2026-09-23/`. Dove un punto compare in più aree, i riferimenti sono tutti elencati.

### Fase 0: messa in sicurezza e base stabile (settimana 1, circa 8-10 g)

Obiettivo: smettere di peggiorare la situazione, spegnere il finto, sbloccare i bug più visibili con fix piccole.

**0.1 Governance (0,5 g)**
- [ ] Aggiornare `Sessions/specs/README.md` con gli stati reali della tabella §2, e `PLANNING.md` ("LTR, API OTA, discovery AI: spenti dietro flag").
- [ ] Riaprire #269 come frozen (è chiusa "completed" ma non è implementata).
- [ ] Aprire un'issue per ogni punto P0 di questo piano, con label `risanamento`.

**0.2 Triage delle 27 PR draft del bot Cursor (#424-#450) (1-2 g)**
Tutte partono da `4cbaeaa`. Nella simulazione di merge in sequenza 19 si applicano pulite e 8 vanno in conflitto (#429, #430, #433, #439, #445, #446, #447, #450). Per ciascuna: revisione, test su Postgres (dopo 1.2), poi merge o chiusura.

| Azione | PR | Collegamento al piano |
|---|---|---|
| **Integrare** (rebase dove indicato) | #448 webhook Stripe ritentabili | A1-09, A3-03 |
| | #443 scadenza hold abbandonati → poi **#446** (rebase) prenotazioni host non auto-annullate | A3-13, A2-01 (fix parziale: serve comunque `BookingSource.Manual`) |
| | #442 → #438 → **#447** (rebase) → **#450** (rebase): cluster addebito differito e webhook | A3-14, A3-04 |
| | #431 snapshot ospite per prenotazione | A2-02, A9-01 (fix provvisorio; quella definitiva è 1.3) |
| | #437 ruoli sulle service request; #444 check-out dopo la data di check-in; #425 tipi documento check-in; #426 buchi auth short-rent; #427 invio RLI dietro flag | A4, A2-08, A5, A9, A7-01 |
| **Rivedere nel merito** | #424, #428, #441 | Toccano booking, fiscale e lease insieme |
| **Parcheggiare** (LTR in freeze) | #429, #430, #432, #433, #434, #435, #436, #439, #440, #445, #449 | Da riprendere solo se si decide l'"Assistente LTR" (Fase 5) |

**0.3 Spegnere il freeze e il codice fuori perimetro (1,5 g)**
- [ ] Flag `Features:LongTermRental` (OFF in prod). Nascondere l'opzione LTR/"Entrambi" nell'onboarding, il contesto long-rent e i menu. I job RLI diventano no-op (A7 passo 1, A8-16).
- [ ] API OTA: togliere voce di menu e rotte FE `/ota*`, deregistrare i job `ota-sync-all` e `booking-pull-all`, rimuovere `PUT /api/ota/pricing` e `validate?apiKey=` (A2-09, A9-15, A9-17).
- [ ] Discovery AI fornitori: flag OFF, rate limit e budget sulla chiamata `match-supplier` (A8-01, A8-07, A4-26).
- [ ] "Prezzi AI": rinominare in "Suggerimenti stagionali" senza "AI" e senza confidenza finta, oppure nascondere (A2-14, A8-17).
- [ ] Rimuovere il codice morto pericoloso: `POST /api/auth/register` (A1-30, A9-27), `GuestCheckInController` legacy (A9-30, A5-29), `SupplierRegisterPageController` (A4-03), `StripeWebhookHandler.cs.bak`, `.idea/`, `playwright-report/` (A9-34).

**0.4 Stub onesti: mai un successo finto (1,5 g)**
- [ ] **Alloggiati:** stato `DaInviareManualmente` al posto di `Submitted`, file tracciato scaricabile e banner in UI. Riscrivere i test che certificano la simulazione (A5-01, A9-05).
- [ ] Rimborsi e "process payment": 501 finché non esiste il rimborso reale (A3-05, A9-15).
- [ ] Notifiche stub (`Task.Delay`) e alert CIN: disattivare o implementare con `IEmailService` (A9-15, A5-31).
- [ ] LTR (anche con flag ON in test): `FilingEnabled=false` → 409 + registrazione manuale; firma offline; `LeaseTemplates:*:Approved=false` nel config base (A7-01/02/03).

**0.5 Sicurezza e dati, fix rapide (2 g)**
- [ ] **#274 upgrade gratuito:** `PUT /orgs/me/plan` solo per downgrade; `PlanTier` ignorato in onboarding; con `SubscriptionStatus.None` tier effettivo = Starter. Aggiornare `TenantBoundaryIntegrationTests.cs:160` (A1-03, A3-07, A9-02).
- [ ] **Ruoli Auth0 cancellati:** `AssignRoleAsync` solo in aggiunta e tolto da `SupplierOrgContextResolver` (A4-01, A1-17).
- [ ] **Hangfire:** schema per ambiente (`hangfire_test` / `hangfire_prod`). Verificare **subito** su Supabase se `hangfire.server` contiene server di entrambi gli ambienti (A9-03).
- [ ] **Email:** verificare il dominio mittente su Resend e togliere il fallback forzato a `onboarding@resend.dev`; far usare a `ServiceRequestService` lo stesso check di configurazione (A9-06, A4-06, A9-16).
- [ ] **SEO:** `BootstrapOnStartup=false`, `AutoApproveAfterBootstrap=false`, niente `counselApproved=true` di default; la rigenerazione torna in Draft (A8-04/05).
- [ ] **XSS e HTML:** sanitizer ad allowlist (DOMPurify / HtmlSanitizer); `HtmlEncode` in tutte le email costruite a mano (A8-08, A9-29, A4-08, A5-33).
- [ ] **SSRF:** validazione degli URL iCal (solo https, niente IP privati o link-local, limite dimensione) per property e fornitori (A2-21, A4-10, A9-32).
- [ ] **Rate limit per IP** con `UseForwardedHeaders` (#273): oggi il check-in ospite è limitato a 3 invii al minuto per **tutta la piattaforma** (A1-12, A3-12, A5-10, A9-10).
- [ ] **Pacchetti vulnerabili:** aggiornare `Microsoft.AspNetCore.DataProtection` e `System.Security.Cryptography.Xml`; rimuovere SendGrid (porta `starkbank-ecdsa` critical ed è inutilizzato) (A9-26).
- [ ] **INFRA.md:** documentare `Stripe__ConnectWebhookSecret`, `Stripe__PublishableKey` e i due endpoint webhook (A3-24).

**0.6 Sblocchi rapidi dei bug più visibili (2-3 g)**
- [ ] **Formato CIN** unico in Core e FE: normalizzazione più regex sul formato BDSR (`IT` + ISTAT 6 cifre + lettera + cifra + 8 alfanumerici, **da confermare con fonte ufficiale**); correggere anche `.claude/rules/compliance.md` e `cin.md` (R-02, A5-05).
- [ ] **Portale ospite:** campo sesso ed errori di validazione per campo (A5-04).
- [ ] **Checkout:** doppio `?` nel passaggio widget → checkout, e campi data nel checkout (A3-01, R-04).
- [ ] **Disponibilità pubblica** chiamata con `property.id` (A3-09, A2-13, R-03).
- [ ] **"Le mie prenotazioni":** aggiungere il codice prenotazione al form (A3-10, R-06).
- [ ] **Link del cockpit** verso rotte FE esistenti (A5-09).
- [ ] **Onboarding senza uscita:** POST con consensi quando manca l'org, admin non forzati, retry e "indietro" sempre visibili (A1-01).
- [ ] **Step tassa di soggiorno** come *warning*, create tariffe admin corretto, seed tariffe per i comuni pilota (A5-06).
- [ ] **FE:** `fetch('/api/…')` relativi → `ApiClient` (A4-16, A9-21); match per sottostringa degli endpoint pubblici nell'interceptor (A9-20); share link dell'app con lo slug dell'org (A3-22, A6-09).

**Uscita Fase 0:** nessuna funzione finta in produzione; freeze rispettato; PR Cursor smaltite; i 10 bug più visibili chiusi.

---

### Fase 1: fondamenta tecniche (settimane 2-4, circa 25-30 g)

Obiettivo: una CI che fallisce davvero e un'infrastruttura su cui le fix di feature reggono. **Fatta prima delle fix di feature**, altrimenti ogni fix poggia su tenancy, test e date che non proteggono.

**1.1 CI vera (3 g)** (A9-23, A9-24, R0)
- [ ] FE: correggere `e2e.yml` (`secrets` in `env:` di job); nuovo job obbligatorio `lint + typecheck + vitest + build`; eslint da 55 errori a 0.
- [ ] Mobile: workflow `tsc + eslint + expo export`; configurare eslint (oggi `expo lint` fallisce).
- [ ] BE: `dotnet list package --vulnerable` e `npm audit` come gate.
- [ ] Eliminare i job `echo` (backend `e2e-golden-journey.yml`, job Maestro placeholder) e il `continue-on-error`.
- [ ] Branch protection su `develop` e `main` con check obbligatori.

**1.2 Test su PostgreSQL (3 g)** (A9-11, A9-35, A2-37, A3-23, A5-35)
- [ ] `CasazenWebApplicationFactory` su Testcontainers PostgreSQL con isolamento per test. In questo audit, rieseguendo i 184 test d'integrazione su Postgres, sono emersi un bug reale e 2 test non isolati.
- [ ] Test "applica tutte le migrazioni + `HasPendingModelChanges()==false`".
- [ ] Riscrivere i test che codificano bug: `TenantBoundaryIntegrationTests.cs:160`, `SupplierConsoleIntegrationTests.cs:256`, `DirectCheckoutIntegrationTests.cs:273`, `AlloggiatiWebServiceTests.cs`, `PlgOnboardingIntegrationTests.cs:186`, `ServiceRequestIntegrationTests.cs:40`, `PropertyICalSyncServiceTests.cs:151`, `CinComplianceRulesTests.cs`.

**1.3 Confine tenant come infrastruttura (5-6 g)** (A9-01, A2-02, A5-07, A1-28, A1-20, A9-31)
- [ ] `Guest.OrgId`: migrazione in 3 step che sdoppia gli ospiti condivisi per org; unique `(OrgId, lower(Email))`; niente riuso globale per email.
- [ ] Interfaccia `ITenantOwned` con filtro registrato automaticamente per tutte le entità con `OrgId`, più un test architetturale su ogni `DbSet`.
- [ ] `OrgId` sulle entità figlie esposte direttamente (PropertyDocument, OtaIntegration, PricingAdapterConfig, AlloggiatiWebReport, GuestCheckInSession).
- [ ] Autorizzazione resource-based al posto dei 18 helper `GetUserRoles` copiati. La policy `PropertyOwner` (che oggi significa "autenticato") va rinominata e sostituita da policy di contesto uniformi (Guests, Gdpr, PricingAdapter, Payments, ServiceRequests).
- [ ] `TenantContext` asincrono con `SetOrgId()` dopo il provisioning; claim `org_id` dall'Action Auth0.

**1.4 Contratto errori e localizzazione backend (3 g)** (A9-07/08/09/19, R-07, A1-24, #15, #16)
- [ ] Gerarchia `DomainException` / `NotFoundException`: oggi ogni `InvalidOperationException` diventa **503** con il messaggio interno esposto.
- [ ] Solo ProblemDetails con `code` stabile.
- [ ] Fix del percorso delle risorse: oggi ogni risposta mostra la chiave grezza, es. `"detail":"UnauthorizedDetail"`. Migrare circa 210 messaggi hardcoded a `.resx` IT/EN.
- [ ] `ValidateOnStart` per le opzioni Stripe, Email, Auth0, Alloggiati.
- [ ] Health check reali (`/health/live`, `/health/ready` con DB, Hangfire e config).

**1.5 Date e fusi orari (2 g)** (R-01, A9-12, A2-15, A5-17, A7-05)
- [ ] `DateOnly` per le date di soggiorno, oppure binder/converter globale che forza UTC e ValueConverter EF che rifiuta `Unspecified`. Oggi una data `yyyy-MM-dd` dal client fa **500** su Postgres (verificato sul calcolatore tassa di soggiorno).
- [ ] "Oggi" calcolato in Europe/Rome (10 punti usano `UtcNow.Date`).

**1.6 Storage e chiavi (3-4 g)** (A2-03, A9-04, A2-31, A5-29, A5-30)
- [ ] Chiavi DataProtection persistite su DB: oggi a ogni deploy i segreti cifrati diventano illeggibili.
- [ ] Object storage (Supabase Storage): bucket pubblico per le foto, privato con URL firmati per i documenti di proprietà e ospiti; migrazione dei file esistenti. Oggi tutto finisce sul disco effimero del container e gli URL relativi su Vercel restituiscono `index.html`.
- [ ] Cifratura applicativa dei campi documento ospite e delle credenziali Questura.

**1.7 Client FE unico (2 g)** (A9-20/21/22, A9-09, A2-28)
- [ ] Flag `public` esplicito per chiamata; 401 → re-login; 403 → pagina no-access; niente retry sui 4xx.
- [ ] Parser ProblemDetails usato nei 44 `onError` che oggi mostrano un toast generico; regola ESLint che vieta `fetch` diretto.
- [ ] Messaggi Zod tradotti con `t()`: oggi compaiono chiavi grezze come `property.validation.name.minLength`.

**1.8 Identità Auth0 (2-3 g)** (A1-02, A1-22, A1-13, A1-32, A6-01)
- [ ] Token Management API via client credentials con cache (oggi token statico che scade in circa 24h); esito della sync propagato; `UserContextMembership` scritte per tutti i ruoli, così l'autorizzazione non dipende da Auth0.
- [ ] Action Auth0 con claim `email`, `name`, `email_verified` sull'access token, più backfill. Oggi utenti e org nascono anonimi ("La mia organizzazione", customer Stripe senza email).
- [ ] Collegamento degli account solo con email verificata o token d'invito (A1-13, A4-23).
- [ ] Client Auth0 **Native** per l'app mobile; tenant separati test/prod (EU per prod).

**1.9 Infrastruttura notifiche (3 g)** (A9-16, A3-11, A5-11, A6-05/06/07, A4-20)
- [ ] Un solo `IEmailService` con opzioni tipizzate; template IT/EN; invio accodato su Hangfire (mai dentro la transazione della richiesta).
- [ ] Deduplica degli alert: oggi l'alert Alloggiati parte ogni ora, circa 200 email e push per prenotazione.
- [ ] Push: FCM/APNs su EAS con `projectId` reale, `deviceId` stabile, gestione delle ricevute.

**1.10 Parità i18n FE (1-2 g)** (A9-25, A5-32, R-08)
- [ ] 78 chiavi mancanti in `en.json` (tutto `compliance.*`); 3 chiavi mancanti in entrambe le lingue; circa 80 stringhe hardcoded; plurali i18next ("3 nottei").
- [ ] Test di parità delle chiavi ed `eslint-plugin-i18next/no-literal-string` in CI.

**Uscita Fase 1:** CI rossa quando qualcosa si rompe; test su Postgres; nessun dato perso al deploy; tenant isolati per costruzione; errori leggibili in italiano.

---

### Fase 2: Golden Journey funzionante da UI, step per step (settimane 5-10, circa 55-70 g)

Ogni blocco si chiude **solo** con il relativo test L3 da UI verde (vedi Fase 4, sviluppata in parallelo).

**2.1 Step 1-2: fornitore (5-6 g)** (A4)
- [ ] Invito che punta alla SPA (`/register?inviteToken=`) con login SDK Auth0; form di registrazione con campi editabili senza invito; token legato all'email; allowlist dei comuni pilota; rate limit (A4-03, A4-04).
- [ ] Endpoint `POST /api/suppliers/claim` dopo il login; onboarding e home route consapevoli del fornitore; opzione "Sono un fornitore" (A4-02).
- [ ] **Tassonomia unica delle categorie:** enum backend + `GET /api/suppliers/categories` + migrazione dati `Pulizie→cleaning`; FE e app usano solo i codici tradotti. Eliminare la regola "zero categorie = compare ovunque" (A4-05, A6-03).
- [ ] **Anagrafica comuni ISTAT** completa: oggi il registro ha 12 comuni con F205 = "Firenze" (è Milano) e Torino con il codice di Genova. Aggiungere `Property.ComuneIstatCode` e `RegionCode` e un picker comuni (A4-12, A5-34, A5-19).
- [ ] Attivazione con requisiti reali (categorie, comuni, bio, telefono) e wizard a 5 step persistito; ToS con link e versione (A4-09, A4-31).
- [ ] Stati d'errore in tutte le pagine fornitore; area admin fornitori (elenco, sospensione, inviti) (A4-25, A4-29).

**2.2 Step 3: host, property, sito (10-12 g)** (A1, A2, A3, A5)
- [ ] Consensi imposti dal backend (`UserRole.None` di default, 403 `onboarding_required`); utente disattivato davvero bloccato; org fornitore separata dall'org host (A1-05, A1-04, A1-40).
- [ ] **Impostazioni org e branding:** nome, slug leggibile (oggi `org-<auth0 sub>`), email di contatto pubblica opt-in, logo, hero, colori, tagline, tema; pagina "Aspetto sito" (A1-22/23, A3-17).
- [ ] Property: PUT con semantica PATCH e campi mancanti nel form (pulizia, deposito, regole, policy); pausa/attiva con endpoint dedicato; UI galleria foto; soft delete; indice indirizzo per org con interno; coordinate `numeric(9,6)` (A2-04/05/26/18/19/33/27).
- [ ] Wizard attivazione: bug FE (checklist sovrascritta al reload, 409 senza blocker mostrati), rivalutazione dello stato e `Suspended`, checklist sicurezza validata da un legale con opzione "non applicabile" (A5-18/20/21/36).
- [ ] iCal: più feed per property (Airbnb + Booking insieme), feed vuoto = rimozione blocchi, robustezza del job, URL cifrata, export `VALUE=DATE` senza SUMMARY sensibili, UI con sincronizza/scollega ed errori leggibili (A2-10/11/12/20/22/23/24).
- [ ] Sito "pubblicato" solo con `chargesEnabled` e banner in Vetrina; Connect senza reset su errori transitori e con idempotency key (A3-26, A3-19, A3-42).

**2.3 Step 4: prenotazione e pagamento del guest (10-12 g)** (A3, A5-16)
- [ ] **Tassa di soggiorno unificata** su `TouristTaxRate` (esenzioni per età, tetto notti) + `POST /api/public/bookings/quote` usato da widget e checkout; eliminare la tabella `TaxRates` (A3-02, A5-16).
- [ ] Idempotenza webhook transazionale (dopo #448); job di scadenza dei `Pending` con annullamento di PI/SI (dopo #443); pagamento arrivato su prenotazione cancellata → riconferma o rimborso automatico (A3-03/13/04).
- [ ] **Rimborsi reali** su Connect, `charge.refunded` dalla sorgente Connected, policy di cancellazione → importo, cancellazione lato guest (#51, A3-05).
- [ ] "Paga in struttura" abilitabile per property, con approvazione host o carta a garanzia e limiti di soggiorno. Oggi un anonimo può bloccare un anno di calendario, anche sulle OTA via iCal (A3-06).
- [ ] Checkout: pagina di esito basata sullo stato reale, gestione dei redirect Stripe, errori per codice, opzioni di pagamento corrette, precompilazione dal widget (A3-15/16/34); addebito differito robusto con `OffSession` (A3-14, cluster PR).
- [ ] **Email transazionali** (#58): conferma, ricevuta, cancellazione e rimborso a guest e host; push all'host (A3-11).
- [ ] Pagine privacy e termini per operatore: oggi i link del footer portano al login (A3-21); guest orfani con PII (A3-33).

**2.4 Step 5: calendario host (6-7 g)** (A2)
- [ ] Prenotazioni manuali dell'host create `Confirmed` con `BookingSource.Manual`, mai auto-annullate (A2-01).
- [ ] Ciclo di vita della prenotazione da web: DTO di modifica (oggi il PUT fa binding dell'entità EF e fallisce sempre), azioni Conferma, Check-in e Annulla con rimborso, CTA "Nuova prenotazione" (A2-07/08).
- [ ] Calendario web e app con range corretto e refetch alla navigazione; blocchi manuali di date (A2-06/16/25).
- [ ] Performance: niente query N+1 su liste prenotazioni e ospiti (A2-17); KPI dashboard reali (A2-29).

**2.5 Step 6: check-in ospite e Alloggiati (12-15 g, rischio penale)** (A5)
- [ ] Azione "Registra arrivo" su web e app; `start` e `complete` del check-out allineati (A5-08).
- [ ] Fallback host: form "Dati Alloggiati", link copiabile quando l'email non parte, "Invia link" sempre disponibile (A5-26/27/28).
- [ ] **Modello a N ospiti per soggiorno** (`StayGuest` con tipo alloggiato) e tabelle codici Alloggiati (comuni, stati, documenti) con autocomplete (A5-02).
- [ ] **Client del web service Alloggiati** (autenticazione, test file, invio, ricevuta), job schedulato alla data d'arrivo in Europe/Rome, idempotenza per ospite, stati `DaInviare`, `Inviato` (con ricevuta), `Rifiutato`, `InvioManualeRichiesto`. Finché non è pronto resta il file manuale del punto 0.4 (A5-01/03/37).
- [ ] GDPR: anonimizzazione ed export completi, retention per categoria (Alloggiati 5 anni, fiscale 10, scansioni X giorni), consensi con versione, informativa art. 13 separata dal consenso marketing, Party (#179) (A5-12…15, A9-18).
- [ ] **Decisione:** prenotazioni OTA importate via iCal (oggi solo blocchi di calendario, quindi niente link check-in né Alloggiati). Vedi §7.

**2.6 Step 7-10: incarico fornitore (5-6 g)** (A4, A6)
- [ ] Chiave di correlazione unica web↔app (`bookingId` sì o no, decisione §7) e aggiornamento di #340 (A4-13).
- [ ] Il fornitore vede indirizzo, data e contatto; pagina di dettaglio `/app/supplier/inbox/:id` con CTA fissa a 375px; tab storico (A4-14).
- [ ] Timeline host completa (presa in carico, data, motivo del rifiuto), conferma prima di "Segna pagato", CTA "Richiedi ad altro fornitore"; notifiche su rifiuto e pagamento (A4-28, A6-08).
- [ ] Concorrenza (`xmin`), validazione DTO, 500 evitabili (A4-17/18/19/21).
- [ ] Rimuovere `SupplierJob` e il check-in QR, oppure fonderli in `ServiceRequest`; KPI della dashboard fornitore ricalcolati su `ServiceRequest` (oggi sempre 0) (A4-15).
- [ ] App: scelta del fornitore via `match-supplier` invece del primo della lista (A6-18).

**2.7 Step 11-12: check-out e cockpit (4-5 g)** (A5)
- [ ] Wizard di check-out a 5 step basato su `/start`: conferma partenza, chiusura Alloggiati, riepilogo tassa riscossa, pulizie con picker fornitori (crea la `ServiceRequest`), pagamento, "property pronta" salvato davvero (A5-24).
- [ ] Reminder di check-out via email + push, schedulato alla conferma (A5-25).
- [ ] Cockpit con semafori basati sugli stati reali (ricevuta Alloggiati presente o no, check-out dovuti), link funzionanti.

**2.8 App host (in parallelo, 8-10 g)** (A6)
- [ ] Configurazione EAS: `projectId` reale, `env` per profilo, build che fallisce senza `EXPO_PUBLIC_API_URL`; demo E2E protetta da `__DEV__` (A6-02, A6-21).
- [ ] Sessione: refresh token single-flight, logout completo con deregistrazione del device, gating onboarding e consensi (A6-14/15, A1-33).
- [ ] Calendario (selettore proprietà, blocchi iCal non cliccabili, range corretto), dettaglio booking (stato check-in ospite, badge compliance, pull-to-refresh), check-out con riepilogo, stati d'errore espliciti (A6-10…17, A6-13).
- [ ] Riepilogo compliance (CW-AC15); tap su notifica ad app chiusa che apre il booking giusto (A6-19); proprietà visibili anche ai property manager (A6-20); i18n IT/EN (A6-27).

**Uscita Fase 2:** i 12 step del Golden Journey percorribili da UI su staging con tre utenti distinti, verificati dai test L3 della Fase 4.

---

### Fase 3: MVP vendibile (settimane 11-14, circa 35-45 g)

**3.1 Billing SaaS (8-10 g + commercialista)** (A1-07…11, A1-31, A1-41)
- [ ] FE: pagina piani con checkout, pagina billing con subscription e portale, paese e P.IVA, badge di stato.
- [ ] Webhook e checkout robusti: niente subscription duplicate; stati `incomplete` che non concedono il piano.
- [ ] Configurazione ambienti: test in `Staging` e non in `Production` (oggi il gate di billing si chiude anche sul test), URL di ritorno reali, price id reali.
- [ ] IVA/OSS corretta (aliquota del paese di destinazione, IT sotto soglia, extra-UE fuori campo) con Stripe Tax; provider SDI reale. **[COUNSEL_REQUIRED]**

**3.2 Legale e PLG (3 g + legale)** (A1-06, A1-37, A1-15, A1-39)
- [ ] Testi ToS, Privacy e DPA pubblicati (`/legale/*`) e linkati dal wizard; elenco sub-responsabili veritiero (Resend, Auth0 regione reale, Expo, DeepSeek, Vercel); ri-accettazione al cambio di versione.
- [ ] Checklist di attivazione in dashboard (PLG-AC10) su `GET /api/onboarding/status`; flag "sito pubblicato" reale.

**3.3 Siti pubblici e SEO dei siti host (6-8 g)** (A3-18/20/32/35, A3-27/28, A8-29)
- [ ] Temi realmente diversi con font caricati; contrasto AA; stati d'errore.
- [ ] Prerender o SSR delle rotte `/book/*` e `/p/*`: title e meta, OG, canonical, JSON-LD `VacationRental`, sitemap per org, `robots.txt`, `lang="it"`.

**3.4 Funnel SEO #300 (6-8 g)** (A8)
- [ ] ADR sul dominio canonico (decisione §7); sitemap e canonical sul dominio del FE.
- [ ] Rotta `/signup` con attribuzione (comune, UTM); immobili in evidenza (`OrgSlug` nel DTO pubblico); `SeoEvent` senza PII; widget top-comuni nella dashboard admin.
- [ ] Contenuti pilota su 3 comuni con prompt versionato, anteprima, audit dell'approvazione e tariffe precaricate; avviso quando manca la tariffa.
- [ ] Ricerca `/search`: fix NaN/zod, link ai dettagli, shell pubblica (A8-13, A3-27).

**3.5 Domini (4-5 g)** (A3-08/25, CD-AC9/11)
- [ ] Middleware Vercel o route pubblica per host non di default; CORS dinamico (`*.casazen.it` più domini verificati) o proxy `/api`.
- [ ] Vercel Domains API (add, verifica, SSL), controllo CNAME, ricontrollo periodico, runbook in INFRA; step di onboarding "modalità di pubblicazione"; un host beta reale.

**3.6 Fiscale e imposta di soggiorno (5-6 g)** (A5-22/23, #4)
- [ ] Soglia per contribuente; ritenuta solo senza P.IVA; regime IRPEF ordinaria; UI regime per property; report per property e OTA con CSV e PDF tabellare in italiano.
- [ ] Rendiconto dell'imposta di soggiorno per comune e periodo.

**3.7 Admin (3 g)** (A1-16/17/26/27, A4-29)
- [ ] Rotta audit CIN; gestione multi-ruolo con audit; filtri e paginazione SQL; stati d'errore.

---

### Fase 4: gate di uscita MVP (in parallelo dalla Fase 2, circa 12-15 g)

Riferimento: PLANNING §"Gate di uscita MVP". Oggi nessuno dei 9 gate è verificato in modo affidabile.

- [ ] **Stack effimero in CI** sulle PR verso `develop`: `postgres:16`, API dal commit del backend, migrazioni, health che rifiuta InMemory, FE con `VITE_DEMO_MODE=false`; tre identità Auth0 di test (host, fornitore, guest anonimo); Stripe test mode con `stripe listen --forward-connect-to`.
- [ ] **Golden Journey web L3 riscritto da UI** (12 step): wizard fornitore → property da UI → prenotazione guest sul sito con pagamento test → calendario → form di check-in ospite compilato → richiesta dal form host → take/complete dall'inbox fornitore a 375px (F1-F2) → "Segna pagato" → wizard check-out → cockpit. Le API servono solo come oracolo. `gj-seed.json` come artifact.
- [ ] **Maestro reale:** login Auth0 vero, push vera sull'emulatore (M3), assert per ID e `testID`, niente `optional` né chiusura automatica degli ANR; seed dedicato per l'app; APK `preview` su emulatore in CI (nightly e release PR); job fallito su `FATAL EXCEPTION` o `ANR`.
- [ ] **Parità web↔app:** dopo ogni step confronto dello stato mostrato con `GET /bookings/{id}` e `GET /service-requests/{id}`.
- [ ] **Branch protection:** "GJ web L3" obbligatorio sulle PR verso `develop`; "GJ app M1-M7" obbligatorio sulla release PR `develop` → `main`.
- [ ] **Run su staging e poi su prod** con un host beta, con registrazione video. Solo allora il registry passa a "shipped".

---

### Fase 5: dopo l'MVP (da decidere, fuori dal perimetro corrente)

| Tema | Proposta | Effort indicativo |
|---|---|---|
| LTR | "Assistente LTR" onesto: calcolatore concordato con dati verificati, bozza del contratto, precompilazione RLI, promemoria con scadenza corretta, firma e registrazione **manuali** (A7 passi 3-7) | 15-20 g + legale |
| Prezzi dinamici | `PropertyDailyRate` usato da preventivo e checkout, oppure integrazione di un pricing esterno (A2-14) | L |
| Vetrina fornitore #303 | Slug, `PublicSiteShell`, CTA, anteprima (A4-16) | 3-4 g |
| App fornitore #304 | Flavor Expo "supplier" (A6 fase 4) | 10+ g |
| ISTAT #6, regionale/CIR #8 | Da specificare con legale | L |
| API OTA partner | Solo con contratto partner | — |
| Org seats US-013 | Non iniziato | M |

---

## 6. Stima complessiva e sequenza

| Fase | Contenuto | Giorni-persona | Settimane (1 dev full-time) |
|---|---|---|---|
| 0 | Messa in sicurezza, freeze, PR Cursor, sblocchi rapidi | 8-10 | 2 |
| 1 | CI, test Postgres, tenant, errori, date, storage, Auth0, notifiche, i18n | 25-30 | 5-6 |
| 2 | Golden Journey da UI, step per step, più app host | 55-70 | 11-14 |
| 3 | Billing, legale, siti, SEO, domini, fiscale, admin | 35-45 | 7-9 |
| 4 | Gate L3 web e app in CI, run staging e prod | 12-15 | in parallelo alle Fasi 2-3 |
| **Totale MVP** | | **circa 135-170 g** | circa 6-8 mesi con 1 dev; circa 3,5-4,5 mesi con 2 dev in parallelo (Fasi 2 e 3 si parallelizzano bene per area) |

A questi si aggiungono i tempi di legale e commercialista (IVA/OSS, SDI, checklist sicurezza, testi legali, CIN, Alloggiati). Le stime vengono dagli effort S/M/L dei singoli difetti, ridotti del 25% circa per le sovrapposizioni tra aree.

**Percorso critico:** 0.5/0.6 → 1.1/1.2 → 1.3 → 2.1 (categorie, comuni) e 2.2 (property Active) → 2.3 (guest) → 2.5 (check-in) → 2.7 (check-out) → Fase 4.

---

## 7. Decisioni richieste al product owner

Senza queste decisioni alcuni punti restano bloccati. Per ciascuna c'è una raccomandazione.

| # | Decisione | Raccomandazione | Blocca |
|---|---|---|---|
| D1 | LTR: completare ora o spegnere? | **Spegnere dietro flag** e riprenderlo dopo la Golden Journey come "Assistente LTR" | 0.3, parcheggio PR |
| D2 | Richiesta fornitore legata a `bookingId` (PLANNING M4) o alla sola property (#340)? | `bookingId` **opzionale ma valorizzato** quando c'è un soggiorno; #340 va aggiornata | 2.6 |
| D3 | Dominio canonico (`casazen.it`, `casazen.app` o Vercel) | Un dominio proprio (ADR), con sitemap e canonical coerenti | 3.3, 3.4, 3.5 |
| D4 | "Prezzo AI": rinominare/nascondere o implementare? | Rinominare ora in "suggerimenti", implementare dopo l'MVP | 0.3 |
| D5 | "Paga in struttura" | OFF di default; ON per property con approvazione host o carta a garanzia | 2.3 |
| D6 | Pagamento del fornitore: flag manuale o Stripe? | Flag manuale nell'MVP, con importo registrato | 2.6 |
| D7 | Alloggiati: integrazione WS nell'MVP o invio manuale assistito? | **Manuale assistito subito** (file tracciato + stato onesto), WS reale entro la Fase 2 | 0.4, 2.5 |
| D8 | Prenotazioni OTA importate via iCal: generano un soggiorno con check-in ospite? | Sì: "soggiorno OTA" creabile dall'host da un blocco, con link check-in | 2.5 |
| D9 | Storage file | Supabase Storage (stesso provider del DB) | 1.6 |
| D10 | Test e prod sullo stesso progetto Supabase? | Due progetti separati, oppure almeno schema Hangfire e utenti DB separati | 0.5, 1.x |
| D11 | Tenant Auth0 | Tenant di produzione EU separato da quello di sviluppo | 1.8, 3.2 |
| D12 | `SupplierJob` e check-in QR | Eliminare e tenere solo `ServiceRequest` | 2.6 |
| D13 | `chargeToGuest` (addebito all'ospite del servizio extra) | Rimandare a dopo l'MVP; togliere il campo dalla UI | 2.6 |
| D14 | Bot Cursor che apre PR automatiche | Metterlo in pausa durante la Fase 0 e poi indirizzarlo alle issue `risanamento` | 0.2 |

---

## 8. Riferimenti

- Report di dettaglio: [`audit-2026-09-23/README.md`](./audit-2026-09-23/README.md)
  - `R0-runtime.md`: build, test, migrazioni, test su Postgres, smoke API e browser
  - `A1-piattaforma.md`: accesso, onboarding, billing, admin
  - `A2-proprieta-calendario.md`: proprietà, calendario, iCal, pricing, OTA
  - `A3-booking-pagamenti-siti.md`: direct booking, Stripe/Connect, siti pubblici, domini
  - `A4-fornitori-marketplace.md`: console fornitore, micro-marketplace
  - `A5-compliance.md`: CIN, wizard, check-in ospite, Alloggiati, GDPR, fiscale, tassa di soggiorno
  - `A6-mobile-e2e.md`: app host, push, harness Golden Journey
  - `A7-ltr.md`: affitti lunghi
  - `A8-seo-ai-freeze.md`: SEO, funnel, AI, feature in freeze
  - `A9-trasversale.md`: sicurezza, inventario endpoint, test, CI
- Visione e Golden Journey: [`PLANNING.md`](./PLANNING.md)
- Registry spec: [`specs/README.md`](./specs/README.md) (da aggiornare in Fase 0.1)
