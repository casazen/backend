# Analisi business — Canone concordato assistito con ConcordatoFacile

Aggiornamento: 21 ottobre 2026. Fonte: risposta del fornitore riportata dal referente CasaZen in questa richiesta. Il testo ricevuto non contiene una data autonoma. Questa revisione aggiorna l'analisi e la progettazione, senza implementare né attivare servizi.

L'[analisi precedente](business-analysis-canone-concordato-storico.md) resta come ricerca storica: le sue descrizioni del codebase, del freeze LTR e del flusso Openapi non attestano lo stato attuale. Per l'integrazione CF prevalgono questa revisione e la [specifica aggiornata](../docs/integrations/concordatofacile/da-inviare/01-technical-specification.md).

## 1. Decisione di prodotto

CasaZen accompagna il locatore verso il modulo CF. Il fornitore gestisce raccolta dati e documenti, verifiche, redazione del contratto sul modello ministeriale, attestazione quando necessaria, scambio dei firmati e registrazione se richiesta. CasaZen conserva il collegamento al contratto, consulta lo stato e offre l'archivio documentale. CasaZen non emette attestazioni e non è il trasmittente della registrazione.

Il calcolatore esistente resta informativo: il range locale non sostituisce la verifica dell'associazione. Le agevolazioni dipendono dai requisiti del contratto e del comune; la cedolare ridotta non va promessa per ogni concordato. La comunicazione IMU resta un adempimento separato e non risulta inclusa nel servizio CF.

## 2. Flusso aggiornato

1. L'utente seleziona il servizio sul contratto CasaZen. Si conserva il `leaseId` reale e si impediscono invii duplicati dalla piattaforma.
2. CasaZen apre `https://app.concordatofacile.it` con `partner`, `leaseId`, `comune`, `provincia`. Provincia in sigla, ad esempio `MB`; valori codificati nell'URL; nessuna firma, scadenza, `theme`, codice ISTAT o `returnUrl` richiesti nel nuovo accordo. Codice partner da ricevere da CF.
3. L'utente completa la pratica con CF; modalità di rientro, salvataggio e firma effettiva restano da verificare con il fornitore, senza assumere le garanzie delle vecchie specifiche.
4. CF conserva `leaseId` nella pratica e lo restituisce nelle email e nell'endpoint. CasaZen non richiede né la prima email di pratica ricevuta né quella finale con documenti, né copie o inoltri delle comunicazioni all'utente. Per CasaZen lo stato arriva esclusivamente dall'endpoint, registrazione compresa. Email operative e consegna dei documenti sono gestite direttamente tra CF e utente; resta il bottone nelle email all'utente con documenti. Eliminate le righe di stato nelle email.
5. CasaZen consulta dal backend l'endpoint CF con chiave fornita da CF e ricerca per `leaseId`: numero di pratica, stato comprensivo della registrazione e data dell'ultimo passaggio, senza dati personali. Metodo, URL, header, enum e frequenza sono da concordare.
6. Dopo l'invio dei firmati CasaZen paga CF pratica per pratica. Il locatore non paga mai direttamente CF; eventuale prezzo e incasso CasaZen verso l'utente restano una decisione commerciale separata.
7. Se richiesta, l'associazione registra telematicamente come intermediario a nome del locatore. Secondo CF non serve una delega firmata dalle parti; non si richiede il passaggio Openapi né la relativa delega per questa stessa pratica. I percorsi alternativi esistenti mantengono i propri requisiti.
8. CF inserisce «Carica i dati in CasaZen» nelle email con documenti da conservare; nella conferma testuale inserisce il link per esteso. Il dominio è fisso, non arbitrario nell'URL del modulo.
9. Il ritorno è una navigazione: l'utente accede a CasaZen, si verifica l'accesso al contratto e carica/conferma i documenti. Il bottone e lo stato da soli non trasferiscono file né dati attestati. Nessuna importazione automatica o registrazione confermata dal solo click.
10. Stato registrazione, documenti archiviati, dati di registrazione verificati e comunicazione IMU sono indicatori distinti. Se mancano i documenti finali, la pratica non si presenta come archivio completo.

## 3. Offerta e costi

| Voce | Conferma del fornitore / trattamento |
|---|---|
| Importi CF | 69 e 89 euro finali, senza IVA. Confermare esplicitamente quale pacchetto corrisponde a ciascun importo; non dedurlo dal vecchio listino 79/99. |
| Contratto | Preparazione su modello ministeriale compresa. Se già presente, CF cura l'attestazione. |
| Registrazione | Eseguita dall'associazione, se richiesta; la cedolare si sceglie in registrazione. |
| Tempi | Di norma pochi giorni da firmati e pagamento, secondo CF entro 30 giorni dalla stipula. Non è uno SLA di 48 ore. Registrare data di stipula e scadenza applicabile, segnalare ritardi o margine insufficiente. |
| Registro e bollo | Esclusi; con cedolare non dovuti. Senza cedolare addebito sul conto del locatore e IBAN raccolto da CF attraverso il proprio canale, mai in query o endpoint di stato. |
| Pagamento CF | CasaZen paga per pratica dopo invio dei firmati. Superata la richiesta di credito/mensilizzazione. Metodo, riferimento e gestione di annullamenti/rifiuti da chiarire. |
| Documento | Ricevuta al locatore per quota associativa e contributo: operazione non commerciale esente IVA, art. 4 D.P.R. 633/72, secondo la qualificazione comunicata da CF. Non assimilare la ricevuta del locatore a fattura intestata a CasaZen; riconciliazione contabile da definire. |
| Altre locazioni | CF dichiara supporto per libero 4+4 e transitori agli stessi prezzi. Oggi dati per email: il modulo accetta solo concordati. Non estendere indiscriminatamente l'assenza di attestazione a tutti i transitori; per quelli soggetti ad accordo territoriale verificare requisiti e attestazione con CF. |
| Sola registrazione | 20 euro per contratto già pronto; confermare trattamento fiscale/documento e inclusioni di questa tariffa. |

Il margine CasaZen non è determinabile senza prezzo al cliente, pacchetti confermati, costi di pagamento e assistenza. Imposte e bollo non sono ricavi né costi del servizio da assorbire nel prezzo CF.

## 4. Impatto tecnico e operativo

- Sostituire la proposta di webhook firmati e link HMAC con polling di stato autenticato dal server. Nessuna chiave nel browser o nel PDF. I vecchi OpenAPI non sono contratti accettati.
- Progettare il ritorno su dominio fisso e percorso stabile. L'identificativo nel link non autentica il locatore: login, verifica di appartenenza e conferma del caricamento sono necessari. Link senza firma non deve permettere accesso o modifica di dati CF conoscendo solo `leaseId`.
- Conservare stato originale CF, numero pratica, data ultimo passaggio e data ultima sincronizzazione; distinguere indisponibilità da pratica non trovata. Retry con backoff e gestione 429; frequenza da concordare. In caso di timestamp precedente non regredire lo stato; in caso di nuova pratica o timestamp ambiguo verificare prima di sovrascrivere.
- Un numero pratica diverso per lo stesso leaseId può indicare riapertura: definire la regola con CF, non perdere lo storico e non bloccare per sempre il polling dopo l'attestazione se è prevista registrazione.
- Per attestazione e ricevute usare caricamento controllato e verifica dei documenti. Nessuna estrazione strutturata automatica promessa; eventuale futuro trasferimento file richiede accordo separato.
- Veste: logo `frontend/public/brand/lockup.svg`, marchio `marchio.svg`; terracotta `#e2622d`, inchiostro `#1f3a3d`, fondo chiaro. Dominio definitivo confermato dal referente: `casazen.space`. Non usare il namespace Auth0 come prova del dominio pubblico.

## 5. Punti aperti e accettazione

Prima della costruzione: ricevere endpoint e chiave per canale riservato, esempio di risposta senza dati personali, enum inclusi registrazione/rifiuto/annullamento, timezone, rate limit, comportamento su leaseId ignoto e pratiche multiple; implementare e verificare l'URL del bottone definito sotto. Confermare pacchetti 69/89, tariffe per transitori e requisiti, pagamenti, organizzazioni firmatarie per comune, ruoli privacy e conservazione. Le precedenti proposte su account, ripresa, 48 ore, dieci anni, sandbox e rinnovo link non sono accettazioni del fornitore.

Accettazione del futuro sviluppo:

1. URL con i soli quattro parametri; provincia `MB`, comune con spazi/accenti correttamente codificato.
2. Correlazione leaseId → numero pratica in email ed endpoint; leaseId sconosciuto e riapertura gestiti.
3. Chiave solo server; risposta senza dati personali; errore di rete non azzera lo stato; timestamp obsoleto non sovrascrive dati più recenti.
4. Stato attestato distinto da registrato; attestazione senza registrazione non chiude un servizio che include registrazione.
5. Bottone e link testuale conservano il contesto dopo login; un altro utente non accede al contratto; click ripetuti non duplicano documenti.
6. Firma, pagamento CasaZen, ricevuta al locatore e documenti finali verificati su pratica pilota; IMU resta separata.

Per il contratto del 1° novembre 2026 il fornitore propone di usare subito il modulo ordinario come pratica CasaZen, senza aspettare la veste. Questa richiesta aggiorna documenti e apre PR: non autorizza l'invio di una pratica reale né il pagamento. La data di stipula può differire dalla decorrenza e determina il controllo del termine applicabile.

## 6. Atterraggio per il caricamento sulla casa

Dominio confermato dal referente CasaZen: `https://casazen.space`. URL definito per il fornitore:

`https://casazen.space/app/long-rent/documents/upload?leaseId=<leaseId>`

Percorso applicativo proposto: `/app/long-rent/documents/upload`; query `leaseId` obbligatoria, UUID del contratto già trasmesso a CF. Non serve un propertyId nel link: il backend ricava la casa dal contratto, verifica i diritti dell'utente su contratto e casa e mantiene il riferimento a entrambi nell'archiviazione. Mostrare casa e contratto prima di confermare l'upload; i documenti compaiono nell'archivio della casa con il collegamento alla locazione.

Se l'utente deve accedere, preservare percorso e leaseId nel ritorno interno dopo login. Non accettare destinazioni di redirect esterne. Con leaseId assente, malformato, inesistente o non autorizzato non caricare né associare file automaticamente; proporre un ritorno alle proprie case senza rivelare dati altrui. Ripetere il controllo dei diritti all'effettivo caricamento, non solo all'apertura della pagina.

Questo è il percorso da implementare nel frontend e collegare al caricamento documentale: non è una route già disponibile, e questa PR rimane documentale. Prima dell'attivazione delle email verificare login con ritorno preservato, associazione alla casa corretta, accesso negato per un altro utente, leaseId errato e caricamento ripetuto.
