# CasaZen — riscontro a ConcordatoFacile

21 ottobre 2026 · Oggetto: avvio collaborazione e integrazione del flusso

Buongiorno,

grazie per le conferme. Per CasaZen il flusso proposto va bene. Riassumiamo le nostre risposte e i dettagli utili per procedere.

## Modulo e collegamento alla pratica

Confermiamo il link al vostro modulo con i soli parametri partner, leaseId, comune e provincia, senza firma. Per la provincia invieremo la sigla, per esempio MB. Vi chiediamo il codice partner definitivo e di conservare il leaseId nelle email e nell'endpoint fino alla chiusura della pratica.

## Email e bottone CasaZen

Alla vostra domanda: sì, desideriamo mantenere sia la prima email di pratica ricevuta sia quella finale con i documenti. L'endpoint coprirà l'aggiornamento degli stati, quindi non servono più le righe di stato nelle email. Restano necessarie le comunicazioni all'utente per eventuali integrazioni, revisione del contratto e firma.

Confermiamo il bottone «Carica i dati in CasaZen» nelle email con documenti da conservare e il link scritto per esteso nella conferma testuale del modulo. Concordiamo un dominio fisso e la presenza del leaseId nel collegamento di ritorno. Il ritorno servirà ad aprire CasaZen e consentire all'utente di caricare/confermare i documenti; non presuppone un trasferimento automatico dei file dal vostro sistema.

Il dominio definitivo è https://casazen.space. Il percorso di atterraggio che definiamo per il bottone e per il link testuale è:

`https://casazen.space/app/long-rent/documents/upload?leaseId=<leaseId>`

Sostituite `<leaseId>` con l'identificativo CasaZen ricevuto all'apertura del modulo. Esempio completo:

`https://casazen.space/app/long-rent/documents/upload?leaseId=0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11`

La pagina sarà il punto di caricamento dei documenti sulla casa associata al contratto: dopo l'accesso, CasaZen ricaverà la casa dal leaseId e mostrerà casa e contratto per conferma. Il collegamento sarà mantenuto anche durante il login. Non occorre inviare un propertyId né dati personali nell'URL. In caso di identificativo assente, non valido o non accessibile, non verrà eseguita alcuna associazione automatica. Il percorso è definito per l'integrazione e deve essere implementato e verificato prima di attivare i link nelle email.

## Endpoint di stato

Confermiamo la consultazione per leaseId, con chiave fornita da voi, numero pratica, stato comprensivo della registrazione e data dell'ultimo passaggio, senza dati personali. La chiave verrà usata soltanto dal nostro server.

Per collegarlo ci occorrono URL e metodo, nome dell'header per la chiave, un esempio di risposta, elenco e significato degli stati, formato e fuso orario della data, limiti/frequenza di consultazione e comportamento se il leaseId non esiste o ha più pratiche. La chiave va trasmessa attraverso un canale riservato, separatamente da questo documento. Vi chiediamo anche un caso di prova e la regola per riaprire una pratica sullo stesso leaseId.

## Contratto, registrazione e imposte

Prendiamo atto che preparate il contratto sul modello ministeriale, incluso nel prezzo, e che per un contratto già predisposto curate l'attestazione.

Prendiamo atto che la registrazione viene presentata telematicamente dall'associazione come intermediario a nome del locatore, senza necessità di delega firmata delle parti, e che la cedolare secca viene scelta in registrazione. Non aggiungeremo un secondo invio tramite un altro intermediario per la stessa pratica.

È chiaro che registro e bollo sono esclusi dal prezzo: con cedolare non sono dovuti; senza cedolare vengono addebitati sul conto del locatore. L'IBAN necessario sarà raccolto da voi attraverso il vostro canale, senza inserirlo nei link o nell'endpoint di stato.

Consideriamo il tempo di pochi giorni dopo firmati e pagamento come indicativo, nel rispetto del termine di registrazione dalla stipula. La comunicazione IMU al Comune resta un adempimento separato, non incluso automaticamente nella registrazione.

## Prezzi, pagamento e ricevuta

Confermiamo che l'utente non paga mai direttamente voi: CasaZen paga pratica per pratica dopo l'invio dei firmati.

Prendiamo atto degli importi finali di 69 e 89 euro senza IVA e della ricevuta rilasciata al locatore per quota associativa e contributo, quale operazione non commerciale esente IVA ai sensi dell'art. 4 D.P.R. 633/72, come da vostra indicazione.

Per evitare equivoci, vi chiediamo di riepilogare quale servizio è incluso nei 69 euro e quale negli 89 euro, se la registrazione costituisce la differenza tra i pacchetti e quali documenti finali consegnate. Indicateci inoltre metodo di pagamento, riferimento da usare per riconciliare il pagamento CasaZen con la ricevuta al locatore e trattamento di pratiche annullate o non attestabili. Non richiediamo il precedente modello di saldo mensile.

## Libero, transitori e sola registrazione

Prendiamo atto del servizio per libero 4+4 e transitori agli stessi prezzi, con trasmissione dei dati per email finché il modulo accetta solo concordati, e della sola registrazione di contratto già pronto a 20 euro. Indicateci l'indirizzo e le modalità sicure da usare, i documenti richiesti e il trattamento fiscale/documento rilasciato per la tariffa di 20 euro.

Per i transitori vi chiediamo di distinguere quelli soggetti ad accordo territoriale e di confermare quando occorre l'attestazione: non estenderemo a tutti i transitori la regola di assenza di attestazione del libero 4+4.

## Veste CasaZen e avvio

Il marchio è disponibile nei file SVG lockup.svg e marchio.svg del kit CasaZen. Colori su fondo chiaro: terracotta #e2622d e inchiostro #1f3a3d. Il kit è allegato alla documentazione di revisione. Una volta ricevuti asset e dominio, attendiamo la vostra data per la veste personalizzata.

Per il contratto con decorrenza 1° novembre 2026 prendiamo nota della possibilità di usare subito https://app.concordatofacile.it come pratica CasaZen senza aspettare la veste. La compilazione e l'invio dei firmati saranno gestiti separatamente: questo riscontro non costituisce invio della pratica. Vi chiediamo come identificarla come CasaZen nel modulo attuale in attesa del codice partner.

Prima dell'avvio operativo completiamo anche i riferimenti dell'associazione/intermediario e delle organizzazioni che attestano per i comuni serviti, il referente di assistenza, i ruoli privacy e i tempi di conservazione dei documenti.

Cordiali saluti,
CasaZen
