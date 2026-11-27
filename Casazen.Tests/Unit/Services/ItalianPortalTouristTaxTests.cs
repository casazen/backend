using System.Data;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Models;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Authorization;
using Casazen.Tests.Unit.Infrastructure;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// FIX/BE-IT-TOURISTTAX-DEFAULTS (decision 2 of <c>fatturazione/decisioni-fiscali-2026-11-20.md</c>, research R2): the guest document of an Italian portal booking says «incassata dal portale» only if
/// for that booking the tax is recorded as collected, otherwise «da pagare in struttura»; no channel is taken to collect it by default; the setup proposes «Airbnb incassa, da confermare» for Milano and
/// Firenze only, never saving or applying it; and the Swiss behaviour is what it was.
/// </summary>
public class ItalianPortalTouristTaxTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid OrgId = Guid.NewGuid();
    private static readonly Guid OtherOrgId = Guid.NewGuid();
    private const string IstatRoma = "058091";
    private const string IstatComo = "013075";
    private const string MunicipalityLugano = "5192";

    /// <summary>
    /// PROOF TEST: money rule. An Italian portal booking with no record of the collection and no rule of the organization says «da pagare in struttura» whatever the portal (Airbnb, Booking.com, Expedia,
    /// Vrbo, an unknown one): the tax is billed on its own line, the document says what the portal collected (450.00) and what is still to collect at the property (12.00, an estimate for which the property
    /// issues a separate numbered receipt), in Italian and in English with different words, and never «incassata dal portale». No channel collects it by default: the default of the configuration is empty, and an
    /// Italian issuer never inherits the Swiss list.
    /// </summary>
    [Fact]
    public async Task ItalianPortalBooking_DefaultsToPayAtProperty_Test()
    {
        await using var db = NewDb();
        await SeedItalianOrgAsync(db);
        var property = await AddPropertyAsync(db, IstatRoma);
        var profile = await db.OrgFiscalIssuerProfiles.SingleAsync();

        // The default of the channel is none, and the Swiss list (Airbnb) is never an Italian issuer's.
        var builder = BuilderOf(db, new SwissTouristTaxOptions { PlatformCollected = [BookingSource.Airbnb] }, italian: new ItalianTouristTaxOptions());
        Assert.False(profile.PlatformCollectedTouristTaxChannelsConfirmed);
        Assert.Empty(builder.PlatformCollectedChannelsOf(profile));
        Assert.Empty(new ItalianTouristTaxOptions().PlatformCollected);
        Assert.Empty(ShippedItalianTouristTax().PlatformCollected);
        Assert.Equal([BookingSource.Airbnb], builder.PlatformCollectedChannelsOf(new OrgFiscalIssuerProfile { CountryCode = "CH" }));

        var rule = Rule("IT", DocumentRegime.VatOrdinary);
        var channels = new[] { BookingSource.Airbnb, BookingSource.BookingCom, BookingSource.Expedia, BookingSource.Vrbo, BookingSource.OtherChannel };
        var bookings = new List<Guid>();
        foreach (var channel in channels)
        {
            var booking = await AddBookingAsync(db, property, channel, basePrice: 450m, touristTax: 12m, companyCollect: true);
            bookings.Add(booking);
            var built = await builder.BuildAsync(booking, rule, builder.PlatformCollectedChannelsOf(profile));

            Assert.Equal((FiscalPaymentChannel.Portal, 462m, (decimal?)12m), (built.PaymentChannel, built.Lines.Sum(l => l.GrossAmount), TaxOf(built)));
            Assert.Equal("Soggiorno di 3 notti", Assert.Single(built.Lines, l => l.LineType == FiscalLineType.Accommodation).Description);
        }

        var service = ServiceOf(db);
        var draft = await service.CreateDraftAsync(bookings[1], "auth0|host");
        Assert.Equal(462.00m, draft.Total);
        Assert.Equal(new PortalCollectionSplit(450.00m, 12.00m), FiscalDocumentPortalCollection.SplitOf(draft));

        var italian = TextOf(await service.PdfContentAsync(draft.Id, "it"));
        Assert.Contains("Pagamento: pagato tramite il portale per EUR 450,00", italian, StringComparison.Ordinal);
        Assert.Contains("Imposta di soggiorno da pagare in struttura: EUR 12,00", italian, StringComparison.Ordinal);
        Assert.Contains("una ricevuta separata e numerata", italian, StringComparison.Ordinal);
        Assert.DoesNotContain("incassata dal portale", italian, StringComparison.Ordinal);

        Assert.Contains("Imposta di soggiorno da pagare in struttura\n", italian, StringComparison.Ordinal);
        Assert.DoesNotContain("riscossa per conto del comune", italian, StringComparison.Ordinal);
        Assert.DoesNotContain("da riscuotere", italian, StringComparison.Ordinal);
        Assert.DoesNotContain("Tassa", italian, StringComparison.Ordinal);

        var english = TextOf(await service.PdfContentAsync(draft.Id, "en"));
        Assert.Contains("Payment: paid through the portal for EUR 450.00", english, StringComparison.Ordinal);
        Assert.Contains("Tourist tax payable at the property: EUR 12.00", english, StringComparison.Ordinal);
        Assert.Contains("Tourist tax payable at the property\n", english, StringComparison.Ordinal);
        Assert.Contains("separate numbered receipt", english, StringComparison.Ordinal);
        Assert.DoesNotContain("collected by the portal", english, StringComparison.Ordinal);
    }

    /// <summary>
    /// PROOF TEST: money rule. When the channel is declared as collecting by the organization (its list of portals, or the default of the channel while it saved none), or the record of the stay says so
    /// (the host's declaration, the collector of the calculation it confirmed, the rule of the comune), the document is not «da pagare»: the tax has no line (never a double charge), the document is paid
    /// through the portal in full, and the line of the stay says that the portal collected it and that the exemptions are asked of the portal, without naming the portal. The record of the stay is stronger than
    /// the organization's list; what the host saved always wins over the default, an empty list included; and with no amount known there is nothing to say.
    /// </summary>
    [Fact]
    public async Task ItalianPortalBooking_ChannelDeclaredAsCollecting_OrRecordedAsCollected_IsNotToPay_Test()
    {
        await using var db = NewDb();
        await SeedItalianOrgAsync(db);
        var property = await AddPropertyAsync(db, IstatRoma);
        var profile = await db.OrgFiscalIssuerProfiles.SingleAsync();
        var airbnb = await AddBookingAsync(db, property, BookingSource.Airbnb, basePrice: 450m, touristTax: 12m, companyCollect: true);
        var bookingCom = await AddBookingAsync(db, property, BookingSource.BookingCom, basePrice: 450m, touristTax: 12m, companyCollect: true);
        var service = ServiceOf(db);

        profile.ConfirmPlatformCollectedTouristTaxChannels([BookingSource.Airbnb]);
        await db.SaveChangesAsync();
        var declared = await service.CreateDraftAsync(airbnb, "auth0|host");
        Assert.DoesNotContain(declared.Lines, l => l.LineType == FiscalLineType.TouristTax);
        Assert.Equal((450.00m, FiscalDocumentPaymentState.Paid, FiscalPaymentChannel.Portal), (declared.Total, declared.PaymentState, declared.PaymentChannel));
        Assert.Null(FiscalDocumentPortalCollection.SplitOf(declared));
        var collected = TextOf(await service.PdfContentAsync(declared.Id, "it"));
        Assert.Contains("Imposta di soggiorno incassata dal portale (per le esenzioni rivolgersi al portale)", collected, StringComparison.Ordinal);
        Assert.Contains("Pagamento: pagato", collected, StringComparison.Ordinal);
        Assert.DoesNotContain("da pagare in struttura", collected, StringComparison.Ordinal);
        Assert.DoesNotContain("Airbnb", collected, StringComparison.OrdinalIgnoreCase);

        var notListed = await service.CreateDraftAsync(bookingCom, "auth0|host");
        Assert.Equal(new PortalCollectionSplit(450.00m, 12.00m), FiscalDocumentPortalCollection.SplitOf(notListed));
        Assert.Contains("Imposta di soggiorno da pagare in struttura: EUR 12,00", TextOf(await service.PdfContentAsync(notListed.Id, "it")), StringComparison.Ordinal);

        // What the host saved always wins over the default of the channel: Airbnb is the default here, and the host confirmed that no portal collects it.
        var withDefault = ServiceOf(db, italian: new ItalianTouristTaxOptions { PlatformCollected = [BookingSource.Airbnb] });
        profile.ConfirmPlatformCollectedTouristTaxChannels([]);
        await db.SaveChangesAsync();
        Assert.Equal(new PortalCollectionSplit(450.00m, 12.00m), FiscalDocumentPortalCollection.SplitOf(await withDefault.RefreshDraftAsync(declared.Id)));

        profile.PlatformCollectedTouristTaxChannelsJson = null;
        await db.SaveChangesAsync();
        var byDefault = await withDefault.RefreshDraftAsync(declared.Id);
        Assert.DoesNotContain(byDefault.Lines, l => l.LineType == FiscalLineType.TouristTax);
        Assert.Null((await db.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync()).PlatformCollectedTouristTaxChannelsJson);

        var declaredChannel = await AddBookingAsync(db, property, BookingSource.BookingCom, basePrice: 450m, touristTax: 12m, companyCollect: true);
        var confirmedChannel = await AddBookingAsync(db, property, BookingSource.Expedia, basePrice: 450m, touristTax: 12m, companyCollect: true);
        db.StayTouristTaxes.Add(Record(declaredChannel, confirmedAt: null, total: 0m, declared: TouristTaxCollector.Channel, collector: null));
        db.StayTouristTaxes.Add(Record(confirmedChannel, confirmedAt: Now.UtcDateTime, total: 12m, declared: null, collector: TouristTaxCollector.Channel));
        await db.SaveChangesAsync();
        var plain = ServiceOf(db);
        foreach (var booking in new[] { declaredChannel, confirmedChannel })
        {
            var recorded = await plain.CreateDraftAsync(booking, "auth0|host");
            Assert.DoesNotContain(recorded.Lines, l => l.LineType == FiscalLineType.TouristTax);
            Assert.Null(FiscalDocumentPortalCollection.SplitOf(recorded));
            Assert.Contains("Imposta di soggiorno incassata dal portale", TextOf(await plain.PdfContentAsync(recorded.Id, "it")), StringComparison.Ordinal);
        }

        profile.ConfirmPlatformCollectedTouristTaxChannels([BookingSource.Airbnb]);
        var airbnbPaidHere = await AddBookingAsync(db, property, BookingSource.Airbnb, basePrice: 450m, touristTax: 12m, companyCollect: true);
        db.StayTouristTaxes.Add(Record(airbnbPaidHere, confirmedAt: null, total: 0m, declared: TouristTaxCollector.Host, collector: null));
        await db.SaveChangesAsync();
        Assert.Equal(new PortalCollectionSplit(450.00m, 12.00m), FiscalDocumentPortalCollection.SplitOf(await plain.CreateDraftAsync(airbnbPaidHere, "auth0|host")));

        db.TouristTaxChannelRules.Add(new TouristTaxChannelRule { Channel = BookingSource.Vrbo, IstatCode = IstatRoma, Collector = TouristTaxCollector.Channel, SourceUrl = "https://example.com/vrbo" });
        var vrbo = await AddBookingAsync(db, property, BookingSource.Vrbo, basePrice: 450m, touristTax: 12m, companyCollect: true);
        await db.SaveChangesAsync();
        Assert.DoesNotContain((await plain.CreateDraftAsync(vrbo, "auth0|host")).Lines, l => l.LineType == FiscalLineType.TouristTax);

        var noAmount = await AddBookingAsync(db, property, BookingSource.Airbnb, basePrice: 450m, touristTax: 0m, companyCollect: true);
        var quiet = await plain.CreateDraftAsync(noAmount, "auth0|host");
        Assert.Equal(450.00m, quiet.Total);
        Assert.Equal("Soggiorno di 3 notti", Assert.Single(quiet.Lines, l => l.LineType == FiscalLineType.Accommodation).Description);
    }

    [Fact]
    public void ItalianPortalTouristTax_Decide_FollowsTheOrderOfTheDecision()
    {
        var airbnbList = new[] { BookingSource.Airbnb };
        var channelRule = new TouristTaxChannelRule { Channel = BookingSource.Airbnb, IstatCode = IstatRoma, Collector = TouristTaxCollector.Channel, SourceUrl = "https://example.com/airbnb" };
        var hostRule = new TouristTaxChannelRule { Channel = BookingSource.Airbnb, IstatCode = IstatRoma, Collector = TouristTaxCollector.Host, SourceUrl = "https://example.com/airbnb" };

        Assert.Equal(
            new ItalianTouristTaxDecision(TouristTaxCollector.Host, ItalianTouristTaxOrigin.Default),
            ItalianPortalTouristTax.Decide(BookingSource.BookingCom, null, null, null, []));
        Assert.Equal(
            new ItalianTouristTaxDecision(TouristTaxCollector.Host, ItalianTouristTaxOrigin.NotAChannelBooking),
            ItalianPortalTouristTax.Decide(BookingSource.Direct, channelRule, TouristTaxCollector.Channel, TouristTaxCollector.Channel, [BookingSource.Direct]));

        Assert.Equal(
            new ItalianTouristTaxDecision(TouristTaxCollector.Channel, ItalianTouristTaxOrigin.Organization),
            ItalianPortalTouristTax.Decide(BookingSource.Airbnb, null, null, null, airbnbList));
        Assert.Equal(
            new ItalianTouristTaxDecision(TouristTaxCollector.Host, ItalianTouristTaxOrigin.StayRecord),
            ItalianPortalTouristTax.Decide(BookingSource.Airbnb, null, null, TouristTaxCollector.Host, airbnbList));
        Assert.Equal(
            new ItalianTouristTaxDecision(TouristTaxCollector.Host, ItalianTouristTaxOrigin.StayRecord),
            ItalianPortalTouristTax.Decide(BookingSource.Airbnb, null, TouristTaxCollector.Host, TouristTaxCollector.Channel, airbnbList));
        Assert.Equal(
            new ItalianTouristTaxDecision(TouristTaxCollector.Channel, ItalianTouristTaxOrigin.ChannelRule),
            ItalianPortalTouristTax.Decide(BookingSource.Airbnb, channelRule, TouristTaxCollector.Host, null, []));
        Assert.Equal(
            new ItalianTouristTaxDecision(TouristTaxCollector.Host, ItalianTouristTaxOrigin.ChannelRule),
            ItalianPortalTouristTax.Decide(BookingSource.Airbnb, hostRule, TouristTaxCollector.Channel, null, airbnbList));

        // The portal the organization confirmed for one comune counts for that comune only: not for another comune, not for a property with no ISTAT code, not for another portal; the record of the stay
        // is still stronger than it.
        var milanoAirbnb = new[] { new PortalComune(BookingSource.Airbnb, ItalianPortalProposals.MilanoIstat) };
        Assert.Equal(
            new ItalianTouristTaxDecision(TouristTaxCollector.Channel, ItalianTouristTaxOrigin.Organization),
            ItalianPortalTouristTax.Decide(BookingSource.Airbnb, null, null, null, [], milanoAirbnb, ItalianPortalProposals.MilanoIstat));
        Assert.Equal(
            new ItalianTouristTaxDecision(TouristTaxCollector.Host, ItalianTouristTaxOrigin.Default),
            ItalianPortalTouristTax.Decide(BookingSource.Airbnb, null, null, null, [], milanoAirbnb, "013075"));
        Assert.Equal(
            new ItalianTouristTaxDecision(TouristTaxCollector.Host, ItalianTouristTaxOrigin.Default),
            ItalianPortalTouristTax.Decide(BookingSource.Airbnb, null, null, null, [], milanoAirbnb, null));
        Assert.Equal(
            new ItalianTouristTaxDecision(TouristTaxCollector.Host, ItalianTouristTaxOrigin.Default),
            ItalianPortalTouristTax.Decide(BookingSource.BookingCom, null, null, null, [], milanoAirbnb, ItalianPortalProposals.MilanoIstat));
        Assert.Equal(
            new ItalianTouristTaxDecision(TouristTaxCollector.Host, ItalianTouristTaxOrigin.StayRecord),
            ItalianPortalTouristTax.Decide(BookingSource.Airbnb, null, TouristTaxCollector.Host, null, [], milanoAirbnb, ItalianPortalProposals.MilanoIstat));
        Assert.True(ItalianPortalTouristTax.Contradicts(BookingSource.Airbnb, TouristTaxCollector.Host, [], milanoAirbnb, ItalianPortalProposals.MilanoIstat));
        Assert.False(ItalianPortalTouristTax.Contradicts(BookingSource.Airbnb, TouristTaxCollector.Host, [], milanoAirbnb, "013075"));

        Assert.True(ItalianPortalTouristTax.Contradicts(BookingSource.BookingCom, TouristTaxCollector.Channel, []));
        Assert.True(ItalianPortalTouristTax.Contradicts(BookingSource.Airbnb, TouristTaxCollector.Host, airbnbList));
        Assert.False(ItalianPortalTouristTax.Contradicts(BookingSource.Airbnb, TouristTaxCollector.Channel, airbnbList));
        Assert.False(ItalianPortalTouristTax.Contradicts(BookingSource.Airbnb, TouristTaxCollector.Host, []));
        Assert.False(ItalianPortalTouristTax.Contradicts(BookingSource.Airbnb, null, airbnbList));
        Assert.False(ItalianPortalTouristTax.Contradicts(BookingSource.Direct, TouristTaxCollector.Channel, []));
    }

    /// <summary>
    /// PROOF TEST: data rule. The billing setup of an Italian organization proposes «Airbnb incassa, da confermare» only for Milano (ISTAT 015146) and Firenze (ISTAT 048017), from the comuni of its own
    /// properties and never from another organization's; nothing is saved or applied by reading it (the profile keeps its list, the documents still say «da pagare in struttura»), and what the organization saved,
    /// an empty list included, is never proposed over or overridden. A Swiss issuer gets none of it, and a caller who reaches only some of the properties does not get the members that sum up all of them.
    /// </summary>
    [Fact]
    public async Task ItalianSetup_ProposesAirbnbOnlyForMilanAndFlorence_AndNeverOverridesWhatIsSaved_Test()
    {
        var databaseName = $"italian-setup-{Guid.NewGuid()}";
        await using (var seed = NewDb(databaseName))
        {
            await SeedItalianOrgAsync(seed);
            await AddPropertyAsync(seed, ItalianPortalProposals.MilanoIstat);
            await AddPropertyAsync(seed, ItalianPortalProposals.FirenzeIstat);
            await AddPropertyAsync(seed, IstatRoma);
            await AddPropertyAsync(seed, null);

            await AddPropertyAsync(seed, IstatRoma, orgId: OtherOrgId);
        }

        await using var db = NewDb(databaseName, new FixedTenant(OrgId));
        var service = SetupOf(db);
        var expected = new[]
        {
            new ItalianPortalProposal(ItalianPortalProposals.MilanoIstat, "Milano", "Airbnb", "to_confirm"),
            new ItalianPortalProposal(ItalianPortalProposals.FirenzeIstat, "Firenze", "Airbnb", "to_confirm"),
        };

        // Milano and Firenze only: Roma, a property with no ISTAT code and any other comune have no proposal.
        var section = await TouristTaxSectionAsync(service);
        Assert.Equal(expected, Assert.IsAssignableFrom<IReadOnlyList<ItalianPortalProposal>>(section.Values[ItalianTouristTaxSetupKeys.Proposals]));
        Assert.Empty(ItalianPortalProposals.For([IstatRoma, null, " ", "999999"]));
        Assert.Equal(["Milano"], ItalianPortalProposals.For([" 015146 "]).Select(p => p.Comune));

        using (var json = JsonDocument.Parse(JsonSerializer.Serialize(BillingSetupDto.From(await service.GetAsync(OrgId), orgWide: true), new JsonSerializerOptions(JsonSerializerDefaults.Web))))
        {
            var values = json.RootElement.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("section").GetString() == "touristTax").GetProperty("values");
            var first = values.GetProperty("italianPortalProposals").EnumerateArray().First();
            Assert.Equal(
                ("015146", "Milano", "Airbnb", "to_confirm"),
                (first.GetProperty("comuneIstat").GetString(), first.GetProperty("comune").GetString(), first.GetProperty("channel").GetString(), first.GetProperty("status").GetString()));
            Assert.False(values.GetProperty("portalTouristTaxConflict").GetBoolean());
            Assert.Equal(JsonValueKind.Array, values.GetProperty("defaultPlatformCollectedTouristTaxChannels").ValueKind);
        }

        Assert.Equal(false, section.Values["confirmed"]);
        Assert.Null(section.Values[BillingSetupFields.PlatformCollectedTouristTaxChannels]);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(section.Values[BillingSetupService.DefaultPlatformCollectedTouristTaxChannels]));
        Assert.Null((await StoredProfileAsync(databaseName)).PlatformCollectedTouristTaxChannelsJson);
        var milan = await db.Properties.AsNoTracking().FirstAsync(p => p.ComuneIstatCode == ItalianPortalProposals.MilanoIstat && p.OrgId == OrgId);
        var airbnb = await AddBookingAsync(db, milan.Id, BookingSource.Airbnb, basePrice: 450m, touristTax: 12m, companyCollect: true);
        var builder = BuilderOf(db, new SwissTouristTaxOptions());
        var profile = await StoredProfileAsync(databaseName);
        var built = await builder.BuildAsync(airbnb, Rule("IT", DocumentRegime.VatOrdinary), builder.PlatformCollectedChannelsOf(profile));
        Assert.Equal(12m, TaxOf(built));

        await using (var other = NewDb(databaseName, new FixedTenant(OtherOrgId)))
        {
            Assert.Empty((await new ItalianTouristTaxSetupService(other, new FakeTimeProvider(Now)).ReadAsync(OtherOrgId, [], organizationConfirmed: false)).Proposals);

            Assert.Empty((await new ItalianTouristTaxSetupService(other, new FakeTimeProvider(Now)).ReadAsync(OrgId, [], organizationConfirmed: false)).Proposals);
        }

        var saved = await service.PutSectionAsync(OrgId, "touristTax", new BillingSetupPayload { PlatformCollectedTouristTaxChannels = ["BookingCom"] }, null);
        Assert.Equal(["BookingCom"], Assert.IsAssignableFrom<IReadOnlyList<string>>(saved.Values[BillingSetupFields.PlatformCollectedTouristTaxChannels]));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ItalianPortalProposal>>(saved.Values[ItalianTouristTaxSetupKeys.Proposals]));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ItalianPortalProposal>>((await TouristTaxSectionAsync(service)).Values[ItalianTouristTaxSetupKeys.Proposals]));
        Assert.Equal("""["BookingCom"]""", (await StoredProfileAsync(databaseName)).PlatformCollectedTouristTaxChannelsJson);

        await service.PutSectionAsync(OrgId, "touristTax", new BillingSetupPayload { PlatformCollectedTouristTaxChannels = [] }, null);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ItalianPortalProposal>>((await TouristTaxSectionAsync(service)).Values[ItalianTouristTaxSetupKeys.Proposals]));
        Assert.Equal("[]", (await StoredProfileAsync(databaseName)).PlatformCollectedTouristTaxChannelsJson);

        await service.PutSectionAsync(OrgId, "issuer", new BillingSetupPayload { CountryCode = "CH" }, null);
        var swiss = await TouristTaxSectionAsync(service);
        Assert.False(swiss.Values.ContainsKey(ItalianTouristTaxSetupKeys.Proposals));
        Assert.False(swiss.Values.ContainsKey(ItalianTouristTaxSetupKeys.ChannelDataConflict));

        await service.PutSectionAsync(OrgId, "issuer", new BillingSetupPayload { CountryCode = "IT" }, null);
        var setup = await service.GetAsync(OrgId);
        var wholeOrg = Assert.Single(BillingSetupDto.From(setup, orgWide: true).Sections, s => s.Section == BillingSetupSections.TouristTax);
        var partial = Assert.Single(BillingSetupDto.From(setup, orgWide: false).Sections, s => s.Section == BillingSetupSections.TouristTax);
        Assert.Contains(ItalianTouristTaxSetupKeys.Proposals, wholeOrg.Values.Keys);
        Assert.Contains(ItalianTouristTaxSetupKeys.ChannelDataConflict, wholeOrg.Values.Keys);
        Assert.DoesNotContain(ItalianTouristTaxSetupKeys.Proposals, partial.Values.Keys);
        Assert.DoesNotContain(ItalianTouristTaxSetupKeys.ChannelDataConflict, partial.Values.Keys);
        Assert.Contains(BillingSetupService.DefaultPlatformCollectedTouristTaxChannels, partial.Values.Keys);
    }

    [Fact]
    public async Task ItalianSetup_OrgWideMembers_AreGivenOnlyToACallerWhoReachesTheWholeOrganization_Test()
    {
        var databaseName = $"italian-setup-scope-{Guid.NewGuid()}";
        await using (var seed = NewDb(databaseName))
        {
            await SeedItalianOrgAsync(seed);
            await AddPropertyAsync(seed, ItalianPortalProposals.MilanoIstat);
        }

        await using var db = NewDb(databaseName, new FixedTenant(OrgId));
        var service = SetupOf(db);

        async Task<IReadOnlyDictionary<string, object?>> TouristTaxValuesOf(string userId, string role)
        {
            var read = await ControllerOf(service, HostAuthorizationTestHarness.User(userId, role)).Get(CancellationToken.None);
            var setup = Assert.IsType<BillingSetupDto>(Assert.IsType<OkObjectResult>(read.Result).Value);
            return setup.Sections.Single(s => s.Section == BillingSetupSections.TouristTax).Values;
        }

        var manager = await TouristTaxValuesOf("auth0|manager-a", "PropertyManager");
        Assert.Equal("Milano", Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ItalianPortalProposal>>(manager[ItalianTouristTaxSetupKeys.Proposals])).Comune);
        Assert.Contains(ItalianTouristTaxSetupKeys.ChannelDataConflict, manager.Keys);

        var staff = await TouristTaxValuesOf("auth0|staff-a", "Staff");
        Assert.DoesNotContain(ItalianTouristTaxSetupKeys.Proposals, staff.Keys);
        Assert.DoesNotContain(ItalianTouristTaxSetupKeys.ChannelDataConflict, staff.Keys);
        Assert.Contains(BillingSetupService.DefaultPlatformCollectedTouristTaxChannels, staff.Keys);
        Assert.Contains("confirmed", staff.Keys);
    }

    [Fact]
    public async Task ItalianSetup_FlagsRecordsOfTheStaysThatContradictTheRuleOfTheOrganization()
    {
        var databaseName = $"italian-setup-hint-{Guid.NewGuid()}";
        await using (var seed = NewDb(databaseName))
        {
            await SeedItalianOrgAsync(seed);

            var otherProperty = await AddPropertyAsync(seed, IstatRoma, orgId: OtherOrgId);
            var otherStay = await AddBookingAsync(seed, otherProperty, BookingSource.BookingCom, basePrice: 450m, touristTax: 12m, companyCollect: true, orgId: OtherOrgId);
            seed.StayTouristTaxes.Add(Record(otherStay, confirmedAt: null, total: 0m, declared: TouristTaxCollector.Channel, collector: null, orgId: OtherOrgId));
            await seed.SaveChangesAsync();
        }

        await using var db = NewDb(databaseName, new FixedTenant(OrgId));
        var service = SetupOf(db);
        var property = await AddPropertyAsync(db, IstatRoma);

        async Task<bool> Conflict() => (bool)(await TouristTaxSectionAsync(service)).Values[ItalianTouristTaxSetupKeys.ChannelDataConflict]!;

        Assert.False(await Conflict());
        var agrees = await AddBookingAsync(db, property, BookingSource.BookingCom, basePrice: 450m, touristTax: 12m, companyCollect: true);
        db.StayTouristTaxes.Add(Record(agrees, confirmedAt: null, total: 0m, declared: TouristTaxCollector.Host, collector: null));
        await db.SaveChangesAsync();
        Assert.False(await Conflict());

        var old = await AddBookingAsync(db, property, BookingSource.BookingCom, basePrice: 450m, touristTax: 12m, companyCollect: true, checkIn: new DateTime(2025, 6, 7), checkOut: new DateTime(2025, 6, 10));
        var cancelled = await AddBookingAsync(db, property, BookingSource.BookingCom, basePrice: 450m, touristTax: 12m, companyCollect: true, status: BookingStatus.Cancelled);
        var direct = await AddBookingAsync(db, property, BookingSource.Direct, basePrice: 450m, touristTax: 12m, companyCollect: false);
        foreach (var booking in new[] { old, cancelled, direct })
            db.StayTouristTaxes.Add(Record(booking, confirmedAt: null, total: 0m, declared: TouristTaxCollector.Channel, collector: null));
        await db.SaveChangesAsync();
        Assert.False(await Conflict());

        var storedBefore = (await StoredProfileAsync(databaseName)).PlatformCollectedTouristTaxChannelsJson;

        var recent = await AddBookingAsync(db, property, BookingSource.BookingCom, basePrice: 450m, touristTax: 12m, companyCollect: true);
        db.StayTouristTaxes.Add(Record(recent, confirmedAt: Now.UtcDateTime, total: 12m, declared: null, collector: TouristTaxCollector.Channel));
        await db.SaveChangesAsync();
        Assert.True(await Conflict());

        await service.PutSectionAsync(OrgId, "touristTax", new BillingSetupPayload { PlatformCollectedTouristTaxChannels = ["BookingCom"] }, null);
        Assert.True(await Conflict());
        db.StayTouristTaxes.Remove(await db.StayTouristTaxes.SingleAsync(t => t.BookingId == agrees));
        await db.SaveChangesAsync();
        Assert.False(await Conflict());

        Assert.Null(storedBefore);
        Assert.Equal("""["BookingCom"]""", (await StoredProfileAsync(databaseName)).PlatformCollectedTouristTaxChannelsJson);

        await service.PutSectionAsync(OrgId, "touristTax", new BillingSetupPayload { PlatformCollectedTouristTaxChannels = [] }, null);
        Assert.True(await Conflict());
        var setup = new ItalianTouristTaxSetupService(db, new FakeTimeProvider(Now));
        await setup.ConfirmComuniAsync(OrgId, [new PortalComune(BookingSource.BookingCom, "013075")], null);
        Assert.True(await Conflict());
        await setup.ConfirmComuniAsync(OrgId, [new PortalComune(BookingSource.BookingCom, IstatRoma)], null);
        Assert.False(await Conflict());
    }

    /// <summary>
    /// PROOF TEST: money rule (the existing Swiss tests stay untouched and green). Switzerland is what it was whatever the Italian options say: the Swiss default list (Airbnb) and the list the issuer
    /// confirmed decide, the tax is a line unless the portal collects it, the Swiss layout says «tassa» («Tassa di soggiorno da riscuotere in struttura»), the line of the stay is the plain one, and nothing
    /// Italian (the receipt, «incassata dal portale», «imposta») is on a Swiss document.
    /// </summary>
    [Fact]
    public async Task SwissPortalBooking_IsUnchanged_Test()
    {
        await using var db = NewDb();
        await SeedSwissOrgAsync(db);
        var property = await AddSwissPropertyAsync(db);
        var airbnb = await AddBookingAsync(db, property, BookingSource.Airbnb, basePrice: 450m, touristTax: 12m, companyCollect: true);
        var bookingCom = await AddBookingAsync(db, property, BookingSource.BookingCom, basePrice: 450m, touristTax: 12m, companyCollect: true);
        var swiss = new SwissTouristTaxOptions { PlatformCollected = [BookingSource.Airbnb] };

        // The Italian list says the opposite (Booking.com collects it): a Swiss issuer never reads it.
        var italian = new ItalianTouristTaxOptions { PlatformCollected = [BookingSource.BookingCom] };
        var service = ServiceOf(db, swiss, italian);
        var builder = BuilderOf(db, swiss, italian);
        var profile = await db.OrgFiscalIssuerProfiles.SingleAsync();
        Assert.Equal([BookingSource.Airbnb], builder.PlatformCollectedChannelsOf(profile));

        var collected = await service.CreateDraftAsync(airbnb, "auth0|host");
        Assert.DoesNotContain(collected.Lines, l => l.LineType == FiscalLineType.TouristTax);
        Assert.Equal("Soggiorno di 3 notti", Assert.Single(collected.Lines, l => l.LineType == FiscalLineType.Accommodation).Description);
        Assert.Null(FiscalDocumentPortalCollection.SplitOf(collected));
        var collectedText = TextOf(await service.PdfContentAsync(collected.Id, "it"));
        Assert.Contains("Pagata tramite il portale", collectedText, StringComparison.Ordinal);
        AssertNothingItalian(collectedText);

        var toCollect = await service.CreateDraftAsync(bookingCom, "auth0|host");
        Assert.Equal(new PortalCollectionSplit(450.00m, 12.00m), FiscalDocumentPortalCollection.SplitOf(toCollect));
        var toCollectText = TextOf(await service.PdfContentAsync(toCollect.Id, "it"));
        Assert.Contains("Pagata tramite il portale: CHF 450.00", toCollectText, StringComparison.Ordinal);
        Assert.Contains("Tassa di soggiorno da riscuotere in struttura: CHF 12.00", toCollectText, StringComparison.Ordinal);
        Assert.Contains("Tassa di soggiorno (non soggetta all'IVA) - tassa da riscuotere in struttura", toCollectText, StringComparison.Ordinal);
        AssertNothingItalian(toCollectText);
        Assert.Contains("Tourist tax to be collected at the property: CHF 12.00", TextOf(await service.PdfContentAsync(toCollect.Id, "en")), StringComparison.Ordinal);

        profile.ConfirmPlatformCollectedTouristTaxChannels([BookingSource.BookingCom]);
        await db.SaveChangesAsync();
        Assert.Equal([BookingSource.BookingCom], builder.PlatformCollectedChannelsOf(profile));
        Assert.Equal(12m, TaxOf(await builder.BuildAsync(airbnb, Rule("CH", DocumentRegime.SwissVatRegistered), builder.PlatformCollectedChannelsOf(profile))));
        Assert.Null(TaxOf(await builder.BuildAsync(bookingCom, Rule("CH", DocumentRegime.SwissVatRegistered), builder.PlatformCollectedChannelsOf(profile))));
    }

    /// <summary>
    /// The queries of the Italian rule and of the setup go to SQL on the PostgreSQL provider with no server (<see cref="NpgsqlTranslationProbe"/>): InMemory accepts what Npgsql cannot translate, and a
    /// query with no translation would be a 500 of the billing setup of every Italian organization, not a failed proof. The line builder is asked for an Airbnb stay in Roma (the rule of the comune, the
    /// record of the stay with the collector of its calculation), the setup for the proposals and for the hint (a join of the records with the active stays of the Italian properties).
    /// </summary>
    [Fact]
    public async Task ItalianTouristTax_Queries_TranslateToSqlOnPostgreSql()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements, rows: StayRowOf);
        var builder = BuilderOf(db, new SwissTouristTaxOptions());
        var setup = new ItalianTouristTaxSetupService(db, new FakeTimeProvider(Now));

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => builder.BuildAsync(Guid.NewGuid(), Rule("IT", DocumentRegime.VatOrdinary), [BookingSource.Airbnb]));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => setup.ReadAsync(OrgId, [BookingSource.Airbnb], organizationConfirmed: false));

        Assert.Contains(statements, s => s.Contains("FROM \"TouristTaxChannelRules\"", StringComparison.Ordinal));

        Assert.Contains(statements, s => s.Contains("FROM \"OrgFiscalIssuerProfiles\"", StringComparison.Ordinal) && s.Contains("\"PlatformCollectedTouristTaxChannels\"", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.Contains("FROM \"StayTouristTaxes\"", StringComparison.Ordinal) && s.Contains("CASE", StringComparison.Ordinal));
        Assert.Contains(statements, s => s.Contains("DISTINCT", StringComparison.Ordinal) && s.Contains("\"ComuneIstatCode\"", StringComparison.Ordinal));
        Assert.Contains(
            statements,
            s => s.Contains("\"StayTouristTaxes\"", StringComparison.Ordinal) && s.Contains("JOIN", StringComparison.Ordinal) && s.Contains("\"CheckOutDate\"", StringComparison.Ordinal)
                 && s.Contains("\"DeclaredCollector\"", StringComparison.Ordinal));
    }

    private static DataTable? StayRowOf(string sql)
    {
        var text = sql.TrimStart();
        if (!text.StartsWith("SELECT", StringComparison.Ordinal) || !text.Contains("FROM \"Bookings\"", StringComparison.Ordinal) || !text.Contains("\"OtaCompanyCollect\"", StringComparison.Ordinal))
            return null;

        var types = new Dictionary<string, (Type Type, object Value)>
        {
            ["Id"] = (typeof(Guid), Guid.NewGuid()),
            ["Source"] = (typeof(int), (int)BookingSource.Airbnb),
            ["BasePrice"] = (typeof(decimal), 450m),
            ["CleaningFee"] = (typeof(decimal), 0m),
            ["TouristTax"] = (typeof(decimal), 12m),
            ["NumberOfGuests"] = (typeof(int), 2),
            ["CheckInDate"] = (typeof(DateTime), new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc)),
            ["CheckOutDate"] = (typeof(DateTime), new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc)),
            ["OtaCompanyCollect"] = (typeof(bool), true),
            ["OtaCommissionIncludedInPayments"] = (typeof(bool), DBNull.Value),
            ["SwissMunicipalityCode"] = (typeof(string), DBNull.Value),
            ["ComuneIstatCode"] = (typeof(string), IstatRoma),
            ["OrgId"] = (typeof(Guid), OrgId),
        };
        var select = text[..text.IndexOf("FROM \"Bookings\"", StringComparison.Ordinal)];
        var names = Regex.Matches(select, "\"(\\w+)\"").Select(m => m.Groups[1].Value).ToList();
        if (names.Count == 0 || names.Any(name => !types.ContainsKey(name)))
            return null;

        var table = new DataTable();
        foreach (var name in names)
            table.Columns.Add(name, types[name].Type);
        table.Rows.Add(names.Select(name => types[name].Value).ToArray());
        return table;
    }

    private static void AssertNothingItalian(string text)
    {
        Assert.DoesNotContain("incassata dal portale", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ricevuta separata", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Imposta di soggiorno", text, StringComparison.Ordinal);
        Assert.DoesNotContain("da pagare in struttura", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ItalianTouristTaxOptions_AreEmptyByDefault_AndAreValidatedAtStartup()
    {
        Assert.Empty(ItalianTouristTaxOptionsValidator.Problems(new ItalianTouristTaxOptions()));
        Assert.Empty(ItalianTouristTaxOptionsValidator.Problems(new ItalianTouristTaxOptions { PlatformCollected = [BookingSource.Airbnb, BookingSource.BookingCom] }));
        Assert.Empty(ShippedItalianTouristTax().PlatformCollected);

        Assert.Single(ItalianTouristTaxOptionsValidator.Problems(new ItalianTouristTaxOptions { PlatformCollected = [BookingSource.Direct] }));
        Assert.Single(ItalianTouristTaxOptionsValidator.Problems(new ItalianTouristTaxOptions { PlatformCollected = [(BookingSource)99] }));
        var failure = new ItalianTouristTaxOptionsValidator().Validate(null, new ItalianTouristTaxOptions { PlatformCollected = [BookingSource.Manual] });
        Assert.True(failure.Failed);
        Assert.Contains("ItalianTouristTax:PlatformCollected", failure.FailureMessage, StringComparison.Ordinal);
        Assert.True(new ItalianTouristTaxOptionsValidator().Validate(null, new ItalianTouristTaxOptions()).Succeeded);
    }

    /// <summary>
    /// PROOF TEST: money rule. The proposal is per comune and so is its confirmation: confirming «Airbnb incassa» for Milano (015146) makes the Airbnb stays of a flat in Milano «incassata dal portale» and
    /// leaves the Airbnb stays of a flat in Como «da pagare in struttura» (the organization never said that Airbnb collects there); without the confirmation both are «da pagare»; another portal in Milano
    /// is not confirmed either; the confirmation is kept apart from the list for every comune (saving that list never takes it away, and it never answers it); an empty confirmation takes them all away
    /// and the column is the plain array it always was; a portal that is not one, a code that is not six digits, a Swiss issuer and a missing profile are refused.
    /// </summary>
    [Fact]
    public async Task ItalianPortalBooking_ConfirmedForOneComune_CountsOnlyForThatComune_Test()
    {
        await using var db = NewDb();
        await SeedItalianOrgAsync(db);
        var milano = await AddPropertyAsync(db, ItalianPortalProposals.MilanoIstat);
        var como = await AddPropertyAsync(db, IstatComo);
        var airbnbMilano = await AddBookingAsync(db, milano, BookingSource.Airbnb, basePrice: 450m, touristTax: 12m, companyCollect: true);
        var airbnbComo = await AddBookingAsync(db, como, BookingSource.Airbnb, basePrice: 450m, touristTax: 12m, companyCollect: true);
        var bookingMilano = await AddBookingAsync(db, milano, BookingSource.BookingCom, basePrice: 450m, touristTax: 12m, companyCollect: true);
        var service = ServiceOf(db);
        var setup = new ItalianTouristTaxSetupService(db, new FakeTimeProvider(Now));
        var toPay = new PortalCollectionSplit(450.00m, 12.00m);

        async Task<PortalCollectionSplit?> SplitOfAsync(Guid booking) => FiscalDocumentPortalCollection.SplitOf(await service.CreateDraftAsync(booking, "auth0|host"));

        Assert.Equal(toPay, await SplitOfAsync(airbnbMilano));
        Assert.Equal(toPay, await SplitOfAsync(airbnbComo));
        Assert.Equal(toPay, await SplitOfAsync(bookingMilano));

        await setup.ConfirmComuniAsync(OrgId, [new PortalComune(BookingSource.Airbnb, ItalianPortalProposals.MilanoIstat)], "auth0|manager-a");
        var profile = await db.OrgFiscalIssuerProfiles.SingleAsync();
        Assert.False(profile.PlatformCollectedTouristTaxChannelsConfirmed);
        Assert.Empty(profile.PlatformCollectedTouristTaxChannels);
        Assert.Equal([new PortalComune(BookingSource.Airbnb, ItalianPortalProposals.MilanoIstat)], profile.PortalTouristTaxComuni);
        Assert.Equal("auth0|manager-a", profile.UpdatedByUserId);

        var collected = await service.CreateDraftAsync(airbnbMilano, "auth0|host");
        Assert.DoesNotContain(collected.Lines, l => l.LineType == FiscalLineType.TouristTax);
        Assert.Null(FiscalDocumentPortalCollection.SplitOf(collected));
        Assert.Contains("Imposta di soggiorno incassata dal portale", TextOf(await service.PdfContentAsync(collected.Id, "it")), StringComparison.Ordinal);
        Assert.Equal(toPay, await SplitOfAsync(airbnbComo));
        Assert.Equal(toPay, await SplitOfAsync(bookingMilano));

        profile.ConfirmPlatformCollectedTouristTaxChannels([BookingSource.BookingCom]);
        await db.SaveChangesAsync();
        Assert.Equal([new PortalComune(BookingSource.Airbnb, ItalianPortalProposals.MilanoIstat)], profile.PortalTouristTaxComuni);
        Assert.Null(FiscalDocumentPortalCollection.SplitOf(await service.CreateDraftAsync(airbnbMilano, "auth0|host")));
        Assert.Null(FiscalDocumentPortalCollection.SplitOf(await service.CreateDraftAsync(bookingMilano, "auth0|host")));
        Assert.Equal(toPay, await SplitOfAsync(airbnbComo));

        await setup.ConfirmComuniAsync(OrgId, [], "auth0|manager-a");
        Assert.Empty(profile.PortalTouristTaxComuni);
        Assert.Equal("""["BookingCom"]""", (await StoredProfileAsync(db)).PlatformCollectedTouristTaxChannelsJson);
        Assert.Equal(toPay, await SplitOfAsync(airbnbMilano));

        async Task<string> RefusalOfAsync(IReadOnlyList<PortalComune> comuni) =>
            (await Assert.ThrowsAsync<DomainRuleException>(() => setup.ConfirmComuniAsync(OrgId, comuni, null))).Code;

        Assert.Equal(DocumentIssuerErrorCodes.FieldInvalid, await RefusalOfAsync([new PortalComune(BookingSource.Direct, ItalianPortalProposals.MilanoIstat)]));
        Assert.Equal(DocumentIssuerErrorCodes.FieldInvalid, await RefusalOfAsync([new PortalComune(BookingSource.Airbnb, "15146")]));
        Assert.Equal(DocumentIssuerErrorCodes.FieldInvalid, await RefusalOfAsync([new PortalComune(BookingSource.Airbnb, "01514A")]));
        Assert.Equal(
            DocumentIssuerErrorCodes.FieldInvalid,
            await RefusalOfAsync([.. Enumerable.Range(0, ItalianTouristTaxSetupService.MaxConfirmedComuni + 1).Select(n => new PortalComune(BookingSource.Airbnb, n.ToString("000000", System.Globalization.CultureInfo.InvariantCulture)))]));
        Assert.Equal("""["BookingCom"]""", (await StoredProfileAsync(db)).PlatformCollectedTouristTaxChannelsJson);

        await using (var empty = NewDb())
        {
            var refusal = await Assert.ThrowsAsync<DomainRuleException>(
                () => new ItalianTouristTaxSetupService(empty, new FakeTimeProvider(Now)).ConfirmComuniAsync(OrgId, [new PortalComune(BookingSource.Airbnb, ItalianPortalProposals.MilanoIstat)], null));
            Assert.Equal(DocumentIssuerErrorCodes.ProfileIncomplete, refusal.Code);
        }

        await using var swiss = NewDb();
        await SeedSwissOrgAsync(swiss);
        var swissRefusal = await Assert.ThrowsAsync<DomainRuleException>(
            () => new ItalianTouristTaxSetupService(swiss, new FakeTimeProvider(Now)).ConfirmComuniAsync(OrgId, [new PortalComune(BookingSource.Airbnb, ItalianPortalProposals.MilanoIstat)], null));
        Assert.Equal(DocumentIssuerErrorCodes.FieldInvalid, swissRefusal.Code);
        Assert.Null((await swiss.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync()).PlatformCollectedTouristTaxChannelsJson);
    }

    [Fact]
    public void PlatformCollectedColumn_KeepsThePlainArray_AndHoldsTheComuneConfirmationsApart()
    {
        var milano = new PortalComune(BookingSource.Airbnb, ItalianPortalProposals.MilanoIstat);
        var profile = new OrgFiscalIssuerProfile();
        Assert.False(profile.PlatformCollectedTouristTaxChannelsConfirmed);
        Assert.Null(profile.PlatformCollectedTouristTaxChannelsJson);

        profile.ConfirmPlatformCollectedTouristTaxChannels([BookingSource.BookingCom]);
        Assert.Equal("""["BookingCom"]""", profile.PlatformCollectedTouristTaxChannelsJson);
        profile.ConfirmPortalTouristTaxComuni([]);
        Assert.Equal("""["BookingCom"]""", profile.PlatformCollectedTouristTaxChannelsJson);
        Assert.Empty(profile.PortalTouristTaxComuni);

        profile.ConfirmPortalTouristTaxComuni([milano, milano]);
        Assert.Equal("""{"channels":["BookingCom"],"comuni":[{"channel":"Airbnb","comuneIstat":"015146"}]}""", profile.PlatformCollectedTouristTaxChannelsJson);
        Assert.True(profile.PlatformCollectedTouristTaxChannelsConfirmed);
        Assert.Equal([BookingSource.BookingCom], profile.PlatformCollectedTouristTaxChannels);
        Assert.Equal([milano], profile.PortalTouristTaxComuni);

        profile.ConfirmPlatformCollectedTouristTaxChannels([]);
        Assert.Equal("""{"channels":[],"comuni":[{"channel":"Airbnb","comuneIstat":"015146"}]}""", profile.PlatformCollectedTouristTaxChannelsJson);
        Assert.True(profile.PlatformCollectedTouristTaxChannelsConfirmed);
        profile.PlatformCollectedTouristTaxChannels = [];
        Assert.Equal("""{"comuni":[{"channel":"Airbnb","comuneIstat":"015146"}]}""", profile.PlatformCollectedTouristTaxChannelsJson);
        Assert.False(profile.PlatformCollectedTouristTaxChannelsConfirmed);
        Assert.Equal([milano], profile.PortalTouristTaxComuni);

        profile.ConfirmPortalTouristTaxComuni([]);
        Assert.Null(profile.PlatformCollectedTouristTaxChannelsJson);
        profile.ConfirmPlatformCollectedTouristTaxChannels([]);
        profile.ConfirmPortalTouristTaxComuni([milano]);
        profile.ConfirmPortalTouristTaxComuni([]);
        Assert.Equal("[]", profile.PlatformCollectedTouristTaxChannelsJson);
        Assert.True(profile.PlatformCollectedTouristTaxChannelsConfirmed);

        var unreadable = new OrgFiscalIssuerProfile { PlatformCollectedTouristTaxChannelsJson = "not json" };
        Assert.True(unreadable.PlatformCollectedTouristTaxChannelsConfirmed);
        Assert.Empty(unreadable.PlatformCollectedTouristTaxChannels);
        Assert.Empty(unreadable.PortalTouristTaxComuni);
        var odd = new OrgFiscalIssuerProfile
        {
            PlatformCollectedTouristTaxChannelsJson = """{"comuni":[{"channel":"Airbnb","comuneIstat":"15146"},{"channel":"Nope","comuneIstat":"015146"},{"channel":"Airbnb","comuneIstat":"015146"},{"channel":"Airbnb","comuneIstat":"015146"}]}""",
        };
        Assert.False(odd.PlatformCollectedTouristTaxChannelsConfirmed);
        Assert.Equal([milano], odd.PortalTouristTaxComuni);
    }

    /// <summary>
    /// PROOF TEST: data and permission rule. The setup carries the confirmation (<c>confirmedPortalTouristTaxComuni</c>) and drops the proposal that was confirmed, keeping the other; the confirmation is
    /// saved by a caller who reaches the whole organization only (403 for «Solo alcuni»), a request with no list is 422 (never «every comune»), it never answers the list for every comune, and a
    /// caller who does not say whom it answers gets none of the members that sum up the organization (the DTO is fail-closed by default).
    /// </summary>
    [Fact]
    public async Task ItalianSetup_ConfirmingAProposal_IsPerComune_AndOnlyForACallerWhoReachesTheWholeOrganization_Test()
    {
        var databaseName = $"italian-setup-confirm-{Guid.NewGuid()}";
        await using (var seed = NewDb(databaseName))
        {
            await SeedItalianOrgAsync(seed);
            await AddPropertyAsync(seed, ItalianPortalProposals.MilanoIstat);
            await AddPropertyAsync(seed, ItalianPortalProposals.FirenzeIstat);
            await AddPropertyAsync(seed, IstatComo);
        }

        await using var db = NewDb(databaseName, new FixedTenant(OrgId));
        var setup = new ItalianTouristTaxSetupService(db, new FakeTimeProvider(Now));
        var service = SetupOf(db);
        var manager = ControllerOf(service, HostAuthorizationTestHarness.User("auth0|manager-a", "PropertyManager"), setup);
        var staff = ControllerOf(service, HostAuthorizationTestHarness.User("auth0|staff-a", "Staff"), setup);

        async Task<BillingSetupSectionDto> ReadSectionAsync(BillingSetupController controller) =>
            Assert.Single(Assert.IsType<BillingSetupDto>(Assert.IsType<OkObjectResult>((await controller.Get(CancellationToken.None)).Result).Value).Sections, s => s.Section == BillingSetupSections.TouristTax);

        var before = await ReadSectionAsync(manager);
        Assert.Equal(["Milano", "Firenze"], Assert.IsAssignableFrom<IReadOnlyList<ItalianPortalProposal>>(before.Values[ItalianTouristTaxSetupKeys.Proposals]).Select(p => p.Comune));
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<ItalianPortalConfirmation>>(before.Values[ItalianTouristTaxSetupKeys.ConfirmedComuni]));

        var request = new ConfirmPortalComuniRequest { Comuni = [new PortalComuneRequest { Channel = "Airbnb", ComuneIstat = ItalianPortalProposals.MilanoIstat }] };
        var saved = Assert.IsType<BillingSetupSectionDto>(Assert.IsType<OkObjectResult>((await manager.PutPortalComuni(request, CancellationToken.None)).Result).Value);
        Assert.Equal(new ItalianPortalConfirmation(ItalianPortalProposals.MilanoIstat, "Airbnb"), Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ItalianPortalConfirmation>>(saved.Values[ItalianTouristTaxSetupKeys.ConfirmedComuni])));
        Assert.Equal(["Firenze"], Assert.IsAssignableFrom<IReadOnlyList<ItalianPortalProposal>>(saved.Values[ItalianTouristTaxSetupKeys.Proposals]).Select(p => p.Comune));
        Assert.False(saved.Values.ContainsKey(BillingSetupFields.PlatformCollectedTouristTaxChannels));
        Assert.Equal(false, saved.Values["confirmed"]);
        Assert.Equal("""{"comuni":[{"channel":"Airbnb","comuneIstat":"015146"}]}""", (await StoredProfileAsync(databaseName)).PlatformCollectedTouristTaxChannelsJson);

        var refused = await staff.PutPortalComuni(new ConfirmPortalComuniRequest { Comuni = [] }, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<ObjectResult>(refused.Result).StatusCode);
        var staffSection = await ReadSectionAsync(staff);
        Assert.DoesNotContain(ItalianTouristTaxSetupKeys.ConfirmedComuni, staffSection.Values.Keys);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ItalianPortalConfirmation>>((await ReadSectionAsync(manager)).Values[ItalianTouristTaxSetupKeys.ConfirmedComuni]));

        var missing = await Assert.ThrowsAsync<DomainRuleException>(() => manager.PutPortalComuni(new ConfirmPortalComuniRequest(), CancellationToken.None));
        Assert.Equal(DocumentIssuerErrorCodes.FieldInvalid, missing.Code);
        var unknown = await Assert.ThrowsAsync<DomainRuleException>(
            () => manager.PutPortalComuni(new ConfirmPortalComuniRequest { Comuni = [new PortalComuneRequest { Channel = "Nope", ComuneIstat = ItalianPortalProposals.MilanoIstat }] }, CancellationToken.None));
        Assert.Equal(DocumentIssuerErrorCodes.FieldInvalid, unknown.Code);

        var closed = Assert.Single(BillingSetupDto.From(await service.GetAsync(OrgId)).Sections, s => s.Section == BillingSetupSections.TouristTax);
        foreach (var key in BillingSetupSectionDto.OrgWideValues)
            Assert.DoesNotContain(key, closed.Values.Keys);
        Assert.Contains(ItalianTouristTaxSetupKeys.ConfirmedComuni, BillingSetupSectionDto.OrgWideValues);
        Assert.Contains(ItalianTouristTaxSetupKeys.Proposals, BillingSetupSectionDto.OrgWideValues);
        Assert.Contains(ItalianTouristTaxSetupKeys.ChannelDataConflict, BillingSetupSectionDto.OrgWideValues);
    }

    /// <summary>
    /// PROOF TEST: money rule. The line of the tax a portal left to the host is written «Imposta di soggiorno da pagare in struttura» when the document is made, so that the stored line (the XML and the API
    /// read it) says what the PDF says; the line of a tax the host collects with the rest says «riscossa per conto del comune». The PDF keeps both honest: once the stay records all the tax as collected
    /// the line reads «riscossa» even though the document was made with «da pagare», and while some is to collect it reads «da pagare» whatever an older document stored.
    /// </summary>
    [Fact]
    public async Task ItalianPortalBooking_TaxLeftToTheHost_IsWrittenDaPagareInStruttura_InTheLineItself_Test()
    {
        await using var db = NewDb();
        await SeedItalianOrgAsync(db);
        var property = await AddPropertyAsync(db, IstatRoma);
        var portalCollects = await AddBookingAsync(db, property, BookingSource.Airbnb, basePrice: 450m, touristTax: 12m, companyCollect: true);
        var hostCollects = await AddBookingAsync(db, property, BookingSource.Airbnb, basePrice: 450m, touristTax: 12m, companyCollect: false);
        var direct = await AddBookingAsync(db, property, BookingSource.Direct, basePrice: 450m, touristTax: 12m, companyCollect: false);
        var service = ServiceOf(db);

        static string TaxLineOf(FiscalDocument draft) => Assert.Single(draft.Lines, l => l.LineType == FiscalLineType.TouristTax).Description;

        var toPay = await service.CreateDraftAsync(portalCollects, "auth0|host");
        Assert.Equal("Imposta di soggiorno da pagare in struttura", TaxLineOf(toPay));
        Assert.Equal("Imposta di soggiorno da pagare in struttura", Assert.Single(FiscalDocumentDto.From((await service.GetAsync(toPay.Id))!).Lines, l => l.LineType == "TouristTax").Description);
        Assert.Contains("Imposta di soggiorno da pagare in struttura\n", TextOf(await service.PdfContentAsync(toPay.Id, "it")), StringComparison.Ordinal);

        Assert.Equal("Imposta di soggiorno riscossa per conto del comune", TaxLineOf(await service.CreateDraftAsync(hostCollects, "auth0|host")));
        Assert.Equal("Imposta di soggiorno riscossa per conto del comune", TaxLineOf(await service.CreateDraftAsync(direct, "auth0|host")));

        var record = Record(portalCollects, confirmedAt: null, total: 0m, declared: null, collector: null);
        record.CollectionStatus = TouristTaxCollectionStatus.PaidAtProperty;
        db.StayTouristTaxes.Add(record);
        await db.SaveChangesAsync();
        var collected = TextOf(await service.PdfContentAsync(toPay.Id, "it"));
        Assert.Contains("Imposta di soggiorno riscossa per conto del comune", collected, StringComparison.Ordinal);
        Assert.DoesNotContain("in struttura", collected, StringComparison.Ordinal);

        record.CollectionStatus = TouristTaxCollectionStatus.ToCollect;
        var stored = await db.FiscalDocumentLines.SingleAsync(l => l.DocumentId == toPay.Id && l.LineType == FiscalLineType.TouristTax);
        stored.Description = "Imposta di soggiorno riscossa per conto del comune";
        await db.SaveChangesAsync();
        var older = TextOf(await service.PdfContentAsync(toPay.Id, "it"));
        Assert.Contains("Imposta di soggiorno da pagare in struttura\n", older, StringComparison.Ordinal);
        Assert.DoesNotContain("riscossa per conto del comune", older, StringComparison.Ordinal);
    }

    /// <summary>
    /// PROOF TEST: money rule. A document issued before the default, whose booking now bills a tax only because the default says «da pagare in struttura», is no changed booking: the daily run tells nothing
    /// (no «To do» of a change), and a change event makes no credit note and no new document. A real change (the dates) is still corrected, and the new draft carries the line.
    /// </summary>
    [Fact]
    public async Task ItalianPortalDocument_IssuedBeforeTheDefault_IsNotCorrectedForTheSoleAdditionOfTheTaxLine_Test()
    {
        await using var db = NewDb();
        await SeedItalianOrgAsync(db);
        var property = await AddPropertyAsync(db, IstatRoma);

        var booking = await AddBookingAsync(db, property, BookingSource.Airbnb, basePrice: 450m, touristTax: 0m, companyCollect: true, checkIn: new DateTime(2026, 11, 8), checkOut: new DateTime(2026, 11, 11));
        var service = ServiceOf(db, withIssuanceFlag: true);
        var issued = await service.IssueAsync((await service.CreateDraftAsync(booking, "auth0|host")).Id, confirm: true);
        Assert.Equal(FiscalDocumentStatus.Issued, issued.Status);
        Assert.DoesNotContain((await db.FiscalDocuments.Include(d => d.Lines).SingleAsync(d => d.Id == issued.Id)).Lines, l => l.LineType == FiscalLineType.TouristTax);

        var stay = await db.Bookings.SingleAsync(b => b.Id == booking);
        stay.TouristTax = 12m;
        await db.SaveChangesAsync();
        var built = await BuilderOf(db, new SwissTouristTaxOptions()).BuildAsync(booking, Rule("IT", DocumentRegime.VatOrdinary), []);
        Assert.True(built.TouristTaxFromDefault);
        Assert.Equal(12m, TaxOf(built));

        var run = await service.ProcessDueDocumentsAsync(OrgId, limit: 50, lookbackDays: 60);
        Assert.Equal(0, run.ChangesTold);
        await service.ApplyBookingChangeAsync(booking, BookingChange.Modified);
        Assert.Empty(await db.InternalActivities.ToListAsync());
        Assert.Equal([issued.Id], await db.FiscalDocuments.Where(d => d.BookingId == booking).Select(d => d.Id).ToListAsync());

        stay.BasePrice = 500m;
        stay.TotalPrice = 512m;
        await db.SaveChangesAsync();
        await service.ApplyBookingChangeAsync(booking, BookingChange.Modified);
        var documents = await db.FiscalDocuments.Include(d => d.Lines).Where(d => d.BookingId == booking).ToListAsync();
        Assert.Contains(documents, d => d.Kind == Casazen.Core.Entities.Enums.FiscalDocumentKind.CreditNote);
        Assert.Contains(documents, d => d.Status == FiscalDocumentStatus.Draft && d.Kind != Casazen.Core.Entities.Enums.FiscalDocumentKind.CreditNote && d.Lines.Any(l => l.LineType == FiscalLineType.TouristTax));
    }

    private static decimal? TaxOf(GuestDocumentLines lines) => lines.Lines.SingleOrDefault(l => l.LineType == FiscalLineType.TouristTax)?.GrossAmount;

    private static FiscalTaxRule Rule(string country, DocumentRegime regime) =>
        TaxRuleCatalogs.Resolver().Resolve(country, regime, StayVatTreatment.Standard, CleaningFeeTreatment.FollowsStay, new DateOnly(2026, 11, 18));

    private static GuestDocumentLineBuilder BuilderOf(AppDbContext db, SwissTouristTaxOptions swiss, ItalianTouristTaxOptions? italian = null) =>
        new(db, new ResxDocumentTexts(), Options.Create(new FiscalDocumentRulesOptions()), Options.Create(swiss), italian is null ? null : Options.Create(italian));

    private static DocumentIssuerService IssuersOf(AppDbContext db, TimeProvider time)
    {
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Test");
        return new DocumentIssuerService(
            db,
            new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance),
            TaxRuleCatalogs.Resolver(),
            environment.Object,
            time,
            NullLogger<DocumentIssuerService>.Instance);
    }

    private static BillingSetupService SetupOf(AppDbContext db)
    {
        var time = new FakeTimeProvider(Now);
        var issuers = IssuersOf(db, time);
        return new BillingSetupService(
            issuers,
            [new TouristTaxSectionContributor(issuers)],
            NullLogger<BillingSetupService>.Instance,
            documentLines: BuilderOf(db, new SwissTouristTaxOptions { PlatformCollected = [BookingSource.Airbnb] }, new ItalianTouristTaxOptions()),
            italianTouristTax: new ItalianTouristTaxSetupService(db, time));
    }

    private static BillingSetupController ControllerOf(BillingSetupService service, ClaimsPrincipal user, IItalianTouristTaxSetup? italianSetup = null)
    {
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(OrgId);
        return new BillingSetupController(service, resolver.Object, HostAuthorizationTestHarness.ScopeResolver(), NullLogger<BillingSetupController>.Instance, null, italianSetup)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = user,
                    RequestServices = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider(),
                },
            },
        };
    }

    private static async Task<BillingSetupSection> TouristTaxSectionAsync(BillingSetupService service) =>
        (await service.GetAsync(OrgId)).Sections.Single(s => s.Section == BillingSetupSections.TouristTax);

    private static FiscalDocumentService ServiceOf(AppDbContext db, SwissTouristTaxOptions? swiss = null, ItalianTouristTaxOptions? italian = null, bool withIssuanceFlag = false)
    {
        var rules = TaxRuleCatalogs.Resolver();
        var time = new FakeTimeProvider(Now);
        var issuers = IssuersOf(db, time);
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
            BuilderOf(db, swiss ?? new SwissTouristTaxOptions(), italian),
            new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, time),
            featureFlags: withIssuanceFlag ? new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance) : null);
    }

    private static ItalianTouristTaxOptions ShippedItalianTouristTax()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "Casazen.Web", "appsettings.json"), optional: false).Build();
        var options = new ItalianTouristTaxOptions();
        configuration.GetSection(ItalianTouristTaxOptions.SectionName).Bind(options);
        return options;
    }

    private static string TextOf(PdfDocumentContent content) => string.Join('\n', new[] { content.Title }.Concat(content.Blocks.SelectMany(TextsOf)));

    private static IEnumerable<string> TextsOf(PdfBlock block) => block switch
    {
        PdfHeading heading => new[] { heading.Text },
        PdfParagraph paragraph => new[] { paragraph.Text },
        PdfTable table => table.Columns.Select(c => c.Header).Concat(table.Rows.SelectMany(r => r)),
        _ => Array.Empty<string>(),
    };

    private static StayTouristTax Record(Guid bookingId, DateTime? confirmedAt, decimal total, TouristTaxCollector? declared, TouristTaxCollector? collector, Guid? orgId = null) =>
        new()
        {
            OrgId = orgId ?? OrgId,
            BookingId = bookingId,
            Status = TouristTaxQuoteStatus.Calculated,
            Nights = 3,
            TaxableNights = 3,
            Total = total,
            AmountDue = total,
            Collector = collector,
            ConfirmedAt = confirmedAt,
            CalculationHash = confirmedAt is null ? null : new string('a', StayTouristTax.CalculationHashLength),
            DeclaredCollector = declared,
            RetainUntil = new DateOnly(2036, 11, 4),
        };

    private static OrgEntity NewOrg(Guid id) => new()
    {
        Id = id,
        Name = "TEST Org",
        Slug = $"test-{id:N}"[..20],
        DisplayName = "TEST Org",
        ContactEmail = "test@example.com",
        HasPartitaIva = true,
        PartitaIvaNumber = "01234567897",
        FiscalCode = "01234567897",
    };

    private static async Task SeedItalianOrgAsync(AppDbContext db)
    {
        db.Orgs.Add(NewOrg(OrgId));
        db.Orgs.Add(NewOrg(OtherOrgId));
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

    private static async Task SeedSwissOrgAsync(AppDbContext db)
    {
        db.Orgs.Add(NewOrg(OrgId));
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
        });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AddPropertyAsync(AppDbContext db, string? istatCode, Guid? orgId = null)
    {
        var property = new Property
        {
            OrgId = orgId ?? OrgId,
            OwnerId = "auth0|host",
            Name = "TEST Casa",
            Address = "Via 1",
            City = "Roma",
            Country = "IT",
            Currency = "EUR",
            ComuneIstatCode = istatCode,
        };
        db.Properties.Add(property);
        await db.SaveChangesAsync();
        return property.Id;
    }

    private static async Task<Guid> AddSwissPropertyAsync(AppDbContext db)
    {
        var property = new Property
        {
            OrgId = OrgId,
            OwnerId = "auth0|host",
            Name = "TEST Casa",
            Address = "Via 1",
            City = "Lugano",
            Country = "CH",
            Currency = "CHF",
            SwissMunicipalityCode = MunicipalityLugano,
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
        decimal touristTax,
        bool companyCollect,
        DateTime? checkIn = null,
        DateTime? checkOut = null,
        BookingStatus status = BookingStatus.CheckedOut,
        Guid? orgId = null)
    {
        var guest = new Guest { OrgId = orgId ?? OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" };
        var booking = new Booking
        {
            OrgId = orgId ?? OrgId,
            PropertyId = propertyId,
            GuestId = guest.Id,
            CheckInDate = checkIn ?? new DateTime(2026, 11, 14),
            CheckOutDate = checkOut ?? new DateTime(2026, 11, 17),
            NumberOfGuests = 2,
            Status = status,
            Source = source,
            BasePrice = basePrice,
            CleaningFee = 0m,
            TouristTax = touristTax,
            TotalPrice = basePrice + touristTax,
            OtaCompanyCollect = FiscalCopy.IsOtaBookingSource(source) ? companyCollect : null,
        };
        db.Guests.Add(guest);
        db.Bookings.Add(booking);
        await db.SaveChangesAsync();
        return booking.Id;
    }

    private static async Task<OrgFiscalIssuerProfile> StoredProfileAsync(AppDbContext db) =>
        await db.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.OrgId == OrgId);

    private static async Task<OrgFiscalIssuerProfile> StoredProfileAsync(string databaseName)
    {
        await using var check = NewDb(databaseName);
        return await check.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.OrgId == OrgId);
    }

    private static AppDbContext NewDb(string? databaseName = null, ITenantContext? tenant = null) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName ?? $"ItalianPortalTouristTax_{Guid.NewGuid()}").Options,
            tenant ?? new FixedTenant(null),
            TestDataProtection.Shared);

    private sealed class FixedTenant(Guid? orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => OrgId is not null;
    }
}
