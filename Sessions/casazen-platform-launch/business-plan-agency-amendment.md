# CasaZen: Business Plan, amendment Tier Agenzia (B2B)

> **Overlay, non una riscrittura.** `business-plan.md` e `business-plan-after-devils-review.md` restano invariati. Dove questa pagina indica **SOSTITUISCE**, vale al posto della sezione con lo stesso numero. Tutto il resto del piano resta valido.
> **Data:** 2026-10-19. **Origine:** prospect Quokka 360 (agenzia, 250 unità), analisi in `Sessions/prospect-quokka360/analisi-funzionalita.md` (v7).
> **Fonti:** **[FACT]** = verificato su fonte ufficiale o nel codice; **[EST]** = stima con ipotesi dichiarata. Prezzi e numeri **da rivedere con il PO**.

---

## §5 Livelli di prodotto (INTEGRA)

Si aggiunge il livello **Agenzia (B2B)** accanto a Starter, Pro e Scale. È lo stesso prodotto (R8 dell'analisi), con in più:

| Capacità | Agenzia (B2B) | Stato |
|---|:---:|---|
| Nuovo tipo di host `Agency`, ruoli proprietario e staff | ✓ | da sviluppare |
| Channel manager con contenuti e chat via API | ✓ | da sviluppare (fornitore in verifica) |
| Portale proprietari: rendiconto mensile, ritenuta 21% e F24 precompilato, fatture SDI, file di bonifico | ✓ | da sviluppare |
| Check-in con confronto volto-documento fatto dall'ospite | ✓ | da sviluppare |
| Invio ad Alloggiati automatico da CasaZen | ✓ (anche B2C) | da sviluppare |
| Routine configurabili (QUANDO → SE → ALLORA) | ✓ | da sviluppare |
| CRM e campagne (opzione AI e Marketing) | ✓ | da sviluppare |
| Fornitori propri a 0% | ✓ | da sviluppare |

---

## §6 Modello di prezzo (SOSTITUISCE per il livello Agenzia)

**Principio invariato:** abbonamento per unità, **nessuna commissione** sulle prenotazioni dell'operatore (D1).

| Pacchetto | Prezzo per unità al mese **[EST]** | Cosa include |
|---|---|---|
| **Base** | **€9** (10–49 unità) · **€8** (50–149) · **€7** (150–399) · preventivo oltre | Gestionale, channel manager con contenuti, inbox con risposte AI suggerite, sito di prenotazione diretta, check-in con riconoscimento del volto, Alloggiati, portale proprietari, routine, fornitori |
| **+ AI e Marketing** | **+€4** | Risposte AI automatiche, routine da una frase, CRM e campagne |
| **Quokka (prima agenzia)** | **~€2.300 al mese** per 250 unità (€9,20/unità) | Base + AI e Marketing, circa −16% sul listino di €2.750 |

- Il prezzo di Quokka **non si abbassa**: la priorità è il margine di CasaZen.
- Il prezzo si giustifica con il pacchetto Agenzia completo, non con personalizzazioni (il prodotto resta unico).
- Programma Founding Partner: listino con sconto di circa il 15%, bloccato 24 mesi, rivisto solo se cambiano il costo del channel manager o il cambio USD/EUR (proposta, da confermare).
- Prezzi minimi da rivedere: sotto le **39 unità** il minimo del channel manager (€350) supera il ricavo del Base a €9.

---

## §7 Mix dei ricavi (INTEGRA)

Si aggiunge il flusso **Abbonamenti Agenzia**, con ARPU per account molto più alto del B2C: Quokka da sola vale ~€27.600 di ARR, circa **il 31% del pavimento di ARR del primo anno** (€90.000, §10 dell'originale). Per un'agenzia il ricavo è per unità: 50 unità ≈ €400–550 al mese, 250 unità ≈ €1.750–2.750.

---

## §9 Unità economiche (SOSTITUISCE per il livello Agenzia)

Per ora l'**unico costo di terze parti è il channel manager**: NextPax è il riferimento di costo (**[FACT]** <https://nextpax.com/nextpax-pricing>): minimo €350 al mese con 50 unità incluse, poi €3,15 a unità con fatturazione annuale (€3,50 mensile). La scelta del fornitore è in verifica.

| Unità | Costo channel manager | Ricavo Base | Margine Base |
|---|---|---|---|
| 30 | €350 (minimo) | €270 | **−€80** |
| 50 | €350 | €400 | +€50 |
| 100 | €508 | €800 | +€292 |
| 150 | €665 | €1.050 | +€385 |
| 250 | €980 | €1.750 | +€770 (44%) |
| 250, Quokka a €2.300 | €980 | €2.300 | **+€1.320 (57%)** |
| 250, Base + AI e Marketing a listino | €980 | €2.750 | +€1.770 (64%) |

- **Margine lordo del livello Agenzia: 44–64%**, contro l'80–85% del B2C. Il vincolo "GM ≥80%" del §9 originale vale per il B2C e **non** per il livello Agenzia, perché il channel manager è un costo per unità.
- Non sono conteggiati: hosting, email, fatture SDI, AI, abbonamento di sviluppo, tempo del PO e sviluppo. Il costo dell'AI e del confronto del volto, sviluppato in casa, non è stato misurato **[EST]**.
- **Prima del primo cliente:** €0, oppure €350 al mese se il channel manager fattura il minimo dalla firma. Da negoziare: decorrenza dal go-live.

---

## §10 Orizzonte di cinque anni (INTEGRA)

- Il pavimento di ARR del primo anno (€90.000 con 40–60 account a €180–200) non include il livello Agenzia. Con Quokka sale di ~€27.600.
- Un'agenzia da 250 unità vale quanto circa 12 account B2C a €190 di ARPU: il conteggio degli account non è più confrontabile tra i due livelli. Gli obiettivi del §10 vanno letti in ARR e non in numero di account.
- **Tempi di sviluppo del livello Agenzia:** ~100–165 finestre di Claude Code Max 20x (≈10–16 settimane equivalenti, ipotesi di 10 finestre a settimana **[EST]**), in sei ondate (§7 dell'analisi). Va sommato alle fasi del §10 dopo la devil's review.

---

## §13 Registro dei rischi (INTEGRA)

| # | Rischio | Tipo | P/I **[EST]** | Mitigazione |
|---|---|---|:---:|---|
| R9 | Dipendenza da un solo channel manager (minimo, condizioni, uscita dal mercato) | Fornitore | Media/Alta | Isolare il fornitore dietro `IChannelAdapter`; negoziare minimo dal go-live; seconda alternativa (Octorate) in valutazione |
| R10 | Margine lordo del livello Agenzia (44–64%) sotto l'obiettivo del piano | Finanziario | Media/Media | Prezzo di Quokka non scontato; revisione degli scaglioni; opzione AI e Marketing a +€4 |
| R11 | Invio automatico ad Alloggiati: nessuna sandbox, invio irreversibile, ricevuta il giorno dopo | Tecnico/Normativo | Media/Alta | `Test` prima di ogni `Send`; primo invio supervisionato su una struttura reale; stato "ricevuta in attesa" |
| R12 | Fiscale: CU e 770 restano a carico dell'agenzia | Prodotto | Media/Media | Parità con i concorrenti su rendiconto e F24; riprendere i file se i clienti lo chiedono |
| R13 | Confronto del volto in casa senza controllo che la persona sia viva | Tecnico | Media/Media | Soglia calibrata; registro dei tentativi; passaggio a un operatore dopo N tentativi (da decidere) |
