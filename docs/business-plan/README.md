# CasaZen: business plan

Questa cartella contiene il business plan di CasaZen e i documenti che lo accompagnano. È scritta in prima persona
dal founder ed è pensata per essere **versionata in git** e **presentata** a investitori, partner e potenziali soci.

| File | Cosa contiene | Per chi |
|---|---|---|
| [`BUSINESS-PLAN.md`](./BUSINESS-PLAN.md) | Il piano completo: modello di business, monetizzazione, mercato, stato del prodotto, roadmap a 6/12/24/36 mesi, piano finanziario, società, rischi, richiesta | Tutti i destinatari |
| [`LISTINO.md`](./LISTINO.md) | Listino B2C e B2B con costi fissi, costi unitari delle integrazioni, margini per piano e confronto con tredici concorrenti, **generato** da `scripts/business-plan/listino.py` | Tutti i destinatari |
| [`MODELLO-FINANZIARIO.md`](./MODELLO-FINANZIARIO.md) | Proiezioni a 36 mesi in tre scenari, **generate** da `scripts/business-plan/modello_finanziario.py` | Investitori, soci |
| [`modello-finanziario.csv`](./modello-finanziario.csv) | Serie mensili complete dei tre scenari (separatore `;`), apribile in Excel o Google Sheets | Chi vuole rifare i conti |
| [`VERIFICA-PIANI-B2C.md`](./VERIFICA-PIANI-B2C.md) | Verifica di mercato dei limiti iniziali dei piani B2C (3 / 50 / illimitate) sui listini 2026 dei concorrenti; superata dal listino 1.1.0 | Founder, soci |
| [`PITCH.md`](./PITCH.md) | La presentazione, slide per slide, con le note di quello che dico | Per presentare dal vivo |
| [`CHANGELOG.md`](./CHANGELOG.md) | Storico delle versioni del piano | Tutti |

## Come è versionato

- Il piano ha una versione **semantica** (`MAJOR.MINOR.PATCH`) scritta nell'intestazione di `BUSINESS-PLAN.md` e in `CHANGELOG.md`:
  - `MAJOR`: cambia il modello di business o la strategia (es. una nuova linea di ricavo, una commissione dove non c'era);
  - `MINOR`: cambia il listino, le proiezioni o la roadmap; nuove sezioni; dati reali al posto delle stime;
  - `PATCH`: correzioni di testo e di forma.
- Ogni versione presentata a qualcuno viene **taggata** in git, così si sa sempre cosa ha visto chi:
  `git tag -a business-plan/v1.0.0 -m "Business plan v1.0.0"` e `git push origin business-plan/v1.0.0`.
- Le modifiche seguono il flusso del repository (branch → pull request verso `develop`): la storia del piano è la storia di git.
- Le cifre di ricavi, costi e cassa **non si scrivono a mano**: si cambiano le ipotesi nello script e si rigenerano
  `MODELLO-FINANZIARIO.md` e il CSV nello stesso commit:

  ```bash
  python3 scripts/business-plan/listino.py              # LISTINO.md: prezzi, costi, margini
  python3 scripts/business-plan/modello_finanziario.py  # MODELLO-FINANZIARIO.md e CSV
  ```

## Come lo presento

- **Su GitHub** i file Markdown si leggono già impaginati (tabelle e diagrammi Mermaid inclusi).
- **In HTML autonomo** (un file per documento, con indice, pronto da inviare o da stampare in PDF dal browser):

  ```bash
  scripts/business-plan/export.sh      # scrive docs/business-plan/out/*.html (cartella ignorata da git)
  ```

- **Dal vivo**: `PITCH.md` è la scaletta; ogni slide ha il titolo, i punti e le note che leggo.

Cosa mostrare a chi:

| Destinatario | Sezioni del piano da cui partire |
|---|---|
| Investitore | 1 (in una pagina), 4-5 (modello e monetizzazione), 6 (mercato), 11 (piano finanziario), 16 (cosa chiedo); `LISTINO.md` per i margini |
| Partner (agenzia, fornitore di tecnologia) | 3 (soluzione), 5.5 (Founding Partner), 8 (stato del prodotto), 9 (roadmap) |
| Potenziale socio | 8 (stato del prodotto), 9 (roadmap), 12 (organizzazione), 13 (società), 16 (cosa chiedo) |

## Riservatezza

Il piano contiene prezzi, margini e condizioni dei partner: è confidenziale. La cartella vive in un repository privato;
chi riceve l'export HTML o PDF lo riceve con la versione e la data in testa al documento.
