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
