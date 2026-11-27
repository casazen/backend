using Casazen.Infrastructure.Identity;

namespace Casazen.Web.Extensions;

/// <summary>
/// The document step of the self-hosted identity verification (E71/F01/US03/T01, NF-189, NF-190, NF-191): the part of the hook <c>AddDocumentStepServices</c> (see
/// <c>IdentityVerificationServiceCollectionExtensions.cs</c>). The checks of a picture of the document are the part <c>DocumentChecks</c> of the flow service, already registered, and the
/// zoned prefill is <c>LocalCheckInDocumentReader</c>, registered by the part <c>DocumentReading</c>; both take the services below from the container. They read the flags when they
/// run, so with <c>Features:CheckInIdentitySelfHosted</c> off nothing changes.
/// </summary>
public static partial class IdentityVerificationServiceCollectionExtensions
{
    static partial void AddDocumentStepServices(IServiceCollection services)
    {
        // Scoped: it holds the reader of the zones, which is scoped (it reads the MRZ through IMrzExtractionService), and the inspector of the geometry, a singleton. It keeps nothing of a
        // picture: each picture is opened for a call and zeroed when the call ends.
        services.AddScoped<DocumentPictureAnalyzer>();

        // What the zoned prefill takes from the container only when it is asked for (the reading of today needs none of it): the one holder of the container of the identity namespace.
        services.AddScoped<ZonedPrefillServices>();
    }
}
