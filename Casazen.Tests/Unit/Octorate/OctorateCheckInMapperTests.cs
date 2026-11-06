using Casazen.Core.Entities;
using Casazen.Core.Octorate;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Octorate;
using Xunit;

namespace Casazen.Tests.Unit.Octorate;

/// <summary>
/// The check-in of a stay as Octorate receives it (E03/F01/US01/T03, NF-41, NF-46): the kinds of guest, the tourist tax values that may travel and
/// the hash of a payload. Pure: no database, no call.
/// </summary>
public class OctorateCheckInMapperTests
{
    private static readonly DateOnly CheckIn = new(2026, 11, 20);
    private static readonly DateOnly CheckOut = new(2026, 11, 23);

    [Fact]
    public void AccommodatedTypes_EveryKindOfGuest_HasAnOctorateValue()
    {
        // A new StayGuestType without its Octorate value fails here, before a guest of that kind is refused by Octorate.
        Assert.All(Enum.GetValues<StayGuestType>(), type => Assert.False(string.IsNullOrWhiteSpace(OctorateCheckInMapper.AccommodatedTypeOf(type))));
        Assert.Equal("SINGLE_GUEST", OctorateCheckInMapper.AccommodatedTypeOf(StayGuestType.SingleGuest));
    }

    [Fact]
    public void Map_MemberOfAFamily_SendsNoDocument_AndTheHashFollowsTheDraft()
    {
        var head = Guest(StayGuestType.HeadOfFamily, position: 0);
        var member = Guest(StayGuestType.FamilyMember, position: 1);

        var headUpsert = OctorateCheckInMapper.Map(head, CheckIn, CheckOut, draft: false, octorateGuestId: null, tax: null);
        var memberUpsert = OctorateCheckInMapper.Map(member, CheckIn, CheckOut, draft: false, octorateGuestId: null, tax: null);

        Assert.Equal("IDENT", headUpsert.DocumentType);
        Assert.Equal("AB1234567", headUpsert.DocumentCode);
        Assert.Equal(OctorateCheckInMapper.ItalyCountry, headUpsert.ResidenceCountry);
        Assert.Equal("Como", headUpsert.ResidenceCity);
        Assert.Null(memberUpsert.DocumentType);
        Assert.Null(memberUpsert.DocumentCode);
        Assert.Null(memberUpsert.DocumentExpire);

        // The draft is part of the content; the Octorate id and the position are not.
        var asDraft = OctorateCheckInMapper.Map(head, CheckIn, CheckOut, draft: true, octorateGuestId: null, tax: null);
        var known = OctorateCheckInMapper.Map(head, CheckIn, CheckOut, draft: false, octorateGuestId: "88", tax: null);
        Assert.NotEqual(OctorateCheckInMapper.Hash(headUpsert), OctorateCheckInMapper.Hash(asDraft));
        Assert.Equal(OctorateCheckInMapper.Hash(headUpsert), OctorateCheckInMapper.Hash(known));
    }

    [Fact]
    public void OctorateCheckIn_TaxValuesOnlyWhenEnabledAndConfirmed_Test()
    {
        var codes = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [TouristTaxExemptionReason.ResidentCode] = "RESIDENT",
            [TouristTaxExemptionReason.OtherCode] = null,
        };

        // Off, not confirmed, stale, or no record: no position has values, and nothing about the tax is in the payload.
        Assert.Empty(OctorateCheckInMapper.TaxValues(false, Record(confirmed: true), stale: false, codes));
        Assert.Empty(OctorateCheckInMapper.TaxValues(true, Record(confirmed: false), stale: false, codes));
        Assert.Empty(OctorateCheckInMapper.TaxValues(true, Record(confirmed: true), stale: true, codes));
        Assert.Empty(OctorateCheckInMapper.TaxValues(true, null, stale: false, codes));

        var withoutTax = OctorateCheckInMapper.Map(Guest(StayGuestType.SingleGuest, 0), CheckIn, CheckOut, false, null, tax: null);
        Assert.Null(withoutTax.CityTaxPrice);
        Assert.Null(withoutTax.CityTaxExemption);
        Assert.Null(withoutTax.ExcludeCityTax);
        var json = OctorateApiClient.CheckInGuestJson(withoutTax, null);
        Assert.DoesNotContain("cityTaxPrice", json, StringComparison.Ordinal);
        Assert.DoesNotContain("cityTaxExemption", json, StringComparison.Ordinal);
        Assert.DoesNotContain("excludeCityTax", json, StringComparison.Ordinal);

        // On, confirmed and current: the amount of each guest; the Octorate reason only for a reason that has one.
        var values = OctorateCheckInMapper.TaxValues(true, Record(confirmed: true), stale: false, codes);
        Assert.Equal(new OctorateCityTaxValues(6m, null, false), values[0]);
        Assert.Equal(new OctorateCityTaxValues(0m, "RESIDENT", true), values[1]);
        Assert.Equal(new OctorateCityTaxValues(0m, null, true), values[2]);
        Assert.Equal(new OctorateCityTaxValues(0m, null, true), values[3]);

        // A stay the channel collected is excluded for every guest.
        var channel = Record(confirmed: true);
        channel.Collector = TouristTaxCollector.Channel;
        Assert.All(OctorateCheckInMapper.TaxValues(true, channel, stale: false, codes).Values, v => Assert.True(v.Exclude));
    }

    /// <summary>A confirmed or not record of four guests: one paying, one exempt for a reason with an Octorate code, one without, one minor.</summary>
    private static StayTouristTax Record(bool confirmed) => new()
    {
        Status = TouristTaxQuoteStatus.Calculated,
        Total = 6m,
        AmountDue = 6m,
        Collector = TouristTaxCollector.Host,
        CalculationHash = confirmed ? new string('a', StayTouristTax.CalculationHashLength) : null,
        ConfirmedAt = confirmed ? new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc) : null,
        Guests =
        [
            new StayTouristTaxGuest { Position = 0, ExemptionKind = TouristTaxExemptionKind.None, TaxableNights = 3, Amount = 6m },
            new StayTouristTaxGuest
            {
                Position = 1, ExemptionKind = TouristTaxExemptionKind.Host, ExemptionCode = TouristTaxExemptionReason.ResidentCode, Amount = 0m,
            },
            new StayTouristTaxGuest
            {
                Position = 2, ExemptionKind = TouristTaxExemptionKind.Host, ExemptionCode = TouristTaxExemptionReason.OtherCode, Amount = 0m,
            },
            new StayTouristTaxGuest
            {
                Position = 3, ExemptionKind = TouristTaxExemptionKind.Age, ExemptionCode = TouristTaxExemptionReason.MinorAgeCode, Amount = 0m,
            },
        ],
    };

    private static StayGuest Guest(StayGuestType type, int position) => new()
    {
        Position = position,
        Type = type,
        FirstName = "Ospite",
        LastName = "Di Prova",
        Gender = Gender.Female,
        DateOfBirth = new DateTime(1990, 5, 4, 0, 0, 0, DateTimeKind.Utc),
        BornInItaly = true,
        BirthComuneName = "Como",
        CitizenshipCode = OctorateCheckInMapper.ItalyStateCode,
        CitizenshipName = "ITALIA",
        ResidenceInItaly = true,
        ResidenceComuneName = "Como",
        DocumentType = GuestDocumentType.IdentityCard,
        DocumentNumber = "AB1234567",
        DocumentIssuePlaceName = "Como",
        DocumentIssuedOn = new DateOnly(2022, 1, 10),
        DocumentExpiresOn = new DateOnly(2032, 1, 10),
    };
}
