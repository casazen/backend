# Runbook: object storage (Supabase Storage) and Data Protection keys

Task FD-07. Audit defects: A2-03, A2-31, A9-04. Product decision D8: Supabase Storage through its S3 API, one public bucket for photos and one private bucket for documents.

## What changed

| Data | Before | Now |
|---|---|---|
| Property photos, supplier photos | Container disk (`wwwroot/uploads/properties`), relative URL `/uploads/...` | **Public** bucket, absolute URL `{Storage:PublicBaseUrl}/{key}` |
| Property documents (APE, CIN certificate, …) | Container disk under `wwwroot`, **downloadable anonymously** | **Private** bucket. The database stores the object key. Read only through `GET /api/properties/{id}/documents/{docId}/download` (JWT, tenant filter, ownership) or a 5-minute signed URL from `GET /api/properties/{id}/documents/{docId}/signed-url` |
| Guest ID scans (check-in) | Container disk (`uploads/guest-documents`) | **Private** bucket. `Guest.DocumentScanUrl` holds the object key |
| Data Protection keys (encrypt `OtaIntegration.ApiKey/ApiSecret` and, since PC-11, the iCal import URLs `PropertyICalFeeds.ImportUrl`) | Container disk, lost on every deploy | Table `DataProtectionKeys` (EF migration `AddDataProtectionKeys`), optionally encrypted with a certificate |

Object keys:

| Bucket | Key |
|---|---|
| public | `properties/{propertyId}/photos/{random}.{ext}` |
| public | `suppliers/{supplierOrgId}/photos/{random}.{ext}` |
| private | `properties/{propertyId}/documents/{random}.{ext}` |
| private | `guest-documents/{orgId}/{guestId}/{random}.{ext}` |
| private | `leases/{orgId}/{leaseId}/registration/{random}.pdf` (RLI receipts, LT-01: only through `GET /api/leases/{id}/registration/receipt`) |
| private | `leases/{orgId}/{leaseId}/signed-contract/{random}.pdf` (contract signed by every party, LT-02: uploaded by the landlord or copied from the e-sign provider; only through `GET /api/leases/{id}/signed-document`) |

Lease contract PDFs (offline signing flow, D15) use the private bucket through `IFileStorage`: the landlord uploads the signed contract (`POST /api/leases/{id}/signed-document`, PDF of at most 20 MB, `docs/runbooks/rli.md` § Contract signature). A path left by the old e-sign stub (`/signed/…`) is not a storage key and is never served.

Outside `Development` and `Testing` (so on **both** Railway environments: `ASPNETCORE_ENVIRONMENT=Production` on production, `Staging` on test) the API **does not start** without a complete S3 configuration. Startup fails with `OptionsValidationException` and lists the missing keys. The filesystem provider is refused there.

> **Deploy order:** create the buckets and set the Railway variables (sections 1-3) **before** merging FD-07 to `develop` / `main`. Otherwise the new container fails at startup and Railway keeps the previous one.

## 1. Create the buckets (Supabase dashboard)

The database uses one Supabase project for test and production (two schemas). Buckets are project-wide, so create **one pair per environment**:

| Environment | Public bucket | Private bucket |
|---|---|---|
| test | `casazen-test-public` | `casazen-test-private` |
| production | `casazen-prod-public` | `casazen-prod-private` |

Supabase → project → **Storage → New bucket**:

1. `casazen-<env>-public`: enable **Public bucket**. Optional: allowed MIME types `image/jpeg, image/png, image/webp` and a 10 MB file size limit (the API enforces the same rules).
2. `casazen-<env>-private`: leave **Public bucket off**. Optional: allowed MIME types `application/pdf, application/msword, application/vnd.openxmlformats-officedocument.wordprocessingml.document, image/jpeg, image/png`, 20 MB limit (signed lease contracts, LT-02, accept up to 20 MB; the other documents stay at 10 MB in the API).

Policies: **do not add** policies on `storage.objects` for the private buckets. The frontend never talks to Supabase Storage directly: all reads and writes go through the API with the S3 keys (server side). The public bucket needs no policy for reading, because Supabase serves public buckets at `https://<project-ref>.supabase.co/storage/v1/object/public/<bucket>/<key>`.

Check the storage quota of the Supabase plan in the dashboard (Settings → Usage) before real use.

## 2. S3 access keys

Supabase → **Project Settings → Storage → S3 Connection**:

1. Make sure the S3 protocol connection is enabled.
2. Copy the **Endpoint** (`https://<project-ref>.storage.supabase.co/storage/v1/s3`) and the **Region**.
3. **New access key** → copy *Access key ID* and *Secret access key* (the secret is shown only once). Create one key for test and one for production, so that each can be revoked on its own.

> **Risk: keys are project-wide.** A Supabase S3 key bypasses RLS and can read and write **every bucket of the project**. Because test and production share one Supabase project, the test API key can technically reach the production buckets. The bucket names in the Railway variables keep the environments apart, but they do not protect them. Using a separate Supabase project for production removes this risk (product owner decision).

The field names in the dashboard may differ slightly: this runbook was written without access to the Supabase dashboard. Follow the dashboard labels if they differ.

## 3. Railway variables

Railway → service → each environment → **Variables** (use the buckets and key of that environment):

```
Storage__Provider=S3
Storage__PublicBaseUrl=https://<project-ref>.supabase.co/storage/v1/object/public/casazen-<env>-public
Storage__S3__ServiceUrl=https://<project-ref>.storage.supabase.co/storage/v1/s3
Storage__S3__Region=<region shown by Supabase, e.g. eu-central-1>
Storage__S3__AccessKeyId=<access key id>
Storage__S3__SecretAccessKey=<secret access key>
Storage__S3__PublicBucket=casazen-<env>-public
Storage__S3__PrivateBucket=casazen-<env>-private
# optional, default 5 (1-60): lifetime of signed URLs of private documents
Storage__SignedUrlTtlMinutes=5
```

Validation rules (startup): HTTPS URLs, all keys present, two different buckets.

The frontend needs no new variable: photo URLs arrive absolute from the API. When the Vercel CSP is introduced (task FD-17), `img-src` must allow `https://<project-ref>.supabase.co`.

## 4. Data Protection keys: at-rest encryption certificate

Keys are stored in the `DataProtectionKeys` table. **Without a certificate they are stored in clear text**: whoever can read the table (a database dump, a leaked `postgres` password) can decrypt the protected secrets. Since CO-14 the certificate is **required outside Development/Testing**: the API does not start without it, because the keys now also protect the guest identity documents and the Questura credentials ([`encryption.md`](encryption.md)).

Create one certificate **per environment** on a trusted machine:

```bash
ENV=prod   # or test
openssl req -x509 -newkey rsa:3072 -sha256 -days 3650 -nodes \
  -subj "/CN=casazen-dataprotection-$ENV" -keyout dp-$ENV.key -out dp-$ENV.crt
openssl pkcs12 -export -inkey dp-$ENV.key -in dp-$ENV.crt -out dp-$ENV.pfx -passout pass:'<strong password>'
base64 -w0 dp-$ENV.pfx > dp-$ENV.pfx.b64      # macOS: base64 -i dp-$ENV.pfx
```

Railway variables:

```
DataProtection__CertificatePfxBase64=<content of dp-$ENV.pfx.b64>
DataProtection__CertificatePassword=<strong password>
```

Store the `.pfx` file and its password in the password manager, then delete the local files. **If the certificate is lost, every key it protects is lost too**, and the OTA secrets and the iCal import links must be entered again (docs/runbooks/ical.md, "Key ring lost").

Certificate rotation:

1. Create the new certificate.
2. Move the current values to `DataProtection__PreviousCertificatePfxBase64` / `DataProtection__PreviousCertificatePassword`.
3. Put the new certificate in `DataProtection__CertificatePfxBase64` / `DataProtection__CertificatePassword`, then redeploy. New keys are encrypted with the new certificate, and old keys stay readable with the previous one.
4. Remove the previous certificate only after all keys written with it have expired (Data Protection keys last 90 days) and have been replaced.

Adding the certificate later is safe: keys already stored in clear text stay readable, and new keys are encrypted.

Note: OTA secrets encrypted **before** FD-07 used keys that were lost with the old containers, so they cannot be recovered. Those integrations must be configured again (the OTA partner APIs are hidden behind a feature flag anyway, decision D10).

### Encrypted database columns

Moved to [`encryption.md`](encryption.md): the list of the encrypted columns (OTA secrets, iCal import URLs, guest
identity documents, Questura credentials), the one mechanism (`EncryptedStringConverter`,
`DataProtectionModelCacheKeyFactory`, `EncryptedColumns`), the startup encryption of values stored in clear, key
rotation and how to add a column.

## 5. Migrating files from the old local storage (optional)

On Railway there has never been a volume, so the old files were most likely lost at the first redeploy. The command is still useful for any environment that kept the files, such as a volume, a local machine or a backup copy.

```bash
# inside the container (railway ssh), or locally with the target environment's variables
dotnet Casazen.Web.dll storage:migrate-legacy --dry-run   # report only, changes nothing
dotnet Casazen.Web.dll storage:migrate-legacy
# locally from the repo:
dotnet run --project Casazen.Web -- storage:migrate-legacy --dry-run
```

- It needs `ConnectionStrings__DefaultConnection` and the `Storage__*` variables of the target environment. It applies pending EF migrations first, like a normal startup, then exits without starting the web server.
- It reads the old files from `ImageStorage:LocalPath` (default `wwwroot/uploads/properties`) and `GuestDocumentStorage:LocalPath` (default `uploads/guest-documents`), relative to the working directory. Point them to the volume or backup folder when needed, for example `ImageStorage__LocalPath=/data/uploads/properties`.
- It uploads each file to the right bucket and rewrites `Property.PhotoUrls`, `SupplierProfile.PhotoUrlsJson`, `PropertyDocument.StorageUrl` and `Guest.DocumentScanUrl`.
- It is **idempotent**: rewritten references are skipped on the next run, and an object that is already in the bucket is not uploaded again (it only rewrites the reference, for example after a run interrupted before saving).
- Output: `uploaded=… alreadyPresent=… missing=… referencesRewritten=…`, exit code 0, or 1 on error. `missing` counts references whose file is neither on disk nor in the bucket. Those references are left unchanged:
  - photos are hidden by the frontend (a relative URL is never displayed);
  - documents answer `404 document_file_missing` and must be uploaded again by the host.

## 6. Verification after deploy

1. Railway logs: no `OptionsValidationException`. With a certificate, the log contains `Data Protection keys: persisted in the database, encrypted with the configured certificate.`
2. Database: `select count(*) from "DataProtectionKeys";` returns ≥ 1 (a key is created at the first startup). With a certificate, the `Xml` column contains `encryptedSecret`.
3. Photo: upload a photo in the console. The returned URL starts with `Storage__PublicBaseUrl` and opens in a private browser window.
4. Document: upload a PDF to a property, then download it from the console. Then check that it is not public:
   - `curl -I https://<project-ref>.supabase.co/storage/v1/object/public/casazen-<env>-private/<key>` must **not** return 200;
   - `curl -I https://<api>/uploads/properties/<id>/documents/<file>` must return 404;
   - the download endpoint without a token returns 401;
   - with a token of another org's user, it returns 404.
5. Signed URL: `curl -H "Authorization: Bearer <token>" https://<api>/api/properties/<id>/documents/<docId>/signed-url` returns `{ url, expiresAt }`. The URL works, then stops working after `Storage__SignedUrlTtlMinutes`.
6. Redeploy (or restart the service), then repeat steps 3-5: the files are still there. If an OTA integration was configured again, its secret is still readable.

## Local development

In `Development` the default is the filesystem provider. Files go to `Casazen.Web/App_Data/storage/{public,private}` (git-ignored), the public folder is served at `{App:ApiBaseUrl}/storage/public/…`, and a warning is logged at startup. Set `App:ApiBaseUrl` to the local API URL, as in `appsettings.Development.example.json`. The signed-url endpoint answers `501 signed_url_unavailable` with this provider, while the authenticated download works. To try S3 locally, set `Storage__Provider=S3` and the `Storage__S3__*` variables (plain `http` endpoints are accepted only in Development/Testing).
