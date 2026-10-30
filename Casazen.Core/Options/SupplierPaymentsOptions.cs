using Casazen.Core.Suppliers;
using Microsoft.Extensions.Options;

namespace Casazen.Core.Options;

/// <summary>
/// Section <c>SupplierPayments</c> (SP-15a): the commission CasaZen keeps on the services paid inside CasaZen, and the times of
/// the payment requests. Env vars <c>SupplierPayments__*</c>. Runbook <c>docs/runbooks/suppliers.md</c> section 26 and
/// <c>docs/runbooks/stripe.md</c> § "Services of the suppliers (SP-15)".
/// </summary>
/// <remarks>
/// <para><b>The commission is configuration, never code (decision D3).</b> There is no default of the percentage in this class:
/// the committed <c>appsettings.json</c> carries the <b>provisional</b> hypothesis of the product owner (10) and
/// <see cref="CommissionPercent"/> is validated at startup (<see cref="SupplierPaymentsOptionsValidator"/>): a deployment
/// without it, or with a value outside 0 to <see cref="MaxCommissionPercent"/>, does not start. Nothing computes with a literal
/// percentage: a payment keeps the percentage it was created with (<c>ServiceRequestPayment.CommissionPercent</c>), and a
/// supplier can have its own (<c>SupplierProfile.CommissionPercentOverride</c>, e.g. a free period).</para>
/// <para>The VAT on the commission and DAC7 are open (<b>[CONSULENTE FISCALE]</b>, decision D4): no rate lives here.</para>
/// </remarks>
public sealed class SupplierPaymentsOptions
{
    public const string SectionName = "SupplierPayments";

    /// <summary>Highest commission the configuration or a supplier's override may carry (percent).</summary>
    public const decimal MaxCommissionPercent = 50m;

    /// <summary>Shortest and longest times, in days, of the settings below.</summary>
    public const int MinDays = 1;

    /// <inheritdoc cref="MinDays"/>
    public const int MaxDays = 365;

    /// <summary>
    /// The commission CasaZen keeps, as a percentage of the price the payer pays (0 to 50). <b>Provisional</b>: the figure is the
    /// product owner's decision (the hypothesis is 10). Env var <c>SupplierPayments__CommissionPercent</c>. Required.
    /// </summary>
    public decimal? CommissionPercent { get; set; }

    /// <summary>Days after the payment was requested after which it is flagged as late (7). Used by the reminder job (SP-15b). Env var <c>SupplierPayments__LateAfterDays</c>.</summary>
    public int LateAfterDays { get; set; } = 7;

    /// <summary>
    /// Days after the payment was requested on which the reminder job sends a reminder (2 and 7). Empty means those two. Duplicates
    /// and order do not matter (see <see cref="EffectiveReminderDays"/>). Env var <c>SupplierPayments__ReminderDays__0</c>, <c>__1</c>…
    /// </summary>
    public int[] ReminderDays { get; set; } = [];

    /// <summary>Days a payment link stays valid, from the email that carries it (30). Env var <c>SupplierPayments__PaymentLinkValidityDays</c>.</summary>
    public int PaymentLinkValidityDays { get; set; } = 30;

    /// <summary>
    /// The smallest amount, in cents, that is charged inside CasaZen (50: the minimum Stripe accepts for a charge in euro). A
    /// completed request below it is not charged online: it falls back to the manual flow. Env var
    /// <c>SupplierPayments__MinAmountCents</c>.
    /// </summary>
    public int MinAmountCents { get; set; } = 50;

    /// <summary>
    /// The VAT rate of CasaZen's commission, as a percentage (0 to 100). <b>[CONSULENTE FISCALE]</b> (decision D4): <b>empty</b>
    /// until the tax consultant decides whether the commission percentage already includes VAT or VAT is added on top. Nothing
    /// computes with it and no payment stores a VAT amount (<c>FeeVatMode</c> and <c>FeeVatCents</c> stay empty): the monthly
    /// commission export (SP-15b) only shows it in its <c>vat_percent</c> column, for the manual invoice. No default in code. Env
    /// var <c>SupplierPayments__CommissionVatPercent</c>.
    /// </summary>
    public decimal? CommissionVatPercent { get; set; }

    /// <summary>Highest VAT rate the configuration may carry (percent).</summary>
    public const decimal MaxVatPercent = 100m;

    // ─── The payment held by CasaZen (SP-15c-a, decision VT-D1). Every time below is configuration: Stripe's own limits are not ───
    // ─── guaranteed by this code, and the product owner decides the ones that depend on the answers of Stripe (VT-D22). ──────────

    /// <summary>
    /// Longest a card block may last, in hours, that the configuration allows: Stripe keeps a block for 7 days for most cards but for
    /// 4 days 18 hours (114 hours) for a Visa charged without the cardholder present (VT-D22), so CasaZen never relies on more than
    /// four days.
    /// </summary>
    public const int MaxAuthorizationValidityHours = 96;

    /// <summary>Longest wait between the work and the release, in minutes, that the configuration allows (one week).</summary>
    public const int MaxReleaseAfterMinutes = 7 * 24 * 60;

    /// <summary>
    /// How long CasaZen treats the block of a customer's card as usable, in hours (48). It is the longest a supplier has to accept a
    /// booking: after it the capture is refused, the block is released and the request is closed. <b>Far below</b> what a card keeps
    /// (<see cref="MaxAuthorizationValidityHours"/>), so a booking is never left "paid" with no money behind it. Env var
    /// <c>SupplierPayments__AuthorizationValidityHours</c>, 1 to <see cref="MaxAuthorizationValidityHours"/>.
    /// </summary>
    public int AuthorizationValidityHours { get; set; } = 48;

    /// <summary>
    /// Minutes between the completion of the work and the release of the money to the supplier if the payer says nothing (60: the
    /// rule of the product owner, VT-D1). Env var <c>SupplierPayments__ReleaseAfterMinutes</c>, 0 to <see cref="MaxReleaseAfterMinutes"/>.
    /// </summary>
    public int ReleaseAfterMinutes { get; set; } = 60;

    /// <summary>
    /// The longest CasaZen means to hold a payer's money before releasing it, in days (60). <b>A decision of the product owner</b>, to be
    /// confirmed with Stripe (VT-D22, question 2: a table of its documentation says 90 days for platforms outside the US, and it is
    /// not clear whether that is a limit on the platform's balance); this is a setting, never a constant. It does not move any money:
    /// it makes the admins warned <see cref="HoldWarningDays"/> days before a held payment reaches it. Env var
    /// <c>SupplierPayments__MaxHoldDays</c>, 1 to 365.
    /// </summary>
    public int MaxHoldDays { get; set; } = 60;

    /// <summary>Days before <see cref="MaxHoldDays"/> on which the admins are warned about a payment still held (7). Env var <c>SupplierPayments__HoldWarningDays</c>, 0 to <see cref="MaxHoldDays"/>.</summary>
    public int HoldWarningDays { get; set; } = 7;

    /// <summary>
    /// Minutes before the first new try of a transfer Stripe refused, or that could not be made because the supplier's account is not
    /// ready (15). Stripe does not retry a failed transfer by itself (VT-D22), so the release job does, doubling the wait each time up to
    /// <see cref="TransferRetryMaxMinutes"/>. Env var <c>SupplierPayments__TransferRetryBaseMinutes</c>, 1 to 1440.
    /// </summary>
    public int TransferRetryBaseMinutes { get; set; } = 15;

    /// <summary>The longest wait between two tries of a transfer, in minutes (720 = 12 hours). Env var <c>SupplierPayments__TransferRetryMaxMinutes</c>, <see cref="TransferRetryBaseMinutes"/> to 10080.</summary>
    public int TransferRetryMaxMinutes { get; set; } = 720;

    /// <summary>
    /// After this many failed tries of a transfer the admins are told (once) that a supplier is not being paid (5). Env var
    /// <c>SupplierPayments__TransferAlertAfterAttempts</c>, 1 to 100.
    /// </summary>
    public int TransferAlertAfterAttempts { get; set; } = 5;

    /// <summary>The authorization validity as a time span: how long a card block is treated as usable.</summary>
    public TimeSpan AuthorizationValidity => TimeSpan.FromHours(AuthorizationValidityHours);

    /// <summary>The wait between the completion of the work and the release as a time span.</summary>
    public TimeSpan ReleaseAfter => TimeSpan.FromMinutes(ReleaseAfterMinutes);

    /// <summary>The days of the reminders: <see cref="ReminderDays"/> without duplicates, ascending; 2 and 7 when none is set.</summary>
    public IReadOnlyList<int> EffectiveReminderDays =>
        ReminderDays is { Length: > 0 } ? ReminderDays.Distinct().Order().ToArray() : [2, 7];

    /// <summary>The configured commission; the options are validated at startup, so a missing value cannot get here in the running application.</summary>
    /// <exception cref="InvalidOperationException">The commission is not configured.</exception>
    public decimal RequireCommissionPercent() =>
        CommissionPercent
        ?? throw new InvalidOperationException("SupplierPayments__CommissionPercent is not configured (validated at startup).");

    /// <summary>Configuration errors, empty when the section is valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        if (CommissionPercent is not { } percent)
        {
            failures.Add(
                $"{SectionName}__CommissionPercent is missing: set the commission CasaZen keeps on the services paid inside CasaZen "
                + $"(0 to {MaxCommissionPercent}). It is never taken from the code.");
        }
        else if (percent is < 0m or > MaxCommissionPercent || decimal.Round(percent, 2) != percent)
        {
            failures.Add($"{SectionName}__CommissionPercent must be between 0 and {MaxCommissionPercent} with at most two decimals.");
        }

        if (LateAfterDays is < MinDays or > MaxDays)
            failures.Add($"{SectionName}__LateAfterDays must be between {MinDays} and {MaxDays} days.");
        if (PaymentLinkValidityDays is < MinDays or > MaxDays)
            failures.Add($"{SectionName}__PaymentLinkValidityDays must be between {MinDays} and {MaxDays} days.");
        if (ReminderDays.Any(day => day is < MinDays or > MaxDays))
            failures.Add($"{SectionName}__ReminderDays must be days between {MinDays} and {MaxDays}.");
        if (MinAmountCents is < 1 or > ServiceRequestLimits.MaxAmountCents)
            failures.Add($"{SectionName}__MinAmountCents must be between 1 and {ServiceRequestLimits.MaxAmountCents} cents.");
        if (CommissionVatPercent is { } vat && (vat is < 0m or > MaxVatPercent || decimal.Round(vat, 2) != vat))
            failures.Add($"{SectionName}__CommissionVatPercent must be empty or between 0 and {MaxVatPercent} with at most two decimals.");

        // The held payment (SP-15c-a). A block that outlives what the card networks guarantee would let a booking be accepted with no
        // money behind it, so the limit is part of the startup check, not of the code that uses it.
        if (AuthorizationValidityHours is < 1 or > MaxAuthorizationValidityHours)
        {
            failures.Add(
                $"{SectionName}__AuthorizationValidityHours must be between 1 and {MaxAuthorizationValidityHours} hours "
                + "(a card block lasts 4 days 18 hours at the least: VT-D22).");
        }

        if (ReleaseAfterMinutes is < 0 or > MaxReleaseAfterMinutes)
            failures.Add($"{SectionName}__ReleaseAfterMinutes must be between 0 and {MaxReleaseAfterMinutes} minutes.");
        if (MaxHoldDays is < MinDays or > MaxDays)
            failures.Add($"{SectionName}__MaxHoldDays must be between {MinDays} and {MaxDays} days.");
        if (HoldWarningDays < 0 || HoldWarningDays > MaxHoldDays)
            failures.Add($"{SectionName}__HoldWarningDays must be between 0 and {SectionName}__MaxHoldDays.");
        if (TransferRetryBaseMinutes is < 1 or > 1440)
            failures.Add($"{SectionName}__TransferRetryBaseMinutes must be between 1 and 1440 minutes.");
        if (TransferRetryMaxMinutes < TransferRetryBaseMinutes || TransferRetryMaxMinutes > MaxReleaseAfterMinutes)
            failures.Add($"{SectionName}__TransferRetryMaxMinutes must be between {SectionName}__TransferRetryBaseMinutes and {MaxReleaseAfterMinutes} minutes.");
        if (TransferAlertAfterAttempts is < 1 or > 100)
            failures.Add($"{SectionName}__TransferAlertAfterAttempts must be between 1 and 100.");
        return failures;
    }
}

/// <summary>Refuses a <c>SupplierPayments</c> section that is not valid at startup, so a typo never changes a commission silently.</summary>
public sealed class SupplierPaymentsOptionsValidator : IValidateOptions<SupplierPaymentsOptions>
{
    public ValidateOptionsResult Validate(string? name, SupplierPaymentsOptions options)
    {
        var failures = options.Validate();
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
