# Domande aperte e documentazione — piano di risanamento CasaZen

Aggiornato dopo ~105/166 task chiusi. Ogni voce riporta l'ID del task tra parentesi: cerca `grep -n "<ID>"` in `notes.log` (`Sessions/risanamento/stato/notes.log`) per il dettaglio tecnico completo. Questo file viene rigenerato/ampliato a ogni ondata: nulla viene tolto, solo aggiunto o marcato risolto.

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
- FD-19 **bloccato**: pulizia file di scarto (`.idea/`, `StripeWebhookHandler.cs.bak`, `AddContextAuthorization.sql`, 3 classi vuote in `Payments/`, `Casazen.Web.http`, `.claude/settings.local.json` tracciato per errore) richiede una tua conferma esplicita perché il classificatore di sicurezza blocca `git rm` in automatico. Dimmi "ok rimuovi" per procedere — il lavoro è pronto (`.gitignore` già aggiornato e pubblicato).

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

*Questo file è la fonte unica delle domande aperte: se una risposta arriva, la segno come risolta qui invece di lasciarla solo nel log. Se preferisci un altro formato (es. tabella per priorità, o diviso per file separati) dimmelo.*
