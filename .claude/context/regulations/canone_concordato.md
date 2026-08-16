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
| Cedolare secca **10%** (anziché 21%) | Solo se il Comune è "**ad alta tensione abitativa**" (elenco CIPE 13/11/2003 — mai formalmente aggiornato) |
| Riduzione IRPEF **−30%** (regime ordinario) | Idem — solo comuni ATA |
| Riduzione imposta di registro **−30%** (base al 70%, aliquota 2%) | Idem — solo comuni ATA |

**Attenzione**: la lista "comuni ATA" (nazionale, cedolare/IRPEF/registro) è **distinta** dalla lista "comuni ad alta densità abitativa" usata da un singolo accordo territoriale per definire la propria copertura — si sovrappongono spesso ma non sono la stessa cosa. Non trattarle come intercambiabili in un calcolatore.

### Attestazione di conformità — condizione per tutte le agevolazioni

Obbligatoria per contratti **non assistiti** stipulati dopo il 30/03/2017 (Risoluzione AdE 31/E/2018). Rilasciata da **almeno una** organizzazione firmataria dell'accordo territoriale (proprietà o inquilini). Senza di essa: decadenza retroattiva di tutte le agevolazioni + sanzioni 90–180%. **CasaZen non può rilasciarla** — solo indirizzare verso l'associazione competente.

### Doppio adempimento post-registrazione (spesso non colto)

1. **Registrazione RLI** (Agenzia delle Entrate, 30 giorni) → vedi `spec-ltr-rli-registration.md` per il flusso assistito già speccato.
2. **Comunicazione IMU al Comune** — **separata**, quasi mai automatica: ogni Comune ha una propria procedura (modulo, email, PEC) per applicare lo sconto IMU. La sola registrazione RLI/cedolare secca **non** attiva lo sconto IMU.

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

**Data consultazione ricerca sottostante**: 2026-08-16
