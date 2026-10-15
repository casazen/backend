# Tipo host "Agenzia" (B2B): analisi delle 6 funzionalità richieste

**Versione:** v2, 2026-10-15. Sostituisce la v1 del 2026-10-14.
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
| R10 | Nel B2B i **fornitori CasaZen sono sempre il default**. Staff e fornitori propri sono un'opzione da attivare in configurazione. |

---

## 0. Sintesi: dove le regole si scontrano con vincoli esterni

Tre regole **non sono realizzabili alla lettera** per vincoli esterni (legge, Stripe, enti pubblici). Per ciascuna propongo la variante più vicina e lecita.

| Regola | Vincolo esterno verificato | Variante proposta |
|---|---|---|
| **R6/R9: riconoscimento facciale obbligatorio** | GDPR art. 9: il dato biometrico richiede consenso esplicito e **non si può condizionare il servizio al consenso**. EDPB Linee guida 3/2019 §86: va offerta un'alternativa *"without restraints or additional cost"*. Il Garante ha sanzionato con €50.000 un consenso biometrico "obbligatorio" (eCampus, 29/01/2026). In Svizzera vale lo stesso (nLPD art. 6 + guida EDÖB). | **Check-in online obbligatorio** (dati + documento). Il **riconoscimento facciale è il percorso predefinito proposto all'ospite**, con consenso esplicito, e c'è un'**alternativa equivalente**: videochiamata con operatore. Vedi §3.6 |
| **R6, validità del check-in in Italia** | Consiglio di Stato n. 9101/2025: il controllo deve avvenire *"hic et nunc"* all'**ingresso della struttura**. Un selfie fatto prima dell'arrivo non basta. | Verifica facciale **all'arrivo**, tramite QR rotante esposto all'ingresso. Parere scritto del Viminale prima di eliminare l'operatore umano ⚖ |
| **R2: tutti i flussi in Stripe** | I payout di Airbnb e Booking.com arrivano sul conto bancario dell'agenzia, **fuori da Stripe**. L'unico strumento Stripe per portarli dentro è Treasury for platforms: Italia in *private preview*, solo EUR, **Svizzera non disponibile**. | Le prenotazioni dirette passano interamente in Stripe. Per i ricavi OTA: chiedere subito l'accesso a Treasury, oppure deroga motivata (§3.3, decisione D3) |
| **R4: nessun flusso manuale** | **Non esiste un'API** per: dichiarazioni AdE (imposta di soggiorno annuale, CU/770, DAC7, dati locazioni brevi); notifica ospiti alla polizia in **Ticino** (solo caricamento di file); tassa di soggiorno di Como, Trapani, Tropea, Genova, Portofino, S. Margherita, Cinque Terre e delle OTR ticinesi; F24 (solo per conti Banca Sella o tramite intermediario Entratel); verifica CIN. | Automazione **fino al confine dell'ente**. Oltre quel confine: (a) richiesta formale di API ai fornitori degli enti; (b) **intermediario abilitato** che riceve i dati via API da CasaZen e trasmette (delega R7). Elenco puntuale in §3.5 |

### Le 6 funzionalità in breve

| # | Funzionalità | Soluzione | Fornitori |
|---|---|---|---|
| 1 | OTA: contenuti, ARI, chat AI | Channel manager via API. **Unico fornitore che dichiara contenuti + messaggi per tutte e 4 le OTA: NextPax** (da confermare per iscritto). **Alternativa: Octorate** (Airbnb + Booking.com + Vrbo; manca la gestione dei contenuti su Expedia). Channex e Rentals United sono **scartati** (R5) | NextPax / Octorate |
| 2 | CRM e campagne AI | CRM nel nostro DB, invio via Resend (team separato) o SES, WhatsApp via Meta Cloud API | Resend / SES / Meta |
| 3 | Portale proprietari e fatturazione | Ruolo `PropertyOwner`, rendiconti automatici, split Stripe a 4 parti, fatture con invio SDI via API | Stripe Connect + Invoicing; A-Cube / Invoicetronic |
| 4 | Fornitori e staff propri | Default: marketplace CasaZen con pagamento Stripe e commissione. Opzione in configurazione: staff e fornitori propri a 0% | — |
| 5 | Fiscalità IT + CH | Build dove c'è un'API ufficiale; partner dove no; intermediario per Entratel | Polizia di Stato, Regioni, feratel/Chekin, Taxually, intermediario Entratel |
| 6 | Routine AI e check-in | Motore di attività + AI; check-in biometrico conforme con alternativa video | IDnow / Namirial / Veriff; serrature via Seam/Nuki/igloohome |

**Ordine di grandezza (indicativo, da validare con il team):** circa **110–150 settimane-persona**, cioè 12–18 mesi con 2 sviluppatori. Non include QA, pareri legali e i tempi dei fornitori (conferme scritte, accesso a Treasury, credenziali degli enti).

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
| Fornitori marketplace CasaZen | ✓ | ✓ **default** |
| Staff e fornitori propri a 0% | — | Opzione in configurazione |
| Flusso Stripe | **Direct charge** sull'account dell'host (come oggi, `docs/runbooks/stripe.md:244`) | **Separate charges and transfers** con `on_behalf_of` = agenzia (§3.3) |

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

**Da seguire [NV]:** secondo Normattiva i commi 5 e 5-bis dell'art. 4 DL 50/2017 sono abrogati dal 1/1/2027 da un decreto legislativo del 2026 (probabile spostamento in un testo unico). Va riverificato prima di sviluppare.

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

#### Architettura proposta (prenotazioni dirette)

| Parte | Oggetto Stripe | Note |
|---|---|---|
| CasaZen | Account piattaforma (IT): Billing, Tax, Invoicing + app A-Cube | Abbonamento SaaS dell'agenzia + quota per prenotazione (trattenuta nello split) |
| Agenzia | Account collegato con **contratto di servizio completo** (`card_payments` + `transfers`), su Accounts v2 | È **esercente** (`on_behalf_of`) |
| Proprietario | Account collegato con `transfers`. IT: contratto *recipient* ammesso; **CH: contratto completo** (i recipient non ricevono payout transfrontalieri) | Payout mensile (`interval=monthly`) |
| Ospite | PaymentIntent sulla piattaforma con `on_behalf_of` = agenzia e `transfer_group` = prenotazione | Carte, SEPA DD, **TWINT** (CHF) |

**Sequenza:**
1. L'ospite paga.
2. Quando i fondi sono disponibili (o al check-in), partono due trasferimenti con `source_transaction`:
   - **al proprietario:** canone − commissione agenzia − ritenuta 21% (se dovuta);
   - **all'agenzia:** commissione + IVA + ritenuta 21% da versare + tassa di soggiorno + pulizie.
3. **CasaZen** tiene la differenza come quota di piattaforma.
4. Rimborsi e contestazioni ricadono sulla piattaforma; si recuperano stornando i trasferimenti.

**Fatture:**
- **Agenzia → proprietario:** fattura della commissione via Stripe Invoicing, emessa *on behalf of* l'agenzia e marcata **pagata fuori Stripe** perché già trattenuta nello split. Nessuna commissione Invoicing (<https://docs.stripe.com/invoicing/connect>). Invio SDI automatico via API.
- **Svizzera:** con lo split la QR-bill non serve. Se il saldo del proprietario è negativo (spese superiori ai ricavi), addebito Stripe (SEPA DD / TWINT).

**Vincoli Stripe verificati:**
- I fondi si possono trattenere **al massimo 90 giorni** in IT e CH (<https://docs.stripe.com/connect/account-balances>). Ok per il ciclo mensile, ma non per prenotazioni pagate molti mesi prima del soggiorno se si vuole girare i soldi solo dopo il check-in.
- I conti svizzeri non possono essere regolati in EUR: sui trasferimenti EUR → proprietario CH c'è conversione (1–2%) [NV sul cambio esatto applicato ai trasferimenti].
- **Agenzia svizzera come esercente + split verso proprietari UE: non documentato [NV].** Serve conferma scritta da Stripe.

#### Decisione D2, rischio fiscale e legale ⚖ (bloccante)

Con SCT i fondi transitano dal saldo della piattaforma. L'obbligo di ritenuta ricade su chi *"interviene nel pagamento"* e ha la *"materiale disponibilità"* dei fondi (Circ. AdE 24/E/2017 §2.2.2: <https://www.agenziaentrate.gov.it/portale/documents/20143/297757/Circolare+n.+24+del+12+10+2017_Circolare+n.+24+del+12-10-2017.pdf>).

Rischio concreto: CasaZen potrebbe essere considerata:
- sostituto d'imposta;
- responsabile della tassa di soggiorno;
- gestore di piattaforma ai fini DAC7.

Con `on_behalf_of` = agenzia l'esercente è l'agenzia, il che riduce il rischio. **Prima del go-live serve un parere fiscale, e preferibilmente un interpello all'AdE.**

#### Decisione D3, ricavi OTA (la maggior parte del fatturato di un'agenzia)

| Opzione | Stato | Valutazione |
|---|---|---|
| **Treasury for platforms**: conto finanziario dell'agenzia con IBAN Stripe; l'agenzia vi trasferisce i payout OTA, poi pagamenti in uscita ai proprietari | IT **private preview**, solo EUR, CH non disponibile (<https://docs.stripe.com/treasury/connect>) | **Raccomandata per l'Italia.** Richiedere subito l'accesso a Stripe. CasaZen non detiene mai i fondi |
| Addebito SEPA all'agenzia, poi trasferimenti ai proprietari | Rischio "aggregazione" tra le attività soggette a restrizioni (<https://stripe.com/legal/restricted-businesses>) | Solo con **approvazione scritta di Stripe** |
| Payout OTA fuori Stripe, con bonifici ai proprietari generati automaticamente via API bancaria | Fuori da R2 | Deroga a R2 da decidere. **Unica opzione oggi per la Svizzera** |

#### Rendiconto proprietario (automatico)

- Generato ogni mese dagli eventi di prenotazione, Stripe e spese.
- Contenuto: ricavi per canale, commissione, spese con allegati, ritenute, netto, stato del payout.
- Valuta della proprietà.
- Area documenti e blocchi per uso personale.

**Dato da raccogliere all'onboarding del proprietario:** lo **status fiscale**. Dal 2026 una persona fisica con più di 2 appartamenti in affitto breve è presunta impresa (L. 199/2025 c. 17), e questo determina se si applica la ritenuta.

---

### 3.4 Fornitori e staff propri (solo B2B, R10)

| Elemento | Default B2B | Opzione in configurazione |
|---|---|---|
| Fornitore | Marketplace CasaZen | Rubrica dei fornitori dell'agenzia (`OrgSupplier`) + ruolo `Staff` |
| Pagamento | **Online via Stripe**, con commissione CasaZen. Oggi è dietro il flag `SupplierOnlinePayments` (spento); il default attuale "Pagato" manuale viola R4 | Staff: stipendio fuori da CasaZen. Fornitori propri: **D7, da decidere**, via Stripe a 0% oppure fuori da CasaZen |
| Commissione | 10% "provvisoria" (`SupplierPaymentsOptions`) | 0% |

**Lavoro:**
- rubrica dei fornitori dell'organizzazione (oggi `OrgTrustedSupplier` non esiste: `GlobalSearchService.cs:29,344`);
- invito diretto dei fornitori;
- ruolo `Staff` e assegnazione dei `ServiceRequest`;
- interruttore nell'onboarding o nelle impostazioni dell'agenzia;
- regola di commissione per tipo di fornitore.

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

**Raccomandazione (rispetta R4 il più possibile)**
1. **Build:** Alloggiati SOAP; Ross1000 SOAP (Lombardia, Marche); API Sicilia; interfaccia feratel (Davos).
2. **Richieste formali di API (attività commerciale, da avviare subito):**
   - Polizia cantonale TI;
   - Regioni Liguria e Calabria;
   - Hyksos (StayTour), Advanced Systems, OESIS, Pegaso;
   - destinazioni di Laax, St. Moritz e Andermatt.
3. **Intermediario Entratel partner** (CAF o studio) con integrazione API verso CasaZen, per dichiarazioni AdE e F24. È l'unica via conforme a R7 finché l'AdE non pubblica servizi di invio.
4. **Deroga R4 da decidere (D6):** nelle giurisdizioni "NON POSSIBILE", finché non arriva l'API, CasaZen prepara il file validato e avvisa l'agenzia. In alternativa **non si attiva** la giurisdizione.
5. **Sconsigliato:** automatizzare i portali via browser con le credenziali del cliente (RPA). È fragile, viola i termini dei portali, e il Ticino richiede 2FA.

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

#### Fornitori di verifica identità

| Fornitore | NFC | Dati in UE | Prezzo | Nota |
|---|---|---|---|---|
| **IDnow** | ✓ | Solo data center UE | Su richiesta | ETSI TS 119 461 (dichiarato dal fornitore) |
| **Namirial** (QTSP italiano) | ✓ | ? | Su richiesta | Certificazione ETSI TS 119 461 valida fino al 11/2027; prevede anche un flusso con operatore |
| **Veriff** | ✓ | UE (AWS Irlanda) | da $0,80 (minimo $49/mese) a $1,89 | Conforme iBeta; SDK React Native |
| Stripe Identity | — | Prevalentemente USA | €1,25 | **Sconsigliato per gli ospiti:** vietato sotto i 16 anni; esclusioni per nazionalità; Stripe è **titolare autonomo** e conserva i dati fino a 7 anni; solo *public beta* per aziende IT e CH; non restituisce nazionalità né luogo di rilascio |

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
| Stripe carte (EEA) | 1,5% + €0,25 (standard), 2,8% + €0,25 (premium), 3,15% + €0,25 (internazionali, +2% con conversione) | Agenzia (esercente) |
| Stripe Connect | €2 per account attivo/mese + **0,25% + €0,10 per payout** | CasaZen (da ribaltare) |
| Stripe Billing / Tax / Invoicing | 0,7% / 0,5% / 0,4–0,5% (Invoicing gratis se la fattura è pagata fuori Stripe) | CasaZen / agenzia |
| Verifica identità | ~$0,80–1,89 per verifica (Veriff); IDnow/Namirial su richiesta | Per ospite adulto: pass-through |
| SDI | Invoicetronic €0,10–0,02 per transazione; Openapi €0,07–0,022 | Trascurabile |
| Taxually (dichiarazioni IVA) | Non pubblicato | Agenzia (opzionale) |
| feratel / interfacce di destinazione | A contratto | Agenzia |
| Intermediario Entratel | A contratto (partner) | Agenzia |

Fonti:
- <https://stripe.com/en-it/pricing>
- <https://stripe.com/en-it/connect/pricing>
- <https://www.veriff.com/pricing>
- <https://invoicetronic.com/en/pricing/>

---

## 5. Roadmap a fasi (da validare con PO e team)

| Fase | Contenuto | Note |
|---|---|---|
| **0. Decisioni e conferme esterne** (in parallelo, avvio immediato) | Pareri ⚖ (D2 SCT/ritenuta/DAC7, D5 biometria + DPIA, richiesta al Viminale); richiesta a Stripe (Treasury, conferma sull'agenzia svizzera come esercente, pre-approvazione del modello); 11 domande + PoC a NextPax/Octorate; richieste di API a enti e fornitori (§3.5) | Determina il perimetro reale |
| **1. Fondamenta** | `OrgType.Agency`; ruoli `PropertyOwner` e `Staff`; paese, valuta, lingua DE; matrice delle funzionalità per piano; ADR "channel manager", "flussi Stripe B2B", "provider AI per dati personali" | Sblocca tutto |
| **2. Compliance e check-in** | Alloggiati SOAP, Ross1000, API Sicilia, feratel Davos; check-in online con documento e NFC + verifica all'arrivo (A/B) + serrature | Massimo valore di compliance |
| **3. Denaro** | SCT a 4 parti, rendiconti, fatture con invio SDI, payout mensili; fornitori marketplace via Stripe + opzione staff propri | Dipende da D2 e D3 |
| **4. Distribuzione** | Channel manager (contenuti + disponibilità/tariffe + prenotazioni) + inbox + AI | Dipende dalla scelta del fornitore |
| **5. Crescita** | CRM + campagne + WhatsApp; nuove giurisdizioni man mano che arrivano le API | — |

---

## 6. Decisioni aperte per il PO

| ID | Decisione | Raccomandazione |
|---|---|---|
| D1 | Matrice B2C/B2B e limiti per piano (§1.2) | Approvare la matrice; definire il listino B2B per unità |
| D2 | Modello di addebito B2B | SCT con `on_behalf_of` = agenzia, **subordinato a parere fiscale o interpello** ⚖ |
| D3 | Ricavi OTA verso i proprietari | Richiedere Treasury (IT). Per la Svizzera: deroga a R2 oppure attendere |
| D4 | Fornitore OTA | NextPax o Octorate, scelto dopo risposte scritte e PoC |
| D5 | Riconoscimento facciale | Percorso predefinito con consenso + alternativa video (R6 "obbligatorio" non è lecito) ⚖ |
| D6 | Giurisdizioni senza API | Intermediario Entratel partner per l'AdE; per comuni e Ticino scegliere tra deroga temporanea a R4 o mancata attivazione |
| D7 | Pagamento dei fornitori propri dell'agenzia | Via Stripe a 0% (coerente con R2) oppure fuori da CasaZen |
| D8 | Provider LLM per i dati degli ospiti | DPA UE, nessun addestramento sui dati; **non DeepSeek** (oggi è il provider configurabile) |

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
