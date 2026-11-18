using Casazen.Core.Entities;
using Casazen.Core.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class CheckInGuestMatchTests
{
    private static readonly StayGuest Guest = new() { Position = 1, FirstName = "Luca", LastName = "D'Angelo" };
    private static CheckInDocumentFields Read(string first, string last) => new(first, last, null, null, null, null, null, null, null, null, null);

    [Theory]
    [InlineData("Lùca", "D ANGELO", false)]
    [InlineData("EUCA", "DANGELO", true)]
    public void UniqueName_SelectsBookedPosition(string first, string last, bool confirmation) =>
        Assert.Equal((1, confirmation), CheckInGuestMatch.Find([Guest], Read(first, last)));

    [Fact]
    public void UnknownAndAmbiguousNamesAreNeverAssigned()
    {
        Assert.Null(CheckInGuestMatch.Find([Guest], Read("Luca", "Rossi")));
        Assert.Null(CheckInGuestMatch.Find([Guest, Guest], Read("Luca", "Dangelo")));
    }
}
