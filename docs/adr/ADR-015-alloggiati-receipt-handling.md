# ADR-015: Alloggiati Web Receipt Handling

**Status:** Accepted  
**Date:** 2026-10-24  
**Issue:** none (run R20261024-1925)  
**Informs:** NF-14

## Context

Agencies need automatic submission of guest data to Questura via Alloggiati Web portal, with receipt retrieval and document deletion triggers. Phase 1 uses a fake SOAP server; real integration is a pre-launch gate.

**Existing code** (from `kb/gap/gap-analysis.md`):
- `AlloggiatiWebReport` entity at `Casazen.Core/Entities/AlloggiatiWebReport.cs` with states
- No SOAP client infrastructure exists
- Hangfire for background jobs

**Repo scope:** Backend (SOAP client, fake server, receipt retrieval), Frontend (delegation consent, status display), Mobile (status enum extension)

## Decision

### AlloggiatiWebStatus Extension

Add `SentReceiptPending` state between Send success and receipt retrieved:

```csharp
public enum AlloggiatiWebStatus
{
    DaInviare = 0,
    DaInviareManualmente = 1,
    Inviato = 2,
    InviatoManualmente = 3,
    Errore = 4,
    SentReceiptPending = 5  // NEW: sent, awaiting receipt
}
```

### SOAP Client Implementation

`AlloggiatiSoapClient` using HttpClient + XDocument for SOAP 1.1:

Operations:
1. **GenerateToken:** Authenticate with WSKEY, returns session token (cache 1 hour)
2. **Test:** Validate rows before send, returns per-row errors
3. **Send:** Submit validated rows, returns confirmation
4. **Ricevuta:** Retrieve receipts for date range

### Credential Storage

```csharp
public class OrgQuesturaCredentials : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string Username { get; set; }
    public string EncryptedPassword { get; set; }  // OtaSecretsPurpose
    public string EncryptedWsKey { get; set; }
    public QuesturaProfile Profile { get; set; }  // SingleStructure | GestioneAppartamenti
}
```

### Receipt Storage

```csharp
public class AlloggiatiReceipt : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid CredentialId { get; set; }
    public DateTime ReceiptDate { get; set; }
    public string ReceiptPdfUrl { get; set; }
    public string ConfirmationNumbersJson { get; set; }  // JSON array for multi-property
    public DateTime RetrievedAt { get; set; }
}
```

Retention: 5 years per legal requirement.

### Send Flow

1. Auto-submit job (Hangfire) on arrival day per delegation consent
2. Call Test operation first; proceed only if all rows valid
3. On success: set status `SentReceiptPending`
4. On failure: set status `Errore` with error details

### Receipt Retrieval Job

`AlloggiatiReceiptJob` (Hangfire daily):
1. For each credential with pending receipts
2. Call Ricevuta for last 30 days
3. Store receipt PDF
4. Trigger document deletion (ADR-013 integration)
5. Update status to `Inviato`

### Delegation Consent

Org flag `AlloggiatiDelegationConsent` checked before auto-submit:
- No consent = 403 `alloggiati_delegation_required`
- Consent checkbox in org settings

### Token Caching

Per Q-A25 answer, cache token per credential:
- 1-hour expiration
- Refresh on expiry or 401 error

### Fake SOAP Server (Phase 1)

Per AC-NF14-17, phase 1 uses fake server:
- Matches manual examples pp. 7-18
- Returns test ConfirmationNumbers
- Never calls real Questura API
- UI shows "Simulated send" indicator

### Multi-Month Stay

Per Q-002 answer, create one report on arrival day only.

### Rejected Alternatives

- **WCF client:** Complex configuration, HttpClient + XDocument simpler
- **Real service in phase 1:** Risk of incorrect submissions; fake server safer

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-015-R1 | P0 | AlloggiatiWebStatus enum MUST add `SentReceiptPending` (between Send success and receipt retrieved) |
| ADR-015-R2 | P0 | AlloggiatiReceipt table (OrgId, CredentialId, Date, ReceiptPdfUrl, ConfirmationNumbers JSON array, RetrievedAt, ITenantOwned) with 5-year retention |
| ADR-015-R3 | P0 | OrgQuesturaCredentials table (OrgId, Username, EncryptedPassword, EncryptedWsKey, Profile enum SingleStructure|GestioneAppartamenti, ITenantOwned) |
| ADR-015-R4 | P0 | AlloggiatiSoapClient MUST use HttpClient + XDocument, SOAP 1.1, operations Test/Send/Ricevuta |
| ADR-015-R5 | P0 | Token from GenerateToken MUST be cached per credential with 1-hour expiration (refresh on 401) |
| ADR-015-R6 | P0 | Send operation MUST call Test first, proceed only if all rows valid, set status SentReceiptPending on success |
| ADR-015-R7 | P0 | Ricevuta daily job (Hangfire) MUST retrieve last 30 days receipts per credential, store PDF, trigger document deletion (ADR-013) |
| ADR-015-R8 | P0 | Fake SOAP server (phase 1) MUST match manual examples pp. 7-18, return test ConfirmationNumbers, never call real Questura API |
| ADR-015-R9 | P0 | Multi-month stay MUST create one report on arrival day per Q-002 answer |
| ADR-015-R10 | P0 | Delegation consent (Org.AlloggiatiDelegationConsent bool) checked before auto-submit, else 403 `alloggiati_delegation_required` |
| ADR-015-R11 | P0 | Questura credentials (WSKEY, password) MUST be encrypted with OtaSecretsPurpose, never in logs |
| ADR-015-R12 | P0 | Mobile AlloggiatiWebStatus enum MUST include `SentReceiptPending` state |
| ADR-015-R13 | P0 | Mobile ALLOGGIATI_TONES map MUST map `SentReceiptPending` to 'info' tone |
| ADR-015-R14 | P1 | Fake SOAP server MUST be labeled "Simulated send" in UI (frontend report row, mobile status badge) |

## Consequences

**Follow-on work:**
- Backend: SOAP client (Test/Send/Ricevuta), fake SOAP server, state machine extension, receipt retrieval job, delegation consent
- Frontend: Delegation consent checkbox, apartment registry portal, auto-send indicator + new status display, receipt archive + missing receipt alert
- Mobile: AlloggiatiWebStatus enum extension, notification routes for errors/receipts
- Tests: SOAP test vectors matching manual examples

**Pre-launch gate:**
- First supervised send on real structure (manual verification)
- Verify fake server matches real behavior

**Risks:**
- Repeated send behavior undocumented (stay "to be verified", no auto-retry)
- Token expiration handling (cache + refresh)
- ConfirmationNumber format parsing

**Feature flag:** `automatic_alloggiati` default OFF
