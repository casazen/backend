using System.Diagnostics.CodeAnalysis;

namespace Casazen.Core.Identity;

// E70/F01/US01/T01 (NF-188..NF-197, ADR-013-R17..R22): the codes of the self-hosted identity verification of the check-in, shared by the backend, the
// frontend (which translates them) and the stored rows. Codes only: never a value read on a document, a frame, a face representation or a score.
// A code is never renamed or reused; a new one is added at the end of its class and of its All list.

public static class IdentityChoices
{
    public const string Automatic = "automatic";
    public const string Assisted = "assisted";

    public static IReadOnlyList<string> All { get; } = [Automatic, Assisted];
}

public static class IdentityOutcomeCodes
{
    public const string AutoApproved = "auto_approved";

    /// <summary>A person reviews the verification (the decision of the policy, a correctable defect after the guided retry, a conflict).</summary>
    public const string Review = "review";

    public const string Assisted = "assisted";

    public const string ConsentRefused = "consent_refused";

    public const string FormatUnsupported = "format_unsupported";

    public const string TechnicalError = "technical_error";

    public const string Unavailable = "unavailable";

    public const string MinorExcluded = "minor_excluded";

    public const string Abandoned = "abandoned";

    public static IReadOnlyList<string> All { get; } =
        [AutoApproved, Review, Assisted, ConsentRefused, FormatUnsupported, TechnicalError, Unavailable, MinorExcluded, Abandoned];
}

public static class IdentityChannels
{
    public const string IosSafari = "web:ios-safari";
    public const string AndroidChrome = "web:android-chrome";
    public const string DesktopChromium = "web:desktop-chromium";
    public const string DesktopSafari = "web:desktop-safari";
    public const string DesktopFirefox = "web:desktop-firefox";

    public const string Other = "web:other";

    public static IReadOnlyList<string> All { get; } = [IosSafari, AndroidChrome, DesktopChromium, DesktopSafari, DesktopFirefox, Other];
}

public static class PresenceActions
{
    public const string TurnLeft = "turn_left";
    public const string TurnRight = "turn_right";
    public const string LookUp = "look_up";
    public const string LookDown = "look_down";

    public static IReadOnlyList<string> All { get; } = [TurnLeft, TurnRight, LookUp, LookDown];
}

public static class IdentityGuidanceCodes
{
    public const string DocumentNotFound = "document_not_found";
    public const string DocumentEdgesNotVisible = "document_edges_not_visible";
    public const string DocumentLayoutMismatch = "document_layout_mismatch";
    public const string DocumentOrientation = "document_orientation";
    public const string DocumentPortraitMissing = "document_portrait_missing";
    public const string DocumentSideDuplicate = "document_side_duplicate";
    public const string DocumentWrongSide = "document_wrong_side";
    public const string DocumentFormatUnsupported = "document_format_unsupported";
    public const string DocumentGlare = "document_glare";
    public const string DocumentBlur = "document_blur";
    public const string DocumentFieldConflict = "document_field_conflict";
    public const string DocumentExpired = "document_expired";
    public const string DocumentUnreadable = "document_unreadable";
    public const string PresenceTooShort = "presence_too_short";
    public const string PresenceTiming = "presence_timing";
    public const string PresenceQuality = "presence_quality";
    public const string PresenceSingleFace = "presence_single_face";
    public const string PresenceChallengeNotFollowed = "presence_challenge_not_followed";
    public const string PresenceContinuity = "presence_continuity";

    public static IReadOnlyList<string> All { get; } =
    [
        DocumentNotFound, DocumentEdgesNotVisible, DocumentLayoutMismatch, DocumentOrientation, DocumentPortraitMissing, DocumentSideDuplicate,
        DocumentWrongSide, DocumentFormatUnsupported, DocumentGlare, DocumentBlur, DocumentFieldConflict, DocumentExpired, DocumentUnreadable,
        PresenceTooShort, PresenceTiming, PresenceQuality, PresenceSingleFace, PresenceChallengeNotFollowed, PresenceContinuity,
    ];
}

public static class IdentityApiErrors
{
    public const string IdentityAlreadyClosed = "identity_already_closed";

    public const string NoticeVersionUnknown = "notice_version_unknown";

    public const string PresenceChallengeUnavailable = "presence_challenge_unavailable";
    public const string PresenceChallengeRequired = "presence_challenge_required";
    public const string PresenceChallengeExpired = "presence_challenge_expired";

    public const string PresenceSequenceRequired = "presence_sequence_required";

    public const string PresenceSequenceInvalid = "presence_sequence_invalid";
    public const string PresenceSequenceTooLarge = "presence_sequence_too_large";

    /// <summary>The request has no valid <see cref="IdentityHeaders.IdempotencyKey"/> header.</summary>
    public const string IdempotencyKeyRequired = "idempotency_key_required";

    public const string IdentitySegmentNotQualified = "identity_segment_not_qualified";
    public const string IdentitySegmentInvalid = "identity_segment_invalid";
    public const string IdentityEvidenceNotFound = "identity_evidence_not_found";

    public static IReadOnlyList<string> All { get; } =
    [
        IdentityAlreadyClosed, NoticeVersionUnknown, PresenceChallengeUnavailable, PresenceChallengeRequired, PresenceChallengeExpired,
        PresenceSequenceRequired, PresenceSequenceInvalid, PresenceSequenceTooLarge, IdempotencyKeyRequired, IdentitySegmentNotQualified,
        IdentitySegmentInvalid, IdentityEvidenceNotFound,
    ];
}

public static class IdentityHeaders
{
    /// <summary>
    /// The explicit key of one attempt (NF-194, AC-NF192-5): the client sends the same key when it repeats a request (a double submit, a network retry), and the
    /// server answers the same outcome without a second one. Its format is <see cref="IdempotencyKeyPattern"/>.
    /// </summary>
    public const string IdempotencyKey = "Idempotency-Key";

    /// <summary>The format of <see cref="IdempotencyKey"/>: 8 to 64 ASCII letters, digits or hyphens (a UUID fits).</summary>
    public const string IdempotencyKeyPattern = "^[A-Za-z0-9-]{8,64}$";

    public const int IdempotencyKeyMinLength = 8;

    public const int IdempotencyKeyMaxLength = 64;

    /// <summary>Whether <paramref name="value"/> matches <see cref="IdempotencyKeyPattern"/> (a blank or a null value does not).</summary>
    public static bool IsValidIdempotencyKey([NotNullWhen(true)] string? value)
    {
        if (value is null || value.Length < IdempotencyKeyMinLength || value.Length > IdempotencyKeyMaxLength)
            return false;

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character != '-')
                return false;
        }

        return true;
    }
}

public static class IdentityAssuranceLevels
{
    public const string Consistency = "consistency";
    public const string Cryptographic = "cryptographic";

    public static IReadOnlyList<string> All { get; } = [Consistency, Cryptographic];
}

public static class IdentityEvidenceDeletionReasons
{
    public const string Decided = "decided";

    public const string Expired = "expired";

    public const string Abandoned = "abandoned";

    public const string Erased = "erased";

    public const string Replaced = "replaced";

    public static IReadOnlyList<string> All { get; } = [Decided, Expired, Abandoned, Erased, Replaced];
}
