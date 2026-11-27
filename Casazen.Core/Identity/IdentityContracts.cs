using System.Diagnostics.CodeAnalysis;

namespace Casazen.Core.Identity;

// E70/F01/US01/T01 (NF-188..NF-197, ADR-013-R17..R22): the codes of the self-hosted identity verification of the check-in, shared by the backend, the
// frontend (which translates them) and the stored rows. Codes only: never a value read on a document, a frame, a face representation or a score.
// A code is never renamed or reused; a new one is added at the end of its class and of its All list.

/// <summary>What the guest chose when the identity step opened (NF-188): the automatic verification or a person (the assisted path, always offered).</summary>
public static class IdentityChoices
{
    public const string Automatic = "automatic";
    public const string Assisted = "assisted";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } = [Automatic, Assisted];
}

/// <summary>
/// The terminal outcome of a self-hosted verification, as the session records it (<c>GuestCheckInSession.IdentityOutcomeCode</c>, NF-188, NF-196): the
/// categories the counters split the opened sessions into.
/// </summary>
public static class IdentityOutcomeCodes
{
    /// <summary>The policy approved the guest in a qualified segment, with the global permission on.</summary>
    public const string AutoApproved = "auto_approved";

    /// <summary>A person reviews the verification (the decision of the policy, a correctable defect after the guided retry, a conflict).</summary>
    public const string Review = "review";

    /// <summary>The guest chose the assisted path, or asked for a person.</summary>
    public const string Assisted = "assisted";

    /// <summary>The guest refused the biometric consent (ADR-013-R9).</summary>
    public const string ConsentRefused = "consent_refused";

    /// <summary>The document is of a format the registry does not hold (NF-191).</summary>
    public const string FormatUnsupported = "format_unsupported";

    /// <summary>A model failed or timed out during an attempt: a person decides, never an approval.</summary>
    public const string TechnicalError = "technical_error";

    /// <summary>The automatic verification was not offered (switched off for the organization, no approved legal text, no usable model or policy).</summary>
    public const string Unavailable = "unavailable";

    /// <summary>The guest who must be verified is a minor: never processed (ADR-013-R9).</summary>
    public const string MinorExcluded = "minor_excluded";

    /// <summary>The verification was opened and never closed within <c>IdentityVerification:Retention:AbandonedAfterHours</c>.</summary>
    public const string Abandoned = "abandoned";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } =
        [AutoApproved, Review, Assisted, ConsentRefused, FormatUnsupported, TechnicalError, Unavailable, MinorExcluded, Abandoned];
}

/// <summary>
/// The coarse family of the browser a verification ran in (NF-188: the User-Agent is never stored, only this), one axis of a segment (property × format ×
/// channel, NF-194) and of the coverage of the counters (NF-196).
/// </summary>
public static class IdentityChannels
{
    public const string IosSafari = "web:ios-safari";
    public const string AndroidChrome = "web:android-chrome";
    public const string DesktopChromium = "web:desktop-chromium";
    public const string DesktopSafari = "web:desktop-safari";
    public const string DesktopFirefox = "web:desktop-firefox";

    /// <summary>Any other browser, or one that could not be recognised.</summary>
    public const string Other = "web:other";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } = [IosSafari, AndroidChrome, DesktopChromium, DesktopSafari, DesktopFirefox, Other];
}

/// <summary>The moves a presence challenge asks for, in the order it gives them (NF-192, ADR-013-R17), as the image sent to the server shows them.</summary>
public static class PresenceActions
{
    public const string TurnLeft = "turn_left";
    public const string TurnRight = "turn_right";
    public const string LookUp = "look_up";
    public const string LookDown = "look_down";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } = [TurnLeft, TurnRight, LookUp, LookDown];
}

/// <summary>What the guest is asked to change for a new capture (NF-189, NF-190, NF-192): codes the page translates, one targeted reason each.</summary>
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

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        DocumentNotFound, DocumentEdgesNotVisible, DocumentLayoutMismatch, DocumentOrientation, DocumentPortraitMissing, DocumentSideDuplicate,
        DocumentWrongSide, DocumentFormatUnsupported, DocumentGlare, DocumentBlur, DocumentFieldConflict, DocumentExpired, DocumentUnreadable,
        PresenceTooShort, PresenceTiming, PresenceQuality, PresenceSingleFace, PresenceChallengeNotFollowed, PresenceContinuity,
    ];
}

/// <summary>The <c>code</c> of the errors of the endpoints of the self-hosted verification (<c>ApiProblem</c>): the page translates them.</summary>
public static class IdentityApiErrors
{
    /// <summary>The verification of the session is closed: no document or presence step is accepted any more, the request for a person stays open (AC-NF188-3).</summary>
    public const string IdentityAlreadyClosed = "identity_already_closed";

    /// <summary>The opening names a version of the notice that is not the one served (AC-NF188-2).</summary>
    public const string NoticeVersionUnknown = "notice_version_unknown";

    public const string PresenceChallengeUnavailable = "presence_challenge_unavailable";
    public const string PresenceChallengeRequired = "presence_challenge_required";
    public const string PresenceChallengeExpired = "presence_challenge_expired";

    /// <summary>A sequence of frames is required: with <c>Features:CheckInIdentitySelfHosted</c> on the legacy selfie routes answer it (AC-NF192-4).</summary>
    public const string PresenceSequenceRequired = "presence_sequence_required";

    public const string PresenceSequenceInvalid = "presence_sequence_invalid";
    public const string PresenceSequenceTooLarge = "presence_sequence_too_large";

    /// <summary>The request has no valid <see cref="IdentityHeaders.IdempotencyKey"/> header.</summary>
    public const string IdempotencyKeyRequired = "idempotency_key_required";

    public const string IdentitySegmentNotQualified = "identity_segment_not_qualified";
    public const string IdentitySegmentInvalid = "identity_segment_invalid";
    public const string IdentityEvidenceNotFound = "identity_evidence_not_found";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        IdentityAlreadyClosed, NoticeVersionUnknown, PresenceChallengeUnavailable, PresenceChallengeRequired, PresenceChallengeExpired,
        PresenceSequenceRequired, PresenceSequenceInvalid, PresenceSequenceTooLarge, IdempotencyKeyRequired, IdentitySegmentNotQualified,
        IdentitySegmentInvalid, IdentityEvidenceNotFound,
    ];
}

/// <summary>The headers of the self-hosted verification.</summary>
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

    /// <summary>The longest key, and the length of the columns that keep one.</summary>
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

/// <summary>
/// The level of document assurance a segment requires and a format gives (NF-191, D11): a photo gives <see cref="Consistency"/> only (the layout and the check
/// digits are coherent); where <see cref="Cryptographic"/> is required (the signed data of the chip), the photo alone leads to review.
/// </summary>
public static class IdentityAssuranceLevels
{
    public const string Consistency = "consistency";
    public const string Cryptographic = "cryptographic";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } = [Consistency, Cryptographic];
}

/// <summary>Why the evidence kept for a reviewer was deleted (<c>IdentityReviewEvidence.DeletionReason</c>, NF-195).</summary>
public static class IdentityEvidenceDeletionReasons
{
    /// <summary>The verification was decided.</summary>
    public const string Decided = "decided";

    /// <summary>Its time to live passed.</summary>
    public const string Expired = "expired";

    /// <summary>The session was abandoned.</summary>
    public const string Abandoned = "abandoned";

    /// <summary>The data of the guest was erased (GDPR art. 17).</summary>
    public const string Erased = "erased";

    /// <summary>A later review of the same booking kept its own evidence: one set per booking (E73/F01/US02/T01).</summary>
    public const string Replaced = "replaced";

    /// <summary>Every code, in this order.</summary>
    public static IReadOnlyList<string> All { get; } = [Decided, Expired, Abandoned, Erased, Replaced];
}
