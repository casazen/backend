namespace Casazen.Core.Models;

/// <summary>The legal documents a host accepts during the onboarding (texts provided by the product owner, D14).</summary>
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
