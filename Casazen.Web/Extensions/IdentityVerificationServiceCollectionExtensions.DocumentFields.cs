using Casazen.Infrastructure.Identity;

namespace Casazen.Web.Extensions;

/// <summary>
/// The fields of the identity document read by zone and their coherence (E71/F01/US02, NF-190, NF-191): the reading of each value of a side bound to the zone of the
/// format it comes from, and the check that the sides, the dates and the MRZ agree. This is the part of the hook <c>AddDocumentFieldServices</c> (see
/// <c>IdentityVerificationServiceCollectionExtensions.cs</c>). The services read no flag and nothing until they are used: with
/// <c>Features:CheckInIdentitySelfHosted</c> off nobody calls them, and the check-in behaves as before.
/// </summary>
public static partial class IdentityVerificationServiceCollectionExtensions
{
    static partial void AddDocumentFieldServices(IServiceCollection services)
    {
        // Scoped: it reads the MRZ through IMrzExtractionService, which is scoped. The page reader it shares with the document OCR is a singleton, so the engines of
        // MrzOcr:MaxConcurrency are the same ones. Registered under its interface too, one instance for both.
        services.AddScoped<ZonedFieldReader>();
        services.AddScoped<IZonedFieldReader>(provider => provider.GetRequiredService<ZonedFieldReader>());

        // Pure and without state (no I/O, no clock, no configuration): one instance for the whole application.
        services.AddSingleton<DocumentCoherenceChecker>();
    }
}
