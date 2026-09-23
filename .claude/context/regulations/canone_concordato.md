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

## Impatto su CasaZen

### Funzionalità coinvolte (proposta in `Sessions/specs/spec-ltr-canone-concordato-calculator.md`, frozen)

1. **Calcolatore idoneità**: dato un comune + caratteristiche immobile (mq, dotazioni, arredamento, durata) → fascia/canone + split esplicito IMU (sempre) vs. cedolare/IRPEF/registro (solo se ATA)
2. **Dati di riferimento**: accordi territoriali e relative tabelle parametriche — **mai hardcodati**, stesso pattern di `TouristTaxRate`
3. **Guida attestazione**: elenco associazioni firmatarie con contatti (nessuna chiamata API esterna, nessun rilascio automatico)
4. **Export comunicazione IMU**: pacchetto comune-specifico (destinatario, testo precompilato) — **export/anteprima, mai invio automatico**

### Criticità tecniche

- **Due liste distinte** (ATA nazionale vs. copertura accordo locale) da modellare come entità separate, non un solo flag
- **Dati incompleti per la maggior parte dei comuni**: il pilota copre solo Seveso e Cesano Maderno (Provincia di Monza e Brianza) con dati completi; qualunque altro comune deve restituire "dato non disponibile", mai una stima
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

**Data consultazione ricerca sottostante**: 2026-08-16 · **Verifica RS-5 su fonti ufficiali**: 2026-09-23 (vedi `fiscale.md`)
