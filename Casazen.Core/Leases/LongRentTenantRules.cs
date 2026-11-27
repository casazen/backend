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
    public const string OnTimeDaysKey = "LongRentTenants:OnTimeDays";

    public const int DefaultOnTimeDays = 5;

    public const int MaxOnTimeDays = 60;

    public static int OnTimeDays(string? configured) =>
        int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days is >= 0 and <= MaxOnTimeDays
            ? days
            : DefaultOnTimeDays;

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

    public static bool IsFormer(IReadOnlyCollection<LeaseStage?> stages) =>
        stages.Count > 0 && stages.All(stage => stage == LeaseStage.Concluded);

    public static bool IsOnTime(RentLedgerStatus status, DateOnly dueDate, DateOnly? paidOn, int onTimeDays) =>
        status == RentLedgerStatus.Paid && paidOn is { } paid && paid.DayNumber - dueDate.DayNumber <= onTimeDays;
}
