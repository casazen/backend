using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>E03/F04/US02/T02: the MRZ against the check-in data, one flag per field, after normalization.</summary>
public class MrzCheckInConsistencyTests
{
    private static MrzExtractionResult Mrz(string surname, string givenNames, string? number = "CA12345AB", string? nationality = "ITA") => new()
    {
        Surname = surname,
        GivenNames = givenNames,
        DocumentNumber = number,
        DateOfBirth = new DateTime(1990, 4, 12),
        Nationality = nationality,
        CheckDigitsValid = true,
    };

    [Theory]
    [InlineData("ROSSI", "MARIA", "Maria", "Rossi")]
    [InlineData("ROSSI", "MARIA", "Rossi", "Maria")]
    [InlineData("DE LUCA", "NICOLO", "Nicolò", "De Luca")]
    [InlineData("D ANGELO", "ANNA MARIA", "Anna", "D'Angelo")]
    [InlineData("MUELLER", "JUERGEN", "Jürgen", "Müller")]
    [InlineData("ROSSI", "MARIA GIUSEPPINA", "Maria", "Rossi")]
    [InlineData("BIANCHI VERDI", "ALESSANDRO", "Alessandro", "Bianchi Verdi")]
    [InlineData("ROSSI", "MARIA GIUSEPP", "Maria Giuseppina", "Rossi")]
    public void Compare_TheSamePersonWrittenDifferently_NamesAgree(string surname, string givenNames, string firstName, string lastName)
    {
        var result = MrzCheckInConsistency.Compare(Mrz(surname, givenNames), new CheckInIdentity(firstName, lastName, null, null, null));

        Assert.True(result.Names);
    }

    [Theory]
    [InlineData("ROSSI", "MARIA", "Mario", "Rossi")]
    [InlineData("ROSSI", "MARIA", "Paolo", "Rossi")]
    [InlineData("ROSSI", "MARIA", "", "Rossi")]
    [InlineData("ROSSI", "MARIA", "", "")]
    [InlineData("", "", "Maria", "Rossi")]
    public void Compare_AnotherPersonOrASurnameAlone_NamesDoNotAgree(string surname, string givenNames, string firstName, string lastName)
    {
        var result = MrzCheckInConsistency.Compare(Mrz(surname, givenNames), new CheckInIdentity(firstName, lastName, null, null, null));

        Assert.False(result.Names);
    }

    [Fact]
    public void Compare_TheOtherFields_AreComparedOnlyWhenTheCheckInHasThem()
    {
        var notDeclared = MrzCheckInConsistency.Compare(Mrz("ROSSI", "MARIA"), new CheckInIdentity("Maria", "Rossi", null, null, "Italia"));
        Assert.Null(notDeclared.BirthDate);
        Assert.Null(notDeclared.DocumentNumber);
        // A citizenship written as a name is never guessed into a code.
        Assert.Null(notDeclared.Nationality);

        var declared = MrzCheckInConsistency.Compare(
            Mrz("ROSSI", "MARIA"),
            new CheckInIdentity("Maria", "Rossi", new DateOnly(1990, 4, 12), "ca 12345-ab", "ita"));
        Assert.True(declared.BirthDate);
        Assert.True(declared.DocumentNumber);
        Assert.True(declared.Nationality);

        var different = MrzCheckInConsistency.Compare(
            Mrz("ROSSI", "MARIA"),
            new CheckInIdentity("Maria", "Rossi", new DateOnly(1991, 4, 12), "CA99999ZZ", "FRA"));
        Assert.False(different.BirthDate);
        Assert.False(different.DocumentNumber);
        Assert.False(different.Nationality);
    }
}
