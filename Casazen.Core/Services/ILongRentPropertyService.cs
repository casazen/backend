using Casazen.Core.Authorization;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;

namespace Casazen.Core.Services;

/// <summary>Which properties of the long-term area the list holds: all of them, the let ones, or the ones that are not let.</summary>
public enum LongRentPropertyView
{
    /// <summary>Every property of the caller's reach (the default).</summary>
    All = 0,

    /// <summary>The properties a lease holds now (<see cref="LongRentPropertyStatus.Rented"/>).</summary>
    Rented = 1,

    /// <summary>The properties no lease holds: free, or with a lease still being prepared.</summary>
    Free = 2,
}

/// <summary>What the list asks for: a view and a text looked for, without regard to case, in the name, address, city and unit.</summary>
public sealed record LongRentPropertyQuery(LongRentPropertyView View = LongRentPropertyView.All, string? Search = null)
{
    /// <summary>Longest search text taken into account (a longer one is cut).</summary>
    public const int MaxSearchLength = 100;

    /// <summary>The search text trimmed and cut, or <c>null</c> when there is nothing to look for.</summary>
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

/// <summary>
/// Where a property of the long-term area stands. <see cref="Rented"/> has a lease in force (stage <c>Active</c> or <c>Expiring</c>: registered
/// and not over); <see cref="InPreparation"/> has none but a lease on its way (draft, in review, out for signature, signed and still to
/// register): it is free to let and the page says who is coming; <see cref="Free"/> has nothing. Serialized by name.
/// </summary>
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

/// <summary>The lease on its way of a property that has none in force: which one, and where it stands on the path.</summary>
public sealed record LongRentPreparingLease(Guid LeaseId, LeaseStage? Stage);

/// <summary>A change of rental mode programmed for the property (PM-02): the mode it goes to and its first day.</summary>
public sealed record LongRentScheduledModeChange(RentalMode ToMode, DateTime EffectiveDate);

/// <summary>
/// One property of the long-term area with what the lists of properties and leases do not say (LR-07a). <c>Status</c>, <c>CurrentLease</c>
/// and <c>PreparingLease</c> are <c>null</c> when the caller may not read the leases: nothing is guessed. <c>ApeValid</c> is an identified
/// certificate; <c>ApeClass</c> the energy class of the latest one that has it; <c>CadastralComplete</c> the rule of the contract (sheet,
/// parcel, category and income).
/// </summary>
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
    /// <summary>
    /// The active properties in long-term mode of the scope, ordered by name. <paramref name="includeLeases"/> is whether the caller may read
    /// the leases: without it nothing is said of them (status and leases are <c>null</c>, and a view other than <c>All</c> holds nothing).
    /// </summary>
    Task<IReadOnlyList<LongRentPropertyRow>> GetPropertiesAsync(
        HostScope scope, LongRentPropertyQuery query, bool includeLeases, CancellationToken cancellationToken = default);
}
