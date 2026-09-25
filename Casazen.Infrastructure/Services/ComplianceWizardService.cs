using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class ComplianceWizardService(
    AppDbContext db,
    IAlloggiatiWebService alloggiatiWebService,
    IStayLifecycleService stayLifecycle,
    ITouristTaxQuoteService touristTaxQuoteService,
    IPropertyComplianceStatusService complianceStatus,
    ILogger<ComplianceWizardService> logger,
    TimeProvider? timeProvider = null) : IComplianceWizardService
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<(Property Property, IReadOnlyList<ComplianceActivationStep> Steps)> GetActivationWizardAsync(
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var property = await LoadPropertyAsync(propertyId, cancellationToken)
            ?? throw new KeyNotFoundException($"Property {propertyId} not found");

        var steps = await BuildActivationStepsAsync(property, cancellationToken);
        return (property, steps);
    }

    public async Task<(Property Property, IReadOnlyList<ComplianceActivationStep> IncompleteBlockers)> CompleteActivationAsync(
        Guid propertyId,
        string userId,
        bool? tosAccepted,
        CancellationToken cancellationToken = default)
    {
        var property = await LoadPropertyAsync(propertyId, cancellationToken)
            ?? throw new KeyNotFoundException($"Property {propertyId} not found");

        if (tosAccepted != true)
            throw new DomainConflictException("activation_tos_required", "ActivationTosRequired");

        // Same evaluation and transitions as the re-evaluation after a change (CO-06): Active without blockers; with
        // blockers a pending or suspended property stays as it is and an active one is suspended.
        var check = await complianceStatus.ActivateAsync(propertyId, cancellationToken);
        if (check.Status == PropertyComplianceStatus.Active)
            logger.LogInformation("Property {PropertyId} compliance activated", propertyId);

        return (property, check.IncompleteSteps);
    }

    public async Task<ComplianceSummaryResult> GetSummaryAsync(Guid orgId, CancellationToken cancellationToken = default)
    {
        var today = _clock.TodayInRome();

        var pendingProperties = await db.Properties
            .AsNoTracking()
            .Where(p => p.OrgId == orgId && p.ComplianceStatus != PropertyComplianceStatus.Active)
            .OrderBy(p => p.Name)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(cancellationToken);

        var checkInCandidates = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Where(b => b.OrgId == orgId)
            .Where(b => b.Status == BookingStatus.Confirmed || b.Status == BookingStatus.CheckedIn)
            .Where(b => b.CheckInDate.Date <= today.AddDays(1))
            .ToListAsync(cancellationToken);

        var incompleteCheckIns = new List<ComplianceSummaryItem>();
        foreach (var booking in checkInCandidates)
        {
            // Every guest of the stay, not only the booker (CO-12).
            var dataComplete = await alloggiatiWebService.IsStayDataCompleteAsync(booking.Id);
            if (!dataComplete)
            {
                incompleteCheckIns.Add(ComplianceSummaryItem.ForBooking(
                    ComplianceCockpitAction.CompleteGuestCheckIn,
                    booking.Id,
                    $"{booking.Guest.FirstName} {booking.Guest.LastName}".Trim()));
            }
        }

        // Departures of today (Europe/Rome), with or without the arrival registered, and checked-in stays not closed
        // (CO-08, A5-08): until then only checked-in stays counted and none could be checked in from the apps.
        var checkoutDue = (await db.Bookings
                .AsNoTracking()
                .Where(b => b.OrgId == orgId)
                .Where(StayLifecycleRules.CheckOutDue(today))
                .OrderBy(b => b.CheckOutDate)
                .Select(b => new { b.Id, GuestName = (b.Guest.FirstName + " " + b.Guest.LastName).Trim() })
                .ToListAsync(cancellationToken))
            .Select(b => ComplianceSummaryItem.ForBooking(ComplianceCockpitAction.CheckOut, b.Id, b.GuestName))
            .ToList();

        var (alloggiatiFailures, alloggiatiManualRequired) = await GetAlloggiatiSectionsAsync(orgId, today, cancellationToken);
        var turnoversPending = await GetTurnoversPendingAsync(orgId, cancellationToken);

        return new ComplianceSummaryResult(
            new ComplianceSummarySection(
                pendingProperties.Count,
                // Pending and suspended alike: the activation wizard opens on the first blocking step still open.
                pendingProperties.Select(p => ComplianceSummaryItem.ForProperty(
                    ComplianceCockpitAction.ActivateProperty, p.Id, p.Name)).ToList()),
            new ComplianceSummarySection(incompleteCheckIns.Count, incompleteCheckIns),
            new ComplianceSummarySection(checkoutDue.Count, checkoutDue),
            alloggiatiFailures,
            alloggiatiManualRequired,
            turnoversPending);
    }

    /// <summary>Most recent turnovers listed; the count covers all of them.</summary>
    internal const int TurnoverSectionMaxItems = 10;

    /// <summary>
    /// Turnovers still open (CO-17): stays checked out whose property was not declared ready, from the check-out record
    /// (a stay closed before CO-17 has none and is not listed). A later stay of the same property whose arrival is
    /// registered closes it: the property was obviously ready for it.
    /// </summary>
    private async Task<ComplianceSummarySection> GetTurnoversPendingAsync(Guid orgId, CancellationToken cancellationToken)
    {
        var pending = db.StayCheckouts
            .AsNoTracking()
            .Where(c => c.OrgId == orgId && c.CompletedAt != null && c.PropertyReadyAt == null)
            .Where(c => c.Booking.Status == BookingStatus.CheckedOut)
            .Where(c => !db.Bookings.Any(next =>
                next.PropertyId == c.Booking.PropertyId
                && next.Id != c.BookingId
                && (next.Status == BookingStatus.CheckedIn || next.Status == BookingStatus.CheckedOut)
                && next.CheckInDate >= c.Booking.CheckOutDate));

        var count = await pending.CountAsync(cancellationToken);
        var items = (await pending
                .OrderByDescending(c => c.CompletedAt)
                .Take(TurnoverSectionMaxItems)
                .Select(c => new
                {
                    c.BookingId,
                    PropertyName = c.Booking.Property.Name,
                    GuestName = (c.Booking.Guest.FirstName + " " + c.Booking.Guest.LastName).Trim(),
                })
                .ToListAsync(cancellationToken))
            .Select(c => ComplianceSummaryItem.ForBooking(
                ComplianceCockpitAction.ConfirmPropertyReady,
                c.BookingId,
                string.IsNullOrWhiteSpace(c.GuestName) ? c.PropertyName : $"{c.PropertyName} · {c.GuestName}"))
            .ToList();

        return new ComplianceSummarySection(count, items);
    }

    /// <summary>Most recent items listed per Alloggiati section; the count covers all of them.</summary>
    internal const int AlloggiatiSectionMaxItems = 10;

    /// <summary>
    /// Alloggiati sections of the cockpit (CO-11): errors/rejections, and communications the host must send on the
    /// portal. Every stay whose arrival day has come is counted until it is sent with a receipt or declared sent by
    /// the host, with or without a report row: CasaZen does not transmit, so nothing turns "done" on its own.
    /// </summary>
    private async Task<(ComplianceSummarySection Failures, ComplianceSummarySection ManualRequired)> GetAlloggiatiSectionsAsync(
        Guid orgId,
        DateTime today,
        CancellationToken cancellationToken)
    {
        var stays = await db.Bookings
            .AsNoTracking()
            .Where(b => b.OrgId == orgId)
            .Where(b => b.Status == BookingStatus.Confirmed
                || b.Status == BookingStatus.CheckedIn
                || b.Status == BookingStatus.CheckedOut)
            .Where(b => b.CheckInDate <= today)
            .Select(b => new
            {
                b.Id,
                b.GuestId,
                b.CheckInDate,
                GuestName = (b.Guest.FirstName + " " + b.Guest.LastName).Trim(),
            })
            .ToListAsync(cancellationToken);

        var stayIds = stays.Select(b => b.Id).ToList();
        var reports = (await db.AlloggiatiWebReports
                .AsNoTracking()
                .Where(r => stayIds.Contains(r.BookingId))
                .Select(r => new { r.BookingId, r.GuestId, r.Status, r.UpdatedAt })
                .ToListAsync(cancellationToken))
            .ToLookup(r => r.BookingId);

        var failures = new List<(DateTime CheckIn, ComplianceSummaryItem Item)>();
        var manual = new List<(DateTime CheckIn, ComplianceSummaryItem Item)>();
        foreach (var stay in stays)
        {
            var ofStay = reports[stay.Id].ToList();
            var report = ofStay.FirstOrDefault(r => r.GuestId == stay.GuestId)
                ?? ofStay.OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
            var status = AlloggiatiStatusRules.Effective(report?.Status, stay.CheckInDate, today);
            if (AlloggiatiStatusRules.IsFailure(status))
            {
                failures.Add((stay.CheckInDate, ComplianceSummaryItem.ForBooking(
                    ComplianceCockpitAction.ResolveAlloggiatiFailure, stay.Id, stay.GuestName)));
            }
            else if (status == AlloggiatiWebStatus.DaInviareManualmente)
            {
                manual.Add((stay.CheckInDate, ComplianceSummaryItem.ForBooking(
                    ComplianceCockpitAction.SendAlloggiati, stay.Id, stay.GuestName)));
            }
        }

        return (Section(failures), Section(manual));

        static ComplianceSummarySection Section(List<(DateTime CheckIn, ComplianceSummaryItem Item)> items) =>
            new(items.Count, items
                .OrderByDescending(i => i.CheckIn)
                .Take(AlloggiatiSectionMaxItems)
                .Select(i => i.Item)
                .ToList());
    }

    public async Task<CheckoutWizardState> StartCheckoutWizardAsync(
        Guid bookingId,
        bool registerArrival = false,
        CancellationToken cancellationToken = default)
    {
        // Same rules and transition as the completion and POST /check-out (CO-08).
        await stayLifecycle.StartCheckOutAsync(bookingId, registerArrival, cancellationToken);
        return await BuildCheckoutStateAsync(bookingId, cancellationToken);
    }

    public Task<CheckoutWizardState> GetCheckoutWizardAsync(Guid bookingId, CancellationToken cancellationToken = default) =>
        BuildCheckoutStateAsync(bookingId, cancellationToken);

    public async Task<CheckoutWizardState> SaveCheckoutProgressAsync(
        Guid bookingId,
        StayCheckoutProgress progress,
        CancellationToken cancellationToken = default)
    {
        await stayLifecycle.SaveCheckoutProgressAsync(bookingId, progress, cancellationToken);
        return await BuildCheckoutStateAsync(bookingId, cancellationToken);
    }

    public async Task<CheckoutWizardState> CompleteCheckoutWizardAsync(
        Guid bookingId,
        string userId,
        CompleteCheckoutWizardInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.ConfirmDeparture)
            throw new DomainRuleException(BookingErrorCodes.DepartureNotConfirmed, "CheckoutDepartureNotConfirmed");

        // Before CO-17 a supplier alone meant "request": the app still sends it that way.
        var cleaning = input.CleaningChoice ?? (input.SupplierOrgId is null ? null : CheckoutCleaningChoice.Request);
        if ((cleaning == CheckoutCleaningChoice.Request && input.SupplierOrgId is null)
            || (cleaning == CheckoutCleaningChoice.Skip && input.SupplierOrgId is not null))
            throw new DomainRuleException(BookingErrorCodes.CleaningChoiceInvalid, "CheckoutCleaningChoiceInvalid");

        var turnover = cleaning == CheckoutCleaningChoice.Request && input.SupplierOrgId is { } supplierOrgId
            ? new StayTurnoverRequest(userId, supplierOrgId, input.ServiceCategory, input.ServiceNotes)
            : null;

        var checkOut = new StayCheckOut(input.RegisterArrival, turnover)
        {
            Wizard = new StayCheckoutDeclaration(
                cleaning == CheckoutCleaningChoice.Skip,
                input.TouristTaxCollection,
                input.PropertyReady == true,
                input.PropertyNotes),
        };
        await stayLifecycle.CheckOutAsync(bookingId, checkOut, cancellationToken);

        var state = await BuildCheckoutStateAsync(bookingId, cancellationToken);
        logger.LogInformation(
            "Checkout wizard completed for booking {BookingId} (cleaning: {Cleaning}, tourist tax: {TouristTax}, property ready: {PropertyReady})",
            bookingId,
            cleaning,
            input.TouristTaxCollection,
            state.PropertyReady);
        return state;
    }

    public async Task<CheckoutWizardState> ConfirmPropertyReadyAsync(
        Guid bookingId,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        await stayLifecycle.ConfirmPropertyReadyAsync(bookingId, notes, cancellationToken);
        return await BuildCheckoutStateAsync(bookingId, cancellationToken);
    }

    /// <summary>The wizard as committed now: the booking, its check-out record and what each step shows.</summary>
    private async Task<CheckoutWizardState> BuildCheckoutStateAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        var booking = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
            ?? throw new NotFoundException($"Booking {bookingId} not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };
        var checkout = await db.StayCheckouts
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.BookingId == bookingId, cancellationToken);

        var alloggiatiStatus = await alloggiatiWebService.GetStatusAsync(bookingId);
        var alloggiati = new CheckoutAlloggiatiSummary(
            alloggiatiStatus.Status,
            AlloggiatiStatusRules.IsSent(alloggiatiStatus.Status),
            alloggiatiStatus.DeadlineAt,
            alloggiatiStatus.IsOverdue,
            alloggiatiStatus.DataComplete);
        var touristTax = new CheckoutTouristTaxSummary(
            RecordedTouristTax(booking),
            await IsTouristTaxCollectedOnlineAsync(booking, cancellationToken),
            checkout?.TouristTaxCollection);

        return new CheckoutWizardState(
            booking,
            checkout,
            BuildCheckoutSteps(booking, checkout, alloggiati),
            alloggiati,
            touristTax);
    }

    /// <summary>
    /// The tax CasaZen recorded on the booking (BK-03): only bookings it priced (booking site, host booking) and only an
    /// amount above 0. A channel booking or a comune without a rate records 0, which is not the tax of the stay.
    /// </summary>
    private static decimal? RecordedTouristTax(Booking booking) =>
        booking.Source is BookingSource.Direct or BookingSource.Manual && booking.TouristTax > 0
            ? booking.TouristTax
            : null;

    /// <summary>
    /// The tax was in the total paid online on the booking site (BK-03: the direct booking charges it with the stay):
    /// a booking-site booking not paid at the property, with a Stripe payment collected.
    /// </summary>
    private async Task<bool> IsTouristTaxCollectedOnlineAsync(Booking booking, CancellationToken cancellationToken)
    {
        if (booking.Source != BookingSource.Direct || booking.PaymentOption == PaymentOption.OnSite || booking.TouristTax <= 0)
            return false;

        return await db.Payments
            .AsNoTracking()
            .AnyAsync(p => p.BookingId == booking.Id
                           && p.StripePaymentIntentId != null
                           && (p.Status == PaymentStatus.Completed || p.Status == PaymentStatus.PartiallyRefunded),
                cancellationToken);
    }

    // The documents and the checklist are read by the evaluation itself (IPropertyComplianceStatusService).
    private async Task<Property?> LoadPropertyAsync(Guid propertyId, CancellationToken cancellationToken) =>
        await db.Properties.FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken);

    private async Task<IReadOnlyList<ComplianceActivationStep>> BuildActivationStepsAsync(
        Property property,
        CancellationToken cancellationToken)
    {
        // Base data, CIN, documents and safety checklist: the single evaluation shared with the activation, the
        // re-evaluation after every change and the nightly check (CO-06).
        var blockingSteps = await complianceStatus.GetBlockingStepsAsync(property, cancellationToken);

        var regionCode = await ResolveRegionCodeAsync(property.City, cancellationToken);
        var touristTax = await ResolveTouristTaxAsync(property.City, cancellationToken);

        // Any import feed of the property (PC-11: Airbnb, Booking.com, ... each its own feed).
        var icalComplete = await db.PropertyICalFeeds
            .AnyAsync(f => f.PropertyId == property.Id && f.ImportUrl != null && f.ImportUrl != "", cancellationToken);

        return
        [
            .. blockingSteps,
            BuildTouristTaxStep(touristTax),
            new ComplianceActivationStep(
                "ical",
                "Sincronizzazione calendario",
                icalComplete ? "complete" : "warning",
                false,
                icalComplete ? null : "Consigliato: collega feed iCal OTA"),
        ];
    }

    /// <summary>
    /// Tourist tax step (PLANNING Wizard 1, step 5): a warning, never a blocker (A5-06). It shows the rate of the
    /// comune when CasaZen has one in force today, otherwise a warning with the public page of the comune, if any.
    /// </summary>
    private static ComplianceActivationStep BuildTouristTaxStep(TouristTaxActivationInfo touristTax)
    {
        var step = new ComplianceActivationStep(
            "tourist-tax",
            "Imposta di soggiorno",
            touristTax.Rate is null ? "warning" : "complete",
            false)
        {
            TouristTax = touristTax,
        };

        if (touristTax.Rate is not null)
            return step;

        if (string.IsNullOrWhiteSpace(touristTax.City))
            return step with { MessageKey = "ActivationTouristTaxCityMissing" };

        return touristTax.CategoryRequired
            ? step with { MessageKey = "ActivationTouristTaxCategoryRequired", MessageArgs = [touristTax.City] }
            : step with { MessageKey = "ActivationTouristTaxNoRate", MessageArgs = [touristTax.City] };
    }

    private async Task<TouristTaxActivationInfo> ResolveTouristTaxAsync(
        string? propertyCity,
        CancellationToken cancellationToken)
    {
        var city = propertyCity?.Trim() ?? string.Empty;
        if (city.Length == 0)
            return new TouristTaxActivationInfo(city, null, null);

        // Same lookup as the checkout and the calculator (BK-03): comune by normalized name, rate in force today in
        // Europe/Rome and in season. The property has no accommodation category, so category rates do not apply.
        var today = _clock.TodayInRomeAsDateOnly();
        var ratesInForce = await touristTaxQuoteService.GetRatesInForceAsync(
            new TouristTaxComune(null, city), today, cancellationToken);
        var rate = TouristTaxCalculator.RateFor(ratesInForce, today);
        var categoryRequired = rate is null
            && ratesInForce.Any(r => !string.IsNullOrWhiteSpace(r.AccommodationCategory));

        // Only a reviewed page is public in production (PublicContentController); the wizard never links a draft.
        string? publicPageSlug = null;
        var comune = ItalianComuneRegistry.GetByName(city);
        if (comune is not null)
        {
            var hasPublicPage = await db.SeoContentPages
                .AsNoTracking()
                .AnyAsync(p => p.PageType == SeoPageType.TouristTaxCalc
                               && p.ComuneCode == comune.Code
                               && p.LegalReviewStatus == LegalReviewStatus.Reviewed,
                    cancellationToken);
            if (hasPublicPage)
                publicPageSlug = comune.ComuneSlug;
        }

        return new TouristTaxActivationInfo(city, rate, publicPageSlug) { CategoryRequired = categoryRequired };
    }

    /// <summary>
    /// The 5 steps of the check-out wizard (CO-17): stay summary and departure, Alloggiati, cleaning, tourist tax,
    /// property ready. None blocks the check-out except the confirmation of the departure: what is left open is shown
    /// (warning) and, for the property, stays in the cockpit as a turnover to close.
    /// </summary>
    private static IReadOnlyList<ComplianceActivationStep> BuildCheckoutSteps(
        Booking booking,
        StayCheckout? checkout,
        CheckoutAlloggiatiSummary alloggiati)
    {
        var checkedOut = booking.Status == BookingStatus.CheckedOut;

        var departure = new ComplianceActivationStep(
            CheckoutWizardSteps.StaySummary,
            "Riepilogo soggiorno",
            checkedOut || checkout?.DepartureConfirmed == true ? "complete" : "pending",
            true)
        { LabelKey = "CheckoutStepStaySummary" };

        var alloggiatiStep = new ComplianceActivationStep(
            CheckoutWizardSteps.Alloggiati,
            "Alloggiati Web",
            alloggiati.Sent ? "complete" : "warning",
            false)
        {
            LabelKey = "CheckoutStepAlloggiati",
            MessageKey = alloggiati.Sent
                ? null
                : AlloggiatiStatusRules.IsFailure(alloggiati.Status)
                    ? "CheckoutAlloggiatiFailed"
                    : "CheckoutAlloggiatiNotSent",
        };

        var cleaningChosen = checkout?.CleaningChoice is not null || checkout?.CleaningRequestId is not null;
        var cleaning = new ComplianceActivationStep(
            CheckoutWizardSteps.Cleaning,
            "Pulizie",
            cleaningChosen ? "complete" : checkedOut ? "warning" : "pending",
            false)
        { LabelKey = "CheckoutStepCleaning" };

        var taxDeclared = checkout?.TouristTaxCollection is not null;
        var touristTax = new ComplianceActivationStep(
            CheckoutWizardSteps.TouristTax,
            "Imposta di soggiorno",
            taxDeclared ? "complete" : checkedOut ? "warning" : "pending",
            false)
        {
            LabelKey = "CheckoutStepTouristTax",
            MessageKey = !taxDeclared && checkedOut ? "CheckoutTouristTaxNotDeclared" : null,
        };

        var ready = checkout?.PropertyReadyAt is not null;
        var propertyReady = new ComplianceActivationStep(
            CheckoutWizardSteps.PropertyReady,
            "Proprietà pronta",
            ready ? "complete" : checkedOut ? "warning" : "pending",
            false)
        {
            LabelKey = "CheckoutStepPropertyReady",
            MessageKey = !ready && checkedOut ? "CheckoutPropertyNotReady" : null,
        };

        return [departure, alloggiatiStep, cleaning, touristTax, propertyReady];
    }

    private async Task<string> ResolveRegionCodeAsync(string city, CancellationToken cancellationToken)
    {
        var rate = await db.TouristTaxRates
            .AsNoTracking()
            .Where(t => t.IsActive && t.City.ToLower() == city.ToLower())
            .Select(t => t.RegionCode)
            .FirstOrDefaultAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(rate) ? "default" : rate;
    }
}
