using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Infrastructure.Services;

/// <summary>The state a card shows (E09/F01/US01/T03): its status, whose it is, and how many properties have it on.</summary>
internal readonly record struct MarketplaceCardState(MarketplaceCardStatus Status, MarketplaceStatusScope Scope, int? PropertyCount);

/// <summary>
/// The activations of one org, read once, from which every card of a catalog (or one detail card) takes its state. Only the org's own rows
/// are read, whatever the tenant filter says: the activations of another org are never looked at. The rows of a property are those of the
/// properties inside the caller's scope (a collaborator limited to some properties counts only those); the org-level rows (a partner, a licensed
/// function) belong to the org and are read whole.
/// </summary>
internal sealed class MarketplaceCardStates
{
    private readonly ILookup<(MarketplaceActivationKind, string), ActivationRow> _rows;
    private readonly Guid? _propertyId;

    private MarketplaceCardStates(ILookup<(MarketplaceActivationKind, string), ActivationRow> rows, Guid? propertyId)
    {
        _rows = rows;
        _propertyId = propertyId;
    }

    public static async Task<MarketplaceCardStates> LoadAsync(
        AppDbContext db, Guid orgId, HostScope scope, Guid? propertyId, CancellationToken ct)
    {
        var reachable = db.Properties.Where(p => p.OrgId == orgId).InScope(scope).Select(p => p.Id);
        var rows = await db.MarketplaceActivations.AsNoTracking()
            .Where(a => a.OrgId == orgId && (a.PropertyId == null || reachable.Contains(a.PropertyId.Value)))
            .Select(a => new ActivationRow(a.Kind, a.TargetCode, a.PropertyId, a.Status))
            .ToListAsync(ct);
        return new MarketplaceCardStates(rows.ToLookup(row => (row.Kind, row.TargetCode)), propertyId);
    }

    /// <summary>
    /// The state of the card of <paramref name="kind"/> and <paramref name="code"/>. A licensed function and a partner are the org's. A channel
    /// and a tool are a property's when the catalog is asked about a property; asked without one, the card is Active when any property of the
    /// scope has it on, with how many, otherwise To fix, Pending or Not active (<see cref="MarketplaceCardRules.Summarize"/>).
    /// </summary>
    public MarketplaceCardState Of(MarketplaceActivationKind kind, string code)
    {
        var rows = _rows[(kind, code)].ToList();
        if (kind is MarketplaceActivationKind.LicensedFunction or MarketplaceActivationKind.Partner)
        {
            var orgRow = rows.FirstOrDefault(row => row.PropertyId is null);
            var status = orgRow is null ? MarketplaceCardStatus.NotActive : MarketplaceCardRules.CardStatusOf(orgRow.Status);
            return new MarketplaceCardState(status, MarketplaceStatusScope.Org, null);
        }

        if (_propertyId is { } propertyId)
        {
            var propertyRow = rows.FirstOrDefault(row => row.PropertyId == propertyId);
            var status = propertyRow is null ? MarketplaceCardStatus.NotActive : MarketplaceCardRules.CardStatusOf(propertyRow.Status);
            return new MarketplaceCardState(status, MarketplaceStatusScope.Property, null);
        }

        var ofProperties = rows.Where(row => row.PropertyId is not null).ToList();
        var summary = MarketplaceCardRules.Summarize(ofProperties.Select(row => MarketplaceCardRules.CardStatusOf(row.Status)));
        var active = ofProperties.Count(row => row.Status == MarketplaceActivationStatus.Active);
        return new MarketplaceCardState(
            summary, MarketplaceStatusScope.Org, summary == MarketplaceCardStatus.Active ? active : null);
    }

    private sealed record ActivationRow(MarketplaceActivationKind Kind, string TargetCode, Guid? PropertyId, MarketplaceActivationStatus Status);
}
