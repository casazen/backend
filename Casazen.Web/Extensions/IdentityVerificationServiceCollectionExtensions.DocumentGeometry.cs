using Casazen.Infrastructure.Identity;

namespace Casazen.Web.Extensions;

/// <summary>
/// The geometry of the document of the self-hosted identity verification (E71/F01/US01, NF-189): the inspector of the card (whole, upright, of the layout of its format,
/// with its portrait where the format puts it) and the classifier of the side a picture shows (and of the pair of pictures of a document). This is the part of the hook
/// <c>AddDocumentGeometryServices</c> (see <c>IdentityVerificationServiceCollectionExtensions.cs</c>); the services are registered always and do nothing until the flow asks
/// them, so with <c>Features:CheckInIdentitySelfHosted</c> off nothing changes.
/// </summary>
public static partial class IdentityVerificationServiceCollectionExtensions
{
    static partial void AddDocumentGeometryServices(IServiceCollection services)
    {
        services.AddSingleton<DocumentGeometryInspector>();

        services.AddSingleton<DocumentSideClassifier>();
    }
}
