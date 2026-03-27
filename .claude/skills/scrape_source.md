# Skill: Scrape Source - Scraping Sorgenti Istituzionali

## Descrizione
Questa skill descrive come fare scraping di sorgenti istituzionali italiane ed europee per estrarre informazioni normative rilevanti usando gli strumenti di Claude Code.

## Quando Usarla
- Quando devi raccogliere il testo di una legge, decreto o regolamento
- Quando devi verificare aggiornamenti su siti istituzionali
- Quando devi estrarre contenuto strutturato da pagine web normative

## Sorgenti Supportate

### Sorgenti Italiane
| Sorgente | URL Base | Note |
|----------|----------|------|
| Gazzetta Ufficiale | gazzettaufficiale.it | Testi di legge ufficiali |
| Agenzia delle Entrate | agenziaentrate.gov.it | Circolari, risoluzioni, guide |
| Ministero del Turismo | ministeroturismo.gov.it | Normativa turistica, CIN |
| Normattiva | normattiva.it | Testi coordinati delle leggi |
| BDSR | bdsr.ministeroturismo.it | Banca Dati Strutture Ricettive |

### Sorgenti Europee
| Sorgente | URL Base | Note |
|----------|----------|------|
| EUR-Lex | eur-lex.europa.eu | Direttive e regolamenti UE |
| European Commission | ec.europa.eu | Proposte e comunicazioni |

## Procedura

### Step 1: Ricerca
```
WebSearch("termine di ricerca site:dominio.gov.it")
```
Usa termini specifici e limita al dominio istituzionale.

### Step 2: Fetch
```
WebFetch(url, "Estrai il testo normativo principale, inclusi: titolo completo, numero e data del provvedimento, articoli rilevanti, date di entrata in vigore, eventuali sanzioni previste")
```

### Step 3: Validazione
Verifica che il contenuto estratto contenga:
- [x] Riferimento normativo completo (es. "D.L. 145/2023, art. 13-ter")
- [x] Data di pubblicazione/entrata in vigore
- [x] Testo degli articoli rilevanti
- [x] Eventuali modifiche successive

### Step 4: Strutturazione
Organizza il contenuto estratto in questo formato:

```markdown
# [Titolo del Provvedimento]

- **Tipo**: Decreto Legge / Legge / Direttiva UE / Circolare
- **Numero**: [numero/anno]
- **Data pubblicazione**: [data]
- **Data entrata in vigore**: [data]
- **Sorgente**: [URL]
- **Data consultazione**: [data odierna]

## Sintesi
[breve riassunto dell'obbligo]

## Articoli Rilevanti
### Art. [N] - [Titolo]
[testo o sintesi dell'articolo]

## Impatto su CasaZen
[descrizione di come questo provvedimento impatta il sistema]

## Sanzioni
[eventuali sanzioni per non conformita']
```

## Gestione Errori
- Se la pagina non e' raggiungibile: annota nel report e prova una sorgente alternativa
- Se il contenuto e' un PDF: usa `WebFetch` con prompt specifico per estrarre il testo
- Se il contenuto e' troppo lungo: chiedi di estrarre solo le sezioni rilevanti per gli affitti brevi

## Best Practice
- Preferisci sempre `normattiva.it` per i testi coordinati delle leggi (includono modifiche successive)
- Per le circolari dell'Agenzia delle Entrate, cerca anche le FAQ correlate
- Salva sempre la data di consultazione per tracciabilita'
- Non fidarti di sorgenti non istituzionali per il testo esatto della legge
