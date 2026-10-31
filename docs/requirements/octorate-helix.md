# Requirements Helix — CasaZen × Octorate: un solo punto di accesso

Versione 2.0 · 31 ottobre 2026 · Stato: **requisiti pronti per Helix** (nuovo mandato del PO
del 31/10). Account test Octorate attivo; credenziali consegnate ma non ancora validate
(avviso «current credentials have expired» in S10).

La v2.0 allarga il perimetro dal solo channel management (v1.3) agli otto processi che il PO
vuole gestire interamente da CasaZen, con Octorate dietro le quinte; aggiunge regole UX
vincolanti e una mappa delle automazioni AI. I contenuti tecnici verificati della v1.3
(contratto OAuth, inventario API, risposte D1–D14, matrice per canale) restano validi e sono
ripresi e aggiornati in §9–§11.

**Indice.** §0 Come usarlo · §1 Mandato · §2 Cosa cambia rispetto alla v1.3 · §3 Fonti ·
§4 Base del codice e stato reale della fase 1 · §5 Regole UX · §6 Requisiti (6.0 debiti della
fase 1, 6.1–6.8 processi) · §7 Automazioni AI ·
§8 Architettura, autorità dei dati e ADR · §9 Contratto OAuth e confini con Octorate ·
§10 Inventario API · §11 Matrice delle capacità · §12 Pacchetti Helix · §13 Gate ·
§14 Decisioni del PO e domande a Octorate · §15 Revisioni · Appendice A Benchmark UX

## 0. Come usare questo documento

- **Identificativi.** `OCT-01…15` sono le fondazioni del connettore della v1.3 (§8.1, invariate
  salvo note). I requisiti di processo sono `LST` (annunci e strutture), `CHK` (check-in e
  adempimenti), `MKT` (marketplace), `MSG` (messaggi), `BKG` (prenotazioni e calendario), `FIS`
  (fatturazione e documenti fiscali), `CRM` (clienti e marketing), `REP` (report e documenti);
  i trasversali sono `UX` (§5) e `AI` (§7); `DEB` sono i debiti della fase 1 da chiudere prima o
  insieme ai processi (§6.0). L'intake di Helix può tenere una feature per requisito.
- **Priorità.** **P0** = demo completa con l'adattatore Mock e fondazioni; **P1** = live dove la
  capacità Octorate è provata o confermata per iscritto; **P2** = dopo il pilota, oppure dipende
  da una decisione o da una terza parte (§14).
- **Stato di una capacità Octorate** (vocabolario della v1.3): `sconosciuto`, `documentato`
  (OpenAPI o email), `provato sandbox`, `confermato per OTA`, `non supportato`, `simulato`.
  Una capacità non provata resta spenta in live e l'interfaccia lo dice.
- **Prodotto unico.** Nessun codice, testo o identificativo di un cliente (Quokka o altri);
  funzioni abilitate da piano o livello dell'organizzazione e da flag spenti per default.
- **Riuso prima di tutto.** §4 elenca ciò che esiste: un requisito che tocca un'area esistente
  la estende, non la duplica. Helix rifà comunque il confronto con il codice corrente all'avvio.
- **Regime di test del processo Helix.** In locale build, format, lint e typecheck; una prova
  minima scritta per ogni regola di denaro, dati, permessi o privacy (indicata nel requisito);
  per ogni wizard le prove UX di UX-14. Le suite le esegue la CI. Nessuna chiamata reale a
  Octorate, alle OTA o agli enti come prova.
- **Segreti.** Nessun valore di credenziale in Git, PR, log, fixture, documentazione o frontend
  (v1.3 §2); il client secret vive solo nel secret store del backend.

## 1. Mandato del PO (31/10/2026)

CasaZen deve diventare **il solo punto di accesso** dell'host e dell'agenzia: l'utente lavora
in CasaZen e Octorate propaga ai canali (Airbnb, Booking.com, Vrbo, Expedia e gli altri attivi),
senza che l'utente debba aprire il pannello Octorate. Otto processi:

1. **Annunci e strutture.** Recuperare, creare e modificare da CasaZen informazioni generali,
   descrizioni e contenuti multilingua, foto e media, servizi e dotazioni, camere, appartamenti,
   tipologie e tariffe, disponibilità, prezzi e restrizioni, regole e condizioni di prenotazione;
   tutto sincronizzato su tutte le OTA collegate. Un solo punto di modifica.
2. **Check-in e adempimenti.** Dati anagrafici e documenti degli ospiti, check-in e check-out,
   invio ad Alloggiati Web **tramite Octorate**, statistiche ISTAT secondo le modalità regionali
   **tramite Octorate**, calcolo automatico della tassa di soggiorno (con Octorate se possibile,
   altrimenti in CasaZen), recupero degli esiti degli invii; se non recuperabili, un collegamento
   o una guida per ottenere la prova dell'adempimento.
3. **Marketplace dei partner Octorate.** Elenco di partner e integrazioni, informazioni e
   requisiti, attivazione e configurazione, stato delle integrazioni attive, modifica e
   disattivazione, il più possibile senza uscire da CasaZen.
4. **Messaggistica centralizzata.** Tutte le conversazioni dei canali attivi, storico, risposte
   e nuovi messaggi dall'hub, associazione a prenotazione e cliente, notifiche configurabili
   (email, push, nessuna, solo contatore; per default l'utente è avvisato). Risposte con l'AI
   secondo standard di comunicazione per organizzazione e per appartamento, con un **ranking di
   confidenza**: invio automatico se la situazione corrisponde alle regole date, altrimenti
   conferma dell'utente o dell'agenzia.
5. **Prenotazioni.** Sincronizzazione bidirezionale: recupero con i dettagli, creazione,
   modifica, cancellazione, prezzi, importi pagati, pagamenti e stati, aggiornamenti
   preferibilmente via webhook, calendario sincronizzato; base anche della reportistica.
6. **Fatturazione e documenti fiscali.** Dati per la fatturazione, creazione e gestione di
   fatture, ricevute delle prenotazioni, documenti fiscali già emessi, pagamenti della
   prenotazione, fatturazione elettronica dove prevista, documenti in formato utilizzabile;
   verificare cosa fa Octorate.
7. **Clienti e marketing.** Database clienti legato alle prenotazioni, base CRM per campagne
   mirate (reminder, offerte, sconti): anagrafiche, recapiti, storico dei soggiorni, dati di
   segmentazione, consensi. Indicazione del PO: i consensi non devono bloccare il lavoro e,
   dove mancano, va trovato un modo per raggiungere comunque il cliente (§6.7 indica i modi
   leciti e perché gli altri non sono percorribili).
8. **Report, contabilità e documenti.** Prenotazioni, fatturato, occupazione, incassi e
   pagamenti, commissioni e costi dei canali, ricevute, fatture e documenti amministrativi,
   riepiloghi della tassa di soggiorno, ricevute e attestazioni degli invii normativi, dati
   esportabili per la documentazione fiscale e contabile. Obiettivo: minimo lavoro manuale e
   quadro completo dell'attività in CasaZen.

**Diktat UX (vale per tutto).** Utente di riferimento inesperto, che usa poco il computer.
Wizard guidati e collegati tra loro; una via d'uscita sempre visibile; possibilità di completare
in un secondo momento; nessun dato già inserito va perso; interfaccia pulita e chiara. «Tante
integrazioni e tante funzioni, ma facili da usare» è la forza di CasaZen. Aggiunta del 31/10:
il wizard porta l'utente **dall'inizio alla fine senza mai uscirne** (oggi il passo Alloggiati
del wizard di check-out porta alla scheda della prenotazione: non deve succedere); le attività
da fare devono essere **più evidenti** (notifiche push e popup che avvisano); i wizard si aprono
**su un livello separato** sopra l'app, sempre dentro CasaZen e non in un'altra finestra del
browser. Il PO chiede di trovare il modo più efficace di guidare l'utente e di proporre dove
introdurre automazioni AI: §5 e §7.

## 2. Cosa cambia rispetto alla v1.3

| Tema | v1.3 (30/10) | v2.0 | Effetto |
|---|---|---|---|
| Perimetro | Solo channel management | Otto processi; CasaZen unico punto di accesso | Nuove aree §6.1–6.8; le fondazioni `OCT` restano (§8.1) |
| Annunci | OCT-04/05/06: mapping, editor dei contenuti, ARI | Gestione completa: import, creazione, modifica, pubblicazione per canale | `LST`; OCT-05 diventa la base dell'editor |
| Check-in e adempimenti | Esclusi (dipendenza futura) | Inclusi: dati degli ospiti verso Octorate, invio Alloggiati e ISTAT da Octorate, esiti | `CHK`; da conciliare con il client Alloggiati di CasaZen (Q1-14): PO-02 |
| Tassa di soggiorno | Interamente CasaZen (S7) | Verificato su S13: Octorate ha importo ed esenzioni per ospite, nessuna API per configurarne le regole | CasaZen calcola e scrive l'esito in Octorate (CHK-07) |
| Marketplace | Assente | Catalogo, attivazione e stato di partner e integrazioni | `MKT`; l'API copre solo portali, strumenti esterni e licenza (§10) |
| Messaggi | OCT-10/11: inbox OTA, AI in bozza | Hub unico, notifiche configurabili, AI con soglia di confidenza | `MSG` |
| Prenotazioni | OCT-07/08/09: import, webhook, riconciliazione | Bidirezionale: creazione, modifica, cancellazione, pagamenti, calendario | `BKG` |
| Fatturazione | Esclusa | Documenti Octorate letti con lo stato SDI; CasaZen emette dove Octorate non lo fa | `FIS`; la creazione via API è da chiarire (D19) |
| Clienti e marketing | Escluso | Database clienti alimentato da Octorate; contatto lecito senza blocchi inutili | `CRM` |
| Report | Escluso (nessuna API Octorate) | Report e pacchetto per il commercialista calcolati da CasaZen | `REP` |
| UX | Non trattata | Regole vincolanti: wizard su livello, mai fuori dal processo, «Da fare», avvisi | `UX` |
| AI | Solo messaggi | Mappa per processo, automatica o da approvare | `AI` |
| Fase 1 Quokka | Data per costruita (Q1-01…29, «non anticipare i contratti») | Verificata nel codice al 30/10: molte funzioni sono solo struttura (CRM, campagne, condizioni, routine, coda di approvazione, Alloggiati automatico, documento e selfie, catena finanziaria dell'agenzia) e c'è un problema di permessi | §4.5 e prerequisiti `DEB` (§6.0); DEB-01 subito |

## 3. Fonti

S1–S11 sono le fonti della v1.3, aggiornate dove indicato; S12–S17 sono nuove.

| ID | Fonte | Uso |
|---|---|---|
| S1 | [Planning attivo](../../Sessions/PLANNING.md), [intervista PO](../../Sessions/INTERVISTA-PLANNING-2026-10-20.md) | Priorità Quokka, nessuna scadenza fissata, proprietà/territori del pilota da concordare |
| S2 | Quokka v10, in `develop` dal 30/10 ([PR #553](https://github.com/casazen/backend/pull/553)), `Sessions/prospect-quokka360/analisi-funzionalita.md`, §1 R1/R5/R8/R12, §2 D4/D9/D15, §3.1 e Allegato A | Chat e contenuti obbligatori; recensioni mantenute; scelta fornitore ancora in verifica |
| S3 | `CasaZen_Octorate_Analisi_Integrazione.docx`, v1.0, 21/10/2026, §3–13 | Proposta tecnica/commerciale caricata dal PO |
| S4 | `CasaZen_Octorate_Guida_Tecnica_Implementazione_Pilota.docx`, v1.0, 21/10/2026, §3–12 | Architettura, demo, test e gate proposti |
| S5 | [OpenAPI ufficiale](https://api.octorate.com/connect/rest/v1/integration/openapi.yaml), [documentazione Postman](https://documenter.getpostman.com/view/12991460/2s93RKzb52) | Letti il 23/10/2026: OpenAPI 3.0.1 `1.0.DEVEL`, 164 percorsi e 208 operazioni, licenza «Proprietary»; collection pubblica Postman. Da riacquisire con data e hash prima di generare DTO/payload; le copie non sono nel repository |
| S6 | Risposta scritta di Octorate al PO, 23/10/2026 (testo riportato dal PO, non allegato) | Dichiarazioni su canali, recensioni, webhook, messaggi, camere e rete di test: valgono come `documentato` con fonte S6, non come prova sandbox (§6) |
| S7 | Resoconto conclusivo dello scambio CasaZen × Octorate, consegnato dal PO il 30/10/2026 (testo riportato, email originali non allegate) | Conferme finali su copertura API, IP statici, canali, compliance, messaggistica, recensioni, documenti fiscali, reporting, offerta commerciale e disponibilità a consegnare le credenziali |
| S8 | `CasaZen_Octorate_Integration_Checklist.md`, consegnata dal PO il 30/10/2026 (file esterno, non copiato nel repository) | Checklist di provisioning, sicurezza, mapping, sync, prenotazioni, webhook, guest, messaggistica, compliance, fatturazione, review, reporting, test e rollout; è backlog operativo, non evidenza di funzionalità già implementate |
| S9 | Email Octorate «istruzioni per accedere», riportata dal PO il 30/10/2026; testo integrale ripetuto dal PO in chat il 31/10/2026 (non contiene segreti) | Flusso OAuth authorization code, whitelist redirect URI, durata token, link alla documentazione e canale di supporto; fonte operativa più recente per l'autenticazione |
| S10 | Email Octorate con credenziali partner, riportata dal PO il 30/10/2026 (valori segreti deliberatamente omessi) | Host frontend/API, accesso limitato alle proprietà test, un'autorizzazione e una coppia di token per proprietà. L'email contiene anche la frase «current credentials have expired»: validità dei valori consegnati da verificare, non presumere |
| S11 | Email di attivazione account «CasaZen calendar master», riportata dal PO il 30/10/2026 (username, password, email e ID omessi) | Conferma che l'account utente del network di test è stato attivato; le credenziali UI servono al login/autorizzazione del proprietario e non sono credenziali applicative CasaZen |
| S12 | Mandato del PO in chat, 31/10/2026: otto processi, diktat UX, aggiunta su wizard, avvisi e ricerca | Perimetro e regole UX della v2.0; prevale su S1–S2 per il perimetro |
| S13 | OpenAPI e collection Postman riscaricate il 31/10/2026: OpenAPI 3.0.1 `1.0.DEVEL`, 164 percorsi, 208 operazioni, 178 schemi, sha256 `4d36489bd35827f863c78a47fbb765cc33eb8923ab4f98cc62c2e41b28f23989`; Postman sha256 `073708db3897495e601cd73edf046c5bd15412672188ec03eb5bf9a40bb18ba0` | Verifica per check-in, ospiti, tassa, fatture, pagamenti, prodotti extra, licenze e portali (§10); copie non nel repository (licenza «Proprietary») |
| S14 | Codice di `develop` del 30/10: backend `ea90482c`, frontend `ad3008c0`, mobile `3cf8dd7` | Base del codice (§4) |
| S15 | Requisiti Quokka fase 1 (`Sessions/prospect-quokka360/requisiti-fase1-helix.md`, Q1-01…29) e ADR-010…022 | Funzioni già costruite da riusare: consensi, coda di approvazione, condizioni, servizio AI, routine e bus di eventi, Alloggiati, rendiconti, fatture dell'agenzia, CRM |
| S16 | Benchmark UX del 31/10/2026 (ricerca web su Guesty, Airbnb, Lodgify, Krossbooking, Dolcebot e altri), Appendice A | Pattern di guida, avvisi e automazione; le affermazioni di marketing sono segnate come tali |
| S17 | Manuale del web service Alloggiati, sintesi in `.claude/context/regulations/alloggiati.md` §4 | La ricevuta è per giorno e per utenza, non per schedina (base di CHK-04) |

Dove S13 contraddice S7 (resoconto di Octorate) la v2.0 lo segnala e chiede conferma scritta
(§14): non dà per buona né l'una né l'altra versione.

## 4. Base del codice al 30/10 (S14)

Inventario in sola lettura di backend, frontend e mobile di `develop`. Indica punti di riuso, non
attesta che una funzione sia operativa in produzione: Helix rifà il confronto all'avvio (H0).
Percorsi: backend da `Casazen.*`, frontend da `src/`.

### 4.1 Annunci, prenotazioni, calendario

| Area | Riusare | Manca o va corretto |
|---|---|---|
| Immobile | `Property` (un'unità prenotabile): dati generali, paese e valuta (non modificabile dopo la creazione), fuso, CIN con scadenze, codice ISTAT del comune, comune svizzero, dati catastali; `PropertiesController`; frontend `features/properties` | Stanze e tipi di letto (solo conteggi); tre modi di creare un immobile da ridurre a uno |
| Testi | `ListingTitle`, `Description` (una lingua, 2000 caratteri); profilo del sito `SitePropertyProfileDocument` con `LocalizedText` IT/EN (`api/properties/{id}/site-profile`) | Altre lingue; varianti per canale; **nessun editor del profilo nel frontend**; il «Suggerisci un testo» è a modello fisso |
| Foto | `GET/POST/DELETE {id}/images`, ordine e copertina, `IFileStorage` (S3), fino a 20 foto | Didascalie e testi alternativi modificabili dall'interfaccia; indirizzi pubblici per Octorate (PO-15) |
| Dotazioni | `PropertyAmenity` (circa 50), `AmenityCatalog` IT/EN, `GET api/public/amenities` | Abbinamento ai codici Octorate e OTA; etichette ripetute in tre posti |
| Prezzi | `StayPricing` (regola unica), prezzo per notte, pulizia, maggiorazione del fine settimana, tassa per comune; suggerimenti stagionali (`PricingAdapterConfig`, `SeasonalPriceSuggestion`, `DynamicPricingJob`) | Sconti per durata, piani tariffari, prezzo per data senza suggerimento; costo per ospite in più e cauzione senza effetto |
| Disponibilità | `CalendarBlock` (manuale o iCal), `PropertyOccupancy`, `PublicCalendarService` (disponibilità, prezzo e soggiorno minimo per giorno) | Soggiorno massimo, arrivi e partenze chiusi, finestra di prenotazione, soggiorno minimo per data |
| Politiche | Catalogo delle politiche di cancellazione con eccezioni per immobile; opzioni di pagamento (subito, alla scadenza della cancellazione, in struttura) | Varianti per canale |
| iCal | `PropertyICalFeed` cifrato, sincronizzazione ogni 15 minuti, esportazione per token, ADR-002, `docs/runbooks/ical.md`; pagina «Canali» del frontend (solo iCal) | Piano di passaggio per canale al connettore |
| Codice OTA | Solo come elenco di cosa non fare | `Casazen.Infrastructure/OTA/*`, `OtaManager`, `OtaSyncJob`, `BookingPullJob`, webhook `webhooks/ota/{platform}`: adattatori che dichiarano successo senza chiamare nulla, URL Airbnb non pubbliche, job con id vuoto, firma inventata, `OtaSyncLog` senza `OrgId` e mai scritto, pagine del frontend verso endpoint inesistenti. Flag `OtaPartnerApi` spento: **sostituire, non riattivare** |
| Prenotazioni | `Booking` (stati, origini, importi, `ExternalId`, codice), dirette con Stripe (attesa di 30 minuti, conferma da webhook), manuali (`POST api/bookings/manual`), soggiorni OTA dai blocchi iCal con verifica, ciclo di vita e wizard di check-out (`BookingLifecycleController`), cancellazione con anteprima, pagamenti e rimborsi idempotenti, permessi `ota.read`/`ota.write` | Commissione OTA, netto, importo pagato memorizzato, valuta; chiave univoca per le prenotazioni esterne (l'indice su `ICalFeedId, ExternalId` non è univoco); due endpoint per la prenotazione manuale; `TouristTax` e `TouristTaxAmount` duplicati |
| Calendario | Linea temporale di tutti gli immobili (`calendar-page.tsx`, `host-timeline.tsx`), `GET api/calendar` (92 giorni, 50 immobili); mobile: un immobile, un mese | Modifica di prezzi e restrizioni dal calendario, modifiche in blocco |
| Eventi | Hangfire con job non sovrapposti, lock consultivi per immobile, schema dei webhook Stripe (firma → coda → evento rivendicato in `ProcessedStripeEvents`), `Routine`, `RoutineExecution`, `RoutineService`, `RoutineDelayJob` | **Bus di eventi con outbox dell'ADR-022 non costruito**; `RoutineEventDispatcher` registrato ma mai chiamato; nessuna tabella generale dei webhook in entrata |

### 4.2 Interfaccia: guida, bozze, attività

| Area | Riusare | Manca o va corretto |
|---|---|---|
| Wizard | Kit `components/shared/wizard/` (`wizard-shell`, `wizard-summary`, `wizard-done`) e `ui/stepper.tsx`: passo in `?step=`, validazione per passo, salvataggio prima di avanzare, passi facoltativi, banner «riprendi o ricomincia», uscita, schermata finale. Usato da: immobile breve e lungo, cambio di modalità, prenotazione manuale, check-out, invito al team, richiesta di intervento, due wizard dei fornitori | Livello separato (UX-01); fuori dal kit: vecchia attivazione, contratto di locazione, check-in lato host; uscite verso altre pagine (UX-02) |
| Bozze | `use-wizard-draft.ts` (scheda del browser, 24 ore), salvataggio per passo del wizard dell'immobile, salvataggio sul server con versione del contratto (`use-lease-draft-saver.ts`) | Bozza sul server per tutti i wizard e su più dispositivi (UX-03); nessuna protezione dell'uscita nella navigazione interna |
| Guida e attività | Checklist di attivazione dal server (`GET api/onboarding/status`), schede «prossimo passo» (`next-step-card.tsx`, `booking-next-step.ts`, `property-next-step.tsx`), stati vuoti, `help-tip.tsx`, guida del prodotto (`help/product-guide-page.tsx`) | Un solo centro «Da fare» (UX-07); popup (UX-08); la guida del prodotto va aggiornata con ogni flusso consegnato |
| Flag | `FeatureFlags.cs`, `IFeatureFlags`, `[FeatureGate]`, `GET /api/public/features`, `featureFlag` nel manifest delle rotte; tutti spenti per default | `AgencyTier` dichiarato ma letto da nessuno e senza chiave nel frontend; il flag `routines` citato dall'ADR-022 non esiste; CRM, campagne, coda di approvazione, routine e messaggi non hanno flag |

### 4.3 Check-in, adempimenti, fiscale e report

| Area | Riusare | Manca o va corretto |
|---|---|---|
| Check-in online | `PublicGuestCheckInController` (`api/public/checkin/{token}`), `GuestCheckInService`, `GuestCheckInSession`, `StayGuest` con i campi e i codici Alloggiati, invio automatico del link 3 giorni prima, completamento dell'host, consensi | Lingue (IT ed EN, apertura in italiano senza scelta); campi statistici regionali |
| Arrivo e partenza | `Booking.Status` `CheckedIn`/`CheckedOut`, `ArrivedAt`, `CheckedOutAt`, `BookingLifecycleController`, wizard di check-out (web e mobile) | Uscite dal wizard (UX-02); sincronizzazione verso Octorate |
| Alloggiati | Percorso manuale reale: `AlloggiatiController` (riepilogo, stato, file da 168 caratteri, «segna come inviato»), stati onesti con vincolo sulla ricevuta, tabelle ufficiali aggiornate, avvisi orari (`StayAlertService`), credenziali per immobile cifrate e in sola scrittura | Invio automatico e ricevute: vedi §4.5; nessun archivio delle ricevute; `AlloggiatiPage` chiama endpoint inesistenti |
| ISTAT e flussi regionali | Analisi: `docs/runbooks/istat-flussi-regionali.md` (nove famiglie di portali), `docs/runbooks/ross1000.md` | Nessuna implementazione (per decisione) |
| Tassa di soggiorno | `TouristTaxCalculator` (tariffa per notte, stagioni, categorie, tetto di notti, età, percentuale con tetto; mai zero presunto), tariffe con fonte e livello di verifica, aggiornamento giornaliero, CRUD dell'admin; report per comune e mese (`GET api/fiscal/reports/tourist-tax`) | Solo 16 comuni; esenzioni per residenti e altre; tetti annuali; collegamenti ai portali, scadenze, ricevute; i soggiorni OTA di solito non hanno importo |
| Documenti di conformità | `PropertyDocument` (CIN, APE, licenza…), linea temporale della prenotazione, registri di privacy e di attività dell'organizzazione | Ricevute degli adempimenti; registro degli eventi di conformità |
| Pagamenti | `Payment`, `PaymentRefund`, `PaymentsController`, rimborsi Stripe, pagamenti manuali, CSV dal browser | Commissione Stripe, accredito, commissione OTA |
| Fatture | `PlatformInvoiceService` (CasaZen → organizzazione, solo server, SDI non configurato); `AgencyInvoiceService` (prototipo: §4.5) | Ricevute e fatture all'ospite; dati dell'emittente |
| Report fiscali | `FiscalService` (redditi annuali, cedolare stimata, ritenute subite, tassa per comune; JSON, CSV, PDF con MigraDoc) | — |
| Report di gestione | `DashboardController` (occupazione, ricavi, incassato, quota diretta, serie), `ReportsController` (ricavi, occupazione, canali in CSV), ruolo Contabile in sola lettura | ADR e RevPAR, costi dei canali, Excel, archivio unico, mobile |

### 4.4 Messaggi, avvisi, AI, clienti, marketplace

| Area | Riusare | Manca o va corretto |
|---|---|---|
| Messaggi | Thread delle prenotazioni dirette (`GuestThread`, `GuestMessage`, `GuestMessageRules`: oscuramento, chiusura, idempotenza, limiti), `GET api/messages` con filtri «da rispondere» e immobile; runbook `guest-messages.md` | Pagina inbox; fonti OTA, email e WhatsApp; schermata mobile |
| Avvisi | Push con chiavi idempotenti, `PushTypes`, `PushRoutes`, `POST api/devices`, campanella web (`InAppNotification`, hub SignalR, 90 giorni), email all'indirizzo dell'organizzazione | Preferenze per utente; email alle persone; rotta mobile dei messaggi; `NotificationRouter` non usato; hub SignalR su una sola istanza |
| AI | `SwitchingAiProvider` (Stub, DeepSeek, OpenAI, Anthropic), budget, cache, `PiiScrubber`, avviso di trasparenza; soglie per organizzazione di `BiometricSettings` come modello per la confidenza | Servizio AI dell'agenzia (il frontend chiama un endpoint che non esiste); bozze e risposte ai messaggi |
| Clienti | `Guest` con scheda e strumenti GDPR completi, `MarketingConsentRecord` per canale, `GuestConsentRecord`, conferma e disiscrizione firmate, `SuppressionService` | Vedi §4.5 |
| Marketplace | Marketplace dei fornitori (richieste, preventivi, riserve, commissione, vetrina e pagamenti dietro flag), cornice a schede, pagina «Canali» iCal con stato, collegamento guidato a Stripe | Catalogo delle integrazioni, modello di connessione, attivazione e stato |

### 4.5 Stato reale della fase 1 (Q1)

Le PR della fase 1 sono in `develop`, ma diverse funzioni sono solo struttura. Da questo dipendono i
prerequisiti DEB di §6.0.

| Requisito della fase 1 | Stato nel codice al 30/10 |
|---|---|
| Q1-01 Livello Agenzia | Le policy controllano solo il livello, non i permessi del ruolo (DEB-01); flag `AgencyTier` mai letto; navigazione B2B inutilizzata |
| Q1-05 Consensi, doppia conferma, soppressione | Consensi per canale raccolti ma non letti; nessuno invia la email di conferma; soppressione con impronta senza chiave |
| Q1-06 Coda di approvazione | Approvare non esegue l'azione; nessuna modifica prima dell'approvazione; avviso mai inviato |
| Q1-07 Costruttore di condizioni | Ogni regola risulta falsa; anteprima zero; componente del frontend non usato |
| Q1-08 Servizio AI dell'agenzia | Assente (endpoint chiamato dal frontend inesistente) |
| Q1-09 Canali di messaggio | Solo email reale; WhatsApp e SMS sono valori di un enum; nessun registro degli invii |
| Q1-10, Q1-11 Documento e selfie | Lettura del documento sempre vuota; vivacità sempre vera; somiglianza fissa; componenti del frontend non usati |
| Q1-12, Q1-13 Verifica e codice | Permesso non assegnato e diverso tra frontend e backend; rilascio del codice mai chiamato; pagina QR non raggiungibile |
| Q1-14 Alloggiati automatico | Client, job e connessione HTTP non registrati; righe dal solo prenotante; nessuna ricevuta salvata (analisi AW-01) |
| Q1-15 Ticino e OTR | Formato semplificato; solo soggiorni confermati; pagina non raggiungibile |
| Q1-16…Q1-19 Proprietari, rendiconti, ritenute, bonifici | Portale non registrato; esportazioni 501; ritenute mai calcolate; indirizzi del frontend in 404; stati incoerenti tra frontend e backend |
| Q1-20 Fatture dell'agenzia | Prototipo: XML non validato con segnaposto, «PDF» non vero, nessuna interfaccia |
| Q1-23, Q1-24 CRM e campagne | Profili mai popolati; segmenti vuoti; campagne non approvabili; link di disiscrizione non firmato con l'email nell'indirizzo; pagine non raggiungibili |
| Q1-26 Routine | Nessuna API; azioni simulate; dispatcher mai chiamato; bus di eventi assente |

## 5. Regole UX trasversali (vincolanti)

Valgono per ogni processo di §6 e per le parti esistenti che i processi toccano. Estendono le
regole di semplicità già in uso (un solo punto focale per schermata, guida una cosa alla volta,
dettagli a strati), scritte nei blocchi «calmi» del frontend (`src/components/shared/calm/README.md`,
che riprende `redesign/docs/CONVENZIONI.md` §7bis del workspace, fuori dai repository), e il
modello di wizard del prototipo (stepper, bozza automatica, riepilogo, schermata finale). L'utente di riferimento è inesperto:
ogni regola si giudica chiedendo «una persona che usa poco il computer arriva in fondo da sola,
senza perdersi e senza perdere quello che ha scritto?».

#### UX-01 — Modalità guidata su un livello separato · P0

1. Ogni processo con più di un passo, con una scadenza di legge o con un passaggio su un sito
   esterno si svolge nella **modalità guidata**: un livello sopra l'app. Su desktop un pannello
   ampio in primo piano (centrato o laterale a tutta altezza), con l'app dietro oscurata e non
   cliccabile; su mobile a tutto schermo. È visivamente un'altra «dimensione» rispetto alle
   pagine (ombra, sfondo oscurato, ingresso animato breve che rispetta `prefers-reduced-motion`).
2. Barra propria in alto: nome del processo e dell'oggetto («Check-out · Rossi · 12–15 nov»),
   avanzamento («Passo 2 di 5 · Ospiti») con i nomi dei passi, «Salva ed esci». La X di chiusura
   equivale a «Salva ed esci».
3. È **dentro CasaZen**: mai una nuova finestra o scheda del browser. Ha un proprio indirizzo
   (processo, oggetto, passo) così che aggiornare la pagina, il tasto indietro del browser e i
   collegamenti da push, email e «Da fare» riaprano lo stesso passo con gli stessi dati.
4. Focus intrappolato nel livello, tastiera ed `Esc` gestiti, lettori di schermo informati del
   cambio di passo.
5. Ciò che serve per decidere (riepilogo della prenotazione, dati dell'immobile, scadenza) sta
   **dentro** il livello, in un pannello laterale richiudibile: l'utente non ha mai bisogno della
   pagina coperta (rischio segnalato per i wizard modali, Appendice A).
6. Un processo lungo si apre con il suo **piano**: le parti con stato «Fatto», «Da fare» o «Non
   ancora possibile» con il motivo (per esempio «serve prima il collegamento del canale»), e si
   chiude con «Controlla e conferma» che riporta a ogni parte da correggere.
7. Si costruisce sul kit di wizard esistente (`components/shared/wizard/wizard-shell.tsx`,
   `wizard-summary.tsx`, `wizard-done.tsx`, `ui/stepper.tsx`, passo in `?step=`), già usato da
   otto flussi: il livello è il contenitore in cui il kit si apre, non un secondo kit. Entrano nel
   kit anche i flussi che oggi lo evitano: la vecchia attivazione
   (`compliance/activation-wizard.tsx`), il wizard del contratto di locazione (cornice propria) e
   il check-in lato host.

Accettazione: ogni wizard di §6 si apre nel livello; dopo un aggiornamento della pagina si
ritrova lo stesso passo con gli stessi dati; nessun `target="_blank"` dentro un wizard.

#### UX-02 — Mai fuori dal processo · P0

1. Nessun collegamento o pulsante dentro un wizard porta a un'altra pagina dell'app. Ogni
   prerequisito o attività collaterale si risolve **dentro** il wizard come **sotto-passo**: un
   pannello annidato nello stesso livello, con «Fatto» e «Annulla», che riporta al punto da cui
   è stato aperto. Esempi: completare i dati di un ospite, inserire le credenziali Alloggiati,
   il CIN, un dato dell'immobile, un metodo di pagamento, la scelta di un fornitore.
2. I moduli condivisi sono componenti usati sia nelle pagine sia nei sotto-passi: una sola
   implementazione, una sola validazione.
3. Un sito esterno solo quando è inevitabile (consenso sul channel manager, accesso a una OTA,
   Stripe, portale Alloggiati per la chiave del web service, portale regionale): una schermata
   spiega in una frase cosa succede e cosa fare, il sito si apre nella stessa scheda e il
   ritorno porta **allo stesso passo** con la bozza intatta. Se il sito esterno non può
   riportare l'utente, il passo mostra «Quando hai finito, torna qui e premi Ho fatto» e
   CasaZen verifica da solo l'esito dove può (per esempio `policeAccount.valid`, §10.2).
4. Uscite da eliminare già trovate nel codice (S14): nel wizard di check-out il passo
   Alloggiati porta alla scheda della prenotazione (`checkout-wizard-steps.tsx`,
   `checkout-alloggiati-link` → `?tab=alloggiati`); il wizard di attivazione porta alla modifica
   dell'immobile (`activation-wizard.tsx` → `/properties/{id}/edit`); il passo della tassa di
   soggiorno (`tourist-tax-step-info.tsx`) e il riepilogo di attivazione
   (`activation-progress.tsx`) hanno collegamenti verso pagine. Helix completa l'elenco con la
   prova di UX-14.

Accettazione: in ogni wizard una prova clicca tutti i collegamenti e i pulsanti e verifica che
il livello resti aperto sul percorso del wizard; i sotto-passi tornano al passo d'origine.

#### UX-03 — Salvataggio continuo, ripresa, nessun dato perso · P0

1. Ogni modifica si salva da sola in una **bozza sul server** (per utente, organizzazione,
   processo e oggetto), non solo nel browser: sopravvive alla chiusura e al cambio di
   dispositivo. Indicatore discreto «Salvato alle 10:42».
2. «Salva ed esci» è sempre disponibile; la bozza compare in «Da fare» come «Riprendi:
   <processo>», con il passo, e «Riprendi» riapre lo stesso passo con gli stessi dati.
3. Nessun dato perso con: errori di validazione (i campi restano compilati), sessione scaduta
   (dopo il nuovo accesso si torna allo stesso passo), rete assente (salvataggio in coda e
   avviso «Senza rete: salveremo appena torna»), modifica contemporanea di un'altra persona
   (avviso, mai sovrascrittura silenziosa).
4. Conservazione delle bozze: 30 giorni per default, con un promemoria prima della scadenza;
   «Elimina bozza» solo esplicito e confermato. I dati personali nelle bozze seguono le stesse
   regole di cifratura e conservazione dei dati definitivi (documenti compresi).
5. Oggi la bozza generica dei wizard vive solo nella scheda del browser (`sessionStorage`, 24
   ore, `hooks/use-wizard-draft.ts`); salvano sul server solo il wizard dell'immobile (dopo il
   primo passo) e quello del contratto (con controllo di versione e dei conflitti,
   `use-lease-draft-saver.ts`). Il modello del contratto diventa quello di tutti i wizard.
6. Uscire dalla pagina con modifiche non salvate è intercettato anche nella navigazione interna
   dell'app (oggi esiste solo `beforeunload` in alcuni flussi).

Accettazione: compilare metà wizard, chiudere il browser, riaprire da «Da fare» su un altro
dispositivo: stesso passo e stessi dati. Prova: la bozza di un utente non è leggibile da un
altro utente o da un'altra organizzazione.

#### UX-04 — Un argomento per passo, parole semplici · P0

1. Un argomento per passo e al massimo circa cinque campi visibili; le opzioni rare sotto
   «Altre opzioni».
2. **Precompilare** con ciò che CasaZen sa già (immobile, soggiorni precedenti, import da
   Octorate, lettura del documento, risposte già date): l'utente conferma invece di scrivere.
3. Parole semplici, niente sigle senza spiegazione alla prima comparsa («Alloggiati Web, la
   comunicazione degli ospiti alla Polizia»); un esempio sotto i campi difficili; «Perché te lo
   chiediamo?» dove il dato sembra strano.
4. Un solo pulsante principale per passo («Continua»); «Indietro» sempre; le azioni distruttive
   non sono mai il pulsante principale.

#### UX-05 — Riepilogo prima, risultato dopo · P0

1. Prima di un'azione che ha effetti fuori da CasaZen o non si annulla: riepilogo in parole
   semplici di cosa succederà, dove e quando («Pubblicheremo l'annuncio su Airbnb e
   Booking.com; può richiedere fino a 30 minuti»), con «Modifica» che torna al passo (dentro il
   wizard).
2. Dopo: schermata di esito per elemento (per canale, per ospite, per documento), cosa farà
   CasaZen da solo e quando, e **un solo** passo successivo proposto (UX-10).
3. Un errore è spiegato in parole semplici con il pulsante «Correggi» che apre il passo giusto.

#### UX-06 — Via d'uscita, aiuto, «lo faccio dopo» · P0

1. Sempre visibili: «Salva ed esci» e «Aiuto» (articolo contestuale e contatto; più avanti
   l'assistente AI-12). Nei passi non bloccanti anche «Lo faccio dopo», con la scelta di quando
   ricordarlo.
2. Uscire non annulla mai un processo in silenzio: ciò che resta da fare finisce in «Da fare»
   con la sua scadenza.

#### UX-07 — «Da fare»: un solo posto per le attività · P0

1. Un centro attività per utente con tutto ciò che deve fare, generato dal sistema: check-in
   incompleti, scadenze Alloggiati e ISTAT, ricevute da recuperare, messaggi e bozze AI da
   approvare, errori di pubblicazione, documenti fiscali da emettere, bozze di wizard da
   riprendere, integrazioni in errore, credenziali da rinnovare.
2. Ogni voce dice cosa, su quale oggetto, entro quando, perché conta (una riga) e ha **un solo
   pulsante, «Fai ora»**, che apre il wizard giusto al passo giusto (UX-01).
3. Ordine per urgenza (scadenza, obbligo di legge, denaro), perimetro per ruolo, permessi e
   immobili (`HostScope`).
4. La Home mostra la prima voce nel blocco «Adesso» (`FocusBlock`, un solo punto focale) e le
   successive con `TodayList`; la navigazione ha un contatore delle sole voci urgenti.
5. Una voce si chiude da sola quando la condizione è soddisfatta.
6. **Una sola fonte sul backend** per web, mobile, email e push: confluiscono le logiche oggi
   sparse, che restano come viste della stessa lista: prossimo passo della prenotazione e
   dell'immobile (`booking-next-step.ts`, `property-next-step.tsx`), righe di compliance
   (`compliance-summary-widget.tsx`), checklist di attivazione (`GET api/onboarding/status`).

Prova: un collaboratore con perimetro «Solo alcuni» non vede voci di altri immobili.

#### UX-08 — Avvisi proattivi: push e popup · P0

1. Le voci con una scadenza o un obbligo di legge generano avvisi: push sul mobile, push del
   browser dove l'utente lo consente (PO-14), email secondo le preferenze, e un **popup in primo
   piano** all'apertura dell'app o all'avvicinarsi della scadenza.
2. Il popup è uno alla volta; dice cosa, entro quando, «Fai ora» (apre il wizard) e
   «Ricordamelo più tardi» (tra un'ora, stasera, domani mattina: mai oltre la scadenza); non
   copre un wizard aperto; mai per voci non urgenti.
3. Escalation per scadenza: primo avviso quando l'attività diventa possibile, promemoria a metà
   del tempo rimasto, avviso urgente a 6 ore, avviso di scadenza superata anche agli
   amministratori dell'organizzazione.
4. Contro l'eccesso di avvisi: raggruppamento («3 arrivi di oggi hanno dati da completare»),
   ore di silenzio (default 22–8; un obbligo di legge che scade dentro la finestra la supera),
   riepilogo giornaliero, preferenze per tipo e canale con lo stesso modello di MSG-05. Gli
   avvisi di obblighi di legge non si spengono del tutto: si sceglie il canale, almeno uno resta.
   Consigli e suggerimenti sono spenti per default.
5. **Successo silenzioso, eccezione evidente**: ciò che va a buon fine si registra senza
   disturbare (stato e cronologia); si avvisa per ciò che richiede una persona.

Accettazione: un invio Alloggiati in sospeso produce push e popup ai tempi configurati; il
rinvio rispetta la scadenza; le preferenze sono rispettate.

#### UX-09 — Uno stato chiaro per ogni cosa · P0

Ogni oggetto (annuncio, prenotazione, ospite, adempimento, documento, integrazione) mostra un
solo stato in parole semplici con colore e icona, e lo stato per canale dove serve
(«Pubblicato su Airbnb · In attesa su Booking.com · Da correggere su Vrbo: manca la foto di
copertina»). Mai un «errore» generico; orari nel fuso dell'immobile; stesso vocabolario su web,
mobile, email e push.

#### UX-10 — Percorsi di wizard collegati · P0

1. I wizard formano percorsi. Primo percorso, «Inizia con CasaZen»: collega i canali (LST-02) →
   controlla gli annunci importati (LST-03) → prezzi e calendario (LST-09) → check-in online e
   adempimenti (CHK-01, CHK-03) → messaggi e AI (MSG-06) → documenti fiscali (FIS-02).
2. La lista del percorso è compatta (`ChecklistCompact`, oppure `NextStepCard` per il solo passo
   successivo, mai entrambi), visibile solo finché è incompleta; ogni voce apre il suo wizard.
3. Alla fine di un wizard si propone solo il passo logico successivo, con «Più tardi» che lo
   mette in «Da fare».

#### UX-11 — Accessibile a chi usa poco il computer · P0

WCAG 2.2 AA; testo di almeno 16 px; aree cliccabili di almeno 44 px; contrasto alto; nessuna
azione solo al passaggio del mouse; icone sempre con etichetta; tastiera e lettore di schermo;
conferma prima delle azioni distruttive e annullamento dove possibile; date e numeri nel
formato italiano; mobile prima di tutto.

#### UX-12 — Nomi e marchi · P0

L'interfaccia non nomina il channel manager tranne dove l'utente deve agire sul suo sito
(consenso iniziale, prove non disponibili via API), con una dicitura neutra decisa dal PO
(PO-01). I nomi delle OTA si mostrano. La regola già in vigore per il fornitore del canone
concordato (nessun nome del fornitore nell'interfaccia) resta il riferimento.

#### UX-13 — Web e mobile coerenti · P1

Stesse voci di «Da fare» e stessi avvisi su web e mobile; il push apre l'app sull'elemento. Sul
mobile i wizard brevi (check-in ospiti, check-out, risposta a un messaggio, approvazione di una
bozza AI); quelli lunghi (creazione di un annuncio) si avviano sul computer e il mobile mostra
«Continua dal computer» con la bozza salvata.

#### UX-14 — Prove UX di ogni wizard (Definition of Done) · P0

Per ogni wizard: percorso completo dall'inizio alla fine; «Salva ed esci» e ripresa allo
stesso passo con gli stessi dati; nessuna uscita (UX-02); sessione scaduta; controllo di
accessibilità automatico su ogni passo; viewport mobile. Prima del rilascio di un processo
il PO fa una prova d'uso con almeno cinque persone non esperte (gate G3, fuori da Helix).

#### UX-15 — «Fallo per me» · P2

In ogni wizard di configurazione (collegamento dei canali, adempimenti, emissione dei documenti)
l'utente può chiedere che lo completi il supporto di CasaZen: la richiesta porta con sé la bozza,
crea un'attività per lo staff della piattaforma e l'utente vede lo stato in «Da fare». È la
corsia per chi non arriverà mai in fondo da solo (configurazione assistita offerta anche da altri
operatori, Appendice A); ogni azione dello staff resta nel registro dell'organizzazione.

## 6. Requisiti per processo

Ogni processo dice cosa fa Octorate (verificato su S13 o dichiarato in S6/S7), cosa c'è già in
CasaZen (§4) e i requisiti. I wizard citati seguono tutte le regole di §5; i passi elencati sono
quelli minimi, Helix può dividerli ma non aggiungere uscite dal processo.

### 6.0 Prerequisiti: debiti della fase 1 da chiudere (DEB)

Molte funzioni della fase 1 (Q1-xx), registrate come completate, sono nel codice solo come
struttura: entità e servizi presenti, logica segnaposto o non collegata (§4.5). I processi di
questo documento le usano, quindi vanno **completate**, non date per esistenti. DEB-01 si fa per
primo e non aspetta Octorate.

#### DEB-01 — Sicurezza e privacy · P0, subito

1. **Permessi dell'Agenzia.** Le policy `agency.*` controllano solo il livello dell'organizzazione
   (`AgencyTierAuthorizationHandler`): qualunque membro di un'organizzazione Agenzia, collaboratori
   e contabili compresi, passa anche `agency.owner.financial.read` (IBAN completo dei proprietari)
   e `agency.wire.manage` (lotti di bonifico). Ogni policy deve controllare anche il permesso del
   ruolo. Prova: un collaboratore riceve 403 sull'IBAN completo e sui lotti.
2. **Endpoint pubblici con esiti finti.** `/document` e `/selfie` del check-in rispondono con esiti
   fissi (vivacità sempre vera, somiglianza costante, lettura del documento sempre vuota): dietro un
   flag spento finché non sono reali, senza conservare immagini.
3. **Campagne.** Le correzioni di CRM-08 prima di qualunque invio reale; voci di menu dei moduli non
   finiti (CRM, campagne, coda di approvazione, routine) dietro un flag (Q1-29).

#### DEB-02 — Bus di eventi con outbox (ADR-022) · P0

Accettato ma non costruito: necessario a BKG-02, alle routine, agli avvisi e ai report. Parte del
pacchetto H1.

#### DEB-03 — Coda di approvazione completa · P0

Vedi MSG-08.

#### DEB-04 — Condizioni e segmenti reali · P1

Vedi CRM-04 (oggi ogni regola risulta falsa; anteprima sempre zero; il catalogo dei campi non è
esposto; il componente del frontend usa un nome di campo diverso dal backend).

#### DEB-05 — CRM popolato · P0

Vedi CRM-01 e CRM-08 (servizio di abbinamento registrato, profili creati, esportazione e
cancellazione complete).

#### DEB-06 — Routine eseguibili · P1

API delle routine, azioni reali al posto dei testi «(email inviata)», condizioni reali, attese
programmate, dispatcher collegato al bus (DEB-02), pagine raggiungibili, flag `routines`. Sono la
base dei promemoria e di diverse automazioni di §7.

#### DEB-07 — Avvisi per persona · P0

Vedi MSG-05 (`NotificationRouter` oggi registrato e non usato, email solo all'indirizzo
dell'organizzazione, nessuna preferenza per utente).

#### DEB-08 — Client Alloggiati collegato · P1

Completamento secondo `docs/requirements/AW-01-alloggiati-web-completamento-helix.md`: client e job
registrati, righe costruite dagli ospiti del soggiorno, `Tabella`, profilo «Gestione
Appartamenti», ricevuta archiviata, pagina dei rapporti con endpoint esistenti. Serve agli immobili
non collegati (CHK-03) e allo scaricamento delle ricevute (CHK-04).

#### DEB-09 — Verifica dell'operatore e codice d'accesso · P1

Serve a CHK-11 (identificazione dal vivo, ⚖): chiave del permesso uguale tra frontend e backend e
assegnata ai ruoli, decisione manuale registrata con il modo dell'identificazione, rilascio del
codice collegato e possibile solo dopo; spento finché non è completo.

#### DEB-10 — Catena finanziaria dell'agenzia · P1

Portale proprietari registrato, esportazioni dei rendiconti (oggi 501), calcolo delle ritenute (mai
chiamato) e F24 in PDF, indirizzi del frontend corretti (oggi 404), stato richiesto per i bonifici
allineato tra frontend e backend, validazione del pain.001, FatturaPA reale (FIS-03), valuta del
rendiconto non fissa in euro. Usa gli importi delle OTA di BKG-01 (risponde a D-03 della fase 1).

#### DEB-11 — Ticino e OTR · P2

Formato ufficiale (oggi semplificato), soggiorni arrivati e partiti inclusi (oggi solo
confermati), ricevuta conservata (oggi solo l'impronta), pagina raggiungibile, promemoria
programmato.

### 6.1 Annunci e strutture (LST)

**Octorate.** Proprietà, unità e tariffe (tre versioni delle API `roomrates`), contenuti per
prodotto (descrizioni per lingua con varianti per portale, annuncio, stanze e letti, foto in coda,
disponibilità, costi, tariffa, prezzi, regole di prenotazione, cancellazione) e per connessione
(impostazioni della struttura, servizi, costi, letti aggiuntivi, contatti, politiche di
cancellazione, contratto di locazione, invio finale), foto della struttura, catalogo delle
dotazioni (`meta/octorateAmenities`) e dei campi per portale (`meta/content`,
`meta/contentPageFields`), traduzione delle descrizioni mancanti, stato dei processi di
pubblicazione e webhook `CONTENT_NOTIFICATION`. `pushQueue` solo per Booking.com, Airbnb e Vrbo;
Expedia da confermare; nessun campo CIN dedicato (D2). Import degli annunci già presenti sui
canali con `IMPORT_ROOMS`, `IMPORT_RESERVATIONS`, `IMPORT_CALENDAR` e mapping (D6).

**CasaZen oggi (§4).** Un immobile è un'unità prenotabile. Completi: dati generali, CIN e codici,
foto (fino a 20, copertina, ordine), dotazioni (circa 50 valori con catalogo IT/EN), politiche di
cancellazione e di pagamento, orari e regole di base, iCal maturo, scheda pubblica del sito.
Parziali: descrizione in una sola lingua più un profilo del sito IT/EN **senza editor
nell'interfaccia**; prezzi per data solo applicando un suggerimento stagionale; soggiorno minimo
unico per immobile. Assenti: stanze e tipi di letto (solo conteggi), sconti per durata, piani
tariffari, soggiorno massimo, arrivi e partenze chiusi, abbinamento delle dotazioni ai codici
delle OTA. Il costo per ospite in più e la cauzione sono salvati ma non hanno effetto. Il codice
OTA esistente (`Casazen.Infrastructure/OTA/*`, flag `OtaPartnerApi` spento) è un segnaposto che
dichiara successo senza chiamare nulla: **si sostituisce, non si riusa**. Tre modi di creare un
immobile (modulo rapido, wizard, vecchia attivazione) vanno ridotti a uno.

#### LST-01 — CasaZen fonte unica dei contenuti · P0

1. Per un immobile collegato, i contenuti si modificano in CasaZen e CasaZen li propaga a Octorate
   e ai canali (autorità in §8.2). L'utente non deve aprire né Octorate né le extranet delle OTA
   per le funzioni coperte dalla matrice (§11).
2. Le modifiche fatte fuori da CasaZen (in Octorate o su una OTA) si rilevano a ogni lettura
   periodica e con `CONTENT_NOTIFICATION`, e si mostrano come «Modificato fuori da CasaZen» con
   la scelta «Tieni la versione di CasaZen» o «Prendi la nuova versione»; mai sovrascrittura
   silenziosa.
3. Ogni campo ha uno stato per canale (UX-09): bozza, in invio, accettato dal channel manager,
   pubblicato sulla OTA, da correggere (con il motivo in parole semplici).

Prova: una modifica fatta in Octorate non viene sovrascritta senza una scelta dell'utente.

#### LST-02 — Wizard «Collega i tuoi canali» · P0

Passi: perché collegare (una schermata) → consenso sul channel manager per ogni immobile (§9.1,
stessa scheda e ritorno allo stesso passo) → canali trovati con il loro stato → abbinamento degli
annunci dei canali agli immobili di CasaZen (proposto automaticamente per indirizzo, nome e
capienza; l'utente conferma) → importa → riepilogo.

1. Per gli annunci già presenti sui canali si conservano gli identificativi esistenti (D6); un
   abbinamento ambiguo resta da confermare e blocca le scritture su quell'immobile (OCT-04).
2. Un immobile non ancora presente in Octorate si crea da CasaZen solo con LST-03, che ottiene il
   token della proprietà alla creazione senza consenso separato. Il wizard sceglie l'ingresso con
   una sola domanda iniziale (i due ingressi di §9.1; PO-19 per le credenziali del proprietario).
3. La sincronizzazione iCal dell'immobile passa al connettore canale per canale senza doppie
   importazioni (ADR-002, OCT-03 della v1.3 §3).

Accettazione: con l'adattatore Mock, dieci immobili collegati, importati e abbinati senza uscire
dal wizard; un abbinamento ambiguo compare in «Da fare».

#### LST-03 — Wizard «Nuovo annuncio» e «Controlla l'annuncio importato» · P0

Passi: tipo, indirizzo e codici (CIN, comune) → spazi, letti e ospiti → foto → servizi e
dotazioni → descrizioni e lingue → regole e condizioni → prezzi e disponibilità → canali su cui
pubblicare → riepilogo e pubblicazione.

1. Lo stesso wizard, precompilato, serve a controllare un annuncio importato: l'utente vede solo
   i passi con dati mancanti o da confermare.
2. Riusa il wizard dell'immobile esistente (§4: crea l'immobile al primo passo e salva ogni
   passo sul server) portandolo nel livello guidato; il modulo rapido e la vecchia attivazione
   confluiscono in questo flusso.
3. AI-01 propone i testi e l'ordine delle foto; AI-03 segnala ciò che manca per ciascun canale.
4. La pubblicazione crea o aggiorna la struttura in Octorate e invia ai canali scelti; un errore su
   un canale non annulla gli altri (UX-05, punto 2).
5. Le regole dei canali si vedono nel passo in cui servono (per esempio il numero minimo di foto
   chiesto da un portale) e non solo all'invio; un dato che può aspettare (il CIN in attesa del
   comune) si rimanda con «Lo inserisco dopo», che crea una voce in «Da fare» e blocca solo la
   pubblicazione sui canali che lo esigono.
6. «Duplica un annuncio» per chi gestisce appartamenti simili: copia tutto tranne foto, indirizzo
   e codici, poi apre il wizard sui passi da completare (modello del wizard di Octorate,
   Appendice A).

#### LST-04 — Informazioni generali e codici · P0

Nome, tipo (appartamento, casa, villa…), indirizzo, posizione, orari di check-in e check-out
(`checkinStart`, `checkinEnd`), lingua, contatti, CIN e altri codici di legge. Il campo in cui
ogni OTA riceve il CIN si decide con la risposta a D2 (candidati `legalId`, `legalIdIssuer`,
`taxId`); fino ad allora il CIN resta in CasaZen e l'interfaccia dice dove non è ancora inviato.

#### LST-05 — Descrizioni multilingua · P0

1. Testi per lingua (titolo, descrizione, spazi, accesso, quartiere, trasporti, regole) con
   variante per canale dove serve (`LanguageMap`, `PortalLanguageMap`); lingue offerte: quelle di
   Octorate (IT, EN, FR, ES, DE, RU, PT, NL, JA, EL, TR, ZH, CA, RO).
2. Italiano e inglese obbligatori per pubblicare; le altre facoltative con AI-02.
3. Ogni testo dice se è scritto dall'utente, tradotto automaticamente o modificato dopo la
   traduzione.
4. Il modello dei testi multilingua estende quello del profilo del sito (`LocalizedText`,
   `SitePropertyProfileDocument`, oggi IT ed EN) invece di crearne un secondo; l'editor nasce
   nell'interfaccia (oggi manca) e serve sia al sito sia ai canali. Il «Suggerisci un testo»
   attuale, a modello fisso, lascia il posto ad AI-01.

#### LST-06 — Foto e contenuti multimediali · P0

1. Caricamento in CasaZen (anche da telefono), ordine con trascinamento, copertina, didascalie,
   assegnazione alle stanze; controllo di dimensione e qualità (AI-03).
2. Octorate scarica le foto da un indirizzo: CasaZen espone le sole foto degli annunci con
   indirizzi pubblici non indovinabili (PO-15); stato della coda per foto (`photos/queue`).
3. Eliminare o sostituire una foto in CasaZen la toglie o la sostituisce sui canali.

#### LST-07 — Servizi, dotazioni e caratteristiche · P0

Catalogo di CasaZen (`AmenityCatalog`, oggi con le etichette ripetute in tre posti da ridurre a
uno) abbinato al catalogo di Octorate e ai codici dei portali (`meta/content`);
ricerca per parola («lavatrice»), gruppi semplici (cucina, bagno, esterni, sicurezza,
accessibilità); dotazioni riconosciute dalle foto proposte da AI-01. Servizi a pagamento venduti
con il soggiorno (prodotti extra di Octorate) solo P2.

#### LST-08 — Unità, stanze, letti e tipologie · P0

Un annuncio corrisponde a un'unità indipendente (PO-13): stanze con tipo e numero di letti
(nuovo: oggi CasaZen ha solo i conteggi; Octorate le vuole in `rooms`), ospiti massimi, bagni,
metratura. Più unità in una struttura (residence) dopo il pilota; gli hotel
restano esclusi (v1.3 §1).

#### LST-09 — Tariffe e piani tariffari · P0

Prezzo base, costi (pulizia, ospite in più, animali), sconti per durata, piani tariffari
(`rateplans`), valuta dell'immobile (Q1-02). Si parte dalla regola unica dei prezzi di CasaZen
(`StayPricing`); il costo per ospite in più e la cauzione, oggi salvati senza effetto, o si
applicano o si tolgono dall'interfaccia; sconti per durata e piani tariffari sono nuovi. CasaZen è l'unico scrittore (PO-16); le regole
automatiche di Octorate (prezzi derivati, `runRules`, chiusure automatiche, revenue) si leggono e
si mostrano, e restano spente salvo decisione.

#### LST-10 — Disponibilità, prezzi e restrizioni (calendario) · P0

Riprende OCT-06: modifica per giorno e per periodo dal calendario di CasaZen (BKG-07), soggiorno
minimo e massimo, arrivi e partenze chiusi, anticipo massimo e minimo (oggi CasaZen ha solo un
soggiorno minimo per immobile: le restrizioni per data sono nuove e alimentano anche il sito);
la base è il calcolo per giorno già esistente (`StayPricing`, `PropertyOccupancy`,
`PublicCalendarService`); differenze e invio in
blocco con stato del processo; date nel fuso dell'immobile. Un'accettazione del channel manager
non è una pubblicazione sulla OTA.

#### LST-11 — Regole della struttura e condizioni di prenotazione · P0

Regole strutturate (bambini, animali, fumo, feste, orari di silenzio) più testo libero; orari di
arrivo e partenza; politica di cancellazione per canale (`cancellationPolicies`, `cancellation`);
politiche di pagamento e caparra (lette da `policies`); prenotazione immediata o su richiesta;
anticipo minimo. Le regole si scrivono nei campi strutturati (`reservation`) e in `houseRules`,
mai con gli aiuti di testo `addTextToHouseRules` e `addTextToField` (§10.1).

#### LST-12 — Stato della pubblicazione e completezza per canale · P0

1. Per ogni immobile un riquadro semplice: per canale «Pubblicato», «In attesa», «Da
   correggere» (con «Correggi» che apre il passo giusto del wizard), stato di approvazione Airbnb
   (`airbnbApprovalStatus`).
2. Completezza per canale dai campi richiesti dal portale (`meta/content`) e dai controlli di
   AI-03.
3. Un errore di pubblicazione crea una voce in «Da fare» (UX-07).

### 6.2 Check-in e adempimenti (CHK)

**Octorate.** Ospiti del check-in in scrittura per prenotazione con tutti i campi richiesti da
Alloggiati e dai flussi statistici, tipi di documento e città con i codici della Polizia, `draft`
per rivedere prima dell'invio, stato check-in, check-out e no show, esito dell'invio per ospite
in lettura (`policeSentStatus`, `policeSentTime`), configurazione della Polizia nella struttura
(`policeAccount`), importo ed esenzione della tassa per ospite. Non c'è: un'operazione per avviare
l'invio, le ricevute, le impostazioni e gli esiti ISTAT, la configurazione delle regole della
tassa (§9.2, §10.2).

**CasaZen oggi (§4).** Reali: check-in online dell'ospite con tutti i campi Alloggiati e i codici
ufficiali (solo IT ed EN, la pagina si apre in italiano senza scelta della lingua), stato di
arrivo e partenza con wizard di check-out, Alloggiati **manuale** (file da 168 caratteri, «segna
come inviato», avvisi orari di scadenza), motore della tassa di soggiorno con tariffe con fonte
(ma solo 16 comuni, senza esenzioni per residenti e simili), report della tassa per comune e mese.
**Segnaposto della fase 1** (§4.5): lettura del documento (MRZ e OCR restituiscono sempre nulla),
selfie e confronto (esito fisso), coda di verifica dell'operatore (permesso non assegnato a nessun
ruolo), rilascio del codice d'accesso (mai chiamato), client del web service Alloggiati e job di
invio e ricevuta (non registrati; righe costruite dal solo prenotante; analisi dei gap già in
`docs/requirements/AW-01-alloggiati-web-completamento-helix.md`), file del Ticino e OTR (formato
semplificato, pagina non raggiungibile). ISTAT e flussi regionali assenti per decisione, con
l'analisi di nove famiglie di portali in `docs/runbooks/istat-flussi-regionali.md`. Nessuna
ricevuta archiviata.

#### CHK-01 — Raccolta dei dati e dei documenti degli ospiti · P0

1. Si riusa il check-in online esistente (pagina dell'ospite con link firmato, ospiti del
   soggiorno con i codici ufficiali, consensi: §4) e lo si completa con i campi che Octorate e la
   regione richiedono: per ogni ospite tipo
   (`accommodatedType`), documento con codice della Polizia, luogo e data di rilascio,
   cittadinanza, residenza, e per i flussi statistici i campi che la regione chiede (tipo di
   turismo, mezzo di trasporto, istruzione, canale di prenotazione) solo dove obbligatori.
2. Lato host il wizard «Check-in degli ospiti» per prenotazione: ospiti → documenti → controlli
   → tassa (CHK-07) → invio (CHK-02) → esito. Dati mancanti e documenti scaduti si correggono nei
   sotto-passi (UX-02), mai rimandando a un'altra pagina.
3. Promemoria automatici all'ospite per i dati mancanti prima dell'arrivo (AI-05, routine Q1-26),
   come messaggi transazionali.
4. La pagina dell'ospite si apre nella lingua dell'ospite (prenotazione, browser) con la scelta
   visibile della lingua; almeno IT, EN, DE, FR, ES. Anche l'ospite è un utente inesperto: gli
   stessi principi di §5 valgono per il suo percorso (un passo per volta, salvataggio, ripresa
   dal link).
5. Lettura del documento: la foto del documento precompila i campi solo quando la lettura MRZ è
   reale (oggi restituisce sempre nulla, §4.5); fino ad allora inserimento manuale guidato. Selfie
   e confronto del volto restano nel perimetro di Q1-11 e non sono richiesti da questo documento;
   gli endpoint pubblici che oggi danno esiti fissi si spengono (DEB-01).

Prove: nessun documento o dato Alloggiati alimenta CRM o segmenti (regola esistente Q1-05);
isolamento per organizzazione.

#### CHK-02 — Ospiti inviati a Octorate · P0 (Mock), P1 (live)

1. Quando i dati di un soggiorno sono completi e confermati dall'host (o dall'operatore, Q1-12),
   CasaZen crea o aggiorna gli ospiti in Octorate (`POST /rest/v2/checkin/...`,
   `PUT /rest/v1/checkin/...`), conserva l'identificativo dell'ospite di Octorate e invia con
   `draft` falso solo se l'immobile ha Octorate come mittente (CHK-03); altrimenti `draft` vero.
2. Una correzione dopo l'invio aggiorna Octorate e avvisa l'host che i dati già trasmessi alla
   Polizia possono richiedere una rettifica sul portale Alloggiati (guida nel sotto-passo).
3. Mai immagini del documento verso Octorate: solo i dati.

Prova: nessun ospite con `draft` falso verso Octorate se il mittente dell'immobile non è Octorate.

#### CHK-03 — Mittente unico per immobile · P0

1. Ogni immobile italiano ha un'impostazione «Chi invia i dati alla Polizia»: il channel manager
   (default per gli immobili collegati, PO-02), CasaZen (per gli immobili non collegati: richiede
   il client del web service completato secondo AW-01, DEB-08), oppure manuale (file da caricare,
   già esistente e reale). Mai due mittenti automatici: il
   cambio di mittente richiede la conferma che l'altro è spento.
2. Wizard «Attiva l'invio automatico alla Polizia»: spiega la delega dell'incaricato alla
   trasmissione, raccoglie o verifica le credenziali dove servono (modulo esistente in sola
   scrittura), controlla la configurazione della Polizia in Octorate (`policeAccount.valid`,
   `autorun`) se leggibile (D17); se va fatta sul sito del channel manager, sotto-passo guidato con
   verifica al ritorno (UX-02, punto 3). In Octorate la configurazione documentata è una pagina di
   impostazioni (credenziali, chiave del web service, numero della struttura per camera, «Test di
   connessione» → «Attivata» → invio automatico giornaliero; Appendice A): il wizard la spiega con
   le stesse parole e chiude con un passo «Prova del collegamento» che mostra «Attivo» solo a
   verifica riuscita. Se le funzioni di adempimento di Octorate richiedono il suo modulo gestionale
   a pagamento, il wizard lo dice prima di iniziare (D17).
3. Lo stesso modello vale per i flussi ISTAT (CHK-05) e per gli immobili svizzeri (notifica del
   Ticino, Q1-15).

#### CHK-04 — Esito dell'invio e ricevuta · P0 (Mock), P1 (live)

1. Dopo l'orario di invio previsto CasaZen legge per ogni ospite `policeSentStatus` e
   `policeSentTime` e mostra «Inviato alla Polizia il …», «Non ancora inviato» o «Da verificare»
   (D18). Se S7 è confermato e i campi restano vuoti, lo stato è «Affidato al channel manager»
   con la prova del punto 2 o 3.
2. **Ricevuta ufficiale dalla Polizia**: la ricevuta di Alloggiati è per giorno e per utenza (S17),
   quindi comprende anche le schedine inviate da Octorate. Se l'host affida a CasaZen le
   credenziali Alloggiati (modulo esistente, cifrate, sola scrittura), CasaZen scarica ogni giorno
   la ricevuta del giorno precedente con l'operazione `Ricevuta` del client (da collegare, DEB-08),
   **senza mai chiamare `Send`** quando il mittente è il channel manager, e la archivia per 5 anni (stato
   «Ricevuta acquisita»). Da verificare al primo invio reale che la ricevuta comprenda le
   schedine inviate da un altro software e che due software possano usare la stessa utenza [NV].
3. Senza credenziali in CasaZen: passo guidato «Recupera la ricevuta» con istruzioni brevi, il
   collegamento alla pagina giusta e il caricamento del PDF in CasaZen (trascina il file);
   promemoria il giorno dopo.
4. Una ricevuta mancante il giorno dopo l'invio, o un ospite non inviato entro la scadenza
   (24 ore dall'arrivo; subito, e al massimo entro 6 ore, per i soggiorni fino a 24 ore), crea un
   avviso urgente (UX-08).
5. Soggiorni oltre 30 notti: secondo la documentazione di Octorate non partono in automatico
   (Appendice A): CasaZen li riconosce e crea una voce «Invia a mano» con il file pronto.

Prova: con il mittente «channel manager» il client di CasaZen non esegue mai `Send`.

#### CHK-05 — Statistiche ISTAT e regionali · P1

1. Dove Octorate invia i flussi della regione (D15) e l'host l'ha configurato: CasaZen fornisce i
   dati degli ospiti (CHK-02) e mostra lo stato «Affidato al channel manager», con un promemoria
   mensile «Controlla che i dati del mese siano stati inviati» e una prova da caricare.
2. Altrove: file e promemoria di Q1-15 solo con il tracciato ufficiale acquisito (oggi nessun
   flusso regionale è implementato; base di partenza `docs/runbooks/istat-flussi-regionali.md` e
   `docs/runbooks/ross1000.md`) oppure il portale regionale con istruzioni guidate.
3. Una tabella per regione dice il sistema usato, chi invia, la scadenza e come si ottiene la
   prova; i territori non noti restano «da definire», senza ipotesi. Come fa Octorate (avviso rosso
   per una regione non supportata, Appendice A), il wizard dice subito se la regione dell'immobile
   è coperta, prima di chiedere qualunque dato.
4. Octorate manda l'esito ISTAT per email all'host (non via API): il passo «Prova del mese»
   chiede di caricare quella email o la ricevuta del portale, con promemoria.

#### CHK-06 — Check-in e check-out · P0

1. Stato per prenotazione: atteso, arrivato, partito, mancato arrivo; CasaZen lo invia a Octorate
   (`CHECKIN_END`, `CHECKOUT_END`, `NO_SHOW`) e lo legge (`effectiveCheckin`, `arrivalTime`).
2. Riusa il wizard di check-out esistente (§4) portato nel livello guidato e senza uscite
   (UX-02): partenza confermata, Alloggiati, pulizie, tassa, note.
3. Gli stati producono eventi sul bus (Q1-26): codice d'accesso solo dopo la verifica (Q1-13),
   pulizia al check-out, richiesta di recensione.

#### CHK-07 — Tassa di soggiorno · P0

1. CasaZen calcola (PO-04) con le tariffe e le regole per comune già presenti (stagioni, età,
   tetto di notti, esenzioni) per ospite e per notte; le esenzioni per età sono automatiche, le
   altre l'host le sceglie da un elenco con parole semplici, abbinato ai motivi di Octorate
   (`cityTaxExemption`).
2. CasaZen scrive per ospite `cityTaxPrice`, `cityTaxExemption` ed `excludeCityTax` quando la
   tassa l'ha incassata il canale (per alcuni comuni Airbnb la incassa e la versa: va gestito per
   canale e comune, senza far pagare due volte l'ospite).
3. Confronto con `touristTax` calcolata da Octorate: una differenza crea una voce in «Da fare»
   (D20).
4. Riepiloghi per comune e periodo per la dichiarazione e il versamento (riuso del report della
   tassa esistente, `GET api/fiscal/reports/tourist-tax`), con collegamento al portale del comune,
   scadenze in «Da fare» e archivio delle ricevute (CHK-08).
5. Copertura: oggi le tariffe ci sono per 16 comuni. Ogni immobile senza tariffa del suo comune
   crea una voce «Inserisci la tassa del tuo comune» con un sotto-passo guidato (importo, stagioni,
   esenzioni, fonte); mai importo zero presunto (regola esistente). Esenzioni per residenti e
   altre categorie e tetti annuali sono nuovi.

Prove: importi al centesimo su casi d'esempio (minore esente, tetto di notti, tassa incassata dal
canale); nessuna doppia tassa per lo stesso ospite.

#### CHK-08 — Archivio delle prove di adempimento · P0

Per prenotazione e per periodo: ricevute Alloggiati (scaricate o caricate), prove ISTAT
caricate, dichiarazioni e versamenti della tassa, registro di cosa è stato inviato, quando e da
chi (CasaZen o channel manager), esportazione in un archivio compresso per un controllo. Si
collega a REP-04 e REP-05.

#### CHK-09 — Scadenze degli adempimenti in «Da fare» · P0

Alloggiati (24 ore dall'arrivo), flussi regionali (giornalieri o mensili secondo la regione),
tassa di soggiorno (scadenze del comune), dati mancanti prima dell'arrivo, ricevute da
recuperare: tutte con avvisi secondo UX-08 e «Fai ora» che apre il wizard giusto.

#### CHK-10 — Immobili non collegati e Svizzera · P0

Gli immobili senza Octorate continuano con i flussi di CasaZen (Alloggiati manuale oggi,
automatico dopo DEB-08; Ticino e OTR di Q1-15 dopo la correzione del formato e degli stati
inclusi) dentro gli stessi wizard; l'utente vede la stessa interfaccia e lo stesso stato, cambia
solo il mittente.

#### CHK-11 — Identificazione dell'ospite · P1 ⚖

1. Le fonti raccolte il 31/10 (Appendice A) riportano che il Consiglio di Stato, il 21 novembre
   2025, ha confermato l'obbligo di identificare l'ospite di persona; una verifica a distanza **dal
   vivo** (videochiamata, verifica in tempo reale) può bastare, una foto del documento con un
   codice d'accesso automatico no. Le fonti non concordano sul numero della sentenza: la verifica
   del legale precede qualunque testo verso gli utenti.
2. Per ogni ospite adulto CasaZen registra **come** è avvenuta l'identificazione (di persona,
   videochiamata, verifica biometrica dal vivo quando sarà reale: Q1-11), chi l'ha fatta e quando;
   il wizard di arrivo ha un passo «Riconosci gli ospiti» con una casella per ospite.
3. Il codice d'accesso si rilascia solo a identificazione registrata (Q1-13, DEB-09); il self
   check-in senza un passo dal vivo è segnalato all'host come non conforme secondo PO-18.

Prova: nessun rilascio del codice senza identificazione registrata per ogni ospite adulto.

### 6.3 Marketplace e integrazioni (MKT)

**Octorate.** Via API si leggono i portali con categoria `OTA` o `EXTERNAL_TOOL`, requisiti di
collegamento e funzioni; le connessioni si creano, modificano, disattivano ed elencano; i moduli
della licenza si leggono (scrittura da confermare, D21). **Non esiste un elenco delle app dei
partner del marketplace Octorate né un modo di attivarle via API** (D16): i partner terzi si
collegano a Octorate con il proprio accesso e il consenso del proprietario (§9.2).

**CasaZen oggi (§4).** Marketplace dei fornitori completo (richieste, preventivi, riserve,
commissione; pagamenti online e vetrina dietro flag) con la cornice a schede «Scopri»,
«Categorie», «Le mie app» (che oggi elenca i fornitori usati, non app); pagina «Canali» con lo
stato di ogni calendario iCal; collegamento guidato a Stripe; integrazione del canone concordato
dietro flag. Non esistono un catalogo di integrazioni, un modello generale di connessione, chiavi
API o connessioni OAuth verso terzi.

#### MKT-01 — Un solo catalogo «Marketplace» · P0

1. Nella cornice esistente, cinque gruppi: **Canali di vendita** (portali `OTA` letti via API),
   **Strumenti collegati** (portali `EXTERNAL_TOOL`), **Funzioni del channel manager** (moduli
   della licenza), **Servizi per la casa** (marketplace dei fornitori esistente), **Partner**
   (catalogo curato dall'admin di CasaZen, PO-11, ordinato per lavoro da fare — vendere, accedere
   all'immobile, adempimenti, pulizie, prezzi, amministrazione — prendendo come riferimento le
   categorie del «Partner Hub» pubblico di Octorate, Appendice A).
2. Ogni scheda: nome, logo, beneficio in una riga, costo se noto e chi lo fattura, stato per
   l'organizzazione o l'immobile (Attivo, Non attivo, In attesa, Da sistemare).
3. «Le mie app» diventa «Attivi»: integrazioni e fornitori in uso insieme.
4. Fonti: portali letti da Octorate (cache giornaliera), catalogo dell'admin, fornitori. Nessun
   nome di partner scritto nel codice.

#### MKT-02 — Scheda di dettaglio · P0

Cosa fa, per chi, cosa serve per attivarlo (dai flag del portale: identificativo della struttura
sul canale, utente e password, accesso con consenso; oppure dal catalogo), costi e chi li
fattura, quali dati vengono condivisi (privacy), come si disattiva, domande frequenti.

#### MKT-03 — Attivazione guidata · P0 (Mock), P1 (live)

Wizard nel livello guidato, diverso per tipo:

- **Canale o strumento via API:** immobili → credenziali o accesso al canale (se
  `requiresTokenAuthentication`, l'utente completa l'accesso sul sito indicato: UX-02 punto 3) →
  connessione (`POST /rest/v1/connection/{acc}`) → import e abbinamento (LST-02) → attivazione
  (`MARK_CONNECTED`) → esito.
- **Funzione del channel manager:** richiesta guidata (PO-12, D21), stato «Richiesto» finché
  l'attivazione non risulta dalla licenza.
- **Partner senza API:** passaggi guidati con lista di controllo, «Ho fatto» e verifica dove
  possibile (connessioni, licenza); altrimenti stato dichiarato dall'utente con la data.
- **Servizi per la casa:** flusso esistente, invariato.

#### MKT-04 — Stato delle integrazioni attive · P0

Per immobile e canale: stato della connessione (`GET /rest/v1/connection`), ultima
sincronizzazione, errori in parole semplici, avvisi di quota; è il riquadro di salute di OCT-14
visto dall'utente. Ogni problema crea una voce in «Da fare».

#### MKT-05 — Modifica e disattivazione · P0

Modifica delle impostazioni (`PATCH` della connessione); disattivazione (`DELETE`) solo dopo una
spiegazione dell'effetto («le nuove prenotazioni di Booking.com non arriveranno più in CasaZen;
l'annuncio su Booking.com resta pubblicato finché non lo chiudi») e una conferma; richiesta di
cambio per le funzioni in licenza; disattivazione guidata per i partner senza API.

#### MKT-06 — Gestione del catalogo · P1

L'admin di piattaforma crea, ordina e nasconde le schede del gruppo «Partner», con testi IT/EN,
visibilità per piano e paese, collegamento a un portale Octorate quando esiste.

#### MKT-07 — Il marketplace dentro i processi · P1

Suggerimenti nel punto in cui servono, sempre come sotto-passo (UX-02) e mai come pubblicità: in
«Collega i tuoi canali» («Vuoi pubblicare anche su Expedia?»), nel check-out («Serve una
pulizia?» con i fornitori), negli adempimenti (strumenti collegati per il check-in).

### 6.4 Messaggi (MSG)

**Octorate.** Chat via API per Airbnb, Booking.com, Vrbo ed Expedia (S7): thread, messaggi,
modelli, non letti, lettura di tutti, traduzione, archivio; invio verso le OTA con il processore
`CHANNEL` e `OTA_FLOW_DIRECTION` = `TO`; webhook `CHAT_MESSAGE_RECEIVED`; assistente AI di Octorate
per thread (da tenere spento); allegati, offerte speciali e pre-approvazione con supporto per OTA
da confermare (D3).

**CasaZen oggi (§4).** Thread ospite–host per le sole prenotazioni dirette (oscuramento di carte e
IBAN, chiusura 7 giorni dopo il check-out, idempotenza, limiti, non letti); elenco unico delle
conversazioni già pronto sul backend (`GET api/messages`, filtri «da rispondere» e immobile) ma
**nessuna pagina inbox**; per ogni messaggio email, push e voce nella campanella web; push mobile
senza schermata dei messaggi e senza rotta dedicata; email degli avvisi solo all'indirizzo
dell'organizzazione; **nessuna preferenza di notifica per utente**; canali WhatsApp e SMS solo
come valori di un enum; `NotificationRouter` registrato ma non usato. AI: infrastruttura completa
(`IAiProvider`, Stub per default, budget, cache, `PiiScrubber`), **nessuna bozza o risposta AI ai
messaggi**; coda di approvazione parziale (approvare non esegue l'azione, la notifica non parte).
Modello riusabile per la confidenza: soglie per organizzazione con accetta e passa all'operatore
di `BiometricSettings`.

#### MSG-01 — Hub dei messaggi · P0

1. Nuova voce «Messaggi» su web e mobile: tutte le conversazioni, dalle OTA via Octorate e dai
   thread diretti esistenti (più avanti email e WhatsApp), in un solo elenco. Vista predefinita
   «Da rispondere»; filtri per immobile, canale, prenotazione, non lette; ricerca.
2. La conversazione mostra accanto la scheda della prenotazione (date, ospiti, stato, importi) e
   del cliente (CRM-01).
3. Si estende `GET api/messages` con la fonte OTA; un solo modello di thread per tutte le fonti.

#### MSG-02 — Storico e sincronizzazione · P0

Import iniziale di thread e messaggi per immobile, poi aggiornamento con `CHAT_MESSAGE_RECEIVED`,
`createdAfter`, `recentMessages` e riconciliazione (OCT-09, OCT-10); conservazione della copia in
CasaZen decisa in ADR; allegati dove il canale li supporta.

#### MSG-03 — Rispondere e scrivere · P0

1. Risposta nel thread: verso le OTA tramite Octorate, verso i diretti con il servizio esistente.
2. Nuovo messaggio all'ospite di una prenotazione: l'hub sceglie il canale (thread OTA se esiste,
   altrimenti thread diretto o email).
3. Modelli di CasaZen con variabili sostituite in CasaZen; modelli di Octorate in lettura;
   traduzione (AI-06 o traduzione di Octorate); offerte speciali e pre-approvazione solo dove
   confermate (D3).
4. Stato del messaggio (inviato, errore, da verificare); un secondo clic o un nuovo tentativo non
   duplica il messaggio (OCT-10).

#### MSG-04 — Associazione a prenotazione e cliente · P0

Thread e prenotazione si collegano con `reservation` e `threadId`; un thread senza prenotazione
(richiesta prima della prenotazione) appare come «Richiesta», con le azioni consentite dal canale;
ogni thread porta al profilo del cliente (CRM-01).

#### MSG-05 — Preferenze di notifica per utente · P0

1. Per utente, per tipo di evento (nuovo messaggio, messaggio urgente, bozza AI da approvare,
   nuova prenotazione, cancellazione, scadenza di legge, errore di sincronizzazione…) e per canale
   (push mobile, push del browser, email, solo contatore, nessuna).
2. Default PO-10: nuovo messaggio dell'ospite con push e contatore. Ore di silenzio; perimetro per
   immobili (il collaboratore riceve solo i suoi).
3. Un solo punto decide chi riceve cosa, sul modello di `SupplierNotificationPreferences` con il
   suo test di architettura; le email vanno alle persone, non solo all'indirizzo
   dell'organizzazione.
4. Il push dei messaggi apre la conversazione su mobile (nuova rotta in `PushRoutes` e
   `NOTIFICATION_ROUTES`).
5. Le stesse preferenze governano gli avvisi di UX-08.

#### MSG-06 — Standard di comunicazione dell'assistente · P1

1. **Base di conoscenza per immobile**, costruita da sola con i dati dell'annuncio (istruzioni di
   arrivo, Wi-Fi, parcheggio, regole, orari, dotazioni) più domande frequenti libere.
2. **Regole dell'organizzazione**: tono, lingue, firma, orari, argomenti ammessi e vietati.
3. **Situazioni riconosciute**, ciascuna con risposta tipo o fonte e azione (invio automatico,
   bozza, passa all'operatore): orario di arrivo, arrivo anticipato e partenza posticipata,
   parcheggio, Wi-Fi, indicazioni, animali, richiesta di fattura, ringraziamento, guasto, rumore,
   richiesta di sconto, cancellazione o rimborso, reclamo, emergenza.
4. **Ereditarietà**: impostazioni dell'organizzazione con eccezioni per immobile.
5. Wizard «Insegna all'assistente» nel livello guidato: situazioni → risposte proposte dai dati
   dell'immobile → automatico o con conferma → prova su messaggi d'esempio (simulatore senza
   invio) → attivazione.
6. **Lezioni**: quando l'utente corregge o rifiuta una bozza, l'assistente propone il fatto o la
   regola da imparare («il parcheggio è gratuito solo nel weekend») e l'utente la approva con un
   ambito: questo immobile, tutti gli immobili, l'organizzazione (modello già usato dal mercato,
   Appendice A). Nessuna lezione entra senza approvazione.

#### MSG-07 — Ranking di confidenza e invio · P1

1. Per ogni messaggio in arrivo: situazioni e urgenza, fatti necessari recuperati, bozza, punteggio
   di confidenza (corrispondenza della situazione, copertura dei fatti, controllo delle regole);
   tutto registrato (modello, versione, versione delle istruzioni, fonti, punteggio, decisione,
   chi ha approvato).
2. **Invio automatico solo se tutte le condizioni sono vere:** situazione impostata come
   automatica per quell'immobile; punteggio sopra la soglia (dell'organizzazione o dell'immobile,
   default 0,85, PO-08); canale che ammette risposte automatiche (D4); categoria non sensibile
   (denaro, cancellazione o rimborso, reclamo, danni, sicurezza o emergenza, questioni legali,
   richieste sui dati personali); nessuna regola del canale violata (su Airbnb niente contatti né
   link prima della prenotazione; niente cambi di data Booking.com via chat; OCT-11).
3. Altrimenti bozza nella coda di approvazione con avviso «Bozza pronta» e azioni rapide
   «Approva» e «Modifica» anche dalla push.
4. Tre livelli di fiducia per situazione: **bozza** (default, PO-08: periodo iniziale con la misura
   «approvate senza modifiche»), **automatico** (sopra soglia), **automatico con eccezioni**
   (le eccezioni vanno all'operatore con urgenza e scadenza). Il passaggio di livello lo propone
   CasaZen e lo decide l'utente.
5. Interruttore per organizzazione, immobile e situazione; gli assistenti AI di Octorate (per
   thread e l'agente dei messaggi) restano spenti sui thread gestiti da CasaZen, verificato a ogni
   sincronizzazione (OCT-11).
6. Ogni messaggio inviato mostra all'host se l'ha scritto una persona o l'assistente, e quale
   regola l'ha fatto partire.
7. Trasparenza (AI Act art. 50) nell'interfaccia; dicitura verso l'ospite secondo la decisione del
   PO (D-11 della fase 1, ⚖). Dati al modello secondo PO-09.

Prove: un messaggio di una categoria sensibile non parte mai in automatico; sotto la soglia non
parte nulla senza approvazione.

#### MSG-08 — Coda di approvazione funzionante · P0

Completa Q1-06, oggi parziale: approvare esegue l'azione d'origine (invia il messaggio, programma
la campagna); modifica prima dell'approvazione; motivo del rifiuto salvato; avviso alla comparsa
di un elemento (oggi il metodo non è mai chiamato); permesso dedicato; approvazione dal mobile.

#### MSG-09 — Recensioni nell'hub · P1

OCT-12 invariato: lettura per prenotazione; risposta e recensione dell'host solo per Airbnb;
filtro «Recensioni» nell'hub; bozze di risposta con AI-06 e approvazione.

#### MSG-10 — Canali aggiuntivi · P2

WhatsApp e SMS restano fuori finché non c'è il fornitore (oggi esistono solo come valori di un
enum); l'email come canale per gli ospiti diretti segue MSG-03.

#### MSG-11 — Messaggi programmati visibili per prenotazione · P1

Ogni prenotazione mostra la linea dei messaggi che partiranno da soli (dalle routine: istruzioni
prima dell'arrivo, link del check-in, ringraziamento), con «Modifica», «Salta» e «Invia ora»; un
messaggio con un dato mancante (per esempio il codice d'accesso non ancora rilasciabile) non parte
e crea una voce in «Da fare»; l'host è avvisato quando un messaggio è saltato o inviato fuori
orario (modello dei messaggi programmati delle OTA, Appendice A). Niente è programmato per default
finché l'utente non attiva una routine.

### 6.5 Prenotazioni e calendario (BKG)

**Octorate.** Elenco con filtri per la riconciliazione, creazione singola e in blocco, modifica,
cancellazione, divisione; pagamenti sulla prenotazione; prezzo per componenti (soggiorno, pulizia,
extra, sconto, IVA, **commissione**, tassa di soggiorno), `paymentStatus`, `totalPaid`, stato,
chi ha cancellato e quando, `otaId`, `threadId`; webhook `RESERVATION_*`; controllo di
disponibilità; contatti dell'ospite solo con permesso speciale (§10).

**CasaZen oggi (§4).** Prenotazione completa per le dirette (Stripe) e le manuali; soggiorni OTA
creati a mano dai blocchi iCal con importi digitati; ciclo di vita, cancellazione con anteprima,
rimborsi; calendario di tutti gli immobili in sola lettura; schema dei webhook Stripe riusabile.
Mancano: commissione OTA, netto, importo pagato memorizzato, valuta sulla prenotazione, chiave
univoca per le prenotazioni esterne, il bus di eventi con outbox dell'ADR-022 (accettato ma non
costruito; il dispatcher delle routine non è mai chiamato).

#### BKG-01 — Import e sincronizzazione delle prenotazioni · P0

1. Tutte le prenotazioni degli immobili collegati entrano in CasaZen con i dettagli: date (da UTC
   al fuso dell'immobile), adulti, bambini e neonati, stato, canale (`otaId`, `channelRefer`),
   componenti di prezzo, pagamenti, `paymentStatus`, `totalPaid`, cancellazione, `threadId`,
   note (OCT-07).
2. Chiave univoca per tenant, ambiente e identificativo Octorate, con ordine per `updateTime`;
   un duplicato o un evento fuori ordine non crea una seconda prenotazione né effetti doppi.
3. La prenotazione si estende con commissione del canale, netto, importo pagato, saldo, valuta e
   l'origine di ogni importo (Octorate, Stripe, manuale): per gli immobili collegati risponde
   alla domanda bloccante D-03 della fase 1 (da dove arrivano gli importi delle OTA per i
   rendiconti dei proprietari).
4. Il soggiorno OTA creato da un blocco iCal resta per gli immobili non collegati; per quelli
   collegati l'import iCal di un canale si spegne al passaggio al connettore, senza doppioni.

Prove: un evento duplicato non duplica la prenotazione; isolamento per organizzazione.

#### BKG-02 — Aggiornamenti via webhook, riconciliazione e bus di eventi · P0

1. Si riprendono OCT-08 e OCT-09 e si **costruisce il bus di eventi con outbox dell'ADR-022**
   (fondazione H1): webhook persistito prima dell'ACK → rilettura via API → aggiornamento →
   eventi «prenotazione creata, confermata, modificata, cancellata» sul bus nella stessa
   transazione → routine (Q1-26), «Da fare», messaggi, report.
2. Riconciliazione periodica con finestre sovrapposte; il ritardo della sincronizzazione è
   visibile nello stato dell'immobile (UX-09).

#### BKG-03 — Wizard «Nuova prenotazione» · P0

Passi: immobile e date con disponibilità verificata (CasaZen e `availabilityCheck`) → ospiti →
prezzo proposto dalle regole di CasaZen, modificabile con nota → pagamento (link di pagamento,
pagamento in struttura, bonifico) → riepilogo → creazione → esito.

1. Si estende il wizard della prenotazione manuale esistente, portato nel livello guidato.
2. La prenotazione nasce in CasaZen e in Octorate (`POST /rest/v1/reservation/{acc}`), che toglie
   la disponibilità su tutti i canali; le prenotazioni dirette del sito seguono lo stesso percorso
   senza wizard.
3. L'incasso si registra in Octorate quando avviene (`/payment`, «non è l'addebito della
   carta», D9); i campi di addebito automatico di Octorate non si usano.

#### BKG-04 — Modifica · P0

1. Dirette e manuali: date, ospiti, prezzo e note si cambiano in CasaZen e poi in Octorate
   (PUT/PATCH); un 409 rilegge e mostra lo stato aggiornato. Oggi si modificano solo le manuali
   non iniziate: la regola si estende alle dirette con ricalcolo, addebito o rimborso Stripe.
2. OTA: solo ciò che il canale consente; per il resto il wizard spiega in una frase come
   chiederlo all'ospite sul canale (Booking.com: cambio date dall'extranet o dall'ospite). Mai
   un successo finto.

#### BKG-05 — Cancellazione · P0

Dirette: anteprima della politica (esistente), rimborso Stripe (esistente), cancellazione in
Octorate (PUT con stato `CANCELLED` o DELETE; `PATCH` non consente da confermata a cancellata).
OTA: si mostrano cancellate quando lo sono sul canale; una cancellazione chiesta da CasaZen solo
dove il canale lo consente (D8).

#### BKG-06 — Importi, pagamenti e stato · P0

Per prenotazione, in parole semplici: totale, commissione, netto, pagato, saldo, stato del
pagamento, chi incassa (`companyCollect`, `commissionIncludedInPayments`), elenco dei pagamenti e
rimborsi. Dirette: Stripe è la fonte; OTA: Octorate. Stesso vocabolario di UX-09.

#### BKG-07 — Calendario unico · P0

1. Il calendario di tutti gli immobili (oggi in sola lettura) diventa lo spazio di lavoro:
   prenotazioni di tutti i canali con l'icona del canale, blocchi, prezzo e soggiorno minimo per
   giorno.
2. Selezionare dei giorni apre un pannello per bloccare, cambiare il prezzo o le restrizioni
   (LST-10); le modifiche in blocco passano dal livello guidato con riepilogo. Il trascinamento
   non è richiesto.
3. Sul mobile lettura e blocco dei giorni.

#### BKG-08 — Eventi e avvisi · P0

Gli eventi delle prenotazioni alimentano: link di check-in all'ospite (esistente), attività di
pulizia, messaggi delle routine, «Da fare» («nuova prenotazione con richiesta particolare»),
avvisi secondo le preferenze (MSG-05; push per una nuova prenotazione attivo per default).

#### BKG-09 — Protezione da sovrapposizioni e anomalie · P1

Controllo della disponibilità prima di ogni creazione diretta; una prenotazione che arriva su
notti già occupate (per esempio durante il passaggio da iCal) crea un avviso urgente in «Da
fare»; anomalie di AI-07.

### 6.6 Fatturazione e documenti fiscali (FIS)

**Octorate.** Lettura dei documenti (fatture, ricevute fiscali e generiche, note di credito,
proforma) con stato SDI se l'integrazione «treasury» è attiva, PDF di riepilogo per prenotazione,
pagamenti e incassi collegati; impostazioni di fatturazione della struttura e scomposizione della
commissione del property manager (`splitCommissionPropertyManager`, fattura a doppia intestazione).
La **creazione** via API è in contrasto tra S13 (esistono due operazioni) e S7 (non esiste): D19.
Modulo `invoice` e stampante fiscale nella licenza.

**CasaZen oggi (§4).** Pagamenti e rimborsi completi (senza commissione Stripe, accredito e
commissione OTA); report fiscali annuali completi (redditi, cedolare stimata, ritenute subite,
tassa per comune). **Nessuna ricevuta o fattura all'ospite.** Fatture di CasaZen alle
organizzazioni solo lato server, con intermediario SDI non configurato. Fatture dell'agenzia al
proprietario (FatturaPA) allo stato di prototipo: numerazione corretta, ma IVA e regime fissi,
XML non validato con dati segnaposto, «PDF» che non è un PDF, nessuna interfaccia. Dati fiscali
dell'organizzazione incompleti per un emittente (sede su una riga, nessun regime fiscale né codice
destinatario dell'emittente).

#### FIS-01 — Dati di fatturazione della prenotazione · P0

Per ogni prenotazione: a chi intestare (ospite o azienda: nome, codice fiscale o partita IVA,
indirizzo, codice destinatario o PEC), presi da Octorate e dal check-in («Ti serve la fattura?»,
facoltativo) e modificabili in un sotto-passo con controlli di formato (codice fiscale, partita
IVA, codice destinatario).

#### FIS-02 — Emittente unico e documento giusto · P0

1. Wizard «Come emetti i documenti?» per l'organizzazione: chi emette (CasaZen, il channel
   manager se già lo usa, nessuno perché se ne occupa il commercialista: PO-05) e quale documento
   per il proprio regime (privato senza partita IVA, partita IVA ordinaria o forfettaria, agenzia
   con doppia intestazione proprietario e agenzia), con domande semplici e un esempio del
   documento. Le regole (tipo di documento, IVA, bollo, natura delle operazioni) sono validate da
   un commercialista prima del rilascio (⚖) e stanno in configurazione, non nel codice sparso.
2. Mai due emittenti per la stessa organizzazione: se emette il channel manager, CasaZen legge
   (FIS-04) e non emette.

#### FIS-03 — Emissione da CasaZen · P1

1. Bozza al check-out o su richiesta (AI-08), emissione con conferma; numerazione senza buchi per
   emittente e sezionale (si riusa quella di `AgencyInvoiceService`); note di credito; bollo dove
   dovuto; invio all'ospite per email.
2. XML FatturaPA corretto (caratteri speciali, dati reali al posto dei segnaposto, validazione con
   lo schema ufficiale) e PDF vero con il generatore esistente (MigraDoc).
3. Invio allo SDI tramite l'intermediario scelto (PO-06) con gli stati inviata, consegnata,
   scartata; la porta `ISdiEInvoiceProvider` si generalizza da CasaZen a qualunque emittente
   (emenda ADR-019).

Prove: totali, IVA e bollo al centesimo su casi d'esempio; numerazione senza buchi né doppioni.

#### FIS-04 — Documenti emessi dal channel manager · P1

Se l'organizzazione usa la fatturazione di Octorate: import di tutti i documenti con tipo, numero,
data, importi, stato del pagamento e stato SDI (`fiscalInvoiceGroup`), PDF, collegati alle
prenotazioni; sola lettura in CasaZen; «scartato dallo SDI» e «da incassare» diventano voci di
«Da fare» con il motivo in parole semplici.

#### FIS-05 — Pagamenti della prenotazione · P0

Un solo elenco per prenotazione e periodo: pagamenti diretti (Stripe, con commissione e accredito,
oggi mancanti), pagamenti delle OTA (Octorate), pagamenti manuali (contanti, bonifico: servizio
esistente); stato pagato o da incassare collegato ai documenti.

#### FIS-06 — Scarti e correzioni · P1

Uno scarto dello SDI apre una voce «Correggi la fattura» con il motivo spiegato e un sotto-passo
per correggere e reinviare (nota di credito dove serve).

#### FIS-07 — Formati utilizzabili · P0

PDF veri, XML FatturaPA, CSV ed Excel per gli elenchi; pacchetto mensile per il commercialista
(REP-06).

### 6.7 Clienti e marketing (CRM)

**Octorate.** Rubrica degli ospiti (elenco, dettaglio, inserimento, modifica, suggerimenti); email,
telefono e PEC solo con permesso speciale, ogni lettura registrata, e con gli IP statici in
whitelist (S7); nessun campo di consenso. Per le prenotazioni Booking.com l'email è un indirizzo
di inoltro; Airbnb non fornisce l'email dell'ospite.

**CasaZen oggi (§4).** Anagrafica degli ospiti completa (una email, un solo consenso al marketing
sì/no dato dal checkout diretto); consenso per canale raccolto nel check-in, con email di
marketing separata, ma **non letto da nessuno**; profilo CRM e abbinamento (ADR-021) presenti ma
**mai popolati** (il servizio non è registrato); doppia conferma senza invio della email; lista
di soppressione parziale; segmenti con regole che non selezionano mai nessuno; campagne senza un
percorso di approvazione funzionante, con link di disiscrizione non firmato e l'email
nell'indirizzo; pagine di campagne e segmenti non raggiungibili; esportazione GDPR senza consensi,
profilo CRM e messaggi.

**Cosa si può fare e cosa no (indicazione del PO: «non bloccarsi»).** L'obiettivo è raggiungere
più clienti possibile **senza esporre l'host o l'agenzia** (titolari del trattamento) a sanzioni
del Garante o alla sospensione dell'account sulle OTA, e senza esporre CasaZen, che è responsabile
del trattamento. Quindi:

- **Si fa, sempre:** messaggi di servizio legati al soggiorno (informazioni prima dell'arrivo,
  link del check-in, promemoria della prenotazione, ringraziamento dopo la partenza senza
  promozioni), via chat della OTA o email.
- **Si fa, senza consenso esplicito:** email ai clienti che hanno comprato un soggiorno e hanno
  dato l'email a CasaZen (prenotazione diretta, check-in), per soggiorni analoghi, con
  informativa al momento della raccolta e disiscrizione in ogni messaggio («soft opt-in»,
  art. 130 c. 4 Codice privacy): PO-07, da confermare con il legale.
- **Si fa, con consenso:** WhatsApp, SMS, offerte a chi non ha comprato, pubblici pubblicitari.
  Il consenso si chiede nei momenti che CasaZen controlla: check-in (esiste), pagina dell'ospite,
  pagina di benvenuto con QR nell'appartamento, checkout del sito diretto; con un incentivo
  (codice sconto per il ritorno).
- **Si fa, con gli strumenti del canale:** promozioni e offerte agli ospiti delle OTA dentro le
  OTA (offerte speciali Airbnb, promozioni Booking.com), dove le regole del canale lo consentono.
- **Si fa, senza dati personali:** biglietto o QR nell'appartamento con un codice per il sito
  diretto.
- **Non si fa e CasaZen non lo offre:** usare per il marketing l'indirizzo di inoltro di
  Booking.com (il contratto lo vieta) o i dati Airbnb senza consenso separato; leggere in blocco i
  contatti da Octorate per il marketing (permesso speciale, ogni lettura registrata); scrivere a
  chi si è disiscritto; consensi preselezionati o obbligatori.

#### CRM-01 — Database clienti unico · P0

1. Profili da tutte le prenotazioni (Octorate: prenotante e ospiti del check-in; dirette;
   manuali), con abbinamento secondo ADR-021 **e il servizio di abbinamento finalmente registrato**
   (oggi non lo è); l'abbinamento non si basa solo sull'email (gli indirizzi delle OTA sono di
   inoltro): anche telefono, nome normalizzato, data di nascita e soggiorni, con unione manuale
   proposta.
2. Storico: soggiorni, spesa, canali, immobili, composizione del gruppo, anticipo della
   prenotazione, lingua, paese, recensioni; etichette; ricerca e filtri nel perimetro
   dell'utente.
3. Mai dati Alloggiati o documenti nel profilo (regola Q1-05).

#### CRM-02 — Recapiti e base giuridica · P0

Ogni recapito (email, telefono o WhatsApp) ha: origine (indirizzo di inoltro OTA, check-in,
prenotazione diretta, inserimento manuale), conferma (doppio opt-in), base giuridica per canale
(consenso, soft opt-in, solo servizio, nessuna), soppressione. Gli indirizzi di inoltro delle OTA
si riconoscono e sono «solo servizio».

#### CRM-03 — Raccolta del consenso nei momenti giusti · P0

Check-in (esiste, per canale, minori esclusi), pagina dell'ospite, pagina di benvenuto con QR,
checkout del sito diretto (oggi un solo sì/no: passa ai consensi per canale); la email di doppia
conferma parte davvero (oggi nessuno la invia); informativa del soft opt-in per i clienti diretti
(PO-07).

#### CRM-04 — Segmenti · P1

Costruttore di condizioni funzionante (oggi ogni regola è falsa) sui campi del CRM (catalogo di
Q1-07) più i campi che arrivano da Octorate (canale, paese, soggiorni, spesa, anticipo, tipo di
gruppo, ultimo soggiorno, immobile); conteggio in tempo reale; perimetro dell'utente.

#### CRM-05 — Campagne e promemoria · P1

Wizard «Nuova campagna» nel livello guidato: obiettivo → pubblico → messaggio (AI-09) → offerta
(codice sconto del sito diretto) → anteprima e prova → approvazione (MSG-08) → programmazione →
risultati (inviati, errori, disiscrizioni, prenotazioni dirette attribuite con i parametri UTM).
Promemoria automatici con le routine: prima dell'arrivo e ringraziamento (servizio), offerta di
ritorno (solo con base giuridica).

#### CRM-06 — Pubblico raggiungibile, mai un vicolo cieco · P0

Ogni campagna mostra quante persone si possono raggiungere per canale e base giuridica, quante no e
perché, e le azioni per allargare il pubblico (consenso nel check-in attivo, QR di benvenuto,
promozioni sui canali). Nessun invio a contatti senza base giuridica, verificato al momento
dell'invio.

Prova: la lista dei destinatari esclude i contatti senza base giuridica, non confermati o in
soppressione al momento dell'invio.

#### CRM-07 — Contatti presi da Octorate · P0

`contactInfo` solo per finalità di servizio (messaggi, check-in, fattura), registrato; mai
esportazioni in blocco; prerequisito degli IP statici (S7); PO-17.

#### CRM-08 — Correzioni di conformità delle campagne · P0 (prima di qualunque invio reale)

Link di disiscrizione firmato senza l'email nell'indirizzo (link firmati esistenti, ADR-007),
intestazioni `List-Unsubscribe` e `List-Unsubscribe-Post`, impronta con chiave per la soppressione
(come `ServiceCustomers.EmailHash`), destinatari dai consensi per canale e dal soft opt-in (non dal
sì/no unico), nessun indirizzo email nei log, una revoca registrata dall'host entra in
soppressione, esportazione e cancellazione GDPR estese a consensi, profilo CRM e messaggi; voci di
menu dei moduli non finiti dietro un flag (Q1-29).

### 6.8 Report, contabilità e documenti (REP)

**Octorate.** Nessuna API di report (S7, S13). I dati vengono da prenotazioni (componenti di
prezzo con commissione e tassa), pagamenti, documenti fiscali, ospiti (paese, residenza) e
recensioni (voti per categoria).

**CasaZen oggi (§4).** Indicatori della Home (occupazione, ricavi, incassato, quota diretta, serie),
report di ricavi, occupazione e canali in CSV, report fiscali in CSV e PDF, ruolo Contabile in sola
lettura. Mancano: ADR e RevPAR, commissioni e costi dei canali (un soggiorno OTA ha solo il
totale), Excel, un archivio unico dei documenti, report sul mobile.

#### REP-01 — Quadro dell'attività · P0

Home secondo le regole di calma (un blocco «Adesso», al massimo tre numeri); pagina Report con
occupazione, ricavi, ADR e RevPAR (calcolati), andamento delle prenotazioni, per immobile, canale
e periodo, con confronto con lo stesso periodo dell'anno prima; vista per agenzia e per
proprietario (portale proprietari).

#### REP-02 — Incassi e pagamenti · P0

Incassato per data, metodo e canale; saldi da incassare; accrediti (Stripe e, dove disponibili,
OTA).

#### REP-03 — Commissioni e costi dei canali · P0

Dai componenti di prezzo di Octorate (`COMMISSION`) e dalle impostazioni del portale
(`commissionIncludedInPayments`, `commissionsInvoiced`): lordo, commissione e netto per canale;
commissioni Stripe; «quanto ti costa ogni canale» in una riga.

#### REP-04 — Archivio dei documenti · P0

Un solo posto «Documenti» con filtri per tipo (fatture, ricevute, note di credito, ricevute
Alloggiati, prove ISTAT, tassa di soggiorno, contratti, documenti dell'immobile), ricerca,
scaricamento singolo e archivio compresso per periodo.

#### REP-05 — Tassa di soggiorno · P0

Riepiloghi per comune e periodo con dovuto, versato, ricevute (CHK-07, CHK-08).

#### REP-06 — Pacchetto per il commercialista · P1

Esportazione mensile e annuale con struttura fissa (CSV ed Excel, PDF, XML), accesso per il ruolo
Contabile ai documenti (oggi legge prenotazioni e pagamenti), invio per email facoltativo.

#### REP-07 — Report in parole · P2

AI-10: riepilogo del mese, avvisi su cali e anomalie, domande in linguaggio naturale.

#### REP-08 — Sul mobile · P1

I tre numeri principali e «Da fare» anche sull'app.

## 7. Automazioni AI

**Principi.**

- Ogni automazione è **AUTOMATICA** (la fa CasaZen, la registra, è innocua o annullabile) oppure
  **PROPONE E APPROVI** (CasaZen prepara, l'utente conferma con un tocco da «Da fare», da una push
  o dal wizard). Niente di irreversibile verso ospiti, denaro, enti o OTA parte da solo senza una
  regola esplicita dell'organizzazione e una soglia di confidenza (MSG-07).
- Riuso: `IAiProvider` (Stub per default), servizio AI dell'agenzia (Q1-08), coda di approvazione
  (Q1-06), costruttore di condizioni (Q1-07), routine e bus di eventi (Q1-26, ADR-022), budget e
  limiti per organizzazione, avviso di trasparenza (AI Act art. 50).
- Dati: minimizzazione; dati personali al modello solo dove indispensabile e secondo PO-09;
  documenti e dati Alloggiati mai al modello (la lettura del documento resta in casa, Q1-10).
- Interruttore per organizzazione e per automazione; registro di modello, versione, confidenza,
  fonti usate e decisione; misure per automazione: accettate senza modifiche, modificate,
  rifiutate.

| ID | Processo | Automazione | Modalità | Priorità |
|---|---|---|---|---|
| AI-01 | Annunci | Bozza dell'annuncio da foto e cinque domande (tipo, posti letto, punti forti, zona, regole): titolo, descrizioni, testo degli spazi, regole; dotazioni riconosciute nelle foto («vedo lavatrice e balcone»); ordine delle foto con la copertina migliore; testi alternativi | PROPONE E APPROVI dentro LST-03 | P1 |
| AI-02 | Annunci | Traduzioni nelle lingue gestite da Octorate, marcate «tradotto automaticamente»; una modifica manuale non viene più sovrascritta; ripiego: traduzione di Octorate (§10.2) | AUTOMATICA, revisione facoltativa | P1 |
| AI-03 | Annunci | Controllo di qualità per canale: campi obbligatori mancanti, foto buie o piccole, incoerenze (ospiti massimi e letti), CIN assente; ogni problema con «Correggi» | AUTOMATICA (segnalazione) | P1 |
| AI-04 | Prezzi e calendario | Suggerimenti su notti isolate (riduzione del soggiorno minimo), ultimi minuti, stagioni ed eventi; mai cambi di prezzo automatici in questa versione (PO-16) | PROPONE E APPROVI | P2 |
| AI-05 | Check-in e adempimenti | Lettura del documento (in casa), controlli di coerenza (scadenza, età, codici di comune e stato), dati statistici dedotti dalla residenza, esenzione della tassa per età; esenzioni per altri motivi proposte all'host; promemoria agli ospiti con dati mancanti; sorveglianza delle scadenze verso «Da fare» | AUTOMATICA, tranne le esenzioni non legate all'età | P0 (regole), P1 (AI) |
| AI-06 | Messaggi | Intento e urgenza di ogni messaggio; risposta dalla base di conoscenza dell'immobile; traduzione nei due sensi; riassunto della conversazione; problemi (guasto, reclamo, rimborso) portati all'operatore con un'attività proposta | AUTOMATICA sopra soglia per le situazioni configurate, altrimenti PROPONE E APPROVI (MSG-06, MSG-07) | P1 |
| AI-07 | Prenotazioni | Attività di pulizia al check-out (routine); anomalie: più ospiti del consentito, prezzo molto sotto la media, rischio di sovrapposizione | AUTOMATICA (attività e segnalazioni) | P1 |
| AI-08 | Fatture e pagamenti | Bozza del documento fiscale al check-out secondo la regola dell'organizzazione; abbinamento di incassi e accrediti alle prenotazioni; dati di fatturazione letti da un messaggio dell'ospite («fattura a nome di…») | AUTOMATICA la bozza, PROPONE E APPROVI l'emissione e gli abbinamenti | P1 |
| AI-09 | Clienti e marketing | Segmenti suggeriti («tornati due o più volte», «famiglie di luglio»), bozze di campagna (Q1-08), momento d'invio, offerte di ritorno; invio sempre dopo approvazione e solo ai contatti leciti (CRM-02) | PROPONE E APPROVI | P1 |
| AI-10 | Report | Riepilogo del mese in parole («a ottobre hai incassato X, +12% rispetto a ottobre scorso»), avvisi su cali e anomalie, domande in linguaggio naturale sui propri dati (sola lettura) | AUTOMATICA | P2 |
| AI-11 | Da fare | Ordine per urgenza, raggruppamenti, momento migliore dei promemoria, una riga sul perché | AUTOMATICA | P1 |
| AI-12 | Tutti i wizard | Assistente dentro «Aiuto»: risponde sul passo corrente con le guide di CasaZen e compila campi su richiesta; non conferma mai al posto dell'utente | PROPONE E APPROVI | P2 |

Esempi di mercato per ogni automazione e la modalità che usano gli altri operatori: Appendice A,
parte C. Il mercato converge sullo stesso schema: bozza per default, automatico per argomento con
soglie ed escalation, etichetta che distingue persona e AI, conoscenza per immobile presa dai dati
dell'annuncio.

Nota tecnica: Octorate prevede tra le categorie dei partner `MODEL_CONTEXT_PROTOCOL` (§10.1):
non serve a questa versione, ma conferma che un accesso «da agente» alle sue API è previsto;
nessun uso senza ADR.

## 8. Architettura, autorità dei dati e ADR

### 8.1 Fondazioni del connettore (OCT-01…15 della v1.3)

Restano i requisiti tecnici di base, riportati come nella v1.3; i rimandi ai processi di §6 sono
sotto la tabella. Nella tabella «§5.1 Dn» indica le risposte D1–D14, oggi in §10.3, e «§6» la
matrice, oggi in §11. Le esclusioni della v1.3 §1 (check-in, fiscale, CRM, report, «senza
implementare tali moduli» in OCT-07) non valgono più: le sostituiscono §6.2, §6.6, §6.7 e §6.8.

| ID | Priorità / fonte | Requisito e criterio di accettazione |
|---|---|---|
| OCT-01 | P0 · S2–S4 | Adapter Mock e client Octorate con modalità interne Mock/Sandbox/Production; stessi contratti applicativi. Demo funziona senza rete esterna; nessuna chiamata Production dal Mock. Configurazione mancante impedisce attivazione live. Sandbox: S5 dichiara un solo server («Production - Restricted for Sandbox»); l'ambiente è un attributo dell'API member (`GET /rest/v1/api/configuration`, campo `environment` = `SANDBOX` o `PRODUCTION`), la chiamata nell'ambiente sbagliato dà 403 `WrongEnviroment` e le proprietà di prova hanno `sandboxProperty`. S6: la rete di test non è collegabile ai portali e non prova il push: la modalità Sandbox copre il contratto API, non gli effetti sulle OTA (§6, G2). |
| OCT-02 | P0 · S3/S4 | Separazione per tenant, ambiente e proprietà in DB, token, job e query. Un operatore non può leggere o scrivere risorse di altro tenant neppure passando ID esterni validi. Owner/guest non ricevono permessi operativi OTA. |
| OCT-03 | P0/P1 · S3/S4/S7–S10 | OAuth server-side per operation: authorizationCode e clientCredentials solo dove prescritti; state e callback validati, PKCE (il corpo di `POST /rest/v1/identity/token` prevede `code_challenge`). Segreti cifrati/secret store, rinnovo concorrente controllato, revoca e riconnessione. Token scaduto o scope insufficiente genera stato azionabile, mai successo simulato. S5: `OAuthLogin` (authorization code per proprietà o rete) e `ApiOperations` (client credentials, solo per creare proprietà, portali e sottoscrizioni; `apilogin` compare solo in Postman). S9 prevale sul lifecycle: access token valido 24 ore e refresh token senza scadenza; rinnovo serializzato per proprietà. Permessi per risorsa (`RESERVATION_READ/WRITE`, `CONTENT_*`, `CALENDAR_*`, `CONNECTION_*`, `WEBHOOK_WRITE`): chiedere il minimo; `CREDIT_CARD_*` e `RESERVATION_SENTITIVE_*` (ortografia della specifica) fuori perimetro salvo ADR. CasaZen non raccoglie né conserva username/password Octorate: il proprietario completa il login direttamente sul frontend Octorate. Prima dei dati reali: validare credenziali, scope, rate limit/quote e ambienti; configurare secret vault e IP statici da comunicare per whitelist. S7 conferma che email, telefono e altri dati sensibili non sono accessibili senza IP statici autorizzati e che non esiste un fallback sandbox. |
| OCT-04 | P0 · S2/S3 | Mapping tenant→proprietà→unità→rate plan→connessione→listing esterno. Vincoli univoci nello scope corretto, conflitti e import preview; nessuna scrittura con mapping ambiguo. Conservare ID annunci esistenti, recensioni e continuità solo dopo prova/conferma OTA. S5: connessione (`POST /rest/v1/connection/{accommodation}`, verifica licenza e credenziali), azioni `IMPORT_ROOMS`, `IMPORT_RESERVATIONS`, `IMPORT_CALENDAR`, poi mapping `productId` ↔ `externalId` (`updateCalendar` e `pullReservations` attivabili solo dopo il mapping; `pullMappedOnly` vero di default), `MARK_CONNECTED` o `COMMIT_MAPPING_AS_COMPLETED`; i prodotti esterni con `manageable` falso sono solo informativi (§5.1 D6). |
| OCT-05 | P0/P1 · S2–S4 | Editor contenuti: titoli, descrizioni e traduzioni IT/EN/DE/FR, foto, servizi, camere/letti, regole, politiche e CIN/licenze. Distinguere bozza, in coda, accettato dal provider e pubblicato su OTA; limitare campi e azioni alla matrice capacità. Un errore parziale non marca tutto come pubblicato. Interfaccia DE non richiesta ora. S5: nessun campo CIN dedicato (`nrua` esiste solo per la Spagna); candidati `legalId`/`legalIdIssuer` (camera) e `taxId` (listing), campo effettivo per OTA da confermare. Stato di pubblicazione: `airbnbApprovalStatus` e `GET /rest/v1/content/products/{accommodation}/process/{processId}`; push per prodotto solo `booking_xml`, `airbnb_xml`, `homeaway`. Non usare `addTextToHouseRules` e `addTextToField` come API di scrittura (§5, §5.1 D2). |
| OCT-06 | P0/P1 · S2–S4 | ARI con diff, aggiornamenti bulk e controllo stato processo. Date locali della struttura, soggiorni [check-in, check-out), valuta e decimali espliciti; test DST Italia/Svizzera. Nessuna disponibilità/prezzo/restrizione inviata a unità errata. Accettazione bulk non equivale a propagazione OTA. S5: date `yyyy-mm-dd` con `dateTo` inclusivo, nessuna valuta nel payload; quota 1 unità ogni 15 camera-giorno, dinamica per proprietà e tariffe (§5.1 D7). Il bulk può rispondere HTTP 200 con `success: false` (esempio Postman): controllare il corpo e conservare gli id `process`; lo stato per data e portale è solo in Postman. Octorate può alterare ARI da solo (`closeNextDays`/`openNextDays`, `tripleThePrice`, `revenue*`, `derivedRule`; per connessione `correctionRatio`, `correctionFixed`, `correctionRound`, `availabilityPercentage`): l'adapter li legge e li tiene neutri o li modella, altrimenti il writer unico non basta. |
| OCT-07 | P0/P1 · S2–S4 | Import, modifica e cancellazione prenotazioni con upsert per ID provider e tenant/ambiente. Duplicati/out-of-order non duplicano prenotazioni o effetti downstream. Eventi interni verso check-in/compliance/rendiconti, senza implementare tali moduli. Prenotazione OTA non crea addebito Stripe; dirette non diventano pagate per un ACK Octorate. S5: eventi `RESERVATION_CREATED`, `RESERVATION_CHANGE`, `RESERVATION_CANCELLED`, `RESERVATION_CONFIRMED`; chiave di upsert `id` Octorate (`refer` raggruppa più camere, `channelRefer` è il codice del portale) con `updateTime`; `checkin` e `checkout` sono istanti UTC da convertire con il fuso della struttura. Cancellazione con `PUT` e `status` CANCELLED oppure `DELETE`; `PATCH` non consente CONFIRMED→CANCELLED; 409 per prenotazioni spostate o annullate da undo. Creare una prenotazione diretta in Octorate decrementa la disponibilità salvo `availability=false`: decisione in ADR (§5.1 D9). Telefono ed email dell'ospite solo con `POST /rest/v2/reservation/{accommodation}/{refer}/contactInfo` (permesso speciale, ogni lettura è registrata). |
| OCT-08 | P0/P1 · S3/S4 | Receiver webhook con validazione secondo contratto reale, persistenza inbox prima dell'ACK, elaborazione asincrona, deduplica, retry e quarantena. Non inventare header di firma o nomi eventi. Se autenticità non verificabile, documentare mitigazioni e bloccare gli effetti non sicuri. S5: eventi `RESERVATION_CREATED`, `RESERVATION_CHANGE`, `RESERVATION_CANCELLED`, `RESERVATION_CONFIRMED`, `CHAT_MESSAGE_RECEIVED`, `CONTENT_NOTIFICATION` (esito di una pubblicazione), `CONTENT_PUSH` e `PORTAL_SUBSCRIPTION_CALENDAR` (descritti per le OTA); quelli sottoscrivibili da CasaZen risultano da `GET /rest/v1/subscription/list`. Consegna: ritenta fino a 2 giorni con ritardi crescenti, HTTP 200 = consegnato, ogni altro codice ritenta, solo 406 ferma i tentativi; timeout molto breve (ACK prima di elaborare); ordine non garantito; invii ripetuti dello stesso contenuto entro 5 minuti collassati in uno. La sottoscrizione accetta solo `url` https (`application/x-www-form-urlencoded`), senza segreto, e l'operazione non dichiara una security propria (la collection la colloca tra le operazioni Admin). Il campo `hmac` compare solo nell'esempio calendario: per prenotazioni e chat l'autenticità non è documentata, quindi (proposta) il webhook è una notifica e prima di ogni effetto l'oggetto si rilegge via API. |
| OCT-09 | P0/P1 · S3/S4 | Riconciliazione periodica con checkpoint, finestre sovrapposte e paginazione da contratto; recupero eventi persi e confronto stato remoto. Concorrenza serializzata per risorsa; dopo crash il job riprende senza duplicati. S5: elenco `GET /rest/v1/reservation/{accommodation}` con `type` `MODIFIED_BETWEEN`, `CREATETIME`, `CANCELLATION` o `STAY`, `size` da 1 a 200, `page` da 0 a 1000, date `yyyy-MM-dd` sempre in ora di Roma. `GET .../reservation/{accommodation}/search` non è un elenco: è la ricerca del booking engine. Chat: `createdAfter` sui messaggi del thread e `GET /rest/v1/accommodation/recentMessages` (ultime 24 ore). Finestre sovrapposte di almeno 2 giorni, la finestra di retry dei webhook (proposta). |
| OCT-10 | P0/P1 · S2–S4/S7 | Inbox web e mobile: conversazioni per canale/proprietà/prenotazione, lettura, risposta e stato errore/consegna disponibile. S7 conferma le API di messaggistica per Airbnb, Booking.com, Vrbo ed Expedia; per gli altri canali non promettere inbox OTA e usare solo i contatti disponibili (email, telefono, WhatsApp) o gli strumenti chat Octorate. Pre-booking, post-stay e allegati sono capacità distinte. Due click/retry non duplicano messaggi; timeout ambiguo resta da riconciliare. S5: thread con `defaultProcessor` (`DIRECT`, `WHATSAPP`, `CHANNEL`, `WHATSAPP_MANUAL`, `MAIL`, `NOTE`) e messaggi con `status` (`CREATED`, `WAIT_AUTH`, `SENT`, `ERROR`, `MANUAL_VERIFY`). Verso le OTA: processor `CHANNEL` con attributo `OTA_FLOW_DIRECTION` = `TO`; la licenza ha un flag `chat` e la connessione un flag `messages`: verificare che siano inclusi e attivi. Ingresso quasi in tempo reale con `CHAT_MESSAGE_RECEIVED` più riconciliazione; thread e prenotazione si collegano con `reservation` e `threadId`. Allegati, offerte speciali e pre-approvazione esistono nel modello ma il supporto per OTA non è confermato (§5.1 D3). |
| OCT-11 | P0/P1 · S2–S4 | AI: bozza modificabile e inviata dall'operatore nel Base. Invio automatico solo con opzione/permesso attivo, regole e soglia approvate, supporto OTA confermato; altrimenti bozza. Audit modello/versione, decisione e approvazione; contesto minimo per tenant. Bloccare contatti/link Airbnb pre-booking e azioni non consentite; nessuna modifica date Booking via chat. Octorate ha un proprio assistente AI per thread (`PATCH /rest/v1/chat/{accommodationId}/threads/{thread}/aiAssistant`, flag `aiDriven`, attributo `AI_SKIP_DISABLE`): sui thread gestiti da CasaZen resta disattivo e va verificato a ogni sincronizzazione, per evitare doppie risposte. La policy delle OTA sulle risposte automatiche non è né in S5 né in S6. |
| OCT-12 | P0/P1 · S2–S4 | Review UI e workflow risposta in Mock per tutti i canali, etichettati simulati. Live solo per Airbnb, con S5: `GET /rest/v2/review/{accommodationId}/{refer}` (recensione dell'ospite e dell'host per prenotazione, visibile solo a recensione pubblicata, stato `REVIEW_PUBLISHED`), `POST .../sendHostReply` (solo Airbnb, a recensione dell'ospite pubblicata) e `POST .../sendHostReview` (solo Airbnb, a finestra aperta; 409 se non ammessa o già inviata): lettura, risposta e recensione dell'host sono capacità separate. Nessun elenco per proprietà: si legge per prenotazione. S6: per Booking.com, Vrbo ed Expedia nessuna integrazione, quindi nessuna lettura o risposta live; nessun endpoint inventato, scraping o risposta live implicita. Il gap sulle altre OTA resta nel go/no-go e un eventuale fallback manuale è un'eccezione di prodotto da approvare. |
| OCT-13 | P0/P1 · S3/S4 | Resilienza: timeout/cancellazione, backoff con jitter e Retry-After se presente, rate limit per scope effettivo e backpressure. Distinguere validazione, 401, 403, 404, 409/422 se previsti, 5xx. S5 non documenta né 429 né Retry-After: la quota esaurita è un 403 `ApiQuotaException`, da distinguere dai 403 di permessi (`ApiSecurityException`) e di ambiente (`WrongEnviroment`); un 550 `ApiPartnerServerError` indica il fallimento del partner o della OTA. Decidere sul `code` stabile di `ApiError` e non sul testo; salvare `request-id`; leggere `X-RateLimit-Remaining` e, se presente, `X-RateLimit-Additional-Cost`; quota in `quotaLimit` e `quotaStatus` di `GET /rest/v1/api/configuration`. Nessun header o chiave di idempotenza né ETag documentati; alcune PATCH rispondono 304 se nulla cambia; alcuni esempi di errore sono pagine HTML del server, quindi non assumere JSON. Retry scritture solo se sicuro/idempotente o previa riconciliazione. Nessuna tempesta di retry. |
| OCT-14 | P0/P1 · S3/S4 | Dashboard stato per proprietà/canale, lag, quota, errori, ultima riconciliazione, operazioni sospese e replay autorizzato. Correlation ID e audit senza token/documenti/testi sensibili nei log. Kill switch scritture per tenant/proprietà/canale, senza perdere eventi ricevuti. |
| OCT-15 | P0 · S3/S4 | Demo deterministica con agenzia DEMO e 10 proprietà fittizie: mapping, contenuti/foto, ARI, creazione/modifica/cancellazione, chat/AI e review. Reset ripristina dataset; mostra sempre Mock e le funzionalità non certificate; nessun dato reale Quokka. |

Riferimenti nella v2.0: OCT-04, OCT-05 e OCT-06 → `LST`; OCT-07, OCT-08 e OCT-09 → `BKG`;
OCT-10 e OCT-11 → `MSG`; OCT-12 → MSG-09; OCT-14 → MKT-04 e UX-09; OCT-15 → G1 con l'Agenzia
DEMO (Q1-27).

### 8.2 Autorità dei dati

Prima della prima scrittura verso Octorate si registra in ADR, per ogni dato, chi è la fonte e
chi può scrivere. Conflitto non risolvibile → coda dell'operatore in «Da fare», mai l'ultima
scrittura che vince in silenzio. Si memorizzano versione remota (se disponibile), ultimo stato
osservato e origine.

| Dato | Fonte | Chi scrive verso gli altri | Note |
|---|---|---|---|
| Contenuti dell'annuncio (testi, foto, servizi, regole, politiche) | CasaZen | CasaZen → Octorate → OTA | Modifiche fatte fuori da CasaZen rilevate e proposte (LST-01) |
| Unità, letti, tariffe e piani tariffari | CasaZen | CasaZen → Octorate | Versione delle API `roomrates` scelta in ADR |
| Disponibilità, prezzi, restrizioni | CasaZen | Unico scrittore (PO-16) | Regole automatiche di Octorate spente o lette |
| Prenotazioni delle OTA | OTA via Octorate | Octorate → CasaZen | In CasaZen solo ciò che l'OTA consente (BKG-03) |
| Prenotazioni dirette e manuali | CasaZen | CasaZen → Octorate | Bloccano la disponibilità su tutti i canali |
| Pagamenti diretti | Stripe tramite CasaZen | CasaZen → Octorate (registrazione) | Un ACK di Octorate non rende pagata una prenotazione |
| Pagamenti delle OTA | OTA via Octorate | Octorate → CasaZen | Commissione dai componenti di prezzo |
| Dati degli ospiti per il check-in | CasaZen (raccolta) | CasaZen → Octorate | Documenti mai inviati a Octorate come immagini |
| Invio Alloggiati | Mittente unico per immobile (PO-02) | Octorate o CasaZen | Esito per ospite da Octorate; ricevuta dalla Polizia |
| Flussi ISTAT | Octorate dove configurato (PO-03) | Octorate | Altrove file di CasaZen |
| Tassa di soggiorno | CasaZen (PO-04) | CasaZen → Octorate | Confronto con il valore di Octorate |
| Stato di check-in e check-out | CasaZen | CasaZen → Octorate | Eventi sul bus |
| Documenti fiscali | Emittente unico per organizzazione (PO-05) | CasaZen oppure Octorate | Mai entrambi |
| Messaggi delle OTA | OTA via Octorate | Lettura e invio tramite Octorate | Copia in CasaZen per hub, ricerca e AI |
| Messaggi diretti | CasaZen | CasaZen | Thread esistenti |
| Recensioni | OTA (Airbnb via Octorate) e sito CasaZen | — | Risposte solo Airbnb via API |
| Consensi, preferenze di contatto, soppressione | CasaZen | CasaZen | Octorate non ha consensi |
| Report e indicatori | CasaZen (calcolati) | — | Nessun report Octorate |

Outbox e modifica interna atomiche (bus di eventi con outbox, ADR-022); la chiave idempotente
interna non implica che Octorate supporti un'intestazione di idempotenza (non la supporta, v1.3
OCT-13). Conservazione e cancellazione dei dati degli ospiti definite prima dei dati reali.

### 8.3 Componenti

- **Connettore Octorate** per capacità (contenuti, ARI, prenotazioni, check-in, chat, documenti,
  portali), con modalità Mock, Sandbox e Production, client generato dalla specifica, OAuth per
  proprietà e resilienza (OCT-01…03, 13). Affianca `IChannelAdapter` senza riattivare gli
  adattatori diretti (§4).
- **Ingresso degli eventi**: webhook persistiti prima dell'ACK, rilettura dell'oggetto via API
  prima di ogni effetto, riconciliazione periodica, pubblicazione sul bus di eventi esistente
  (OCT-08, 09; ADR-022).
- **Sincronizzazione dei contenuti**: differenze, invio per canale, stato per canale (LST-01,
  LST-12).
- **Livello guidato** con bozze sul server, sotto-passi e indirizzi dei passi (UX-01…03).
- **Servizio «Da fare»**, preferenze di notifica e dispatcher degli avvisi (push mobile e
  browser, email, contatore, popup) (UX-07, UX-08, MSG-05).
- **Hub dei messaggi** con politiche AI, confidenza e coda di approvazione (MSG).
- **Catalogo del marketplace** (MKT).
- **Modelli di lettura per i report** alimentati dagli eventi (REP).

### 8.4 ADR da aprire o emendare

Numeri assegnati dalla fase ADR di Helix.

1. Connettore del channel manager per capacità; rapporto con `IChannelAdapter` e con gli
   adattatori diretti; versione delle API `roomrates`.
2. Autorità dei dati e conflitti (tabella §8.2).
3. Livello guidato, bozze sul server e sotto-passi (UX-01…03): componenti, conservazione, dati
   personali nelle bozze.
4. «Da fare» e avvisi: modello unico, escalation, preferenze, push del browser (UX-07, UX-08,
   MSG-05).
5. Politica AI dei messaggi: situazioni, confidenza, soglie, categorie sensibili, dati al modello
   (PO-08, PO-09).
6. Mittente unico degli adempimenti e ricevute (emenda ADR-015) (PO-02, PO-03).
7. Emittente unico dei documenti fiscali (emenda ADR-019) (PO-05, PO-06).
8. Contatti leciti per il marketing: fonti, base giuridica, soft opt-in, soppressione (PO-07,
   PO-17); collegamento con l'abbinamento degli ospiti (emenda ADR-021).
9. Catalogo del marketplace e attivazioni (MKT).
10. Emendamenti: ADR-002 (passaggio dal calendario iCal al connettore per canale), ADR-022
    (eventi del connettore sul bus).

## 9. Contratto OAuth e confini con Octorate

### 9.1 Contratto OAuth (S9, testo confermato dal PO il 31/10)

S9 e S10 chiudono i seguenti punti operativi senza autorizzare l'uso dei segreti nel frontend o
nel repository:

1. CasaZen genera il link sul frontend Octorate con `client_id` (chiave pubblica del partner),
   `redirect_uri` codificata e `state`, secondo il modello di S9:
   `https://admin.octorate.com/octobook/identity/oauth.xhtml?client_id=<client_id>&redirect_uri=<redirect_uri codificata>&state=<state>`.
   Octorate descrive `state` come opzionale ma consigliato; CasaZen lo rende obbligatorio,
   monouso, a scadenza e legato a tenant, utente e redirect attesi.
2. Il proprietario dell'appartamento accede direttamente a Octorate e autorizza quella
   proprietà. CasaZen non riceve né conserva username e password Octorate.
3. Octorate reindirizza alla callback CasaZen con `code`. S9 dice «pochi secondi», S5 indicava
   3 minuti: scambio immediato, nessuna durata minima presunta; un codice scaduto riavvia il
   consenso.
4. Il backend scambia il codice con il client secret e conserva cifrati access e refresh token.
   L'access token dura 24 ore; il refresh token non scade secondo S9. La scadenza è scritta nel
   token stesso: CasaZen la legge da lì, non la presume, e prima di ogni chiamata rinnova un token
   scaduto o vicino alla scadenza. Rinnovo single-flight per proprietà; mai token nel browser o
   nei log.
5. L'autorizzazione è per proprietà: **N appartamenti indipendenti = N consensi e N coppie di
   token**. Storage, lock, revoca, audit e salute sono per tenant + ambiente +
   `OctorateAccommodationId`, non globali per agenzia.

Endpoint da configurazione, mai sparsi nel codice: documentazione
`https://api.octorate.com/connect`; frontend e login `https://admin.octorate.com/octobook`;
base API `https://api.octorate.com/octobook/rest`; autorizzazione
`/octobook/identity/oauth.xhtml` sul frontend Octorate; token e refresh con le operazioni
ufficiali `token` e `refresh`. Le redirect URI di test e di produzione vanno entrambe in
whitelist (test: `postConfiguration`; produzione: `openapi@octorate.com`); il default
`https://localhost/` va sostituito con la callback CasaZen, che non esiste ancora. Riferimenti di
S9: operazioni `postConfiguration`, `token` e `refresh` nella documentazione
(`https://api.octorate.com/connect/redocly.html`), video passo passo dell'autenticazione
(`https://api.octorate.com/connect/showcases/authentication.html`), supporto tecnico
`openapi@octorate.com`.

**Due ingressi.** Il consenso OAuth presuppone che il proprietario abbia già le credenziali della
sua struttura su Octorate (S9: «il cliente accede con le credenziali create all'interno della
propria struttura»). Quindi:

- **struttura già su Octorate** → consenso OAuth nel wizard «Collega i tuoi canali» (LST-02);
- **struttura nuova, creata da CasaZen** con `POST /rest/v2/accommodation` (`ApiOperations`,
  LST-03) → la risposta contiene già il refresh token della proprietà, quindi non serve un
  consenso separato. CasaZen crea la struttura con `welcome_user` **falso** (il default di
  Octorate è vero): il proprietario non riceve email da Octorate e resta in CasaZen. Le
  credenziali si attivano solo su sua richiesta o quando lascia CasaZen (PO-19, deciso dal PO il
  31/10; procedura da concordare con Octorate, D11 e D27).

Il wizard chiede all'inizio «Le tue strutture sono già su Octorate?» (uno dei punti in cui il nome
si mostra, PO-01) e sceglie da solo l'ingresso giusto: l'utente non deve sapere cosa sia un
consenso OAuth. Chi usa un altro channel manager passa dal secondo ingresso, con l'import degli
annunci dai canali (D6).

Configurazione già fatta il 30/10/2026: nel secret store Railway del solo ambiente backend
`test` ci sono `Octorate__ClientId`, `Octorate__ClientSecret`, `Octorate__ApiBaseUrl`,
`Octorate__FrontendBaseUrl` e `Octorate__AuthorizationEndpoint`; niente in `production`,
Vercel o GitHub. Prima dell'implementazione verificare che le credenziali siano valide; se
scadute, ruotare il secret senza cambiare i nomi.

**Per l'utente inesperto (UX-02):** il consenso è un passo del wizard «Collega i tuoi canali»
(LST-02): una schermata spiega in una frase cosa succede («ti portiamo sul sito del channel
manager per autorizzare CasaZen, poi torni qui»), apre la pagina di consenso nella stessa
scheda e la callback riporta **allo stesso passo dello stesso wizard**, con la bozza intatta.
Se l'utente chiude la pagina, il wizard riprende dal passo salvato (UX-03).

### 9.2 Confini con Octorate: resoconto (S7) e specifica (S13)

| Area | Cosa dice S7 (30/10) | Cosa mostra S13 (31/10) | Posizione della v2.0 |
|---|---|---|---|
| Check-in e Polizia | CasaZen crea schedine e check-in e genera i dati Alloggiati e ISTAT; l'invio lo fa Octorate; gli esiti sono solo nella UI Octorate | Ospiti del check-in in scrittura con tutti i campi Alloggiati e statistici; per ospite `policeSentTime` e `policeSentStatus` in lettura; nella struttura `policeAccount` (`autorun`, `valid`, `lastCron`); nessuna operazione per avviare l'invio o scaricare la ricevuta; impostazioni ISTAT solo nella UI (tipi di log `ISTAT_SETTINGS_EDIT`) | CasaZen scrive gli ospiti e Octorate invia (PO-02); CasaZen legge l'esito per ospite e prende la ricevuta ufficiale dal web service della Polizia (CHK-04); ISTAT con prova guidata (CHK-05). D17, D18 |
| Tassa di soggiorno | Interamente CasaZen | `touristTax` della prenotazione «calcolata al momento»; per ospite `cityTaxPrice`, `cityTaxExemption` (30 motivi), `excludeCityTax` se incassata dal canale; modello `TaxImpl` inviato ai portali; nessuna operazione per le regole | CasaZen è il calcolatore di riferimento e scrive importo ed esenzione per ospite (CHK-07). D20 |
| Documenti fiscali | Lettura di documenti già generati e dei dati di fatturazione; nessuna API di creazione | Lettura con tipo e stato SDI (`fiscalInvoiceGroup`, solo con integrazione «treasury»); PDF di riepilogo; **esistono** `createDoubleHeadingInvoice` e `issueSummaryDocument` | Lettura sì; creazione solo dopo conferma scritta (D19); altrimenti emette CasaZen (FIS-02) |
| Report | Nessuna API | Confermato: nessuna operazione di report | CasaZen calcola dai dati di prenotazioni, pagamenti e documenti (REP) |
| Recensioni | Solo Airbnb | Confermato (v1.3 OCT-12) | Invariato |
| Dati degli ospiti | Senza IP statici in whitelist niente email, telefono e dati sensibili | `contactInfo` con permesso speciale, ogni lettura registrata (`CONTACT_INFO_SHOWN`); elenchi con `emailAvailable` e `phoneAvailable`; nessun campo di consenso | Lettura solo quando serve al servizio (messaggi, check-in, fattura), mai in blocco per il marketing (CRM-07). D24 |
| Marketplace | — | `meta/portals` con `portalCategory` `OTA` o `EXTERNAL_TOOL`, connessioni attivabili e disattivabili; moduli in `license`; nessun elenco delle app dei partner | MKT con tre fonti (portali via API, moduli, catalogo curato da CasaZen). D16, D21, D23 |
| Messaggi | API per Airbnb, Booking.com, Vrbo, Expedia | Confermato; più `readAll`, membri dei thread, traduzione, modelli | `MSG` |

## 10. Inventario API verificato (OpenAPI del 31/10/2026, S13)

Base `https://api.octorate.com/connect`, unico server dichiarato («Production - Restricted for
Sandbox»); l'ambiente è un attributo dell'API member (`environment`). Token: `OAuthLogin` per
proprietà, `ApiOperations` per operazioni senza dati di proprietà (§9.1). Molte operazioni di
check-in, ospiti, fatture e prenotazioni **non dichiarano** lo schema di sicurezza
nell'OpenAPI: usare il token della proprietà e verificarlo in sandbox. «Postman» = voce presente
solo nella collection, da confermare prima dell'uso.

### 10.1 Operazioni della v1.3 (verificate di nuovo, invariate)

| Area | Operazioni | Note |
|---|---|---|
| Autenticazione | POST `/rest/v1/identity/token`, `/rest/v1/identity/refresh`, `/rest/v1/identity/migrate` | `apilogin` (client credentials) solo in Postman |
| Configurazione | GET, PATCH `/rest/v1/api/configuration` (`ApiOperations`) | `environment`, `quotaLimit`, `quotaStatus`, `permissions`, `redirectUri`, `reseller`, `maintenance`; `category` sola lettura (D22); `applicationName`, `description`, `icon`, `secretVisibility` = come CasaZen appare nell'elenco delle API di Octorate |
| Proprietà | POST `/rest/v2/accommodation` (`ApiOperations`); GET `/rest/v1/accommodation` e `/rest/v1/accommodation/{acc}`; PATCH `/rest/v2/accommodation/{acc}`; DELETE `/rest/v1/accommodation/{acc}/account` | `welcome_user` vero di default manda le credenziali all'email indicata; `network_name` (v1.3 §2: non usato per appartamenti indipendenti); il DELETE disattiva senza cancellare |
| Unità e tariffe | POST `/rest/v1/roomrates/{acc}` e `/rest/v3/roomrates/{acc}`; GET `/rest/v1/roomrates/{acc}/{roomid}`, `/rest/v2/roomrates/{acc}`, `/rest/v3/roomrates/{acc}`; PATCH `/rest/v1/roomrates/{acc}/{id}` e `/rest/v3/roomrates/{acc}/{id}`; GET, POST `/rest/v1/rateplans/{acc}`; PUT, DELETE `/rest/v1/rateplans/{acc}/{id}`; POST `/rest/v2/roomrates/{acc}/runRules` | tre versioni coesistono: sceglierne una con Octorate (ADR); `legalId`, `legalIdIssuer`, `quantity`, `ratePlans`; 304 se nulla cambia |
| Connessioni e mapping | POST `/rest/v1/connection/{acc}`; GET `/rest/v1/connection`; PATCH, DELETE `/rest/v1/connection/{acc}/{connection}`; POST `/rest/v1/connection/{action}/{acc}/{connection}`; GET, POST `.../connection/external/...` e `.../connection/mapping/...`; DELETE `.../mapping/{acc}/{connection}/{mappingId}` | azioni `IMPORT_ROOMS`, `IMPORT_RESERVATIONS`, `IMPORT_CALENDAR`, `MARK_CONNECTED`, `COMMIT_MAPPING_AS_COMPLETED`, `IMPORT_ROOMS_PMS`, `IMPORT_RESA_DECREMENT_AVAIL`, `EMAIL_HELP_CUSTOMER`; 550 se il partner fallisce |
| ARI | GET `/rest/v1/calendar/{acc}`; POST `/rest/v1/calendar/bulk`; GET `/rest/v1/calendar/{acc}/{productId}/availabilityCheck`; POST `/rest/v1/content/products/{acc}/{productId}/{portalname}/calendar` | `GET /calendar/process/{id}` e `/calendar/failures` solo in Postman |
| Prenotazioni | GET, POST `/rest/v1/reservation/{acc}`; PUT, PATCH, DELETE `/rest/v1/reservation/{acc}/{id}`; POST `/rest/v1/reservation/bulk/{acc}`; POST, DELETE `.../{reservationId}/split`; DELETE `/rest/v1/reservation/{acc}/group/{refer}`; POST `/rest/v2/reservation/{acc}/{refer}/contactInfo` | `GET .../{id}` deprecata; `GET .../search` è il motore di prenotazione |
| Chat | GET `/rest/v1/chat/{property}/threads`, `.../threads/{id}`, `.../threads/{thread}/messages`, `.../threads/{thread}/templates`, `.../unreadedMessagesCount`, `.../messages`; POST `.../messages`; PUT `.../messages/{messageId}`; PATCH `.../threads/{thread}/archive`, `read`, `setAsUnread`, `aiAssistant`, `processor`, `.../messages/readAll`, `.../translate/{language}`; PUT, DELETE `.../threads/members/{personId}`; POST `.../preApprove`, `.../specialOffer`; PATCH `.../withdraw`; GET `/rest/v1/accommodation/recentMessages` | v1.3 OCT-10 |
| Recensioni | GET `/rest/v2/review/{acc}/{refer}`; POST `.../sendHostReply`, `.../sendHostReview` | risposta e recensione dell'host solo Airbnb; categorie di voto e `privateReview` |
| Webhook | POST `/rest/v1/subscription/{event}`; GET `/rest/v1/subscription`, `/rest/v1/subscription/list`; PUT, DELETE `/rest/v1/subscription/{id}` | `RESERVATION_CREATED`, `RESERVATION_CHANGE`, `RESERVATION_CANCELLED`, `RESERVATION_CONFIRMED`, `CHAT_MESSAGE_RECEIVED`, `CONTENT_NOTIFICATION`, `CONTENT_PUSH`, `PORTAL_SUBSCRIPTION_CALENDAR`; nessun evento per check-in, Polizia o fatture |
| Contenuti per connessione | GET, POST `/rest/v1/content/connections/{connection}/accommodationconf`, `services`, `fees`, `extrabeds`, `contacts`, `cancellationPolicies` (DELETE per elemento); POST `.../rentalagreement`, `.../push`; GET `.../meta/{portal}` | impostazioni per canale |
| Contenuti per prodotto | `/rest/v1/content/products/{acc}/{productId}/`: `descriptions`, `listing`, `rooms`, `photos` (coda), `availabilityconf`, `fees`, `rate`, `pricing`, `reservation`, `cancellation`, `pushQueue/{portalname}`; GET `.../process/{processId}` | `pushQueue` solo `booking_xml`, `airbnb_xml`, `homeaway`; `amenities` deprecati; `addTextToHouseRules` e `addTextToField` sono generatori di testo, non scritture |

### 10.2 Operazioni aggiunte dalla v2.0 (prima fuori perimetro)

| Area | Operazioni | Note |
|---|---|---|
| Check-in e Polizia | POST `/rest/v2/checkin/{acc}/{id}/guest`; PUT, DELETE `/rest/v1/checkin/{acc}/{reservationId}/guest/{guestId}`; PUT `/rest/v1/checkin/{acc}/{reservationId}?status=` `CHECKIN_END`, `CHECKOUT_END`, `NO_SHOW`; GET `/rest/v2/checkin/documents/{acc}`, `/rest/v2/checkin/city/{country}`, `/rest/v2/checkin/{acc}/tags`, `/rest/v1/meta/documents`; GET `/rest/v1/meta/cities` (Postman) | `ApiReservationGuestDTO` e V2: anagrafica; documento con i codici della Polizia (`documentType`, `cityPoliceCode`); `accommodatedType` (singolo, capofamiglia, capogruppo, familiare, membro); campi statistici (`tourismType`, `travelWay`, `degree`, `travelRefer`, residenza, cittadinanza); `draft` (da rivedere prima dell'invio); `validated`, `validatedDate` (record inserito dall'ospite); `policeSentTime`, `policeSentStatus` (sola lettura); tassa per ospite. Nessuna operazione per avviare l'invio o per le ricevute |
| Struttura: Polizia, fiscale, licenza | nella struttura (`ApiAccommodationDTO`): lettura con GET `/rest/v1/accommodation/{acc}`; scrittura con PATCH `/rest/v2/accommodation/{acc}` (`ApiOperations`) da confermare per questi campi (D17, D21) | `policeAccount` (`codice`, `wsCode`, `autorun`, `valid`, `groupGuest`, `lastCron`); `vatCode`, `fiscalCode`, `companyType`, `taxes`, `invoiceOptions` (`treasuryActive`, `splitCommissionPropertyManager`); `license`; `checkinStart`, `checkinEnd`; `propertyCategory` |
| Ospiti (rubrica) | GET `/rest/v1/guests/{acc}/list`, `/rest/v1/guests/{acc}/{guestId}`, `/rest/v1/guests/{acc}/suggestions`; POST `/rest/v1/guests/{acc}`; PUT `/rest/v1/guests/{acc}/{guestId}`; POST `.../{guestId}/contactInfo` | contatti solo con permesso speciale, ogni lettura registrata; nessun campo di consenso |
| Fatture e documenti | GET `/rest/v2/invoice/{codice}`; POST `/rest/v2/invoice/{acc}/summary/{refer}`; GET `/rest/v2/invoice/pdf/{acc}/summary/{idDocumentEncrypted}`; POST `/rest/v2/invoice/{codice}/{refer}/createDoubleHeadingInvoice` | filtri per prenotazione, tipo, pagato, scaduto, `fiscalInvoiceGroup` (`EMPTY`, `REJECTED_BEFORE`, `BEFORE_SDI`, `SDI_PROCESSING`, `SDI_REJECTED`, `DONE`), `passive` (documenti ricevuti); tipi `PROFORMA`, `INVOICE`, `FISCALRECEIPT`, `GENERICRECEIPT`, `CREDIT_NOTE` (Postman); modulo `invoice` della licenza; creazione da chiarire (D19) |
| Pagamenti della prenotazione | POST `/rest/v1/reservation/{acc}/{id}/payment`; PUT, DELETE `.../payment/{paymentId}`; PATCH `/rest/v1/payment/{acc}/{paymentId}/move`; DELETE `/rest/v1/payment/{acc}/{paymentId}` | modo, `source` (`PRIVATE`, `AGENCY`), `transaction` esterna, `cityTaxAmount`, fattura collegata. Carte (`addCard`, `detokenize`, `register`) fuori perimetro: PCI |
| Prezzo e stato della prenotazione | campi di `ApiReservationRespDTO` | componenti `DAILY_ROOM_PRICE`, `ROOM_NET`, `VAT`, `EXTRA`, `CLEANING`, `DISCOUNT`, `COMMISSION`, `TOURIST_TAX`, `BREAKFAST`; `paymentStatus` (`UNPAID`, `PARTIALLY_PAID`, `PAID_DEPOSIT`, `PAID`), `totalPaid`, `status` (`CANCELLED`, `WAITING`, `CONFIRMED`), `effectiveCheckin`, `arrivalTime`, `cancelledBy`, `cancelledTime`, `otaId`, `threadId`; per portale `commissionIncludedInPayments` |
| Prodotti extra | GET `/rest/v1/extra/{acc}`; POST `/rest/v1/extra/bulk/{acc}`; PUT, DELETE `/rest/v1/extra/{acc}/{productId}`; PUT `.../updateAll`; sulla prenotazione POST `.../{id}/extras`, PUT, DELETE `.../extra/{extraId}` | servizi venduti con il soggiorno (calcolo, IVA ed esenzioni italiane, visibilità): base per offrire i servizi CasaZen sulla prenotazione (P2) |
| Politiche | GET `/rest/v1/policies/{acc}` | cancellazione e pagamento (`ApiPaymentStepDTO`) a livello struttura, sola lettura; scrittura per connessione e per prodotto |
| Foto della struttura | GET, POST `/rest/v1/accommodation/{acc}/photos`; DELETE `.../photos/{name}`; GET `/rest/v1/accommodation/{acc}/queue`, `.../queue/{id}` | caricamento da URL in coda: le foto devono essere raggiungibili da Octorate (PO-15) |
| Traduzioni | PATCH `/rest/v1/roomrates/{acc}/{roomid}/translateMissingDescriptions/{language}` e v3 | Octorate traduce le descrizioni mancanti da una lingua: alternativa all'AI di CasaZen (AI-02) |
| Portali e strumenti | GET `/rest/v1/meta/portals` (`ApiOperations`) | `portalCategory` `OTA` o `EXTERNAL_TOOL`, `creatable`, `enabled`, `preferred`, `website`, `logo`, requisiti (`requiresHotelId`, `requiresPassword`, `requiresTokenAuthentication`), funzioni (`featureContent`, `octorateManageable`), `commissionIncludedInPayments`: base del catalogo MKT |
| Licenza e moduli | `license` (`ApiLicenseForm`) nella struttura; permesso `license` del partner; «Add licence» tra le operazioni admin di Postman, senza URL | moduli: tier del channel manager, `chat`, `invoice`, `fiscalPrinter`, `smartOffer`, `octoDaily` (pulizie), `rms`, `businessIntelligence`, `bookingEngine`, `octoSite`, `webConcierge`, `metaSearch`, `rateChecker`; attivazione da partner da confermare (D21) |
| Notifiche Octorate all'host | PATCH `/rest/v2/accommodation/{acc}/changeGoodMorningEmail` | la mail quotidiana di Octorate si spegne per non duplicare gli avvisi di CasaZen (D25) |
| Report | — | nessuna operazione di report o statistiche |

Restano fuori perimetro: operazioni su carte di credito, rete di strutture (`/accommodation/network`),
stanze PMS (`/rest/v1/pms`), creazione di portali API (`/rest/v1/portal/apiportal`, riservata a chi
riceve ARI), motore di prenotazione Octorate.

Helix deve riacquisire OpenAPI e collection con data, hash e provenienza, e generare il client
con un comando riproducibile; per ogni requisito registrare `operationId`, corpo, risposta,
sicurezza e scope, paginazione, errori, test e scostamenti. La presenza di un percorso non
dimostra che il partner CasaZen possa usarlo: lo dimostrano i permessi della configurazione
(`permissions`) e una prova.

### 10.3 Risposte alle domande tecniche D1–D14 (v1.3, invariate)

Le risposte D1–D14 della v1.3 (§5.1: annuncio da zero, campi di contenuto e CIN, messaggi,
risposte automatiche, recensioni, annunci esistenti, ARI e quota, prenotazioni, Stripe e
dirette, altri canali, titolarità e uscita, sandbox, convenzioni, costo e licenza) restano valide
e sono riportate integralmente qui sotto; le nuove domande D15–D27 sono in §14.2.

| ID | Domanda | Specifica | Resta da chiedere |
|---|---|---|---|
| D1 | Annuncio da zero via API e passaggi manuali (A.1) | Sequenza: proprietà (`POST /rest/v2/accommodation`) → camere e tariffe (`POST /rest/v3/roomrates/{accommodation}`, con il token della proprietà) → contenuti per prodotto e per connessione → connessione al portale (`POST /rest/v1/connection/{accommodation}`, verifica licenza e credenziali) → push (`pushQueue` o `.../content/connections/{connection}/push`). S6: push via API e collegamento OTA supportati se le camere esistono già. Passaggi manuali: per i portali OAuth il login lo completa l'utente su Octorate (`userLogged`, `loginAt`); Airbnb espone lo stato di approvazione del listing (`airbnbApprovalStatus`). | Passaggi manuali per ciascuna OTA; creazione su Expedia e HomeToGo |
| D2 | Campi di contenuto per OTA, lingue, CIN (A.2) | Testi per lingua con fallback e override per portale (`LanguageMap`, `PortalLanguageMap`: titolo, descrizione, regole, accesso, quartiere, trasporti). Lingue della proprietà: IT, EN, FR, ES, DE, RU, PT, NL, JA, EL, TR, ZH, CA, RO; le chiavi dei testi non sono enumerate. Foto per URL in coda con stato; letti in `rooms` (sottocamere con configurazioni letto); regole strutturate in `reservation` (bambini, fumatori, animali, eventi, orari, preavviso, prenotazione immediata) e testo in `houseRules`; servizi nell'inventario, codici per portale da `GET /meta/content`. CIN: nessun campo dedicato (`nrua` solo per la Spagna); candidati `legalId` e `legalIdIssuer` (camera) e `taxId` (listing). | Campi leggibili e scrivibili per OTA; in quale campo Airbnb, Booking.com, Vrbo ed Expedia ricevono il CIN |
| D3 | Messaggi: pre-booking, allegati, offerte speciali, latenza, limiti (A.3) | Lettura e invio sì: thread, messaggi con `createdAfter`, `recentMessages` (24 ore). S7 conferma API di messaggistica per Airbnb, Booking.com, Vrbo ed Expedia; gli altri canali usano contatti email/telefono/WhatsApp o chat Octorate. Verso le OTA: processor `CHANNEL` con `OTA_FLOW_DIRECTION` = `TO`. Ingresso: `CHAT_MESSAGE_RECEIVED`. Allegati JPEG, PNG e `OCTET_STREAM` (base64 in upload). Offerte speciali e pre-approvazione (`SPECIAL_OFFER`, `PREAPPROVAL`, ritiro). Stati del messaggio `CREATED`, `WAIT_AUTH`, `SENT`, `ERROR`, `MANUAL_VERIFY`; attributo di thread `THREAD_AUTHORIZATION_WINDOW_EXPIRE`. La licenza ha un flag `chat` e la connessione un flag `messages`: verificare che siano inclusi e attivi. Limite di frequenza: solo la quota generale (D7). | Pre-booking, allegati e offerte speciali per OTA; latenza; limiti per OTA; significato della finestra di autorizzazione |
| D4 | Risposte automatiche o AI ammesse (A.4) | Nessuna policy delle OTA. Octorate ha un proprio assistente AI per thread (`aiAssistant`, `aiDriven`, `AI_SKIP_DISABLE`): sui thread gestiti da CasaZen va tenuto disattivo. | Policy per Airbnb, Booking.com, Vrbo ed Expedia e permessi del partner |
| D5 | Recensioni: lettura e risposta (A.5) | Tre operazioni (OCT-12): lettura per prenotazione, risposta dell'host e recensione dell'host; le ultime due solo Airbnb. S6: le altre OTA non sono integrate. Il messaggio di chat può portare una `review`. | Eventuale roadmap per Booking.com, Vrbo ed Expedia |
| D6 | Annunci esistenti: mapping senza ricrearli, con ID, recensioni, storico (email 1) | Il flusso documentato parte dalle camere già presenti sul canale: connessione, `IMPORT_ROOMS` (camere dal canale, pronte per il mapping), `IMPORT_RESERVATIONS`, `IMPORT_CALENDAR`, mapping `productId` ↔ `externalId`, poi `MARK_CONNECTED`; `updateCalendar` e `pullReservations` si attivano solo dopo il mapping. | Conferma scritta, per OTA, che ID annuncio, recensioni, ranking e storico restano dopo il passaggio dal sistema attuale |
| D7 | ARI: valori, propagazione, quota (email 3) | Per camera e intervallo: `availability`, `price`, `minstay`, `maxstay`, `stopSells`, `closeToArrival`, `closeToDeparture`, `cutOffDays`; i valori gestibili per portale sono in `calendarValues`. Quota: 1 unità ogni 15 camera-giorno, dinamica per proprietà e tariffe (esempio della specifica: 5-6 aggiornamenti completi di 2 anni ogni 5 minuti, non un SLA); `quotaLimit` e `quotaStatus` in `GET /rest/v1/api/configuration`; `category` (sola lettura) fissa il tetto di quota in lettura. Il bulk restituisce id di processo (esempio Postman); lo stato per data e portale è in `GET /calendar/process/{id}` e `/calendar/failures`, solo in Postman, con esempi di stati `NEW`, `NOT_PROCESSED`, `ERROR`. Rinvio di un intervallo: `POST /connection/sync/...` (solo Postman). | Tempi di propagazione per OTA; quota per circa 250 strutture; conferma che gli endpoint solo Postman sono pubblici |
| D8 | Prenotazioni: eventi, API, riconciliazione, recupero (email 4) | Eventi `RESERVATION_CREATED`, `RESERVATION_CHANGE`, `RESERVATION_CANCELLED`, `RESERVATION_CONFIRMED` (S6 conferma nuove, modificate e cancellate). Riconciliazione: elenco con `type` `MODIFIED_BETWEEN`, `CREATETIME`, `CANCELLATION`, `STAY`, `size` fino a 200, `page` fino a 1000, `ids[]` per rileggere. Recupero: retry dei webhook fino a 2 giorni più rilettura. Ordine: `updateTime`; istanti in UTC, ricerche in ora di Roma. | Eventi effettivamente generati per ciascuna OTA; quando arriva `CONFIRMED` |
| D9 | Stripe e prenotazioni dirette (email 8) | CasaZen può creare la prenotazione diretta (`POST /reservation/{accommodation}`: decrementa la disponibilità salvo `availability=false`) e registrare l'incasso con `POST .../{id}/payment` («non è l'addebito della carta»; `transaction` = id esterno, `paymentMode`, `amount`). I campi di addebito automatico di Octorate (`scheduled*`, `preauth*`, `stopCron`) non si usano. Per le OTA `companyCollect` e `paymentType` indicano chi incassa. | `channelId` da usare per le dirette (l'esempio dei webhook mostra 233, «Octorate»); confine di responsabilità per iscritto |
| D10 | Altri canali (A.11) | Né e-domizil né feratel né Google Vacation Rentals compaiono in S5 (c'è Google Hotel Ads per gli hotel). Portali con metadati di contenuto negli esempi: Booking.com, Airbnb, Vrbo (`homeaway`), Expedia, HomeToGo, Holidu, Agoda, Rentals United, Rakuten, Marriott VR (`marriot_vr`). L'elenco completo è `GET /meta/portals`. S6: tutti i canali elencati sono disponibili tranne Google Vacation Rentals. | e-domizil e feratel non sono stati chiesti |
| D11 | Titolarità degli account, export, uscita (A.7) | Tecnicamente: la creazione con `ApiOperations` restituisce il token e crea un utente con l'email indicata (credenziali inviate se `welcome_user`); l'API member vede le proprietà create o collegate (`GET /accommodation`); `DELETE .../account` disattiva senza cancellare; `PATCH .../changeAdminUserAccess` autorizza o revoca l'accesso degli amministratori Octorate agli utenti della proprietà. Export via GET: contenuti, camere e tariffe, calendario, prenotazioni, mapping, ospiti. Le recensioni stanno sull'annuncio OTA, non in Octorate. | Titolarità contrattuale degli account e dei contratti OTA; uscita senza perdere le recensioni |
| D12 | Sandbox (A.8) | `environment` `SANDBOX`/`PRODUCTION` in `GET /rest/v1/api/configuration`, stesso host, 403 `WrongEnviroment` se si sbaglia ambiente, proprietà di prova con `sandboxProperty`. S6: rete di test non collegabile ai portali, senza prova di push. S7: i dati ospite sensibili non sono accessibili finché gli IP statici non sono in whitelist e non esiste un'alternativa in sandbox. S9–S11: account del network test attivato, credenziali utente e partner consegnate, accesso API limitato alle proprietà marcate test; callback iniziale `https://localhost/`, da sostituire e mettere in whitelist. S10 contiene anche un avviso di credenziali scadute, quindi l'autenticazione riuscita resta il criterio di validità. | URL callback CasaZen di test; se il network consente thread chat e prenotazioni di prova; procedura e tempi di whitelist IP; validità effettiva delle credenziali consegnate |
| D13 | Convenzioni: idempotenza, paginazione, date, valute | Nessun header di idempotenza né ETag; alcune PATCH e PUT rispondono 304 se il contenuto è identico. Paginazione `page`/`size` (prenotazioni fino a 200 per pagina e pagina fino a 1000; calendario fino a 20 prodotti); `ApiListResponse` con link. `dateTo` del calendario è inclusivo; ricerche in ora di Roma; `checkin` e `checkout` in UTC. Valuta nella connessione e nella prenotazione, non nel calendario; `SpecialOfferDTO.totalPrice` è un intero con unità non documentata. Errori `ApiError` con `code` stabile. | Unità di `totalPrice` |
| D14 | Costo e licenza (A.6) | Nessun prezzo in S5. La licenza è per proprietà (`ApiLicenseForm`: `channelManager` `FREE`/`BASIC`/`PRO`/`ENTERPRISE`/`PREMIUM`, flag separati per `chat`, `smartOffer`, `invoice` e altri, `roomsLimit`, `expireDay`) e `createConnection` la verifica; `GET /rest/v1/api/configuration` prevede un account reseller. Per il solo perimetro CasaZen di appartamenti indipendenti, S7 riporta: OTA Integration **€130 base + €0,50/proprietà**; Messaging **€1/proprietà**. Le tariffe hotel sono fuori perimetro. | Frequenza e IVA; conferma che il base €130 si applichi al ramo proprietà e come; tier e flag inclusi (almeno chat e contenuti); decorrenza, minimi e condizioni da reseller |

### 10.4 Correzioni

Rispetto alla v1.0 e agli allegati S3/S4 (v1.3 §5.2): `GET /calendar/process/{id}` solo in
Postman; `GET .../reservation/{acc}/search` è il motore di prenotazione; `GET .../reservation/{acc}/{id}`
e `amenities` deprecati; `addTextToHouseRules` non scrive le regole; recensioni con tre
operazioni, risposta solo Airbnb; webhook anche per chat e contenuti; quota esaurita = 403
`ApiQuotaException`, nessun 429; rete di test senza OTA; inventario di 208 operazioni.

Rispetto alla v1.3: le operazioni di check-in, ospiti, fatture, pagamenti, prodotti extra e
politiche non sono più fuori perimetro (§10.2); il resoconto S7 va corretto o confermato in tre
punti (esito per ospite dell'invio alla Polizia, calcolo della tassa, creazione di documenti
fiscali): §9.2.

## 11. Matrice delle capacità

### 11.1 Per canale (v1.3, aggiornata il 31/10)

Stati secondo il vocabolario di §0; «documentato (S6)» = affermazione scritta del fornitore
senza prova. ARI, restrizioni ed eventi di prenotazione sono documentati in modo generale, non
per canale; mapping esistente, CIN, allegati, offerte speciali, pre-approvazione e AI automatica
sono sconosciuti.

| Canale | Collegamento | Contenuti e push | Recensioni | Messaggi |
|---|---|---|---|---|
| Airbnb | documentato (S5–S7) | documentato (`airbnb_xml`, `airbnbApprovalStatus`) | lettura, risposta e recensione dell'host documentate | API documentata (S7); funzioni specifiche da provare |
| Booking.com | documentato (S5–S7) | documentato (`booking_xml`) | non supportato | API documentata (S7); cambio date solo da extranet |
| Vrbo | documentato (S5–S7) | documentato (`homeaway`) | non supportato | API documentata (S7) |
| Expedia | documentato (S5–S7) | sconosciuto: assente da `pushQueue`, presente nei metadati | non supportato | API documentata (S7) |
| HomeToGo (opzionale) | documentato (S5–S7) | sconosciuto | non supportato | nessuna API OTA confermata |
| Holidu (opzionale) | collegamento documentato (S7) | sconosciuto | non supportato | nessuna API OTA confermata |
| Google Vacation Rentals (opzionale) | non supportato in Europa (S6) | — | — | — |

La rete di test non è collegabile ai portali (S6): nessuna cella con effetti sulle OTA può
diventare `provato sandbox`; l'evidenza arriva da una conferma scritta per canale o dalla
produzione (G3/G4).

### 11.2 Per processo (nuova)

| Processo | Capacità tramite Octorate | Stato al 31/10 | Fonte | Se manca |
|---|---|---|---|---|
| Annunci | contenuti, foto, servizi, unità, tariffe, regole, push per canale | documentato | S5, S13 | — (CIN ed Expedia: D2) |
| Annunci | traduzione delle descrizioni mancanti | documentato | S13 | AI di CasaZen (AI-02) |
| Check-in | scrittura degli ospiti con dati Alloggiati e statistici | documentato | S7, S13 | — |
| Check-in | stato check-in, check-out, no show | documentato | S13 | solo in CasaZen |
| Alloggiati | invio automatico da Octorate | documentato, dipende da `policeAccount` configurato | S7, S13 | client CasaZen (Q1-14) o file manuale |
| Alloggiati | esito per ospite (`policeSentStatus`, `policeSentTime`) | documentato in S13, smentito da S7 | S7, S13 | prova guidata (CHK-04) |
| Alloggiati | ricevuta PDF | non supportato via Octorate | S7, S13 | `Ricevuta` del web service della Polizia (S17) oppure caricamento |
| ISTAT | invio da Octorate | documentato in generale; regioni sconosciute | S7 | file di CasaZen (Q1-15) o portale regionale |
| ISTAT | esito | non supportato | S7 | prova guidata e caricamento (CHK-05) |
| Tassa di soggiorno | importo ed esenzioni per ospite e prenotazione | documentato | S13 | calcolo CasaZen (CHK-07) |
| Tassa di soggiorno | configurazione delle regole via API | non trovato | S13 | regole in CasaZen |
| Fatture | lettura con stato SDI e PDF | documentato | S7, S13 | — |
| Fatture | creazione | contraddittorio (S13 sì, S7 no) | S7, S13 | emissione CasaZen (FIS-02) |
| Pagamenti | lettura e registrazione sulla prenotazione | documentato | S5, S13 | — |
| Ospiti e contatti | rubrica; contatti con permesso speciale e IP statici | documentato | S7, S13 | dati raccolti da CasaZen nel check-in |
| Marketplace | portali e strumenti esterni attivabili | documentato | S13 | — |
| Marketplace | elenco e attivazione delle app dei partner | non supportato | S13 | catalogo curato da CasaZen (MKT-01) |
| Marketplace | moduli in licenza | documentato in lettura; scrittura da confermare | S13 | richiesta guidata (MKT-05) |
| Report | statistiche e report | non supportato | S7, S13 | calcolo CasaZen (REP) |

Ogni cella `documentato` diventa `provato sandbox` solo con una prova registrata (G2) e resta
spenta in live fino ad allora.

## 12. Pacchetti Helix, dipendenze e prove di uscita

I nomi sono pacchetti di lavoro, non comandi di Helix. Ogni pacchetto chiude un percorso
completo, dal dato alla schermata, dimostrabile con l'adattatore Mock.

| Pacchetto | Dipende da | Contenuto | Prove di uscita |
|---|---|---|---|
| S0 — Sicurezza e privacy | — | DEB-01 | Prove di DEB-01; nessuna dipendenza da Octorate; da fare subito |
| H0 — Gap e contratti | — | Confronto con il codice corrente per le otto aree, per i wizard esistenti (elenco delle uscite, UX-02) e per lo stato reale della fase 1 (§4.5); snapshot API o blocco esplicito; ADR di §8.4; matrice delle capacità; autorità dei dati; backlog requisito → operazione → prova | Backlog completo; nessun requisito senza fonte |
| U0 — Livello guidato e «Da fare» | H0 | UX-01…UX-13 (UX-15 dopo): livello guidato, bozze sul server, sotto-passi, indirizzi dei passi, servizio unico «Da fare», preferenze e avvisi (push, email, popup; DEB-07); conversione dei wizard esistenti al livello e rimozione delle uscite | UX-14 sui wizard esistenti; nessuna dipendenza da Octorate |
| H1 — Fondazioni del connettore | H0 | DEB-02 (bus di eventi con outbox) e OCT-01…03, 08, 09, 13, 14: adattatore Mock e client, OAuth per proprietà, webhook in entrata e riconciliazione sul bus, resilienza, interruttori, stato delle connessioni | Isolamento per tenant e proprietà; nessuna chiamata live dal Mock; duplicati e fuori ordine senza effetti doppi |
| H2 — Annunci e ARI | H1, U0 | LST-01…12 | Import, creazione, modifica e pubblicazione per canale in Mock, con errori parziali per canale |
| H3 — Prenotazioni e calendario | H1 | BKG-01…09 | Creazione, modifica e cancellazione in Mock; webhook duplicati, persi e fuori ordine; nessun doppio scrittore con iCal |
| H4 — Check-in e adempimenti | H3, U0 | CHK-01…11, DEB-08, DEB-09 | Dati dell'ospite fino a Octorate in Mock; mittente unico; ricevuta dal server finto Alloggiati; tassa al centesimo; nessun codice d'accesso senza identificazione registrata |
| H5 — Messaggi e AI | H3, U0 | MSG-01…11, AI-06, DEB-03, DEB-06 | Hub con thread OTA simulati e diretti; soglia di confidenza, categorie sensibili, approvazione da push |
| H6 — Documenti, clienti e report | H3 (H4 per la tassa) | FIS-01…07, CRM-01…08, REP-01…08, DEB-04, DEB-05, DEB-10 | Emittente unico; documenti Octorate simulati letti; pubblico raggiungibile per base giuridica; report coerenti con le prenotazioni |
| H7 — Marketplace | H1, U0 | MKT-01…07 | Catalogo dalle tre fonti; attivazione e disattivazione di un portale simulato; stato |
| H8 — Sandbox | H2–H7, credenziali valide | Prove del contratto API con l'account test (ex H5 della v1.3): OAuth e rinnovo, letture prima delle scritture, errori 400/401/403/404/422, 403 di quota, 550, timeout, webhook | Rapporto per capacità; le celle con effetti sulle OTA restano «da provare in produzione» |
| H9 — Preparazione del pilota | H8, approvazioni | Import in sola lettura, confronto dei mapping, convivenza con il sistema attuale, rollback, soglie (ex H6) | G3 |
| H10 — Osservazione del pilota | H9, autorizzazione | 5–10 proprietà per 30 giorni (proposta S3/S4), controlli e rapporti (ex H7) | G4 |

**Si può iniziare subito, senza Octorate:** S0 (prima di tutto), U0 per intero; DEB-02 e DEB-05;
CHK-07 (calcolo della tassa, già in CasaZen); FIS-02/05 per l'emissione di CasaZen; REP sui dati
già presenti; CRM-02/03/06/08 (base giuridica dei contatti e correzioni delle campagne); MSG-05
(preferenze di notifica) e l'hub sui thread diretti esistenti. Tutto il resto parte con
l'adattatore Mock. DEB-11 segue le priorità della fase 1, non questo documento.

Ogni PR cita gli identificativi dei requisiti, aggiorna matrice e prove, documenta migrazioni e
configurazione, resta nel suo pacchetto e non attiva la produzione. Report separati per prove
del simulatore e prove contrattuali ufficiali.

## 13. Gate di accettazione

- **G0 — Design.** Contratti interni, autorità dei dati, ADR, backlog e blocchi esterni registrati.
- **G1 — Demo.** Tutti i requisiti P0 dimostrabili in Mock con l'«Agenzia DEMO» (Q1-27), errori e
  ripristino compresi; prove UX-14 verdi su ogni wizard; simulazione sempre dichiarata.
- **G2 — Sandbox.** OAuth e capacità disponibili provate con evidenze; rapporto separato delle
  capacità mancanti. La rete di test non collega le OTA (S6): contenuti, ARI, prenotazioni da OTA,
  messaggi e recensioni sulle OTA risultano «da provare in produzione».
- **G3 — Pronti al pilota.** Autorizzazione commerciale e operativa, proprietà e durata concordate,
  mapping verificati, permessi API necessari (compresi `checkIn`, `invoices`, `contactInfo` se
  usati), mittente degli adempimenti deciso per ogni immobile, rollback e responsabilità definiti;
  prova d'uso del PO con almeno cinque persone non esperte su: collegamento dei canali, modifica
  di un annuncio, check-in con invio, risposta a un messaggio, documento fiscale.
- **G4 — Fine pilota.** Nessun mapping errato, prenotazione persa o duplicata non risolta;
  nessuna scadenza di legge mancata per un avviso non partito; invio e ricezione dei messaggi
  verificati sui canali autorizzati; politiche AI rispettate; quote sostenibili. Latenze, finestre
  di riconciliazione, soglie degli allarmi e tempi di recupero quantificati e approvati a G3.

Rollback: fermare i nuovi comandi, drenare e riconciliare le operazioni pendenti, conservare
audit e mapping, riabilitare lo scrittore precedente solo dopo un confronto; non cancellare
annunci o prenotazioni per ripristinare uno stato. Nessun contatto esterno, merge o messa in
produzione fa parte della consegna di questo documento.

## 14. Decisioni del PO e domande a Octorate

### 14.1 Decisioni del PO, con default

Helix procede con il default; una decisione diversa del PO cambia solo i requisiti indicati.

| ID | Domanda | Default proposto | Requisiti | Blocca |
|---|---|---|---|---|
| PO-01 | Il channel manager si nomina nell'interfaccia? | No, tranne dove l'utente deve agire sul suo sito (consenso, prove non disponibili via API), con una dicitura neutra («il tuo channel manager») e il nome solo nella pagina di consenso | UX-12, LST-02, CHK-04 | No |
| PO-02 | Chi invia ad Alloggiati per gli immobili collegati a Octorate? | Octorate (mandato S12); CasaZen scarica la ricevuta ufficiale con `Ricevuta` se l'host gli affida le credenziali; il client di CasaZen (Q1-14) resta per gli immobili non collegati; **mai due mittenti automatici** per lo stesso immobile | CHK-03, CHK-04 | No |
| PO-03 | Chi invia i flussi ISTAT? | Octorate dove copre la regione (D15); file di CasaZen (Q1-15) o portale regionale altrove | CHK-05 | No |
| PO-04 | Chi calcola la tassa di soggiorno? | CasaZen (regole e tariffe per comune già presenti) e scrive importo ed esenzione per ospite in Octorate; avviso se Octorate calcola un valore diverso | CHK-07 | No |
| PO-05 | Chi emette i documenti fiscali delle prenotazioni? | CasaZen; se l'organizzazione usa già la fatturazione di Octorate, CasaZen la legge e non emette; mai entrambi per la stessa organizzazione | FIS-02 | No |
| PO-06 | Quale intermediario SDI per l'invio reale delle fatture elettroniche emesse da CasaZen? | Nessun invio reale finché non è scelto: XML generato, validato e scaricabile, stato «da inviare» | FIS-05 | Solo l'invio reale |
| PO-07 | Email di marketing ai clienti diretti e agli ospiti che hanno dato l'email a CasaZen nel check-in senza consenso esplicito («soft opt-in», art. 130 c. 4 Codice privacy) | **Sì**, con informativa al momento della raccolta, solo per servizi analoghi (soggiorni), disiscrizione in ogni messaggio; cambia il default D-10 della fase 1; ⚖ conferma del legale prima dell'attivazione in produzione | CRM-02, CRM-03, CRM-06 | Solo l'attivazione in produzione |
| PO-08 | Invio automatico delle risposte AI | Acceso solo per le situazioni che l'organizzazione ha configurato, sopra la soglia (default 0,85), mai per le categorie sensibili e mai dove la policy del canale non è confermata (D4 della v1.3); periodo iniziale di due settimane in sola bozza per ogni nuova situazione, con la percentuale di bozze approvate senza modifiche come misura | MSG-06, MSG-07 | No |
| PO-09 | Dati personali verso il modello AI per rispondere ai messaggi (la regola attuale è «nessun dato personale nei prompt») | Modello ospitato nell'UE con accordo sul trattamento (D8 della v10); minimizzazione: nomi, contatti, codici e documenti sostituiti da segnaposto; testo del messaggio inviato perché necessario; ⚖ informativa | MSG-07, AI | Solo l'attivazione dell'AI sui messaggi |
| PO-10 | Avvisi predefiniti | Push e contatore in-app per i nuovi messaggi degli ospiti e per le scadenze di legge; riepilogo email giornaliero; popup solo per scadenze entro 24 ore | UX-08, MSG-05 | No |
| PO-11 | Chi cura il catalogo dei partner e con quali partner si parte? | L'admin di piattaforma di CasaZen; all'avvio solo i portali e gli strumenti letti via API più i servizi CasaZen; i partner terzi quando Octorate fornisce l'elenco (D16) | MKT-01, MKT-06 | No |
| PO-12 | CasaZen attiva i moduli in licenza di Octorate (chat, fatture, pulizie…) per conto dell'host? | Solo come richiesta guidata finché il modello commerciale (rivenditore o no) non è deciso con Octorate (D21) | MKT-05 | No |
| PO-13 | Strutture con più unità (più appartamenti nello stesso edificio) | Un annuncio = una unità; più unità in una struttura dopo il pilota (gli hotel restano esclusi, v1.3) | LST-08 | No |
| PO-14 | Notifiche push anche nel browser (oltre all'app) | Sì, facoltative, chieste solo dopo il primo avviso utile | UX-08 | No |
| PO-15 | Foto degli annunci raggiungibili da Octorate tramite URL pubblico | Sì, solo per le foto degli annunci (sono già pubbliche sulle OTA), con indirizzi non indovinabili; i documenti degli ospiti restano privati | LST-06 | No |
| PO-16 | Chi comanda prezzi e disponibilità? | CasaZen è l'unico scrittore per gli immobili collegati; le regole automatiche di Octorate (chiusure automatiche, prezzi derivati, revenue) restano spente o lette e mostrate | LST-09, LST-10 | No |
| PO-17 | Dati degli ospiti presi da Octorate per il CRM | Solo dati della prenotazione e del soggiorno; i contatti letti con `contactInfo` solo per il servizio (messaggi, check-in, fattura), mai per il marketing | CRM-07 | No |
| PO-18 | Come si identifica l'ospite dopo la decisione del Consiglio di Stato del novembre 2025 (da verificare con il legale)? | L'host o lo staff registra in CasaZen l'identificazione di persona o in videochiamata per ogni ospite adulto; la verifica biometrica dal vivo si aggiunge quando Q1-11 sarà reale; il codice d'accesso parte solo dopo; il self check-in senza passo dal vivo è segnalato come non conforme ⚖ | CHK-11, DEB-09 | Solo i testi verso gli utenti |
| PO-19 | Quando CasaZen crea una struttura nuova su Octorate, il proprietario riceve da Octorate le credenziali del suo account (`welcome_user`)? | **Deciso dal PO il 31/10:** no. CasaZen crea la struttura con `welcome_user` falso, l'esperienza resta in CasaZen e il token arriva con la creazione; le credenziali si attivano solo su richiesta del proprietario o alla sua uscita da CasaZen, con la procedura da concordare con Octorate (D11, D27) | §9.1, LST-02, LST-03 | No |

### 14.2 Domande a Octorate (si aggiungono a D1–D14)

| ID | Domanda | Perché serve |
|---|---|---|
| D15 | Quali sistemi regionali ISTAT invia Octorate (per regione) e come si configurano; esiste un esito leggibile? | CHK-05, PO-03 |
| D16 | Esiste un elenco dei partner del vostro marketplace (il «Partner Hub» pubblico) leggibile da un partner, con requisiti, modalità di attivazione e stato per proprietà? | MKT-01 |
| D17 | `policeAccount` (codice, chiave del web service, `autorun`, numero della struttura per camera) si può scrivere via API con il consenso del proprietario? Cosa significa `valid`? Alloggiati, ISTAT e tassa di soggiorno richiedono il vostro modulo gestionale (costo, licenza)? | CHK-03 |
| D18 | `policeSentStatus` e `policeSentTime` sono valorizzati per ogni invio, anche per gli errori? Esiste o è prevista la ricevuta via API? Il resoconto del 30/10 dice che gli esiti sono solo nella vostra interfaccia | CHK-04 |
| D19 | `createDoubleHeadingInvoice` e `issueSummaryDocument` sono utilizzabili da un partner? Con quali permessi e licenza? Il resoconto del 30/10 dice che non ci sono API di creazione | FIS-02 |
| D20 | Come calcolate `touristTax`? Le regole si configurano via API? Se il partner scrive `cityTaxPrice` e `cityTaxExemption` per ospite, prevalgono sul vostro calcolo? | CHK-07 |
| D21 | Un partner può attivare o cambiare la licenza di una proprietà (moduli `chat`, `invoice`…)? Con quale modello commerciale? | MKT-05, PO-12 |
| D22 | Quale `category` è assegnata a CasaZen e quale tetto di quota in lettura ne deriva? | OCT-13, quote |
| D23 | Quali sono oggi i portali `EXTERNAL_TOOL` e come si collegano? | MKT-01 |
| D24 | Il permesso `contactInfo`/`sensitiveData` per CasaZen: condizioni, vincoli d'uso, procedura per gli IP statici | CRM-07, MSG |
| D25 | Oltre alla mail del mattino, quali notifiche Octorate riceve l'host e come si spengono per gli immobili gestiti da CasaZen? | UX-08, MSG-05 |
| D26 | Lo stato `CHECKIN_END`, `CHECKOUT_END`, `NO_SHOW` ha effetti verso le OTA o verso gli invii? | CHK-06 |
| D27 | Una struttura creata via API con `welcome_user` falso: come si consegnano in seguito le credenziali al proprietario (su sua richiesta o quando lascia CasaZen)? Esiste un'operazione o una procedura, e il proprietario conserva struttura, collegamenti alle OTA e storico? | PO-19, D11 |

## 15. Revisioni

- **2.0 · 31 ottobre 2026:** nuovo mandato del PO (S12): da solo channel management a otto
  processi con CasaZen unico punto di accesso. Aggiunti: base del codice al 30/10 (§4), regole UX
  vincolanti con l'aggiunta del 31/10 (wizard su livello separato, mai fuori dal processo, «Da
  fare», push e popup) (§5), requisiti per processo (§6), mappa delle automazioni AI (§7),
  autorità dei dati estesa (§8.2), decisioni PO-01…19 e domande D15–D27 (§14), benchmark UX
  (Appendice A). Verifica della specifica del 31/10 (S13): check-in e Polizia, ospiti, tassa di
  soggiorno, fatture con stato SDI, pagamenti, prodotti extra, politiche, licenze, portali e
  strumenti esterni; tre punti in contrasto con il resoconto S7 (esito per ospite dell'invio alla
  Polizia, calcolo della tassa, creazione di documenti fiscali) da confermare per iscritto.
  Cambio di una decisione precedente: per gli immobili collegati a Octorate l'invio ad Alloggiati
  passa a Octorate (la v10 D12 lo assegnava a CasaZen); il client di CasaZen (Q1-14) resta per
  gli altri immobili e per scaricare le ricevute (PO-02). Pacchetti riorganizzati: S0, U0 e
  H0–H10. Dal benchmark: identificazione dell'ospite dopo la decisione del Consiglio di Stato del
  novembre 2025 riportata dalle fonti (CHK-11, PO-18, ⚖), piano del processo e informazioni dentro
  il livello guidato (UX-01), «fallo per me» (UX-15), lezioni e livelli di fiducia dell'AI
  (MSG-06, MSG-07), messaggi programmati visibili (MSG-11). Testo integrale di S9 confermato dal
  PO il 31/10: modello del link, scadenza letta dal token, riferimenti, due ingressi (consenso per
  le strutture già su Octorate, token alla creazione per quelle nuove) e PO-19, decisa dal PO lo
  stesso giorno (struttura creata con `welcome_user` falso, credenziali solo su richiesta o
  all'uscita; domanda D27); fonti S1–S11 riportate per intero in §3.
- **1.3 · 30 ottobre 2026:** integrate le email S9–S11 senza riportare segreti: host e base API,
  authorization code flow, whitelist delle callback, access token di 24 ore, refresh token senza
  scadenza, N proprietà = N consensi e token; account test attivo, credenziali non marcate valide;
  perimetro e listino ristretti agli appartamenti indipendenti, hotel esclusi.
- **1.2 · 30 ottobre 2026:** integrate S7 e S8: esito della partnership, copertura API, IP statici
  per i dati sensibili, messaggistica per Airbnb, Booking.com, Vrbo ed Expedia, confini di
  compliance, documenti fiscali, reporting e recensioni, Holidu, offerta commerciale.
- **1.1 · 23 ottobre 2026:** verifica contro S5 (164 percorsi, 208 operazioni) e S6: recensioni,
  eventi webhook e consegna, CIN, quota ed errori, sandbox; risposte D1–D14, correzioni e matrice.
- **1.0 · 21 ottobre 2026:** prima stesura.

## Appendice A — Benchmark UX (31/10/2026)

**Metodo e limiti.** Ricerca web del 31/10/2026 su documentazione e pagine pubbliche, senza accesso
ai prodotti. Sigle: [D] documentazione, [M] marketing del fornitore, [3P] fonte terza, † pagina non
leggibile direttamente (riassunto del motore di ricerca). Non leggibili: centri di assistenza di
Guesty, Guesty for Hosts, Hostaway e Smoobu, `partner.booking.com`, gran parte di `lodgify.com`; per
Octorate si è usata la community (articoli forse datati). Molte funzioni AI del 2026 sono annunciate
o in prova. Una pagina del blog di Chekin conteneva istruzioni nascoste per gli assistenti AI: sono
state ignorate. I riferimenti tra parentesi quadre rimandano a A.6.

### A.1 Cinque risultati che cambiano i requisiti

1. **Nessuno documenta il wizard come livello sovrapposto.** I flussi documentati sono pagine
   dedicate e riprendibili (bozza nel wizard degli annunci di Octorate [O10], «continua la
   registrazione» di Booking.com † [B1]); le linee guida sui wizard avvertono che un wizard modale
   può nascondere informazioni che servono [X3]. Da qui UX-01 punti 5 e 6: livello a tutto schermo
   con le informazioni utili dentro, passi visibili, «Salva ed esci» sempre presente.
2. **Identificazione dell'ospite.** Le fonti [X1][X2] riportano la decisione del Consiglio di Stato
   del 21/11/2025: identificazione di persona; una verifica a distanza dal vivo può bastare, foto
   del documento più codice automatico no. Il numero della sentenza differisce tra le fonti (5732
   [X1], 9101/2025 [C3]). Solo Chekin e Vikey documentano un passo dal vivo [C3][V1]. Da qui CHK-11
   e PO-18 (⚖, non è un parere legale).
3. **Adempimenti italiani: modulo del gestionale o partner.** Octorate li fa nel suo gestionale con
   una configurazione tecnica [O7][O9][O11]; Lodgify e Smoobu rimandano a Chekin [L1][S1]; il
   collegamento Chekin–Octorate importa solo le prenotazioni [C4]. Due mittenti per lo stesso
   immobile producono schedine doppie (inferenza): da qui CHK-03.
4. **Prerequisiti a metà percorso.** I flussi migliori rimandano ciò che può aspettare (il CIN nel
   wizard di Octorate [O10]) o dicono «non ancora possibile» con il motivo [X4]; i peggiori mandano
   l'utente altrove (creare prima l'annuncio su Airbnb † [G4], nuovo account per gli utenti Octorate
   [C4]). Da qui UX-02 e LST-03.
5. **Marketplace.** Nessuno documenta lo stato per partner né cosa succede disattivando: MKT-04 e
   MKT-05 vanno progettati da CasaZen.

### A.2 Cosa fanno le piattaforme

| Piattaforma | Guida e attività | Messaggi e AI | Adempimenti e marketplace |
|---|---|---|---|
| Guesty (e Guesty for Hosts) | Import guidato degli annunci Airbnb con stati per annuncio e riprova; pulsante «importa» visibile finché non si importa † [G4] | Suggerimenti AI inviati o modificati dall'host † [G1]; dal 2026 invio automatico con soglie, categorie, regole per immobile ed escalation [M][G2]; agenti per attività, traduzioni, prezzi, prenotazioni a rischio, risposte alle recensioni, report [M][G3] | Marketplace con ricerca e pagina del partner; «collega» crea un token da copiare dal partner † [G5]; elenco pubblico di circa 350 partner [G6] |
| Airbnb (host) | Scheda «Oggi» con promemoria e avvisi; richieste con conto alla rovescia di 24 ore [A1][A2]; creazione dell'annuncio in tre fasi [A6] | Messaggi programmati per evento, nulla per default, linea per prenotazione con Modifica, Salta, Invia ora e avviso se saltati [A3]; notifiche per categoria e canale, quelle legali e di sicurezza non disattivabili [A4]; risposte AI dai dati dell'annuncio, etichettate, da maggio 2026 [A5] | — |
| Lodgify | — | Assistente AI con modifica prima dell'invio [3P][L6]; co-host AI che segnala solo ciò che serve decidere, prezzi dinamici, in prova [3P][L3] | Guida Alloggiati e CIN; check-in tramite Chekin [L1][L2]; collegamento dei canali con mappatura fatta dal loro team † [L5]; marketplace con chiave API da copiare † [L4] |
| Krossbooking | Demo di 30 giorni con istruzioni al primo accesso [K1] | — | Invio schedine automatico, report ISTAT, tassa automatica con report trimestrale [M][K1]; portale dell'ospite con passi scelti per canale e appartamento (condizioni, firma, dati, documento, selfie facoltativo, extra, tassa) e conferma dell'host in un clic [M][K2] |
| Dolcebot | Avvio fatto dal loro team dopo un'intervista sull'immobile [M][D1] | Assistente AI su WhatsApp, Booking e Airbnb; le eccezioni vanno in un pannello prioritario; nessuna approvazione descritta [M][D1][D3] | Linea temporale per prenotazione: Alloggiati tracciato, ISTAT giornaliero, tassa per notte con ricevuta e rendiconto per il comune, promemoria per i dati mancanti, codice della porta solo a documenti e tassa completi [M][D2]; documenti fiscali alla conferma [M][D5]; nessun passo dal vivo [D4] |
| Octorate | Wizard degli annunci in sei passi con bozza, duplicazione (senza foto), CIN rimandabile, regole dei portali nel passo [D][O10] | Inbox unica per OTA, WhatsApp ed email [O5]; agente AI dei messaggi con escalation per urgenza, etichetta AI o persona, «lezioni» approvate con un ambito [M][O6] | Alloggiati come pagina di impostazioni: credenziali, chiave del web service, numero della struttura per camera, «Test di connessione», «Attivata», invio giornaliero; oltre 30 notti a mano; file di riserva [D][O7][O8]; ISTAT per regione con avviso se non supportata, esito per email, storico [D][O9]; tassa con regole per struttura ed esportazioni [O11]; Partner Hub per categorie con modulo di richiesta, senza stato [O4] |
| Chekin | — | — | Link all'ospite, dati, documento e selfie (premium), firma, pagamenti; revisione dell'host prima dell'invio [M][C2]; Alloggiati entro 24 ore, ISTAT entro il 5, tassa con PayTourist, ricevute archiviate per 5 anni [M][C1]; non garantisce il riconoscimento di persona [C3] |
| Vikey | — | — | Link dopo la prenotazione, documento, pagamento anche della tassa, firma, videochiamata di benvenuto facoltativa [M][V1]; prezzo per unità [V2] |
| Smoobu | Percorso dei primi 30 giorni descritto in un articolo, senza wizard [S2] | AI tramite strumenti esterni [S3] | Nessun adempimento nativo, rimanda a Chekin; tabella dei portali ISTAT per regione [S1] |
| Hostaway | — | Risposte AI in bozza per default, automatiche per argomento a scelta; base di conoscenza per annuncio, regole, prova in sandbox, filtro sul tono con escalation [M][H1][H2] | Marketplace con oltre 200 partner in categorie; chiave API generata dal cliente [H3] |
| Booking.com (extranet) | Registrazione salvata a ogni passo, «Continua» dalla home ed email di promemoria; lista di controllo nella home † [B1]; opportunità con «Agisci», «Scopri», «Forse più tardi» † [B2] | Notifiche sì o no per tipo, quando e come † [B3]; risposte automatiche dal 2025 [3P][B4] | — |

### A.3 Pattern adottati e dove

| # | Pattern | Requisito | Esempi |
|---|---|---|---|
| 1 | Piano del processo con stati e «non ancora possibile», controllo finale | UX-01 p. 6 | [X4], [O10] |
| 2 | Livello con passi visibili, salvataggio continuo, «Salva ed esci» | UX-01, UX-03 | [X3], [O10], [B1] |
| 3 | Ripresa dalla scheda «Da fare» | UX-03, UX-07 | [B1], [G4] |
| 4 | Prerequisito come sotto-passo; rinvio di ciò che può aspettare | UX-02, LST-03 | [O10], [X4] |
| 5 | Prima importa, poi scrivi; avanzamento e riprova per elemento | LST-02 | [G4], [O1], [O10] |
| 6 | Corsia «fallo per me» | UX-15 | [D1], [O1], [L5] |
| 7 | Valori predefiniti da regione e comune | CHK-05, CHK-07 | [O9], [C1], [S1] |
| 8 | «Oggi» con poche schede per scadenza e un pulsante | UX-07 | [A1], [A2], [O3], [L3] |
| 9 | Escalation con conto alla rovescia | UX-08, CHK-04 | [A1], [A4], [L1], [C1] |
| 10 | Successo silenzioso, eccezione evidente | UX-08 | [A3], [O9], [O6] |
| 11 | Pochi gruppi di notifiche; obblighi non silenziabili; consigli spenti; riepilogo giornaliero | UX-08, MSG-05 | [A4], [B3] |
| 12 | Prova dell'invio come oggetto visibile, archivio di 5 anni | CHK-04, CHK-08 | [C1], [L1], [O8], [D2] |
| 13 | Passo di prova del collegamento | CHK-03, MKT-03 | [O7], [O8] |
| 14 | Il sistema rincorre l'ospite, l'host conferma con un clic | CHK-01 | [D2], [K2], [C2] |
| 15 | Registro dell'identificazione dal vivo | CHK-11 | [C3], [V1], [X1], [X2] |
| 16 | Linea dei messaggi programmati con Modifica, Salta, Invia ora | MSG-11 | [A3] |
| 17 | Tre livelli di fiducia dell'AI, prova in sandbox, etichetta AI o persona, lezioni con ambito | MSG-06, MSG-07 | [H1], [H2], [G2], [O6], [A5] |
| 18 | Marketplace per lavori da fare: scheda, collegamento, stato, testo di disattivazione | MKT-01…05 | [H3], [G5], [O4] |

### A.4 AI: cosa fa il mercato

| Processo | Opportunità | Modalità consigliata | Esempi | In CasaZen |
|---|---|---|---|---|
| Annunci | Bozza di titolo, descrizioni e dotazioni da indirizzo, foto o annuncio esistente; controllo delle regole dei portali | Propone e approvi | [A8] (solo USA per ora), [G3], [O10] | AI-01, AI-03 |
| Traduzioni | Lingue mancanti dal testo approvato, segnalate come tradotte | Automatica | [G3] | AI-02 |
| Prezzi | Suggerimenti di prezzo e soggiorno minimo; automatico solo tra minimo e massimo dell'host | Propone e approvi | [A9], [G3], [L3] | AI-04 |
| Prenotazioni | Riepilogo della richiesta con rischi, dati mancanti, solleciti all'ospite | Automatica la segnalazione, approvi la decisione | [A1], [G3] | AI-07 |
| Check-in e adempimenti | Precompilazione dal documento, controlli, promemoria, invii, tassa, nuovi tentativi | Automatica; identità ed errori con approvazione | [D2], [O7], [O9], [C1], [V2] | AI-05 |
| Messaggi | Risposte di routine per argomento con escalation; il resto in bozza | Automatica per argomento, bozza per default | [H2], [A5], [B4], [G2], [O6] | AI-06, MSG-07 |
| Attività | Attività da messaggi, recensioni e partenze | Automatica la creazione, propone l'incaricato | [G3], [D1], [H1] | AI-07 |
| Recensioni | Bozze di risposta, analisi | Propone e approvi | [G3], [H1] | MSG-09 |
| Documenti fiscali | Ricevuta o fattura agli eventi della prenotazione; invio allo SDI dopo l'OK | Automatica la bozza, approvi l'invio | [D5], [O12], [O2] | AI-08 |
| Clienti e marketing | Testi del sito diretto, richieste di recensione, offerte | Propone e approvi | [L3], [G3], [H1], [S2] | AI-09 |
| Report | Riepiloghi settimanali e domande in parole | Automatica, sola lettura | [G3], [A9] | AI-10 |
| Avvio | Import di annuncio e calendario, valori per regione, «cosa manca» | Automatico l'import, propone il resto | [G4], [O1], [O10] | LST-02, UX-10 |

Diversi fornitori non dicono se l'utente approva (preparazione degli annunci di Airbnb, testi di
Guesty, sito diretto di Lodgify): la modalità in tabella è una raccomandazione.

### A.5 Adempimenti italiani a confronto

| Piattaforma | Alloggiati Web | ISTAT e regioni | Tassa di soggiorno | Prova e ricevute | Check-in online dell'ospite |
|---|---|---|---|---|---|
| Octorate | Invio giornaliero automatico dopo credenziali, chiave e numero della struttura; oltre 30 notti a mano [O7][O8] | Elenco delle regioni, automatico o manuale, file di riserva; più strutture dal giugno 2026 [O9][O12] | Regole per struttura (fissa o percentuale, esenzioni), esportazioni; PayTourist con file † [O11][O13] | Stato «Attivata», storico, nuovo invio; esito ISTAT per email; ricevuta ufficiale non trovata [O8][O9] | Link, dati, documenti, firma, stato per prenotazione; scansione e selfie non descritti [O3] |
| Krossbooking | Invio automatico [M][K1] | Report ISTAT; invio non descritto [K1] | Calcolo automatico, report trimestrale, pagamento nel portale [K1][K2] | Non trovato | Portale con documento, selfie facoltativo, conferma dell'host [K2] |
| Dolcebot | Automatico all'arrivo, tracciato [M][D2] | Dati giornalieri; conferma non descritta [D2] | Per notte e ospite, incassata al check-in, ricevuta e rendiconto per il comune [D2] | Linea temporale e archivio; nessuna ricevuta ufficiale descritta [D2] | Modulo guidato con controlli e promemoria; nessuna scansione o passo dal vivo [D2][D4] |
| Chekin | Entro 24 ore, invio serale [M][C1] | Entro il 5 del mese, tabella per regione [M][C1] | Con PayTourist [M][C1] | Ricevute archiviate 5 anni, esportabili [M][C1] | Link, documento e selfie (premium), firma, pagamento; verifica dal vivo all'arrivo [C2][C3] |
| Vikey | Invio al portale [M][V1] | Non chiaro [V2] | Modulo con pagamento online [M][V1][V2] | Non trovato | Link, documento, firma, videochiamata; selfie nel piano superiore [V1][V2] |
| Lodgify | Tramite integrazioni; dichiara l'archivio delle ricevute [M][L1][L2] | Non trattato [L2] | Non trattato [L1] | L'host deve conservare le ricevute [L2] | Tramite Chekin [L1] |
| Smoobu | Non nativo, tramite Chekin [S1] | Tramite Chekin; tabella per regione [S1] | Non descritto | Non trovato | Tramite Chekin [S1] |
| Guesty, Hostaway | Non trovato in modo nativo; partner † [G8] | Non trovato | Non trovato | Non trovato | Partner |

Sul CIN: lo trattano solo il wizard di Octorate (un campo) e la guida di Lodgify (spiegazione).

### A.6 Fonti

- Guesty: [G1]† https://help.guesty.com/hc/en-gb/articles/16697585390621-Using-ReplyAI-in-your-Inbox-messages · [G2] https://yourtechdiet.com/news/guesty-launches-replyai-autopilot-and-ai-task-creation-to-power-proactive-operations-inside-the-pms/ · [G3] https://www.guesty.com/features/ai-for-short-term-rentals/ · [G4]† https://help.guestyforhosts.com/hc/en-gb/articles/9350503315229-Getting-Started-by-Connecting-to-Airbnb · [G5]† https://help.guesty.com/hc/en-gb/articles/9371171208733-Marketplace-overview · [G6] https://www.guesty.com/integrations-marketplace/ · [G8]† https://www.guesty.com/it/blog/come-costruire-il-check-in-perfetto/
- Airbnb: [A1] https://www.airbnb.com/resources/hosting-homes/a/reservations-redesigned-765 · [A2] https://www.airbnb.com/resources/hosting-homes/a/new-hosting-tools-to-help-grow-your-business-738 · [A3] https://www.airbnb.com/help/article/2897 · [A4] https://www.airbnb.com/help/article/14 · [A5] https://www.airbnb.com/resources/hosting-homes/a/respond-faster-in-the-messages-tab-770 · [A6] https://www.airbnb.com/resources/hosting-homes/a/how-to-finish-listing-your-space-461 · [A7] https://www.chameleon.io/inspiration/airbnb-value-reminder · [A8] https://www.rentalscaleup.com/airbnb-ai-strategy-2026-summer-release/ · [A9] https://www.rentalscaleup.com/airbnb-2026-fall-update-hosts/
- Lodgify: [L1] https://www.lodgify.com/blog/it/alloggiati-web/ · [L2] https://encyclopedia.lodgify.com/it/invio-automatico-schedine · [L3] https://www.thehostreport.com/news/lodgify-launches-dynamic-pricing-an-ai-co-host-and-a-chatgpt-direct-booking-pilot · [L4]† https://www.houfy.com/help/how-to-connect-lodgify-with-houfy-14515090 · [L5]† https://www.lodgify.com/vacation-rental-channel-manager/airbnb/ · [L6]† https://www.travolution.com/news/travel-sectors/accommodation/lodgify-launches-ai-assistant-to-optimise-guest-communications/
- Krossbooking: [K1] https://www.krossbooking.com/caratteristiche · [K2] https://www.krossbooking.com/kdoor/
- Dolcebot: [D1] https://www.dolcebot.com/ · [D2] https://www.dolcebot.com/registrare-ospiti-senza-errori-alloggiati-istat-imposta-soggiorno/ · [D3] https://www.dolcebot.com/automatizzare-comunicazioni-ospiti-affitti-brevi-2026/ · [D4] https://www.dolcebot.com/check-in-perfetto-senza-consegna-chiavi-guida-host-pm/ · [D5] https://www.dolcebot.com/gestione-documenti-fiscali-affitti-turistici/
- Octorate: [O1] https://www.octorate.com/ · [O2] https://www.octorate.com/it/pms/ · [O3] https://www.octorate.com/self-check-in/ · [O4] https://www.octorate.com/partner-hub/ · [O5] https://www.octorate.com/unified-inbox/ · [O6] https://www.octorate.com/ai-agent-for-hotels/ · [O7] https://community.octorate.com/post/5eaab840f1ea4840883cc4dc · [O8] https://community.octorate.com/articles-u84kjj5k/post/guida-alla-risoluzione-dei-problemi-di-connessione-tra-octorate-e-il-65FbIMCUmPzZfjF · [O9] https://community.octorate.com/articles-u84kjj5k/post/come-inviare-i-dati-all-istat-wkAW0YIw9js5EPE · [O10] https://community.octorate.com/articles-u84kjj5k/post/utilizza-il-nostro-assistente-per-la-creazione-del-tuo-annuncio-6WrThOzc84QknoN · [O11] https://community.octorate.com/articles-u84kjj5k/post/tassa-di-soggiorno---tassa-turistica-Eq2UWtq9r3hgwf8 · [O12] https://community.octorate.com/webinar-ko91w8bv/post/webinar-giugno-2026-le-ultime-novita-di-octorate-0qUw6ANR5K5bJOT · [O13]† https://community.octorate.com/articles-u84kjj5k/post/paytourist-ODpCbeEUqGVS6iZ
- Chekin: [C1] https://chekin.com/en/legal-compliance/italy/ · [C2] https://chekin.com/en/online-check-in/ · [C3] https://chekin.com/it/blog/riconoscimento-de-visu-affitti-brevi/ · [C4] https://support.chekin.com/en/octorate-integration-chekin-help-center
- Vikey: [V1] https://get.vikey.it/self-check-in/ · [V2] https://vikey.it/en/pricing/ · [V3] https://vikey.it/tassa-soggiorno-affitti-brevi-guida-2025/
- Smoobu: [S1] https://www.smoobu.com/it/guides/istat-affitti-brevi/ · [S2] https://www.smoobu.com/en/blog/the-smoobu-feature-roadmap-for-your-first-30-days/ · [S3] https://www.smoobu.com/en/blog/best-ai-tools-for-vacation-rental-messaging/
- Hostaway: [H1] https://www.hostaway.com/features/hostaway-ai/ · [H2] https://changelog.hostaway.com/ai-auto-reply-is-finally-here!-333474 · [H3] https://www.hostaway.com/marketplace/
- Booking.com: [B1]† https://partner.booking.com/en-us/help/working-booking/going-live/registering-your-property · [B2]† https://partner.booking.com/en-us/help/growing-your-business/increase-revenue/opportunity-center · [B3]† https://partner.booking.com/en-us/help/reservations/contact-extranet/setting-templates-automatic-replies-and-message-notifications · [B4] https://www.altexsoft.com/travel-industry-news/booking-com-rolls-out-ai-agents-for-guest-support/
- Norme e UX: [X1] https://www.misterlex.it/doc/affitti-brevi-obbligatorio-riconoscimento-de-visu-distanza/ · [X2] https://www.ilfattoquotidiano.it/2025/11/21/affitti-brevi-self-check-in-vietato-consiglio-di-stato-sentenza-oggi-news/8203757/ · [X3] https://www.nngroup.com/articles/wizards/ · [X4] https://design-system.service.gov.uk/patterns/complete-multiple-tasks/
