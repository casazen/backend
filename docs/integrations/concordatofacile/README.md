# CasaZen e ConcordatoFacile — flusso del servizio

Aggiornamento: 21 ottobre 2026. Questo documento spiega ruoli, percorso dell’utente e accordi operativi. Il lavoro ancora da implementare è definito nell’unico [documento di requisiti per Helix](../../../Sessions/business-analysis-canone-concordato.md). Questi due documenti sostituiscono le precedenti proposte tecniche CF: vecchi OpenAPI, link firmati, webhook o corrispondenza non costituiscono ulteriori requisiti.

## 1. Dalla casa al modulo del partner

L’utente sceglie prima il tipo di contratto. Se sceglie canone concordato, CasaZen crea una bozza reale collegata alla casa e assegna subito il **leaseId**, senza chiedere prima parti, canone, date o APE. Quel riferimento identifica il contratto per tutto il percorso: non è il propertyId e non cambia quando arrivano i documenti definitivi.

Il vero contratto viene preparato da ConcordatoFacile, non generato prima una seconda volta da CasaZen. Il percorso libero conserva il proprio funzionamento; il modulo CF attuale accetta soltanto concordati.

CasaZen presenta il servizio a **119 € IVA compresa per pratica**, con il proprio ricarico già incluso. L’utente paga CasaZen prima di accedere al modulo, salvo:

- admin di piattaforma CasaZen: accesso diretto senza pagina di pagamento; non vale per un amministratore di una normale organizzazione;
- abbonamento CasaZen di due anni: una pratica omaggio, governata da flag nel database e associata una sola volta a un contratto dell’organizzazione abbonata.

La seconda pratica torna a pagamento dopo il consumo dell’omaggio. Riaprire la stessa pratica già sbloccata non richiede un altro acquisto. Il server conferma pagamento/diritto; il semplice ritorno dal checkout non basta.

La pagina servizio descrive prima del pagamento questi passaggi: reindirizzamento al modulo del partner; compilazione dei dati/documenti; gestione diretta via email con CF di integrazioni, revisione e firma; ricezione finale dei documenti; caricamento consigliato in CasaZen per la gestione centralizzata dei contratti. Registro e bollo eventualmente dovuti sono esclusi.

Dopo lo sblocco, CasaZen apre `https://app.concordatofacile.it` con i soli parametri `partner`, `leaseId`, `comune` e `provincia` in sigla, ad esempio `MB`, senza firma. CF conserva il leaseId nella pratica e lo restituisce nei riferimenti delle email e nell’endpoint di stato. Il codice partner deve ancora essere consegnato.

Poiché il modulo esterno è pubblico e il link non è firmato, prima dell’operatività va concordata con CF una verifica del diritto o una riconciliazione prima di lavorare le pratiche partner. Il controllo di pagamento CasaZen non rende segreto l’indirizzo del modulo.

## 2. Lavorazione e comunicazioni

CF segue raccolta dati, documenti, integrazioni, preparazione del contratto sul modello ministeriale, attestazione quando necessaria, firma e registrazione prevista dal servizio. Se l’utente ha già un contratto, CF cura l’attestazione secondo il servizio concordato.

Le comunicazioni operative e la consegna dei documenti avvengono **direttamente tra CF e utente**. CasaZen non vuole la prima email di pratica ricevuta, quella finale, copie o inoltri delle email dell’utente. Non sono richieste righe di stato nelle email.

CasaZen legge lo stato soltanto attraverso l’endpoint CF, dal proprio backend con una chiave fornita dal partner. L’interrogazione usa il leaseId; i dati confermati in risposta sono numero pratica, stato comprensivo della registrazione e data dell’ultimo passaggio, senza dati personali. L’utente vede lo stato nel contratto e può aggiornarlo; non è richiesto un motore di sincronizzazione in background.

URL, metodo, header, schema, stati e significati, formato/fuso della data, limiti e gestione di pratica assente/riaperta/multipla restano da concordare. Non esiste ancora una specifica esterna implementabile da presentare come definitiva. Le credenziali devono essere consegnate riservatamente e restare sul server CasaZen.

## 3. Email finale e ritorno a CasaZen

Nelle email all’utente con documenti da conservare CF inserisce il bottone «Carica i dati in CasaZen»; nella conferma del modulo, di solo testo, usa il link scritto per esteso. Il partner ha precisato che i documenti definitivi sono inviati dopo la firma.

Dominio e URL fissati:

`https://casazen.space/app/long-rent/documents/upload?leaseId=<leaseId>`

CF sostituisce il segnaposto con l’identificativo della bozza ricevuto all’apertura del modulo. È una pagina web di atterraggio, non l’endpoint di stato né un’API che trasferisce file da CF. Non contiene chiavi, propertyId o dati personali aggiuntivi. Il leaseId non è una credenziale.

Dopo login, con percorso e parametro preservati, CasaZen verifica l’accesso al contratto e mostra casa e riferimento. L’utente carica i documenti ricevuti, scegliendo contratto, attestazione, registrazione o altro. Non serve un secondo pagamento. Un riferimento errato o non autorizzato non consente accessi o associazioni automatiche.

## 4. Conservazione e consultazione dei documenti

Le relazioni sono distinte:

| Raccolta | Contenuto e accesso |
|---|---|
| Documenti della casa | APE, planimetria e documenti dell’immobile |
| Contratti della casa | Contratti collegati alla casa, ciascuno con il proprio leaseId |
| Documenti del contratto | Contratto definitivo, attestazione, registrazione e altri allegati di quel leaseId |

Dalla casa si apre un contratto e si consulta la sua sezione Documenti. Gli allegati del contratto non sono duplicati nei documenti della casa. Lo storage è privato e il download richiede accesso autorizzato al contratto/documento.

Si può caricare un PDF oppure più foto JPEG/PNG dello stesso documento. Le foto possono essere aggiunte in più selezioni, visualizzate, ruotate, riordinate o rimosse; l’utente conferma che tutte le pagine siano presenti. CasaZen le riunisce in **un solo PDF scaricabile**: le fotografie non vengono conservate separatamente. Un PDF già esistente è conservato senza modificarne i byte, comprese eventuali firme digitali.

La funzione multipagina è condivisa con i documenti della casa ed è indipendente dal canone concordato. Il nuovo caricamento ammette fino a 12 foto, 20 MB per foto e 40 MB totali in ingresso; il PDF risultante deve rispettare 10 MB per i documenti della casa e 20 MB per gli allegati del contratto. Se supera il limite, non viene salvato un documento parziale.

Per l’APE, che rimane nella casa, CasaZen esegue estrazione/OCR sull’intero attestato, anche se codice, classe, indice EPgl, indirizzo, certificatore e date sono distribuiti su pagine diverse. Se dati fondamentali non sono leggibili, mancano o le date non sono valide, segnala cosa correggere prima di salvare. Il controllo non certifica autenticità nel registro regionale o validità legale in ogni circostanza. Non serve completare questo controllo per ottenere il leaseId della bozza minima.

Il successivo lavoro Helix acquisirà invece i dati dei documenti contrattuali: contratto definitivo → dati del contratto; attestazione → sezione Attestazione; ricevuta → sezione Registrazione. Tutto rimane sullo stesso leaseId. Valori incerti o in conflitto richiedono revisione; i PDF rimangono consultabili anche se l’estrazione fallisce. L’OCR APE non equivale già a questa importazione contrattuale.

Caricamento, attestazione e registrazione sono eventi distinti. Un file caricato o una ricevuta di sola trasmissione non prova che il contratto sia registrato e non attiva un secondo invio fiscale.

## 5. Accordi economici e adempimenti

Il pagamento dell’utente a CasaZen (119 € IVA compresa) è distinto dal saldo al partner. L’utente **non paga CF**: CasaZen paga pratica per pratica dopo l’invio dei firmati.

CF ha indicato importi finali di **69 e 89 € senza IVA**, con ricevuta al locatore per quota associativa e contributo, quale operazione non commerciale esente IVA ai sensi dell’art. 4 D.P.R. 633/72, secondo la dichiarazione del fornitore. L’abbinamento preciso dei servizi ai due prezzi, documenti finali, modalità di pagamento/riconciliazione e condizioni di annullamento o pratica non attestabile restano da confermare. Non applicare questa esenzione alla vendita CasaZen.

Secondo CF, l’associazione presenta la registrazione telematica come intermediario a nome del locatore, senza delega firmata dalle parti; la cedolare viene scelta in registrazione. La stessa pratica non deve essere inviata anche tramite Openapi. I percorsi alternativi esistenti mantengono i propri requisiti.

Registro e bollo sono esclusi dal prezzo: con cedolare non sono dovuti; altrimenti vengono addebitati sul conto del locatore, con IBAN raccolto da CF nel proprio canale, mai inserito nei link. I tempi indicati sono pochi giorni dopo firmati e pagamento, entro il termine dalla stipula richiamato dal partner; non è uno SLA garantito di 48 ore.

CF segue anche libero 4+4 e transitori per email finché il modulo accetta solo concordati; non sono da implementare in questo percorso. Ha indicato 20 € per la sola registrazione di un contratto già pronto, con condizioni da completare. Per transitori soggetti ad accordo territoriale verificare quando occorre attestazione. La comunicazione IMU al Comune resta separata dalla registrazione e non è automatizzata da questa integrazione.

## 6. Disponibilità e coordinamento con il partner

Le PR [frontend #315](https://github.com/casazen/frontend/pull/315) e [backend #561](https://github.com/casazen/backend/pull/561) implementano caricamento, archivio, PDF multipagina e controllo APE. Non attestano rilascio in produzione. Scelta iniziale/bozza CF, offerta e pagamento/omaggio, apertura partner, importazione dei dati contrattuali e lettura stato restano descritti nei requisiti Helix.

Il link nelle email va attivato dopo conferma esplicita CasaZen, successiva al rilascio e al collaudo: apertura del link, eventuale redirect fra dominio e www, login, leaseId preservato, contratto corretto, upload, riapertura e download. Il solo HTTP 200 della shell applicativa non prova il funzionamento della pagina.

Per la veste personalizzata il partner richiede marchio e dominio; colori su fondo chiaro: terracotta `#e2622d` e inchiostro `#1f3a3d`. Il lockup da consegnare deve avere il nome in tracciati, senza dipendere da Figtree installato. La data della veste è da comunicare da CF dopo ricezione dei materiali: non blocca tecnicamente la gestione di una pratica pilota concordata.

Prima dell’operatività completare codice partner e contratto dell’endpoint, contenuti dei pacchetti, modalità di riconciliazione dei diritti/pagamenti e riferimenti operativi dell’associazione, assistenza, ruoli privacy e tempi di conservazione. Questi punti di coordinamento non richiedono nuovi documenti o funzionalità non previste nei requisiti Helix.
