# Runbook: Hangfire (one schema per environment, no overlapping runs)

Task FD-11 (audit defect A9-03, P0). The code is in place; the product owner applies the Supabase and Railway
steps below, test first, then production.

## The problem

Test and production use **one** Supabase database and differ only by the EF `SearchPath`
(`casazen_test` / `casazen_prod`). Until FD-11 Hangfire always used the fixed schema `hangfire` in that same
database, so both Railway environments:

- consumed **the same queue**: a production Stripe webhook job could be taken by the test worker, which looks
  for the booking in `casazen_test`, does not find it and loses the event;
- shared **the same recurring jobs** (same ids, e.g. `direct-booking-charge`): the daily production run could be
  executed by the test server against the test data, so production charges were skipped;
- registered each other's job types: a `develop` deploy with a new job left a recurring job the production
  server cannot deserialize.

## What the backend does now

| Concern | Behaviour | Code |
|---|---|---|
| Schema | 1. `Hangfire:Schema` when set. 2. Otherwise `hangfire_<first SearchPath schema>`, e.g. `hangfire_casazen_prod`. 3. Otherwise, outside Production only, `hangfire_<environment>` (e.g. `hangfire_development` locally). 4. Production with neither: **startup fails** with an explicit message. `hangfire` together with a SearchPath is refused at startup. Only lowercase letters, digits and `_` are accepted | `Casazen.Web/Configuration/HangfireStorageSettings.cs`, `Casazen.Web/Extensions/HangfireServiceCollectionExtensions.cs` |
| Startup log | `Hangfire storage schema: hangfire_casazen_prod`, plus Hangfire's own `Starting Hangfire Server using job storage: '… Schema: …'` | `Casazen.Web/Program.cs` |
| Recurring jobs | Registered (`AddOrUpdate`) at every startup in the environment's schema | `Casazen.Web/BackgroundJobs/RecurringJobsRegistration.cs` |
| No overlapping runs | `[DisableConcurrentExecution]` on every recurring job, and on the Alloggiati report per booking. A run waits for the previous one (60 s for jobs every 5–15 minutes, 300 s otherwise), then fails with a lock timeout and Hangfire retries it later | `Casazen.Web/BackgroundJobs/JobLockTimeouts.cs` and each job |
| Lock expiry | `Hangfire:DistributedLockTimeoutMinutes`, default **30** (Hangfire.PostgreSql's own default is 10). A lock older than this is considered abandoned and taken over, so it must exceed the longest run; it is also the longest a lock survives a crashed container | `HangfireStorageSettings` |
| Dashboard | Unchanged: off by default (`Hangfire:DashboardEnabled=false`), behind `HangfireAuthorizationFilter` (API key header or Admin role). The title now shows the schema | `Casazen.Web/Program.cs` |

Locks live in the environment's own schema (table `<schema>.lock`, resource `<schema>:<Job>.<Method>`), so test
and production never block each other.

| Recurring job | Cron (UTC) | Lock resource | Wait |
|---|---|---|---|
| `ota-sync-all` (only with `Features:OtaPartnerApi=true`, otherwise removed at startup: FD-20, [feature-flags.md](feature-flags.md)) | hourly | `OtaSyncJob.ExecuteAsync:<propertyId>` (also manual syncs) | 300 s |
| `booking-pull-all` (only with `Features:OtaPartnerApi=true`, otherwise removed at startup) | `*/15` | `BookingPullJob.ExecuteAsync:<propertyId>` | 60 s |
| `dynamic-pricing-adaptation` | 02:00 | `DynamicPricingJob` (shared with the per-property manual run) | 300 s |
| `gdpr-data-retention` | 03:00 | `GdprDataRetentionJob.ExecuteAsync` | 300 s |
| `stay-alerts` (CO-10, see [§9](#9-stay-alerts-co-10); replaces `alloggiati-deadline-alert` and `guest-checkin-reminder`, removed at startup) | hourly | `StayAlertsJob.ExecuteAsync` (plus a PostgreSQL advisory lock per run) | 300 s |
| `cin-deadline-alert` (CO-20: host alert about properties without a valid CIN, once per stage, see [cin-format.md](cin-format.md#cin-deadline-and-host-alerts-co-20)) | 08:00 | `CinDeadlineAlertJob.ExecuteAsync` (plus a PostgreSQL advisory lock per run) | 300 s |
| `lease-sign-status-poll` | `*/10` | `LeaseSignStatusPollingJob.ExecuteAsync` | 60 s |
| `lease-registration-status-poll` | `*/5` | `LeaseRegistrationStatusPollingJob.ExecuteAsync` | 60 s |
| `rli-deadline-reminder` (LT-04, see [§8](#8-rli-deadline-reminder-lt-04)) | 08:00 | `RliDeadlineReminderJob.ExecuteAsync` | 120 s |
| `seo-content-refresh` | 04:00 on day 1 | `SeoContentRefreshJob.ExecuteAsync` | 300 s |
| `direct-booking-charge` | 06:00 | `DirectBookingChargeJob.ExecuteAsync` | 300 s |
| `checkout-hold-expiry` (BK-21 and BK-06, see [§7](#7-checkout-hold-expiry-bk-21)) | `*/5` | `CheckoutHoldExpiryJob.ExecuteAsync` (plus a row lock per hold) | 60 s |
| `ical-supplier-sync` (SU-15: active **and pending** suppliers with an iCal URL, see [ical.md](ical.md#supplier-calendars-su-15)) | `*/15` | `IcalSupplierSyncJob.ExecuteAsync` (plus a PostgreSQL advisory lock per supplier while its days are written) | 60 s |
| `property-ical-sync` | `*/15` | `PropertyICalSyncJob.ExecuteAsync` | 60 s |
| `guest-checkin-send` (CO-09: expires stale links, queues `GuestCheckInLinkEmailJob`, see [alloggiati.md](alloggiati.md#guest-check-in-link-and-host-fallback-co-09)) | 08:00 | `GuestCheckInSendJob.ExecuteAsync` | 300 s |
| `property-compliance-check` (CO-06, see [§10](#10-property-compliance-check-co-06)) | 04:00 | `PropertyComplianceCheckJob.ExecuteAsync` (plus a PostgreSQL advisory lock per run) | 300 s |
| `push-receipts` (MO-04, see [§11](#11-push-notifications-mo-04)) | `*/15` | `PushReceiptsJob.ExecuteAsync` | 60 s |

On-demand: `AlloggiatiWebReportJob.ReportGuestAsync` locks per booking (`…ReportGuestAsync:<bookingId>`), so two
submissions of the same booking to Alloggiati Web never run at once. `PushDeliveryJob.SendAsync` (MO-04) locks per
delivery key (`PushDeliveryJob.SendAsync:<key>`, 60 s), so two runs of the same push event never overlap.
`IcalSupplierSyncJob.SyncSupplierAsync` (first sync of a supplier's iCal URL and "sync now", SU-15) locks per
supplier (`…SyncSupplierAsync:<orgId>`, 60 s).

The test `RecurringJobsConcurrencyTests` fails if a recurring job is added without `[DisableConcurrentExecution]`.

## 1. Check now: do test and production share the queue?

Read-only; run it in the Supabase **SQL editor** before deploying FD-11.

**1a. Servers in the shared schema**

```sql
SELECT id,
       lastheartbeat,
       now() - lastheartbeat        AS since_heartbeat,
       data ->> 'StartedAt'         AS started_at,
       data ->> 'WorkerCount'       AS workers
FROM hangfire.server
ORDER BY lastheartbeat DESC;
```

A server is alive when its heartbeat is less than a minute or two old. The id is
`<container hostname>:<pid>:<guid>`.

**1b. Which environment is each server?** In Railway → each environment → the service → **Deploy logs** of the
current deployment, search `successfully announced`: the line `Server <id> successfully announced` gives that
environment's server id. If ids from **both** the test and the production logs appear in `hangfire.server`
(ignore the short overlap of old and new container during a deploy), the queue is shared: deploy FD-11 as soon as
possible.

**1c. Who executed the critical jobs** (only jobs not yet expired: succeeded jobs are kept for one day, failed jobs
until deleted)

```sql
SELECT j.id,
       j.createdat,
       j.statename,
       j.invocationdata ->> 'Type'  AS job_type,
       s.data ->> 'ServerId'        AS executed_by
FROM hangfire.job j
JOIN hangfire.state s ON s.jobid = j.id AND s.name = 'Processing'
WHERE j.invocationdata ->> 'Type' LIKE 'Casazen.Web.BackgroundJobs.DirectBookingChargeJob%'
   OR j.invocationdata ->> 'Type' LIKE 'Casazen.Web.BackgroundJobs.StripeWebhookJob%'
ORDER BY j.createdat DESC
LIMIT 200;
```

A `DirectBookingChargeJob` executed by the test server means that day's production charges were skipped; a
failed `StripeWebhookJob` executed by the test server means a production event was lost. Replay them as in §3.4.

**1d. Pending jobs** (they will not run after the switch, see §3.4)

```sql
SELECT j.id,
       j.statename,
       j.createdat,
       j.invocationdata ->> 'Type'   AS job_type,
       j.invocationdata ->> 'Method' AS method,
       j.arguments
FROM hangfire.job j
WHERE j.statename IN ('Enqueued', 'Scheduled', 'Processing', 'Awaiting', 'Failed')
ORDER BY j.createdat;
```

Save the output: it is the to-do list for §3.4.

## 2. Railway variables

Set per environment (Railway → environment → service → **Variables**):

| Variable | test | production |
|---|---|---|
| `Hangfire__Schema` | `hangfire_casazen_test` | `hangfire_casazen_prod` |
| `Hangfire__DistributedLockTimeoutMinutes` | optional, default `30` | optional, default `30` |
| `ConnectionStrings__DefaultConnection` | unchanged, `SearchPath=casazen_test` | unchanged, `SearchPath=casazen_prod` |
| `Hangfire__DashboardEnabled` | `false` (enable only temporarily) | `false` |
| `Hangfire__DashboardApiKey` | only while the dashboard is enabled | same |

- The values equal the names derived from the SearchPath, so setting them moves nothing; they make the choice
  visible and keep it stable if the connection string changes.
- Never use the same value in two environments, and never `hangfire` with a SearchPath (startup refuses it).
- Production runs with `ASPNETCORE_ENVIRONMENT=Production`: without a SearchPath and without `Hangfire__Schema` the
  service does not start (`Hangfire schema is ambiguous…`). The test environment runs as `Staging` (PL-11): there the
  fallback would be `hangfire_staging`, so keep `Hangfire__Schema=hangfire_casazen_test` set.
- **Railway PR environments** copy the variables of their base environment. If they are enabled, give them their
  own `Hangfire__Schema` (e.g. `hangfire_casazen_pr`) — better, their own database — otherwise an unreviewed PR
  build consumes the test queue.
- On first start Hangfire creates its schema and tables (`PrepareSchemaIfNecessary`): the database user needs
  `CREATE` on the database, or the schema must exist and belong to that user (§4).

## 3. Switching over (one-time)

1. Run §1 and save the output of 1d.
2. **Test**: set `Hangfire__Schema=hangfire_casazen_test`, deploy `develop`. Verify with §5. From now on only
   production reads `hangfire`.
3. **Production**: set `Hangfire__Schema=hangfire_casazen_prod`, release to `main`. Verify with §5.
4. **Pending jobs of the old schema.** Recurring jobs need nothing: each environment re-registers them in its own
   schema at every startup; the old definitions in `hangfire` stay inert. Everything else left in `hangfire`
   (enqueued, scheduled — retries and checkout reminders included — interrupted, awaiting) **never runs**: no server
   reads that schema any more. "Letting them expire" does not happen by itself either: Hangfire removes expired
   rows through the expiration manager of a server attached to the schema, and there is none. Handle the list from
   1d by hand; the arguments (booking id, event id) tell the environment — look the id up in `casazen_prod` and
   `casazen_test`:

   | Job type | What to do |
   |---|---|
   | `StripeWebhookJob` | Stripe Dashboard → Developers → Events → the event → **Resend** to that environment's endpoint (processing is idempotent per event id) |
   | `AlloggiatiWebReportJob` | Send the booking's report again; the hourly `stay-alerts` job (CO-10) alerts the host of every communication still missing |
   | `CheckoutReminderJob` | Nothing: since CO-10 the check-out reminder comes from the hourly `stay-alerts` job for every stay; the class no longer exists (delete the job) |
   | `ESignWebhookJob` | Ask the e-sign provider to resend the event, or check the lease status there |
   | `OtaSyncJob`, `DynamicPricingJob`, `SeoPageGenerationJob` | Trigger again from the app/admin if needed; periodic work is covered by the next recurring run |
   | `DirectBookingChargeJob` and other recurring jobs | Nothing: the next run in the new schema catches up (it charges every booking past its deadline without a completed charge) |

   Before CO-10, bookings stored the id of their checkout reminder job (`Bookings.CheckoutReminderJobId`); the
   migration `AddStayAlertStates` drops the column, the reminder is no longer a job per booking.

5. **Right after each environment's first start in its new schema**, move its job id sequence past the old ids.
   Alloggiati reports store the Hangfire id of their arrival-day job (`AlloggiatiWebReports.ScheduledJobId`, CO-11),
   and ids restart from 1 in a new schema: without this, replacing an old job could delete an unrelated new job with
   the same number.

   ```sql
   SELECT setval('hangfire_casazen_test.job_id_seq', (SELECT last_value FROM hangfire.job_id_seq) + 1000000);
   SELECT setval('hangfire_casazen_prod.job_id_seq', (SELECT last_value FROM hangfire.job_id_seq) + 1000000);
   ```

6. **Cleanup**, after both environments have run on their own schema for at least a week and 3.4 is done:
   optionally keep a copy (`pg_dump --schema=hangfire …`), then

   ```sql
   DROP SCHEMA hangfire CASCADE;
   ```

## 4. Stronger isolation (recommended)

Separate schemas stop the shared queue, but both environments still connect as `postgres`: a bug or an unreviewed
build running in test can still read and write `casazen_prod` and `hangfire_casazen_prod`.

**Option A — separate Supabase projects (preferred).** One project for test, one for production (the free plan
allows two active projects). Test code cannot reach production data at all, and backups, keys, pausing and limits
are separate. Each project keeps the same layout (`casazen_*` + `hangfire_casazen_*`), only the connection strings
change. Migrate the test data by re-running the migrations on the new project (`scripts/migrate.sh test`).

**Option B — one login role per environment**, limited to its own schemas. Sketch, to rehearse on test first
(SQL editor, as `postgres`; choose strong passwords and store them only in Railway):

```sql
CREATE ROLE casazen_test_app LOGIN PASSWORD '<strong password>';
CREATE ROLE casazen_prod_app LOGIN PASSWORD '<strong password>';
GRANT casazen_test_app, casazen_prod_app TO postgres;  -- lets postgres hand objects over to them

-- Each role owns only its environment's schemas (EF migrations and Hangfire's installer run DDL at startup).
CREATE SCHEMA IF NOT EXISTS hangfire_casazen_test AUTHORIZATION casazen_test_app;
CREATE SCHEMA IF NOT EXISTS hangfire_casazen_prod AUTHORIZATION casazen_prod_app;
ALTER SCHEMA casazen_test OWNER TO casazen_test_app;
ALTER SCHEMA casazen_prod OWNER TO casazen_prod_app;

-- Existing tables and sequences stay owned by postgres: hand them over (repeat with casazen_prod / casazen_prod_app).
DO $$
DECLARE r record;
BEGIN
  FOR r IN SELECT format('%I.%I', schemaname, tablename) AS t FROM pg_tables WHERE schemaname = 'casazen_test' LOOP
    EXECUTE 'ALTER TABLE ' || r.t || ' OWNER TO casazen_test_app';
  END LOOP;
END $$;

-- Only if Hangfire's installer fails with "permission denied for database postgres":
-- GRANT CREATE ON DATABASE postgres TO casazen_test_app, casazen_prod_app;

-- No access to the other environment: nothing is granted, so only check that no grant was left behind.
SELECT grantee, table_schema, count(*) FROM information_schema.role_table_grants
WHERE grantee IN ('casazen_test_app', 'casazen_prod_app') GROUP BY 1, 2;
```

Then set the connection string of each Railway environment to its role (with the Supabase pooler the user name
is `<role>.<project ref>`), check §5, and stop using `postgres` in Railway. Do the step for `hangfire_casazen_*`
before the first start with FD-11, or hand over the tables Hangfire created with the same `DO` block.

## 5. Verify after each deploy

- Railway deploy logs: `Hangfire storage schema: hangfire_casazen_test` (or `_prod`).
- SQL:

  ```sql
  -- One live server per environment, each in its own schema
  SELECT 'test' AS env, id, lastheartbeat FROM hangfire_casazen_test.server
  UNION ALL
  SELECT 'prod', id, lastheartbeat FROM hangfire_casazen_prod.server
  ORDER BY env, lastheartbeat DESC;

  -- 17 recurring jobs in each schema with every flag on; 13 with the defaults
  -- (Features:OtaPartnerApi, Features:RliProvider and Features:ESignProvider off)
  SELECT 'test' AS env, count(*) FROM hangfire_casazen_test.set WHERE key = 'recurring-jobs'
  UNION ALL
  SELECT 'prod', count(*) FROM hangfire_casazen_prod.set WHERE key = 'recurring-jobs';

  -- Must be empty once both environments are switched
  SELECT id, lastheartbeat FROM hangfire.server WHERE lastheartbeat > now() - interval '5 minutes';
  ```

- If the dashboard is enabled temporarily, its title shows the schema.

## 6. Troubleshooting

| Symptom | Meaning / action |
|---|---|
| Startup fails: `Hangfire schema is ambiguous…` | Production without SearchPath and without `Hangfire__Schema`: set the variable (§2) |
| Startup fails: `Hangfire:Schema 'hangfire' is the Hangfire schema shared…` | The old shared value is set: use `hangfire_casazen_test` / `hangfire_casazen_prod` |
| Job failed with `DistributedLockTimeoutException` (`…distributed lock on the '…' resource`) | The previous run was still going; Hangfire retries it. Occasional on the 15-minute syncs when a run is slow; if systematic, the job is too slow for its schedule |
| A job seems blocked by a lock after a crash | `SELECT resource, acquired FROM hangfire_casazen_prod.lock ORDER BY acquired;` The lock expires after `Hangfire__DistributedLockTimeoutMinutes`. Delete the row only if the dashboard shows no run of that job in *Processing* |
| A job legitimately runs longer than 30 minutes | Raise `Hangfire__DistributedLockTimeoutMinutes` above its duration. Hangfire.PostgreSql also hands a job still running after 30 minutes (invisibility timeout) to another worker; the lock makes that second run wait instead of running in parallel |

## 7. Checkout hold expiry (BK-21)

Audit defect A3-13. The public checkout (`POST /api/public/bookings`) stores a `Direct` booking `Pending` while the
guest pays (PaymentIntent) or saves a card (SetupIntent). The hold lasts `DirectBooking:PendingTtlMinutes`
(default 30 since BK-04; it was 15) from its creation. Before BK-21 an abandoned hold kept its dates busy on the booking site, in the host
calendar and in the iCal export (so on Airbnb/Booking) until someone tried to book the same dates.

**What counts as an expired hold** — one definition, `Casazen.Core/Services/CheckoutHolds.cs`, used by the job, by
the cleanup before a new booking, by public availability, by the host calendar, by the iCal export and by the overlap
checks: `Pending` + source `Direct` and either
- a **payment hold**: payment option not `OnSite` + a PaymentIntent or SetupIntent + no payment
  `Processing`/`Completed` + created more than the TTL ago;
- or a **"pay at the property" request** (`OnSite`, BK-06, decision D5) past its own deadline `RequestExpiresAt`: the
  email confirmation window (`DirectBooking:OnSiteEmailVerificationMinutes`, default the checkout TTL), then, once the
  guest has confirmed the email, the host's answer deadline (`DirectBooking:OnSiteApprovalHours`, **provisional**
  default 24). A request created before BK-06 without a deadline expires with the checkout TTL. Details:
  [direct-booking.md](direct-booking.md).

- **Reads** (availability, calendar, iCal export) leave expired holds out at once, before the job runs. They never
  cancel anything.
- **Job `checkout-hold-expiry`**, every 5 minutes (`CheckoutHoldExpiryJob` → `CheckoutHoldExpiryService`). For each
  expired hold, in its own transaction holding the booking row (`FOR UPDATE SKIP LOCKED`: a concurrent run skips it):
  1. reads the intent on the connected account it was created on (`Stripe-Account` header: the payment row's
     `StripeAccountId`, stored since BK-02, else the org's current account);
  2. `succeeded`, `processing` or `requires_capture` (PaymentIntent) / `succeeded`, `processing` (SetupIntent): the
     guest has paid or is paying. The booking is **not** cancelled; the payment row becomes `Processing`, so the hold
     keeps its dates, and the payment webhook confirms it (a later failure makes it expire at the next run).
     Log: `Checkout hold {BookingId} not expired: payment intent … is succeeded; left to the payment webhook`;
  3. otherwise cancels the intent (`cancellation_reason=abandoned`, idempotency key
     `checkout-hold-expiry:<bookingId>:<intentId>:<status>:<latest attempt>`), then sets the booking `Cancelled`
     with `CancellationReason = CheckoutHoldExpired` (1) and its uncollected payment rows `Canceled`, as a host
     cancellation does (BK-02).
     Log: `Checkout hold {BookingId} expired: intent cancelled on Stripe, dates released`.
     A "pay at the property" request has no intent: it is cancelled with `CancellationReason = OnSiteRequestExpired`
     (4, the host did not answer; the guest gets the "request expired" email, queued after the commit) or
     `OnSiteEmailNotConfirmed` (5, the guest never confirmed the email; no email).
     Log: `On-site request {BookingId} expired (<reason>): dates released`;
  4. a Stripe error leaves the hold untouched; the next run retries it (log `could not be expired`). An intent that is
     not found on that account (`resource_missing`), or no connected account at all, releases the dates with a
     warning.
- **Before a new booking** of the same dates (public checkout or host booking) the same routine runs on the
  overlapping expired holds only: a late payment keeps the dates (the new request gets 409), otherwise the hold is
  cancelled with its intent.
- **Never touched**: host bookings (`Manual`), OTA bookings, every status other than `Pending`, and "pay at the
  property" (`OnSite`) requests before their own deadline, whatever their age: under decision D5 they wait for the
  host's approval, never the checkout TTL (BK-06). `Pending` payment bookings without a Stripe intent are not holds
  either (legacy host bookings created as `Direct` before PC-01).
- The host's accept / decline of a request (BK-06) lock the booking row (`FOR UPDATE`): a run finding it locked skips
  it (`SKIP LOCKED`), and a run holding it makes the answer wait and then answer 409.

Nothing to configure on Railway: the job is registered at startup like the others. To change the TTL set
`DirectBooking__PendingTtlMinutes` (keep it longer than the time a guest needs for 3-D Secure; why 30 minutes:
`docs/runbooks/stripe.md` § "Late payments"). A payment that still succeeds after the hold was released is confirmed
again or refunded in full by the payment webhook (BK-04, same section). The "pay at the property" deadlines are in
[direct-booking.md §2](direct-booking.md#2-configuration-railway-variables-per-environment).

**Checks** (SQL editor, replace the schema):

```sql
-- Expired holds still waiting for the job (should be empty or only a few minutes old)
SELECT b."Id", b."CreatedAt", now() - b."CreatedAt" AS age
FROM casazen_prod."Bookings" b
WHERE b."Status" = 0 AND b."Source" = 0 AND b."PaymentOption" <> 2
  AND (b."StripeSetupIntentId" IS NOT NULL
       OR EXISTS (SELECT 1 FROM casazen_prod."Payments" p WHERE p."BookingId" = b."Id" AND p."StripePaymentIntentId" IS NOT NULL))
  AND NOT EXISTS (SELECT 1 FROM casazen_prod."Payments" p WHERE p."BookingId" = b."Id" AND p."Status" IN (1, 2))
  AND b."CreatedAt" < now() - interval '30 minutes'  -- DirectBooking__PendingTtlMinutes
ORDER BY b."CreatedAt";

-- Holds expired by the system, last 7 days
SELECT count(*) FROM casazen_prod."Bookings"
WHERE "CancellationReason" = 1 AND "UpdatedAt" > now() - interval '7 days';

-- Holds left to the webhook for more than an hour: payment Processing but booking still Pending
SELECT b."Id", p."StripePaymentIntentId", p."UpdatedAt"
FROM casazen_prod."Bookings" b JOIN casazen_prod."Payments" p ON p."BookingId" = b."Id"
WHERE b."Status" = 0 AND b."Source" = 0 AND p."Status" = 1 AND p."UpdatedAt" < now() - interval '1 hour';
```

The last query should stay empty: a row there means the payment webhook did not arrive (check the Connect endpoint and
`Stripe__ConnectWebhookSecret`, then resend the event from the Stripe Dashboard) or a SEPA payment is still
processing. A SetupIntent still `processing` is not recorded on the booking: the job reads it again at every run until
the webhook confirms it.

## 8. RLI deadline reminder (LT-04)

Audit defect A7-04. `rli-deadline-reminder` (daily 08:00 UTC, `RliDeadlineReminderJob`) reminds the landlord of the
RLI registration deadline `min(stipula, start) + 30` days (rule and details: [rli.md](rli.md#registration-deadline-lt-04)).

- Covers every lease not registered yet, from `Draft` on (`Registered` and `Rejected` excluded).
- Thresholds, not exact days: ≤ 15, ≤ 7, ≤ 1 days (the deadline day included), overdue from the day after. The most
  urgent threshold reached is sent once per deadline, so a failed, skipped or late run is caught up by the next one and
  a retry or a manual trigger from the dashboard never sends twice (`DeadlineReminderSent` event, payload
  `{threshold}:{deadline}`, written only after the email is accepted).
- A failed email is logged (`RLI reminder … not sent`) and retried at the next daily run, not by Hangfire retries.
- Nothing to configure on Railway. To send the reminders of the day again after an outage, trigger the job from the
  dashboard: already-sent thresholds are skipped.

```sql
-- Reminders sent today, per lease
SELECT "LeaseContractId", "Payload", "OccurredAt" FROM casazen_prod."LeaseEvents"
WHERE "EventType" = 12 AND "OccurredAt" >= date_trunc('day', now())
ORDER BY "OccurredAt";
```

## 9. Stay alerts (CO-10)

Audit defects A5-11, A6-07 (P1) and A5-25. Until CO-10 the hourly `alloggiati-deadline-alert` and the daily
`guest-checkin-reminder` sent the same "Check-in incompleto" push and email to every stay with incomplete guest data or
a communication not sent, **every hour**, from 24 hours before arrival to 8 days after (also for a failed report). With
no transmission to the Questura yet (CO-11) every stay qualified: the provider's daily quota ran out and the guests'
check-in links stopped going out. The check-out reminder was a push-only job scheduled only by the host check-in.

**What runs now**: one recurring job, `stay-alerts`, hourly (`0 * * * *` UTC): `StayAlertsJob` → `StayAlertService`.

| Alert | When | To |
|---|---|---|
| Alloggiati Web sequence: guest data missing, deadline approaching, overdue, at most `MaxOverdueReminders` daily reminders; "invio fallito" for a failed or rejected communication | Stages and maximum per stay: [alloggiati.md § "Host alerts"](alloggiati.md#host-alerts-co-10) | Email to the org contact address + push to the property's hosts |
| Check-out reminder | `Compliance:CheckoutReminderHourLocal` (default 20) of the check-out day, property time zone (Europe/Rome when the property has none); dropped if the job is more than 12 hours late | Same; push "Promemoria check-out" |

- **Every confirmed stay** gets the check-out reminder, whether or not the host registered the arrival (A5-25). The job
  reads the booking as it is at each run: moved dates move the reminder, a cancellation or the check-out drops it.
  Nothing is scheduled per booking, so no stale job can fire on old dates.
- **Once per stage**: table `StayAlertStates`, one row per booking and type (`Type` 1 Alloggiati deadline, 2 failed
  communication, 3 check-out reminder; `ReferenceDate` = check-in or check-out date, `Stage`, `AlertCount`,
  `LastAlertAt`). Before sending, the run moves the row forward with one conditional `UPDATE`; only the caller that moved
  it sends. A retry, a manual trigger from the dashboard or a second server never sends a stage twice. When the booking's
  date changes, the sequence starts again for the new date. A delivery that fails after the claim is logged
  (`claimed but not delivered`) and not repeated: at most once. The email is queued on `EmailDeliveryJob` (5 attempts).
- **One run at a time**: `[DisableConcurrentExecution]` plus a PostgreSQL session advisory lock
  (`pg_try_advisory_lock(1011, …)`, `PostgresAdvisoryLocks.Scope.StayAlertsRun`); a run that finds it taken logs
  `Stay alerts run skipped: another run is in progress` and does nothing.
- **No backlog**: only the most advanced stage due is sent, so a run after an outage sends one message per stay; the last
  Alloggiati stage is dropped a day after its time.
- **Volume**: with the defaults a stay gets at most 5 Alloggiati messages, 1 "invio fallito" and 1 check-out reminder
  per date (each one email + one push), instead of up to 24 emails a day.

**Configuration** (Railway variables, optional; defaults in `appsettings.json`):

| Variable | Default | Meaning |
|---|---|---|
| `StayAlerts__GuestDataReminderHourLocal` | `10` | Hour (Europe/Rome) of the day before arrival for "guest data missing" |
| `StayAlerts__DeadlineWarningHours` | `12` | Hours before the deadline for "deadline approaching" (never before the arrival day) |
| `StayAlerts__MaxOverdueReminders` | `2` | Daily reminders after "overdue" (`0` = none); maximum per stay = 3 + this |
| `StayAlerts__OverdueReminderHourLocal` | `9` | Hour (Europe/Rome) of the daily reminders |
| `Compliance__CheckoutReminderHourLocal` | `20` | Hour (property time) of the check-out reminder |

**At the first deploy of CO-10**:

1. `RecurringJobsRegistration` removes `alloggiati-deadline-alert` and `guest-checkin-reminder` from the environment's
   schema and registers `stay-alerts` (§5 counts the recurring jobs).
2. Check-out reminder jobs scheduled before CO-10 (`CheckoutReminderJob.SendReminderAsync`, tab *Scheduled*) fail with a
   type load error when they come due: delete them from the dashboard (*Scheduled* / *Failed* → select → *Delete*).
   Their stays get the reminder from `stay-alerts` anyway. The migration `AddStayAlertStates` drops
   `Bookings.CheckoutReminderJobId`.
3. The first run sends at most one message per stay checked in within the last 5 days (or arriving tomorrow) whose
   communication is neither sent nor declared sent: the most advanced stage due, then the sequence goes on normally.

**Checks** (SQL editor, replace the schema):

```sql
-- Alerts of the last 7 days per stay and type: AlertCount at most 5 for type 1 with the defaults, 1 per date otherwise
SELECT "BookingId", "Type", "ReferenceDate", "Stage", "AlertCount", "LastAlertAt"
FROM casazen_prod."StayAlertStates"
WHERE "LastAlertAt" > now() - interval '7 days'
ORDER BY "LastAlertAt" DESC;

-- Messages sent per day (one email + one push each)
SELECT date_trunc('day', "LastAlertAt") AS day, count(*) FROM casazen_prod."StayAlertStates"
WHERE "LastAlertAt" > now() - interval '7 days' GROUP BY 1 ORDER BY 1;

-- Old check-out reminder jobs still in the schema (delete them from the dashboard)
SELECT id, statename, createdat FROM hangfire_casazen_prod.job
WHERE invocationdata ->> 'Type' LIKE 'Casazen.Web.BackgroundJobs.CheckoutReminderJob%'
  AND statename IN ('Scheduled', 'Failed', 'Enqueued');
```

The second query counts the stays alerted per day (a row keeps only its last message): for exact volumes use the
provider's dashboard (Resend → Emails, filter by subject).

## 10. Property compliance check (CO-06)

Audit defects A5-20 (P1) and A5-36. Full procedure, rules and SQL: [compliance.md](compliance.md).

**What runs**: `property-compliance-check`, daily at 04:00 UTC: `PropertyComplianceCheckJob` →
`IPropertyComplianceStatusService.RecalculateAllAsync`. Every property with `ComplianceStatus` `Active` or `Suspended`
is evaluated with the activation blockers (base data, CIN, required documents, D.L. 145/2023 safety checklist): an
active one with a blocker becomes `Suspended` (not published, reason stored) and its host gets one
`property-compliance-suspended` email; a suspended one without blockers becomes `Active` again (no email). Nothing else
changes: pending properties are not touched, bookings are never cancelled.

- **Idempotent**: only the `Active` → `Suspended` transition emails the host, and a suspended property still
  incomplete stays as it is, so a retry, a manual trigger or the next night sends no second email. The transition of each property runs under the advisory lock `PropertyComplianceStatus` (1030): a host
  request suspending the same property at the same time sends one email in total.
- **One run at a time**: `[DisableConcurrentExecution]` plus the session advisory lock `PropertyComplianceCheckRun`
  (1031), shared with the one-shot command `dotnet Casazen.Web.dll compliance:recalculate [--dry-run]`; a run that
  finds it taken logs `Property compliance check skipped: another run is in progress` and does nothing.
- **Isolation**: a property whose evaluation fails is logged (`Compliance check of property … failed; continuing with
  the next property`) and counted as `failed`; the run goes on. A run that fails as a whole (database down) is retried
  by Hangfire and is safe to repeat.
- **First run after the CO-06 deploy** = recalculation of the historic properties: the email of that first evaluation
  follows `Compliance__StatusCheck__NotifyOnFirstCheck` (default `false`), see [compliance.md §5](compliance.md#5-recalculation-of-the-historic-properties-a5-36).
- **Log**: `Property compliance check completed: N active or suspended properties checked, S suspended, R reactivated,
  E hosts notified, F failed, blockers …`.

## 11. Push notifications (MO-04)

Audit defects A6-08 and A6-29. Until MO-04 every push was sent inside the request that caused it, one HTTP call to Expo
per device (a supplier's *take* waited for every host phone), without receipts: dead tokens were never removed. What the
pushes are and when they are sent: [mobile-release.md § 9.1 and § 9.7](mobile-release.md#97-backend-delivery-queue-batches-receipts-mo-04).

**Jobs**

| Job | When | What |
|---|---|---|
| `PushDeliveryJob.SendAsync(key, push)` | Queued (`Enqueue`) by the service that made the change, after its save | Resolves the devices of the audience, claims one `PushDeliveries` row per device for the key, sends the pending ones to Expo in batches of at most 100, stores the tickets. `[AutomaticRetry(Attempts = 5)]`, deleted after the last attempt (the arguments hold the push text: property, category, dates; no guest name, no token) |
| `push-receipts` (`PushReceiptsJob` → `PushReceiptService`) | Recurring, every 15 minutes (`*/15 * * * *` UTC) | Reads the receipts of the tickets accepted at least 15 minutes earlier (1000 ids per request, at most 10 000 per run), deletes the device registrations with `DeviceNotRegistered`, logs every other error code, marks tickets without a receipt after 24 hours, purges rows older than 7 days (5000 per run) |

**Once per event and device.** `PushDeliveries` has a unique index on (`DeliveryKey`, `PushToken`). A retry of the job
(Hangfire retries a job that throws), a manual *Requeue* from the dashboard or the same event queued twice skip every
device that already has a row for the key: only rows still `Pending` are sent. Statuses: 0 `Pending`, 1 `Sending`
(handed to Expo without an outcome: never repeated), 2 `Accepted` (ticket ok, receipt not read), 3 `Delivered`,
4 `Failed` (`Error` = Expo code or `Http<status>`), 5 `ReceiptUnavailable`.

**Configuration**: optional `Expo__AccessToken` (Railway, both environments; see
[mobile-release.md § 9.7.3](mobile-release.md#973-expo-access-token-optional-recommended)). Nothing else.

**At the first deploy**: the migration `AddPushDeliveries` creates the empty table; `push-receipts` is registered at
startup (§5 counts it). Nothing to migrate: pushes sent before the deploy have no ticket stored, so their receipts are not
read.

**Checks** (SQL editor, replace the schema):

```sql
-- Pushes of the last 24 hours per type and status
SELECT "Type", "Status", count(*) FROM casazen_prod."PushDeliveries"
WHERE "CreatedAt" > now() - interval '24 hours' GROUP BY 1, 2 ORDER BY 1, 2;

-- Errors of the last 7 days (Expo codes): DeviceNotRegistered removes the device, the others need a look
SELECT "Error", count(*) FROM casazen_prod."PushDeliveries"
WHERE "Status" = 4 GROUP BY 1 ORDER BY 2 DESC;

-- Messages stuck: pending after their job was deleted, or accepted without a receipt read (push-receipts not running?)
SELECT "Status", count(*), min("CreatedAt") FROM casazen_prod."PushDeliveries"
WHERE ("Status" = 0 AND "CreatedAt" < now() - interval '1 day')
   OR ("Status" = 2 AND "SentAt" < now() - interval '1 hour')
GROUP BY 1;

-- Push jobs that failed (Expo down for hours): they are deleted after 5 attempts; failed ones still retrying
SELECT id, statename, createdat FROM hangfire_casazen_prod.job
WHERE invocationdata ->> 'Type' LIKE 'Casazen.Infrastructure.Push.PushDeliveryJob%'
  AND statename IN ('Failed', 'Scheduled', 'Enqueued')
ORDER BY createdat DESC LIMIT 20;
```

**Logs**: `Push <type> queued for <audience> <id> (key …)`, then `Push <type> (key …): N of M messages accepted by Expo`
or `nothing to send`; `Expo did not take … retried by Hangfire` (429/5xx: the job retries); `outcome of … unknown …, not
repeated` (timeout); `refused … not retried` (4xx: for 401 see the access token); every 15 minutes `Push receipts: …
checked, … delivered, … failed, … devices removed`. No push token appears in the logs.
