# Analisi tecnica per Helix — integrazione minima ConcordatoFacile

Versione 3.0 · 21 ottobre 2026 · Documento unico e autosufficiente per implementare la parte CasaZen. Documentazione del flusso: `docs/integrations/concordatofacile/README.md`. Sostituisce le precedenti analisi e proposte tecniche per questo flusso. I documenti destinati al fornitore restano materiale di corrispondenza, non ulteriori requisiti da sommare. Questa revisione contiene istruzioni: non implementa funzionalità.

## 1. Obiettivo e perimetro minimo

Implementare tre collegamenti leggeri nell'app esistente:

1. Aprire il modulo ConcordatoFacile dal contratto CasaZen con quattro parametri.
2. Ricevere l'utente dal bottone nelle email CF e consentirgli di caricare i documenti sulla casa usando l'archivio esistente.
3. Mostrare lo stato della pratica letto dal backend CasaZen attraverso l'endpoint CF, appena il fornitore ne consegna il contratto tecnico.

CF gestisce dati, documenti, verifiche, contratto, attestazione quando necessaria, firme e registrazione se richiesta. Email operative e consegna documenti sono tra CF e utente. CasaZen non vuole la prima email di pratica ricevuta, quella finale né copie/inoltri: l'unico canale per conoscere lo stato è l'endpoint CF.

La parte documentale può essere quasi tutta riuso. La lettura dello stato richiede invece una piccola integrazione server, ancora dipendente da informazioni CF mancanti. Non costruire una nuova gestione pratiche, un nuovo archivio, un processo di firma o una nuova registrazione fiscale.

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

Conseguenza: il leaseId serve a trovare la casa corretta e mostrare il contesto. Nel minimo rilascio i file sono documenti della casa, come oggi: non aggiungere una relazione documenti–contratto, una migrazione o categorie nuove solo per questa integrazione. Se un'esigenza concreta richiede una relazione persistente, documentare il motivo prima di ampliare il modello; non dichiararla già disponibile.

## 3. Apertura del modulo CF

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

Inserire un'azione nel contesto del contratto concordato esistente, senza nuovo wizard. Leggere comune e provincia dai dati territoriali già disponibili: verificare la sorgente della sigla, non derivarla arbitrariamente dal testo dell'indirizzo. Se manca un dato necessario o il codice partner, spiegare cosa manca e non aprire un link incompleto. Non creare contratti fittizi per ottenere un leaseId.

Il modulo accetta oggi solo concordati. Libero 4+4 e transitori sono trattati da CF per email: non inviarli nel modulo e non costruire quel canale manuale in questo intervento. Aprire il modulo non prova che CF abbia creato una pratica; non marcare la pratica come ricevuta o completata dal solo click. Nessun requisito di sessione o riapertura CF è da inventare.

## 4. URL definitivo per email e bottone

Dominio confermato dall'utente: `https://casazen.space`.

**URL da dare a CF e implementare senza cambiarlo:**

`https://casazen.space/app/long-rent/documents/upload?leaseId=<leaseId>`

CF sostituisce `<leaseId>` con l'UUID ricevuto all'apertura del modulo. Esempio completo:

`https://casazen.space/app/long-rent/documents/upload?leaseId=0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11`

È una pagina web aperta con GET, non un'API multipart né l'endpoint di stato. Nelle email all'utente che contengono documenti CF usa il bottone «Carica i dati in CasaZen»; nella conferma solo testo scrive il link per esteso. Non servono propertyId, dati personali, chiavi o URL di download nella query.

### Comportamento della pagina

1. Registrare `/app/long-rent/documents/upload` nel routing esistente, con i permessi coerenti con caricamento documenti e accesso long-rent.
2. Validare leaseId. Se l'utente non è autenticato, usare il login esistente e preservare percorso e query al ritorno; mantenere anche il corretto contesto organizzativo secondo le regole esistenti.
3. Caricare il contratto con le API autorizzate esistenti e ottenere il suo propertyId. Non fidarsi di identificativi aggiuntivi provenienti dal link.
4. Mostrare nome/indirizzo della casa e riferimento del contratto, poi riusare il caricamento documenti della casa con tipo iniziale `Other`. Si può comporre una pagina minima con i componenti esistenti; non serve ridisegnare il dettaglio casa.
5. Dopo il caricamento mostrare conferma e accesso ai documenti della casa. L'utente può ripetere il caricamento per altri file usando il comportamento esistente.
6. Con leaseId mancante, malformato, inesistente o non accessibile, mostrare uno stato comprensibile e un collegamento alle proprie case; non caricare file, non associare alla prima casa trovata e non rivelare informazioni altrui.

I controlli effettivi rimangono sul server: accesso al contratto in lettura e autorizzazione alla casa in scrittura, con isolamento dell'organizzazione. Un UUID nel link non concede diritti. Usare i permessi esistenti senza limitarsi a confrontare OwnerId nel client.

Il click non importa file da CF e non avvia alcuna scrittura. L'utente sceglie i propri documenti e conferma l'upload. Non aggiungere OCR, parsing email, download remoto, nuove categorie o deduplicazione globale. Evitare solo doppi submit durante l'upload con il meccanismo esistente.

## 5. Stato della pratica: integrazione server minima

### Dati confermati da CF

CF fornirà un endpoint interrogabile per leaseId, con una chiave fornita da CF. La risposta contiene numero pratica, stato comprensivo della registrazione e data dell'ultimo passaggio, senza dati personali.

### Informazioni ancora mancanti

URL, metodo, header della chiave, schema JSON, valori degli stati, formato/fuso orario della data, rate limit, risposta per pratica assente e regola per più pratiche sullo stesso leaseId. Senza questi dati non esiste un contratto esterno implementabile e verificabile: non usare come reali i vecchi OpenAPI e non inventare endpoint CF.

### Scelta di implementazione CasaZen

- Un piccolo client HTTP tipizzato nel backend, secondo i pattern esistenti, con URL/chiave configurati sul server. Nessuna chiave o chiamata autenticata CF dal browser.
- Esporre una lettura autorizzata nel contesto del contratto. Percorso interno proposto: `GET /api/leases/{leaseId}/concordatofacile/status`; non è un endpoint richiesto a CF e non è già presente. Verificare assenza di conflitti prima di aggiungerlo.
- Verificare accesso alla locazione e organizzazione prima di chiamare CF. Applicare timeout e gestione degli errori coerenti con il backend.
- Il frontend legge su apertura del dettaglio contratto e con azione esplicita «Aggiorna stato». Nessun job Hangfire, polling continuo o nuova tabella nel minimo rilascio. Introdurre refresh periodico o cache server solo se necessario per i limiti comunicati da CF.
- Mostrare numero pratica, stato e data ultimo passaggio. Lo stato CF non è lo stato di caricamento dei documenti e non aggiorna automaticamente registrazione fiscale, opzione cedolare o dati del contratto.
- Finché CF non consegna gli enum, non inventare una macchina a stati. Dopo la consegna mappare i valori documentati a etichette italiane e gestire valori nuovi senza presentare uno stato terminale falso.
- Distinguere configurazione mancante, pratica assente e servizio temporaneamente indisponibile. Non trasformare un timeout in «nessuna pratica». Un refresh fallito non sostituisce un risultato valido già mostrato con uno stato negativo inventato. Non fare retry illimitati; rispettare 429/Retry-After se previsti.
- Per aggiornamenti concorrenti non applicare una risposta più vecchia di quella già mostrata; usare il dato temporale CF solo dopo averne chiarito la semantica. Gestione di pratica riaperta/multipla secondo contratto CF, senza creare uno storico locale non richiesto.

DTO normalizzato CasaZen da definire nella lettura interna: numero pratica, stato originale CF, data ultimo passaggio e momento della consultazione. I nomi JSON di questo DTO sono una scelta interna, non la forma confermata della risposta CF.

Prima della consegna dei dettagli CF si possono completare apertura modulo e pagina di ritorno e predisporre il confine del client secondo necessità. Un mock può servire nei test ma non deve comparire come stato reale in produzione. Dichiarare esplicitamente che la verifica live dello stato resta bloccata dai dettagli CF mancanti.

## 6. Sequenza di lavoro per Helix

1. Verificare route, accesso long-rent, DTO contratto, sorgente provincia e riuso upload sulle branch correnti. Elencare il delta reale prima di modificare.
2. Implementare azione di apertura CF e pagina di ritorno con upload esistente. Non aggiungere dipendenze o migrazioni per questi due collegamenti.
3. Integrare la lettura di stato quando disponibili specifica CF e credenziali server. Tenere isolata questa parte se i dettagli mancano.
4. Aggiornare documentazione e aprire le PR nei repository effettivamente modificati, con test pertinenti e limitazioni esplicite. Non effettuare deployment, invii di pratiche reali o pagamenti come prova.

## 7. Criteri di completamento

| Caso | Risultato atteso |
|---|---|
| Apertura modulo da concordato salvato | Solo quattro parametri corretti; comune con accenti/spazi codificato; provincia in sigla |
| Partner o dati obbligatori assenti | Azione non produce link incompleto; spiegazione utile |
| Libero/transitorio | Nessun invio nel modulo riservato ai concordati |
| Link email da utente non autenticato | Dopo login ritorna allo stesso path con leaseId preservato |
| Contratto accessibile | Casa corretta mostrata, upload riusato con default Other, documento visibile nell'archivio casa |
| LeaseId mancante/errato o altro utente/org | Nessun accesso a dati altrui, nessuna scrittura o associazione automatica |
| Riapertura del link | Nessun documento creato senza selezione e conferma dell'utente |
| Stato CF disponibile | Lettura solo via server, numero pratica/stato/data corretti; chiave assente da bundle e log |
| CF assente/non configurato/timeout/429 | Stati distinti e gestiti; nessun falso completamento, nessun azzeramento di un risultato valido |
| Stato di attestazione/registrazione | Distinto dall'archivio documenti e dall'IMU; nessuna modifica fiscale automatica |

Test mirati: composizione URL, ritorno dopo login, contesto casa/permessi upload e client di stato con risposte CF documentate. Usare i test esistenti come base; nessun test che verifichi soltanto il testo di questa specifica. Eseguire lint/typecheck e test pertinenti secondo le istruzioni dei repository; per le modifiche server anche build e test del percorso autorizzato. Verifica manuale del link completo su desktop/mobile e caricamento di un documento di prova, senza pratica reale CF.

**Consegna richiesta:** il flusso minimo funzionante, elenco dei file modificati, verifiche eseguite e dettagli CF ancora mancanti. Non dichiarare completata l'integrazione live se l'endpoint CF non è stato fornito e verificato.
