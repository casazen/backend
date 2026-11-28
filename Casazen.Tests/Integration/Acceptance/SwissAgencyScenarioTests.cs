using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Web.DTOs.OwnerPortal;
using Casazen.Web.Resources;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;
using World = Casazen.Tests.Integration.Acceptance.AcceptanceScenarioBuilder;

namespace Casazen.Tests.Integration.Acceptance;

/// <summary>
/// E19/F01/US01/T01 (NF-112, NF-113, NF-114, NF-115, NF-118; the Swiss acceptance scenario of 2026-11-14): the agency of Lugano of
/// <see cref="AcceptanceScenarioBuilder"/> takes bookings from the four origins (Booking.com through the channel manager, its own site through the channel
/// manager, the CasaZen site paid with Stripe, a booking entered by hand) and each one goes, through the real code, from the booking to the CHF document of the guest
/// (lines, VAT 3.8% / 8.1%, the tourist tax of Ticino, the payment state, the number, the PDF, the e-mail with its link), the posting of the month, the statement
/// version (the gross without the tourist tax, the channel fee, the commission, the recharges, the net), the invoice of the property manager (8.1%, its VAT posted
/// to the ledger, the QR-bill only when an amount is due), the statement published on the owner portal, the package of the accountant and the wire file of the
/// payout. Every amount is asserted to the cent. The changes of a booking (B12: a date change, a cancellation with a refund, a no-show) produce their corrections.
/// </summary>
/// <remarks>
/// The trait <c>Scenario</c> of each test lists the steps of the scenario it covers (A, B, C of <c>pilota-flusso-completo-2026-11-14.md</c>; <c>--filter Scenario~C4</c>
/// selects them). No test has a display name of its own: the planner of the test shards of the CI (<c>scripts/ci/test_shards.py</c>) reads the default name,
/// <c>Namespace.Class.Method</c>, to place each test in its class. The main path runs on the in-memory database and, as a <see cref="PostgresFactAttribute"/> twin,
/// on PostgreSQL in CI.
/// </remarks>
public class SwissAgencyScenarioTests
{
    private static readonly AccountantPackagePeriod October = AccountantPackagePeriod.OfMonth(2026, 10);

    private static readonly DateTimeOffset EndOfOctober = World.InRome(Day(10, 31), 16);

    private static readonly DateOnly PayDay = Day(11, 2);

    // Origine 1, Booking.com tramite Octorate: documento CHF, rendiconto, fattura PM, pacchetto e bonifico.
    [Fact]
    [Trait("Scenario", "A1 A2 B7 B8a B8b C1 C2-V1 C3 C4 C6 C7 C8")]
    public async Task Origin_BookingCom_ThroughOctorate_FromTheReservationToThePackageAndTheWire()
    {
        await using var world = await World.CreateAsync();
        world.At(World.InRome(Day(9, 20), 10));
        var booking = await world.ImportFromChannelManagerAsync(world.BookingComReservation(Day(10, 2), Day(10, 5), 450m, commission: 67.50m));

        var stored = await world.BookingAsync(booking);
        Assert.Equal(
            (BookingSource.BookingCom, BookingStatus.Confirmed, 450.00m, 450.00m, 0m, (decimal?)67.50m, (bool?)true, 2),
            (stored.Source, stored.Status, stored.BasePrice, stored.TotalPrice, stored.TouristTax, stored.OtaCommission, stored.OtaCompanyCollect, stored.NumberOfGuests));
        Assert.NotNull(stored.OctorateReservationId);

        // The portal sends no invoice data and the agency types none: the document is made out to the guest of the booking, by name only (FIX/DOC-RECIPIENT).
        // B7, B8a: the document after the stay: the stay at 3.8% and the tourist tax of Ticino (2 guests x 3 nights x CHF 2.00, Booking.com does not collect it).
        // FIX/BE-PORTAL-TAXLINE: the document of a booking of a portal is paid through the portal for what the portal collected (450.00), never for the line of the tax the portal does not
        // collect and nobody recorded (12.00): that is to collect at the property. The stored state is still the one of the channel (the portal collected), the total is the whole document,
        // and the PDF says the rest.
        var document = await IssuedAfterTheStayAsync(world, booking, Day(10, 6), "2026/1");
        AssertRecipientIsTheGuestByName(document, "TEST Ospite Portale");
        AssertDocument(
            document,
            [(FiscalLineType.Accommodation, 433.53m, 3.8m, 16.47m), (FiscalLineType.TouristTax, 12.00m, 0m, 0m)],
            (445.53m, 16.47m, 462.00m),
            FiscalPaymentChannel.Portal,
            FiscalDocumentPaymentState.Paid);
        await AssertGuestPdfAsync(world, document, "Pagata tramite il portale: CHF 450.00", ("445.53", "16.47", "462.00"));
        var guestPdf = TextOf(await world.PdfContentAsync(document.Id));
        Assert.Contains("Tassa di soggiorno da riscuotere in struttura: CHF 12.00", guestPdf, StringComparison.Ordinal);
        Assert.Contains("Tassa di soggiorno (non soggetta all'IVA) - tassa da riscuotere in struttura", guestPdf, StringComparison.Ordinal);

        AssertTheGuestGotTheDocument(world, document, await world.GuestEmailOfAsync(booking), "Fattura");

        var month = await world.CloseMonthAsync(2026, 10, EndOfOctober);
        await AssertMonthAsync(
            world,
            month,
            new Expected(
                Gross: 450.00m,
                ChannelFees: -67.50m,
                Commission: -90.00m,
                Vat: -13.77m,
                Recharges: -80.00m,
                Closing: 198.73m,
                InvoiceLines: [(FiscalLineType.Commission, 90.00m, 8.1m, 7.29m), (FiscalLineType.Recharge, 80.00m, 8.1m, 6.48m)],
                InvoiceTotals: (170.00m, 13.77m, 183.77m)));

        var fee = Assert.Single(await world.LedgerOfAsync(2026, 10), e => e.EntryType == OwnerLedgerEntryType.ChannelFee);
        Assert.Equal((-67.50m, (Guid?)booking, new DateOnly(2026, 10, 5)), (fee.Amount, fee.SourceId, fee.RecognitionDate));
        Assert.Equal(ChannelFeeSources.Payload, fee.RuleVersion);

        var statement = await world.StatementOfAsync(month.Published.Id);
        Assert.False(Assert.Single(statement.Of(OwnerStatementSection.ChannelFees)).AgencyHoldsPortalAccount);
        Assert.Contains("Trattenuta dal portale (fattura del portale a tuo nome)", StatementTextOf(statement), StringComparison.Ordinal);

        AssertPackage(
            await world.PackageAsync(October),
            guestDocuments: 1,
            vatRows: ["2026-Q4;8,10;170,00;13,77;CHF;1", "2026-Q4;3,80;433,53;16,47;CHF;1"],
            portalCommissionRow: null);
        await AssertPayoutAndWireAsync(world, month.Published, 198.73m);
    }

    // Origine 2, sito dell'agenzia tramite il canale diretto di Octorate: incasso registrato a mano, documento CHF, rendiconto, fattura PM, pacchetto e bonifico.
    [Fact]
    [Trait("Scenario", "B7 B8a B8b C1 C3 C4 C6 C7 C8")]
    public async Task Origin_AgencySite_ThroughTheDirectChannel_PaidOutsideCasaZen_FromTheReservationToThePackageAndTheWire()
    {
        await using var world = await World.CreateAsync();
        world.At(World.InRome(Day(9, 25), 10));
        var booking = await world.ImportFromChannelManagerAsync(world.AgencySiteReservation(Day(10, 9), Day(10, 12), 390m, touristTax: 12m));

        var stored = await world.BookingAsync(booking);
        Assert.Equal(
            (BookingSource.Direct, 390.00m, 402.00m, 12.00m, (decimal?)null, (bool?)null),
            (stored.Source, stored.BasePrice, stored.TotalPrice, stored.TouristTax, stored.OtaCommission, stored.OtaCompanyCollect));
        Assert.NotNull(stored.OctorateReservationId);
        await world.GiveInvoiceDataAsync(booking, "TEST Ospite Sito");

        world.At(World.InRome(Day(10, 8), 9));
        await world.RecordPaymentByHandAsync(booking, 402m, PaymentMethod.BankTransfer, Day(10, 8));

        var document = await IssuedAfterTheStayAsync(world, booking, Day(10, 13), "2026/1");
        AssertDocument(
            document,
            [(FiscalLineType.Accommodation, 375.72m, 3.8m, 14.28m), (FiscalLineType.TouristTax, 12.00m, 0m, 0m)],
            (387.72m, 14.28m, 402.00m),
            FiscalPaymentChannel.Outside,
            FiscalDocumentPaymentState.Paid);
        await AssertGuestPdfAsync(world, document, "Pagata", ("387.72", "14.28", "402.00"));
        AssertTheGuestGotTheDocument(world, document, await world.GuestEmailOfAsync(booking), "Fattura");

        var month = await world.CloseMonthAsync(2026, 10, EndOfOctober);
        await AssertMonthAsync(
            world,
            month,
            new Expected(
                Gross: 390.00m,
                ChannelFees: 0m,
                Commission: -78.00m,
                Vat: -12.80m,
                Recharges: -80.00m,
                Closing: 219.20m,
                InvoiceLines: [(FiscalLineType.Commission, 78.00m, 8.1m, 6.32m), (FiscalLineType.Recharge, 80.00m, 8.1m, 6.48m)],
                InvoiceTotals: (158.00m, 12.80m, 170.80m)));
        var revenue = Assert.Single(await world.LedgerOfAsync(2026, 10), e => e.EntryType == OwnerLedgerEntryType.BookingRevenue);
        Assert.Equal((OwnerLedgerSourceType.Payment, new DateOnly(2026, 10, 8)), (revenue.SourceType, revenue.RecognitionDate));

        AssertPackage(
            await world.PackageAsync(October),
            guestDocuments: 1,
            vatRows: ["2026-Q4;8,10;158,00;12,80;CHF;1", "2026-Q4;3,80;375,72;14,28;CHF;1"],
            portalCommissionRow: null);
        await AssertPayoutAndWireAsync(world, month.Published, 219.20m);
    }

    // Origine 3, sito CasaZen pagato con Stripe: commissione Stripe a carico dell'agenzia, documento CHF, rendiconto, fattura PM, pacchetto e bonifico.
    [Fact]
    [Trait("Scenario", "B7 B8a B8b C1 C3 C4 C6 C7 C8")]
    public async Task Origin_CasaZenSite_PaidWithStripe_FromTheCheckoutToThePackageAndTheWire()
    {
        await using var world = await World.CreateAsync();
        world.At(World.InRome(Day(10, 1), 12));
        var (booking, payment) = await world.PayOnTheCasaZenSiteAsync(Day(10, 16), Day(10, 19), price: 420m, cleaningFee: 60m, providerFee: 12.48m);

        Assert.Equal(BookingStatus.Confirmed, (await world.BookingAsync(booking)).Status);
        var paid = await world.PaymentAsync(payment);
        Assert.Equal((PaymentStatus.Completed, 420.00m, (decimal?)12.48m, (decimal?)407.52m), (paid.Status, paid.Amount, paid.ProviderFeeAmount, paid.ProviderNetAmount));
        await world.GiveInvoiceDataAsync(booking, "TEST Ospite Vetrina");

        var document = await IssuedAfterTheStayAsync(world, booking, Day(10, 20), "2026/1");
        AssertDocument(
            document,
            [(FiscalLineType.Accommodation, 346.82m, 3.8m, 13.18m), (FiscalLineType.Cleaning, 57.80m, 3.8m, 2.20m), (FiscalLineType.TouristTax, 12.00m, 0m, 0m)],
            (416.62m, 15.38m, 432.00m),
            FiscalPaymentChannel.CasaZen,
            FiscalDocumentPaymentState.ToCollect);
        await AssertGuestPdfAsync(world, document, "Da incassare", ("416.62", "15.38", "432.00"));
        AssertTheGuestGotTheDocument(world, document, await world.GuestEmailOfAsync(booking), "Fattura");

        var month = await world.CloseMonthAsync(2026, 10, EndOfOctober);
        await AssertMonthAsync(
            world,
            month,
            new Expected(
                Gross: 420.00m,
                ChannelFees: 0m,
                Commission: -84.00m,
                Vat: -13.28m,
                Recharges: -80.00m,
                Closing: 242.72m,
                InvoiceLines: [(FiscalLineType.Commission, 84.00m, 8.1m, 6.80m), (FiscalLineType.Recharge, 80.00m, 8.1m, 6.48m)],
                InvoiceTotals: (164.00m, 13.28m, 177.28m)));
        var ledger = await world.LedgerOfAsync(2026, 10);
        Assert.Equal(new DateOnly(2026, 10, 1), Assert.Single(ledger, e => e.EntryType == OwnerLedgerEntryType.BookingRevenue).RecognitionDate);
        Assert.DoesNotContain(ledger, e => e.EntryType == OwnerLedgerEntryType.OwnerExpense);

        AssertPackage(
            await world.PackageAsync(October),
            guestDocuments: 1,
            vatRows: ["2026-Q4;8,10;164,00;13,28;CHF;1", "2026-Q4;3,80;404,62;15,38;CHF;1"],
            portalCommissionRow: null);
        await AssertPayoutAndWireAsync(world, month.Published, 242.72m);
    }

    // Origine 4, prenotazione inserita a mano: pagamento in contanti registrato, documento CHF, rendiconto, fattura PM, pacchetto e bonifico.
    [Fact]
    [Trait("Scenario", "B7 B8a B8b C1 C3 C4 C6 C7 C8")]
    public async Task Origin_Manual_PaidInCash_FromTheBookingToThePackageAndTheWire()
    {
        await using var world = await World.CreateAsync();
        world.At(World.InRome(Day(10, 10), 10));
        var booking = await world.EnterByHandAsync(Day(10, 23), Day(10, 26), price: 300m, touristTax: 12m);
        await world.GiveInvoiceDataAsync(booking, "TEST Ospite Telefono");
        world.At(World.InRome(Day(10, 23), 16));
        await world.RecordPaymentByHandAsync(booking, 312m, PaymentMethod.CashOnArrival, Day(10, 23));

        var document = await IssuedAfterTheStayAsync(world, booking, Day(10, 27), "2026/1");
        AssertDocument(
            document,
            [(FiscalLineType.Accommodation, 289.02m, 3.8m, 10.98m), (FiscalLineType.TouristTax, 12.00m, 0m, 0m)],
            (301.02m, 10.98m, 312.00m),
            FiscalPaymentChannel.Outside,
            FiscalDocumentPaymentState.Paid);
        await AssertGuestPdfAsync(world, document, "Pagata", ("301.02", "10.98", "312.00"));
        AssertTheGuestGotTheDocument(world, document, await world.GuestEmailOfAsync(booking), "Fattura");

        var month = await world.CloseMonthAsync(2026, 10, EndOfOctober);
        await AssertMonthAsync(
            world,
            month,
            new Expected(
                Gross: 300.00m,
                ChannelFees: 0m,
                Commission: -60.00m,
                Vat: -11.34m,
                Recharges: -80.00m,
                Closing: 148.66m,
                InvoiceLines: [(FiscalLineType.Commission, 60.00m, 8.1m, 4.86m), (FiscalLineType.Recharge, 80.00m, 8.1m, 6.48m)],
                InvoiceTotals: (140.00m, 11.34m, 151.34m)));

        AssertPackage(
            await world.PackageAsync(October),
            guestDocuments: 1,
            vatRows: ["2026-Q4;8,10;140,00;11,34;CHF;1", "2026-Q4;3,80;289,02;10,98;CHF;1"],
            portalCommissionRow: null);
        await AssertPayoutAndWireAsync(world, month.Published, 148.66m);
    }

    [Fact]
    [Trait("Scenario", "A1 A2 B7 B8a B8b C1 C2 C3 C4 C6 C7 C8")]
    public async Task MainPath_TheFourOriginsInOneMonth_UpToThePackageAndTheWire()
    {
        await using var world = await World.CreateAsync();
        await RunTheMainPathAsync(world);
    }

    [PostgresFact]
    [Trait("Scenario", "A1 A2 B7 B8a B8b C1 C2 C3 C4 C6 C7 C8")]
    public async Task MainPath_TheFourOriginsInOneMonth_UpToThePackageAndTheWire_Postgres()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("acceptance");
        await using var world = await World.CreateAsync(options: database.CreateOptions());
        await RunTheMainPathAsync(world);
    }

    private static async Task RunTheMainPathAsync(World world)
    {
        world.At(World.InRome(Day(9, 20), 10));
        var portal = await world.ImportFromChannelManagerAsync(world.BookingComReservation(Day(10, 2), Day(10, 5), 450m, commission: 67.50m));
        world.At(World.InRome(Day(9, 25), 10));
        var site = await world.ImportFromChannelManagerAsync(world.AgencySiteReservation(Day(10, 9), Day(10, 12), 390m, touristTax: 12m));
        await world.GiveInvoiceDataAsync(site, "TEST Ospite Sito");
        world.At(World.InRome(Day(10, 1), 12));
        var (showcase, _) = await world.PayOnTheCasaZenSiteAsync(Day(10, 16), Day(10, 19), price: 420m, cleaningFee: 60m, providerFee: 12.48m);
        await world.GiveInvoiceDataAsync(showcase, "TEST Ospite Vetrina");

        var portalDocument = await IssuedAfterTheStayAsync(world, portal, Day(10, 6), "2026/1");
        AssertRecipientIsTheGuestByName(portalDocument, "TEST Ospite Portale");

        world.At(World.InRome(Day(10, 8), 9));
        await world.RecordPaymentByHandAsync(site, 402m, PaymentMethod.BankTransfer, Day(10, 8));
        world.At(World.InRome(Day(10, 10), 10));
        var manual = await world.EnterByHandAsync(Day(10, 23), Day(10, 26), price: 300m, touristTax: 12m);
        await world.GiveInvoiceDataAsync(manual, "TEST Ospite Telefono");

        var siteDocument = await IssuedAfterTheStayAsync(world, site, Day(10, 13), "2026/2");
        var showcaseDocument = await IssuedAfterTheStayAsync(world, showcase, Day(10, 20), "2026/3");
        world.At(World.InRome(Day(10, 23), 16));
        await world.RecordPaymentByHandAsync(manual, 312m, PaymentMethod.CashOnArrival, Day(10, 23));
        var manualDocument = await IssuedAfterTheStayAsync(world, manual, Day(10, 27), "2026/4");

        AssertDocument(
            portalDocument,
            [(FiscalLineType.Accommodation, 433.53m, 3.8m, 16.47m), (FiscalLineType.TouristTax, 12.00m, 0m, 0m)],
            (445.53m, 16.47m, 462.00m),
            FiscalPaymentChannel.Portal,
            FiscalDocumentPaymentState.Paid);
        AssertDocument(
            siteDocument,
            [(FiscalLineType.Accommodation, 375.72m, 3.8m, 14.28m), (FiscalLineType.TouristTax, 12.00m, 0m, 0m)],
            (387.72m, 14.28m, 402.00m),
            FiscalPaymentChannel.Outside,
            FiscalDocumentPaymentState.Paid);
        AssertDocument(
            showcaseDocument,
            [(FiscalLineType.Accommodation, 346.82m, 3.8m, 13.18m), (FiscalLineType.Cleaning, 57.80m, 3.8m, 2.20m), (FiscalLineType.TouristTax, 12.00m, 0m, 0m)],
            (416.62m, 15.38m, 432.00m),
            FiscalPaymentChannel.CasaZen,
            FiscalDocumentPaymentState.ToCollect);
        AssertDocument(
            manualDocument,
            [(FiscalLineType.Accommodation, 289.02m, 3.8m, 10.98m), (FiscalLineType.TouristTax, 12.00m, 0m, 0m)],
            (301.02m, 10.98m, 312.00m),
            FiscalPaymentChannel.Outside,
            FiscalDocumentPaymentState.Paid);
        foreach (var (booking, document) in new[] { (portal, portalDocument), (site, siteDocument), (showcase, showcaseDocument), (manual, manualDocument) })
            AssertTheGuestGotTheDocument(world, document, await world.GuestEmailOfAsync(booking), "Fattura");

        var month = await world.CloseMonthAsync(2026, 10, EndOfOctober);
        await AssertMonthAsync(
            world,
            month,
            new Expected(
                Gross: 1560.00m,
                ChannelFees: -67.50m,
                Commission: -312.00m,
                Vat: -51.19m,
                Recharges: -320.00m,
                Closing: 809.31m,
                InvoiceLines:
                [
                    (FiscalLineType.Commission, 312.00m, 8.1m, 25.27m),
                    (FiscalLineType.Recharge, 80.00m, 8.1m, 6.48m),
                    (FiscalLineType.Recharge, 80.00m, 8.1m, 6.48m),
                    (FiscalLineType.Recharge, 80.00m, 8.1m, 6.48m),
                    (FiscalLineType.Recharge, 80.00m, 8.1m, 6.48m),
                ],
                InvoiceTotals: (632.00m, 51.19m, 683.19m)));
        var statement = await world.StatementOfAsync(month.Published.Id);
        Assert.Equal(new[] { 300.00m, 390.00m, 420.00m, 450.00m }, statement.Of(OwnerStatementSection.Stays).Select(l => l.Amount).Order().ToArray());

        var summary = await world.AgencySummaryAsync(October);
        Assert.True(summary.OwnerAccountingIncluded);
        var commission = Assert.Single(summary.Commissions);
        Assert.Equal((world.OwnerId, "CHF", 1), (commission.OwnerId, commission.Currency, commission.Documents));
        Assert.Equal(new AgencyAmounts(312.00m, 25.27m, 337.27m), commission.Commission);
        Assert.Equal(new AgencyAmounts(320.00m, 25.92m, 345.92m), commission.Recharges);
        Assert.Equal(683.19m, commission.InvoicedTotal);
        Assert.Equal(new AgencyOwnerInvoiceCount("CHF", 1, 0), Assert.Single(summary.OwnerInvoices.Counts));
        var swissTax = Assert.Single(summary.TouristTax.Switzerland);
        Assert.Equal(("TI", "CHF", 4, 48.00m), (swissTax.Canton, swissTax.Currency, swissTax.Documents, swissTax.Collected));

        AssertPackage(
            await world.PackageAsync(October),
            guestDocuments: 4,
            vatRows: ["2026-Q4;8,10;632,00;51,19;CHF;1", "2026-Q4;3,80;1502,89;57,11;CHF;4"],
            portalCommissionRow: null);
        await AssertPayoutAndWireAsync(world, month.Published, 809.31m);
    }

    // Variante V2 (⚖ I10 I11): l'account Booking.com dell'appartamento e' dell'agenzia: commissione dalla regola del canale, riaddebito nella fattura PM,
    // informativa per l'imposta sull'acquisto, e il portale del proprietario letto come lo legge il proprietario.
    [Fact]
    [Trait("Scenario", "C2 C4 C8")]
    public async Task Variant_V2_TheAgencyHoldsTheBookingAccount_RechargeOnThePmInvoice_AndPurchaseTaxInformation()
    {
        await using var world = await World.CreateAsync(agencyHoldsTheBookingAccount: true);
        world.At(World.InRome(Day(9, 20), 10));

        var booking = await world.ImportFromChannelManagerAsync(world.BookingComReservation(Day(10, 2), Day(10, 5), 450m, commission: null));
        await world.GiveInvoiceDataAsync(booking, "TEST Ospite Portale");
        await IssuedAfterTheStayAsync(world, booking, Day(10, 6), "2026/1");

        var month = await world.CloseMonthAsync(2026, 10, EndOfOctober);
        await AssertMonthAsync(
            world,
            month,
            new Expected(
                Gross: 450.00m,
                ChannelFees: -67.50m,
                Commission: -90.00m,
                Vat: -19.24m,
                Recharges: -80.00m,
                Closing: 193.26m,
                InvoiceLines:
                [
                    (FiscalLineType.Commission, 90.00m, 8.1m, 7.29m),
                    (FiscalLineType.Recharge, 80.00m, 8.1m, 6.48m),
                    (FiscalLineType.Recharge, 67.50m, 8.1m, 5.47m),
                ],
                InvoiceTotals: (237.50m, 19.24m, 256.74m)));
        var fee = Assert.Single(await world.LedgerOfAsync(2026, 10), e => e.EntryType == OwnerLedgerEntryType.ChannelFee);
        Assert.Equal(-67.50m, fee.Amount);
        Assert.Equal(ChannelFeeSources.Rule(world.BookingComRuleId), fee.RuleVersion);
        Assert.StartsWith("Riaddebito commissioni Booking.com", month.Invoice.Lines.Single(l => l.TotalPrice == 67.50m).Description, StringComparison.Ordinal);

        var statement = await world.StatementOfAsync(month.Published.Id);
        Assert.True(Assert.Single(statement.Of(OwnerStatementSection.ChannelFees)).AgencyHoldsPortalAccount);
        var text = StatementTextOf(statement);
        Assert.Contains("Commissioni del portale riaddebitate dal gestore (fattura n. PM2026/1)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Trattenuta dal portale", text, StringComparison.Ordinal);

        var detail = OwnerPortalStatementDetailDto.From(await world.PortalStatementAsTheOwnerAsync(month.Published.Id));
        var portalFee = Assert.Single(detail.Sections.Single(s => s.Section == "channelFees").Lines);
        Assert.Equal((-67.50m, true), (portalFee.Amount, portalFee.ChannelFeeRechargedByAgency));
        Assert.Equal("PM2026/1", detail.OwnerInvoiceNumber);

        // The VAT file of the agency: the commission of the portal it holds the account of, for information (a possible base of the purchase tax, never VAT due).
        AssertPackage(
            await world.PackageAsync(October),
            guestDocuments: 1,
            vatRows: ["2026-Q4;8,10;237,50;19,24;CHF;1", "2026-Q4;3,80;433,53;16,47;CHF;1"],
            portalCommissionRow: "Booking.com;67,50;CHF");
    }

    // Variante V2 senza riaddebito (⚖ I10 I11, decisione 1 del 20/11/2026, FIX/BE-V2-NO-RECHARGE): l'account Booking.com e' dell'agenzia che non riaddebita le commissioni:
    // la commissione e' a carico dell'agenzia e il proprietario non la paga (chiusura 266.23 invece di 198.73), nessun riaddebito nella fattura PM, il rendiconto la mostra
    // solo a titolo informativo con un'etichetta propria e non cita la fattura, il pacchetto la dice a carico dell'agenzia.
    [Fact]
    [Trait("Scenario", "C2 C4 C6 C8")]
    public async Task Variant_V2_WithoutTheRecharge_NoRechargeOnThePmInvoice_AndTheStatementNeverNamesItForTheCommission()
    {
        await using var world = await World.CreateAsync(agencyHoldsTheBookingAccount: true, rechargePortalCommissions: false);
        world.At(World.InRome(Day(9, 20), 10));
        var booking = await world.ImportFromChannelManagerAsync(world.BookingComReservation(Day(10, 2), Day(10, 5), 450m, commission: null));
        await world.GiveInvoiceDataAsync(booking, "TEST Ospite Portale");
        await IssuedAfterTheStayAsync(world, booking, Day(10, 6), "2026/1");

        var month = await world.CloseMonthAsync(2026, 10, EndOfOctober);
        await AssertMonthAsync(
            world,
            month,
            new Expected(
                Gross: 450.00m,
                ChannelFees: 0m,
                Commission: -90.00m,
                Vat: -13.77m,
                Recharges: -80.00m,
                Closing: 266.23m,
                InvoiceLines: [(FiscalLineType.Commission, 90.00m, 8.1m, 7.29m), (FiscalLineType.Recharge, 80.00m, 8.1m, 6.48m)],
                InvoiceTotals: (170.00m, 13.77m, 183.77m)));

        var ledger = await world.LedgerOfAsync(2026, 10);
        Assert.DoesNotContain(ledger, e => e.EntryType == OwnerLedgerEntryType.ChannelFee);
        var borne = Assert.Single(ledger, e => e.EntryType == OwnerLedgerEntryType.ChannelFeeBorneByAgency);
        Assert.Equal((-67.50m, (Guid?)booking, ChannelFeeSources.Rule(world.BookingComRuleId)), (borne.Amount, borne.SourceId, borne.RuleVersion));

        var statement = await world.StatementOfAsync(month.Published.Id);
        var line = Assert.Single(statement.Of(OwnerStatementSection.ChannelFees));
        Assert.False(line.AgencyHoldsPortalAccount);
        Assert.DoesNotContain("riaddebitate dal gestore", StatementTextOf(statement), StringComparison.Ordinal);
        var detail = OwnerPortalStatementDetailDto.From(await world.PortalStatementAsTheOwnerAsync(month.Published.Id));
        var portalPart = detail.Sections.Single(s => s.Section == "channelFees");
        Assert.False(Assert.Single(portalPart.Lines).ChannelFeeRechargedByAgency);

        Assert.Equal((true, true, -67.50m), (line.AgencyHoldsPortalAccountWithoutRecharge, line.Informational, line.Amount));
        var text = StatementTextOf(statement);
        Assert.Contains("Commissione del portale sostenuta dall'agenzia, non addebitata al proprietario", text, StringComparison.Ordinal);
        Assert.Contains("67.50", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-67.50", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Trattenuta dal portale", text, StringComparison.Ordinal);
        Assert.DoesNotContain("fattura del portale a tuo nome", text, StringComparison.Ordinal);
        Assert.True(Assert.Single(portalPart.Lines).ChannelFeeBilledToAgencyNotRecharged);
        Assert.Equal(0m, portalPart.Total);

        // C8: the package says the agency bears it (and the VAT file of the agency still has the commission of the account it holds); C6: the owner is paid the net without it.
        var package = await world.PackageAsync(October);
        AssertPackage(
            package,
            guestDocuments: 1,
            vatRows: ["2026-Q4;8,10;170,00;13,77;CHF;1", "2026-Q4;3,80;433,53;16,47;CHF;1"],
            portalCommissionRow: "Booking.com;67,50;CHF");
        var commissions = Text(package[AccountantPackageStructure.OwnerInvoicesFolder + "/" + AccountantPackageStructure.PortalCommissionsFile + ".csv"]);
        Assert.Contains("A carico dell'agenzia", commissions, StringComparison.Ordinal);
        Assert.DoesNotContain("Riaddebitata al proprietario", commissions, StringComparison.Ordinal);
        await AssertPayoutAndWireAsync(world, month.Published, 266.23m);
    }

    [Fact]
    [Trait("Scenario", "B7 B8a B8b C1 C3 C4 C7")]
    public async Task Variant_ThePortalCollectsTheTouristTax_Airbnb_NoTaxLineOnTheDocument_NorInTheStatementBox()
    {
        await using var world = await World.CreateAsync();
        world.At(World.InRome(Day(9, 20), 10));
        var booking = await world.ImportFromChannelManagerAsync(world.AirbnbReservation(Day(10, 2), Day(10, 5), 450m, commission: 69.75m, touristTax: 12m));

        var stored = await world.BookingAsync(booking);
        Assert.Equal(
            (BookingSource.Airbnb, 450.00m, 462.00m, 12.00m, (decimal?)69.75m, (bool?)true),
            (stored.Source, stored.BasePrice, stored.TotalPrice, stored.TouristTax, stored.OtaCommission, stored.OtaCompanyCollect));
        await world.GiveInvoiceDataAsync(booking, "TEST Ospite Lago");

        var document = await IssuedAfterTheStayAsync(world, booking, Day(10, 6), "2026/1");
        AssertDocument(
            document,
            [(FiscalLineType.Accommodation, 433.53m, 3.8m, 16.47m)],
            (433.53m, 16.47m, 450.00m),
            FiscalPaymentChannel.Portal,
            FiscalDocumentPaymentState.Paid);
        var text = TextOf(await world.PdfContentAsync(document.Id));
        Assert.DoesNotContain("Tassa di soggiorno", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Airbnb", text, StringComparison.OrdinalIgnoreCase);
        AssertTheGuestGotTheDocument(world, document, await world.GuestEmailOfAsync(booking), "Fattura");

        var month = await world.CloseMonthAsync(2026, 10, EndOfOctober);
        await AssertMonthAsync(
            world,
            month,
            new Expected(
                Gross: 450.00m,
                ChannelFees: -69.75m,
                Commission: -90.00m,
                Vat: -13.77m,
                Recharges: -80.00m,
                Closing: 196.48m,
                InvoiceLines: [(FiscalLineType.Commission, 90.00m, 8.1m, 7.29m), (FiscalLineType.Recharge, 80.00m, 8.1m, 6.48m)],
                InvoiceTotals: (170.00m, 13.77m, 183.77m)));
        Assert.Empty((await world.StatementOfAsync(month.Published.Id)).TouristTax);
        Assert.Empty((await world.AgencySummaryAsync(October)).TouristTax.Switzerland);
    }

    // QR-fattura solo con un importo dovuto: un mese con il solo riaddebito di una riparazione, la fattura PM chiede il pagamento con la QR-fattura.
    [Fact]
    [Trait("Scenario", "C4")]
    public async Task QrBill_OnlyWhenTheOwnerOwesTheAgency()
    {
        await using var world = await World.CreateAsync();
        world.At(World.InRome(Day(11, 10), 10));
        await world.ChargeTheOwnerAsync(300m, Day(11, 10), "TEST Riparazione caldaia");

        var month = await world.CloseMonthAsync(2026, 11, World.InRome(Day(11, 30), 16));

        var invoice = month.Invoice;
        Assert.Equal(
            new[] { (FiscalLineType.Recharge, 300.00m, 8.1m, 24.30m) },
            invoice.Lines.OrderBy(l => l.LineNumber).Select(l => (l.LineType, l.TotalPrice, l.VatRatePercent, l.VatAmount ?? 0m)).ToArray());
        Assert.Equal((300.00m, 24.30m, 324.30m), (invoice.TaxableBase, invoice.VatAmount, invoice.Total));
        Assert.Equal(-324.30m, month.Published.ClosingBalance);

        var content = await world.PdfContentAsync(invoice.Id);
        var bill = Assert.IsType<PdfSwissQrBill>(content.QrBill);
        var fields = bill.Payload.Split('\n');
        Assert.Equal(("SPC", "0200", "1", World.AgencyQrIban), (fields[0], fields[1], fields[2], fields[3]));
        Assert.Equal(("324.30", "CHF"), (fields[18], fields[19]));
        Assert.Equal(("S", World.OwnerName, "Via Cantonale", "12", "6900", "Lugano", "CH"), (fields[20], fields[21], fields[22], fields[23], fields[24], fields[25], fields[26]));
        Assert.Equal(SwissQrBill.ReferenceQrr, fields[27]);
        Assert.True(SwissQrBill.IsValidQrr(fields[28]));
        Assert.Equal("Fattura PM2026/1", fields[29]);
        Assert.Contains("Importo da pagare: CHF 324.30", TextOf(content), StringComparison.Ordinal);
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(await world.PdfAsync(invoice.Id));
        Assert.Equal(2, pdf.NumberOfPages);
    }

    [Fact]
    [Trait("Scenario", "B12")]
    public async Task Change_DateChange_FullCreditNoteAndNewDocument_AndTheStatementOfTheNewStay()
    {
        await using var world = await World.CreateAsync();
        world.At(World.InRome(Day(10, 10), 10));
        var booking = await world.EnterByHandAsync(Day(10, 23), Day(10, 26), price: 300m, touristTax: 12m);
        await world.GiveInvoiceDataAsync(booking, "TEST Ospite Telefono");
        world.At(World.InRome(Day(10, 23), 16));
        await world.RecordPaymentByHandAsync(booking, 312m, PaymentMethod.CashOnArrival, Day(10, 23));
        var original = await IssuedAfterTheStayAsync(world, booking, Day(10, 27), "2026/1");

        world.At(World.InRome(Day(10, 28), 11));
        await world.ChangeAsHostAsync(booking, b =>
        {
            b.CheckOutDate = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc);
            b.BasePrice = 500m;
            b.TouristTax = 20m;
            b.TouristTaxAmount = 20m;
            b.TotalPrice = 520m;
        });
        await world.RecordPaymentByHandAsync(booking, 208m, PaymentMethod.CashOnArrival, Day(10, 28));
        await world.DeliverEventsAsync();

        var documents = await world.DocumentsOfAsync(booking);
        Assert.Equal(3, documents.Count);
        var (stored, note, replacement) = (documents[0], documents[1], documents[2]);
        Assert.Equal((original.Id, true), (stored.Id, stored.CreditedInFull));
        Assert.Equal((DocumentKind.CreditNote, FiscalDocumentStatus.Issued, (Guid?)original.Id, 312.00m), (note.Kind, note.Status, note.CreditedDocumentId, note.Total));
        Assert.Equal("2026/2", note.Number);
        Assert.Equal(LinesOf(stored), LinesOf(note));
        Assert.Equal((DocumentKind.Invoice, FiscalDocumentStatus.Issued), (replacement.Kind, replacement.Status));
        Assert.Equal("2026/3", replacement.Number);
        AssertDocument(
            replacement,
            [(FiscalLineType.Accommodation, 481.70m, 3.8m, 18.30m), (FiscalLineType.TouristTax, 20.00m, 0m, 0m)],
            (501.70m, 18.30m, 520.00m),
            FiscalPaymentChannel.Outside,
            FiscalDocumentPaymentState.Paid);

        var email = await world.GuestEmailOfAsync(booking);
        foreach (var number in new[] { "2026/1", "2026/2", "2026/3" })
            Assert.Single(world.Emails.Queued, m => m.Template == EmailTemplates.Names.FiscalDocumentDelivery && m.To == email && m.Content.Subject.Contains(number, StringComparison.Ordinal));

        var month = await world.CloseMonthAsync(2026, 10, EndOfOctober);
        await AssertMonthAsync(
            world,
            month,
            new Expected(
                Gross: 500.00m,
                ChannelFees: 0m,
                Commission: -100.00m,
                Vat: -14.58m,
                Recharges: -80.00m,
                Closing: 305.42m,
                InvoiceLines: [(FiscalLineType.Commission, 100.00m, 8.1m, 8.10m), (FiscalLineType.Recharge, 80.00m, 8.1m, 6.48m)],
                InvoiceTotals: (180.00m, 14.58m, 194.58m)));
        var revenue = (await world.LedgerOfAsync(2026, 10)).Where(e => e.EntryType == OwnerLedgerEntryType.BookingRevenue).Select(e => e.Amount).Order().ToArray();
        Assert.Equal(new[] { 200.00m, 300.00m }, revenue);
    }

    [Fact]
    [Trait("Scenario", "B12")]
    public async Task Change_CancellationWithRefund_FullCreditNote_AndNothingInTheLedger()
    {
        await using var world = await World.CreateAsync();
        world.At(World.InRome(Day(10, 1), 12));
        var (booking, payment) = await world.PayOnTheCasaZenSiteAsync(Day(10, 16), Day(10, 19), price: 420m, cleaningFee: 60m, providerFee: 12.48m);
        await world.GiveInvoiceDataAsync(booking, "TEST Ospite Vetrina");

        var original = await world.IssueNowAsync(booking);
        Assert.Equal((DocumentKind.Invoice, 432.00m, FiscalPaymentChannel.CasaZen), (original.Kind, original.Total, original.PaymentChannel));
        Assert.Equal("2026/1", original.Number);

        world.At(World.InRome(Day(10, 5), 10));
        await world.CancelAsHostAsync(booking);
        var refund = await world.RefundThroughStripeAsync(payment, amount: null);
        Assert.Equal((PaymentRefundStatus.Succeeded, 420.00m), (refund.Status, refund.Amount));
        Assert.Equal(PaymentStatus.Refunded, (await world.PaymentAsync(payment)).Status);
        await world.DeliverEventsAsync();

        var documents = await world.DocumentsOfAsync(booking);
        var note = Assert.Single(documents, d => d.Kind == DocumentKind.CreditNote);
        Assert.Equal((FiscalDocumentStatus.Issued, (Guid?)original.Id, 432.00m), (note.Status, note.CreditedDocumentId, note.Total));
        Assert.Equal("2026/2", note.Number);
        Assert.Equal(LinesOf(documents.Single(d => d.Id == original.Id)), LinesOf(note));
        Assert.True(documents.Single(d => d.Id == original.Id).CreditedInFull);
        var email = await world.GuestEmailOfAsync(booking);
        Assert.Single(world.Emails.Queued, m => m.Template == EmailTemplates.Names.FiscalDocumentDelivery && m.To == email && m.Content.Subject.Contains("2026/2", StringComparison.Ordinal));

        var posting = await world.PostMonthAsync(2026, 10, EndOfOctober);
        Assert.DoesNotContain(posting.Issues, i => i.Blocking);
        Assert.Empty(await world.LedgerOfAsync(2026, 10));
    }

    // B12 no-show: nota di credito della differenza fra il documento e l'acconto trattenuto, IVA in proporzione; nel libro solo l'acconto, senza forfait.
    [Fact]
    [Trait("Scenario", "B12")]
    public async Task Change_NoShow_CreditNoteOfTheDifference_AndOnlyTheDepositInTheLedger()
    {
        await using var world = await World.CreateAsync();
        world.At(World.InRome(Day(9, 25), 10));
        var booking = await world.ImportFromChannelManagerAsync(world.AgencySiteReservation(Day(10, 9), Day(10, 12), 390m, touristTax: 12m));
        await world.GiveInvoiceDataAsync(booking, "TEST Ospite Sito");
        world.At(World.InRome(Day(10, 1), 9));
        await world.RecordPaymentByHandAsync(booking, 100m, PaymentMethod.BankTransfer, Day(10, 1));

        var original = await IssuedAfterTheStayAsync(world, booking, Day(10, 13), "2026/1");
        AssertDocument(
            original,
            [(FiscalLineType.Accommodation, 375.72m, 3.8m, 14.28m), (FiscalLineType.TouristTax, 12.00m, 0m, 0m)],
            (387.72m, 14.28m, 402.00m),
            FiscalPaymentChannel.Outside,
            FiscalDocumentPaymentState.ToCollect);

        world.At(World.InRome(Day(10, 13), 10));
        await world.MarkNoShowAsync(booking);
        await world.DeliverEventsAsync();

        // ⚖ I12: the consideration is what the guest pays (the deposit kept): a credit note of 302.00, the VAT split in proportion to the lines of the document.
        var note = Assert.Single(await world.DocumentsOfAsync(booking), d => d.Kind == DocumentKind.CreditNote);
        Assert.Equal((FiscalDocumentStatus.Issued, (Guid?)original.Id, 302.00m), (note.Status, note.CreditedDocumentId, note.Total));
        Assert.Equal("2026/2", note.Number);
        Assert.Equal(new[] { (FiscalLineType.Accommodation, 282.26m, 3.8m, 10.73m), (FiscalLineType.TouristTax, 9.01m, 0m, 0m) }, LinesOf(note));
        Assert.Equal((291.27m, 10.73m), (note.TaxableBase, note.VatAmount));
        Assert.All(note.Lines, l => Assert.Contains("no-show", l.Description, StringComparison.Ordinal));

        var month = await world.CloseMonthAsync(2026, 10, EndOfOctober);
        await AssertMonthAsync(
            world,
            month,
            new Expected(
                Gross: 100.00m,
                ChannelFees: 0m,
                Commission: -20.00m,
                Vat: -1.62m,
                Recharges: 0m,
                Closing: 78.38m,
                InvoiceLines: [(FiscalLineType.Commission, 20.00m, 8.1m, 1.62m)],
                InvoiceTotals: (20.00m, 1.62m, 21.62m)));
        Assert.Equal(OwnerLedgerSourceType.Payment, Assert.Single(await world.LedgerOfAsync(2026, 10), e => e.EntryType == OwnerLedgerEntryType.BookingRevenue).SourceType);
        Assert.Empty((await world.StatementOfAsync(month.Published.Id)).TouristTax);
    }

    [SwissWireSchemaFact]
    [Trait("Scenario", "C6")]
    public async Task Wire_ThePayoutFile_ValidatesAgainstTheOfficialSwissSchema()
    {
        await using var world = await World.CreateAsync();
        world.At(World.InRome(Day(10, 10), 10));
        var booking = await world.EnterByHandAsync(Day(10, 23), Day(10, 26), price: 300m, touristTax: 12m);
        world.At(World.InRome(Day(10, 23), 16));
        await world.RecordPaymentByHandAsync(booking, 312m, PaymentMethod.CashOnArrival, Day(10, 23));
        var month = await world.CloseMonthAsync(2026, 10, EndOfOctober);
        world.At(World.InRome(PayDay, 10));
        var payout = await world.PayTheOwnerAsync(month.Published, PayDay);

        var (batch, xml) = await world.WireAsync([payout.Id], PayDay.AddDays(1), schemas: null);

        Assert.Equal((148.66m, 1), (batch.TotalAmount, batch.TransferCount));
        Assert.Equal(XNamespace.Get(Pain001SchemaProvider.SwissNamespace), XDocument.Parse(xml).Root!.Name.Namespace);
    }

    private static async Task<FiscalDocument> IssuedAfterTheStayAsync(World world, Guid booking, DateOnly day, string number)
    {
        var run = await world.RunDocumentsDueAsync(day);
        Assert.True(run.Ran, run.NotRunReason);
        Assert.Equal((1, 1, 0), (run.DraftsCreated, run.DocumentsIssued, run.Skipped));

        var document = Assert.Single(await world.DocumentsOfAsync(booking));
        Assert.Equal((DocumentKind.Invoice, FiscalDocumentStatus.Issued, (DateOnly?)day), (document.Kind, document.Status, document.IssueDate));
        Assert.Equal(number, document.Number);
        return document;
    }

    private static void AssertRecipientIsTheGuestByName(FiscalDocument document, string name)
    {
        Assert.Equal((InvoiceRecipientKind.Guest, name), (document.RecipientKind, document.RecipientName));
        var rest = new[]
        {
            document.RecipientFiscalCode,
            document.RecipientVatNumber,
            document.RecipientAddressStreet,
            document.RecipientAddressCivic,
            document.RecipientPostalCode,
            document.RecipientCity,
            document.RecipientProvince,
            document.RecipientCountryCode,
            document.RecipientSdiCode,
            document.RecipientPec,
        };
        Assert.All(rest, value => Assert.Null(value));
    }

    /// <summary>A Swiss document of a stay: its lines (net, rate, VAT), its totals, who collected and the state of the payment, to the cent; no XML, nothing for the SDI.</summary>
    private static void AssertDocument(
        FiscalDocument document,
        (FiscalLineType Type, decimal Net, decimal Rate, decimal Vat)[] lines,
        (decimal Taxable, decimal Vat, decimal Total) totals,
        FiscalPaymentChannel channel,
        FiscalDocumentPaymentState state)
    {
        Assert.Equal(FiscalDocumentPurpose.GuestStay, document.Purpose);
        Assert.Equal("CHF", document.Currency);
        Assert.Equal("ch-vat-2024.1", document.RuleVersion);
        Assert.Null(document.FatturaPaXml);
        Assert.Equal(FiscalDocumentSdiStatuses.NotApplicable, document.SdiStatus);
        Assert.Equal(lines, LinesOf(document));
        Assert.Equal(totals, (document.TaxableBase, document.VatAmount, document.Total));
        Assert.Equal((channel, state), (document.PaymentChannel, document.PaymentState));
    }

    /// <summary>The Swiss PDF of the document: the title with the number, the issuer with its UID, the tourist tax outside the VAT, the payment, the totals; never a portal.</summary>
    private static async Task AssertGuestPdfAsync(World world, FiscalDocument document, string payment, (string Taxable, string Vat, string Total) totals)
    {
        var content = await world.PdfContentAsync(document.Id);
        Assert.Equal($"Fattura n. {document.Number}", content.Title);
        var text = TextOf(content);
        Assert.Contains(World.AgencyLegalName, text, StringComparison.Ordinal);
        Assert.Contains($"N. IVA {World.AgencyUid} IVA", text, StringComparison.Ordinal);
        Assert.Contains("Tassa di soggiorno (non soggetta all'IVA)", text, StringComparison.Ordinal);
        Assert.Contains(payment, text, StringComparison.Ordinal);
        var summary = Assert.Single(content.Blocks.OfType<PdfTable>(), t => t.Columns.Count == 2);
        Assert.Equal(new[] { totals.Taxable, totals.Vat, totals.Total }, summary.Rows.Select(r => r[1]).ToArray());
        foreach (var name in new[] { "Booking", "Octorate", "Stripe" })
            Assert.DoesNotContain(name, text, StringComparison.OrdinalIgnoreCase);

        var pdf = await world.PdfAsync(document.Id);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
    }

    private static void AssertTheGuestGotTheDocument(World world, FiscalDocument document, string guestEmail, string kind)
    {
        var mail = Assert.Single(
            world.Emails.Queued,
            m => m.Template == EmailTemplates.Names.FiscalDocumentDelivery && m.Content.Subject.EndsWith($"n. {document.Number} di {World.AgencyLegalName}", StringComparison.Ordinal));
        Assert.Equal(guestEmail, mail.To);
        Assert.Equal($"{kind} n. {document.Number} di {World.AgencyLegalName}", mail.Content.Subject);
        var body = WebUtility.HtmlDecode(mail.Content.HtmlBody);
        Assert.Contains($"{World.PublicSiteBaseUrl}/documents/view/{document.Id:D}?token=", body, StringComparison.Ordinal);
        Assert.DoesNotContain(document.Total.ToString("0.00", CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
        Assert.DoesNotContain(document.Total.ToString("0.00", CultureInfo.GetCultureInfo("it-IT")), body, StringComparison.Ordinal);
    }

    /// <summary>What the month of the owner must say (the amounts of the ledger are negative for what is taken from the owner).</summary>
    /// <param name="Commission">The commission of the agency, without its VAT.</param>
    /// <param name="Vat">The VAT of the invoice of the property manager, posted to the ledger.</param>
    private sealed record Expected(
        decimal Gross,
        decimal ChannelFees,
        decimal Commission,
        decimal Vat,
        decimal Recharges,
        decimal Closing,
        (FiscalLineType Type, decimal Net, decimal Rate, decimal Vat)[] InvoiceLines,
        (decimal Taxable, decimal Vat, decimal Total) InvoiceTotals);

    /// <summary>
    /// C1, C3, C4: the published version (its totals, the lines of the statement), the ledger (the VAT of the invoice posted once), the invoice of the property manager
    /// (its lines at 8.1%, its totals, offset in the statement: no QR-bill), and the owner portal (the statement with its stored PDF, the invoice) with the owner told.
    /// </summary>
    private static async Task AssertMonthAsync(World world, MonthEnd month, Expected expected)
    {
        Assert.DoesNotContain(month.Posting.Issues, i => i.Blocking);

        // The version generated again with the VAT of the invoice, approved and published.
        var published = month.Published;
        Assert.NotEqual(month.FirstVersion.Id, published.Id);
        Assert.Equal((OwnerStatementVersionStatus.Published, 2026, month.FirstVersion.PeriodMonth), (published.Status, published.PeriodYear, published.PeriodMonth));
        Assert.Equal("CHF", published.Currency);
        var totals = OwnerStatementTotals.Of(published);
        Assert.Equal(
            (expected.Gross, expected.ChannelFees, 0m, expected.Commission + expected.Vat, expected.Recharges, 0m, 0m, 0m, expected.Closing),
            (totals.Gross, totals.ChannelFees, totals.Withholding, totals.Commission, totals.Recharges, totals.Corrections, totals.Payments, totals.OpeningBalance, totals.ClosingBalance));

        // The lines of the statement: the stays without the tourist tax, the channel fees the owner bears (a commission the agency bears is a line for information only, in no
        // total: FIX/BE-V2-NO-RECHARGE), the commission with the VAT of its invoice, the recharges.
        var statement = await world.StatementOfAsync(published.Id);
        Assert.Equal(expected.Gross, statement.Of(OwnerStatementSection.Stays).Sum(l => l.Amount));
        Assert.Equal(expected.ChannelFees, statement.Of(OwnerStatementSection.ChannelFees).Where(l => !l.Informational).Sum(l => l.Amount));
        Assert.Equal(expected.Commission + expected.Vat, statement.Of(OwnerStatementSection.Commission).Sum(l => l.Amount));
        Assert.Equal(expected.Recharges, statement.Of(OwnerStatementSection.Recharges).Sum(l => l.Amount));
        Assert.Equal("PM2026/1", statement.OwnerInvoiceNumber);

        // The ledger: the VAT of the invoice posted once, on top of the commission.
        var ledger = await world.LedgerOfAsync(2026, published.PeriodMonth);
        var vat = Assert.Single(ledger, e => e.EntryType == OwnerLedgerEntryType.AgencyVat);
        Assert.Equal((expected.Vat, (Guid?)month.Invoice.Id), (vat.Amount, vat.SourceDocumentId));
        Assert.Equal(expected.Commission, ledger.Where(e => e.EntryType == OwnerLedgerEntryType.AgencyCommission).Sum(e => e.Amount));
        Assert.Equal(expected.Gross, ledger.Where(e => e.EntryType == OwnerLedgerEntryType.BookingRevenue).Sum(e => e.Amount));
        Assert.Equal(expected.Closing, ledger.Where(e => OwnerLedgerBalance.Counts(e.EntryType)).Sum(e => e.Amount));

        var invoice = month.Invoice;
        Assert.Equal((FiscalDocumentPurpose.OwnerInvoice, DocumentKind.Invoice, FiscalDocumentStatus.Issued, (Guid?)world.OwnerId), (invoice.Purpose, invoice.Kind, invoice.Status, invoice.RecipientOwnerId));
        Assert.Equal("PM2026/1", invoice.Number);
        Assert.Equal("CHF", invoice.Currency);
        Assert.Equal("ch-vat-2024.1", invoice.RuleVersion);
        Assert.Null(invoice.FatturaPaXml);
        Assert.Equal(expected.InvoiceLines, LinesOf(invoice));
        Assert.Equal(expected.InvoiceTotals, (invoice.TaxableBase, invoice.VatAmount, invoice.Total));
        Assert.Equal(-expected.Vat, invoice.VatAmount);

        var pdf = await world.PdfContentAsync(invoice.Id);
        Assert.Null(pdf.QrBill);
        Assert.Contains($"Compensata nel rendiconto del {published.PeriodMonth:00}/2026", TextOf(pdf), StringComparison.Ordinal);

        var listed = Assert.Single(await world.PortalStatementsAsync());
        Assert.Equal((published.Id, true, false), (listed.StatementId, listed.HasPdf, listed.Superseded));
        Assert.Equal(totals, listed.Totals);
        var download = await world.PortalStatementPdfAsync(published.Id);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(download, 0, 4));
        Assert.Equal(await world.StoredStatementPdfAsync(published.Id), download);
        var portalInvoice = Assert.Single(await world.PortalInvoicesAsync());
        Assert.Equal((invoice.Id, invoice.Total, "CHF"), (portalInvoice.InvoiceId, portalInvoice.Total, portalInvoice.Currency));
        Assert.Equal("PM2026/1", portalInvoice.Number);
        Assert.Single(world.Emails.Queued, m => m.Template == EmailTemplates.Names.OwnerStatementPublished && m.To == World.OwnerEmail);
    }

    /// <summary>
    /// C7, C8: the index of the ZIP lists every file and no other, with the SHA-256 of its bytes; the documents of the guests, the invoice of the property manager and the
    /// statement are in it, no XML (Swiss documents); the VAT of the issuer for each rate of the quarter and, for a portal account the agency holds, its commission.
    /// </summary>
    private static void AssertPackage(Dictionary<string, byte[]> files, int guestDocuments, IReadOnlyList<string> vatRows, string? portalCommissionRow)
    {
        var index = IndexOf(files[AccountantPackageStructure.IndexFile]);
        Assert.Equal(
            files.Keys.Where(k => k != AccountantPackageStructure.IndexFile).Order(StringComparer.Ordinal),
            index.Select(e => e.Path).Order(StringComparer.Ordinal));
        foreach (var (path, sha256) in index)
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(files[path])), sha256);

        Assert.Equal(guestDocuments, files.Keys.Count(k => k.StartsWith("documenti-ospiti/", StringComparison.Ordinal) && k.EndsWith(".pdf", StringComparison.Ordinal)));
        Assert.Single(files.Keys, k => k.StartsWith("fatture-proprietari/", StringComparison.Ordinal) && k.EndsWith(".pdf", StringComparison.Ordinal));
        Assert.Single(files.Keys, k => k.StartsWith("rendiconti/", StringComparison.Ordinal) && k.EndsWith(".pdf", StringComparison.Ordinal));
        Assert.Single(files.Keys, k => k.StartsWith("rendiconti/", StringComparison.Ordinal) && k.EndsWith(".csv", StringComparison.Ordinal) && k != "rendiconti/elenco-rendiconti.csv");
        Assert.DoesNotContain(files.Keys, k => k.EndsWith(".xml", StringComparison.Ordinal));

        var vat = Text(Assert.Single(files, f => f.Key.StartsWith("iva/iva-", StringComparison.Ordinal) && f.Key.EndsWith(".csv", StringComparison.Ordinal)).Value);
        foreach (var row in vatRows)
            Assert.Contains(row, vat, StringComparison.Ordinal);

        if (portalCommissionRow is null)
            Assert.DoesNotContain("Booking.com;", vat, StringComparison.Ordinal);
        else
            Assert.Contains(portalCommissionRow, vat, StringComparison.Ordinal);
    }

    private static async Task AssertPayoutAndWireAsync(World world, OwnerStatementVersion published, decimal net)
    {
        world.At(World.InRome(PayDay, 10));
        var payout = await world.PayTheOwnerAsync(published, PayDay);
        Assert.Equal((OwnerPayoutStatus.Confirmed, net, 0m, net), (payout.Status, payout.GrossAmount, payout.WithholdingAmount, payout.NetAmount));

        var (batch, xml) = await world.WireAsync([payout.Id], PayDay.AddDays(1), _ => Pain001TestSchemas.Accepting());
        Assert.Equal((net, 1), (batch.TotalAmount, batch.TransferCount));
        Assert.Equal("CHF", batch.Currency);

        XNamespace swiss = Pain001SchemaProvider.SwissNamespace;
        var document = XDocument.Parse(xml);
        Assert.Equal(swiss, document.Root!.Name.Namespace);
        var amount = net.ToString("F2", CultureInfo.InvariantCulture);
        Assert.All(document.Descendants(swiss + "CtrlSum"), sum => Assert.Equal(amount, sum.Value));
        var transfer = Assert.Single(document.Descendants(swiss + "CdtTrfTxInf"));
        var instructed = transfer.Descendants(swiss + "InstdAmt").Single();
        Assert.Equal((amount, "CHF"), (instructed.Value, (string?)instructed.Attribute("Ccy")));
        Assert.Equal(World.OwnerIban, transfer.Descendants(swiss + "CdtrAcct").Single().Descendants(swiss + "IBAN").Single().Value);
        Assert.Equal(World.AgencyIban, document.Descendants(swiss + "DbtrAcct").Single().Descendants(swiss + "IBAN").Single().Value);
        Assert.Equal($"Rendiconto {published.PeriodMonth:00}/2026", transfer.Descendants(swiss + "Ustrd").Single().Value);
    }

    private static (FiscalLineType Type, decimal Net, decimal Rate, decimal Vat)[] LinesOf(FiscalDocument document) =>
        document.Lines.OrderBy(l => l.LineNumber).Select(l => (l.LineType, l.TotalPrice, l.VatRatePercent, l.VatAmount ?? 0m)).ToArray();

    private static DateOnly Day(int month, int day) => new(2026, month, day);

    private static string TextOf(PdfDocumentContent content) =>
        string.Join('\n', new[] { content.Title }.Concat(content.Blocks.SelectMany(TextsOf)));

    private static string StatementTextOf(OwnerStatementContent statement) =>
        string.Join('\n', OwnerStatementLayout.Pdf(statement, new ResxDocumentTexts()).Blocks.SelectMany(TextsOf));

    private static IEnumerable<string> TextsOf(PdfBlock block) => block switch
    {
        PdfHeading heading => [heading.Text],
        PdfParagraph paragraph => [paragraph.Text],
        PdfTable table => table.Columns.Select(c => c.Header).Concat(table.Rows.SelectMany(r => r)),
        _ => [],
    };

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart((char)0xFEFF);

    private static List<(string Path, string Sha256)> IndexOf(byte[] bytes)
    {
        var rows = CsvRows(Text(bytes));
        var entries = new List<(string Path, string Sha256)>();

        foreach (var row in rows.Skip(2))
        {
            if (row.Count == 1 && row[0].Length == 0)
                break;

            entries.Add((row[0], row[3]));
        }

        return entries;
    }

    private static List<List<string>> CsvRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c != '"')
                {
                    cell.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    quoted = true;
                    break;
                case ';':
                    row.Add(cell.ToString());
                    cell.Clear();
                    break;
                case '\r':
                    break;
                case '\n':
                    row.Add(cell.ToString());
                    cell.Clear();
                    rows.Add(row);
                    row = [];
                    break;
                default:
                    cell.Append(c);
                    break;
            }
        }

        return rows;
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SwissWireSchemaFactAttribute : FactAttribute
{
    public SwissWireSchemaFactAttribute()
    {
        if (!Pain001SchemaProvider.HasOfficialSchema(WireTransferErrors.SwissCurrency))
        {
            Skip = "The official schema pain.001.001.09.ch.03.xsd is not in this build (Casazen.Infrastructure/Payments/Schemas/Swiss): "
                + "the wire file is validated against the stand-in schema of the tests only; see docs/runbooks/wire-transfers.md to restore it.";
        }
    }
}
