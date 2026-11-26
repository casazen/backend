using System.Net;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

public sealed partial class MarketingStudio
{
    private async Task SetDestinationAsync(MarketingAsset asset, StudioSettings settings, Property property, CancellationToken ct)
    {
        var external = settings.PropertyUrls.GetValueOrDefault(property.Id);
        asset.ExternalDestination = external != null;
        if (external != null)
        {
            asset.Destination = external;
            return;
        }
        var org = await db.Orgs.SingleAsync(o => o.Id == asset.OrgId, ct);
        asset.Destination = sites.TryPropertyUrl(org, property.Slug ?? property.Id.ToString());
    }

    private string WithPropertyLink(MarketingAsset asset, string html) =>
        TrackingUrl(asset) is { } url ? html + $"<p><a href=\"{WebUtility.HtmlEncode(url)}\">Scopri la struttura</a></p>" : html;

    public async Task<Campaign> CreatePropertyDraftAsync(HostScope scope, Guid propertyId, Guid segmentId, string subject,
        string html, bool aiGenerated, CancellationToken ct)
    {
        var property = await db.Properties.Where(p => p.OrgId == scope.OrgId).InScope(scope).SingleOrDefaultAsync(p => p.Id == propertyId, ct)
            ?? throw Invalid("property_not_found");
        var asset = new MarketingAsset { OrgId = scope.OrgId, PropertyId = propertyId, SegmentId = segmentId,
            Channel = "email", Subject = subject, Body = html };
        await SetDestinationAsync(asset, await SettingsAsync(scope, ct), property, ct);
        await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
        var campaign = await campaigns.CreateDraftAsync(scope.OrgId, segmentId, subject, WithPropertyLink(asset, html), aiGenerated, ct, propertyId);
        asset.CampaignId = campaign.Id;
        db.MarketingAssets.Add(asset);
        await db.SaveChangesAsync(ct);
        if (transaction != null) await transaction.CommitAsync(ct);
        return campaign;
    }
}
