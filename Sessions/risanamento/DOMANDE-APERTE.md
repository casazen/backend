# Domande aperte e documentazione — piano di risanamento CasaZen

**Fonte unica** delle domande aperte. Aggiornato dalla sessione di ripresa del 2026-10-01 (branch `claude/sleepy-edison-oil8ru`). Ogni voce riporta l'ID del task tra parentesi. Nulla viene tolto: le voci risolte si spostano in fondo, nella sezione **Chiuse**, con la data e la risposta.

Le voci nuove di questa sessione sono nella sezione **8** (in fondo, prima di *Chiuse*), con il formato `- [ ] TASK-ID — domanda — default prudente applicato — data`.

---

## 1. Azioni da fare TU prima del deploy (blocca produzione se mancano)

**Railway — variabili d'ambiente obbligatorie** (l'API non si avvia senza):
- `Storage__*` — Supabase Storage (FD-07)
- `Email__ApiKey`, `Email__FromAddress` (dominio verificato su Resend), `App__PublicSiteBaseUrl` (FD-13) — impostare **prima del merge**
- `Cors__AllowedOrigins` (FD-17); `Cors__VercelPreviewPattern` solo in test
- `DataProtection__CertificatePfxBase64` + password, su **test e prod** (CO-14, FD-07)
- `Billing__Prices__*` (chiavi Stripe price id per ambiente; live in prod) (PL-11)
- `App__PublicSiteBaseUrl` / dominio pubblico definitivo — scegliere **prima di main**, altrimenti la build prod fallisce (SE-02)
- `Legal__Documents__Privacy__DocumentUrl` / `...Tos__DocumentUrl` — link footer Privacy/Termini (SE-03)
- `ASPNETCORE_ENVIRONMENT=Staging` sull'ambiente Railway "test" (PL-11)
- Webhook secret Stripe: oggi è un placeholder → 500; impostare il segreto reale su Railway (PL-10)

**Auth0:**
- Creare client "Native" per l'app mobile + verificare login su device reale (runbook `auth0.md` §7.7) (MO-01)
- Riparare manualmente gli utenti con doppio ruolo che hanno già perso un ruolo prima della fix (runbook `auth0.md` §9) (FD-14)
- Senza M2M configurato, gli endpoint di gestione ruoli rispondono 502 `auth0_management_not_configured` (FD-14)
- Decidere quale tenant Auth0 tenere per produzione (PL-11)

**Supabase / DB:**
- Verificare/creare progetto Supabase separato per prod, o impostare GRANT adeguati (FD-11, FD-07)
- Hangfire: impostare `Hangfire__Schema` (prima test poi prod), gestire i job pendenti dello schema vecchio, verificare `hangfire.server` (FD-11)
- **Prima del deploy di SU-11**: eseguire `SELECT count(*) FROM SupplierJobs` — la tabella viene droppata da una migrazione (procedura di export in runbook `suppliers.md` §10.3) (SU-11)
- Scaricare le tabelle ufficiali Alloggiati Web (COMUNI, STATI, DOCUMENTI, TIPO_ALLOGGIATO) e importarle con `POST /api/admin/alloggiati/code-tables/{tabella}` (CO-12)
- Alla fine dei lavori: eliminare i DB `it_*` residui degli esperimenti degli agenti (FD-04)

**Railway/CI generali:**
- Attivare "Wait for CI" su Railway; Healthcheck Path = `/api/health/ready`; confermare che l'ambiente test Railway coincide con "Production" nel loro pannello; `verify-test` richiede la variabile `RAILWAY_GIT_COMMIT_SHA` (FD-12)
- Rendere obbligatorio (branch protection) il check CI su `develop`/`main` (richiede permessi admin GitHub) (FD-01)
- Aggiornare `dotnet-ef` globale a 10.0.12 (FD-02)

**Rete/ambiente agenti (non produzione, ma blocca dei task):**
- RS-6 **bloccato**: `www.istat.it` e altri siti ISTAT/gov ufficiali sono bloccati dal proxy di rete dell'ambiente (403). Serve una di queste due cose per sbloccare SU-04 (codici catastali comuni): (a) whitelistare `www.istat.it` nella rete dell'ambiente, oppure (b) fornire tu il CSV ufficiale dei codici catasto/ISTAT comuni.
- ~~FD-19 bloccato~~ → chiuso il 2026-10-01 (vedi *Chiuse*).

---

## 2. Domande per il commercialista

- CasaZen è sostituto d'imposta sui pagamenti "direct booking" via Stripe Connect? (RS-5)
- Gestione comproprietà (più CF sullo stesso immobile) — come vanno contate ai fini soglia 2 unità per cedolare secca 26%/21%? (RS-5, CO-18)
- P.IVA dichiarate dagli host non sono verificate a livello di sistema — accettabile? (RS-5)
- Slittamento del termine di 30 giorni per la registrazione del contratto quando cade di sabato o festivo — si applica? (LT-04)
- Chi paga materialmente le ~12,90 €+IVA per pratica di registrazione RLI (Agenzia Entrate)? (RS-4, LT-01)
- Budget/piano da attivare su Youtrust per firma AES (necessaria oltre alla FEA semplice) (RS-4, LT-02)

## 3. Domande legali

- **Check-in ospiti**: la Cassazione (sent. 9101/2025) richiede identificazione "de visu"? Il self check-in online è comunque conforme al D.L. 145/2023? (RS-1)
- **Checklist sicurezza D.L. 145/2023** (RS-3, poi implementata in CO-07): 12 punti irrisolti sulle prescrizioni (estintori, impianti, segnaletica) da far validare a un legale — elenco completo in `.claude/context/regulations/sicurezza.md`.
- **Canone concordato — accordo Monza-Brianza**: 19 discrepanze/ambiguità nel testo (arrotondamento mq, coefficienti da sommare o moltiplicare, sub-fascia 3, pertinenze, durate oltre 6 anni, transitori/studenti non regolati) — dati tenuti volutamente `Partial` finché non risolte. Elenco in `.claude/context/regulations/canone_concordato.md` (RS-8, LT-10).
- **Ross1000 / flussi turistici Regione Lombardia** (RS-9): 6 dubbi aperti — chi fa da referente privacy, come ottenere le credenziali Ross1000, ruolo del wizard, sanzioni applicabili, rapporto CIR/CIN, esiste un ambiente di test ARIA? Dettaglio in `.claude/context/regulations/istat-flussi-turistici.md` e `cir-lombardia.md`. L'implementazione (CO-22) è ancora da fare.
- **Firma elettronica contratti** (LT-02): serve un parere legale su erogatore FEA conforme al DPCM 22/02/2013 art. 57; verificare se la firma "semplice" self-serve basta o serve sempre AES/QES per la registrazione.
- **RLI — chi deposita la pratica**: serve parere su chi (CasaZen o il locatore) è il soggetto che deposita/ha la delega, e sul testo di attestazione da mostrare (RS-4, LT-01).
- **PDF/A per RLI**: l'Agenzia Entrate richiede PDF/A-1a/1b; oggi generiamo PDF normali. Serve un passaggio di conversione o basta caricare il firmato dall'utente? (LT-09)
- Testi delle clausole contrattuali per ciascun regime (libero, concordato, transitorio) — servono dal Product Owner, non inventati (LT-03).

## 4. Decisioni di prodotto aperte, per area

### Check-in / Compliance / Alloggiati
- `activation_documents_missing` non indica quali documenti mancano — aggiungere gli argomenti e mostrarli nel wizard? (CO-05)
- Check-in incompleti nel cockpit: linkare la scheda "Alloggiati" o la scheda "Ospite"? Oggi va su Alloggiati (CO-04) — cambiabile in una riga se preferite l'altra.
- Upload scansione documento nel nuovo portale check-in: mantenerlo o toglierlo per minimizzazione dati GDPR (Alloggiati Web non lo richiede)? (CO-16)
- Cancellazione/retention non elimina ancora i file scansione documento dallo storage (difetto A5-12) — assegnato a CO-15.
- Anonimizzazione del prenotante anonimizza anche gli accompagnatori (StayGuest) — che retention si vuole per loro? (CO-12, CO-15)
- Mascheramento numero documento: `*****` + ultime 3 cifre — ok? (CO-02)
- Dopo il completamento del check-in, l'endpoint pubblico risponde solo `{completed,status}` — serve anche il nome della struttura? (CO-02)
- Scadenza esposizione CIN reale (`Cin:ExposureDeadline`) — oggi vuota di default; il MiTur sembra indicare 01/01/2025, da confermare (CO-20).
- Alert CIN: notificare al primo controllo o solo su cambiamenti? Escludere le property solo-LTR (manca ancora il flag)? (CO-20)
- Suspended (proprietà non conforme): serve un'email di riattivazione? L'export iCal deve continuare (solo notti occupate) o bloccarsi del tutto? (CO-06)

### Prenotazioni / Pagamenti
- Cancellazione self-service da parte dell'ospite: serve un link firmato? Come si calcola l'importo dopo la scadenza della policy? (BK-02, BK-07)
- Se l'host cancella, il rimborso è sempre totale indipendentemente dalla policy? La tassa di soggiorno va esclusa dal calcolo % rimborso? (BK-02)
- "Segna come incassato" per pagamenti offline — serve? (BK-02)
- OnSite (paga in struttura): finestra di approvazione host 24h e durata massima soggiorno 30 notti sono provvisorie — confermare (BK-06). Le richieste in attesa non sono esportate su iCal (rischio overbooking nella finestra di 15').
- Riconferma pagamento anche per prenotazioni annullate manualmente (storiche) o va fatto un rimborso? (BK-04)
- Tentativi di addebito differito: `MaxAttempts=3` / `CancelAfterDays=3` provvisori — confermare (BK-08). Proposta: email CasaZen dedicata "Pagamento ricevuto" + disattivare le email native di Stripe (non ancora implementata, serve decisione PO).
- Contatto host da mostrare nelle email di conferma ospite: oggi è il contatto pubblico del footer, non quello reale dell'host — serve un contatto opt-in? (BK-10, nota per BK-12)
- Catalogo `CancellationPolicy`: non esiste seed né pagina admin — il selettore in UI è vuoto. Serve una policy standard di default decisa dal PO + pagina di gestione (PC-02).
- BookingCode a 10 caratteri già generato ma non ancora mostrato in console host/conferma OnSite (mostra ancora il Guid) — micro-fix pendente.

### Calendario / iCal / Tariffe
- Import iCal: soglia di 2 sincronizzazioni vuote consecutive prima di considerare "sospetto" — non implementata, serve? (PC-10)
- Suggerimenti stagionali: la regola di esempio di default (basata su festività L.260/1949 + L.151/2025) va bene come proposta iniziale, o volete regole diverse? (PC-15)
- KPI proprietà: le penali di cancellazione vanno incluse nel ricavo? I blocchi iCal contano come "occupati" nei KPI? (PC-16)
- Tariffe tassa di soggiorno: Roma e Venezia risultano "non disponibile" perché manca la categoria struttura/ISTAT sulla property — va aggiunto un campo categoria struttura? Bologna non ancora caricata (BK-03, PC-02).
- Link per canale/OTA come "ponte" tra iCal export e piattaforme OTA — richiesta futura? (PC-12)

### Affitti lunghi (LTR)
- PropertyManager può firmare/delegare per RLI e IMU per conto del proprietario? (LT-05)
- Landlord solo-LTR: `nightlyRate`/`maxGuests` restano 0 — atteso o serve un modello diverso per LTR-only? (LT-05)
- Stima IRPEF nel calcolo canone concordato: mostrarla sempre o nasconderla finché non è più precisa? (LT-08)
- Contratti già scaduti al momento del deploy: inviare una singola email "scaduto" di allineamento o nulla? (LT-07)

### Fornitori (Suppliers)
- Comuni pilota per self-serve fornitori: oggi `Suppliers:PilotComuni` è vuoto (= self-serve spento ovunque) — quali comuni attivare? Il criterio è codice catastale o ISTAT? (SU-01)
- Verifica email obbligatoria per il claim self-serve del profilo fornitore? (SU-02)
- In caso di deploy con fornitori duplicati appartenenti ad account diversi o sospesi, il deploy va bloccato o si procede comunque? Il merge di default è in dry-run — va bene come default? (SU-14)
- `RejectedAt` dedicato per le richieste rifiutate? La definizione di "in arrivo" (PresoInCarico+InCorso) e i relativi periodi KPI vanno bene? (SU-11)
- **Dato errato da correggere**: nel registro comuni (`ItalianComuneRegistry`) F205 non è Firenze ma **Milano**; il codice per Torino sembra in realtà quello di Genova (010025); Varenna (LC) manca (013182). Serve una fonte ufficiale ISTAT per correggere — collegato al blocco RS-6 sopra (SU-01, SU-04).

### Piattaforma / Billing / Onboarding
- Piano gratuito/pilota concedibile da un admin? Lo step "Scegli piano" nel wizard va sostituito interamente dal nuovo checkout? Come comunicare a chi aveva Pro gratuito che torna a Starter? (FD-18, PL-12)
- "Passa a un piano inferiore": va mostrato anche per org senza abbonamento attivo ma con un tier assegnato manualmente da un admin? (PL-12)
- Export GDPR e endpoint `/api/gdpr/*`: vanno lasciati accessibili anche quando l'utente deve riaccettare nuovi consensi, o bloccati come il resto? (PL-02)
- Starter deve diventare un piano a pagamento? (PL-11)
- Token Auth0: tenerlo in `localStorage` (persistente) o passare a `memory` (più sicuro ma logout ad ogni refresh)? Ricorre in più task (FD-15, FD-17, FD-08).
- Terminologia IT: "Landlord" → "Locatore" o "Proprietario"? (FD-09)

### Mobile
- Durata del refresh token per l'app (ricorrente in MO-01/MO-05) — quanti giorni?
- Pulizia dei vecchi `deviceId` di push non più attivi — serve un job? (MO-03)
- Calendario offline in app: i nomi ospiti vanno tenuti in chiaro nella cache locale o cifrati (richiede una dipendenza nativa aggiuntiva)? (MO-07)

### SEO / Sito pubblico
- Serve un banner di consenso cookie per il tracking UTM sulle landing page pubbliche (`/p/*`)? (SE-03)

---

## 5. Documentazione tecnica creata (runbook — `backend/docs/runbooks/`)

Ogni file copre configurazione, variabili d'ambiente e passi operativi per l'area:

`ai.md` · `alloggiati.md` · `auth0.md` · `canone-concordato.md` · `ci-backend.md` · `ci-frontend.md` · `cin-format.md` · `compliance.md` · `cors-security-headers.md` · `demo-mode.md` · `direct-booking.md` · `email.md` · `encryption.md` · `external-fetch.md` · `feature-flags.md` · `guest-tenant-migration.md` · `hangfire.md` · `health-checks.md` · `ical.md` · `lease-contract-templates.md` · `mobile-release.md` · `onboarding-consents.md` · `proxy-ip.md` · `rli.md` · `seasonal-suggestions.md` · `seo-domain.md` · `service-categories.md` · `storage.md` · `stripe.md` · `suppliers.md` · `tenant-child-orgid-migration.md` · `tourist-tax-rates.md`

## 6. Documentazione normativa (`backend/.claude/context/regulations/`)

Dati normativi verificati da fonti ufficiali (mai inventati), con le relative incertezze segnalate nel testo di ciascun file: `cin.md`, `alloggiati.md`, `canone_concordato.md`, `cir-lombardia.md`, `fiscale.md`, `gdpr.md`, `imposta_soggiorno.md`, `istat-flussi-turistici.md`, `ota_normativa.md`, `regionale.md`, `sicurezza.md` (indice: `_index.md`).

## 7. Lavori noti ancora da fare (non bloccanti, già in coda nel piano)

- **FN-03** (task dedicato, non ancora eseguito): test e2e/unit diventati flaky sotto il carico di più agenti in parallelo (checkout-page, S3FileStorage, LogRedaction, DirectCheckout, property-form) e alcuni e2e che simulano rotte ormai rimosse (`compliance-journey.spec.ts`) — vanno rivisti tutti insieme a fine lavori.
- **SU-04** (bloccato da RS-6/dati ISTAT): correggere il registro comuni.
- **CO-22**: implementare l'invio Ross1000 (Regione Lombardia), oggi solo documentato (RS-9).
- **EXTRA non ancora assegnati**: mostrare il BookingCode al posto del Guid in console host; catalogo `CancellationPolicy` con seed/admin; email "Pagamento ricevuto" per addebiti differiti.
- **FD-05**: alcuni messaggi di errore inglesi non ancora tradotti (#425, minori).

---

## 8. Emerse nelle sessioni di ripresa

### Sessione Cursor (2026-09-25) — lavoro non pubblicato su GitHub

Queste domande sono state annotate in Cursor mentre si lavorava a BK-18, PL-06, MO-12 e PC-14, ma di quel lavoro non risulta nessun branch né commit su GitHub: nella sessione del 2026-10-01 i quattro task vengono rifatti da capo e le domande riverificate sul codice.

- [ ] BK-18 — Se Stripe fallisce dopo il salvataggio di booking+guest, cancellare entrambi i record (niente PII) oppure lasciare il booking `Cancelled` per audit? — default: cancellare booking+guest (minimizzazione GDPR, A3-33) — 2026-09-25
- [ ] BK-18 — Pulire anche i `Guest` orfani già presenti in produzione, o solo impedire i nuovi? — default: solo prevenzione (niente backfill distruttivo) — 2026-09-25
- [ ] PL-06 — Il 400 `staleDocuments` del backend non ha un `code` ProblemDetails stabile (`stale_documents`). Il FE lo riconosce dal campo `staleDocuments`. Va aggiunto il code lato BE? — default: riconoscimento dal campo, niente code inventato — 2026-09-25
- [ ] PL-06 — Dopo la modifica del tipo operatore si apre la pagina piano. Un utente senza ruolo billing admin vede il blocco PL-12. Meglio tornare al profilo? — default: pagina piano come da A1-15 — 2026-09-25
- [ ] MO-12 — `GET /api/properties/cin-compliance` è filtrato per owner (`GetOwnerCinComplianceAsync`), non per `HostScope`: un PropertyManager vede le proprietà in lista ma non il riepilogo CIN dell'org. Va allineato allo scope dell'org? — default: lasciato owner-only (fuori da A6-20) — 2026-09-25
- [ ] PC-14 — `GET /api/bookings` resta un array non paginato per non rompere il contratto FE; A2-17 chiedeva la paginazione. Introdurla in un task dedicato (nuova forma della risposta o header Link)? — default: stesso array, filtro `HostScope` in una sola query — 2026-09-25

### Sessione 2026-10-01

- [ ] AMBIENTE — Il proxy di rete di questa sessione blocca ancora `istat.it`, `*.gov.it` e `dati.gov.it`. RS-6 resta bloccato. SU-04 viene implementato con import dei dati ufficiali da file CSV fornito dall'admin, come per le tabelle Alloggiati (CO-12), senza dati scritti a mano. — 2026-10-01

- [ ] SU-16 — La pagina pubblica `/help/ical` (usata dalle impostazioni iCal dell'host) ha "Indietro" fisso verso `/app/supplier/calendar`, anche per gli host, e dal wizard di attivazione si perde lo step (#327). Correggerla con `navigate(-1)` e un fallback per contesto? — default: lasciata com'è (fuori da A4-32) — 2026-10-01
- [ ] PL-06 — (conferma del dubbio Cursor) Il 400 `staleDocuments` (`UsersController.ToConsentError`) ha solo `error` (testo italiano inline) e `staleDocuments`, senza `code` ProblemDetails né messaggio localizzato. Serve un piccolo task BE per un code stabile `stale_documents` + resx? — default: il FE lo riconosce dal campo — 2026-10-01
- [ ] PL-06 — Il backend valida `planTier` ma lo ignora anche al primo onboarding: il selettore del piano nell'onboarding è solo decorativo (A1-03/FD-18). Toglierlo o renderlo effettivo (checkout)? — default: invariato — 2026-10-01
- [ ] BK-19 — Verificare nel dashboard Stripe (test e live) che gli endpoint webhook platform e connect siano sulla API version `2025-12-15.clover`, quella dell'SDK. — default: tolleranza attiva, con log della versione ricevuta. La firma resta sempre verificata. — 2026-10-01
- [ ] PL-07 — Il nuovo converter JSON degli enum accetta ancora il numero di un valore dichiarato (es. `1` = LongTerm), per non rompere i client attuali. I valori non dichiarati danno 400. Accettare solo i nomi? — default: numeri dichiarati accettati (basta `allowIntegerValues: false` per cambiare) — 2026-10-01
- [ ] PL-07 — L'upload documenti in `PropertiesController` risponde ancora `BadRequest({error})` invece di ProblemDetails (fuori perimetro). Da allineare in FN-01? — default: invariato — 2026-10-01
- [ ] MO-09 — Il backend accetta il check-out anticipato, prima della data di partenza, senza nessun controllo. L'app ora mostra un avviso e chiede una conferma esplicita. Serve anche un controllo lato server (rifiuto, oppure conferma obbligatoria)? — default: nessuna modifica al backend — 2026-10-01
- [ ] MO-10 — Il finding chiedeva la scelta del fornitore con punteggio (`match-supplier`), ma quell'endpoint è dietro il flag AI spento (D11). L'app fa la stessa scelta manuale del web. Inoltre l'API fornitori non espone prezzi, quindi in app non se ne mostrano. Va bene così? — default: scelta manuale, senza prezzi — 2026-10-01

- [ ] PL-04 — Quando l'host cambia lo slug, il vecchio resta come alias (`OrgSlugAlias`): i link già condivisi reindirizzano a quello nuovo e nessun'altra org può prendere il vecchio. Gli alias devono scadere? — default: tenuti per sempre — 2026-10-01
- [ ] PL-04 — Le org nuove ricevono uno slug neutro (`org-xxxxxxxx`) e l'host sceglie quello leggibile nelle impostazioni. Rinominare in automatico gli slug esistenti `org-<sub>`? — default: no — 2026-10-01
- [ ] PL-04 — L'email pubblica opt-in vale solo per il sito pubblico anonimo. Gli ospiti con una prenotazione continuano a vedere l'email dell'host nelle email e nella ricerca prenotazione. Va bene? — default: sì — 2026-10-01
- [ ] PL-04 — Ancora aperto (A1-22): lo step "Nome attività" nell'onboarding non esiste ancora, e il customer Stripe già creato non riceve la nuova email di contatto dell'org. Servono? — default: non fatti — 2026-10-01
- [x] BK-18 — (dubbi Cursor riverificati) Booking e ospite ora si salvano insieme, dopo la validazione e sotto l'advisory lock. Se il pagamento non parte si cancellano nella stessa transazione: nessun booking `Cancelled` lasciato per audit, che resta nei log con i soli ID. Gli orfani già in produzione non vengono toccati: le query di verifica in sola lettura sono nel runbook `direct-booking.md` §10 e la decisione su cosa cancellare resta al PO. — 2026-10-01
- [ ] BK-18 — Se Stripe crea il Customer ma poi fallisce il SetupIntent, nome ed email restano sull'account Connect dell'host. Va cancellato il Customer? — default: non gestito — 2026-10-01
- [ ] BK-18 — I checkout abbandonati (hold scaduto) tengono l'ospite fino alla retention GDPR. Cancellarlo subito alla scadenza dell'hold? — default: invariato — 2026-10-01
- [ ] PL-09 — Il filtro per ruolo della lista utenti admin guarda solo il ruolo primario: un utente che ha Supplier come ruolo secondario non compare filtrando per Supplier. Correggerlo? — default: invariato — 2026-10-01
- [ ] PL-09 — Il vecchio `PUT /api/users/{id}/role` resta solo per compatibilità e il FE non lo usa più. Ha comunque la protezione "ultimo admin". Rimuoverlo? — default: tenuto — 2026-10-01
- [ ] PL-09 — Salvare un utente senza nessun ruolo è consentito, con un avviso, e lo porta a `None`. Bloccarlo? — default: consentito — 2026-10-01
- [ ] LT-12 — Per quanto si conservano i dati delle parti dei contratti LTR finiti (`Gdpr__Retention__LeaseParties__Years` + `__Source`)? Non c'è un periodo verificato. — default: retention spenta, quindi non anonimizza nulla; le richieste di cancellazione vengono comunque onorate — 2026-10-01
- [ ] LT-12 — Cancellazione richiesta a contratto finito: oggi l'anonimizzazione è immediata. Va differita per obblighi successivi alla fine del contratto (cessazione RLI, deposito cauzionale)? Da confermare con il DPO. — default: immediata — 2026-10-01
- [ ] LT-12 — I documenti nel bucket privato (contratto firmato, ricevute RLI e Questura) contengono ancora i dati delle parti e non vengono cancellati. Cancellarli con l'anonimizzazione? — default: conservati — 2026-10-01
- [ ] LT-14 — Una parte "società" non ha un tipo proprio né la ragione sociale: si inserisce con nome e cognome più CF/P.IVA a 11 cifre. Serve un tipo "persona giuridica"? — default: nessun tipo — 2026-10-01
- [ ] LT-14 — Massimo 10 parti per ruolo (soglia tecnica di sicurezza). Va bene? — default: 10 — 2026-10-01
- [x] MO-12 — (dubbio Cursor) `GET /api/properties/cin-compliance` ora usa lo stesso HostScope della lista: un PropertyManager vede il riepilogo CIN delle proprietà dell'org; per l'owner non cambia nulla. — 2026-10-01
- [ ] PC-14 — (dubbio Cursor, confermato) `GET /api/bookings` non è paginato: lista web, dashboard e dettaglio ospite filtrano e aggregano lato client l'array completo, quindi paginare significa spostare filtri e KPI sul server (vedi A2-29). Ora la lista arriva con una sola query SQL filtrata per HostScope, senza N+1. Pianificare la paginazione server-side? — default: array non paginato — 2026-10-01

- [ ] PL-14 — Mancano ancora i testi ToS, Privacy e DPA (D14, li fornisce il PO): le pagine `/legale/*` mostrano "in preparazione". Per caricare un testo e pubblicarlo come nuova versione segui `docs/runbooks/legal-documents.md` §2. — default: versioni 2026-06-v1 invariate — 2026-10-01
- [ ] PL-14 — Per Supabase, Auth0, Stripe, Resend, Expo, Railway, Vercel e DeepSeek non sono deducibili dal codice né la ragione sociale, né il luogo del trattamento, né la base del trasferimento extra-SEE. Vanno compilati in config. L'unica dedotta è la regione di Auth0, ed è **US**: serve un tenant Auth0 EU per la produzione? — default: campi "in definizione" — 2026-10-01
- [ ] PL-14 — L'elenco dei sub-responsabili passa alla versione 2026-10-v1, con la data di entrata in vigore ancora vuota. Come notificare agli host le modifiche (GDPR art. 28(2))? Va confermato anche Vercel, che oggi risulta attivo di default. — default: data vuota, nessuna notifica — 2026-10-01
- [ ] PL-14 — `.claude/rules/integrations.md` e `infra.md` citano ancora SendGrid, ma il codice usa Resend (`IEmailService` con template `.resx`). Posso correggere questi file di istruzioni? — default: non modificati, serve il tuo OK — 2026-10-01
- [ ] CO-13 — **Bloccato.** Le specifiche ufficiali di Alloggiati Web (tracciato record e web service) non sono nel repo. RS-1 ha ricavato posizioni dei campi e WSDL solo da estratti di motori di ricerca, e `alloggiatiweb.poliziadistato.it` / `questure.poliziadistato.it` sono bloccati dalla rete di questo ambiente. Per questo non ho scritto né il file né il client SOAP. Per sbloccarlo servono `CREAFILE.pdf`, `MANUALEALBERGHI.pdf`, il manuale del web service e il WSDL `Service.asmx`, in una cartella leggibile (es. `docs/vendor/alloggiati/`), oppure l'apertura del proxy verso quei domini. Da confermare anche il limite di 1000 righe per file, citato da una sintesi di ricerca ma non verificato. — default: l'invio resta manuale con stato onesto (D6) — 2026-10-01
- [ ] RS-6 — Il PO ha chiesto di cercare il CSV dei comuni sui siti istituzionali, ma nella sessione del 2026-10-01 sono tutti bloccati dalla rete (istat.it, esploradati/dati.istat.it, anagrafenazionale.interno.it, interno.gov.it, agenziaentrate.gov.it, indicepa.gov.it, dati.gov.it), sia con `curl` sia con WebFetch. Il blocco non è stato aggirato. SU-04 viene implementato con import da CSV caricato dall'admin (stesso pattern di CO-12), senza dati scritti a mano. Esistono mirror non ufficiali su GitHub e portali di terzi (es. opendatasicilia/comuni-italiani, databasecomuniitaliani.it): se li vuoi usare per un primo popolamento, serve un tuo OK esplicito e una verifica incrociata con le fonti ufficiali. — default: dataset vuoto, funzionalità dipendenti con stato "dataset non importato" — 2026-10-01
- [x] PL-14 — SendGrid → Resend in `.claude/rules/integrations.md` e `.claude/rules/infra.md`: fatto con il tuo OK (2026-10-01). Restano riferimenti a SendGrid solo nei file `.claude/skills/council-*` e `.claude/agents/platform-launch/` (analisi dei costi e checklist di conformità): non toccati. — 2026-10-01
- [ ] CONFIG — Da questa sessione non ho accesso ai pannelli Railway, Vercel, Auth0, Supabase, Stripe, Resend o EAS, né a credenziali: non posso impostare le variabili. Preparo nel repo la checklist verificata contro il codice (`docs/runbooks/deploy-checklist.md`) e i file `*.example`; l'impostazione effettiva resta a te, oppure dammi un token con permessi minimi e dimmi quale servizio. — 2026-10-01

### Integrazione con ChatGPT e Claude (task AI-PLAN, Wave 8)

Richiesta del PO del 2026-10-01: prevedere l'integrazione a fine sviluppi. Piano e ricerca con fonti in `docs/integrations/ai-assistants-plan.md`; task `AI-01` … `AI-12` nel registro. Nessuna decisione è stata presa: i default sotto sono prudenti e valgono finché non rispondi.

- [ ] AI-10 — Titolarità degli account: quale società possiede l'organizzazione Anthropic (piano Team/Enterprise: sottomette un Owner e l'inserzione appartiene all'org) e l'organizzazione OpenAI Platform (verifica d'identità aziendale e permesso `api.apps.write`)? Chi sono Owner e firmatari delle conferme di conformità? — default: account aziendale della società che gestisce CasaZen, mai personale; nessuna sottomissione finché non indicato — 2026-10-01
- [ ] AI-02 — Quali ruoli possono usare l'assistente (PropertyOwner, PropertyManager, Staff, landlord LTR)? I contratti LTR in sola lettura entrano in v1? — default: stessi ruoli e policy dell'app, Staff solo lettura, LTR escluso (dati delle parti dei contratti, retention LT-12 non decisa) — 2026-10-01
- [ ] AI-04 — Quali azioni di scrittura sono ammesse in GA? — default: nessuna in GA v1; in beta solo approvare/rifiutare una richiesta on-site e aprire una richiesta fornitore, sempre con conferma fuori dal modello; mai pagamenti, rimborsi, prezzi, invio di messaggi, Alloggiati, cancellazioni — 2026-10-01
- [ ] AI-02 — Canali ospite e fornitore: sì o no, e quando? — default: solo host in v1 (gli ospiti non hanno account Auth0 e i loro dati sono di terzi; il fornitore richiederebbe un secondo connettore) — 2026-10-01
- [ ] AI-11 — Piani e prezzo dell'integrazione: inclusa nei piani a pagamento, add-on o per tutti? OpenAI vieta di mostrare piani o promuovere upgrade dentro il plugin. — default: inclusa nei piani a pagamento esistenti; nessun riferimento ai piani nel plugin (errore neutro "non attivo per la tua organizzazione") — 2026-10-01
- [ ] AI-10 — Nome, slug (su Claude è permanente dopo la pubblicazione), icona e logo, dominio del server MCP (D3: configurabile) e testi IT/EN delle inserzioni? — default: nome "CasaZen", testi IT/EN, nessun dominio scritto nel codice; si decide prima della sottomissione — 2026-10-01
- [ ] AI-08 — Regione dei dati e trasferimenti: tenant Auth0 EU o US per i token dell'assistente (`docs/runbooks/auth0.md` prescrive EU, PL-14 ha dedotto US dal codice)? Quale base per il trasferimento verso OpenAI e Anthropic? — default: nessun dato personale dell'ospite oltre il minimo; parere del DPO prima della beta con dati reali — 2026-10-01
- [ ] AI-08 — Come qualificare OpenAI e Anthropic ai fini GDPR (destinatari scelti dall'host, responsabili, titolari autonomi) e dove inserirli (elenco sub-responsabili di PL-14, DPA, informativa)? Serve una DPIA? I testi li fornisce il PO (D14). — default: elencati come "destinatari scelti dall'host" in attesa del parere; integrazione spenta di default per ogni org — 2026-10-01
- [ ] AI-12 — Account di test per i revisori: organizzazione demo nel tenant di produzione o tenant separato? Chi la gestisce? OpenAI richiede credenziali senza MFA, SMS, codici email o VPN. — default: org demo isolata nel tenant di produzione con dati fittizi, utente dedicato senza MFA, credenziali ruotate a ogni sottomissione — 2026-10-01
- [ ] AI-10 — I plugin ChatGPT sono disponibili per gli host italiani (SEE)? Una pagina di aiuto sui connettori segnala funzioni non disponibili in SEE, Svizzera e Regno Unito (fonte secondaria, non verificata). Si parte comunque da Claude? — default: prima Claude, ChatGPT dopo la verifica di AI-01 — 2026-10-01
- [ ] AI-06 — Il plugin bundle richiede un repository GitHub pubblico prima del listing e una `LICENSE`. Quale organizzazione GitHub e quale licenza? — default: repository dedicato con solo manifest, Skills e README, licenza permissiva scelta dal PO; il codice del server resta privato — 2026-10-01
- [ ] AI-02 — Retention dell'audit delle chiamate dell'assistente (solo ID e hash, nessuna PII)? — default: 90 giorni, configurabile; da confermare con il DPO (nessun periodo verificato) — 2026-10-01
- [ ] AI-03 — È accettabile attivare la registrazione dinamica dei client (DCR) sul tenant Auth0 degli utenti di produzione, se CIMD e credenziali predefinite non bastano? — default: no, ci si ferma e si chiede — 2026-10-01
- [ ] AI-02 — Il riepilogo fiscale/cedolare in sola lettura espone solo aggregati già calcolati da CasaZen con l'etichetta "informativo, non consulenza fiscale"? — default: sì, nessuna raccomandazione e nessuna aliquota scritta nei tool (`TaxRate` resta l'unica fonte) — 2026-10-01
- [ ] AI-02 — Le note e le richieste libere dell'ospite possono comparire nei risultati dei tool? — default: escluse in v1 (ingresso principale della prompt injection e minimizzazione dei dati) — 2026-10-01
- [ ] AI-01 — **Ambiente.** In questa sessione sono bloccati dalla rete `developers.openai.com`, `help.openai.com`, `learn.chatgpt.com`, `platform.openai.com`, `modelcontextprotocol.io` e `auth0.com` (non aggirato). I fatti OpenAI e Auth0 del piano sono perciò estratti di ricerca ([E]) e non letti dalle pagine. Puoi sbloccare questi domini per AI-01, oppure fornire le pagine come documenti? — default: AI-01 parte con le fonti [E] e le riverifica quando possibile; nessuna sottomissione prima — 2026-10-01
- [ ] AI-10 — Lasciare ad Anthropic la scelta tra etichetta Community e Verified (nessuna candidatura separata) e non chiedere trattamenti particolari? — default: sì, Community di default — 2026-10-01

### Sessione 2026-10-02 — completamento del piano, CI e hosting

Voci accumulate durante la chiusura dei task, la CI delle tre PR e l'analisi dell'hosting.

- [x] RS-6 — Dataset ufficiale dei comuni: **fornito dal PO il 2026-10-01** (ISTAT, "Elenco comuni italiani" aggiornato al 21/02/2026, 7894 comuni, sha256 dell'originale `83842076…7a26d5`). Controlli: codici ISTAT e catastali tutti univoci e ben formati, codice comune = provincia + progressivo su ogni riga. Conferma gli errori del vecchio registro (Milano è F205, Torino L219/001272, Genova D969/010025, Varenna 097084/LC); vengono corretti da SU-04 sostituendo i dati hardcoded con la tabella `Comune`. — 2026-10-01
- [x] PL-14 — Testi ToS, Privacy e DPA: il PO (2026-10-01) ha delegato la redazione delle bozze a un agente, superando D14. Task `LEGAL-TEXTS` avviato. Le bozze richiedono comunque la revisione di un legale prima dell'uso in produzione. I dati societari (ragione sociale, sede, P.IVA, PEC, email privacy, foro competente) restano da configurare in `Legal:*`: finché mancano, le pagine restano "in preparazione". — 2026-10-01
- [ ] PL-05 — Fornitori la cui org conteneva già dati host: la migrazione converte quell'org in Host sul posto, senza spostare righe tenant né billing. Solo il lato fornitore (profilo con slug vetrina, disponibilità, richieste, link) passa a una nuova org fornitore. Confermare prima del deploy; query pre-deploy e controlli post-deploy sono in `docs/runbooks/suppliers.md` §13. — default: come descritto — 2026-10-01
- [ ] PL-05 — `User.OrgId` vale ora solo per l'org host: un account solo-fornitore ha `orgId` null in `/users/me` e registra il device sull'org fornitore. I client (web e app) lo gestiscono già? — default: verificato lato backend, da provare su un account reale in test — 2026-10-01
- [ ] PL-05 — Le push Hangfire già in coda verso un'org poi divisa vanno perse (al massimo una per richiesta aperta). Accettabile? — default: sì — 2026-10-01
- [ ] BK-12 — Limiti scelti per il branding del sito senza decisione del PO: tagline massimo 160 caratteri; logo PNG/JPEG/WebP fino a 2 MB, da 64×32 a 4000×4000 px; immagine hero fino a 10 MB, da 1200×400 a 8000×8000 px; SVG rifiutato (rischio XSS). Va bene? — default: questi valori — 2026-10-01
- [ ] BK-12 — Si gestisce un solo colore (primario; nessun valore = colore del tema). Servono secondario e accento? Il contrasto AA non blocca il salvataggio (lo gestisce BK-13). — default: un solo colore — 2026-10-01
- [ ] PC-03 — Le property legacy con `IsActive=false` (nascoste dal vecchio form) restano nascoste e occupano uno slot del piano: non si sa se erano pause o eliminazioni. Riattivarle in blocco? — default: nessuna conversione automatica — 2026-10-01
- [ ] PC-03 — Pausa e attiva sono solo per gli affitti brevi (permesso `PropertyWrite`): un locatore solo LTR riceve 403. Il frontend dovrebbe nascondergli il pulsante. Va corretto in un task frontend dedicato? — default: nessuna modifica al frontend — 2026-10-01
- [ ] PC-03 — Lo step di onboarding "sito pubblicato" conta anche le property in pausa. Va bene o devono contare solo quelle attive? (si collega a PL-15) — default: contano anche quelle in pausa — 2026-10-01
- [ ] PC-05 — "Rifiuto se dati fiscali" l'ho interpretato così: i dati fiscali si conservano (soft delete; report fiscali ed export GDPR li leggono ancora) e la cancellazione non viene rifiutata. Confermi? — default: così — 2026-10-01
- [ ] PC-05 — Bloccano la cancellazione di una property anche le richieste in attesa e i contratti LTR in corso (409 `property_has_active_leases`). Il frontend mostra già un messaggio adatto? — default: scelta prudente lato backend — 2026-10-01
- [ ] PC-05 — Le prenotazioni passate di una property eliminata spariscono dalle liste dell'host e dai rimborsi (es. la cauzione dopo il check-out). Mostrarle comunque, in sola lettura? — default: nascoste — 2026-10-01
- [ ] CO-13 — **Parziale.** Il file tracciato Alloggiati è generato (backend + frontend, pubblicato); il client del web service non è implementato. Fonte del tracciato: ho letto per intero `MANUALEALBERGHI.pdf` e `MANUALEWS.pdf` (Rev. 01 del 24/01/2022, non quella del 13/01 citata da RS-1) e il WSDL da **copie su GitHub**, perché il portale della Polizia resta bloccato. Prima dell'uso in produzione serve confrontare l'SHA-256 con un download dal portale ufficiale (valori in `docs/runbooks/alloggiati.md`). I PDF non sono nel repo, perché la licenza è ignota. — default: file generato, invio resta manuale — 2026-10-01
- [ ] CO-13 — Implementare anche il client del web service? Servono decisioni su: uno stato "inviato, ricevuta in attesa" (oggi manca), invio automatico per conto dell'host, comportamento sui duplicati se `Send` viene ripetuto (l'invio è irreversibile e non esiste una sandbox). — default: non implementato — 2026-10-01
- [ ] CO-13 — I nomi nel file ammettono solo A-Z, spazio e apostrofo (informazione di una fonte di terzi) e i giorni vanno con lo zero iniziale: da verificare con la funzione "Elabora" su un account reale. Se un nome non è scrivibile il file viene rifiutato; di default i caratteri speciali sono traslitterati. — default: traslitterazione — 2026-10-01
- [ ] CO-13 — Se il file contiene meno ospiti di quelli dichiarati nella prenotazione, oggi c'è solo un avviso. Bloccare la generazione? — default: non blocca — 2026-10-01
- [ ] CO-13 — Il "File Unico" (174 caratteri) non è generato: il manuale non specifica il padding dell'identificativo. — default: non generato — 2026-10-01
- [ ] BK-13 — Palette dei 3 temi del sito pubblico (colori, raggio, ombre) e font (Inter, Fraunces, Lora, Space Grotesk, licenza OFL, self-hosted in `src/assets/fonts/public`) scelti dall'agente; tutti i token superano il contrasto AA 4.5:1. Da approvare esteticamente. — default: come scelti — 2026-10-01
- [ ] BK-13 — Colore primario dell'host: il testo del pulsante è scelto in automatico (bianco/scuro/nero, sempre AA) e c'è una variante scurita per link, icone e focus, mostrati in "Aspetto sito". Nessun blocco sul colore scelto. La dark mode del sito pubblico non è coperta: serve? — default: nessun blocco, nessuna dark mode — 2026-10-01
- [ ] BK-14 — La versione dell'informativa dell'operatore letta dall'ospite non è registrata sulla prenotazione (il `consentVersion` resta quello del checkout): serve una migrazione su `Booking`. — default: solo link — 2026-10-01
- [ ] BK-14 — Il checkout non richiede l'accettazione esplicita dei termini dell'operatore e non è bloccato se l'host non ha pubblicato la privacy (la pagina dice "non pubblicata"). Bloccarlo? — default: non bloccare — 2026-10-01
- [ ] BK-14 — I testi dell'operatore sono testo semplice con pochi segni Markdown (HTML rifiutato), senza bozze: pubblicazione immediata, con versioni e ritiro. Va bene? — default: come descritto — 2026-10-01
- [ ] BK-20 — Filtri della ricerca pubblica: tolti "camere max" e "bagni max", aggiunto "ospiti"; l'API supporta prezzo min/max, camere/bagni min, ospiti e città (400 fuori range). Esclusi gli immobili di org disattivate. Nessuna ricerca per date né paginazione (massimo 50). Confermi? — default: come descritto — 2026-10-01
- [x] CONFIG — Checklist di deploy pronta: `docs/runbooks/deploy-checklist.md` (tutte le variabili backend/frontend/mobile con nome esatto, dove impostarle, obbligatorietà ed effetto se mancano), con test di coerenza col codice, health check su URL API e documenti legali, e `secrets/*.example.json` allineati. Nulla è stato impostato sui servizi (nessun accesso da questa sessione): le imposti tu seguendo la checklist. — 2026-10-02
- [ ] PL-14 — Sub-responsabili: ragione sociale, sede, luogo del trattamento e meccanismo di trasferimento sono compilati per Supabase, Auth0/Okta, Stripe, Resend, Expo, Railway, Vercel e DeepSeek, ciascuno con URL fonte e data di consultazione (2026-10-01). Dato che quei domini sono bloccati dalla rete, i dati vengono da estratti delle pagine ufficiali restituiti da una ricerca web limitata a quei domini: **prima di impostare `Legal__Documents__Subprocessors__EffectiveAt` apri ogni URL e confronta i dati**. — default: dati committati ma sovrascrivibili da variabile Railway — 2026-10-02
- [ ] PL-14 — Da scegliere, non dedotto dal codice: regione del progetto Supabase (e dei bucket), tenant Auth0 (EU?), regione del servizio Railway e delle Vercel Functions. Le voci restano "in definizione" finché non sono leggibili dalla configurazione. — default: in definizione — 2026-10-02
- [ ] PL-14 — DeepSeek: nessun meccanismo di trasferimento extra-SEE dichiarato (sede in Cina), lasciato vuoto di proposito. Tenere `Ai__Provider=DeepSeek` attivo in produzione? — default: vuoto — 2026-10-02
- [ ] PL-14 — Se l'elenco dei sub-responsabili cambia rispetto a quello già accettato dagli host, incrementare `Legal:Documents:Subprocessors:Version`. — 2026-10-02
- [ ] VERIFICA — La suite backend completa non è stata eseguita su DEPLOY-CFG (tre tentativi interrotti dal limite di 30 minuti con la macchina a load 15–38): va lanciata una volta sul branch di integrazione. — 2026-10-02
- [ ] BK-17 — Lo "step di onboarding" per l'indirizzo del sito è coperto solo dalla scheda `SiteAddressCard` nella dashboard, non da una voce nella checklist di attivazione (PL-15). Aggiungere la voce? — default: nessuna voce — 2026-10-02
- [ ] BK-17 — Prima di usare il collegamento con Vercel serve impostare il token con permessi minimi e gli id di team/progetto, come descritto in `docs/runbooks/seo-domain.md` (l'app segnala la config mancante). — 2026-10-02
- [ ] SU-09 — Non c'è nessuna notifica (email o push) al rifiuto di una richiesta oltre a quelle già esistenti: il motivo del rifiuto è visibile all'host in timeline, e le notifiche nuove riguardano solo il "Pagato". L'audit cita anche una notifica push al rifiuto: serve? — default: solo timeline — 2026-10-02
- [ ] LEGAL-TEXTS — Bozze di ToS, Informativa privacy (art. 13/14) e DPA (art. 28) pronte in IT e EN, versione `2026-10-v1`, **non attive**: `appsettings.json` lascia le versioni `2026-06-v1`. Per attivarle servono `Legal__Documents__{Tos,Privacy,Dpa}__Version=2026-10-v1` e la data di entrata in vigore, e ogni host dovrà **ri-accettare** (PL-02). Prima fai rivedere i testi a un legale. — default: non attivate — 2026-10-02
- [ ] LEGAL-TEXTS — Dati societari mancanti, non inventati: `Legal__Controller__{Name,Address,VatId,Pec,PrivacyEmail}` e `Legal__Terms__GoverningCourt`. Finché mancano i documenti restano "in preparazione" e l'health check `legal` risulta `degraded`. — default: in preparazione — 2026-10-02
- [ ] LEGAL-TEXTS — Valori proposti da confermare con un legale (tutti configurabili): preavviso di modifica, preavviso di recesso, limite di responsabilità (mesi di canone), giorni per la restituzione dei dati, ore per la notifica di data breach, preavviso per sub-responsabili e audit. — default: conservativi, come da fixture — 2026-10-02
- [ ] LEGAL-TEXTS — Periodi di conservazione dei dati: dove le chiavi `Gdpr__Retention__*` sono spente o da definire, l'informativa lo dichiara senza inventare periodi. Vanno decisi (ospiti, parti dei contratti, documenti, log). — default: dichiarato "da definire" — 2026-10-02
- [ ] PL-13 — Fattura elettronica (SDI): nessun provider commerciale integrato, perché servono contratto e documentazione verificata. Le fatture restano nello stato `manual_required` ("da emettere manualmente") e l'health check `einvoicing` risulta `degraded`. Quale provider scegliere? — default: emissione manuale — 2026-10-02
- [ ] PL-13 — Reverse charge con P.IVA UE non verificata su VIES (pending/unverified): Stripe lo applica comunque. La fattura viene segnalata `reverse_charge_vat_id_not_verified` per revisione: da definire con il commercialista. — default: segnalazione, nessun blocco — 2026-10-02
- [ ] PL-13 — Gli abbonamenti creati prima di PL-13 non hanno `automatic_tax` e sono segnalati `automatic_tax_disabled`: migrarli? (procedura nel runbook `docs/runbooks/billing-tax.md`) — default: non migrati — 2026-10-02
- [ ] PL-13 — Clienti extra-UE (`not_collecting`): servono le registrazioni fiscali nelle rispettive giurisdizioni, da verificare con il commercialista. — default: nessuna imposta riscossa — 2026-10-02
- [ ] LT-15 — La copertura dei test (coverlet) non è stata aggiunta: resta aperta la parte di A7-29 sulle soglie di coverage. Aggiungerla in FN-05? — default: non aggiunta — 2026-10-02
- [ ] LT-15 — Il Playwright installato in questo ambiente cerca Chromium 1223 ma c'è solo la 1194: gli e2e sono girati con una config temporanea non committata. In CI non cambia nulla. — 2026-10-02
- [ ] PC-06 — **Attenzione prima del deploy:** per creare l'indice univoco dell'indirizzo per org, la migrazione assegna ai duplicati successivi al più vecchio un interno visibile `dup-<8 caratteri dell'id>`. Nessuna cancellazione, ma i dati vengono modificati. Prima del deploy elenca i duplicati con le query di `docs/runbooks/property-address.md`. — default: così — 2026-10-02
- [ ] PC-06 — Le coordinate fuori range (|lat| > 90 o |lon| > 180) vengono azzerate (0,0 = non impostato) per restringere la colonna a `numeric(9,6)`: il valore originale non si conserva. Preferisci che il deploy si fermi se ce ne sono? — default: azzerate — 2026-10-02
- [ ] PL-15 — Un sito conta come "pubblicato" solo se almeno una property è attiva, non in pausa e con compliance attiva (`PublicListing.IsPublished`) **e** l'org può incassare (`chargesEnabled`). Confermi? — default: così — 2026-10-02
- [ ] SE-05 — La discovery dei fornitori è spenta (D11): l'avviso di trasparenza `supplierReason` è nel DTO ma non ha ancora una UI dove montarlo. — default: invariato — 2026-10-02
- [ ] PL-13 — **Prima del lancio in produzione con chiave live:** il checkout si apre solo se sono impostati `Billing__VatNumber` (obbligatoria in Production con chiave live) e `Sdi__ManualIssuanceAccepted=true`. Senza, il checkout risponde 409 `billing_gate_closed` e il check `einvoicing` va `degraded`. Impostando `Sdi__ManualIssuanceAccepted=true` accetti di emettere a mano le fatture elettroniche finché non c'è un provider SDI. — default: false (checkout chiuso) — 2026-10-02
- [x] RS-6 — Dataset ufficiale ISTAT importato da SU-04: tabella `Comune` con migrazione, import admin e seed da `Casazen.Infrastructure/Data/Seeds/comuni-istat.csv` (con README e source.json), `ComuneIstatCode`/`RegionCode` su property e fornitore, picker nel frontend. `ItalianComuneRegistry` rimosso: gli errori noti (Milano, Torino, Genova, Varenna) sono corretti. Per aggiornare il file in futuro vedi `docs/runbooks/comuni-istat.md`. — 2026-10-02
- [ ] SU-04 — Per le property senza `ComuneIstatCode` gli "in evidenza" SEO continuano a usare il match sul nome della città, solo se non hanno il codice. Le property esistenti vanno associate a un comune con il picker (nessuna inferenza automatica dal testo libero). — default: match per nome solo come fallback — 2026-10-02
- [x] SE-04 — Ora completo: gli immobili in evidenza e gli aggregati SeoEvent usano `ComuneIstatCode` come chiave; il match per nome città resta solo come fallback per le property senza codice. Limite noto: i comuni omonimi sono documentati in `docs/runbooks/seo-funnel.md`. — 2026-10-02
- [ ] CO-22 — **Bloccato.** Flussi turistici ISTAT e portale regionale lombardo (Ross1000): le specifiche ufficiali non sono verificabili da questa rete (`flussituristici.servizirl.it` e `regione.lombardia.it` bloccati, anche il WSDL). Nessun codice scritto. Il runbook `docs/runbooks/ross1000.md` dice cosa è noto e cosa manca. Per riaprirlo servono i PDF ufficiali (tracciati XML/TXT del portale lombardo, manuale 10/2023, nota WS_Gies, WSDL) oppure lo sblocco dei domini. — default: nessuna implementazione — 2026-10-02
- [ ] CO-22 — Gli adempimenti ISTAT (#6) e portali regionali (#8) sono post-MVP? Mancano comunque prerequisiti nel modello: campo CIR separato sulla property, residenza dell'ospite codificata, ruolo/capogruppo, disponibilità giornaliera. Chi ha il ruolo GDPR e chi custodisce le credenziali Ross1000 degli host? Serve un parere legale. — default: nessuna credenziale custodita — 2026-10-02
- [ ] FN-03 — Golden Journey da UI con attori distinti: verificato davvero in locale 5 volte di fila (circa 55 s) nella variante "paga in struttura" (D5), con host, fornitore, guest e admin, dopo i merge di SU-04 e SU-05. **Gira solo in CI** la variante con carta Stripe di test (webhook Connect, account Connect creato via API Stripe) e il job GitHub Actions: non sono stati provati in questa sessione. — 2026-10-02
- [ ] FN-03 — **Segreti da creare su GitHub** per far girare i job: `BACKEND_REPO_TOKEN` (nel repo frontend), `FRONTEND_REPO_TOKEN` (nel repo backend), `STRIPE_TEST_SECRET_KEY` e `STRIPE_TEST_PUBLISHABLE_KEY` (in entrambi). Token mancante = job in errore; chiavi Stripe mancanti = variante saltata con warning visibile. Dopo alcuni run verdi, aggiungere i due check come required. — default: come descritto — 2026-10-02
- [ ] FN-03 — Il fornitore entra solo su invito, quindi il percorso usa un quarto attore, l'admin di piattaforma. Basta così o serve la registrazione self-serve di pilota nel test? — default: invito — 2026-10-02
- [ ] FN-03 — Per i test c'è un IdP finto con certificato self-signed e la variabile `Email__ApiUrl` (rifiutata fuori da Development/Testing): l'autenticazione di produzione è invariata. L'onboarding Stripe ospitato non è automatizzabile da UI: l'account Connect è collegato via SQL, oppure si usa un account di test Stripe in CI. — 2026-10-02
- [ ] FN-03 — La tassa di soggiorno non è asserita nel percorso (servono `TaxRate` ufficiali). La console host non si aggiorna da sola quando il fornitore completa: il test ricarica la pagina. Serve aggiornamento automatico (polling o push)? — default: invariato — 2026-10-02
- [ ] FN-04 — **Non provato:** i flow Maestro (login reale M0, schermate M1–M8 per testID) e il job su emulatore Android sono consegnati ma **non sono mai girati** (qui non c'è emulatore né Maestro). Verificato davvero in locale: `npm run e2e:check` (ogni `id:` dei flow è un testID dell'app), tsc, eslint, jest 525 test, actionlint, `expo prebuild`/`export`, e il seed più i controlli API sul backend reale. Da confermare alla prima run in CI: build Gradle, boot dell'emulatore, certificato dell'IdP di test, login nella Chrome Custom Tab, share sheet. Non renderlo check obbligatorio finché non è verde. — 2026-10-02
- [ ] FN-04 — **Segreti da creare** su casazen/mobile: `BACKEND_REPO_TOKEN` e `FRONTEND_REPO_TOKEN` (token fine-grained in sola lettura sugli altri due repo). Senza, il job fallisce in modo esplicito. — 2026-10-02
- [ ] FN-04 — La versione di Maestro è fissata a `1.41.0` senza averne potuto verificare l'esistenza: da confermare o cambiare. — default: 1.41.0 — 2026-10-02
- [ ] FN-04 — Il job gira su PR verso `main`, su PR con etichetta `e2e-app`, su push a `main` e ogni notte (40–60 minuti a run). Lo vuoi anche su `develop`? — default: no — 2026-10-02
- [ ] FN-04 — Le notifiche push si provano solo come registrazione del dispositivo (un emulatore non ha FCM): consegna e tap restano da provare a mano su un dispositivo fisico. Solo Android: iOS non è coperto. Il login Auth0 reale resta una verifica manuale (`auth0.md` §7.9). — 2026-10-02
- [ ] SU-05 — La P.IVA non è un requisito di attivazione del fornitore; il telefono è controllato solo nel formato (6–15 cifre), senza verifica SMS. Confermi? — default: così — 2026-10-02
- [ ] SU-05 — Fornitori già attivi con ToS senza versione registrata: ricevono un banner e la richiesta di riaccettazione ma NON sono bloccati. Una versione registrata ma non più corrente blocca take/complete/reject (422 `supplier_tos_reacceptance_required`). Bloccare anche i fornitori senza versione? — default: non bloccati — 2026-10-02
- [ ] SU-05 — Il wizard non ha il campo "tariffa" previsto dall'audit, perché non esiste nel modello. I fornitori già Active senza categorie o comuni (attivati prima di SU-05) restano Active: la query per elencarli è in `docs/runbooks/suppliers.md` §16. Sospenderli o contattarli? — default: restano attivi — 2026-10-02
- [ ] SU-13 — La vetrina pubblica del fornitore è `noindex` (meta, `robots.txt` `/fornitori/`, `X-Robots-Tag`, nessuna sitemap): indicizzarla è una scelta di prodotto, perché pubblica nome e descrizione di ogni fornitore attivo. Il pannello "Disponibilità" mostra le disponibilità dei prossimi 14 giorni: va bene che sia pubblico? — default: noindex, disponibilità visibili — 2026-10-02
- [ ] SU-13 — I fornitori attivati prima di SU-13 ottengono lo slug solo alla prima apertura di "Vetrina" in console (nessun backfill). Serve un backfill se la vetrina deve essere pubblica da subito? — default: nessun backfill — 2026-10-02
- [x] SU-06 — La parte mobile di A4-27 era già risolta (le due schermate usano già il dizionario i18n): chiuso, nessuna modifica. — 2026-10-02
- [ ] FN-02 — `knip` è ora un passo **bloccante** della CI del frontend (`npm run knip`, config `knip.json`): rimossi 26 file di componenti e hook non raggiunti, due file CSS, 2 fixture e2e orfane e 6 dipendenze npm. Limite: un file usato solo dal proprio test non viene segnalato. Il typecheck `tsc -p tsconfig.e2e.json` ha 14 errori già presenti prima, non legati al task. — 2026-10-02
- [x] PL-06 — (dubbio Cursor) Il 400 `staleDocuments` ora ha un code stabile `stale_documents`, mantenendo il campo `staleDocuments` per i client; nuovi code anche `consents_incomplete` e `org_domain_*`. — 2026-10-02
- [ ] FN-01 — Molte risposte di errore sono passate da `{error}` a ProblemDetails (`detail` + `code`) in OrgDomain, Billing, Devices, PublicHost, Orgs/Admin/Users planTier, cinStatus, Leases APE/IMU, upload documenti e città bloccata. Web e mobile leggono già `detail` per primo e i code già esistenti sono invariati. Le `InvalidOperationException` generiche nei controller Leases e Properties non sono più mappate a 400 (ora 500 generico): da provare su test. — 2026-10-02
- [ ] FN-01 — I messaggi dei webhook OTA ("Invalid signature", ecc.) restano in inglese perché rispondono a macchine. Circa 370 attributi DataAnnotations (in gran parte entità Core non esposte) usano ancora i messaggi di default del framework: serve una scelta globale (chiavi per campo)? — default: non toccati — 2026-10-02
- [ ] HOSTING — Analisi dell'hosting gratuito del backend pubblicata in `docs/runbooks/free-hosting-analysis.md`. Raccomandazione: **Oracle Always Free Arm A1** (2 OCPU, 12 GB dopo il taglio del 15/06/2026) con Docker Compose + Caddy, deploy via GitHub Actions → registry → SSH, connessione a Supabase tramite session pooler (porta 5432, mai la 6543: rompe gli advisory lock). Alternative: Cloud Run con scheduler (cambia l'architettura, nessun tetto di spesa), Render free come ponte (dorme). Molti siti ufficiali erano bloccati dalla rete: i dati relativi sono marcati "non verificato sulla fonte ufficiale". — 2026-10-02
- [x] HOSTING — **Problema trovato nel backend:** a riposo apre 26 connessioni PostgreSQL, perché non sono configurati né `WorkerCount` di Hangfire (default 20) né `Maximum Pool Size`. Il session pooler gratuito di Supabase ne concede circa 15 (da riconfermare): senza limiti il backend andrebbe in errore di connessioni. Proposta: 4 worker Hangfire e pool Npgsql a 10, configurabili. — 2026-10-02 **Risolto** nel commit `6290355f`: `Hangfire__WorkerCount` (default 4), `Database__MaxPoolSize|MinPoolSize|HangfireMaxPoolSize|ConnectionBudget`, health check `db-connections` e rifiuto della porta 6543 fuori da Development/Testing.
- [ ] HOSTING — Supabase free non ha backup e va in pausa dopo 7 giorni: serve un `pg_dump` notturno cifrato fuori da Supabase. Regione Supabase da confermare dal dashboard: `docs/INFRA.md` indica `eu-central-1` in un punto e `eu-west-1` in un altro. — 2026-10-02
- [ ] HOSTING — **Vercel Hobby vieta l'uso commerciale**: per un SaaS serve un'alternativa (es. Cloudflare Pages, 5–8 giorni di lavoro) o un piano a pagamento. Le funzioni di dominio personalizzato (BK-17) richiedono lavoro su Cloudflare for SaaS. Auth0 free: le fonti discordano su RBAC e M2M, che il backend usa: da verificare nel dashboard. Resend free: 100 email al giorno. — 2026-10-02
- [ ] HOSTING — Il PO aveva scelto Oracle Cloud Always Free (2026-10-02), poi ha **fermato** il task `HOSTING-ORACLE` e chiesto di tenere solo i limiti di connessione a Supabase. Docker Compose, Caddy, workflow di deploy e backup notturno **non sono stati creati**. Il backend **non ha oggi nessun host**: da decidere tra Oracle, riattivare Railway come ponte o altro (analisi in `docs/runbooks/free-hosting-analysis.md`). — 2026-10-03
- [ ] FN-05 — Il piano è documentato: 335 difetti dell'audit, 327 chiusi e 8 parziali (A1-22, A2-17, A7-29, A6-04, A6-23, A6-24, A3-23, A6-22), con task ed evidenza in `Sessions/risanamento/RIEPILOGO-FINALE.md` e `PIANO-RISANAMENTO` §9. La coverage dei test (coverlet) di A7-29 non c'è: va fatta in un task separato? Una parte della storia git del primo giorno è consolidata nel merge `develop@58843389`: per quei task l'evidenza è un runbook o un test, non un commit con scope. — default: A7-29 resta parziale — 2026-10-02
- [ ] CI — Su `develop` il job `E2E Tests` del frontend risulta rosso da almeno 4 esecuzioni programmate (#362–#365, stesso commit di base): `E2E L2 (demo)` ha 40 test falliti e `E2E staging smoke + GJ` fallisce al setup dell'autenticazione (timeout in `waitForAppReady` sull'utente long-term). Sono difetti precedenti a questo lavoro. Sulla PR frontend #210 `E2E L2 (demo)` ha 46 rossi: 40 uguali a `develop` e 6 nuovi (footer privacy/termini, attivazione fornitore, widget tassa di soggiorno, consensi dell'onboarding per i piani). Un agente (`E2E-L2-FIX`) cerca la causa di tutti e 46. — 2026-10-02 **Aggiornamento:** i 46 test di `E2E L2 (demo)` e il Golden Journey L3 sono stati corretti (voci chiuse sotto) e la PR frontend è andata in verde. Resta rosso il job programmato su `develop`, ora solo per lo smoke di staging: lo staging su Railway non esiste più. Disattivare o ripuntare quel job? — default: invariato — 2026-10-03
- [x] CI — `E2E L2 (demo)`: tutti i 46 test rossi corretti nel frontend (commit `ba564b9`), 142 su 142 in locale con 0 tentativi ripetuti. Cause: **1 difetto reale dell'app** (`checkin-page.tsx`: il pulsante "Avanti" diventava submit nello stesso click, quindi il modulo partiva e l'informativa privacy dell'ultimo passo non veniva mai mostrata) e **45 mock o test obsoleti** (mock dei consensi che non riconoscevano `?lang=`, glob che catturavano anche i moduli Vite, mock mancanti per comuni ISTAT, iCal, quote, safety-checklist, alloggiati, domini; selettori e rotte cambiati; test di pagamento riscritto). Nessun test disattivato né indebolito. — 2026-10-02
- [ ] CI — Il frontend non ha più l'azione "process payment" (il test è stato riscritto per asserire solo il POST e la riga "In attesa"). Se il backend espone ancora `/payments/{id}/process`, valutarne la rimozione. — default: non toccato — 2026-10-02

---

## Chiuse

- [x] FD-19 — OK alla rimozione dei file di scarto? → **Sì** ("ok rimuovi", 2026-10-01). Rimossi `.idea/`, `StripeWebhookHandler.cs.bak`, `AddContextAuthorization.sql`, le 3 classi vuote in `Payments/`, `Casazen.Web.http` e `.claude/settings.local.json`. Corretto anche `.gitignore`: i commenti sulla stessa riga impedivano di ignorare `settings.local.json` e `opencode.json`. Nota: chi aveva un `.claude/settings.local.json` personale lo vedrà sparire al pull; basta ricrearlo, ora è ignorato. — 2026-10-01
- [x] PR — Come portare i commit del branch `claude/app-analysis-fixes-plan-p0mx0a` su `develop`? → **Risolta**: mergiato con le PR casazen/backend#451, casazen/frontend#209 e casazen/mobile#5. I 9 task interrotti vengono ripresi sul nuovo branch `claude/sleepy-edison-oil8ru`. — 2026-10-01

---

*Questo file è la fonte unica delle domande aperte: se una risposta arriva, la segno come risolta qui invece di lasciarla solo nel log. Se preferisci un altro formato (es. tabella per priorità, o diviso per file separati) dimmelo.*
