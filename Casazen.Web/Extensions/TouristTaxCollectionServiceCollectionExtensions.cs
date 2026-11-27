using Casazen.Core.Events;
using Casazen.Core.Services;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Services;

namespace Casazen.Web.Extensions;

/// <summary>
/// The tourist tax paid online in the guest check-in (epic E25, NF-119, decision O12): the collection on the stay's tourist tax (what is due, the
/// PaymentIntent on the organization's connected account, the webhook, the refunds, «Pagata in struttura», the setting of the organization) and the consumer of
/// the booking events that keeps it in step with the stay (the refund of a stay cancelled before any night, the «To do» of a difference owed back). Both are
/// always registered; what they do is behind <c>Features:TouristTaxOnlinePayment</c>.
/// </summary>
public static class TouristTaxCollectionServiceCollectionExtensions
{
    public static IServiceCollection AddCasazenTouristTaxCollection(this IServiceCollection services)
    {
        services.AddScoped<ITouristTaxCollectionService, TouristTaxCollectionService>();
        services.AddScoped<IEventHandler<DomainEvent>, TouristTaxCollectionEventHandler>();
        return services;
    }
}
