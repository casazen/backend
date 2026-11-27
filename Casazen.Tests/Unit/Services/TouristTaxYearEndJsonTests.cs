using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using Casazen.Core.Authorization;
using Casazen.Core.Documents;
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
using Casazen.Web.DTOs;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using FiscalDocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FIX/BE-YEAREND-JSON (E18/F01/US02/T03): the three year-end reads of the tourist tax answer a JSON when it is asked by name (<c>format=json</c>), by the same road as the files: the same policy, the same refusal of a
/// scope that does not reach the whole org, the same decision on <c>guest.read</c>. The controller is built with the real service over an in-memory database, as the tests of the files do; the JSON is what the
/// API serializes (camel case, enums as names). Everything is invented (TEST properties, @example.com).
/// </summary>
public sealed class TouristTaxYearEndJsonTests
{
    private static readonly DateTimeOffset Now = new(2026, 12, 31, 10, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The reasons of an exemption that can reveal a health condition (art. 9 GDPR): the codes of the catalogue and the «erased» of a reason that went with the data of the guest.</summary>
    private static readonly string[] ReasonCodes =
    [
        TouristTaxExemptionReason.ResidentCode,
        TouristTaxExemptionReason.HealthCareCode,
        TouristTaxExemptionReason.DisabilityCompanionCode,
        TouristTaxExemptionReason.PublicServiceCode,
        TouristTaxExemptionReason.HostFamilyCode,
        TouristTaxExemptionReason.OtherCode,
        TouristTaxExemptionKinds.Erased,
    ];

    /// <summary>
    /// Privacy (art. 9 GDPR): the reason of an exemption can reveal a health condition, so the declaration to the comune in JSON, serialized as the API serializes it, names no reason to a caller that does not hold
    /// <c>guest.read</c> (the accountant holds <c>payment.read</c> and not <c>guest.read</c>): the age and one «restricted» row that counts the same stays, guests and nights, and not a code anywhere in the
    /// text. The same data for a caller that holds <c>guest.read</c> has the codes: the reasons are decided on the server, never by the controller or the client.
    /// </summary>
    [Fact]
    public async Task YearEndDeclarationJson_WithoutGuestRead_HidesTheReasons_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var loft = SeedProperty(db, orgId, "Loft Milano", "Milano");

        // A minor and a guest exempt for a health reason, two residents, and a guest whose reason was erased with the data.
        var health = SeedStay(db, loft, Day(2026, 3, 2), nights: 3, touristTax: 20m);
        SeedRecord(db, orgId, health, (TouristTaxExemptionKind.Age, TouristTaxExemptionReason.MinorAgeCode), (TouristTaxExemptionKind.Host, TouristTaxExemptionReason.HealthCareCode));
        var residents = SeedStay(db, loft, Day(2026, 5, 10), nights: 1, touristTax: 0m);
        SeedRecord(db, orgId, residents, (TouristTaxExemptionKind.Host, TouristTaxExemptionReason.ResidentCode), (TouristTaxExemptionKind.Host, TouristTaxExemptionReason.ResidentCode));
        var erased = SeedStay(db, loft, Day(2026, 6, 15), nights: 2, touristTax: 0m);
        SeedRecord(db, orgId, erased, (TouristTaxExemptionKind.Host, null));
        await db.SaveChangesAsync();

        // Without guest.read: the age and «restricted», with the counts of the reasons it folds (3 stays, 4 guests, 7 nights of guests), and no reason anywhere in the text.
        var hidden = await DeclarationJsonAsync(db, orgId, holdsGuestRead: false);
        List<(string Reason, int Stays, int Guests, int GuestNights)> restricted = [(TouristTaxExemptionReason.MinorAgeCode, 1, 1, 3), (TouristTaxExemptionKinds.Restricted, 3, 4, 7)];
        Assert.Equal(restricted, ExemptionsOf(hidden));
        foreach (var code in ReasonCodes)
            Assert.DoesNotContain(code, hidden, StringComparison.Ordinal);

        // With guest.read the same data names every reason: the contrast is the rule, not an empty answer.
        var shown = await DeclarationJsonAsync(db, orgId, holdsGuestRead: true);
        List<(string Reason, int Stays, int Guests, int GuestNights)> named =
        [
            (TouristTaxExemptionKinds.Erased, 1, 1, 2),
            (TouristTaxExemptionReason.HealthCareCode, 1, 1, 3),
            (TouristTaxExemptionReason.MinorAgeCode, 1, 1, 3),
            (TouristTaxExemptionReason.ResidentCode, 1, 2, 2),
        ];
        Assert.Equal(named, ExemptionsOf(shown));
        foreach (var code in new[] { TouristTaxExemptionReason.HealthCareCode, TouristTaxExemptionReason.ResidentCode, TouristTaxExemptionKinds.Erased })
            Assert.Contains($"\"{code}\"", shown, StringComparison.Ordinal);
    }

    /// <summary>
    /// Permission: the JSON needs a scope that reaches the whole org, as the files do (a declaration of some of the properties would be false, and the payments belong to the org): a caller that reaches only some
    /// of the properties (the ones it was given, or the ones it created) is refused with 403 <c>tourist_tax_year_end_needs_whole_org</c> on the three reads, in JSON and in CSV, before anything is read (even a year the
    /// reports do not support is the refusal of the scope), while the whole org reads the three of them.
    /// </summary>
    [Fact]
    public async Task YearEndJson_NeedsTheWholeOrganization_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var loft = SeedProperty(db, orgId, "Loft Milano", "Milano");
        SeedStay(db, loft, Day(2026, 3, 2), nights: 3, touristTax: 20m);
        await db.SaveChangesAsync();

        foreach (var restricted in new[] { new HostScope(orgId, GrantedToUserId: "auth0|collaboratore"), new HostScope(orgId, OwnerId: "auth0|collaboratore") })
        {
            var controller = CreateController(db, restricted, holdsGuestRead: true);
            foreach (var format in new[] { "json", "csv" })
            {
                foreach (var year in new[] { 2026, 1999 })
                {
                    foreach (var read in Reads(controller, year, format))
                    {
                        var refusal = await Assert.ThrowsAsync<DomainForbiddenException>(read);
                        Assert.Equal(TouristTaxErrorCodes.YearEndNeedsWholeOrg, refusal.Code);
                    }
                }
            }
        }

        var whole = CreateController(db, new HostScope(orgId), holdsGuestRead: false);
        Assert.IsType<TouristTaxDeclarationDto>(Assert.IsType<OkObjectResult>(await whole.GetYearEndDeclaration(2026, "json", CancellationToken.None)).Value);
        Assert.IsType<TouristTaxAgentAccountDto>(Assert.IsType<OkObjectResult>(await whole.GetYearEndAgentAccount(2026, "json", null, CancellationToken.None)).Value);
        Assert.IsType<TicinoYearlyCountDto>(Assert.IsType<OkObjectResult>(await whole.GetYearEndTicinoCount(2026, "json", CancellationToken.None)).Value);
    }

    /// <summary>
    /// Data: the JSON of the count of Ticino keeps the unknown canton on each property (a property in Switzerland with no municipality, or with one the registry does not have, may be in Ticino: it is counted
    /// and flagged, never dropped) and carries <c>cantonUnknownCount</c>, the number of those properties, so the screen says it without counting; a property of another canton is not in it, the amounts are numbers
    /// in the currency of the property and the day it is due is an ISO date. The org with no such property says 0, and does not see the properties of another org.
    /// </summary>
    [Fact]
    public async Task TicinoJson_CountsTheUnknownCantons_Test()
    {
        var orgId = Guid.NewGuid();
        var otherOrgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        SeedOrg(db, otherOrgId);
        db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = "5192", CantonCode = "TI", OfficialName = "Lugano" });
        db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = "5113", CantonCode = "TI", OfficialName = "Locarno" });
        db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = "261", CantonCode = "ZH", OfficialName = "Zürich" });
        var lugano = SeedProperty(db, orgId, "Casa Lugano", "Lugano", country: "CH", municipality: "5192");
        var withoutMunicipality = SeedProperty(db, orgId, "Villa Senza Comune", "Chiasso", country: "CH", municipality: null);
        var notInRegistry = SeedProperty(db, orgId, "Villa Comune Ignoto", "Mendrisio", country: "CH", municipality: "9999");
        var zurich = SeedProperty(db, orgId, "Casa Zurigo", "Zürich", country: "CH", municipality: "261");
        var otherOrgHouse = SeedProperty(db, otherOrgId, "Casa Locarno", "Locarno", country: "CH", municipality: "5113");

        var taxed = SeedStay(db, lugano, Day(2026, 3, 1), nights: 3, touristTax: 0m);
        SeedStay(db, withoutMunicipality, Day(2026, 9, 9), nights: 2, touristTax: 0m);
        SeedStay(db, notInRegistry, Day(2026, 10, 1), nights: 4, touristTax: 0m);
        SeedStay(db, zurich, Day(2026, 3, 1), nights: 5, touristTax: 0m);
        SeedStay(db, otherOrgHouse, Day(2026, 4, 1), nights: 1, touristTax: 0m);
        SeedDocument(db, orgId, lugano, taxed, FiscalDocumentKind.Invoice, "CHF", tax: 12.5m);
        await db.SaveChangesAsync();

        var controller = CreateController(db, new HostScope(orgId), holdsGuestRead: false);
        var answer = Assert.IsType<OkObjectResult>(await controller.GetYearEndTicinoCount(2026, "json", CancellationToken.None));
        var json = JsonSerializer.Serialize(Assert.IsType<TicinoYearlyCountDto>(answer.Value), Json);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(2026, root.GetProperty("year").GetInt32());
        Assert.Equal("2027-01-05", root.GetProperty("dueDate").GetString());
        Assert.Equal(2, root.GetProperty("cantonUnknownCount").GetInt32());

        // The properties by name: the one in Ticino and the two whose canton is not known, flagged; the house of Zürich and the one of another org are not in the count.
        var properties = root.GetProperty("properties").EnumerateArray().ToList();
        Assert.Equal(["Casa Lugano", "Villa Comune Ignoto", "Villa Senza Comune"], properties.Select(p => p.GetProperty("propertyName").GetString()));
        Assert.Equal([false, true, true], properties.Select(p => p.GetProperty("cantonUnknown").GetBoolean()));
        Assert.Equal(root.GetProperty("cantonUnknownCount").GetInt32(), properties.Count(p => p.GetProperty("cantonUnknown").GetBoolean()));
        Assert.Equal((1, 3, 1, 3, 0, 0), (Int(properties[0], "stays"), Int(properties[0], "nights"), Int(properties[0], "taxedStays"), Int(properties[0], "taxedNights"), Int(properties[0], "staysWithoutDocument"), Int(properties[0], "nightsWithoutDocument")));
        Assert.Equal((1, 4, 1, 4), (Int(properties[1], "stays"), Int(properties[1], "nights"), Int(properties[1], "staysWithoutDocument"), Int(properties[1], "nightsWithoutDocument")));
        Assert.Equal((1, 2, 1, 2), (Int(properties[2], "stays"), Int(properties[2], "nights"), Int(properties[2], "staysWithoutDocument"), Int(properties[2], "nightsWithoutDocument")));

        // The amount is a number in the currency the record carries, not a string.
        Assert.Equal(JsonValueKind.Number, properties[0].GetProperty("touristTax").ValueKind);
        Assert.Equal(12.5m, properties[0].GetProperty("touristTax").GetDecimal());
        Assert.Equal("CHF", properties[0].GetProperty("currency").GetString());

        // An org whose properties are all known says 0, and has none of the first org's.
        var other = CreateController(db, new HostScope(otherOrgId), holdsGuestRead: false);
        var otherAnswer = Assert.IsType<OkObjectResult>(await other.GetYearEndTicinoCount(2026, "json", CancellationToken.None));
        var otherCount = Assert.IsType<TicinoYearlyCountDto>(otherAnswer.Value);
        Assert.Equal(0, otherCount.CantonUnknownCount);
        Assert.Equal(["Casa Locarno"], otherCount.Properties.Select(p => p.PropertyName));
    }

    /// <summary>
    /// The format has to be named, as it always was, and the files are the files: none, a blank one or another word is the 400 <c>report_invalid_format</c> of the files; <c>json</c> in any case is the data; a year
    /// the reports do not support and an account cut by anything but month or quarter are the same 400 as for the files; and <c>csv</c> is the very file the exporter makes of the same data.
    /// </summary>
    [Fact]
    public async Task YearEndJson_IsAskedByName_AndTheFilesAreStillTheFiles_Test()
    {
        var orgId = Guid.NewGuid();
        await using var db = CreateDb();
        SeedOrg(db, orgId);
        var loft = SeedProperty(db, orgId, "Loft Milano", "Milano");
        SeedStay(db, loft, Day(2026, 3, 2), nights: 3, touristTax: 57.35m);
        await db.SaveChangesAsync();
        var controller = CreateController(db, new HostScope(orgId), holdsGuestRead: false);

        foreach (var format in new[] { null, string.Empty, " ", "pdf", "json2" })
        {
            foreach (var read in Reads(controller, 2026, format))
                AssertProblem(await read(), StatusCodes.Status400BadRequest, ReportRequestChecks.InvalidFormatCode);
        }

        foreach (var format in new[] { "json", "csv" })
        {
            AssertProblem(await controller.GetYearEndDeclaration(1999, format, CancellationToken.None), StatusCodes.Status400BadRequest, "fiscal_report_period_invalid");
            AssertProblem(await controller.GetYearEndAgentAccount(2026, format, "week", CancellationToken.None), StatusCodes.Status400BadRequest, TouristTaxSummaryController.AccountInvalidPeriodCode);
        }

        Assert.IsType<OkObjectResult>(await controller.GetYearEndDeclaration(2026, "JSON", CancellationToken.None));

        var data = await CreateService(db).GetDeclarationAsync(new HostScope(orgId), 2026, withExemptionReasons: false);
        var file = Assert.IsType<FileContentResult>(await controller.GetYearEndDeclaration(2026, "csv", CancellationToken.None));
        Assert.Equal("text/csv", file.ContentType);
        Assert.Equal(TouristTaxYearEndExports.DeclarationFileName(2026, "csv"), file.FileDownloadName);
        Assert.Equal(TouristTaxYearEndExports.DeclarationCsv(data, new ResxDocumentTexts(), DocumentLanguages.Current), file.FileContents);
    }

    private static async Task<string> DeclarationJsonAsync(AppDbContext db, Guid orgId, bool holdsGuestRead)
    {
        var controller = CreateController(db, new HostScope(orgId), holdsGuestRead);
        var answer = Assert.IsType<OkObjectResult>(await controller.GetYearEndDeclaration(2026, "json", CancellationToken.None));

        // Money and, for a caller that holds guest.read, the reasons of the exemptions: never kept by a cache, like the files.
        Assert.Equal("no-store", controller.Response.Headers.CacheControl.ToString());
        return JsonSerializer.Serialize(Assert.IsType<TouristTaxDeclarationDto>(answer.Value), Json);
    }

    /// <summary>The exemptions of the only comune of a declaration, as the JSON says them.</summary>
    private static List<(string Reason, int Stays, int Guests, int GuestNights)> ExemptionsOf(string json)
    {
        using var document = JsonDocument.Parse(json);
        var comune = Assert.Single(document.RootElement.GetProperty("comuni").EnumerateArray());
        return [.. comune.GetProperty("exemptions").EnumerateArray().Select(r => (r.GetProperty("reason").GetString()!, Int(r, "stays"), Int(r, "guests"), Int(r, "guestNights")))];
    }

    private static int Int(JsonElement element, string name) => element.GetProperty(name).GetInt32();

    private static void AssertProblem(IActionResult result, int status, string code)
    {
        var problem = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Equal(status, problem.StatusCode);
        Assert.Equal(code, Assert.IsType<ProblemDetails>(problem.Value).Extensions[ApiProblemDetails.CodeExtension]);
    }

    /// <summary>The three reads of the year-end in a format, for a controller.</summary>
    private static Func<Task<IActionResult>>[] Reads(TouristTaxSummaryController controller, int year, string? format)
    {
        return
        [
            () => controller.GetYearEndDeclaration(year, format, CancellationToken.None),
            () => controller.GetYearEndAgentAccount(year, format, null, CancellationToken.None),
            () => controller.GetYearEndTicinoCount(year, format, CancellationToken.None),
        ];
    }

    private static TouristTaxSummaryController CreateController(AppDbContext db, HostScope scope, bool holdsGuestRead)
    {
        var authorization = new Mock<IAuthorizationService>();
        authorization
            .Setup(a => a.AuthorizeAsync(It.IsAny<ClaimsPrincipal>(), It.IsAny<object?>(), CasazenPolicies.GuestRead))
            .ReturnsAsync(holdsGuestRead ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        var org = new Mock<IOrgContextResolver>();
        org.Setup(o => o.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(scope.OrgId);
        var scopes = new Mock<IHostScopeResolver>();
        scopes.Setup(s => s.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), scope.OrgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);

        return new TouristTaxSummaryController(CreateService(db), org.Object, scopes.Object, Mock.Of<IStringLocalizer<SharedResources>>(), new ResxDocumentTexts(), authorization.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "auth0|commercialista")], "Test")),
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

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

    private static Property SeedProperty(AppDbContext db, Guid orgId, string name, string city, string country = "IT", string? municipality = null)
    {
        var property = new Property
        {
            OrgId = orgId,
            OwnerId = "auth0|host",
            Name = name,
            Address = "Via Test 1",
            City = city,
            PostalCode = "00100",
            Country = country,
            Currency = country == "CH" ? "CHF" : "EUR",
            SwissMunicipalityCode = municipality,
            Bedrooms = 1,
            Bathrooms = 1,
            MaxGuests = 4,
            NightlyRate = 100m,
            IsActive = true,
        };
        db.Properties.Add(property);
        return property;
    }

    private static Booking SeedStay(AppDbContext db, Property property, DateTime checkIn, int nights, decimal touristTax)
    {
        var booking = new Booking
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
        };
        db.Bookings.Add(booking);
        return booking;
    }

    /// <summary>What a stay stores of its tourist tax once the host confirmed it: the guests with the kind of their exemption and its reason.</summary>
    private static void SeedRecord(AppDbContext db, Guid orgId, Booking booking, params (TouristTaxExemptionKind Kind, string? Code)[] guests)
    {
        var nights = booking.CheckOutDate.Date.Subtract(booking.CheckInDate.Date).Days;
        db.StayTouristTaxes.Add(new StayTouristTax
        {
            OrgId = orgId,
            BookingId = booking.Id,
            Status = TouristTaxQuoteStatus.Calculated,
            Nights = nights,
            TaxableNights = nights,
            Total = booking.TouristTaxAmount,
            AmountDue = booking.TouristTaxAmount,
            Collector = TouristTaxCollector.Host,
            ConfirmedAt = Now.UtcDateTime,
            RetainUntil = StayTouristTax.RetentionFor(DateOnly.FromDateTime(booking.CheckOutDate)),
            Guests = [.. guests.Select((g, index) => new StayTouristTaxGuest { Position = index + 1, ExemptionKind = g.Kind, ExemptionCode = g.Code, TaxableNights = g.Kind == TouristTaxExemptionKind.None ? nights : 0 })],
        });
    }

    /// <summary>An issued document of a stay (a guest document) with a tourist tax line of <paramref name="tax"/> when it is above zero, and a line of the stay.</summary>
    private static void SeedDocument(AppDbContext db, Guid orgId, Property property, Booking booking, FiscalDocumentKind kind, string currency, decimal tax)
    {
        var lines = new List<FiscalDocumentLine> { new() { OrgId = orgId, LineNumber = 1, LineType = FiscalLineType.Accommodation, Description = "TEST soggiorno", Quantity = 1m, UnitPrice = 100m, TotalPrice = 100m } };
        if (tax > 0m)
            lines.Add(new FiscalDocumentLine { OrgId = orgId, LineNumber = 2, LineType = FiscalLineType.TouristTax, Description = "TEST tassa di soggiorno", Quantity = 1m, UnitPrice = tax, TotalPrice = tax });

        db.FiscalDocuments.Add(new FiscalDocument
        {
            OrgId = orgId,
            BookingId = booking.Id,
            PropertyId = property.Id,
            Purpose = FiscalDocumentPurpose.GuestStay,
            Kind = kind,
            Status = FiscalDocumentStatus.Issued,
            Currency = currency,
            Series = "TEST",
            Number = "TEST/" + Guid.NewGuid().ToString("N")[..8],
            IssueDate = new DateOnly(2027, 1, 7),
            Lines = lines,
        });
    }
}
