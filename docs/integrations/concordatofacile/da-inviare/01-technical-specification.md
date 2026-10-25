---
title: Specifiche tecniche - integrazione CasaZen / ConcordatoFacile
version: 1.3-bozza
date: 2026-10-13
---

# Specifiche tecniche: integrazione CasaZen / ConcordatoFacile

> **SUPERATO.** Superato dal documento `docs/integrations/concordatofacile/README.md`: non usare come riferimento. Questa è la specifica v1.3 che CasaZen propose a ConcordatoFacile il 13 ottobre 2026 (modello a webhook); è stata sostituita dalla «specifica endpoint di stato v0.2» di CF (ottobre 2026), che governa il link firmato e la lettura dello stato. Restano validi soltanto: il §2 (vettore di prova della firma del link: `linkSecret`, messaggio e firma), le convenzioni richiamate dalla v0.2 (JSON `camelCase`, date ISO 8601 UTC con `Z`, errori `application/problem+json` con `code` in `snake_case`) e il punto R24 (inquadramento privacy da definire con CF prima delle pratiche reali). **Non si implementano:** i webhook e gli eventi (`case_status_changed`, `case_completed`, `case_rejected`, `case_cancelled`), il `caseId`, la consegna dei file dell'attestazione e i parametri `comuneIstatCode`, `returnUrl` (qui obbligatorio) e `theme` del link, che CF ignora. Il `.pdf` della stessa cartella è lo stesso testo e vale lo stesso avviso.

**Versione 1.3 (bozza)** · 13 ottobre 2026 · Allegato: `02-openapi.yaml` (contratto leggibile da macchina, stessi nomi di questo documento).

**Principio.** L'utente apre il **modulo di ConcordatoFacile (CF)** da CasaZen e **svolge lì tutta la pratica**: inserimento dei dati, documenti, integrazioni richieste, correzioni, reinvio dei documenti, revisione di eventuali bozze, firma, stato della pratica. Ogni azione sui dati e sui documenti la compie l'utente nel modulo: CasaZen non risponde a nessuna richiesta di CF e non raccoglie, corregge né trasmette dati o documenti al posto dell'utente. CasaZen fa solo tre cose: apre o riapre il modulo con un link firmato (sezione 2), riceve da CF gli **avvisi di stato** (per esempio "serve un'azione dell'utente nel modulo"), che può inoltrare all'utente nella propria app, e riceve l'**esito finale** (sezione 3). Il documento ha due parti: **che cosa deve garantire il modulo** (sezione 1) e **lo scambio di dati** tra CF e CasaZen (sezioni 2, 3 e 4). Proposte ed esempi sono inclusi. Il testo è in italiano; **i nomi (endpoint, campi, valori) sono in inglese**, tranne i pochi già italiani nell'API di CasaZen (`canoneMinMensile`, `subFascia`, `Proprieta`, `Inquilini`), che manteniamo uguali per non dover mappare nulla.

**Convenzioni.** Sono quelle già in uso nell'API di CasaZen, così nessun campo va adattato dopo l'integrazione.

| Tema | Regola |
|---|---|
| JSON | UTF-8, nomi dei campi in `camelCase` |
| Valori enumerati | Stringa con il nome del membro, per esempio `Tenant`, `Proprieta` (mai numeri) |
| Date e orari | ISO 8601 UTC con `Z`: `2026-10-13T09:30:00Z`. Una data di calendario (`startDate`) è la mezzanotte UTC: `2026-11-01T00:00:00Z` |
| Importi | Numero decimale in euro, due decimali, nessuna conversione in centesimi: `monthlyRent: 400.00` |
| Comune | `comuneIstatCode`: codice ISTAT a 6 cifre come stringa (`"108040"`) |
| Identificativi | `leaseId` = GUID del contratto in CasaZen, restituito da CF in ogni evento. `caseId` = identificativo creato da CF |
| Errori | `application/problem+json` con `code` in snake_case |

---

## 1. Cosa deve garantire il modulo CF

Poiché CasaZen delega a CF l'intera pratica, il modulo deve garantire quanto segue. Per ogni punto che non potete garantire così com'è, indicate l'alternativa.

**Accesso e veste**

| # | Requisito |
|--|------------------------------|
| R1 | Il modulo si apre dal link della sezione 2 e l'utente **non deve creare né usare un account CF**. |
| R2 | Il modulo e le comunicazioni dirette all'utente (email, pagina di stato) riportano la veste di CasaZen indicata da `theme` (nome, logo, colori). |
| R3 | Funziona da telefono e dal browser integrato dell'app CasaZen. |
| R4 | È in lingua italiana. |
| R5 | Non chiede pagamenti all'utente: il compenso a CF è regolato tra CF e CasaZen. |
| R6 | Il modulo si apre solo con un **link firmato da CasaZen** (sezione 2): un link senza firma valida, o scaduto, è rifiutato. Così nessuno può aprire pratiche a nome di CasaZen né leggere i dati di una pratica conoscendo solo il `leaseId`. Il link di rientro che CF invia all'utente per email (R11) è equivalente. |

**Gestione della pratica, tutta dentro il modulo**

| # | Requisito |
|--|------------------------------|
| R7 | Raccoglie tutti i dati e i documenti necessari all'attestazione, compresi quelli elencati in `attestation` nella sezione 3 (parti, immobile e dati catastali, durata, canone, superfici, zona). |
| R8 | Salva la compilazione in bozza: l'utente può interrompere e riprendere dallo stesso link. |
| R9 | Dati o documenti mancanti, correzioni, reinvio dei documenti, bozza da approvare e firmare, firma: si gestiscono **solo nel modulo, dall'utente**. CasaZen non li raccoglie e non li trasmette, non deve rispondere e non fa da intermediario. |
| R10 | L'**avviso** all'utente che serve un'azione è un'impostazione del partner che CasaZen sceglie e può cambiare: (a) CF avvisa solo CasaZen con `case_status_changed` / `ActionRequired` (sezione 3) e CasaZen avvisa l'utente nella propria app, (b) CF avvisa solo l'utente per email, (c) entrambi. In ogni caso l'avviso dice che cosa manca o che cosa fare (`detail`) e l'utente può riaprire il modulo (R11). |
| R11 | L'utente può **rientrare nel modulo in qualsiasi momento** per inviare i dati o i documenti mancanti, modificare i dati, approvare la bozza e firmare: dal link firmato di CasaZen (sezione 2) oppure, se è attiva l'email di CF (R10), dal link di rientro ricevuto (valido almeno 30 giorni dall'ultima richiesta, proposta; l'utente può farlo rigenerare). Al rientro il modulo mostra subito la richiesta in sospeso. |
| R12 | Mostra all'utente lo stato della pratica e la prossima azione richiesta, in ogni momento. |
| R13 | Verifica all'inizio che il comune sia coperto da un accordo in vigore. Se non lo è, chiude subito con `case_rejected` e `reason` `municipality_not_covered`, senza lavorazione. |
| R14 | Una sola pratica aperta per `leaseId`. Lo stesso `leaseId` riapre la pratica se è aperta. Se è chiusa con `case_rejected` o `case_cancelled`, ne apre una nuova (nuovo `caseId`). Se è chiusa con `case_completed`, mostra l'attestazione e **non ne crea un'altra**. |
| R15 | In ogni momento, e al termine della compilazione, l'utente può tornare a CasaZen con un pulsante che porta a `returnUrl` (sezione 2). Il modulo reindirizza solo a domini di CasaZen concordati in anticipo. |

**Attestazione**

| # | Requisito |
|--|------------------------------|
| R16 | È emessa sul **modello ufficiale** previsto dall'accordo territoriale del comune, dalle organizzazioni **firmatarie di quell'accordo** (una per la proprietà e una per gli inquilini, dove l'accordo lo richiede). Nome e ruolo (`Proprieta` o `Inquilini`) di ciascuna sono nell'evento. |
| R17 | Il canone è verificato sull'accordo in vigore (zona, sub-fascia, coefficienti): i valori usati sono nell'evento (`assessment`), così CasaZen può confrontarli con i propri. |
| R18 | È consegnata a CasaZen come **PDF separato**, apribile e senza password, anche se CF produce uno zip dell'intero fascicolo (che CasaZen non richiede). Le eventuali firme digitali restano valide: il file non viene ristampato né riconvertito. |
| R19 | È emessa entro 48 ore da quando la pratica è completa (tempo da voi indicato). |
| R20 | Resta scaricabile da CF per l'utente per un periodo da concordare (proposta: 10 anni). |

**Esito verso CasaZen**

| # | Requisito |
|--|------------------------------|
| R21 | CF invia a CasaZen, firmati come nella sezione 3, solo: gli **avvisi di stato** (`case_status_changed`) e **un solo evento finale** per pratica (`case_completed`, `case_rejected` o `case_cancelled`). Nessun altro evento è richiesto. |
| R22 | Se CasaZen non risponde `200`, CF ritenta; CF permette di **rinviare un evento** (a richiesta o da pannello) con lo stesso `eventId`. |
| R23 | `case_completed` contiene i dati attestati e il PDF, nei formati della sezione 3. |

**Dati personali, sicurezza, prova**

| # | Requisito |
|--|------------------------------|
| R24 | Prima del lancio si definisce l'inquadramento privacy (responsabile del trattamento ex art. 28 GDPR o titolare autonomo). I dati sono trattati nello Spazio Economico Europeo e non sono usati per altri scopi né per comunicazioni commerciali all'utente. |
| R25 | HTTPS con TLS 1.2 o superiore; nessun dato personale negli URL; `caseId` e `leaseId` non indovinabili. |
| R26 | Esiste un ambiente di prova con lo stesso comportamento, che emette eventi firmati con un segreto di prova, accetta link firmati con un segreto di prova e permette di ottenere ciascun evento della sezione 3. |

## 2. CasaZen → CF: link al modulo

L'utente apre il modulo CF da CasaZen:

`{CF_FORM_URL}?partner=<code>&leaseId=<guid>&expiresAt=<iso>&signature=<hex>&comuneIstatCode=<istat>&returnUrl=<url-encoded>&theme=<theme>`

Esempio:

```
https://form.concordatofacile.example/start?partner=casazen&leaseId=0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11&expiresAt=2026-10-13T10%3A00%3A00Z&signature=fa10ce472c1e3b33939e62e01a7f5f8cec10d45abc02726f691cae37bd639eea&comuneIstatCode=108040&returnUrl=https%3A%2F%2Fapp.casazen.example%2Fleases%2F0b6f7a3e&theme=casazen
```

| Parametro | Obbligatorio | Note |
|---|---|---|
| `partner` | sì | Codice assegnato da CF; identifica anche il servizio contrattato con CasaZen |
| `leaseId` | sì | Restituito in ogni evento. Lo stesso link serve per aprire la pratica la prima volta e per riaprirla (R11, R14) |
| `expiresAt` | sì | Fine di validità del link, ISO 8601 UTC (proposta: 15 minuti dopo la generazione). Vale solo per l'apertura: la sessione che ne segue è di CF |
| `signature` | sì | `hex(HMAC-SHA256(linkSecret, leaseId + "." + expiresAt))`, in esadecimale minuscolo. `expiresAt` è il valore decodificato, per esempio `2026-10-13T10:00:00Z`. `linkSecret` è un segreto diverso da quello dei webhook, consegnato fuori banda, diverso per prova e produzione |
| `comuneIstatCode` | no | Precompila il comune |
| `returnUrl` | sì | Il pulsante "Torna a CasaZen" porta a `<returnUrl>?leaseId=<guid>&caseId=<id>` (nessun dato personale nell'URL). Il ritorno non comunica l'esito: l'esito arriva solo con l'evento della sezione 3 |
| `theme` | no | Identificativo della veste di CasaZen |

CF rifiuta un link con `signature` errata o con `expiresAt` passato (R6). `comuneIstatCode`, `returnUrl` e `theme` non sono coperti dalla firma.

**Vettore di prova del link** (`linkSecret` `test_link_secret_0123456789abcdef0123456789abcdef`):

```
message   = 0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11.2026-10-13T10:00:00Z
signature = fa10ce472c1e3b33939e62e01a7f5f8cec10d45abc02726f691cae37bd639eea
```

## 3. CF → CasaZen: stato ed esito della pratica (webhook)

`POST {CASAZEN_BASE_URL}/webhooks/concordatofacile` (URL di prova e di produzione comunicati separatamente) · HTTPS · corpo JSON. Il file non viaggia nel corpo (sezione 4). È lo stesso schema degli altri webhook di CasaZen.

| Intestazione | Valore |
|---|---|
| `Content-Type` | `application/json` |
| `X-ConcordatoFacile-Signature` | HMAC-SHA256 in esadecimale minuscolo, senza prefisso |

**Firma:** `X-ConcordatoFacile-Signature = hex(HMAC-SHA256(secret, rawBody))`, dove `rawBody` sono i byte esatti inviati (UTF-8). Il segreto è casuale, di almeno 32 byte, diverso per prova e produzione, consegnato fuori banda.

**Vettore di prova** (segreto `test_secret_0123456789abcdef0123456789abcdef`). Il corpo è **su una sola riga, senza spazi aggiunti**: copiarlo dal file `.md`, non dal PDF.

```
body = {"eventId":"7b0c1c5e-3a52-4d0e-9d3e-0a1f2b3c4d5e","eventType":"case_cancelled","occurredAt":"2026-10-13T09:30:00Z","caseId":"CF-TEST-000001","leaseId":"0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11","data":{"reason":"cancelled_by_user"}}
X-ConcordatoFacile-Signature = 29feda5f7daf037d0aecbe49cdc7a2b8d0ab215cf26b164b0106775c2fd484b3
```

**Risposte di CasaZen.** CasaZen verifica la firma, accoda l'evento e risponde subito: l'elaborazione è asincrona.

| Risposta | Significato | Azione di CF |
|---|---|---|
| `200` `{"received":true}` | Evento accettato (anche se già ricevuto, o per un `leaseId` che CasaZen non conosce: in quel caso lo ignora e lo registra nel log) | consegnato |
| `401` `invalid_signature` | Firma assente o errata | non ritentare, correggere il segreto |
| `5xx`, errore di rete, timeout | Temporaneo | ritentare con backoff (a scelta di CF) |

**Idempotenza e ordine.** `eventId` (UUID) identifica l'evento: un `eventId` già ricevuto è ignorato. Un evento ritentato o rinviato mantiene `eventId` e `occurredAt`. L'ordine di consegna non è garantito: CasaZen ignora un evento con `occurredAt` precedente all'ultimo già applicato per lo stesso `caseId`, e ignora ogni evento ricevuto dopo l'evento finale.

**Busta di ogni evento:**

```json
{
  "eventId": "7b0c1c5e-3a52-4d0e-9d3e-0a1f2b3c4d5e",
  "eventType": "case_completed",
  "occurredAt": "2026-10-15T09:00:00Z",
  "caseId": "CF-2026-000123",
  "leaseId": "0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11",
  "data": {}
}
```

`eventType`: `case_status_changed`, `case_completed`, `case_rejected`, `case_cancelled`.

**`case_status_changed`**: avviso di stato. CasaZen lo usa per avvisare l'utente, nella propria app, che deve agire nel modulo (se è la modalità scelta in R10); CasaZen non risponde e non invia nulla a CF. `status`: `InReview` (CF sta lavorando: all'invio del modulo e dopo ogni azione dell'utente) oppure `ActionRequired` (serve un'azione dell'utente **nel modulo**). Con `ActionRequired`, `reason` è `information_requested` (dati o documenti mancanti, correzioni) oppure `draft_ready` (bozza da approvare e firmare); `detail` è un testo per l'utente, in italiano, che CasaZen può mostrare così com'è: dice che cosa manca o che cosa fare; `dueAt` è opzionale.

```json
{ "status": "ActionRequired", "reason": "information_requested", "detail": "Manca l'APE in corso di validità.", "dueAt": "2026-10-20T12:00:00Z" }
```

```json
{ "status": "InReview" }
```

CasaZen mostra "Azione richiesta" con `detail` e un pulsante che riapre il modulo con il link della sezione 2; ripristina lo stato normale con `InReview`. L'utente invia o corregge i dati sempre e solo nel modulo.

**`case_completed`**: l'attestazione è stata rilasciata. I campi sono quelli che CasaZen usa già per il contratto e per la verifica del canone concordato; CasaZen li confronta con il contratto che ha. I valori sono di esempio.

```json
{
  "attestation": {
    "attestationCode": "ATT-2026-0456",
    "issuedAt": "2026-10-15T00:00:00Z",
    "agreementName": "Accordo locale Quadro — Provincia di Monza e della Brianza",
    "comuneIstatCode": "108040",
    "signatories": [
      { "name": "Organization A (example)", "role": "Proprieta" },
      { "name": "Organization B (example)", "role": "Inquilini" }
    ],
    "property": {
      "cadastralSheet": "12", "cadastralParcel": "345", "cadastralSubaltern": "6", "cadastralCategory": "A/2"
    },
    "lease": {
      "startDate": "2026-11-01T00:00:00Z", "endDate": "2030-10-31T00:00:00Z", "monthlyRent": 400.00
    },
    "parties": [
      { "partyRole": "Landlord", "position": 0, "fiscalCode": "RSSMRA80A01H501U" },
      { "partyRole": "Tenant", "position": 0, "fiscalCode": "BNCLRA85M41F205C" }
    ],
    "assessment": {
      "zone": "Zona 1", "subFascia": 2, "sqm": 65.00, "usableSqm": 65.00,
      "canoneMinAnnuo": 1300.00, "canoneMaxAnnuo": 5525.00,
      "canoneMinMensile": 108.33, "canoneMaxMensile": 460.42
    }
  },
  "file": {
    "fileId": "f-001", "kind": "Attestation", "fileName": "attestation.pdf", "contentType": "application/pdf",
    "downloadUrl": "https://...", "downloadUrlExpiresAt": "2026-10-22T09:00:00Z"
  }
}
```

- `signatories` devono essere **firmatari dell'accordo** del comune: CasaZen li verifica sul proprio archivio.
- Di ogni parte sono inviati solo `partyRole`, `position` (0 per la prima parte di quel ruolo) e `fiscalCode`: servono a riconoscere il contratto, non si inviano altri dati personali.
- `property`, `lease`, `parties` e `assessment` sono i valori **attestati**, cioè quelli scritti nell'attestazione.

**`case_rejected`**: l'attestazione non può essere rilasciata.

```json
{
  "reason": "rent_out_of_range",
  "detail": "Il canone proposto supera il massimo dell'accordo per questa fascia.",
  "limits": { "canoneMinMensile": 108.33, "canoneMaxMensile": 460.42 }
}
```

`reason`: `rent_out_of_range`, `inconsistent_data`, `agreement_not_in_force`, `municipality_not_covered`, `invalid_documents`, `other`. `limits` è opzionale. `detail` è un testo per l'utente, in italiano.

**`case_cancelled`**: pratica chiusa senza esito.

```json
{ "reason": "cancelled_by_user", "detail": "Annullata su richiesta dell'utente." }
```

`reason`: `cancelled_by_user`, `expired`, `other`. `detail` è opzionale.

## 4. File

Il PDF dell'attestazione è descritto da `file` e scaricato da CasaZen da `downloadUrl`. L'oggetto:

```json
{ "fileId": "f-001", "kind": "Attestation", "fileName": "attestation.pdf", "contentType": "application/pdf",
  "downloadUrl": "https://...", "downloadUrlExpiresAt": "2026-10-22T09:00:00Z" }
```

| Campo | Regola |
|---|---|
| `kind` | `Attestation` |
| `contentType` | `application/pdf` |
| `downloadUrl`, `downloadUrlExpiresAt` | HTTPS, senza altre credenziali, **valido per almeno 7 giorni** |

Limite: PDF fino a **20 MB**, lo stesso che CasaZen applica ai propri caricamenti. CasaZen controlla che il file sia davvero un PDF (intestazione `%PDF-`), non solo il `contentType`, e scarta quelli che non lo sono.
