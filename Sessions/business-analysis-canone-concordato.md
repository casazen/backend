# Requisiti per Helix — integrazione ConcordatoFacile

Revisione 7 · 22 ottobre 2026. Questo è l’unico documento di requisiti da fornire a Helix. Descrive il lavoro ancora necessario per il servizio CF; accordi e percorso dell’utente sono nella [documentazione del flusso](../docs/integrations/concordatofacile/README.md). Sostituisce le precedenti proposte di integrazione, senza richiedere di sommarne le funzionalità.

## 1. Base da riusare e lavoro restante

Le PR [frontend #315](https://github.com/casazen/frontend/pull/315) e [backend #561](https://github.com/casazen/backend/pull/561) sono integrate in `develop` e realizzano già il caricamento e la conservazione. Le successive PR [frontend #316](https://github.com/casazen/frontend/pull/316) e [backend #562](https://github.com/casazen/backend/pull/562) correggono i controlli CI, il ritorno dal login e il riferimento temporale del controllo APE: verificare il codice corrente prima di intervenire. Integrazione in `develop` e CI verde non attestano disponibilità in produzione. Leggere AGENTS.md dei repository e lavorare sul codice corrente.

| Già implementato nelle PR di codice: riusare | Resta a Helix |
|---|---|
| Pagina `/app/long-rent/documents/upload?leaseId=…`, login con ritorno allo stesso contratto | Tipo di contratto come prima scelta; bozza minima concordato e collegamento al servizio CF |
| Allegati `LeaseDocument`, API autorizzate elenco/upload/download, storage privato e isolamento OrgId | Pagina offerta, pagamento singolo 119 € IVA compresa, flag pratica omaggio biennale e bypass admin CasaZen |
| Sezione Documenti del contratto, raggiungibile anche aprendo il contratto dalla casa | Link CF firmato dal server al clic, dopo verifica del diritto; prevenzione delle riaperture improprie |
| Un PDF esistente oppure più foto JPEG/PNG riordinate/ruotate e unite in un solo PDF scaricabile; le foto non sono conservate separatamente | Estrazione dai documenti del contratto e popolamento delle rispettive sezioni dello stesso leaseId |
| OCR e controlli fondamentali APE, nel distinto archivio della casa | Client stato CF v0.1, snapshot applicativo di 15 minuti e ritorno con pratica/conferma del contratto |

Fonti: «ConcordatoFacile — specifica endpoint di stato v0.1», ottobre 2026, governa link e letture di stato; della precedente «01-technical-specification», v1.3 del 13 ottobre 2026, si riusano soltanto §2 per HMAC/vettore e le convenzioni esplicitamente richiamate dalla v0.1. Non implementare webhook, download automatico dei file, vecchi caseId/eventi, riapertura garantita o altri requisiti v1.3 superati dal nuovo flusso. Le differenze aperte sono riportate sotto, senza inventare accordi.

Non ricostruire pagina upload, assemblaggio foto/PDF, OCR APE o un secondo archivio. La funzione documentale multipagina è generale, non un nuovo requisito CF. Il suo riuso nell’importazione contrattuale non implica che il controllo APE sappia già estrarre i dati di un contratto.

### Relazioni vincolanti

- Casa → documenti della casa: APE, planimetria e documenti propri dell’immobile.
- Casa → contratti; contratto → documenti del contratto: contratto definitivo, attestazione, registrazione e altri allegati.
- Il caricamento di allegati del contratto richiede `leaseId`; `propertyId` non lo sostituisce. La casa si ricava dal contratto autorizzato. Dalla casa si apre il contratto e poi la sua sezione Documenti.
- `leaseId` è l’UUID reale e stabile di `LeaseContract`, anche quando è una bozza. Non usare l’ID della casa, del pagamento o della pratica CF. L’importazione non crea un secondo contratto.

Punti di riuso: frontend `src/config/route-manifest.ts`, `src/api/leases.api.ts`, `src/queries/use-leases.ts`, `LeaseDocumentsPanel`, `src/lib/document-pdf.ts`; backend `LeaseDraftService`, `LeaseDocumentService`, `IApeDocumentInspector`, storage e autorizzazioni esistenti. Le API allegati sono `GET/POST /api/leases/{leaseId}/documents` e `GET /api/leases/{leaseId}/documents/{documentId}/download`.

## 2. Ingresso e bozza minima concordato

Il flusso di prodotto da realizzare parte dalla scelta del tipo di contratto, prima di parti, termini economici o calcolatore. Per il canone concordato il vero contratto è preparato da CF e poi importato in CasaZen.

1. Con casa selezionata e tipo concordato, salvare subito una bozza minima `LeaseContract` con PropertyId, OrgId, tipo/provenienza CF e restituire leaseId. Verificare e riusare `POST /api/leases/drafts` / `LeaseDraftService`; non generare soltanto un UUID nel browser.
2. Non richiedere APE, parti, canone, deposito, date, coefficienti o attestazione per ottenere il riferimento della bozza. Non allentare per questo i controlli dei diversi percorsi di firma/registrazione già esistenti.
3. Mostrare casa, leaseId copiabile e «Bozza — in attesa del contratto del partner». I dati mancanti restano da acquisire, senza importi zero, date o persone fittizie presentate come definitive. La bozza non genera occupazione, incassi o adempimenti di un contratto attivo.
4. Proteggere il leaseId già usato per un acquisto o una pratica CF dalla cancellazione automatica delle bozze abbandonate: verificare `LeaseDraftRetentionService` e `LeaseDraftDeleter`, senza disabilitare la retention generale. Un contratto sospeso dal partner non deve perdere documenti o riferimento per inattività.
5. Doppi click e retry della stessa azione riusano la bozza salvata; rimane possibile creare intenzionalmente un nuovo contratto sulla stessa casa.
6. Da quella bozza si accede all’offerta/pagamento o all’omaggio. L’admin CasaZen segue l’accesso diretto descritto sotto. Il percorso libero mantiene il proprio funzionamento: non inviarlo al modulo riservato ai concordati.

## 3. Offerta, pagamento e pratica omaggio

### Pagina servizio

Percorso proposto: `/app/long-rent/leases/:id/concordatofacile`. Mostrare casa e contratto, prezzo **119,00 € totali IVA compresa per pratica**, eventuale omaggio e questi passaggi prima del pagamento:

1. Dopo il pagamento o l’uso dell’omaggio vieni reindirizzato al modulo del partner ConcordatoFacile.
2. Compili il modulo con i dati e i documenti richiesti.
3. Gestisci direttamente via email con il partner integrazioni, revisione del contratto e firma, fino alla registrazione prevista dal servizio.
4. Ricevi l’email finale del partner con i documenti e il link per caricarli in CasaZen.
5. Ti consigliamo di caricarli sul tuo contratto per conservare e gestire tutti i contratti in modo centralizzato in CasaZen.

Registro e bollo eventualmente dovuti sono esclusi. Il caricamento è effettuato dall’utente: non promettere un trasferimento automatico dei file da CF. I costi di acquisto dal fornitore non sono il prezzo dell’utente e non vanno esposti nel checkout.

| Condizione verificata dal server | Azione |
|---|---|
| Utente senza diritto | «Paga 119 € e continua» → checkout CasaZen una tantum |
| Omaggio disponibile | «Usa la pratica omaggio e continua» → assegnazione atomica a questo leaseId, poi CF |
| Pratica già pagata o omaggio già associato a questo leaseId | Nessun nuovo addebito/consumo; accesso al modulo solo se consentito dallo stato CF, altrimenti scheda pratica |
| Admin di piattaforma CasaZen | Accesso diretto dal contratto, senza pagina di pagamento e senza consumare omaggi |
| Pagamento in attesa, fallito o annullato | Stato comprensibile, nessun accesso CF sbloccato |

### Controlli e persistenza

- Usare il billing CasaZen esistente (`BillingController`, `BillingCheckoutService`, `StripeBillingService`, `ProcessedStripeEvent`, `StripeWebhookJob` e frontend billing). Aggiungere un acquisto **una tantum**, senza cambiare l’abbonamento e senza utilizzare pagamenti degli affitti o Stripe Connect.
- Il server determina EUR **11900 centesimi, IVA già inclusa**. Price/configurazione fiscale devono mantenere il totale; non aggiungere IVA sopra i 119 €. L’esenzione dichiarata dall’associazione non si applica automaticamente alla vendita CasaZen. Riusare la documentazione fiscale CasaZen pertinente.
- Il diritto è legato a OrgId e LeaseId e ne conserva origine, stato, identificativi del pagamento e date necessarie. Sbloccare da pagamento confermato server, con eventi Stripe verificati e corrispondenza di acquisto, importo, valuta e contratto. Il ritorno `checkout=success` non è una prova.
- Solo l’admin di **piattaforma** (`CasazenPolicies.AdminOnly`, ruolo JWT `Admin`) salta il pagamento. L’admin dell’organizzazione o `OrgBillingAdmin` non ha questo bypass. Mantenere autorizzazione al contratto e audit; non simulare un pagamento admin.
- L’abbonamento effettivamente sottoscritto per **due anni** assegna una sola pratica omaggio all’organizzazione abbonata, condivisa tra i membri autorizzati. Non equivale a 24 pagamenti mensili o due anni di anzianità. Verificare il mapping del piano/Price; se manca, evidenziare la configurazione necessaria senza creare prodotti Stripe live come prova.
- Flag DB persistente, ad esempio `HasConcordatoGift`, con riferimento al leaseId beneficiario e date/identificativo abbonamento, o equivalente. Migrazione con default non disponibile. Assegnazione solo server o admin autorizzato/auditato; rinnovi, refresh ed eventi duplicati non rigenerano un beneficio già assegnato o consumato. Per abbonamenti esistenti verificare i dati prima del riconoscimento.
- Consumare l’omaggio e associarlo al contratto nella stessa transazione, con controllo di concorrenza tra schede/membri. La riapertura dello stesso leaseId resta gratuita; un altro contratto torna a pagamento. I GET non consumano il beneficio. Se manca la configurazione necessaria ad aprire CF, non consumarlo.
- Checkout idempotente per lo stesso acquisto; ricontrollare il diritto prima di creare una sessione. Gestire conferme duplicate/fuori ordine o tardive rispetto all’uso di un omaggio senza doppio diritto/addebito. Se abilitati, gestire anche pagamenti asincroni.
- Rimborsi/contestazioni verificati aggiornano il relativo diritto secondo il billing esistente, senza cancellare automaticamente pratica CF o documenti. Non inventare una politica commerciale di rimborso: lasciare il caso alla gestione operativa.

Esporre comandi/letture interni, nel contesto del contratto, per prezzo e diritto, checkout e prosecuzione. I nomi sono una scelta Helix coerente con il repository. Il comando di prosecuzione autorizza sempre sul server: il frontend non decide gratuità e non deve aggirare il controllo aprendo un URL costruito da solo. Login e checkout preservano il contratto di partenza.

La firma del link non sostituisce il diritto al servizio: generarne una nuova richiede sempre autorizzazione, pagamento/omaggio o bypass admin verificati sul server. La v0.1 permette comunque di compilare il modulo anche con link assente, invalido o scaduto, creando una pratica senza leaseId. Concordare con CF la riconciliazione operativa prima di addebitare/lavorare pratiche non collegate; non usarne l'esistenza o il solo abbinamento come prova del pagamento.

## 4. Link firmato e ritorno dalle email

### Apertura del modulo

Comando interno autenticato e autorizzato sul contratto, preferibilmente POST, che controlla il diritto e genera al clic un link a **`https://app.concordatofacile.it/`**. Nessuna firma preparata nel browser o conservata nel checkout/email CasaZen.

| Parametro | Valore e regola v0.1 |
|---|---|
| `partner` | `casazen` |
| `leaseId` | UUID reale della bozza, in minuscolo |
| `expiresAt` | UTC, adesso + 15 minuti, formato esatto `yyyy-MM-ddTHH:mm:ssZ`, senza frazioni |
| `signature` | HMAC-SHA256 di `leaseId + "." + expiresAt`, UTF-8, chiave `linkSecret`, output esadecimale minuscolo |
| `comune` | Nome del comune della casa, dalla sorgente territoriale esistente |
| `provincia` | Sigla provinciale, per esempio `MB`, dalla stessa sorgente |

Firmare i **valori decodificati esatti**, poi codificare ogni valore per la query con una libreria URL. Non firmare il testo URL-encoded né normalizzare dopo la firma. Verificare il vettore pubblico della v1.3 §2, ripreso dalla v0.1; non usare la sua chiave dimostrativa in produzione. Comune/provincia non sono coperti dalla firma. CF tollera due minuti di scarto e rifiuta scadenze oltre 60 minuti: CasaZen emette sempre 15 minuti nella forma canonica. Configurazione e segreti distinti per prova/produzione, solo backend, mai nel bundle, nelle query interne, nei log o nella telemetria. Il link restituito contiene una firma temporanea: evitare log del relativo URL completo e risposte cacheabili.

Vettore pubblico di conformità v1.3 §2, confermato dalla v0.1 (è una fixture dimostrativa, **non una credenziale operativa**):

```text
linkSecret = test_link_secret_0123456789abcdef0123456789abcdef
message    = 0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11.2026-10-13T10:00:00Z
signature  = fa10ce472c1e3b33939e62e01a7f5f8cec10d45abc02726f691cae37bd639eea
```

La scadenza storica serve al test puro della firma, non all'apertura live. Nel link il relativo `expiresAt` è `2026-10-13T10%3A00%3A00Z`; il messaggio HMAC mantiene i due punti decodificati. Per i link reali usare invece scadenza corrente + 15 minuti e segreto dell'ambiente fornito da CF.

Non inviare `comuneIstatCode`, `returnUrl` o `theme`: CF li ignora. Aprire in navigazione normale, mai iframe; se nuova scheda usare `noopener noreferrer`. CF rimuove i parametri firmati dalla barra prima di script di terzi; non dare per questo il leaseId come autenticazione dell'utente.

Prima dell'invio, stesso dispositivo/browser: un nuovo link può riprendere la bozza. Dopo l'invio o su altro dispositivo può creare **un'altra pratica**, non riaprire quella inviata. Quando l'elenco contiene una pratica non chiusa mostrare stato e indicazione di proseguire per email con CF, al posto del pulsante modulo. Non generare nuovi link con una pratica attestata/registrata; una nuova richiesta dopo chiusura senza attestazione deve essere intenzionale e non consuma automaticamente un altro pagamento/omaggio. Con errore nella lettura non interpretare «nessuna pratica» e non riaprire automaticamente. Anche un elenco vuoto può dipendere dal ritardo di pubblicazione: dopo il clic non rilanciare il modulo in automatico né promettere unicità garantita da CF.

### Ritorno ai documenti: estendere la pagina esistente

La pagina implementata oggi accetta **`https://casazen.space/app/long-rent/documents/upload?leaseId=<uuid>`**. La v0.1 propone invece il parametro **`practice=P-…`**, con lookup della pratica; questo supporto **non è ancora implementato**. Estendere la stessa pagina e il ritorno dal login, senza creare un altro archivio.

Proposta da confermare a CF: mantenere il percorso e inviare `?practice=<practiceId>&leaseId=<uuid>` quando il leaseId è noto; ammettere `?practice=<practiceId>` quando manca. Se CF mantiene solo `practice`, la pagina deve risolverlo ugualmente. Continuare a supportare i link esistenti con solo `leaseId`. La presenza di entrambi non è un accordo già concluso e non è necessaria al funzionamento del lookup.

- Validare UUID, formato `P-` + 16 caratteri esadecimali e parametri duplicati. Per `practice`, il backend usa `GET /v1/practices/{practiceId}`; espone dati soltanto se il leaseId restituito appartiene all'organizzazione e l'utente è autorizzato. Non creare un proxy pubblico per enumerare pratiche del partner e non fidarsi di un leaseId aggiunto alla query.
- Il collegamento CF non prova che il contenuto caricato appartenga al contratto: anche dal link valido l'utente può aver compilato i dati di un altro immobile. Prima di abilitare l'upload far scegliere/confermare esplicitamente **casa e contratto autorizzati**, mostrando riferimento e dati disponibili; consentire la correzione della scelta. Applicare la conferma anche al ritorno legacy con solo leaseId. Non usare propertyId come destinazione degli allegati.
- LeaseId nullo, discordante, pratica non trovata o non autorizzata: nessuna associazione automatica, nessun dettaglio di altri tenant. Consentire la scelta di un proprio contratto per conservare i documenti, avvisando che il collegamento della pratica deve essere corretto **da CF manualmente**; non introdurre un'API di scrittura CF che la specifica non offre. La scelta dell'utente non modifica il leaseId della pratica.
- Upload e download riusano le API LeaseDocument esistenti sul leaseId selezionato. Non importare file da CF, non richiedere un altro acquisto e non fatturare in base al solo practiceId/leaseId. Non creare obbligatoriamente un legame CF al documento caricato su un contratto diverso da quello restituito.

CasaZen non riceve email di pratica o finali: le comunicazioni e il bottone sono nelle email **all'utente**. Il bottone è una pagina web, non l'endpoint di stato o un'API di importazione. Attivarlo solo dopo conferma del rilascio e collaudo del formato di ritorno effettivamente concordato.

## 5. Importazione dei dati dai documenti del contratto

Estendere l’archivio **LeaseDocument già implementato**, conservando lo stesso leaseId e la relazione alla casa. L’upload oggi archivia, ma non popola ancora i campi del contratto.

| Documento | Dati da acquisire quando presenti | Destinazione |
|---|---|---|
| Contratto definitivo CF | Parti/ruoli, immobile, tipo, durata/date, stipula, canone, deposito e regime fiscale esplicito | Dati del contratto + PDF consultabile |
| Attestazione | Numero/data, accordo territoriale/comune, attestatori, dati attestati del canone | Sezione Attestazione + PDF consultabile |
| Ricevuta di registrazione | Data, protocollo/codice, ufficio, esito ed eventuali dati fiscali espressi nel documento | Sezione Registrazione + PDF consultabile |

- Input già normalizzato: un PDF preesistente è conservato con gli stessi byte; più foto diventano un solo PDF ordinato e scaricabile, senza originali fotografici separati. Non introdurre qui una seconda copia delle foto. Non riscrivere PDF firmati o ricevute per aggiungere il testo OCR.
- Estrarre il testo del documento intero, usando OCR quando serve, anche se i dati sono distribuiti su più pagine. Verificare il riuso dei tool locali già introdotti per l’APE, adattando interfacce/limiti solo dove necessario per gli allegati del contratto. Non riutilizzare il validatore APE come validatore contrattuale e non introdurre un provider esterno per presunzione.
- Popolare automaticamente i dati affidabili dopo controlli strutturali e coerenza con casa, parti e contratto. Campi mancanti restano da acquisire; valori incerti o conflitti vanno in revisione con evidenza della pagina e del documento. Nessuna sovrascrittura silenziosa dei dati confermati.
- Se file sbagliato, OCR fallito o documento incompleto: mantenerlo consultabile e segnalare «Da verificare», senza inventare dati o eliminare l’upload. Questo è diverso dal controllo bloccante dell’APE nei documenti della casa: non estenderlo agli allegati contrattuali.
- Aggiungere soltanto i metadati di elaborazione necessari (stato, provenienza, impronta/versione e risultato utile), con audit e concorrenza secondo il dominio esistente. Arrivo dei tre tipi in ordine libero; retry dello stesso file non crea un secondo contratto o evento. Nuove versioni non eliminano implicitamente le precedenti.
- Distinguere «Caricato», «Elaborazione», «Dati acquisiti» e «Da verificare». Caricare il contratto non prova attestazione o registrazione; una ricevuta di trasmissione non prova esito positivo. Verificare il documento/esito o la semantica dello stato CF prima di segnare registrato. Non avviare un secondo invio fiscale.
- Conservare autorizzazioni lease.read/lease.create secondo operazione, isolamento OrgId e verifica documentId insieme a leaseId. Nessuna duplicazione in PropertyDocuments e nessun secondo acquisto per caricare o consultare i documenti.

L’APE resta un documento della casa. Il suo OCR verifica i dati fondamentali e le date sull’intero attestato; non è una prova di autenticità nel registro regionale e non sostituisce le verifiche del partner. Non deve bloccare l’assegnazione del leaseId alla bozza minima.

## 6. Stato CF v0.1: lettura e cache applicativa

Client HTTP tipizzato backend verso **`https://stato.concordatofacile.it`**, HTTPS, TLS 1.2 o superiore. Autenticazione `Authorization: Bearer <chiave>`, separata dal segreto HMAC, distinta tra prova e produzione. Più chiavi possono coesistere per la rotazione; CF le consegna tramite Bitwarden Send a una sola apertura e password su altro canale. Non acquisire o salvare credenziali nei documenti/repository.

| Chiamata CF | Risultato |
|---|---|
| `GET /v1/leases/{leaseId}/practices` | `{ leaseId, practices: [...] }`, dalla più recente, fino a 20. Contratto sconosciuto, non CasaZen o senza pratiche: sempre 200 e array vuoto |
| `GET /v1/practices/{practiceId}` | Oggetto della singola pratica con `leaseId`, anche null; pratica inesistente, di altro partner/ambiente: stesso 404 |

Oggetto pratica: `practiceId`, `status`, `statusChangedAt`, `submittedAt`, `comune`, `comuneDiffersFromLink`, `closeReason`; `leaseId` si aggiunge nel dettaglio. `practiceId` è il numero CF, `P-` + 16 caratteri esadecimali, **non** il vecchio `caseId`. JSON camelCase, date ISO 8601 UTC con Z, errori `application/problem+json` con `code` snake_case. Il comune non è un codice ISTAT; `comuneDiffersFromLink` è booleano nullable e non dimostra coerenza degli altri dati del contratto.

| Stato CF | Presentazione e comportamento |
|---|---|
| `Received` | Modulo inviato, pratica ricevuta |
| `InProgress` | In lavorazione |
| `AwaitingUser` | Dati/documenti richiesti: proseguire per email con CF |
| `DraftSent` | Bozza inviata da approvare e firmare tramite il partner |
| `SignedReceived` | Firmati ricevuti |
| `SentToAssociation` | Inviata all'associazione per attestazione |
| `Attested` | Attestazione rilasciata; finale **solo per il servizio base** |
| `Registered` | Contratto registrato; finale per il servizio completo |
| `ClosedWithoutAttestation` | Chiusa senza attestazione; mostrare motivo noto |
| `Suspended` | Sospesa, può riprendere; non finale |

Motivi di chiusura: `duplicate`, `withdrawn`, `not_compliant`, `other`. InProgress/AwaitingUser possono alternarsi, Suspended può riprendere; `statusChangedAt` non retrocede. Non modellare una progressione numerica irreversibile e non nascondere doppioni: elencare tutte le pratiche, distinguendo quelle chiuse per duplicazione. Valori/campi/codici sconosciuti non fanno fallire la lettura e non diventano «completata»: presentazione neutra, nessuna automazione finché la semantica non è nota.

Il servizio base/completo viene scelto **nel modulo CF** e non è presente nello schema v0.1. Chiedere a CF di aggiungerlo con valori documentati (nome proposto `serviceLevel`, da concordare), necessario a distinguere la finalità di Attested e riconciliare le tariffe 69/89. Finché manca, mostrare «Attestazione rilasciata» senza dichiarare concluso l'intero servizio, senza addebitare o inferire il pacchetto dal prezzo CasaZen. Il prezzo di vendita rimane 119 € IVA inclusa come richiesto; la scelta del pacchetto non crea un secondo checkout.

### Chiamate CasaZen e frequenza

- Esporre lettura interna autenticata nel contesto del contratto, ad esempio `GET /api/leases/{leaseId}/concordatofacile/status`; non è l'URL CF né un endpoint già sviluppato. Autorizzare il contratto prima della chiamata remota e prima di leggere qualsiasi risultato memorizzato. Il lookup practice del ritorno applica i controlli della sezione 4.
- Interrogare CF quando l'utente apre la **sezione di verifica del contratto**, se lo snapshot valido è scaduto: **non più di una lettura ogni 15 minuti per leaseId**, condivisa fra schede e utenti autorizzati dell'organizzazione. Anche «Aggiorna» e ritorni ripetuti rispettano il periodo; niente timer browser, job periodico o polling in background nel minimo intervento.
- Memorizzare l'ultimo risultato applicativo con `fetchedAt`, ambiente/OrgId/leaseId, scadenza e stato dell'ultimo tentativo; una semplice cache per istanza non basta a garantire il limite in più repliche. Usare storage applicativo e coordinamento concorrente già disponibili, senza creare un'infrastruttura dedicata. Accorpare le richieste concorrenti. Per il dettaglio di ritorno applicare lo stesso intervallo al practiceId; evitare di ricontattarlo se un elenco già valido ne contiene il risultato autorizzato.
- CF imposta `Cache-Control: no-store`: non abilitare cache HTTP/browser/proxy. Lo snapshot è stato applicativo privato e autorizzato, non risposta pubblicamente cacheabile; mantenere no-store anche nelle risposte CasaZen. Mostrare data dell'ultimo passaggio CF e data dell'ultima verifica come informazioni distinte, con eventuale indicazione del prossimo aggiornamento.
- Limite teorico delle sole letture elenco: **4 richieste/ora per contratto consultato continuativamente**, altrimenti zero; sommare i lookup per practiceId degli arrivi da email. Non promettere un volume aggregato senza numero di pratiche/utenti attivi. Comunicare questa formula a CF perché definisca il limite partner; nessun refresh forzato lo aggira.

### Errori e integrità

- 400: `invalid_lease_id`/`invalid_practice_id`; 401 `unauthorized` con WWW-Authenticate; 404 `practice_not_found` solo sul dettaglio; 429 `rate_limited` con `Retry-After` in secondi. Non trasformare errori, timeout, 5xx o chiave errata in array vuoto o stato finale.
- Timeout e tentativi limitati secondo pattern esistenti. Rispettare Retry-After, ritardare il successivo tentativo e accorpare richieste anche in caso di errore; niente retry automatici che superino il budget concordato. Conservare l'ultimo risultato valido come non aggiornato, senza presentarlo come verifica appena riuscita. La cache di un elenco vuoto scade anch'essa dopo 15 minuti: CF può ritardare la comparsa della pratica, con ritentativi propri ogni 15 minuti.
- Non applicare un risultato con `statusChangedAt` più vecchio per la stessa pratica; distinguere assenza dalla mancata associazione e dai problemi di configurazione. In caso di `comuneDiffersFromLink=true` avvertire l'utente di verificare il contratto, senza correggere silenziosamente la casa.
- Nessun webhook CF, evento finale con dati fiscali o download automatico dei file. Stato CF, estrazione documentale e dati attestazione/registrazione restano distinti: Registered è uno stato del partner, non una ricevuta già conservata in CasaZen. Nessun invio fiscale automatico aggiuntivo.

Prima del collaudo live servono chiave endpoint di prova, segreto link di prova e casi nei vari stati; produzione usa entrambi segreti diversi. Chiedere URL/commutazione dell'ambiente di prova e formato del campo servizio, non assumere che i prefissi delle chiavi da soli definiscano la selezione dell'ambiente. L'inquadramento privacy v1.3 R24 rimane da definire con CF prima delle pratiche reali; non dedurre ruoli privacy dall'assenza di nomi nell'endpoint.

## 7. Accettazione e consegna

| Prova | Risultato |
|---|---|
| Concordato scelto come primo passo | Bozza reale con soli casa/tipo; leaseId stabile anche dopo importazione; nessun APE/dato definitivo richiesto prima |
| Offerta e checkout | Cinque passaggi comprensibili, 119 € IVA inclusa, conferma solo server, ritorno allo stesso contratto |
| Admin piattaforma / admin organizzazione | Solo il primo salta il pagamento; entrambi rispettano l’accesso alla risorsa |
| Biennale, eventi duplicati e accessi concorrenti | Un omaggio assegnato una volta, consumabile da un solo contratto, riapertura senza addebito |
| CF | HMAC conforme al vettore, UTC al secondo, scadenza 15 minuti e URL-encoding; nessun segreto al client; non riaprire pratiche in lavorazione/attestate |
| Ritorno email | `practice`/`leaseId` preservati dal login; conferma del contratto; null, discordanza o altro tenant senza associazioni né esposizioni indebite |
| Importazione | Dati delle tre sezioni sullo stesso leaseId, PDF consultabili, gestione incompleti/conflitti/versioni senza perdita di dati |
| Più pagine | Riuso del PDF unificato esistente; estrazione sull’intero documento, senza conservare foto separate |
| Permessi | Nessuna scrittura su casa/contratto altrui, nessun uso del solo propertyId, nessuna chiave CF al client |
| Stato CF | Elenco multipratiche e dettaglio; tutti gli stati/errori v0.1; servizio ignoto senza falso finale Attested; snapshot condiviso 15 minuti, concorrenza e 429 rispettati |

Eseguire test pertinenti del diritto lato server, Stripe test, consumo atomico dell’omaggio, bozza minima, importazione con PDF digitale/scansione multipagina e risposte CF documentate, link scaduti, doppioni e ritorni con pratica non collegata; verificare il riuso senza regressioni di upload/download. Seguire build/lint/typecheck e controlli richiesti dai repository. Nessun invio di pratiche reali o addebito live come prova.

Consegna: PR con delta effettivo, verifiche eseguite e configurazione/dati CF ancora necessari. Non dichiarare attivo in produzione ciò che è soltanto implementato o in PR.
