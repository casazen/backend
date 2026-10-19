# CasaZen: tipo host Agenzia (B2B), analisi finale

**Versione:** finale (v8), 2026-10-19. Sostituisce le versioni dal 14 al 15/10 (storico in git). Novità v5-v8: check-in con selfie dal vivo e confronto volto-documento sviluppato in casa, con avviso all'operatore alla prima foto non verificata; invio ad Alloggiati automatico da CasaZen; nessuna integrazione con le serrature; fiscale alla pari dei concorrenti, CU e 770 fuori scope (§3.7); perimetro del marketing con AI ridefinito (§3.2); costi di terze parti (§5); prezzo di Quokka ~€2.300/mese; stima in finestre di Claude Code Max 20x con timeline (§7).
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
| Ritenuta 21% | Fiscale **alla pari dei concorrenti**: rendiconto mensile delle ritenute del 21% e **F24 precompilato** (codice 1919) nel portale proprietari. **File CU e 770: fuori scope** (nessun concorrente verificato li produce); pagamento e invio restano delegati all'agenzia (§3.7) |
| Check-in | Selfie dal vivo e confronto con la foto del documento, **sviluppato in casa**. Se la prima foto non è verificata, **l'operatore è avvisato subito** e sceglie se chiedere una nuova foto o verificare in videochiamata. QR code sempre come ultima verifica; codice di accesso inviato per messaggio solo dopo la verifica; **invio ad Alloggiati automatico da CasaZen**; cancellazione alla ricevuta (§3.6) |
| Accesso | **Nessuna integrazione con le serrature**: Quokka usa serrature smart già installate e il **codice viene inviato per messaggio subito dopo il riconoscimento facciale** |
| Prezzo (proposta) | Base **€7–9/unità/mese** a scaglioni (portale proprietari e check-in con riconoscimento del volto inclusi) + AI e Marketing €4. **Quokka: ~€2.300/mese** (250 unità, €9,20/unità, margine CasaZen ~57%). Da rivedere con il business plan (D10) |
| Sviluppo | **~112–178 finestre di Claude Code Max 20x** (≈11–18 settimane equivalenti, ipotesi di 10 finestre a settimana) con 1 abbonamento + PO. Timeline per ondate in §7. Il percorso critico sono i tempi esterni |

**Rischi principali:**
1. tempi esterni: fornitori OTA, verifica identità, enti;
2. invio automatico ad Alloggiati: nessuna sandbox e invio irreversibile, il primo invio va supervisionato su una struttura reale;
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
| R13 | **Selfie dal vivo e confronto volto-documento in casa** (2026-10-19): l'ospite scatta un selfie con la fotocamera (non si accettano foto di foto) e CasaZen lo confronta con il documento. Alla prima foto non verificata l'operatore è avvisato subito e sceglie: nuova foto o videochiamata. Il codice di accesso parte solo dopo la verifica. Consenso esplicito; immagini cancellate subito dopo il confronto, copie dei documenti alla ricevuta di Alloggiati |
| R14 | **Fiscale** (2026-10-19): impeccabile e alla pari dei concorrenti, nello stesso modo. Rendiconto mensile delle ritenute e F24 precompilato; **CU e 770 fuori scope** (nessun concorrente verificato li produce); pagamento e invio all'AdE delegati all'agenzia (§3.7) |
| R15 | **Costi di terze parti** (2026-10-19): per ora l'unico costo di integrazione è il channel manager; con l'opzione AI e Marketing attiva si aggiungono email e API social (~$21–55 al mese per agenzia, §5.1). Nessun fornitore di verifica d'identità, nessuna integrazione con le serrature, nessun servizio di invio CU/770 |
| R16 | **Alloggiati inviato direttamente da CasaZen** (2026-10-19), senza conferma manuale dell'host: invio automatico, ricevuta archiviata, stato "Inviato" solo con la ricevuta (§3.6) |

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
| D9 | Serrature: nessuna integrazione; il codice viene inviato per messaggio solo dopo la verifica del selfie | **Rivisto il 19/10** (prima: Seam) |
| D10 | Prezzi: proposta in §6; **Quokka ~€2.300/mese** (19/10: non si abbassa, prima si aumenta il margine di CasaZen). Business plan aggiornato in `Sessions/casazen-platform-launch/business-plan-agency-amendment.md` | Aperto, da confermare con il business plan |
| D11 | Fiscale: parità con i concorrenti (rendiconto mensile, F24 precompilato); CU e 770 fuori scope; pagamento e invio delegati all'agenzia | Deciso il 19/10 |
| D12 | Alloggiati: invio automatico da CasaZen, senza conferma manuale (richiede il client del servizio, la gestione della ricevuta e il consenso dell'host come incaricato) | Deciso il 19/10 |
| D13 | Riconoscimento del volto con modelli open source: AuraFace-v1 (confronto, riserva legale sui dati di addestramento), MiniFASNet (anti foto di foto), SFace come ripiego; collaudo su dati reali prima del lancio | Deciso il 19/10 |

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

### 3.2 CRM e marketing con AI

**Richiesta del PO (2026-10-19):** far crescere e targettizzare la clientela dal CRM, con marketing supportato dall'AI: campagne via email, post sui social e visibilità sui canali AI come ChatGPT, sul modello di HubSpot, Holo e BabyLoveGrowth.

**Cosa fanno i riferimenti** (pagine ufficiali, verificato il 19/10):

| | Cosa fa | Prezzo |
|---|---|---|
| **HubSpot Marketing Hub** ✓ | CRM completo, email, pubblicazione social (Facebook/Instagram, LinkedIn, TikTok, X, YouTube: solo Professional ed Enterprise), pubblicità anche su ChatGPT, SEO, agenti AI, monitoraggio della visibilità nelle risposte AI | Professional da $800/mese più $3.000 una tantum |
| **Holo** (tryholo.ai) ✓ | Dal sito genera e pubblica ogni giorno pubblicità, social, email e SEO. Non ha un CRM | $20–48/mese |
| **BabyLoveGrowth** ✓ | Articoli SEO automatici con pubblicazione sul sito, backlink, monitoraggio della visibilità in ChatGPT/Claude/Gemini | $49–199/mese |

Anche Holo e BabyLoveGrowth sono livelli di creazione e pubblicazione: non possiedono il database degli ospiti. **Il CRM di CasaZen, con i consensi, è il vantaggio**; la pubblicazione è la parte replicabile.

**A chi si può scrivere (limite per il CRM):**

| Origine dell'ospite | Email | WhatsApp |
|---|---|---|
| Prenotazione diretta | Sì con consenso; in Italia "soft spam" per servizi analoghi con opposizione a ogni invio (art. 130 Codice privacy ✓) | Solo con consenso |
| Airbnb | Solo con un consenso separato ed espresso: i dati servono alla transazione (ToS UE 2026 ✓) | Come email |
| Booking.com | **Non per impostazione predefinita:** il contratto vieta di usare i contatti per comunicazioni non richieste (clausola 2.9.3 ✓); l'indirizzo è un alias. Il consenso va raccolto sul canale dell'agenzia (modulo pre-arrivo, check-in) | Come email |
| Dati di Alloggiati | **No** (raccolti per obbligo di legge) | **No** |

**Consenso nel check-in (decisione del PO, 2026-10-19):** per tutti gli ospiti, anche delle OTA, il check-in online chiede il consenso al marketing. Regole di progetto:
- **Facoltativo e non preselezionato.** Se fosse obbligatorio per completare il check-in, il consenso non sarebbe libero e non varrebbe. Il check-in prosegue anche senza.
- **Separato dagli altri consensi** (biometria, informativa) e **per canale**: email e WhatsApp con caselle distinte. Il Garante richiede consensi non accorpati (decisione Libero/Virgilio, 22/2/2024 ✓).
- **Per ogni ospite adulto che dà il proprio indirizzo,** non solo per chi ha prenotato. I minori sono esclusi.
- **Titolare del trattamento è l'agenzia;** CasaZen è responsabile. Il testo indica l'agenzia, le finalità, i canali, come revocare e il link all'informativa.
- **Prova del consenso:** data e ora, versione del testo, canale, prenotazione di origine. Revoca con un clic in ogni email e lista di soppressione per agenzia.
- **Nessun uso dei dati di Alloggiati.** L'email di marketing è un campo a parte, raccolto per quella finalità.

**Qual è l'email giusta:** per Booking.com e Airbnb l'indirizzo della prenotazione è un alias di inoltro e non va usato per il marketing. Nel check-in si chiede **l'email reale** in un campo dedicato, legato al consenso. Si salvano separati: indirizzo della prenotazione (alias) e indirizzo di marketing. Conferma con **doppio consenso** (email con un link da cliccare): non è un obbligo di legge, ma prova che l'indirizzo è dell'ospite, evita errori di battitura e protegge la reputazione del mittente. Fino alla conferma non si invia nulla. Il consenso va chiesto nella pagina di check-in di CasaZen, mai nei messaggi della OTA.


**Perimetro proposto (da confermare dal PO):**

| Fase | Contenuto |
|---|---|
| **1 (WP3a)** | CRM con la funzione di controllo del consenso, segmenti e lista di soppressione per agenzia. **Email** su Resend: un dominio per agenzia con guida SPF/DKIM/DMARC, disiscrizione con un clic, **tracciamento delle aperture spento** (il Garante richiede il consenso preventivo per i pixel dal 29/10/2026 ✓). **Bozze AI** in IT/EN/DE/FR con approvazione umana. **Visibilità sui motori AI:** consentire il crawler di ricerca di OpenAI (OAI-SearchBot ✓), dati strutturati corretti per le pagine di prenotazione diretta, e candidatura a **Google Vacation Rentals** (gratuita, 8–12 settimane, contratto con Google ✓) |
| **2 (WP3b)** | **Social tramite API unificata** (Facebook, Instagram, LinkedIn, Google Business Profile) con approvazione prima della pubblicazione; **WhatsApp** con template e consenso per categoria; rendiconto di invii, clic e prenotazioni da campagna (UTM) |
| **Dopo** | Creazione di pubblicità (Meta, Google, ChatGPT Ads); articoli SEO automatici o rinvio a strumenti come BabyLoveGrowth; sincronizzazione con HubSpot per le agenzie che lo usano; esperimento di app in ChatGPT |
| **Fuori** | Reti di backlink e commenti falsi su Reddit/Quora, pubblico "lookalike" costruito su ospiti OTA, importazione di email OTA in liste marketing, liste acquistate, pubblicazione senza approvazione, TikTok, X e Pinterest come predefiniti (su X un post con link costa $0,20) |

**ChatGPT, cosa è reale (fonti OpenAI, 19/10):** non esiste un "pubblicare su ChatGPT". Esistono la **pubblicità in ChatGPT** (Ads Manager in beta, solo per inserzionisti aziendali; le agenzie non possono aprire account per conto dei clienti; mostrata solo agli utenti Free e Go; l'API per i partner richiede un accordo con OpenAI), i feed prodotto (pensati per il commercio al dettaglio, ospitalità [NV]) e le app di ChatGPT (nessuna garanzia di visibilità). Google dichiara che per i riepiloghi AI non servono requisiti aggiuntivi né file speciali: conta la qualità del sito, i dati strutturati e le recensioni. La visibilità più concreta è **Google Vacation Rentals**.

**Social via API:** collegarsi direttamente a Meta, LinkedIn e TikTok richiede lo stato di fornitore tecnico di Meta, verifica di LinkedIn e audit di TikTok. È più realistico un servizio unificato che detiene le approvazioni: **Ayrshare** (Business $599/mese per 30 profili, poi $8,99 ciascuno fino a 100) o **Zernio** (primi 2 account gratis, poi $1–6 per account), da scegliere dopo aver letto i termini per i rivenditori (non verificati). Postiz in proprio ha licenza AGPL e richiederebbe le approvazioni delle piattaforme.

**Costi e prezzo:** terze parti circa **$21–55 al mese per agenzia** (email $8–16, API social $12–36, testi AI $1–3; stima su prezzi pubblicati), pari al 2–6% di €1.000 (250 unità × €4). WhatsApp e pubblicità sono a carico dell'agenzia. Una piattaforma come HubSpot Professional ($800 più $3.000 di avvio) non entra in €4 a unità. Per le agenzie piccole il costo fisso pesa di più (a 20 unità sono €80): proposta di un minimo mensile o dei social come opzione a parte.

**Cosa è realistico a €4 a unità:** CRM con consensi, email, bozze AI e pianificazione dei social, non una suite come HubSpot.


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
- **Alloggiati Web:** SOAP, 12 operazioni ✓, **invio automatico da CasaZen** (§3.6) (<https://alloggiatiweb.poliziadistato.it/service/service.asmx>).
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

### 3.6 Check-in con riconoscimento del volto, accesso, Alloggiati, routine

**Decisione del PO (2026-10-19):** il riconoscimento del volto si sviluppa in casa, senza fornitori terzi, e lo fa l'utente durante il check-in. Il PO ritiene il flusso già conforme alla legge: nel flusso non ci sono verifiche legali aggiuntive.

**Flusso:**
1. **Dati e documento:** l'ospite compila i dati richiesti da Alloggiati e carica il documento (lettura MRZ/OCR; l'ospite conferma i dati). In una schermata separata, dopo i passaggi obbligatori, la richiesta facoltativa di consenso al marketing (§3.2).
2. **Selfie dal vivo e confronto:** con il **consenso esplicito** al trattamento biometrico, l'ospite scatta un selfie con la fotocamera (non si accettano foto di foto né file caricati dalla galleria). CasaZen lo confronta con la foto del documento; se corrisponde, la verifica è superata.
3. **Se la prima foto non è verificata:** CasaZen **avvisa subito l'operatore**, che sceglie se **chiedere una nuova foto** oppure **verificare con una videochiamata**. L'ospite non prosegue finché la verifica non è superata.
4. **QR code:** resta sempre come ultima verifica, se l'ospite non ha ancora inviato il selfie; scansionato all'ingresso, porta allo stesso passaggio.
5. **Codice di accesso:** inviato per messaggio **solo dopo la verifica**, durante il check-in oppure all'ingresso con il QR code.
6. **Alloggiati:** invio automatico da CasaZen (vedi sotto).
7. **Dopo la ricevuta:** si cancellano dati e copie dei documenti; la ricevuta si scarica **entro 30 giorni** e si conserva 5 anni. Le immagini e i dati biometrici si cancellano **subito dopo il confronto**.
8. **Registro di verifica** per prenotazione: data e ora, esito, numero di tentativi, decisione dell'operatore, ricevuta. Senza immagini.
9. Minori senza biometria.

**Componenti sviluppati in casa:**

| Componente | Note |
|---|---|
| Lettura del documento (MRZ/OCR) | PassportEye (MIT) con Tesseract o equivalenti; l'ospite conferma i dati |
| Confronto del volto | **Scelto: AuraFace-v1, solo `glintr100.onnx`** (ResNet100, 512 dimensioni, ~74 ms su CPU, Apache-2.0): è l'unico modello permissivo i cui autori dichiarano dati scelti per l'uso commerciale e ha i risultati migliori sui test difficili (CFP-FP 95,2%, AgeDB 96,1%, CPLFW 90,9%). **Riserva legale:** i dati di addestramento non sono nominati e fal non ha risposto alle richieste pubbliche di conferma: serve una conferma scritta prima del lancio. **Non usare gli altri file del repository** (rilevatore e allineatore InsightFace, non commerciali). **Ripiego:** OpenCV Zoo SFace (Apache-2.0, 19 ms su CPU, dati non documentati, nessun risultato sui test difficili). Escluse InsightFace, EdgeFace, AdaFace e i modelli addestrati su MS-Celeb-1M o VGGFace2. Rilevatore e allineamento hanno una licenza a parte da verificare (YuNet, MIT, dati di addestramento [NV]) |
| Selfie dal vivo (controllo anti foto di foto) | Obbligatorio. **Scelto: MiniFASNet** (Silent-Face-Anti-Spoofing, Apache-2.0 anche i pesi): nessuna certificazione ISO 30107-3, dati di addestramento non dichiarati [NV], non resiste a video iniettati (deepfake, telecamere virtuali). L'avviso all'operatore alla prima foto non verificata compensa. Da browser non si può provare che la telecamera sia reale: un'app nativa dà garanzie migliori. Un upgrade a licenza a canone fisso (Oz Forensics, Regula, Luxand) resta possibile in futuro |
| Videochiamata con l'operatore | Daily (gratis fino a 10.000 minuti al mese): infrastruttura video, non verifica d'identità |
| Documenti | Solo controlli formali (checksum MRZ, scadenza, formato) |
| Soglia e collaudo | Nessun test pubblicato riflette un ritratto piccolo di documento contro un selfie. Prima del lancio: collaudo su coppie reali (carta d'identità, passaporto, patente, permesso di soggiorno), con consenso e DPIA; soglia calibrata a un tasso di falsi accoppiamenti di 1 su 10.000 (obiettivo NIST SP 800-63A, falsi rifiuti 1 su 100); due soglie (accetta, passa all'operatore, rifiuta); conferma su dati non usati per la calibrazione; ripetere il collaudo a ogni cambio di modello, rilevatore o versione |

**Invio ad Alloggiati: automatico da CasaZen** (decisione del PO, 2026-10-19, R16). Oggi il codice non trasmette: l'host invia sul portale e segna "inviato manualmente" (`docs/runbooks/alloggiati.md`). Da sviluppare:
- client del servizio SOAP (`GenerateToken` → `Test` → `Send` → `Ricevuta`): `Test` prima di ogni `Send`, `Send` solo se ogni riga è valida;
- **nessuna sandbox e `Send` irreversibile:** il primo invio va supervisionato su una struttura reale; cosa succede con un invio ripetuto (duplicati) è [NV];
- scadenze: 24 ore dall'arrivo (6 ore per soggiorni fino a 24 ore); il portale accetta solo arrivo di oggi o di ieri;
- **ricevuta il giorno dopo**, solo PDF per l'intero account: serve un nuovo stato "inviato, ricevuta in attesa", un job giornaliero e un archivio privato di 5 anni; "Inviato" solo con la ricevuta;
- credenziali dell'host (codici dispositivo) conservate e rigenerate quando cambia la password; l'host delega CasaZen come incaricato alla trasmissione (D.M. 2013/2021, art. 2 ✓) con un consenso in configurazione;
- errori: invio parziale (il portale acquisisce solo le righe corrette), allarmi e nuovo tentativo.

**Accesso all'immobile:** Quokka usa **serrature smart già installate** (dato del PO, 2026-10-19; in precedenza risultavano keybox Masterlock). CasaZen non si integra con le serrature: invia il codice per messaggio solo dopo la verifica del selfie, durante il check-in o all'ingresso con il QR code. Il codice non è creato né revocato da CasaZen: resta quello impostato dall'agenzia sulla serratura (non è quindi unico per soggiorno né scade alla partenza).


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

### 3.7 Ritenuta del 21%: rendiconto mensile e F24 (pari ai concorrenti; CU e 770 fuori scope)

**Decisione del PO (2026-10-19):** sul fiscale CasaZen deve essere impeccabile e produrre quello che producono i concorrenti, nello stesso modo. Il pagamento dell'F24 e l'invio all'AdE restano delegati all'agenzia.

**Perché serve:** l'agenzia che incassa gli affitti brevi è sostituto d'imposta (art. 4 D.L. 50/2017; dal 1/1/2027 art. 208 del nuovo TUIR, D.Lgs. 117/2026). A ogni pagamento al proprietario trattiene il **21% a titolo d'acconto** sul lordo e lo versa con **F24 codice 1919** entro il **16 del mese successivo**, con un versamento cumulativo per tutti i proprietari ✓ (<https://www.agenziaentrate.gov.it/portale/documents/d/guest/locazioni_brevi_disciplina_fiscale_e_regole_per_intermediari_aprile-2026>).

**Cosa fanno i concorrenti** (pagine ufficiali, verificato il 19/10):
- **Octorate ✓:** impostazione "sostituto d'imposta" per agenzia; voci di commissione marcabili come deducibili o no prima del calcolo; distinta di pagamento al proprietario e **Modello F24 stampabile**; esportazione Excel.
- **CiaoBooking ✓:** flag "sostituto d'imposta" sul proprietario (scala un ulteriore 21% dal netto); **nessun F24**.
- **Nessun concorrente verificato produce il file CU o 770:** Kross, Smoobu, Lodgify, Rentals United, Beddy, Scidoo, Slope, Vikey non ne parlano sui siti ufficiali; Avantio non è verificabile.

**Cosa produce CasaZen (parità o meglio):**
- flag di sostituto d'imposta per agenzia e per proprietario (con P.IVA non si trattiene);
- commissione dell'agenzia deducibile o no; base di calcolo: lordo con pulizie e biancheria a forfait e provvigione trattenuta dai soldi del proprietario; esclusi cauzioni, caparre e penali ✓;
- **rendiconto mensile** per proprietario, appartamento e prenotazione, con il totale da versare, in PDF e CSV/Excel;
- **F24 precompilato** (codice 1919, mese, anno, importo) in PDF;
- riconciliazione trattenuto/versato con la data di pagamento che l'agenzia registra;
- anno di cassa e anno di soggiorno per ogni importo (le prenotazioni a cavallo d'anno restano divise).

**Delegato all'agenzia:** il pagamento dell'F24 e l'invio all'AdE.

**Fuori scope, ma da sapere** (ricerca del 19/10):
- Il lavoro di fine anno resta a carico dell'agenzia: **CU entro il 16 marzo, 770 entro il 31 ottobre**. Il rendiconto mensile non lo sostituisce.
- L'invio all'AdE è solo con **Entratel** (oltre 20 percipienti); l'AdE non ha un'API di invio e nessun gateway è stato trovato (controllati Openapi, Invio Facile, Genya, Namirial/SpinOk, ACCA).
- Se si riprenderà: la CU richiede il **CIN a 18 caratteri esatti** (`CinFormat.cs` ne accetta 11–18) e il **codice catastale** del comune, non l'ISTAT.

**Da chiedere a Quokka:** cosa copre esattamente il commercialista dei €60 ad appartamento (CU/770 o anche il 730 dei proprietari). Dato riferito dal PO, non verificato.

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
| AI e Marketing (se attivo) | ~$21–55 al mese per agenzia: email su Resend con piano condiviso $8–16, API social $12–36, testi AI $1–3 (stima su prezzi pubblicati) | WhatsApp e pubblicità a carico dell'agenzia |

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

| | **A. 0 clienti** | **B. Quokka, 250 unità, ~€2.300** | **C. 250 unità a listino** (Base €7 + AI e Marketing €4) |
|---|---|---|---|
| **Paga CasaZen:** channel manager | €0 (se il minimo parte dal go-live) o €350 | €980 (€350 + 200 × €3,15) | €980 |
| **Pagano i clienti a CasaZen** | — | €2.300 (€9,20/unità) | €2.750 (€11/unità) |
| **Risultato mensile** | €0 / −€350 | **+€1.320 (57%)** | **+€1.770 (64%)** |

**Come leggere la tabella:**
- **Scenario A:** se NextPax fattura il minimo dalla firma, CasaZen perde €350 al mese finché non arriva il primo cliente. Conviene firmare solo quando il cliente è vicino.
- **Scenario B (Quokka):** il prezzo non si abbassa (19/10): ~€2.300 al mese, circa il 16% sotto il listino di €2.750, per un pacchetto con portale proprietari, check-in con riconoscimento del volto, CRM e marketing, routine. Il margine di CasaZen è €1.320, il 57%.
- **Soglia di convenienza:** il minimo di €350 supera il ricavo del Base (€9) sotto le **39 unità**: a 30 unità si incassano €270 e si pagano €350. Va rivisto lo scaglione o il minimo (D10).
- **Margine lordo:** 44–64% a seconda del pacchetto, perché il channel manager è un costo variabile per unità. Il target di margine lordo ≥80% del business plan vale per il B2C, non per il tier Agenzia.
- **AI e Marketing** (+€4/unità, +€1.000 al mese per 250 unità): costo di terze parti ~$21–55 per agenzia (§5.1), cioè il 2–6% del ricavo.
- **Non conteggiati:** costi di piattaforma (hosting, email, SDI, AI, video, abbonamento di sviluppo), tempo del PO, pareri legali e sviluppo (§7).

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
| **Base** | **Gestionale**: proprietà, calendario, prenotazioni, ospiti, KPI.<br>**Channel manager** (Airbnb, Booking.com, Vrbo, Expedia): sincronizzazione di disponibilità, prezzi e prenotazioni; creazione e modifica degli annunci da CasaZen; recensioni.<br>**Inbox unificata** con **risposte AI suggerite**, inviate dall'operatore.<br>**Sito di prenotazione diretta** con pagamento Stripe sull'account dell'agenzia.<br>**Check-in online** con riconoscimento del volto fatto dall'ospite (confronto tra foto e documento); codice di accesso inviato per messaggio dopo l'esito.<br>**Adempimenti via API**: Alloggiati Web, ISTAT (regioni con API), feratel; calcolo della tassa di soggiorno; **file pronti** per dichiarazioni, CU e portali senza API.<br>**Portale proprietari**: dati e documenti, rendiconto mensile automatico, fatture elettroniche SDI per conto dell'agenzia, file di bonifico SEPA/CH, registro delle ritenute.<br>**Routine**: costruttore QUANDO → SE → ALLORA con libreria di routine pronte.<br>**Fornitori**: marketplace, fornitori propri a 0%, staff, app | **€9** (10–49 unità) · **€8** (50–149) · **€7** (150–399) · preventivo (400+) |
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

- **Prezzo = listino con sconto di circa il 15%, bloccato per 24 mesi** (proposta, D10); si rivede solo se cambiano il costo del channel manager o il cambio USD/EUR. Quokka: €2.300 al mese, −16% sul listino di €2.750.
- **Durata del prezzo:** tutto il rapporto, per le funzionalità presenti alla firma. Le nuove opzioni con −30%.
- **Posti:** le prime 10 agenzie, entro una data da definire.
- **In cambio:** durata minima 24 mesi, pagamento annuale, test e feedback, referenza o caso di studio.
- **Margine:** dal 19/10 il margine di CasaZen ha la priorità. Non si applica più la formula "costo + €1/unità" né la copertura dei costi fissi a prezzo di costo.
- **Esempio Quokka** (250 unità):

| Voce al mese | Importo |
|---|---|
| Prezzo Quokka (Base + AI e Marketing, €9,20/unità) | **~€2.300** |
| Costo channel manager NextPax (€350 + 200 × €3,15) | €980 |
| **Margine CasaZen** | **~€1.320 (57%)** |

- Rispetto ai ~€2.000 che Quokka paga oggi a Kross: **+€300 (+15%)**. In cambio ottiene check-in con riconoscimento del volto, compliance CH, portale proprietari, routine, CRM e marketing. La piattaforma di marketing e CRM che usano oggi non è nota: se la sostituiscono, il confronto migliora.
- Il prezzo si giustifica con il **pacchetto Agenzia completo, non con personalizzazioni**: il prodotto resta unico (R1).

- Il listino pubblico (Base €7 + AI e Marketing €4, 250 unità) costerebbe €2.750.
- Se Quokka sostituisce anche la sua attuale piattaforma di marketing e CRM, il risparmio complessivo aumenta.

**B2C:** il listino deciso l'8/10 (Starter €29, Pro €79, Scale €199) non copre il channel manager (€3,15–3,50 a unità). Per il B2C va venduto come opzione per unità (es. €4).

---

## 7. Stima dello sviluppo (finestre di Claude Code Max 20x)

**Unità di misura:** la **finestra** è una sessione di lavoro di 5 ore con 1 abbonamento Claude Code Max 20x. Niente giorni-persona.

**Calibrazione:**
- il modulo Locazioni (~39k righe tra codice, test e frontend, con l'integrazione RLI verso l'AdE) è stato costruito in ~2,5 settimane attive: **~15k righe a settimana**;
- **ipotesi di conversione: 1 settimana attiva = ~10 finestre** (2 al giorno per 5 giorni), cioè ~1.500 righe a finestra. **[NV]** Anthropic non pubblica limiti numerici del piano Max ✓ (<https://support.claude.com/en/articles/11145838-using-claude-code-with-your-pro-or-max-plan>): il rapporto va ricalibrato dopo la prima ondata;
- le stime includono +25% per test umani sull'interfaccia e piccoli refactor; precisione ±40%.

| Pacchetto | Finestre | Cosa pesa |
|---|---|---|
| WP1 Fondamenta: `Agency`, ruoli `PropertyOwner`/`Staff`, paese/valuta/fuso, tedesco, piani | 12–20 | Multitenancy, ruoli, traduzioni IT/EN/DE, migrazioni |
| WP2 OTA: client del channel manager, mappatura, disponibilità/tariffe, prenotazioni, editor contenuti, inbox, risposte AI, recensioni | 18–28 | Il pacchetto più grande: tante superfici (editor, inbox, mappatura) e un partner esterno mai usato |
| WP3a CRM con controllo del consenso, email su Resend, bozze AI, dati strutturati e candidatura a Google Vacation Rentals | 12–18 | Consensi per canale, segmenti, deliverability, testi AI |
| WP3b Social tramite API unificata, WhatsApp con template, rendiconto delle campagne | 8–12 | Servizio social di terzi, approvazioni Meta, UTM |
| WP4 Proprietari: portale, rendiconti mensili con ritenute e F24 precompilato, SDI per conto, file di bonifico, account Stripe a v2 | 14–22 | Rendiconti, PDF, migrazione Stripe |
| WP5 Fornitori propri e staff | 4–7 | Estensione di moduli esistenti |
| WP6a Alloggiati automatico: client SOAP, `Test`→`Send`, ricevuta e stato, job giornaliero, consenso, allarmi | 6–10 | Nessuna sandbox, invio irreversibile, ricevuta il giorno dopo |
| WP6b Altri adempimenti: ISTAT, feratel, pagoPA, file e CSV per Ticino e comuni | 7–11 | Più enti, ognuno con il suo formato |
| WP7 Check-in: selfie dal vivo, confronto volto-documento in casa, consenso, avviso e console dell'operatore (nuova foto o videochiamata), QR, messaggio con il codice, registro di verifica | 17–28 | Modello di volto, soglia da calibrare, anti foto di foto, console operatore, videochiamata, cancellazione dei dati |
| WP8 Routine: motore eventi/condizioni/azioni con attese, costruttore web, azioni, creazione da frase, libreria, registro | 14–22 | Motore con attese (Hangfire) e costruttore |
| **Totale** | **~112–178** (≈11–18 settimane equivalenti) | |

**Perché la stima è alta:**
- sono **6 funzionalità più le fondamenta** di un nuovo tipo di host, non una funzione;
- ogni pacchetto comprende backend, web, i18n in IT/EN (e DE), migrazioni, dati demo e test, come richiedono le regole del repo;
- tre pacchetti pesano oltre il 40%: WP2, WP7 e WP1;
- CRM, marketing e routine (WP3a + WP3b + WP8) valgono circa il 30%: sono rimandabili a una seconda uscita, ma il marketing è parte di ciò che Quokka paga;
- il +25% per test umani e la precisione di ±40% allargano la forchetta.

**Timeline** (un solo abbonamento, quindi le ondate sono in sequenza):

| Ondata | Contenuto | Finestre | Cumulato | Gate esterni |
|---|---|---|---|---|
| 1 | WP1 Fondamenta | 12–20 | 12–20 | — |
| 2 | WP6a Alloggiati automatico + WP7 Check-in | 23–38 | 35–58 | Struttura reale con credenziali per il primo invio supervisionato; conferma scritta sui dati di addestramento di AuraFace; collaudo su dati reali; DPIA |
| 3 | WP6b + WP5 + WP4 | 25–40 | 60–98 | Stripe Accounts v2; documentazione ISTAT Sicilia; contratto feratel |
| 4 | WP2 OTA | 18–28 | 78–126 | Channel manager scelto, sandbox e certificazione |
| 5 | WP8 Routine | 14–22 | 92–148 | — |
| 6 | WP3a + WP3b CRM, campagne e social | 20–30 | 112–178 | Account Meta (WhatsApp e social); domini email; candidatura a Google Vacation Rentals da avviare prima (8–12 settimane) |

I test umani sull'interfaccia sono a fine ondata e sono già dentro le finestre. Equivalenza in calendario: finestre ÷ 10 = settimane **[ipotesi]**.

**Limiti della stima:** misura grossolana basata sulle righe di codice; il ritmo misurato viene da poche settimane intense; le integrazioni con fornitori mai usati sono le più a rischio.

**Percorso critico esterno** (da avviare subito, in parallelo):

| Dipendenza | Blocca |
|---|---|
| Channel manager: scelta (in verifica dal PO), risposte scritte e sandbox | WP2 |
| Struttura reale con credenziali Alloggiati per il primo invio supervisionato | WP6a |
| Licenza e dati di addestramento del modello di volto; DPIA | WP7 |
| Enti: documentazione Sicilia, web service Liguria/Calabria, contratto feratel; richieste di API ai portali | WP6b |
| Stripe: migrazione degli account delle agenzie ad Accounts v2 | WP4 |
| Google Vacation Rentals: candidatura e contratto (8–12 settimane, gratuita); servizio social di terzi: lettura dei termini per rivenditori | WP3a, WP3b |
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
- definire il consenso dell'host alla delega per l'invio ad Alloggiati e la gestione delle credenziali;
- supervisionare il primo invio ad Alloggiati su una struttura reale;
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
