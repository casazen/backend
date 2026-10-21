# CasaZen / ConcordatoFacile — flusso del servizio

Aggiornamento: 21 ottobre 2026. Questo documento descrive il flusso concordato, non certifica che l'integrazione sia già attiva. Le sole attività da implementare sono nell'[analisi tecnica per Helix](../../../Sessions/business-analysis-canone-concordato.md).

## Offerta CasaZen e accesso al servizio

Il prezzo all'utente è **119 euro IVA compresa per una pratica di contratto**, con ricarico CasaZen già compreso. L'utente ordinario accede dalla pagina servizio del contratto, che presenta prezzo e sequenza dei passaggi, paga CasaZen e prosegue al modulo CF soltanto dopo conferma server del pagamento. Registro e bollo eventualmente dovuti restano esclusi. Il pagamento al fornitore è distinto dall'acquisto dell'utente.

L'admin di piattaforma CasaZen può aprire direttamente il modulo senza pagina di pagamento; questo bypass non vale per l'admin di una normale organizzazione. Un abbonamento CasaZen di due anni dà diritto a una pratica omaggio: il flag persistente nel database abilita il beneficio, consumato una sola volta e associato al contratto. L'architettura billing attuale attribuisce il beneficio all'organizzazione abbonata. La seconda pratica richiede il pagamento ordinario; riaprire la pratica già sbloccata non costa di nuovo.

La pagina spiega all'utente:

1. Dopo il pagamento o l'utilizzo dell'omaggio vieni reindirizzato al modulo del partner.
2. Compili il modulo con dati e documenti richiesti.
3. Gestisci tutto direttamente via email con il partner: integrazioni, contratto e firma, fino alla registrazione prevista dal servizio.
4. Alla fine ricevi l'email del partner con documenti e link per caricarli in CasaZen.
5. Ti consigliamo il caricamento per conservare i documenti sulla tua casa e gestire i contratti in modo centralizzato in CasaZen.

Il controllo commerciale è sul server: il solo ritorno dal checkout non prova il pagamento. Apertura per admin, pratica pagata o omaggio usa lo stesso leaseId della pratica. Il caricamento finale non richiede un secondo acquisto.

Il link CF senza firma non garantisce da solo che un utente non ricostruisca l'indirizzo pubblico del modulo. Prima dell'avvio va concordata con CF la verifica del diritto o riconciliazione prima di lavorare pratiche partner; CasaZen protegge sempre l'accesso dalla propria piattaforma.

## Dalla casa alla bozza concordato

Nel flusso definitivo la prima domanda è il tipo di contratto. Se l'utente sceglie canone concordato, CasaZen salva subito una bozza reale collegata alla casa e assegna il leaseId; non chiede prima dati che saranno definiti dal partner. Quel riferimento rimane lo stesso dopo l'acquisizione dei documenti definitivi.

Dopo pagamento o omaggio, il link apre `https://app.concordatofacile.it` con `partner`, `leaseId`, `comune` e `provincia` (sigla, ad esempio MB), senza firma. Il codice partner deve essere assegnato da CF. L'admin CasaZen segue l'accesso diretto previsto.

CF prepara il vero contratto; l'utente gestisce direttamente con CF dati, documenti, integrazioni, revisione e firma. CasaZen non genera prima un secondo contratto concordato. Il modulo attuale accetta solo concordati: libero 4+4 e transitori sono gestiti dal fornitore per email, fuori da questo percorso.

## Comunicazioni e stato

CasaZen non riceve la prima email di pratica ricevuta, quella finale con i documenti né copie o inoltri delle email dell'utente. Comunicazioni operative e consegna dei documenti sono direttamente tra CF e utente.

CasaZen conosce lo stato esclusivamente tramite l'endpoint del fornitore, interrogato dal backend con chiave e leaseId. CF ha confermato numero pratica, stato comprensivo della registrazione e data dell'ultimo passaggio, senza dati personali. URL, metodo, schema, autenticazione, stati e limiti restano da fornire: non esiste ancora un contratto tecnico esterno definitivo.

Il minimo previsto è leggere lo stato quando l'utente apre il contratto o chiede di aggiornarlo. Non sono richiesti webhook CF, email di stato o un motore di sincronizzazione in background. Gli eventi server verificati del provider di pagamento sono invece necessari per confermare acquisti e omaggi.

## Ritorno a CasaZen e documenti sulla casa

Nelle email all'utente con documenti da conservare CF inserisce «Carica i dati in CasaZen»; nella conferma testuale del modulo usa un link per esteso.

Dominio e percorso definiti:

`https://casazen.space/app/long-rent/documents/upload?leaseId=<leaseId>`

Esempio completo:

`https://casazen.space/app/long-rent/documents/upload?leaseId=0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11`

CF sostituisce il segnaposto con l'identificativo ricevuto. Questo è un indirizzo web di atterraggio, non l'endpoint di stato e non l'API di caricamento file. La route deve essere realizzata e verificata prima di attivare i collegamenti nelle email. Verifica in browser produzione del 21 ottobre 2026: il percorso (anche con leaseId) mostra «404 — Pagina non trovata» su www.casazen.space; nessun via libera al fornitore finché non supera la verifica completa. Il solo HTTP 200 della shell SPA non è una prova di funzionamento.

Dopo login CasaZen verifica l'accesso al contratto, ricava la casa associata e mostra il caricamento documenti esistente. L'utente sceglie i file e conferma; i documenti vengono conservati nell'archivio della casa. Nella pagina di caricamento iniziale è disponibile anche propertyId per conservare file sulla casa prima che esista il contratto. Il successivo lavoro Helix collega i documenti al leaseId della bozza e li rende consultabili sia dalla casa sia dal contratto.

Il click non trasferisce automaticamente documenti da CF e non invia la registrazione. Nel flusso definitivo il caricamento del contratto, dell'attestazione e della ricevuta di registrazione conserva gli originali e avvia l'estrazione dei dati per popolare le rispettive sezioni del contratto. Errori o campi incerti restano da verificare; non si crea un altro contratto e le copie restano consultabili. L'estrazione strutturata è lavoro Helix successivo all'esposizione della pagina generica. Senza identificativo valido o autorizzazione non avviene alcun caricamento. I controlli di scrittura sulla casa rimangono sul server.

## Registrazione, pagamento e adempimenti

Se richiesta, l'associazione registra telematicamente come intermediario a nome del locatore; secondo CF non occorre delega firmata dalle parti. La cedolare viene scelta in registrazione. La stessa registrazione non passa anche da Openapi; i percorsi alternativi esistenti mantengono i propri requisiti.

CasaZen paga per pratica dopo invio dei firmati; l'utente non paga direttamente CF. Il saldo CasaZen → CF resta operativo; il checkout utente → CasaZen a 119 € IVA compresa è invece parte dell'implementazione richiesta.

CF indica prezzi finali di 69 e 89 euro senza IVA e ricevuta al locatore per quota associativa e contributo, quale operazione non commerciale esente IVA ai sensi dell'art. 4 D.P.R. 633/72. L'abbinamento dei servizi ai due importi è ancora da confermare. La sola registrazione di contratto già pronto è indicata a 20 euro, con condizioni da completare.

Registro e bollo sono esclusi: con cedolare non dovuti; altrimenti addebitati sul conto del locatore, con IBAN raccolto da CF. I tempi indicati sono pochi giorni da firmati e pagamento, nel termine dalla stipula; non è uno SLA di 48 ore. Per i transitori soggetti ad accordo territoriale restano da verificare i requisiti di attestazione.

Stato pratica CF, documenti archiviati, registrazione e comunicazione IMU restano distinti. L'IMU non viene comunicata automaticamente al Comune da questa integrazione; il calcolatore CasaZen resta informativo.

## Materiale per il fornitore

- [Riscontro su attivazione link e lockup in tracciati](da-inviare/04-riscontro-link-e-marchio.md).
- [Riscontro in PDF](da-inviare/03-risposta-fornitore.pdf) e [sorgente Markdown](da-inviare/03-risposta-fornitore.md).
- [ZIP del marchio](da-inviare/CasaZen-kit-marchio.zip): lockup.svg, marchio.svg e README.md con descrizioni, colori e URL da usare nelle email. Terracotta `#e2622d`, inchiostro `#1f3a3d`, fondo chiaro.
- [Sintesi degli accordi tecnici](da-inviare/01-technical-specification.md).

Le proposte precedenti di link firmati e webhook sono ritirate. I file OpenAPI correnti hanno `paths: {}` proprio perché CF non ha ancora fornito il contratto effettivo dell'endpoint.
