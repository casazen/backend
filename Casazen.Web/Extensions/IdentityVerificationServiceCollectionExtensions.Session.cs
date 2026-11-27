using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;

namespace Casazen.Web.Extensions;

/// <summary>
/// The session bound to one guest and the segments of the automation (E70/F02/US01, NF-188, NF-194): the part of the hook <c>AddSessionServices</c> (see
/// <c>IdentityVerificationServiceCollectionExtensions.cs</c>). The session itself needs no registration of its own: it is the part <c>Session</c> of the flow service, already
/// registered, and the family of the browser is the pure function <see cref="IdentityChannelClassifier"/>. The segments are registered always and read the flags when they
/// run, so with <c>Features:CheckInIdentitySelfHosted</c> off nothing changes.
/// </summary>
public static partial class IdentityVerificationServiceCollectionExtensions
{
    static partial void AddSessionServices(IServiceCollection services)
    {
        // E70/F02/US01/T02: property × format × channel, observation by default; scoped like the database it reads.
        services.AddScoped<IIdentitySegmentService, IdentitySegmentService>();
    }
}
