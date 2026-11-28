using Casazen.Core.Services;
using Casazen.Infrastructure.Identity;

namespace Casazen.Web.Extensions;

public static partial class IdentityVerificationServiceCollectionExtensions
{
    static partial void AddDocumentReadingServices(IServiceCollection services)
    {
        services.AddScoped<ICheckInDocumentReader, LocalCheckInDocumentReader>();
    }
}
