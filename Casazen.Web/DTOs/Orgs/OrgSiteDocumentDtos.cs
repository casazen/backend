using System.ComponentModel.DataAnnotations;
using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.SiteDocuments;

namespace Casazen.Web.DTOs.Orgs;

/// <summary>
/// One version of an operator document as the org's administrator sees it (BK-14, A3-21). <see cref="Content"/> is the
/// text as written, only on the full read of a version (<c>GET .../versions/{version}</c> and the current version),
/// never in the history list.
/// </summary>
public class OrgSiteDocumentVersionDto
{
    public int Version { get; set; }

    public OrgSiteDocumentSource Source { get; set; }

    public string? Content { get; set; }

    public string? ExternalUrl { get; set; }

    public DateTime PublishedAt { get; set; }

    public DateTime? WithdrawnAt { get; set; }

    public static OrgSiteDocumentVersionDto From(OrgSiteDocument document, bool includeContent) => new()
    {
        Version = document.Version,
        Source = document.Source,
        Content = includeContent ? document.Content : null,
        ExternalUrl = document.ExternalUrl,
        PublishedAt = document.PublishedAt,
        WithdrawnAt = document.WithdrawnAt,
    };
}

public class OrgSiteDocumentStateDto
{
    public string Kind { get; set; } = string.Empty;

    public bool Published { get; set; }

    public OrgSiteDocumentVersionDto? Current { get; set; }

    public List<OrgSiteDocumentVersionDto> History { get; set; } = [];

    public static OrgSiteDocumentStateDto From(OrgSiteDocumentState state) => new()
    {
        Kind = OrgSiteDocumentRules.RouteName(state.Kind),
        Published = state.Current is { WithdrawnAt: null },
        Current = state.Current is null ? null : OrgSiteDocumentVersionDto.From(state.Current, includeContent: true),
        History = state.History.Select(d => OrgSiteDocumentVersionDto.From(d, includeContent: false)).ToList(),
    };
}

public class PublishOrgSiteDocumentDto
{
    [Required(ErrorMessage = "OrgDocumentSourceInvalid")]
    public OrgSiteDocumentSource? Source { get; set; }

    [MaxLength(OrgSiteDocumentRules.ContentMaxLength * 2, ErrorMessage = "OrgDocumentContentTooLongInput")]
    public string? Content { get; set; }

    [MaxLength(OrgSiteDocumentRules.ExternalUrlMaxLength * 2, ErrorMessage = "OrgDocumentUrlInvalid")]
    public string? ExternalUrl { get; set; }
}

/// <summary>
/// An operator document on the public site (<c>GET /api/public/orgs/{slug}/documents/{kind}</c>, anonymous). When the
/// operator has not published it, <see cref="Published"/> is false and nothing else is set: the site says so, it never
/// shows a text of CasaZen's in its place.
/// </summary>
public class PublicOrgDocumentDto
{
    public string Kind { get; set; } = string.Empty;

    public bool Published { get; set; }

    public int? Version { get; set; }

    public OrgSiteDocumentSource? Source { get; set; }

    public string? ContentHtml { get; set; }

    public string? ExternalUrl { get; set; }

    public DateTime? PublishedAt { get; set; }

    public static PublicOrgDocumentDto NotPublished(OrgSiteDocumentKind kind) => new()
    {
        Kind = OrgSiteDocumentRules.RouteName(kind),
        Published = false,
    };

    public static PublicOrgDocumentDto From(OrgSiteDocument document) => new()
    {
        Kind = OrgSiteDocumentRules.RouteName(document.Kind),
        Published = true,
        Version = document.Version,
        Source = document.Source,
        ContentHtml = document.Source == OrgSiteDocumentSource.Text ? document.ContentHtml : null,
        ExternalUrl = document.Source == OrgSiteDocumentSource.ExternalUrl ? document.ExternalUrl : null,
        PublishedAt = document.PublishedAt,
    };
}
