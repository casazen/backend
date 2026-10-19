# CasaZen: tipo host Agenzia (B2B), analisi finale

**Versione:** finale (v6), 2026-10-19. Sostituisce le versioni dal 14 al 15/10 (storico in git). Novità v5-v6: check-in con riconoscimento del volto sviluppato in casa (§3.6); nessuna integrazione con le serrature; CU e 770 fuori scope, solo rendiconto mensile delle ritenute (§3.7); costi di terze parti ridotti al channel manager (§5).
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
| Adempimenti | **Regola R12:** dove esiste un'API lo fa CasaZen in automatico (Alloggiati, ISTAT Lombardia/Marche/Sicilia, feratel, pagoPA). Dove non è tecnicamente possibile (Agenzia delle Entrate, F24, portali comunali, polizia del Ticino) **CasaZen prepara file e istruzioni, il caricamento lo fa l'agenzia** e la ricevuta si archivia in CasaZen |
| Ritenuta 21% | Il portale proprietari produce il **rendiconto mensile** delle ritenute del 21% per il versamento F24 (codice 1919). **File CU e 770: fuori scope per ora** (§3.7) |
| Check-in | Riconoscimento del volto e del documento **sviluppato in casa**, senza fornitori terzi. Flusso: dati prima dell'arrivo, **QR all'ingresso con confronto volto-documento**, alternativa in videochiamata, codice di accesso inviato per messaggio dopo l'esito, cancellazione alla ricevuta di Alloggiati, registro di verifica (§3.6) |
| Accesso | **Nessuna integrazione con le serrature**: Quokka usa serrature smart già installate e il **codice viene inviato per messaggio subito dopo il riconoscimento facciale** |
| Prezzo (proposta) | Base **€7–9/unità/mese** a scaglioni (portale proprietari e check-in con riconoscimento del volto inclusi) + AI e Marketing €4; programma **Founding Partner** a costo del channel manager + €1/unità, con margine bloccato. **Da rivedere con il business plan** (D10) |
| Sviluppo | **~11–17 settimane attive (~2,75–4,25 mesi)** con 1 abbonamento Claude Code Max 20x + PO. Il percorso critico sono i tempi esterni |

**Rischi principali:**
1. tempi esterni: fornitori OTA, verifica identità, enti;
2. obbligo di un'alternativa non biometrica e verifica all'ingresso (CdS 9101/2025) ⚖;
3. adempimenti senza API (Ticino ogni giorno, diversi comuni): restano un'attività manuale dell'agenzia, anche se CasaZen prepara tutto;
4. norme fiscali italiane rinumerate dal 1/1/2027.

---

## 1. Regole di prodotto (decise dal PO)

| # | Regola |
|---|---|
| R1 | Quokka è un **cliente agenzia**. CasaZen resta il fornitore del servizio; ogni funzionalità è di prodotto |
| R2 | Il B2B è un nuovo tipo di host `Agency` con una propria gestione: agenzia, CasaZen, ospite (diretto), proprietari. Pagamenti in Stripe; **dal 15/10 si applica il modello di mercato** (§3.3) |
| R3 | Non si replica Kross Booking: le 6 funzionalità sono condizione per collaborare |
| R4 | Nessun flusso manuale, tranne la firma dei contratti. **Eccezioni:** l'approvazione dei bonifici in banca (obbligatoria per l'autenticazione forte, SCA) e i casi della regola R12 |
| R5 | Chat AI e modifica dei contenuti obbligatorie nell'API del fornitore OTA |
| R6 / R9 | Check-in con riconoscimento del documento e del volto: **predefinito nel B2B, opzionale nel B2C**. Il consenso non può essere obbligatorio per legge (§3.6) |
| R7 | Delegare a terzi il più possibile, sapendo che **la responsabilità di legge non si delega** (§4) |
| R8 | **Prodotto unico B2C + B2B**, con limiti per piano. Portale proprietari, staff e fornitori propri solo B2B |
| R10 | Fornitori CasaZen sempre il default. I fornitori propri sono registrati in CasaZen: 0% e fatturazione fuori da CasaZen quando li usa la loro agenzia |
| R11 | **Tutti i costi Stripe a carico dei clienti** |
| R12 | **Fattibilità tecnica** (2026-10-15): se un flusso è **tecnicamente possibile** (esiste un'API), lo esegue CasaZen e il costo è suo; caso per caso si decide se venderlo come extra quando è un servizio a pagamento. Se **non è tecnicamente possibile**, CasaZen prepara tutto (file, dati, istruzioni, promemoria, archivio della ricevuta) ma **l'esecuzione resta all'host o all'agenzia**. Niente back-office umano di CasaZen |
| R13 | **Riconoscimento del volto in casa** (2026-10-19): il confronto volto-documento è obbligatorio nel B2B e si sviluppa in CasaZen, senza fornitori terzi di verifica d'identità. Consenso esplicito e alternativa equivalente (videochiamata) restano obbligatori; i dati biometrici si cancellano subito dopo il confronto, le copie dei documenti alla ricevuta di Alloggiati |
| R14 | **CU e 770 fuori scope per ora** (2026-10-19): solo il rendiconto mensile delle ritenute del 21% per il versamento mensile, nel portale proprietari (§3.7) |
| R15 | **Costi di terze parti** (2026-10-19): per ora l'unico costo di integrazione di CasaZen è il channel manager. Nessun fornitore di verifica d'identità, nessuna integrazione con le serrature, nessun servizio di invio CU/770 |

---

## 2. Registro delle decisioni

| ID | Decisione | Stato |
|---|---|---|
| D1 | CasaZen si fa pagare **solo a canone** (per unità, più opzioni): nessuna percentuale sulle prenotazioni | Deciso |
| D2 | Incasso delle prenotazioni dirette: **l'agenzia è l'esercente**, con direct charge sul suo account Stripe (Accounts v2, `fees_collector = stripe`). CasaZen non incassa | **Rivisto il 15/10** (prima: CasaZen incassa e Stripe ripartisce), su indicazione del PO di applicare il comportamento di mercato |
| D3 | Pagamento dei proprietari: rendiconto + file di bonifico SEPA/CH approvato dall'agenzia in banca; ripartizione Airbnb co-host dove possibile; EBICS (CH) e aggregatore o Treasury (IT) come automazione successiva | **Deciso secondo il mercato** |
| D4 | Fornitore OTA: 11 domande + PoC con NextPax e Octorate, scelta sulle risposte scritte | **In verifica dal PO** (19/10) |
| D5 | Biometria **sviluppata in casa**, nessun fornitore di verifica d'identità; conservazione conforme al Garante | **Rivisto il 19/10** (prima: API di terzi) |
| D6 | Adempimenti secondo la regola R12: API eseguite da CasaZen; senza API, CasaZen prepara e l'agenzia esegue. **Back-office e intermediario partner non sono più a carico di CasaZen** | **Rivisto il 15/10** |
| D7 | Fornitori propri: registrati in CasaZen; 0% e fatturazione esterna con la propria agenzia, regole del marketplace con gli altri; interruttore "visibile nel marketplace" | Deciso |
| D8 | LLM: DeepSeek (costo). **Raccomandazione aperta:** per i dati degli ospiti usarlo ospitato in UE (AWS Bedrock, Stoccolma) | Deciso con riserva |
| D9 | Serrature: nessuna integrazione; il codice viene inviato per messaggio subito dopo il riconoscimento facciale | **Rivisto il 19/10** (prima: Seam) |
| D10 | Prezzi: proposta in §6, **da rivedere con il business plan** | Aperto |
| D11 | CU e 770: fuori scope per ora; solo il rendiconto mensile delle ritenute per l'F24 | Deciso il 19/10 |

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
   - **Per Quokka non si applica:** gli annunci sono intestati a Quokka su tutte le OTA (dato del PO). Tutti i ricavi OTA arrivano a Quokka, che paga i proprietari con il file di bonifico ed è l'intermediario che incassa: ritenuta del 21% sui proprietari privati italiani: rendiconto mensile per l'F24 nel portale proprietari (§3.7); CU e 770 rimandati.
5. **Documenti fiscali generati da CasaZen per conto dell'emittente**, con invio SDI via API (Invoicetronic, A-Cube o Openapi):
   - fattura dell'agenzia al proprietario (mensile, differita);
   - fattura di CasaZen all'agenzia;
   - rendiconto mensile delle ritenute (§3.7).
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

**Senza API (regola R12): CasaZen prepara, l'agenzia esegue**

| Adempimento | Cosa prepara CasaZen | Cosa fa l'agenzia |
|---|---|---|
| Dichiarazione annuale imposta di soggiorno, dati locazioni brevi, DAC7 | File validati secondo le specifiche AdE (moduli di controllo), scadenze e promemoria | Invio tramite il proprio commercialista o intermediario; carica la ricevuta in CasaZen |
| **CU e 770 dell'agenzia** | Fuori scope per ora (§3.7); resta il rendiconto mensile delle ritenute per l'F24 | Invio con il proprio commercialista o intermediario |
| F24 (ritenute, imposte) | Importi e codici tributo pronti | Pagamento dalla propria banca o tramite intermediario. Il pagamento via API esiste solo per i conti Banca Sella (Fabrick): si valuta caso per caso |
| StayTour (Como, Trapani, Tropea), TouristTax (Genova, Portofino, S. Margherita), SoggiorniAmo (Milano), Pegaso, Cinque Terre | File o dati da caricare, link diretto al portale, promemoria con scadenza | Caricamento sul portale con le proprie credenziali; ricevuta in CasaZen |
| **Ticino**: notifica alla polizia | **File XML pronto ogni giorno** (formato ufficiale), promemoria entro le 24 ore | Caricamento quotidiano su notifiche.ti.ch (2FA) |
| Tassa OTR in Ticino | CSV dei pernottamenti | Pagamento secondo le regole dell'OTR |

**Eseguiti da CasaZen via API (costo di CasaZen):** pagamento pagoPA via Openapi (€2 a carico di chi paga), oltre ad Alloggiati, ISTAT e feratel indicati sopra.

**Vincoli:**
- Automatizzare lo SPID dell'agenzia (RPA) non è lecito: le credenziali sono di uso personale (AgID).
- CasaZen non può essere intermediario Entratel.
- Il **Ticino vieta di fotocopiare il documento** ✓ (RL 942.120 art. 6).

### 3.6 Check-in con riconoscimento del volto, accesso, routine

**Decisione del PO (2026-10-19):** il riconoscimento volto-documento si sviluppa in casa, senza fornitori terzi, ed è obbligatorio nel B2B. Quokka lo vuole per avere un processo strutturato e tracciato in caso di controlli sotto copertura delle autorità. Il flusso sotto è quello approvato dal PO, che lo ritiene conforme alla legge.

**Base normativa** (testi primari letti il 2026-10-19):
- **Art. 109 TULPS** ✓: si alloggiano solo persone con documento idoneo; i dati si comunicano alla Questura entro 24 ore (6 ore per soggiorni fino a 24 ore).
- **D.M. 7/1/2013 e 16/9/2021** ✓: regolano la comunicazione, non il metodo di identificazione. I dati digitali si cancellano alla ricevuta, che si conserva 5 anni.
- **Circolare del Viminale 18/11/2024 e Consiglio di Stato 9101/2025** ✓: il check-in solo da remoto, con codice di apertura senza verifica, non basta. Serve la verifica *de visu* oppure un videocollegamento all'ingresso che accerti "hic et nunc" la corrispondenza tra ospite e documento (esempio citato: spioncino digitale o QR code che faccia un fermo immagine).
- **Garante, 29/04/2026** ✓: la copia del documento si cancella alla ricevuta di Alloggiati.
- **GDPR art. 9:** per i dati biometrici servono consenso esplicito e un'alternativa equivalente.

**Flusso (deciso il 2026-10-19):**
1. **Prima dell'arrivo:** l'ospite compila i dati richiesti da Alloggiati. L'immagine del documento non è obbligatoria; se si raccoglie, è temporanea.
2. **All'ingresso:** QR sulla porta; foto o selfie sul posto e confronto con la foto del documento (**opzione A, predefinita**, con consenso esplicito). Alternativa equivalente senza biometria: **videochiamata con una persona** (**opzione B**).
3. **Codice di accesso:** inviato per messaggio all'ospite **subito dopo l'esito positivo**, mai prima. Esito negativo o dubbio → operatore.
4. **Invio ad Alloggiati entro 24 ore** (6 ore per soggiorni fino a 24 ore); dal 31° giorno nuova registrazione.
5. **Dopo la ricevuta:** si cancellano subito dati e copie dei documenti; la ricevuta si scarica **entro 30 giorni** e si conserva 5 anni. I dati biometrici (immagini, template) si cancellano **subito dopo il confronto**.
6. **Registro di verifica** per prenotazione: data e ora, metodo, esito, operatore, ricevuta. Senza immagini. È il pacchetto di prova in caso di controlli.
7. Minori senza biometria. DPIA prima del lancio.

**Componenti sviluppati in casa:**

| Componente | Note |
|---|---|
| Lettura del documento (MRZ/OCR) | PassportEye (MIT) con Tesseract o equivalenti; l'ospite conferma i dati |
| Confronto del volto | Serve un modello con licenza commerciale: OpenCV Zoo SFace (Apache-2.0, dati di addestramento non dichiarati [NV]); **InsightFace escluso** (modelli solo per ricerca non commerciale ✓) |
| Controllo che la persona sia viva | Di base (passivo o a sfida) |
| Documenti | Solo controlli formali (checksum MRZ, scadenza, formato) |
| Chip NFC | Non previsto ora; richiederebbe un'app nativa |
| Videochiamata (opzione B) | Daily (gratis fino a 10.000 minuti/mese): infrastruttura video |

**Accesso all'immobile:** Quokka usa **serrature smart già installate** (dato del PO, 2026-10-19; in precedenza risultavano keybox Masterlock). CasaZen non si integra con le serrature: invia il codice per messaggio dopo il riconoscimento. Il codice non è creato né revocato da CasaZen: resta quello impostato dall'agenzia sulla serratura (non è quindi unico per soggiorno né scade alla partenza).


**Routine (definizione del PO, 2026-10-15): costruttore di automazioni configurabile dall'host.** L'host crea le proprie routine nel formato **QUANDO** (evento) → **SE** (condizioni) → **ALLORA** (una o più azioni, anche con attese e passaggi successivi).

| Elemento | Catalogo proposto |
|---|---|
| **Eventi (QUANDO)** | Prenotazione nuova/confermata/modificata/cancellata; X giorni/ore prima o dopo check-in e check-out; check-in o check-out avvenuto; messaggio dell'ospite ricevuto; recensione ricevuta; pagamento riuscito/fallito; check-in online completato; **identificazione completata**; ricevuta Alloggiati arrivata; attività completata; ricorrenza settimanale |
| **Condizioni (SE)** | Proprietà o tag, canale, durata del soggiorno, numero di ospiti, anticipo della prenotazione, stato del pagamento, stato del check-in/identificazione, lingua dell'ospite, giorni liberi prima o dopo |
| **Azioni (ALLORA)** | Messaggio all'ospite (OTA, email, WhatsApp, SMS); notifica a proprietario, staff o team; crea un'attività (pulizia, manutenzione, check-in) e assegnala a staff, fornitore proprio o marketplace; **invia il codice di accesso** per messaggio (solo se l'identificazione è completata); richiesta di recensione; aggiungi un tag; attesa; webhook verso sistemi esterni |
| **AI** | Opzione AI e Marketing: **creazione della routine da una frase** ("al check-out manda la pulizia e dopo 2 giorni chiedi la recensione"), con anteprima da approvare; azioni AI (messaggio personalizzato, sintesi, traduzione, controllo foto) |
| **Libreria pronta** | Routine predefinite da attivare con un clic: pulizia al check-out, istruzioni 2 giorni prima, recensione dopo la partenza, promemoria documenti mancanti |

**Guardrail** (la routine non può aggirare le regole):
- il codice di accesso parte solo dopo l'identificazione;
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

### 3.7 Ritenuta del 21%: rendiconto mensile (CU e 770 fuori scope per ora)

**Decisione del PO (2026-10-19):** per ora CasaZen produce solo il **rendiconto mensile per il versamento mensile** delle ritenute nel portale proprietari. I file CU e 770 sono esclusi.

**Perché serve:** l'agenzia che incassa gli affitti brevi è sostituto d'imposta (art. 4 D.L. 50/2017; dal 1/1/2027 art. 208 del nuovo TUIR, D.Lgs. 117/2026). A ogni pagamento al proprietario trattiene il **21% a titolo d'acconto** sul lordo e lo versa con **F24 codice 1919** entro il **16 del mese successivo**, con un versamento cumulativo per tutti i proprietari ✓ (<https://www.agenziaentrate.gov.it/portale/documents/d/guest/locazioni_brevi_disciplina_fiscale_e_regole_per_intermediari_aprile-2026>).

**Cosa produce il portale proprietari ogni mese:**
- ritenute per proprietario, appartamento e prenotazione, con il totale da versare con l'F24;
- base di calcolo: lordo con pulizie e biancheria a forfait e con la provvigione dell'agenzia se trattenuta dai soldi del proprietario; esclusi cauzioni, caparre e penali ✓;
- anno solare di cassa e anno di soggiorno per ogni importo (le prenotazioni a cavallo d'anno restano divise), così una CU futura sarebbe possibile senza rielaborare;
- esportazione PDF e CSV, e riconciliazione trattenuto/versato con la data di pagamento dell'F24, che l'agenzia registra. Il pagamento dell'F24 resta all'agenzia (R12).

**Fuori scope, ma da sapere** (emerso dalla ricerca del 19/10):
- **Resta a carico dell'agenzia** il lavoro di fine anno: CU entro il 16 marzo, 770 entro il 31 ottobre. Il rendiconto mensile **non sostituisce** il commercialista per quegli invii.
- L'invio all'AdE è solo con **Entratel** (oltre 20 percipienti); l'AdE non ha un'API di invio e nessun gateway è stato trovato (controllati Openapi, Invio Facile, Genya, Namirial/SpinOk, ACCA).
- Se si riprenderà: la CU richiede il **CIN a 18 caratteri esatti** (`CinFormat.cs` ne accetta 11–18) e il **codice catastale** del comune, non l'ISTAT.
- Sanzioni per la CU omessa o errata: €100 per certificazione (max €50.000).

**Da chiedere a Quokka:** cosa copre esattamente il commercialista dei €60 ad appartamento (CU/770 dell'agenzia o anche il 730 dei proprietari). Dato riferito dal PO, non verificato.

---

## 4. Stripe e fiscalità: cosa si delega e cosa no

| Adempimento | Stripe | Soluzione |
|---|---|---|
| Commissioni di pagamento | Addebitate all'agenzia (`fees_collector = stripe`) ✓ | — |
| IVA sul soggiorno | Nessun codice per alloggio in Stripe Tax ✓ | Aliquote da `TaxRate` |
| Dichiarazione e versamento IVA IT/CH | No ✓ | Taxually (opzionale, per l'agenzia) |
| Fattura elettronica | No ✓ | Provider SDI via API |
| Tassa di soggiorno, ritenuta, F24, DAC7 | No | File preparati da CasaZen; invio a cura dell'agenzia o del suo commercialista (R12) |

**Responsabilità che per legge non si possono delegare:** art. 109 TULPS (gestore), responsabile dell'imposta di soggiorno (chi incassa), sostituto d'imposta (intermediario che incassa), IVA (soggetto passivo), Kurtaxe e notifica degli ospiti in CH (locatore).
→ **Promessa commerciale corretta: "compliance automatizzata", non "compliance delegata".**

**Norme che cambiano dal 1/1/2027:**
- **Testo unico IVA**, D.Lgs. 10/2026 ✓ (<https://www.normattiva.it/eli/id/2026/01/30/26G00022/ORIGINAL>);
- l'art. 4 DL 50/2017 confluisce probabilmente nel D.Lgs. 117/2026, artt. 207–208 [NV].
- Riverificare tutti i riferimenti prima di sviluppare.
- Dal 2026, oltre 2 appartamenti in affitto breve si presume attività d'impresa (L. 199/2025 c. 17): lo status fiscale del proprietario si raccoglie all'onboarding.

---

## 5. Costi di CasaZen e prezzi

Prezzi ufficiali, IVA esclusa, al 2026-10-19. Per prudenza USD = EUR 1:1.

**Decisione del PO (2026-10-19): per ora l'unico costo di terze parti è il channel manager.** Non sono conteggiati hosting, email, fatturazione SDI, AI, video e abbonamento di sviluppo (costi di piattaforma non attribuiti a Quokka). Non sono previsti fornitori di verifica d'identità, integrazioni con le serrature né servizi di invio CU/770.

### 5.1 Cosa paga CasaZen

| Voce | Costo | Nota |
|---|---|---|
| Channel manager (NextPax, **scelta in verifica dal PO**) | Minimo €350/mese con le prime 50 unità incluse; poi €3,15/unità (annuale) o €3,50 (mensile) ✓ | Da negoziare: minimo con decorrenza dal go-live, non dalla firma |

Verifica del 19/10 su una trentina di fornitori (fonti ufficiali): nessuno rispetta la regola R5 (contenuti e chat via API su Airbnb e Booking.com) a un costo inferiore. Channex (~$380/mese per 250 unità) non ha contenuti via API; Octorate non ha prezzi pubblici per software vendor; Rentals United è da quotare. Il confronto completo non è ancora nel documento.

### 5.2 Cosa pagano i clienti direttamente a terzi

Non sono né costi né ricavi per CasaZen:
- **Stripe:** commissioni sugli incassi, addebitate direttamente all'agenzia (`fees_collector = stripe`);
- **WhatsApp (Meta):** fatturato all'agenzia;
- **pagoPA:** €2 a pagamento, a carico di chi paga;
- **commercialista o intermediario fiscale dell'agenzia:** CU, 770, F24 e dichiarazioni, fuori da CasaZen;
- **Taxually** (dichiarazioni IVA, opzionale);
- **serrature:** hardware e abbonamenti della marca, non gestiti da CasaZen.

### 5.3 Scenari mensili

| | **A. 0 clienti** | **B. Quokka, 250 unità, prezzo partner** | **C. 250 unità a listino** (Base €7) |
|---|---|---|---|
| **Paga CasaZen:** channel manager | €0 (se il minimo parte dal go-live) o €350 | €980 (€350 + 200 × €3,15) | €980 |
| **Pagano i clienti a CasaZen** | — | €1.230 (costo + €1/unità) | €1.750 (250 × €7) |
| **Risultato mensile** | €0 / −€350 | **+€250** | **+€770** |

**Come leggere la tabella:**
- **Scenario A:** se NextPax fattura il minimo dalla firma, CasaZen perde €350 al mese finché non arriva il primo cliente. Conviene firmare solo quando il cliente è vicino.
- **Scenario B (Quokka partner):** €1.230 al mese, cioè €4,92 per unità. Copre il channel manager e lascia €1 per unità.
- **Scenario C:** la differenza con B è il prezzo che si rinuncia a chiedere ai partner.
- **Soglia di convenienza:** il minimo di €350 supera il ricavo del Base (€9) sotto le **39 unità**: a 30 unità si incassano €270 e si pagano €350. Va rivisto lo scaglione o il minimo (D10).
- **AI e Marketing** (+€4/unità, +€1.000 al mese per 250 unità): costo non conteggiato [NV].
- **Non sono inclusi:** il tempo del PO, i pareri legali e il costo di sviluppo (§7).

---

### 5.4 Ambienti di test fino al primo cliente (verificato il 2026-10-15)

**Obiettivo:** sviluppare e collaudare tutto in sandbox, a costo zero o quasi, e attivare i piani a pagamento solo con il primo cliente.

| Partner | Ambiente di test | Gratis? | Quando si inizia a pagare | Cosa non si può provare in test |
|---|---|---|---|---|
| **Stripe** | Test mode + fino a 5 sandbox; Connect, Accounts v2, Treasury (simulazioni), TWINT | Sì | Solo commissioni in produzione | Payout reali |
| **Invoicetronic** | Sandbox che simula tutto il flusso SDI | Sì, senza scadenza e senza carta | Pacchetto prepagato da €100 al go-live | Invio reale allo SDI |
| **A-Cube / Openapi** | Sandbox SDI (Openapi anche bollettini) | A-Cube sì; Openapi richiede "credito sandbox" [NV se reale] | Credito o abbonamento in produzione | Invii e pagamenti reali |
| **NextPax** | "Full sandbox" + Developer Portal; certificazione prima della produzione | Costo della sandbox non pubblicato [NV] | **Minimo €350/mese: da negoziare con decorrenza dal go-live**, non dalla firma | Annunci e prenotazioni reali sulle OTA |
| **Octorate** | Proprietà di prova, dati simulati; breve certificazione | Non pubblicato; *"API access is conditional on payment of Octorate services"* | Da chiedere | Connessioni reali ai canali |
| **Resend** | Piano Free (3.000 email/mese, 100/giorno, 3 domini) | Sì | Pro $20 quando si superano i limiti | — |
| **Daily** | Piano gratuito (10.000 minuti/mese) | Sì | Oltre i 10.000 minuti | — |
| **WhatsApp (Meta)** | Account e numero di test creati in automatico | Sì | Per messaggio template in produzione | Messaggi a ospiti reali |
| **DeepSeek / Bedrock** | Nessuna sandbox | A consumo (pochi euro in sviluppo) | Per token. Su Bedrock in UE DeepSeek V3.2 è disponibile a Stoccolma: $0,74/M in ingresso, $2,22/M in uscita | — |
| **Alloggiati Web** ✓ | **Nessun ambiente di test.** L'operazione `Test` valida i dati senza inviarli | Gratuito | — | L'invio reale e la ricevuta richiedono **credenziali di una struttura vera** |
| **Ross1000** | Nessun ambiente di collaudo documentato | Gratuito | — | Serve una struttura vera; l'indirizzo del servizio va chiesto alla Regione (supporto GIES) |
| **feratel** | Ambiente di test non documentato [NV]; interfacce *"free of charge to all system providers"* | Sì | — | Da chiedere |
| **Supabase / Railway** | Piani gratuiti (Supabase si mette in pausa dopo 7 giorni di inattività) | Railway Free non basta per l'API sempre attiva | Railway Hobby $5/mese subito; Supabase Pro $25 al go-live | Backup (solo Pro) |

**Costo prima del primo cliente:** **€0**, oppure €350 al mese se NextPax fattura il minimo dalla firma. Non sono conteggiati l'hosting minimo (~€5) e l'abbonamento di sviluppo.


**Azioni consigliate:**
1. **NextPax:** chiedere per iscritto che il minimo di €350 decorra dal go-live del primo cliente, non dalla firma.
2. **Alloggiati e Ross1000:** per il collaudo serve una **struttura reale**: quella del PO oppure un host pilota che fornisce le credenziali. Fino ad allora si usa l'operazione `Test`, che valida senza inviare.
3. **Octorate:** chiedere se il PoC richiede un abbonamento.

Fonti:
- <https://docs.stripe.com/sandboxes>
- <https://invoicetronic.com/en/docs/sandbox/>
- <https://www.acubeapi.com/en/services/free-sandbox-environment>
- <https://nextpax.com/products/supply-api>
- <https://api.octorate.com/connect/start.html>
- <https://developers.facebook.com/documentation/business-messaging/whatsapp/about-the-platform>
- <https://aws.amazon.com/bedrock/pricing/>
- <https://questure.poliziadistato.it/statics/13/manualewebsercices_alloggiatiweb.pdf>
- <https://railway.com/pricing>
- <https://supabase.com/pricing>

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
| OwnerRez ✓ | $2–8 a unità (scala decrescente) + modulo proprietari $0,50–10 + AI $9 |

**Opzioni sul mercato:**
- portale proprietari: gratis, incluso in un piano superiore oppure ~£8;
- verifica d'identità: £1,25–$8 a controllo (costo all'ingrosso $0,35–1,25);

**Programmi "founding" con prezzo bloccato: quasi assenti.** Solo casi di prezzi storici mantenuti (Hospitable Legacy, OwnerRez); Host Tools li ha poi revocati. Un programma scritto e chiaro sarebbe distintivo.

**Quokka oggi paga ~€2.000/mese a Kross** (dato del PO).

### 6.2 Proposta di listino

Prezzi per unità al mese, IVA esclusa, **con pagamento annuale** (+10% con pagamento mensile). Minimo €90/mese.

**Cosa contiene ogni pacchetto** (aggiornato il 2026-10-15: il portale proprietari è nella base, il Marketing costa di più):

| Pacchetto | Cosa offre | Prezzo |
|---|---|---|
| **Base** | **Gestionale**: proprietà, calendario, prenotazioni, ospiti, KPI.<br>**Channel manager** (Airbnb, Booking.com, Vrbo, Expedia): sincronizzazione di disponibilità, prezzi e prenotazioni; creazione e modifica degli annunci da CasaZen; recensioni.<br>**Inbox unificata** con **risposte AI suggerite**, inviate dall'operatore.<br>**Sito di prenotazione diretta** con pagamento Stripe sull'account dell'agenzia.<br>**Check-in online** con riconoscimento del volto all'ingresso e alternativa in videochiamata; codice di accesso inviato per messaggio dopo l'esito.<br>**Adempimenti via API**: Alloggiati Web, ISTAT (regioni con API), feratel; calcolo della tassa di soggiorno; **file pronti** per dichiarazioni, CU e portali senza API.<br>**Portale proprietari**: dati e documenti, rendiconto mensile automatico, fatture elettroniche SDI per conto dell'agenzia, file di bonifico SEPA/CH, registro delle ritenute.<br>**Routine**: costruttore QUANDO → SE → ALLORA con libreria di routine pronte.<br>**Fornitori**: marketplace, fornitori propri a 0%, staff, app | **€9** (10–49 unità) · **€8** (50–149) · **€7** (150–399) · preventivo (400+) |
| **+ AI e Marketing** | Risposte AI **inviate in automatico** (sopra una soglia di confidenza), **routine create da una frase** e azioni AI nelle routine, **CRM**: profili ospite, consensi, segmenti, campagne email e WhatsApp con testi AI | **+€4** |

**Perché il portale proprietari è nella base:** per un'agenzia è indispensabile, e sul mercato è spesso incluso (Avantio; il portale di OwnerRez è gratuito). Il costo marginale per CasaZen è di pochi centesimi per unità (fatture SDI). Il ricavo perso (€2) è recuperato alzando il pacchetto AI e Marketing da €2 a €4.

**Margine del Base sul costo del channel manager** (solo NextPax, nessun altro costo):

| Unità | Costo NextPax | Ricavo Base | Margine |
|---|---|---|---|
| 30 | €350 (minimo) | €270 (€9) | **−€80** |
| 50 | €350 | €400 (€8) | +€50 |
| 100 | €508 | €800 (€8) | +€292 |
| 150 | €665 | €1.050 (€7) | +€385 |
| 250 | €980 | €1.750 (€7) | +€770 |

**Posizionamento:**
- Base + AI e Marketing costa €11–13 per unità, **meno** del pacchetto Kross + Vikey (~€16) e delle suite internazionali, con più servizi (check-in con riconoscimento del volto, portale proprietari, CRM);
- il check-in con riconoscimento del volto è incluso nel Base: i concorrenti lo fanno pagare a verifica (Uplisting £1,25, Hospitable $2).


### 6.3 Programma Founding Partner (agenzie della prima ora)

- **Prezzo = costo del channel manager + €1/unità.** Il **margine è bloccato**: il prezzo si rivede una volta l'anno **solo** se cambiano il costo del channel manager o il cambio USD/EUR (voci indicate nel contratto).
- **Durata del prezzo:** tutto il rapporto, per le funzionalità presenti alla firma. Le nuove opzioni con −30%.
- **Posti:** le prime 10 agenzie, entro una data da definire.
- **In cambio:** durata minima 24 mesi, pagamento annuale, test e feedback, referenza o caso di studio.
- **Costi fissi di piattaforma:** non attribuiti ai partner in questa versione (decisione del PO, 19/10). La copertura di €400 al mese decisa il 15/10 è da riconfermare (D10): con quella, Quokka pagherebbe €1.630.
- **Esempio Quokka** (250 unità):

| Voce di costo al mese | Importo |
|---|---|
| Channel manager NextPax (€350 + 200 × €3,15) | €980 |
| Margine CasaZen (+€1/unità) | €250 |
| **Totale Quokka** | **~€1.230/mese (€4,92/unità)** |

- Quokka spenderebbe **~€770 in meno** rispetto a Kross (~€2.000, −38%) e otterrebbe in più check-in con riconoscimento del volto, compliance CH, portale proprietari, AI e CRM.
- Canone fisso: nessuna parte variabile legata a verifiche di identità o serrature.

- Il listino pubblico (Base €7 per 250 unità) costerebbe €1.750.
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
| WP4 Proprietari: portale, rendiconti mensili con ritenute 21% per l'F24, SDI per conto, file di bonifico, migrazione degli account Stripe a v2 | 1,3–2,0 |
| WP5 Fornitori propri e staff | 0,4–0,7 |
| WP6 Compliance: Alloggiati, ISTAT, feratel, pagoPA via API; file AdE, F24, XML Ticino e CSV comunali con promemoria e archivio ricevute | 1,3–2,1 |
| WP7 Check-in con riconoscimento del volto **in casa** (cattura, confronto, controllo di base che la persona sia viva, consenso e alternativa, registro di verifica) + QR all'ingresso + videochiamata + invio del codice per messaggio | 2,2–3,6 |
| WP8 Routine: motore eventi/condizioni/azioni con attese (Hangfire), costruttore web, azioni (messaggi, attività, notifiche, webhook), creazione da linguaggio naturale, libreria, registro | 1,4–2,25 |
| **Totale** | **~11–17 settimane attive (~2,75–4,25 mesi)** |

**Ordine** (un solo abbonamento, quindi in sequenza): WP1 → WP6 + WP7 → WP5 → WP4 → WP2 → WP8 → WP3.

**Limiti della stima:** misura grossolana basata sulle righe di codice (±40%); il ritmo misurato viene da poche settimane intense; le integrazioni con fornitori mai usati sono le più a rischio.

**Percorso critico esterno** (da avviare subito, in parallelo):

| Dipendenza | Blocca |
|---|---|
| NextPax / Octorate: risposte scritte + sandbox | WP2 |
| DPIA; licenza e dati di addestramento del modello di riconoscimento del volto | WP7 |
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
5. Serrature: **già smart** (dato del PO, 19/10). Quale marca e come si imposta il codice (fisso per immobile o per soggiorno)?
6. Staff interno e fornitori esterni: quanti, in quali lingue?
7. Tempi di migrazione da Kross e durata residua del contratto.
8. Cosa copre il commercialista dei €60 ad appartamento (solo CU/770 dell'agenzia o anche il 730 dei proprietari)? CU e 770 restano a loro.
9. Quali controlli sotto copertura hanno già subito o temono, e cosa è stato contestato (per tarare il registro di verifica).

**Per CasaZen:**
- confermare i prezzi con il business plan (D10), compresa la soglia sotto le 39 unità;
- scegliere il channel manager (in verifica dal PO) e negoziare il minimo con decorrenza dal go-live;
- scegliere e validare il modello di riconoscimento del volto (licenza commerciale e dati di addestramento) e misurare il costo dell'infrastruttura;
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
- <https://invoicetronic.com/en/pricing/>
- <https://resend.com/pricing>
- <https://api-docs.deepseek.com/quick_start/pricing>
- <https://www.daily.co/pricing/video-sdk/>

**CU, 770 e Entratel:**
- <https://www.agenziaentrate.gov.it/portale/le-regole-per-gli-intermediari>
- <https://www.agenziaentrate.gov.it/portale/documents/d/guest/specifiche-tecniche-cu-2026_agg-24-02>
- <https://www.agenziaentrate.gov.it/portale/certificazione-unica-2026/software-di-compilazione>
- <https://assistenza.agenziaentrate.gov.it/portale/effettuare-invio-entratel>
- <https://telematici.agenziaentrate.gov.it/Main/SAbilitazione.jsp>
- <https://www.agenziaentrate.gov.it/portale/i-servizi-per-gli-intermediari-fiscali>
- <https://centropaghe.it/invii-telematici/>

**Identificazione degli ospiti:**
- <https://www.normattiva.it/uri-res/N2Ls?urn:nir:stato:regio.decreto:1931-06-18;773~art109>
- <https://alloggiatiweb.poliziadistato.it/PortaleAlloggiati/Normativa.aspx>
- <https://questure.poliziadistato.it/statics/16/dm-07.01.2013-aggiornato.pdf>
- <https://chekin.com/it/blog/riconoscimento-de-visu-affitti-brevi/>
- <https://vikey.it/self-check-in-de-visu-normativa-affitti-brevi-bnb/>
- <https://www.smoobu.com/it/blog/stop-self-check-in-e-key-box/>
- <https://developer.mozilla.org/en-US/docs/Web/API/Web_NFC_API>
- <https://csca-ita.interno.gov.it/>

**Strumenti di sviluppo:**
- <https://support.claude.com/en/articles/11145838-using-claude-code-with-your-pro-or-max-plan>
- <https://claude.com/pricing>
