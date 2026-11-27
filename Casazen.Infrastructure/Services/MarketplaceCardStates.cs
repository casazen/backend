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
/// properties inside the caller's scope (a collaborator limited to some properties counts only those); the org-level rows (a partner declared for
/// the whole org, and the rows of a licensed function written before its license was a property's) belong to the org and are read whole. A partner
/// may also have a row of a property (E09/F02/US03/T01): that one is read only when the property is inside the caller's scope.
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
    /// The state of the card of <paramref name="kind"/> and <paramref name="code"/>. A partner is the org's (<see cref="PartnerOf"/>). A channel, a tool
    /// and a licensed function (<see cref="LicensedFunctionOf"/>) are a property's when the catalog is asked about a property; asked without one, the
    /// card is Active when any property of the scope has it on, with how many, otherwise To fix, Pending or Not active
    /// (<see cref="MarketplaceCardRules.Summarize"/>).
    /// </summary>
    public MarketplaceCardState Of(MarketplaceActivationKind kind, string code)
    {
        var rows = _rows[(kind, code)].ToList();
        if (kind == MarketplaceActivationKind.LicensedFunction)
            return LicensedFunctionOf(rows);

        if (kind == MarketplaceActivationKind.Partner)
            return PartnerOf(rows);

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

    /// <summary>
    /// A licensed function is a property's, because the license is the accommodation's (D14, E09/F02/US02/T01): asked about a property, the card shows
    /// that property's row (<see cref="MarketplaceStatusScope.Property"/>); asked without one, Active when any property of the caller's scope has it,
    /// otherwise To fix, Pending or Not active (<see cref="MarketplaceCardRules.Summarize"/>). A row of the org (no property), which no code writes
    /// for a licensed function any more, still counts: for the property that has no row of its own, and in the summary.
    /// </summary>
    private MarketplaceCardState LicensedFunctionOf(List<ActivationRow> rows)
    {
        if (_propertyId is { } propertyId)
        {
            var row = rows.FirstOrDefault(r => r.PropertyId == propertyId) ?? rows.FirstOrDefault(r => r.PropertyId is null);
            var status = row is null ? MarketplaceCardStatus.NotActive : MarketplaceCardRules.CardStatusOf(row.Status);
            return new MarketplaceCardState(status, MarketplaceStatusScope.Property, null);
        }

        var summary = MarketplaceCardRules.Summarize(rows.Select(r => MarketplaceCardRules.CardStatusOf(r.Status)));
        return new MarketplaceCardState(summary, MarketplaceStatusScope.Org, null);
    }

    /// <summary>
    /// A partner is activated on its own site for the whole org (its org-level row), and the host may also declare it for one property (E09/F02/US03/T01):
    /// the card counts the org's row and, asked about a property, that property's row, or, asked without one, the rows of the properties inside the caller's
    /// scope. Active when any of them is, otherwise To fix, Pending or Not active (<see cref="MarketplaceCardRules.Summarize"/>). The scope of the status
    /// stays the org's: it is the partner's card, not a property's. With only the org's row this is the status of that row, as it always was.
    /// </summary>
    private MarketplaceCardState PartnerOf(List<ActivationRow> rows)
    {
        var counted = rows.Where(r => r.PropertyId is null || _propertyId is null || r.PropertyId == _propertyId);
        var summary = MarketplaceCardRules.Summarize(counted.Select(r => MarketplaceCardRules.CardStatusOf(r.Status)));
        return new MarketplaceCardState(summary, MarketplaceStatusScope.Org, null);
    }

    private sealed record ActivationRow(MarketplaceActivationKind Kind, string TargetCode, Guid? PropertyId, MarketplaceActivationStatus Status);
}
