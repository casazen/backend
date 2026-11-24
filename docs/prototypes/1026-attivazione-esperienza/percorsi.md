# CasaZen #1026 · Percorsi di attivazione

## Passaggi e responsabilità

Lo staff predispone le schede per l’organizzazione e vede quali richiedono una verifica. La creazione è gestita caso per caso. L’host o il PM riceve il lavoro da fare sugli immobili assegnati, con un collegamento al dato preciso da correggere. La pubblicazione sblocca la preparazione della struttura. Quando quella struttura è pronta, CasaZen propone i portali compatibili. Dopo la verifica dei portali richiesti diventano disponibili gli adempimenti applicabili. [Issue #1026 e procedura operativa di Claudia](https://github.com/casazen/backend/issues/1026)

| Percorso | Ingresso e interazione | Attesa, recupero ed esito |
| --- | --- | --- |
| **1. Pubblicare una scheda** | Short Rent → Cruscotto → Da fare, oppure Immobili → Attivazione. L’host apre il campo, confronta le fonti, conferma ciò che conosce e pubblica la scheda pronta. | Gli errori indicano il campo preciso. Indietro conserva i dati già confermati. L’host vede la scheda pubblicata e può seguire la preparazione o lavorare sugli altri immobili. |
| **2. Collegare un portale per immobile** | Immobili → Attivazione o Canali e calendari → dettaglio immobile → catalogo portali. CasaZen mostra i passaggi richiesti dal singolo profilo. | Durante l’autorizzazione si può uscire e riprendere. Dopo il riscontro, CasaZen verifica mapping, prenotazioni, prezzi e disponibilità. Un riscontro parziale resta in verifica. La revoca sospende gli aggiornamenti di quella connessione e conserva lo storico. |
| **3. Collegare più immobili** | Attivazione → seleziona più immobili → portale → scegli l’ambito se disponibile. Per un account condiviso, inserisci una credenziale e indica gli immobili e i riferimenti individuali. | Il riepilogo mostra l’ambito prima dell’avvio. Ogni unità ha il suo esito e l’azione pertinente. |
| **4. Configurare gli adempimenti** | Attivazione → Adempimenti → Alloggiati o ISTAT. Prima scegli l’ambito, poi compili il modulo previsto dal servizio e le associazioni necessarie. | Disponibili dopo i portali richiesti oppure dopo la scelta esplicita «Nessun portale». CasaZen mostra la richiesta, l’eventuale dato da correggere e l’esito per servizio e immobile. |
| **5. Preparare e verificare un lotto** | Super Admin → Processi → Preparazione strutture. Il super admin gestisce il collegamento principale. Lo staff delegato seleziona e avvia il lotto delle strutture pronte. | Le creazioni sono singole via API e la richiesta commerciale è una per lotto. Una risposta incerta porta alla riconciliazione prima del retry. Dopo il riscontro, si registrano soltanto le righe verificate. |
| **6. Consegnare un caso allo staff** | Dalla schermata in cui serve aiuto, «Chiedi a CasaZen» spiega motivo e fonte. Con «Fallo per me» l’host vede e conferma il contesto da trasmettere. | Lo staff apre la stessa pratica, prende in carico o chiede un dato. Da fare riporta al campo richiesto. La risposta e il riscontro finale restano nella stessa pratica. |
| **7. Verificare un’altra integrazione** | Canali e calendari → immobile → Integrazioni. CasaZen mostra i servizi disponibili, l’ambito e le autorizzazioni richieste. | Verifica via API, consenso sul sito partner o richiesta al team per licenza o intervento manuale. Ogni servizio mostra la capacità effettivamente verificata e l’azione sul proprio collegamento. |

## Decisioni del wizard

| Condizione verificata | Comportamento dell’interfaccia |
| --- | --- |
| **ID per proprietà** | Booking.com riusa il Property ID già confermato. L’associazione mancante o dubbia apre la verifica per quel singolo immobile. |
| **Autorizzazione esterna** | Il passo indica il portale, l’immobile e dove riprendere. CasaZen aspetta il riscontro. Scadenza o revoca offrono un’azione per ricollegare. |
| **Credenziale e più ambiti supportati** | Prima scelta tra una o più strutture, poi solo i campi richiesti. Per un account condiviso, i codici restano per unità. |
| **API supportata** | CasaZen salva i campi pertinenti sulla connessione e rilegge l’autorizzazione. Se l’operazione richiede il pannello o una verifica umana, la coda interna contiene struttura, servizio e dati pertinenti. |
| **Rilettura o mapping incerti** | La riga mostra il motivo e salta alla verifica. L’host può continuare con gli altri immobili. Lo staff riprende dallo stesso passaggio. |
| **Calendario con prezzi** | Mantiene i prezzi esistenti. Se mancano, usa una sola fonte autorizzata e associata. |
| **Alloggiati o ISTAT applicabili** | La regione, il paese, il tipo di accesso e i riferimenti per unità determinano i campi. L’eventuale attesa dei portali è per immobile. |
| **Altri servizi** | Controlli riusabili per inventario, messaggi e recensioni, check-in, contatti, documenti, fatture, pagamenti e strumenti esterni. Il servizio determina ambito, direzione, permessi, licenza, metodo di attivazione e verifica. |

## Dati e responsabilità nel passaggio operativo

- **Schede Quokka360.** Il collaudo di import resta l’intero set di 277 annunci. La revisione mostra i quattro dubbi su letti e capienza, il CIN discordante di Romantic Chic Apartment e i tre recuperi da fonti pubbliche da verificare. Le descrizioni originali vanno conservate integralmente con il limite CasaZen adeguato. Le tariffe osservate conservano i piani, le condizioni e le fonti. La tariffa principale può essere modificata senza perdere il dato sorgente.
- **Funzioni AI.** «Chiedi a CasaZen» mostra il motivo con la fonte, suggerisce il passo successivo e, se occorre, prepara il passaggio allo staff. L’host vede e conferma il contesto prima dell’invio. Le richieste espongono riferimenti e stato. L’accesso ai segreti avviene nel vault con il ruolo abilitato e la motivazione.
- **Procedura di Claudia.** La console segue consenso per ambiente, creazione per struttura con `billingNetworkName`, attesa di abbonamento e promo, rilettura di struttura e licenza. Per la connessione portale la sequenza è autorizzazione → `IMPORT_ROOMS` → `pushMapping` → `IMPORT_RESERVATIONS` → `IMPORT_CALENDAR` se mancano i prezzi → `MARK_CONNECTED` e rilettura. L’host vede i controlli in linguaggio CasaZen e la prossima azione. [Procedura completa e contratti API](https://github.com/casazen/backend/issues/1026)

## Scelte di interazione e fonti

- **Ingressi e orientamento.** Navigazione operativa di Short Rent e Console staff, Terracotta e Ametista dalle aree CasaZen, stati dai token semantici. Riferimento visivo al prototipo Stitch, riapplicato ai punti d’ingresso effettivi. [Frontend CasaZen](https://github.com/casazen/frontend/blob/0ca27ec351ccb6854875102c7083bf1c2e76eae1/docs/product/navigazione-operativa.md) · [Studio Google Stitch](https://stitch.google.com/projects/4091929792544570752)
- **Ricerca e progressione.** Ricerca da tastiera ispirata a Command, tab di sezione a Slide Tabs e indicatore a Steps. Mostrano insieme tappa corrente, prossima azione e alternative di recupero. [21st.dev Command](https://21st.dev/@shadcn/components/command) · [Slide Tabs](https://21st.dev/@minhxthanh/components/slide-tabs) · [Steps](https://21st.dev/@adit-firdaus/components/steps/vertical-with-descriptions)
- **Continuità e accessibilità.** Le skill `ui-ux-pro-max`, `ui-styling`, `design`, `brand` e `design-system` hanno guidato raggruppamento, campi condizionali, errori vicino al dato, ritorno al punto di interruzione, focus e movimento ridotto. L’aiuto resta disponibile nello stesso punto, con contesto verificabile. [Aiuto coerente WCAG](https://www.w3.org/WAI/WCAG22/Understanding/consistent-help.html) · [Visibilità dello stato NN/g](https://www.nngroup.com/articles/ten-usability-heuristics/) · [Interazione umano–AI](https://www.microsoft.com/en-us/research/publication/guidelines-for-human-ai-interaction/)

## Prova nel browser

Apri l’HTML e seleziona **Vedi i passaggi** per consultare la mappa del singolo percorso. In ogni passo, **Azioni e destinazioni** indica dove portano le scelte e il ritorno. Usa **Prova il percorso** per partire dall’ingresso reale. La simulazione supporta moduli, filtri, ricerca e cambio di ruolo. Le risposte di portali, AI e servizi esterni sono dimostrative.
