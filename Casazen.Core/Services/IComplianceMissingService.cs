namespace Casazen.Core.Services;

/// <summary>
/// One thing an item of the compliance cockpit still lacks (SR-03): a stable snake_case <see cref="Code"/> the client
/// translates, the <see cref="Field"/> it is about and, when several guests of a stay lack the same field, how many.
/// </summary>
/// <param name="Code">
/// For a property to activate, the code of the blocker of its activation wizard (<c>activation_cin_missing</c>,
/// <c>safety_gas_detector_missing</c>, ...); for the stays, one of <see cref="ComplianceMissingCodes"/>.
/// </param>
/// <param name="Field">
/// What to fill in: the id of the activation step (<c>base-data</c>, <c>cin</c>, <c>documents</c>, <c>safety</c>) or the
/// name of a field of the Alloggiati record, camelCase as <c>AlloggiatiRecordRules</c> names it (<c>documentNumber</c>,
/// <c>dateOfBirth</c>, ...); null when the code says it all.
/// </param>
/// <param name="Count">Guests of the stay that lack the <paramref name="Field"/>; null when it does not apply.</param>
public sealed record ComplianceMissing(string Code, string? Field = null, int? Count = null);

/// <summary>The codes of <see cref="ComplianceMissing"/> that are not the blockers of the activation wizard (SR-03): part of the API contract.</summary>
public static class ComplianceMissingCodes
{
    /// <summary>A property to activate whose blockers were not worked out (see <see cref="IComplianceMissingService"/>): open the wizard.</summary>
    public const string ActivationIncomplete = "activation_incomplete";

    /// <summary>A property whose requirements are all met: only the confirmation of the activation (the terms) is left.</summary>
    public const string ActivationNotConfirmed = "activation_not_confirmed";

    /// <summary>One field of the Alloggiati record that some guests of the stay lack: <see cref="ComplianceMissing.Field"/> names it.</summary>
    public const string GuestFieldMissing = "guest_field_missing";

    /// <summary>The guests of the stay are in an order the record does not accept (a head without members, a member without its head).</summary>
    public const string GuestCompositionInvalid = "guest_composition_invalid";

    /// <summary>The guest data are incomplete but the stay could not be read again (it changed meanwhile): open the stay.</summary>
    public const string GuestDataIncomplete = "guest_data_incomplete";

    /// <summary>The departure of the stay was not closed.</summary>
    public const string CheckoutNotClosed = "checkout_not_closed";

    /// <summary>The Alloggiati communication was not sent on the portal.</summary>
    public const string AlloggiatiNotSent = "alloggiati_not_sent";

    /// <summary>The Alloggiati communication failed or was rejected.</summary>
    public const string AlloggiatiFailed = "alloggiati_failed";

    /// <summary>The host did not declare the property ready after the check-out.</summary>
    public const string PropertyReadyNotConfirmed = "property_ready_not_confirmed";

    /// <summary>A request to answer: the host has not accepted or declined it yet (<c>GET /api/dashboard/today</c>).</summary>
    public const string ApprovalNotAnswered = "approval_not_answered";

    /// <summary>The payment of a confirmed stay failed and nothing else paid it (<c>GET /api/dashboard/today</c>).</summary>
    public const string PaymentFailed = "payment_failed";
}

/// <summary>
/// Says what each item of the compliance cockpit still lacks (SR-03): the blockers of a property, the fields of the
/// guests of a stay, the single thing left for the others. Apart from <see cref="IComplianceWizardService"/> so that its
/// cockpit stays what it was: <c>GET /api/compliance/summary</c> and the Home call this on the result.
/// </summary>
public interface IComplianceMissingService
{
    /// <summary>
    /// <paramref name="summary"/> with <see cref="ComplianceSummaryItem.Missing"/> filled on every item; the sections, their
    /// counts and the items themselves are not changed. The blockers of a property take a few queries each, so they are worked
    /// out for the first <see cref="MaxDetailedProperties"/> only (the others get
    /// <see cref="ComplianceMissingCodes.ActivationIncomplete"/>); the guests of the stays are read in one go.
    /// </summary>
    Task<ComplianceSummaryResult> DescribeAsync(ComplianceSummaryResult summary, CancellationToken cancellationToken = default);

    /// <summary>Properties whose blockers are worked out in one call of <see cref="DescribeAsync"/>.</summary>
    const int MaxDetailedProperties = 10;
}
