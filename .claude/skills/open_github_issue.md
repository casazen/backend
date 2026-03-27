# Skill: Open GitHub Issue - Apertura Issue via gh CLI

## Descrizione
Questa skill descrive come aprire issue su GitHub in modo strutturato usando la `gh` CLI. Include template, label, e best practice per issue di compliance normativa.

> **Riusabile cross-project**: questa skill funziona con qualsiasi repository GitHub accessibile via `gh` CLI.

## Quando Usarla
- Quando il `github_agent` deve creare issue da gap analysis
- Quando serve aprire issue strutturate in modo programmatico
- Quando serve aggiornare issue esistenti con nuove informazioni

## Prerequisiti
Verifica che la `gh` CLI sia disponibile e autenticata:
```bash
gh auth status
```

## Procedura

### Step 1: Verifica Duplicati
Prima di creare un'issue, cerca duplicati:
```bash
gh issue list --search "keyword1 keyword2" --state open --json number,title
```

Se trovi un'issue simile, valuta se:
- Aggiungere un commento invece di creare una nuova issue
- L'issue esistente copre gia' il requisito

### Step 2: Preparazione Label
Assicurati che le label necessarie esistano:
```bash
gh label create "regulatory" --description "Regulatory compliance" --color "d93f0b" 2>/dev/null || true
gh label create "compliance" --description "Compliance gap" --color "fbca04" 2>/dev/null || true
```

### Step 3: Creazione Issue
Usa il comando `gh issue create` con heredoc per il body:

```bash
gh issue create \
  --title "[COMPLIANCE] Titolo descrittivo del gap" \
  --label "regulatory,compliance,priority:high" \
  --body "$(cat <<'ISSUE_EOF'
## User Story
Come **[ruolo]**, voglio **[azione]**, in modo da **[beneficio]**.

## Contesto Normativo
- **Riferimento**: [legge/decreto]
- **Data entrata in vigore**: [data]
- **Sanzioni previste**: [descrizione]

## Stato Attuale
[descrizione di cosa esiste nella codebase]

## Requisiti di Implementazione
- [ ] [requisito 1]
- [ ] [requisito 2]

## Acceptance Criteria
- [ ] [criterio 1]
- [ ] [criterio 2]

## Riferimenti
- [link sorgente normativa]

---
_Issue generata dal sistema di Regulatory Intelligence_
ISSUE_EOF
)"
```

### Step 4: Cattura Output
Cattura il numero dell'issue creata per aggiornare il registro:
```bash
ISSUE_URL=$(gh issue create --title "..." --body "..." 2>&1)
echo "$ISSUE_URL"
```

### Step 5: Aggiunta Commenti (opzionale)
Per aggiungere informazioni a issue esistenti:
```bash
gh issue comment ISSUE_NUMBER --body "Aggiornamento: [descrizione]"
```

### Step 6: Chiusura Issue (quando risolta)
```bash
gh issue close ISSUE_NUMBER --comment "Implementato in PR #XX"
```

## Convenzioni di Naming

### Titoli Issue
- `[COMPLIANCE] Implementare gestione Codice CIN` - gap normativo
- `[COMPLIANCE] Aggiornare calcolo ritenuta OTA al 21%` - aggiornamento
- `[COMPLIANCE] Verificare conformita' GDPR consenso ospiti` - verifica

### Label
- `regulatory` - requisito normativo
- `compliance` - gap di conformita'
- `priority:critical` - obbligo in vigore, sanzioni immediate
- `priority:high` - obbligo in vigore, sanzioni previste
- `priority:medium` - rischio moderato
- `priority:low` - best practice o obbligo futuro

## Limiti e Precauzioni
- Massimo 10 issue per esecuzione
- Non creare mai issue duplicate
- Verifica sempre l'autenticazione `gh` prima di procedere
- Se la creazione fallisce, logga l'errore e continua con le successive
- Non includere mai credenziali o dati sensibili nel body delle issue

## Gestione Errori
```bash
# Se gh non e' autenticato
if ! gh auth status &>/dev/null; then
  echo "ERRORE: gh CLI non autenticata. Eseguire 'gh auth login'"
  exit 1
fi

# Se la creazione fallisce
if ! gh issue create --title "..." --body "..." 2>/tmp/gh_error.log; then
  echo "ERRORE nella creazione issue: $(cat /tmp/gh_error.log)"
fi
```
