# CasaZen: modello finanziario a 36 mesi

> **Documento generato**: non modificare a mano. Sorgente e ipotesi in `scripts/business-plan/modello_finanziario.py`;
> serie mensili complete in `modello-finanziario.csv`. Rigenerare con `python3 scripts/business-plan/modello_finanziario.py`.
> Orizzonte: M1 = novembre 2026, M36 = ottobre 2029. Importi in euro, IVA esclusa. Tutte le cifre sono **stime** [STIMA];
> le fonti dei prezzi e dei costi unitari sono nel business plan (§ 5 e Appendice A).

## 1. Cosa misura il modello

- **Ricavi B2B**: canoni delle agenzie. I founding partner pagano un canone fisso (costo documentato + € 1/unità);
  le agenzie a listino pagano il tutto compreso per unità. Le verifiche d'identità sono rifatturate al costo e restano fuori.
- **Ricavi B2C**: abbonamenti degli host (Starter € 29, Pro € 79, Scale € 199; ARPU medio ponderato).
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
| Unità medie per agenzia a listino, prezzo medio per unità | 40, € 14,50 | 45, € 14,50 | 50, € 14,00 |
| Churn mensile agenzie a listino | 1,5 % | 1,0 % | 1,0 % |
| Inizio onboarding B2C | M9 (2027-07) | M7 (2027-05) | M7 (2027-05) |
| Nuovi host paganti al mese (anno 1 / 2 / 3) | 3 / 6 / 12 | 6 / 12 / 25 | 10 / 20 / 40 |
| Churn mensile host B2C | 4,0 % | 3,0 % | 2,5 % |
| ARPU host B2C | € 50 | € 55 | € 58 |
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
marketing € 150 → € 400 → € 800 dall'inizio del B2C; costi per unità B2B € 0,40 (SDI, AI, email); AI per host B2C € 0,50;
Stripe sulle nostre fatture 2 % (B2C) e 0,5 % (B2B).

## 3. Riepilogo per anno

### Scenario Prudente

| Voce | Anno 1 (nov 26 - ott 27) | Anno 2 (nov 27 - ott 28) | Anno 3 (nov 28 - ott 29) | Totale 36 mesi |
|---|---:|---:|---:|---:|
| Ricavi B2B (agenzie) | € 8.650 | € 39.551 | € 70.205 | € 118.406 |
| Ricavi B2C (host) | € 1.441 | € 25.539 | € 70.792 | € 97.772 |
| Ricavi marketplace (10 % interventi) | € 701 | € 4.619 | € 10.934 | € 16.254 |
| **Ricavi totali** | € 10.792 | € 69.709 | € 151.931 | € 232.432 |
| Costi piattaforma | € 8.641 | € 26.077 | € 38.017 | € 72.735 |
| Accantonamento garanzia | € 105 | € 693 | € 1.640 | € 2.438 |
| Marketing | € 600 | € 4.800 | € 9.600 | € 15.000 |
| Amministrazione e consulenze | € 2.730 | € 3.960 | € 3.960 | € 10.650 |
| Una tantum (società, legale, marchio) | € 4.800 | € 0 | € 0 | € 4.800 |
| **Costi totali** | € 16.876 | € 35.530 | € 53.217 | € 105.623 |
| **Risultato operativo** (founder non retribuito) | -€ 6.084 | € 34.179 | € 98.714 | € 126.809 |
| Volume marketplace transato | € 7.013 | € 46.190 | € 109.340 | € 162.543 |

| A fine anno | Anno 1 | Anno 2 | Anno 3 |
|---|---:|---:|---:|
| MRR (ricavi mensili ricorrenti) | € 2.452 | € 8.041 | € 16.111 |
| ARR (MRR × 12) | € 29.426 | € 96.490 | € 193.335 |
| Agenzie B2B attive | 1 | 6 | 11 |
| Unità gestite dalle agenzie | 250 | 471 | 673 |
| Host B2C paganti | 11 | 65 | 156 |
| Fornitori attivi | 8 | 44 | 104 |
| Cassa cumulata (risultati sommati) | -€ 6.084 | € 28.095 | € 126.809 |

### Scenario Base

| Voce | Anno 1 (nov 26 - ott 27) | Anno 2 (nov 27 - ott 28) | Anno 3 (nov 28 - ott 29) | Totale 36 mesi |
|---|---:|---:|---:|---:|
| Ricavi B2B (agenzie) | € 14.510 | € 58.737 | € 124.967 | € 198.215 |
| Ricavi B2C (host) | € 6.594 | € 64.408 | € 175.586 | € 246.587 |
| Ricavi marketplace (10 % interventi) | € 2.321 | € 15.989 | € 43.869 | € 62.179 |
| **Ricavi totali** | € 23.425 | € 139.134 | € 344.422 | € 506.981 |
| Costi piattaforma | € 12.416 | € 34.292 | € 58.613 | € 105.321 |
| Accantonamento garanzia | € 348 | € 2.398 | € 6.580 | € 9.327 |
| Marketing | € 900 | € 4.800 | € 9.600 | € 15.300 |
| Amministrazione e consulenze | € 3.030 | € 3.960 | € 3.960 | € 10.950 |
| Una tantum (società, legale, marchio) | € 4.800 | € 0 | € 0 | € 4.800 |
| **Costi totali** | € 21.494 | € 45.450 | € 78.753 | € 145.698 |
| **Risultato operativo** (founder non retribuito) | € 1.931 | € 93.683 | € 265.669 | € 361.283 |
| Volume marketplace transato | € 23.211 | € 159.890 | € 438.690 | € 621.791 |

| A fine anno | Anno 1 | Anno 2 | Anno 3 |
|---|---:|---:|---:|
| MRR (ricavi mensili ricorrenti) | € 5.211 | € 16.686 | € 37.959 |
| ARR (MRR × 12) | € 62.534 | € 200.226 | € 455.512 |
| Agenzie B2B attive | 3 | 9 | 19 |
| Unità gestite dalle agenzie | 390 | 646 | 1.128 |
| Host B2C paganti | 33 | 146 | 356 |
| Fornitori attivi | 18 | 78 | 174 |
| Cassa cumulata (risultati sommati) | € 1.931 | € 95.614 | € 361.283 |

### Scenario Ambizioso

| Voce | Anno 1 (nov 26 - ott 27) | Anno 2 (nov 27 - ott 28) | Anno 3 (nov 28 - ott 29) | Totale 36 mesi |
|---|---:|---:|---:|---:|
| Ricavi B2B (agenzie) | € 20.240 | € 96.447 | € 238.550 | € 355.237 |
| Ricavi B2C (host) | € 11.685 | € 116.091 | € 314.239 | € 442.015 |
| Ricavi marketplace (10 % interventi) | € 5.026 | € 38.125 | € 108.268 | € 151.420 |
| **Ricavi totali** | € 36.951 | € 250.664 | € 661.057 | € 948.672 |
| Costi piattaforma | € 15.596 | € 49.102 | € 98.693 | € 163.391 |
| Accantonamento garanzia | € 754 | € 5.719 | € 16.240 | € 22.713 |
| Marketing | € 900 | € 4.800 | € 9.600 | € 15.300 |
| Amministrazione e consulenze | € 3.180 | € 3.960 | € 3.960 | € 11.100 |
| Una tantum (società, legale, marchio) | € 4.800 | € 0 | € 0 | € 4.800 |
| **Costi totali** | € 25.230 | € 63.580 | € 128.494 | € 217.304 |
| **Risultato operativo** (founder non retribuito) | € 11.721 | € 187.083 | € 532.564 | € 731.367 |
| Volume marketplace transato | € 50.260 | € 381.255 | € 1.082.683 | € 1.514.198 |

| A fine anno | Anno 1 | Anno 2 | Anno 3 |
|---|---:|---:|---:|
| MRR (ricavi mensili ricorrenti) | € 8.025 | € 31.361 | € 73.659 |
| ARR (MRR × 12) | € 96.299 | € 376.337 | € 883.909 |
| Agenzie B2B attive | 4 | 15 | 37 |
| Unità gestite dalle agenzie | 490 | 1.058 | 2.130 |
| Host B2C paganti | 56 | 251 | 605 |
| Fornitori attivi | 30 | 126 | 270 |
| Cassa cumulata (risultati sommati) | € 11.721 | € 198.804 | € 731.367 |

## 4. Le tappe del piano (mese per mese)

### Scenario Prudente

| Mese | M6 (2027-04) | M12 (2027-10) | M18 (2028-04) | M24 (2028-10) | M30 (2029-04) | M36 (2029-10) |
|---|---:|---:|---:|---:|---:|---:|
| Agenzie (partner + listino) | 0 + 0 | 1 + 0 | 3 + 1 | 3 + 3 | 3 + 5 | 3 + 8 |
| Unità B2B | 0 | 250 | 418 | 471 | 577 | 673 |
| Host B2C paganti | 0 | 11 | 41 | 65 | 116 | 156 |
| Fornitori attivi | 0 | 8 | 26 | 44 | 74 | 104 |
| Volume marketplace nel mese | € 0 | € 1.572 | € 3.767 | € 5.766 | € 8.976 | € 11.558 |
| MRR B2B | € 0 | € 1.730 | € 3.448 | € 4.213 | € 5.750 | € 7.154 |
| MRR B2C | € 0 | € 565 | € 2.072 | € 3.251 | € 5.803 | € 7.801 |
| MRR marketplace | € 0 | € 157 | € 377 | € 577 | € 898 | € 1.156 |
| **MRR totale** | € 0 | € 2.452 | € 5.896 | € 8.041 | € 12.451 | € 16.111 |
| Costi del mese | € 465 | € 1.936 | € 3.035 | € 3.336 | € 4.401 | € 4.926 |
| **Risultato del mese** | -€ 465 | € 517 | € 2.861 | € 4.705 | € 8.050 | € 11.185 |
| Cassa cumulata | -€ 7.010 | -€ 6.084 | € 4.330 | € 28.095 | € 67.245 | € 126.809 |

### Scenario Base

| Mese | M6 (2027-04) | M12 (2027-10) | M18 (2028-04) | M24 (2028-10) | M30 (2029-04) | M36 (2029-10) |
|---|---:|---:|---:|---:|---:|---:|
| Agenzie (partner + listino) | 1 + 0 | 3 + 0 | 3 + 3 | 3 + 6 | 3 + 11 | 3 + 16 |
| Unità B2B | 250 | 390 | 522 | 646 | 894 | 1.128 |
| Host B2C paganti | 0 | 33 | 95 | 146 | 261 | 356 |
| Fornitori attivi | 0 | 18 | 48 | 78 | 126 | 174 |
| Volume marketplace nel mese | € 2.000 | € 5.238 | € 12.379 | € 21.186 | € 35.808 | € 48.203 |
| MRR B2B | € 1.730 | € 2.850 | € 4.759 | € 6.557 | € 10.158 | € 13.549 |
| MRR B2C | € 0 | € 1.837 | € 5.205 | € 8.010 | € 14.328 | € 19.590 |
| MRR marketplace | € 200 | € 524 | € 1.238 | € 2.119 | € 3.581 | € 4.820 |
| **MRR totale** | € 1.930 | € 5.211 | € 11.202 | € 16.686 | € 28.067 | € 37.959 |
| Costi del mese | € 1.734 | € 2.571 | € 3.729 | € 4.488 | € 6.468 | € 7.835 |
| **Risultato del mese** | € 196 | € 2.640 | € 7.473 | € 12.197 | € 21.598 | € 30.124 |
| Cassa cumulata | -€ 6.509 | € 1.931 | € 34.027 | € 95.614 | € 201.256 | € 361.283 |

### Scenario Ambizioso

| Mese | M6 (2027-04) | M12 (2027-10) | M18 (2028-04) | M24 (2028-10) | M30 (2029-04) | M36 (2029-10) |
|---|---:|---:|---:|---:|---:|---:|
| Agenzie (partner + listino) | 1 + 0 | 4 + 0 | 4 + 6 | 4 + 11 | 4 + 22 | 4 + 33 |
| Unità B2B | 250 | 490 | 783 | 1.058 | 1.610 | 2.130 |
| Host B2C paganti | 0 | 56 | 161 | 251 | 441 | 605 |
| Fornitori attivi | 0 | 30 | 78 | 126 | 198 | 270 |
| Volume marketplace nel mese | € 3.000 | € 11.053 | € 29.358 | € 51.885 | € 88.162 | € 119.872 |
| MRR B2B | € 1.730 | € 3.650 | € 7.746 | € 11.603 | € 19.330 | € 26.606 |
| MRR B2C | € 0 | € 3.270 | € 9.348 | € 14.570 | € 25.595 | € 35.066 |
| MRR marketplace | € 300 | € 1.105 | € 2.936 | € 5.188 | € 8.816 | € 11.987 |
| **MRR totale** | € 2.030 | € 8.025 | € 20.030 | € 31.361 | € 53.742 | € 73.659 |
| Costi del mese | € 1.749 | € 3.101 | € 5.167 | € 6.822 | € 10.499 | € 13.435 |
| **Risultato del mese** | € 281 | € 4.924 | € 14.863 | € 24.540 | € 43.243 | € 60.224 |
| Cassa cumulata | -€ 5.677 | € 11.721 | € 75.463 | € 198.804 | € 411.487 | € 731.367 |

## 5. Confronto tra scenari

| Indicatore | Prudente | Base | Ambizioso |
|---|---:|---:|---:|
| MRR a M12 | € 2.452 | € 5.211 | € 8.025 |
| MRR a M24 | € 8.041 | € 16.686 | € 31.361 |
| MRR a M36 | € 16.111 | € 37.959 | € 73.659 |
| ARR a M36 | € 193.335 | € 455.512 | € 883.909 |
| Quota B2B / B2C / marketplace sui ricavi a M36 | 44 % / 48 % / 7 % | 36 % / 52 % / 13 % | 36 % / 48 % / 16 % |
| Primo mese stabilmente in utile (founder non retribuito) | M8 (2027-06) | M6 (2027-04) | M5 (2027-03) |
| Fabbisogno massimo di cassa (minimo del cumulato) | -€ 7.475 a M7 | -€ 6.705 a M5 | -€ 6.240 a M4 |
| Cassa cumulata a M36 | € 126.809 | € 361.283 | € 731.367 |
| Risultato cumulato anni 1-3 | € 126.809 | € 361.283 | € 731.367 |

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
| Prudente | € 16.111 | € 4.926 | € 11.185 | -€ 315 | 97 % |
| Base | € 37.959 | € 7.835 | € 30.124 | € 18.624 | 262 % |
| Ambizioso | € 73.659 | € 13.435 | € 60.224 | € 48.724 | 524 % |

Lettura: con copertura sopra il 100 % le persone sono pagate dai ricavi del mese; sotto, la differenza va coperta
dalla cassa cumulata degli anni 1-3 o da capitale esterno, oppure si scagliona l'ingresso delle assunzioni.

## 7. Sensibilità (scenario Base, MRR a M36)

| Variazione | MRR M36 | Differenza MRR | Cassa cumulata M36 | Fabbisogno massimo |
|---|---:|---:|---:|---:|
| Scenario Base (riferimento) | € 37.959 | € 0 | € 361.283 | -€ 6.705 (M5) |
| ARPU B2C −20 % (€ 44) | € 34.041 | -€ 3.918 | € 312.952 | -€ 6.705 (M5) |
| ARPU B2C +20 % (€ 66) | € 41.877 | € 3.918 | € 409.614 | -€ 6.705 (M5) |
| Churn B2C 2 %/mese | € 40.294 | € 2.335 | € 381.313 | -€ 6.705 (M5) |
| Churn B2C 4 %/mese | € 35.933 | -€ 2.027 | € 343.411 | -€ 6.705 (M5) |
| Commissione marketplace 8 % | € 36.995 | -€ 964 | € 348.847 | -€ 6.705 (M5) |
| Commissione marketplace 12 % | € 38.923 | € 964 | € 373.719 | -€ 6.705 (M5) |
| Nessuna agenzia a listino (solo founding partner) | € 26.670 | -€ 11.289 | € 269.377 | -€ 6.705 (M5) |
| Go-live Quokka a M9 invece di M6 | € 37.959 | € 0 | € 359.333 | -€ 7.462 (M7) |

Il driver principale dei ricavi è il numero di host B2C paganti (e con loro il volume del marketplace); il driver
principale della cassa nei primi 12 mesi è la data di go-live del primo founding partner, che da sola copre i costi fissi.

## 8. Come leggere e aggiornare

1. Le ipotesi sono dichiarate in testa allo script, una riga per scenario; le fonti sono nel business plan.
2. Ogni revisione del piano che tocca un'ipotesi rigenera questo file e il CSV nello stesso commit (vedi `CHANGELOG.md`).
3. Dal primo cliente, i valori stimati vanno sostituiti con i dati reali (MRR, churn, GMV) e lo scenario reale diventa il Base.

