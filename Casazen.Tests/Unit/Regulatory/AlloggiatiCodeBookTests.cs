using Casazen.Core.Entities;
using Casazen.Core.Regulatory;
using Xunit;

namespace Casazen.Tests.Unit.Regulatory;

/// <summary>
/// CO-12: codes of a guest's record line come only from the imported official tables, by stored code or by a unique
/// match of the entered name; otherwise they stay "to complete". The entries below are SYNTHETIC test data (codes
/// starting with 9, descriptions that are not all real names), not official Alloggiati codes.
/// </summary>
public class AlloggiatiCodeBookTests
{
    private static readonly AlloggiatiCodeEntry[] SyntheticEntries =
    [
        Entry(AlloggiatiCodeTable.Comuni, "900000001", "Milano", "MI"),
        Entry(AlloggiatiCodeTable.Comuni, "900000002", "Castro", "BG"),
        Entry(AlloggiatiCodeTable.Comuni, "900000003", "Castro", "LE"),
        Entry(AlloggiatiCodeTable.Stati, "900000100", "Italia"),
        Entry(AlloggiatiCodeTable.Stati, "900000101", "Francia"),
        Entry(AlloggiatiCodeTable.Documenti, "TSTID", "Documento di prova"),
        Entry(AlloggiatiCodeTable.TipiAlloggiato, "91", "Ospite Singolo"),
        Entry(AlloggiatiCodeTable.TipiAlloggiato, "92", "Capo Famiglia"),
        Entry(AlloggiatiCodeTable.TipiAlloggiato, "94", "Familiare"),
    ];

    [Fact]
    public void ResolveGuest_NoTableImported_EveryCodeIsToComplete()
    {
        var codes = AlloggiatiCodeBook.Empty.ResolveGuest(Head());

        Assert.Equal(
            new[] { "type", "birthComune", "birthCountry", "citizenship", "documentType", "documentIssuePlace" },
            codes.CodesToComplete);
        Assert.Equal(CodeResolutionStatus.TableEmpty, codes.Type.Status);
    }

    [Fact]
    public void ResolveGuest_TablesImported_ResolvesByUniqueNameAndItalyForThoseBornInItaly()
    {
        var book = Book();
        var head = Head();
        head.DocumentTypeCode = "TSTID";

        var codes = book.ResolveGuest(head);

        Assert.Empty(codes.CodesToComplete);
        Assert.Equal("92", codes.Type.Code);
        Assert.Equal("900000001", codes.BirthComune.Code);
        Assert.Equal("900000100", codes.BirthCountry.Code); // "ITALIA" in the stati table
        Assert.Equal("900000100", codes.Citizenship.Code);
        Assert.Equal(("TSTID", "Documento di prova"), (codes.DocumentType.Code, codes.DocumentType.Description));
        Assert.Equal("900000001", codes.DocumentIssuePlace.Code);
    }

    [Fact]
    public void ResolveGuest_DocumentKindWithoutCode_DocumentTypeStaysToComplete()
    {
        var codes = Book().ResolveGuest(Head());

        Assert.Equal(new[] { "documentType" }, codes.CodesToComplete);
    }

    [Fact]
    public void ResolveGuest_FamilyMember_HasNoDocumentCodes()
    {
        var member = Head();
        member.Type = StayGuestType.FamilyMember;
        member.BornInItaly = false;
        member.BirthCountryName = "francia";

        var codes = Book().ResolveGuest(member);

        Assert.Empty(codes.CodesToComplete);
        Assert.Equal(CodeResolutionStatus.NotRequired, codes.DocumentType.Status);
        Assert.Equal(CodeResolutionStatus.NotRequired, codes.BirthComune.Status);
        Assert.Equal("900000101", codes.BirthCountry.Code);
    }

    [Fact]
    public void Resolve_HomonymousComuni_NeedsTheProvince()
    {
        var book = Book();

        Assert.Equal(CodeResolutionStatus.Ambiguous, book.Resolve([AlloggiatiCodeTable.Comuni], null, "Castro").Status);
        Assert.Equal("900000003", book.Resolve([AlloggiatiCodeTable.Comuni], null, "Castro", "LE").Code);
    }

    [Fact]
    public void Resolve_StoredCodeNotInTheTable_IsNotFoundEvenIfTheNameMatches()
    {
        var resolved = Book().Resolve([AlloggiatiCodeTable.Stati], "900009999", "Francia");

        Assert.Equal(CodeResolutionStatus.NotFound, resolved.Status);
        Assert.False(resolved.IsComplete);
    }

    [Fact]
    public void ResolveGuest_GroupKindMissingFromTheTable_TypeStaysToComplete()
    {
        var head = Head();
        head.Type = StayGuestType.HeadOfGroup;

        Assert.Contains("type", Book().ResolveGuest(head).CodesToComplete);
    }

    private static AlloggiatiCodeBook Book() =>
        new(
            SyntheticEntries.GroupBy(e => e.Table).ToDictionary(g => g.Key, g => g.Count()),
            SyntheticEntries);

    private static StayGuest Head() => new()
    {
        Type = StayGuestType.HeadOfFamily,
        FirstName = "Mario",
        LastName = "Rossi",
        Gender = Gender.Male,
        DateOfBirth = new DateTime(1980, 4, 2, 0, 0, 0, DateTimeKind.Utc),
        BornInItaly = true,
        BirthComuneName = "Milano",
        BirthProvince = "MI",
        CitizenshipName = "ITALIA",
        DocumentType = GuestDocumentType.IdentityCard,
        DocumentNumber = "CA12345AB",
        DocumentIssuePlaceName = "Milano",
    };

    private static AlloggiatiCodeEntry Entry(AlloggiatiCodeTable table, string code, string description, string? province = null) => new()
    {
        Table = table,
        Code = code,
        Description = description,
        NormalizedDescription = AlloggiatiRecordRules.NormalizeDescription(description),
        Province = province,
    };
}
