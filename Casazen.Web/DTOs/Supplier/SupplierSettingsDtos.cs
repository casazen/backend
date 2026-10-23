using Casazen.Core.Services;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

/// <summary>
/// Body of <c>GET</c> and <c>PUT api/supplier/settings</c> (SP-16a): the supplier's own settings. The defaults while the supplier never
/// saved one (a read writes nothing).
/// </summary>
public class SupplierSettingsDto
{
    /// <summary>
    /// Whether the public showcase takes bookings. <b>Read-only here</b>: the <c>PUT</c> neither takes nor changes it, only the showcase
    /// editor opens and closes the bookings (decision SP-D29).
    /// </summary>
    public bool OnlineBookingEnabled { get; set; }

    /// <summary>The supplier accepts the requests of its regular hosts (three jobs taken) by itself.</summary>
    public bool AutoAcceptRegulars { get; set; }

    /// <summary>The supplier wants a notification for every new request.</summary>
    public bool NotifyNewRequests { get; set; }

    /// <summary>The supplier wants the reminder of tomorrow's jobs.</summary>
    public bool NotifyDayBeforeReminder { get; set; }

    /// <summary>The supplier wants to know when a payment arrives.</summary>
    public bool NotifyPayments { get; set; }

    /// <summary>
    /// Minutes a customer of the public showcase waits for the supplier's answer (15 to 10080; default 180). A request of a CasaZen host
    /// has its own deadline (<c>Suppliers:ServiceRequests:HostResponseMinutes</c>), shown on the request as <c>respondBy</c>.
    /// </summary>
    public int RespondWithinMinutes { get; set; }
}

/// <summary>
/// Body of <c>PUT api/supplier/settings</c> (SP-16a): the five settings, all of them (<c>PUT</c> replaces them; one that is left out is a
/// 422 naming it, never reset to its default). <c>onlineBookingEnabled</c> is not a field of the request: a value sent for it is ignored.
/// </summary>
public class SaveSupplierSettingsRequest
{
    public bool? AutoAcceptRegulars { get; set; }

    public bool? NotifyNewRequests { get; set; }

    public bool? NotifyDayBeforeReminder { get; set; }

    public bool? NotifyPayments { get; set; }

    /// <summary>15 to 10080 minutes.</summary>
    public int? RespondWithinMinutes { get; set; }
}

/// <summary>Maps the supplier's settings between the API and the service.</summary>
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
