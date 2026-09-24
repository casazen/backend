using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Email;
using Casazen.Web.BackgroundJobs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// CO-06 (A5-20, A5-36) on PostgreSQL, through the API and the nightly job: an active property that loses its CIN, a
/// required document or its safety checklist is suspended at once (not published, reason stored, one email to the host,
/// bookings kept); a CIN entered again makes it ready for reactivation, and the host's activation publishes it again; the
/// nightly check and the recalculation of the historic properties suspend what the backfill had published, without
/// duplicate emails.
/// </summary>
public class PropertyComplianceStatusPostgresTests : IClassFixture<PropertyComplianceStatusPostgresTests.EmailsFactory>
{
    private readonly EmailsFactory _factory;

    public PropertyComplianceStatusPostgresTests(EmailsFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task UpdateCin_RemovedFromActiveProperty_SuspendedWithReasonAndOneEmailBookingsKept()
    {
        var seeded = await SeedActiveCompliantPropertyAsync("cin-removed");
        var bookingId = await SeedConfirmedBookingAsync(seeded);
        using var client = _factory.CreateAuthenticatedClient(seeded.HostId, "PropertyOwner");

        var response = await client.PutAsJsonAsync($"/api/properties/{seeded.PropertyId}/cin", new { cinCode = (string?)null });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var property = await LoadPropertyAsync(seeded.PropertyId);
        Assert.Equal(PropertyComplianceStatus.Suspended, property.ComplianceStatus);
        Assert.NotNull(property.ComplianceSuspendedAt);
        Assert.Equal(["activation_cin_missing"], property.ComplianceSuspensionReasons);

        var email = Assert.Single(EmailsTo(seeded.HostEmail));
        Assert.Equal(EmailTemplates.Names.PropertyComplianceSuspended, email.Template);
        Assert.Equal($"Annuncio sospeso - {seeded.Name}", email.Content.Subject);
        Assert.Contains("codice identificativo nazionale (CIN) mancante o non valido", email.Content.HtmlBody);
        Assert.Contains($"/app/short-rent/properties/{seeded.PropertyId:D}/activation", email.Content.HtmlBody);

        // The confirmed stay stays valid: nothing is cancelled.
        Assert.Equal(BookingStatus.Confirmed, await BookingStatusAsync(bookingId));

        var wizard = await ReadJsonAsync(await client.GetAsync($"/api/properties/{seeded.PropertyId}/compliance/activation"));
        Assert.Equal("Suspended", wizard.GetProperty("complianceStatus").GetString());
        Assert.Equal(["activation_cin_missing"], wizard.GetProperty("suspensionReasons").EnumerateArray().Select(r => r.GetString()));
        Assert.NotEqual(JsonValueKind.Null, wizard.GetProperty("suspendedAt").ValueKind);
    }

    [PostgresFact]
    public async Task UpdateCin_EnteredAgain_ReadyForReactivationThenActiveAfterHostActivation()
    {
        var seeded = await SeedActiveCompliantPropertyAsync("cin-back");
        using var client = _factory.CreateAuthenticatedClient(seeded.HostId, "PropertyOwner");
        await client.PutAsJsonAsync($"/api/properties/{seeded.PropertyId}/cin", new { cinCode = (string?)null });

        var reinserted = await client.PutAsJsonAsync($"/api/properties/{seeded.PropertyId}/cin", new { cinCode = UniqueCin() });

        Assert.Equal(HttpStatusCode.NoContent, reinserted.StatusCode);
        // Not republished on its own: the host reactivates it, as at the first activation.
        Assert.Equal(PropertyComplianceStatus.Suspended, (await LoadPropertyAsync(seeded.PropertyId)).ComplianceStatus);
        var wizard = await ReadJsonAsync(await client.GetAsync($"/api/properties/{seeded.PropertyId}/compliance/activation"));
        Assert.All(
            wizard.GetProperty("steps").EnumerateArray().Where(s => s.GetProperty("blocker").GetBoolean()),
            s => Assert.Equal("complete", s.GetProperty("status").GetString()));

        var activation = await client.PostAsJsonAsync(
            $"/api/properties/{seeded.PropertyId}/compliance/activation/complete", new { tosAccepted = true });

        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        Assert.Equal("Active", (await ReadJsonAsync(activation)).GetProperty("complianceStatus").GetString());
        var property = await LoadPropertyAsync(seeded.PropertyId);
        Assert.Equal(PropertyComplianceStatus.Active, property.ComplianceStatus);
        Assert.Null(property.ComplianceSuspendedAt);
        Assert.Null(property.ComplianceSuspensionReasons);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/properties/{seeded.PropertyId}/public")).StatusCode);
        Assert.Single(EmailsTo(seeded.HostEmail)); // only the suspension
    }

    [PostgresFact]
    public async Task SaveSafetyChecklist_WithoutConfirmationOnActiveProperty_SuspendedWithEmail()
    {
        var seeded = await SeedActiveCompliantPropertyAsync("checklist");
        using var client = _factory.CreateAuthenticatedClient(seeded.HostId, "PropertyOwner");

        var response = await client.PutAsJsonAsync(
            $"/api/properties/{seeded.PropertyId}/compliance/safety-checklist",
            new
            {
                facts = new
                {
                    entrepreneurial = false,
                    hasGasSupply = true,
                    combustionAppliances = new[] { "GasHob" },
                    floorCount = 1,
                    floorAreasSqm = new[] { 80m },
                },
                items = new object[]
                {
                    new { code = "FireExtinguishers", answer = "Present", quantity = 1 },
                    new { code = "BdsrDeclaration", answer = "Present" },
                },
                confirm = false,
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var property = await LoadPropertyAsync(seeded.PropertyId);
        Assert.Equal(PropertyComplianceStatus.Suspended, property.ComplianceStatus);
        Assert.Equal(
            ["safety_gas_detector_missing", "safety_co_detector_missing", "safety_confirmation_missing"],
            property.ComplianceSuspensionReasons);
        var email = Assert.Single(EmailsTo(seeded.HostEmail));
        Assert.Contains("checklist di sicurezza (D.L. 145/2023) incompleta o non confermata", email.Content.HtmlBody);
        Assert.DoesNotContain("(CIN) mancante", email.Content.HtmlBody);
    }

    [PostgresFact]
    public async Task DeleteDocument_RequiredDocumentOfActiveProperty_Suspended()
    {
        var seeded = await SeedActiveCompliantPropertyAsync("document");
        using var client = _factory.CreateAuthenticatedClient(seeded.HostId, "PropertyOwner");

        var response = await client.DeleteAsync($"/api/properties/{seeded.PropertyId}/documents/{seeded.DocumentId}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var property = await LoadPropertyAsync(seeded.PropertyId);
        Assert.Equal(PropertyComplianceStatus.Suspended, property.ComplianceStatus);
        Assert.Equal(["activation_documents_missing"], property.ComplianceSuspensionReasons);
        Assert.Contains("documenti obbligatori mancanti", Assert.Single(EmailsTo(seeded.HostEmail)).Content.HtmlBody);
    }

    [PostgresFact]
    public async Task PublicSite_SuspendedProperty_Returns404AndICalExportKeepsTheConfirmedStay()
    {
        var seeded = await SeedActiveCompliantPropertyAsync("public");
        var checkIn = PublicAvailabilityPostgresTests.NextYear(10, 1);
        await SeedConfirmedBookingAsync(seeded, checkIn);
        var exportToken = await SeedExportFeedAsync(seeded);
        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/properties/{seeded.PropertyId}/public")).StatusCode);

        using var client = _factory.CreateAuthenticatedClient(seeded.HostId, "PropertyOwner");
        await client.PutAsJsonAsync($"/api/properties/{seeded.PropertyId}/cin", new { cinCode = (string?)null });

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/properties/{seeded.PropertyId}/public")).StatusCode);
        var availability = await anonymous.GetAsync(
            PublicAvailabilityPostgresTests.AvailabilityPath(seeded.PropertyId, checkIn, checkIn.AddDays(10)));
        Assert.Equal(HttpStatusCode.NotFound, availability.StatusCode);
        Assert.Equal("public_property_not_found", (await ReadJsonAsync(availability)).GetProperty("code").GetString());
        var search = await ReadJsonAsync(await anonymous.GetAsync("/api/properties/search"));
        Assert.DoesNotContain(search.EnumerateArray(), p => p.GetProperty("id").GetGuid() == seeded.PropertyId);

        // The OTAs keep the confirmed stay blocked: the export lists busy dates, never availability (runbook).
        var export = await anonymous.GetAsync($"/api/public/ical/{exportToken}");
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var calendar = await export.Content.ReadAsStringAsync();
        Assert.Contains("BEGIN:VEVENT", calendar);
        Assert.Contains(checkIn.ToString("yyyyMMdd"), calendar);
    }

    [PostgresFact]
    public async Task NightlyCheck_RunTwice_SuspendsOnceWithoutDuplicateEmails()
    {
        var bySuspendedRequest = await SeedActiveCompliantPropertyAsync("job-request");
        var lostOutsideTheApi = await SeedActiveCompliantPropertyAsync("job-db");
        var stillCompliant = await SeedActiveCompliantPropertyAsync("job-ok");
        using var client = _factory.CreateAuthenticatedClient(bySuspendedRequest.HostId, "PropertyOwner");
        await client.PutAsJsonAsync($"/api/properties/{bySuspendedRequest.PropertyId}/cin", new { cinCode = (string?)null });
        // A requirement lost without any request (e.g. a row changed by hand): only the nightly check sees it.
        await WithDbAsync(db => db.PropertySafetyChecklists
            .IgnoreQueryFilters() // test scope without an org
            .Where(c => c.PropertyId == lostOutsideTheApi.PropertyId)
            .ExecuteUpdateAsync(set => set.SetProperty(c => c.ConfirmedAt, (DateTime?)null)));

        await RunNightlyJobAsync();
        await RunNightlyJobAsync();

        Assert.Single(EmailsTo(bySuspendedRequest.HostEmail));
        Assert.Single(EmailsTo(lostOutsideTheApi.HostEmail));
        Assert.Empty(EmailsTo(stillCompliant.HostEmail));
        Assert.Equal(PropertyComplianceStatus.Suspended, (await LoadPropertyAsync(lostOutsideTheApi.PropertyId)).ComplianceStatus);
        Assert.Equal(["safety_confirmation_missing"], (await LoadPropertyAsync(lostOutsideTheApi.PropertyId)).ComplianceSuspensionReasons);
        Assert.Equal(PropertyComplianceStatus.Active, (await LoadPropertyAsync(stillCompliant.PropertyId)).ComplianceStatus);
    }

    [PostgresFact]
    public async Task ReevaluateAsync_ConcurrentCallsOnTheSameProperty_OneSuspensionOneEmail()
    {
        var seeded = await SeedActiveCompliantPropertyAsync("concurrent");
        await WithDbAsync(db => db.Properties
            .IgnoreQueryFilters()
            .Where(p => p.Id == seeded.PropertyId)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.CinCode, (string?)null)));

        var checks = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            using var scope = _factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPropertyComplianceStatusService>()
                .ReevaluateAsync(seeded.PropertyId);
        }));

        Assert.Single(checks, c => c.Suspended);
        Assert.Single(checks, c => c.HostNotified);
        Assert.Single(EmailsTo(seeded.HostEmail));
    }

    [PostgresFact]
    public async Task HistoricRecalculation_ActiveWithoutDocumentsOrChecklist_SuspendedWithoutEmailByDefault()
    {
        // As the backfill of AddPropertyComplianceStatus left them (A5-36): active with any CIN, no document, no
        // checklist; AddPropertyComplianceSuspension adds ComplianceCheckedAt null to every existing row.
        var historic = await SeedHistoricActivePropertyAsync("historic-bare", complete: false);
        var historicComplete = await SeedHistoricActivePropertyAsync("historic-complete", complete: true);
        Assert.Contains(
            await WithDbAsync(db => Task.FromResult(db.Database.GetAppliedMigrations().ToList())),
            m => m.EndsWith("_AddPropertyComplianceSuspension", StringComparison.Ordinal));

        var dryRun = await RecalculateAsync(dryRun: true);
        Assert.True(dryRun.Suspended >= 1);
        Assert.Equal(PropertyComplianceStatus.Active, (await LoadPropertyAsync(historic.PropertyId)).ComplianceStatus);

        var report = await RecalculateAsync(dryRun: false);

        Assert.False(report.DryRun);
        Assert.Equal(0, report.Failed);
        var suspended = await LoadPropertyAsync(historic.PropertyId);
        Assert.Equal(PropertyComplianceStatus.Suspended, suspended.ComplianceStatus);
        Assert.Contains("activation_documents_missing", suspended.ComplianceSuspensionReasons!);
        Assert.Contains("safety_confirmation_missing", suspended.ComplianceSuspensionReasons!);
        Assert.NotNull(suspended.ComplianceCheckedAt);
        var kept = await LoadPropertyAsync(historicComplete.PropertyId);
        Assert.Equal(PropertyComplianceStatus.Active, kept.ComplianceStatus);
        Assert.NotNull(kept.ComplianceCheckedAt);
        // First check of a historic property: Compliance:StatusCheck:NotifyOnFirstCheck is off by default.
        Assert.Empty(EmailsTo(historic.HostEmail));

        // Once checked, a later suspension always emails the host.
        await WithDbAsync(db => db.Properties
            .IgnoreQueryFilters()
            .Where(p => p.Id == historicComplete.PropertyId)
            .ExecuteUpdateAsync(set => set.SetProperty(p => p.CinCode, (string?)null)));
        await RecalculateAsync(dryRun: false);
        Assert.Single(EmailsTo(historicComplete.HostEmail));
    }

    private sealed record SeededProperty(string HostId, string HostEmail, Guid PropertyId, Guid OrgId, string Name, Guid DocumentId);

    /// <summary>Base data, CIN, CIN certificate and complete checklist, activated through the API.</summary>
    private async Task<SeededProperty> SeedActiveCompliantPropertyAsync(string label)
    {
        var seeded = await SeedPropertyAsync(label, PropertyComplianceStatus.Pending, withDocument: true, withChecklist: true, checkedAt: null);
        using var client = _factory.CreateAuthenticatedClient(seeded.HostId, "PropertyOwner");
        var activation = await client.PostAsJsonAsync(
            $"/api/properties/{seeded.PropertyId}/compliance/activation/complete", new { tosAccepted = true });
        Assert.Equal(HttpStatusCode.OK, activation.StatusCode);
        Assert.Equal(PropertyComplianceStatus.Active, (await LoadPropertyAsync(seeded.PropertyId)).ComplianceStatus);
        return seeded;
    }

    private Task<SeededProperty> SeedHistoricActivePropertyAsync(string label, bool complete) =>
        SeedPropertyAsync(label, PropertyComplianceStatus.Active, withDocument: complete, withChecklist: complete, checkedAt: null);

    private async Task<SeededProperty> SeedPropertyAsync(
        string label,
        PropertyComplianceStatus status,
        bool withDocument,
        bool withChecklist,
        DateTime? checkedAt)
    {
        var hostId = $"auth0|co06-{label}-{Guid.NewGuid():N}";
        var hostEmail = $"host-{Guid.NewGuid():N}@example.com";
        var org = await _factory.SeedOrgForOwnerAsync(hostId);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storedOrg = await db.Orgs.SingleAsync(o => o.Id == org.Id);
        storedOrg.ContactEmail = hostEmail;
        var property = new Property
        {
            OwnerId = hostId,
            OrgId = org.Id,
            Name = $"Casa {label}",
            Description = "CO-06",
            Address = $"Via Test {Guid.NewGuid():N}",
            City = "Seveso",
            PostalCode = "20822",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 120m,
            CinCode = UniqueCin(),
            IsActive = true,
            ComplianceStatus = status,
            ComplianceCheckedAt = checkedAt,
        };
        db.Properties.Add(property);
        var document = new PropertyDocument
        {
            PropertyId = property.Id,
            OrgId = org.Id,
            FileName = "cin.pdf",
            StorageUrl = $"documents/{Guid.NewGuid():N}.pdf",
            DocumentType = DocumentType.CinCertificate,
            UploadedBy = hostId,
        };
        if (withDocument)
            db.PropertyDocuments.Add(document);
        if (withChecklist)
            db.PropertySafetyChecklists.Add(SafetyChecklistTestData.CompleteAllElectric(property.Id, org.Id));
        await db.SaveChangesAsync();
        return new SeededProperty(hostId, hostEmail, property.Id, org.Id, property.Name, document.Id);
    }

    private async Task<Guid> SeedConfirmedBookingAsync(SeededProperty property, DateTime? checkIn = null)
    {
        var from = checkIn ?? PublicAvailabilityPostgresTests.NextYear(9, 10);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var guest = new Guest
        {
            OrgId = property.OrgId,
            FirstName = "Anna",
            LastName = "Verdi",
            Email = $"anna.{Guid.NewGuid():N}@example.com",
            DataProcessingPurpose = "Direct Booking Checkout",
        };
        var booking = new Booking
        {
            PropertyId = property.PropertyId,
            OrgId = property.OrgId,
            GuestId = guest.Id,
            CheckInDate = from,
            CheckOutDate = from.AddDays(3),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            PaymentOption = PaymentOption.Immediate,
            TotalPrice = 360m,
            FreeRefundDeadline = from.AddDays(-7),
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    private async Task<Guid> SeedExportFeedAsync(SeededProperty property)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var feed = new PropertyICalFeed { PropertyId = property.PropertyId, OrgId = property.OrgId };
        db.PropertyICalFeeds.Add(feed);
        await db.SaveChangesAsync();
        return feed.ExportToken;
    }

    private async Task RunNightlyJobAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PropertyComplianceCheckJob>().ExecuteAsync(CancellationToken.None);
    }

    private async Task<PropertyComplianceRecalculation> RecalculateAsync(bool dryRun)
    {
        using var scope = _factory.Services.CreateScope();
        var report = await scope.ServiceProvider.GetRequiredService<IPropertyComplianceStatusService>()
            .RecalculateAllAsync(dryRun);
        Assert.NotNull(report);
        return report;
    }

    private async Task<BookingStatus> BookingStatusAsync(Guid bookingId) =>
        await WithDbAsync(db => db.Bookings.IgnoreQueryFilters().Where(b => b.Id == bookingId).Select(b => b.Status).SingleAsync());

    private async Task<Property> LoadPropertyAsync(Guid propertyId) =>
        // Test scope without an authenticated org: read the row directly to check what the API stored.
        await WithDbAsync(db => db.Properties.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == propertyId));

    private async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = _factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private List<(string? To, EmailContent Content, string Template)> EmailsTo(string recipient) =>
        _factory.Emails.Snapshot().Where(e => e.To == recipient).ToList();

    /// <summary>A valid CIN (CinFormat) never used by another property: the CIN endpoint refuses duplicates.</summary>
    private static string UniqueCin() => $"IT058091C2{Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()}";

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>The integration host with a recording email queue.</summary>
    public sealed class EmailsFactory : CasazenWebApplicationFactory
    {
        internal RecordingEmailQueue Emails { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                RemoveAllOf<IEmailQueue>(services);
                services.AddSingleton<IEmailQueue>(Emails);
            });
        }
    }
}
