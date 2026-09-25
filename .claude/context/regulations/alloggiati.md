# Comunicazione Alloggiati Web

> Aggiornato: 2026-09-23 (task RS-1). Le sezioni "Specifiche tecniche verificate (2026-09)" e
> "Verdetto per CO-13" sostituiscono le affermazioni tecniche precedenti dove sono in contrasto.

## Riferimento Normativo
- **Fonte primaria**: Art. 109 TULPS (R.D. 18/06/1931 n. 773), comma 3 nel testo in vigore dal 10/08/2019
  (modificato dall'art. 5 del D.L. 14/06/2019 n. 53, convertito dalla L. 08/08/2019 n. 77)
- **Decreto attuativo**: D.M. Interno 07/01/2013 (GU 17/01/2013), **modificato e integrato dal D.M. Interno 16/09/2021**
  (GU Serie Generale n. 246 del 14/10/2021, in vigore 90 giorni dopo la pubblicazione, quindi dal 12/01/2022), con **Allegato tecnico**
- **Portale**: Alloggiati Web (Polizia di Stato), `https://alloggiatiweb.poliziadistato.it`

## Sintesi dell'Obbligo

Tutti i gestori di strutture ricettive, compresi i locatori e sublocatori con contratti di durata inferiore a 30 giorni (locazioni brevi), hanno l'**obbligo di comunicare** alla Questura i dati degli ospiti.

### Caratteristiche Principali
- **Termine**: entro **24 ore dall'arrivo**; **entro 6 ore dall'arrivo** se il soggiorno non supera le 24 ore (vedi "Tempistiche di legge")
- **Soggetti obbligati**: gestori di strutture ricettive, B&B, affittacamere, case vacanze, locazioni turistiche e brevi
- **Dati richiesti**: dati anagrafici di tutti gli ospiti, compresi i minori. Il **documento** si trasmette solo per ospite singolo, capofamiglia e capogruppo, non per familiari e membri del gruppo (D.M. 16/09/2021, Allegato tecnico)
- **Sanzioni**: **penali** (non solo amministrative). *Non riverificato in RS-1.*

## Modalità di Comunicazione

### Piattaforma Nazionale: Alloggiati Web
La comunicazione avviene tramite il portale **Alloggiati Web** della Polizia di Stato. L'Allegato tecnico del D.M. 16/09/2021 prevede **tre canali**:
1. inserimento online di una schedina alla volta;
2. invio di un **file di testo** (tracciato record);
3. invio tramite **web service** (cooperazione applicativa con il gestionale della struttura).

**Accesso:**
- Credenziali rilasciate dalla Questura competente per territorio, su richiesta del gestore (modulo di abilitazione)
- Login con utente che identifica la struttura, password modificabile e codice OTP valido per la singola sessione (Allegato tecnico D.M. 16/09/2021)
- Chi gestisce **due o più appartamenti** può chiedere un profilo **"Gestione Appartamenti"**: un'unica utenza con cui aggiungere o rimuovere appartamenti senza chiedere nuove credenziali

**Dati da comunicare** (D.M. 16/09/2021, Allegato tecnico):
- Ospite singolo, capofamiglia, capogruppo: data di arrivo, numero giorni di permanenza, cognome, nome, sesso, data di nascita, luogo di nascita (comune e provincia se in Italia, stato se all'estero), cittadinanza, tipo documento, numero documento, luogo di rilascio (comune e provincia se in Italia, stato se all'estero)
- Familiari e membri del gruppo: gli stessi dati **senza** i tre campi del documento

> **Correzione rispetto alla versione precedente**: la **residenza non si trasmette più**. Dal 23 aprile (l'anno non si ricava dall'estratto consultato) il modulo online non accetta più i dati di residenza e il "numero giorni di permanenza" è diventato obbligatorio (Questura di Siena). La "data di partenza prevista" si esprime come **numero di giorni di permanenza (massimo 30)**. La **scadenza del documento** non fa parte del tracciato.

### Piattaforme regionali: da non confondere
La versione precedente di questo file indicava portali regionali (Toscana, Veneto, Puglia) "che sostituiscono o integrano Alloggiati Web". **Nessuna fonte ufficiale consultata in RS-1 lo conferma.** Il D.M. 07/01/2013, come modificato nel 2021, indica solo i canali del portale Alloggiati. Quei portali riguardano con ogni probabilità i flussi statistici ISTAT o l'imposta di soggiorno, che sono obblighi **distinti**. Informazione non verificata: non usarla per il design.

## Scadenze e Tempistiche
- **24 ore dall'arrivo**: termine ordinario
- **6 ore dall'arrivo**: per soggiorni non superiori alle 24 ore
- **Check-out**: non si comunica la partenza. In caso di **proroga** del soggiorno si reinserisce la schedina come nuovo arrivo, indicando i giorni aggiuntivi (Questura di Siena)

## Sanzioni
*Sezione ereditata, non riverificata in RS-1 (fuori perimetro).*
- **Omessa comunicazione**: sanzione **penale** (contravvenzione)
- **Comunicazione tardiva**: sanzione penale
- **Dati incompleti o errati**: sanzione amministrativa/penale

Le sanzioni sono **personali** (a carico del gestore) e non possono essere delegate.

---

## Specifiche tecniche verificate (2026-09)

### Metodo e limiti della verifica (leggere prima di usare i dati)
- **Data di consultazione di tutte le fonti: 2026-09-23.**
- Dalla sessione di ricerca il proxy di egress **ha bloccato l'accesso diretto** a `alloggiatiweb.poliziadistato.it`, `questure.poliziadistato.it`, `gazzettaufficiale.it` e `normattiva.it` (HTTP 403 "EGRESS_BLOCKED"). **Non è stato possibile scaricare e leggere per intero** i PDF ufficiali né il WSDL.
- I fatti qui riportati vengono dagli **estratti indicizzati delle pagine ufficiali**, letti tramite motore di ricerca con filtro sui soli domini ufficiali. Livelli di verifica usati:
  - **U**: estratto da fonte ufficiale (URL indicato);
  - **D**: dedotto (per esempio per somma delle lunghezze o per coerenza tra fonti ufficiali), **non letto** nel documento;
  - **T**: indicato solo da fonti terze (software house, blog). È un indizio, **non una fonte**.
- **Prerequisito di CO-13**: prima di scrivere codice, scaricare da una rete non filtrata `CREAFILE.pdf`, `MANUALEALBERGHI.pdf`, il manuale WS e `Service.asmx?wsdl`, poi confermare ogni riga marcata **D** o **T**.

### Documentazione ufficiale pubblica (senza login)
| Documento | URL | Note |
|---|---|---|
| Guida al servizio (strutture) | https://alloggiatiweb.poliziadistato.it/portalealloggiati/Download/Manuali/MANUALEALBERGHI.pdf | U: contiene il tracciato (168 caratteri) e il File Unico "Gestione Appartamenti" |
| Invio File (tracciato record) | https://alloggiatiweb.poliziadistato.it/PortaleAlloggiati/Download/Manuali/CREAFILE.pdf | U: specifica il file .txt |
| Manuale web service "Documento di Descrizione WS_ALLOGGIATI", Rev. 01 del 13/01/2022, 21 pagine | https://questure.poliziadistato.it/statics/13/manualewebsercices_alloggiatiweb.pdf?lang=it | U: copia pubblicata da una Questura. Sul portale è segnalato anche `.../PortaleAlloggiati/Download/Manuali/MANUALEWS.pdf` (URL non verificato direttamente) |
| Ricevuta digitale | https://alloggiatiweb.poliziadistato.it/portalealloggiati/Download/Info/RICDIGALLO.pdf | U |
| Elenco manuali | https://alloggiatiweb.poliziadistato.it/PortaleAlloggiati/SupManuali.aspx | U |
| Area download tabelle | https://alloggiatiweb.poliziadistato.it/portalealloggiati/tabelle.aspx | U |
| Art. 109 TULPS (testo in vigore dal 10/08/2019) | https://alloggiatiweb.poliziadistato.it/PortaleAlloggiati/Download/Normativa/109_TULPS.pdf | U |
| D.M. 16/09/2021 e Allegato tecnico | https://www.gazzettaufficiale.it/eli/id/2021/10/14/21A06000/sg | U (GU SG n. 246 del 14/10/2021) |
| D.M. 07/01/2013 | https://www.gazzettaufficiale.it/eli/id/2013/01/17/13A00360/sg | U |

> Alcune Questure pubblicano copie **più vecchie** dei manuali, per esempio `questure.poliziadistato.it/statics/08/manuale-alloggiati.pdf` e `.../statics/41/manuale-alloggiati.pdf`, con un tracciato di **236 caratteri**. È quasi certamente il tracciato **precedente**, con residenza e senza giorni di permanenza (D). Va usata **solo** la versione pubblicata sul portale `alloggiatiweb.poliziadistato.it`.

### 1. Tracciato record della schedina (file .txt e web service)

**Regole generali**
- Una riga per ospite, **168 caratteri** per riga, campi a lunghezza fissa riempiti con spazi (U, CREAFILE.pdf e MANUALEALBERGHI.pdf).
- Righe separate da **CR+LF**; dopo l'ultima riga **non** si aggiunge CR+LF (U, CREAFILE.pdf).
- Date nel formato **`gg/mm/aaaa`**, per esempio `16/02/2005` (U, per la data di arrivo; D, per la data di nascita).
- Niente simboli speciali (U, ma solo da una versione del manuale). **La codifica dei caratteri (ASCII, Windows-1252, ...) non risulta dagli estratti ufficiali**: da verificare. Le fonti terze parlano di ANSI/Windows-1252 e di lettere accentate sostituite dalla lettera base (T).
- Nel web service ogni elemento di `ElencoSchedine` è una stringa che rispetta **lo stesso tracciato** (U, manuale WS).

**Campi** (posizioni 0-based come nel manuale. Le posizioni sono **D**, ricostruite sommando le lunghezze, e vanno confermate su CREAFILE.pdf)

| # | Campo | Pos. | Lung. | Formato / note | Verifica |
|---|---|---|---|---|---|
| 1 | Tipo alloggiato | 0–1 | 2 | codice dalla tabella Tipo Alloggiato | U |
| 2 | Data arrivo | 2–11 | 10 | `gg/mm/aaaa` | U |
| 3 | Numero giorni di permanenza | 12–13 | 2 | massimo 30, obbligatorio | U (max 30, obbligatorio); D (lunghezza e posizione) |
| 4 | Cognome | 14–63 | 50 | testo, riempito con spazi | U (lunghezza); D (posizione) |
| 5 | Nome | 64–93 | 30 | testo, riempito con spazi | U (lunghezza); D (posizione) |
| 6 | Sesso | 94 | 1 | `1` = maschio, `2` = femmina | U |
| 7 | Data di nascita | 95–104 | 10 | `gg/mm/aaaa` | D |
| 8 | Comune di nascita | 105–113 | 9 | codice dalla tabella Comuni, **solo se nato in Italia** | U |
| 9 | Provincia di nascita | 114–115 | 2 | sigla di targa (Roma = `RM`), **solo se nato in Italia** | U |
| 10 | Stato di nascita | 116–124 | 9 | codice dalla tabella Stati (anche per i nati in Italia) | U |
| 11 | Cittadinanza | 125–133 | 9 | codice dalla tabella Stati | U |
| 12 | Tipo documento | 134–138 | 5 | codice dalla tabella Documenti | U |
| 13 | Numero documento | 139–158 | 20 | riempito con spazi fino a 20 | U |
| 14 | Luogo di rilascio documento | 159–167 | 9 | codice comune (se in Italia) o stato (se all'estero) | U |
| | **Totale** | | **168** | | U |

- **Familiari e membri del gruppo**: al posto dei campi 12–14 vanno **34 spazi** (5+20+9), per arrivare comunque a 168 caratteri (U, CREAFILE.pdf).
- Per i nati all'estero i campi 8 e 9 si presume vadano lasciati a spazi (D).
- **Profilo "Gestione Appartamenti" (File Unico)**: in fondo a ogni riga si aggiunge **IDAPPARTAMENTO di 6 caratteri** (U), quindi 174 caratteri totali (D). L'ID viene dall'anagrafica appartamenti del portale; nel web service c'è `Tabella` / `ListaAppartamenti` (D).
- Nel file, le righe dei familiari e dei membri probabilmente devono seguire quella del rispettivo capofamiglia o capogruppo. **Non verificato**: da confermare su CREAFILE.pdf.

**Tipi alloggiato**
- Categorie ufficiali (U): **Ospite singolo**, **Capo famiglia**, **Capo gruppo**, **Familiare**, **Membro gruppo**. Capofamiglia e capogruppo richiedono l'inserimento delle schedine collegate. Singolo, capofamiglia e capogruppo richiedono sempre il documento.
- **Codici numerici: NON verificati su fonte ufficiale.** Molte fonti terze (T) indicano 16 = Ospite singolo, 17 = Capo famiglia, 18 = Capo gruppo, 19 = Familiare, 20 = Membro gruppo, ma altre fonti terze sono **discordanti**. CO-13 deve leggerli dalla tabella ufficiale `TIPO_ALLOGGIATO` o dal web service `Tabella(Tipi_Alloggiato)` e **non codificarli a mano senza verifica**.

**Vincoli applicativi del portale**
- Il portale accetta come **data di arrivo solo la data odierna o quella del giorno precedente** (U, manuali e FAQ delle Questure). Conseguenze: **non si può inviare prima del giorno di arrivo** e un invio oltre il giorno successivo viene rifiutato.
- **Permanenza massima 30 giorni** per schedina. Per una proroga si invia una nuova schedina come nuovo arrivo (U, Questura di Siena).

### 2. Tabelle codici (comuni, stati/cittadinanze, tipi documento, luoghi di rilascio)
- **Dove**: pagina pubblica "Area Download Tabelle", https://alloggiatiweb.poliziadistato.it/portalealloggiati/tabelle.aspx (U), con download diretti:
  - Comuni: `https://alloggiatiweb.poliziadistato.it/portalealloggiati/ashx/Download.ashx?ID=0&N=COMUNI`
  - Stati (nascita, cittadinanza, luogo di rilascio estero): `...Download.ashx?ID=1&N=STATI`
  - Tipi documento: `...Download.ashx?ID=2&N=DOCUMENTI`
  - Tipi alloggiato: `...Download.ashx?ID=3&N=TIPO_ALLOGGIATO`
- **Accesso**: pagina e link di download sono **indicizzati pubblicamente** dai motori di ricerca, sotto il titolo "Tabella Comuni – Elenco dei comuni…" e simili. Quindi **sono scaricabili senza login** (D, deduzione forte: un crawler non può autenticarsi). Le stesse tabelle si ottengono anche dal web service `Tabella`, che richiede token e quindi credenziali (U).
- **Luoghi di rilascio**: non esiste una tabella a parte. Si usa il codice **comune** se il rilascio è in Italia e il codice **stato** se è all'estero (U, D.M. 16/09/2021, Allegato tecnico). Nel web service la tabella `Luoghi` unisce comuni e stati (D).
- **Formato dei file scaricati: non verificato** (download bloccato in questa sessione). Il web service `Tabella` restituisce una stringa **CSV** (U, pagina `service.asmx?op=Tabella`).
- **Istruzioni per CO-12 e CO-13**: **non** committare le tabelle nel repository. Vanno importate a runtime, con un job o un caricamento da amministratore, dagli URL sopra o dal web service `Tabella`, conservando la versione e la data di importazione. I codici comuni cambiano nel tempo (fusioni, soppressioni), quindi serve un aggiornamento periodico.

### 3. Web service
- **Esiste un web service pubblico SOAP**, previsto dal D.M. 16/09/2021: "cooperazione applicativa" tra il gestionale della struttura e Alloggiati, basata su **SOAP** (U, Allegato tecnico).
- **Endpoint**: `https://alloggiatiweb.poliziadistato.it/service/Service.asmx` (ASMX .NET, SOAP 1.1 e 1.2). **WSDL**: `https://alloggiatiweb.poliziadistato.it/service/Service.asmx?wsdl` (U, pagine indicizzate). Namespace e SOAPAction nella forma `AlloggiatiService/<Operazione>`, per esempio `AlloggiatiService/Tabella` (U, pagina `?op=Tabella`).
- **Autenticazione** (U, manuale WS e Allegato tecnico):
  1. il gestore si registra al portale con le credenziali rilasciate dalla Questura;
  2. nel portale genera la **WSKEY** (menu profilo in alto a destra, voce "Web Service Key"). È una stringa alfanumerica univoca, **revocabile e rigenerabile** dalla struttura; se ne può generare **una sola al giorno** e **va rigenerata a ogni cambio password**;
  3. `GenerateToken(Utente, Password, WsKey)` restituisce `TokenInfo` (emissione, scadenza, token). Il token ha **validità temporanea**, ma la durata non risulta dagli estratti;
  4. le operazioni successive usano `Utente` e `token`.
  - L'OTP serve per il login al portale; `GenerateToken` non ha un parametro OTP (D).
- **Operazioni osservate** (pagine `service.asmx?op=...` indicizzate e indice del manuale WS):

  | Operazione | Parametri principali | Scopo | Verifica |
  |---|---|---|---|
  | `GenerateToken` | Utente, Password, WsKey | genera il token temporaneo | U |
  | `Authentication_Test` | Utente, token | controllo del token ("Controllo del Token di Autenticazione") | U (funzione); D (nome esatto) |
  | `Test` | Utente, token, ElencoSchedine | **controlla la correttezza** delle schedine **senza inviarle** | U |
  | `Send` | Utente, token, ElencoSchedine | **invio** delle schedine | U |
  | `Ricevuta` | Utente, token, Data | scarica la **ricevuta PDF** (base64) di una data | U |
  | `Tabella` | Utente, token, tipo (`Luoghi`, `Tipi_Documento`, `Tipi_Alloggiato`, `TipoErrore`, forse `ListaAppartamenti`) | scarica una tabella in **CSV** | U (Luoghi, Tipi_Documento, Tipi_Alloggiato, TipoErrore); D (ListaAppartamenti) |
  | `GestioneAppartamenti_*` (per esempio `GestioneAppartamenti_AggiungiAppartamento`) | — | operazioni del profilo Gestione Appartamenti (anagrafica appartamenti, invio File Unico) | U (esistenza di AggiungiAppartamento); D (le altre) |

- **Esiti**: strutture `EsitoOperazioneServizio` (esito ed errore con dettaglio `ErroreDettaglio`) ed `ElencoSchedineEsito` (`SchedineValide` più un `Dettaglio` per ogni schedina) (U, indice del manuale WS e pagine `op=Test` e `op=Send`).
- **Ambiente di test**: **nessun ambiente di test o sandbox separato risulta documentato** nelle fonti consultate. L'unico strumento è il metodo `Test`, che valida senza inviare ma richiede **credenziali reali** di una struttura.
- **Non determinato dalle fonti consultate**: durata del token, numero massimo di schedine per chiamata, limiti di frequenza, codici di errore (ottenibili con `Tabella(TipoErrore)`).
- **Contratti e accreditamenti**: l'Allegato tecnico richiede solo "la preventiva registrazione dell'utente sul portale web e la richiesta di generazione delle chiavi di autorizzazione" (U). **Nessuna fonte consultata prevede** accreditamenti, convenzioni o contratti per il fornitore del software.

### 4. Ricevuta
- La ricevuta è un **PDF firmato digitalmente** dalla Polizia di Stato, con QR code di controllo. È emessa **il giorno successivo** all'invio e resta scaricabile online per **30 giorni**. Il gestore la deve **conservare per 5 anni** (U, RICDIGALLO.pdf).
- Via web service si scarica con `Ricevuta(Utente, token, Data)`.
- Conseguenza per CasaZen, coerente con D6 in DECISIONI.md: dopo un `Send` con esito positivo lo stato è "inviato, ricevuta in attesa". Si passa a "ricevuta acquisita" solo dopo aver scaricato la ricevuta del giorno di invio.

### 5. Tempistiche di legge
- **Art. 109, comma 3, TULPS** (testo in vigore dal 10/08/2019): comunicazione **entro le 24 ore successive all'arrivo** "e comunque **entro le sei ore successive all'arrivo** nel caso di soggiorni non superiori alle ventiquattro ore". L'inciso è stato inserito dall'art. 5 del D.L. 53/2019, come convertito dalla L. 77/2019 (U, 109_TULPS.pdf del portale e copia della Questura).
- Il **D.M. 07/01/2013**, nel testo originario, parla di trasmissione "entro le ventiquattro ore successive all'arrivo … e comunque **all'arrivo stesso** per soggiorni inferiori alle ventiquattro ore" (U). Prevale la norma di legge successiva (6 ore), ma **per prudenza CasaZen deve inviare subito all'arrivo** i soggiorni non superiori alle 24 ore.
- Implementazione (CO-11): l'arrivo si calcola in **Europe/Rome**. La scadenza è arrivo + 24h, oppure arrivo + 6h se il soggiorno non supera le 24 ore. L'invio è possibile solo dal giorno di arrivo in poi (vincolo del portale sulla data di arrivo).

### 6. Punti aperti fuori perimetro RS-1 (segnalati, non verificati)
- **Identificazione "de visu"**: la circolare del Ministero dell'Interno del 18/11/2024 (n. 38138, copia su `questure.poliziadistato.it/statics/48/circolare---identificazione-delle-persone-ospitate-presso-strutture-ricettive.pdf`) ritiene insufficiente il check-in solo da remoto. Il TAR Lazio l'ha annullata (sent. 10210/2025, T). Il **Consiglio di Stato, Sez. III, sent. n. 9101 del 21/11/2025** (pagina su giustizia-amministrativa.it: "Obbligo di identificazione de visu delle persone alloggiate a carico del gestore di strutture ricettive") ha riformato la sentenza del TAR (riforma: T; esistenza e oggetto della sentenza: U). Secondo fonti terze (T) l'identificazione è ammessa anche **a distanza ma in tempo reale**. Il tema riguarda direttamente il **self check-in** di CasaZen e richiede una verifica legale dedicata.

---

## Verdetto per CO-13

**Verdetto: (b), web service integrabile senza contratto.** Ne segue anche (a): il web service accetta le stesse stringhe del tracciato file, che è pubblico.

Motivazione (fonti ufficiali):
- l'Allegato tecnico del D.M. 16/09/2021 prevede il web service SOAP per i gestionali e richiede **solo** la registrazione dell'utente al portale e la generazione delle chiavi. I manuali tecnici sono pubblicati sul portale;
- endpoint, WSDL e manuale WS (Rev. 01 del 13/01/2022) sono pubblici, così come il tracciato record (CREAFILE.pdf, MANUALEALBERGHI.pdf) e le tabelle codici (area download pubblica, oppure `Tabella` via web service).

Condizioni e limiti da rispettare in CO-13:
1. **Verifica preliminare obbligatoria** sui documenti originali delle righe marcate D o T: posizioni dei campi, codici tipo alloggiato, codifica caratteri, ordine delle righe famiglia/gruppo, operazioni `GestioneAppartamenti_*`. In questa sessione erano **bloccati dal proxy**.
2. **Credenziali per host**: ogni host deve avere la **propria** utenza della Questura e generare la **propria WSKEY**. CasaZen non può inviare con un'utenza sua. Deve conservare utente, password e WSKEY di ogni host **cifrati** e gestire il rinnovo della WSKEY a ogni cambio password dell'host. È consigliato un runbook `docs/runbooks/alloggiati.md` per l'onboarding dell'host.
3. **Due profili**: struttura singola (168 caratteri) e "Gestione Appartamenti" (più IDAPPARTAMENTO, 174 caratteri).
4. **Nessuna sandbox**: i test automatici devono simulare il SOAP. La verifica end-to-end richiede credenziali reali di un host, usando `Test` prima di `Send`.
5. **Stato onesto**: "Inviato" solo con esito positivo di `Send`; "Ricevuta" solo con il PDF scaricato da `Ricevuta` (disponibile dal giorno dopo).
6. **Fallback**: generare il file .txt che l'host carica manualmente sul portale, per host senza WSKEY o in caso di errori del web service.

---

## Impatto su CasaZen

### Funzionalità Coinvolte

1. **Guest Management**
   - Raccolta dei dati ospite richiesti dal tracciato: cognome, nome, sesso, data e luogo di nascita (comune e provincia oppure stato), cittadinanza. Per singolo, capofamiglia e capogruppo anche tipo, numero e luogo di rilascio del documento.
   - Numero di giorni di permanenza (massimo 30), ricavato dal soggiorno
   - La residenza **non** è richiesta da Alloggiati. La scadenza del documento non è nel tracciato: se viene raccolta serve per altri scopi, da valutare in ottica GDPR
   - Validazione della completezza dei dati prima dell'arrivo; codici luogo e documento dalle tabelle ufficiali importate

2. **Check-In Digitale**
   - Self check-in ospiti via web o app mobile. **Attenzione** all'obbligo di identificazione "de visu" (vedi punto aperto 6)
   - Compilazione dei dati Alloggiati da parte dell'ospite
   - Raccolta consenso privacy (GDPR)

3. **Integrazione Alloggiati Web** (vedi Verdetto CO-13)
   - Client SOAP verso `Service.asmx`: `GenerateToken`, `Test`, `Send`, `Ricevuta`, `Tabella`
   - Mapping dei dati CasaZen sul tracciato record a 168 o 174 caratteri
   - Gestione cifrata delle credenziali Questura e della WSKEY per ogni host
   - Esito dell'invio e download della ricevuta PDF (conservazione 5 anni)
   - Fallback: file .txt da caricare manualmente

4. **Compliance Dashboard**
   - Avvisi per ospiti con dati incompleti (prima dell'arrivo)
   - Confronto tra comunicazioni effettuate e prenotazioni
   - Scadenza evidenziata: 24h, oppure 6h per soggiorni non superiori alle 24 ore
   - Report delle comunicazioni mancanti o in ritardo

5. **Comunicazione ISTAT (separata)**
   - Oltre ad Alloggiati Web esiste un obbligo **separato** di comunicazione ISTAT, spesso tramite portali regionali
   - Dati aggregati (arrivi, presenze, nazionalità); scadenza variabile per regione. *Non verificato in RS-1.*
   - **Verificato in RS-9 per la Lombardia** (`istat-flussi-turistici.md`): il canale è Ross1000, con scadenza il giorno 5 del mese successivo. I dati **non** sono aggregati: sono record per ospite e per giorno, codificati con le stesse tabelle Comuni, Nazioni e Tipi Alloggiato di Alloggiati Web. Le tabelle importate per CO-12 e CO-13 vanno quindi riusate.

### Criticità Tecniche
- **Accesso ai documenti ufficiali**: in RS-1 il proxy di egress ha bloccato i domini ufficiali. CO-13 deve partire dalla lettura integrale dei PDF e del WSDL
- **Nessuna sandbox ufficiale**: collaudo solo con credenziali reali e metodo `Test`
- **Vincolo sulla data di arrivo**: il portale accetta solo oggi o ieri, quindi niente invii anticipati e niente recupero tardivo automatico
- **Dati incompleti**: gli ospiti potrebbero non fornire i dati in tempo
- **Minori**: la comunicazione è obbligatoria anche per loro. Di solito sono familiari o membri del gruppo, quindi senza documento
- **Responsabilità penale**: resta personale del gestore; CasaZen è solo uno strumento

### Dati da Tracciare
Per ogni ospite:
- Dati del tracciato (vedi sopra) e ruolo nel soggiorno (singolo, capofamiglia, capogruppo, familiare, membro)
- Data e ora dell'invio, esito `Send` per schedina, eventuale errore
- Ricevuta PDF del giorno (riferimento e hash), conservata 5 anni

Per ogni proprietà o host:
- Utente Alloggiati, password e WSKEY cifrate; data dell'ultima rigenerazione della WSKEY
- Profilo (struttura singola o Gestione Appartamenti) e IDAPPARTAMENTO

## Workflow Ottimale
1. **Pre-arrivo** (3-7 giorni prima): invio all'ospite del link per il check-in digitale e raccolta dei dati
2. **Pre-arrivo** (1 giorno prima): promemoria se i dati sono incompleti; avviso al proprietario se mancano dati
3. **Giorno di arrivo** (Europe/Rome): `Test`, poi `Send` delle schedine. Per soggiorni non superiori alle 24 ore l'invio va fatto subito, **massimo 6 ore**. In caso di errore: avviso e fallback manuale (file .txt o portale)
4. **Entro 24h dall'arrivo**: monitoraggio delle comunicazioni pendenti, avviso urgente vicino alla scadenza
5. **Giorno successivo all'invio**: download della ricevuta PDF con `Ricevuta` e passaggio allo stato "ricevuta acquisita"

## Sorgenti

### Ufficiali (consultate il 2026-09-23 tramite estratti indicizzati; accesso diretto bloccato dal proxy)
- [Alloggiati Web – Guida al servizio (MANUALEALBERGHI.pdf)](https://alloggiatiweb.poliziadistato.it/portalealloggiati/Download/Manuali/MANUALEALBERGHI.pdf)
- [Alloggiati Web – Invio File (CREAFILE.pdf)](https://alloggiatiweb.poliziadistato.it/PortaleAlloggiati/Download/Manuali/CREAFILE.pdf)
- [Documento di Descrizione WS_ALLOGGIATI Rev. 01 13/01/2022](https://questure.poliziadistato.it/statics/13/manualewebsercices_alloggiatiweb.pdf?lang=it)
- [Service.asmx](https://alloggiatiweb.poliziadistato.it/service/service.asmx) · [WSDL](https://alloggiatiweb.poliziadistato.it/service/service.asmx?wsdl) · [op=GenerateToken](https://alloggiatiweb.poliziadistato.it/service/Service.asmx?op=GenerateToken) · [op=Test](https://alloggiatiweb.poliziadistato.it/service/service.asmx?op=Test) · [op=Send](https://alloggiatiweb.poliziadistato.it/service/service.asmx?op=Send) · [op=Ricevuta](https://alloggiatiweb.poliziadistato.it/service/service.asmx?op=Ricevuta) · [op=Tabella](https://alloggiatiweb.poliziadistato.it/service/service.asmx?op=Tabella)
- [Area Download Tabelle](https://alloggiatiweb.poliziadistato.it/portalealloggiati/tabelle.aspx)
- [La ricevuta digitale (RICDIGALLO.pdf)](https://alloggiatiweb.poliziadistato.it/portalealloggiati/Download/Info/RICDIGALLO.pdf)
- [Art. 109 TULPS, testo in vigore dal 10/08/2019 (portale)](https://alloggiatiweb.poliziadistato.it/PortaleAlloggiati/Download/Normativa/109_TULPS.pdf) · [copia Questura](https://questure.poliziadistato.it/statics/43/art.-109-tulps.pdf?lang=it)
- [D.M. 16/09/2021 – GU SG n. 246 del 14/10/2021](https://www.gazzettaufficiale.it/eli/id/2021/10/14/21A06000/sg) · [Allegato tecnico](https://www.gazzettaufficiale.it/atto/serie_generale/caricaArticolo?art.versione=1&art.idGruppo=0&art.flagTipoArticolo=1&art.codiceRedazionale=21A06000&art.idArticolo=1&art.idSottoArticolo=1&art.idSottoArticolo1=10&art.dataPubblicazioneGazzetta=2021-10-14&art.progressivo=0)
- [D.M. 07/01/2013 – GU](https://www.gazzettaufficiale.it/eli/id/2013/01/17/13A00360/sg) · [copia aggiornata Questura](https://questure.poliziadistato.it/statics/16/dm-07.01.2013-aggiornato.pdf?lang=it)
- [L. 08/08/2019 n. 77 – GU](https://www.gazzettaufficiale.it/eli/id/2019/08/09/19G00089/SG)
- [Questura di Siena – Alloggiati web: le novità per gli albergatori](https://questure.poliziadistato.it/it/Siena/articolo/5730de9d2f257378524963)
- [Consiglio di Stato – Obbligo di identificazione de visu](https://www.giustizia-amministrativa.it/en/web/guest/-/105486-1372)

### Secondarie (non ufficiali, solo indizi; dalla versione precedente del file, consultate il 2026-03-27)
- [ANBBA - Vademecum 2026 Locazioni Turistiche](https://www.anbba.it/vademecum-locazioni-turistiche-2026/)
- [Lodgify - Comunicazione ISTAT](https://www.lodgify.com/blog/it/comunicazione-istat-ospiti/)
- [Affitti Brevi 360 - Alloggiati Web](https://affittibrevi360.it/disbrigo-check-in-burocrazia/comunicazioni-alla-questura-per-gli-affitti-brevi-cosa-devi-sapere/)
- [CheckInFacile - Multa Alloggiati Web 2026](https://checkinfacile.com/blog/multa-alloggiati-web-sanzioni.html)
