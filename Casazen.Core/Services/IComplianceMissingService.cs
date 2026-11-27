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

public static class ComplianceMissingCodes
{
    public const string ActivationIncomplete = "activation_incomplete";

    public const string ActivationNotConfirmed = "activation_not_confirmed";

    /// <summary>One field of the Alloggiati record that some guests of the stay lack: <see cref="ComplianceMissing.Field"/> names it.</summary>
    public const string GuestFieldMissing = "guest_field_missing";

    public const string GuestCompositionInvalid = "guest_composition_invalid";

    public const string GuestDataIncomplete = "guest_data_incomplete";

    public const string CheckoutNotClosed = "checkout_not_closed";

    public const string AlloggiatiNotSent = "alloggiati_not_sent";

    public const string AlloggiatiFailed = "alloggiati_failed";

    public const string PropertyReadyNotConfirmed = "property_ready_not_confirmed";

    public const string ApprovalNotAnswered = "approval_not_answered";

    public const string PaymentFailed = "payment_failed";

    public const string ProposalNotAnswered = "proposal_not_answered";

    public const string FinalAmountNotConfirmed = "final_amount_not_confirmed";

    public const string ServiceNotPaid = "service_not_paid";

    public const string CalendarConflict = "calendar_conflict";
}

/// <summary>
/// Says what each item of the compliance cockpit still lacks (SR-03): the blockers of a property, the fields of the
/// guests of a stay, the single thing left for the others. Apart from <see cref="IComplianceWizardService"/> so that its
/// cockpit stays what it was: <c>GET /api/compliance/summary</c> and the Home call this on the result.
/// </summary>
public interface IComplianceMissingService
{
    Task<ComplianceSummaryResult> DescribeAsync(ComplianceSummaryResult summary, CancellationToken cancellationToken = default);

    const int MaxDetailedProperties = 10;
}
