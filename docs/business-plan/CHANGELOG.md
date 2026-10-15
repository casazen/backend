# Changelog del business plan

Formato: una voce per versione, dalla più recente. La versione corrente è anche nell'intestazione di `BUSINESS-PLAN.md`.

## 1.1.0 — 2026-10-15

Listino definitivo B2C e B2B dopo la ricerca di mercato e il calcolo dei costi (`LISTINO.md`, generato da
`scripts/business-plan/listino.py`); modello finanziario riallineato al listino.

- B2C: piano **Free** (1 proprietà, marketplace); Starter € 29 (3 proprietà); **Pro € 79 con 10 proprietà incluse + € 8 per
  unità fino a 50 e channel manager via API incluso**; **Scale € 199 con 25 incluse + € 7 per unità fino a 100**; annuale −10 %.
  Prima: Pro 50 proprietà e Scale illimitate a prezzo fisso.
- B2B: Base **€ 10 / 9 / 8** per unità (prima € 9 / 8 / 7), AI e Marketing +€ 4, tutto compreso € 15 / 14 / 13 con **6**
  verifiche per unità al mese (prima 8), verifica in più € 0,75 (NFC +€ 0,20), serratura € 7 (prima € 6), minimo € 100 (prima € 90).
- Modello finanziario: ARPU B2C pagante € 54 / 60 / 65 per scenario, host Free e loro volume marketplace, channel
  manager incluso in Pro/Scale, verifiche incluse per le agenzie a listino, costi per host e per unità dal listino.
  Scenario Base a 36 mesi: MRR € 43.315, ARR € 519.778, risultato cumulato € 323.262.
- Confronto con tredici concorrenti (Kross, Vikey, Chekin, Beds24, Hospitable, Turno letti sulle pagine ufficiali;
  Smoobu, Lodgify, Guesty, Hostaway, Avantio, Octorate, Uplisting da fonti terze 2026).
- Regola di versione: un cambio di listino è `MINOR`; `MAJOR` resta per il modello di business e la strategia.

## 1.0.0 — 2026-10-15

Prima versione completa, scritta per la presentazione a investitori, partner e potenziali soci.

- Modello di business: sistema che si auto-alimenta tra host, agenzie e fornitori; affitti brevi e lunghi; B2B e B2C.
- Monetizzazione: abbonamenti, zero commissioni sulle prenotazioni e sui canoni, 10 % sugli interventi dei fornitori pagato
  dal cliente in cambio della Garanzia CasaZen.
- Roadmap: 6 mesi (Founding Partnership Program con Quokka 360, costituzione della società, percorso verso la produzione),
  12 mesi (rete fornitori, onboarding B2C, almeno altri 2 B2B), 24 mesi (consolidamento e crescita), 36 mesi (founder a
  tempo pieno e prime assunzioni).
- Modello finanziario a 36 mesi in tre scenari, generato da `scripts/business-plan/modello_finanziario.py`.
- Verifica di mercato dei piani B2C (`VERIFICA-PIANI-B2C.md`): prezzi in linea, limiti di Pro (50) e Scale (illimitate)
  fuori mercato; proposta di proprietà incluse più prezzo per unità in più, decisione aperta.
- Fonti: analisi di mercato 2026, analisi del prospect Quokka 360 (v4, 2026-10-15), stato verificato del prodotto al
  2026-10-02 (registro delle spec e riepilogo del risanamento), runbook dei fornitori e del billing.
