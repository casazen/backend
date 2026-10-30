# CasaZen: modello finanziario a 36 mesi

> **Documento generato**: non modificare a mano. Sorgente e ipotesi in `scripts/business-plan/modello_finanziario.py`;
> serie mensili complete in `modello-finanziario.csv`. Rigenerare con `python3 scripts/business-plan/modello_finanziario.py`.
> Orizzonte: M1 = novembre 2026, M36 = ottobre 2029. Importi in euro, IVA esclusa. Tutte le cifre sono **stime** [STIMA];
> le fonti dei prezzi e dei costi unitari sono nel business plan (§ 5 e Appendice A).

## 1. Cosa misura il modello

- **Ricavi B2B**: canoni delle agenzie. I founding partner pagano un canone fisso (costo documentato + € 1/unità);
  le agenzie a listino pagano il tutto compreso per unità. Le verifiche d'identità sono rifatturate al costo e restano fuori.
- **Ricavi B2C**: abbonamenti degli host paganti (Starter € 29, Pro € 79 + unità in più, Scale € 199 + unità in più;
  ARPU medio ponderato). Gli host del piano Free non pagano: contano nei costi e nel volume del marketplace.
- **Ricavi marketplace**: 10 % sul valore degli interventi dei fornitori pagati tramite CasaZen, a carico del cliente.
  Mai una commissione sulle prenotazioni o sui canoni di locazione.
- **Costi**: piattaforma (hosting, email, channel manager, costi per unità, Stripe sulle nostre fatture, strumenti di sviluppo),
  accantonamento garanzia (1,5 % del volume marketplace per rimborsi e controversie), marketing, amministrazione, una tantum.
- **Il founder non percepisce compenso fino al mese 36** (lavoro part-time, sviluppo assistito da AI). La struttura
  con founder a tempo pieno e prime assunzioni è nella sezione 6.

## 2. Ipotesi per scenario

| Ipotesi | Prudente | Base | Ambizioso |
|---|---|---|---|
| Go-live Quokka 360 (250 unità, canone partner € 1.730) | M8 (2027-06) | M6 (2027-04) | M5 (2027-03) |
| Altri founding partner (mese: unità) | M14: 60, M18: 50 | M10: 80, M12: 60 | M8: 100, M10: 80, M12: 60 |
| Nuove agenzie a listino al mese (anno 2 / anno 3) | 0.25 / 0.5 | 0.5 / 1.0 | 1.0 / 2.0 |
| Unità medie per agenzia a listino, prezzo medio per unità | 40, € 15,00 | 45, € 15,00 | 50, € 14,00 |
| Churn mensile agenzie a listino | 1,5 % | 1,0 % | 1,0 % |
| Inizio onboarding B2C | M9 (2027-07) | M7 (2027-05) | M7 (2027-05) |
| Nuovi host paganti al mese (anno 1 / 2 / 3) | 3 / 6 / 12 | 6 / 12 / 25 | 10 / 20 / 40 |
| Churn mensile host B2C | 4,0 % | 3,0 % | 2,5 % |
| ARPU host B2C pagante (listino: Starter € 29, Pro € 79 + extra, Scale € 199 + extra; annuale −10 %) | € 54 | € 60 | € 65 |
| Host del piano Free per ogni host pagante, volume marketplace di un host Free | 4, € 15 | 3, € 30 | 2.5, € 40 |
| Nuovi fornitori attivi al mese (anno 1 / 2 / 3) | 2 / 3 / 5 | 3 / 5 / 8 | 5 / 8 / 12 |
| Host B2C che usano il marketplace (inizio → M24) | 15 % → 35 % | 20 % → 50 % | 25 % → 60 % |
| Volume interventi al mese per host che lo usa | € 150 | € 220 | € 260 |
| Volume interventi al mese per unità B2B | € 5 | € 8 | € 12 |
| Commissione marketplace (pagata dal cliente) | 10 % | 10 % | 10 % |
| Accantonamento garanzia su volume | 1,5 % | 1,5 % | 1,5 % |
| Primo ambiente di produzione | M6 | M4 | M4 |

Costi comuni: channel manager minimo € 350/mese dal go-live del primo partner (50 unità incluse, poi € 3,15/unità);
hosting € 5 prima della produzione, poi € 85 → € 125 → € 185 per anno; email € 20 → € 90; strumenti di sviluppo € 200;
commercialista € 150 + conto/PEC € 30 dal mese di costituzione (M2); costituzione SRL € 2.000 una tantum (M2);
revisione legale e marchio € 2.800 una tantum (M3); pareri fiscali € 100 e RC professionale € 50 dal go-live;
marketing € 150 → € 400 → € 800 dall'inizio del B2C; costi per unità B2B € 1,00 (hosting, email, AI, rendiconto, assistenza)
più € 1,98 di verifiche incluse per le unità delle agenzie a listino; costi per host B2C pagante € 3,10 più il channel
manager via API per il 45 % di host Pro/Scale (9,7 unità medie); host Free € 0,45; Stripe sulle nostre fatture 2 % (B2C) e
0,5 % (B2B). Dettaglio dei costi unitari e dei margini per piano in `LISTINO.md`.

## 3. Riepilogo per anno

### Scenario Prudente

| Voce | Anno 1 (nov 26 - ott 27) | Anno 2 (nov 27 - ott 28) | Anno 3 (nov 28 - ott 29) | Totale 36 mesi |
|---|---:|---:|---:|---:|
| Ricavi B2B (agenzie) | € 8.650 | € 39.920 | € 71.546 | € 120.116 |
| Ricavi B2C (host) | € 1.556 | € 27.582 | € 76.455 | € 105.594 |
| Ricavi marketplace (10 % interventi) | € 874 | € 7.684 | € 19.429 | € 27.987 |
| **Ricavi totali** | € 11.081 | € 75.186 | € 167.431 | € 253.697 |
| Costi piattaforma | € 9.862 | € 38.737 | € 70.669 | € 119.268 |
| Accantonamento garanzia | € 131 | € 1.153 | € 2.914 | € 4.198 |
| Marketing | € 600 | € 4.800 | € 9.600 | € 15.000 |
| Amministrazione e consulenze | € 2.730 | € 3.960 | € 3.960 | € 10.650 |
| Una tantum (società, legale, marchio) | € 4.800 | € 0 | € 0 | € 4.800 |
| **Costi totali** | € 18.123 | € 48.649 | € 87.143 | € 153.916 |
| **Risultato operativo** (founder non retribuito) | -€ 7.042 | € 26.537 | € 80.287 | € 99.781 |
| Volume marketplace transato | € 8.742 | € 76.837 | € 194.290 | € 279.869 |

| A fine anno | Anno 1 | Anno 2 | Anno 3 |
|---|---:|---:|---:|
| MRR (ricavi mensili ricorrenti) | € 2.565 | € 8.746 | € 17.828 |
| ARR (MRR × 12) | € 30.782 | € 104.956 | € 213.938 |
| Agenzie B2B attive | 1 | 6 | 11 |
| Unità gestite dalle agenzie | 250 | 471 | 673 |
| Host B2C paganti | 11 | 65 | 156 |
| Fornitori attivi | 8 | 44 | 104 |
| Cassa cumulata (risultati sommati) | -€ 7.042 | € 19.494 | € 99.781 |

### Scenario Base

| Voce | Anno 1 (nov 26 - ott 27) | Anno 2 (nov 27 - ott 28) | Anno 3 (nov 28 - ott 29) | Totale 36 mesi |
|---|---:|---:|---:|---:|
| Ricavi B2B (agenzie) | € 14.510 | € 59.583 | € 128.097 | € 202.191 |
| Ricavi B2C (host) | € 7.193 | € 70.263 | € 191.548 | € 269.004 |
| Ricavi marketplace (10 % interventi) | € 3.400 | € 26.528 | € 72.601 | € 102.530 |
| **Ricavi totali** | € 25.103 | € 156.375 | € 392.247 | € 573.724 |
| Costi piattaforma | € 15.553 | € 60.101 | € 128.378 | € 204.033 |
| Accantonamento garanzia | € 510 | € 3.979 | € 10.890 | € 15.379 |
| Marketing | € 900 | € 4.800 | € 9.600 | € 15.300 |
| Amministrazione e consulenze | € 3.030 | € 3.960 | € 3.960 | € 10.950 |
| Una tantum (società, legale, marchio) | € 4.800 | € 0 | € 0 | € 4.800 |
| **Costi totali** | € 24.793 | € 72.841 | € 152.828 | € 250.462 |
| **Risultato operativo** (founder non retribuito) | € 310 | € 83.534 | € 239.418 | € 323.262 |
| Volume marketplace transato | € 34.001 | € 265.284 | € 726.012 | € 1.025.297 |

| A fine anno | Anno 1 | Anno 2 | Anno 3 |
|---|---:|---:|---:|
| MRR (ricavi mensili ricorrenti) | € 5.679 | € 18.852 | € 43.315 |
| ARR (MRR × 12) | € 68.146 | € 226.228 | € 519.778 |
| Agenzie B2B attive | 3 | 9 | 19 |
| Unità gestite dalle agenzie | 390 | 646 | 1.128 |
| Host B2C paganti | 33 | 146 | 356 |
| Fornitori attivi | 18 | 78 | 174 |
| Cassa cumulata (risultati sommati) | € 310 | € 83.844 | € 323.262 |

### Scenario Ambizioso

| Voce | Anno 1 (nov 26 - ott 27) | Anno 2 (nov 27 - ott 28) | Anno 3 (nov 28 - ott 29) | Totale 36 mesi |
|---|---:|---:|---:|---:|
| Ricavi B2B (agenzie) | € 20.240 | € 96.447 | € 238.550 | € 355.237 |
| Ricavi B2C (host) | € 13.095 | € 130.102 | € 352.165 | € 495.362 |
| Ricavi marketplace (10 % interventi) | € 7.041 | € 58.141 | € 162.448 | € 227.629 |
| **Ricavi totali** | € 40.376 | € 284.690 | € 753.162 | € 1.078.228 |
| Costi piattaforma | € 20.444 | € 93.806 | € 223.306 | € 337.557 |
| Accantonamento garanzia | € 1.056 | € 8.721 | € 24.367 | € 34.144 |
| Marketing | € 900 | € 4.800 | € 9.600 | € 15.300 |
| Amministrazione e consulenze | € 3.180 | € 3.960 | € 3.960 | € 11.100 |
| Una tantum (società, legale, marchio) | € 4.800 | € 0 | € 0 | € 4.800 |
| **Costi totali** | € 30.380 | € 111.287 | € 261.233 | € 402.901 |
| **Risultato operativo** (founder non retribuito) | € 9.995 | € 173.403 | € 491.928 | € 675.327 |
| Volume marketplace transato | € 70.407 | € 581.412 | € 1.624.475 | € 2.276.293 |

| A fine anno | Anno 1 | Anno 2 | Anno 3 |
|---|---:|---:|---:|
| MRR (ricavi mensili ricorrenti) | € 8.983 | € 35.632 | € 83.937 |
| ARR (MRR × 12) | € 107.799 | € 427.582 | € 1.007.246 |
| Agenzie B2B attive | 4 | 15 | 37 |
| Unità gestite dalle agenzie | 490 | 1.058 | 2.130 |
| Host B2C paganti | 56 | 251 | 605 |
| Fornitori attivi | 30 | 126 | 270 |
| Cassa cumulata (risultati sommati) | € 9.995 | € 183.399 | € 675.327 |

## 4. Le tappe del piano (mese per mese)

### Scenario Prudente

| Mese | M6 (2027-04) | M12 (2027-10) | M18 (2028-04) | M24 (2028-10) | M30 (2029-04) | M36 (2029-10) |
|---|---:|---:|---:|---:|---:|---:|
| Agenzie (partner + listino) | 0 + 0 | 1 + 0 | 3 + 1 | 3 + 3 | 3 + 5 | 3 + 8 |
| Unità B2B | 0 | 250 | 418 | 471 | 577 | 673 |
| Host B2C paganti | 0 | 11 | 41 | 65 | 116 | 156 |
| Host Free | 0 | 45 | 166 | 260 | 464 | 624 |
| Fornitori attivi | 0 | 8 | 26 | 44 | 74 | 104 |
| Volume marketplace nel mese | € 0 | € 2.250 | € 6.253 | € 9.667 | € 15.940 | € 20.920 |
| MRR B2B | € 0 | € 1.730 | € 3.477 | € 4.269 | € 5.859 | € 7.311 |
| MRR B2C | € 0 | € 610 | € 2.237 | € 3.511 | € 6.268 | € 8.425 |
| MRR marketplace | € 0 | € 225 | € 625 | € 967 | € 1.594 | € 2.092 |
| **MRR totale** | € 0 | € 2.565 | € 6.339 | € 8.746 | € 13.720 | € 17.828 |
| Costi del mese | € 465 | € 2.280 | € 4.114 | € 4.958 | € 7.177 | € 8.641 |
| **Risultato del mese** | -€ 465 | € 285 | € 2.225 | € 3.788 | € 6.543 | € 9.187 |
| Cassa cumulata | -€ 7.010 | -€ 7.042 | € 554 | € 19.494 | € 51.023 | € 99.781 |

### Scenario Base

| Mese | M6 (2027-04) | M12 (2027-10) | M18 (2028-04) | M24 (2028-10) | M30 (2029-04) | M36 (2029-10) |
|---|---:|---:|---:|---:|---:|---:|
| Agenzie (partner + listino) | 1 + 0 | 3 + 0 | 3 + 3 | 3 + 6 | 3 + 11 | 3 + 16 |
| Unità B2B | 250 | 390 | 522 | 646 | 894 | 1.128 |
| Host B2C paganti | 0 | 33 | 95 | 146 | 261 | 356 |
| Host Free | 0 | 100 | 284 | 437 | 782 | 1.069 |
| Fornitori attivi | 0 | 18 | 48 | 78 | 126 | 174 |
| Volume marketplace nel mese | € 2.000 | € 8.245 | € 20.896 | € 34.293 | € 59.253 | € 80.260 |
| MRR B2B | € 1.730 | € 2.850 | € 4.825 | € 6.685 | € 10.410 | € 13.918 |
| MRR B2C | € 0 | € 2.004 | € 5.678 | € 8.738 | € 15.630 | € 21.371 |
| MRR marketplace | € 200 | € 824 | € 2.090 | € 3.429 | € 5.925 | € 8.026 |
| **MRR totale** | € 1.930 | € 5.679 | € 12.593 | € 18.852 | € 31.966 | € 43.315 |
| Costi del mese | € 1.884 | € 3.382 | € 5.937 | € 7.896 | € 12.500 | € 16.122 |
| **Risultato del mese** | € 46 | € 2.297 | € 6.656 | € 10.956 | € 19.466 | € 27.192 |
| Cassa cumulata | -€ 6.659 | € 310 | € 28.665 | € 83.844 | € 178.870 | € 323.262 |

### Scenario Ambizioso

| Mese | M6 (2027-04) | M12 (2027-10) | M18 (2028-04) | M24 (2028-10) | M30 (2029-04) | M36 (2029-10) |
|---|---:|---:|---:|---:|---:|---:|
| Agenzie (partner + listino) | 1 + 0 | 4 + 0 | 4 + 6 | 4 + 11 | 4 + 22 | 4 + 33 |
| Unità B2B | 250 | 490 | 783 | 1.058 | 1.610 | 2.130 |
| Host B2C paganti | 0 | 56 | 161 | 251 | 441 | 605 |
| Host Free | 0 | 141 | 403 | 628 | 1.103 | 1.511 |
| Fornitori attivi | 0 | 30 | 78 | 126 | 198 | 270 |
| Volume marketplace nel mese | € 3.000 | € 16.690 | € 45.475 | € 77.005 | € 132.291 | € 180.332 |
| MRR B2B | € 1.730 | € 3.650 | € 7.746 | € 11.603 | € 19.330 | € 26.606 |
| MRR B2C | € 0 | € 3.664 | € 10.476 | € 16.328 | € 28.684 | € 39.298 |
| MRR marketplace | € 300 | € 1.669 | € 4.548 | € 7.701 | € 13.229 | € 18.033 |
| **MRR totale** | € 2.030 | € 8.983 | € 22.770 | € 35.632 | € 61.244 | € 83.937 |
| Costi del mese | € 1.899 | € 4.366 | € 8.992 | € 12.908 | € 21.284 | € 28.374 |
| **Risultato del mese** | € 131 | € 4.617 | € 13.778 | € 22.724 | € 39.960 | € 55.564 |
| Cassa cumulata | -€ 5.977 | € 9.995 | € 69.149 | € 183.399 | € 380.007 | € 675.327 |

## 5. Confronto tra scenari

| Indicatore | Prudente | Base | Ambizioso |
|---|---:|---:|---:|
| MRR a M12 | € 2.565 | € 5.679 | € 8.983 |
| MRR a M24 | € 8.746 | € 18.852 | € 35.632 |
| MRR a M36 | € 17.828 | € 43.315 | € 83.937 |
| ARR a M36 | € 213.938 | € 519.778 | € 1.007.246 |
| Quota B2B / B2C / marketplace sui ricavi a M36 | 41 % / 47 % / 12 % | 32 % / 49 % / 19 % | 32 % / 47 % / 21 % |
| Primo mese stabilmente in utile (founder non retribuito) | M10 (2027-08) | M6 (2027-04) | M5 (2027-03) |
| Fabbisogno massimo di cassa (minimo del cumulato) | -€ 7.556 a M9 | -€ 6.705 a M5 | -€ 6.240 a M4 |
| Cassa cumulata a M36 | € 99.781 | € 323.262 | € 675.327 |
| Risultato cumulato anni 1-3 | € 99.781 | € 323.262 | € 675.327 |

## 6. Dal mese 37: founder a tempo pieno e prime assunzioni

Il piano prevede che a 36 mesi lasci il mio lavoro e assuma. La domanda da porsi è: l'MRR di M36 copre la struttura?

| Voce (costo azienda mensile, stima) | € / mese |
|---|---:|
| Founder a tempo pieno (CEO/prodotto) | € 4.500 |
| Sviluppatore full-stack senior (con AI) | € 4.000 |
| Customer success e onboarding fornitori | € 3.000 |
| **Totale persone** | **€ 11.500** |

| Scenario | MRR M36 | Costi operativi M36 | Margine prima delle persone | Margine dopo le persone | Copertura |
|---|---:|---:|---:|---:|---:|
| Prudente | € 17.828 | € 8.641 | € 9.187 | -€ 2.313 | 80 % |
| Base | € 43.315 | € 16.122 | € 27.192 | € 15.692 | 236 % |
| Ambizioso | € 83.937 | € 28.374 | € 55.564 | € 44.064 | 483 % |

Lettura: con copertura sopra il 100 % le persone sono pagate dai ricavi del mese; sotto, la differenza va coperta
dalla cassa cumulata degli anni 1-3 o da capitale esterno, oppure si scagliona l'ingresso delle assunzioni.

## 7. Sensibilità (scenario Base, MRR a M36)

| Variazione | MRR M36 | Differenza MRR | Cassa cumulata M36 | Fabbisogno massimo |
|---|---:|---:|---:|---:|
| Scenario Base (riferimento) | € 43.315 | € 0 | € 323.262 | -€ 6.705 (M5) |
| ARPU B2C −20 % (€ 48) | € 39.041 | -€ 4.274 | € 270.537 | -€ 6.705 (M5) |
| ARPU B2C +20 % (€ 72) | € 47.589 | € 4.274 | € 375.987 | -€ 6.705 (M5) |
| Nessun piano Free (0 host gratuiti) | € 40.109 | -€ 3.206 | € 295.017 | -€ 6.705 (M5) |
| Churn B2C 2 %/mese | € 46.145 | € 2.830 | € 342.215 | -€ 6.705 (M5) |
| Churn B2C 4 %/mese | € 40.858 | -€ 2.456 | € 306.351 | -€ 6.705 (M5) |
| Commissione marketplace 8 % | € 41.710 | -€ 1.605 | € 302.756 | -€ 6.705 (M5) |
| Commissione marketplace 12 % | € 44.920 | € 1.605 | € 343.768 | -€ 6.705 (M5) |
| Nessuna agenzia a listino (solo founding partner) | € 31.657 | -€ 11.658 | € 247.917 | -€ 6.705 (M5) |
| Go-live Quokka a M9 invece di M6 | € 43.315 | € 0 | € 321.973 | -€ 7.411 (M7) |

Il driver principale dei ricavi è il numero di host B2C paganti (e con loro il volume del marketplace); il driver
principale della cassa nei primi 12 mesi è la data di go-live del primo founding partner, che da sola copre i costi fissi.

## 8. Come leggere e aggiornare

1. Le ipotesi sono dichiarate in testa allo script, una riga per scenario; le fonti sono nel business plan.
2. Ogni revisione del piano che tocca un'ipotesi rigenera questo file e il CSV nello stesso commit (vedi `CHANGELOG.md`).
3. Dal primo cliente, i valori stimati vanno sostituiti con i dati reali (MRR, churn, GMV) e lo scenario reale diventa il Base.

