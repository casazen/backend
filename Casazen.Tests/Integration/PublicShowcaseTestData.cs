using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Unit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Casazen.Tests.Integration;

/// <summary>
/// An active supplier of the public showcase tests (SP-09) and the private data around it that no public answer may ever
/// contain: the phone, the e-mail, the VAT number, the private labels of its calendar.
/// </summary>
/// <param name="OrgId">The supplier org.</param>
/// <param name="Slug">The showcase slug.</param>
/// <param name="Secrets">Every private text seeded for this supplier: none may appear in any answer.</param>
internal sealed record PublicShowcaseSupplier(Guid OrgId, string Slug, IReadOnlyList<string> Secrets);

/// <summary>
/// The default integration host with the flag <c>SupplierShowcaseBooking</c> on and a clock the test moves: Monday 12 October
/// 2026, 08:45 in Rome (06:45 UTC). The clock is the host's <see cref="TimeProvider"/>, so the slot planner and the cache
/// both read it: the slots of a test are exact, and the 30 seconds of the cache pass when the test says so.
/// </summary>
public class PublicShowcaseFactory : CasazenWebApplicationFactory
{
    /// <summary>Monday 12 October 2026, 08:45 in Rome (summer time): a quarter of an hour before the first slot of a working day.</summary>
    public static readonly DateTimeOffset Start = new(2026, 10, 12, 6, 45, 0, TimeSpan.Zero);

    public FakeTimeProvider Clock { get; } = new(Start);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["Features:SupplierShowcaseBooking"] = "true" }.Concat(ExtraSettings())));
        builder.ConfigureTestServices(services =>
        {
            RemoveService<TimeProvider>(services);
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IStartupFilter, TestPeerIpStartupFilter>();
        });
    }

    /// <summary>More configuration for a host of a test class that needs it.</summary>
    protected virtual IEnumerable<KeyValuePair<string, string?>> ExtraSettings() => [];
}

/// <summary>The showcase host with both new rate limits at two requests per minute, to see them answer 429.</summary>
public sealed class PublicShowcaseThrottledFactory : PublicShowcaseFactory
{
    protected override IEnumerable<KeyValuePair<string, string?>> ExtraSettings() =>
    [
        new("RateLimiting:PublicSupplierSlots:PermitLimit", "2"),
        new("RateLimiting:PublicSupplierQuote:PermitLimit", "2"),
        new("RateLimiting:PublicRead:PermitLimit", "1000"),
    ];
}

/// <summary>Seeds for the public showcase tests (SP-09): rows written straight to the database, valid on PostgreSQL and in memory.</summary>
internal static class PublicShowcaseTestData
{
    public const string Phone = "+39 06 5550199";
    public const string BlockLabel = "Dentista riservato del titolare";
    public const string TimeOffLabel = "Operazione al ginocchio";

    /// <summary>
    /// A supplier org with a profile (with a showcase slug, phone, e-mail and VAT number that must stay private) and, unless
    /// <paramref name="withHours"/> is false, working hours Monday to Friday 09:00-13:00 and 14:00-18:00 (Rome). It takes the
    /// default rules: 30 minutes between jobs, 3 jobs a day, 24 hours' notice, 35 days ahead, a slot every hour.
    /// </summary>
    public static async Task<PublicShowcaseSupplier> SeedSupplierAsync(
        CasazenWebApplicationFactory factory,
        SupplierStatus status = SupplierStatus.Active,
        bool withHours = true,
        string comuni = """["H501"]""")
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var slug = $"vetrina-{suffix}";
        var email = $"privata.{suffix}@example.com";
        var vat = $"IT{Random.Shared.NextInt64(10_000_000_000, 99_999_999_999)}";

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = new OrgEntity
        {
            Name = $"Vetrina {suffix}",
            Slug = $"supplier-{suffix}",
            DisplayName = $"Vetrina {suffix}",
            ContactEmail = email,
            OrgType = OrgType.Supplier,
            PlanTier = PlanTier.Starter,
            IsActive = true,
        };
        db.Orgs.Add(org);
        db.SupplierProfiles.Add(new SupplierProfile
        {
            OrgId = org.Id,
            Email = email,
            LegalName = $"Vetrina {suffix} Srl",
            VatNumber = vat,
            Phone = Phone,
            Status = status,
            ShowcaseSlug = slug,
            ComuniJson = comuni,
            CategoriesJson = """["cleaning"]""",
            Bio = "Pulizie professionali per case vacanza.",
            PhotoUrlsJson = """["https://storage.test/vetrina.jpg"]""",
            TosAcceptedAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
        });

        if (withHours)
        {
            foreach (var weekday in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
            {
                db.SupplierWorkingHours.Add(new SupplierWorkingHours { OrgId = org.Id, Weekday = weekday, StartMinute = 9 * 60, EndMinute = 13 * 60 });
                db.SupplierWorkingHours.Add(new SupplierWorkingHours { OrgId = org.Id, Weekday = weekday, StartMinute = 14 * 60, EndMinute = 18 * 60 });
            }
        }

        await db.SaveChangesAsync();
        return new PublicShowcaseSupplier(org.Id, slug, [Phone, email, vat]);
    }

    /// <summary>A service row of <paramref name="orgId"/>; returns its slug.</summary>
    public static async Task<string> SeedServiceAsync(
        CasazenWebApplicationFactory factory,
        Guid orgId,
        string name = "Pulizia profonda",
        SupplierServiceListingStatus status = SupplierServiceListingStatus.Active,
        int? priceFromCents = 6000,
        SupplierServicePriceUnit priceUnit = SupplierServicePriceUnit.PerJob,
        bool requiresQuote = false,
        bool pricesIncludeVat = false,
        int? durationMinutes = 120,
        int sortOrder = 0,
        string supplementsJson = "[]",
        DateTime? deletedAt = null,
        string? slug = null)
    {
        slug ??= $"{SupplierShowcaseSlug.FromName(name, "servizio")}-{Guid.NewGuid().ToString("N")[..6]}";
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SupplierServiceListings.Add(new SupplierServiceListing
        {
            OrgId = orgId,
            Slug = slug,
            Name = name,
            Category = ServiceCategories.Cleaning,
            Summary = $"{name}: riassunto.",
            Description = $"{name}: descrizione lunga.",
            PriceFromCents = priceFromCents,
            PriceUnit = priceUnit,
            PricesIncludeVat = pricesIncludeVat,
            RequiresQuote = requiresQuote,
            DurationMinutes = durationMinutes,
            SortOrder = sortOrder,
            SupplementsJson = supplementsJson,
            IncludedJson = """["Bagni e cucina"]""",
            ExcludedJson = """["Vetri esterni"]""",
            PhotoUrlsJson = """["https://storage.test/servizio.jpg"]""",
            Status = status,
            DeletedAt = deletedAt,
            CreatedAt = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc).AddMinutes(sortOrder),
            UpdatedAt = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        return slug;
    }

    /// <summary>
    /// A request of a host for <paramref name="supplierOrgId"/> with hours (<paramref name="startUtc"/> to
    /// <paramref name="endUtc"/>): the host, its property, its guest and its private notes are seeded around it. Returns the
    /// private texts that must never reach a public answer.
    /// </summary>
    public static async Task<IReadOnlyList<string>> SeedTimedRequestAsync(
        CasazenWebApplicationFactory factory,
        Guid supplierOrgId,
        DateTime startUtc,
        DateTime endUtc,
        ServiceRequestStatus status = ServiceRequestStatus.Richiesto)
    {
        var (requestId, address, guestName) = await SupplierAgendaTestData.SeedRequestAsync(
            factory, supplierOrgId, DateOnly.FromDateTime(startUtc), status);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.ServiceRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == requestId);
        request.ScheduledStartUtc = startUtc;
        request.ScheduledEndUtc = endUtc;
        await db.SaveChangesAsync();
        return [address, guestName, "Note riservate dell'host", "Casa Riservata", "Romeo"];
    }

    /// <summary>A manual block with a private label.</summary>
    public static Task<SupplierBusyWindow> SeedBlockAsync(CasazenWebApplicationFactory factory, Guid orgId, DateTime startUtc, DateTime endUtc) =>
        SupplierAgendaTestData.SeedWindowAsync(factory, orgId, startUtc, endUtc, SupplierBusyWindowKind.Block);

    /// <summary>A time off with a private label.</summary>
    public static async Task SeedTimeOffAsync(CasazenWebApplicationFactory factory, Guid orgId, DateOnly from, DateOnly to)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.SupplierTimeOff.Add(new SupplierTimeOff { OrgId = orgId, FromDate = from, ToDate = to, Reason = SupplierTimeOffReason.Illness, Label = TimeOffLabel });
        await db.SaveChangesAsync();
    }

    /// <summary>A request the supplier took <paramref name="minutesToTake"/> minutes after it arrived, <paramref name="daysAgo"/> days before <paramref name="now"/>.</summary>
    public static async Task SeedAnsweredRequestAsync(
        CasazenWebApplicationFactory factory,
        Guid supplierOrgId,
        DateTimeOffset now,
        int daysAgo,
        int minutesToTake)
    {
        var (requestId, _, _) = await SupplierAgendaTestData.SeedRequestAsync(
            factory, supplierOrgId, DateOnly.FromDateTime(now.UtcDateTime), ServiceRequestStatus.PresoInCarico);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var request = await db.ServiceRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == requestId);
        request.CreatedAt = now.UtcDateTime.AddDays(-daysAgo);
        request.TakenAt = request.CreatedAt.AddMinutes(minutesToTake);
        await db.SaveChangesAsync();
    }
}
