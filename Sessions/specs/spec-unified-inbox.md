# Spec — Unified Inbox (US-011)

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

## Overview

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

Introduce a **unified inbox** that aggregates guest communication from every OTA channel
and from direct bookings into a single, threaded, routable workspace. This closes the
booking-window-compression gap (F4: 27% of bookings happen within 0–7 days), where fast
guest response is a direct conversion lever.

The feature adds **three new entities** — `Conversation`, `Message`, `Thread` — and an
**asynchronous Hangfire `InboundMessageIngestionJob`** that normalizes inbound messages
from heterogeneous sources and routes them into conversations. No `Conversation`/`Message`/
`Thread` entity exists today; this is a greenfield subsystem layered on the existing OTA
adapter and webhook infrastructure.

User story reference: **US-011** (Phase 2 — Operations AI Copilot)
Stage of entry: **Stage 01 Planning** (create the issue before design)

> **Key assumption (stated explicitly):** *per-adapter inbound-message support varies.* OTA
> messaging APIs differ across channels; some channels expose no native messaging API and
> must fall back to **email ingestion**. Ingestion **MUST be off-request (Hangfire)**,
> consistent with the existing 3-second webhook rule and the `StripeWebhookJob` /
> `OtaSyncJob` async pattern — the webhook endpoint only verifies + enqueues, never does
> heavy work on the request thread.

---

## User Story

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

As a property manager handling guests across multiple OTAs **and** direct bookings, I want
every guest message — Airbnb, Booking.com, Expedia, Vrbo, TripAdvisor, Agoda, direct, and
email-fallback channels — aggregated into one threaded inbox with status and assignment
routing, so that my team can respond quickly and never lose a message across channels.

As the system, I want to ingest inbound messages **asynchronously** (off-request), normalize
per-channel payloads, deduplicate, and attach each message to the correct conversation and
`Org`, so that the inbox stays consistent regardless of which channel a message arrives on.

---

## Acceptance Criteria

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

### Backend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC1**: New entity `Conversation` (carries `OrgId` from creation — RF1) with at least:
  `{ Id, OrgId (FK), PropertyId? (FK), GuestId? (FK), BookingId? (FK), Channel (enum),
  Status (enum: Open|Pending|Snoozed|Closed), Subject, AssignedUserId?, LastMessageAt,
  CreatedAt, UpdatedAt }`.

- **AC2**: New entity `Message` (carries `OrgId`) with at least:
  `{ Id, OrgId, ConversationId (FK), ThreadId? (FK), Direction (enum: Inbound|Outbound),
  SenderType (enum: Guest|Operator|System|Ai), Body, ExternalMessageId, Channel, SentAt,
  ReadAt?, CreatedAt }`.

- **AC3**: New entity `Thread` (carries `OrgId`) grouping messages by external provider thread:
  `{ Id, OrgId, ConversationId (FK), ExternalThreadId, Channel, CreatedAt }`.

- **AC4**: `Channel` enum covers `Direct, Airbnb, BookingCom, Expedia, Vrbo, TripAdvisor,
  Agoda, Email` (mirrors `BookingSource` so direct + 6 OTA channels + email fallback are
  representable).

- **AC5**: `IChannelAdapter` is extended with an **inbound-messaging capability contract**:
  `bool SupportsInboundMessaging { get; }` and
  `Task<List<OtaMessageModel>> GetMessagesAsync(string externalPropertyId, DateTime since)`.
  Adapters whose OTA API has **no** messaging endpoint return `SupportsInboundMessaging = false`
  and are handled via the **email-fallback** path (per-adapter assumption made explicit).

- **AC6**: New Hangfire job `InboundMessageIngestionJob` that:
  - is **enqueued off-request** by `WebhooksController` (≤3s ack, mirroring `StripeWebhookJob`),
  - normalizes the inbound payload to `Message`/`Thread`,
  - **deduplicates** on `(Channel, ExternalMessageId)`,
  - resolves or **creates** the target `Conversation` (matched by `ExternalThreadId` /
    `BookingId` / `GuestId`), and
  - applies default **routing** (assign to property owner/manager of the `Org`).

- **AC7**: `WebhooksController` gains an inbound-message webhook
  `POST /webhooks/ota/{platform}/messages` that **verifies the provider signature**, returns
  `200` within the 3-second window, and enqueues `InboundMessageIngestionJob` — **no parsing
  or persistence on the request thread** (regression guard against the 3-second rule).

- **AC8**: For channels without webhooks/messaging APIs, `InboundMessageIngestionJob` is also
  registered as a **recurring poll** (email-fallback + poll channels) via
  `ConfigureRecurringJobs` in `Program.cs` (same pattern as `OtaSyncJob`/`BookingPullJob`).

- **AC9**: `GET /api/inbox/conversations` — paged list, filterable by `status`, `channel`,
  `propertyId`, `assignedUserId`, with `unreadCount`; **scoped to the caller's `OrgId`**.

- **AC10**: `GET /api/inbox/conversations/{id}` — conversation detail with ordered messages;
  cross-`Org` access returns **403** (tenant isolation).

- **AC11**: `POST /api/inbox/conversations/{id}/messages` — send an outbound reply. Routes via
  `IChannelFactory.GetAdapter(platform)` when the channel supports outbound messaging,
  otherwise via `SendGridService` (email fallback). Persists an `Outbound` `Message`.

- **AC12**: `PUT /api/inbox/conversations/{id}/status` — transition `Open|Pending|Snoozed|Closed`;
  `POST /api/inbox/conversations/{id}/assign` — assign/unassign an `AssignedUserId`.

- **AC13**: Idempotency — a unique index on `(Channel, ExternalMessageId)` guarantees a
  re-delivered webhook does not create a duplicate `Message` (job is safe to retry under
  Hangfire's at-least-once semantics).

- **AC14**: Authorization — inbox endpoints require the `short-rent` context permission
  (`inbox.read` / `inbox.write`), enforced through the existing
  `RequireContext:{context}:{permission}` policy convention.

- **AC15 (Regression)**: Inbound message DTOs never expose OTA `apiKey`/`apiSecret`
  (case-insensitive check on the serialized body), consistent with the existing OTA
  no-secret-leak rule.

### Frontend

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **AC16**: `inbox-page.tsx` at route `/inbox` — multi-pane layout: conversation list,
  thread view, and a context panel (guest/property/booking summary).

- **AC17**: Conversation list shows per-row **channel badge** (Airbnb/Booking.com/Direct/Email
  icons), unread indicator, last-message preview, status chip; supports status/channel filters
  and search.

- **AC18**: Thread view renders inbound/outbound message bubbles with channel indicator and a
  reply composer; route `/inbox/:conversationId`.

- **AC19**: Status + assignment controls (Apri/In attesa/Posticipa/Chiudi; Assegna a…), with
  optimistic update via TanStack Query.

- **AC20**: `<ProtectedRoute>` wraps `/inbox/*`; the inbox is only visible to users with the
  `short-rent` inbox permission (seat-scoped).

- **AC21**: All end-user strings in Italian (e.g. "Posta in arrivo", "Rispondi", "Assegna",
  "Chiudi conversazione", "Nessun messaggio"); empty/loading/error states included.

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



1. Enter the primary route for `unified-inbox`

2. Complete the main user action defined in Acceptance Criteria

3. Done when the Verifiable Outcome for the primary AC holds

---

## Verifiable Outcomes

**Required.** One row per AC. Stage 03 L1/L2/L3 must assert these outcomes - not only that a page loads.

| AC | Layer (min) | Observable pass condition | Fail examples (must catch) |
|---|---|---|---|
| AC1 | L1 | New entity `Conversation` (carries `OrgId` from creation — RF1) with at least: | Outcome not met; wrong status; silent no-op |
| AC2 | L1 | New entity `Message` (carries `OrgId`) with at least: | Outcome not met; wrong status; silent no-op |
| AC3 | L1 | New entity `Thread` (carries `OrgId`) grouping messages by external provider thread: | Outcome not met; wrong status; silent no-op |
| AC4 | L1 | `Channel` enum covers `Direct, Airbnb, BookingCom, Expedia, Vrbo, TripAdvisor, | Outcome not met; wrong status; silent no-op |
| AC5 | L1 | `IChannelAdapter` is extended with an **inbound-messaging capability contract**: | Outcome not met; wrong status; silent no-op |
| AC6 | L1 | New Hangfire job `InboundMessageIngestionJob` that: | Outcome not met; wrong status; silent no-op |
| AC7 | L1 | `WebhooksController` gains an inbound-message webhook | Outcome not met; wrong status; silent no-op |
| AC8 | L1 | For channels without webhooks/messaging APIs, `InboundMessageIngestionJob` is also | Outcome not met; wrong status; silent no-op |
| AC9 | L1 + L2 + L3 | `GET /api/inbox/conversations` — paged list, filterable by `status`, `channel`, | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC10 | L1 + L2 + L3 | `GET /api/inbox/conversations/{id}` — conversation detail with ordered messages; | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC11 | L1 + L2 + L3 | `POST /api/inbox/conversations/{id}/messages` — send an outbound reply. Routes via | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC12 | L1 + L2 + L3 | `PUT /api/inbox/conversations/{id}/status` — transition `Open/Pending/Snoozed/Closed`; | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC13 | L1 | Idempotency — a unique index on `(Channel, ExternalMessageId)` guarantees a | Outcome not met; wrong status; silent no-op |
| AC14 | L1 + L2 + L3 | Authorization — inbox endpoints require the `short-rent` context permission | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC15 | L1 | See Acceptance Criteria. | Outcome not met; wrong status; silent no-op |
| AC16 | L2 + L3 | `inbox-page.tsx` at route `/inbox` — multi-pane layout: conversation list, | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC17 | L2 + L3 | Conversation list shows per-row **channel badge** (Airbnb/Booking.com/Direct/Email | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC18 | L2 + L3 | Thread view renders inbound/outbound message bubbles with channel indicator and a | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC19 | L2 + L3 | Status + assignment controls (Apri/In attesa/Posticipa/Chiudi; Assegna a…), with | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC20 | L2 + L3 | `<ProtectedRoute>` wraps `/inbox/*`; the inbox is only visible to users with the | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |
| AC21 | L2 + L3 | All end-user strings in Italian (e.g. "Posta in arrivo", "Rispondi", "Assegna", | Missing Italian CTA; blank empty state; flow dead-end; visibility-only |

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
| `Casazen.Core/Entities/Conversation.cs` | Create — entity incl. `OrgId`, `Channel`, `Status`, FKs |
| `Casazen.Core/Entities/Message.cs` | Create — entity incl. `OrgId`, `Direction`, `SenderType`, `ExternalMessageId` |
| `Casazen.Core/Entities/Thread.cs` | Create — entity incl. `OrgId`, `ExternalThreadId` |
| `Casazen.Core/Entities/Enums/ConversationChannel.cs` | Create — mirrors `BookingSource` + `Email` |
| `Casazen.Core/Entities/Enums/ConversationStatus.cs` | Create — `Open/Pending/Snoozed/Closed` |
| `Casazen.Core/Entities/Enums/MessageDirection.cs` | Create — `Inbound/Outbound` |
| `Casazen.Core/Entities/Enums/MessageSenderType.cs` | Create — `Guest/Operator/System/Ai` |
| `Casazen.Core/Repositories/IConversationRepository.cs` | Create |
| `Casazen.Core/Repositories/IMessageRepository.cs` | Create |
| `Casazen.Infrastructure/Repositories/ConversationRepository.cs` | Create — EF Core, `OrgId`-filtered |
| `Casazen.Infrastructure/Repositories/MessageRepository.cs` | Create |
| `Casazen.Core/Services/IInboxService.cs` | Create |
| `Casazen.Infrastructure/Services/InboxService.cs` | Create — routing, dedup, send dispatch |
| `Casazen.Infrastructure/OTA/IChannelAdapter.cs` | Modify — add `SupportsInboundMessaging` + `GetMessagesAsync` + `OtaMessageModel` |
| `Casazen.Infrastructure/OTA/AirbnbAdapter.cs` | Modify — implement messaging where API supports |
| `Casazen.Infrastructure/OTA/BookingComAdapter.cs` | Modify — implement or mark unsupported (email fallback) |
| `Casazen.Infrastructure/OTA/ExpediaAdapter.cs` | Modify — implement or mark unsupported |
| `Casazen.Infrastructure/OTA/VrboAdapter.cs` | Modify — implement or mark unsupported |
| `Casazen.Infrastructure/OTA/TripAdvisorAdapter.cs` | Modify — implement or mark unsupported |
| `Casazen.Infrastructure/OTA/AgodaAdapter.cs` | Modify — implement or mark unsupported |
| `Casazen.Web/BackgroundJobs/InboundMessageIngestionJob.cs` | Create — mirror `OtaSyncJob` (per-source error isolation) |
| `Casazen.Web/Controllers/InboxController.cs` | Create — conversations/messages/status/assign endpoints |
| `Casazen.Web/Controllers/WebhooksController.cs` | Modify — add `POST /webhooks/ota/{platform}/messages` (verify + enqueue only) |
| `Casazen.Web/DTOs/Inbox/ConversationDto.cs` | Create |
| `Casazen.Web/DTOs/Inbox/MessageDto.cs` | Create |
| `Casazen.Web/DTOs/Inbox/SendMessageRequest.cs` | Create |
| `Casazen.Infrastructure/External/SendGridService.cs` | Modify — email-fallback inbound parse + outbound send |
| `Casazen.Infrastructure/Data/AppDbContext.cs` | Modify — `DbSet`s, relationships, indexes incl. unique `(Channel, ExternalMessageId)` + `OrgId` |
| `Casazen.Infrastructure/Migrations/` | Add migration `AddUnifiedInbox` (tables carry `OrgId` from creation; rebase onto `AppDbContextModelSnapshot.cs`, RF3) |
| `Casazen.Web/Program.cs` | Modify — register recurring `InboundMessageIngestionJob` poll in `ConfigureRecurringJobs` (email/poll channels) |
| `Casazen.Web/Extensions/ServiceCollectionExtensions.cs` | Modify — register `IInboxService` + repos; add `inbox.read`/`inbox.write` under `short-rent` in `RegisterContextPolicies` |

### Frontend — Files to create/modify

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

| File | Action |
|---|---|
| `src/features/inbox/inbox-page.tsx` | Create — multi-pane inbox shell |
| `src/features/inbox/components/conversation-list.tsx` | Create |
| `src/features/inbox/components/conversation-thread.tsx` | Create |
| `src/features/inbox/components/message-composer.tsx` | Create |
| `src/features/inbox/components/channel-badge.tsx` | Create |
| `src/queries/use-inbox.ts` | Create — TanStack Query hooks |
| `src/api/inbox.api.ts` | Create — inbox API client |
| `src/types/inbox.types.ts` | Create — `ConversationDto`, `MessageDto`, enums |
| `src/routes/index.tsx` | Modify — add `/inbox` + `/inbox/:conversationId` under `ProtectedRoute` |

---

## Compliance

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Per-adapter inbound assumption (explicit gate)**: OTA messaging APIs vary; channels with
  no native messaging API use **email-only fallback**. Ingestion is **strictly off-request
  (Hangfire)**, mirroring the 3-second webhook rule and the `StripeWebhookJob` pattern — the
  webhook only verifies the signature and enqueues (AC7).
- **GDPR (guest PII in messages)**: message bodies and threads contain guest PII. Lawful basis
  = booking/contract; data minimization in DTOs; **no PII in logs**; guest erasure (Art. 17)
  must cascade — when a `Guest` erasure is requested, linked `Message` bodies are anonymized.
- **Data retention**: `Conversation`/`Message` inherit a retention policy (7-year default,
  consistent with `Guest.DataRetentionUntil`) and are swept by the existing
  `GdprDataRetentionJob`.
- **Tenant isolation (RF1)**: `Conversation`, `Message`, and `Thread` carry `OrgId` from
  creation and honor plan entitlement; every query is `OrgId`-scoped (cross-`Org` = 403).
- **OTA secrets**: inbound/outbound DTOs never include `apiKey`/`apiSecret` (AC15).

---

## Dependencies

> Template contract: `Sessions/specs/_TEMPLATE.md`. Validated by Stage 02 G9b (`check-ac-depth.ps1 -SpecPath`).

- **Requires**:
  - OTA adapters — `IChannelAdapter` + `ChannelFactory` (extended with messaging capability).
  - `spec-direct-checkout` — direct guests (`Booking`/`Guest`) are a first-class inbox channel.
  - `spec-tenant-boundary` — `Org`/`OrgId` + plan entitlement (RF1 invariant).
  - Hangfire (existing) + `SendGridService` (email fallback).
- **Blocks**:
  - `spec-ai-copilot-messaging` (US-012) — the AI copilot drafts/sends replies into these
    `Conversation`/`Message` records and consumes the ingestion event.
- **Related**:
  - Existing `WebhooksController` + `StripeWebhookJob`/`OtaSyncJob` async pattern (3-second rule).
  - `GdprDataRetentionJob` (retention/erasure sweep).

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
