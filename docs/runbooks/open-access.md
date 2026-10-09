# Runbook: "accesso aperto" (open access) — a tier override from configuration

Task BL-01 of the redesign wave spec (backend only; decisions D-A, D33, D35; `gap/07` section 6, decision 2). The product
owner decided on 2026-10-08 that **every plan is paid** (Starter 29, Pro 79, Scale 199 EUR per month plus VAT) but that
**for now everything is open**. This runbook is the switch that makes the second half true in the backend without touching
the first: prices, plans, subscriptions and payments stay exactly as they are. **The switch is off by default: with the
default configuration nothing changes.**

Why a switch was needed: an org without a subscription is served as `Starter` (`EntitlementService`, fail-closed, #274), and
the property limit, the custom domain, the "Realizzato con" label (and, with AM-02, the seats) hang from that tier.

## 1. The two settings

| Railway variable | Default | Effect |
|---|---|---|
| `Entitlement__OpenAccess__Enabled` | `false` (committed in `appsettings.json`; missing or empty = off) | `true` turns the open access on. Anything but `true`, `false` or empty **stops the startup** (see § 6). |
| `Entitlement__OpenAccess__Tier` | `Scale` (missing or empty = `Scale`) | The tier every org gets **at least** while the switch is on: `Starter`, `Pro` or `Scale` (case does not matter). Any other value **stops the startup**. `Starter` changes nothing. |

Both are read from the configuration at every use, and Railway redeploys the service when a variable changes: the new value
applies when the new container is up. Set them per environment (`test` first, then `production`); they are listed in
[`deploy-checklist.md`](deploy-checklist.md) § 2.8. Where the checklist says "Railway" read "the host of the backend"
(see the note at the top of [`index.md`](index.md)).

## 2. What changes while it is on, and what does not

The **effective** tier of an org (`IEntitlementService.ResolveEffectiveTier`, the one every gate uses) becomes
`max(tier the subscription pays for, Entitlement__OpenAccess__Tier)`. It is an override **on read**: it is computed when
asked and never written anywhere.

| Changes (follows the raised tier) | Does not change |
|---|---|
| Property limit (`maxProperties` of `GET /api/orgs/me/entitlement`; creation beyond 3 is no longer refused with 403 `plan_limit_reached` on Starter). The per-tier overrides `Entitlement__Tiers__{Tier}__MaxProperties` still apply, to the raised tier | Prices (`Billing__Display__*`, the Stripe Prices `Billing__Prices__*`) and the plan catalogue (`GET /api/orgs/plans`, `GET /api/billing/plans`) |
| Custom domain (`canUseCustomDomain`, serving a verified custom domain, the periodic domain recheck) | The stored plan of the org (`Org.PlanTier`) and every Stripe field (`SubscriptionId`, `SubscriptionStatus`, `PastDueSince`, `CurrentPeriodEnd`); `GET /api/billing/subscription` keeps answering with them |
| "Realizzato con" on the public booking sites (`showPoweredBy` is true only for effective Starter) | Checkout, billing portal, webhooks, invoices, e-invoices: an org can still subscribe, change plan in the portal or cancel, and a paying org is charged as before |
| `planTier` of `GET /api/users/me` and `GET /api/orgs/me/entitlement`: they show the **effective** tier | The plan change rules (`PlanChangePolicy`): `PUT /api/orgs/me/plan` and `PATCH /api/admin/orgs/{id}/plan` compare with the tier the org **pays** for, so an upgrade without a subscription is still refused (403 / 409 `subscription_required`), a Stripe-managed plan is still `managed_by_stripe`, and nothing can store a paid tier for free |
| `openAccess` of `GET /api/orgs/me/entitlement` (new field, see § 4) | What `SyncFromSubscriptionAsync` stores after a Stripe event: it still downgrades an org whose subscription ended, from the tier the subscription pays for |
| Seats (`MaxSeats` per tier, from AM-02 on, decision D35): they follow the effective tier | |

Rules worth remembering:

- **Never worse than what the org pays for.** An org that pays Pro with `Tier=Starter` stays Pro; an org that pays Scale with
  `Tier=Pro` stays Scale. The override only raises.
- **Orgs in trouble with the payment are raised too.** An org whose subscription is past due beyond the grace period, unpaid,
  canceled or never started is served as the open tier, like any other. Stripe keeps tracking its state and the webhooks keep
  updating the stored plan; when the switch goes off the org is served again as what its subscription says.
- Only orgs that exist are raised. A request for an unknown org id keeps the Starter fallback.
- It is not a feature flag: `GET /api/public/features` does not list it and it does not depend on `UiRedesign`.

## 3. Turning it on

1. On `test` set `Entitlement__OpenAccess__Enabled=true` (and `Entitlement__OpenAccess__Tier` only if it must not be `Scale`).
   The service redeploys.
2. Check the startup log of the new deployment: a warning
   `Open access is ON (Entitlement__OpenAccess__Enabled): every org is served as Scale at least, whatever its subscription. …`.
   No such line = the switch is off. If the container exits instead, read the message (§ 6): Railway keeps the previous
   deployment.
3. Log in as the owner of an org without a subscription and open the plan page: in the browser network tab,
   `GET /api/orgs/me/entitlement` answers `"planTier": "Scale"`, `"openAccess": true`, `"canUseCustomDomain": true` and a
   `limits.maxProperties` of `2147483647` (unlimited); `GET /api/billing/subscription` still shows the stored plan
   (`"planTier": "Starter"`, `"status": "none"`).
4. Create a fourth property in that org: it works (without the switch it is 403 `plan_limit_reached`).
5. Repeat on `production` when `test` is as expected.

## 4. What the apps can show

`GET /api/orgs/me/entitlement` (and the answer of `PUT /api/orgs/me/plan` and `PATCH /api/admin/orgs/{id}/plan`) carries a new
boolean, `openAccess`: `true` when the `planTier` shown is **higher than the plan the org's subscription pays for** because of
this switch. It is `false` when the switch is off (the default) and also when it is on but changes nothing for that org (it
already has the tier or a higher one, or the configured tier is `Starter`). The apps use it to show "accesso aperto" instead of
an upgrade button. Two things for the front-end work (not done here):

- `planTier` (here and in `GET /api/users/me`) is the **effective** tier. The plan the org actually pays for, and its status,
  are in `GET /api/billing/subscription`.
- The checkout stays available. If the plans page should not sell a plan the org already has for free, that is a decision
  of the screen, based on `openAccess`.

## 5. Going back to the paid regime

1. Decide the date and tell the customers: the orgs that have no paid access lose the open tier all at once, at the redeploy.
2. See who is affected before you do it (read-only, on the Supabase SQL editor, schema `casazen_prod`). It lists the host orgs
   that are not on an active or trialing subscription (an org past due within the grace period keeps its paid tier: check
   those by hand) and either have more properties than the Starter limit (3, or `Entitlement__Tiers__Starter__MaxProperties`)
   or a custom domain:

   ```sql
   SELECT o."Id", o."Name", o."PlanTier", o."SubscriptionStatus", o."PastDueSince",
          count(p."Id") FILTER (WHERE NOT p."IsDeleted") AS properties,
          (o."PublicHostMode" = 2 AND o."CustomDomain" IS NOT NULL) AS has_custom_domain
   FROM "Orgs" o
   LEFT JOIN "Properties" p ON p."OrgId" = o."Id"
   WHERE o."OrgType" = 0 AND o."IsActive" AND o."SubscriptionStatus" NOT IN (1, 2)   -- 1 Trialing, 2 Active
   GROUP BY o."Id"
   HAVING count(p."Id") FILTER (WHERE NOT p."IsDeleted") > 3
       OR (o."PublicHostMode" = 2 AND o."CustomDomain" IS NOT NULL)
   ORDER BY properties DESC;
   ```
3. Set `Entitlement__OpenAccess__Enabled=false` (or delete the variable, or deploy without it) on `test`, check, then on
   `production`. The log line of § 3 is gone from the new deployment.
4. What the orgs without a paid plan get back, at once and without a data change:

   | Area | After the switch goes off |
   |---|---|
   | Properties | Nothing is deleted. Above the Starter limit the org keeps what it has and cannot create more (403 `plan_limit_reached`) until it is under the limit or subscribes. |
   | Custom domain | The domain is no longer served (the public site stays on its CasaZen address) and the periodic recheck skips it; the configuration is kept, so it works again as soon as the org pays Pro or Scale. |
   | "Realizzato con" | Back on the public booking site. |
   | Seats (from AM-02) | The members stay; new invitations and reactivations are refused (409 `org_seat_limit_reached`) while the org is above the Starter number of seats. |

5. Orgs that pay see no change at all.

## 6. Startup errors (the previous deployment stays)

A wrong value is refused at startup, in every environment, naming the variable (never the value):

| Message | Fix |
|---|---|
| `Entitlement__OpenAccess__Enabled must be true or false (or empty, which means false).` | Write `true` or `false` (`1`, `yes`, `on`, a typo are refused: a switch that nobody can read must not guess). |
| `Entitlement__OpenAccess__Tier must be the name of a plan (Starter, Pro, Scale) or empty, which means Scale.` | Write one of the three names, or delete the variable. Also refused while the switch is off: better found now than the day it is turned on. |

If the switch seems to do nothing: the variable name has two underscores between the parts (`Entitlement__OpenAccess__Enabled`),
the redeploy has finished, the org already has that tier or a higher one (`openAccess` is then `false`), or `Tier` is `Starter`.

## 7. Code map

| Piece | Where |
|---|---|
| Settings, parsing and the startup check | `Casazen.Core/Services/OpenAccess.cs` (`OpenAccess.Read`, `OpenAccess.GetErrors`, `OpenAccessSetting.Apply`) |
| The override | `Casazen.Infrastructure/Services/EntitlementService.cs`: `ResolveEffectiveTier` (with the override) and `ResolvePaidTier` (without it) |
| Startup validation and log | `Casazen.Web/Configuration/OpenAccessConfiguration.cs`, registered in `Program.cs` |
| Plan change rules (paid tier) | `OrgsController.UpdateMyPlan`, `AdminController.UpdateOrgPlan` → `PlanChangePolicy` |
| `openAccess` | `EntitlementResult.OpenAccess` → `EntitlementDto.OpenAccess` |
| Tests | `OpenAccessTests`, `EntitlementServiceOpenAccessTests`, `OpenAccessConfigurationTests`, `OpenAccessIntegrationTests` (`Casazen.Tests`) |

## 8. The `UiRedesign` flag (same task)

BL-01 also introduces the feature flag `UiRedesign` (`Features__UiRedesign`, off), for the gradual rollout of the new interface
(decision 01-D8). The backend only exposes it in `GET /api/public/features` (`uiRedesign`); the frontend reads it to switch the
new look on or off. See [`feature-flags.md`](feature-flags.md).
