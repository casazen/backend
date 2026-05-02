# Processo di Code Review Standard

> **Riutilizzabile**: Questo processo è condiviso da tutti i workflow che richiedono code review.

## Obiettivo
Validare PR tramite `@code_reviewer` con ciclo iterativo controllato (max 3 iterazioni).

## Processo

### 1. Avvia Review Iniziale

Quando le PR sono aperte, invoca `@code_reviewer` per verificare:

- ✅ Correttezza logica rispetto alle issue
- ✅ Qualità del codice (SOLID, async patterns, testing)
- ✅ Coerenza tra frontend e backend (se applicabile)
- ✅ Compatibilità contratto API (se applicabile)
- ✅ Assenza di regressioni evidenti
- ✅ Problemi di sicurezza basilari (SQL injection, XSS, secrets)

**Riferimenti standard**:
- `REVIEW.md` - Linee guida di review
- `CLAUDE.md` - Standard di progetto
- `.claude/rules/` - Regole specifiche (security, code-style, etc.)

**Output atteso**: Elenco di finding categorizzati per severità:
- 🔴 **Critical**: MUST fix (security, compliance, deadlock)
- 🟡 **High**: SHOULD fix (missing tests, SOLID violations)
- 🟢 **Medium**: Consider fixing (duplication, complexity)
- ⚪ **Low**: Optional (style, naming)

### 2. Gestione Feedback

**Se review è APPROVED**:
- ✅ Considera la PR valida
- ✅ Procedi con merge (tramite `@release_manager`)

**Se review ha Required Changes**:
1. Inoltra finding a `@feature_developer`
2. Chiedi di applicare **solo le modifiche richieste**, senza refactor non necessari
3. Dopo l'aggiornamento, chiedi a `@code_reviewer` di **rivedere solo**:
   - Le modifiche appena applicate
   - I punti precedentemente segnalati
   - **NON** riesaminare parti già approvate (salvo modifiche)

### 3. Limite Iterazioni (Anti-Loop)

**Massimo 3 iterazioni** per PR.

**Se dopo 3 iterazioni la PR non è approvata**:
1. ❌ Interrompi il ciclo automaticamente
2. 📋 Produci **report di escalation** con:
   - Problemi residui (elenco finding non risolti)
   - Storico delle 3 iterazioni (cosa è stato risolto, cosa no)
   - Raccomandazioni per risoluzione manuale
3. 🚫 **NON** continuare oltre il limite

### 4. Regole Operative

- ✅ Ragiona sempre sulle **modifiche delta**, non riesaminare l'intera codebase
- ✅ Mantieni tracciabilità: quale finding → quale commit/modifica
- ❌ Non inventare nuovi requisiti durante la review
- ❌ Non riaprire discussioni su parti già approvate
- ⚠️ In caso di ambiguità, **segnala** e chiedi decisione esplicita

## Output Standard

Ogni iterazione di review produce:

```markdown
## Review Iteration N/3

**PR**: <link>
**Reviewer**: @code_reviewer
**Status**: APPROVED | CHANGES_REQUESTED | ESCALATION_REQUIRED

### Findings
- 🔴 [Critical] <descrizione> → Commit <hash>
- 🟡 [High] <descrizione> → Commit <hash>
- ...

### Action
- [x] Finding #1 risolto in commit abc123
- [ ] Finding #2 da risolvere
- ...

**Next Step**: Iteration N+1 | APPROVED | ESCALATION
```

## Integrazione con GitHub Flow

Questo processo si inserisce nel flusso standard:

```
1. Feature branch creato
2. Implementazione completata
3. PR aperta
4. **→ REVIEW PROCESS (questo documento)** ←
5. PR approvata
6. Merge a main (solo @release_manager)
```

---

**Usato da**:
- `feature-implementation.md` - Implementazione feature da issue
- `compliance-feature-creation.md` - Feature compliance-driven
- Qualsiasi workflow che apre PR

**Last Updated**: 2026-05-01
