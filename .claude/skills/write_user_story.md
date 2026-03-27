# Skill: Write User Story - Generazione User Story Ben Formate

## Descrizione
Questa skill descrive come scrivere user story ben strutturate a partire da requisiti normativi o gap analysis. Le user story prodotte sono pronte per diventare issue GitHub.

> **Riusabile cross-project**: questa skill e' generica e puo' essere usata in qualsiasi progetto.

## Quando Usarla
- Quando l'`analyzer_agent` ha identificato un gap normativo
- Quando bisogna trasformare un requisito tecnico in un task comprensibile
- Quando si creano issue GitHub dal `github_agent`

## Formato User Story

### Template Base
```
Come [RUOLO],
voglio [AZIONE/FUNZIONALITA'],
in modo da [BENEFICIO/VALORE].
```

### Ruoli Comuni (CasaZen)
- **proprietario** - gestisce proprieta' e affitti
- **ospite** - prenota e soggiorna
- **amministratore** - gestisce il sistema
- **sistema** - operazioni automatiche

### Template Completo per Issue

```markdown
## User Story
Come **[ruolo]**, voglio **[azione]**, in modo da **[beneficio]**.

## Contesto
[Perche' questa funzionalita' e' necessaria. Riferimento normativo se applicabile.]

## Requisiti Funzionali
- [ ] [requisito 1]
- [ ] [requisito 2]
- [ ] [requisito N]

## Requisiti Non Funzionali
- [ ] [performance, sicurezza, etc.]

## Acceptance Criteria
- [ ] GIVEN [precondizione], WHEN [azione], THEN [risultato atteso]
- [ ] GIVEN [precondizione], WHEN [azione], THEN [risultato atteso]

## Note Tecniche
[suggerimenti implementativi, file da modificare, pattern da seguire]

## Riferimenti
- [link normativa]
- [link documentazione]
```

## Regole di Scrittura

### DO
- Scrivi dal punto di vista dell'utente, non del sistema
- Usa un linguaggio chiaro e non ambiguo
- Includi sempre acceptance criteria verificabili
- Specifica il contesto normativo quando applicabile
- Indica la priorita' suggerita

### DON'T
- Non usare gergo tecnico nella user story (riservalo alle note tecniche)
- Non combinare piu' funzionalita' in una sola story (principio INVEST - Independent)
- Non scrivere story troppo vaghe ("migliorare la compliance")
- Non omettere i criteri di accettazione

## Esempio Completo

**Input**: Gap CRITICAL - Manca la gestione del Codice CIN nelle proprieta'

**Output**:
```markdown
## User Story
Come **proprietario**, voglio **registrare e gestire il Codice CIN delle mie proprieta'**, in modo da **essere conforme all'obbligo normativo ed evitare sanzioni da 800 a 8.000 euro**.

## Contesto
Il D.L. 145/2023 (art. 13-ter) ha introdotto l'obbligo del Codice Identificativo Nazionale (CIN)
per tutte le strutture destinate a locazioni brevi. Il CIN deve essere:
- Ottenuto tramite la BDSR (Banca Dati Strutture Ricettive)
- Esposto negli annunci su tutte le piattaforme OTA
- Esposto fisicamente all'esterno dell'immobile

## Requisiti Funzionali
- [ ] Campo CIN nell'entita' Property (formato: IT-XXXXX-XXXXXXXXXX)
- [ ] Validazione formato CIN
- [ ] Visualizzazione CIN nella scheda proprieta'
- [ ] Inclusione CIN nei dati sincronizzati con le OTA
- [ ] Alert per proprieta' senza CIN registrato

## Requisiti Non Funzionali
- [ ] Il CIN deve essere cifrato a riposo nel database
- [ ] Log di audit per modifiche al CIN

## Acceptance Criteria
- [ ] GIVEN una proprieta' senza CIN, WHEN accedo alla dashboard, THEN vedo un alert di non conformita'
- [ ] GIVEN un CIN valido, WHEN lo inserisco nella proprieta', THEN viene salvato e mostrato nella scheda
- [ ] GIVEN una proprieta' con CIN, WHEN sincronizzo con una OTA, THEN il CIN e' incluso nei dati

## Note Tecniche
- Aggiungere campo `CinCode` all'entita' `Property` in `Casazen.Core/Entities/`
- Creare migration EF Core per la colonna
- Aggiornare gli adapter OTA in `Casazen.Infrastructure/OTA/` per includere il CIN
- Aggiungere validazione con regex per il formato CIN

## Riferimenti
- D.L. 145/2023, art. 13-ter
- https://bdsr.ministeroturismo.it
```

## Dimensionamento
- Una user story dovrebbe essere completabile in 1-5 giorni di sviluppo
- Se e' piu' grande, spezzala in piu' story (epic -> stories)
- Indica se la story fa parte di un epic piu' ampio
