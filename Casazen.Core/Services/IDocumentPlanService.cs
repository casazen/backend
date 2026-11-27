using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The documents a booking needs, and who issues each (NF-116, NF-83, D12, E17/F01/US01/T02): the one place that says it, used by every draft CasaZen makes (the
/// manual draft, the check-out event, the daily run of the stays that ended, the corrections of a change).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Italian property with owners.</b> The owner in force on the day of the stay issues the document of the stay in its own name, on its issuer profile:
/// one document of several co-owners, issued on the profile of the co-owner who holds the mandate and listing all of them with their shares (⚖ I6). A private
/// owner's rule gives a receipt (the stamp duty above 77.47 by the rule, the stamp reference of the profile, ⚖ I4); a company's rule a FatturaPA invoice with its
/// sezionale and the VAT of its stay treatment (⚖ I1), the agency being the third-party issuer (E17/F01/US01/T03). The services the agency charges the guest (the
/// cleaning when the agency treats it as a service, the extras) are a second document, issued by the organization. An owner with no issuer profile that can issue
/// gets no document at all (<see cref="DocumentPlan.OwnersWithoutIssuer"/>).</item>
/// <item><b>Swiss property</b> (⚖ C1): the organization issues, as E11/E12 do, unless the owner has a Swiss issuer profile on which the agency chose «emette il
/// proprietario».</item>
/// <item><b>Any other booking</b> (a property without owners, a property abroad): the organization issues one document with everything.</item>
/// </list>
/// The day of the stay is its check-out, the day that also chooses the tax rule and the owner of the revenue in the owner ledger. Reading only: nothing is written.
/// </remarks>
public interface IDocumentPlanService
{
    /// <summary>
    /// The plan of the booking: its documents in order (the document of the stay first), or none with the owners who still need an issuer profile. 404
    /// <c>booking_not_found</c>; 422 <c>issuer_profile_incomplete</c> when the organization has no issuer profile.
    /// </summary>
    Task<DocumentPlan> PlanAsync(Guid bookingId, CancellationToken ct = default);
}

/// <summary>What a booking needs: its documents, or the owners whose issuer profile is missing (then no document at all).</summary>
/// <param name="BookingId">The booking.</param>
/// <param name="Documents">The documents, the one of the stay first; empty while an owner has no issuer profile that can issue.</param>
/// <param name="OwnersWithoutIssuer">The owners who issue the stay and have no issuer profile that can issue: the «Da fare» <c>owner_issuer_missing</c>.</param>
public sealed record DocumentPlan(Guid BookingId, IReadOnlyList<PlannedDocument> Documents, IReadOnlyList<Guid> OwnersWithoutIssuer)
{
    /// <summary>The planned document of the services of the agency (<paramref name="agencyServices"/>) or of the stay; <c>null</c> when the plan has none.</summary>
    public PlannedDocument? Of(bool agencyServices) => Documents.FirstOrDefault(document => document.ForAgencyServices == agencyServices);
}

/// <summary>A document of the plan.</summary>
/// <param name="IssuerProfileId">The issuer profile it is issued on: the organization's or the owner's.</param>
/// <param name="IssuerOwnerId">The owner who issues it; <c>null</c> when the organization does.</param>
/// <param name="ForAgencyServices">The document of the services the agency charges the guest, beside the owner's document of the stay.</param>
/// <param name="LineTypes">The types of the lines it bills (the stay of an owner leaves the services of the agency out).</param>
/// <param name="CoOwners">The co-owners the document lists with their shares (⚖ I6); empty for a single owner and for the organization.</param>
public sealed record PlannedDocument(
    Guid IssuerProfileId,
    Guid? IssuerOwnerId,
    bool ForAgencyServices,
    IReadOnlyCollection<FiscalLineType> LineTypes,
    IReadOnlyList<PlannedCoOwner> CoOwners)
{
    /// <summary>Every document of a booking is a document to the guest about the stay.</summary>
    public FiscalDocumentPurpose Purpose => FiscalDocumentPurpose.GuestStay;

    /// <summary>The document bills the lines of <paramref name="type"/>.</summary>
    public bool Bills(FiscalLineType type) => LineTypes.Contains(type);
}

/// <summary>A co-owner listed on the document of the stay: the name the document prints and the share in percent.</summary>
public sealed record PlannedCoOwner(Guid OwnerId, string Name, decimal SharePercent);

/// <summary>The rules of the plan that the documents and their corrections share (E17/F01/US01/T02).</summary>
public static class DocumentPlanRules
{
    /// <summary>Every type of line.</summary>
    public static IReadOnlyCollection<FiscalLineType> AllLineTypes { get; } = Enum.GetValues<FiscalLineType>();

    /// <summary>
    /// The lines the agency bills in its own name when an owner issues the document of the stay: the extras, and the cleaning when the agency treats it as a
    /// service (<see cref="CleaningFeeTreatment.Service"/> on the profile of the organization); a cleaning that follows the stay stays in the owner's document.
    /// </summary>
    public static IReadOnlyCollection<FiscalLineType> AgencyServiceLineTypes(CleaningFeeTreatment agencyCleaning) =>
        agencyCleaning == CleaningFeeTreatment.Service ? [FiscalLineType.Cleaning, FiscalLineType.ExtraService] : [FiscalLineType.ExtraService];

    /// <summary>
    /// True when an ownership that started at <paramref name="ownershipStart"/> and ended at <paramref name="ownershipEnd"/> (<c>null</c> = still in force) is in force on
    /// <paramref name="day"/>: started on or before it and not ended on it (the definition of the owner ledger, so the document names the owner the ledger credits the stay to).
    /// </summary>
    public static bool IsActiveOn(DateTime ownershipStart, DateTime? ownershipEnd, DateOnly day) =>
        DateOnly.FromDateTime(ownershipStart) <= day && (ownershipEnd is not { } end || DateOnly.FromDateTime(end) > day);

    /// <summary>
    /// Who signs for a property of several co-owners (⚖ I6) among the ownerships in force on <paramref name="day"/>: the co-owner whose mandate is in force that day
    /// (<see cref="OwnerMandateRules.InForce"/>); among several, or when none has one, the largest share, then the oldest ownership, then the smallest id: always the same
    /// one. The one rule of the document of a stay and of the self-billed invoice of a portal account held by the owner (E17/F01/US03/T01). <c>null</c> without an ownership.
    /// </summary>
    public static ActiveOwnership? SignatoryOf(IReadOnlyCollection<ActiveOwnership> active, IEnumerable<OwnerMandate> mandates, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(active);
        ArgumentNullException.ThrowIfNull(mandates);

        var mandateList = mandates as IReadOnlyCollection<OwnerMandate> ?? mandates.ToList();
        var withMandate = active.Where(o => OwnerMandateRules.InForce(mandateList, o.OwnerId, o.OwnershipId, day) is not null).ToList();
        return (withMandate.Count > 0 ? withMandate : active)
            .OrderByDescending(o => o.SharePercent)
            .ThenBy(o => o.OwnershipStart)
            .ThenBy(o => o.OwnerId)
            .Select(o => (ActiveOwnership?)o)
            .FirstOrDefault();
    }
}

/// <summary>An ownership in force on a day: the one rule of who signs for several co-owners reads these (<see cref="DocumentPlanRules.SignatoryOf"/>).</summary>
/// <param name="OwnershipId">The ownership.</param>
/// <param name="OwnerId">The owner.</param>
/// <param name="SharePercent">The share in percent.</param>
/// <param name="OwnershipStart">When the ownership started.</param>
public readonly record struct ActiveOwnership(Guid OwnershipId, Guid OwnerId, decimal SharePercent, DateTime OwnershipStart);
