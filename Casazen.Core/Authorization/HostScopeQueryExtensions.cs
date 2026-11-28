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
    public static IQueryable<Property> InScope(this IQueryable<Property> source, HostScope scope) =>
        source.InScope(scope, property => property);

    public static IQueryable<Booking> InScope(this IQueryable<Booking> source, HostScope scope) =>
        source.InScope(scope, booking => booking.Property);

    public static IQueryable<LeaseContract> InScope(this IQueryable<LeaseContract> source, HostScope scope) =>
        source.InScope(scope, lease => lease.Property);

    public static IQueryable<Payment> InScope(this IQueryable<Payment> source, HostScope scope) =>
        source.InScope(scope, payment => payment.Booking.Property);

    public static IQueryable<ServiceRequest> InScope(this IQueryable<ServiceRequest> source, HostScope scope) =>
        source.InScope(scope, request => request.Property);

    public static IQueryable<PropertyICalFeed> InScope(this IQueryable<PropertyICalFeed> source, HostScope scope) =>
        source.InScope(scope, feed => feed.Property);

    public static IQueryable<StayCheckout> InScope(this IQueryable<StayCheckout> source, HostScope scope) =>
        source.InScope(scope, checkout => checkout.Booking.Property);

    public static IQueryable<Review> InScope(this IQueryable<Review> source, HostScope scope) =>
        source.InScope(scope, review => review.Property);

    public static IQueryable<OtaReview> InScope(this IQueryable<OtaReview> source, HostScope scope) =>
        source.InScope(scope, review => review.Property);

    public static IQueryable<GuestThread> InScope(this IQueryable<GuestThread> source, HostScope scope) =>
        source.InScope(scope, thread => thread.Property);

    /// <summary>
    /// The conversations of the channels of the scope (those of a property it reaches, E05/F02/US01/T01, ADR-032-R6). A conversation whose listing has
    /// no confirmed mapping has no property (<c>PropertyId</c> null, OCT-04): it is outside every restricted scope, and inside the org-wide one only,
    /// so a collaborator «Solo alcuni» never sees it and it is never shown under a property it was not mapped to.
    /// </summary>
    public static IQueryable<OtaMessageThread> InScope(this IQueryable<OtaMessageThread> source, HostScope scope) =>
        source.InScope(scope, thread => thread.Property);

    public static IQueryable<InternalActivity> InScope(this IQueryable<InternalActivity> source, HostScope scope) =>
        source.InScope(scope, activity => activity.Property);

    /// <summary>
    /// The compliance proofs of the scope (E03/F02/US03/T01, NF-47): those whose every flat the scope reaches. A receipt of the Alloggiati communication
    /// may cover several flats (E20/F02/US01/T01) and the file is about all of them (the guests of each), so a collaborator «Solo alcuni» reads it only when it
    /// reaches the flat the proof was filed under <b>and</b> every flat it covers; one flat outside the reach keeps the whole proof out, whichever flat is
    /// asked for. As one join and one <c>NOT EXISTS</c> in the same statement; an org-wide scope returns the query as it is.
    /// </summary>
    public static IQueryable<ManualComplianceProof> InScope(this IQueryable<ManualComplianceProof> source, HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);

        if (scope.IsOrgWide)
            return source;

        return source
            .InScope(scope, proof => proof.Property)
            .Where(EveryCoveredFlatInReach(scope));
    }

    public static IQueryable<ComplianceSendLogEntry> InScope(this IQueryable<ComplianceSendLogEntry> source, HostScope scope) =>
        source.InScope(scope, entry => entry.Property);

    /// <summary>
    /// The guests of the scope (SR-04b). A guest belongs to the org and to no property, so it is reached through its stays: in a
    /// restricted scope it is one with a booking at a property the scope reaches (the rule of the bookings above, whatever the status
    /// of the booking). An org-wide scope returns the query as it is, so it also keeps the guests that have no stay yet (registered
    /// from the guest list); a restricted scope never reaches those. <paramref name="bookings"/> is where the stays are read from
    /// (the <c>Bookings</c> of the caller's context): they are a query of their own, so that the lambda holds a subquery EF can
    /// translate (<c>InScope</c> is not a LINQ operator), and the SQL is still one <c>EXISTS</c>.
    /// </summary>
    public static IQueryable<Guest> InScope(this IQueryable<Guest> source, HostScope scope, IQueryable<Booking> bookings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(bookings);

        if (scope.IsOrgWide)
            return source;

        var orgId = scope.OrgId;
        var stays = bookings.Where(booking => booking.OrgId == orgId).InScope(scope);
        return source.Where(guest => stays.Any(stay => stay.GuestId == guest.Id));
    }

    public static IQueryable<GuestCrmProfile> InScope(this IQueryable<GuestCrmProfile> source, HostScope scope, IQueryable<Booking> bookings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(bookings);

        if (scope.IsOrgWide)
            return source;

        var orgId = scope.OrgId;
        var stays = bookings.Where(booking => booking.OrgId == orgId).InScope(scope);
        return source.Where(profile => stays.Any(stay => stay.Guest.GuestCrmProfileId == profile.Id));
    }

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

        var predicate = PropertyPredicate(scope);
        var body = new ReplaceParameter(predicate.Parameters[0], property.Body).Visit(predicate.Body)!;
        return source.Where(Expression.Lambda<Func<T, bool>>(body, property.Parameters));
    }

    /// <summary>
    /// «Every flat the proof covers is in the scope»: the one predicate of the properties (<see cref="PropertyPredicate"/>) moved onto the flat of each line of
    /// <see cref="ManualComplianceProof.CoveredProperties"/> and required of all of them (<c>All</c>, which Npgsql turns into <c>NOT EXISTS</c> of a line
    /// that fails it). A proof with no line has nothing to fail: the flat it was filed under is judged by the caller.
    /// </summary>
    private static Expression<Func<ManualComplianceProof, bool>> EveryCoveredFlatInReach(HostScope scope)
    {
        var predicate = PropertyPredicate(scope);
        var proof = Expression.Parameter(typeof(ManualComplianceProof), "proof");
        var link = Expression.Parameter(typeof(ManualComplianceProofProperty), "link");
        var flat = Expression.Property(link, nameof(ManualComplianceProofProperty.Property));
        var inReach = Expression.Lambda<Func<ManualComplianceProofProperty, bool>>(
            new ReplaceParameter(predicate.Parameters[0], flat).Visit(predicate.Body)!,
            link);
        var all = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.All),
            [typeof(ManualComplianceProofProperty)],
            Expression.Property(proof, nameof(ManualComplianceProof.CoveredProperties)),
            inReach);
        return Expression.Lambda<Func<ManualComplianceProof, bool>>(all, proof);
    }

    private static Expression<Func<Property, bool>> PropertyPredicate(HostScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        var ownerId = scope.OwnerId;
        var memberId = scope.GrantedToUserId;

        if (ownerId is not null && memberId is not null)
            return p => p.OwnerId == ownerId && p.MemberAccesses.Any(a => a.UserId == memberId);
        if (ownerId is not null)
            return p => p.OwnerId == ownerId;

        if (memberId is not null && scope.GrantedMode is { } mode)
            return p => p.RentalMode == mode && p.MemberAccesses.Any(a => a.UserId == memberId);
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
