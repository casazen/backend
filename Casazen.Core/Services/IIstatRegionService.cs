using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// The ISTAT region table and what a property gets from it (E21/F01/US01/T01, decision O6, NF-44, NF-52): the rows the platform staff keeps, and the
/// answer for one property (its region, system, deadline, required guest fields, coverage by the channel manager, ISTAT sender and what the host does
/// next). It reads and writes the table only: nothing is sent to a portal, to a channel manager or to anybody.
/// </summary>
/// <remarks>
/// <para><b>Who may.</b> The service never checks a role: the web layer authorizes (<c>AdminOnly</c> for the table, the property read of the caller for
/// the coverage of a property). The table is platform data, the same for every org.</para>
/// <para><b>Nothing is claimed that was not verified.</b> Every seeded row is «da verificare»: its coverage is
/// <see cref="IstatCoverage.Unknown"/> and its next step <see cref="IstatNextStep.AskSupport"/>. A comune that is not in the list, or a region without a
/// row, answers the same. The deadline the cockpit uses is the one of <see cref="IstatRegionRule.MonthlyDeadlineDay"/> unless
/// <c>Compliance:IstatDeadlineDays</c> overrides it.</para>
/// <para><b>The statistical fields.</b> The row says which of them the region requires and the guest page asks only those. No member of the channel
/// manager for them was proven in its sandbox, so CasaZen collects them and does not pass them on (an open point of
/// <c>docs/runbooks/istat-flussi-regionali.md</c>).</para>
/// </remarks>
public interface IIstatRegionService
{
    /// <summary>The whole table, regions in the order of their ISTAT code (the province rows after the region's, none for Trentino-Alto Adige).</summary>
    Task<IReadOnlyList<IstatRegionRule>> ListAsync(CancellationToken ct);

    /// <summary>
    /// Replaces what the staff may edit in the row of a region or autonomous province and leaves a line in the audit trail (who, when, what). It never
    /// creates a row: the territories are the closed list of the seed. The row is stamped as verified at <see cref="IstatRegionRuleUpdate.VerifiedAt"/>
    /// or now: whoever saves it says they read it against its source.
    /// </summary>
    /// <remarks>
    /// Errors: <c>NotFoundException</c> <c>istat_region_not_found</c> (a territory that is not in the table); <c>DomainRuleException</c>
    /// <c>istat_region_source_invalid</c> (the source is not an absolute https address), <c>istat_region_portal_invalid</c> (the same for the portal),
    /// <c>istat_region_deadline_invalid</c> (a monthly deadline without a day from 1 to 31, or a day on another kind) and
    /// <c>istat_region_fields_invalid</c> (a name that is not a statistical field of the lists).
    /// </remarks>
    Task<IstatRegionRule> UpdateAsync(IstatRegionRuleUpdate update, string userId, CancellationToken ct);

    /// <summary>
    /// What the ISTAT communication of a property is: from the region of its comune, never guessed. A property outside Italy has nothing to do with the
    /// regional flows (<see cref="IstatCoverageView.Applies"/> false). Throws <c>NotFoundException</c> <c>property_not_found</c> for a property the caller's
    /// org does not have.
    /// </summary>
    Task<IstatCoverageView> GetCoverageAsync(Guid propertyId, CancellationToken ct);
}

/// <summary>What a person of the staff edits in a row (<see cref="IIstatRegionService.UpdateAsync"/>): the territory names the row, the rest replaces what it holds.</summary>
/// <param name="RegionIstatCode">The ISTAT code of the region, two digits.</param>
/// <param name="ProvinceCode">The plate code of the autonomous province (<c>BZ</c>, <c>TN</c>); null for a region.</param>
/// <param name="System">The system of the portal; null or blank: not stated.</param>
/// <param name="PortalUrl">The address of the portal or of its guide (https); null or blank: not stated.</param>
/// <param name="DeadlineKind">How often the portal wants the movement.</param>
/// <param name="DeadlineDay">The day of the following month, 1 to 31, for a monthly deadline; null for the others.</param>
/// <param name="RequiredGuestFields">The statistical fields the region requires: null = not verified (none is asked), empty = verified, none required. Never «all».</param>
/// <param name="OctorateCoverage">Whether the channel manager covers the region.</param>
/// <param name="SourceUrl">The official page or act the row was read from (https), required.</param>
/// <param name="VerifiedAt">The day the staff checked the row against the source (UTC); null: now.</param>
/// <param name="Notes">A note of the staff; null or blank: none.</param>
public sealed record IstatRegionRuleUpdate(
    string RegionIstatCode,
    string? ProvinceCode,
    string? System,
    string? PortalUrl,
    IstatDeadlineKind DeadlineKind,
    int? DeadlineDay,
    IReadOnlyCollection<string>? RequiredGuestFields,
    IstatCoverage OctorateCoverage,
    string SourceUrl,
    DateTime? VerifiedAt,
    string? Notes);

/// <summary>
/// The ISTAT communication of a property as the table says it (<c>GET api/properties/{id}/istat-coverage</c>). Every value the table does not state is
/// «da verificare»: null, <see cref="IstatDeadlineKind.Unknown"/>, <see cref="IstatCoverage.Unknown"/>. No personal data.
/// </summary>
/// <param name="PropertyId">The property.</param>
/// <param name="Applies">False for a property outside Italy: the regional flows are not its business (no region, no next step).</param>
/// <param name="RegionKnown">True when the comune of the property is in the list and the table has the row of its territory.</param>
/// <param name="RegionIstatCode">The ISTAT code of the region of the comune; null when the property has no comune of the list.</param>
/// <param name="ProvinceCode">The plate code of the autonomous province when the row is the province's.</param>
/// <param name="RegionName">The name of the region (or of the autonomous province); null when not known.</param>
/// <param name="System">The system of the portal; null: not stated.</param>
/// <param name="PortalUrl">The address of the portal or of its guide; null: not stated.</param>
/// <param name="DeadlineKind">How often the portal wants the movement; monthly when <c>Compliance:IstatDeadlineDays</c> sets a day for the region.</param>
/// <param name="DeadlineDay">The day of the following month the cockpit uses; null when none is stated.</param>
/// <param name="RequiredGuestFields">The statistical fields the region requires: empty when it requires none or the row is not verified.</param>
/// <param name="RequiredGuestFieldsKnown">False when the row is not verified: then no field is asked, and none can be said to be not required.</param>
/// <param name="Coverage">Whether the channel manager covers the region; <see cref="IstatCoverage.Unknown"/> unless the staff verified it.</param>
/// <param name="IstatSender">
/// Who sends the ISTAT communication of the property: the Alloggiati sender, except that it is <see cref="ComplianceSenderType.Manual"/> where the
/// channel manager does not cover the region (<see cref="IstatCoverage.NotCovered"/>): the host uses the portal of the region, as the next step says and as
/// the cockpit shows (<see cref="Casazen.Core.Regulatory.IstatRegionResolver.IstatSenderFor"/>).
/// </param>
/// <param name="NextStep">What the host does next; null when <paramref name="Applies"/> is false.</param>
/// <param name="AssistContext">The context of the «Do it for me» request when the next step is to ask support (<c>panelIstat</c>); null otherwise.</param>
/// <param name="SourceUrl">The source of the row; null when no row applies.</param>
/// <param name="VerifiedAt">When the staff checked the row (UTC); null: never.</param>
public sealed record IstatCoverageView(
    Guid PropertyId,
    bool Applies,
    bool RegionKnown,
    string? RegionIstatCode,
    string? ProvinceCode,
    string? RegionName,
    string? System,
    string? PortalUrl,
    IstatDeadlineKind DeadlineKind,
    int? DeadlineDay,
    IReadOnlyList<string> RequiredGuestFields,
    bool RequiredGuestFieldsKnown,
    IstatCoverage Coverage,
    ComplianceSenderType IstatSender,
    IstatNextStep? NextStep,
    string? AssistContext,
    string? SourceUrl,
    DateTime? VerifiedAt);

/// <summary>The stable codes of the errors of the region table.</summary>
public static class IstatRegionErrors
{
    /// <summary>The territory is not a row of the table (404).</summary>
    public const string NotFoundCode = "istat_region_not_found";

    /// <summary>The source is not an absolute https address (422).</summary>
    public const string SourceInvalidCode = "istat_region_source_invalid";

    /// <summary>The portal is not an absolute https address (422).</summary>
    public const string PortalInvalidCode = "istat_region_portal_invalid";

    /// <summary>A monthly deadline without a day from 1 to 31, or a day on another kind (422).</summary>
    public const string DeadlineInvalidCode = "istat_region_deadline_invalid";

    /// <summary>A name that is not a statistical field of the lists (422).</summary>
    public const string FieldsInvalidCode = "istat_region_fields_invalid";

    /// <summary>The kind of row the audit trail names: <c>RegulatoryDataAuditEntry.EntityType</c>.</summary>
    public const string AuditEntityType = "IstatRegionRule";
}
