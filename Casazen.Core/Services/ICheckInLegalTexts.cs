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
    IReadOnlyList<CheckInLegalText> GetForStay(bool documentNotArchived, string? language);

    CheckInLegalVersion? GetActiveVersion(string kind);

    CheckInLegalVersion? FindServedVersion(string kind, string? version);
}

public static class CheckInLegalTextKinds
{
    public const string BiometricConsent = "biometric-consent";

    public const string TicinoNotice = "ticino-notice";

    public static IReadOnlyList<string> All { get; } = [BiometricConsent, TicinoNotice];

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}

public static class CheckInLegalTextStatuses
{
    public const string Draft = "draft";

    public const string Approved = "approved";
}

public sealed record CheckInLegalText(string Kind, string Version, string Language, bool IsDraft, string ContentHtml);

public sealed record CheckInLegalVersion(string Kind, string Version, bool IsDraft, string TextHash);
