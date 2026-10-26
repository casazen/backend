# AW-01 — Completamento integrazione Alloggiati Web per Helix

Stato: requisiti di completamento; decisioni PO e documentazione ufficiale integrate. In produzione l'invio reale è
attivo per le utenze dell'agenzia validamente configurate; sviluppo, test e demo restano simulati.
Data: 26 ottobre 2026. Destinazione: Helix. Collegamenti: Q1-14, NF-14, ADR-015.
Perimetro: backend, frontend e compatibilità mobile CasaZen; solo immobili italiani.
Analisi statica del branch develop: backend `25c46aa28adce857a5eecbedeb2b5230ca411f80`; frontend `6b231aaedded8378377c1d18de07d4fb9c43bb7e`; mobile `3cf8dd793fe2f66abf40b17a2dcf68f3fed2b920`. Non equivale a verifica del codice effettivamente distribuito né delle configurazioni degli ambienti.

## Verifica degli allegati — completata il 26 ottobre 2026

Sono stati letti integralmente `2013_DM.pdf`, `109_TULPS.pdf`, `InfoGen.pdf`, `2021_DM.pdf`, `RICDIGALLO.pdf` e
`MANUALEWS.pdf` (rev. 01, 24/01/2022), oltre a `documenti.csv`, `stati.csv`, `tipo_alloggiato.csv` e `comuni.csv`.
Le quattro tabelle CSV coincidono con gli SHA-256 dichiarati in
`Casazen.Infrastructure/Data/Seeds/alloggiati/source.json`: i seed già distribuiti sono gli stessi dati ufficiali
allegati.

Questa revisione prevale sui punti incompatibili del testo precedente. Le fonti fissano in particolare che:

- `GenerateToken` usa `Utente`, `Password` e `WsKey`; le richieste successive usano `Utente` e `token`;
- `Test` e `Send` ricevono una lista `ElencoSchedine` ed espongono un esito generale e uno per riga; `Send` può acquisire
  solo le righe corrette;
- `Ricevuta` riceve una singola data, non un intervallo, e restituisce il PDF della giornata; la ricevuta è per utenza e
  giorno e riporta il totale delle schedine, non un protocollo per singola prenotazione;
- il profilo `Gestione Appartamenti` usa l'`IdAppartamento`, e il formato File Unico è di 174 caratteri contro i 168 del
  profilo singolo;
- il D.M. 2021 consente l'uso delle credenziali da parte di un soggetto identificato, espressamente incaricato e
  istruito; non basta una casella di consenso;
- ricevute e loro metadati vanno conservati cinque anni; la ricevuta remota è disponibile solo per gli ultimi 30 giorni.

Alloggiati Web è il flusso Questura ex art. 109 TULPS, **non** è l'invio statistico ISTAT e non chiude Ross1000. I due
adempimenti possono riusare dati ospite, ma devono conservare canali, formati, scadenze, ricevute e stati distinti.
`Property.ComuneIstatCode` non sostituisce il codice Alloggiati a nove caratteri. Ross1000/ISTAT rimane non implementato
e condizionato al tracciato ufficiale di `docs/runbooks/ross1000.md`.

## Obiettivo e risultato atteso

Completare il percorso ospiti del soggiorno → validazione locale → Test SOAP → Send → acquisizione degli esiti per riga → ricevuta giornaliera privata → stato verificabile e avvisi. Il flusso manuale esistente resta disponibile per incidenti o casi non trasmissibili, ma non è la modalità ordinaria dell'agenzia configurata. Una classe presente o una risposta del simulatore non costituisce prova di un invio alla Questura.

Il presente documento integra Q1-14 e richiede la correzione di ADR-015 dove assume un protocollo da Send, una Ricevuta per intervallo, enum rinumerati o cifratura diversa da quella già applicata. Non cambiare retroattivamente i valori persistiti dell'enum.

## Stato riscontrato nel codice

| Area | Evidenza | Valutazione e lavoro residuo |
|---|---|---|
| Ospiti e codici | `StayGuests`, `AlloggiatiRecordRules`, `AlloggiatiCodeBook`, import e seed delle quattro tabelle | Base riutilizzabile. Includere tutti gli ospiti e verificare che non manchino persone rispetto al soggiorno dichiarato. |
| File manuale | `Casazen.Core/Regulatory/AlloggiatiRecordFile.cs`; GET `/api/alloggiati/{bookingId}/record-file` | Esiste il builder a 168 caratteri, export con permessi e no-store. Il job SOAP non lo usa. |
| Pianificazione | `AlloggiatiReportScheduler`; `External/AlloggiatiWebService.ProcessScheduledReportAsync` | Il flusso collegato mette ancora il report in DaInviareManualmente. Non trasmette. |
| Invio API | `Casazen.Web/Controllers/AlloggiatiController.cs` | POST send risponde ancora 422 alloggiati_transmission_unavailable. |
| Credenziali | `PropertyQuesturaCredentials`, `QuesturaCredentialsService`, card frontend | Per immobile, cifrate tramite converter EF, sola scrittura. Mancano profilo e account condiviso per appartamenti. |
| Client SOAP | `Infrastructure/Services/AlloggiatiWebClient.cs` | Prototipo: namespace, payload ed esiti da riallineare al contratto ufficiale; GenerateToken manda solo WSKEY; Test cerca Errore invece di gestire l'intero esito; Send pretende NumeroRicevuta; Ricevuta usa DataDa/DataA; Tabella restituisce stringa vuota. |
| Cifratura nel client | `EncryptedColumns.cs`, `EncryptedStringConverter.cs`, AlloggiatiWebClient | EF restituisce già valori decifrati; il client chiama ancora Unprotect su Username, Password e WsKey. Eliminare la doppia decifratura e preservare il purpose esistente. |
| Job di invio | `Infrastructure/Jobs/AlloggiatiSendJob.cs` | BuildAlloggiatiRows è un placeholder basato sul prenotante, con tipo a un carattere e padding; non costruisce le schedine di StayGuests. Non controlla incarico/documentazione, account o arrivo nel job. |
| Job ricevute | `Infrastructure/Jobs/AlloggiatiReceiptJob.cs` | Cerca corrispondenza per ConfirmationNumber, non salva il PDF e non limita la query di matching a credentialId. DeletedAt sulle immagini non elimina il file. |
| Collegamento runtime | `Casazen.Web/Program.cs`, `BackgroundJobs/RecurringJobsRegistration.cs` | Non risultano registrati client e nuovi job né il named HttpClient AlloggiatiWeb; manca la pianificazione del recupero ricevute. |
| Attivazione | `Casazen.Core/Features/FeatureFlags.cs` | AutomaticAlloggiati citato nei commenti non compare nel catalogo. Per decisione PO non introdurre un flag ordinario spento: usare configurazione credenziali/account e solo un kill switch di emergenza auditato. |
| Stati | `Core/Entities/AlloggiatiWebReport.cs` | SentReceiptPending=7 già presente. Valori reali: 0 DaInviare, 2 Inviato, 3 Errore, 4 DaInviareManualmente, 5 Rifiutato, 6 InviatoManualmente; 1 ritirato. L'esempio di ADR-015 è incompatibile. |
| Frontend | `src/types/alloggiati.types.ts`, `features/alloggiati/components/alloggiati-booking-panel.tsx` | Il tipo include Pending, ma il pannello conserva il flusso manuale. Completare azioni, ricevute, errori e delega. |
| Mobile | `src/bookings/booking-compliance.ts`, `src/types/api.ts` | Mappa toni e logica ancora limitate al flusso manuale; allineare i nuovi stati e i testi. |
| Simulatore | `Infrastructure/Services/FakeAlloggiatiServer.cs` | Riproduce le stesse assunzioni del client, accetta ogni riga e inventa protocolli. Non dimostra compatibilità con il servizio. Rimuovere anche logging di username e prefissi WSKEY. |
| Documentazione | `docs/runbooks/alloggiati.md`, ADR-015, Q1-14 | Runbook e prototipo divergono. Riscrivere lo stato effettivo e distinguere simulazione, integrazione disponibile e servizio abilitato. |

## Requisiti implementativi

### AW-R01 — Contratto SOAP e simulatore indipendente · P0

Implementare il binding SOAP (1.1 o 1.2) risultante dal WSDL vigente e verificarlo prima del pilot. Endpoint configurato e validato, namespace e SOAPAction ufficiali; tipi e casing letterali, escaping XML e parser sicuro senza risoluzione di entità esterne. Coprire GenerateToken, Authentication_Test, Test, Send, Ricevuta, Tabella e i metodi GestioneAppartamenti previsti da Q1-14.

Validare esito globale, conteggi e singoli esiti. Risposta vuota, incompleta, malformata o SOAP Fault non significa successo; l'assenza di nodi Errore non basta. Distinguere rifiuto applicativo, token scaduto, errore trasporto ed esito sconosciuto dopo Send. Non fabbricare un protocollo perché il client ne richiede uno.

Il fake deve usare fixture letterali del manuale, controllare anche le richieste e simulare errori globali, per riga, acquisizione parziale, token scaduto e timeout dopo acquisizione. Test di contratto indipendenti dalla serializzazione del client.

Accettazione: richieste e risposte delle operazioni confrontate con fixture ufficiali; un HTTP 200 con esito negativo non produce uno stato di successo.

### AW-R02 — Credenziali, token e delega · P0

Riusare i converter EF e il purpose QuesturaCredentialsPurpose; non eseguire Unprotect dopo una lettura EF. Nessuna credenziale, token, prefisso WSKEY o busta SOAP nei log. Mantenere API di configurazione in sola scrittura.

Modellare account, versione delle credenziali e profilo: account singolo per struttura; account condiviso per Gestione Appartamenti con associazioni esplicite agli immobili. Nessuna selezione implicita di credenziali di un'altra org. Conservare l'identità dell'account usato da un invio anche se la configurazione dell'immobile cambia.

Memorizzare incarico con attore, data, versione del testo e revoca; verificare permesso di trasmissione e ambito dell'immobile. L'host o l'agenzia resta il responsabile dell'adempimento: CasaZen fornisce lo strumento, evidenzia eccezioni e registra le azioni manuali, senza decidere per loro. Revoca o kill switch di emergenza bloccano nuovi invii, ma non il recupero delle ricevute di quelli già acquisiti. Collaboratori senza alloggiati.submit non trasmettono.

L'agenzia inserisce e usa direttamente in CasaZen le proprie credenziali. DPA, termini e testo di configurazione devono chiarire che agenzia/host restano responsabili di correttezza, tempestività, abilitazione Questura e gestione eccezioni; il legale deve validare il riparto senza introdurre una rinuncia inefficace alle responsabilità del fornitore.

Cache del token per org/account/versione, con scadenza restituita dal servizio e margine; invalidare a sostituzione/revoca credenziali. Rinnovo su scadenza o errore di autenticazione esplicito, senza ripetere automaticamente un Send dall'esito ambiguo.

Accettazione: lettura di credenziali cifrate su PostgreSQL e autenticazione con fake; sostituzione invalida il token; altra org non accede a credenziali o ricevute; incarico/documentazione mancante → nessun Send.

### AW-R03 — Righe e profili · P0

Usare il builder verificato e tutte le righe StayGuests, mai il placeholder e mai il solo Guest della prenotazione. Snapshot immutabile con ID e posizione di ogni ospite, versione dei dati e hash del contenuto, collegato al tentativo. Test e Send devono ricevere esattamente lo stesso contenuto.

Validare codici ufficiali, campi, documenti per singolo/capofamiglia/capogruppo, ordine dei componenti, lunghezze, date e copertura di tutti gli ospiti, minori inclusi. Nessun troncamento silenzioso. Riutilizzare le regole del file manuale; variazioni richieste dal contratto SOAP devono essere esplicite e testate.

Gestione Appartamenti: credenziali comuni, ListaAppartamenti, IdAppartamento verificato e associazione immobile/account. Metodi per singolo appartamento e FileUnico secondo il manuale; 168/174 caratteri e padding finale verificati con fixture e successivamente Test reale. Il portale CasaZen deve creare e disabilitare gli appartamenti tramite WS, con conferma esplicita, audit e refresh della lista; fallimenti o esiti incerti non producono retry automatici né modifiche locali implicite. Il pilota copre cinque appartamenti e l'agenzia sceglie profilo singolo o Gestione Appartamenti.

Accettazione: famiglia e gruppo con più ospiti generano una riga per persona; appartamento errato o codice mancante bloccano prima di Send; modifica dati dopo Test invalida la validazione.

### AW-R04 — Invio, concorrenza e stati · P0

Collegare API, servizi, named HttpClient e scheduler; mantenere il percorso manuale. In produzione inviare realmente per ogni utenza dell'agenzia validamente configurata, senza flag ordinario spento; sviluppo, test e demo restano separati e simulati. Prevedere solo un kill switch di emergenza auditato.

Inviare soltanto quando l'arrivo è confermato e nella finestra ammessa; il completamento anticipato del modulo non prova l'arrivo. Il job del giorno d'arrivo prepara il lavoro e rivaluta l'eleggibilità; non deve trasmettere ospiti di prenotazioni cancellate o non arrivati. Gestire arrivo registrato in ritardo e modifiche delle date.

Prima del Send tutte le righe devono aver superato Test. Un errore impedisce l'intero invio volontario. Il servizio può comunque restituire acquisizione parziale: registrare righe acquisite e rifiutate senza reinviare le prime; non classificare l'intero soggiorno come completato. Preservare i gruppi quando si prepara una correzione.

Stati aggiuntivi solo in append: validazione, invio in corso, acquisito/ricevuta in attesa, parziale, esito da verificare se necessari. SentReceiptPending=7 già esiste. Inviato richiede ricevuta privata persistita e associazione valida, non soltanto una stringa ConfirmationNumber. Un simulato non soddisfa il vincolo del completamento reale.

Riservare atomicamente il tentativo prima della chiamata esterna con controllo concorrenza/lease e vincoli univoci. Il solo indice BookingId/GuestId non protegge un Send concorrente. Snapshot acquisiti e tentativi rimangono auditabili.

Nessun secondo Send automatico dopo timeout, connessione interrotta, cancellazione durante la chiamata, crash o fallimento DB dopo possibile acquisizione remota. Persistenza dello stato prima del Send e recupero dei tentativi rimasti in corso come esito da verificare. Disabilitare retry impliciti di Hangfire, HttpClient o policy sul Send. L'agenzia riconcilia con il portale e registra l'azione manuale; CasaZen la evidenzia senza approvarla. Ripetizione solo dopo verifica esplicita e tracciata.

Accettazione: due job concorrenti e API+job producono al massimo un tentativo esterno; crash dopo risposta e prima del commit non genera reinvio; timeout dopo acquisizione mostra da verificare; falso non diventa Inviato reale.

### AW-R05 — Ricevute e conservazione · P0

Recuperare Ricevuta per data e account secondo il contratto, dal giorno successivo e nella finestra disponibile. Non usare intervalli o NumeroRicevuta restituito da Send senza supporto nel contratto.

Introdurre entità ricevuta privata con OrgId, identità account, giorno di trasmissione Europe/Rome, hash PDF, chiave storage, istante di acquisizione e modalità simulata/reale; unicità per account/giorno/modalità. Salvare e verificare il PDF prima di segnare ricevuta disponibile. Non esporre URL pubblici.

Collegare la ricevuta giornaliera agli invii acquisiti di quell'account/giorno; non assumere un PDF per prenotazione né che il PDF dimostri da solo l'acquisizione di ogni schedina. Per i casi parziali o ambigui resta necessaria la riconciliazione. ConfirmationNumber deve diventare un riferimento interno documentato alla ricevuta, senza spacciarlo per protocollo ufficiale.

Job giornaliero idempotente, recupero arretrati e retry delle sole letture; eccezione di un account non ferma gli altri. Avviso persistito e visibile all'host per ricevuta mancante oltre il giorno successivo, anche se altre ricevute sono state recuperate nella stessa esecuzione. Archiviazione per cinque anni e procedura di recupero prima della scadenza della disponibilità remota.

Cancellazione delle immagini secondo decisioni Q1-10/D-07: eliminazione dell'oggetto reale e degli eventuali derivati, con retry e audit; DeletedAt da solo non basta. La conservazione delle ricevute resta distinta da quella delle foto e dei dati ospiti.

Accettazione: PDF recuperabile solo da utenti autorizzati; matching di un altro account/org impossibile; errore storage non produce Inviato; job ripetuto non duplica PDF né notifiche; cancellazione verificata nello storage.

### AW-R06 — Interfacce e avvisi · P0

Host: scelta profilo/account e appartamento, configurazione e verifica credenziali senza invio, delega revocabile, indicazione automatismo attivo o manuale, errori per ospite/campo, stato di acquisizione e stato ricevuta separati, archivio con download autorizzato, esito da verificare con istruzioni operative. Simulazione sempre riconoscibile.

Non presentare invio manuale o nuovo invio come azione ordinaria per comunicazioni già acquisite/in attesa ricevuta. Correzioni dopo acquisizione non devono causare una nuova trasmissione automatica dello snapshot precedente. Mantenere esportazione e dichiarazione manuale per casi appropriati.

Allineare enum, badge, traduzioni, filtri, riepiloghi, scadenze e sessione check-in su backend/frontend/mobile. Avvisi distinti per dati mancanti, errore Test, invio rifiutato/parziale/ambiguo e ricevuta mancante. Nessuna foto o numero documento nei push.

Accettazione: l'host distingue dati pronti, simulazione, acquisizione reale, ricevuta in attesa e ricevuta archiviata; nessun tasto facilita un doppio invio di dati già acquisiti.

### AW-R07 — Termini e indisponibilità · P0

Riusare le scadenze di 24/6 ore da AlloggiatiTerms e gli avvisi CO-10, verificando i termini sulle fonti normative allegate. Distinguere l'istante di arrivo dalla data-only del soggiorno e gestire ora legale Europe/Rome. In assenza dell'orario, mostrare chiaramente la stima prudenziale già usata; non trattare la finestra tecnica oggi/ieri come proroga del termine di legge.

Disponibilità del servizio, credenziali non valide, Test rifiutato o dati incompleti non sospendono l'obbligo: avviso tempestivo e percorso manuale/procedura Questura visibili. Conservare traccia di guasti e azioni dell'operatore senza dati identificativi nei log. Per i casi tardivi non falsificare la data di arrivo per far accettare una schedina.

Accettazione: prove a cavallo di mezzanotte e ora legale, soggiorni brevi e ordinari, invio vicino alla scadenza e indisponibilità; notifiche basate sulla scadenza reale e mai rinviate solo perché un job non è eseguito.

### AW-R08 — Tabelle e provenienza · P1

Confrontare gli allegati CSV con seed esistenti per hash e contenuti, conservando codici come stringhe e zeri iniziali. Registrare origine, data/hash e versione import; gestire comuni soppressi, omonimi e province senza deduzioni ambigue. Non sovrascrivere automaticamente una tabella aggiornata con un allegato più vecchio. Implementare Tabella per i tipi necessari, incluse ListaAppartamenti e TipoErrore quando applicabili; mantenere il refresh pubblico già esistente per le tabelle comuni.

Accettazione: confronto riproducibile; nessun codice ufficiale inventato; parser compatibile con gli allegati e nessuna perdita di zeri.

## Verifica e consegna Helix

| Prova | Esito obbligatorio |
|---|---|
| Contratto ufficiale, errori globali e per riga | Parser e serializer corrispondono al manuale; negative response mai promossa a successo |
| PostgreSQL reale con converter | Credenziali decifrate una sola volta; valori cifrati a riposo |
| Singolo, famiglia, gruppo, nati all'estero, appartamenti | Righe complete, codici e ordine corretti |
| Consenso/flag/arrivo/paese/permessi | Nessuna trasmissione fuori perimetro |
| API+job concorrenti, riavvio, timeout e commit fallito | Nessun reinvio automatico ambiguo |
| Send parziale | Acquisite preservate, rifiutate visibili, nessun completamento totale falso |
| Ricevuta dal giorno successivo, arretrati e storage fallito | Associazione account/giorno corretta, stato solo dopo persistenza |
| Cambio credenziali e appartamento dopo invio | Storico e ricevute restano associati all'account usato |
| Isolamento tra org e ambito immobili | 401/403/404 coerenti e nessun accesso trasversale |
| UI web/mobile, modalità simulata e manuale | Stati e azioni coerenti; nessuna promessa di invio reale in demo |
| Cancellazione immagini e retention ricevute | File immagini eliminati; ricevute conservate e private |

Ordine suggerito: correggere contratto e fake; definire modello account/tentativi/ricevute e migrazioni; riusare builder; implementare orchestrazione idempotente e collegare runtime; completare ricevute; interfacce e avvisi; prove end-to-end e runbook. Ogni PR riporta gli ID AW-Rxx/Q1-14 e il delta verificato, oltre alle configurazioni mancanti.

Definition of done: suite di contratto e integrazione superata; nessun placeholder nel percorso attivabile; migrazioni compatibili con dati storici; documentazione ADR-015 e runbook coerente; nessun Send reale in sviluppo/test/demo; invio reale e ricevute riconciliate sui cinque appartamenti pilota. Questa PR pubblica solo requisiti, non implementa questi cambiamenti.

## Decisioni PO chiuse il 26 ottobre 2026

| ID | Decisione vincolante |
|---|---|
| AW-D1 | L'agenzia o l'host è responsabile dell'invio. CasaZen fornisce lo strumento e non prende la decisione per loro. |
| AW-D2 | Supportare utenza singola e `Gestione Appartamenti`, scelte dall'agenzia; pilota su cinque appartamenti. |
| AW-D3 | L'agenzia usa le proprie credenziali direttamente in CasaZen; DPA/termini chiariscono il riparto delle responsabilità. |
| AW-D4 | Soggiorni oltre 30 giorni: gestione manuale, senza troncamenti o arrivi fittizi. |
| AW-D5 | Aggiunta e disabilitazione appartamenti dal portale CasaZen tramite WS. |
| AW-D6 | Esito ambiguo o mismatch gestito dall'agenzia; CasaZen lo evidenzia e registra l'azione manuale. |

Restano verifiche tecniche, non decisioni di prodotto: il WSDL vigente, limiti per richiesta, padding `IdAppartamento`,
duplicati remoti, ritardi ricevute e limiti di chiamata. Dove il contratto non è esplicito, Helix deve marcare l'assunzione
come non verificata e validarla nel pilot senza trasmettere dati fittizi.
