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

    public static BiometricsStatusDto From(BiometricModelsStatus status) => new()
    {
        Enabled = status.FeatureEnabled,
        State = status.State,
        ReasonCode = status.ReasonCode,
        Models = status.Models.Select(BiometricModelStatusDto.From).ToList(),
    };
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
