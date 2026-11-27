using System.Text.Json;
using System.Text.Json.Serialization;

namespace Casazen.Core.Services;

/// <summary>
/// One check of an identity verification decision (E03/F04/US02, AC-NF51-5, AC-NF51-8): which check, its result, and the number and the
/// threshold it was compared with when there is one. Codes and numbers only: never an image, a vector, a name or a document number. This is
/// what <c>BiometricVerificationLog.ReasonsJson</c> stores and what the operator queue shows.
/// </summary>
/// <param name="Check">A code of <see cref="VerificationReasons.Checks"/>.</param>
/// <param name="Result">A code of <see cref="VerificationReasons.Results"/>.</param>
/// <param name="Value">The number measured (a score, a share), when the check has one.</param>
/// <param name="Threshold">The threshold the value was compared with, when the check has one.</param>
public sealed record VerificationReason(string Check, string Result, double? Value = null, double? Threshold = null);

public static class VerificationReasons
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static class Checks
    {
        public const string Quality = "quality";

        public const string Liveness = "liveness";

        public const string DocumentValidity = "document_validity";

        public const string MrzConsistency = "mrz_consistency";

        public const string Similarity = "similarity";

        public const string Processing = "processing";

        public const string ActivePose = "active_pose";

        public const string PresenceChallenge = "presence_challenge";

        /// <summary>The timing of the sequence is plausible: dense, ordered, and never longer than the time since the challenge was issued.</summary>
        public const string PresenceTiming = "presence_timing";

        public const string PresenceContinuity = "presence_continuity";

        public const string PresenceSingleFace = "presence_single_face";

        public const string PresenceQuality = "presence_quality";

        public const string DocumentLayout = "document_layout";

        public const string DocumentSide = "document_side";

        public const string DocumentCoherence = "document_coherence";

        public const string DocumentFormat = "document_format";

        public const string AssuranceLevel = "assurance_level";

        public const string ObservationMode = "observation_mode";
    }

    public static class Results
    {
        public const string Passed = "passed";
        public const string Failed = "failed";

        public const string NotRun = "not_run";

        public const string BetweenThresholds = "between_thresholds";

        public const string Expired = "expired";
        public const string Unreadable = "unreadable";
        public const string CheckDigitsInvalid = "check_digits_invalid";
        public const string NamesMismatch = "names_mismatch";
        public const string BirthDateMismatch = "birth_date_mismatch";
        public const string DocumentNumberMismatch = "document_number_mismatch";
        public const string NationalityMismatch = "nationality_mismatch";
        public const string Unavailable = "unavailable";
        public const string Timeout = "timeout";
        public const string Error = "error";
    }

    public static string Serialize(IReadOnlyList<VerificationReason>? reasons) =>
        reasons is null || reasons.Count == 0 ? "[]" : JsonSerializer.Serialize(reasons, Json);

    public static IReadOnlyList<VerificationReason> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            return JsonSerializer.Deserialize<List<VerificationReason>>(json, Json)?
                .Where(r => !string.IsNullOrWhiteSpace(r.Check) && !string.IsNullOrWhiteSpace(r.Result))
                .ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
