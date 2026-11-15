using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Casazen.Infrastructure.Services;
using Casazen.Web.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// E20/F01/US01/T01: what the registrations of the guests written to Octorate on their own add, pinned as exact lists, as the other registrations of the
/// bus are. The behaviour is proved with the handler built by hand, so without these tests taking a registration out, or the optional parameter that the
/// registration turns on, would fail nothing: the guests would simply never go on their own, or go with the names the guest typed.
/// </summary>
public class OctorateGuestAutoSendRegistrationTests
{
    [Fact]
    public void AddCasazenOctorateGuestAutoSend_RegistersTheSettingTheServiceAndTheConsumerOnTheBus_AndNothingElse()
    {
        var services = new ServiceCollection();

        services.AddCasazenOctorateGuestAutoSend();

        var registered = services.Select(d => (d.ServiceType, d.ImplementationType, d.Lifetime)).ToList();
        Assert.Equal(
            [
                (typeof(IOrgGuestAutoSendSetting), typeof(OrgGuestAutoSendSetting), ServiceLifetime.Scoped),
                (typeof(IStayGuestsAutoSendService), typeof(StayGuestsAutoSendService), ServiceLifetime.Scoped),
                (typeof(IEventHandler<DomainEvent>), typeof(StayGuestsReadyOctorateHandler), ServiceLifetime.Scoped),
            ],
            registered);
    }

    [Fact]
    public void TheConnectorRegistersThePlaceNames_AndItsHandlerOfTheArrivalStaysTheOnlyOneOfItsOwn()
    {
        var services = new ServiceCollection();

        services.AddCasazenOctorate(new ConfigurationBuilder().Build());

        // The sync service takes the places as an optional parameter: this registration is what turns the recognized names on.
        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(IOctoratePlaceNames));
        Assert.Equal((typeof(OctoratePlaceNames), ServiceLifetime.Scoped), (descriptor.ImplementationType, descriptor.Lifetime));
        // The handler of the guests has its own registration: the connector still registers the one handler of the arrival and the departure.
        Assert.Equal(
            [typeof(OctorateBookingStatePushHandler)],
            services.Where(d => d.ServiceType == typeof(IEventHandler<DomainEvent>)).Select(d => d.ImplementationType).ToList());
    }

    [Fact]
    public void TheTwoRegistrationsTogether_PutTheHandlersOnTheBusInTheOrderOfProgram()
    {
        var services = new ServiceCollection();

        services.AddCasazenOctorate(new ConfigurationBuilder().Build());
        services.AddCasazenOctorateGuestAutoSend();

        Assert.Equal(
            [typeof(OctorateBookingStatePushHandler), typeof(StayGuestsReadyOctorateHandler)],
            services.Where(d => d.ServiceType == typeof(IEventHandler<DomainEvent>)).Select(d => d.ImplementationType).ToList());
    }
}
