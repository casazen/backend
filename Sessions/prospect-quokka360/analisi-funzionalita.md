# CasaZen: tipo host Agenzia (B2B), analisi finale

**Versione:** finale (v5), 2026-10-19. Sostituisce le versioni dal 14 al 15/10 (storico in git). Novità v5: check-in con riconoscimento del volto sviluppato in casa (§3.6); ritenuta 21%, CU e 770 come sostituto d'imposta (§3.7); costi e stime riallineati.
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
| Ritenuta 21%, CU e 770 | L'agenzia che incassa è **sostituto d'imposta**: CasaZen tiene il registro delle ritenute, genera il file CU (record L) e i dati del 770, validati con la procedura di controllo dell'AdE. **Non esiste un'API di invio**: l'agenzia invia con la propria registrazione **Entratel** o tramite un intermediario (§3.7). Risposte di Namirial (SpinOk) e Centro Paghe in attesa |
| Check-in | Riconoscimento del volto e del documento **sviluppato in casa** (nessun fornitore di verifica d'identità: Didit e Sumsub non più previsti). Flusso: dati prima dell'arrivo, **QR all'ingresso con confronto volto-documento sul posto**, alternativa in videochiamata, codice di accesso solo dopo l'esito, cancellazione alla ricevuta di Alloggiati, registro di verifica. Nessuna certificazione richiesta dai testi (§3.6) |
| Serrature | **Seam**, come fa il mercato. Costo ribaltato all'agenzia; codice rilasciato **solo dopo l'identificazione** |
| Prezzo (proposta) | Base **€7–9/unità/mese** a scaglioni (portale proprietari incluso) + AI e Marketing €4 + verifiche €0,75 (voce da rivedere senza fornitore, D10); **tutto compreso €13–15**; programma **Founding Partner** a costo + €1/unità, con margine bloccato |
| Sviluppo | **~12–19 settimane attive (~3–4,5 mesi)** con 1 abbonamento Claude Code Max 20x + PO. Il percorso critico sono i tempi esterni |

**Rischi principali:**
1. tempi esterni: fornitori OTA, verifica identità, enti;
2. obbligo di un'alternativa non biometrica e verifica all'ingresso (CdS 9101/2025): la sentenza non avalla né esclude un controllo automatico senza una persona ⚖;
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
| R14 | **CU e 770** (2026-10-19): CasaZen genera e valida i file; l'invio all'AdE lo fa l'agenzia con la propria registrazione Entratel (nessuna API di invio esiste, R12) |

---

## 2. Registro delle decisioni

| ID | Decisione | Stato |
|---|---|---|
| D1 | CasaZen si fa pagare **solo a canone** (per unità, più opzioni e verifiche): nessuna percentuale sulle prenotazioni | Deciso |
| D2 | Incasso delle prenotazioni dirette: **l'agenzia è l'esercente**, con direct charge sul suo account Stripe (Accounts v2, `fees_collector = stripe`). CasaZen non incassa | **Rivisto il 15/10** (prima: CasaZen incassa e Stripe ripartisce), su indicazione del PO di applicare il comportamento di mercato |
| D3 | Pagamento dei proprietari: rendiconto + file di bonifico SEPA/CH approvato dall'agenzia in banca; ripartizione Airbnb co-host dove possibile; EBICS (CH) e aggregatore o Treasury (IT) come automazione successiva | **Deciso secondo il mercato** |
| D4 | Fornitore OTA: 11 domande + PoC con NextPax e Octorate, scelta sulle risposte scritte | Deciso |
| D5 | Biometria **sviluppata in casa**, nessun fornitore di verifica d'identità; conservazione conforme al Garante; Didit e Sumsub restano solo come ripiego | **Rivisto il 19/10** (prima: API di terzi) |
| D6 | Adempimenti secondo la regola R12: API eseguite da CasaZen; senza API, CasaZen prepara e l'agenzia esegue. **Back-office e intermediario partner non sono più a carico di CasaZen** | **Rivisto il 15/10** |
| D7 | Fornitori propri: registrati in CasaZen; 0% e fatturazione esterna con la propria agenzia, regole del marketplace con gli altri; interruttore "visibile nel marketplace" | Deciso |
| D8 | LLM: DeepSeek (costo). **Raccomandazione aperta:** per i dati degli ospiti usarlo ospitato in UE (AWS Bedrock, Stoccolma) | Deciso con riserva |
| D9 | Serrature: Seam, costo ribaltato all'agenzia, codice solo dopo l'identificazione | Deciso (raccomandazione accettata) |
| D10 | Prezzi: proposta in §6, **da rivedere con il business plan** | Aperto |
| D11 | CU/770: CasaZen genera e valida i file; invio con Entratel dell'agenzia o intermediario; un gateway di invio via API non è stato trovato (verifiche su Openapi, Invio Facile, Genya, Namirial/SpinOk, ACCA). Richieste scritte a Namirial (SpinOk) e Centro Paghe | Deciso, con richieste in corso |
| D12 | CIN nell'export della CU: la specifica richiede **18 caratteri esatti**, mentre `CinFormat.cs` ne accetta 11–18. Proposta: controllo bloccante a 18 caratteri solo nell'export della CU | **Aperto** |

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

**Senza API (regola R12): CasaZen prepara, l'agenzia esegue**

| Adempimento | Cosa prepara CasaZen | Cosa fa l'agenzia |
|---|---|---|
| Dichiarazione annuale imposta di soggiorno, dati locazioni brevi, DAC7 | File validati secondo le specifiche AdE (moduli di controllo), scadenze e promemoria | Invio tramite il proprio commercialista o intermediario; carica la ricevuta in CasaZen |
| **CU e 770 dell'agenzia come sostituto d'imposta** (§3.7) | Registro ritenute, file CU (record L) e dati 770, validati con la procedura di controllo dell'AdE | Invio con la propria registrazione Entratel; carica la ricevuta in CasaZen |
| F24 (ritenute, imposte) | Importi e codici tributo pronti | Pagamento dalla propria banca o tramite intermediario. Il pagamento via API esiste solo per i conti Banca Sella (Fabrick): si valuta caso per caso |
| StayTour (Como, Trapani, Tropea), TouristTax (Genova, Portofino, S. Margherita), SoggiorniAmo (Milano), Pegaso, Cinque Terre | File o dati da caricare, link diretto al portale, promemoria con scadenza | Caricamento sul portale con le proprie credenziali; ricevuta in CasaZen |
| **Ticino**: notifica alla polizia | **File XML pronto ogni giorno** (formato ufficiale), promemoria entro le 24 ore | Caricamento quotidiano su notifiche.ti.ch (2FA) |
| Tassa OTR in Ticino | CSV dei pernottamenti | Pagamento secondo le regole dell'OTR |

**Eseguiti da CasaZen via API (costo di CasaZen):** pagamento pagoPA via Openapi (€2 a carico di chi paga), oltre ad Alloggiati, ISTAT e feratel indicati sopra.

**Vincoli:**
- Automatizzare lo SPID dell'agenzia (RPA) non è lecito: le credenziali sono di uso personale (AgID).
- CasaZen non può essere intermediario Entratel.
- Il **Ticino vieta di fotocopiare il documento** ✓ (RL 942.120 art. 6).

### 3.6 Check-in con riconoscimento del volto, serrature, routine

**Decisione del PO (2026-10-19): il riconoscimento volto-documento si sviluppa in casa, senza fornitori terzi di verifica d'identità** (Didit e Sumsub non sono più previsti). È obbligatorio nel B2B: Quokka lo vuole come punto principale per poter dimostrare un processo strutturato in caso di controlli sotto copertura delle autorità.

**Cosa dicono i testi primari** (letti il 2026-10-19):
- **Art. 109 TULPS** ✓ (<https://www.normattiva.it/uri-res/N2Ls?urn:nir:stato:regio.decreto:1931-06-18;773~art109>): si alloggiano solo persone con documento idoneo; i dati si comunicano alla Questura entro 24 ore (6 ore per soggiorni fino a 24 ore, D.L. 53/2019).
- **D.M. 7/1/2013, modificato dal D.M. 16/9/2021** ✓: regola solo la **comunicazione**. Non prescrive il metodo di identificazione né foto, copia del documento o firma. Il gestore elimina i dati digitali alla ricevuta e conserva la ricevuta 5 anni (art. 4-bis c. 2).
- **Nessuna certificazione** (AgID, eIDAS, ISO 30107, approvazione del Garante) è richiesta allo strumento dai testi letti ✓.
- **Obbligato** è il gestore (art. 109; art. 19-bis D.L. 113/2018 per i locatori). Se agenzie e piattaforme lo siano è [NV] ⚖. Violazione: art. 17 TULPS, contravvenzione (arresto fino a 3 mesi o ammenda; importo in euro [NV]).
- **Circolare del Viminale del 18/11/2024** (prot. 38138): il check-in "da remoto" che acquisisce solo i documenti e invia un codice di apertura o una key box **non soddisfa** l'art. 109; serve la verifica *de visu* della corrispondenza tra persona e documento. Testo originale non trovato: letti la trasposizione della Prefettura di Parma e le citazioni della sentenza.
- **Consiglio di Stato 9101/2025** ✓ (§6.2–6.3): conferma la verifica de visu. Come osservazione a margine ammette il **videocollegamento all'ingresso**, idoneo ad accertare "hic et nunc" la corrispondenza tra ospite e titolare del documento, "esibito o trasmesso con altro canale telematico all'atto dell'accesso" (esempio: **spioncino digitale o QR code che faccia un fermo immagine**). **Non parla di biometria, di controllo automatico né di chi debba guardare.**
- **Garante, 29/04/2026** ✓: base giuridica l'obbligo di legge (art. 6.1.c GDPR); la copia del documento si cancella alla ricevuta di Alloggiati; informativa agli ospiti; accordo ex art. 28 con i fornitori; niente WhatsApp. Sul riconoscimento del volto la nota non dice nulla.
- **Deroghe:** nessuna nell'art. 109 (salvo i rifugi alpini). Per famiglie e gruppi guidati basta comunicare i dati del documento del capofamiglia o capogruppo (semplificazione della *comunicazione*); una Questura dice che ogni ospite va identificato: per prudenza si identificano tutti gli adulti. Minore non accompagnato: non ospitabile; con adulto non genitore serve il consenso scritto dei genitori (nota ministeriale 2008).

**Flusso (deciso il 2026-10-19):**
1. **Prima dell'arrivo:** l'ospite compila i dati richiesti da Alloggiati. L'immagine del documento non è obbligatoria; se si raccoglie, è temporanea.
2. **All'ingresso:** QR sulla porta; foto o selfie sul posto e confronto con la foto del documento (**opzione A, predefinita**, con consenso esplicito). Alternativa equivalente senza biometria: **videochiamata con una persona** (**opzione B**).
3. **Il codice della serratura parte solo dopo l'identificazione** e solo con esito positivo. Esito negativo o dubbio → operatore.
4. **Invio ad Alloggiati entro 24 ore** (6 ore per soggiorni fino a 24 ore); dal 31° giorno nuova registrazione.
5. **Dopo la ricevuta:** si cancellano subito dati e copie dei documenti; la ricevuta si scarica **entro 30 giorni** e si conserva 5 anni. I dati biometrici (immagini, template) si cancellano **subito dopo il confronto**.
6. **Registro di verifica** per prenotazione: data e ora, metodo, esito, operatore, ricevuta. Senza immagini. È il pacchetto di prova in caso di controlli.
7. Minori senza biometria. DPIA prima del lancio.

**Componenti sviluppati in casa:**

| Componente | Fattibilità | Note |
|---|---|---|
| Lettura del documento (MRZ/OCR) | Sì | PassportEye (MIT) con Tesseract o equivalenti; l'ospite conferma i dati |
| Chip NFC | Opzionale, fase 2 | Richiede **app nativa**: Web NFC legge solo messaggi NDEF e non è supportato su iOS ✓. iOS: NFCPassportReader (MIT); Android: JMRTD (licenza [NV]); catena di certificati: CSCA italiana pubblicata dal Ministero (<https://csca-ita.interno.gov.it/>) |
| Confronto del volto | Sì, con modello a licenza commerciale | OpenCV Zoo SFace: Apache-2.0, **dati di addestramento non dichiarati [NV] ⚖**. **InsightFace escluso** (modelli solo per ricerca non commerciale ✓). Nessuna valutazione NIST indipendente per i modelli aperti |
| Liveness | Di base, **non certificata** | Nessuna soluzione aperta con licenza commerciale e certificazione trovata [NV]; come fa il mercato |
| Documenti falsi | Solo controlli formali (checksum MRZ, scadenza, formato) | Il rilevamento dei falsi è un problema di dati; i documenti senza chip sono più esposti |
| Videochiamata (alternativa B) | Sì | Daily (gratis fino a 10.000 minuti/mese): infrastruttura video, non verifica d'identità |

**Come fa il mercato** (pagine ufficiali):
- **Chekin ✓ e Vikey ✓:** QR sulla porta e selfie sul posto confrontato con il documento; non risultano né operatore né liveness. Chekin: *"strumento progettato per supportare l'identificazione de visu, non una garanzia automatica di conformità"* (27/5/2026). Vikey: *"mancano linee guida dettagliate sulle tecnologie ammesse"*.
- **Kross KDoor, Octorate, Scidoo:** raccolgono documento e foto, senza dichiarazioni sul de visu. **Smoobu:** videochiamata o videocitofono; key box più documenti inviati *"non è autorizzato"*.
- **Nessun fornitore dichiara una certificazione specifica per l'identificazione.** Le affermazioni che selfie o biometria bastano sono interpretazioni dei fornitori.

**Limiti da dichiarare e punti per il legale ⚖:**
1. Un confronto automatico **senza una persona** equivale al de visu? La sentenza non lo dice. Anche la videochiamata con una persona poggia su un'osservazione a margine, senza circolari a conferma.
2. L'identificazione può essere fatta da un delegato (portiere, addetto alle pulizie, personale dell'agenzia)? Nulla di esplicito.
3. Vanno identificati tutti gli adulti o basta il capofamiglia?
4. Agenzie e piattaforme sono "gestori" ai fini della sanzione? Importo e oblazione dell'art. 17 TULPS.
5. Licenza commerciale del modello di volto scelto e dei suoi dati di addestramento; art. 9 GDPR (consenso, alternativa, DPIA): in casa CasaZen risponde direttamente del trattamento biometrico; con un fornitore sarebbe responsabile esterno.
6. Novità 2026 (nuove circolari o leggi): non trovate [NV].

**Cosa si può promettere:** *"processo strutturato e tracciato"*, non "a prova di controllo". Nei controlli sotto copertura l'ispettore ha un documento vero: il confronto del volto non lo riconosce. Protegge il processo documentato (passaggi non saltabili, codice solo dopo l'esito, registro, comunicazione entro i termini). Senza liveness certificata resta il rischio di inganno con foto o schermo: lo si accetta come il mercato e lo si mitiga con un operatore sugli esiti dubbi.

**Ripiego se lo sviluppo in casa non regge:** Didit ($0,33–0,48 a verifica, $6.600–9.600 l'anno a 20.000 verifiche; prezzi in cambio dal 1/11/2026 [NV]) o Sumsub ($1,35, $27.000 l'anno).

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

### 3.7 Ritenuta 21%, CU e 770 (agenzia come sostituto d'imposta)

**Richiesta del PO (2026-10-19):** a fine anno Quokka deve ricondurre le imposte versate alle singole prenotazioni, per appartamento e per proprietario, e oggi paga un commercialista **€60 per appartamento** (dato riferito dal PO, non verificato; con 250 unità sono ~€15.000 l'anno). Il nome corretto non è "cedolare secca" (è la scelta del proprietario nella sua dichiarazione): sono gli obblighi dell'**intermediario che incassa** gli affitti brevi (art. 4 D.L. 50/2017; dal 1/1/2027 art. 208 del nuovo TUIR, D.Lgs. 117/2026, stesso contenuto).

| Quando | Obbligo | Scadenza |
|---|---|---|
| A ogni pagamento al proprietario | **Ritenuta 21% a titolo d'acconto** sul lordo | — |
| Ogni mese | **F24 codice 1919**: un versamento cumulativo per tutti i proprietari | 16 del mese successivo |
| Fine anno | **CU "locazioni brevi"** (record L): una per proprietario, una riga per appartamento (CIN) e per anno di soggiorno | 16 marzo (all'AdE e al proprietario); 2027: 16 marzo 2027 |
| Fine anno | **770**, flusso "Locazioni brevi" (quadri ST e SX): riconciliazione mensile ritenute operate e versate | 31 ottobre (2026: 2 novembre; 2027: 2 novembre [NV, calcolo]) |
| Solo contratti senza ritenuta | Comunicazione dei dati dei contratti | 30 giugno (la CU la assorbe per chi trattiene) |

Fonti: guida AdE aprile 2026 (<https://www.agenziaentrate.gov.it/portale/documents/d/guest/locazioni_brevi_disciplina_fiscale_e_regole_per_intermediari_aprile-2026>), Provvedimento 12/7/2017 coordinato, istruzioni CU 2026 (<https://www.agenziaentrate.gov.it/portale/documents/d/guest/cu_istr_2026_agg-1->), specifiche tecniche CU 2026 (<https://www.agenziaentrate.gov.it/portale/documents/d/guest/specifiche-tecniche-cu-2026_agg-24-02>), istruzioni 770/2026.

**Cosa entra nella base del 21%:** pulizie e biancheria addebitate a forfait ✓; la provvigione dell'agenzia se trattenuta dai soldi del proprietario ✓. Restano fuori: cauzioni, caparre e penali; pulizie addebitate a costo effettivo. Imposta di soggiorno e commissioni dei portali non sono trattate esplicitamente [NV].

**Cosa serve nei dati (che CasaZen ha già in gran parte):**
- per prenotazione: canale, date di soggiorno e notti per anno solare, importo lordo diviso tra affitto, pulizie forfettarie e costi, chi ha incassato;
- per pagamento al proprietario: data (decide mese dell'F24 e anno di cassa), prenotazioni coperte, ritenuta per prenotazione e per anno;
- per proprietario: codice fiscale, P.IVA (se c'è, niente ritenuta né CU), numero di appartamenti dichiarato (≤2 dal 2026), proprietario o sublocatore;
- per immobile: **CIN a 18 caratteri**, indirizzo e **codice catastale (Belfiore) del comune**, comproprietari e firmatario del contratto.

**Cosa sviluppa CasaZen:**
1. **Registro delle ritenute** per pagamento, prenotazione, appartamento, proprietario e anno (con la divisione delle prenotazioni a cavallo d'anno), riepilogo mensile per l'F24 1919 e riconciliazione trattenuto/versato;
2. **generatore del file CU** (record A + L, tracciato ministeriale), validato con la *Procedura di controllo* gratuita dell'AdE, più un PDF sintetico per ogni proprietario;
3. **dati del 770** (righe ST) e, per i contratti senza ritenuta, la comunicazione del 30 giugno;
4. archivio delle ricevute di invio.
Valore per l'agenzia: sostituisce il lavoro di riconciliazione che oggi costa €60 ad appartamento; nessun gestionale concorrente verificato produce il file ufficiale (Octorate e CiaoBooking calcolano solo il 21%; Octorate stampa l'F24).

**Invio all'AdE: nessuna API.** Le API di interoperabilità 2026 dell'AdE coprono solo lo scarico massivo delle CU, non l'invio. Con più di 20 percipienti serve **Entratel**. Opzioni:
- **A (consigliata): l'agenzia invia da sola** con la propria registrazione Entratel (nessun costo AdE). Una tantum: pre-registrazione, richiesta di abilitazione (di solito via PEC), incaricati designati, Desktop Telematico. A ogni invio: controllo del file, autenticazione (`.ccf`), invio, scarico della ricevuta e caricamento in CasaZen. File scartato e corretto entro 5 giorni: conta come presentato in tempo. **Da confermare con l'assistenza AdE** che il sostituto con Entratel possa inviare da solo anche la CU (la pagina ufficiale cita il 770).
- **B: intermediario** (commercialista, CAF o associazione di categoria) che trasmette con impegno alla presentazione telematica. CAF Interregionale e Assocaaf accettano file generati da software di terzi, per email e a preventivo, senza API.
- **Gateway via API:** non verificato per nessun fornitore. Controllati sulle pagine ufficiali: Openapi (nessun prodotto CU/770/Entratel), Invio Facile di Centro Paghe (software da installare, nessuna API pubblica, CU sì e 770 non citato), Wolters Kluwer Genya/TuttoTel, TeamSystem, Zucchetti, Datalog, Namirial Wow Suite/SpinOk (nessuna API per terzi), ACCA/Edificius (software BIM, nessuna funzione fiscale). **Richieste scritte in corso a Namirial (SpinOk) e Centro Paghe**; se rispondono con un canale per software house, l'invio diventa un miglioramento successivo.
- **Diventare intermediario noi:** non praticabile (serve una società controllata da commercialisti o associazioni di categoria).

**Sanzioni:** CU omessa, tardiva o errata €100 per certificazione (max €50.000; ridotta a un terzo se corretta entro 60 giorni); 770 omesso 120% delle ritenute non versate (min €250) o €250–2.000; ritenuta non operata 20%; ritenuta non versata 25%; comunicazione omessa €250–2.000.

**Problemi nel codice esistente (da decidere, D12):**
1. **CIN:** la specifica della CU richiede **esattamente 18 caratteri**; `Casazen.Core/Regulatory/CinFormat.cs` e `compliance.md` accettano una parte casuale di 1–8 caratteri (CIN da 11 a 18). Proposta: controllo bloccante a 18 caratteri solo nell'export della CU, senza toccare la regola generale.
2. **Codice comune:** la CU vuole il codice catastale (Belfiore), non l'ISTAT: le proprietà ne hanno bisogno di entrambi.

**Punti aperti:** cosa copre esattamente il commercialista dei €60 (se include il 730 dei proprietari, quella è una spesa dei proprietari e non un obbligo dell'agenzia); come trattare un proprietario con 3+ appartamenti senza P.IVA (nessun testo ufficiale: autodichiarazione del numero di appartamenti); rinumerazioni dal 1/1/2027 (D.Lgs. 117/2026 per il TUIR, D.Lgs. 141/2026 per CU e 770, sanzioni nel D.Lgs. 173/2024): nessun cambio sostanziale trovato, riferimenti da riverificare [NV].


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

## 5. Chi paga cosa: costi di CasaZen, prezzi ai clienti, costi pagati dai clienti a terzi

Prezzi ufficiali, IVA esclusa, al 2026-10-15. Per prudenza USD = EUR 1:1.

### 5.1 Cosa paga CasaZen

**Costi fissi** (si pagano anche con zero clienti, con tutte le integrazioni attive):

| Voce | €/mese | Nota |
|---|---|---|
| NextPax, minimo mensile | 350 | Include le prime 50 unità; si paga dalla firma del contratto |
| Hosting di produzione (Railway Pro + consumo, Supabase Pro) | ~85 | Oggi CasaZen è su piani gratuiti/minimi |
| Resend Scale (email, un dominio per agenzia) | 90 | Con pochi clienti basta Pro a $20 |
| Invoicetronic | 0 | Solo pacchetti prepagati (il più piccolo €100 per 1.000 transazioni, non scade); i €5/mese sono la postazione web opzionale |
| Abbonamento Claude Code Max 20x (sviluppo) | ~200 [NV] | La pagina ufficiale mostra solo "from $100"; fa fede l'addebito reale |
| **Totale fisso in produzione** | **~€725** | Prima del primo cliente si scende a ~€205 (vedi §5.4) |

**Costi variabili** (crescono con le unità e gli ospiti):

| Voce | Costo | Nota |
|---|---|---|
| NextPax oltre le 50 unità | €3,15/unità (annuale) | |
| Verifica identità (volto e documento) | Nessun fornitore: sviluppo in casa | Costo di calcolo e conservazione temporanea da misurare [NV] |
| Serrature Seam | $5 per serratura | Rivenduto a €6 |
| Fatture SDI, AI DeepSeek, video Daily | ~€0,4/unità | Daily gratis fino a 10.000 minuti |

### 5.2 Cosa pagano i clienti direttamente a terzi

Non sono né costi né ricavi per CasaZen:
- **Stripe:** commissioni sugli incassi, addebitate direttamente all'agenzia (`fees_collector = stripe`);
- **WhatsApp (Meta):** fatturato all'agenzia;
- **pagoPA:** €2 a pagamento, a carico di chi paga;
- **commercialista o intermediario fiscale dell'agenzia:** invii all'AdE e F24 (~€30 per la dichiarazione annuale come riferimento);
- **Taxually** (dichiarazioni IVA, opzionale);
- **serrature:** l'hardware (igloohome da $129) e gli eventuali abbonamenti della marca (es. Nuki Smart Hosting).

### 5.3 Scenari mensili

| | **A. Tutte le integrazioni attive, 0 clienti** | **B. Quokka, 250 unità, prezzo partner** | **C. 250 unità a listino** (tutto compreso €13) |
|---|---|---|---|
| **Paga CasaZen** | | | |
| Costi fissi | €730 | €730 | €730 |
| NextPax oltre il minimo (200 unità) | — | €630 | €630 |
| Verifiche d'identità (in casa) | — | — | — |
| SDI, AI, video | — | ~€100 | ~€100 |
| **Totale costi CasaZen** | **€730** | **€1.460** | **€1.460** |
| **Pagano i clienti a CasaZen** | | | |
| Canone | — | €1.730 (partner) | €3.250 (verifiche incluse) |
| Verifiche | — | incluse nel canone | incluse |
| **Totale ricavi CasaZen** | **€0** | **€1.730** | **€3.250** |
| **Risultato mensile CasaZen** | **−€730** | **+€270** | **+€1.790** |
| **Extra opzionali** (ricavo − costo) | — | Serrature: 250 × (€6 − $5) ≈ +€250 | Come B |

**Come leggere la tabella:**
- **Scenario A** (piattaforma pronta, nessun cliente): CasaZen perde ~€730 al mese, di cui €350 sono il minimo NextPax. Conviene firmare con NextPax solo quando il primo cliente è vicino.
- **Scenario B** (Quokka partner): il partner copre tutti i costi, compresi i fissi, e lascia ~€270 al mese di margine. È un pareggio voluto, non un guadagno.
- **Scenario C** (stessa dimensione a listino): ~€1.790 al mese. La differenza con B è il prezzo che si rinuncia a chiedere ai partner.
- **Adempimenti senza API:** secondo la regola R12 li esegue l'agenzia con i file preparati da CasaZen. Nessun back-office e nessun intermediario a carico di CasaZen.
- **Non sono inclusi:** il tempo del PO (assistenza e vendita), i pareri legali una tantum e il costo di sviluppo (§7).

**Rispetto ai calcoli precedenti:** i costi fissi passano da ~€400 a ~€730/mese, perché il minimo NextPax (€350) ora è contato tra i fissi e non per unità. Dal 19/10 le verifiche d'identità sono sviluppate in casa e non sono più un costo a consumo (€660–960 al mese nelle versioni precedenti). Il canone partner di Quokka (§6.3) resta ~€1.730 e copre tutti i costi. I costi dell'infrastruttura del riconoscimento del volto non sono stati misurati [NV].

---

### 5.4 Ambienti di test fino al primo cliente (verificato il 2026-10-15)

**Obiettivo:** sviluppare e collaudare tutto in sandbox, a costo zero o quasi, e attivare i piani a pagamento solo con il primo cliente.

| Partner | Ambiente di test | Gratis? | Quando si inizia a pagare | Cosa non si può provare in test |
|---|---|---|---|---|
| **Stripe** | Test mode + fino a 5 sandbox; Connect, Accounts v2, Treasury (simulazioni), TWINT | Sì | Solo commissioni in produzione | Payout reali |
| **Seam** | Workspace sandbox con dispositivi virtuali | Fatturazione della sandbox non indicata [NV]; prova gratuita di 14 giorni | Per dispositivo in produzione | Serrature reali |
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

**Costi prima del primo cliente:**

| Voce | €/mese |
|---|---|
| Railway Hobby | ~5 |
| Abbonamento Claude Code (sviluppo) | ~200 |
| NextPax, solo se fatturato dalla firma | (350) |
| **Totale** | **~€205/mese** (~€555 se NextPax fattura dalla firma) |

Tutto il resto è a consumo, in sandbox gratuita, oppure si attiva al go-live.

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
- <https://www.seam.co/docs/core-concepts/workspaces>
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
| **Check-in con riconoscimento del volto** | Riconoscimento del volto e del documento all'ingresso, con alternativa in videochiamata, registro di verifica | **€0,75 a verifica** (B2B: obbligatorio). **Voce da rivedere (D10):** il prezzo era costruito sul costo Didit; senza fornitore la voce va riposizionata (probabile inclusione nella Base) |
| **Serrature smart** | Integrazione della serratura già installata (via Seam): codice unico per ogni soggiorno, inviato solo dopo l'identificazione, revocato alla partenza o alla cancellazione, stato della batteria. **Non include l'acquisto della serratura** | **€6 per serratura al mese** (costo CasaZen $5) |
| **Tutto compreso** | Base + AI e Marketing + **verifiche incluse** (8/unità al mese nel calcolo originale) | **€15 / €14 / €13** per scaglione (~20% in meno rispetto ai pacchetti separati) |

**Perché il portale proprietari è nella base:** per un'agenzia è indispensabile, e sul mercato è spesso incluso (Avantio; il portale di OwnerRez è gratuito). Il costo marginale per CasaZen è di pochi centesimi per unità (fatture SDI). Il ricavo perso (€2) è recuperato alzando il pacchetto AI e Marketing da €2 a €4.

**Margini** (costo di piattaforma a 250 unità: ~€4,1 la base, ~€8,2 il tutto compreso):

| Voce | Margine |
|---|---|
| Base (portale proprietari incluso) | €4,8 / €3,8 / €2,8 per scaglione |
| Tutto compreso | €6,8 (45%) / €5,8 (41%) / €4,8 (37%) |
| Verifica d'identità | Nessun costo di fornitore (sviluppo in casa): margine da ricalcolare con i costi dell'infrastruttura [NV] |

**Posizionamento:**
- il tutto compreso a €13–15 costa **meno** del pacchetto Kross + Vikey (~€16) e delle suite internazionali, con più servizi;
- il check-in con riconoscimento del volto costava, nell'ipotesi originale, €0,75 contro £1,25 di Uplisting e $2 di Hospitable a controllo; senza costo di fornitore il confronto va rifatto (D10).

### 6.3 Programma Founding Partner (agenzie della prima ora)

- **Prezzo = costo documentato + €1/unità** sul tutto compreso. Il **margine è bloccato**: il prezzo si rivede una volta l'anno **solo** se cambiano le voci di costo indicate nel contratto (NextPax, verifica identità, SDI, AI, hosting, cambio USD/EUR).
- **Durata del prezzo:** tutto il rapporto, per le funzionalità presenti alla firma. Le nuove opzioni con −30%.
- **Posti:** le prime 10 agenzie, entro una data da definire.
- **In cambio:** durata minima 24 mesi, pagamento annuale, test e feedback, referenza o caso di studio.
- **Riduzione dei costi fissi:** con la crescita della piattaforma non abbassa il prezzo partner (da scrivere nel contratto ⚖).
- **Esempio Quokka** (250 unità; formula decisa dal PO: costi fissi coperti dal partner; dal 19/10 la verifica d'identità è sviluppata in casa, quindi non è più un costo a consumo; le versioni precedenti stimavano €660–960 al mese di verifiche a Didit):

| Voce di costo al mese | Importo |
|---|---|
| Channel manager NextPax | €980 |
| Fatturazione elettronica, AI, email, video | ~€100 |
| Costi fissi di piattaforma (hosting, strumenti) | €400 (deciso dal PO: il partner copre almeno i costi fissi) |
| Margine CasaZen (+€1/unità) | €250 |
| **Totale Quokka** | **~€1.730/mese (€6,92/unità)** |

- Quokka spenderebbe **~€270 in meno** rispetto a Kross (~€2.000, −13%) e otterrebbe in più riconoscimento del volto, compliance CH, portale proprietari, CU/770, AI e CRM.
- **Canone fisso di ~€1.730/mese**, senza parte variabile legata alle verifiche. Da rivedere con i costi reali dell'infrastruttura del riconoscimento del volto [NV].
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
| WP6 Compliance: Alloggiati, ISTAT, feratel, pagoPA via API; file AdE, F24, XML Ticino e CSV comunali con promemoria e archivio ricevute; **registro ritenute, generatore CU (record L) e dati 770** | 1,8–3,1 (+0,5–1,0 per CU/770) |
| WP7 Check-in con riconoscimento del volto **in casa** (cattura, confronto, liveness di base, consenso e alternativa, registro di verifica) + QR all'ingresso + videochiamata + serrature | 2,4–4,0 (+1,0–1,8 rispetto al fornitore esterno) |
| WP8 Routine: motore eventi/condizioni/azioni con attese (Hangfire), costruttore web, azioni (messaggi, attività, notifiche, serrature, webhook), creazione da linguaggio naturale, libreria, registro | 1,4–2,25 |
| **Totale** | **~12–19 settimane attive (~3–4,5 mesi)** |

**Ordine** (un solo abbonamento, quindi in sequenza): WP1 → WP6 + WP7 → WP5 → WP4 → WP2 → WP8 → WP3.

**Limiti della stima:** misura grossolana basata sulle righe di codice (±40%); il ritmo misurato viene da poche settimane intense; le integrazioni con fornitori mai usati sono le più a rischio.

**Percorso critico esterno** (da avviare subito, in parallelo):

| Dipendenza | Blocca |
|---|---|
| NextPax / Octorate: risposte scritte + sandbox | WP2 |
| DPIA; parere legale sul controllo all'ingresso (§3.6); licenza e dati di addestramento del modello di riconoscimento del volto | WP7 |
| Enti: documentazione Sicilia, web service Liguria/Calabria, contratto feratel; richieste di API ai portali | Parti di WP6 |
| Namirial (SpinOk) e Centro Paghe: risposta sull'invio per software house; conferma dell'AdE sull'invio della CU con Entratel del sostituto | Invio CU/770 |
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
8. Cosa copre esattamente il commercialista dei €60 ad appartamento (solo CU/770 dell'agenzia o anche il 730 dei proprietari)?
9. Quali controlli sotto copertura hanno già subito o temono, e cosa è stato contestato (per tarare il registro di verifica).

**Per CasaZen:**
- confermare i prezzi con il business plan;
- scegliere e validare il modello di riconoscimento del volto (licenza commerciale e dati di addestramento) e misurare il costo reale dell'infrastruttura;
- decidere il CIN a 18 caratteri nell'export della CU (D12);
- aspettare le risposte di Namirial (SpinOk) e Centro Paghe sull'invio di CU/770;
- confermare con l'assistenza AdE l'invio della CU con Entratel del sostituto;
- pareri legali elencati in §3.6 (controllo automatico senza persona, delegato, tutti gli adulti, qualifica di gestore);
- rivedere il prezzo del check-in (D10).
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
