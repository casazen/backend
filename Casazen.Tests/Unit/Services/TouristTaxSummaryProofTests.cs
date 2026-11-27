using System.Reflection;
using System.Text;
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

        SeedStay(db, loft, Day(2026, 3, 2), nights: 3, touristTax: 57.35m);
        SeedStay(db, bilocale, Day(2026, 3, 20), nights: 2, touristTax: 9.55m);
        SeedStay(db, loft, Day(2026, 3, 28), nights: 1, touristTax: 0m);
        SeedStay(db, roma, Day(2026, 2, 10), nights: 4, touristTax: 72.10m);
        SeedStay(db, loft, Day(2026, 4, 6), nights: 2, touristTax: 40m);

        SeedPayment(db, orgId, "Milano", FirstQuarter.From, FirstQuarter.To, 40.01m, new DateOnly(2026, 4, 10));
        SeedPayment(db, orgId, "milano", FirstQuarter.From, FirstQuarter.To, 26.89m, new DateOnly(2026, 4, 20));
        SeedPayment(db, orgId, "Milano", new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30), 40m, new DateOnly(2026, 7, 10));
        SeedPayment(db, orgId, "Milano", new DateOnly(2026, 3, 1), new DateOnly(2026, 4, 30), 5m, new DateOnly(2026, 5, 10));
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
        Assert.Equal([40.01m, 26.89m], milano.Remittances!.Select(r => r.Amount));

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

    [Fact]
    public void TouristTaxRemittance_RecordRequiresPaymentWrite_Test()
    {
        var controller = typeof(TouristTaxSummaryController);

        Assert.Contains(CasazenPolicies.PaymentRead, PoliciesOf(controller));
        foreach (var write in new[] { nameof(TouristTaxSummaryController.RecordRemittance), nameof(TouristTaxSummaryController.DeleteRemittance) })
            Assert.Contains(CasazenPolicies.PaymentWrite, PoliciesOf(controller.GetMethod(write)!));
        foreach (var read in new[] { nameof(TouristTaxSummaryController.GetSummary), nameof(TouristTaxSummaryController.GetReceipt) })
            Assert.DoesNotContain(CasazenPolicies.PaymentWrite, PoliciesOf(controller.GetMethod(read)!));

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

    [Fact]
    public async Task TouristTaxSummary_ScopeLimitedToSomeProperties_SeesOnlyWhatIsOwedAndTouchesNoPayment_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var mine = SeedProperty(db, orgId, "Loft Milano", "Milano", ownerId: "auth0|member");
        var sibling = SeedProperty(db, orgId, "Bilocale Milano", "Milano");
        var roma = SeedProperty(db, orgId, "Casa Roma", "Roma");
        SeedStay(db, mine, Day(2026, 3, 2), nights: 3, touristTax: 20m);
        SeedStay(db, sibling, Day(2026, 3, 20), nights: 2, touristTax: 30m);
        SeedStay(db, roma, Day(2026, 2, 10), nights: 4, touristTax: 72.10m);

        var payment = SeedPayment(db, orgId, "Milano", FirstQuarter.From, FirstQuarter.To, 50m, new DateOnly(2026, 4, 10));
        SeedPayment(db, orgId, "Roma", FirstQuarter.From, FirstQuarter.To, 72.10m, new DateOnly(2026, 4, 12));
        await db.SaveChangesAsync();
        var sut = CreateService(db);
        var limited = new HostScope(orgId, OwnerId: "auth0|member");

        var summary = await sut.GetSummaryAsync(limited, FirstQuarter);

        Assert.True(summary.PaymentsHidden);
        var milano = Assert.Single(summary.Comuni);
        Assert.Equal(("Milano", 1, 3, 20.00m), (milano.Comune, milano.Stays, milano.Nights, milano.Owed));
        Assert.Null(milano.Paid);
        Assert.Null(milano.Balance);
        Assert.Null(milano.Status);
        Assert.Null(milano.Remittances);
        Assert.Equal(20.00m, summary.Total.Owed);
        Assert.Null(summary.Total.Paid);
        Assert.Null(summary.Total.Balance);

        var file = Encoding.UTF8.GetString(TouristTaxSummaryDocuments.Csv(summary, new TouristTaxCsvTexts("Nessun importo registrato", "Importo incompleto")));
        Assert.Contains("Comune;Soggiorni;Notti;Ospiti;Imposta dovuta (€);Soggiorni senza importo;Nota\r\n", file);
        Assert.Contains("Milano;1;3;2;20,00;0;\r\n", file);
        Assert.DoesNotContain("Versamenti registrati", file);

        foreach (var attempt in new Func<Task>[]
        {
            () => sut.RecordRemittanceAsync(limited, NewRequest("Milano"), receipt: null),
            () => sut.DeleteRemittanceAsync(limited, payment.Id),
            () => sut.OpenReceiptAsync(limited, payment.Id),
        })
        {
            var refused = await Assert.ThrowsAsync<DomainForbiddenException>(attempt);
            Assert.Equal(TouristTaxErrorCodes.PaymentsNeedWholeOrg, refused.Code);
        }

        Assert.Equal(2, await db.TouristTaxRemittances.CountAsync());

        var whole = await sut.GetSummaryAsync(new HostScope(orgId), FirstQuarter);
        Assert.False(whole.PaymentsHidden);
        Assert.Equal(["Milano", "Roma"], whole.Comuni.Select(c => c.Comune));
        Assert.Equal((50.00m, 50.00m, TouristTaxSummaryStatus.Paid), (whole.Comuni[0].Owed, whole.Comuni[0].Paid, whole.Comuni[0].Status));
    }

    [Fact]
    public async Task TouristTaxSummary_AHouseAbroad_IsNeitherCountedNorPayable_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var como = SeedProperty(db, orgId, "Casa Como", "Como");
        var varese = SeedProperty(db, orgId, "Casa Varese", "Varese", country: "it");
        var chaletComo = SeedProperty(db, orgId, "Chalet Como", "Como", country: "CH");
        var lugano = SeedProperty(db, orgId, "Chalet Lugano", "Lugano", country: "CH");

        SeedStay(db, como, Day(2026, 2, 5), nights: 2, touristTax: 12m);
        SeedStay(db, varese, Day(2026, 2, 20), nights: 3, touristTax: 9m);
        SeedStay(db, chaletComo, Day(2026, 2, 12), nights: 4, touristTax: 0m);
        SeedStay(db, lugano, Day(2026, 3, 3), nights: 2, touristTax: 0m);
        await db.SaveChangesAsync();
        var sut = CreateService(db);
        var scope = new HostScope(orgId);

        var summary = await sut.GetSummaryAsync(scope, FirstQuarter);

        Assert.Equal(["Como", "Varese"], summary.Comuni.Select(c => c.Comune));
        var comoRow = summary.Comuni[0];
        Assert.Equal((1, 2, 12.00m, 0), (comoRow.Stays, comoRow.Nights, comoRow.Owed, comoRow.StaysWithoutAmount));
        Assert.Equal(new TouristTaxSummaryTotals(2, 5, 4, 21.00m, 0, 0m, 21.00m), summary.Total);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => sut.RecordRemittanceAsync(scope, NewRequest("Lugano"), receipt: null));
        Assert.Equal(TouristTaxErrorCodes.ComuneUnknown, refused.Code);
        Assert.Empty(await db.TouristTaxRemittances.ToListAsync());

        var line = await sut.RecordRemittanceAsync(scope, NewRequest("Como"), receipt: null);
        Assert.Equal("Como", line.Comune);
    }

    [Fact]
    public async Task TouristTaxReceipt_OneByteOverTenMegabytes_IsRefusedAndNothingIsStored_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedProperty(db, orgId, "Loft Milano", "Milano");
        await db.SaveChangesAsync();
        var storage = new Mock<IFileStorage>();
        var sut = CreateService(db, storage.Object);
        var scope = new HostScope(orgId);

        var content = new byte[TouristTaxRemittanceLimits.MaxReceiptBytes + 1];
        "%PDF-"u8.CopyTo(content);

        await using (var tooLarge = new MemoryStream(content))
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(
                () => sut.RecordRemittanceAsync(scope, NewRequest("Milano"), tooLarge));
            Assert.Equal(TouristTaxErrorCodes.ReceiptInvalid, refused.Code);
        }

        Assert.Empty(await db.TouristTaxRemittances.ToListAsync());
        storage.Verify(
            s => s.PutAsync(It.IsAny<StorageBucket>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        await using var atTheLimit = new MemoryStream(content, 0, (int)TouristTaxRemittanceLimits.MaxReceiptBytes);
        var line = await sut.RecordRemittanceAsync(scope, NewRequest("Milano"), atTheLimit);

        Assert.True(line.HasReceipt);
        storage.Verify(
            s => s.PutAsync(
                StorageBucket.Private,
                It.Is<string>(key => key.EndsWith(".pdf", StringComparison.Ordinal)),
                It.IsAny<Stream>(),
                "application/pdf",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TouristTaxRemittance_OfAnotherOrg_IsNotFoundAndLeftAlone_Test()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        await using var db = CreateDb();
        var theirs = SeedPayment(db, orgB, "Roma", FirstQuarter.From, FirstQuarter.To, 10m, new DateOnly(2026, 4, 12));
        await db.SaveChangesAsync();
        var sut = CreateService(db);

        foreach (var attempt in new Func<Task>[]
        {
            () => sut.DeleteRemittanceAsync(new HostScope(orgA), theirs.Id),
            () => sut.OpenReceiptAsync(new HostScope(orgA), theirs.Id),
            () => sut.DeleteRemittanceAsync(new HostScope(orgA), Guid.NewGuid()),
        })
        {
            var notFound = await Assert.ThrowsAsync<NotFoundException>(attempt);
            Assert.Equal(TouristTaxErrorCodes.RemittanceNotFound, notFound.Code);
        }

        Assert.Equal(theirs.Id, Assert.Single(await db.TouristTaxRemittances.ToListAsync()).Id);
    }

    private static IEnumerable<string?> PoliciesOf(MemberInfo member) =>
        member.GetCustomAttributes<AuthorizeAttribute>(inherit: true).Select(a => a.Policy);

    private static RecordRemittanceRequest NewRequest(string comune) =>
        new(comune, FirstQuarter.From, FirstQuarter.To, 25.50m, new DateOnly(2026, 4, 15), "Bonifico di prova", null, "auth0|host");

    private static TouristTaxSummaryService CreateService(AppDbContext db, IFileStorage? storage = null)
    {
        var clock = new FakeTimeProvider(Now);
        var fiscal = new FiscalService(db, new MigraDocPdfDocumentRenderer(), Options.Create(new ShortStayFiscalOptions()), clock);
        return new TouristTaxSummaryService(db, fiscal, storage ?? Mock.Of<IFileStorage>(), NullLogger<TouristTaxSummaryService>.Instance, clock);
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

    private static Property SeedProperty(
        AppDbContext db,
        Guid orgId,
        string name,
        string city,
        string ownerId = "auth0|host",
        string country = "IT")
    {
        var property = new Property
        {
            OrgId = orgId,
            OwnerId = ownerId,
            Name = name,
            Address = "Via Test 1",
            City = city,
            PostalCode = "00100",
            Country = country,
            Currency = country == "CH" ? "CHF" : "EUR",
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

    private static TouristTaxRemittance SeedPayment(
        AppDbContext db,
        Guid orgId,
        string comune,
        DateOnly from,
        DateOnly to,
        decimal amount,
        DateOnly paidOn)
    {
        var payment = new TouristTaxRemittance
        {
            OrgId = orgId,
            Comune = comune,
            ComuneKey = TouristTaxComune.NormalizeName(comune),
            PeriodFrom = from,
            PeriodTo = to,
            PaidAmount = amount,
            PaidOn = paidOn,
            RecordedByUserId = "auth0|host",
        };
        db.TouristTaxRemittances.Add(payment);
        return payment;
    }
}
