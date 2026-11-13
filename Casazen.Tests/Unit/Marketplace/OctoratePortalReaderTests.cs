using System.Text.Json;
using Casazen.Infrastructure.Octorate;
using Xunit;

namespace Casazen.Tests.Unit.Marketplace;

/// <summary>
/// <see cref="OctorateApiClient.ReadPortal"/> on the shape <c>meta/portals</c> really sends (sandbox answer of 13/11/2026): the public name is
/// <c>externalName</c>, so the portal cache, the Marketplace and the connect wizard show «Booking», never the portal's code.
/// </summary>
public class OctoratePortalReaderTests
{
    [Fact]
    public void ReadPortal_TakesThePublicName_FromExternalName()
    {
        using var document = JsonDocument.Parse(
            """
            { "id": 142, "internalName": "booking", "externalName": " Booking ", "portalCategory": "OTA", "xmlType": true,
              "creatable": true, "enabled": true, "preferred": true, "featureContent": true }
            """);

        var portal = OctorateApiClient.ReadPortal(document.RootElement);

        Assert.NotNull(portal);
        Assert.Equal(("142", "Booking", "OTA"), (portal.PortalCode, portal.Name, portal.PortalCategory));
        Assert.True(portal.Preferred);
    }

    [Fact]
    public void ReadPortal_WithNoPublicName_KeepsTheCode_AndNeverShowsTheInternalName()
    {
        using var document = JsonDocument.Parse("""{ "id": 270, "internalName": "octoevo", "enabled": true }""");

        var portal = OctorateApiClient.ReadPortal(document.RootElement);

        Assert.NotNull(portal);
        Assert.Equal("270", portal.Name);
    }
}
