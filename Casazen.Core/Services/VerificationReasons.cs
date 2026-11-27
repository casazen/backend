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

/// <summary>The codes of <see cref="VerificationReason"/> and their JSON form (E03/F04/US02/T02).</summary>
public static class VerificationReasons
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The checks, in the order the agent evaluates them.</summary>
    public static class Checks
    {
        /// <summary>The captures are good enough to be compared (face found, one face, large enough, light, focus).</summary>
        public const string Quality = "quality";

        /// <summary>The anti-spoofing model judges the selfie a live capture (score against <c>Biometrics:LivenessThreshold</c>).</summary>
        public const string Liveness = "liveness";

        /// <summary>The document was read from its MRZ, its check digits are valid and it is not expired.</summary>
        public const string DocumentValidity = "document_validity";

        /// <summary>The MRZ agrees with the check-in data of the guest (names, birth date, document number, nationality).</summary>
        public const string MrzConsistency = "mrz_consistency";

        /// <summary>The similarity of the selfie and the photo of the document, against the accept and the escalate thresholds.</summary>
        public const string Similarity = "similarity";

        /// <summary>The models could not run (not available, timeout, error): the verification goes to the operator.</summary>
        public const string Processing = "processing";

        /// <summary>The head-turn proof of the legacy selfie (E22/F01: the neutral and the turned frame, the direction asked).</summary>
        public const string ActivePose = "active_pose";

        // E70/F01/US01/T01: the checks of the self-hosted verification (NF-189..NF-194, ADR-013-R17..R19).

        /// <summary>The presence challenge was followed: the moves asked, in their order, before its expiry, for its session and attempt.</summary>
        public const string PresenceChallenge = "presence_challenge";

        /// <summary>The timing of the sequence is plausible: dense, ordered, and never longer than the time since the challenge was issued.</summary>
        public const string PresenceTiming = "presence_timing";

        /// <summary>The sequence is continuous: no static replay, no splice of two people.</summary>
        public const string PresenceContinuity = "presence_continuity";

        /// <summary>Every frame of the sequence holds one face, and only one.</summary>
        public const string PresenceSingleFace = "presence_single_face";

        /// <summary>The frames are good enough to be judged (light, focus, size of the face).</summary>
        public const string PresenceQuality = "presence_quality";

        /// <summary>The document fits the layout of its format: the whole card, its edges, its orientation, the portrait where the layout expects it.</summary>
        public const string DocumentLayout = "document_layout";

        /// <summary>The sides of the document are the expected ones: no side repeated, no wrong side.</summary>
        public const string DocumentSide = "document_side";

        /// <summary>The fields read agree across the sides, the dates and the check digits (a conflict on an identifying field goes to review).</summary>
        public const string DocumentCoherence = "document_coherence";

        /// <summary>The format of the document is in the registry of the formats.</summary>
        public const string DocumentFormat = "document_format";

        /// <summary>The format and the channel give the level of document assurance the segment requires.</summary>
        public const string AssuranceLevel = "assurance_level";

        /// <summary>The segment is in observation: the decision of the policy is the shadow decision and a person decides.</summary>
        public const string ObservationMode = "observation_mode";
    }

    /// <summary>The results of a check.</summary>
    public static class Results
    {
        public const string Passed = "passed";
        public const string Failed = "failed";

        /// <summary>The check was not reached: an earlier step stopped the verification.</summary>
        public const string NotRun = "not_run";

        /// <summary>The similarity is above the escalate threshold and below the accept one: a person decides.</summary>
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

    /// <summary>The JSON array stored in the log (<c>[]</c> for none).</summary>
    public static string Serialize(IReadOnlyList<VerificationReason>? reasons) =>
        reasons is null || reasons.Count == 0 ? "[]" : JsonSerializer.Serialize(reasons, Json);

    /// <summary>The reasons of a stored JSON array; empty for none or for a value that is not one (a row of before the column).</summary>
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
