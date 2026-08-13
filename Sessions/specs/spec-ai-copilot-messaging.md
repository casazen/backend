# Spec — AI Copilot Messaging (US-012)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Extend CasaZen's AI from **pricing-only** into a **guest-messaging copilot** that drafts —
and optionally auto-sends — guest replies inside the unified inbox. This reuses the proven
pricing-AI architecture (`PricingAdapterConfig` for per-scope config, `PricingHistory` for
confidence + decision logging, `DynamicPricingJob` for off-request execution) and applies the
same confidence-scoring + audit-logging conventions to messaging.

Today there is **only AI pricing** (`PricingAdapterConfig`, `PricingHistory`,
`DynamicPricingJob`, confidence scoring + logging) — there is **no messaging AI**. This spec
adds the messaging copilot as the first lifecycle expansion of that engine.

The feature is governed by a **hard product/financial constraint** (Financial #13): a
cheap-model default, confidence-gated frontier routing, response caching, and a per-account
token cap with overage metering, targeting **AI ≤ 10–15% of ARPU and gross margin ≥ 80%**.
These are written below as **measurable acceptance criteria**, not prose.

User story reference: **US-012** (Phase 2 — Operations AI Copilot)
Stage of entry: **Stage 01 Planning** (create the issue before design)

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a property manager, I want an AI copilot that drafts guest replies in the unified inbox —
with a clear confidence signal, guest-facing transparency, and a human-in-the-loop default —
so I can respond instantly without losing control or blowing my AI budget.

As the founder/operator, I want hard fair-use caps enforced in code (cheap-model default,
confidence-gated escalation, caching, per-account token cap + overage metering) so that AI
cost stays **≤ 10–15% of ARPU** and **gross margin ≥ 80%** at any account scale.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: New entity `AiMessagingConfig` (per `Org`, optionally overridable per `Property`;
  carries `OrgId` — RF1), mirroring `PricingAdapterConfig`:
  `{ Id, OrgId, PropertyId?, IsEnabled (default false), AutoSendEnabled (default false),
  ConfidenceThreshold (default 0.85), DefaultModelTier (default Economy),
  MonthlyTokenCap, CreatedAt, UpdatedAt }`.

- **AC2**: New entity `AiMessageLog` (mirrors `PricingHistory`, incl.
  `AiConfidence` as `Precision(5,4)`, range 0–1) logging **every AI decision**:
  `{ Id, OrgId, ConversationId, MessageId?, ModelTier, PromptTokens, CompletionTokens,
  AiConfidence, Action (enum: Drafted|AutoSent|Suppressed|Escalated), DisclosureShown (bool),
  CreatedAt }` — satisfies the AI Act "log AI decisions" gate.

- **AC3 (cheap-model default — HARD CAP)**: the **default model tier is the cheap/small model**
  (`Economy`). The frontier tier is invoked **only** on confidence-gated escalation (AC4).
  Measurable: in steady state **≥ 85% of generated drafts are served by the Economy tier**
  (assertable from `AiMessageLog.ModelTier` aggregates).

- **AC4 (confidence-gated frontier routing — HARD CAP)**: generation runs the Economy tier
  first; if `AiConfidence ≥ ConfidenceThreshold` (default **0.85**) the Economy draft is used;
  if below threshold, escalate to the **frontier** tier **exactly once** (logged as
  `Action=Escalated`); if still below threshold, **suppress auto-send** and flag for a human
  (`Action=Suppressed`). No auto-send ever occurs below threshold.

- **AC5 (caching — HARD CAP)**: a response cache keyed on **normalized intent + property
  context** serves repeat/FAQ-type prompts (check-in time, Wi-Fi, directions, parking). A
  **cache hit MUST NOT call any model and MUST NOT consume the token budget**; cache hits are
  recorded (with `PromptTokens=0, CompletionTokens=0`) and measurable as a cache-hit rate.

- **AC6 (per-account monthly token cap + overage metering — HARD CAP)**: each `Org` has a
  `MonthlyTokenCap`; token usage is metered in `AiMessageLog` per `Org` per calendar month.
  On reaching the cap, the copilot **drops to draft-only (no auto-send)** and **records overage
  tokens** for billing (handed to `spec-saas-billing`). Measurable: cap is enforced and overage
  is recorded per `Org` per month.

- **AC7 (margin guardrail — measurable)**: `GET /api/ai-messaging/usage` returns, per `Org`
  for the current month: `tokensUsed`, `tokensCap`, `estimatedCostEur`, and
  `aiCostPctOfArpu`. The documented target is **`estimatedCostEur / ARPU ≤ 0.15`** (AI ≤ 10–15%
  of ARPU) supporting **gross margin ≥ 80%**; this endpoint makes the cap auditable per account.

- **AC8 (EU AI Act transparency disclosure)**: every **AI auto-sent** guest-facing message
  includes a transparency disclosure (Italian UI string, e.g. *"Stai parlando con un
  assistente AI di {Org}"*), and the disclosure is recorded as `DisclosureShown=true` in
  `AiMessageLog`. A human-edited-and-sent draft is operator-attributed and need not be labelled
  AI, but **auto-sent messages MUST be disclosed**.

- **AC9 (human-in-the-loop default)**: `AutoSendEnabled` defaults to **false** (opt-in).
  Drafts surface in the inbox composer for operator review/edit/send. Auto-send fires **only**
  when `AutoSendEnabled = true` **AND** `AiConfidence ≥ ConfidenceThreshold` **AND** the `Org`
  is under its `MonthlyTokenCap`.

- **AC10**: New Hangfire job `AiDraftGenerationJob` generates drafts **off-request** (mirrors
  `DynamicPricingJob`: per-conversation try/catch isolation, structured logging), triggered on
  inbound-message ingestion (chained from `spec-unified-inbox`'s `InboundMessageIngestionJob`
  or an inbox event). Generation never blocks the request thread.

- **AC11**: `POST /api/inbox/conversations/{id}/ai-draft` — on-demand draft for a conversation,
  returning the suggested reply + `AiConfidence` + `ModelTier` (does not send).

- **AC12**: `GET /api/ai-messaging/config` and `PUT /api/ai-messaging/config` — read/update the
  `AiMessagingConfig`; authorization via the existing
  `RequireContext:{context}:{permission}` convention (`ai.read` / `ai.write`).

- **AC13**: AI provider is abstracted behind `IAiProvider` (tiered: `Economy` / `Frontier`) in
  `Casazen.Infrastructure/External` (mirrors the `StripeService` / `SendGridService` external
  pattern; `HttpClient` + Polly resilience like the OTA adapters). Keys come from
  configuration/secrets and **never** appear in responses or logs.

- **AC14**: Confidence + decision logging reuses the pricing convention — `AiConfidence` stored
  as `Precision(5,4)` in `[0,1]`, exactly as `PricingHistory.AiConfidence`.

- **AC15**: Migration `AddAiMessaging` creates `AiMessagingConfig`/`AiMessageLog` with `OrgId`
  from creation and rebases onto `AppDbContextModelSnapshot.cs` (RF3).

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC16**: In the inbox composer (`message-composer.tsx` from `spec-unified-inbox`), an AI
  draft panel shows the suggested reply with a **confidence indicator** and Modifica / Approva
  e invia / Scarta actions.

- **AC17**: `ai-messaging-settings-page.tsx` at `/settings/ai-messaging` — toggle enable,
  **auto-send opt-in (default OFF)**, confidence-threshold control, model-tier selector, and a
  monthly **token-usage meter** (used / cap / overage).

- **AC18**: A guest-facing **AI disclosure badge** (Italian: *"Assistito da AI"*) renders on
  any AI auto-sent message in the thread view.

- **AC19**: A usage/cost panel surfaces `tokensUsed`, `% of cap`, `estimatedCostEur`, and
  `aiCostPctOfArpu` from `GET /api/ai-messaging/usage`.

- **AC20**: `<ProtectedRoute>` on all AI settings routes; auto-send toggle defaults to OFF in
  the UI (human-in-the-loop); all end-user strings in Italian.

---


## UX / UI Quality



**Required** (Frontend ACs present). Testable bar for Stage 03.



| Criterion | Required | How to verify |

|---|---|---|

| Primary path clear | User completes happy path without guessing | L3 scripted flow below |

| Language | End-user strings Italian | L2/L3 assert Italian primary labels |

| Empty state | No blank dead-end when data length = 0 | L2 empty fixture |

| Error state | 4xx/5xx as human Italian message | L2/L3 forced error |

| Destructive / legal copy | Confirmations/disclaimers as in ACs | Assert documented phrases |



**Happy-path script:**



1. Enter the primary route for `ai-copilot-messaging`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---


## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | New entity `AiMessagingConfig` (per `Org`, optionally overridable per `Property`; | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | New entity `AiMessageLog` (mirrors `PricingHistory`, incl. | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC6 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC7 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC8 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC9 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC10 | L1 | New Hangfire job `AiDraftGenerationJob` generates drafts **off-request** (mirrors | Outcome not met; wrong status; silent no-op |
| AC11 | L1 + L2 + L3 | `POST /api/inbox/conversations/{id}/ai-draft` — on-demand draft for a conversation, | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC12 | L1 | `GET /api/ai-messaging/config` and `PUT /api/ai-messaging/config` — read/update the | Outcome not met; wrong status; silent no-op |
| AC13 | L1 | AI provider is abstracted behind `IAiProvider` (tiered: `Economy` / `Frontier`) in | Outcome not met; wrong status; silent no-op |
| AC14 | L1 | Confidence + decision logging reuses the pricing convention — `AiConfidence` stored | Outcome not met; wrong status; silent no-op |
| AC15 | L1 | Migration `AddAiMessaging` creates `AiMessagingConfig`/`AiMessageLog` with `OrgId` | Outcome not met; wrong status; silent no-op |
| AC16 | L2 + L3 | In the inbox composer (`message-composer.tsx` from `spec-unified-inbox`), an AI | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC17 | L2 + L3 | `ai-messaging-settings-page.tsx` at `/settings/ai-messaging` — toggle enable, | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC18 | L2 + L3 | A guest-facing **AI disclosure badge** (Italian: *"Assistito da AI"*) renders on | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC19 | L2 + L3 | A usage/cost panel surfaces `tokensUsed`, `% of cap`, `estimatedCostEur`, and | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC20 | L2 + L3 | `<ProtectedRoute>` on all AI settings routes; auto-send toggle defaults to OFF in | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

Rules:
- UI ACs need L2 **and** L3 outcomes (titled tests per AC).
- Non-UI ACs may be L1-only (`N/A` L2/L3 in design map).
- Visibility-only asserts are insufficient for mutations, exports, or multi-step flows.

---

## Technical Notes

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend — Files to create/modify

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `Casazen.Core/Entities/AiMessagingConfig.cs` | Create — mirror `PricingAdapterConfig.cs`; incl. `OrgId`, thresholds, caps |
| `Casazen.Core/Entities/AiMessageLog.cs` | Create — mirror `PricingHistory.cs`; `AiConfidence` `Precision(5,4)` |
| `Casazen.Core/Entities/Enums/AiModelTier.cs` | Create — `Economy/Frontier` |
| `Casazen.Core/Entities/Enums/AiMessageAction.cs` | Create — `Drafted/AutoSent/Suppressed/Escalated` |
| `Casazen.Core/Services/IAiMessagingService.cs` | Create |
| `Casazen.Infrastructure/Services/AiMessagingService.cs` | Create — routing, confidence gating, cap enforcement |
| `Casazen.Infrastructure/Services/AiResponseCache.cs` | Create — intent+context cache (cache hit = 0 tokens, AC5) |
| `Casazen.Infrastructure/External/IAiProvider.cs` | Create — tiered provider abstraction |
| `Casazen.Infrastructure/External/AiProvider.cs` | Create — `HttpClient` + Polly (mirror OTA/`StripeService`) |
| `Casazen.Core/Repositories/IAiMessagingConfigRepository.cs` | Create — mirror `IPricingAdapterConfigRepository.cs` |
| `Casazen.Core/Repositories/IAiMessageLogRepository.cs` | Create — mirror `IPricingHistoryRepository.cs` |
| `Casazen.Infrastructure/Repositories/AiMessagingConfigRepository.cs` | Create |
| `Casazen.Infrastructure/Repositories/AiMessageLogRepository.cs` | Create — monthly token aggregates per `Org` |
| `Casazen.Web/BackgroundJobs/AiDraftGenerationJob.cs` | Create — mirror `DynamicPricingJob.cs` (per-conversation isolation) |
| `Casazen.Web/Controllers/AiMessagingController.cs` | Create — mirror `PricingAdapterController.cs` (config/draft/usage) |
| `Casazen.Web/DTOs/AiMessaging/AiMessagingConfigDto.cs` | Create |
| `Casazen.Web/DTOs/AiMessaging/AiDraftResponse.cs` | Create |
| `Casazen.Web/DTOs/AiMessaging/AiUsageResponse.cs` | Create — `tokensUsed/tokensCap/estimatedCostEur/aiCostPctOfArpu` |
| `Casazen.Infrastructure/Data/AppDbContext.cs` | Modify — `DbSet`s, config, indexes (`OrgId`, month) |
| `Casazen.Infrastructure/Migrations/` | Add migration `AddAiMessaging` (`OrgId` from creation; rebase snapshot, RF3) |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | Modify — register service/provider/repos + Polly `HttpClient`; add `ai.read`/`ai.write` context policies |
| `Casazen.Web/Program.cs` | Modify — optional monthly usage-rollup recurring job in `ConfigureRecurringJobs` |

### Frontend — Files to create/modify

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `src/features/inbox/components/ai-draft-panel.tsx` | Create — suggested reply + confidence + approve/edit/discard |
| `src/features/inbox/components/ai-disclosure-badge.tsx` | Create — "Assistito da AI" badge |
| `src/features/settings/ai-messaging-settings-page.tsx` | Create — enable/auto-send/threshold/tier/usage |
| `src/features/settings/components/ai-usage-meter.tsx` | Create — tokens used / cap / overage |
| `src/queries/use-ai-messaging.ts` | Create — TanStack Query hooks |
| `src/api/ai-messaging.api.ts` | Create — AI messaging API client |
| `src/types/ai-messaging.types.ts` | Create — config/draft/usage types |
| `src/routes/index.tsx` | Modify — add `/settings/ai-messaging` under `ProtectedRoute` |

---

## Compliance

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **EU AI Act transparency disclosure (council wording)**: guest-facing *"you're talking to
  AI"* disclosure on auto-sent messages (AC8) **+ log AI decisions** (`AiMessageLog`, AC2)
  **+ DPA** (AI provider added to the subprocessor list). This is the explicit AI Act gate from
  the council draft (Legal C5 / R4).
- **Human-in-the-loop / opt-in auto-send default**: `AutoSendEnabled` defaults to **false**;
  no auto-send below confidence threshold or over token cap (AC9).
- **Hard fair-use caps (Financial #13) — as measurable AC**: cheap-model default (AC3) +
  confidence-gated frontier routing (AC4) + caching (AC5) + per-account monthly token cap with
  overage metering (AC6) → **AI ≤ 10–15% of ARPU, gross margin ≥ 80%** (auditable via AC7).
- **GDPR**: guest PII may appear in prompts — apply data minimization (send only what the reply
  needs), treat the AI provider as a **subprocessor** in the DPA, and keep **no PII in logs**.
- **Confidence logging continuity**: reuses the existing pricing-AI confidence-logging
  convention (R4: "pricing confidence already logged") for messaging decisions.

---

## Dependencies

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Requires**:
  - `spec-unified-inbox` (US-011) — `Conversation`/`Message` records + the ingestion event the
    copilot reacts to.
  - AI provider (tiered Economy/Frontier) behind `IAiProvider`.
  - Existing pricing-AI patterns — `PricingAdapterConfig`, `PricingHistory`, `DynamicPricingJob`
    (architecture + confidence/logging conventions reused).
  - `spec-tenant-boundary` — `Org`/`OrgId` + plan entitlement (RF1).
  - `spec-saas-billing` — consumes recorded **overage** for metered billing.
- **Blocks**:
  - Phase 2 exit criterion — "AI drafts replies (cheap-model default, confidence-gated frontier,
    cached, metered) keeping AI ≤ 10–15% ARPU / GM ≥ 80%".
- **Related**:
  - `WebhooksController` + `StripeWebhookJob` async pattern (off-request execution discipline).
  - Risk R6 (AI cost/quality erodes margin) — this spec's hard caps are its mitigation.

## Test expectations (process contract)



| Layer | Allowed | Forbidden as sole proof |

|---|---|---|

| L1 | xUnit unit/integration asserting AC outcomes | Compile-only |

| L2 | Playwright demo + page.route OK; titled test per AC | One smoke for all ACs; visibility-only for exports |

| L3 | Real API local/staging; titled test per UI AC | Mocking path under test; AC map without titled tests |



Design Stage 02 must produce ## AC Test Map with one row per AC. Stage 03/04 gate check-ac-depth.ps1 -RequireTests enforces titled tests + export depth.

## Regulatory / Legal Gates

- None

## Out of Scope

- See Acceptance Criteria non-goals / PLANNING freeze list

## Open Questions

- None (or list with owner/date before Stage 03)
