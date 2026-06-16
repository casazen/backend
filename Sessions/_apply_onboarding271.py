#!/usr/bin/env python3
"""Apply Issue #271 PLG onboarding backend files atomically."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

FILES = {
"Casazen.Core/Entities/Enums/ConsentType.cs": '''namespace Casazen.Core.Entities.Enums;

public enum ConsentType { Tos, Privacy, Dpa, Subprocessors, Marketing }
''',
"Casazen.Core/Entities/ConsentRecord.cs": '''using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Entities;

[Table("ConsentRecords")]
public class ConsentRecord
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();
    [Required, MaxLength(255)] public string UserId { get; set; } = string.Empty;
    public virtual User User { get; set; } = null!;
    public Guid OrgId { get; set; }
    public virtual Org Org { get; set; } = null!;
    [Required] public ConsentType Type { get; set; }
    [Required, MaxLength(50)] public string Version { get; set; } = string.Empty;
    public DateTime AcceptedAt { get; set; }
    [MaxLength(45)] public string? IpAddress { get; set; }
}
''',
"Casazen.Core/Models/OnboardingModels.cs": '''namespace Casazen.Core.Models;

public record OnboardingConsentsInput(bool TosAccepted, string TosVersion, bool PrivacyAccepted, string PrivacyVersion, bool DpaAccepted, string DpaVersion, bool SubprocessorsAcknowledged, string SubprocessorsVersion, bool? MarketingOptIn = null);
public record OnboardingActivationStatus(bool RoleChosen, bool OrgProvisioned, bool ConsentsAccepted, bool PropertyCreated, bool SitePublished, bool FirstBookingTaken, bool Activated, string? PublicBookingUrl);
''',
"Casazen.Core/Models/LegalDocumentModels.cs": '''namespace Casazen.Core.Models;

public record LegalDocumentMeta(string Version, DateTime EffectiveAt, string Title, string Summary, string? DocumentUrl);
public record SubprocessorItem(string Name, string Purpose, string Region, string? Website);
public record SubprocessorsDocument(string Version, DateTime EffectiveAt, IReadOnlyList<SubprocessorItem> Items);
''',
"Casazen.Core/Services/ILegalDocumentService.cs": '''using Casazen.Core.Models;

namespace Casazen.Core.Services;

public interface ILegalDocumentService
{
    LegalDocumentMeta GetTos();
    LegalDocumentMeta GetPrivacy();
    LegalDocumentMeta GetDpa();
    SubprocessorsDocument GetSubprocessors();
}
''',
"Casazen.Core/Services/IOnboardingService.cs": '''using Casazen.Core.Models;

namespace Casazen.Core.Services;

public enum ConsentValidationErrorType { Incomplete, StaleVersion }
public record ConsentValidationError(ConsentValidationErrorType Type, IReadOnlyList<string> StaleDocuments, string Message);

public interface IOnboardingService
{
    Task<(bool Success, ConsentValidationError? Error, bool ConsentsRecorded)> ValidateAndRecordConsentsAsync(string userId, Guid orgId, OnboardingConsentsInput? consents, bool requireConsents, string? ipAddress, CancellationToken cancellationToken = default);
    Task<OnboardingActivationStatus> GetActivationStatusAsync(string userId, CancellationToken cancellationToken = default);
}
''',
}

def main():
    for rel, content in FILES.items():
        path = ROOT / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(content, encoding="utf-8")
        print(f"Wrote {rel}")

if __name__ == "__main__":
    main()
