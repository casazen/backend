# #1026 · Attivazione immobili e sincronizzazione Octorate

La issue GitHub definisce il comportamento del prodotto. Lo ZIP Figma descrive sette percorsi su desktop e mobile. I dati, le chiamate e i suggerimenti nello ZIP sono dimostrativi. Il plugin locale è stato letto come sorgente del design. [Issue #1026](https://github.com/casazen/backend/issues/1026)

## Decomposizione

| Blocco | Dipendenze | Consegna e verifica |
| --- | --- | --- |
| Revisione e pubblicazione | Schede CasaZen già create e ruoli per proprietà | Identità sorgente immutabile, gerarchia struttura/unità, provenienza, revisione per campo e un’attività per immobile da aggiornare dopo ogni controllo. Pubblicazione CasaZen indipendente dai portali. Accettazione Quokka360 con 277 identità, 277 testi italiani, 275 inglesi e due proposte da confermare. |
| Piani tariffari | Identità sorgente e corrispondenza delle unità | Acquisire listini, piani, preventivi datati, condizioni e valori da mappare. Conservare le modifiche manuali dopo un nuovo sync. Verificare i tre piani di Lario Suite Brienno e riconciliare le eccezioni del set Quokka prima di abilitare scritture. |
| Account principale e lotto | Perimetri autorizzati, scope Octorate verificati e vault | Consenso e gestione OAuth del super admin per ambiente. Assegnazioni esplicite org/struttura/accommodation. Worker con una creazione per struttura, esito incerto da riconciliare, richiesta commerciale unica per lotto e verifica licenze distinta per riga. Testare esiti parziali, retry, webhook di due org e recupero da Owner Portal. |
| Portale singolo | Struttura e licenza pronte, profilo e autorizzazione del portale | Catalogo nominale e piano dei campi. Riuso della connessione verificata, camere, mapping, prenotazioni, calendario condizionale, conferma e rilettura. Prove con calendario vuoto e prezzato, autorizzazione in attesa, mapping parziale e perdita di risposta. |
| Account condiviso | Percorso portale singolo e vault | Salvare una credenziale per l’ambito scelto, con identificativi e avanzamento per singolo immobile. Per profili a solo ID riusare l’ID verificato dell’immobile. Coprire autorizzazione partner, scadenza, revoca e ripresa, anche con un risultato parziale. |
| Alloggiati e ISTAT | Tutti i portali richiesti sincronizzati per l’immobile, oppure scelta esplicita «Nessun portale» | Moduli singoli e condivisi con codici per unità. Configurazione da pannello tramite staff, vault con audit e riscontro per servizio. Verificare l’applicabilità regionale, la coerenza del raggruppamento e l’avanzamento indipendente delle strutture. |
| Assistenza e integrazioni | Richieste staff e regole di verifica per capacità | Riprendere la stessa pratica dopo la correzione di un campo. Mostrare fonte e azione, confermare la richiesta prima di aprirla. Verificare per struttura permessi, licenza, consenso, revoca e fallback. Testare ricezione e risposta OTA su thread reale autorizzato. |

## Implementazione

- **Schede e tariffe.** Lo staff registra identità sorgente e fonti dalla schermata Processi → Preparazione strutture. L’host vede le verifiche e la relativa attività in Da fare. La scheda conserva descrizioni e traduzioni fino a 10.000 caratteri. Un nuovo import conserva i valori CasaZen già modificati. I preventivi osservati mantengono date, occupazione, valuta e condizioni e restano nel registro sorgente.
- **Preparazione.** I lotti salvano uno stato per struttura, l’ID del fornitore, la conferma commerciale e l’esito della rilettura. Una creazione con esito incerto resta da riconciliare prima di altre scritture. Il job verifica prima l’ambiente dichiarato dal fornitore e assegna ciascuna struttura verificata a una sola organizzazione.
- **Portali.** Il passo prima del mapping chiede solo le camere. Il salvataggio invia le coppie mancanti o modificate e le rilegge. L’attivazione chiede le prenotazioni, riusa l’importatore CasaZen, verifica il calendario e rilegge la connessione dopo il PATCH.
- **Account e adempimenti.** L’host sceglie uno o più immobili, salva una credenziale cifrata con codici individuali e vede l’esito per immobile. Gli adempimenti si sbloccano dopo i portali richiesti o con la scelta esplicita «Nessun portale». La conferma riusa «Fallo per me», la coda staff e l’apertura con motivo e audit. Le credenziali scadono dopo sette giorni e possono essere revocate.
- **Frontend.** La sezione Immobili → Attivazione mostra gli immobili autorizzati, revisione, portali, preparazione e adempimenti. Il layout usa elenco e dettaglio affiancati su desktop e una vista di dettaglio su mobile.

### Preparare il file Quokka

Lo staff verifica prima la corrispondenza tra schede CasaZen e percorsi Quokka. Il file di associazione è un array con `orgId`, `propertyId`, `sourcePath` e, quando applicabili, `parentSourceId`, `findings` e `unmappedFields`. L’identità Kross è riletta dai dati strutturati della pagina.

```sh
python3 scripts/quokka-activation-import.py --mapping quokka-mapping.json --output quokka-import.json --from 2026-12-10 --to 2026-12-12
```

Il comando scrive le righe riuscite e segnala le eccezioni per percorso. Processi → Preparazione strutture accetta il file prodotto. Le date e l’occupazione del preventivo sono parametri della verifica.

### Attivare il test su Railway

- Il merge su `develop` pubblica il backend su Railway test e il frontend su Vercel Preview. Il backend espone i flag correnti in `GET /api/public/features`.
- Sul servizio Railway **test**, impostare `Octorate__MasterOrganizationId` con l’ID dell’organizzazione CasaZen del super admin e `Octorate__BillingNetworkName` con la rete confermata per **SANDBOX**. Il super admin completa il consenso OAuth nel contesto di quell’organizzazione. La configurazione condivisa richiede credenziali della sandbox per test e credenziali di produzione per produzione.
- Dopo la revisione e la pubblicazione, lo staff avvia il lotto, registra il riferimento della richiesta commerciale e riprende le verifiche. Le strutture con riscontro commerciale e prodotto valido passano a «Pronta per i collegamenti».
- Il collaudo live richiede un immobile autorizzato per ogni profilo OTA, almeno un thread per la prova messaggi e scope concordati con Octorate. I flag D6 si abilitano per le operazioni effettivamente confermate.

## Configurazione e verifica del pilota

- La sitemap pubblica Quokka360 restituisce 278 URL distinti, inclusa la home. I 277 percorsi rimanenti definiscono il perimetro da riconciliare con le identità sorgente. La verifica di persistenza di testi e piani richiede il dataset di importazione. [Sitemap Quokka360](https://quokka.kross.travel/sitemap.xml)
- Le capacità D6 restano disattivate per impostazione predefinita. Configurare `Octorate:ExistingInventory:Channels:{codice}` solo dopo il riscontro sul singolo portale. `CommitMapping` è una capacità separata.
- La sorgente iniziale del calendario si indica per ambiente con `Octorate:ExistingInventory:InitialCalendarPortals:{accommodationId}`, valore codice portale. Il wizard controlla 30 giorni per ciascun prodotto mappato come evidenza iniziale. Prezzi già osservati o dati incompleti impediscono un import distruttivo.
- Il tracciato del pilota deve provare l’effettiva autorizzazione sul portale, la semantica dell’azione asincrona sulle prenotazioni e i riscontri di mapping e calendario. Il mock prova l’ordine delle chiamate e la ripresa applicativa. Il riscontro con account, licenza e strutture reali resta da registrare. [Runbook del pilota](../runbooks/octorate-existing-inventory-import.md)
