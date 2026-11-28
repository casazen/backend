using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;

namespace Casazen.Core.Services;

public enum LongRentPropertyView
{
    All = 0,

    Rented = 1,

    Free = 2,
}

public sealed record LongRentPropertyQuery(LongRentPropertyView View = LongRentPropertyView.All, string? Search = null)
{
    public const int MaxSearchLength = 100;

    public string? NormalizedSearch
    {
        get
        {
            var text = Search?.Trim();
            if (string.IsNullOrEmpty(text))
                return null;
            return text.Length > MaxSearchLength ? text[..MaxSearchLength].TrimEnd() : text;
        }
    }
}

public enum LongRentPropertyStatus
{
    Free,
    Rented,
    InPreparation,
}

/// <summary>
/// What the APE of the property says, the way the contract needs it (the rule of the guided draft): no certificate on file, a certificate
/// whose code and energy class are not on record (the landlord confirms them by typing them), or a certificate with both. The content of the
/// file is read when it is uploaded and again when the lease is made, never for a list. Serialized by name.
/// </summary>
public enum LongRentApeStatus
{
    Missing,
    ToConfirm,
    Identified,
}

/// <summary>
/// The lease that holds the property now. <c>FreeFrom</c> is the first day the property is free again when the lease does not go on (a notice
/// or an early termination is recorded, or its type does not renew: <see cref="LeaseRenewal.Renews"/>), otherwise <c>null</c>: a lease that
/// nobody gave notice of renews by itself (LR-14) and the property stays let. No name and no amount: those are in the lease list.
/// </summary>
public sealed record LongRentPropertyLease(Guid LeaseId, LeaseStage? Stage, DateTime CurrentTermEndDate, DateTime? NoticeOn, DateOnly? FreeFrom);

public sealed record LongRentPreparingLease(Guid LeaseId, LeaseStage? Stage);

public sealed record LongRentScheduledModeChange(RentalMode ToMode, DateTime EffectiveDate);

public sealed record LongRentPropertyRow(
    Guid Id,
    string Name,
    string Address,
    string City,
    string? Unit,
    LongRentApeStatus Ape,
    bool ApeValid,
    string? ApeClass,
    bool CadastralComplete,
    LongRentPropertyStatus? Status,
    LongRentPropertyLease? CurrentLease,
    LongRentPreparingLease? PreparingLease,
    LongRentScheduledModeChange? ScheduledModeChange);

/// <summary>
/// The properties of the long-term area with their state (LR-07a): the APE and the cadastral data the lease needs, who holds the property
/// and until when ("then free"), the lease that is coming, and the change of mode that is programmed. Read only, restricted in SQL to the
/// caller's org and, unless org-wide, to the properties they reach (<see cref="HostScope"/>); a fixed number of statements whatever the
/// number of properties.
/// </summary>
public interface ILongRentPropertyService
{
    Task<IReadOnlyList<LongRentPropertyRow>> GetPropertiesAsync(
        HostScope scope, LongRentPropertyQuery query, bool includeLeases, CancellationToken cancellationToken = default);
}
