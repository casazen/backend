# Runbook: property address uniqueness, unit and coordinates

Task PC-06 (audit defects A2-19, A2-33). The code applies it by itself: this page explains what changes for the data
already in the database, how to check it before and after the deploy, and how to resolve the duplicates that the
migration reports. EF migration: `AddPropertyUnitAndAddressIndex`.

## The rule

| Topic | Before | Now |
|---|---|---|
| Scope of the unique address | **Whole platform**: `(Address, City, PostalCode, IsActive)`. A second host at the same building got `409` and so learned that the address was in use by someone else | **Per org**: `UIX_Properties_OrgId_AddressKey` on `(OrgId, AddressKey)` |
| Several apartments in one building | Impossible (`409` on the second) | Possible: they differ by the new optional `Properties.Unit` ("interno / scala", at most 30 characters, e.g. `int. 5`, `Scala B int. 5`) |
| What "the same address" means | The exact text | Street, city, postal code and unit with **case and runs of spaces ignored**: `Via Roma  1` / `via roma 1` / ` VIA ROMA 1 ` are one address |
| Which properties count | Active ones | Active **and not deleted** (`IsActive = true AND IsDeleted = false`). A deleted property (PC-05) frees its address; a paused one (PC-03) keeps it |
| Concurrency | Unique index, but any violation of any unique index answered as "address in use" | The unique index is the only guarantee: the loser of two parallel creates or updates gets PostgreSQL `23505` on `UIX_Properties_OrgId_AddressKey` and the API answers `409 duplicate_property_address`. No check is done before the insert. A `23505` on the slug index answers `409 duplicate_property_slug` |
| Coordinates | `numeric(18,2)`: 2 decimals, up to about 1 km of error on the public map | `numeric(9,6)`: 6 decimals (about 0.1 m). Latitude -90..90 and longitude -180..180 are validated at the API (`400 validation_error`, fields `Latitude`/`Longitude`) and again in `PropertyService` (`422 property_coordinates_invalid`); more than 6 decimals are rounded |

`AddressKey` is a **stored generated column** computed by PostgreSQL:

```sql
lower(regexp_replace(btrim("Address"), '\s+', ' ', 'g')) || '|' ||
lower(regexp_replace(btrim("City"),    '\s+', ' ', 'g')) || '|' ||
lower(btrim("PostalCode")) || '|' ||
lower(regexp_replace(btrim(coalesce("Unit", '')), '\s+', ' ', 'g'))
```

The application never writes it and never builds it, so every writer of the table (API, jobs, SQL, imports) is covered.
It is a shadow property of `Property` (not in the entity). Accents are not folded: `Via Pietà` and `Via Pieta` are
different addresses.

API: `unit` is accepted by `POST /api/properties` and `PUT /api/properties/{id}` (PATCH semantics: left out keeps it,
`null` or blank clears it) and returned by `GET /api/properties/{id}` and `/detail`. It is **not** exposed on the public
site, the search results or the public API.

## Migration `AddPropertyUnitAndAddressIndex`

Runs with the other migrations when the backend starts on Railway; no manual step is needed, but **read the checks
below first**: the migration changes data in two cases and never deletes a row.

1. **Coordinates that cannot exist.** `numeric(9,6)` cannot hold `4190.28`, so before narrowing the columns every
   property with `abs(Latitude) > 90` or `abs(Longitude) > 180` gets `Latitude = 0` and `Longitude = 0` ("not set").
   The original values are not kept anywhere: save them first (check 1). Valid values keep their number
   (`41.90` becomes `41.900000`): the old 2-decimal values are as imprecise as before until the host types the exact
   point.
2. **Duplicate addresses inside an org.** The old index was global and exact, so two active properties of the **same
   org** whose addresses differ only by case or spaces were allowed and would now collide. For every group of active,
   not deleted properties of an org with the same `AddressKey`, the **oldest** (`CreatedAt`, then `Id`) is left as it is
   and each later one gets `Unit = 'dup-' + the first 8 hexadecimal characters of its Id` (e.g. `dup-3fa2b1c4`). So the
   unique index can always be created, **no property is deleted or hidden**, and the marker is visible to the host in
   the "Interno / scala" field. Rows of different orgs never conflict. Inactive and deleted rows are not touched.

`Down` drops the index, the generated column and the unit, widens the coordinates back and recreates the old **global**
unique index; it does not undo the two data changes above, and it fails once two orgs have an active property at the
same address (the situation the rule change allows). Do not roll back after the new rule has been in use.

### Check before the deploy (production, then test)

Run on the database of the environment (Supabase SQL editor, schema `casazen_prod` / `casazen_test`).

1. Coordinates that the migration will reset (save the result):

   ```sql
   SELECT "Id", "OrgId", "Name", "Address", "City", "Latitude", "Longitude"
   FROM "Properties"
   WHERE abs("Latitude") > 90 OR abs("Longitude") > 180;
   ```

   Usually empty. For a row that is a typing mistake (`4190.28` for `41.9028`) fix it with `UPDATE` before the deploy so
   it keeps its position.

2. Active properties of the same org that will collide (the migration will mark every one but the oldest):

   ```sql
   SELECT "OrgId",
          lower(regexp_replace(btrim("Address"), '\s+', ' ', 'g')) || '|' ||
          lower(regexp_replace(btrim("City"), '\s+', ' ', 'g')) || '|' ||
          lower(btrim("PostalCode")) AS "AddressKey",
          count(*) AS "Properties",
          array_agg("Id" ORDER BY "CreatedAt", "Id") AS "Ids"
   FROM "Properties"
   WHERE "IsActive" = true AND "IsDeleted" = false
   GROUP BY 1, 2
   HAVING count(*) > 1;
   ```

   Usually empty. If rows come back, decide per group (below) now or after the deploy; the deploy works either way.

### Check after the deploy

```sql
-- 1. the new index exists and the old global one is gone
SELECT indexname FROM pg_indexes
WHERE tablename = 'Properties' AND indexname IN ('UIX_Properties_OrgId_AddressKey', 'IX_Properties_Address_City_PostalCode_IsActive');
-- expected: only UIX_Properties_OrgId_AddressKey

-- 2. coordinate columns
SELECT column_name, numeric_precision, numeric_scale FROM information_schema.columns
WHERE table_name = 'Properties' AND column_name IN ('Latitude', 'Longitude');
-- expected: 9 / 6 for both

-- 3. properties the migration marked as duplicates
SELECT "Id", "OrgId", "Name", "Address", "Unit", "City", "PostalCode", "CreatedAt"
FROM "Properties"
WHERE "Unit" LIKE 'dup-%'
ORDER BY "OrgId", "Address", "CreatedAt";
```

## Resolving the duplicates (never delete automatically)

Each row listed by check 3 is a property of the same org that the old code let through at an address (up to case and
spaces) already used by an older property. Decide with the host, per row:

| Situation | What to do |
|---|---|
| It is **another apartment** in the same building | The host (or support) writes the real unit in the property's "Interno / scala" field (web: edit property). The `dup-…` marker is replaced. If another property of the org already has that unit, the API answers `409 duplicate_property_address` |
| It is **the same apartment** entered twice | Keep the one that has the bookings, leases, CIN and fiscal data. Archive the other with the delete action of the property (**soft delete**, PC-05: it is refused while stays or leases are still to come; fiscal history is kept). The address is then free. Never run `DELETE` on `Properties`: bookings, payments, Alloggiati reports and fiscal years reference it |
| It is **a test or abandoned property** with no data | Archive it as above |

When two duplicates carry data (bookings on both), do not merge them by SQL: ask the product owner.

After resolving, `SELECT count(*) FROM "Properties" WHERE "Unit" LIKE 'dup-%'` should be 0. A host who types a unit
that starts with `dup-` is not a problem for the index; the prefix is only a convention for finding the migrated rows.

## What hosts and the support see

- Creating or renaming a property to an address that the org already has (same unit): `409 duplicate_property_address`.
  The web form shows "Hai già un immobile con questo indirizzo: se è un altro appartamento dello stesso edificio, indica
  l'interno o la scala." under the "Interno / scala" field.
- An address already used by **another org** is no longer an error and is never revealed.
- Latitude outside -90..90 or longitude outside -180..180: the form shows the range under the field; the API answers `400`.
- The lease contract and the supplier request still print the street address without the unit (not part of PC-06).

## Tests

- `PropertyAddressPostgresTests`: several apartments of one building, 409 and stable code, case/space variants, another
  org, six parallel creates for one address (exactly one `201`), parallel updates to the same address (one wins), deleted
  and paused properties, unit normalization, coordinate range and six decimals.
- `AddPropertyUnitAndAddressIndexMigrationPostgresTests`: the migration on rows as the old code stored them (duplicates
  by case and spaces, other org, inactive copy, impossible coordinates), no row deleted, the index afterwards.
- `PropertyServiceTests`, `UpdatePropertyRequestTests`, `PropertyAddressTests`: `23505` mapping by constraint name,
  normalization, validation keys.
