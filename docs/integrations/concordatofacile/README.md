# CasaZen e ConcordatoFacile — il processo (documento unico di riferimento)

> **Questo è l'UNICO riferimento per il processo ConcordatoFacile (CF).** Con il documento dei requisiti [`Sessions/business-analysis-canone-concordato.md`](../../../Sessions/business-analysis-canone-concordato.md) (che dice che cosa deve valere e come lo si accetta) è tutta la documentazione di riferimento sul tema. Ogni altro file che parla di CF è, nella tabella del §15, **corretto** (e allora rimanda qui), **SUPERATO** (storico: non usarlo come riferimento) oppure **eliminato**. Se un altro file dice cose diverse da questo documento, ha torto l'altro file.

Revisione 11 · 25 ottobre 2026 (riscrittura completa della revisione 10 del 23 ottobre). **Fonti:** il codice di `develop` al 25 ottobre (backend e frontend, compreso il transitorio del task TR), la specifica di CF «ConcordatoFacile — specifica endpoint di stato v0.2», ottobre 2026, bozza per commenti (d'ora in poi *la v0.2*; il PDF non è nel repository) e le decisioni dell'utente fino al 25 ottobre. La v0.2 governa il link firmato e l'API di stato. Della «specifica tecnica v1.3» di CasaZen (13 ottobre, cartella `da-inviare/`) valgono soltanto il §2 (vettore di prova della firma), le convenzioni che la v0.2 richiama (JSON in UTF-8 con campi `camelCase`, date ISO 8601 UTC con `Z`, errori `application/problem+json` con `code` in `snake_case`) e il punto R24 (inquadramento privacy).

**Stato in una riga.** Il servizio è costruito in `develop` ed è spento di default; in produzione non c'è alcun codice CF (`main` è la v2.1.2 del 23 ottobre); manca il collaudo con CF. Quanto manca o non torna è nel §13.

## 1. Scopo e attori

**Scopo.** Il proprietario (locatore) di un immobile a lungo termine fa il contratto a canone concordato (3+2) con ConcordatoFacile: CF lo prepara sul modello ufficiale, lo fa firmare, ottiene l'attestazione dall'associazione e lo registra. CasaZen vende il servizio, apre il modulo di CF con un link firmato, mostra lo stato della pratica e conserva nel contratto i documenti finali.

| Attore | Che cosa fa |
|---|---|
| Utente (locatore) | Sceglie il tipo di contratto, paga, compila il modulo di CF, gestisce con CF (per e-mail) integrazioni, revisione e firma, carica in CasaZen i documenti finali |
| CasaZen (backend e frontend) | Bozza del contratto con il suo `leaseId`, pagina del servizio, pagamento (Stripe), pratica omaggio, link firmato, lettura dello stato, pagina di ritorno, conservazione e lettura dei documenti |
| CF | Modulo `https://app.concordatofacile.it/`, API di stato `https://stato.concordatofacile.it`, e-mail all'utente con il pulsante «Carica i dati in CasaZen»; con l'associazione cura attestazione e registrazione |
| Stripe | Incassa il pagamento dell'utente a CasaZen (conto della piattaforma) |
| Amministratore di piattaforma CasaZen | Accede al servizio senza pagare; assegna a mano la pratica omaggio |
| Prodotto (PO) | Decide interruttore, collaudo con CF, accordi economici e privacy |

**Chi paga chi.** L'utente paga a **CasaZen 119,00 € IVA compresa per pratica** (con il ricarico di CasaZen incluso) e **non paga CF**: CasaZen paga CF pratica per pratica dopo l'invio dei documenti firmati (CF ha indicato 69 e 89 € senza IVA; importo e riconciliazione sono da confermare, §12). Registro e bollo, se dovuti, sono esclusi dal prezzo.

## 2. L'interruttore e lo stato di ogni ambiente

**Interruttore.** Tutto il servizio sta dietro il flag del backend `Features:ConcordatoFacile` (variabile `Features__ConcordatoFacile`), voluto dall'utente il 25 ottobre ([`feature-flags.md`](../../runbooks/feature-flags.md)). È **spento di default**. Il frontend lo legge da `GET /api/public/features` (chiave `concordatoFacile`).

| Interruttore | Che cosa succede |
|---|---|
| Spento | Rispondono **404**, prima dell'autenticazione, tutte le azioni di `ConcordatoFacileController` (`POST api/leases/{id}/concordatofacile/checkout`, `GET …/access`, `POST …/consume-gift`, `POST …/link`, `GET …/status`), il lookup `GET api/concordatofacile/practices/{practiceId}/lease` e `POST api/leases/drafts/concordatofacile`. L'app non mostra il pannello nella scheda del contratto, la rotta `/app/long-rent/leases/:id/concordatofacile` torna al contratto, la carta «Parto da zero» del nuovo contratto è chiusa («Disponibile a breve») e la pagina di caricamento aperta dal pulsante di CF ripiega sulla scelta manuale del contratto |
| Acceso | Tutto il flusso del §3 |
| Mai dietro l'interruttore | I webhook Stripe (un pagamento o un rimborso già avvenuto viene registrato comunque), il client di stato, il regalo assegnato da un abbonamento a 24 mesi, il controller admin (`api/admin/orgs/{orgId}/concordatofacile/grant-gift`) e il **validatore di avvio** delle opzioni `ConcordatoFacile`: richiede `StripePriceId`, `BiennialPriceIds` e `LinkSecret` in Production **e in ogni ambiente che non sia Development o Testing** (quindi anche sul test Staging di Railway), anche con il servizio spento. Per questo il primo rilascio in produzione con questo codice le richiede già impostate |

**Attivazione.** Test: acceso. Produzione: l'interruttore resta **non impostato** finché (a) CasaZen ha confermato a CF il collaudo della pagina di ritorno con `?practice=` e della firma dei link, (b) è concordato con CF l'inquadramento privacy (R24, §12), (c) le variabili del §7 e le due destinazioni webhook del §8 sono a posto e (d) il codice CF è arrivato in produzione con una release (develop→main, solo con il via libera esplicito dell'utente). Si accende impostando `Features__ConcordatoFacile=true` su Railway e rifacendo il deploy; si spegne togliendo la variabile.

**Stato per ambiente (25 ottobre).**

| | Test (Railway `test`, app web Vercel su `develop`) | Produzione (Railway `production`, `https://www.casazen.space`) |
|---|---|---|
| Codice CF | Quello di `develop` (deploy automatico) | **Nessuno**: `main` è la v2.1.2 del 23 ottobre |
| Interruttore | `true` | non impostato (spento) |
| Variabili `ConcordatoFacile__*` | Impostate il 24 ottobre, **tranne `StatusApiKey`** (la deve consegnare CF); `LinkSecret` = segreto pubblico del vettore di prova (dalle 23:09Z del 24 ottobre) | Price live e `BaseUrl` impostati il 24 ottobre; `LinkSecret`: quello vero lo genera CF per la produzione (un valore provvisorio basta ad avviare l'API ma non apre il modulo di produzione di CF); `StatusApiKey` assente |
| Webhook Stripe | Due destinazioni (§8) | Due destinazioni (§8) |
| Con CF | Firma verificata da CF sui tre link di esempio (23 ottobre); mancano la chiave di stato di prova e il collaudo | Il pulsante «Carica i dati in CasaZen» è spento da CF |

Lo stato di Railway, Stripe e Vercel è quello dichiarato dal coordinatore della wave il 25 ottobre: questo documento non l'ha riverificato (il codice non lo può provare).

## 3. Il flusso, passo per passo

### 3.1 «Nuovo contratto»: prima il tipo, poi il come

La pagina `/app/long-rent/leases/new` fa due domande, una per schermata. Le risposte restano nell'indirizzo, così «Indietro» (link e browser) torna alla domanda prima: `?tipo=concordato|libero|transitorio` e `?come=casazen|documenti|zero`; restano valide `?tipo=altro` (= libero), `?origine=importato` (= `come=documenti`) e `?propertyId=` (preseleziona l'immobile).

| Tipo | Seconda domanda |
|---|---|
| Canone concordato (3+2) | «Hai già il contratto e i documenti?»: **«Ho già il contratto e i documenti»** (ingresso A, §3.2) oppure **«Parto da zero»** (ingresso B, §3.3, aperto solo con l'interruttore acceso). Il concordato **non si prepara mai con il wizard di CasaZen**: lo prepara CF |
| Canone libero (4+4) | «Come vuoi procedere?»: «Lo preparo con CasaZen» (wizard) oppure «Ho già i documenti» |
| Transitorio (da 1 a 18 mesi) | Stessa domanda del libero; «Lo preparo con CasaZen» è aperta solo se il server lo dice (`availableTypes` di `GET /api/long-rent/rules`). Vedi §3.9 |

### 3.2 Ingresso A: «Ho già il contratto e i documenti»

L'utente sceglie l'immobile (preselezionato da `?propertyId=`) e carica il contratto come PDF o come foto delle pagine (trascina e rilascia, più file; le foto diventano un solo PDF nel browser). CasaZen crea la bozza di origine «Importato» con il tipo scelto, carica il PDF come documento `Contract` e lo legge sul server (§3.8): il wizard si apre sul primo passo in cui manca qualcosa, con i valori letti nei campi (modificabili, con il segno «Letto dal contratto — controlla»), e chiede **solo ciò che manca**. L'importazione finale usa il PDF già caricato (`POST /api/leases/{id}/import` con `contractDocumentId`) e il giorno della firma letto. Questo ingresso **non passa dal pagamento né dal modulo di CF**: il contratto esiste già. Se la lettura fallisce, o l'utente non ha il file, si continua a mano. Dettagli: [`long-rent-lease-draft.md`](../../runbooks/long-rent-lease-draft.md) § 16 e [`long-rent-lease-import.md`](../../runbooks/long-rent-lease-import.md) § 11.

### 3.3 Ingresso B: «Parto da zero» (concordato con CF)

1. L'utente sceglie l'immobile. CasaZen salva subito una bozza vera (`POST /api/leases/drafts`, poi `PATCH …/draft` con `contractType: Concordato`): il contratto ha il suo **`leaseId`** senza chiedere APE, parti, canone o date. Un doppio clic o un nuovo tentativo riusano la stessa bozza; un nuovo contratto sulla stessa casa è una nuova visita della pagina e ha un `leaseId` nuovo. (La rotta `POST api/leases/drafts/concordatofacile`, che marca la bozza `IsConcordatoFacileSource`, esiste ma l'app non la usa: debito CF-FIX-FU5.)
2. L'app va (sostituendo la voce di cronologia) alla pagina del servizio `/app/long-rent/leases/:id/concordatofacile`: pagamento o pratica omaggio (§3.4), poi il modulo di CF con il link firmato (§3.5).
3. La scheda `/app/long-rent/leases/:id` della bozza mostra l'avviso «Bozza — in attesa del contratto del partner» con un solo pulsante, «Procedi con la creazione del contratto», che porta alla pagina del servizio (con l'interruttore spento è disabilitato, con la riga «in arrivo»). La bozza non genera occupazione, incassi o adempimenti di un contratto attivo.
4. Quando CF consegna i documenti, l'utente li carica nella sezione Documenti del contratto (§3.7): il `leaseId` non cambia. La bozza «da zero» nasce con origine `Created`, mentre l'importazione di un contratto già firmato (`POST /api/leases/{id}/import`) ammette solo bozze di origine `Imported` (422 `lease_import_not_imported_origin`): oggi la bozza «da zero» resta quindi una bozza con i suoi documenti e non diventa da sola un contratto «Firmato». Come collegare i due percorsi è una decisione aperta del PO (LR-06a-FU4, CF-DOC-FU10, §13).

La bozza è protetta dalla pulizia delle bozze abbandonate (`lease-draft-retention`, 180 giorni): le chiavi esterne `Restrict` di acquisti e omaggi impediscono di cancellare un contratto che ne ha uno, e `LeaseDraftDeleter` protegge in più le bozze marcate CF con un acquisto non fallito.

### 3.4 La pagina del servizio: pagamento, omaggio, accesso admin

Il diritto lo decide **il server** (`GET …/access`: `hasAccess`, `source` = `payment`/`gift`/`admin`, `giftAvailable`, `giftConsumed`); il ritorno `?checkout=success` da Stripe non è una prova.

| Condizione verificata dal server | Che cosa vede e fa l'utente |
|---|---|
| Nessun diritto | Contratto, prezzo «119,00 EUR totali IVA compresa» e i cinque passi del processo (paga o usa l'omaggio; compila il modulo; coordina via e-mail con CF; ricevi l'e-mail finale con i documenti; carica i documenti in CasaZen). «Paga 119 euro e continua» → Stripe Checkout (`POST …/checkout`) → ritorno a `…/concordatofacile?checkout=success` o `=cancelled` («Il pagamento è stato annullato», «Riprova») |
| Omaggio disponibile (`giftAvailable` e non consumato) | «Usa la pratica omaggio e continua» (`POST …/consume-gift`) al posto del pagamento |
| Pagato, o omaggio già consumato per questo contratto | «Accedi alla tua pratica» e sotto lo stato della pratica (§3.6) |
| Pagamento già fatto ma non ancora registrato, o secondo clic su «Paga» | 409 `concordatofacile_already_purchased`: «Hai già accesso al servizio ConcordatoFacile per questo contratto», nessuna nuova sessione Stripe |
| Amministratore di piattaforma (ruolo `Admin`) | «Accesso amministratore attivo»: accede senza pagare e senza consumare omaggi. L'amministratore di una normale organizzazione non ha questo accesso |

**Pratica omaggio.** Ogni organizzazione con un abbonamento a **24 mesi** riceve **una** pratica omaggio, da usare per un solo contratto (non vale «24 mesi di anzianità»). Si assegna con l'evento `customer.subscription.created` del webhook di piattaforma quando il Price è in `ConcordatoFacile__BiennialPriceIds` (anche con il servizio spento), oppure a mano con `POST api/admin/orgs/{orgId}/concordatofacile/grant-gift` (solo admin di piattaforma; 409 se l'organizzazione ne ha già una; evento di audit `AdminGrantedConcordatoFacileGift`). Il consumo è atomico (lock consultivo di PostgreSQL per organizzazione): rientrare per lo stesso contratto non consuma di nuovo (200); un secondo contratto → 409 `concordatofacile_gift_already_consumed`. I Price a 24 mesi esistono (provvisori) ma la vendita dei piani a 24 mesi non è ancora in `develop` (task BIENNIAL, §13).

**Una pratica per contratto.** Rientrare nel modulo prima dell'invio non richiede un altro acquisto. Una seconda pratica richiede un **nuovo contratto** e un nuovo pagamento (l'omaggio è uno solo), anche dopo una pratica chiusa.

### 3.5 Il modulo di CF e il link firmato

«Accedi alla tua pratica» (`POST …/link`: serve `lease.create` e il diritto del §3.4) genera **sul server, al clic**, il link e l'app lo apre in una nuova scheda (`noopener,noreferrer`; mai in un iframe). Il testo sotto il pulsante avverte che «il modulo riprende solo sullo stesso dispositivo/browser utilizzato»: prima dell'invio, sullo stesso dispositivo e browser, un nuovo link riprende la bozza del modulo; dopo l'invio, o da un altro dispositivo, apre una compilazione **nuova** e quindi, se inviata, una seconda pratica sullo stesso contratto (CF la chiude come doppione). Parametri, firma e segreto: §5.

Dopo l'invio l'utente gestisce con CF, **direttamente e per e-mail**, integrazioni, revisione del contratto e firma; CF gli manda i documenti dopo la firma. CasaZen non riceve, non copia e non inoltra le e-mail di pratica e non ne manda di sue (nessuna e-mail di «pratica ricevuta» o finale da CasaZen). Il servizio per CasaZen è sempre la **pratica completa**, fino alla registrazione all'Agenzia delle Entrate: il modulo non mostra i prezzi di CF né la scelta del servizio.

### 3.6 Dopo l'invio: lo stato della pratica

CasaZen legge lo stato dal proprio backend (§6) e mostra la sezione «Stato pratica ConcordatoFacile»: tutte le pratiche del contratto (numero `P-…`, comune, stato, data dell'ultimo passaggio di CF, data dell'ultimo controllo di CasaZen, motivo di chiusura), l'avviso «Il comune associato dal partner differisce dalla casa del contratto» e, se CF non risponde, l'ultimo stato noto con l'avviso «Non riusciamo a contattare il servizio ConcordatoFacile». I doppioni restano nell'elenco (CF li chiude con il motivo `duplicate`).

| Fase | Stati di CF | Che cosa fa l'utente |
|---|---|---|
| Ricezione e lavorazione | `Received`, `InProgress`, `SignedReceived`, `SentToAssociation` | Aspetta |
| Azione richiesta | `AwaitingUser`, `DraftSent` | Legge e gestisce le e-mail di CF (dati o documenti mancanti; bozza da approvare e firmare) |
| Attestazione | `Attested` | Attestazione rilasciata e inviata all'utente; per CasaZen è un passaggio verso la registrazione, **non** uno stato finale |
| Registrazione | `Registered` | Stato finale positivo |
| Chiusura senza attestazione | `ClosedWithoutAttestation` (`closeReason`: `duplicate`, `withdrawn`, `not_compliant`, `other`) | Stato finale negativo |
| Sospensione | `Suspended` | Pratica ferma (per esempio l'utente non risponde); può riprendere, non è una chiusura |

Gli stati possono alternarsi (`InProgress` e `AwaitingUser`; da `Suspended` si torna allo stato di prima), la data dell'ultimo passaggio non retrocede; non si considera «attestata» uguale a «registrata». Uno stato sconosciuto si mostra in modo neutro, mai come «completata».

**Come è fatto oggi e come deve essere.** Voluto: un solo punto d'ingresso nella scheda del contratto; appena l'elenco di CF mostra una pratica, in qualunque stato, anche chiusa, **sparisce il pulsante** e resta lo stato; se la lettura non riesce e non c'è alcuno snapshot, il pulsante resta con l'avviso «Non riusciamo a verificare lo stato: se hai già inviato il modulo non riaprirlo». Realizzato: il pannello della scheda del contratto (`ConcordatoFacilePanel`) **non legge lo stato** e mostra sempre «Procedi con la creazione del contratto» (senza diritto) o «Accedi alla tua pratica» (con diritto); lo stato sta solo nella pagina del servizio, sotto lo stesso pulsante, che non sparisce; l'avviso per la lettura non riuscita non è usato. È il debito CF-DOC-FU4 (§13).

### 3.7 Il ritorno da CF: «Carica i dati in CasaZen» e `?practice=`

CF manda all'utente, nelle e-mail con i documenti da conservare e nella conferma testuale del modulo, il pulsante «Carica i dati in CasaZen»: apre `https://casazen.space/app/long-rent/documents/upload?practice=P-0123456789abcdef` (numero di pratica: `P-` più 16 caratteri esadecimali). È una pagina web: non trasferisce file e non è l'endpoint di stato. CF ha già impostato il pulsante su questo indirizzo e lo tiene **spento** finché CasaZen non conferma il collaudo (§12). Il parametro è solo `practice`: il `leaseId` non serve nel link.

| Indirizzo | Che cosa fa la pagina |
|---|---|
| `?practice=P-…` valido | Chiede al server `GET /api/concordatofacile/practices/{id}/lease` (una lettura dell'API di stato di CF, senza nuovi tentativi). Pratica collegata a un contratto **dell'organizzazione dell'utente**: mostra i documenti di quel contratto con la riga «Pratica P-… collegata a questo contratto» |
| Pratica non collegata (`leaseId` nullo: `manualSelectionRequired`) | Avviso («La pratica P-… non è ancora collegata a un contratto») e **scelta manuale** fra i propri contratti; si prosegue su `?leaseId=…&practice=…`. Il collegamento nel sistema di CF lo corregge CF a mano: CasaZen non può scrivere a CF e la scelta dell'utente non cambia il `leaseId` della pratica |
| Pratica non trovata, di un'altra organizzazione, lettura non disponibile o servizio spento | «Non troviamo questa pratica tra i tuoi contratti» e scelta manuale; nessun contratto è toccato finché l'utente non sceglie; nessun dettaglio su altri tenant (404 uniforme) |
| `practice` con formato errato o ripetuto | «Collegamento non valido» e scelta manuale, nessuna lettura |
| `?leaseId=<uuid>` (link vecchi) | I documenti di quel contratto; con `leaseId` e `practice` insieme vince il contratto nominato |
| Nessun parametro | Scelta del contratto fra i propri |

Dopo il login percorso e parametri sono preservati. Caricamento e consultazione riusano le API dei documenti del contratto (`GET`/`POST /api/leases/{leaseId}/documents` e download), senza un altro acquisto. Il collegamento di CF **non prova** che i documenti appartengano a quel contratto (l'utente può aver scritto nel modulo i dati di un altro immobile; `comuneDiffersFromLink` coglie solo il comune cambiato): CasaZen non fattura e non consegna nulla in base al solo abbinamento. Il passo esplicito «questo è il contratto giusto» prima del caricamento non c'è ancora (CF-FIX-FU4, §13).

### 3.8 Documenti e dati del contratto

| Raccolta | Contenuto |
|---|---|
| Documenti della casa | APE, planimetria e documenti propri dell'immobile |
| Contratti della casa | Ogni contratto con il proprio `leaseId` |
| Documenti del contratto | Contratto definitivo, attestazione, ricevuta di registrazione e altri allegati di quel `leaseId` (tipi `Contract`, `Attestation`, `Registration`, `Other`) |

Si carica un PDF oppure più foto JPEG/PNG dello stesso documento (fino a 12 foto, 20 MB per foto e 40 MB in ingresso; le foto diventano **un solo PDF** nel browser e gli originali non si conservano; un PDF esistente conserva i propri byte, firme digitali comprese; il PDF di un allegato del contratto è al massimo 20 MB). Storage privato; autorizzazione sul contratto e sul documento: [`lease-documents.md`](../../runbooks/lease-documents.md), [`documenti-multipagina.md`](../../runbooks/documenti-multipagina.md).

**Lettura (NL-DOCS).** Il contratto caricato in una bozza «Importato» si legge **sul server**: PdfPig per il PDF digitale, poi Tesseract (`ita+eng`) per scansioni e foto; nessun servizio esterno e nessun LLM (un contratto contiene nomi, codici fiscali e indirizzi e non c'è un sub-responsabile approvato). Il parser (che ha sostituito la prima pipeline di ADR-009) compila **solo ciò che è vuoto** e che il testo dice senza ambiguità (una persona si legge solo con un codice fiscale valido), non sovrascrive mai, e segnala i conflitti; `ExtractionResult` non contiene valori personali (solo nomi dei campi e stati); i valori vanno solo nella bozza cifrata. Dettagli e limiti: [`lease-document-extraction.md`](../../runbooks/lease-document-extraction.md), non duplicati qui. La lettura completa vale per la bozza «Importato» (§3.2): un documento caricato in una bozza «da zero» (origine `Created`) passa dalla prima pipeline, che scrive nel contratto solo i pochi campi di cui è certa. **Non** si leggono ancora l'attestazione e la ricevuta di registrazione: si archiviano e si consultano, ma i loro dati non popolano le sezioni Attestazione e Registrazione (CF-DOC-FU6).

Caricamento, attestazione e registrazione sono eventi distinti: il solo upload, o una ricevuta di trasmissione, non prova la registrazione, e lo stato `Registered` di CF non significa che la ricevuta sia già archiviata in CasaZen. Un contratto si dichiara registrato in CasaZen con la dichiarazione manuale esistente (`POST /api/leases/{id}/registration/manual`, ricevuta obbligatoria). Nessun secondo invio fiscale automatico.

**APE.** Resta un documento della casa. Il suo controllo (codice, classe e date sull'intero attestato) non è una certificazione di autenticità e non è bloccante: il file si conserva sempre con un esito, e per creare, firmare o registrare un contratto bastano codice e classe scritti dal locatore oppure un APE verificato (PR #569, release v2.1.1); codice e classe si leggono anche dal documento (PR #578, v2.1.2). Non si applica agli allegati del contratto.

### 3.9 Libero 4+4 e transitorio: fuori da CF

Il modulo di CF accetta soltanto contratti a canone concordato: libero e transitorio non passano da CF (CF li segue per e-mail fuori da questo percorso; ha indicato 20 € per la sola registrazione di un contratto già pronto, condizioni da completare). In CasaZen: «Lo preparo con CasaZen» (wizard e modello di contratto) oppure «Ho già i documenti» (stessa lettura del §3.8). Il modello del libero e degli altri regimi è una bozza **non approvata** finché il PO non la approva con le variabili `LeaseTemplates__Variants__*` ([`lease-contract-templates.md`](../../runbooks/lease-contract-templates.md)).

Il transitorio «in CasaZen» è in `develop` dal 25 ottobre (task TR: backend #609, frontend #349): `availableTypes` lo include, il wizard chiede l'esigenza transitoria e il termine (da 1 a 18 mesi) e il modello è l'Allegato B del D.M. 16/01/2017. Il modello è una **bozza non approvata**: «Scarica da firmare» risponde 422 `contract_template_not_approved` e l'anteprima è marcata BOZZA finché il PO non lo approva con `LeaseTemplates__Variants__Transitorio__*`. Debiti TR-FU1..4 al §13.

## 4. Il pagamento in dettaglio (Stripe)

Checkout Session di Stripe in modalità `payment` (non abbonamento), sul **conto della piattaforma** (non Stripe Connect, non i pagamenti degli affitti), con una riga: il Price una tantum configurato (`ConcordatoFacile__StripePriceId`), quantità 1. Il totale atteso è **11900 centesimi, `eur`, IVA già inclusa**: il server non aggiunge IVA e l'esenzione che l'associazione dichiara per le proprie ricevute non si applica alla vendita CasaZen. `SuccessUrl` e `CancelUrl` sono `{ConcordatoFacile__BaseUrl}/app/long-rent/leases/{leaseId}/concordatofacile?checkout=success` e `…?checkout=cancelled`.

| Regola | Come |
|---|---|
| Metadati | `orgId`, `leaseId` e `userId` sulla sessione **e** sul PaymentIntent (`payment_intent_data.metadata`): Stripe non copia i metadati della sessione sul PaymentIntent e il webhook li legge dal PaymentIntent. **Nessuna chiave `kind`**: `StripeWebhookHandler.IsConcordatoFacilePayment` riconosce un pagamento CF dalla presenza di `leaseId` senza `kind` (con `kind` finirebbe agli altri gestori: affitti, prenotazioni, servizi dei fornitori) |
| Chi completa l'acquisto | Il webhook `payment_intent.succeeded` dell'endpoint di piattaforma (`/webhooks/stripe`, §8): solo se importo = 11900 e valuta = `eur` (altrimenti errore nel log e nessun accesso), se l'organizzazione dei metadati è quella dell'acquisto, e una sola volta (l'evento si reclama in `ProcessedStripeEvents` con sorgente `ConcordatoFacile`). L'acquisto passa a `Completed` con `PaidAt` e `StripePaymentIntentId` |
| Prima di creare una sessione (sempre prima di chiamare Stripe) | Contratto inesistente o di un'altra organizzazione: 404 `lease_not_found`. Acquisto già `Completed` o omaggio consumato per il contratto: 409 `concordatofacile_already_purchased`. Acquisto `Pending` con sessione **aperta**: la sessione è riusata. Sessione `complete` e `paid` con il webhook non ancora arrivato: se importo e valuta sono giusti l'acquisto passa subito a `Completed` e la risposta è 409; con importo diverso errore nel log, accesso non concesso, 409. Sessione `complete` ma non pagata (pagamento ritardato): 409, nessuna nuova sessione. Sessione scaduta o sparita: l'acquisto vecchio passa a `Failed` e se ne apre uno nuovo |
| Rimborsi | `charge.refunded` con rimborso **totale**: acquisto `Refunded` (`RefundedAt`); un rimborso parziale lascia lo stato; la pratica e i documenti non si toccano mai. Non c'è una politica commerciale di rimborso: il caso lo gestisce l'operatore dal Dashboard |
| Stati dell'acquisto | `Pending`, `Completed`, `Failed`, `Refunded` |
| Scritture verso terzi | La sessione Stripe nasce solo da un clic dell'utente |

Il diritto di accesso (`ConcordatoFacileAccessService`) è: amministratore di piattaforma, oppure acquisto `Completed` del contratto, oppure omaggio consumato per il contratto. Il codice è in `ConcordatoFacileCheckoutService`, `ConcordatoFacileAccessService` e `ConcordatoFacilePaymentHandler` (backend #588, #590, #592, #602). Il runbook Stripe ha un rimando in [`stripe.md`](../../runbooks/stripe.md).

## 5. Il link firmato in dettaglio

`https://app.concordatofacile.it/?partner=casazen&leaseId=<uuid>&expiresAt=<data>&signature=<hex>&comune=<nome>&provincia=<sigla>`: l'indirizzo del modulo è sempre questo.

| Parametro | Regola |
|---|---|
| `partner` | `casazen` |
| `leaseId` | UUID del contratto, in minuscolo |
| `expiresAt` | UTC, adesso + **15 minuti**, formato `yyyy-MM-ddTHH:mm:ssZ` al secondo, senza frazioni. CF accetta scadenze fino a 60 minuti avanti, tollera 2 minuti di scarto degli orologi e rifiuta offset (`+00:00`), lo spazio al posto della `T` e `t`/`z` minuscole |
| `signature` | HMAC-SHA256 con la chiave `linkSecret` del testo `leaseId + "." + expiresAt` **sui valori decodificati**, UTF-8, in esadecimale minuscolo |
| `comune`, `provincia` | Nome e sigla del comune ISTAT dell'immobile (`Property.ComuneIstatCode` e tabella `Comuni`); senza comune noto: 422 `property_municipality_unknown`. Precompilano la ricerca del modulo e non sono coperti dalla firma |

Dopo aver firmato, ogni valore si codifica nell'URL (`HttpUtility.UrlEncode`: lo spazio diventa `+`, che CF legge come spazio). **CF ignora `comuneIstatCode`, `returnUrl` e `theme`**: non si mandano. Se il comune non è nell'elenco del modulo (oggi Seveso, comune pilota, non c'è; Monza e Reggio nell'Emilia sì) il modulo lo lascia scritto e l'utente prosegue con quel nome: la pratica resta legata al contratto e `comune` arriva nello stato ridotto a lettere, spazi, apostrofi, trattini e parentesi. Se il link manca, non è valido o è scaduto l'utente può compilare lo stesso, ma la pratica nasce **senza `leaseId`** e CF la collega a mano. La firma non rende il modulo privato e non prova il pagamento della singola pratica.

**Segreto.** `ConcordatoFacile__LinkSecret` (la variabile semplice `CONCORDATOFACILE_LINK_SECRET`, se c'è, vince; almeno 32 caratteri) lo **genera CF**, non CasaZen. Per la produzione CF lo manda per e-mail con un Bitwarden Send a una sola apertura, e la password viaggia su un altro canale (CF propone un SMS al numero dal quale scrive CasaZen); mai nel browser, mai in chat, mai nei log. Per la **prova** vale il segreto pubblico del vettore di prova della v1.3 §2 (sotto), che è una fixture e non una credenziale. Il modulo di produzione di CF **rifiuta** il segreto di prova: per questo i link di prova si mandano a CF perché li verifichi (così sono stati verificati, il 23 ottobre, i tre link di esempio del 22 ottobre, prodotti da uno script di riferimento).

```text
linkSecret = test_link_secret_0123456789abcdef0123456789abcdef
message    = 0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11.2026-10-13T10:00:00Z
signature  = fa10ce472c1e3b33939e62e01a7f5f8cec10d45abc02726f691cae37bd639eea
```

La scadenza storica serve al test puro della firma (nel link sarebbe `expiresAt=2026-10-13T10%3A00%3A00Z`); gli altri due vettori verificati da CF sono nei [requisiti](../../../Sessions/business-analysis-canone-concordato.md) (§4).

**Emissione.** `POST …/link` risponde `{ url }` con `Cache-Control: private, no-store`; senza diritto: 403. L'emissione è registrata come evento del contratto (`ConcordatoFacileLinkGenerated`: contratto e ora, **mai** URL o firma) e serve all'audit e alla rilettura breve del §6. CF toglie `leaseId`, `expiresAt` e `signature` dalla barra dell'indirizzo prima di qualunque script di terzi: il `leaseId` non è un'autenticazione dell'utente. Il codice di `ConcordatoFacileLinkService` è conforme alla v0.2 e al vettore (letto riga per riga); il test del vettore (`ConcordatoFacileLinkServiceTests`) però ricalcola la firma nel test e non chiama il servizio, e nessun link prodotto dal server è ancora stato verificato da CF (§12).

## 6. L'API di stato di CF in dettaglio

**Indirizzo e chiave.** `https://stato.concordatofacile.it` è già il default del codice: `ConcordatoFacile__StatusApiUrl` **non si imposta**. Solo HTTPS, TLS 1.2 o superiore. Autenticazione `Authorization: Bearer <chiave>`: una chiave per partner e per ambiente (`cfsp_test_…`, `cfsp_live_…`), di 32 byte casuali, che CF consegna per e-mail con un Bitwarden Send a una sola apertura (password su un altro canale) quando il suo endpoint di prova è pronto; CF ne conserva solo l'impronta SHA-256 e più chiavi possono coesistere per la rotazione. In CasaZen: `ConcordatoFacile__StatusApiKey`. **Senza chiave** l'API di stato è spenta con un avviso nel log (`ConcordatoFacile:StatusApiKey not configured`) e ogni lettura fallisce: la scheda mostra lo stato come non disponibile e la pagina `?practice=` ripiega sulla scelta manuale; pagamento e link firmato non ne risentono. Ogni risposta di CF ha `Cache-Control: no-store`.

| Chiamata | Risposta |
|---|---|
| `GET /v1/leases/{leaseId}/practices` | `{ "leaseId": …, "practices": [ … ] }`: le pratiche nate dal contratto, dalla più recente, di norma una, al massimo 20. Contratto sconosciuto, non di CasaZen o senza pratiche: sempre **200** con `"practices": []` |
| `GET /v1/practices/{practiceId}` | La singola pratica, con in più `leaseId` (**`null`** se non è collegata a un contratto). Pratica inesistente, di un altro partner o dell'altro ambiente: sempre lo stesso 404 `practice_not_found` |

Oggetto pratica: `practiceId` (`P-` più 16 esadecimali, non sequenziale: è il numero che l'utente vede nella conferma e che porta il pulsante), `status`, `statusChangedAt`, `submittedAt`, `comune` (come l'ha scelto l'utente, con il nome dell'indice di CF), `comuneDiffersFromLink` (`true`/`false`, **`null`** se il link non indicava il comune o la pratica non è collegata) e `closeReason` (solo con `ClosedWithoutAttestation`). Nessun dato personale. Errori: 400 `invalid_lease_id`/`invalid_practice_id`, 401 `unauthorized` (con `WWW-Authenticate: Bearer`), 404, 429 `rate_limited` con `Retry-After` in secondi. Campi, stati e codici d'errore sconosciuti si ignorano (CF può aggiungerne senza cambiare versione; un cambiamento incompatibile avrebbe il percorso `/v2`). La pratica compare pochi secondi dopo l'invio con lo stato `Received`; se la chiamata di CF non riesce la ripete ogni 15 minuti.

**Profilo di lettura (accettato da CF, v0.2 §4).**

- L'elenco del contratto si legge quando l'utente apre la scheda, con una copia privata di **15 minuti** condivisa fra utenti e schede della stessa organizzazione: al massimo **4 letture all'ora** per contratto consultato, zero per i contratti non consultati.
- Dopo l'emissione del link, finché la pratica non compare, **una lettura ogni 5 minuti per un'ora** (al massimo 12 all'ora per quel contratto), così il pulsante sparisce poco dopo l'invio.
- La singola pratica (lookup del ritorno `?practice=`) con lo stesso intervallo di 15 minuti; oggi il lookup non è in cache e legge CF a ogni apertura della pagina (in correzione, §13).
- **Nessuna lettura in background**: nessun job, nessun timer fuori dalla pagina aperta. Come ordine di grandezza, 50 contratti consultati senza sosta sono al massimo 200 richieste all'ora. Il limite per chiave lo fissa CF con margine sopra questo profilo e lo rivede sulle pratiche effettive.
- **Non serve un ricevitore di eventi**: CF non manda webhook a CasaZen (la v1.3 li prevedeva; la v0.2 li ha sostituiti con l'API di stato).

**Come è fatto.** `GET /api/leases/{leaseId}/concordatofacile/status` (permesso `lease.read`) risponde `{ practices, lastCheckedAt, nextUpdateAt, stale }` con `Cache-Control: private, no-store`. `ConcordatoFacileStatusCacheService` tiene lo snapshot nella **memoria del processo**, con chiave (ambiente, organizzazione, contratto): un semaforo per contratto accorpa le letture concorrenti; se CF fallisce si tiene l'ultimo snapshot valido, segnato `stale` (un errore non diventa mai «nessuna pratica» né uno stato finale); un risultato con `statusChangedAt` più vecchio di quello già noto non lo sostituisce. Il client (`ConcordatoFacileApiClient`) ritenta 5xx, timeout ed errori di rete 3 volte con attese di 1, 2 e 4 secondi (30 secondi per tentativo, nessun circuit breaker); un 429 con `Retry-After` fino a 30 secondi viene atteso e ritentato una volta, oltre o senza intestazione è un errore. Il frontend (`useCfStatus`) rilegge alla scadenza di `nextUpdateAt`, solo con la pagina aperta e in primo piano. Nel funzionamento normale il profilo è rispettato; tre difetti trovati leggendo il codice contro la v0.2 (forma della risposta dell'elenco, controllo dell'organizzazione, attesa dopo un errore) sono in correzione (§13).

## 7. Configurazione per ambiente

L'elenco completo, con l'effetto di ogni variabile mancante, è in [`deploy-checklist.md`](../../runbooks/deploy-checklist.md) § 2.8; i due file `secrets/railway.{test,prod}.variables.example.json` hanno gli stessi nomi.

| Variabile | Test | Produzione | Chi la dà |
|---|---|---|---|
| `Features__ConcordatoFacile` | `true` | **non impostata** (spento) fino al collaudo con CF e all'accordo sull'inquadramento privacy | CasaZen (PO) |
| `ConcordatoFacile__StripePriceId` | Price una tantum di **119,00 EUR** IVA inclusa, in modalità test | Lo stesso Price, in modalità live | CasaZen (Dashboard Stripe) |
| `ConcordatoFacile__BiennialPriceIds__0..2` | I tre Price dei piani a 24 mesi (Starter, Pro, Scale), **provvisori**, sullo stesso prodotto del piano, in modalità test | Gli stessi in modalità live | CasaZen (Dashboard Stripe) |
| `ConcordatoFacile__LinkSecret` (o la variabile semplice `CONCORDATOFACILE_LINK_SECRET`, che vince) | Il segreto **pubblico** del vettore di prova della v1.3 §2 | Il segreto che **genera CF** per la produzione | CF (Bitwarden Send, password su un altro canale) |
| `ConcordatoFacile__BaseUrl` | Origine dell'app web di prova (senza barra finale): **non** è un indirizzo di CF | `https://www.casazen.space` | CasaZen |
| `ConcordatoFacile__StatusApiKey` | `cfsp_test_…` | `cfsp_live_…` | CF (Bitwarden Send) |
| `ConcordatoFacile__StatusApiUrl` | non impostare | non impostare | — |
| `ConcordatoFacile__StripeApiKey` | facoltativa, di norma non impostata | idem | CasaZen |
| `Stripe__WebhookSecret` e `Stripe__ConnectWebhookSecret` | I due `whsec_…` delle due destinazioni di test | I due `whsec_…` delle due destinazioni live | CasaZen (Dashboard Stripe; mai in chat) |
| `LeaseTemplates__Variants__<Variante>__VersionId`, `__Approved`, `__ApprovalReference`, `__ApprovedAt` | Nessun modello approvato | Idem | PO e legale |

Obbligatorie all'avvio fuori da Development e Testing (quindi anche su Staging): `StripePriceId`, `BiennialPriceIds` (almeno uno) e `LinkSecret` (almeno 32 caratteri), anche con l'interruttore spento. Senza `BaseUrl` il checkout risponde 500 `internal_error` e nulla avvisa all'avvio. Senza `LinkSecret` in Development o Testing (dove il validatore non lo richiede), con l'interruttore acceso, ogni azione di `ConcordatoFacileController` risponde 500 (il servizio del link non si costruisce). Le variabili dei modelli di contratto non servono al percorso CF (il contratto concordato lo prepara CF): riguardano i contratti fatti in CasaZen (libero e transitorio, §3.9).

## 8. Webhook Stripe

Due destinazioni **per ambiente** (test e produzione, ciascuna nella propria modalità di Stripe), con due segreti di firma diversi.

| | Destinazione di piattaforma | Destinazione Connect |
|---|---|---|
| Dashboard | «Il tuo account» | «Account collegati» (campo `application` valorizzato) |
| Indirizzo | `https://<API>/webhooks/stripe` | `https://<API>/webhooks/stripe/connect` |
| Eventi | 9: `customer.subscription.created`, `customer.subscription.updated`, `customer.subscription.deleted`, `invoice.paid`, `invoice.payment_failed`, `charge.refunded`, `payment_intent.succeeded`, `payment_intent.payment_failed`, `payment_intent.canceled` | 11: `account.updated`, `payment_intent.succeeded`, `payment_intent.processing`, `payment_intent.payment_failed`, `payment_intent.canceled`, `setup_intent.succeeded`, `charge.refunded`, `refund.created`, `refund.updated`, `refund.failed`, `charge.dispute.created` |
| Segreto di firma | `Stripe__WebhookSecret` | `Stripe__ConnectWebhookSecret` |
| Per CF serve | `payment_intent.succeeded` (completa l'acquisto), `charge.refunded` (rimborso totale), `customer.subscription.created` (regalo del piano a 24 mesi) | Niente: il pagamento CF è sul conto della piattaforma |

Regole fisse:

- **Payload sempre «Istantanea»** (*snapshot*), mai «Thin»: il gestore legge gli oggetti completi e un evento sottile non basta.
- **L'indirizzo è quello dell'API, non del sito**: `https://casazen-api.up.railway.app` in produzione e `https://casazen-api-test.up.railway.app` sul test. `casazen.space` risponde con un redirect 308 e `www.casazen.space` con 405 ai POST di Stripe.
- La firma è sempre verificata; un segreto mancante, o il segnaposto committato in `appsettings.json`, risponde 500 `stripe_webhook_not_configured` (Stripe ritenta).
- **Versione API.** `docs/INFRA.md` e `stripe.md` indicavano `2025-12-15.clover`, ma il Dashboard non la offre più: si sceglie la più recente (oggi `2026-09-30.endive`; l'API di Stripe accetta ancora `api_version: 2025-12-15.clover` se la destinazione si crea via API). Le destinazioni di test e produzione sono su `endive`. Il backend non si ferma per la differenza (`EventUtility.ConstructEvent(..., throwOnApiVersionMismatch: false)`, e la firma è verificata comunque), ma legge il payload con i modelli di Stripe.net 50.1.0, che fissa `clover` anche per le chiamate in uscita; l'SDK 53.0.0 fissa `endive`: l'aggiornamento è un lavoro a parte, in coda. I cambi non retrocompatibili di `endive` riguardano soprattutto i parametri delle richieste, non gli eventi che leggiamo; **non è provato**: si verifica con le consegne di prova (§9).

## 9. Come si prova

**Test** (Railway `test`, Stripe in modalità test). Prima: `GET /api/public/features` mostra `"concordatoFacile": true`; le variabili del §7 sono impostate; `LinkSecret` è il segreto del vettore. Senza `StatusApiKey` lo stato e il lookup `?practice=` falliscono (la pagina ripiega sulla scelta manuale): si prova il resto e lo stato quando CF consegna la chiave.

1. **Pagamento.** Nuovo contratto, canone concordato, «Parto da zero», un immobile, pagina del servizio, «Paga 119 euro e continua» con la carta `4242 4242 4242 4242`. Atteso: ritorno a `…/concordatofacile?checkout=success`; sul PaymentIntent, in Stripe, i metadati `leaseId`, `orgId` e `userId` e **nessun** `kind`; la consegna di `payment_intent.succeeded` alla destinazione di piattaforma risponde 200 e l'acquisto passa a `Completed`; un secondo clic su «Paga» mostra «Hai già accesso…» e non apre Stripe.
2. **Link.** «Accedi alla tua pratica» apre il modulo. Il link ha i sei parametri del §5; qualche link prodotto dal server si manda a CF perché lo verifichi come i tre di esempio (v0.2 §7).
3. **Stato.** Con la chiave di prova di CF e le sue pratiche d'esempio nei vari stati: sezione «Stato pratica» corretta in ogni stato, avviso di dato non aggiornato se CF non risponde.
4. **Ritorno.** Aprire `/app/long-rent/documents/upload?practice=P-0123456789abcdef` (l'esempio di CF) e i casi del §3.7: pratica collegata, non collegata, di un'altra organizzazione, formato errato, servizio spento.
5. **Omaggio.** Assegnarlo a mano con l'endpoint admin e usarlo su un contratto (il secondo contratto → 409).
6. **Rimborso.** Rimborsare tutto dal Dashboard: l'acquisto passa a `Refunded`.
7. **Webhook.** Dal Dashboard, «Invia evento di prova» su ciascuna destinazione: 200. Con un pagamento di prova verificare anche che l'evento letto con la versione API `endive` sia elaborato (§8).

**Produzione** (solo quando il codice CF sarà in produzione e il PO darà il via).

- In modalità live **le carte di prova non funzionano**: ogni prova di pagamento è un pagamento vero di 119 €, rimborsabile dal Dashboard (il sistema registra il rimborso totale come `Refunded`).
- Un **amministratore di piattaforma** apre il link senza pagare: è il modo di provare il modulo senza spendere.
- Il link di produzione apre il **modulo vero** di CF: ogni invio è una pratica vera. Come riconoscere le pratiche di prova e non lavorarle va concordato con CF prima.
- L'interruttore acceso vale per **tutti** gli utenti di produzione dal deploy.
- L'API di stato (`cfsp_live_…`) e i piani a 24 mesi (omaggio) **restano fuori** dalla prova.
- Prima: variabili live, le due destinazioni webhook live con i loro segreti, `LinkSecret` generato da CF, accordo R24.

## 10. Cosa non si fa, e gli errori da non far ricomparire

**Non si fa.**

- Nessuna scrittura verso terzi se non su azione esplicita dell'utente: la sessione Stripe nasce dal clic su «Paga», il link firmato dal clic su «Accedi alla tua pratica»; nessun job, evento o webhook avvia pagamenti, link o comunicazioni a CF. CasaZen non manda e-mail di pratica e non inoltra quelle di CF.
- Nessun webhook o ricevitore di eventi da CF, nessun download automatico di file da CF, nessun `caseId`.
- Nessuna lettura in background dello stato di CF (§6).
- Nessun segreto nel browser, nel bundle, nelle query interne, nei log o in chat; nessun URL o firma del link nei log o nel database.
- Nessun uso del solo abbinamento pratica-contratto per fatturare o consegnare documenti.
- Nessuna registrazione della stessa pratica anche tramite RLI o Openapi; nessun secondo invio fiscale automatico.
- Nessun secondo pagamento per lo stesso contratto e nessuna seconda pratica sullo stesso contratto.

**Errori da non far ricomparire.**

| Affermazione sbagliata | La verità |
|---|---|
| «Il `LinkSecret` lo genera CasaZen» | Lo genera **CF** e lo consegna fuori banda; per la prova vale il segreto pubblico del vettore |
| «`returnUrl` è obbligatorio nel link» | CF ignora `returnUrl`, `comuneIstatCode` e `theme`: il link ha sei parametri |
| «CF manda eventi: serve un ricevitore» | Non serve: l'API di stato della v0.2 ha sostituito gli eventi; CasaZen legge, CF non scrive |
| «Senza configurazione il checkout risponde 422 `concordato_facile_not_configured`» | Quel codice non esiste: senza `StripePriceId` o `BaseUrl` il checkout risponde 500 `internal_error`; senza `LinkSecret` ogni azione del controller risponde 500 (o l'app non parte) |
| «Il pulsante di CF resta spento finché…» come se fosse un nostro interruttore | Il pulsante «Carica i dati in CasaZen» delle e-mail lo spegne **CF**, dal suo lato, finché CasaZen non conferma il collaudo; il nostro interruttore è `Features__ConcordatoFacile` |
| «L'elenco di CF è un array» (ADR-008-R6) | È l'oggetto `{ leaseId, practices }` |
| «Senza chiave di stato si torna un elenco vuoto» (ADR-008-R22) | Senza chiave ogni lettura fallisce: un errore non è mai «nessuna pratica» |
| «Il segreto di prova funziona anche in produzione» | Il modulo di produzione di CF lo rifiuta |
| «Le destinazioni webhook vanno create su `2025-12-15.clover`» | Il Dashboard non la offre più: si sceglie la più recente (§8) |

## 11. Decisioni di prodotto già prese

| Decisione | Da quando |
|---|---|
| Il pulsante di CF porta solo `?practice=P-…`; il `leaseId` arriva dal lookup o dalla scelta dell'utente; i link vecchi con solo `leaseId` restano supportati | 22 ottobre |
| Una pratica per contratto: dopo l'invio niente pulsante; una nuova pratica richiede un nuovo contratto e un nuovo pagamento o omaggio | 22 ottobre |
| Un solo punto d'ingresso nella scheda del contratto: «Procedi con la creazione del contratto» (pagamento o omaggio, poi il modulo), «Accedi alla tua pratica» fino all'invio, poi lo stato | 22 ottobre |
| Nessuna lettura in background, più la rilettura breve (ogni 5 minuti per un'ora dopo l'emissione del link); lettura non riuscita e nessuno snapshot: il pulsante resta con un avviso | 22 ottobre |
| Solo il servizio completo: `Attested` è un passaggio, `Registered` lo stato finale | 22 ottobre |
| 119,00 € IVA compresa per pratica per tutte le organizzazioni; l'utente non paga CF | Intake Helix, 23 ottobre |
| Pratica omaggio: una per organizzazione con un abbonamento a 24 mesi; gli abbonamenti già esistenti si trattano a mano con l'endpoint admin (nessun backfill automatico) | Intake Helix, 23 ottobre |
| L'amministratore di piattaforma accede senza pagare; quello di un'organizzazione no | Intake Helix, 23 ottobre |
| Comunicazioni operative e documenti passano direttamente fra CF e utente; CasaZen non vuole copie né inoltri | 22 ottobre |
| «Nuovo contratto» chiede prima il tipo; il concordato non si prepara mai in CasaZen; «Ho già il contratto e i documenti» = si caricano i documenti e basta, «Parto da zero» = pagamento e modulo di CF con il nostro link firmato | 25 ottobre |
| Interruttore `Features:ConcordatoFacile`: acceso sul test, spento in produzione fino al collaudo e all'accordo sull'inquadramento privacy; nessun altro flag | 25 ottobre |
| Lettura dei documenti locale (PdfPig e Tesseract), nessun LLM né servizio esterno; si compila solo il vuoto | 25 ottobre (default NL-DOCS) |
| Il transitorio si fa in CasaZen solo dove `availableTypes` lo dice; il modello ufficiale è una bozza finché il PO non lo approva | 25 ottobre |
| Funzioni che scrivono a terzi (e-mail, solleciti, richieste di pagamento): solo su azione esplicita dell'utente | regola della wave |

## 12. Accordi con CF e punti aperti con CF

**Scambio con CF** (risposte di CasaZen del 22 ottobre, esito di CF del 23 ottobre, recepiti nella v0.2).

1. **Firma.** Generazione sul server al clic, scadenza 15 minuti, `leaseId` minuscolo, UTC al secondo con `Z`, HMAC-SHA256 sui valori decodificati, query codificata. CF ha verificato con il codice del suo modulo i tre link di esempio: le firme sono valide. Monza e Reggio nell'Emilia sono nell'elenco comuni del modulo; **Seveso no**: il modulo lo lascia scritto nella ricerca e la pratica resta legata al contratto.
2. **Frequenza.** Profilo del §6 accettato, compresa la rilettura ogni 5 minuti; il limite per chiave lo fissa CF sopra il profilo.
3. **Pulsante.** Percorso `https://casazen.space/app/long-rent/documents/upload` con `?practice=P-…`. CF ha già impostato il pulsante su questo indirizzo e lo tiene spento finché CasaZen non conferma il collaudo (redirect dominio/www, login, parametri preservati, pratica risolta, contratto autorizzato e confermato, upload, riapertura e download: la sola raggiungibilità HTTP della pagina non lo prova).
4. **Chiavi.** Bitwarden Send a una sola apertura, quando l'endpoint di prova è pronto, al referente CasaZen (indirizzo già comunicato a CF); la password su un altro canale (CF propone un SMS al numero dal quale scrive CasaZen). In prova resta il segreto del vettore; in produzione segreto di firma e chiave sono diversi e li genera CF.
5. **Servizio.** Solo il completo; se un giorno per CasaZen ci fossero altri servizi, CF avvisa prima e aggiunge un campo nell'endpoint.

**Da fare per CasaZen** (v0.2 §7 e messaggio di CF del 23 ottobre): (a) qualche link prodotto dal comando del server, da far verificare a CF come i tre di esempio (il comando è pronto: «Accedi alla tua pratica» sul test con il segreto del vettore); (b) la conferma del collaudo della pagina di caricamento con `practice` (ora in `develop`): fino ad allora il pulsante di CF resta spento; (c) la risposta sul canale per la password delle chiavi; (d) il `leaseId` del contratto per la pratica inviata dal modulo senza link firmato, che CF collega a mano (UUID minuscolo di un contratto CasaZen reale, comunicato a CF al momento dell'invio).

**Da chiudere con CF:** URL o selezione dell'ambiente di prova e i casi campione; la soglia per chiave (la fissa CF); la riconciliazione delle pratiche senza `leaseId` e dei costi; la presenza di Seveso e degli altri comuni pilota nell'elenco comuni del modulo e la lavorazione normale delle pratiche per comuni fuori elenco; l'**inquadramento privacy (R24 della v1.3)**: responsabile del trattamento ex art. 28 GDPR o titolare autonomo, dati trattati nello Spazio Economico Europeo e non usati per altri scopi né per comunicazioni commerciali, riferimenti dell'associazione, assistenza e conservazione, prima delle pratiche reali; come riconoscere le pratiche di prova fatte in produzione (§9).

**Accordi economici e adempimenti.** Il pagamento dell'utente a CasaZen (119 € IVA compresa) è distinto dal saldo a CF: l'utente non paga CF, CasaZen paga pratica per pratica dopo l'invio dei firmati. CF ha indicato importi finali di **69 e 89 € senza IVA**, con ricevuta al locatore per quota associativa e contributo, operazione non commerciale esente IVA ai sensi dell'art. 4 D.P.R. 633/72 secondo la dichiarazione del fornitore (non si applica alla vendita CasaZen). Per CasaZen vale solo il servizio completo: la tariffa da riconciliare è quella del completo (da confermare) e la modalità di riconciliazione è da concordare; le condizioni di annullamento o di pratica non attestabile sono da confermare. Secondo CF l'associazione registra telematicamente come intermediario a nome del locatore, senza delega firmata dalle parti, e la cedolare si sceglie in registrazione. Registro e bollo sono esclusi: con la cedolare non sono dovuti, altrimenti si addebitano sul conto del locatore con l'IBAN raccolto da CF nel proprio canale, mai nei link. I tempi indicati sono pochi giorni dopo i firmati e il pagamento, entro il termine dalla stipula richiamato dal partner: non uno SLA garantito di 48 ore. La comunicazione IMU al Comune resta separata e non automatizzata. Veste personalizzata del modulo (terracotta `#e2622d`, inchiostro `#1f3a3d`, lockup con il nome in tracciati): CF ne comunicherà la data dopo aver ricevuto marchio e dominio; non blocca una pratica pilota concordata.

## 13. Limiti e debiti noti

Gli ID `CF-DOC-FU…` sono proposti da questo documento; gli altri vengono dai rapporti dei task (`redesign/docs/wave/results/`, fuori dal repository).

**A. Difetti del codice trovati leggendo il codice contro la v0.2.** I primi cinque sono **in correzione (task CF-FIX2, branch `fix/cf-status-contract-and-tenant`)**; gli ultimi due sono da assegnare.

| ID | Difetto | Effetto |
|---|---|---|
| CF-DOC-FU1 | `ConcordatoFacileApiClient` legge l'elenco come array (`List<PracticeDto>`, da ADR-008-R6) e `PracticeDto.ComuneDiffersFromLink` non è nullable, mentre la v0.2 risponde `{ leaseId, practices }` e manda `null` (verificato con un controllo usa-e-getta di System.Text.Json: `JsonException` in entrambi i casi). **In correzione** | Appena arriva la chiave di stato, `GET …/status` risponde 500 e il lookup `?practice=` di una pratica non collegata pure. I test mockano il client e nessuno prova la lettura del JSON vero |
| CF-DOC-FU2 | `GET …/status` non verifica che il contratto sia dell'organizzazione del chiamante prima di chiamare CF (ADR-008-R11 non attuata; la chiave di cache include l'organizzazione, la chiamata a CF no). **In correzione** | Chi conosce il `leaseId` di un altro tenant ne legge le pratiche (numero, stato, comune, date) |
| CF-DOC-FU3 | `POST …/consume-gift` non verifica che il contratto sia dell'organizzazione (ADR-004-R6 non attuata). **In correzione** | L'omaggio si può consumare per un contratto qualunque |
| CF-DOC-FU7 | Dopo un errore di CF lo snapshot `stale` conserva la scadenza passata: ogni apertura rilegge CF subito (nessun intervallo dopo un errore). **In correzione** (nessuna rilettura per 5 minuti dopo un errore) | Durante un'interruzione il tetto di 4 letture all'ora non vale |
| CF-FIX-FU1 | Il lookup `practices/{id}/lease` non è in cache. **In correzione** (cache di 15 minuti) | Una lettura di CF a ogni apertura della pagina `?practice=` |
| CF-DOC-FU8 | Lo snapshot sta nella memoria del processo | Con più repliche del servizio il limite di 15 minuti vale per replica (oggi un solo servizio `backend` per ambiente; il numero di repliche non è stato verificato) |
| CF-DOC-FU9 | Il test del vettore ricalcola la firma nel test e non chiama `ConcordatoFacileLinkService`; nessun link prodotto dal server è stato verificato da CF | Il servizio è conforme alla lettura del codice, non a una prova |

**B. Frontend: requisiti non ancora realizzati.**

| ID | Difetto |
|---|---|
| CF-DOC-FU4 | Il pannello della scheda del contratto non legge lo stato e il pulsante «Accedi alla tua pratica» non sparisce quando l'elenco mostra una pratica (lo stato sta solo nella pagina del servizio); l'avviso «Non riusciamo a verificare lo stato: se hai già inviato il modulo non riaprirlo» (`concordatofacile.entry.statusFailed`) esiste ma non è usato; su un contratto concordato non-bozza (per esempio importato già firmato) il pannello CF compare comunque con l'interruttore acceso (dal codice, non verificato a vista) |
| CF-DOC-FU5 | La pagina del servizio non dice che registro e bollo eventualmente dovuti sono esclusi (lo chiedono i requisiti) |
| CF-FIX-FU4 | Nessun passo esplicito di conferma del contratto sulla pagina `?practice=`: con una pratica collegata si mostrano subito i documenti di quel contratto; l'avviso per la pratica non collegata dice che non è collegata, non che il collegamento lo corregge CF a mano |
| CF-DOC-FU10 | **Fine del percorso «da zero».** La bozza nasce con origine `Created` e l'importazione di un contratto già firmato (`POST …/import`) ammette solo bozze `Imported` (422 `lease_import_not_imported_origin`): il contratto fatto con CF si archivia nei Documenti ma la bozza non diventa «Firmata» da sola, e i documenti caricati nella bozza passano dalla prima pipeline di lettura, non dalla lettura completa. **Decisione aperta del PO** (LR-06a-FU4): requisito invariato, stesso `leaseId`, nessun secondo contratto |
| CF-FIX-FU5 | `useCreateCfDraft`, `leasesApi.createCfDraft` e la rotta `POST api/leases/drafts/concordatofacile` non sono usati dal flusso «da zero»: da togliere o da usare |
| (CF-FIX) | La pagina `?checkout=success` dice «Pagamento completato» a prescindere dall'accesso reale; l'`useEffect` della pagina del servizio è codice morto |
| NL-FLOW-FU1..3 | Il passo ③ del wizard può comparire «Da completare» prima di essere aperto; il focus non si sposta sul titolo fra una domanda e l'altra; il titolo «Transitorio (da 1 a 18 mesi)» è statico |

**C. Pagamento, omaggio e piani.**

| ID | Debito |
|---|---|
| CF-FIX-FU2 | Pagamento ritardato (sessione `complete` e non pagata) che poi **fallisce**: l'acquisto resta `Pending` e blocca un nuovo checkout (409) perché nessun gestore lo chiude (`payment_intent.payment_failed` per CF). Poco probabile oggi: il conto live non ha SEPA fra le capability |
| CF-FIX-FU3 | Due clic simultanei su «Paga» possono ancora aprire due sessioni e due righe `Pending` (nessun lock); l'interfaccia disabilita il pulsante durante la richiesta |
| BIENNIAL | La vendita dei piani a 24 mesi (e il regalo solo con sottoscrizione `active`/`trialing`) non è in `develop`: i Price a 24 mesi sono provvisori; il regalo si assegna solo con `customer.subscription.created` su un Price di `BiennialPriceIds` (un cambio piano verso il biennale non lo assegna) o a mano |
| CF-DOC-FU6 | I dati di attestazione e ricevuta di registrazione non si leggono dai documenti: sono archiviati e consultabili, le sezioni Attestazione e Registrazione non si popolano |

**D. Lettura dei documenti (NL-DOCS).** Il cammino OCR vero (`pdftoppm` e `tesseract` su una scansione o sulle foto) non è esercitato da nessun test e il parser non è mai stato provato su un contratto vero (tre contratti scritti a mano): **prova a vista del PO** su un PDF digitale e su una scansione nell'ambiente di test. Rimandati: NL-DOCS-FU1 società come parti; FU2 altre cittadinanze; FU3 dati dell'unità, garante, ISTAT, spese; FU4 canone che cambia negli anni e deposito scritto in lettere; FU5 indirizzo confrontato con l'anagrafica del comune; **FU6 ripulire le vecchie righe `LeaseDocument.ExtractionResult` di versione 1, che contengono in chiaro nomi di locatore e conduttore, canone, deposito e date** (serve una decisione sull'`UPDATE` mirato); FU7 prova automatica del cammino (PDF-scansione di prova e una spec L2); FU8 «Continua a mano» mentre la lettura finisce; FU9 il segno «letto dal contratto» riappare dopo un ricarico.

**E. Transitorio (TR).** TR-FU1 calcolatore delle fasce dell'accordo territoriale e controllo del canone (comuni oltre 10.000 abitanti); TR-FU2 accordo territoriale per comune; TR-FU3 residenza, cittadinanza e nascita delle parti e testo condizionale nel formato dei modelli; TR-FU4 mostrare l'esigenza nella scheda del contratto. Il modello è una bozza non approvata (§3.9).

## 14. Glossario degli ID e mappa del codice

| ID | Che cosa è |
|---|---|
| `ADR-004`…`ADR-009`, `ADR-00x-Rn` | Le decisioni del run Helix R20261023-1107 (23 ottobre) e le loro regole numerate, citate nei commenti del codice e dei test. Sono **etichette**, non requisiti: il punto di verità è questo documento (§15) |
| `NF-01`…`NF-08`, `AC-NFnn-m` | Requisiti del run Helix: NF-01 bozza minima; NF-02 offerta e pagamento; NF-03 pratica omaggio; NF-04 bypass dell'admin; NF-05 link firmato e punto d'ingresso; NF-06 ritorno con ricerca della pratica; NF-07 lettura dei documenti; NF-08 API di stato |
| `E01/F01/US01`, `e01-f02-us01-t01`… | Compiti di consegna Helix (epica, funzione, storia, task) delle PR backend #581–#598 e frontend #338–#343 |
| `CF-FIX` (backend #602, frontend #347) | Metadati sul PaymentIntent, niente doppio pagamento, interruttore, pagina `?practice=`; debiti `CF-FIX-FU1..5` |
| `CF-FIX2` | Correzione dei difetti CF-DOC-FU1, 2, 3, 7 e del lookup (branch `fix/cf-status-contract-and-tenant`) |
| `NL-FLOW` (frontend #346) | «Nuovo contratto»: prima il tipo, poi il come; debiti `NL-FLOW-FU1..3` |
| `NL-DOCS` (backend #604 e #608, frontend #348) | Lettura locale dei documenti del contratto e bozza che si compila; debiti `NL-DOCS-FU1..9` |
| `TR` (backend #609, frontend #349) | Tipo transitorio con il modello ufficiale (bozza non approvata); debiti `TR-FU1..4` |
| `LR-12`, `LR-13`, `LR-APE`, `LR-DEP` | Compiti della catena Affitti lunghi che toccano lo stesso contratto: bozza guidata, importazione di un contratto già firmato, codice e classe dell'APE, deposito |
| `PL-10`, `A3-39` | Idempotenza dei webhook Stripe; tolleranza sulla versione API degli eventi |
| `R24` | Punto della v1.3 di CasaZen: inquadramento privacy da definire con CF prima delle pratiche reali |
| `P-…` | Numero di pratica di CF: `P-` e 16 caratteri esadecimali |
| `leaseId` | UUID reale e stabile di `LeaseContract`, anche per una bozza; non è l'ID della casa, del pagamento o della pratica |
| `cfsp_test_…`, `cfsp_live_…` | Prefissi delle chiavi dell'API di stato di CF (prova, produzione) |

**Dove sta nel codice.** Backend: `ConcordatoFacileController`, `AdminConcordatoFacileController`, `LeasesController.CreateCfDraft` (`Casazen.Web/Controllers`); servizi `ConcordatoFacileCheckoutService`, `ConcordatoFacileAccessService`, `ConcordatoFacileGiftService`, `ConcordatoFacileLinkService`, `ConcordatoFacileStatusCacheService`, `ConcordatoFacileAdminService` (`Casazen.Infrastructure/Services`); `ConcordatoFacileApiClient` e `ConcordatoFacilePaymentHandler` (`Casazen.Infrastructure/External`, il secondo chiamato da `StripeWebhookHandler`); entità `ConcordatoFacilePurchase`, `ConcordatoFacileGift`, `LeaseContract.IsConcordatoFacileSource` e gli eventi `ConcordatoFacileLinkGenerated`, `ConcordatoFacileGiftConsumed`, `ConcordatoFacileAdminAccess`; opzioni `ConcordatoFacileOptions` con il validatore di avvio; flag in `FeatureFlags.cs`. Frontend: `lease-create-page.tsx`, `concordato-draft-start.tsx`, `start-way-choice.tsx`, `documents-start.tsx` (`src/features/leases`), `concordatofacile-offer-page.tsx` e `cf-status-section.tsx` (`src/pages/long-rent/leases`), `lease-detail-page.tsx` (pannello e avviso di bozza), `lease-documents-upload-page.tsx` (ritorno), `use-concordatofacile.ts` (query).

## 15. Documento → stato

| Documento | Stato | Perché |
|---|---|---|
| `docs/integrations/concordatofacile/README.md` | **corretto** | Questo documento (revisione 11) |
| `Sessions/business-analysis-canone-concordato.md` | **corretto** | Requisiti e accettazione, revisione 11, allineata a questo documento |
| `docs/integrations/concordatofacile/da-inviare/01-technical-specification.md` e `.pdf` (v1.3, 13 ottobre) | **SUPERATO** | Banner in testa al `.md` (il `.pdf` è lo stesso testo, senza banner). Valgono soltanto §2 (vettore di prova della firma), le convenzioni richiamate dalla v0.2 e R24. Webhook, `caseId`, `returnUrl`, `theme`, `comuneIstatCode`, dati attestati e consegna dei file non si implementano |
| `docs/integrations/concordatofacile/da-inviare/02-openapi.yaml` | **SUPERATO** | Come sopra (commento in testa) |
| `docs/integrations/concordatofacile/01-specifica-integrazione.md`, `.pdf` e `02-openapi.yaml` (v1.0, 13 ottobre) | **eliminati** | Modello a eventi con nomi in italiano, `reference` e fasi F1–F3: sbagliato per intero e non citato da nessuno; restano nella cronologia di git |
| `docs/adr/ADR-004` … `ADR-009` | **SUPERATI** come riferimento del processo | Banner in testa a ciascuno con i punti che non valgono più. Restano nel repository perché i commenti del codice e dei test citano gli identificativi delle loro regole |
| `docs/runbooks/deploy-checklist.md`, `docs/runbooks/feature-flags.md` | **corretti** | Righe CF e Stripe riviste (§7, §8) e una frase sul flusso «da zero» |
| `docs/runbooks/stripe.md`, `docs/INFRA.md` § «Stripe keys and webhooks» | **corretti** | Versione API delle destinazioni, indirizzo, tolleranza sulla versione; sezione sul pagamento CF |
| `docs/runbooks/canone-concordato.md` | **corretto** | Regole del canone (non il processo CF); aggiornata la riga del transitorio e aggiunto il rimando |
| `docs/runbooks/lease-document-extraction.md` | **corretto** | Riferimento tecnico della lettura dei documenti (NL-DOCS); a questo documento rimanda il §3.8 |
| `docs/runbooks/lease-documents.md` | **corretto** | Aggiornata una frase che dava per futuro ciò che ora esiste |
| `docs/runbooks/documenti-multipagina.md`, `long-rent-lease-import.md`, `long-rent-lease-draft.md`, `lease-contract-templates.md`, `long-rent-aggregates.md`, `org-team.md` | **corretti** | Non descrivono il processo CF, o lo fanno in modo vero (per esempio `AwaitsPartnerContract` oggi non è impostato da nessuno) |
| `.claude/context/regulations/canone_concordato.md`, `docs/index.md` | **corretti** | Regole del canone concordato e indice della documentazione: aggiornato il rimando ai documenti di riferimento |
| `Sessions/canone-concordato-calculator/*`, `Sessions/research-canone-concordato-mb.md`, `Sessions/contratto-locazione-canone-concordato-bozza.html`, `docs/legal-drafts/leases/*` | **corretti** (storico) | Ricerca, calcolo e bozze sul canone concordato, non il processo CF |
| `secrets/railway.test.variables.example.json`, `secrets/railway.prod.variables.example.json` | **corretti** | Variabili `ConcordatoFacile__*` e `Features__ConcordatoFacile` per ambiente |

Gli appunti di lavoro fuori dal repository (l'area di lavoro di Helix, i registri della wave) non sono riferimenti e non vanno usati per il processo CF.
