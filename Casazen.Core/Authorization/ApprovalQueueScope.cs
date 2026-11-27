using Casazen.Core.Entities;

namespace Casazen.Core.Authorization;

/// <summary>
/// The reach of the caller on the items of the approval queue (E05/F04/US01/T02, NF-68, AM-03): the one way a read or a decision narrows them, next to the
/// <c>InScope</c> of the other host lists (<see cref="HostScopeQueryExtensions"/>), in a file of its own.
/// </summary>
public static class ApprovalQueueScope
{
    /// <summary>
    /// The items of the scope. An org-wide scope gets the query as it is. A restricted scope (an account that reaches only the properties it created, a
    /// collaborator «Solo alcuni») gets the items of a property it reaches, and <b>never</b> an item with no property (a campaign, a conversation that is
    /// not mapped to a property): such an item is the org's, not the property's, as the To do items and the conversations of the channels are. The
    /// reach is the same rule of every list (<c>InScope</c> on the properties of the org), as one <c>IN (SELECT ...)</c> in the same statement; the
    /// set of properties is never loaded to be filtered in memory. <paramref name="properties"/> is where the properties are read from (the
    /// <c>Properties</c> of the caller's context, so a deleted property is not one the caller reaches). The org boundary is not applied here: every caller
    /// still filters by <see cref="HostScope.OrgId"/>.
    /// </summary>
    public static IQueryable<ApprovalQueueItem> InScope(this IQueryable<ApprovalQueueItem> source, HostScope scope, IQueryable<Property> properties)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(properties);

        if (scope.IsOrgWide)
            return source;

        var orgId = scope.OrgId;
        var reached = properties.Where(property => property.OrgId == orgId).InScope(scope).Select(property => property.Id);
        return source.Where(item => item.PropertyId != null && reached.Contains(item.PropertyId.Value));
    }
}
