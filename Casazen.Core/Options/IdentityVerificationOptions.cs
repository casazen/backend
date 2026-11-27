using System.Globalization;

namespace Casazen.Core.Options;

/// <summary>
/// Section <c>IdentityVerification</c> (E70/F01/US01/T01, NF-188, NF-192, NF-194, NF-195, ADR-013-R17..R22): the files, the transport limits and the
/// retention of the self-hosted identity verification of the check-in (<c>Features:CheckInIdentitySelfHosted</c>). On Railway the variables are
/// <c>IdentityVerification__PolicySha256</c>, <c>IdentityVerification__Presence__MaxFrames</c>, and so on.
/// <para>
/// Nothing here is a secret and nothing here decides: the thresholds, the aggregation rule and the limits of a decision live in the versioned policy file
/// (<see cref="PolicyFile"/>, pinned by <see cref="PolicySha256"/>). These are the limits of what the server accepts and keeps, prudent defaults chosen for
/// the product owner. <see cref="Validate"/> names an invalid setting, never its value; an invalid section never stops the application: its readers treat
/// it as unavailable, and an unavailable verification goes to a person.
/// </para>
/// </summary>
public sealed class IdentityVerificationOptions
{
    public const string SectionName = "IdentityVerification";

    /// <summary>The versioned decision policy, relative to the application folder. Default <c>IdentityPolicies/identity-policy-2026.11-1.json</c>.</summary>
    public string PolicyFile { get; set; } = "IdentityPolicies/identity-policy-2026.11-1.json";

    /// <summary>
    /// The canonical SHA-256 of <see cref="PolicyFile"/> (64 hexadecimal characters) that a reader requires before it uses the policy; empty until
    /// E70/F01/US02/T01 pins it.
    /// </summary>
    public string PolicySha256 { get; set; } = string.Empty;

    /// <summary>The folder of the registry of the document formats (one file per format), relative to the application folder. Default <c>IdentityFormats</c>.</summary>
    public string FormatsDirectory { get; set; } = "IdentityFormats";

    /// <summary>The limits of the sequence of frames of the presence (<c>POST api/public/checkin/{token}/presence</c>, ADR-013-R17, R22).</summary>
    public IdentityPresenceOptions Presence { get; set; } = new();

    /// <summary>What is kept for the person who reviews a verification, with <c>Features:CheckInIdentityReviewEvidence</c> on (ADR-013-R20).</summary>
    public IdentityEvidenceOptions Evidence { get; set; } = new();

    /// <summary>How long the sessions, the checks of the document and the verification logs are kept (NF-195).</summary>
    public IdentityRetentionOptions Retention { get; set; } = new();

    /// <summary>Configuration errors in the order of the settings, empty when the section is usable. Each names the setting and its bounds, never its value.</summary>
    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(PolicyFile))
            failures.Add($"{SectionName}:{nameof(PolicyFile)} must not be empty.");
        if (!string.IsNullOrEmpty(PolicySha256) && !IsSha256(PolicySha256))
            failures.Add($"{SectionName}:{nameof(PolicySha256)} must be empty or 64 hexadecimal characters.");
        if (string.IsNullOrWhiteSpace(FormatsDirectory))
            failures.Add($"{SectionName}:{nameof(FormatsDirectory)} must not be empty.");

        CheckRange(failures, "Presence:MaxFrames", Presence.MaxFrames, 2, 300);
        CheckRange(failures, "Presence:MaxFrameBytes", Presence.MaxFrameBytes, 8_192, 2_097_152);
        CheckRange(failures, "Presence:MaxSequenceBytes", Presence.MaxSequenceBytes, 65_536, 33_554_432);
        if (Presence.MaxSequenceBytes < Presence.MaxFrameBytes)
            failures.Add($"{SectionName}:Presence:MaxSequenceBytes must not be smaller than {SectionName}:Presence:MaxFrameBytes.");
        CheckRange(failures, "Presence:MaxFrameSide", Presence.MaxFrameSide, 160, 1_920);
        CheckRange(failures, "Presence:SequenceTimeoutMs", Presence.SequenceTimeoutMs, 500, 30_000);

        CheckRange(failures, "Evidence:RetentionHours", Evidence.RetentionHours, 1, 168);
        CheckRange(failures, "Evidence:MaxFrames", Evidence.MaxFrames, 0, 5);
        CheckRange(failures, "Evidence:MaxSide", Evidence.MaxSide, 64, 1_920);

        CheckRange(failures, "Retention:AbandonedAfterHours", Retention.AbandonedAfterHours, 1, 168);
        CheckRange(failures, "Retention:DocumentCheckDays", Retention.DocumentCheckDays, 1, 365);
        CheckRange(failures, "Retention:VerificationLogMonths", Retention.VerificationLogMonths, 1, 120);
        return failures;
    }

    private static void CheckRange(List<string> failures, string name, int value, int min, int max)
    {
        if (value < min || value > max)
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"{SectionName}:{name} must be between {min} and {max}."));
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);
}

/// <summary>
/// The limits of one sequence of frames of the presence (ADR-013-R17, R22): what the server accepts before it reads a frame. The number of frames a sequence
/// must have and the timing it must keep are decisions of the policy file, not limits.
/// </summary>
public sealed class IdentityPresenceOptions
{
    /// <summary>The most frames one sequence may carry (2 to 300). Default 60.</summary>
    public int MaxFrames { get; set; } = 60;

    /// <summary>The largest frame, a JPEG, in bytes (8 KiB to 2 MiB). Default 262144 (256 KiB).</summary>
    public int MaxFrameBytes { get; set; } = 262_144;

    /// <summary>The largest sequence, all its frames together, in bytes (64 KiB to 32 MiB, never below <see cref="MaxFrameBytes"/>). Default 6291456 (6 MiB).</summary>
    public int MaxSequenceBytes { get; set; } = 6_291_456;

    /// <summary>The longest side of a frame, in pixels (160 to 1920, ADR-013-R22). Default 720.</summary>
    public int MaxFrameSide { get; set; } = 720;

    /// <summary>
    /// The time the server may take on one sequence, in milliseconds (500 to 30000, ADR-013-R17): the request is synchronous, past the limit a person decides.
    /// Default 8000.
    /// </summary>
    public int SequenceTimeoutMs { get; set; } = 8_000;
}

/// <summary>
/// The evidence kept for the person who reviews a verification (ADR-013-R20, <c>Features:CheckInIdentityReviewEvidence</c>): the crop of the portrait of the
/// document and the frames chosen by the server, encrypted, never an embedding, never for a property in Ticino.
/// </summary>
public sealed class IdentityEvidenceOptions
{
    /// <summary>Hours the evidence of a verification is kept before the purge deletes it (1 to 168). Default 48.</summary>
    public int RetentionHours { get; set; } = 48;

    /// <summary>The most frames chosen by the server kept next to the crop of the portrait (0 to 5; 0 keeps the crop only). Default 3.</summary>
    public int MaxFrames { get; set; } = 3;

    /// <summary>The longest side, in pixels, of the crop and of the frames kept (64 to 1920). Default 480.</summary>
    public int MaxSide { get; set; } = 480;
}

/// <summary>How long the data of the identity verification is kept (NF-195): the defaults are proposals of the product owner to confirm with the DPO.</summary>
public sealed class IdentityRetentionOptions
{
    /// <summary>Hours after which a verification opened and never closed is abandoned, and what it kept is deleted (1 to 168). Default 24.</summary>
    public int AbandonedAfterHours { get; set; } = 24;

    /// <summary>Days the checks of the document kept on the session (<c>GuestCheckInSession.DocumentCheckJson</c>) are kept (1 to 365). Default 30.</summary>
    public int DocumentCheckDays { get; set; } = 30;

    /// <summary>Months the rows of <c>BiometricVerificationLogs</c> are kept (1 to 120). Default 12.</summary>
    public int VerificationLogMonths { get; set; } = 12;
}
