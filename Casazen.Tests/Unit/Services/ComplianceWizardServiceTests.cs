using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class ComplianceWizardServiceTests
{
    private static AppDbContext CreateDb(string dbName)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(options);
    }

    private static IConfiguration CreateConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Compliance:CinGuidanceUrl"] = "https://www.bdsr.it/cin",
                ["Compliance:RequiredDocuments:default:0"] = "CinCertificate",
                ["Compliance:GdprRetentionYears"] = "7",
            })
            .Build();

    private static ComplianceWizardService CreateService(
        AppDbContext db,
        TimeProvider? timeProvider = null,
        IConfiguration? configuration = null)
    {
        var alloggiati = new Mock<IAlloggiatiWebService>();
        alloggiati.Setup(a => a.IsStayDataCompleteAsync(It.IsAny<Guid>())).ReturnsAsync(false);

        var stayLifecycle = new StayLifecycleService(
            db,
            alloggiati.Object,
            Mock.Of<IAlloggiatiReportScheduler>(),
            Mock.Of<IServiceRequestService>(),
            Options.Create(new ComplianceOptions { GdprRetentionYears = 7 }),
            NullLogger<StayLifecycleService>.Instance,
            timeProvider);

        return new ComplianceWizardService(
            db,
            alloggiati.Object,
            stayLifecycle,
            new TouristTaxQuoteService(new TouristTaxRateRepository(db), NullLogger<TouristTaxQuoteService>.Instance),
            CreateStatusService(db, configuration ?? CreateConfig(), timeProvider),
            Mock.Of<ILogger<ComplianceWizardService>>(),
            timeProvider);
    }

    /// <summary>The real evaluation of the blockers (CO-06): the wizard has no copy of its own.</summary>
    private static PropertyComplianceStatusService CreateStatusService(
        AppDbContext db,
        IConfiguration configuration,
        TimeProvider? timeProvider) =>
        new(
            db,
            configuration,
            Mock.Of<IEmailQueue>(),
            EmailTestHelpers.Links(),
            Options.Create(new ComplianceOptions()),
            NullLogger<PropertyComplianceStatusService>.Instance,
            timeProvider);

    // 2026-09-24 00:30 in Rome is still 2026-09-23 in UTC: "today" must be the Rome date.
    private static readonly TimeProvider RomeJustAfterMidnight =
        new FixedTimeProvider(new DateTimeOffset(2026, 9, 23, 22, 30, 0, TimeSpan.Zero));

    [Fact]
    public async Task Activation_NoRateForCity_ReturnsNonBlockingWarningWithLocalizableMessage()
    {
        await using var db = CreateDb(nameof(Activation_NoRateForCity_ReturnsNonBlockingWarningWithLocalizableMessage));
        var property = await SeedPropertyAsync(db, city: "Seveso");

        var (_, steps) = await CreateService(db).GetActivationWizardAsync(property.Id);

        var tax = steps.Single(s => s.Id == "tourist-tax");
        Assert.Equal("warning", tax.Status);
        Assert.False(tax.Blocker);
        Assert.Equal("ActivationTouristTaxNoRate", tax.MessageKey);
        Assert.Equal(new object[] { "Seveso" }, tax.MessageArgs!);
        Assert.NotNull(tax.TouristTax);
        Assert.Null(tax.TouristTax!.Rate);
        Assert.Null(tax.TouristTax.PublicPageSlug);
    }

    [Fact]
    public async Task Activation_RateInForceTodayInRome_ReturnsCompleteStepWithRate()
    {
        await using var db = CreateDb(nameof(Activation_RateInForceTodayInRome_ReturnsCompleteStepWithRate));
        var property = await SeedPropertyAsync(db, city: "Milano");
        var rate = AddRate(db, "milano", 9.50m, new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc));
        AddRate(db, "Milano", 1m, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), isActive: false);
        await db.SaveChangesAsync();

        var (_, steps) = await CreateService(db, RomeJustAfterMidnight).GetActivationWizardAsync(property.Id);

        var tax = steps.Single(s => s.Id == "tourist-tax");
        Assert.Equal("complete", tax.Status);
        Assert.False(tax.Blocker);
        Assert.Null(tax.MessageKey);
        Assert.Equal(rate.Id, tax.TouristTax!.Rate!.Id);
        Assert.Equal(9.50m, tax.TouristTax.Rate.RatePerPersonPerNight);
    }

    [Fact]
    public async Task Activation_RateNotYetInForceOrExpired_ReturnsWarning()
    {
        await using var db = CreateDb(nameof(Activation_RateNotYetInForceOrExpired_ReturnsWarning));
        var property = await SeedPropertyAsync(db, city: "Milano");
        AddRate(db, "Milano", 9.50m, new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc));
        AddRate(
            db,
            "Milano",
            6.30m,
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            effectiveTo: new DateTime(2026, 9, 23, 0, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync();

        var (_, steps) = await CreateService(db, RomeJustAfterMidnight).GetActivationWizardAsync(property.Id);

        var tax = steps.Single(s => s.Id == "tourist-tax");
        Assert.Equal("warning", tax.Status);
        Assert.Null(tax.TouristTax!.Rate);
    }

    [Fact]
    public async Task Activation_ReviewedTouristTaxPage_ReturnsPublicPageSlug()
    {
        await using var db = CreateDb(nameof(Activation_ReviewedTouristTaxPage_ReturnsPublicPageSlug));
        var property = await SeedPropertyAsync(db, city: "Como");
        db.SeoContentPages.Add(new SeoContentPage
        {
            Slug = "tassa-soggiorno/como",
            ComuneCode = "013075",
            RegionCode = "LOM",
            PageType = SeoPageType.TouristTaxCalc,
            LegalReviewStatus = LegalReviewStatus.Reviewed,
        });
        await db.SaveChangesAsync();

        var (_, steps) = await CreateService(db).GetActivationWizardAsync(property.Id);

        Assert.Equal("como", steps.Single(s => s.Id == "tourist-tax").TouristTax!.PublicPageSlug);
    }

    [Fact]
    public async Task Activation_DraftTouristTaxPage_ReturnsNoPublicPageSlug()
    {
        await using var db = CreateDb(nameof(Activation_DraftTouristTaxPage_ReturnsNoPublicPageSlug));
        var property = await SeedPropertyAsync(db, city: "Como");
        db.SeoContentPages.Add(new SeoContentPage
        {
            Slug = "tassa-soggiorno/como",
            ComuneCode = "013075",
            RegionCode = "LOM",
            PageType = SeoPageType.TouristTaxCalc,
            LegalReviewStatus = LegalReviewStatus.Draft,
        });
        await db.SaveChangesAsync();

        var (_, steps) = await CreateService(db).GetActivationWizardAsync(property.Id);

        Assert.Null(steps.Single(s => s.Id == "tourist-tax").TouristTax!.PublicPageSlug);
    }

    [Fact]
    public async Task Activation_CityMissing_ReturnsCityMissingWarning()
    {
        await using var db = CreateDb(nameof(Activation_CityMissing_ReturnsCityMissingWarning));
        var property = await SeedPropertyAsync(db, city: " ");

        var (_, steps) = await CreateService(db).GetActivationWizardAsync(property.Id);

        var tax = steps.Single(s => s.Id == "tourist-tax");
        Assert.Equal("warning", tax.Status);
        Assert.False(tax.Blocker);
        Assert.Equal("ActivationTouristTaxCityMissing", tax.MessageKey);
    }

    [Fact]
    public async Task Activation_CinStep_ExposesConfiguredGuidanceUrl()
    {
        await using var db = CreateDb(nameof(Activation_CinStep_ExposesConfiguredGuidanceUrl));
        var property = await SeedPropertyAsync(db, cinCode: null);

        var (_, steps) = await CreateService(db).GetActivationWizardAsync(property.Id);

        Assert.Equal("https://www.bdsr.it/cin", steps.Single(s => s.Id == "cin").LinkUrl);
    }

    [Fact]
    public async Task CompleteActivation_NoTouristTaxRate_SetsActive()
    {
        await using var db = CreateDb(nameof(CompleteActivation_NoTouristTaxRate_SetsActive));
        var property = await SeedFullyCompliantPropertyAsync(db, withTouristTaxRate: false);

        var (updated, blockers) = await CreateService(db).CompleteActivationAsync(
            property.Id,
            property.OwnerId,
            tosAccepted: true);

        Assert.Empty(blockers);
        Assert.Equal(PropertyComplianceStatus.Active, updated.ComplianceStatus);
    }

    private static TouristTaxRate AddRate(
        AppDbContext db,
        string city,
        decimal amount,
        DateTime effectiveFrom,
        DateTime? effectiveTo = null,
        bool isActive = true)
    {
        var rate = new TouristTaxRate
        {
            City = city,
            RegionCode = "LOM",
            RatePerPersonPerNight = amount,
            MinimumAge = 18,
            IsActive = isActive,
            EffectiveFrom = effectiveFrom,
            EffectiveTo = effectiveTo,
        };
        db.TouristTaxRates.Add(rate);
        return rate;
    }

    [Fact]
    public async Task Activation_CinMissing_ReturnsPendingCinStep()
    {
        await using var db = CreateDb(nameof(Activation_CinMissing_ReturnsPendingCinStep));
        var property = await SeedPropertyAsync(db, cinCode: null);

        var (_, steps) = await CreateService(db).GetActivationWizardAsync(property.Id);

        var cin = steps.Single(s => s.Id == "cin");
        Assert.Equal("pending", cin.Status);
        Assert.True(cin.Blocker);
    }

    [Fact]
    public async Task Activation_CinValid_MarksCinComplete()
    {
        await using var db = CreateDb(nameof(Activation_CinValid_MarksCinComplete));
        var property = await SeedPropertyAsync(db, cinCode: "IT058091C27G5FFZDZ");

        var (_, steps) = await CreateService(db).GetActivationWizardAsync(property.Id);

        var cin = steps.Single(s => s.Id == "cin");
        Assert.Equal("complete", cin.Status);
    }

    [Fact]
    public async Task CompleteActivation_AllBlockersMet_SetsActive()
    {
        await using var db = CreateDb(nameof(CompleteActivation_AllBlockersMet_SetsActive));
        var property = await SeedFullyCompliantPropertyAsync(db);

        var (updated, blockers) = await CreateService(db).CompleteActivationAsync(
            property.Id,
            property.OwnerId,
            tosAccepted: true);

        Assert.Empty(blockers);
        Assert.Equal(PropertyComplianceStatus.Active, updated.ComplianceStatus);
        Assert.NotNull(updated.ComplianceCompletedAt);
    }

    [Fact]
    public async Task CompleteActivation_TosOmitted_DoesNotActivateProperty()
    {
        await using var db = CreateDb(nameof(CompleteActivation_TosOmitted_DoesNotActivateProperty));
        var property = await SeedFullyCompliantPropertyAsync(db);

        var error = await Assert.ThrowsAsync<DomainConflictException>(() => CreateService(db).CompleteActivationAsync(
            property.Id,
            property.OwnerId,
            tosAccepted: null));
        Assert.Equal("activation_tos_required", error.Code);

        var reloaded = await db.Properties.FindAsync(property.Id);
        Assert.Equal(PropertyComplianceStatus.Pending, reloaded!.ComplianceStatus);
        Assert.Null(reloaded.ComplianceCompletedAt);
    }

    [Fact]
    public async Task CompleteActivation_BlockersRemaining_StaysPending()
    {
        await using var db = CreateDb(nameof(CompleteActivation_BlockersRemaining_StaysPending));
        var property = await SeedPropertyAsync(db, cinCode: null);

        var (updated, blockers) = await CreateService(db).CompleteActivationAsync(
            property.Id,
            property.OwnerId,
            tosAccepted: true);

        var cin = Assert.Single(blockers, b => b.Id == "cin");
        Assert.Equal("activation_cin_missing", Assert.Single(cin.Blockers).Code);
        Assert.Equal(PropertyComplianceStatus.Pending, updated.ComplianceStatus);
    }

    [Fact]
    public async Task Activation_NoSafetyChecklist_SafetyStepBlocksWithStableCodes()
    {
        await using var db = CreateDb(nameof(Activation_NoSafetyChecklist_SafetyStepBlocksWithStableCodes));
        var property = await SeedPropertyAsync(db);

        var (_, steps) = await CreateService(db).GetActivationWizardAsync(property.Id);

        var safety = steps.Single(s => s.Id == "safety");
        Assert.Equal("pending", safety.Status);
        Assert.True(safety.Blocker);
        Assert.Equal("ActivationSafetyIncomplete", safety.MessageKey);
        Assert.Contains(safety.Blockers, b => b.Code == "safety_extinguishers_missing");
        Assert.DoesNotContain(safety.Blockers, b => b.Code.Contains("smoke", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompleteActivation_AllElectricHomeWithoutSmokeDetector_SetsActive()
    {
        await using var db = CreateDb(nameof(CompleteActivation_AllElectricHomeWithoutSmokeDetector_SetsActive));
        var property = await SeedFullyCompliantPropertyAsync(db);
        var smoke = await db.PropertySafetyChecklistItems.SingleAsync(i => i.Code == SafetyItemCode.SmokeDetector);
        smoke.Answer = SafetyItemAnswer.Missing;
        await db.SaveChangesAsync();

        var (updated, blockers) = await CreateService(db).CompleteActivationAsync(property.Id, property.OwnerId, tosAccepted: true);

        Assert.Empty(blockers);
        Assert.Equal(PropertyComplianceStatus.Active, updated.ComplianceStatus);
    }

    [Fact]
    public async Task CompleteActivation_GasHomeWithoutDetectors_StaysPendingWithDetectorBlockers()
    {
        await using var db = CreateDb(nameof(CompleteActivation_GasHomeWithoutDetectors_StaysPendingWithDetectorBlockers));
        var property = await SeedFullyCompliantPropertyAsync(db);
        var checklist = await db.PropertySafetyChecklists.SingleAsync(c => c.PropertyId == property.Id);
        checklist.HasGasSupply = true;
        checklist.CombustionAppliances = [CombustionAppliance.GasHob];
        await db.SaveChangesAsync();

        var (updated, blockers) = await CreateService(db).CompleteActivationAsync(property.Id, property.OwnerId, tosAccepted: true);

        var safety = Assert.Single(blockers);
        Assert.Equal("safety", safety.Id);
        Assert.Equal(
            ["safety_gas_detector_missing", "safety_co_detector_missing"],
            safety.Blockers.Select(b => b.Code));
        Assert.Equal(PropertyComplianceStatus.Pending, updated.ComplianceStatus);
    }

    [Fact]
    public async Task Activation_NoRequiredDocumentsConfigured_DoesNotAskForASafetyCertificate()
    {
        await using var db = CreateDb(nameof(Activation_NoRequiredDocumentsConfigured_DoesNotAskForASafetyCertificate));
        var property = await SeedFullyCompliantPropertyAsync(db);
        var noDocumentsConfig = new ConfigurationBuilder().AddInMemoryCollection([]).Build();

        var (_, steps) = await CreateService(db, configuration: noDocumentsConfig).GetActivationWizardAsync(property.Id);

        Assert.DoesNotContain(property.PropertyDocuments, d => d.DocumentType == DocumentType.SafetyCompliance);
        Assert.Equal("complete", steps.Single(s => s.Id == "documents").Status);
    }

    [Fact]
    public async Task Summary_ReturnsExpectedCounts()
    {
        await using var db = CreateDb(nameof(Summary_ReturnsExpectedCounts));
        var org = new OrgEntity { Name = "Test Org", Slug = $"org-{Guid.NewGuid():N}" };
        db.Orgs.Add(org);

        var pending = await SeedPropertyAsync(db, org.Id, complianceStatus: PropertyComplianceStatus.Pending);
        var active = await SeedFullyCompliantPropertyAsync(db, org.Id);
        active.ComplianceStatus = PropertyComplianceStatus.Active;
        await db.SaveChangesAsync();

        var guest = new Guest
        {
            FirstName = "Mario",
            LastName = "Rossi",
            Email = $"mario-{Guid.NewGuid():N}@test.com",
        };
        db.Guests.Add(guest);

        db.Bookings.Add(new Booking
        {
            PropertyId = pending.Id,
            OrgId = org.Id,
            GuestId = guest.Id,
            CheckInDate = DateTime.UtcNow.Date.AddDays(-2),
            CheckOutDate = DateTime.UtcNow.Date,
            Status = BookingStatus.CheckedIn,
            NumberOfGuests = 2,
            BasePrice = 100,
            TouristTax = 0,
            TotalPrice = 100,
        });

        await db.SaveChangesAsync();

        var summary = await CreateService(db).GetSummaryAsync(org.Id);

        Assert.True(summary.PropertiesPending.Count >= 1);
        Assert.True(summary.CheckoutsDue.Count >= 1);
    }

    [Fact]
    public async Task Summary_AlloggiatiNotTransmitted_CountsAsToSendManuallyNeverAsDone()
    {
        await using var db = CreateDb(nameof(Summary_AlloggiatiNotTransmitted_CountsAsToSendManuallyNeverAsDone));
        var property = await SeedPropertyAsync(db);
        var today = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero));

        Booking Stay(string name, DateTime checkIn, BookingStatus status = BookingStatus.Confirmed)
        {
            var guest = new Guest { FirstName = name, LastName = "Test", Email = $"{Guid.NewGuid():N}@test.com", OrgId = property.OrgId };
            db.Guests.Add(guest);
            var booking = BuildBooking(property, guest, checkIn.AddDays(2), status);
            db.Bookings.Add(booking);
            return booking;
        }

        void Report(Booking booking, AlloggiatiWebStatus status) => db.AlloggiatiWebReports.Add(new AlloggiatiWebReport
        {
            BookingId = booking.Id,
            GuestId = booking.GuestId,
            OrgId = booking.OrgId,
            Status = status,
            ReportedAt = status == AlloggiatiWebStatus.InviatoManualmente ? today : null,
        });

        var noReport = Stay("NoReport", today);
        var jobNotRunYet = Stay("JobPending", today);
        Report(jobNotRunYet, AlloggiatiWebStatus.DaInviare);
        var manual = Stay("Manual", today.AddDays(-1), BookingStatus.CheckedIn);
        Report(manual, AlloggiatiWebStatus.DaInviareManualmente);
        var declared = Stay("Declared", today.AddDays(-1), BookingStatus.CheckedIn);
        Report(declared, AlloggiatiWebStatus.InviatoManualmente);
        var rejected = Stay("Rejected", today.AddDays(-1));
        Report(rejected, AlloggiatiWebStatus.Rifiutato);
        var future = Stay("Future", today.AddDays(1));
        Report(future, AlloggiatiWebStatus.DaInviare);
        Stay("Cancelled", today, BookingStatus.Cancelled);
        await db.SaveChangesAsync();

        var summary = await CreateService(db, clock).GetSummaryAsync(property.OrgId);

        Assert.Equal(
            new[] { noReport.Id, jobNotRunYet.Id, manual.Id }.OrderBy(id => id),
            summary.AlloggiatiManualRequired.Items.Select(i => i.Id).OrderBy(id => id));
        Assert.Equal(3, summary.AlloggiatiManualRequired.Count);
        Assert.Equal(rejected.Id, Assert.Single(summary.AlloggiatiFailures.Items).Id);
        Assert.Equal($"/bookings/{manual.Id}/alloggiati", summary.AlloggiatiManualRequired.Items.Single(i => i.Id == manual.Id).RouteLink);
    }

    [Fact]
    public async Task StartCheckoutWizard_ConfirmedBookingWithoutArrival_Returns409UntilTheHostRegistersTheArrival()
    {
        await using var db = CreateDb(nameof(StartCheckoutWizard_ConfirmedBookingWithoutArrival_Returns409UntilTheHostRegistersTheArrival));
        var property = await SeedFullyCompliantPropertyAsync(db);
        property.ComplianceStatus = PropertyComplianceStatus.Active;
        // Rome 24/09 00:30: the departure day of the stay has started.
        var checkout = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);
        var guest = new Guest
        {
            FirstName = "Luigi",
            LastName = "Verdi",
            Email = $"luigi-{Guid.NewGuid():N}@test.com",
            // Retention is only ever extended (#429): start below checkout + 7y so the check-out's
            // checkout-anchored horizon is observable regardless of the entity's UtcNow default.
            DataRetentionUntil = checkout.AddYears(1),
        };
        db.Guests.Add(guest);
        var booking = BuildBooking(property, guest, checkout, BookingStatus.Confirmed);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        var service = CreateService(db, RomeJustAfterMidnight);

        var deadEnd = await Assert.ThrowsAsync<DomainConflictException>(() => service.StartCheckoutWizardAsync(booking.Id));
        var (started, steps) = await service.StartCheckoutWizardAsync(booking.Id, registerArrival: true);
        var statusAfterStart = started.Status;
        var (updated, propertyReady) = await service.CompleteCheckoutWizardAsync(
            booking.Id,
            property.OwnerId,
            new CompleteCheckoutWizardInput(
                ConfirmDeparture: true,
                SupplierOrgId: null,
                ServiceNotes: null,
                ServiceCategory: null));

        Assert.Equal(BookingErrorCodes.ArrivalNotRegistered, deadEnd.Code);
        Assert.Equal(BookingStatus.CheckedIn, statusAfterStart);
        Assert.NotNull(started.CheckoutWizardStartedAt);
        Assert.Contains(steps, s => s.Id == "confirm-departure" && s.Status == "complete");
        Assert.True(propertyReady);
        Assert.Equal(BookingStatus.CheckedOut, updated.Status);
        Assert.Equal(checkout.AddYears(7), updated.Guest.DataRetentionUntil);
    }

    [Fact]
    public async Task CompleteCheckoutWizard_DepartureNotConfirmed_Returns422AndKeepsTheStayOpen()
    {
        await using var db = CreateDb(nameof(CompleteCheckoutWizard_DepartureNotConfirmed_Returns422AndKeepsTheStayOpen));
        var property = await SeedPropertyAsync(db);
        var guest = new Guest { FirstName = "Anna", LastName = "Neri", Email = $"anna-{Guid.NewGuid():N}@test.com" };
        db.Guests.Add(guest);
        var booking = BuildBooking(property, guest, new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc), BookingStatus.CheckedIn);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<DomainRuleException>(() => CreateService(db, RomeJustAfterMidnight)
            .CompleteCheckoutWizardAsync(booking.Id, property.OwnerId, new CompleteCheckoutWizardInput(false, null, null, null)));

        Assert.Equal(BookingErrorCodes.DepartureNotConfirmed, error.Code);
        Assert.Equal(BookingStatus.CheckedIn, (await db.Bookings.AsNoTracking().SingleAsync(b => b.Id == booking.Id)).Status);
    }

    [Fact]
    public async Task Summary_CheckoutsDue_TodaysDeparturesInRomeConfirmedOrCheckedInAndOpenStays()
    {
        await using var db = CreateDb(nameof(Summary_CheckoutsDue_TodaysDeparturesInRomeConfirmedOrCheckedInAndOpenStays));
        var property = await SeedPropertyAsync(db);
        // 2026-09-23 22:30 UTC is already 24/09 in Rome.
        var todayInRome = new DateTime(2026, 9, 24, 0, 0, 0, DateTimeKind.Utc);

        Booking Stay(DateTime checkout, BookingStatus status)
        {
            var guest = new Guest { FirstName = "Ospite", LastName = status.ToString(), Email = $"{Guid.NewGuid():N}@test.com", OrgId = property.OrgId };
            db.Guests.Add(guest);
            var booking = BuildBooking(property, guest, checkout, status);
            db.Bookings.Add(booking);
            return booking;
        }

        var confirmedToday = Stay(todayInRome, BookingStatus.Confirmed);
        var checkedInToday = Stay(todayInRome, BookingStatus.CheckedIn);
        var checkedInOverdue = Stay(todayInRome.AddDays(-1), BookingStatus.CheckedIn);
        Stay(todayInRome.AddDays(-1), BookingStatus.Confirmed);
        Stay(todayInRome.AddDays(1), BookingStatus.CheckedIn);
        Stay(todayInRome.AddDays(1), BookingStatus.Confirmed);
        Stay(todayInRome, BookingStatus.CheckedOut);
        Stay(todayInRome, BookingStatus.Cancelled);
        await db.SaveChangesAsync();

        var summary = await CreateService(db, RomeJustAfterMidnight).GetSummaryAsync(property.OrgId);

        Assert.Equal(3, summary.CheckoutsDue.Count);
        Assert.Equal(
            new[] { confirmedToday.Id, checkedInToday.Id, checkedInOverdue.Id }.Order(),
            summary.CheckoutsDue.Items.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task CompleteCheckoutWizard_SharedGuest_KeepsRetentionForLatestBooking()
    {
        await using var db = CreateDb(nameof(CompleteCheckoutWizard_SharedGuest_KeepsRetentionForLatestBooking));
        var org = new OrgEntity { Name = "Repeat Guest Org", Slug = $"org-{Guid.NewGuid():N}" };
        db.Orgs.Add(org);
        var property = await SeedPropertyAsync(db, org.Id);
        var guest = new Guest
        {
            FirstName = "Repeat",
            LastName = "Guest",
            Email = $"repeat-{Guid.NewGuid():N}@test.com",
            DataRetentionUntil = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc).AddYears(7),
        };
        db.Guests.Add(guest);

        var earlyCheckout = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        var laterCheckout = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);
        var earlyBooking = BuildBooking(property, guest, earlyCheckout, BookingStatus.CheckedIn);
        var laterBooking = BuildBooking(property, guest, laterCheckout, BookingStatus.Confirmed);
        db.Bookings.AddRange(earlyBooking, laterBooking);
        await db.SaveChangesAsync();

        await CreateService(db).CompleteCheckoutWizardAsync(
            earlyBooking.Id,
            property.OwnerId,
            new CompleteCheckoutWizardInput(true, null, null, null));

        var reloadedGuest = await db.Guests.SingleAsync(g => g.Id == guest.Id);
        Assert.Equal(laterCheckout.AddYears(7), reloadedGuest.DataRetentionUntil);
    }

    private static async Task<Property> SeedPropertyAsync(
        AppDbContext db,
        Guid? orgId = null,
        string? cinCode = "IT058091C27G5FFZDZ",
        PropertyComplianceStatus complianceStatus = PropertyComplianceStatus.Pending,
        string city = "Rome")
    {
        var org = orgId.HasValue
            ? await db.Orgs.FindAsync(orgId.Value)
            : new OrgEntity { Name = "Org", Slug = $"slug-{Guid.NewGuid():N}" };

        if (org is null)
            throw new InvalidOperationException("Org not found");

        if (!orgId.HasValue)
            db.Orgs.Add(org);

        var property = new Property
        {
            OrgId = org.Id,
            OwnerId = "auth0|owner",
            Name = "Villa Test",
            Address = "Via Roma 1",
            City = city,
            PostalCode = "00100",
            Bedrooms = 2,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100,
            CinCode = cinCode,
            ComplianceStatus = complianceStatus,
            IsActive = true,
        };

        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property;
    }

    private static Booking BuildBooking(Property property, Guest guest, DateTime checkout, BookingStatus status) => new()
    {
        Id = Guid.NewGuid(),
        PropertyId = property.Id,
        Property = property,
        OrgId = property.OrgId,
        GuestId = guest.Id,
        Guest = guest,
        CheckInDate = checkout.AddDays(-2),
        CheckOutDate = checkout,
        Status = status,
        NumberOfGuests = 2,
        BasePrice = 100,
        TouristTax = 0,
        TotalPrice = 100,
    };

    private static async Task<Property> SeedFullyCompliantPropertyAsync(
        AppDbContext db,
        Guid? orgId = null,
        bool withTouristTaxRate = true)
    {
        var property = await SeedPropertyAsync(db, orgId, cinCode: "IT058091C27G5FFZDZ");

        if (withTouristTaxRate)
        {
            db.TouristTaxRates.Add(new TouristTaxRate
            {
                City = property.City,
                RegionCode = "LAZ",
                RatePerPersonPerNight = 2m,
                IsActive = true,
                EffectiveFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
        }

        db.PropertyDocuments.Add(
            new PropertyDocument
            {
                PropertyId = property.Id,
                FileName = "cin.pdf",
                StorageUrl = "/docs/cin.pdf",
                DocumentType = DocumentType.CinCertificate,
                UploadedBy = property.OwnerId,
            });

        db.PropertySafetyChecklists.Add(SafetyChecklistTestData.CompleteAllElectric(property.Id, property.OrgId));

        await db.SaveChangesAsync();
        return property;
    }
}
