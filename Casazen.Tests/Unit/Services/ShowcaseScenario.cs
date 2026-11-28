using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Unit.Services;

internal static class ShowcaseScenario
{
    public const string Slug = "vetrina-test";
    public const string City = "Monza";
    public const string PostalCode = "20900";
    public const string Address = "Via Segretissima 7";
    public const string Floor = "Piano 3, interno 7";
    public const string AccessNotes = "Citofono Rossi, chiavi nella cassetta";
    public const string CustomerName = "Mario Rossi";
    public const string CustomerEmail = "mario.rossi@example.com";
    public const string CustomerPhone = "+39 333 123 4567";
    public const string ConsentIp = "203.0.113.7";

    public static async Task<SupplierProfile> EnableBookingAsync(this ServiceRequestScenario s, bool onlineBooking = true)
    {
        var profile = await s.Db.SupplierProfiles.SingleAsync(sp => sp.OrgId == s.SupplierOrgId);
        profile.ShowcaseSlug = Slug;
        profile.ComuniJson = $"[\"{ServiceRequestScenario.Comune}\",\"{City}\"]";
        var settings = await s.Db.SupplierSettings.SingleAsync(x => x.OrgId == s.SupplierOrgId);
        settings.OnlineBookingEnabled = onlineBooking;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
        return await s.SupplierAsync();
    }

    public static Task<SupplierProfile> SupplierAsync(this ServiceRequestScenario s, Guid? orgId = null) =>
        s.Db.SupplierProfiles.AsNoTracking().SingleAsync(sp => sp.OrgId == (orgId ?? s.SupplierOrgId));

    /// <summary>The public slug of a service of the catalog (the main one of the scenario unless another is given).</summary>
    public static async Task<string> ListingSlugAsync(this ServiceRequestScenario s, Guid? listingId = null) =>
        (await s.Db.SupplierServiceListings.AsNoTracking().SingleAsync(l => l.Id == (listingId ?? s.ListingId))).Slug;

    public static async Task<ShowcaseBookingInput> InputAsync(
        this ServiceRequestScenario s,
        DateTime? start = null,
        string? email = CustomerEmail,
        Guid? clientRequestId = null,
        string? service = null,
        Func<ShowcaseBookingInput, ShowcaseBookingInput>? change = null)
    {
        var input = new ShowcaseBookingInput(
            clientRequestId ?? Guid.NewGuid(),
            service ?? await s.ListingSlugAsync(),
            start ?? ServiceRequestScenario.FridayAt10,
            null,
            null,
            null,
            null,
            City,
            PostalCode,
            Address,
            Floor,
            AccessNotes,
            CustomerName,
            email,
            CustomerPhone,
            "it",
            true,
            ServiceRequestTestKit.PrivacyNoticeVersion,
            ConsentIp);
        return change is null ? input : change(input);
    }

    public static async Task<(ShowcaseBookingHoldResult Hold, string Token)> HoldAsync(
        this ServiceRequestScenario s,
        ShowcaseBookingInput? input = null,
        SupplierProfile? supplier = null)
    {
        supplier ??= await s.SupplierAsync();
        s.ForgetNotifications();
        var hold = await s.Kit.Booking.CreateHoldAsync(supplier, input ?? await s.InputAsync());
        return (hold, s.VerificationTokenOf(hold.Id));
    }

    public static string VerificationTokenOf(this ServiceRequestScenario s, Guid holdId)
    {
        var email = s.Emails.Snapshot().Last(e => e.Template == Casazen.Infrastructure.Email.Templates.EmailTemplates.Names.SupplierBookingVerification);
        var match = Regex.Match(email.Content.HtmlBody, $"hold={holdId:D}#token=([A-Za-z0-9_-]+)");
        Xunit.Assert.True(match.Success, "the verification e-mail has no link with the hold and the token");
        return match.Groups[1].Value;
    }

    public static async Task<(ServiceRequest Request, ShowcaseBookingConfirmation Confirmation)> BookedAsync(
        this ServiceRequestScenario s,
        ShowcaseBookingInput? input = null,
        SupplierProfile? supplier = null)
    {
        supplier ??= await s.SupplierAsync();
        var (hold, token) = await s.HoldAsync(input, supplier);
        var confirmation = await s.Kit.Booking.ConfirmEmailAsync(supplier, hold.Id, token);
        var supplierOrgId = supplier.OrgId;
        var request = await s.Db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(r => r.PublicCode == confirmation.PublicCode && r.SupplierOrgId == supplierOrgId);
        return (request, confirmation);
    }

    public static Task<List<ServiceRequest>> ShowcaseRequestsAsync(this ServiceRequestScenario s) =>
        s.Db.ServiceRequests
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(r => r.RentalContext == ServiceRequestRentalContext.Showcase)
            .OrderBy(r => r.CreatedAt)
            .ThenBy(r => r.Id)
            .ToListAsync();

    public static async Task<(Guid OrgId, SupplierProfile Profile, string ServiceSlug)> AddBookableSupplierAsync(this ServiceRequestScenario s)
    {
        var orgId = await s.AddOtherSupplierAsync();
        await s.Agenda.ReplaceRulesAsync(orgId, new SupplierRulesInput(30, 3, 0, 35, 60));
        await s.Agenda.ReplaceHoursAsync(orgId, ServiceRequestScenario.WorkingWeek());
        var listingId = await s.AddListingAsync(ServiceRequestScenario.ServiceName, supplierOrgId: orgId);

        var profile = await s.Db.SupplierProfiles.SingleAsync(sp => sp.OrgId == orgId);
        profile.ShowcaseSlug = "altra-vetrina";
        profile.ComuniJson = $"[\"{ServiceRequestScenario.Comune}\",\"{City}\"]";
        var settings = await s.Db.SupplierSettings.SingleAsync(x => x.OrgId == orgId);
        settings.OnlineBookingEnabled = true;
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();

        return (orgId, await s.SupplierAsync(orgId), await s.ListingSlugAsync(listingId));
    }

    public static async Task SetSupplementsAsync(this ServiceRequestScenario s, Guid listingId, params SupplierServiceSupplement[] supplements)
    {
        var listing = await s.Db.SupplierServiceListings.SingleAsync(l => l.Id == listingId);
        listing.SupplementsJson = SupplierServiceListingJson.Serialize(supplements);
        await s.Db.SaveChangesAsync();
        s.Db.ChangeTracker.Clear();
    }

    public static Task<List<ShowcaseBookingHold>> HoldsAsync(this ServiceRequestScenario s, Guid? supplierOrgId = null) =>
        s.Db.ShowcaseBookingHolds.AsNoTracking().Where(h => h.OrgId == (supplierOrgId ?? s.SupplierOrgId)).OrderBy(h => h.CreatedAt).ToListAsync();

    public static Task<List<ServiceCustomer>> CustomersAsync(this ServiceRequestScenario s, Guid? supplierOrgId = null) =>
        s.Db.ServiceCustomers.AsNoTracking().Where(c => c.OrgId == (supplierOrgId ?? s.SupplierOrgId)).OrderBy(c => c.CreatedAt).ToListAsync();

    public static async Task<IReadOnlyList<DateTime>> FreeSlotsOfFridayAsync(this ServiceRequestScenario s, Guid? supplierOrgId = null)
    {
        var day = new DateOnly(2026, 10, 9);
        var plans = await s.Agenda.PlanAsync(
            supplierOrgId ?? s.SupplierOrgId,
            day,
            day,
            new SupplierSlotQuery(ServiceRequestScenario.ServiceMinutes));
        return plans.SelectMany(plan => plan.Slots).Select(slot => slot.StartUtc).ToList();
    }
}
