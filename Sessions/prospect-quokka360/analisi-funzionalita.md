# CasaZen: tipo host Agenzia (B2B), analisi finale

**Versione:** finale (v4), 2026-10-15. Sostituisce le versioni dal 14 al 15/10 (storico in git).
**Origine:** le 6 funzionalità che il prospect Quokka 360 ritiene indispensabili, generalizzate come prodotto per tutte le agenzie.
**Stato:** analisi pre-vendita. Prezzi e numeri **da rivedere con il business plan**.

**Metodo:**
- analisi del codice (`backend`, `frontend`, `mobile`);
- misura della velocità di sviluppo dallo storico git;
- ricerca su fonti ufficiali (OTA, Stripe, enti pubblici, autorità privacy, listini dei fornitori e dei concorrenti);
- intervista al PO.

**Legenda:** **[NV]** = non verificato su fonte ufficiale · **⚖** = parere legale o fiscale necessario prima del go-live · ✓ = verificato direttamente.

---

## 0. Sintesi

**Cosa si costruisce.** Un nuovo tipo di host `Agency` dentro il prodotto CasaZen (non un gestionale su misura per un cliente). Contiene le 6 funzionalità richieste:
1. OTA con contenuti e chat AI;
2. CRM con campagne;
3. portale proprietari con fatturazione;
4. fornitori e staff propri;
5. fiscalità Italia e Svizzera;
6. routine configurabili dall'host (QUANDO → SE → ALLORA, anche create con l'AI) e check-in biometrico.

OTA, CRM, fiscalità e routine sono disponibili anche al B2C, con limiti per piano.

**Scelte chiave:**

| Tema | Scelta |
|---|---|
| OTA | Channel manager via API con **chat e modifica dei contenuti obbligatorie**. PoC con **NextPax** e **Octorate**; Channex e Rentals United scartati |
| Pagamenti | **Si applica il modello di mercato:** l'agenzia è l'esercente (direct charge sul suo account Stripe con `fees_collector = stripe`). **Stripe addebita tutti i suoi costi direttamente all'agenzia**: CasaZen non incassa nulla e non paga nulla a Stripe |
| Proprietari | Rendiconto mensile automatico + **file di bonifico SEPA/CH** che l'agenzia approva in banca (come fa Avantio). Su Airbnb, quando l'annuncio è del proprietario, si usa la **ripartizione nativa co-host**. Automazione completa in seguito con EBICS (CH) |
| Adempimenti | API dove esistono (Alloggiati, ISTAT Lombardia/Marche/Sicilia, feratel); **intermediario abilitato** per Agenzia delle Entrate e F24; **back-office delegato** per comuni e Ticino. L'utente fa tutto da CasaZen e riceve la ricevuta in CasaZen |
| Check-in | Riconoscimento del documento e del volto **via API di terzi** (PoC Didit e Sumsub). Per legge serve un'alternativa in videochiamata. Immagini cancellate alla ricevuta Alloggiati |
| Serrature | **Seam**, come fa il mercato. Costo ribaltato all'agenzia; codice rilasciato **solo dopo l'identificazione** |
| Prezzo (proposta) | Base **€7–9/unità/mese** a scaglioni (portale proprietari incluso) + AI e Marketing €4 + verifiche €0,75; **tutto compreso €13–15**; programma **Founding Partner** a costo + €1/unità, con margine bloccato |
| Sviluppo | **~10–16 settimane attive (~2,5–4 mesi)** con 1 abbonamento Claude Code Max 20x + PO. Il percorso critico sono i tempi esterni |

**Rischi principali:**
1. tempi esterni: fornitori OTA, verifica identità, intermediario, enti;
2. obbligo di un'alternativa non biometrica e verifica all'ingresso (CdS 9101/2025) ⚖;
3. adempimenti senza API (Ticino, diversi comuni): coperti dal back-office, che è un costo operativo;
4. norme fiscali italiane rinumerate dal 1/1/2027.

---

## 1. Regole di prodotto (decise dal PO)

| # | Regola |
|---|---|
| R1 | Quokka è un **cliente agenzia**. CasaZen resta il fornitore del servizio; ogni funzionalità è di prodotto |
| R2 | Il B2B è un nuovo tipo di host `Agency` con una propria gestione: agenzia, CasaZen, ospite (diretto), proprietari. Pagamenti in Stripe; **dal 15/10 si applica il modello di mercato** (§3.3) |
| R3 | Non si replica Kross Booking: le 6 funzionalità sono condizione per collaborare |
| R4 | Nessun flusso manuale, tranne la firma dei contratti. **Eccezione accettata:** l'approvazione dei bonifici in banca, che è obbligatoria per l'autenticazione forte (SCA) |
| R5 | Chat AI e modifica dei contenuti obbligatorie nell'API del fornitore OTA |
| R6 / R9 | Check-in con riconoscimento del documento e del volto: **predefinito nel B2B, opzionale nel B2C**. Il consenso non può essere obbligatorio per legge (§3.6) |
| R7 | Delegare a terzi il più possibile, sapendo che **la responsabilità di legge non si delega** (§4) |
| R8 | **Prodotto unico B2C + B2B**, con limiti per piano. Portale proprietari, staff e fornitori propri solo B2B |
| R10 | Fornitori CasaZen sempre il default. I fornitori propri sono registrati in CasaZen: 0% e fatturazione fuori da CasaZen quando li usa la loro agenzia |
| R11 | **Tutti i costi Stripe a carico dei clienti** |

---

## 2. Registro delle decisioni

| ID | Decisione | Stato |
|---|---|---|
| D1 | CasaZen si fa pagare **solo a canone** (per unità, più opzioni e verifiche): nessuna percentuale sulle prenotazioni | Deciso |
| D2 | Incasso delle prenotazioni dirette: **l'agenzia è l'esercente**, con direct charge sul suo account Stripe (Accounts v2, `fees_collector = stripe`). CasaZen non incassa | **Rivisto il 15/10** (prima: CasaZen incassa e Stripe ripartisce), su indicazione del PO di applicare il comportamento di mercato |
| D3 | Pagamento dei proprietari: rendiconto + file di bonifico SEPA/CH approvato dall'agenzia in banca; ripartizione Airbnb co-host dove possibile; EBICS (CH) e aggregatore o Treasury (IT) come automazione successiva | **Deciso secondo il mercato** |
| D4 | Fornitore OTA: 11 domande + PoC con NextPax e Octorate, scelta sulle risposte scritte | Deciso |
| D5 | Biometria via API di terzi; conservazione conforme al Garante; PoC Didit e Sumsub | Deciso |
| D6 | Tutto da CasaZen con la ricevuta in CasaZen: API, intermediario partner, back-office delegato; costi extra accettati | Deciso |
| D7 | Fornitori propri: registrati in CasaZen; 0% e fatturazione esterna con la propria agenzia, regole del marketplace con gli altri; interruttore "visibile nel marketplace" | Deciso |
| D8 | LLM: DeepSeek (costo). **Raccomandazione aperta:** per i dati degli ospiti usarlo ospitato in UE (AWS Bedrock, Stoccolma) | Deciso con riserva |
| D9 | Serrature: Seam, costo ribaltato all'agenzia, codice solo dopo l'identificazione | Deciso (raccomandazione accettata) |
| D10 | Prezzi: proposta in §6, **da rivedere con il business plan** | Aperto |

---

## 3. Soluzione per funzionalità

### 3.1 OTA: contenuti, disponibilità/tariffe/prenotazioni, chat AI

**Oggi in CasaZen:** iCal reale (ADR-002); adapter OTA stub e congelati (D10 del risanamento); nessuna entità messaggi.

**Connessione diretta alle OTA: chiusa.**
- Booking.com: *"we are pausing integrations with new connectivity providers"* ✓ (<https://connect.booking.com/>).
- Airbnb: accesso API solo su invito (<https://www.airbnb.com/terms/api>).

| Fornitore | Contenuti via API | Chat via API | Esito |
|---|---|---|---|
| **NextPax** Supply API | Airbnb, Booking.com, Vrbo; Expedia solo modifica. Attivazione del "Full Content" via API **da confermare** ✓ | Tutte e 4, con webhook | **Candidato 1** |
| **Octorate** (OpenAPI pubblica) | Airbnb, Booking.com, Vrbo; **no Expedia** | Sì, con webhook | **Candidato 2** (vende anche ai gestori) |
| Rentals United | Creazione su Booking.com/Expedia solo da interfaccia | Sì (ristretta) | Scartato |
| Channex | **No** (roadmap 2027) ✓ | Sì | Scartato |

**Prezzo NextPax:** €3,50/unità (€3,15 con fatturazione annuale), minimo €350/mese con 50 unità incluse ✓ (<https://nextpax.com/nextpax-pricing>).

**Da sviluppare in CasaZen** (non è "solo configurazione", perché l'utente deve fare tutto da CasaZen):
- client API del partner;
- collegamento dei canali e mappatura;
- invio di disponibilità, prezzi e restrizioni;
- prenotazioni che entrano in check-in, Alloggiati e rendiconti;
- editor dei contenuti;
- inbox web e mobile;
- risposte AI in bozza;
- recensioni.

**Vincoli per l'AI:** su Airbnb niente contatti né link prima della prenotazione; cambi data su Booking.com dall'extranet; trasparenza AI Act art. 50; invio automatico solo sopra una soglia di confidenza.

### 3.2 CRM con campagne e AI

**Vincolo:** Airbnb vieta il marketing verso i suoi ospiti ✓ (<https://www.airbnb.com/help/article/2799>). Booking.com lo consente solo con un consenso raccolto dall'agenzia.

**Soluzione:**
- CRM sul nostro database: consensi per canale e base giuridica, segmenti, campagne, lista di soppressione per tenant;
- invio con un account Resend separato, un dominio per agenzia;
- WhatsApp con Embedded Signup (Meta fattura l'agenzia);
- testi AI in IT/EN/DE/FR con approvazione umana.

### 3.3 Portale proprietari, pagamenti e fatturazione (solo B2B)

#### Come fa il mercato (verificato su pagine ufficiali)

| Aspetto | Prassi dominante | Esempi |
|---|---|---|
| Chi è l'esercente sulle prenotazioni dirette | **Il gestore**, con il proprio account di pagamento; il software quasi mai | Guesty: chi incassa è il merchant of record; Lodgify Payments: *"You are the merchant of record"*; Hostaway, Hostfully, Smoobu, Uplisting, OwnerRez, Beds24 usano l'account Stripe del gestore |
| Chi paga le commissioni | Il gestore; ribaltarle sul proprietario è un'**impostazione del contratto** con il proprietario | Guesty "processing fees to PMC or owner"; OwnerRez rimborso spese o "PM will pay fees" |
| Commissione del software sui pagamenti | Spesso 0,5–1% (Guesty, Hostaway, Hostfully, Lodgify, Rentals United); assente in Smoobu, Beds24, OwnerRez | — |
| Come vengono pagati i proprietari | **Rendiconto + bonifico del gestore** dal proprio conto. In UE al massimo il software esporta il **file SEPA XML** | Avantio ✓: *"Avantio generates the XML file, but you must upload it to your bank's intranet"* |
| Pagamenti eseguiti dal software | Solo negli USA (ACH: Guesty, Hospitable, Hostfully, Track, Streamline) | — |
| Ripartizione automatica al momento del pagamento | Rara. Alternativa: incasso direttamente sull'account Stripe del proprietario per annuncio, con il gestore che fattura la commissione | Rentals United, Hostaway, OwnerRez "direct owner payments" |
| Ricavi OTA | Airbnb ripartisce in modo nativo tra proprietario e co-host; Booking.com no ✓ | Airbnb: limite di un solo co-host fuori da USA/UE (quindi in CH); Booking.com: *"payouts for a single property can only be sent to one active bank account"* |

Fonti:
- <https://help.guesty.com/hc/en-gb/articles/13079241973917-Collecting-guest-payments>
- <https://cdn.lodgify.com/transactional/payments/Terms_and_Conditions_-_Payment_Services_05052025.pdf>
- <https://support.avantio.pro/en/articles/419441-sepa>
- <https://www.ownerrez.com/support/articles/direct-owner-payments>
- <https://www.airbnb.com/help/article/3767>
- <https://partner.booking.com/en-us/help/policies-payments/payment-products/payments-bookingcom-faqs>

#### Modello CasaZen (D2 + D3, allineato al mercato)

1. **Prenotazione diretta.** PaymentIntent come **direct charge sull'account Stripe dell'agenzia** (già il modello attuale di CasaZen, `docs/runbooks/stripe.md:244`).
   - Gli account delle agenzie passano da Express v1 ad **Accounts v2 con `fees_collector = stripe`**. Stripe stessa sconsiglia le direct charge con Express ✓.
   - Così *"Stripe collects fees directly from your connected account. We don't charge any Connect fees to it or to your platform"* ✓ (<https://docs.stripe.com/connect/direct-charges-fee-payer-behavior>).
   - **Costo Stripe per CasaZen: zero** (R11).
2. **Rendiconto mensile automatico per proprietario:** ricavi per canale (OTA + diretti), commissione dell'agenzia, spese con allegati (anche dei fornitori propri, D7), ritenuta 21% se dovuta, commissioni Stripe (ribaltate o no, secondo il contratto col proprietario), netto da pagare. Valuta della proprietà, PDF e area documenti.
3. **Pagamento ai proprietari:** CasaZen genera il **file di bonifico** (SEPA pain.001/CBI in Italia, pain.001 in Svizzera). L'agenzia lo approva in banca una volta al mese.
   - **Automazione successiva:**
     - **EBICS** (CH, canale gratuito presso PostFinance, Raiffeisen, ZKB, BancaStato): CasaZen invia il lotto, l'agenzia firma;
     - **aggregatore autorizzato** (es. Fabrick) oppure **Stripe Treasury** (IT, *private preview*).
4. **Airbnb:** quando l'annuncio è intestato al proprietario e l'agenzia è co-host, si usa la **ripartizione nativa** di Airbnb. La ritenuta del 21% Airbnb la calcola sul lordo, prima della quota co-host, e la quota co-host non è soggetta a ritenuta ✓ (<https://www.airbnb.com/help/article/3527>). In Svizzera è ammesso un solo co-host per annuncio.
   - **Per Quokka non si applica:** gli annunci sono intestati a Quokka su tutte le OTA (dato del PO). Tutti i ricavi OTA arrivano a Quokka, che paga i proprietari con il file di bonifico ed è l'intermediario che incassa: ritenuta del 21% sui proprietari privati italiani e relativa CU (registro già previsto nell'opzione Proprietari).
5. **Documenti fiscali generati da CasaZen per conto dell'emittente**, con invio SDI via API (Invoicetronic, A-Cube o Openapi):
   - fattura dell'agenzia al proprietario (mensile, differita);
   - fattura di CasaZen all'agenzia;
   - dati CU per la ritenuta.
   - Una fattura copre un solo fornitore (art. 21 DPR 633/72, art. 72 del TU IVA dal 2027).

**Cosa cambia rispetto al modello precedente** (CasaZen incassa e Stripe ripartisce):

| Aspetto | Prima | Ora |
|---|---|---|
| Rischio che CasaZen sia sostituto d'imposta (ritenuta), responsabile della tassa di soggiorno o soggetta a DAC7 per l'incasso | Presente ⚖ | **Eliminato**: incassa l'agenzia. Resta un parere sul DAC7 per il sito di prenotazione, a rischio ridotto |
| Account Stripe per ogni proprietario | Necessari (con verifica KYC, €2/mese ciascuno) | **Non servono** |
| Fondi trattenibili al massimo 90 giorni | Vincolo | Non applicabile |
| Complessità di WP4 | Alta | Ridotta |
| R2 "tutto in Stripe" | Rispettata per i pagamenti diretti | Allentata: il pagamento ai proprietari passa dalla banca dell'agenzia, come fa il mercato |

### 3.4 Fornitori e staff propri (solo B2B)

| | Marketplace CasaZen (default) | Fornitore proprio (opzione in configurazione) | Staff |
|---|---|---|---|
| Registrazione | Organizzazione `Supplier` | Organizzazione `Supplier` invitata dall'agenzia e legata a lei; nessun vincolo di comune, anche CH | Ruolo `Staff` |
| Pagamento | Online via Stripe con commissione (oggi flag `SupplierOnlinePayments` spento) | Nuova modalità `External`: 0%, fattura fuori da CasaZen | Fuori da CasaZen |
| Lavori per terzi | — | Ammessi, con le regole del marketplace; interruttore "visibile nel marketplace" | — |
| Rendiconto proprietario | Importo Stripe | Importo approvato sulla richiesta | Tariffa interna se riaddebitata |

### 3.5 Fiscalità e adempimenti Italia + Svizzera

**API ufficiali, automatizzate da CasaZen:**
- **Alloggiati Web:** SOAP, 12 operazioni ✓ (<https://alloggiatiweb.poliziadistato.it/service/service.asmx>).
- **ISTAT:**
  - Lombardia: Ross1000 `checkinV2` ✓;
  - Marche: Ross1000;
  - Sicilia: API HTTPS per gestionali (documentazione su richiesta);
  - Liguria e Calabria: web service da confermare con le Regioni.
- **Davos:** interfaccia feratel. Laax e Andermatt hanno tasse forfettarie o per capacità, quindi bastano promemoria.

**Senza API, ma con ricevuta in CasaZen (D6):**

| Adempimento | Chi esegue |
|---|---|
| Dichiarazione annuale imposta di soggiorno, CU/770, dati locazioni brevi, DAC7, F24 | **Intermediario partner** (CAF o studio): CasaZen genera i file validati, il partner li trasmette e restituisce la ricevuta. ~€30 per dichiarazione (esempio pubblico) |
| StayTour (Como, Trapani, Tropea), TouristTax (Genova, Portofino, S. Margherita), SoggiorniAmo (Milano) | **Back-office delegato**: operatore con le proprie credenziali (StayTour: fino a 3 delegati SPID) |
| Pagamento pagoPA | Openapi |
| **Ticino**: notifica alla polizia | Back-office: operatore abilitato dal gestore, caricamento di un file cumulativo ogni giorno, **7 giorni su 7, entro 24 ore** |
| Pegaso (Ascoli Piceno), Cinque Terre | Da verificare |

**Vincoli:**
- Automatizzare lo SPID dell'agenzia (RPA) non è lecito: le credenziali sono di uso personale (AgID).
- CasaZen non può essere intermediario Entratel.
- Il **Ticino vieta di fotocopiare il documento** ✓ (RL 942.120 art. 6).

### 3.6 Check-in biometrico, serrature, routine

**Vincoli legali:**
- **Consiglio di Stato 9101/2025** ✓: identificazione de visu, oppure **videocollegamento all'ingresso "hic et nunc"**. Un selfie prima dell'arrivo non basta.
- **GDPR art. 9:** consenso esplicito e **alternativa equivalente obbligatoria** (EDPB 3/2019 §86; Garante, eCampus 29/01/2026, €50.000 ✓).
- **Garante, nota 29/04/2026** ✓: le copie dei documenti si cancellano alla ricevuta Alloggiati.
- AI Act: la verifica 1:1 non è ad alto rischio.

**Flusso:**
1. check-in online obbligatorio, con documento letto via OCR o NFC;
2. all'arrivo, **QR rotante all'ingresso**:
   - **(A) predefinito:** liveness + confronto del volto, con consenso;
   - **(B) alternativa:** videochiamata con operatore;
3. esito negativo → operatore;
4. minori senza biometria;
5. DPIA prima del lancio.

**Fornitori di verifica:**

| Fornitore | Prezzo | Punto di forza |
|---|---|---|
| **Didit** ✓ | $0,33 (+$0,15 NFC) a verifica | Costo |
| **Sumsub** ✓ | $1,35 a verifica, minimo $149/mese | Modalità "cancella solo le foto del documento" e certificazione anti-frode di livello più alto |

Scartati: Veriff (massimo 10 cancellazioni al giorno) e Stripe Identity (titolare autonomo dei dati, non verifica i minori).

**Serrature:** Quokka oggi usa **keybox Masterlock** ✓ (condizioni su `quokka.kross.travel`).
- Il mercato usa Seam e fa pagare all'host €3–8 per serratura al mese.
- **Kross (KDoor da €29/mese) ✓, Chekin e Vikey rilasciano il codice solo dopo l'identificazione**: in Italia è di fatto obbligatorio, quindi lo facciamo anche noi.

**Routine (definizione del PO, 2026-10-15): costruttore di automazioni configurabile dall'host.** L'host crea le proprie routine nel formato **QUANDO** (evento) → **SE** (condizioni) → **ALLORA** (una o più azioni, anche con attese e passaggi successivi).

| Elemento | Catalogo proposto |
|---|---|
| **Eventi (QUANDO)** | Prenotazione nuova/confermata/modificata/cancellata; X giorni/ore prima o dopo check-in e check-out; check-in o check-out avvenuto; messaggio dell'ospite ricevuto; recensione ricevuta; pagamento riuscito/fallito; check-in online completato; **identificazione completata**; ricevuta Alloggiati arrivata; attività completata; batteria della serratura scarica; ricorrenza settimanale |
| **Condizioni (SE)** | Proprietà o tag, canale, durata del soggiorno, numero di ospiti, anticipo della prenotazione, stato del pagamento, stato del check-in/identificazione, lingua dell'ospite, giorni liberi prima o dopo |
| **Azioni (ALLORA)** | Messaggio all'ospite (OTA, email, WhatsApp, SMS); notifica a proprietario, staff o team; crea un'attività (pulizia, manutenzione, check-in) e assegnala a staff, fornitore proprio o marketplace; **invia il codice della serratura** (solo se l'identificazione è completata); richiesta di recensione; aggiungi un tag; attesa; webhook verso sistemi esterni |
| **AI** | Opzione AI e Marketing: **creazione della routine da una frase** ("al check-out manda la pulizia e dopo 2 giorni chiedi la recensione"), con anteprima da approvare; azioni AI (messaggio personalizzato, sintesi, traduzione, controllo foto) |
| **Libreria pronta** | Routine predefinite da attivare con un clic: pulizia al check-out, istruzioni 2 giorni prima, recensione dopo la partenza, promemoria documenti mancanti |

**Guardrail** (la routine non può aggirare le regole):
- il codice della serratura parte solo dopo l'identificazione;
- niente contatti né link prima della prenotazione su Airbnb;
- campagne solo con consenso;
- registro di ogni esecuzione;
- modalità di prova prima dell'attivazione.

**Cosa fa il mercato** (help center ufficiali):
- **Hostaway, Guesty, Hospitable, Hostfully, OwnerRez, Uplisting e Breezeway** offrono evento + condizioni + **una** azione: messaggio o attività.
- **Solo Enso Connect** ✓ ha passaggi multipli con rami sì/no, webhook e creazione da linguaggio naturale: *"Tell it what you want in plain English and it drafts the when, if, then for you"* (<https://ensoconnect.com/features/custom-automations>).
- **Nessuno** collega le routine a eventi di compliance (identificazione, Alloggiati): è un elemento distintivo di CasaZen.

Fonti:
- <https://support.hostaway.com/hc/en-us/articles/50066767703195-Inbox-Set-Up-Message-Automations>
- <https://help.guesty.com/hc/en-gb/articles/9369482431389-Applying-message-conditions-to-automated-messages>
- <https://help.hospitable.com/en/articles/4598563-task-rules-how-to-create-a-task-rule>
- <https://help.hostfully.com/en/articles/1674808-create-custom-templates-and-triggers>
- <https://www.ownerrez.com/support/articles/triggers-setup-configuration>

---

## 4. Stripe e fiscalità: cosa si delega e cosa no

| Adempimento | Stripe | Soluzione |
|---|---|---|
| Commissioni di pagamento | Addebitate all'agenzia (`fees_collector = stripe`) ✓ | — |
| IVA sul soggiorno | Nessun codice per alloggio in Stripe Tax ✓ | Aliquote da `TaxRate` |
| Dichiarazione e versamento IVA IT/CH | No ✓ | Taxually (opzionale, per l'agenzia) |
| Fattura elettronica | No ✓ | Provider SDI via API |
| Tassa di soggiorno, ritenuta, F24, DAC7 | No | Intermediario partner + file generati da CasaZen |

**Responsabilità che per legge non si possono delegare:** art. 109 TULPS (gestore), responsabile dell'imposta di soggiorno (chi incassa), sostituto d'imposta (intermediario che incassa), IVA (soggetto passivo), Kurtaxe e notifica degli ospiti in CH (locatore).
→ **Promessa commerciale corretta: "compliance automatizzata", non "compliance delegata".**

**Norme che cambiano dal 1/1/2027:**
- **Testo unico IVA**, D.Lgs. 10/2026 ✓ (<https://www.normattiva.it/eli/id/2026/01/30/26G00022/ORIGINAL>);
- l'art. 4 DL 50/2017 confluisce probabilmente nel D.Lgs. 117/2026, artt. 207–208 [NV].
- Riverificare tutti i riferimenti prima di sviluppare.
- Dal 2026, oltre 2 appartamenti in affitto breve si presume attività d'impresa (L. 199/2025 c. 17): lo status fiscale del proprietario si raccoglie all'onboarding.

---

## 5. Chi paga cosa: costi di CasaZen, prezzi ai clienti, costi pagati dai clienti a terzi

Prezzi ufficiali, IVA esclusa, al 2026-10-15. Per prudenza USD = EUR 1:1.

### 5.1 Cosa paga CasaZen

**Costi fissi** (si pagano anche con zero clienti, con tutte le integrazioni attive):

| Voce | €/mese | Nota |
|---|---|---|
| NextPax, minimo mensile | 350 | Include le prime 50 unità; si paga dalla firma del contratto |
| Hosting di produzione (Railway Pro + consumo, Supabase Pro) | ~85 | Oggi CasaZen è su piani gratuiti/minimi |
| Resend Scale (email, un dominio per agenzia) | 90 | Con pochi clienti basta Pro a $20 |
| Invoicetronic, chiave di produzione | 5 | Le transazioni sono pacchetti prepagati |
| Abbonamento Claude Code Max 20x (sviluppo) | ~200 [NV] | La pagina ufficiale mostra solo "from $100"; fa fede l'addebito reale |
| Sumsub, minimo (solo se scelto al posto di Didit) | (149) | Didit non ha minimi |
| **Totale fisso** | **~€730** (~€880 con Sumsub) | |

**Costi variabili** (crescono con le unità e gli ospiti):

| Voce | Costo | Nota |
|---|---|---|
| NextPax oltre le 50 unità | €3,15/unità (annuale) | |
| Verifica identità Didit | $0,33 (+$0,15 con NFC) a verifica | Sumsub $1,35 |
| Serrature Seam | $5 per serratura | Rivenduto a €6 |
| Fatture SDI, AI DeepSeek, video Daily | ~€0,4/unità | Daily gratis fino a 10.000 minuti |
| **Back-office "adempimenti gestiti"** | **€0 di cassa: per ora lo svolge il PO** (decisione del 2026-10-15) | Tempo stimato per 250 unità: ~15–20 ore/mese (Ticino ~15 min/giorno + ~5 min/unità/mese sui portali comunali). Ipotesi da validare |

### 5.2 Cosa pagano i clienti direttamente a terzi

Non sono né costi né ricavi per CasaZen:
- **Stripe:** commissioni sugli incassi, addebitate direttamente all'agenzia (`fees_collector = stripe`);
- **WhatsApp (Meta):** fatturato all'agenzia;
- **pagoPA:** €2 a pagamento, a carico di chi paga;
- **intermediario fiscale:** pratiche (~€30 la dichiarazione annuale), fatturate al costo;
- **Taxually** (dichiarazioni IVA, opzionale);
- **serrature:** l'hardware (igloohome da $129) e gli eventuali abbonamenti della marca (es. Nuki Smart Hosting).

### 5.3 Scenari mensili

| | **A. Tutte le integrazioni attive, 0 clienti** | **B. Quokka, 250 unità, prezzo partner** | **C. 250 unità a listino** (tutto compreso €13) |
|---|---|---|---|
| **Paga CasaZen** | | | |
| Costi fissi | €730 | €730 | €730 |
| NextPax oltre il minimo (200 unità) | — | €630 | €630 |
| Verifiche d'identità (2.000/mese) | — | €660–960 | €660–960 |
| SDI, AI, video | — | ~€100 | ~€100 |
| **Totale costi CasaZen** | **€730** | **€2.120–2.420** | **€2.120–2.420** |
| **Pagano i clienti a CasaZen** | | | |
| Canone | — | €1.730 (partner) | €3.250 (8 verifiche/unità incluse) |
| Verifiche | — | €660–960 (al costo) | incluse |
| **Totale ricavi CasaZen** | **€0** | **€2.390–2.690** | **€3.250** |
| **Risultato mensile CasaZen** | **−€730** | **+€270** | **+€830–1.130** |
| **Extra opzionali** (ricavo − costo) | — | Serrature: 250 × (€6 − $5) ≈ +€250 · Adempimenti gestiti: €4 × unità senza API − costo dell'operatore | Come B |

**Come leggere la tabella:**
- **Scenario A** (piattaforma pronta, nessun cliente): CasaZen perde ~€730 al mese, di cui €350 sono il minimo NextPax. Conviene firmare con NextPax solo quando il primo cliente è vicino.
- **Scenario B** (Quokka partner): il partner copre tutti i costi, compresi i fissi, e lascia ~€270 al mese di margine. È un pareggio voluto, non un guadagno.
- **Scenario C** (stessa dimensione a listino): ~€830–1.130 al mese. La differenza con B è il prezzo che si rinuncia a chiedere ai partner.
- **Back-office:** per ora lo svolge il PO, quindi l'opzione "adempimenti gestiti" (€4/unità dove non c'è API) è tutta margine di cassa.
- **Condizioni per farlo in modo sostenibile:**
  - CasaZen deve preparare in automatico i file (XML Ticino, CSV comunali) e i promemoria, così che l'operatore debba solo caricarli e scaricare la ricevuta;
  - serve **un sostituto** abilitato sugli stessi portali (con proprie credenziali) per ferie e malattia: la notifica in Ticino va fatta entro 24 ore, 7 giorni su 7;
  - mandato scritto e accordo sul trattamento dei dati (GDPR art. 28) con l'agenzia, anche se l'operatore è il PO.
- **Non sono inclusi:** il tempo del PO (assistenza, vendita, back-office), i pareri legali una tantum e il costo di sviluppo (§7).

**Rispetto ai calcoli precedenti:** i costi fissi passano da ~€400 a ~€730/mese, perché il minimo NextPax (€350) ora è contato tra i fissi e non per unità. Il canone partner di Quokka (§6.3) resta ~€1.730 + verifiche: copre comunque tutti i costi.

---

## 6. Proposta di prezzo (da rivedere con il business plan)

### 6.1 Come prezzano i concorrenti (pagine ufficiali)

**Struttura:**
- **prezzo per unità con scaglioni di volume** e minimo mensile ($40–250 o £90–208, oppure quota di piattaforma da $99);
- preventivo sopra 20–200 unità;
- sconto annuale **10–12%** (fino al 20% su 2 anni).

**Prezzi di riferimento:**

| Concorrente | Prezzo |
|---|---|
| Kross Premium ✓ | €6/unità (gestionale + channel manager) |
| Pacchetto italiano Kross + Vikey | ~€16 |
| Uplisting Manager ✓ | £13 + verifica d'identità £1,25 a controllo |
| Avantio | $16–20 |
| OwnerRez ✓ | $2–8 a unità (scala decrescente) + modulo proprietari $0,50–10 + AI $9 + serrature $4 |
| Hostaway (serrature) | 8 → 3 per dispositivo |

**Opzioni sul mercato:**
- portale proprietari: gratis, incluso in un piano superiore oppure ~£8;
- verifica d'identità: £1,25–$8 a controllo (costo all'ingrosso $0,35–1,25);
- serrature: $3–8.

**Programmi "founding" con prezzo bloccato: quasi assenti.** Solo casi di prezzi storici mantenuti (Hospitable Legacy, OwnerRez); Host Tools li ha poi revocati. Un programma scritto e chiaro sarebbe distintivo.

**Quokka oggi paga ~€2.000/mese a Kross** (dato del PO).

### 6.2 Proposta di listino

Prezzi per unità al mese, IVA esclusa, **con pagamento annuale** (+10% con pagamento mensile). Minimo €90/mese.

**Cosa contiene ogni pacchetto** (aggiornato il 2026-10-15: il portale proprietari è nella base, il Marketing costa di più):

| Pacchetto | Cosa offre | Prezzo |
|---|---|---|
| **Base** | **Gestionale**: proprietà, calendario, prenotazioni, ospiti, KPI.<br>**Channel manager** (Airbnb, Booking.com, Vrbo, Expedia): sincronizzazione di disponibilità, prezzi e prenotazioni; creazione e modifica degli annunci da CasaZen; recensioni.<br>**Inbox unificata** con **risposte AI suggerite**, inviate dall'operatore.<br>**Sito di prenotazione diretta** con pagamento Stripe sull'account dell'agenzia.<br>**Check-in online**: dati degli ospiti e documento, senza biometria.<br>**Adempimenti via API**: Alloggiati Web, ISTAT (regioni con API), feratel; calcolo della tassa di soggiorno; **file pronti** per dichiarazioni, CU e portali senza API.<br>**Portale proprietari**: dati e documenti, rendiconto mensile automatico, fatture elettroniche SDI per conto dell'agenzia, file di bonifico SEPA/CH, registro delle ritenute.<br>**Routine**: costruttore QUANDO → SE → ALLORA con libreria di routine pronte.<br>**Fornitori**: marketplace, fornitori propri a 0%, staff, app | **€9** (10–49 unità) · **€8** (50–149) · **€7** (150–399) · preventivo (400+) |
| **+ AI e Marketing** | Risposte AI **inviate in automatico** (sopra una soglia di confidenza), **routine create da una frase** e azioni AI nelle routine, **CRM**: profili ospite, consensi, segmenti, campagne email e WhatsApp con testi AI | **+€4** |
| **Check-in biometrico** | Riconoscimento del documento e del volto all'ingresso, con alternativa in videochiamata | **€0,75 a verifica** (B2B: obbligatorio) |
| **Serrature smart** | Integrazione della serratura già installata (via Seam): codice unico per ogni soggiorno, inviato solo dopo l'identificazione, revocato alla partenza o alla cancellazione, stato della batteria. **Non include l'acquisto della serratura** | **€6 per serratura al mese** (costo CasaZen $5) |
| **Adempimenti gestiti** | Servizio svolto da una persona (oggi il PO): caricamento dei file sui portali **senza API** (polizia del Ticino ogni giorno entro 24 ore, portali comunali della tassa di soggiorno), con la ricevuta salvata in CasaZen. Senza questa opzione l'agenzia carica da sola i file che CasaZen prepara | **€4/unità al mese**, solo per le unità nelle giurisdizioni senza API |
| **Tutto compreso** | Base + AI e Marketing + **8 verifiche/unità al mese** | **€15 / €14 / €13** per scaglione (~20% in meno rispetto ai pacchetti separati) |

**Perché il portale proprietari è nella base:** per un'agenzia è indispensabile, e sul mercato è spesso incluso (Avantio; il portale di OwnerRez è gratuito). Il costo marginale per CasaZen è di pochi centesimi per unità (fatture SDI). Il ricavo perso (€2) è recuperato alzando il pacchetto AI e Marketing da €2 a €4.

**Margini** (costo di piattaforma a 250 unità: ~€4,1 la base, ~€8,2 il tutto compreso):

| Voce | Margine |
|---|---|
| Base (portale proprietari incluso) | €4,8 / €3,8 / €2,8 per scaglione |
| Tutto compreso | €6,8 (45%) / €5,8 (41%) / €4,8 (37%) |
| Verifica d'identità | ~€0,27 a verifica |

**Posizionamento:**
- il tutto compreso a €13–15 costa **meno** del pacchetto Kross + Vikey (~€16) e delle suite internazionali, con più servizi;
- la verifica d'identità a €0,75 costa meno della metà di Uplisting (£1,25) e di Hospitable ($2).

### 6.3 Programma Founding Partner (agenzie della prima ora)

- **Prezzo = costo documentato + €1/unità** sul tutto compreso. Il **margine è bloccato**: il prezzo si rivede una volta l'anno **solo** se cambiano le voci di costo indicate nel contratto (NextPax, verifica identità, SDI, AI, hosting, cambio USD/EUR).
- **Durata del prezzo:** tutto il rapporto, per le funzionalità presenti alla firma. Le nuove opzioni con −30%.
- **Posti:** le prime 10 agenzie, entro una data da definire.
- **In cambio:** durata minima 24 mesi, pagamento annuale, test e feedback, referenza o caso di studio.
- **Riduzione dei costi fissi:** con la crescita della piattaforma non abbassa il prezzo partner (da scrivere nel contratto ⚖).
- **Esempio Quokka** (250 unità; ipotesi di 2.000 verifiche al mese = 4 prenotazioni × 2 adulti per unità; da confermare con i dati reali):

| Voce di costo al mese | Prima ipotesi (verifiche incluse) | **Formula decisa dal PO**: verifiche a consumo, costi fissi coperti dal partner |
|---|---|---|
| Channel manager NextPax | €980 | €980 |
| Verifiche d'identità | €960 (incluse, $0,48 con NFC) | €660–960 **a consumo, al costo** ($0,33–0,48): si paga solo quello che si usa |
| Fatturazione elettronica, AI, email | ~€100 | ~€100 |
| Costi fissi di piattaforma (hosting, strumenti) | €400 | **€400** (deciso dal PO: il partner copre almeno i costi fissi) |
| Margine CasaZen (+€1/unità) | €250 | €250 |
| **Totale Quokka** | **~€2.700/mese** | **~€2.390–2.690/mese** |

- Con la formula decisa Quokka spende **€390–690 in più** rispetto a Kross (~€2.000, cioè +20–35%) e ottiene in più biometria, compliance CH, portale proprietari, AI e CRM.
- **Composizione fissa e variabile:** canone partner fisso di **~€1.730/mese** (NextPax €980 + servizi €100 + costi fissi €400 + margine €250 = **€6,92/unità**), più le verifiche a consumo al costo. Le verifiche sono l'unica parte variabile e dipendono dalle prenotazioni reali di Quokka.
- I €400 di costi fissi restano nel prezzo partner anche quando la piattaforma cresce (clausola già prevista).
- Il listino pubblico (tutto compreso a €13) costerebbe €3.250.
- Se Quokka sostituisce anche la sua attuale piattaforma di marketing e CRM, il risparmio complessivo aumenta.

**B2C:** il listino deciso l'8/10 (Starter €29, Pro €79, Scale €199) non copre il channel manager (€3,15–3,50 a unità). Per il B2C va venduto come opzione per unità (es. €4).

---

## 7. Stima dello sviluppo (processo AI: 1 Claude Code Max 20x + PO)

**Calibrazione dallo storico git:**
- il modulo Locazioni (~39k righe tra codice, test e frontend, con l'integrazione RLI verso l'AdE) è stato costruito in ~2,5 settimane attive: **~15k righe/settimana**;
- +25% per test sull'interfaccia e piccoli refactor umani;
- Anthropic non pubblica limiti numerici del piano Max ✓ (<https://support.claude.com/en/articles/11145838-using-claude-code-with-your-pro-or-max-plan>).

**Cosa si stima:** solo lo sviluppo in CasaZen, componente per componente, confrontato con parti già misurate del codice.

| Pacchetto | Settimane attive |
|---|---|
| WP1 Fondamenta: `Agency`, ruoli `PropertyOwner`/`Staff`, paese/valuta/fuso, tedesco, piani | 1,2–2,0 |
| WP2 OTA: client, mappatura, disponibilità/tariffe, prenotazioni, editor contenuti, inbox, AI, recensioni | 1,75–2,8 |
| WP3 CRM + campagne + WhatsApp | 1,2–1,75 |
| WP4 Proprietari: portale, rendiconti, SDI per conto, file di bonifico, migrazione degli account Stripe a v2 | 1,3–2,0 |
| WP5 Fornitori propri e staff | 0,4–0,7 |
| WP6 Compliance: Alloggiati, ISTAT, feratel, file AdE, partner, console back-office, pagoPA, XML Ticino | 1,5–2,4 |
| WP7 Check-in biometrico + QR all'ingresso + videochiamata + serrature | 1,4–2,2 |
| WP8 Routine: motore eventi/condizioni/azioni con attese (Hangfire), costruttore web, azioni (messaggi, attività, notifiche, serrature, webhook), creazione da linguaggio naturale, libreria, registro | 1,4–2,25 |
| **Totale** | **~10–16 settimane attive (~2,5–4 mesi)** |

**Ordine** (un solo abbonamento, quindi in sequenza): WP1 → WP6 + WP7 → WP5 → WP4 → WP2 → WP8 → WP3.

**Limiti della stima:** misura grossolana basata sulle righe di codice (±40%); il ritmo misurato viene da poche settimane intense; le integrazioni con fornitori mai usati sono le più a rischio.

**Percorso critico esterno** (da avviare subito, in parallelo):

| Dipendenza | Blocca |
|---|---|
| NextPax / Octorate: risposte scritte + sandbox | WP2 |
| Didit / Sumsub: preventivi + PoC; DPIA; parere del Viminale per eliminare l'operatore | WP7 |
| Intermediario partner (contratto + integrazione); organizzazione del back-office | WP6 in produzione |
| Enti: documentazione Sicilia, web service Liguria/Calabria, contratto feratel; richieste di API ai portali | Parti di WP6 |
| Stripe: migrazione degli account delle agenzie ad Accounts v2 | WP4 |
| Pareri ⚖: DAC7 per il sito di prenotazione, contratto Founding Partner, testi di consenso | Go-live |
| Revisione madrelingua delle traduzioni in tedesco | WP1 per la CH tedesca |

---

## 8. Punti aperti e domande per Quokka

**Per Quokka:**
1. ~~Quante unità?~~ **250 proprietà** (dato del PO). Resta da sapere la ripartizione tra Italia e Svizzera (cantoni e comuni).
2. Cosa pagano oltre ai ~€2.000/mese di Kross? Il PO ricorda una **piattaforma di marketing e CRM**: servono nome e costo. Dal sito pubblico non si ricava: carica solo Google Tag Manager, iubenda e Kross; un plugin per i form WordPress (Tablesome) ha un connettore HubSpot che risulta disattivato. Chiedere anche numero di prenotazioni e ospiti al mese, che determinano il costo delle verifiche.
3. ~~Intestazione degli annunci?~~ **Intestati a Quokka ovunque** (dato del PO): niente ripartizione co-host, i proprietari si pagano con il file di bonifico.
4. Chi incassa oggi e con quale status fiscale dei proprietari (ritenuta, P.IVA)?
5. Serrature: tutte keybox Masterlock o ci sono già serrature smart? Quali marche?
6. Staff interno e fornitori esterni: quanti, in quali lingue?
7. Tempi di migrazione da Kross e durata residua del contratto.

**Per CasaZen:**
- confermare i prezzi con il business plan;
- scegliere tra Didit e Sumsub dopo il PoC (decide il margine sul check-in);
- prezzo di DeepSeek su Bedrock UE;
- verifiche [NV] ancora aperte: Liguria e Calabria, Pegaso, Cinque Terre, Laax/St. Moritz/Andermatt, testi delle norme 2027.

---

## Allegato A: domande per NextPax e Octorate (risposta scritta)

1. Si può creare da zero via API un annuncio nuovo su Airbnb, Booking.com, Vrbo ed Expedia? Elencare i passaggi che richiedono interfaccia o intervento dell'host.
2. Quali campi di contenuto si possono inviare per ciascuna OTA: testi IT/EN/DE/FR, foto, servizi, regole, camere e letti, campi licenza (**CIN**)?
3. Messaggi: lettura e invio, richieste prima della prenotazione, allegati, offerte speciali, webhook, latenza, limiti di frequenza.
4. Le risposte automatiche o generate dall'AI sono ammesse?
5. Recensioni: lettura e risposta.
6. Prezzi per un software vendor: per unità, minimi, setup e certificazione, eventuale sovrapprezzo per i messaggi.
7. Chi è titolare degli account e dei contratti OTA? In caso di uscita si migra senza perdere le recensioni? Il contenuto è esportabile via API?
8. Esiste una sandbox su ciascuna OTA?
9. Vendete direttamente ai gestori in Italia e Svizzera? Accettate una clausola di non sollecitazione?
10. DPA, dati ospitati in UE, elenco dei sub-fornitori, livelli di partnership con le OTA.
11. Collegate e-domizil/HomeToGo, feratel Deskline e Google Vacation Rentals?

## Allegato B: fonti principali

**OTA:**
- <https://connect.booking.com/>
- <https://channex.io/llms.txt>
- <https://nextpax.com/nextpax-pricing>
- <https://support.nextpax.com/distribute-new-or-reactivated-properties-to-channels>
- <https://api.octorate.com/connect/rest/v1/integration/openapi.yaml>

**Pagamenti:**
- <https://docs.stripe.com/connect/direct-charges-fee-payer-behavior>
- <https://docs.stripe.com/connect/charges>
- <https://stripe.com/en-it/pricing>
- <https://support.avantio.pro/en/articles/419441-sepa>
- <https://www.airbnb.com/help/article/3767>
- <https://www.airbnb.com/help/article/3527>
- <https://help.guesty.com/hc/en-gb/articles/20232278191261-Charging-payment-processing-fees-to-PMC-or-owner>
- <https://www.ownerrez.com/support/articles/direct-owner-payments>

**Prezzi dei concorrenti:**
- <https://www.krossbooking.com/prezzo>
- <https://www.uplisting.io/pricing>
- <https://www.ownerrez.com/support/articles/costs-and-fees>
- <https://www.avantio.com/pricing-usa-v2/>
- <https://www.smoobu.com/en/pricing/>
- <https://hospitable.com/pricing>
- <https://chekin.com/en/pricing/>
- <https://vikey.it/prezzi/>

**Compliance:**
- <https://alloggiatiweb.poliziadistato.it/service/service.asmx>
- <https://www.flussituristici.servizirl.it/Turismo5/app/ws/checkinV2?wsdl>
- <https://www4.ti.ch/fileadmin/DI/POLIZIA/documentazione/autorizzazioni/autorizzazioni_e_permessi/Manuale_Utente.pdf>
- <https://m3.ti.ch/CAN/RLeggi/public/index.php/raccolta-leggi/pdfatto/atto/10301>
- <https://www.agenziaentrate.gov.it/portale/catalogo-dei-servizi-di-interoperabilita>

**Privacy e check-in:**
- <https://mdp.giustizia-amministrativa.it/visualizza/?nodeRef=&schema=cds&nrg=202505732&nomeFile=202509101_11.html&subDir=Provvedimenti>
- <https://www.garanteprivacy.it/home/docweb/-/docweb-display/docweb/10221611>
- <https://www.garanteprivacy.it/home/docweb/-/docweb-display/docweb/10244289>
- <https://www.edpb.europa.eu/sites/default/files/files/file1/edpb_guidelines_201903_video_devices_en_0.pdf>

**Fornitori:**
- <https://didit.me/pricing>
- <https://sumsub.com/pricing/>
- <https://docs.sumsub.com/changelog/january-2026>
- <https://www.seam.co/pricing>
- <https://www.krossbooking.com/kdoor>
- <https://invoicetronic.com/en/pricing/>
- <https://resend.com/pricing>
- <https://api-docs.deepseek.com/quick_start/pricing>
- <https://www.daily.co/pricing/video-sdk/>

**Strumenti di sviluppo:**
- <https://support.claude.com/en/articles/11145838-using-claude-code-with-your-pro-or-max-plan>
- <https://claude.com/pricing>
