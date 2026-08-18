using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

public class ComplianceWizardService(
    AppDbContext db,
    IConfiguration configuration,
    IAlloggiatiWebService alloggiatiWebService,
    IServiceRequestService serviceRequestService,
    ILogger<ComplianceWizardService> logger) : IComplianceWizardService
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<(Property Property, IReadOnlyList<ComplianceActivationStep> Steps)> GetActivationWizardAsync(
        Guid propertyId,
        CancellationToken cancellationToken = default)
    {
        var property = await LoadPropertyAsync(propertyId, cancellationToken)
            ?? throw new KeyNotFoundException($"Property {propertyId} not found");

        var steps = await BuildActivationStepsAsync(property, cancellationToken);
        return (property, steps);
    }

    public async Task<(Property Property, IReadOnlyList<string> IncompleteBlockers)> CompleteActivationAsync(
        Guid propertyId,
        string userId,
        PropertySafetyChecklistInput? safetyChecklist,
        bool? tosAccepted,
        CancellationToken cancellationToken = default)
    {
        var property = await LoadPropertyAsync(propertyId, cancellationToken)
            ?? throw new KeyNotFoundException($"Property {propertyId} not found");

        if (safetyChecklist is not null)
        {
            property.SafetyChecklistJson = JsonSerializer.Serialize(new
            {
                smokeDetector = safetyChecklist.SmokeDetector,
                fireExtinguisher = safetyChecklist.FireExtinguisher,
                gasCompliance = safetyChecklist.GasCompliance,
                acknowledgedAt = DateTime.UtcNow,
                acknowledgedBy = safetyChecklist.AcknowledgedBy ?? userId,
            }, JsonOpts);
            property.UpdatedAt = DateTime.UtcNow;
        }

        if (tosAccepted == false)
            throw new InvalidOperationException("Devi accettare i termini di servizio");

        var steps = await BuildActivationStepsAsync(property, cancellationToken);
        var blockers = steps.Where(s => s.Blocker && s.Status != "complete").Select(s => s.Id).ToList();

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
        var now = DateTime.UtcNow;
        var today = now.Date;

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
            var dataComplete = await alloggiatiWebService.ValidateGuestDataAsync(booking.GuestId);
            if (!dataComplete)
            {
                incompleteCheckIns.Add(new ComplianceSummaryItem(
                    booking.Id,
                    $"{booking.Guest.FirstName} {booking.Guest.LastName}".Trim(),
                    $"/bookings/{booking.Id}/check-in"));
            }
        }

        var checkoutCandidates = await db.Bookings
            .AsNoTracking()
            .Include(b => b.Guest)
            .Where(b => b.OrgId == orgId)
            .Where(b => b.Status == BookingStatus.CheckedIn)
            .OrderBy(b => b.CheckOutDate)
            .ToListAsync(cancellationToken);

        var checkoutDue = checkoutCandidates
            .Where(b => b.CheckOutDate.Date <= today)
            .Select(b => new ComplianceSummaryItem(
                b.Id,
                $"{b.Guest.FirstName} {b.Guest.LastName}".Trim(),
                $"/bookings/{b.Id}/checkout-wizard"))
            .ToList();

        var alloggiatiFailures = await db.AlloggiatiWebReports
            .AsNoTracking()
            .Include(r => r.Booking)
            .ThenInclude(b => b.Guest)
            .Where(r => r.Booking.OrgId == orgId)
            .Where(r => r.Status == AlloggiatiWebStatus.Failed)
            .OrderByDescending(r => r.UpdatedAt)
            .Select(r => new ComplianceSummaryItem(
                r.BookingId,
                $"{r.Booking.Guest.FirstName} {r.Booking.Guest.LastName}".Trim(),
                $"/bookings/{r.BookingId}/alloggiati"))
            .ToListAsync(cancellationToken);

        return new ComplianceSummaryResult(
            new ComplianceSummarySection(
                pendingProperties.Count,
                pendingProperties.Select(p => new ComplianceSummaryItem(
                    p.Id, p.Name, $"/properties/{p.Id}/compliance/activation")).ToList()),
            new ComplianceSummarySection(incompleteCheckIns.Count, incompleteCheckIns),
            new ComplianceSummarySection(checkoutDue.Count, checkoutDue),
            new ComplianceSummarySection(alloggiatiFailures.Count, alloggiatiFailures));
    }

    public async Task<(Booking Booking, IReadOnlyList<ComplianceActivationStep> Steps)> StartCheckoutWizardAsync(
        Guid bookingId,
        CancellationToken cancellationToken = default)
    {
        var booking = await db.Bookings
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
            ?? throw new KeyNotFoundException($"Booking {bookingId} not found");

        if (!CanCompleteCheckout(booking))
        {
            throw new InvalidOperationException(
                $"Il check-out richiede una prenotazione in check-in. Stato attuale: {booking.Status}.");
        }

        booking.CheckoutWizardStartedAt ??= DateTime.UtcNow;
        booking.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var steps = BuildCheckoutSteps(booking);
        return (booking, steps);
    }

    public async Task<(Booking Booking, bool PropertyReady)> CompleteCheckoutWizardAsync(
        Guid bookingId,
        string userId,
        CompleteCheckoutWizardInput input,
        CancellationToken cancellationToken = default)
    {
        if (!input.ConfirmDeparture)
            throw new InvalidOperationException("Conferma che l'ospite ha lasciato la struttura.");

        var booking = await db.Bookings
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
            ?? throw new KeyNotFoundException($"Booking {bookingId} not found");

        if (!CanCompleteCheckout(booking))
            throw new InvalidOperationException(
                $"Il check-out richiede una prenotazione in check-in. Stato attuale: {booking.Status}.");

        if (input.SupplierOrgId.HasValue)
        {
            await serviceRequestService.CreateAsync(new CreateServiceRequestCommand(
                booking.OrgId,
                userId,
                booking.PropertyId,
                booking.Id,
                input.SupplierOrgId.Value,
                input.ServiceCategory ?? "cleaning",
                ServiceRequestUrgency.Normal,
                input.ServiceNotes,
                ChargeToGuest: false), cancellationToken);
        }

        booking.Status = BookingStatus.CheckedOut;
        booking.CheckoutReminderJobId = null;
        booking.UpdatedAt = DateTime.UtcNow;

        var retentionYears = configuration.GetValue("Compliance:GdprRetentionYears", 7);
        booking.Guest.DataRetentionUntil = booking.CheckOutDate.AddYears(retentionYears);
        booking.Guest.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
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
            ?? "https://www.bdsr.it/cin";

        var baseComplete = !string.IsNullOrWhiteSpace(property.Name)
            && !string.IsNullOrWhiteSpace(property.Address)
            && !string.IsNullOrWhiteSpace(property.City)
            && property.Bedrooms > 0
            && property.MaxGuests > 0
            && property.NightlyRate > 0;

        var requiredDocs = ResolveRequiredDocuments(property);
        var uploadedTypes = property.PropertyDocuments
            .Select(d => d.DocumentType.ToString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingDocs = requiredDocs.Where(d => !uploadedTypes.Contains(d)).ToList();
        var docsComplete = missingDocs.Count == 0;

        var safety = ParseSafetyChecklist(property.SafetyChecklistJson);
        var safetyComplete = safety.SmokeDetector && safety.FireExtinguisher && safety.GasCompliance
            && safety.AcknowledgedAt.HasValue;

        var regionCode = await ResolveRegionCodeAsync(property.City, cancellationToken);
        var touristTaxConfigured = await db.TouristTaxRates
            .AsNoTracking()
            .AnyAsync(t => t.IsActive && t.City.ToLower() == property.City.ToLower(), cancellationToken);

        var icalFeed = await db.PropertyICalFeeds
            .AsNoTracking()
            .FirstOrDefaultAsync(f => f.PropertyId == property.Id, cancellationToken);
        var icalComplete = !string.IsNullOrWhiteSpace(icalFeed?.ImportUrl);

        return
        [
            new ComplianceActivationStep(
                "base-data",
                "Dati base proprietà",
                baseComplete ? "complete" : "pending",
                true,
                baseComplete ? null : "Completa nome, indirizzo, città e tariffe"),
            new ComplianceActivationStep(
                "cin",
                "Codice CIN",
                cinStatus == "valid" ? "complete" : "pending",
                true,
                cinStatus == "valid" ? null : cinStatus == "missing"
                    ? $"Inserisci il CIN (guida: {cinGuidanceUrl})"
                    : $"Formato CIN non valido (guida: {cinGuidanceUrl})"),
            new ComplianceActivationStep(
                "documents",
                "Documenti richiesti",
                docsComplete ? "complete" : "pending",
                true,
                docsComplete ? null : $"Documenti mancanti: {string.Join(", ", missingDocs)}"),
            new ComplianceActivationStep(
                "safety",
                "Checklist sicurezza",
                safetyComplete ? "complete" : "pending",
                true,
                safetyComplete ? null : "Conferma rilevatori, estintore e conformità gas"),
            new ComplianceActivationStep(
                "tourist-tax",
                "Imposta di soggiorno",
                touristTaxConfigured ? "complete" : "pending",
                true,
                touristTaxConfigured ? null : $"Configura aliquota per {property.City}"),
            new ComplianceActivationStep(
                "ical",
                "Sincronizzazione calendario",
                icalComplete ? "complete" : "warning",
                false,
                icalComplete ? null : "Consigliato: collega feed iCal OTA"),
        ];
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

    private static bool CanCompleteCheckout(Booking booking)
    {
        var today = DateTime.UtcNow.Date;
        return booking.Status == BookingStatus.CheckedIn
            || (booking.Status == BookingStatus.Confirmed && booking.CheckOutDate.Date <= today);
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
        return docs is { Length: > 0 } ? docs : ["CinCertificate", "SafetyCompliance"];
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

    private static (bool SmokeDetector, bool FireExtinguisher, bool GasCompliance, DateTime? AcknowledgedAt) ParseSafetyChecklist(
        string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (false, false, false, null);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return (
                root.TryGetProperty("smokeDetector", out var sd) && sd.GetBoolean(),
                root.TryGetProperty("fireExtinguisher", out var fe) && fe.GetBoolean(),
                root.TryGetProperty("gasCompliance", out var gc) && gc.GetBoolean(),
                root.TryGetProperty("acknowledgedAt", out var at) && at.ValueKind == JsonValueKind.String
                    ? DateTime.Parse(at.GetString()!)
                    : null);
        }
        catch
        {
            return (false, false, false, null);
        }
    }
}
