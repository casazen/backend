# Stato del risanamento (snapshot)

Copia dello stato dell'orchestrazione, per poter riprendere il lavoro da un'altra sessione o in locale.

| File | Contenuto |
|---|---|
| `tasks.json` | Registro dei task (id, titolo, difetti, repo, dipendenze) |
| `status.json` | Stato di ogni task (done / running / pending / blocked / partial) e note |
| `notes.log` | Esiti dei task, dubbi raccolti e note per i task successivi |
| `findings.json` | I 335 difetti dell'audit (id, severità, titolo) |
| `tooling/` | Script usati per worktree, integrazione e scheduling (`start-task.sh`, `integrate.sh`, `finish-task.sh`, `sched.py`, `env.sh`) |

## Riprendere

1. Copia `tooling/` in `wt/bin/`. Aggiorna `INT_BRANCH` e i percorsi in `env.sh` e i percorsi in `sched.py`.
2. `sched.py ready` elenca i task con le dipendenze soddisfatte.
3. Ogni agente segue `../AGENT-PROTOCOL.md`: un task per volta, in un worktree dedicato, con integrazione sul branch `claude/app-analysis-fixes-plan-p0mx0a`.

I task marcati `running` nello snapshot erano in corso al momento della copia. I loro worktree non sono inclusi: vanno ripresi o rifatti.
