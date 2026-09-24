# Runbook: service categories (one taxonomy) and data migration

Task SU-03 (audit defects A4-05, A6-03). Before this change the supplier wizard saved Italian labels
(`Pulizie`, `Manutenzione`, ...), the host web filtered by `cleaning`/`maintenance`/`plumbing`/`laundry`, the app by
`cleaning`/`maintenance`/`linen`/`check-in`, and the backend compared exact strings: hosts never found real
suppliers. The code applies the fix by itself; this page explains the rule, what the migration does to existing
data and how to check it after the deploy.

## The rule

| Where | Behaviour | Code |
|---|---|---|
| Source of truth | Ten stable lowercase English codes, in display order: `cleaning`, `maintenance`, `plumbing`, `laundry`, `linen`, `check-in`, `gardening`, `events`, `rental`, `excursions` | `Casazen.Core/Suppliers/ServiceCategories.cs` |
| Catalog API | `GET /api/service-categories` (any signed-in user) → `{ "items": [{ "code": "cleaning" }, ...] }` | `ServiceCategoriesController` |
| Writes | Supplier profile (`PUT /api/supplier/profile`), admin invite (`POST /api/admin/suppliers/invite`), service request (`POST /api/service-requests`, checkout wizard) accept only codes. Input is trimmed and lower-cased first (`" Cleaning "` → `cleaning`), duplicates removed. Anything else → **422** `invalid_service_category` (message `ServiceCategoryInvalid`, IT/EN), nothing saved | `ServiceCategories.Require/RequireAll` |
| Search | `GET /api/suppliers?category=<code>` and `POST /api/service-requests/match-supplier` filter by code; a supplier matches only if it declared the code (no categories = no match). An unknown code → 422 instead of an empty list | `SupplierService.GetActiveByComune`, `SupplierMatchService` |
| Labels | Clients translate the code: web `serviceRequest.categories.<code>` (`it.json`/`en.json`), app `src/i18n/locales/{it,en}.ts`, emails `ServiceCategory_<code>` (`EmailTexts*.resx`) | frontend, mobile, `EmailTemplates.ServiceCategoryLabel` |

The list is the union of what the three clients already offered; no category was invented. Doubtful synonyms were
kept distinct: `laundry` (web) and `linen` (app) are two categories, `check-in` (app) is its own category.
Never rename a code: it is stored in the database and used as an i18n key. A new category needs the constant in
`ServiceCategories`, its labels in web, app and `EmailTexts`, and the updated lists in the tests that pin them
(`ServiceCategoriesTests`, `i18n.test.ts` on web and app).

## Data migration `NormalizeServiceCategories`

EF migration `20260924022518_NormalizeServiceCategories`, data only (no schema change). It runs with the other
migrations when the backend starts on Railway; no manual step is needed.

- Converted columns: `SupplierProfiles.CategoriesJson`, `SupplierInviteRecords.CategoriesJson` (JSON arrays: each
  string item mapped, duplicates removed, first occurrence order kept) and `ServiceRequests.Category`.
  `UpdatedAt` is not touched.
- Map (compared trimmed and lower case): the codes themselves, the six labels of the old supplier wizard
  (`Pulizie`→`cleaning`, `Manutenzione`→`maintenance`, `Giardinaggio`→`gardening`, `Eventi`→`events`,
  `Noleggio`→`rental`, `Escursioni`→`excursions`) and the Italian labels the web showed for host codes
  (`Idraulico`→`plumbing`, `Lavanderia`→`laundry`).
- **Nothing is deleted.** A value that cannot be mapped stays as it is and is logged with
  `RAISE WARNING 'NormalizeServiceCategories: unmapped category kept: table=…, id=…, value=…'` (WARNING reaches the
  PostgreSQL server log: Supabase → Logs → Postgres). A final line gives the counts.
- Idempotent (`NormalizeServiceCategoriesPostgresTests` runs it twice). `Down` is a no-op.

### Check after the deploy (test, then production)

1. Report of the values that are still not codes (platform admin token):

   ```
   GET /api/admin/suppliers/unmapped-categories
   → { "items": [{ "source": "supplier_profile" | "supplier_invite" | "service_request", "id": "…", "value": "…" }], "total": N }
   ```

   The same with SQL:

   ```sql
   SELECT 'SupplierProfiles' AS tbl, p."OrgId"::text AS id, v AS value
   FROM "SupplierProfiles" p, jsonb_array_elements_text(p."CategoriesJson") v
   WHERE v NOT IN ('cleaning','maintenance','plumbing','laundry','linen','check-in','gardening','events','rental','excursions')
   UNION ALL
   SELECT 'SupplierInviteRecords', i."Id"::text, v
   FROM "SupplierInviteRecords" i, jsonb_array_elements_text(i."CategoriesJson") v
   WHERE i."CategoriesJson" IS NOT NULL
     AND v NOT IN ('cleaning','maintenance','plumbing','laundry','linen','check-in','gardening','events','rental','excursions')
   UNION ALL
   SELECT 'ServiceRequests', s."Id"::text, s."Category"
   FROM "ServiceRequests" s
   WHERE s."Category" NOT IN ('cleaning','maintenance','plumbing','laundry','linen','check-in','gardening','events','rental','excursions');
   ```

2. For each unmapped value decide with the product owner which code it means, then fix it by hand (for example
   `UPDATE "ServiceRequests" SET "Category" = 'cleaning' WHERE "Id" = '…';`). A supplier can also fix its own
   profile: the web profile page lists the values that are no longer valid and removes them at the next save.
   Until then an unmapped value is harmless: it matches no search and emails show it as it is.

3. Smoke test: as a host, `GET /api/suppliers?propertyId=<id>&category=cleaning` returns the suppliers that had
   chosen "Pulizie" in the wizard.

## Clients

- Web: `useServiceCategories()` (`GET /api/service-categories`, cached for the session), `ServiceCategoryPicker`
  (supplier activation and profile, admin invite) and `ServiceCategorySelect` (host request form, marketplace
  filter), with loading and error states. No hardcoded list.
- App: the "Richiedi fornitore" screen reads the same endpoint; labels follow the device language (Italian by
  default). Installed builds that still send `cleaning`, `maintenance`, `linen` or `check-in` keep working: they are
  codes.
