using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
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

    [Fact]
    public void AcceptedDocumentNumber_BelongsToTheVerifiedGuest()
    {
        var session = new GuestCheckInSession { TokenHash = "test-session", BiometricOutcome = BiometricOutcome.Accept };
        var proof = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(session.TokenHash), "AB12345"u8));
        var check = new DocumentCheck(true, true, false, true, true, true, true, false);
        var stored = new StoredDocumentCheck(check, "front", DateTime.UnixEpoch,
            Back: new StoredDocumentPicture(null, true, check, proof), VerifyingGuestPosition: 1);
        session.DocumentCheckJson = JsonSerializer.Serialize(stored, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var matching = new StayGuestInput { DocumentNumber = "ab12345" };
        var different = new StayGuestInput { DocumentNumber = "CD67890" };
        Assert.Null(IdentityVerificationFlowService.VerifiedNumberMismatch(session, [different, matching]));
        Assert.Equal(1, IdentityVerificationFlowService.VerifiedNumberMismatch(session, [matching, different]));

        session.BiometricOutcome = null;
        Assert.Null(IdentityVerificationFlowService.VerifiedNumberMismatch(session, [matching, different]));
    }

    /// <summary>
    /// The guest at the door keeps the session and gets a new token (<c>IGuestCheckInService.ReissueLinkAsync</c>): the proof of the verified number was made with the key of the first picture
    /// (<c>ProofKey</c>), so it still verifies, whatever token hash the session has now.
    /// </summary>
    [Fact]
    public void AcceptedDocumentNumber_StillBelongsToTheVerifiedGuest_AfterTheSessionGetsANewToken()
    {
        var session = new GuestCheckInSession { TokenHash = "first-link", BiometricOutcome = BiometricOutcome.Accept };
        var proof = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes("first-link"), "AB12345"u8));
        var check = new DocumentCheck(true, true, false, true, true, true, true, false);
        var stored = new StoredDocumentCheck(check, "front", DateTime.UnixEpoch,
            Back: new StoredDocumentPicture(null, true, check, proof), VerifyingGuestPosition: 1, ProofKey: "first-link");
        session.DocumentCheckJson = JsonSerializer.Serialize(stored, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        session.TokenHash = "link-renewed-at-the-door";

        var matching = new StayGuestInput { DocumentNumber = "ab12345" };
        var different = new StayGuestInput { DocumentNumber = "CD67890" };

        Assert.Null(IdentityVerificationFlowService.VerifiedNumberMismatch(session, [different, matching]));
        Assert.Equal(1, IdentityVerificationFlowService.VerifiedNumberMismatch(session, [matching, different]));
    }
}
