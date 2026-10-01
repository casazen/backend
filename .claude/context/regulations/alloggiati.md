# Comunicazione Alloggiati Web

> Aggiornato: 2026-09-23 (task RS-1) e 2026-10-01 (task CO-13). Le sezioni "Specifiche tecniche verificate (2026-09)" e
> "Verdetto per CO-13" sostituiscono le affermazioni tecniche precedenti dove sono in contrasto. **Il 2026-10-01 CO-13 ha letto
> per intero le copie di `MANUALEALBERGHI.pdf` e `MANUALEWS.pdf` e due copie del WSDL** (fonti e checksum nel runbook
> `docs/runbooks/alloggiati.md`, "Record file (CO-13)"): le righe marcate **D** del tracciato sono ora **U** con il numero di
> pagina, dove sotto non è indicato altro. `CREAFILE.pdf` non è stato letto (dominio ufficiale bloccato).

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
- **Aggiornamento CO-13 (2026-10-01)**: `alloggiatiweb.poliziadistato.it` e `questure.poliziadistato.it` sono ancora bloccati, ma
  `MANUALEALBERGHI.pdf` (35 pagine), `MANUALEWS.pdf` (21 pagine, Rev. 01 del 24/01/2022) e il WSDL sono stati letti per intero da
  copie in repository GitHub pubblici di terzi (non verificate byte per byte sul portale: gli SHA-256 sono nel runbook). Livelli:
  **U-copia** = riga letta nel testo integrale di una copia del documento ufficiale, con pagina; **T2** = confermata anche da
  un'implementazione di terzi indipendente. Il tracciato è **verificato** (due documenti ufficiali integrali che coincidono, più due
  implementazioni di terzi). Non verificato: `CREAFILE.pdf`, la lista dei codici di errore (`TipoErrore`), i limiti del web service.

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
- Righe separate da **CR+LF** (ASCII 13 e 10); dopo l'ultima riga **non** si aggiunge CR+LF (U, CREAFILE.pdf; U-copia, MANUALEALBERGHI.pdf p. 33-35, "Nota Bene").
- Date nel formato **`gg/mm/aaaa`**, per esempio `16/02/2005` (arrivo) e `13/03/1973` (nascita) (U-copia, MANUALEALBERGHI.pdf p. 31).
- **Codifica: UTF-8** (U-copia, MANUALEALBERGHI.pdf p. 31: "Il file precompilato, con codifica UTF-8"); **al massimo 1000 righe** per file (U-copia, stessa pagina). Il manuale non elenca i caratteri ammessi (la versione più vecchia diceva "niente simboli speciali"). Un client di terzi che invia al portale reale riferisce che **il portale rifiuta i nomi con caratteri diversi da A-Z, spazio e apostrofo** ("Cognome con caratteri non validi") e che vanno traslitterati, non cancellati (T, `cito09/alloggiati-web-app`): CasaZen scrive i nomi in maiuscolo A-Z senza accenti (`AlloggiatiRecordFile.ToRecordName`) e rifiuta di generare il file se un nome non si può scrivere così. Da confermare con la prima chiamata `Test` su un account reale.
- Il numero di giorni di permanenza va scritto su 2 caratteri: i manuali non dicono se con zero iniziale. CasaZen scrive `03` (T, i client di terzi `padNum`); il minimo 1 e il massimo 30 sono il vincolo del portale (U-copia, "Massimo 30 gg").
- Nel web service ogni elemento di `ElencoSchedine` è una stringa che rispetta **lo stesso tracciato** (U, manuale WS).

**Campi** (posizioni 0-based "DA" / "A" come nella tabella del manuale: **U-copia**, MANUALEALBERGHI.pdf p. 34 e MANUALEWS.pdf p. 19, che coincidono con le somme delle lunghezze e con le due implementazioni di terzi)

| # | Campo | Pos. | Lung. | Formato / note | Verifica |
|---|---|---|---|---|---|
| 1 | Tipo alloggiato | 0–1 | 2 | codice dalla tabella Tipo Alloggiato | U |
| 2 | Data arrivo | 2–11 | 10 | `gg/mm/aaaa` | U |
| 3 | Numero giorni di permanenza | 12–13 | 2 | massimo 30, obbligatorio | U-copia (p. 34, DA 12 A 13) |
| 4 | Cognome | 14–63 | 50 | testo, riempito con spazi (es. ROSSI + 45 spazi) | U-copia (p. 31 e 34) |
| 5 | Nome | 64–93 | 30 | testo, riempito con spazi (es. PAOLO + 25 spazi) | U-copia (p. 31 e 34) |
| 6 | Sesso | 94 | 1 | `1` = maschio, `2` = femmina | U |
| 7 | Data di nascita | 95–104 | 10 | `gg/mm/aaaa` | U-copia (p. 31 e 34) |
| 8 | Comune di nascita | 105–113 | 9 | codice dalla tabella Comuni, **solo se nato in Italia**: 9 spazi se nato all'estero | U-copia (p. 31 e 34) |
| 9 | Provincia di nascita | 114–115 | 2 | sigla di targa (Roma = `RM`), **solo se nato in Italia**: 2 spazi se nato all'estero | U-copia (p. 32 e 34) |
| 10 | Stato di nascita | 116–124 | 9 | codice dalla tabella Stati, **anche per i nati in Italia** (con il codice dell'Italia) | U-copia (p. 32 e 34) |
| 11 | Cittadinanza | 125–133 | 9 | codice dalla tabella Stati | U |
| 12 | Tipo documento | 134–138 | 5 | codice dalla tabella Documenti | U |
| 13 | Numero documento | 139–158 | 20 | riempito con spazi fino a 20 | U |
| 14 | Luogo di rilascio documento | 159–167 | 9 | codice comune (se in Italia) o stato (se all'estero) | U |
| | **Totale** | | **168** | | U |

- **Familiari e membri del gruppo**: al posto dei campi 12–14 vanno **34 spazi** (5+20+9), per arrivare comunque a 168 caratteri (U, CREAFILE.pdf; U-copia, MANUALEALBERGHI.pdf p. 32, colonna "Tipo Alloggiato (19-20)": "Riempire con Blank").
- Per i nati all'estero i campi 8 e 9 vanno lasciati a spazi: "il campo va comunque completato con 9 spazi bianchi" e "con 2 spazi bianchi" (**U-copia**, MANUALEALBERGHI.pdf p. 31-32).
- **Profilo "Gestione Appartamenti" (File Unico)**: in fondo a ogni riga si aggiunge **IDAPPARTAMENTO di 6 caratteri** nelle posizioni 168-173 (U-copia, MANUALEALBERGHI.pdf p. 35 e MANUALEWS.pdf p. 20), quindi 174 caratteri (176 con CR+LF). L'ID è "presente nell'elenco appartamenti (non disabilitati)"; nel web service `Tabella(ListaAppartamenti)` restituisce `IDAPP;Descrizione;COMUNE;PROV;Indirizzo;Proprietario` (U-copia, MANUALEWS.pdf p. 18). **Non specificato**: allineamento e riempimento dell'ID nei 6 caratteri (gli ID dell'esempio sono `0`, `1`, `3`, `9`, mentre l'esempio di `GestioneAppartamenti_Test` usa `000004` come parametro intero). Il portale permette a quel profilo di scegliere l'appartamento anche caricando il file da 168 caratteri (MANUALEALBERGHI.pdf p. 24): CasaZen non genera il File Unico.
- **Ordine delle righe**: quando si inserisce un capo famiglia/gruppo, i familiari/componenti "vanno sempre riportati nelle righe immediatamente successive" (**U-copia**, MANUALEALBERGHI.pdf p. 33).

**Tipi alloggiato**
- Categorie ufficiali (U): **Ospite singolo**, **Capo famiglia**, **Capo gruppo**, **Familiare**, **Membro gruppo**. Capofamiglia e capogruppo richiedono l'inserimento delle schedine collegate. Singolo, capofamiglia e capogruppo richiedono sempre il documento.
- **Codici numerici.** Le intestazioni della tabella del tracciato ufficiale raggruppano i tipi in "Tipo Alloggiato (16-17-18)" (con documento) e "Tipo Alloggiato (19-20)" (senza documento) (**U-copia**, MANUALEALBERGHI.pdf p. 34 e MANUALEWS.pdf p. 19): i cinque tipi hanno quindi i codici 16-20, e i tre con documento sono 16, 17, 18. L'abbinamento di ogni numero al nome (16 = Ospite singolo, 17 = Capo famiglia, 18 = Capo gruppo, 19 = Familiare, 20 = Membro gruppo) segue l'ordine delle voci della guida ed è confermato da client di terzi (T2), ma la tabella dei codici non è nei manuali. **CasaZen continua a leggerli solo dalla tabella ufficiale importata** (`TipiAlloggiato`, per nome), non a mano.

**Vincoli applicativi del portale**
- Il portale accetta come **data di arrivo solo la data odierna o quella del giorno precedente** (U-copia, MANUALEALBERGHI.pdf p. 7 e 31; U, manuali e FAQ delle Questure). Conseguenze: **non si può inviare prima del giorno di arrivo** e un invio oltre il giorno successivo viene rifiutato.
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
  3. `GenerateToken(Utente, Password, WsKey)` restituisce `TokenInfo` (`issued`, `expires`, `token`). Il token ha **validità temporanea**: il manuale non dichiara la durata, ma l'esempio di risposta ha `expires` un'ora dopo `issued` (U-copia, MANUALEWS.pdf p. 7; un client di terzi riferisce 24 ore: T). Si legge `expires` dalla risposta, senza assumere una durata;
  4. le operazioni successive usano `Utente` e `token`.
  - L'OTP serve per il login al portale; `GenerateToken` non ha un parametro OTP (D).
- **Operazioni** (12, tutte presenti nel WSDL e descritte con esempi di richiesta e risposta in MANUALEWS.pdf, Rev. 01 del 24/01/2022; namespace `AlloggiatiService`, binding SOAP 1.1 e 1.2, endpoint `https://alloggiatiweb.poliziadistato.it/service/service.asmx`):

  | Operazione | Parametri principali | Scopo | Verifica |
  |---|---|---|---|
  | `GenerateToken` | Utente, Password, WsKey | genera il token temporaneo | U-copia (p. 7, WSDL) |
  | `Authentication_Test` | Utente, token | controllo del token ("Controllo del Token di Autenticazione") | U-copia (MANUALEWS.pdf p. 8, WSDL) |
  | `Test` | Utente, token, ElencoSchedine (`ArrayOfString`, un `string` per riga) | **controlla la correttezza** delle schedine **senza inviarle** | U-copia (p. 9, WSDL) |
  | `Send` | Utente, token, ElencoSchedine | **invio**: "le sole schedine corrette saranno acquisite dal sistema" (le altre no, le corrette sì: invio parziale possibile) | U-copia (p. 10, WSDL) |
  | `Ricevuta` | Utente, token, Data (`xs:dateTime`, es. `2021-12-10T00:00:00`) | scarica la **ricevuta PDF** (`base64Binary`) degli invii di quel giorno: "ultimi 30gg escluso il giorno corrente" | U-copia (p. 17, WSDL) |
  | `Tabella` | Utente, token, tipo (`Luoghi`=0, `Tipi_Documento`=1, `Tipi_Alloggiato`=2, `TipoErrore`=3, `ListaAppartamenti`=4) | scarica una tabella in **CSV con separatore `;`** | U-copia (MANUALEWS.pdf p. 6 e 18, WSDL) |
  | `GestioneAppartamenti_Test` / `_Send` | Utente, token, ElencoSchedine, IdAppartamento (int) | come `Test` / `Send` per un appartamento; solo utenze "Gestione Appartamenti" | U-copia (p. 11-12) |
  | `GestioneAppartamenti_FileUnico_Test` / `_Send` | Utente, token, ElencoSchedine (righe da 174 caratteri) | come sopra con l'id dell'appartamento in ogni riga | U-copia (p. 13-14) |
  | `GestioneAppartamenti_AggiungiAppartamento` / `_DisabilitaAppartamento` | Utente, token, Descrizione, ComuneCodice, Indirizzo, Proprietario / IdAppartamento | anagrafica appartamenti | U-copia (p. 15-16) |

- **Esiti** (U-copia, MANUALEWS.pdf p. 5-6, WSDL): `EsitoOperazioneServizio` = `esito` (boolean), `ErroreCod`, `ErroreDes` (codici nella tabella `TipoErrore`), `ErroreDettaglio`; `ElencoSchedineEsito` = `SchedineValide` (int) più `Dettaglio`, un `EsitoOperazioneServizio` per ogni riga inviata, **nell'ordine delle righe**. La risposta di `Test`/`Send` ha `TestResult`/`SendResult` (esito generale) e `result` (`ElencoSchedineEsito`) come elementi fratelli; quella di `GenerateToken` ha `GenerateTokenResult` (`TokenInfo`) e `result` (esito). Esempi di errori di riga nel manuale: `11 SCHEDINA_FORMATO_NON_CORRETTO` ("Dimensione Riga errata"), `12 SCHEDINA_CAMPO_NON_CORRETTO` ("Data di Arrivo Errata"). La lista completa dei codici non è nel manuale (si scarica con `Tabella(TipoErrore)` con credenziali).
- **Ambiente di test**: **nessun ambiente di test o sandbox separato risulta documentato** nelle fonti consultate. L'unico strumento è il metodo `Test`, che valida senza inviare ma richiede **credenziali reali** di una struttura.
- **Non determinato dalle fonti consultate** (nemmeno dalle copie integrali): durata garantita del token, numero massimo di schedine per chiamata al web service (il file ne ammette 1000), limiti di frequenza o blocchi dell'utenza dopo credenziali errate, lista dei codici di errore (ottenibile con `Tabella(TipoErrore)`), comportamento di un `Send` ripetuto con le stesse schedine (duplicati?). Un client di terzi che usa il servizio reale mappa gli errori di autenticazione 100-104 (credenziali errate, WSKEY scaduta/errata/mancante, utente disabilitato): T, non documentato nel manuale.
- **Contratti e accreditamenti**: l'Allegato tecnico richiede solo "la preventiva registrazione dell'utente sul portale web e la richiesta di generazione delle chiavi di autorizzazione" (U). **Nessuna fonte consultata prevede** accreditamenti, convenzioni o contratti per il fornitore del software.

### 4. Ricevuta
- La ricevuta è un **PDF firmato digitalmente** dalla Polizia di Stato, con QR code di controllo. È emessa **il giorno successivo** all'invio e resta scaricabile online per **30 giorni**. Il gestore la deve **conservare per 5 anni** (U, RICDIGALLO.pdf).
- Via web service si scarica con `Ricevuta(Utente, token, Data)`: la data è un giorno degli ultimi 30, **escluso il giorno corrente** (U-copia, MANUALEWS.pdf p. 17); sul portale ogni ricevuta riporta data, numero di schedine inviate e **numero di protocollo** (MANUALEALBERGHI.pdf p. 19). La ricevuta è **per giorno e per utenza**, non per schedina.
- Conseguenza per CasaZen, coerente con D6 in DECISIONI.md: dopo un `Send` con esito positivo lo stato è "inviato, ricevuta in attesa". Si passa a "ricevuta acquisita" solo dopo aver scaricato la ricevuta del giorno di invio.

### 5. Tempistiche di legge
- **Art. 109, comma 3, TULPS** (testo in vigore dal 10/08/2019): comunicazione **entro le 24 ore successive all'arrivo** "e comunque **entro le sei ore successive all'arrivo** nel caso di soggiorni non superiori alle ventiquattro ore". L'inciso è stato inserito dall'art. 5 del D.L. 53/2019, come convertito dalla L. 77/2019 (U, 109_TULPS.pdf del portale e copia della Questura).
- Il **D.M. 07/01/2013**, nel testo originario, parla di trasmissione "entro le ventiquattro ore successive all'arrivo … e comunque **all'arrivo stesso** per soggiorni inferiori alle ventiquattro ore" (U). Prevale la norma di legge successiva (6 ore), ma **per prudenza CasaZen deve inviare subito all'arrivo** i soggiorni non superiori alle 24 ore.
- Implementazione (CO-11): l'arrivo si calcola in **Europe/Rome**. La scadenza è arrivo + 24h, oppure arrivo + 6h se il soggiorno non supera le 24 ore. L'invio è possibile solo dal giorno di arrivo in poi (vincolo del portale sulla data di arrivo).

### 6. Punti aperti fuori perimetro RS-1 (segnalati, non verificati)
- **Identificazione "de visu"**: la circolare del Ministero dell'Interno del 18/11/2024 (n. 38138, copia su `questure.poliziadistato.it/statics/48/circolare---identificazione-delle-persone-ospitate-presso-strutture-ricettive.pdf`) ritiene insufficiente il check-in solo da remoto. Il TAR Lazio l'ha annullata (sent. 10210/2025, T). Il **Consiglio di Stato, Sez. III, sent. n. 9101 del 21/11/2025** (pagina su giustizia-amministrativa.it: "Obbligo di identificazione de visu delle persone alloggiate a carico del gestore di strutture ricettive") ha riformato la sentenza del TAR (riforma: T; esistenza e oggetto della sentenza: U). Secondo fonti terze (T) l'identificazione è ammessa anche **a distanza ma in tempo reale**. Il tema riguarda direttamente il **self check-in** di CasaZen e richiede una verifica legale dedicata.

---

## Verdetto per CO-13

> **Stato CO-13 (2026-10-01): file tracciato implementato, client web service NON implementato.** Il proxy blocca ancora
> `alloggiatiweb.poliziadistato.it` e `questure.poliziadistato.it`, ma le copie integrali dei manuali e del WSDL (repository
> GitHub di terzi) hanno confermato il tracciato (le righe D sono ora U-copia). CasaZen genera il file da caricare sul portale
> (`AlloggiatiRecordFile`, `GET /api/alloggiati/{bookingId}/record-file`); l'invio via web service resta fuori perimetro e lo
> stato "Inviato" resta impossibile senza ricevuta. Dettagli e motivi: `docs/runbooks/alloggiati.md`, sezione "Record file
> (CO-13)".

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
   - Mapping dei dati CasaZen sul tracciato record a 168 caratteri (**fatto in CO-13**; il File Unico a 174 caratteri non è generato)
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
