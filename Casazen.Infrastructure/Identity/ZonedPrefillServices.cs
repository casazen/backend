using Microsoft.Extensions.DependencyInjection;

namespace Casazen.Infrastructure.Identity;

/// <summary>
/// The services the zoned prefill of <c>read-document</c> takes from the container only when it is asked for (E71/F01/US03/T01): the reading of today needs none of them, and a
/// container built for the reading of today does not have to be able to build them. It is the one class of this namespace that holds the container, and it names what it takes: the
/// architecture test <c>IdentityPathEgressArchitectureTests</c> forbids the container anywhere else in the namespace and checks that every service this class gives is a type of the
/// namespace itself, so nothing that could let a picture out (an AI provider, an HTTP client) can be resolved through it unnoticed. A service the application did not register is null.
/// </summary>
public sealed class ZonedPrefillServices(IServiceProvider provider)
{
    /// <summary>The decoder and the geometry of a picture, and the reader of its zones.</summary>
    public DocumentPictureAnalyzer? Pictures => provider.GetService<DocumentPictureAnalyzer>();

    /// <summary>The registry of the formats: the format of the card is told from the MRZ, or it is the card the registry holds.</summary>
    public DocumentFormatRegistry? Formats => provider.GetService<DocumentFormatRegistry>();

    /// <summary>The side a picture shows, from what the geometry found in it.</summary>
    public DocumentSideClassifier? Sides => provider.GetService<DocumentSideClassifier>();
}
