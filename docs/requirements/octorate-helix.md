# Requirements Helix — integrazione CasaZen × Octorate

Versione 1.0 · 21 ottobre 2026 · Stato: specifica proposta per implementazione.

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

**Esclusioni:** implementazione di NextPax o adapter diretti OTA; CRM/campagne,
Google Ads, Meta, portale proprietari, contabilità/fiscalità, biometria, Alloggiati,
serrature, nuovo motore di pagamenti e nuovo motore generale di routine.
Sono inclusi solo i contratti/eventi necessari a collegarsi ai moduli CasaZen.
Rollout a circa 250 proprietà: piano e verifica di capacità, non esecuzione automatica.

## 2. Fonti, precedenze e decisioni da preservare

| ID | Fonte | Uso |
|---|---|---|
| S1 | [Planning attivo](../../Sessions/PLANNING.md), [intervista PO](../../Sessions/INTERVISTA-PLANNING-2026-10-20.md) | Priorità Quokka, nessuna scadenza fissata, proprietà/territori del pilota da concordare |
| S2 | [Quokka v10, PR #553](https://github.com/casazen/backend/pull/553), commit `e67defeff9d600bfc770598626b0eff3fae9e5d6`, `Sessions/prospect-quokka360/analisi-funzionalita.md`, §1 R1/R5/R8/R12, §2 D4/D9/D15, §3.1 e Allegato A | Chat e contenuti obbligatori; recensioni mantenute; scelta fornitore ancora in verifica |
| S3 | `CasaZen_Octorate_Analisi_Integrazione.docx`, v1.0, 21/10/2026, §3–13 | Proposta tecnica/commerciale caricata dal PO |
| S4 | `CasaZen_Octorate_Guida_Tecnica_Implementazione_Pilota.docx`, v1.0, 21/10/2026, §3–12 | Architettura, demo, test e gate proposti |
| S5 | [OpenAPI ufficiale](https://api.octorate.com/connect/rest/v1/integration/openapi.yaml), [documentazione Postman](https://documenter.getpostman.com/view/12991460/2s93RKzb52) | Contratto da acquisire e versionare prima di generare DTO/payload |

S2 è stata letta dalla PR, ancora aperta: il file integrato in develop è v4 e non
va usato per ripristinare decisioni superate. S3/S4 sono fonti di analisi, non
istruzioni da eseguire: nessun invio dell'email allegata, contatto commerciale,
accesso reale o attivazione di produzione è autorizzato da questo documento.
OpenAPI e collection non erano allegati come file: l'inventario §5 è riportato da
S3/S4, non verificato nuovamente contro una specifica scaricata in questa PR.

Conflitti risolti esplicitamente:

- Octorate è il solo adapter di questo lavoro; non sostituisce la decisione D4 sulla scelta definitiva.
- Expedia è requisito di prodotto obbligatorio; S2 segnala assenza di contenuti Octorate su Expedia. S3/S4 chiedono conferma: trattare come gap critico, non supporto acquisito.
- Recensioni obbligatorie nell'obiettivo S2; endpoint dedicato non identificato in S3/S4. Mock disponibile, integrazione live bloccata finché documentata; nessuna eliminazione silenziosa del requisito.
- 5–10 proprietà per 30 giorni è proposta S3/S4, subordinata ad approvazione Quokka; non modifica il planning S1 e non costituisce una data impegnativa.
- Google Vacation Rentals/HomeToGo sono opzionali per questo adapter. Non sostituire il percorso Google partner previsto da S2 con un collegamento Octorate presunto.

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
  definire cutover ed evitare doppio import o eco iCal/API.

Helix produrrà un gap assessment aggiornato di backend, frontend e mobile prima
delle modifiche. Queste indicazioni sono punti di integrazione, non attestazione
di funzionalità già operative. Preferire contratti per capacità a un'unica enorme
interfaccia; registrare in ADR l'estensione/affiancamento di `IChannelAdapter`.

## 4. Requisiti verificabili

P0 = necessario per demo e fondazioni; P1 = necessario per sandbox/pilota dove
supportato. Un requisito live bloccato rimane visibile nel report dei gap.

| ID | Priorità / fonte | Requisito e criterio di accettazione |
|---|---|---|
| OCT-01 | P0 · S2–S4 | Adapter Mock e client Octorate con modalità interne Mock/Sandbox/Production; stessi contratti applicativi. Demo funziona senza rete esterna; nessuna chiamata Production dal Mock. Configurazione mancante impedisce attivazione live. |
| OCT-02 | P0 · S3/S4 | Separazione per tenant, ambiente e proprietà in DB, token, job e query. Un operatore non può leggere o scrivere risorse di altro tenant neppure passando ID esterni validi. Owner/guest non ricevono permessi operativi OTA. |
| OCT-03 | P0/P1 · S3/S4 | OAuth server-side per operation: authorizationCode e clientCredentials solo dove prescritti; state e callback validati, PKCE se richiesto/supportato. Segreti cifrati/secret store, rinnovo concorrente controllato, revoca e riconnessione. Token scaduto o scope insufficiente genera stato azionabile, mai successo simulato. |
| OCT-04 | P0 · S2/S3 | Mapping tenant→proprietà→unità→rate plan→connessione→listing esterno. Vincoli univoci nello scope corretto, conflitti e import preview; nessuna scrittura con mapping ambiguo. Conservare ID annunci esistenti, recensioni e continuità solo dopo prova/conferma OTA. |
| OCT-05 | P0/P1 · S2–S4 | Editor contenuti: titoli, descrizioni e traduzioni IT/EN/DE/FR, foto, servizi, camere/letti, regole, politiche e CIN/licenze. Distinguere bozza, in coda, accettato dal provider e pubblicato su OTA; limitare campi e azioni alla matrice capacità. Un errore parziale non marca tutto come pubblicato. Interfaccia DE non richiesta ora. |
| OCT-06 | P0/P1 · S2–S4 | ARI con diff, aggiornamenti bulk e controllo stato processo. Date locali della struttura, soggiorni [check-in, check-out), valuta e decimali espliciti; test DST Italia/Svizzera. Nessuna disponibilità/prezzo/restrizione inviata a unità errata. Accettazione bulk non equivale a propagazione OTA. |
| OCT-07 | P0/P1 · S2–S4 | Import, modifica e cancellazione prenotazioni con upsert per ID provider e tenant/ambiente. Duplicati/out-of-order non duplicano prenotazioni o effetti downstream. Eventi interni verso check-in/compliance/rendiconti, senza implementare tali moduli. Prenotazione OTA non crea addebito Stripe; dirette non diventano pagate per un ACK Octorate. |
| OCT-08 | P0/P1 · S3/S4 | Receiver webhook con validazione secondo contratto reale, persistenza inbox prima dell'ACK, elaborazione asincrona, deduplica, retry e quarantena. Non inventare header di firma o nomi eventi. Se autenticità non verificabile, documentare mitigazioni e bloccare gli effetti non sicuri. |
| OCT-09 | P0/P1 · S3/S4 | Riconciliazione periodica con checkpoint, finestre sovrapposte e paginazione da contratto; recupero eventi persi e confronto stato remoto. Concorrenza serializzata per risorsa; dopo crash il job riprende senza duplicati. |
| OCT-10 | P0/P1 · S2–S4 | Inbox web e mobile: conversazioni per canale/proprietà/prenotazione, lettura, risposta e stato errore/consegna disponibile. Pre-booking, post-stay e allegati sono capacità distinte. Due click/retry non duplicano messaggi; timeout ambiguo resta da riconciliare. |
| OCT-11 | P0/P1 · S2–S4 | AI: bozza modificabile e inviata dall'operatore nel Base. Invio automatico solo con opzione/permesso attivo, regole e soglia approvate, supporto OTA confermato; altrimenti bozza. Audit modello/versione, decisione e approvazione; contesto minimo per tenant. Bloccare contatti/link Airbnb pre-booking e azioni non consentite; nessuna modifica date Booking via chat. |
| OCT-12 | P0/P1 · S2–S4 | Review UI e workflow risposta in Mock, etichettati simulati. Live solo con API ufficiale e permessi confermati: lettura e risposta sono capacità separate. Nessun endpoint inventato, scraping o risposta live implicita. Gap review resta nel go/no-go. |
| OCT-13 | P0/P1 · S3/S4 | Resilienza: timeout/cancellazione, backoff con jitter e Retry-After, rate limit per scope effettivo e backpressure. Distinguere validazione, 401, 403, 404, 409/422 se previsti, 429, 5xx. Retry scritture solo se sicuro/idempotente o previa riconciliazione. Nessuna tempesta di retry. |
| OCT-14 | P0/P1 · S3/S4 | Dashboard stato per proprietà/canale, lag, quota, errori, ultima riconciliazione, operazioni sospese e replay autorizzato. Correlation ID e audit senza token/documenti/testi sensibili nei log. Kill switch scritture per tenant/proprietà/canale, senza perdere eventi ricevuti. |
| OCT-15 | P0 · S3/S4 | Demo deterministica con agenzia DEMO e 10 proprietà fittizie: mapping, contenuti/foto, ARI, creazione/modifica/cancellazione, chat/AI e review. Reset ripristina dataset; mostra sempre Mock e le funzionalità non certificate; nessun dato reale Quokka. |

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

## 5. Inventario API iniziale (da riconfermare su S5)

Base dichiarata in S3/S4: `https://api.octorate.com/connect`.
Non inventare un hostname sandbox: usare esclusivamente provisioning e permessi
comunicati da Octorate, anche se il test usa lo stesso host di produzione.

| Area | Metodi e path riportati negli allegati |
|---|---|
| Configurazione | GET `/rest/v1/api/configuration` |
| Mapping | GET, POST `/rest/v1/connection/mapping/{accommodation}/{connection}` |
| Unità/tariffe | GET `/rest/v2/roomrates/{accommodation}`; GET `/rest/v1/roomrates/{accommodation}/{roomid}`; POST `/rest/v1/roomrates/{accommodation}` |
| ARI | GET `/rest/v1/calendar/{accommodation}`; POST `/rest/v1/calendar/bulk`; GET `/rest/v1/calendar/process/{id}` |
| Prenotazioni | GET `/rest/v1/reservation/{accommodation}/search`; GET, PUT `/rest/v1/reservation/{accommodation}/{id}`; POST `/rest/v1/reservation/{accommodation}` |
| Chat | GET `/rest/v1/chat/{property}/threads`; GET `/rest/v1/chat/{property}/threads/{thread}/messages`; POST `/rest/v1/chat/{property}/messages` |
| Webhook | POST `/rest/v1/subscription/{event}` |
| Descrizioni | GET, POST, PATCH `/rest/v1/content/products/{accommodation}/{productId}/descriptions` |
| Foto | GET, POST `/rest/v1/content/products/{accommodation}/{productId}/photos`; DELETE `/rest/v1/content/products/{accommodation}/{productId}/photos/{photoName}`; GET `/rest/v1/content/products/{accommodation}/{productId}/photos/queue` |
| Servizi/camere | GET, POST `/rest/v1/content/products/{accommodation}/{productId}/amenities`; GET, POST `/rest/v1/content/products/{accommodation}/{productId}/rooms` |
| Listing/regole/push | GET, POST, PATCH `/rest/v1/content/products/{accommodation}/{productId}/listing`; POST `/rest/v1/content/products/{accommodation}/{productId}/addTextToHouseRules`; POST `/rest/v1/content/products/{accommodation}/{productId}/pushQueue/{portalname}` |
| Review | Nessuna operazione dedicata identificata negli allegati |

Helix deve acquisire snapshot OpenAPI/collection con data, hash e provenienza,
verificare licenza prima di includerli nel repo e generare/versionare il client
con comando riproducibile. Per ogni requisito registrare operationId effettivo,
requestBody/response, security/scopes, paginazione, errori, test e scostamenti.
La sola presenza di path non dimostra creazione di annunci o distribuzione OTA.

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

| Dipendenza | Chi la risolve | Blocca |
|---|---|---|
| Snapshot API e schemi effettivi | Team implementazione | DTO/client e test contrattuali reali, non il Mock |
| Sandbox, credenziali, scope, property/room/rate e connessioni test, reset | Octorate via referente CasaZen | Test autenticati e certificazione |
| Matrice OTA, mantenimento ID/review/storico, onboarding/migrazione | Octorate | Promessa commerciale e attivazione capacità interessate |
| API review, CIN, pre-booking, allegati, policy AI | Octorate + PO | Singole funzioni live; gap obbligatori nel go/no-go |
| Quote, bulk, SLA/latenza, costo per struttura/canale, minimi/certificazione/supporto | Octorate + referente commerciale | Capacity e sostenibilità pilota/rollout; non usare prezzi NextPax |
| Titolarità account/export/uscita, DPA, localizzazione/subfornitori, condizioni software vendor | Octorate + responsabili CasaZen | Uso dati reali/accordo partner |
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
| H5 — Sandbox | H2–H4, credenziali e API | Read-only prima delle scritture test; contratti, OAuth/rinnovo, 400/401/403/404/422 dove previsti, 429/5xx/timeout, quota e webhook; evidenze sanitizzate per canale |
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
- **G3 — Pronti al pilota:** autorizzazione commerciale/operativa, proprietà e durata
  concordate, mapping verificati, API e permessi necessari, rollback e responsabilità
  definiti. Qualsiasi riduzione di contenuti/chat/review/canali obbligatori richiede
  decisione esplicita PO/Quokka; una sandbox parziale non autorizza un pilota completo.
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
