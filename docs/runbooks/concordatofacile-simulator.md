# ConcordatoFacile simulator (CF-SIM)

Task CF-SIM of the redesign wave. The whole round of the agreed-rent contract goes through ConcordatoFacile (CF): contract sheet, privacy,
payment, **CF's form** (opened with CasaZen's signed link), the practice appears in CF's status API, CF e-mails the contract, the attestation
and the receipt of the registration, the user uploads them in CasaZen (`?practice=P-…`), reading, import, signed contract, registration
declared. CF has no interactive test environment (its specification v0.2: the production form rejects the test secret, the test status
endpoint has a key that CF has not delivered yet, and the form is always `https://app.concordatofacile.it/`), so on the **test
environment** the person who tries the round would stop at the click that leaves CasaZen. The simulator plays CF there, so that the round
can be walked in full; it switches off with one variable when CF delivers its own test environment.

It is **for test environments only**. Production always talks to the real CF.

Pages of CasaZen that depend on it: the contract sheet and the offer page (they open the link), the upload page (`?practice=`), the status
section. The pages of the simulated module are the web app's `/guided-form` (frontend repository, task CF-SIM).

## 1. What it simulates

| Part | Real CF | With the simulator |
|---|---|---|
| The form | `https://app.concordatofacile.it/?partner=casazen&leaseId=…&expiresAt=…&signature=…&comune=…&provincia=…` | `{ConcordatoFacile:BaseUrl}/guided-form?…` with **the same parameters and the same signature** (the real signing code is the one that runs) |
| Verification of the link | CF verifies signature and expiry | `GET api/cf-simulator/link` verifies it **as the specification v0.2 §2 describes**, with code written from that text and not shared with the signing (`CfSimulatorLinkVerifier`) |
| The practice | born when the user submits the form, in state `Received` | `POST api/cf-simulator/practices`: row of `cf_simulated_practices`, number `P-` + 16 hex |
| The status API | `GET /v1/leases/{leaseId}/practices` and `GET /v1/practices/{practiceId}` (key in `Authorization: Bearer`) | `SimulatedConcordatoFacileApiClient` (`IConcordatoFacileApiClient`), same shapes, read from the table; **no key is asked** |
| The states | Received, InProgress, AwaitingUser, DraftSent, SignedReceived, SentToAssociation, Attested, Registered, ClosedWithoutAttestation (+ `closeReason`), Suspended | any of them, set from the test panel of `/guided-form` (`POST api/cf-simulator/practices/{id}/status`) |
| The three e-mails | contract, attestation, receipt arrive by e-mail | sample PDFs, downloaded from the panel (`GET api/cf-simulator/practices/{id}/documents/{contract\|attestation\|receipt}`) |
| The second practice of a contract | CF closes the first as `ClosedWithoutAttestation` / `duplicate` (v0.2 §6) | the same |
| A link that is missing, wrong or expired | the form can be filled in all the same, the practice arrives **without a lease** (v0.2 §1) | the same: `valid: false`, and the practice has `leaseId: null` |

Not simulated: CF's own screens, the e-mails, the association that attests, the fee, the other services CF may add, the rate limits of the
status API, the privacy framework (R24). The simulator is not a model of CF's internal flow: any state can be set at any time.

## 2. Why it cannot start in production

`ConcordatoFacile:FormMode` is `Real` (default) or `Simulated`. Three layers keep `Simulated` out of production:

1. **Startup.** `ConcordatoFacileOptionsValidator` (`ValidateOnStart`) fails when `FormMode=Simulated` and `ASPNETCORE_ENVIRONMENT` is
   `Production`, and when `ConcordatoFacile:BaseUrl` is not a valid address. The deployment does not start; nothing runs with the wrong
   mode. The environments are named, as the rest of the platform expects (`docs/INFRA.md`, `deploy-checklist.md`, `BillingConfiguration`):
   Railway **test** = `Staging`, Railway **production** = `Production`. No second variable is needed because the names differ and the
   difference is already enforced: the startup refuses a live Stripe key outside `Production` and a test key inside it, so an environment
   renamed to get round this guard could not run with the live keys of production.
2. **Request time.** `[CfSimulatorOnly]` on the controller answers 404 (like a route that does not exist, before the action and the model
   binding) unless `FormMode=Simulated` **and** the environment is not `Production`. With `Real` every `api/cf-simulator/*` endpoint is a 404.
3. **Selection of the status client.** The status cache gets `SimulatedConcordatoFacileApiClient` only with `FormMode=Simulated`; otherwise
   the HTTP client of the real status API, as before.

Tests: `ConcordatoFacileFormModeTests` (the guard), `CfSimulatorRealModeTests` (404 with `Real`, real client selected).

## 3. Variables to set on the test environment

| Variable | Value | Note |
|---|---|---|
| `ConcordatoFacile__FormMode` | `Simulated` | the switch. Unset or `Real` = real CF |
| `ConcordatoFacile__BaseUrl` | `https://<web app host of the test environment>` | no trailing slash; **already needed by the checkout** (Stripe's return address); the simulated link is `{BaseUrl}/guided-form` |
| `ConcordatoFacile__LinkSecret` | the secret the test environment already has | signs the link and verifies it; 32+ characters; the same variable as today |
| `ConcordatoFacile__Simulator__StatusTtlSeconds` | `5` (default) | optional; seconds the status cache keeps what it read from the simulator |
| `Features__ConcordatoFacile` | `true` | already on in the test environment; the whole service stays behind it |

`ConcordatoFacile__StatusApiKey` is **not** needed (and ignored) while `Simulated`. Production: none of these changes.

The deployment applies the migration `AddCfSimulatedPractices` (a new table, additive, with the comment «solo per ambienti di prova»);
production gets the empty table with the next release and never uses it.

## 4. Trying the round

1. Create a contract and get to the payment and the form as usual: the click that used to leave for CF opens `/guided-form` of the web app.
2. The page says it is a test environment, shows the municipality received and whether the link is good (it is not good when it expired
   after 15 minutes; the form can be sent all the same and the practice arrives without a reference).
3. **Invia la pratica**: the practice is born (`Received`). The panel shows its number and its state.
4. In the **test panel**: choose a state (and the reason of closing for `ClosedWithoutAttestation`) and apply; download the sample contract,
   attestation and receipt; **Carica i dati in CasaZen** opens `/app/long-rent/documents/upload?practice=P-…`, like CF's button.
5. The contract sheet shows the state within a few seconds (`StatusTtlSeconds`), then upload the three PDFs, read, import, declare the
   registration.

How to tell that the simulator is on: `GET {api}/api/cf-simulator/link` answers **200** `{"valid":false,"reason":"missing_parameters",…}`
(with the real mode it answers **404** `not_found`). The contract sheet link starts with `{web app}/guided-form`.

## 5. Endpoints

All anonymous (as the real form), rate limited per IP (`PublicRead`), bodies ≤ 4 KB, `Cache-Control: private, no-store`, 404 unless `Simulated`.

| Endpoint | Does |
|---|---|
| `GET api/cf-simulator/link?partner&leaseId&expiresAt&signature&comune&provincia` | `{ valid, reason?, leaseId, comune, provincia, practices[] }`. `practices` (state of the contract's practices) only for a good link. `reason` ∈ `missing_parameters`, `partner_invalid`, `lease_id_invalid`, `expires_at_invalid`, `signature_invalid`, `expired`, `too_far`, `secret_not_configured` |
| `POST api/cf-simulator/practices?<same parameters>` | 201 `{ practiceId, controlToken, status, statusChangedAt, linked }`. The token is shown **only here** (stored as SHA-256). A second practice of the same contract closes the earlier non-final one as `duplicate` |
| `POST api/cf-simulator/practices/{practiceId}/status` `{ controlToken, status, closeReason? }` | sets any state (`Unknown` is not a state of the service), `StatusChangedAt` = now (never goes back); `closeReason` ∈ `duplicate`, `withdrawn`, `not_compliant`, `other` (only for `ClosedWithoutAttestation`, `other` when missing). 404 for an unknown practice **or a wrong token** (not told apart) |
| `GET api/cf-simulator/practices/{practiceId}/documents/{kind}?controlToken=…` | PDF, `kind` ∈ `contract`, `attestation`, `receipt`. Same 404 rule |

**Link verification (v0.2 §2)**: `partner=casazen`; `leaseId` a GUID (upper or lower case); `expiresAt` ISO 8601 UTC with `T` and `Z` in
capitals, to the second or with 1–7 decimals (an offset, a space for the `T`, lowercase `t`/`z` are refused); `signature` = hex of
HMAC-SHA256 with the link secret as UTF-8 key of `leaseId + "." + expiresAt`, **on the text exactly as received**; a link expired by more
than 2 minutes is refused, and one that expires more than 60 minutes after the moment it is opened.

## 6. The sample documents

Each PDF carries, in plain sight, «Documento di esempio generato dall'ambiente di prova.» (the renderer of the application,
`IPdfDocumentRenderer`, A4, embedded fonts).

- **Contract** (`CfSimulatorDocuments.Contract`): a canone concordato 3+2 written to be **read by the local reading** of the lease documents
  (`lease-document-extraction.md`; layout of a form, like the hand-written contracts of its tests): signed **today** (Europe/Rome), starts
  the **first day of next month**, lasts 3 years, rent 650,00 € a month, deposit 1.300,00 € (two months), landlord Mario Rossi
  `RSSMRA80A01H501U` and tenant Giulia Verdi `VRDGLI85B42F205E` (invented people, valid check characters), the **address and municipality of
  the property of the practice's lease** so the comparison with the property finds a match. A practice without a lease gets «Via Roma 1,
  Milano». One page on purpose (a page footer would cut a sentence). `CfSimulatorContractPdfTests` renders it, reads it back with the real
  `ContractTextReader` and `LeaseContractTextParser` and checks people, type, rent, deposit, dates and property: it is also the first test
  of the whole way «a real PDF to the reading».
- **Attestation** and **receipt of the registration**: short, with a number and a date of example; the system archives them and does not
  read them.

## 7. Status cache

`ConcordatoFacileStatusCacheService` keeps the practices of a lease 15 minutes (5 minutes after the opening of the link, 5 after an error)
and the lookup of a practice 15 minutes (1 when unknown). With `Simulated` all these times are `StatusTtlSeconds` (default 5), otherwise a
person who changes a state would wait minutes to see it. The sheet polls at `nextUpdateAt`, so it follows by itself.

## 8. Data and cleaning

Table `cf_simulated_practices` (no tenant: it is the data of an outside service, listed in the allow-list of
`TenantQueryFilterArchitectureTests`): `PracticeId` (unique), `LeaseId` (no foreign key: a practice can arrive without a lease and the lease
can be deleted), `Comune`, `Provincia`, `Status`, `CloseReason`, `SubmittedAt`, `StatusChangedAt`, `ControlTokenHash`. No personal data. It is
empty and inert with `Real`. To clean the test environment: `DELETE FROM cf_simulated_practices;`.

## 9. Going over to the real CF

When CF delivers its test environment (or for production): **unset `ConcordatoFacile__FormMode`** (or `Real`) and set
`ConcordatoFacile__LinkSecret` (the secret CF issues for that environment) and `ConcordatoFacile__StatusApiKey` (`cfsp_test_…` /
`cfsp_live_…`). Nothing else changes: the same signing code produces the link to `https://app.concordatofacile.it/`, the status client is the
HTTP one. The practices left in the table are never read again. The frontend page `/guided-form` answers «Pagina non disponibile» (the
endpoints are 404).
