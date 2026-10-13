namespace Casazen.Web.Resources;

/// <summary>
/// Marker type for <c>IStringLocalizer&lt;SharedResources&gt;</c>: the single resource set of the API, read from
/// <c>Resources/SharedResources.resx</c> (Italian, default) and <c>Resources/SharedResources.en.resx</c> (English).
/// The resource name is the type's full name (<c>Casazen.Web.Resources.SharedResources</c>), which is why
/// <c>AddLocalization</c> must not set a <c>ResourcesPath</c>. Every key must exist in both files.
/// </summary>
public class SharedResources
{
}
