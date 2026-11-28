using Casazen.Core.Bookings;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Jobs;
using Xunit;
using AutoSendOutcome = Casazen.Infrastructure.Octorate.StayGuestsAutoSendOutcome;
using World = Casazen.Tests.Unit.Operations.OperationsScenarioBuilder;

namespace Casazen.Tests.Unit.Operations;

/// <summary>
/// E28/F01/US01/T01 (NF-41, NF-119, NF-120, NF-121, NF-122, NF-123; the operations steps «O» of <c>pilota-flusso-completo-2026-11-14.md</c>): the end-to-end proof of the
/// operations of the agency of Como for the four ways a stay is booked, one test for each origin. Each test is the stay from the booking to the day after the departure on the
/// real code (see <see cref="OperationsScenarioBuilder"/>: the application's own registrations, the channel manager, Stripe and the AI planner of the routines replaced, the clock the scenario's), with every
/// flag of the operations steps and every organization setting on, and asserts <b>states</b>, never texts: the state of the delivery of the link, the status of the stay, the rows
/// the steps leave, what the channel manager and Stripe were asked. A link of the chain that breaks (a handler nobody registered, a rule that stopped giving the stay to the
/// next step) fails the test at the step that needed it.
/// </summary>
/// <remarks>
/// The trait <c>Scenario</c> of each test lists the steps of the scenario it covers (A and B of <c>pilota-flusso-completo-2026-11-14.md</c>; <c>--filter Scenario~B7</c> selects
/// them). No test has a display name of its own: the planner of the test shards of the CI (<c>scripts/ci/test_shards.py</c>) reads the default name,
/// <c>Namespace.Class.Method</c>. The tests run on the in-memory database (no server): the chain shares its services with the unit classes, and the queries of every step have their
/// translation proofs on PostgreSQL in the tests of the slice that wrote them. The days are the days of Rome: the stay is from 4 to 7 December 2026 (three nights, winter time).
/// </remarks>
public class OperationsChainTests
{
    [Fact]
    [Trait("Scenario", "A7 B1 B2 B3 B5 B7 B9 B10 B11")]
    public async Task OperationsChain_PortalOrigin_Test()
    {
        await using var world = await World.CreateAsync();
        world.TheChatIsNotLicensed();

        world.At(World.InRome(World.Day(11, 20), 9));
        var bookingId = await world.ImportPortalBookingAsync(world.PortalReservation(World.CheckInDay, World.CheckOutDay));
        await world.SeedPortalConversationAsync(bookingId);
        await world.DeliverEventsAsync();
        var booking = await world.BookingAsync(bookingId);
        Assert.Equal((BookingSource.BookingCom, BookingStatus.Confirmed, BookingOrigin.Portal), (booking.Source, booking.Status, BookingOrigins.Of(booking)));
        Assert.True(string.IsNullOrWhiteSpace(booking.Guest.Email));

        world.At(World.InRome(World.Day(12, 1), 9));
        await world.RunTheDailyCheckInJobAsync();
        var waiting = await world.DeliveryOfAsync(bookingId);
        Assert.Equal(
            (CheckInLinkChannel.PortalChat, CheckInLinkDeliveryState.WaitingChatPermission, OtaMessageSyncErrorCodes.ChatNotLicensed),
            (waiting.Channel, waiting.State, waiting.LastErrorCode));
        Assert.Empty(world.ChatMessages);
        var todo = Assert.Single(await world.ActivitiesOfAsync(bookingId), a => a.ActivityType == nameof(ActivityType.CheckInLinkToSend));
        Assert.Null(todo.CompletedAt);

        world.TheChatTakesMessages();
        world.At(World.InRome(World.Day(12, 2), 10));
        await world.TheChatSyncReadsTheListingAsync();
        await world.RunTheLinkRetryAsync();
        var sent = await world.DeliveryOfAsync(bookingId);
        Assert.Equal((CheckInLinkChannel.PortalChat, CheckInLinkDeliveryState.Sent), (sent.Channel, sent.State));
        Assert.Null(sent.LinkToken);
        var token = World.TokenIn(Assert.Single(world.ChatMessages));
        Assert.NotNull(Assert.Single(await world.ActivitiesOfAsync(bookingId), a => a.ActivityType == nameof(ActivityType.CheckInLinkToSend)).CompletedAt);
        world.At(World.InRome(World.Day(12, 2), 12));
        await world.RunTheLinkRetryAsync();
        Assert.Single(world.ChatMessages);

        var submit = await world.GuestChecksInAsync(
            token,
            World.Head(),
            World.Member("Rita", "Rossi", new DateTime(1987, 5, 10, 0, 0, 0, DateTimeKind.Utc), Gender.Female));
        Assert.True(submit.Success, string.Join(", ", submit.ValidationErrors.Select(e => $"{e.Index}:{e.Field}:{e.MessageKey}")));
        Assert.Equal(CheckInLinkEndReasons.CheckInCompleted, (await world.DeliveryOfAsync(bookingId)).EndedReason);

        await world.DeliverEventsAsync();
        Assert.Equal([0, 1], world.GuestsWritten.Select(g => g.Position));
        Assert.All(world.GuestsWritten, g => Assert.Equal((false, "COMO", "COMO"), (g.Draft, g.BirthCity, g.ResidenceCity)));

        world.At(World.InRome(World.CheckInDay, 14));
        var entrusted = await world.StatusOfAsync(bookingId);
        Assert.Equal((AlloggiatiWebStatus.EntrustedToChannelManager, ComplianceSenderType.ChannelManager), (entrusted.Status, entrusted.Sender));
        Assert.Equal((ComplianceLight.Yellow, ComplianceReasonCodes.EntrustedToChannelManager), (entrusted.Verdict!.Verdict.Light, entrusted.Verdict.Verdict.Reason));
        Assert.Null(entrusted.ReceiptProof);

        var image = await world.StoreDocumentImageAsync(bookingId);
        Assert.Equal(new GuestDocumentPurgeResult(true, 0, 0, 0), await world.PurgeAsync());
        Assert.Contains(image, world.Storage.Keys);
        var anotherDay = await world.UploadReceiptAsync(World.CheckInDay.AddDays(-1));
        Assert.Equal(0, anotherDay.CoveredStays);
        Assert.Equal(new GuestDocumentPurgeResult(true, 0, 0, 0), await world.PurgeAsync());
        Assert.Contains(image, world.Storage.Keys);
        Assert.Single(await world.DocumentImagesOfAsync(bookingId));
        Assert.Empty(await world.DocumentDeletionsOfAsync(bookingId));

        var queuedRuns = PurgeRunsQueued(world);
        var receipt = await world.UploadReceiptAsync(World.CheckInDay);
        Assert.Equal(1, receipt.CoveredStays);
        var green = await world.StatusOfAsync(bookingId);
        Assert.Equal((ComplianceLight.Green, ComplianceReasonCodes.ReceiptArchived), (green.Verdict!.Verdict.Light, green.Verdict.Verdict.Reason));
        Assert.Equal(queuedRuns + 1, PurgeRunsQueued(world));
        var purged = await world.PurgeAsync();
        Assert.Equal((true, 1, 1), (purged.Enabled, purged.Stays, purged.Images));
        Assert.DoesNotContain(image, world.Storage.Keys);
        Assert.Empty(await world.DocumentImagesOfAsync(bookingId));
        var deletion = Assert.Single(await world.DocumentDeletionsOfAsync(bookingId));
        Assert.Equal((world.OrgId, world.PropertyId), (deletion.OrgId, deletion.PropertyId));
        var kept = await world.StayGuestsOfAsync(bookingId);
        Assert.Equal([("Mario", "Rossi"), ("Rita", "Rossi")], kept.Select(g => (g.FirstName, g.LastName)));
        Assert.Equal("YA1234567", kept[0].DocumentNumber);
        Assert.Equal(new GuestDocumentPurgeResult(true, 0, 0, 0), await world.PurgeAsync());

        Assert.Null(await world.StatusAsAnotherOrganizationAsync(bookingId));
        await Assert.ThrowsAsync<NotFoundException>(() => world.UploadReceiptAsAnotherOrganizationAsync(World.CheckInDay));

        // B7: the portal collects the tourist tax of its own stays: nothing is payable online, and Stripe is never asked for it.
        var tax = await world.TouristTaxOfAsync(token);
        Assert.Equal((TouristTaxCollectionStatus.CollectedByChannel, TouristTaxNotPayableReason.ChannelCollects, 0m), (tax!.Status, tax.Reason, tax.Payable));
        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => world.StartTouristTaxPaymentAsync(token));
        Assert.Equal(TouristTaxCharges.NothingToPayCode, refused.Code);
        world.TheTaxWasNeverCharged();
        Assert.Empty(await world.PaymentsOfAsync(bookingId));

        await world.RunTheTimeTriggersAsync(World.InRome(World.CheckOutDay, 10, 5));
        var cleaning = Assert.Single(await world.CleaningsOfAsync(bookingId));
        Assert.Equal(PropertyCleanerKind.Staff, cleaning.CleanerKind);
        var work = Assert.Single(await world.ActivitiesOfAsync(bookingId), a => a.ActivityType == InternalActivity.CleaningActivityType);
        Assert.Equal((World.StaffUser, 40.00m, true), (work.StaffUserId, work.InternalRate, work.RechargeToOwner));
        await world.RunTheTimeTriggersAsync(World.InRome(World.CheckOutDay, 10, 20));
        Assert.Single(await world.CleaningsOfAsync(bookingId));

        await world.RunTheTimeTriggersAsync(World.InRome(World.CheckOutDay.AddDays(1), 10, 5));
        var run = Assert.Single(await world.ExecutionsOfAsync("Richiesta di recensione"));
        Assert.Equal(RoutineExecutionOutcomes.Success, run.Outcome);
        Assert.Equal(
            new RoutineActionResult(0, RoutineActionType.RequestReview, RoutineActionOutcomes.Skipped, ReviewInviteSkipReasons.NotDirect),
            Assert.Single(RoutineExecutionResult.Parse(run.ResultJson).Actions));
        Assert.Empty(await world.ReviewInvitesAsync());
        Assert.DoesNotContain(world.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.ReviewInvite);
    }

    [Fact]
    [Trait("Scenario", "A11 B1 B2 B3 B7")]
    public async Task OperationsChain_AgencySiteOrigin_Test()
    {
        await using var world = await World.CreateAsync();

        world.At(World.InRome(World.Day(12, 2), 9));
        var created = await world.BookOnTheSiteAsync(
            World.CheckInDay,
            World.CheckOutDay,
            adults: 2,
            attribution: new BookingAttributionInput("Agency-Site", "link", "estate-2026", "https://www.agenzia-como.it/case?x=1"));
        var bookingId = created.BookingId;
        var held = await world.BookingAsync(bookingId);
        Assert.Equal((BookingSource.Direct, BookingStatus.Pending), (held.Source, held.Status));
        Assert.Equal(
            ("agency-site", "link", "estate-2026", "www.agenzia-como.it"),
            (held.AttributionSource, held.AttributionMedium, held.AttributionCampaign, held.AttributionReferrerHost));
        Assert.Equal(BookingOrigin.AgencySite, BookingOrigins.Of(held));

        Assert.Equal((500.00m, 12.00m, 512.00m), (created.BasePrice, created.TouristTaxAmount, created.Amount));

        await world.PayOnTheSiteAsync(bookingId);
        await world.DeliverEventsAsync();
        Assert.Equal(BookingStatus.Confirmed, (await world.BookingAsync(bookingId)).Status);
        var payment = Assert.Single(await world.PaymentsOfAsync(bookingId));
        Assert.Equal((PaymentStatus.Completed, 512.00m), (payment.Status, payment.Amount));
        var delivery = await world.DeliveryOfAsync(bookingId);
        Assert.Equal(CheckInLinkChannel.Email, delivery.Channel);
        Assert.Contains(delivery.State, new[] { CheckInLinkDeliveryState.Sent, CheckInLinkDeliveryState.Sending });

        var token = world.TokenOfTheConfirmationEmail();
        var submit = await world.GuestChecksInAsync(
            token,
            World.Head(),
            World.Member("Rita", "Rossi", new DateTime(1987, 5, 10, 0, 0, 0, DateTimeKind.Utc), Gender.Female));
        Assert.True(submit.Success, string.Join(", ", submit.ValidationErrors.Select(e => $"{e.Index}:{e.Field}:{e.MessageKey}")));
        await world.DeliverEventsAsync();
        Assert.Equal(12.00m, (await world.ConfirmTouristTaxAsync(bookingId)).AmountDue);

        var view = await world.TouristTaxOfAsync(token);
        Assert.Equal(0m, view!.Payable);
        var host = await world.TouristTaxForHostAsync(bookingId);
        Assert.Equal((12.00m, 0m, 0m, 0m), (host.IncludedInBooking, host.OnlinePayable, host.RefundDue, host.CollectedAmount));
        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => world.StartTouristTaxPaymentAsync(token));
        Assert.Equal(TouristTaxCharges.NothingToPayCode, refused.Code);
        world.TheTaxWasNeverCharged();
        Assert.Single(await world.PaymentsOfAsync(bookingId));
    }

    [Fact]
    [Trait("Scenario", "B1 B2 B3 B7 B9 B10 B11")]
    public async Task OperationsChain_CasaZenSiteOrigin_Test()
    {
        await using var world = await World.CreateAsync();
        await world.AddTheCheckInMessageRoutineAsync();

        world.At(World.InRome(World.Day(11, 25), 9));
        var created = await world.BookOnTheSiteAsync(World.CheckInDay, World.CheckOutDay, adults: 2, children: 1, childrenAges: [10]);
        var bookingId = created.BookingId;
        Assert.Equal(BookingOrigin.CasaZenSite, BookingOrigins.Of(await world.BookingAsync(bookingId)));
        Assert.Equal(12.00m, created.TouristTaxAmount);
        await world.PayOnTheSiteAsync(bookingId);
        await world.DeliverEventsAsync();
        Assert.Equal(BookingStatus.Confirmed, (await world.BookingAsync(bookingId)).Status);

        var planned = Assert.Single(await world.ScheduledMessagesOfAsync(bookingId));
        Assert.Equal(ScheduledMessageStatus.Scheduled, planned.Status);
        Assert.Equal(World.InRome(World.CheckInDay.AddDays(-1), 15).UtcDateTime, planned.PlannedAtUtc);
        Assert.Empty(world.DirectThread.Automatic);

        await world.RunTheTimeTriggersAsync(World.InRome(World.CheckInDay.AddDays(-1), 15, 5));
        var message = Assert.Single(world.DirectThread.Automatic);
        Assert.Equal(bookingId, message.BookingId);
        var token = World.TokenIn(message.Text);
        var sentMessage = Assert.Single(await world.ScheduledMessagesOfAsync(bookingId));
        Assert.Equal((ScheduledMessageStatus.Sent, $"direct:{bookingId:D}"), (sentMessage.Status, sentMessage.SentThreadKey));
        Assert.Equal(GuestCheckInSessionStatus.Inviato, (await world.SessionOfAsync(bookingId)).Status);
        await world.RunTheTimeTriggersAsync(World.InRome(World.CheckInDay.AddDays(-1), 15, 20));
        Assert.Single(world.DirectThread.Automatic);

        var submit = await world.GuestChecksInAsync(
            token,
            World.Head(),
            World.Member("Rita", "Rossi", new DateTime(1987, 5, 10, 0, 0, 0, DateTimeKind.Utc), Gender.Female),
            World.Member("Luca", "Rossi", new DateTime(2011, 1, 15, 0, 0, 0, DateTimeKind.Utc), Gender.Male));
        Assert.True(submit.Success, string.Join(", ", submit.ValidationErrors.Select(e => $"{e.Index}:{e.Field}:{e.MessageKey}")));
        await world.DeliverEventsAsync();

        Assert.Equal(18.00m, (await world.ConfirmTouristTaxAsync(bookingId)).AmountDue);
        var due = await world.TouristTaxOfAsync(token);
        Assert.Equal((TouristTaxCollectionStatus.ToCollect, 6.00m), (due!.Status, due.Payable));
        var session = await world.StartTouristTaxPaymentAsync(token);
        Assert.Equal((6.00m, "EUR"), (session!.Amount, session.Currency));
        await world.TouristTaxPaidAsync(session, "evt_tax_casazen_1");
        var record = await world.TouristTaxRecordOfAsync(bookingId);
        Assert.Equal((TouristTaxCollectionStatus.PaidOnline, 6.00m, "EUR"), (record!.CollectionStatus, record.CollectedAmount, record.Currency));
        Assert.Contains(world.Pushes.Queued, p => p.Payload.Type == PushTypes.TouristTaxPaid && p.Payload.BookingId == bookingId);
        Assert.Single(await world.PaymentsOfAsync(bookingId));

        await world.RunTheTimeTriggersAsync(World.InRome(World.CheckOutDay.AddDays(1), 10, 5));
        var invite = Assert.Single(await world.ReviewInvitesAsync());
        Assert.Equal(bookingId, invite.BookingId);
        var inviteEmail = Assert.Single(world.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.ReviewInvite);
        Assert.Equal(World.GuestEmail, inviteEmail.To);
        await world.RunTheTimeTriggersAsync(World.InRome(World.CheckOutDay.AddDays(1), 10, 20));
        Assert.Single(await world.ReviewInvitesAsync());
        Assert.Single(world.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.ReviewInvite);
    }

    // Origine 4, prenotazione a mano: il link per e-mail; la tassa si paga online per intero; la pulizia dello staff con il suo costo nella lettura per la fatturazione.
    [Fact]
    [Trait("Scenario", "B1 B2 B3 B7 B10")]
    public async Task OperationsChain_ManualOrigin_Test()
    {
        await using var world = await World.CreateAsync();

        world.At(World.InRome(World.Day(12, 2), 9));
        var bookingId = await world.EnterByHandAsync(World.CheckInDay, World.CheckOutDay, adults: 2, totalPrice: 400m);
        await world.DeliverEventsAsync();
        var booking = await world.BookingAsync(bookingId);
        Assert.Equal((BookingSource.Manual, BookingStatus.Confirmed, BookingOrigin.Manual), (booking.Source, booking.Status, BookingOrigins.Of(booking)));
        Assert.Empty(await world.PaymentsOfAsync(bookingId));

        await world.RunTheDailyCheckInJobAsync();
        Assert.Equal(GuestCheckInLinkEmailStatus.Queued, (await world.SessionOfAsync(bookingId)).LinkEmailStatus);
        var token = world.TokenOfTheQueuedLinkEmail();

        var submit = await world.GuestChecksInAsync(
            token,
            World.Head(),
            World.Member("Rita", "Rossi", new DateTime(1987, 5, 10, 0, 0, 0, DateTimeKind.Utc), Gender.Female));
        Assert.True(submit.Success, string.Join(", ", submit.ValidationErrors.Select(e => $"{e.Index}:{e.Field}:{e.MessageKey}")));
        await world.DeliverEventsAsync();
        Assert.Equal(12.00m, (await world.ConfirmTouristTaxAsync(bookingId)).AmountDue);

        var due = await world.TouristTaxOfAsync(token);
        Assert.Equal((TouristTaxCollectionStatus.ToCollect, 12.00m), (due!.Status, due.Payable));
        var session = await world.StartTouristTaxPaymentAsync(token);
        Assert.Equal(12.00m, session!.Amount);
        await world.TouristTaxPaidAsync(session, "evt_tax_manual_1");
        var record = await world.TouristTaxRecordOfAsync(bookingId);
        Assert.Equal((TouristTaxCollectionStatus.PaidOnline, 12.00m), (record!.CollectionStatus, record.CollectedAmount));
        Assert.Empty(await world.PaymentsOfAsync(bookingId));

        await world.RunTheTimeTriggersAsync(World.InRome(World.CheckOutDay, 10, 5));
        var work = Assert.Single(await world.ActivitiesOfAsync(bookingId), a => a.ActivityType == InternalActivity.CleaningActivityType);
        Assert.Empty(await world.CompletedCleaningsAsync(World.Day(12, 1), World.Day(12, 31)));
        world.At(World.InRome(World.CheckOutDay, 12));
        await world.CompleteTheCleaningAsync(work.Id);
        var cost = Assert.Single(await world.CompletedCleaningsAsync(World.Day(12, 1), World.Day(12, 31)));
        Assert.Equal(
            (world.PropertyId, bookingId, World.CleaningPriceCents, "EUR", CleaningKind.Staff, true, CleaningSourceKind.InternalActivity, work.Id),
            (cost.PropertyId, cost.BookingId, cost.AmountCents, cost.Currency, cost.Kind, cost.RechargeToOwner, cost.SourceKind, cost.SourceId));
    }

    // Regola di denaro: la tassa di soggiorno non si incassa due volte, qualunque sia l'origine: compresa nel pagamento (sito dell'agenzia), solo la differenza (sito di CasaZen), per intero (a mano), mai dal portale.
    [Fact]
    [Trait("Scenario", "B7")]
    public async Task OperationsChain_NoDoubleTouristTax_Test()
    {
        await using var world = await World.CreateAsync();
        var adult = new DateTime(1987, 5, 10, 0, 0, 0, DateTimeKind.Utc);

        world.At(World.InRome(World.Day(12, 2), 9));
        var created = await world.BookOnTheSiteAsync(World.CheckInDay, World.CheckOutDay, adults: 2, children: 1, childrenAges: [10]);
        await world.PayOnTheSiteAsync(created.BookingId);
        await world.DeliverEventsAsync();
        var token = world.TokenOfTheConfirmationEmail();
        var submit = await world.GuestChecksInAsync(
            token,
            World.Head(),
            World.Member("Rita", "Rossi", adult, Gender.Female),
            World.Member("Luca", "Rossi", new DateTime(2011, 1, 15, 0, 0, 0, DateTimeKind.Utc), Gender.Male));
        Assert.True(submit.Success, string.Join(", ", submit.ValidationErrors.Select(e => $"{e.Index}:{e.Field}:{e.MessageKey}")));
        await world.DeliverEventsAsync();
        await world.ConfirmTouristTaxAsync(created.BookingId);
        var session = await world.StartTouristTaxPaymentAsync(token);
        Assert.Equal(6.00m, session!.Amount);

        var reopened = await world.StartTouristTaxPaymentAsync(token);
        Assert.Equal(session.PaymentIntentId, reopened!.PaymentIntentId);
        world.TheTaxWasChargedOnce();

        await world.TouristTaxPaidAsync(session, "evt_tax_once");
        await world.TouristTaxPaidAsync(session, "evt_tax_once");
        await world.TouristTaxPaidAsync(session, "evt_tax_other_endpoint", WebhookSource.Platform);
        var record = await world.TouristTaxRecordOfAsync(created.BookingId);
        Assert.Equal((TouristTaxCollectionStatus.PaidOnline, 6.00m), (record!.CollectionStatus, record.CollectedAmount));
        Assert.Single(world.Pushes.Queued, p => p.Payload.Type == PushTypes.TouristTaxPaid);

        var again = await Assert.ThrowsAsync<DomainConflictException>(() => world.StartTouristTaxPaymentAsync(token));
        Assert.Contains(again.Code, new[] { TouristTaxCharges.NothingToPayCode, TouristTaxCharges.PaidOnlineCode });
        await world.ConfirmTouristTaxAsync(created.BookingId);
        var after = await world.TouristTaxForHostAsync(created.BookingId);
        Assert.Equal((6.00m, 12.00m, 0m), (after.CollectedAmount, after.IncludedInBooking, after.OnlinePayable));

        // The money of the tax is never a payment of the booking: its only payment is the one of the checkout. Stripe was asked for one payment intent of tax in all.
        var payment = Assert.Single(await world.PaymentsOfAsync(created.BookingId));
        Assert.Equal(512.00m, payment.Amount);
        world.TheTaxWasChargedOnce();
    }

    [Fact]
    [Trait("Scenario", "B3 B5")]
    public async Task OperationsChain_IdentityBeforeTheGuestsReachTheChannelManager_Test()
    {
        await using var world = await World.CreateAsync(FeatureFlags.CheckInIdentityVerification);
        world.At(World.InRome(World.Day(11, 20), 9));
        var bookingId = await world.ImportPortalBookingAsync(world.PortalReservation(World.CheckInDay, World.CheckOutDay));
        await world.DeliverEventsAsync();

        world.At(World.InRome(World.Day(12, 2), 9));
        var token = await world.HostIssuesTheLinkAsync(bookingId);
        var submit = await world.GuestChecksInAsync(
            token,
            World.Head(),
            World.Member("Rita", "Rossi", new DateTime(1987, 5, 10, 0, 0, 0, DateTimeKind.Utc), Gender.Female));
        Assert.True(submit.Success, string.Join(", ", submit.ValidationErrors.Select(e => $"{e.Index}:{e.Field}:{e.MessageKey}")));

        // B3: the data is complete, but nobody verified who the guest is: nothing leaves, whether no verification exists or the step has escalated to the operator. The consumer of the events
        // answers the registration of the guests; the escalation records no event, so the stay is offered to the channel manager again, as the retry and the sweep do, and says why it stays.
        await world.DeliverEventsAsync();
        Assert.Empty(world.GuestsWritten);
        Assert.Equal(AutoSendOutcome.IdentityNotVerified, await world.TheGuestsAreOfferedAsync(bookingId));
        await world.TheIdentityWaitsForTheOperatorAsync(bookingId);
        await world.DeliverEventsAsync();
        Assert.Equal(AutoSendOutcome.IdentityNotVerified, await world.TheGuestsAreOfferedAsync(bookingId));
        Assert.Empty(world.GuestsWritten);

        world.At(World.InRome(World.Day(12, 2), 10));
        Assert.True(await world.TheOperatorAcceptsAsync(bookingId));
        await world.DeliverEventsAsync();
        Assert.Equal([0, 1], world.GuestsWritten.Select(g => g.Position));
        Assert.All(world.GuestsWritten, g => Assert.False(g.Draft));
        world.At(World.InRome(World.Day(12, 2), 12));
        await world.DeliverEventsAsync();
        Assert.Equal(2, world.GuestsWritten.Count);
    }

    [Fact]
    [Trait("Scenario", "B3 B5")]
    public async Task OperationsChain_IdentityRejectedByTheOperator_Test()
    {
        await using var world = await World.CreateAsync(FeatureFlags.CheckInIdentityVerification);
        world.At(World.InRome(World.Day(11, 20), 9));
        var bookingId = await world.ImportPortalBookingAsync(world.PortalReservation(World.CheckInDay, World.CheckOutDay));
        await world.DeliverEventsAsync();

        world.At(World.InRome(World.Day(12, 2), 9));
        var token = await world.HostIssuesTheLinkAsync(bookingId);
        var submit = await world.GuestChecksInAsync(
            token,
            World.Head(),
            World.Member("Rita", "Rossi", new DateTime(1987, 5, 10, 0, 0, 0, DateTimeKind.Utc), Gender.Female));
        Assert.True(submit.Success, string.Join(", ", submit.ValidationErrors.Select(e => $"{e.Index}:{e.Field}:{e.MessageKey}")));
        await world.DeliverEventsAsync();
        await world.TheIdentityWaitsForTheOperatorAsync(bookingId);

        // The operator looks at the stay and refuses: the latest row of the log is a rejection, which is no pass, so the data of the guests stays where it is, however the stay is offered to the
        // channel manager again (an event, the retry, the sweep).
        world.At(World.InRome(World.Day(12, 2), 10));
        Assert.True(await world.TheOperatorRejectsAsync(bookingId));
        await world.DeliverEventsAsync();
        Assert.Equal(AutoSendOutcome.IdentityNotVerified, await world.TheGuestsAreOfferedAsync(bookingId));
        Assert.Empty(world.GuestsWritten);
    }

    // ISTAT tramite il gestore delle camere: la regione dell'appartamento, «da verificare» finche' lo staff non l'ha verificata sulla fonte; poi la copertura, il passo successivo e la prova del mese in archivio.
    [Fact]
    [Trait("Scenario", "A8 B6")]
    public async Task OperationsChain_IstatRegionOfTheFlat_Test()
    {
        await using var world = await World.CreateAsync();
        await world.SeedTheIstatTableAsync();

        // A8: the flat is in Lombardia, with its row as the runbook gives it: its coverage is «to verify», never claimed, and the host is told to ask the support. CasaZen sends
        // nothing to ISTAT: the sender is the channel manager.
        var unknown = await world.IstatCoverageAsync();
        Assert.Equal((true, true, IstatCoverage.Unknown, IstatNextStep.AskSupport), (unknown.Applies, unknown.RegionKnown, unknown.Coverage, unknown.NextStep));
        Assert.Equal(ComplianceSenderType.ChannelManager, unknown.IstatSender);

        // The staff verifies the region on its source: the channel manager covers it, so the host connects ISTAT in the panel.
        await world.StaffVerifiesTheRegionAsync(unknown, IstatCoverage.Covered);
        var covered = await world.IstatCoverageAsync();
        Assert.Equal((IstatCoverage.Covered, IstatNextStep.ConnectInPanel), (covered.Coverage, covered.NextStep));
        Assert.Equal(unknown.RegionIstatCode, covered.RegionIstatCode);

        // B6: the host files the proof of the ISTAT communication of December: it is kept five years, for the flat and the month, and the archive lists it.
        world.At(World.InRome(World.Day(12, 31), 18));
        var proof = await world.UploadIstatProofAsync(World.Day(12, 31));
        Assert.Equal((ManualProofKind.IstatProof, world.PropertyId, "2026-12"), (proof.Kind, proof.PropertyId, proof.Period));
        Assert.Equal(proof.Id, Assert.Single((await world.ProofsOfAsync(ManualProofKind.IstatProof)).Items).Id);
    }

    [Fact]
    [Trait("Scenario", "B9 B10 B11")]
    public async Task OperationsChain_RoutinesNeedAnAiProvider_Test()
    {
        await using var world = await World.CreateWithoutAnAiProviderAsync();

        world.At(World.InRome(World.Day(12, 2), 9));
        var bookingId = await world.EnterByHandAsync(World.CheckInDay, World.CheckOutDay, adults: 2, totalPrice: 400m);
        await world.DeliverEventsAsync();

        await world.RunTheTimeTriggersAsync(World.InRome(World.CheckOutDay, 10, 5));
        var run = Assert.Single(await world.ExecutionsOfAsync("Pulizia al check-out"));
        Assert.Equal(RoutineExecutionOutcomes.Failed, run.Outcome);
        Assert.Equal(RoutineCodes.AiUnavailable, RoutineExecutionResult.Parse(run.ResultJson).Code);
        Assert.Empty(await world.CleaningsOfAsync(bookingId));
    }

    [Fact]
    [Trait("Scenario", "B7")]
    public void OperationsChain_TheTaxStepOfTheWizardIsTheOneTheWorldReplays_Test() =>
        Assert.True(
            World.TheWizardTaxStepMakesTheCallsOfTheWorld(),
            "The tax step of CheckInWizardService no longer confirms the tax and then reconciles the collection: change OperationsScenarioBuilder.ConfirmTouristTaxAsync with it, or call the wizard.");

    private static int PurgeRunsQueued(World world) => world.Jobs.Created.Count(j => j.Job.Type == typeof(GuestDocumentPurgeJob));
}
