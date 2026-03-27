# Skill: Diff Context - Confronto Vecchio e Nuovo Contesto

## Descrizione
Questa skill descrive come confrontare lo stato precedente e attuale dei file di contesto normativo per identificare cambiamenti, novita' e aggiornamenti.

## Quando Usarla
- Prima di aggiornare un file di contesto, per capire cosa e' cambiato
- Dopo un aggiornamento normativo, per produrre un changelog
- Per verificare se un aggiornamento ha introdotto nuovi requisiti

## Procedura

### Step 1: Snapshot Precedente
Leggi il file `_last_updated.json` per ottenere:
- Data ultimo aggiornamento
- Hash dell'indice precedente

Se l'hash non e' disponibile (prima esecuzione), considera tutto come "nuovo".

### Step 2: Lettura Stato Attuale
Leggi tutti i file in `.claude/context/regulations/` e `.claude/context/_index.md`.

### Step 3: Confronto
Per ogni file di contesto, confronta:

| Aspetto | Cosa Cercare |
|---------|-------------|
| **Nuovi file** | File in `regulations/` che non esistevano prima |
| **File modificati** | Contenuto diverso rispetto all'ultima lettura |
| **Nuovi requisiti** | Obblighi non presenti nella versione precedente |
| **Scadenze cambiate** | Date di entrata in vigore modificate |
| **Sanzioni aggiornate** | Importi o tipologie di sanzioni cambiate |
| **Abrogazioni** | Norme abrogate o sostituite |

### Step 4: Generazione Diff Report

Formato output:

```markdown
# Diff Report - [DATA]

## Confronto con ultimo aggiornamento del [DATA_PRECEDENTE]

### Nuovi File
- `regulations/[nome].md` - [breve descrizione]

### File Modificati
- `regulations/[nome].md`
  - AGGIUNTO: [descrizione novita']
  - MODIFICATO: [cosa e' cambiato]
  - RIMOSSO: [cosa non e' piu' valido]

### Nuovi Requisiti Identificati
| Requisito | Fonte | Scadenza | Priorita' |
|-----------|-------|----------|-----------|
| [desc] | [legge] | [data] | CRITICAL/HIGH/MEDIUM/LOW |

### Requisiti Rimossi/Abrogati
| Requisito | Motivo |
|-----------|--------|
| [desc] | [abrogato da / sostituito da] |

### Nessun Cambiamento
- `regulations/[nome].md` - invariato
```

### Step 5: Aggiornamento Metadata
Dopo il confronto, aggiorna `_last_updated.json` con il nuovo stato.

## Gestione Casi Speciali

### Prima Esecuzione
Se `_last_updated.json` ha `last_update: null`:
- Tratta tutto come "nuovo"
- Non generare un diff, ma un report iniziale

### File Corrotto o Mancante
Se un file di contesto e' mancante o corrotto:
- Segnala nel report
- Ricrealo dal contesto disponibile
- Annota che i dati potrebbero essere incompleti

## Best Practice
- Esegui sempre il diff PRIMA di sovrascrivere i file di contesto
- Conserva il diff report per tracciabilita'
- I nuovi requisiti identificati nel diff devono essere passati all'`analyzer_agent`
