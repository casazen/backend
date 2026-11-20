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
using Moq;
using Xunit;
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
    // Origine 1, prenotazione da un portale (Booking.com) tramite Octorate: senza e-mail, il link aspetta il permesso della chat, poi parte nel thread; check-in; ospiti a Octorate da soli; ricevuta; cancellazione dei documenti; tassa del canale; pulizia; nessuna recensione.
    [Fact]
    [Trait("Scenario", "A7 B1 B2 B3 B5 B7 B9 B10 B11")]
    public async Task OperationsChain_PortalOrigin_Test()
    {
        await using var world = await World.CreateAsync();
        world.TheChatIsNotLicensed();

        // B1: the booking comes from Booking.com through the channel manager; it carries no address of the guest.
        world.At(World.InRome(World.Day(11, 20), 9));
        var bookingId = await world.ImportPortalBookingAsync(world.PortalReservation(World.CheckInDay, World.CheckOutDay));
        await world.SeedPortalConversationAsync(bookingId);
        await world.DeliverEventsAsync();
        var booking = await world.BookingAsync(bookingId);
        Assert.Equal((BookingSource.BookingCom, BookingStatus.Confirmed, BookingOrigin.Portal), (booking.Source, booking.Status, BookingOrigins.Of(booking)));
        Assert.True(string.IsNullOrWhiteSpace(booking.Guest.Email));

        // B2: three days before the arrival the daily job issues the link. Nobody can carry it by e-mail and the chat of the portal is not licensed yet: the link waits for the
        // permission, nothing goes anywhere, and the host has one «To do» to carry the link itself.
        world.At(World.InRome(World.Day(12, 1), 9));
        await world.RunTheDailyCheckInJobAsync();
        var waiting = await world.DeliveryOfAsync(bookingId);
        Assert.Equal(
            (CheckInLinkChannel.PortalChat, CheckInLinkDeliveryState.WaitingChatPermission, OtaMessageSyncErrorCodes.ChatNotLicensed),
            (waiting.Channel, waiting.State, waiting.LastErrorCode));
        Assert.Empty(world.ChatMessages);
        var todo = Assert.Single(await world.ActivitiesOfAsync(bookingId), a => a.ActivityType == nameof(ActivityType.CheckInLinkToSend));
        Assert.Null(todo.CompletedAt);

        // The permission arrives: the copy of the chat reads the listing without error, and the next pass puts the link in the thread of the reservation, once, and the
        // «To do» closes by itself. A refused send is tried again once a day, never an hour after the other in the thread of the guest.
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

        // B3: the guest opens the link and completes the check-in; the link is over.
        var submit = await world.GuestChecksInAsync(
            token,
            World.Head(),
            World.Member("Rita", "Rossi", new DateTime(1987, 5, 10, 0, 0, 0, DateTimeKind.Utc), Gender.Female));
        Assert.True(submit.Success, string.Join(", ", submit.ValidationErrors.Select(e => $"{e.Index}:{e.Field}:{e.MessageKey}")));
        Assert.Equal(CheckInLinkEndReasons.CheckInCompleted, (await world.DeliveryOfAsync(bookingId)).EndedReason);

        // B5, A7: the organization asked for it, so the guests reach the channel manager on their own, with the places as its table names them, never as a draft (the
        // channel manager is the sender: CasaZen never sends to the police).
        await world.DeliverEventsAsync();
        Assert.Equal([0, 1], world.GuestsWritten.Select(g => g.Position));
        Assert.All(world.GuestsWritten, g => Assert.Equal((false, "COMO", "COMO"), (g.Draft, g.BirthCity, g.ResidenceCity)));

        // On the day of the arrival the stay reads «entrusted» to the channel manager, yellow, with no receipt yet.
        world.At(World.InRome(World.CheckInDay, 14));
        var entrusted = await world.StatusOfAsync(bookingId);
        Assert.Equal((AlloggiatiWebStatus.EntrustedToChannelManager, ComplianceSenderType.ChannelManager), (entrusted.Status, entrusted.Sender));
        Assert.Equal((ComplianceLight.Yellow, ComplianceReasonCodes.EntrustedToChannelManager), (entrusted.Verdict!.Verdict.Light, entrusted.Verdict.Verdict.Reason));
        Assert.Null(entrusted.ReceiptProof);

        // B11: the photo of the document stays while no receipt covers the stay.
        var image = await world.StoreDocumentImageAsync(bookingId);
        Assert.Equal(new GuestDocumentPurgeResult(true, 0, 0, 0), await world.PurgeAsync());
        Assert.Contains(image, world.Storage.Keys);

        // B5: the host uploads the receipt of the day for the flat: the stay turns green and the run that deletes the documents is queued; it deletes the photo of the
        // document and nothing of the legal record of the guests.
        var receipt = await world.UploadReceiptAsync(World.CheckInDay);
        Assert.Equal(1, receipt.CoveredStays);
        var green = await world.StatusOfAsync(bookingId);
        Assert.Equal((ComplianceLight.Green, ComplianceReasonCodes.ReceiptArchived), (green.Verdict!.Verdict.Light, green.Verdict.Verdict.Reason));
        Assert.Contains(world.Jobs.Created, j => j.Job.Type == typeof(GuestDocumentPurgeJob));
        var purged = await world.PurgeAsync();
        Assert.Equal((true, 1, 1), (purged.Enabled, purged.Stays, purged.Images));
        Assert.DoesNotContain(image, world.Storage.Keys);
        Assert.Equal(2, (await world.StayGuestsOfAsync(bookingId)).Count);
        Assert.Equal(new GuestDocumentPurgeResult(true, 0, 0, 0), await world.PurgeAsync());

        // Another organization reads nothing of the stay and cannot file a receipt for the flat of the agency.
        Assert.Null(await world.StatusAsAnotherOrganizationAsync(bookingId));
        await Assert.ThrowsAsync<NotFoundException>(() => world.UploadReceiptAsAnotherOrganizationAsync(World.CheckInDay));

        // B7: the portal collects the tourist tax of its own stays: nothing is payable online, and Stripe is never asked for it.
        var tax = await world.TouristTaxOfAsync(token);
        Assert.Equal((TouristTaxCollectionStatus.CollectedByChannel, TouristTaxNotPayableReason.ChannelCollects, 0m), (tax!.Status, tax.Reason, tax.Payable));
        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => world.StartTouristTaxPaymentAsync(token));
        Assert.Equal(TouristTaxCharges.NothingToPayCode, refused.Code);
        world.TheTaxWasNeverCharged();
        Assert.Empty(await world.PaymentsOfAsync(bookingId));

        // B10: at the check-out time of the flat the ready routine makes the cleaning for the member of the staff, once, though the host never registered the departure.
        await world.RunTheTimeTriggersAsync(World.InRome(World.CheckOutDay, 10, 5));
        var cleaning = Assert.Single(await world.CleaningsOfAsync(bookingId));
        Assert.Equal(PropertyCleanerKind.Staff, cleaning.CleanerKind);
        var work = Assert.Single(await world.ActivitiesOfAsync(bookingId), a => a.ActivityType == InternalActivity.CleaningActivityType);
        Assert.Equal((World.StaffUser, 40.00m, true), (work.StaffUserId, work.InternalRate, work.RechargeToOwner));
        await world.RunTheTimeTriggersAsync(World.InRome(World.CheckOutDay, 10, 20));
        Assert.Single(await world.CleaningsOfAsync(bookingId));

        // B11: the review is asked of the guests of the booking site only: the stay of a portal is skipped, with its reason, and nothing is sent.
        await world.RunTheTimeTriggersAsync(World.InRome(World.CheckOutDay.AddDays(1), 10, 5));
        var run = Assert.Single(await world.ExecutionsOfAsync("Richiesta di recensione"));
        Assert.Equal(RoutineExecutionOutcomes.Success, run.Outcome);
        Assert.Equal(
            new RoutineActionResult(0, RoutineActionType.RequestReview, RoutineActionOutcomes.Skipped, ReviewInviteSkipReasons.NotDirect),
            Assert.Single(RoutineExecutionResult.Parse(run.ResultJson).Actions));
        Assert.Empty(await world.ReviewInvitesAsync());
        Assert.DoesNotContain(world.Emails.Snapshot(), e => e.Template == EmailTemplates.Names.ReviewInvite);
    }

    // Origine 2, sito dell'agenzia: prenotazione diretta con utm_source=agency-site; origine «Sito dell'agenzia»; il link del check-in nella conferma; la tassa e' compresa nel pagamento, nessun secondo incasso.
    [Fact]
    [Trait("Scenario", "A11 B1 B2 B3 B7")]
    public async Task OperationsChain_AgencySiteOrigin_Test()
    {
        await using var world = await World.CreateAsync();

        // A11: the guest follows a link of the agency's own site to the booking page of CasaZen and books two days before the arrival. The link says where it comes from.
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

        // The payment of the booking carries the tourist tax of the two adults: three nights, the cleaning, and 2 x 3 x 2.00.
        Assert.Equal((500.00m, 12.00m, 512.00m), (created.BasePrice, created.TouristTaxAmount, created.Amount));

        // Stripe tells the platform the payment succeeded: the booking is confirmed, and the confirmation carries the link of the check-in, which is already open (the
        // arrival is within three days): it went by e-mail.
        await world.PayOnTheSiteAsync(bookingId);
        await world.DeliverEventsAsync();
        Assert.Equal(BookingStatus.Confirmed, (await world.BookingAsync(bookingId)).Status);
        var payment = Assert.Single(await world.PaymentsOfAsync(bookingId));
        Assert.Equal((PaymentStatus.Completed, 512.00m), (payment.Status, payment.Amount));
        var delivery = await world.DeliveryOfAsync(bookingId);
        Assert.Equal(CheckInLinkChannel.Email, delivery.Channel);
        Assert.Contains(delivery.State, new[] { CheckInLinkDeliveryState.Sent, CheckInLinkDeliveryState.Sending });

        // B3: the guest opens the link of the e-mail and registers the two adults; the host confirms the tax of the stay.
        var token = world.TokenOfTheConfirmationEmail();
        var submit = await world.GuestChecksInAsync(
            token,
            World.Head(),
            World.Member("Rita", "Rossi", new DateTime(1987, 5, 10, 0, 0, 0, DateTimeKind.Utc), Gender.Female));
        Assert.True(submit.Success, string.Join(", ", submit.ValidationErrors.Select(e => $"{e.Index}:{e.Field}:{e.MessageKey}")));
        await world.DeliverEventsAsync();
        Assert.Equal(12.00m, (await world.ConfirmTouristTaxAsync(bookingId)).AmountDue);

        // B7: what the booking payment carried is the whole tax: nothing is payable online, the host reads it as included, and the guest cannot pay it a second time.
        var view = await world.TouristTaxOfAsync(token);
        Assert.Equal(0m, view!.Payable);
        var host = await world.TouristTaxForHostAsync(bookingId);
        Assert.Equal((12.00m, 0m, 0m, 0m), (host.IncludedInBooking, host.OnlinePayable, host.RefundDue, host.CollectedAmount));
        var refused = await Assert.ThrowsAsync<DomainConflictException>(() => world.StartTouristTaxPaymentAsync(token));
        Assert.Equal(TouristTaxCharges.NothingToPayCode, refused.Code);
        world.TheTaxWasNeverCharged();
        Assert.Single(await world.PaymentsOfAsync(bookingId));
    }

    // Origine 3, sito di CasaZen: prenotazione diretta; il messaggio programmato del check-in parte dalla routine; la differenza della tassa si paga online (webhook finto); l'invito alla recensione il giorno dopo la partenza.
    [Fact]
    [Trait("Scenario", "B1 B2 B3 B7 B9 B10 B11")]
    public async Task OperationsChain_CasaZenSiteOrigin_Test()
    {
        await using var world = await World.CreateAsync();
        await world.AddTheCheckInMessageRoutineAsync();

        // B1: the guest books on the booking site nine days before the arrival: two adults and a child of ten, who owes no tourist tax (the rate starts at fourteen). The
        // check-in is not open yet, so the confirmation carries no link.
        world.At(World.InRome(World.Day(11, 25), 9));
        var created = await world.BookOnTheSiteAsync(World.CheckInDay, World.CheckOutDay, adults: 2, children: 1, childrenAges: [10]);
        var bookingId = created.BookingId;
        Assert.Equal(BookingOrigin.CasaZenSite, BookingOrigins.Of(await world.BookingAsync(bookingId)));
        Assert.Equal(12.00m, created.TouristTaxAmount);
        await world.PayOnTheSiteAsync(bookingId);
        await world.DeliverEventsAsync();
        Assert.Equal(BookingStatus.Confirmed, (await world.BookingAsync(bookingId)).Status);

        // B9: the confirmed booking is planned: one message, the day before the arrival at the check-in time of the flat (15:00 in Rome), with no link issued yet.
        var planned = Assert.Single(await world.ScheduledMessagesOfAsync(bookingId));
        Assert.Equal(ScheduledMessageStatus.Scheduled, planned.Status);
        Assert.Equal(World.InRome(World.CheckInDay.AddDays(-1), 15).UtcDateTime, planned.PlannedAtUtc);
        Assert.Empty(world.DirectThread.Automatic);

        // At the time the routine fires: the message goes to the conversation of the booking, once, with the link of the check-in made at that moment.
        await world.RunTheTimeTriggersAsync(World.InRome(World.CheckInDay.AddDays(-1), 15, 5));
        var message = Assert.Single(world.DirectThread.Automatic);
        Assert.Equal(bookingId, message.BookingId);
        var token = World.TokenIn(message.Text);
        var sentMessage = Assert.Single(await world.ScheduledMessagesOfAsync(bookingId));
        Assert.Equal((ScheduledMessageStatus.Sent, $"direct:{bookingId:D}"), (sentMessage.Status, sentMessage.SentThreadKey));
        Assert.Equal(GuestCheckInSessionStatus.Inviato, (await world.SessionOfAsync(bookingId)).Status);
        await world.RunTheTimeTriggersAsync(World.InRome(World.CheckInDay.AddDays(-1), 15, 20));
        Assert.Single(world.DirectThread.Automatic);

        // B3: the guest registers the three people: the child of ten of the booking is, in the registration, a member of fifteen, and owes the tax.
        var submit = await world.GuestChecksInAsync(
            token,
            World.Head(),
            World.Member("Rita", "Rossi", new DateTime(1987, 5, 10, 0, 0, 0, DateTimeKind.Utc), Gender.Female),
            World.Member("Luca", "Rossi", new DateTime(2011, 1, 15, 0, 0, 0, DateTimeKind.Utc), Gender.Male));
        Assert.True(submit.Success, string.Join(", ", submit.ValidationErrors.Select(e => $"{e.Index}:{e.Field}:{e.MessageKey}")));
        await world.DeliverEventsAsync();

        // B7: the host confirms the tax of the stay: 3 x 3 x 2.00 = 18.00, 12.00 of which the booking payment carried. The guest pays the difference, 6.00, online.
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

        // B11: the day after the departure the routine asks the guest for the review of the stay: one invitation, to the address of the guest, however many times the job runs.
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

        // B1: the host enters the booking by hand two days before the arrival: confirmed at once, no payment, nothing sent to the guest by the entry itself.
        world.At(World.InRome(World.Day(12, 2), 9));
        var bookingId = await world.EnterByHandAsync(World.CheckInDay, World.CheckOutDay, adults: 2, totalPrice: 400m);
        await world.DeliverEventsAsync();
        var booking = await world.BookingAsync(bookingId);
        Assert.Equal((BookingSource.Manual, BookingStatus.Confirmed, BookingOrigin.Manual), (booking.Source, booking.Status, BookingOrigins.Of(booking)));
        Assert.Empty(await world.PaymentsOfAsync(bookingId));

        // B2: the arrival is within three days, so the daily job issues the link of the check-in and queues the e-mail to the guest.
        await world.RunTheDailyCheckInJobAsync();
        Assert.Equal(GuestCheckInLinkEmailStatus.Queued, (await world.SessionOfAsync(bookingId)).LinkEmailStatus);
        var token = world.TokenOfTheQueuedLinkEmail();

        // B3: the guest registers the two adults; the host confirms the tax of the stay.
        var submit = await world.GuestChecksInAsync(
            token,
            World.Head(),
            World.Member("Rita", "Rossi", new DateTime(1987, 5, 10, 0, 0, 0, DateTimeKind.Utc), Gender.Female));
        Assert.True(submit.Success, string.Join(", ", submit.ValidationErrors.Select(e => $"{e.Index}:{e.Field}:{e.MessageKey}")));
        await world.DeliverEventsAsync();
        Assert.Equal(12.00m, (await world.ConfirmTouristTaxAsync(bookingId)).AmountDue);

        // B7: the price of a booking of the host is the host's, so CasaZen has collected nothing of the tax: the guest pays all of it online, 12.00, once.
        var due = await world.TouristTaxOfAsync(token);
        Assert.Equal((TouristTaxCollectionStatus.ToCollect, 12.00m), (due!.Status, due.Payable));
        var session = await world.StartTouristTaxPaymentAsync(token);
        Assert.Equal(12.00m, session!.Amount);
        await world.TouristTaxPaidAsync(session, "evt_tax_manual_1");
        var record = await world.TouristTaxRecordOfAsync(bookingId);
        Assert.Equal((TouristTaxCollectionStatus.PaidOnline, 12.00m), (record!.CollectionStatus, record.CollectedAmount));
        Assert.Empty(await world.PaymentsOfAsync(bookingId));

        // B10: at the check-out time the ready routine makes the cleaning for the member of the staff; once the work is done it is a cost the invoicing of the owners reads,
        // with its amount, its kind and its source.
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

        // The CasaZen site, with a difference: the booking payment carried 12.00 of the 18.00 the guests owe. The tax is collected for 6.00, once.
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

        // When Stripe tells the platform the payment succeeded the record is paid; the same event delivered again, as Stripe does, changes nothing.
        await world.TouristTaxPaidAsync(session, "evt_tax_once");
        await world.TouristTaxPaidAsync(session, "evt_tax_once");
        var record = await world.TouristTaxRecordOfAsync(created.BookingId);
        Assert.Equal((TouristTaxCollectionStatus.PaidOnline, 6.00m), (record!.CollectionStatus, record.CollectedAmount));
        Assert.Single(world.Pushes.Queued, p => p.Payload.Type == PushTypes.TouristTaxPaid);

        // Paid: a guest who asks again, with the same link or after the host confirms the tax once more, is refused, and Stripe is not asked.
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

    // Identificazione prima dell'invio: con il passo d'identita' acceso, gli ospiti non partono verso il gestore delle camere finche' una persona non ha accettato; poi partono, una volta sola.
    [Fact]
    [Trait("Scenario", "B3 B5")]
    public async Task OperationsChain_IdentityBeforeTheGuestsReachTheChannelManager_Test()
    {
        await using var world = await World.CreateAsync(FeatureFlags.CheckInIdentityVerification);
        world.At(World.InRome(World.Day(11, 20), 9));
        var bookingId = await world.ImportPortalBookingAsync(world.PortalReservation(World.CheckInDay, World.CheckOutDay));
        await world.DeliverEventsAsync();

        // B2: the host carries the link to the guest, who registers the two people.
        world.At(World.InRome(World.Day(12, 2), 9));
        var token = await world.HostIssuesTheLinkAsync(bookingId);
        var submit = await world.GuestChecksInAsync(
            token,
            World.Head(),
            World.Member("Rita", "Rossi", new DateTime(1987, 5, 10, 0, 0, 0, DateTimeKind.Utc), Gender.Female));
        Assert.True(submit.Success, string.Join(", ", submit.ValidationErrors.Select(e => $"{e.Index}:{e.Field}:{e.MessageKey}")));

        // B3: the data is complete, but nobody verified who the guest is: nothing leaves, whether no verification exists or the step has escalated to the operator.
        await world.DeliverEventsAsync();
        Assert.Empty(world.GuestsWritten);
        await world.TheIdentityWaitsForTheOperatorAsync(bookingId);
        await world.DeliverEventsAsync();
        Assert.Empty(world.GuestsWritten);

        // The operator accepts the identity: the guests reach the channel manager, once, and only then.
        world.At(World.InRome(World.Day(12, 2), 10));
        Assert.True(await world.TheOperatorAcceptsAsync(bookingId));
        await world.DeliverEventsAsync();
        Assert.Equal([0, 1], world.GuestsWritten.Select(g => g.Position));
        Assert.All(world.GuestsWritten, g => Assert.False(g.Draft));
        world.At(World.InRome(World.Day(12, 2), 12));
        await world.DeliverEventsAsync();
        Assert.Equal(2, world.GuestsWritten.Count);
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

    // Prerequisito dei passi delle routine (messaggio programmato, pulizia al check-out, richiesta di recensione): senza un provider AI in effetto (Ai:Provider=Stub, il default) il pianificatore non sceglie nulla e ogni esecuzione finisce Failed.
    [Fact]
    [Trait("Scenario", "B9 B10 B11")]
    public async Task OperationsChain_RoutinesNeedAnAiProvider_Test()
    {
        await using var world = await World.CreateWithoutAnAiProviderAsync();

        // B1: the host enters the booking by hand two days before the arrival.
        world.At(World.InRome(World.Day(12, 2), 9));
        var bookingId = await world.EnterByHandAsync(World.CheckInDay, World.CheckOutDay, adults: 2, totalPrice: 400m);
        await world.DeliverEventsAsync();

        // B10: at the check-out time the ready routine fires and the planner has no provider to ask: the execution is closed Failed with its code (the host reads it in the log of the routine)
        // and no cleaning is made. Every routine of the chain is in the same case, whatever its action.
        await world.RunTheTimeTriggersAsync(World.InRome(World.CheckOutDay, 10, 5));
        var run = Assert.Single(await world.ExecutionsOfAsync("Pulizia al check-out"));
        Assert.Equal(RoutineExecutionOutcomes.Failed, run.Outcome);
        Assert.Equal(RoutineCodes.AiUnavailable, RoutineExecutionResult.Parse(run.ResultJson).Code);
        Assert.Empty(await world.CleaningsOfAsync(bookingId));
    }
}
