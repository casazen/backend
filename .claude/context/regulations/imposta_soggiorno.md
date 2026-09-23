# Imposta di Soggiorno

## Riferimento Normativo
- **Fonte primaria**: D.Lgs. 14/03/2011 n. 23 (art. 4)
- **Modifiche recenti**: L. 30/12/2018 n. 145 (Legge di Bilancio 2019)
- **Aggiornamenti 2026**: L. 30/12/2025 n. 199 (Legge di Bilancio 2026)

## Sintesi dell'Obbligo

L'imposta di soggiorno è una **tassa comunale** applicata ai pernottamenti nelle strutture ricettive e negli immobili in locazione turistica.

### Caratteristiche Principali
- Istituita dai **Comuni** (non obbligatoria, ma sempre più diffusa)
- A carico dell'**ospite**, ma riscossa dal **gestore** della struttura
- Importo variabile per Comune e tipologia struttura
- Destinata a finanziare interventi turistici, culturali e territoriali

## Novità 2026

> **Nota RS-7 (2026-09-23).** Questa sezione viene da fonti terze (marzo 2026) e non va usata per calcolare l'imposta. I "12 €" di Milano riguardano gli alberghi di fascia alta; per le locazioni brevi a Milano la tariffa 2026 è 9,50 €. Tariffe, tetti di notti ed esenzioni per età dei comuni pilota, con le fonti ufficiali, sono nella sezione «Tariffe verificate (2026-09)» in fondo al file e in `Casazen.Infrastructure/Data/Seeds/tourist-tax/rates.csv`.

### Aumenti Generalizzati
Molti Comuni hanno aumentato le tariffe per il 2026:
- **Capoluoghi di provincia**: tetto massimo ~€7/giorno/persona
- **Città d'arte e turistiche**: tetto massimo fino a €12/giorno/persona
- **Comuni Olimpiadi 2026**: incremento ulteriore fino a €5/notte (Milano, Cortina, etc.)

### Incremento Olimpico (Milano-Cortina 2026)
I Comuni che ospitano eventi olimpici possono applicare un **supplemento temporaneo**:
- **Milano**: da €7 a €12/notte
- **Cortina**: aumenti significativi durante periodo olimpico
- **Altri comuni olimpici**: incremento variabile

### Gettito Nazionale 2026
- Gettito previsto: **€1,3 miliardi** (+9,2% rispetto al 2025)
- Numero Comuni con imposta: **1.409 comuni** (+20 rispetto al 2025)

### Nuove Destinazioni d'Uso
Parte del gettito aggiuntivo 2026 sarà destinato a:
- Spese per **inclusività sociale**
- Assistenza ai **minori**
- Interventi turistici e culturali tradizionali

## Modalità di Applicazione

### Chi Riscuote
- Il **gestore della struttura** (proprietario, host, property manager)
- Obbligo di riscossione anche per locazioni brevi private

### Chi Paga
- L'**ospite** al momento del soggiorno
- Pagamento contestuale o separato dal canone di locazione

### Come si Versa
- **Cadenza**: mensile, trimestrale o semestrale (varia per Comune)
- **Modalità**: F24, bonifico, o piattaforme comunali dedicate
- **Scadenze**: fissate da ciascun Comune (es. entro il 16 del mese successivo)

### Esenzioni Comuni
Tipicamente esenti (da verificare per ciascun Comune):
- Minori sotto determinata età (es. under 14)
- Residenti nel Comune
- Soggiorni per motivi di salute/cura
- Accompagnatori di disabili
- Forze dell'ordine in servizio

## Modello 21 - Dichiarazione Annuale
Molti Comuni richiedono la **presentazione annuale** del modello dichiarativo con:
- Riepilogo pernottamenti
- Importi riscossi
- Eventuali esenzioni applicate
- Versamenti effettuati

**Scadenza tipica**: 30 giugno dell'anno successivo

## Impatto su CasaZen

### Funzionalità Coinvolte

1. **Booking Management**
   - Calcolo automatico imposta soggiorno per prenotazione:
     - In base a Comune della proprietà
     - Numero ospiti e notti
     - Eventuali esenzioni (minori, residenti)
   - Addebito separato all'ospite
   - Visualizzazione breakdown costi (canone + imposta)

2. **Payment Processing**
   - Riscossione imposta contestualmente al pagamento
   - Separazione contabile: canone vs. imposta soggiorno
   - Tracking imposta riscossa ma non ancora versata (debito verso Comune)

3. **Configurazione Tariffe Comunale**
   - Database tariffe per Comune aggiornato
   - Configurazione per tipologia struttura
   - Gestione esenzioni configurabili
   - Aggiornamenti tariffari (es. incrementi 2026)

4. **Reporting e Versamenti**
   - Report mensile/trimestrale imposta riscossa
   - Generazione F24 o file per versamento
   - Storico versamenti effettuati
   - Preparazione dati per Modello 21

5. **Compliance Dashboard**
   - Alert scadenze versamenti per Comune
   - Monitor imposta riscossa vs. versata
   - Segnalazione anomalie (mancati versamenti)
   - Generazione dichiarazione annuale (Modello 21)

6. **Guest Portal**
   - Informativa trasparente su imposta dovuta
   - Breakdown dettagliato costi in fase booking
   - Ricevuta separata per imposta soggiorno

### Criticità Tecniche
- **Database tariffe comunali**: 1.409+ Comuni con tariffe variabili, da mantenere aggiornato
- **Variabilità normative**: ogni Comune ha regolamento diverso, scadenze diverse
- **Esenzioni complesse**: logica di esenzioni varia (età minori, residenza, etc.)
- **Versamenti multi-Comune**: un proprietario con immobili in Comuni diversi ha scadenze multiple
- **Incrementi temporanei**: gestire aumenti straordinari (Olimpiadi, eventi speciali)

### Dati da Tracciare
Per ogni Comune:
- Tariffe vigenti per tipologia struttura
- Scadenze versamenti (mensili/trimestrali/semestrali)
- Modalità versamento (F24, bonifico, portale)
- Regole esenzioni
- Formato e scadenza Modello 21

Per ogni prenotazione:
- Imposta soggiorno calcolata
- Imposta riscossa (data, importo)
- Esenzioni applicate
- Versamento effettuato (data, riferimento F24)

## Sorgenti
- [Idealista - Tassa soggiorno 2026](https://www.idealista.it/news/vacanze/mercato-turistico/2026/01/15/312295-tassa-di-soggiorno-2026-quanto-aumenta-e-come-cambiano-le-tariffe-nelle-citta)
- [TuttoTributi - Imposta soggiorno 2026](https://www.tuttotributi.it/imposta-di-soggiorno-il-gettito-2026-arriva-a-13-miliardi/)
- [Comune di Napoli - Imposta soggiorno 2026](https://www.comune.napoli.it/articolo_tematico/tributi-locali/imposta-di-soggiorno/imposta-di-soggiorno-2026/)

**Data consultazione**: 2026-03-27

## Tariffe verificate (2026-09)

> Ricerca del task RS-7 (risanamento), eseguita il **2026-09-23**. È la base dati del task CO-03 (seed delle tariffe) e di BK-03 (calcolo unico su `TouristTaxRate`). **Non è un parere legale.** Prima di mostrare una tariffa agli host come "ufficiale" vanno chiusi i punti in "Dubbi aperti".

**Limite della verifica.** Da questo ambiente il proxy di rete blocca la lettura diretta (WebFetch e curl, HTTP 403) di tutti i domini ufficiali provati: `comune.milano.it` (anche `servizicrm.` e `fareimpresa.`), `comune.roma.it` (anche `marcoaurelio.`), `comune.fi.it` (anche `servizi.`), `comune.napoli.it`, `comune.torino.it`, `comune.venezia.it`, `comune.bologna.it`, `comune.como.it`, `comune.seveso.mb.it`, `comune.cesano-maderno.mb.it`, `istat.it`, `finanze.gov.it`, `gazzettaufficiale.it`, `normattiva.it`. I dati vengono quindi dagli **estratti indicizzati** (WebSearch filtrata sui domini ufficiali). Tutti i valori sono **"non verificato alla fonte"**: prima del go-live di CO-03 conviene aprire i PDF ufficiali elencati in "Fonti" da una rete non filtrata e confermare ogni riga.

**Legenda del livello di verifica** (colonna `verified` del CSV, stessa legenda di RS-1 e RS-3)
- **U**: ufficiale. Dato letto nell'estratto di una pagina o di un atto del comune (o di un ente pubblico).
- **D**: dedotto. Ragionamento nostro a partire da dati U o T (per esempio da una percentuale o da una regola di decorrenza).
- **T**: fonte terza. Gestionali, blog, stampa. È un indizio, **non una fonte**.

### File dati

`Casazen.Infrastructure/Data/Seeds/tourist-tax/rates.csv` (UTF-8, separatore virgola, campi con virgole tra doppi apici). Una riga per comune e per categoria, zona o stagione quando la tariffa cambia.

| Colonna | Significato |
|---|---|
| `istat_code` | Codice ISTAT del comune (6 cifre), preso dalle pagine ISTAT 8milaCensus (vedi "Codici ISTAT") |
| `comune` | Nome ufficiale |
| `rate_per_person_night_eur` | Euro per persona per notte. **Vuoto = nessuna tariffa fissa trovata o applicabile** (non vuol dire 0) |
| `max_nights` | Numero massimo di pernottamenti tassati (consecutivi, salvo quanto detto in nota) |
| `min_age_exempt` | Età da cui l'imposta è dovuta: gli ospiti con età **inferiore** a questo valore sono esenti. Corrisponde a `TouristTaxRate.MinimumAge` |
| `valid_from` | Data di decorrenza della tariffa (ISO 8601) |
| `zone_or_notes` | Categoria di struttura, zona o stagione, atto di riferimento, altre esenzioni, livello di verifica dei campi diversi dalla tariffa |
| `source_url` | Fonte ufficiale principale della riga |
| `retrieved_at` | Data di consultazione |
| `verified` | Livello U/D/T **dell'importo della tariffa** (per le righe senza tariffa: dell'affermazione in nota) |

### Riepilogo per comune (locazioni brevi / extralberghiero)

| Comune | ISTAT | €/persona/notte | Max notti | Esenti sotto i | Decorrenza | Livello | Note chiave |
|---|---|---|---|---|---|---|---|
| Milano | 015146 | 9,50 | 14 consecutive | 18 anni | 01/01/2026 | U | Tariffa 2026 per l'incremento olimpico, confermata dal 01/04/2026; 2027 non nota |
| Roma | 058091 | 6,00 (locazioni brevi); CAV cat. 1 6,00, cat. 2 5,00 | 10 consecutive nell'anno solare | 10 anni | 01/10/2023 | T (loc. brevi), U (CAV) | Nessuna variazione 2026 trovata |
| Como | 013075 | 3,00 | 4 consecutive | 14 anni | 01/01/2024 | U | Stessa tariffa per CAV, locazioni turistiche e brevi, B&B |
| Firenze | 048017 | 6,00 | 7 consecutive | 12 anni | 01/02/2025 | U (tariffa), D/T (data) | Da 5,50 a 6,00, delib. G.C. 535/2024 |
| Napoli | 063049 | 6,00 | 14 consecutive | 14 anni (formula ambigua) | 01/05/2026 | U | Delib. G.C. 89/2026; tariffa gennaio-aprile 2026 non verificata |
| Torino | 001272 | 3,80 | 7 (su base annua dal 01/04/2026) | 13 anni | 01/04/2026 | T | Aumento 2,30 → 3,80 ufficiale solo per i B&B |
| Venezia | 027042 | da 2,10 a 5,00 per gruppo catastale e stagione | 5 consecutive | 10 anni (50% da 10 a 16) | 01/04/2025 | U (Gruppo 3 alta stagione: D) | Tariffa per categoria catastale dell'alloggio, non per zona |
| Bologna | 037006 | — (10,5% del prezzo, max 7,00) | 5 consecutive | 14 anni | 01/01/2026 (D) | U | Tariffa percentuale: non rappresentabile in `TouristTaxRate` |
| Seveso | 108040 | — | — | — | — | D | Imposta non trovata; assenza non confermata da un atto |
| Cesano Maderno | 108019 | — | — | — | — | D | Imposta non trovata; esistenza da verificare |

### Dettaglio e fonti per comune

**Milano** (fonti M1-M5)
- Case e appartamenti per vacanze e locazioni brevi (art. 4 D.L. 50/2017): **9,50 €** per persona per notte dal **01/01/2026**. Tariffe approvate con delib. G.C. n. 1418 del 13/11/2025, **valide solo per il 2026**, in applicazione del D.L. 156 del 29/10/2025 (i comuni lombardi e veneti entro 30 km dalle sedi olimpiche possono aumentare l'imposta fino a 5 € a notte; il maggior gettito va per metà allo Stato). [U: M1, M2]
- La delib. n. 144 del 12/02/2026 ha ridefinito le tariffe dal 01/04/2026: per case vacanze e locazioni brevi resta 9,50 €. [U: M3]
- Nel 2025 la tariffa per case vacanze e locazioni brevi era 6,30 € (da 4,50). [U: M5]
- Max **14 pernottamenti consecutivi**: dal 15° l'imposta non è dovuta; se la consecutività si interrompe il conteggio riparte. [U: M4]
- Esenti i **minori fino al diciottesimo anno di età** (art. 19 del Regolamento 2026). Altre esenzioni: accompagnatori di degenti nelle strutture sanitarie della provincia, giovani fino a 30 anni negli ostelli per la gioventù. [U: M4]

**Roma** (fonti R1-R5, RT)
- Contributo di soggiorno, tariffe della delib. G.Ca. n. 255 del 17/07/2023, in vigore dal **01/10/2023**, con 3 categorie per guest house/affittacamere e 2 per case e appartamenti per vacanze (CAV). [U: R1, R2]
- **CAV categoria 1: 6,00 €; categoria 2: 5,00 €.** [U: R1, R2]
- **Alloggi per uso turistico e immobili destinati alla locazione breve: 6,00 €.** La riga è nella tabella ufficiale (R1), ma il suo testo non compare negli estratti: l'importo viene da fonti terze che la citano. [T: RT]
- Max **10 pernottamenti consecutivi nell'anno solare nella stessa struttura**. [U: R3]
- Esenti i **minori fino al compimento del decimo anno**; esenti anche le persone con disabilità grave (L. 104/1992, art. 3 c. 3) e un familiare accompagnatore. [U: R4, R3]
- Nessuna variazione del contributo per il 2026 trovata su `comune.roma.it`. Le "Nuove tariffe Servizi Turistici in vigore dal 1° gennaio 2026" (R5, delib. G.Ca. 492/2025) riguardano i servizi dello sportello SUAR, non il contributo. La pagina di Æqua Roma (società di Roma Capitale) riporta ancora le tariffe precedenti al 2023 (3,50 €): non usarla.

**Como** (fonti C1-C3)
- Case e appartamenti per vacanze, locazioni turistiche e locazioni brevi: **3,00 €** per persona per notte (stessa tariffa per affittacamere, foresterie lombarde e B&B). Tariffe approvate con delib. G.C. n. 387 del 10-11/11/2023, in vigore dal **01/01/2024**. [U: C1, C2]
- Max **4 pernottamenti consecutivi**. [U: C2, C3]
- Esenti i **minori di 14 anni**, i residenti, il gestore con familiari e collaboratori. [U: C3]
- Nessun aumento 2026 (per esempio olimpico) trovato sul sito del comune.

**Firenze** (fonti F1-F3, FT)
- Locazioni turistiche e locazioni brevi: da 5,50 a **6,00 €**, equiparate agli alberghi 3 stelle. Delib. G.C. n. 535 del 10/12/2024. [U: F1, F2]
- Decorrenza: le tariffe hanno effetto "dal primo giorno del secondo mese successivo" alla pubblicazione; le fonti terze indicano il **01/02/2025**. [D: F1; T: FT]
- Max **7 pernottamenti consecutivi** (per le locazioni turistiche dal 01/10/2014). [U: F3]
- Esenti i **minori fino al compimento del dodicesimo anno**, gli accompagnatori dei degenti (max 2 per paziente), i pazienti in day hospital, gli studenti dell'Università di Firenze, le forze dell'ordine in servizio. [U: F3]
- Nessuna variazione 2026 trovata.

**Napoli** (fonti N1-N4, NT)
- Locazioni brevi, equiparate alle strutture extralberghiere: **6,00 €** dal **01/05/2026**. Delib. G.C. n. 89 del 12/03/2026: +1,00 € sui valori base vigenti a inizio anno (+0,50 € per gli alberghi 3 stelle). [U: N1, N2]
- 2025: dal 01/03/2025 (delib. n. 572 di dicembre 2024) locazioni brevi 5,00 €, B&B/CAV/affittacamere 4,50 €, con la deroga "Giubileo" valida fino al 31/12/2025. [U: N4 per delibera e data; T: NT per gli importi]
- La tariffa del periodo 01/01-30/04/2026 **non è verificata**: se i valori base di inizio 2026 erano quelli del 2025, era 5,00 € (D). Non è nel CSV.
- Max **14 pernottamenti consecutivi**. [U: N3, N4]
- Esenti i **minori "entro il quattordicesimo anno di età"** (art. 7 del Regolamento); la pagina 2025 dice "dovuta dai soggetti di età superiore ai 14 anni". Non è chiaro se il quattordicenne sia esente: nel CSV `min_age_exempt = 14` (esenti sotto i 14). [U: N3, N4]

**Torino** (fonti T1-T4, TT)
- Dal **01/04/2026** (modifica del Regolamento n. 349): alberghi 1-2 stelle da 2,30 a 3,00 €, 3 stelle da 2,80 a 3,50, 4 stelle da 3,70 a 4,50, 5 stelle invariati a 5,00; **B&B da 2,30 a 3,80 €**. [U: T1, T2]
- Prima dell'aumento la tariffa era uniforme a 2,30 € per gli alberghi 1-2 stelle e per **tutte** le strutture extralberghiere. [U: T2]
- **Locazioni brevi e turistiche, appartamenti per vacanze, affittacamere, case per ferie: 3,80 €** secondo fonti terze; coerente con l'uniformità precedente, ma non letto in un estratto ufficiale. [T: TT]
- Max **7 pernottamenti**; dal 01/04/2026 il conteggio è **su base annua** (prima trimestrale). [U: T1, T2]
- Esenti i **minori che non hanno compiuto il tredicesimo anno di età**, autisti e accompagnatori di gruppi organizzati; dal 01/04/2026 anche persone con disabilità grave con un accompagnatore e forze dell'ordine in servizio di ordine pubblico. [U: T3, T1]

**Venezia** (fonti V1-V4)
- Tariffe IDS valide dal **01/04/2025** (aggiornamento del 10/03/2025). Per le locazioni turistiche c'è una **zona territoriale unica** (dal 2019); la tariffa dipende dalla **categoria catastale** dell'alloggio e dalla **stagione**. [U: V1]
- Alta stagione (01/02-31/12): Gruppo 1 (A/1, A/8, A/9) **5,00 €**; Gruppo 2 (A/2, A/3, A/6, A/7, A/11) **4,00 €**; Gruppo 3 (A/4, A/5) **3,00 €** (Gruppo 3 alta stagione: D, vedi sotto). [U: V1]
- Bassa stagione (01/01-31/01, -30%): Gruppo 1 **3,50 €**, Gruppo 2 **2,80 €**, Gruppo 3 **2,10 €**. [U: V1]
- Tariffa ridotta al 50% per i **minori da 10 a 16 anni** (2,50 / 2,00 / 1,50 in alta stagione; 1,70 / 1,40 / 1,00 in bassa). [U: V1, V2]
- L'importo del Gruppo 3 in alta stagione non compare nell'estratto del PDF 2025: 3,00 € è dedotto dalla bassa stagione (2,10 = 3,00 - 30%) ed è coerente con fonti terze. [D]
- Max **5 pernottamenti consecutivi**. Esenti i **minori di 10 anni**. Regolamento modificato con delib. C.C. n. 77 del 19/12/2024. [U: V2, V4]

**Bologna** (fonti B1-B4)
- Locazioni brevi (art. 4 D.L. 50/2017) e appartamenti ammobiliati a uso turistico (art. 12 L.R. Emilia-Romagna 16/2004): **10,5% del prezzo del pernottamento** (al netto di IVA e servizi aggiuntivi), con **tetto di 7,00 € per persona per notte**. Tariffe per l'anno 2026, proposta DG/PRO/2025/283. [U: B1, B2]
- Decorrenza nel CSV: 01/01/2026, dedotta dal titolo "tariffe per l'anno 2026". Il 2025 aveva tariffe maggiorate (deroga Giubileo, fino a +2 €) dal 01/04 al 31/12/2025. [D: B1; U: B4]
- Fonti terze citano per gli incassi tramite portali percentuali del 6% o del 7,5% con tetto 5 €: la FAQ ufficiale (B2) indica 10,5% / 7 € anche per quel caso. Non confermato.
- Max **5 pernottamenti consecutivi**. Esenti i **minori di 14 anni** e i genitori che assistono un figlio minore di 14 anni ricoverato. [U: B2, B3]
- Nel CSV `rate_per_person_night_eur` è vuoto: una tariffa percentuale non si può ridurre a un importo fisso.

**Seveso** (fonte S1)
- Nessuna imposta di soggiorno trovata. La pagina ufficiale "Imposte e tasse" elenca TARI, IMU, Canone unico patrimoniale, TASI e TOSAP. Nel portale del federalismo fiscale (`finanze.gov.it`) non risulta un regolamento del comune (gli unici risultati riguardano Lentate sul Seveso). [U: S1 per l'elenco; D per l'assenza]
- **Non è una conferma esplicita** che il comune non applichi l'imposta: serve una verifica con l'Ufficio tributi prima di scriverlo agli host.

**Cesano Maderno** (fonte CM1)
- Nessuna tariffa trovata. La pagina "Risorse tributarie" indica come tributi gestiti solo TARI e IMU. [U: CM1]
- Un estratto di ricerca sul portale `finanze.gov.it` attribuisce al comune un regolamento sull'imposta di soggiorno (delib. C.C. n. 7 dell'08/02/2021, dal 01/01/2021), ma il documento non è stato visto e l'attribuzione non è certa. **Esistenza dell'imposta da verificare** con l'ufficio Risorse tributarie.

### Codici ISTAT

Il dataset ufficiale ISTAT è in arrivo con il task RS-6 e al 2026-09-23 non è nel branch di integrazione. I codici del CSV vengono dagli URL delle pagine ISTAT 8milaCensus (`https://ottomilacensus.istat.it/comune/<provincia>/<codice>/`), consultati come estratti indicizzati [U]: Milano 015146, Roma 058091, Como 013075, Firenze 048017, Napoli 063049, Torino 001272, Venezia 027042, Bologna 037006, Seveso 108040, Cesano Maderno 108019. Quando RS-6 sarà integrato vanno riconciliati con il dataset.

Differenze con `Casazen.Core/Regulatory/ItalianComuneRegistry.cs` (usato anche dal bootstrap SEO, `SeoBootstrapHostedService`):
- Torino è registrato come `010025`, che è Genova: il codice corretto è `001272` (difetto A5-34/A8-24, task SU-04).
- Seveso e Cesano Maderno non sono nel registro.
- Palermo, Bellagio, Menaggio e Varenna sono nel registro ma fuori dal perimetro di RS-7: nessuna tariffa raccolta.
- Nessun seed di `TouristTaxRates` esiste oggi: né migrazioni né bootstrap SEO popolano la tabella (A5-06, A8-12).

### Indicazioni per CO-03 e BK-03

1. Importare nel seed solo le righe con tariffa valorizzata e `verified = U`. Le righe T o D vanno confermate dall'admin prima di attivarle.
2. Una tariffa vuota **non** va convertita in 0: il comune resta "senza tariffa" e il wizard mostra l'avviso (A5-06, A8-12).
3. Il modello `TouristTaxRate` (una tariffa per città, `MaxNights`, `MinimumAge`) non rappresenta:
   - le categorie di struttura (Roma: locazione breve vs CAV cat. 1/2): serve un tipo di struttura sulla tariffa e sulla property;
   - la stagione e il gruppo catastale di Venezia, e la riduzione del 50% per i 10-16 anni;
   - la tariffa percentuale con tetto di Bologna;
   - il conteggio annuo delle notti di Torino dal 01/04/2026 (non consecutivo);
   - la scadenza delle tariffe temporanee (Milano 2026: `EffectiveTo` = 31/12/2026 solo se confermato dal comune).
   Questi casi vanno modellati o esclusi in modo esplicito. Non vanno approssimati con un importo fisso.
4. Il matching per città deve passare dal codice ISTAT, non dal nome (A8-23).

### Dubbi aperti

- Roma: importo per "alloggi per uso turistico / locazione breve" (6,00 €) da confermare sul PDF ufficiale R1.
- Torino: importo per le locazioni brevi (3,80 €) da confermare sull'allegato tariffe della delibera T2.
- Napoli: il quattordicenne è esente? Quale tariffa valeva dal 01/01 al 30/04/2026?
- Venezia: importo del Gruppo 3 in alta stagione (3,00 €) da confermare su V1.
- Bologna: percentuale e tetto per gli incassi tramite portali; data di decorrenza delle tariffe 2026.
- Milano: la tariffa 9,50 € vale solo per il 2026. Dal 01/01/2027 serve la nuova delibera.
- Firenze: data esatta di decorrenza (01/02/2025 dedotta).
- Seveso e Cesano Maderno: chiedere ai comuni se l'imposta è istituita. Se non lo è, conviene una conferma scritta da citare agli host.
- Tipologia della property: CasaZen deve sapere se l'alloggio è "locazione breve" o CAV (Roma) e la sua categoria catastale (Venezia). Decisione di prodotto per BK-03/CO-03.

### Fonti

Tutte consultate il **2026-09-23** tramite estratti WebSearch (lettura diretta bloccata dal proxy).

- M1 Comune di Milano, "Turismo. Approvate tariffe imposta di soggiorno in vigore dal 1° gennaio 2026": https://www.comune.milano.it/-/turismo.-approvate-tariffe-imposta-di-soggiorno-in-vigore-dal-1-gennaio-2026
- M2 Comune di Milano, delib. G.C. n. 1418 del 13/11/2025: https://www.comune.milano.it/documents/20118/1016509/DELG+1418.2025.pdf/932be158-cc96-1933-6696-80f6056f179a?version=2.1&t=1766480677991&download=true
- M3 Comune di Milano, "Imposta di soggiorno tariffe 2026 dall'1/4/2026": https://www.comune.milano.it/documents/20118/1016509/Tariffe+2026+decorrenza+1%C2%B0aprile+2026.pdf/d101078f-26b0-4446-d16b-787845b76bca?version=1.1&t=1771602332605&download=true
- M4 Comune di Milano, Regolamento dell'imposta comunale di soggiorno (2026): https://fareimpresa.comune.milano.it/documents/20126/200621333/Regolamento+Imposta+di+Soggiorno+2026.pdf/e32b40da-2c92-ff1e-d2bc-f12c4756b1fe?t=1769777675207
- M5 Comune di Milano, "Palazzo Marino. Adeguate le tariffe dell'imposta di soggiorno per il 2025": https://www.comune.milano.it/w/palazzo-marino.-adeguate-le-tariffe-dell-imposta-di-soggiorno-per-il-2025
- R1 Roma Capitale, tabella tariffe dal 01/10/2023: https://www.comune.roma.it/web-resources/cms/documents/Nuove_tariffe_contributo_di_soggiorno_dal_01.10.2023_logo.pdf
- R2 Roma Capitale, "Avviso - Nuove tariffe del Contributo di Soggiorno": https://www.comune.roma.it/web/it/scheda-servizi.page?contentId=INF1088067&stem=suar
- R3 Roma Capitale, scheda "Contributo di soggiorno": https://www.comune.roma.it/web/it/scheda-servizi.page?contentId=INF41430
- R4 Roma Capitale, Regolamento sul contributo di soggiorno (Assemblea Capitolina 34/2024): https://www.comune.roma.it/web-resources/cms/documents/Assemblea_Capitolina_34_2024.pdf
- R5 Roma Capitale, "Nuove tariffe Servizi Turistici in vigore dal 1° gennaio 2026" (SUAR): https://www.comune.roma.it/web/it/scheda-servizi.page?contentId=INF1516506&stem=suar
- RT (terze) Lodgify, https://www.lodgify.com/blog/it/tassa-soggiorno-roma/ ; Chekin, https://chekin.com/it/blog/tassa-di-soggiorno-roma/
- C1 Comune di Como, tariffe dell'imposta di soggiorno: https://www.comune.como.it/export/sites/comune-di-como/.galleries/Settore-14/TARIFFE-IMPOSTA-DI-SOGGIORNO.pdf
- C2 Comune di Como, "Imposta di soggiorno": https://www.comune.como.it/it/servizi/tasse-e-imposte/imposta-di-soggiorno/
- C3 Comune di Como, Regolamento (delib. C.C. n. 84 del 26/11/2019): https://www.comune.como.it/export/sites/comune-di-como/.galleries/Regolamenti/Regolamento-IMP-SOGG-approvato-con-delibera-CC-n.84-del-26.11.2019.pdf
- F1 Comune di Firenze, delib. G.C. n. 535 del 10/12/2024: https://servizi.comune.fi.it/sites/www.comune.fi.it/files/deliberazione_di_giunta_completa-dg_2024_00599_00535-1.pdf
- F2 Comune di Firenze, comunicato "Bilancio di previsione, Funaro: Crescono gli investimenti sulla casa…": https://www.comune.fi.it/comunicati-stampa/bilancio-di-previsione-funaro-crescono-gli-investimenti-sulla-casa-raddoppiano
- F3 Comune di Firenze, "Imposta di Soggiorno - Informati": https://servizi.comune.fi.it/servizi/imposta-di-soggiorno-informati
- FT (terze) Lodgify, https://www.lodgify.com/blog/it/tassa-soggiorno-firenze/ ; TTG Italia, https://www.ttgitalia.com/incoming/tassa-di-soggiorno-firenze-2026-tariffe-esenzioni-e-come-si-paga-FN25937727
- N1 Comune di Napoli, "Napoli, aggiornate le tariffe dell'imposta di soggiorno per il 2026": https://www.comune.napoli.it/novita/napoli-aggiornate-le-tariffe-dellimposta-di-soggiorno-per-il-2026/
- N2 Comune di Napoli, "Imposta di Soggiorno 2026": https://www.comune.napoli.it/articolo_tematico/tributi-locali/imposta-di-soggiorno/imposta-di-soggiorno-2026/
- N3 Comune di Napoli, testo coordinato del Regolamento: https://static-www.comune.napoli.it/wp-content/uploads/2025/09/Testo_Coordinato_del_Regolamento_sull_Imposta_di_Soggiorno_2019.pdf
- N4 Comune di Napoli, "Imposta di Soggiorno 2025": https://www.comune.napoli.it/articolo_tematico/tributi-locali/imposta-di-soggiorno/imposta-di-soggiorno-2025/
- NT (terze) Fanpage, https://www.fanpage.it/napoli/tassa-di-soggiorno-a-napoli-nel-2025-le-nuove-tariffe-con-gli-aumenti-per-alberghi-bb-e-locazioni-brevi/
- T1 Città di Torino, comunicato "Imposta di soggiorno - Imu 2026": https://www.comune.torino.it/novita/comunicati/imposta-soggiorno-imu-2026
- T2 Città di Torino, proposta di deliberazione T-P202534683 (modifica Regolamento n. 349): https://servizi.comune.torino.it/consiglio/prg/intranet/display_testi.php?doc=T-P202534683
- T3 Città di Torino, "Esenzioni imposta di soggiorno": https://www.comune.torino.it/media/10201
- T4 Città di Torino, "Imposta di soggiorno per gestori": https://www.comune.torino.it/servizi/imposta-soggiorno-per-gestori
- TT (terze) TorinoToday, https://www.torinotoday.it/politica/aumento-tassa-soggiorno-bnb-hotel.html ; BusinessMobility, https://www.businessmobility.travel/tassa-di-soggiorno-a-torino-2026-tutto-quello-che-devi-sapere/26229/
- V1 Comune di Venezia, tariffe IDS valide dal 01/04/2025: https://www.comune.venezia.it/sites/comune.venezia.it/files/documenti/Tributi/ids/TARIFFE%20IDS%20-%20STRUTTURE%20con%20classificazione%20L.R.%2011_2013_valide%20dal%2001.04.2025.pdf
- V2 Comune di Venezia, "Esenzioni": https://www.comune.venezia.it/it/content/esenzioni-vocabolario-ids-dal-19052020
- V3 Comune di Venezia, "Tariffe IDS": https://www.comune.venezia.it/it/content/tariffe-ids-dal-19052020
- V4 Comune di Venezia, "Regolamento dell'Imposta di Soggiorno": https://www.comune.venezia.it/it/content/regolamento-imposta-soggiorno
- B1 Comune di Bologna, "Approvazione delle tariffe per l'anno 2026" (DG/PRO/2025/283): https://www.comune.bologna.it/myportal/C_A944/api/content/download?id=660532c5ca8004009ad9519a
- B2 Comune di Bologna, "Come si calcola l'imposta di soggiorno?": https://www.comune.bologna.it/assistenza/come-calcolare-imposta-soggiorno
- B3 Comune di Bologna, "Ci sono esenzioni sull'imposta di soggiorno?": https://www.comune.bologna.it/assistenza/esenzioni-imposta-soggiorno
- B4 Comune di Bologna, "Imposta di soggiorno, approvato l'adeguamento tariffario per il 2025": https://www.comune.bologna.it/novita/comunicati-stampa/imposta-di-soggiorno-approvato-ladeguamento-tariffario-il-2025
- S1 Città di Seveso, "Imposte e tasse": https://www.comune.seveso.mb.it/it/page/imposte-e-tasse-1
- CM1 Città di Cesano Maderno, "Risorse tributarie": https://www.comune.cesano-maderno.mb.it/amministrazione/unita-organizzative/risorse-tributarie/
- I1 ISTAT 8milaCensus (codici comune): https://ottomilacensus.istat.it/comune/015/015146/ , https://ottomilacensus.istat.it/comune/058/058091/ , https://ottomilacensus.istat.it/sottotema/013/013075/2/ , https://ottomilacensus.istat.it/comune/048/048017/ , https://ottomilacensus.istat.it/comune/063/063049/ , https://ottomilacensus.istat.it/comune/001/001272/ , https://ottomilacensus.istat.it/comune/027/027042/ , https://ottomilacensus.istat.it/comune/037/037006/ , https://ottomilacensus.istat.it/sottotema/108/108040/1/ , https://ottomilacensus.istat.it/comune/108/108019/
