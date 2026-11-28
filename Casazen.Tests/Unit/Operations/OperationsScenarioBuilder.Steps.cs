using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Bookings;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Octorate;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data.Seeds;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Jobs;
using Casazen.Web.BackgroundJobs;
using Microsoft.EntityFrameworkCore;
using Moq;
using StripeEvent = Stripe.Event;
using StripeEventData = Stripe.EventData;
using StripeIntent = Stripe.PaymentIntent;

namespace Casazen.Tests.Unit.Operations;

internal sealed partial class OperationsScenarioBuilder
{
    public OctorateReservation PortalReservation(DateOnly checkIn, DateOnly checkOut)
    {
        var id = $"BDC{Interlocked.Increment(ref _sequence):D4}";
        return new OctorateReservation
        {
            Id = id,
            Status = OctorateBookingStatus.Confirmed,
            Product = OctorateRoom,
            Currency = "EUR",
            ChannelId = BookingComChannel,
            ChannelName = "Booking.com",
            CheckIn = Iso(checkIn),
            CheckOut = Iso(checkOut),
            UpdatedAt = Clock.GetUtcNow().UtcDateTime,
            FirstName = "Mario",
            LastName = "Rossi",
            TotalGuests = 2,
            Adults = 2,
            RoomGross = 450m,
            TotalGross = 450m,
            CompanyCollect = OctorateReservation.CompanyCollectCompany,
            PriceBreakdown = [new OctoratePriceComponent(OctorateReservation.CommissionType, 67.50m)],
        };
    }

    public async Task<Guid> ImportPortalBookingAsync(OctorateReservation reservation)
    {
        Octorate
            .Setup(c => c.GetReservationsAsync(OctorateAccommodation, It.IsAny<OctorateReservationQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, OctorateReservationQuery query, CancellationToken _) =>
                query.Page == 0 ? OctorateReservationPage.Of(reservation) : OctorateReservationPage.Empty);
        Octorate
            .Setup(c => c.GetReservationAsync(OctorateAccommodation, reservation.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(reservation);

        await using (var scope = Scope())
        {
            var result = await scope.Get<IBookingImportService>().ImportAllAsync(PropertyId, CancellationToken.None);
            if (result.Outcome != ImportOutcome.Completed || result.Failed > 0)
                throw new InvalidOperationException($"The import of reservation {reservation.Id} did not complete: {result.Outcome}, {result.Failed} failed.");
        }

        await using var read = Scope(underTheOrg: false);
        return await read.Db.Bookings.AsNoTracking().Where(b => b.OctorateReservationId == reservation.Id).Select(b => b.Id).SingleAsync();
    }

    public async Task SeedPortalConversationAsync(Guid bookingId)
    {
        await using var scope = Scope(underTheOrg: false);
        var now = Clock.GetUtcNow().UtcDateTime;
        scope.Db.OtaMessageThreads.Add(new OtaMessageThread
        {
            OrgId = OrgId,
            PropertyId = PropertyId,
            BookingId = bookingId,
            OctorateThreadId = $"T-{Guid.NewGuid():N}"[..20],
            OctorateAccommodationId = OctorateAccommodation,
            ChannelSource = OtaChannel.Booking,
            Kind = OtaThreadKind.Reservation,
            GuestDisplayName = "Mario R. (TEST)",
            LastMessageAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await scope.Db.SaveChangesAsync();
    }

    public async Task TheChatSyncReadsTheListingAsync()
    {
        Octorate
            .Setup(c => c.GetRecentChatMessagesAsync(OctorateAccommodation, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await using var scope = Scope(underTheOrg: false);
        await scope.Get<IMessageSyncService>().ReconcileRecentAsync(OrgId, CancellationToken.None);

        // The pass leaves a clean checkpoint for the flat of the mapping: a pass that read nothing (the connector off, no listing) would lift nothing, and the step says so instead of going on.
        var state = await scope.Db.OtaMessageSyncStates.AsNoTracking().SingleOrDefaultAsync(s => s.OrgId == OrgId && s.OctorateAccommodationId == OctorateAccommodation);
        if (state is null || state.LastError is not null || state.PropertyId != PropertyId || state.LastRecentPassAt is null)
            throw new InvalidOperationException("The pass of the chat sync left no clean checkpoint for the listing of the flat.");
    }

    public async Task<DirectBookingCreateResult> BookOnTheSiteAsync(
        DateOnly checkIn,
        DateOnly checkOut,
        int adults,
        int children = 0,
        IReadOnlyList<int>? childrenAges = null,
        BookingAttributionInput? attribution = null)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Get<IBookingService>().CreateDirectBookingAsync(new DirectBookingCreateInput(
            PropertyId,
            Utc(checkIn),
            Utc(checkOut),
            adults,
            children,
            new DirectBookingGuestInput("Mario", "Rossi", GuestEmail, null, "IT"),
            "2026-06-direct-checkout-v1",
            "203.0.113.7",
            null,
            PaymentOption.Immediate,
            childrenAges,
            Attribution: attribution));
    }

    public async Task PayOnTheSiteAsync(Guid bookingId)
    {
        string intent;
        long cents;
        await using (var read = Scope(underTheOrg: false))
        {
            var payment = await read.Db.Payments.AsNoTracking().SingleAsync(p => p.BookingId == bookingId);
            intent = payment.StripePaymentIntentId ?? throw new InvalidOperationException("The checkout made no payment intent.");
            cents = (long)decimal.Round(payment.Amount * 100m, 0);
        }

        await using var scope = Scope(underTheOrg: false);
        await scope.Get<StripeWebhookHandler>().HandleEventAsync(
            Succeeded(
                intent,
                cents,
                new Dictionary<string, string> { ["kind"] = "direct-booking", ["bookingId"] = bookingId.ToString(), ["orgId"] = OrgId.ToString(), ["propertyId"] = PropertyId.ToString() }),
            WebhookSource.Connected);
    }

    public async Task<Guid> EnterByHandAsync(DateOnly checkIn, DateOnly checkOut, int adults, decimal totalPrice)
    {
        await using var scope = Scope();
        var property = await scope.Db.Properties.SingleAsync(p => p.Id == PropertyId);
        var result = await scope.Get<IManualBookingService>().CreateAsync(new ManualBookingInput(
            property,
            Utc(checkIn),
            Utc(checkOut),
            adults,
            0,
            null,
            new ManualBookingGuest("Mario", "Rossi", GuestEmail, null, "IT"),
            null,
            totalPrice,
            null,
            null,
            BookingSource.Manual,
            HostPaymentArrangement.None));
        return result.Booking.Id;
    }

    public async Task RunTheDailyCheckInJobAsync()
    {
        await using var scope = Scope(underTheOrg: false);
        await scope.Create<GuestCheckInSendJob>().ExecuteAsync();
    }

    public async Task RunTheLinkRetryAsync()
    {
        await using var scope = Scope(underTheOrg: false);
        await scope.Create<CheckInLinkDeliveryRetryJob>().ExecuteAsync(CancellationToken.None);
    }

    /// <summary>
    /// The outbox delivers the events not delivered yet, the oldest first, to the handlers the application registers (the dispatcher, the in-process bus, each handler in a scope of its own
    /// under the org of its event), until nothing is left: what a handler records in answer is delivered in the next round, and an event a handler asks to see again (a send that
    /// finds the lock of its stay taken, a reservation still being made) is delivered again. An event that is still undelivered after the rounds, or dead-lettered, fails the step with
    /// what the handler said.
    /// </summary>
    public async Task DeliverEventsAsync()
    {
        List<OutboxEvent> open = [];
        for (var round = 0; round < 6; round++)
        {
            await using (var run = Scope(underTheOrg: false))
                await run.Create<OutboxDispatcherJob>().ExecuteAsync(CancellationToken.None);

            await using var read = Scope(underTheOrg: false);
            open = await read.Db.OutboxEvents.AsNoTracking().Where(e => e.DispatchedAt == null).ToListAsync();
            if (open.Count == 0)
                return;
        }

        throw new InvalidOperationException(
            "The outbox still holds events after the rounds: " + string.Join("; ", open.Select(e => $"{e.EventType} (attempts {e.Attempts}, dead {e.DeadLettered}): {e.LastError}")));
    }

    public async Task RunTheTimeTriggersAsync(DateTimeOffset at)
    {
        At(at);
        await using (var scope = Scope(underTheOrg: false))
            await scope.Create<TimeEventEmitterJob>().ExecuteAsync(CancellationToken.None);

        await DeliverEventsAsync();
    }

    public static StayGuestInput Head(string firstName = "Mario", string lastName = "Rossi") => new()
    {
        Type = "HeadOfFamily",
        FirstName = firstName,
        LastName = lastName,
        Gender = Gender.Male,
        DateOfBirth = new DateTime(1985, 3, 2, 0, 0, 0, DateTimeKind.Utc),
        BornInItaly = true,
        BirthComuneName = ComuneName,
        BirthProvince = "CO",
        CitizenshipName = "Italia",
        CitizenshipCode = OctorateCheckInMapper.ItalyStateCode,
        ResidenceInItaly = true,
        ResidenceComuneName = ComuneName,
        ResidenceProvince = "CO",
        DocumentType = "Passport",
        DocumentNumber = "YA1234567",
        DocumentIssuePlaceName = ComuneName,
    };

    public static StayGuestInput Member(string firstName, string lastName, DateTime dateOfBirth, Gender gender) => new()
    {
        Type = "FamilyMember",
        FirstName = firstName,
        LastName = lastName,
        Gender = gender,
        DateOfBirth = dateOfBirth,
        BornInItaly = true,
        BirthComuneName = ComuneName,
        BirthProvince = "CO",
        CitizenshipName = "Italia",
        CitizenshipCode = OctorateCheckInMapper.ItalyStateCode,
        ResidenceInItaly = true,
        ResidenceComuneName = ComuneName,
        ResidenceProvince = "CO",
    };

    public async Task<GuestCheckInSubmitResult> GuestChecksInAsync(string token, params StayGuestInput[] guests)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Get<IGuestCheckInService>().SubmitAsync(
            token,
            new GuestCheckInSubmitRequest { Guests = guests, PrivacyNoticeAcknowledged = true, ConsentIpAddress = "203.0.113.7" });
    }

    public async Task<string> HostIssuesTheLinkAsync(Guid bookingId)
    {
        await using var scope = Scope();
        return (await scope.Get<IGuestCheckInService>().IssueLinkAsync(bookingId, OrgId)).Token;
    }

    /// <summary>The per-stay status of the Alloggiati communication, as the API of the stay reads it.</summary>
    public async Task<ComplianceStayStatus> StatusOfAsync(Guid bookingId)
    {
        await using var scope = Scope();
        return await scope.Get<IComplianceStatusService>().GetStayStatusAsync(WholeOrg, bookingId, CancellationToken.None)
               ?? throw new InvalidOperationException("The stay has no status.");
    }

    /// <summary>The host uploads the receipt of the Alloggiati communication of the flat for <paramref name="day"/>.</summary>
    public async Task<ComplianceArchiveEntry> UploadReceiptAsync(DateOnly day)
    {
        await using var scope = Scope();
        await using var file = new MemoryStream("%PDF-1.4 TEST ricevuta Alloggiati"u8.ToArray());
        return await scope.Get<IComplianceArchiveService>().UploadProofAsync(
            WholeOrg,
            new UploadComplianceProofRequest(PropertyId, ManualProofKind.AlloggiatiReceipt, day, HostUser),
            file,
            CancellationToken.None);
    }

    public async Task<string> StoreDocumentImageAsync(Guid bookingId)
    {
        await using var scope = Scope(underTheOrg: false);
        var booking = await scope.Db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        var key = StorageKeys.GuestDocument(OrgId, booking.GuestId, $"{Guid.NewGuid():N}.bin");
        Storage.Add(key, [1, 2, 3, 4]);
        var now = Clock.GetUtcNow().UtcDateTime;
        scope.Db.DocumentImages.Add(new DocumentImage
        {
            OrgId = OrgId,
            GuestId = booking.GuestId,
            BookingId = bookingId,
            FrontImageUrl = key,
            UploadedAt = now,
            ProcessedAt = now,
        });
        await scope.Db.SaveChangesAsync();
        return key;
    }

    public async Task<GuestDocumentPurgeResult> PurgeAsync()
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Get<IGuestDocumentPurgeService>().PurgeAsync(OrgId, CancellationToken.None);
    }

    public async Task<List<DocumentImage>> DocumentImagesOfAsync(Guid bookingId)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Db.DocumentImages.AsNoTracking().Where(i => i.BookingId == bookingId).ToListAsync();
    }

    public async Task<List<ComplianceSendLogEntry>> DocumentDeletionsOfAsync(Guid bookingId)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Db.ComplianceSendLogEntries.AsNoTracking().Where(e => e.BookingId == bookingId && e.Kind == ComplianceSendKind.DocumentsDeleted).ToListAsync();
    }

    public async Task<ComplianceStayStatus?> StatusAsAnotherOrganizationAsync(Guid bookingId)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Get<IComplianceStatusService>().GetStayStatusAsync(new HostScope(Guid.NewGuid()), bookingId, CancellationToken.None);
    }

    public async Task UploadReceiptAsAnotherOrganizationAsync(DateOnly day)
    {
        await using var scope = Scope(underTheOrg: false);
        await using var file = new MemoryStream("%PDF-1.4 TEST ricevuta altrui"u8.ToArray());
        await scope.Get<IComplianceArchiveService>().UploadProofAsync(
            new HostScope(Guid.NewGuid()),
            new UploadComplianceProofRequest(PropertyId, ManualProofKind.AlloggiatiReceipt, day, "auth0|test-ops-intruder"),
            file,
            CancellationToken.None);
    }

    /// <summary>The proof of the monthly ISTAT communication of the flat, filed by the host for the month of <paramref name="day"/>.</summary>
    public async Task<ComplianceArchiveEntry> UploadIstatProofAsync(DateOnly day)
    {
        await using var scope = Scope();
        await using var file = new MemoryStream("%PDF-1.4 TEST prova ISTAT"u8.ToArray());
        return await scope.Get<IComplianceArchiveService>().UploadProofAsync(
            WholeOrg,
            new UploadComplianceProofRequest(PropertyId, ManualProofKind.IstatProof, day, HostUser),
            file,
            CancellationToken.None);
    }

    public async Task<ComplianceArchivePage> ProofsOfAsync(ManualProofKind kind)
    {
        await using var scope = Scope();
        return await scope.Get<IComplianceArchiveService>().ListAsync(WholeOrg, new ComplianceArchiveQuery(PropertyId, Kind: kind), CancellationToken.None);
    }

    public async Task TheIdentityWaitsForTheOperatorAsync(Guid bookingId)
    {
        await using var scope = Scope(underTheOrg: false);
        var booking = await scope.Db.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        var session = await scope.Db.GuestCheckInSessions.OrderByDescending(s => s.CreatedAt).FirstAsync(s => s.BookingId == bookingId);
        session.BiometricOutcome = BiometricOutcome.Escalate;
        scope.Db.BiometricVerificationLogs.Add(new BiometricVerificationLog
        {
            OrgId = OrgId,
            BookingId = bookingId,
            GuestId = booking.GuestId,
            Outcome = BiometricOutcome.Escalate,
            Method = VerificationMethod.Biometric,
            Attempts = 1,
            ModelName = "none",
            ModelVersion = "0",
            ThresholdsJson = "{}",
            ReasonsJson = "[]",
            Provisional = false,
            CreatedAt = Clock.GetUtcNow().UtcDateTime,
        });
        await scope.Db.SaveChangesAsync();
    }

    public async Task<bool> TheOperatorAcceptsAsync(Guid bookingId)
    {
        await using var scope = Scope();
        return await scope.Get<IVerificationQueueService>().RecordManualVerificationAsync(bookingId, WholeOrg, true, null, HostUser);
    }

    public async Task<bool> TheOperatorRejectsAsync(Guid bookingId)
    {
        await using var scope = Scope();
        return await scope.Get<IVerificationQueueService>().RecordManualVerificationAsync(bookingId, WholeOrg, false, "TEST documento non leggibile", HostUser);
    }

    /// <summary>
    /// The guests of the stay are offered to the channel manager now, as the consumer of the events, the retry and the sweep offer them (the consumer does exactly this on an event): the answer
    /// says why they stay. A rejection of the operator records no event (only an approval does), so no event reaches the consumer after it: this is the evaluation that the next event, the retry or
    /// the sweep would make, and it tells that the gate of the identity holds instead of leaving it to an event that never comes.
    /// </summary>
    public async Task<Casazen.Infrastructure.Octorate.StayGuestsAutoSendOutcome> TheGuestsAreOfferedAsync(Guid bookingId)
    {
        await using var scope = Scope(underTheOrg: false);
        var answer = await scope.Get<Casazen.Infrastructure.Octorate.IStayGuestsAutoSendService>().TrySendAsync(OrgId, bookingId, CancellationToken.None);
        return answer.Outcome;
    }

    /// <summary>The comuni and the table of the regions of the ISTAT communication, as the migrations leave them on a database (the in-memory one starts empty).</summary>
    public async Task SeedTheIstatTableAsync()
    {
        await using var scope = Scope(underTheOrg: false);
        await ComuneTestData.ImportSampleAsync(scope.Db);
        scope.Db.IstatRegionRules.AddRange(IstatRegionRuleSeed.Build());
        await scope.Db.SaveChangesAsync();
    }

    /// <summary>What the host reads of the ISTAT communication of the flat: its region, its deadline, who covers it and the next step.</summary>
    public async Task<IstatCoverageView> IstatCoverageAsync()
    {
        await using var scope = Scope();
        return await scope.Get<IIstatRegionService>().GetCoverageAsync(PropertyId, CancellationToken.None);
    }

    public async Task StaffVerifiesTheRegionAsync(IstatCoverageView view, IstatCoverage coverage)
    {
        await using var scope = Scope(underTheOrg: false);
        await scope.Get<IIstatRegionService>().UpdateAsync(
            new IstatRegionRuleUpdate(
                view.RegionIstatCode!,
                view.ProvinceCode,
                view.System,
                view.PortalUrl,
                view.DeadlineKind,
                view.DeadlineDay,
                view.RequiredGuestFields,
                coverage,
                view.SourceUrl!,
                null,
                "verificata sulla fonte (TEST)"),
            "auth0|test-ops-platform-staff",
            CancellationToken.None);
    }

    /// <summary>
    /// The host confirms the tourist tax of the stay: the amount the guests registered owe. It is the tax step of the wizard of the check-in, in the order the wizard makes it
    /// (<c>CheckInWizardService.ConfirmTaxAsync</c>: confirm, then reconcile the collection), <b>made here call by call</b>. The step is private to the wizard, and the public door of the wizard,
    /// <c>ConfirmAsync</c>, also runs the whole readiness check and sends the guests again through the in-house Alloggiati machinery, which the world replaces on purpose (CasaZen sends nothing to
    /// the police). <see cref="TheWizardTaxStepMakesTheCallsOfTheWorld"/> reads the wizard and tells when it stops making these two calls; it is declared in docs/runbooks/operations-scenario.md § 4.
    /// </summary>
    public async Task<StayTouristTaxView> ConfirmTouristTaxAsync(Guid bookingId)
    {
        await using var scope = Scope();
        var confirmed = await scope.Get<IStayTouristTaxService>().ConfirmAsync(bookingId, HostUser);
        await scope.Get<ITouristTaxCollectionService>().ReconcileAfterChangeAsync(bookingId);
        return confirmed;
    }

    /// <summary>
    /// Whether the wizard still makes, in its tax step, the two calls <see cref="ConfirmTouristTaxAsync"/> makes by hand: the confirmation of the tax and, after it, the reconciliation of the
    /// collection. The step is private, so this reads the source of the wizard (the way the architecture tests of the repository read it): a wizard that changes them makes the world replay a
    /// step that is not the application's any more.
    /// </summary>
    public static bool TheWizardTaxStepMakesTheCallsOfTheWorld()
    {
        var source = File.ReadAllText(Path.Combine(SolutionRoot(), "Casazen.Infrastructure", "Services", "CheckInWizardService.cs"));
        var step = source.IndexOf("Task<CheckInTaxOutcome> ConfirmTaxAsync(", StringComparison.Ordinal);
        var confirm = step < 0 ? -1 : source.IndexOf("stayTouristTax.ConfirmAsync(", step, StringComparison.Ordinal);
        var reconcile = confirm < 0 ? -1 : source.IndexOf("touristTaxCollection.ReconcileAfterChangeAsync(", confirm, StringComparison.Ordinal);
        return step >= 0 && confirm > step && reconcile > confirm && reconcile - step < 2000;
    }

    public async Task<TouristTaxGuestView?> TouristTaxOfAsync(string token)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Get<ITouristTaxCollectionService>().GetForGuestAsync(token);
    }

    public async Task<TouristTaxPaymentSession?> StartTouristTaxPaymentAsync(string token)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Get<ITouristTaxCollectionService>().CreatePaymentIntentAsync(token);
    }

    public async Task TouristTaxPaidAsync(TouristTaxPaymentSession session, string eventId, WebhookSource source = WebhookSource.Connected)
    {
        var intent = _taxIntents[session.PaymentIntentId];
        await using var scope = Scope(underTheOrg: false);
        await scope.Get<StripeWebhookHandler>().HandleEventAsync(Succeeded(intent.Id, intent.Amount, intent.Metadata, eventId), source);
    }

    private StripeEvent Succeeded(string paymentIntentId, long cents, IDictionary<string, string> metadata, string? eventId = null) => new()
    {
        Id = eventId ?? $"evt_{Guid.NewGuid():N}",
        Type = "payment_intent.succeeded",
        Account = StripeAccount,
        Data = new StripeEventData
        {
            Object = new StripeIntent
            {
                Id = paymentIntentId,
                Amount = cents,
                AmountReceived = cents,
                Currency = "eur",
                Status = "succeeded",
                Metadata = new Dictionary<string, string>(metadata),
            },
        },
    };

    public async Task<Booking> BookingAsync(Guid bookingId)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Db.Bookings.AsNoTracking().Include(b => b.Guest).SingleAsync(b => b.Id == bookingId);
    }

    public async Task<CheckInLinkDelivery> DeliveryOfAsync(Guid bookingId)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Db.CheckInLinkDeliveries.AsNoTracking().SingleAsync(d => d.BookingId == bookingId);
    }

    public async Task<List<InternalActivity>> ActivitiesOfAsync(Guid bookingId)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Db.InternalActivities.AsNoTracking().Where(a => a.EntityId == bookingId || a.BookingId == bookingId).OrderBy(a => a.CreatedAt).ToListAsync();
    }

    public async Task<List<StayGuest>> StayGuestsOfAsync(Guid bookingId)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Db.StayGuests.AsNoTracking().Where(g => g.BookingId == bookingId).OrderBy(g => g.Position).ToListAsync();
    }

    public async Task<StayTouristTax?> TouristTaxRecordOfAsync(Guid bookingId)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Db.StayTouristTaxes.AsNoTracking().SingleOrDefaultAsync(t => t.BookingId == bookingId);
    }

    public async Task<List<Payment>> PaymentsOfAsync(Guid bookingId)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Db.Payments.AsNoTracking().Where(p => p.BookingId == bookingId).OrderBy(p => p.CreatedAt).ToListAsync();
    }

    public async Task<List<StayCleaning>> CleaningsOfAsync(Guid bookingId)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Db.StayCleanings.AsNoTracking().Where(c => c.BookingId == bookingId).ToListAsync();
    }

    public async Task<List<RoutineExecution>> ExecutionsOfAsync(string routineName)
    {
        await using var scope = Scope(underTheOrg: false);
        var routineId = await scope.Db.Routines.AsNoTracking().Where(r => r.Name == routineName).Select(r => r.Id).SingleAsync();
        return await scope.Db.RoutineExecutions.AsNoTracking().Where(e => e.RoutineId == routineId).OrderBy(e => e.StartedAt).ToListAsync();
    }

    public async Task<List<ReviewInvite>> ReviewInvitesAsync()
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Db.ReviewInvites.AsNoTracking().ToListAsync();
    }

    /// <summary>Stripe was never asked for a payment intent of the tourist tax (the seven-argument door is the tax's, the four-argument one is the checkout's).</summary>
    public void TheTaxWasNeverCharged() => VerifyTaxIntents(Times.Never());

    public void TheTaxWasChargedOnce() => VerifyTaxIntents(Times.Once());

    private void VerifyTaxIntents(Times times) =>
        Stripe.Verify(
            s => s.CreateConnectedAccountPaymentIntentAsync(
                It.IsAny<string>(),
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<Dictionary<string, string>>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            times);

    public async Task<TouristTaxCollectionView> TouristTaxForHostAsync(Guid bookingId)
    {
        await using var scope = Scope();
        return await scope.Get<ITouristTaxCollectionService>().GetForHostAsync(bookingId, null);
    }

    public async Task<GuestCheckInSession> SessionOfAsync(Guid bookingId)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Db.GuestCheckInSessions.AsNoTracking().OrderByDescending(s => s.CreatedAt).FirstAsync(s => s.BookingId == bookingId);
    }

    public string TokenOfTheQueuedLinkEmail()
    {
        var queued = Jobs.Created.Last(j => j.Job.Type == typeof(Casazen.Infrastructure.Email.GuestCheckInLinkEmailJob));
        return (string)queued.Job.Args[1];
    }

    public string TokenOfTheConfirmationEmail()
    {
        var email = Emails.Snapshot().Last(e => e.Template == Casazen.Infrastructure.Email.Templates.EmailTemplates.Names.GuestBookingConfirmed);
        return TokenIn(email.Content.HtmlBody);
    }

    public async Task AddTheCheckInMessageRoutineAsync()
    {
        await using var scope = Scope(underTheOrg: false);
        var template = new MessageTemplate { OrgId = OrgId, Name = "Arrivo (TEST)", Language = "it", Body = "Il check-in si fa qui: {{check_in_link}} (TEST)", IsActive = true };
        scope.Db.MessageTemplates.Add(template);
        scope.Db.Routines.Add(NewRoutine(
            "Messaggio di arrivo",
            RoutineEventType.BeforeCheckin,
            """{"days":1}""",
            "[{\"type\":\"MessageToGuest\",\"config\":{\"templateId\":\"" + template.Id + "\"}}]"));
        await scope.Db.SaveChangesAsync();
    }

    public async Task<List<ScheduledMessage>> ScheduledMessagesOfAsync(Guid bookingId)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Db.ScheduledMessages.AsNoTracking().Where(m => m.BookingId == bookingId).OrderBy(m => m.PlannedAtUtc).ToListAsync();
    }

    public async Task CompleteTheCleaningAsync(Guid activityId)
    {
        await using var scope = Scope();
        await scope.Get<IInternalActivityService>().UpdateStatusAsync(activityId, InternalActivity.StatusCompleted);
    }

    public async Task<IReadOnlyList<CompletedCleaning>> CompletedCleaningsAsync(DateOnly from, DateOnly to)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Get<ICleaningCostSource>().GetCompletedAsync(OrgId, [PropertyId], from, to, CancellationToken.None);
    }

    public async Task<List<OutboxEvent>> EventsAsync(DomainEventType type)
    {
        await using var scope = Scope(underTheOrg: false);
        return await scope.Db.OutboxEvents.AsNoTracking().Where(e => e.EventType == type).ToListAsync();
    }

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
