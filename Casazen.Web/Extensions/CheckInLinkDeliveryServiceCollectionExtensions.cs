using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;

namespace Casazen.Web.Extensions;

/// <summary>
/// The delivery of the check-in link of a stay that has no e-mail address (epic E23, feature F01, NF-122, decision O10): the service that tries the portal chat and the contact
/// of the guest and records where the link stands, the service that reads the contact of ONE guest for the check-in link only, and the hourly job that tries again what waits. An
/// extension class of its own, so this feature never collides with the shared <c>ServiceCollectionExtensions</c> or with <c>AddMessagesHub</c>, whose list of services a test
/// photographs; <c>Program.cs</c> calls it once. Nothing here is conditional: the flags <c>PortalChatCheckInLink</c> and <c>GuestContactInfo</c> are read where the services
/// act (and by the registration of the job), and with both off the services only record why a link is not going anywhere.
/// </summary>
public static class CheckInLinkDeliveryServiceCollectionExtensions
{
    public static IServiceCollection AddCheckInLinkDelivery(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // E23/F01/US01/T01: the delivery record of a booking and the ways to the guest; the daily job guest-checkin-send, the host's endpoint and the completion of the check-in ask it.
        services.AddScoped<ICheckInLinkDeliveryService, CheckInLinkDeliveryService>();

        // E23/F01/US01/T02: the e-mail address of the guest of one booking, read from the channel manager for the check-in link only (never into the CRM).
        services.AddScoped<IGuestContactInfoService, GuestContactInfoService>();

        // E23/F01/US01/T01, T02: the hourly retry of what waits (scheduled only while a flag of the module is on, see RecurringJobsRegistration).
        services.AddScoped<CheckInLinkDeliveryRetryJob>();

        return services;
    }
}
