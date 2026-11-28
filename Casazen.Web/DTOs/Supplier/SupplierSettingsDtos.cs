using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

public class SupplierSettingsDto
{
    public bool OnlineBookingEnabled { get; set; }

    public bool AutoAcceptRegulars { get; set; }

    public bool NotifyNewRequests { get; set; }

    public bool NotifyDayBeforeReminder { get; set; }

    public bool NotifyPayments { get; set; }

    public int RespondWithinMinutes { get; set; }
}

public class SaveSupplierSettingsRequest
{
    public bool? AutoAcceptRegulars { get; set; }

    public bool? NotifyNewRequests { get; set; }

    public bool? NotifyDayBeforeReminder { get; set; }

    public bool? NotifyPayments { get; set; }

    public int? RespondWithinMinutes { get; set; }
}

public static class SupplierSettingsMapper
{
    public static SupplierSettingsDto ToDto(SupplierSettingsView view) => new()
    {
        OnlineBookingEnabled = view.OnlineBookingEnabled,
        AutoAcceptRegulars = view.AutoAcceptRegulars,
        NotifyNewRequests = view.NotifyNewRequests,
        NotifyDayBeforeReminder = view.NotifyDayBeforeReminder,
        NotifyPayments = view.NotifyPayments,
        RespondWithinMinutes = view.RespondWithinMinutes,
    };

    public static SupplierSettingsInput ToInput(SaveSupplierSettingsRequest request) => new(
        request.AutoAcceptRegulars,
        request.NotifyNewRequests,
        request.NotifyDayBeforeReminder,
        request.NotifyPayments,
        request.RespondWithinMinutes);
}
