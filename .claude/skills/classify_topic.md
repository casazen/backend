# Skill: Classify Topic - Classificazione Normativa per Macro-Argomento

## Descrizione
Questa skill descrive come classificare un provvedimento normativo o una novita' legislativa all'interno dei macro-argomenti definiti per il monitoraggio degli affitti brevi.

## Quando Usarla
- Quando hai estratto un nuovo provvedimento normativo e devi catalogarlo
- Quando devi decidere in quale file di contesto inserire un'informazione
- Quando un provvedimento tocca piu' argomenti e devi decidere la classificazione primaria

## Tassonomia

### Macro-Argomenti
| ID | Argomento | Keyword | File Contesto |
|----|-----------|---------|---------------|
| 1 | Codice CIN | CIN, codice identificativo, BDSR, registrazione | `regulations/cin.md` |
| 2 | Comunicazione Alloggiati | alloggiati, questura, PS, schedina, check-in | `regulations/alloggiati.md` |
| 3 | Imposta di Soggiorno | imposta soggiorno, tassa soggiorno, tributo locale | `regulations/imposta_soggiorno.md` |
| 4 | Regime Fiscale | cedolare secca, ritenuta 21%, redditi, IRPEF, dichiarazione | `regulations/fiscale.md` |
| 5 | Normativa OTA | piattaforma, intermediario, DAC7, obblighi comunicazione | `regulations/ota_normativa.md` |
| 6 | GDPR | privacy, dati personali, consenso, GDPR, trattamento | `regulations/gdpr.md` |
| 7 | Sicurezza | sicurezza, estintore, rilevatore, requisiti strutturali | `regulations/sicurezza.md` |
| 8 | Normativa Regionale | regione, regionale, comunale, locale | `regulations/regionale.md` |

## Procedura di Classificazione

### Step 1: Analisi Keyword
Analizza il testo del provvedimento cercando le keyword nella tabella sopra.

### Step 2: Classificazione Primaria
Assegna il macro-argomento che meglio rappresenta l'oggetto principale del provvedimento.

**Regole di precedenza** (in caso di ambiguita'):
1. Se il provvedimento riguarda specificamente il CIN -> argomento 1
2. Se riguarda obblighi di comunicazione alla Questura -> argomento 2
3. Se riguarda tributi locali -> argomento 3
4. Se riguarda tassazione nazionale -> argomento 4
5. Se riguarda obblighi delle piattaforme online -> argomento 5
6. Se riguarda trattamento dati -> argomento 6
7. Se riguarda requisiti fisici dell'immobile -> argomento 7
8. Se e' specifico di una regione -> argomento 8

### Step 3: Classificazione Secondaria
Se il provvedimento tocca piu' argomenti:
- Inserisci il contenuto nel file dell'argomento primario
- Aggiungi un riferimento incrociato (cross-reference) nei file degli argomenti secondari

Formato cross-reference:
```markdown
> Vedi anche: `regulations/[file_primario].md` - [breve descrizione del collegamento]
```

### Step 4: Tag
Assegna i seguenti tag al provvedimento:
- **scope**: `nazionale` | `regionale` | `europeo`
- **status**: `in_vigore` | `in_attesa` | `abrogato`
- **urgency**: `immediato` | `prossima_scadenza` | `informativo`

## Esempio

**Input**: "Il D.L. 145/2023 introduce l'obbligo del Codice Identificativo Nazionale (CIN) per le strutture ricettive e gli immobili destinati a locazioni brevi. Il CIN deve essere esposto nell'annuncio e all'esterno dell'immobile. Previste sanzioni da 800 a 8.000 euro."

**Output**:
- **Classificazione primaria**: 1 - Codice CIN
- **Classificazione secondaria**: 5 - Normativa OTA (obbligo esposizione su piattaforme)
- **Tag**: scope=nazionale, status=in_vigore, urgency=immediato
- **File destinazione**: `regulations/cin.md`
- **Cross-reference**: in `regulations/ota_normativa.md` aggiungere riferimento al CIN
