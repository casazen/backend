# Runbook: encryption at rest of guest documents and Questura credentials

Task CO-14. Audit defect: A5-30. Related: FD-07 (Data Protection key ring in the database), FD-20 (OTA secrets),
PC-11 (iCal import URLs), CO-09 (audited reveal of document numbers), TN-1/TN-3 (tenant and authorization).

## 1. What is encrypted

Every encrypted column is declared in **one place**, `Casazen.Infrastructure/Data/Encryption/EncryptedColumns.cs`.
`AppDbContext` gives each of them the EF value converter `EncryptedStringConverter`: code reads and writes the clear
value through EF, the database only ever holds the encrypted payload.

| Table.column | Purpose (never change it) | Since |
|---|---|---|
| `OtaIntegrations.ApiKey`, `ApiSecret` | `Casazen.OtaIntegration.Secrets` | FD-07 |
| `Guests.DocumentNumber`, `Guests.DocumentIssuingCountry` | `Casazen.Guest.Document` | CO-14 |
| `StayGuests.DocumentNumber`, `StayGuests.DocumentIssuePlaceName` | `Casazen.Guest.Document` | CO-14 |
| `PropertyQuesturaCredentials.Username`, `Password`, `WsKey` | `Casazen.PropertyQuesturaCredentials` | CO-14 |

`Guests` and `StayGuests` share one purpose on purpose: a payload copied from one table to the other by SQL (for example
the CO-12 backfill) stays readable.

Not encrypted, deliberately:

- **Name, e-mail, phone** of the guest: the guest list searches them (`ILIKE`) and the booking flow finds a guest by
  e-mail (TN-1 lookup). Encrypting them would break both.
- **Dates** (birth, document issue and expiry): they are `timestamptz` columns used for ages and deadlines; they do not
  identify a document on their own.
- **Document type and Alloggiati codes** (`DocumentTypeCode`, `DocumentIssuePlaceCode`): public code tables.
- **Document scans**: they are files in the private storage bucket (FD-07, `docs/runbooks/storage.md`), not columns.

**Blind index: none.** No query filters or sorts on an encrypted column (the document number is only read for a known
guest). If an exact lookup is ever needed, add a separate column with an HMAC of the normalized value, keyed by a
secret that is not in the database; never compare payloads (the same value encrypts differently every time).

## 2. How it works

- **Algorithm**: ASP.NET Core Data Protection, authenticated encryption (AES-256-CBC + HMAC-SHA256). The same value
  gives a different payload every time; a tampered payload fails to decrypt.
- **Payload**: base64url text starting with `CfDJ8` (magic header), followed by the **id of the key** that produced
  it. That id is what makes rotation possible: old payloads are decrypted with their own key.
- **Keys**: the key ring is in the `DataProtectionKeys` table (FD-07), **encrypted with the X.509 certificate of the
  environment**, which comes from Railway variables (section 3). A new data key is created automatically every 90
  days; old keys stay in the ring to decrypt old values.
- **Model cache**: EF caches the model of `AppDbContext` per Data Protection provider
  (`DataProtectionModelCacheKeyFactory`). Before, the first context of the process fixed the provider for every later
  context (the limit noted by FD-20). Production has one provider, hence one model.
- **Column type**: the encrypted columns are `text` (a payload is about 4/3 of the value plus ~90 characters). The
  plain-text limits are checked on input: document number 20 characters (Alloggiati record), place of issue 100,
  Questura username 100, password 200, WSKey 200.

## 3. Setup on Railway (test and production) — before deploying CO-14

Outside Development and Testing **the API does not start** without the key-encryption certificate
(`DataProtection__CertificatePfxBase64` is missing ...). This is intentional: without it the keys would sit in clear
next to the data they protect.

1. Create one certificate per environment and set the Railway variables, as described in
   [`storage.md` §4](storage.md#4-data-protection-keys-at-rest-encryption-certificate):
   ```
   DataProtection__CertificatePfxBase64=<base64 of the .pfx>
   DataProtection__CertificatePassword=<password of the .pfx>
   ```
2. Store the `.pfx` and its password in the password manager. **If the certificate is lost, the keys are lost, and
   with them every encrypted value** (guest document numbers, Questura credentials, OTA secrets): the guests' document
   data must be collected again and the credentials entered again.
3. Deploy. The startup applies the EF migrations and then encrypts the values still stored in clear (section 4).
4. Check the logs: `Data Protection keys: persisted in the database, encrypted with the configured certificate.` and,
   at the first start only, `Encrypted <n> Guest rows stored in clear` (and `StayGuest`).

Local development: no certificate needed (`Development`); the keys are stored in clear in the local database and the
API logs a warning.

## 4. Migration of the values stored in clear

The EF migration `EncryptGuestDocumentAndQuesturaCredentials` only widens the columns to `text`, renames
`PropertyQuesturaCredentials.PasswordEncrypted` to `Password` (the old name was never true) and gives the credentials
their tenant (`OrgId`, from the property) and their "configured on" date. SQL has no key, so it cannot encrypt.

The encryption of existing values is done by the application: `EncryptedColumns.EncryptLegacyPlaintextAsync`, called
in `Program.cs` right after `Database.Migrate()` at **every startup**.

- It finds the rows with a column neither empty nor a payload (`NOT LIKE 'CfDJ8%'`) and rewrites only those columns
  through EF. `UpdatedAt` is not touched.
- **Idempotent**: once done it finds nothing and writes nothing. Two instances starting together are harmless (a
  value is never encrypted twice: EF reads the clear value in both cases).
- Until a row is rewritten its clear value is still readable (the converter accepts a value without the `CfDJ8`
  prefix as clear text), so nothing breaks before or while the step runs.
- During a rolling deploy the old instance may still write a few values in clear: they are encrypted at the next
  startup. To verify (expected result `0` everywhere):

```sql
SELECT count(*) FROM "Guests"
 WHERE ("DocumentNumber" <> '' AND "DocumentNumber" NOT LIKE 'CfDJ8%')
    OR ("DocumentIssuingCountry" <> '' AND "DocumentIssuingCountry" NOT LIKE 'CfDJ8%');
SELECT count(*) FROM "StayGuests"
 WHERE ("DocumentNumber" <> '' AND "DocumentNumber" NOT LIKE 'CfDJ8%')
    OR ("DocumentIssuePlaceName" <> '' AND "DocumentIssuePlaceName" NOT LIKE 'CfDJ8%');
SELECT count(*) FROM "PropertyQuesturaCredentials"
 WHERE "Username" NOT LIKE 'CfDJ8%' OR "Password" NOT LIKE 'CfDJ8%' OR "WsKey" NOT LIKE 'CfDJ8%';
```

If the count is not 0 after a restart, restart once more; if it stays, look for `Encrypted ... rows` or an exception
in the startup logs.

Rollback: the `Down` of the migration turns the columns back into `varchar(50/100)`, which fails once they hold
payloads. Roll back the code only (the previous version cannot read the payloads: document numbers would show as
`CfDJ8…`), or restore a backup taken before the deploy.

## 5. Key rotation

- **Data keys**: automatic every 90 days. New writes use the new key; old payloads keep the id of their key and stay
  readable. **Never delete rows from `DataProtectionKeys`**: the values encrypted with those keys would become
  unreadable. A value moves to the current key when it is written again.
- **Certificate** (the key that protects the key ring): procedure in `storage.md` §4 "Certificate rotation" (current
  certificate to `DataProtection__PreviousCertificate*`, new one in `DataProtection__Certificate*`, redeploy).
- **Suspected leak of the database and the certificate**: rotate the certificate and create a new data key (a redeploy
  after the rotation is enough for new writes). Re-encrypting every existing value with the new key is not automated
  today: the old keys must stay in the ring until all values have been rewritten.

## 6. Questura (Alloggiati Web) credentials: write-only

API (JWT, TN-3 policies, the property must be of the caller's org — otherwise 404 — and the caller must own it or have
an org-wide role — otherwise 403):

| Method | Route | Answer |
|---|---|---|
| `GET` | `/api/properties/{id}/questura-credentials` | `{ configured, configuredAt }` |
| `PUT` | `/api/properties/{id}/questura-credentials` | body `{ username, password, wsKey }` (all three every time) → `{ configured, configuredAt }` |
| `DELETE` | `/api/properties/{id}/questura-credentials` | 204 |

- **No answer ever carries a value**, not even masked. To change them the host types all three again.
- Username and WSKey are trimmed; the password is kept exactly as typed.
- Every change is logged with the user and the property, never with a value.
- UI: property detail page, card "Credenziali Alloggiati Web (Questura)": status "Configurate il <data>" or "Non
  configurate", form with empty fields (password and WSKey masked), replace and remove with confirmation. Only users
  with `property.write` see the form.
- CasaZen does not transmit to Alloggiati Web yet (CO-13): the credentials are stored for that integration.

## 7. Document numbers in API answers

- `GET /api/guests/{id}` and `GET /api/guests/email/{email}` return `documentNumberMasked` (`*****` + last 3
  characters), never the full number.
- The full number comes only from `GET /api/guests/{id}/document-number` (explicit "Show" in the guest page): same org
  (404 otherwise), `guest.read` (403 otherwise), `Cache-Control: private, no-store`, logged with user and guest.
  The stay guests have the equivalent `GET /api/alloggiati/{bookingId}/stay-guests/document-numbers` (CO-09).

## 8. Adding an encrypted column

1. Add the entity and property to `EncryptedColumns` (existing purpose if the values are of the same family, otherwise
   a new constant; never rename a purpose).
2. Make the column `text` (remove `[MaxLength]`) and check the length on input; generate the EF migration.
3. Values already stored in clear are encrypted by the startup step with no extra code.
4. Never read or write the column with raw SQL expecting the clear value, and never filter on it in a query.
5. Update the list in `EncryptedColumnsTests` and this runbook.

## 9. Troubleshooting

| Symptom | Cause | Action |
|---|---|---|
| Startup fails: `DataProtection__CertificatePfxBase64 is missing` | Certificate variables not set on Railway | Section 3 |
| Startup fails: `The encrypted columns are not encrypted` | `AppDbContext` built without Data Protection (code change) | Fix the registration: `AddCasazenDataProtection` must run before `AddCasazenDatabase` |
| `CryptographicException: The key {id} was not found in the key ring` on guest or credentials pages | Key ring rows deleted, or database restored without its `DataProtectionKeys` | Restore `DataProtectionKeys` from the same backup; otherwise the values are lost (collect them again) |
| `CryptographicException` while unprotecting the key ring at startup | Wrong or missing certificate for the stored keys | Set the certificate used when the keys were written (current or `Previous`) |
