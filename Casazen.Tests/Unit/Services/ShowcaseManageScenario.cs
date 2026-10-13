using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// What the tests of the customer's own area of a booking (SP-11) add to <see cref="ShowcaseScenario"/>: a booking made and checked,
/// with the credentials its customer types to find it; the supplier's answers; the run of the upkeep.
/// </summary>
internal static class ShowcaseManageScenario
{
    /// <summary>The slug, the code as people read it (<c>XXXXX-XXXXX</c>) and the address that find a booking.</summary>
    public static ShowcaseBookingCredentials CredentialsOf(
        string publicCode,
        string email = ShowcaseScenario.CustomerEmail,
        string slug = ShowcaseScenario.Slug) =>
        new(slug, BookingCodes.Format(publicCode), email);

    /// <summary>A booking whose address was checked (the request exists, <c>Richiesto</c>), and the credentials of its customer.</summary>
    public static async Task<(ServiceRequest Request, ShowcaseBookingCredentials Credentials)> BookedForManagementAsync(
        this ServiceRequestScenario s,
        DateTime? start = null,
        string email = ShowcaseScenario.CustomerEmail,
        string? locale = null)
    {
        var input = await s.InputAsync(start, email, change: locale is null ? null : i => i with { Locale = locale });
        var (request, confirmation) = await s.BookedAsync(input);
        return (request, CredentialsOf(confirmation.PublicCode, email));
    }

    /// <summary>The supplier takes the request (as its console does).</summary>
    public static Task<ServiceRequest> TakeAsSupplierAsync(this ServiceRequestScenario s, Guid requestId, TakeServiceRequestCommand? command = null) =>
        s.Service.TakeAsync(requestId, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, command);

    /// <summary>The supplier proposes another time (the customer has a day to answer).</summary>
    public static Task<ServiceRequest> ProposeAsSupplierAsync(this ServiceRequestScenario s, Guid requestId, DateTime start, string? message = null) =>
        s.Service.ProposeTimeAsync(
            requestId,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new ProposeServiceRequestTimeCommand(start, null, message));

    /// <summary>The upkeep job of the showcase requests, on the clock of the scenario (the flag of the hosts' job is off: not its business).</summary>
    public static ServiceRequestExpiryService ExpiryJob(this ServiceRequestScenario s) =>
        new(
            s.Db,
            new ServiceRequestAutoCancelService(
                s.Db, s.Notifier, Mock.Of<IFeatureFlags>(), NullLogger<ServiceRequestAutoCancelService>.Instance, s.Clock),
            NullLogger<ServiceRequestExpiryService>.Instance,
            s.Clock);
}
