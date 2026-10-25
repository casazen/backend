# CasaZen e ConcordatoFacile — il processo (documento unico di riferimento)

> **Questo è l'UNICO riferimento per il processo ConcordatoFacile (CF).** Con il documento dei requisiti [`Sessions/business-analysis-canone-concordato.md`](../../../Sessions/business-analysis-canone-concordato.md) (che dice che cosa deve valere e come lo si accetta) è tutta la documentazione di riferimento sul tema. Ogni altro file che parla di CF è, nella tabella del §15, **corretto** (e allora rimanda qui), **SUPERATO** (storico: non usarlo come riferimento) oppure **eliminato**. Se un altro file dice cose diverse da questo documento, ha torto l'altro file.

Revisione 12 · 25 ottobre 2026 (aggiornata sul codice fuso dei task CF-IMPORT, CF-FLOW, CF-BE, CF-SIM, CF-END, CF-URL e CF-LEGAL e sul test automatico del giro CF-L3; la revisione 11 era la riscrittura completa della revisione 10 del 23 ottobre). **Fonti:** il codice di `develop` al 25 ottobre (backend e frontend, compreso il transitorio del task TR), la specifica di CF «ConcordatoFacile — specifica endpoint di stato v0.2», ottobre 2026, bozza per commenti (d'ora in poi *la v0.2*; il PDF non è nel repository) e le decisioni dell'utente fino al 25 ottobre. La v0.2 governa il link firmato e l'API di stato. Della «specifica tecnica v1.3» di CasaZen (13 ottobre, cartella `da-inviare/`) valgono soltanto il §2 (vettore di prova della firma), le convenzioni che la v0.2 richiama (JSON in UTF-8 con campi `camelCase`, date ISO 8601 UTC con `Z`, errori `application/problem+json` con `code` in `snake_case`) e il punto R24 (inquadramento privacy).

**Stato in una riga.** Il servizio è costruito in `develop` ed è spento di default; in produzione non c'è alcun codice CF (`main` è la v2.1.2 del 23 ottobre); il giro intero si prova sul test con un modulo simulato (§9) e CF sta preparando un ambiente di prova del suo modulo (§12); manca il collaudo con CF. Quanto manca o non torna è nel §13.

## 1. Scopo e attori

**Scopo.** Il proprietario (locatore) di un immobile a lungo termine fa il contratto a canone concordato (3+2) con ConcordatoFacile: CF lo prepara sul modello ufficiale, lo fa firmare, ottiene l'attestazione dall'associazione e lo registra. CasaZen vende il servizio, apre il modulo di CF con un link firmato, mostra lo stato della pratica e conserva nel contratto i documenti finali.

| Attore | Che cosa fa |
|---|---|
| Utente (locatore) | Sceglie il tipo di contratto, accetta l'informativa privacy, paga, compila il modulo di CF, gestisce con CF (per e-mail) integrazioni, revisione e firma, carica in CasaZen i documenti finali |
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
| Spento | Rispondono **404**, prima dell'autenticazione, tutte le azioni di `ConcordatoFacileController` (`POST api/leases/{id}/concordatofacile/checkout`, `GET …/access`, `POST …/consume-gift`, `POST …/privacy-acceptance`, `POST …/link`, `GET …/status`), il lookup `GET api/concordatofacile/practices/{practiceId}/lease` e `POST api/leases/drafts/concordatofacile`. L'app non mostra il pannello nella scheda del contratto, la rotta `/app/long-rent/leases/:id/guided-contract` torna al contratto, la carta «Parto da zero» del nuovo contratto è chiusa («Disponibile a breve») e la pagina di caricamento aperta dal pulsante di CF ripiega sulla scelta manuale del contratto |
| Acceso | Tutto il flusso del §3 |
| Mai dietro l'interruttore | I webhook Stripe (un pagamento o un rimborso già avvenuto viene registrato comunque), il client di stato, il regalo assegnato da un abbonamento a 24 mesi, il controller admin (`api/admin/orgs/{orgId}/concordatofacile/grant-gift`) e il **validatore di avvio** delle opzioni `ConcordatoFacile`: richiede `StripePriceId`, `BiennialPriceIds` e `LinkSecret` in Production **e in ogni ambiente che non sia Development o Testing** (quindi anche sul test Staging di Railway), anche con il servizio spento. Per questo il primo rilascio in produzione con questo codice le richiede già impostate |

**Attivazione.** Test: acceso. Produzione: l'interruttore resta **non impostato** finché (a) CasaZen ha confermato a CF il collaudo della pagina di ritorno con `?practice=` e della firma dei link, (b) è concordato con CF l'inquadramento privacy (R24, §12), (c) le variabili del §7 e le due destinazioni webhook del §8 sono a posto e (d) il codice CF è arrivato in produzione con una release (develop→main, solo con il via libera esplicito dell'utente). Si accende impostando `Features__ConcordatoFacile=true` su Railway e rifacendo il deploy; si spegne togliendo la variabile.

**Stato per ambiente (25 ottobre).**

| | Test (Railway `test`, app web Vercel su `develop`) | Produzione (Railway `production`, `https://www.casazen.space`) |
|---|---|---|
| Codice CF | Quello di `develop` (deploy automatico) | **Nessuno**: `main` è la v2.1.2 del 23 ottobre |
| Interruttore | `true` | non impostato (spento) |
| Variabili `ConcordatoFacile__*` | Impostate il 24 ottobre, **tranne `StatusApiKey`** (la deve consegnare CF); `LinkSecret` = segreto pubblico del vettore di prova; dal 25 ottobre **`FormMode=Simulated`** (modulo e API di stato simulati, §9; runbook `concordatofacile-simulator.md`) | Price live e `BaseUrl` impostati il 24 ottobre; `LinkSecret`: **il valore vero di CF, impostato dall'utente il 25 ottobre**; `StatusApiKey` assente; `FormMode` e `FormUrl` **mai impostati** (il validatore di avvio li rifiuta in Production) |
| Webhook Stripe | Due destinazioni (§8) | Due destinazioni (§8) |
| Con CF | Firma verificata da CF sui tre link di esempio (23 ottobre). Il 25 ottobre CF ha risposto sì a due richieste: un **ambiente di prova del suo modulo** che accetta i link firmati col segreto di prova della §2 (indirizzo e accesso riservati per e-mail con un Send a una sola apertura e password per SMS; le pratiche di prova non si lavorano, l'e-mail di conferma parte) e le copie in PDF di una pratica conclusa con i dati personali oscurati (attestazione, contratto, ricevuta). Mancano ancora la chiave di stato di prova e il collaudo | Il pulsante «Carica i dati in CasaZen» è spento da CF |

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

1. L'utente sceglie l'immobile. CasaZen salva subito una bozza vera con `POST /api/leases/drafts/concordatofacile` (solo con l'interruttore acceso): il contratto ha il suo **`leaseId`** senza chiedere APE, parti, canone o date. La bozza è una bozza **importata** (origine `Imported`, tipo `Concordato`, marcata `IsConcordatoFacileSource`): il contratto lo scrive e lo fa firmare CF fuori dai nostri modelli, quindi a CasaZen torna come contratto già firmato da importare (§3.8). La rotta è idempotente per immobile: un doppio clic, un nuovo tentativo o un nuovo «Parto da zero» sulla stessa casa riaprono la stessa bozza finché è una bozza; per una seconda pratica sulla stessa casa si importa prima (o, se non pagata, si cancella) la prima.
2. L'app va alla **scheda del contratto** `/app/long-rent/leases/:id` (sostituendo la voce di cronologia): è il centro del giro, non la pagina del servizio.
3. La scheda mostra UN solo blocco evidenziato, con questa precedenza: (a) un documento `Contract` caricato → `CfImportPanel` (punto 4), anche con l'interruttore spento; (b) interruttore spento → «Il modulo guidato non è ancora disponibile.», pulsante disabilitato; (c) accesso già attivo, modulo già aperto (`linkOpenedAt`) e lo stato elenca una pratica **non chiusa** (ogni stato tranne `ClosedWithoutAttestation`) → vista di **stato**: badge, data dell'ultimo cambio, numero `P-…`, riga sul comune se differisce dalla casa, e soltanto «Carica i documenti»: nessun pulsante riapre il modulo (regola di CF: riaprirlo dopo l'invio crea una seconda pratica); (d) altrimenti la casella «Ho letto e accetto l'informativa privacy» (link a `/legale/privacy`; si registra una sola volta per contratto e versione dell'informativa) e il pulsante «Procedi con la creazione del contratto» (prima volta) o «Riprendi la compilazione» (modulo già aperto): senza diritto porta alla pagina di pagamento (§3.4), con il diritto (amministratore, pagato, omaggio) apre il modulo subito (§3.5); se lo stato non è leggibile e il modulo è già stato aperto, sopra il pulsante c'è «Non riusciamo a verificare lo stato: se hai già inviato il modulo, non riaprirlo». Una pratica chiusa mostra il numero e il motivo in parole neutre (doppione, ritirata, contratto non attestabile, altro) e il pulsante per ricominciare. Sempre «Carica i documenti». Nessun testo dell'app nomina il fornitore né «partner» (test `no-supplier-name`). La bozza non genera occupazione, incassi o adempimenti di un contratto attivo.
4. Quando CF consegna i documenti, l'utente li carica nella sezione Documenti del contratto (§3.7): il `leaseId` non cambia. Il contratto si legge sul server come quello di ogni bozza importata (§3.8) e la pagina di caricamento e la scheda mostrano lo stato della lettura (in corso, letto, non letto, con la frase onesta del caso) e un solo pulsante, «Completa e importa il contratto», che apre la procedura guidata della bozza sul primo passo in cui manca qualcosa (a mano, dal passo delle parti, se la lettura è fallita o non ha trovato nulla). L'importazione finale (`POST /api/leases/{id}/import` con `contractDocumentId`) usa il PDF già caricato e porta la bozza a «Firmato», con lo stesso `leaseId` e la marcatura CF intatta; da lì la scadenza, il piano canoni e la registrazione sono quelli di ogni contratto firmato.

La bozza non si cancella finché ha un acquisto CF non fallito (in corso, completato o rimborsato): `LeaseDraftDeleter.IsDeletableAsync` è l'unica regola, usata sia dalla cancellazione del proprietario (409 `lease_not_deletable`) sia dal job di pulizia delle bozze abbandonate (`lease-draft-retention`, 180 giorni), che la salta. Un acquisto `Pending` più vecchio della scadenza della sessione Checkout (2 ore) più 10 minuti conta come chiuso (`Failed`): una sessione abbandonata non blocca né un nuovo pagamento né la cancellazione (CF-BE, chiude CF-IMPORT-FU1). Le chiavi esterne `Restrict` di acquisti e omaggi restano una seconda barriera (e coprono l'omaggio consumato).

### 3.4 La pagina del servizio: pagamento, omaggio, accesso admin

Il diritto lo decide **il server** (`GET …/access`: `hasAccess`, `source` = `payment`/`gift`/`admin`, `giftAvailable`, `giftConsumed`, e `privacyAccepted`, `privacyAcceptedAt`, `linkOpenedAt`); il ritorno `?checkout=success` da Stripe non è una prova. **La pagina del servizio `/app/long-rent/leases/:id/guided-contract` è soltanto la pagina di pagamento** e si raggiunge dal pulsante della scheda quando manca il diritto (il vecchio indirizzo `…/concordatofacile` resta come rinvio per i segnalibri e le sessioni già create).

| Condizione verificata dal server | Che cosa vede e fa l'utente |
|---|---|
| Nessun diritto | Contratto, prezzo «119,00 EUR totali IVA compresa» e «registro e bollo, se dovuti, non sono compresi nel prezzo» (CF-DOC-FU5), e i cinque passi del processo senza nomi (paghi 119 euro, «o usi la pratica omaggio» solo se c'è; compili il modulo guidato; il contratto viene preparato per te; lo ricevi via e-mail pronto da firmare; lo carichi in CasaZen, lo leggiamo e compiliamo i dati). «Paga 119 euro e continua» → Stripe Checkout (`POST …/checkout`) → ritorno a `…/guided-contract?checkout=success` o `=cancelled` («Il pagamento è stato annullato», «Riprova»); dopo `success` la pagina rilegge l'accesso ogni 2 secondi per circa 40 (il webhook può arrivare dopo il ritorno) e appena c'è apre il modulo; se tarda dice che l'attivazione richiede qualche istante e offre «Controlla di nuovo» |
| Omaggio disponibile (`giftAvailable` e non consumato) | «Usa la pratica omaggio e continua» (`POST …/consume-gift`, serve `lease.create`) al posto del pagamento; subito dopo si apre il modulo |
| Pagato, o omaggio già consumato per questo contratto | La pagina di pagamento rimanda alla scheda (segnalibro e «indietro» non aprono il modulo da soli); dalla scheda il pulsante apre il modulo (§3.5) |
| Pagamento già fatto ma non ancora registrato, o secondo clic su «Paga» | 409 `concordatofacile_already_purchased` («Per questo contratto il servizio risulta già pagato o attivato: non serve pagare di nuovo»), nessuna nuova sessione Stripe |
| Amministratore di piattaforma (ruolo `Admin`) | Non vede la pagina di pagamento: dalla scheda il pulsante apre il modulo (accede senza pagare e senza consumare omaggi). L'amministratore di una normale organizzazione non ha questo accesso |

**Pratica omaggio.** Ogni organizzazione con un abbonamento a **24 mesi** riceve **una** pratica omaggio, da usare per un solo contratto (non vale «24 mesi di anzianità»). Si assegna con l'evento `customer.subscription.created` del webhook di piattaforma quando il Price è in `ConcordatoFacile__BiennialPriceIds` (anche con il servizio spento), oppure a mano con `POST api/admin/orgs/{orgId}/concordatofacile/grant-gift` (solo admin di piattaforma; 409 se l'organizzazione ne ha già una; evento di audit `AdminGrantedConcordatoFacileGift`). Il consumo è atomico (lock consultivo di PostgreSQL per organizzazione): rientrare per lo stesso contratto non consuma di nuovo (200); un secondo contratto → 409 `concordatofacile_gift_already_consumed`. I Price a 24 mesi esistono (provvisori) ma la vendita dei piani a 24 mesi non è ancora in `develop` (task BIENNIAL, §13).

**Una pratica per contratto.** Rientrare nel modulo prima dell'invio non richiede un altro acquisto. Una seconda pratica richiede un **nuovo contratto** e un nuovo pagamento (l'omaggio è uno solo), anche dopo una pratica chiusa.

### 3.5 Il modulo di CF e il link firmato

«Procedi con la creazione del contratto» / «Riprendi la compilazione» (dalla scheda; dalla pagina di pagamento solo subito dopo il ritorno da Stripe o l'uso dell'omaggio) chiede al server `POST …/link` (serve `lease.create`, il diritto del §3.4 e **l'accettazione dell'informativa privacy corrente per quel contratto**: senza, 409 `concordatofacile_privacy_not_accepted`, anche per l'amministratore) e porta la **stessa scheda del browser** all'indirizzo firmato (`window.location.assign`: nessuna nuova scheda, mai in un iframe; una richiesta per gesto). La privacy si registra con `POST …/privacy-acceptance` (evento di cronologia `ConcordatoFacilePrivacyAccepted` con la versione dell'informativa, senza dati personali; ripetuta, non scrive un secondo evento); quando la versione dell'informativa cambia il modulo la richiede di nuovo. Il testo sotto il pulsante avverte che il modulo si apre in una pagina dedicata e che «riprende solo sullo stesso dispositivo/browser utilizzato»: prima dell'invio, sullo stesso dispositivo e browser, un nuovo link riprende la bozza del modulo; dopo l'invio, o da un altro dispositivo, apre una compilazione **nuova** e quindi, se inviata, una seconda pratica sullo stesso contratto (CF la chiude come doppione). Parametri, firma e segreto: §5. Con il referrer di default dei browser a CF arriva la sola origine del sito.

Dopo l'invio l'utente gestisce con CF, **direttamente e per e-mail**, integrazioni, revisione del contratto e firma; CF gli manda i documenti dopo la firma. CasaZen non riceve, non copia e non inoltra le e-mail di pratica e non ne manda di sue (nessuna e-mail di «pratica ricevuta» o finale da CasaZen). Il servizio per CasaZen è sempre la **pratica completa**, fino alla registrazione all'Agenzia delle Entrate: il modulo non mostra i prezzi di CF né la scelta del servizio.

### 3.6 Dopo l'invio: lo stato della pratica

CasaZen legge lo stato dal proprio backend (§6), solo se c'è l'accesso e il modulo è già stato aperto, e lo mostra nel blocco della scheda (§3.3): badge dello stato, data dell'ultimo passaggio, numero della pratica e, se il comune scelto nel modulo differisce da quello della casa, la riga che lo dice. Una pratica chiusa mostra il motivo in parole neutre. Se CF non risponde si usa l'ultimo stato noto (segnato non aggiornato); se non c'è nessuno stato (anche per chiave mancante: `available: false`) il pulsante resta con l'avviso «Non riusciamo a verificare lo stato: se hai già inviato il modulo, non riaprirlo». I doppioni restano nell'elenco (CF li chiude con il motivo `duplicate`).

| Fase | Stati di CF | Che cosa fa l'utente |
|---|---|---|
| Ricezione e lavorazione | `Received`, `InProgress`, `SignedReceived`, `SentToAssociation` | Aspetta |
| Azione richiesta | `AwaitingUser`, `DraftSent` | Legge e gestisce le e-mail di CF (dati o documenti mancanti; bozza da approvare e firmare) |
| Attestazione | `Attested` | Attestazione rilasciata e inviata all'utente; per CasaZen è un passaggio verso la registrazione, **non** uno stato finale |
| Registrazione | `Registered` | Stato finale positivo |
| Chiusura senza attestazione | `ClosedWithoutAttestation` (`closeReason`: `duplicate`, `withdrawn`, `not_compliant`, `other`) | Stato finale negativo |
| Sospensione | `Suspended` | Pratica ferma (per esempio l'utente non risponde); può riprendere, non è una chiusura |

Gli stati possono alternarsi (`InProgress` e `AwaitingUser`; da `Suspended` si torna allo stato di prima), la data dell'ultimo passaggio non retrocede; non si considera «attestata» uguale a «registrata». Uno stato sconosciuto si mostra in modo neutro, mai come «completata».

**Come è fatto (25 ottobre, CF-FLOW).** Un solo punto d'ingresso nella scheda; appena l'elenco di CF mostra una pratica non chiusa il pulsante sparisce e resta lo stato (con una pratica già attestata o registrata non si offre mai il modulo); se la lettura non riesce e non c'è nessuno snapshot il pulsante resta con l'avviso. Il ritardo fra l'invio e la lettura (cache fino a 15 minuti, rilettura ogni 5 minuti per un'ora dopo l'apertura del modulo) può lasciare «Riprendi la compilazione» per qualche minuto: è la regola di CF e dal frontend non si evita (CF-FLOW-FU6).

### 3.7 Il ritorno da CF: «Carica i dati in CasaZen» e `?practice=`

CF manda all'utente, nelle e-mail con i documenti da conservare e nella conferma testuale del modulo, il pulsante «Carica i dati in CasaZen»: apre `https://casazen.space/app/long-rent/documents/upload?practice=P-0123456789abcdef` (numero di pratica: `P-` più 16 caratteri esadecimali). È una pagina web: non trasferisce file e non è l'endpoint di stato. CF ha già impostato il pulsante su questo indirizzo e lo tiene **spento** finché CasaZen non conferma il collaudo (§12). Il parametro è solo `practice`: il `leaseId` non serve nel link.

| Indirizzo | Che cosa fa la pagina |
|---|---|
| `?practice=P-…` valido | Chiede al server `GET /api/concordatofacile/practices/{id}/lease` (una lettura dell'API di stato di CF, tenuta in cache 15 minuti, senza nuovi tentativi dell'app). Pratica collegata a un contratto **dell'organizzazione dell'utente**: mostra i documenti di quel contratto con la riga «Pratica P-… collegata a questo contratto» |
| Pratica non collegata (`leaseId` nullo: `manualSelectionRequired`) | Avviso («La pratica P-… non è ancora collegata a un contratto») e **scelta manuale** fra i propri contratti; si prosegue su `?leaseId=…&practice=…`. Il collegamento nel sistema di CF lo corregge CF a mano: CasaZen non può scrivere a CF e la scelta dell'utente non cambia il `leaseId` della pratica |
| Pratica non trovata, di un'altra organizzazione, lettura non disponibile o servizio spento | «Non troviamo questa pratica tra i tuoi contratti» e scelta manuale; nessun contratto è toccato finché l'utente non sceglie; nessun dettaglio su altri tenant (404 uniforme) |
| `practice` con formato errato o ripetuto | «Collegamento non valido» e scelta manuale, nessuna lettura |
| `?leaseId=<uuid>` (link vecchi) | I documenti di quel contratto; con `leaseId` e `practice` insieme vince il contratto nominato |
| Nessun parametro | Scelta del contratto fra i propri |

Dopo il login percorso e parametri sono preservati. Caricamento e consultazione riusano le API dei documenti del contratto (`GET`/`POST /api/leases/{leaseId}/documents` e download), senza un altro acquisto. Se il contratto è la bozza «da zero» di CF e ha un documento `Contract`, la pagina mostra anche lo stato della lettura e «Completa e importa il contratto» (§3.3). Il collegamento di CF **non prova** che i documenti appartengano a quel contratto (l'utente può aver scritto nel modulo i dati di un altro immobile; `comuneDiffersFromLink` coglie solo il comune cambiato): CasaZen non fattura e non consegna nulla in base al solo abbinamento. Prima del caricamento c'è un passo esplicito («Stai caricando i documenti della pratica P-… sul contratto «casa · città»: conferma che è quello giusto», con «Scegli un altro contratto»), anche per il link vecchio con solo `leaseId` (CF-FIX-FU4, chiuso da CF-FLOW); i link dell'app alla pagina non lo richiedono.

### 3.8 Documenti e dati del contratto

| Raccolta | Contenuto |
|---|---|
| Documenti della casa | APE, planimetria e documenti propri dell'immobile |
| Contratti della casa | Ogni contratto con il proprio `leaseId` |
| Documenti del contratto | Contratto definitivo, attestazione, ricevuta di registrazione e altri allegati di quel `leaseId` (tipi `Contract`, `Attestation`, `Registration`, `Other`) |

Si carica un PDF oppure più foto JPEG/PNG dello stesso documento (fino a 12 foto, 20 MB per foto e 40 MB in ingresso; le foto diventano **un solo PDF** nel browser e gli originali non si conservano; un PDF esistente conserva i propri byte, firme digitali comprese; il PDF di un allegato del contratto è al massimo 20 MB). Storage privato; autorizzazione sul contratto e sul documento: [`lease-documents.md`](../../runbooks/lease-documents.md), [`documenti-multipagina.md`](../../runbooks/documenti-multipagina.md).

**Lettura (NL-DOCS).** Il contratto caricato in una bozza «Importato» si legge **sul server**: PdfPig per il PDF digitale, poi Tesseract (`ita+eng`) per scansioni e foto; nessun servizio esterno e nessun LLM (un contratto contiene nomi, codici fiscali e indirizzi e non c'è un sub-responsabile approvato). Il parser (che ha sostituito la prima pipeline di ADR-009) compila **solo ciò che è vuoto** e che il testo dice senza ambiguità (una persona si legge solo con un codice fiscale valido), non sovrascrive mai, e segnala i conflitti; `ExtractionResult` non contiene valori personali (solo nomi dei campi e stati); i valori vanno solo nella bozza cifrata. Dettagli e limiti: [`lease-document-extraction.md`](../../runbooks/lease-document-extraction.md), non duplicati qui. La lettura completa vale per ogni bozza «Importato»: quella dell'ingresso A (§3.2) e la bozza «da zero» di CF (§3.3, importata anch'essa); un documento che non è di tipo `Contract` caricato in una bozza importata non si legge. Per ogni altro contratto (un contratto che esiste già) resta la prima pipeline, che scrive nel contratto solo i pochi campi di cui è certa. **Non** si leggono ancora l'attestazione e la ricevuta di registrazione: si archiviano e si consultano, ma i loro dati non popolano le sezioni Attestazione e Registrazione (CF-DOC-FU6).

Caricamento, attestazione e registrazione sono eventi distinti: il solo upload, o una ricevuta di trasmissione, non prova la registrazione, e lo stato `Registered` di CF non significa che la ricevuta sia già archiviata in CasaZen. Un contratto si dichiara registrato in CasaZen con la dichiarazione manuale (`POST /api/leases/{id}/registration/manual`: `registrationCode`, `registrationDate` e **esattamente uno** fra `receipt`, il file, e `receiptDocumentId`, una ricevuta già caricata fra i Documenti come tipo `Registration`: documento di un'altra locazione o organizzazione → 404 `document_not_found`). Per un contratto fatto con il servizio (`isConcordatoFacileSource`), importato e firmato, il passo del server è `RegisterLease` con `imported: true` e la scheda dice che **la registrazione la esegue il servizio**: nessuna istruzione per registrare da soli sul sito dell'Agenzia e nessun «Invia al provider»; la persona carica la ricevuta nei Documenti e la dichiara con «Usa la ricevuta già caricata». Nel passo «Dopo la firma» di un concordato c'è anche la sezione «Attestazione» (documento `Attestation`, non bloccante) e, a registrazione ultimata, il blocco «Contratto registrato: ora fai valere le agevolazioni»: portare copia di contratto firmato, attestazione e ricevuta al commercialista e al comune (IMU ridotta del 25 %; cedolare secca al 10 % dove è prevista; il comune può chiedere l'attestazione per l'aliquota ridotta), con «confermale con il tuo commercialista» (CF-END). Nessun secondo invio fiscale automatico.

**APE.** Resta un documento della casa. Il suo controllo (codice, classe e date sull'intero attestato) non è una certificazione di autenticità e non è bloccante: il file si conserva sempre con un esito, e per creare, firmare o registrare un contratto bastano codice e classe scritti dal locatore oppure un APE verificato (PR #569, release v2.1.1); codice e classe si leggono anche dal documento (PR #578, v2.1.2). Non si applica agli allegati del contratto.

### 3.9 Libero 4+4 e transitorio: fuori da CF

Il modulo di CF accetta soltanto contratti a canone concordato: libero e transitorio non passano da CF (CF li segue per e-mail fuori da questo percorso; ha indicato 20 € per la sola registrazione di un contratto già pronto, condizioni da completare). In CasaZen: «Lo preparo con CasaZen» (wizard e modello di contratto) oppure «Ho già i documenti» (stessa lettura del §3.8). Il modello del libero e degli altri regimi è una bozza **non approvata** finché il PO non la approva con le variabili `LeaseTemplates__Variants__*` ([`lease-contract-templates.md`](../../runbooks/lease-contract-templates.md)).

Il transitorio «in CasaZen» è in `develop` dal 25 ottobre (task TR: backend #609, frontend #349): `availableTypes` lo include, il wizard chiede l'esigenza transitoria e il termine (da 1 a 18 mesi) e il modello è l'Allegato B del D.M. 16/01/2017. Il modello è una **bozza non approvata**: «Scarica da firmare» risponde 422 `contract_template_not_approved` e l'anteprima è marcata BOZZA finché il PO non lo approva con `LeaseTemplates__Variants__Transitorio__*`. Debiti TR-FU1..4 al §13.

## 4. Il pagamento in dettaglio (Stripe)

Checkout Session di Stripe in modalità `payment` (non abbonamento), sul **conto della piattaforma** (non Stripe Connect, non i pagamenti degli affitti), con una riga: il Price una tantum configurato (`ConcordatoFacile__StripePriceId`), quantità 1. Il totale atteso è **11900 centesimi, `eur`, IVA già inclusa**: il server non aggiunge IVA e l'esenzione che l'associazione dichiara per le proprie ricevute non si applica alla vendita CasaZen. `SuccessUrl` e `CancelUrl` sono `{ConcordatoFacile__BaseUrl}/app/long-rent/leases/{leaseId}/guided-contract?checkout=success` e `…?checkout=cancelled` (dal 25 ottobre, CF-END; il frontend tiene il vecchio `…/concordatofacile` come rinvio). La sessione ha una scadenza esplicita di 2 ore.

| Regola | Come |
|---|---|
| Metadati | `orgId`, `leaseId` e `userId` sulla sessione **e** sul PaymentIntent (`payment_intent_data.metadata`): Stripe non copia i metadati della sessione sul PaymentIntent e il webhook li legge dal PaymentIntent. **Nessuna chiave `kind`**: `StripeWebhookHandler.IsConcordatoFacilePayment` riconosce un pagamento CF dalla presenza di `leaseId` senza `kind` (con `kind` finirebbe agli altri gestori: affitti, prenotazioni, servizi dei fornitori) |
| Chi completa l'acquisto | Il webhook `payment_intent.succeeded` dell'endpoint di piattaforma (`/webhooks/stripe`, §8): solo se importo = 11900 e valuta = `eur` (altrimenti errore nel log e nessun accesso), se l'organizzazione dei metadati è quella dell'acquisto, e una sola volta (l'evento si reclama in `ProcessedStripeEvents` con sorgente `ConcordatoFacile`). L'acquisto passa a `Completed` con `PaidAt` e `StripePaymentIntentId` |
| Prima di creare una sessione (sempre prima di chiamare Stripe) | Il checkout di un contratto sta sotto un blocco consultivo di PostgreSQL: due clic simultanei producono **una sola** sessione e una sola riga `Pending` (la seconda riusa la prima). Contratto inesistente o di un'altra organizzazione: 404 `lease_not_found`. Acquisto già `Completed` o omaggio consumato per il contratto: 409 `concordatofacile_already_purchased`. Acquisto `Pending` con sessione **aperta**: la sessione è riusata. Sessione `complete` e `paid` con il webhook non ancora arrivato: se importo e valuta sono giusti l'acquisto passa subito a `Completed` e la risposta è 409; con importo diverso errore nel log, accesso non concesso, 409. Sessione `complete` ma non pagata (pagamento ritardato): 409, nessuna nuova sessione. Sessione scaduta o sparita: l'acquisto vecchio passa a `Failed` e se ne apre uno nuovo. Un `Pending` più vecchio di 2 ore e 10 minuti conta come chiuso per la cancellazione della bozza |
| Rimborsi | `charge.refunded` con rimborso **totale**: acquisto `Refunded` (`RefundedAt`); un rimborso parziale lascia lo stato; la pratica e i documenti non si toccano mai. Non c'è una politica commerciale di rimborso: il caso lo gestisce l'operatore dal Dashboard |
| Pagamento fallito o annullato | `payment_intent.payment_failed` e `payment_intent.canceled` dei pagamenti CF chiudono l'acquisto (`Failed`) **solo** se la sessione del PaymentIntent non può più riuscire (scaduta, o `complete` non pagata); una carta rifiutata con la sessione ancora aperta resta `Pending` (si può riprovare); un acquisto pagato o rimborsato non si tocca (CF-BE, chiude CF-FIX-FU2) |
| Stati dell'acquisto | `Pending`, `Completed`, `Failed`, `Refunded` |
| Scritture verso terzi | La sessione Stripe nasce solo da un clic dell'utente |

Il diritto di accesso (`ConcordatoFacileAccessService`) è: amministratore di piattaforma, oppure acquisto `Completed` del contratto, oppure omaggio consumato per il contratto. Il codice è in `ConcordatoFacileCheckoutService`, `ConcordatoFacileAccessService` e `ConcordatoFacilePaymentHandler` (backend #588, #590, #592, #602). Il runbook Stripe ha un rimando in [`stripe.md`](../../runbooks/stripe.md).

## 5. Il link firmato in dettaglio

`https://app.concordatofacile.it/?partner=casazen&leaseId=<uuid>&expiresAt=<data>&signature=<hex>&comune=<nome>&provincia=<sigla>`: l'indirizzo del modulo di produzione è sempre questo. Sul test, con `ConcordatoFacile__FormUrl` (CF-URL; vietata in Production dal validatore di avvio: assoluto, https, senza query né frammento) il link apre il **modulo di prova di CF**; con `FormMode=Simulated` (CF-SIM, solo test) apre `{BaseUrl}/guided-form`, la pagina che simula il modulo. Parametri e firma sono identici nei tre casi.

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

**Emissione.** `POST …/link` risponde `{ url }` con `Cache-Control: private, no-store`; senza diritto: 403. L'emissione è registrata come evento del contratto (`ConcordatoFacileLinkGenerated`: contratto e ora, **mai** URL o firma) e serve all'audit e alla rilettura breve del §6. CF toglie `leaseId`, `expiresAt` e `signature` dalla barra dell'indirizzo prima di qualunque script di terzi: il `leaseId` non è un'autenticazione dell'utente. Il codice di `ConcordatoFacileLinkService` è conforme alla v0.2 e al vettore, e dal 25 ottobre lo prova il test (CF-BE, chiude CF-DOC-FU9): `GenerateLink_PublicVectorOfTheSpecification_ProducesTheExactAddressAndSignature` passa i dati del vettore pubblico dal servizio vero (con un orologio fisso) e verifica indirizzo e firma esatti; la codifica del comune usa `HttpUtility.UrlEncode` (esadecimale minuscolo: `%3a`, non `%3A`; lo spazio diventa `+`). Nessun link prodotto dal server è ancora stato verificato da CF (§12).

## 6. L'API di stato di CF in dettaglio

**Indirizzo e chiave.** `https://stato.concordatofacile.it` è già il default del codice: `ConcordatoFacile__StatusApiUrl` **non si imposta**. Solo HTTPS, TLS 1.2 o superiore. Autenticazione `Authorization: Bearer <chiave>`: una chiave per partner e per ambiente (`cfsp_test_…`, `cfsp_live_…`), di 32 byte casuali, che CF consegna per e-mail con un Bitwarden Send a una sola apertura (password su un altro canale) quando il suo endpoint di prova è pronto; CF ne conserva solo l'impronta SHA-256 e più chiavi possono coesistere per la rotazione. In CasaZen: `ConcordatoFacile__StatusApiKey`. **Senza chiave** CF non viene chiamato: una sola riga di avviso nel log (nessun segreto), `GET …/status` risponde **200** con `available: false` (nessuna pratica, `stale: true`, `lastCheckedAt: null`, prossima lettura fra 5 minuti: l'esito «non disponibile» si ricorda 5 minuti nella stessa cache anche dopo un primo errore senza snapshot, CF-BE chiude CF-FIX2-FU1/FU2) e la scheda mostra l'avviso «non riusciamo a verificare lo stato»; il lookup `?practice=` risponde 404 `practice_not_found` senza chiamare CF e la pagina ripiega sulla scelta manuale; pagamento e link firmato non ne risentono. Con `FormMode=Simulated` lo stato viene dalla tabella `cf_simulated_practices` e non serve la chiave. Ogni risposta di CF ha `Cache-Control: no-store`.

| Chiamata | Risposta |
|---|---|
| `GET /v1/leases/{leaseId}/practices` | `{ "leaseId": …, "practices": [ … ] }`: le pratiche nate dal contratto, dalla più recente, di norma una, al massimo 20. Contratto sconosciuto, non di CasaZen o senza pratiche: sempre **200** con `"practices": []` |
| `GET /v1/practices/{practiceId}` | La singola pratica, con in più `leaseId` (**`null`** se non è collegata a un contratto). Pratica inesistente, di un altro partner o dell'altro ambiente: sempre lo stesso 404 `practice_not_found` |

Oggetto pratica: `practiceId` (`P-` più 16 esadecimali, non sequenziale: è il numero che l'utente vede nella conferma e che porta il pulsante), `status`, `statusChangedAt`, `submittedAt`, `comune` (come l'ha scelto l'utente, con il nome dell'indice di CF), `comuneDiffersFromLink` (`true`/`false`, **`null`** se il link non indicava il comune o la pratica non è collegata) e `closeReason` (solo con `ClosedWithoutAttestation`). Nessun dato personale. Errori: 400 `invalid_lease_id`/`invalid_practice_id`, 401 `unauthorized` (con `WWW-Authenticate: Bearer`), 404, 429 `rate_limited` con `Retry-After` in secondi. Campi, stati e codici d'errore sconosciuti si ignorano (CF può aggiungerne senza cambiare versione; un cambiamento incompatibile avrebbe il percorso `/v2`). La pratica compare pochi secondi dopo l'invio con lo stato `Received`; se la chiamata di CF non riesce la ripete ogni 15 minuti.

**Profilo di lettura (accettato da CF, v0.2 §4).**

- L'elenco del contratto si legge quando l'utente apre la scheda, con una copia privata di **15 minuti** condivisa fra utenti e schede della stessa organizzazione: al massimo **4 letture all'ora** per contratto consultato, zero per i contratti non consultati.
- Dopo l'emissione del link, finché la pratica non compare, **una lettura ogni 5 minuti per un'ora** (al massimo 12 all'ora per quel contratto), così il pulsante sparisce poco dopo l'invio.
- La singola pratica (lookup del ritorno `?practice=`) con lo stesso intervallo di 15 minuti, per ambiente e numero di pratica (1 minuto se CF non la conosce ancora, perché potrebbe essere stata inviata da pochi secondi; una lettura fallita non si memorizza).
- **Nessuna lettura in background**: nessun job, nessun timer fuori dalla pagina aperta. Come ordine di grandezza, 50 contratti consultati senza sosta sono al massimo 200 richieste all'ora. Il limite per chiave lo fissa CF con margine sopra questo profilo e lo rivede sulle pratiche effettive.
- **Non serve un ricevitore di eventi**: CF non manda webhook a CasaZen (la v1.3 li prevedeva; la v0.2 li ha sostituiti con l'API di stato).

**Come è fatto.** `GET /api/leases/{leaseId}/concordatofacile/status` (permesso `lease.read`) risponde `{ practices, available, stale, lastCheckedAt (nullable), nextUpdateAt }` con `Cache-Control: private, no-store`; prima di leggere la cache o di chiamare CF verifica che il contratto sia dell'organizzazione del chiamante (404 `lease_not_found` altrimenti, uguale per un contratto inesistente e per uno altrui, anche per l'amministratore di piattaforma). `ConcordatoFacileStatusCacheService` tiene lo snapshot nella **memoria del processo**, con chiave (ambiente, organizzazione, contratto): un semaforo per contratto accorpa le letture concorrenti; se CF fallisce si tiene l'ultimo snapshot valido, segnato `stale` e servito per **5 minuti** senza richiedere di nuovo a CF (`nextUpdateAt` dice quando); un errore non diventa mai «nessuna pratica» né uno stato finale; un risultato con `statusChangedAt` più vecchio di quello già noto non lo sostituisce. Il client (`ConcordatoFacileApiClient`) legge l'oggetto `{ leaseId, practices }` e `comuneDiffersFromLink` nullable; ogni risposta non riuscita diventa un errore che porta il `code` del problema (una 401, chiave mancante, errata o dell'altro ambiente, si registra come errore nel log); ritenta 5xx, timeout ed errori di rete 3 volte con attese di 1, 2 e 4 secondi (30 secondi per tentativo, nessun circuit breaker); un 429 con `Retry-After` fino a 30 secondi viene atteso e ritentato una volta, oltre o senza intestazione è un errore. Nella risposta di CasaZen il `null` di `comuneDiffersFromLink` esce come `false`. Il frontend (`useCfStatus`) rilegge alla scadenza di `nextUpdateAt`, solo con la pagina aperta e in primo piano. Queste correzioni vengono dal task CF-FIX2 (backend #611, 25 ottobre), che ha anche registrato nel contenitore il servizio della cache: prima, con l'interruttore acceso, ogni azione del controller falliva già nella sua creazione (§13).

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
| `ConcordatoFacile__FormMode` | `Simulated` (dal 25 ottobre: modulo e API di stato simulati; tabella `cf_simulated_practices`, endpoint `api/cf-simulator/*`, pagina `/guided-form`) oppure `Real`/non impostata per usare CF | **mai impostata**: `Simulated` fa fermare l'avvio quando `ASPNETCORE_ENVIRONMENT` è `Production` | CasaZen (solo test) |
| `ConcordatoFacile__FormUrl` | Indirizzo del **modulo di prova di CF**, assoluto https senza query né frammento (rimuovere `FormMode` o metterlo a `Real`; serve anche `StatusApiKey` `cfsp_test_…`) | **mai impostata** (rifiutata all'avvio in Production) | CF dà l'indirizzo (Send); CasaZen lo imposta |
| `ConcordatoFacile__Simulator__StatusTtlSeconds` | Facoltativa, 1–3600 (default 5): quanto la cache dello stato tiene ciò che ha letto dal simulatore | non impostare | CasaZen (solo test) |
| `Legal__Documents__Privacy__Version` e `__EffectiveAt` | `2026-10-v2` e la data di entrata in vigore: attivano l'informativa che nomina CF (§12) | da impostare con il rilascio e **dopo** aver sostituito i segnaposto dei dati d'azienda | CasaZen (PO) |
| `Legal__Controller__{Name,Address,VatId,Pec,PrivacyEmail}` | Oggi valori espliciti «da definire» (e-mail `….invalid`): CasaZen non ha ancora dati d'azienda | **segnaposto da sostituire prima del lancio** | CasaZen (PO) |
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

**Test** (Railway `test`, Stripe in modalità test). Prima: `GET /api/public/features` mostra `"concordatoFacile": true`; le variabili del §7 sono impostate; `LinkSecret` è il segreto del vettore. Dal 25 ottobre il test ha `ConcordatoFacile__FormMode=Simulated`: **il giro intero si fa sul test senza CF** (`GET /api/cf-simulator/link` risponde 200 con `missing_parameters` quando il simulatore è acceso, 404 con `Real`).

**Giro intero con il modulo simulato** (un amministratore di piattaforma con un'organizzazione di locatore salta il pagamento; per provare il pagamento serve un utente non amministratore con la carta `4242`): «Nuovo contratto» → «Canone concordato» → «Parto da zero» → la casa → la scheda → spuntare l'informativa → «Procedi con la creazione del contratto» → `/guided-form` («Ambiente di prova», «Collegamento valido») → «Invia la pratica» → «Torna al contratto» (stato della pratica) → nel pannello di prova portare lo stato avanti e scaricare i tre PDF d'esempio (contratto 3+2, attestazione, ricevuta) → «Carica i documenti» (lettura, «Completa e importa il contratto», importazione) → caricare attestazione e ricevuta → «Dichiara la registrazione» con «Usa la ricevuta già caricata» → `Registered`. Lo stesso percorso è provato in CI da `e2e/gj-l3/concordato-round.spec.ts` (CF-L3; accesso da amministratore e con pratica omaggio; il pagamento con carta non è in CI perché le chiavi di prova di Stripe non sono nei segreti, CF-L3-FU1). Con il modulo di prova vero di CF: togliere `FormMode`, impostare `FormUrl` e, quando arriva, `StatusApiKey`.

1. **Pagamento.** Nuovo contratto, canone concordato, «Parto da zero», un immobile, pagina del servizio, «Paga 119 euro e continua» con la carta `4242 4242 4242 4242`. Atteso: ritorno a `…/guided-contract?checkout=success` (poi il modulo si apre da solo quando l'accesso è attivo); sul PaymentIntent, in Stripe, i metadati `leaseId`, `orgId` e `userId` e **nessun** `kind`; la consegna di `payment_intent.succeeded` alla destinazione di piattaforma risponde 200 e l'acquisto passa a `Completed`; un secondo clic su «Paga» mostra «Hai già accesso…» e non apre Stripe.
2. **Link.** «Procedi con la creazione del contratto» (dopo la casella dell'informativa) apre il modulo nella stessa scheda. Il link ha i sei parametri del §5; qualche link prodotto dal server si manda a CF perché lo verifichi come i tre di esempio (v0.2 §7).
3. **Stato.** Con la chiave di prova di CF e le sue pratiche d'esempio nei vari stati: sezione «Stato pratica» corretta in ogni stato, avviso di dato non aggiornato se CF non risponde.
4. **Ritorno.** Aprire `/app/long-rent/documents/upload?practice=P-0123456789abcdef` (l'esempio di CF) e i casi del §3.7: pratica collegata, non collegata, di un'altra organizzazione, formato errato, servizio spento. Sulla bozza «da zero» caricare un contratto di prova (PDF digitale e una scansione): lo stato della lettura, «Completa e importa il contratto», la procedura guidata sul primo passo che manca, l'importazione («Firmato», stesso `leaseId`).
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
| «Senza chiave di stato si torna un elenco vuoto» (ADR-008-R22) | Senza chiave `GET …/status` risponde 200 con `available: false` e nessuna pratica: la scheda lo dice, e non lo prende per «nessuna pratica inviata» (se il modulo è già stato aperto l'avviso chiede di non riaprirlo) |
| «Il segreto di prova funziona anche in produzione» | Il modulo di produzione di CF lo rifiuta |
| «Le destinazioni webhook vanno create su `2025-12-15.clover`» | Il Dashboard non la offre più: si sceglie la più recente (§8) |
| «I dati scritti nel modulo di CF sono trattati nello Spazio economico europeo e non servono ad altro» | L'informativa di CF (Confproprietà, v4.14 del 29/09/2026) non lo dice: usa fornitori negli Stati Uniti (accordo UE-USA o clausole standard) e strumenti di intelligenza artificiale per leggere i documenti; la nostra informativa rinvia alla sua (§12) |
| «Il link del modulo si apre in una nuova scheda» | Si apre nella **stessa** scheda: il ritorno è il «indietro» del browser o l'e-mail di CF |
| «Si può riaprire il modulo finché si vuole» | Dopo l'invio un nuovo link apre una compilazione nuova e quindi una seconda pratica: con una pratica non chiusa la scheda mostra lo stato, non il pulsante |
| «Il nome di CF può comparire nell'app» | Mai: né nei testi, né nell'indirizzo, né nel nome del prodotto Stripe («Contratto a canone concordato»); sta solo nei testi legali |

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
| La bozza «Parto da zero» è una bozza **importata** (il contratto lo scrive e lo fa firmare CF fuori dai nostri modelli): si compila leggendo il contratto che CF consegna e si importa con il PDF già caricato, stesso `leaseId` (chiude LR-06a-FU4 / CF-DOC-FU10) | 25 ottobre (CF-IMPORT, default del coordinatore) |
| Interruttore `Features:ConcordatoFacile`: acceso sul test, spento in produzione fino al collaudo e all'accordo sull'inquadramento privacy; nessun altro flag | 25 ottobre |
| Lettura dei documenti locale (PdfPig e Tesseract), nessun LLM né servizio esterno; si compila solo il vuoto | 25 ottobre (default NL-DOCS) |
| Il transitorio si fa in CasaZen solo dove `availableTypes` lo dice; il modello ufficiale è una bozza finché il PO non lo approva | 25 ottobre |
| Funzioni che scrivono a terzi (e-mail, solleciti, richieste di pagamento): solo su azione esplicita dell'utente | regola della wave |
| «Parto da zero» atterra sulla **scheda del contratto**, che è il centro del giro e la pagina di stato; la pagina di pagamento si raggiunge dal pulsante della scheda e solo senza diritto; con il diritto (amministratore, pagato, omaggio) il pulsante porta **dritto al modulo**, nella stessa scheda del browser | 25 ottobre (prova a mano dell'utente) |
| Prima del modulo l'utente accetta l'informativa privacy di CasaZen (che nomina CF come destinatario); il server rifiuta il link senza l'accettazione della versione corrente; il consenso di CF lo raccoglie CF nel suo modulo | 25 ottobre |
| Con una pratica non chiusa la scheda mostra lo stato e non il pulsante del modulo (regola di CF) | 25 ottobre |
| Nessun nome del fornitore e nessuna parola «partner» in nessun testo dell'app, nell'indirizzo e nel prodotto Stripe; solo nei testi legali | 25 ottobre |
| Sul test il modulo di CF è simulato (`FormMode=Simulated`) finché CF non consegna il suo ambiente di prova; mai in produzione | 25 ottobre |
| A registrazione ultimata di un concordato: portare contratto, attestazione e ricevuta al commercialista e al comune per le agevolazioni; l'attestazione si chiede nel wizard senza bloccarlo | 25 ottobre |
| Informativa con CF titolare autonomo (R24 non concordata): testo `2026-10-v2` approvato dal legale dell'utente per il paragrafo 6.3; CasaZen non ha ancora dati d'azienda: pubblicazione con «da definire» sul test, sostituzione prima del lancio | 25 ottobre |

## 12. Accordi con CF e punti aperti con CF

**Scambio con CF** (risposte di CasaZen del 22 ottobre, esito di CF del 23 ottobre, recepiti nella v0.2).

1. **Firma.** Generazione sul server al clic, scadenza 15 minuti, `leaseId` minuscolo, UTC al secondo con `Z`, HMAC-SHA256 sui valori decodificati, query codificata. CF ha verificato con il codice del suo modulo i tre link di esempio: le firme sono valide. Monza e Reggio nell'Emilia sono nell'elenco comuni del modulo; **Seveso no**: il modulo lo lascia scritto nella ricerca e la pratica resta legata al contratto.
2. **Frequenza.** Profilo del §6 accettato, compresa la rilettura ogni 5 minuti; il limite per chiave lo fissa CF sopra il profilo.
3. **Pulsante.** Percorso `https://casazen.space/app/long-rent/documents/upload` con `?practice=P-…`. CF ha già impostato il pulsante su questo indirizzo e lo tiene spento finché CasaZen non conferma il collaudo (redirect dominio/www, login, parametri preservati, pratica risolta, contratto autorizzato e confermato, upload, riapertura e download: la sola raggiungibilità HTTP della pagina non lo prova).
4. **Chiavi.** Bitwarden Send a una sola apertura, quando l'endpoint di prova è pronto, al referente CasaZen (indirizzo già comunicato a CF); la password su un altro canale (CF propone un SMS al numero dal quale scrive CasaZen). In prova resta il segreto del vettore; in produzione segreto di firma e chiave sono diversi e li genera CF.
5. **Servizio.** Solo il completo; se un giorno per CasaZen ci fossero altri servizi, CF avvisa prima e aggiunge un campo nell'endpoint.

**Da fare per CasaZen** (v0.2 §7 e messaggio di CF del 23 ottobre): (a) qualche link prodotto dal comando del server, da far verificare a CF come i tre di esempio (il comando è pronto: «Accedi alla tua pratica» sul test con il segreto del vettore); (b) la conferma del collaudo della pagina di caricamento con `practice` (ora in `develop`): fino ad allora il pulsante di CF resta spento; (c) la risposta sul canale per la password delle chiavi; (d) il `leaseId` del contratto per la pratica inviata dal modulo senza link firmato, che CF collega a mano (UUID minuscolo di un contratto CasaZen reale, comunicato a CF al momento dell'invio).

**Da chiudere con CF:** la **chiave dell'API di stato di prova** (`cfsp_test_…`) e l'**indirizzo e l'accesso del suo ambiente di prova del modulo** (promessi il 25 ottobre con un Send a una sola apertura e password per SMS; le pratiche di prova non si lavorano), poi le copie in PDF di una pratica conclusa con i dati oscurati (attestazione, contratto, ricevuta) con cui provare la lettura su documenti veri; la soglia per chiave (la fissa CF); la riconciliazione delle pratiche senza `leaseId` e dei costi; la presenza di Seveso e degli altri comuni pilota nell'elenco comuni del modulo e la lavorazione normale delle pratiche per comuni fuori elenco; l'**accordo sull'inquadramento privacy (R24 della v1.3)**, ancora non concluso (vedi sotto); come riconoscere le pratiche di prova fatte in produzione (§9).

**Privacy: l'informativa di CasaZen e quella di CF (25 ottobre).** CF è **Confproprietà – Confederazione della Proprietà Edilizia** (Via Armando Diaz 8, 80134 Napoli; codice fiscale 94117210636; info@confproprieta.org; PEC confproprieta@pec.it), titolare di ConcordatoFacile.it; la sua informativa («Regolamento delle attività di assistenza agli associati e informativa privacy», versione 4.14 del 29/09/2026, `https://legal.concordatofacile.it`) dice che CF è il **titolare** (i suoi fornitori sono responsabili: fra gli altri sono negli Stati Uniti, con accordo UE-USA o clausole standard), che usa strumenti di IA per leggere i documenti caricati e preparare le bozze (dati limitati a quanto serve, nessun addestramento secondo le condizioni dei fornitori), che i dati della pratica si conservano fino a 24 mesi e i contratti 10 anni, e che il consenso dell'utente si raccoglie **nel suo modulo** (art. 31: casella di presa visione e accettazione). Quindi: l'informativa di CasaZen `2026-10-v2` (`Casazen.Web/LegalDocuments/privacy/2026-10-v2.{it,en}.html`; la v1 non cambia, è la prova di ciò che gli utenti hanno accettato) nomina CF al paragrafo **6.3** con la sua identità legale come **titolare autonomo** dei dati che l'utente scrive nel suo modulo (non sub-responsabile: non è nell'elenco dei sub-responsabili né nel DPA), spiega che a CF vanno solo l'identificativo del contratto, il comune e la provincia e che a CasaZen tornano solo numero della pratica, comune, stato e date, e rinvia alla sua informativa per fornitori, trasferimenti fuori dal SEE, IA e conservazione; il paragrafo 3.5 dice che si registra l'accettazione per contratto; il 4.4 che il contratto consegnato da CF si legge sui nostri sistemi senza servizi esterni né IA. La frase della prima bozza «dati nel SEE, nessun altro scopo» fu **tolta** perché l'informativa di CF non la conferma. Il legale dell'utente ha dato l'OK al paragrafo 6.3. Se l'accordo R24 facesse di CF un responsabile cambiano le frasi «come titolare autonomo» e «non è un nostro sub-responsabile», il catalogo dei sub-responsabili e il DPA. **Attivazione:** l'informativa non si attiva da sola: serve `Legal__Documents__Privacy__Version=2026-10-v2` e `__EffectiveAt` per ambiente, più i dati del titolare (`Legal__Controller__*`), altrimenti la pagina resta «in preparazione» (regola «fail-closed»); da quel momento ogni utente dell'ambiente deve riaccettare **la sola privacy**. Sul test sono impostati valori «da definire» (CasaZen non ha ancora dati d'azienda); **in produzione si imposta con il rilascio, e i segnaposto vanno sostituiti prima del lancio**. Documentazione: [`legal-documents.md`](../../runbooks/legal-documents.md) §5.7.

**Accordi economici e adempimenti.** Il pagamento dell'utente a CasaZen (119 € IVA compresa) è distinto dal saldo a CF: l'utente non paga CF, CasaZen paga pratica per pratica dopo l'invio dei firmati. CF ha indicato importi finali di **69 e 89 € senza IVA**, con ricevuta al locatore per quota associativa e contributo, operazione non commerciale esente IVA ai sensi dell'art. 4 D.P.R. 633/72 secondo la dichiarazione del fornitore (non si applica alla vendita CasaZen). Per CasaZen vale solo il servizio completo: la tariffa da riconciliare è quella del completo (da confermare) e la modalità di riconciliazione è da concordare; le condizioni di annullamento o di pratica non attestabile sono da confermare. Secondo CF l'associazione registra telematicamente come intermediario a nome del locatore, senza delega firmata dalle parti, e la cedolare si sceglie in registrazione. Registro e bollo sono esclusi: con la cedolare non sono dovuti, altrimenti si addebitano sul conto del locatore con l'IBAN raccolto da CF nel proprio canale, mai nei link. I tempi indicati sono pochi giorni dopo i firmati e il pagamento, entro il termine dalla stipula richiamato dal partner: non uno SLA garantito di 48 ore. La comunicazione IMU al Comune resta separata e non automatizzata. Veste personalizzata del modulo (terracotta `#e2622d`, inchiostro `#1f3a3d`, lockup con il nome in tracciati): CF ne comunicherà la data dopo aver ricevuto marchio e dominio; non blocca una pratica pilota concordata.

## 13. Limiti e debiti noti

Gli ID `CF-DOC-FU…` sono proposti da questo documento; gli altri vengono dai rapporti dei task (`redesign/docs/wave/results/`, fuori dal repository).

**A. Difetti del codice trovati leggendo il codice contro la v0.2 (task CF-DOC), corretti il 25 ottobre dal task CF-FIX2 (backend #611).** Erano quattro, tutti del backend:

1. Il servizio della cache dello stato (`IConcordatoFacileStatusCacheService`) **non era registrato nel contenitore**: con l'interruttore acceso ogni azione di `ConcordatoFacileController` (checkout, accesso, omaggio, link, stato, lookup) falliva già nella creazione del controller. I test non lo vedevano (costruivano il controller a mano). Ora è registrato, con una prova che ogni dipendenza del costruttore è nota al contenitore.
2. Il client leggeva l'elenco come array (`List<PracticeDto>`, da ADR-008-R6) e `PracticeDto.ComuneDiffersFromLink` come booleano non nullable, mentre la v0.2 risponde `{ leaseId, practices }` e manda `null` (verificato con un controllo usa-e-getta di System.Text.Json: `JsonException` in entrambi i casi). Con la chiave vera `GET …/status`, e il lookup di una pratica non collegata, avrebbero risposto 500. I test mockavano il client.
3. `GET …/status` e `POST …/consume-gift` non verificavano che il contratto fosse dell'organizzazione del chiamante (ADR-008-R11 e ADR-004-R6): chi conosceva il `leaseId` di un altro tenant ne leggeva le pratiche, e l'omaggio si poteva spendere su un contratto altrui. Ora 404 `lease_not_found`, uguale per un contratto inesistente e per uno altrui, prima di leggere la cache o chiamare CF e senza eccezioni per l'amministratore di piattaforma.
4. Il lookup `practices/{id}/lease` non era in cache e dopo un errore lo snapshot non aggiornato conservava la scadenza passata (ogni apertura rileggeva CF subito). Ora: cache di 15 minuti (1 minuto per una pratica che CF non conosce ancora) e 5 minuti di attesa dopo un errore.

Restano (CF-FIX2-FU1, CF-DOC-FU11 e CF-DOC-FU9 sono stati chiusi il 25 ottobre dal task CF-BE: stato senza chiave e primo errore ricordato 5 minuti; `consume-gift` con `lease.create`; prova della firma sul servizio vero):

| ID | Debito | Effetto |
|---|---|---|
| CF-DOC-FU8 | Lo snapshot sta nella memoria del processo | Con più repliche del servizio il limite di 15 minuti vale per replica (oggi un solo servizio `backend` per ambiente; il numero di repliche non è stato verificato) |

**B. Frontend: tutti chiusi il 25 ottobre da CF-FLOW e CF-END** (pannello della scheda che legge lo stato e nasconde il pulsante, avviso «non riusciamo a verificare lo stato», «registro e bollo esclusi» sulla pagina di pagamento, passo «è questo il contratto?» sulla pagina `?practice=`, «Pagamento completato» solo con l'accesso reale, rifiniture del wizard NL-FLOW-FU1..3 e NL-DOCS-FU8..9). Non resta nulla di questa tabella:


**Chiusi dal task CF-IMPORT (25 ottobre).** CF-DOC-FU10 (= LR-06a-FU4), fine del percorso «da zero»: la bozza è una bozza importata, il contratto che CF consegna si legge, la bozza si completa e si importa con il PDF già caricato (§3.3); e CF-FIX-FU5: la rotta `POST api/leases/drafts/concordatofacile`, `leasesApi.createCfDraft` e `useCreateCfDraft` sono il percorso di «Parto da zero».

**C. Pagamento, omaggio e piani.** Chiusi il 25 ottobre da CF-BE: CF-FIX-FU2 (pagamento fallito o annullato), CF-FIX-FU3 (due clic: blocco per contratto), CF-IMPORT-FU1 (acquisto `Pending` scaduto). Restano:

| ID | Debito |
|---|---|
| BIENNIAL | La vendita dei piani a 24 mesi (e il regalo solo con sottoscrizione `active`/`trialing`) non è in `develop`: i Price a 24 mesi sono provvisori; il regalo si assegna solo con `customer.subscription.created` su un Price di `BiennialPriceIds` (un cambio piano verso il biennale non lo assegna) o a mano |
| CF-DOC-FU6 | I dati di attestazione e ricevuta di registrazione non si leggono dai documenti: sono archiviati e consultabili, le sezioni Attestazione e Registrazione non si popolano |

**D. Lettura dei documenti (NL-DOCS).** Il cammino OCR vero (`pdftoppm` e `tesseract` su una scansione) ha una prova in CI (`ContractScanOcrTests`, CF-END) e il contratto di esempio del simulatore, un PDF digitale, è letto dal parser vero (CF-SIM); il parser non è però mai stato provato su un **contratto vero di CF**: CF ha promesso copie in PDF di una pratica conclusa con i dati oscurati (§12) con cui provarlo (i dati oscurati limitano ciò che si legge: nomi e codici fiscali). Rimandati: NL-DOCS-FU1 società come parti; FU2 altre cittadinanze; FU3 dati dell'unità, garante, ISTAT, spese; FU4 canone che cambia negli anni e deposito scritto in lettere; FU5 indirizzo confrontato con l'anagrafica del comune; **FU6 ripulire le vecchie righe `LeaseDocument.ExtractionResult` di versione 1, che contengono in chiaro nomi di locatore e conduttore, canone, deposito e date** (serve una decisione sull'`UPDATE` mirato); FU7 prova automatica del cammino (PDF-scansione di prova e una spec L2); FU8 «Continua a mano» mentre la lettura finisce; FU9 il segno «letto dal contratto» riappare dopo un ricarico.

**E. Transitorio (TR).** TR-FU1 calcolatore delle fasce dell'accordo territoriale e controllo del canone (comuni oltre 10.000 abitanti); TR-FU2 accordo territoriale per comune; TR-FU3 residenza, cittadinanza e nascita delle parti e testo condizionale nel formato dei modelli; TR-FU4 mostrare l'esigenza nella scheda del contratto. Il modello è una bozza non approvata (§3.9).

**F. Nuovi limiti noti (25 ottobre).** CF-FLOW-FU4: il modulo sta sul sito di CF e il suo aspetto e il suo indirizzo sono di CF (non si possono nascondere); CF-FLOW-FU6: «Riprendi la compilazione» può restare qualche minuto dopo un invio non ancora visibile nello stato (regola di CF, §3.6); CF-L3-FU1: il percorso a pagamento con carta e webhook non è nei test automatici (mancano le chiavi di prova di Stripe nei segreti della CI) e non è mai stato eseguito dall'utente: un amministratore non paga; CF-L3-FU3/FU5: il giro in CI non esercita il testo legale vero né telefono e inglese; CF-SIM-FU1..3: pulizia o conservazione delle pratiche simulate (oggi `DELETE FROM cf_simulated_practices;`), avanzamento degli stati nel tempo, ritiro del simulatore quando CF avrà il suo ambiente; CF-END-FU1/FU2: dal blocco «ora fai valere le agevolazioni» non si richiamano ancora la bozza di comunicazione IMU al Comune e le associazioni per l'attestazione, che restano nella scheda «Dati» (candidate per la guida del contratto, LR-GUIDE-1); CF-DOC-FU8: lo snapshot dello stato sta nella memoria del processo (il numero di repliche non è verificato). **Legale:** l'informativa `2026-10-v2` è scritta ma non attiva su nessun ambiente e CasaZen non ha dati d'azienda: sul test valori «da definire», da sostituire prima del lancio.

## 14. Glossario degli ID e mappa del codice

| ID | Che cosa è |
|---|---|
| `ADR-004`…`ADR-009`, `ADR-00x-Rn` | Le decisioni del run Helix R20261023-1107 (23 ottobre) e le loro regole numerate, citate nei commenti del codice e dei test. Sono **etichette**, non requisiti: il punto di verità è questo documento (§15) |
| `NF-01`…`NF-08`, `AC-NFnn-m` | Requisiti del run Helix: NF-01 bozza minima; NF-02 offerta e pagamento; NF-03 pratica omaggio; NF-04 bypass dell'admin; NF-05 link firmato e punto d'ingresso; NF-06 ritorno con ricerca della pratica; NF-07 lettura dei documenti; NF-08 API di stato |
| `E01/F01/US01`, `e01-f02-us01-t01`… | Compiti di consegna Helix (epica, funzione, storia, task) delle PR backend #581–#598 e frontend #338–#343 |
| `CF-FIX` (backend #602, frontend #347) | Metadati sul PaymentIntent, niente doppio pagamento, interruttore, pagina `?practice=`; debiti `CF-FIX-FU1..5` (il FU1, cache del lookup, è corretto da CF-FIX2) |
| `CF-FIX2` (backend #611) | Servizio della cache dello stato registrato nel contenitore, forma della risposta di CF, controllo dell'organizzazione su `status` e `consume-gift`, cache del lookup, attesa dopo un errore; debito `CF-FIX2-FU1` |
| `NL-FLOW` (frontend #346) | «Nuovo contratto»: prima il tipo, poi il come; debiti `NL-FLOW-FU1..3` |
| `NL-DOCS` (backend #604 e #608, frontend #348) | Lettura locale dei documenti del contratto e bozza che si compila; debiti `NL-DOCS-FU1..9` |
| `TR` (backend #609, frontend #349) | Tipo transitorio con il modello ufficiale (bozza non approvata); debiti `TR-FU1..4` |
| `CF-IMPORT` (backend #615, frontend #350, documenti #618) | La bozza «da zero» è una bozza importata protetta dalla cancellazione; «Completa e importa il contratto» |
| `CF-FLOW` (frontend #351) | Scheda come centro del giro e pagina di stato, un solo gesto che apre il modulo nella stessa scheda, pagina di pagamento a parte (`/guided-contract`), nessun nome del fornitore nei testi |
| `CF-BE` (backend #632) | Accettazione dell'informativa per contratto, informativa `2026-10-v2`, stato senza chiave, acquisti scaduti, blocco del checkout, `consume-gift` con `lease.create` |
| `CF-SIM` (backend #631, frontend #352) | Modulo e API di stato simulati per il test (`FormMode=Simulated`) |
| `CF-END` (backend #637, frontend #353 e #355) | Registrazione dichiarata con la ricevuta già caricata, nessuna registrazione doppia per i contratti del servizio, rifiniture del wizard, attestazione e seguito della registrazione |
| `CF-L3` (frontend #354) | Test Golden Journey L3 dell'intero giro con il simulatore |
| `CF-URL` (backend #638) | `ConcordatoFacile__FormUrl` per il modulo di prova di CF (solo test) |
| `CF-LEGAL` (backend #639) | Identità legale di CF e rinvio alla sua informativa nel paragrafo 6.3 |
| `LR-12`, `LR-13`, `LR-APE`, `LR-DEP` | Compiti della catena Affitti lunghi che toccano lo stesso contratto: bozza guidata, importazione di un contratto già firmato, codice e classe dell'APE, deposito |
| `PL-10`, `A3-39` | Idempotenza dei webhook Stripe; tolleranza sulla versione API degli eventi |
| `R24` | Punto della v1.3 di CasaZen: inquadramento privacy da definire con CF prima delle pratiche reali |
| `P-…` | Numero di pratica di CF: `P-` e 16 caratteri esadecimali |
| `leaseId` | UUID reale e stabile di `LeaseContract`, anche per una bozza; non è l'ID della casa, del pagamento o della pratica |
| `cfsp_test_…`, `cfsp_live_…` | Prefissi delle chiavi dell'API di stato di CF (prova, produzione) |

**Dove sta nel codice.** Backend: `ConcordatoFacileController`, `AdminConcordatoFacileController`, `LeasesController.CreateCfDraft` (`Casazen.Web/Controllers`); servizi `ConcordatoFacileCheckoutService`, `ConcordatoFacileAccessService`, `ConcordatoFacileGiftService`, `ConcordatoFacileLinkService`, `ConcordatoFacileStatusCacheService`, `ConcordatoFacileAdminService` (`Casazen.Infrastructure/Services`); `ConcordatoFacileApiClient` e `ConcordatoFacilePaymentHandler` (`Casazen.Infrastructure/External`, il secondo chiamato da `StripeWebhookHandler`); entità `ConcordatoFacilePurchase`, `ConcordatoFacileGift`, `LeaseContract.IsConcordatoFacileSource` e gli eventi `ConcordatoFacileLinkGenerated`, `ConcordatoFacileGiftConsumed`, `ConcordatoFacileAdminAccess`; opzioni `ConcordatoFacileOptions` con il validatore di avvio; flag in `FeatureFlags.cs`. Frontend: `lease-create-page.tsx`, `concordato-draft-start.tsx`, `start-way-choice.tsx`, `documents-start.tsx` (`src/features/leases`), `concordatofacile-offer-page.tsx` e `cf-status-section.tsx` (`src/pages/long-rent/leases`), `lease-detail-page.tsx` (pannello e avviso di bozza), `lease-documents-upload-page.tsx` (ritorno), `cf-import-panel.tsx` e `lib/cf-import.ts` (il contratto consegnato da CF: stato della lettura e «Completa e importa il contratto», nella pagina di ritorno e nella scheda), `use-concordatofacile.ts` (query).

## 15. Documento → stato

| Documento | Stato | Perché |
|---|---|---|
| `docs/integrations/concordatofacile/README.md` | **corretto** | Questo documento (revisione 12) |
| `Sessions/business-analysis-canone-concordato.md` | **corretto** | Requisiti e accettazione, revisione 12, allineata a questo documento |
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
| `docs/runbooks/concordatofacile-simulator.md` | **nuovo** | Il simulatore del modulo e dell'API di stato per il test (CF-SIM) |
| `docs/runbooks/legal-documents.md` §5.7, `Casazen.Web/LegalDocuments/privacy/2026-10-v2.*` | **nuovi** | Informativa che nomina CF e la sua attivazione per ambiente (CF-BE, CF-LEGAL) |

Gli appunti di lavoro fuori dal repository (l'area di lavoro di Helix, i registri della wave) non sono riferimenti e non vanno usati per il processo CF.
