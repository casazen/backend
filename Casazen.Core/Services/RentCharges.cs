using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Casazen.Core.Services;

public static class RentCharges
{
    public const string Kind = "rent-charge";

    public const string InstallmentMetadataKey = "rentLedgerEntryId";

    public const string Currency = "eur";

    public const string RequestDaysBeforeDueSetting = "RentBilling:PaymentRequestDaysBeforeDue";

    public const int ProvisionalRequestDaysBeforeDue = 5;

    public static int GetRequestDaysBeforeDue(IConfiguration configuration) =>
        Math.Clamp(configuration.GetValue(RequestDaysBeforeDueSetting, ProvisionalRequestDaysBeforeDue), 0, 31);

    public const string ReminderIntervalHoursSetting = "RentBilling:ReminderIntervalHours";

    public const int DefaultReminderIntervalHours = 24;

    public const int MinReminderIntervalHours = 1;

    public const int MaxReminderIntervalHours = 720;

    public const int MaxReminderNoteLength = 300;

    public const int MaxBulkReminders = 50;

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
