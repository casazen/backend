using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Web.DTOs.Owners;

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

public sealed record DeclareExternalUnitsRequest
{
    public int? Units { get; init; }
}
