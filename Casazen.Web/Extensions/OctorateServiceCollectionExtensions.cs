using Casazen.Core.Events;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Photos;
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

        services.AddHttpClient(OctorateTokenService.HttpClientName, OctorateHttp.Configure);
        services.AddScoped<IOctorateTokenService, OctorateTokenService>();

        services.AddSingleton<OctoratePartnerTokenCache>();

        services.AddHttpClient<IOctorateApiClient, OctorateApiClient>(OctorateHttp.Configure);
        services.AddScoped<IOctorateOrgScope, OctorateOrgScope>();
        services.AddScoped<OctorateMasterBoundary>();
        services.AddSingleton<OctorateRateLimiter>();

        services.AddSingleton<OctorateWebhookVerifier>();

        services.AddOptions<OctorateOptions>()
            .BindConfiguration(OctorateOptions.SectionName)
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OctorateOptions>, OctorateOptionsValidator>();
        services.AddScoped<OctorateOAuthStateService>();
        services.AddScoped<IOctorateConnectService, OctorateConnectService>();

        services.AddScoped<IChannelMappingService, ChannelMappingService>();

        services.AddSingleton<BookingStateMapper>();
        services.AddScoped<IBookingImportService, BookingImportService>();
        services.AddScoped<OctorateBookingImportJob>();

        services.AddScoped<IEventHandler<OctorateWebhookReceivedEvent>, BookingCreatedHandler>();
        services.AddScoped<IEventHandler<OctorateWebhookReceivedEvent>, BookingModifiedHandler>();
        services.AddScoped<IEventHandler<OctorateWebhookReceivedEvent>, BookingCancelledHandler>();
        services.AddScoped<OctorateBookingSyncJob>();
        services.AddScoped<IEventHandler<DomainEvent>, OctorateBookingStatePushHandler>();
        services.AddScoped<OctorateBookingStatePushJob>();

        services.AddScoped<ICalMigrationService>();
        services.AddScoped<CalendarSyncJob>();

        // The import of the listings that already exist on OTAs (E02/F01/US03, D6): the orchestrator, the guard that keeps existing inventory
        // from being created again (consumed by E02/F03/US01/T06) and the minimal handoff of the person to the Octorate panel for the access to a
        // channel, which the panel steps service of E10 replaces by registering its own IChannelAccessHandoff.
        services.AddScoped<IChannelAccessHandoff, OnboardingChannelAccessHandoff>();
        services.AddScoped<IExistingInventoryImportService, ExistingInventoryImportService>();
        services.AddScoped<IInventoryScenarioGuard, InventoryScenarioGuard>();

        // The creation of a property on Octorate (E02/F03/US01/T06, PO-19): only for a property declared never published; it saves the refresh
        // token Octorate gives once and confirms the new accommodation as the mapping of the property.
        services.AddScoped<OctoratePropertyCreator>();
        services.AddScoped<IOctoratePropertyCreator>(provider => provider.GetRequiredService<OctoratePropertyCreator>());
        services.AddScoped<OctoratePreparationService>();

        // The publication of a listing (E02/F03/US01/T02, NF-27): the content channels and their rules (ListingChannels), the public photos, the
        // reader of the groups of the content (the baseline of the changes made outside CasaZen), the «To do» of the channels, the service and its
        // job. The job runs only while Features:OctorateConnector is on (the service does nothing otherwise).
        services.AddOptions<ListingChannelOptions>().BindConfiguration(ListingChannelOptions.SectionName);
        services.AddScoped<PublicPhotoUrlService>();
        services.AddScoped<ListingPhotoSync>();
        services.AddScoped<OctorateContentReader>();
        services.AddScoped<ListingTodos>();
        services.AddScoped<IContentPushService, ContentPushService>();
        services.AddScoped<ListingPublishJob>();

        services.AddScoped<IListingStatusService, ListingStatusService>();
        services.AddScoped<IOutsideChangeService, OutsideChangeService>();
        services.AddScoped<IEventHandler<OctorateWebhookReceivedEvent>, ContentNotificationHandler>();
        services.AddScoped<ListingStatusSyncJob>();
        services.AddScoped<ListingChannelHealthJob>();
        services.AddScoped<ListingOutsideChangeJob>();

        // The check-in of a stay (E03/F01/US01/T03): the guests sent to Octorate, the NO_SHOW (queued by the check-in endpoints; the arrival and
        // the departure are pushed by the one handler above, never by a second one) and the retry every 15 minutes (scheduled only while
        // Features:OctorateConnector is on). Keys Octorate:WriteCityTaxValues, CheckInMaxAttempts and SendGuestsWhenSenderIsNotChannelManager.
        services.AddScoped<ICheckInOctorateSyncService, CheckInOctorateSyncService>();
        services.AddScoped<OctorateStayStateJob>();
        services.AddScoped<OctorateCheckInRetryJob>();

        services.AddScoped<IOctoratePlaceNames, OctoratePlaceNames>();

        return services;
    }
}
