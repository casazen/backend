# Stato del risanamento (snapshot del 2026-10-02)

Copia dello stato dell'orchestrazione, per poter riprendere il lavoro da un'altra sessione o in locale. Ultimo aggiornamento: task FN-05 (stato finale: 163 task `done` su 166, 3 `partial`: RS-7, CO-13, CO-22).

| File | Contenuto |
|---|---|
| `tasks.json` | Registro dei task (id, titolo, difetti, repo, dipendenze); copia identica in `../tasks.json` |
| `status.json` | Stato di ogni task (done / running / pending / partial) e dei task aggiunti in corso d'opera (`PL14-SUBP`, `LEGAL-TEXTS`, `FIX-DEPLOYCHK`) |
| `notes.log` | Esiti dei task e note dell'orchestratore (SHA dei commit, esiti parziali) |
| `findings.json` | I 335 difetti dell'audit (id, severità, titolo) |

Stato per difetto: `../../PIANO-RISANAMENTO-2026-09.md` § 9. Riepilogo e procedura di rilascio: `../RIEPILOGO-FINALE.md`.

## Consultazione storica

Questo snapshot documenta il risanamento del 2 ottobre. Gli script di orchestrazione con percorsi locali e branch ritirati sono stati rimossi; il planning attivo è `Sessions/PLANNING.md`, dedicato al pilota Quokka. Non riprendere automaticamente i vecchi task.

`notes.log` è normalmente ignorato da git (`*.log`): il `.gitignore` ha un'eccezione per questo file.
