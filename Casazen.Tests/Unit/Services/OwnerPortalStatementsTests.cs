using System.Text;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Casazen.Web.DTOs.OwnerPortal;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E13/F01/US02/T02 (NF-111, NF-115): the statements and the invoices of the property manager in the owner portal, with their amounts, lines and PDF; only the owner of
/// the claim, <c>accounting.read</c> for a delegated contact, nothing of a guest, nothing with <c>Features:AgencyTier</c> off, and the e-mail that tells the owner a
/// statement was published (no amount in it).
/// </summary>
public class OwnerPortalStatementsTests
{
    /// <summary>
    /// Privacy: the owner sees the published statements of its own owner profile and nothing else (not another owner's, not a draft, not a guest), the ones a later
    /// version replaced marked as such; the statement of another owner and its PDF are the same 404 as one that does not exist, and so are the invoices.
    /// </summary>
    [Fact]
    public async Task OwnerPortal_StatementsOnlyOfTheClaimOwner_Test()
    {
        var world = await World.CreateAsync();

        var mine = await world.Portal.GetOwnerStatementsAsync(world.OwnerUserA);
        Assert.Equal([world.SecondVersionId, world.FirstVersionId], mine.Select(s => s.StatementId));
        var latest = mine[0];
        Assert.Equal((2026, 10, 2, "CHF", false, (Guid?)null), (latest.Year, latest.Month, latest.Version, latest.Currency, latest.Superseded, latest.SupersededByStatementId));
        Assert.Equal("TEST Agenzia", latest.AgencyName);
        var first = mine[1];
        Assert.Equal((true, (Guid?)world.SecondVersionId), (first.Superseded, first.SupersededByStatementId));

        // Another owner's statement, a draft, and a statement that does not exist: one 404.
        foreach (var id in new[] { world.OtherOwnerVersionId, world.DraftVersionId, Guid.NewGuid() })
        {
            var detail = await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.GetOwnerStatementAsync(world.OwnerUserA, id));
            var pdf = await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.OpenOwnerStatementPdfAsync(world.OwnerUserA, id));
            Assert.Equal((OwnerAccountingErrors.VersionNotFound, OwnerAccountingErrors.VersionNotFound), (detail.Code, pdf.Code));
        }

        // A person with no ownership in force and no delegation has nothing to see: an empty list, not an error.
        Assert.Empty(await world.Portal.GetOwnerStatementsAsync(Guid.NewGuid()));
        Assert.Empty(await world.Portal.GetOwnerInvoicesAsync(Guid.NewGuid()));

        // The other owner sees its own and none of ours.
        Assert.Equal([world.OtherOwnerVersionId], (await world.Portal.GetOwnerStatementsAsync(world.OwnerUserB)).Select(s => s.StatementId));
        await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.GetOwnerStatementAsync(world.OwnerUserB, world.SecondVersionId));

        // A guest is nowhere: not in the list, not in the lines, not in the JSON the portal answers.
        var answer = JsonSerializer.Serialize(OwnerPortalStatementDetailDto.From(await world.Portal.GetOwnerStatementAsync(world.OwnerUserA, world.SecondVersionId)))
            + JsonSerializer.Serialize(mine.Select(OwnerPortalStatementDto.From));
        Assert.DoesNotContain("Ospite", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Riservato", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("ospite.riservato@example.com", answer, StringComparison.Ordinal);

        // The invoices: the issued ones made out to the owner, not a draft, not another owner's, not a document of a guest.
        var invoices = await world.Portal.GetOwnerInvoicesAsync(world.OwnerUserA);
        var invoice = Assert.Single(invoices);
        Assert.Equal((world.InvoiceId, "PM2026/1", new DateOnly(2026, 11, 12), 250m, "CHF", FiscalDocumentKind.Invoice), (invoice.InvoiceId, invoice.Number, invoice.IssueDate, invoice.Total, invoice.Currency, invoice.Kind));
        Assert.Equal(world.SecondVersionId, invoice.StatementId);
        Assert.Equal(world.OtherInvoiceId, Assert.Single(await world.Portal.GetOwnerInvoicesAsync(world.OwnerUserB)).InvoiceId);
        await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.OpenOwnerInvoicePdfAsync(world.OwnerUserA, world.OtherInvoiceId));
        await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.OpenOwnerInvoicePdfAsync(world.OwnerUserA, world.DraftInvoiceId));
        await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.OpenOwnerInvoicePdfAsync(world.OwnerUserA, world.GuestDocumentId));
        var file = await world.Portal.OpenOwnerInvoicePdfAsync(world.OwnerUserA, world.InvoiceId);
        await using var invoiceStream = file.Content;
        using var copy = new MemoryStream();
        await invoiceStream.CopyToAsync(copy);
        Assert.Equal("%PDF", Encoding.ASCII.GetString(copy.ToArray(), 0, 4));
        Assert.Equal("application/pdf", file.ContentType);
    }

    /// <summary>
    /// Permission: a delegated contact the owner did not give <c>accounting.read</c> is refused (403) on the statements, their lines and PDF, and the invoices and their
    /// PDF; with the permission it reads what the owner reads; a withdrawn contact gets nothing.
    /// </summary>
    [Fact]
    public async Task OwnerPortal_ContactWithoutAccountingRead403_Test()
    {
        var world = await World.CreateAsync();

        foreach (var ask in new Func<Task>[]
                 {
                     () => world.Portal.GetOwnerStatementsAsync(world.ContactWithoutAccounting),
                     () => world.Portal.GetOwnerStatementAsync(world.ContactWithoutAccounting, world.SecondVersionId),
                     () => world.Portal.OpenOwnerStatementPdfAsync(world.ContactWithoutAccounting, world.SecondVersionId),
                     () => world.Portal.GetOwnerInvoicesAsync(world.ContactWithoutAccounting),
                     () => world.Portal.OpenOwnerInvoicePdfAsync(world.ContactWithoutAccounting, world.InvoiceId),
                 })
        {
            var refusal = await Assert.ThrowsAsync<DomainForbiddenException>(ask);
            Assert.Equal(OwnerPortalErrors.PermissionMissing, refusal.Code);
        }

        // With the permission the contact reads the statements and the invoices of the owner that gave it.
        Assert.Equal([world.SecondVersionId, world.FirstVersionId], (await world.Portal.GetOwnerStatementsAsync(world.ContactWithAccounting)).Select(s => s.StatementId));
        Assert.Equal(world.SecondVersionId, (await world.Portal.GetOwnerStatementAsync(world.ContactWithAccounting, world.SecondVersionId)).Statement.StatementId);
        Assert.Equal(world.InvoiceId, Assert.Single(await world.Portal.GetOwnerInvoicesAsync(world.ContactWithAccounting)).InvoiceId);

        // The delegation does not reach another owner of the same agency, and a withdrawn contact has nothing.
        await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.GetOwnerStatementAsync(world.ContactWithAccounting, world.OtherOwnerVersionId));
        Assert.Empty(await world.Portal.GetOwnerStatementsAsync(world.RevokedContact));
        Assert.Empty(await world.Portal.GetOwnerInvoicesAsync(world.RevokedContact));
    }

    /// <summary>With <c>Features:AgencyTier</c> off nothing changes: the lists stay empty, whoever asks, and a statement or an invoice is not there.</summary>
    [Fact]
    public async Task OwnerPortal_FlagOffStaysEmpty_Test()
    {
        var world = await World.CreateAsync(agencyTier: false);

        Assert.Empty(await world.Portal.GetOwnerStatementsAsync(world.OwnerUserA));
        Assert.Empty(await world.Portal.GetOwnerInvoicesAsync(world.OwnerUserA));

        // Not a 403 for a contact without the permission either: the flag decides first, as before the accounting existed.
        Assert.Empty(await world.Portal.GetOwnerStatementsAsync(world.ContactWithoutAccounting));
        Assert.Empty(await world.Portal.GetOwnerInvoicesAsync(world.ContactWithoutAccounting));
        await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.GetOwnerStatementAsync(world.OwnerUserA, world.SecondVersionId));
        await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.OpenOwnerStatementPdfAsync(world.OwnerUserA, world.SecondVersionId));
        await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.OpenOwnerInvoicePdfAsync(world.OwnerUserA, world.InvoiceId));
    }

    /// <summary>
    /// The totals of the list are the ones the version stores (the withholding being what its closing balance leaves over), the detail has the lines grouped as in the
    /// PDF, and the PDF the owner downloads is the stored file; a statement with no stored file is a 404 of its own.
    /// </summary>
    [Fact]
    public async Task OwnerPortal_TotalsLinesAndPdfAreThoseOfTheVersion_Test()
    {
        var world = await World.CreateAsync();
        var summary = (await world.Portal.GetOwnerStatementsAsync(world.OwnerUserA)).Single(s => s.StatementId == world.SecondVersionId);

        Assert.Equal(OwnerStatementTotals.Of(world.SecondVersion), summary.Totals);
        Assert.Equal((100m, 0m, 0m, -10m, 0m, 0m, 90m, 0m, 0m, 90m), (summary.Totals.Gross, summary.Totals.ChannelFees, summary.Totals.Withholding, summary.Totals.Commission, summary.Totals.Recharges, summary.Totals.Corrections, summary.Totals.Period, summary.Totals.Payments, summary.Totals.OpeningBalance, summary.Totals.ClosingBalance));
        var dto = OwnerPortalStatementDto.From(summary);
        Assert.Equal((100m, -10m, 90m, 90m), (dto.Totals.Gross, dto.Totals.Commission, dto.Totals.Net, dto.Totals.ClosingBalance));
        Assert.True(summary.HasPdf);
        Assert.False((await world.Portal.GetOwnerStatementsAsync(world.OwnerUserA)).Single(s => s.StatementId == world.FirstVersionId).HasPdf);

        var detail = OwnerPortalStatementDetailDto.From(await world.Portal.GetOwnerStatementAsync(world.OwnerUserA, world.SecondVersionId));
        Assert.Equal(["stays", "channelFees", "withholding", "commission", "recharges", "corrections", "payments"], detail.Sections.Select(s => s.Section));
        var stays = detail.Sections.Single(s => s.Section == "stays");
        var line = Assert.Single(stays.Lines);
        Assert.Equal(("TEST Casa", "BookingCom", 100m), (line.Property, line.Channel, line.Amount));
        Assert.Equal(100m, stays.Total);
        Assert.Equal(-10m, detail.Sections.Single(s => s.Section == "commission").Total);

        // The PDF is exactly what was stored at the publication.
        var stored = await world.Db.OwnerDocumentVersions.AsNoTracking().SingleAsync(v => v.OwnerDocumentId == world.SecondVersionId);
        var bytes = await ReadAsync(world.Storage, stored.StorageKey);
        var download = await world.Portal.OpenOwnerStatementPdfAsync(world.OwnerUserA, world.SecondVersionId);
        await using (download.Content)
        {
            using var copy = new MemoryStream();
            await download.Content.CopyToAsync(copy);
            Assert.Equal(bytes, copy.ToArray());
            Assert.Equal("application/pdf", download.ContentType);
        }

        Assert.Equal(world.SecondVersionId, (await world.Db.OwnerDocumentDownloads.AsNoTracking().SingleAsync()).OwnerDocumentId);

        // A published version with no stored file (published before the files existed) has no PDF to hand out.
        var missing = await Assert.ThrowsAsync<NotFoundException>(() => world.Portal.OpenOwnerStatementPdfAsync(world.OwnerUserA, world.FirstVersionId));
        Assert.Equal(OwnerStatementFileErrors.PdfNotAvailable, missing.Code);
    }

    /// <summary>
    /// Data: for the owner a statement is replaced only when its replacement is published. The agency marks the published version as superseded as soon as it generates a
    /// correction; until that one is published, the statement the owner was given is still the valid one, not «replaced» by something the owner cannot see.
    /// </summary>
    [Fact]
    public async Task OwnerPortal_StatementIsSupersededOnlyOnceItsReplacementIsPublished_Test()
    {
        var world = await World.CreateAsync();

        // A correction of the month was generated: the published version is superseded in the ledger, by a version that is still a draft.
        await world.SupersedeAsync(world.SecondVersionId, world.DraftVersionId);
        var asked = (await world.Portal.GetOwnerStatementsAsync(world.OwnerUserA)).Single(s => s.StatementId == world.SecondVersionId);
        Assert.Equal((false, (Guid?)null), (asked.Superseded, asked.SupersededByStatementId));
        var detail = await world.Portal.GetOwnerStatementAsync(world.OwnerUserA, world.SecondVersionId);
        Assert.Equal((false, (Guid?)null), (detail.Statement.Superseded, detail.Statement.SupersededByStatementId));
        Assert.DoesNotContain(await world.Portal.GetOwnerStatementsAsync(world.OwnerUserA), s => s.StatementId == world.DraftVersionId);

        // The correction is published: now the first statement is superseded, by it.
        await world.PublishAsync(world.DraftVersionId);
        var replaced = (await world.Portal.GetOwnerStatementsAsync(world.OwnerUserA)).Single(s => s.StatementId == world.SecondVersionId);
        Assert.Equal((true, (Guid?)world.DraftVersionId), (replaced.Superseded, replaced.SupersededByStatementId));
    }

    /// <summary>
    /// The e-mail of the publication goes to the people of the portal who read the statements of the owner (the owner, a contact with <c>accounting.read</c>), says who
    /// published and links to the page «Contabilità»: no amount, no period, no line, in the language of the agency.
    /// </summary>
    [Fact]
    public async Task OwnerPortal_PublicationEmailHasNoAmountsAndGoesToWhoReadsTheStatements_Test()
    {
        var world = await World.CreateAsync();

        await world.Documents.TellOwnerPublishedAsync(world.SecondVersion);

        var recipients = world.Queue.Sent.Select(s => s.To).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["contatto.contabilita@example.com", "proprietario.a@example.com"], recipients);
        var (_, content, template) = world.Queue.Sent[0];
        Assert.Equal("owner-statement-published", template);
        Assert.Equal("Nuovo rendiconto da TEST Agenzia", content.Subject);
        Assert.Contains("https://app.example.com/portale-proprietari/contabilita", content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("TEST Agenzia", content.HtmlBody, StringComparison.Ordinal);
        foreach (var secret in new[] { "100,00", "100.00", "90,00", "90.00", "10,00", "10.00", "10/2026" })
            Assert.DoesNotContain(secret, content.HtmlBody + content.Subject, StringComparison.Ordinal);

        // An English agency has an English e-mail.
        var english = await World.CreateAsync(language: "en");
        await english.Documents.TellOwnerPublishedAsync(english.SecondVersion);
        Assert.All(english.Queue.Sent, sent => Assert.Equal("New statement from TEST Agenzia", sent.Content.Subject));

        // Nobody in the portal, nobody told.
        var empty = await World.CreateAsync(withOwners: false);
        await empty.Documents.TellOwnerPublishedAsync(empty.SecondVersion);
        Assert.Empty(empty.Queue.Sent);

        // With Features:AgencyTier off the portal shows nothing: nobody is told.
        var off = await World.CreateAsync(agencyTier: false);
        await off.Documents.TellOwnerPublishedAsync(off.SecondVersion);
        Assert.Empty(off.Queue.Sent);
    }

    private static async Task<byte[]> ReadAsync(InMemoryFileStorage storage, string key)
    {
        await using var stream = (await storage.OpenReadAsync(StorageBucket.Private, key))!;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }

    private sealed class CapturingQueue : IEmailQueue
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
        private static readonly DateTime Cutoff = new(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc);
        private readonly Guid _orgId = Guid.NewGuid();
        private readonly Guid _ownerA = Guid.NewGuid();
        private readonly Guid _ownerB = Guid.NewGuid();
        private readonly Guid _propertyA = Guid.NewGuid();

        private World(AppDbContext db, bool agencyTier)
        {
            Db = db;
            var clock = new FakeTimeProvider(new DateTimeOffset(Cutoff.AddDays(2)));
            Storage = new InMemoryFileStorage();
            Queue = new CapturingQueue();
            var flags = agencyTier ? new TestFeatureFlags(FeatureFlags.AgencyTier) : new TestFeatureFlags();
            Documents = new OwnerStatementDocuments(
                db,
                new MigraDocPdfDocumentRenderer(),
                new ResxDocumentTexts(),
                Storage,
                new OwnerFileContentScanner(),
                flags,
                Queue,
                new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://app.example.com" })),
                clock,
                NullLogger<OwnerStatementDocuments>.Instance);
            Portal = new OwnerPortalService(
                db,
                clock,
                null,
                flags,
                new OwnerPortalAccessResolver(db, clock),
                Documents);
        }

        public AppDbContext Db { get; }

        public InMemoryFileStorage Storage { get; }

        public CapturingQueue Queue { get; }

        public OwnerStatementDocuments Documents { get; }

        public OwnerPortalService Portal { get; }

        public Guid OwnerUserA { get; } = Guid.NewGuid();

        public Guid OwnerUserB { get; } = Guid.NewGuid();

        public Guid ContactWithoutAccounting { get; } = Guid.NewGuid();

        public Guid ContactWithAccounting { get; } = Guid.NewGuid();

        public Guid RevokedContact { get; } = Guid.NewGuid();

        public Guid FirstVersionId { get; } = Guid.NewGuid();

        public Guid SecondVersionId { get; } = Guid.NewGuid();

        public Guid DraftVersionId { get; } = Guid.NewGuid();

        public Guid OtherOwnerVersionId { get; } = Guid.NewGuid();

        public Guid InvoiceId { get; } = Guid.NewGuid();

        public Guid OtherInvoiceId { get; } = Guid.NewGuid();

        public Guid DraftInvoiceId { get; } = Guid.NewGuid();

        public Guid GuestDocumentId { get; } = Guid.NewGuid();

        public OwnerStatementVersion SecondVersion => Db.OwnerStatementVersions.AsNoTracking().Single(v => v.Id == SecondVersionId);

        public static async Task<World> CreateAsync(bool agencyTier = true, string language = "it", bool withOwners = true)
        {
            var world = new World(
                new AppDbContext(
                    new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"OwnerPortalStatements_{Guid.NewGuid()}").Options,
                    new PortalTenant(),
                    new EphemeralDataProtectionProvider()),
                agencyTier);
            await world.SeedAsync(language, withOwners);
            return world;
        }

        private async Task SeedAsync(string language, bool withOwners)
        {
            var propertyA = _propertyA;
            var propertyB = Guid.NewGuid();
            var booking = Guid.NewGuid();
            Db.Orgs.Add(new OrgEntity { Id = _orgId, Name = "TEST Agenzia", DisplayName = "TEST Agenzia", Slug = "test-agenzia", PlanTier = PlanTier.Agency, DefaultLanguage = language });
            Db.PropertyOwners.Add(new PropertyOwner { Id = _ownerA, OrgId = _orgId, FirstName = "TEST", LastName = "Proprietario A", Email = "proprietario.a@example.com" });
            Db.PropertyOwners.Add(new PropertyOwner { Id = _ownerB, OrgId = _orgId, FirstName = "TEST", LastName = "Proprietario B", Email = "proprietario.b@example.com" });
            Db.Properties.Add(new Property { Id = propertyA, OrgId = _orgId, OwnerId = "auth0|agency", Name = "TEST Casa", Address = "Via 1", City = "Lugano", Currency = "CHF" });
            Db.Properties.Add(new Property { Id = propertyB, OrgId = _orgId, OwnerId = "auth0|agency", Name = "TEST Villa", Address = "Via 2", City = "Lugano", Currency = "CHF" });

            if (withOwners)
            {
                Db.OwnerUsers.Add(new OwnerUser { Id = OwnerUserA, Auth0Sub = "auth0|a", Email = "proprietario.a@example.com", FirstName = "A", LastName = "A" });
                Db.OwnerUsers.Add(new OwnerUser { Id = OwnerUserB, Auth0Sub = "auth0|b", Email = "proprietario.b@example.com", FirstName = "B", LastName = "B" });
                Db.OwnerUsers.Add(new OwnerUser { Id = ContactWithoutAccounting, Auth0Sub = "auth0|c1", Email = "contatto.senza@example.com", FirstName = "C", LastName = "1" });
                Db.OwnerUsers.Add(new OwnerUser { Id = ContactWithAccounting, Auth0Sub = "auth0|c2", Email = "contatto.contabilita@example.com", FirstName = "C", LastName = "2" });
                Db.OwnerUsers.Add(new OwnerUser { Id = RevokedContact, Auth0Sub = "auth0|c3", Email = "contatto.revocato@example.com", FirstName = "C", LastName = "3" });
                Db.PropertyOwnerships.Add(Ownership(propertyA, _ownerA, OwnerUserA));
                Db.PropertyOwnerships.Add(Ownership(propertyB, _ownerB, OwnerUserB));
                Db.OwnerPortalContacts.Add(Contact(ContactWithoutAccounting, OwnerPortalPermission.DocumentsRead | OwnerPortalPermission.CalendarRead, OwnerPortalContactStatus.Active));
                Db.OwnerPortalContacts.Add(Contact(ContactWithAccounting, OwnerPortalPermission.AccountingRead, OwnerPortalContactStatus.Active));
                Db.OwnerPortalContacts.Add(Contact(RevokedContact, OwnerPortalPermission.AccountingRead, OwnerPortalContactStatus.Revoked));
            }

            // A stay with revenue and a commission, for the lines: the guest of the booking is a name that must never reach the portal.
            var guest = new Guest { OrgId = _orgId, FirstName = "Ospite", LastName = "Riservato", Email = "ospite.riservato@example.com", PhoneNumber = "+41790000000" };
            Db.Guests.Add(guest);
            Db.Bookings.Add(new Booking
            {
                Id = booking,
                OrgId = _orgId,
                PropertyId = propertyA,
                GuestId = guest.Id,
                CheckInDate = new DateTime(2026, 10, 5, 14, 0, 0, DateTimeKind.Utc),
                CheckOutDate = new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc),
                Source = BookingSource.BookingCom,
                TotalPrice = 100m,
                Status = BookingStatus.Confirmed,
            });
            var lines = new List<OwnerLedgerEntry>
            {
                Line(OwnerLedgerEntryType.BookingRevenue, booking, 100m, 1),
                Line(OwnerLedgerEntryType.AgencyCommission, booking, -10m, 2),
            };
            Db.OwnerLedgerEntries.AddRange(lines);

            Db.OwnerStatementVersions.Add(Version(FirstVersionId, _ownerA, 1, OwnerStatementVersionStatus.Superseded, lines, publishedAt: Cutoff.AddHours(-30), supersededBy: SecondVersionId));
            Db.OwnerStatementVersions.Add(Version(SecondVersionId, _ownerA, 2, OwnerStatementVersionStatus.Published, lines, publishedAt: Cutoff.AddHours(1)));
            Db.OwnerStatementVersions.Add(Version(DraftVersionId, _ownerA, 3, OwnerStatementVersionStatus.Approved, lines, publishedAt: null));
            Db.OwnerStatementVersions.Add(Version(OtherOwnerVersionId, _ownerB, 1, OwnerStatementVersionStatus.Published, [], publishedAt: Cutoff.AddHours(2)));

            Db.FiscalDocuments.Add(Invoice(InvoiceId, _ownerA, "PM2026/1", FiscalDocumentStatus.Issued, FiscalDocumentPurpose.OwnerInvoice, SecondVersionId));
            Db.FiscalDocuments.Add(Invoice(OtherInvoiceId, _ownerB, "PM2026/2", FiscalDocumentStatus.Issued, FiscalDocumentPurpose.OwnerInvoice, null));
            Db.FiscalDocuments.Add(Invoice(DraftInvoiceId, _ownerA, null, FiscalDocumentStatus.Draft, FiscalDocumentPurpose.OwnerInvoice, null));
            Db.FiscalDocuments.Add(Invoice(GuestDocumentId, _ownerA, "2026/9", FiscalDocumentStatus.Issued, FiscalDocumentPurpose.GuestStay, null));
            await Db.SaveChangesAsync();

            // The PDF of the second version is stored at its publication; the first was published before the files existed.
            await Documents.StorePublishedPdfAsync(SecondVersionForStore(), lines, "auth0|publisher");
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        private OwnerStatementVersion SecondVersionForStore() => Db.OwnerStatementVersions.AsNoTracking().Single(v => v.Id == SecondVersionId);

        /// <summary>The ledger marks a version as superseded by a later one (what generating a correction does to the previous version).</summary>
        public async Task SupersedeAsync(Guid versionId, Guid byVersionId)
        {
            var version = await Db.OwnerStatementVersions.SingleAsync(v => v.Id == versionId);
            version.Status = OwnerStatementVersionStatus.Superseded;
            version.SupersededByVersionId = byVersionId;
            version.SupersededAt = Cutoff.AddHours(3);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        public async Task PublishAsync(Guid versionId)
        {
            var version = await Db.OwnerStatementVersions.SingleAsync(v => v.Id == versionId);
            version.Status = OwnerStatementVersionStatus.Published;
            version.PublishedAt = Cutoff.AddHours(4);
            await Db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

        private PropertyOwnership Ownership(Guid property, Guid owner, Guid ownerUser) => new()
        {
            OrgId = _orgId,
            PropertyId = property,
            PropertyOwnerId = owner,
            OwnerUserId = ownerUser,
            SharePercent = 100m,
            OwnershipStart = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        private OwnerPortalContact Contact(Guid ownerUser, OwnerPortalPermission permissions, OwnerPortalContactStatus status) => new()
        {
            OrgId = _orgId,
            PropertyOwnerId = _ownerA,
            Email = $"{ownerUser:N}@example.com",
            FirstName = "TEST",
            LastName = "Contatto",
            EmailHash = new string('a', 64),
            TokenHash = new string('b', 64),
            Permissions = permissions,
            Status = status,
            OwnerUserId = ownerUser,
            InvitedByOwnerUserId = OwnerUserA,
            InvitationExpiresAt = Cutoff.AddDays(30),
            CreatedAt = Cutoff,
            UpdatedAt = Cutoff,
        };

        private OwnerLedgerEntry Line(OwnerLedgerEntryType type, Guid booking, decimal amount, int minute) => new()
        {
            OrgId = _orgId,
            PropertyOwnerId = _ownerA,
            PropertyId = _propertyA,
            EntryType = type,
            SourceType = OwnerLedgerSourceType.Booking,
            SourceId = booking,
            SourceReference = booking.ToString("N"),
            IdempotencyKey = $"test:{Guid.NewGuid():N}",
            ServiceDate = new DateOnly(2026, 10, 5),
            RecognitionDate = new DateOnly(2026, 10, 8),
            PeriodYear = 2026,
            PeriodMonth = 10,
            SharePercent = 100m,
            OriginalCurrency = "CHF",
            SourceAmount = amount,
            OriginalAmount = amount,
            StatementCurrency = "CHF",
            FxRate = 1m,
            Amount = amount,
            PostedAt = Cutoff.AddDays(-5).AddMinutes(minute),
        };

        private OwnerStatementVersion Version(
            Guid id,
            Guid owner,
            int number,
            OwnerStatementVersionStatus status,
            IReadOnlyList<OwnerLedgerEntry> lines,
            DateTime? publishedAt,
            Guid? supersededBy = null) => new()
            {
                Id = id,
                OrgId = _orgId,
                PropertyOwnerId = owner,
                PeriodYear = 2026,
                PeriodMonth = 10,
                VersionNumber = number,
                Status = status,
                Currency = "CHF",
                CutoffAt = Cutoff,
                Revenue = lines.Where(l => l.EntryType == OwnerLedgerEntryType.BookingRevenue).Sum(l => l.Amount),
                Commission = lines.Where(l => l.EntryType == OwnerLedgerEntryType.AgencyCommission).Sum(l => l.Amount),
                ClosingBalance = lines.Sum(l => l.Amount),
                EntryCount = lines.Count,
                EntriesSha256 = OwnerAccountingService.Hash(lines),
                CreatedAt = Cutoff,
                PublishedAt = publishedAt,
                SupersededByVersionId = supersededBy,
            };

        private FiscalDocument Invoice(Guid id, Guid owner, string? number, FiscalDocumentStatus status, FiscalDocumentPurpose purpose, Guid? statement) => new()
        {
            Id = id,
            OrgId = _orgId,
            Purpose = purpose,
            RecipientOwnerId = owner,
            OwnerStatementVersionId = statement,
            PeriodYear = 2026,
            PeriodMonth = 10,
            Kind = FiscalDocumentKind.Invoice,
            Status = status,
            Series = "PM2026",
            Number = number,
            IssueDate = status == FiscalDocumentStatus.Issued ? new DateOnly(2026, 11, 12) : null,
            Currency = "CHF",
            TaxableBase = 231.5m,
            VatAmount = 18.5m,
            Total = 250m,
            Lines =
            [
                new FiscalDocumentLine
                {
                    OrgId = _orgId,
                    DocumentId = id,
                    LineNumber = 1,
                    LineType = FiscalLineType.Commission,
                    Description = "Provvigione di gestione",
                    Quantity = 1m,
                    UnitPrice = 231.5m,
                    TotalPrice = 231.5m,
                    VatRatePercent = 8.1m,
                    VatAmount = 18.5m,
                },
            ],
        };
    }
}

file sealed class PortalTenant : ITenantContext
{
    public Guid? OrgId => null;

    public bool FilterEnabled => false;
}
