# Stato del risanamento (snapshot del 2026-10-02)

Copia dello stato dell'orchestrazione, per poter riprendere il lavoro da un'altra sessione o in locale. Ultimo aggiornamento: task FN-05 (stato finale: 163 task `done` su 166, 3 `partial`: RS-7, CO-13, CO-22; i 12 task `AI-*` sono `pending`).

| File | Contenuto |
|---|---|
| `tasks.json` | Registro dei task (id, titolo, difetti, repo, dipendenze); copia identica in `../tasks.json` |
| `status.json` | Stato di ogni task (done / running / pending / partial) e dei task aggiunti in corso d'opera (`PL14-SUBP`, `LEGAL-TEXTS`, `FIX-DEPLOYCHK`) |
| `notes.log` | Esiti dei task e note dell'orchestratore (SHA dei commit, esiti parziali) |
| `findings.json` | I 335 difetti dell'audit (id, severità, titolo) |
| `tooling/` | Script usati per worktree, integrazione e scheduling (`start-task.sh`, `integrate.sh`, `finish-task.sh`, `heavy.sh`, `sched.py`, `env.sh`) |

Stato per difetto: `../../PIANO-RISANAMENTO-2026-09.md` § 9. Riepilogo e procedura di rilascio: `../RIEPILOGO-FINALE.md`.

## Riprendere

1. Copia `tooling/` in `wt/bin/`. Aggiorna `INT_BRANCH` e i percorsi in `env.sh` e i percorsi in `sched.py`.
2. `sched.py ready` elenca i task con le dipendenze soddisfatte.
3. Ogni agente segue `../AGENT-PROTOCOL.md` più gli override di sessione: un task per volta, in un worktree dedicato, con integrazione sul branch di integrazione corrente (`claude/sleepy-edison-oil8ru` al 2026-10-02; il precedente `claude/app-analysis-fixes-plan-p0mx0a` è già su `develop`).

`notes.log` è normalmente ignorato da git (`*.log`): il `.gitignore` ha un'eccezione per questo file.
