using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Authorization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.Services;
using Casazen.Web.Controllers;
using Casazen.Web.DTOs.OwnerPortal;
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E13/F01/US02/T01 (NF-110, NF-115): the PDF and the CSV of a statement version from its own lines, with the net the version stores, the tourist tax in a box outside the
/// net, the PDF stored once at the publication as an owner document, and the 404 of a version of another organization.
/// </summary>
public class OwnerStatementDocumentsTests
{
    /// <summary>
    /// Money: the net the file shows is the net the version stores (the opening balance, the total of the period, the payments, the closing balance), every part of
    /// the lines gives what the version holds for it, and a version whose lines no longer give its totals gets no file at all (409), never a wrong net.
    /// </summary>
    [Fact]
    public async Task StatementPdf_NetEqualsVersionTotals_Test()
    {
        var world = await World.CreateAsync();
        var texts = new ResxDocumentTexts();

        var content = await world.Documents.GetContentAsync(world.OrgId, world.VersionId, "it");

        // The totals of the file are the ones of the version: the opening, the closing and each part, the withholding being what the closing balance leaves over.
        var stored = OwnerStatementTotals.Of(world.Version);
        Assert.Equal(stored, content.Totals);
        Assert.Equal((800m, -75m, -10m, -80m, -60m, 5m), (content.Totals.Gross, content.Totals.ChannelFees, content.Totals.Withholding, content.Totals.Commission, content.Totals.Recharges, content.Totals.Corrections));
        Assert.Equal((100m, 580m, -400m, 280m), (content.Totals.OpeningBalance, content.Totals.Period, content.Totals.Payments, content.Totals.ClosingBalance));
        Assert.Equal(world.Version.ClosingBalance, content.Totals.OpeningBalance + content.Totals.Period + content.Totals.Payments);

        // The PDF writes them in the number format of the currency: the Swiss franc with the apostrophe, the euro with the dot and the comma.
        var layout = OwnerStatementLayout.Pdf(content, texts);
        var net = NetTable(layout, texts);
        Assert.Equal(
            [
                ("Saldo iniziale", "100.00"),
                ("Totale del periodo", "580.00"),
                ("Pagamenti", "-400.00"),
                ("Saldo finale", "280.00"),
            ],
            net.Select(row => (row[0], row[1])));
        Assert.Equal("1'234.50", OwnerStatementLayout.Money(1234.5m, "CHF"));
        Assert.Equal("1.234,50", OwnerStatementLayout.Money(1234.5m, "EUR"));

        // It is a real PDF, and the CSV carries the same closing balance.
        var file = await world.Documents.GetPdfAsync(world.OrgId, world.VersionId, "it");
        Assert.Equal("%PDF", Encoding.ASCII.GetString(file.Content, 0, 4));
        Assert.Equal("application/pdf", file.ContentType);
        var csv = Encoding.UTF8.GetString((await world.Documents.GetCsvAsync(world.OrgId, world.VersionId, "it")).Content);
        Assert.Contains("Saldo finale;;;;;;;280,00;CHF", csv, StringComparison.Ordinal);

        // A version whose closing balance is not what its lines give is refused: the file would show a net the version does not have (a published version cannot be
        // changed at all: the append-only guard refuses it, so this is one that is not published yet).
        var draft = await World.CreateAsync(approved: true);
        await World.Tamper(draft.Db, draft.VersionId, v => v.ClosingBalance += 1m);
        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() => draft.Documents.GetPdfAsync(draft.OrgId, draft.VersionId));
        Assert.Equal(OwnerAccountingErrors.LedgerImmutable, refusal.Code);
        await Assert.ThrowsAsync<DomainConflictException>(() => draft.Documents.GetCsvAsync(draft.OrgId, draft.VersionId));
        await Assert.ThrowsAsync<DomainConflictException>(() => draft.Documents.GetContentAsync(draft.OrgId, draft.VersionId));
    }

    /// <summary>
    /// Money: the tourist tax is in a box of its own, for information, and never in the net: the same statement with a different tourist tax has the same net, the
    /// amounts of the box are nowhere in the «Netto» part, and a stay whose tax the channel collects itself is not in the box.
    /// </summary>
    [Fact]
    public async Task StatementPdf_TouristTaxNotInNet_Test()
    {
        var world = await World.CreateAsync();
        var texts = new ResxDocumentTexts();

        var content = await world.Documents.GetContentAsync(world.OrgId, world.VersionId, "it");
        Assert.Equal(20m, content.TouristTax.Sum(t => t.Amount));
        Assert.All(content.TouristTax, t => Assert.Equal("CHF", t.Currency));

        // The box is after the net, and its amounts are not in any table before it.
        var layout = OwnerStatementLayout.Pdf(content, texts);
        var heading = texts.Get("StatementFileTouristTaxHeading", "it");
        var boxAt = layout.Blocks.ToList().FindIndex(b => b is PdfHeading h && h.Text == heading);
        var netAt = layout.Blocks.ToList().FindIndex(b => b is PdfHeading h && h.Text == "Netto");
        Assert.True(netAt >= 0 && boxAt > netAt, "the tourist tax box follows the net");
        var boxTable = Assert.IsType<PdfTable>(layout.Blocks[boxAt + 2]);
        Assert.Contains(boxTable.Rows, row => row[3].Contains("12.00", StringComparison.Ordinal));
        Assert.Contains(boxTable.Rows, row => row[3].Contains("8.00", StringComparison.Ordinal));
        var beforeTheBox = TextOf(layout.Blocks.Take(boxAt));
        Assert.DoesNotContain("12.00", beforeTheBox, StringComparison.Ordinal);
        Assert.DoesNotContain("8.00", beforeTheBox, StringComparison.Ordinal);

        // The tax changes, the net does not: it is the lines of the version, not the booking.
        await world.SetTouristTaxAsync(world.PortalBookingId, 99m);
        var changed = await world.Documents.GetContentAsync(world.OrgId, world.VersionId, "it");
        Assert.Equal(content.Totals, changed.Totals);
        Assert.Equal(107m, changed.TouristTax.Sum(t => t.Amount));

        // A stay whose channel collects the tax itself is not collected by the agency: a channel of the confirmed list of the issuer is left out, the other stay stays.
        world.Db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile { OrgId = world.OrgId, PlatformCollectedTouristTaxChannelsJson = "[\"BookingCom\"]" });
        await world.Db.SaveChangesAsync();
        var direct = (await world.Documents.GetContentAsync(world.OrgId, world.VersionId, "it")).TouristTax;
        Assert.Equal(8m, Assert.Single(direct).Amount);
        Assert.Equal(content.Totals, (await world.Documents.GetContentAsync(world.OrgId, world.VersionId, "it")).Totals);
    }

    /// <summary>
    /// Money: who collects the tax of a channel stay is what the tourist tax of the host says: the active rule of the channel in the comune of the property, else the
    /// collector the host declared for the stay (before any calculation was confirmed too), else the one confirmed with the calculation. A stay that nobody is known to
    /// collect for stays in the box (never an assumed 0).
    /// </summary>
    [Fact]
    public async Task StatementPdf_TouristTaxTheChannelCollectsIsNotInTheBox_Test()
    {
        var world = await World.CreateAsync();
        async Task<decimal> BoxAsync() => (await world.Documents.GetContentAsync(world.OrgId, world.VersionId)).TouristTax.Sum(t => t.Amount);

        // Nothing says who collects the 12 of the portal stay and the 8 of the direct one: the agency does.
        Assert.Equal(20m, await BoxAsync());

        // The host declared that the channel collects it, before any calculation was confirmed (the record holds only the declaration): the portal stay leaves the box.
        var record = new StayTouristTax { OrgId = world.OrgId, BookingId = world.PortalBookingId, DeclaredCollector = TouristTaxCollector.Channel };
        world.Db.StayTouristTaxes.Add(record);
        await world.Db.SaveChangesAsync();
        Assert.Equal(8m, await BoxAsync());

        // Declared the host: it is the agency's again.
        record.DeclaredCollector = TouristTaxCollector.Host;
        await world.Db.SaveChangesAsync();
        Assert.Equal(20m, await BoxAsync());

        // An active rule of the channel in the comune of the property decides, whatever the host declared.
        var property = await world.Db.Properties.SingleAsync();
        property.ComuneIstatCode = "015146";
        var rule = new TouristTaxChannelRule { Channel = BookingSource.BookingCom, IstatCode = "015146", Collector = TouristTaxCollector.Channel, SourceUrl = "https://example.com/regola" };
        world.Db.TouristTaxChannelRules.Add(rule);
        await world.Db.SaveChangesAsync();
        Assert.Equal(8m, await BoxAsync());
        rule.Collector = TouristTaxCollector.Host;
        record.DeclaredCollector = TouristTaxCollector.Channel;
        await world.Db.SaveChangesAsync();
        Assert.Equal(20m, await BoxAsync());

        // A rule that is not active does not count; the collector confirmed with the calculation does when nothing else says.
        rule.IsActive = false;
        record.DeclaredCollector = null;
        record.Collector = TouristTaxCollector.Channel;
        await world.Db.SaveChangesAsync();
        Assert.Equal(8m, await BoxAsync());
        record.Collector = null;
        await world.Db.SaveChangesAsync();
        Assert.Equal(20m, await BoxAsync());
    }

    /// <summary>
    /// Money: the tourist tax of a stay that owes nothing is not in the box. A cancelled booking, a request and a no-show can keep revenue (a payment the guest does not
    /// get back), but nobody collects a tax for a stay that never took place (the one definition of an active booking). The revenue stays in the lines, the net does not move.
    /// </summary>
    [Fact]
    public async Task StatementPdf_TouristTaxOfAStayThatOwesNothingIsNotInTheBox_Test()
    {
        var world = await World.CreateAsync();
        var content = await world.Documents.GetContentAsync(world.OrgId, world.VersionId);
        Assert.Equal(20m, content.TouristTax.Sum(t => t.Amount));

        // A cancelled booking, then a no-show: the other stay is still in the box with its own tax.
        await world.ChangeBookingAsync(world.DirectBookingId, b => b.Status = BookingStatus.Cancelled);
        Assert.Equal(12m, (await world.Documents.GetContentAsync(world.OrgId, world.VersionId)).TouristTax.Sum(t => t.Amount));
        await world.ChangeBookingAsync(world.DirectBookingId, b => { b.Status = BookingStatus.Confirmed; b.NoShowAt = World.Cutoff; });
        Assert.Equal(12m, (await world.Documents.GetContentAsync(world.OrgId, world.VersionId)).TouristTax.Sum(t => t.Amount));

        // A booking that is only a request: nothing is left, and the CSV has no row of the box either.
        await world.ChangeBookingAsync(world.PortalBookingId, b => b.Status = BookingStatus.Pending);
        var none = await world.Documents.GetContentAsync(world.OrgId, world.VersionId);
        Assert.Empty(none.TouristTax);
        var csv = Encoding.UTF8.GetString((await world.Documents.GetCsvAsync(world.OrgId, world.VersionId, "it")).Content);
        Assert.DoesNotContain("Tassa di soggiorno (informativa);", csv, StringComparison.Ordinal);

        // The revenue of those stays is still in the statement and the net is the version's.
        Assert.Equal(content.Lines.Count, none.Lines.Count);
        Assert.Equal(content.Totals, none.Totals);
    }

    /// <summary>
    /// PROOF TEST: money rule (FIX/RES-OWNERS, S007, ⚖ C5). Until the issuer confirms its own list of the portals that collect the tourist tax, the box of the statement leaves
    /// out the stays of the portals of the shipped default (<c>SwissTouristTax:PlatformCollected</c>), the very list the guest documents use for that issuer
    /// (<see cref="IGuestDocumentLineBuilder.PlatformCollectedChannelsOf"/>): the statement and the documents never disagree on who collects. The issuer's own answer, an empty
    /// one included, replaces the default; an Italian issuer has none.
    /// </summary>
    [Fact]
    public async Task StatementTouristTaxBox_BeforeIssuerConfirmation_UsesThePlatformDefaults_Test()
    {
        var swiss = new Casazen.Core.Options.SwissTouristTaxOptions { PlatformCollected = [BookingSource.BookingCom] };
        var world = await World.CreateAsync(swiss: swiss);
        var documents = new GuestDocumentLineBuilder(world.Db, new ResxDocumentTexts(), Options.Create(new Casazen.Core.Options.FiscalDocumentRulesOptions()), Options.Create(swiss));
        async Task<decimal> BoxAsync() => (await world.Documents.GetContentAsync(world.OrgId, world.VersionId)).TouristTax.Sum(t => t.Amount);

        // The agency has no issuer profile at all: nothing is confirmed and Italy has no default, so it collects the 12 of the portal stay and the 8 of the direct one.
        Assert.Equal(20m, await BoxAsync());

        // A Swiss issuer that has confirmed nothing: Booking.com collects the tax of its stay itself by default, as the documents of that stay say, so only the 8 is the agency's.
        var issuer = new OrgFiscalIssuerProfile { OrgId = world.OrgId, CountryCode = "CH" };
        world.Db.OrgFiscalIssuerProfiles.Add(issuer);
        await world.Db.SaveChangesAsync();
        Assert.Equal([BookingSource.BookingCom], documents.PlatformCollectedChannelsOf(issuer));
        Assert.Equal(8m, await BoxAsync());

        // The issuer confirms that no portal collects it: that is an answer and not «use the default»; the documents and the box both put the 12 back.
        issuer.PlatformCollectedTouristTaxChannelsJson = "[]";
        await world.Db.SaveChangesAsync();
        Assert.Empty(documents.PlatformCollectedChannelsOf(issuer));
        Assert.Equal(20m, await BoxAsync());

        // The issuer confirms Booking.com: it is its own list now, and the box follows it as before.
        issuer.PlatformCollectedTouristTaxChannelsJson = "[\"BookingCom\"]";
        await world.Db.SaveChangesAsync();
        Assert.Equal([BookingSource.BookingCom], documents.PlatformCollectedChannelsOf(issuer));
        Assert.Equal(8m, await BoxAsync());

        // Confirmed on another portal, the stay of Booking.com is the agency's whatever the default says.
        issuer.PlatformCollectedTouristTaxChannelsJson = "[\"Airbnb\"]";
        await world.Db.SaveChangesAsync();
        Assert.Equal(20m, await BoxAsync());

        // An Italian issuer that has confirmed nothing has no default: the list of the platform is the Swiss one.
        issuer.CountryCode = "IT";
        issuer.PlatformCollectedTouristTaxChannelsJson = null;
        await world.Db.SaveChangesAsync();
        Assert.Empty(documents.PlatformCollectedChannelsOf(issuer));
        Assert.Equal(20m, await BoxAsync());
    }

    /// <summary>
    /// PROOF TEST: money rule (FIX/RES-OWNERS, S016). The agency reads a version with the tourist tax box of the statement, with its amounts: the same rows, in the same order and
    /// currency, as the owner portal reads for that version (both are the content of the one source of the PDF and the CSV), the amounts of the box are in the files too, and
    /// they are outside the totals of the version; a tax that changes changes both boxes alike. A caller of another organization gets the 404 of a version that does not exist
    /// and one with a partial reach is refused before anything is read.
    /// </summary>
    [Fact]
    public async Task AgencyStatement_GroupedTouristTax_EqualsThePortalOne_Test()
    {
        var world = await World.CreateAsync();

        async Task<(OwnerStatementVersionDetailDto Agency, OwnerPortalStatementDetailDto Portal)> ReadBothAsync()
        {
            var answer = await Controller(world.OrgId, new HostScope(world.OrgId), world.Accounting()).Version(world.VersionId, world.Documents);
            var agency = Assert.IsType<OwnerStatementVersionDetailDto>(Assert.IsType<OkObjectResult>(answer.Result).Value);
            var content = await world.Documents.GetContentAsync(world.OrgId, world.VersionId);
            var summary = new OwnerPortalStatementInfo(
                world.VersionId, world.OrgId, content.AgencyName, content.Year, content.Month, content.VersionNumber, content.Currency, World.Cutoff.AddHours(1), false, null, false, content.Totals);
            return (agency, OwnerPortalStatementDetailDto.From(new OwnerPortalStatementDetailInfo(summary, content)));
        }

        // The 12 of the portal stay and the 8 of the direct one, by check-in, in francs, for the agency as for the owner.
        var (agency, portal) = await ReadBothAsync();
        Assert.Equal(portal.TouristTax, agency.TouristTax);
        Assert.Equal([12m, 8m], agency.TouristTax.Select(t => t.Amount));
        Assert.All(agency.TouristTax, t => Assert.Equal("CHF", t.Currency));
        Assert.Equal(new[] { "BookingCom", "Direct" }, agency.TouristTax.Select(t => t.Channel));

        // The agency keeps what it read before: the version and its lines, and the box is outside every total.
        Assert.Equal((world.VersionId, 10), (agency.Version.Id, agency.Version.EntryCount));
        Assert.NotEmpty(agency.Entries);
        Assert.Equal(580m, portal.Statement.Totals.Net);
        var csv = Encoding.UTF8.GetString((await world.Documents.GetCsvAsync(world.OrgId, world.VersionId, "it")).Content);
        Assert.Contains("Tassa di soggiorno (informativa);", csv, StringComparison.Ordinal);
        Assert.Contains("12,00", csv, StringComparison.Ordinal);
        Assert.Contains("8,00", csv, StringComparison.Ordinal);

        // The tax of the portal stay changes: the agency's box and the portal's change together, and the net stays what the version says.
        await world.SetTouristTaxAsync(world.PortalBookingId, 99m);
        var (changedAgency, changedPortal) = await ReadBothAsync();
        Assert.Equal(changedPortal.TouristTax, changedAgency.TouristTax);
        Assert.Equal([99m, 8m], changedAgency.TouristTax.Select(t => t.Amount));
        Assert.Equal(580m, changedPortal.Statement.Totals.Net);

        // Another organization asks for the version: the 404 of a version that does not exist, and no tax of ours.
        var other = await Assert.ThrowsAsync<NotFoundException>(
            () => Controller(world.OtherOrgId, new HostScope(world.OtherOrgId), world.Accounting()).Version(world.VersionId, world.Documents));
        Assert.Equal(OwnerAccountingErrors.VersionNotFound, other.Code);

        // A caller with a partial reach is refused before the content is read (a strict mock would have thrown).
        var files = new Mock<IOwnerStatementDocuments>(MockBehavior.Strict);
        var refused = await Controller(world.OrgId, new HostScope(world.OrgId, GrantedToUserId: "auth0|caller"), world.Accounting()).Version(world.VersionId, files.Object);
        Assert.IsType<ForbidResult>(refused.Result);
        files.VerifyNoOtherCalls();
    }

    /// <summary>
    /// Permission: a version of another organization is the 404 of one that does not exist, for the content, the PDF and the CSV, and the endpoints refuse a caller
    /// whose reach is not the whole org before anything is read.
    /// </summary>
    [Fact]
    public async Task StatementFile_OtherOrganizationVersion404_Test()
    {
        var world = await World.CreateAsync();

        foreach (var ask in new Func<Task>[]
                 {
                     () => world.Documents.GetContentAsync(world.OtherOrgId, world.VersionId),
                     () => world.Documents.GetPdfAsync(world.OtherOrgId, world.VersionId),
                     () => world.Documents.GetCsvAsync(world.OtherOrgId, world.VersionId),
                     () => world.Documents.GetPdfAsync(world.OrgId, Guid.NewGuid()),
                 })
        {
            var missing = await Assert.ThrowsAsync<NotFoundException>(ask);
            Assert.Equal(OwnerAccountingErrors.VersionNotFound, missing.Code);
        }

        // The owner's own organization is served.
        Assert.NotEmpty((await world.Documents.GetPdfAsync(world.OrgId, world.VersionId)).Content);

        // A caller restricted to some properties (a collaborator «Solo alcuni») is refused by the controller, whatever the version.
        foreach (var restricted in new[] { new HostScope(world.OrgId, GrantedToUserId: "auth0|solo-alcuni"), new HostScope(world.OrgId, OwnerId: "auth0|solo-alcuni") })
        {
            var files = new Mock<IOwnerStatementDocuments>(MockBehavior.Strict);
            var controller = Controller(world.OrgId, restricted);

            Assert.IsType<ForbidResult>(await controller.VersionPdf(world.VersionId, null, files.Object));
            Assert.IsType<ForbidResult>(await controller.VersionCsv(world.VersionId, null, files.Object));
            files.VerifyNoOtherCalls();
        }

        // The whole org gets the file of its version; another org's version is the 404 of the service.
        var whole = Controller(world.OrgId, new HostScope(world.OrgId));
        var served = Assert.IsType<FileContentResult>(await whole.VersionPdf(world.VersionId, null, world.Documents));
        Assert.Equal("application/pdf", served.ContentType);
        var other = Controller(world.OtherOrgId, new HostScope(world.OtherOrgId));
        await Assert.ThrowsAsync<NotFoundException>(() => other.VersionCsv(world.VersionId, null, world.Documents));
    }

    /// <summary>
    /// The publication stores the PDF once, with the version, as an owner document of type statement whose id is the version's, with its hash: the file the owner
    /// downloads is that file (the download is written), and the agency gets the same bytes. A withdrawn document is not handed out.
    /// </summary>
    [Fact]
    public async Task StatementPdf_PublishedStoredOnceWithTheVersion_AndIsTheFileTheOwnerGets_Test()
    {
        var world = await World.CreateAsync(approved: true);
        var accounting = world.Accounting();

        var published = await accounting.PublishVersionAsync(world.OrgId, world.VersionId, "auth0|publisher");

        Assert.Equal(OwnerStatementVersionStatus.Published, published.Status);
        var document = await world.Db.OwnerDocuments.AsNoTracking().SingleAsync();
        Assert.Equal((world.VersionId, world.OwnerId, OwnerDocumentType.Statement, "auth0|publisher"), (document.Id, document.PropertyOwnerId, document.Type, document.CreatedByUserId));
        Assert.Equal("Rendiconto 10/2026 - versione 1", document.Title);
        var file = await world.Db.OwnerDocumentVersions.AsNoTracking().SingleAsync();
        var bytes = await ReadAsync(world.Storage, file.StorageKey);
        Assert.Equal((Convert.ToHexStringLower(SHA256.HashData(bytes)), "application/pdf", (long)bytes.Length), (file.Sha256, file.ContentType, file.SizeBytes));
        Assert.Equal("%PDF", Encoding.ASCII.GetString(bytes, 0, 4));

        // The agency's PDF of a published version is the stored file, byte for byte; asking again for the stored file changes nothing.
        Assert.Equal(bytes, (await world.Documents.GetPdfAsync(world.OrgId, world.VersionId, "en")).Content);
        Assert.Equal(StoredStatementPdf.None, await StoreAgainAsync(world, published));
        Assert.Equal((1, 1), (await world.Db.OwnerDocuments.CountAsync(), await world.Db.OwnerDocumentVersions.CountAsync()));

        // The owner gets exactly that file, and the download is written before it is handed out.
        var owner = Guid.NewGuid();
        await using (var download = (await world.Documents.OpenPublishedPdfAsync(world.OrgId, world.VersionId, owner))!.Content)
        {
            using var copy = new MemoryStream();
            await download.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
        }

        var audit = await world.Db.OwnerDocumentDownloads.AsNoTracking().SingleAsync();
        Assert.Equal((world.VersionId, file.Id, owner), (audit.OwnerDocumentId, audit.OwnerDocumentVersionId, audit.OwnerUserId));

        // The agency withdraws the document from the portal: the owner stops getting it.
        var row = await world.Db.OwnerDocuments.SingleAsync();
        row.RevokedAt = world.Clock.GetUtcNow().UtcDateTime;
        await world.Db.SaveChangesAsync();
        Assert.Null(await world.Documents.OpenPublishedPdfAsync(world.OrgId, world.VersionId, owner));
        Assert.Null(await world.Documents.OpenPublishedPdfAsync(world.OtherOrgId, world.VersionId, owner));
    }

    /// <summary>
    /// Privacy and data: the publication tells the owner. The e-mail is queued after the version was saved as published, to the address of the person of the portal, with
    /// no amount in it. A queue that refuses the e-mail, or fails, never turns the publication into an error and is never silent: the agency gets a «To do» about the
    /// version (once, even if it is raised again), and nothing is raised when nobody in the portal has to be told.
    /// </summary>
    [Fact]
    public async Task OwnerStatementPublication_TellsTheOwnerAfterTheSave_AndRaisesAToDoWhenItCannot_Test()
    {
        // The notice goes out after the save, once, to the owner.
        var world = await World.CreateAsync(approved: true);
        await world.AddPortalOwnerAsync("proprietario@example.com");
        var statusWhenQueued = OwnerStatementVersionStatus.Approved;
        world.Queue.WhenQueued = () => statusWhenQueued = world.Db.OwnerStatementVersions.AsNoTracking().Single().Status;

        var published = await world.Accounting().PublishVersionAsync(world.OrgId, world.VersionId, "auth0|publisher");

        Assert.Equal(OwnerStatementVersionStatus.Published, published.Status);
        Assert.Equal(OwnerStatementVersionStatus.Published, statusWhenQueued);
        var sent = Assert.Single(world.Queue.Sent);
        Assert.Equal(("proprietario@example.com", EmailTemplates.Names.OwnerStatementPublished), (sent.To, sent.Template));
        Assert.Contains("https://app.example.com/portale-proprietari/contabilita", sent.Content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("280,00", sent.Content.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain("280.00", sent.Content.HtmlBody, StringComparison.Ordinal);
        Assert.Empty(await world.Db.InternalActivities.AsNoTracking().ToListAsync());

        // The queue refuses the e-mail: the publication stands and the agency gets a To do about the version, once even if it is raised again.
        var refused = await World.CreateAsync(approved: true);
        await refused.AddPortalOwnerAsync("proprietario@example.com");
        refused.Queue.Accepts = false;
        var stands = await refused.Accounting().PublishVersionAsync(refused.OrgId, refused.VersionId, "auth0|publisher");
        Assert.Equal(OwnerStatementVersionStatus.Published, stands.Status);
        await refused.Documents.TellOwnerPublishedAsync(refused.Version);
        var todo = Assert.Single(await refused.Db.InternalActivities.AsNoTracking().ToListAsync());
        Assert.Equal(nameof(ActivityType.OwnerStatementNoticeFailed), todo.ActivityType);
        Assert.Equal(ActivityAction.ReviewOwnerStatement, todo.Action);
        Assert.Equal(OwnerStatementDocuments.OwnerStatementVersionEntityType, todo.EntityType);
        Assert.Equal(refused.VersionId, todo.EntityId);
        Assert.Equal(refused.OrgId, todo.OrgId);
        Assert.Equal(OwnerStatementDocuments.NoticeFailedReason, todo.Reason);

        // The queue itself fails: the same, and the exception is not turned into an error of the publication.
        var failing = await World.CreateAsync(approved: true);
        await failing.AddPortalOwnerAsync("proprietario@example.com");
        failing.Queue.Fails = new InvalidOperationException("the queue is down");
        var survives = await failing.Accounting().PublishVersionAsync(failing.OrgId, failing.VersionId, "auth0|publisher");
        Assert.Equal(OwnerStatementVersionStatus.Published, survives.Status);
        Assert.Equal(failing.VersionId, Assert.Single(await failing.Db.InternalActivities.AsNoTracking().ToListAsync()).EntityId);

        // Nobody in the portal to tell: no e-mail and nothing to raise.
        var nobody = await World.CreateAsync(approved: true);
        await nobody.Accounting().PublishVersionAsync(nobody.OrgId, nobody.VersionId, "auth0|publisher");
        Assert.Empty(nobody.Queue.Sent);
        Assert.Empty(await nobody.Db.InternalActivities.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Data: the PDF stored at the publication is the file that was published, for good. The owner documents refuse a new version of it (409), a version that got there
    /// some other way is never the file handed out, and a file in the bucket that is no longer the one that was published is not handed to the owner.
    /// </summary>
    [Fact]
    public async Task StatementPdf_PublishedFileIsNeverReplaced_Test()
    {
        var world = await World.CreateAsync(approved: true);
        await world.Accounting().PublishVersionAsync(world.OrgId, world.VersionId, "auth0|publisher");
        var published = await world.Db.OwnerDocumentVersions.AsNoTracking().SingleAsync();
        var original = await ReadAsync(world.Storage, published.StorageKey);
        var forged = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /Catalog >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n");

        // The agency cannot add a version to the document of a statement: 409, and nothing is stored.
        var input = new OwnerDocumentVersionInput(null, null);
        var upload = new OwnerFileUpload("altro.pdf", forged);
        var refusal = await Assert.ThrowsAsync<DomainConflictException>(() => world.Collaboration().AddDocumentVersionAsync(world.OrgId, world.VersionId, input, upload, "auth0|agent"));
        Assert.Equal(OwnerStatementFileErrors.FileImmutable, refusal.Code);
        Assert.Equal(1, await world.Db.OwnerDocumentVersions.CountAsync());

        // A second version that got there some other way (a direct insert) is not the file handed out: the first one is the file that was published.
        world.Storage.Add("owner-documents/test/planted.pdf", forged);
        world.Db.OwnerDocumentVersions.Add(new OwnerDocumentVersion
        {
            Id = Guid.NewGuid(),
            OrgId = world.OrgId,
            OwnerDocumentId = world.VersionId,
            VersionNumber = 2,
            StorageKey = "owner-documents/test/planted.pdf",
            FileName = "planted.pdf",
            ContentType = "application/pdf",
            SizeBytes = forged.Length,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(forged)),
            ScanEngine = "test",
            UploadedAt = world.Clock.GetUtcNow().UtcDateTime,
            UploadedByUserId = "auth0|agent",
        });
        await world.Db.SaveChangesAsync();
        Assert.Equal(original, (await world.Documents.GetPdfAsync(world.OrgId, world.VersionId)).Content);
        await using (var download = (await world.Documents.OpenPublishedPdfAsync(world.OrgId, world.VersionId, Guid.NewGuid()))!.Content)
        {
            using var copy = new MemoryStream();
            await download.CopyToAsync(copy);
            Assert.Equal(original, copy.ToArray());
        }

        // The file in the bucket is replaced: it is no longer what was published (its SHA-256 differs), so the owner is not given it and no download is written for it.
        var downloads = await world.Db.OwnerDocumentDownloads.CountAsync();
        world.Storage.Add(published.StorageKey, forged);
        Assert.Null(await world.Documents.OpenPublishedPdfAsync(world.OrgId, world.VersionId, Guid.NewGuid()));
        Assert.Equal(downloads, await world.Db.OwnerDocumentDownloads.CountAsync());
        var rebuilt = (await world.Documents.GetPdfAsync(world.OrgId, world.VersionId)).Content;
        Assert.NotEqual(forged, rebuilt);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(rebuilt, 0, 4));
    }

    /// <summary>
    /// The PDF of a published statement is not among the documents the agency shares (one for each owner and month would push the mandates and the insurances out of a list
    /// that stops at 500): the list has the shared documents only, for the org and for the owner, and the statement file stays reachable by its id.
    /// </summary>
    [Fact]
    public async Task StatementPdf_IsNotAmongTheDocumentsTheAgencyShares_Test()
    {
        var world = await World.CreateAsync(approved: true);
        await world.Accounting().PublishVersionAsync(world.OrgId, world.VersionId, "auth0|publisher");
        var published = await world.Db.OwnerDocumentVersions.AsNoTracking().SingleAsync();
        var pdf = await ReadAsync(world.Storage, published.StorageKey);
        var collaboration = world.Collaboration();

        // The agency shares a mandate with the owner: that is the one document of the list.
        var mandate = new ShareOwnerDocument(world.OwnerId, null, OwnerDocumentType.Mandate, "Mandato di gestione", null, null);
        var shared = await collaboration.ShareDocumentAsync(world.OrgId, mandate, new OwnerFileUpload("mandato.pdf", pdf), "auth0|agent");
        Assert.Equal([shared.Document.Id], (await collaboration.ListDocumentsForAgencyAsync(world.OrgId, null)).Select(d => d.Document.Id));
        Assert.Equal([shared.Document.Id], (await collaboration.ListDocumentsForAgencyAsync(world.OrgId, world.OwnerId)).Select(d => d.Document.Id));

        // The statement file is still a document of the portal, reachable by its id: its downloads, its withdrawal.
        await using (var download = (await world.Documents.OpenPublishedPdfAsync(world.OrgId, world.VersionId, Guid.NewGuid()))!.Content)
            Assert.NotNull(download);

        Assert.Single(await collaboration.ListDocumentDownloadsAsync(world.OrgId, world.VersionId));
        var withdrawn = await collaboration.SetDocumentVisibilityAsync(world.OrgId, world.VersionId, visible: false, "auth0|agent");
        Assert.False(withdrawn.Visible);
    }

    /// <summary>The PDF stored when a save fails is removed again, and a version that is not published stores no file.</summary>
    [Fact]
    public async Task StatementPdf_AFailedSaveRemovesTheStoredFile_Test()
    {
        var world = await World.CreateAsync();
        var entries = await world.Db.OwnerLedgerEntries.AsNoTracking().Where(e => e.PeriodMonth == 10).ToListAsync();

        var stored = await world.Documents.StorePublishedPdfAsync(world.Version, entries, null);

        Assert.NotNull(stored.StorageKey);
        Assert.True(await world.Storage.ExistsAsync(StorageBucket.Private, stored.StorageKey!));
        await world.Documents.DiscardStoredPdfAsync(stored);
        Assert.False(await world.Storage.ExistsAsync(StorageBucket.Private, stored.StorageKey!));
        await world.Documents.DiscardStoredPdfAsync(StoredStatementPdf.None);
    }

    /// <summary>
    /// The CSV has the lines of every part, the total of each, the net and the tourist tax box (in its own part, with its own currency), in rows of the same nine cells.
    /// </summary>
    [Fact]
    public async Task StatementCsv_HasTheLinesTheTotalsTheNetAndTheBox_Test()
    {
        var world = await World.CreateAsync();

        var file = await world.Documents.GetCsvAsync(world.OrgId, world.VersionId, "it");

        Assert.Equal("text/csv", file.ContentType);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, file.Content.Take(3));
        var rows = Encoding.UTF8.GetString(file.Content).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var cells = rows.Select(r => r.Split(';')).ToList();
        var table = cells.Where(c => c.Length == 9).ToList();
        Assert.True(table.Count >= 14, $"only {table.Count} rows of nine cells");
        Assert.Contains(table, c => c[0] == "Soggiorni" && c[2] == "TEST Casa Lugano" && c[4] == "Booking.com" && c[7] == "500,00");
        Assert.Contains(table, c => c[0] == "Commissioni dei canali" && c[7] == "-75,00");
        Assert.Contains(table, c => c[0] == "Ritenuta" && c[7] == "-10,00");
        Assert.Contains(table, c => c[0] == "Riaddebiti e spese" && c[5] == "Pulizie" && c[7] == "-40,00");
        Assert.Contains(table, c => c[0] == "Riaddebiti e spese" && c[5] == "Riparazione rubinetto" && c[7] == "-20,00");
        Assert.Contains(table, c => c[0] == "Totale - Soggiorni" && c[7] == "800,00");
        Assert.Contains(table, c => c[0] == "Saldo iniziale" && c[7] == "100,00");
        Assert.Contains(table, c => c[0] == "Totale del periodo" && c[7] == "580,00");
        Assert.Contains(table, c => c[0] == "Saldo finale" && c[7] == "280,00");
        var box = table.Where(c => c[0] == "Tassa di soggiorno (informativa)").ToList();
        Assert.Equal(20m, box.Sum(c => decimal.Parse(c[7].Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture)));
        Assert.All(box, c => Assert.Equal("CHF", c[8]));
    }

    [Fact]
    public async Task StatementCsv_PreservesTheOriginalMoneyFullFxRateAndLedgerDates()
    {
        var world = await World.CreateAsync(convertedStay: true);

        var file = await world.Documents.GetCsvAsync(world.OrgId, world.VersionId, "it");
        var rows = Encoding.UTF8.GetString(file.Content).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(row => row.Split(';')).ToList();
        var header = Assert.Single(rows, row => row[0] == "ledger_entry_id");
        Assert.Equal(14, header.Length);
        var original = await world.Db.OwnerLedgerEntries.AsNoTracking().SingleAsync(entry => entry.OriginalCurrency == "EUR");
        var evidence = Assert.Single(rows, row => row[0] == original.Id.ToString());

        Assert.Equal("Booking", evidence[1]);
        Assert.Equal(world.PortalBookingId.ToString(), evidence[2]);
        Assert.Equal(world.PortalBookingId.ToString("N"), evidence[3]);
        Assert.Equal(new[] { "2026-10-05", "2026-10-08", "", "535,00", "EUR", "500,00", "CHF", "0,93457944", "TEST BCE", "2026-10-05" }, evidence[4..]);
        Assert.Contains(rows, row => row[0].Contains("Non sostituiscono i cambi fiscali IVA", StringComparison.Ordinal));
        // The original statement table remains usable by existing importers.
        Assert.Contains(rows, row => row.Length == 9 && row[0] == "Soggiorni" && row[7] == "500,00" && row[8] == "CHF");
    }

    /// <summary>The label of the commission of the channels follows who holds the portal account of the listing: the owner (V1, the default), or the agency (V2) with the invoice number.</summary>
    [Fact]
    public async Task StatementPdf_ChannelFeesLabelFollowsWhoHoldsThePortalAccount_Test()
    {
        var world = await World.CreateAsync();
        var texts = new ResxDocumentTexts();
        var content = await world.Documents.GetContentAsync(world.OrgId, world.VersionId, "it");

        var owner = TextOf(OwnerStatementLayout.Pdf(content, texts).Blocks);
        Assert.Contains("Trattenuta dal portale (fattura del portale a tuo nome)", owner, StringComparison.Ordinal);
        Assert.DoesNotContain("riaddebitate dal gestore", owner, StringComparison.Ordinal);

        // A listing whose portal account is the agency's (E14 says so through the resolver): the commission is recharged by the property manager, with its invoice.
        var agency = content with
        {
            Lines = [.. content.Lines.Select(l => l.Section == OwnerStatementSection.ChannelFees ? l with { AgencyHoldsPortalAccount = true } : l)],
            OwnerInvoiceNumber = "PM2026/7",
            OwnerInvoiceDate = new DateOnly(2026, 11, 12),
        };
        var rechargedText = TextOf(OwnerStatementLayout.Pdf(agency, texts).Blocks);
        Assert.Contains("Commissioni del portale riaddebitate dal gestore (fattura n. PM2026/7)", rechargedText, StringComparison.Ordinal);
        Assert.Contains("Fattura del gestore n. PM2026/7 (12/11/2026)", rechargedText, StringComparison.Ordinal);
        Assert.DoesNotContain("Trattenuta dal portale", rechargedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Money and trust (FIX/BE-V2-NO-RECHARGE, decision 1 of 20/11/2026): the commission of an account of the agency that the organization does not recharge (V2 without the recharge) is
    /// borne by the agency, so the posting writes it as a line for information only (<see cref="OwnerLedgerEntryType.ChannelFeeBorneByAgency"/>). The statement tells it in words of its own, in
    /// both languages, the commission shown as the amount the owner is not charged: never with the sentence of V1, the one of the recharge (which names an invoice) or the one of a commission
    /// charged to the owner, and in no total: the net is the one of the same statement whose commission the owner bears (V1), plus the commission. What the line is comes from the line as it
    /// was posted, never from the setting of today; read through the same resolver, a channel fee the owner bears keeps the flag of its own case (the recharge, V1, or charged before the
    /// rule): the four cases never overlap.
    /// </summary>
    [Fact]
    public async Task StatementV2WithoutRecharge_ShowsItsOwnLabel_AndNoDeduction_Test()
    {
        var texts = new ResxDocumentTexts();
        var plain = await World.CreateAsync();
        var asV1 = await plain.Documents.GetContentAsync(plain.OrgId, plain.VersionId, "it");

        var world = await World.CreateAsync(readsPortalAccounts: true, borneByAgency: true);
        var profile = new OrgFiscalIssuerProfile { OrgId = world.OrgId, PortalAccountHolder = PortalAccountHolder.Agency, RechargePortalCommissions = false };
        world.Db.OrgFiscalIssuerProfiles.Add(profile);
        await world.Db.SaveChangesAsync();

        var italian = await world.Documents.GetContentAsync(world.OrgId, world.VersionId, "it");

        // Money: the commission is a line for information only, in the part of the commissions; the owner keeps it (the net of V1 plus 75.00), and the totals are the ones the version stores.
        var fee = Assert.Single(italian.Of(OwnerStatementSection.ChannelFees));
        Assert.Equal((-75m, false, true, true), (fee.Amount, fee.AgencyHoldsPortalAccount, fee.AgencyHoldsPortalAccountWithoutRecharge, fee.Informational));
        Assert.Equal(OwnerStatementTotals.Of(world.Version), italian.Totals);
        Assert.Equal((0m, 655m, 355m), (italian.Totals.ChannelFees, italian.Totals.Period, italian.Totals.ClosingBalance));
        Assert.Equal((asV1.Totals.Period + 75m, asV1.Totals.ClosingBalance + 75m), (italian.Totals.Period, italian.Totals.ClosingBalance));

        // The words in Italian, with an invoice of the property manager linked: its own label over the commission the agency bears, never the sentence of V1, the one of the recharge
        // (which would name that invoice) or the one of a commission charged to the owner; and nothing taken from the net (no negative amount of it).
        var invoiced = italian with { OwnerInvoiceNumber = "PM2026/7", OwnerInvoiceDate = new DateOnly(2026, 11, 12) };
        var italianText = TextOf(OwnerStatementLayout.Pdf(invoiced, texts).Blocks);
        Assert.Contains("Commissione del portale sostenuta dall'agenzia, non addebitata al proprietario", italianText, StringComparison.Ordinal);
        Assert.Contains(Chf(75m), italianText, StringComparison.Ordinal);
        Assert.DoesNotContain(Chf(-75m), italianText, StringComparison.Ordinal);
        Assert.DoesNotContain("Trattenuta dal portale", italianText, StringComparison.Ordinal);
        Assert.DoesNotContain("fattura del portale a tuo nome", italianText, StringComparison.Ordinal);
        Assert.DoesNotContain("riaddebitate dal gestore", italianText, StringComparison.Ordinal);
        Assert.DoesNotContain("(fattura n. PM2026/7)", italianText, StringComparison.Ordinal);
        Assert.DoesNotContain("senza riaddebito in fattura", italianText, StringComparison.Ordinal);

        // The words in English, which are not the Italian ones.
        var english = await world.Documents.GetContentAsync(world.OrgId, world.VersionId, "en");
        var englishText = TextOf(OwnerStatementLayout.Pdf(english with { OwnerInvoiceNumber = "PM2026/7", OwnerInvoiceDate = new DateOnly(2026, 11, 12) }, texts).Blocks);
        Assert.Contains("Portal commission covered by the property manager, not deducted from your statement", englishText, StringComparison.Ordinal);
        Assert.DoesNotContain("Withheld by the portal", englishText, StringComparison.Ordinal);
        Assert.DoesNotContain("portal invoice in your name", englishText, StringComparison.Ordinal);
        Assert.DoesNotContain("recharged by the property manager", englishText, StringComparison.Ordinal);
        Assert.NotEqual(texts.Get("StatementFileChannelFeesAgencyNotRecharged", "it"), texts.Get("StatementFileChannelFeesAgencyNotRecharged", "en"));

        // The owner portal gets the same fact next to the flag of the recharge, so that it can say it too (the two are never true together); the total of the part leaves it out.
        var portalPart = OwnerPortalStatementSectionDto.From(OwnerStatementSection.ChannelFees, italian.Of(OwnerStatementSection.ChannelFees));
        var portal = Assert.Single(portalPart.Lines);
        Assert.Equal((-75m, false, true), (portal.Amount, portal.ChannelFeeRechargedByAgency, portal.ChannelFeeBilledToAgencyNotRecharged));
        Assert.Equal(0m, portalPart.Total);

        // The CSV: the commission follows the net in a part of its own, as the amount the agency bears; it is not a row of the commissions of the channels, whose total has none of it.
        var cells = Encoding.UTF8.GetString(OwnerStatementLayout.Csv(italian, texts)).TrimStart((char)0xFEFF).Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(r => r.Split(';')).ToList();
        Assert.Contains(cells, c => c.Length == 9 && c[0] == "Commissione del portale sostenuta dall'agenzia, non addebitata al proprietario" && c[7] == "75,00");
        Assert.Contains(cells, c => c.Length == 9 && c[0] == "Totale - Commissioni dei canali" && c[7] == "0,00");
        Assert.DoesNotContain(cells, c => c.Length == 9 && c[0] == "Commissioni dei canali");
        Assert.Contains(cells, c => c.Length == 9 && c[0] == "Saldo finale" && c[7] == "355,00");

        // What the line is comes from the line as it was posted, never from the setting of today: turning the recharge back on, or the account back to the owner, does not make it a
        // commission the owner bears (only a new posting of the month would).
        profile.RechargePortalCommissions = true;
        await world.Db.SaveChangesAsync();
        var stillBorne = Assert.Single((await world.Documents.GetContentAsync(world.OrgId, world.VersionId, "it")).Of(OwnerStatementSection.ChannelFees));
        Assert.Equal((false, true, false), (stillBorne.AgencyHoldsPortalAccount, stillBorne.AgencyHoldsPortalAccountWithoutRecharge, stillBorne.AgencyAccountChargedWithoutRecharge));

        // A channel fee the owner bears, through the same resolver: the recharge (V2), a commission charged to the owner on an account of the agency without the recharge (a line posted
        // before the rule) and the account of the owner (V1): each with its own flag, never the one of the commission the agency bears.
        var charged = await World.CreateAsync(readsPortalAccounts: true);
        var chargedProfile = new OrgFiscalIssuerProfile { OrgId = charged.OrgId, PortalAccountHolder = PortalAccountHolder.Agency, RechargePortalCommissions = true };
        charged.Db.OrgFiscalIssuerProfiles.Add(chargedProfile);
        await charged.Db.SaveChangesAsync();
        async Task<(bool Recharged, bool Borne, bool Charged, bool Informational)> FlagsAsync()
        {
            var line = Assert.Single((await charged.Documents.GetContentAsync(charged.OrgId, charged.VersionId, "it")).Of(OwnerStatementSection.ChannelFees));
            return (line.AgencyHoldsPortalAccount, line.AgencyHoldsPortalAccountWithoutRecharge, line.AgencyAccountChargedWithoutRecharge, line.Informational);
        }

        Assert.Equal((true, false, false, false), await FlagsAsync());
        chargedProfile.RechargePortalCommissions = false;
        await charged.Db.SaveChangesAsync();
        Assert.Equal((false, false, true, false), await FlagsAsync());
        chargedProfile.PortalAccountHolder = PortalAccountHolder.Owner;
        await charged.Db.SaveChangesAsync();
        Assert.Equal((false, false, false, false), await FlagsAsync());
    }

    /// <summary>
    /// Trust: each commission of a channel is told under the label of its own account, in one statement. An organization whose Booking.com accounts are the owner's (V1) but one, the listing
    /// «TEST Casa Locarno», whose account is the agency's with the recharge turned off (the override of a listing and portal, read through the real resolver), has two commissions of different
    /// amounts: the one of the owner's listing is under the sentence of V1 and the one of the agency's listing, which the agency bears (FIX/BE-V2-NO-RECHARGE: posted as a line for information
    /// only), under the label of its own, each in a table with the total of its own lines, in the order of the kinds and not in the order of the days (the agency's listing is the earlier day).
    /// The same in English, and the totals are the ones the version stores: the commission the owner bears only.
    /// </summary>
    [Fact]
    public async Task StatementChannelFees_AnOwnerListingAndAnAgencyListingWithoutRecharge_AreTwoGroupsEachUnderItsOwnLabel_Test()
    {
        var texts = new ResxDocumentTexts();
        var (world, content) = await StatementOfAnOwnerListingAndAnAgencyListingAsync();

        // The lines: each carries the flags of its own account, the amounts are the ones the ledger posted, and the totals are the ones the version stores (the commission of the owner's
        // listing only: the agency bears the other).
        var fees = content.Of(OwnerStatementSection.ChannelFees);
        var owner = Assert.Single(fees, line => line.PropertyName == "TEST Casa Lugano");
        var agency = Assert.Single(fees, line => line.PropertyName == "TEST Casa Locarno");
        Assert.Equal(2, fees.Count);
        Assert.Equal((-75m, false, false, false), (owner.Amount, owner.AgencyHoldsPortalAccount, owner.AgencyHoldsPortalAccountWithoutRecharge, owner.Informational));
        Assert.Equal((-42m, false, true, true), (agency.Amount, agency.AgencyHoldsPortalAccount, agency.AgencyHoldsPortalAccountWithoutRecharge, agency.Informational));
        Assert.Equal(OwnerStatementTotals.Of(world.Version), content.Totals);
        Assert.Equal((-75m, 580m, 280m), (content.Totals.ChannelFees, content.Totals.Period, content.Totals.ClosingBalance));

        // The owner portal reads the same two facts line by line.
        var portal = OwnerPortalStatementSectionDto.From(OwnerStatementSection.ChannelFees, fees).Lines.ToDictionary(line => line.Property!);
        Assert.Equal((false, false), (portal["TEST Casa Lugano"].ChannelFeeRechargedByAgency, portal["TEST Casa Lugano"].ChannelFeeBilledToAgencyNotRecharged));
        Assert.Equal((false, true), (portal["TEST Casa Locarno"].ChannelFeeRechargedByAgency, portal["TEST Casa Locarno"].ChannelFeeBilledToAgencyNotRecharged));

        // The words in Italian: the sentence of V1 over the owner's listing and, after it though its day is earlier, the label of its own over the agency's, each with a table and its own
        // total; the commission the agency bears is the amount it bears, nothing taken from the owner.
        var italian = ChannelFeeBlocks(OwnerStatementLayout.Pdf(content, texts), texts, "it");
        Assert.Equal(new[] { true, false, true, false }, italian.Select(block => block is PdfParagraph).ToArray());
        Assert.Equal(new PdfParagraph("Trattenuta dal portale (fattura del portale a tuo nome)", Bold: true), italian[0]);
        Assert.Equal(new PdfParagraph("Commissione del portale sostenuta dall'agenzia, non addebitata al proprietario", Bold: true), italian[2]);
        Assert.Equal(new[] { ("TEST Casa Lugano", "Booking.com", Chf(-75m)), ("Totale", string.Empty, Chf(-75m)) }, ChannelFeeCells(italian[1]));
        Assert.Equal(new[] { ("TEST Casa Locarno", "Booking.com", Chf(42m)), ("Totale", string.Empty, Chf(42m)) }, ChannelFeeCells(italian[3]));

        // The two tables keep the columns they always had.
        var columns = new[]
        {
            texts.Get("StatementFileColProperty", "it"),
            texts.Get("StatementFileColStay", "it"),
            texts.Get("StatementFileColChannel", "it"),
            texts.Get("StatementFileColAmount", "it", "CHF"),
        };
        Assert.All(new[] { italian[1], italian[3] }, table => Assert.Equal(columns, Assert.IsType<PdfTable>(table).Columns.Select(column => column.Header).ToArray()));

        // The same two groups, in the same order, in English.
        var english = ChannelFeeBlocks(OwnerStatementLayout.Pdf(await world.Documents.GetContentAsync(world.OrgId, world.VersionId, "en"), texts), texts, "en");
        Assert.Equal(new[] { true, false, true, false }, english.Select(block => block is PdfParagraph).ToArray());
        Assert.Equal(
            new[] { "Withheld by the portal (portal invoice in your name)", "Portal commission covered by the property manager, not deducted from your statement" },
            english.OfType<PdfParagraph>().Select(paragraph => paragraph.Text).ToArray());
        Assert.Equal(new[] { "TEST Casa Lugano", "TEST Casa Locarno" }, english.OfType<PdfTable>().Select(table => table.Rows[0][0]).ToArray());
    }

    /// <summary>
    /// Trust, the three kinds of commission in one statement (asked of the layout with the flags set by hand: an organization recharges all the accounts it holds or none, so the real resolver never
    /// gives the recharge and the account without it together): the account of the owner (V1), the recharge of the agency with the invoice of the property manager, and the account of the agency without
    /// the recharge, whose commission the agency bears (FIX/BE-V2-NO-RECHARGE: for information only, the amount it bears). They are three groups, each a label and a table of its own, in that order
    /// whatever the order of the lines; the first two are what the statement had before the third kind existed (the same blocks as a statement without it) and the third only follows them.
    /// </summary>
    [Fact]
    public async Task StatementChannelFees_TheThreeKindsInOneStatement_AreThreeGroupsInOrder_AndTheFirstTwoAreAsBefore_Test()
    {
        var texts = new ResxDocumentTexts();
        var (_, content) = await StatementOfAnOwnerListingAndAnAgencyListingAsync();
        var fees = content.Of(OwnerStatementSection.ChannelFees);
        var owner = fees.Single(line => line.PropertyName == "TEST Casa Lugano");
        var withoutRecharge = fees.Single(line => line.PropertyName == "TEST Casa Locarno");
        var recharged = owner with { EntryId = Guid.NewGuid(), PropertyName = "TEST Casa Como", Amount = -30m, AgencyHoldsPortalAccount = true };

        // The statement with the given commissions of the channels (the other parts are the ones of the statement above), with an invoice of the property manager linked.
        OwnerStatementContent Statement(params OwnerStatementLine[] channelFees)
        {
            var lines = content.Lines.Where(line => line.Section != OwnerStatementSection.ChannelFees).Concat(channelFees).ToList();
            return content with
            {
                Lines = lines,
                Totals = OwnerStatementLayout.TotalsOf(content.Totals.OpeningBalance, lines),
                OwnerInvoiceNumber = "PM2026/7",
                OwnerInvoiceDate = new DateOnly(2026, 11, 12),
            };
        }

        // The lines come in an order that is neither the one of the kinds nor the one of the days: the layout makes the groups.
        var three = ChannelFeeBlocks(OwnerStatementLayout.Pdf(Statement(withoutRecharge, recharged, owner), texts), texts, "it");
        Assert.Equal(new[] { true, false, true, false, true, false }, three.Select(block => block is PdfParagraph).ToArray());
        Assert.Equal(
            new[]
            {
                new PdfParagraph("Trattenuta dal portale (fattura del portale a tuo nome)", Bold: true),
                new PdfParagraph("Commissioni del portale riaddebitate dal gestore (fattura n. PM2026/7)", Bold: true),
                new PdfParagraph("Commissione del portale sostenuta dall'agenzia, non addebitata al proprietario", Bold: true),
            },
            three.OfType<PdfParagraph>().ToArray());
        Assert.Equal(new[] { ("TEST Casa Lugano", "Booking.com", Chf(-75m)), ("Totale", string.Empty, Chf(-75m)) }, ChannelFeeCells(three[1]));
        Assert.Equal(new[] { ("TEST Casa Como", "Booking.com", Chf(-30m)), ("Totale", string.Empty, Chf(-30m)) }, ChannelFeeCells(three[3]));
        Assert.Equal(new[] { ("TEST Casa Locarno", "Booking.com", Chf(42m)), ("Totale", string.Empty, Chf(42m)) }, ChannelFeeCells(three[5]));

        // The commission the agency bears is in no total: the statement with it has the totals of the statement without it.
        Assert.Equal(Statement(recharged, owner).Totals, Statement(withoutRecharge, recharged, owner).Totals);

        // As before: without the third kind the part has exactly the first two groups, and with it the same blocks come first: the new kind only follows them.
        var before = ChannelFeeBlocks(OwnerStatementLayout.Pdf(Statement(recharged, owner), texts), texts, "it");
        Assert.Equal(new[] { true, false, true, false }, before.Select(block => block is PdfParagraph).ToArray());
        Assert.Equal(before.OfType<PdfParagraph>().ToArray(), three.Take(4).OfType<PdfParagraph>().ToArray());
        Assert.Equal(TextOf(before), TextOf(three.Take(4)));
        Assert.DoesNotContain("non addebitata al proprietario", TextOf(before), StringComparison.Ordinal);
    }

    /// <summary>The day a statement says it was published is the day in Rome, as the other documents write their days: 23:30 UTC is already the next day there, winter and summer time.</summary>
    [Fact]
    public async Task StatementFiles_PublishedDayIsTheDayInRome_Test()
    {
        var world = await World.CreateAsync();
        var texts = new ResxDocumentTexts();
        var content = await world.Documents.GetContentAsync(world.OrgId, world.VersionId, "it");

        var winter = content with { PublishedAt = new DateTime(2026, 11, 10, 23, 30, 0, DateTimeKind.Utc) };
        Assert.Contains("Pubblicato il 11/11/2026", TextOf(OwnerStatementLayout.Pdf(winter, texts).Blocks), StringComparison.Ordinal);
        Assert.Contains("Pubblicato il 11/11/2026", Encoding.UTF8.GetString(OwnerStatementLayout.Csv(winter, texts)), StringComparison.Ordinal);

        var summer = content with { PublishedAt = new DateTime(2026, 7, 10, 22, 30, 0, DateTimeKind.Utc) };
        Assert.Contains("Pubblicato il 11/07/2026", TextOf(OwnerStatementLayout.Pdf(summer, texts).Blocks), StringComparison.Ordinal);
        Assert.Contains("Pubblicato il 11/07/2026", Encoding.UTF8.GetString(OwnerStatementLayout.Csv(summer, texts)), StringComparison.Ordinal);
    }

    /// <summary>The invoice of the property manager is linked through the version, or through the owner and the month; a credit note, a draft or another owner's is not.</summary>
    [Fact]
    public async Task StatementPdf_PropertyManagerInvoiceNumberIsFoundWhenLinked_Test()
    {
        var world = await World.CreateAsync();
        Assert.Null((await world.Documents.GetContentAsync(world.OrgId, world.VersionId)).OwnerInvoiceNumber);

        world.Db.FiscalDocuments.Add(Invoice(world, "PM2026/1", new DateOnly(2026, 11, 5), FiscalDocumentKind.CreditNote, world.OwnerId));
        world.Db.FiscalDocuments.Add(Invoice(world, "PM2026/2", new DateOnly(2026, 11, 6), FiscalDocumentKind.Invoice, Guid.NewGuid()));
        world.Db.FiscalDocuments.Add(Invoice(world, "PM2026/3", new DateOnly(2026, 11, 7), FiscalDocumentKind.Invoice, world.OwnerId));
        await world.Db.SaveChangesAsync();

        var content = await world.Documents.GetContentAsync(world.OrgId, world.VersionId);
        Assert.Equal(("PM2026/3", (DateOnly?)new DateOnly(2026, 11, 7)), (content.OwnerInvoiceNumber, content.OwnerInvoiceDate));
    }

    /// <summary>Every label the layout reads exists in both languages, every type of ledger line has a part, and the English file has English words.</summary>
    [Fact]
    public async Task StatementLayout_EveryTextExistsInBothLanguages_AndEveryLedgerTypeHasAPart_Test()
    {
        var texts = new ResxDocumentTexts();
        foreach (var key in OwnerStatementLayout.TextKeys)
        {
            Assert.NotEqual(key, texts.Get(key, "it"));
            Assert.NotEqual(key, texts.Get(key, "en"));
            Assert.NotEqual(texts.Get(key, "it"), texts.Get(key, "en"));
        }

        foreach (var type in Enum.GetValues<OwnerLedgerEntryType>())
            Assert.Contains(OwnerStatementLayout.SectionOf(type), OwnerStatementLayout.Sections);
        Assert.Throws<InvalidOperationException>(() => OwnerStatementLayout.SectionOf((OwnerLedgerEntryType)999));

        var world = await World.CreateAsync();
        var english = TextOf(OwnerStatementLayout.Pdf(await world.Documents.GetContentAsync(world.OrgId, world.VersionId, "en"), texts).Blocks);
        Assert.Contains("Stays", english, StringComparison.Ordinal);
        Assert.Contains("Closing balance", english, StringComparison.Ordinal);
        Assert.DoesNotContain("Soggiorni", english, StringComparison.Ordinal);
    }

    /// <summary>Privacy: a statement names no guest: its lines are by property, days, channel and booking code, and its text has no name, e-mail or phone of a guest.</summary>
    [Fact]
    public async Task StatementContent_NamesNoGuest_Test()
    {
        var world = await World.CreateAsync();
        var guest = new Guest { OrgId = world.OrgId, FirstName = "Ospite", LastName = "Riservato", Email = "ospite.riservato@example.com", PhoneNumber = "+41790000000" };
        world.Db.Guests.Add(guest);
        var booking = await world.Db.Bookings.SingleAsync(b => b.Id == world.PortalBookingId);
        booking.GuestId = guest.Id;
        await world.Db.SaveChangesAsync();

        var content = await world.Documents.GetContentAsync(world.OrgId, world.VersionId);
        var everything = TextOf(OwnerStatementLayout.Pdf(content, new ResxDocumentTexts()).Blocks)
            + Encoding.UTF8.GetString(OwnerStatementLayout.Csv(content, new ResxDocumentTexts()))
            + System.Text.Json.JsonSerializer.Serialize(content);
        Assert.DoesNotContain("Ospite", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Riservato", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("ospite.riservato@example.com", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("+41790000000", everything, StringComparison.Ordinal);
    }

    private static async Task<byte[]> ReadAsync(InMemoryFileStorage storage, string key)
    {
        await using var stream = (await storage.OpenReadAsync(StorageBucket.Private, key))!;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private static async Task<StoredStatementPdf> StoreAgainAsync(World world, OwnerStatementVersion version)
    {
        var entries = await world.Db.OwnerLedgerEntries.AsNoTracking().Where(e => e.PeriodMonth == 10).ToListAsync();
        return await world.Documents.StorePublishedPdfAsync(version, entries, null);
    }

    private static FiscalDocument Invoice(World world, string number, DateOnly issued, FiscalDocumentKind kind, Guid recipient) => new()
    {
        OrgId = world.OrgId,
        Purpose = FiscalDocumentPurpose.OwnerInvoice,
        RecipientOwnerId = recipient,
        PeriodYear = 2026,
        PeriodMonth = 10,
        Kind = kind,
        Status = FiscalDocumentStatus.Issued,
        Series = "PM2026",
        Number = number,
        IssueDate = issued,
        Currency = "CHF",
    };

    private static OwnerAccountingController Controller(Guid orgId, HostScope scope, IOwnerAccountingService? accounting = null)
    {
        var org = new Mock<IOrgContextResolver>();
        org.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        var scopes = new Mock<IHostScopeResolver>();
        scopes.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), orgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        var identity = new ClaimsIdentity([new Claim("sub", "auth0|caller"), new Claim(ClaimTypes.NameIdentifier, "auth0|caller")], "test");
        return new OwnerAccountingController(accounting ?? Mock.Of<IOwnerAccountingService>(), org.Object, scopes.Object, Mock.Of<IAuthorizationService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } },
        };
    }

    /// <summary>The rows of the table of the part «Netto» that closes the statement (the one with the opening and the closing balance).</summary>
    private static IReadOnlyList<IReadOnlyList<string>> NetTable(PdfDocumentContent content, IDocumentTexts texts) =>
        content.Blocks.OfType<PdfTable>().Single(t => t.Rows.Any(r => r[0] == texts.Get("StatementFileNetClosing", "it"))).Rows;

    /// <summary>
    /// The statement of an organization whose Booking.com accounts are the owner's (V1) but one: the listing «TEST Casa Locarno», whose account is the agency's, with the recharge of the commissions
    /// turned off (the override of a listing and portal, read through the real resolver). Each listing has the commission its portal kept: 75.00 on «TEST Casa Lugano» (the 8th of October) and 42.00
    /// on «TEST Casa Locarno» (the 2nd), so that the order of the days is not the order of the kinds; the agency bears the second, which the posting writes as a line for information only
    /// (FIX/BE-V2-NO-RECHARGE).
    /// </summary>
    private static async Task<(World World, OwnerStatementContent Content)> StatementOfAnOwnerListingAndAnAgencyListingAsync()
    {
        // A version that is not published yet (approved): the append-only guard refuses any change of a published one, and the totals this version stores follow the second commission.
        var world = await World.CreateAsync(approved: true, readsPortalAccounts: true);
        world.Db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile { OrgId = world.OrgId, PortalAccountHolder = PortalAccountHolder.Owner, RechargePortalCommissions = false });
        await world.Db.SaveChangesAsync();

        var agencyListing = await world.AddListingWithPortalFeeAsync("TEST Casa Locarno", -42m, 2, OwnerLedgerEntryType.ChannelFeeBorneByAgency);
        world.Db.PropertyChannelAccountHolders.Add(
            new PropertyChannelAccountHolder { OrgId = world.OrgId, PropertyId = agencyListing, Channel = nameof(BookingSource.BookingCom), Holder = PortalAccountHolder.Agency });
        await world.Db.SaveChangesAsync();

        return (world, await world.Documents.GetContentAsync(world.OrgId, world.VersionId, "it"));
    }

    /// <summary>The blocks of the part «Commissioni dei canali» of a statement: what follows its heading, up to the heading of the next part.</summary>
    private static List<PdfBlock> ChannelFeeBlocks(PdfDocumentContent content, IDocumentTexts texts, string language)
    {
        var blocks = content.Blocks.ToList();
        var heading = texts.Get("StatementFileChannelFeesHeading", language);
        var start = blocks.FindIndex(block => block is PdfHeading found && found.Text == heading);
        Assert.True(start >= 0, "the statement has the part of the commissions of the channels");
        return blocks.Skip(start + 1).TakeWhile(block => block is not PdfHeading).ToList();
    }

    /// <summary>The cells of a table of the commissions that do not depend on the days of the stay: the property, the channel and the amount (the row of the total has no channel).</summary>
    private static (string Property, string Channel, string Amount)[] ChannelFeeCells(PdfBlock table) =>
        [.. Assert.IsType<PdfTable>(table).Rows.Select(row => (row[0], row[2], row[3]))];

    /// <summary>An amount as the statement writes it in francs.</summary>
    private static string Chf(decimal amount) => OwnerStatementLayout.Money(amount, "CHF");

    /// <summary>All the text of some blocks, one line each.</summary>
    private static string TextOf(IEnumerable<PdfBlock> blocks) => string.Join(
        '\n',
        blocks.SelectMany(block => block switch
        {
            PdfHeading heading => [heading.Text],
            PdfParagraph paragraph => [paragraph.Text],
            PdfTable table => table.Columns.Select(c => c.Header).Concat(table.Rows.SelectMany(r => r)),
            _ => Array.Empty<string>(),
        }));

    private sealed class CapturingEmailQueue : IEmailQueue
    {
        public List<(string? To, EmailContent Content, string Template)> Sent { get; } = [];

        /// <summary>What the queue answers: <c>true</c> it queued the e-mail, <c>false</c> it refused it.</summary>
        public bool Accepts { get; set; } = true;

        /// <summary>A failure of the queue itself (it should never throw: that would be a bug of it).</summary>
        public Exception? Fails { get; set; }

        /// <summary>Looked at when an e-mail is queued: what the database says at that moment.</summary>
        public Action? WhenQueued { get; set; }

        public bool Enqueue(string? to, EmailContent content, string template)
        {
            if (Fails is not null)
                throw Fails;

            WhenQueued?.Invoke();
            Sent.Add((to, content, template));
            return Accepts;
        }
    }

    private sealed class World
    {
        public static readonly DateTime Cutoff = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);

        private World(AppDbContext db, Casazen.Core.Options.SwissTouristTaxOptions swiss, bool readsPortalAccounts)
        {
            Db = db;
            Clock = new FakeTimeProvider(new DateTimeOffset(Cutoff.AddDays(1)));
            Storage = new InMemoryFileStorage();
            Queue = new CapturingEmailQueue();

            // The channels that collect the tourist tax are read as the guest documents read them (the line builder over the same database): the shipped default of a Swiss
            // issuer that confirmed none is the one the test gives (none unless a test says one).
            Documents = new OwnerStatementDocuments(
                db,
                new MigraDocPdfDocumentRenderer(),
                new ResxDocumentTexts(),
                Storage,
                new OwnerFileContentScanner(),
                new TestFeatureFlags(FeatureFlags.AgencyTier),
                Queue,
                new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://app.example.com" })),
                new InternalActivityService(db, NullLogger<InternalActivityService>.Instance, Clock),
                Clock,
                NullLogger<OwnerStatementDocuments>.Instance,

                // A statement built without the resolver reads every listing as V1: a test of the account holders asks for the real one, over the same database.
                holders: readsPortalAccounts ? new PortalAccountHolderResolver(db) : null,
                documentLines: new GuestDocumentLineBuilder(db, new ResxDocumentTexts(), Options.Create(new Casazen.Core.Options.FiscalDocumentRulesOptions()), Options.Create(swiss)));
        }

        public AppDbContext Db { get; }

        public FakeTimeProvider Clock { get; }

        public InMemoryFileStorage Storage { get; }

        public CapturingEmailQueue Queue { get; }

        public OwnerStatementDocuments Documents { get; }

        public Guid OrgId { get; } = Guid.NewGuid();

        public Guid OtherOrgId { get; } = Guid.NewGuid();

        public Guid OwnerId { get; } = Guid.NewGuid();

        public Guid PropertyId { get; } = Guid.NewGuid();

        public Guid PortalBookingId { get; } = Guid.NewGuid();

        public Guid DirectBookingId { get; } = Guid.NewGuid();

        public Guid VersionId { get; private set; }

        public OwnerStatementVersion Version => Db.OwnerStatementVersions.AsNoTracking().Single(v => v.Id == VersionId);

        public OwnerAccountingService Accounting() =>
            new(Db, Mock.Of<IOwnerFiscalProfileService>(), Clock, NullLogger<OwnerAccountingService>.Instance, null, Documents);

        /// <summary>The documents the agency shares with its owners, on the same database and bucket (what adds a version to a document).</summary>
        public OwnerCollaborationService Collaboration()
        {
            var links = new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://app.example.com" }));
            var environment = Mock.Of<IHostEnvironment>(e => e.EnvironmentName == "Development");
            var contacts = new OwnerContactIndex(Options.Create(new Casazen.Core.Options.OwnerPortalOptions()), environment, NullLogger<OwnerContactIndex>.Instance);
            var activities = new InternalActivityService(Db, NullLogger<InternalActivityService>.Instance, Clock);
            return new OwnerCollaborationService(
                Db,
                Storage,
                new OwnerFileContentScanner(),
                new OwnerPortalAccessResolver(Db, Clock),
                new ActivityLog(Db, Clock),
                activities,
                Queue,
                links,
                Mock.Of<IAuth0ManagementService>(),
                contacts,
                Clock,
                NullLogger<OwnerCollaborationService>.Instance);
        }

        /// <summary>
        /// The world of the statement of October. With <paramref name="borneByAgency"/> the commission of the Booking.com stay is posted as the agency's (FIX/BE-V2-NO-RECHARGE: an account of
        /// the agency that the organization does not recharge), a line for information only that the totals of the version leave out.
        /// </summary>
        public static async Task<World> CreateAsync(
            bool approved = false,
            Casazen.Core.Options.SwissTouristTaxOptions? swiss = null,
            bool convertedStay = false,
            bool readsPortalAccounts = false,
            bool borneByAgency = false)
        {
            var world = new World(
                new AppDbContext(
                    new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OwnerStatementDocuments_{Guid.NewGuid()}").Options,
                    new StatementTenant(),
                    new EphemeralDataProtectionProvider()),
                swiss ?? new Casazen.Core.Options.SwissTouristTaxOptions(),
                readsPortalAccounts);
            await world.SeedAsync(approved, convertedStay, borneByAgency);
            return world;
        }

        /// <summary>Changes the stored version (a version whose totals no longer follow its lines).</summary>
        public static async Task Tamper(AppDbContext db, Guid versionId, Action<OwnerStatementVersion> change)
        {
            var version = await db.OwnerStatementVersions.SingleAsync(v => v.Id == versionId);
            change(version);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }

        /// <summary>A person of the owner portal with an ownership in force of the owner: who the e-mail of a publication goes to.</summary>
        public async Task AddPortalOwnerAsync(string email)
        {
            var user = new OwnerUser { Id = Guid.NewGuid(), Auth0Sub = $"auth0|{Guid.NewGuid():N}", Email = email, FirstName = "TEST", LastName = "Proprietario" };
            Db.OwnerUsers.Add(user);
            Db.PropertyOwnerships.Add(new PropertyOwnership
            {
                OrgId = OrgId,
                PropertyId = PropertyId,
                PropertyOwnerId = OwnerId,
                OwnerUserId = user.Id,
                SharePercent = 100m,
                OwnershipStart = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task SetTouristTaxAsync(Guid bookingId, decimal tax)
        {
            await ChangeBookingAsync(bookingId, booking => booking.TouristTax = tax);
        }

        public async Task ChangeBookingAsync(Guid bookingId, Action<Booking> change)
        {
            var booking = await Db.Bookings.SingleAsync(b => b.Id == bookingId);
            change(booking);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        /// <summary>
        /// A second listing of the owner with a Booking.com stay and the commission its portal kept on it (<paramref name="fee"/>, negative), posted on the <paramref name="day"/> of October as a
        /// line of <paramref name="type"/> (a channel fee the owner bears, or one the agency bears): the statement has then a second commission of a channel, of another listing. The totals the
        /// version stores follow the new line, as the ones of a version cut from these lines do (a line the agency bears is in none of them): for a version that is not published yet
        /// (<c>approved: true</c>), because the append-only guard refuses any change of a published one.
        /// </summary>
        public async Task<Guid> AddListingWithPortalFeeAsync(string name, decimal fee, int day, OwnerLedgerEntryType type = OwnerLedgerEntryType.ChannelFee)
        {
            var propertyId = Guid.NewGuid();
            var bookingId = Guid.NewGuid();
            var checkOut = new DateTime(2026, 10, day, 10, 0, 0, DateTimeKind.Utc);
            Db.Properties.Add(new Property { Id = propertyId, OrgId = OrgId, OwnerId = "auth0|agency", Name = name, Address = "Via 2", City = "Locarno", Currency = "CHF" });
            Db.Bookings.Add(new Booking
            {
                Id = bookingId,
                OrgId = OrgId,
                PropertyId = propertyId,
                CheckInDate = checkOut.AddDays(-3).AddHours(4),
                CheckOutDate = checkOut,
                Source = BookingSource.BookingCom,
                TouristTax = 0m,
                TotalPrice = 400m,
                Status = BookingStatus.Confirmed,
            });
            var entry = Entry(type, OwnerLedgerSourceType.Booking, bookingId, bookingId.ToString("N"), fee, new DateOnly(2026, 10, day), 11);
            entry.PropertyId = propertyId;
            Db.OwnerLedgerEntries.Add(entry);
            await Db.SaveChangesAsync();

            var counts = OwnerLedgerBalance.Counts(type);
            await Tamper(
                Db,
                VersionId,
                version =>
                {
                    version.ChannelFees += counts ? fee : 0m;
                    version.ClosingBalance += counts ? fee : 0m;
                    version.EntryCount += 1;
                });
            return propertyId;
        }

        private async Task SeedAsync(bool approved, bool convertedStay, bool borneByAgency)
        {
            Db.Orgs.Add(new OrgEntity { Id = OrgId, Name = "TEST Agenzia Lugano", DisplayName = "TEST Agenzia Lugano", Slug = "test-agenzia", PlanTier = PlanTier.Agency });
            Db.Orgs.Add(new OrgEntity { Id = OtherOrgId, Name = "TEST Altra Agenzia", DisplayName = "TEST Altra Agenzia", Slug = "test-altra", PlanTier = PlanTier.Agency });
            Db.PropertyOwners.Add(new PropertyOwner { Id = OwnerId, OrgId = OrgId, FirstName = "TEST", LastName = "Proprietario", Email = "proprietario@example.com" });
            Db.Properties.Add(new Property { Id = PropertyId, OrgId = OrgId, OwnerId = "auth0|agency", Name = "TEST Casa Lugano", Address = "Via 1", City = "Lugano", Currency = "CHF" });
            Db.Bookings.Add(new Booking
            {
                Id = PortalBookingId,
                OrgId = OrgId,
                PropertyId = PropertyId,
                CheckInDate = new DateTime(2026, 10, 5, 14, 0, 0, DateTimeKind.Utc),
                CheckOutDate = new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc),
                Source = BookingSource.BookingCom,
                TouristTax = 12m,
                TotalPrice = 612m,
                Status = BookingStatus.Confirmed,
            });
            Db.Bookings.Add(new Booking
            {
                Id = DirectBookingId,
                OrgId = OrgId,
                PropertyId = PropertyId,
                CheckInDate = new DateTime(2026, 10, 12, 14, 0, 0, DateTimeKind.Utc),
                CheckOutDate = new DateTime(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc),
                Source = BookingSource.Direct,
                TouristTax = 8m,
                TotalPrice = 308m,
                Status = BookingStatus.Confirmed,
            });

            var payment = Guid.NewGuid();
            var october = new List<OwnerLedgerEntry>
            {
                Entry(OwnerLedgerEntryType.BookingRevenue, OwnerLedgerSourceType.Booking, PortalBookingId, PortalBookingId.ToString("N"), 500m, new DateOnly(2026, 10, 8), 1),
                Entry(
                    borneByAgency ? OwnerLedgerEntryType.ChannelFeeBorneByAgency : OwnerLedgerEntryType.ChannelFee,
                    OwnerLedgerSourceType.Booking,
                    PortalBookingId,
                    PortalBookingId.ToString("N"),
                    -75m,
                    new DateOnly(2026, 10, 8),
                    2),
                Entry(OwnerLedgerEntryType.AgencyCommission, OwnerLedgerSourceType.Booking, PortalBookingId, PortalBookingId.ToString("N"), -50m, new DateOnly(2026, 10, 8), 3),
                Entry(OwnerLedgerEntryType.OwnerCharge, OwnerLedgerSourceType.MandateFlatFee, PortalBookingId, OwnerLedgerFlatFees.Cleaning, -40m, new DateOnly(2026, 10, 8), 4),
                Entry(OwnerLedgerEntryType.BookingRevenue, OwnerLedgerSourceType.Payment, payment, DirectBookingId.ToString("N"), 300m, new DateOnly(2026, 10, 12), 5),
                Entry(OwnerLedgerEntryType.AgencyCommission, OwnerLedgerSourceType.Payment, payment, DirectBookingId.ToString("N"), -30m, new DateOnly(2026, 10, 12), 6),
                Entry(OwnerLedgerEntryType.OwnerExpense, OwnerLedgerSourceType.OwnerCharge, Guid.NewGuid(), "2026-10", -20m, new DateOnly(2026, 10, 20), 7, "Riparazione rubinetto"),
                Entry(OwnerLedgerEntryType.Correction, OwnerLedgerSourceType.Manual, null, null, 5m, new DateOnly(2026, 10, 25), 8, "Rettifica test"),
                Entry(OwnerLedgerEntryType.Withholding, OwnerLedgerSourceType.OwnerPayout, Guid.NewGuid(), null, -10m, new DateOnly(2026, 11, 5), 9, null, new DateOnly(2026, 11, 5)),
                Entry(OwnerLedgerEntryType.OwnerPayment, OwnerLedgerSourceType.WireBatch, Guid.NewGuid(), null, -400m, new DateOnly(2026, 11, 5), 10, null, new DateOnly(2026, 11, 5)),
            };
            var september = Entry(OwnerLedgerEntryType.BookingRevenue, OwnerLedgerSourceType.Booking, Guid.NewGuid(), null, 100m, new DateOnly(2026, 9, 20), 0);
            if (convertedStay)
            {
                var converted = october[0];
                converted.OriginalCurrency = "EUR";
                converted.SourceAmount = 535m;
                converted.OriginalAmount = 535m;
                converted.FxRate = 0.93457944m;
                converted.FxSource = "TEST BCE";
                converted.FxDate = new DateOnly(2026, 10, 5);
            }
            september.PeriodMonth = 9;
            Db.OwnerLedgerEntries.AddRange(october);
            Db.OwnerLedgerEntries.Add(september);

            decimal Sum(params OwnerLedgerEntryType[] types) => october.Where(e => types.Contains(e.EntryType)).Sum(e => e.Amount);
            var version = new OwnerStatementVersion
            {
                OrgId = OrgId,
                PropertyOwnerId = OwnerId,
                PeriodYear = 2026,
                PeriodMonth = 10,
                VersionNumber = 1,
                Status = approved ? OwnerStatementVersionStatus.Approved : OwnerStatementVersionStatus.Published,
                Currency = "CHF",
                CutoffAt = Cutoff,
                OpeningBalance = 100m,
                Revenue = Sum(OwnerLedgerEntryType.BookingRevenue),
                ChannelFees = Sum(OwnerLedgerEntryType.ChannelFee),
                Commission = Sum(OwnerLedgerEntryType.AgencyCommission),
                Charges = Sum(OwnerLedgerEntryType.OwnerCharge, OwnerLedgerEntryType.OwnerExpense),
                Payments = Sum(OwnerLedgerEntryType.OwnerPayment, OwnerLedgerEntryType.AgencyPayment),
                Corrections = Sum(OwnerLedgerEntryType.Correction),
                ClosingBalance = 100m + october.Where(e => OwnerLedgerBalance.Counts(e.EntryType)).Sum(e => e.Amount),
                EntryCount = october.Count,
                EntriesSha256 = OwnerAccountingService.Hash(october),
                CreatedAt = Cutoff,
                ApprovedAt = approved ? Cutoff : null,
                PublishedAt = approved ? null : Cutoff.AddHours(1),
            };
            VersionId = version.Id;
            Db.OwnerStatementVersions.Add(version);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        private OwnerLedgerEntry Entry(
            OwnerLedgerEntryType type,
            OwnerLedgerSourceType source,
            Guid? sourceId,
            string? reference,
            decimal amount,
            DateOnly recognition,
            int minute,
            string? description = null,
            DateOnly? paid = null) => new()
            {
                OrgId = OrgId,
                PropertyOwnerId = OwnerId,
                PropertyId = PropertyId,
                EntryType = type,
                SourceType = source,
                SourceId = sourceId,
                SourceReference = reference,
                IdempotencyKey = $"test:{Guid.NewGuid():N}",
                ServiceDate = recognition.AddDays(-3),
                RecognitionDate = recognition,
                PaymentDate = paid,
                PeriodYear = 2026,
                PeriodMonth = 10,
                SharePercent = 100m,
                OriginalCurrency = "CHF",
                SourceAmount = amount,
                OriginalAmount = amount,
                StatementCurrency = "CHF",
                FxRate = 1m,
                Amount = amount,
                Description = description,
                PostedAt = Cutoff.AddDays(-5).AddMinutes(minute),
            };
    }
}

file sealed class StatementTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
