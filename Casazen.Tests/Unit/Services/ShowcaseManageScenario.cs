using Casazen.Core.Entities;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Casazen.Tests.Unit.Services;

internal static class ShowcaseManageScenario
{
    public static ShowcaseBookingCredentials CredentialsOf(
        string publicCode,
        string email = ShowcaseScenario.CustomerEmail,
        string slug = ShowcaseScenario.Slug) =>
        new(slug, BookingCodes.Format(publicCode), email);

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

    public static Task<ServiceRequest> TakeAsSupplierAsync(this ServiceRequestScenario s, Guid requestId, TakeServiceRequestCommand? command = null) =>
        s.Service.TakeAsync(requestId, s.SupplierOrgId, ServiceRequestScenario.SupplierUserId, command);

    public static Task<ServiceRequest> ProposeAsSupplierAsync(this ServiceRequestScenario s, Guid requestId, DateTime start, string? message = null) =>
        s.Service.ProposeTimeAsync(
            requestId,
            s.SupplierOrgId,
            ServiceRequestScenario.SupplierUserId,
            new ProposeServiceRequestTimeCommand(start, null, message));

    public static ServiceRequestExpiryService ExpiryJob(this ServiceRequestScenario s) =>
        new(
            s.Db,
            new ServiceRequestAutoCancelService(
                s.Db, s.Notifier, Mock.Of<IFeatureFlags>(), NullLogger<ServiceRequestAutoCancelService>.Instance, s.Clock),
            NullLogger<ServiceRequestExpiryService>.Instance,
            s.Clock);
}
