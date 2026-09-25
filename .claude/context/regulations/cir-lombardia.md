# CIR Lombardia (Codice Identificativo di Riferimento)

> Ricerca del task **RS-9** (risanamento), eseguita il **2026-09-24**, per l'issue #8 (portali regionali/CIR). La regione pilota è la Lombardia.
> - Il formato del CIN nazionale è in `cin.md`.
> - I flussi turistici e Ross1000 sono in `istat-flussi-turistici.md`.
>
> **Non è un parere legale.**
> Legenda dei livelli: **U** fonte ufficiale di un ente pubblico, letta come estratto indicizzato; **D** dedotto; **T** terza parte. L'accesso diretto ai siti ufficiali era bloccato dal proxy (elenco in `istat-flussi-turistici.md` § 8).

## Esito in breve

| Domanda | Risposta | Livello |
|---|---|---|
| Che cos'è | Codice regionale univoco per **ogni unità ricettiva**, comprese le locazioni turistiche. Coincide con il "Codice regione" che Ross1000 genera quando registra la struttura | U |
| Chi deve averlo | Tutte le strutture della L.R. 27/2015, compresi gli alloggi o le porzioni di alloggi dati in locazione per finalità turistiche (L. 431/1998) e le locazioni brevi del D.L. 50/2017 | U |
| Come si ottiene | CIA (per le LT) o SCIA al SUAP del Comune → il Comune la trasmette alla Provincia o alla Città metropolitana → l'ente registra la struttura in Ross1000 → il titolare riceve una mail e trova il CIR nell'anagrafica Ross1000. **Il SUAP non rilascia il CIR** | U |
| Formato | 6 cifre (codice ISTAT del comune) + 3 lettere (tipologia) + 5 caratteri sequenziali generati automaticamente. Negli annunci compare con i trattini, per esempio `015146-CNI-01894` | U (composizione), T (esempi con trattini) |
| Rapporto con il CIN | Il CIR è **propedeutico**: serve per chiedere il CIN in BDSR. **Il CIN non si ricava dal CIR** | U (propedeuticità), D (non derivabilità, vedi `cin.md`) |
| Esposizione oggi | Dal **02/11/2024** l'indicazione del **CIN** in pubblicità, promozione e commercializzazione **sostituisce** quella del CIR. Dopo aver ottenuto il CIN non serve più indicare il CIR negli annunci | U |
| Da ricordare | Il CIR resta necessario come **codice struttura in Ross1000** per la comunicazione mensile dei flussi | U, D |

## 1. Norme

| Atto | Contenuto | Livello | Fonte |
|---|---|---|---|
| L.R. 25/01/2018 n. 7 | Introduce il CIR modificando la L.R. 27/2015 | U | [C2], [N2] |
| L.R. 1/10/2015 n. 27, art. 38, c. 8-bis e 8-ter | A strutture ricettive e locazioni turistiche viene assegnato un CIR per ogni unità ricettiva, "generato dal sistema di gestione dei flussi turistici" usato per comunicare i flussi. Il CIR va indicato in pubblicità, promozione e commercializzazione. Le disposizioni si applicano anche alle locazioni brevi del D.L. 50/2017 | U (estratto; testo vigente non letto) | [N1] |
| D.G.R. n. XI/280 del 28/06/2018 | Prima disciplina del CIR per CAV e LT, in attuazione dell'art. 38 c. 8-bis | U | [C2], [P3] |
| D.G.R. n. XII/169 | Disciplina vigente del CIR. Estende l'obbligo a tutte le strutture ricettive lombarde. L'estratto indica la data 19/04/2023, che è anche la data del BURL Serie Ordinaria n. 16: **la data della delibera va confermata** | U (estratto), D (data) | [C3], [P1] |
| Corte costituzionale, sent. n. 84/2019 | Ha respinto la questione sul CIR lombardo | U (esistenza ed esito, da comunicato regionale e banca dati del Consiglio); motivazione non letta | [N3], [R3] |
| L.R. 6/12/2024 n. 20, art. 15 | Modifica la L.R. 27/2015. Riconferma il CIR generato dal sistema dei flussi. Le strutture e le locazioni turistiche devono adempiere alle disposizioni regionali **prima** di accedere alla BDSR per il CIN. Prevede un regolamento regionale, da adottare entro 120 giorni, sui requisiti minimi delle LT, e una sanzione per chi esercita la LT non imprenditoriale senza CIA | U (estratto). **L'attribuzione ai singoli commi e l'importo della sanzione non sono verificati** | [N4] |

## 2. Come si ottiene (percorso)

1. Il titolare presenta al **SUAP** del Comune:
   - la **CIA** (comunicazione di inizio attività) per le CAV e per gli alloggi dati in locazione per finalità turistiche (U, [R1]);
   - la **SCIA** per le altre strutture ricettive (U, [C3]).
   Il confine tra CIA e SCIA per le forme imprenditoriali non è stato verificato. A Milano si presenta solo in via telematica su `impresainungiorno.gov.it`, con effetto dalla presentazione (U, [M1]).
2. Il SUAP trasmette la comunicazione alla **Provincia** o alla **Città metropolitana** competente (U, [C3], [M1]).
3. L'ente crea la sezione della struttura in **Ross1000** e manda al titolare una mail automatica di abilitazione. L'accesso avviene con SPID, CIE o CNS (U, [C3], [P2]).
4. Nell'anagrafica Ross1000 il titolare trova il **"Codice regione"**, che corrisponde al CIR (U, [C3], [R1]).
5. Con il CIR il titolare chiede il **CIN** in BDSR (U, [R2], [P1]). La BDSR è interoperabile con Ross1000: in Lombardia il CIN si può chiedere dal **3 luglio 2024** (U, titolo della news MiTur [MT1]).

## 3. Formato

**Composizione (U, [R1], [P2], [M1]):** "6 caratteri numerici riferiti al codice Istat del Comune, 3 caratteri alfabetici che individuano la tipologia di struttura e 5 caratteri sequenziali generati automaticamente".

| Parte | Lunghezza | Contenuto | Livello |
|---|---|---|---|
| Comune | 6 | codice ISTAT del comune, per esempio `015146` Milano | U |
| Tipologia | 3 | lettere che identificano la tipologia | U (composizione) |
| Sequenziale | 5 | generato da Ross1000 | U ("caratteri"); D (negli esempi sono solo cifre) |

Cosa **non** è verificato:
- **Separatori.** Le fonti ufficiali lette non dicono se il trattino fa parte del codice. Negli annunci reali il CIR compare come `015146-CNI-01894`, cioè 16 caratteri con due trattini (T, annunci Airbnb e Booking).
  - Un documento del concessionario tributi di Cremona parla di "14 caratteri" con "tratto di separazione" (T, [T1]), che è incoerente con i trattini.
  - Conclusione (D): 14 caratteri senza separatori, 16 con i trattini.
- **Significato delle sigle di tipologia.** Negli annunci compaiono `CNI`, `CIM`, `LNI`, `LIM` e `REC` (T). Nessuna fonte ufficiale letta ne dà la tabella.
  - L'ipotesi C = CAV, L = locazione turistica, NI = non imprenditoriale, IM = imprenditoriale è **solo una deduzione (D)**.
  - **Non va usata** per classificare le property.
- **Checksum**: nessuna fonte ne documenta uno.

**Per un eventuale controllo in CO-22 o SU-04 (D, solo come avviso, mai come blocco):**
1. Normalizzare: trim, maiuscolo, rimozione degli spazi.
2. Accettare `^\d{6}-?[A-Z]{3}-?[A-Z0-9]{5}$`.
3. Salvare la forma con i trattini, come la mostra Ross1000.
4. Confrontare le prime 6 cifre con il codice ISTAT della property solo per un avviso. Lo stesso ragionamento vale per il CIN: i codici comunali cambiano con le fusioni.

Il rischio di un falso "non valido" è alto, perché la regola deriva da un estratto e da esempi. Prima di attivare un controllo va letta la D.G.R. XII/169.

## 4. Rapporto con il CIN

- **Propedeuticità** (U, [R2], [P1], [P4]):
  - il CIN si può chiedere **solo** se si ha già il CIR;
  - strutture con CIR assegnato **prima** del 02/11/2024: 60 giorni da quella data, quindi CIN posseduto ed esposto entro il **01/01/2025**;
  - strutture con CIR assegnato **dopo** il 02/11/2024: chiedere il CIN e iniziare a esporlo **entro 30 giorni** dal CIR.
- **Il CIN non si ricava dal CIR** (D):
  - il CIR lombardo ha 3 lettere di tipologia e un progressivo, mentre il CIN ha la categoria ISTAT di 2 caratteri e una parte casuale;
  - la regola "CIN = `IT` + CIR" del decreto interoperabilità vale solo per i codici regionali che hanno già la struttura nazionale (vedi `cin.md`);
  - esempio: un hotel di Milano con CIN `IT015146A12HOLV2MZ` (vedi `cin.md`).
- **Campi separati.** CasaZen deve tenere CIR e CIN in campi separati e non accettare un CIR nel campo CIN (lo fa già `CinFormat`).

## 5. Esposizione

| Periodo | Regola | Livello | Fonte |
|---|---|---|---|
| Dal 2018 al 02/11/2024 | Il CIR va indicato in ogni annuncio, stampato o digitale, per CAV e LT. La data di inizio è **incoerente tra le fonti**: "1 settembre 2018" per la Regione, "1 novembre 2018" per la Città metropolitana e il Comune di Milano | U (entrambe) | [R1], [C1], [M1] |
| Dal 02/11/2024 | "L'indicazione del CIN nelle attività di promozione e commercializzazione della struttura turistica **sostituisce** l'obbligo di indicazione del CIR". Dopo aver ottenuto il CIN "non sarà più necessario indicare il CIR nel materiale promozionale" | U | [R2], [P1], [P4] |

Diverse fonti terze consigliano di indicare **entrambi** i codici, per esempio Asppi Milano (T). Contrastano con le fonti ufficiali regionali e provinciali e **non vanno seguite** per il design.

Obbligo di esporre il CIR fuori dall'immobile: **non trovato** nelle fonti lette. L'esposizione esterna riguarda il CIN (art. 13-ter, vedi `cin.md`).

## 6. Sanzioni (L.R. 27/2015)

| Violazione | Importo | Livello | Fonte |
|---|---|---|---|
| Titolare: CIR omesso, errato o ingannevole negli annunci | €500–2.500 per ciascuna attività pubblicizzata | U | [M1], [C3] |
| Intermediari immobiliari e gestori di portali: CIR non indicato | €250–1.500 per ciascuna unità pubblicizzata | U (estratto) | [N1], [C3] |
| Comunicazione dei flussi omessa o incompleta | €250–2.500 al mese (art. 40 c. 9) | U | vedi `istat-flussi-turistici.md` |

- **Numero di articolo e comma** delle sanzioni sul CIR: gli estratti indicano sia l'art. 39 c. 3-bis sia l'art. 40. **Da verificare sul testo vigente.**
- Se dopo il 02/11/2024 la sanzione sul CIR negli annunci sia ancora applicabile a chi espone il CIN: vedi DUBBI.

## 7. Impatto su CasaZen

- **Campo dati.** Serve un campo `RegionalCode` o `CirCode` sulla property, **separato** da `CinCode`, con il tipo di codice regionale. In Lombardia è il CIR; altre regioni hanno formati diversi (vedi `cin.md`). La proposta rientra nel perimetro di **SU-04** e **CO-22**. Serve per:
  - il codice struttura in Ross1000 (flussi mensili, `istat-flussi-turistici.md`);
  - la cronologia e i controlli;
  - il wizard regionale (#8, #295): "hai il CIR? Serve per chiedere il CIN".
- **Annunci e pagine pubbliche.** Per la Lombardia basta il **CIN**. Non serve mostrare il CIR agli ospiti; mostrarlo non è vietato, ma nessuna fonte lo richiede (D).
- **Wizard regionale Lombardia (#8)**, passi verificati (U):
  1. CIA o SCIA al SUAP;
  2. abilitazione Ross1000 e CIR;
  3. CIN in BDSR entro 30 giorni dal CIR;
  4. flussi mensili su Ross1000 entro il giorno 5;
  5. Alloggiati Web entro 24 ore (vedi `alloggiati.md`);
  6. imposta di soggiorno comunale (vedi `imposta_soggiorno.md`).
  Link ai portali: SUAP `impresainungiorno.gov.it`, Ross1000 `flussituristici.servizirl.it`, BDSR del Ministero del Turismo.
- **Correzioni a `regionale.md`.** Le voci "Registro comunale" e "Requisiti sicurezza: estintori, rilevatori fumo obbligatori" nella sezione Lombardia non sono verificate. Il "registro" è in realtà la CIA al SUAP più la registrazione in Ross1000. Per la sicurezza fa fede `sicurezza.md`.

## 8. DUBBI

1. **CIR negli annunci dopo il CIN.** La sostituzione del CIR con il CIN nasce da un'informativa regionale e da comunicazioni provinciali (U). Non è verificato se il testo della L.R. 27/2015 (art. 38 c. 8-ter e sanzione collegata) sia stato formalmente modificato. Un legale deve confermare che esporre solo il CIN non espone a sanzioni regionali.
2. **Sigle di tipologia e separatori.** Servono la D.G.R. XII/169 e il suo allegato per attivare una validazione del CIR. Fino ad allora il CIR resta un campo libero, al massimo con un avviso.
3. **Periodo transitorio.** Per le property lombarde senza CIN ma con CIR il prodotto deve ancora mostrare il CIR? Dal 01/01/2025 il CIN è obbligatorio per tutte, quindi il caso dovrebbe essere residuale (D).
4. **Regolamento regionale sui requisiti minimi delle LT** (L.R. 20/2024): non è stato verificato se sia stato adottato né che cosa preveda. Va aggiunto a #8 quando sarà verificato.
5. **Altre regioni.** Solo la Lombardia è stata verificata in RS-9. Gli esempi di altre regioni in `cin.md` non bastano per un wizard multi-regione.

## Fonti

Consultate il **2026-09-24** come estratti indicizzati; l'accesso diretto era bloccato dal proxy.

| ID | Fonte | Tipo | Livello |
|---|---|---|---|
| R1 | [Regione Lombardia: Codice Identificativo di Riferimento (CIR)](https://www.regione.lombardia.it/cultura-turismo-e-sport/imprese-e-professioni-turistiche/codice-identificativo-di-riferimento-cir) | Regione | U |
| R2 | [Regione Lombardia: Entrata in vigore del CIN](https://www.regione.lombardia.it/wps/portal/istituzionale/HP/DettaglioRedazionale/servizi-e-informazioni/Imprese/Imprese-turistiche/01-entrata-in-vigore-cin/01-entrata-in-vigore-cin); [Informativa CIN su Ross1000](https://www.flussituristici.servizirl.it/Turismo5/res/Informativa_CIN.pdf) | Regione | U |
| R3 | [Regione Lombardia, news 2019: "CIR, sentenza storica: Regione Lombardia vince in Corte Costituzionale"](https://www.regione.lombardia.it/wps/portal/istituzionale/HP/lombardia-notizie/DettaglioNews/2019/04-aprile/08-14/turismo-cir-sentenza-storica-regione-lombardia-vince-in-corte-costituzionale) | Regione | U |
| N1 | [L.R. 27/2015, art. 38](https://normelombardia.consiglio.regione.lombardia.it/NormeLombardia/Accessibile/main.aspx?view=showpart&idparte=lr002015100100027ar0038a); [testo della legge](https://normelombardia.consiglio.regione.lombardia.it/normelombardia/Accessibile/main.aspx?view=showdoc&iddoc=lr002015100100027) | Consiglio regionale | U |
| N2 | [L.R. 7/2018](https://normelombardia.consiglio.regione.lombardia.it/NormeLombardia/Accessibile/main.aspx?view=showdoc&iddoc=lr002018012500007) | Consiglio regionale | U |
| N3 | [Consiglio regionale: sentenza Corte cost. 84/2019](https://www.consiglio.regione.lombardia.it/wps/portal/crl/home/leggi-e-banche-dati/giurisprudenza-costituzionale/Dettaglio-sentenza/Sentenze/2019/sen-sentenza-84-2019) | Consiglio regionale | U |
| N4 | [L.R. 20/2024, art. 15](https://normelombardia.consiglio.regione.lombardia.it/NormeLombardia/Accessibile/main.aspx?view=showpart&idparte=lr002024120600020ar0015a) | Consiglio regionale | U |
| C1 | [Città metropolitana di Milano: CAV, CIR obbligatorio dal 1° novembre 2018](https://www.cittametropolitana.mi.it/turismo/news/Case-e-appartamenti-vacanze-obbligatorio-dal-1-novembre-2018-il-CIR-Codice-Identificativo-di-Riferimento./) | Città metropolitana | U |
| C2 | [Città metropolitana di Milano: CIR (pagina storica)](https://www.cittametropolitana.mi.it/turismo/CIR) | Città metropolitana | U |
| C3 | [Città metropolitana di Milano: CIR](https://www.cittametropolitana.mi.it/aree-tematiche/turismo/strutture-turistiche/cir/) | Città metropolitana | U |
| M1 | [Comune di Milano, Fare Impresa: Locazioni turistiche (LT)](https://fareimpresa.comune.milano.it/en/strutture-ricettive/locazioni-alloggi-finalita-turistiche-affitti-brevi); [CIN, informazioni](https://fareimpresa.comune.milano.it/it/-/-cin-codice-identificativo-nazionale-informazioni) | Comune | U |
| P1 | [Provincia di Bergamo: CIR e CIN](https://www.provincia.bergamo.it/cnvpbgrm/po/mostra_news.php?id=1620); [Entrata in vigore del CIN](https://www.provincia.bergamo.it/cnvpbgrm/po/mostra_news.php?id=1857&area=H) | Provincia | U |
| P2 | [Provincia di Como: registrazione Ross1000, CIR e credenziali](https://www.provincia.como.it/strutture-ricettive-registrazione-applicativo-ross-1000-codice-regionale-cir-e-credenziali-per-la-comunicazione-dei-flussi-turistici); [Provincia di Lecco: rilascio credenziali Ross1000 e CIR](https://www.provincia.lecco.it/servizio/strutture-ricettive-rilascio-credenziali-turismo-5-e-codice-identificativo/) | Provincia | U |
| P3 | [Provincia di Cremona: CIR](https://www.provincia.cremona.it/turismo/?view=Pagina&id=6620) | Provincia | U |
| P4 | [Provincia di Como: Entrata in vigore del CIN](https://www.provincia.como.it/-/entrata-in-vigore-del-cin-codice-identificativo-nazionale) | Provincia | U |
| MT1 | [Ministero del Turismo: "BDSR, dal 3 luglio anche nelle regioni Lombardia e Marche si può richiedere il CIN"](https://www.ministeroturismo.gov.it/banca-dati-strutture-ricettive-dal-3-luglio-anche-nelle-regioni-lombardia-e-marche-si-puo-richiedere-il-cin/) | Ministero | U (titolo) |
| T1 | [ICA Tributi Cremona: Codici identificativi CIR e nuovo CIN](https://cremona.icatributi.it/is/Documenti/CREMONA/Codici%20identificativi%20C.I.R%20e%20nuovo%20C.I.N..pdf?i=2) | concessionario tributi | T |
| T2 | Annunci pubblici con CIR, per esempio [Airbnb 34966502](https://www.airbnb.com/rooms/34966502) (`015146-CNI-01894`), [Airbnb 27603385](https://www.airbnb.it/rooms/27603385) (`015146-LNI-02990`), [Booking](https://www.booking.com/hotel/it/central-guesthouse-cir-015146-cni-01484.html) (`015146-CNI-01484`) | annunci | T |
| T3 | [Asppi Milano: CIN e CIR](https://www.asppimilano.it/cin-e-cir/) | associazione | T (contrasta con le fonti U su un punto) |
