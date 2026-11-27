# docs/ — indirizzamento

> Verificato il 2026-11-27 su `develop@82cb00ac`. Questa cartella contiene **solo questo file**: non aggiungere altri `.md` in `docs/` (vedi `AGENTS.md`, sezione «Documentazione»).

Tutta la documentazione di progetto sta nella cartella **`documentazione/` del workspace CasaZen** (la cartella che contiene `backend/`, `frontend/` e `mobile/`), **fuori dai repository**: unica fonte di verità. Dal 2026-11-27 ci sono anche i runbook, il riferimento esteso e le guide di infrastruttura; nessun test C# li legge più.

| Cercavi | Ora è in `documentazione/` |
|---|---|
| `docs/runbooks/*.md` (89 runbook; l'indice `index.md` era obsoleto) | `03-documenti-ai/runbook/backend/` (indice con argomento e dimensione in `03-documenti-ai/runbook/README.md`) |
| `docs/TECHNICAL.md`, `docs/INFRA.md`, `docs/AUTH0_SETUP.md` | `03-documenti-ai/runbook/backend/` |
| `docs/compliance/guest-registration-switzerland.md` | `03-documenti-ai/integrazioni/fonti/svizzera/` |
| `docs/legal-drafts/leases/` | `03-documenti-ai/revisioni/bozze-legali/contratti/` |
| `docs/adr/` | `03-documenti-ai/adr/` |
| `docs/requirements/`, `docs/BUSINESS.md`, `docs/AI-STRATEGY.md`, `Sessions/specs`, … | `03-documenti-ai/requisiti/specifiche/` (e lo stato verificato per area in `03-documenti-ai/requisiti/`) |
| `.claude/context/regulations/`, `Sessions/research-canone-concordato-mb.md` | `03-documenti-ai/revisioni/normativa/` |
| `docs/legal/` (bozze DPA, privacy, ToS) | `03-documenti-ai/revisioni/bozze-legali/` |
| `docs/integrations/`, `docs/implementation/` | `03-documenti-ai/integrazioni/` |
| `docs/PROJECT.md`, `docs/index.md`, `docs/technical/`, `docs/FRONTEND-*.md` | `AGENTS.md` e `README.md` del repository |
| `Sessions/` e `councils/` | rimossi: sintesi in `03-documenti-ai/{punti-aperti.md,decisioni.md,consegne/,requisiti/}` |

Mappa completa, file per file: `documentazione/99-archivio/MAPPA-SPOSTAMENTI.md`. I messaggi di errore e i commenti del codice che citano `docs/runbooks/<nome>.md` indicano il vecchio percorso: il file è ora in `documentazione/03-documenti-ai/runbook/backend/<nome>.md`.
