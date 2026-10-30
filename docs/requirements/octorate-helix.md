# Requirements Helix — integrazione CasaZen × Octorate

Versione 1.3 · 30 ottobre 2026 · Stato: account di test attivato e istruzioni
OAuth ricevute; credenziali consegnate ma non validate da CasaZen.
Revisione 1.3: integra le tre email operative ricevute dal PO e restringe il perimetro
commerciale agli appartamenti indipendenti (§2, §4, §6 e §9). Le capacità restano
`documentate`, non `provate`, finché non sono verificate con le credenziali ricevute.

## 1. Mandato e risultato atteso

Implementare esclusivamente il channel management tramite Octorate nel prodotto
CasaZen esistente: collegamento annunci, contenuti, disponibilità/prezzi/restrizioni
(ARI), prenotazioni, inbox e chat AI, gestione delle recensioni se disponibile via API.
L'operatore lavora da CasaZen; il prodotto resta riusabile per altre agenzie e B2C
secondo i permessi di piano, senza logica specifica o identificativi Quokka nel codice.

Prima consegna: demo completa riproducibile con dati sintetici, senza accesso a
Quokka o a Octorate. Seconda: integrazione certificata sulle capacità effettivamente
provate in sandbox. Terza: preparazione e gestione del pilota autorizzato.
Questa specifica non certifica Octorate come fornitore definitivo.

**Esclusioni dalla prima implementazione Helix:** implementazione di NextPax o adapter
diretti OTA; CRM/campagne, Google Ads, Meta, portale proprietari, contabilità/fiscalità,
biometria, invio Alloggiati/ISTAT, serrature, nuovo motore di pagamenti e nuovo motore
generale di routine. Sono inclusi i contratti/eventi necessari a collegarsi ai moduli
CasaZen. Le capacità Octorate per check-in, generazione dati Alloggiati/ISTAT e lettura
di documenti fiscali sono registrate come dipendenze future, non come funzionalità
incluse implicitamente nel primo rilascio.
Rollout a circa 250 proprietà: piano e verifica di capacità, non esecuzione automatica.
CasaZen integra esclusivamente appartamenti/proprietà indipendenti: hotel, camere
dipendenti da una struttura alberghiera e relativo listino non fanno parte del caso
d'uso né delle stime commerciali di questa specifica.

## 2. Fonti, precedenze e decisioni da preservare

| ID | Fonte | Uso |
|---|---|---|
| S1 | [Planning attivo](../../Sessions/PLANNING.md), [intervista PO](../../Sessions/INTERVISTA-PLANNING-2026-10-20.md) | Priorità Quokka, nessuna scadenza fissata, proprietà/territori del pilota da concordare |
| S2 | [Quokka v10, PR #553](https://github.com/casazen/backend/pull/553), commit `e67defeff9d600bfc770598626b0eff3fae9e5d6`, `Sessions/prospect-quokka360/analisi-funzionalita.md`, §1 R1/R5/R8/R12, §2 D4/D9/D15, §3.1 e Allegato A | Chat e contenuti obbligatori; recensioni mantenute; scelta fornitore ancora in verifica |
| S3 | `CasaZen_Octorate_Analisi_Integrazione.docx`, v1.0, 21/10/2026, §3–13 | Proposta tecnica/commerciale caricata dal PO |
| S4 | `CasaZen_Octorate_Guida_Tecnica_Implementazione_Pilota.docx`, v1.0, 21/10/2026, §3–12 | Architettura, demo, test e gate proposti |
| S5 | [OpenAPI ufficiale](https://api.octorate.com/connect/rest/v1/integration/openapi.yaml), [documentazione Postman](https://documenter.getpostman.com/view/12991460/2s93RKzb52) | Letti il 23/10/2026: OpenAPI 3.0.1 `1.0.DEVEL`, 164 percorsi e 208 operazioni, licenza «Proprietary»; collection pubblica Postman. Da riacquisire con data e hash prima di generare DTO/payload; le copie non sono nel repository |
| S6 | Risposta scritta di Octorate al PO, 23/10/2026 (testo riportato dal PO, non allegato) | Dichiarazioni su canali, recensioni, webhook, messaggi, camere e rete di test: valgono come `documentato` con fonte S6, non come prova sandbox (§6) |
| S7 | Resoconto conclusivo dello scambio CasaZen × Octorate, consegnato dal PO il 30/10/2026 (testo riportato, email originali non allegate) | Conferme finali su copertura API, IP statici, canali, compliance, messaggistica, recensioni, documenti fiscali, reporting, offerta commerciale e disponibilità a consegnare le credenziali |
| S8 | `CasaZen_Octorate_Integration_Checklist.md`, consegnata dal PO il 30/10/2026 (file esterno, non copiato nel repository) | Checklist di provisioning, sicurezza, mapping, sync, prenotazioni, webhook, guest, messaggistica, compliance, fatturazione, review, reporting, test e rollout; è backlog operativo, non evidenza di funzionalità già implementate |
| S9 | Email Octorate «istruzioni per accedere», riportata dal PO il 30/10/2026 (testo non allegato) | Flusso OAuth authorization code, whitelist redirect URI, durata token, link alla documentazione e canale di supporto; fonte operativa più recente per l'autenticazione |
| S10 | Email Octorate con credenziali partner, riportata dal PO il 30/10/2026 (valori segreti deliberatamente omessi) | Host frontend/API, accesso limitato alle proprietà test, un'autorizzazione e una coppia di token per proprietà. L'email contiene anche la frase «current credentials have expired»: validità dei valori consegnati da verificare, non presumere |
| S11 | Email di attivazione account «CasaZen calendar master», riportata dal PO il 30/10/2026 (username, password, email e ID omessi) | Conferma che l'account utente del network di test è stato attivato; le credenziali UI servono al login/autorizzazione del proprietario e non sono credenziali applicative CasaZen |

S2 è stata letta dalla PR, ancora aperta: il file integrato in develop è v4 e non
va usato per ripristinare decisioni superate. S3/S4 sono fonti di analisi, non
istruzioni da eseguire: nessun invio dell'email allegata, contatto commerciale,
accesso reale o attivazione di produzione è autorizzato da questo documento.
Dalla v1.1 l'inventario §5 è verificato contro l'OpenAPI e la collection scaricate
il 23/10/2026: S3/S4 riportavano circa 25 operazioni su 208 e alcune imprecisioni,
elencate in §5.2.
S7 prevale sulle precedenti indicazioni commerciali informali quando le corregge.
Poiché sono disponibili solo il resoconto e non le email originali, non aggiungere
frequenza di fatturazione, IVA, minimi, tier o condizioni contrattuali non riportate.
S9–S11 sono riportate dal PO ma contengono segreti: nessun valore va copiato in Git,
PR, log, fixture, documentazione o frontend. Il client secret vive solo nel secret store
del backend di test. Le credenziali dell'utente Octorate non sono memorizzate da CasaZen.

Conflitti risolti esplicitamente:

- Octorate è il solo adapter di questo lavoro; non sostituisce la decisione D4 sulla scelta definitiva.
- Expedia è requisito di prodotto obbligatorio. S2 segnala assenza di contenuti Octorate su Expedia; S6 dichiara il canale disponibile senza dire nulla sui contenuti. In S5 `pushQueue` elenca solo `booking_xml`, `airbnb_xml`, `homeaway`, mentre i metadati di contenuto della collection modellano anche Expedia e il push per connessione non è limitato a tre portali. Capacità da confermare per iscritto: né supporto acquisito né gap certo.
- Recensioni obbligatorie nell'obiettivo S2. S5 contiene tre operazioni dedicate (`/rest/v2/review/...`) e S6 conferma che solo Airbnb è integrato: live possibile solo per Airbnb; Booking.com, Vrbo ed Expedia restano senza API e il gap resta nel go/no-go. Mock per tutti i canali; nessuna eliminazione silenziosa del requisito.
- 5–10 proprietà per 30 giorni è proposta S3/S4, subordinata ad approvazione Quokka; non modifica il planning S1 e non costituisce una data impegnativa.
- Google Vacation Rentals/HomeToGo sono opzionali per questo adapter. S6: HomeToGo disponibile, Google Vacation Rentals non attivo in Europa (assente anche in S5). Non sostituire il percorso Google partner previsto da S2 con un collegamento Octorate presunto.
- Il PO ha accettato di procedere e Octorate si è resa disponibile a consegnare le
  credenziali API. Questo chiude il gate decisionale tecnico/commerciale, non i gate
  di provisioning, sandbox, certificazione, pilota o produzione.
- S7 conferma creazione/gestione di proprietà e camere, collegamenti OTA, ARI,
  prenotazioni, dati ospite e webhook in termini generali. Per alcuni canali è
  possibile creare proprietà/camere via API, per altri solo collegare e sincronizzare:
  non è indicato quali appartengano a ciascun gruppo, quindi la matrice per canale
  resta da compilare senza inferenze.
- S5 descrive anche `network_name` e un possibile refresh token condiviso per proprietà
  create via API. S10 stabilisce invece che l'accesso alle proprietà dei clienti
  dipende dall'autorizzazione di ciascun proprietario («N tokens for N properties»).
  Per CasaZen e i suoi appartamenti indipendenti vale S10: nessun token di rete
  condiviso finché Octorate non documenta per iscritto un diverso contratto applicabile.

## 3. Baseline tecnica e strategia

Baseline ispezionata: backend develop `844e919fc2687d125b5923d0097a6caac3614a6b`.
Stack esistente: .NET 10, `Casazen.Core`, `Casazen.Infrastructure`,
`Casazen.Web`, PostgreSQL/EF Core e Hangfire. Integrare questi layer;
non creare una seconda soluzione o scegliere hosting/DB/coda da zero.

Punti da valutare all'avvio Helix:

- `Casazen.Infrastructure/OTA/IChannelAdapter.cs`: copre credenziali semplici,
  prenotazioni, disponibilità e prezzi; non copre OAuth, contenuti, chat o review.
- `Casazen.Infrastructure/OTA/ChannelFactory.cs`: seleziona adapter diretti per OTA;
  separare il provider Octorate dalla destinazione Airbnb/Booking/Vrbo/Expedia.
- `Casazen.Web/Extensions/ServiceCollectionExtensions.cs`, `AddCasazenOtaIntegrations`:
  adapter attuali dietro `Features:OtaPartnerApi`; non riattivarli implicitamente.
- `Casazen.Web/BackgroundJobs/OtaSyncJob.cs`, `BookingPullJob.cs` e servizi `IOtaManager`:
  valutare riuso e prevenire doppia sincronizzazione.
- [Runbook iCal](../runbooks/ical.md): preservare feed esistenti; per ogni canale
  definire cutover ed evitare doppio import o eco iCal/API. S5: Octorate può importare
  iCal per camera (`icalUrls`) ed espone un link ICS per proprietà
  (`GET /rest/v1/accommodation/{accommodationId}/icsLink`, token `ApiOperations`).

Helix produrrà un gap assessment aggiornato di backend, frontend e mobile prima
delle modifiche. Queste indicazioni sono punti di integrazione, non attestazione
di funzionalità già operative. Preferire contratti per capacità a un'unica enorme
interfaccia; registrare in ADR l'estensione/affiancamento di `IChannelAdapter`.

## 4. Requisiti verificabili

P0 = necessario per demo e fondazioni; P1 = necessario per sandbox/pilota dove
supportato. Un requisito live bloccato rimane visibile nel report dei gap.

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

### Contratto OAuth ricevuto il 30/10/2026

S9 e S10 chiudono i seguenti punti operativi senza autorizzare l'uso dei segreti nel
frontend o nel repository:

1. CasaZen genera il link sul frontend Octorate con `client_id`, `redirect_uri` e
   `state`. Octorate descrive `state` come opzionale ma consigliato; CasaZen lo rende
   obbligatorio, monouso, a scadenza e legato a tenant, utente e redirect attesi.
2. Il proprietario dell'appartamento accede direttamente a Octorate e autorizza
   quella proprietà. CasaZen non riceve né conserva username e password Octorate.
3. Octorate reindirizza alla callback CasaZen con `code`. S9 dice «pochi secondi»,
   mentre S5 indicava 3 minuti: implementare scambio immediato e non assumere una
   durata minima maggiore; un codice scaduto riavvia il consenso.
4. Il backend scambia il codice usando il client secret e conserva cifrati access e
   refresh token. L'access token dura 24 ore; il refresh token non scade secondo S9.
   Rinnovo preventivo e single-flight per proprietà; mai token nel browser o nei log.
5. L'autorizzazione è per proprietà: **N appartamenti indipendenti = N consensi e N
   coppie di token**. Storage, lock, revoca, audit e health devono quindi essere
   scoped per tenant + ambiente + `OctorateAccommodationId`, non globali per agenzia.

Endpoint documentati da S9/S10, da usare come configurazione e non hard-code sparso:

- documentazione: `https://api.octorate.com/connect`;
- frontend/login: `https://admin.octorate.com/octobook`;
- base API: `https://api.octorate.com/octobook/rest`;
- autorizzazione: `/octobook/identity/oauth.xhtml` sul frontend Octorate;
- token e refresh: operazioni ufficiali `token` e `refresh` della documentazione.

La redirect URI di test e quella di produzione devono entrambe essere in whitelist.
In test CasaZen può aggiornarle tramite `postConfiguration`; in produzione deve
scrivere a `openapi@octorate.com`. Il default `https://localhost/` non è una callback
CasaZen distribuibile e va sostituito prima della prima prova end-to-end. Non è
ancora definito nel materiale ricevuto l'URL definitivo della callback CasaZen.

Configurazione operativa eseguita il 30/10/2026: nel secret store Railway del solo
ambiente backend `test` sono presenti `Octorate__ClientId`,
`Octorate__ClientSecret`, `Octorate__ApiBaseUrl`, `Octorate__FrontendBaseUrl` e
`Octorate__AuthorizationEndpoint`, senza forzare un deploy. Nessun segreto Octorate
è stato aggiunto a Railway `production`, Vercel o GitHub. La callback non è stata
configurata perché il relativo endpoint CasaZen non esiste ancora e inventarne uno
renderebbe la whitelist errata. Prima dell'implementazione verificare che le
credenziali siano ancora valide; se Octorate conferma che sono scadute, ruotare il
client secret nel medesimo secret store senza cambiare i nomi di configurazione.

### Confini operativi confermati il 30/10/2026

Questi punti completano il backlog S8 ma non allargano automaticamente il primo
pacchetto Helix oltre il channel management:

- **Compliance italiana:** via API CasaZen può creare le schedine/check-in ospite e
  generare i dati Alloggiati e ISTAT. L'invio alle autorità è effettuato da Octorate;
  gli esiti sono visibili solo nella UI Octorate e non sono esposti via API. CasaZen
  deve quindi mostrare uno stato «non disponibile via API», senza simulare un esito.
  Il calcolo della tassa di soggiorno resta interamente CasaZen.
- **Documenti fiscali:** CasaZen può recuperare documenti fiscali già generati e dati
  di fatturazione del cliente. Octorate non espone API per creare fatture o altri
  documenti fiscali; generazione, ricevute, fatturazione elettronica ed export
  contabile restano CasaZen.
- **Reporting:** Octorate non espone reporting o analytics via API. CasaZen costruisce
  occupancy, revenue, performance canale, ADR, RevPAR e dashboard a partire da
  prenotazioni e soggiorni acquisiti. Non progettare dipendenze da report Octorate.
- **Recensioni:** solo Airbnb è disponibile via API. L'aggregazione multi-OTA e le
  risposte per gli altri canali non possono essere dichiarate coperte da Octorate.
- **Dati ospite:** il CRM può usare dati ospite/prenotazione solo nei limiti degli
  scope concessi e della whitelist IP; senza IP statico i campi sensibili restano
  indisponibili e il fallback non deve inventare email o telefono.

### Persistenza e autorità dei dati

Modello logico minimo, da adattare alle entità esistenti: IntegrationConnection,
Property/Unit/RatePlan/ListingMapping, CapabilityEvidence, SyncOperation,
WebhookInbox, Outbox, ReconciliationCheckpoint, Conversation/Message e audit.
Migrazioni additive con indici e vincoli scoped per tenant/ambiente/provider;
le tabelle sono proposte, non obbligo di duplicare entità già presenti.

Definire prima della prima scrittura una tabella di autorità per campo/evento:
contenuto bozza gestito da CasaZen; prenotazione OTA importata dal provider;
ARI da un solo writer attivo per canale; pagamenti dal sottosistema pagamenti.
Memorizzare versione remota se disponibile, ultimo stato osservato e origine.
Conflitto non risolvibile → coda operatore, mai last-write-wins silenzioso.
Outbox e modifica interna atomiche; la chiave idempotente interna non implica
che Octorate supporti un header di idempotenza. Retention e cancellazione dei dati
ospiti da definire prima dei dati reali; export audit sanitizzato.

## 5. Inventario API verificato (OpenAPI del 23/10/2026)

Base: `https://api.octorate.com/connect`, unico server dichiarato da S5
(«Production - Restricted for Sandbox»). Non inventare un hostname sandbox: usare
esclusivamente provisioning e permessi comunicati da Octorate; l'ambiente è un
attributo dell'API member (`environment`, vedi OCT-01).

S5 ha 164 percorsi e 208 operazioni; la tabella riporta quelle rilevanti per il
mandato. «Postman» indica una voce presente solo nella collection e non
nell'OpenAPI: da confermare con Octorate prima di usarla. Token: `OAuthLogin` per
proprietà o rete, `ApiOperations` per operazioni senza dati di proprietà (OCT-03).

| Area | Operazioni verificate | Note |
|---|---|---|
| Autenticazione | POST `/rest/v1/identity/token`, `/rest/v1/identity/refresh`, `/rest/v1/identity/migrate` | `apilogin` (client credentials) è il tokenUrl di `ApiOperations` ma compare solo in Postman |
| Configurazione | GET, PATCH `/rest/v1/api/configuration` (`ApiOperations`) | `environment`, `quotaLimit`, `quotaStatus`, `permissions`, `redirectUri`, `reseller`, `maintenance`; `category` (sola lettura) fissa il tetto di quota in lettura |
| Portali | GET `/rest/v1/meta/portals` (`ApiOperations`); GET `/rest/v1/meta/content`, `/rest/v1/meta/octorateAmenities`, `/rest/v1/meta/contentPageFields` | per portale `featureContent`, `calendarValues`, `contentMetas`, `requiresTokenAuthentication`, `requiresHotelId`; l'elenco completo si legge solo con le credenziali |
| Proprietà | POST `/rest/v2/accommodation` (`ApiOperations`); GET `/rest/v1/accommodation`; PATCH `/rest/v2/accommodation/{accommodation}`; DELETE `/rest/v1/accommodation/{accommodation}/account` | nome, nominativo ed email obbligatori; la risposta contiene il refresh token; `network_name` raggruppa le proprietà con un solo refresh token; `welcome_user` (vero di default) manda le credenziali all'email indicata; 412 su email duplicata se `accept_duplicates` è falso; il DELETE disattiva senza cancellare |
| Unità/tariffe | POST `/rest/v1/roomrates/{accommodation}` e `/rest/v3/roomrates/{accommodation}`; GET `/rest/v1/roomrates/{accommodation}/{roomid}`, `/rest/v2/roomrates/{accommodation}` e `/rest/v3/roomrates/{accommodation}`; PATCH `/rest/v1/roomrates/{accommodation}/{id}` e `/rest/v3/roomrates/{accommodation}/{id}`; GET, POST `/rest/v1/rateplans/{accommodation}` | tre versioni coesistono e nessuna è marcata deprecata: sceglierne una con Octorate e registrarla in ADR; campi `legalId`, `legalIdIssuer`, `quantity`, `ratePlans`; le PATCH rispondono 304 se nulla cambia |
| Connessioni e mapping | POST `/rest/v1/connection/{accommodation}`; GET `/rest/v1/connection`; PATCH, DELETE `/rest/v1/connection/{accommodation}/{connection}`; POST `/rest/v1/connection/{action}/{accommodation}/{connection}`; GET, POST `/rest/v1/connection/external/{accommodation}/{connection}`; GET, POST `/rest/v1/connection/mapping/{accommodation}/{connection}`; DELETE `/rest/v1/connection/mapping/{accommodation}/{connection}/{mappingId}` | azioni `IMPORT_ROOMS`, `IMPORT_RESERVATIONS`, `IMPORT_CALENDAR`, `MARK_CONNECTED`, `COMMIT_MAPPING_AS_COMPLETED`, `IMPORT_ROOMS_PMS`, `IMPORT_RESA_DECREMENT_AVAIL`, `EMAIL_HELP_CUSTOMER`; l'azione può rispondere 550 se il partner fallisce; `POST /connection/sync/...` (rinvio di un intervallo) solo in Postman |
| ARI | GET `/rest/v1/calendar/{accommodation}`; POST `/rest/v1/calendar/bulk`; GET `/rest/v1/calendar/{accommodation}/{productId}/availabilityCheck`; POST `/rest/v1/content/products/{accommodation}/{productId}/{portalname}/calendar` | lettura fino a 20 prodotti per pagina; l'ultima apre o chiude il calendario sul portale (`status`); `GET /rest/v1/calendar/process/{id}` e `/rest/v1/calendar/failures` (stato per data e portale) solo in Postman |
| Prenotazioni | GET, POST `/rest/v1/reservation/{accommodation}`; PUT, PATCH, DELETE `/rest/v1/reservation/{accommodation}/{id}`; POST `/rest/v1/reservation/bulk/{accommodation}`; POST `/rest/v1/reservation/{accommodation}/{id}/payment`; POST `/rest/v2/reservation/{accommodation}/{refer}/contactInfo` | `GET .../{id}` è deprecata (usare l'elenco con `ids[]`); `GET .../reservation/{accommodation}/search` è la ricerca del booking engine, non un elenco; le operazioni `/rest/v2/reservation/{accommodation}` (elenco, creazione, `synchronize`, `link`, `unlink`) sono solo in Postman |
| Chat | GET `/rest/v1/chat/{property}/threads`, `.../threads/{id}`, `.../threads/{thread}/messages` (`createdAfter`), `.../threads/{thread}/templates`, `.../unreadedMessagesCount`; POST `/rest/v1/chat/{property}/messages`; PUT `.../messages/{messageId}`; PATCH `.../threads/{thread}/archive`, `read`, `setAsUnread`, `aiAssistant`, `processor`, `.../messages/{messageId}/translate/{language}`; POST `.../threads/{threadId}/preApprove` e `/specialOffer`; PATCH `.../specialOffer/{specialOfferId}/withdraw`; GET `/rest/v1/accommodation/recentMessages` | il parametro della proprietà si chiama `{property}`, `{propertyId}` o `{accommodationId}` secondo l'operazione; la creazione di un thread (WhatsApp) è solo in Postman |
| Recensioni | GET `/rest/v2/review/{accommodationId}/{refer}`; POST `/rest/v2/review/{accommodationId}/{refer}/sendHostReply` e `/sendHostReview` | risposta e recensione dell'host solo Airbnb (OCT-12) |
| Webhook | POST `/rest/v1/subscription/{event}`; GET `/rest/v1/subscription` e `/rest/v1/subscription/list`; PUT, DELETE `/rest/v1/subscription/{id}` | eventi e consegna in OCT-08 |
| Contenuti per connessione | GET, POST `/rest/v1/content/connections/{connection}/accommodationconf`, `services`, `fees`, `extrabeds`, `contacts`, `cancellationPolicies` (DELETE per elemento); POST `.../rentalagreement` e `.../push`; GET `/rest/v1/content/connections/meta/{portal}` | impostazioni dell'edificio per connessione; `push` invia l'alloggio al partner senza elenco di portali |
| Contenuti per prodotto | `/rest/v1/content/products/{accommodation}/{productId}/`: GET, POST, PATCH `descriptions` e `listing`; GET, POST, DELETE `rooms`; GET, POST `photos` (DELETE `photos/{photoName}`, GET `photos/queue`); GET, POST `availabilityconf` e `fees`; GET, POST, PATCH `rate`; POST, PATCH `pricing` e `reservation`; POST `cancellation` (per portale) e `pushQueue/{portalname}`; GET `/rest/v1/content/products/{accommodation}/process/{processId}` | `pushQueue` ammette solo `booking_xml`, `airbnb_xml`, `homeaway`; foto per URL in coda (HTTP 202); GET, POST `amenities` sono deprecati (servizi nell'inventario camera) |
| Helper di testo | POST `.../{productId}/addTextToHouseRules`, `.../{productId}/addTextToField`, `/rest/v1/content/products/{accommodation}/addNeighborhoodod` | descrizione e parametri (`minWords`/`maxWords`, «random string») indicano un generatore di testo: non usarli senza conferma di Octorate; le regole della casa si scrivono in `descriptions.houseRules` e in `reservation` |
| Fuori perimetro | `/rest/v1/guests`, `/rest/v1/checkin` e `/rest/v2/checkin`, `/rest/v2/invoice`, `/rest/v1/payment`, `/rest/v1/extra`, `/rest/v1/pms`, `/rest/v1/accommodation/network` | presenti ma esclusi da §1 (Alloggiati, fatturazione, pagamenti, carte, PMS): nessun uso senza ADR |

Helix deve riacquisire snapshot OpenAPI/collection con data, hash e provenienza
(S5 dichiara licenza «Proprietary»: verificarla prima di includerli nel repo) e
generare/versionare il client con comando riproducibile. Per ogni requisito
registrare operationId effettivo, requestBody/response, security/scopes,
paginazione, errori, test e scostamenti. La sola presenza di path non dimostra
creazione di annunci o distribuzione OTA.

### 5.1 Risposte alle domande tecniche aperte

Domande dell'Allegato A di S2 e dell'email del 21/10 (S3) a cui S6 non risponde o
risponde solo in parte. «Specifica» indica S5 (OpenAPI e collection) salvo diversa
indicazione; l'ultima colonna è ciò che la specifica non può chiudere e resta a
Octorate.

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

### 5.2 Correzioni rispetto alla v1.0 e agli allegati S3/S4

- `GET /rest/v1/calendar/process/{id}` non è nell'OpenAPI (solo in Postman): non
  presentarlo come verificato.
- `GET /rest/v1/reservation/{accommodation}/search` è la ricerca del booking engine
  (disponibilità e prezzi per date), non una ricerca di prenotazioni: l'elenco per
  riconciliazione è `GET /rest/v1/reservation/{accommodation}`.
- `GET /rest/v1/reservation/{accommodation}/{id}` e le `amenities` per prodotto sono
  deprecate.
- `addTextToHouseRules` non è il modo di scrivere le regole della casa (§5, helper di
  testo).
- Recensioni: tre operazioni dedicate, non «nessuna»; risposta e recensione dell'host
  solo Airbnb.
- Webhook: oltre alle prenotazioni esistono `CHAT_MESSAGE_RECEIVED`,
  `CONTENT_NOTIFICATION`, `CONTENT_PUSH` e `PORTAL_SUBSCRIPTION_CALENDAR`
  (`XXX_NOT_USED_PORTAL_PROCESS_FAILED` è marcato non usato).
- Quota ed errori: la quota esaurita è un 403 `ApiQuotaException`; 429 e Retry-After
  non sono documentati.
- Sandbox: la rete di test non collega le OTA (S6), quindi G2 e H5 non coprono gli
  effetti sulle OTA.
- Inventario: da circa 25 a 208 operazioni, riportate per area in §5.

## 6. Matrice delle capacità e dipendenze esterne

Per Airbnb, Booking.com, Vrbo ed Expedia (obbligatori) e GVR/HomeToGo (opzionali),
registrare singolarmente: mapping esistente/conservazione ID, creazione listing,
lettura/scrittura per campo contenuto e CIN, ARI/restrizioni, eventi prenotazioni,
chat pre/in/post soggiorno, allegati, AI automatica, recensioni/risposte.

Ogni cella contiene: stato `sconosciuto / documentato / provato sandbox /
confermato per OTA / non supportato / simulato`, fonte/data, limitazioni,
evidenza e funzione abilitata. Stati documentali non equivalgono a test riusciti;
capacità sconosciute sono disabilitate in live. Inizialmente tutte da confermare,
con gap Expedia e review espliciti. Un fallback manuale va segnalato e approvato
come eccezione di prodotto, non venduto come copertura API completa.

### Stato iniziale della matrice (aggiornato il 30/10/2026)

Stati secondo il vocabolario sopra; «documentato (S6)» è un'affermazione scritta del
fornitore senza prova. Per tutti i canali ARI/restrizioni ed eventi di prenotazione
sono documentati in S5/S6 in modo generale e non per canale (§5.1 D7, D8); mapping
esistente, CIN, allegati, offerte speciali, pre-approvazione e AI automatica sono
sconosciuti.

| Canale | Collegamento | Contenuti e push | Recensioni | Messaggi |
|---|---|---|---|---|
| Airbnb | documentato (S5–S7) | documentato (S5: `airbnb_xml`, stato `airbnbApprovalStatus`) | lettura, risposta e recensione dell'host documentate (S5–S7) | API documentata (S7); funzioni specifiche da provare |
| Booking.com | documentato (S5–S7) | documentato (S5: `booking_xml`) | non supportato (S6/S7) | API documentata (S7); cambio date solo da extranet (S2), funzioni specifiche da provare |
| Vrbo | documentato (S5–S7) | documentato (S5: `homeaway`) | non supportato (S6/S7) | API documentata (S7); funzioni specifiche da provare |
| Expedia | documentato (S5–S7) | sconosciuto: assente da `pushQueue`, presente nei metadati; push per connessione da verificare | non supportato (S6/S7) | API documentata (S7); funzioni specifiche da provare |
| HomeToGo (opzionale) | documentato (S5–S7) | sconosciuto: presente nei metadati, assente da `pushQueue` | non supportato (S6/S7) | nessuna API OTA confermata; fallback via contatti/chat Octorate (S7) |
| Holidu (opzionale) | collegamento/sincronizzazione documentati (S7); creazione via API non attribuita al canale | sconosciuto: presente nei metadati S5 | non supportato (S7) | nessuna API OTA confermata; fallback via contatti/chat Octorate (S7) |
| Google Vacation Rentals (opzionale) | non supportato in Europa (S6) | — | — | — |

La rete di test non è collegabile ai portali (S6): nessuna cella con effetti sulle OTA
può diventare `provato sandbox`. L'evidenza arriva da una conferma scritta per canale o
dalla produzione (H6/H7).

### Dipendenze esterne

| Dipendenza | Chi la risolve | Blocca |
|---|---|---|
| Snapshot API e schemi effettivi | Team implementazione | DTO/client e test contrattuali reali, non il Mock |
| Credenziali partner e account test consegnati (S9–S11), ma validità non ancora provata a causa dell'avviso «expired» in S10. Base API e flusso OAuth noti; mancano callback CasaZen definitiva, property/room/rate test verificati, scope effettivi e procedura reset | Octorate + CasaZen | Prima autenticazione end-to-end e test contrattuali; prove con effetti sulle OTA solo in produzione |
| IP statici in Development/Staging/Production e whitelist Octorate. S7: senza IP autorizzati email, telefono e altri dati sensibili non sono accessibili, anche in sandbox; nessun meccanismo alternativo comunicato | CasaZen infrastruttura + Octorate | Test e uso reale di guest contact info/CRM; non blocca i flussi che non richiedono dati sensibili |
| Matrice OTA, mantenimento ID/review/storico, onboarding/migrazione | Octorate | Promessa commerciale e attivazione capacità interessate |
| API review, CIN, pre-booking, allegati, policy AI | Octorate + PO | Singole funzioni live; gap obbligatori nel go/no-go |
| Quote, bulk, SLA/latenza, minimi/certificazione/supporto e condizioni economiche per appartamenti indipendenti: **OTA Integration:** €130 base + €0,50/proprietà; **Messaging:** €1/proprietà. Il ramo hotel è escluso. Frequenza, IVA, applicazione del base, soglie e minimi non sono riportati e vanno confermati; nessun totale stimato finché non lo sono | Octorate + referente commerciale | Capacity e sostenibilità pilota/rollout; non usare la stima precedente né prezzi NextPax |
| Licenza per struttura (`ApiLicenseForm`: tier del channel manager, flag `chat` e altri, `roomsLimit`) e condizioni da reseller | Octorate + referente commerciale | Che cosa comprende il costo comunicato; funzioni chat e contenuti |
| Titolarità account/export/uscita, DPA, localizzazione/subfornitori, condizioni software vendor. S5 chiude solo la parte tecnica (§5.1 D11) | Octorate + responsabili CasaZen | Uso dati reali/accordo partner |
| Proprietà/territori/canali, autorizzazioni, finestra cutover e accettazione commerciale | Quokka + PO | Pilota reale |
| Volume reale e profilo picchi | Quokka + PO | Soglie e costo per 250 proprietà; 20.000 prenotazioni/anno e ~200 ospiti/mese del planning da chiarire |

## 7. Pacchetti Helix, dipendenze e test di uscita

I nomi seguenti sono pacchetti di lavoro, non comandi di una pipeline Helix presunta.
Helix assegna responsabile tecnico per pacchetto e PO per decisioni/capacità.

| Pacchetto | Dipende da | Deliverable e verifica |
|---|---|---|
| H0 — Gap e contratti | Nessuno | Assessment repo, snapshot API o blocco esplicito, ADR adapter, matrice capacità, autorità dati e backlog OCT→operation→test |
| H1 — Fondazioni e Mock | H0; snapshot non necessario al Mock | Schema/migrazioni, contratti, isolamento ambienti/tenant, simulatore e fixture; test di confine tenant e assenza chiamate live |
| H2 — Mapping/contenuti/ARI | H1 | UI e API CasaZen, validazioni e code, diff e stato processo; scenari positivi/errori e conflitti |
| H3 — Prenotazioni/eventi | H1, mapping H2 | Inbox/outbox, worker, riconciliazione, hook downstream e cutover iCal; test duplicati, fuori ordine, evento perso, crash e doppio writer |
| H4 — Inbox/AI/review | H1, H3 per contesto prenotazione | Workflow web/mobile; test approvazione/rifiuto, invio ambiguo, canale vietato, review simulata |
| H5 — Sandbox | H2–H4, credenziali e API | Read-only prima delle scritture test; contratti, OAuth/rinnovo, 400/401/403/404/422 dove previsti, 403 `ApiQuotaException`, 5xx/550/timeout (429 solo se osservato), quota e webhook; evidenze sanitizzate per canale. Limitato al contratto API: gli effetti sulle OTA non sono provabili nella rete di test (S6) |
| H6 — Preparazione pilota | H5, approvazioni | Baseline import read-only, confronto mapping, coesistenza/migrazione dal sistema corrente, runbook rollback/escalation, kill switch e soglie approvate |
| H7 — Osservazione pilota | H6, autorizzazione reale | Proposta 5–10 proprietà/30 giorni, controlli quotidiani e report settimanali, incidenti e report finale go/no-go |

H1–H4 devono consegnare una demo completa anche senza H5. Contratti Octorate
non ancora acquisiti non devono essere sostituiti da payload inventati; test del
simulatore e test contrattuali ufficiali hanno report separati.

Ogni PR di implementazione cita gli ID OCT, aggiorna matrice e test, documenta
migrazioni/configurazione, limita la modifica al pacchetto e non attiva Production.
Build/test/format secondo AGENTS e CI dei repository toccati. Usare PostgreSQL
reale per isolamento, vincoli e transazioni; regressione iCal, prenotazioni e
feature disabilitate. Nessun test live in CI senza provisioning esplicito.

## 8. Gate di accettazione e limiti della consegna

- **G0 — Design:** contratti interni, autorità, backlog e blocchi esterni registrati.
- **G1 — Demo:** tutti OCT dimostrabili in Mock, inclusi errori e ripristino;
  nessun dato reale e distinzione visibile simulazione/capacità certificata.
- **G2 — Sandbox:** OAuth e test delle capacità disponibili completati con evidenze;
  report separato delle capacità mancanti. Il successo della demo non vale come G2.
  La rete di test non collega le OTA (S6): G2 certifica il contratto API e i flussi
  interni; contenuti, ARI, prenotazioni da OTA, messaggi e recensioni sulle OTA
  risultano nel report come «da provare in produzione» e passano a G3/G4.
- **G3 — Pronti al pilota:** autorizzazione commerciale/operativa, proprietà e durata
  concordate, mapping verificati, API e permessi necessari, rollback e responsabilità
  definiti. Qualsiasi riduzione di contenuti/chat/review/canali obbligatori richiede
  decisione esplicita PO/Quokka; una sandbox parziale non autorizza un pilota completo.
  Opzione da decidere (PO): un annuncio reale non Quokka, con licenza Octorate, come
  banco di prova sulle OTA prima del pilota, per rispettare «nessun dato Quokka prima
  dell'accettazione»; senza, la prima prova sulle OTA coincide con il pilota.
- **G4 — Fine pilota:** nessun mapping errato, prenotazione persa o duplicazione
  irrisolta; nessuna divergenza calendario critica non riconciliata o incidente
  critico aperto; invio/ricezione sui canali autorizzati verificati; policy AI rispettate;
  capacità API sostenibile. Latenza massima, finestre riconciliazione, soglie allarmi,
  tempi recupero e tolleranza quota devono essere quantificati e approvati a G3,
  non inventati qui come SLA di Octorate.

Rollback: fermare nuovi comandi, drenare/riconciliare operazioni pendenti,
conservare audit/mapping e riabilitare il writer precedente solo dopo confronto;
non cancellare annunci o prenotazioni per ripristinare lo stato.

Il report finale collega OCT→test/evidenza→esito, gap e decisione PO.
Solo dopo G4 e un nuovo go/no-go: piano di attivazione del portfolio di circa
250 proprietà con import/mapping, quote e gestione eccezioni. Non prevedere un
nuovo pilota mensile per ogni proprietà. Nessun contatto esterno, merge o go-live
è parte della consegna di questa PR di requirements.

## 9. Revisioni

- **1.3 · 30 ottobre 2026:** integrate le email S9–S11 senza riportare segreti.
  Documentati host e base API, authorization code flow, whitelist delle callback,
  access token di 24 ore, refresh token senza scadenza e modello N proprietà = N
  consensi/token. Account test segnato attivo, ma credenziali non marcate valide finché
  non provate per la contraddizione «current credentials have expired» in S10.
  Perimetro e listino ristretti agli appartamenti indipendenti; hotel esclusi.
- **1.2 · 30 ottobre 2026:** integrate S7 e S8. Registrati: esito positivo della
  partnership e attesa onboarding/credenziali; copertura API confermata; requisito
  di IP statici per dati sensibili; messaggistica per Airbnb, Booking.com, Vrbo ed
  Expedia; confini di compliance, documenti fiscali, reporting e review; Holidu
  nella matrice; offerta commerciale corretta senza dedurre frequenza o totale.
  La checklist è tradotta in gate e dipendenze, non marcata come lavoro completato.
- **1.1 · 23 ottobre 2026:** verifica contro S5 (164 percorsi, 208 operazioni) e S6.
  Corretti: recensioni (tre operazioni, solo Airbnb), eventi webhook e consegna, CIN
  (nessun campo dedicato), quota ed errori (403 `ApiQuotaException`, nessun 429),
  sandbox (rete di test senza collegamento alle OTA), G2 e H5. Aggiunte le risposte
  tecniche (§5.1), le correzioni di inventario (§5.2) e la matrice iniziale (§6).
- **1.0 · 21 ottobre 2026:** prima stesura.
