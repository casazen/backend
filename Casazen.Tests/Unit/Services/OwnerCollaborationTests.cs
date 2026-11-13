using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Services;
using Hangfire;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E07/F07/US01/T02 (NF-111): the documents, the delegated contacts and the inbox of the owner portal. The three proof tests of the brief are the permission
/// rule of the contacts (AC-NF111-4), the data rule of the documents between owners and agencies (ADR-011-R2, AC-NF111-3) and the privacy rule of the
/// notifications of the inbox (AC-NF111-5). EF InMemory, the storage in memory, the real scan of the files, the e-mails captured.
/// </summary>
public partial class OwnerCollaborationTests
{
    // 9 October 2026, 10:00 UTC: the 9th in Rome.
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Today = new(2026, 10, 9);
    private const string Agent = "auth0|agenzia";

    // One provider for the whole class: EF keeps one model per provider, and a provider per test would fill the cache the other tests share.
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    /// <summary>
    /// PROOF TEST: permission rule (AC-NF111-4). A contact holds exactly the permissions the owner gave, each on its own: with <c>calendar.read</c> only it
    /// reads the calendar and nothing else (no stay request, no document, no inbox, no contact management); given <c>documents.read</c> it reads the
    /// documents from its next request, and nothing more; withdrawn, it reaches nothing from its next request on, and every change is in the activity log.
    /// </summary>
    [Fact]
    public async Task OwnerContact_EachPermissionIsIndependentAndRevocationImmediate_Test()
    {
        var w = await World.CreateAsync();
        var owner = await w.AccessAsync(w.UserA);
        var contactId = await w.SeedActiveContactAsync(w.OwnerA, w.UserC, OwnerPortalPermission.CalendarRead);
        var service = w.Service();
        var stays = w.Stays();

        var onlyCalendar = await w.AccessAsync(w.UserC);
        Assert.NotNull(await stays.GetCalendarAsync(onlyCalendar, w.PropertyA, Today, Today.AddDays(7)));
        Assert.Equal(OwnerPortalErrors.PermissionMissing, (await Assert.ThrowsAsync<DomainForbiddenException>(
            () => stays.RequestAsync(onlyCalendar, new OwnerStayRequestCommand(w.PropertyA, Today.AddDays(20), Today.AddDays(22))))).Code);
        Assert.Equal(OwnerPortalErrors.PermissionMissing, (await Assert.ThrowsAsync<DomainForbiddenException>(() => service.ListDocumentsForOwnerAsync(onlyCalendar))).Code);
        Assert.Equal(OwnerPortalErrors.PermissionMissing, (await Assert.ThrowsAsync<DomainForbiddenException>(() => service.ListConversationsForOwnerAsync(onlyCalendar))).Code);
        Assert.Equal(OwnerCollaborationErrors.ContactOwnerOnly, (await Assert.ThrowsAsync<DomainForbiddenException>(() => service.ListContactsForOwnerAsync(onlyCalendar))).Code);

        // The owner adds documents.read: the documents, from the next request; the inbox and the stays still not.
        var updated = await service.SetContactPermissionsAsync(owner, contactId, [OwnerPortalPermissions.CalendarRead, OwnerPortalPermissions.DocumentsRead]);
        Assert.Equal([OwnerPortalPermissions.CalendarRead, OwnerPortalPermissions.DocumentsRead], updated.Permissions);
        var withDocuments = await w.AccessAsync(w.UserC);
        Assert.Empty((await service.ListDocumentsForOwnerAsync(withDocuments)).Documents);
        await Assert.ThrowsAsync<DomainForbiddenException>(() => service.ListConversationsForOwnerAsync(withDocuments));

        // An empty list of permissions is never «all».
        Assert.Equal(OwnerPortalErrors.PermissionsEmpty, (await Assert.ThrowsAsync<DomainRuleException>(() => service.SetContactPermissionsAsync(owner, contactId, []))).Code);

        // Withdrawn: nothing from the next request on, and the row stays.
        await service.RevokeContactByOwnerAsync(owner, contactId);
        var revoked = await w.AccessAsync(w.UserC);
        Assert.Empty(revoked.Grants);
        await Assert.ThrowsAsync<DomainForbiddenException>(() => stays.GetCalendarAsync(revoked, w.PropertyA, null, null));
        Assert.Equal("revoked", (await service.ListContactsForAgencyAsync(w.OrgId, w.OwnerA)).Single().Status);

        var lines = await w.Db.OrgActivityEntries.AsNoTracking().Where(e => e.SubjectId == contactId.ToString()).Select(e => e.DetailsJson).ToListAsync();
        Assert.Contains(lines, line => line.Contains("\"status\":\"permissionsChanged\"", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("\"status\":\"revoked\"", StringComparison.Ordinal));
    }

    /// <summary>
    /// The invitation of a contact is accepted only by an account whose e-mail is verified and is the invited one; the account becomes an identity of its own
    /// (never the owner's), a repeat answers the same, and another account cannot use the link.
    /// </summary>
    [Fact]
    public async Task OwnerContact_TheInvitationIsAcceptedOnlyByTheVerifiedInvitedAddress_WithItsOwnAccount()
    {
        var w = await World.CreateAsync();
        var service = w.Service();
        var invited = await service.InviteContactAsync(
            await w.AccessAsync(w.UserA), new InviteOwnerContact(null, "Carla@Example.com", "Carla", "Contabile", [OwnerPortalPermissions.AccountingRead]));
        Assert.Equal(("invited", "carla@example.com"), (invited.Status, invited.Email));
        var token = w.LastToken();

        AcceptOwnerContact As(string sub, string email, bool verified) => new(token, sub, email, verified, "Carla", "Contabile");
        Assert.Equal(OwnerCollaborationErrors.EmailNotVerified, (await Assert.ThrowsAsync<DomainForbiddenException>(() => service.AcceptContactAsync(As("auth0|carla", "carla@example.com", false)))).Code);
        Assert.Equal(OwnerCollaborationErrors.EmailMismatch, (await Assert.ThrowsAsync<DomainForbiddenException>(() => service.AcceptContactAsync(As("auth0|carla", "altra@example.com", true)))).Code);

        var accepted = await service.AcceptContactAsync(As("auth0|carla", "carla@example.com", true));
        Assert.False(accepted.AlreadyAccepted);
        Assert.NotEqual(w.UserA, accepted.OwnerUserId);
        Assert.Equal([w.PropertyA], accepted.PropertyIds);
        Assert.True((await service.AcceptContactAsync(As("auth0|carla", "carla@example.com", true))).AlreadyAccepted);
        Assert.Equal(OwnerCollaborationErrors.InvitationUsed, (await Assert.ThrowsAsync<DomainGoneException>(() => service.AcceptContactAsync(As("auth0|altro", "carla@example.com", true)))).Code);

        // Accounting only: the statements, not the calendar.
        var access = await w.AccessAsync(accepted.OwnerUserId);
        Assert.All(access.Grants, g => Assert.Equal(OwnerPortalPermission.AccountingRead, g.Permissions));
    }

    /// <summary>
    /// PROOF TEST: data rule (ADR-011-R2, AC-NF111-3). A document of another owner, or of another agency, does not exist for the caller: not in its list,
    /// and 404 when asked; another agency neither lists, downloads nor withdraws it. A download is written before the file is handed out; a withdrawal hides
    /// the document at the next request; a new file is a new version and the first stays as it was.
    /// </summary>
    [Fact]
    public async Task OwnerDocument_OtherAgencyOrOwnerReturnsNotFound_Test()
    {
        var w = await World.CreateAsync();
        var service = w.Service();
        var ofA = await service.ShareDocumentAsync(
            w.OrgId, new ShareOwnerDocument(w.OwnerA, w.PropertyA, OwnerDocumentType.Insurance, "Polizza casa", null, Today.AddDays(200)), Pdf("A"), Agent);
        var ofB = await service.ShareDocumentAsync(
            w.OrgId, new ShareOwnerDocument(w.OwnerB, null, OwnerDocumentType.Mandate, "Mandato", null, null), Pdf("B"), Agent);
        var ownerA = await w.AccessAsync(w.UserA);

        Assert.Equal([ofA.Document.Id], (await service.ListDocumentsForOwnerAsync(ownerA)).Documents.Select(d => d.Id));
        Assert.Equal(OwnerCollaborationErrors.DocumentNotFound, (await Assert.ThrowsAsync<NotFoundException>(() => service.OpenDocumentForOwnerAsync(ownerA, ofB.Document.Id, null))).Code);
        Assert.Empty(await service.ListDocumentsForAgencyAsync(w.OtherOrgId, null));
        await Assert.ThrowsAsync<NotFoundException>(() => service.SetDocumentVisibilityAsync(w.OtherOrgId, ofA.Document.Id, false, "auth0|altra-agenzia"));
        await Assert.ThrowsAsync<NotFoundException>(() => service.OpenDocumentForAgencyAsync(w.OtherOrgId, ofA.Document.Id, ofA.Document.Versions[0].Id, "auth0|altra-agenzia"));
        await Assert.ThrowsAsync<NotFoundException>(() => service.ShareDocumentAsync(
            w.OtherOrgId, new ShareOwnerDocument(w.OwnerA, null, OwnerDocumentType.Other, "Altro", null, null), Pdf("X"), "auth0|altra-agenzia"));

        // The download is written before the file is handed out, with who.
        var file = await service.OpenDocumentForOwnerAsync(ownerA, ofA.Document.Id, null);
        await using (file.Content)
            Assert.Equal(("application/pdf", "polizza.pdf"), (file.ContentType, file.FileName));
        var downloads = await service.ListDocumentDownloadsAsync(w.OrgId, ofA.Document.Id);
        Assert.Equal(("Anna Alfa", false), (downloads.Single().By, downloads.Single().ByAgency));

        // A new file is a new version, the first one is kept; a withdrawal hides the document at the next request.
        var versioned = await service.AddDocumentVersionAsync(w.OrgId, ofA.Document.Id, new OwnerDocumentVersionInput(null, Today.AddDays(400)), Pdf("A2"), Agent);
        Assert.Equal([2, 1], versioned.Document.Versions.Select(v => v.VersionNumber));
        Assert.Equal(ofA.Document.Versions[0].Sha256, versioned.Document.Versions[1].Sha256);
        await service.SetDocumentVisibilityAsync(w.OrgId, ofA.Document.Id, false, Agent);
        Assert.Empty((await service.ListDocumentsForOwnerAsync(await w.AccessAsync(w.UserA))).Documents);
        await Assert.ThrowsAsync<NotFoundException>(async () => await service.OpenDocumentForOwnerAsync(await w.AccessAsync(w.UserA), ofA.Document.Id, null));
    }

    /// <summary>
    /// PROOF TEST: privacy rule (AC-NF111-5). The e-mail that tells the owner (or the agency) about a new message names the agency and links to CasaZen, and
    /// carries nothing of the message: no amount, no name, address or phone of a guest. Another owner cannot read the conversation, and an owner who opted
    /// out gets no e-mail.
    /// </summary>
    [Fact]
    public async Task OwnerInbox_NotificationContainsNoFinancialOrGuestData_Test()
    {
        var w = await World.CreateAsync();
        var service = w.Service();
        const string Words = "Incasso di agosto 1.234,56 EUR; l'ospite Mario Rossi (mario.rossi@example.com, +39 333 1234567) ha lasciato le chiavi.";

        var conversation = await service.StartConversationByAgencyAsync(
            w.OrgId, new StartOwnerConversation(w.OwnerA, null, OwnerConversationTopic.Property, w.PropertyA, null, null, null, "Agosto", Words), Agent);

        var toOwner = Assert.Single(w.Emails, e => e.To == "anna@example.com");
        Assert.Contains($"/portale-proprietari/messaggi/{conversation.Id:D}", toOwner.Content.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Agenzia Mare", toOwner.Content.Subject, StringComparison.Ordinal);
        AssertCarriesNothingOf(Words, toOwner.Content);

        // The owner answers: the agency is told the same way, at its contact address.
        w.Emails.Clear();
        await service.PostMessageByOwnerAsync(await w.AccessAsync(w.UserA), conversation.Id, Words, null);
        var toAgency = Assert.Single(w.Emails);
        Assert.Equal(w.AgencyEmail, toAgency.To);
        Assert.Contains($"/app/owners/messages/{conversation.Id:D}", toAgency.Content.HtmlBody, StringComparison.Ordinal);
        AssertCarriesNothingOf(Words, toAgency.Content);

        // Another owner does not reach it; an owner who opted out is not written to.
        await Assert.ThrowsAsync<NotFoundException>(async () => await service.ListMessagesForOwnerAsync(await w.AccessAsync(w.UserB), conversation.Id));
        await service.SetInboxPreferencesAsync(w.UserA, emailNotifications: false);
        w.Emails.Clear();
        await service.PostMessageByAgencyAsync(w.OrgId, conversation.Id, "Ancora una cosa", null, Agent);
        Assert.Empty(w.Emails);
        Assert.Equal(3, (await service.ListMessagesForOwnerAsync(await w.AccessAsync(w.UserA), conversation.Id)).Count);
    }

    [Fact]
    public async Task OwnerDocument_ExpiryAlert_IsRaisedOncePerVersion_AndClosedByANewVersion()
    {
        var w = await World.CreateAsync();
        var service = w.Service();
        var shared = await service.ShareDocumentAsync(
            w.OrgId, new ShareOwnerDocument(w.OwnerA, w.PropertyA, OwnerDocumentType.Insurance, "Polizza", null, Today.AddDays(10)), Pdf("A"), Agent);
        Assert.Equal("expiring", shared.Document.ExpiryState);

        Assert.Equal(new OwnerDocumentAlertResult(1, 0), await service.RaiseExpiryAlertsAsync());
        Assert.Equal(new OwnerDocumentAlertResult(0, 0), await service.RaiseExpiryAlertsAsync());

        await service.AddDocumentVersionAsync(w.OrgId, shared.Document.Id, new OwnerDocumentVersionInput(null, Today.AddDays(365)), Pdf("A2"), Agent);
        Assert.Equal(new OwnerDocumentAlertResult(0, 1), await service.RaiseExpiryAlertsAsync());
    }

    /// <summary>
    /// PROOF TEST: privacy rule. The words of the inbox (the message, the subject of the conversation, the name of the file of a message) and the address and the name of a
    /// delegated contact are Data Protection payloads at rest (<c>EncryptedColumns</c>): the table holds none of the words, the service reads them back in clear, and a
    /// contact is found, and kept unique for its owner profile, by the keyed hash of the address whatever the spelling (never by the address).
    /// </summary>
    [Fact]
    public async Task OwnerInbox_WordsAndContacts_AreEncryptedAtRest_AndTheContactIsFoundByItsKeyedHash_Test()
    {
        const string Words = "Il mio IBAN e IT60X0542811101000000123456; chiamami al +39 333 1234567";
        const string Subject = "Dati bancari della signora Verdi";
        var w = await World.CreateAsync(encrypted: true);
        var service = w.Service();
        var owner = await w.AccessAsync(w.UserA);

        await service.InviteContactAsync(owner, new InviteOwnerContact(null, "Carla@Example.com", "Carla", "Contabile", [OwnerPortalPermissions.InboxUse]));
        var conversation = await service.StartConversationByOwnerAsync(
            owner, new StartOwnerConversation(null, null, OwnerConversationTopic.General, null, null, null, null, Subject, Words));
        await service.PostMessageByOwnerAsync(owner, conversation.Id, "Allego la visura", new OwnerFileUpload("Visura Verdi Maria.pdf", Pdf("V").Content));

        // What the tables hold: payloads, none of the words.
        await using (var raw = w.RawDb())
        {
            var contact = await raw.OwnerPortalContacts.AsNoTracking().SingleAsync();
            Assert.All(
                new[] { contact.Email, contact.FirstName, contact.LastName },
                stored => Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, stored, StringComparison.Ordinal));
            Assert.DoesNotContain("carla", contact.Email + contact.FirstName + contact.LastName, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("contabile", contact.Email + contact.FirstName + contact.LastName, StringComparison.OrdinalIgnoreCase);
            Assert.Matches("^[0-9a-f]{64}$", contact.EmailHash);
            Assert.Equal(World.ContactIndex.HashEmail("carla@example.com"), contact.EmailHash);

            var subject = (await raw.OwnerConversations.AsNoTracking().SingleAsync()).Subject;
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, subject, StringComparison.Ordinal);
            Assert.DoesNotContain("Verdi", subject, StringComparison.Ordinal);

            var messages = await raw.OwnerMessages.AsNoTracking().ToListAsync();
            Assert.Equal(2, messages.Count);
            Assert.All(messages, m => Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, m.Body, StringComparison.Ordinal));
            Assert.DoesNotContain(messages, m => m.Body.Contains("IBAN", StringComparison.Ordinal) || m.Body.Contains("Allego", StringComparison.Ordinal));
            var fileName = Assert.Single(messages, m => m.AttachmentKey is not null).AttachmentFileName!;
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, fileName, StringComparison.Ordinal);
            Assert.DoesNotContain("Visura", fileName, StringComparison.Ordinal);
        }

        // What the service reads: the words, in clear.
        Assert.Equal("carla@example.com", (await service.ListContactsForOwnerAsync(owner)).Single().Email);
        var read = await service.ListMessagesForOwnerAsync(owner, conversation.Id);
        Assert.Contains(read, m => m.Body == Words);
        Assert.Contains(read, m => m.Attachment is { } file && file.FileName.StartsWith("Visura Verdi Maria", StringComparison.Ordinal));
        Assert.Equal(Subject, (await service.ListConversationsForOwnerAsync(owner)).Single().Subject);

        // The address is found by its keyed hash: another spelling of it is the same contact, an invitation already in force.
        var again = await Assert.ThrowsAsync<DomainConflictException>(
            () => service.InviteContactAsync(owner, new InviteOwnerContact(null, "  CARLA@example.com ", "Carla", "Altra", [OwnerPortalPermissions.InboxUse])));
        Assert.Equal(OwnerCollaborationErrors.ContactExists, again.Code);
    }

    /// <summary>
    /// Every invitation sends an e-mail to an address the owner chose: the ones of a day are capped, the withdrawn ones counted too (inviting, withdrawing and
    /// inviting again never reaches the limit of the contacts in force), and nothing is sent once the cap is reached.
    /// </summary>
    [Fact]
    public async Task OwnerContact_TheInvitationsOfADayAreCapped_TheWithdrawnOnesCounted()
    {
        var w = await World.CreateAsync();
        var service = w.Service();
        var owner = await w.AccessAsync(w.UserA);

        for (var i = 0; i < OwnerCollaborationRules.MaxContactInvitationsPerDay; i++)
        {
            var invited = await service.InviteContactAsync(owner, new InviteOwnerContact(null, $"persona{i}@example.com", "Persona", $"Numero{i}", [OwnerPortalPermissions.InboxUse]));
            await service.RevokeContactByOwnerAsync(owner, invited.Id);
        }

        Assert.DoesNotContain(await service.ListContactsForOwnerAsync(owner), c => c.Status != "revoked");
        Assert.Equal(OwnerCollaborationRules.MaxContactInvitationsPerDay, w.Emails.Count);

        var over = await Assert.ThrowsAsync<DomainTooManyRequestsException>(
            () => service.InviteContactAsync(owner, new InviteOwnerContact(null, "una.di.troppo@example.com", "Una", "Di troppo", [OwnerPortalPermissions.InboxUse])));
        Assert.Equal(OwnerCollaborationErrors.ContactDailyLimitReached, over.Code);
        Assert.Equal(OwnerCollaborationRules.MaxContactInvitationsPerDay, w.Emails.Count);
        Assert.Equal(OwnerCollaborationRules.MaxContactInvitationsPerDay, (await service.ListContactsForOwnerAsync(owner)).Count);
    }

    /// <summary>
    /// The name a download carries is the one sent without its extension and its dots, with the extension of the type the scan recognised: a document named
    /// «fattura.pdf.exe» and an attachment named «a.pdf.bat» are offered as «fattura_pdf.pdf» and «a_pdf.pdf», by the service and again at the download.
    /// </summary>
    [Fact]
    public async Task OwnerFile_TheNameOfADocumentAndOfAnAttachment_CarriesOnlyTheExtensionOfItsType()
    {
        var w = await World.CreateAsync();
        var service = w.Service();
        var owner = await w.AccessAsync(w.UserA);

        var shared = await service.ShareDocumentAsync(
            w.OrgId, new ShareOwnerDocument(w.OwnerA, null, OwnerDocumentType.Other, "Fattura", null, null), new OwnerFileUpload("fattura.pdf.exe", Pdf("F").Content), Agent);
        Assert.Equal("fattura_pdf.pdf", shared.Document.Versions.Single().FileName);
        var file = await service.OpenDocumentForOwnerAsync(owner, shared.Document.Id, null);
        await using (file.Content)
            Assert.Equal(("application/pdf", "fattura_pdf.pdf"), (file.ContentType, file.FileName));

        var conversation = await service.StartConversationByOwnerAsync(
            owner, new StartOwnerConversation(null, null, OwnerConversationTopic.General, null, null, null, null, "Allegato", "ecco il file"));
        var message = await service.PostMessageByOwnerAsync(owner, conversation.Id, "il file", new OwnerFileUpload("a.pdf.bat", Pdf("B").Content));
        Assert.Equal("a_pdf.pdf", message.Attachment!.FileName);
    }

    [Theory]
    [InlineData("%PDF-1.4\n1 0 obj << /Type /Catalog /OpenAction << /S /JavaScript /JS (app.alert(1)) >> >> endobj\n%%EOF")]
    [InlineData("%PDF-1.4\n1 0 obj << /Type /Catalog /OpenAction << /S /J#61vaScript >> >> endobj\n%%EOF")]
    [InlineData("%PDF-1.4\n1 0 obj << /Type /Action /S /Launch /F (cmd.exe) >> endobj\n%%EOF")]
    [InlineData("%PDF-1.4\n1 0 obj << /Names << /EmbeddedFiles 2 0 R >> >> endobj\n%%EOF")]
    public void OwnerFileScanner_APdfWithActiveContent_IsRefused(string pdf)
    {
        var verdict = new OwnerFileContentScanner().Scan(Encoding.Latin1.GetBytes(pdf));

        Assert.Equal((false, OwnerFileRules.ActiveContent), (verdict.Clean, verdict.RejectionCode));
    }

    [Fact]
    public void OwnerFileScanner_ReadsTheCompressedStreams_AndAcceptsOnlyPdfJpegAndPngByTheirContent()
    {
        var scanner = new OwnerFileContentScanner();

        // A script hidden in a compressed object stream is found.
        var hidden = Encoding.Latin1.GetBytes("%PDF-1.5\n1 0 obj << /Type /ObjStm /Filter /FlateDecode >>\nstream\n")
            .Concat(Deflate("2 0 obj << /S /JavaScript /JS (x) >> endobj"))
            .Concat(Encoding.Latin1.GetBytes("\nendstream\nendobj\n%%EOF"))
            .ToArray();
        Assert.Equal(OwnerFileRules.ActiveContent, scanner.Scan(hidden).RejectionCode);

        Assert.Equal((true, OwnerFileRules.Pdf, ".pdf"), Verdict(scanner.Scan(Pdf("ok").Content)));
        Assert.Equal((true, OwnerFileRules.Png, ".png"), Verdict(scanner.Scan([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0])));
        Assert.Equal((true, OwnerFileRules.Jpeg, ".jpg"), Verdict(scanner.Scan([0xFF, 0xD8, 0xFF, 0xE0, 0, 0])));
        Assert.Equal(OwnerFileRules.TypeNotAllowed, scanner.Scan(Encoding.ASCII.GetBytes("MZ\x90\0 this program cannot be run in DOS mode")).RejectionCode);
        Assert.Equal(OwnerFileRules.TypeNotAllowed, scanner.Scan(Encoding.ASCII.GetBytes("<html><script>alert(1)</script></html>")).RejectionCode);
        Assert.Equal(OwnerFileRules.Missing, scanner.Scan([]).RejectionCode);

        static (bool, string?, string?) Verdict(OwnerFileVerdict v) => (v.Clean, v.ContentType, v.Extension);
    }

    private static void AssertCarriesNothingOf(string words, EmailContent content)
    {
        var text = content.Subject + content.HtmlBody;
        foreach (var secret in new[] { "1.234,56", "Mario", "Rossi", "mario.rossi@example.com", "+39 333 1234567", "chiavi", "Incasso" })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Mario", words, StringComparison.Ordinal);
    }

    private static OwnerFileUpload Pdf(string marker) =>
        new("polizza.pdf", Encoding.Latin1.GetBytes($"%PDF-1.4\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n% {marker}\n%%EOF"));

    private static byte[] Deflate(string text)
    {
        using var target = new MemoryStream();
        using (var zlib = new ZLibStream(target, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(Encoding.Latin1.GetBytes(text));
        return target.ToArray();
    }

    [GeneratedRegex(@"token=([A-Za-z0-9_\-]+)")]
    private static partial Regex TokenInLink();

    /// <summary>
    /// One agency with two owners (each with a property and a portal account), a person a contact can be, and another agency. The storage is a dictionary,
    /// the e-mails are captured.
    /// </summary>
    private sealed class World
    {
        private readonly string _name = $"owner-collaboration-{Guid.NewGuid():N}";
        private readonly FixedTimeProvider _clock = new(Now);
        private readonly Dictionary<string, byte[]> _files = [];

        private readonly IDataProtectionProvider? _provider;

        private World(bool encrypted)
        {
            _provider = encrypted ? Provider : null;
            Db = NewDb();
        }

        public AppDbContext Db { get; }

        public List<(string To, EmailContent Content)> Emails { get; } = [];

        public string AgencyEmail { get; private set; } = string.Empty;

        public Guid OrgId { get; private set; }

        public Guid OtherOrgId { get; private set; }

        public Guid PropertyA { get; private set; }

        public Guid OwnerA { get; private set; }

        public Guid OwnerB { get; private set; }

        public Guid UserA { get; private set; }

        public Guid UserB { get; private set; }

        public Guid UserC { get; private set; }

        /// <param name="encrypted">The model encrypts the columns of <c>EncryptedColumns</c> (Data Protection), as in production; the default stores them as written.</param>
        public static async Task<World> CreateAsync(bool encrypted = false)
        {
            var w = new World(encrypted);
            await w.SeedAsync();
            return w;
        }

        /// <summary>A context of the same database with no Data Protection: it reads the columns as the table holds them.</summary>
        public AppDbContext RawDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_name).Options);

        public async Task<OwnerPortalAccess> AccessAsync(Guid ownerUserId) => await new OwnerPortalAccessResolver(NewDb(), _clock).ResolveAsync(ownerUserId);

        public string LastToken() => TokenInLink().Match(Emails[^1].Content.HtmlBody).Groups[1].Value;

        public OwnerCollaborationService Service()
        {
            var storage = new Mock<IFileStorage>();
            storage
                .Setup(s => s.PutAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Callback<StorageBucket, string, Stream, string, CancellationToken>((_, key, content, _, _) =>
                {
                    using var copy = new MemoryStream();
                    content.CopyTo(copy);
                    _files[key] = copy.ToArray();
                })
                .Returns(Task.CompletedTask);
            storage
                .Setup(s => s.OpenReadAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((StorageBucket _, string key, CancellationToken _) => _files.TryGetValue(key, out var bytes) ? new MemoryStream(bytes) : null);
            var emails = new Mock<IEmailQueue>();
            emails
                .Setup(e => e.Enqueue(It.IsAny<string?>(), It.IsAny<EmailContent>(), It.IsAny<string>()))
                .Callback<string?, EmailContent, string>((to, content, _) => Emails.Add((to ?? string.Empty, content)))
                .Returns(true);
            var auth0 = new Mock<IAuth0ManagementService>();
            auth0
                .Setup(a => a.SetOwnerPortalClaimsAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new Auth0SyncResult(Auth0SyncStatus.Synced));

            return new OwnerCollaborationService(
                Db,
                storage.Object,
                new OwnerFileContentScanner(),
                new OwnerPortalAccessResolver(Db, _clock),
                new ActivityLog(Db, _clock),
                new InternalActivityService(Db, NullLogger<InternalActivityService>.Instance, _clock),
                emails.Object,
                new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = "https://casazen.example.test/" })),
                auth0.Object,
                ContactIndex,
                _clock,
                NullLogger<OwnerCollaborationService>.Instance);
        }

        /// <summary>The keyed index of the contacts, with the fixed key of Development (what the tests run with).</summary>
        public static OwnerContactIndex ContactIndex { get; } = new(
            Options.Create(new OwnerPortalOptions()),
            Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == "Development"),
            NullLogger<OwnerContactIndex>.Instance);

        public OwnerStayService Stays() =>
            new(
                Db,
                Mock.Of<ICalendarBlockService>(),
                Mock.Of<ICalendarChangeRunner>(),
                Mock.Of<IInternalActivityService>(),
                new ActivityLog(Db, _clock),
                _clock,
                NullLogger<OwnerStayService>.Instance,
                Mock.Of<IBackgroundJobClient>());

        public async Task<Guid> SeedActiveContactAsync(Guid propertyOwnerId, Guid contactUserId, OwnerPortalPermission permissions)
        {
            await using var db = NewDb();
            var contact = new OwnerPortalContact
            {
                OrgId = OrgId,
                PropertyOwnerId = propertyOwnerId,
                Email = "carla@example.com",
                EmailHash = ContactIndex.HashEmail("carla@example.com"),
                FirstName = "Carla",
                LastName = "Contatto",
                Permissions = permissions,
                Status = OwnerPortalContactStatus.Active,
                TokenHash = new string('a', 64),
                InvitationExpiresAt = Now.UtcDateTime.AddDays(7),
                InvitedByOwnerUserId = UserA,
                OwnerUserId = contactUserId,
                AcceptedAt = Now.UtcDateTime,
                CreatedAt = Now.UtcDateTime,
                UpdatedAt = Now.UtcDateTime,
            };
            db.OwnerPortalContacts.Add(contact);
            await db.SaveChangesAsync();
            return contact.Id;
        }

        private AppDbContext NewDb() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_name).Options, tenantContext: null, _provider);

        private async Task SeedAsync()
        {
            await using var db = NewDb();
            var org = HostScopeScenario.NewOrg("Agenzia Mare", $"mare-{Guid.NewGuid():N}"[..20]);
            var otherOrg = HostScopeScenario.NewOrg("Agenzia Monti", $"monti-{Guid.NewGuid():N}"[..20]);
            db.Orgs.AddRange(org, otherOrg);
            var propertyA = HostScopeScenario.NewProperty(org.Id, Agent, "Casa Alfa");
            var propertyB = HostScopeScenario.NewProperty(org.Id, Agent, "Casa Beta");
            db.Properties.AddRange(propertyA, propertyB);

            var userA = new OwnerUser { Auth0Sub = "auth0|anna", Email = "anna@example.com", FirstName = "Anna", LastName = "Alfa", CreatedAt = Now.UtcDateTime };
            var userB = new OwnerUser { Auth0Sub = "auth0|bruno", Email = "bruno@example.com", FirstName = "Bruno", LastName = "Beta", CreatedAt = Now.UtcDateTime };
            var userC = new OwnerUser { Auth0Sub = "auth0|carla", Email = "carla@example.com", FirstName = "Carla", LastName = "Contatto", CreatedAt = Now.UtcDateTime };
            db.OwnerUsers.AddRange(userA, userB, userC);
            var owners = new List<PropertyOwner>();
            foreach (var (user, property) in new[] { (userA, propertyA), (userB, propertyB) })
            {
                var owner = new PropertyOwner { OrgId = org.Id, FirstName = user.FirstName, LastName = user.LastName, Email = user.Email, CreatedAt = Now.UtcDateTime };
                owners.Add(owner);
                db.PropertyOwners.Add(owner);
                db.PropertyOwnerships.Add(new PropertyOwnership
                {
                    OrgId = org.Id,
                    PropertyId = property.Id,
                    PropertyOwnerId = owner.Id,
                    OwnerUserId = user.Id,
                    SharePercent = 100m,
                    OwnershipStart = Now.UtcDateTime.AddYears(-1),
                });
            }

            await db.SaveChangesAsync();
            (OrgId, OtherOrgId, PropertyA, OwnerA, OwnerB, UserA, UserB, UserC, AgencyEmail) =
                (org.Id, otherOrg.Id, propertyA.Id, owners[0].Id, owners[1].Id, userA.Id, userB.Id, userC.Id, org.ContactEmail);
        }
    }
}
