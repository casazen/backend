# CasaZen / ConcordatoFacile — flusso concordato

Versione 2.0 — 21 ottobre 2026. Revisione documentale sulla risposta CF riportata dal referente CasaZen. Sostituisce la proposta 1.3: non è prova di endpoint già implementati. Dettagli di prodotto e accettazione: [analisi aggiornata](../../../../Sessions/business-analysis-canone-concordato.md).

## Accordi confermati

- Modulo: `https://app.concordatofacile.it`, con `partner`, `leaseId`, `comune`, `provincia` e senza firma. Provincia in sigla (`MB`). Codice partner da fornire da CF. Nessun `expiresAt`, `signature`, `theme`, `returnUrl` o `comuneIstatCode` richiesto.
- Esempio illustrativo (codice partner da confermare): `https://app.concordatofacile.it?partner=CODICE_CF&leaseId=0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11&comune=Cesano%20Maderno&provincia=MB`.
- CF conserva leaseId fino alla pratica e lo restituisce nelle email e nell'endpoint.
- CF prepara un endpoint di stato interrogabile per leaseId con chiave fornita da CF: numero pratica, stato inclusa registrazione, data ultimo passaggio; nessun dato personale. CasaZen lo consulta dal backend. URL, metodo, header, nomi JSON, enum, timezone e limiti da definire con CF; nessun webhook richiesto nel nuovo flusso.
- Bottone «Carica i dati in CasaZen» nelle email con documenti da conservare. Nella conferma del modulo, solo testo, link per esteso. Dominio definitivo confermato: `https://casazen.space`. URL del ritorno definito da CasaZen: `https://casazen.space/app/long-rent/documents/upload?leaseId=<leaseId>`. CF sostituisce il segnaposto con il leaseId ricevuto. Percorso da implementare e verificare prima di attivare le email.
- CF prepara il contratto su modello ministeriale; se il contratto esiste, cura l'attestazione. Registrazione telematica dell'associazione come intermediario a nome del locatore, senza delega firmata delle parti secondo CF. Opzione cedolare in registrazione.
- Tempi indicativi: pochi giorni dal ricevimento di firmati e pagamento, entro il termine di 30 giorni dalla stipula indicato da CF; nessuno SLA di 48 ore concordato.
- Registro e bollo esclusi. Con cedolare non dovuti; senza cedolare addebito sul conto del locatore, IBAN raccolto da CF.
- CasaZen paga pratica per pratica dopo invio dei firmati. L'utente non paga CF. Importi 69 e 89 euro finali senza IVA, abbinamento ai pacchetti da confermare. Ricevuta al locatore per quota associativa e contributo, operazione non commerciale esente IVA (art. 4 D.P.R. 633/72), secondo CF.
- Libero 4+4 e transitori gestiti per email agli stessi prezzi; modulo oggi solo per concordati. Sola registrazione contratto pronto: 20 euro. Per transitori soggetti ad accordo territoriale verificare attestazione e condizioni con CF.

## Scelte CasaZen proposte nella risposta

Mantenere prima email di pratica ricevuta e finale con documenti, oltre alle email necessarie per integrazioni e firma. Eliminare le righe di stato via email: le copre l'endpoint. Il ritorno a CasaZen è navigazione verso caricamento/conferma dell'utente, dopo login e verifica dei diritti. Non trasmette da solo documenti o dati strutturati. Il click non certifica completamento o registrazione.

Logo e colori: `frontend/public/brand/lockup.svg` e `marchio.svg`; terracotta `#e2622d`, inchiostro `#1f3a3d`, fondo chiaro. Data veste da CF dopo consegna asset e dominio; avvio pilota senza attendere la veste.

## Dettagli ancora da definire

1. Codice partner, URL/method/header dell'endpoint, esempio reale anonimizzato, stati e significati, data/ora e timezone, limiti e frequenza di interrogazione, prova con leaseId ignoto e chiave errata. Chiave da consegnare fuori banda.
2. Comportamento per stesso leaseId riaperto, pratiche multiple e correzioni; il link iniziale non è un'autenticazione e non deve consentire lettura di dati riservati.
3. Implementazione e verifica del percorso di caricamento definito sotto; contenuto dei documenti finali da confermare. Nessuna API di importazione file concordata.
4. Contenuti dei pacchetti da 69/89, condizioni dei 20 euro, modalità di pagamento e riconciliazione tra pagante CasaZen e ricevuta al locatore, annullamenti e rifiuti.
5. Attestazioni e firmatari per accordo territoriale, transitori, privacy, conservazione, supporto e date di disponibilità. Nessuna delle garanzie aggiuntive della vecchia bozza è automaticamente accettata.

## Architettura prevista e verifica

Polling server con chiave segreta, retry/backoff e gestione rate limit. Conservare numero pratica, stato CF, data ultimo passaggio e ultima sincronizzazione; distinguere errori da pratica inesistente. Non applicare risposte obsolete, non confondere attestazione con registrazione e non sovrascrivere uno storico con una nuova pratica.

Archiviazione documenti separata dallo stato; caricamento autenticato e verificato. IMU separata dalla registrazione. Il canale CF non passa anche da Openapi per la stessa registrazione; gli altri canali mantengono i loro requisiti.

Prove previste: quattro parametri e URL encoding; leaseId preservato in email/stato/ritorno; chiave mai al client; risposta priva di dati personali; stato registrazione distinto dall'attestazione; guasti e timestamp vecchi; accesso al contratto solo autorizzato; caricamenti ripetuti; ricevute finali e IMU distinta. Questa PR modifica solo documenti.

## Atterraggio CasaZen definito

`https://casazen.space/app/long-rent/documents/upload?leaseId=<leaseId>`

Il dominio è definitivo; il percorso `/app/long-rent/documents/upload` è il punto di atterraggio da realizzare per caricare i documenti sulla casa collegata al contratto. Il leaseId è obbligatorio e non autentica l'utente. Dopo login, con percorso e parametro preservati, CasaZen verifica i diritti e ricava la casa dal contratto: mostra entrambi prima della conferma e archivia sulla casa mantenendo il collegamento al contratto. Nessun propertyId o dato personale aggiuntivo richiesto a CF.

Con leaseId assente, errato o non autorizzato non si effettua alcun caricamento o associazione automatica. Il server verifica nuovamente i diritti al caricamento. Il percorso rimane una specifica, non una funzionalità già attiva: testare ritorno dopo login, associazione alla casa e accesso di un altro utente prima di inserirlo nelle email operative.
