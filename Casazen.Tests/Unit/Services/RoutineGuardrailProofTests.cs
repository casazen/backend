using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Models;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-26, E01/F04/US01/T01: the guardrails of ADR-022-R11 (an access code only after the identity is verified, no contact with an Airbnb guest before the
/// booking is confirmed, a campaign only with consent) are non-bypassable. They read the booking of the event, in the org of the routine, and never the
/// payload: a payload that says the booking is confirmed, the identity verified or the consent given changes nothing, and with no booking to read
/// nothing passes. A blocked action is recorded with the stable code of its guardrail.
/// </summary>
public class RoutineGuardrailProofTests
{
    private static readonly Guid OrgA = RoutineRig.OrgA;
    private static readonly Guid OrgB = RoutineRig.OrgB;
    private static readonly DateTime Earlier = RoutineRig.Now.UtcDateTime.AddHours(-5);
    private static readonly DateTime Later = RoutineRig.Now.UtcDateTime.AddHours(-1);

    /// <summary>Runs the one action in a routine on <paramref name="payload"/> and returns what the engine recorded for it.</summary>
    private static async Task<RoutineActionResult> RunAsync(RoutineRig rig, string type, string? config, object payload)
    {
        await rig.SeedRoutineAsync(OrgA, RoutineEventType.BookingConfirmed, RoutineRig.Actions((type, config)));
        await rig.TriggerAsync(OrgA, RoutineEventType.BookingConfirmed, payload);
        var execution = Assert.Single(await rig.ExecutionsAsync());
        return Assert.Single(RoutineRig.ResultOf(execution).Actions);
    }

    private static void AssertBlocked(RoutineActionResult result, RoutineActionType type, string code)
    {
        Assert.Equal(new RoutineActionResult(0, type, RoutineActionOutcomes.Blocked, code), result);
    }

    private static void AssertNotBlocked(RoutineActionResult result)
    {
        // What follows a guardrail that lets an action through is the action: today none of these has an implementation, so it fails with its code.
        Assert.NotEqual(RoutineActionOutcomes.Blocked, result.Outcome);
    }

    // ─── Access code (AC-NF26-4) ─────────────────────────────────────────────────

    /// <summary>
    /// Proof (permission rule, AC-NF26-4): <c>SendAccessCode</c> is blocked while the booking carries no accepted verification of the identity, even when
    /// the payload of the event says <c>IdVerified</c>: the payload is not where the gate reads. Without it, guests could receive access codes without
    /// identity verification.
    /// </summary>
    [Fact]
    public async Task Routine_AccessCodeGuardrail()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);

        var result = await RunAsync(rig, "SendAccessCode", null, new { BookingId = booking.Id, IdVerified = true });

        AssertBlocked(result, RoutineActionType.SendAccessCode, RoutineCodes.AccessCodeNeedsIdVerification);
        var execution = Assert.Single(await rig.ExecutionsAsync());
        Assert.Equal(RoutineExecutionOutcomes.Success, execution.Outcome);
    }

    [Fact]
    public async Task AccessCode_AnAcceptedVerificationOfTheBooking_LetsItThrough()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedVerificationAsync(OrgA, booking, BiometricOutcome.Accept, Earlier);

        AssertNotBlocked(await RunAsync(rig, "SendAccessCode", null, new { BookingId = booking.Id }));
    }

    [Theory]
    [InlineData(BiometricOutcome.Escalate)]
    [InlineData(BiometricOutcome.Reject)]
    public async Task AccessCode_AVerificationThatIsNotAnAccept_DoesNotOpenTheGate(BiometricOutcome outcome)
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedVerificationAsync(OrgA, booking, outcome, Earlier);

        var result = await RunAsync(rig, "SendAccessCode", null, new { BookingId = booking.Id });

        AssertBlocked(result, RoutineActionType.SendAccessCode, RoutineCodes.AccessCodeNeedsIdVerification);
    }

    /// <summary>The latest verification decides, as for the code itself: an operator's rejection after an accept closes the gate again.</summary>
    [Fact]
    public async Task AccessCode_TheLatestVerificationOfTheBookingDecides()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedVerificationAsync(OrgA, booking, BiometricOutcome.Accept, Earlier);
        await rig.SeedVerificationAsync(OrgA, booking, BiometricOutcome.Reject, Later);

        var result = await RunAsync(rig, "SendAccessCode", null, new { BookingId = booking.Id });

        AssertBlocked(result, RoutineActionType.SendAccessCode, RoutineCodes.AccessCodeNeedsIdVerification);
    }

    /// <summary>A verification written for another org's booking id is not this org's: every read names the org.</summary>
    [Fact]
    public async Task AccessCode_AVerificationOfAnotherOrg_IsNotOurs()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA);
        await rig.SeedVerificationAsync(OrgB, booking, BiometricOutcome.Accept, Earlier);

        var result = await RunAsync(rig, "SendAccessCode", null, new { BookingId = booking.Id });

        AssertBlocked(result, RoutineActionType.SendAccessCode, RoutineCodes.AccessCodeNeedsIdVerification);
    }

    /// <summary>No booking in the event, an unknown booking, and a booking of another org that is verified: with nothing of this org to read, nothing opens the gate.</summary>
    [Fact]
    public async Task AccessCode_NoBookingInTheEvent_FailsClosed()
    {
        var rig = new RoutineRig();

        var result = await RunAsync(rig, "SendAccessCode", null, new { IdVerified = true });

        AssertBlocked(result, RoutineActionType.SendAccessCode, RoutineCodes.AccessCodeNeedsIdVerification);
    }

    [Fact]
    public async Task AccessCode_AnUnknownBooking_FailsClosed()
    {
        var rig = new RoutineRig();

        var result = await RunAsync(rig, "SendAccessCode", null, new { BookingId = Guid.NewGuid(), IdVerified = true });

        AssertBlocked(result, RoutineActionType.SendAccessCode, RoutineCodes.AccessCodeNeedsIdVerification);
    }

    [Fact]
    public async Task AccessCode_AVerifiedBookingOfAnotherOrg_FailsClosed()
    {
        var rig = new RoutineRig();
        var otherOrgs = await rig.SeedBookingAsync(OrgB);
        await rig.SeedVerificationAsync(OrgB, otherOrgs, BiometricOutcome.Accept, Earlier);

        var result = await RunAsync(rig, "SendAccessCode", null, new { BookingId = otherOrgs.Id });

        AssertBlocked(result, RoutineActionType.SendAccessCode, RoutineCodes.AccessCodeNeedsIdVerification);
    }

    // ─── Airbnb (AC-NF26-5) ──────────────────────────────────────────────────────

    /// <summary>
    /// Proof (permission rule, AC-NF26-5): <c>MessageToGuest</c> is blocked for an Airbnb booking that is not confirmed, whatever the payload says (a
    /// payload that carried <c>Channel</c> and <c>BookingConfirmed</c> is how the old check could be fooled, or left open by omission). Without it, the
    /// org could violate Airbnb's terms by contacting guests outside the channel before the confirmation.
    /// </summary>
    [Fact]
    public async Task Routine_AirbnbGuardrail()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA, BookingSource.Airbnb, BookingStatus.Pending);

        var result = await RunAsync(
            rig,
            "MessageToGuest",
            """{"templateId":"welcome"}""",
            new { BookingId = booking.Id, Channel = "Direct", BookingConfirmed = true });

        AssertBlocked(result, RoutineActionType.MessageToGuest, RoutineCodes.AirbnbNotConfirmed);
    }

    [Theory]
    [InlineData(BookingSource.Airbnb, BookingStatus.Confirmed)]
    [InlineData(BookingSource.Airbnb, BookingStatus.CheckedIn)]
    [InlineData(BookingSource.Airbnb, BookingStatus.CheckedOut)]
    [InlineData(BookingSource.BookingCom, BookingStatus.Pending)]
    [InlineData(BookingSource.Direct, BookingStatus.Pending)]
    public async Task Airbnb_ConfirmedBookings_AndOtherChannels_AreNotBlocked(BookingSource source, BookingStatus status)
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA, source, status);

        AssertNotBlocked(await RunAsync(rig, "MessageToGuest", null, new { BookingId = booking.Id }));
    }

    [Fact]
    public async Task Airbnb_ACancelledBooking_IsNotConfirmed()
    {
        var rig = new RoutineRig();
        var booking = await rig.SeedBookingAsync(OrgA, BookingSource.Airbnb, BookingStatus.Cancelled);

        var result = await RunAsync(rig, "MessageToGuest", null, new { BookingId = booking.Id });

        AssertBlocked(result, RoutineActionType.MessageToGuest, RoutineCodes.AirbnbNotConfirmed);
    }

    /// <summary>With no booking to read the engine cannot say the guest is not an Airbnb one that is not confirmed: it blocks (no booking, no pass).</summary>
    [Fact]
    public async Task Airbnb_NoBookingToRead_FailsClosed()
    {
        var rig = new RoutineRig();
        await rig.SeedBookingAsync(OrgB, BookingSource.Direct);

        var result = await RunAsync(rig, "MessageToGuest", null, new { Channel = "Direct", BookingConfirmed = true });

        AssertBlocked(result, RoutineActionType.MessageToGuest, RoutineCodes.AirbnbNotConfirmed);
    }

    [Fact]
    public async Task Airbnb_ABookingOfAnotherOrg_IsNotRead()
    {
        var rig = new RoutineRig();
        var otherOrgs = await rig.SeedBookingAsync(OrgB, BookingSource.Direct);

        var result = await RunAsync(rig, "MessageToGuest", null, new { BookingId = otherOrgs.Id });

        AssertBlocked(result, RoutineActionType.MessageToGuest, RoutineCodes.AirbnbNotConfirmed);
    }

    // ─── Campaigns (AC-NF26-6) ───────────────────────────────────────────────────

    /// <summary>
    /// Proof (privacy rule, AC-NF26-6): a <c>MessageToGuest</c> configured as a campaign is blocked while the guest of the booking has not consented to
    /// marketing, whatever the payload says (<c>MarketingConsent</c> in it is not read). Without it, the org would send marketing e-mails to guests
    /// who never opted in (GDPR art. 6).
    /// </summary>
    [Fact]
    public async Task Routine_MarketingConsentGuardrail()
    {
        var rig = new RoutineRig();
        var guest = await rig.SeedGuestAsync(OrgA, marketingConsent: false);
        var booking = await rig.SeedBookingAsync(OrgA, guestId: guest.Id);

        var result = await RunAsync(
            rig,
            "MessageToGuest",
            """{"templateId":"promo-offer","isCampaign":true}""",
            new { BookingId = booking.Id, MarketingConsent = true });

        AssertBlocked(result, RoutineActionType.MessageToGuest, RoutineCodes.CampaignNeedsConsent);
    }

    /// <summary>
    /// Proof (privacy rule, AC-NF26-6): the marker of a campaign is read closed in the doubt. Any value that is present and is not <c>false</c> (a text, a
    /// number, null, an object) makes the message a campaign, so it never goes to a guest who did not consent because the marker was not spelled as a JSON
    /// <c>true</c>: the API saves any configuration for this action, so the engine cannot rely on its spelling.
    /// </summary>
    [Theory]
    [InlineData("true")]
    [InlineData("\"true\"")]
    [InlineData("\"yes\"")]
    [InlineData("\"false\"")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task Campaign_AMarkerThatIsNotFalse_IsACampaign_AndNeedsTheConsent(string marker)
    {
        var rig = new RoutineRig();
        var guest = await rig.SeedGuestAsync(OrgA, marketingConsent: false);
        var booking = await rig.SeedBookingAsync(OrgA, guestId: guest.Id);

        var result = await RunAsync(rig, "MessageToGuest", $$"""{"templateId":"promo","isCampaign":{{marker}}}""", new { BookingId = booking.Id });

        AssertBlocked(result, RoutineActionType.MessageToGuest, RoutineCodes.CampaignNeedsConsent);
    }

    [Fact]
    public async Task Campaign_AGuestWhoConsented_IsNotBlocked()
    {
        var rig = new RoutineRig();
        var guest = await rig.SeedGuestAsync(OrgA, marketingConsent: true);
        var booking = await rig.SeedBookingAsync(OrgA, guestId: guest.Id);

        AssertNotBlocked(await RunAsync(rig, "MessageToGuest", """{"isCampaign":true}""", new { BookingId = booking.Id }));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task Campaign_AGuestWhoWasDeletedOrErased_HasNoConsent(bool deleted, bool erasureRequested, bool anonymized)
    {
        var rig = new RoutineRig();
        var guest = await rig.SeedGuestAsync(OrgA, marketingConsent: true, deleted: deleted, erasureRequested: erasureRequested, anonymized: anonymized);
        var booking = await rig.SeedBookingAsync(OrgA, guestId: guest.Id);

        var result = await RunAsync(rig, "MessageToGuest", """{"isCampaign":true}""", new { BookingId = booking.Id });

        AssertBlocked(result, RoutineActionType.MessageToGuest, RoutineCodes.CampaignNeedsConsent);
    }

    [Fact]
    public async Task Campaign_ThePlainMessageOfAnotherOrgsGuest_HasNoConsentEither()
    {
        var rig = new RoutineRig();
        var guestOfAnotherOrg = await rig.SeedGuestAsync(OrgB, marketingConsent: true);
        var booking = await rig.SeedBookingAsync(OrgA, guestId: guestOfAnotherOrg.Id);

        var result = await RunAsync(rig, "MessageToGuest", """{"isCampaign":true}""", new { BookingId = booking.Id });

        AssertBlocked(result, RoutineActionType.MessageToGuest, RoutineCodes.CampaignNeedsConsent);
    }

    [Fact]
    public async Task Campaign_AMessageThatIsNotACampaign_NeedsNoConsent()
    {
        var rig = new RoutineRig();
        var guest = await rig.SeedGuestAsync(OrgA, marketingConsent: false);
        var booking = await rig.SeedBookingAsync(OrgA, guestId: guest.Id);

        AssertNotBlocked(await RunAsync(rig, "MessageToGuest", """{"isCampaign":false,"templateId":"welcome"}""", new { BookingId = booking.Id }));
    }
}
