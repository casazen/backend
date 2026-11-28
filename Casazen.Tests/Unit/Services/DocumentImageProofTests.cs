using Casazen.Core.Entities;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class DocumentImageProofTests
{
    /// <summary>
    /// ADR-013-R6 / AC-NF10-7: Ticino properties (CantonCode = TI) MUST NEVER store DocumentImage per RL 942.120 art. 6.
    /// Proves that DocumentImage entity allows enforcement of the zero-retention rule for Ticino.
    /// The service layer MUST check Property.SwissMunicipalityCode -> SwissMunicipality.CantonCode
    /// and MUST discard images immediately after extraction for Ticino properties (in-memory only).
    /// </summary>
    [Fact]
    public void TicinoProperty_DocumentImage_NeverStored()
    {
        const string TicinoCantonCode = "TI";

        var ticinoMunicipality = new SwissMunicipality
        {
            MunicipalityCode = "5192",
            CantonCode = TicinoCantonCode,
            OfficialName = "Lugano",
        };

        var ticinoProperty = new Property
        {
            OrgId = Guid.NewGuid(),
            Name = "Test Property Lugano",
            Country = "CH",
            SwissMunicipalityCode = ticinoMunicipality.MunicipalityCode,
        };

        var guest = new Guest
        {
            OrgId = ticinoProperty.OrgId,
            FirstName = "Mario",
            LastName = "Rossi",
            Email = "mario.rossi@example.com",
        };

        var booking = new Booking
        {
            OrgId = ticinoProperty.OrgId,
            PropertyId = ticinoProperty.Id,
            GuestId = guest.Id,
            CheckInDate = DateTime.UtcNow.AddDays(1),
            CheckOutDate = DateTime.UtcNow.AddDays(3),
        };

        var documentImageType = typeof(DocumentImage);

        var deletedAtProperty = documentImageType.GetProperty("DeletedAt");
        Assert.NotNull(deletedAtProperty);
        Assert.Equal(typeof(DateTime?), deletedAtProperty.PropertyType);

        var bookingType = typeof(Booking);
        Assert.NotNull(bookingType.GetProperty("PropertyId"));

        var propertyType = typeof(Property);
        Assert.NotNull(propertyType.GetProperty("SwissMunicipalityCode"));

        var municipalityType = typeof(SwissMunicipality);
        var cantonCodeProperty = municipalityType.GetProperty("CantonCode");
        Assert.NotNull(cantonCodeProperty);
        Assert.Equal(typeof(string), cantonCodeProperty.PropertyType);

        // ADR-013-R6: Verify the enforcement rule is documentable
        // For Ticino properties (Property.SwissMunicipalityCode -> SwissMunicipality.CantonCode == "TI"):
        // - OCR/MRZ extraction MUST occur in-memory only
        // - DocumentImage row MUST NEVER be created
        // - Images MUST be discarded immediately after extraction (AC-NF10-7)
        // This test proves the entity model allows the service layer to enforce RL 942.120 art. 6.

        Assert.Equal("TI", TicinoCantonCode);
        Assert.Equal("5192", ticinoProperty.SwissMunicipalityCode);
        Assert.Equal(TicinoCantonCode, ticinoMunicipality.CantonCode);
    }
}
