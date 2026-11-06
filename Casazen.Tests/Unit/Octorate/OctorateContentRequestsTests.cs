using System.Text.Json.Nodes;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.SiteProfile;
using Casazen.Infrastructure.Octorate;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The bodies CasaZen writes to the content operations of Octorate and the reading of its answers (E02/F03/US01/T02, T06, T07): the creation of an
/// accommodation never asks the host's address to be used for the credentials, Octorate's refusal comes back by field, a state of the process that
/// CasaZen does not know is never a success, and the baseline of a channel row survives a round trip.
/// </summary>
public class OctorateContentRequestsTests
{
    private static OctoratePropertyCreate Request(string? network = null) => new()
    {
        Name = "Casa del Lago",
        Address = "Via del Lago 1",
        City = "Como",
        ZipCode = "22100",
        Country = "IT",
        Latitude = 45.4642m,
        Longitude = 9.19m,
        Currency = "EUR",
        ContactEmail = "host@example.com",
        ContactPhone = "+390212345678",
        ContactFirstName = "Giulia",
        ContactLastName = "Rossi",
        AlternativeEmail = "octorate-0123@owners.casazen.test",
        NetworkName = network,
    };

    [Fact]
    public void PropertyCreateBody_SendsNoWelcomeUser_TheCasaZenAddressAndBothIsoMembers()
    {
        var body = JsonNode.Parse(OctorateContentRequests.PropertyCreateBody(Request()))!.AsObject();

        Assert.False(body["welcome_user"]!.GetValue<bool>());
        Assert.Equal("octorate-0123@owners.casazen.test", body["welcome_alternative_email"]!.GetValue<string>());
        Assert.Equal("IT", body["country"]!.GetValue<string>());
        Assert.Equal("IT", body["isoCountry"]!.GetValue<string>());
        Assert.Equal("EUR", body["isoCurrency"]!.GetValue<string>());
        Assert.Null(body["network_name"]);
        Assert.Null(body["accept_duplicates"]);
        Assert.Equal("host@example.com", body["contact"]!["email"]!.GetValue<string>());
        Assert.Equal("OtherNetwork", JsonNode.Parse(OctorateContentRequests.PropertyCreateBody(Request("OtherNetwork")))!["network_name"]!.GetValue<string>());
    }

    [Fact]
    public void ReadCreated_NeedsBothTheAccommodationAndTheRefreshToken()
    {
        var created = OctorateContentRequests.ReadCreated("""{"status":"created","codice":["585492"],"ids":"585492","refresh_token":"r-1"}""");

        Assert.NotNull(created);
        Assert.Equal("585492", created.AccommodationId);
        Assert.Equal("r-1", created.RefreshToken);
        Assert.DoesNotContain("r-1", created.ToString(), StringComparison.Ordinal);
        Assert.Null(OctorateContentRequests.ReadCreated("""{"status":"created","codice":["585492"]}"""));
        Assert.Null(OctorateContentRequests.ReadCreated("""{"status":"created","refresh_token":"r-1"}"""));
        Assert.Null(OctorateContentRequests.ReadCreated("not json"));
    }

    [Fact]
    public void TranslateCreateError_ParamsExemptionComesBackByField_AndTheUnknownNetworkIsItsOwnError()
    {
        const string body = """{"type":"ApiParamsExemption","message":"Invalid","nested":[{"machine_name":"zipCode","message":"not valid"},{"machine_name":"country"}]}""";

        var rejected = Assert.IsType<OctoratePropertyRejectedException>(OctorateContentRequests.TranslateCreateError(400, body));
        var network = OctorateContentRequests.TranslateCreateError(412, "{}");

        Assert.Equal("octorate_property_invalid", rejected.Code);
        Assert.Equal(422, rejected.ResponseStatusCode);
        Assert.Equal(["zipCode", "country"], rejected.Fields.Select(f => f.Field));
        Assert.Equal("not valid", rejected.Fields[0].Message);
        Assert.IsType<OctorateNetworkUnknownException>(network);
        Assert.Null(OctorateContentRequests.TranslateCreateError(400, """{"type":"SomethingElse"}"""));
        Assert.Null(OctorateContentRequests.TranslateCreateError(500, "{}"));
    }

    [Fact]
    public void ReadProcess_AStateItDoesNotKnowIsNeverASuccess_AndAListOfErrorsIsAPartialRefusal()
    {
        Assert.Equal(OctorateProcessState.Done, OctorateContentRequests.ReadProcess("""{"status":"DONE"}""").State);
        Assert.Equal(OctorateProcessState.Pending, OctorateContentRequests.ReadProcess("""{"data":{"status":"WAIT"}}""").State);
        var failed = OctorateContentRequests.ReadProcess("""{"status":"ERROR","message":"Title too long"}""");
        Assert.Equal(OctorateProcessState.Failed, failed.State);
        Assert.Equal("Title too long", failed.Reason);
        Assert.Equal(OctorateProcessState.Unknown, OctorateContentRequests.ReadProcess("""{"status":"SURPRISE"}""").State);
        Assert.Equal(OctorateProcessState.Unknown, OctorateContentRequests.ReadProcess("<html>").State);
        var partial = OctorateContentRequests.ReadProcess("""{"status":"DONE","errors":[{"message":"No photos"}]}""");
        Assert.Equal(OctorateProcessState.Failed, partial.State);
        Assert.Equal("No photos", partial.Reason);
    }

    [Fact]
    public void ReadPhotoEntry_TakesTheIdTheStateAndTheNameOfADonePhoto()
    {
        var waiting = OctorateContentRequests.ReadPhotoEntry("""{"id":77,"status":"WAIT"}""");
        var done = OctorateContentRequests.ReadPhotoEntry("""{"id":77,"status":"DONE","name":"abc.jpg"}""");
        var error = OctorateContentRequests.ReadPhotoEntry("""{"id":77,"status":"ERROR","message":"unreachable"}""");

        Assert.Equal(new OctoratePhotoQueueEntry("77", OctoratePhotoQueueState.Waiting, null, null), waiting);
        Assert.Equal(new OctoratePhotoQueueEntry("77", OctoratePhotoQueueState.Done, "abc.jpg", null), done);
        Assert.Equal(OctoratePhotoQueueState.Error, error!.State);
        Assert.Null(OctorateContentRequests.ReadPhotoEntry("""{"status":"WAIT"}"""));
    }

    [Fact]
    public void ReadContentRooms_AndTheirForm_DoNotDependOnTheOrderOctorateAnswersIn()
    {
        var first = OctorateContentRequests.ReadContentRooms(
            """[{"index":1,"room":{"type":"BEDROOM","roomNumber":2,"maxGuests":1,"defaultSubRoomBeds":[{"bedType":"SINGLE","quantity":1}]}},{"index":0,"room":{"type":"BEDROOM","roomNumber":1,"maxGuests":2,"subRoomConfigurations":[{"beds":[{"bedType":"DOUBLE","quantity":1}]}]}}]""");
        var second = OctorateContentRequests.ReadContentRooms(
            """[{"room":{"type":"BEDROOM","roomNumber":1,"maxGuests":2,"defaultSubRoomBeds":[{"bedType":"DOUBLE","quantity":1}]}},{"room":{"type":"BEDROOM","roomNumber":2,"maxGuests":1,"defaultSubRoomBeds":[{"bedType":"SINGLE","quantity":1}]}}]""");

        Assert.Equal([1, 2], first.Select(r => r.RoomNumber));
        Assert.Equal(OctorateContentReader.RoomsForm(first), OctorateContentReader.RoomsForm(second));
    }

    [Fact]
    public void MapperRooms_NumberTheRoomsFromOneAndCountTheGuestsOfTheirBeds()
    {
        var rooms = OctorateContentMapper.Rooms(
        [
            new PropertyRoom { Position = 1, Type = PropertyRoomType.LivingRoom, Beds = [new RoomBed(BedType.SofaBed, 1)] },
            new PropertyRoom { Position = 0, Type = PropertyRoomType.Bedroom, Beds = [new RoomBed(BedType.Double, 1), new RoomBed(BedType.Single, 2), new RoomBed(BedType.Crib, 1)] },
        ]);

        Assert.Equal([1, 2], rooms.Select(r => r.RoomNumber));
        Assert.Equal("BEDROOM", rooms[0].Type);
        Assert.Equal(4, rooms[0].MaxGuests);
        Assert.Equal("LIVING_ROOM", rooms[1].Type);
        Assert.Equal(2, rooms[1].MaxGuests);
    }

    [Fact]
    public void MapperRules_SendOnlyWhatTheHostDeclared_AndAnOnRequestPolicyIsNotAYes()
    {
        var none = OctorateContentMapper.ReservationRules(new Property());
        var declared = OctorateContentMapper.ReservationRules(new Property
        {
            PetsPolicy = PetsPolicy.OnRequest,
            SmokingPolicy = SmokingPolicy.No,
            ChildrenPolicy = ChildrenPolicy.Yes,
            CheckInFrom = new TimeOnly(15, 0),
            CheckOutBy = new TimeOnly(10, 30),
        });

        Assert.True(none.IsEmpty);
        Assert.False(declared.PetsAllowed);
        Assert.False(declared.SmokersAllowed);
        Assert.True(declared.ChildrenAllowed);
        Assert.Equal(15, declared.CheckinTimeStart);
        Assert.Null(declared.CheckinTimeEnd);
        Assert.Equal(10, declared.CheckoutTime);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MapperRules_TheChildrenLabelOfThePublicPage_IsNeverTheRuleOfAChannel(bool welcome)
    {
        // ChildrenWelcome is the label of the public page, its own answer: a host who never declared the rule about children has declared none, and
        // «not suited to children» on the page does not become «children not allowed» on Booking.com or Airbnb.
        var rules = OctorateContentMapper.ReservationRules(new Property { ChildrenWelcome = welcome });

        Assert.Null(rules.ChildrenAllowed);
        Assert.True(rules.IsEmpty);
        Assert.False(OctorateContentMapper.ReservationRules(new Property { ChildrenWelcome = welcome, ChildrenPolicy = ChildrenPolicy.No }).ChildrenAllowed);
        Assert.True(OctorateContentMapper.ReservationRules(new Property { ChildrenWelcome = welcome, ChildrenPolicy = ChildrenPolicy.Yes }).ChildrenAllowed);
    }

    [Fact]
    public void MapperDescriptions_SendEveryLanguageTheHostWrote_AndNeverTheCin()
    {
        var property = new Property { ListingTitle = "Casa sul lago", Description = "Luminosa.", HouseRules = "Niente feste.", CinCode = "IT058091C27G5FFZDZ" };
        var document = new SitePropertyProfileDocument
        {
            Translations = new Dictionary<string, ListingTexts> { ["en"] = new ListingTexts(Description: "Bright.", Rules: "No parties."), ["fr"] = new ListingTexts(Description: "Lumineuse.") },
        };

        var descriptions = OctorateContentMapper.Descriptions(property, document);
        var json = OctorateContentRequests.DescriptionsBody(descriptions);

        Assert.Equal("Luminosa.", descriptions.Description["IT"]);
        Assert.Equal("Bright.", descriptions.Description["EN"]);
        Assert.Equal("Lumineuse.", descriptions.Description["FR"]);
        Assert.Equal("Niente feste.", descriptions.HouseRules["IT"]);
        Assert.Equal("No parties.", descriptions.HouseRules["EN"]);
        Assert.DoesNotContain("IT058091C27G5FFZDZ", json, StringComparison.Ordinal);
        Assert.DoesNotContain("addText", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Baseline_SurvivesARoundTrip_AndNeverFailsOnAColumnThatCannotBeRead()
    {
        var baseline = new ListingChannelBaseline(
            new Dictionary<string, string> { [ListingContentGroups.Rooms] = "bb", [ListingContentGroups.Descriptions] = "aa" },
            "proc-1",
            [ListingContentGroups.Descriptions]);

        var read = ListingChannelBaseline.Parse(baseline.ToJson());

        Assert.Equal("proc-1", read.ProcessId);
        Assert.Equal(["descriptions", "rooms"], read.Hashes.Keys.Order());
        Assert.Equal([ListingContentGroups.Descriptions], read.ChangedGroups);
        Assert.Equal([ListingContentGroups.Rooms], read.Differences(new Dictionary<string, string> { ["descriptions"] = "aa", ["rooms"] = "cc", ["property"] = "x" }));
        Assert.Empty(ListingChannelBaseline.Parse("not json").Hashes);
        Assert.Null(ListingChannelBaseline.Empty.ToJson());
    }
}
