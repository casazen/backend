# Tipo host "Agenzia" (B2B): analisi delle 6 funzionalità richieste

**Versione:** v3, 2026-10-15. Aggiunge le decisioni raccolte nell'intervista al PO (§6). Sostituisce v2 (2026-10-15) e v1 (2026-10-14).
**Origine:** requisiti indispensabili posti dal prospect Quokka 360 per proseguire la collaborazione.
**Tipo:** analisi pre-vendita, nessuna modifica di codice.
**Metodo:** analisi del codice di `backend`, `frontend` e `mobile` e ricerca su fonti ufficiali (OTA, Stripe, enti pubblici, autorità privacy, documentazione dei fornitori).

**Legenda:**
- **[NV]** = non verificato su fonte ufficiale.
- **⚖** = serve un parere legale o fiscale prima del go-live.

## Regole di prodotto (decise dal PO)

| # | Regola |
|---|---|
| R1 | Quokka è un **cliente agenzia**. CasaZen resta il fornitore del servizio: nessuna personalizzazione per un singolo cliente, ogni funzionalità è di prodotto. |
| R2 | Il B2B è un **nuovo tipo di host (`Agency`)** con una propria gestione, inclusa la fatturazione tra agenzia, CasaZen, ospite (prenotazione diretta) e proprietari. **Tutti i flussi economici passano da Stripe.** |
| R3 | Non si replica Kross Booking. Le 6 funzionalità sono **condizione necessaria** per la collaborazione. |
| R4 | Ogni flusso è **automatico via API esterne**. Nessun flusso manuale, tranne la firma dei contratti. |
| R5 | Chat AI e **modifica dei contenuti** degli annunci sono **obbligatorie** nell'API del fornitore OTA. Un fornitore che non le offre è scartato. |
| R6 | Check-in online con **riconoscimento del documento e riconoscimento facciale**. |
| R7 | Delegare il più possibile a terzi gli aspetti fiscali e legali (Stripe o altri fornitori). |
| R8 | **Prodotto unico B2C + B2B.** OTA con chat AI, CRM, fiscalità e routine sono disponibili a entrambi, con limiti per piano. Portale proprietari e staff/fornitori propri sono solo B2B. |
| R9 | Riconoscimento facciale **obbligatorio nel B2B, opzionale nel B2C**. |
| R10 | Nel B2B i **fornitori CasaZen sono sempre il default**. Staff e fornitori propri sono un'opzione da attivare in configurazione. I fornitori propri sono registrati in CasaZen; quando li usa la loro agenzia: 0% di commissione e fatturazione fuori da CasaZen (D7). |

---

## 0. Sintesi: dove le regole si scontrano con vincoli esterni

Tre regole **non sono realizzabili alla lettera** per vincoli esterni (legge, Stripe, enti pubblici). Per ciascuna propongo la variante più vicina e lecita.

| Regola | Vincolo esterno verificato | Variante proposta |
|---|---|---|
| **R6/R9: riconoscimento facciale obbligatorio** | GDPR art. 9: il dato biometrico richiede consenso esplicito e **non si può condizionare il servizio al consenso**. EDPB Linee guida 3/2019 §86: va offerta un'alternativa *"without restraints or additional cost"*. Il Garante ha sanzionato con €50.000 un consenso biometrico "obbligatorio" (eCampus, 29/01/2026). In Svizzera vale lo stesso (nLPD art. 6 + guida EDÖB). **Check-in online obbligatorio** (dati + documento). Il **riconoscimento facciale è il percorso predefinito**, tramite **API di terzi** (D5: sviluppo interamente in casa non realistico), con consenso esplicito e un'**alternativa equivalente** in videochiamata. Vedi §3.6 |
| **R6, validità del check-in in Italia** | Consiglio di Stato n. 9101/2025: il controllo deve avvenire *"hic et nunc"* all'**ingresso della struttura**. Un selfie fatto prima dell'arrivo non basta. | Verifica facciale **all'arrivo**, tramite QR rotante esposto all'ingresso. Parere scritto del Viminale prima di eliminare l'operatore umano ⚖ |
| **R2: tutti i flussi in Stripe** | I payout di Airbnb e Booking.com arrivano sul conto bancario dell'agenzia, **fuori da Stripe**. L'unico strumento Stripe per portarli dentro è Treasury for platforms: Italia in *private preview*, solo EUR, **Svizzera non disponibile**. | Le prenotazioni dirette passano interamente in Stripe (D2). Per i ricavi OTA la decisione D3 è **in attesa di verifica del PO**: opzioni con processo, costi e responsabilità in §3.3 |
| **R4: nessun flusso manuale** | **Non esiste un'API** per: dichiarazioni AdE (imposta di soggiorno annuale, CU/770, DAC7, dati locazioni brevi); notifica ospiti alla polizia in **Ticino** (solo caricamento di file); tassa di soggiorno di Como, Trapani, Tropea, Genova, Portofino, S. Margherita, Cinque Terre e delle OTR ticinesi; F24 (solo per conti Banca Sella o tramite intermediario Entratel); verifica CIN. | **D6, deciso:** l'utente fa **tutto da CasaZen e riceve in CasaZen la ricevuta** dell'operazione. Dove non c'è API agisce un intermediario partner (AdE, F24) oppure un back-office CasaZen o del partner come utente delegato sui portali (comuni, Ticino). I costi extra sono accettati. Dettaglio in §3.5 |

### Le 6 funzionalità in breve

| # | Funzionalità | Soluzione | Fornitori |
|---|---|---|---|
| 1 | OTA: contenuti, ARI, chat AI | Channel manager via API. **Unico fornitore che dichiara contenuti + messaggi per tutte e 4 le OTA: NextPax** (da confermare per iscritto). **Alternativa: Octorate** (Airbnb + Booking.com + Vrbo; manca la gestione dei contenuti su Expedia). Channex e Rentals United sono **scartati** (R5) | NextPax / Octorate |
| 2 | CRM e campagne AI | CRM nel nostro DB, invio via Resend (team separato) o SES, WhatsApp via Meta Cloud API | Resend / SES / Meta |
| 3 | Portale proprietari e fatturazione | Ruolo `PropertyOwner`, rendiconti automatici, CasaZen incassa e Stripe ripartisce (D2), documenti fiscali generati da CasaZen per conto di ciascun emittente con invio SDI via API | Stripe Connect; Invoicetronic / A-Cube / Openapi |
| 4 | Fornitori e staff propri | Default: marketplace CasaZen con pagamento Stripe e commissione. Opzione in configurazione: fornitori propri registrati in CasaZen e legati all'agenzia (0%, fatturazione fuori da CasaZen) + staff | — |
| 5 | Fiscalità IT + CH | Build dove c'è un'API ufficiale; intermediario partner per AdE e F24; back-office delegato per comuni e Ticino; ricevuta sempre in CasaZen (D6) | Polizia di Stato, Regioni, feratel, intermediario Entratel, Openapi pagoPA |
| 6 | Routine AI e check-in | Motore di attività + AI; check-in biometrico via API di terzi con alternativa video (D5) | Didit / Sumsub (PoC); serrature via Seam/Nuki/igloohome |

**Stima con processo AI (1 abbonamento Claude Code Max 20x + PO):** circa **20 settimane attive** (intervallo 14–35), cioè ~5 mesi (3,5–8), test sull'interfaccia e piccoli refactor umani inclusi. **Il percorso critico sono i tempi esterni** (parere fiscale, Stripe, fornitori, enti), non lo sviluppo. Metodo e dettaglio in §7.

---

## 1. Modello B2C / B2B

### 1.1 Cosa cambia nel codice

- **`OrgType`** (`Casazen.Core/Entities/Enums/OrgType.cs`) è *append-only* e oggi vale `Host` o `Supplier`. Va aggiunto **`Agency`** in coda.
- **Nuovi ruoli** in `OrgRole`:
  - `PropertyOwner`, il proprietario dell'immobile. È diverso da `Owner`, che oggi indica il titolare dell'organizzazione.
  - `Staff`, con sottotipi pulizie, manutenzione e check-in.
- **Prerequisiti multi-paese:**
  - `Property.CountryCode` e giurisdizione (comune IT oppure comune + cantone CH);
  - valuta per proprietà: oggi `"EUR"` compare scritto a mano in 23 punti del C#;
  - lingua DE;
  - `Europe/Zurich`, che ha le stesse regole di `Europe/Rome`.
  - Il modello oggi esiste solo nella spec congelata `spec-eu-compliance-es-fr.md`.

### 1.2 Matrice delle funzionalità

| Funzionalità | B2C (`Host`) | B2B (`Agency`) |
|---|---|---|
| Channel manager OTA + contenuti + chat AI | ✓ (limiti per piano: numero di unità, invio automatico AI) | ✓ |
| CRM e campagne | ✓ base | ✓ completo |
| Fiscalità IT + CH automatizzata | ✓ | ✓ + ritenute 21%, dati CU e rendiconti per proprietario |
| Routine AI | ✓ | ✓ + assegnazione allo staff proprio |
| Check-in online (dati + documento) | Obbligatorio | Obbligatorio |
| Riconoscimento facciale | Attivabile dall'host | Percorso predefinito, con consenso e alternativa video (§3.6) |
| Portale proprietari | — | ✓ |
| Prezzo CasaZen | Piani attuali | **Solo canone per unità** (D1): nessuna percentuale sulle prenotazioni |
| Fornitori marketplace CasaZen | ✓ | ✓ **default** |
| Staff e fornitori propri (registrati in CasaZen, 0%, fatturazione fuori da CasaZen) | — | Opzione in configurazione |
| Flusso Stripe | **Direct charge** sull'account dell'host (come oggi, `docs/runbooks/stripe.md:244`) | **CasaZen incassa e Stripe ripartisce** (separate charges and transfers; `on_behalf_of` = agenzia per le agenzie italiane). Commissioni Stripe ripartite in proporzione tra agenzia e proprietario (§3.3) |

---

## 2. Stripe e fiscalità: cosa fa Stripe, cosa si delega, cosa non si delega (R2, R7)

### 2.1 Cosa fa e cosa non fa Stripe

| Adempimento | Stripe | Fonte | A chi delegare |
|---|---|---|---|
| Calcolo IVA sulle fee CasaZen e sulle commissioni dell'agenzia (IT 22%, CH 8,1%) | **Sì**, Stripe Tax, anche sugli account collegati | <https://docs.stripe.com/tax/tax-for-platforms> | — |
| IVA sul soggiorno (aliquota alloggio IT; CH 3,8%) | **No**: nessun codice fiscale per alloggio/lodging nel catalogo | <https://docs.stripe.com/tax/tax-codes> | Aliquote manuali (Stripe Tax Rates) prese da `TaxRate`, da validare col commercialista ⚖ |
| Dichiarazione e versamento IVA IT/CH | **No**: la dichiarazione è disponibile solo per USA e pochi altri casi; Stripe *"doesn't automatically debit"* | <https://docs.stripe.com/tax/filing> | **Taxually** (app Stripe, IT e CH). In Svizzera resta un passaggio sul portale ESTV: non c'è un'API pubblica |
| Fattura elettronica IT (SDI) | **No**: Stripe *"doesn't generate or transmit legally compliant e-invoice files"* | <https://docs.stripe.com/invoicing/e-invoicing> | **A-Cube** (app Stripe, solo per l'account piattaforma). Per gli account delle agenzie: webhook `invoice.finalized` → API SDI (A-Cube / Invoicetronic / Openapi) |
| QR-bill CH | **No** | — | Libreria Codecrete.SwissQRBill (MIT). Con lo split Stripe serve solo se il proprietario deve pagare all'agenzia; meglio addebito Stripe (SEPA DD / TWINT) |
| Imposta/tassa di soggiorno | **No**: solo come riga separata della fattura | — | Nessun fornitore assume la responsabilità. Airbnb la riscuote solo sulle prenotazioni Airbnb e **non in Ticino, Grigioni e Uri** (<https://www.airbnb.com/help/article/2291>) |
| Ritenuta 21% + F24 + CU/770 | **No**: Stripe non versa imposte | — | Lo split trattiene il 21% nel saldo dell'agenzia; F24 e CU tramite intermediario Entratel (§3.5) |
| DAC7 | **Parziale, in preview**: raccoglie i TIN, XML solo con lo schema NL, *"Stripe accepts no liability"* | <https://docs.stripe.com/connect/platform-tax-reporting> | Fonoa, oppure file + intermediario |
| KYC/AML di agenzie e proprietari | **Parziale**: Stripe verifica per i propri obblighi, *"Don't rely on Stripe's verification…"* | <https://docs.stripe.com/connect/identity-verification> | — |
| Licenza di pagamento | **Sì**, a condizione che i fondi non siano mai *"in the possession or control of the platform"* | <https://stripe.com/guides/how-psd2-impacts-marketplaces-and-platforms> | — |

### 2.2 Responsabilità che per legge non si possono delegare

Un fornitore può **automatizzare** l'adempimento, ma la responsabilità resta in capo al soggetto indicato:

| Obbligo | Responsabile | Fonte |
|---|---|---|
| Identificazione e comunicazione degli ospiti (art. 109 TULPS) | Gestore | — |
| Responsabile del pagamento dell'imposta di soggiorno e dichiarazione annuale | Chi incassa | art. 4 c. 5-ter DL 50/2017 |
| Sostituto d'imposta per la ritenuta del 21% | Intermediario che *"interviene nel pagamento"* | art. 4 c. 5 DL 50/2017; Circ. 24/E/2017 |
| IVA | Soggetto passivo | — |
| DAC7 | Gestore di piattaforma | — |
| Notifica degli ospiti e Kurtaxe (CH) | Locatore | LStrI art. 16; diritto cantonale |

**Promessa commerciale corretta:** "compliance automatizzata", mai "compliance delegata".

**Norme che cambiano dal 1/1/2027** (lo sviluppo cadrà dopo questa data):
- **Testo unico IVA**, D.Lgs. 19/01/2026 n. 10: si applica dal 1/1/2027 e sostituisce il DPR 633/72 (verificato: <https://www.normattiva.it/eli/id/2026/01/30/26G00022/ORIGINAL>). Corrispondenze indicate dalla ricerca [NV]:
  - art. 21 (fattura) → art. 72;
  - art. 3 c. 3 (mandato) → art. 10 c. 3;
  - art. 15 (esclusioni dalla base imponibile) → art. 29.
- **Art. 4 DL 50/2017** (locazioni brevi, ritenuta, imposta di soggiorno): secondo la ricerca confluisce nel testo unico delle imposte sui redditi, D.Lgs. 117/2026, artt. 207–208 [NV].
- Prima di sviluppare, verificare il testo vigente di entrambe le norme.

---

## 3. Analisi per funzionalità

### 3.1 OTA: contenuti, disponibilità/tariffe/prenotazioni, chat AI (R5)

**Oggi:** solo iCal (reale). Gli adapter OTA sono stub congelati (D10). Non esistono entità messaggi.

**Connessione diretta alle OTA: chiusa.**
- Booking.com: *"we are pausing integrations with new connectivity providers"* (<https://connect.booking.com/>).
- Airbnb: accesso API solo a sua discrezione (<https://www.airbnb.com/terms/api>).

**Fornitori valutati secondo R5** (✅ documentato · ⚠️ parziale o condizionato · ❌ non supportato · ? non documentato):

| Fornitore | Contenuti (creazione + modifica) | Chat (lettura, invio, webhook) | Esito |
|---|---|---|---|
| **NextPax** Supply API (REST) | Airbnb ✅, Booking.com ✅, Vrbo ✅, Expedia: modifica ✅, creazione ?. Non è confermato pubblicamente che il "Full Content" sia attivabile **via API** né il dettaglio per singola OTA: lo Swagger è dietro login | ✅ su tutte e 4, con webhook | **Candidato 1**, previa conferma scritta |
| **Octorate** Integration Central (REST, OpenAPI pubblica) | Airbnb ✅, Booking.com ✅, Vrbo ✅, **Expedia ❌** | ✅ (webhook `CHAT_MESSAGE_RECEIVED`) | **Candidato 2**: copre le OTA principali; vende anche direttamente ai gestori |
| Rentals United | Creazione Booking.com/Expedia **solo da interfaccia** (*"you have to connect them via UI first"*) | ✅ (API ristretta dal 17/02/2026) | **Scartato** (R4 + R5) |
| Channex | ❌ creazione e modifica dei contenuti (roadmap 2027) | ✅ | **Scartato** (R5) |
| Zodomus | Airbnb solo collegamento | ❌ | Scartato |
| Beds24, Hostaway, Guesty, Smily, WuBook, Hospitable, Lodgify | Nessuna pubblicazione sulle OTA via API | Variabile | Scartati |

Fonti:
- NextPax: <https://nextpax.com/products/supply-api>, <https://support.nextpax.com/distribute-new-or-reactivated-properties-to-channels>, <https://nextpax.com/resources/manage-expedia-group-guest-messages-directly-in-nextpax>
- Octorate: <https://api.octorate.com/connect/rest/v1/integration/openapi.yaml>
- Rentals United: <https://docs.rentalsunited.com/hc/en-gb/articles/36955698514717>
- Channex: <https://channex.io/llms.txt>

**Costi NextPax:** €3,50/unità/mese (€3,15 con fatturazione annuale), con **minimo €350/mese** che include le prime 50 unità (<https://nextpax.com/nextpax-pricing>). Il prezzo della Supply API in white label è "contact us".

**Vincoli per l'AI di chat:**
- Airbnb: prima della prenotazione non si possono scambiare contatti né link; l'AI deve rimuoverli (<https://www.airbnb.com/help/article/2799>).
- Booking.com: cambi data e "cancel for less" vanno gestiti nell'extranet.
- AI Act art. 50: obbligo di trasparenza.
- Modalità: bozza + invio automatico solo sopra una soglia di confidenza e su argomenti consentiti, come Guesty, Hostaway e Hospitable.

**Svizzera:** e-domizil/Interhome (gruppo HomeToGo) usano NextPax; Google Vacation Rentals è disponibile. feratel Deskline e ticino.ch non sono documentati da nessuno dei due candidati.

**Raccomandazione**
1. Inviare a NextPax e Octorate le **11 domande** in allegato A e chiedere un **PoC in sandbox**: creazione di un annuncio nuovo via API su Airbnb e Booking.com, modifica di foto e descrizione IT/EN/DE, ciclo completo di un messaggio.
2. Scegliere il fornitore solo con le risposte scritte.
3. **Un solo adapter reale dietro `IChannelAdapter`**, da estendere con contenuti e messaggi. Rimuovere gli stub.

---

### 3.2 CRM con campagne e AI

Invariato rispetto alla v1, salvo la matrice B2C/B2B.

**Vincolo principale:** Airbnb vieta di usare i contatti degli ospiti per il marketing (<https://www.airbnb.com/help/article/2799>). Booking.com consente il marketing solo con un consenso raccolto direttamente dal gestore.

**Costruzione:**
- CRM nel nostro DB: profilo ospite, `ConsentRecord` per canale e base giuridica, segmenti, campagne, lista di soppressione per tenant.
- Invio: Resend con un **team separato per il marketing** (oppure SES con tenant).
- WhatsApp via Meta Cloud API con Embedded Signup (ogni agenzia o host è titolare del proprio account).
- AI: testi in IT/EN/DE/FR con approvazione umana; richiesta in linguaggio naturale → segmento con anteprima dei destinatari.

**Automazione (R4):** le campagne si inviano da sole; il consenso si raccoglie nel check-in online e nel checkout diretto.

---

### 3.3 Portale proprietari e flusso economico a 4 parti in Stripe (solo B2B)

**Oggi:**
- Stripe Connect Express, **direct charge** sull'account dell'host, senza commissione per la piattaforma.
- Stripe **sconsiglia le direct charge con gli account Express v1** (<https://docs.stripe.com/connect/charges>).
- Nessun ruolo proprietario, nessun rendiconto, SDI non configurato.

**Perché serve un altro tipo di addebito.** Con direct o destination charge un pagamento va a **un solo** account collegato. Stripe **non trasferisce da un account collegato a un altro**: i fondi passano sempre dalla piattaforma. Per dividere lo stesso pagamento tra agenzia, proprietario e CasaZen serve quindi **separate charges and transfers (SCT)** (<https://docs.stripe.com/connect/separate-charges-and-transfers>).

#### D2, deciso: CasaZen incassa, Stripe ripartisce (prenotazioni dirette)

**Decisione del PO:** CasaZen incassa e Stripe divide i corrispettivi, con il minor numero possibile di transazioni. Le commissioni Stripe si ripartiscono **in proporzione tra agenzia e proprietario**. CasaZen non trattiene nulla sulla prenotazione: si fa pagare **solo con il canone per unità** (D1).

| Parte | Oggetto Stripe | Note |
|---|---|---|
| CasaZen | Account piattaforma (IT): Billing per il canone per unità | Nessuna quota sulla prenotazione |
| Agenzia | Account collegato con **contratto di servizio completo** (`card_payments` + `transfers`) | **Esercente** (`on_behalf_of`) solo per le agenzie **italiane**. Con un'agenzia svizzera, `on_behalf_of` applica le tariffe svizzere (carte UE trattate come internazionali: 3,25% + CHF 0,30), quindi per le prenotazioni in EUR si omette |
| Proprietario | Account collegato con `transfers`. IT: contratto *recipient* ammesso; **CH: contratto completo** (i recipient non ricevono payout transfrontalieri) | **Payout mensile** (`interval=monthly`) |
| Ospite | **Un solo PaymentIntent** per prenotazione, sulla piattaforma, con `transfer_group` = prenotazione | Carte, SEPA DD, TWINT (CHF) |

**Sequenza (minimo numero di transazioni):**
1. **Un addebito** all'ospite.
2. **Due trasferimenti** con `source_transaction`, che partono quando l'addebito è regolato. Non serve saldo disponibile sulla piattaforma e Stripe non applica commissioni ai trasferimenti.
   - **al proprietario:** canone − commissione agenzia − ritenuta 21% (se dovuta) − sua quota proporzionale delle commissioni Stripe;
   - **all'agenzia:** il resto, cioè commissione + IVA + ritenuta da versare + tassa di soggiorno + pulizie − sua quota proporzionale delle commissioni Stripe.
3. **Un payout al mese** per ciascun account.
4. Rimborsi e contestazioni ricadono sulla piattaforma; si recuperano stornando i trasferimenti.

**Da non usare:** Stripe Invoicing (+0,4%), Stripe Tax sul soggiorno (non ha un codice per gli alloggi: si usano aliquote manuali), Instant Payouts (+1%).

**Costo Stripe, esempio verificato** (prenotazione €1.000; un proprietario con 4 prenotazioni al mese; 2 account attivi):

| Metodo di pagamento | Commissioni del mese | % dell'incassato |
|---|---|---|
| Carta EEA standard | €74,70 | 1,87% |
| SEPA Direct Debit | €15,10 | 0,38% |

**Il costo dipende dal metodo di pagamento, non dal numero di trasferimenti.** Accorpare i payout fa risparmiare solo €0,10 ciascuno. Conviene quindi proporre in evidenza il SEPA DD, che però si regola in circa 6 giorni lavorativi e lascia all'ospite 8 settimane per chiedere il rimborso.

Fonti:
- <https://stripe.com/en-it/pricing>
- <https://stripe.com/en-it/connect/pricing>
- <https://docs.stripe.com/connect/separate-charges-and-transfers>
- <https://docs.stripe.com/connect/charges>

**Vincoli Stripe verificati:**
- **"Stripe handles pricing" non è disponibile** con questo modello: con gli addebiti creati sulla piattaforma le commissioni sono sempre a carico della piattaforma. CasaZen le ribalta nello split.
- I fondi si trattengono **al massimo 90 giorni** in IT e CH (<https://docs.stripe.com/connect/account-balances>). Per una prenotazione pagata più di 90 giorni prima del soggiorno, i trasferimenti non possono aspettare il check-in.
- Sui trasferimenti EUR → proprietario CH c'è conversione di valuta; il tasso si può bloccare con le FX Quote. Tasso esatto [NV].

**"Delegare a Stripe la ownership" ha un limite ⚖.** Stripe copre la licenza di pagamento, ma **non gli obblighi fiscali**. Con i fondi che transitano dalla piattaforma resta il rischio che CasaZen sia considerata sostituto d'imposta (ritenuta 21%), responsabile della tassa di soggiorno e gestore di piattaforma ai fini DAC7: l'obbligo ricade su chi *"interviene nel pagamento"* (Circ. AdE 24/E/2017 §2.2.2), e la delega dell'incasso a terzi non lo trasferisce. Con `on_behalf_of` = agenzia e un contratto che nomina CasaZen incaricata dell'incasso per conto dell'agenzia il rischio si riduce. **Prima del go-live serve un parere fiscale o un interpello.**

#### Fattura unica: verifica richiesta dal PO

| Livello | Esito |
|---|---|
| **Stripe** | Possibile ma inutile: una Invoice con `on_behalf_of` si può ripartire con i trasferimenti (<https://docs.stripe.com/invoicing/connect>), ma costa +0,4% e **non è una fattura elettronica SDI** (D.Lgs. 127/2015). Meglio il PaymentIntent + documenti fiscali generati da CasaZen |
| **Italia** | **Una fattura copre un solo fornitore** (art. 21 DPR 633/72, art. 72 del TU IVA dal 2027): servono identità e P.IVA di chi rende la prestazione. CasaZen però può **emettere ogni documento per conto** di ciascun soggetto (fattura emessa da terzo, `SoggettoEmittente` = "TZ"), in automatico |
| **Svizzera** | Una sola fattura all'ospite è possibile se l'agenzia agisce **in nome proprio** (MWSTG art. 20): alloggio al 3,8% fino al 31/12/2027; la Kurtaxe esposta a parte è esente; fattura solo su richiesta |

**Insieme minimo di documenti in Italia** (agenzia con mandato con rappresentanza):

| Quando | Documenti |
|---|---|
| Per prenotazione | Nessun documento fiscale obbligatorio all'ospite se l'agenzia fattura i suoi servizi al proprietario; una e-fattura dell'agenzia solo se fattura all'ospite |
| Ogni mese | Una fattura agenzia → proprietario (differita, entro il 15 del mese successivo) + una fattura CasaZen → agenzia (canone) |
| Ogni anno | CU al proprietario per la ritenuta |

Tutti generati da CasaZen con invio SDI via API.

**Una sola fattura all'ospite per l'intero soggiorno** in Italia sarebbe possibile solo con agenzia **senza rappresentanza** (art. 3 c. 3). Ma allora l'IVA si applica all'intero corrispettivo e il proprietario probabilmente perde il regime delle locazioni brevi [NV] ⚖. **Sconsigliato.**

#### D3, in attesa del PO: ricavi OTA verso i proprietari

**Contesto:** Airbnb e Booking.com pagano sul conto bancario dell'agenzia, fuori da Stripe. In ogni opzione serve **un'autorizzazione dell'agenzia almeno una volta al mese**. CasaZen non può muovere da sola i soldi dell'agenzia, e non deve: altrimenti le servirebbe una licenza di pagamento.

| | **A. Stripe Treasury (solo Italia)** | **B. EBICS con la banca dell'agenzia (Svizzera)** | **C. bLink di SIX (Svizzera)** |
|---|---|---|---|
| Processo mensile | 1. CasaZen calcola il totale. 2. L'agenzia fa **un bonifico** dalla sua banca al suo conto Stripe: le OTA non possono pagare direttamente lì, *"You should only credit financial accounts using external bank accounts that you own"*. 3. CasaZen paga i proprietari via SEPA (circa 1 giorno) | 1. CasaZen invia i bonifici alla banca dell'agenzia via EBICS. 2. L'agenzia **approva il lotto** nel suo e-banking. 3. La banca paga | Come B, tramite SIX; approvazione di ogni pagamento in e-banking |
| Costi | Conto e bonifici in entrata gratuiti. Uscite a listino €0,30 per bonifico (prezzi del programma non pubblici) + probabilmente Connect: €2 per account/mese + 0,25% + €0,10 per payout | Canale gratuito (PostFinance, Raiffeisen, ZKB, BancaStato) + normali costi bancari | CHF 5.000 + CHF 200/mese (dal 2027: CHF 2.500 + almeno CHF 250/mese per servizio) + CHF 0,15 per pagamento |
| Sviluppo | Alto: migrazione degli account delle agenzie da Express v1 a Accounts v2 senza dashboard Stripe; dashboard e assistenza a carico di CasaZen | Medio: client EBICS + contratto EBICS per agenzia | Medio + ammissione SIX (8–12 settimane) |
| Responsabilità di CasaZen | Dai termini Stripe: dashboard, assistenza, programma di compliance, approvazione Stripe della comunicazione commerciale, gestione delle contestazioni, **responsabilità in solido per i debiti delle agenzie verso Stripe**, manleva. Fondi custoditi da Stripe come moneta elettronica, senza garanzia dei depositi | Solo fornitore di software; la firma resta all'agenzia. Licenza non necessaria [NV] | Come B + contratto SIX e audit annuale |
| Rischi | *Private preview*: Stripe può dire di no. "Incasso per conto terzi" è tra le attività soggette a restrizioni (<https://stripe.com/legal/restricted-businesses>) | Ogni agenzia deve firmare il contratto EBICS con la propria banca | Costo fisso alto, nessuna automazione in più |

**Ripiego per l'Italia senza Treasury:** un aggregatore autorizzato all'avvio dei pagamenti (es. Fabrick), con autenticazione forte dell'agenzia sul lotto mensile e prezzo da contrattare.

Fonti:
- <https://docs.stripe.com/treasury/connect>
- <https://docs.stripe.com/treasury/connect/moving-money/fund-a-financial-account>
- <https://stripe.com/legal/ssa-services-terms>
- <https://docs.blink.six-group.com/downloads/contracts/annex4-pricelist-six-en.pdf>
- <https://www.postfinance.ch/en/business/products/payment-transactions/e-banking-apps-channels/technical-business-channels.html>

**Raccomandazione:** Italia A (con ripiego aggregatore), Svizzera B.

#### Rendiconto proprietario (automatico)

- Generato ogni mese dagli eventi di prenotazione, Stripe e spese.
- Contenuto: ricavi per canale, commissione, spese con allegati, ritenute, netto, stato del payout.
- Valuta della proprietà.
- Area documenti e blocchi per uso personale.

**Dato da raccogliere all'onboarding del proprietario:** lo **status fiscale**. Dal 2026 una persona fisica con più di 2 appartamenti in affitto breve è presunta impresa (L. 199/2025 c. 17), e questo determina se si applica la ritenuta.

---

### 3.4 Fornitori e staff propri (solo B2B, R10)

**D7, deciso dal PO (2026-10-15):** i fornitori propri sono **registrati e gestiti interamente in CasaZen** e legati a un'agenzia. Quando li usa l'agenzia a cui sono legati non si applica **nessuna commissione** e la **fatturazione avviene fuori da CasaZen**.

| Elemento | Default B2B: fornitore del marketplace | Opzione in configurazione: fornitore proprio legato all'agenzia | Staff dell'agenzia |
|---|---|---|---|
| Registrazione | Organizzazione `Supplier` (invito admin o autoregistrazione) | Organizzazione `Supplier` **invitata dall'agenzia** e legata a essa (nuovo legame agenzia ↔ fornitore). Nessun vincolo per comune pilota, anche in Svizzera | Utente dell'organizzazione con ruolo `Staff` |
| Gestione in CasaZen | Completa (richieste, agenda, foto, chiusura) | **Completa, come per il marketplace** | Attività assegnate, checklist, foto |
| Pagamento e fatturazione | **Online via Stripe**, con commissione CasaZen. Oggi è dietro il flag `SupplierOnlinePayments` (spento); il default attuale "Pagato" manuale viola R4 | **Fuori da CasaZen**: nuova modalità `ServiceRequestPaymentMode.External` (da aggiungere in coda all'enum), senza Stripe e senza flag "Pagato" | Fuori da CasaZen (stipendio) |
| Commissione | 10% "provvisoria" (`SupplierPaymentsOptions`) | **0%**, fissata dal legame con l'agenzia, non per singolo fornitore | — |
| Costo nel rendiconto del proprietario | Dall'importo pagato su Stripe | Dall'importo **dichiarato sulla richiesta** (preventivo o consuntivo approvato dall'agenzia) | Tariffa interna configurabile, se l'agenzia la riaddebita |

**Regole:**
- La modalità di pagamento si decide all'accettazione della richiesta, come oggi (`ServiceRequestPaymentMode`): `External` **solo se** chi richiede è l'agenzia legata al fornitore.
- Il fornitore legato è **a tutti gli effetti un fornitore CasaZen** (confermato dal PO il 2026-10-15). Può lavorare anche per altri host o agenzie tramite il marketplace, e per quei lavori valgono le regole del marketplace (Stripe + commissione).
  - Per i lavori del marketplace serve il suo account Stripe Connect: vale la regola KYC già esistente, che non permette pagamenti prima che l'account Stripe sia pronto (`ServiceRequestPaymentMode`).
  - Se non vuole lavori da terzi, **disattiva l'interruttore "visibile nel marketplace"** nel proprio profilo (deciso dal PO il 2026-10-15). L'agenda (orari, ferie, blocchi, iCal: `SupplierAgendaRules.cs`, `SupplierPlanning.cs`) resta la disponibilità reale per tutti, agenzia compresa: oggi è unica per fornitore, quindi chiuderla bloccherebbe anche l'agenzia.
- Il rendiconto del proprietario resta automatico (R4): il costo arriva dalla richiesta, non da un caricamento manuale.

**Lavoro:**
- legame agenzia ↔ fornitore con flusso di invito (oggi esiste solo l'invito da admin; `OrgTrustedSupplier` non esiste: `GlobalSearchService.cs:29,344`);
- modalità `External` + regola di commissione 0% legata al legame;
- interruttore "visibile nel marketplace" sul profilo del fornitore;
- ruolo `Staff` e assegnazione dei `ServiceRequest`;
- interruttore nell'onboarding o nelle impostazioni dell'agenzia;
- importo approvato → spesa nel rendiconto del proprietario;
- app mobile per fornitori e staff (US-028, oggi pianificata).

**Taglia:** M.

---

### 3.5 Fiscalità Italia + Svizzera: automazione verificata giurisdizione per giurisdizione (R4)

Livelli di automazione: **COMPLETA** = API ufficiale o di un partner, nessun passaggio manuale · **PARZIALE** = esiste solo un canale file, oppure manca la conferma dell'API · **NON POSSIBILE** = nessun canale automatizzabile oggi.

**Italia: adempimenti nazionali**

| Adempimento | Sistema | API | Automazione |
|---|---|---|---|
| Alloggiati Web | `alloggiatiweb.poliziadistato.it/service/service.asmx`: **verificate 12 operazioni** (`GenerateToken`, `Send`, `Ricevuta`, `Tabella`, `GestioneAppartamenti_*`). Autenticazione con utente + password + WsKey (generata dall'host una volta) | SOAP ufficiale, gratuito | **COMPLETA** (oggi CasaZen produce solo il file) |
| Dichiarazione annuale imposta di soggiorno, CU/770, dati locazioni brevi, DAC7 | Entratel/Fisconline | **Nessuna API di invio.** Il catalogo di interoperabilità AdE (provv. 6/7/2026) offre solo download e verifiche (<https://www.agenziaentrate.gov.it/portale/catalogo-dei-servizi-di-interoperabilita>) | **NON POSSIBILE** via API. CasaZen genera i file validati e un **intermediario Entratel partner** li riceve via API e li trasmette (delega R7) |
| F24 (ritenute) | Banca / Entratel | Fabrick F24 API solo per conti **Banca Sella**; altrimenti un intermediario | **PARZIALE** |
| Verifica CIN | Pagina pubblica BDSR | Nessuna | **NON POSSIBILE** (resta la validazione del formato) |

**Italia: ISTAT per regione del cliente**

| Regione | Sistema | API | Automazione |
|---|---|---|---|
| Lombardia (Como, Milano) | Ross1000 | **SOAP `checkinV2` verificato** (`inviaMovimentazione`, `inviaMovimentazioneList`) | **COMPLETA** |
| Marche (Ascoli Piceno) | Istrice-Ross1000 | SOAP `checkinV2` | **COMPLETA** |
| Sicilia (Trapani) | Osservatorio Turistico | API HTTPS per gestionali (documentazione su richiesta) | **COMPLETA** dopo aver ottenuto la documentazione |
| Liguria | Ross1000 Liguria | Documentato solo l'import di file; web service [NV] | **PARZIALE**: da chiedere alla Regione |
| Calabria (Tropea) | SIRDAT (Ross1000) | Web service [NV] | **PARZIALE**: da chiedere alla Regione |

**Italia: imposta di soggiorno per comune**

| Comune | Piattaforma | Automazione |
|---|---|---|
| Como, Trapani, Tropea [NV] | StayTour (Hyksos) | **NON POSSIBILE** |
| Genova, Portofino, S. Margherita | TouristTax (Advanced Systems) | **NON POSSIBILE** |
| Cinque Terre (Riomaggiore) | Strumento del comune | **NON POSSIBILE** |
| Milano, Sanremo [NV] | SoggiorniAmo / EntrateOnline (OESIS): solo caricamento CSV | **PARZIALE** |
| Ascoli Piceno | Pegaso St@y Tourist: import di file | **PARZIALE** |
| Versamento pagoPA | Il codice IUV lo genera solo il portale del comune | **PARZIALE** |

**Svizzera**

| Adempimento | Sistema | API | Automazione |
|---|---|---|---|
| **Ticino**: notifica ospiti alla polizia (RL 942.120) | `notifiche.ti.ch`: solo caricamento di file XML/XLSX/CSV (XSD `F-Modello_4.xsd`) con 2FA | Nessun web service | **NON POSSIBILE** oggi. Chekin: Ticino "coming soon" |
| Ticino: tassa di soggiorno e registrazione delle unità | OTR / piattaforma ATT | Nessuna | **NON POSSIBILE** |
| Davos Klosters | feratel MeldeClient (interfaccia per gestionali) | Contratto feratel o Chekin | **COMPLETA** |
| Laax/Flims, Engadina/St. Moritz, Andermatt (UR) | Sistemi da identificare (feratel probabile) | ? | **NON CONFERMATA** |
| Riforma federale (EasyGov, notifica elettronica senza firma) | Consultazione 26/8–27/11/2026 | Nessuna specifica | Futuro (~2028 [NV]) |

**Ticino, vincolo sui documenti.** RL 942.120 art. 6 cpv. 2: numero e tipo del documento si registrano *"senza procedere alla fotocopia dello stesso"* (<https://m3.ti.ch/CAN/RLeggi/public/index.php/raccolta-leggi/pdfatto/atto/10301>). Impatto sul check-in in §3.6.

**Chekin come partner:** l'API v3 copre Alloggiati e ISTAT per Lombardia e Marche, ma **non** Liguria e Calabria; nessuna polizia svizzera via API. Chekin agisce solo come *responsabile del trattamento* e *"does not guarantee full compliance"*. **Il valore aggiunto per i nostri territori è basso:** conviene costruire direttamente Alloggiati e Ross1000 e usare feratel per Davos.

#### D6, deciso: tutto da CasaZen, con la ricevuta in CasaZen

**Decisione del PO:** l'utente fa tutto da CasaZen e riceve in CasaZen la ricevuta dell'operazione completata, tramite API o tramite intermediazione; i costi extra sono accettati.

**Vincoli verificati:**
- Le credenziali SPID sono di **uso esclusivamente personale** (AgID): automatizzare il login dell'agenzia (RPA) **non è lecito**.
- CasaZen **non può diventare intermediario Entratel**: servono professionisti, CAF o una società di servizi a maggioranza di commercialisti (AdE Risposte 79/2022 e 87/2022).
- I portali StayTour, TouristTax e Ticino consentono al gestore di **abilitare altre persone**, ognuna con le proprie credenziali.

| Adempimento | Modello | Chi esegue | Ricevuta in CasaZen | Costo indicativo |
|---|---|---|---|---|
| Alloggiati Web | API SOAP | CasaZen, automatico | Sì (`Ricevuta`) | Gratuito |
| ISTAT Lombardia, Marche, Sicilia | API SOAP/HTTPS | CasaZen, automatico | Sì | Gratuito |
| ISTAT Liguria, Calabria | API da confermare con le Regioni; altrimenti back-office | CasaZen | Sì | — |
| Imposta di soggiorno annuale, CU/770, dati locazioni brevi, DAC7 | **Intermediario partner** (CAF o studio): CasaZen genera file validati con i moduli di controllo AdE e li invia via API/SFTP | Partner | Sì (ricevuta AdE caricata dal partner) | ~€30 per dichiarazione (esempio ANBBA); altri a contratto |
| F24 (ritenute, imposte) | Intermediario partner con addebito sul conto dell'agenzia | Partner | Sì (quietanza) | A contratto |
| StayTour (Como, Trapani, Tropea) | **Back-office**: operatore delegato (fino a 3 delegati SPID per struttura) | CasaZen o partner, con il proprio SPID | Sì (dichiarazione + ricevuta pagoPA/F24) | Tempo operatore + €2 pagoPA |
| TouristTax (Genova, Portofino, S. Margherita) | Back-office con utenza secondaria + caricamento file | CasaZen o partner | Sì (lista versamenti + pagoPA) | Tempo operatore |
| SoggiorniAmo (Milano, Sanremo) | Back-office con delega del gestore + CSV | CasaZen o partner | Sì | Tempo operatore. Portata della delega [NV] |
| Pegaso (Ascoli Piceno) | Delega da verificare con assistenza@impostasoggiorno.it | — | — | **Incerto** |
| Cinque Terre | Da verificare | — | — | **Incerto** |
| Pagamento pagoPA | Openapi "PagoPA Bill" | CasaZen via API | Sì | €2 per pagamento |
| **Ticino: notifica alla polizia** | Back-office: operatore abilitato dal gestore (§13.1 del manuale), con proprio TOTP + caricamento XML | CasaZen o partner | Sì (pagina esiti + email) | **Presidio 7 giorni su 7 entro 24 ore** |
| Ticino: tassa OTR | CSV dal portale notifiche + fattura OTR all'agenzia | Back-office | Fattura OTR [NV] | — |
| Davos (feratel) | Interfaccia feratel per gestionali | CasaZen, automatico | Sì | Contratto feratel |
| Laax/Flims, Andermatt | Tassa forfettaria o per capacità, nessuna dichiarazione per soggiorno | CasaZen (promemoria) | — | — |
| Verifica CIN | Solo validazione del formato; richiedere al Ministero un servizio su PDND (aperta ai privati dal 15/01/2026) | — | — | — |

**Condizioni del back-office:**
- **Contratti:** mandato con l'agenzia (art. 1710 c.c.) e accordo GDPR art. 28 / nLPD; massimali di responsabilità e assicurazione.
- **Ruolo di CasaZen:** non deve diventare "gestore delegato" con atto formale, perché alcuni regolamenti comunali lo renderebbero corresponsabile del tributo.
- **Denaro:** CasaZen non paga imposte con fondi propri. I pagamenti partono dal conto dell'agenzia (F24 tramite intermediario, link pagoPA).
- **Responsabilità:** resta all'agenzia (§2.2).

**In parallelo, richieste di API** (riducono il costo del back-office nel tempo):
- Hyksos (StayTour), Advanced Systems, OESIS, Pegaso;
- Polizia cantonale TI (servizio.autorizzazioni@polca.ti.ch);
- Regioni Liguria e Calabria.

Fonti:
- <https://www.agid.gov.it/sites/default/files/repository_files/regole_tecniche/obblighi_dei_gestori_e_dei_titolari.pdf>
- <https://agenziaentrate.gov.it/portale/documents/20143/4169782/Risposta_79_07.02.2022.pdf/69415bb9-9e71-71a7-edda-3a571130e11d>
- <https://imposta-soggiorno.org/manuale-struttura.pdf>
- <https://tourtaxmain.servizienti.it/TourTaxHelp/>
- <https://www4.ti.ch/fileadmin/DI/POLIZIA/documentazione/autorizzazioni/autorizzazioni_e_permessi/Manuale_Utente.pdf>
- <https://openapi.com/products/pagopa-bill>
- <https://www.anbba.it/dichiarazione-annuale-imposta-soggiorno-2025-scadenza-30-giugno/>

---

### 3.6 Routine AI e check-in con riconoscimento del documento e del volto (R6, R9)

#### Vincoli legali (fonti primarie verificate)

**Italia: art. 109 TULPS**
- **Consiglio di Stato, Sez. III, n. 9101/2025 (testo integrale):**
  - §6.2: la verifica *"non può che essere effettuata de visu"*; il check-in da remoto non dà garanzie su *"luogo, tempo e corrispondenza visiva"*.
  - §6.3: è ammesso un *"videocollegamento predisposto dal gestore all'ingresso della struttura"* idoneo ad accertare *"hic et nunc"* la corrispondenza tra ospite e documento (es. *"spioncino digitale o QR code che faccia un fermo immagine"*).
  - Fonte: <https://mdp.giustizia-amministrativa.it/visualizza/?nodeRef=&schema=cds&nrg=202505732&nomeFile=202509101_11.html&subDir=Provvedimenti>
  - **Conseguenza: un selfie fatto prima dell'arrivo, sul telefono dell'ospite, non è conforme.**
- **Viminale, luglio 2025** (risposta ad AssoSoftware): sono ammessi videochiamata o QR/codice *"periodicamente aggiornato e visualizzabile solo presso la struttura"* (<https://www.assosoftware.it/comunicati-stampa/viminale-via-libera-proposta-assosoftware-check-in-distanza-230725/>). Nessuna nuova circolare nel 2026.

**GDPR / Garante**
- Base giuridica per dati e documento: art. 6(1)(c), obbligo di legge.
- Per la biometria: **solo consenso esplicito** (art. 9(2)(a)). L'art. 109 non prevede biometria.
- EDPB 3/2019 §86: non si può condizionare il servizio, serve un'alternativa *"without restraints or additional cost"* (<https://www.edpb.europa.eu/sites/default/files/files/file1/edpb_guidelines_201903_video_devices_en_0.pdf>).
- **Garante, eCampus (29/01/2026), €50.000:** consenso biometrico non valido perché senza consenso *"sarebbe stata del tutto negata la possibilità di partecipare"*; DPIA obbligatoria (<https://www.garanteprivacy.it/home/docweb/-/docweb-display/docweb/10221611>).
- Garante, SEA Linate FaceBoarding (12/03/2026): trattamento illecito, con richiamo all'EDPB Opinion 11/2024 (template sotto il **controllo esclusivo** dell'interessato).

**AI Act:** la verifica 1:1 è **esclusa dall'alto rischio** (Allegato III, punto 1(a)). Restano gli art. 4 e 5. Disattivare la stima di età o genere dei fornitori, che ricadrebbe nell'art. 50(3).

**Svizzera**
- nLPD: art. 5 lett. c n. 4 (dato biometrico = dato degno di particolare protezione), art. 6 cpv. 6–7 (consenso libero ed esplicito), art. 22 (valutazione d'impatto).
- La guida EDÖB impone alternative.
- **Ticino:** divieto di fotocopiare il documento (vedi §3.5).

#### Flusso proposto (il più vicino a R6 tra quelli leciti)

1. **Check-in online obbligatorio (pre-arrivo)**
   - Dati di ogni ospite + documento letto via OCR/MRZ o **NFC** (lettura del chip CIE consentita dal Reg. UE 2025/1208 art. 11(6), con cancellazione a verifica conclusa).
   - Le immagini del documento si **cancellano alla ricevuta Alloggiati**. **In Ticino non si conservano mai.**
2. **All'arrivo, all'ingresso**
   - L'ospite inquadra un **QR rotante** esposto all'ingresso (display o tastierino della serratura).
   - Due percorsi equivalenti, scelti dall'ospite:
     - **(A) predefinito:** liveness + confronto 1:1 del volto con la foto del chip o del documento, con consenso esplicito;
     - **(B) alternativa:** videochiamata live con un operatore dell'agenzia.
   - Il **codice della serratura** viene rilasciato solo dopo l'esito positivo.
3. **Supervisione umana**
   - Ogni esito negativo va a un operatore: nessun rifiuto puramente automatico (art. 22 GDPR).
   - In Italia, finché non c'è un parere scritto del Viminale, conviene la **conferma dell'operatore sul fermo immagine** anche nel percorso A ⚖.
4. **Minori:** nessuna biometria. Li dichiara il genitore (sono familiari ai fini di Alloggiati).
5. **Documenti di compliance:** DPIA prima del lancio (IT e CH); nomina di CasaZen come responsabile del trattamento (art. 28) e dei sub-fornitori; informative art. 13 GDPR / art. 19 nLPD; testo del consenso separato e non preselezionato.

#### D5, deciso: riconoscimento facciale via API di terzi, conservazione conforme al Garante

**Sviluppo interamente in casa: non realistico.**
- Effort di 50–75 settimane-persona più i test di certificazione.
- Nessun sistema open source di verifica di presenza dal vivo (liveness) è certificato.
- I modelli di riconoscimento più accurati non sono utilizzabili a fini commerciali. InsightFace, verificato: *"available for non-commercial research purposes only"* (<https://github.com/deepinsight/insightface>).
- CERT-AgID (05/2026) segnala che contro i video falsi iniettati nel flusso della fotocamera (deepfake) la liveness attiva *"fallisce strutturalmente"*.

**Conservazione (decisa: conforme al Garante).** La nota del 29/04/2026 impone di cancellare la copia del documento appena Alloggiati rilascia la ricevuta (<https://www.garanteprivacy.it/home/docweb/-/docweb-display/docweb/10244289>).
- **Le immagini restano consultabili solo fino alla ricevuta. In Ticino non si conservano mai.**
- Restano consultabili sempre in CasaZen: ricevuta Alloggiati (5 anni), esito della verifica (positivo/negativo, data e ora, riferimento del fornitore), tipo e paese del documento, dati anagrafici trasmessi.
- Alla ricevuta, CasaZen cancella la sessione presso il fornitore via API.

**Costi e fornitori** (listino ufficiale; volumi di esempio 10.000 e 40.000 verifiche/anno, un adulto = una verifica):

| Fornitore | Prezzo per verifica (documento + selfie + liveness + confronto) | 10k/anno | 40k/anno | Cancellazione via API | Note |
|---|---|---|---|---|---|
| **Didit** | **$0,33** (NFC +$0,15); nessun minimo; prezzi in modifica dal 1/11/2026 | ~$3.200 (con NFC ~$4.700) | ~$13.100 (con NFC ~$19.100) | Sì, ma cancella anche l'esito → CasaZen lo salva prima | Dati in UE, SDK React Native/Expo, iBeta livello 1 |
| **Sumsub** | $1,35 (minimo $149/mese) | ~$13.500 | ~$54.000 | **Modalità "cancella solo le foto del documento"**, mantiene i dati testuali (verificato) | Dati in Germania, iBeta livelli 1–3; cancellazione completa solo con contratto personalizzato |
| iDenfy Enterprise | $0,55–0,65 + $0,15 confronto volto + $0,15 liveness 3D | ~$9.500 | ~$34.000 | Sì (foto e dati personali) | Dati in UE; nessun SDK React Native ufficiale trovato |
| Veriff | $0,80–1,89 + minimi | $8.000–18.900 | $32.000–75.600 | **Max 10 cancellazioni al giorno** | **Scartato** (non rispetta il Garante a questi volumi) |
| Stripe Identity | €1,25 | ~€12.400 | su preventivo | Stripe conserva i dati come titolare autonomo (fino a 7 anni) | **Scartato**: non verifica i minori, titolare autonomo |
| Regula / Innovatrics / IDnow / Namirial | Su preventivo | — | — | Installazione propria o SaaS | Alternativa se serve certificazione più alta |

Fonti:
- <https://didit.me/pricing>
- <https://docs.didit.me/console/data-retention>
- <https://sumsub.com/pricing/>
- <https://docs.sumsub.com/changelog/january-2026>
- <https://idenfy.com/savings/>
- <https://devdocs.veriff.com/apidocs/v1sessionsid-3>
- <https://support.stripe.com/questions/managing-your-id-verification-information>

**Raccomandazione:**
1. Fare un PoC con **Didit** (costo più basso, iBeta livello 1) e con **Sumsub** (miglior aderenza alla nota del Garante, iBeta fino al livello 3).
2. Chiedere preventivi con le domande sulla cancellazione, sul NFC per CIE e passaporti, sull'addestramento dei modelli con i nostri dati e sui minori.
3. **Carta d'identità svizzera:** quella con chip è rilasciata solo su richiesta dal 2/11/2026 (verificato: <https://www.fedpol.admin.ch/de/newnsb/aKCjZsaUbIe8>). Per anni la maggior parte delle carte svizzere si leggerà solo da MRZ, con fallback visivo.

**Nessun fornitore restituisce il "luogo di rilascio"** richiesto da Alloggiati: serve una mappatura propria o la conferma da parte dell'ospite. La lettura NFC richiede un'app nativa (Expo development build, non Expo Go).

**Hardware per proprietà:** display con QR rotante o tastierino della serratura. Costo da includere nell'offerta.

#### Routine AI

Invariato rispetto alla v1:
- regole "prenotazione → attività" su Hangfire;
- checklist a template;
- foto da mobile;
- AI per controllo delle foto, sintesi delle segnalazioni, traduzioni;
- serrature secondo `spec-smart-devices-access.md` (Seam, oppure Nuki/igloohome diretti).

**Automazione (R4):** le attività si generano da sole; il codice della serratura si rilascia solo dopo l'identificazione.

---

## 4. Costi ricorrenti (prezzi di listino, IVA esclusa)

| Componente | Prezzo | Chi lo paga |
|---|---|---|
| NextPax | €3,50/unità/mese, minimo €350 (50 unità incluse). Esempio: 100 unità ≈ €525/mese, 250 unità ≈ €1.050/mese | Pass-through nel listino per unità |
| Stripe carte (EEA) / SEPA DD / TWINT | 1,5% + €0,25 (standard), 2,8% + €0,25 (premium), 3,15% + €0,25 (internazionali, +2% con conversione) / €0,35 / 2,6% + €0,25 | **Ripartite in proporzione tra agenzia e proprietario** (deciso) |
| Stripe Connect | €2 per account attivo/mese + **0,25% + €0,10 per payout**; nessuna commissione sui trasferimenti | Ripartite come sopra |
| Stripe Billing (canone CasaZen) | 0,7% | CasaZen |
| Verifica identità | Didit $0,33 (+$0,15 NFC); Sumsub $1,35 (minimo $149/mese) | Per ospite adulto: incluso nel canone per unità |
| SDI | Invoicetronic €0,10–0,02 per transazione; Openapi €0,07–0,022 | Trascurabile |
| Taxually (dichiarazioni IVA) | Non pubblicato | Agenzia (opzionale) |
| feratel / interfacce di destinazione | A contratto | Agenzia |
| Intermediario Entratel (dichiarazioni, CU/770, F24) | ~€30 per dichiarazione; altro a contratto | Agenzia |
| Back-office per portali (comuni, Ticino) | Personale CasaZen o partner; il Ticino richiede un presidio 7 giorni su 7 | Da prezzare come servizio aggiuntivo |
| pagoPA via Openapi | €2 per pagamento | Agenzia |
| Stripe Treasury (se D3 = A) | Uscite a listino €0,30 per bonifico; programma su preventivo | Da definire |

Fonti:
- <https://stripe.com/en-it/pricing>
- <https://stripe.com/en-it/connect/pricing>
- <https://www.veriff.com/pricing>
- <https://invoicetronic.com/en/pricing/>

---

## 5. Roadmap a fasi (da validare con PO e team)

| Fase | Contenuto | Note |
|---|---|---|
| **0. Decisioni e conferme esterne** (in parallelo, avvio immediato) | Pareri ⚖ (D2 ritenuta/tassa di soggiorno/DAC7, biometria + DPIA, richiesta al Viminale); Stripe (pre-approvazione del modello SCT, Treasury se D3 = A); 11 domande + PoC a NextPax e Octorate; PoC Didit e Sumsub; contratto con l'intermediario partner (CAF o studio); organizzazione del back-office; richieste di API a enti e fornitori | Determina il perimetro reale |
| **1. Fondamenta** | `OrgType.Agency`; ruoli `PropertyOwner` e `Staff`; paese, valuta, lingua DE; matrice delle funzionalità per piano; ADR "channel manager", "flussi Stripe B2B", "provider AI per dati personali" | Sblocca tutto |
| **2. Compliance e check-in** | Alloggiati SOAP, Ross1000, API Sicilia, feratel Davos; check-in online con documento e NFC + verifica all'arrivo (A/B) + serrature | Massimo valore di compliance |
| **3. Denaro** | SCT a 4 parti, rendiconti, fatture con invio SDI, payout mensili; fornitori marketplace via Stripe + opzione staff propri | Dipende da D2 e D3 |
| **4. Distribuzione** | Channel manager (contenuti + disponibilità/tariffe + prenotazioni) + inbox + AI | Dipende dalla scelta del fornitore |
| **5. Crescita** | CRM + campagne + WhatsApp; nuove giurisdizioni man mano che arrivano le API | — |

---

## 6. Registro delle decisioni del PO

| ID | Decisione | Stato |
|---|---|---|
| D1 | Prezzo B2B | **Deciso:** solo canone per unità, nessuna percentuale sulle prenotazioni |
| D2 | Incasso delle prenotazioni dirette B2B | **Deciso:** CasaZen incassa, Stripe ripartisce (un addebito, due trasferimenti, payout mensili); `on_behalf_of` = agenzia per le agenzie italiane; commissioni Stripe in proporzione tra agenzia e proprietario. Parere fiscale o interpello prima del go-live ⚖ |
| D3 | Ricavi OTA verso i proprietari | **In attesa del PO** (sta verificando). Raccomandazione: Italia Treasury (ripiego aggregatore), Svizzera EBICS (§3.3) |
| D4 | Fornitore OTA | **Deciso:** 11 domande + PoC sia con NextPax sia con Octorate, scelta sulle risposte scritte |
| D5 | Riconoscimento facciale | **Deciso:** API di terzi (in casa non realistico). Conservazione conforme al Garante: immagini cancellate alla ricevuta Alloggiati, esito e ricevuta sempre consultabili. PoC Didit e Sumsub. Resta per legge l'alternativa non biometrica ⚖ |
| D6 | Adempimenti senza API | **Deciso:** tutto da CasaZen con la ricevuta in CasaZen; intermediario partner (AdE, F24) + back-office delegato (comuni, Ticino); costi extra accettati (§3.5) |
| D7 | Fornitori propri dell'agenzia | **Deciso:** registrati in CasaZen, 0% e fatturazione fuori da CasaZen quando li usa la loro agenzia; per i lavori di terzi valgono le regole del marketplace; interruttore "visibile nel marketplace" |
| D8 | Provider LLM | **Deciso dal PO:** DeepSeek, per il costo. **Raccomandazione aperta:** per i dati degli ospiti usare DeepSeek ospitato in UE, perché il Garante ha limitato con urgenza il trattamento dei dati degli utenti italiani da parte di DeepSeek (comunicato del 30/01/2025: <https://www.garanteprivacy.it/home/docweb/-/docweb-display/print/10097450>). DeepSeek-V3.1 è disponibile su AWS Bedrock anche nella regione Europa (Stoccolma) (<https://aws.amazon.com/about-aws/whats-new/2025/09/deepseek-v3-1-model-fully-managed-amazon-bedrock/>); prezzo e compatibilità con il nostro client sono da verificare |

---

## 7. Stima con processo AI (Claude Code Max 20x)

Su richiesta del PO la stima non è in settimane-persona: è **calibrata sulla velocità reale di questo progetto**, sviluppato da una persona (il PO) con un abbonamento Claude Code Max 20x (+ Cursor Agent, circa il 3% dei commit nel periodo).

**Limiti del piano Max 20x.** Anthropic non pubblica limiti numerici: indica un limite per sessione di 5 ore e un limite settimanale, condivisi tra Claude e Claude Code (<https://support.claude.com/en/articles/11145838-using-claude-code-with-your-pro-or-max-plan>). Per questo la capacità si ricava dallo storico, non da tabelle.

### 7.1 Calibrazione (misurata con git, 17/08–15/10/2026)

| Misura | Valore |
|---|---|
| Settimane attive | ~4,5 (W34 parziale, W39–W42) |
| Righe nette prodotte | backend +118k codice, +193k test; frontend +109k (+8k e2e); mobile +14k |
| Task del piano di risanamento eseguiti | 178 |
| **Modulo analogo: Locazioni** (contratti, canone, registrazione RLI verso AdE) | ~39k righe (10,8k codice + 18,7k test + 9,9k frontend) in ~2,5 settimane (24/09–09/10) → **~15k righe/settimana attiva** |
| Modulo analogo: pagamenti fornitori via Stripe Connect | Costruito in W41–W42 |

**Velocità usata per i nuovi moduli:** 13k righe/settimana (prudente) – 25k (ottimistica).
- La velocità media del periodo (~98k righe/settimana) **non si usa**: comprende rifacimenti interni, molto più veloci delle integrazioni con sistemi esterni.
- La revisione delle PR e i test del PO del periodo sono già dentro la velocità misurata.
- **Si aggiunge il +25%** per test manuali dell'interfaccia sugli ambienti di prova esterni e piccoli refactor. È un'ipotesi, non una misura.

### 7.2 Stima per pacchetto di lavoro

La dimensione è stimata per analogia con moduli esistenti misurati: Stripe/pagamenti 42,6k righe, Locazioni 39,3k, check-in/Alloggiati 14,7k, fiscale 13,6k, team dell'organizzazione 8,6k, GDPR/ospiti 6,6k.

| Pacchetto | Dimensione (k righe) | Analogo | Settimane attive AI | +25% test e refactor umani |
|---|---|---|---|---|
| WP1 Fondamenta: `Agency`, ruoli, paese/valuta/fuso, tedesco, piani | 25–35 | Team ×3 + rifacimento di EUR ed Europe/Rome | 1,0–2,7 | 1,3–3,4 |
| WP2 OTA: channel manager (contenuti, disponibilità/tariffe, prenotazioni, messaggi) + inbox + AI | 40–60 | ≈ modulo Stripe | 1,6–4,6 | 2,0–5,8 |
| WP3 CRM, campagne, WhatsApp, AI | 20–30 | GDPR/ospiti ×3–4 | 0,8–2,3 | 1,0–2,9 |
| WP4 Proprietari + incasso e ripartizione Stripe + documenti SDI per conto + payout (+D3) | 55–80 | Stripe + Locazioni | 2,2–6,2 | 2,8–7,7 |
| WP5 Fornitori propri, staff, interruttore marketplace | 8–12 | Aggiunte al modulo fornitori | 0,3–0,9 | 0,4–1,2 |
| WP6 Compliance IT+CH: Alloggiati, ISTAT, feratel, file AdE, partner, console back-office, pagoPA, XML Ticino | 40–60 | (Fiscale + check-in) ×1,5–2 | 1,6–4,6 | 2,0–5,8 |
| WP7 Check-in biometrico (SDK + NFC + QR all'ingresso + videochiamata) + serrature | 35–50 | Check-in ×2 + spec serrature | 1,4–3,8 | 1,8–4,8 |
| WP8 Routine AI | 15–25 | Parte del modulo richieste di servizio | 0,6–1,9 | 0,8–2,4 |
| **Totale** | **238–352** | | **~11–28** | **~14–35 (valore centrale ~20)** |

**Calendario.** Con un solo abbonamento i pacchetti procedono **in sequenza**: le sessioni parallele condividono gli stessi limiti. Sequenza consigliata:
1. WP1;
2. WP6 + WP7 (legalità in Italia);
3. WP5;
4. WP4;
5. WP2;
6. WP8;
7. WP3.

Valore centrale **~20 settimane attive ≈ 5 mesi**; intervallo 3,5–8 mesi.

**Costo dello strumento:** mesi × prezzo Max 20x.
- La pagina ufficiale indica il piano Max "From $100 per month" senza il prezzo del 20x (<https://claude.com/pricing>): fa fede l'addebito reale.
- Lo storico mostra che un solo abbonamento ha sostenuto questa velocità. Se si superano i limiti si può attivare l'uso extra a pagamento.

### 7.3 Cosa l'AI non accorcia (percorso critico)

Da avviare **subito e in parallelo**, perché determinano il go-live più dello sviluppo:

| Dipendenza esterna | Blocca | Tempo |
|---|---|---|
| Parere fiscale / interpello AdE su D2 (ritenuta, tassa di soggiorno, DAC7) | Go-live WP4 | Interpello: risposta in 90 giorni [NV] |
| Stripe: approvazione del modello di incasso e ripartizione; Treasury se D3 = A | WP4 in produzione | Non pubblicato |
| NextPax / Octorate: risposte scritte + sandbox | Avvio WP2 | Non pubblicato |
| Didit / Sumsub: preventivi + PoC; DPIA | WP7 in produzione | Settimane |
| Intermediario partner (contratto, integrazione); organizzazione del back-office (Ticino 7 su 7) | WP6 in produzione | Non pubblicato |
| Enti: documentazione API Sicilia, web service Liguria e Calabria, contratto feratel; richieste di API ai portali | Parti di WP6 | Non pubblicato |
| Parere Viminale (verifica senza operatore) | Automazione completa WP7 | Non pubblicato |
| Revisione madrelingua delle traduzioni in tedesco | WP1 per CH tedesca | Da pianificare |

**Limiti della stima:**
- Le righe di codice sono una misura grossolana. L'analogia ha un'incertezza di circa ±40%.
- La velocità misurata viene da poche settimane intense e potrebbe non reggere per 5 mesi consecutivi.
- Le integrazioni con fornitori mai usati (NextPax, Didit/Sumsub, Treasury) sono le più esposte a ritardi.

---

## Allegato A: domande per NextPax e Octorate (risposta scritta)

1. Per Airbnb, Booking.com, Vrbo ed Expedia: si può creare **da zero via API** un annuncio nuovo? Elencare ogni passaggio che richiede interfaccia o intervento dell'host (OAuth, Legal Entity ID, contratto, SSO).
2. Quali campi di contenuto si possono inviare per ciascuna OTA: titolo e descrizione IT/EN/DE/FR, foto (ordine, didascalie), servizi, regole, camere e letti, campi licenza (**CIN**)?
3. Messaggi per ciascuna OTA: lettura e invio, richieste prima della prenotazione, allegati, offerte speciali, webhook o polling, latenza, limiti di frequenza.
4. Le risposte automatiche o generate dall'AI sono ammesse dai vostri termini e da quelli delle OTA? Servono certificazioni o etichette?
5. Recensioni: lettura e risposta per ciascuna OTA.
6. Prezzi per un software vendor: per unità, minimi, setup e certificazione, eventuale sovrapprezzo per i messaggi, a chi si fattura.
7. Chi è titolare degli account e dei contratti OTA? In caso di uscita si può migrare senza ripubblicare (mantenendo le recensioni)? Tutto il contenuto è esportabile via API?
8. Esiste una sandbox con annunci di test su ciascuna OTA?
9. Vendete direttamente ai gestori in Italia e Svizzera? Accettate una clausola di non sollecitazione?
10. Potete documentare i livelli di partnership con Airbnb, Booking.com, Expedia e Vrbo, fornire un DPA, dati ospitati in UE e l'elenco dei sub-fornitori?
11. Collegate e-domizil/HomeToGo, feratel Deskline e Google Vacation Rentals?

## Fonti principali aggiuntive

- **Stripe:**
  - <https://docs.stripe.com/connect/charges>
  - <https://docs.stripe.com/connect/cross-border-payouts>
  - <https://docs.stripe.com/connect/service-agreement-types>
  - <https://docs.stripe.com/connect/top-ups>
  - <https://docs.stripe.com/payments/twint>
  - <https://stripe.com/en-it/guides/send-e-invoices-on-stripe-billing-through-app-marketplace-partners>
- **Identity:**
  - <https://docs.stripe.com/identity/use-cases>
  - <https://stripe.com/legal/ssa-service-terms>
- **AdE:**
  - <https://www.agenziaentrate.gov.it/portale/documents/d/guest/locazioni_brevi_disciplina_fiscale_e_regole_per_intermediari_aprile-2026>
  - <https://www.normattiva.it/uri-res/N2Ls?urn:nir:stato:decreto.legge:2017-04-24;50~art4>
- **ISTAT:**
  - <https://www.flussituristici.servizirl.it/Turismo5/app/ws/checkinV2?wsdl>
  - <https://istrice-ross1000.turismo.marche.it/ws/checkinV2?wsdl>
  - <https://osservatorioturistico.regione.sicilia.it/>
- **Tassa di soggiorno:**
  - <https://www.comune.como.it/it/servizi/tasse-e-imposte/imposta-di-soggiorno/>
  - <https://www.comune.milano.it/en/servizi/tributi/imposta-di-soggiorno>
  - <https://www.comune.genova.it/servizi/tributi-finanze-e-contravvenzioni/imposta-di-soggiorno-ids>
- **Svizzera:**
  - <https://www4.ti.ch/di/pol-new/servizi-online/importare-i-pernottamenti>
  - <https://www.dkinfo.ch/fileadmin/user_upload/dokumente/Gaestetaxen/Factsheet_Info_Vermieter_de.pdf>
  - <https://www.admin.ch/de/newnsb/STwblroLhJ4->
- **Privacy:**
  - <https://www.garanteprivacy.it/home/docweb/-/docweb-display/docweb/10244289>
  - <https://eur-lex.europa.eu/eli/reg/2024/1689/oj>
  - <https://eur-lex.europa.eu/eli/reg/2025/1208/oj>
  - <https://www.fedlex.admin.ch/eli/cc/2022/491/de>
