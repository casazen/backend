using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class ComplianceWizardService(
    AppDbContext db,
    IConfiguration configuration,
    IAlloggiatiWebService alloggiatiWebService,
    IStayLifecycleService stayLifecycle,
    ITouristTaxQuoteService touristTaxQuoteService,
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

        var steps = await BuildActivationStepsAsync(property, cancellationToken);
        var blockers = steps.Where(s => s.Blocker && s.Status != "complete").ToList();

        if (blockers.Count == 0)
        {
            property.ComplianceStatus = PropertyComplianceStatus.Active;
            property.ComplianceCompletedAt = DateTime.UtcNow;
            logger.LogInformation("Property {PropertyId} compliance activated", propertyId);
        }
        else
        {
            property.ComplianceStatus = PropertyComplianceStatus.Pending;
            property.ComplianceCompletedAt = null;
        }

        property.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return (property, blockers);
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
                incompleteCheckIns.Add(new ComplianceSummaryItem(
                    booking.Id,
                    $"{booking.Guest.FirstName} {booking.Guest.LastName}".Trim(),
                    $"/bookings/{booking.Id}/check-in"));
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
            .Select(b => new ComplianceSummaryItem(b.Id, b.GuestName, $"/bookings/{b.Id}/checkout-wizard"))
            .ToList();

        var (alloggiatiFailures, alloggiatiManualRequired) = await GetAlloggiatiSectionsAsync(orgId, today, cancellationToken);

        return new ComplianceSummaryResult(
            new ComplianceSummarySection(
                pendingProperties.Count,
                pendingProperties.Select(p => new ComplianceSummaryItem(
                    p.Id, p.Name, $"/properties/{p.Id}/compliance/activation")).ToList()),
            new ComplianceSummarySection(incompleteCheckIns.Count, incompleteCheckIns),
            new ComplianceSummarySection(checkoutDue.Count, checkoutDue),
            alloggiatiFailures,
            alloggiatiManualRequired);
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
            var item = new ComplianceSummaryItem(stay.Id, stay.GuestName, $"/bookings/{stay.Id}/alloggiati");

            if (AlloggiatiStatusRules.IsFailure(status))
                failures.Add((stay.CheckInDate, item));
            else if (status == AlloggiatiWebStatus.DaInviareManualmente)
                manual.Add((stay.CheckInDate, item));
        }

        return (Section(failures), Section(manual));

        static ComplianceSummarySection Section(List<(DateTime CheckIn, ComplianceSummaryItem Item)> items) =>
            new(items.Count, items
                .OrderByDescending(i => i.CheckIn)
                .Take(AlloggiatiSectionMaxItems)
                .Select(i => i.Item)
                .ToList());
    }

    public async Task<(Booking Booking, IReadOnlyList<ComplianceActivationStep> Steps)> StartCheckoutWizardAsync(
        Guid bookingId,
        bool registerArrival = false,
        CancellationToken cancellationToken = default)
    {
        // Same rules and transition as the completion and POST /check-out (CO-08).
        var booking = await stayLifecycle.StartCheckOutAsync(bookingId, registerArrival, cancellationToken);
        return (booking, BuildCheckoutSteps(booking));
    }

    public async Task<(Booking Booking, bool PropertyReady)> CompleteCheckoutWizardAsync(
        Guid bookingId,
        string userId,
        CompleteCheckoutWizardInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.ConfirmDeparture)
            throw new DomainRuleException(BookingErrorCodes.DepartureNotConfirmed, "CheckoutDepartureNotConfirmed");

        var turnover = input.SupplierOrgId is { } supplierOrgId
            ? new StayTurnoverRequest(userId, supplierOrgId, input.ServiceCategory, input.ServiceNotes)
            : null;
        var booking = await stayLifecycle.CheckOutAsync(
            bookingId,
            new StayCheckOut(input.RegisterArrival, turnover),
            cancellationToken);
        logger.LogInformation("Checkout wizard completed for booking {BookingId}", bookingId);

        return (booking, true);
    }

    private async Task<Property?> LoadPropertyAsync(Guid propertyId, CancellationToken cancellationToken) =>
        await db.Properties
            .Include(p => p.PropertyDocuments)
            .FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken);

    private async Task<IReadOnlyList<ComplianceActivationStep>> BuildActivationStepsAsync(
        Property property,
        CancellationToken cancellationToken)
    {
        var cinStatus = CinComplianceRules.ResolveStatus(property.CinCode);
        var cinGuidanceUrl = configuration["Compliance:CinGuidanceUrl"]
            ?? ComplianceOptions.DefaultCinGuidanceUrl;

        // Bedrooms are not checked: 0 is a studio flat (monolocale, A2-27).
        var baseComplete = !string.IsNullOrWhiteSpace(property.Name)
            && !string.IsNullOrWhiteSpace(property.Address)
            && !string.IsNullOrWhiteSpace(property.City)
            && property.MaxGuests > 0
            && property.NightlyRate > 0;

        var requiredDocs = ResolveRequiredDocuments(property);
        var uploadedTypes = property.PropertyDocuments
            .Select(d => d.DocumentType.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingDocs = requiredDocs.Where(d => !uploadedTypes.Contains(d)).ToList();
        var docsComplete = missingDocs.Count == 0;

        // D.L. 145/2023 art. 13-ter (CO-07): only the required items of the checklist block.
        var safetyChecklist = await db.PropertySafetyChecklists
            .AsNoTracking()
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.PropertyId == property.Id, cancellationToken);
        var safety = SafetyChecklistRules.Evaluate(safetyChecklist);
        var safetyComplete = safety.IsComplete;

        var regionCode = await ResolveRegionCodeAsync(property.City, cancellationToken);
        var touristTax = await ResolveTouristTaxAsync(property.City, cancellationToken);

        // Any import feed of the property (PC-11: Airbnb, Booking.com, ... each its own feed).
        var icalComplete = await db.PropertyICalFeeds
            .AnyAsync(f => f.PropertyId == property.Id && f.ImportUrl != null && f.ImportUrl != "", cancellationToken);

        return
        [
            new ComplianceActivationStep(
                "base-data",
                "Dati base proprietà",
                baseComplete ? "complete" : "pending",
                true,
                baseComplete ? null : "Completa nome, indirizzo, città e tariffe")
            {
                Blockers = baseComplete ? [] : [new("activation_base_data_incomplete", "ActivationBaseDataIncomplete")],
            },
            new ComplianceActivationStep(
                "cin",
                "Codice CIN",
                cinStatus == "valid" ? "complete" : "pending",
                true,
                cinStatus == "valid" ? null : cinStatus == "missing"
                    ? $"Inserisci il CIN (guida: {cinGuidanceUrl})"
                    : $"Formato CIN non valido (guida: {cinGuidanceUrl})")
            {
                LinkUrl = cinGuidanceUrl,
                Blockers = cinStatus switch
                {
                    "valid" => [],
                    "missing" => [new("activation_cin_missing", "ActivationCinMissing")],
                    _ => [new("activation_cin_invalid", "ActivationCinInvalid")],
                },
            },
            new ComplianceActivationStep(
                "documents",
                "Documenti richiesti",
                docsComplete ? "complete" : "pending",
                true,
                docsComplete ? null : $"Documenti mancanti: {string.Join(", ", missingDocs)}")
            {
                Blockers = docsComplete
                    ? []
                    : [new("activation_documents_missing", "ActivationDocumentsMissing", [string.Join(", ", missingDocs)])],
            },
            new ComplianceActivationStep(
                "safety",
                "Checklist sicurezza",
                safetyComplete ? "complete" : "pending",
                true)
            {
                MessageKey = safetyComplete ? null : "ActivationSafetyIncomplete",
                MessageArgs = safetyComplete ? null : [safety.Blockers.Count],
                Blockers = safety.Blockers.Select(b => new ActivationBlocker(b.Code, b.MessageKey, b.MessageArgs)).ToList(),
            },
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

    private static IReadOnlyList<ComplianceActivationStep> BuildCheckoutSteps(Booking booking)
    {
        var departureConfirmed = booking.CheckoutWizardStartedAt.HasValue;
        var complianceOk = booking.Property.ComplianceStatus == PropertyComplianceStatus.Active;

        return
        [
            new ComplianceActivationStep(
                "confirm-departure",
                "Conferma partenza ospite",
                departureConfirmed ? "complete" : "pending",
                true),
            new ComplianceActivationStep(
                "compliance-summary",
                "Riepilogo compliance",
                complianceOk ? "complete" : "warning",
                false,
                complianceOk ? null : "La proprietà non è ancora pienamente conforme"),
            new ComplianceActivationStep(
                "supplier-selection",
                "Selezione fornitore turnover",
                "pending",
                false),
            new ComplianceActivationStep(
                "payment",
                "Pagamento servizi",
                "pending",
                false),
            new ComplianceActivationStep(
                "property-ready",
                "Proprietà pronta",
                booking.Status == BookingStatus.CheckedOut ? "complete" : "pending",
                true),
        ];
    }

    private IReadOnlyList<string> ResolveRequiredDocuments(Property property)
    {
        var section = configuration.GetSection("Compliance:RequiredDocuments");
        var regionCode = section.GetChildren()
            .Select(c => c.Key)
            .FirstOrDefault(k => k.Equals(property.City, StringComparison.OrdinalIgnoreCase));

        regionCode ??= section.GetChildren()
            .Select(c => c.Key)
            .FirstOrDefault(k => k.Equals("default", StringComparison.OrdinalIgnoreCase))
            ?? "default";

        var docs = section.GetSection(regionCode).Get<string[]>();
        // No safety certificate is required by D.L. 145/2023 art. 13-ter: proofs are optional on the checklist (CO-07).
        return docs is { Length: > 0 } ? docs : ["CinCertificate"];
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
