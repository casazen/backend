using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The guests of a stay written to the channel manager on their own (E20/F01/US01/T01, decision O1): the org setting, the service that does what the
/// setting allows and its consumer on the event bus. Its own registration, so the exact lists of the other registrations (the bus consumers of the
/// booking operations, the handlers of the Octorate connector) are not touched; <c>Program.cs</c> calls it once, after <c>AddCasazenOctorate</c>.
/// Nothing here is conditional: the behaviour sits behind <c>Features:OctorateConnector</c> and the setting of the org (off by default) where it is used.
/// </summary>
public static class OctorateGuestAutoSendServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenOctorateGuestAutoSend(this IServiceCollection services)
    {
        // The setting of the org, read by the sender endpoint and by the service below.
        services.AddScoped<IOrgGuestAutoSendSetting, OrgGuestAutoSendSetting>();

        // What the setting allows: the stay whose guests are complete and whose identity passed goes through the send path of the wizard, and the retry job
        // sweeps the stays whose event was missed through the same service.
        services.AddScoped<IStayGuestsAutoSendService, StayGuestsAutoSendService>();

        // The consumer of the two facts that make a stay ready: the guests saved and the identity verified.
        services.AddScoped<IEventHandler<DomainEvent>, StayGuestsReadyOctorateHandler>();
        return services;
    }
}
