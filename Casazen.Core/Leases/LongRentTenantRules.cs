using System.Globalization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases;

/// <summary>
/// The rules behind the tenants page of the long-term area (LR-09): who counts as the same person, who is a former tenant, and what
/// "paid on time" means. The area has no tenant record: a person is read from the parties of the leases, so these rules are the only
/// definition of it and the list, the sheet, the export and the erasure all use them.
/// </summary>
public static class LongRentTenantRules
{
    /// <summary>Configuration key of the number of days after its due date a payment still counts as made on time.</summary>
    public const string OnTimeDaysKey = "LongRentTenants:OnTimeDays";

    /// <summary>Days after the due date a payment is still on time when nothing is configured (the five days of the prototype).</summary>
    public const int DefaultOnTimeDays = 5;

    /// <summary>The longest grace period the configuration may ask for.</summary>
    public const int MaxOnTimeDays = 60;

    /// <summary>
    /// The grace period from the configured text: the default for anything that is not a whole number between 0 and
    /// <see cref="MaxOnTimeDays"/> (a typo in the environment must not turn the page into an error).
    /// </summary>
    public static int OnTimeDays(string? configured) =>
        int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days is >= 0 and <= MaxOnTimeDays
            ? days
            : DefaultOnTimeDays;

    /// <summary>The fiscal code as it is compared: trimmed and upper case, the form the privacy service matches in SQL.</summary>
    public static string NormalizeFiscalCode(string? fiscalCode) => (fiscalCode ?? string.Empty).Trim().ToUpperInvariant();

    /// <summary>
    /// The identity of a tenant inside an org: their normalized fiscal code, so one person on several leases is one person. A party with
    /// no fiscal code (rows older than its validation) cannot be recognised in another lease and stands for itself. A wrong code makes two
    /// people, and a name never joins two codes: the limit is written on the page.
    /// </summary>
    public static string PersonKey(string? fiscalCode, Guid partyId)
    {
        var code = NormalizeFiscalCode(fiscalCode);
        return code.Length > 0 ? code : $"party:{partyId:N}";
    }

    /// <summary>A person is a former tenant when every lease they are on has concluded (at least one, and none still in preparation or running).</summary>
    public static bool IsFormer(IReadOnlyCollection<LeaseStage?> stages) =>
        stages.Count > 0 && stages.All(stage => stage == LeaseStage.Concluded);

    /// <summary>
    /// Paid, and no later than <paramref name="onTimeDays"/> days after the due date. The punctuality counts the installments due on or
    /// before today and not cancelled: one still to come says nothing about the tenant yet, and one past due and not paid is not on time.
    /// </summary>
    public static bool IsOnTime(RentLedgerStatus status, DateOnly dueDate, DateOnly? paidOn, int onTimeDays) =>
        status == RentLedgerStatus.Paid && paidOn is { } paid && paid.DayNumber - dueDate.DayNumber <= onTimeDays;
}
