# ADR-016: Compliance File Formats

**Status:** Accepted (amended 2026-11-03)  
**Date:** 2026-10-24  
**Issue:** none (run R20261024-1925)  
**Informs:** NF-15, NF-44, NF-47

## Context

Different territories require compliance file submissions in specific formats: Ticino XML for Swiss canton Ticino, OTR CSV for Swiss overnight tax reporting, and municipality-specific formats. Format verification before code is mandatory per existing runbook pattern.

**Existing code** (from `kb/gap/gap-analysis.md`):
- Ross1000 runbook at `docs/runbooks/ross1000.md` requires official format manual before code
- No Ticino or OTR compliance code exists
- Municipality configuration pattern does not exist

**Repo scope:** Backend (XML/CSV builders, municipality config, reminder scheduler), Frontend (file download, receipt upload)

## Decision

### Format Verification Gate

Per Q-A27 answer and ross1000.md pattern: **no code without verified format**.

Official format manuals MUST be saved in repo with date and link BEFORE implementing builders.

### Ticino XML Notification

For Swiss canton Ticino (Property.CantonCode == "TI"):

```csharp
public class TicinoNotificationXmlBuilder
{
    public string Build(Property property, DateTime date, IEnumerable<Guest> guests);
}
```

**PRECONDITION:** Official manual from notifiche.ti.ch must be in repo at `.claude/context/regulations/ticino-notifica.md` before implementing.

### OTR CSV (Swiss Overnight Tax)

```csharp
public class OtrCsvBuilder
{
    public string Build(Property property, DateRange period);
}
```

Columns: property ID, period, overnight count, guest count per category.

### Municipality Configuration

```csharp
public class MunicipalityComplianceConfig : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid PropertyId { get; set; }
    public string Municipality { get; set; }
    public ComplianceFormat Format { get; set; }  // TicinoXml | OtrCsv | StayTour | None
    public string SubmissionLink { get; set; }
    public int DeadlineDay { get; set; }  // Day of month
    public bool Reminder24h { get; set; }
}

public enum ComplianceFormat { None, TicinoXml, OtrCsv, StayTour }
```

### Reminder Job

`ComplianceReminderJob` (Hangfire daily):
- Check deadlines for all properties
- Send in-app + email notification 24h before
- Ticino properties with 24h reminder enabled

### Receipt Storage

```csharp
public class ComplianceFileReceipt : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid PropertyId { get; set; }
    public string Municipality { get; set; }
    public ComplianceFormat FileType { get; set; }
    public DateTime GeneratedAt { get; set; }
    public DateTime? UploadedAt { get; set; }
    public string ReceiptRef { get; set; }
}
```

### Ross1000 Deferred

Per Q-A29 answer: Ross1000 MUST NOT be implemented until official "Importa file da gestionale" format obtained. Show data only, no file download.

### Rejected Alternatives

- **Web service integration:** ISTAT, feratel, pagoPA out of phase 1
- **Dynamic format config:** Too complex; code per verified format is safer

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-016-R1 | P0 | PRECONDITION: Ticino official format manual MUST be in `.claude/context/regulations/ticino-notifica.md` before TicinoNotificationXmlBuilder code (gate) |
| ADR-016-R2 | P0 | TicinoNotificationXmlBuilder MUST generate XML per verified official format (daily notification, guest data) |
| ADR-016-R3 | P0 | OtrCsvBuilder MUST generate CSV (overnight stays for period) per Swiss OTR format |
| ADR-016-R4 | P0 | MunicipalityComplianceConfig table (Municipality, Format enum, Link, DeadlineDay, Reminder24h, ITenantOwned) |
| ADR-016-R5 | P0 | ComplianceFileReceipt table (OrgId, PropertyId, Municipality, FileType, GeneratedAt, UploadedAt, ReceiptRef, ITenantOwned) |
| ADR-016-R6 | P0 | ComplianceReminderJob (Hangfire daily) MUST check deadlines, send in-app + email notification 24h before (Ticino) |
| ADR-016-R7 | P0 | Ross1000 MUST NOT be implemented until official format manual obtained per ross1000.md step 1 |
| ADR-016-R8 | P0 | GET /api/properties/{id}/compliance/ticino-notification MUST return 501 `format_not_verified` until manual in repo |
| ADR-016-R9 | P1 | Official manual for each format MUST be saved in repo `docs/compliance/` with date and link |

## Consequences

**Follow-on work:**
- Backend: Ticino XML builder (AFTER manual obtained), OTR CSV builder, municipality config, reminder scheduler, receipt storage
- Frontend: Compliance file download UI, municipality data export, reminder display, receipt upload UI
- Tests: XML/CSV format proof tests with test vectors from manuals

**Preconditions:**
- Obtain Ticino official manual from notifiche.ti.ch, save in repo
- Ross1000 format from Italian authority (deferred)

**Risks:**
- Ticino manual source unknown (blocks Ticino XML builder)
- Ross1000 format timeline unknown (deferred)

**CI cost:** +5s for format validation tests

## Amendment 2026-11-03 (run R20261103-1506)

**Why:** NF-44 (regional ISTAT per ADR-030) and NF-47 (unified compliance archive) require extending file formats to integrate with regional configuration and Octorate.

**Decision delta:**

### Integration with Regional Config

Per ADR-030, `MunicipalityComplianceConfig` replaced by `RegionalIstatConfig` for Italian regions:

```csharp
// For Italian regions, use RegionalIstatConfig (ADR-030)
// MunicipalityComplianceConfig remains for Swiss municipalities
public class MunicipalityComplianceConfig : ITenantOwned
{
    // ... existing fields ...
    public string CountryCode { get; set; }  // CH for Swiss only
}
```

Swiss municipalities use `MunicipalityComplianceConfig`. Italian regions use `RegionalIstatConfig` (ADR-030).

### Octorate Format Support

When Octorate supports regional ISTAT (per Q-003 regional matrix):

```csharp
public enum ComplianceFormat 
{ 
    None, 
    TicinoXml, 
    OtrCsv, 
    StayTour,
    OctorateManaged  // NEW: Octorate generates file
}
```

If `RegionalIstatConfig.OctorateSupported = true`, CasaZen does not generate file; Octorate handles submission.

### Unified Compliance Archive

All compliance files and receipts visible in one archive:
- Alloggiati receipts (ADR-015)
- ISTAT proofs (ADR-030)
- Municipality compliance files (this ADR)

| ID | Priority | Requirement |
|---|---|---|
| ADR-016-R10 | P0 | `MunicipalityComplianceConfig.CountryCode` MUST be added; Swiss municipalities use this table, Italian regions use `RegionalIstatConfig` (ADR-030) |
| ADR-016-R11 | P0 | `ComplianceFormat` enum MUST add `OctorateManaged` for regions where Octorate handles ISTAT |
| ADR-016-R12 | P0 | Compliance archive endpoint `GET /api/compliance/archive` MUST return unified list: Alloggiati receipts, ISTAT proofs, municipality files |
| ADR-016-R13 | P1 | If `OctorateSupported = true` for region, file download returns 501 `octorate_handles_submission` with instructions |

**Replaces:** None (new requirements extend existing set).

**Consequences delta:**
- Backend: CountryCode field, OctorateManaged format, unified archive query
- Frontend: Archive shows all compliance file types with source indicator
- Octorate ISTAT handling conditional on Q-003 regional matrix verification
