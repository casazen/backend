# Analisi tecnica per Helix — integrazione minima ConcordatoFacile

Versione 5.0 · 21 ottobre 2026 · Documento unico e autosufficiente per Helix. Il flusso di riferimento è quello concordato qui: tipo di contratto come primo passo, bozza minima con leaseId, contratto redatto da CF e importazione dei documenti definitivi. La pagina di caricamento sulla casa viene realizzata separatamente nelle PR frontend #314/#315; gli altri requisiti di questa analisi sono da implementare con Helix e non attestano funzionalità già rilasciate. Documentazione del flusso: `docs/integrations/concordatofacile/README.md`. Sostituisce le precedenti analisi e proposte tecniche per questo flusso. I documenti destinati al fornitore restano materiale di corrispondenza, non ulteriori requisiti da sommare. Questa revisione contiene istruzioni: non implementa funzionalità.

## 1. Obiettivo e perimetro minimo

Implementare il flusso seguente nell'app esistente:

1. Chiedere come prima cosa il tipo di contratto. Per il concordato creare subito una bozza minima assegnata alla casa e ottenere il vero leaseId, prima di chiedere parti, date, canone o APE.
2. Presentare il servizio e incassare **119,00 euro IVA compresa per singola pratica di contratto**, prima di aprire CF. Ammettere accesso diretto per admin CasaZen e una pratica omaggio per abbonamento biennale, con diritto governato dal database.
3. Dopo la verifica del diritto di accesso, aprire il modulo ConcordatoFacile dal contratto CasaZen con quattro parametri.
4. Ricevere l'utente dal bottone nelle email CF e consentirgli di caricare contratto definitivo, attestazione e ricevuta di registrazione: conservarne le copie e popolare automaticamente i dati/sezioni del contratto corrispondenti.
5. Mostrare lo stato della pratica letto dal backend CasaZen attraverso l'endpoint CF, appena il fornitore ne consegna il contratto tecnico.

CF gestisce dati, documenti, verifiche, contratto, attestazione quando necessaria, firme e registrazione se richiesta. Email operative e consegna documenti sono tra CF e utente. CasaZen non vuole la prima email di pratica ricevuta, quella finale né copie/inoltri: l'unico canale per conoscere lo stato è l'endpoint CF.

La parte documentale può essere quasi tutta riuso. Pagamento e pratica omaggio richiedono invece controllo server, persistenza e migrazione: il perimetro non è più limitato a tre link. La lettura dello stato richiede una piccola integrazione server, ancora dipendente da informazioni CF mancanti. Riusare storage e controlli esistenti; estendere il modello per documenti collegati al contratto e importazione dei dati. Non costruire un processo di firma o una nuova registrazione fiscale: li gestisce il partner.

## 2. Stato verificato del codice e componenti da riusare

Rilevazione nei checkout `casazen/frontend` commit `e27b2fb7` e `casazen/backend` commit `6481cb0f`. Prima di modificare, leggere AGENTS.md e verificare i corrispondenti file sulle branch correnti: i riferimenti sono una mappa di partenza, non istruzioni di sovrascrittura.

| Esigenza | Riuso verificato |
|---|---|
| Routing applicativo | frontend: `src/config/route-manifest.ts`; dettaglio contratto `/app/long-rent/leases/:id` |
| Contratto e casa associata | frontend: `src/queries/use-leases.ts`, `src/api/leases.api.ts`; backend: `Casazen.Core/DTOs/Leases/LeaseDtos.cs`, campo `PropertyId` |
| Pagina della casa long-rent | frontend: `src/features/properties/long-rent/long-rent-property-detail-page.tsx` |
| Archivio documenti casa | frontend: `src/features/properties/components/property-documents-section.tsx` |
| Caricamento esistente | frontend: `src/features/properties/components/document-upload-dialog.tsx`, `src/queries/use-properties.ts`, `src/api/properties.api.ts` |
| API archivio | backend: `Casazen.Web/Controllers/PropertiesController.cs`: `GET/POST /api/properties/{id}/documents`, download e cancellazione già disponibili |
| Controlli upload | Il POST esistente verifica autenticazione, policy SharedPropertyWrite, accesso alla casa e tipo documento. Conservare queste regole e le validazioni del servizio di storage. |
| Modello archivio | backend: `Casazen.Core/Entities/PropertyDocument.cs`: `PropertyId`, `OrgId`, tipo, file e autore. Non contiene un `LeaseId`. |
| Tipo generico | L'upload frontend offre `Other`; usarlo come default per i documenti CF nel minimo rilascio, evitando CIN o APE predefiniti. |
| Ritorno dopo login | frontend: `src/lib/auth-return-to.ts`, `currentReturnTo` e `safeReturnTo`; integrare il flusso di accesso esistente, senza nuova autenticazione. |
| Informazioni attestazione | frontend: `src/features/leases/components/attestation-guidance-panel.tsx`; non sostituire il calcolatore o le altre informazioni normative. |

Il leaseId è l'ID reale e stabile della bozza, non quello della casa, di un pagamento o di una pratica CF. Resta identico quando arrivano i dati definitivi: non creare un secondo contratto all'importazione.

Il modello attuale PropertyDocument non basta da solo per l'obiettivo finale: servono documenti collegati anche alla locazione, tipizzati e consultabili dalle sezioni del contratto. Riusare storage e API dove possibile, aggiungendo relazione/versioni/metadati ed eventuali migrazioni necessarie. L'upload generico sulla casa è il primo passo già isolato nelle PR frontend, non l'importazione strutturata completa.

### Ingresso concordato e bozza minima (baseline di prodotto per Helix)

- Il primo passo chiede «Che tipo di contratto vuoi gestire?» prima di parti, termini o calcolatore. Libero mantiene il proprio percorso; concordato sceglie il percorso CF e non il generatore CasaZen.
- Per il concordato bastano tipo e casa selezionata. Casa già esistente: nessun altro dato obbligatorio per ottenere l'ID. Non bloccare la creazione minima per APE, dati delle parti, importi, date, coefficienti o attestazione assenti: arriveranno dal partner.
- Salvare subito lato server una vera bozza collegata a PropertyId e OrgId e restituire leaseId. Nel codice develop esistono `POST /api/leases/drafts`, `LeaseDraftService` e una bozza incompleta che crea una riga LeaseContract reale; verificarne disponibilità in produzione e riusare/estendere quel percorso per registrare atomicamente tipo Concordato e provenienza CF. Non usare solo un UUID locale, l'ID della casa o un record fittizio di contratto completo.
- Doppi click/retry non devono creare più bozze per la stessa azione: idempotenza server e ripresa del riferimento salvato. Un nuovo contratto intenzionale sulla stessa casa resta possibile.
- Mostrare «Bozza — in attesa del contratto del partner», nome della casa e leaseId copiabile. Dati mancanti come «Da acquisire», mai canone zero, date segnaposto o persone inventate presentate come dati reali. La bozza non genera occupazione, promemoria fiscali, canoni da incassare o ricevute di contratto già registrato.
- Dopo creazione, pagina offerta/pagamento o uso omaggio; admin CasaZen accede direttamente secondo i controlli indicati sotto. Solo dopo il diritto acquisito si passa a CF con quel leaseId.
- CF prepara il contratto vero: CasaZen non deve chiedere di completare e generare prima un altro contratto concordato né avviare una propria firma/registrazione per questa pratica.

## 3. Pagina offerta, pagamento e accessi gratuiti

### Regole commerciali vincolanti

- Prezzo all'utente: **119,00 euro totali, IVA compresa, per una pratica riferita a un contratto**. È il prezzo CasaZen con il proprio ricarico, non il costo del fornitore. Non aggiungere IVA al checkout e non esporre all'utente i costi CF 69/89 euro. Registro e bollo restano esclusi quando dovuti, come nel servizio del partner.
- L'utente ordinario deve pagare CasaZen prima di poter proseguire al modulo CF. Pagamento fallito, cancellato o ancora in attesa non sblocca il servizio.
- L'admin **di piattaforma CasaZen** accede direttamente al modulo, senza pagina di pagamento e senza consumare omaggi. Riusare la policy server `CasazenPolicies.AdminOnly` (ruolo JWT `Admin`), non confonderla con l'amministratore dell'organizzazione o con `OrgBillingAdmin`. Conservare i controlli di accesso al contratto e l'eventuale contesto amministrativo previsto dal prodotto.
- Chi sottoscrive un abbonamento CasaZen di due anni riceve **una sola pratica omaggio**, non accesso gratuito illimitato. Il diritto è governato da un flag persistente nel DB. Assunzione di implementazione coerente con il billing attuale: beneficio dell'organizzazione titolare dell'abbonamento, condiviso tra i membri autorizzati; non una pratica per ogni membro.
- Una pratica già pagata o già associata all'omaggio si riapre senza un secondo pagamento/consumo. La seconda pratica su un altro contratto torna a 119 euro una volta esaurito l'omaggio.

### Pagina di presentazione richiesta

Percorso proposto: `/app/long-rent/leases/:id/concordatofacile`, raggiungibile dal contratto concordato. È la pagina dell'offerta CasaZen, prima del pagamento. Non costruire il modulo del partner in CasaZen.

Mostrare casa e contratto interessati, prezzo finale **119 € IVA compresa**, eventuale omaggio disponibile e questi passaggi, in ordine e in linguaggio per il locatore:

1. «Dopo il pagamento, o usando la pratica inclusa nel tuo abbonamento biennale, vieni reindirizzato al modulo del nostro partner ConcordatoFacile.»
2. «Compila il modulo con i dati e i documenti richiesti per il tuo contratto.»
3. «Gestisci direttamente via email con il partner eventuali integrazioni, revisione del contratto e firma. Il partner segue la pratica e la registrazione prevista dal servizio.»
4. «Alla fine ricevi dal partner un'email con i documenti e il link per caricarli in CasaZen.»
5. «Ti consigliamo di caricare i documenti sulla tua casa: potrai ritrovarli e gestire i tuoi contratti in un unico posto, in CasaZen.»

Chiarire che gli aggiornamenti di stato sono visibili in CasaZen ma le comunicazioni operative rimangono con il partner, che il caricamento finale è effettuato dall'utente e che registro/bollo eventualmente dovuti sono esclusi dai 119 euro. Non promettere inclusioni aggiuntive dei pacchetti CF finché non sono confermate.

CTA secondo lo stato verificato dal server:

| Condizione | Azione |
|---|---|
| Utente senza diritto | «Paga 119 € e continua» → checkout CasaZen |
| Pratica omaggio disponibile | «Usa la pratica omaggio e continua» → associa atomicamente l'omaggio al contratto, poi apre CF; nessun checkout |
| Accesso già pagato o omaggio già associato a questo contratto | «Continua sul modulo del partner» → stesso accesso, senza addebito |
| Admin CasaZen | Azione diretta dal contratto → verifica server → modulo CF, senza passare dalla pagina di pagamento |
| Pagamento in conferma | Messaggio di attesa/verifica, senza link CF sbloccato |

Il ritorno dopo checkout va alla pagina offerta dello stesso contratto. Dopo conferma server il pulsante permette di proseguire a CF; non perdere casa/contratto nel checkout o nel login.

### Implementazione minima di pagamento e diritto di accesso

Nel codice esistono `BillingController`, `BillingCheckoutService`, `StripeBillingService`, `ProcessedStripeEvent`, `StripeWebhookJob` e frontend `src/features/billing`, `src/queries/use-billing.ts`. Riusare SDK, configurazione, profilo di fatturazione e gestione eventi già presenti dove compatibili. Il checkout attuale degli abbonamenti non è automaticamente un checkout per acquisto singolo: aggiungere un flusso **una tantum** del servizio CF senza cambiare l'abbonamento dell'utente e senza usare i pagamenti degli affitti o Stripe Connect.

- Importo determinato dal server: EUR 11900 centesimi complessivi, IVA inclusa. La configurazione fiscale/Price del servizio deve preservare questo totale; non riutilizzare automaticamente configurazioni che aggiungono imposte al prezzo. Integrare la documentazione fiscale CasaZen con i meccanismi esistenti; non applicare a CasaZen l'esenzione IVA dichiarata dall'associazione.
- Persistenza minima: acquisto/diritto riferito a `OrgId` e `LeaseId`, origine (`Paid`, `BiennialGift`, `Admin` come motivo di bypass, senza finto pagamento), stato del pagamento ove presente, identificativi Stripe e date necessarie. Il bypass admin può essere registrato nell'audit esistente senza un ordine fittizio.
- Flag DB sull'organizzazione, ad esempio `HasConcordatoGift`, più `ConcordatoGiftLeaseId` e data di assegnazione/consumo, o campi equivalenti. Il flag distingue disponibilità del beneficio; il riferimento al contratto mantiene l'accesso quando il flag è consumato. Non basta un booleano permanente di gratuità.
- Abilitare il flag una sola volta alla conferma server di un abbonamento realmente sottoscritto per due anni, identificato da piano/Price o durata verificata del contratto di abbonamento. Non confondere due anni con 24 pagamenti mensili o con il semplice tempo passato come cliente. Collegare l'assegnazione all'identificativo dell'abbonamento, così rinnovi notificati più volte, login o refresh non rigenerano l'omaggio. Verificare se il catalogo biennale esiste: se manca, esplicitare il mapping da configurare senza inventare un prodotto Stripe live.
- Il flag viene amministrato solo dal server e dagli strumenti admin autorizzati; nessun campo modificabile dall'utente nel profilo. Migrazione con flag false per default; per abbonamenti biennali esistenti assegnare il beneficio solo dopo verifica dei dati di sottoscrizione, senza omaggi generalizzati. Riutilizzare un backoffice esistente se disponibile, altrimenti prevedere un controllo admin minimo e auditato per assegnazione/correzione, senza ricaricare benefici già spesi per errore.
- Consumo omaggio e associazione al leaseId nella stessa transazione con controllo di concorrenza: due schede o due membri non devono spenderlo su contratti diversi. La riapertura dello stesso contratto non lo consuma di nuovo. Se manca configurazione/dato necessario per costruire il link, non consumare l'omaggio.
- Una sessione checkout aperta per quel contratto viene riusata/idempotente per evitare doppi click; prima di crearla ricontrollare diritto già acquisito e omaggio. Non avviare un addebito se lo stesso contratto è stato appena sbloccato con omaggio da un'altra scheda. Il completamento tardivo di una sessione già aperta va riconciliato senza creare due pratiche o due diritti.
- Sbloccare solo da pagamento confermato server con firma/eventi Stripe verificati e correlazione di importo, valuta, acquisto e contratto. `?checkout=success`, ID sessione nel browser o click sul ritorno non sono prova del pagamento. Eventi duplicati o fuori ordine non devono riaprire ordini falliti né annullare pagamenti validi. Gestire anche metodi asincroni se abilitati.
- Rimborso/contestazione confermati devono aggiornare il diritto del pagamento interessato secondo il flusso esistente; non devono cancellare automaticamente una pratica CF già avviata o i documenti dell'utente. Non inventare una politica di rimborso del servizio: documentare il caso per la gestione operativa.

Prevedere endpoint interni nel contesto del contratto per leggere prezzo/diritto e stato acquisto, avviare checkout e proseguire a CF. Il comando «prosegui» controlla sempre sul server admin/diritto pagato/omaggio e autorizzazione al contratto; restituisce il link CF solo quando autorizzato. I GET informativi non consumano omaggi. Il frontend non decide la gratuità da solo. I nomi esatti degli endpoint interni sono una scelta Helix coerente con le convenzioni esistenti.

**Limite da esplicitare:** il modulo CF è pubblico e il link concordato non è firmato. CasaZen può proteggere la propria sezione e l'emissione del link; questo non impedisce tecnicamente a un utente di ricostruire un URL CF. Per garantire che nessuna pratica venga lavorata senza pagamento/omaggio, concordare con CF una verifica del diritto presso CasaZen o una riconciliazione operativa prima di accettare/lavorare le pratiche partner. Non aggiungere di nascosto parametri o firme non concordati e non dichiarare impossibile l'accesso diretto a un modulo pubblico. La verifica di pagamento sul server CasaZen rimane obbligatoria.

## 3 bis. Apertura del modulo CF dopo lo sblocco

Base: `https://app.concordatofacile.it`.

Query obbligatoria per CasaZen:

| Parametro | Valore |
|---|---|
| `partner` | Codice assegnato da CF; configurabile, ancora da ricevere |
| `leaseId` | UUID reale del contratto già salvato in CasaZen |
| `comune` | Nome del comune della casa |
| `provincia` | Sigla provinciale, per esempio `MB` |

Costruire con URL/URLSearchParams per codificare correttamente spazi e accenti. Nessuna firma, scadenza, `theme`, codice ISTAT o `returnUrl` da aggiungere al modulo.

Esempio illustrativo, con codice partner segnaposto:

`https://app.concordatofacile.it?partner=CODICE_CF&leaseId=0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11&comune=Cesano%20Maderno&provincia=MB`

Inserire l'azione di accesso al servizio nel contratto concordato esistente: utente ordinario verso la pagina offerta, admin verso il comando server di accesso diretto. Aprire CF soltanto dopo il controllo del diritto descritto sopra. Leggere comune e provincia dai dati territoriali già disponibili: verificare la sorgente della sigla, non derivarla arbitrariamente dal testo dell'indirizzo. Se manca un dato necessario o il codice partner, spiegare cosa manca e non aprire un link incompleto. Usare la bozza minima reale descritta sopra; non richiedere il completamento del vecchio form concordato per ottenere il leaseId.

Il modulo accetta oggi solo concordati. Libero 4+4 e transitori sono trattati da CF per email: non inviarli nel modulo e non costruire quel canale manuale in questo intervento. Aprire il modulo non prova che CF abbia creato una pratica; non marcare la pratica come ricevuta o completata dal solo click. Nessun requisito di sessione o riapertura CF è da inventare.

## 4. URL definitivo per email e bottone

Dominio confermato dall'utente: `https://casazen.space`.

**Verifica produzione del 21 ottobre 2026:** il browser mostra «404 — Pagina non trovata» sul percorso, anche con leaseId. La pagina non è operativa; il fornitore deve mantenere disattivati i link fino a conferma esplicita CasaZen dopo il collaudo completo. Il redirect a www.casazen.space va incluso nel test di conservazione del parametro.

**URL da dare a CF e implementare senza cambiarlo:**

`https://casazen.space/app/long-rent/documents/upload?leaseId=<leaseId>`

CF sostituisce `<leaseId>` con l'UUID ricevuto all'apertura del modulo. Esempio completo:

`https://casazen.space/app/long-rent/documents/upload?leaseId=0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11`

È una pagina web aperta con GET, non un'API multipart né l'endpoint di stato. Nell’email con i documenti definitivi inviata dopo la firma, CF inserisce il link al leaseId della bozza. Nelle email all'utente che contengono documenti CF usa il bottone «Carica i dati in CasaZen»; nella conferma solo testo scrive il link per esteso. Non servono propertyId, dati personali, chiavi o URL di download nella query.

### Comportamento della pagina

1. Registrare `/app/long-rent/documents/upload` nel routing esistente, con i permessi coerenti con caricamento documenti e accesso long-rent.
2. Validare leaseId. Se l'utente non è autenticato, usare il login esistente e preservare percorso e query al ritorno; mantenere anche il corretto contesto organizzativo secondo le regole esistenti.
3. Caricare il contratto con le API autorizzate esistenti e ottenere il suo propertyId. Non fidarsi di identificativi aggiuntivi provenienti dal link.
4. Mostrare nome/indirizzo della casa e riferimento del contratto, poi permettere di caricare i documenti e riconoscere/selezionare contratto, attestazione e registrazione. Il primo rilascio generico riusa Other; Helix aggiunge la classificazione e il collegamento al contratto descritti sotto. Si può comporre una pagina minima con i componenti esistenti; non serve ridisegnare il dettaglio casa.
5. Dopo il caricamento mostrare conferma e accesso ai documenti della casa. L'utente può ripetere il caricamento per altri file usando il comportamento esistente.
6. Con leaseId mancante, malformato, inesistente o non accessibile, mostrare uno stato comprensibile e un collegamento alle proprie case; non caricare file, non associare alla prima casa trovata e non rivelare informazioni altrui.

I controlli effettivi rimangono sul server: accesso al contratto in lettura e autorizzazione alla casa in scrittura, con isolamento dell'organizzazione. Un UUID nel link non concede diritti. Usare i permessi esistenti senza limitarsi a confrontare OwnerId nel client.

Il click non importa file direttamente da CF. L'utente sceglie e carica i documenti ricevuti; il caricamento avvia l'importazione strutturata descritta sotto. Niente lettura delle email dell'utente o download arbitrario da URL esterni. Nessun secondo pagamento per caricare o consultare i documenti della propria casa.

## 4 bis. Documenti definitivi: conservazione e popolamento automatico

Questa è parte del flusso da implementare con Helix. Le copie e i dati estratti devono rimanere collegati allo stesso leaseId della bozza e alla sua casa, senza generare un nuovo contratto.

| Documento caricato | Popolamento automatico atteso | Sezione e copia consultabile |
|---|---|---|
| Contratto definitivo CF, dopo firma | Parti e ruoli, dati dell'immobile, tipo concordato, durata/date, data di stipula se rilevabile, canone, deposito e regime fiscale ove dichiarati esplicitamente | Dati del contratto + copia del contratto originale/firme |
| Attestazione | Numero/data attestazione, accordo territoriale/comune, soggetti attestatori e dati attestati del canone se presenti | Attestazione del contratto + PDF originale |
| Ricevuta di registrazione | Data registrazione, protocollo/codice identificativo e ufficio, esito, eventuale opzione fiscale e importi soltanto se presenti nel documento | Registrazione del contratto + ricevuta originale |

- Ogni file viene persistito nello storage privato e ha metadati nel DB (leaseId, propertyId derivato dal contratto, tipo, nome, data caricamento, autore, stato elaborazione e impronta/versione). Riusare i servizi esistenti, evitando due copie fisiche dello stesso file per archivio casa e contratto: rendere consultabile lo stesso documento da entrambi i contesti.
- Conservare i byte originali: non ristampare né sostituire il PDF firmato o la ricevuta con una versione OCR. L'eventuale OCR è un derivato per estrazione, non la copia legale consultabile.
- Estrarre automaticamente il testo del PDF e, se scansione, usare un percorso OCR/estrazione compatibile con i servizi già disponibili. Scelta concreta di libreria/provider a Helix dopo verifica dello stack: non presupporre che una API esistente sappia già leggere i PDF. Classificare documento e campi in modo verificabile, con evidenza della pagina e provenienza del valore.
- Popolare automaticamente i campi estratti con sufficiente affidabilità, dopo validazione e verifica di coerenza con casa, contratto e documenti già acquisiti. Campi mancanti restano vuoti/da completare; errori, bassa affidabilità o conflitti vanno in revisione con anteprima dei valori, senza indovinare né sovrascrivere silenziosamente dati già confermati.
- Per importi, codice fiscale, date, immobile e regime fiscale richiedere controlli strutturali e di coerenza; un semplice nome file non prova il tipo/esito del documento. Un contratto della casa o delle parti sbagliate viene conservato come caricamento da verificare senza popolare il contratto corrente finché non è chiarito.
- Le sezioni attestazione e registrazione sono indipendenti. Caricare il contratto non equivale a ottenere attestazione o registrazione. Una ricevuta di trasmissione non basta a marcare «Registrato»: verificare l'esito di registrazione o usare lo stato CF confermato secondo la sua semantica. L'upload non attiva un secondo invio a registrazione.
- Mostrare stati «Caricato», «Elaborazione», «Dati acquisiti» o «Da verificare» per documento e permettere apertura/download autenticato di tutte e tre le copie. Se l'estrazione fallisce, il file deve comunque restare conservato e consultabile, con possibilità di correggere/riprovare.
- Ordine di arrivo libero: contratto, attestazione e registrazione possono arrivare separatamente. Nessuna perdita dei dati già acquisiti; conferma delle variazioni importanti prima di sovrascrivere. Stesso file ricaricato/riprocessato idempotente per leaseId/impronta; una nuova versione va distinta e non cancella la precedente senza una regola esplicita.
- Le scritture sui dati del contratto avvengono attraverso le regole del dominio esistenti, con audit della provenienza documento/estrazione, controllo concorrenza e autorizzazione di scrittura. Il flag pagamento/omaggio non è un sostituto dei permessi dell'utente.
- Un caricamento soltanto con propertyId (pagina esposta subito per conservare i documenti della casa) persiste correttamente il file, ma non sceglie arbitrariamente un contratto tra più presenti. Il flusso definitivo CF usa leaseId; per file già in archivio casa prevedere selezione esplicita della bozza corretta e associazione/riprocessamento, senza nuova copia o nuovo ID.

## 5. Stato della pratica: integrazione server minima

### Dati confermati da CF

CF fornirà un endpoint interrogabile per leaseId, con una chiave fornita da CF. La risposta contiene numero pratica, stato comprensivo della registrazione e data dell'ultimo passaggio, senza dati personali.

### Informazioni ancora mancanti

URL, metodo, header della chiave, schema JSON, valori degli stati, formato/fuso orario della data, rate limit, risposta per pratica assente e regola per più pratiche sullo stesso leaseId. Senza questi dati non esiste un contratto esterno implementabile e verificabile: non usare come reali i vecchi OpenAPI e non inventare endpoint CF.

### Scelta di implementazione CasaZen

- Un piccolo client HTTP tipizzato nel backend, secondo i pattern esistenti, con URL/chiave configurati sul server. Nessuna chiave o chiamata autenticata CF dal browser.
- Esporre una lettura autorizzata nel contesto del contratto. Percorso interno proposto: `GET /api/leases/{leaseId}/concordatofacile/status`; non è un endpoint richiesto a CF e non è già presente. Verificare assenza di conflitti prima di aggiungerlo.
- Verificare accesso alla locazione e organizzazione prima di chiamare CF. Applicare timeout e gestione degli errori coerenti con il backend.
- Il frontend legge su apertura del dettaglio contratto e con azione esplicita «Aggiorna stato». Nessun job Hangfire, polling continuo o nuova tabella per memorizzare lo stato CF nel minimo rilascio; la persistenza acquisto/omaggio è invece richiesta. Introdurre refresh periodico o cache server solo se necessario per i limiti comunicati da CF.
- Mostrare numero pratica, stato e data ultimo passaggio. Lo stato CF non è lo stato di caricamento/estrazione dei documenti. Il popolamento dei dati e delle sezioni è governato dall'importazione sopra, non dal semplice click di ritorno o da una stringa di stato generica.
- Finché CF non consegna gli enum, non inventare una macchina a stati. Dopo la consegna mappare i valori documentati a etichette italiane e gestire valori nuovi senza presentare uno stato terminale falso.
- Distinguere configurazione mancante, pratica assente e servizio temporaneamente indisponibile. Non trasformare un timeout in «nessuna pratica». Un refresh fallito non sostituisce un risultato valido già mostrato con uno stato negativo inventato. Non fare retry illimitati; rispettare 429/Retry-After se previsti.
- Per aggiornamenti concorrenti non applicare una risposta più vecchia di quella già mostrata; usare il dato temporale CF solo dopo averne chiarito la semantica. Gestione di pratica riaperta/multipla secondo contratto CF, senza creare uno storico locale non richiesto.

DTO normalizzato CasaZen da definire nella lettura interna: numero pratica, stato originale CF, data ultimo passaggio e momento della consultazione. I nomi JSON di questo DTO sono una scelta interna, non la forma confermata della risposta CF.

Prima della consegna dei dettagli CF si possono completare apertura modulo e pagina di ritorno e predisporre il confine del client secondo necessità. Un mock può servire nei test ma non deve comparire come stato reale in produzione. Dichiarare esplicitamente che la verifica live dello stato resta bloccata dai dettagli CF mancanti.

## 6. Sequenza di lavoro per Helix

1. Verificare route, accesso long-rent, DTO contratto, sorgente provincia e riuso upload sulle branch correnti. Elencare il delta reale prima di modificare.
2. Implementare scelta del tipo come primo passo e bozza minima concordato con leaseId reale; poi pagina offerta, acquisto singolo 119 € IVA inclusa, diritto persistente e flag omaggio biennale con migrazione, bypass admin e controlli server. Poi collegare apertura CF e pagina di ritorno; estendere l'upload esistente con documenti tipizzati, relazione al contratto, estrazione e popolamento automatico delle sezioni. Riusare lo storage, senza archivio parallelo.
3. Integrare la lettura di stato quando disponibili specifica CF e credenziali server. Tenere isolata questa parte se i dettagli mancano.
4. Aggiornare documentazione e aprire le PR nei repository effettivamente modificati, con test pertinenti e limitazioni esplicite. Non effettuare deployment, invii di pratiche reali o pagamenti come prova.

## 7. Criteri di completamento

| Caso | Risultato atteso |
|---|---|
| Pagina offerta | Prezzo totale 119 € IVA inclusa e tutti i cinque passaggi descritti prima del pagamento |
| Utente ordinario non pagante | Nessun link CF emesso dal server; accesso alla pagina commerciale |
| Checkout annullato/fallito/in attesa o success URL alterato | Nessuno sblocco; contratto e casa preservati al ritorno |
| Pagamento singolo verificato | Una pratica sbloccata, totale EUR 11900, nessun rinnovo di abbonamento |
| Admin CasaZen / admin di organizzazione | Solo il primo salta il pagamento; accesso risorsa sempre controllato |
| Abbonamento biennale confermato | Flag assegnato una sola volta; una pratica omaggio, seconda pratica a pagamento |
| Due schede/membri usano omaggio | Un solo contratto associato atomicamente; riapertura dello stesso resta gratuita |
| Webhook pagamento/abbonamento duplicato | Nessun doppio ordine, addebito o riaccredito del flag omaggio |
| Link email per caricamento | Nessun secondo pagamento o consumo omaggio per archiviare documenti; controlli ordinari su contratto/casa |
| Scelta iniziale concordato | Solo casa e tipo necessari per creare bozza persistita; nessun APE, dati parti o canone richiesti per ottenere il leaseId |
| Documenti definitivi | Contratto, attestazione e registrazione popolano le rispettive sezioni dello stesso leaseId; originali conservati |
| Estrazione fallita/conflitto/documento sbagliato | File consultabile, errore o revisione visibile; nessuna sovrascrittura falsa dei dati |
| Doppio upload o nuova versione | Nessun doppio contratto o evento; versioni e provenienza preservate |
| Apertura modulo da concordato salvato | Solo quattro parametri corretti; comune con accenti/spazi codificato; provincia in sigla |
| Partner o dati obbligatori assenti | Azione non produce link incompleto; spiegazione utile |
| Libero/transitorio | Nessun invio nel modulo riservato ai concordati |
| Link email da utente non autenticato | Dopo login ritorna allo stesso path con leaseId preservato |
| Contratto accessibile | Casa corretta mostrata, file persistiti e consultabili da casa e contratto; tre sezioni con relativi dati acquisiti |
| LeaseId mancante/errato o altro utente/org | Nessun accesso a dati altrui, nessuna scrittura o associazione automatica |
| Riapertura del link | Nessun documento creato senza selezione e conferma dell'utente |
| Stato CF disponibile | Lettura solo via server, numero pratica/stato/data corretti; chiave assente da bundle e log |
| CF assente/non configurato/timeout/429 | Stati distinti e gestiti; nessun falso completamento, nessun azzeramento di un risultato valido |
| Stato di attestazione/registrazione | Distinto dall'archivio e dall'IMU; dati acquisiti soltanto da importazione verificata/stato CF documentato |

Test mirati: diritto di accesso lato server, acquisto singolo e conferma Stripe, distinzione admin piattaforma/org, assegnazione/consumo omaggio concorrente e idempotente, composizione URL, ritorno dopo login, contesto casa/permessi upload e client di stato con risposte CF documentate. Usare i test esistenti come base; nessun test che verifichi soltanto il testo di questa specifica. Eseguire lint/typecheck e test pertinenti secondo le istruzioni dei repository; per le modifiche server anche build e test del percorso autorizzato. Verifica checkout in modalità test e flusso offerta → conferma → CF con stub controllato; verifica manuale del link completo su desktop/mobile e caricamento di un documento di prova, senza pratica reale CF.

**Consegna richiesta:** il flusso minimo funzionante, elenco dei file modificati, verifiche eseguite e dettagli CF ancora mancanti. Non dichiarare completata l'integrazione live se l'endpoint CF non è stato fornito e verificato.
