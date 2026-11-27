using Casazen.Infrastructure.Identity;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.DTOs.Admin;

/// <summary>
/// The models of the identity verification as the platform administrators see them (E22/F01/US01/T01, <c>GET api/admin/biometrics/status</c>): whether
/// the feature is on, the state of the set (<c>disabled</c>, <c>loading</c>, <c>ready</c>, <c>unavailable</c>) with the reason code the store gave, and
/// one entry per model. The shape is closed on purpose: a model is named by its role, version and licence, and says whether its SHA-256 is pinned; there
/// is no path, no hash and no download address here, so a status can be read aloud or pasted into a ticket.
/// </summary>
public sealed class BiometricsStatusDto
{
    /// <summary><c>Features:CheckInIdentityVerification</c>.</summary>
    public bool Enabled { get; set; }

    /// <summary><c>disabled</c>, <c>loading</c>, <c>ready</c> or <c>unavailable</c> (at least one model cannot be used: every verification goes to the operator).</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Why the set is unavailable, as a code (<c>config_invalid</c>, <c>hash_not_configured</c>, <c>file_missing</c>, ...); null otherwise.</summary>
    public string? ReasonCode { get; set; }

    public IReadOnlyList<BiometricModelStatusDto> Models { get; set; } = [];

    /// <summary>
    /// The verifiable deletion of what the self-hosted verification keeps (E73/F01/US02/T03, NF-195, AC-NF195-4): what is overdue now and the last run of the hourly purge.
    /// Null only for a status built without it.
    /// </summary>
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

/// <summary>
/// The retention of the self-hosted identity verification as a platform administrator checks it (E73/F01/US02/T03): <c>overdueEvidence</c> are the pictures kept for a
/// reviewer past their expiry and not deleted, <c>overdueDocumentImages</c> the kept pictures of documents that no session is still using; both read from the database, across
/// every organization, and both 0 when nothing overdue remains (the hourly purge deletes them). <c>lastRunAt</c> and <c>lastRunCounts</c> are the last run of this process (null
/// after a start until the first run, and while the purge is not scheduled). Counts and a time only.
/// </summary>
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

/// <summary>What one run of the identity retention deleted, per category, and how many organizations it could not finish (counts only).</summary>
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

/// <summary>One model of <see cref="BiometricsStatusDto"/>.</summary>
public sealed class BiometricModelStatusDto
{
    /// <summary><c>face_detector</c>, <c>anti_spoofing</c>, <c>face_recognition</c> or <c>mrz_ocr</c> (<see cref="BiometricModelRoles"/>).</summary>
    public string Role { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string License { get; set; } = string.Empty;

    /// <summary><c>disabled</c>, <c>loading</c>, <c>ready</c>, <c>present</c> (the OCR-B file: pinned and on disk) or <c>failed</c>.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>The failure code when <see cref="State"/> is <c>failed</c> (<c>hash_not_configured</c>, <c>file_missing</c>, <c>hash_mismatch</c>, <c>download_failed</c>, ...); null otherwise.</summary>
    public string? Code { get; set; }

    /// <summary>The SHA-256 of the file is pinned in the configuration (a well-formed value): without it the file is never used.</summary>
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
