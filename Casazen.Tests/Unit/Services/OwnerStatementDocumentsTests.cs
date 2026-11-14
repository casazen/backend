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
using Casazen.Web.Infrastructure;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

        // A stay whose channel collects the tax itself is not collected by the agency: the confirmed list of the issuer, or the collector declared for the stay.
        world.Db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile { OrgId = world.OrgId, PlatformCollectedTouristTaxChannelsJson = "[\"BookingCom\"]" });
        world.Db.StayTouristTaxes.Add(new StayTouristTax { OrgId = world.OrgId, BookingId = world.DirectBookingId, Collector = TouristTaxCollector.Channel });
        await world.Db.SaveChangesAsync();
        Assert.Empty((await world.Documents.GetContentAsync(world.OrgId, world.VersionId, "it")).TouristTax);
        Assert.Equal(content.Totals, (await world.Documents.GetContentAsync(world.OrgId, world.VersionId, "it")).Totals);
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

    private static OwnerAccountingController Controller(Guid orgId, HostScope scope)
    {
        var org = new Mock<IOrgContextResolver>();
        org.Setup(r => r.GetOrProvisionOrgIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(orgId);
        var scopes = new Mock<IHostScopeResolver>();
        scopes.Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<IReadOnlySet<string>>(), orgId, It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        var identity = new ClaimsIdentity([new Claim("sub", "auth0|caller"), new Claim(ClaimTypes.NameIdentifier, "auth0|caller")], "test");
        return new OwnerAccountingController(Mock.Of<IOwnerAccountingService>(), org.Object, scopes.Object, Mock.Of<IAuthorizationService>())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) } },
        };
    }

    /// <summary>The rows of the table of the part «Netto» that closes the statement (the one with the opening and the closing balance).</summary>
    private static IReadOnlyList<IReadOnlyList<string>> NetTable(PdfDocumentContent content, IDocumentTexts texts) =>
        content.Blocks.OfType<PdfTable>().Single(t => t.Rows.Any(r => r[0] == texts.Get("StatementFileNetClosing", "it"))).Rows;

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

        public bool Enqueue(string? to, EmailContent content, string template)
        {
            Sent.Add((to, content, template));
            return true;
        }
    }

    private sealed class World
    {
        public static readonly DateTime Cutoff = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);

        private World(AppDbContext db)
        {
            Db = db;
            Clock = new FakeTimeProvider(new DateTimeOffset(Cutoff.AddDays(1)));
            Storage = new InMemoryFileStorage();
            Queue = new CapturingEmailQueue();
            Documents = new OwnerStatementDocuments(
                db,
                new MigraDocPdfDocumentRenderer(),
                new ResxDocumentTexts(),
                Storage,
                new OwnerFileContentScanner(),
                new TestFeatureFlags(FeatureFlags.AgencyTier),
                Queue,
                new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://app.example.com" })),
                Clock,
                NullLogger<OwnerStatementDocuments>.Instance);
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

        public static async Task<World> CreateAsync(bool approved = false)
        {
            var world = new World(new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OwnerStatementDocuments_{Guid.NewGuid()}").Options,
                new StatementTenant(),
                new EphemeralDataProtectionProvider()));
            await world.SeedAsync(approved);
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

        public async Task SetTouristTaxAsync(Guid bookingId, decimal tax)
        {
            var booking = await Db.Bookings.SingleAsync(b => b.Id == bookingId);
            booking.TouristTax = tax;
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        private async Task SeedAsync(bool approved)
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
                Entry(OwnerLedgerEntryType.ChannelFee, OwnerLedgerSourceType.Booking, PortalBookingId, PortalBookingId.ToString("N"), -75m, new DateOnly(2026, 10, 8), 2),
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
                ClosingBalance = 100m + october.Sum(e => e.Amount),
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
