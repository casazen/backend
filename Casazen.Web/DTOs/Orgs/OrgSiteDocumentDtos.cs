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

    /// <summary>The text as written (<see cref="OrgSiteDocumentSource.Text"/>); null in the history list.</summary>
    public string? Content { get; set; }

    /// <summary>Absolute https address of the document (<see cref="OrgSiteDocumentSource.ExternalUrl"/>).</summary>
    public string? ExternalUrl { get; set; }

    public DateTime PublishedAt { get; set; }

    /// <summary>When the operator withdrew it; null while it is shown.</summary>
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

/// <summary>A kind of document: what the public site shows now and the recent versions.</summary>
public class OrgSiteDocumentStateDto
{
    /// <summary><c>privacy</c> or <c>terms</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>True when the public site shows the document: there is a current version and it was not withdrawn.</summary>
    public bool Published { get; set; }

    /// <summary>The current (highest) version with its text; null when the document was never published.</summary>
    public OrgSiteDocumentVersionDto? Current { get; set; }

    /// <summary>The most recent versions, newest first, without their text.</summary>
    public List<OrgSiteDocumentVersionDto> History { get; set; } = [];

    public static OrgSiteDocumentStateDto From(OrgSiteDocumentState state) => new()
    {
        Kind = OrgSiteDocumentRules.RouteName(state.Kind),
        Published = state.Current is { WithdrawnAt: null },
        Current = state.Current is null ? null : OrgSiteDocumentVersionDto.From(state.Current, includeContent: true),
        History = state.History.Select(d => OrgSiteDocumentVersionDto.From(d, includeContent: false)).ToList(),
    };
}

/// <summary>
/// Body of <c>PUT /api/orgs/me/site-documents/{kind}</c>: the new version. The annotations only bound the input; the
/// rules (not empty, no HTML, allowed links, https address) are <see cref="OrgSiteDocumentRules"/>. Error messages are
/// keys of <c>Resources/SharedResources.resx</c>.
/// </summary>
public class PublishOrgSiteDocumentDto
{
    [Required(ErrorMessage = "OrgDocumentSourceInvalid")]
    public OrgSiteDocumentSource? Source { get; set; }

    /// <summary>The text, for <see cref="OrgSiteDocumentSource.Text"/>: plain text with a few Markdown marks.</summary>
    [MaxLength(OrgSiteDocumentRules.ContentMaxLength * 2, ErrorMessage = "OrgDocumentContentTooLongInput")]
    public string? Content { get; set; }

    /// <summary>The https address, for <see cref="OrgSiteDocumentSource.ExternalUrl"/>.</summary>
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
    /// <summary><c>privacy</c> or <c>terms</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    public bool Published { get; set; }

    public int? Version { get; set; }

    public OrgSiteDocumentSource? Source { get; set; }

    /// <summary>Sanitized HTML of the text (<see cref="OrgSiteDocumentSource.Text"/>).</summary>
    public string? ContentHtml { get; set; }

    /// <summary>Absolute https address of the document (<see cref="OrgSiteDocumentSource.ExternalUrl"/>).</summary>
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
