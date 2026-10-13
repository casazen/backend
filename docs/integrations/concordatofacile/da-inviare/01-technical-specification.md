---
title: Specifiche tecniche - integrazione CasaZen / ConcordatoFacile
version: 1.2-bozza
date: 2026-10-13
---

# Specifiche tecniche: integrazione CasaZen / ConcordatoFacile

**Versione 1.2 (bozza)** · 13 ottobre 2026 · Allegato: `02-openapi.yaml` (contratto leggibile da macchina, stessi nomi di questo documento).

**Principio.** L'utente compila il **modulo di ConcordatoFacile (CF)**, che resta sul dominio CF con la veste di CasaZen. CasaZen non invia i dati della pratica. Per far restare l'utente nell'app di CasaZen definiamo solo questi comportamenti: il link al modulo (sezione 1), gli eventi che CF invia a CasaZen (sezioni 2 e 3), le risposte e i documenti che CasaZen restituisce a CF (sezione 4) e le regole sui file (sezione 5). Proposte ed esempi sono inclusi. Il testo è in italiano; **i nomi (endpoint, campi, valori) sono in inglese**, tranne i pochi già italiani nell'API di CasaZen (`canoneMinMensile`, `subFascia`, `Proprieta`, `Inquilini`, `CedolareSecca`, `Ordinario`), che manteniamo uguali per non dover mappare nulla.

**Convenzioni.** Sono quelle già in uso nell'API di CasaZen, così nessun campo va adattato dopo l'integrazione.

| Tema | Regola |
|---|---|
| JSON | UTF-8, nomi dei campi in `camelCase` |
| Valori enumerati | Stringa con il nome del membro, per esempio `Tenant`, `CedolareSecca` (mai numeri) |
| Date e orari | ISO 8601 UTC con `Z`: `2026-10-13T09:30:00Z`. Una data di calendario (`startDate`, `registrationDate`) è la mezzanotte UTC: `2026-11-01T00:00:00Z` |
| Importi | Numero decimale in euro, due decimali, nessuna conversione in centesimi: `monthlyRent: 400.00`. Valuta sempre EUR |
| Comune | `comuneIstatCode`: codice ISTAT a 6 cifre come stringa (`"108040"`) |
| Identificativi | `leaseId` = GUID del contratto in CasaZen, restituito da CF in ogni evento. `caseId` = identificativo creato da CF |
| Errori | `application/problem+json` con `code` in snake_case (sezione 4) |

---

## 1. Link al modulo CF

L'utente apre il modulo CF da CasaZen:

`{CF_FORM_URL}?partner=<code>&leaseId=<guid>&level=<basic|complete>&comuneIstatCode=<istat>&returnUrl=<url-encoded>&theme=<theme>`

Esempio:

```
https://form.concordatofacile.example/start?partner=casazen&leaseId=0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11&level=complete&comuneIstatCode=108040&returnUrl=https%3A%2F%2Fapp.casazen.example%2Fleases%2F0b6f7a3e&theme=casazen
```

| Parametro | Obbligatorio | Note |
|---|---|---|
| `partner` | sì | Codice assegnato da CF |
| `leaseId` | sì | Restituito in ogni evento |
| `level` | sì | Valori di CF (qui `basic`, `complete`: da confermare con i vostri nomi) |
| `comuneIstatCode` | no | Precompila il comune |
| `returnUrl` | no | A fine compilazione CF reindirizza a `<returnUrl>?leaseId=<guid>&caseId=<id>` (nessun dato personale nell'URL) |
| `theme` | no | Identificativo della veste di CasaZen (nome, logo, colori) |

Un secondo accesso con lo stesso `leaseId` riapre la stessa pratica: non ne crea un'altra. Quando l'utente invia il modulo, CF emette `case_created` (sezione 3).

## 2. CF → CasaZen: eventi (webhook)

`POST {CASAZEN_BASE_URL}/webhooks/concordatofacile` (URL di prova e di produzione comunicati separatamente) · HTTPS · corpo JSON. I file non viaggiano nel corpo (sezione 5). È lo stesso schema degli altri webhook di CasaZen.

| Intestazione | Valore |
|---|---|
| `Content-Type` | `application/json` |
| `X-ConcordatoFacile-Signature` | HMAC-SHA256 in esadecimale minuscolo, senza prefisso |

**Firma:** `X-ConcordatoFacile-Signature = hex(HMAC-SHA256(secret, rawBody))`, dove `rawBody` sono i byte esatti inviati (UTF-8). Il segreto è casuale, di almeno 32 byte, diverso per prova e produzione, consegnato fuori banda.

**Vettore di prova** (segreto `test_secret_0123456789abcdef0123456789abcdef`). Il corpo è **su una sola riga, senza spazi aggiunti**: copiarlo dal file `.md`, non dal PDF.

```
body = {"eventId":"7b0c1c5e-3a52-4d0e-9d3e-0a1f2b3c4d5e","eventType":"case_created","occurredAt":"2026-10-13T09:30:00Z","caseId":"CF-TEST-000001","leaseId":"0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11","data":{"level":"complete","comuneIstatCode":"108040"}}
X-ConcordatoFacile-Signature = 0791287ff20b268465b053546db639f3cc1c47b0b17d999ebb39077477560027
```

**Risposte di CasaZen.** CasaZen verifica la firma, accoda l'evento e risponde subito: l'elaborazione è asincrona.

| Risposta | Significato | Azione di CF |
|---|---|---|
| `200` `{"received":true}` | Evento accettato (anche se già ricevuto, o per un `leaseId` che CasaZen non conosce: in quel caso lo ignora e lo registra nel log) | consegnato |
| `401` `invalid_signature` | Firma assente o errata | non ritentare, correggere il segreto |
| `5xx`, errore di rete, timeout | Temporaneo | ritentare con backoff (a scelta di CF) |

**Idempotenza e ordine.** `eventId` (UUID) identifica l'evento: un `eventId` già ricevuto è ignorato. Un evento ritentato mantiene `eventId` e `occurredAt`. L'ordine di consegna non è garantito: CasaZen ignora un evento con `occurredAt` precedente all'ultimo già applicato per lo stesso `caseId`.

**Busta di ogni evento:**

```json
{
  "eventId": "7b0c1c5e-3a52-4d0e-9d3e-0a1f2b3c4d5e",
  "eventType": "case_status_changed",
  "occurredAt": "2026-10-14T08:00:00Z",
  "caseId": "CF-2026-000123",
  "leaseId": "0b6f7a3e-5a2d-4c58-9e0a-6c8b0f2e1d11",
  "data": {}
}
```

`eventType`: `case_created`, `case_status_changed`, `case_information_requested`, `case_draft_ready`, `case_completed`, `case_rejected`, `case_cancelled`.

## 3. Eventi ed esempi (`data`)

**`case_created`**: l'utente ha inviato il modulo.

```json
{ "level": "complete", "comuneIstatCode": "108040" }
```

**`case_status_changed`** (`status`: `InReview`, `Certified`, `AwaitingSignature`, `InRegistration`)

```json
{ "status": "InReview" }
```

**`case_information_requested`**: manca un dato o un documento. L'utente risponde dall'app di CasaZen (sezione 4). Formato proposto:

```json
{
  "requestId": "req-001",
  "dueAt": "2026-10-20T12:00:00Z",
  "questions": [
    { "field": "cadastralSubaltern", "type": "string", "required": true },
    { "field": "isFurnished", "type": "boolean", "required": true },
    { "field": "startDate", "type": "date", "required": true },
    { "field": "sqm", "type": "decimal", "required": true },
    { "field": "taxRegime", "type": "enum", "options": ["CedolareSecca", "Ordinario"], "required": true },
    { "field": "fiscalCode", "partyRole": "Tenant", "position": 0, "type": "string", "required": true },
    { "field": "document", "documentType": "Ape", "type": "file", "required": true }
  ]
}
```

`type`: `string`, `integer`, `decimal`, `boolean`, `date`, `enum` (con `options`), `file`. Una richiesta può contenere più domande: CasaZen risponde a tutte con una sola chiamata (sezione 4).

`field` è **sempre un nome del catalogo seguente**: sono i campi che CasaZen ha già e che mostra con i propri testi localizzati (IT/EN), quindi CF non invia `label` né `help`. Per i campi delle parti, `partyRole` (`Landlord` o `Tenant`) e `position` (0 per la prima parte di quel ruolo) dicono di quale parte si tratta. Per un documento, `field` è `document` e `documentType` è un valore di `DocumentType` di CasaZen (qui `Ape`; gli altri: `CinCertificate`, `FloorPlan`, `InsurancePolicy`, `PropertyLicense`, `SafetyCompliance`, `Other`).

| Gruppo | `field` (tipo) |
|---|---|
| Immobile | `comuneIstatCode` (string, 6 cifre), `cadastralSheet`, `cadastralParcel`, `cadastralSubaltern`, `cadastralCategory` (string), `cadastralIncome` (decimal, euro) |
| Contratto | `startDate`, `endDate` (date), `monthlyRent`, `securityDeposit` (decimal), `taxRegime` (enum: `CedolareSecca`, `Ordinario`) |
| Superfici | `sqm`, `garageSqm`, `balconySqm`, `otherAppurtenanceSqm`, `privateGreenSqm` (decimal, mq) |
| Elementi di qualità | `typeAElementCount`, `typeBElementCount`, `typeCElementCount`, `typeDElementCount`, `qualifyingTypeDElementCount` (integer) |
| Dotazioni | `stoveHeating`, `isFurnished`, `airConditioning` (boolean) |
| Zona | `zoneName` (string) |
| Parti (con `partyRole`, `position`) | `firstName`, `lastName`, `fiscalCode` (16 caratteri), `citizenship` (ISO 3166-1 alpha-2), `contactEmail` (string), `isExtraEU` (boolean) |
| Documenti | `document` (file, con `documentType`), `apeCode`, `apeEnergyClass` (string) |

Se CF ha bisogno di un dato che non è nel catalogo, lo segnala come richiesta di estensione del catalogo (non come domanda): è un'aggiunta da concordare, non un campo libero.

**`case_draft_ready`**: bozza di contratto da approvare e firmare.

```json
{
  "draftId": "draft-001",
  "version": 1,
  "files": [
    { "fileId": "f-010", "kind": "ContractDraft", "fileName": "contract-draft-v1.pdf", "contentType": "application/pdf",
      "downloadUrl": "https://...", "downloadUrlExpiresAt": "2026-10-21T09:00:00Z" }
  ],
  "dueAt": "2026-10-25T12:00:00Z"
}
```

Se vengono chieste modifiche, CF emette un nuovo `case_draft_ready` con `version` maggiore.

**`case_completed`**: tutto consegnato. I nomi dei campi sono quelli già usati da CasaZen per la verifica del canone concordato e per la registrazione (valori di esempio).

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
    "assessment": {
      "zone": "Zona 1", "subFascia": 2, "sqm": 65.00, "usableSqm": 65.00,
      "canoneMinAnnuo": 1300.00, "canoneMaxAnnuo": 5525.00,
      "canoneMinMensile": 108.33, "canoneMaxMensile": 460.42,
      "monthlyRent": 400.00
    }
  },
  "registration": {
    "registrationCode": "TEST-REG-0001",
    "registrationDate": "2026-10-19T00:00:00Z",
    "taxRegime": "CedolareSecca"
  },
  "files": [
    { "fileId": "f-001", "kind": "Attestation", "fileName": "attestation.pdf", "contentType": "application/pdf",
      "downloadUrl": "https://...", "downloadUrlExpiresAt": "2026-10-28T15:02:11Z" },
    { "fileId": "f-004", "kind": "RegistrationReceipt", "fileName": "registration-receipt.pdf", "contentType": "application/pdf",
      "downloadUrl": "https://...", "downloadUrlExpiresAt": "2026-10-28T15:02:11Z" }
  ]
}
```

- `registration` è presente solo se la registrazione è inclusa nel `level`. `registrationCode` è il numero o protocollo dell'Agenzia delle Entrate così come scritto sulla ricevuta (massimo 100 caratteri); CasaZen segna la registrazione come eseguita solo con `registrationCode`, `registrationDate` e il file `RegistrationReceipt`.
- I `signatories` devono essere **firmatari dell'accordo** del comune (`role`: `Proprieta` o `Inquilini`): CasaZen li verifica sul proprio archivio.
- La ricevuta di registrazione è un **PDF separato** (`RegistrationReceipt`). Altri documenti (per esempio la ricevuta di pagamento) hanno `kind` `Other`.

**`case_rejected`**: l'attestazione o la registrazione non possono essere completate.

```json
{
  "reason": "rent_out_of_range",
  "detail": "Il canone proposto supera il massimo dell'accordo per questa fascia.",
  "limits": { "canoneMinMensile": 108.33, "canoneMaxMensile": 460.42 }
}
```

`reason`: `rent_out_of_range`, `inconsistent_data`, `agreement_not_in_force`, `municipality_not_covered`, `invalid_documents`, `registration_rejected`, `other`. `limits` è opzionale.

**`case_cancelled`**: pratica chiusa senza esito.

```json
{ "reason": "cancelled_by_user", "detail": "Annullata su richiesta dell'utente." }
```

`reason`: `cancelled_by_user`, `expired`, `other`.

## 4. CasaZen → CF: risposte e documenti (proposta)

È il solo punto in cui CF espone endpoint: servono per far tornare a CF ciò che l'utente fa nell'app di CasaZen. Base `{CF_BASE_URL}/v1`, `Authorization: Bearer <api key>` (una chiave per la prova, una per la produzione), `Idempotency-Key: <uuid>` (una chiave ripetuta restituisce la stessa risposta).

**Esempio: dato mancante** (risposta al `case_information_requested` della sezione 3). Prima il file, poi le risposte:

```
POST /v1/cases/CF-2026-000123/documents
Content-Type: multipart/form-data
  kind=RequestedDocument   requestId=req-001   documentType=Ape   file=@energy-certificate.pdf

201 Created
{ "fileId": "file-9f2" }
```

```
POST /v1/cases/CF-2026-000123/answers
{
  "type": "Information",
  "requestId": "req-001",
  "answers": [
    { "field": "cadastralSubaltern", "value": "6" },
    { "field": "isFurnished", "value": true },
    { "field": "startDate", "value": "2026-11-01T00:00:00Z" },
    { "field": "sqm", "value": 65.00 },
    { "field": "taxRegime", "value": "CedolareSecca" },
    { "field": "fiscalCode", "partyRole": "Tenant", "position": 0, "value": "RSSMRA80A01H501U" },
    { "field": "document", "documentType": "Ape", "fileId": "file-9f2" }
  ]
}

202 Accepted
{ "received": true }
```

`value` ha il tipo JSON del campo (stringa, booleano, numero decimale) e il formato della sezione "Convenzioni". Dopo una risposta valida CF emette `case_status_changed` (`InReview`) o l'evento successivo.

**Esempio: approvazione della bozza e contratto firmato:**

```
POST /v1/cases/CF-2026-000123/answers
{ "type": "DraftDecision", "draftId": "draft-001", "decision": "Approved" }
→ 202 { "received": true }

POST /v1/cases/CF-2026-000123/documents
  kind=SignedContract   file=@signed-contract.pdf
→ 201 { "fileId": "file-a41" }
```

`decision`: `Approved` oppure `ChangesRequested` (con `notes`). Con `Approved`, CF emette `case_status_changed` con `AwaitingSignature` finché non riceve il `SignedContract`.

**Formato degli errori di CF** (proposto: lo stesso delle API di CasaZen, `application/problem+json`):

```json
{
  "status": 400,
  "code": "validation_error",
  "errors": {
    "cadastralSubaltern": ["The field is required."],
    "startDate": ["The value is not a valid date."]
  }
}
```

Valori di `code` (quelli già usati da CasaZen): `bad_request`, `validation_error`, `unauthorized`, `forbidden`, `not_found`, `conflict`, `payload_too_large`, `unsupported_media_type`, `business_rule_violation`, `too_many_requests`. Uso proposto: caso sconosciuto → `404` `not_found`; richiesta già risposta o stato non valido → `409` `conflict`; domanda non presente nella richiesta o risposta obbligatoria mancante → `400` `validation_error`; file troppo grande → `413` `payload_too_large`. CasaZen ritenta solo su `5xx`, `429` e timeout.

**Se queste chiamate non sono disponibili al lancio**, l'alternativa provvisoria è il link di ritorno al modulo CF (sezione 1) con lo stesso `leaseId`: l'utente completa lì il passaggio e CF emette lo stesso evento successivo.

## 5. File

I file sono elencati in `files[]` (eventi) e scaricati da CasaZen; i file che CasaZen restituisce a CF viaggiano con `POST .../documents`. L'oggetto:

```json
{ "fileId": "f-001", "kind": "Attestation", "fileName": "attestation.pdf", "contentType": "application/pdf",
  "downloadUrl": "https://...", "downloadUrlExpiresAt": "2026-10-28T15:02:11Z" }
```

| Campo | Regola |
|---|---|
| `kind` | Eventi: `Attestation`, `ContractDraft`, `SignedContract`, `RegistrationReceipt`, `Other`. Caricamento: `SignedContract`, `RequestedDocument` (con `requestId` e `documentType`) |
| `contentType` | `application/pdf`. Se CF invia anche uno zip dell'intero fascicolo, CasaZen lo ignora |
| `downloadUrl`, `downloadUrlExpiresAt` | HTTPS, senza altre credenziali, **valido per almeno 7 giorni** |

Limiti, gli stessi che CasaZen applica ai propri caricamenti: PDF fino a **20 MB** (contratto, attestazione) e fino a **10 MB** (ricevuta). CasaZen controlla che il file sia davvero un PDF (intestazione `%PDF-`), non solo il `contentType`, e scarta quelli che non lo sono.
