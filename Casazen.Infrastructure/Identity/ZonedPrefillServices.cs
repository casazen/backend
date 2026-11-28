using Microsoft.Extensions.DependencyInjection;

namespace Casazen.Infrastructure.Identity;

public sealed class ZonedPrefillServices(IServiceProvider provider)
{
    public DocumentPictureAnalyzer? Pictures => provider.GetService<DocumentPictureAnalyzer>();

    public DocumentFormatRegistry? Formats => provider.GetService<DocumentFormatRegistry>();

    public DocumentSideClassifier? Sides => provider.GetService<DocumentSideClassifier>();
}
