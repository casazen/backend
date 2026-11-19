using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Web.DTOs.OwnerPortal;
using Casazen.Web.Resources;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;
using World = Casazen.Tests.Integration.Acceptance.AcceptanceScenarioBuilder;

namespace Casazen.Tests.Integration.Acceptance;

/// <summary>
/// E19/F01/US02/T01 (NF-112, NF-114, NF-115, NF-116, NF-117, NF-118; the Italian acceptance scenario of 2026-11-14): the agency of Como of <see cref="AcceptanceScenarioBuilder"/>
/// (ordinary VAT regime, two flats under a mandate with representation, the Booking.com accounts held by the owners, V1, or by the agency, V2) takes bookings from the four
/// origins (Booking.com through the channel manager, its own site through the channel manager, the CasaZen site paid with Stripe, a booking entered by hand) on two flats and
/// each one goes, through the real code, from the booking to the documents of the month and of the year: <b>flat A</b> of a private owner (the receipt in the owner's name with the
/// stamp duty above 77.47 and the tourist tax on its own line, the invoice of the agency for the cleaning it bills the guest, the invoice of the property manager with the VAT
/// of 22% and the cleaning recharged at the cost of its request, the withholding of 21% when the agency collects and the portal did not withhold, the F24 with the code 1919,
/// the CU and the 770, or the communication of 30 June when nothing was withheld), <b>flat B</b> of a company (the FatturaPA with the company as cedente and the agency as
/// third-party issuer, the sezionale of the company, the VAT of 10% or exempt N4, the tourist tax N1, valid against the official schema, «to send» to the SDI with no provider
/// and then declared by hand, no withholding with its reason, the invoice of the agency at 22%, the documents issued in the name of the company in its portal and in its
/// accountant export), and for both the self-billed invoice (TD17) of the company that holds a portal account, none for the private owner, and the tourist tax of the year.
/// Every amount is asserted to the cent.
/// </summary>
/// <remarks>
/// The trait <c>Scenario</c> of each test lists the steps of the scenario it covers (A, B, C, D of <c>pilota-flusso-completo-2026-11-14.md</c>; <c>--filter Scenario~C5</c>
/// selects them). No test has a display name of its own: the planner of the test shards of the CI (<c>scripts/ci/test_shards.py</c>) reads the default name,
/// <c>Namespace.Class.Method</c>. The main path runs on the in-memory database and, as a <see cref="PostgresFactAttribute"/> twin, on PostgreSQL in CI. Flat B's stay ends a day
/// after flat A's in every origin and the daily run of each day makes the documents of its own flat, so the numbers of the documents of the agency, which both flats share, are
/// in a fixed order.
/// </remarks>
public class ItalianAgencyScenarioTests
{
    private static readonly AccountantPackagePeriod October = AccountantPackagePeriod.OfMonth(2026, 10);

    private static readonly AccountantPackagePeriod Year2026 = AccountantPackagePeriod.OfYear(2026);

    /// <summary>The end of the month of the agency: the last day of October, 16:00 in Como.</summary>
    private static readonly DateTimeOffset EndOfOctober = World.InRome(Day(10, 31), 16);

    /// <summary>The day the agency issues the self-billed invoices of October (the month is over, the deadline is the 15th).</summary>
    private static readonly DateTimeOffset NovemberThird = World.InRome(Day(11, 3), 10);

    /// <summary>The day the agency pays the owners (the withholding goes into the F24 of November; the wire is asked for the next day).</summary>
    private static readonly DateOnly PayDay = Day(11, 2);

    /// <summary>The first days of the year after: the CU, the 770, the communication of 30 June and the declaration of the tourist tax are those of 2026.</summary>
    private static readonly DateOnly YearEnd = new(2027, 1, 10);

    // ---------------------------------------------------------------- the four origins

    // Origine 1, Booking.com tramite Octorate (V1, i proprietari intestatari): A ricevuta del proprietario, B FatturaPA della societa'; l'incasso e' del portale, nessuna ritenuta, TD17 solo per B.
    [Fact]
    [Trait("Scenario", "A2 A3 B7 B8a B8b C1 C2-V1 C3 C4 C5 C6 C7 C9 D1 D2 D3")]
    public async Task Origin_BookingCom_ThroughOctorate_FlatsAAndB_FromTheReservationToTheYearEnd()
    {
        await using var world = await World.CreateItalianAsync();
        var stays = await BookingComStaysAsync(world);

        // What the import stored, for both flats: the price of the host, the commission of the reservation, the portal collects and the tourist tax of the comune is in it.
        foreach (var stay in new[] { stays.A, stays.B })
        {
            var stored = await world.BookingAsync(stay);
            Assert.Equal(
                (BookingSource.BookingCom, BookingStatus.Confirmed, 450.00m, 462.00m, 12.00m, 12.00m, (decimal?)67.50m, (bool?)true, 2),
                (stored.Source, stored.Status, stored.BasePrice, stored.TotalPrice, stored.TouristTax, stored.TouristTaxAmount, stored.OtaCommission, stored.OtaCompanyCollect, stored.NumberOfGuests));
            Assert.NotNull(stored.OctorateReservationId);
        }

        await RunTheDocumentsAsync(world, (Day(10, 6), 1), (Day(10, 7), 1));

        // B8a, flat A: the receipt in the name of the owner (its sezionale, its fiscal code, the agency as the one who issues for it), the stamp duty over 77.47 charged to nobody
        // (the owner bears it, the total is what the guest pays), the tourist tax on its own line. A portal sends no invoice data: made out to the guest of the booking, by name only.
        // FIX/BE-PORTAL-TAXLINE: the document of a booking of a portal is paid through the portal for what the portal collected (450.00), never for the line of the tourist tax the portal
        // does not collect and nobody recorded (12.00): that is to collect at the property. The stored state is still the one of the channel (the portal collected), the total is the
        // whole document, and the PDF says the rest.
        var receipt = await AssertReceiptAsync(
            world, stays.A, "RIC2026/1", Day(10, 6), [(FiscalLineType.Accommodation, 450.00m, 0m, "N2.2", 0m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m)], 462.00m,
            FiscalPaymentChannel.Portal, FiscalDocumentPaymentState.Paid);
        AssertRecipientIsTheGuestByName(receipt, "TEST Ospite Portale");
        await AssertReceiptPdfAsync(world, receipt, ("462,00", "2,00"), "Pagamento: pagato tramite il portale per EUR 450,00");
        await AssertTheTaxIsToCollectAsync(world, receipt);
        await AssertTheGuestGotTheDocumentAsync(world, receipt, stays.A, "Ricevuta", World.OwnerAName);

        // B8b, flat B: the FatturaPA of the company (cedente), the agency its third-party issuer (TZ), the sezionale SRL, the VAT of the stay at 10%, the tourist tax N1 outside the
        // VAT, valid against the official schema; «to send» to the SDI, no provider configured: nothing is sent by itself, the issuer declares what it sent by hand.
        var invoice = await AssertCompanyInvoiceAsync(
            world, stays.B, "SRL2026/1", Day(10, 7), [(FiscalLineType.Accommodation, 409.09m, 10m, null, 40.91m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m)],
            (421.09m, 40.91m, 462.00m), FiscalPaymentChannel.Portal, FiscalDocumentPaymentState.Paid);
        await AssertCompanyPdfAsync(world, invoice, ("421,09", "40,91", "462,00"), "Pagamento: pagato tramite il portale per EUR 450,00");
        await AssertTheTaxIsToCollectAsync(world, invoice);
        await AssertTheGuestGotTheDocumentAsync(world, invoice, stays.B, "Fattura", World.OwnerBName);
        await AssertTheSdiStatesAsync(world, invoice);

        // C1-C4: the month, for both owners. V1: the account of Booking.com is the owner's, so the portal bills the owner and the statement says so; the invoice of the property
        // manager has no recharge of it.
        var months = await CloseTheMonthAsync(
            world,
            new Month(450.00m, -67.50m, -90.00m, -30.80m, -50.00m, 211.70m, [(FiscalLineType.Commission, 90.00m, 22m, 19.80m), (FiscalLineType.Recharge, 50.00m, 22m, 11.00m)], (140.00m, 30.80m, 170.80m), "PM2026/1"),
            new Month(450.00m, -67.50m, -90.00m, -35.20m, -70.00m, 187.30m, [(FiscalLineType.Commission, 90.00m, 22m, 19.80m), (FiscalLineType.Recharge, 70.00m, 22m, 15.40m)], (160.00m, 35.20m, 195.20m), "PM2026/2"),
            stays.Cleanings);
        foreach (var (flat, month) in new[] { (world.FlatA, months.A), (world.FlatB, months.B) })
        {
            var fee = Assert.Single(await world.LedgerOfAsync(2026, 10, flat), e => e.EntryType == OwnerLedgerEntryType.ChannelFee);
            Assert.Equal(-67.50m, fee.Amount);
            Assert.Equal(ChannelFeeSources.Payload, fee.RuleVersion);
            var statement = await world.StatementOfAsync(month.Published.Id);
            Assert.False(Assert.Single(statement.Of(OwnerStatementSection.ChannelFees)).AgencyHoldsPortalAccount);
        }

        // C2 (V1): the private owner has no TD17 (the portal charged the Italian VAT); the company that holds the account integrates the commission of the month (TD17 of the
        // month, self-billed by CasaZen on its profile, the portal the cedente).
        await AssertSelfBilledOfTheCompanyAsync(world, 67.50m, 14.85m, 82.35m, "TD17-2026/1");

        // C5, C6: nothing was collected by the agency (the portal pays the owner): no withholding, with its reason; the wire is the whole of each statement.
        var payouts = await PayTheOwnersAsync(
            world,
            months,
            new Payout(null, 0m, 211.70m, OwnerWithholdingRules.NoCollectionOrIntervention),
            new Payout(null, 0m, 187.30m, OwnerWithholdingRules.NoCollectionOrIntervention));
        AssertTheFactsOfTheStays(payouts.A, (BookingSource.BookingCom, PayoutStayReasons.PortalPaysOwner, false, OwnerWithholdingRules.NoCollectionOrIntervention, 450.00m));
        await AssertNothingWithheldAsync(world);

        // D1: no CU, no 770 and no F24: the agency withheld nothing, so the stay of the private owner is in the communication of the contracts to send by 30 June.
        await AssertContractsAsync(world, (stays.A, BookingSource.BookingCom, Day(10, 2), Day(10, 5), 3, 450.00m, OwnerWithholdingRules.NoCollectionOrIntervention));

        // C9, D3: the tourist tax of the year, owed for the two stays and remitted to the comune.
        await AssertTouristTaxAsync(world, 2, 6, 4, 24.00m);
    }

    // Origine 2, sito dell'agenzia tramite il canale diretto di Octorate: incasso registrato a mano, A con ritenuta del 21% e F24 1919, B senza ritenuta.
    [Fact]
    [Trait("Scenario", "A2 A3 B7 B8a B8b C1 C3 C4 C5 C6 C7 C9 D1 D2 D3")]
    public async Task Origin_AgencySite_ThroughTheDirectChannel_FlatsAAndB_PaidOutsideCasaZen_FromTheReservationToTheYearEnd()
    {
        await using var world = await World.CreateItalianAsync();
        var stays = await AgencySiteStaysAsync(world);

        // A direct booking with the id of the channel manager, the tourist tax the site charged, no commission, nobody says who collects.
        foreach (var stay in new[] { stays.A, stays.B })
        {
            var stored = await world.BookingAsync(stay);
            Assert.Equal(
                (BookingSource.Direct, 390.00m, 402.00m, 12.00m, (decimal?)null, (bool?)null),
                (stored.Source, stored.BasePrice, stored.TotalPrice, stored.TouristTax, stored.OtaCommission, stored.OtaCompanyCollect));
            Assert.NotNull(stored.OctorateReservationId);
        }

        await RunTheDocumentsAsync(world, (Day(10, 13), 1), (Day(10, 14), 1));

        // The guest paid by bank transfer to the agency, which recorded it by hand: both documents are paid, outside CasaZen.
        var receipt = await AssertReceiptAsync(
            world, stays.A, "RIC2026/1", Day(10, 13), [(FiscalLineType.Accommodation, 390.00m, 0m, "N2.2", 0m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m)], 402.00m,
            FiscalPaymentChannel.Outside, FiscalDocumentPaymentState.Paid);
        Assert.Equal((InvoiceRecipientKind.Guest, "TEST Ospite Sito", World.GuestFiscalCode), (receipt.RecipientKind, receipt.RecipientName, receipt.RecipientFiscalCode));
        await AssertReceiptPdfAsync(world, receipt, ("402,00", "2,00"), "Pagamento: pagato");
        await AssertTheGuestGotTheDocumentAsync(world, receipt, stays.A, "Ricevuta", World.OwnerAName);

        var invoice = await AssertCompanyInvoiceAsync(
            world, stays.B, "SRL2026/1", Day(10, 14), [(FiscalLineType.Accommodation, 354.55m, 10m, null, 35.45m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m)],
            (366.55m, 35.45m, 402.00m), FiscalPaymentChannel.Outside, FiscalDocumentPaymentState.Paid);
        await AssertTheGuestGotTheDocumentAsync(world, invoice, stays.B, "Fattura", World.OwnerBName);
        await AssertTheSdiStatesAsync(world, invoice);

        // The revenue is the payment the agency recorded, less its tourist tax, on the day it came in: no channel fee for the agency's own site.
        var months = await CloseTheMonthAsync(
            world,
            new Month(390.00m, 0m, -78.00m, -28.16m, -50.00m, 233.84m, [(FiscalLineType.Commission, 78.00m, 22m, 17.16m), (FiscalLineType.Recharge, 50.00m, 22m, 11.00m)], (128.00m, 28.16m, 156.16m), "PM2026/1"),
            new Month(390.00m, 0m, -78.00m, -32.56m, -70.00m, 209.44m, [(FiscalLineType.Commission, 78.00m, 22m, 17.16m), (FiscalLineType.Recharge, 70.00m, 22m, 15.40m)], (148.00m, 32.56m, 180.56m), "PM2026/2"),
            stays.Cleanings);
        var revenue = Assert.Single(await world.LedgerOfAsync(2026, 10, world.FlatA), e => e.EntryType == OwnerLedgerEntryType.BookingRevenue);
        Assert.Equal((OwnerLedgerSourceType.Payment, Day(10, 8)), (revenue.SourceType, revenue.RecognitionDate));
        await AssertNoSelfBillingAsync(world);

        // C5: the agency collected (the payment it recorded) and no portal withheld: 21% of the gross of the stay without the tourist tax for flat A, code 1919; flat B, a company,
        // is no short let of a private person: nothing is withheld, and the decision says why.
        var payouts = await PayTheOwnersAsync(
            world,
            months,
            new Payout(390.00m, 81.90m, 151.94m, "applicable"),
            new Payout(null, 0m, 209.44m, OwnerWithholdingRules.NotQualifyingShortLet));
        AssertTheFactsOfTheStays(payouts.A, (BookingSource.Direct, PayoutStayReasons.RecordedPayment, true, PayoutStayReasons.InBase, 390.00m));
        AssertTheFactsOfTheStays(payouts.B, (BookingSource.Direct, PayoutStayReasons.RecordedPayment, false, OwnerWithholdingRules.NotQualifyingShortLet, 390.00m));
        Assert.Equal(PayoutStayReasons.OwnerNotPrivate, PayoutFactRules.FromJson(payouts.B.Evidence[0].FactsJson)!.Stays.Single().QualifyingReason);
        await AssertWithholdingAsync(world, 390.00m, 81.90m, 233.84m, 151.94m);

        // D1: nothing is left to communicate by 30 June: the one stay of a private owner is in the CU.
        await AssertContractsAsync(world);
        await AssertTouristTaxAsync(world, 2, 6, 4, 24.00m);
    }

    // Origine 3, sito CasaZen pagato con Stripe: la commissione Stripe e' dell'agenzia, la pulizia e' fatturata dall'agenzia all'ospite, ritenuta di A, B senza ritenuta.
    [Fact]
    [Trait("Scenario", "A2 A3 B7 B8a B8b C1 C3 C4 C5 C6 C7 C9 D1 D2 D3")]
    public async Task Origin_CasaZenSite_FlatsAAndB_PaidWithStripe_FromTheCheckoutToTheYearEnd()
    {
        await using var world = await World.CreateItalianAsync();
        var stays = await CasaZenSiteStaysAsync(world);

        // The settlement of the webhook confirmed the bookings and completed the payments; the fee of each charge is the one of its balance transaction.
        foreach (var (booking, payment) in new[] { (stays.A, stays.PaymentA!.Value), (stays.B, stays.PaymentB!.Value) })
        {
            Assert.Equal(BookingStatus.Confirmed, (await world.BookingAsync(booking)).Status);
            var paid = await world.PaymentAsync(payment);
            Assert.Equal((PaymentStatus.Completed, 432.00m, (decimal?)12.48m, (decimal?)419.52m), (paid.Status, paid.Amount, paid.ProviderFeeAmount, paid.ProviderNetAmount));
        }

        await RunTheDocumentsAsync(world, (Day(10, 20), 2), (Day(10, 21), 2));

        // Flat A: the receipt of the owner for the stay and the tourist tax; the cleaning the agency bills the guest is the invoice of the agency (22%), in its own series.
        var receipt = await AssertReceiptAsync(
            world, stays.A, "RIC2026/1", Day(10, 20), [(FiscalLineType.Accommodation, 360.00m, 0m, "N2.2", 0m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m)], 372.00m,
            FiscalPaymentChannel.CasaZen, FiscalDocumentPaymentState.Paid);
        var cleaningA = await AssertAgencyServicesInvoiceAsync(world, stays.A, "2026/1", Day(10, 20));
        await AssertReceiptPdfAsync(world, receipt, ("372,00", "2,00"), "Pagamento: pagato");
        await AssertTheGuestGotTheDocumentAsync(world, receipt, stays.A, "Ricevuta", World.OwnerAName);
        await AssertTheGuestGotTheDocumentAsync(world, cleaningA, stays.A, "Fattura", World.ItalianAgencyLegalName);

        // Flat B: the invoice of the company for the stay (10%) and the tourist tax, and the invoice of the agency for the cleaning, which does not belong to the company.
        var invoice = await AssertCompanyInvoiceAsync(
            world, stays.B, "SRL2026/1", Day(10, 21), [(FiscalLineType.Accommodation, 327.27m, 10m, null, 32.73m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m)],
            (339.27m, 32.73m, 372.00m), FiscalPaymentChannel.CasaZen, FiscalDocumentPaymentState.Paid);
        var cleaningB = await AssertAgencyServicesInvoiceAsync(world, stays.B, "2026/2", Day(10, 21));
        await AssertTheGuestGotTheDocumentAsync(world, invoice, stays.B, "Fattura", World.OwnerBName);
        await AssertTheGuestGotTheDocumentAsync(world, cleaningB, stays.B, "Fattura", World.ItalianAgencyLegalName);
        await AssertTheSdiStatesAsync(world, invoice);
        await AssertTheSdiStatesAsync(world, cleaningB);

        // The revenue is the Stripe payment on its day, less its tourist tax; the mandate does not pass the card fee to the owner, so the agency bears it.
        var months = await CloseTheMonthAsync(
            world,
            new Month(420.00m, 0m, -84.00m, -29.48m, -50.00m, 256.52m, [(FiscalLineType.Commission, 84.00m, 22m, 18.48m), (FiscalLineType.Recharge, 50.00m, 22m, 11.00m)], (134.00m, 29.48m, 163.48m), "PM2026/1"),
            new Month(420.00m, 0m, -84.00m, -33.88m, -70.00m, 232.12m, [(FiscalLineType.Commission, 84.00m, 22m, 18.48m), (FiscalLineType.Recharge, 70.00m, 22m, 15.40m)], (154.00m, 33.88m, 187.88m), "PM2026/2"),
            stays.Cleanings);
        var ledger = await world.LedgerOfAsync(2026, 10, world.FlatA);
        Assert.Equal(Day(10, 1), Assert.Single(ledger, e => e.EntryType == OwnerLedgerEntryType.BookingRevenue).RecognitionDate);
        Assert.DoesNotContain(ledger, e => e.EntryType == OwnerLedgerEntryType.OwnerExpense);
        await AssertNoSelfBillingAsync(world);

        var payouts = await PayTheOwnersAsync(
            world,
            months,
            new Payout(420.00m, 88.20m, 168.32m, "applicable"),
            new Payout(null, 0m, 232.12m, OwnerWithholdingRules.NotQualifyingShortLet));
        AssertTheFactsOfTheStays(payouts.A, (BookingSource.Direct, PayoutStayReasons.CasaZenPayment, true, PayoutStayReasons.InBase, 420.00m));
        await AssertWithholdingAsync(world, 420.00m, 88.20m, 256.52m, 168.32m);
        await AssertContractsAsync(world);
        await AssertTouristTaxAsync(world, 2, 6, 4, 24.00m);
    }

    // Origine 4, prenotazione inserita a mano: pagamento in contanti registrato, A con ritenuta del 21%, B senza ritenuta.
    [Fact]
    [Trait("Scenario", "A2 A3 B7 B8a B8b C1 C3 C4 C5 C6 C7 C9 D1 D2 D3")]
    public async Task Origin_Manual_FlatsAAndB_PaidInCash_FromTheBookingToTheYearEnd()
    {
        await using var world = await World.CreateItalianAsync();
        var stays = await ManualStaysAsync(world);

        await RunTheDocumentsAsync(world, (Day(10, 27), 1), (Day(10, 28), 1));

        var receipt = await AssertReceiptAsync(
            world, stays.A, "RIC2026/1", Day(10, 27), [(FiscalLineType.Accommodation, 300.00m, 0m, "N2.2", 0m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m)], 312.00m,
            FiscalPaymentChannel.Outside, FiscalDocumentPaymentState.Paid);
        await AssertReceiptPdfAsync(world, receipt, ("312,00", "2,00"), "Pagamento: pagato");
        await AssertTheGuestGotTheDocumentAsync(world, receipt, stays.A, "Ricevuta", World.OwnerAName);
        var invoice = await AssertCompanyInvoiceAsync(
            world, stays.B, "SRL2026/1", Day(10, 28), [(FiscalLineType.Accommodation, 272.73m, 10m, null, 27.27m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m)],
            (284.73m, 27.27m, 312.00m), FiscalPaymentChannel.Outside, FiscalDocumentPaymentState.Paid);
        await AssertTheGuestGotTheDocumentAsync(world, invoice, stays.B, "Fattura", World.OwnerBName);
        await AssertTheSdiStatesAsync(world, invoice);

        var months = await CloseTheMonthAsync(
            world,
            new Month(300.00m, 0m, -60.00m, -24.20m, -50.00m, 165.80m, [(FiscalLineType.Commission, 60.00m, 22m, 13.20m), (FiscalLineType.Recharge, 50.00m, 22m, 11.00m)], (110.00m, 24.20m, 134.20m), "PM2026/1"),
            new Month(300.00m, 0m, -60.00m, -28.60m, -70.00m, 141.40m, [(FiscalLineType.Commission, 60.00m, 22m, 13.20m), (FiscalLineType.Recharge, 70.00m, 22m, 15.40m)], (130.00m, 28.60m, 158.60m), "PM2026/2"),
            stays.Cleanings);
        await AssertNoSelfBillingAsync(world);

        var payouts = await PayTheOwnersAsync(
            world,
            months,
            new Payout(300.00m, 63.00m, 102.80m, "applicable"),
            new Payout(null, 0m, 141.40m, OwnerWithholdingRules.NotQualifyingShortLet));
        AssertTheFactsOfTheStays(payouts.A, (BookingSource.Manual, PayoutStayReasons.RecordedPayment, true, PayoutStayReasons.InBase, 300.00m));
        await AssertWithholdingAsync(world, 300.00m, 63.00m, 165.80m, 102.80m);
        await AssertContractsAsync(world);
        await AssertTouristTaxAsync(world, 2, 6, 4, 24.00m);
    }

    // ---------------------------------------------------------------- the main path, in memory and on PostgreSQL

    // Percorso principale: le quattro origini nello stesso mese sui due appartamenti, fino al pacchetto, al bonifico e agli export di fine anno.
    [Fact]
    [Trait("Scenario", "A2 A3 B7 B8a B8b C1 C2-V1 C3 C4 C5 C6 C7 C8 C9 D1 D2 D3")]
    public async Task MainPath_TheFourOriginsOnTheTwoFlatsInOneMonth_UpToThePackageTheWireAndTheYearEnd()
    {
        await using var world = await World.CreateItalianAsync();
        await RunTheMainPathAsync(world);
    }

    // Percorso principale su PostgreSQL: le quattro origini nello stesso mese sui due appartamenti, fino al pacchetto, al bonifico e agli export di fine anno.
    [PostgresFact]
    [Trait("Scenario", "A2 A3 B7 B8a B8b C1 C2-V1 C3 C4 C5 C6 C7 C8 C9 D1 D2 D3")]
    public async Task MainPath_TheFourOriginsOnTheTwoFlatsInOneMonth_UpToThePackageTheWireAndTheYearEnd_Postgres()
    {
        await using var database = await PostgresTestDatabase.CreateMigratedAsync("acceptanceitaly");
        await using var world = await World.CreateItalianAsync(options: database.CreateOptions());
        await RunTheMainPathAsync(world);
    }

    /// <summary>
    /// The four origins in the same month on the same two flats: the documents numbered in the order of their stays (the receipts of the owner of flat A, the invoices of the
    /// company of flat B, the two invoices of the agency for the cleaning), one statement with the four stays for each owner, one invoice of the property manager for each (one
    /// commission line for the flat, a recharge for each cleaning), the TD17 of the company, the monthly report of the agency, the package, the wire, the withholding of the
    /// stays the agency collected (the Booking.com one, paid to the owner by the portal, is left out and is in the communication of 30 June) and the exports of the year.
    /// </summary>
    private static async Task RunTheMainPathAsync(World world)
    {
        var portal = await BookingComStaysAsync(world);
        var showcase = await CasaZenSiteStaysAsync(world);
        var site = await AgencySiteStaysAsync(world, importedOn: Day(10, 2));
        var manual = await ManualStaysAsync(world);

        // The daily runs of the days after the stays, flat A first and flat B the day after, so the numbers of the agency's invoices are in a fixed order.
        await RunTheDocumentsAsync(world, (Day(10, 6), 1), (Day(10, 7), 1));
        await RunTheDocumentsAsync(world, (Day(10, 13), 1), (Day(10, 14), 1));
        await RunTheDocumentsAsync(world, (Day(10, 20), 2), (Day(10, 21), 2));
        await RunTheDocumentsAsync(world, (Day(10, 27), 1), (Day(10, 28), 1));

        // The documents of the four stays of flat A (receipts in the order of the stays) and of flat B (invoices of the company), and the two invoices of the agency.
        var receipts = new[]
        {
            await AssertReceiptAsync(world, portal.A, "RIC2026/1", Day(10, 6), [(FiscalLineType.Accommodation, 450.00m, 0m, "N2.2", 0m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m)], 462.00m, FiscalPaymentChannel.Portal, FiscalDocumentPaymentState.Paid),
            await AssertReceiptAsync(world, site.A, "RIC2026/2", Day(10, 13), [(FiscalLineType.Accommodation, 390.00m, 0m, "N2.2", 0m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m)], 402.00m, FiscalPaymentChannel.Outside, FiscalDocumentPaymentState.Paid),
            await AssertReceiptAsync(world, showcase.A, "RIC2026/3", Day(10, 20), [(FiscalLineType.Accommodation, 360.00m, 0m, "N2.2", 0m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m)], 372.00m, FiscalPaymentChannel.CasaZen, FiscalDocumentPaymentState.Paid),
            await AssertReceiptAsync(world, manual.A, "RIC2026/4", Day(10, 27), [(FiscalLineType.Accommodation, 300.00m, 0m, "N2.2", 0m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m)], 312.00m, FiscalPaymentChannel.Outside, FiscalDocumentPaymentState.Paid),
        };
        var invoices = new[]
        {
            await AssertCompanyInvoiceAsync(world, portal.B, "SRL2026/1", Day(10, 7), [(FiscalLineType.Accommodation, 409.09m, 10m, null, 40.91m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m)], (421.09m, 40.91m, 462.00m), FiscalPaymentChannel.Portal, FiscalDocumentPaymentState.Paid),
            await AssertCompanyInvoiceAsync(world, site.B, "SRL2026/2", Day(10, 14), [(FiscalLineType.Accommodation, 354.55m, 10m, null, 35.45m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m)], (366.55m, 35.45m, 402.00m), FiscalPaymentChannel.Outside, FiscalDocumentPaymentState.Paid),
            await AssertCompanyInvoiceAsync(world, showcase.B, "SRL2026/3", Day(10, 21), [(FiscalLineType.Accommodation, 327.27m, 10m, null, 32.73m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m)], (339.27m, 32.73m, 372.00m), FiscalPaymentChannel.CasaZen, FiscalDocumentPaymentState.Paid),
            await AssertCompanyInvoiceAsync(world, manual.B, "SRL2026/4", Day(10, 28), [(FiscalLineType.Accommodation, 272.73m, 10m, null, 27.27m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m)], (284.73m, 27.27m, 312.00m), FiscalPaymentChannel.Outside, FiscalDocumentPaymentState.Paid),
        };
        await AssertAgencyServicesInvoiceAsync(world, showcase.A, "2026/1", Day(10, 20));
        await AssertAgencyServicesInvoiceAsync(world, showcase.B, "2026/2", Day(10, 21));
        Assert.All(receipts, receipt => Assert.NotNull(receipt.IssuerSnapshot));

        // B8b: every invoice of the company is «to send» with no provider; the issuer declares the first one sent by hand.
        foreach (var invoice in invoices)
            Assert.Equal(FiscalDocumentSdiStatuses.ToSend, invoice.SdiStatus);

        await AssertTheSdiStatesAsync(world, invoices[0]);

        // The statements of the month: the four stays (gross 1'560.00 without the EUR 48.00 of tourist tax), the fee of Booking.com, the commission of 20% of each, the cleanings
        // (flat A: the cost of the four requests; flat B: a flat fee for each stay), and the invoice of the property manager of each owner.
        var cleanings = portal.Cleanings.Concat(showcase.Cleanings).Concat(site.Cleanings).Concat(manual.Cleanings).ToList();
        var months = await CloseTheMonthAsync(
            world,
            new Month(
                1560.00m,
                -67.50m,
                -312.00m,
                -112.64m,
                -200.00m,
                867.86m,
                [
                    (FiscalLineType.Commission, 312.00m, 22m, 68.64m),
                    (FiscalLineType.Recharge, 50.00m, 22m, 11.00m),
                    (FiscalLineType.Recharge, 50.00m, 22m, 11.00m),
                    (FiscalLineType.Recharge, 50.00m, 22m, 11.00m),
                    (FiscalLineType.Recharge, 50.00m, 22m, 11.00m),
                ],
                (512.00m, 112.64m, 624.64m),
                "PM2026/1"),
            new Month(
                1560.00m,
                -67.50m,
                -312.00m,
                -130.24m,
                -280.00m,
                770.26m,
                [
                    (FiscalLineType.Commission, 312.00m, 22m, 68.64m),
                    (FiscalLineType.Recharge, 70.00m, 22m, 15.40m),
                    (FiscalLineType.Recharge, 70.00m, 22m, 15.40m),
                    (FiscalLineType.Recharge, 70.00m, 22m, 15.40m),
                    (FiscalLineType.Recharge, 70.00m, 22m, 15.40m),
                ],
                (592.00m, 130.24m, 722.24m),
                "PM2026/2"),
            cleanings);
        foreach (var month in new[] { months.A, months.B })
        {
            var statement = await world.StatementOfAsync(month.Published.Id);
            Assert.Equal(new[] { 300.00m, 390.00m, 420.00m, 450.00m }, statement.Of(OwnerStatementSection.Stays).Select(l => l.Amount).Order().ToArray());
        }

        // C7: the monthly report of the agency adds up what is stored: the commission and the recharges of each owner, the invoices of the property manager, the tourist tax.
        var summary = await world.AgencySummaryAsync(October);
        Assert.True(summary.OwnerAccountingIncluded);
        Assert.Equal(2, summary.Commissions.Count);
        var commissionA = Assert.Single(summary.Commissions, c => c.OwnerId == world.OwnerId);
        var commissionB = Assert.Single(summary.Commissions, c => c.OwnerId == world.OwnerBId);
        Assert.Equal(("EUR", 1), (commissionA.Currency, commissionA.Documents));
        Assert.Equal(new AgencyAmounts(312.00m, 68.64m, 380.64m), commissionA.Commission);
        Assert.Equal(new AgencyAmounts(200.00m, 44.00m, 244.00m), commissionA.Recharges);
        Assert.Equal(624.64m, commissionA.InvoicedTotal);
        Assert.Equal(new AgencyAmounts(312.00m, 68.64m, 380.64m), commissionB.Commission);
        Assert.Equal(new AgencyAmounts(280.00m, 61.60m, 341.60m), commissionB.Recharges);
        Assert.Equal(722.24m, commissionB.InvoicedTotal);
        Assert.Equal(new AgencyOwnerInvoiceCount("EUR", 2, 0), Assert.Single(summary.OwnerInvoices.Counts));
        Assert.Equal(96.00m, summary.TouristTax.Italy.Owed);

        // C2 (V1): the TD17 of the company for the commission of Booking.com; none for the private owner.
        await AssertSelfBilledOfTheCompanyAsync(world, 67.50m, 14.85m, 82.35m, "TD17-2026/1");

        // C5, C6: the stays the agency collected (the site, the CasaZen site, the booking by hand: 390.00 + 420.00 + 300.00) are withheld at 21% in one decision, once; the stay
        // of Booking.com, paid to the owner by the portal, is not. Flat B, a company, has none, with its reason.
        var payouts = await PayTheOwnersAsync(
            world,
            months,
            new Payout(1110.00m, 233.10m, 634.76m, "applicable"),
            new Payout(null, 0m, 770.26m, OwnerWithholdingRules.NotQualifyingShortLet));
        AssertTheFactsOfTheStays(
            payouts.A,
            (BookingSource.BookingCom, PayoutStayReasons.PortalPaysOwner, false, OwnerWithholdingRules.NoCollectionOrIntervention, 450.00m),
            (BookingSource.Direct, PayoutStayReasons.RecordedPayment, true, PayoutStayReasons.InBase, 390.00m),
            (BookingSource.Direct, PayoutStayReasons.CasaZenPayment, true, PayoutStayReasons.InBase, 420.00m),
            (BookingSource.Manual, PayoutStayReasons.RecordedPayment, true, PayoutStayReasons.InBase, 300.00m));
        await AssertWithholdingAsync(world, 1110.00m, 233.10m, 867.86m, 634.76m);

        // D1: the stay of Booking.com of the private owner is the one contract to communicate by 30 June (nothing was withheld on it); D2, C8: the documents issued in the name of
        // the company, in its portal and in its accountant export, and not a document of the private owner (nor of the agency).
        await AssertContractsAsync(world, (portal.A, BookingSource.BookingCom, Day(10, 2), Day(10, 5), 3, 450.00m, OwnerWithholdingRules.NoCollectionOrIntervention));
        await AssertTheCompanyPortalAsync(world, invoices);
        await AssertThePortalOfThePrivateOwnerAsync(world, receipts);

        // C8: the package of October for the accountant of the agency: the documents of the guests with their XML, the invoices of the property manager, the statements, the VAT of
        // each issuer (the agency, the company, the private owner), the withholding, the tourist tax and the index that lists every file once with its SHA-256.
        await AssertPackageAsync(world);

        // C9, D3: the tourist tax of the year for the eight stays, and its declaration.
        await AssertTouristTaxAsync(world, 8, 24, 16, 96.00m);
    }

    // ---------------------------------------------------------------- the variants

    // Variante V2 (⚖ I10 I11): l'account Booking.com dei due appartamenti e' dell'agenzia: commissione dalla regola del canale, riaddebito nella fattura PM, TD17 dell'agenzia
    // (non della societa'), e la ritenuta del 21% sul soggiorno di A, che adesso incassa l'agenzia.
    [Fact]
    [Trait("Scenario", "A2 A3 B8a B8b C1 C2-V2 C3 C4 C5 C6 C8")]
    public async Task Variant_V2_TheAgencyHoldsTheBookingAccount_RechargeOnThePmInvoice_TheAgencyTD17_AndTheWithholdingOfTheStay()
    {
        await using var world = await World.CreateItalianAsync(new ItalianSetup { AgencyHoldsTheBookingAccount = true });
        world.At(World.InRome(Day(9, 20), 10));

        // No commission in the reservation: the fee comes from the rule of the agency for Booking.com (15% of 450.00).
        var a = await world.ImportFromChannelManagerAsync(world.BookingComReservation(Day(10, 2), Day(10, 5), 450m, commission: null, touristTax: 12m, flat: world.FlatA), world.FlatA);
        var b = await world.ImportFromChannelManagerAsync(world.BookingComReservation(Day(10, 3), Day(10, 6), 450m, commission: null, touristTax: 12m, flat: world.FlatB), world.FlatB);
        await world.GiveItalianInvoiceDataAsync(b, "TEST Ospite Portale");
        var cleaning = await world.CompleteTheCleaningAsync(a, 50m, Day(10, 5), world.FlatA);
        await RunTheDocumentsAsync(world, (Day(10, 6), 1), (Day(10, 7), 1));

        var months = await CloseTheMonthAsync(
            world,
            new Month(
                450.00m,
                -67.50m,
                -90.00m,
                -45.65m,
                -50.00m,
                196.85m,
                [(FiscalLineType.Commission, 90.00m, 22m, 19.80m), (FiscalLineType.Recharge, 50.00m, 22m, 11.00m), (FiscalLineType.Recharge, 67.50m, 22m, 14.85m)],
                (207.50m, 45.65m, 253.15m),
                "PM2026/1"),
            new Month(
                450.00m,
                -67.50m,
                -90.00m,
                -50.05m,
                -70.00m,
                172.45m,
                [(FiscalLineType.Commission, 90.00m, 22m, 19.80m), (FiscalLineType.Recharge, 70.00m, 22m, 15.40m), (FiscalLineType.Recharge, 67.50m, 22m, 14.85m)],
                (227.50m, 50.05m, 277.55m),
                "PM2026/2"),
            [cleaning]);
        foreach (var (flat, month) in new[] { (world.FlatA, months.A), (world.FlatB, months.B) })
        {
            var fee = Assert.Single(await world.LedgerOfAsync(2026, 10, flat), e => e.EntryType == OwnerLedgerEntryType.ChannelFee);
            Assert.Equal(-67.50m, fee.Amount);
            Assert.Equal(ChannelFeeSources.Rule(world.BookingComRuleId), fee.RuleVersion);
            Assert.StartsWith("Riaddebito commissioni Booking.com", Assert.Single(month.Invoice.Lines, l => l.TotalPrice == 67.50m).Description, StringComparison.Ordinal);

            // The statement says it too: the commission was recharged by the property manager with its invoice, not billed by the portal.
            var statement = await world.StatementOfAsync(month.Published.Id);
            Assert.True(Assert.Single(statement.Of(OwnerStatementSection.ChannelFees)).AgencyHoldsPortalAccount);
            var detail = OwnerPortalStatementDetailDto.From(await world.PortalStatementAsTheOwnerAsync(month.Published.Id, flat));
            Assert.True(Assert.Single(detail.Sections.Single(s => s.Section == "channelFees").Lines).ChannelFeeRechargedByAgency);
        }

        // C2 (V2): the account is the agency's: one TD17 of the agency for the two stays (the commission is the portal's), none of the owners; the amount is the rule of the
        // agency (15%), not the figure of the portal, so the issue asks to accept the estimate.
        var (generation, issued) = await world.SelfBillMonthAsync(2026, 10, NovemberThird, acceptEstimate: true);
        var draft = Assert.Single(generation.Drafts);
        Assert.Equal(SelfBillingHolderKind.Agency, draft.HolderKind);
        Assert.Equal(135.00m, draft.EstimatedAmount);
        Assert.Empty(generation.NotDue);
        var td17 = Assert.Single(issued);
        Assert.Equal(("TD17-2026/1", FiscalDocumentStatus.Issued, 135.00m, 29.70m, 164.70m, SelfBillingHolderKind.Agency, "Booking.com B.V."), (td17.Document.Number, td17.Document.Status, td17.Document.TaxableBase, td17.Document.VatAmount, td17.Document.Total, td17.Row.HolderKind, td17.Row.PortalName));
        var td17Xml = ValidXml(td17.Document.FatturaPaXml);
        Assert.Equal(("TD17", "CC"), (Of(td17Xml, "TipoDocumento"), Of(td17Xml, "SoggettoEmittente")));

        // C5 (V2): the portal pays the agency, which holds the account: the agency collected the stay of the private owner and Booking.com is not a portal that withholds, so the
        // agency withholds 21% of its gross; the company has none.
        var payouts = await PayTheOwnersAsync(
            world,
            months,
            new Payout(450.00m, 94.50m, 102.35m, "applicable"),
            new Payout(null, 0m, 172.45m, OwnerWithholdingRules.NotQualifyingShortLet));
        AssertTheFactsOfTheStays(payouts.A, (BookingSource.BookingCom, PayoutStayReasons.PortalPaysAgency, true, PayoutStayReasons.InBase, 450.00m));

        // The VAT file of the agency: its invoices and the ones of the property manager (the recharge of the commission is a line of those), and nothing of the commission of the
        // portal, which is the TD17's (the reverse charge of an Italian agency, never a row for information as the purchase tax of a Swiss one).
        var package = await world.PackageAsync(October);
        var vat = Text(Assert.Single(package, f => f.Key == "iva/iva-it-01234567897.csv").Value);
        Assert.Contains("2026-10;Fatture ordinarie;22,00;;EUR;435,00;95,70;530,70;2", vat, StringComparison.Ordinal);
        Assert.DoesNotContain("Booking.com;", vat, StringComparison.Ordinal);
    }

    // Variante del portale che incassa la tassa e trattiene la ritenuta (Airbnb, ⚖ C5 R5): nessuna riga della tassa nella ricevuta, nessuna ritenuta dell'agenzia sul soggiorno
    // (il portale l'ha gia' trattenuta: mai due volte) e il contratto e' nella comunicazione del 30 giugno.
    [Fact]
    [Trait("Scenario", "A2 B7 B8a C1 C3 C4 C5 D1")]
    public async Task Variant_ThePortalCollectsTheTouristTaxAndWithholds_Airbnb_NoTaxLine_NoWithholdingOfTheAgency_AndTheContractIsCommunicated()
    {
        await using var world = await World.CreateItalianAsync(new ItalianSetup { AirbnbCollectsTheTouristTax = true });
        world.At(World.InRome(Day(9, 20), 10));
        var booking = await world.ImportFromChannelManagerAsync(world.AirbnbReservation(Day(10, 2), Day(10, 5), 450m, commission: 69.75m, touristTax: 12m));
        var cleaning = await world.CompleteTheCleaningAsync(booking, 50m, Day(10, 5));

        // The import stored the tax the portal collected from the guest, outside the price of the host.
        var stored = await world.BookingAsync(booking);
        Assert.Equal((BookingSource.Airbnb, 450.00m, 462.00m, 12.00m, (decimal?)69.75m, (bool?)true), (stored.Source, stored.BasePrice, stored.TotalPrice, stored.TouristTax, stored.OtaCommission, stored.OtaCompanyCollect));

        // The comune's rule says Airbnb collects the tax: the receipt bills the stay only, with no line of the tax.
        var run = await world.RunDocumentsDueAsync(Day(10, 6));
        Assert.True(run.Ran, run.NotRunReason);
        Assert.Equal((1, 1, 0), (run.DraftsCreated, run.DocumentsIssued, run.Skipped));
        var receipt = await AssertReceiptAsync(world, booking, "RIC2026/1", Day(10, 6), [(FiscalLineType.Accommodation, 450.00m, 0m, "N2.2", 0m)], 450.00m, FiscalPaymentChannel.Portal, FiscalDocumentPaymentState.Paid);
        var text = TextOf(await world.PdfContentAsync(receipt.Id));
        Assert.DoesNotContain("Imposta di soggiorno", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Airbnb", text, StringComparison.OrdinalIgnoreCase);

        var month = await world.CloseMonthAsync(2026, 10, EndOfOctober, world.FlatA);
        await AssertMonthAsync(
            world,
            world.FlatA,
            month,
            new Month(450.00m, -69.75m, -90.00m, -30.80m, -50.00m, 209.45m, [(FiscalLineType.Commission, 90.00m, 22m, 19.80m), (FiscalLineType.Recharge, 50.00m, 22m, 11.00m)], (140.00m, 30.80m, 170.80m), "PM2026/1"),
            [cleaning]);
        Assert.Empty((await world.StatementOfAsync(month.Published.Id)).TouristTax);

        // C5, R5: Airbnb withholds for a private Italian host itself: the agency withholds nothing on the stay, and the decision says it never withholds twice.
        var payout = await world.PayTheOwnerWithEvidenceAsync(month.Published, PayDay, world.FlatA);
        Assert.Equal((209.45m, 0m, 209.45m), (payout.Payout.GrossAmount, payout.Payout.WithholdingAmount, payout.Payout.NetAmount));
        var evidence = Assert.Single(payout.Evidence);
        Assert.Equal((TaxDecision.NotApplicable, OwnerWithholdingRules.AlreadyWithheldByPortal), (evidence.Decision, evidence.ReasonCode));
        var fact = Assert.Single(PayoutFactRules.FromJson(evidence.FactsJson)!.Stays);
        Assert.Equal((BookingSource.Airbnb.ToString(), true, PayoutStayReasons.WithheldByPortalRule, OwnerWithholdingRules.AlreadyWithheldByPortal), (fact.Channel, fact.WithheldByPortal, fact.PortalWithholding, fact.Reason));
        await AssertNothingWithheldAsync(world);

        // D1: no CU (nothing withheld by the agency): the contract is in the communication of 30 June, with the reason.
        await AssertContractsAsync(world, (booking, BookingSource.Airbnb, Day(10, 2), Day(10, 5), 3, 450.00m, OwnerWithholdingRules.AlreadyWithheldByPortal));
    }

    // Variante dell'appartamento B esente (⚖ I1): il soggiorno e' esente art. 10 n. 8 (natura N4) e la tassa di soggiorno e' N1 fuori campo: bollo virtuale sul documento della societa'.
    [Fact]
    [Trait("Scenario", "B7 B8b")]
    public async Task Variant_FlatB_TheStayIsExempt_NatureN4_StampDutyOnTheInvoiceOfTheCompany()
    {
        await using var world = await World.CreateItalianAsync(new ItalianSetup { StayVatOfFlatB = StayVatTreatment.ExemptArt10 });
        world.At(World.InRome(Day(10, 10), 10));
        var booking = await world.EnterByHandAsync(Day(10, 23), Day(10, 26), 300m, 12m, world.FlatB);
        await world.GiveItalianInvoiceDataAsync(booking, "TEST Ospite Telefono");
        world.At(World.InRome(Day(10, 23), 16));
        await world.RecordPaymentByHandAsync(booking, 312m, PaymentMethod.CashOnArrival, Day(10, 23));
        var run = await world.RunDocumentsDueAsync(Day(10, 27));
        Assert.True(run.Ran, run.NotRunReason);
        Assert.Equal((1, 1, 0), (run.DraftsCreated, run.DocumentsIssued, run.Skipped));

        // No VAT: the stay is N4, the tax N1; what is outside the VAT is over 77.47, so the stamp duty of 2.00 is due, in the document and in the XML (the issuer bears it).
        var invoice = Assert.Single(await world.DocumentsOfAsync(booking));
        Assert.Equal((DocumentKind.Invoice, FiscalDocumentStatus.Issued, "SRL2026/1", "it-ordinary-2026.1"), (invoice.Kind, invoice.Status, invoice.Number, invoice.RuleVersion));
        Assert.Equal(new (FiscalLineType, decimal, decimal, string?, decimal)[] { (FiscalLineType.Accommodation, 300.00m, 0m, "N4", 0m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m) }, LinesOf(invoice));
        Assert.Equal((312.00m, 0m, 2.00m, 312.00m), (invoice.TaxableBase, invoice.VatAmount, invoice.StampDuty, invoice.Total));
        var xml = ValidXml(invoice.FatturaPaXml);
        Assert.Equal(("TD01", "312.00"), (Of(xml, "TipoDocumento"), Of(xml, "ImportoTotaleDocumento")));
        Assert.Equal(("SI", "2.00"), (Of(xml, "BolloVirtuale"), Of(xml, "ImportoBollo")));
        Assert.Equal(
            new (string, string?, string, string)[] { ("0.00", "N1", "12.00", "0.00"), ("0.00", "N4", "300.00", "0.00") },
            xml.Descendants("DatiRiepilogo").Select(r => (Of(r, "AliquotaIVA")!, Of(r, "Natura"), Of(r, "ImponibileImporto")!, Of(r, "Imposta")!)).OrderBy(r => r.Item2, StringComparer.Ordinal).ToArray());
        Assert.Equal("TZ", Of(xml, "SoggettoEmittente"));
    }

    // Variante dei comproprietari 60/40 (⚖ I6): una sola ricevuta, a nome del firmatario, con le quote; il libro, il rendiconto, il bonifico e la ritenuta per quota; il CU per quota,
    // come dice il mandato.
    [Fact]
    [Trait("Scenario", "A4 B8a C1 C3 C4 C5 C6 D1")]
    public async Task Variant_CoOwners6040_OneReceiptWithTheShares_TheWithholdingAndTheCuPerShare()
    {
        await using var world = await World.CreateItalianAsync(new ItalianSetup { CoOwnersOfFlatA = true });
        var first = world.FlatA;
        var second = new Flat(World.FlatAName, world.PropertyId, world.CoOwnerId, world.CoOwnerUserId, World.OctorateAccommodation, World.OctorateRoom);
        world.At(World.InRome(Day(10, 10), 10));
        var booking = await world.EnterByHandAsync(Day(10, 23), Day(10, 26), 300m, 12m);
        await world.GiveItalianInvoiceDataAsync(booking, "TEST Ospite Telefono");
        world.At(World.InRome(Day(10, 23), 16));
        await world.RecordPaymentByHandAsync(booking, 312m, PaymentMethod.CashOnArrival, Day(10, 23));
        var cleaning = await world.CompleteTheCleaningAsync(booking, 50m, Day(10, 26));
        var run = await world.RunDocumentsDueAsync(Day(10, 27));
        Assert.True(run.Ran, run.NotRunReason);
        Assert.Equal((1, 1, 0), (run.DraftsCreated, run.DocumentsIssued, run.Skipped));

        // One receipt for the stay, issued on the profile of the co-owner with the largest share, listing both with their shares (the owner portal of the other co-owner has none).
        var receipt = await AssertReceiptAsync(world, booking, "RIC2026/1", Day(10, 27), [(FiscalLineType.Accommodation, 300.00m, 0m, "N2.2", 0m), (FiscalLineType.TouristTax, 12.00m, 0m, "N1", 0m)], 312.00m, FiscalPaymentChannel.Outside, FiscalDocumentPaymentState.Paid);
        var snapshot = FiscalIssuerSnapshot.FromJson(receipt.IssuerSnapshot)!;
        Assert.Equal(new[] { (World.OwnerAName, 60m), (World.CoOwnerName, 40m) }, snapshot.CoOwners!.Select(c => (c.Name, c.SharePercent)).ToArray());
        Assert.Contains("Comproprietari: ", TextOf(await world.PdfContentAsync(receipt.Id)), StringComparison.Ordinal);
        Assert.Empty(await world.PortalIssuedDocumentsAsync(second));

        // The month of each owner: its share of the stay (180.00 and 120.00), the commission and the cleaning cost of its share.
        var months = await world.CloseMonthForAsync(2026, 10, EndOfOctober, first, second);
        Assert.Equal(300.00m, months.Sum(m => OwnerStatementTotals.Of(m.Published).Gross));
        Assert.Equal(new[] { 180.00m, 120.00m }, months.Select(m => OwnerStatementTotals.Of(m.Published).Gross).ToArray());
        Assert.Equal(new[] { -30.00m, -20.00m }, months.Select(m => OwnerStatementTotals.Of(m.Published).Recharges).ToArray());
        Assert.Equal(
            cleaning,
            Assert.Single(await world.LedgerOfAsync(2026, 10, first), e => e.SourceType == OwnerLedgerSourceType.CleaningRequest && e.EntryType == OwnerLedgerEntryType.OwnerCharge).SourceId);

        // C5: the withholding of each owner is 21% of its own share (37.80 and 25.20), decided with its payout; the F24 of November adds them up (63.00) with a line for each.
        var payouts = new[] { await world.PayTheOwnerWithEvidenceAsync(months[0].Published, PayDay, first), await world.PayTheOwnerWithEvidenceAsync(months[1].Published, PayDay, second) };
        Assert.Equal(new[] { 37.80m, 25.20m }, payouts.Select(p => p.Payout.WithholdingAmount).ToArray());
        Assert.Equal(new[] { 180.00m, 120.00m }, payouts.Select(p => Assert.Single(p.Evidence).GrossBase!.Value).ToArray());
        var f24 = await world.F24DataAsync(2026, 11);
        Assert.Equal(("1919", 63.00m, 2), (Assert.Single(f24.Lines).TaxCode, Assert.Single(f24.Lines).Amount, f24.Breakdown.Count));
        Assert.Equal(new[] { 37.80m, 25.20m }, f24.Breakdown.OrderByDescending(l => l.GrossBase).Select(l => l.Amount).ToArray());

        // D1: the mandate says the CU is per share: a record for each co-owner with its own base and withholding, each on its own name.
        world.At(World.InRome(YearEnd, 10));
        var cu = await world.CuOfTheYearAsync(2026);
        Assert.Equal(2, cu.Records.Count);
        var byName = cu.Records.ToDictionary(r => r.Recipient!.Name!);
        Assert.Equal((CuAttributions.Share, 180.00m, 37.80m, World.OwnerAFiscalCode), (byName[World.OwnerAName].Attribution, byName[World.OwnerAName].GrossBase, byName[World.OwnerAName].Withheld, byName[World.OwnerAName].Recipient!.FiscalCode));
        Assert.Equal((CuAttributions.Share, 120.00m, 25.20m, World.CoOwnerFiscalCode), (byName[World.CoOwnerName].Attribution, byName[World.CoOwnerName].GrossBase, byName[World.CoOwnerName].Withheld, byName[World.CoOwnerName].Recipient!.FiscalCode));
        Assert.Equal(300.00m, cu.Records.Sum(r => r.GrossBase));
        Assert.Equal(63.00m, cu.Records.Sum(r => r.Withheld));
    }

    // Variante del conduttore societa' (⚖ I7): l'avviso (una sola volta), la decisione registrata con il motivo, nessun cambio automatico di regime; con la decisione «non locazione
    // breve» l'agenzia non trattiene.
    [Fact]
    [Trait("Scenario", "A2 C5")]
    public async Task Variant_CompanyTenant_TheWarningOnce_TheDecisionIsRecorded_AndNoWithholdingAfterIt()
    {
        await using var world = await World.CreateItalianAsync();
        world.At(World.InRome(Day(10, 10), 10));
        var booking = await world.EnterByHandAsync(Day(10, 23), Day(10, 26), 300m, 12m);
        await world.GiveItalianInvoiceDataAsync(booking, "TEST Ospite Srl", company: true);
        world.At(World.InRome(Day(10, 23), 16));
        await world.RecordPaymentByHandAsync(booking, 312m, PaymentMethod.CashOnArrival, Day(10, 23));
        var cleaning = await world.CompleteTheCleaningAsync(booking, 50m, Day(10, 26));

        // The warning: a stay of a company tenant in the flat of a private owner needs a person's decision; raised once, whatever the number of times it is asked.
        Assert.True(await world.RaiseCompanyTenantDecisionAsync(booking));
        Assert.False(await world.RaiseCompanyTenantDecisionAsync(booking));
        var pending = Assert.Single(await world.CompanyTenantCasesAsync(CompanyTenantCaseState.Pending));
        Assert.Equal((booking, world.FlatA.PropertyId, Day(10, 23), Day(10, 26), (ShortLetTaxRegime?)null), (pending.BookingId, pending.PropertyId, pending.CheckIn, pending.CheckOut, pending.Decision));

        var run = await world.RunDocumentsDueAsync(Day(10, 27));
        Assert.True(run.Ran, run.NotRunReason);
        var month = await world.CloseMonthAsync(2026, 10, EndOfOctober, world.FlatA);
        await AssertMonthAsync(
            world,
            world.FlatA,
            month,
            new Month(300.00m, 0m, -60.00m, -24.20m, -50.00m, 165.80m, [(FiscalLineType.Commission, 60.00m, 22m, 13.20m), (FiscalLineType.Recharge, 50.00m, 22m, 11.00m)], (110.00m, 24.20m, 134.20m), "PM2026/1"),
            [cleaning]);

        // ⚖ I7: while nobody decided, the regime does not change by itself: the preview withholds, and says the decision is pending.
        var preview = await world.PreviewThePayoutAsync(month.Published, PayDay);
        Assert.Equal((63.00m, (string?)null), (preview.WithholdingAmount, preview.BlockingCode));
        var pendingFact = Assert.Single(preview.Facts.Stays);
        Assert.Equal((true, true, PayoutStayReasons.InBase), (pendingFact.CompanyTenantDecisionPending, pendingFact.Qualifying, pendingFact.Reason));

        // The decision of the agency (the regime and its reason, by whom), once: the same decision again is the same record.
        const string reason = "TEST Il conduttore e' una societa': non e' una locazione breve di persona fisica.";
        var decision = await world.DecideTheCompanyTenantAsync(booking, ShortLetTaxRegime.NotShortLet, reason);
        Assert.Equal((booking, ShortLetTaxRegime.NotShortLet, World.AgencyUser), (decision.BookingId, decision.Regime, decision.DecidedByUserId));
        Assert.Equal(decision.Id, (await world.DecideTheCompanyTenantAsync(booking, ShortLetTaxRegime.NotShortLet, reason)).Id);
        Assert.Empty(await world.CompanyTenantCasesAsync(CompanyTenantCaseState.Pending));
        Assert.Equal(ShortLetTaxRegime.NotShortLet, Assert.Single(await world.CompanyTenantCasesAsync(CompanyTenantCaseState.Decided)).Decision);

        // With the decision, the payout withholds nothing on it, and the facts say why (the decision, not the owner nor the length of the stay).
        var payout = await world.PayTheOwnerWithEvidenceAsync(month.Published, PayDay, world.FlatA);
        Assert.Equal((165.80m, 0m, 165.80m), (payout.Payout.GrossAmount, payout.Payout.WithholdingAmount, payout.Payout.NetAmount));
        var evidence = Assert.Single(payout.Evidence);
        Assert.Equal((TaxDecision.NotApplicable, OwnerWithholdingRules.NotQualifyingShortLet), (evidence.Decision, evidence.ReasonCode));
        var fact = Assert.Single(PayoutFactRules.FromJson(evidence.FactsJson)!.Stays);
        Assert.Equal((false, ShortLetTreatmentReasons.CompanyTenantNotShortLet), (fact.Qualifying, fact.QualifyingReason));
        await AssertNothingWithheldAsync(world);
    }

    // Variante del terzo appartamento (⚖ I8): dal terzo appartamento in locazione breve, contando quello dichiarato fuori dall'agenzia, il proprietario e' presunto imprenditore:
    // l'avviso nel rendiconto, nessuna ritenuta dell'agenzia.
    [Fact]
    [Trait("Scenario", "A2 C3 C5")]
    public async Task Variant_ThirdUnit_TheOwnerIsPresumedABusiness_TheStatementFlagsIt_AndNoWithholding()
    {
        await using var world = await World.CreateItalianAsync();
        var before = await world.UnitsOfTheOwnerAsync(world.FlatA, Day(10, 31));
        Assert.Equal((true, 1, 0, 1, 2, false), (before.Applies, before.ManagedUnits, before.ExternalUnits, before.TotalUnits, before.MaxUnits, before.PresumedBusiness));

        // The owner lets two more apartments outside the agency: three in all, one over the limit of two.
        var declared = await world.DeclareExternalUnitsAsync(world.FlatA, 2);
        Assert.Equal((1, 2, 3, 2, true), (declared.ManagedUnits, declared.ExternalUnits, declared.TotalUnits, declared.MaxUnits, declared.PresumedBusiness));

        world.At(World.InRome(Day(10, 10), 10));
        var booking = await world.EnterByHandAsync(Day(10, 23), Day(10, 26), 300m, 12m);
        await world.GiveItalianInvoiceDataAsync(booking, "TEST Ospite Telefono");
        world.At(World.InRome(Day(10, 23), 16));
        await world.RecordPaymentByHandAsync(booking, 312m, PaymentMethod.CashOnArrival, Day(10, 23));
        var cleaning = await world.CompleteTheCleaningAsync(booking, 50m, Day(10, 26));
        var run = await world.RunDocumentsDueAsync(Day(10, 27));
        Assert.True(run.Ran, run.NotRunReason);

        // The statement is right and is not held back: it carries the flag of the tax treatment of the owner.
        var month = await world.CloseMonthAsync(2026, 10, EndOfOctober, world.FlatA);
        await AssertMonthAsync(
            world,
            world.FlatA,
            month,
            new Month(300.00m, 0m, -60.00m, -24.20m, -50.00m, 165.80m, [(FiscalLineType.Commission, 60.00m, 22m, 13.20m), (FiscalLineType.Recharge, 50.00m, 22m, 11.00m)], (110.00m, 24.20m, 134.20m), "PM2026/1"),
            [cleaning]);
        Assert.Contains(OwnerReadinessCodes.ShortLetBusinessPresumed, month.Published.IssuesJson ?? string.Empty, StringComparison.Ordinal);

        // The agency collected the stay, but a business is no short let of a private person: nothing is withheld, and the facts say it is the owner.
        var payout = await world.PayTheOwnerWithEvidenceAsync(month.Published, PayDay, world.FlatA);
        Assert.Equal((165.80m, 0m, 165.80m), (payout.Payout.GrossAmount, payout.Payout.WithholdingAmount, payout.Payout.NetAmount));
        var evidence = Assert.Single(payout.Evidence);
        Assert.Equal((TaxDecision.NotApplicable, OwnerWithholdingRules.NotQualifyingShortLet), (evidence.Decision, evidence.ReasonCode));
        var fact = Assert.Single(PayoutFactRules.FromJson(evidence.FactsJson)!.Stays);
        Assert.Equal((false, ShortLetTreatmentReasons.OwnerPresumedBusiness, true), (fact.Qualifying, fact.QualifyingReason, fact.CollectedByAgency));
        await AssertNothingWithheldAsync(world);
    }

    // ---------------------------------------------------------------- the changes of a booking (B12)

    // B12 cancellazione con rimborso: i documenti erano stati emessi subito; il rimborso Stripe dell'intero pagamento produce le note di credito di tutti i documenti (TD04 per le
    // fatture, lo storno per la ricevuta), emesse subito e coerenti con lo schema; nessun ricavo nel libro.
    [Fact]
    [Trait("Scenario", "B12")]
    public async Task Change_CancellationWithRefund_CreditNotesOfEveryDocument_TheTd04ValidAgainstTheSchema_AndNothingInTheLedger()
    {
        await using var world = await World.CreateItalianAsync();
        world.At(World.InRome(Day(10, 1), 12));
        var (bookingA, paymentA) = await world.PayOnTheCasaZenSiteAsync(Day(10, 16), Day(10, 19), 420m, 60m, 12.48m, touristTax: 12m, flat: world.FlatA);
        var (bookingB, paymentB) = await world.PayOnTheCasaZenSiteAsync(Day(10, 17), Day(10, 20), 420m, 60m, 12.48m, touristTax: 12m, flat: world.FlatB);
        await world.GiveItalianInvoiceDataAsync(bookingA, "TEST Ospite Vetrina");
        await world.GiveItalianInvoiceDataAsync(bookingB, "TEST Ospite Vetrina");

        // The guests asked for their documents when paying: the agency issued them at once (the stay of the owner and the cleaning of the agency).
        Assert.Equal(2, (await world.IssueEveryDocumentNowAsync(bookingA)).Count);
        Assert.Equal(2, (await world.IssueEveryDocumentNowAsync(bookingB)).Count);

        // The guests cancel within the free cancellation: the agency cancels the bookings and refunds the whole payments through Stripe.
        world.At(World.InRome(Day(10, 5), 10));
        await world.CancelAsHostAsync(bookingA);
        await world.CancelAsHostAsync(bookingB);
        foreach (var payment in new[] { paymentA, paymentB })
        {
            var refund = await world.RefundThroughStripeAsync(payment, amount: null);
            Assert.Equal((PaymentRefundStatus.Succeeded, 432.00m), (refund.Status, refund.Amount));
            Assert.Equal(PaymentStatus.Refunded, (await world.PaymentAsync(payment)).Status);
        }

        await world.DeliverEventsAsync();

        // Flat A: the receipt is reversed by a credit note of its own kind and series (no XML: a receipt has none), and the invoice of the agency by a TD04 (valid, linked to it).
        var documentsA = await world.DocumentsOfAsync(bookingA);
        var receiptNote = Assert.Single(documentsA, d => d.Kind == DocumentKind.CreditNote && !d.ForAgencyServices);
        var receipt = Assert.Single(documentsA, d => d.Kind == DocumentKind.Receipt);
        Assert.Equal((FiscalDocumentStatus.Issued, (Guid?)receipt.Id, 372.00m, FiscalDocumentSdiStatuses.NotApplicable), (receiptNote.Status, receiptNote.CreditedDocumentId, receiptNote.Total, receiptNote.SdiStatus));
        Assert.Null(receiptNote.FatturaPaXml);
        Assert.True(receipt.CreditedInFull);
        AssertCreditNoteOfAnInvoice(documentsA, true, 60.00m);

        // Flat B: the invoice of the company and the invoice of the agency are credited by a TD04 each, the one of the company on its own series, issued through the agency.
        var documentsB = await world.DocumentsOfAsync(bookingB);
        var companyNote = AssertCreditNoteOfAnInvoice(documentsB, false, 372.00m);
        var companyXml = ValidXml(companyNote.FatturaPaXml);
        Assert.Equal(("TD04", "TZ"), (Of(companyXml, "TipoDocumento"), Of(companyXml, "SoggettoEmittente")));
        AssertCreditNoteOfAnInvoice(documentsB, true, 60.00m);

        // October: the refunded payments leave no revenue and a cancelled stay no cleaning, so nothing is posted for the stays.
        var posting = await world.PostMonthAsync(2026, 10, EndOfOctober);
        Assert.DoesNotContain(posting.Issues, i => i.Blocking);
        Assert.Empty(await world.LedgerOfAsync(2026, 10, world.FlatA));
        Assert.Empty(await world.LedgerOfAsync(2026, 10, world.FlatB));
    }

    // ---------------------------------------------------------------- the official schema of the wire file

    // Bonifico: il file pain.001.001.09 dei pagamenti ai proprietari e' valido per lo schema ufficiale ISO (il test e' saltato, con il motivo, finche' lo schema non e' nel repository).
    [IsoWireSchemaFact]
    [Trait("Scenario", "C6")]
    public async Task Wire_ThePayoutFile_ValidatesAgainstTheOfficialIsoSchema()
    {
        await using var world = await World.CreateItalianAsync();
        await ManualStaysAsync(world);
        await RunTheDocumentsAsync(world, (Day(10, 27), 1), (Day(10, 28), 1));
        var months = await world.CloseMonthForAsync(2026, 10, EndOfOctober, world.FlatA, world.FlatB);
        world.At(World.InRome(PayDay, 10));
        var payouts = new[] { await world.PayTheOwnerAsync(months[0].Published, PayDay, world.FlatA), await world.PayTheOwnerAsync(months[1].Published, PayDay, world.FlatB) };

        // No schema of the test: the official one embedded in the build validates the file before the batch is saved.
        var (batch, xml) = await world.WireAsync([.. payouts.Select(p => p.Id)], PayDay.AddDays(1), schemas: null);

        Assert.Equal((102.80m + 141.40m, 2), (batch.TotalAmount, batch.TransferCount));
        Assert.Equal(XNamespace.Get(Pain001SchemaProvider.IsoNamespace), XDocument.Parse(xml).Root!.Name.Namespace);
    }

    // ---------------------------------------------------------------- the stays of the four origins (flat B's stay ends a day after flat A's)

    /// <summary>The stay of an origin on each flat: A of the private owner (a receipt), B of the company (an invoice); the cleanings of flat A's stays, and the payments when they are in CasaZen.</summary>
    private sealed record Stays(Guid A, Guid B, IReadOnlyList<Guid> Cleanings, Guid? PaymentA = null, Guid? PaymentB = null);

    /// <summary>
    /// Booking.com through the channel manager: 450.00 and a commission of 67.50 in the reservation, the portal collects, the tourist tax of the comune (12.00) in it. The portal
    /// sends no invoice data: the receipt of flat A is made out to the guest by name; the invoice of flat B needs its recipient, which the agency types. Flat A's stay is 2-5 October,
    /// flat B's 3-6.
    /// </summary>
    private static async Task<Stays> BookingComStaysAsync(World world)
    {
        world.At(World.InRome(Day(9, 20), 10));
        var a = await world.ImportFromChannelManagerAsync(world.BookingComReservation(Day(10, 2), Day(10, 5), 450m, commission: 67.50m, touristTax: 12m, flat: world.FlatA), world.FlatA);
        var b = await world.ImportFromChannelManagerAsync(world.BookingComReservation(Day(10, 3), Day(10, 6), 450m, commission: 67.50m, touristTax: 12m, flat: world.FlatB), world.FlatB);
        await world.GiveItalianInvoiceDataAsync(b, "TEST Ospite Portale");
        return new Stays(a, b, [await world.CompleteTheCleaningAsync(a, 50m, Day(10, 5), world.FlatA)]);
    }

    /// <summary>
    /// The agency's own site through the «direct» channel of the channel manager: 390.00 + 12.00 of tourist tax, no commission, paid by bank transfer to the agency, which records it
    /// by hand on 8 October. Flat A's stay is 9-12 October, flat B's 10-13.
    /// </summary>
    private static async Task<Stays> AgencySiteStaysAsync(World world, DateOnly? importedOn = null)
    {
        world.At(World.InRome(importedOn ?? Day(9, 25), 10));
        var a = await world.ImportFromChannelManagerAsync(world.AgencySiteReservation(Day(10, 9), Day(10, 12), 390m, touristTax: 12m, flat: world.FlatA), world.FlatA);
        var b = await world.ImportFromChannelManagerAsync(world.AgencySiteReservation(Day(10, 10), Day(10, 13), 390m, touristTax: 12m, flat: world.FlatB), world.FlatB);
        await world.GiveItalianInvoiceDataAsync(a, "TEST Ospite Sito");
        await world.GiveItalianInvoiceDataAsync(b, "TEST Ospite Sito");
        world.At(World.InRome(Day(10, 8), 9));
        await world.RecordPaymentByHandAsync(a, 402m, PaymentMethod.BankTransfer, Day(10, 8));
        await world.RecordPaymentByHandAsync(b, 402m, PaymentMethod.BankTransfer, Day(10, 8));
        return new Stays(a, b, [await world.CompleteTheCleaningAsync(a, 50m, Day(10, 12), world.FlatA)]);
    }

    /// <summary>
    /// The CasaZen site paid with Stripe on 1 October: 360.00 for the stay and 60.00 of cleaning (420.00) and 12.00 of tourist tax the checkout calculated: 432.00, the fee of the charge
    /// (12.48) borne by the agency. Flat A's stay is 16-19 October, flat B's 17-20.
    /// </summary>
    private static async Task<Stays> CasaZenSiteStaysAsync(World world)
    {
        world.At(World.InRome(Day(10, 1), 12));
        var (a, paymentA) = await world.PayOnTheCasaZenSiteAsync(Day(10, 16), Day(10, 19), price: 420m, cleaningFee: 60m, providerFee: 12.48m, touristTax: 12m, flat: world.FlatA);
        var (b, paymentB) = await world.PayOnTheCasaZenSiteAsync(Day(10, 17), Day(10, 20), price: 420m, cleaningFee: 60m, providerFee: 12.48m, touristTax: 12m, flat: world.FlatB);
        await world.GiveItalianInvoiceDataAsync(a, "TEST Ospite Vetrina");
        await world.GiveItalianInvoiceDataAsync(b, "TEST Ospite Vetrina");
        return new Stays(a, b, [await world.CompleteTheCleaningAsync(a, 50m, Day(10, 19), world.FlatA)], paymentA, paymentB);
    }

    /// <summary>A booking entered by the agency, 300.00 + 12.00 of tourist tax, paid in cash on the day of the arrival. Flat A's stay is 23-26 October, flat B's 24-27.</summary>
    private static async Task<Stays> ManualStaysAsync(World world)
    {
        world.At(World.InRome(Day(10, 10), 10));
        var a = await world.EnterByHandAsync(Day(10, 23), Day(10, 26), price: 300m, touristTax: 12m, world.FlatA);
        var b = await world.EnterByHandAsync(Day(10, 24), Day(10, 27), price: 300m, touristTax: 12m, world.FlatB);
        await world.GiveItalianInvoiceDataAsync(a, "TEST Ospite Telefono");
        await world.GiveItalianInvoiceDataAsync(b, "TEST Ospite Telefono");
        world.At(World.InRome(Day(10, 23), 16));
        await world.RecordPaymentByHandAsync(a, 312m, PaymentMethod.CashOnArrival, Day(10, 23));
        world.At(World.InRome(Day(10, 24), 16));
        await world.RecordPaymentByHandAsync(b, 312m, PaymentMethod.CashOnArrival, Day(10, 24));
        return new Stays(a, b, [await world.CompleteTheCleaningAsync(a, 50m, Day(10, 26), world.FlatA)]);
    }

    /// <summary>The daily runs of the days after the stays: each makes the drafts of the stay that ended the day before and issues the documents (automatic issue), flat A first.</summary>
    private static async Task RunTheDocumentsAsync(World world, (DateOnly Day, int Documents) forFlatA, (DateOnly Day, int Documents) forFlatB)
    {
        foreach (var (day, documents) in new[] { forFlatA, forFlatB })
        {
            var run = await world.RunDocumentsDueAsync(day);
            Assert.True(run.Ran, run.NotRunReason);
            Assert.Equal((1, documents, 0), (run.DraftsCreated, run.DocumentsIssued, run.Skipped));
        }
    }

    // ---------------------------------------------------------------- the documents of the guests

    /// <summary>A receipt of the private owner of flat A (its sezionale, its rule, the stamp duty of 2.00 over 77.47 borne by the issuer): its lines (net, rate, nature, VAT), its total, who collected.</summary>
    private static async Task<FiscalDocument> AssertReceiptAsync(
        World world,
        Guid booking,
        string number,
        DateOnly issuedOn,
        (FiscalLineType Type, decimal Net, decimal Rate, string? Nature, decimal Vat)[] lines,
        decimal total,
        FiscalPaymentChannel channel,
        FiscalDocumentPaymentState state)
    {
        var document = Assert.Single(await world.DocumentsOfAsync(booking), d => !d.ForAgencyServices);
        Assert.Equal((FiscalDocumentPurpose.GuestStay, DocumentKind.Receipt, FiscalDocumentStatus.Issued, number, (DateOnly?)issuedOn), (document.Purpose, document.Kind, document.Status, document.Number, document.IssueDate));
        Assert.Equal(("EUR", "it-private-2026.1", FiscalDocumentSdiStatuses.NotApplicable), (document.Currency, document.RuleVersion, document.SdiStatus));
        Assert.Null(document.FatturaPaXml);
        Assert.Equal(lines, LinesOf(document));
        Assert.Equal((total, 0m, 2.00m, total), (document.TaxableBase, document.VatAmount, document.StampDuty, document.Total));
        Assert.Equal((channel, state), (document.PaymentChannel, document.PaymentState));

        // Issued in the name of the owner (its profile, its sezionale, its fiscal code), by the agency on its behalf; the stamp reference of the owner is on it.
        var issuer = FiscalIssuerSnapshot.FromJson(document.IssuerSnapshot)!;
        await using var scope = world.Scope();
        var owner = await scope.Db.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.Id == document.IssuerProfileId);
        Assert.Equal(((Guid?)world.OwnerId, (DocumentRegime?)DocumentRegime.PrivatePerson, "RIC2026"), (owner.PropertyOwnerId, owner.DocumentRegime, document.Series));
        Assert.Equal((World.OwnerAName, World.OwnerAFiscalCode, "AUT-BOLLO-TEST-2026"), (issuer.LegalName, issuer.FiscalCode, issuer.StampDutyReference));
        Assert.Equal((World.ItalianAgencyLegalName, World.ItalianAgencyVat), (issuer.Intermediary!.Name, issuer.Intermediary.VatNumber));
        return document;
    }

    /// <summary>The booking has no invoice data (a portal sends none): the receipt is made out to the guest of the booking, by name only, and nothing else of the guest is on it.</summary>
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

    /// <summary>The PDF of a receipt: the title with the number, the owner as the issuer and the agency issuing on its behalf, the stamp duty with the reference that pays it, the totals, the payment; never a portal.</summary>
    private static async Task AssertReceiptPdfAsync(World world, FiscalDocument document, (string Total, string StampDuty) totals, string payment)
    {
        var content = await world.PdfContentAsync(document.Id);
        Assert.Equal($"Ricevuta n. {document.Number}", content.Title);
        var text = TextOf(content);
        Assert.Contains(World.OwnerAName, text, StringComparison.Ordinal);
        Assert.Contains($"Codice fiscale: {World.OwnerAFiscalCode}", text, StringComparison.Ordinal);
        Assert.Contains($"Emessa tramite {World.ItalianAgencyLegalName} per conto del proprietario", text, StringComparison.Ordinal);
        Assert.Contains("Imposta di soggiorno riscossa per conto del comune", text, StringComparison.Ordinal);
        Assert.Contains($"Imposta di bollo | {totals.StampDuty}", text, StringComparison.Ordinal);
        Assert.Contains($"Totale documento | {totals.Total}", text, StringComparison.Ordinal);
        Assert.Contains("Imposta di bollo assolta: AUT-BOLLO-TEST-2026", text, StringComparison.Ordinal);
        Assert.Contains(payment, text, StringComparison.Ordinal);
        AssertNoSupplierName(text);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(await world.PdfAsync(document.Id), 0, 4));
    }

    /// <summary>
    /// An invoice of the company of flat B issued through the agency: the issuer is the company (its sezionale, its VAT number), the agency the third party that issues for it; the
    /// lines, the totals and the stamp duty to the cent, who collected; the XML valid against the official schema, «to send» to the SDI.
    /// </summary>
    private static async Task<FiscalDocument> AssertCompanyInvoiceAsync(
        World world,
        Guid booking,
        string number,
        DateOnly issuedOn,
        (FiscalLineType Type, decimal Net, decimal Rate, string? Nature, decimal Vat)[] lines,
        (decimal Taxable, decimal Vat, decimal Total) totals,
        FiscalPaymentChannel channel,
        FiscalDocumentPaymentState state)
    {
        var document = Assert.Single(await world.DocumentsOfAsync(booking), d => !d.ForAgencyServices);
        Assert.Equal((FiscalDocumentPurpose.GuestStay, DocumentKind.Invoice, FiscalDocumentStatus.Issued, number, (DateOnly?)issuedOn), (document.Purpose, document.Kind, document.Status, document.Number, document.IssueDate));
        Assert.Equal(("EUR", "it-ordinary-2026.1", FiscalDocumentSdiStatuses.ToSend, "SRL2026"), (document.Currency, document.RuleVersion, document.SdiStatus, document.Series));
        Assert.Equal(lines, LinesOf(document));
        Assert.Equal(totals, (document.TaxableBase, document.VatAmount, document.Total));
        Assert.Equal(0m, document.StampDuty);
        Assert.Equal((channel, state), (document.PaymentChannel, document.PaymentState));

        var issuer = FiscalIssuerSnapshot.FromJson(document.IssuerSnapshot)!;
        await using (var scope = world.Scope())
        {
            var company = await scope.Db.OrgFiscalIssuerProfiles.AsNoTracking().SingleAsync(p => p.Id == document.IssuerProfileId);
            Assert.Equal(((Guid?)world.OwnerBId, (DocumentRegime?)DocumentRegime.VatOrdinary), (company.PropertyOwnerId, company.DocumentRegime));
        }

        Assert.Equal((World.OwnerBName, World.OwnerBVat, "RF01"), (issuer.LegalName, issuer.VatNumber, issuer.FatturaPaRegimeCode));
        Assert.Equal((World.ItalianAgencyLegalName, World.ItalianAgencyVat), (issuer.Intermediary!.Name, issuer.Intermediary.VatNumber));

        // The FatturaPA: the company is the cedente, the agency transmits and issues for it (TZ), the guest is the cessionario; the VAT summary is the stored VAT of the lines.
        var xml = ValidXml(document.FatturaPaXml);
        Assert.Equal(("FPR12", "TD01", "EUR", number), ((string?)xml.Root!.Attribute("versione"), Of(xml, "TipoDocumento"), Of(xml, "Divisa"), Of(xml, "Numero")));
        Assert.Equal((issuedOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), Money(totals.Total)), (Of(xml, "Data"), Of(xml, "ImportoTotaleDocumento")));
        var cedente = xml.Descendants("CedentePrestatore").Single();
        Assert.Equal(("IT", World.OwnerBVat, World.OwnerBName, "RF01"), (Of(cedente, "IdPaese"), Of(cedente, "IdCodice"), Of(cedente, "Denominazione"), Of(cedente, "RegimeFiscale")));
        Assert.Equal("Como", Of(cedente, "Comune"));
        var third = xml.Descendants("TerzoIntermediarioOSoggettoEmittente").Single();
        Assert.Equal(("IT", World.ItalianAgencyVat, World.ItalianAgencyLegalName), (Of(third, "IdPaese"), Of(third, "IdCodice"), Of(third, "Denominazione")));
        Assert.Equal(("TZ", World.ItalianAgencyVat), (Of(xml, "SoggettoEmittente"), Of(xml.Descendants("IdTrasmittente").Single(), "IdCodice")));
        Assert.Equal(World.GuestFiscalCode, Of(xml.Descendants("CessionarioCommittente").Single(), "CodiceFiscale"));
        Assert.Equal("0000000", Of(xml, "CodiceDestinatario"));
        Assert.Null(xml.Descendants("DatiBollo").FirstOrDefault());
        Assert.Equal(
            lines.GroupBy(l => (l.Rate, l.Nature)).OrderByDescending(g => g.Key.Rate).ThenBy(g => g.Key.Nature, StringComparer.Ordinal)
                .Select(g => (Money(g.Key.Rate), g.Key.Nature, Money(g.Sum(l => l.Net)), Money(g.Sum(l => l.Vat))))
                .ToArray(),
            xml.Descendants("DatiRiepilogo").Select(r => (Of(r, "AliquotaIVA")!, Of(r, "Natura"), Of(r, "ImponibileImporto")!, Of(r, "Imposta")!)).ToArray());
        return document;
    }

    /// <summary>The PDF of an invoice of the company: the title, the company as issuer with its VAT number and the agency on its behalf, the guest, the totals and the courtesy copy of the e-invoice.</summary>
    private static async Task AssertCompanyPdfAsync(World world, FiscalDocument document, (string Taxable, string Vat, string Total) totals, string payment)
    {
        var content = await world.PdfContentAsync(document.Id);
        Assert.Equal($"Fattura n. {document.Number}", content.Title);
        var text = TextOf(content);
        Assert.Contains(World.OwnerBName, text, StringComparison.Ordinal);
        Assert.Contains($"Partita IVA: {World.OwnerBVat}", text, StringComparison.Ordinal);
        Assert.Contains($"Emessa tramite {World.ItalianAgencyLegalName} per conto del proprietario", text, StringComparison.Ordinal);
        Assert.Contains($"Totale imponibile | {totals.Taxable}", text, StringComparison.Ordinal);
        Assert.Contains($"Totale IVA | {totals.Vat}", text, StringComparison.Ordinal);
        Assert.Contains($"Totale documento | {totals.Total}", text, StringComparison.Ordinal);
        Assert.Contains(payment, text, StringComparison.Ordinal);
        Assert.Contains("Copia di cortesia della fattura elettronica", text, StringComparison.Ordinal);
        AssertNoSupplierName(text);
    }

    /// <summary>
    /// FIX/BE-PORTAL-TAXLINE: the document of a booking of a portal says that the tourist tax the portal did not collect is to collect at the property (12.00), in a paragraph of its own and as the
    /// note of its line; the total of the document is not touched. The scenario records nothing of the collection of the tax in E25 (it sets only the channel rule of the comune, which says the host collects,
    /// and never confirms, declares or collects the tax of a stay), so the whole 12.00 is still to collect: the case of «no record» (a recorded collection comes off it, <c>PortalBookingDocument_*</c>).
    /// </summary>
    private static async Task AssertTheTaxIsToCollectAsync(World world, FiscalDocument document)
    {
        var text = TextOf(await world.PdfContentAsync(document.Id));
        Assert.Contains("Imposta di soggiorno da pagare in struttura: EUR 12,00", text, StringComparison.Ordinal);
        Assert.Contains("Imposta di soggiorno riscossa per conto del comune - da pagare in struttura", text, StringComparison.Ordinal);
        Assert.Contains("Totale documento | 462,00", text, StringComparison.Ordinal);
    }

    /// <summary>The invoice of the agency for the services it charged the guest (the cleaning, 60.00 at 22%): in its own series and in its own name, paid with the stay.</summary>
    private static async Task<FiscalDocument> AssertAgencyServicesInvoiceAsync(World world, Guid booking, string number, DateOnly issuedOn)
    {
        var document = Assert.Single(await world.DocumentsOfAsync(booking), d => d.ForAgencyServices);
        Assert.Equal((FiscalDocumentPurpose.GuestStay, DocumentKind.Invoice, FiscalDocumentStatus.Issued, number, (DateOnly?)issuedOn), (document.Purpose, document.Kind, document.Status, document.Number, document.IssueDate));
        Assert.Equal(("EUR", "it-ordinary-2026.1", FiscalDocumentSdiStatuses.ToSend), (document.Currency, document.RuleVersion, document.SdiStatus));
        Assert.Equal(new[] { (FiscalLineType.Cleaning, 49.18m, 22m, (string?)null, 10.82m) }, LinesOf(document));
        Assert.Equal((49.18m, 10.82m, 60.00m), (document.TaxableBase, document.VatAmount, document.Total));
        Assert.Equal((FiscalPaymentChannel.CasaZen, FiscalDocumentPaymentState.Paid), (document.PaymentChannel, document.PaymentState));
        Assert.Equal(await world.IssuerProfileOfAsync(null), document.IssuerProfileId);

        // The agency issues in its own name: the cedente is the agency, nobody issues for it (no third party), the guest is the cessionario.
        var xml = ValidXml(document.FatturaPaXml);
        var cedente = xml.Descendants("CedentePrestatore").Single();
        Assert.Equal((World.ItalianAgencyVat, World.ItalianAgencyLegalName, "RF01"), (Of(cedente, "IdCodice"), Of(cedente, "Denominazione"), Of(cedente, "RegimeFiscale")));
        Assert.Equal(("TD01", number), (Of(xml, "TipoDocumento"), Of(xml, "Numero")));
        Assert.Empty(xml.Descendants("TerzoIntermediarioOSoggettoEmittente"));
        Assert.Empty(xml.Descendants("SoggettoEmittente"));
        return document;
    }

    /// <summary>B8b: the e-mail of the document queued to the guest with the signed link of the document, and no amount in it.</summary>
    private static async Task AssertTheGuestGotTheDocumentAsync(World world, FiscalDocument document, Guid booking, string kind, string issuerName)
    {
        var mail = Assert.Single(
            world.Emails.Queued,
            m => m.Template == EmailTemplates.Names.FiscalDocumentDelivery && m.Content.Subject == $"{kind} n. {document.Number} di {issuerName}");
        Assert.Equal(await world.GuestEmailOfAsync(booking), mail.To);
        var body = WebUtility.HtmlDecode(mail.Content.HtmlBody);
        Assert.Contains($"{World.PublicSiteBaseUrl}/documents/view/{document.Id:D}?token=", body, StringComparison.Ordinal);
        Assert.DoesNotContain(document.Total.ToString("0.00", CultureInfo.InvariantCulture), body, StringComparison.Ordinal);
        Assert.DoesNotContain(document.Total.ToString("0.00", CultureInfo.GetCultureInfo("it-IT")), body, StringComparison.Ordinal);
    }

    /// <summary>
    /// B8b: the electronic invoice is «to send» and no provider is configured: a submission answers «not configured» and changes nothing (nothing is ever «sent» by itself); the issuer
    /// declares the file it uploaded to the portal of the Agenzia delle Entrate (the day and its reference), and the document is «declared by hand», which no submission changes.
    /// </summary>
    private static async Task AssertTheSdiStatesAsync(World world, FiscalDocument invoice)
    {
        var state = await world.SdiStateOfAsync(invoice.Id);
        Assert.Equal((FiscalDocumentSdiStatuses.ToSend, false, true, true, false), (state.Status, state.ProviderConfigured, state.CanSubmit, state.CanDeclare, state.CanCorrect));
        var submitted = await world.SubmitToSdiAsync(invoice.Id);
        Assert.Equal((SdiSubmitOutcome.NotConfigured, FiscalDocumentSdiStatuses.ToSend), (submitted.Outcome, submitted.Status));
        Assert.Equal(FiscalDocumentSdiStatuses.ToSend, (await world.SdiStateOfAsync(invoice.Id)).Status);

        var declared = await world.DeclareSentToSdiAsync(invoice.Id, invoice.IssueDate!.Value, $"TEST-ADE-{invoice.Number}");
        Assert.Equal((FiscalDocumentSdiStatuses.ManualDeclared, false), (declared.Status, declared.CanSubmit));
        var again = await world.SubmitToSdiAsync(invoice.Id);
        Assert.Equal((SdiSubmitOutcome.NotConfigured, FiscalDocumentSdiStatuses.ManualDeclared), (again.Outcome, again.Status));
        Assert.Equal(FiscalDocumentSdiStatuses.ManualDeclared, (await world.SdiStateOfAsync(invoice.Id)).Status);
    }

    /// <summary>The credit notes of a booking: the TD04 of the invoice of the stay of the company (<paramref name="agency"/> false) or of the cleaning of the agency, issued, whole, linked to its invoice and valid against the schema.</summary>
    private static FiscalDocument AssertCreditNoteOfAnInvoice(IReadOnlyList<FiscalDocument> documents, bool agency, decimal total)
    {
        var note = Assert.Single(documents, d => d.Kind == DocumentKind.CreditNote && d.ForAgencyServices == agency);
        var credited = Assert.Single(documents, d => d.Kind == DocumentKind.Invoice && d.ForAgencyServices == agency);
        Assert.Equal((FiscalDocumentStatus.Issued, (Guid?)credited.Id, total, FiscalDocumentSdiStatuses.ToSend), (note.Status, note.CreditedDocumentId, note.Total, note.SdiStatus));
        Assert.True(credited.CreditedInFull);
        Assert.Equal(credited.Series, note.Series);
        var xml = ValidXml(note.FatturaPaXml);
        Assert.Equal(("TD04", credited.Number), (Of(xml, "TipoDocumento"), Of(xml.Descendants("DatiFattureCollegate").Single(), "IdDocumento")));
        Assert.Equal(PlainLinesOf(credited), PlainLinesOf(note));
        return note;
    }

    // ---------------------------------------------------------------- the month

    /// <summary>What the month of an owner must say (the amounts of the ledger are negative for what is taken from the owner).</summary>
    /// <param name="Commission">The commission of the agency, without its VAT.</param>
    /// <param name="Vat">The VAT of the invoice of the property manager, posted to the ledger.</param>
    private sealed record Month(
        decimal Gross,
        decimal ChannelFees,
        decimal Commission,
        decimal Vat,
        decimal Recharges,
        decimal Closing,
        (FiscalLineType Type, decimal Net, decimal Rate, decimal Vat)[] InvoiceLines,
        (decimal Taxable, decimal Vat, decimal Total) InvoiceTotals,
        string InvoiceNumber);

    /// <summary>The end of October for the two owners: the month posted once, then each version generated, invoiced, approved and published; every figure asserted to the cent.</summary>
    private static async Task<(MonthEnd A, MonthEnd B)> CloseTheMonthAsync(World world, Month forA, Month forB, IReadOnlyList<Guid> cleaningsOfA)
    {
        var months = await world.CloseMonthForAsync(2026, 10, EndOfOctober, world.FlatA, world.FlatB);
        await AssertMonthAsync(world, world.FlatA, months[0], forA, cleaningsOfA);
        await AssertMonthAsync(world, world.FlatB, months[1], forB, []);
        return (months[0], months[1]);
    }

    /// <summary>
    /// C1, C3, C4: the published version (its totals, the lines of the statement), the ledger (the VAT of the invoice posted once), the invoice of the property manager (its lines at
    /// 22%, its totals, its recipient, its FatturaPA valid against the schema and «to send», the cleaning recharged at the cost of its request or by the flat fee of the mandate),
    /// and the owner portal (the statement with its stored PDF, the invoice) with the owner told.
    /// </summary>
    private static async Task AssertMonthAsync(World world, Flat flat, MonthEnd month, Month expected, IReadOnlyList<Guid> cleaningRequests)
    {
        Assert.DoesNotContain(month.Posting.Issues, i => i.Blocking);
        var privateOwner = flat.OwnerId == world.OwnerId || flat.OwnerId == world.CoOwnerId;

        // The version generated again with the VAT of the invoice, approved and published.
        var published = month.Published;
        Assert.NotEqual(month.FirstVersion.Id, published.Id);
        Assert.Equal((OwnerStatementVersionStatus.Published, 2026, 10), (published.Status, published.PeriodYear, published.PeriodMonth));
        Assert.Equal("EUR", published.Currency);
        var totals = OwnerStatementTotals.Of(published);
        Assert.Equal(
            (expected.Gross, expected.ChannelFees, 0m, expected.Commission + expected.Vat, expected.Recharges, 0m, 0m, 0m, expected.Closing),
            (totals.Gross, totals.ChannelFees, totals.Withholding, totals.Commission, totals.Recharges, totals.Corrections, totals.Payments, totals.OpeningBalance, totals.ClosingBalance));

        // The lines of the statement: the stays without the tourist tax, the channel fees, the commission with the VAT of its invoice, the recharges.
        var statement = await world.StatementOfAsync(published.Id);
        Assert.Equal(expected.Gross, statement.Of(OwnerStatementSection.Stays).Sum(l => l.Amount));
        Assert.Equal(expected.ChannelFees, statement.Of(OwnerStatementSection.ChannelFees).Sum(l => l.Amount));
        Assert.Equal(expected.Commission + expected.Vat, statement.Of(OwnerStatementSection.Commission).Sum(l => l.Amount));
        Assert.Equal(expected.Recharges, statement.Of(OwnerStatementSection.Recharges).Sum(l => l.Amount));
        Assert.Equal(expected.InvoiceNumber, statement.OwnerInvoiceNumber);

        // The ledger: the VAT of the invoice posted once, on top of the commission; the cleanings of flat A linked to their requests.
        var ledger = await world.LedgerOfAsync(2026, 10, flat);
        var vat = Assert.Single(ledger, e => e.EntryType == OwnerLedgerEntryType.AgencyVat);
        Assert.Equal((expected.Vat, (Guid?)month.Invoice.Id), (vat.Amount, vat.SourceDocumentId));
        Assert.Equal(expected.Commission, ledger.Where(e => e.EntryType == OwnerLedgerEntryType.AgencyCommission).Sum(e => e.Amount));
        Assert.Equal(expected.Gross, ledger.Where(e => e.EntryType == OwnerLedgerEntryType.BookingRevenue).Sum(e => e.Amount));
        Assert.Equal(expected.Closing, ledger.Sum(e => e.Amount));
        var linked = ledger.Where(e => e.EntryType == OwnerLedgerEntryType.OwnerCharge && e.SourceType == OwnerLedgerSourceType.CleaningRequest).ToList();
        Assert.Equal(cleaningRequests.Order().ToArray(), linked.Select(e => e.SourceId!.Value).Order().ToArray());
        Assert.All(linked, e => Assert.Equal(-50.00m, e.Amount));

        // The invoice of the property manager: in its own series, the VAT of 22% to the cent, a commission line for the flat and a recharge for each cleaning.
        var invoice = month.Invoice;
        Assert.Equal((FiscalDocumentPurpose.OwnerInvoice, DocumentKind.Invoice, FiscalDocumentStatus.Issued, (Guid?)flat.OwnerId), (invoice.Purpose, invoice.Kind, invoice.Status, invoice.RecipientOwnerId));
        Assert.Equal((expected.InvoiceNumber, "EUR", "it-ordinary-2026.1", FiscalDocumentSdiStatuses.ToSend), (invoice.Number, invoice.Currency, invoice.RuleVersion, invoice.SdiStatus));
        Assert.Equal(expected.InvoiceLines, PlainLinesOf(invoice));
        Assert.Equal(expected.InvoiceTotals, (invoice.TaxableBase, invoice.VatAmount, invoice.Total));
        Assert.Equal(-expected.Vat, invoice.VatAmount);
        Assert.Equal(0m, invoice.StampDuty);
        if (cleaningRequests.Count > 0)
        {
            // The cleaning recharged at its cost names its request, never the flat fee of the mandate.
            Assert.All(cleaningRequests, id => Assert.Single(invoice.Lines, l => l.Description.Contains($"richiesta {OwnerLedgerCleaningRecharges.ReferenceOf(id)} del ", StringComparison.Ordinal)));
            Assert.DoesNotContain(invoice.Lines, l => l.Description.StartsWith("Forfait pulizie", StringComparison.Ordinal));
        }
        else
        {
            Assert.DoesNotContain(invoice.Lines, l => l.Description.Contains("richiesta", StringComparison.Ordinal));
        }

        // Who the invoice is made out to: the private owner by its fiscal code, the company by its VAT number; the file is the agency's own (no third party), to send.
        if (privateOwner)
        {
            Assert.Equal((InvoiceRecipientKind.Guest, World.OwnerAFiscalCode, (string?)null), (invoice.RecipientKind, invoice.RecipientFiscalCode, invoice.RecipientVatNumber));
        }
        else
        {
            Assert.Equal(
                (InvoiceRecipientKind.Company, World.OwnerBName, World.OwnerBVat, World.OwnerBVat),
                (invoice.RecipientKind, invoice.RecipientName, invoice.RecipientVatNumber, invoice.RecipientFiscalCode));
        }

        var xml = ValidXml(invoice.FatturaPaXml);
        var cessionario = xml.Descendants("CessionarioCommittente").Single();
        Assert.Equal(("TD01", expected.InvoiceNumber, Money(expected.InvoiceTotals.Total)), (Of(xml, "TipoDocumento"), Of(xml, "Numero"), Of(xml, "ImportoTotaleDocumento")));
        Assert.Equal(World.ItalianAgencyVat, Of(xml.Descendants("CedentePrestatore").Single(), "IdCodice"));
        Assert.Empty(xml.Descendants("TerzoIntermediarioOSoggettoEmittente"));
        Assert.Equal(privateOwner ? World.OwnerAFiscalCode : World.OwnerBVat, Of(cessionario, privateOwner ? "CodiceFiscale" : "IdCodice"));
        Assert.Equal(
            (Money(22m), Money(expected.InvoiceTotals.Taxable), Money(expected.InvoiceTotals.Vat)),
            Assert.Single(xml.Descendants("DatiRiepilogo").Select(r => (Of(r, "AliquotaIVA")!, Of(r, "ImponibileImporto")!, Of(r, "Imposta")!))));
        var pdf = await world.PdfContentAsync(invoice.Id);
        Assert.Equal($"Fattura n. {expected.InvoiceNumber}", pdf.Title);

        // The owner portal: the published statement with its totals and the PDF stored at the publication, the invoice; the owner was told.
        var listed = Assert.Single(await world.PortalStatementsAsync(flat));
        Assert.Equal((published.Id, true, false), (listed.StatementId, listed.HasPdf, listed.Superseded));
        Assert.Equal(totals, listed.Totals);
        var download = await world.PortalStatementPdfAsync(published.Id, flat);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(download, 0, 4));
        Assert.Equal(await world.StoredStatementPdfAsync(published.Id), download);
        var portalInvoice = Assert.Single(await world.PortalInvoicesAsync(flat));
        Assert.Equal((invoice.Id, invoice.Total, "EUR", expected.InvoiceNumber), (portalInvoice.InvoiceId, portalInvoice.Total, portalInvoice.Currency, portalInvoice.Number));
        var ownerEmail = flat.OwnerId == world.OwnerId ? World.OwnerAEmail : flat.OwnerId == world.OwnerBId ? World.OwnerBEmail : World.CoOwnerEmail;
        Assert.Single(world.Emails.Queued, m => m.Template == EmailTemplates.Names.OwnerStatementPublished && m.To == ownerEmail);
    }

    // ---------------------------------------------------------------- the self-billed invoices (TD17)

    /// <summary>
    /// C2 (V1): the invoices TD17 of October at the beginning of November: the commission of the portal the company holds the account of is integrated by the company, through the
    /// agency (a TD17 of the month in its own series); the private owner has none (the portal charged the Italian VAT) and the commission of its stay is reported, not invoiced.
    /// </summary>
    private static async Task AssertSelfBilledOfTheCompanyAsync(World world, decimal taxable, decimal vat, decimal total, string number)
    {
        var (generation, issued) = await world.SelfBillMonthAsync(2026, 10, NovemberThird);
        Assert.True(generation.LedgerPosted);
        Assert.Empty(generation.Failed);
        var notDue = Assert.Single(generation.NotDue);
        Assert.Equal((SelfBillingNotDueReasons.PrivateHolder, "BookingCom", 1, taxable, "EUR"), (notDue.Reason, notDue.Channel, notDue.Stays, notDue.Amount, notDue.Currency));
        var draft = Assert.Single(generation.Drafts);
        Assert.Equal((SelfBillingHolderKind.Owner, (Guid?)world.OwnerBId, 0m), (draft.HolderKind, draft.HolderOwnerId, draft.EstimatedAmount));

        var invoice = Assert.Single(issued);
        var document = invoice.Document;
        Assert.Equal((FiscalDocumentPurpose.SelfBilling, FiscalDocumentStatus.Issued, number, "EUR", FiscalDocumentSdiStatuses.ToSend), (document.Purpose, document.Status, document.Number, document.Currency, document.SdiStatus));
        Assert.Equal((taxable, vat, total), (document.TaxableBase, document.VatAmount, document.Total));
        Assert.Equal((SelfBillingHolderKind.Owner, World.OwnerBName, "Booking.com B.V.", Day(11, 15)), (invoice.Row.HolderKind, invoice.Row.HolderName, invoice.Row.PortalName, invoice.Row.DueDate));

        // The file turns the parties round: the portal (the Netherlands) is the cedente, the company the cessionario, issued by the recipient (CC) and sent by the agency.
        var xml = ValidXml(document.FatturaPaXml);
        Assert.Equal(("TD17", number, "CC"), (Of(xml, "TipoDocumento"), Of(xml, "Numero"), Of(xml, "SoggettoEmittente")));
        Assert.Equal(("NL", "805734958B01"), (Of(xml.Descendants("CedentePrestatore").Single(), "IdPaese"), Of(xml.Descendants("CedentePrestatore").Single(), "IdCodice")));
        Assert.Equal(World.OwnerBVat, Of(xml.Descendants("CessionarioCommittente").Single(), "IdCodice"));
        Assert.Equal((Money(22m), Money(taxable), Money(vat)), Assert.Single(xml.Descendants("DatiRiepilogo").Select(r => (Of(r, "AliquotaIVA")!, Of(r, "ImponibileImporto")!, Of(r, "Imposta")!))));

        // The list of the screen reads the same invoice.
        var listed = Assert.Single(await world.SelfBilledInvoicesAsync(2026, 10));
        Assert.Equal((document.Id, SelfBillingHolderKind.Owner, taxable, vat, total), (listed.Id, listed.HolderKind, listed.TaxableBase, listed.VatAmount, listed.Total));
    }

    /// <summary>No TD17 in a month where no portal charged a commission to an owner that integrates it: nothing to make, nothing left out.</summary>
    private static async Task AssertNoSelfBillingAsync(World world)
    {
        var (generation, issued) = await world.SelfBillMonthAsync(2026, 10, NovemberThird);
        Assert.Empty(generation.Drafts);
        Assert.Empty(generation.Failed);
        Assert.Empty(generation.NotDue);
        Assert.Empty(issued);
    }

    // ---------------------------------------------------------------- the payout, the withholding, the wire and the year end

    /// <summary>What the payout of an owner must say: the base and the amount withheld (the base is <c>null</c> when nothing is), the net paid, and the reason when nothing is withheld.</summary>
    private sealed record Payout(decimal? Base, decimal Withheld, decimal Net, string Reason);

    /// <summary>
    /// C5, C6: the agency pays each owner what its published statement owes it, on the second of November; the withholding is decided on the facts of the ledger (the amount, the code,
    /// the rule, or the reason) and a retry with the same key is the same payout; then the wire file of the two payouts (<c>pain.001.001.09</c>, euro), checked against the stand-in
    /// schema of the tests (the official one is not in the repository: <see cref="Wire_ThePayoutFile_ValidatesAgainstTheOfficialIsoSchema"/>).
    /// </summary>
    private static async Task<(OwnerPayoutResult A, OwnerPayoutResult B)> PayTheOwnersAsync(World world, (MonthEnd A, MonthEnd B) months, Payout forA, Payout forB)
    {
        world.At(World.InRome(PayDay, 10));
        var a = await AssertPayoutAsync(world, world.FlatA, months.A, forA);
        var b = await AssertPayoutAsync(world, world.FlatB, months.B, forB);

        var (batch, xml) = await world.WireAsync([a.Payout.Id, b.Payout.Id], PayDay.AddDays(1), _ => Pain001TestSchemas.Accepting());
        var total = forA.Net + forB.Net;
        Assert.Equal((total, 2, "EUR"), (batch.TotalAmount, batch.TransferCount, batch.Currency));
        XNamespace iso = Pain001SchemaProvider.IsoNamespace;
        var document = XDocument.Parse(xml);
        Assert.Equal(iso, document.Root!.Name.Namespace);
        Assert.All(document.Descendants(iso + "CtrlSum"), sum => Assert.Equal(Money(total), sum.Value));
        Assert.Equal(World.ItalianAgencyIban, document.Descendants(iso + "DbtrAcct").Single().Descendants(iso + "IBAN").Single().Value);
        foreach (var (iban, net) in new[] { (World.OwnerAIban, forA.Net), (World.OwnerBIban, forB.Net) })
        {
            var transfer = Assert.Single(document.Descendants(iso + "CdtTrfTxInf"), t => t.Descendants(iso + "CdtrAcct").Single().Descendants(iso + "IBAN").Single().Value == iban);
            var instructed = transfer.Descendants(iso + "InstdAmt").Single();
            Assert.Equal((Money(net), "EUR"), (instructed.Value, (string?)instructed.Attribute("Ccy")));
            Assert.Equal("Rendiconto 10/2026", transfer.Descendants(iso + "Ustrd").Single().Value);
        }

        return (a, b);
    }

    private static async Task<OwnerPayoutResult> AssertPayoutAsync(World world, Flat flat, MonthEnd month, Payout expected)
    {
        var result = await world.PayTheOwnerWithEvidenceAsync(month.Published, PayDay, flat);
        var payout = result.Payout;
        Assert.False(result.Replayed);
        Assert.Equal((OwnerPayoutStatus.Confirmed, month.Published.ClosingBalance, expected.Withheld, expected.Net, "EUR"), (payout.Status, payout.GrossAmount, payout.WithholdingAmount, payout.NetAmount, payout.Currency));
        Assert.Equal(payout.GrossAmount - payout.WithholdingAmount, payout.NetAmount);

        var evidence = Assert.Single(result.Evidence);
        Assert.Equal(("IT-STR-WITHHOLDING", "2026.1"), (evidence.RuleCode, evidence.RuleVersion));
        if (expected.Base is { } baseAmount)
        {
            Assert.Equal(
                (TaxDecision.Applicable, "applicable", (decimal?)baseAmount, (decimal?)21m, (decimal?)expected.Withheld, "1919"),
                (evidence.Decision, evidence.ReasonCode, evidence.GrossBase, evidence.RatePercent, evidence.Amount, evidence.TaxCode));
        }
        else
        {
            Assert.Equal((TaxDecision.NotApplicable, expected.Reason), (evidence.Decision, evidence.ReasonCode));
            Assert.Null(evidence.Amount);
        }

        // A retry of the same payout (the same key) finds it: the same payout and the same evidence, never decided, nor withheld, twice.
        var again = await world.PayTheOwnerWithEvidenceAsync(month.Published, PayDay, flat);
        Assert.Equal((true, payout.Id), (again.Replayed, again.Payout.Id));
        Assert.Equal(result.Evidence.Select(e => e.Id), again.Evidence.Select(e => e.Id));
        return result;
    }

    /// <summary>The stays a payout decided, as the facts say it: the channel, how the money was collected, whether it is in the base of the withholding and why, and its gross without the tourist tax; in the order of the stays.</summary>
    private static void AssertTheFactsOfTheStays(OwnerPayoutResult payout, params (BookingSource Channel, string Collection, bool InBase, string Reason, decimal Gross)[] expected)
    {
        var facts = PayoutFactRules.FromJson(Assert.Single(payout.Evidence).FactsJson)!;
        Assert.Equal(
            expected.Select(e => (e.Channel.ToString(), e.Collection, e.InBase, e.Reason, e.Gross)).ToArray(),
            facts.Stays.Select(s => (s.Channel, s.Collection, s.InBase, s.Reason, s.Gross)).ToArray());
    }

    /// <summary>C5: the F24 of November, the CU and the 770 of 2026, and what the owner portal says, for the withholding of the private owner (code 1919, 21%), once.</summary>
    private static async Task AssertWithholdingAsync(World world, decimal baseAmount, decimal withheld, decimal payoutGross, decimal payoutNet)
    {
        // The F24 of the month the payout was paid in: one line, tax code 1919, due on the 16th of the next month; the breakdown per owner and payout adds up to it.
        var f24 = await world.F24DataAsync(2026, 11);
        var line = Assert.Single(f24.Lines);
        Assert.Equal(("1919", 11, 2026, withheld, Day(12, 16), 1), (line.TaxCode, line.ReferenceMonth, line.ReferenceYear, line.Amount, line.DueDate, line.EvidenceCount));
        var row = Assert.Single(f24.Breakdown);
        Assert.Equal(
            (world.OwnerId, World.OwnerAName, false, "1919", baseAmount, (decimal?)21m, withheld, "EUR", payoutGross, withheld, payoutNet),
            (row.OwnerId, row.OwnerName, row.Reversal, row.TaxCode, row.GrossBase, row.RatePercent, row.Amount, row.PayoutCurrency, row.PayoutGross, row.PayoutWithheld, row.PayoutNet));
        var record = await world.GenerateF24Async(2026, 11);
        Assert.Equal((withheld, "1919", TaxFilingState.Generated, Day(12, 16)), (record.TotalAmount, record.TaxCode, record.FilingState, DateOnly.FromDateTime(record.DueDate)));

        // The year end: the CU of the owner (its base and its withholding, who it is made out to), the 770 (the F24 of the month with its figures) and the portal of the owner.
        world.At(World.InRome(YearEnd, 10));
        var cu = await world.CuOfTheYearAsync(2026);
        var cuRecord = Assert.Single(cu.Records);
        Assert.Equal((world.OwnerId, CuAttributions.Owner, baseAmount, withheld, 1, "1919"), (cuRecord.OwnerId, cuRecord.Attribution, cuRecord.GrossBase, cuRecord.Withheld, cuRecord.Evidence, cuRecord.TaxCode));
        Assert.Equal(new[] { "IT-STR-WITHHOLDING@2026.1" }, cuRecord.RuleVersions);
        Assert.Equal((World.OwnerAName, World.OwnerAFiscalCode, "IT"), (cuRecord.Recipient!.Name, cuRecord.Recipient.FiscalCode, cuRecord.Recipient.Country));
        Assert.Empty(cuRecord.Recipient.Missing);
        Assert.Equal((baseAmount, withheld), (cuRecord.Lines.Sum(l => l.GrossBase), cuRecord.Lines.Sum(l => l.Withheld)));
        Assert.Equal((World.FlatAName, 1), (Assert.Single(cuRecord.Properties).Name, cuRecord.Properties.Count));

        var tax770 = await world.Tax770Async(2026);
        var month = Assert.Single(tax770.Lines);
        Assert.Equal((11, "1919", baseAmount, withheld, 1, (decimal?)withheld, (DateOnly?)Day(12, 16), (DateOnly?)null, (TaxFilingState?)TaxFilingState.Generated), (month.Month, month.TaxCode, month.GrossBase, month.Withheld, month.EvidenceCount, month.F24Amount, month.F24DueDate, month.F24PaidOn, month.FilingState));

        // The files of the year as the spreadsheet of an accountant set to Italian reads them (a decimal comma, a day as dd/MM/yyyy): the CU (who it is made out to, the base and the
        // withholding with the tax code, the evidence, and the stays that add up to them) and the 770 (the month, the F24 with its due date and no difference).
        var texts = new ResxDocumentTexts();
        var cuRows = CsvRows(Text(TaxReportingExports.CuCsv(2026, cu.Records, texts, DocumentLanguages.Italian)));
        var cuRecipient = Assert.Single(cuRows, r => r.Count == 22 && r[4] == World.OwnerAFiscalCode);
        Assert.Equal((World.OwnerAName, Euro(baseAmount), Euro(withheld), "1919", "1"), (cuRecipient[0], cuRecipient[14], cuRecipient[15], cuRecipient[16], cuRecipient[20]));
        var cuStays = cuRows.Where(r => r.Count == 18 && r[1] == World.OwnerAFiscalCode).ToList();
        Assert.Equal((World.FlatAName, Euro(baseAmount), Euro(withheld)), (Assert.Single(cuStays.Select(r => r[2]).Distinct()), Euro(cuStays.Sum(r => Amount(r[16]))), Euro(cuStays.Sum(r => Amount(r[17])))));
        var row770 = Assert.Single(CsvRows(Text(TaxReportingExports.Tax770Csv(tax770, texts, DocumentLanguages.Italian))), r => r.Count == 13 && r[0] == "11");
        Assert.Equal(
            ("2026", "1919", Euro(baseAmount), Euro(withheld), Euro(withheld), "16/12/2026", string.Empty, Euro(0m), "1"),
            (row770[1], row770[2], row770[3], row770[4], row770[5], row770[6], row770[7], row770[11], row770[12]));

        var portal = await world.PortalWithholdingAsync(world.FlatA, 2026);
        Assert.Equal((2026, "EUR", withheld), (portal.Year, portal.Currency, portal.TotalWithheld));
        Assert.Equal((11, baseAmount, withheld), (Assert.Single(portal.Details).Month, Assert.Single(portal.Details).GrossBase, Assert.Single(portal.Details).AmountWithheld));
    }

    /// <summary>D1: the agency withheld nothing in the year: there is no CU, no 770 and no F24 to make, and the exports say so (422), never a file of zeros.</summary>
    private static async Task AssertNothingWithheldAsync(World world)
    {
        world.At(World.InRome(YearEnd, 10));
        foreach (var ask in new Func<Task>[] { () => world.CuOfTheYearAsync(2026), () => world.Tax770Async(2026), () => world.F24DataAsync(2026, 11) })
        {
            var refusal = await Assert.ThrowsAsync<DomainRuleException>(ask);
            Assert.Equal(OwnerPayoutErrors.NoObligations, refusal.Code);
        }
    }

    /// <summary>
    /// D1: the contracts of the year to communicate by 30 June, the qualifying short lets of a private owner that the agency handled and did not withhold on, with the lessor, the
    /// property, the days, the consideration without the tourist tax and the reason; none for a company, and none for a stay the agency withheld on.
    /// </summary>
    private static async Task AssertContractsAsync(World world, params (Guid Booking, BookingSource Channel, DateOnly CheckIn, DateOnly CheckOut, int Nights, decimal Consideration, string Reason)[] expected)
    {
        var communication = await world.ContractsOfTheYearAsync(2026);
        Assert.Equal(2026, communication.Year);
        Assert.Equal(
            expected.Select(e => (e.Booking, e.Channel.ToString(), e.CheckIn, e.CheckOut, e.Nights, "EUR", e.Consideration, e.Reason)).ToArray(),
            communication.Rows.Select(r => (r.BookingId, r.Channel, r.CheckIn, r.CheckOut, r.Nights, r.Currency, r.Consideration, r.Reason)).ToArray());
        Assert.All(
            communication.Rows,
            row => Assert.Equal((world.OwnerId, World.OwnerAName, World.OwnerAFiscalCode, World.FlatAName), (row.LessorId, row.Lessor.Name, row.Lessor.FiscalCode, row.Property!.Name)));

        // The file of the communication: a row for each contract, the lessor with its fiscal code, the property, the dates, the nights, the channel and the consideration.
        var rows = CsvRows(Text(TaxReportingExports.ContractsCsv(communication, new ResxDocumentTexts(), DocumentLanguages.Italian)))
            .Where(r => r.Count == 25 && r[1] == World.OwnerAFiscalCode)
            .ToList();
        Assert.Equal(
            expected.Select(e => (World.OwnerAName, World.FlatAName, DayText(e.CheckIn), DayText(e.CheckOut), Whole(e.Nights), e.Channel.ToString(), "EUR", Euro(e.Consideration))).ToArray(),
            rows.Select(r => (r[0], r[8], r[17], r[18], r[19], r[20], r[21], r[22])).ToArray());
    }

    /// <summary>
    /// C9, D3: the tourist tax of the year per comune (the stays, the nights, the guests, what is owed by the stays), paid to the comune on 8 January and recorded: nothing is left
    /// to pay, and the account of the agent says so by quarter and for the year, with the payment.
    /// </summary>
    private static async Task AssertTouristTaxAsync(World world, int stays, int nights, int guests, decimal owed)
    {
        world.At(World.InRome(YearEnd, 10));
        var before = Assert.Single((await world.TouristTaxDeclarationAsync(2026)).Comuni);
        Assert.Equal(
            (World.ComuneName, stays, nights, guests, owed, (decimal?)0m, (decimal?)owed, (TouristTaxSummaryStatus?)TouristTaxSummaryStatus.ToPay),
            (before.Summary.Comune, before.Summary.Stays, before.Summary.Nights, before.Summary.Guests, before.Summary.Owed, before.Summary.Paid, before.Summary.Balance, before.Summary.Status));
        Assert.Equal((stays, 0), (before.TaxedStays, before.Summary.StaysWithoutAmount));

        var remittance = await world.RemitTouristTaxAsync(Day(10, 1), Day(10, 31), owed, new DateOnly(2027, 1, 8), "TEST-F24ELIDE-2026");
        Assert.Equal((World.ComuneName, owed, new DateOnly(2027, 1, 8)), (remittance.Comune, remittance.Amount, remittance.PaidOn));

        var after = Assert.Single((await world.TouristTaxDeclarationAsync(2026)).Comuni);
        Assert.Equal((owed, (decimal?)owed, (decimal?)0m, (TouristTaxSummaryStatus?)TouristTaxSummaryStatus.Paid), (after.Summary.Owed, after.Summary.Paid, after.Summary.Balance, after.Summary.Status));
        var account = await world.TouristTaxAccountAsync(2026, TouristTaxAccountPeriod.Quarter);
        var quarter = Assert.Single(account.Rows, r => !r.IsYear && r.Period == "2026-Q4");
        Assert.Equal((stays, owed, owed, 0m), (quarter.Stays, quarter.Collected, quarter.Remitted, quarter.Balance));
        var year = Assert.Single(account.Rows, r => r.IsYear);
        Assert.Equal((stays, owed, owed, 0m), (year.Stays, year.Collected, year.Remitted, year.Balance));
        Assert.Equal(("TEST-F24ELIDE-2026", owed), (Assert.Single(account.Remittances).Reference, Assert.Single(account.Remittances).Amount));

        // The declaration as a file: the comune with its stays, nights, guests and the stays taxed, what is owed, remitted and the difference (nothing), and the payment with its reference.
        var rows = CsvRows(Text(TouristTaxYearEndExports.DeclarationCsv(await world.TouristTaxDeclarationAsync(2026), new ResxDocumentTexts(), DocumentLanguages.Italian)));
        var comune = Assert.Single(rows, r => r.Count == 15 && r[0] == World.ComuneName);
        Assert.Equal(
            new[] { Whole(stays), Whole(nights), Whole(guests), Whole(stays), "0", Euro(owed), Euro(owed), Euro(0m) },
            new[] { comune[1], comune[2], comune[3], comune[4], comune[5], comune[10], comune[11], comune[12] });
        Assert.Contains(rows, r => r.Contains("TEST-F24ELIDE-2026") && r.Contains(Euro(owed)));
    }

    // ---------------------------------------------------------------- the portal of the owners and the package of the accountant

    /// <summary>
    /// C8, D2: what the company reads in its portal and exports for its own accountant: the documents issued in its name (the four invoices, the one declared by hand marked), the
    /// ZIP of the year with their PDF and XML (valid against the schema), their register and the invoices of the agency with their recharges; never a receipt of the private owner,
    /// an invoice of the agency for the cleaning or a contact of a guest.
    /// </summary>
    private static async Task AssertTheCompanyPortalAsync(World world, IReadOnlyList<FiscalDocument> invoices)
    {
        var listed = await world.PortalIssuedDocumentsAsync(world.FlatB);
        Assert.Equal(invoices.Select(i => i.Id).Order().ToArray(), listed.Select(d => d.DocumentId).Order().ToArray());
        Assert.All(listed, d => Assert.Equal((DocumentKind.Invoice, "EUR", true, "TEST Agenzia Como"), (d.Kind, d.Currency, d.HasXml, d.AgencyName)));
        Assert.Equal(new[] { "SRL2026/4", "SRL2026/3", "SRL2026/2", "SRL2026/1" }, listed.Select(d => d.Number).ToArray());
        Assert.Equal(FiscalDocumentSdiStatuses.ManualDeclared, listed.Single(d => d.Number == "SRL2026/1").SdiStatus);
        Assert.All(listed.Where(d => d.Number != "SRL2026/1"), d => Assert.Equal(FiscalDocumentSdiStatuses.ToSend, d.SdiStatus));

        var files = await world.PortalIssuedPackageAsync(world.FlatB, Year2026);
        Assert.Equal(4, files.Keys.Count(k => k.StartsWith("documenti-emessi/", StringComparison.Ordinal) && k.EndsWith(".pdf", StringComparison.Ordinal)));
        Assert.Equal(4, files.Keys.Count(k => k.StartsWith("documenti-emessi/", StringComparison.Ordinal) && k.EndsWith(".xml", StringComparison.Ordinal)));
        Assert.Single(files.Keys, k => k.StartsWith("fatture-agenzia/", StringComparison.Ordinal) && k.EndsWith(".pdf", StringComparison.Ordinal));
        Assert.Single(files.Keys, k => k.StartsWith("fatture-agenzia/", StringComparison.Ordinal) && k.EndsWith(".xml", StringComparison.Ordinal));
        Assert.Contains(files.Keys, k => k.StartsWith("riepilogo-annuale/", StringComparison.Ordinal));
        AssertEveryXmlIsValid(files, 5);
        AssertTheIndexOf(files);

        // The register adds up to the four invoices; no contact of a guest is in any file.
        var register = Text(files["documenti-emessi/registro-it-09876543217.csv"]);
        foreach (var number in new[] { "SRL2026/1", "SRL2026/2", "SRL2026/3", "SRL2026/4" })
            Assert.Contains(number, register, StringComparison.Ordinal);

        Assert.DoesNotContain("RIC2026", register, StringComparison.Ordinal);
        foreach (var (path, content) in files.Where(f => !f.Key.EndsWith(".pdf", StringComparison.Ordinal) && !f.Key.EndsWith(".xlsx", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("@guest.booking.com", Text(content), StringComparison.Ordinal);
            Assert.DoesNotContain("@example.com", Text(content), StringComparison.Ordinal);
            Assert.False(path.Contains("RIC2026", StringComparison.Ordinal), path);
        }
    }

    /// <summary>The private owner reads in its portal only the receipts issued in its name, in the order of the numbers (no XML), and never a document of the company.</summary>
    private static async Task AssertThePortalOfThePrivateOwnerAsync(World world, IReadOnlyList<FiscalDocument> receipts)
    {
        var listed = await world.PortalIssuedDocumentsAsync(world.FlatA);
        Assert.Equal(new[] { "RIC2026/4", "RIC2026/3", "RIC2026/2", "RIC2026/1" }, listed.Select(d => d.Number).ToArray());
        Assert.Equal(receipts.Select(r => r.Id).Order().ToArray(), listed.Select(d => d.DocumentId).Order().ToArray());
        Assert.All(listed, d => Assert.Equal((DocumentKind.Receipt, FiscalDocumentSdiStatuses.NotApplicable, false), (d.Kind, d.SdiStatus, d.HasXml)));
    }

    /// <summary>
    /// C7, C8: the monthly package of the accountant for October, file by file: the documents of the guests of the three issuers (PDF and, for an invoice, the XML), the invoices of
    /// the property manager with theirs, the statements, the VAT of each issuer by rate and nature, the index that lists every file and no other with the SHA-256 of its bytes, and
    /// every Italian XML valid against the official schema; then the year, with the withholding, the F24 and the CU.
    /// </summary>
    private static async Task AssertPackageAsync(World world)
    {
        var files = await world.PackageAsync(October);
        AssertTheIndexOf(files);

        // The documents of the guests: 4 receipts, 4 invoices of the company, 2 invoices of the agency; XML for each invoice (6) and the 2 of the property manager: 8, all valid.
        Assert.Equal(10, files.Keys.Count(k => k.StartsWith("documenti-ospiti/", StringComparison.Ordinal) && k.EndsWith(".pdf", StringComparison.Ordinal)));
        Assert.Equal(2, files.Keys.Count(k => k.StartsWith("fatture-proprietari/", StringComparison.Ordinal) && k.EndsWith(".pdf", StringComparison.Ordinal)));
        Assert.Equal(2, files.Keys.Count(k => k.StartsWith("rendiconti/", StringComparison.Ordinal) && k.EndsWith(".pdf", StringComparison.Ordinal)));
        AssertEveryXmlIsValid(files, 8);

        // The VAT of each issuer: the agency (its invoices and the property manager's), the company (10% and the tax N1) and the private owner (receipts outside the VAT).
        var agency = Text(files["iva/iva-it-01234567897.csv"]);
        Assert.Contains("2026-10;Fatture ordinarie;22,00;;EUR;1202,36;264,52;1466,88;4", agency, StringComparison.Ordinal);
        var company = Text(files["iva/iva-it-09876543217.csv"]);
        Assert.Contains("2026-10;Fatture ordinarie;10,00;;EUR;1363,64;136,36;1500,00;4", company, StringComparison.Ordinal);
        Assert.Contains("2026-10;Fatture ordinarie;0,00;N1;EUR;48,00;0,00;48,00;4", company, StringComparison.Ordinal);
        var owner = Text(files["iva/iva-it-rssmra80a01h501u.csv"]);
        Assert.Contains("2026-10;Ricevute;0,00;N2.2;EUR;1500,00;0,00;1500,00;4", owner, StringComparison.Ordinal);
        Assert.Contains("2026-10;Ricevute;0,00;N1;EUR;48,00;0,00;48,00;4", owner, StringComparison.Ordinal);

        // The month of the payout (November) has the withholding, its F24 and the CU of the year.
        var november = await world.PackageAsync(AccountantPackagePeriod.OfMonth(2026, 11));
        Assert.Contains("TEST Mario Rossi;2026-11;1919;1;1110,00;233,10", Text(november["ritenute/ritenute-proprietari.csv"]), StringComparison.Ordinal);
        Assert.Contains("2026-11;1919;233,10;16/12/2026;;generato, non trasmesso;IT-STR-WITHHOLDING@2026.1", Text(november["ritenute/f24.csv"]), StringComparison.Ordinal);
        var year = await world.PackageAsync(Year2026);
        Assert.Contains("TEST Mario Rossi;1110,00;233,10;1", Text(year["ritenute/cu-2026.csv"]), StringComparison.Ordinal);
        AssertEveryXmlIsValid(year, 8);
    }

    /// <summary>The index of the ZIP lists every file and no other, with the SHA-256 of its bytes.</summary>
    private static void AssertTheIndexOf(Dictionary<string, byte[]> files)
    {
        var index = IndexOf(files[AccountantPackageStructure.IndexFile]);
        Assert.Equal(
            files.Keys.Where(k => k != AccountantPackageStructure.IndexFile).Order(StringComparer.Ordinal),
            index.Select(e => e.Path).Order(StringComparer.Ordinal));
        foreach (var (path, sha256) in index)
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(files[path])), sha256);
    }

    // ---------------------------------------------------------------- readers

    private static DateOnly Day(int month, int day) => new(2026, month, day);

    private static (FiscalLineType Type, decimal Net, decimal Rate, string? Nature, decimal Vat)[] LinesOf(FiscalDocument document) =>
        document.Lines.OrderBy(l => l.LineNumber).Select(l => (l.LineType, l.TotalPrice, l.VatRatePercent, l.VatNatureCode, l.VatAmount ?? 0m)).ToArray();

    private static (FiscalLineType Type, decimal Net, decimal Rate, decimal Vat)[] PlainLinesOf(FiscalDocument document) =>
        document.Lines.OrderBy(l => l.LineNumber).Select(l => (l.LineType, l.TotalPrice, l.VatRatePercent, l.VatAmount ?? 0m)).ToArray();

    /// <summary>An amount as the schema writes it: two decimals, invariant culture.</summary>
    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>An amount as the CSV of the exports writes it, for a spreadsheet set to Italian: two decimals, a decimal comma and no thousands separator.</summary>
    private static string Euro(decimal value) => value.ToString("0.00", CultureInfo.GetCultureInfo("it-IT"));

    /// <summary>The amount a cell of those CSV files holds.</summary>
    private static decimal Amount(string cell) => decimal.Parse(cell, NumberStyles.Number, CultureInfo.GetCultureInfo("it-IT"));

    /// <summary>A day as the CSV of the exports writes it.</summary>
    private static string DayText(DateOnly day) => day.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>A whole number as the CSV of the exports writes it.</summary>
    private static string Whole(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>The FatturaPA of a document, parsed after it passed the official schema (a document with no XML, or one that does not validate, fails the test).</summary>
    private static XDocument ValidXml(string? xml)
    {
        Assert.False(string.IsNullOrEmpty(xml));
        Assert.Empty(XsdValidator.Validate(xml!, new FatturaPaSchemaProvider().GetSchemas()));
        return XDocument.Parse(xml!);
    }

    /// <summary>The text of the first element of that name below <paramref name="parent"/>; <c>null</c> when there is none.</summary>
    private static string? Of(XContainer parent, string name) => parent.Descendants(name).FirstOrDefault()?.Value;

    /// <summary>AC3: every XML of a ZIP is a FatturaPA valid against the official schema, and the ZIP has exactly the XML files it should.</summary>
    private static void AssertEveryXmlIsValid(Dictionary<string, byte[]> files, int expected)
    {
        var xmls = files.Where(f => f.Key.EndsWith(".xml", StringComparison.Ordinal)).ToList();
        Assert.Equal(expected, xmls.Count);
        var schemas = new FatturaPaSchemaProvider().GetSchemas();
        foreach (var (path, content) in xmls)
            Assert.True(XsdValidator.Validate(Text(content), schemas).Count == 0, $"{path} does not validate against the official schema");
    }

    /// <summary>The name of a supplier of CasaZen is never on a document of a guest.</summary>
    private static void AssertNoSupplierName(string text)
    {
        foreach (var name in new[] { "Booking", "Octorate", "Stripe", "Airbnb" })
            Assert.DoesNotContain(name, text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every text of a PDF content: the title, the headings, the paragraphs and the cells of the tables (a row is its cells joined by <c>|</c>), one per line.</summary>
    private static string TextOf(PdfDocumentContent content) =>
        string.Join('\n', new[] { content.Title }.Concat(content.Blocks.SelectMany(TextsOf)));

    private static IEnumerable<string> TextsOf(PdfBlock block) => block switch
    {
        PdfHeading heading => [heading.Text],
        PdfParagraph paragraph => [paragraph.Text],
        PdfTable table => table.Columns.Select(c => c.Header).Concat(table.Rows.Select(r => string.Join(" | ", r))),
        _ => [],
    };

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart((char)0xFEFF);

    /// <summary>The files of the index of the package (its first table: the path, the description, the rows and the SHA-256 of each file).</summary>
    private static List<(string Path, string Sha256)> IndexOf(byte[] bytes)
    {
        var rows = CsvRows(Text(bytes));
        var entries = new List<(string Path, string Sha256)>();

        // The title of the table and its header come first; the first empty row ends the table.
        foreach (var row in rows.Skip(2))
        {
            if (row.Count == 1 && row[0].Length == 0)
                break;

            entries.Add((row[0], row[3]));
        }

        return entries;
    }

    /// <summary>The rows of a CSV of the package: <c>;</c> between the cells, quoted cells with the quotes doubled, a line break after each row.</summary>
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

/// <summary>
/// A fact that needs the official schema of the SEPA wire file (<c>pain.001.001.09.xsd</c>, <see cref="Pain001SchemaProvider"/>): it is not in the repository until an operator adds it
/// with its checksum (docs/runbooks/wire-transfers.md), so until then the test is reported as skipped, with the reason, everywhere.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class IsoWireSchemaFactAttribute : FactAttribute
{
    public IsoWireSchemaFactAttribute()
    {
        if (!Pain001SchemaProvider.HasOfficialSchema(WireTransferErrors.SepaCurrency))
        {
            Skip = "The official schema pain.001.001.09.xsd is not in the repository (Casazen.Infrastructure/Payments/Schemas/Iso): "
                + "the wire file is validated against the stand-in schema of the tests only; see docs/runbooks/wire-transfers.md to add it.";
        }
    }
}
