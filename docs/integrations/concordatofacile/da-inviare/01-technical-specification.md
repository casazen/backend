---
title: Specifiche tecniche - integrazione CasaZen / ConcordatoFacile
version: 1.0-bozza
date: 2026-10-13
---

# Specifiche tecniche: integrazione CasaZen / ConcordatoFacile

**Versione 1.0 (bozza)** · 13 ottobre 2026 · Allegato: `02-openapi.yaml` (contratto leggibile da macchina, stessi nomi di questo documento).

Il documento definisce che cosa CasaZen si aspetta da ConcordatoFacile (CF) e che cosa CasaZen invia. Contiene proposte ed esempi. Il testo è in italiano; **tutti i nomi (endpoint, campi, valori) sono in inglese**.

**Convenzioni.** JSON UTF-8. Orari ISO 8601 UTC (`2026-10-13T09:30:00Z`); date `YYYY-MM-DD`. Importi in centesimi interi (campi `...Cents`, valuta `EUR`). Comune = codice ISTAT a 6 cifre (`istatCode`). `reference` = UUID creato da CasaZen: CF lo restituisce in ogni evento. `caseId` = identificativo creato da CF.

**Valori usati.** `level`: `basic`, `complete`. `status`: `received`, `in_review`, `information_requested`, `certified`, `draft_ready`, `awaiting_signature`, `in_registration`, `completed`, `rejected`, `cancelled`. `role` delle parti: `landlord`, `tenant`. `requester.type`: `agency`, `landlord`.

---

## 1. CasaZen → CF: invio di una pratica (proposta)

`POST {CF_BASE_URL}/v1/cases` · `Authorization: Bearer <api key>` (una chiave per la prova, una per la produzione) · `Idempotency-Key: <uuid>` (una chiave ripetuta restituisce la stessa risposta).

```json
{
  "reference": "0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11",
  "level": "complete",
  "requester": {
    "type": "agency",
    "name": "Agenzia Esempio S.r.l.",
    "vatNumber": "01234567890",
    "email": "pratiche@agenzia-esempio.example",
    "phone": "+39 02 0000000"
  },
  "property": {
    "istatCode": "108040",
    "address": "Via Esempio 1",
    "postalCode": "20822",
    "cadastre": { "sheet": "12", "parcel": "345", "subparcel": "6", "category": "A/2" },
    "areaSqm": 65.0
  },
  "parties": [
    { "role": "landlord", "firstName": "Mario", "lastName": "Rossi", "taxCode": "RSSMRA80A01H501U", "ownershipPercent": 100 },
    { "role": "tenant", "firstName": "Anna", "lastName": "Bianchi", "taxCode": "BNCNNA85C41F205Z" }
  ],
  "contract": {
    "startDate": "2026-11-01",
    "termYears": 3,
    "monthlyRentCents": 40000,
    "furnished": false,
    "airConditioning": false,
    "elements": { "A": ["A1", "A2"], "B": ["B2", "B3", "B4", "B5"], "C": ["C4", "C6"], "D": ["D1", "D5"] }
  },
  "casazenCalculation": { "zone": "1", "subBand": 2, "minMonthlyRentCents": 10833, "maxMonthlyRentCents": 46042 },
  "documents": [
    {
      "fileId": "p-001", "kind": "request_form", "fileName": "request-form.pdf", "mimeType": "application/pdf",
      "sizeBytes": 183201, "sha256": "<64 caratteri esadecimali>", "downloadUrl": "https://casazen.example/files/p-001?token=...", "expiresAt": "2026-10-20T09:30:00Z"
    }
  ]
}
```

- `documents[]` sono link che CF scarica (stesso oggetto `File` della sezione 6). `kind` dei documenti che inviamo: `request_form` (modulo di richiesta precompilato, sempre), `lease_contract`, `cadastral_survey`, `floor_plan`, `energy_certificate` (solo se servono).
- `elements` contiene i codici A, B, C, D dichiarati dalle parti. `casazenCalculation` è il nostro calcolo, per il confronto con la verifica di CF.
- `requester` identifica **chi richiede la pratica**: `agency` (agenzia che opera per conto del proprietario) oppure `landlord` (proprietario privato). Con `agency` sono obbligatori `name` e `vatNumber`; con `landlord`, `name` è il nome e cognome e `taxCode` (facoltativo se coincide con quello della parte `landlord`) il codice fiscale. `email` e `phone` sono i recapiti per le comunicazioni sulla pratica. Le parti del contratto restano in `parties[]`: il `requester` non le sostituisce.
- Se per la registrazione CF ha bisogno di un mandato o di una delega dell'agenzia, lo chiede come ogni altro documento mancante, con `case.information_requested` (`documents.mandate`, tipo `file`; sezione 4).
- I campi opzionali possono mancare. CF ignora i campi che non conosce.

**Risposta `201`:**

```json
{ "caseId": "CF-2026-000123", "reference": "0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11", "status": "received", "formUrl": "https://cf.example/form?case=CF-2026-000123" }
```

`formUrl` è opzionale: un link in cui l'utente può completare i dati che CF ancora richiede (vedi sezione 2).

**Risposta `422` (validazione), formato proposto per ogni errore di CF:**

```json
{
  "code": "validation_failed",
  "message": "The request is not valid.",
  "errors": [
    { "field": "parties[0].taxCode", "code": "invalid_format" },
    { "field": "property.cadastre.parcel", "code": "required" }
  ]
}
```

Valori di `code`: `validation_failed`, `unauthorized`, `case_not_found`, `invalid_state`, `file_too_large`, `checksum_mismatch`, `duplicate_request`. Valori di `code` per campo: `required`, `invalid_format`, `out_of_range`, `unknown_value`.

## 2. Alternativa alla sezione 1: link al modulo CF

Se l'endpoint di creazione non è disponibile al lancio, l'utente apre il modulo CF da CasaZen:

`{CF_FORM_URL}?partner=<code>&ref=<reference>&level=<basic|complete>&istat=<istatCode>&return_url=<url-encoded>&theme=<theme id>`

| Parametro | Obbligatorio | Note |
|---|---|---|
| `partner` | sì | Codice assegnato da CF |
| `ref` | sì | `reference` di CasaZen, restituito in ogni evento |
| `level` | sì | `basic` o `complete` |
| `istat` | no | Precompila il comune |
| `return_url` | no | A fine compilazione CF reindirizza a `<return_url>?ref=<reference>&caseId=<id>` (nessun dato personale nell'URL) |
| `theme` | no | Identificativo della veste di CasaZen (nome, logo, colori) |

Un secondo accesso con lo stesso `ref` riapre la stessa pratica: non ne crea un'altra.

## 3. CF → CasaZen: eventi (webhook)

`POST {CASAZEN_BASE_URL}/webhooks/concordatofacile` (URL di prova e di produzione comunicati separatamente) · HTTPS, TLS 1.2 o superiore · corpo JSON, massimo **256 KB**. I file non viaggiano nel corpo (sezione 6).

| Intestazione | Valore |
|---|---|
| `Content-Type` | `application/json` |
| `X-CF-Event-Id` | UUID dell'evento, uguale a `eventId` nel corpo (chiave di idempotenza) |
| `X-CF-Timestamp` | Secondi Unix UTC dell'invio |
| `X-CF-Key-Id` | Identificativo del segreto usato |
| `X-CF-Signature` | `sha256=` + HMAC-SHA256 in esadecimale minuscolo |
| `X-CF-Api-Version` | `1` |

**Firma**

```
message        = X-CF-Timestamp + "." + rawBody          (i byte esatti inviati, UTF-8)
signature      = HMAC-SHA256(secret, message)            (esadecimale minuscolo)
X-CF-Signature = "sha256=" + signature
```

CasaZen rifiuta (`401`) una firma errata, un orario a più di 300 s dal proprio, o un `X-CF-Key-Id` sconosciuto. Il segreto è casuale, di almeno 32 byte, diverso per prova e produzione, consegnato fuori banda. Alla rotazione restano validi due segreti per 7 giorni.

**Vettore di prova** (segreto `test_secret_0123456789abcdef0123456789abcdef`, `X-CF-Timestamp: 1760347800`). Il corpo è **su una sola riga, senza spazi aggiunti**: copiarlo dal file `.md`, non dal PDF.

```
body = {"eventId":"7b0c1c5e-3a52-4d0e-9d3e-0a1f2b3c4d5e","eventType":"case.created","apiVersion":"1","occurredAt":"2026-10-13T09:30:00Z","sequence":1,"caseId":"CF-TEST-000001","reference":"0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11","data":{"level":"complete","istatCode":"108039"}}
X-CF-Signature = sha256=78dd2d480a898d4c9079df175edd0a4f287a6a499bcae20bdb395da6d121dc2e
```

**Risposte di CasaZen e ritentativi.** CasaZen risponde entro 3 secondi e lavora in modo asincrono.

| Risposta | Significato | Azione di CF |
|---|---|---|
| `200 {"received":true}` | Accettato (anche se già ricevuto) | consegnato |
| `401` `invalid_signature` / `timestamp_out_of_range` | Firma o orario errati | non ritentare |
| `422` `unknown_reference` | `reference` sconosciuto | non ritentare |
| `400`, `413` | Corpo non valido o troppo grande | non ritentare |
| `408`, `429`, `5xx`, errore di rete | Temporaneo | ritentare dopo 1 min, 5 min, 30 min, 2 h, 6 h, 12 h |

Un evento ritentato mantiene `eventId`, `occurredAt` e `sequence`; cambiano solo `X-CF-Timestamp` e la firma. L'ordine di consegna non è garantito: `sequence` è un intero che cresce di 1 per ogni pratica, da 1; CasaZen applica uno stato solo se la sua `sequence` è maggiore dell'ultima applicata.

**Busta di ogni evento:**

```json
{
  "eventId": "7b0c1c5e-3a52-4d0e-9d3e-0a1f2b3c4d5e",
  "eventType": "case.status_changed",
  "apiVersion": "1",
  "occurredAt": "2026-10-14T08:00:00Z",
  "sequence": 2,
  "caseId": "CF-2026-000123",
  "reference": "0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11",
  "data": {}
}
```

## 4. Eventi ed esempi (`data`)

**`case.created`** (`level`, `istatCode`)

```json
{ "level": "complete", "istatCode": "108040" }
```

**`case.status_changed`** (`status`: `in_review`, `certified`, `awaiting_signature`, `in_registration`)

```json
{ "status": "in_review" }
```

**`case.information_requested`**: manca un dato o un documento. Formato proposto:

```json
{
  "requestId": "req-001",
  "dueAt": "2026-10-20T12:00:00Z",
  "questions": [
    { "field": "property.cadastre.subparcel", "label": "Subalterno", "type": "text", "required": true, "help": "Come sulla visura catastale" },
    { "field": "contract.furnished", "label": "L'immobile è arredato?", "type": "choice", "required": true, "options": ["yes", "no", "partial"] },
    { "field": "contract.startDate", "label": "Data di decorrenza", "type": "date", "required": true },
    { "field": "property.areaSqm", "label": "Superficie in mq", "type": "number", "required": true },
    { "field": "documents.energy_certificate", "label": "APE in corso di validità", "type": "file", "required": true }
  ]
}
```

`type`: `text`, `number`, `date`, `choice`, `file`. `field` usa i nomi della sezione 1 (percorso con punti); per i documenti `documents.<kind>`. `label` e `help` sono mostrati all'utente così come sono (in italiano). Una richiesta può contenere più domande: CasaZen risponde a tutte con una sola chiamata (sezione 5).

**`case.draft_ready`**: bozza di contratto da approvare e firmare.

```json
{
  "draftId": "draft-001",
  "version": 1,
  "files": [ { "fileId": "f-010", "kind": "contract_draft", "fileName": "contract-draft-v1.pdf", "mimeType": "application/pdf", "sizeBytes": 245123, "sha256": "<64 hex>", "downloadUrl": "https://...", "expiresAt": "2026-10-21T09:00:00Z" } ],
  "allowedActions": ["approve", "request_changes"],
  "notes": "Verificare i dati delle parti.",
  "dueAt": "2026-10-25T12:00:00Z"
}
```

Se vengono chieste modifiche, CF emette un nuovo `case.draft_ready` con `version` maggiore.

**`case.completed`**: tutto consegnato.

```json
{
  "attestation": {
    "attestationId": "ATT-2026-0456",
    "issuedOn": "2026-10-15",
    "agreement": { "name": "Accordo locale Quadro Monza e Brianza", "istatCode": "108040", "inForceFrom": "2024-05-01" },
    "organizations": [
      { "name": "Organization A (example)", "represents": "landlords", "signerName": "Mario Rossi", "signedOn": "2026-10-15" },
      { "name": "Organization B (example)", "represents": "tenants", "signerName": "Anna Bianchi", "signedOn": "2026-10-15" }
    ],
    "rent": { "zone": "1", "subBand": 2, "areaSqm": 65.0, "minMonthlyRentCents": 10833, "maxMonthlyRentCents": 46042, "agreedMonthlyRentCents": 40000 },
    "documentFileId": "f-001"
  },
  "registration": {
    "type": "L2", "revenueOffice": "Desio", "registeredOn": "2026-10-19",
    "electronicProtocol": "TEST-PROTO-0001", "contractIdentificationCode": "TEST-CODE-0001",
    "flatTaxElected": true, "registrationTaxCents": 0, "stampDutyCents": 0, "receiptFileIds": ["f-003", "f-004"]
  },
  "files": [
    { "fileId": "f-001", "kind": "attestation", "fileName": "attestation.pdf", "mimeType": "application/pdf", "sizeBytes": 412345, "sha256": "<64 hex>", "downloadUrl": "https://...", "expiresAt": "2026-10-28T15:02:11Z" },
    { "fileId": "f-003", "kind": "submission_receipt", "fileName": "submission-receipt.pdf", "mimeType": "application/pdf", "sizeBytes": 51234, "sha256": "<64 hex>", "downloadUrl": "https://...", "expiresAt": "2026-10-28T15:02:11Z" },
    { "fileId": "f-004", "kind": "registration_receipt", "fileName": "registration-receipt.pdf", "mimeType": "application/pdf", "sizeBytes": 48110, "sha256": "<64 hex>", "downloadUrl": "https://...", "expiresAt": "2026-10-28T15:02:11Z" },
    { "fileId": "f-002", "kind": "full_package_zip", "fileName": "case-CF-2026-000123.zip", "mimeType": "application/zip", "sizeBytes": 2345678, "sha256": "<64 hex>", "downloadUrl": "https://...", "expiresAt": "2026-10-28T15:02:11Z" }
  ]
}
```

- `registration` è presente solo se la registrazione è inclusa nel `level`; `type` è `L1` o `L2`.
- Le organizzazioni in `attestation.organizations` devono essere **firmatarie dell'accordo** del comune: CasaZen le verifica sul proprio archivio.
- Le ricevute sono consegnate **anche come PDF separati**, non solo dentro lo zip.

**`case.rejected`**: l'attestazione non può essere rilasciata.

```json
{
  "reason": "rent_out_of_range",
  "detail": "Il canone proposto supera il massimo dell'accordo per questa fascia.",
  "limits": { "minMonthlyRentCents": 10833, "maxMonthlyRentCents": 46042 }
}
```

`reason`: `rent_out_of_range`, `inconsistent_data`, `agreement_not_in_force`, `municipality_not_covered`, `invalid_documents`, `other`. `limits` è opzionale.

**`case.cancelled`**: pratica chiusa senza esito.

```json
{ "reason": "Annullata su richiesta dell'utente." }
```

## 5. CasaZen → CF: risposte e documenti (proposta)

Base `{CF_BASE_URL}/v1`, `Authorization: Bearer <api key>`, `Idempotency-Key: <uuid>`, errori come alla sezione 1.

**Esempio: dato mancante** (risposta al `case.information_requested` della sezione 4). Prima il file, poi le risposte:

```
POST /v1/cases/CF-2026-000123/documents
Content-Type: multipart/form-data
  kind=requested_document   requestId=req-001   sha256=<64 hex>   file=@energy-certificate.pdf

201 Created
{ "fileId": "file-9f2" }
```

```
POST /v1/cases/CF-2026-000123/answers
{
  "type": "information",
  "requestId": "req-001",
  "answers": [
    { "field": "property.cadastre.subparcel", "value": "6" },
    { "field": "contract.furnished", "value": "yes" },
    { "field": "contract.startDate", "value": "2026-11-01" },
    { "field": "property.areaSqm", "value": "65.0" },
    { "field": "documents.energy_certificate", "fileId": "file-9f2" }
  ]
}

202 Accepted
{ "received": true }
```

`value` è sempre una stringa (per `date`: `YYYY-MM-DD`; per `number`: decimale con il punto). Una domanda che non è nella richiesta, o una obbligatoria mancante, restituisce `422` con gli errori per campo della sezione 1. Dopo una risposta valida CF emette `case.status_changed` (`in_review`) o l'evento successivo.

**Esempio: approvazione della bozza e contratto firmato:**

```
POST /v1/cases/CF-2026-000123/answers
{ "type": "draft_decision", "draftId": "draft-001", "decision": "approved", "notes": "" }
→ 202 { "received": true }

POST /v1/cases/CF-2026-000123/documents
  kind=signed_contract   sha256=<64 hex>   file=@signed-contract.pdf
→ 201 { "fileId": "file-a41" }
```

`decision`: `approved` oppure `changes_requested` (con `notes`). Con `approved`, CF emette `case.status_changed` con `awaiting_signature` finché non riceve il `signed_contract`.

Se le due chiamate non sono disponibili al lancio, l'alternativa provvisoria è un link di ritorno al modulo CF (sezione 2) con lo stesso `ref`: l'utente completa lì il passaggio e CF emette lo stesso evento successivo.

## 6. File

I file sono elencati in `files[]` (eventi) o `documents[]` (sezione 1) e scaricati da chi li riceve. L'oggetto:

```json
{ "fileId": "f-001", "kind": "attestation", "fileName": "attestation.pdf", "mimeType": "application/pdf",
  "sizeBytes": 412345, "sha256": "<64 esadecimali minuscoli>", "downloadUrl": "https://...", "expiresAt": "2026-10-28T15:02:11Z" }
```

| Campo | Regola |
|---|---|
| `kind` | Eventi: `attestation`, `calculation_sheet`, `contract_draft`, `signed_contract`, `submission_receipt`, `registration_receipt`, `payment_receipt`, `full_package_zip`, `other`. Creazione pratica: `request_form`, `lease_contract`, `cadastral_survey`, `floor_plan`, `energy_certificate`. Caricamento: `signed_contract`, `requested_document`, `other` |
| `mimeType` | `application/pdf` oppure `application/zip` |
| `sizeBytes`, `sha256` | Dimensione e SHA-256 (esadecimale minuscolo): il ricevente scarta un file che non coincide |
| `downloadUrl`, `expiresAt` | HTTPS, senza altre credenziali, **valido per almeno 7 giorni** |

Limiti: PDF fino a 20 MB; zip fino a 40 MB compresso e 120 MB non compresso, al massimo 20 voci, nessuno zip annidato, nomi dei file senza percorsi né `..`.
