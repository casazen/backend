# Runbook: global search and last area (UI-13a)

Task UI-13a of the redesign wave (F13 of the design-system guide): the server side of the command palette (Ctrl/Cmd+K) and the
last area used, saved on the server. **Backend only**: the palette of the frontend (UI-06, version 1: menus, actions and lists
already in the cache) is connected to this endpoint by a later task. Everything below is behind the flag `GlobalSearch`, off by
default, except `PUT /api/me/last-context`.

## 1. What exists

| Piece | Where | In one line |
|---|---|---|
| `GET /api/search?q=&limit=` | `SearchController`, `GlobalSearchService` (`Casazen.Infrastructure/Search`) | Groups of results by kind, only what the caller may read in its context, bound in SQL to its org and to the properties it reaches. |
| `PUT /api/me/last-context` | `MeController.PutLastContext`, `UserService.SetLastUsedContextAsync` | Stores the area the caller entered last (`User.LastUsedContextKey`); `GET /api/me/contexts` gives it back as `lastUsedContextKey`. |
| Flag `GlobalSearch` | `Casazen.Core/Features/FeatureFlags.cs`, `appsettings.json` | `Features:GlobalSearch` / `Features__GlobalSearch`, **off**. Off: `GET /api/search` answers 404 `not_found` like a route that does not exist. |
| Rate limit | policy `GlobalSearch` (`RateLimitPolicies`) | 60 requests per minute **per user** (not per IP). |
| Search keys | stored generated column `SearchKey` on five tables, a GIN index each | The words of the main texts of a row, folded (no case, no accents); see section 5. |
| Migration | `AddGlobalSearchKeys` | Adds the five columns and six indexes. Applied with the other migrations whether the flag is on or off. |

The migration and the columns are there with the flag off: shipping the backend does not change what anybody sees, and turning the
flag on later needs no other deploy.

## 2. Turning it on

1. Deploy the backend with the migration (section 6 says what it locks and for how long).
2. Set `Features__GlobalSearch=true` on the **test** environment first. The frontend reads the flag from `GET /api/public/features`
   (key `globalSearch`); the palette will call the endpoint only when it is on (the task that connects it).
3. Try the searches of section 9 with a real account, then with a collaborator limited to some properties (it must not find the others).
4. Production: the same variable. Turning it off again is immediate (Railway redeploys with the new value) and loses nothing.

`PUT /api/me/last-context` is **not** behind the flag: it writes one column of the caller's own row and nothing reads it but
`GET /api/me/contexts`.

## 3. The API

### 3.1 `GET /api/search?q=&limit=`

Signed-in user (JWT), no role of its own: a group is in the answer only when the caller holds the permission of that group in its
context (section 4). `q` is the term, `limit` the most results **per group**.

| Parameter | Rule |
|---|---|
| `q` | After trimming: at least `SearchText.MinQueryLength` (2) and at most `SearchText.MaxQueryLength` (64) characters. Case and accents do not matter. At most `SearchText.MaxTokens` (5) words count. A term that has no word of two characters or more (`a b`, `- -`) is a valid term with nothing to search: `200` with no group. |
| `limit` | Default `SearchLimits.DefaultLimit` (5), clamped to 1..`SearchLimits.MaxLimit` (20). |

Answer `200`, header `Cache-Control: private, no-store` (the answer holds names of people):

```json
{
  "groups": [
    {
      "type": "guest",
      "hasMore": false,
      "items": [
        { "type": "guest", "id": "…", "title": "José Müller", "subtitle": "j***@example.com", "destination": "short-rent.guest" }
      ]
    }
  ]
}
```

* Only the groups with at least one result are there, in this order: `property`, `booking`, `guest`, `lease`, `service-request`,
  `supplier`, `supplier-request` (`SearchTypes.All`). `hasMore` is true when there are more than `limit` results in the group.
* A result carries `type`, `id`, a short `title`, a `subtitle` that is not sensitive (or null) and a `destination` **key**, never a URL:
  `{area}.{kind}`, the area being the context of the caller that the object belongs to. The client owns the routes and maps each key to
  its route with the `id`: `short-rent.property`, `long-rent.property`, `short-rent.booking`, `short-rent.guest`, `long-rent.lease`,
  `short-rent.service-request`, `long-rent.service-request`, `short-rent.supplier`, `supplier.request` (`SearchDestinations`).
* Titles and subtitles by kind: property (name, city); booking (guest name, "code · property"; only the code when the guest was erased);
  guest (name, masked e-mail); lease (tenants, at most two and "+n", then the property); service request (the name of the service, or the
  category in the language of the caller, then the property); supplier (business name, up to two categories); request in the inbox of a
  supplier (service, comune).

### 3.2 Errors

All in the shape of the other endpoints (ProblemDetails with a stable `code`; the message is in `SharedResources.resx` / `.en.resx`).

| Status | `code` | When |
|---|---|---|
| 400 | `search_query_too_short` | Fewer than 2 characters after trimming (or no `q`). Nothing is read. |
| 400 | `search_query_too_long` | More than 64 characters after trimming. |
| 404 | `not_found` | The flag is off (before authentication). |
| 401 | | No valid token. |
| 429 | `rate_limited` | More than the limit of section 7; `Retry-After` is set. |

### 3.3 `PUT /api/me/last-context`

Body `{ "contextKey": "long-rent" }`: one of the `contextKey` that `GET /api/me/contexts` lists for the caller right now
(`short-rent`, `long-rent`, `supplier`, `admin`, `account`). The match ignores case and spaces at the ends; the key is stored as the
list spells it. Answer `200 { "lastUsedContextKey": "long-rent" }`. Saving the value that is already stored writes nothing.

| Status | `code` | When |
|---|---|---|
| 422 | `context_not_accessible` | The key does not exist, or the caller cannot enter that context (an access it does not have; `account` while `OrgTeam` is off). Nothing is stored. |
| 400 | `validation_error` | No key, or longer than 64 characters. |
| 403 | `account_inactive` | The account is deactivated. |

`GET /api/me/contexts` now gives `lastUsedContextKey` **only while the caller can still enter that context**; a stale key (an access
taken away) comes back as `null`, so the client never opens an area that refuses it.

## 4. Who finds what

The web layer (`SearchAccessResolver`) asks the authorization service the **same policies** as the endpoints that list the same
objects, and the scope resolver (`IHostScopeResolver`, from the database, as everywhere since AM-03) for the org and the properties the
caller reaches. The search service reads only what that says; it has no role checks of its own. A caller with nothing to search gets
`200` with no group.

| Group | Looked up by | The caller needs | Bounded by |
|---|---|---|---|
| `property` | name, city, CIN | `property.read` of the area of the property: `PropertyRead` (short-rent context) for short-rent properties, `LongRentPropertyRead` (long-rent context) for long-term ones | the org and the scope; active, not deleted |
| `booking` | the code (its beginning, 4 characters at least) or the name of the guest | `BookingRead` | the org and the scope (the property of the booking) |
| `guest` | name, e-mail address | `GuestRead` | the org; a caller limited to some properties finds the guests that have a stay at those properties, not the others |
| `lease` | the name of a tenant | `LeaseRead` (long-rent context) | the org and the scope |
| `service-request` | name of the service, category | `property.read` of the area of the request | the org and the scope |
| `supplier` | business name | `PropertyRead` (the marketplace of the host is a short-rent matter) | the suppliers, active, that got a request from the org within the scope (the list of the preferred suppliers does not exist yet) |
| `supplier-request` | service, category, the code the customer quotes, comune | the `Supplier` policy | the inbox of its own supplier org; no data of the host |

What the scope does: a collaborator limited to some properties ("Solo alcuni") finds those properties and what belongs to them (stays,
the guests of those stays, leases, requests), never the others, and nothing that tells the others exist: the filter is in the SQL of each
group (`InScope`), before the limit, so a result, `hasMore` and the order are computed on what the caller may see. An account in no team
keeps the old rule (the properties it created).

What the answer never carries (privacy):

* a guest: name, and the e-mail address **masked** (`PersonalDataMasking.MaskEmail`, `j***@example.com`); never the phone, the document,
  the date of birth;
* a lease: the first and last name of the tenants (what the lease screen shows), never the fiscal code, the e-mail or the citizenship;
  a landlord or another party is not searched;
* a guest who asked to be erased or was anonymized, a guest deleted by the host, an anonymized tenant, a deleted or switched-off
  property, a suspended supplier: not found. A booking of an erased guest is found by its code and shows only the code;
* the term: it is a name more often than not, so it is **never logged** (the only log line is a Debug line with the number of groups)
  and the answer is never cached (`Cache-Control: private, no-store`).

## 5. How it works, and why it is not `pg_trgm`

**The strategy.** A search for "the words begin with the words typed" over names, cities and codes, served by indexes, on the
built-in full-text search of PostgreSQL. Nothing is filtered in memory and no query reads a whole table.

* **Search key.** Each of five tables has a **stored generated column** `SearchKey` that the database computes from the main texts of the
  row, folded: lower case, no accents, every other character a space, so the key is words of `a-z0-9` separated by one space.
  `Properties`: name, city, CIN. `Guests`: surname, first name, e-mail. `Parties` (the signers of a lease; only tenants are searched): surname, first name.
  `ServiceRequests`: name of the service, category, public code, comune of the work. `SupplierProfiles`: business name. Example: the
  guest "José Müller-Weiß", `Jose.Muller@Example.COM` has the key `muller weiss jose jose muller example com`.
* **Folding.** `translate()` with the table of `SearchFolding` (the letters of Latin-1 Supplement and Latin Extended-A, the Romanian letters with a comma below and the Kelvin, Angstrom and dotted-I signs, one plain letter each; six characters become two letters: ß, ẞ, Æ, æ, Œ, œ),
  `lower()`, `regexp_replace('[^a-z0-9]+', ' ')`, `btrim()`. `SearchText.Fold` does the same steps in C# for the term typed, from the
  same table; `SearchFoldingTests` runs the SQL steps in C# over the whole basic plane and `GlobalSearchPostgresTests` compares the
  real database with the folding for every row.
* **Index and query.** A GIN index over `to_tsvector('simple', coalesce("SearchKey", ''))` (configuration `simple`: no stemming and no
  stop words, so a name is a name in any language). The term "ros mar" becomes the query `ros:* & mar:*`: **every word of the term is
  the beginning of a word of the key**, in any order. The query repeats the expression of the index, so the planner uses it.
* **Booking code.** Stored upper case, 10 characters, no separator (shown as `7K3M9-PQ2XV`). The term is also looked up as the
  beginning of a code (`LIKE 'ABC%'`, a range of `IX_Bookings_BookingCode_Prefix`, `varchar_pattern_ops`) when it looks like one:
  4 characters or more of the alphabet of the codes (no I, L, O, U; O is read as 0, I and L as 1), with a digit in it, or the whole code.
  A word like `zxcv` is a name, not the beginning of a code.

**Why a stored key and not `ILIKE '%…%'`, `unaccent` or `pg_trgm`.**

* `ILIKE '%x%'` cannot use a btree index: on the big tables it reads them all.
* `pg_trgm` and `unaccent` are extensions, and nothing makes them safe to depend on here. An extension lives in **one schema of the
  database**, and the environments of CasaZen share **one Supabase database, one schema each** ([`INFRA.md`](../INFRA.md)); the
  connection of each environment has `SearchPath=<its schema>` only, so an operator class created in another schema (`extensions`,
  `public`) is not found by the migration of the other environment unless every connection string and every query is changed.
  No runbook enables or checks an extension, no migration of the project creates one, and the startup runs `Database.Migrate()`: a
  migration that fails for a missing extension or privilege would stop the deploy. So the search does not depend on any, and says so.
  If one day each environment has its own database, `pg_trgm` can bring the "contains" search (follow-up).
* A generated column is computed by the database for **every** writer (the application, a SQL backfill, an anonymization): the key can
  never be stale. The database refuses to write it by hand (`428C9`, tested), so no code can leave it wrong.
* The folding table is in the code, not in the database (no dictionary to install and to keep in step with the code).

## 6. Indexes and migration `AddGlobalSearchKeys`

| Index | Table | Definition |
|---|---|---|
| `IX_Properties_SearchKey_Fts` | `Properties` | GIN, `to_tsvector('simple', coalesce("SearchKey", ''))` |
| `IX_Guests_SearchKey_Fts` | `Guests` | the same |
| `IX_Parties_SearchKey_Fts` | `Parties` | the same |
| `IX_ServiceRequests_SearchKey_Fts` | `ServiceRequests` | the same |
| `IX_SupplierProfiles_SearchKey_Fts` | `SupplierProfiles` | the same |
| `IX_Bookings_BookingCode_Prefix` | `Bookings` | btree on `"BookingCode" varchar_pattern_ops` (the beginning of a code, in any collation) |

The migration is one, generated with `dotnet ef` (no extension, no data written by hand): five `ALTER TABLE … ADD "SearchKey" text
GENERATED ALWAYS AS (…) STORED` and the six `CREATE INDEX`. `AddGlobalSearchKeysMigrationSqlTests` checks the script, and
`AddGlobalSearchKeysMigrationPostgresTests` applies the migration to a database with rows at the schema right before it, checks the
key of each row and the definition of the indexes, and reverts it.

**What it costs when it is applied** (the startup of the first deploy that contains it runs `Database.Migrate()`, one transaction):

* adding a stored generated column **rewrites the table** and takes an exclusive lock on it until the commit; `CREATE INDEX` is not
  `CONCURRENTLY` (it cannot be inside the transaction of a migration) and blocks the writes of its table while it is built. The time is
  proportional to the size of the five tables and the indexes; with the tables of today (thousands of rows) it is seconds, but the
  application cannot write those tables while it runs. Look at the sizes before the deploy:

  ```sql
  SELECT 'Properties' AS "table", count(*) FROM "Properties" UNION ALL SELECT 'Guests', count(*) FROM "Guests"
  UNION ALL SELECT 'Parties', count(*) FROM "Parties" UNION ALL SELECT 'ServiceRequests', count(*) FROM "ServiceRequests"
  UNION ALL SELECT 'SupplierProfiles', count(*) FROM "SupplierProfiles";
  ```

* for a database with millions of rows this is not the way: add the column and build the indexes `CONCURRENTLY` by hand in a window
  (follow-up UI-13a-FU4); the project is far from it.
* it is listed in [`deploy-checklist.md`](deploy-checklist.md) § 7 (migrations that rewrite data).

**When a column of a key changes.** `Name`, `City`, `CinCode`, `LastName`, `FirstName`, `Email`, `ServiceNameSnapshot`, `Category`,
`PublicCode`, `LocationCity` and `LegalName` are read by the generated columns (`SearchKeyModel.Keys` lists which table uses which).
PostgreSQL refuses to change the type of such a column, or to drop it, while a generated column depends on it: a later migration that
does so drops the key and its index first and creates them again (the model snapshot and `has-pending-model-changes` show it). The
same for a change of the folding table: it changes the expression of the five columns, so it is a migration, never an edit in place.

**Checking a database**

```sql
-- the indexes exist and are valid
SELECT c.relname, i.indisvalid FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
WHERE c.relname LIKE 'IX%SearchKey%Fts' OR c.relname = 'IX_Bookings_BookingCode_Prefix';

-- the key of a row, and that the planner uses the index (with the expression of the queries)
SELECT "SearchKey" FROM "Guests" WHERE "Id" = '00000000-0000-0000-0000-000000000000';
EXPLAIN SELECT 1 FROM "Guests"
WHERE "OrgId" = '00000000-0000-0000-0000-000000000000'
  AND to_tsvector('simple', coalesce("SearchKey", '')) @@ to_tsquery('simple', 'ros:* & mar:*');
```

A small table is read with a sequential scan whatever the indexes: that is the planner's choice, not a defect.
`GlobalSearchPostgresTests` proves the index is the one used (with the sequential scan off) and that a host with 20,000 guests is
searched through it.

## 7. Rate limit

Policy `GlobalSearch` (`RateLimitPolicies.GlobalSearch`): a fixed window of **60 requests per minute per user**, no queue. Like
`OrgAccessRequest` (AM-02b) it is the policy of a signed-in endpoint, so it is partitioned by person (a hash of the subject of the
token) and not by IP: the palette asks at every pause in typing and the people of one office share an address. A call runs up to
seven short queries, hence the limit of its own.

| Variable | Default | |
|---|---|---|
| `RateLimiting__GlobalSearch__PermitLimit` | 60 | requests per user per window; below 1: **startup fails** |
| `RateLimiting__GlobalSearch__WindowSeconds` | 60 | length of the window in seconds; below 1: startup fails |

Over the limit: `429` with `code` `rate_limited` and `Retry-After`. The counters live in the memory of each replica, like the other
policies ([`proxy-ip.md`](proxy-ip.md)). The client should debounce the typing (the palette waits for a pause) and not retry a 429
before `Retry-After`.

## 8. What the frontend does with it

* `GET /api/me/contexts` gives `lastUsedContextKey`: open that area after the login, if it is not null (it is null when nothing was
  saved or the caller can no longer enter it). At every change of area, `PUT /api/me/last-context` with the `contextKey` entered; a
  `422` means the list changed (reload it).
* The palette (UI-06 and the task that connects it): `GET /api/search?q=` after a pause in typing, only with `globalSearch` on, `q` of at
  least two characters; one heading per group (`type` is stable, the label is the client's), the `destination` key mapped to its route
  with the `id`. The client must add the codes `search_query_too_short`, `search_query_too_long` and `context_not_accessible` to its
  table of API errors.

## 9. Checking and troubleshooting

* **"A person I know is not found."** In this order: the flag is on; the caller holds the permission of the group (a collaborator
  without `guest.read` never gets the `guest` group); the scope (a collaborator limited to some properties finds only the guests of
  their stays, a guest with no stay is found by the whole org only); the guest was erased or anonymized, or deleted by the host (never
  found); the word is typed in the middle of a word (only the beginning of a word is searched); the name is not in the Latin alphabet;
  then the key of the row (section 6): it must contain the words, folded.
* **"Too many results / slow."** `limit` is per group and at most 20; each group is one query with a limit and the index. Look at the
  plan of the group (section 6) before anything else; a missing or invalid index (`indisvalid` false after a failed build) is the usual
  cause.
* **The logs** never hold the term: the only line is `Global search: N group(s) with results` at Debug level.
* **A search finds a booking by a code that looks like a word.** The code is looked up only if it has a digit or is the whole code (10
  characters), so ordinary words are not taken for codes.

## 10. Rollback

1. **Flag off** (`Features__GlobalSearch` unset or `false`): immediate, nothing is lost. `GET /api/search` answers 404; the migration and
   the keys stay, and cost only the space of the indexes and the work of the database on every write of these tables.
2. **Revert the migration** (only if the keys or the indexes themselves are the problem): turn the flag off **first** (with the
   columns gone, the endpoint would fail with a 500), then `dotnet ef database update <migration before AddGlobalSearchKeys>`. `Down`
   drops the six indexes and the five columns and keeps every row; applying the migration again computes the keys from the rows that
   are there (tested on PostgreSQL). Never drop the columns by hand: the model would no longer match the database.
3. `PUT /api/me/last-context` has nothing to roll back: it writes `Users.LastUsedContextKey`, a column that already existed and that
   `GET /api/me/contexts` already read.

## 11. Known limits and follow-ups

* **Only the beginning of a word is searched** ("tru" finds "Trullo Bianco", "ullo" does not): it is what an index serves. A
  "contains" search needs `pg_trgm` (section 5).
* **Scripts other than Latin** (Cyrillic, Greek, Arabic, CJK) are not folded: a name written only in them is not found by name in the
  palette (its e-mail address and the other fields still are). Follow-up UI-13a-FU3.
* **Categories are codes in English** in the database (`plumbing`): a term in Italian ("idraulico") finds a request through the name of
  the service the supplier wrote, not through the category; the title shows the category in the language of the caller. Follow-up
  UI-13a-FU2 (expand the category labels of the term on the server).
* **Suppliers** are the ones that already got a request from the org: the list of the preferred suppliers (`OrgTrustedSupplier`) does
  not exist yet. Follow-up UI-13a-FU1: when it lands, the group lists them.
* The term has at most five words (the others are ignored) and 64 characters.
* Phone, document, fiscal code and date of birth are not searched at all, on purpose.
* One limit per user; no second limit per IP. Follow-up UI-13a-FU5.
* Large databases: the migration rewrites the tables (section 6). Follow-up UI-13a-FU4.

## 12. Tests

| Test | Runs | What it proves |
|---|---|---|
| `GlobalSearchCasesTests`, `GlobalSearchServiceTests` | local and CI | about seventy questions with the ids they must find, for an owner, a collaborator limited to one property, an account in no team, another org, partial permissions and a supplier |
| `GlobalSearchNpgsqlTranslationTests` | local and CI | every group is translated to SQL by the Npgsql provider (no server needed) and the SQL holds the org, the scope and the index expression |
| `SearchTextTests`, `SearchFoldingTests` | local and CI | the term (case, accents, words, codes) and the folding table against the SQL steps over the whole basic plane |
| `AddGlobalSearchKeysMigrationSqlTests` | local and CI | the script of the migration; the model equals the migration; the query expression equals the index expression |
| `LegacyRowsSchemaTests` | local and CI | the SQL helpers that seed old schemas fit the schema at the point where tests use them |
| `SearchControllerTests`, `SearchAccessResolverTests`, `MeControllerLastContextTests`, `UserServiceLastContextTests`, `GlobalSearchRateLimitTests` | local and CI | the endpoint (limits, errors, headers, attributes), who may search what, the last context, the policy of the rate limit |
| `GlobalSearchHttpIntegrationTests` | CI on PostgreSQL (in memory locally) | the whole pipeline: flag off 404, 401, 400, 429, owner, collaborator, supplier, org boundary, nothing sensitive in the answer, `last-context` written and read back |
| `GlobalSearchPostgresTests` | CI only (needs PostgreSQL) | the cases on the real SQL; the key of every row equals the folding; the key cannot be written by hand and follows an update; the planner uses the indexes; 20,000 guests |
| `AddGlobalSearchKeysMigrationPostgresTests` | CI only | the migration on rows that existed before, the indexes, the revert |
| `GlobalSearchRunbookTests`, `SearchArchitectureTests` | local and CI | this page says what the code does; the term is never logged and the sensitive fields are never read |
