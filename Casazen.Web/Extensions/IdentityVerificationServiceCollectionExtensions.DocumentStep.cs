using Casazen.Infrastructure.Identity;

namespace Casazen.Web.Extensions;

public static partial class IdentityVerificationServiceCollectionExtensions
{
    static partial void AddDocumentStepServices(IServiceCollection services)
    {
        services.AddScoped<DocumentPictureAnalyzer>();

        services.AddScoped<ZonedPrefillServices>();
    }
}
