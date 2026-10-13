using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

/// <summary>
/// CO-13: the Alloggiati Web record file. Expected lines are built by hand from the examples of the official guide
/// (<c>MANUALEALBERGHI.pdf</c>, paragraph 12: arrival 16/02/2005, birth 13/03/1973, ROSSI followed by 45 spaces, PAOLO by 25,
/// document number AB123CD by 13, document type IDENT) and from the table of positions of the guide (p. 34) and of the web
/// service manual (<c>MANUALEWS.pdf</c>, p. 19). The codes of comuni and stati are opaque test values, not real table rows.
/// </summary>
public class AlloggiatiRecordFileTests
{
    private const string ComuneCode = "415063049"; // the example ComuneCodice of MANUALEWS.pdf, used here as an opaque value
    private const string ItalyCode = "100000100";
    private const string FranceCode = "100000227";

    private static readonly DateTime Arrival = new(2005, 2, 16, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Birth = new(1973, 3, 13, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Layout_FollowsTheOfficialTable_168CharactersWithNoGaps()
    {
        // DA / A of the table "Tracciato Record" (MANUALEALBERGHI.pdf p. 34, MANUALEWS.pdf p. 19).
        var official = new (string Name, int From, int To)[]
        {
            ("TipoAlloggiato", 0, 1), ("DataArrivo", 2, 11), ("GiorniPermanenza", 12, 13), ("Cognome", 14, 63),
            ("Nome", 64, 93), ("Sesso", 94, 94), ("DataNascita", 95, 104), ("ComuneNascita", 105, 113),
            ("ProvinciaNascita", 114, 115), ("StatoNascita", 116, 124), ("Cittadinanza", 125, 133),
            ("TipoDocumento", 134, 138), ("NumeroDocumento", 139, 158), ("LuogoRilascioDocumento", 159, 167),
        };

        Assert.Equal(official, AlloggiatiRecordFile.Layout.Select(f => (f.Name, f.Start, f.End)));
        Assert.Equal(AlloggiatiRecordFile.LineLength, AlloggiatiRecordFile.Layout.Sum(f => f.Length));
        Assert.Equal(168, AlloggiatiRecordFile.LineLength);
        Assert.Equal(1000, AlloggiatiRecordFile.MaxLines);
    }

    [Fact]
    public void Build_SingleGuestWithTheValuesOfTheOfficialExamples_WritesEveryFieldAtItsPosition()
    {
        var result = AlloggiatiRecordFile.Build([SingleGuest()]);

        Assert.True(result.Success);
        var expected =
            "16" + "16/02/2005" + "03"
            + "ROSSI" + new string(' ', 45)
            + "PAOLO" + new string(' ', 25)
            + "1" + "13/03/1973"
            + ComuneCode + "RM" + ItalyCode + ItalyCode
            + "IDENT" + "AB123CD" + new string(' ', 13) + ComuneCode;
        Assert.Equal(168, expected.Length);
        Assert.Equal(expected, result.Text);
        Assert.Equal(1, result.LineCount);
    }

    [Fact]
    public void Build_FamilyOfThree_HeadFirstMembersWithBlankDocumentSeparatedByCrLfAndNoneAfterTheLast()
    {
        var head = SingleGuest(position: 0, type: StayGuestType.HeadOfFamily, typeCode: "17");
        var wife = Member(position: 1, "BIANCHI", "ANNA", Gender.Female, StayGuestType.FamilyMember, "19");
        var son = Member(position: 2, "ROSSI", "LUCA", Gender.Male, StayGuestType.FamilyMember, "19");

        var result = AlloggiatiRecordFile.Build([head, wife, son]);

        Assert.True(result.Success);
        var lines = result.Text!.Split("\r\n");
        Assert.Equal(3, lines.Length);
        Assert.All(lines, line => Assert.Equal(168, line.Length));
        Assert.StartsWith("17", lines[0]);
        Assert.StartsWith("19", lines[1]);
        Assert.StartsWith("19", lines[2]);
        // Members: the three document fields (5 + 20 + 9 = 34 characters, positions 134-167) are blank.
        Assert.All(lines.Skip(1), line => Assert.Equal(new string(' ', 34), line[134..]));
        Assert.Equal("IDENT", lines[0][134..139]);
        Assert.Equal("BIANCHI", lines[1][14..63].TrimEnd());
        Assert.Equal("2", lines[1][94..95]); // woman
        Assert.Equal(3, result.LineCount);
        Assert.DoesNotContain('\n', result.Text.Replace("\r\n", string.Empty));
        Assert.False(result.Text.EndsWith('\n') || result.Text.EndsWith('\r'));
    }

    [Fact]
    public void Build_GuestBornAbroad_LeavesComuneAndProvinceBlankAndWritesTheStateOfBirth()
    {
        var guest = SingleGuest() with
        {
            BornInItaly = false,
            BirthComune = string.Empty,
            BirthProvince = null,
            BirthCountry = "Francia",
            Codes = SingleGuest().Codes with { BirthComune = null, BirthCountry = FranceCode },
        };

        var line = AlloggiatiRecordFile.Build([guest]).Text!;

        Assert.Equal(168, line.Length);
        Assert.Equal(new string(' ', 9), line[105..114]); // comune: "completato con 9 spazi bianchi"
        Assert.Equal("  ", line[114..116]); // province: 2 blanks
        Assert.Equal(FranceCode, line[116..125]);
        Assert.Equal(ItalyCode, line[125..134]); // citizenship stays
    }

    [Fact]
    public void Build_GuestBornInItaly_WritesTheCodeOfItalyAsStateOfBirth()
    {
        var line = AlloggiatiRecordFile.Build([SingleGuest()]).Text!;

        Assert.Equal(ComuneCode, line[105..114]);
        Assert.Equal("RM", line[114..116]);
        Assert.Equal(ItalyCode, line[116..125]);
    }

    [Theory]
    [InlineData(1, "01")]
    [InlineData(9, "09")]
    [InlineData(30, "30")]
    public void Build_StayDays_AreWrittenWithTwoDigits(int days, string expected)
    {
        var line = AlloggiatiRecordFile.Build([SingleGuest() with { StayDays = days }]).Text!;

        Assert.Equal(expected, line[12..14]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(-2)]
    public void Build_StayOutsideOneToThirtyDays_IsRefusedWithoutAFile(int days)
    {
        var result = AlloggiatiRecordFile.Build([SingleGuest() with { StayDays = days }]);

        Assert.False(result.Success);
        Assert.Null(result.Text);
        var issue = Assert.Single(result.Issues);
        Assert.Equal(AlloggiatiRecordFileIssueKind.StayDaysOutOfRange, issue.Kind);
    }

    [Fact]
    public void Build_DocumentNumberWithSpacesAndLowercase_IsNormalizedToThe20CharacterField()
    {
        var line = AlloggiatiRecordFile.Build([SingleGuest() with { DocumentNumber = "ab 123 cd" }]).Text!;

        Assert.Equal("AB123CD".PadRight(20), line[139..159]);
    }

    [Fact]
    public void Build_CodeToComplete_IsRefusedAndNothingIsGuessed()
    {
        var guest = SingleGuest() with
        {
            Codes = SingleGuest().Codes with { BirthComune = null },
            CodesToComplete = ["birthComune"],
        };

        var result = AlloggiatiRecordFile.Build([guest]);

        Assert.False(result.Success);
        Assert.Null(result.Text);
        var issue = Assert.Single(result.Issues);
        Assert.Equal((0, AlloggiatiRecordFileIssueKind.CodeToComplete, "birthComune"), (issue.Position, issue.Kind, issue.Field));
    }

    [Fact]
    public void Build_MissingFieldOrBrokenOrder_IsRefusedWithThePositionOfTheGuest()
    {
        var complete = SingleGuest(position: 0);
        var incomplete = SingleGuest(position: 1) with { MissingFields = ["dateOfBirth"] };
        var orphan = Member(position: 2, "ROSSI", "LUCA", Gender.Male, StayGuestType.FamilyMember, "19") with
        {
            CompositionIssue = "member_without_head",
        };

        var result = AlloggiatiRecordFile.Build([complete, incomplete, orphan]);

        Assert.Null(result.Text);
        Assert.Equal(
            new[] { (1, AlloggiatiRecordFileIssueKind.DataIncomplete), (2, AlloggiatiRecordFileIssueKind.DataIncomplete) },
            result.Issues.Select(i => (i.Position, i.Kind)));
    }

    [Fact]
    public void Build_NoGuests_IsRefused()
    {
        var result = AlloggiatiRecordFile.Build([]);

        Assert.Null(result.Text);
        Assert.Equal(AlloggiatiRecordFileIssueKind.NoGuests, Assert.Single(result.Issues).Kind);
    }

    [Fact]
    public void Build_MoreThan1000Guests_IsRefused()
    {
        var guests = Enumerable.Range(0, 1001).Select(i => SingleGuest(position: i)).ToList();

        var result = AlloggiatiRecordFile.Build(guests);

        Assert.Null(result.Text);
        Assert.Contains(result.Issues, i => i.Kind == AlloggiatiRecordFileIssueKind.TooManyGuests);
    }

    [Fact]
    public void Build_Exactly1000Guests_IsAccepted()
    {
        var guests = Enumerable.Range(0, 1000).Select(i => SingleGuest(position: i)).ToList();

        var result = AlloggiatiRecordFile.Build(guests);

        Assert.True(result.Success);
        Assert.Equal(1000, result.LineCount);
        Assert.Equal(1000 * 168 + 999 * 2, result.Text!.Length);
    }

    [Theory]
    [InlineData("abc")] // lowercase
    [InlineData("1234567890")] // longer than the 9 characters of the field
    [InlineData("12 34")]
    public void Build_CodeThatDoesNotFitItsField_IsRefusedInsteadOfBeingCutOrShifted(string code)
    {
        var guest = SingleGuest() with { Codes = SingleGuest().Codes with { Citizenship = code } };

        var result = AlloggiatiRecordFile.Build([guest]);

        Assert.Null(result.Text);
        var issue = Assert.Single(result.Issues);
        Assert.Equal((AlloggiatiRecordFileIssueKind.DataIncomplete, "citizenship"), (issue.Kind, issue.Field));
    }

    [Fact]
    public void Build_NamesWithAccentsAndApostrophes_AreWrittenInCapitalLettersWithoutAccents()
    {
        var guest = SingleGuest() with { LastName = "D'Angelo-Müller", FirstName = "José Ñandú" };

        var line = AlloggiatiRecordFile.Build([guest]).Text!;

        Assert.Equal("D'ANGELO MULLER".PadRight(50), line[14..64]);
        Assert.Equal("JOSE NANDU".PadRight(30), line[64..94]);
        Assert.Equal(168, line.Length);
    }

    [Theory]
    [InlineData("李明")] // another alphabet
    [InlineData("Иван")]
    [InlineData("R2D2")] // digits
    [InlineData("Rossi@")] // symbol
    [InlineData("  ")] // blank
    public void Build_NameThatCannotBeWrittenWithLatinLetters_IsRefusedInsteadOfBeingCorrupted(string lastName)
    {
        var result = AlloggiatiRecordFile.Build([SingleGuest() with { LastName = lastName }]);

        Assert.Null(result.Text);
        var issue = Assert.Single(result.Issues);
        Assert.Equal((AlloggiatiRecordFileIssueKind.NameNotRepresentable, "lastName"), (issue.Kind, issue.Field));
    }

    [Fact]
    public void Build_NameLongerThanTheFieldAfterTransliteration_IsRefused()
    {
        // 26 times "ß" is 26 characters, but 52 once written "SS": it no longer fits the 50 characters of the surname.
        var result = AlloggiatiRecordFile.Build([SingleGuest() with { LastName = new string('ß', 26) }]);

        Assert.Null(result.Text);
        Assert.Equal(AlloggiatiRecordFileIssueKind.NameNotRepresentable, Assert.Single(result.Issues).Kind);
    }

    [Theory]
    [InlineData("Rossi", 50, "ROSSI")]
    [InlineData("  de   la  Cruz ", 50, "DE LA CRUZ")]
    [InlineData("O’Brien", 50, "O'BRIEN")]
    [InlineData("Straße", 50, "STRASSE")]
    [InlineData("Łukasz", 30, "LUKASZ")]
    [InlineData("Jr.", 30, "JR")]
    [InlineData("Čerňák", 30, "CERNAK")]
    public void ToRecordName_Latin_IsReducedToCapitalLettersSpacesAndApostrophes(string value, int max, string expected)
    {
        Assert.Equal(expected, AlloggiatiRecordFile.ToRecordName(value, max));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("---")]
    [InlineData("Σοφία")]
    public void ToRecordName_NothingToWrite_IsNull(string? value)
    {
        Assert.Null(AlloggiatiRecordFile.ToRecordName(value, 50));
    }

    [Fact]
    public void ToBytes_IsUtf8WithoutByteOrderMarkAndPureAscii()
    {
        var guests = new[]
        {
            SingleGuest(position: 0, type: StayGuestType.HeadOfFamily, typeCode: "17"),
            Member(position: 1, "Ünal", "Zoë", Gender.Female, StayGuestType.FamilyMember, "19"),
        };

        var bytes = AlloggiatiRecordFile.Build(guests).ToBytes();

        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
        Assert.All(bytes, b => Assert.True(b < 0x80, "multi-byte characters would make bytes and characters differ"));
        Assert.Equal(168 * 2 + 2, bytes.Length);
        Assert.Equal(AlloggiatiRecordFile.Build(guests).Text, Encoding.UTF8.GetString(bytes));
    }

    private static AlloggiatiGuestRow SingleGuest(
        int position = 0,
        StayGuestType type = StayGuestType.SingleGuest,
        string typeCode = "16") =>
        new(
            StayGuestId: Guid.NewGuid(),
            Position: position,
            Type: type,
            IsMinor: false,
            ArrivalDate: Arrival,
            StayDays: 3,
            LastName: "Rossi",
            FirstName: "Paolo",
            Gender: Gender.Male,
            DateOfBirth: Birth,
            BornInItaly: true,
            BirthComune: "Roma",
            BirthProvince: "RM",
            BirthCountry: string.Empty,
            Citizenship: "Italia",
            RequiresDocument: true,
            DocumentType: GuestDocumentType.IdentityCard,
            DocumentNumber: "AB123CD",
            DocumentIssuePlace: "Roma",
            Codes: new AlloggiatiRowCodes(typeCode, ComuneCode, ItalyCode, ItalyCode, "IDENT", "Carta d'identità", ComuneCode),
            MissingFields: [],
            CodesToComplete: [],
            CompositionIssue: null);

    private static AlloggiatiGuestRow Member(
        int position,
        string lastName,
        string firstName,
        Gender gender,
        StayGuestType type,
        string typeCode) =>
        SingleGuest(position, type, typeCode) with
        {
            LastName = lastName,
            FirstName = firstName,
            Gender = gender,
            RequiresDocument = false,
            DocumentType = null,
            DocumentNumber = string.Empty,
            DocumentIssuePlace = string.Empty,
            Codes = new AlloggiatiRowCodes(typeCode, ComuneCode, ItalyCode, ItalyCode, null, null, null),
        };
}
