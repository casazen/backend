# Requisiti per Helix — integrazione ConcordatoFacile

Revisione 10 · 23 ottobre 2026. Questo è l’unico documento che guida lo sviluppo e quello da fornire a Helix. Descrive il lavoro ancora necessario per il servizio CF. La [documentazione del flusso](../docs/integrations/concordatofacile/README.md) è descrittiva (percorso dell’utente, accordi con CF, risposte per CF): non aggiunge requisiti e, se differisce, vale questo testo. Sostituisce le precedenti proposte di integrazione e l’analisi di business che stava in questo file (storico in git), senza richiedere di sommarne le funzionalità. La revisione 10 aggiorna lo stato in produzione (release v2.1.0), il controllo APE (PR #569) e aggiunge il coordinamento con la catena Affitti lunghi (fine della sezione 1).

## 1. Base da riusare e lavoro restante

Le PR [frontend #315](https://github.com/casazen/frontend/pull/315) e [backend #561](https://github.com/casazen/backend/pull/561) sono integrate in `develop` e realizzano già il caricamento e la conservazione. Le successive PR [frontend #316](https://github.com/casazen/frontend/pull/316) e [backend #562](https://github.com/casazen/backend/pull/562), integrate in `develop` il 22 ottobre, correggono i controlli CI, il ritorno dal login e il riferimento temporale del controllo APE: verificare il codice corrente prima di intervenire. Integrazione in `develop` e CI verde non attestano disponibilità in produzione. Leggere AGENTS.md dei repository e lavorare sul codice corrente. La release v2.1.0 del 23 ottobre (backend #566, frontend #320) ha portato in produzione la pagina di caricamento con `leaseId`, l'archivio allegati del contratto, il PDF multipagina e il controllo APE delle PR #315/#561, con le correzioni #316/#562; in produzione non esiste alcun codice CF (`practice`, firma del link, client di stato).

| Già implementato nelle PR di codice: riusare | Resta a Helix |
|---|---|
| Pagina `/app/long-rent/documents/upload?leaseId=…` (accetta solo `leaseId`), login con ritorno allo stesso contratto | Tipo di contratto come prima scelta; bozza minima concordato e collegamento al servizio CF |
| Allegati `LeaseDocument`, API autorizzate elenco/upload/download, storage privato e isolamento OrgId | Pagina offerta, pagamento singolo 119 € IVA compresa, flag pratica omaggio biennale e bypass admin CasaZen |
| Sezione Documenti del contratto, raggiungibile anche aprendo il contratto dalla casa | Scheda del contratto con pulsante e stato della pratica; link CF firmato dal server al clic, dopo verifica del diritto; una sola pratica per contratto |
| Un PDF esistente oppure più foto JPEG/PNG riordinate/ruotate e unite in un solo PDF scaricabile; le foto non sono conservate separatamente | Estrazione dai documenti del contratto e popolamento delle rispettive sezioni dello stesso leaseId |
| OCR e controlli fondamentali APE, nel distinto archivio della casa | Client stato CF v0.2, snapshot applicativo di 15 minuti e ritorno con pratica/conferma del contratto |

Fonti: «ConcordatoFacile — specifica endpoint di stato v0.2», ottobre 2026 (sostituisce la v0.1), governa link e letture di stato; della precedente «01-technical-specification», v1.3 del 13 ottobre 2026, si riusano soltanto §2 per HMAC/vettore e le convenzioni esplicitamente richiamate dalla v0.2. Non implementare webhook, download automatico dei file, vecchi caseId/eventi, riapertura garantita o altri requisiti v1.3 superati dal nuovo flusso. Nella v0.2 (§1 e §5) il servizio per CasaZen è sempre la **pratica completa**, fino alla registrazione. Il profilo di lettura della sezione 6 è accettato da CF (v0.2 §4), e il 23 ottobre CF ha verificato con il codice del modulo i tre link di esempio del 22 ottobre. Le differenze aperte sono riportate sotto, senza inventare accordi.

Non ricostruire pagina upload, assemblaggio foto/PDF, OCR APE o un secondo archivio. La funzione documentale multipagina è generale, non un nuovo requisito CF. Il suo riuso nell’importazione contrattuale non implica che il controllo APE sappia già estrarre i dati di un contratto. Perimetro: solo web; l’app mobile non ha schermate dei contratti long-rent e resta fuori.

### Relazioni vincolanti

- Casa → documenti della casa: APE, planimetria e documenti propri dell’immobile.
- Casa → contratti; contratto → documenti del contratto: contratto definitivo, attestazione, registrazione e altri allegati.
- Il caricamento di allegati del contratto richiede `leaseId`; `propertyId` non lo sostituisce. La casa si ricava dal contratto autorizzato. Dalla casa si apre il contratto e poi la sua sezione Documenti.
- `leaseId` è l’UUID reale e stabile di `LeaseContract`, anche quando è una bozza. Non usare l’ID della casa, del pagamento o della pratica CF. L’importazione non crea un secondo contratto.

Punti di riuso: frontend `src/config/route-manifest.ts`, `src/api/leases.api.ts`, `src/queries/use-leases.ts`, `LeaseDocumentsPanel`, `src/lib/document-pdf.ts`; backend `LeaseDraftService`, `LeaseDocumentService`, `IApeDocumentInspector`, storage e autorizzazioni esistenti. Le API allegati sono `GET/POST /api/leases/{leaseId}/documents` e `GET /api/leases/{leaseId}/documents/{documentId}/download`.

### Coordinamento con la catena Affitti lunghi (revisione 10)

Il piano di redesign dell'area Affitti lunghi (task con sigla LR-, documentazione fuori dal repository) costruisce in parallelo parti dello stesso ciclo di vita del contratto. Per non duplicare lavoro e non dividere i dati valgono queste regole.

- **Base già in produzione (LR-12, release v2.1.0).** `POST /api/leases/drafts { propertyId, origin? }` con `origin = Created | Imported` (nessun APE, parte o data richiesti), bozza salvata a passi (`LeaseDraftService`) e pulizia delle bozze ferme (`LeaseDraftRetentionService`, `LeaseDraftDeleter`, job `LeaseDraftRetentionJob`, opzione `LongRentLease__DraftRetentionDays`, oggi 180 giorni). La bozza minima della sezione 2 riusa questa API. Il contratto reale è preparato da CF e poi importato: valutare `Origin = Imported` con tipo concordato (nessun controllo di fascia, APE mancante come avviso, nessuna revisione né «scaricato per la firma», come previsto dal task LR-13); il riferimento al partner e alla pratica sta nel diritto e nella pratica CF, non in `LeaseContract.Origin`.
- **Dalla bozza al contratto firmato: lacuna colmata dall'importazione LR-13.** Questo documento carica i file e ne estrae i dati, ma non dice come la bozza diventa «Firmato» o «Registrato». Quel passaggio è l'importazione di un contratto già firmato prevista dal task LR-13 (non ancora costruito): `POST /api/leases/{id}/import` con PDF firmato e data di stipula, senza modello approvato; la registrazione si dichiara dopo con la dichiarazione manuale esistente (ricevuta obbligatoria). Regole comuni: (1) un solo archivio visibile all'utente: il PDF firmato importato è registrato come `LeaseDocument` di tipo `Contract`, così la sezione Documenti lo mostra; (2) l'estrazione della sezione 5 compila i valori della bozza con la stessa validazione dei passi della procedura e l'utente conferma, senza sovrascritture silenziose; (3) l'importazione non crea un secondo contratto e non cambia il `leaseId`.
- **Archivio unico dei documenti (task LR-11a): superato.** Esistono già `LeaseDocument`, le API e la sezione Documenti (#315/#561): non si costruisce un secondo archivio dei documenti dei contratti. Resta solo ciò che questo documento aggiunge (estrazione e stato di elaborazione).
- **Procedura «Crea contratto» (LR-06a/b).** Il tipo di contratto è il primo passo della stessa procedura (sezione 2). Con concordato la procedura non prosegue con i passi dei dati: crea la bozza minima e porta alla scheda del contratto; il percorso libero conserva i propri passi. Il passo «contratto già firmato» della procedura usa l'importazione LR-13.
- **Stato e «prossimo passo» (LR-01b).** Per le bozze concordato CF la scheda ha un solo punto d'ingresso (sezione 4): il «prossimo passo» generale non duplica né ricalcola gli stati CF, che restano un'informazione separata della scheda.
- **Fine contratto (LR-14).** Un'eventuale «duplica come bozza» (non prevista nella versione ridotta del task LR-14) crea un nuovo contratto con un nuovo `leaseId`: una nuova pratica CF richiede un nuovo pagamento o l'omaggio (sezione 3) e la pratica CF non si copia.
- **Migrazioni.** La catena delle migrazioni di `LeaseContract` e delle tabelle collegate è una sola: una migrazione per volta (LR-14 aggiunge il fine periodo con backfill, LR-04a una colonna del piano dei canoni, il delta CF diritto/omaggio, audit di emissione, snapshot e metadati di elaborazione). Chi si fonde dopo rigenera la propria con `dotnet ef`.

## 2. Ingresso e bozza minima concordato

Il flusso di prodotto da realizzare parte dalla scelta del tipo di contratto, prima di parti, termini economici o calcolatore. Per il canone concordato il vero contratto è preparato da CF e poi importato in CasaZen.

1. Con casa selezionata e tipo concordato, salvare subito una bozza minima `LeaseContract` con PropertyId, OrgId, tipo/provenienza CF e restituire leaseId. Verificare e riusare `POST /api/leases/drafts` / `LeaseDraftService`; non generare soltanto un UUID nel browser.
2. Non richiedere APE, parti, canone, deposito, date, coefficienti o attestazione per ottenere il riferimento della bozza. Non allentare per questo i controlli dei diversi percorsi di firma/registrazione già esistenti.
3. Mostrare casa, leaseId copiabile e «Bozza — in attesa del contratto del partner». I dati mancanti restano da acquisire, senza importi zero, date o persone fittizie presentate come definitive. La bozza non genera occupazione, incassi o adempimenti di un contratto attivo.
4. Proteggere il leaseId già usato per un acquisto o una pratica CF dalla cancellazione automatica delle bozze abbandonate: verificare `LeaseDraftRetentionService` e `LeaseDraftDeleter`, senza disabilitare la retention generale. Un contratto sospeso dal partner non deve perdere documenti o riferimento per inattività.
5. Doppi click e retry della stessa azione riusano la bozza salvata; rimane possibile creare intenzionalmente un nuovo contratto sulla stessa casa.
6. La scheda del contratto (la pagina di dettaglio `/app/long-rent/leases/:id`) ha un solo punto d’ingresso al servizio, il cui testo cambia con lo stato (sezione 4): «Procedi con la creazione del contratto» porta all’offerta/pagamento o all’omaggio e poi al modulo del partner. L’admin CasaZen segue l’accesso diretto descritto sotto. Il percorso libero mantiene il proprio funzionamento: non inviarlo al modulo riservato ai concordati.

## 3. Offerta, pagamento e pratica omaggio

### Pagina servizio

Percorso proposto: `/app/long-rent/leases/:id/concordatofacile`, raggiunto da «Procedi con la creazione del contratto». Mostrare casa e contratto, prezzo **119,00 € totali IVA compresa per pratica** (una pratica per contratto), eventuale omaggio e questi passaggi prima del pagamento:

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
| Pratica già pagata o omaggio già associato a questo leaseId | Nessun nuovo addebito/consumo; nella scheda del contratto «Accedi alla tua pratica» finché non risulta una pratica inviata, poi lo stato della pratica (sezione 4) |
| Admin di piattaforma CasaZen | Accesso diretto dal contratto, senza pagina di pagamento e senza consumare omaggi |
| Pagamento in attesa, fallito o annullato | Stato comprensibile, nessun accesso CF sbloccato |

### Controlli e persistenza

- Usare il billing CasaZen esistente (`BillingController`, `BillingCheckoutService`, `StripeBillingService`, `ProcessedStripeEvent`, `StripeWebhookJob` e frontend billing). Aggiungere un acquisto **una tantum**, senza cambiare l’abbonamento e senza utilizzare pagamenti degli affitti o Stripe Connect.
- Il server determina EUR **11900 centesimi, IVA già inclusa**. Price/configurazione fiscale devono mantenere il totale; non aggiungere IVA sopra i 119 €. L’esenzione dichiarata dall’associazione non si applica automaticamente alla vendita CasaZen. Riusare la documentazione fiscale CasaZen pertinente.
- Il diritto è legato a OrgId e LeaseId e ne conserva origine, stato, identificativi del pagamento e date necessarie. Sbloccare da pagamento confermato server, con eventi Stripe verificati e corrispondenza di acquisto, importo, valuta e contratto. Il ritorno `checkout=success` non è una prova.
- Solo l’admin di **piattaforma** (`CasazenPolicies.AdminOnly`, ruolo JWT `Admin`) salta il pagamento. L’admin dell’organizzazione o `OrgBillingAdmin` non ha questo bypass. Mantenere autorizzazione al contratto e audit; non simulare un pagamento admin.
- L’abbonamento effettivamente sottoscritto per **due anni** assegna una sola pratica omaggio all’organizzazione abbonata, condivisa tra i membri autorizzati. Non equivale a 24 pagamenti mensili o due anni di anzianità. Verificare il mapping del piano/Price; se manca, evidenziare la configurazione necessaria senza creare prodotti Stripe live come prova.
- Flag DB persistente, ad esempio `HasConcordatoGift`, con riferimento al leaseId beneficiario e date/identificativo abbonamento, o equivalente. Migrazione con default non disponibile. Assegnazione solo server o admin autorizzato/auditato; rinnovi, refresh ed eventi duplicati non rigenerano un beneficio già assegnato o consumato. Per abbonamenti esistenti verificare i dati prima del riconoscimento.
- Consumare l’omaggio e associarlo al contratto nella stessa transazione, con controllo di concorrenza tra schede/membri. Rientrare nel modulo prima dell’invio resta gratuito; una nuova pratica richiede un nuovo contratto, che torna a pagamento (l’omaggio è uno solo). I GET non consumano il beneficio. Se manca la configurazione necessaria ad aprire CF, non consumarlo.
- Checkout idempotente per lo stesso acquisto; ricontrollare il diritto prima di creare una sessione. Gestire conferme duplicate/fuori ordine o tardive rispetto all’uso di un omaggio senza doppio diritto/addebito. Se abilitati, gestire anche pagamenti asincroni.
- Rimborsi/contestazioni verificati aggiornano il relativo diritto secondo il billing esistente, senza cancellare automaticamente pratica CF o documenti. Non inventare una politica commerciale di rimborso: lasciare il caso alla gestione operativa.

Esporre comandi/letture interni, nel contesto del contratto, per prezzo e diritto, checkout e prosecuzione. I nomi sono una scelta Helix coerente con il repository. Il comando di prosecuzione autorizza sempre sul server: il frontend non decide gratuità e non deve aggirare il controllo aprendo un URL costruito da solo. Login e checkout preservano il contratto di partenza.

La firma del link non sostituisce il diritto al servizio: generarne una nuova richiede sempre autorizzazione, pagamento/omaggio o bypass admin verificati sul server. La v0.2 permette comunque di compilare il modulo anche con link assente, invalido o scaduto, creando una pratica senza leaseId. Concordare con CF la riconciliazione operativa prima di addebitare/lavorare pratiche non collegate; non usarne l'esistenza o il solo abbinamento come prova del pagamento.

## 4. Link firmato e ritorno dalle email

### Apertura del modulo

Comando interno autenticato e autorizzato sul contratto, preferibilmente POST, che controlla il diritto e genera al clic un link a **`https://app.concordatofacile.it/`**. Nessuna firma preparata nel browser o conservata nel checkout/email CasaZen. Il comando registra soltanto l’emissione (contratto, utente, ora; mai l’URL né la firma): serve all’audit e alla rilettura breve della sezione 6.

| Parametro | Valore e regola v0.2 |
|---|---|
| `partner` | `casazen` |
| `leaseId` | UUID reale della bozza, in minuscolo |
| `expiresAt` | UTC, adesso + 15 minuti, formato esatto `yyyy-MM-ddTHH:mm:ssZ`, senza frazioni |
| `signature` | HMAC-SHA256 di `leaseId + "." + expiresAt`, UTF-8, chiave `linkSecret`, output esadecimale minuscolo |
| `comune` | Nome del comune della casa, dalla sorgente territoriale esistente |
| `provincia` | Sigla provinciale, per esempio `MB`, dalla stessa sorgente |

Firmare i **valori decodificati esatti**, poi codificare ogni valore per la query con una libreria URL. Non firmare il testo URL-encoded né normalizzare dopo la firma. Verificare il vettore pubblico della v1.3 §2, ripreso dalla v0.2; non usare la sua chiave dimostrativa in produzione. Comune/provincia non sono coperti dalla firma. Il comune precompila la ricerca del modulo: se non è nell'elenco di CF (oggi Seveso, comune pilota, non c'è), il modulo lo lascia scritto e l'utente prosegue con quel nome; la pratica resta legata al contratto e `comune` arriva nello stato ridotto a lettere, spazi, apostrofi, trattini e parentesi. Non è un errore da gestire in CasaZen: non cambiare il nome inviato e aprire il punto con CF (sezione 6). CF tollera due minuti di scarto e rifiuta scadenze oltre 60 minuti: CasaZen emette sempre 15 minuti nella forma canonica. Configurazione e segreti distinti per prova/produzione, solo backend, mai nel bundle, nelle query interne, nei log o nella telemetria. Il link restituito contiene una firma temporanea: evitare log del relativo URL completo e risposte cacheabili.

Vettore pubblico di conformità v1.3 §2, confermato dalla v0.2 (è una fixture dimostrativa, **non una credenziale operativa**):

```text
linkSecret = test_link_secret_0123456789abcdef0123456789abcdef
message    = 0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11.2026-10-13T10:00:00Z
signature  = fa10ce472c1e3b33939e62e01a7f5f8cec10d45abc02726f691cae37bd639eea
```

La scadenza storica serve al test puro della firma, non all'apertura live. Nel link il relativo `expiresAt` è `2026-10-13T10%3A00%3A00Z`; il messaggio HMAC mantiene i due punti decodificati. Per i link reali usare invece scadenza corrente + 15 minuti e segreto dell'ambiente fornito da CF.

Altri due vettori, generati da uno script di riferimento il 22 ottobre con lo stesso segreto pubblico e verificati da CF con il codice del modulo il 23 ottobre (secondi diversi da zero; il secondo ha un comune con spazi e apostrofo):

```text
leaseId   = ea326713-1005-4c17-9f6f-37f3b6af395a
expiresAt = 2026-10-22T16:40:19Z   (nel link: 2026-10-22T16%3A40%3A19Z)
signature = c387fc1f3da26cd41003d5d3acb4a4d3533ae026d700bba318349a19865db367
comune    = Seveso, provincia MB

leaseId   = 62affd98-c899-449d-b08e-b64972d79bb0
expiresAt = 2026-10-22T16:40:19Z
signature = 04dc8390857669d00e87d6ee70b2f64437fa4ed38889720d4517d59debcf05e1
comune    = Reggio nell'Emilia, provincia RE   (nel link: Reggio%20nell%27Emilia)
```

Non inviare `comuneIstatCode`, `returnUrl` o `theme`: CF li ignora. Aprire in navigazione normale, mai iframe; se nuova scheda usare `noopener noreferrer`. CF rimuove i parametri firmati dalla barra prima di script di terzi; non dare per questo il leaseId come autenticazione dell'utente.

### Stati della scheda del contratto: una pratica per contratto

Nella scheda del contratto concordato il punto d’ingresso al servizio cambia con lo stato; i testi dei pulsanti sono quelli indicati.

| Stato | Che cosa vede l’utente |
|---|---|
| Diritto non acquisito | «Procedi con la creazione del contratto» → pagina servizio (sezione 3) |
| Pagamento in attesa, fallito o annullato | Stato comprensibile, nessun accesso al modulo |
| Diritto acquisito (pagamento, omaggio o admin) e nessuna pratica nell’elenco CF | «Accedi alla tua pratica»: genera il link firmato e apre il modulo. Il testo avverte che la bozza riprende solo sullo stesso dispositivo e browser; da un altro il modulo riparte da zero |
| Almeno una pratica nell’elenco CF, in qualunque stato (anche chiusa) | Nessun pulsante: sezione «Stato della pratica» (sezione 6) con tutte le pratiche, distinguendo quelle chiuse per duplicazione. Dopo l’invio il modulo non si riapre, nemmeno con `ClosedWithoutAttestation`: una nuova pratica richiede un nuovo contratto, con nuovo pagamento o omaggio |

Prima dell’invio l’utente può rientrare nel modulo quante volte serve, senza nuovo addebito. Dopo l’invio o da un altro dispositivo un nuovo link aprirebbe una compilazione nuova e quindi un’altra pratica: per questo il pulsante sparisce appena la lettura di stato mostra una pratica. Il ritardo tra invio, pubblicazione di CF e lettura è coperto dalla rilettura breve della sezione 6. Un doppione resta possibile e lo chiude CF come `duplicate`: elencarlo, non nasconderlo, e non promettere unicità garantita da CF.

Con errore nella lettura non interpretare «nessuna pratica»: se esiste uno snapshot precedente usarlo, segnalato come non aggiornato; se non c’è nessuno snapshot, mostrare «Accedi alla tua pratica» con l’avviso «Non riusciamo a verificare lo stato: se hai già inviato il modulo non riaprirlo». Dopo il clic non rilanciare il modulo in automatico.

### Ritorno ai documenti: estendere la pagina esistente

La pagina implementata oggi (in `develop` e in produzione dalla release v2.1.0 del 23 ottobre) accetta **`https://casazen.space/app/long-rent/documents/upload?leaseId=<uuid>`**. Il bottone «Carica i dati in CasaZen» di CF porterà invece **`?practice=P-…`**, come nella v0.2, con lookup della pratica; questo supporto **non è ancora implementato**. Estendere la stessa pagina e il ritorno dal login, senza creare un altro archivio.

Parametro comunicato a CF: mantenere il percorso e usare soltanto `?practice=<practiceId>`. Il `leaseId` non serve nel link: arriva dal lookup della pratica o dalla scelta dell'utente, ed è comunque indispensabile per salvare gli allegati. Continuare a supportare i link esistenti con solo `?leaseId=<uuid>`, con la stessa conferma del contratto; se nella query compaiono entrambi i parametri vale `practice` e il `leaseId` della query è ignorato.

- Validare UUID, formato `P-` + 16 caratteri esadecimali e parametri duplicati. Per `practice`, il backend usa `GET /v1/practices/{practiceId}`; espone dati soltanto se il leaseId restituito appartiene all'organizzazione e l'utente è autorizzato. Non creare un proxy pubblico per enumerare pratiche del partner. Con il link legacy il `leaseId` della query non prova nulla: contano l'autorizzazione sul contratto e la conferma dell'utente.
- Il collegamento CF non prova che il contenuto caricato appartenga al contratto: anche dal link valido l'utente può aver compilato i dati di un altro immobile. Prima di abilitare l'upload far scegliere/confermare esplicitamente **casa e contratto autorizzati**, mostrando riferimento e dati disponibili; consentire la correzione della scelta. Applicare la conferma anche al ritorno legacy con solo leaseId. Non usare propertyId come destinazione degli allegati.
- LeaseId nullo, pratica non trovata o non autorizzata, oppure lookup non disponibile (errore, timeout, 429): nessuna associazione automatica, nessun dettaglio di altri tenant. Consentire la scelta di un proprio contratto per conservare i documenti, avvisando, se la pratica risulta non collegata, che il collegamento deve essere corretto **da CF manualmente**; non introdurre un'API di scrittura CF che la specifica non offre. La scelta dell'utente non modifica il leaseId della pratica.
- Upload e download riusano le API LeaseDocument esistenti sul leaseId selezionato. Non importare file da CF, non richiedere un altro acquisto e non fatturare in base al solo practiceId/leaseId. Non creare obbligatoriamente un legame CF al documento caricato su un contratto diverso da quello restituito.

CasaZen non riceve email di pratica o finali: le comunicazioni e il bottone sono nelle email **all'utente**. Il bottone è una pagina web, non l'endpoint di stato o un'API di importazione. CF lo ha già impostato su `https://casazen.space/app/long-rent/documents/upload?practice=P-…` (v0.2 §7) e lo tiene spento finché CasaZen non conferma, dopo il rilascio in produzione e il collaudo del ritorno con `practice`.

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

L’APE resta un documento della casa. Il suo OCR verifica i dati fondamentali e le date sull’intero attestato; non è una prova di autenticità nel registro regionale e non sostituisce le verifiche del partner. Non deve bloccare l’assegnazione del leaseId alla bozza minima. La PR backend #569 (aperta il 23 ottobre, non ancora integrata) rende non bloccante il caricamento dell'APE di una casa: il file è sempre conservato con un esito di controllo (`Verified`, `Incomplete`, `NotRecognized`, `NotChecked`) e, per creare, firmare o registrare un contratto, bastano codice identificativo e classe energetica scritti dal locatore oppure un APE verificato. Resta invariato che il controllo APE non si applica agli allegati del contratto.

## 6. Stato CF v0.2: lettura e cache applicativa

Client HTTP tipizzato backend verso **`https://stato.concordatofacile.it`**, HTTPS, TLS 1.2 o superiore. Autenticazione `Authorization: Bearer <chiave>`, separata dal segreto HMAC, distinta tra prova e produzione. Più chiavi possono coesistere per la rotazione; CF le consegna al referente CasaZen per email con un Bitwarden Send a una sola apertura, quando l'endpoint di prova è pronto, e la password su un altro canale concordato a parte (v0.2 §3). Non acquisire o salvare credenziali nei documenti/repository.

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
| `Attested` | Attestazione rilasciata, registrazione in corso; non finale |
| `Registered` | Contratto registrato; unico stato finale positivo |
| `ClosedWithoutAttestation` | Chiusa senza attestazione (finale); mostrare motivo noto |
| `Suspended` | Sospesa, può riprendere; non finale |

Motivi di chiusura: `duplicate`, `withdrawn`, `not_compliant`, `other`. InProgress/AwaitingUser possono alternarsi, Suspended può riprendere; `statusChangedAt` non retrocede. Non modellare una progressione numerica irreversibile e non nascondere doppioni: elencare tutte le pratiche, distinguendo quelle chiuse per duplicazione. Valori/campi/codici sconosciuti non fanno fallire la lettura e non diventano «completata»: presentazione neutra, nessuna automazione finché la semantica non è nota.

Per CasaZen il servizio è sempre la **pratica completa**, fino alla registrazione (v0.2 §1 e §5): nessuna scelta base/completo nel modulo e nessun campo servizio nell'endpoint. `Attested` è quindi un passaggio intermedio («Attestazione rilasciata, registrazione in corso»), `Registered` è l'unico finale positivo e `ClosedWithoutAttestation` il finale negativo; non dichiarare concluso il servizio prima di `Registered`. Se CF attivasse in futuro altri servizi per CasaZen, la v0.2 prevede che avvisi prima e aggiunga un campo nell'endpoint: finché non è documentato, un campo o un valore sconosciuto non diventa mai «completata». Il prezzo di vendita rimane 119 € IVA inclusa; non c'è un secondo checkout.

### Chiamate CasaZen e frequenza

- Esporre lettura interna autenticata nel contesto del contratto, ad esempio `GET /api/leases/{leaseId}/concordatofacile/status`, per la sezione «Stato della pratica» e per il pulsante «Accedi alla tua pratica» della scheda del contratto; non è l'URL CF né un endpoint già sviluppato. Autorizzare il contratto prima della chiamata remota e prima di leggere qualsiasi risultato memorizzato. Il lookup practice del ritorno applica i controlli della sezione 4.
- Interrogare CF quando l'utente apre la **scheda del contratto** (che mostra «Accedi alla tua pratica» o lo stato della pratica), se lo snapshot valido è scaduto: **non più di una lettura ogni 15 minuti per leaseId**, condivisa fra schede e utenti autorizzati dell'organizzazione. Anche «Aggiorna» e ritorni ripetuti rispettano il periodo. Niente timer browser, job periodico o polling in background: è il profilo che CF ha accettato nella v0.2 (§4), che non prevede letture in background.
- **Rilettura breve dopo l'apertura del modulo.** Dopo l'emissione di un link (sezione 4), finché l'elenco non mostra una pratica, la scheda può rileggere lo stato al massimo **ogni 5 minuti per contratto, per 60 minuti dall'ultima emissione**, e solo quando l'utente apre la pagina o la rimette in primo piano; poi, o appena compare una pratica, torna il periodo di 15 minuti. Serve a far sparire il pulsante pochi minuti dopo l'invio. CF l'ha accettata (v0.2 §4) e fissa il limite per chiave con margine sopra questo profilo, rivedendolo sulle pratiche effettive.
- Memorizzare l'ultimo risultato applicativo con `fetchedAt`, ambiente/OrgId/leaseId, scadenza e stato dell'ultimo tentativo; una semplice cache per istanza non basta a garantire il limite in più repliche. Usare storage applicativo e coordinamento concorrente già disponibili, senza creare un'infrastruttura dedicata. Accorpare le richieste concorrenti. Per il dettaglio di ritorno applicare lo stesso intervallo al practiceId; evitare di ricontattarlo se un elenco già valido ne contiene il risultato autorizzato.
- CF imposta `Cache-Control: no-store`: non abilitare cache HTTP/browser/proxy. Lo snapshot è stato applicativo privato e autorizzato, non risposta pubblicamente cacheabile; mantenere no-store anche nelle risposte CasaZen. Mostrare data dell'ultimo passaggio CF e data dell'ultima verifica come informazioni distinte, con eventuale indicazione del prossimo aggiornamento.
- Limite teorico delle sole letture elenco: **4 richieste/ora per contratto consultato continuativamente**, altrimenti zero; solo nell'ora successiva a ogni apertura del modulo, fino a **12 richieste/ora** per quel contratto (rilettura breve); sommare i lookup per practiceId degli arrivi da email. Non promettere un volume aggregato senza numero di pratiche/utenti attivi: come ordine di grandezza, 50 contratti consultati senza sosta sono al massimo 200 richieste/ora, circa 3 al minuto. La formula è già comunicata a CF, che definisce il limite partner; nessun refresh forzato la aggira.

### Errori e integrità

- 400: `invalid_lease_id`/`invalid_practice_id`; 401 `unauthorized` con WWW-Authenticate; 404 `practice_not_found` solo sul dettaglio; 429 `rate_limited` con `Retry-After` in secondi. Non trasformare errori, timeout, 5xx o chiave errata in array vuoto o stato finale.
- Timeout e tentativi limitati secondo pattern esistenti. Rispettare Retry-After, ritardare il successivo tentativo e accorpare richieste anche in caso di errore; niente retry automatici che superino il budget concordato. Conservare l'ultimo risultato valido come non aggiornato, senza presentarlo come verifica appena riuscita. La cache di un elenco vuoto scade anch'essa dopo 15 minuti: CF può ritardare la comparsa della pratica, con ritentativi propri ogni 15 minuti.
- Non applicare un risultato con `statusChangedAt` più vecchio per la stessa pratica; distinguere assenza dalla mancata associazione e dai problemi di configurazione. In caso di `comuneDiffersFromLink=true` avvertire l'utente di verificare il contratto, senza correggere silenziosamente la casa.
- Nessun webhook CF, evento finale con dati fiscali o download automatico dei file. Stato CF, estrazione documentale e dati attestazione/registrazione restano distinti: Registered è uno stato del partner, non una ricevuta già conservata in CasaZen. Nessun invio fiscale automatico aggiuntivo.

Prima del collaudo live servono la chiave dell'endpoint di prova e i casi nei vari stati; per la firma in prova resta il segreto del vettore pubblico della sezione 4 (v0.2 §3); produzione usa chiave e segreto di firma diversi, consegnati allo stesso modo. Chiedere URL/commutazione dell'ambiente di prova, non assumere che i prefissi delle chiavi da soli definiscano la selezione dell'ambiente. Il comune pilota Seveso non è nell'elenco comuni del modulo CF (il modulo accetta il nome scritto e la pratica resta legata al contratto): chiedere a CF se le pratiche di Seveso e degli altri comuni pilota sono lavorate normalmente e se possono entrare nell'elenco. L'inquadramento privacy v1.3 R24 rimane da definire con CF prima delle pratiche reali; non dedurre ruoli privacy dall'assenza di nomi nell'endpoint.

## 7. Accettazione e consegna

| Prova | Risultato |
|---|---|
| Concordato scelto come primo passo | Bozza reale con soli casa/tipo; leaseId stabile anche dopo importazione; nessun APE/dato definitivo richiesto prima |
| Offerta e checkout | Cinque passaggi comprensibili, 119 € IVA inclusa, conferma solo server, ritorno allo stesso contratto |
| Admin piattaforma / admin organizzazione | Solo il primo salta il pagamento; entrambi rispettano l’accesso alla risorsa |
| Biennale, eventi duplicati e accessi concorrenti | Un omaggio assegnato una volta, consumabile da un solo contratto; rientro nel modulo prima dell’invio senza addebito |
| CF | HMAC conforme al vettore, UTC al secondo, scadenza 15 minuti e URL-encoding; nessun segreto al client; emissione del link registrata senza URL né firma |
| Scheda del contratto | «Procedi con la creazione del contratto», «Accedi alla tua pratica» e «Stato della pratica» secondo lo stato; nessun pulsante appena l'elenco CF mostra una pratica, anche chiusa; nuova pratica solo con un nuovo contratto; avviso se lo stato non è leggibile |
| Ritorno email | `practice` (e il legacy `leaseId`) preservati dal login; conferma del contratto; leaseId null, pratica non trovata, altro tenant o lookup non disponibile senza associazioni né esposizioni indebite |
| Importazione | Dati delle tre sezioni sullo stesso leaseId, PDF consultabili, gestione incompleti/conflitti/versioni senza perdita di dati |
| Più pagine | Riuso del PDF unificato esistente; estrazione sull’intero documento, senza conservare foto separate |
| Permessi | Nessuna scrittura su casa/contratto altrui, nessun uso del solo propertyId, nessuna chiave CF al client |
| Stato CF | Elenco multipratiche e dettaglio; tutti gli stati/errori v0.2; Attested intermedio e Registered unico finale positivo; snapshot condiviso 15 minuti con rilettura breve dopo l’apertura, concorrenza e 429 rispettati |

Verifiche, secondo il regime del processo Helix: in locale solo build, lint e typecheck; per ogni regola di denaro, dati, permessi o privacy (diritto lato server e consumo atomico dell’omaggio, bozza minima e retention, firma e scadenza del link, appartenenza di `practice`/`leaseId` all’organizzazione, importazione dei documenti) una prova minima scritta, che gira in CI insieme alle suite dei repository. Le suite esistenti di upload/download restano il controllo contro le regressioni. Nessun invio di pratiche reali o addebito live come prova.

Consegna: PR con delta effettivo, verifiche eseguite e configurazione/dati CF ancora necessari. Non dichiarare attivo in produzione ciò che è soltanto implementato o in PR. Restano da CasaZen verso CF (v0.2 §7): qualche link prodotto dal comando del server, da far verificare come i tre di esempio; la conferma del collaudo della pagina di caricamento con `practice` (fino ad allora il bottone di CF resta spento); il canale per la password delle chiavi.
