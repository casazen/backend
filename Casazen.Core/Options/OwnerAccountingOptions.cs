using Casazen.Core.Entities;
using Casazen.Core.Services;
using Casazen.Core.Validation;

namespace Casazen.Core.Options;

/// <summary>
/// Section <c>OwnerAccounting</c> (E13/F01/US01/T01): the settings of the owner ledger that are prudent defaults to be confirmed by an accountant (⚖). Today
/// one, <see cref="ChannelCommissionDefaults"/>. Nothing here is validated at startup: a value that is not a portal or not a percent is ignored, so the stays
/// of that channel are held back by the readiness (<c>channel_commission_missing</c>) instead of being posted with a guessed commission. Runbook
/// <c>docs/runbooks/owner-accounting.md</c>, <c>docs/runbooks/deploy-checklist.md</c> § 2.11.
/// </summary>
public class OwnerAccountingOptions
{
    public const string SectionName = "OwnerAccounting";

    /// <summary>
    /// The commission a portal keeps, in percent of the gross of the stay without the tourist tax, used for a stay whose reservation brings no
    /// commission when the agency has no rule of its own for the channel in force at the check-out (⚖ R2). The key is the name of the
    /// <see cref="BookingSource"/> (<c>OwnerAccounting:ChannelCommissionDefaults:BookingCom</c>, env var
    /// <c>OwnerAccounting__ChannelCommissionDefaults__BookingCom</c>). Committed in <c>appsettings.json</c>: BookingCom 15, Expedia 18, Airbnb 15.5 (host-only
    /// fee), Vrbo 8; never written in the code.
    /// </summary>
    public Dictionary<string, decimal> ChannelCommissionDefaults { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The default of <paramref name="channel"/>: a key that names a portal (case-insensitive) with a percent from 0 to 100; <c>false</c> without one, or
    /// for a channel that is not a portal.
    /// </summary>
    public bool TryGetChannelDefault(BookingSource channel, out decimal percent)
    {
        percent = 0m;
        if (!FiscalCopy.IsOtaBookingSource(channel))
            return false;

        foreach (var (key, value) in ChannelCommissionDefaults)
        {
            if (EnumNames.TryParseDefined<BookingSource>(key, out var named) && named == channel && value is >= 0m and <= 100m)
            {
                percent = value;
                return true;
            }
        }

        return false;
    }
}
