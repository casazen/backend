using Casazen.Core.Events;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Casazen.Web.Configuration;
using Microsoft.Extensions.Options;

namespace Casazen.Web.Extensions;

/// <summary>
/// The one place where the Octorate connector of epic E02 is registered (ADR-023): its services, HTTP clients, handlers and jobs.
/// Every Octorate task adds its registrations here, none in <c>ServiceCollectionExtensions</c>; <c>Program.cs</c> calls it once.
/// Nothing here is conditional: what is not ready yet sits behind its feature flag where it is used, not by being left out of DI.
/// </summary>
public static class OctorateServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenOctorate(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // OAuth tokens of the connections (E02/F01/US01/T01): the refresh call has an HttpClient of its own (30 s, https, User-Agent).
        services.AddHttpClient(OctorateTokenService.HttpClientName, OctorateHttp.Configure);
        services.AddScoped<IOctorateTokenService, OctorateTokenService>();

        // The token of the application itself (the partner login, E09/F01/US01/T02): one for the whole process, reused while valid.
        services.AddSingleton<OctoratePartnerTokenCache>();

        // The API client (E02/F01/US01/T02), always registered: the org of every call comes from the scope of the DI scope, the
        // rate-limit buckets are the application's (one per org).
        services.AddHttpClient<IOctorateApiClient, OctorateApiClient>(OctorateHttp.Configure);
        services.AddScoped<IOctorateOrgScope, OctorateOrgScope>();
        services.AddSingleton<OctorateRateLimiter>();

        // The webhook (E02/F01/US01/T03): the secret of the address, compared in constant time; the controller needs no registration.
        services.AddSingleton<OctorateWebhookVerifier>();

        // The consent and the connection (E02/F01/US02/T01): the Octorate options, validated at startup with the flag on, the
        // single-use states and the connect service. The code exchange uses the HttpClient of the token service.
        services.AddOptions<OctorateOptions>()
            .BindConfiguration(OctorateOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OctorateOptions>, OctorateOptionsValidator>();
        services.AddScoped<OctorateOAuthStateService>();
        services.AddScoped<IOctorateConnectService, OctorateConnectService>();

        // The mapping of the listings to the CasaZen properties and the import (E02/F01/US02/T03); thresholds in Octorate:Mapping.
        services.AddScoped<IChannelMappingService, ChannelMappingService>();

        // The bookings of the channel manager (E02/F02/US01/T01): the state mapper (no state, shared), the import service and the daily job.
        // The job is only scheduled while Features:OctorateConnector is on (RecurringJobsRegistration).
        services.AddSingleton<BookingStateMapper>();
        services.AddScoped<IBookingImportService, BookingImportService>();
        services.AddScoped<OctorateBookingImportJob>();

        // The notifications and the reconciliation of the bookings (E02/F02/US01/T02): one handler for each kind of notification of a
        // reservation (they re-read through the import service, the event bus runs each in its own scope under the org of the event), the
        // reconciliation every six hours, and the push of the arrival and the departure CasaZen owns (a handler of the check-in and check-out
        // events that queues the job, which sends the state).
        services.AddScoped<IEventHandler<OctorateWebhookReceivedEvent>, BookingCreatedHandler>();
        services.AddScoped<IEventHandler<OctorateWebhookReceivedEvent>, BookingModifiedHandler>();
        services.AddScoped<IEventHandler<OctorateWebhookReceivedEvent>, BookingCancelledHandler>();
        services.AddScoped<OctorateBookingSyncJob>();
        services.AddScoped<IEventHandler<DomainEvent>, OctorateBookingStatePushHandler>();
        services.AddScoped<OctorateBookingStatePushJob>();

        // The calendar of the listings (E02/F02/US01/T03): the hand-over of the iCal feeds of a channel to Octorate, and the job that reads the
        // calendar every 15 minutes and calls it after a successful read (scheduled only while Features:OctorateConnector is on).
        services.AddScoped<ICalMigrationService>();
        services.AddScoped<CalendarSyncJob>();

        // The check-in of a stay (E03/F01/US01/T03): the guests sent to Octorate, the states of the stay (a handler of the check-in and
        // check-out events that queues the job; the no-show is queued by the check-in endpoints) and the retry every 15 minutes (scheduled
        // only while Features:OctorateConnector is on). Keys Octorate:WriteCityTaxValues, CheckInMaxAttempts and
        // SendGuestsWhenSenderIsNotChannelManager.
        services.AddScoped<ICheckInOctorateSyncService, CheckInOctorateSyncService>();
        services.AddScoped<IEventHandler<DomainEvent>, OctorateStayStateHandler>();
        services.AddScoped<OctorateStayStateJob>();
        services.AddScoped<OctorateCheckInRetryJob>();

        return services;
    }
}
