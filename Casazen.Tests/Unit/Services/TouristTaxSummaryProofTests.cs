using System.Reflection;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Web.Authorization;
using Casazen.Web.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-100, E07/F03/US04/T02: the tourist tax owed, paid and receipted per comune. One proof for each rule that costs if it fails: the money
/// (owed, paid and balance to the cent), the permission (who may record or delete a payment) and the data (the comune of a payment is one of
/// the org's).
/// </summary>
public sealed class TouristTaxSummaryProofTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    private static readonly FiscalReportPeriod FirstQuarter = new(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31));

    /// <summary>
    /// PROOF TEST: money rule. Owed is the sum of the amounts recorded on the stays, paid the sum of the payments inside the period
    /// (instalments included), and the balance exactly their difference to the cent, with an overpayment negative. A stay with no recorded
    /// amount is counted apart and never taken as a zero that closes the account; a payment that is not inside the period is not counted.
    /// </summary>
    [Fact]
    public async Task TouristTaxSummary_OwedPaidBalanceToTheCent_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var loft = SeedProperty(db, orgId, "Loft Milano", "Milano");
        var bilocale = SeedProperty(db, orgId, "Bilocale Milano", "  milano ");
        var roma = SeedProperty(db, orgId, "Casa Roma", "Roma");

        // Milano: 57.35 + 9.55 recorded, and one stay with no amount. Roma: 72.10. A Milano stay in April is outside the period.
        SeedStay(db, loft, Day(2026, 3, 2), nights: 3, touristTax: 57.35m);
        SeedStay(db, bilocale, Day(2026, 3, 20), nights: 2, touristTax: 9.55m);
        SeedStay(db, loft, Day(2026, 3, 28), nights: 1, touristTax: 0m);
        SeedStay(db, roma, Day(2026, 2, 10), nights: 4, touristTax: 72.10m);
        SeedStay(db, loft, Day(2026, 4, 6), nights: 2, touristTax: 40m);

        // Milano is paid in two instalments of the whole quarter (40.01 + 26.89 = 66.90). Two more payments are not inside the quarter.
        SeedPayment(db, orgId, "Milano", FirstQuarter.From, FirstQuarter.To, 40.01m, new DateOnly(2026, 4, 10));
        SeedPayment(db, orgId, "milano", FirstQuarter.From, FirstQuarter.To, 26.89m, new DateOnly(2026, 4, 20));
        SeedPayment(db, orgId, "Milano", new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30), 40m, new DateOnly(2026, 7, 10));
        SeedPayment(db, orgId, "Milano", new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 30), 5m, new DateOnly(2026, 5, 10));
        // Roma is paid ten cents too much, and Como (no stays) was paid something.
        SeedPayment(db, orgId, "Roma", FirstQuarter.From, FirstQuarter.To, 72.20m, new DateOnly(2026, 4, 12));
        SeedPayment(db, orgId, "Como", FirstQuarter.From, FirstQuarter.To, 5m, new DateOnly(2026, 4, 12));
        await db.SaveChangesAsync();

        var summary = await CreateService(db).GetSummaryAsync(new HostScope(orgId), FirstQuarter);

        Assert.Equal(["Como", "Milano", "Roma"], summary.Comuni.Select(c => c.Comune));

        var milano = summary.Comuni[1];
        Assert.Equal((3, 6, 6), (milano.Stays, milano.Nights, milano.Guests));
        Assert.Equal(66.90m, milano.Owed);
        Assert.Equal(1, milano.StaysWithoutAmount);
        Assert.Equal(66.90m, milano.Paid);
        Assert.Equal(0m, milano.Balance);
        Assert.Equal(TouristTaxSummaryStatus.Paid, milano.Status);
        Assert.Equal([40.01m, 26.89m], milano.Remittances.Select(r => r.Amount));

        var romaRow = summary.Comuni[2];
        Assert.Equal(72.10m, romaRow.Owed);
        Assert.Equal(72.20m, romaRow.Paid);
        Assert.Equal(-0.10m, romaRow.Balance);
        Assert.Equal(TouristTaxSummaryStatus.Overpaid, romaRow.Status);

        var como = summary.Comuni[0];
        Assert.Equal((0, 0m, 5m, -5m), (como.Stays, como.Owed, como.Paid, como.Balance));
        Assert.Equal(TouristTaxSummaryStatus.Overpaid, como.Status);

        Assert.Equal(new TouristTaxSummaryTotals(4, 10, 8, 139.00m, 1, 144.10m, -5.10m), summary.Total);
    }

    /// <summary>
    /// PROOF TEST: permission rule. Recording and deleting a payment ask <c>payment.write</c> on top of the <c>payment.read</c> of the class,
    /// and the roles that hold <c>payment.write</c> in the short-rent area are the owner, the administrators and the property managers: not
    /// the accountant (who reads) and not the collaborator. The reads do not ask for it.
    /// </summary>
    [Fact]
    public void TouristTaxRemittance_RecordRequiresPaymentWrite_Test()
    {
        var controller = typeof(TouristTaxSummaryController);

        Assert.Contains(CasazenPolicies.PaymentRead, PoliciesOf(controller));
        foreach (var write in new[] { nameof(TouristTaxSummaryController.RecordRemittance), nameof(TouristTaxSummaryController.DeleteRemittance) })
            Assert.Contains(CasazenPolicies.PaymentWrite, PoliciesOf(controller.GetMethod(write)!));
        foreach (var read in new[] { nameof(TouristTaxSummaryController.GetSummary), nameof(TouristTaxSummaryController.GetReceipt) })
            Assert.DoesNotContain(CasazenPolicies.PaymentWrite, PoliciesOf(controller.GetMethod(read)!));

        // What each role holds, as seeded (what the migrations leave in the database).
        using var db = OrgTeamTestData.NewDb();
        var held = db.RolePermissions.Include(rp => rp.Role)
            .Where(rp => rp.Role.ContextKey == OrgRoleCatalog.ShortRent)
            .AsEnumerable()
            .GroupBy(rp => rp.Role.RoleKey)
            .ToDictionary(group => group.Key, group => group.Select(rp => rp.PermissionKey).ToHashSet(StringComparer.Ordinal));
        var holders = Enum.GetValues<OrgRole>()
            .Where(role => held.TryGetValue(OrgRoleCatalog.HostRoleKey(role, OrgRoleCatalog.ShortRent), out var permissions)
                           && permissions.Contains("payment.write"))
            .ToList();

        Assert.Equal(new[] { OrgRole.Owner, OrgRole.Admin, OrgRole.PropertyManager }.Order(), holders.Order());
        Assert.DoesNotContain(OrgRole.Accountant, holders);
        Assert.DoesNotContain(OrgRole.Collaborator, holders);
    }

    /// <summary>
    /// PROOF TEST: data rule. A payment is recorded for a comune of the properties of the caller's org: the comune of another org's
    /// property, or one that is nowhere, is refused and nothing is stored; the comune of the org is accepted however it is typed (case,
    /// spaces), and the row is the org's.
    /// </summary>
    [Fact]
    public async Task TouristTaxRemittance_ComuneMustBelongToTheOrg_Test()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        await using var db = CreateDb();
        SeedProperty(db, orgA, "Loft Milano", "Milano");
        SeedProperty(db, orgB, "Casa Roma", "Roma");
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        foreach (var elsewhere in new[] { "Roma", "Atlantide", "   ", string.Empty })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(
                () => sut.RecordRemittanceAsync(new HostScope(orgA), NewRequest(elsewhere), receipt: null));
            Assert.Equal(TouristTaxErrorCodes.ComuneUnknown, refused.Code);
        }

        Assert.Empty(await db.TouristTaxRemittances.ToListAsync());

        var line = await sut.RecordRemittanceAsync(new HostScope(orgA), NewRequest(" MILANO "), receipt: null);

        Assert.Equal("MILANO", line.Comune);
        var stored = Assert.Single(await db.TouristTaxRemittances.ToListAsync());
        Assert.Equal(line.Id, stored.Id);
        Assert.Equal(orgA, stored.OrgId);
        Assert.Equal("milano", stored.ComuneKey);
        Assert.Equal(25.50m, stored.PaidAmount);
    }

    private static IEnumerable<string?> PoliciesOf(MemberInfo member) =>
        member.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy);

    private static RecordRemittanceRequest NewRequest(string comune) =>
        new(comune, FirstQuarter.From, FirstQuarter.To, 25.50m, new DateOnly(2026, 4, 15), "Bonifico di prova", null, "auth0|host");

    private static TouristTaxSummaryService CreateService(AppDbContext db)
    {
        var clock = new FakeTimeProvider(Now);
        var fiscal = new FiscalService(db, new MigraDocPdfDocumentRenderer(), Options.Create(new ShortStayFiscalOptions()), clock);
        return new TouristTaxSummaryService(db, fiscal, Mock.Of<IFileStorage>(), NullLogger<TouristTaxSummaryService>.Instance, clock);
    }

    private static AppDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static DateTime Day(int year, int month, int day) => new(year, month, day, 0, 0, 0, DateTimeKind.Utc);

    private static void SeedOrg(AppDbContext db, Guid orgId) =>
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "Host",
            Slug = $"org-{orgId:N}"[..20],
            DisplayName = "Host",
            ContactEmail = "h@example.com",
        });

    private static Property SeedProperty(AppDbContext db, Guid orgId, string name, string city)
    {
        var property = new Property
        {
            OrgId = orgId,
            OwnerId = "auth0|host",
            Name = name,
            Address = "Via Test 1",
            City = city,
            PostalCode = "00100",
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
        };
        db.Properties.Add(property);
        return property;
    }

    private static void SeedStay(AppDbContext db, Property property, DateTime checkIn, int nights, decimal touristTax)
    {
        db.Bookings.Add(new Booking
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            Property = property,
            CheckInDate = checkIn,
            CheckOutDate = checkIn.AddDays(nights),
            NumberOfGuests = 2,
            Status = BookingStatus.Confirmed,
            Source = BookingSource.Direct,
            TouristTax = touristTax,
            TouristTaxAmount = touristTax,
        });
    }

    private static void SeedPayment(AppDbContext db, Guid orgId, string comune, DateOnly from, DateOnly to, decimal amount, DateOnly paidOn) =>
        db.TouristTaxRemittances.Add(new TouristTaxRemittance
        {
            OrgId = orgId,
            Comune = comune,
            ComuneKey = TouristTaxComune.NormalizeName(comune),
            PeriodFrom = from,
            PeriodTo = to,
            PaidAmount = amount,
            PaidOn = paidOn,
            RecordedByUserId = "auth0|host",
        });
}
