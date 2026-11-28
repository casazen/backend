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
    Task<IReadOnlyList<OrgSiteDocumentState>> GetStatesAsync(Guid orgId, CancellationToken cancellationToken = default);

    Task<OrgSiteDocument?> GetVersionAsync(
        Guid orgId,
        OrgSiteDocumentKind kind,
        int version,
        CancellationToken cancellationToken = default);

    Task<OrgSiteDocument> PublishAsync(
        Guid orgId,
        OrgSiteDocumentKind kind,
        OrgSiteDocumentInput input,
        string? publishedByUserId,
        CancellationToken cancellationToken = default);

    Task<bool> WithdrawAsync(Guid orgId, OrgSiteDocumentKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// The version shown on the public site (anonymous read): the current version unless it was withdrawn, otherwise
    /// <c>null</c>. The text is sanitized again on read.
    /// </summary>
    Task<OrgSiteDocument?> GetPublishedAsync(Guid orgId, OrgSiteDocumentKind kind, CancellationToken cancellationToken = default);
}

public sealed record OrgSiteDocumentInput(OrgSiteDocumentSource Source, string? Content, string? ExternalUrl);

public sealed record OrgSiteDocumentState(
    OrgSiteDocumentKind Kind,
    OrgSiteDocument? Current,
    IReadOnlyList<OrgSiteDocument> History);
