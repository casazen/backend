using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Models;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Events;
using Casazen.Infrastructure.Jobs;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E26/F01/US01/T01 (NF-69, NF-21): the action <c>RequestReview</c> of a routine asks the guest of a direct stay for the review of the booking site, once for
/// each booking whoever asks; a booking that is not to be invited is skipped with its reason and never fails; the ready routine «Richiesta di recensione»
/// fires a day after the check-out on the clock of the house; and <c>ReviewReceived</c> is on the bus when a guest sends a review.
/// </summary>
public class ReviewRequestRoutineTests
{
    private static readonly Guid OrgA = RoutineRig.OrgA;
    private static readonly Guid OrgB = RoutineRig.OrgB;

    // The engine thinks it is 12 November 2026, 09:00 UTC (10:00 in Rome, winter time). A stay that ended on the 11th is on its first day to review.
    private static readonly DateTime Today = new(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CheckOut = Today.AddDays(-1);

    /// <summary>The trigger of the ready routine: a day after the check-out, as the routines page writes it.</summary>
    private const string OneDayAfter = """{"days":1,"hours":0}""";

    private static readonly string RequestReviewActions = RoutineRig.Actions(("RequestReview", "{}"));

    // ─── Seeding ─────────────────────────────────────────────────────────────────

    private static async Task SeedOrgAsync(RoutineRig rig, Guid orgId, string slug)
    {
        await using var db = rig.NewDb();
        db.Orgs.Add(new OrgEntity { Id = orgId, Name = slug, DisplayName = slug, Slug = slug });
        await db.SaveChangesAsync();
    }

    /// <summary>A confirmed stay of the booking site that ended on <paramref name="checkOut"/>, with its guest (an address, unless said otherwise) and its house.</summary>
    private static async Task<(Booking Booking, Guest Guest)> SeedStayAsync(
        RoutineRig rig,
        Guid orgId,
        BookingSource source = BookingSource.Direct,
        BookingStatus status = BookingStatus.Confirmed,
        DateTime? checkOut = null,
        bool anonymized = false,
        bool erasureRequested = false,
        bool deleted = false)
    {
        var guest = await rig.SeedGuestAsync(orgId, deleted: deleted, erasureRequested: erasureRequested, anonymized: anonymized);
        var end = checkOut ?? CheckOut;
        var booking = await rig.SeedBookingAsync(orgId, source, status, guest.Id, checkIn: end.AddDays(-3), checkOut: end);
        return (booking, guest);
    }

    private static async Task ChangeGuestAsync(RoutineRig rig, Guid guestId, Action<Guest> change)
    {
        await using var db = rig.NewDb();
        var guest = await db.Guests.SingleAsync(g => g.Id == guestId);
        change(guest);
        await db.SaveChangesAsync();
    }

    /// <summary>A check-in session of the stay in which the guest chose <paramref name="language"/> on the check-in page.</summary>
    private static async Task SeedCheckInSessionAsync(RoutineRig rig, Booking booking, string language)
    {
        await using var db = rig.NewDb();
        db.GuestCheckInSessions.Add(new GuestCheckInSession
        {
            OrgId = booking.OrgId,
            BookingId = booking.Id,
            TokenHash = new string('a', 64),
            ExpiresAt = RoutineRig.Now.UtcDateTime.AddDays(3),
            Language = language,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>The subject of the invitation in <paramref name="language"/>, from the same template: what the guest is expected to read.</summary>
    private static string InvitationSubject(string language)
    {
        var content = EmailTemplates.ReviewInvite(
            EmailTemplates.ReviewInviteCulture(language),
            "Mario",
            "Casa",
            CheckOut.AddDays(-3),
            CheckOut,
            "https://example.com/review",
            CheckOut);
        return content.Subject;
    }

    private static Task SeedRequestRoutineAsync(RoutineRig rig, bool testMode = false) =>
        rig.SeedRoutineAsync(OrgA, RoutineEventType.AfterCheckout, RequestReviewActions, testMode: testMode, eventConfig: OneDayAfter);

    private static Task TriggerAsync(RoutineRig rig, Booking booking, TimeProvider? clock = null) =>
        rig.TriggerAsync(OrgA, RoutineEventType.AfterCheckout, RoutineRig.PayloadOf(booking), clock: clock);

    private static RoutineActionResult SingleActionOf(RoutineExecution execution) => Assert.Single(RoutineRig.ResultOf(execution).Actions);

    private static async Task AssertNothingWasSentAsync(RoutineRig rig)
    {
        Assert.Empty(rig.Emails.Snapshot());
        await using var db = rig.NewDb();
        Assert.Empty(await db.ReviewInvites.ToListAsync());
    }

    private static async Task AssertSkippedAsync(RoutineRig rig, string reason)
    {
        var execution = Assert.Single(await rig.ExecutionsAsync());
        Assert.Equal(RoutineExecutionOutcomes.Success, execution.Outcome);
        Assert.Equal(new RoutineActionResult(0, RoutineActionType.RequestReview, RoutineActionOutcomes.Skipped, reason), SingleActionOf(execution));
        await AssertNothingWasSentAsync(rig);
    }

    private static async Task AssertOneInvitationAsync(RoutineRig rig, Booking booking, Guest guest, string orgSlug)
    {
        var email = Assert.Single(rig.Emails.Snapshot());
        Assert.Equal(guest.Email, email.To);
        Assert.Equal(EmailTemplates.Names.ReviewInvite, email.Template);
        Assert.Contains($"/book/{orgSlug}/booking/{booking.Id:D}/review?token=", email.Content.HtmlBody, StringComparison.Ordinal);

        await using var db = rig.NewDb();
        var invite = await db.ReviewInvites.SingleAsync();
        Assert.Equal(booking.OrgId, invite.OrgId);
        Assert.Equal(booking.Id, invite.BookingId);
    }

    // ─── The action: once for each booking (data rule) ───────────────────────────

    /// <summary>
    /// Proof (data rule): two routines asking for the same stay, an event for it that comes again and the host's button leave one invitation. The mark of the
    /// booking is the only thing that decides it, so the guest is asked once, whoever asks and however many times the event is delivered.
    /// </summary>
    [Fact]
    public async Task RequestReview_OncePerBooking_Test()
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        var (booking, guest) = await SeedStayAsync(rig, OrgA);
        await SeedRequestRoutineAsync(rig);
        await SeedRequestRoutineAsync(rig);

        await TriggerAsync(rig, booking); // two routines, one event
        await TriggerAsync(rig, booking); // another event for the same stay

        var executions = await rig.ExecutionsAsync();
        Assert.Equal(4, executions.Count);
        Assert.All(executions, e => Assert.Equal(RoutineExecutionOutcomes.Success, e.Outcome));
        var results = executions.Select(SingleActionOf).ToList();
        Assert.Single(results, r => r.Outcome == RoutineActionOutcomes.Done);
        Assert.Equal(3, results.Count(r => r is { Outcome: RoutineActionOutcomes.Skipped, Code: ReviewInviteSkipReasons.AlreadyInvited }));
        await AssertOneInvitationAsync(rig, booking, guest, "org-a");

        // The host's button after a routine is the same mark: refused, and no second email.
        await using var db = rig.NewDb();
        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => rig.Reviews(db).SendInviteAsync(booking.Id, "auth0|host"));
        Assert.Equal("review_invite_already_sent", refused.Code);
        Assert.Single(rig.Emails.Snapshot());
        Assert.Null((await db.ReviewInvites.SingleAsync()).SentByUserId);
    }

    /// <summary>The other way round: the host asked by hand first, so the routine skips the stay as already invited and sends nothing more.</summary>
    [Fact]
    public async Task RequestReview_AfterTheHostAskedByHand_IsSkippedAsAlreadyInvited()
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        var (booking, guest) = await SeedStayAsync(rig, OrgA);
        await SeedRequestRoutineAsync(rig);
        await using (var db = rig.NewDb())
            await rig.Reviews(db).SendInviteAsync(booking.Id, "auth0|host");

        await TriggerAsync(rig, booking);

        var execution = Assert.Single(await rig.ExecutionsAsync());
        Assert.Equal(
            new RoutineActionResult(0, RoutineActionType.RequestReview, RoutineActionOutcomes.Skipped, ReviewInviteSkipReasons.AlreadyInvited),
            SingleActionOf(execution));
        await AssertOneInvitationAsync(rig, booking, guest, "org-a");
        await using var read = rig.NewDb();
        Assert.Equal("auth0|host", (await read.ReviewInvites.SingleAsync()).SentByUserId);
    }

    /// <summary>The invitation of a routine is the one email of the host's button: to the address of the guest, with the signed link of this booking and no other.</summary>
    [Fact]
    public async Task RequestReview_AnEligibleDirectStay_QueuesTheInvitationWithTheSignedLinkOfTheBooking()
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");

        // The link of the invitation expires at the end of the review window, and the key ring checks that against the real clock: this stay ended yesterday
        // by the real clock, so the link it gets is valid whenever the test runs.
        var clock = new FixedTimeProvider(DateTimeOffset.UtcNow);
        var endedYesterday = clock.TodayInRomeAsDateOnly().AddDays(-1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var (booking, guest) = await SeedStayAsync(rig, OrgA, checkOut: endedYesterday);
        await SeedRequestRoutineAsync(rig);

        await TriggerAsync(rig, booking, clock);

        var execution = Assert.Single(await rig.ExecutionsAsync());
        Assert.Equal(RoutineExecutionOutcomes.Success, execution.Outcome);
        Assert.Equal(new RoutineActionResult(0, RoutineActionType.RequestReview, RoutineActionOutcomes.Done, null), SingleActionOf(execution));
        await AssertOneInvitationAsync(rig, booking, guest, "org-a");

        var body = Assert.Single(rig.Emails.Snapshot()).Content.HtmlBody;
        var start = body.IndexOf("token=", StringComparison.Ordinal) + "token=".Length;
        var end = body.IndexOfAny(['"', '\'', '&', '<', ' '], start);
        var token = Uri.UnescapeDataString(body[start..end]);
        Assert.True(rig.Tokens.TryRead(token, out var linked));
        Assert.Equal(booking.Id, linked);
    }

    /// <summary>A stay the host left in any state of a stay that happened (confirmed, checked in, checked out: the one definition of an active stay) is invited.</summary>
    [Theory]
    [InlineData(BookingStatus.Confirmed)]
    [InlineData(BookingStatus.CheckedIn)]
    [InlineData(BookingStatus.CheckedOut)]
    public async Task RequestReview_AStayThatHappened_IsInvited_WhateverStateTheHostLeftItIn(BookingStatus status)
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        var (booking, guest) = await SeedStayAsync(rig, OrgA, status: status);
        await SeedRequestRoutineAsync(rig);

        await TriggerAsync(rig, booking);

        Assert.Equal(RoutineActionOutcomes.Done, SingleActionOf(Assert.Single(await rig.ExecutionsAsync())).Outcome);
        await AssertOneInvitationAsync(rig, booking, guest, "org-a");
    }

    // ─── The host's button keeps its answers (the same core) ─────────────────────

    [Theory]
    [InlineData("portal")]
    [InlineData("pending")]
    [InlineData("window-closed")]
    [InlineData("anonymized")]
    [InlineData("no-address")]
    public async Task HostButton_ABookingThatCannotBeReviewed_IsRefusedWith422_AsBefore(string kind)
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        var (booking, guest) = await SeedStayAsync(
            rig,
            OrgA,
            kind == "portal" ? BookingSource.Airbnb : BookingSource.Direct,
            kind == "pending" ? BookingStatus.Pending : BookingStatus.Confirmed,
            kind == "window-closed" ? Today.AddDays(-15) : null,
            anonymized: kind == "anonymized");
        if (kind == "no-address")
            await ChangeGuestAsync(rig, guest.Id, g => g.Email = string.Empty);

        await using var db = rig.NewDb();
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Reviews(db).SendInviteAsync(booking.Id, "auth0|host"));

        Assert.Equal("review_invite_not_allowed", refused.Code);
        await AssertNothingWasSentAsync(rig);
    }

    [Fact]
    public async Task HostButton_AfterTheGuestReviewed_IsRefusedWith409_AsBefore()
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        var (booking, _) = await SeedStayAsync(rig, OrgA);
        await using (var seed = rig.NewDb())
        {
            seed.Reviews.Add(new Review
            {
                OrgId = OrgA,
                PropertyId = booking.PropertyId,
                BookingId = booking.Id,
                Rating = 4,
                AuthorName = "Mario",
                StayEndedOn = CheckOut,
                PublishedAt = RoutineRig.Now.UtcDateTime,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = rig.NewDb();
        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => rig.Reviews(db).SendInviteAsync(booking.Id, "auth0|host"));

        Assert.Equal("review_already_submitted", refused.Code);
        await AssertNothingWasSentAsync(rig);
    }

    [Fact]
    public async Task HostButton_AnUnknownBooking_IsNotFound_AndTheQueueThatRefuses_IsA422_WithTheMarkTakenBack()
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        var (booking, _) = await SeedStayAsync(rig, OrgA);
        await using var db = rig.NewDb();

        await Assert.ThrowsAsync<NotFoundException>(() => rig.Reviews(db).SendInviteAsync(Guid.NewGuid(), "auth0|host"));
        rig.Emails.Refuse = true;
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Reviews(db).SendInviteAsync(booking.Id, "auth0|host"));

        Assert.Equal("review_invite_not_sent", refused.Code);
        await AssertNothingWasSentAsync(rig);
    }

    // ─── Portal bookings and every other booking that is not to be invited ───────

    /// <summary>
    /// Proof (data rule): a stay that came through a portal is skipped with <c>not_direct</c>: the portal asks its own guests, so nothing is written and nothing
    /// is sent, whatever else is eligible (confirmed, over, with an address). It is not a failure: the execution is a success and nothing is tried again.
    /// </summary>
    [Fact]
    public async Task RequestReview_PortalBookingSkipped_Test()
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        await SeedRequestRoutineAsync(rig);
        var portals = Enum.GetValues<BookingSource>().Where(FiscalCopy.IsOtaBookingSource).ToList();
        Assert.NotEmpty(portals);

        foreach (var portal in portals)
        {
            var (booking, _) = await SeedStayAsync(rig, OrgA, portal);
            await TriggerAsync(rig, booking);
        }

        var executions = await rig.ExecutionsAsync();
        Assert.Equal(portals.Count, executions.Count);
        Assert.All(executions, execution =>
        {
            Assert.Equal(RoutineExecutionOutcomes.Success, execution.Outcome);
            Assert.Equal(
                new RoutineActionResult(0, RoutineActionType.RequestReview, RoutineActionOutcomes.Skipped, ReviewInviteSkipReasons.NotDirect),
                SingleActionOf(execution));
        });
        await AssertNothingWasSentAsync(rig);
    }

    [Theory]
    [InlineData("manual", ReviewInviteSkipReasons.NotDirect)]
    [InlineData("local", ReviewInviteSkipReasons.NotDirect)]
    [InlineData("cancelled", ReviewInviteSkipReasons.NotConfirmed)]
    [InlineData("pending", ReviewInviteSkipReasons.NotConfirmed)]
    [InlineData("no-show", ReviewInviteSkipReasons.NoShow)]
    [InlineData("no-address", ReviewInviteSkipReasons.NoEmail)]
    [InlineData("anonymized", ReviewInviteSkipReasons.NoEmail)]
    [InlineData("erasure-asked", ReviewInviteSkipReasons.NoEmail)]
    [InlineData("guest-deleted", ReviewInviteSkipReasons.NoEmail)]
    [InlineData("already-reviewed", ReviewInviteSkipReasons.AlreadyReviewed)]
    public async Task RequestReview_ABookingThatIsNotToBeInvited_IsSkippedWithItsReason_AndNothingIsWritten(string kind, string reason)
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        await SeedRequestRoutineAsync(rig);
        var source = kind switch
        {
            "manual" => BookingSource.Manual,
            "local" => BookingSource.Local,
            _ => BookingSource.Direct,
        };
        var status = kind switch
        {
            "cancelled" => BookingStatus.Cancelled,
            "pending" => BookingStatus.Pending,
            _ => BookingStatus.Confirmed,
        };
        var (booking, guest) = await SeedStayAsync(
            rig,
            OrgA,
            source,
            status,
            anonymized: kind == "anonymized",
            erasureRequested: kind == "erasure-asked",
            deleted: kind == "guest-deleted");
        switch (kind)
        {
            case "no-show":
                await rig.ChangeBookingAsync(booking.Id, b => b.NoShowAt = RoutineRig.Now.UtcDateTime.AddDays(-2));
                break;
            case "no-address":
                await ChangeGuestAsync(rig, guest.Id, g => g.Email = "  ");
                break;
            case "already-reviewed":
                await using (var db = rig.NewDb())
                {
                    db.Reviews.Add(new Review
                    {
                        OrgId = OrgA,
                        PropertyId = booking.PropertyId,
                        BookingId = booking.Id,
                        Rating = 5,
                        AuthorName = "Mario",
                        StayEndedOn = CheckOut,
                        PublishedAt = RoutineRig.Now.UtcDateTime,
                    });
                    await db.SaveChangesAsync();
                }

                break;
        }

        await TriggerAsync(rig, booking);

        await AssertSkippedAsync(rig, reason);
    }

    /// <summary>The window of the review: the day after the check-out is the first day to ask, the 14th day after it is the last; before and after, the stay is skipped.</summary>
    [Theory]
    [InlineData(-1, ReviewInviteSkipReasons.TooEarly)]
    [InlineData(0, ReviewInviteSkipReasons.TooEarly)]
    [InlineData(1, null)]
    [InlineData(14, null)]
    [InlineData(15, ReviewInviteSkipReasons.TooLate)]
    public async Task RequestReview_AsksFromTheDayAfterTheCheckOutToTheFourteenth_AndSkipsOutsideIt(int daysSinceCheckOut, string? reason)
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        await SeedRequestRoutineAsync(rig);
        var (booking, guest) = await SeedStayAsync(rig, OrgA, checkOut: Today.AddDays(-daysSinceCheckOut));

        await TriggerAsync(rig, booking);

        if (reason is null)
        {
            Assert.Equal(RoutineActionOutcomes.Done, SingleActionOf(Assert.Single(await rig.ExecutionsAsync())).Outcome);
            await AssertOneInvitationAsync(rig, booking, guest, "org-a");
        }
        else
        {
            await AssertSkippedAsync(rig, reason);
        }
    }

    // ─── Failures: what is not a skip ────────────────────────────────────────────

    /// <summary>Proof (permission rule): the booking is read in the org of the routine. The id of a stay of another org is no stay: the action fails and nothing is sent or marked, in either org.</summary>
    [Fact]
    public async Task RequestReview_ABookingOfAnotherOrg_FailsAndSendsNothing()
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        await SeedOrgAsync(rig, OrgB, "org-b");
        var (foreign, _) = await SeedStayAsync(rig, OrgB);
        await SeedRequestRoutineAsync(rig);

        await TriggerAsync(rig, foreign);

        var execution = Assert.Single(await rig.ExecutionsAsync());
        Assert.Equal(RoutineExecutionOutcomes.Failed, execution.Outcome);
        Assert.Equal(
            new RoutineActionResult(0, RoutineActionType.RequestReview, RoutineActionOutcomes.Failed, RoutineCodes.ReviewNeedsBooking),
            SingleActionOf(execution));
        await AssertNothingWasSentAsync(rig);
    }

    [Fact]
    public async Task RequestReview_NoBookingInTheEvent_FailsWithItsCode()
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        await SeedRequestRoutineAsync(rig);

        await rig.TriggerAsync(OrgA, RoutineEventType.AfterCheckout, new { Nothing = "to see" });

        var execution = Assert.Single(await rig.ExecutionsAsync());
        Assert.Equal(RoutineExecutionOutcomes.Failed, execution.Outcome);
        Assert.Equal(RoutineCodes.ReviewNeedsBooking, SingleActionOf(execution).Code);
        await AssertNothingWasSentAsync(rig);
    }

    /// <summary>
    /// The queue does not take the email (the provider is not configured): the action fails with the code of the host's button, never a success nobody confirmed,
    /// and the booking is not marked, so the host can still ask by hand and the guest is not left without the invitation for good.
    /// </summary>
    [Fact]
    public async Task RequestReview_TheQueueDoesNotTakeTheEmail_FailsAndLeavesTheBookingFree()
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        var (booking, guest) = await SeedStayAsync(rig, OrgA);
        await SeedRequestRoutineAsync(rig);
        rig.Emails.Refuse = true;

        await TriggerAsync(rig, booking);

        var execution = Assert.Single(await rig.ExecutionsAsync());
        Assert.Equal(RoutineExecutionOutcomes.Failed, execution.Outcome);
        Assert.Equal(
            new RoutineActionResult(0, RoutineActionType.RequestReview, RoutineActionOutcomes.Failed, RoutineCodes.ReviewInviteNotSent),
            SingleActionOf(execution));
        await AssertNothingWasSentAsync(rig);

        rig.Emails.Refuse = false;
        await using (var db = rig.NewDb())
            await rig.Reviews(db).SendInviteAsync(booking.Id, "auth0|host");
        await AssertOneInvitationAsync(rig, booking, guest, "org-a");
    }

    [Fact]
    public async Task RequestReview_InTestMode_IsSimulated_AndSendsNothing()
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        var (booking, _) = await SeedStayAsync(rig, OrgA);
        await SeedRequestRoutineAsync(rig, testMode: true);

        await TriggerAsync(rig, booking);

        Assert.Equal(RoutineActionOutcomes.Simulated, SingleActionOf(Assert.Single(await rig.ExecutionsAsync())).Outcome);
        await AssertNothingWasSentAsync(rig);
    }

    /// <summary>A skipped action is not a failure: the actions after it still run (a To do for the host, say), and the execution ends in success.</summary>
    [Fact]
    public async Task RequestReview_ASkippedAction_DoesNotStopTheNextOnes()
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        var (booking, _) = await SeedStayAsync(rig, OrgA, BookingSource.Airbnb);
        await rig.SeedRoutineAsync(
            OrgA,
            RoutineEventType.AfterCheckout,
            RoutineRig.Actions(("RequestReview", "{}"), ("CreateActivity", """{"text":"Chiedi la recensione sul portale"}""")),
            eventConfig: OneDayAfter);

        await TriggerAsync(rig, booking);

        var execution = Assert.Single(await rig.ExecutionsAsync());
        Assert.Equal(RoutineExecutionOutcomes.Success, execution.Outcome);
        Assert.Equal(
            [
                new RoutineActionResult(0, RoutineActionType.RequestReview, RoutineActionOutcomes.Skipped, ReviewInviteSkipReasons.NotDirect),
                new RoutineActionResult(1, RoutineActionType.CreateActivity, RoutineActionOutcomes.Done, null),
            ],
            RoutineRig.ResultOf(execution).Actions);
    }

    /// <summary>After a Wait the action reads the booking of the saved payload: one invitation however many jobs resume it, and none for a stay cancelled while it waited.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RequestReview_AfterAWait_AsksOnce_ForAStayThatIsStillOn(bool cancelledMeanwhile)
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        var (booking, guest) = await SeedStayAsync(rig, OrgA);
        await rig.SeedRoutineAsync(
            OrgA,
            RoutineEventType.AfterCheckout,
            RoutineRig.Actions(("Wait", """{"hours":1}"""), ("RequestReview", "{}")),
            eventConfig: OneDayAfter);
        await TriggerAsync(rig, booking);
        var waiting = Assert.Single(await rig.ExecutionsAsync());
        Assert.Empty(rig.Emails.Snapshot());
        if (cancelledMeanwhile)
            await rig.ChangeBookingAsync(booking.Id, b => b.Status = BookingStatus.Cancelled);
        var later = new FixedTimeProvider(RoutineRig.Now.AddHours(1));

        await rig.ResumeAsync(waiting.Id, later);
        await rig.ResumeAsync(waiting.Id, later);

        var ended = await rig.ExecutionAsync(waiting.Id);
        Assert.Equal(RoutineExecutionOutcomes.Success, ended.Outcome);
        var request = RoutineRig.ResultOf(ended).Actions[1];
        if (cancelledMeanwhile)
        {
            Assert.Equal(new RoutineActionResult(1, RoutineActionType.RequestReview, RoutineActionOutcomes.Blocked, RoutineCodes.BookingNotActive), request);
            await AssertNothingWasSentAsync(rig);
        }
        else
        {
            Assert.Equal(RoutineActionOutcomes.Done, request.Outcome);
            await AssertOneInvitationAsync(rig, booking, guest, "org-a");
        }
    }

    [Fact]
    public async Task Simulate_ARoutineThatRequestsAReview_WouldRun_AndSendsNothing()
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        var (booking, _) = await SeedStayAsync(rig, OrgA);
        var routine = await rig.SeedRoutineAsync(OrgA, RoutineEventType.AfterCheckout, RequestReviewActions, eventConfig: OneDayAfter);

        await using var db = rig.NewDb();
        var result = await rig.Service(db).SimulateAsync(OrgA, routine.Id, RoutineRig.PayloadOf(booking));

        Assert.True(result.WouldExecute);
        Assert.Equal([false], result.Actions.Select(a => a.WouldBlock));
        await AssertNothingWasSentAsync(rig);
    }

    // ─── The ready routine «Richiesta di recensione» ─────────────────────────────

    /// <summary>
    /// The ready routine as the routines page saves it (a day after the check-out, the action <c>RequestReview</c>) is a routine the API accepts, active and not in
    /// test mode, and its trigger is the offset of 24 hours the time triggers read.
    /// </summary>
    [Fact]
    public async Task ReadyRoutine_RichiestaDiRecensione_IsARoutineTheApiSaves_ActiveAndNotInTestMode()
    {
        var rig = new RoutineRig();
        await using var db = rig.NewDb();
        var command = new RoutineCommand(
            "Richiesta di recensione",
            "AfterCheckout",
            RoutineRig.Json(OneDayAfter),
            null,
            [new RoutineActionInput("RequestReview", RoutineRig.Json("{}"))],
            false,
            true);

        var saved = await rig.Management(db).CreateAsync(OrgA, command);

        Assert.True(saved.Active);
        Assert.False(saved.TestMode);
        Assert.Equal(RoutineEventType.AfterCheckout, saved.EventType);
        Assert.Equal(24, TimeTrigger.OffsetHours(saved.EventConfig));
    }

    /// <summary>
    /// Acceptance: with the routine active, a day after the check-out at the check-out time of the house (10:00 on the clock of the property) every eligible direct stay
    /// gets one invitation and the others are skipped with a reason. From the time trigger the emitter writes to the routine that runs, through the handler of the bus.
    /// </summary>
    [Fact]
    public async Task ReadyRoutine_ADayAfterTheCheckOut_InvitesTheDirectStay_AndSkipsThePortalOne()
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        await using (var db = rig.NewDb())
        {
            var command = new RoutineCommand(
                "Richiesta di recensione",
                "AfterCheckout",
                RoutineRig.Json(OneDayAfter),
                null,
                [new RoutineActionInput("RequestReview", RoutineRig.Json("{}"))],
                false,
                true);
            await rig.Management(db).CreateAsync(OrgA, command);
        }

        // One house with the usual check-out time, and two stays that end on the 12th: one of the booking site, one of a portal.
        var guest = await rig.SeedGuestAsync(OrgA);
        var checkOutDay = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc);
        var stays = new Dictionary<BookingSource, Booking>();
        await using (var db = rig.NewDb())
        {
            var property = new Property
            {
                OrgId = OrgA,
                OwnerId = "auth0|rig-owner",
                Name = "Casa",
                Address = "Via Roma 1",
                City = "Roma",
                CheckOutBy = new TimeOnly(10, 0),
            };
            db.Properties.Add(property);
            foreach (var source in new[] { BookingSource.Direct, BookingSource.Airbnb })
            {
                var stay = new Booking
                {
                    OrgId = OrgA,
                    PropertyId = property.Id,
                    GuestId = guest.Id,
                    Source = source,
                    Status = BookingStatus.Confirmed,
                    CheckInDate = checkOutDay.AddDays(-3),
                    CheckOutDate = checkOutDay,
                    NumberOfGuests = 2,
                };
                db.Bookings.Add(stay);
                stays[source] = stay;
            }

            await db.SaveChangesAsync();
        }

        // 10:00 in Rome on the 13th is 09:00 UTC: five minutes earlier nothing is due, five minutes later each stay has its trigger.
        await EmitTimeTriggersAsync(rig, new DateTime(2026, 11, 13, 8, 55, 0, DateTimeKind.Utc));
        await using (var read = rig.NewDb())
            Assert.Empty(await read.OutboxEvents.ToListAsync());

        var now = new DateTime(2026, 11, 13, 9, 5, 0, DateTimeKind.Utc);
        await EmitTimeTriggersAsync(rig, now);
        List<OutboxEvent> triggers;
        await using (var db = rig.NewDb())
        {
            triggers = await db.OutboxEvents.ToListAsync();
            Assert.Equal(2, triggers.Count);
            Assert.All(triggers, t => Assert.Contains("\"offset\":24", t.PayloadJson, StringComparison.Ordinal));

            // The dispatcher hands each trigger to the routine engine.
            var clock = new FixedTimeProvider(new DateTimeOffset(now, TimeSpan.Zero));
            var handler = new RoutineEventHandler(rig.Service(db, clock), NullLogger<RoutineEventHandler>.Instance);
            foreach (var trigger in triggers)
                await handler.HandleAsync(DomainEventTypeMap.Create(trigger), CancellationToken.None);
        }

        var executions = await rig.ExecutionsAsync();
        Assert.Equal(2, executions.Count);
        var direct = executions.Single(e => e.EventId == triggers.Single(t => t.EntityId == stays[BookingSource.Direct].Id).Id.ToString("D"));
        var portal = executions.Single(e => e.EventId == triggers.Single(t => t.EntityId == stays[BookingSource.Airbnb].Id).Id.ToString("D"));
        Assert.Equal(RoutineActionOutcomes.Done, SingleActionOf(direct).Outcome);
        Assert.Equal(
            new RoutineActionResult(0, RoutineActionType.RequestReview, RoutineActionOutcomes.Skipped, ReviewInviteSkipReasons.NotDirect),
            SingleActionOf(portal));
        await AssertOneInvitationAsync(rig, stays[BookingSource.Direct], guest, "org-a");
    }

    private static async Task EmitTimeTriggersAsync(RoutineRig rig, DateTime utcNow)
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(utcNow, TimeSpan.Zero));
        await using var db = rig.NewDb();
        var job = new TimeEventEmitterJob(
            db,
            new DomainEventRecorder(db, NullTenantContext.Instance, clock),
            Options.Create(new RoutineOptions()),
            rig.Flags,
            clock,
            NullLogger<TimeEventEmitterJob>.Instance);
        await job.ExecuteAsync(CancellationToken.None);
    }

    // ─── ReviewReceived on the bus ───────────────────────────────────────────────

    private static PublicReviewService PublicReviews(RoutineRig rig, AppDbContext db)
    {
        var clock = new FixedTimeProvider(RoutineRig.Now);
        return new PublicReviewService(
            db,
            rig.Tokens,
            NullLogger<PublicReviewService>.Instance,
            clock,
            new DomainEventRecorder(db, NullTenantContext.Instance, clock));
    }

    /// <summary>
    /// Proof (privacy rule): a review sent by the guest records <c>ReviewReceived</c> once, in the save of the review, and its payload is the ids of the review, the
    /// stay and the house, never the mark, the text or the name; the same link sent again is refused and leaves no second event.
    /// </summary>
    [Fact]
    public async Task ReviewReceived_IsRecordedOnceForEachReview_WithTheIdsOnly_Test()
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        var (booking, _) = await SeedStayAsync(rig, OrgA);
        var token = rig.Tokens.Create(booking.Id, DateTimeOffset.UtcNow.AddDays(1));
        var command = new SubmitReviewCommand(5, "Un soggiorno perfetto, grazie a Mario e alla sua famiglia.", true);

        PublicReview review;
        await using (var db = rig.NewDb())
            review = await PublicReviews(rig, db).SubmitAsync(booking.Id, token, command);
        await using (var db = rig.NewDb())
        {
            var again = await Assert.ThrowsAsync<DomainConflictException>(() => PublicReviews(rig, db).SubmitAsync(booking.Id, token, command));
            Assert.Equal("review_already_submitted", again.Code);
        }

        await using var read = rig.NewDb();
        var published = Assert.Single(await read.OutboxEvents.ToListAsync());
        Assert.Equal(DomainEventType.ReviewReceived, published.EventType);
        Assert.Equal(OrgA, published.OrgId);
        Assert.Equal(review.Id, published.EntityId);
        Assert.Equal(ReviewReceivedPayload.DedupKeyOf(review.Id), published.DedupKey);
        var members = JsonDocument.Parse(published.PayloadJson).RootElement.EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString(), StringComparer.OrdinalIgnoreCase);
        Assert.Equal(["BookingId", "PropertyId", "ReviewId"], members.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(booking.Id.ToString(), members["BookingId"]);
        Assert.Equal(booking.PropertyId.ToString(), members["PropertyId"]);
        Assert.Equal(review.Id.ToString(), members["ReviewId"]);
        Assert.DoesNotContain("Mario", published.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("perfetto", published.PayloadJson, StringComparison.Ordinal);
    }

    /// <summary>A review that is refused (the stay is of a portal, so it cannot be reviewed) records no event and writes no review.</summary>
    [Fact]
    public async Task ReviewReceived_AReviewThatIsRefused_RecordsNoEvent()
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        var (booking, _) = await SeedStayAsync(rig, OrgA, BookingSource.Airbnb);
        var token = rig.Tokens.Create(booking.Id, DateTimeOffset.UtcNow.AddDays(1));

        await using var db = rig.NewDb();
        var refused = await Assert.ThrowsAsync<DomainConflictException>(
            () => PublicReviews(rig, db).SubmitAsync(booking.Id, token, new SubmitReviewCommand(4, null, false)));

        Assert.Equal("review_not_available", refused.Code);
        await using var read = rig.NewDb();
        Assert.Empty(await read.OutboxEvents.ToListAsync());
        Assert.Empty(await read.Reviews.ToListAsync());
    }

    /// <summary>The event reaches the routines of the host: a routine on <c>ReviewReceived</c> finds the stay of the review in the payload and raises its To do for it.</summary>
    [Fact]
    public async Task ReviewReceived_StartsTheRoutinesOfTheOrg_OnTheStayOfTheReview()
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        var (booking, _) = await SeedStayAsync(rig, OrgA);
        await rig.SeedRoutineAsync(
            OrgA,
            RoutineEventType.ReviewReceived,
            RoutineRig.Actions(("CreateActivity", """{"text":"Rispondi alla recensione"}""")));
        var token = rig.Tokens.Create(booking.Id, DateTimeOffset.UtcNow.AddDays(1));
        await using (var db = rig.NewDb())
            await PublicReviews(rig, db).SubmitAsync(booking.Id, token, new SubmitReviewCommand(5, null, true));

        await using (var db = rig.NewDb())
        {
            var row = await db.OutboxEvents.SingleAsync();
            var handler = new RoutineEventHandler(rig.Service(db), NullLogger<RoutineEventHandler>.Instance);
            await handler.HandleAsync(DomainEventTypeMap.Create(row), CancellationToken.None);
        }

        var execution = Assert.Single(await rig.ExecutionsAsync());
        Assert.Equal(RoutineExecutionOutcomes.Success, execution.Outcome);
        await using var read = rig.NewDb();
        var todo = await read.InternalActivities.AsNoTracking().SingleAsync(a => a.ActivityType == nameof(ActivityType.RoutineTask));
        Assert.Equal(booking.Id, todo.EntityId);
    }

    // ─── The language of the invitation: the guest's, chosen as the message with the check-in link does ───

    /// <summary>
    /// The invitation is written in the language of the guest: the one chosen on the check-in page, else the one of the country of the guest, else Italian. It exists in Italian and
    /// English, so every other language reads English. The English one, the choice over the country and the fallback to Italian are each proved here.
    /// </summary>
    [Theory]
    [InlineData("en", "", "en")]
    [InlineData(null, "GB", "en")]
    [InlineData("it", "GB", "it")]
    [InlineData("de", "", "en")]
    [InlineData(null, "DE", "en")]
    [InlineData(null, "", "it")]
    public async Task RequestReview_IsWrittenInTheLanguageOfTheGuest_AndInItalianWhenNoneIsKnown(string? chosen, string country, string written)
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        var (booking, guest) = await SeedStayAsync(rig, OrgA);
        await ChangeGuestAsync(rig, guest.Id, g => g.Country = country);
        if (chosen is not null)
            await SeedCheckInSessionAsync(rig, booking, chosen);
        await SeedRequestRoutineAsync(rig);

        await TriggerAsync(rig, booking);

        Assert.Equal(RoutineActionOutcomes.Done, SingleActionOf(Assert.Single(await rig.ExecutionsAsync())).Outcome);
        Assert.NotEqual(InvitationSubject("it"), InvitationSubject("en"));
        Assert.Equal(InvitationSubject(written), Assert.Single(rig.Emails.Snapshot()).Content.Subject);
    }

    /// <summary>The core is the same, so the host's button writes in the language of the guest too (with the answers and the order of the checks it always had).</summary>
    [Fact]
    public async Task HostButton_WritesInTheLanguageOfTheGuest_Too()
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        var (booking, guest) = await SeedStayAsync(rig, OrgA);
        await ChangeGuestAsync(rig, guest.Id, g => g.Country = "GB");

        await using (var db = rig.NewDb())
            await rig.Reviews(db).SendInviteAsync(booking.Id, "auth0|host");

        Assert.Equal(InvitationSubject("en"), Assert.Single(rig.Emails.Snapshot()).Content.Subject);
    }

    /// <summary>
    /// E50/F01/US03/T02, with the guest portal on: the invitation is written in any of the five languages of the guest (the check-in page, a country whose language is beyond doubt, else the
    /// account of the host, else Italian). A guest from another country is not turned into English: Switzerland, where four languages are spoken, is the language of the host's account.
    /// With the portal off it is the invitation of the tests above, in Italian or English.
    /// </summary>
    [Theory]
    [InlineData("", "de", "de")]
    [InlineData("CH", "de", "de")]
    [InlineData("CH", "it", "it")]
    [InlineData("US", "it", "en")]
    [InlineData("DE", "it", "de")]
    [InlineData("FR", "en", "fr")]
    public async Task HostButton_PortalOn_WritesInTheLanguageOfTheGuestOrOfTheHostsAccount(string country, string accountLanguage, string written)
    {
        var rig = new RoutineRig();
        await SeedOrgAsync(rig, OrgA, "org-a");
        var (booking, guest) = await SeedStayAsync(rig, OrgA);
        await ChangeGuestAsync(rig, guest.Id, g => g.Country = country);
        await using (var seed = rig.NewDb())
        {
            (await seed.Orgs.SingleAsync(o => o.Id == OrgA)).DefaultLanguage = accountLanguage;
            await seed.SaveChangesAsync();
        }

        var flags = new TestFeatureFlags(Casazen.Core.Features.FeatureFlags.GuestPortal);
        await using (var db = rig.NewDb())
        {
            var reviews = new HostReviewService(
                db,
                rig.Tokens,
                rig.Emails,
                Casazen.Tests.Unit.Email.EmailTestHelpers.Links(),
                NullLogger<HostReviewService>.Instance,
                new FixedTimeProvider(RoutineRig.Now),
                emailBranding: new Casazen.Infrastructure.Email.GuestEmailBrandingProvider(db, flags, NullLogger<Casazen.Infrastructure.Email.GuestEmailBrandingProvider>.Instance),
                emailLanguage: new Casazen.Infrastructure.Email.GuestEmailLanguageProvider(db, flags, NullLogger<Casazen.Infrastructure.Email.GuestEmailLanguageProvider>.Instance));

            await reviews.SendInviteAsync(booking.Id, "auth0|host");
        }

        var sent = Assert.Single(rig.Emails.Snapshot()).Content;
        var expected = EmailTemplates.ReviewInvite(GuestLanguages.CultureOf(written), "Mario", "Casa", CheckOut.AddDays(-3), CheckOut, "https://example.com/review", CheckOut);
        Assert.Equal(expected.Subject, sent.Subject);
        Assert.Contains($"<html lang=\"{written}\">", sent.HtmlBody, StringComparison.Ordinal);
    }
}
