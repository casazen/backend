# ADR-030: Regional ISTAT Integration

**Status:** Accepted  
**Date:** 2026-11-03  
**Issue:** none (run R20261103-1506)  
**Informs:** NF-44 (ISTAT regions), NF-49 (proof archive)

## Context

Italian regions and Swiss cantons have different ISTAT reporting requirements and systems. CasaZen must support regional variations with monthly reminders and proof upload for compliance archive.

**Existing code:**
- No regional ISTAT configuration exists
- `ComplianceReminderJob` pattern from ADR-016
- Proof archive pattern from ADR-015

**Repo scope:** Backend (regional config, reminder job, proof storage), Frontend (region table, proof upload), Mobile (compliance status display)

## Decision

### Regional Configuration Entity

```csharp
public class RegionalIstatConfig
{
    public string RegionCode { get; set; }              // IT-LZ, CH-TI, etc.
    public string RegionName { get; set; }              // Lazio, Ticino
    public string SystemName { get; set; }              // StayTour, Notifiche TI
    public IstatSupportStatus OctorateSupported { get; set; }
    public ComplianceFormat FileFormat { get; set; }
    public int DeadlineDay { get; set; }                // Day of month
    public string PortalUrl { get; set; }
    public bool Enabled { get; set; }
}

public enum IstatSupportStatus
{
    Supported,      // Octorate can send
    NotSupported,   // Manual only
    ToVerify        // Unknown, needs testing
}
```

Seeded with known regions; admin-only write endpoint for coverage updates.

### Monthly Reminder Job

Per Q-A23: fixed 25th of month for all regions.

```csharp
public class IstatMonthlyReminderJob
{
    public async Task ExecuteAsync(CancellationToken ct)
    {
        var today = _romeCalendar.Today();
        if (today.Day != 25)
            return;
            
        var enabledRegions = await _db.RegionalIstatConfigs
            .Where(r => r.Enabled)
            .Select(r => r.RegionCode)
            .ToArrayAsync(ct);
            
        var properties = await _db.Properties
            .Where(p => enabledRegions.Contains(p.RegionCode))
            .Where(p => p.ComplianceSender.IstatSender != ComplianceSenderType.ChannelManager)
            .ToArrayAsync(ct);
            
        foreach (var property in properties)
        {
            _db.Activities.Add(new Activity
            {
                OrgId = property.OrgId,
                PropertyId = property.Id,
                Action = ActivityAction.UploadIstatProof,
                Urgency = ActivityUrgency.Normal,
                DueAt = new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)),
                CreatedAt = DateTime.UtcNow
            });
        }
        
        await _db.SaveChangesAsync(ct);
    }
}
```

Hangfire daily at 00:00 Europe/Rome (via `RomeCalendar`).

### Proof Upload

Per Q-A24: file upload primary, text entry fallback.

```csharp
public class IstatProof : ITenantOwned
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid PropertyId { get; set; }
    public string RegionCode { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public string? FileUrl { get; set; }                // Encrypted in storage
    public string? ConfirmationNumber { get; set; }     // Text fallback
    public DateTime UploadedAt { get; set; }
    public Guid UploadedByUserId { get; set; }
}
```

File retention: 5 years per CHK-08. Files encrypted with `GuestDocumentPurpose` (same as Alloggiati receipts per ADR-015).

### Region Coverage Matrix

Initial seed data:

| Region | System | Octorate Support | Format |
|--------|--------|------------------|--------|
| CH-TI | Notifiche TI | NotSupported | TicinoXml |
| CH-* | OTR | Supported | OtrCsv |
| IT-LZ | StayTour | ToVerify | StayTour |
| IT-LM | Ross1000 | NotSupported | None |
| (others) | Regional | ToVerify | None |

### Rejected Alternatives

- **Per-region deadline days:** Some regions use different days; fixed 25th simplifies UX (can defer with "I'll do it later")
- **Web service integration:** feratel, StayTour out of phase 1; file download + manual upload
- **Dynamic format discovery:** Too complex; code per verified format is safer

## Requirements

| ID | Priority | Requirement |
|---|---|---|
| ADR-030-R1 | P0 | `RegionalIstatConfig` table (RegionCode, RegionName, SystemName, OctorateSupported enum, FileFormat enum, DeadlineDay, PortalUrl, Enabled) seeded with known regions |
| ADR-030-R2 | P0 | `IstatProof` table (OrgId, PropertyId, RegionCode, Year, Month, FileUrl nullable, ConfirmationNumber nullable, UploadedAt, UploadedByUserId, ITenantOwned) with 5-year retention |
| ADR-030-R3 | P0 | ISTAT proof file MUST be encrypted before storage with `GuestDocumentPurpose`; auto-delete after 5 years via `GdprDataRetentionJob` |
| ADR-030-R4 | P0 | `IstatMonthlyReminderJob` (Hangfire daily at 00:00 Europe/Rome) MUST create activities on 25th for properties in enabled regions with sender != ChannelManager |
| ADR-030-R5 | P0 | Proof upload `POST /api/properties/{id}/istat/proof` MUST accept file (PDF/image) OR text confirmation number; store in archive |
| ADR-030-R6 | P0 | ISTAT region table MUST display: Region, System, Status (Supported/NotSupported/ToVerify), Action button (Activate/Not-available/Verify-first) |
| ADR-030-R7 | P0 | Admin-only endpoint `PUT /api/admin/istat/regions` behind `CasazenPolicies.PlatformAdmin` for coverage updates |
| ADR-030-R8 | P1 | Monthly reminder creates activity with deadline = last day of month |
| ADR-030-R9 | P1 | Proof upload step MUST show "Upload email/receipt" as primary option, "Enter confirmation number" as fallback |

## Consequences

**Follow-on work:**
- Backend: `RegionalIstatConfig` seed, `IstatProof` entity, reminder job, proof upload endpoint
- Frontend: Region table UI, proof upload dialog, reminder display
- Mobile: Compliance status includes ISTAT proof status (read-only)
- Tests: Reminder job fires on 25th proof test

**Risks:**
- Regional coverage incomplete: only TI and OTR fully supported in wave 1; others manual
- Fixed 25th may not match all regional deadlines; users can defer

**CI cost:** +5s for reminder job proof test
