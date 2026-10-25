# Requisiti per Helix — integrazione ConcordatoFacile

Revisione 11 · 25 ottobre 2026. Questo è l'unico documento dei **requisiti e dell'accettazione** dell'integrazione ConcordatoFacile (CF), quello da fornire a Helix se si rifà un giro. Il **processo** (come funziona oggi, configurazione, prove, stato per ambiente, debiti) è nel [documento di processo](../docs/integrations/concordatofacile/README.md), l'unico riferimento del processo CF: i due documenti non si contraddicono. Per i fatti (che cosa fa il sistema, che cosa è configurato) vale il documento di processo; per ciò che deve valere e per ciò che si accetta vale questo testo. Sostituisce la revisione 10 del 23 ottobre e le precedenti proposte di integrazione, senza richiedere di sommarne le funzionalità.

**Che cosa cambia nella revisione 11.**

1. Quasi tutto il lavoro che la revisione 10 lasciava a Helix è in `develop`: bozza minima, offerta e pagamento, pratica omaggio e accesso dell'admin, link firmato, client e cache dello stato, ritorno con `practice`, lettura dei documenti del contratto. La tabella del §1 dice che cosa è fatto, che cosa è parziale e che cosa resta; ogni requisito dei §2–§6 porta il suo stato.
2. Nuovo **interruttore** `Features:ConcordatoFacile` (voluto dall'utente il 25 ottobre): spento di default, acceso sul test, non impostato in produzione fino al collaudo con CF e all'accordo sull'inquadramento privacy (R24). Spento, ogni endpoint CF risponde 404 e l'app non mostra il servizio.
3. Nuovo flusso «Nuovo contratto»: prima il tipo, poi il come; per il concordato «Ho già il contratto e i documenti» o «Parto da zero» (§2).
4. Pagamento rafforzato: i metadati `orgId`/`leaseId`/`userId` stanno anche sul PaymentIntent, nessun secondo pagamento per lo stesso contratto, 404 per i contratti di altre organizzazioni (§3).
5. La lettura dei documenti è **locale, senza LLM né servizio esterno**, e sostituisce la pipeline di ADR-009 (§5).
6. Le fonti: la v0.2 di CF prevale sulla v1.3 per link e API di stato (§1).
7. I requisiti **resi obsoleti** sono al §8; i difetti noti e il lavoro restante al §9. Quattro difetti del backend trovati leggendo il codice contro la v0.2 (servizio della cache dello stato non registrato nel contenitore, forma della risposta dell'elenco di CF, controllo dell'organizzazione su `status` e `consume-gift`, lookup non in cache e attesa dopo un errore) sono stati corretti dal task CF-FIX2 (backend #611).

## 1. Base da riusare, stato e lavoro restante

Le PR [frontend #315](https://github.com/casazen/frontend/pull/315) e [backend #561](https://github.com/casazen/backend/pull/561) (caricamento e conservazione degli allegati, PDF multipagina da foto, controllo APE) e le correzioni [frontend #316](https://github.com/casazen/frontend/pull/316) e [backend #562](https://github.com/casazen/backend/pull/562) sono in `develop` e in **produzione dalla release v2.1.0** del 23 ottobre; la v2.1.1 ha reso non bloccante il controllo dell'APE (backend #569) e la v2.1.2 (23 ottobre, è la versione di `main`) legge codice e classe energetica dal documento (backend #578). **In produzione non esiste alcun codice CF** (`practice`, firma del link, client di stato, pagamento, omaggio): tutto il resto della tabella è in `develop`. Integrazione in `develop` e CI verde non attestano la disponibilità in produzione. Leggere AGENTS.md dei repository e lavorare sul codice corrente.

| Area | Stato in `develop` (25 ottobre) | PR |
|---|---|---|
| Pagina `/app/long-rent/documents/upload`, archivio `LeaseDocument` con API autorizzate, PDF multipagina da foto, OCR e controllo APE nell'archivio della casa | Fatto (in produzione) | #315/#561, #316/#562, #569, #578 |
| Tipo di contratto come prima scelta; ingressi «Ho già il contratto e i documenti» e «Parto da zero» | **Fatto** | frontend #346 (NL-FLOW) |
| Bozza minima del concordato con il suo `leaseId` | **Fatto** (con `POST /api/leases/drafts/concordatofacile`: bozza `Imported` marcata `IsConcordatoFacileSource`, una per immobile) | backend #582, #615, frontend #338, #346, #350 |
| Pagina del servizio, pagamento una tantum 119 € IVA compresa, pratica omaggio, accesso dell'admin di piattaforma | **Fatto**; i piani a 24 mesi non sono ancora in vendita | backend #587, #588, #590, #592, #593, #595, #602; frontend #339–#341, #347 |
| Link firmato dal server al clic; «Accedi alla tua pratica» | **Fatto** | backend #596; frontend #342 |
| Client di stato v0.2, snapshot di 15 minuti, rilettura breve, sezione «Stato pratica» | **Fatto nel backend** (CF-FIX2 ha corretto la registrazione del servizio, la forma della risposta, il controllo dell'organizzazione e l'attesa dopo un errore); **parziale nel frontend**: il pulsante non sparisce quando c'è una pratica | backend #591, #594, #597, #611; frontend #343 |
| Ritorno con `?practice=P-…`, lookup lato server, scelta manuale | **Fatto** (lookup in cache da CF-FIX2); conferma esplicita del contratto non ancora | backend #598, #602, #611; frontend #347 |
| Lettura dei documenti del contratto e bozza che si compila | **Fatto per il contratto** (locale, senza LLM); attestazione e ricevuta non ancora | backend #589, #604, #608; frontend #348 |
| Interruttore `Features:ConcordatoFacile` | **Fatto** | backend #602; frontend #347 |
| Collaudo con CF, chiavi di stato, accordo R24, rilascio in produzione | **Da fare** (processo, non codice) | — |

Fonti: «ConcordatoFacile — specifica endpoint di stato v0.2», ottobre 2026 (sostituisce la v0.1), governa link e letture di stato; della precedente «01-technical-specification», v1.3 del 13 ottobre 2026 (cartella `da-inviare/`, **superata**), si riusano soltanto il §2 per HMAC e vettore, le convenzioni richiamate dalla v0.2 e il punto R24. Non implementare webhook, download automatico dei file, vecchi `caseId` ed eventi, riapertura garantita o altri requisiti della v1.3 superati dal nuovo flusso. Nella v0.2 (§1 e §5) il servizio per CasaZen è sempre la **pratica completa**, fino alla registrazione. Il profilo di lettura del §6 è accettato da CF (v0.2 §4) e il 23 ottobre CF ha verificato con il codice del modulo i tre link di esempio del 22 ottobre. Le differenze aperte sono riportate sotto, senza inventare accordi.

Non ricostruire la pagina di upload, l'assemblaggio foto/PDF, l'OCR dell'APE o un secondo archivio. La funzione documentale multipagina è generale, non un requisito CF. Perimetro: solo web; l'app mobile non ha schermate dei contratti long-rent e resta fuori.

### Relazioni vincolanti

- Casa → documenti della casa: APE, planimetria e documenti propri dell'immobile.
- Casa → contratti; contratto → documenti del contratto: contratto definitivo, attestazione, registrazione e altri allegati.
- Il caricamento di allegati del contratto richiede `leaseId`; `propertyId` non lo sostituisce. La casa si ricava dal contratto autorizzato. Dalla casa si apre il contratto e poi la sua sezione Documenti.
- `leaseId` è l'UUID reale e stabile di `LeaseContract`, anche quando è una bozza. Non usare l'ID della casa, del pagamento o della pratica CF. L'importazione non crea un secondo contratto.

Punti di riuso: frontend `src/config/route-manifest.ts`, `src/api/leases.api.ts`, `src/queries/use-leases.ts`, `LeaseDocumentsPanel`, `src/lib/document-pdf.ts`; backend `LeaseDraftService`, `LeaseDocumentService`, `IApeDocumentInspector`, storage e autorizzazioni esistenti. Le API allegati sono `GET/POST /api/leases/{leaseId}/documents` e `GET /api/leases/{leaseId}/documents/{documentId}/download`.

### Coordinamento con la catena Affitti lunghi (aggiornato alla revisione 11)

Il piano di redesign dell'area Affitti lunghi (task con sigla LR-, documentazione fuori dal repository) costruisce in parallelo parti dello stesso ciclo di vita del contratto. Per non duplicare lavoro e non dividere i dati valgono queste regole.

- **Base già in produzione (LR-12, release v2.1.0).** `POST /api/leases/drafts { propertyId, origin? }` con `origin = Created | Imported` (nessun APE, parte o data richiesti), bozza salvata a passi (`LeaseDraftService`) e pulizia delle bozze ferme (`LeaseDraftRetentionService`, `LeaseDraftDeleter`, job `LeaseDraftRetentionJob`, opzione `LongRentLease__DraftRetentionDays`, oggi 180 giorni). La bozza minima del concordato usa questa API. Il riferimento al partner e alla pratica sta nel diritto (acquisto o omaggio) e nella pratica CF, non in `LeaseContract.Origin`.
- **Dalla bozza al contratto firmato: l'importazione LR-13 (in `develop` dal 23 ottobre con la PR backend #571, non ancora in produzione).** `POST /api/leases/{id}/import` (multipart: `signedContract`, PDF fino a 20 MB, oppure, dal 25 ottobre, `contractDocumentId` di un documento `Contract` già caricato per quella locazione; `stipulaDate`, non oltre oggi; permessi `lease.create` e `lease.sign`) su una bozza creata con `origin: Imported` porta il contratto a `Signed` senza modello approvato; un secondo import dà 409, non si crea un secondo contratto e il `leaseId` resta lo stesso. La registrazione si dichiara dopo con la dichiarazione manuale esistente (`POST /api/leases/{id}/registration/manual`, ricevuta obbligatoria). Una bozza importata ammette Libero, Concordato (nessun controllo di fascia) e Transitorio, con APE e dati catastali mancanti come avviso e nessuna revisione né «scaricato per la firma». Regole comuni: (1) un solo archivio visibile all'utente: il PDF importato è anche un `LeaseDocument` di tipo `Contract`, con un file proprio, e la sezione Documenti lo mostra; (2) la lettura della sezione 5 compila i valori della bozza (passi 1–4, stessa validazione) e l'utente conferma, senza sovrascritture silenziose, prima di chiamare l'importazione; (3) l'importazione non cambia il `leaseId`.
- **Archivio unico dei documenti (task LR-11a): superato.** Esistono già `LeaseDocument`, le API e la sezione Documenti (#315/#561): non si costruisce un secondo archivio dei documenti dei contratti.
- **Procedura «Nuovo contratto».** Il tipo di contratto è la prima domanda (sezione 2, NL-FLOW). Con il concordato non si prosegue con i passi dei dati: «Ho già il contratto e i documenti» porta alla lettura dei documenti (sezione 5), «Parto da zero» crea la bozza minima e porta alla pagina del servizio. Il percorso libero e il transitorio conservano i propri passi.
- **Stato e «prossimo passo» (LR-01b).** Per le bozze concordato CF la scheda ha un solo punto d'ingresso (sezione 4): il «prossimo passo» generale non duplica né ricalcola gli stati CF, che restano un'informazione separata. Il punto d'innesto `LeaseFlowInput.AwaitsPartnerContract` oggi **non è impostato da nessuno**.
- **Fine contratto (LR-14).** Un'eventuale «duplica come bozza» crea un nuovo contratto con un nuovo `leaseId`: una nuova pratica CF richiede un nuovo pagamento o l'omaggio (sezione 3) e la pratica CF non si copia.
- **Migrazioni.** La catena delle migrazioni di `LeaseContract` e delle tabelle collegate è una sola: una migrazione per volta. Chi si fonde dopo rigenera la propria con `dotnet ef`.

## 2. Ingresso e bozza minima concordato

Il flusso di prodotto parte dalla scelta del tipo di contratto, prima di parti, termini economici o calcolatore. **Fatto (NL-FLOW, frontend #346):** `/app/long-rent/leases/new` chiede prima il tipo (concordato 3+2, libero 4+4, transitorio) e poi il come. Per il concordato solo: «Ho già il contratto e i documenti» (si caricano i documenti e la bozza si compila da sola, sezione 5; non passa dal pagamento né da CF) oppure «Parto da zero» (bozza, pagamento e modulo di CF con il link firmato dal nostro server; aperto solo con l'interruttore acceso). Il concordato **non si prepara mai con il wizard di CasaZen**: il vero contratto è preparato da CF e poi caricato o importato. Libero e transitorio chiedono «Lo preparo con CasaZen» o «Ho già i documenti» (stessa lettura).

1. Con casa selezionata e tipo concordato, salvare subito una bozza minima `LeaseContract` con PropertyId, OrgId e tipo, e restituire `leaseId`; non generare soltanto un UUID nel browser. **Fatto:** `POST api/leases/drafts/concordatofacile` (frontend `useCreateCfDraft`; backend #615, frontend #350): bozza minima `Imported`, tipo concordato, marcata `IsConcordatoFacileSource`, una per immobile.
2. Non richiedere APE, parti, canone, deposito, date, coefficienti o attestazione per ottenere il riferimento della bozza. Non allentare per questo i controlli dei percorsi di firma e registrazione esistenti. **Fatto.**
3. Mostrare casa, `leaseId` copiabile e «Bozza — in attesa del contratto del partner». I dati mancanti restano da acquisire, senza importi zero, date o persone fittizie presentate come definitive. La bozza non genera occupazione, incassi o adempimenti di un contratto attivo. **Fatto** (avviso di bozza e riquadro «Riferimento per il partner»).
4. Proteggere il `leaseId` già usato per un acquisto o una pratica CF dalla cancellazione automatica delle bozze abbandonate, senza disabilitare la retention generale. **Fatto per acquisti e omaggi:** le chiavi esterne `Restrict` di `ConcordatoFacilePurchases` e `ConcordatoFacileGifts` impediscono di cancellare un contratto che ne ha uno, e `LeaseDraftDeleter` protegge in più le bozze marcate CF con un acquisto non fallito. L'accesso dell'admin non lascia righe: una bozza usata solo da un admin non è protetta (limite noto, dal codice).
5. Doppi click e retry della stessa azione riusano la bozza salvata; resta possibile creare intenzionalmente un nuovo contratto sulla stessa casa. **Fatto.**
6. La scheda del contratto (`/app/long-rent/leases/:id`) ha un solo punto d'ingresso al servizio, il cui testo cambia con lo stato (sezione 4): «Procedi con la creazione del contratto» porta all'offerta o al pagamento, all'omaggio e poi al modulo del partner. L'admin CasaZen segue l'accesso diretto descritto sotto. Il percorso libero mantiene il proprio funzionamento: non inviarlo al modulo riservato ai concordati. **Parziale:** vedi la sezione 4.

## 3. Offerta, pagamento e pratica omaggio

### Pagina servizio

`/app/long-rent/leases/:id/concordatofacile`, raggiunta da «Procedi con la creazione del contratto». Mostra casa e contratto, il prezzo **119,00 € totali IVA compresa per pratica** (una pratica per contratto), l'eventuale omaggio e questi passaggi prima del pagamento:

1. Paghi 119 euro o usi la pratica omaggio, poi vieni reindirizzato al modulo del partner ConcordatoFacile.
2. Compili il modulo con i dati e i documenti richiesti.
3. Gestisci direttamente via e-mail con il partner integrazioni, revisione del contratto e firma, fino alla registrazione prevista dal servizio.
4. Ricevi l'e-mail finale del partner con i documenti e il link per caricarli in CasaZen.
5. Ti consigliamo di caricarli sul tuo contratto per conservare e gestire tutti i contratti in modo centralizzato in CasaZen.

Registro e bollo eventualmente dovuti sono esclusi. Il caricamento è effettuato dall'utente: non promettere un trasferimento automatico dei file da CF. I costi di acquisto dal fornitore non sono il prezzo dell'utente e non vanno esposti nel checkout. **Fatto,** tranne la riga su registro e bollo, che la pagina non riporta (CF-DOC-FU5).

| Condizione verificata dal server | Azione | Stato |
|---|---|---|
| Utente senza diritto | «Paga 119 euro e continua» → checkout CasaZen una tantum | Fatto |
| Omaggio disponibile | «Usa la pratica omaggio e continua» → assegnazione atomica a questo `leaseId`, poi CF | Fatto |
| Pratica già pagata o omaggio già associato a questo `leaseId` | Nessun nuovo addebito o consumo; «Accedi alla tua pratica» finché non risulta una pratica inviata, poi lo stato della pratica (sezione 4) | Fatto; il pulsante non sparisce quando c'è una pratica |
| Admin di piattaforma CasaZen | Accesso diretto dal contratto, senza pagina di pagamento e senza consumare omaggi | Fatto |
| Pagamento in attesa, fallito o annullato | Stato comprensibile, nessun accesso CF sbloccato | Parziale: il pagamento ritardato che poi fallisce lascia l'acquisto `Pending` e blocca un nuovo checkout (CF-FIX-FU2) |

### Controlli e persistenza

- Acquisto **una tantum**, senza cambiare l'abbonamento e senza utilizzare pagamenti degli affitti o Stripe Connect. **Fatto con un servizio dedicato** (`ConcordatoFacileCheckoutService`, ADR-005), non estendendo il billing degli abbonamenti: riusa il webhook di piattaforma, la verifica della firma e `ProcessedStripeEvent`.
- Il server determina EUR **11900 centesimi, IVA già inclusa**; Price e configurazione fiscale mantengono il totale; non si aggiunge IVA sopra i 119 €. L'esenzione dichiarata dall'associazione non si applica automaticamente alla vendita CasaZen. **Fatto.**
- Il diritto è legato a OrgId e LeaseId e ne conserva origine, stato, identificativi del pagamento e date. Si sblocca da pagamento confermato dal server, con eventi Stripe verificati e corrispondenza di acquisto, importo, valuta e organizzazione; il ritorno `checkout=success` non è una prova. **Fatto:** i metadati `orgId`/`leaseId`/`userId` stanno sulla sessione **e** sul PaymentIntent, senza la chiave `kind` (con `kind` il pagamento andrebbe ad altri gestori); `payment_intent.succeeded` completa l'acquisto solo con importo e valuta giusti.
- Solo l'admin di **piattaforma** (`CasazenPolicies.AdminOnly`, ruolo JWT `Admin`) salta il pagamento; l'admin dell'organizzazione o `OrgBillingAdmin` no. Autorizzazione al contratto e audit (`ConcordatoFacileAdminAccess`); non si simula un pagamento admin. **Fatto.**
- L'abbonamento sottoscritto per **due anni** assegna **una** pratica omaggio all'organizzazione, condivisa tra i membri autorizzati: non equivale a 24 pagamenti mensili o a due anni di anzianità. **Fatto:** `customer.subscription.created` con un Price di `ConcordatoFacile__BiennialPriceIds`, anche con il servizio spento; oppure a mano con `POST api/admin/orgs/{orgId}/concordatofacile/grant-gift` (solo admin di piattaforma). I Price a 24 mesi esistono ma sono provvisori e la **vendita** dei piani a 24 mesi non è in `develop` (task BIENNIAL); il regalo non richiede ancora una sottoscrizione `active` o `trialing`.
- Persistenza dell'omaggio: tabella `ConcordatoFacileGifts` (una riga per organizzazione, indice unico su `OrgId`) con il contratto beneficiario e le date; nessun omaggio retroattivo per gli abbonamenti esistenti (si trattano a mano con l'endpoint admin); rinnovi, refresh ed eventi duplicati non rigenerano un beneficio già assegnato o consumato. **Fatto.**
- Consumare l'omaggio e associarlo al contratto nella stessa transazione, con controllo di concorrenza tra schede e membri (lock consultivo di PostgreSQL); rientrare per lo stesso contratto resta gratuito, un altro contratto → 409 `concordatofacile_gift_already_consumed`; i GET non consumano il beneficio. **Fatto:** un contratto inesistente o di un'altra organizzazione è 404 `lease_not_found` prima di toccare l'omaggio (CF-FIX2, backend #611; prima non si controllava). `consume-gift` scrive ma richiede solo il permesso `lease.read` (CF-DOC-FU11, da decidere).
- Checkout idempotente: ricontrollare il diritto prima di creare una sessione; gestire conferme duplicate, fuori ordine o tardive senza doppio diritto né doppio addebito. **Fatto** per sessione riusata, 409 `concordatofacile_already_purchased`, sessione `complete` e `paid` recuperata senza attendere il webhook, 404 `lease_not_found` per contratti di altre organizzazioni. Restano: pagamento ritardato che poi fallisce (CF-FIX-FU2) e due clic simultanei (CF-FIX-FU3).
- Rimborsi verificati aggiornano il diritto, senza cancellare automaticamente pratica CF o documenti, e senza inventare una politica commerciale di rimborso. **Fatto per il rimborso totale** (`charge.refunded`: acquisto `Refunded`); un rimborso parziale lascia lo stato; le contestazioni (`charge.dispute.created`) non sono gestite per CF.

Letture e comandi interni, nel contesto del contratto: `GET …/access`, `POST …/checkout`, `POST …/consume-gift`. Il comando di prosecuzione autorizza sempre sul server: il frontend non decide la gratuità e non aggira il controllo aprendo un URL costruito da solo. Login e checkout preservano il contratto di partenza.

La firma del link non sostituisce il diritto al servizio: generarne una nuova richiede sempre autorizzazione, pagamento o omaggio o accesso admin verificati sul server. La v0.2 permette comunque di compilare il modulo anche con link assente, invalido o scaduto, creando una pratica senza `leaseId`: concordare con CF la riconciliazione operativa prima di addebitare o lavorare pratiche non collegate, e non usarne l'esistenza o il solo abbinamento come prova del pagamento.

## 4. Link firmato e ritorno dalle email

### Apertura del modulo

Comando interno autenticato e autorizzato sul contratto (`POST /api/leases/{leaseId}/concordatofacile/link`, permesso `lease.create`) che controlla il diritto e genera al clic un link a **`https://app.concordatofacile.it/`**. Nessuna firma preparata nel browser o conservata nel checkout o nelle e-mail CasaZen. Il comando registra soltanto l'emissione (contratto e ora; mai l'URL né la firma): serve all'audit e alla rilettura breve della sezione 6. **Fatto** (backend #596, frontend #342).

| Parametro | Valore e regola v0.2 |
|---|---|
| `partner` | `casazen` |
| `leaseId` | UUID reale della bozza, in minuscolo |
| `expiresAt` | UTC, adesso + 15 minuti, formato esatto `yyyy-MM-ddTHH:mm:ssZ`, senza frazioni |
| `signature` | HMAC-SHA256 di `leaseId + "." + expiresAt`, UTF-8, chiave `linkSecret`, output esadecimale minuscolo |
| `comune` | Nome del comune della casa, dalla sorgente territoriale esistente (tabella dei comuni ISTAT) |
| `provincia` | Sigla provinciale, per esempio `MB`, dalla stessa sorgente |

Firmare i **valori decodificati esatti**, poi codificare ogni valore per la query con una libreria URL. Non firmare il testo URL-encoded né normalizzare dopo la firma. Verificare il vettore pubblico della v1.3 §2, ripreso dalla v0.2; non usare la sua chiave dimostrativa in produzione. Comune e provincia non sono coperti dalla firma. Il comune precompila la ricerca del modulo: se non è nell'elenco di CF (oggi Seveso, comune pilota, non c'è) il modulo lo lascia scritto e l'utente prosegue con quel nome; la pratica resta legata al contratto e `comune` arriva nello stato ridotto a lettere, spazi, apostrofi, trattini e parentesi. Non è un errore da gestire in CasaZen: non cambiare il nome inviato e aprire il punto con CF (sezione 6). CF tollera due minuti di scarto e rifiuta scadenze oltre 60 minuti: CasaZen emette sempre 15 minuti nella forma canonica. **Il segreto `linkSecret` lo genera CF**, non CasaZen: per la prova vale il segreto pubblico del vettore, per la produzione quello che CF consegna (il modulo di produzione di CF rifiuta il segreto di prova); configurazione e segreti distinti per prova e produzione, solo backend, mai nel bundle, nelle query interne, nei log o nella telemetria. Il link restituito contiene una firma temporanea: niente log del relativo URL completo e risposte non cacheabili (`Cache-Control: private, no-store`). **Fatto.**

Vettore pubblico di conformità v1.3 §2, confermato dalla v0.2 (è una fixture dimostrativa, **non una credenziale operativa**):

```text
linkSecret = test_link_secret_0123456789abcdef0123456789abcdef
message    = 0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11.2026-10-13T10:00:00Z
signature  = fa10ce472c1e3b33939e62e01a7f5f8cec10d45abc02726f691cae37bd639eea
```

La scadenza storica serve al test puro della firma, non all'apertura live. Nel link il relativo `expiresAt` è `2026-10-13T10%3A00%3A00Z`; il messaggio HMAC mantiene i due punti decodificati. Per i link reali usare invece scadenza corrente + 15 minuti e il segreto dell'ambiente.

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

Non inviare `comuneIstatCode`, `returnUrl` o `theme`: CF li ignora. Aprire in navigazione normale, mai in un iframe; in una nuova scheda con `noopener noreferrer`. CF rimuove i parametri firmati dalla barra prima degli script di terzi; non dare per questo il `leaseId` come autenticazione dell'utente. **Fatto.** Nessun link prodotto dal server è però ancora stato verificato da CF, e il test del vettore ricalcola la firma senza chiamare il servizio (CF-DOC-FU9).

### Stati della scheda del contratto: una pratica per contratto

Nella scheda del contratto concordato il punto d'ingresso al servizio cambia con lo stato; i testi dei pulsanti sono quelli indicati.

| Stato | Che cosa vede l'utente | In `develop` |
|---|---|---|
| Diritto non acquisito | «Procedi con la creazione del contratto» → pagina servizio (sezione 3) | Fatto |
| Pagamento in attesa, fallito o annullato | Stato comprensibile, nessun accesso al modulo | Parziale (sezione 3) |
| Diritto acquisito (pagamento, omaggio o admin) e nessuna pratica nell'elenco CF | «Accedi alla tua pratica»: genera il link firmato e apre il modulo. Il testo avverte che la bozza riprende solo sullo stesso dispositivo e browser; da un altro il modulo riparte da zero | Fatto |
| Almeno una pratica nell'elenco CF, in qualunque stato (anche chiusa) | **Nessun pulsante**: sezione «Stato della pratica» (sezione 6) con tutte le pratiche, distinguendo quelle chiuse per duplicazione. Dopo l'invio il modulo non si riapre, nemmeno con `ClosedWithoutAttestation`: una nuova pratica richiede un nuovo contratto, con nuovo pagamento o omaggio | **Non fatto:** il pannello della scheda non legge lo stato e il pulsante non sparisce; lo stato sta solo nella pagina del servizio, sotto lo stesso pulsante (CF-DOC-FU4) |

Prima dell'invio l'utente può rientrare nel modulo quante volte serve, senza nuovo addebito. Dopo l'invio o da un altro dispositivo un nuovo link aprirebbe una compilazione nuova e quindi un'altra pratica: per questo il pulsante deve sparire appena la lettura di stato mostra una pratica. Il ritardo tra invio, pubblicazione di CF e lettura è coperto dalla rilettura breve della sezione 6. Un doppione resta possibile e lo chiude CF come `duplicate`: elencarlo, non nasconderlo, e non promettere unicità garantita da CF.

Con errore nella lettura non interpretare «nessuna pratica»: se esiste uno snapshot precedente usarlo, segnalato come non aggiornato; se non c'è nessuno snapshot, mostrare «Accedi alla tua pratica» con l'avviso «Non riusciamo a verificare lo stato: se hai già inviato il modulo non riaprirlo». Dopo il clic non rilanciare il modulo in automatico. **Parziale:** lo snapshot non aggiornato è usato e segnalato; l'avviso per il caso senza snapshot esiste come testo (`concordatofacile.entry.statusFailed`) ma non è mostrato (CF-DOC-FU4).

### Ritorno ai documenti: la pagina esistente

La pagina `https://casazen.space/app/long-rent/documents/upload` accetta `?leaseId=<uuid>` (link vecchi, in produzione dalla v2.1.0) e **`?practice=P-…`** (in `develop`, backend #598 e #602, frontend #347; non ancora in produzione). Il bottone «Carica i dati in CasaZen» di CF porta `?practice=P-…`, come nella v0.2. Si estende la stessa pagina e lo stesso ritorno dal login, senza un altro archivio.

Parametro comunicato a CF: percorso e solo `?practice=<practiceId>`; il `leaseId` non serve nel link: arriva dal lookup della pratica o dalla scelta dell'utente, ed è comunque indispensabile per salvare gli allegati. I link esistenti con solo `?leaseId=<uuid>` restano supportati. **Requisito aggiornato (rev. 11):** se nella query compaiono entrambi i parametri vale il contratto nominato da `leaseId`, perché è la scelta manuale dell'utente quando la pratica non è collegata o lo è a un altro contratto (la revisione 10 diceva il contrario); la scelta non cambia il `leaseId` della pratica remota.

- Validare UUID, formato `P-` più 16 caratteri esadecimali e parametri duplicati. Per `practice`, il backend usa `GET /v1/practices/{practiceId}` e espone dati soltanto se il `leaseId` restituito appartiene all'organizzazione e l'utente è autorizzato; nessun proxy pubblico per enumerare pratiche del partner. Con il link vecchio il `leaseId` della query non prova nulla: contano l'autorizzazione sul contratto e la conferma dell'utente. **Fatto** (`GET /api/concordatofacile/practices/{id}/lease`, 404 uniforme; formato errato o ripetuto: «Collegamento non valido» e scelta manuale). Il lookup è in cache (15 minuti; 1 minuto per una pratica che CF non conosce ancora).
- Il collegamento CF non prova che il contenuto caricato appartenga al contratto: prima di abilitare l'upload far scegliere o confermare esplicitamente **casa e contratto autorizzati**, mostrando riferimento e dati disponibili, e consentire la correzione. Non usare `propertyId` come destinazione degli allegati. **Parziale:** la scelta manuale c'è quando la pratica non è collegata o non si trova; con una pratica collegata si mostrano subito i documenti di quel contratto, senza passo di conferma (CF-FIX-FU4). Con il ritorno vecchio (solo `leaseId`) non c'è conferma.
- `leaseId` nullo, pratica non trovata o non autorizzata, oppure lookup non disponibile (errore, timeout, 429, servizio spento): nessuna associazione automatica, nessun dettaglio di altri tenant; scelta di un proprio contratto per conservare i documenti, avvisando, se la pratica non è collegata, che il collegamento deve essere corretto **da CF manualmente**; nessuna API di scrittura verso CF, che la specifica non offre. **Fatto,** con un avviso più breve del voluto («La pratica P-… non è ancora collegata a un contratto»: non dice che la correzione spetta a CF).
- Upload e download riusano le API `LeaseDocument` sul `leaseId` selezionato. Non importare file da CF, non richiedere un altro acquisto, non fatturare in base al solo `practiceId`/`leaseId`, non creare un legame CF obbligatorio al documento caricato su un contratto diverso da quello restituito. **Fatto.**

CasaZen non riceve e-mail di pratica o finali: le comunicazioni e il bottone sono nelle e-mail **all'utente**. Il bottone è una pagina web, non l'endpoint di stato o un'API di importazione. CF lo ha già impostato su `https://casazen.space/app/long-rent/documents/upload?practice=P-…` (v0.2 §7) e lo tiene **spento** finché CasaZen non conferma, dopo il rilascio in produzione e il collaudo del ritorno con `practice`.

### Fine del percorso: dal contratto di CF al contratto firmato in CasaZen

Quando CF consegna il contratto firmato, l'utente lo carica dalla pagina di ritorno o dalla sezione Documenti del contratto e completa l'importazione: **fatto** (CF-IMPORT, backend #615, frontend #350). La bozza «Parto da zero» nasce con origine `Imported`, quindi il contratto caricato si legge (NL-DOCS) e `POST /api/leases/{id}/import` (con `contractDocumentId`) lo porta a «Firmato»: da lì partono scadenze, canoni e registrazione. Resta il requisito della revisione 10: stesso `leaseId`, nessun secondo contratto, una sola copia del PDF.

## 5. Lettura dei documenti del contratto e popolamento dei dati

**Fatto per il contratto (NL-DOCS, backend #604 e #608, frontend #348); attestazione e ricevuta non ancora.** La pipeline asincrona di ADR-009 (backend #589) è sostituita, per le bozze «Importato», dalla lettura locale descritta nel runbook [`lease-document-extraction.md`](../docs/runbooks/lease-document-extraction.md); per ogni altra locazione (un contratto che esiste già) resta la prima pipeline, che scrive nel contratto solo i pochi campi di cui è certa e non contraddice nulla.

| Documento | Dati da acquisire quando presenti | Destinazione | In `develop` |
|---|---|---|---|
| Contratto definitivo CF | Parti e ruoli, immobile, tipo, durata e date, stipula, canone, deposito e regime fiscale esplicito | Dati del contratto e PDF consultabile | **Fatto** per la bozza «Importato»; per la bozza «da zero» solo la prima pipeline (CF-DOC-FU10) |
| Attestazione | Numero e data, accordo territoriale e comune, attestatori, dati attestati del canone | Sezione Attestazione e PDF consultabile | Non fatto: archiviata e consultabile, non letta (CF-DOC-FU6) |
| Ricevuta di registrazione | Data, protocollo o codice, ufficio, esito ed eventuali dati fiscali espressi nel documento | Sezione Registrazione e PDF consultabile | Non fatto, come sopra |

- Input già normalizzato: un PDF preesistente è conservato con gli stessi byte; più foto diventano un solo PDF ordinato e scaricabile, senza originali fotografici separati; non si riscrivono PDF firmati o ricevute per aggiungere il testo OCR. **Fatto.**
- Estrarre il testo del documento intero, usando l'OCR quando serve, anche se i dati sono su più pagine; riusare gli strumenti locali già introdotti per l'APE; non riutilizzare il validatore APE come validatore contrattuale; non introdurre un provider esterno per presunzione. **Fatto, e ora è una decisione (default NL-DOCS):** PdfPig nel processo e, per scansioni e foto, Tesseract `ita+eng` con gli stessi limiti dell'APE; **nessun servizio esterno e nessun LLM**, perché un contratto contiene nomi, codici fiscali e indirizzi e non c'è un sub-responsabile approvato; niente del testo nei log.
- Popolare automaticamente i dati affidabili dopo controlli strutturali e coerenza con casa, parti e contratto; campi mancanti restano da acquisire; valori incerti o conflitti vanno in revisione; nessuna sovrascrittura silenziosa dei dati confermati. **Fatto con queste regole:** si compila **solo ciò che è vuoto**, mai si sovrascrive; un valore diverso scritto nel contratto è un conflitto segnalato (la nota «il contratto dice altro»), non scritto; una persona si legge solo con un codice fiscale valido; un canone senza periodo non si legge, quello annuo si divide per 12 e si segna calcolato; un valore che le regole del flusso rifiuterebbero (deposito oltre 3 canoni, durata non ammessa dal tipo) non si scrive e resta da chiedere. Ogni valore letto porta un segno «Letto dal contratto — controlla» ed è un campo normale, modificabile. L'evidenza della pagina del documento non c'è.
- Se il file è sbagliato, l'OCR fallisce o il documento è incompleto: resta consultabile e si continua a mano con il PDF già caricato, senza inventare dati né eliminare l'upload. **Fatto:** stato `Failed` con codice stabile (`unreadable`, `ocr_unavailable`, `no_text`, `nothing_found`, `file_missing`) e `Skipped` per un altro tipo di documento, un duplicato o una bozza già andata avanti. Il controllo bloccante dell'APE non si estende agli allegati del contratto.
- Soltanto i metadati di elaborazione necessari (stato, provenienza, impronta e risultato utile), con audit e concorrenza. **Fatto:** colonne `Extraction*` e `DocumentHash` su `LeaseDocument`; `ExtractionResult` non contiene **alcun valore personale** (solo nomi dei campi, esiti e confidenza); i valori vanno solo nella bozza cifrata. Le vecchie righe di versione 1 contengono ancora nomi, canone e date in chiaro (NL-DOCS-FU6, serve una decisione sull'`UPDATE`).
- Distinguere «Caricato», «Elaborazione», «Dati acquisiti» e «Da verificare»; caricare il contratto non prova attestazione o registrazione; una ricevuta di trasmissione non prova l'esito positivo; non avviare un secondo invio fiscale. **Fatto** (`GET /api/leases/{id}/draft/reading`: `None`, `Reading`, `Done`, `Failed`; documento `Processing`, `Completed`, `NeedsReview`, `Failed`, `Skipped`).
- Autorizzazioni `lease.read`/`lease.create` secondo l'operazione, isolamento OrgId e verifica di `documentId` insieme a `leaseId`; nessuna duplicazione in `PropertyDocuments`; nessun secondo acquisto per caricare o consultare i documenti. **Fatto** (un documento di un'altra organizzazione dà `tenant_isolation_violation` e non si legge mai nella bozza).
- L'importazione finale usa il PDF già caricato (`contractDocumentId` al posto del file) e il giorno della firma letto. **Fatto.**

L'APE resta un documento della casa. Il suo OCR verifica i dati fondamentali e le date sull'intero attestato; non è una prova di autenticità nel registro regionale e non sostituisce le verifiche del partner; non blocca l'assegnazione del `leaseId` alla bozza minima. Dalla v2.1.1 il caricamento dell'APE di una casa non è più bloccante: il file è sempre conservato con un esito di controllo (`Verified`, `Incomplete`, `NotRecognized`, `NotChecked`) e, per creare, firmare o registrare un contratto, bastano codice e classe energetica scritti dal locatore oppure un APE verificato; dalla v2.1.2 codice e classe si leggono anche dal documento. Il controllo APE non si applica agli allegati del contratto.

## 6. Stato CF v0.2: lettura e cache applicativa

Client HTTP tipizzato backend verso **`https://stato.concordatofacile.it`** (è il default del codice: `StatusApiUrl` non si imposta), HTTPS, TLS 1.2 o superiore. Autenticazione `Authorization: Bearer <chiave>` (`cfsp_test_…` o `cfsp_live_…`), separata dal segreto HMAC, una chiave per partner e ambiente; più chiavi possono coesistere per la rotazione; CF le consegna al referente CasaZen per e-mail con un Bitwarden Send a una sola apertura, quando l'endpoint di prova è pronto, e la password su un altro canale concordato a parte (v0.2 §3). Variabile `ConcordatoFacile__StatusApiKey`; senza chiave l'API di stato è spenta con un avviso nel log. Non acquisire o salvare credenziali nei documenti o nei repository. **Fatto** (backend #591).

| Chiamata CF | Risultato |
|---|---|
| `GET /v1/leases/{leaseId}/practices` | `{ leaseId, practices: [...] }`, dalla più recente, fino a 20. Contratto sconosciuto, non CasaZen o senza pratiche: sempre 200 e array vuoto |
| `GET /v1/practices/{practiceId}` | Oggetto della singola pratica con `leaseId`, anche null; pratica inesistente, di altro partner o ambiente: stesso 404 |

Oggetto pratica: `practiceId`, `status`, `statusChangedAt`, `submittedAt`, `comune`, `comuneDiffersFromLink`, `closeReason`; `leaseId` si aggiunge nel dettaglio. `practiceId` è il numero CF, `P-` più 16 caratteri esadecimali, **non** il vecchio `caseId`. JSON camelCase, date ISO 8601 UTC con Z, errori `application/problem+json` con `code` snake_case. Il comune non è un codice ISTAT; `comuneDiffersFromLink` è un booleano **nullable** (`null` se il link non indicava il comune o la pratica non è collegata) e non dimostra coerenza degli altri dati del contratto. **Corretto da CF-FIX2 (backend #611):** il client leggeva l'elenco come array (ADR-008-R6 era sbagliata) e `comuneDiffersFromLink` come booleano non nullable, quindi falliva sulla risposta vera di CF; ora legge l'oggetto `{ leaseId, practices }` e il valore nullable (che nella risposta di CasaZen esce come `false` quando è `null`). Il servizio della cache dello stato non era registrato nel contenitore: con l'interruttore acceso ogni azione del controller falliva già nella creazione.

| Stato CF | Presentazione e comportamento |
|---|---|
| `Received` | Modulo inviato, pratica ricevuta |
| `InProgress` | In lavorazione |
| `AwaitingUser` | Dati o documenti richiesti: proseguire per e-mail con CF |
| `DraftSent` | Bozza inviata da approvare e firmare tramite il partner |
| `SignedReceived` | Firmati ricevuti |
| `SentToAssociation` | Inviata all'associazione per attestazione |
| `Attested` | Attestazione rilasciata, registrazione in corso; non finale |
| `Registered` | Contratto registrato; unico stato finale positivo |
| `ClosedWithoutAttestation` | Chiusa senza attestazione (finale); mostrare il motivo noto |
| `Suspended` | Sospesa, può riprendere; non finale |

Motivi di chiusura: `duplicate`, `withdrawn`, `not_compliant`, `other`. `InProgress` e `AwaitingUser` possono alternarsi, `Suspended` può riprendere; `statusChangedAt` non retrocede. Non modellare una progressione numerica irreversibile e non nascondere i doppioni: elencare tutte le pratiche, distinguendo quelle chiuse per duplicazione. Valori, campi e codici sconosciuti non fanno fallire la lettura e non diventano «completata»: presentazione neutra, nessuna automazione finché la semantica non è nota. **Fatto** (`CfPracticeStatus` con `Unknown`).

Per CasaZen il servizio è sempre la **pratica completa**, fino alla registrazione (v0.2 §1 e §5): nessuna scelta base/completo nel modulo e nessun campo servizio nell'endpoint. `Attested` è un passaggio intermedio («Attestazione rilasciata, registrazione in corso»), `Registered` è l'unico finale positivo e `ClosedWithoutAttestation` il finale negativo; non dichiarare concluso il servizio prima di `Registered`. Se CF attivasse altri servizi per CasaZen, la v0.2 prevede che avvisi prima e aggiunga un campo nell'endpoint: finché non è documentato, un campo o un valore sconosciuto non diventa mai «completata». Il prezzo di vendita resta 119 € IVA inclusa; non c'è un secondo checkout.

### Chiamate CasaZen e frequenza

- Lettura interna autenticata nel contesto del contratto, `GET /api/leases/{leaseId}/concordatofacile/status` (permesso `lease.read`), per la sezione «Stato della pratica» e per il pulsante «Accedi alla tua pratica»; non è l'URL di CF. Autorizzare il contratto prima della chiamata remota e prima di leggere qualsiasi risultato memorizzato. **Fatto (CF-FIX2, backend #611):** un contratto inesistente o di un'altra organizzazione è 404 `lease_not_found` prima di leggere la cache o di chiamare CF (ADR-008-R11; prima non si controllava), senza eccezioni per l'amministratore di piattaforma. Il lookup della pratica del ritorno applica i controlli della sezione 4 (e verifica l'organizzazione).
- Interrogare CF quando l'utente apre la **scheda del contratto**, se lo snapshot valido è scaduto: **non più di una lettura ogni 15 minuti per `leaseId`**, condivisa fra schede e utenti autorizzati dell'organizzazione. Anche «Aggiorna» e i ritorni ripetuti rispettano il periodo. Niente timer del browser fuori dalla pagina aperta, job periodico o polling in background: è il profilo accettato da CF nella v0.2 (§4). **Fatto** (lo snapshot sta nella memoria del processo, con chiave ambiente/organizzazione/contratto, e un semaforo per contratto accorpa le richieste concorrenti).
- **Rilettura breve dopo l'apertura del modulo.** Dopo l'emissione di un link, finché l'elenco non mostra una pratica, la scheda può rileggere lo stato al massimo **ogni 5 minuti per contratto, per 60 minuti dall'ultima emissione**, e solo quando l'utente apre la pagina o la rimette in primo piano; poi, o appena compare una pratica, torna il periodo di 15 minuti. Serve a far sparire il pulsante pochi minuti dopo l'invio (oggi il pulsante non sparisce, CF-DOC-FU4). **Fatto** per la lettura.
- Memorizzare l'ultimo risultato con `fetchedAt`, ambiente, organizzazione, contratto, scadenza e stato dell'ultimo tentativo. Una cache per istanza non garantisce il limite in più repliche: oggi la cache è nella memoria del processo (un solo servizio per ambiente; il numero di repliche non è stato verificato, CF-DOC-FU8). Per il dettaglio di ritorno applicare lo stesso intervallo al `practiceId` ed evitare di ricontattarlo se un elenco già valido ne contiene il risultato autorizzato. **Fatto** per il lookup (CF-FIX2): in cache per ambiente e numero di pratica, 15 minuti, 1 minuto per una pratica che CF non conosce ancora, mai una lettura fallita; non si riusa ancora l'elenco già letto.
- CF imposta `Cache-Control: no-store`: nessuna cache HTTP, browser o proxy; lo snapshot è stato applicativo privato e autorizzato; le risposte di CasaZen hanno `Cache-Control: private, no-store`. Mostrare data dell'ultimo passaggio CF e data dell'ultima verifica come informazioni distinte, con l'indicazione del prossimo aggiornamento. **Fatto.**
- Limite teorico delle sole letture elenco: **4 richieste/ora per contratto consultato continuativamente**, altrimenti zero; solo nell'ora successiva a ogni apertura del modulo fino a **12 richieste/ora** per quel contratto; si sommano i lookup per `practiceId` degli arrivi da e-mail. Come ordine di grandezza, 50 contratti consultati senza sosta sono al massimo 200 richieste/ora, circa 3 al minuto. La formula è comunicata a CF, che definisce il limite per chiave; nessun refresh forzato la aggira.

### Errori e integrità

- 400 `invalid_lease_id`/`invalid_practice_id`; 401 `unauthorized` con `WWW-Authenticate`; 404 `practice_not_found` solo sul dettaglio; 429 `rate_limited` con `Retry-After` in secondi. Non trasformare errori, timeout, 5xx o chiave errata in array vuoto o stato finale. **Fatto.**
- Timeout e tentativi limitati: 3 tentativi con attese di 1, 2 e 4 secondi e 30 secondi per tentativo; un 429 con `Retry-After` fino a 30 secondi si attende e si ritenta una volta, oltre o senza intestazione è un errore. Conservare l'ultimo risultato valido come non aggiornato, senza presentarlo come verifica appena riuscita. La cache di un elenco vuoto scade anch'essa dopo 15 minuti: CF può ritardare la comparsa della pratica, con ritentativi propri ogni 15 minuti. **Fatto;** dopo un errore lo snapshot non aggiornato si serve per 5 minuti senza richiedere di nuovo a CF (CF-FIX2; prima era riletto subito a ogni visita). Un primo errore senza snapshot non è limitato (CF-FIX2-FU1).
- Non applicare un risultato con `statusChangedAt` più vecchio per la stessa pratica; distinguere l'assenza dalla mancata associazione e dai problemi di configurazione. Con `comuneDiffersFromLink=true` avvertire l'utente di verificare il contratto, senza correggere silenziosamente la casa. **Fatto.**
- Nessun webhook CF, evento finale con dati fiscali o download automatico dei file. Stato CF, lettura documentale e dati di attestazione e registrazione restano distinti: `Registered` è uno stato del partner, non una ricevuta già conservata in CasaZen. Nessun invio fiscale automatico aggiuntivo.

Prima del collaudo live servono la chiave dell'endpoint di prova e i casi nei vari stati; per la firma in prova resta il segreto del vettore pubblico della sezione 4 (v0.2 §3); la produzione usa chiave e segreto di firma diversi, consegnati allo stesso modo. Chiedere URL e commutazione dell'ambiente di prova: non assumere che i prefissi delle chiavi da soli definiscano la selezione dell'ambiente. Il comune pilota Seveso non è nell'elenco comuni del modulo di CF (il modulo accetta il nome scritto e la pratica resta legata al contratto): chiedere a CF se le pratiche di Seveso e degli altri comuni pilota sono lavorate normalmente e se possono entrare nell'elenco. L'inquadramento privacy v1.3 R24 resta da definire con CF prima delle pratiche reali; non dedurre ruoli privacy dall'assenza di nomi nell'endpoint.

## 7. Accettazione e consegna

| Prova | Risultato | In `develop` |
|---|---|---|
| Concordato scelto come primo passo | Bozza reale con soli casa e tipo; `leaseId` stabile; nessun APE o dato definitivo richiesto prima | Fatto |
| Offerta e checkout | Cinque passaggi comprensibili, 119 € IVA inclusa, conferma solo server, ritorno allo stesso contratto; nessun secondo pagamento | Fatto (manca la riga su registro e bollo) |
| Admin piattaforma / admin organizzazione | Solo il primo salta il pagamento; entrambi rispettano l'accesso alla risorsa | Fatto |
| Biennale, eventi duplicati e accessi concorrenti | Un omaggio assegnato una volta, consumabile da un solo contratto; rientro nel modulo prima dell'invio senza addebito | Fatto (controllo dell'organizzazione del contratto: CF-FIX2) |
| CF | HMAC conforme al vettore, UTC al secondo, scadenza 15 minuti e URL-encoding; nessun segreto al client; emissione del link registrata senza URL né firma | Fatto; link del server da far verificare a CF |
| Scheda del contratto | «Procedi con la creazione del contratto», «Accedi alla tua pratica» e «Stato della pratica» secondo lo stato; nessun pulsante appena l'elenco CF mostra una pratica, anche chiusa; nuova pratica solo con un nuovo contratto; avviso se lo stato non è leggibile | **Parziale** (CF-DOC-FU4) |
| Ritorno e-mail | `practice` (e il vecchio `leaseId`) preservati dal login; conferma del contratto; `leaseId` nullo, pratica non trovata, altro tenant o lookup non disponibile senza associazioni né esposizioni indebite | Fatto, tranne la conferma esplicita del contratto (CF-FIX-FU4) |
| Importazione | Dati delle tre sezioni sullo stesso `leaseId`, PDF consultabili, gestione di incompleti, conflitti e versioni senza perdita di dati | **Parziale:** solo il contratto, per la bozza «Importato» |
| Più pagine | Riuso del PDF unificato esistente; lettura sull'intero documento, senza conservare foto separate | Fatto |
| Permessi | Nessuna scrittura su casa o contratto altrui, nessun uso del solo `propertyId`, nessuna chiave CF al client | Fatto (CF-FIX2: `status` e `consume-gift` verificano l'organizzazione); `consume-gift` scrive con il solo permesso `lease.read` (CF-DOC-FU11) |
| Stato CF | Elenco multipratiche e dettaglio; tutti gli stati e gli errori v0.2; `Attested` intermedio e `Registered` unico finale positivo; snapshot condiviso di 15 minuti con rilettura breve dopo l'apertura, concorrenza e 429 rispettati | Fatto nel backend (CF-FIX2); mai provato contro l'endpoint vero di CF (manca la chiave di prova); il pulsante non sparisce (CF-DOC-FU4) |
| Interruttore | Spento: 404 su ogni endpoint CF, nessun pannello, «Parto da zero» chiuso; acceso: tutto il flusso; webhook Stripe, regalo e validatore di avvio non ne dipendono | Fatto |

Verifiche, secondo il regime del processo: in locale solo build, lint e typecheck; per ogni regola di denaro, dati, permessi o privacy (diritto lato server e consumo atomico dell'omaggio, bozza minima e retention, firma e scadenza del link, appartenenza di `practice`/`leaseId` all'organizzazione, lettura dei documenti) una prova minima scritta, che gira in CI insieme alle suite dei repository. Le suite esistenti di upload e download restano il controllo contro le regressioni. Nessun invio di pratiche reali o addebito live come prova. La prova a vista della lettura dei documenti su un PDF digitale e su una scansione vera spetta al PO (NL-DOCS).

Consegna: PR con il delta effettivo, le verifiche eseguite e la configurazione o i dati CF ancora necessari. Non dichiarare attivo in produzione ciò che è soltanto implementato o in PR. Restano da CasaZen verso CF (v0.2 §7): qualche link prodotto dal comando del server, da far verificare come i tre di esempio; la conferma del collaudo della pagina di caricamento con `practice` (fino ad allora il bottone di CF resta spento); il canale per la password delle chiavi.

## 8. Requisiti resi obsoleti o superati dalla revisione 11

- «Estendere il billing CasaZen esistente (`BillingCheckoutService`, `StripeBillingService`)» (revisione 10, sezione 3): superato dal servizio dedicato `ConcordatoFacileCheckoutService`.
- «Il supporto a `?practice=` non è ancora implementato» e «la pagina accetta solo `leaseId`» (sezione 4): superati, la pagina accetta entrambi.
- «Se nella query compaiono entrambi i parametri vale `practice`»: sostituito (sezione 4): vale il `leaseId` nominato.
- La pipeline di estrazione di ADR-009 (regole del ±50% sul canone, controllo del nome, risultato con i valori): sostituita per le bozze «Importato» dalla lettura locale con parser (sezione 5); `ExtractionResult` non contiene più valori personali.
- Le regole sbagliate degli ADR: ADR-008-R6 (l'elenco di CF è un array: è l'oggetto `{ leaseId, practices }`), ADR-008-R2 (variabile `CONCORDATOFACILE_API_KEY`: la variabile è `ConcordatoFacile__StatusApiKey`), ADR-008-R22 (senza chiave si torna un elenco vuoto: ogni lettura fallisce); l'emendamento del 24 ottobre di ADR-007 (R17–R23: link di marketing e QR) riguarda altri link e il suo «segreto condiviso» non vale per il link CF, il cui segreto lo genera CF. ADR-008-R11 e ADR-004-R6 sono rimaste non attuate fino al 25 ottobre e lo sono dal task CF-FIX2. Gli ADR non sono più un riferimento (banner in testa a ciascuno).
- Il LinkSecret «generato da noi», il `returnUrl` obbligatorio, il ricevitore di eventi di CF e il 422 `concordato_facile_not_configured` non sono mai stati requisiti validi: non reintrodurli.
- «Valutare `Origin = Imported` per la bozza del concordato» (revisione 10, coordinamento): **attuato** (CF-IMPORT, backend #615, frontend #350): la bozza «da zero» è `Imported`, marcata CF e protetta dalla cancellazione dopo un acquisto.

## 9. Lavoro restante, in ordine

Gli ID e i dettagli sono nel §13 del [documento di processo](../docs/integrations/concordatofacile/README.md).

1. Backend: **fatto il 25 ottobre da CF-FIX2 (#611)** il servizio dello stato registrato, la forma `{leaseId, practices}`, il controllo dell'organizzazione su `status` e `consume-gift`, la cache del lookup e l'attesa di 5 minuti dopo un errore. Restano un primo errore senza snapshot non limitato (CF-FIX2-FU1) e `consume-gift` che scrive con il solo permesso `lease.read` (CF-DOC-FU11).
2. Frontend: scheda del contratto che legge lo stato, nasconde il pulsante quando c'è una pratica e mostra l'avviso di lettura non riuscita (CF-DOC-FU4); riga su registro e bollo nella pagina del servizio (CF-DOC-FU5); conferma esplicita del contratto sul ritorno con `practice` (CF-FIX-FU4).
3. **Decisione del PO:** come il contratto fatto con CF diventa un contratto firmato in CasaZen (bozza `Created` contro importazione `Imported`, LR-06a-FU4, CF-DOC-FU10).
4. Lettura dei dati di attestazione e ricevuta di registrazione (CF-DOC-FU6).
5. Pagamento: pagamento ritardato che poi fallisce (CF-FIX-FU2) e due clic simultanei (CF-FIX-FU3).
6. Vendita dei piani a 24 mesi e regalo solo con sottoscrizione attiva (task BIENNIAL).
7. Con CF: chiave di stato di prova e casi campione, verifica dei link del server, collaudo della pagina di caricamento, canale per la password, inquadramento privacy R24, riconciliazione dei costi (69 o 89 €); poi il rilascio in produzione e l'interruttore (solo con il via libera esplicito dell'utente).
