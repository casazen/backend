# Flussi turistici ISTAT (movimento clienti): regione pilota Lombardia

> Ricerca del task **RS-9** (risanamento), eseguita il **2026-09-24**. Riguarda l'issue #6 (reportistica ISTAT) e, per la parte sui portali regionali, l'issue #8. Il CIR lombardo è in `cir-lombardia.md`.
> **Non è un parere legale.** I punti aperti sono in "DUBBI".

## Esito in breve

| Domanda | Risposta | Livello |
|---|---|---|
| Le locazioni turistiche in Lombardia devono comunicare i flussi? | **Sì.** L'obbligo riguarda tutte le strutture ricettive, alberghiere e non, e anche gli alloggi (o porzioni) dati in locazione per finalità turistiche, imprenditoriali e non (L.R. 27/2015, art. 38 c. 8) | U |
| Canale | **Ross1000** (ex Turismo5), portale regionale `https://www.flussituristici.servizirl.it/`. È l'unico canale dal gennaio 2018 | U |
| Periodicità e scadenza | Rilevazione **mensile**: inserimento entro il **giorno 5 del mese successivo**, anche senza movimento (apertura senza ospiti o chiusura). L'inserimento giornaliero è preferibile | U |
| Sanzione | Da **€250 a €2.500 per ciascun mese** di comunicazione omessa o incompleta (L.R. 27/2015, art. 40 c. 9). La applica la Provincia o la Città metropolitana | U |
| Formato dei dati | **Non è un report aggregato.** Ross1000 riceve **record per ospite, giorno per giorno** (arrivi, partenze, prenotazioni, rettifiche, disponibilità della struttura), codificati con le tabelle di Alloggiati Web. L'aggregato ISTAT (MOV/C) lo produce la Regione | U (struttura), T (elenco completo dei campi) |
| Esiste uno standard pubblico? | **Sì.** Tracciato XML e tracciato TXT per l'import da gestionale e web service SOAP (`checkinV2`), comuni a tutte le regioni che usano Ross1000 | U (esistenza), T (dettagli del web service) |
| Verificato per intero? | **No.** Il proxy ha bloccato il download dei PDF ufficiali; i contenuti vengono da estratti indicizzati e da codice open source di terze parti | — |
| Codice in RS-9 | **Nessuno.** Vedi "Decisione sul codice" | — |

### Decisione sul codice
In RS-9 non si scrive codice, per tre motivi:
1. Lo standard esiste ed è pubblico, ma in questa sessione **non è stato letto per intero**. Obbligatorietà, lunghezze e codifiche dei campi vengono da estratti e da codice di terzi, quindi non soddisfano "standard documentato e verificato".
2. L'implementazione spetta al task **CO-22**, che dipende da **SU-04**. Oggi `Property` non ha né il codice ISTAT del comune né il CIR, e il CIR serve come codice struttura in Ross1000.
3. Il product owner ha classificato l'issue #6 come **post-MVP** (commento del 19/06/2026: "Post-MVP. Not in Golden Journey exit criteria").

## Legenda e metodo

- **U**: fonte ufficiale di un ente pubblico (Regione, Provincia, Città metropolitana, Comune, ISTAT, Ministero). Salvo diversa indicazione è stato letto l'**estratto indicizzato** (WebSearch filtrato sui domini ufficiali), non la pagina intera.
- **D**: dedotto da fonti U, non letto testualmente.
- **T**: terza parte (software house, blog, codice open source, **GIES**, il fornitore della piattaforma Ross1000). È un indizio, non una fonte.
- Data di consultazione di tutte le fonti: **2026-09-24**.
- **Accesso diretto bloccato.** WebFetch e curl hanno ricevuto `EGRESS_BLOCKED` o `403` su tutti i domini ufficiali (elenco in "Fonti bloccate"). Solo due documenti sono stati **letti per intero**, entrambi di terze parti:
  - la nota GIES sul web service (copia ospitata su `s3.amazonaws.com`);
  - il codice sorgente open source di due integrazioni (VikBooking e `Laischon/Turismo5-WebService` su GitHub).

## 1. Quadro normativo

### 1.1 Livello nazionale (ISTAT)

| Fatto | Livello | Fonte |
|---|---|---|
| La rilevazione "Movimento dei clienti negli esercizi ricettivi" è **totale e mensile**. Si basa sul Reg. (UE) 692/2011, sul Reg. di esecuzione 1051/2011 e sul Reg. delegato (UE) 2019/1681 | U | [F1] |
| Codice PSN **IST-00139**, nel Programma statistico nazionale 2023-2025, aggiornamento 2024-2025, approvato con D.P.R. del 06/11/2025 | U | [F1] |
| **Obbligo di risposta** per i soggetti privati: **sì** | U | [F1] |
| Rileva per mese e per comune gli **arrivi** e le **presenze** di residenti e non residenti, per categoria e tipo di esercizio e per paese estero o regione italiana di residenza | U | [F1] |
| L'ISTAT usa gli uffici di statistica delle Regioni come **organi intermedi** della raccolta (D.Lgs. 322/1989) | U | [F1] |
| Le Regioni trasmettono all'ISTAT i file del **modello MOV/C** in formato testo (`.txt`, `.csv`, `.dat`) tramite il portale INDATA `mtur` | U | [F1], [F3] |
| Il modello cartaceo storico per le strutture è l'**ISTAT C/59**: una sezione mensile (C/59_M) e una giornaliera (C/59_G) con i clienti arrivati e partiti nel giorno. Si compila in duplice copia: una va all'ente territoriale, l'altra resta in struttura per almeno 2 anni. Sono ammessi moduli locali, anche telematici, che riportano le stesse informazioni | U (istruzioni 2013) | [F2] |
| Gli **alloggi privati gestiti in forma non imprenditoriale** non rientrano tra gli esercizi extra-alberghieri della rilevazione. L'ISTAT ha diffuso per la prima volta dati su di essi (circa 317.000 alloggi e 1,5 milioni di posti letto nel 2024) | U | [F4] |
| **Sanzioni** (D.Lgs. 322/1989, artt. 7 e 11) per mancata risposta o dati scientemente errati o incompleti: da €206 a €2.065 per le persone fisiche, da €516 a €5.164 per enti e imprese | U (estratto), T (importi per le persone fisiche) | [F5] |

**Conseguenza (D):**
- A livello nazionale la rilevazione ISTAT riguarda gli **esercizi ricettivi**.
- Per le locazioni turistiche non imprenditoriali l'obbligo **non** discende direttamente dal PSN, ma dalla **legge regionale**. Per la Lombardia è la L.R. 27/2015 (sezione 1.2).
- Per le altre regioni va verificato caso per caso.

### 1.2 Lombardia

| Fatto | Livello | Fonte |
|---|---|---|
| L.R. 1/10/2015 n. 27, art. 38 c. 8: le strutture ricettive alberghiere e non alberghiere, **compresi gli alloggi o le porzioni di alloggi dati in locazione per finalità turistiche**, devono comunicare i flussi turistici secondo le indicazioni regionali e comunicare gli ospiti secondo le indicazioni dell'autorità di pubblica sicurezza | U | [L1], [L5] |
| La comunicazione dei flussi attua in Lombardia l'obbligo di risposta statistica dell'art. 7 del D.Lgs. 322/1989 | U (estratto provinciale), D (rapporto giuridico) | [L6] |
| **Sanzione** (L.R. 27/2015, art. 40 c. 9): da **€250 a €2.500 per ciascun mese** di omessa o incompleta comunicazione dei flussi. Si applica al titolare della struttura e al titolare dell'alloggio in locazione turistica (L. 431/1998) | U (estratto; testo di legge non letto) | [L4], [L6] |
| La sanzione la applica l'ente territoriale: per Milano la **Città metropolitana**, che la applica "a partire dalla data di scadenza" | U | [L4] |
| Rilevazioni servite: ISTAT "Movimento dei clienti negli esercizi ricettivi" e "Capacità degli esercizi ricettivi" (PSN), più i debiti informativi verso la Regione (L.R. 27/2015) | U | [L3], [L5] |
| **PoliS-Lombardia** fa da organo intermedio: trasmette all'ISTAT i dati raccolti nei formati previsti (Mov/C e "allegato 7, modello CTT4") | U | [L5], [C3] |

## 2. Chi è obbligato in Lombardia

- **Tutte** le strutture ricettive alberghiere e non alberghiere: CAV, B&B, agriturismi, ostelli, eccetera (U, [L4], [L6]).
- Le **locazioni turistiche (LT)**, in forma imprenditoriale e non imprenditoriale (U, [L1], [L4]). Le disposizioni sul CIR, e quindi la registrazione in Ross1000, si applicano anche alle **locazioni brevi** del D.L. 50/2017 (U, estratto dell'art. 38, [L1]).
- L'obbligo vale **per ogni unità** con un proprio CIR, cioè una sezione anagrafica in Ross1000 (D: l'art. 38 assegna un CIR "di ogni singola unità ricettiva", [L1]).
- È un obbligo **distinto e parallelo** ad Alloggiati Web (Questura):
  - Ross1000 ha finalità statistiche e scadenza mensile;
  - Alloggiati Web ha finalità di pubblica sicurezza e scadenza di 24 ore (vedi `alloggiati.md`) (T, [T1]. U: l'art. 38 c. 8 li elenca come due obblighi distinti [L1]; la Provincia di Lecco chiede un gestionale compatibile con entrambi [L7]).

## 3. Canale: Ross1000 Lombardia

| Aspetto | Dettaglio | Livello | Fonte |
|---|---|---|---|
| Portale | `https://www.flussituristici.servizirl.it/` (login: `/Turismo5/app/login.xhtml`) | U | [L3], [L8] |
| Nome | "Gestione dei Dati Turistici ROSS 1000", ex **Turismo5**. Stesso adempimento, nome diverso | U | [L4] |
| Dal | Gennaio 2018: unico strumento per comunicare i flussi a Regione e ISTAT | U | [L8] |
| Accesso | Dal 01/10/2021 solo con identità digitale (**SPID, CIE o CNS**) del titolare o del legale rappresentante | U | [L3] |
| Abilitazione | Non ci si registra da soli: CIA/SCIA al SUAP del Comune → il Comune la trasmette alla Provincia o alla Città metropolitana → l'ente registra la struttura in Ross1000 e manda una mail automatica al titolare (dettagli in `cir-lombardia.md`) | U | [C1], [C3] |
| Assistenza tecnica | Numero verde 800.070.090, `info-flussituristici@ariaspa.it` (ARIA S.p.A.) | U | [L4], [L3] |
| Assistenza amministrativa (Milano) | `operatoriturismo@cittametropolitana.mi.it` | U | [L4] |
| Gestionali compatibili | Nella sezione "Lista SW House" di Ross1000. La Provincia di Lecco raccomanda di verificare che il gestionale sia compatibile sia con Alloggiati Web sia con Ross1000 | U | [L7] |
| Diffusione | Lo stesso software, sviluppato da **GIES S.r.l.** (San Marino), è usato da molte regioni: Veneto, Emilia-Romagna, Piemonte, Marche, Abruzzo, Calabria, Sardegna, Liguria, Lazio e alcune province toscane. Il fornitore dichiara che copre oltre il 70% delle strutture nazionali | T | [T2], [T3] |

## 4. Periodicità, scadenze e obblighi accessori

| Regola | Livello | Fonte |
|---|---|---|
| Dati mensili da inserire **entro e non oltre il giorno 5 del mese successivo** a quello di riferimento | U | [L4], [L6] |
| L'inserimento **giornaliero** è preferibile | U | [L6] |
| L'obbligo vale **anche senza ospiti**: apertura senza movimento e chiusura vanno dichiarate con la funzione "gestione disponibilità" (calendario) di Ross1000. Va inserito anche ogni periodo di interruzione | U | [L6], [L7] |
| Per la capacità vanno comunicati letti e camere disponibili. Nel tracciato XML esistono i campi `apertura`, `camereoccupate`, `cameredisponibili` e `lettidisponibili` per ogni giorno | U (capacità), T (nomi dei campi) | [L5], [T2] |

## 5. Formati dei dati

### 5.1 Inserimento manuale
Check-in e check-out ospite per ospite sul portale (U, [L3]: manuale "Profilo struttura ricettiva", versione 10/2023).

### 5.2 Import di file dal gestionale
- Menu **Check-in → "Importa file da gestionale"**, poi "Seleziona file" (U, [L3], [L8]).
- Formati accettati: **solo `.txt` e `.xml`**. Si può partire con `.txt` e passare poi a `.xml` (U, [L3]).
- I due tracciati si scaricano dal riquadro "Manuali" nella pagina iniziale del portale (U, [L8]). Se quella pagina sia visibile senza login non è stato verificato (D: probabilmente sì).
- Versioni note:

| Documento | Versione | Livello | Fonte |
|---|---|---|---|
| "Tracciato record di interscambio XML-WS" | 2.4 | U (copia della Regione Emilia-Romagna) | [R1] |
| "Tracciato record di integrazione dati (TXT)" | 4, del 20/10/2022 | U (copia della Regione Emilia-Romagna) | [R1] |
| "Tracciato record di integrazione dati (XML)", sul sito del fornitore | "versione 3 del 18/03/2026" | T | [T3] |

  Il rapporto tra la numerazione 2.4 e la "versione 3" del 2026 **non è chiaro**: potrebbe essere la versione del documento e non quella dello schema. CO-22 deve usare la versione pubblicata sul portale **lombardo**.

### 5.3 Web service (trasmissione automatica)

| Aspetto | Dettaglio | Livello | Fonte |
|---|---|---|---|
| Disponibile in Lombardia | Il manuale lombardo rimanda alla "Nota Trasmissione WS_Gies" per la trasmissione via web service | U | [L3] |
| Parametri | Quattro: **indirizzo del servizio** (da chiedere all'ente; di solito l'indirizzo di Ross1000 più `/ws/checkinV2`), **username** (pagina "Modifica profilo"), **password di trasmissione** e **codice struttura** (campo "Codice regione" dell'anagrafica, sezione Generale) | T (nota GIES letta per intero) | [T5] |
| Password di trasmissione | Chi accede con SPID, CIE o CNS deve crearla con la procedura "Recupero password" (username più email del profilo). Serve **solo** per la trasmissione dal gestionale | T (nota GIES) | [T5] |
| Codice struttura in Lombardia | Il "Codice regione" di Ross1000 **corrisponde al CIR** | U (Città metropolitana), D (che coincida con `<codice>` del tracciato) | [C3], [T5] |
| Endpoint lombardo | `https://www.flussituristici.servizirl.it/Turismo5/app/ws/checkinV2?wsdl` | T (commento nel codice VikBooking). **Da confermare con ARIA o con la Regione** | [T2] |
| Protocollo | SOAP 1.1, `Content-Type: text/xml; charset=utf-8`, autenticazione **HTTP Basic** (username:password) | T | [T2], [T6] |
| Operazione | `inviaMovimentazione`, namespace `http://checkin.ws.service.turismo5.gies.it/`. Il corpo contiene lo stesso XML del file, con radice `<movimentazione>` invece di `<movimenti>` | T | [T6] |
| Risposta | `inviaMovimentazioneResponse/return/risultatiGiorno`: esito per giorno e, per ogni arrivo o partenza, `idswh` ed `errore` | T | [T6] |
| Ambiente di test | Per l'Emilia-Romagna il codice di terzi cita un ambiente di test (`q-rer.turitweb.it`). **Per la Lombardia non è stato trovato nulla** | T | [T6] |

### 5.4 Struttura del tracciato XML (per CO-22, da confermare sul documento ufficiale)

Struttura generale (U, estratti di [R1] e [T3]):
- Radice `<movimenti>` con due campi obbligatori:
  - `<codice>`: codice della struttura che trasmette;
  - `<prodotto>`: nome del gestionale.
- Seguono uno o più `<movimento>`, **uno per giorno di attività**, in ordine di data crescente.
- Ogni `<movimento>` contiene `<data>` e può contenere `<struttura>`, `<arrivi>`, `<partenze>`, `<prenotazioni>` e `<rettifiche>`.
- Tutti i campi indicati come obbligatori devono essere presenti e valorizzati correttamente. Altrimenti la **schedina viene scartata**.

Campi di `<arrivo>`, nell'ordine usato da un'integrazione esistente (T, [T2]; la colonna "U" indica cosa conferma l'estratto ufficiale):

| Campo | Note | U |
|---|---|---|
| `idswh` | identificativo della registrazione nel gestionale | obbligatorio, max 20 caratteri |
| `tipoalloggiato` | codice dalla tabella "Tipi Alloggiato" della Polizia di Stato | obbligatorio |
| `idcapo` | `idswh` del capofamiglia o capogruppo | obbligatorio se `tipoalloggiato` è 19 o 20 |
| `cognome` | | max 50 caratteri |
| `nome` | | sì (lunghezza non letta) |
| `sesso` | | — |
| `cittadinanza` | tabella "Nazioni" della Polizia di Stato | codifica U |
| `statoresidenza`, `luogoresidenza` | tabelle "Nazioni" e "Comuni" della Polizia di Stato | codifica U |
| `datanascita` | nel codice di terzi `AAAAMMGG` | — |
| `statonascita`, `comunenascita` | tabelle "Nazioni" e "Comuni" della Polizia di Stato | codifica U |
| `tipoturismo`, `mezzotrasporto`, `canaleprenotazione`, `titolostudio`, `professione` | codifiche del tracciato, non lette | — |
| `esenzioneimposta` | codifica da chiedere **per ogni comune**: il campo riguarda l'imposta di soggiorno | U |

Altri elementi (T, [T2]):
- `<partenza>`: `idswh`, `tipoalloggiato`, `arrivo` (data di arrivo);
- `<prenotazione>`: `idswh`, `arrivo`, `partenza`, `ospiti`, `camere`;
- `<struttura>`: `apertura` (`SI` o `NO`), `camereoccupate`, `cameredisponibili`, `lettidisponibili`;
- `<rettifiche>`: struttura non letta.

**Note sulle codifiche:**
- Il tracciato usa **le stesse tabelle Comuni, Nazioni e Tipi Alloggiato di Alloggiati Web** (U, [R1]). L'importazione delle tabelle prevista per CO-12 e CO-13 (vedi `alloggiati.md`) va riusata, non duplicata.
- La regola "`idcapo` obbligatorio se `tipoalloggiato` è 19 o 20" è coerente con l'ipotesi, da fonti terze, 19 = familiare e 20 = membro gruppo (vedi `alloggiati.md`). Resta una deduzione (D): i codici vanno letti dalla tabella ufficiale.

### 5.5 Cosa comporta per il modello dati

**D, da confermare:**
- I dati sono **nominativi**: cognome, nome, data e luogo di nascita, residenza.
- Il report aggregato "Mese, Anno, Nazionalità, Arrivi, Presenze" previsto dall'issue #6 **non è un formato accettato** da Ross1000. Può essere solo un cruscotto interno.
- L'aggregazione per l'ISTAT la fa la Regione.
- Servono dati che oggi CasaZen non raccoglie in modo strutturato:
  - residenza codificata (stato e comune dalle tabelle della Polizia);
  - ruolo nel soggiorno e legame con il capofamiglia o capogruppo;
  - disponibilità di letti e camere per giorno, aperture e chiusure.
  - `Guest.Nationality` e `Guest.Country` sono testo libero.

## 6. Cosa non riguarda CasaZen

- **MOV/C e CTT4**: sono i file che la **Regione** (PoliS-Lombardia) manda all'ISTAT. Un host o un gestionale non li produce e non li invia (U, [L5], [F3]).
- **Modello C/59 cartaceo**: in Lombardia è sostituito dall'inserimento su Ross1000 (D, da [L8] e [F2]). Non va generato.
- **INDATA `mtur`**: il portale ISTAT riservato agli organi intermedi (U, [F3]).

## 7. Impatto su CasaZen e indicazioni per CO-22

### Correzioni alle ipotesi dell'issue #6

| Ipotesi dell'issue #6 | Esito RS-9 |
|---|---|
| Comunicazione di dati **aggregati** (arrivi, presenze, nazionalità) | **Errata** per la Lombardia. Ross1000 vuole record per ospite e per giorno (sezione 5) |
| Export "CSV/XML formato ISTAT" | Non esiste un "formato ISTAT" per l'host. Esistono il tracciato **Ross1000** (XML o TXT) e il web service regionale |
| Scadenza "tipicamente entro metà mese successivo" | In Lombardia è il **giorno 5** del mese successivo |
| "Sanzioni amministrative" generiche | In Lombardia **€250–2.500 per mese** (L.R. 27/2015, art. 40 c. 9) |
| Campo `Nationality` ISO 3166-1 alpha-2 | Ross1000 usa i **codici della tabella Nazioni della Polizia di Stato** (9 cifre in Alloggiati), non ISO. Serve una mappatura |
| Integrazione portali "opzionale" | Il web service esiste ed è documentato dal fornitore (T). La via a basso rischio è l'**export del file XML** da caricare a mano |

### Prerequisiti per CO-22
1. Scaricare da una rete non filtrata e salvare con data (per esempio in `docs/runbooks/ross1000.md`, con i link) questi documenti:
   - il tracciato XML-WS e il tracciato TXT pubblicati sul portale **lombardo**;
   - il manuale "Profilo struttura ricettiva" 10/2023;
   - la "Nota Trasmissione WS_Gies";
   - il WSDL di `.../Turismo5/app/ws/checkinV2?wsdl`.
   Poi confermare ogni riga marcata T o D in questo file.
2. **SU-04**:
   - codice ISTAT del comune e regione sulla property;
   - un campo separato per il **CIR**, che fa da codice struttura (vedi `cir-lombardia.md`).
3. Riuso delle tabelle Polizia (Comuni, Nazioni, Tipi Alloggiato) previste per **CO-12 e CO-13**.
4. Dati ospite codificati: residenza, ruolo nel soggiorno, capogruppo. Si allineano con il check-in digitale di Alloggiati.
5. Calendario di apertura, chiusura e disponibilità (letti e camere) per ogni giorno.
6. Se si sceglie il web service: credenziali Ross1000 per host (username e password di trasmissione) **cifrate**, come le credenziali Alloggiati. Niente credenziali o dati personali nei log.

### Proposta di sequenza (D, da validare col product owner)
1. **Fase 1, informativa**:
   - scheda "Obblighi Lombardia" nel wizard regionale (#8, #295) con canale, scadenza e sanzione;
   - promemoria il giorno 1 e il giorno 4 del mese.
   Richiede solo dati già verificati (U) e nessuna integrazione.
2. **Fase 2, export**: generazione del file XML Ross1000 per il mese o per un intervallo, da caricare a mano con "Importa file da gestionale". Nessuna credenziale da custodire. Solo dopo il prerequisito 1.
3. **Fase 3, web service**: trasmissione automatica `inviaMovimentazione` con esito per schedina. Richiede credenziali per host e un ambiente di prova (vedi DUBBI).

## 8. Fonti bloccate e come sbloccarle

Da questa sessione WebFetch e curl hanno ricevuto `EGRESS_BLOCKED` o `403 CONNECT` su:

| Dominio | Cosa servirebbe leggere |
|---|---|
| `www.regione.lombardia.it` | manuale Ross1000 10/2023; pagine su CIR, CIN e avvio della struttura |
| `www.flussituristici.servizirl.it` | tracciati XML e TXT (riquadro "Manuali"), `res/FAQ.pdf`, `res/Informativa_CIN.pdf`, WSDL `Turismo5/app/ws/checkinV2?wsdl` |
| `normelombardia.consiglio.regione.lombardia.it` | testo vigente della L.R. 27/2015 (artt. 38 e 40) e della L.R. 20/2024 (art. 15) |
| `www.cittametropolitana.mi.it`, `www.provincia.como.it`, `www.provincia.mb.it`, `www.provincia.lecco.it` e altre province | pagine su flussi e CIR |
| `fareimpresa.comune.milano.it` | pagina LT, CIA e CIR |
| `www.istat.it`, `www.sistan.it`, `indata.istat.it` | scheda IST-00139, istruzioni C/59, pagina sanzioni |
| `statistica.regione.emilia-romagna.it`, `www.ross1000.it`, `web.gies.sm` | copie integrali dei tracciati XML e TXT |
| `www.dati.lombardia.it` | dataset delle strutture ricettive |

Per sbloccarli bisogna aggiungere questi domini agli **allowed domains** dell'accesso di rete dell'ambiente cloud (impostazioni dell'ambiente, voce "Network access") o scegliere un livello di accesso più ampio. In alternativa si scaricano i PDF da una rete non filtrata e si allegano al task CO-22.

## 9. DUBBI (per il product owner e per un legale)

1. **Priorità.** L'issue #6 è post-MVP (commento del product owner del 19/06/2026). La fase 1 informativa rientra nel wizard regionale di #295 o resta fuori anche lei?
2. **Ruolo privacy.** Con export o web service CasaZen tratterebbe e trasmetterebbe dati nominativi degli ospiti alla Regione per conto dell'host. Serve un parere su:
   - ruolo (responsabile del trattamento per conto dell'host);
   - base giuridica (obbligo di legge in capo all'host);
   - informativa agli ospiti;
   - conservazione.
   Vedi `gdpr.md`.
3. **Custodia delle credenziali Ross1000 dell'host.** La password di trasmissione è personale del titolare, che accede con SPID. È accettabile che CasaZen la conservi, come per Alloggiati, o si offre solo l'export del file?
4. **Sanzioni sovrapposte.** Per la stessa omissione concorrono la sanzione regionale (€250–2.500 al mese) e quella statistica nazionale (D.Lgs. 322/1989, artt. 7 e 11)? Serve un parere legale. Nel prodotto conviene citare solo quella regionale.
5. **Campi facoltativi in Lombardia.** Non è chiaro quali campi del tracciato siano obbligatori per la Lombardia: `tipoturismo`, `mezzotrasporto`, `canaleprenotazione`, `titolostudio`, `professione`, `esenzioneimposta`. Si chiede agli ospiti solo il minimo obbligatorio?
6. **Ambiente di test lombardo.** Esiste? Va chiesto ad ARIA (`info-flussituristici@ariaspa.it`) prima di CO-22 fase 3.
7. **Inoltro alla Questura da Ross1000.** Il fornitore pubblicizza l'inoltro automatico ad Alloggiati Web (T, [T7]); non è verificato che sia attivo in Lombardia. Se lo fosse, cambierebbe il disegno di CO-13 e CO-22 (un solo invio). Da chiarire con la Regione.
8. **Altre regioni.** Questa ricerca copre solo la Lombardia. Le regioni che usano Ross1000 condividono il tracciato (T), ma obbligo per le LT non imprenditoriali, scadenze e sanzioni sono regionali e vanno verificati uno per uno.

## Fonti

Consultate il **2026-09-24**. Per le fonti U è stato letto l'estratto indicizzato; l'accesso diretto era bloccato.

| ID | Fonte | Tipo | Livello | Supporta |
|---|---|---|---|---|
| F1 | [ISTAT: Movimento dei clienti negli esercizi ricettivi, informazioni sulla rilevazione](https://www.istat.it/informazioni-sulla-rilevazione/movimento-dei-clienti-negli-esercizi-ricettivi/) | ISTAT | U | regolamenti UE, IST-00139, obbligo di risposta, organi intermedi, file MOV/C |
| F2 | [ISTAT: Guida alla compilazione del Mod. C/59_G](https://www.istat.it/it/files/2011/02/Istruzioni_C59_G.pdf) (2013) | ISTAT | U | modello C/59, sezioni giornaliera e mensile, conservazione per 2 anni |
| F3 | [ISTAT: INDATA mtur](https://indata.istat.it/mtur/) | ISTAT | U | canale di trasmissione riservato alle Regioni |
| F4 | [ISTAT: Statistica Today, Turismo 2024](https://www.istat.it/wp-content/uploads/2026/03/Statistica-Today_Turismo-2024.pdf) (09/03/2026) | ISTAT | U | alloggi privati non imprenditoriali esclusi dagli esercizi ricettivi; primi dati |
| F5 | [ISTAT: Sanzioni amministrative in caso di mancata risposta](https://www.istat.it/attivita-e-servizi-per-tipo-di-utenti/rispondenti/sanzioni-amministrative/); [D.Lgs. 322/1989 (copia ISTAT)](https://www.istat.it/it/files/2011/04/dlgs322.pdf) | ISTAT | U (estratto), T (importi per le persone fisiche, da laleggepertutti.it) | artt. 7 e 11 D.Lgs. 322/1989 |
| L1 | [L.R. Lombardia 27/2015, art. 38](https://normelombardia.consiglio.regione.lombardia.it/NormeLombardia/Accessibile/main.aspx?view=showpart&idparte=lr002015100100027ar0038a) | Consiglio regionale | U | obbligo di comunicare i flussi (c. 8), CIR (c. 8-bis e 8-ter) |
| L2 | [Regione Lombardia: Codice Identificativo di Riferimento (CIR)](https://www.regione.lombardia.it/cultura-turismo-e-sport/imprese-e-professioni-turistiche/codice-identificativo-di-riferimento-cir) | Regione | U | ambito, CIR generato da Ross1000 |
| L3 | [Regione Lombardia: Manuale Ross1000, Profilo struttura ricettiva (versione 10/2023)](https://www.regione.lombardia.it/wps/wcm/connect/a27f961e-1aa2-4da3-be0f-8f4c38d72f17/MANUALE+ROSS1000+-+PROFILO+STRUTTURA+RICETTIVA+(versione+10_2023).pdf?MOD=AJPERES) | Regione | U | import `.txt` e `.xml`, riferimento alla nota WS GIES, accesso SPID/CIE/CNS, assistenza |
| L4 | [Città metropolitana di Milano: Comunicazione dei flussi turistici delle strutture ricettive](https://www.cittametropolitana.mi.it/servizi/Comunicazione-dei-flussi-turistici-delle-strutture-ricettive/) | Città metropolitana | U | obbligati, scadenza al giorno 5, sanzione €250–2.500 al mese, contatti |
| L5 | [Regione Lombardia: Informazioni importanti per l'avvio della struttura ricettiva](https://www.regione.lombardia.it/cultura-turismo-e-sport/imprese-e-professioni-turistiche/informazioni-importanti-per-avvio-della-struttura-ricettiva); [PoliS-Lombardia: Statistica](https://www.polis.lombardia.it/wps/portal/site/polis/attivita/statistica) | Regione | U | rilevazioni ISTAT servite, PoliS organo intermedio, Mov/C e CTT4 |
| L6 | [Provincia di Lecco: La comunicazione dei flussi turistici alla Provincia](https://www.provincia.lecco.it/2025/05/05/la-comunicazione-dei-flussi-turistici-alla-provincia/); [Strutture ricettive: inserimento e controllo flussi](https://www.provincia.lecco.it/2024/10/05/strutture-ricettive-inserimento-e-controllo-flussi-turistici-nel-portale-ross1000/) | Provincia | U | art. 38 c. 8 e art. 40 c. 9, art. 7 D.Lgs. 322/1989, giorno 5, inserimento giornaliero preferibile, obbligo anche senza movimento |
| L7 | [Provincia di Lecco: Ross 1000, trasmissione flussi turistici da altro gestionale](https://www.provincia.lecco.it/2023/07/06/ross-1000-trasmissione-flussi-turistici-da-altro-gestionale/) | Provincia | U | import da gestionale, manuali tecnici dopo l'abilitazione, "Lista SW House" |
| L8 | [Ross1000 Lombardia: FAQ](https://www.flussituristici.servizirl.it/Turismo5/res/FAQ.pdf); [portale](https://www.flussituristici.servizirl.it/) | Regione (ARIA) | U | unico canale dal 2018, riquadro "Manuali" con i tracciati, percorso di import |
| C1 | [Comune di Milano, Fare Impresa: Locazioni turistiche (LT)](https://fareimpresa.comune.milano.it/en/strutture-ricettive/locazioni-alloggi-finalita-turistiche-affitti-brevi) | Comune | U | CIA su impresainungiorno.gov.it, il CIR non lo rilascia il SUAP, percorso SUAP → Città metropolitana → Ross1000 |
| C3 | [Città metropolitana di Milano: CIR](https://www.cittametropolitana.mi.it/aree-tematiche/turismo/strutture-turistiche/cir/) | Città metropolitana | U | "Codice regione" in Ross1000 = CIR; D.G.R. XII/169 |
| R1 | [Regione Emilia-Romagna, Statistica: manuali e tracciati Ross1000](https://statistica.regione.emilia-romagna.it/metadati/rilevazioni/turismo/allegati-rilevazioni-turismo/manuali-e-tracciati-rilevazioni-turismo) (tracciato XML-WS 2.4, tracciato TXT v4 del 20/10/2022) | Regione (altra regione, stessa piattaforma) | U | struttura XML, campi obbligatori `idswh`, `tipoalloggiato`, `idcapo`, `cognome`; codifiche dalle tabelle della Polizia; `esenzioneimposta` per comune; scarto della schedina |
| T1 | [Chekin: ROSS1000 Lombardia e Turismo 5](https://chekin.com/it/blog/ross1000-lombardia-turismo-5-lombardia/) | blog commerciale | T | distinzione Ross1000 / Alloggiati |
| T2 | [VikBooking, `istat_ross1000.php`](https://github.com/common-repository/vikbooking/blob/HEAD/admin/helpers/report/istat_ross1000.php) (E4J, GPL) | codice open source, letto per intero | T | endpoint `checkinV2` per regione (Lombardia compresa), elenco e ordine dei campi XML, `<struttura>`, formato data `AAAAMMGG` |
| T3 | [ROSS 1000, Osservatorio Digitale del Turismo: tracciato XML](https://www.ross1000.it/source/tracciato-xml.pdf); [tracciati](https://www.ross1000.it/it/tracciati.php) | sito del fornitore GIES | T | "versione 3 del 18/03/2026", radice `<movimenti>`, oltre il 70% delle strutture nazionali |
| T5 | [GIES: Note per la trasmissione dei dati via webservice](https://s3.amazonaws.com/helpscout.net/docs/assets/5b154ead0428632c466a7e2f/attachments/61b435b9689c5f49b2d18d5e/Note-trasmissione-WS_GIES.pdf) | nota del fornitore, letta per intero (copia su HelpScout) | T | i quattro parametri, "Codice regione", procedura per la password di trasmissione |
| T6 | [`Laischon/Turismo5-WebService`, `Turismo5.Class.php`](https://github.com/Laischon/Turismo5-WebService/blob/HEAD/Turismo5.Class.php) | codice open source, letto per intero | T | busta SOAP, namespace, `inviaMovimentazione`, HTTP Basic, `risultatiGiorno` |
| T7 | [ROSS 1000: invio automatico dei dati di pubblica sicurezza ad Alloggiati Web](https://www.ross1000.it/it/invio-automatico-dei-dati-di-pubblica-sicurezza-da-ross-1000-al-portale-alloggiati-web.php) | sito del fornitore | T | inoltro alla Questura (attivazione in Lombardia non verificata) |
