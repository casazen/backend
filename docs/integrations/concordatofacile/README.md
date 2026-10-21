# CasaZen / ConcordatoFacile — flusso del servizio

Aggiornamento: 21 ottobre 2026. Questo documento descrive il flusso concordato, non certifica che l'integrazione sia già attiva. Le sole attività da implementare sono nell'[analisi tecnica per Helix](../../../Sessions/business-analysis-canone-concordato.md).

## Dal contratto alla pratica

L'utente apre il servizio ConcordatoFacile dal proprio contratto concordato in CasaZen. Il link porta a `https://app.concordatofacile.it` con quattro parametri: `partner`, `leaseId`, `comune` e `provincia`. La provincia è una sigla, per esempio `MB`; il link non è firmato. Il codice partner deve essere assegnato da CF.

Il fornitore conserva leaseId e lo associa al numero di pratica. L'utente gestisce direttamente con CF i dati, i documenti, eventuali integrazioni, il contratto e la firma. CF prepara il contratto sul modello ministeriale; se è già predisposto, cura l'attestazione quando necessaria. Il modulo attuale accetta solo concordati: libero 4+4 e transitori sono gestiti dal fornitore per email, fuori dall'integrazione del modulo.

## Comunicazioni e stato

CasaZen non riceve la prima email di pratica ricevuta, quella finale con i documenti né copie o inoltri delle email dell'utente. Comunicazioni operative e consegna dei documenti sono direttamente tra CF e utente.

CasaZen conosce lo stato esclusivamente tramite l'endpoint del fornitore, interrogato dal backend con chiave e leaseId. CF ha confermato numero pratica, stato comprensivo della registrazione e data dell'ultimo passaggio, senza dati personali. URL, metodo, schema, autenticazione, stati e limiti restano da fornire: non esiste ancora un contratto tecnico esterno definitivo.

Il minimo previsto è leggere lo stato quando l'utente apre il contratto o chiede di aggiornarlo. Non sono richiesti webhook, email di stato o un motore di sincronizzazione in background.

## Ritorno a CasaZen e documenti sulla casa

Nelle email all'utente con documenti da conservare CF inserisce «Carica i dati in CasaZen»; nella conferma testuale del modulo usa un link per esteso.

Dominio e percorso definiti:

`https://casazen.space/app/long-rent/documents/upload?leaseId=<leaseId>`

Esempio completo:

`https://casazen.space/app/long-rent/documents/upload?leaseId=0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11`

CF sostituisce il segnaposto con l'identificativo ricevuto. Questo è un indirizzo web di atterraggio, non l'endpoint di stato e non l'API di caricamento file. La route deve essere realizzata e verificata prima di attivare i collegamenti nelle email.

Dopo login CasaZen verifica l'accesso al contratto, ricava la casa associata e mostra il caricamento documenti esistente. L'utente sceglie i file e conferma; i documenti vengono conservati nell'archivio della casa. Nel minimo rilascio leaseId individua il contesto: non si introduce un legame persistente nuovo tra documento e contratto.

Il click non trasferisce automaticamente documenti da CF, non registra il contratto e non certifica il completamento della pratica. Senza identificativo valido o autorizzazione non avviene alcun caricamento. I controlli di scrittura sulla casa rimangono sul server.

## Registrazione, pagamento e adempimenti

Se richiesta, l'associazione registra telematicamente come intermediario a nome del locatore; secondo CF non occorre delega firmata dalle parti. La cedolare viene scelta in registrazione. La stessa registrazione non passa anche da Openapi; i percorsi alternativi esistenti mantengono i propri requisiti.

CasaZen paga per pratica dopo invio dei firmati; l'utente non paga direttamente CF. Queste attività restano operative, senza checkout o pagamento automatico aggiunto dall'integrazione.

CF indica prezzi finali di 69 e 89 euro senza IVA e ricevuta al locatore per quota associativa e contributo, quale operazione non commerciale esente IVA ai sensi dell'art. 4 D.P.R. 633/72. L'abbinamento dei servizi ai due importi è ancora da confermare. La sola registrazione di contratto già pronto è indicata a 20 euro, con condizioni da completare.

Registro e bollo sono esclusi: con cedolare non dovuti; altrimenti addebitati sul conto del locatore, con IBAN raccolto da CF. I tempi indicati sono pochi giorni da firmati e pagamento, nel termine dalla stipula; non è uno SLA di 48 ore. Per i transitori soggetti ad accordo territoriale restano da verificare i requisiti di attestazione.

Stato pratica CF, documenti archiviati, registrazione e comunicazione IMU restano distinti. L'IMU non viene comunicata automaticamente al Comune da questa integrazione; il calcolatore CasaZen resta informativo.

## Materiale per il fornitore

- [Riscontro in PDF](da-inviare/03-risposta-fornitore.pdf) e [sorgente Markdown](da-inviare/03-risposta-fornitore.md).
- [ZIP del marchio](da-inviare/CasaZen-kit-marchio.zip): lockup.svg, marchio.svg e README.md con descrizioni, colori e URL da usare nelle email. Terracotta `#e2622d`, inchiostro `#1f3a3d`, fondo chiaro.
- [Sintesi degli accordi tecnici](da-inviare/01-technical-specification.md).

Le proposte precedenti di link firmati e webhook sono ritirate. I file OpenAPI correnti hanno `paths: {}` proprio perché CF non ha ancora fornito il contratto effettivo dell'endpoint.
