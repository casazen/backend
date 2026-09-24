using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

/// <summary>
/// CO-12 (A5-02): rules of the Alloggiati record for the guests of a stay, from the verified (U) facts of
/// <c>.claude/context/regulations/alloggiati.md</c>: five kinds of guest, heads followed by their members, the
/// document only for single guests and heads, sex male/female, comune and province only for those born in Italy.
/// </summary>
public class AlloggiatiRecordRulesTests
{
    [Theory]
    [InlineData(StayGuestType.SingleGuest, true)]
    [InlineData(StayGuestType.HeadOfFamily, true)]
    [InlineData(StayGuestType.HeadOfGroup, true)]
    [InlineData(StayGuestType.FamilyMember, false)]
    [InlineData(StayGuestType.GroupMember, false)]
    public void RequiresDocument_Kind_OnlySingleGuestAndHeads(StayGuestType type, bool expected)
    {
        Assert.Equal(expected, AlloggiatiRecordRules.RequiresDocument(type));
    }

    [Fact]
    public void CompositionErrors_FamilyThenGroupThenSingle_IsValid()
    {
        var types = new[]
        {
            StayGuestType.HeadOfFamily, StayGuestType.FamilyMember, StayGuestType.FamilyMember,
            StayGuestType.HeadOfGroup, StayGuestType.GroupMember,
            StayGuestType.SingleGuest,
        };

        Assert.Empty(AlloggiatiRecordRules.CompositionErrors(types));
    }

    [Fact]
    public void CompositionErrors_MemberFirstOrOfTheWrongHead_AreReportedOnTheMember()
    {
        var types = new[]
        {
            StayGuestType.FamilyMember,
            StayGuestType.HeadOfGroup, StayGuestType.GroupMember, StayGuestType.FamilyMember,
        };

        var errors = AlloggiatiRecordRules.CompositionErrors(types);

        Assert.Equal(
            new[] { (0, StayGuestCompositionErrorKind.MemberWithoutHead), (3, StayGuestCompositionErrorKind.MemberWithoutHead) },
            errors.Select(e => (e.Index, e.Kind)));
    }

    [Fact]
    public void CompositionErrors_HeadWithoutMembersOrMemberAfterSingle_AreReported()
    {
        var types = new[] { StayGuestType.HeadOfFamily, StayGuestType.SingleGuest, StayGuestType.GroupMember };

        var errors = AlloggiatiRecordRules.CompositionErrors(types);

        Assert.Equal(
            new[] { (0, StayGuestCompositionErrorKind.HeadWithoutMembers), (2, StayGuestCompositionErrorKind.MemberWithoutHead) },
            errors.Select(e => (e.Index, e.Kind)));
    }

    [Fact]
    public void MissingFields_CompleteHeadBornInItaly_IsEmpty()
    {
        Assert.Empty(AlloggiatiRecordRules.MissingFields(CompleteHead()));
    }

    [Fact]
    public void MissingFields_FamilyMemberWithoutDocument_IsEmpty()
    {
        var member = CompleteHead();
        member.Type = StayGuestType.FamilyMember;
        member.DocumentType = null;
        member.DocumentNumber = string.Empty;
        member.DocumentIssuePlaceName = string.Empty;

        Assert.Empty(AlloggiatiRecordRules.MissingFields(member));
    }

    [Fact]
    public void MissingFields_HeadWithoutDocumentAndOtherValues_ListsTheFieldsTheRecordRejects()
    {
        var head = CompleteHead();
        head.Gender = Gender.Other;
        head.DocumentType = GuestDocumentType.Other;
        head.DocumentNumber = "AB-123";
        head.DocumentIssuePlaceName = string.Empty;
        head.BirthProvince = "Roma";

        Assert.Equal(
            new[] { "gender", "birthProvince", "documentType", "documentNumber", "documentIssuePlace" },
            AlloggiatiRecordRules.MissingFields(head));
    }

    [Fact]
    public void MissingFields_BornAbroad_NeedsTheStateNotTheComune()
    {
        var guest = CompleteHead();
        guest.BornInItaly = false;
        guest.BirthComuneName = string.Empty;
        guest.BirthProvince = null;

        Assert.Equal(new[] { "birthCountry" }, AlloggiatiRecordRules.MissingFields(guest));

        guest.BirthCountryName = "Francia";
        Assert.Empty(AlloggiatiRecordRules.MissingFields(guest));
    }

    [Fact]
    public void MissingFields_NamesLongerThanTheRecord_AreReported()
    {
        var guest = CompleteHead();
        guest.FirstName = new string('A', AlloggiatiRecordRules.MaxFirstNameLength + 1);
        guest.LastName = new string('B', AlloggiatiRecordRules.MaxLastNameLength + 1);

        Assert.Equal(new[] { "lastName", "firstName" }, AlloggiatiRecordRules.MissingFields(guest));
    }

    [Fact]
    public void IsDataComplete_HeadWithoutMembers_IsFalse()
    {
        var head = CompleteHead();
        head.Type = StayGuestType.HeadOfFamily;

        Assert.False(AlloggiatiRecordRules.IsDataComplete([head]));
        head.Type = StayGuestType.SingleGuest;
        Assert.True(AlloggiatiRecordRules.IsDataComplete([head]));
        Assert.False(AlloggiatiRecordRules.IsDataComplete([]));
    }

    [Theory]
    [InlineData("2008-10-11", true)] // turns 18 the day after the arrival
    [InlineData("2008-10-10", false)] // turns 18 on the arrival day
    [InlineData("1980-01-01", false)]
    public void IsMinor_DateOfBirth_ComparedWithTheArrivalDate(string dateOfBirth, bool expected)
    {
        var birth = DateTime.SpecifyKind(DateTime.Parse(dateOfBirth), DateTimeKind.Utc);

        Assert.Equal(expected, AlloggiatiRecordRules.IsMinor(birth, new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData(AlloggiatiCodeTable.Comuni, "123456789", true)]
    [InlineData(AlloggiatiCodeTable.Comuni, "1234567890", false)]
    [InlineData(AlloggiatiCodeTable.Stati, "12345 ", false)]
    [InlineData(AlloggiatiCodeTable.Documenti, "ABC12", true)]
    [InlineData(AlloggiatiCodeTable.Documenti, "ABC123", false)]
    [InlineData(AlloggiatiCodeTable.TipiAlloggiato, "99", true)]
    [InlineData(AlloggiatiCodeTable.TipiAlloggiato, "999", false)]
    [InlineData(AlloggiatiCodeTable.Comuni, "abc", false)]
    public void IsValidCodeShape_Code_ChecksTheLengthOfTheRecordField(AlloggiatiCodeTable table, string code, bool expected)
    {
        Assert.Equal(expected, AlloggiatiRecordRules.IsValidCodeShape(table, code));
    }

    [Theory]
    [InlineData("Reggio nell'Emilia", "REGGIONELLEMILIA")]
    [InlineData("  Forlì-Cesena ", "FORLICESENA")]
    [InlineData("Capo Famiglia", "CAPOFAMIGLIA")]
    [InlineData(null, "")]
    public void NormalizeDescription_Text_UppercaseWithoutAccentsAndPunctuation(string? text, string expected)
    {
        Assert.Equal(expected, AlloggiatiRecordRules.NormalizeDescription(text));
    }

    [Theory]
    [InlineData(" ca 12345 ab ", "CA12345AB", true)]
    [InlineData("AB-123", "AB-123", false)]
    [InlineData("123456789012345678901", "123456789012345678901", false)]
    public void NormalizeDocumentNumber_Value_RemovesSpacesAndValidatesTheRecordShape(string raw, string normalized, bool valid)
    {
        var value = AlloggiatiRecordRules.NormalizeDocumentNumber(raw);

        Assert.Equal(normalized, value);
        Assert.Equal(valid, AlloggiatiRecordRules.IsValidDocumentNumber(value));
    }

    private static StayGuest CompleteHead() => new()
    {
        Type = StayGuestType.SingleGuest,
        FirstName = "Mario",
        LastName = "Rossi",
        Gender = Gender.Male,
        DateOfBirth = new DateTime(1980, 4, 2, 0, 0, 0, DateTimeKind.Utc),
        BornInItaly = true,
        BirthComuneName = "Milano",
        BirthProvince = "MI",
        CitizenshipName = "Italia",
        DocumentType = GuestDocumentType.IdentityCard,
        DocumentNumber = "CA12345AB",
        DocumentIssuePlaceName = "Milano",
    };
}
