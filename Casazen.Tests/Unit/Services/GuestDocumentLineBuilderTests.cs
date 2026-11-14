using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E12/F01/US01/T01 (NF-83, NF-85, NF-112, NF-113, D6, ⚖ C4, C5): the lines of the document of every booking. The host price of a portal and never the service fee of the portal, the
/// cleaning never twice, the tourist tax only when the host collects it (Italy: the channel rules of the comune; Switzerland: Ticino CHF 2.00 per person and night out of VAT,
/// never for a channel that collects it), who collected the money and the state of the payment, and an issued document that never follows a change of its booking.
/// </summary>
public class GuestDocumentLineBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid OrgId = Guid.NewGuid();
    private const string IstatRoma = "058091";
    private const string MunicipalityLugano = "5192";

    /// <summary>
    /// PROOF TEST: money rule (⚖ C4, D6). Ticino, from the shipped <c>SwissTouristTax:Rules</c>: 2 guests × 3 nights × CHF 2.00 = CHF 12.00, a line of its own of type tourist tax,
    /// out of VAT, when the booking carries no tax of its own; the document is the stay and the cleaning at the Swiss rates plus the 12.00 untaxed. The canton comes from
    /// <c>SwissMunicipalities</c> through the municipality of the property; a canton without a rule, or a night before the rule, adds no line.
    /// </summary>
    [Fact]
    public async Task SwissTouristTax_TicinoRatePerPersonNight_Test()
    {
        await using var db = NewDb();
        await SeedSwissOrgAsync(db, []);
        var property = await AddPropertyAsync(db, swiss: true);
        var booking = await AddBookingAsync(db, property, BookingSource.Direct, basePrice: 450m, cleaningFee: 50m, touristTax: 0m, guests: 2);
        var rule = Rule("CH", DocumentRegime.SwissVatRegistered);

        var built = await BuilderOf(db, ShippedSwissTouristTax()).BuildAsync(booking, rule, []);

        var tax = Assert.Single(built.Lines, l => l.LineType == FiscalLineType.TouristTax);
        Assert.Equal(12.00m, tax.GrossAmount);
        Assert.Equal(0m, tax.VatRatePercent);
        Assert.Null(tax.NatureCode);
        Assert.Equal(462.00m, built.Lines.Sum(l => l.GrossAmount));

        // The document the service makes from them: the 12.00 is outside the VAT, the rest is taxed.
        var service = ServiceOf(db, ShippedSwissTouristTax());
        var draft = await service.CreateDraftAsync(booking, "auth0|host");
        var line = Assert.Single(draft.Lines, l => l.LineType == FiscalLineType.TouristTax);
        Assert.Equal((12.00m, 0m, 0m), (line.TotalPrice, line.VatRatePercent, line.VatAmount ?? 0m));
        Assert.Equal(462.00m, draft.Total);
        Assert.Equal("CHF", draft.Currency);

        // The amount of the booking wins when it has one; no canton rule, no municipality, no guests: no line of its own.
        var own = await AddBookingAsync(db, property, BookingSource.Direct, basePrice: 450m, cleaningFee: 50m, touristTax: 5m, guests: 2);
        Assert.Equal(5m, Assert.Single((await BuilderOf(db, ShippedSwissTouristTax()).BuildAsync(own, rule, [])).Lines, l => l.LineType == FiscalLineType.TouristTax).GrossAmount);
        Assert.DoesNotContain(
            (await BuilderOf(db, new SwissTouristTaxOptions()).BuildAsync(booking, rule, [])).Lines,
            l => l.LineType == FiscalLineType.TouristTax);
        var elsewhere = await AddPropertyAsync(db, swiss: true, municipality: "9999");
        var unknownCanton = await AddBookingAsync(db, elsewhere, BookingSource.Direct, basePrice: 450m, cleaningFee: 50m, touristTax: 0m, guests: 2);
        Assert.DoesNotContain(
            (await BuilderOf(db, ShippedSwissTouristTax()).BuildAsync(unknownCanton, rule, [])).Lines,
            l => l.LineType == FiscalLineType.TouristTax);

        // A night before the first day of the rule is not taxed: the stay of 2024-12-30 to 2025-01-02 pays the nights of 2025-01-01 only.
        var overNewYear = await AddBookingAsync(
            db, property, BookingSource.Direct, basePrice: 300m, cleaningFee: 0m, touristTax: 0m, guests: 2, checkIn: new DateTime(2024, 12, 30), checkOut: new DateTime(2025, 1, 2));
        var taxOverNewYear = Assert.Single((await BuilderOf(db, ShippedSwissTouristTax()).BuildAsync(overNewYear, rule, [])).Lines, l => l.LineType == FiscalLineType.TouristTax);
        Assert.Equal(4.00m, taxOverNewYear.GrossAmount);
    }

    /// <summary>
    /// PROOF TEST: money rule. A guest is never charged the tourist tax twice: Switzerland, no line for a channel the profile says collects it (Airbnb in Ticino, whatever the
    /// booking carries, and whether the rule or the booking gives the amount), a line for the channels that do not (Booking.com, direct); Italy, no line when the portal collects
    /// it (the active channel rule of the comune), a line when the host does (a rule that says so, a direct or a manual booking, the declaration of the host), the confirmed tax of
    /// the stay before the amount of the booking, and no line while nobody has said who collects it for a channel.
    /// </summary>
    [Fact]
    public async Task TouristTaxLine_NotAddedWhenPortalCollects_Test()
    {
        // Switzerland: the list of the profile decides.
        await using (var swiss = NewDb())
        {
            await SeedSwissOrgAsync(swiss, [BookingSource.Airbnb]);
            var property = await AddPropertyAsync(swiss, swiss: true);
            var rule = Rule("CH", DocumentRegime.SwissVatRegistered);
            var builder = BuilderOf(swiss, ShippedSwissTouristTax());
            var profile = await swiss.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync();

            var airbnbWithTax = await AddBookingAsync(swiss, property, BookingSource.Airbnb, 400m, 0m, touristTax: 12m, guests: 2);
            var airbnbByRule = await AddBookingAsync(swiss, property, BookingSource.Airbnb, 400m, 0m, touristTax: 0m, guests: 2);
            var bookingCom = await AddBookingAsync(swiss, property, BookingSource.BookingCom, 400m, 0m, touristTax: 0m, guests: 2);
            var direct = await AddBookingAsync(swiss, property, BookingSource.Direct, 400m, 0m, touristTax: 12m, guests: 2);

            Assert.DoesNotContain((await builder.BuildAsync(airbnbWithTax, rule, profile.PlatformCollectedTouristTaxChannels)).Lines, l => l.LineType == FiscalLineType.TouristTax);
            Assert.DoesNotContain((await builder.BuildAsync(airbnbByRule, rule, profile.PlatformCollectedTouristTaxChannels)).Lines, l => l.LineType == FiscalLineType.TouristTax);
            Assert.Equal(12m, TaxOf(await builder.BuildAsync(bookingCom, rule, profile.PlatformCollectedTouristTaxChannels)));
            Assert.Equal(12m, TaxOf(await builder.BuildAsync(direct, rule, profile.PlatformCollectedTouristTaxChannels)));
        }

        // Italy: the channel rule of the comune, then the host's declaration.
        await using var italy = NewDb();
        await SeedItalianOrgAsync(italy);
        var roma = await AddPropertyAsync(italy, swiss: false);
        var italianRule = Rule("IT", DocumentRegime.VatOrdinary);
        var italianBuilder = BuilderOf(italy, new SwissTouristTaxOptions());
        italy.TouristTaxChannelRules.Add(new TouristTaxChannelRule { Channel = BookingSource.Airbnb, IstatCode = IstatRoma, Collector = TouristTaxCollector.Channel, SourceUrl = "https://example.com/airbnb" });
        italy.TouristTaxChannelRules.Add(new TouristTaxChannelRule { Channel = BookingSource.Expedia, IstatCode = IstatRoma, Collector = TouristTaxCollector.Host, SourceUrl = "https://example.com/expedia" });
        await italy.SaveChangesAsync();

        var portalCollects = await AddBookingAsync(italy, roma, BookingSource.Airbnb, 300m, 50m, touristTax: 12m, guests: 2);
        var hostCollectsByRule = await AddBookingAsync(italy, roma, BookingSource.Expedia, 300m, 50m, touristTax: 12m, guests: 2);
        var directStay = await AddBookingAsync(italy, roma, BookingSource.Direct, 300m, 50m, touristTax: 12m, guests: 2);
        var manualStay = await AddBookingAsync(italy, roma, BookingSource.Manual, 300m, 50m, touristTax: 12m, guests: 2);
        var nobodySaid = await AddBookingAsync(italy, roma, BookingSource.BookingCom, 300m, 50m, touristTax: 12m, guests: 2);
        var declared = await AddBookingAsync(italy, roma, BookingSource.Vrbo, 300m, 50m, touristTax: 12m, guests: 2);
        var confirmed = await AddBookingAsync(italy, roma, BookingSource.Direct, 300m, 50m, touristTax: 12m, guests: 2);
        italy.StayTouristTaxes.Add(Record(declared, confirmedAt: null, total: 0m, declaredCollector: TouristTaxCollector.Host));
        italy.StayTouristTaxes.Add(Record(confirmed, confirmedAt: Now.UtcDateTime, total: 9m, declaredCollector: null));
        await italy.SaveChangesAsync();

        async Task<decimal?> Tax(Guid bookingId) => TaxOf(await italianBuilder.BuildAsync(bookingId, italianRule, []));

        Assert.Null(await Tax(portalCollects));
        Assert.Equal(12m, await Tax(hostCollectsByRule));
        Assert.Equal(12m, await Tax(directStay));
        Assert.Equal(12m, await Tax(manualStay));
        Assert.Null(await Tax(nobodySaid));
        Assert.Equal(12m, await Tax(declared));
        Assert.Equal(9m, await Tax(confirmed));

        // The line is out of VAT with the nature code of the rule (N1), whoever collects.
        var taxLine = (await italianBuilder.BuildAsync(directStay, italianRule, [])).Lines.Single(l => l.LineType == FiscalLineType.TouristTax);
        Assert.Equal(("N1", 0m), (taxLine.NatureCode, taxLine.VatRatePercent));
    }

    /// <summary>
    /// PROOF TEST: money rule. The document of a booking of a portal bills the price of the host (<c>BasePrice</c>, the room price of the channel) and never what the guest paid the
    /// portal on top (<c>TotalPrice</c>, the service fee); the cleaning is never counted twice, for the portal (no cleaning fee of its own: its price is the stay) and for a booking
    /// made in CasaZen (its price includes the cleaning, which is a line of its own and no more): the lines add up to <c>BasePrice</c> whatever the source.
    /// </summary>
    [Fact]
    public async Task PortalBooking_DocumentUsesHostPriceNotGuestFee_Test()
    {
        await using var db = NewDb();
        await SeedItalianOrgAsync(db);
        var property = await AddPropertyAsync(db, swiss: false);
        var rule = Rule("IT", DocumentRegime.VatOrdinary);
        var builder = BuilderOf(db, new SwissTouristTaxOptions());

        // Airbnb: the host price is 400.00, the guest paid 466.00 with the fee of the portal.
        var portal = await AddBookingAsync(db, property, BookingSource.Airbnb, basePrice: 400m, cleaningFee: 0m, touristTax: 0m, guests: 2, totalPrice: 466m);
        var fromPortal = await builder.BuildAsync(portal, rule, []);
        var stay = Assert.Single(fromPortal.Lines);
        Assert.Equal((FiscalLineType.Accommodation, 400m), (stay.LineType, stay.GrossAmount));

        // The document: 400.00, never 466.00.
        var draft = await ServiceOf(db, new SwissTouristTaxOptions()).CreateDraftAsync(portal, "auth0|host");
        Assert.Equal(400m, draft.Total);

        // A booking made in CasaZen: the price of 450.00 includes the cleaning of 50.00: 400.00 + 50.00, never 450.00 + 50.00.
        var direct = await AddBookingAsync(db, property, BookingSource.Direct, basePrice: 450m, cleaningFee: 50m, touristTax: 0m, guests: 2, totalPrice: 450m);
        var fromCasaZen = await builder.BuildAsync(direct, rule, []);
        Assert.Equal(
            [(FiscalLineType.Accommodation, 400m), (FiscalLineType.Cleaning, 50m)],
            fromCasaZen.Lines.Select(l => (l.LineType, l.GrossAmount)).ToArray());
        Assert.Equal(450m, fromCasaZen.Lines.Sum(l => l.GrossAmount));

        // Bad data never bills more than the price: a cleaning above the price is the whole price.
        Assert.Equal((0m, 100m), GuestDocumentLineBuilder.SplitOf(100m, 150m));
        Assert.Equal((100m, 0m), GuestDocumentLineBuilder.SplitOf(100m, 0m));
        Assert.Equal((0m, 0m), GuestDocumentLineBuilder.SplitOf(-5m, 20m));
    }

    /// <summary>
    /// Who collected the money, and the state of the payment: paid through the portal for a booking whose portal collects (the reservation says, else the portal catalog, else the
    /// configured default), outside CasaZen and «to collect» for a booking the host collects, CasaZen for a booking paid through it (Stripe) with the state read from the payments.
    /// </summary>
    [Fact]
    public async Task PaymentChannel_FollowsWhoCollects_AndTheStateFollowsTheChannel()
    {
        await using var db = NewDb();
        await SeedItalianOrgAsync(db);
        var property = await AddPropertyAsync(db, swiss: false);
        var options = new FiscalDocumentRulesOptions { PortalCollectedChannels = [BookingSource.Airbnb, BookingSource.Vrbo] };
        var service = ServiceOf(db, new SwissTouristTaxOptions(), options);
        var builder = BuilderOf(db, new SwissTouristTaxOptions(), options);
        var rule = Rule("IT", DocumentRegime.VatOrdinary);

        var byDefault = await AddBookingAsync(db, property, BookingSource.Airbnb, 400m, 0m, 0m, 2);
        var hostCollects = await AddBookingAsync(db, property, BookingSource.Airbnb, 400m, 0m, 0m, 2, companyCollect: false);
        var catalogSaysPortal = await AddBookingAsync(db, property, BookingSource.TripAdvisor, 400m, 0m, 0m, 2, commissionIncluded: true);
        var notListed = await AddBookingAsync(db, property, BookingSource.Agoda, 400m, 0m, 0m, 2);
        var agencySite = await AddBookingAsync(db, property, BookingSource.Manual, 400m, 0m, 0m, 2);
        var paidInCasaZen = await AddBookingAsync(db, property, BookingSource.Direct, 400m, 0m, 0m, 2);
        db.Payments.Add(new Payment
        {
            OrgId = OrgId,
            BookingId = paidInCasaZen,
            Amount = 400m,
            Status = PaymentStatus.Completed,
            Method = PaymentMethod.CreditCard,
            TransactionId = "pi_test_1",
            StripePaymentIntentId = "pi_test_1",
        });
        await db.SaveChangesAsync();

        async Task<FiscalPaymentChannel> Channel(Guid bookingId) => (await builder.BuildAsync(bookingId, rule, [])).PaymentChannel;

        Assert.Equal(FiscalPaymentChannel.Portal, await Channel(byDefault));
        Assert.Equal(FiscalPaymentChannel.Outside, await Channel(hostCollects));
        Assert.Equal(FiscalPaymentChannel.Portal, await Channel(catalogSaysPortal));
        Assert.Equal(FiscalPaymentChannel.Outside, await Channel(notListed));
        Assert.Equal(FiscalPaymentChannel.Outside, await Channel(agencySite));
        Assert.Equal(FiscalPaymentChannel.CasaZen, await Channel(paidInCasaZen));

        // The document: paid through the portal (stored, and kept when it is read), to collect for the host's own, the current state for CasaZen's.
        var portalDraft = await service.CreateDraftAsync(byDefault, null);
        Assert.Equal((FiscalPaymentChannel.Portal, FiscalDocumentPaymentState.Paid), (portalDraft.PaymentChannel, portalDraft.PaymentState));
        Assert.Equal(FiscalDocumentPaymentState.Paid, (await service.GetAsync(portalDraft.Id))!.PaymentState);

        var outsideDraft = await service.CreateDraftAsync(agencySite, null);
        Assert.Equal((FiscalPaymentChannel.Outside, FiscalDocumentPaymentState.ToCollect), (outsideDraft.PaymentChannel, outsideDraft.PaymentState));

        var casaZenDraft = await service.CreateDraftAsync(paidInCasaZen, null);
        Assert.Equal((FiscalPaymentChannel.CasaZen, FiscalDocumentPaymentState.Paid), (casaZenDraft.PaymentChannel, casaZenDraft.PaymentState));
    }

    /// <summary>
    /// PROOF TEST: data rule (T01 point 4). A booking that changes after its document was issued never rewrites it: the issued document is byte for byte what it was, and the host
    /// gets one «To do» about the document (<c>fiscal_document_booking_changed</c>, no personal data), raised once whatever the number of changes delivered. A draft follows the booking
    /// (made again from its new price), and the draft of a booking that is no longer active with nothing collected is discarded.
    /// </summary>
    [Fact]
    public async Task IssuedDocument_BookingChange_RaisesTodoNeverRewrites_Test()
    {
        await using var db = NewDb();
        await SeedItalianOrgAsync(db);
        var property = await AddPropertyAsync(db, swiss: false);
        var service = ServiceOf(db, new SwissTouristTaxOptions());
        var booking = await AddBookingAsync(db, property, BookingSource.Direct, basePrice: 350m, cleaningFee: 50m, touristTax: 0m, guests: 2);
        var issued = await service.IssueAsync((await service.CreateDraftAsync(booking, null)).Id, confirm: true);
        var before = Snapshot(await db.FiscalDocuments.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.Id == issued.Id));

        // The host changes the dates and the price after the issue.
        await ChangeBookingAsync(db, booking, b => { b.BasePrice = 500m; b.TotalPrice = 500m; b.CheckOutDate = b.CheckOutDate.AddDays(2); });
        var outcome = await service.ApplyBookingChangeAsync(booking, BookingChange.Modified);
        var again = await service.ApplyBookingChangeAsync(booking, BookingChange.Modified);

        Assert.Equal(before, Snapshot(await db.FiscalDocuments.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.Id == issued.Id)));
        Assert.Equal(1, outcome.TodosRaised);
        Assert.Equal(1, again.TodosRaised);
        Assert.Equal(0, outcome.DraftsRefreshed + outcome.DraftsDiscarded + outcome.CreditNotesPrepared + outcome.DocumentsIssued);
        var todo = Assert.Single(await db.InternalActivities.AsNoTracking().Where(a => a.Action != null).ToListAsync());
        Assert.Equal(nameof(ActivityType.FiscalDocument), todo.ActivityType);
        Assert.Equal(ActivityAction.ReviewFiscalDocument, todo.Action);
        Assert.Equal(FiscalDocumentTodos.EntityType, todo.EntityType);
        Assert.Equal(issued.Id, todo.EntityId);
        Assert.Equal(FiscalDocumentTodos.BookingChangedReason, todo.Reason);
        Assert.Equal(property, todo.PropertyId);
        Assert.Single(await db.FiscalDocuments.AsNoTracking().Where(d => d.BookingId == booking).ToListAsync());

        // A draft follows the booking: made again from the new price; the draft of a cancelled booking with nothing collected is discarded.
        var open = await AddBookingAsync(db, property, BookingSource.Manual, basePrice: 350m, cleaningFee: 50m, touristTax: 0m, guests: 2);
        var draft = await service.CreateDraftAsync(open, null);
        Assert.Equal(350m, draft.Total);
        await ChangeBookingAsync(db, open, b => b.BasePrice = 410m);
        var refreshed = await service.ApplyBookingChangeAsync(open, BookingChange.Modified);
        Assert.Equal(1, refreshed.DraftsRefreshed);
        Assert.Equal(410m, (await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == draft.Id)).Total);

        await ChangeBookingAsync(db, open, b => { b.Status = BookingStatus.Cancelled; b.CancelledAt = Now.UtcDateTime; });
        var discarded = await service.ApplyBookingChangeAsync(open, BookingChange.Cancelled);
        Assert.Equal(1, discarded.DraftsDiscarded);
        Assert.Equal(FiscalDocumentStatus.Discarded, (await db.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == draft.Id)).Status);
    }

    [Fact]
    public void ShippedDefaults_AreTicinoAndAirbnb_AndAreValid()
    {
        var shipped = ShippedSwissTouristTax();

        var rule = Assert.Single(shipped.Rules);
        Assert.Equal(("TI", 2.00m, new DateOnly(2025, 1, 1), null), (rule.Canton, rule.RatePerPersonPerNight, rule.ValidFrom, rule.ValidTo));
        Assert.Equal("https://www.ticino.ch/en/hospitality/opportunita/tasse/alloggio.html", rule.SourceUrl);
        Assert.Equal([BookingSource.Airbnb], shipped.PlatformCollected);
        Assert.Empty(SwissTouristTaxOptionsValidator.Problems(shipped));
        Assert.Equal(12.00m, shipped.TaxOf("ti", new DateOnly(2026, 11, 14), 3, 2));
        Assert.Null(shipped.TaxOf("ZH", new DateOnly(2026, 11, 14), 3, 2));
        Assert.Null(shipped.TaxOf("TI", new DateOnly(2024, 6, 1), 3, 2));
        Assert.Null(shipped.TaxOf("TI", new DateOnly(2026, 11, 14), 0, 2));
    }

    [Fact]
    public void SwissTouristTaxOptions_AreValidatedAtStartup()
    {
        var good = new SwissTouristTaxRuleOptions
        {
            Canton = "TI",
            RatePerPersonPerNight = 2m,
            ValidFrom = new DateOnly(2025, 1, 1),
            SourceUrl = "https://www.ticino.ch/x",
        };

        Assert.Empty(SwissTouristTaxOptionsValidator.Problems(new SwissTouristTaxOptions { Rules = [good] }));
        Assert.Empty(SwissTouristTaxOptionsValidator.Problems(new SwissTouristTaxOptions()));

        string[] Problems(Action<SwissTouristTaxRuleOptions> change)
        {
            var rule = new SwissTouristTaxRuleOptions { Canton = good.Canton, RatePerPersonPerNight = good.RatePerPersonPerNight, ValidFrom = good.ValidFrom, SourceUrl = good.SourceUrl };
            change(rule);
            return SwissTouristTaxOptionsValidator.Problems(new SwissTouristTaxOptions { Rules = [rule] }).ToArray();
        }

        Assert.Single(Problems(r => r.Canton = "TICINO"));
        Assert.Single(Problems(r => r.RatePerPersonPerNight = 0m));
        Assert.Single(Problems(r => r.RatePerPersonPerNight = 2.005m));
        Assert.Single(Problems(r => r.RatePerPersonPerNight = 80m));
        Assert.Single(Problems(r => r.SourceUrl = null));
        Assert.Single(Problems(r => r.SourceUrl = "http://not-https.example"));
        Assert.Single(Problems(r => r.ValidFrom = default));
        Assert.Single(Problems(r => r.ValidTo = new DateOnly(2024, 12, 31)));

        var overlap = SwissTouristTaxOptionsValidator.Problems(new SwissTouristTaxOptions { Rules = [good, new SwissTouristTaxRuleOptions { Canton = "ti", RatePerPersonPerNight = 3m, ValidFrom = new DateOnly(2026, 1, 1), SourceUrl = "https://x.example" }] });
        Assert.Contains(overlap, p => p.Contains("overlap", StringComparison.Ordinal));

        Assert.Single(SwissTouristTaxOptionsValidator.Problems(new SwissTouristTaxOptions { PlatformCollected = [BookingSource.Direct] }));
        Assert.Single(SwissTouristTaxOptionsValidator.Problems(new SwissTouristTaxOptions { PlatformCollected = [(BookingSource)99] }));
        Assert.True(new SwissTouristTaxOptionsValidator().Validate(null, new SwissTouristTaxOptions { Rules = [good] }).Succeeded);
        Assert.True(new SwissTouristTaxOptionsValidator().Validate(null, new SwissTouristTaxOptions { PlatformCollected = [BookingSource.Direct] }).Failed);
    }

    [Fact]
    public async Task NewSwissProfile_StartsFromTheShippedPlatformCollectedDefault_AndAnAnswerIsKept()
    {
        await using var db = NewDb();
        db.Orgs.Add(NewOrg(OrgId, hasVat: false));
        var second = Guid.NewGuid();
        db.Orgs.Add(NewOrg(second, hasVat: false));
        await db.SaveChangesAsync();
        var issuers = IssuersOf(db, ShippedSwissTouristTax());

        // Nothing said about the channels: the default is copied into the profile when it is created.
        await issuers.SaveSettingsAsync(OrgId, SwissSetting(channels: null));
        Assert.Equal([BookingSource.Airbnb], (await db.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.OrgId == OrgId)).PlatformCollectedTouristTaxChannels);

        // An answer, even an empty one, is kept; and a save of the existing profile never brings the default back.
        await issuers.SaveSettingsAsync(second, SwissSetting(channels: []));
        Assert.Empty((await db.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.OrgId == second)).PlatformCollectedTouristTaxChannels);
        await issuers.SaveSettingsAsync(OrgId, SwissSetting(channels: []));
        Assert.Empty((await db.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.OrgId == OrgId)).PlatformCollectedTouristTaxChannels);

        // An Italian profile never gets it.
        var italian = Guid.NewGuid();
        db.Orgs.Add(NewOrg(italian, hasVat: true));
        await db.SaveChangesAsync();
        await issuers.SaveSettingsAsync(
            italian,
            SwissSetting(channels: null) with { CountryCode = "IT", Regime = DocumentRegime.VatOrdinary, PostalCode = "00187", Province = "RM", SwissVatNumber = null, VatRegistered = null });
        Assert.Empty((await db.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.OrgId == italian)).PlatformCollectedTouristTaxChannels);
    }

    // ---------------------------------------------------------------- helpers

    private static SaveIssuerSettingsCommand SwissSetting(IReadOnlyList<string>? channels) =>
        new()
        {
            Mode = DocumentIssuerMode.CasaZen,
            Regime = DocumentRegime.SwissVatRegistered,
            LegalName = "TEST Agenzia SA",
            AddressStreet = "Via Test",
            AddressCivic = "1",
            PostalCode = "6900",
            City = "Lugano",
            CountryCode = "CH",
            SwissVatNumber = "CHE-109.322.551",
            PlatformCollectedTouristTaxChannels = channels,
        };

    private static DocumentIssuerService IssuersOf(AppDbContext db, SwissTouristTaxOptions swissTax)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Test");
        return new DocumentIssuerService(
            db,
            new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance),
            TaxRuleCatalogs.Resolver(),
            environment.Object,
            new FakeTimeProvider(Now),
            NullLogger<DocumentIssuerService>.Instance,
            Options.Create(swissTax));
    }

    private static decimal? TaxOf(GuestDocumentLines lines) =>
        lines.Lines.SingleOrDefault(l => l.LineType == FiscalLineType.TouristTax)?.GrossAmount;

    private static FiscalTaxRule Rule(string country, DocumentRegime regime) =>
        TaxRuleCatalogs.Resolver().Resolve(country, regime, StayVatTreatment.Standard, CleaningFeeTreatment.FollowsStay, new DateOnly(2026, 11, 18));

    private static GuestDocumentLineBuilder BuilderOf(AppDbContext db, SwissTouristTaxOptions swissTax, FiscalDocumentRulesOptions? options = null) =>
        new(db, new ResxDocumentTexts(), Options.Create(options ?? new FiscalDocumentRulesOptions()), Options.Create(swissTax));

    private static FiscalDocumentService ServiceOf(AppDbContext db, SwissTouristTaxOptions swissTax, FiscalDocumentRulesOptions? options = null)
    {
        var featureFlags = new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance);
        var rules = TaxRuleCatalogs.Resolver();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Test");
        var time = new FakeTimeProvider(Now);
        var issuers = new DocumentIssuerService(db, featureFlags, rules, environment.Object, time, NullLogger<DocumentIssuerService>.Instance);
        var invoiceData = new Mock<IInvoiceDataService>();
        invoiceData.Setup(i => i.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid bookingId, CancellationToken _) => new InvoiceDataView(
                bookingId, true, Now.UtcDateTime, InvoiceRecipientKind.Company, "TEST Cliente Srl", null, "IT09876543217", "Via del Cliente", "5",
                "00100", "Roma", "RM", "IT", "ABC1234", null, InvoiceDataSource.Host, Now.UtcDateTime));
        var xml = new Mock<IFatturaPaXmlBuilder>();
        xml.Setup(x => x.Build(It.IsAny<FatturaPaInput>())).Returns("<p:FatturaElettronica/>");
        var texts = new ResxDocumentTexts();
        return new FiscalDocumentService(
            db,
            issuers,
            invoiceData.Object,
            new FiscalNumberingService(db, time, NullLogger<FiscalNumberingService>.Instance),
            xml.Object,
            Mock.Of<IDomainEventRecorder>(),
            new MigraDocPdfDocumentRenderer(),
            texts,
            rules,
            time,
            NullLogger<FiscalDocumentService>.Instance,
            BuilderOf(db, swissTax, options),
            new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, time));
    }

    /// <summary>The section <c>SwissTouristTax</c> of <c>Casazen.Web/appsettings.json</c>, bound as the application binds it.</summary>
    private static SwissTouristTaxOptions ShippedSwissTouristTax()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "Casazen.Web", "appsettings.json"), optional: false).Build();
        var options = new SwissTouristTaxOptions();
        configuration.GetSection(SwissTouristTaxOptions.SectionName).Bind(options);
        return options;
    }

    private static string Snapshot(FiscalDocument d) =>
        string.Join('|', d.Number, d.Status, d.TaxableBase, d.VatAmount, d.StampDuty, d.Total, d.PaymentChannel, d.RuleVersion, d.IssuerSnapshot, d.UpdatedAt.Ticks,
            string.Join(',', d.Lines.OrderBy(l => l.LineNumber).Select(l => $"{l.LineNumber}:{l.LineType}:{l.Description}:{l.TotalPrice}:{l.VatRatePercent}:{l.VatAmount}")));

    private static StayTouristTax Record(Guid bookingId, DateTime? confirmedAt, decimal total, TouristTaxCollector? declaredCollector) =>
        new()
        {
            OrgId = OrgId,
            BookingId = bookingId,
            Status = TouristTaxQuoteStatus.Calculated,
            Nights = 3,
            TaxableNights = 3,
            Total = total,
            AmountDue = total,
            Collector = confirmedAt is null ? null : TouristTaxCollector.Host,
            ConfirmedAt = confirmedAt,
            CalculationHash = confirmedAt is null ? null : new string('a', StayTouristTax.CalculationHashLength),
            DeclaredCollector = declaredCollector,
            RetainUntil = new DateOnly(2036, 11, 4),
        };

    private static OrgEntity NewOrg(Guid id, bool hasVat) => new()
    {
        Id = id,
        Name = "TEST Org",
        Slug = $"test-{id:N}"[..20],
        DisplayName = "TEST Org",
        ContactEmail = "test@example.com",
        HasPartitaIva = hasVat,
        PartitaIvaNumber = hasVat ? "01234567897" : null,
        FiscalCode = hasVat ? "01234567897" : null,
    };

    private static async Task SeedItalianOrgAsync(AppDbContext db)
    {
        db.Orgs.Add(NewOrg(OrgId, hasVat: true));
        db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
        {
            OrgId = OrgId,
            IssuerMode = DocumentIssuerMode.CasaZen,
            DocumentRegime = DocumentRegime.VatOrdinary,
            LegalName = "TEST Casa Vacanze Srl",
            AddressStreet = "Via di Test",
            AddressCivic = "1",
            PostalCode = "00187",
            City = "Roma",
            Province = "RM",
            CountryCode = "IT",
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedSwissOrgAsync(AppDbContext db, IReadOnlyList<BookingSource> platformCollected)
    {
        db.Orgs.Add(NewOrg(OrgId, hasVat: false));
        db.SwissMunicipalities.Add(new SwissMunicipality { MunicipalityCode = MunicipalityLugano, CantonCode = "TI", OfficialName = "Lugano" });
        db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
        {
            OrgId = OrgId,
            IssuerMode = DocumentIssuerMode.CasaZen,
            DocumentRegime = DocumentRegime.SwissVatRegistered,
            LegalName = "TEST Agenzia SA",
            AddressStreet = "Via Test",
            AddressCivic = "1",
            PostalCode = "6900",
            City = "Lugano",
            CountryCode = "CH",
            VatRegistered = true,
            SwissVatNumber = "CHE-109.322.551",
            PlatformCollectedTouristTaxChannels = platformCollected,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AddPropertyAsync(AppDbContext db, bool swiss, string municipality = MunicipalityLugano)
    {
        var property = new Property
        {
            OrgId = OrgId,
            OwnerId = "auth0|host",
            Name = "TEST Casa",
            Address = "Via 1",
            City = swiss ? "Lugano" : "Roma",
            Country = swiss ? "CH" : "IT",
            Currency = swiss ? "CHF" : "EUR",
            SwissMunicipalityCode = swiss ? municipality : null,
            ComuneIstatCode = swiss ? null : IstatRoma,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }

    private static async Task<Guid> AddBookingAsync(
        AppDbContext db,
        Guid propertyId,
        BookingSource source,
        decimal basePrice,
        decimal cleaningFee,
        decimal touristTax,
        int guests,
        decimal? totalPrice = null,
        bool? companyCollect = null,
        bool? commissionIncluded = null,
        DateTime? checkIn = null,
        DateTime? checkOut = null)
    {
        var guest = new Guest { OrgId = OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = OrgId,
            PropertyId = propertyId,
            GuestId = guest.Id,
            CheckInDate = checkIn ?? new DateTime(2026, 11, 14),
            CheckOutDate = checkOut ?? new DateTime(2026, 11, 17),
            NumberOfGuests = guests,
            Status = BookingStatus.CheckedOut,
            Source = source,
            BasePrice = basePrice,
            CleaningFee = cleaningFee,
            TouristTax = touristTax,
            TotalPrice = totalPrice ?? basePrice + touristTax,
            OtaCompanyCollect = companyCollect,
            OtaCommissionIncludedInPayments = commissionIncluded,
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    private static async Task ChangeBookingAsync(AppDbContext db, Guid bookingId, Action<Booking> change)
    {
        var booking = await db.Bookings.SingleAsync(b => b.Id == bookingId);
        change(booking);
        booking.UpdatedAt = Now.UtcDateTime.AddMinutes(5);
        await db.SaveChangesAsync();
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"GuestDocumentLines_{Guid.NewGuid()}").Options, new LinesTenant(), new EphemeralDataProtectionProvider());

    private sealed class LinesTenant : ITenantContext
    {
        public Guid? OrgId => null;

        public bool FilterEnabled => false;
    }
}
