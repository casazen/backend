using Casazen.Infrastructure.Identity;

namespace Casazen.Web.Extensions;

public static partial class IdentityVerificationServiceCollectionExtensions
{
    static partial void AddDocumentFieldServices(IServiceCollection services)
    {
        services.AddScoped<ZonedFieldReader>();
        services.AddScoped<IZonedFieldReader>(provider => provider.GetRequiredService<ZonedFieldReader>());

        services.AddSingleton<DocumentCoherenceChecker>();
    }
}
