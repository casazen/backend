# Quokka 360 × CasaZen: analisi delle 6 funzionalità richieste

**Data:** 2026-10-14 · **Tipo:** analisi pre-vendita (nessuna modifica di codice)
**Metodo:**
- analisi del codice di `backend`, `frontend` e `mobile` (branch `develop` al 2026-10-14);
- ricerca web su fonti ufficiali (OTA, enti pubblici, documentazione dei vendor).

**Legenda delle fonti:**
- **[NV]** = non verificato su una fonte ufficiale (fonte secondaria o snippet).
- **⚖** = serve il parere di un legale o di un commercialista prima di impegnarsi con il cliente.

---

## 0. Sintesi

| # | Richiesta | Stato attuale in CasaZen | Soluzione raccomandata | Build/Buy | Taglia* | Rischio principale |
|---|---|---|---|---|---|---|
| 1 | OTA completa: annunci centralizzati, messaggi, AI | Solo iCal (funzionante). Adapter OTA **stub** e congelati (D10). Nessuna entità messaggi | Integrare un **channel manager via API**: Channex se il contenuto annunci può aspettare, NextPax/Rentals United/Octorate se è un requisito da subito. AI in modalità "bozza + approvazione" | Buy (connettività) + Build (inbox, AI) | L + M (+L per i contenuti) | La connessione diretta alle OTA è impossibile oggi. La creazione degli annunci dipende dal fornitore |
| 2 | CRM avanzato, campagne, AI | Ospiti con consenso marketing GDPR versionato. Nessuna campagna o segmentazione | CRM leggero sul nostro DB + invio con Resend (team separato) o SES. WhatsApp in fase 2 | Build + Buy (invio) | L + M | **Gli ospiti Airbnb non sono contattabili per marketing** (policy Airbnb). Il valore reale riguarda gli ospiti diretti o con consenso |
| 3 | Portale proprietari: statistiche, fatturazione (B2B) | KPI host e report fiscali esistono. Nessun ruolo "proprietario", rendiconto o SDI | Ruolo `Owner` limitato alle proprie proprietà, rendiconto mensile, fattura di commissione (IT: SDI via provider; CH: QR-bill) | Build + Buy (SDI) | L | Valuta CHF/EUR. Obblighi fiscali di un intermediario svizzero su immobili italiani ⚖ |
| 4 | Fornitori/dipendenti propri senza commissioni (B2B) | Marketplace fornitori reale. La modalità `Manual` **non ha già oggi commissioni**. Mancano rubrica "fornitori miei" e ruolo staff | Rubrica fornitori dell'organizzazione + invito diretto + ruolo staff (pulizie/manutenzione) + regola "0% per piano B2B" | Build | M | Cannibalizza il take-rate dei fornitori (oggi comunque spento) |
| 5 | Fiscalità completa IT + CH con integrazioni | IT: CIN, motore tassa di soggiorno, report cedolare. Alloggiati **solo file manuale**. ISTAT bloccato. **CH: zero** | IT: Alloggiati via SOAP + Ross1000 via WS + generazione file (dichiarazione imposta, dati CU/F24) consegnati al commercialista. CH: via partner (Chekin / sistemi di destinazione) | Build (IT) + Buy (CH) | L (IT) + M/L (CH) | **"Integrazioni funzionanti ovunque" non è ottenibile**: Entratel non ha API REST e molti cantoni non hanno API |
| 6 | Routine con AI, check-in/out (opzionale) | Check-in online (inserimento dati), wizard di check-out, job Hangfire per sola compliance. Spec serrature (Seam) pianificata | Motore task "prenotazione → attività" + checklist + foto da mobile + AI per controllo foto, sintesi e traduzione. Serrature via la spec esistente | Build + Buy (serrature) | M + M + L | **In Italia il self check-in senza riconoscimento de visu (o video in tempo reale) non è conforme** (CdS 9101/2025) |

\* **Taglie indicative**, per 1 sviluppatore senior: S ≤ 2, M 2–6, L 6–12 settimane-persona. Da validare con il team.

**Ordine di grandezza complessivo:** circa **80–110 settimane-persona**, cioè circa 12 mesi con 2 sviluppatori a tempo pieno. Sono esclusi QA, revisione legale e la creazione/modifica dei contenuti annunci.

### Valutazione critica (da leggere prima di proporre qualcosa)

1. **Il cliente oggi usa Kross Booking (Zucchetti).**
   - Il loro sito di prenotazione `quokka.kross.travel` riporta "Powered by Kross Booking", con valute CHF/EUR.
   - Kross offre già channel manager, check-in online, report turistici e area proprietari.
   - **Il punto non è "cosa chiedono" ma "cosa manca in Kross"**: va chiarito al prossimo incontro (§7), altrimenti rischiamo di ricostruire un PMS completo per inseguire un incumbent.
2. **Le 6 richieste vanno contro decisioni di prodotto già prese:**
   - D10 (API OTA congelate);
   - US-011 inbox unificata e US-012 copilot AI (congelate);
   - US-016 enterprise e US-017 multi-paese (congelate).
   - Prima di impegnarsi serve una **decisione esplicita del PO** di aprire il segmento "property manager B2B multi-paese" (§8).
3. **Prerequisito non visibile al cliente ma bloccante: CasaZen è cablato sull'Italia.**
   - `Property` non ha un campo paese.
   - Ci sono 23 valori `"EUR"` scritti a mano nel codice C# e 167 riferimenti a `Europe/Rome` in `Casazen.Core`.
   - Le lingue sono solo IT/EN, ma Laax, Davos, Andermatt ed Engadina richiedono almeno il DE.
   - Senza il punto 2 di questo documento nessuna funzionalità svizzera è realizzabile.
4. **Non firmare un perimetro "tutto incluso".** Vanno proposte delle fasi (§5), con esclusioni scritte (§6).

---

## 1. Il prospect (dati verificati)

- **Società:** Quokka Sagl, con sede a Massagno–Lugano (CH) e a Maslianico (CO, IT).
- **Attività:** gestione affitti brevi per conto di proprietari di seconde case.
- **Zone servite:**
  - CH: Lugano, Locarno, Bellinzona, Mendrisiotto, Leventina, Laax, Andermatt, Davos, Engadina.
  - IT: Como, Milano, Liguria, Trapani, Tropea, Ascoli Piceno.
  - Fonte: <https://www.quokka360.com/en/vacation-rental-management/>
- **Modello economico:**
  - commissione del 25% per la gestione completa, esclusi tassa di soggiorno, pulizie, commissioni OTA e carte;
  - pagamento ai proprietari **mensile**;
  - i proprietari hanno già un'"area personale" (prenotazioni, calendario, spese, guadagni).
- **Canali:** Booking.com, Airbnb, Vrbo, Expedia e sito proprio.
- **Strumento attuale:** Kross Booking (<https://quokka.kross.travel/>).
- **Numero di unità: non verificato** (il dato "250+" compare solo in fonti secondarie). Va chiesto al cliente.
- **Franchising "Quokka Point"** [NV]: implicherebbe un modello multi-organizzazione o multi-brand (oggi congelato in US-016).

---

## 2. Prerequisiti trasversali (bloccanti per CH e per il B2B)

| Prerequisito | Oggi | Lavoro | Taglia |
|---|---|---|---|
| Modello paese/mercato | `Property` ha solo `ComuneIstatCode` e `RegionCode` italiani (`Casazen.Core/Entities/Property.cs:76-83`). Il modello è previsto solo nella spec congelata `spec-eu-compliance-es-fr.md` | `CountryCode` e una giurisdizione astratta (comune IT oppure comune + cantone CH). Le regole di compliance vanno instradate per paese | L |
| Valuta | EUR scritto a mano (`ServiceCharges.cs:25`, `RentCharges.cs:18`, `BookingService.cs:463`, …) | Valuta per proprietà/organizzazione, importi `decimal` + ISO 4217, report in più valute (nessuna conversione automatica nei rendiconti) | M |
| Fuso orario | Default `Europe/Rome` (`Property.cs:201`), 167 riferimenti in Core | `Europe/Zurich` è identico a `Europe/Rome` (stesso offset e stesse regole DST), quindi è un problema di nomenclatura e validazione, non di calcolo | S |
| Lingue | Solo IT/EN (frontend, mobile, `.resx`) | DE subito per le pagine rivolte agli ospiti e allo staff; FR solo se il cliente opera in Romandia (oggi no) | M |
| Ruoli B2B | `OrgRole`: Owner, Admin, PropertyManager, Collaborator, Accountant (flag `OrgTeam` spento). Il ruolo "Owner" è il **titolare dell'organizzazione**, non il proprietario dell'immobile | Nuovo ruolo distinto (es. `PropertyOwner`, proprietario dell'immobile) + ruolo `Staff` | M |
| Prezzi B2B | Piani Starter/Pro/Scale con prezzi in conflitto tra le fonti (`docs/runbooks/open-access.md` contro `FeatureFlags.cs:142`) | Listino B2B per unità/mese che includa i costi pass-through (§4) | — (decisione) |
| Provider AI | `DeepSeekAiProvider` (default Stub, DeepSeek configurabile), che parla già sia l'API OpenAI sia quella Anthropic | Per dati personali degli ospiti (messaggi) usare un provider con DPA UE e senza addestramento sui dati. **Non DeepSeek**: il Garante ne ha bloccato l'app a gennaio 2025 ⚖ | S |

---

## 3. Analisi per funzionalità

### 3.1 Integrazione OTA completa (annunci, disponibilità, messaggi, AI)

**Cosa c'è oggi**
- **Sincronizzazione iCal reale** (`Infrastructure/Services/ICal/*`, ADR-002), con conversione del blocco iCal in "soggiorno OTA" (D7).
- **Adapter OTA:**
  - stub in `Casazen.Infrastructure/OTA/*` (es. `BookingComAdapter.cs:48` "TODO: Implement actual API call");
  - `AirbnbAdapter` usa endpoint non documentati;
  - `IChannelAdapter` non prevede la gestione dei contenuti.
- **Nessuna entità** Conversation/Message.

**Vincoli ufficiali: la connessione diretta non è percorribile**

| OTA | Stato 2026 | Fonte |
|---|---|---|
| Booking.com | *"we are pausing integrations with new connectivity providers until further notice"* | <https://connect.booking.com/> |
| Airbnb | Accesso API "a sola ed esclusiva discrezione" di Airbnb; nessun modulo pubblico di candidatura | <https://www.airbnb.com/terms/api> · <https://developer.withairbnb.com/> |
| Expedia/Vrbo | Candidatura via email, accettazione a discrezione di Expedia; la Property API per l'onboarding dei contenuti risulta non disponibile per nuovi partner [NV] | <https://developers.expediagroup.com/supply/lodging/cpp/overview> |

**Opzioni: channel manager con API per software house**

| Fornitore | Creazione/modifica annunci | Messaggi | Prezzo ufficiale | Note |
|---|---|---|---|---|
| **Channex** (UK) | **No**: *"cannot currently create listings on OTAs"* e non aggiorna immagini o servizi. Roadmap 2027 solo per Booking.com e Airbnb, *"not a guaranteed release date"* | Airbnb, Booking.com, Expedia (non Vrbo) | $130/mese + $0,50/unità + $0,50/unità per messaggi e recensioni. Nessun setup, recesso con 30 giorni di preavviso | Vende solo a software house (nessun conflitto), API OpenAPI, staging gratuito. Vrbo: le valute supportate non includono CHF |
| **NextPax** (NL) | **Sì** secondo il vendor: contenuti, immagini, servizi, policy, attivazione dei canali | Airbnb, Booking.com, Expedia, Vrbo | €3,50/unità/mese (€3,15 annuale), **minimo €350/mese** (50 unità incluse); prezzo della Supply API "contact us" | Vende anche direttamente ai gestori (concorrente). Verificare chi è titolare dei contratti OTA |
| **Rentals United** (ES) | **Sì** (approccio content-first, creazione annunci Airbnb documentata) | Airbnb, Booking.com, Vrbo; API "restricted" | Prezzo per software house non pubblico | Solo XML; lock-in sul modello dei contenuti |
| **Octorate** (IT) | Sì (endpoint contenuti + push al portale) | Chat API, anche con assistente AI | Non pubblico | Vende anche un proprio PMS in Italia (concorrente) |

Fonti:
- <https://channex.io/pricing.md>
- <https://channex.io/llms.txt>
- <https://nextpax.com/nextpax-pricing>
- <https://info.rentalsunited.com/whitelabel-channel-manager-api>
- <https://api.octorate.com/connect/>

**Messaggi e AI: vincoli**
- **Airbnb Off-Platform Policy:** prima della prenotazione la comunicazione resta su Airbnb. È vietato chiedere email o altri canali e usare i contatti per marketing (<https://www.airbnb.com/help/article/2799>).
- **Booking.com:** le richieste di cambio date e "cancel for less" vanno gestite nell'extranet, non via API (<https://developers.booking.com/connectivity/docs/messaging-api/understanding-the-messaging-api>).
- **AI Act, art. 50:** obbligo di trasparenza verso chi interagisce con un sistema AI, applicabile dal 2 agosto 2026. Verificare eventuali modifiche dal Digital Omnibus ⚖.
- **Prassi dei concorrenti** (Guesty ReplyAI, Hostaway AI, Hospitable):
  - bozza generata dall'AI con base di conoscenza per immobile;
  - invio automatico solo sopra una soglia di confidenza e su argomenti consentiti;
  - controllo di sicurezza prima dell'invio.

**Raccomandazione**
1. Mantenere `IChannelAdapter` come porta e scrivere **un solo** adapter reale verso il channel manager scelto. Eliminare gli stub per singola OTA.
2. **Se la creazione/modifica degli annunci è indispensabile al go-live:** fare due diligence scritta con **NextPax** e **Rentals United**. Chiedere:
   - una matrice per OTA di ciò che si può creare o modificare;
   - il prezzo della Supply API;
   - la portabilità degli account e dei contratti OTA in caso di uscita.
3. **Altrimenti:** usare **Channex** per disponibilità, tariffe, prenotazioni, messaggi e recensioni (il costo più basso e il minor lock-in); per ora i contenuti restano nelle extranet.
4. **Inbox unificata** (riattiva US-011) + **AI in modalità bozza** (riattiva US-012):
   - base di conoscenza per immobile;
   - argomenti bloccati;
   - rimozione di contatti e link nei thread Airbnb prima della prenotazione;
   - log di approvazione.
5. **Da verificare:** copertura delle OTA svizzere (e-domizil, Interhome, ticino.ch, dove Quokka pubblica annunci). Non coperta dalla ricerca.

---

### 3.2 CRM avanzato con campagne e AI

**Cosa c'è oggi**
- `Guest` con PII cifrati e `MarketingConsent` (`Core/Entities/Guest.cs:122-124`).
- Consenso versionato e solo in aggiunta (`GuestConsentRecord`).
- Resend usato solo per email transazionali.

**Vincoli ufficiali (da comunicare al cliente)**
- **Airbnb:** è vietato *"using guest contact information for marketing communications"* (<https://www.airbnb.com/help/article/2799>; ToS §11.1 <https://www.airbnb.com/help/article/2908>).
  → **Gli ospiti arrivati da Airbnb non possono entrare in campagne di marketing.**
- **Booking.com**, General Delivery Terms §2.9.3: è vietato usare i contatti per *"unsolicited electronic communications"* (<https://admin.booking.com/hotelreg/terms-and-conditions.html>).
  → Si possono contattare solo con un consenso raccolto direttamente dal gestore. La lettura del DMA art. 5(4) va validata ⚖.
- **Italia:**
  - art. 130 Codice Privacy: consenso preventivo per email, SMS e WhatsApp;
  - "soft spam" (c. 4): **solo email**, solo per servizi analoghi, solo se l'email è stata fornita durante la vendita, quindi in pratica **solo prenotazioni dirette**;
  - linee guida Garante 2013: <https://www.garanteprivacy.it/home/docweb/-/docweb-display/docweb/2542348>.
  - I dati Alloggiati e dei documenti d'identità **non possono mai** finire nel CRM (limitazione della finalità, art. 5(1)(b) GDPR).
- **Svizzera:** UWG art. 3 cpv. 1 lett. o richiede l'opt-in, con eccezione per i clienti esistenti e servizi analoghi (<https://www.fedlex.admin.ch/eli/cc/1988/223_223_223/de>). Per l'EDÖB l'accettazione delle condizioni generali non basta.
- **WhatsApp:** opt-in esplicito che nomina l'azienda. Prezzo per messaggio dal 1° luglio 2025, con template "utility" gratuiti nella finestra di 24 ore (<https://developers.facebook.com/docs/whatsapp/pricing>).

**Raccomandazione**
1. **CRM leggero nel nostro DB, per tenant:**
   - profilo ospite unificato, soggiorni, `ConsentRecord` per canale e base giuridica, lista di soppressione per tenant;
   - `Segment` salvato come definizione di filtro, `Campaign` / `CampaignRecipient`, eventi di consegna.
   - Regole nel codice: fonte Airbnb mai marketizzabile; Italia + WhatsApp/SMS solo con consenso.
2. **Invio email:**
   - Resend con un **team separato per il marketing**, così le email transazionali restano protette. Le reputazioni condivise tra tenant sono un rischio dichiarato da Resend (<https://resend.com/docs/knowledge-base/setting-up-resend-for-multi-tenants>).
   - Un sottodominio per ogni gestore, header List-Unsubscribe con un clic, accodamento su Hangfire.
   - Alternativa con migliore isolamento: Amazon SES con tenant (<https://docs.aws.amazon.com/ses/latest/dg/tenants.html>).
   - Evitare Resend Broadcasts: i contatti sono globali per team e la disiscrizione vale per tutto il team.
3. **WhatsApp (fase 2):** Meta Embedded Signup, così ogni gestore è titolare del proprio account WhatsApp Business e Meta gli fattura direttamente.
4. **AI realistica:**
   - testi delle campagne in IT/EN/DE/FR con approvazione umana;
   - richiesta in linguaggio naturale → filtro di segmento con anteprima del numero di destinatari;
   - offerte sulle notti vuote a partire dal calendario.
   - **No** all'ottimizzazione dell'orario di invio e a modelli predittivi finché non ci sono dati sufficienti. La profilazione richiede un consenso specifico ⚖.
5. **Niente HubSpot o Customer.io** come base: costo per tenant fuori scala (HubSpot Pro $800/mese [NV]; Customer.io con workspace illimitati $1.000/mese).

---

### 3.3 Portale proprietari con statistiche e fatturazione (solo B2B)

**Cosa c'è oggi**
- KPI (`DashboardController.cs:36-66`).
- Report fiscali CSV/PDF (`FiscalController.cs:150-223`).
- Accesso per proprietà dei membri dell'organizzazione (`PropertyMemberAccess`).
- SDI non configurato (`UnconfiguredSdiEInvoiceProvider` lancia un'eccezione).
- Stripe Connect con un account per organizzazione, senza bonifici ai proprietari.

**Standard di mercato** (Guesty, Hostaway, Lodgify, Hostfully, Octorate):
- login del proprietario limitato ai suoi immobili;
- calendario con blocchi per uso personale;
- rendiconto mensile in PDF (ricavi, commissione, spese con ricevute, netto);
- occupazione e ADR.

Fonti:
- <https://www.hostaway.com/features/owner-portal/>
- <https://help.lodgify.com/hc/en-us/articles/26808844059932-Owner-Statements>
- <https://www.octorate.com/owner-portal/>

**Fatturazione: vincoli**
- **IT:**
  - fattura della commissione al proprietario in FatturaPA via SDI;
  - l'intermediario che **incassa** i canoni trattiene il **21%** a titolo d'acconto, lo versa con F24 (codice 1919) e lo certifica nella CU (art. 4 DL 50/2017; guida AdE aprile 2026: <https://www.agenziaentrate.gov.it/portale/documents/d/guest/locazioni_brevi_disciplina_fiscale_e_regole_per_intermediari_aprile-2026>).
  - **Quokka Sagl non è residente in Italia** e senza stabile organizzazione serve un **rappresentante fiscale** ⚖ (provv. AdE 12/7/2017).
- **IT, novità 2026 (L. 199/2025, art. 1 c. 17):** il regime delle locazioni brevi vale **solo fino a 2 appartamenti**; oltre, l'attività si presume d'impresa. Questo cambia lo status fiscale di molti proprietari e quindi i documenti del rendiconto ⚖.
- **CH:**
  - QR-bill obbligatoria; dal 21/11/2025 la versione IG v2.3 richiede indirizzi strutturati (<https://www.six-group.com/dam/download/banking-services/standardization/qr-bill/ig-qr-bill-v2.3-en.pdf>);
  - IVA alloggio al 3,8% (<https://www.estv.admin.ch/en/vat-rates-switzerland>).

**Raccomandazione**
1. **Build** dentro CasaZen:
   - ruolo `PropertyOwner` limitato alle proprie proprietà;
   - rendiconto mensile (entrate per prenotazione, commissione configurabile per proprietario o proprietà, spese con allegati, ritenute, netto) **nella valuta della proprietà**;
   - PDF + area documenti;
   - blocchi per uso personale.
2. **IT, invio SDI tramite provider** dietro un'interfaccia `IEInvoiceProvider` (esiste già il punto di estensione):
   - **Invoicetronic**: prepagato da €0,10 a €0,02 per transazione, SDK C#, sandbox gratuita (<https://invoicetronic.com/en/pricing/>);
   - **Openapi**: da €0,07 a €0,022 per invio, conservazione a norma opzionale (<https://console.openapi.com/apis/sdi/pricing>).
   - **Non** accreditarsi direttamente a SDICoop.
3. **CH, QR-bill nativa** con **Codecrete.SwissQRBill** (licenza MIT, .NET, supporta la specifica 2.3: <https://www.nuget.org/packages/Codecrete.SwissQRBill.Core/>).
   - Sincronizzazione con bexio (OAuth2, <https://docs.bexio.com/>) solo se il cliente la usa.
4. **Pagamenti ai proprietari:**
   - v1 con esportazione **ISO 20022 pain.001** (SEPA EUR + CHF) per la banca del gestore;
   - **non** Stripe Connect: la maggior parte dei soldi arriva dai payout OTA fuori da Stripe, e le ricariche della piattaforma in UE sono in preview privata, con la Svizzera non elencata (<https://docs.stripe.com/connect/top-ups>).
   - La gestione di fondi di terzi va inquadrata contrattualmente ⚖.

---

### 3.4 Fornitori e dipendenti propri senza commissioni CasaZen (solo B2B)

**Cosa c'è oggi**
- La commissione si applica solo se `PaymentMode=Online`, con il flag `SupplierOnlinePayments` spento e il 10% indicato come provvisorio (`SupplierPaymentsOptions.cs:33-37`).
- **La modalità `Manual`, quella di default, non applica già nessuna commissione.**
- Esiste un override per singolo fornitore (`SupplierCommission.cs`).
- Mancano:
  - la rubrica "fornitori miei" (`OrgTrustedSupplier` non esiste: `GlobalSearchService.cs:29,344`);
  - l'invito di un fornitore da parte dell'host (oggi solo da admin o in autoregistrazione nei comuni pilota);
  - un ruolo staff interno.

**Raccomandazione** (la richiesta meno costosa e quella col miglior rapporto valore/sforzo)
1. `OrgSupplier`: rubrica dell'organizzazione.
   - Il fornitore può essere una persona esterna **senza account** (solo contatto) oppure un'organizzazione fornitore invitata.
   - Nessun vincolo per comune.
2. Ruolo `Staff` in `OrgRole`, con sottotipi pulizie/manutenzione/check-in.
   - Assegnazione dei `ServiceRequest` a un membro staff.
   - Vista "le mie attività" su mobile.
3. Regola commerciale nel codice: per i piani B2B la commissione è 0% su fornitori e staff propri. Il fornitore del marketplace resta disponibile, con commissione solo se pagato online.
4. **Impatto sul business:** rinunciare al take-rate per i B2B va compensato con il canone per unità (§4). Oggi il take-rate è comunque inattivo.

---

### 3.5 Fiscalità completa Italia + Svizzera con integrazioni

**Italia**

| Adempimento | Oggi in CasaZen | Integrazione possibile (fonte ufficiale) | Raccomandazione | Taglia |
|---|---|---|---|---|
| Alloggiati Web (art. 109 TULPS, entro 24h) | Solo file per caricamento manuale (`AlloggiatiRecordFile.cs`); credenziali Questura salvate ma non usate | **Web service SOAP ufficiale** `alloggiatiweb.poliziadistato.it/service/service.asmx`: `GenerateToken` (utente + password + WsKey), `Test`/`Send`, `Ricevuta`, `Tabella`, `GestioneAppartamenti_*` | **Build**: integrazione stabile, nazionale e gratuita | S–M |
| ISTAT flussi turistici | Bloccato (`docs/runbooks/ross1000.md`) | Nessuno standard nazionale. **Ross1000** ha un web service (`/ws/checkinV2`) e l'import XML. È adottato da molte regioni (Lazio da maggio 2025); le altre regioni hanno sistemi propri [NV per regione] | **Build** su Ross1000 per le regioni del cliente (Lombardia, Liguria, Calabria, Marche, Sicilia da verificare) | M |
| Imposta di soggiorno: dichiarazione annuale | Motore di calcolo reale; aliquote **non verificate alla fonte** (`specs/README.md`) | File a tracciato fisso di 1.900 caratteri, inviato via **Entratel/Fisconline** (nessuna REST). Scadenza 30/6 (<https://www.finanze.gov.it/it/archivi/notizie/dettaglio-notizie/Dichiarazione-telematica-imposta-di-soggiorno/>) | **Build: generare il file**, inviato dal commercialista | M |
| Imposta di soggiorno: versamento e comunicazioni mensili | Report per comune/mese | Portali comunali o pagoPA; dove adottato, PayTourist ha una REST API | Caso per caso sui comuni del cliente | M (per comune) |
| Ritenuta 21% intermediari + dati CU/770 | Nessuno | CU/770 via file Entratel, nessuna REST. F24 codice 1919 entro il 16 | **Build**: registro delle ritenute + esportazione dati CU/F24 per il commercialista. **Non diventare intermediario Entratel** | M |
| SDI | Non configurato | Provider API (vedi 3.3) | **Buy** | S |
| CIN / BDSR | Validazione del formato | Nessuna API pubblica di verifica trovata | Mantenere `CinFormat.cs` | — |
| Mod. 21 conto giudiziale | — | Cass. SU ord. 1527/2026: il gestore non è più agente contabile [NV] | Nessuna azione | — |

**Svizzera** (la Confederazione fissa i minimi, i cantoni e i comuni l'esecuzione)

| Adempimento | Autorità | Integrazione | Raccomandazione |
|---|---|---|---|
| Notifica ospiti (art. 16 LStrI + art. 18 OASA); **Ticino**: tutti gli ospiti entro 24h, **solo elettronica** | Polizia cantonale TI | Piattaforma `notifiche.ti.ch`; API non documentata pubblicamente [NV]. Unità piccole: registrazione preventiva presso Ticino Turismo (<https://www4.ti.ch/di/pol-new/servizi-online/notifiche-degli-ospiti-alla-polizia-no-lear>) | Chiedere alla Polizia cantonale se esiste un'interfaccia per i gestionali; in alternativa Chekin (Ticino "coming soon") |
| Notifica / tassa di soggiorno nei **Grigioni** (Laax, Davos, Engadina) e a **Uri** (Andermatt) | Destinazioni / comuni | Sistemi di destinazione (feratel Deskline, AVS, …); nessun sistema cantonale trovato [NV] | Via partner (Chekin: feratel e AVS attivi) o accordo per singola destinazione |
| Riforma federale | SEM | Consultazione 26/8–27/11/2026 per la notifica elettronica senza firma dell'ospite, con soluzione nazionale EasyGov (~2028 [NV]) (<https://www.admin.ch/de/newnsb/STwblroLhJ4->) | Seguire la riforma; non investire troppo nei singoli cantoni prima |
| Statistica BFS | BFS | HESTA riguarda alberghi; **PASTA** (case vacanza) è a campione e trimestrale, via eSurvey (<https://www.bfs.admin.ch/bfs/de/home/statistiken/tourismus/beherbergung/parahotellerie.html>) | Solo un'esportazione, se il cliente viene estratto nel campione |
| IVA | AFC/ESTV | 3,8% alloggio (scade a fine 2027, proposta di proroga); iscrizione obbligatoria da CHF 100k | Aliquota configurabile per paese |

**Raccomandazione**
1. **Italia = build sul nucleo di compliance che è già nostro:**
   - Alloggiati via SOAP;
   - Ross1000;
   - generazione del file della dichiarazione imposta;
   - registro delle ritenute + dati CU/F24.
2. **Gli invii Entratel restano al commercialista:** CasaZen produce file validati, non li trasmette.
3. **Svizzera = buy/partner**, con conferma scritta della copertura per Ticino, Grigioni e Uri:
   - **Chekin** con REST v3, €3,95–7,95 per proprietà al mese (<https://chekin.com/it/prezzo/>, <https://chekin.com/en/legal-compliance/switzerland/>);
   - oppure interfacce partner per singola destinazione.
4. **Da dire al cliente:** "integrazioni funzionanti con tutti gli enti" non è un impegno sostenibile. Entratel non ha API, diversi cantoni e comuni non ne hanno, e la normativa svizzera cambierà intorno al 2027–28.

---

### 3.6 Routine con AI, check-in/check-out (opzionale)

**Cosa c'è oggi**
- Check-in online tramite link con token e dati per ospite; **nessuna acquisizione foto del documento o OCR**.
- Wizard di check-out con richiesta di pulizia.
- Alert sui soggiorni.
- Spec serrature `spec-smart-devices-access.md` (Seam, stato *planned*, #464).

**Vincoli ufficiali**
- **Italia:**
  - la circolare del Viminale del 18/11/2024 (identificazione de visu) era stata annullata dal TAR Lazio (10210/2025);
  - il **Consiglio di Stato, Sez. III, n. 9101 del 21/11/2025** ha riformato il TAR: il controllo visivo è indispensabile. Secondo diverse letture è ammesso un **collegamento video in tempo reale** che accerti la corrispondenza tra ospite e documento.
  - **Keybox o codice inviato automaticamente dopo il solo caricamento del documento = non conforme.**
  - Fonti: <https://www.lapresse.it/economia/2025/11/21/bb-e-non-solo-torna-lobbligo-di-identificare-lospite-di-persona/>, <https://www.qualitytravel.it/self-check-in-consiglio-di-stato-riconoscimento-sempre-de-visu-ma-anche-con-la-tecnologia/178822>. Fonti secondarie: **leggere il testo della sentenza** ⚖.
  - Impatto diretto sul cliente: oggi usa keybox Masterlock [NV per le unità italiane].
- **Ticino:** la pagina della Polizia cantonale indica che il documento "non può essere fotocopiato" [NV]. Se confermato, conservare foto dei documenti per gli ospiti in Ticino è un problema ⚖.

**Raccomandazione**
1. **Motore di routine (build):**
   - regole "evento prenotazione → attività" (pulizia al check-out, controllo pre-arrivo, manutenzione ricorrente) su Hangfire;
   - checklist a template;
   - foto con orario da app mobile;
   - segnalazione → ordine di lavoro → spesa del proprietario, che confluisce nel rendiconto (§3.3).
   - Il caso d'uso coincide con §3.4 (staff proprio).
2. **AI utile e misurabile:**
   - confronto delle foto di fine pulizia con le foto di riferimento;
   - sintesi delle segnalazioni;
   - traduzione delle istruzioni per lo staff;
   - pianificazione suggerita (mai automatica).
   - **Niente** ottimizzazione autonoma dei turni nella v1.
3. **Serrature:** implementare la spec esistente.
   - Seam come aggregatore, oppure Nuki diretto (Smart Hosting a circa €69/serratura/anno [NV]) / igloohome (API $2 per serratura attiva al mese, codici offline: <https://www.igloohome.co/pricing>).
   - **In Italia il codice viene rilasciato solo dopo il riconoscimento de visu (o video) registrato.**
4. **Check-in con video:** una sessione video con operatore, registrata nel log, prima del rilascio del codice. Taglia L. Da confermare legalmente prima di costruirla ⚖.

---

## 4. Costi ricorrenti stimati per i componenti "buy" (prezzi di listino al 2026-10-14)

N = unità gestite (da confermare). Prezzi IVA esclusa.

| Componente | Formula | N = 100 | N = 250 |
|---|---|---|---|
| Channex (disponibilità/tariffe/prenotazioni + messaggi/recensioni) | $130 + $1,00 × N | $230/mese ($2,30/unità) | $380/mese ($1,52/unità) |
| NextPax (fatturazione mensile) | min €350 (50 unità incluse) + €3,50 × (N − 50) | €525/mese (€5,25/unità) | €1.050/mese (€4,20/unità) |
| Chekin (solo se serve per la Svizzera) | €3,95–7,95 × N + commissioni per transazione | €395–795/mese | €988–1.988/mese |
| igloohome API | $2 × serrature attive | $200/mese | $500/mese |
| SDI (Invoicetronic) | ~1 fattura/proprietario/mese | < €100/anno | ~€275/anno (pacchetto 5k) |
| Resend (marketing) | Pro $20 → Scale $90 | $20–90/mese | $90/mese |
| WhatsApp marketing IT | ~€0,066/messaggio [NV, listino Meta da verificare] | a consumo | a consumo |
| LLM (bozze messaggi, AI routine) | Dipende dal provider e dai volumi | da stimare con un prototipo | da stimare |

I costi pass-through vanno inclusi nel listino B2B per unità. Non vanno assorbiti.

---

## 5. Roadmap proposta a fasi (da validare con PO e team)

| Fase | Contenuto | Valore per il cliente | Dipendenze |
|---|---|---|---|
| **0. Fondamenta** | Decisione PO (§8); paese/valuta/fuso/lingua DE; ruoli `PropertyOwner` e `Staff`; ADR "channel manager"; ADR "provider AI per dati personali" | Nessuno visibile, ma sblocca tutto | — |
| **1. Valore rapido** | §3.4 fornitori/staff propri; Alloggiati via SOAP; portale proprietari v1 (KPI + rendiconto PDF); motore routine v1 | Sostituisce subito parti di Kross; compliance italiana | Fase 0 |
| **2. Distribuzione e soldi** | Channel manager (disponibilità/tariffe/prenotazioni + inbox) + AI in bozza; fatturazione SDI + QR-bill; esportazione pain.001; Ross1000; file della dichiarazione imposta e dati CU/F24 | Copre le richieste 1, 3 e 5 (IT) | Scelta del channel manager (due diligence) |
| **3. Crescita** | Svizzera via partner; CRM leggero + campagne email; serrature + check-in video; contenuti annunci (in base al fornitore); WhatsApp | Copre le richieste 2, 5 (CH) e 6 | Parere legale; copertura cantonale scritta |

**Approccio consigliato:** proporre al cliente un **pilota** su un sottoinsieme di unità (es. Ticino + Como) prima della migrazione completa da Kross.

---

## 6. Cosa NON promettere (esclusioni da mettere per iscritto)

- Connessione diretta Airbnb/Booking.com come "partner" (oggi chiusa).
- Creazione/modifica degli annunci su tutte le OTA, se si sceglie Channex (roadmap 2027 non garantita).
- Campagne marketing verso ospiti arrivati da Airbnb.
- Invio diretto CasaZen → Entratel (CU, 770, dichiarazione imposta di soggiorno, DAC7).
- "Integrazione con tutti i cantoni e comuni": solo quelli con interfaccia disponibile, elencati per nome.
- Self check-in in Italia senza riconoscimento de visu o video.
- Consulenza fiscale: rappresentante fiscale, IVA sulle commissioni, status d'impresa dei proprietari restano al commercialista o fiduciario del cliente.

---

## 7. Domande per il prossimo incontro

1. **Numero di unità per paese e per cantone/regione**; previsione di crescita a 12 mesi; ruolo dei Quokka Point (organizzazioni separate?).
2. **Cosa non funziona in Kross Booking?** Quali delle 6 richieste sono "mancanti" e quali "da migliorare"? Durata del contratto Kross e piano di migrazione dei dati.
3. **Creazione annunci:** quanti nuovi annunci al mese? Serve davvero dal giorno 1 o basta disponibilità/tariffe/prenotazioni + messaggi?
4. **Flusso del denaro:** i payout OTA arrivano a Quokka Sagl o ai proprietari? Chi incassa determina la ritenuta del 21%. Esiste già un rappresentante fiscale in Italia?
5. **Fiscalità:** chi è il commercialista (IT) e il fiduciario (CH)? Quali software usano (Fatture in Cloud, bexio, Abacus)? Per loro "integrazione con AdE" significa file pronti o invio diretto?
6. **Staff:** quanti dipendenti e quanti fornitori esterni? Lingue dello staff? Smartphone aziendali?
7. **Check-in:** come identificano oggi gli ospiti nelle unità italiane? Sono disponibili a un check-in con video?
8. **CRM:** che quota di prenotazioni dirette hanno? Raccolgono già consensi marketing?
9. **Budget, tempi, disponibilità a fare da design partner** (pilota a prezzo agevolato in cambio di feedback e referenza).

---

## 8. Decisioni PO necessarie prima di un'offerta

| ID | Decisione | Note |
|---|---|---|
| Q1 | Aprire il segmento "property manager B2B multi-paese (IT + CH)" | Annulla in parte D10 e i freeze di US-011/012/016/017 |
| Q2 | Scelta del channel manager (Channex o NextPax/RU) | Dipende dalla risposta del cliente alla domanda 3 (§7) |
| Q3 | Listino B2B per unità/mese e rinuncia al take-rate per fornitori e staff propri | Coerente con §3.4 e §4 |
| Q4 | Provider LLM per dati personali degli ospiti (DPA UE, nessun addestramento sui dati) | Sostituisce DeepSeek per questi usi |
| Q5 | Budget per i pareri legali/fiscali ⚖ (self check-in, rappresentante fiscale, DMA, profilazione, Ticino) | Prima della firma |

---

## Fonti principali (oltre a quelle citate inline)

- **OTA:**
  - <https://developers.booking.com/connectivity/docs>
  - <https://www.airbnb.com/help/article/2908>
  - <https://docs.channex.io/api-v.1-documentation/messages-collection.md>
  - <https://nextpax.com/products/supply-api>
  - <https://docs.rentalsunited.com/hc/en-gb/articles/35380100616605-Adding-new-listing-to-Airbnb>
- **Fiscale IT:**
  - <https://alloggiatiweb.poliziadistato.it/PortaleAlloggiati/>
  - <https://www.regione.lazio.it/cittadini/turismo/comunicazione-flussi-turistici-e-cir>
  - <https://www.agenziaentrate.gov.it/portale/le-locazioni-brevi-e-la-cedolare-secca>
  - <https://www.fatturapa.gov.it/it/comefare/operatori-economici/inviare-la-fatturapa/>
  - L. 199/2025 c. 17: <https://www.ecnews.it/fiscale/fisco-e-patrimonio/imposte-sul-reddito/novita-2026-per-le-locazioni-brevi/>
- **Svizzera:**
  - <https://m3.ti.ch/CAN/RLeggi/public/index.php/raccolta-leggi/pdfatto/atto/10301>
  - <https://www.estv.admin.ch/en/vat-rates-switzerland>
  - <https://www.edoeb.admin.ch/de/werbung-marketing>
- **Privacy/marketing:**
  - <https://www.garanteprivacy.it/home/docweb/-/docweb-display/docweb/1490553>
  - <https://developers.facebook.com/docs/whatsapp/overview/getting-opt-in>
- **Operazioni:**
  - <https://www.breezeway.io/pricing>
  - <https://developer.nuki.io/t/nuki-web-api-updates-version-v-1-5-0/21795>
  - <https://developer.saltosystems.com/ks/connect-api/>
