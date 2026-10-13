# Indice del contesto normativo

Carica **un solo file** per volta, quello pertinente al task (regola in `.claude/rules/compliance.md`).

Legenda dei livelli di verifica usata nelle sezioni "verificate": **U** fonte ufficiale, **D** dedotto, **T** terza parte (solo indizio).

| File | Argomento | Norma principale | Stato della verifica | Task collegati |
|---|---|---|---|---|
| `alloggiati.md` | Comunicazione degli ospiti alla Questura (Alloggiati Web): tracciato, tabelle, web service | Art. 109 TULPS; D.M. 07/01/2013 modificato dal D.M. 16/09/2021 | Specifiche tecniche verificate da RS-1 (2026-09-23), in parte D e T | CO-12, CO-13 |
| `canone_concordato.md` | Locazioni a canone concordato (lungo termine) | L. 431/1998 art. 2 c. 3; D.M. 16/01/2017 | Accordo Monza e Brianza verificato da RS-8, fiscale da RS-5 (2026-09-23) | LT-04, LT-08, LT-10, LT-13 |
| `cin.md` | Codice Identificativo Nazionale: formato, esposizione, sanzioni; codici regionali in generale | D.L. 145/2023 art. 13-ter | Formato verificato da RS-2 (2026-09-23) | CO-01, CO-20 |
| `cir-lombardia.md` | CIR lombardo: norme, percorso SUAP → Ross1000, formato, rapporto con il CIN, esposizione, sanzioni | L.R. Lombardia 27/2015 art. 38 c. 8-bis e 8-ter; L.R. 7/2018; D.G.R. XII/169 | Ricerca RS-9 (2026-09-24), estratti ufficiali | SU-04, CO-22, #8 |
| `fiscale.md` | Regime fiscale di affitti brevi e lunghi e billing SaaS | D.L. 50/2017 art. 4; L. 178/2020; L. 199/2025 | Regole verificate da RS-5 (2026-09-23) | PL-13, CO-18, LT-04, LT-08 |
| `gdpr.md` | Protezione dei dati degli ospiti | Reg. (UE) 2016/679; D.Lgs. 196/2003 | Non riverificato nel risanamento | — |
| `imposta_soggiorno.md` | Imposta di soggiorno e tariffe dei comuni pilota | D.Lgs. 23/2011 art. 4 | Tariffe verificate da RS-7 (2026-09-23) | CO-03, BK-03 |
| `istat-flussi-turistici.md` | Flussi turistici ISTAT (movimento clienti): obbligo, canale Ross1000, scadenze, tracciato XML e web service, regione pilota Lombardia | Reg. (UE) 692/2011; PSN IST-00139; D.Lgs. 322/1989; L.R. Lombardia 27/2015 art. 38 c. 8 e art. 40 c. 9 | Ricerca RS-9 (2026-09-24): obbligo U; dettagli del tracciato in parte T | CO-22, #6 |
| `ota_normativa.md` | DAC7 e Reg. (UE) 2024/1028 (obblighi di OTA e intermediari) | Dir. (UE) 2021/514; D.Lgs. 32/2023 | Non riverificato nel risanamento | — |
| `regionale.md` | Panoramica delle norme regionali | Leggi regionali varie | **Non verificato**, tranne la Lombardia, che rimanda a `cir-lombardia.md` e `istat-flussi-turistici.md` | #8 |
| `sicurezza.md` | Dispositivi di sicurezza e requisiti strutturali | D.L. 145/2023 art. 13-ter c. 7 | Obblighi verificati da RS-3 (2026-09-23) | CO-07 |

## Quale file leggere

- **Formato o validazione del CIN**: `cin.md`. **Codice regionale lombardo (CIR)**: `cir-lombardia.md`.
- **Questura / schedine ospiti**: `alloggiati.md`. **Statistica ISTAT / Ross1000 / flussi mensili**: `istat-flussi-turistici.md`. Sono obblighi distinti con canali distinti.
- **Tassa di soggiorno**: `imposta_soggiorno.md`. Le tariffe stanno nel DB (`TouristTaxRate`), mai nel codice.
- **Obblighi di una regione**: `regionale.md`, ma solo come panoramica; per la Lombardia i due file RS-9.
