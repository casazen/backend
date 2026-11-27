using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Identity;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.CheckIn;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Identity;

/// <summary>
/// E70/F02/US01/T01 (NF-188, AC-NF188-1..4): the single-use session of the self-hosted identity verification. The opening records the notice, its hash, the family of
/// the browser and ONE bound guest, once; a notice that is not the served one is refused; the User-Agent is never kept; the consent, the refusal and the request for a
/// person record the choice; every hand-off to a person closes the session with its outcome code, and a closed session takes no new identity step while the request
/// for a person stays open. With <c>Features:CheckInIdentitySelfHosted</c> off nothing of it is written.
/// </summary>
public sealed class IdentitySessionOpeningTests
{
    private const string Token = "token";
    private const string ServedVersion = "TEST-biometric-v1";
    private static readonly string ServedHash = new('a', 64);
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 9, 0, 0, TimeSpan.Zero);
    private static readonly CancellationToken Ct = CancellationToken.None;

    // ─── The opening ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>AC1: the opening records the notice version and hash, the channel and the bound guest once; a second call changes nothing.</summary>
    [Fact]
    public async Task IdentitySessionOpening_RecordsTheNoticeTheChannelAndTheBoundGuest_Once()
    {
        await using var world = await World.CreateAsync();
        var session = await world.StayAsync("Anna", "Bianchi");

        var first = await world.Flow().OpenIdentityAsync(session, ServedVersion, IdentityChannels.AndroidChrome, Ct);

        Assert.Equal(IdentityOpenStatus.Answered, first.Status);
        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal(Now.UtcDateTime, saved.IdentityOpenedAt);
        Assert.Equal(ServedVersion, saved.IdentityNoticeVersion);
        Assert.Equal(ServedHash, saved.IdentityNoticeTextHash);
        Assert.Equal(IdentityChannels.AndroidChrome, saved.IdentityChannel);
        Assert.Equal(session.Booking.GuestId, saved.IdentityGuestId);
        Assert.Null(saved.IdentityChoice);
        Assert.Null(saved.IdentityClosedAt);
        Assert.NotNull(first.State);
        Assert.True(first.State.SelfHosted);
        Assert.True(first.State.Opened);
        Assert.Equal(ServedVersion, first.State.NoticeVersion);
        Assert.False(first.State.Closed);
        Assert.Null(first.State.Outcome);

        // Later, from another browser: nothing changes.
        world.Clock.Advance(TimeSpan.FromMinutes(10));
        var again = await world.Flow().OpenIdentityAsync(session, ServedVersion, IdentityChannels.DesktopFirefox, Ct);

        Assert.Equal(IdentityOpenStatus.Answered, again.Status);
        var unchanged = await world.ReadBackAsync(session.Id);
        Assert.Equal(
            (saved.IdentityOpenedAt, saved.IdentityNoticeVersion, saved.IdentityNoticeTextHash, saved.IdentityChannel, saved.IdentityGuestId, saved.UpdatedAt),
            (unchanged.IdentityOpenedAt, unchanged.IdentityNoticeVersion, unchanged.IdentityNoticeTextHash, unchanged.IdentityChannel, unchanged.IdentityGuestId, unchanged.UpdatedAt));

        // A channel that is not one of the codes is kept as web:other: never a text of the request.
        var other = await world.StayAsync("Bruno", "Conti");
        await world.Flow().OpenIdentityAsync(other, ServedVersion, "Mozilla/5.0 (X11; Linux x86_64) Gecko/20100101", Ct);
        Assert.Equal(IdentityChannels.Other, (await world.ReadBackAsync(other.Id)).IdentityChannel);
    }

    /// <summary>AC2: a notice that is not the version the registry serves now (a past one, another case, none) is refused and nothing is recorded.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TEST-biometric-v0")]
    [InlineData("test-biometric-v1")]
    [InlineData("TEST-biometric-v1-and-a-suffix-that-makes-it-longer-than-fifty-characters")]
    public async Task IdentitySessionOpening_ANoticeThatIsNotTheServedOne_IsRefused(string? notice)
    {
        await using var world = await World.CreateAsync();
        var session = await world.StayAsync("Carla", "Dini");

        var refused = await world.Flow().OpenIdentityAsync(session, notice, IdentityChannels.IosSafari, Ct);

        Assert.Equal(IdentityOpenStatus.NoticeVersionUnknown, refused.Status);
        AssertNothingOpened(await world.ReadBackAsync(session.Id));
    }

    [Fact]
    public async Task IdentitySessionOpening_NoTextServed_IsRefused_AndAVerificationThatIsOverIsNotOpened()
    {
        await using var world = await World.CreateAsync();

        // The registry serves no consent (production with nothing approved): no notice to record.
        var withoutText = await world.StayAsync("Dario", "Esposito");
        world.ServeNoConsent();
        Assert.Equal(IdentityOpenStatus.NoticeVersionUnknown, (await world.Flow().OpenIdentityAsync(withoutText, ServedVersion, IdentityChannels.IosSafari, Ct)).Status);
        AssertNothingOpened(await world.ReadBackAsync(withoutText.Id));
        world.ServeTheConsent();

        // Already with a person (the guest refused before the flow was self-hosted) or already passed: the state answers and nothing is opened.
        var refused = await world.StayAsync("Elena", "Ferrari");
        refused.BiometricConsentRefusedAt = Now.UtcDateTime;
        refused.OperatorRequestedAt = Now.UtcDateTime;
        var passed = await world.StayAsync("Franco", "Galli");
        passed.BiometricOutcome = BiometricOutcome.Accept;
        await world.Db.SaveChangesAsync(Ct);

        foreach (var over in new[] { refused, passed })
        {
            var answer = await world.Flow().OpenIdentityAsync(over, ServedVersion, IdentityChannels.IosSafari, Ct);
            Assert.Equal(IdentityOpenStatus.Answered, answer.Status);
            Assert.False(answer.State!.Opened);
            AssertNothingOpened(await world.ReadBackAsync(over.Id));
        }
    }

    /// <summary>
    /// Proof (privacy, AC3): the User-Agent of the request is never kept or logged, only its family. Through the real endpoints (the opening and the consent) with a
    /// header that carries a unique marker: no text column of the saved session, no line of the logs and no answer holds it, and the session says the family.
    /// </summary>
    [Fact]
    public async Task IdentitySessionOpening_UserAgentIsNotPersisted()
    {
        const string marker = "UNIQUE-MARKER-7731";
        const string userAgent = $"Mozilla/5.0 (Linux; Android 14; Pixel 8 Build/{marker}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Mobile Safari/537.36";
        await using var world = await World.CreateAsync();
        var session = await world.StayAsync("Gina", "Neri");
        var controller = world.Controller(session, userAgent);

        var opened = Assert.IsType<OkObjectResult>((await controller.OpenIdentity(Token, new IdentityOpenRequest { NoticeVersion = ServedVersion })).Result);
        var consented = Assert.IsType<OkObjectResult>(
            (await controller.RecordBiometricConsent(Token, new BiometricConsentRequest { ConsentGiven = true, ConsentVersion = ServedVersion })).Result);

        var saved = await world.ReadBackAsync(session.Id);
        Assert.Equal(IdentityChannels.AndroidChrome, saved.IdentityChannel);
        Assert.Equal(IdentityChoices.Automatic, saved.IdentityChoice);
        foreach (var column in typeof(GuestCheckInSession).GetProperties().Where(property => property.PropertyType == typeof(string)))
        {
            var value = (string?)column.GetValue(saved) ?? string.Empty;
            Assert.DoesNotContain(marker, value, StringComparison.Ordinal);
            Assert.DoesNotContain("Pixel 8", value, StringComparison.Ordinal);
            Assert.DoesNotContain("Mozilla", value, StringComparison.Ordinal);
        }

        Assert.NotEmpty(world.Logs);
        Assert.All(world.Logs, line => Assert.DoesNotContain(marker, line, StringComparison.Ordinal));
        Assert.All(world.Logs, line => Assert.DoesNotContain("Mozilla", line, StringComparison.Ordinal));
        foreach (var answer in new[] { opened.Value, consented.Value })
            Assert.DoesNotContain(marker, JsonSerializer.Serialize(answer, new JsonSerializerOptions(JsonSerializerDefaults.Web)), StringComparison.Ordinal);
    }

    // ─── The choice ──────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AC4: a consent given to the served version is the choice <c>automatic</c> and opens the step when it was not opened (the consent version is the notice); a refusal
    /// is <c>assisted</c>; a request for a person on an opened verification is the switch to <c>assisted</c>; a consent to a stale version records nothing.
    /// </summary>
    [Fact]
    public async Task IdentitySessionOpening_TheConsentAndTheRequestForAPerson_RecordTheChoice()
    {
        await using var world = await World.CreateAsync();

        var agreed = await world.StayAsync("Hugo", "Villa");
        Assert.Equal(BiometricConsentStatus.Recorded, (await world.Flow().RecordConsentAsync(agreed, true, ServedVersion, IdentityChannels.IosSafari, Ct)).Status);
        var afterConsent = await world.ReadBackAsync(agreed.Id);
        Assert.Equal((Now.UtcDateTime, ServedVersion, ServedHash, IdentityChannels.IosSafari), (afterConsent.IdentityOpenedAt, afterConsent.IdentityNoticeVersion, afterConsent.IdentityNoticeTextHash, afterConsent.IdentityChannel));
        Assert.Equal(agreed.Booking.GuestId, afterConsent.IdentityGuestId);
        Assert.Equal((IdentityChoices.Automatic, Now.UtcDateTime), (afterConsent.IdentityChoice, afterConsent.IdentityChoiceAt));
        Assert.Null(afterConsent.IdentityClosedAt);

        // The guest asks for a person: the switch to the assisted path, and the hand-off closes the verification.
        world.Clock.Advance(TimeSpan.FromMinutes(3));
        await world.Flow().RequestOperatorAsync(agreed, Ct);
        var afterRequest = await world.ReadBackAsync(agreed.Id);
        Assert.Equal((IdentityChoices.Assisted, Now.UtcDateTime.AddMinutes(3)), (afterRequest.IdentityChoice, afterRequest.IdentityChoiceAt));
        Assert.Equal(IdentityOutcomeCodes.Assisted, afterRequest.IdentityOutcomeCode);

        // A refusal, without a version, on a session never opened: opened with the served consent as the notice, the choice assisted, closed as refused.
        var refusing = await world.StayAsync("Ida", "Piccoli");
        Assert.Equal(BiometricConsentStatus.Refused, (await world.Flow().RecordConsentAsync(refusing, false, null, IdentityChannels.DesktopSafari, Ct)).Status);
        var refused = await world.ReadBackAsync(refusing.Id);
        Assert.Equal((ServedVersion, IdentityChannels.DesktopSafari, IdentityChoices.Assisted), (refused.IdentityNoticeVersion, refused.IdentityChannel, refused.IdentityChoice));
        Assert.Equal(IdentityOutcomeCodes.ConsentRefused, refused.IdentityOutcomeCode);

        // A consent to a stale version is no consent and no choice.
        var stale = await world.StayAsync("Luca", "Rossi");
        Assert.Equal(BiometricConsentStatus.VersionUnknown, (await world.Flow().RecordConsentAsync(stale, true, "TEST-biometric-v0", IdentityChannels.IosSafari, Ct)).Status);
        AssertNothingOpened(await world.ReadBackAsync(stale.Id));

        // A request for a person on a session never opened opens nothing: the hand-off happens, the self-hosted columns stay empty.
        var neverOpened = await world.StayAsync("Mara", "Rossetti");
        await world.Flow().RequestOperatorAsync(neverOpened, Ct);
        var handedOff = await world.ReadBackAsync(neverOpened.Id);
        Assert.NotNull(handedOff.OperatorRequestedAt);
        AssertNothingOpened(handedOff);
    }

    // ─── The closure ─────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(VerificationAlertReasons.Review, IdentityOutcomeCodes.Review)]
    [InlineData(VerificationAlertReasons.AttemptsExhausted, IdentityOutcomeCodes.Review)]
    [InlineData(VerificationAlertReasons.ConsentRefused, IdentityOutcomeCodes.ConsentRefused)]
    [InlineData(VerificationAlertReasons.GuestRequest, IdentityOutcomeCodes.Assisted)]
    [InlineData(VerificationAlertReasons.TechnicalError, IdentityOutcomeCodes.TechnicalError)]
    [InlineData(VerificationAlertReasons.BiometricsDisabled, IdentityOutcomeCodes.Unavailable)]
    [InlineData(VerificationAlertReasons.LegalTextNotApproved, IdentityOutcomeCodes.Unavailable)]
    [InlineData(VerificationAlertReasons.MinorExcluded, IdentityOutcomeCodes.MinorExcluded)]
    public void IdentityOutcomeOf_EveryReasonOfAHandOff_HasItsOutcomeCode(string reason, string outcome)
    {
        Assert.Equal(outcome, IdentityVerificationFlowService.IdentityOutcomeOf(reason));
        Assert.Contains(outcome, IdentityOutcomeCodes.All);
    }

    [Fact]
    public void IdentityOutcomeOf_EveryReasonIsMapped_AndTheGuestSeesOkOnlyForAnAutomaticApproval()
    {
        Assert.All(VerificationAlertReasons.All, reason => Assert.NotEqual(IdentityOutcomeCodes.AutoApproved, IdentityVerificationFlowService.IdentityOutcomeOf(reason)));
        Assert.Equal(IdentityOutcomeCodes.Review, IdentityVerificationFlowService.IdentityOutcomeOf("a_reason_of_tomorrow"));
        Assert.Equal(IdentityVerificationGuestResults.Ok, IdentityVerificationFlowService.GuestOutcomeOf(IdentityOutcomeCodes.AutoApproved));
        Assert.All(IdentityOutcomeCodes.All.Where(code => code != IdentityOutcomeCodes.AutoApproved), code =>
            Assert.Equal(IdentityVerificationGuestResults.Operator, IdentityVerificationFlowService.GuestOutcomeOf(code)));
    }

    /// <summary>
    /// AC5: every hand-off of an opened session to a person, along the real paths of the flow, sets <c>IdentityClosedAt</c> and the code of its reason; a later hand-off
    /// keeps the first code, and the state says closed with the outcome <c>operator</c>.
    /// </summary>
    [Theory]
    [InlineData("review", IdentityOutcomeCodes.Review)]
    [InlineData("last-attempt", IdentityOutcomeCodes.Review)]
    [InlineData("error", IdentityOutcomeCodes.TechnicalError)]
    [InlineData("consent-refused", IdentityOutcomeCodes.ConsentRefused)]
    [InlineData("request-operator", IdentityOutcomeCodes.Assisted)]
    [InlineData("biometrics-disabled", IdentityOutcomeCodes.Unavailable)]
    [InlineData("legal-text-gone", IdentityOutcomeCodes.Unavailable)]
    [InlineData("minor", IdentityOutcomeCodes.MinorExcluded)]
    public async Task IdentitySessionOpening_EveryHandOffToAPerson_ClosesWithItsOutcomeCode(string path, string outcome)
    {
        await using var world = await World.CreateAsync();
        var session = await world.StayAsync("Nora", "Galli", path == "minor" ? new DateTime(2014, 3, 3) : null);
        Assert.Equal(IdentityOpenStatus.Answered, (await world.Flow().OpenIdentityAsync(session, ServedVersion, IdentityChannels.AndroidChrome, Ct)).Status);

        // E73/F01/US01/T02: with the self-hosted flow on the two-frame selfie is closed, so the review, the attempts used and the error come from the closure of the presence.
        switch (path)
        {
            case "review":
                await world.ReadyAsync(session);
                world.Collector.Next(PresenceClosureKit.LivenessFailed());
                await world.PresenceAsync(session);
                break;
            case "last-attempt":
                // The attempts are used up (by the legacy path before the self-hosted flow, or a maximum lowered since): the closure hands the verification to a person.
                await world.ReadyAsync(session);
                session.BiometricAttempts = 3;
                await world.Db.SaveChangesAsync(Ct);
                await world.PresenceAsync(session);
                break;
            case "error":
                await world.ReadyAsync(session);
                world.Collector.Fail(new InvalidOperationException("model crashed"));
                await world.PresenceAsync(session);
                break;
            case "consent-refused":
                await world.Flow().RecordConsentAsync(session, false, null, IdentityChannels.AndroidChrome, Ct);
                break;
            case "request-operator":
                await world.Flow().RequestOperatorAsync(session, Ct);
                break;
            case "biometrics-disabled":
                await world.DisableBiometricsAsync();
                await world.Flow().RecordConsentAsync(session, true, ServedVersion, IdentityChannels.AndroidChrome, Ct);
                break;
            case "legal-text-gone":
                world.ServeNoConsent();
                await world.Flow().RequestOperatorAsync(session, Ct);
                break;
            case "minor":
                await world.Flow().RecordConsentAsync(session, true, ServedVersion, IdentityChannels.AndroidChrome, Ct);
                break;
        }

        var closed = await world.ReadBackAsync(session.Id);
        Assert.NotNull(closed.IdentityClosedAt);
        Assert.Equal(outcome, closed.IdentityOutcomeCode);
        Assert.NotNull(closed.OperatorRequestedAt);
        var state = await world.Flow().GetStateAsync(session, Ct);
        Assert.True(state.Closed);
        Assert.Equal(IdentityVerificationGuestResults.Operator, state.Outcome);

        // A later hand-off (the guest asks for a person after the closure) keeps the first code and time.
        world.Clock.Advance(TimeSpan.FromMinutes(5));
        await world.Flow().RequestOperatorAsync(session, Ct);
        var after = await world.ReadBackAsync(session.Id);
        Assert.Equal((closed.IdentityClosedAt, outcome), (after.IdentityClosedAt, after.IdentityOutcomeCode));
    }

    /// <summary>
    /// Proof (security, AC5, AC-NF188-3): a closed verification refuses every new identity step with 409 <c>identity_already_closed</c> and the state, through the real
    /// endpoints: the opening, the document, the presence challenge and the selfie; nothing of them reaches the flow's work. The request for a person stays available.
    /// </summary>
    [Fact]
    public async Task IdentitySessionOpening_ClosedSessionRefusesNewAttempts()
    {
        await using var world = await World.CreateAsync();
        var session = await world.StayAsync("Olga", "Bassi");
        var controller = world.Controller(session, "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:123.0) Gecko/20100101 Firefox/123.0");
        Assert.IsType<OkObjectResult>((await controller.OpenIdentity(Token, new IdentityOpenRequest { NoticeVersion = ServedVersion })).Result);
        Assert.IsType<OkObjectResult>((await controller.RequestOperator(Token)).Result);
        var closed = await world.ReadBackAsync(session.Id);
        Assert.Equal(IdentityOutcomeCodes.Assisted, closed.IdentityOutcomeCode);
        var alerts = world.Alerts.Count;

        AssertClosed((await controller.OpenIdentity(Token, new IdentityOpenRequest { NoticeVersion = ServedVersion })).Result);
        AssertClosed((await controller.UploadDocument(Token, Image("document"))).Result);
        AssertClosed((await controller.IssueSelfieChallenge(Token)).Result);
        AssertClosed((await controller.UploadSelfie(Token, Image("selfie"), turnedSelfie: Image("turnedSelfie"), challengeToken: "anything")).Result);
        Assert.Equal(IdentityOpenStatus.Closed, (await world.Flow().OpenIdentityAsync(session, ServedVersion, IdentityChannels.DesktopFirefox, Ct)).Status);

        // Nothing ran: no picture read or kept, no attempt, no agent, the closure as it was.
        Assert.Equal(0, world.Agent.Calls);
        Assert.Equal(0, world.Mrz.Reads);
        Assert.Empty(world.Storage.Objects);
        var after = await world.ReadBackAsync(session.Id);
        Assert.Equal((closed.IdentityClosedAt, closed.IdentityOutcomeCode, closed.BiometricAttempts, closed.DocumentCheckJson),
            (after.IdentityClosedAt, after.IdentityOutcomeCode, after.BiometricAttempts, after.DocumentCheckJson));

        // The way to a person is still open.
        var request = Assert.IsType<OperatorRequestResponse>(Assert.IsType<OkObjectResult>((await controller.RequestOperator(Token)).Result).Value);
        Assert.True(request.OperatorPathOpen);
        Assert.True(request.State.Closed);
        Assert.Equal(alerts, world.Alerts.Count);
    }

    // ─── The bound guest ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Proof (data): one guest per session. The opening binds the guest the flow verifies (the booker's row here); an edit of the guest list that would make the rule of
    /// today pick somebody else does not move it: a second opening and the consent keep it, and the step asks the bound guest. A first guest with a document that is not the
    /// booker's row is bound by the id of its row, and stays bound wherever the list moves it. With the self-hosted flow off the rule of today applies.
    /// </summary>
    [Fact]
    public async Task IdentitySessionOpening_BoundGuestNeverChanges()
    {
        await using var world = await World.CreateAsync();
        var session = await world.StayAsync("Paolo", "Riva");
        var booker = session.Booking.GuestId;
        var bookerRow = Guid.NewGuid();
        var otherHead = Guid.NewGuid();
        world.Guests = booking => [Row(booking, bookerRow, 0, StayGuestType.HeadOfFamily, booker)];

        await world.Flow().OpenIdentityAsync(session, ServedVersion, IdentityChannels.AndroidChrome, Ct);
        Assert.Equal(booker, (await world.ReadBackAsync(session.Id)).IdentityGuestId);

        // The list is edited: the booker's row is a family member now and a new head of family comes after it. The rule of today would verify the new head.
        world.Guests = booking =>
        [
            Row(booking, bookerRow, 0, StayGuestType.FamilyMember, booker),
            Row(booking, otherHead, 1, StayGuestType.HeadOfFamily, null),
        ];
        await world.Flow().OpenIdentityAsync(session, ServedVersion, IdentityChannels.DesktopChromium, Ct);
        await world.Flow().RecordConsentAsync(session, true, ServedVersion, IdentityChannels.DesktopChromium, Ct);
        var kept = await world.ReadBackAsync(session.Id);
        Assert.Equal(booker, kept.IdentityGuestId);
        Assert.Equal(IdentityChannels.AndroidChrome, kept.IdentityChannel);
        Assert.Equal(0, (await world.Flow().GetStateAsync(session, Ct)).VerifyingGuestPosition);

        // A first guest with a document that is not the booker's row: bound by its row, and still the one asked when the list moves it.
        var second = await world.StayAsync("Rita", "Sala");
        var headRow = Guid.NewGuid();
        world.Guests = booking =>
        [
            Row(booking, Guid.NewGuid(), 0, StayGuestType.FamilyMember, booking.GuestId),
            Row(booking, headRow, 1, StayGuestType.HeadOfGroup, null),
        ];
        await world.Flow().OpenIdentityAsync(second, ServedVersion, IdentityChannels.AndroidChrome, Ct);
        Assert.Equal(headRow, (await world.ReadBackAsync(second.Id)).IdentityGuestId);
        world.Guests = booking =>
        [
            Row(booking, Guid.NewGuid(), 0, StayGuestType.HeadOfFamily, booking.GuestId),
            Row(booking, Guid.NewGuid(), 1, StayGuestType.FamilyMember, null),
            Row(booking, headRow, 2, StayGuestType.GroupMember, null),
        ];
        Assert.Equal(2, (await world.Flow().GetStateAsync(second, Ct)).VerifyingGuestPosition);
        Assert.Equal(headRow, (await world.ReadBackAsync(second.Id)).IdentityGuestId);

        // The self-hosted flow off: the binding is not read, the rule of today picks the first guest with a document.
        world.Flags.Set(FeatureFlags.CheckInIdentitySelfHosted, false);
        Assert.Equal(0, (await world.Flow().GetStateAsync(second, Ct)).VerifyingGuestPosition);
    }

    /// <summary>The bound guest left the stay: the verification is not offered any more and never moves to somebody else; the consent hands it to a person for review.</summary>
    [Fact]
    public async Task IdentitySessionOpening_TheBoundGuestLeftTheStay_APersonDecides()
    {
        await using var world = await World.CreateAsync();
        var session = await world.StayAsync("Sara", "Testa");
        var headRow = Guid.NewGuid();
        world.Guests = booking =>
        [
            Row(booking, Guid.NewGuid(), 0, StayGuestType.FamilyMember, booking.GuestId),
            Row(booking, headRow, 1, StayGuestType.HeadOfFamily, null),
        ];
        await world.Flow().OpenIdentityAsync(session, ServedVersion, IdentityChannels.AndroidChrome, Ct);

        world.Guests = booking => [Row(booking, Guid.NewGuid(), 0, StayGuestType.HeadOfFamily, booking.GuestId)];
        var state = await world.Flow().GetStateAsync(session, Ct);
        Assert.False(state.BiometricOffered);
        Assert.Equal(IdentityVerificationSteps.Operator, state.Step);

        Assert.Equal(BiometricConsentStatus.Operator, (await world.Flow().RecordConsentAsync(session, true, ServedVersion, IdentityChannels.AndroidChrome, Ct)).Status);
        var closed = await world.ReadBackAsync(session.Id);
        Assert.Null(closed.BiometricConsentAt);
        Assert.Equal(IdentityOutcomeCodes.Review, closed.IdentityOutcomeCode);
        Assert.Equal(headRow, closed.IdentityGuestId);
        Assert.Equal(VerificationAlertReasons.Review, Assert.Single(world.Alerts).Reason);
    }

    /// <summary>AC6 for the flow: with <c>CheckInIdentitySelfHosted</c> off the opening, the consent and the request for a person write none of the self-hosted columns.</summary>
    [Fact]
    public async Task IdentitySessionOpening_SelfHostedOff_RecordsNothing_AndTheStateIsTheLegacyOne()
    {
        await using var world = await World.CreateAsync(FeatureFlags.CheckInIdentityVerification);
        var session = await world.StayAsync("Tina", "Uberti");

        var answer = await world.Flow().OpenIdentityAsync(session, ServedVersion, IdentityChannels.AndroidChrome, Ct);
        Assert.Equal(BiometricConsentStatus.Recorded, (await world.Flow().RecordConsentAsync(session, true, ServedVersion, IdentityChannels.AndroidChrome, Ct)).Status);
        await world.Flow().RequestOperatorAsync(session, Ct);

        Assert.Equal(IdentityOpenStatus.Answered, answer.Status);
        var saved = await world.ReadBackAsync(session.Id);
        AssertNothingOpened(saved);
        Assert.NotNull(saved.OperatorRequestedAt);
        var state = await world.Flow().GetStateAsync(session, Ct);
        Assert.False(state.SelfHosted);
        Assert.False(state.Opened);
        Assert.Null(state.NoticeVersion);
        Assert.Null(state.Choice);
        Assert.False(state.Closed);
        Assert.Null(state.Outcome);
    }

    [Fact]
    public async Task IdentitySessionOpening_TheState_SaysTheChoiceAndOkOnlyForAnAutomaticApproval()
    {
        await using var world = await World.CreateAsync();
        var session = await world.StayAsync("Ugo", "Valli");
        await world.Flow().RecordConsentAsync(session, true, ServedVersion, IdentityChannels.AndroidChrome, Ct);

        var open = await world.Flow().GetStateAsync(session, Ct);
        Assert.True(open.SelfHosted);
        Assert.True(open.Opened);
        Assert.Equal(ServedVersion, open.NoticeVersion);
        Assert.Equal(IdentityChoices.Automatic, open.Choice);
        Assert.False(open.Closed);
        Assert.Null(open.Outcome);

        // The success path of the automatic approval (a later task) writes this code: the guest then sees ok.
        session.IdentityClosedAt = Now.UtcDateTime;
        session.IdentityOutcomeCode = IdentityOutcomeCodes.AutoApproved;
        await world.Db.SaveChangesAsync(Ct);
        var approved = await world.Flow().GetStateAsync(session, Ct);
        Assert.True(approved.Closed);
        Assert.Equal(IdentityVerificationGuestResults.Ok, approved.Outcome);
    }

    // ─── Fixtures ────────────────────────────────────────────────────────────────────────────────────────

    private static void AssertNothingOpened(GuestCheckInSession session)
    {
        Assert.Null(session.IdentityOpenedAt);
        Assert.Null(session.IdentityNoticeVersion);
        Assert.Null(session.IdentityNoticeTextHash);
        Assert.Null(session.IdentityChannel);
        Assert.Null(session.IdentityGuestId);
        Assert.Null(session.IdentityChoice);
        Assert.Null(session.IdentityChoiceAt);
        Assert.Null(session.IdentityClosedAt);
        Assert.Null(session.IdentityOutcomeCode);
    }

    private static void AssertClosed(IActionResult? result)
    {
        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        var details = Assert.IsType<ProblemDetails>(problem.Value);
        Assert.Equal(IdentityApiErrors.IdentityAlreadyClosed, details.Extensions["code"]);
        var state = Assert.IsType<PublicIdentityVerificationStateDto>(details.Extensions[PublicGuestCheckInController.IdentityStateExtension]);
        Assert.True(state.Closed);
        Assert.Equal(IdentityVerificationGuestResults.Operator, state.Outcome);
        Assert.True(state.OperatorPathOpen);
    }

    private static StayGuest Row(Booking booking, Guid id, int position, StayGuestType type, Guid? bookerId) => new()
    {
        Id = id,
        BookingId = booking.Id,
        OrgId = booking.OrgId,
        GuestId = bookerId,
        Position = position,
        Type = type,
        FirstName = $"Ospite{position}",
        LastName = "Test",
        DateOfBirth = new DateTime(1985, 6, 1),
    };

    private static FormFile Image(string field)
    {
        var bytes = TestImages.Png(40, 30, seed: field.Length);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, field, $"{field}.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png",
        };
    }

    /// <summary>One org on the Agency plan (the verification on by default), a property in Italy, the flow with its fakes and the self-hosted flow on.</summary>
    private sealed class World : IAsyncDisposable
    {
        private readonly DbContextOptions<AppDbContext> _options =
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"identity-session-{Guid.NewGuid()}").Options;

        private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();
        private readonly Guid _propertyId = Guid.NewGuid();
        private readonly Mock<ICheckInLegalTexts> _legalTexts = ActivePoseFlowTestVerifier.CreateRegistry();

        // E73/F01/US01/T02: the closure of the presence, the real path of a review or an error with the self-hosted flow on: the committed policy pinned, the models there, a
        // double of the pipeline and a segment in observation.
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "casazen-identity-session-" + Guid.NewGuid().ToString("N"));
        private readonly Casazen.Infrastructure.Identity.IdentityPolicyProvider _policy;
        private readonly Mock<IIdentitySegmentService> _segments = new();

        private World(string[] flags)
        {
            Db = new AppDbContext(_options, null, _dataProtection);
            Flags = new TestFeatureFlags(flags);
            Directory.CreateDirectory(_directory);
            _policy = PresenceClosureKit.Policy(_directory);
            _segments
                .Setup(segments => segments.ResolveAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(EvidenceBundles.Observation);
        }

        public ScriptedEvidenceCollector Collector { get; } = new();

        public Guid OrgId { get; } = Guid.NewGuid();

        public AppDbContext Db { get; }

        public TestFeatureFlags Flags { get; }

        public FakeTimeProvider Clock { get; } = new(Now);

        public ScriptedAgent Agent { get; } = new();

        public CountingMrz Mrz { get; } = new();

        public MemoryStorage Storage { get; } = new();

        public RecordingAlerts Alerts { get; } = new();

        public List<string> Logs { get; } = [];

        /// <summary>The guests of a stay as the flow reads them; null: the booker alone, as the real service answers before anyone is registered.</summary>
        public Func<Booking, IReadOnlyList<StayGuest>>? Guests { get; set; }

        public static async Task<World> CreateAsync(params string[] flags)
        {
            var world = new World(flags.Length == 0 ? [FeatureFlags.CheckInIdentityVerification, FeatureFlags.CheckInIdentitySelfHosted] : flags);
            world.Db.Orgs.Add(new OrgEntity
            {
                Id = world.OrgId,
                Name = "Agenzia sessioni",
                Slug = $"sessioni-{world.OrgId:N}"[..20],
                DisplayName = "Agenzia sessioni",
                ContactEmail = "org@example.com",
                PlanTier = PlanTier.Agency,
            });
            world.Db.Properties.Add(new Property
            {
                Id = world._propertyId,
                OrgId = world.OrgId,
                OwnerId = "auth0|owner",
                Name = "Casa Test",
                Address = "Via Test 1",
                City = "Roma",
                PostalCode = "00100",
                Country = "IT",
                Bedrooms = 1,
                Bathrooms = 1,
                MaxGuests = 2,
                NightlyRate = 100m,
            });
            await world.Db.SaveChangesAsync(Ct);
            return world;
        }

        public IdentityVerificationFlowService Flow() => new(
            Db,
            Agent,
            Alerts,
            Mock.Of<IAccessCodeService>(),
            Mock.Of<IEmailQueue>(),
            Mrz,
            Mock.Of<IDocumentOcrService>(),
            new RosterOfTheTest(this),
            Storage,
            _dataProtection,
            Clock,
            new ListLogger<IdentityVerificationFlowService>(Logs),
            legalTexts: _legalTexts.Object,
            activePose: new ActivePoseFlowTestVerifier(),
            featureFlags: Flags,
            identityPolicy: _policy,
            biometricModels: new FakeBiometricModelStore(),
            identityOptions: Microsoft.Extensions.Options.Options.Create(new Casazen.Core.Options.IdentityVerificationOptions()),
            presenceIssuer: new Casazen.Infrastructure.Identity.PresenceChallengeIssuer(),
            frameReader: new Casazen.Infrastructure.Identity.FrameSequenceReader(
                Microsoft.Extensions.Options.Options.Create(new Casazen.Core.Options.IdentityVerificationOptions()),
                Microsoft.Extensions.Options.Options.Create(new Casazen.Core.Options.BiometricModelOptions()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Casazen.Infrastructure.Identity.FrameSequenceReader>.Instance),
            evidenceCollector: Collector,
            segments: _segments.Object,
            decisionEngine: new Casazen.Infrastructure.Identity.IdentityDecisionEngine());

        /// <summary>One attempt of the presence on the real closure: a challenge when the step offers one, the frames and a new key; the pipeline answers what the test scripted.</summary>
        public async Task<PresenceStepResult> PresenceAsync(GuestCheckInSession session)
        {
            var flow = Flow();
            var challenge = await flow.IssuePresenceChallengeAsync(session, Ct);
            var answer = await flow.SubmitPresenceSequenceAsync(session, PresenceClosureKit.Submission(PresenceClosureKit.NewKey(), challenge?.ChallengeToken), Ct);
            Assert.Equal((PresenceStepStatus.Done, IdentityVerificationGuestResults.Operator), (answer.Status, answer.Result));
            return answer;
        }

        /// <summary>The public endpoints over the real flow, for <paramref name="session"/> and a request that carries <paramref name="userAgent"/>.</summary>
        public PublicGuestCheckInController Controller(GuestCheckInSession session, string userAgent)
        {
            var checkIn = new Mock<IGuestCheckInService>();
            checkIn.Setup(s => s.GetSessionByTokenAsync(Token)).ReturnsAsync(session);
            var context = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
            };
            context.Request.Headers.UserAgent = userAgent;
            return new PublicGuestCheckInController(
                checkIn.Object,
                Mock.Of<IAlloggiatiCodeTableService>(),
                Mock.Of<ICheckInDocumentReader>(),
                Mock.Of<IAlloggiatiReportScheduler>(),
                Flow(),
                Flags,
                Mock.Of<IStringLocalizer<SharedResources>>(),
                new ListLogger<PublicGuestCheckInController>(Logs))
            {
                ControllerContext = new ControllerContext { HttpContext = context },
            };
        }

        public void ServeNoConsent() =>
            _legalTexts.Setup(registry => registry.GetActiveVersion(CheckInLegalTextKinds.BiometricConsent)).Returns((CheckInLegalVersion?)null);

        public void ServeTheConsent() =>
            _legalTexts.Setup(registry => registry.GetActiveVersion(CheckInLegalTextKinds.BiometricConsent))
                .Returns(new CheckInLegalVersion(CheckInLegalTextKinds.BiometricConsent, ServedVersion, false, ServedHash));

        public async Task DisableBiometricsAsync()
        {
            Db.BiometricSettings.Add(new BiometricSettings
            {
                OrgId = OrgId,
                Enabled = false,
                AcceptThreshold = 0.85m,
                EscalateThreshold = 0.70m,
                MaxAttempts = 3,
                CreatedAt = Now.UtcDateTime,
                UpdatedAt = Now.UtcDateTime,
            });
            await Db.SaveChangesAsync(Ct);
        }

        /// <summary>A confirmed stay of a booker with an open check-in session, loaded as the public endpoint loads it.</summary>
        public async Task<GuestCheckInSession> StayAsync(string firstName, string lastName, DateTime? dateOfBirth = null)
        {
            var guest = new Guest
            {
                Id = Guid.NewGuid(),
                OrgId = OrgId,
                FirstName = firstName,
                LastName = lastName,
                Email = $"{firstName.ToLowerInvariant()}@example.com",
                DateOfBirth = dateOfBirth ?? new DateTime(1990, 1, 1),
            };
            var booking = new Booking
            {
                Id = Guid.NewGuid(),
                OrgId = OrgId,
                PropertyId = _propertyId,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 11, 30),
                CheckOutDate = new DateTime(2026, 12, 2),
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                NumberOfGuests = 1,
                TotalPrice = 200m,
            };
            var session = new GuestCheckInSession
            {
                BookingId = booking.Id,
                OrgId = OrgId,
                TokenHash = Guid.NewGuid().ToString("N"),
                ExpiresAt = Now.UtcDateTime.AddDays(7),
                Status = GuestCheckInSessionStatus.InCompilazione,
                Language = "it",
                CreatedAt = Now.UtcDateTime,
                UpdatedAt = Now.UtcDateTime,
            };
            Db.Guests.Add(guest);
            Db.Bookings.Add(booking);
            Db.GuestCheckInSessions.Add(session);
            await Db.SaveChangesAsync(Ct);

            return await Db.GuestCheckInSessions
                .Include(s => s.Booking).ThenInclude(b => b.Property)
                .Include(s => s.Booking).ThenInclude(b => b.Guest)
                .SingleAsync(s => s.Id == session.Id, Ct);
        }

        /// <summary>The consent recorded and the document read, its picture kept encrypted: the selfie can be compared.</summary>
        public async Task ReadyAsync(GuestCheckInSession session)
        {
            Assert.Equal(BiometricConsentStatus.Recorded, (await Flow().RecordConsentAsync(session, true, ServedVersion, IdentityChannels.AndroidChrome, Ct)).Status);
            using var document = new MemoryStream(TestImages.Png(120, 80, seed: 3));
            Assert.True((await Flow().SubmitDocumentAsync(session, document, Ct)).DocumentStored);
        }

        /// <summary>The session as it was saved, read by another context on the same store.</summary>
        public async Task<GuestCheckInSession> ReadBackAsync(Guid sessionId)
        {
            await using var other = new AppDbContext(_options, null, _dataProtection);
            return await other.GuestCheckInSessions.AsNoTracking().IgnoreQueryFilters().SingleAsync(s => s.Id == sessionId, Ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            if (Directory.Exists(_directory))
                Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>The guests of the stay the world says, else the booker alone; the flow reads nothing else of the service.</summary>
    private sealed class RosterOfTheTest(World world) : IStayGuestService
    {
        public Task<IReadOnlyList<StayGuest>> GetForBookingAsync(Booking booking, CancellationToken cancellationToken = default)
        {
            if (world.Guests is { } guests)
                return Task.FromResult(guests(booking));

            IReadOnlyList<StayGuest> bookerAlone = booking.Guest is { } booker ? new[] { StayGuest.FromBooker(booking, booker) } : Array.Empty<StayGuest>();
            return Task.FromResult(bookerAlone);
        }

        public Task<IReadOnlySet<int>> GetDocumentNumberPositionsAsync(Booking booking, IReadOnlyList<StayGuest> guests, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<StayGuest>>> GetForBookingsAsync(IReadOnlyCollection<Booking> bookings, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StayGuestFieldError>> ValidateAsync(IReadOnlyList<StayGuestInput> guests, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StayGuestFieldError>> ValidateAsync(Booking booking, IReadOnlyList<StayGuestInput> guests, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StayGuestSaveResult> ReplaceAsync(
            Booking booking,
            IReadOnlyList<StayGuestInput> guests,
            StayGuestAuthor author,
            bool save = true,
            string? vehiclePlate = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    /// <summary>An agent that accepts every capture and answers the selfie from a script (an outcome, or an exception).</summary>
    private sealed class ScriptedAgent : IIdentityVerificationAgent
    {
        private readonly Queue<Func<IdentityVerificationOutcome>> _answers = new();

        public int Calls { get; private set; }

        public void Next(IdentityVerificationResult result, bool lastAttempt = false) => _answers.Enqueue(() => new IdentityVerificationOutcome(
            result,
            [new VerificationReason(VerificationReasons.Checks.Similarity, VerificationReasons.Results.Failed, 0.31, 0.70)],
            [CaptureGuidanceCodes.RetakeSelfie],
            lastAttempt,
            "SFace",
            "2021dec",
            new VerificationThresholds(0.85, 0.70, 0.7, 3, CaptureQualityRules.Version, Provisional: true),
            800));

        public void Fail(Exception error) => _answers.Enqueue(() => throw error);

        public Task<CaptureAssessment> AssessCaptureAsync(Stream image, CaptureKind kind, CancellationToken ct) =>
            Task.FromResult(new CaptureAssessment(true, [], [], true));

        public Task<IdentityVerificationOutcome> VerifyAsync(IdentityVerificationRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_answers.Dequeue()());
        }
    }

    /// <summary>The alerts raised, in order.</summary>
    private sealed class RecordingAlerts : List<(Guid SessionId, string Reason)>, IVerificationOperatorAlerts
    {
        public Task RaiseAsync(Guid sessionId, string reasonCode, CancellationToken ct)
        {
            Add((sessionId, reasonCode));
            return Task.CompletedTask;
        }

        public Task ResolveAsync(Guid orgId, Guid bookingId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>An MRZ that reads a valid, unexpired Italian card of an adult, counting the pictures it was shown.</summary>
    private sealed class CountingMrz : IMrzExtractionService
    {
        public int Reads { get; private set; }

        public Task<MrzExtractionResult?> ExtractAsync(Stream imageStream, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult<MrzExtractionResult?>(Read());
        }

        public Task<MrzReading> ReadAsync(Stream imageStream, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(new MrzReading(Read(), []));
        }

        private static MrzExtractionResult Read() => new()
        {
            DocumentType = "IdCard",
            Surname = "TEST",
            GivenNames = "TEST",
            DocumentNumber = "CA00000AA",
            DateOfBirth = new DateTime(1990, 1, 1),
            ExpirationDate = new DateTime(2030, 1, 1),
            IsExpired = false,
            Nationality = "ITA",
            CheckDigitsValid = true,
            Confidence = 0.95,
        };
    }

    /// <summary>A private bucket in memory.</summary>
    private sealed class MemoryStorage : IFileStorage
    {
        public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

        public async Task PutAsync(StorageBucket bucket, string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            Objects[key] = copy.ToArray();
        }

        public Task<Stream?> OpenReadAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream?>(Objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes, writable: false) : null);

        public Task<bool> ExistsAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Objects.ContainsKey(key));

        public Task DeleteAsync(StorageBucket bucket, string key, CancellationToken cancellationToken = default)
        {
            Objects.Remove(key);
            return Task.CompletedTask;
        }

        public string GetPublicUrl(string key) => throw new NotSupportedException("The private bucket has no public URL.");

        public string? TryGetPublicKey(string url) => null;

        public Task<Uri?> GetSignedReadUrlAsync(string key, TimeSpan lifetime, string? downloadFileName, CancellationToken cancellationToken = default) =>
            Task.FromResult<Uri?>(null);
    }

    /// <summary>A logger that keeps every formatted line, to prove what is not written.</summary>
    private sealed class ListLogger<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
    }
}
