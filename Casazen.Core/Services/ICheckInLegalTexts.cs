namespace Casazen.Core.Services;

/// <summary>
/// The registry of the legal texts of the guest check-in (E22/F01/US02/T02, NF-24, NF-50, decision O9): the biometric consent and the Ticino notice, as files
/// under <c>Casazen.Web/LegalDocuments/checkin/</c> with a <c>manifest.json</c> that says, for each text, its version, its status (<c>draft</c> or
/// <c>approved</c>, with who approved it and when) and the SHA-256 of each file. The code never writes a text and never serves a file whose hash is not the
/// one of the manifest. <b>Production serves only approved versions</b>; every other environment serves the drafts too, flagged as such. Runbook:
/// <c>docs/runbooks/legal-documents.md</c>.
/// </summary>
public interface ICheckInLegalTexts
{
    /// <summary>
    /// The texts a guest of a stay reads, in <paramref name="language"/> (or the closest one the registry has): the active version of the biometric consent and,
    /// for a property that keeps no document (<paramref name="documentNotArchived"/>: Ticino, or Switzerland with a canton CasaZen does not know), of the Ticino notice. A
    /// kind with no version to serve (in production: none approved) is not in the list.
    /// </summary>
    IReadOnlyList<CheckInLegalText> GetForStay(bool documentNotArchived, string? language);

    /// <summary>The version the registry serves now for <paramref name="kind"/> (<see cref="CheckInLegalTextKinds"/>), or <c>null</c> when it serves none.</summary>
    CheckInLegalVersion? GetActiveVersion(string kind);

    /// <summary>
    /// The version of <paramref name="kind"/> that is <paramref name="version"/>, when it is the one the registry serves now; <c>null</c> for any other string
    /// (a free text, a version of the past, a draft in production): a consent can refer to a served version only.
    /// </summary>
    CheckInLegalVersion? FindServedVersion(string kind, string? version);
}

/// <summary>The kinds of text of the registry (<c>kind</c> of the manifest).</summary>
public static class CheckInLegalTextKinds
{
    /// <summary>The consent to the biometric verification (face against the photo of the document), asked on its own screen (ADR-013-R9).</summary>
    public const string BiometricConsent = "biometric-consent";

    /// <summary>The notice for a property that keeps no document (Switzerland, Ticino): what is processed, what is not archived, the rights (nFADP).</summary>
    public const string TicinoNotice = "ticino-notice";

    /// <summary>Every kind, in the order the guest page shows them.</summary>
    public static IReadOnlyList<string> All { get; } = [BiometricConsent, TicinoNotice];

    /// <summary>True for one of <see cref="All"/>.</summary>
    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

/// <summary>The statuses of a text of the registry (<c>status</c> of the manifest).</summary>
public static class CheckInLegalTextStatuses
{
    /// <summary>Written, not approved by the lawyer yet: served outside production only, with <c>isDraft</c>.</summary>
    public const string Draft = "draft";

    /// <summary>Approved: <c>approvedBy</c> and <c>approvedAt</c> of the manifest say by whom and when (a pull request that sets them).</summary>
    public const string Approved = "approved";
}

/// <summary>
/// One text as the guest page shows it: the <paramref name="Version"/> the consent will refer to, the <paramref name="Language"/> of the file that was served
/// (the one asked for, or the closest the registry has), whether it is a draft, and the text as sanitized HTML (headings, paragraphs and lists only).
/// </summary>
public sealed record CheckInLegalText(string Kind, string Version, string Language, bool IsDraft, string ContentHtml);

/// <summary>
/// A version of a text the registry serves: <paramref name="TextHash"/> is the SHA-256 (hex, lower case) of its reference text, the Italian file, that the consent
/// keeps with the version (<c>GuestCheckInSession.BiometricConsentTextHash</c>).
/// </summary>
public sealed record CheckInLegalVersion(string Kind, string Version, bool IsDraft, string TextHash);
