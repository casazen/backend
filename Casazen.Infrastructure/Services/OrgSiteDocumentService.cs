using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.SiteDocuments;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc />
/// <remarks>
/// Versions are immutable rows numbered per org and kind; the unique index on (org, kind, version) backs the numbering
/// under concurrency: a parallel publish loses on 23505, re-reads and takes the next number. Logs name the org, kind
/// and version, never the text.
/// </remarks>
public sealed class OrgSiteDocumentService(
    AppDbContext db,
    ILogger<OrgSiteDocumentService> logger,
    TimeProvider? timeProvider = null) : IOrgSiteDocumentService
{
    /// <summary>Versions listed in the history of a kind; older ones stay stored and reachable by number.</summary>
    internal const int HistoryLimit = 50;

    /// <summary>Attempts at a version number before giving up on concurrent publishes.</summary>
    private const int MaxPublishAttempts = 5;

    private static readonly OrgSiteDocumentKind[] Kinds = Enum.GetValues<OrgSiteDocumentKind>();

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<OrgSiteDocumentState>> GetStatesAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var states = new List<OrgSiteDocumentState>(Kinds.Length);
        foreach (var kind in Kinds)
        {
            // The text is not needed for the history list: the current version is read in full below.
            var history = await db.OrgSiteDocuments
                .AsNoTracking()
                .Where(d => d.OrgId == orgId && d.Kind == kind)
                .OrderByDescending(d => d.Version)
                .Take(HistoryLimit)
                .Select(d => new OrgSiteDocument
                {
                    Id = d.Id,
                    OrgId = d.OrgId,
                    Kind = d.Kind,
                    Version = d.Version,
                    Source = d.Source,
                    ExternalUrl = d.ExternalUrl,
                    PublishedAt = d.PublishedAt,
                    PublishedByUserId = d.PublishedByUserId,
                    WithdrawnAt = d.WithdrawnAt,
                })
                .ToListAsync(cancellationToken);

            OrgSiteDocument? current = null;
            if (history.Count > 0)
            {
                var currentVersion = history[0].Version;
                current = await db.OrgSiteDocuments
                    .AsNoTracking()
                    .SingleAsync(d => d.OrgId == orgId && d.Kind == kind && d.Version == currentVersion, cancellationToken);
            }

            states.Add(new OrgSiteDocumentState(kind, current, history));
        }

        return states;
    }

    public Task<OrgSiteDocument?> GetVersionAsync(
        Guid orgId,
        OrgSiteDocumentKind kind,
        int version,
        CancellationToken cancellationToken = default) =>
        db.OrgSiteDocuments
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.OrgId == orgId && d.Kind == kind && d.Version == version, cancellationToken);

    public async Task<OrgSiteDocument> PublishAsync(
        Guid orgId,
        OrgSiteDocumentKind kind,
        OrgSiteDocumentInput input,
        string? publishedByUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        // Validate and render before touching the database: an invalid input changes nothing.
        var (content, contentHtml, externalUrl) = Prepare(input);

        for (var attempt = 1; ; attempt++)
        {
            var latest = await db.OrgSiteDocuments
                .AsNoTracking()
                .Where(d => d.OrgId == orgId && d.Kind == kind)
                .OrderByDescending(d => d.Version)
                .FirstOrDefaultAsync(cancellationToken);

            if (latest is { WithdrawnAt: null }
                && latest.Source == input.Source
                && latest.Content == content
                && latest.ExternalUrl == externalUrl)
            {
                return latest;
            }

            var document = new OrgSiteDocument
            {
                OrgId = orgId,
                Kind = kind,
                Version = (latest?.Version ?? 0) + 1,
                Source = input.Source,
                Content = content,
                ContentHtml = contentHtml,
                ExternalUrl = externalUrl,
                PublishedAt = _clock.GetUtcNow().UtcDateTime,
                PublishedByUserId = publishedByUserId,
            };
            db.OrgSiteDocuments.Add(document);

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (
                ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
                && attempt < MaxPublishAttempts)
            {
                // A parallel publish took this number: detach and take the next one.
                db.Entry(document).State = EntityState.Detached;
                continue;
            }

            logger.LogInformation(
                "Site document {Kind} of org {OrgId} published as version {Version} ({Source})",
                kind, orgId, document.Version, document.Source);
            return document;
        }
    }

    public async Task<bool> WithdrawAsync(Guid orgId, OrgSiteDocumentKind kind, CancellationToken cancellationToken = default)
    {
        var current = await db.OrgSiteDocuments
            .Where(d => d.OrgId == orgId && d.Kind == kind)
            .OrderByDescending(d => d.Version)
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null || current.WithdrawnAt is not null)
            return false;

        current.WithdrawnAt = _clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Site document {Kind} of org {OrgId} withdrawn (version {Version})", kind, orgId, current.Version);
        return true;
    }

    public async Task<OrgSiteDocument?> GetPublishedAsync(Guid orgId, OrgSiteDocumentKind kind, CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters: anonymous read of a public site by an org id found from its public slug. A signed-in host
        // looking at another org's site must see that org's document, not an empty one under their own tenant.
        var current = await db.OrgSiteDocuments
            .IgnoreQueryFilters([AppDbContext.TenantQueryFilter])
            .AsNoTracking()
            .Where(d => d.OrgId == orgId && d.Kind == kind)
            .OrderByDescending(d => d.Version)
            .FirstOrDefaultAsync(cancellationToken);
        if (current is null || current.WithdrawnAt is not null)
            return null;

        // Sanitized again on read (as the SEO content, FD-15): a row written by other means never reaches a page unsanitized.
        current.ContentHtml = SeoHtmlSanitizer.Sanitize(current.ContentHtml);
        return current;
    }

    /// <summary>Validated text, its sanitized HTML and the normalized address of an input; only the field of its source is kept.</summary>
    private static (string? Content, string? ContentHtml, string? ExternalUrl) Prepare(OrgSiteDocumentInput input) =>
        input.Source switch
        {
            OrgSiteDocumentSource.Text => PrepareText(input.Content),
            OrgSiteDocumentSource.ExternalUrl => (null, null, OrgSiteDocumentRules.NormalizeExternalUrl(input.ExternalUrl)),
            _ => throw new DomainRuleException(OrgSiteDocumentRules.UrlInvalidCode, "OrgDocumentUrlInvalid"),
        };

    private static (string? Content, string? ContentHtml, string? ExternalUrl) PrepareText(string? rawContent)
    {
        var content = OrgSiteDocumentRules.NormalizeContent(rawContent);
        var html = SeoHtmlSanitizer.Sanitize(OrgSiteDocumentRules.ToHtml(content));
        return (content, html, null);
    }
}
