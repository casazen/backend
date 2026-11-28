using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.DTOs.Admin;

public sealed class BiometricsStatusDto
{
    public bool Enabled { get; set; }

    public string State { get; set; } = string.Empty;

    /// <summary>Why the set is unavailable, as a code (<c>config_invalid</c>, <c>hash_not_configured</c>, <c>file_missing</c>, ...); null otherwise.</summary>
    public string? ReasonCode { get; set; }

    public IReadOnlyList<BiometricModelStatusDto> Models { get; set; } = [];

    public IdentityRetentionStatusDto? Retention { get; set; }

    public static BiometricsStatusDto From(BiometricModelsStatus status, IdentityRetentionStatus? retention = null) => new()
    {
        Enabled = status.FeatureEnabled,
        State = status.State,
        ReasonCode = status.ReasonCode,
        Models = status.Models.Select(BiometricModelStatusDto.From).ToList(),
        Retention = retention is null ? null : IdentityRetentionStatusDto.From(retention),
    };
}

public sealed class IdentityRetentionStatusDto
{
    public int OverdueEvidence { get; set; }

    public int OverdueDocumentImages { get; set; }

    public DateTime? LastRunAt { get; set; }

    public IdentityRetentionCountsDto? LastRunCounts { get; set; }

    public static IdentityRetentionStatusDto From(IdentityRetentionStatus status) => new()
    {
        OverdueEvidence = status.OverdueEvidence,
        OverdueDocumentImages = status.OverdueDocumentImages,
        LastRunAt = status.LastRun?.RanAt,
        LastRunCounts = status.LastRun is { } run ? IdentityRetentionCountsDto.From(run) : null,
    };
}

public sealed class IdentityRetentionCountsDto
{
    public int ExpiredEvidence { get; set; }

    public int DecidedEvidence { get; set; }

    public int AbandonedSessions { get; set; }

    public int AbandonedEvidence { get; set; }

    public int AbandonedDocumentImages { get; set; }

    public int DocumentChecksCleared { get; set; }

    public int VerificationLogsDeleted { get; set; }

    public int FailedOrgs { get; set; }

    public static IdentityRetentionCountsDto From(IdentityRetentionRun run) => new()
    {
        ExpiredEvidence = run.Counts.ExpiredEvidence,
        DecidedEvidence = run.Counts.DecidedEvidence,
        AbandonedSessions = run.Counts.AbandonedSessions,
        AbandonedEvidence = run.Counts.AbandonedEvidence,
        AbandonedDocumentImages = run.Counts.AbandonedDocumentImages,
        DocumentChecksCleared = run.Counts.DocumentChecksCleared,
        VerificationLogsDeleted = run.Counts.VerificationLogsDeleted,
        FailedOrgs = run.FailedOrgs,
    };
}

/// <summary>
/// The qualification of a segment of the self-hosted identity verification (E70/F02/US01/T02, NF-194, <c>PUT api/admin/biometrics/{orgId}/segments/{segmentId}/qualification</c>),
/// recorded by a platform administrator after the offline qualification: the policy version, the canonical policy hash and the weights hash it was made with (they must be
/// the ones in force, and the policy calibrated, or 422 <c>identity_segment_invalid</c>), the SHA-256 of the report of the qualification and the level of assurance
/// (<c>consistency</c> or <c>cryptographic</c>) the segment requires from then on. Hashes and codes only.
/// </summary>
public sealed class QualifyIdentitySegmentDto
{
    public string? PolicyVersion { get; set; }

    public string? PolicyHash { get; set; }

    public string? WeightsHash { get; set; }

    public string? ReportSha256 { get; set; }

    public string? RequiredAssurance { get; set; }
}

public sealed class BiometricModelStatusDto
{
    public string Role { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string License { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    /// <summary>The failure code when <see cref="State"/> is <c>failed</c> (<c>hash_not_configured</c>, <c>file_missing</c>, <c>hash_mismatch</c>, <c>download_failed</c>, ...); null otherwise.</summary>
    public string? Code { get; set; }

    public bool Pinned { get; set; }

    public static BiometricModelStatusDto From(BiometricModelStatus model) => new()
    {
        Role = model.Role,
        Name = model.Name,
        Version = model.Version,
        License = model.License,
        State = model.State,
        Code = model.Code,
        Pinned = model.Pinned,
    };
}
