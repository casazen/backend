# Stage 04 Code Review — PR #397

| Field | Value |
|---|---|
| PR | https://github.com/casazen/backend/pull/397 |
| Title | `fix(sdlc): close MATRIX:native-host:AC21 backend push tests` |
| Base / head | `develop` ← `cursor/casazen-sdlc-delivery-34c4` |
| Reviewer | Stage 04 `code-reviewer` (fresh context) |
| Scope | Diff: `PushNotificationServiceTests.cs`, `ac-matrix-mvp.md`, `requirements.json`, `extract-requirements.ps1` |
| Work-unit | Delivery tick 8 `MATRIX:native-host:AC21` (`SPEC:native-host-app:AC21` — Backend push tests) |

## Checklist (`.claude/sdlc/04-review/agents/code-reviewer.md`)

| Area | Result |
|---|---|
| Correctness / AC | Pass for AC21 gap-close: checkout-reminder recipient + route/type/`bookingId` tests match `PushNotificationService.SendCheckoutReminderAsync`; matrix/requirements → `pass` backed by delivery-8 gates |
| Async patterns | Pass — new tests `async Task`, no `.Result`/`.Wait()`/`async void`; service already uses `CancellationToken` |
| EF Core | Pass — InMemory fixture reuse; no schema/migration changes in this PR |
| Testing | Pass for AC21 — suite covers guest check-in recipients, service-request recipients+routing, checkout reminder recipients+routing; gate also runs `DeviceRegistrationIntegrationTests` |
| SOLID | N/A for new production types; extract-requirements change is small and focused |

## Diff summary

1. **`PushNotificationServiceTests.cs`**: Adds `SendCheckoutReminderAsync_SendsOnlyToPropertyOwnerAndPrivilegedUsers` and `SendCheckoutReminderAsync_RoutesToBookingCheckout` (mirrors existing guest/service-request patterns; asserts `/bookings/{id}/checkout`, `bookingId`, `type=checkout-reminder`).
2. **`ac-matrix-mvp.md`**: Native Host AC21 `missing-test` → `pass` with suite citation.
3. **`requirements.json`**: `SPEC:native-host-app:AC21.matrix_status` → `pass`; extract reorder/timestamp churn; other P0 statuses preserved.
4. **`extract-requirements.ps1`**: failHints now read matrix status cell; honor `pass`/`stub`/`blocked` so resolved AC21 is not clobbered back to `missing-test`.

## Design AC map (AC10–AC12 push + AC21)

| AC / req | Claim | Evidence in PR / suite | Result |
|---|---|---|---|
| Spec AC10 (`POST /api/devices`) | Device token registration | Existing `DeviceRegistrationIntegrationTests` (gate `G-device-api` exit 0, 4 passed); not modified in this diff | Covered (pre-existing) |
| Spec AC11 push types | guest check-in, service-request, checkout reminder | Checkout `type=checkout-reminder` newly asserted; guest/service-request types exercised via send path but not asserted by name | Sufficient for AC21; see 🟢 |
| Spec AC12 deep-link `bookingId` | Tap → booking | Checkout + service-request-with-booking assert `bookingId` + route; guest check-in route/bookingId not asserted | Sufficient for AC21; see 🟢 |
| Matrix / SPEC AC21 | Backend push tests | New checkout tests + existing push/device suite; `gates.json` overall=`pass` | **PASS** |

## Findings

### 🔴 Critical

_None. (0 critical)_

### 🟡 High

_None. (0 high)_

### 🟢 Medium

1. **`PushNotificationServiceTests.cs` / matrix note** — Matrix claims “guest check-in, service-request, checkout reminder + routing”, but guest check-in still has recipient filtering only (no `route` / `type` / `bookingId` asserts). Checkout routing added here closes the clearest hole; guest deep-link asserts would tighten AC12 parity. Non-blocking for this gap-close.

### ⚪ Low

1. **`requirements.json` reorder noise** — Non-AC21 rows shuffled by extract; statuses for AC15/`fail`, AC4/AC20/GJ/marketplace/`blocked`, checkout L3/`missing-test` preserved. Harmless.
2. **`CapturingExpoHandler.RequestBody`** — Last Expo POST wins when multiple devices receive the same payload; routing asserts remain valid because payloads are identical. Pre-existing harness quirk.
3. **`extract-requirements.ps1` failHints** — For non-resolved cells (`fail`/`missing-test`/etc.), script still applies hardcoded hint status rather than the matrix cell. Pre-existing; sticky `pass`/`stub`/`blocked` fix is correct for AC21.

## Verification performed

```text
/tmp/pr-397.diff + gh pr view 397 — 4 files; +85/-45
PushNotificationService.SendCheckoutReminderAsync — route /bookings/{id}/checkout, type checkout-reminder
Sessions/loop/evidence/delivery-8/gates.json — overall=pass
  G-push-unit exit 0 (7 passed)
  G-device-api exit 0 (4 passed)
  G-ac21-pass exit 0 (AC21 matrix_status=pass, cell=pass)
spec-native-host-app.md AC10–AC12 push + matrix AC21
No product/runtime code changed; no migrations
```

## Merge recommendation (code-review)

| Metric | Count |
|---|---|
| 🔴 Critical | **0** |
| 🟡 High | **0** |
| 🟢 Medium | 1 |
| ⚪ Low | 3 |

**Verdict: PASS** — Merge OK from code-review (0 open 🔴). Gap-close correctly adds checkout-reminder coverage, marks AC21 pass with green gate evidence, and stops extract from reopening the gap. Security review is out of this agent’s scope. Do not merge from this agent.
