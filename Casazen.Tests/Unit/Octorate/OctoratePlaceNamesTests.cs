using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Octorate;
using Casazen.Core.Services;
using Casazen.Infrastructure.Octorate;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// E20/F01/US01/T01 (decision O2): the places of a guest go to Octorate with the exact names of Octorate's own table, found by the police codes CasaZen
/// holds. The sandbox accepted a complete guest only with names it recognizes, so a place the table does not know holds that guest back instead of
/// travelling as the guest typed it. Pure rules and the cache of the table; the sends are proved in <see cref="OctorateGuestAutoSendTests"/>.
/// </summary>
public class OctoratePlaceNamesTests
{
    private static readonly OctoratePlaceTable Table = new(
    [
        new OctoratePlaceEntry("ROMA", "412058091", "CITY"),
        new OctoratePlaceEntry("COMO", "403013075", "CITY"),
        new OctoratePlaceEntry("SAN PIETRO", "403011001", "CITY"),
        new OctoratePlaceEntry("San Pietro", "405022002", "CITY"),
    ]);

    /// <summary>
    /// Proof (data, O2): the police code of a comune, as CasaZen holds it, becomes the exact name of Octorate's table in the payload, whatever the guest
    /// typed (case, accents, a nickname of the place); the place of issue of a document too.
    /// </summary>
    [Fact]
    public void OctoratePlaceNames_PoliceCodeToRecognizedName_Test()
    {
        var guest = Guest();
        guest.BirthComuneCode = "412058091";
        guest.BirthComuneName = "roma capitale";
        guest.ResidenceComuneCode = "403013075";
        guest.ResidenceComuneName = "como (co)";
        guest.DocumentIssuePlaceCode = "412058091";
        guest.DocumentIssuePlaceName = "Roma";

        var places = OctoratePlaceRules.Resolve(guest, Table);

        Assert.True(places.IsRecognized);
        Assert.Equal(("ROMA", "COMO", "ROMA"), (places.BirthCity, places.ResidenceCity, places.DocumentIssuePlace));

        // What leaves is what the table says, not what the guest typed.
        var upsert = OctorateCheckInMapper.Map(guest, new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), draft: false, null, null, places);
        Assert.Equal(("ROMA", "COMO", "ROMA"), (upsert.BirthCity, upsert.ResidenceCity, upsert.DocumentIssuePlace));

        // Without the table (an old caller) the names go as typed, as before.
        var legacy = OctorateCheckInMapper.Map(guest, new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 23), draft: false, null, null);
        Assert.Equal(("roma capitale", "como (co)"), (legacy.BirthCity, legacy.ResidenceCity));
    }

    [Fact]
    public void ACodeTheTableDoesNotKnow_HoldsThatGuestBack_NamingTheField()
    {
        var guest = Guest();
        guest.BirthComuneCode = "499999999"; // not in the table, whatever the guest typed
        guest.BirthComuneName = "Roma";
        guest.ResidenceComuneCode = "403013075";
        guest.DocumentIssuePlaceCode = "488888888";

        var places = OctoratePlaceRules.Resolve(guest, Table);

        Assert.False(places.IsRecognized);
        Assert.Equal([OctoratePlaceRules.BirthComuneField, OctoratePlaceRules.DocumentIssuePlaceField], places.UnrecognizedFields);
        Assert.Equal("birthComune,documentIssuePlace", places.FieldsText);
        Assert.Null(places.BirthCity);
        Assert.Equal("COMO", places.ResidenceCity);
    }

    [Fact]
    public void AGuestWithNoCode_IsFoundByItsNameOnlyWhenExactlyOnePlaceCarriesIt()
    {
        var guest = Guest();
        guest.BirthComuneCode = null;
        guest.BirthComuneName = " rOmA ";
        guest.ResidenceComuneCode = null;
        guest.ResidenceComuneName = "San Pietro"; // two different places carry this name: never guessed

        var places = OctoratePlaceRules.Resolve(guest, Table);

        Assert.Equal("ROMA", places.BirthCity);
        Assert.Equal([OctoratePlaceRules.ResidenceComuneField], places.UnrecognizedFields);
    }

    [Fact]
    public void AForeignGuestWithAForeignDocument_NeverNeedsTheTable_AndIsNeverHeldBackForAState()
    {
        var foreign = Guest();
        foreign.BornInItaly = false;
        foreign.BirthCountryName = "GERMANIA";
        foreign.ResidenceInItaly = false;
        foreign.ResidenceCountryName = "SVIZZERA";
        foreign.DocumentIssuePlaceCode = "100000216"; // a state: not a comune, not looked up
        foreign.DocumentIssuePlaceName = "GERMANIA";

        Assert.False(OctoratePlaceRules.NeedsItalianPlaces(foreign));
        var places = OctoratePlaceRules.Resolve(foreign, Table);
        Assert.True(places.IsRecognized);
        Assert.Equal("GERMANIA", places.DocumentIssuePlace);
        Assert.Null(places.BirthCity);
        Assert.Null(places.ResidenceCity);

        // A member of a family has no document: its places are the comune of birth and of residence only.
        var member = Guest();
        member.Type = StayGuestType.FamilyMember;
        Assert.True(OctoratePlaceRules.NeedsItalianPlaces(member));
        Assert.Null(OctoratePlaceRules.Resolve(member, Table).DocumentIssuePlace);
    }

    [Fact]
    public async Task TheTableIsReadOncePerCountryAndKeptForADay()
    {
        var client = new Mock<IOctorateApiClient>();
        client
            .Setup(c => c.GetCheckInCitiesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new OctoratePlaceEntry("ROMA", "412058091", "CITY")]);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var names = new OctoratePlaceNames(client.Object, cache);

        var first = await names.GetPlacesAsync("IT", CancellationToken.None);
        var again = await names.GetPlacesAsync("it", CancellationToken.None);
        await names.GetPlacesAsync("CH", CancellationToken.None);

        Assert.Same(first, again);
        Assert.Equal("ROMA", first.NameOf("412058091"));
        client.Verify(c => c.GetCheckInCitiesAsync("IT", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.GetCheckInCitiesAsync("CH", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(TimeSpan.FromHours(24), OctoratePlaceNames.CacheDuration);
    }

    [Fact]
    public async Task ATableThatCannotBeRead_IsTheTypedErrorOfTheClient_AndIsNotKept()
    {
        var client = new Mock<IOctorateApiClient>();
        client
            .SetupSequence(c => c.GetCheckInCitiesAsync("IT", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OctorateServerException(503))
            .ReturnsAsync([new OctoratePlaceEntry("ROMA", "412058091", "CITY")]);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var names = new OctoratePlaceNames(client.Object, cache);

        await Assert.ThrowsAsync<OctorateServerException>(() => names.GetPlacesAsync("IT", CancellationToken.None));
        var later = await names.GetPlacesAsync("IT", CancellationToken.None);

        Assert.Equal(1, later.Count);
    }

    [Fact]
    public void TheAnswerOfTheClient_IsABareArrayOrADataWrapper_AndItemsWithoutANameOrACodeAreLeftOut()
    {
        const string wrapped = """
            {"data":[
              {"internalName":"ABBADIA CERRETO","regionType":"CITY","countryPoliceCode":"403098001"},
              {"internalName":"","regionType":"CITY","countryPoliceCode":"403097001"},
              {"internalName":"SENZA CODICE","regionType":"CITY"},
              "not an object"
            ],"page":{}}
            """;
        const string bare = """[{"internalName":"ROMA","countryPoliceCode":"412058091"}]""";

        using var wrappedDocument = JsonDocument.Parse(wrapped);
        using var bareDocument = JsonDocument.Parse(bare);

        var fromWrapper = OctorateApiClient.ReadCheckInCities(wrappedDocument.RootElement);
        var fromArray = OctorateApiClient.ReadCheckInCities(bareDocument.RootElement);

        Assert.Equal([new OctoratePlaceEntry("ABBADIA CERRETO", "403098001", "CITY")], fromWrapper);
        Assert.Equal([new OctoratePlaceEntry("ROMA", "412058091", null)], fromArray);
    }

    /// <summary>A complete head of family born, living and holding a document in Italy, every place with its police code.</summary>
    private static StayGuest Guest() => new()
    {
        BookingId = Guid.NewGuid(),
        Position = 0,
        Type = StayGuestType.HeadOfFamily,
        FirstName = "Anna",
        LastName = "Bianchi",
        Gender = Gender.Female,
        DateOfBirth = new DateTime(1985, 3, 2, 0, 0, 0, DateTimeKind.Utc),
        BornInItaly = true,
        BirthComuneCode = "412058091",
        BirthComuneName = "Roma",
        BirthProvince = "RM",
        CitizenshipCode = OctorateCheckInMapper.ItalyStateCode,
        CitizenshipName = "ITALIA",
        ResidenceInItaly = true,
        ResidenceComuneCode = "403013075",
        ResidenceComuneName = "Como",
        DocumentType = GuestDocumentType.IdentityCard,
        DocumentNumber = "AB1234567",
        DocumentIssuePlaceCode = "403013075",
        DocumentIssuePlaceName = "Como",
    };
}
