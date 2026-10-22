# CasaZen e ConcordatoFacile — flusso del servizio

Aggiornamento: 22 ottobre 2026. Questo documento è descrittivo: spiega ruoli, percorso dell'utente e accordi operativi, e raccoglie le risposte per CF. I requisiti di sviluppo sono soltanto nell'unico [documento per Helix](../../../Sessions/business-analysis-canone-concordato.md) e, se i testi differiscono, vale quello. La fonte tecnica corrente è «ConcordatoFacile — specifica endpoint di stato v0.1», ottobre 2026, ancora bozza per commenti. La precedente «01-technical-specification», v1.3 del 13 ottobre, vale soltanto per firma/vettore del link (§2) e convenzioni richiamate dalla nuova specifica: webhook, eventi finali e consegna automatica di file della v1.3 non fanno parte di questo flusso.

**Documenti di questa cartella**

| Documento | Stato |
|---|---|
| `README.md` (questo) | Descrittivo: flusso, ruoli, accordi, risposte per CF |
| [`Sessions/business-analysis-canone-concordato.md`](../../../Sessions/business-analysis-canone-concordato.md) | Unico documento di requisiti per lo sviluppo (Helix) |
| `01-specifica-integrazione.md` e `.pdf`, `02-openapi.yaml` | Superati: modello a webhook e `caseId` (v1.0 del 13 ottobre); restano per storico |
| `da-inviare/` (`01-technical-specification` v1.3 e `02-openapi.yaml`) | Superati, salvo il §2 della v1.3 (firma e vettore del link) e le convenzioni richiamate dalla v0.1: webhook, `caseId` ed eventi finali non si implementano |

## 1. Dalla casa al modulo del partner

L'utente sceglie prima il tipo di contratto. Se sceglie canone concordato, CasaZen crea una bozza reale collegata alla casa e assegna subito il **leaseId**, senza chiedere prima parti, canone, date o APE. Quel riferimento identifica il contratto per tutto il percorso: non è il propertyId e non cambia quando arrivano i documenti definitivi. Ogni nuovo contratto, anche sulla stessa casa, ha il proprio leaseId.

Il contratto viene preparato da ConcordatoFacile e poi importato, senza generarlo prima una seconda volta in CasaZen. Il percorso libero conserva il proprio funzionamento; il modulo CF attuale accetta soltanto concordati.

CasaZen presenta il servizio a **119 € IVA compresa per pratica**, con il proprio ricarico incluso. L'utente paga CasaZen prima del modulo, salvo:

- admin di piattaforma CasaZen: accesso diretto senza pagina di pagamento; non vale per l'amministratore di una normale organizzazione;
- abbonamento CasaZen di due anni: una pratica omaggio, governata da flag nel database e associata una sola volta a un contratto dell'organizzazione abbonata.

Una pratica per contratto: una seconda pratica richiede un nuovo contratto e torna a pagamento, anche dopo il consumo dell'omaggio (che è uno solo). Rientrare nel modulo prima dell'invio non richiede un altro acquisto; dopo l'invio il modulo non si riapre. Il server conferma il diritto; il ritorno dal checkout non basta.

Prima del pagamento la pagina spiega: reindirizzamento al modulo partner; compilazione dei dati/documenti; gestione diretta via email con CF di integrazioni, revisione e firma; ricezione finale dei documenti; caricamento consigliato in CasaZen per la gestione centralizzata dei contratti. Registro e bollo eventualmente dovuti sono esclusi.

Nella scheda del contratto c'è un solo punto d'ingresso, che cambia con lo stato: «Procedi con la creazione del contratto» (pagamento o omaggio, poi il modulo), «Accedi alla tua pratica» fino all'invio (la bozza del modulo riprende solo sullo stesso dispositivo e browser) e, dopo l'invio, al posto del pulsante la sezione «Stato della pratica».

### Link firmato al clic

Dopo lo sblocco, il **server** CasaZen genera un link a `https://app.concordatofacile.it/` con `partner=casazen`, leaseId, scadenza UTC a 15 minuti e firma HMAC-SHA256; comune e provincia in sigla precompilano la località. La firma usa il testo esatto `leaseId.expiresAt`; tutti i valori sono poi codificati nell'URL. Segreto e diritto al servizio restano sul server. CF non usa i vecchi parametri `comuneIstatCode`, `returnUrl` o `theme`.

Questo aggiorna il precedente accordo provvisorio del link senza firma. CF verifica firma e scadenza, conserva il leaseId nelle bozze e nelle riprese e lo collega alla compilazione. Se il link è assente, errato o scaduto, il modulo resta compilabile ma la pratica nasce **senza leaseId**: CF deve collegarla manualmente. La firma non rende il modulo privato e non prova da sola il pagamento della singola pratica non collegata.

Con il link CasaZen, CF non mostra i propri prezzi e ha confermato che per CasaZen esiste **solo il servizio completo** (attestazione e registrazione): nessuna scelta nel modulo e nessun campo servizio da richiedere all'endpoint. Il saldo del partner rimane a carico CasaZen e non genera un altro pagamento dell'utente.

Prima dell'invio, nello stesso browser/dispositivo, un nuovo link può riprendere la bozza. Dopo l'invio oppure su altro dispositivo apre invece una compilazione nuova: un secondo invio può produrre una seconda pratica sullo stesso contratto. CasaZen toglie il pulsante appena l'elenco CF mostra una pratica, in qualunque stato, e mostra lo stato; per una nuova pratica serve un nuovo contratto. Dopo l'invio, correzioni e firma passano per email con CF.

## 2. Lavorazione, stati e comunicazioni

CF segue raccolta dei dati/documenti, integrazioni, contratto sul modello ministeriale, firma, attestazione quando necessaria e registrazione prevista dal servizio. Se l'utente ha già un contratto, CF cura l'attestazione secondo il servizio concordato.

Le comunicazioni operative e la consegna dei documenti avvengono **direttamente tra CF e utente**. CasaZen non vuole prima email di pratica ricevuta, email finale, copie o inoltri delle comunicazioni all'utente. Non sono richieste righe di stato nelle email.

CasaZen legge gli stati dal proprio backend su `https://stato.concordatofacile.it`, con una chiave Bearer per partner e ambiente:

- `GET /v1/leases/{leaseId}/practices`: fino a 20 pratiche del contratto, dalla più recente; 200 con elenco vuoto se non esistono, non sono collegate o il contratto non è del partner;
- `GET /v1/practices/{practiceId}`: singola pratica, anche con leaseId nullo; stesso 404 per riferimento inesistente, di altro partner o altro ambiente.

La risposta contiene numero pratica `P-` più 16 caratteri esadecimali, stato, date UTC dell'invio/ultimo passaggio, comune, indicazione dell'eventuale cambio di comune e motivo di chiusura. Non contiene documenti o dati delle parti. Il numero pratica sostituisce il precedente caseId delle vecchie proposte.

| Fase | Stati CF e azione dell'utente |
|---|---|
| Ricezione e lavorazione | `Received`, `InProgress`, `SignedReceived`, `SentToAssociation` |
| Azione richiesta | `AwaitingUser`, `DraftSent`: leggere e gestire le email del partner |
| Attestazione | `Attested`: attestazione rilasciata, segue la registrazione (non finale) |
| Registrazione | `Registered`: finale positivo |
| Chiusura senza attestazione | `ClosedWithoutAttestation`: motivo duplicate, withdrawn, not_compliant oppure other |
| Sospensione | `Suspended`: può riprendere, non è una chiusura definitiva |

Gli stati possono ripetersi e alternarsi; la data dell'ultimo passaggio non retrocede. I doppioni restano nell'elenco e CF li chiude con il motivo duplicate. Non nascondere le altre pratiche prendendo solo la prima e non considerare «attestata» equivalente a «registrata».

### Frequenza proposta da CasaZen

La lettura avviene quando l'utente apre la **scheda del contratto**, riusando uno snapshot applicativo privato valido per **15 minuti**. Schede e utenti della stessa organizzazione condividono il risultato: non più di una interrogazione elenco ogni 15 minuti per contratto. Non è previsto polling in background (scelta nostra: per le pratiche aperte CF consiglierebbe al massimo una lettura ogni 15 minuti); anche l'aggiornamento manuale rispetta il periodo. Unica deroga: dopo l'apertura del modulo, finché la pratica non compare, la scheda può rileggere ogni 5 minuti per un'ora, così il pulsante sparisce poco dopo l'invio.

Questo significa al massimo **4 richieste/ora per contratto consultato continuativamente**, zero per i contratti non consultati, fino a 12/ora per un contratto solo nell'ora dopo l'apertura del modulo, più gli eventuali lookup per numero pratica dagli arrivi email, anch'essi accorpati per 15 minuti. Senza il numero di pratiche attive non è ancora possibile indicare un totale partner: il limite complessivo va dimensionato con CF su questa formula, senza promettere volumi inventati. Come ordine di grandezza, 50 contratti consultati senza sosta sono al massimo 200 richieste/ora, circa 3 al minuto.

Il `Cache-Control: no-store` di CF resta rispettato per cache HTTP/browser/proxy: lo snapshot è uno stato applicativo autorizzato, non una risposta pubblicamente cacheabile. Un guasto non diventa «nessuna pratica» e non cancella l'ultimo risultato valido; la UI distingue ultimo passaggio del partner, ultima verifica riuscita e temporanea indisponibilità. I 429 rispettano Retry-After. La pratica può comparire con ritardo: CF ritenta la pubblicazione ogni 15 minuti.

## 3. Email finale e ritorno a CasaZen

Nelle email all'utente con documenti da conservare CF inserisce «Carica i dati in CasaZen»; nella conferma testuale del modulo usa il link per esteso. I documenti definitivi sono inviati dopo la firma. Il bottone apre una pagina web: non trasferisce automaticamente file e non è l'endpoint di stato.

Il percorso CasaZen resta:

`https://casazen.space/app/long-rent/documents/upload`

La pagina **già sviluppata** (in develop, non ancora in produzione) accetta `?leaseId=<uuid>`. Il bottone di CF porterà invece `?practice=<practiceId>`, come nella v0.1: Helix deve aggiungere questa risoluzione dal backend e la selezione/conferma esplicita del contratto prima dell'upload. Non confondere i due parametri e non dichiarare il nuovo formato già disponibile.

**Parametro comunicato a CF:** solo `?practice=P-…`. Il leaseId non serve nel link: arriva dal lookup della pratica o dalla scelta dell'utente, ed è comunque indispensabile per salvare gli allegati. I vecchi link con solo leaseId rimangono supportati. Nessun segreto, propertyId o dato delle parti nel link; gli identificativi non sono credenziali.

Dopo login, con percorso e parametri preservati, CasaZen verifica accesso e appartenenza all'organizzazione. Il leaseId restituito dalla pratica aiuta a proporre il contratto, ma **non prova che i dati compilati o i file appartengano a quel contratto**. L'utente deve scegliere/confermare casa e contratto prima di caricare; può correggere la destinazione fra i propri contratti. Il cambio comune segnalato da CF è un avvertimento parziale, non una validazione di tutto il contenuto.

Con pratica non collegata, di un'altra organizzazione o lookup non disponibile, CasaZen non collega automaticamente la pratica, non mostra dati di altri tenant e non fattura dal solo abbinamento. L'utente può conservare i documenti su un proprio contratto autorizzato; CF deve correggere manualmente il collegamento nel proprio sistema. Questa scelta non cambia il leaseId della pratica remota. Non è richiesto un secondo acquisto per upload o consultazione.

## 4. Conservazione e consultazione dei documenti

| Raccolta | Contenuto e accesso |
|---|---|
| Documenti della casa | APE, planimetria e documenti propri dell'immobile |
| Contratti della casa | Ogni contratto con il proprio leaseId |
| Documenti del contratto | Contratto definitivo, attestazione, registrazione e altri allegati di quel leaseId |

Dalla casa si apre il contratto e la sua sezione Documenti. Gli allegati contrattuali non sono duplicati nei documenti della casa. Storage privato e download richiedono autorizzazione sul contratto e sul documento.

Si può caricare un PDF oppure più foto JPEG/PNG dello stesso documento. Le foto possono essere aggiunte in più selezioni, visualizzate, ruotate, riordinate o rimosse; l'utente conferma che tutte le pagine siano presenti. CasaZen conserva **un solo PDF scaricabile**, senza originali fotografici separati. Un PDF preesistente conserva i propri byte, incluse eventuali firme digitali.

La funzione multipagina è condivisa con i documenti della casa ed è indipendente dal concordato: fino a 12 foto, 20 MB per foto e 40 MB totali in ingresso; il PDF risultante deve rispettare 10 MB per i documenti della casa e 20 MB per gli allegati del contratto. Nessun salvataggio parziale se il limite viene superato.

Per l'APE, che rimane nella casa, CasaZen estrae testo/OCR sull'intero attestato: codice, classe, EPgl, indirizzo, certificatore e date possono trovarsi su pagine diverse. Dati fondamentali mancanti/illeggibili o date non valide vengono segnalati prima del salvataggio. Non è una certificazione di autenticità nel registro regionale e non blocca l'assegnazione del leaseId alla bozza minima.

Il lavoro residuo Helix acquisirà i dati dei documenti contrattuali: contratto definitivo → dati del contratto; attestazione → sezione Attestazione; ricevuta → sezione Registrazione. Tutto resta sul leaseId selezionato. Valori incerti o discordanti richiedono revisione; PDF consultabili anche se l'estrazione fallisce. L'OCR APE esistente non equivale a questa importazione.

Caricamento, attestazione e registrazione sono eventi distinti. Il solo upload o una ricevuta di trasmissione non prova la registrazione; lo stato CF Registered non significa che la ricevuta sia già archiviata in CasaZen. Nessun secondo invio fiscale automatico.

## 5. Accordi economici e adempimenti

Il pagamento dell'utente a CasaZen (119 € IVA compresa) è distinto dal saldo al partner. L'utente **non paga CF**: CasaZen paga pratica per pratica dopo l'invio dei firmati.

CF ha indicato importi finali **69 e 89 € senza IVA**, con ricevuta al locatore per quota associativa e contributo quale operazione non commerciale esente IVA ai sensi dell'art. 4 D.P.R. 633/72, secondo la dichiarazione del fornitore. Per CasaZen vale solo il servizio completo: la tariffa da riconciliare è quella del completo (importo da confermare con CF, che ha indicato 69 e 89 €) e non serve alcun campo servizio nell'endpoint; resta da concordare la modalità di riconciliazione. Condizioni di annullamento o pratica non attestabile sono ancora da confermare. Non applicare questa esenzione alla vendita CasaZen.

Secondo CF, l'associazione registra telematicamente come intermediario a nome del locatore, senza delega firmata dalle parti; la cedolare si sceglie in registrazione. Non registrare la stessa pratica anche tramite Openapi. I percorsi alternativi esistenti mantengono i propri requisiti.

Registro e bollo sono esclusi: con cedolare non sono dovuti; altrimenti addebito sul conto del locatore e IBAN raccolto da CF nel proprio canale, mai nei link. I tempi indicati sono pochi giorni dopo firmati e pagamento, entro il termine dalla stipula richiamato dal partner: non uno SLA garantito di 48 ore.

CF segue anche libero 4+4 e transitori per email finché il modulo accetta solo concordati; sono fuori da questo percorso. Ha indicato 20 € per la sola registrazione di un contratto già pronto, con condizioni da completare. Per transitori soggetti ad accordo verificare quando occorre attestazione. La comunicazione IMU al Comune resta separata e non automatizzata.

## 6. Coordinamento e risposte per CF

Le PR [frontend #315](https://github.com/casazen/frontend/pull/315) e [backend #561](https://github.com/casazen/backend/pull/561) sono integrate in develop: upload/archivio, PDF multipagina e controllo APE. Le PR correttive [frontend #316](https://github.com/casazen/frontend/pull/316) e [backend #562](https://github.com/casazen/backend/pull/562), integrate in develop il 22 ottobre, riguardano CI, ritorno dal login e date APE. Questa integrazione non attesta il rilascio in produzione. Bozza CF/pagamento/omaggio, firma del link, stato, ritorno practice/conferma ed estrazione contrattuale restano nel delta Helix.

Le risposte ai punti della v0.1 (§7 e §1) sono:

1. **Firma.** Confermiamo generazione sul server CasaZen al clic, scadenza 15 minuti, leaseId minuscolo, UTC al secondo con Z, HMAC-SHA256 sui valori decodificati e query URL-encoded come §2. È un requisito da completare nella nostra implementazione, non una funzione già attiva. Come proposto dalla v0.1 §3, inviamo tre link di esempio generati con il segreto di prova (quello del vettore v1.3 §2) da uno script di riferimento, perché il codice CasaZen non esiste ancora; il primo ricostruisce carattere per carattere il link della v0.1. Quando il comando sarà pronto, vi invieremo link prodotti dal nostro server.
2. **Frequenza.** Interroghiamo all'apertura della scheda del contratto con riuso privato di 15 minuti, condiviso fra utenti/schede; massimo quattro letture elenco/ora per contratto consultato, senza polling di fondo (non adottiamo il polling che consigliate per le pratiche aperte). Unica deroga: dopo l'apertura del modulo, finché la pratica non compare, una lettura ogni 5 minuti per un'ora, quindi al massimo 12 letture/ora per quel contratto in quell'ora. I lookup della pratica dai link email rispettano lo stesso intervallo di 15 minuti. Come ordine di grandezza, 50 contratti consultati senza sosta sono al massimo 200 richieste/ora. Per il limite aggregato partner proponiamo di partire da questo profilo e concordare la soglia sulla base delle pratiche effettive.
3. **Bottone.** Il percorso è `https://casazen.space/app/long-rent/documents/upload` e il parametro è `practice` (per esempio `?practice=P-0123456789abcdef`), come nella vostra specifica: il leaseId non serve nel link, lo ricaviamo dalla vostra chiamata sulla pratica e l'utente conferma casa e contratto prima del caricamento. La pagina oggi accetta soltanto `?leaseId=<uuid>` e non è ancora in produzione: il supporto di practice richiede l'adattamento sopra descritto. Non accendete il bottone prima della nostra conferma del collaudo; vi confermeremo separatamente la disponibilità effettiva.
4. **Chiavi.** Consegnate le chiavi dell'endpoint (prova e produzione) e il segreto di firma di produzione direttamente a Luca La Malfa all'indirizzo **luca.lamal@hotmail.it**, con il Bitwarden Send a una sola apertura previsto dalla v0.1 e la password su un altro canale da concordare. Per la firma in prova usiamo il segreto del vettore §2, come indicato dalla v0.1; se ne avete uno diverso per la prova, consegnatelo allo stesso modo. Non inviate le chiavi in chiaro nella stessa email.
5. **Servizio.** Preso atto che per CasaZen esiste solo il servizio completo: non serve il campo servizio nell'endpoint. Leggiamo `Attested` come passaggio intermedio e `Registered` come finale positivo. Se attivaste altri servizi per CasaZen, avvisateci e aggiungete prima il campo.

Da chiudere con CF: URL o selezione dell'ambiente di prova e relativi casi campione; soglia complessiva delle chiamate; riconciliazione delle pratiche senza leaseId e costi; inquadramento privacy v1.3 R24, riferimenti dell'associazione, assistenza e conservazione prima delle pratiche reali. Non riproporre webhook o i dati attestati della vecchia v1.3 come obblighi del nuovo endpoint.

Il bottone si attiva dopo conferma esplicita CasaZen successiva a rilascio e collaudo: redirect dominio/www, login, parametri preservati, pratica risolta, contratto autorizzato e confermato, upload, riapertura e download. La raggiungibilità HTTP della shell non prova questa sequenza. Nessuna data di produzione è deducibile dal solo merge in develop.

Per la veste personalizzata: terracotta `#e2622d` e inchiostro `#1f3a3d` su fondo chiaro; lockup con nome in tracciati, senza dipendere da Figtree installato. CF comunicherà la data della veste dopo ricezione di marchio e dominio; questo non blocca una pratica pilota esplicitamente concordata.
