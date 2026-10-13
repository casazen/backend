using System.Linq.Expressions;
using Casazen.Core.Entities;

namespace Casazen.Core.Authorization;

/// <summary>
/// The one way a list query applies the caller's reach on properties (AM-03, replaces the thirteen hand-written
/// <c>scope.OwnerId</c> filters of TN-3). <c>query.InScope(scope)</c> keeps the rows bound to a property the scope reaches:
/// nothing for an org-wide scope, <c>Property.OwnerId = …</c> for a legacy account, and an <c>EXISTS</c> on
/// <see cref="PropertyMemberAccess"/> for a member «Solo alcuni». Always one query: the predicate is part of the SQL, the
/// set of properties is never loaded to be filtered in memory and there is no lookup per row.
/// </summary>
/// <remarks>
/// <para>The org boundary is not applied here: every caller still filters by <see cref="HostScope.OrgId"/> (and the tenant
/// query filter applies underneath). A row with no property (an org-level service request) is outside every restricted scope,
/// as it always was for the owner filter.</para>
/// <para>Not a second global query filter (rejected): a global filter would also apply to the jobs, which run for no user,
/// and would travel through every <c>Include</c>. This one is explicit, at the query that lists.</para>
/// </remarks>
public static class HostScopeQueryExtensions
{
    /// <summary>The properties of the scope.</summary>
    public static IQueryable<Property> InScope(this IQueryable<Property> source, HostScope scope) =>
        source.InScope(scope, property => property);

    /// <summary>The bookings of the scope (those of a property it reaches).</summary>
    public static IQueryable<Booking> InScope(this IQueryable<Booking> source, HostScope scope) =>
        source.InScope(scope, booking => booking.Property);

    /// <summary>The leases of the scope.</summary>
    public static IQueryable<LeaseContract> InScope(this IQueryable<LeaseContract> source, HostScope scope) =>
        source.InScope(scope, lease => lease.Property);

    /// <summary>The payments of the scope (the property of their booking).</summary>
    public static IQueryable<Payment> InScope(this IQueryable<Payment> source, HostScope scope) =>
        source.InScope(scope, payment => payment.Booking.Property);

    /// <summary>The service requests of the scope; a request with no property is never in a restricted scope.</summary>
    public static IQueryable<ServiceRequest> InScope(this IQueryable<ServiceRequest> source, HostScope scope) =>
        source.InScope(scope, request => request.Property);

    /// <summary>The iCal feeds of the scope.</summary>
    public static IQueryable<PropertyICalFeed> InScope(this IQueryable<PropertyICalFeed> source, HostScope scope) =>
        source.InScope(scope, feed => feed.Property);

    /// <summary>The check-out records of the scope (the property of their booking).</summary>
    public static IQueryable<StayCheckout> InScope(this IQueryable<StayCheckout> source, HostScope scope) =>
        source.InScope(scope, checkout => checkout.Booking.Property);

    /// <summary>
    /// The rows of <paramref name="source"/> bound to a property the scope reaches, the property being what
    /// <paramref name="property"/> selects. For an org-wide scope the query is returned as it is.
    /// </summary>
    public static IQueryable<T> InScope<T>(
        this IQueryable<T> source,
        HostScope scope,
        Expression<Func<T, Property?>> property)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(property);

        if (scope.IsOrgWide)
            return source;

        // The predicate on a Property, then moved onto the selected property: EF translates the result as if it were
        // written by hand (a join to the property and an EXISTS), which is what the tests of the SQL check.
        var predicate = PropertyPredicate(scope);
        var body = new ReplaceParameter(predicate.Parameters[0], property.Body).Visit(predicate.Body)!;
        return source.Where(Expression.Lambda<Func<T, bool>>(body, property.Parameters));
    }

    /// <summary>The predicate a property must satisfy to be in the scope (the one rule every <c>InScope</c> applies).</summary>
    private static Expression<Func<Property, bool>> PropertyPredicate(HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var ownerId = scope.OwnerId;
        var memberId = scope.GrantedToUserId;

        if (ownerId is not null && memberId is not null)
            return p => p.OwnerId == ownerId && p.MemberAccesses.Any(a => a.UserId == memberId);
        if (ownerId is not null)
            return p => p.OwnerId == ownerId;
        if (memberId is not null)
            return p => p.MemberAccesses.Any(a => a.UserId == memberId);

        return p => true;
    }

    private sealed class ReplaceParameter(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == parameter ? replacement : base.VisitParameter(node);
    }
}
