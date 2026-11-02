using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Supplier;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-13a: what the published content adds to the public page of a supplier. The page carries the published text and a few checked
/// facts, never a contact, an id, a status or the draft; a supplier that published nothing has none of the new members; and a showcase the
/// supplier paused offers no slot and no estimate.
/// </summary>
public class PublicShowcaseContentTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // The words no public member of the content may carry as a field: contact data, ids, the bookkeeping, the status, the draft.
    private static readonly string[] ForbiddenFieldWords =
    [
        "phone", "email", "mail", "whatsapp", "vatnumber", "partitaiva", "address", "indirizzo", "iban", "orgid", "version", "createdat",
        "updatedat", "deletedat", "token", "password", "status", "legalname", "draft", "serviceorder", "domain",
    ];

    private static ShowcaseContentDocument Document() => new()
    {
        Style = "acqua",
        Color = "#0B5FA5",
        Tagline = "Pronti in un'ora",
        Bio = "Idraulici a Roma dal 2010.",
        Team = [new ShowcaseTeamMember("Ada Rossi", "Titolare")],
        Faq = [new ShowcaseFaq("Quanto costa?", "Dipende dal lavoro.")],
        ServiceOrder = ["pulizia", "bucato"],
    };

    private static PublishedShowcasePage Page(int? jobsDone = null) =>
        new(Document(), Paused: false, BookingsOpen: true, Verified: true, JobsDone: jobsDone, RespondWithinMinutes: 180, AcceptsOnlinePayments: true, PrivacyNoticeVersion: "2026-10");

    [Fact]
    public void ThePage_WithPublishedContent_CarriesTheContentAndTheCheckedFacts_NeverAContactOrAnId()
    {
        var dto = new SupplierShowcaseDto { Slug = "fornitore", LegalName = "Fornitore Srl" };

        PublicShowcaseMapper.Apply(dto, Page(jobsDone: 12));
        var json = JsonSerializer.SerializeToElement(dto, Web);

        Assert.Equal("Pronti in un'ora", json.GetProperty("content").GetProperty("tagline").GetString());
        Assert.False(json.GetProperty("paused").GetBoolean());
        Assert.True(json.GetProperty("bookingsOpen").GetBoolean());
        Assert.True(json.GetProperty("trust").GetProperty("verified").GetBoolean());
        Assert.Equal(12, json.GetProperty("trust").GetProperty("jobsDone").GetInt32());
        Assert.Equal(180, json.GetProperty("respondWithinMinutes").GetInt32());
        Assert.Equal("2026-10", json.GetProperty("privacyNoticeVersion").GetString());

        // The order of the services orders `services`; it is not a member of the content.
        AssertNoForbiddenField(json.GetProperty("content"), "content");
        AssertNoForbiddenField(json.GetProperty("trust"), "trust");
    }

    [Fact]
    public void ThePage_WithoutPublishedContent_HasNoneOfTheNewMembers()
    {
        var json = JsonSerializer.SerializeToElement(new SupplierShowcaseDto { Slug = "fornitore", LegalName = "Fornitore Srl" }, Web);
        var names = json.EnumerateObject().Select(p => p.Name).ToList();

        foreach (var added in new[] { "content", "paused", "bookingsOpen", "trust", "respondWithinMinutes", "acceptsOnlinePayments", "privacyNoticeVersion" })
            Assert.DoesNotContain(added, names);
    }

    [Fact]
    public void TheJobsDone_AreLeftOutUntilThereAreEnoughToMeanSomething()
    {
        var dto = new SupplierShowcaseDto();

        PublicShowcaseMapper.Apply(dto, Page(jobsDone: null));

        Assert.DoesNotContain("jobsDone", JsonSerializer.Serialize(dto.Trust, Web));
    }

    [Fact]
    public void TheOrderOfTheServices_ShowsOnlyThoseOfTheOrder_InThatOrder_AnEmptyOrderShowsAll()
    {
        IReadOnlyList<SupplierPublicService> services =
        [
            Service("bucato"), Service("pulizia"), Service("giardino"),
        ];

        Assert.Equal(new[] { "pulizia", "bucato" }, ShowcaseContentRules.ApplyServiceOrder(services, ["pulizia", "bucato", "vecchio"]).Select(s => s.Slug));
        Assert.Equal(new[] { "bucato", "pulizia", "giardino" }, ShowcaseContentRules.ApplyServiceOrder(services, []).Select(s => s.Slug));
    }

    // ─── Paused ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APausedShowcase_OffersNoSlotAndNoEstimate_AnotherDoes()
    {
        var supplier = new SupplierProfile { OrgId = Guid.NewGuid(), Status = SupplierStatus.Active, ShowcaseSlug = "fornitore" };
        var showcase = new Mock<IPublicSupplierShowcaseService>();
        showcase.Setup(s => s.FindActiveSupplierAsync("fornitore", It.IsAny<CancellationToken>())).ReturnsAsync(supplier);
        showcase.Setup(s => s.IsPausedAsync(supplier, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var controller = Controller(showcase.Object);

        var slots = await Assert.ThrowsAsync<DomainRuleException>(
            () => controller.GetSlots("fornitore", "pulizia", null, null, null, CancellationToken.None));
        var quote = await Assert.ThrowsAsync<DomainRuleException>(
            () => controller.Quote("fornitore", new PublicQuoteRequest { Service = "pulizia" }, CancellationToken.None));

        Assert.Equal(ShowcaseBookingErrors.Offline, slots.Code);
        Assert.Equal(ShowcaseBookingErrors.Offline, quote.Code);

        // Not paused: the slots come as usual.
        showcase.Setup(s => s.IsPausedAsync(supplier, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        showcase
            .Setup(s => s.GetSlotsAsync(supplier, "pulizia", It.IsAny<DateOnly?>(), It.IsAny<int?>(), false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PublicSlots("pulizia", 60, new DateOnly(2026, 11, 20), []));
        var result = await controller.GetSlots("fornitore", "pulizia", null, null, null, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result.Result);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────────

    private static PublicSupplierController Controller(IPublicSupplierShowcaseService showcase) =>
        new(showcase, Mock.Of<IComuneDirectory>(), Mock.Of<ISupplierService>(), Mock.Of<IFeatureFlags>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    private static SupplierPublicService Service(string slug) =>
        new(slug, slug, "cleaning", null, null, 4500, SupplierServicePriceUnit.PerJob, false, false, 60, [], [], [], [], null, 127, false, null);

    private static void AssertNoForbiddenField(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Assert.DoesNotContain(property.Name.ToLowerInvariant(), ForbiddenFieldWords);
                    AssertNoForbiddenField(property.Value, $"{path}.{property.Name}");
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    AssertNoForbiddenField(item, $"{path}[]");
                break;
        }
    }
}
