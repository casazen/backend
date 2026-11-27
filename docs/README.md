# docs/ — cosa resta qui e dove sta il resto

> Verificato il 2026-11-27 su `develop@232b21fb`. Documento di indirizzamento: **non aggiungere altri `.md` in `docs/`** (vedi `AGENTS.md`, sezione «Documentazione»).

La documentazione di progetto (requisiti, punti aperti, decisioni, revisioni legali e fiscali, ADR, integrazioni, specifiche, report) sta nella cartella **`documentazione/` del workspace CasaZen** (la cartella che contiene `backend/`, `frontend/` e `mobile/`), **fuori dai repository** di proposito: questo repository è pubblico.

## Cosa resta in `docs/` e perché

| Percorso | Perché resta nel repository |
|---|---|
| `runbooks/` (indice: `runbooks/index.md`) | molti test C# li leggono (`DeployChecklistConsistencyTests`, `GlobalSearchRunbookTests`, `LeaseDraftRunbookTests`, `PropertyModeRunbookTests`, `InAppNotificationsRunbookTests`, …) e i messaggi di errore a runtime citano `docs/runbooks/<nome>.md` |
| `TECHNICAL.md` (riferimento API e dati, molto esteso) | tre test ne controllano alcune stringhe; non è verificabile per intero |
| `INFRA.md`, `AUTH0_SETUP.md` | citati da molti runbook; `INFRA.md` è **da rivedere** (vedi la nota in testa) |
| `compliance/guest-registration-switzerland.md` | letto da `GuestRegistrationRegimeCatalogTests` (elenca ogni regime del catalogo) |
| `legal-drafts/leases/` | letto da `LeaseTemplateDraftsTests` (bozze AI «non approvate» dei contratti) |

## Dove sono finiti i vecchi percorsi (riassunto)

Mappa completa, file per file: `documentazione/99-archivio/MAPPA-SPOSTAMENTI.md`. I file rimossi si recuperano dalla storia git: `git show 232b21fb:<vecchio percorso>`.

| Vecchio percorso | Nuova posizione in `documentazione/` |
|---|---|
| `docs/adr/` | `03-documenti-ai/adr/` |
| `docs/requirements/`, `docs/BUSINESS.md`, `docs/AI-STRATEGY.md`, `Sessions/prospect-quokka360/`, `Sessions/business-analysis-canone-concordato.md`, `Sessions/specs/`, `Sessions/ai-oriented-discoverability-2026-10-21.md` | `03-documenti-ai/requisiti/specifiche/` (e lo stato verificato per area in `03-documenti-ai/requisiti/`) |
| `docs/business-plan/`, `scripts/business-plan/` | `01-documenti-ufficiali/business-plan/` e `listino-prezzi/` |
| `.claude/context/regulations/`, `Sessions/research-canone-concordato-mb.md` | `03-documenti-ai/revisioni/normativa/` (revisioni in `revisioni/legali.md` e `commercialista.md`) |
| `docs/legal/` (bozze DPA, privacy, ToS) | `03-documenti-ai/revisioni/bozze-legali/` |
| `docs/integrations/`, `docs/implementation/` | `03-documenti-ai/integrazioni/` (ConcordatoFacile: `concordatofacile-processo.md`; specifica inviata a CF: `99-archivio/concordatofacile/da-inviare/`) |
| `docs/compliance/` (tranne la pagina Svizzera) | `03-documenti-ai/integrazioni/fonti/svizzera/` |
| `docs/PROJECT.md`, `docs/index.md`, `docs/technical/`, `docs/FRONTEND-*.md` | `AGENTS.md` e `README.md` del repository |
| `Sessions/` (audit, risanamento, planning, council, round, runbook manuali) e `councils/` | rimossi: la sintesi è in `03-documenti-ai/{punti-aperti.md,decisioni.md,consegne/,requisiti/}` |
