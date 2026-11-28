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

    public string PolicyFile { get; set; } = "IdentityPolicies/identity-policy-2026.11-1.json";

    public string PolicySha256 { get; set; } = string.Empty;

    public string FormatsDirectory { get; set; } = "IdentityFormats";

    public IdentityPresenceOptions Presence { get; set; } = new();

    public IdentityEvidenceOptions Evidence { get; set; } = new();

    public IdentityRetentionOptions Retention { get; set; } = new();

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

        failures.AddRange(ValidateRetention());
        return failures;
    }

    public IReadOnlyList<string> ValidateRetention()
    {
        var failures = new List<string>();
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

public sealed class IdentityPresenceOptions
{
    public int MaxFrames { get; set; } = 60;

    public int MaxFrameBytes { get; set; } = 262_144;

    public int MaxSequenceBytes { get; set; } = 6_291_456;

    public int MaxFrameSide { get; set; } = 720;

    // On a busy CPU the models need several seconds for one sequence, and a time over sends the guest to a person.
    public int SequenceTimeoutMs { get; set; } = 20_000;
}

public sealed class IdentityEvidenceOptions
{
    public int RetentionHours { get; set; } = 48;

    public int MaxFrames { get; set; } = 3;

    public int MaxSide { get; set; } = 480;
}

public sealed class IdentityRetentionOptions
{
    public int AbandonedAfterHours { get; set; } = 24;

    public int DocumentCheckDays { get; set; } = 30;

    public int VerificationLogMonths { get; set; } = 12;
}
