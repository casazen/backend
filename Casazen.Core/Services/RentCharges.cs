using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Casazen.Core.Services;

/// <summary>
/// Settings and Stripe markers of the recurring rent (LT-06, #269). Runbook: <c>docs/runbooks/stripe.md</c> "Recurring
/// rent" and <c>hangfire.md</c> § 12.
/// </summary>
public static class RentCharges
{
    /// <summary><c>metadata.kind</c> of the rent PaymentIntents (routed by the webhooks).</summary>
    public const string Kind = "rent-charge";

    /// <summary><c>metadata</c> key of the installment id on a rent PaymentIntent.</summary>
    public const string InstallmentMetadataKey = "rentLedgerEntryId";

    public const string Currency = "eur";

    public const string RequestDaysBeforeDueSetting = "RentBilling:PaymentRequestDaysBeforeDue";

    /// <summary>
    /// PROVISIONAL technical default (not a product rule, LT-06 DUBBI): days before the due date on which the job emails
    /// the payment link to the tenants. <c>0</c> = on the due date.
    /// </summary>
    public const int ProvisionalRequestDaysBeforeDue = 5;

    /// <summary>Days before the due date of the automatic payment request (0 to 31).</summary>
    public static int GetRequestDaysBeforeDue(IConfiguration configuration) =>
        Math.Clamp(configuration.GetValue(RequestDaysBeforeDueSetting, ProvisionalRequestDaysBeforeDue), 0, 31);

    public const string ReminderIntervalHoursSetting = "RentBilling:ReminderIntervalHours";

    /// <summary>
    /// Product default of LR-01 (the task fixes it): hours between two reminders of the same installment. The landlord's
    /// reminder is a courtesy, not a harassment tool, so an installment is reminded once a day at most.
    /// </summary>
    public const int DefaultReminderIntervalHours = 24;

    /// <summary>Least and greatest value of <see cref="ReminderIntervalHoursSetting"/>: one hour to thirty days.</summary>
    public const int MinReminderIntervalHours = 1;

    /// <inheritdoc cref="MinReminderIntervalHours"/>
    public const int MaxReminderIntervalHours = 720;

    /// <summary>Longest note the landlord can add to a reminder (characters).</summary>
    public const int MaxReminderNoteLength = 300;

    /// <summary>Most installments a bulk reminder takes at once.</summary>
    public const int MaxBulkReminders = 50;

    /// <summary>
    /// Hours between two reminders of the same installment: 1 to 720, default 24 when the setting is missing or is not a whole
    /// number (a typo in the environment must not turn every rent page into an error, nor remove the limit).
    /// </summary>
    public static int GetReminderIntervalHours(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var hours = int.TryParse(configuration[ReminderIntervalHoursSetting], NumberStyles.Integer, CultureInfo.InvariantCulture, out var configured)
            ? configured
            : DefaultReminderIntervalHours;
        return Math.Clamp(hours, MinReminderIntervalHours, MaxReminderIntervalHours);
    }

    /// <summary>Idempotency key of the creation of the <paramref name="count"/>-th PaymentIntent of an installment.</summary>
    public static string CreationIdempotencyKey(Guid installmentId, int count) => $"rent-charge:{installmentId:N}:{count}";

    /// <summary>Idempotency key of the cancellation of a PaymentIntent of an installment.</summary>
    public static string CancellationIdempotencyKey(Guid installmentId, string paymentIntentId) =>
        $"rent-charge-cancel:{installmentId:N}:{paymentIntentId}";

    public static long ToCents(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
}
