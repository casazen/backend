using Casazen.Core.Entities;
using Casazen.Core.SiteDocuments;

namespace Casazen.Core.Services;

/// <summary>
/// The operator documents of an org's public booking site (BK-14, A3-21): privacy notice and booking terms, written by
/// the host, versioned, shown on <c>/book/{slug}/privacy</c> and <c>/book/{slug}/termini</c>. Methods throw
/// <c>DomainRuleException</c> (codes in <see cref="OrgSiteDocumentRules"/>) for an invalid input; nothing is changed
/// then. CasaZen never supplies a text: a kind without a published version is reported as not published.
/// </summary>
public interface IOrgSiteDocumentService
{
    /// <summary>The state of every kind for the org: the current version and the most recent versions, newest first.</summary>
    Task<IReadOnlyList<OrgSiteDocumentState>> GetStatesAsync(Guid orgId, CancellationToken cancellationToken = default);

    /// <summary>One version of the org's document (with its text), or <c>null</c> when it does not exist.</summary>
    Task<OrgSiteDocument?> GetVersionAsync(
        Guid orgId,
        OrgSiteDocumentKind kind,
        int version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Publishes a new version of the document (it is shown right away). Publishing exactly what is already shown
    /// changes nothing and returns the current version. A withdrawn document is published again as a new version.
    /// </summary>
    Task<OrgSiteDocument> PublishAsync(
        Guid orgId,
        OrgSiteDocumentKind kind,
        OrgSiteDocumentInput input,
        string? publishedByUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Stops showing the current version: the site says the operator has not published the document. False when nothing was shown.</summary>
    Task<bool> WithdrawAsync(Guid orgId, OrgSiteDocumentKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// The version shown on the public site (anonymous read): the current version unless it was withdrawn, otherwise
    /// <c>null</c>. The text is sanitized again on read.
    /// </summary>
    Task<OrgSiteDocument?> GetPublishedAsync(Guid orgId, OrgSiteDocumentKind kind, CancellationToken cancellationToken = default);
}

/// <summary>What the operator submits: a text or an address, according to <see cref="Source"/> (the other field is ignored).</summary>
public sealed record OrgSiteDocumentInput(OrgSiteDocumentSource Source, string? Content, string? ExternalUrl);

/// <summary>A kind of document of an org: its current version (null when never published) and the recent history.</summary>
public sealed record OrgSiteDocumentState(
    OrgSiteDocumentKind Kind,
    OrgSiteDocument? Current,
    IReadOnlyList<OrgSiteDocument> History);
