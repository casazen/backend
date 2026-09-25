# Codice CIN (Codice Identificativo Nazionale)

## Riferimento Normativo
- **Fonte primaria**: D.L. 18/10/2023 n. 145, convertito in L. 15/12/2023 n. 191 (art. 13-ter)
- **Decreto attuativo**: D.M. Ministero Turismo prot. n. 0016726/24 del 03/09/2024. Correzione RS-2: il prot. 16726 è il "decreto interoperabilità" del **06/06/2024**; il 03/09/2024 è la data dell'Avviso in GU di entrata in funzione della BDSR.
- **Entrata in vigore**: 01/01/2025 (scadenza richiesta: 01/03/2026 per operatori esistenti — data **non confermata** da fonti ufficiali, vedi "Formato verificato (2026-09)" → Note di verifica)

## Sintesi dell'Obbligo

Il Codice Identificativo Nazionale (CIN) è un codice univoco obbligatorio per:
- Tutte le strutture ricettive turistiche
- Tutti gli immobili destinati a locazione breve o turistica

Ogni unità immobiliare deve essere registrata nella **Banca Dati delle Strutture Ricettive (BDSR)** del Ministero del Turismo.

## Scadenze
- **01/01/2025**: Obbligo in vigore
- **01/03/2026**: Scadenza per operatori già attivi (non confermata: le fonti ufficiali trovate indicano applicabilità dal 02/11/2024 e termine di acquisizione 01/01/2025)
- **60 giorni** dall'inizio attività per nuovi operatori

## Modalità di Richiesta
1. Accesso al portale BDSR: https://www.ministeroturismo.gov.it/banca-dati-strutture-ricettive/
2. Autenticazione tramite SPID o CIE
3. Inserimento dati struttura e documentazione:
   - **Strutture ricettive**: SCIA presentata al SUAP
   - **Locazioni turistiche non imprenditoriali**: comunicazione comunale (ante 02/11/2024)
   - **Locazioni turistiche imprenditoriali**: comunicazione comunale (post 02/11/2024)
4. Rilascio CIN da parte del Ministero

## Obblighi di Esposizione

### Esposizione Fisica
Il CIN deve essere esposto all'esterno dell'immobile in modo **visibile e leggibile**:
- Sulla targa esterna
- Sul citofono
- All'ingresso della proprietà

### Esposizione Digitale
Il CIN deve essere **obbligatoriamente** inserito in:
- Tutti gli annunci online
- Tutte le piattaforme OTA (Airbnb, Booking.com, Expedia, VRBO, etc.)
- Qualsiasi materiale promozionale o pubblicitario

## Sanzioni
(art. 13-ter, comma 9, D.L. 145/2023; corretto il 2026-09-23, vedi "Formato verificato (2026-09)")
- **Assenza del CIN**: da €800 a €8.000, in relazione alle dimensioni della struttura o dell'immobile
- **Mancata esposizione o mancata indicazione del CIN negli annunci**: da €500 a €5.000 per ciascuna struttura o unità in cui è accertata la violazione, più la rimozione immediata dell'annuncio irregolare

## Impatto su CasaZen

### Funzionalità Coinvolte
1. **Property Management**
   - Campo obbligatorio `CINCode` nell'entità Property
   - Validazione formato CIN
   - Controllo presenza CIN prima di pubblicazione annunci

2. **OTA Integration**
   - Sincronizzazione automatica CIN verso piattaforme OTA
   - Verifica presenza CIN in listing esistenti
   - Alert per immobili senza CIN valido

3. **Compliance Dashboard**
   - Monitor scadenze CIN per proprietà
   - Alert per immobili vicini alla scadenza 01/03/2026
   - Report proprietà non conformi

4. **Onboarding Nuove Proprietà**
   - Workflow guidato per richiesta CIN
   - Checklist documentazione necessaria
   - Link diretto al portale BDSR

### Criticità Tecniche
- **Validazione formato**: struttura documentata da fonti istituzionali. Regex, normalizzazione e controllo ISTAT sono nella sezione "Formato verificato (2026-09)" qui sotto
- **Aggiornamenti**: necessità di mantenere allineamento con BDSR
- **Scadenza imminente**: molti proprietari potrebbero non aver ancora richiesto il CIN

## Formato verificato (2026-09)

> Ricerca del task RS-2 (risanamento), eseguita il 2026-09-23. Chiude il "da verificare" sul formato (difetti A5-05 e R-02, correzione del codice nel task CO-01).

**Esito:** la **composizione ufficiale** del CIN è documentata da fonti istituzionali: il decreto interoperabilità del Ministero del Turismo e la Provincia autonoma di Trento. Tutti i CIN reali controllati la rispettano. La regex qui sotto deriva da quella composizione, **non** dal fallback D13 del product owner.

**Limite della verifica:** da questo ambiente il proxy di rete blocca la lettura diretta (WebFetch) di tutti i domini consultati: `ministeroturismo.gov.it`, `bdsr.ministeroturismo.gov.it`, `normattiva.it`, `gazzettaufficiale.it` e i siti regionali. I fatti vengono quindi dagli **estratti del motore di ricerca** (WebSearch) delle pagine ufficiali elencate in "Fonti", non dalla lettura del testo integrale. Prima di andare in produzione conviene una lettura diretta del decreto interoperabilità, in particolare della "Figura 3". Due punti vanno confermati: la lunghezza variabile della parte casuale e il pattern lettera + cifra della categoria.

### Composizione ufficiale

| Posizione | Lunghezza | Contenuto | Caratteri |
|---|---|---|---|
| 1–2 | 2 | prefisso `IT` | fisso |
| 3–5 | 3 | codice ISTAT della **provincia** | cifre |
| 6–8 | 3 | codice ISTAT del **comune**; le posizioni 3–8 formano il codice ISTAT del comune a 6 cifre | cifre |
| 9–10 | 2 | codice di **classificazione ISTAT** della categoria (struttura ricettiva o unità in locazione breve/turistica), es. `A1`, `B4`, `C2` | lettera + cifra (dedotto, vedi sotto) |
| 11–18 | **max 8** | stringa alfanumerica **casuale** | `A–Z`, `0–9` |

Il testo ufficiale dice: "prefisso IT e codice ISTAT della Provincia (3 caratteri); codice ISTAT del Comune (3 caratteri); codice di classificazione ISTAT delle strutture turistico-ricettive e delle unità immobiliari in locazione breve o per finalità turistiche (2 caratteri); stringa alfanumerica casuale (massimo 8 caratteri)" (Provincia autonoma di Trento; stessa composizione nel decreto interoperabilità MiTur).

Cosa se ne ricava con certezza:
- **Prefisso** `IT`, seguito da **6 cifre** (codice ISTAT del comune: 3 cifre di provincia + 3 di comune).
- **Lunghezza**: 18 caratteri nella forma tipica. Tutti i CIN reali controllati sono di 18 caratteri. Il testo ufficiale però dice "massimo 8" per la parte casuale, quindi la lunghezza ammessa va da 11 a 18.
- **Separatori**: nessuno. Spazi e trattini non fanno parte del codice. Gli esempi divulgativi spaziati, come "IT 039 007 B1 00000", servono solo alla leggibilità.
- **Checksum**: nessuna fonte ne documenta uno. La coda è casuale, quindi non è possibile un controllo di integrità oltre alla struttura.
- **Invarianza**: il CIN è generato al primo inserimento in BDSR e *rimane invariato anche dopo variazioni di classificazione e/o localizzazione* (FAQ BDSR / Allegato 1 del Protocollo). Il codice ISTAT e la categoria incorporati possono quindi non coincidere con i dati attuali dell'immobile.
- **Ricodifica**: alcune Regioni/PA hanno un CIR, o un codice provinciale, già composto da codici ISTAT (provincia + comune), classificazione ISTAT e sequenza casuale. Per queste il CIN si ottiene **anteponendo `IT`** al CIR (decreto interoperabilità, prot. 16726 del 06/06/2024). Negli altri casi è la BDSR a generare un CIN nuovo.
- **Categoria come lettera + cifra**: è una **deduzione**, perché la fonte ufficiale dice solo "2 caratteri". Tutti gli esempi reali (`A1`, `B4`, `C2`) e le categorie citate nel Manuale Operatore Privato BDSR ("se la categoria ISTAT indicata è C1 o C2, è selezionabile solo persona fisica") hanno questa forma. La tabella completa delle categorie (Allegato 2 "Data Quality Rules") non è stata letta.

### Esempi reali (solo verifica, non fonte primaria)

| CIN | Luogo / tipo | Scomposizione | Visto su |
|---|---|---|---|
| `IT058091C27G5FFZDZ` | Roma, appartamento | 058·091·C2·7G5FFZDZ | ciaksiaffitta.com/it |
| `IT027042C2IT3TRNHJ` | Venezia, appartamento | 027·042·C2·IT3TRNHJ | estratto di ricerca, pagina non identificata con certezza (probabilmente affittibreviveneto.com) |
| `IT058091A1K2XKTJ9H` | Roma, Hotel Borromeo | 058·091·A1·K2XKTJ9H | hotelborromeo.com |
| `IT015146A12HOLV2MZ` | Milano, ibis Milano Centro | 015·146·A1·2HOLV2MZ | all.accor.com/hotel/0933 |
| `IT048017A1O9HCDONC` | Firenze, Porta Rossa Hotel | 048·017·A1·O9HCDONC | nh-collection.com |
| `IT048017A1FOG7WU8P` | Firenze, NH Firenze | 048·017·A1·FOG7WU8P | minorhotels.com |
| `IT048017B42742QNBZ` | Firenze, Max Studios | 048·017·B4·2742QNBZ | florence-hotels365.com |

Anche gli esempi vengono da estratti WebSearch delle pagine indicate. I codici ISTAT incorporati (058091 Roma, 027042 Venezia, 015146 Milano, 048017 Firenze) coincidono con quelli di `Casazen.Core/Regulatory/ItalianComuneRegistry.cs`. Le lettere `I` e `O` compaiono nella parte casuale, quindi non si escludono caratteri ambigui.

### Regex consigliata

Si applica **dopo la normalizzazione**.

```
^IT\d{6}[A-Z]\d[A-Z0-9]{1,8}$
```

Versione con gruppi, per il controllo ISTAT (.NET):

```
^IT(?<provincia>\d{3})(?<comune>\d{3})(?<categoria>[A-Z]\d)(?<casuale>[A-Z0-9]{1,8})$
```

- **Forma tipica a 18 caratteri**: `^IT\d{6}[A-Z]\d[A-Z0-9]{8}$`. Usarla per placeholder, esempi UI e test, **non** per rifiutare. Un CIN valido più corto di 18 caratteri può al massimo generare un avviso.
- Il pattern lettera + cifra della categoria fa sì che il vecchio formato inventato `IT-12345-1234567890`, una volta normalizzato (`IT123451234567890`), venga **rifiutato**. Serve alla migrazione dati di CO-01 per individuare i CIN fittizi. Con `[A-Z0-9]{2}` passerebbe.
- La vecchia regex `^IT-\d{5}-\d{10}$` (5 punti nel codice, vedi A5-05) è **errata**: rifiuta tutti i CIN reali.
- Il fallback D13 (`^IT\d{6}[A-Z0-9]{10}$`) **non serve**, perché la composizione ufficiale è stata trovata. Accetterebbe i CIN a 18 caratteri, ma rifiuterebbe quelli con parte casuale più corta, che il testo ufficiale ammette.

### Implementazione nel codice (CO-01, 2026-09-23)

- Unica fonte di verità: `Casazen.Core/Regulatory/CinFormat.cs` (backend) e `src/lib/cin-format.ts` (frontend, stessa regola).
- Regex applicata dopo la normalizzazione: `^IT\d{6}[A-Z0-9]{2}[A-Z0-9]{1,8}$` (solo cifre ASCII). Per scelta del piano di risanamento la categoria accetta qualsiasi coppia alfanumerica, perché il testo ufficiale dice solo "2 caratteri".
- Con `[A-Z0-9]{2}` il vecchio formato inventato normalizzato (`IT` + 15 cifre) passerebbe, quindi `CinFormat` lo rifiuta con una regola esplicita: `^IT\d{15}$`.
- La migrazione `NormalizeCinCodes` salva in forma normalizzata i CIN già presenti. Quelli che restano fuori formato non vengono cancellati: lo stato calcolato li mostra come "non valido". Dettagli in `docs/runbooks/cin-format.md`.
- Controllo ISTAT: predisposto (`CinFormat.HasIstatComuneMismatch`, solo avviso) ma non ancora collegato, perché la property ha solo la città in testo libero. Si collega quando arriva l'anagrafica ISTAT (SU-04).

### Normalizzazione (prima di validare e salvare)

1. `null`, stringa vuota o solo spazi: nessun CIN, non è un CIN invalido.
2. Rimuovere **tutti gli spazi** (anche interni e non-breaking) e i **trattini** (`-`, `‐`, `–`, `—`).
3. Convertire in **maiuscolo** con cultura invariante (`ToUpperInvariant`).
4. Validare con la regex. **Salvare ed esporre la forma normalizzata**, senza separatori e in maiuscolo.
5. Non rimuovere altri caratteri (punti, barre, prefissi come "CIN:"). Un input che li contiene va rifiutato con un errore chiaro, non "ripulito" in silenzio.

| Input | Normalizzato | Esito |
|---|---|---|
| `it 058091 c2 7g5ffzdz` | `IT058091C27G5FFZDZ` | valido |
| `IT-058091-C2-7G5FFZDZ` | `IT058091C27G5FFZDZ` | valido |
| `IT039007B100000` | invariato | valido (parte casuale di 5, ammessa da "max 8"; eventuale avviso "non 18") |
| `IT-12345-1234567890` | `IT123451234567890` | **invalido** (vecchio formato fittizio) |
| `015146-CNI-01894` | `015146CNI01894` | **invalido** (è un CIR lombardo, non un CIN) |
| `IT058091C27G5FFZDZX` | invariato | **invalido** (19 caratteri) |

### Controllo del codice ISTAT

- Le posizioni 3–8 (`provincia` + `comune`) sono il codice ISTAT del comune a 6 cifre.
- Il confronto con il comune della property deve dare **solo un avviso non bloccante**, mai un rifiuto, per tre motivi:
  1. Il CIN resta invariato dopo variazioni di localizzazione (fonte ufficiale, vedi sopra).
  2. I codici ISTAT cambiano con fusioni di comuni e riordini provinciali, mentre il CIN no.
  3. `ItalianComuneRegistry` oggi contiene solo 12 comuni: per gli altri il confronto non è possibile e va saltato, non trattato come mancata corrispondenza.
- Non validare la **categoria** (`A1`, `C2`…) contro il tipo di property, per lo stesso motivo dell'invarianza.
- **Esistenza del CIN**: la BDSR ha una pagina pubblica di consultazione (`https://bdsr.ministeroturismo.gov.it/ricerca-cin`). Non è stata trovata un'API documentata, quindi la verifica di esistenza resta manuale e fuori dal perimetro della validazione.

### CIN e codici regionali (CIR / CIS / CIPAT / CIU)

- I codici regionali hanno formati **diversi per ogni regione** e non sono CIN. Esempi:
  - **Lombardia (CIR)**: `015146-CNI-01894`, cioè 6 cifre ISTAT del comune + 3 lettere di tipologia + 5 caratteri progressivi. La composizione viene da Regione Lombardia; l'esempio da un annuncio pubblico (terza parte, verifica RS-9). Un hotel di Milano ha CIN `IT015146A12HOLV2MZ`: il CIN **non si ricava** dal CIR. Norme, percorso, esposizione e sanzioni sono in `cir-lombardia.md` (RS-9, 2026-09-24).
  - **Puglia (CIS)**: il CIS è stato ricodificato in CIR e trasmesso alla BDSR, che rilascia il CIN (Regione Puglia, In-Formati).
  - **Veneto**: il CIR assegnato da Ross1000 è necessario per chiedere il CIN in BDSR (Regione Veneto).
  - **Provincia autonoma di Trento**: il CIN sostituisce il CIPAT provinciale.
  - **Valle d'Aosta**: il CIR degli alloggi a uso turistico è stato ricodificato in BDSR e non corrisponde più a quello assegnato in origine.
  - **CIU**: denominazione usata in alcuni contesti regionali per un codice unico di struttura. Non è stata trovata una fonte ufficiale sul formato, quindi va trattato come un codice regionale qualsiasi.
- **Conversione:** non esiste una regola generale. Solo dove il CIR ha già la struttura nazionale il CIN è `IT` + CIR, e la ricodifica la fa la BDSR, non l'operatore.
- **Per CasaZen:**
  - non derivare mai il CIN dal CIR;
  - non accettare un CIR nel campo CIN (la regex lo rifiuta);
  - se serve il codice regionale, salvarlo in un campo separato.
- Se il codice regionale vada esposto accanto al CIN dipende dalla legge di ciascuna regione. Qui non è stato verificato, salvo la **Lombardia**: dal 02/11/2024 l'indicazione del CIN negli annunci sostituisce quella del CIR, che resta propedeutico al CIN (fonti regionali e provinciali, vedi `cir-lombardia.md` § 5).

### Obbligo di esposizione e sanzioni

Riferimento: art. 13-ter D.L. 18/10/2023 n. 145, convertito dalla L. 15/12/2023 n. 191 (GU 16/12/2023).

- **Comma 8, esposizione**: chi concede in locazione per finalità turistiche o breve, e il titolare di una struttura ricettiva, deve **esporre il CIN all'esterno dello stabile**, rispettando gli eventuali vincoli urbanistici e paesaggistici. Deve anche **indicarlo in ogni annuncio**, ovunque pubblicato e comunicato.
- **Intermediari immobiliari e gestori di portali telematici**: devono indicare il CIN negli annunci. Il comma esatto non è stato verificato sul testo integrale.
- **Comma 9, sanzioni**:
  - assenza del CIN: **€800–8.000**, in relazione alle dimensioni della struttura o dell'immobile;
  - mancata esposizione o indicazione: **€500–5.000** per ciascuna struttura o unità in cui è accertata la violazione, più la **rimozione immediata dell'annuncio** irregolare.
- **Applicabilità**:
  - Avviso di entrata in funzione della BDSR in GU parte II n. 103 del 03/09/2024, da cui l'art. 13-ter si applica dal **02/11/2024** (60° giorno);
  - termine per l'acquisizione del CIN posticipato al **01/01/2025** (news MiTur).
- **Comma 7, dispositivi di sicurezza**: vedi `sicurezza.md` (task RS-3).
- **Reg. (UE) 2024/1028, applicabile dal 20/05/2026**: secondo fonti secondarie il CIN funge da numero di registrazione UE. Il formato non cambia. Non verificato su fonte ministeriale.

### Note di verifica sul resto di questo file

- La sezione "Sanzioni" è stata corretta: prima indicava €800–8.000 anche per la mancata esposizione, che invece costa €500–5.000.
- "Decreto attuativo" (riga iniziale): il prot. 16726 è il decreto interoperabilità del 06/06/2024, non un D.M. del 03/09/2024.
- La scadenza "01/03/2026 per operatori già attivi" (righe iniziali e "Scadenze") **non compare in nessuna fonte ufficiale trovata**. Va verificata prima di usarla nel codice (`CinDeadlineAlertJob`, task CO-20).
  - CO-20 (2026-09-25): la data non è più nel codice. È la configurazione `Cin:ExposureDeadline`, vuota di default: senza data console e alert mostrano solo l'obbligo. Vedi `docs/runbooks/cin-format.md`, "CIN deadline and host alerts (CO-20)".

### Fonti

Tutte consultate il 2026-09-23 tramite estratti WebSearch. La lettura diretta non era possibile: WebFetch è bloccato dal proxy.

| Fonte | Tipo | Data documento | Cosa supporta |
|---|---|---|---|
| [Decreto interoperabilità BDSR, prot. 16726](https://www.ministeroturismo.gov.it/wp-content/uploads/2024/06/Decreto-interoperabilita-BDSR_signed-1.pdf) | Ministero del Turismo | 06/06/2024 | composizione (prefisso IT, ISTAT, classificazione 2 caratteri, casuale max 8); ricodifica CIR → `IT`+CIR; "Figura 3" |
| [Provincia autonoma di Trento: come ottenere il CIN](https://www.provincia.tn.it/en/Services/How-to-Obtain-the-National-Identification-Code-NIC-for-Accommodation-Facilities) | ente pubblico | n.d. | testo esplicito della composizione; CIN sostituisce il CIPAT; esposizione esterna |
| [Allegato 1 al Protocollo d'intesa BDSR](https://www.ministeroturismo.gov.it/wp-content/uploads/2022/10/BDSR_Protocollo-Regioni.PA-MiTur_Allegato1.pdf) | Ministero del Turismo | 2022 | CIN da localizzazione + classificazione ISTAT + sequenza casuale; generato al primo inserimento e invariato |
| [FAQ BDSR](https://www.ministeroturismo.gov.it/faq-banca-dati-strutture-ricettive-bdsr/) | Ministero del Turismo | aggiornate al 23/04/2026 (secondo ufficiocommercio.it) | invarianza del CIN; ambito |
| [Manuale Operatore Privato BDSR v10.0](https://bdsr.ministeroturismo.gov.it/assets/MITUR_BDSR_Manuale_Operatore_Privato_10.0-DMbxyZPe-Cigy_Kmo.pdf) | Ministero del Turismo | n.d. (v8.0: 12/2024) | categorie ISTAT C1/C2 solo persona fisica |
| [BDSR: Ricerca CIN](https://bdsr.ministeroturismo.gov.it/ricerca-cin) | Ministero del Turismo | — | consultazione pubblica dei CIN |
| [Art. 13-ter, GU coordinata](https://www.gazzettaufficiale.it/atto/serie_generale/caricaArticolo?art.versione=1&art.idGruppo=3&art.flagTipoArticolo=0&art.codiceRedazionale=23A06909&art.idArticolo=13&art.idSottoArticolo=3&art.idSottoArticolo1=10&art.dataPubblicazioneGazzetta=2023-12-16&art.progressivo=0) / [Normattiva](https://www.normattiva.it/uri-res/N2Ls?urn%3Anir%3Astato%3Adecreto.legge%3A2023-10-18%3B145%7Eart13ter=) | GU / Normattiva | 16/12/2023 | esposizione (c. 8), sanzioni (c. 9), sicurezza (c. 7) |
| [Avviso BDSR in GU parte II](https://www.gazzettaufficiale.it/atto/parte_seconda/caricaDettaglioAtto/originario?atto.dataPubblicazioneGazzetta=2024-09-03&atto.codiceRedazionale=TX24ADA8917) | GU | 03/09/2024 | decorrenza dal 02/11/2024 |
| [MiTur: termine posticipato al 1° gennaio 2025](https://www.ministeroturismo.gov.it/cin-termine-per-lacquisizione-spostato-al-1-gennaio-2025/) | Ministero del Turismo | 2024 | termine di acquisizione |
| [Regione Lombardia: CIR](https://www.regione.lombardia.it/cultura-turismo-e-sport/imprese-e-professioni-turistiche/codice-identificativo-di-riferimento-cir) | Regione | n.d. | formato del CIR lombardo |
| [Regione Puglia In-Formati: ottenere il CIN](https://info.ect.regione.puglia.it/come-fare-per/ottenere-il-cin/) | Regione | n.d. | CIS → CIR → CIN |
| [Regione Veneto: CIN](https://www.regione.veneto.it/web/turismo/codice-identificativo-nazionale) | Regione | n.d. | CIR Ross1000 prerequisito del CIN |
| [Valle d'Aosta: FAQ BDSR alloggi uso turistico](https://www.regione.vda.it/allegato.aspx?pk=115222) | Regione | n.d. | CIR ricodificato in BDSR |
| [Reg. (UE) 2024/1028](https://eur-lex.europa.eu/legal-content/IT/ALL/?uri=OJ:L_202401028) | EUR-Lex | 11/04/2024 | numero di registrazione UE (fonti secondarie: il CIN funge da tale) |

## Sorgenti
- [Regione Toscana - CIN](https://www.regione.toscana.it/-/codice-identificativo-nazionale-cin-per-le-locazioni-turistiche-e-le-strutture-ricettive-turistiche)
- [Lodgify - Guida CIN 2026](https://www.lodgify.com/blog/it/codice-cin-affitti-brevi/)
- [PraticheCasa - CIN 2026](https://www.pratichecasa.it/parere-di-un-esperto/cin-alloggi-turistici-2026/)

**Data consultazione**: 2026-03-27
