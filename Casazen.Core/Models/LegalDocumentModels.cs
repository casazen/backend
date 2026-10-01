namespace Casazen.Core.Models;

/// <summary>
/// The legal documents a host accepts during the onboarding. The drafts of the texts were written by an AI agent on the
/// product owner's behalf (D14, updated 2026-10-01) and need a lawyer's review before production use.
/// </summary>
public enum LegalDocumentKind
{
    Tos,
    Privacy,
    Dpa,
}

/// <param name="Version">Version the consent records refer to (<c>Legal:Documents:{Kind}:Version</c>).</param>
/// <param name="EffectiveAt">Date the version is in force (<c>Legal:Documents:{Kind}:EffectiveAt</c>); null while not configured.</param>
/// <param name="DocumentUrl">Optional external copy of the official text (<c>Legal:Documents:{Kind}:DocumentUrl</c>, https only).</param>
public record LegalDocumentMeta(string Version, DateTime? EffectiveAt, string Title, string Summary, string? DocumentUrl);

/// <summary>Sanitized HTML text of a document version, in <paramref name="Language"/> (the requested one or Italian).</summary>
public record LegalDocumentText(string Language, string Html);

/// <param name="TransferMechanism">Legal basis of a transfer outside the EEA, when there is one (GDPR chapter V).</param>
/// <param name="DetailsPending">Legal entity, location or transfer basis still to be completed (never invented by code).</param>
/// <param name="Key">Stable identifier of the provider (e.g. <c>auth0</c>).</param>
/// <param name="PurposeKey">Stable identifier of the purpose, localized by the clients; null when the purpose comes from configuration.</param>
/// <param name="Entity">Legal entity and registered office of the provider, as configured by the product owner.</param>
public record SubprocessorItem(
    string Name,
    string Purpose,
    string Region,
    string? Website,
    string? TransferMechanism = null,
    bool DetailsPending = false,
    string? Key = null,
    string? PurposeKey = null,
    string? Entity = null);

public record SubprocessorsDocument(string Version, DateTime? EffectiveAt, IReadOnlyList<SubprocessorItem> Items);

/// <summary>
/// Whether the text of the configured version of a document can be published (LEGAL-TEXTS, decision D9). A text is
/// published only when its file exists for the configured version <b>and</b> every value it needs is configured
/// (fail-closed): otherwise the public page says "in preparation" and the health check and the startup log say why.
/// </summary>
/// <param name="HasText">The Italian text of the version is ready to be served.</param>
/// <param name="HasExternalCopy">An external official copy is configured (<c>Legal:Documents:{Kind}:DocumentUrl</c>).</param>
/// <param name="TextFileFound">A file of the configured version exists (Italian, the reference language).</param>
/// <param name="MissingConfiguration">Railway variables that keep a text of the version from being published, sorted; names only.</param>
/// <param name="Problems">Anything else wrong with the texts of the version (unknown placeholders, an English text that cannot be published, an invalid version name).</param>
public record LegalDocumentPublication(
    LegalDocumentKind Kind,
    string Version,
    bool HasText,
    bool HasExternalCopy,
    bool TextFileFound,
    IReadOnlyList<string> MissingConfiguration,
    IReadOnlyList<string> Problems)
{
    /// <summary>
    /// The page shows an official copy, or the Italian text is ready and nothing is wrong with any text of the version
    /// (a translation that cannot be published counts: its reader would silently get the Italian text).
    /// </summary>
    public bool IsPublished => HasExternalCopy || (HasText && MissingConfiguration.Count == 0 && Problems.Count == 0);

    /// <summary>One line for the logs and the health check: what is missing, never a value.</summary>
    public string Describe()
    {
        var reasons = new List<string>();
        if (!TextFileFound && !HasExternalCopy)
            reasons.Add("no text file for this version");
        if (MissingConfiguration.Count > 0)
            reasons.Add($"missing or invalid {string.Join(", ", MissingConfiguration)}");
        reasons.AddRange(Problems);
        return $"{Kind} {Version}: {string.Join("; ", reasons)}";
    }
}
