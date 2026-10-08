using System.Text.Json;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.DTOs.Supplier;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-09: the public answers (<see cref="PublicSupplierMapper"/> and the DTOs): composed only of what the supplier published,
/// with no field for a person, the supplier's private calendar or the console's bookkeeping; the slot times in UTC and on the
/// clock of Rome (the offset tells apart the two passes of the hour that happens twice); the estimate with its lines.
/// </summary>
public class PublicSupplierDtosTests
{
    // The words no public answer may carry as a field: contact data of the supplier or of a customer, the private calendar,
    // the bookkeeping of the console and the supplier's status.
    private static readonly string[] ForbiddenFieldWords =
    [
        "phone", "email", "mail", "vatnumber", "partitaiva", "address", "indirizzo", "fiscal", "iban",
        "customer", "client", "guest", "host", "notes", "note", "source", "uid", "closure", "block",
        "orgid", "version", "sortorder", "createdat", "updatedat", "deletedat", "token", "password", "status",
        "legalname",
    ];

    [Fact]
    public void TheServiceCard_HasTheContentOfTheCard_AndNothingOfTheDetail()
    {
        var card = PublicSupplierMapper.ToSummaryDto(Service());

        Assert.Equal("pulizia-profonda", card.Slug);
        Assert.Equal("Pulizia profonda", card.Name);
        Assert.Equal(ServiceCategories.Cleaning, card.Category);
        Assert.Equal("Stanza per stanza.", card.Summary);
        Assert.Equal(4500, card.PriceFromCents);
        Assert.Equal(SupplierServicePriceUnit.PerHour, card.PriceUnit);
        Assert.True(card.PricesIncludeVat);
        Assert.False(card.RequiresQuote);
        Assert.Equal(180, card.DurationMinutes);
        Assert.Equal(new[] { "Bagni" }, card.Included);
        Assert.Equal(new[] { "Vetri" }, card.Excluded);
        Assert.Equal(new[] { "https://storage.test/a.jpg" }, card.PhotoUrls);
        Assert.IsNotAssignableFrom<PublicSupplierServiceDetailDto>(card);
        Assert.DoesNotContain("description", JsonSerializer.Serialize(card, Web), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("supplements", JsonSerializer.Serialize(card, Web), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheServiceDetail_AddsTheDescriptionAndTheStructuredSupplements_ThePlanningTermsStayOut()
    {
        var detail = PublicSupplierMapper.ToDetailDto(Service());

        Assert.Equal("Descrizione lunga.", detail.Description);
        Assert.Equal(3, detail.Supplements.Count);
        Assert.Equal(("bagno", "Bagno in più", 1000, "bathroom", (int?)3), (detail.Supplements[0].Code, detail.Supplements[0].Label, detail.Supplements[0].AmountCents, detail.Supplements[0].Per, detail.Supplements[0].Max));
        var json = JsonSerializer.Serialize(detail, Web);
        Assert.DoesNotContain("minNoticeHours", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("weekdays", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASqm30Supplement_SaysFromWhichSurfaceItCounts_TheOthersSayNothing()
    {
        var detail = PublicSupplierMapper.ToDetailDto(Service());

        Assert.Equal(new int?[] { null, null, 60 }, detail.Supplements.Select(s => s.IncludedSqm));
        var json = JsonSerializer.Serialize(detail, Web);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "includedSqm"));
    }

    [Fact]
    public void TheJsonOfEveryPublicAnswer_HasNoForbiddenFieldName()
    {
        var service = Service();
        var slots = new PublicSlots(
            "pulizia-profonda",
            120,
            new DateOnly(2026, 11, 12),
            [new PublicSlotDay(new DateOnly(2026, 10, 20), [new SupplierSlot(Utc(2026, 10, 20, 7), Utc(2026, 10, 20, 9))])]);
        var quote = new PublicQuote(
            service,
            PublicQuoteCoverage.Covered,
            SupplierQuoteCalculator.Calculate(service, SupplierQuoteCalculator.Validate(service, new SupplierQuoteRequest(3, 100, [new SupplierQuoteOption("bagno", 2)], "H501", "00184")), true),
            "00184");
        var showcase = new SupplierShowcaseDto
        {
            Slug = "fornitore",
            Services = [PublicSupplierMapper.ToSummaryDto(service)],
            MedianResponseMinutes = 25,
        };

        var answers = new object[]
        {
            PublicSupplierMapper.ToSummaryDto(service),
            PublicSupplierMapper.ToDetailDto(service),
            new PublicSupplierServiceListResponse { Items = [PublicSupplierMapper.ToSummaryDto(service)], Total = 1 },
            PublicSupplierMapper.ToDto(slots),
            PublicSupplierMapper.ToDto(quote),
        };

        foreach (var answer in answers)
            AssertNoForbiddenField(JsonSerializer.SerializeToElement(answer, Web), answer.GetType().Name);

        // The page itself: the new members only (the legal name of the supplier is the page's title, not an addition).
        var added = JsonSerializer.SerializeToElement(showcase, Web);
        Assert.Equal(25, added.GetProperty("medianResponseMinutes").GetInt32());
        AssertNoForbiddenField(added.GetProperty("services"), "SupplierShowcaseDto.services");
    }

    [Fact]
    public void TheShowcase_WithTheNewMembersUnset_IsExactlyTheShowcaseOfBeforeSp09()
    {
        var showcase = new SupplierShowcaseDto { Slug = "fornitore", LegalName = "Fornitore Srl" };

        var names = JsonSerializer.SerializeToElement(showcase, Web).EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(new[] { "availability", "bio", "categories", "comuni", "legalName", "photoUrls", "slug" }, names);
    }

    [Fact]
    public void TheSlotTimes_AreUtcAndTheClockOfRomeWithItsOffset()
    {
        var slots = new PublicSlots(
            "s",
            120,
            new DateOnly(2026, 11, 12),
            [
                new PublicSlotDay(new DateOnly(2026, 10, 20), [new SupplierSlot(Utc(2026, 10, 20, 7), Utc(2026, 10, 20, 9))]), // summer time
                new PublicSlotDay(new DateOnly(2026, 11, 3), [new SupplierSlot(Utc(2026, 11, 3, 8), Utc(2026, 11, 3, 10))]), // winter time
                new PublicSlotDay(new DateOnly(2026, 10, 21), []),
            ]);

        var dto = PublicSupplierMapper.ToDto(slots);

        Assert.Equal("Europe/Rome", dto.TimeZone);
        var summer = dto.Days[0].Slots[0];
        Assert.Equal(Utc(2026, 10, 20, 7), summer.StartUtc);
        Assert.Equal(DateTimeKind.Utc, summer.StartUtc.Kind);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(2)), summer.StartLocal);
        Assert.Equal(TimeSpan.FromHours(2), summer.StartLocal.Offset);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 11, 0, 0, TimeSpan.FromHours(2)), summer.EndLocal);
        var winter = dto.Days[1].Slots[0];
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.FromHours(1)), winter.StartLocal);
        Assert.False(dto.Days[2].Available);
        Assert.Empty(dto.Days[2].Slots);
        Assert.Equal(new DateOnly(2026, 11, 12), dto.BookableUntil);
    }

    [Fact]
    public void TheHourThatHappensTwice_HasTwoSlotsWithTheSameClockTime_ToldApartByTheirOffsets()
    {
        // 25 October 2026, 02:30 Rome happens at 00:30 UTC (summer time) and again at 01:30 UTC (winter time).
        var slots = new PublicSlots(
            "s",
            60,
            new DateOnly(2026, 11, 12),
            [new PublicSlotDay(new DateOnly(2026, 10, 25), [new SupplierSlot(Utc(2026, 10, 25, 0, 30), Utc(2026, 10, 25, 1, 30)), new SupplierSlot(Utc(2026, 10, 25, 1, 30), Utc(2026, 10, 25, 2, 30))])]);

        var dto = PublicSupplierMapper.ToDto(slots);

        var first = dto.Days[0].Slots[0].StartLocal;
        var second = dto.Days[0].Slots[1].StartLocal;
        Assert.Equal(first.TimeOfDay, second.TimeOfDay); // both read 02:30 on the wall clock...
        Assert.Equal(TimeSpan.FromHours(2), first.Offset); // ...in summer time...
        Assert.Equal(TimeSpan.FromHours(1), second.Offset); // ...and in winter time
        Assert.NotEqual(first.UtcDateTime, second.UtcDateTime);
    }

    [Fact]
    public void TheQuoteAnswer_CarriesTheEstimateTheLinesAndTheCoverage()
    {
        var service = Service();
        var request = new SupplierQuoteRequest(3, 100, [new SupplierQuoteOption("bagno", 2)], "H501", "00184");
        var choices = SupplierQuoteCalculator.Validate(service, request);

        var dto = PublicSupplierMapper.ToDto(new PublicQuote(
            service, PublicQuoteCoverage.Covered, SupplierQuoteCalculator.Calculate(service, choices, true), choices.PostalCode));

        Assert.Equal("pulizia-profonda", dto.Service);
        Assert.Equal("Pulizia profonda", dto.ServiceName);
        Assert.Equal("EUR", dto.Currency);
        Assert.True(dto.PricesIncludeVat);
        Assert.Equal(SupplierQuoteOutcome.Estimate, dto.Outcome);
        Assert.Null(dto.Reason);
        Assert.True(dto.IsEstimate);
        Assert.False(dto.RequiresQuote);
        Assert.Equal(PublicQuoteCoverage.Covered, dto.Coverage);
        Assert.Equal("00184", dto.PostalCode);
        // 3 hours at 45 euro, 2 extra bathrooms at 10, 100 m² = 2 blocks of 30 above 60 at 5.
        Assert.Equal(new[] { SupplierQuoteLineKind.Base, SupplierQuoteLineKind.Supplement, SupplierQuoteLineKind.Supplement }, dto.Lines.Select(l => l.Kind));
        Assert.Equal(new[] { 13_500, 2_000, 1_000 }, dto.Lines.Select(l => l.AmountCents));
        Assert.Equal(16_500, dto.TotalCents);
    }

    [Fact]
    public void TheQuoteRequest_IsMappedToTheInputOfTheEstimate()
    {
        var input = PublicSupplierMapper.ToInput(new PublicQuoteRequest
        {
            Service = "s",
            Quantity = 2,
            SurfaceSqm = 80,
            Options = [new PublicQuoteOptionRequest { Code = "bagno", Quantity = 2 }, null],
            Comune = "H501",
            PostalCode = "00184",
        });

        Assert.Equal(2, input.Quantity);
        Assert.Equal(80, input.SurfaceSqm);
        Assert.Equal(new SupplierQuoteOption("bagno", 2), input.Options![0]);
        Assert.Null(input.Options[1]);
        Assert.Equal("H501", input.Comune);
        Assert.Equal("00184", input.PostalCode);
    }

    [Fact]
    public void TheQuoteWithNoTotal_HasNoTotalNoLinesAndTheReason()
    {
        var service = Service() with { RequiresQuote = true };
        var choices = SupplierQuoteCalculator.Validate(service, new SupplierQuoteRequest(2, null, null, null, null));

        var dto = PublicSupplierMapper.ToDto(new PublicQuote(
            service, PublicQuoteCoverage.Unknown, SupplierQuoteCalculator.Calculate(service, choices, null), null));

        Assert.Equal(SupplierQuoteOutcome.OnQuote, dto.Outcome);
        Assert.Equal(SupplierQuoteReason.RequiresQuote, dto.Reason);
        Assert.False(dto.IsEstimate);
        Assert.True(dto.RequiresQuote);
        Assert.Null(dto.TotalCents);
        Assert.Empty(dto.Lines);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static DateTime Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static void AssertNoForbiddenField(JsonElement element, string where)
    {
        foreach (var name in FieldNames(element))
        {
            foreach (var word in ForbiddenFieldWords)
                Assert.False(name.Contains(word, StringComparison.OrdinalIgnoreCase), $"{where}: the field '{name}' contains '{word}'");
        }
    }

    private static IEnumerable<string> FieldNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var inner in FieldNames(property.Value))
                        yield return inner;
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var inner in FieldNames(item))
                        yield return inner;
                }

                break;
        }
    }

    private static SupplierPublicService Service() =>
        new(
            "pulizia-profonda",
            "Pulizia profonda",
            ServiceCategories.Cleaning,
            "Stanza per stanza.",
            "Descrizione lunga.",
            4500,
            SupplierServicePriceUnit.PerHour,
            true,
            false,
            180,
            [
                new SupplierServiceSupplement("bagno", "Bagno in più", 1000, "bathroom", 3),
                new SupplierServiceSupplement("ferro", "Ferro", 300, "flat", null),
                new SupplierServiceSupplement("mq", "Oltre 60 m²", 500, "sqm30", null),
            ],
            ["Bagni"],
            ["Vetri"],
            ["https://storage.test/a.jpg"],
            12,
            SupplierServiceWeekdays.AllMask);
}
