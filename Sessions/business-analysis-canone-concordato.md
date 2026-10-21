# Requisiti per Helix — integrazione ConcordatoFacile

Revisione 6 · 21 ottobre 2026. Questo è l’unico documento di requisiti da fornire a Helix. Descrive il lavoro ancora necessario per il servizio CF; accordi e percorso dell’utente sono nella [documentazione del flusso](../docs/integrations/concordatofacile/README.md). Sostituisce le precedenti proposte di integrazione, senza richiedere di sommarne le funzionalità.

## 1. Base da riusare e lavoro restante

Le PR [frontend #315](https://github.com/casazen/frontend/pull/315) e [backend #561](https://github.com/casazen/backend/pull/561), verso `develop`, realizzano già il caricamento e la conservazione. Prima di iniziare verificare che siano integrate; una PR aperta non dimostra disponibilità in produzione. Leggere AGENTS.md dei repository e lavorare sul codice corrente.

| Già implementato nelle PR di codice: riusare | Resta a Helix |
|---|---|
| Pagina `/app/long-rent/documents/upload?leaseId=…`, login con ritorno allo stesso contratto | Tipo di contratto come prima scelta; bozza minima concordato e collegamento al servizio CF |
| Allegati `LeaseDocument`, API autorizzate elenco/upload/download, storage privato e isolamento OrgId | Pagina offerta, pagamento singolo 119 € IVA compresa, flag pratica omaggio biennale e bypass admin CasaZen |
| Sezione Documenti del contratto, raggiungibile anche aprendo il contratto dalla casa | Apertura del modulo CF con i quattro parametri concordati, dopo verifica server del diritto |
| Un PDF esistente oppure più foto JPEG/PNG riordinate/ruotate e unite in un solo PDF scaricabile; le foto non sono conservate separatamente | Estrazione dai documenti del contratto e popolamento delle rispettive sezioni dello stesso leaseId |
| OCR e controlli fondamentali APE, nel distinto archivio della casa | Lettura dello stato CF dal backend, quando il fornitore consegna specifica e credenziali |

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
4. Doppi click e retry della stessa azione riusano la bozza salvata; rimane possibile creare intenzionalmente un nuovo contratto sulla stessa casa.
5. Da quella bozza si accede all’offerta/pagamento o all’omaggio. L’admin CasaZen segue l’accesso diretto descritto sotto. Il percorso libero mantiene il proprio funzionamento: non inviarlo al modulo riservato ai concordati.

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
| Pratica già pagata o omaggio già associato a questo leaseId | «Continua sul modulo del partner», senza nuovo addebito/consumo |
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

Il modulo CF è pubblico e il link concordato è senza firma: il controllo CasaZen protegge la propria offerta, ma non impedisce la ricostruzione dell’URL esterno. Prima dell’operatività concordare con CF verifica del diritto o riconciliazione prima di lavorare pratiche partner. Non inventare firme o parametri aggiuntivi.

## 4. Apertura CF

Aprire `https://app.concordatofacile.it` con i soli parametri:

| Parametro | Origine |
|---|---|
| `partner` | Codice assegnato da CF, configurabile e ancora da ricevere |
| `leaseId` | UUID reale della bozza salvata |
| `comune` | Nome del comune della casa |
| `provincia` | Sigla provinciale, per esempio `MB` |

Usare URL/URLSearchParams per accenti e spazi. Ricavare i dati territoriali dalla sorgente esistente, senza dedurre arbitrariamente la provincia dall’indirizzo. Dati o codice partner assenti impediscono l’apertura con spiegazione utile. Nessun `signature`, `expiresAt`, `theme`, `returnUrl` o codice ISTAT richiesto al modulo.

Il ritorno già realizzato nelle PR di codice resta:

`https://casazen.space/app/long-rent/documents/upload?leaseId=<leaseId>`

È una pagina web, non l’endpoint di stato o un’API di importazione da CF. Il bottone nelle email all’utente contiene lo stesso leaseId; la conferma testuale usa il link per esteso. CasaZen non riceve email di pratica, finali o copie delle comunicazioni: per lo stato utilizza soltanto l’endpoint.

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

## 6. Lettura dello stato CF

CF ha confermato interrogazione per leaseId con chiave, numero pratica, stato inclusa registrazione e data dell’ultimo passaggio, senza dati personali. Mancano URL/metodo, header, schema JSON, enum e significati, formato/fuso della data, limiti, pratica assente e regole per riapertura/multiple pratiche. Non trattare vecchi OpenAPI, esempi o placeholder come contratto effettivo.

Quando disponibili:

- Client HTTP tipizzato nel backend con URL/chiave configurati solo sul server; mai credenziali nel browser, link o log.
- Lettura interna autorizzata nel contesto del contratto, ad esempio `GET /api/leases/{leaseId}/concordatofacile/status`. È una proposta CasaZen, non l’URL del fornitore né un endpoint già presente.
- Lettura all’apertura del contratto e su «Aggiorna stato». Nessun nuovo job, webhook CF, polling continuo o archivio storico nel minimo intervento; aggiungere cache/refresh solo se giustificati dai limiti CF.
- Mostrare numero pratica, stato documentato e data. Gestire nuovi valori senza inventare un completamento; distinguere pratica assente, configurazione mancante e indisponibilità. Timeout/429 non equivalgono a «nessuna pratica» e non cancellano l’ultimo risultato valido. Timeout, retry limitati e Retry-After secondo i pattern esistenti.
- Non applicare risposte obsolete o confondere stato CF, elaborazione documenti, attestazione e registrazione. Il popolamento dei campi rimane governato dall’importazione verificata.

Finché CF non consegna i dettagli, realizzare il resto senza simulare stati reali in produzione. La verifica live di questa parte resta esplicitamente dipendente dal fornitore.

## 7. Accettazione e consegna

| Prova | Risultato |
|---|---|
| Concordato scelto come primo passo | Bozza reale con soli casa/tipo; leaseId stabile anche dopo importazione; nessun APE/dato definitivo richiesto prima |
| Offerta e checkout | Cinque passaggi comprensibili, 119 € IVA inclusa, conferma solo server, ritorno allo stesso contratto |
| Admin piattaforma / admin organizzazione | Solo il primo salta il pagamento; entrambi rispettano l’accesso alla risorsa |
| Biennale, eventi duplicati e accessi concorrenti | Un omaggio assegnato una volta, consumabile da un solo contratto, riapertura senza addebito |
| CF | Quattro parametri codificati, dati mancanti bloccanti, nessun invio del libero al modulo concordati |
| Importazione | Dati delle tre sezioni sullo stesso leaseId, PDF consultabili, gestione incompleti/conflitti/versioni senza perdita di dati |
| Più pagine | Riuso del PDF unificato esistente; estrazione sull’intero documento, senza conservare foto separate |
| Permessi | Nessuna scrittura su casa/contratto altrui, nessun uso del solo propertyId, nessuna chiave CF al client |
| Stato CF | Numero/stato/data corretti; errori e pratica assente distinti; nessun falso completamento |

Eseguire test pertinenti del diritto lato server, Stripe test, consumo atomico dell’omaggio, bozza minima, importazione con PDF digitale/scansione multipagina e risposte CF documentate; verificare il riuso senza regressioni di upload/download. Seguire build/lint/typecheck e controlli richiesti dai repository. Nessun invio di pratiche reali o addebito live come prova.

Consegna: PR con delta effettivo, verifiche eseguite e configurazione/dati CF ancora necessari. Non dichiarare attivo in produzione ciò che è soltanto implementato o in PR.
