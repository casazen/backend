using System.Reflection;
using System.Security.Claims;
using System.Xml.Linq;
using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.Fiscal;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using AuthorizeAttribute = Microsoft.AspNetCore.Authorization.AuthorizeAttribute;
using CasazenPolicies = Casazen.Web.Authorization.CasazenPolicies;
using FeatureGateAttribute = Casazen.Web.Infrastructure.FeatureGateAttribute;
using FiscalDocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E17/F01/US03/T01 (NF-116, D4, ⚖ I10): the self-billed invoice (TD17) of the commissions that foreign portals charge an Italian account holder. The taxable amount is the
/// month's channel fees of the listings the holder holds and the VAT is integrated at the rate of the settings; only a holder that needs one gets one; the issue numbers it in
/// the series of the holder and writes a valid TD17 (the supplier is the cedente, the holder the cessionario that issues it); a draft is issued only while it is what the
/// ledger says; another organization finds nothing, and the endpoints of the guest documents never change one.
/// </summary>
public class SelfBillingServiceTests
{
    /// <summary>
    /// PROOF TEST: money rule. The TD17 of a month has one line for each property and its taxable amount is the month's channel fees of the listings of the holder, to the cent
    /// (a reversal and the line it reverses cancel out, the other months, the other holders and the portals without an entity are not in it), the VAT is integrated at the rate
    /// of the settings (22 and, when the setting changes, 20: never hard-coded), the total is both, and a second call makes the same drafts again.
    /// </summary>
    [Fact]
    public async Task Td17_TaxableEqualsMonthChannelFees_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        await rig.StandardMonthAsync();

        var generation = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, "auth0|agency");

        Assert.Equal((2, 0, 0, 0), (generation.Created, generation.Updated, generation.AlreadyIssued, generation.Discarded));
        var booking = generation.Drafts.Single(d => d.PortalName == "Booking.com B.V.");
        var airbnb = generation.Drafts.Single(d => d.PortalName == "Airbnb Ireland UC");
        var bookingDraft = await rig.DocumentAsync(booking.DocumentId);
        var airbnbDraft = await rig.DocumentAsync(airbnb.DocumentId);

        // Booking.com: the lake house 120.00 and 80.50 replaced by 90.00 (the reversal cancels the first), the mountain house 40.00: 250.00 and 55.00 of VAT.
        Assert.Equal((FiscalDocumentPurpose.SelfBilling, FiscalDocumentStatus.Draft, FiscalDocumentKind.Invoice), (bookingDraft.Purpose, bookingDraft.Status, bookingDraft.Kind));
        Assert.Equal(new[] { 210.00m, 40.00m }, bookingDraft.Lines.OrderBy(l => l.LineNumber).Select(l => l.TotalPrice));
        Assert.All(bookingDraft.Lines, l => Assert.Equal((FiscalLineType.Commission, 22m), (l.LineType, l.VatRatePercent)));
        Assert.Equal((250.00m, 55.00m, 305.00m, (decimal?)22m), (bookingDraft.TaxableBase, bookingDraft.VatAmount, bookingDraft.Total, bookingDraft.VatRatePercent));
        Assert.Equal((60.00m, 13.20m, 73.20m), (airbnbDraft.TaxableBase, airbnbDraft.VatAmount, airbnbDraft.Total));

        // The taxable amounts are the channel fees of the month of the company's listings on the two configured portals, read back from the ledger.
        var ledger = await rig.Db.OwnerLedgerEntries.AsNoTracking()
            .Where(e => e.EntryType == OwnerLedgerEntryType.ChannelFee && e.PeriodMonth == 10 && e.PropertyOwnerId == rig.Company)
            .ToListAsync();
        var configured = ledger.Where(e => e.SourceId != rig.ExpediaStay).ToList();
        Assert.Equal(-configured.Sum(e => e.OriginalAmount), bookingDraft.TaxableBase + airbnbDraft.TaxableBase);

        // The cedente is the legal entity of the portal, the issuer the profile of the company, the period the month, the version the one of the settings.
        Assert.Equal(("Booking.com B.V.", "NL805734958B01", "NL", "EUR"), (bookingDraft.RecipientName, bookingDraft.RecipientVatNumber, bookingDraft.RecipientCountryCode, bookingDraft.Currency));
        Assert.Equal((rig.CompanyProfile, 2026, 10, "td17-2026.11-1"), (bookingDraft.IssuerProfileId!.Value, bookingDraft.PeriodYear!.Value, bookingDraft.PeriodMonth!.Value, bookingDraft.RuleVersion));
        Assert.Equal(("SRL1234", "societa@pec.example.com"), (bookingDraft.RecipientSdiCode, bookingDraft.RecipientPec));

        var again = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null);
        Assert.Equal((0, 2, 0), (again.Created, again.Updated, again.AlreadyIssued));
        Assert.Equal(2, await rig.Db.FiscalDocuments.AsNoTracking().CountAsync(d => d.Purpose == FiscalDocumentPurpose.SelfBilling));
        Assert.Equal(bookingDraft.Id, again.Drafts.Single(d => d.PortalName == "Booking.com B.V.").DocumentId);

        // The VAT rate is the one of the settings.
        var twenty = SelfBillingRig.Shipped();
        twenty.VatRatePercent = 20m;
        var changed = await rig.ServiceWith(twenty).CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null);
        Assert.Equal(2, changed.Updated);
        var bookingAtTwenty = await rig.DocumentAsync(booking.DocumentId);
        Assert.Equal((250.00m, 50.00m, 300.00m), (bookingAtTwenty.TaxableBase, bookingAtTwenty.VatAmount, bookingAtTwenty.Total));
        Assert.All(bookingAtTwenty.Lines, l => Assert.Equal(20m, l.VatRatePercent));
    }

    /// <summary>
    /// PROOF TEST: data rule (⚖ I10). Only a holder that needs a TD17 gets one: the owner company that holds the account (V1) and the agency that holds it (V2); a private owner
    /// has none (the portal charged the Italian VAT), a company without an issuer profile is reported, and the commissions of a portal with no entity, in francs, are left out with the
    /// reason, never invoiced by a guess.
    /// </summary>
    [Fact]
    public async Task Td17_OnlyHoldersThatNeedOneGetOne_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        await rig.AddAsync(rig.Fee(rig.Company, rig.Lake, await rig.StayAsync(rig.Lake, BookingSource.BookingCom, new DateTime(2026, 10, 5)), 100.00m));
        await rig.AddAsync(rig.Fee(rig.PrivateOwner, rig.City, await rig.StayAsync(rig.City, BookingSource.BookingCom, new DateTime(2026, 10, 6)), 50.00m));
        await rig.HoldAsync(rig.Mountain, BookingSource.BookingCom, PortalAccountHolder.Agency);
        await rig.AddAsync(rig.Fee(rig.Company, rig.Mountain, await rig.StayAsync(rig.Mountain, BookingSource.BookingCom, new DateTime(2026, 10, 7)), 30.00m));
        await rig.AddAsync(rig.Fee(rig.Company, rig.Lake, await rig.StayAsync(rig.Lake, BookingSource.Expedia, new DateTime(2026, 10, 8)), 25.00m));
        await rig.AddAsync(rig.Fee(rig.NoProfileCompany, rig.Garden, await rig.StayAsync(rig.Garden, BookingSource.Airbnb, new DateTime(2026, 10, 9)), 10.00m));
        await rig.AddAsync(rig.Fee(rig.Company, rig.Lugano, await rig.StayAsync(rig.Lugano, BookingSource.BookingCom, new DateTime(2026, 10, 10)), 20.00m, currency: "CHF"));

        var generation = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null);

        Assert.Equal(2, generation.Created);
        var company = Assert.Single(generation.Drafts, d => d.HolderKind == SelfBillingHolderKind.Owner);
        var agency = Assert.Single(generation.Drafts, d => d.HolderKind == SelfBillingHolderKind.Agency);
        Assert.Equal(((Guid?)rig.Company, 100.00m), (company.HolderOwnerId, company.TaxableBase));
        Assert.Equal(((Guid?)null, 30.00m), (agency.HolderOwnerId, agency.TaxableBase));
        Assert.Equal(rig.AgencyProfile, (await rig.DocumentAsync(agency.DocumentId)).IssuerProfileId);

        var failed = Assert.Single(generation.Failed);
        Assert.Equal((SelfBillingHolderKind.Owner, (Guid?)rig.NoProfileCompany, "Airbnb Ireland UC", FiscalDocumentErrors.OwnerIssuerMissing), (failed.HolderKind, failed.HolderOwnerId, failed.PortalName, failed.Code));

        var notDue = generation.NotDue.ToDictionary(n => n.Reason, n => (n.Channel, n.Stays, n.Amount, n.Currency));
        Assert.Equal(3, notDue.Count);
        Assert.Equal(("BookingCom", 1, 50.00m, "EUR"), notDue[SelfBillingNotDueReasons.PrivateHolder]);
        Assert.Equal(("Expedia", 1, 25.00m, "EUR"), notDue[SelfBillingNotDueReasons.PortalNotConfigured]);
        Assert.Equal(("BookingCom", 1, 20.00m, "CHF"), notDue[SelfBillingNotDueReasons.CurrencyNotSupported]);
        Assert.Equal(2, await rig.Db.FiscalDocuments.AsNoTracking().CountAsync(d => d.Purpose == FiscalDocumentPurpose.SelfBilling));
    }

    /// <summary>PROOF TEST: data rule (⚖ I10). A Swiss agency that holds the account has no TD17: the commissions are left out as «holder not Italian», nothing is drafted.</summary>
    [Fact]
    public async Task Td17_SwissAgencyHoldingTheAccountHasNone_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync(swissAgency: true);
        await rig.AddAsync(rig.Fee(rig.Company, rig.Lake, await rig.StayAsync(rig.Lake, BookingSource.BookingCom, new DateTime(2026, 10, 5)), 100.00m));

        var generation = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null);

        Assert.Empty(generation.Drafts);
        Assert.Empty(generation.Failed);
        var left = Assert.Single(generation.NotDue);
        Assert.Equal((SelfBillingNotDueReasons.HolderNotItalian, "BookingCom", 1, 100.00m), (left.Reason, left.Channel, left.Stays, left.Amount));
        Assert.Empty(await rig.Db.FiscalDocuments.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// PROOF TEST: data rule. The issue numbers the TD17 in the series of its holder (the other holder and the sales documents have their own counters), keeps the holder as it was,
    /// leaves it <c>to_send</c> for the SDI, and writes the XML the schema accepts: the portal is the cedente (its VAT number, <c>RF18</c>, an address abroad), the holder the
    /// cessionario that issues it (<c>CC</c>) and receives it at its own code, the agency the transmitter; the VAT is integrated and the total is both. A second issue is refused.
    /// </summary>
    [Fact]
    public async Task Td17_IssueNumbersInTheHoldersSeriesAndWritesAValidXml_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        await rig.StandardMonthAsync();
        await rig.HoldAsync(rig.City, BookingSource.BookingCom, PortalAccountHolder.Agency);
        await rig.AddAsync(rig.Fee(rig.PrivateOwner, rig.City, await rig.StayAsync(rig.City, BookingSource.BookingCom, new DateTime(2026, 10, 12)), 70.00m));
        var generation = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null);
        var booking = generation.Drafts.Single(d => d.HolderKind == SelfBillingHolderKind.Owner && d.PortalName == "Booking.com B.V.");
        var airbnb = generation.Drafts.Single(d => d.PortalName == "Airbnb Ireland UC");
        var agency = generation.Drafts.Single(d => d.HolderKind == SelfBillingHolderKind.Agency);

        var issued = (await rig.Service.IssueAsync(rig.OrgId, booking.DocumentId, confirm: true, "auth0|agency")).Document;

        Assert.Equal((FiscalDocumentStatus.Issued, "TD17-2026", "TD17-2026/1", new DateOnly(2026, 11, 12)), (issued.Status, issued.Series, issued.Number, issued.IssueDate));
        Assert.Equal(FiscalDocumentSdiStatuses.ToSend, issued.SdiStatus);
        var snapshot = FiscalIssuerSnapshot.FromJson(issued.IssuerSnapshot)!;
        Assert.Equal(("TEST Immobiliare Lago Srl", "01234567897", "TEST Agenzia Lago Srl"), (snapshot.LegalName, snapshot.VatNumber, snapshot.Intermediary!.Name));

        var xml = XDocument.Parse(issued.FatturaPaXml!);
        Assert.Equal(("TD17", FatturaPaXmlBuilder.RecipientIssuerCode, "305.00"), (xml.Descendants("TipoDocumento").Single().Value, xml.Descendants("SoggettoEmittente").Single().Value, xml.Descendants("ImportoTotaleDocumento").Single().Value));
        var cedente = xml.Descendants("CedentePrestatore").Single();
        Assert.Equal(("NL", "805734958B01", "Booking.com B.V.", "RF18", FatturaPaParties.ForeignPostalCode), (
            cedente.Descendants("IdPaese").Single().Value,
            cedente.Descendants("IdCodice").Single().Value,
            cedente.Descendants("Denominazione").Single().Value,
            cedente.Descendants("RegimeFiscale").Single().Value,
            cedente.Descendants("CAP").Single().Value));
        var cessionario = xml.Descendants("CessionarioCommittente").Single();
        Assert.Equal(("IT", "01234567897", "TEST Immobiliare Lago Srl"), (
            cessionario.Descendants("IdPaese").Single().Value,
            cessionario.Descendants("IdCodice").Single().Value,
            cessionario.Descendants("Denominazione").Single().Value));
        Assert.Equal("SRL1234", xml.Descendants("CodiceDestinatario").Single().Value);
        Assert.Equal("09876543217", xml.Descendants("IdTrasmittente").Single().Element("IdCodice")!.Value);
        var summary = xml.Descendants("DatiRiepilogo").Single();
        Assert.Equal(("22.00", "250.00", "55.00", "I"), (summary.Element("AliquotaIVA")!.Value, summary.Element("ImponibileImporto")!.Value, summary.Element("Imposta")!.Value, summary.Element("EsigibilitaIVA")!.Value));
        Assert.Empty(xml.Descendants("DatiFattureCollegate"));
        Assert.Empty(XsdValidator.Validate(issued.FatturaPaXml!, new FatturaPaSchemaProvider().GetSchemas()));

        // The same holder's next TD17 is the next number of its series; the agency's own TD17 starts its own counter at 1.
        var second = (await rig.Service.IssueAsync(rig.OrgId, airbnb.DocumentId, confirm: true, null)).Document;
        Assert.Equal("TD17-2026/2", second.Number);
        var agencys = (await rig.Service.IssueAsync(rig.OrgId, agency.DocumentId, confirm: true, null)).Document;
        Assert.Equal(("TD17-2026/1", rig.AgencyProfile), (agencys.Number, agencys.IssuerProfileId));
        Assert.Equal("09876543217", XDocument.Parse(agencys.FatturaPaXml!).Descendants("IdTrasmittente").Single().Element("IdCodice")!.Value);
        Assert.Equal(2, (await rig.Db.FiscalNumberSequences.AsNoTracking().ToListAsync()).Count);

        var twice = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.IssueAsync(rig.OrgId, booking.DocumentId, confirm: true, null));
        Assert.Equal(FiscalDocumentErrors.NotDraft, twice.Code);
        var unconfirmed = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.IssueAsync(rig.OrgId, airbnb.DocumentId, confirm: false, null));
        Assert.Equal(FiscalDocumentErrors.ConfirmationRequired, unconfirmed.Code);
    }

    /// <summary>
    /// PROOF TEST: money rule. A draft is issued only while it is what the ledger gives: a commission posted after the draft makes the issue 409 and consumes no number; the drafts made
    /// again are issued; once issued the TD17 never changes, the next generation says the ledger changed since, and a draft whose commissions are gone is discarded.
    /// </summary>
    [Fact]
    public async Task Td17_DraftIssuedOnlyWhileItIsTheLedger_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        var first = await rig.StayAsync(rig.Lake, BookingSource.BookingCom, new DateTime(2026, 10, 5));
        var firstFee = rig.Fee(rig.Company, rig.Lake, first, 100.00m);
        await rig.AddAsync(firstFee);
        var generation = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null);
        var id = Assert.Single(generation.Drafts).DocumentId;

        await rig.AddAsync(rig.Fee(rig.Company, rig.Lake, await rig.StayAsync(rig.Lake, BookingSource.BookingCom, new DateTime(2026, 10, 20)), 10.00m));
        var outdated = await Assert.ThrowsAsync<DomainConflictException>(() => rig.Service.IssueAsync(rig.OrgId, id, confirm: true, null));
        Assert.Equal(FiscalDocumentErrors.DraftOutdated, outdated.Code);
        Assert.Empty(await rig.Db.FiscalNumberSequences.AsNoTracking().ToListAsync());

        var again = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null);
        Assert.Equal((1, 110.00m), (again.Updated, Assert.Single(again.Drafts).TaxableBase));
        var issued = (await rig.Service.IssueAsync(rig.OrgId, id, confirm: true, null)).Document;
        Assert.Equal((FiscalDocumentStatus.Issued, 110.00m, 24.20m), (issued.Status, issued.TaxableBase, issued.VatAmount));

        await rig.AddAsync(rig.Fee(rig.Company, rig.Lake, await rig.StayAsync(rig.Lake, BookingSource.BookingCom, new DateTime(2026, 10, 25)), 5.00m));
        var after = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null);
        var kept = Assert.Single(after.Drafts);
        Assert.Equal((0, 0, 1, SelfBillingResults.AlreadyIssued, true, 110.00m), (after.Created, after.Updated, after.AlreadyIssued, kept.Result, kept.ChangedSinceIssue, (await rig.DocumentAsync(id)).TaxableBase));

        // A draft nothing supports any more is dropped: the Airbnb commission of another month is reversed before the draft is made again.
        var stay = await rig.StayAsync(rig.Lake, BookingSource.Airbnb, new DateTime(2026, 10, 15));
        var airbnbFee = rig.Fee(rig.Company, rig.Lake, stay, 40.00m);
        await rig.AddAsync(airbnbFee);
        var withAirbnb = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null);
        var airbnbDraft = withAirbnb.Drafts.Single(d => d.PortalName == "Airbnb Ireland UC");
        await rig.AddAsync(rig.Reversal(airbnbFee));
        var dropped = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null);
        Assert.Equal(1, dropped.Discarded);
        Assert.Equal(FiscalDocumentStatus.Discarded, (await rig.DocumentAsync(airbnbDraft.DocumentId)).Status);
        Assert.DoesNotContain(await rig.Service.ListAsync(rig.OrgId, new SelfBillingCriteria(2026, 10)), r => r.Id == airbnbDraft.DocumentId);
    }

    /// <summary>
    /// PROOF TEST: money rule (the one definition of an active stay). The owner ledger is posted by hand, so the line of a stay cancelled or marked no-show after the posting is still alive: the
    /// TD17 never integrates it, neither in the draft (left out as «stay not active», with its commission) nor at the issue (a draft made while the stay was active is refused, 409, and made
    /// again without it).
    /// </summary>
    [Fact]
    public async Task Td17_OnlyActiveStaysAreIntegrated_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        var kept = await rig.StayAsync(rig.Lake, BookingSource.BookingCom, new DateTime(2026, 10, 5));
        var cancelled = await rig.StayAsync(rig.Lake, BookingSource.BookingCom, new DateTime(2026, 10, 9));
        var noShow = await rig.StayAsync(rig.Lake, BookingSource.BookingCom, new DateTime(2026, 10, 14));
        await rig.AddAsync(rig.Fee(rig.Company, rig.Lake, kept, 100.00m), rig.Fee(rig.Company, rig.Lake, cancelled, 40.00m), rig.Fee(rig.Company, rig.Lake, noShow, 25.00m));

        // Made while the three stays are active: 165.00.
        var first = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null);
        var drafted = Assert.Single(first.Drafts);
        Assert.Equal(165.00m, drafted.TaxableBase);
        Assert.Empty(first.NotDue);
        var id = drafted.DocumentId;

        // The cancellation and the no-show come after the posting: the lines are alive in the ledger, the draft is no longer what the plan gives, and the issue refuses it.
        await rig.ChangeStayAsync(cancelled, status: BookingStatus.Cancelled);
        await rig.ChangeStayAsync(noShow, noShowAt: new DateTime(2026, 10, 15, 0, 0, 0, DateTimeKind.Utc));
        var outdated = await Assert.ThrowsAsync<DomainConflictException>(() => rig.Service.IssueAsync(rig.OrgId, id, confirm: true, null));
        Assert.Equal(FiscalDocumentErrors.DraftOutdated, outdated.Code);
        Assert.Empty(await rig.Db.FiscalNumberSequences.AsNoTracking().ToListAsync());

        // Made again, the draft is what the active stay gives and the two others are left out with their commission and the reason.
        var again = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null);
        var updated = Assert.Single(again.Drafts);
        Assert.Equal((id, 100.00m), (updated.DocumentId, updated.TaxableBase));
        var left = Assert.Single(again.NotDue);
        Assert.Equal((SelfBillingNotDueReasons.StayNotActive, "BookingCom", 2, 65.00m, "EUR"), (left.Reason, left.Channel, left.Stays, left.Amount, left.Currency));
        var issued = (await rig.Service.IssueAsync(rig.OrgId, id, confirm: true, null)).Document;
        Assert.Equal((100.00m, 22.00m), (issued.TaxableBase, issued.VatAmount));
    }

    /// <summary>
    /// PROOF TEST: money rule. The part of the amount that comes from a rule of the agency or the default of the channel, not from the figure of the portal, is an estimate: the detail of the draft
    /// says how much (the figure the issue asks to accept), the issue without the acceptance is 422 and numbers nothing, with it the TD17 is issued; a draft made of the figures of the portal has
    /// no estimate and is issued as before.
    /// </summary>
    [Fact]
    public async Task Td17_AnEstimatedAmountNeedsAnExplicitAcceptance_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        await rig.AddAsync(
            rig.Fee(rig.Company, rig.Lake, await rig.StayAsync(rig.Lake, BookingSource.BookingCom, new DateTime(2026, 10, 5)), 100.00m),
            rig.Fee(rig.Company, rig.Lake, await rig.StayAsync(rig.Lake, BookingSource.BookingCom, new DateTime(2026, 10, 9)), 30.00m, ruleVersion: ChannelFeeSources.Default),
            rig.Fee(rig.Company, rig.Mountain, await rig.StayAsync(rig.Mountain, BookingSource.Airbnb, new DateTime(2026, 10, 12)), 60.00m));
        var generation = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null);
        var booking = generation.Drafts.Single(d => d.PortalName == "Booking.com B.V.");
        var airbnb = generation.Drafts.Single(d => d.PortalName == "Airbnb Ireland UC");
        Assert.Equal((130.00m, 30.00m), (booking.TaxableBase, booking.EstimatedAmount));
        Assert.Equal(0m, airbnb.EstimatedAmount);

        // The detail says the same figure, recomputed from the plan as the issue does.
        Assert.Equal(30.00m, (await rig.Service.GetAsync(rig.OrgId, booking.DocumentId)).EstimatedAmount);
        Assert.Equal(0m, (await rig.Service.GetAsync(rig.OrgId, airbnb.DocumentId)).EstimatedAmount);

        // Without the acceptance nothing is issued and no number is consumed; the refusal carries the figure.
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.IssueAsync(rig.OrgId, booking.DocumentId, confirm: true, null));
        Assert.Equal(SelfBillingErrors.EstimateNotConfirmed, refused.Code);
        Assert.Equal(30.00m, refused.Extensions!["estimatedAmount"]);
        Assert.Empty(await rig.Db.FiscalNumberSequences.AsNoTracking().ToListAsync());
        Assert.Equal(FiscalDocumentStatus.Draft, (await rig.DocumentAsync(booking.DocumentId)).Status);

        // The draft of the portal's own figures is issued without it; the estimated one with it, and an issued document estimates nothing.
        Assert.Equal(FiscalDocumentStatus.Issued, (await rig.Service.IssueAsync(rig.OrgId, airbnb.DocumentId, confirm: true, null)).Document.Status);
        var issued = await rig.Service.IssueAsync(rig.OrgId, booking.DocumentId, confirm: true, null, acceptEstimate: true);
        Assert.Equal((FiscalDocumentStatus.Issued, "TD17-2026/2", 0m), (issued.Document.Status, issued.Row.Number, issued.EstimatedAmount));
        Assert.Equal(0m, (await rig.Service.GetAsync(rig.OrgId, booking.DocumentId)).EstimatedAmount);
    }

    /// <summary>
    /// PROOF TEST: money rule. A TD17 integrates the commissions of a whole month and an issued one is not completed afterwards: the month of the clock and the later ones are refused (422) for the
    /// drafts, and so is the issue while the month runs; the answer of a month that is over says whether its owner ledger holds lines (a month with none has nothing to invoice, and the host is
    /// told to post it instead of reading «nothing is due»).
    /// </summary>
    [Fact]
    public async Task Td17_AMonthThatIsNotOverIsRefused_AndTheAnswerSaysIfTheLedgerIsPosted_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        await rig.StandardMonthAsync();

        // The clock is at 12 November 2026: November and the months after it are not over, nothing is made.
        foreach (var (year, month) in new[] { (2026, 11), (2026, 12), (2027, 1) })
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, year, month, null));
            Assert.Equal(SelfBillingErrors.PeriodNotEnded, refused.Code);
        }

        Assert.Empty(await rig.Db.FiscalDocuments.AsNoTracking().ToListAsync());

        // A month that is over is made, and the answer says whether the ledger holds lines for it: October does, August has none.
        var october = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null);
        Assert.True(october.LedgerPosted);
        Assert.Equal(2, october.Created);
        var august = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 8, null);
        Assert.False(august.LedgerPosted);
        Assert.Empty(august.Drafts);

        // The month is over the day after its last day (Europe/Rome): on 31 October the draft of October is not issued, the day after it is.
        var id = october.Drafts.First().DocumentId;
        rig.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
        var early = await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.IssueAsync(rig.OrgId, id, confirm: true, null));
        Assert.Equal(SelfBillingErrors.PeriodNotEnded, early.Code);
        Assert.Empty(await rig.Db.FiscalNumberSequences.AsNoTracking().ToListAsync());
        rig.Clock.SetUtcNow(new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero));
        Assert.Equal(FiscalDocumentStatus.Issued, (await rig.Service.IssueAsync(rig.OrgId, id, confirm: true, null)).Document.Status);
    }

    /// <summary>
    /// The generic readers of the fiscal documents (the list, the detail, the export of the list) say what a TD17 is: the document carries its purpose (its recipient fields hold the foreign
    /// supplier, not a customer, so a reader cannot take it for an invoice to the portal) and the file names its kind «Autofattura TD17», in Italian and in English.
    /// </summary>
    [Fact]
    public async Task Td17_TheGenericListsSayWhatItIs_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        await rig.StandardMonthAsync();
        var generation = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null);
        var id = generation.Drafts.First(d => d.PortalName == "Booking.com B.V.").DocumentId;
        await rig.Service.IssueAsync(rig.OrgId, id, confirm: true, null);

        var scope = new HostScope(rig.OrgId);
        var listed = FiscalDocumentDto.From((await rig.Documents.ListAsync(scope, new FiscalDocumentCriteria(), 1, 20)).Items.Single(d => d.Id == id));
        Assert.Equal(("SelfBilling", "Booking.com B.V."), (listed.Purpose, listed.RecipientName));
        Assert.Equal("SelfBilling", FiscalDocumentDto.From((await rig.Documents.GetAsync(id))!).Purpose);

        var rows = await rig.Documents.ListForExportAsync(scope, new FiscalDocumentCriteria(), 100);
        Assert.Equal(FiscalDocumentPurpose.SelfBilling, Assert.Single(rows).Purpose);
        var italian = Assert.Single(FiscalDocumentExports.Table(rows, new ResxDocumentTexts(), "it"));
        var english = Assert.Single(FiscalDocumentExports.Table(rows, new ResxDocumentTexts(), "en"));
        Assert.Equal(("TD17-2026/1", "Autofattura TD17", "Self-billed invoice TD17"), ((string?)italian[1], (string?)italian[0], (string?)english[0]));
    }

    /// <summary>
    /// PROOF TEST: permission (tenant) rule. A TD17 of another organization, or a document that is not a TD17, is not found (404): nothing is read, issued or discarded; the list of
    /// another organization is empty; and the endpoints of the guest documents refuse to make again, issue, discard or credit a TD17 (422 <c>document_self_billing</c>).
    /// </summary>
    [Fact]
    public async Task Td17_OtherOrganizationAndOtherDocuments404_AndGuestEndpointsRefuse_Test()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        await rig.StandardMonthAsync();
        var generation = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null);
        var id = generation.Drafts.First().DocumentId;
        var otherOrg = Guid.NewGuid();

        Assert.Equal(FiscalDocumentErrors.NotFound, (await Assert.ThrowsAsync<NotFoundException>(() => rig.Service.GetAsync(otherOrg, id))).Code);
        Assert.Equal(FiscalDocumentErrors.NotFound, (await Assert.ThrowsAsync<NotFoundException>(() => rig.Service.IssueAsync(otherOrg, id, confirm: true, null))).Code);
        Assert.Equal(FiscalDocumentErrors.NotFound, (await Assert.ThrowsAsync<NotFoundException>(() => rig.Service.DiscardDraftAsync(otherOrg, id))).Code);
        Assert.Empty(await rig.Service.ListAsync(otherOrg, new SelfBillingCriteria()));
        Assert.Empty((await rig.Service.CreateDraftsForPeriodAsync(otherOrg, 2026, 10, null)).Drafts);
        Assert.All(await rig.Db.FiscalDocuments.AsNoTracking().ToListAsync(), d => Assert.Equal(FiscalDocumentStatus.Draft, d.Status));

        // A guest document of the same org is not a TD17.
        var guestDocument = new FiscalDocument { OrgId = rig.OrgId, Purpose = FiscalDocumentPurpose.GuestStay, Status = FiscalDocumentStatus.Draft, Currency = "EUR" };
        await rig.AddAsync(guestDocument);
        Assert.Equal(FiscalDocumentErrors.NotFound, (await Assert.ThrowsAsync<NotFoundException>(() => rig.Service.GetAsync(rig.OrgId, guestDocument.Id))).Code);
        Assert.Equal(FiscalDocumentErrors.NotFound, (await Assert.ThrowsAsync<NotFoundException>(() => rig.Service.DiscardDraftAsync(rig.OrgId, guestDocument.Id))).Code);

        // The guest endpoints never change a TD17, issued or not.
        var issued = (await rig.Service.IssueAsync(rig.OrgId, id, confirm: true, null)).Document;
        var other = generation.Drafts.Last().DocumentId;
        var refusals = new[]
        {
            await Assert.ThrowsAsync<DomainRuleException>(() => rig.Documents.IssueAsync(other, confirm: true)),
            await Assert.ThrowsAsync<DomainRuleException>(() => rig.Documents.RefreshDraftAsync(other)),
            await Assert.ThrowsAsync<DomainRuleException>(() => rig.Documents.DiscardDraftAsync(other)),
            await Assert.ThrowsAsync<DomainRuleException>(() => rig.Documents.CreateCreditNoteAsync(issued.Id, null, "TEST", confirm: true, null)),
        };
        Assert.All(refusals, refusal => Assert.Equal(SelfBillingErrors.UseSelfBilling, refusal.Code));
        Assert.Equal(FiscalDocumentStatus.Draft, (await rig.DocumentAsync(other)).Status);
    }

    /// <summary>
    /// PROOF TEST: permission rule (the recurring rule of the portata). The TD17 span the properties of a holder: a caller whose reach is restricted to some properties is refused
    /// (403) on EVERY action, reads included, before the service is asked anything; a caller who reaches the whole org is served; every action needs <c>agency.invoice.manage</c> and
    /// <c>Features:FiscalDocumentIssuance</c>.
    /// </summary>
    [Fact]
    public async Task Td17_Controller_RefusesCallersWithoutTheWholeOrg_AndIsBehindThePolicyAndTheFlag_Test()
    {
        var orgId = Guid.NewGuid();
        foreach (var restricted in new[] { new HostScope(orgId, GrantedToUserId: "auth0|solo-alcuni"), new HostScope(orgId, OwnerId: "auth0|solo-alcuni") })
        {
            var service = new Mock<ISelfBillingService>(MockBehavior.Strict);
            var corrections = new Mock<IFiscalDocumentCorrectionService>(MockBehavior.Strict);
            var controller = new SelfBillingController(service.Object, corrections.Object, Org(orgId), Scope(orgId, restricted)) { ControllerContext = Context() };
            var actions = controller.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttributes().Any(a => a is HttpMethodAttribute))
                .ToList();
            Assert.Equal(6, actions.Count);

            foreach (var action in actions)
            {
                var arguments = action.GetParameters()
                    .Select(p => p.ParameterType == typeof(GenerateSelfBillingRequest) ? new GenerateSelfBillingRequest { Period = "2026-10" }
                        : p.ParameterType == typeof(IssueSelfBillingRequest) ? new IssueSelfBillingRequest { Confirm = true, AcceptEstimate = true }
                        : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null)
                    .ToArray();
                var task = (Task)action.Invoke(controller, arguments)!;
                await task;
                var answer = task.GetType().GetProperty("Result")!.GetValue(task);
                if (answer is not null && answer.GetType().IsGenericType && answer.GetType().GetGenericTypeDefinition() == typeof(ActionResult<>))
                    answer = answer.GetType().GetProperty("Result")!.GetValue(answer);

                Assert.True(answer is ForbidResult, $"{action.Name} did not refuse a caller without an org-wide reach");
            }

            service.VerifyNoOtherCalls();
            corrections.VerifyNoOtherCalls();
        }

        var whole = new Mock<ISelfBillingService>();
        whole.Setup(s => s.ListAsync(orgId, It.IsAny<SelfBillingCriteria>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var served = await new SelfBillingController(whole.Object, Mock.Of<IFiscalDocumentCorrectionService>(), Org(orgId), Scope(orgId, new HostScope(orgId))) { ControllerContext = Context() }.List("2026-10");
        Assert.IsType<OkObjectResult>(served.Result);
        whole.Verify(s => s.ListAsync(orgId, It.Is<SelfBillingCriteria>(c => c.Year == 2026 && c.Month == 10), It.IsAny<CancellationToken>()), Times.Once);

        var type = typeof(SelfBillingController);
        Assert.Equal(new[] { CasazenPolicies.AgencyInvoiceManage }, type.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false).Cast<AuthorizeAttribute>().Select(a => a.Policy));
        Assert.Equal(new[] { FeatureFlags.FiscalDocumentIssuance }, type.GetCustomAttributes(typeof(FeatureGateAttribute), inherit: false).Cast<FeatureGateAttribute>().Select(g => g.Flag));
    }

    /// <summary>
    /// The settings: an entry that is not usable (not a portal, in Italy, without a name, a VAT number or an address, a channel given twice) is ignored, the VAT number is kept with its
    /// country prefix whichever way it is written, an invalid VAT rate or no portal at all refuses the drafts (422), and the shipped settings are valid.
    /// </summary>
    [Fact]
    public async Task SelfBillingOptions_IgnoreUnusableEntries_AndRefuseWithoutARateOrAPortal()
    {
        var options = new SelfBillingOptions
        {
            Portals =
            [
                new() { Channel = "BookingCom", Name = "TEST Portale B.V.", Country = "nl", VatNumber = "NL 8057.34958 B01", Street = "Herengracht", City = "Amsterdam" },
                new() { Channel = "BookingCom", Name = "TEST Secondo B.V.", Country = "NL", VatNumber = "NL111111111B01", Street = "Strada", City = "Amsterdam" },
                new() { Channel = "Airbnb", Name = "TEST Italiana Srl", Country = "IT", VatNumber = "IT01234567897", Street = "Via", City = "Roma" },
                new() { Channel = "Expedia", Name = "TEST Portale Ltd", Country = "GB", VatNumber = "GB123456789", Street = "Road", City = null },
                new() { Channel = "Direct", Name = "TEST Diretto", Country = "DE", VatNumber = "DE123456789", Street = "Strasse", City = "Berlin" },
                new() { Channel = "Vrbo", Name = "TEST Portale GmbH", Country = "GR", VatNumber = "EL123456789", Street = "Odos", City = "Atene" },
            ],
        };

        var portals = options.ConfiguredPortals();

        Assert.Equal(new[] { BookingSource.BookingCom, BookingSource.Vrbo }, portals.Keys.Order());
        Assert.Equal(("TEST Portale B.V.", "NL", "805734958B01", "NL805734958B01"), (portals[BookingSource.BookingCom].Name, portals[BookingSource.BookingCom].CountryCode, portals[BookingSource.BookingCom].VatCode, portals[BookingSource.BookingCom].VatNumber));
        Assert.Equal("GR123456789", portals[BookingSource.Vrbo].VatNumber);
        Assert.Equal(4, options.IgnoredPortalEntries());
        Assert.Equal(SelfBillingRules.PortalKey("NL", "NL805734958B01"), portals[BookingSource.BookingCom].Key);
        Assert.Equal(SelfBillingRules.PortalKey("nl", "805734958b01"), SelfBillingRules.PortalKey("NL", "NL805734958B01"));
        Assert.Equal(new DateOnly(2026, 11, 15), SelfBillingRules.DueDate(2026, 10, 15));
        Assert.Equal(new DateOnly(2027, 1, 28), SelfBillingRules.DueDate(2026, 12, 99));

        await using var rig = await SelfBillingRig.CreateAsync();
        var noRate = SelfBillingRig.Shipped();
        noRate.VatRatePercent = 0m;
        Assert.Equal(SelfBillingErrors.NotConfigured, (await Assert.ThrowsAsync<DomainRuleException>(() => rig.ServiceWith(noRate).CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null))).Code);
        var noPortals = SelfBillingRig.Shipped();
        noPortals.Portals = [];
        Assert.Equal(SelfBillingErrors.NotConfigured, (await Assert.ThrowsAsync<DomainRuleException>(() => rig.ServiceWith(noPortals).CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null))).Code);
        Assert.Equal(SelfBillingErrors.PeriodInvalid, (await Assert.ThrowsAsync<DomainRuleException>(() => rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 13, null))).Code);
        Assert.All(SelfBillingRig.Shipped().Portals, p => Assert.NotNull(p.ToPortal()));
        Assert.True(SelfBillingRig.Shipped().HasValidVatRate);
    }

    /// <summary>
    /// The PDF of a TD17 is the integration of the invoice of the foreign supplier: the title says «Autofattura (TD17)», the issuer block is the buyer that issues it and the recipient block
    /// the supplier, the month of the commissions is printed, and no payment state (nobody pays: the VAT is integrated).
    /// </summary>
    [Fact]
    public async Task Td17_Pdf_NamesTheTwoPartiesAndPrintsNoPayment()
    {
        await using var rig = await SelfBillingRig.CreateAsync();
        await rig.StandardMonthAsync();
        var generation = await rig.Service.CreateDraftsForPeriodAsync(rig.OrgId, 2026, 10, null);
        var id = generation.Drafts.First(d => d.PortalName == "Booking.com B.V.").DocumentId;
        await rig.Service.IssueAsync(rig.OrgId, id, confirm: true, null);

        var content = await rig.Documents.PdfContentAsync(id, DocumentLanguages.Italian);

        Assert.Equal("Autofattura (TD17) n. TD17-2026/1", content.Title);
        var texts = content.Blocks.SelectMany(TextsOf).ToList();
        Assert.Contains("Cessionario/committente (emittente)", texts);
        Assert.Contains("Cedente/prestatore (fornitore estero)", texts);
        Assert.Contains(texts, t => t.Contains("TEST Immobiliare Lago Srl", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("Booking.com B.V.", StringComparison.Ordinal) && t.Contains("NL805734958B01", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Contains("commissioni del periodo 10/2026", StringComparison.Ordinal));
        Assert.DoesNotContain(texts, t => t.StartsWith("Pagamento", StringComparison.Ordinal));
        Assert.NotEmpty(await rig.Documents.GetPdfAsync(id, DocumentLanguages.English));
    }

    /// <summary>
    /// For a property of several co-owners the owner who signs is the one whose mandate is in force, else the largest share, then the oldest ownership, then the smallest id: the rule of
    /// the document plan, shared by the TD17 of a portal account held by the owner.
    /// </summary>
    [Fact]
    public void SignatoryOf_PicksTheMandateThenTheLargestShare()
    {
        var day = new DateOnly(2026, 10, 20);
        var big = new ActiveOwnership(Guid.NewGuid(), Guid.NewGuid(), 60m, new DateTime(2024, 1, 1));
        var small = new ActiveOwnership(Guid.NewGuid(), Guid.NewGuid(), 40m, new DateTime(2020, 1, 1));
        var mandateOfSmall = new OwnerMandate { PropertyOwnerId = small.OwnerId, ValidFrom = new DateOnly(2024, 1, 1), CommissionPercent = 20m };

        Assert.Equal(big.OwnerId, DocumentPlanRules.SignatoryOf([big, small], [], day)!.Value.OwnerId);
        Assert.Equal(small.OwnerId, DocumentPlanRules.SignatoryOf([big, small], [mandateOfSmall], day)!.Value.OwnerId);
        Assert.Null(DocumentPlanRules.SignatoryOf([], [], day));

        var equal = new[] { big with { SharePercent = 50m }, small with { SharePercent = 50m } };
        Assert.Equal(small.OwnerId, DocumentPlanRules.SignatoryOf(equal, [], day)!.Value.OwnerId);
        Assert.True(DocumentPlanRules.IsActiveOn(new DateTime(2026, 10, 20), null, day));
        Assert.False(DocumentPlanRules.IsActiveOn(new DateTime(2026, 10, 1), new DateTime(2026, 10, 20), day));
        Assert.False(DocumentPlanRules.IsActiveOn(new DateTime(2026, 10, 21), null, day));
    }

    private static IEnumerable<string> TextsOf(PdfBlock block) => block switch
    {
        PdfHeading heading => new[] { heading.Text },
        PdfParagraph paragraph => new[] { paragraph.Text },
        PdfTable table => table.Columns.Select(c => c.Header).Concat(table.Rows.SelectMany(r => r)),
        _ => Array.Empty<string>(),
    };

    private static ControllerContext Context()
    {
        var localizer = new Mock<IStringLocalizer<SharedResources>>();
        localizer.Setup(l => l[It.IsAny<string>()]).Returns((string name) => new LocalizedString(name, name, resourceNotFound: true));
        localizer.Setup(l => l[It.IsAny<string>(), It.IsAny<object[]>()]).Returns((string name, object[] _) => new LocalizedString(name, name, resourceNotFound: true));
        var services = new ServiceCollection().AddSingleton(localizer.Object).BuildServiceProvider();
        var identity = new ClaimsIdentity([new Claim("sub", "auth0|solo-alcuni"), new Claim(ClaimTypes.NameIdentifier, "auth0|solo-alcuni")], "test");
        return new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity), RequestServices = services } };
    }

    private static IOrgContextResolver Org(Guid orgId)
    {
        var resolver = new Mock<IOrgContextResolver>();
        resolver.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        return resolver.Object;
    }

    private static IHostScopeResolver Scope(Guid orgId, HostScope scope)
    {
        var resolver = new Mock<IHostScopeResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), orgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        return resolver.Object;
    }
}

/// <summary>
/// An Italian agency (VAT ordinary regime, complete profile), a company owner with its own profile and the Lake and Mountain properties, a private owner with the City flat, a
/// company without an issuer profile with the Garden, and a flat in Lugano, over one in-memory database with the shipped tax rules and settings, the real XML builder (the official
/// schema) and the issuance on. The ledger lines of a month are written as the posting writes them.
/// </summary>
internal sealed class SelfBillingRig : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    private SelfBillingRig(AppDbContext db)
    {
        Db = db;
        Clock = new FakeTimeProvider(Now);
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Test");
        Rules = TaxRuleCatalogs.Resolver();
        Issuers = new DocumentIssuerService(
            db, new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance), Rules, environment.Object, Clock, NullLogger<DocumentIssuerService>.Instance);
        Documents = new FiscalDocumentService(
            db,
            Issuers,
            Mock.Of<IInvoiceDataService>(),
            new FiscalNumberingService(db, Clock, NullLogger<FiscalNumberingService>.Instance),
            new FatturaPaXmlBuilder(new FatturaPaSchemaProvider(), NullLogger<FatturaPaXmlBuilder>.Instance),
            Mock.Of<IDomainEventRecorder>(),
            new MigraDocPdfDocumentRenderer(),
            new ResxDocumentTexts(),
            Rules,
            Clock,
            NullLogger<FiscalDocumentService>.Instance);
        Service = ServiceWith(Shipped());
    }

    public AppDbContext Db { get; }

    public FakeTimeProvider Clock { get; }

    public IFiscalTaxRuleResolver Rules { get; }

    public DocumentIssuerService Issuers { get; }

    public FiscalDocumentService Documents { get; }

    public SelfBillingService Service { get; }

    public Guid OrgId { get; } = Guid.NewGuid();

    public Guid AgencyProfile { get; private set; }

    public Guid Company { get; } = Guid.NewGuid();

    public Guid CompanyProfile { get; private set; }

    public Guid PrivateOwner { get; } = Guid.NewGuid();

    public Guid NoProfileCompany { get; } = Guid.NewGuid();

    public Guid Lake { get; } = Guid.NewGuid();

    public Guid Mountain { get; } = Guid.NewGuid();

    public Guid City { get; } = Guid.NewGuid();

    public Guid Garden { get; } = Guid.NewGuid();

    public Guid Lugano { get; } = Guid.NewGuid();

    public Guid ExpediaStay { get; private set; }

    /// <summary>The settings shipped in <c>appsettings.json</c>, bound as the application binds them (a copy a test can change).</summary>
    public static SelfBillingOptions Shipped()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "Casazen.Web", "appsettings.json"), optional: false).Build();
        var options = new SelfBillingOptions();
        configuration.GetSection(SelfBillingOptions.SectionName).Bind(options);
        return options;
    }

    public SelfBillingService ServiceWith(SelfBillingOptions options) => new(
        Db,
        Documents,
        Issuers,
        Rules,
        new PortalAccountHolderResolver(Db),
        new ResxDocumentTexts(),
        Options.Create(options),
        Clock,
        NullLogger<SelfBillingService>.Instance);

    public static async Task<SelfBillingRig> CreateAsync(bool swissAgency = false)
    {
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"SelfBilling_{Guid.NewGuid()}").Options,
            new SelfBillingTenant(),
            new EphemeralDataProtectionProvider());
        var rig = new SelfBillingRig(db);
        db.Orgs.Add(new OrgEntity
        {
            Id = rig.OrgId,
            Name = "TEST Agenzia Lago",
            Slug = $"test-{rig.OrgId:N}"[..20],
            DisplayName = "TEST Agenzia Lago",
            ContactEmail = "agenzia@example.com",
            PlanTier = PlanTier.Agency,
            HasPartitaIva = true,
            PartitaIvaNumber = "09876543217",
            FiscalCode = "09876543217",
        });
        var agency = swissAgency
            ? new OrgFiscalIssuerProfile
            {
                OrgId = rig.OrgId,
                DocumentRegime = DocumentRegime.SwissVatRegistered,
                VatRegistered = true,
                SwissVatNumber = "CHE-109.322.551",
                LegalName = "TEST Gestioni Lugano Sagl",
                AddressStreet = "Via del Test",
                PostalCode = "6900",
                City = "Lugano",
                CountryCode = "CH",
                PortalAccountHolder = PortalAccountHolder.Agency,
            }
            : new OrgFiscalIssuerProfile
            {
                OrgId = rig.OrgId,
                DocumentRegime = DocumentRegime.VatOrdinary,
                VatRegistered = true,
                LegalName = "TEST Agenzia Lago Srl",
                AddressStreet = "Via del Porto",
                AddressCivic = "2",
                PostalCode = "22100",
                City = "Como",
                Province = "CO",
                CountryCode = "IT",
                IssuerSdiRecipientCode = "AGE1234",
            };
        db.OrgFiscalIssuerProfiles.Add(agency);
        rig.AgencyProfile = agency.Id;

        db.PropertyOwners.Add(new PropertyOwner { Id = rig.Company, OrgId = rig.OrgId, FirstName = "TEST", LastName = "Societa", LegalName = "TEST Immobiliare Lago Srl", EntityKind = OwnerEntityKind.LegalEntity, VatNumber = "01234567897", Email = $"{Guid.NewGuid():N}@example.com" });
        db.PropertyOwners.Add(new PropertyOwner { Id = rig.PrivateOwner, OrgId = rig.OrgId, FirstName = "TEST", LastName = "Privato", EntityKind = OwnerEntityKind.NaturalPerson, Email = $"{Guid.NewGuid():N}@example.com" });
        db.PropertyOwners.Add(new PropertyOwner { Id = rig.NoProfileCompany, OrgId = rig.OrgId, FirstName = "TEST", LastName = "Senza", LegalName = "TEST Senza Profilo Srl", EntityKind = OwnerEntityKind.LegalEntity, VatNumber = "01234567898", Email = $"{Guid.NewGuid():N}@example.com" });
        var company = new OrgFiscalIssuerProfile
        {
            OrgId = rig.OrgId,
            PropertyOwnerId = rig.Company,
            DocumentRegime = DocumentRegime.VatOrdinary,
            VatRegistered = true,
            LegalName = "TEST Immobiliare Lago Srl",
            VatNumber = "01234567897",
            FiscalCode = "01234567897",
            AddressStreet = "Via del Lago",
            AddressCivic = "3",
            PostalCode = "22100",
            City = "Como",
            Province = "CO",
            CountryCode = "IT",
            IssuerSdiRecipientCode = "SRL1234",
            IssuerPec = "societa@pec.example.com",
        };
        db.OrgFiscalIssuerProfiles.Add(company);
        rig.CompanyProfile = company.Id;
        db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
        {
            OrgId = rig.OrgId,
            PropertyOwnerId = rig.PrivateOwner,
            DocumentRegime = DocumentRegime.PrivatePerson,
            LegalName = "TEST Privato",
            FiscalCode = "RSSMRA80A01H501U",
            AddressStreet = "Via del Sole",
            PostalCode = "20121",
            City = "Milano",
            Province = "MI",
            CountryCode = "IT",
        });

        foreach (var (id, name, country, currency, owner) in new[]
                 {
                     (rig.Lake, "TEST Casa Lago", "IT", "EUR", rig.Company),
                     (rig.Mountain, "TEST Casa Monte", "IT", "EUR", rig.Company),
                     (rig.City, "TEST Bilocale Citta", "IT", "EUR", rig.PrivateOwner),
                     (rig.Garden, "TEST Casa Giardino", "IT", "EUR", rig.NoProfileCompany),
                     (rig.Lugano, "TEST Casa Lugano", "CH", "CHF", rig.Company),
                 })
        {
            db.Properties.Add(new Property { Id = id, OrgId = rig.OrgId, OwnerId = "auth0|agenzia", Name = name, Address = "Via 1", City = "Como", Country = country, Currency = currency });
            db.PropertyOwnerships.Add(new PropertyOwnership
            {
                OrgId = rig.OrgId,
                PropertyId = id,
                PropertyOwnerId = owner,
                SharePercent = 100m,
                OwnershipStart = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
        }

        db.Guests.Add(new Guest { Id = StayGuest, OrgId = rig.OrgId, FirstName = "Test", LastName = "Guest", Email = $"{Guid.NewGuid():N}@example.com" });
        await db.SaveChangesAsync();
        return rig;
    }

    private static readonly Guid StayGuest = Guid.NewGuid();

    public async Task<FiscalDocument> DocumentAsync(Guid id) => await Db.FiscalDocuments.AsNoTracking().Include(d => d.Lines).SingleAsync(d => d.Id == id);

    public async Task AddAsync(params object[] entities)
    {
        Db.AddRange(entities);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    /// <summary>A stay of <paramref name="source"/> that checked out on <paramref name="checkOut"/>.</summary>
    public async Task<Guid> StayAsync(Guid property, BookingSource source, DateTime checkOut)
    {
        var booking = new Booking
        {
            OrgId = OrgId,
            PropertyId = property,
            GuestId = StayGuest,
            CheckInDate = checkOut.AddDays(-3),
            CheckOutDate = checkOut,
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = source,
            BasePrice = 500m,
            TotalPrice = 500m,
        };
        if (source == BookingSource.Expedia)
            ExpediaStay = booking.Id;
        await AddAsync(booking);
        return booking.Id;
    }

    /// <summary>The state of a stay after the ledger was posted: cancelled (<paramref name="status"/>), or marked no-show (<paramref name="noShowAt"/>).</summary>
    public async Task ChangeStayAsync(Guid stay, BookingStatus? status = null, DateTime? noShowAt = null)
    {
        var booking = await Db.Bookings.SingleAsync(b => b.Id == stay);
        booking.Status = status ?? booking.Status;
        booking.NoShowAt = noShowAt ?? booking.NoShowAt;
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
    }

    /// <summary>The channel fee line of an owner for a stay: <paramref name="commission"/> is what the portal kept (the ledger line is its negative).</summary>
    public OwnerLedgerEntry Fee(Guid owner, Guid property, Guid stay, decimal commission, string currency = "EUR", int year = 2026, int month = 10, string ruleVersion = "payload") => new()
    {
        OrgId = OrgId,
        PropertyOwnerId = owner,
        PropertyId = property,
        EntryType = OwnerLedgerEntryType.ChannelFee,
        SourceType = OwnerLedgerSourceType.Booking,
        SourceId = stay,
        IdempotencyKey = $"test:{Guid.NewGuid():N}",
        ServiceDate = new DateOnly(year, month, 10),
        RecognitionDate = new DateOnly(year, month, 20),
        PeriodYear = year,
        PeriodMonth = month,
        SharePercent = 100m,
        OriginalCurrency = currency,
        SourceAmount = -commission,
        OriginalAmount = -commission,
        StatementCurrency = currency,
        Amount = -commission,
        RuleVersion = ruleVersion,
        PostedAt = Now.UtcDateTime.AddDays(-5),
    };

    /// <summary>The reversal of a line: the same facts with the opposite amounts, in the same month.</summary>
    public OwnerLedgerEntry Reversal(OwnerLedgerEntry original) => new()
    {
        OrgId = original.OrgId,
        PropertyOwnerId = original.PropertyOwnerId,
        PropertyId = original.PropertyId,
        EntryType = original.EntryType,
        SourceType = original.SourceType,
        SourceId = original.SourceId,
        IdempotencyKey = "rev:" + original.Id.ToString("N"),
        ServiceDate = original.ServiceDate,
        RecognitionDate = original.RecognitionDate,
        PeriodYear = original.PeriodYear,
        PeriodMonth = original.PeriodMonth,
        SharePercent = original.SharePercent,
        OriginalCurrency = original.OriginalCurrency,
        SourceAmount = -original.SourceAmount,
        OriginalAmount = -original.OriginalAmount,
        StatementCurrency = original.StatementCurrency,
        Amount = -original.Amount,
        RuleVersion = original.RuleVersion,
        ReversesEntryId = original.Id,
        PostedAt = Now.UtcDateTime.AddDays(-4),
    };

    /// <summary>Who holds the account of a listing on a portal, written as the settings write it.</summary>
    public Task HoldAsync(Guid property, BookingSource channel, PortalAccountHolder holder) =>
        AddAsync(new PropertyChannelAccountHolder { OrgId = OrgId, PropertyId = property, Channel = channel.ToString(), Holder = holder, UpdatedAt = Now.UtcDateTime });

    /// <summary>
    /// October 2026 of the company: Booking.com on the lake house (120.00, and 80.50 reversed and replaced by 90.00), on the mountain house (40.00) and in September (999.00, not in
    /// the month); Airbnb on the lake house (60.00); Expedia (25.00, a portal with no entity); and a Booking.com commission of the private owner (77.00).
    /// </summary>
    public async Task StandardMonthAsync()
    {
        await AddAsync(Fee(Company, Lake, await StayAsync(Lake, BookingSource.BookingCom, new DateTime(2026, 10, 18)), 120.00m));
        var replaced = Fee(Company, Lake, await StayAsync(Lake, BookingSource.BookingCom, new DateTime(2026, 10, 22)), 80.50m);
        await AddAsync(replaced);
        await AddAsync(Reversal(replaced));
        await AddAsync(Fee(Company, Lake, replaced.SourceId!.Value, 90.00m));
        await AddAsync(Fee(Company, Mountain, await StayAsync(Mountain, BookingSource.BookingCom, new DateTime(2026, 10, 25)), 40.00m));
        await AddAsync(Fee(Company, Lake, await StayAsync(Lake, BookingSource.Airbnb, new DateTime(2026, 10, 12)), 60.00m));
        await AddAsync(Fee(Company, Lake, await StayAsync(Lake, BookingSource.Expedia, new DateTime(2026, 10, 14)), 25.00m));
        await AddAsync(Fee(Company, Lake, await StayAsync(Lake, BookingSource.BookingCom, new DateTime(2026, 9, 14)), 999.00m, year: 2026, month: 9));
        await AddAsync(Fee(PrivateOwner, City, await StayAsync(City, BookingSource.BookingCom, new DateTime(2026, 10, 16)), 77.00m));
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

file sealed class SelfBillingTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
