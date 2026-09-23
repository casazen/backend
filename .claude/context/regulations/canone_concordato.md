# Locazione a Canone Concordato (L. 431/1998 art. 2 c.3) — Contesto Long-Term Rent

## Riferimento Normativo

- **Fonte primaria**: L. 9/12/1998 n. 431, art. 2 comma 3
- **Norma attuativa**: D.M. 16 gennaio 2017 (MIT/MEF) — in vigore dal 30/03/2017, sostituisce il D.M. 5/3/1999
- **Cedolare secca ridotta**: D.Lgs. 23/2011 art. 3 → resa strutturale al 10% da L. 160/2019 → confermata invariata da L. 199/2025 (bilancio 2026)
- **IMU ridotta**: L. 208/2015 art. 1 c.53 → ricodificata L. 160/2019 art. 1 c.760

**Non va confuso con** il regime fiscale affitti brevi (`fiscale.md`, D.L. 50/2017): il canone concordato riguarda **locazioni di lunga durata ad uso abitativo** (3+2, transitorio, studenti), non le locazioni brevi turistiche.

## Sintesi dell'Obbligo/Opportunità

Il canone concordato è un regime **alternativo** al canone libero (4+4), disponibile in **tutti** i Comuni italiani dal 2017, ma con canone/durata vincolati agli **accordi territoriali** locali tra associazioni di proprietà e inquilini.

### Agevolazioni fiscali — due gruppi con requisiti diversi

| Agevolazione | Requisito territoriale |
|---|---|
| **IMU −25%** | **Nessuno — nazionale**, si applica ovunque il contratto sia genuinamente concordato |
| Cedolare secca **10%** (anziché 21%) | Solo se il Comune è "**ad alta tensione abitativa**": comuni del D.L. 551/1988 art. 1 c. 1 lett. a-b (Bari, Bologna, Catania, Firenze, Genova, Milano, Napoli, Palermo, Roma, Torino, Venezia, comuni confinanti, altri capoluoghi di provincia) più quelli individuati dal CIPE (delibera 13/11/2003, mai formalmente aggiornata). Anche nei comuni con stato di emergenza per calamità deliberato nei 5 anni precedenti il 28/5/2014 (D.L. 47/2014 art. 9 c. 2-bis; nessun elenco ufficiale). Vedi `fiscale.md` L11-L12 |
| Riduzione IRPEF **−30%** (regime ordinario) | Idem — solo comuni ATA |
| Riduzione imposta di registro **−30%** (base al 70%, aliquota 2%, **minimo 67 € sulla prima annualità**) | Idem — solo comuni ATA (art. 8 L. 431/1998). Vedi `fiscale.md` L5-L8 |

**Attenzione**: la lista "comuni ATA" (nazionale, cedolare/IRPEF/registro) è **distinta** dalla lista "comuni ad alta densità abitativa" usata da un singolo accordo territoriale per definire la propria copertura — si sovrappongono spesso ma non sono la stessa cosa. Non trattarle come intercambiabili in un calcolatore.

### Attestazione di conformità — condizione per tutte le agevolazioni

Obbligatoria per contratti **non assistiti** stipulati dopo il 30/03/2017 (Risoluzione AdE 31/E/2018). Rilasciata da **almeno una** organizzazione firmataria dell'accordo territoriale (proprietà o inquilini). Senza di essa: decadenza retroattiva di tutte le agevolazioni + sanzioni 90–180%. **CasaZen non può rilasciarla** — solo indirizzare verso l'associazione competente.

### Adempimenti separati dopo la stipula (spesso non colti)

1. **Registrazione RLI** (Agenzia delle Entrate, entro 30 giorni dalla **stipula o dalla decorrenza, se anteriore**: `fiscale.md` L1) → vedi `spec-ltr-rli-registration.md` per il flusso assistito già speccato.
2. **Comunicazione IMU al Comune** — **separata**, quasi mai automatica: ogni Comune ha una propria procedura (modulo, email, PEC) per applicare lo sconto IMU. La sola registrazione RLI/cedolare secca **non** attiva lo sconto IMU.
3. **Comunicazione alla Questura per conduttori extra-UE** (art. 7 D.Lgs. 286/1998), entro 48 ore dalla consegna dell'immobile. **Non è sostituita dalla registrazione RLI**: la registrazione assorbe solo la generica comunicazione di cessione di fabbricato (art. 12 D.L. 59/1978). Vedi `fiscale.md` L13-L15.

### Regole verificate (2026-09)

Verificate da RS-5 il 2026-09-23 per LT-04 e LT-08. Il dettaglio, con URL e classificazione U/D/T, è in `fiscale.md` → "Regole verificate (2026-09)" → LT-04 / LT-08. In sintesi:

- **Termine RLI**: `min(stipula, decorrenza) + 30 giorni` (U, AdE).
- **Registro (ordinario)**: 2% del canone annuo, **minimo 67 € sulla prima annualità** (U, AdE). Nel concordato in comune ATA la base è il **70%** del canone (U, AdE; art. 8 L. 431/1998). Annualità successive entro 30 giorni dalla scadenza della precedente (U).
- **Bollo**: 16 € ogni 4 facciate scritte, e comunque ogni 100 righe, per ciascuna copia (U, AdE). Registro e bollo non dovuti con la cedolare secca (U).
- **Cedolare 10%**: solo concordato **e** comune ATA (o stato di emergenza, D.L. 47/2014), altrimenti 21% (U, AdE).
- **Questura extra-UE**: 48 ore, obbligo autonomo rispetto alla RLI; sanzione 160-1.100 € (U, Polizia di Stato).
- **Da confermare con il commercialista**: cedolare 10% per contratti transitori e per studenti in comune ATA; sanzioni per tardiva registrazione con opzione cedolare.
- **Dal 1/1/2027** si applicano il TU registro (D.Lgs. 123/2025) e il nuovo TUIR (D.Lgs. 117/2026, cedolare artt. 203-206): i riferimenti normativi mostrati in UI e PDF vanno tenuti in configurazione.

## Accordi verificati (2026-09)

Verifica **RS-8** del 2026-09-23 sul testo ufficiale dell'accordo territoriale che copre **Seveso** e **Cesano Maderno**: l'Accordo locale "Quadro" per i Comuni della Provincia di Monza e della Brianza. Questa sezione non modifica il seed. Le correzioni al calcolo e al seed spettano a **LT-10**; IMU e dati normativi su DB spettano a **LT-13**.

**Classi.** **U** = letto sul documento ufficiale indicato. **D** = dedotto (interpretazione o calcolo), da far confermare a un'organizzazione firmataria o a un legale. **T** = fonte terza, oppure estratto di un motore di ricerca di una pagina che non è stato possibile aprire.

**Limite della verifica.** Il proxy blocca i siti dei Comuni (Seveso, Cesano Maderno, Monza, Seregno), del MEF (finanze.gov.it), di Normattiva, della Gazzetta Ufficiale, del MIT e delle associazioni firmatarie. Erano raggiungibili solo i file che il **Comune di Seveso** pubblica nel proprio archivio documenti (bucket Municipium `s3/6875`, lo stesso dei moduli e dei prospetti tributi del Comune).

### Fonti

| ID | Documento | URL | Consultato | Classe |
|---|---|---|---|---|
| F1 | Accordo locale "Quadro" MB: testo e allegati 1/A-3/C (tabelle dei canoni), 73 pagine, pubblicato dal Comune di Seveso. È il file Word "Ultima bozza Accordo" stampato in PDF il 18/03/2024, con firme trascritte ("f.to"); ultima modifica sul server 31/07/2025; sha256 `02cc1efb7b9a2a91…11bc4600`. **Non** contiene gli allegati 4-10 (planimetrie, contratti tipo, modello di attestazione, dichiarazione SICET) | https://municipium-images-production.s3-eu-west-1.amazonaws.com/s3/6875/allegati/accordo-canone-concordato-mb.pdf | 2026-09-23 | U |
| F2 | Prospetto aliquote IMU 2025 del Comune di Seveso (modello MEF, ID prospetto 1328, generato il 12/11/2024) | https://municipium-images-production.s3-eu-west-1.amazonaws.com/s3/6875/allegati/argomenti/tributi/prospetto-aliquote-imu-anno-2025.pdf | 2026-09-23 | U |
| F3 | Modulo del Comune di Seveso "Autocertificazione e trasmissione copia contratto di locazione a canone agevolato" (PDF del 30/07/2025) | https://municipium-images-production.s3-eu-west-1.amazonaws.com/s3/6875/allegati/modello-richiesta-imu-agevolata.pdf | 2026-09-23 | U |
| F4 | Confabitare Monza Brianza, "Nuovo Accordo provincia di Monza e Brianza - 05/24". Solo estratto: firmato il 15/03/2024, in vigore dal 01/05/2024 per 18 mesi, "scade formalmente il 1 novembre 2025", utilizzabile fino a un nuovo accordo | https://www.confabitaremonzabrianza.com/nuovo-accordo-provincia-di-monza-e-brianza-05-24/ | 2026-09-23 | T |
| F5 | Comune di Seregno, notizia sul nuovo accordo. Solo estratto: recepito con delibera n. 45 del 16/04/2024 | https://www.comune.seregno.mb.it/it/news/144824/locazioni-a-canone-concordato-nuovo-accordo-locale-quadro | 2026-09-23 | T |
| F6 | MEF, prospetto aliquote IMU 2025 di Cesano Maderno. Solo estratto: delibera CC n. 132 del 19/12/2024; 0,6% abitazione principale A/1-A/8-A/9; 1,04% gruppo D (escluso D/10) | https://www1.finanze.gov.it/finanze2/dipartimentopolitichefiscali/fiscalitalocale/nuova_imu/download_lib.php?key=0900f230807a6379&nome=90_DIMUNIC-09mb25c566d.pdf | 2026-09-23 | T |
| F7 | D.M. 16/01/2017. Solo estratti: transitori fino a 18 mesi (art. 2), studenti da 6 mesi a 3 anni (art. 3) | https://www.gazzettaufficiale.it/eli/id/2017/03/15/17A01858/sg | 2026-09-23 | T |
| F8 | D.P.R. 138/1998, all. C: la superficie catastale è arrotondata al metro quadrato. Solo estratti di siti tecnici | https://www.topoprogram.it/normativa-catasto/fabbricati/html/d.p.r_.23_03_98_n._138_all._c__norme_tecniche_per_la_determinazione_della_superficie_catastale_delle_unit%C3%A0_immobiliari_a_destinazione_ordinaria_(gruppi_r,_p,_t).html | 2026-09-23 | T |

### Dati generali dell'accordo MB

| Dato | Valore | Classe | Fonte |
|---|---|---|---|
| Base | Art. 2 c. 3 L. 431/1998 e D.M. 16/01/2017 | U | F1 p. 1 |
| Sottoscrizione | Monza, **15/03/2024** | U | F1 p. 2, p. 16 |
| Decorrenza | Le premesse dicono "dal giorno **01/05/2024**"; l'art. 2 dice "dalla data del suo deposito in Comune, in Provincia ed in Regione" | U | F1 p. 2, p. 4 |
| Deposito in Comune (Seveso, Cesano) | **Data non trovata**: il testo pubblicato non la riporta | — | — |
| Durata | **18 mesi dal deposito**; "resta in vigore sino alla sottoscrizione di un Nuovo Accordo" | U | F1 p. 13, art. 14 |
| Scadenza formale | 01/11/2025, se il deposito coincide con il 01/05/2024 | D, T | F1, F4 |
| Dopo la scadenza | Fino al nuovo accordo i limiti minimi e massimi delle fasce "potranno essere incrementati" con l'**intera** variazione ISTAT FOI, dal primo giorno del mese successivo al deposito all'ultimo giorno del mese precedente la stipula del contratto | U | F1 p. 13, art. 14 |
| Nuovo accordo | Nessuno trovato al 2026-09-23 (ricerca negativa, nessun risultato successivo al 2024) | T | ricerche web del 2026-09-23 |
| Ambito | **55 Comuni, compreso Misinto** | U | F1 p. 1, p. 4 art. 3 |
| Zone di Seveso | Unica zona urbana omogenea su tutto il territorio comunale (in tabella "Zona 1") | U | F1 p. 4, p. 63 |
| Zone di Cesano Maderno | Zona 1 "Centrale": fogli 1, 12, 19, 22, 23, 26, 27, 28, 32, 33. Zona 2 "Semi periferica": fogli 2-11, 13-18, 20-21, 24-25, 29-31, 34-35 | U | F1 p. 5 |
| Elenco "alta densità abitativa" | Le premesse includono Seveso e Cesano Maderno tra i comuni dell'"elenco del CIPE datato 13/11/2003" con cedolare 10% e riduzioni IRPEF/registro. È una dichiarazione delle parti firmatarie, non il testo CIPE: non basta per `VerifiedDirectly` (competenza RS-5) | U (dichiarazione), D (valore probatorio) | F1 p. 1 |
| Comuni capofila | Incoerenza interna: l'art. 14 cita Biassono, Desio, Monza, Seregno, Vimercate; la pagina delle firme cita l'Ambito di Carate Brianza. Le firme dei sindaci sono vuote | U | F1 p. 13, p. 18 |
| Recepimento a Seveso | Nessuna delibera trovata. Il Comune pubblica l'accordo e il suo modulo IMU chiede la fascia 1, 2 o 3 | D | F1, F3 |
| Recepimento a Cesano Maderno | Non verificabile (sito bloccato) | — | — |

### Firmatari validi per Seveso e Cesano Maderno

Sede e telefono sono quelli dichiarati nell'accordo nel 2024 (U, F1 p. 3 e p. 17).

| Organizzazione | Ruolo | Sede | Telefono |
|---|---|---|---|
| CONIA | Inquilini | Milano, viale Monza 137 | 02 2814151 |
| SICET | Inquilini | Monza, via Dante 17/A | 039 2399259 |
| SUNIA | Inquilini | Monza, via Premuda 17 | 039 2731201 · 345 6035702 |
| UNIAT | Inquilini | Monza, via Ardigò 15/A | — |
| A.P.E. Monza (aderente Confedilizia) | Proprietà | Monza, via Mosè Bianchi 18/A | 342 5745184 |
| A.S.P.P.I. Comprensorio Brianza | Proprietà | Seveso, via L. Maderna 4 | 393 6435891 |
| CONFABITARE | Proprietà | Monza, via F. Magellano 21 | 380 6929090 |
| CONFAPPI | Proprietà | Monza, via Ponchielli 47 | 335 5368700 |
| FEDERPROPRIETÀ | Proprietà | Milano, viale Certosa 1 | 02 45478950 |
| U.P.P.I. | Proprietà | Monza, via G.F. Parravicini 30 | 02 2047734 |
| UNIONCASA | Proprietà | Limbiate, corso Milano 14 | 328 9050358 · 02 97135036 |

A.S.P.P.I. Monza firma solo per Agrate Brianza, Biassono, Lissone, Monza, Muggiò, Vedano al Lambro e Vimercate (la pagina delle firme aggiunge Brugherio): **non** vale per Seveso e Cesano (U, F1 p. 3, p. 17).

**Attestazione di conformità** (contratti non assistiti): "è valida solo ove rilasciata **congiuntamente** da una delle organizzazioni della Proprietà Edilizia e da una dei conduttori firmatarie del presente accordo" (U, F1 p. 12, art. 12). In MB servono quindi **due** organizzazioni, una per parte. Chiude il gap n. 9 di `Sessions/research-canone-concordato-mb.md`.

### Tabelle dei canoni (€/mq annui)

Tutti i 72 valori del seed (24 per Seveso, 48 per Cesano Maderno) coincidono con l'accordo (U).

**Seveso**, allegato 2/S (F1 p. 63). Valori identici a Giussano, Lentate sul Seveso e Meda.

| Fascia | Sub-fascia 1 | Sub-fascia 2 | Sub-fascia 3 |
|---|---|---|---|
| Fino a 50 mq | 20-57 | 58-91 | 92-109 |
| Da 51 a 74 mq | 20-52 | 53-85 | 86-100 |
| Da 75 a 99 mq | 20-45 | 46-71 | 72-86 |
| Oltre 100 mq | 20-41 | 42-62 | 63-76 |

**Cesano Maderno**, allegato 1/U (F1 p. 39).

| Fascia | Zona 1 Centrale: SF1 / SF2 / SF3 | Zona 2 Semi periferica: SF1 / SF2 / SF3 |
|---|---|---|
| Fino a 50 mq | 20-65 / 66-102 / 103-120 | 20-55 / 56-90 / 91-105 |
| Da 51 a 74 mq | 20-60 / 61-94 / 95-110 | 20-50 / 51-85 / 86-100 |
| Da 75 a 99 mq | 20-50 / 51-80 / 81-95 | 20-45 / 46-70 / 71-83 |
| Oltre 100 mq | 20-45 / 46-70 / 71-85 | 20-40 / 41-60 / 61-73 |

**Fasce di superficie.**
- Le etichette sono letterali: "Fino a 50 Mq.", "Da 51 a 74 Mq.", "Da 75 a 99 Mq.", "Oltre 100 Mq." (U). Le fasce **non sono contigue**: restano scoperti i valori tra 50 e 51, tra 74 e 75, tra 99 e 100 e, a rigore, anche 100 mq esatti (U, lettura letterale).
- **Decimali**: l'accordo non ne parla (U). La superficie è "quella prevista dal DPR 138/98, ovvero quella catastale" (U, F1 p. 8), che è arrotondata al metro quadrato (T, F8). I decimali compaiono quando si sommano le pertinenze in percentuale (D).
- La fascia si sceglie sui **mq utili**: superficie più pertinenze locate insieme (U, F1 p. 8-9). Una misura difforme entro il ±10% non cambia il canone (U, F1 p. 8).
- **Proposta (D)**, da confermare con un'organizzazione firmataria: arrotondare i mq utili al metro quadrato intero (0,5 per eccesso) prima di cercare la fascia, e mettere 100 mq nella fascia "Oltre 100", come fa già il seed. Alternativa equivalente sugli interi: intervalli contigui (0,50], (50,74], (74,99], (99,∞).

### Sub-fasce (U, F1 p. 6-8)

Elementi: **A** = A1-A2 (bagno completo areato, impianti essenziali); **B** = B1-B5 (cucina abitabile con finestra, ascensore, manutenzione normale dell'unità, impianti a norma, riscaldamento centralizzato o autonomo); **C** = C1-C7 (doppio bagno, posto auto scoperto assegnato, giardino condominiale, manutenzione buona dell'unità, manutenzione normale dello stabile, porte blindate o doppi vetri, servizi entro 1 km); **D** = D1-D9 (balconi o terrazzo, cantina o soffitta, vetustà sotto 30 anni, fotovoltaico, allarme, giardino privato, autorimessa o posto auto coperto, intervento edilizio negli ultimi 10 anni, classe energetica A-D).

- **Sub-fascia 1**: manca anche un solo elemento A; **oppure** il riscaldamento è a stufe nei singoli locali, **salvo** che l'immobile abbia almeno 4 elementi B; **oppure** ha meno di 3 elementi B.
- **Sub-fascia 2**: tutti gli A e almeno 3 B (con meno di 3 C).
- **Sub-fascia 3**: tutti gli A, almeno 3 B, 3 C e **almeno 2 tra D1, D2, D4, D6, D7 e D9**.
- Almeno 4 elementi D (qualsiasi) "comporta la possibilità di applicare il valore massimo del canone della sub-fascia 3". Se con 2-3 elementi D il massimo della sub-fascia 3 sia precluso, e quale tetto valga, il testo non lo dice (D).
- **Sub-fascia inferiore** (art. 4.1.d, p. 9): le parti "potranno optare [...] per la sub-fascia con valori inferiori", il minimo della sub-fascia 1 "non può essere ulteriormente ribassato" e il canone è "comunque non superiore al limite della sub-fascia di appartenenza" (U). Ne segue che per un immobile in sub-fascia n il canone lecito va dal **minimo della sub-fascia 1** (20 €/mq in entrambi i comuni) al **massimo della sub-fascia n** (D, lettura diretta).

### Coefficienti (U, F1 p. 8-9)

| Condizione | Testo dell'accordo | Effetto | Tetto | Si applica a | Forma |
|---|---|---|---|---|---|
| Arredo completo | "i valori delle sub-fasce potranno aumentare fino ad un massimo del 15%"; con arredo parziale meno del massimo, in proporzione, e con cucina o angolo cottura attrezzati | fino a +15% | 15% | €/mq | facoltativa |
| Superficie < 40 mq | "si potrà applicare a detta superficie una maggiorazione del 20% fino al limite di 40 mq" | mq × 1,20 | risultato ≤ 40 mq | superficie | facoltativa |
| Superficie > 50 e < 60 mq | "superiore a 50 mq ed inferiore a 60 mq [...] maggiorazione del 10% fino al limite di 60 mq" | mq × 1,10 | risultato ≤ 60 mq | superficie | facoltativa |
| Superficie > 120 mq | "potrà essere ridotta del 20% e il computo finale non potrà essere inferiore a 120 mq" | mq × 0,80 | risultato ≥ 120 mq | superficie | "potrà" |
| Durata 4, 5, 6 anni | "i limiti minimi e massimi della sub-fascia [...] sono aumentati" | +3%, +5%, +6% | — | min e max €/mq | obbligatoria |
| Aria condizionata su ≥ 50% della superficie | "i valori delle sub-fasce potranno aumentare fino ad un massimo del 5%" | fino a +5% | 5% | €/mq | facoltativa |
| Pertinenze locate | box o posto auto coperto 50%; balconi e terrazze 30%; posto auto scoperto, cantina, solaio o altre 25%; aree verdi esclusive 10% | si sommano ai mq | — | mq utili | — |

- "Tutte le variazioni predette sono tra loro cumulabili" (U). Se le percentuali si sommano o si moltiplicano non è scritto (D).
- Le maggiorazioni di superficie sono "subordinate al preventivo conteggio delle pertinenze" (U). Però le soglie di 40, 50-60 e 120 mq sono definite sulla "superficie dell'abitazione", cioè vani principali e accessori diretti. Se le soglie si misurino con o senza pertinenze non è chiaro (D).
- Durate oltre 6 anni: l'accordo non le prevede (U, assenza).

### Durate e altre regole

| Regola | Valore | Classe | Fonte |
|---|---|---|---|
| 3+2 (art. 2 c. 3 L. 431/1998) | Minimo 3 anni ("durata [...] superiore alla minima triennale"); maggiorazioni solo per 4, 5 e 6 anni | U | F1 p. 9 |
| Transitorio (art. 5 c. 1) | Rinvio all'art. 2 c. 1 del DM: fino a 18 mesi. Motivi ammessi elencati nell'accordo, altrimenti assistenza delle organizzazioni | U (rinvio e motivi), T (durata) | F1 p. 10-11, F7 |
| Transitorio fino a 30 giorni | Canone e oneri accessori liberi | U | F1 p. 10 |
| Studenti (art. 5 c. 2) | Rinvio all'art. 3 c. 2 del DM: da 6 mesi a 3 anni. Recesso del conduttore con 2 mesi di preavviso se interrompe gli studi | U (rinvio e recesso), T (durate) | F1 p. 11, F7 |
| Canone di transitori e studenti | Stessi criteri 4.1 a-d (stesse fasce e coefficienti). L'accordo MB **non** prevede variazioni specifiche per i transitori | U | F1 p. 10-11 |
| Aggiornamento ISTAT | Al massimo il 75% della variazione annua FOI; non si applica finché è attiva l'opzione per la cedolare secca | U | F1 p. 9 |
| Deposito cauzionale | Al massimo 3 mensilità; garanzie alternative entro lo stesso importo | U | F1 p. 12 |
| Locazione di camere | Consentita; la somma dei canoni delle porzioni non supera il canone dell'intera unità | U | F1 p. 11 |

### IMU comunale per il concordato

| Comune | Dato | Classe | Fonte |
|---|---|---|---|
| Seveso | **2025**: aliquota **0,76%** per "abitazione locata [...] ai sensi dell'art. 2, comma 3, della Legge n. 431/1998", categorie A/2-A/7 e A/11, "purché l'affittuario [...] la utilizzi come abitazione principale". A questa si applica la riduzione di legge del 25% (art. 1 c. 760 L. 160/2019): 0,57% effettivo | U | F2 |
| Seveso | Il modulo del Comune chiede "l'aliquota IMU agevolata del **5,7 per mille**", anche per contratti transitori e per studenti. Il prospetto MEF cita solo l'art. 2 c. 3: incoerenza da chiarire con il Comune (D) | U | F3 |
| Seveso | Aliquota ordinaria "altri fabbricati" 2025: 1,06% | U | F2 |
| Seveso | Canale: modulo indirizzato ai **Servizi Sociali** e, per conoscenza, all'**Ufficio Tributi**, all'indirizzo `protocollo@comune.seveso.mb.it` (viale Vittorio Veneto 3/5) | U | F3 |
| Seveso | 2026: secondo la ricerca di agosto, la delibera CC n. 43 del 25/11/2025 conferma le aliquote 2025. Non verificato | T | ricerca §5 |
| Cesano Maderno | 2025: delibera CC n. 132 del 19/12/2024. L'1,04% risulta per il gruppo D; per gli "altri fabbricati" e per l'assenza di una riga dedicata al concordato **non c'è verifica** | T | F6 |
| Cesano Maderno | 2026: non verificato | — | — |

### Valore nel codice e valore ufficiale (solo le discrepanze)

Riferimenti di riga al commit base di RS-8 (`24dd008`).

| # | Dove | Nel codice | Ufficiale | Classe | Task |
|---|---|---|---|---|---|
| 1 | `CanoneConcordatoMbSeed.cs:11`, `:24-35`; test `CanoneConcordatoEligibilityServiceTests.cs:262` e `TerritorialRentAgreementMigrationTests.cs:46` | 54 comuni senza **Misinto**; commento "do not invent a 55th"; i test fissano 54 | 55 comuni, compreso Misinto | U | LT-10 |
| 2 | `CanoneConcordatoEligibilityService.cs:163-166` | +10% per 50 ≤ mq ≤ 60 | +10% solo per 50 < mq < 60 | U | LT-10 |
| 3 | `:161-162` | +20% sul canone unitario, senza tetto | mq × 1,20 con tetto a 40 mq | U | LT-10 |
| 4 | `:163-166` | +10% sul canone unitario, senza tetto | mq × 1,10 con tetto a 60 mq | U | LT-10 |
| 5 | `:167-168` | −20% sul canone unitario, senza minimo | mq × 0,80 e non sotto 120 mq; riduzione facoltativa | U; D (facoltatività) | LT-10 |
| 6 | `:56-60`, `:147-153` | Range = [min SF n, max SF n] | Tetto = max SF n; si può scendere fino al min della SF 1 (20 €/mq) | U (testo), D (minimo) | LT-10 |
| 7 | `:143` | SF 3 con almeno 2 elementi D qualsiasi | Almeno 2 tra D1, D2, D4, D6, D7, D9 | U | LT-10 |
| 8 | `:133-145` | Stufe non gestite | Stufe → SF 1, salvo almeno 4 elementi B | U | LT-10 |
| 9 | `:143-144` | Max della SF 3 già con 2 elementi D | Max della SF 3 "possibile" con almeno 4 elementi D | U; D | LT-10 |
| 10 | `:155-179` | Aria condizionata assente | Fino a +5% se copre ≥ 50% della superficie | U | LT-10 |
| 11 | `RentBandCharacteristics` | Pertinenze assenti | +50/30/25/10% ai mq, prima delle maggiorazioni | U | LT-10 |
| 12 | `:106-109` | Bande intere `MinSqm ≤ x ≤ MaxSqm` su `decimal`: 50,5 / 74,5 / 99,5 senza banda | Accordo silente sui decimali; superficie catastale intera | U, T, D | LT-10 |
| 13 | `:170-176` | Anni non validati: 0, 1 e 2 accettati; oltre 6 → 0% | 3+2 almeno 3 anni; maggiorazioni solo per 4, 5, 6 anni; oltre 6 non previsto | U | LT-10 |
| 14 | `CanoneConcordatoMbSeed.cs:106-111` | 3 organizzazioni (A.S.P.P.I. Comprensorio Brianza, Confabitare, SUNIA) | 11 organizzazioni: 4 degli inquilini e 7 della proprietà (tabella sopra) | U | LT-10 |
| 15 | FE `src/i18n/locales/it.json:343` e `en.json:343` (`guidanceIntro`) | "Contatta direttamente un'organizzazione firmataria" | Attestazione valida solo se congiunta: una organizzazione della proprietà **e** una degli inquilini | U | LT-10 |
| 16 | `TerritorialRentAgreement` (entità) | Nessun campo per deposito o scadenza | 18 mesi dal deposito, ultrattività, fasce aggiornabili con il 100% FOI dopo la scadenza | U | LT-13 (A7-22), LT-10 (avviso) |
| 17 | `CanoneConcordatoMbSeed.cs:18` (`AtaSource`) | "Secondary sources converging on CIPE 13/11/2003" | Le premesse di F1 elencano Seveso e Cesano nell'elenco CIPE (dichiarazione delle parti). Citare F1; `VerifiedDirectly` resta `false` | U, D | LT-13 / RS-5 |
| 18 | `ComuneImuNotificationService.cs:82-88` | Seveso: tre indirizzi e portale, "canale NON univoco" | Modulo 2025 ai Servizi Sociali, p.c. Ufficio Tributi, via `protocollo@comune.seveso.mb.it`; aliquota agevolata 5,7‰ (2025) | U | LT-13 |
| 19 | `ComuneImuNotificationService.cs:94` | Cesano: "1,04% x 75% circa 0,78%" per il 2025 | Non verificabile (fonti bloccate); l'1,04% risulta solo per il gruppo D | T | LT-13 |

Esempi per i test (Seveso, sub-fascia 2, 3 anni, nessun altro coefficiente; massimo annuo):

| Superficie | Codice oggi | Accordo | Nota |
|---|---|---|---|
| 35 mq | 91 × 35 × 1,20 = **3.822,00 €** | 91 × 40 = **3.640,00 €** | tetto a 40 mq |
| 50 mq | 91 × 50 × 1,10 = **5.005,00 €** | 91 × 50 = **4.550,00 €** | a 50 mq non spetta il +10% |
| 58 mq | 85 × 58 × 1,10 = **5.423,00 €** | 85 × 60 = **5.100,00 €** | tetto a 60 mq |
| 130 mq | 62 × 130 × 0,80 = **6.448,00 €** | 62 × 120 = **7.440,00 €** | minimo a 120 mq; senza la riduzione facoltativa 8.060,00 € (D) |
| 65 mq (minimo) | 53 × 65 = **3.445,00 €** | 20 × 65 = **1.300,00 €** | le parti possono scegliere una sub-fascia inferiore (D) |

### Cosa deve cambiare LT-10

1. **Seed dei comuni.** Aggiungere `"Misinto"` tra `"Mezzago"` e `"Monza"` in `ProvinceComuni` (resta `Missing`); correggere il commento (55 comuni); aggiornare i due test da 54 a 55. La migrazione `20260816203709_AddTerritorialRentAgreements` legge la classe di seed "viva" (A7-22): il nuovo comune va inserito con una nuova migrazione di dati, non cambiando il comportamento di quella esistente (coordinarsi con LT-13).
2. **Seed dei firmatari.** Sostituire le 3 voci con le 11 organizzazioni della tabella "Firmatari", ruolo e telefono da F1. Le email attuali di Confabitare e SUNIA non compaiono nell'accordo (T): tenerle solo se riverificate.
3. **Valori dei coefficienti.** Sono corretti e non vanno cambiati (15; 40 e 20; 50, 60 e 10; 120 e 20; 3, 5 e 6; 2 elementi A). Va cambiata la semantica: soglia 50-60 esclusiva e tetti o minimi sui mq. Per i nuovi dati (aria condizionata 5%, percentuali delle pertinenze, eccezione stufe con 4 B, elementi D qualificanti D1/D2/D4/D6/D7/D9, 4 D per il massimo della SF 3, soglie 3/3/2, durata minima 3 anni, data di deposito e scadenza, attestazione congiunta) servono campi sull'accordo, non costanti nel servizio (A7-22).
4. **Formula** (dal testo; le parti D vanno confermate):
   - `mqUtili = superficie + Σ pertinenza × percentuale`; fascia cercata su `round(mqUtili)` (D).
   - `mqCalcolo`: sotto 40 → `min(mqUtili × 1,2; 40)`; oltre 50 e sotto 60 → `min(mqUtili × 1,1; 60)`; oltre 120 → `max(mqUtili × 0,8; 120)`.
   - `fattore = 1 + arredo (fino al 15%) + durata (3/5/6%) + aria condizionata (fino al 5%)`. Somma o prodotto: D.
   - `massimo = max SF n × fattore × mqCalcolo`. Per il minimo, partire dal min della SF 1 e applicare solo le variazioni obbligatorie (durata): D.
   - Rifiutare `ContractYears < 3` per il 3+2. Oltre 6 anni lasciare lo 0% (prudente sul massimo) finché non risponde un'organizzazione firmataria.
5. **Fasce di superficie.** Applicare la proposta del paragrafo "Fasce di superficie" e usare un `Reason` distinto ("superficie fuori fascia") invece di "zona obbligatoria".
6. **Dati Partial (A7-23).** Tenere `DataCompleteness.Partial`. Il range resta un consiglio e non un blocco. Mostrare fonte (F1) e data di verifica (2026-09-23).
7. **Testo FE.** `guidanceIntro` in IT e EN: servono due organizzazioni, una della proprietà e una degli inquilini.

### Esito: i dati possono passare da Partial a verificati?

- **Tabelle e zone** di Seveso e Cesano Maderno: **verificate (U)**, nessun valore da correggere. Si può registrare la verifica (fonte F1, 2026-09-23).
- **Coefficienti**: valori verificati (U), ma la semantica nel codice è sbagliata (righe 2-13 della tabella).
- **`DataCompleteness` deve restare `Partial`** per questi motivi:
  1. La data di deposito non è documentata. L'accordo è formalmente scaduto (18 mesi) ed è in ultrattività, con fasce aggiornabili con l'ISTAT, che il codice non gestisce.
  2. L'assenza di un nuovo accordo è verificata solo per ricerca negativa: i siti delle associazioni e dei Comuni sono bloccati.
  3. Il recepimento a Seveso è solo dedotto; a Cesano Maderno non è verificato.
  4. Restano punti interpretativi (D): decimali e 100 mq; somma o prodotto dei coefficienti; riduzione oltre 120 mq e maggiorazioni sul minimo; massimo della SF 3 con 4 elementi D; durate oltre 6 anni; transitori e studenti nel modulo IMU di Seveso.
  5. I firmatari nel seed sono incompleti.
- **Passaggio a `Complete`**: quando LT-10 ha corretto la semantica e un'organizzazione firmataria (per esempio A.S.P.P.I. Comprensorio Brianza, che ha sede a Seveso) o il Comune ha confermato per iscritto vigenza, deposito e i punti D. Data e fonte vanno registrate in `LastVerifiedAt`.

## Impatto su CasaZen

### Funzionalità coinvolte (proposta in `Sessions/specs/spec-ltr-canone-concordato-calculator.md`, frozen)

1. **Calcolatore idoneità**: dato un comune + caratteristiche immobile (mq, dotazioni, arredamento, durata) → fascia/canone + split esplicito IMU (sempre) vs. cedolare/IRPEF/registro (solo se ATA)
2. **Dati di riferimento**: accordi territoriali e relative tabelle parametriche — **mai hardcodati**, stesso pattern di `TouristTaxRate`
3. **Guida attestazione**: elenco associazioni firmatarie con contatti (nessuna chiamata API esterna, nessun rilascio automatico)
4. **Export comunicazione IMU**: pacchetto comune-specifico (destinatario, testo precompilato) — **export/anteprima, mai invio automatico**

### Criticità tecniche

- **Due liste distinte** (ATA nazionale vs. copertura accordo locale) da modellare come entità separate, non un solo flag
- **Dati incompleti per la maggior parte dei comuni**: il pilota copre solo Seveso e Cesano Maderno (Provincia di Monza e Brianza), con tabelle verificate da RS-8 ma stato `Partial` (vedi "Accordi verificati (2026-09)"); qualunque altro comune deve restituire "dato non disponibile", mai una stima
- **Nessuna automazione della registrazione fiscale**: CasaZen non è *intermediario abilitato* (DPR 322/1998) — ogni invio (RLI o IMU) resta un'azione esplicita del locatore
- **Valori derivati vs. ufficiali**: alcuni Comuni (es. Cesano Maderno) non pubblicano un'aliquota IMU dedicata al concordato — va calcolata e **etichettata come derivata**, non presentata come aliquota ufficiale

### Dati da tracciare

- Comune, accordo territoriale di riferimento, stato di completezza dei dati, data di ultima verifica
- Zona/fascia/sub-fascia e range di canone risultante per immobile
- Stato ATA (nazionale) del comune, separato dallo stato di copertura dell'accordo
- Stato dell'attestazione di conformità (assistito / non assistito / acquisita)
- Stato della comunicazione IMU al Comune (esportata / inviata dal locatore — mai "automaticamente completata")

## Sorgenti

Ricerca completa con tabelle ufficiali, contatti e gap espliciti: `Sessions/research-canone-concordato-mb.md`. Business analysis: `Sessions/business-analysis-canone-concordato.md`. Spec tecnica: `Sessions/specs/spec-ltr-canone-concordato-calculator.md`.

**Data consultazione ricerca sottostante**: 2026-08-16 · **Verifica RS-5 su fonti ufficiali**: 2026-09-23 (vedi `fiscale.md`) · **Verifica RS-8 dell'accordo territoriale MB**: 2026-09-23 (sezione "Accordi verificati (2026-09)")
