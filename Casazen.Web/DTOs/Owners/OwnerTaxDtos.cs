using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Owners;

/// <summary>
/// A decision about the regime of the stay of a company tenant (E18/F01/US03/T01): <c>POST api/owner-tax-decisions</c> answers the decision in force. <c>regime</c> is
/// <c>ShortLet</c> (the stay stays a short let: the intermediary withholds) or <c>NotShortLet</c>; <c>reason</c> is the note of the agency; <c>decidedByUserId</c> is who decided.
/// </summary>
public sealed record ShortLetTaxDecisionDto(
    Guid Id,
    Guid BookingId,
    ShortLetTaxRegime Regime,
    string Reason,
    DateTime DecidedAt,
    string DecidedByUserId)
{
    public static ShortLetTaxDecisionDto From(ShortLetTaxDecision d) => new(d.Id, d.BookingId, d.Regime, d.Reason, d.DecidedAt, d.DecidedByUserId);
}

/// <summary>
/// The limit of units of an owner (E18/F01/US03/T01, ⚖ I8): <c>GET api/owners/{ownerId}/tax-checks</c> and <c>PUT api/owners/{ownerId}/external-short-let-units</c>.
/// <c>managedUnits</c> are the apartments the agency manages for the owner, <c>externalUnits</c> the ones the owner declared outside the agency, <c>maxUnits</c> the limit
/// of a private person (<c>ShortStayFiscal:MaxApartmentsPerTaxpayer</c>); <c>presumedBusiness</c> is true when a private person is past it (<c>applies</c> is false for an
/// owner that is a business already). Counts only: no property, no name.
/// </summary>
public sealed record OwnerUnitsCheckDto(
    Guid OwnerId,
    bool Applies,
    int ManagedUnits,
    int ExternalUnits,
    int TotalUnits,
    int MaxUnits,
    bool PresumedBusiness)
{
    public static OwnerUnitsCheckDto From(OwnerUnitsCheck c) => new(c.OwnerId, c.Applies, c.ManagedUnits, c.ExternalUnits, c.TotalUnits, c.MaxUnits, c.PresumedBusiness);
}

/// <summary>
/// The body of <c>PUT api/owners/{ownerId}/external-short-let-units</c>: how many apartments the owner lets short outside the agency. Required: a request that does not carry
/// it (or carries a misspelt field) is 422, never a 0 by omission.
/// </summary>
public sealed record DeclareExternalUnitsRequest
{
    public int? Units { get; init; }
}
