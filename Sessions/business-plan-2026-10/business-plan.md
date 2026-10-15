# CasaZen: business plan 2026-10

**Versione:** v1, 2026-10-15.
**Sostituisce:** `Sessions/casazen-platform-launch/business-plan.md` (giugno 2026) e la sua revisione `business-plan-after-devils-review.md`. Molte loro premesse non valgono più (§1.3).
**Base:** PR #526, `Sessions/prospect-quokka360/analisi-funzionalita.md` (tipo host Agenzia B2B per il prospect Quokka 360). Qui ne riprendo prezzi, costi unitari e stime di sviluppo e li verifico contro il mercato e il codice.
**Stato:** bozza per la decisione del PO. Le decisioni aperte sono in §13 con prefisso `BP-` (gli ID D1… sono già usati da altri documenti).

**Metodo:**
- **Codice:** inventario di `backend`, `frontend` e `mobile` a `develop` HEAD (2026-10-15).
- **Documenti interni:** analisi, decisioni e domande aperte in `Sessions/` e `docs/`.
- **Mercato e concorrenti:** fonti ufficiali consultate il 2026-10-15 (Ministero del Turismo, ISTAT, Eurostat, BFS, InfoCamere, Agenzia delle Entrate, EUR-Lex), più i listini e i comunicati delle aziende.
- **Modello economico a 36 mesi:** `modello/` (Python + Excel con formule, verificati tra loro).

**Legenda:**
- ✓ = verificato su fonte ufficiale o nel codice
- **[IPOTESI]** = assunzione da sostituire con dati reali o preventivi
- **[NV]** = non verificato
- ⚖ = serve un parere legale o fiscale

---

## 0. Sintesi

**Dove siamo.** CasaZen ha un prodotto ampio:
- 152k righe C# e 79k righe TypeScript;
- circa 6.000 test backend;
- gestionale, sito di prenotazione diretta, check-in online, fornitori, locazioni lunghe.

Ma CasaZen **non è ancora vendibile**:
- nessuna società;
- nessun cliente e nessun ricavo;
- nessuna produzione attiva (Railway è stato disdetto il 2/10);
- prezzi decisi e poi sospesi tre volte in cinque giorni;
- adempimenti principali ancora manuali (Alloggiati senza client SOAP, ISTAT assente);
- channel manager solo iCal.

**Tesi.** Il segmento con cui partire sono le **agenzie** che gestiscono appartamenti per conto dei proprietari, in Italia e nella Svizzera italiana. Il primo cliente è **Quokka 360** (250 unità, oggi su Kross a ~€2.000/mese).
- **Perché le agenzie:** sono il segmento che paga per unità, che ha bisogno di tutto il pacchetto (channel manager, adempimenti, identificazione, proprietari) e che oggi lo compone con 2–3 fornitori.
- **Il B2C (host privati) resta su invito** finché channel manager e Alloggiati non sono automatici. Oggi, a €29, CasaZen è meno competitivo di Kross Premium (€6/unità con channel manager e Alloggiati automatico ✓).

**Numeri chiave** (modello §9, IVA esclusa):

| | Valore |
|---|---|
| Margine lordo per unità, listino tutto compreso | **43–51%** (non l'80% di un SaaS puro: channel manager, servizi e verifiche costano €7,39 per unità al mese) |
| Margine lordo per unità, Founding Partner | **31%** |
| Costi fissi in produzione | ~**€1.295/mese** (incluso il minimo NextPax) |
| Quokka da solo | **non copre i costi fissi**: −€3.500 circa l'anno, prima di pagare il fondatore |
| Pareggio con il fondatore non pagato | ~**170 unità a listino** |
| Pareggio con il fondatore a €6.000/mese | ~**960 unità a listino** (~24 agenzie da 40 unità) |
| Scenario obiettivo, a ott 2029 | ~1.060 unità B2B, ricavi ricorrenti ~€16.600/mese (~€200k l'anno) |
| Cassa minima, fondatore non pagato | −€8.500 |
| Cassa minima, fondatore a €6.000 dal go-live | −€110.000, e nessun pareggio entro 36 mesi |

**Cosa decidere ora** (§13):
1. listino unico **per unità** al posto di 29/79/199 piatti;
2. condizioni Founding Partner con un termine;
3. **nessuna spesa sul channel manager prima del contratto firmato con Quokka**;
4. paese e forma della società;
5. impegno e compenso del fondatore, che determinano quante unità servono;
6. **Didit** come fornitore di verifica: con Sumsub il pacchetto tutto compreso va in perdita.

---

## 1. Punto di partenza (fatti)

### 1.1 Azienda, team, clienti

| Tema | Stato | Fonte |
|---|---|---|
| Società | **Non costituita.** Dati societari vuoti, partita IVA assente, checkout SaaS bloccato (409) | `Casazen.Web/appsettings.json:63,165-175`; `Sessions/risanamento/DOMANDE-APERTE.md:262,275` ✓ |
| Team | 1 persona (PO e fondatore) + agenti AI: dal 2026-06-01, 462 commit di Claude e ~114 di Cursor | git log ✓ |
| Tempo | "Sviluppo part-time, budget contenuto" | `Sessions/PLANNING.md:63` |
| Clienti, ricavi | **Zero.** Vendita su invito privato, registrazioni Auth0 chiuse (2026-10-13) | git `e02783cb`; `spec-public-marketing-site.md:26,52` ✓ |
| Prospect | Quokka 360: 250 unità IT+CH, annunci intestati a Quokka, ~€2.000/mese a Kross (dato del PO) | PR #526 §6.1, §8 |
| Partner in discussione | ConcordatoFacile (canone concordato), bozza di integrazione v1.3 | `docs/integrations/concordatofacile/01-specifica-integrazione.md` |
| Produzione | v2.0.0 rilasciata su `main` il 14/10, ma **l'hosting del backend non è deciso** (Railway disdetto il 2/10, regola "zero costi") | git `b92817f1`; `docs/INFRA.md:5` ✓ |
| Testi legali | ToS, Privacy e DPA scritti da un agente, **non attivi**, da far rivedere a un avvocato | `docs/legal/*.md:1-11` ✓ |

### 1.2 Prodotto: cosa funziona oggi e cosa manca

"Funziona" significa codice e test presenti, non "in produzione" (`Sessions/specs/README.md:29`).

| Area | Oggi | Per vendere alle agenzie (PR #526) |
|---|---|---|
| Gestionale | Proprietà, prenotazioni, calendario, blocchi, KPI; modalità breve e lunga ✓ | Tipo `Agency`, ruoli proprietario e staff (assenti ✓, `OrgType.cs`) |
| Canali | **Solo iCal**. Gli adapter API delle OTA sono stub congelati (`BookingComAdapter.cs:48`, flag `OtaPartnerApi`) ✓ | Channel manager via API (NextPax/Octorate), contenuti, inbox (assente ✓) |
| Prenotazione diretta | Sito `/book/{slug}`, 3 temi, SEO ✓. Stripe Connect Express in direct charge, senza commissione ✓. Pagamento con carta **mai provato end to end** | Accounts v2 con `fees_collector = stripe` |
| Adempimenti IT | CIN, wizard, checklist sicurezza ✓. **Alloggiati: solo file, invio manuale** (`AlloggiatiWebService.cs:15-20`) ✓. ISTAT assente. Tassa di soggiorno: ~10 città, aliquote non verificate | Alloggiati via SOAP, ISTAT via API, file AdE/F24 |
| Adempimenti CH | **Assenti**: solo EUR, solo IT/EN, fuso Europe/Rome ✓ | CHF, tedesco, feratel, XML polizia Ticino |
| Ospiti | Check-in online con link e dati cifrati (parziale: niente upload del documento) ✓ | Biometria via terzi, QR all'ingresso, videochiamata |
| Comunicazioni | Email Resend ✓. Inbox e AI congelate; l'AI è usata solo per i contenuti SEO (DeepSeek, spenta di default) | Inbox, risposte AI, CRM, WhatsApp |
| Proprietari | **Assente** | Portale, rendiconti, fatture SDI, file SEPA |
| Fornitori | Console web, richieste di servizio, pagamenti online dietro flag ✓ | Fornitori propri a 0% |
| Automazioni | Solo job Hangfire fissi | Routine QUANDO → SE → ALLORA |
| Locazioni lunghe | Contratti (3 modelli), registro canoni, calcolatore canone concordato (solo Seveso e Cesano Maderno); RLI e firma elettronica **manuali** | — (non richiesto da Quokka) |
| Mobile | App host Expo, non pubblicata negli store ✓ | — |
| Esercizio | Health check ✓; **nessun monitoraggio errori o APM** ✓ | Indispensabile prima di un cliente da 250 unità |

**Lettura critica.**
- Il codice è molto, ma le funzioni che decidono l'acquisto di un'agenzia mancano tutte: channel manager API, Alloggiati automatico, proprietari, inbox.
- I concorrenti italiani le danno per scontate (§3).
- L'audit del 23/09 aveva trovato funzioni dichiarate "shipped" che non lo erano (`Sessions/PIANO-RISANAMENTO-2026-09.md:13-18`). Quindi le stime in righe di codice vanno lette con prudenza (§5).

### 1.3 Cosa cambia rispetto al business plan di giugno

| Tema | Giugno 2026 | Oggi |
|---|---|---|
| OTA | "6 canali, shipped" | Solo iCal; serve un channel manager terzo a €3,15–3,50 per unità ✓ |
| Vantaggio compliance | "Shipped; nessun concorrente lo fa" | Alloggiati è manuale. Kross, Octorate, Vikey, Lodgify e **Guesty AutoComply** inviano Alloggiati in automatico ✓ (§3) |
| Prezzi | Starter gratis, Pro €4–8/unità | 8/10 29/79/199 piatti → 9/10 €0 in prevendita → 13/10 "non definiti". PR #526: €7–9/unità B2B |
| Margine lordo | ≥80% | **43–51%** a listino, per il costo del channel manager e delle verifiche |
| SOM al 3° anno | €1,3–2,3M di ricavi ricorrenti annui | ~€0,2M (scenario obiettivo §9). Il valore di giugno non aveva base dati |
| Geografia | Italia, poi Spagna e Francia | Italia + Svizzera italiana e Grigioni, perché lì opera il primo cliente |
| Infrastruttura | $0 → €5 | Railway disdetto; per un cliente da 250 unità servono piani a pagamento (~€85 + Vercel Pro) |
| Marketplace | 10–20% dei ricavi | Congelato, con commissione incoerente in configurazione (10% contro 3,5%, `appsettings.json:34,90`) ✓. **Fuori da questo piano** |
| Unità STR in Italia | ~600.000 [EST], 25% gestite da professionisti | **734.834 strutture registrate**, di cui **21,6% in forma imprenditoriale** ✓ (§2.1) |

---

## 2. Mercato

### 2.1 Italia

| Dato | Valore | Fonte |
|---|---|---|
| Strutture registrate nella BDSR (15/10/2026) | **734.834**; CIN rilasciati 667.602 (90,9%) | Ministero del Turismo, API BDSR ✓ |
| ↳ C, alloggi privati non imprenditoriali (locazioni brevi) | **515.384** (70,1%) | Export CSV BDSR ✓ |
| ↳ B4, alloggi in affitto gestiti in forma imprenditoriale | **158.486** (21,6%) | idem ✓ |
| ↳ B5 agriturismi / A alberghi | 21.323 / 31.344 | idem ✓ |
| Crescita delle registrazioni | 549.900 a dic 2024 → 734.834 a ott 2026 (+34%) | Ministero del Turismo ✓ |
| Prime regioni | Toscana 83.185 · Lombardia 79.700 · Lazio 72.444 · Veneto 65.930 · Sicilia 59.100 | API BDSR ✓ |
| Presenze extra-alberghiere 2024 | **182,2 milioni** (+6,1%); arrivi 43,3 milioni | ISTAT, Statistica Today Turismo 2024 ✓ |
| Alloggi imprenditoriali 2024 | 156.398 esercizi (+20,6%), 1.114.920 letti | ISTAT, Annuario 2025 cap. 19 ✓ |
| Alloggi privati non imprenditoriali 2024 (92 province su 107) | 316.954 unità; 39,1 milioni di presenze | ISTAT, Statistica Today ✓ |
| Notti prenotate su piattaforme online | 126,7 M (2024) → **138,7 M (2025, +9,5%)**: 14,6% dell'UE | Eurostat `tour_ce_oar` ✓ |
| Imprese ATECO 55.20 (30/09/2025) | **44.801** (+42% sul 2021); alberghi 29.199 (−5,2%) | Unioncamere-InfoCamere ✓ |
| Maggiore gestore quotato | CleanBnB: 3.154 unità (30/06/2026); ricavi 2025 €19,9 M, EBITDA €0,5 M | Comunicati Euronext Growth Milan ✓ |
| Annunci gestiti attivi | 491.117 (2025) | Rescasa-Confcommercio, citato da CleanBnB **[secondaria]** |

**Cosa ne ricavo:**
- **Il mercato professionale è frammentato.** Il primo operatore quotato (CleanBnB, 3.154 unità) vale circa il 2% delle strutture imprenditoriali B4 e lo 0,4% di tutte le registrate. Un'agenzia da 250 unità come Quokka è già di taglia media.
- **Non esiste un dato ufficiale** sul numero di agenzie per classe di unità (ATECO 68.32 non trovato). Il numero di clienti potenziali in "conti" resta **[IPOTESI]**; il dimensionamento si fa in unità (§2.3).

### 2.2 Svizzera (Ticino e Grigioni)

| Dato | Valore | Fonte |
|---|---|---|
| Appartamenti di vacanza gestiti commercialmente, CH 2025 | 28.127 unità; 7,37 milioni di pernottamenti | BFS, comunicato 2026-0358 ✓ |
| ↳ Ticino 2025 | **2.673** unità; 466.313 pernottamenti (−2,7%) | idem ✓ |
| ↳ Svizzera orientale, che comprende i Grigioni (2025) | 7.326 unità; 2,2 milioni di pernottamenti. Il dato del solo cantone non è pubblicato | idem ✓ |
| Notti su piattaforme 2025 | Ticino 895.059 (+12%); Grigioni 1.036.214 (+14%) | Eurostat `tour_ce_oan3` ✓ |
| Tassa di soggiorno Ticino | CHF 2,00/notte per appartamenti e case di vacanza dal 1/1/2025 | Ticino Turismo ✓ |

**Lettura:** la Svizzera è un mercato **piccolo in unità**. Il Ticino vale meno del 2% delle unità imprenditoriali italiane. Si entra per seguire il primo cliente, non per volume. Le funzioni CH vanno quindi limitate a ciò che serve a Quokka (cantoni e comuni reali, PR #526 §8 domanda 1).

### 2.3 Dimensionamento

Il valore è calcolato al prezzo del pacchetto tutto compreso (§6). Non è una previsione.

| Livello | Definizione | Unità | Valore annuo |
|---|---|---|---|
| **TAM** | Tutte le unità registrate di affitto breve: IT categorie B4 + C, più gli appartamenti di vacanza gestiti commercialmente in CH | ~702.000 | ~€76 M a €9/unità (Base) |
| **SAM** | Unità gestite in forma imprenditoriale (IT B4) + Ticino + Svizzera orientale, cioè dove si vende il B2B | ~168.500 | ~€28 M a €14/unità |
| **SOM a 36 mesi** | Scenario obiettivo (§9): unità B2B a ott 2029 | ~1.060 (0,6% del SAM) | ~€0,2 M di ricavi ricorrenti annui |

**Limiti:**
- B4 conta strutture, non appartamenti, e una struttura può avere più unità.
- Le agenzie gestiscono anche unità di categoria C intestate ai proprietari.
- Il SAM in unità è quindi un'approssimazione, probabilmente prudente. Il SOM è un obiettivo commerciale, non una quota di mercato stimata.

### 2.4 Norme che spingono la domanda

| Norma | Effetto | Fonte |
|---|---|---|
| CIN obbligatorio, sanzioni dal 2025 (senza CIN €800–8.000; CIN non esposto €500–5.000) | Spinge le registrazioni (+34% in 22 mesi) e la necessità di strumenti di adempimento | Ministero del Turismo, FAQ BDSR ✓ |
| L. 199/2025 c. 17: oltre **2 appartamenti** l'attività si presume d'impresa (prima erano 4); cedolare 26%, 21% su un'unità | Più host diventano imprese, cioè il target B2B. Aumenta il bisogno di fatturazione e adempimenti | Agenzia delle Entrate, guida aprile 2026 ✓ |
| Consiglio di Stato 9101/2025: identificazione de visu o video all'ingresso "hic et nunc" | La keybox da sola non basta; serve un'identificazione all'ingresso conforme (WP7) | giustizia-amministrativa.it ✓ |
| Reg. UE 2024/1028, applicabile dal **20/05/2026** | Le piattaforme trasmettono dati e numero di registrazione. ⚖ Va verificato se i siti di prenotazione di CasaZen rientrano | EUR-Lex ✓ |
| DAC7 (D.Lgs. 32/2023) | Obblighi di comunicazione per i gestori di piattaforme. ⚖ Va verificato per il sito di prenotazione | Agenzia delle Entrate ✓ |
| Testo unico IVA (D.Lgs. 10/2026) dal 1/1/2027 | Riferimenti normativi rinumerati: le fatture e i file AdE vanno riverificati | Normattiva ✓ (PR #526 §4) |

---

## 3. Concorrenza

### 3.1 Listini (pagine ufficiali, 2026-10-15)

| Prodotto | Prezzo | Alloggiati / ISTAT | Note |
|---|---|---|---|
| **Kross Booking** ✓ | Basic €4/unità (gestionale); **Premium €6/unità** (gestionale + channel manager + booking engine); minimo 3 unità; preventivo oltre 20 camere | Alloggiati automatico; export ISTAT | Fornitore attuale di Quokka. KDoor da €29/mese |
| **Vikey** ✓ (Zucchetti, maggioranza da giu 2025 [secondaria]) | Starter €4,90, Master €9,90/unità. Moduli per unità: Alloggiati €2,90, ISTAT €1,50, tassa di soggiorno €1,90, documento+selfie €3,90, channel manager €3,90 | Sì, a moduli | Master con tutti i moduli: **€23,90/unità** |
| **Chekin** ✓ | €3,95 / €5,95 / €7,95 per proprietà | Adempimenti e tassa di soggiorno in tutti i piani | Verifica d'identità in Premium/Enterprise |
| **Octorate** ✓ | Basic da €8/camera (channel manager + booking engine); Pro e Ultimate su richiesta | Alloggiati automatico giornaliero; ISTAT secondo la regione | Moduli portale proprietari, self check-in, AI. **È anche uno dei due fornitori API candidati** (PR #526) |
| **Smoobu** ✓ (HomeToGo) | €35/mese (€31,50 annuale) per 1 proprietà, €9,60 per unità in più; Teams Pro+ €55 | Solo tramite Chekin | 616.666 utenti attivi mensili in UE (dichiarazione DSA) |
| **Lodgify** | $14–62/mese (pagina ufficiale bloccata; prezzi dalle pagine di confronto di Lodgify) [NV] | Invio Alloggiati nativo (blog Lodgify) | — |
| **Guesty** ✓ | Lite da $9/annuncio + 1%, oppure $20 annuale; Pro ed Enterprise su preventivo | **AutoComply nativo: Alloggiati + ISTAT regionale** | Portale proprietari in Pro |
| **Hostaway** ✓ | Solo su preventivo | Tramite marketplace (Check-in Scan) | — |
| **Beds24** ✓ | Da €15,50/mese + €0,55 per collegamento a un canale | — | — |
| OwnerRez, Uplisting, Avantio | $2–8/unità; £13/unità; $16–20/unità | — | PR #526 §6.1 ✓ |

### 3.2 Capitali e consolidamento

| Evento | Fonte |
|---|---|
| Guesty: Serie F da **$130 M** (apr 2024), $410 M raccolti in totale, oltre 500.000 proprietà | PR Newswire ✓ |
| Guesty compra **Rentals United** (mag 2024) e **Smily** (17/09/2026) | Comunicati Guesty ✓ |
| Hostaway: **$365 M** (dic 2024); dichiara di voler crescere in **Italia**, Francia e Spagna | Blog Hostaway ✓ |
| Zucchetti Hospitality prende la maggioranza di Vikey (giu 2025) | BeBeez, Websim [secondaria] |
| HomeToGo controlla Smoobu dal 2021 | [secondaria] |
| Halldis (534 unità) ceduta ad Anima per €2,6 M (ago 2024) | Comunicato Anima ✓ |

**Lettura:**
- I concorrenti con capitali grandi stanno entrando in Italia e comprano i fornitori di connettività: Rentals United, scartato in PR #526, oggi è di Guesty.
- **Il rischio fornitore sul channel manager è strategico.** NextPax o Octorate possono essere acquisiti o vendere direttamente alle agenzie (Octorate lo fa già).

### 3.3 Confronto per un'agenzia da 40 unità (€/unità/mese)

| Pacchetto equivalente: gestionale + channel manager + Alloggiati/ISTAT + identificazione | €/unità |
|---|---|
| Vikey Master + tutti i moduli | 23,90 |
| Kross Premium + Vikey (dato PR #526) | ~16 |
| **CasaZen tutto compreso** (10–49 unità), che include anche portale proprietari, SDI per conto, SEPA, AI, CRM e 8 verifiche biometriche | **15** |
| Kross Premium da solo (senza identificazione biometrica, senza portale proprietari) | 6 |

### 3.4 Dove CasaZen può vincere e dove no

**Dove non può vincere:**
- **sul prezzo base:** Kross a €6 con channel manager proprio. CasaZen paga €3,15–3,50 per unità solo di channel manager;
- **sulla conformità italiana in sé:** è ormai un requisito minimo, e oggi CasaZen è **indietro** (Alloggiati manuale).

**Dove può vincere** (da validare con i clienti, non fatti):
1. **Un solo fornitore per tutto il ciclo dell'agenzia,** con proprietari (rendiconti, fatture SDI, SEPA) e identificazione conforme a CdS 9101/2025 a un prezzo inferiore alla somma dei fornitori (§3.3).
2. **Routine legate agli eventi di compliance** (es. codice della serratura solo dopo l'identificazione): nessun concorrente lo fa secondo l'analisi dei help center (PR #526 §3.6).
3. **Italia + Svizzera italiana in un solo prodotto:** XML polizia del Ticino, feratel, CHF. È una nicchia piccola (§2.2), ma è quella di Quokka. [NV] se esistono concorrenti con la stessa copertura.
4. **Agenzie che fanno anche affitti lunghi:** contratti, canone concordato, RLI. Il vantaggio è [EST], ridimensionato già a giugno; oggi RLI e firma sono manuali.

**Forze di Porter (sintesi):**
- **Potere dei fornitori: alto.** Channel manager, OTA chiuse alle nuove connessioni (Booking.com ✓), verifica d'identità.
- **Rivalità: alta,** e i concorrenti sono finanziati.
- **Potere dei clienti: medio.** Cambiare gestionale è costoso, ma Quokka da solo varrebbe il 100% dei ricavi per mesi.
- **Barriere all'ingresso:** basse sul software, alte sulla conformità IT/CH, che però i concorrenti hanno già superato.

---

## 4. Clienti e proposta di valore

| Priorità | Segmento | Perché | Cosa compra |
|---|---|---|---|
| **1** | **Agenzie con 20–400 unità in IT e CH** (Quokka come primo cliente) | Pagano per unità, hanno più fornitori da sostituire, il dolore dei proprietari e degli adempimenti è alto | Tutto compreso per unità (§6.2) |
| 2 | Host imprenditoriali con 3–20 unità (spinti dalla L. 199/2025) | In crescita (B4 +20,6% nel 2024 ✓; ATECO 55.20 +42% ✓) | Base per unità, senza portale proprietari |
| 3 | Host privati con 1–2 unità (categoria C) | Grandi numeri ma prezzo basso e già serviti da Kross, Smoobu e Lodgify | Canone minimo. **Solo dopo** che channel manager e Alloggiati sono automatici |
| Fuori piano | Alberghi, Spagna e Francia, marketplace fornitori | Nessuna base nel codice o nei dati | — |

**Proposta di valore per l'agenzia** (in una frase): *"Un solo strumento per canali, ospiti, adempimenti italiani e svizzeri e proprietari, con l'identificazione conforme e i pagamenti ai proprietari pronti da approvare in banca, a un prezzo inferiore ai fornitori che usi oggi."*

**Promessa corretta:** "compliance automatizzata", non "compliance delegata". Le responsabilità di legge restano all'agenzia (PR #526 §4).

---

## 5. Prodotto e roadmap

### 5.1 Prerequisiti per vendere a chiunque (Fase 0)

Sono indipendenti da Quokka e oggi mancano tutti:

| # | Prerequisito | Perché blocca | Fonte |
|---|---|---|---|
| P1 | Società costituita, partita IVA, fatturazione elettronica (provider SDI) | Senza, non si può incassare né fatturare | `appsettings.json:63`; `DOMANDE-APERTE.md:265` |
| P2 | Hosting di produzione a pagamento con backup (Supabase Pro), Vercel Pro (uso commerciale) | Un'agenzia da 250 unità non può dipendere da piani gratuiti senza backup | `docs/runbooks/free-hosting-analysis.md:252,257` |
| P3 | Monitoraggio errori e disponibilità, procedura per gli incidenti | Oggi non c'è nessun APM ✓ | `observability/docker-compose.yml:3` |
| P4 | ToS, Privacy, DPA e SLA rivisti da un avvocato e attivati ⚖ | Oggi sono bozze | `docs/legal/` |
| P5 | Listino deciso (BP-D1) e pagina prezzi | Oggi i prezzi sono a 0 e "non definiti" | `appsettings.json:74-84` |
| P6 | Pagamento con carta provato end to end su Stripe reale | Mai eseguito | `Sessions/specs/README.md:120` |

### 5.2 Sviluppo per le agenzie (PR #526 §7)

| Pacchetto | Settimane attive | Necessario al go-live di Quokka? |
|---|---|---|
| WP1 Fondamenta: `Agency`, ruoli, paese/valuta/fuso, tedesco | 1,2–2,0 | Sì |
| WP6 Compliance via API + file preparati | 1,3–2,1 | Sì (Alloggiati, Ticino) |
| WP7 Check-in biometrico, QR, videochiamata, serrature | 1,4–2,2 | Sì, se il consenso biometrico è predefinito nel B2B (R6) |
| WP5 Fornitori propri e staff | 0,4–0,7 | Sì |
| WP4 Proprietari: portale, rendiconti, SDI, SEPA, Stripe v2 | 1,3–2,0 | Sì: senza, l'agenzia non paga i proprietari |
| WP2 Channel manager: client, mappatura, contenuti, inbox, AI | 1,75–2,8 | **Sì: senza non si può lasciare Kross** |
| WP8 Routine | 1,4–2,25 | **Da chiedere a Quokka** se può arrivare dopo il go-live |
| WP3 CRM e campagne | 1,2–1,75 | **Da chiedere a Quokka** se può arrivare dopo il go-live |
| **Totale** | **~10–16 settimane attive** | |

**Correzioni che propongo alla stima:**
- La stima misura righe di codice (±40%). L'audit di settembre mostra che "scritto" non vuol dire "funzionante" (MVP reale al 35–40% quando era dichiarato completo, `PIANO-RISANAMENTO-2026-09.md:13-18`).
- Per il piano uso quindi **5–6 mesi di calendario** fino al go-live, non 2,5–4.
- **Il percorso critico non è lo sviluppo:** è la certificazione del channel manager, il PoC della verifica d'identità, la DPIA e un collaudo Alloggiati su una struttura reale (PR #526 §5.4, §7).
- **Taglio minimo per il go-live:** WP1, WP6, WP7, WP5, WP4, WP2. WP8 e WP3 dopo il go-live, se Quokka lo accetta. Si risparmiano 2,6–4 settimane sul percorso critico.

### 5.3 Calendario e cancelli

```
nov–dic 2026  Fase 0: P1–P6, contratto Quokka (cancello G0), risposte scritte NextPax/Octorate, PoC Didit, DPIA
gen–apr 2027  WP1 → WP6 + WP7 → WP5 → WP4 → WP2 ; collaudo su struttura reale (cancello G1)
mag 2027      Go-live Quokka [IPOTESI: finestra di bassa stagione tra lo sci (Grigioni) e l'estate (Ticino, laghi), da confermare]
giu–dic 2027  WP8, WP3; altri Founding Partner (cancello G2)
2028          Vendita a listino; B2C a pagamento (cancello G3); supporto (cancello G4)
```

**Rischio di calendario.** Le unità di Quokka sono sia in località sciistiche (Davos, Laax, Andermatt) sia estive (Ticino, laghi). Una migrazione si fa in bassa stagione **[IPOTESI da confermare con Quokka]**. Se il go-live salta la finestra di primavera, la successiva è in autunno: è lo **scenario B** in §9, con 6 mesi di ritardo.

---

## 6. Modello di ricavo e listino

### 6.1 Principi (confermati)

- **Solo canone** per unità, più opzioni e verifiche. Nessuna percentuale sulle prenotazioni (PR #526 D1; nessuna commissione sulle prenotazioni dirette anche nel codice, `StripeService.cs:172-175` ✓).
- **Tutti i costi Stripe a carico dei clienti** (R11): l'agenzia è l'esercente con `fees_collector = stripe`.
- **Pagamento annuale anticipato** come standard (+10% se mensile), come da prassi del mercato (sconto annuale 10–12%, PR #526 §6.1).

### 6.2 Listino B2B (PR #526 §6.2, confermato)

| Pacchetto | 10–49 unità | 50–149 | 150–399 | 400+ |
|---|---|---|---|---|
| Base (gestionale, channel manager, inbox con AI suggerita, sito, check-in, adempimenti via API, portale proprietari, routine, fornitori) | €9 | €8 | €7 | preventivo |
| + AI e Marketing | +€4 | +€4 | +€4 | |
| Verifica biometrica | €0,75 a verifica | | | |
| Serrature (via Seam) | €6 per serratura | | | |
| **Tutto compreso** (Base + AI e Marketing + 8 verifiche/unità) | **€15** | **€14** | **€13** | preventivo |

Minimo €90/mese.

**Osservazioni critiche:**
1. **Base + verifiche costa quanto il tutto compreso.** €9 + 8 × €0,75 = €15, cioè il prezzo del tutto compreso, che però include anche AI e Marketing. Nel B2B, dove la verifica è predefinita, nessuno comprerà la Base da sola. È coerente con l'obiettivo di vendere il tutto compreso, ma allora **il listino reale B2B è il tutto compreso** e la Base serve solo al segmento 2. Il modello usa il tutto compreso.
2. **Il margine dipende dalle verifiche.** Con Didit ($0,48 con NFC) il tutto compreso rende €5,61–7,61 per unità. **Con Sumsub ($1,35) va in perdita da 50 unità in su** (−€0,35 / −€1,35) e quasi a zero sotto (€0,65). → **BP-D6**: Didit, oppure si riduce il numero di verifiche incluse.
3. **Il numero di 8 verifiche per unità al mese** (4 prenotazioni × 2 adulti) è un'ipotesi di PR #526: va misurato sui dati reali di Quokka.

### 6.3 B2C: perché il listino piatto 29/79/199 non regge

| Problema | Dato |
|---|---|
| Non è competitivo oggi | Starter €29 per massimo 3 unità, **senza channel manager API e con Alloggiati manuale**, contro Kross Premium a €18 per 3 unità con entrambi automatici ✓ |
| È incoerente con il B2B | Pro €79 copre fino a 50 proprietà (`PlanCatalog.cs:31-33`): €1,58 per unità, contro €15 nel B2B. Un'agenzia piccola può scegliere il Pro; la separazione regge solo grazie alle funzioni riservate al B2B (portale proprietari, staff) |
| Non copre il channel manager | NextPax costa €3,15–3,50 per unità: 50 unità in Pro costano a CasaZen €177 contro un ricavo di €79 |
| Va contro la prassi | Tutti i concorrenti in §3.1 prezzano per unità o per proprietà |

**Proposta (BP-D1):**
- **Un solo listino per unità** per B2C e B2B, coerente con R8 ("prodotto unico, limiti per piano"):
  - Base a €9/8/7 per unità;
  - un **canone minimo per host da 1–3 unità**; nel modello €29 **[IPOTESI]**, da validare con 10 interviste;
  - le funzioni solo B2B restano il recinto.
- Il B2C a pagamento si apre **solo** quando channel manager e Alloggiati automatico sono in produzione (cancello G3).

### 6.4 Founding Partner (PR #526 §6.3): condizioni da correggere

**Quanto costa il programma.**
- Per un'agenzia da 40 unità il prezzo Founding Partner è €6,92 + verifiche al costo (€3,84) = **€10,76 per unità**, cioè il listino −28%.
- Lo sconto in sé è ragionevole. Il problema è la **durata "per tutto il rapporto"** con margine bloccato: CasaZen non beneficia mai dei ribassi di costo su quelle unità.
- I 9 posti oltre Quokka, a 40 unità ciascuno, rinunciano a **~€1.526/mese di contribuzione** rispetto al listino, per sempre.
- Il programma non ha precedenti consolidati sul mercato (PR #526 §6.1: "quasi assenti"). Non c'è quindi una prassi da seguire: è una scelta del PO.

**Proposta (BP-D2):**
- prezzo Founding Partner vincolato per **36 mesi**, poi listino con uno sconto fisso;
- numero di posti limitato;
- la quota di copertura dei costi fissi (€1,60 per unità, calcolata su Quokka) va ricalcolata per le agenzie piccole.

### 6.5 Offerta a Quokka

| Voce | €/mese | Fonte |
|---|---|---|
| Canone partner fisso (€6,92 × 250) | 1.730 | PR #526 §6.3 |
| Verifiche al costo (2.000 × $0,33–0,48) | 660–960 | idem |
| **Totale Quokka** | **2.390–2.690** | |
| Oggi a Kross | ~2.000 | dato del PO |

**Osservazioni critiche:**
- **Quokka pagherebbe il 20–35% in più** rispetto a oggi. L'argomento di vendita sono le funzioni in più (biometria, CH, proprietari, AI, CRM) e l'eventuale dismissione della sua piattaforma di marketing e CRM, il cui costo è sconosciuto (PR #526 §8 domanda 2). **Va verificato prima di costruire.**
- I €400 di "costi fissi coperti dal partner" erano calcolati sui costi della sola piattaforma. Con società, assicurazione, Vercel Pro e minimo NextPax, i fissi reali sono ~€995/mese senza marketing. **Quokka da solo porta CasaZen a −€3.500 circa l'anno** (scenario A, §9). Si accetta come investimento commerciale, oppure si alza la quota fissa (**BP-D3**).
- **Il contratto con Quokka è il cancello G0.** Prima di firmare con NextPax e di costruire WP2 servono:
  - un contratto firmato con durata minima di 24 mesi;
  - il pagamento annuale anticipato;
  - una penale di recesso anticipato;
  - una data di go-live condivisa.

  Senza, CasaZen costruisce 10–16 settimane di funzioni e si impegna su un minimo di €350/mese per un cliente non vincolato.

### 6.6 Altri ricavi

- **Serrature:** €6 contro $5 di costo, cioè ~€1 per serratura. Opzionale, escluso dal modello (prudenza).
- **Verifiche oltre le 8 incluse:** €0,75 contro $0,48, cioè ~€0,27 a verifica. Escluso.
- **Marketplace fornitori:** congelato, commissione incoerente (10% contro 3,5%), DAC7 ⚖. **Escluso dal piano a 36 mesi.** Da decidere a parte se riattivarlo (**BP-D9**).
- **Locazioni lunghe** (ConcordatoFacile, RLI): da valutare come opzione per le agenzie dopo il go-live. Nessun ricavo nel modello.

---

## 7. Go-to-market

| Fase | Obiettivo | Canale | Chi |
|---|---|---|---|
| Fase 0–1 (nov 2026–mag 2027) | Quokka firmato e live | Vendita diretta del fondatore | PO |
| Fase 2 (giu 2027–ott 2028) | 9 Founding Partner, ~40 unità ciascuno | Referenza e caso di studio Quokka; agenzie nelle stesse località (Ticino, Grigioni, laghi lombardi); contenuti sugli adempimenti (CIN, CdS 9101/2025, L. 199/2025) | PO |
| Fase 3 (da nov 2028) | ~1 agenzia al mese a listino | Pagina prezzi, prova guidata, referral dai Founding Partner, commercialisti | PO + supporto |
| B2C | Riapertura dopo il cancello G3 | Pagine SEO per comune (già esistenti, pilota Lombardia), registrazione libera | Automatico |

**Vincolo reale: la capacità del fondatore.** Vendita, onboarding, supporto e guida dello sviluppo AI ricadono su una persona part-time. Ogni agenzia nuova richiede migrazione dei dati, mappatura dei canali e formazione.
- Il ritmo dello scenario obiettivo (0,6 Founding Partner al mese, poi 1 agenzia al mese) è il **massimo sostenibile senza assunzioni [IPOTESI]**.
- Più velocità richiede un budget di vendita, che nel modello è solo un segnaposto da €300/mese.

---

## 8. Operazioni e organizzazione

| Tema | Oggi | Necessario dal primo cliente B2B |
|---|---|---|
| Sviluppo | PO + 1 abbonamento Claude Code (~€200/mese [NV], PR #526 §5.1) | Invariato. Processo di review e test già maturo (6.000+ test) |
| Supporto | Nessuno | Canale e orari scritti nello SLA; supporto in IT/EN/DE per la CH tedesca [IPOTESI] |
| Reperibilità | Nessuna | Un channel manager che si ferma causa overbooking all'agenzia: serve monitoraggio con allarmi e una procedura per gli incidenti (P3) |
| Continuità | Persona chiave unica | Documentazione operativa (i runbook esistono già), accessi di emergenza, backup verificati |
| Fornitori critici | — | NextPax/Octorate, Didit, Stripe, Supabase, Resend: contratti, DPA e piano di uscita per ciascuno |
| Assunzioni | — | **Primo supporto a ~1.000 unità B2B [IPOTESI]**; fondatore full-time quando i ricavi lo sostengono (cancello G5) |

---

## 9. Piano economico-finanziario (36 mesi, nov 2026 – ott 2029)

Fonte: `modello/modello_economico.py`. L'Excel `modello/modello-economico.xlsx` ha le stesse formule, con le ipotesi modificabili; `modello/verifica_xlsx.py` controlla che i due diano lo stesso risultato. Importi IVA esclusa, senza imposte sul reddito.

### 9.1 Contribuzione per unità B2B (€/mese)

| Voce | Founding Partner | Listino 10–49 | Listino 50–149 | Listino 150–399 |
|---|---|---|---|---|
| Ricavo | 10,76 | 15,00 | 14,00 | 13,00 |
| NextPax (€3,15) + SDI, AI, video (€0,40) | −3,55 | −3,55 | −3,55 | −3,55 |
| Verifiche (8 × €0,48) | −3,84 | −3,84 | −3,84 | −3,84 |
| **Contribuzione** | **3,37** | **7,61** | **6,61** | **5,61** |
| Margine lordo | 31% | 51% | 47% | 43% |
| Contribuzione con Sumsub | −3,59 | 0,65 | −0,35 | −1,35 |

B2C (canone €29, 2 unità con channel manager): contribuzione €21,90, pari al 76% **[IPOTESI]**.

### 9.2 Costi fissi (€/mese)

| Voce | Prima del go-live | In produzione | Fonte |
|---|---|---|---|
| Hosting (Railway Hobby → Railway Pro + Supabase Pro) | 5 | 85 | PR #526 §5.1, §5.4 |
| Resend | — | 90 | idem |
| Claude Code | 200 | 200 | idem [NV] |
| Vercel Pro | — | 20 | `free-hosting-analysis.md:252` |
| Marketing e vendite | — | 300 | **[IPOTESI segnaposto]** |
| Commercialista, PEC, banca | 150 | 150 | **[IPOTESI]** (stima interna, skill financial-strategist) |
| Assicurazioni RC professionale e cyber | 100 | 100 | **[IPOTESI segnaposto]**, da preventivo |
| Minimo NextPax (50 unità incluse) | — | 350 | PR #526 §5.1; da negoziare con decorrenza dal go-live |
| **Totale** | **~€455** | **~€1.295** | |
| Una tantum: pareri (DPIA, DAC7, Reg. 2024/1028), contratti, revisione ToS/DPA ⚖ | €6.000 a gen 2027 | | **[IPOTESI segnaposto]** |

**Non inclusi:**
- il tempo del fondatore;
- la costituzione della società (dipende da BP-D4);
- il monitoraggio a pagamento (strumento da scegliere);
- Apple Developer ($99/anno), finché l'app non va negli store;
- le imposte.

### 9.3 Scenari

**Ipotesi comuni:**
- prezzi e costi come §9.1–9.2;
- agenzie da 40 unità **[IPOTESI]**;
- Founding Partner vincolati 24 mesi, senza churn;
- churn mensile delle agenzie a listino 1% **[IPOTESI]**;
- churn mensile B2C 3% (centro della banda 2–5% del piano di giugno);
- fondatore non pagato.

| Scenario | Ipotesi specifiche |
|---|---|
| **A. Solo Quokka** | Go-live mag 2027, nessun altro cliente |
| **B. Ritardo** | Go-live nov 2027; dal feb 2028 un Founding Partner ogni 4 mesi e 2 host B2C al mese |
| **C. Obiettivo** | Go-live mag 2027. Dal lug 2027, 0,6 Founding Partner al mese (9 entro set 2028). Da nov 2028, 1 agenzia al mese a listino. Da ago 2027, 6 host B2C al mese. Supporto oltre 1.000 unità |

| | A. Solo Quokka | B. Ritardo | C. Obiettivo |
|---|---|---|---|
| Unità B2B a ott 2027 / ott 2028 / ott 2029 | 250 / 250 / 250 | 0 / 340 / 460 | 346 / 610 / 1.064 |
| Agenzie a ott 2029 | 1 | 6 | 21 |
| Host B2C a ott 2029 | 0 | 32 | 112 |
| Ricavi anno 1 / 2 / 3 | €16k / €32k / €32k | €0 / €40k / €61k | €20k / €82k / €157k |
| Margine lordo anno 3 | €7,8k | €20,6k | €70,4k |
| **EBITDA anno 1 / 2 / 3** | **−€10,3k / −€3,5k / −€3,5k** | **−€11,2k / −€0,2k / +€9,3k** | **−€8,7k / +€19,6k / +€53,0k** |
| Ricavi mensili a ott 2029 | €2.690 | €5.863 | €16.632 |
| Cassa cumulata minima | −€15,6k | −€11,4k | −€8,5k |
| Pareggio mensile stabile | mai | mag 2028 | set 2027 |

**Lettura:**
- **Con il fondatore non pagato il fabbisogno di cassa è piccolo** (€8–16k), perché lo sviluppo costa un abbonamento e il pagamento annuale anticipato di Quokka (~€20.800) finanzia i primi mesi.
- **Il vero investimento è il tempo del fondatore,** che il modello non valorizza.
- **Lo scenario A non sta in piedi:** Quokka da solo è un'operazione in perdita. Il programma ha senso solo se dopo Quokka arrivano altre agenzie.

### 9.4 Pareggio e compenso del fondatore

Unità a listino (€15, contribuzione €7,61) necessarie per coprire:

| Costo mensile da coprire | Unità | Agenzie da 40 unità |
|---|---|---|
| Solo costi fissi (€1.295) | 170 | 4 |
| + fondatore €3.000 | 564 | 14 |
| + fondatore €6.000 | 959 | 24 |
| + fondatore €6.000 + 1 supporto €3.000 | 1.353 | 34 |

I compensi sono livelli di esempio, non una proposta: il dato reale dipende da BP-D5.

### 9.5 Sensibilità sullo scenario C

| Variante | EBITDA anno 3 | Cassa cumulata minima | Pareggio mensile stabile |
|---|---|---|---|
| Base (fondatore non pagato) | +€53,0k | −€8,5k | set 2027 |
| Fondatore €3.000/mese dal go-live | +€17,0k | −€38,1k (apr 2028) | dic 2028 |
| Fondatore €6.000/mese dal go-live | −€19,0k | **−€110,0k** (apr 2029) | non raggiunto |
| Verifiche con Sumsub ($1,35) | +€32,1k | −€8,5k | set 2027 |
| Agenzie a listino dimezzate (0,5/mese) | +€47,6k | −€8,5k | set 2027 |
| Nessun B2C | +€27,9k | −€8,5k | ott 2027 |

**I fattori che contano, in ordine:**
1. il compenso del fondatore;
2. il numero di agenzie, sia Founding Partner sia a listino;
3. il fornitore di verifica;
4. il B2C (vale circa la metà dell'EBITDA del terzo anno nello scenario C, ma è il dato meno validato).

### 9.6 Fabbisogno finanziario e fonti

| Ipotesi sul fondatore | Fabbisogno (cassa minima) | Fonte coerente |
|---|---|---|
| Non pagato | ~€10–16k | Mezzi propri + anticipo annuale di Quokka |
| €3.000/mese dal go-live | ~€40k | Mezzi propri o prestito; finanziamenti pubblici solo come integrazione |
| €6.000/mese dal go-live | ~€110k e oltre, senza pareggio a 36 mesi | Capitale esterno. **Non coerente** con il ritmo commerciale dello scenario C |

**Finanziamenti pubblici:**
- Smart&Start, credito d'imposta R&S e startup innovativa restano opzioni per una società italiana.
- Sono lenti (3–12 mesi) e non vanno contati per il fabbisogno dei primi mesi (piano di giugno §12).

### 9.7 Ipotesi da validare (in ordine di impatto)

1. **Disponibilità di Quokka a pagare €2.390–2.690** contro i ~€2.000 attuali, e il costo della sua piattaforma di marketing e CRM.
2. **Numero reale di verifiche per unità al mese** (dati di Quokka: prenotazioni e ospiti al mese).
3. **Unità medie per agenzia e numero di agenzie raggiungibili:** nessun dato ufficiale; serve una lista di prospect reali.
4. **Costi reali di NextPax** (minimo dal go-live? sovrapprezzo per i messaggi?) e di Octorate (Allegato A di PR #526).
5. **Costi societari, assicurativi e legali:** preventivi.
6. **Churn** di agenzie e host: da misurare dal primo cliente.
7. **Disponibilità a pagare del B2C** con un canone minimo per unità: 10 interviste prima della riapertura.

---

## 10. Aspetti legali, fiscali e societari ⚖

| Tema | Stato e rischio | Azione |
|---|---|---|
| Società | Non esiste. La forma (SRL/SRLS in Italia, oppure un veicolo svizzero) dipende dalla residenza del fondatore e dai clienti (IT+CH). Il piano di giugno escludeva il forfettario per una società | Parere del commercialista (**BP-D4**) |
| IVA sulle vendite | B2B italiano: 22% con SDI. B2B svizzero: servizio fuori dal campo IVA italiana (art. 7-ter DPR 633/72) e imposta svizzera a carico del cliente ⚖ [NV]. B2C UE oltre €10k: OSS | Parere; build già previsto (`spec-saas-billing` AC7b) |
| Pagamenti ospiti | L'agenzia è l'esercente: CasaZen **non** è sostituto d'imposta né responsabile della tassa di soggiorno per l'incasso (PR #526 §3.3) | Restano i pareri DAC7 e Reg. 2024/1028 sul sito di prenotazione |
| Biometria | GDPR art. 9: consenso e alternativa equivalente obbligatoria; DPIA; cancellazione delle immagini alla ricevuta Alloggiati (nota del Garante 29/04/2026); CdS 9101/2025 | DPIA prima del go-live (cancello G1) |
| Svizzera | Ticino: divieto di fotocopiare il documento (RL 942.120 art. 6, PR #526 §3.5); legge federale sulla protezione dei dati (nLPD) per i dati CH [NV] | Verifica legale del flusso CH |
| Responsabilità | Le responsabilità di legge restano all'agenzia; CasaZen promette "compliance automatizzata" | Limitazioni di responsabilità e SLA nei contratti |
| Contratti | ToS, DPA, SLA, contratto Founding Partner, contratti fornitori (NextPax, Didit) con DPA e sub-responsabili | Revisione legale (P4) |

---

## 11. Rischi

| # | Rischio | Prob./Impatto | Mitigazione |
|---|---|---|---|
| R1 | **Quokka non firma** o firma dopo che lo sviluppo è partito | Media / Alto | Cancello G0: niente WP2 e niente NextPax senza contratto; WP1, WP6 e WP7 sono riusabili anche per altri clienti |
| R2 | **Concentrazione:** Quokka = 100% dei ricavi per 6–12 mesi | Alta / Alto | Durata minima 24 mesi + penale; Founding Partner dal 2° mese dopo il go-live |
| R3 | Il go-live salta la finestra di bassa stagione | Media / Medio | Taglio minimo (WP8 e WP3 dopo); partire subito sulle dipendenze esterne. Scenario B |
| R4 | **Fornitore channel manager:** acquisito, cambia prezzi o vende direttamente alle agenzie | Media / Alto | Due fornitori in PoC (NextPax, Octorate); clausola di non sollecitazione (Allegato A domanda 9); astrazione `IChannelAdapter` |
| R5 | Margine schiacciato dal costo delle verifiche | Media / Alto | Didit; numero di verifiche incluse legato all'uso misurato |
| R6 | **Persona chiave unica:** fondatore part-time per sviluppo, vendita e supporto | Alta / Alto | SLA realistici; monitoraggio; runbook; assunzione del supporto a soglia; accessi di emergenza |
| R7 | Incidente in produzione su un cliente da 250 unità (overbooking, dati) | Media / Alto | P2 + P3; assicurazione cyber; procedura per gli incidenti; test end to end prima del go-live |
| R8 | Concorrenti finanziati (Guesty, Hostaway) che puntano all'Italia | Alta / Medio | Nicchia agenzie IT+CH; prezzo tutto compreso sotto la somma dei fornitori; relazione diretta |
| R9 | Norme: biometria contestata, CdS 9101/2025, TU IVA 2027, Reg. 2024/1028 | Media / Medio | Alternativa in videochiamata; pareri; riverifica delle norme prima dello sviluppo |
| R10 | Stima di sviluppo ottimistica (±40%; storico di funzioni "scritte ma non funzionanti") | Alta / Medio | 5–6 mesi nel piano; collaudo su dati reali; cancello G1 |
| R11 | Prezzi instabili (tre cambi in cinque giorni) | Alta / Medio | BP-D1 come decisione unica, pubblicata e versionata |

---

## 12. KPI e cancelli decisionali

**KPI mensili:**
- unità sotto contratto e unità live;
- ricavi ricorrenti mensili;
- contribuzione per unità;
- margine lordo %;
- verifiche per unità;
- costo NextPax per unità;
- churn (logo e unità);
- ticket per 100 unità;
- disponibilità del servizio;
- percentuale di schedine Alloggiati inviate entro 24 ore;
- incidenti.

| Cancello | Condizione per passare | Cosa sblocca |
|---|---|---|
| **G0** | Contratto Quokka firmato (24 mesi, annuale anticipato, penale, data di go-live); società costituita; risposte scritte di NextPax/Octorate | Firma NextPax, sviluppo WP2 |
| **G1** | Collaudo su struttura reale (Alloggiati, Ticino); DPIA; P2–P4 chiusi; migrazione dei dati provata | Go-live Quokka |
| **G2** | 60 giorni di Quokka live senza incidenti gravi; contribuzione per unità misurata ≥ quella del piano | Founding Partner 2–10 |
| **G3** | Channel manager API e Alloggiati automatico in produzione; listino per unità pubblicato | B2C a pagamento |
| **G4** | ~1.000 unità B2B, oppure ticket oltre la capacità del fondatore | Assunzione del supporto |
| **G5** | Contribuzione ≥ costi fissi + compenso del fondatore per 3 mesi consecutivi | Fondatore full-time |

---

## 13. Decisioni richieste al PO

| ID | Decisione | Proposta | Impatto |
|---|---|---|---|
| **BP-D1** | Listino | Un solo listino per unità per B2C e B2B (Base €9/8/7; tutto compreso €15/14/13; canone minimo per gli host piccoli), al posto di 29/79/199 piatti | Sostituisce le decisioni dell'8/10, del 9/10 e del 13/10; chiude P5 |
| **BP-D2** | Founding Partner | Prezzo vincolato 36 mesi (non "per tutto il rapporto"), posti limitati, quota fissa ricalcolata per le agenzie piccole | ~€1.500/mese di contribuzione in più a regime |
| **BP-D3** | Quokka | Accettare circa −€300/mese finché Quokka è l'unico cliente, oppure alzare la quota fissa del canone partner | Scenario A |
| **BP-D4** | Società | Paese e forma (parere del commercialista) | Sblocca P1 e tutti gli incassi |
| **BP-D5** | Fondatore | Impegno (part-time / full-time) e compenso obiettivo | Determina il pareggio (§9.4) e il fabbisogno (§9.6) |
| **BP-D6** | Verifica d'identità | Didit (Sumsub rende il tutto compreso in perdita) | §9.1 |
| **BP-D7** | Regola "zero costi" | Superata dal primo cliente: ~€455/mese prima del go-live, ~€1.295 dopo | P2, P3 |
| **BP-D8** | Taglio del go-live | WP8 (routine) e WP3 (CRM) dopo il go-live, se Quokka accetta | −2,6/−4 settimane sul percorso critico |
| **BP-D9** | Marketplace | Fuori dal piano a 36 mesi; allineare la commissione in configurazione (10% contro 3,5%) | Coerenza tra codice e documenti |
| **BP-D10** | Svizzera | Limitare le funzioni CH ai cantoni e comuni reali di Quokka | Riduce WP1/WP6 |

---

## Allegato A: fonti principali (consultate il 2026-10-15)

**Mercato Italia:**
- BDSR, totale nazionale: <https://api-bdsr.ministeroturismo.gov.it/ui/user/getTotaleNazione> (cruscotto <https://bdsr.ministeroturismo.gov.it/>)
- BDSR, export CSV: <https://api-bdsr.ministeroturismo.gov.it/ui/user/downloadCsvDashboard>
- Ministero del Turismo, comunicato di dicembre 2024 sui CIN: <https://www.ministeroturismo.gov.it/affitti-brevi-ministero-turismo-rilasciato-oltre-il-70-dei-cin-per-le-strutture-censite/>
- Ministero del Turismo, FAQ BDSR: <https://www.ministeroturismo.gov.it/faq-banca-dati-strutture-ricettive-bdsr/>
- ISTAT, Statistica Today Turismo 2024: <https://www.istat.it/wp-content/uploads/2026/03/Statistica-Today_Turismo-2024.pdf>
- ISTAT, Annuario 2025 cap. 19: <https://www.istat.it/storage/ASI/2025/capitoli/C19.pdf>
- ISTAT, Stat Flash IV trimestre 2025: <https://www.istat.it/wp-content/uploads/2026/03/Stat_Flash_IV_Trim_2025.pdf>
- Eurostat, notti su piattaforme online: <https://ec.europa.eu/eurostat/databrowser/view/tour_ce_oar/default/table>
- Eurostat, notti su piattaforme per NUTS3: <https://ec.europa.eu/eurostat/databrowser/view/tour_ce_oan3/default/table>
- Unioncamere-InfoCamere, imprese ATECO 55.20: <https://www.infocamere.it/dam/jcr:253ab453-b6cd-4fc4-9bcc-cfb8486c3b79/2025_12_29_Com_alberghi%20e%20ristoranti.pdf>
- CleanBnB, comunicato H1 2026: <https://www.emarketstorage.it/sites/default/files/comunicati/2026-07/20260706_186642.pdf>
- CleanBnB, comunicato bilancio 2025: <https://www.emarketstorage.it/sites/default/files/comunicati/2026-03/20260331_180920.pdf>
- Anima, operazione Halldis: <https://animasgr.it/d/EN/downloads/HoldingComunicatiFinanziari/Comunicato%20Operazione%20Halldis_ENG.pdf>

**Mercato Svizzera:**
- BFS, comunicato 2026-0358: <https://dam-api.bfs.admin.ch/hub/api/dam/assets/36633039/master>
- BFS, dati 2024: <https://dam-api.bfs.admin.ch/hub/api/dam/assets/35995400/master>
- Ticino Turismo, tassa di soggiorno: <https://www.ticino.ch/en/hospitality/opportunita/tasse/alloggio.html>

**Norme:**
- Agenzia delle Entrate, guida locazioni brevi (aprile 2026): <https://www.agenziaentrate.gov.it/portale/documents/d/guest/locazioni_brevi_disciplina_fiscale_e_regole_per_intermediari_aprile-2026>
- Agenzia delle Entrate, DAC7: <https://www.agenziaentrate.gov.it/portale/scambio-automatico-di-informazioni-comunicate-dai-gestori-di-piattaforme-dac7/infogen-scambio-automatico-di-informazioni-comunicate-dai-gestori-di-piattaforme-dac7-imprese>
- Reg. UE 2024/1028: <http://publications.europa.eu/resource/celex/32024R1028>
- Consiglio di Stato 9101/2025: <https://www.giustizia-amministrativa.it/en/-/105486-1372>
- TAR Lazio 10210/2025: <https://www.giustizia-amministrativa.it/en/-/105486-1036>
- Circolare del Viminale (Questura di Torino): <https://questure.poliziadistato.it/it/Torino/articolo/5730ddc674834729711003>

**Concorrenti:**
- Kross Booking: <https://www.krossbooking.com/prezzo>
- Vikey: <https://www.vikey.it/prezzi/>
- Chekin: <https://chekin.com/en/pricing/>
- Octorate: <https://www.octorate.com/prezzi/>
- Smoobu: <https://www.smoobu.com/it/prezzi/>
- Guesty Lite: <https://www.guesty.com/guesty-lite/>
- Guesty, prezzi: <https://www.guesty.com/pricing/>
- Hostaway, prezzi: <https://www.hostaway.com/pricing/>
- Beds24: <https://beds24.com/pricing.html>
- Guesty, Serie F: <https://www.prnewswire.com/news-releases/guesty-raises-130m-to-accelerate-global-expansion-302112918.html>
- Guesty, acquisizione di Smily: <https://tools.prnewswire.com/en-us/live/20813/release/20260917EN49475>
- Hostaway, raccolta da $365 M: <https://hostaway.com/blog/hostaway-raises-365-million>

**Prezzi e costi dei fornitori e stime di sviluppo:** fonti di PR #526, Allegato B.

**Non trovato su fonte ufficiale:**
- numero di imprese ATECO 68.32;
- appartamenti di vacanza dei soli Grigioni;
- registro cantonale ticinese;
- prezzi ufficiali di Lodgify, Hospitable, Avaibook, Smily, WuBook e Scidoo;
- comunicati su Vikey/Zucchetti e HomeToGo/Smoobu.

## Allegato B: modello

```bash
cd Sessions/business-plan-2026-10/modello
python3 modello_economico.py                       # tabelle di §9
python3 modello_economico.py --xlsx modello-economico.xlsx
python3 verifica_xlsx.py modello-economico.xlsx    # ricalcola con LibreOffice e confronta
```

**Come usare l'Excel:**
- foglio *Ipotesi*: celle gialle modificabili; lo scenario attivo si sceglie in `C4`;
- foglio *Mensile*: i 36 mesi;
- foglio *Sintesi*: i totali annui.
