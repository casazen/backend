# Registrazione RLI e firma elettronica: fattibilità delle integrazioni

> Task **RS-4** del risanamento, aggiornato il 2026-09-23. È un documento di ricerca: nessun codice è stato modificato.
> Serve a decidere **LT-01** (registrazione RLI, difetti A7-01 e A7-21) e **LT-02** (firma del contratto, difetti A7-02, A7-16 e A7-20), secondo la decisione **D15** di `Sessions/risanamento/DECISIONI.md`: se l'integrazione non è possibile senza un contratto, si adotta un flusso manuale onesto.

## Verdetti

| Task | Verdetto | In breve |
|---|---|---|
| **LT-01**: registrazione RLI via Openapi.it | **(a) fattibile con sandbox pubblica, con riserva** | L'API DocuEngine è documentata pubblicamente: specifica OpenAPI ufficiale, server di sandbox `test.docuengine.openapi.com`, autenticazione self-serve (email + API key, poi token Bearer), wallet prepagato. Dalle fonti pubbliche **non risulta che serva un contratto commerciale**. Restano tre riserve prima della produzione: (1) l'identificativo e i campi del servizio "Registrazione contratti di locazione" non sono nella specifica pubblica e si leggono solo con un account, tramite `GET /documents` (§1.7); (2) il deposito lo esegue un professionista per conto di Openapi, quindi servono il parere legale già aperto (`[COUNSEL_REQUIRED]` in `spec-ltr-rli-registration.md`) e `Rli:FilingEnabled=false` in produzione finché non arriva; (3) il costo per pratica, circa 12,90 €, va approvato. Il **percorso manuale** di A7-01 serve comunque come fallback. |
| **LT-02**: firma elettronica | **Flusso manuale** adesso. Provider consigliato quando si sblocca: **Yousign (ora Youtrust), API v3 con firma avanzata (AES)** | Per un contratto di locazione abitativa serve almeno la **FEA** (firma elettronica avanzata) perché il documento informatico valga come forma scritta (§3.1). Nessun provider con sandbox gratuita offre la FEA verso firmatari terzi in modalità del tutto self-serve e con prezzo pubblico: su Youtrust la AES è un add-on dei soli piani **annuali**, su Docusign serve un add-on con verifica dell'identità, InfoCert espone le API solo in base al profilo acquistato, Openapi offre ai terzi solo firma **semplice** (SES) con OTP. Resta aperto il punto legale sugli obblighi di chi "eroga" la FEA (DPCM 22/02/2013, art. 57). Yousign/Youtrust è il candidato migliore per la fase 2: documentazione pubblica completa, sandbox gratuita, webhook firmati HMAC, prestatore qualificato (QTSP) eIDAS, AES e QES via API. L'alternativa italiana è Namirial eSignAnyWhere (§3.3). |

---

## 0. Metodo e limiti (leggere prima di usare i dati)

- **Data di consultazione di tutte le fonti: 2026-09-23.**
- Il proxy di egress della sessione ha **bloccato l'accesso diretto** (`EGRESS_BLOCKED` con WebFetch, connessione rifiutata con curl) a questi domini: `openapi.com`, `console.openapi.com`, `static.openapi.com`, `agenziaentrate.gov.it`, `developers.yousign.com`, `developers.youtrust.com`, `youtrust.com`, `developer.docusign.com`, `namirial.com`, `infocert.it`, `normattiva.it`, `gazzettaufficiale.it`, `agid.gov.it`.
- `github.com` era raggiungibile. Da lì è stato clonato il repository **ufficiale** di Openapi SpA [github.com/openapi/openapi-skills](https://github.com/openapi/openapi-skills) (commit `fe121c2`, 2026-09-15). Contiene le specifiche OpenAPI 3 ufficiali dei servizi (`knowledge/oas/*.openapi.json`), che il README indica come copia di `https://console.openapi.com/oas/en/<servizio>.openapi.json`, e la FAQ della console (estratta il 2026-06-10). Openapi presenta il repository sul proprio blog: [openapi.com/blog/openapi-skills-...](https://openapi.com/blog/openapi-skills-the-new-github-repository-dedicated-to-ai-agent-capabilities).
- Livelli di verifica usati nel documento:
  - **V**: letto per intero in un file ufficiale, cioè le specifiche OpenAPI e la knowledge base del repository GitHub ufficiale di Openapi;
  - **U**: estratto **indicizzato** di una pagina ufficiale, letto tramite motore di ricerca e **non** per intero. Va considerato **non verificato** finché qualcuno non apre la pagina da una rete non filtrata;
  - **D**: dedotto, non letto;
  - **T**: fonte terza (blog, stampa, pagine commerciali). È un indizio, **non una fonte**.
- **Nessun endpoint è stato inventato.** Dove la documentazione pubblica non basta, il documento lo dice (§1.7, §3.3).

---

## 1. Openapi.it: registrazione dei contratti di locazione (DocuEngine)

L'adapter attuale è `Casazen.Infrastructure/External/OpenapiLeaseRegistrationProvider.cs`. È uno stub che restituisce `RLI-STUB-{id}` (A7-01). La configurazione `Openapi:{BaseUrl,ClientId,ClientSecret}` in `Casazen.Web/appsettings.json` **non corrisponde** al modello di autenticazione di Openapi (vedi §1.2).

### 1.1 Il servizio

| Fatto | Livello | Fonte |
|---|---|---|
| Openapi vende la registrazione telematica dei contratti di locazione come prodotto "Registrazione Contratti di Locazione tramite API", offerto dentro l'ecosistema **DocuEngine** | U | [openapi.com/products/lease-contract-services-italy](https://openapi.com/products/lease-contract-services-italy) |
| La procedura telematica ha la stessa validità della registrazione allo sportello dell'Agenzia e restituisce la **ricevuta ufficiale dell'Agenzia delle Entrate** | U | stessa pagina |
| Il servizio "è fornito tramite API **direttamente da un professionista qualificato**" e i tempi dipendono dalla complessità del caso. Quindi **non** è una trasmissione automatica: dietro la chiamata lavora un operatore umano | U | stessa pagina |
| Documento disponibile **entro 36 ore lavorative** | U | stessa pagina |
| Più di 11 tipologie contrattuali, immobili di qualsiasi categoria catastale, **fino a 3 locatori e 3 conduttori**, qualunque regime fiscale | U | stessa pagina |
| Target dichiarato: software immobiliari e contabili, gestionali di CAF e patronati, portali che gestiscono locazioni | U | stessa pagina |
| Openapi eroga servizi digitali **forniti da terzi** ("Fornitori"). I dati del fornitore compaiono nelle "Condizioni Specifiche" del singolo servizio | U | [CGC v2.0](https://static.openapi.com/documenti/CGC_ver20.pdf), [Termini e condizioni v3.0 (29/11/2024)](https://static.openapi.com/documenti/Termini-e-condizioni-Openapi.pdf) |

### 1.2 API, autenticazione e sandbox (V)

Fonte: `knowledge/oas/docuengine.openapi.json` (DocuEngine 1.0.0), `knowledge/oas/oauthv2.openapi.json` (OAuth 2.1.0) e `knowledge/platform-guide.md` del repository ufficiale.

| Ambiente | DocuEngine | OAuth (gestione token) |
|---|---|---|
| Produzione | `https://docuengine.openapi.com` | `https://oauth.openapi.com` |
| Sandbox | `https://test.docuengine.openapi.com` | `https://test.oauth.openapi.com` |

- **Autenticazione a due livelli.**
  1. Le credenziali dell'account (**email + API key**) si usano in HTTP Basic **solo** verso `oauth.openapi.com`.
  2. `POST /tokens` con `scopes` (formato `METHOD:host/path-prefix`, per esempio `POST:docuengine.openapi.com/requests`) e `ttl` (al massimo 1 anno) restituisce un **token Bearer**. Il token va in `Authorization: Bearer` su DocuEngine.
- I token si possono creare anche dalla console (sezione "Authentication", pulsante "+New Token"). La versione OAuth v1 (`oauth.openapi.it`) è **deprecata**.
- **Sandbox**: le richieste di test sono gratuite, ma prima bisogna attivare il "sandbox credit" in console. Le risposte sono **illustrative e possono essere incomplete**, quindi in sandbox non avviene alcun deposito reale.
- Endpoint DocuEngine:

| Metodo | Path | Uso |
|---|---|---|
| GET | `/documents` | Catalogo dei servizi, con `id`, prezzo, `isSync`, `hasSearch` e `requestStructure.fields` (i campi di input richiesti, con `validation`) |
| POST | `/requests` | Crea una richiesta: `documentId` (obbligatorio), `search` (campi `field0..fieldN`), `callback`, `notifyEmail`, `selectedOptions`. Con `state: "NEW"` la richiesta resta aperta e modificabile, senza `state` viene subito inoltrata |
| GET | `/requests` · `/requests/{id}` | Elenco e dettaglio delle richieste |
| PATCH | `/requests/{id}` | Modifica una richiesta ancora `NEW`. Con `state: "SEARCH"` la chiude e la manda in lavorazione |
| GET | `/requests/{id}/documents` | Documenti prodotti: `fileName`, `mimeType`, `md5`, `downloadUrl`, `urlExpire` |

- **Limite di frequenza** in produzione: 10.000 richieste al minuto (U, [documentazione DocuEngine in console](https://console.openapi.com/apis/docuengine/documentation)).

### 1.3 Formato dei dati

- `search` è un oggetto chiave/valore `field0..fieldN`. I **file** (contratto, documenti) si passano **in base64 oppure come URL** (V, schema `Search`).
- **I campi del servizio di locazione non sono nella specifica pubblica.** La specifica contiene solo esempi camerali (visure, statuti). Nomi, ordine, tipi e opzioni dei campi di input arrivano da `GET /documents`, che richiede un token (V).
- Dagli estratti indicizzati della pagina prodotto risultano questi campi: *codice fiscale locatore*, *documento locatore*, *tipologia contratto locazione*, date di inizio e fine, *canone annuo*, dati dell'immobile (comune, indirizzo, dati catastali), dati di locatori e conduttori, regime fiscale, *ufficio territoriale*, **codice IBAN** e **codice fiscale del titolare del conto** per l'addebito delle imposte (U, [pagina prodotto](https://openapi.com/products/lease-contract-services-italy)). **Da confermare** con `GET /documents` prima di scrivere il mapping.

### 1.4 Stati, callback e ricevute (V)

- **Stati** (`Request.state`): `NEW` → `SEARCH` → `WAIT` → `DONE`, oppure `CANCELLED` con `cancellationReason`. `timestamps` registra l'istante di ogni transizione. Con `DONE` i documenti sono scaricabili.
- **Callback**: `{ url, method: "POST"|"JSON", field, headers, data }`. `headers` è **obbligatorio** quando si imposta un callback (errore 311 "object headers not set in 'callback', required"). Il payload è l'oggetto `Request`.
  - La specifica **non prevede firma HMAC** del callback. L'unica protezione sono gli header scelti dal chiamante (D): mettere un segreto in un header e, alla ricezione, **rileggere lo stato con `GET /requests/{id}`** invece di fidarsi del payload. L'endpoint OAuth `GET /callbacks` permette di monitorare le consegne.
- **Ricevuta**: `GET /requests/{id}/documents` restituisce un `downloadUrl` firmato e temporaneo. Nell'esempio della specifica è un URL Google Cloud Storage con `X-Goog-Expires=86400`, cioè 24 ore, accompagnato da `urlExpire`. Il PDF va **copiato subito** nello storage privato (D8: Supabase Storage con URL firmati).
- **Estremi di registrazione**: l'oggetto `Request` **non ha un campo strutturato** per il codice o protocollo di registrazione (V). `RegistrationStatusResult.RegistrationCode` non si ricava quindi dall'API: va letto dalla ricevuta o inserito dal locatore (D).
- **Errori documentati**: 400 `wrong documentId` (202), **402** credito insufficiente nel wallet (610), 406 `search` non valido (205), 417 servizio momentaneamente non disponibile (208), 422 non elaborabile, 428 callback senza `headers` (311).

### 1.5 Costi

| Voce | Valore | Livello | Fonte |
|---|---|---|---|
| Registrazione di un contratto | **12,90 €** con ricarica prepagata | U | [pagina prodotto](https://openapi.com/products/lease-contract-services-italy) |
| IVA | "+ IVA" secondo la ricerca interna del 2026, da confermare | T | `Sessions/research-canone-concordato-mb.md` §10.1 |
| Imposta di registro e bollo | Addebitate sull'IBAN indicato nella richiesta (campi IBAN e CF titolare). Con cedolare secca registro e bollo non sono dovuti | U / D | pagina prodotto; [AdE, cedolare secca](https://www.agenziaentrate.gov.it/portale/aree-tematiche/casa/affitto/cedolare-secca) |
| Modello di pagamento | Wallet prepagato (PayPal, carta, bonifico; auto-ricarica solo con carta) oppure abbonamento per API | V | `knowledge/faq.md`, `knowledge/platform-guide.md` |
| Sandbox | Gratuita, previa attivazione del "sandbox credit" | V | idem |

### 1.6 Si usa senza contratto commerciale?

**Sì, secondo le fonti pubbliche.** Motivi:

- **Registrazione self-serve** con Google, GitHub o email (V, FAQ console). All'iscrizione servono nome e cognome oppure ragione sociale, indirizzo, codice fiscale o partita IVA ed email (U, [FAQ Openapi](https://openapi.com/faq)).
- **Token** creati in autonomia, via API o console (V).
- **Pagamento** a consumo dal wallet, **senza contratto quadro** (V). Le condizioni generali si accettano online (U, CGC).
- **Possibile verifica d'identità**: alcuni servizi soggetti al **TULPS (art. 6)** richiedono il caricamento in console di un documento del legale rappresentante o di un delegato (V, FAQ). Anche questo passaggio è self-serve. **Non è noto** se il servizio di locazione ne faccia parte (§1.7).
- **Nessuna fonte pubblica** indica abilitazioni non self-serve, come un contratto quadro o una convenzione, per il servizio di locazione.

### 1.7 Cosa manca: verifiche da fare con un account sandbox prima di LT-01

1. `GET https://test.docuengine.openapi.com/documents`: trovare il servizio "Registrazione contratto di locazione", annotare `id`, prezzo, `requestStructure.fields`, `validation`, `options`, `isSync` e `hasSearch`, e confermare che il servizio **esista anche in sandbox**.
2. Leggere in console le **Condizioni Specifiche** del servizio. Devono dire chi è il professionista che trasmette, quale mandato o delega serve dal locatore, se c'è un modulo da far firmare e se serve la verifica TULPS.
3. Capire se l'addebito avviene alla creazione `NEW` o al passaggio `SEARCH`. È rilevante per l'atomicità di A7-21: si potrebbe creare la richiesta `NEW` nella transazione e chiuderla con `PATCH` dopo il commit (D, da verificare).
4. Verificare il formato del PDF accettato per il contratto (l'Agenzia vuole PDF/A o TIFF, vedi §2.1) e se la ricevuta finale include gli estremi di registrazione.

### 1.8 Mappatura su `ILeaseRegistrationService` (indicazioni per LT-01)

| Metodo attuale | Chiamata Openapi | Note |
|---|---|---|
| `SubmitRegistrationAsync` | `POST /requests` con `documentId` da configurazione, `search.fieldN` dal lease, `callback` (`method: "JSON"`, `headers` con segreto) | Restituisce `data.id`, che diventa l'`ExternalRegistrationId`. Gestire il 402 (credito) come errore esplicito, **mai come successo** |
| `PollStatusAsync` | `GET /requests/{id}` | `WAIT` → in attesa. `DONE` → ricevuta disponibile. `CANCELLED` → `Failed` con `cancellationReason`. `RegistrationCode` resta `null` (§1.4) |
| `DownloadReceiptAsync` | `GET /requests/{id}/documents` → `downloadUrl` | Scaricare entro `urlExpire` e salvare nello storage privato. Non servire più il segnaposto `[RECEIPT PLACEHOLDER]` |
| Webhook (nuovo) | Callback DocuEngine | Controllare l'header segreto, poi rileggere lo stato con `GET /requests/{id}`. Risposta rapida e lavoro in un job in background (vedi `.claude/rules/gotchas.md`) |

Il termine RLI è di **30 giorni** dalla stipula e Openapi dichiara fino a **36 ore lavorative** di lavorazione. L'invio va quindi fatto con margine e i promemoria di LT-04 restano necessari.

### 1.9 Credenziali e configurazione necessarie (solo come variabili d'ambiente Railway, mai nel repository)

| Chiave proposta | Valore | Note |
|---|---|---|
| `Rli__FilingEnabled` | `false` in produzione finché non arriva il parere legale | Oggi è letto solo in un log (A7-01) |
| `Openapi__BaseUrl` | `https://test.docuengine.openapi.com` (test), `https://docuengine.openapi.com` (produzione) | |
| `Openapi__OAuthUrl` | `https://test.oauth.openapi.com`, `https://oauth.openapi.com` | Serve solo se il backend crea o rinnova i token da sé |
| `Openapi__Email` + `Openapi__ApiKey` | Credenziali dell'account console | **Sostituiscono** `ClientId` e `ClientSecret`, che non esistono nel modello Openapi |
| `Openapi__Token` (alternativa) | Token Bearer creato in console con scope minimi (`GET:`/`POST:`/`PATCH:docuengine.openapi.com/requests`, `GET:docuengine.openapi.com/documents`) e TTL breve | Da ruotare prima della scadenza |
| `Openapi__LeaseDocumentId` | `id` del servizio letto da `GET /documents` | Da verificare (§1.7) |
| `Openapi__CallbackUrl` + `Openapi__CallbackSecret` | URL pubblico del webhook e segreto inviato come header | Fail-fast all'avvio se contiene un segnaposto (stesso problema di A7-20) |
| Account e wallet | Account Openapi intestato a CasaZen con credito prepagato, eventuale documento TULPS | Decisione di prodotto: chi paga i 12,90 € |

---

## 2. Canali ufficiali dell'Agenzia delle Entrate

### 2.1 Cosa offre l'Agenzia

| Canale | Chi lo usa | Requisiti | Livello | Fonte |
|---|---|---|---|---|
| **RLI web** (servizio online in area riservata): compilazione, invio, pagamento con addebito sul conto, opzione cedolare | Il contribuente in prima persona | **SPID, CIE, CNS** oppure credenziali Entratel/Fisconline | U | [AdE, compilazione e invio via web](https://www.agenziaentrate.gov.it/portale/schede/fabbricatiterreni/registrazione-di-un-nuovo-contratto/compilazione-e-invio-via-web-regime-ordinario); [Carta dei servizi RLI](https://www.agenziaentrate.gov.it/portale/agenzia/amministrazione-trasparente/servizi-erogati/carta-servizi/contatta-agenzia/il-canale-internet-e-i-servizi-telematici/servizio-rli) |
| **Software RLI** (Java, gratuito) più invio del file telematico | Contribuenti abilitati e intermediari | Il file va **controllato** con il software di controllo AdE/Sogei prima dell'invio | U | [Software di compilazione RLI](https://www.agenziaentrate.gov.it/portale/schede/fabbricatiterreni/registrazione-di-un-nuovo-contratto/sw-comp-rli); [Software di controllo RLI](https://www.agenziaentrate.gov.it/portale/schede/fabbricatiterreni/registrazione-di-un-nuovo-contratto/software-di-controllo-rli) |
| **Specifiche tecniche** del file | Software house | Fornitura **XML** (`RLI12_v1.xsd`). Allegato B aggiornato il **04/11/2025**; istruzioni aggiornate il 14 e il 20/10/2025; modello approvato con provvedimento del 19/03/2019 | U | [Specifiche tecniche](https://www.agenziaentrate.gov.it/portale/schede/fabbricatiterreni/registrazione-di-un-nuovo-contratto/specifiche-tecniche-contratti-locazione); [Specifiche per intermediari](https://www.agenziaentrate.gov.it/portale/schede/fabbricatiterreni/registrazione-di-un-nuovo-contratto/specifiche-tecniche-contratti-locazione-intermediari); [Allegato B, Specifiche_RLI12_20251104.pdf](https://www.agenziaentrate.gov.it/portale/documents/20143/9390371/Specifiche_RLI12_20251104.pdf/b233027c-907a-7564-a340-6ffa74772222); [Motivi dell'aggiornamento, 14/10/2025](https://www.agenziaentrate.gov.it/portale/documents/20143/9390371/Motivi+dell'aggiornamento+Istruzioni+RLI_14+ottobre+2025.pdf/1e8938fb-1526-3e9d-0a3f-e4710ac73000?t=1760440139819) |
| **Intermediario abilitato Entratel** (commercialisti, CAF, associazioni di categoria e altri) | Chi trasmette per conto terzi | Categorie dell'art. 3, comma 3 del DPR 322/1998, ampliate dal DM 19/04/2001 a chi svolge **abitualmente consulenza fiscale**. Serve una **partita IVA autonoma** con attività coerente | U | [FiscoOggi (rivista AdE): abilitazione Entratel](https://www.fiscooggi.it/portale/-/abilitazione-entratel-circoscritta-alle-attivit%C3%A0-previste-per-legge) |
| Soggetti dell'**art. 15 del DM 31/07/1998** (registrazione telematica delle locazioni) | Agenti, associazioni della proprietà e degli inquilini in convenzione e altri | Abilitazione Entratel. Il canale telematico è **obbligatorio** per gli agenti immobiliari e per chi possiede almeno 10 immobili | U | [AdE, RLI: che cos'è](https://www.agenziaentrate.gov.it/portale/schede/fabbricatiterreni/registrazione-di-un-nuovo-contratto/schedainfo-regime-ordinario); [AdE, normativa e prassi](https://www.agenziaentrate.gov.it/portale/web/guest/schede/fabbricatiterreni/registrazione-di-un-nuovo-contratto/normativa-e-prassi-regime-ordinario) |
| Sportello | Contribuente | Modello cartaceo più gli originali | U | [AdE, registrazione in ufficio](https://www.agenziaentrate.gov.it/portale/schede/fabbricatiterreni/registrazione-di-un-nuovo-contratto/registrazione-in-ufficio-regime-ordinario) |

- Allegato con RLI web: copia del contratto firmato in **PDF/A (1a o 1b) o TIFF**. In alcuni casi non è obbligatoria, per esempio tra privati, con non più di 3 locatori o conduttori e una sola unità abitativa (U, [guida RLI web 2024](https://jws.agenziaentrate.it/jws/registro/2012/RLI12/localAppRoot/help/guida_rliweb_PR_2024.pdf); [AdE, compilazione e invio via web](https://www.agenziaentrate.gov.it/portale/schede/fabbricatiterreni/registrazione-di-un-nuovo-contratto/compilazione-e-invio-via-web-regime-ordinario)).

### 2.2 Esiste un web service o un'API dell'Agenzia per terzi?

**Nessuno trovato.** A differenza di Alloggiati Web, che ha un web service SOAP pubblico (vedi `.claude/context/regulations/alloggiati.md`), per l'RLI le fonti ufficiali descrivono solo tre canali: servizio web interattivo, software e file XML da trasmettere con Entratel/Fisconline, sportello. Nel catalogo PDND non è emerso alcun e-service RLI per soggetti privati (U, [catalogo API PDND](https://api.gov.it/it/catalogo)).

**Conseguenza (D):** CasaZen è una software house SaaS e non rientra nelle categorie abilitabili a Entratel (art. 3, comma 3 DPR 322/1998; art. 15 DM 31/07/1998). Non può quindi trasmettere l'RLI per conto dei locatori. Restano tre strade:

1. **Openapi**, dove trasmette il professionista del fornitore (§1);
2. il **commercialista o CAF** del locatore;
3. il **locatore in prima persona** con RLI web.

Un'opzione futura, fuori dallo scope di LT-01, è generare un file XML RLI precompilato secondo le specifiche tecniche, da consegnare all'intermediario del locatore.

---

## 3. Firma elettronica del contratto

L'adapter attuale è `Casazen.Infrastructure/External/LeaseESignHttpAdapter.cs`: uno stub con link verso `sign.provider.example.com` (A7-02) e un webhook senza controllo di stato né segreto reale (A7-20).

### 3.1 Quadro legale italiano: livello di firma richiesto

| Regola | Effetto per CasaZen | Livello | Fonte |
|---|---|---|---|
| **L. 431/1998, art. 1, comma 4**: i contratti di locazione abitativa richiedono la **forma scritta** a pena di nullità | Il contratto firmato deve valere come "scritto" | U | [L. 431/98 (parlamento.it)](https://www.parlamento.it/parlam/leggi/98431l.htm) |
| **CAD (D.Lgs. 82/2005), art. 20, comma 1-bis**: il documento informatico soddisfa la forma scritta e ha l'efficacia dell'art. 2702 c.c. con **firma digitale, altra firma qualificata o firma avanzata** (oppure con un processo conforme alle linee guida AgID). **Negli altri casi**, cioè con firma semplice, l'idoneità è **liberamente valutabile in giudizio** | **La firma semplice (SES, per esempio solo OTP) non garantisce la forma scritta**: serve almeno la **FEA** | U | [AgID, art. 20 CAD](https://www.agid.gov.it/it/codice-dellamministrazione-digitale/art-20-validita-ed-efficacia-probatoria-documenti-informatici); [docs.italia.it, art. 20](https://docs.italia.it/italia/piano-triennale-ict/codice-amministrazione-digitale-docs/it/v2018-09-28/_rst/capo2_sezione1_art20.html) |
| **CAD, art. 21, comma 2-bis**, e **art. 1350 c.c., n. 8** (locazioni di immobili **oltre nove anni**): le scritture private dei numeri 1–12 fatte con documento informatico richiedono, a pena di nullità, **firma qualificata o digitale** | Contratti oltre 9 anni: la FEA **non basta** | U | [docs.italia.it, art. 21](https://docs.italia.it/italia/piano-triennale-ict/codice-amministrazione-digitale-docs/it/v2018-09-28/_rst/capo2_sezione1_art21.html); [art. 1350 c.c. (Gazzetta Ufficiale)](https://www.gazzettaufficiale.it/atto/serie_generale/caricaArticolo?art.versione=1&art.idGruppo=168&art.flagTipoArticolo=2&art.codiceRedazionale=042U0262&art.idArticolo=1350&art.idSottoArticolo=1&art.idSottoArticolo1=10&art.dataPubblicazioneGazzetta=1942-04-04&art.progressivo=0) |
| **AdE, Risoluzione n. 23/E dell'08/04/2021**: le scritture private firmate con **FEA** si registrano con le regole ordinarie. Il caso esaminato riguardava una FEA con tecnologia Namirial in ambito immobiliare | La FEA è accettata anche ai fini della registrazione | U | [Risoluzione 23/E (PDF AdE)](https://www.agenziaentrate.gov.it/portale/documents/20143/3365003/risoluzione+n.+23+Pubblicazione_senza_firmatario.pdf/fe7e11b7-699f-2c56-52ec-f3ce1245540b); [FiscoOggi](https://www.fiscooggi.it/portale/-/registrazione-atti-privati-il-punto-sulla-sottoscrizione) |
| **DPCM 22/02/2013, art. 57**: chi **eroga** soluzioni di FEA deve identificare l'utente con un documento valido, raccogliere la dichiarazione di accettazione delle condizioni, **conservare per 20 anni** documento e dichiarazione, e avere una **polizza RC con massimale di almeno 500.000 €** | Se CasaZen fosse considerata "erogatore" della FEA verso locatori e conduttori, questi obblighi ricadrebbero su di lei. **Serve un parere legale** | U | [DPCM 22/02/2013, art. 57 (Gazzetta Ufficiale)](https://www.gazzettaufficiale.it/atto/serie_generale/caricaArticolo?art.versione=1&art.idGruppo=5&art.flagTipoArticolo=0&art.codiceRedazionale=13A04284&art.idArticolo=57&art.idSottoArticolo=1&art.idSottoArticolo1=10&art.dataPubblicazioneGazzetta=2013-05-21&art.progressivo=0); [testo su agid.gov.it](https://www.agid.gov.it/sites/default/files/repository_files/leggi_decreti_direttive/dpcm_22_febbraio_2013_-_nuove_regole_tecniche.pdf) |
| Allegato RLI in PDF/A o TIFF (§2.1) | Il PDF firmato va conservato. Per l'RLI serve una copia PDF/A | U | §2.1 |

> **Attenzione alle fonti dei provider.** La pagina italiana di Yousign ([blog, locazione a distanza](https://youtrust.com/it-it/blog/firmare-un-contratto-di-locazione-Italia)) scrive che per i contratti fino a 9 anni "l'unica firma valida per la registrazione è la FEQ o digitale", **in contrasto** con la Risoluzione 23/E/2021. In altre pagine suggerisce la firma OTP per le locazioni, cioè una SES. Vanno considerate **T** e non usate per decisioni legali: prevalgono CAD e prassi AdE.

### 3.2 Confronto dei provider

| Provider | Documentazione API pubblica | Sandbox gratuita | Webhook | FEA / FEQ verso firmatari terzi via API | Costi indicativi | Self-serve per la produzione con FEA |
|---|---|---|---|---|---|---|
| **Yousign / Youtrust** (FR, QTSP ANSSI) | Sì, API v3 REST | Sì. Prova API di 40 giorni con solo sandbox | **HMAC SHA-256** (`X-Yousign-Signature-256`) | AES con verifica del documento d'identità; QES con documento, video-liveness e OTP | Piano API "Plus" da circa 104–106 €/mese su base annua; 2,00 € per firma oltre la quota | **No**: AES è un **add-on dei piani annuali** Plus/Pro/Scale |
| **Namirial eSignAnyWhere** (IT, QTSP AgID) | Sì, Confluence e Swagger REST v6 | Sì, account demo gratuito su `demo.esignanywhere.net` | Callback URL con segnaposto, **senza firma** | FEA con OTP SMS e grafometrica; QES nel piano Premium | Da 15 €/mese; Basic 1.000 documenti/anno solo SES; Premium 2.000 documenti/anno con OTP, grafometrica e QES | Da verificare: non è chiaro se l'accesso API in produzione sia incluso nei piani online o passi dal commerciale |
| **Docusign** (USA/UE, QTSP UE) | Sì | Sì, account sviluppatore gratuito e senza scadenza (documenti con filigrana e non validi) | **Connect con HMAC** | "EU Advanced" con ID Verification, "EU Qualified" | API Starter 50 $/mese (40 buste al mese), Intermediate 300 $, Advanced 480 $ | **No**: EU Advanced e IDV sono add-on su piano base, da definire con il commerciale |
| **InfoCert GoSign** (IT, QTSP) | Portale sviluppatori con REST e SDK Java/.NET | **Non trovata** pubblicamente | Non documentati negli estratti | FEA grafometrica e QES | Prezzi API non pubblici | **No**: API "in base al profilo acquistato" |
| **Openapi eSignature** (IT, stesso fornitore dell'RLI) | Sì, OAS 1.0.17 letta per intero (V) | Sì, `test.esignature.openapi.com` | Callback con header personalizzati e fino a 5 retry | Per firmatari terzi **solo `EU-SES`** (SES con OTP). Le QES `EU-QES_*` usano certificati del titolare, da acquistare per ciascun firmatario (Namirial, validi 3 anni) | "da 0,09 €" per firmatario in abbonamento, 0,49 € a richiesta singola | Sì, ma **solo SES**: **non adatta** alla forma scritta del contratto |

### 3.3 Dettagli per provider

#### Yousign (Youtrust) — consigliato per la fase 2

- **Marchio**: Yousign ha cambiato nome in **Youtrust** il 16/07/2026 (T, stampa: [Maddyness](https://www.maddyness.com/2025/11/06/en-2026-yousign-deviendra-youtrust/)). La documentazione vive su `developers.yousign.com` e `developers.youtrust.com`; gli host delle API restano `yousign.app` (U).
- **Ambienti**: sandbox `https://api-sandbox.yousign.app/v3`, produzione `https://api.yousign.app/v3`. I due ambienti sono isolati. Durante la prova API gratuita di 40 giorni è disponibile solo la sandbox (U, [Environments](https://developers.youtrust.com/docs/environments-new)).
- **Autenticazione**: API key in `Authorization: Bearer`, creata e revocata dall'app (U, [API Keys](https://developers.yousign.com/docs/api-keys)).
- **Livelli di firma**: `signature_level: "advanced_electronic_signature"` prevede la verifica del documento d'identità del firmatario. Esistono anche la pre-verifica dell'identità e l'AES con Delegated Registration Authority. La QES richiede documento, video verificato da un operatore e OTP (U, [Set the signature level](https://developers.yousign.com/docs/set-the-signature-level), [AES pre-verify](https://developers.yousign.com/docs/pre-verify-the-identity-of-a-signer-for-advanced-electronic-signature), [QES](https://developers.yousign.com/docs/qualified-signature)).
- **Webhook**: HMAC SHA-256 del **corpo grezzo** con il segreto della sottoscrizione, prefisso `sha256=`, nell'header `X-Yousign-Signature-256`. Esiste l'evento `signature_request.done` (U, [Use webhooks](https://developers.yousign.com/docs/use-webhooks-in-your-app)). Il meccanismo risolve alla radice la parte "segreto" di A7-20.
- **Costi**: piani API mensili o annuali; oltre la quota 2,00 € per firma; "API Plus" da circa 104–106 €/mese su base annua (valori discordanti negli estratti). **L'AES è un add-on dei piani annuali Plus, Pro e Scale** (U, [Pricing API](https://youtrust.com/pricing-api), [Help: AES](https://help.youtrust.com/en/articles/111357-discover-advanced-electronic-signature-aes)).
- **Valore legale**: prestatore qualificato dall'**ANSSI** e presente nella Trusted List francese, quindi riconosciuto in tutta l'UE (U, [Help: eIDAS](https://help.youtrust.com/en/articles/73157-youtrust-the-eidas-regulation)). Una AES eIDAS è una FEA ai sensi del CAD (D). Per i contratti oltre 9 anni serve la QES.
- **Perché è il consigliato**: è l'unico candidato che unisce documentazione API completa e pubblica, sandbox gratuita, **webhook firmati**, AES e QES via API e prezzi in gran parte pubblici. Il limite è l'impegno annuale per l'AES.

#### Namirial eSignAnyWhere — alternativa italiana

- **Demo gratuita**: registrazione libera su `https://demo.esignanywhere.net`. Per più funzioni serve il commerciale (U, [Confluence: API Documentation](https://confluence.namirial.com/display/eSign/API+Documentation), [Developer FAQ](https://confluence.namirial.com/display/eSign/Developer+FAQ)).
- **API REST**: Swagger su `https://demo.esignanywhere.net/Api/swagger/ui/index`. La v6 è la raccomandata e la v5 è ancora supportata (U, [status.namirial.com](https://status.namirial.com/maintenance/535537)).
- **Autenticazione**: API token dell'utente oppure organization key più login utente negli header (U, [Api Token and Apps](https://confluence.namirial.com/display/eSign/Api+Token+and+Apps)).
- **Callback**: `CallbackUrl` scatta sullo stato finale, `StatusUpdateCallbackUrl` sugli eventi. Sono URL con segnaposto, sui SaaS condivisi solo HTTPS, e vanno confermati subito con HTTP 200 (U, stessa pagina). **Non risulta una firma dei callback**: servono un token segreto nell'URL e una rilettura dello stato via API (D).
- **Piani**: "a partire da 15 €/mese". Basic: 1.000 documenti all'anno con SES. Premium: 2.000 documenti all'anno con OTP SMS, grafometrica e QES (U, [namirial.it](https://www.namirial.it/dettagli/esignanywhere/), [pricing](https://www.esignanywhere.net/en/pricing/)).
- **Valore legale**: QTSP nell'elenco AgID (U, [focus.namirial.com](https://focus.namirial.com/it/firma-elettronica-avanzata-o-qualificata-guida/)). Una FEA di tecnologia Namirial è il caso della Risoluzione AdE 23/E/2021.
- **Perché è seconda scelta**: callback non firmati, documentazione meno lineare, accesso API in produzione con FEA non chiaro dai piani pubblici.

#### Docusign

- **Account sviluppatore** gratuito e senza scadenza nell'ambiente demo. I documenti hanno filigrana, **non sono validi** e vengono rimossi dopo 30 giorni (U, [Support: tipi di account](https://support.docusign.com/s/articles/What-are-my-options-for-a-DocuSign-Trial-account?language=en_US), [Developer account](https://developers.docusign.com/platform/account/)).
- **Webhook**: Connect con **HMAC** (U, [Docusign blog: HMAC](https://www.docusign.com/blog/developers/introducing-hmac-partners-docusign-connect)).
- **Costi**: piani API Starter 50 $/mese (40 buste al mese), Intermediate 300 $/mese, Advanced 480 $/mese (U, [ecom.docusign.com](https://ecom.docusign.com/plans-and-pricing/developer)).
- **EU Advanced**: richiede **ID Verification**. Gli add-on si acquistano solo sopra un piano base (U, [Attachment EU Advanced](https://www.docusign.com/legal/terms-and-conditions/schedule-docusign-signature/attachment-eu-advanced-signature)).
- **Valore legale**: QTSP UE (U, [Docusign: Italia](https://www.docusign.com/products/electronic-signature/legality/italy)).
- **Scartato**: è il più costoso e l'AES passa da add-on non self-serve.

#### InfoCert GoSign

- Il portale sviluppatori ha REST API, SDK Java/.NET e autenticazione con API key o token "Customer Code" (U, [developers.infocert.digital/gosign](https://developers.infocert.digital/gosign/), [Integration methodologies](https://developers.infocert.digital/pagina_gosign_old/integration-methodologies/)).
- L'integrazione API è possibile "in base al profilo acquistato" (U, [infocert.it, grandi aziende](https://www.infocert.it/grandi-aziende/gosign)).
- **Non** è stata trovata una sandbox gratuita pubblica né un prezzo API.
- **Scartato** per mancanza di un percorso self-serve.

#### Openapi eSignature (V, `knowledge/oas/esignature.openapi.json` v1.0.17)

- **Server**: `https://esignature.openapi.com`, sandbox `https://test.esignature.openapi.com`. Stessa autenticazione e stesso wallet del servizio RLI.
- **`POST /EU-SES`**: restituisce un link per ciascun firmatario alla webapp di firma, con autenticazione **OTP via SMS o email**. È una **firma semplice**.
- **`EU-QES_automatic` ed `EU-QES_otp`**: firmano con un certificato **del titolare**, da acquistare con `POST /certificates/namirial-*` (validità 3 anni, identificazione presso la CA). Adatti a far firmare il locatore professionista, non un conduttore occasionale.
- **Stati**: `WAIT_VALIDATION`, `WAIT_SIGN`, `WAIT_SIGNER`, `DONE`, `ERROR`.
- **Callback**: `method` `JSON` o `POST`, `headers` per auth basic o bearer, `retry` fino a 5, timeout di 30 secondi.
- **Conservazione**: documenti firmati per **30 giorni**, audit trail per **1 anno**. Vanno copiati nello storage.
- **Nota**: un estratto indicizzato cita un endpoint `EU-AES` (U, [console eSignature](https://console.openapi.com/apis/esignature/documentation)), ma **non compare** nella specifica ufficiale di settembre 2026. Va trattato come non disponibile.
- **Costi**: "da 0,09 €" per firmatario in abbonamento, 0,49 € a richiesta singola (U, [openapi.com eSignature](https://openapi.com/products/european-esignature)).
- **Scartato per i contratti di locazione**: la sola SES non garantisce la forma scritta (§3.1).

---

## 4. Verdetti dettagliati

### LT-01: registrazione RLI → **(a) fattibile con sandbox pubblica, con riserva**

- **Tecnica**: API REST documentata (V). Sandbox ufficiale `test.docuengine.openapi.com`. Autenticazione, wallet e token self-serve. Nessun contratto commerciale richiesto secondo le fonti pubbliche (V/U).
- **Riserve bloccanti per la produzione, non per lo sviluppo**:
  1. identificativo del servizio e campi da leggere con `GET /documents` su un account sandbox (§1.7);
  2. parere legale sul ruolo del professionista del fornitore e sulla delega del locatore (`[COUNSEL_REQUIRED]` di `spec-ltr-rli-registration.md`); fino ad allora `Rli:FilingEnabled=false` in produzione;
  3. approvazione del costo per pratica (circa 12,90 €).
- **Da implementare in LT-01**, in ordine:
  1. correzione di A7-01: con il flag spento, 409 `RliFilingDisabled`, nessuna chiamata al provider e **percorso manuale** (`POST /{id}/registration/manual` con estremi e ricevuta caricata). Il percorso manuale resta il fallback anche con Openapi attivo;
  2. adapter Openapi reale dietro il flag, con la configurazione del §1.9 e la mappatura del §1.8, testato contro la sandbox;
  3. atomicità di A7-21.
  - La ricevuta va copiata nello storage privato. Il `RegistrationCode` va inserito o confermato dal locatore, perché l'API non lo fornisce in forma strutturata.

### LT-02: firma del contratto → **flusso manuale** ora; **Yousign (Youtrust) con AES** per la fase 2

- **Perché manuale ora**:
  - la validità della locazione richiede **almeno la FEA** (FEQ oltre 9 anni);
  - nessun provider offre la FEA verso firmatari terzi in modalità del tutto self-serve: su Youtrust serve un piano **annuale** con add-on, su Docusign un add-on con IDV, su InfoCert un profilo commerciale, e per Namirial non è chiaro;
  - l'unica opzione self-serve immediata (Openapi SES, o la SES di qualunque provider) **non garantisce la forma scritta**;
  - gli obblighi del DPCM 22/02/2013, art. 57 richiedono un parere legale prima che CasaZen offra la FEA.
- **Flusso manuale per LT-02, coerente con A7-02 e D15**:
  1. flag `ESign:Enabled=false`;
  2. `GET /{id}/contract.pdf` per scaricare il contratto;
  3. firma fuori piattaforma: autografa, oppure firma digitale o FEA delle parti con strumenti propri;
  4. upload del PDF firmato (`POST /{id}/signed-document`), preferibilmente anche in PDF/A per l'RLI;
  5. azione "segna come firmato" con la **data di stipula**, che fa partire il termine RLI (LT-04);
  6. pannello firmatari nascosto quando il flag è spento.
- **Webhook e-sign** anche in modalità manuale, correzione di A7-20: guard sugli stati di partenza, idempotenza e **fail-fast** se `ESign:WebhookSecret` è un segnaposto.
- **Fase 2**, dopo budget e parere legale: adapter Yousign API v3 con `advanced_electronic_signature` (QES per i contratti oltre 9 anni), webhook verificato con `X-Yousign-Signature-256`, persistenza dei firmatari e dei link (A7-16), copia del PDF firmato nello storage.
  - Configurazione: `ESign__BaseUrl` (`https://api-sandbox.yousign.app/v3` in test, `https://api.yousign.app/v3` in produzione), `ESign__ApiKey`, `ESign__WebhookSecret`, `ESign__Enabled`. Solo su Railway.

---

## 5. Punti aperti per il product owner e il legale

1. **Costo RLI via Openapi** (circa 12,90 € più IVA a pratica): lo assorbe CasaZen o lo paga il locatore? Serve anche un wallet aziendale con auto-ricarica.
2. **Parere legale RLI**: CasaZen come "software facilitator" che instrada la pratica al professionista di Openapi. Delega del locatore, responsabilità, testo dell'attestazione (`Rli:AttestationText` è ancora una bozza). È già `[COUNSEL_REQUIRED]` nelle spec.
3. **Parere legale FEA**: se CasaZen integra un provider FEA, diventa "erogatore" ai sensi del DPCM 22/02/2013, art. 57, con identificazione, conservazione per 20 anni e polizza da 500.000 €? Oppure l'erogatore è il locatore?
4. **Budget per la firma**: impegno annuale su un piano API Youtrust con add-on AES, contro la sola firma manuale.
5. **Opzione futura**: generare il file XML RLI precompilato (specifiche AdE 04/11/2025) da consegnare al commercialista del locatore?

---

## 6. Riferimenti interni

- `Sessions/audit-2026-09-23/A7-ltr.md`: A7-01, A7-02, A7-16, A7-20, A7-21.
- `Sessions/specs/spec-ltr-rli-registration.md`: AC1, AC8, AC9 e i `[COUNSEL_REQUIRED]`.
- `Sessions/research-canone-concordato-mb.md`: §8.1 (canali RLI) e §10.1 (Openapi).
- `.claude/context/regulations/canone_concordato.md`: CasaZen non è intermediario abilitato.
- Repository ufficiale Openapi: [github.com/openapi/openapi-skills](https://github.com/openapi/openapi-skills), commit `fe121c2` (2026-09-15). File letti: `knowledge/oas/docuengine.openapi.json`, `knowledge/oas/oauthv2.openapi.json`, `knowledge/oas/esignature.openapi.json`, `knowledge/platform-guide.md`, `knowledge/faq.md`, `knowledge/references.md`.
