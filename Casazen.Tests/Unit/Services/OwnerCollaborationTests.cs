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
    /// A document of a property that was deleted has no property to raise the item on: it is skipped (and its open item closed) and never stops the run, the
    /// other documents get their alerts, and the run ends without an error.
    /// </summary>
    [Fact]
    public async Task OwnerDocument_ExpiryAlert_ADocumentOfADeletedProperty_DoesNotStopTheRun()
    {
        var w = await World.CreateAsync();
        var service = w.Service();
        var ofLive = await service.ShareDocumentAsync(
            w.OrgId, new ShareOwnerDocument(w.OwnerA, w.PropertyA, OwnerDocumentType.Insurance, "Polizza casa Alfa", null, Today.AddDays(10)), Pdf("A"), Agent);
        var ofGone = await service.ShareDocumentAsync(
            w.OrgId, new ShareOwnerDocument(w.OwnerB, w.PropertyB, OwnerDocumentType.Insurance, "Polizza casa Beta", null, Today.AddDays(10)), Pdf("B"), Agent);
        Assert.Equal(new OwnerDocumentAlertResult(2, 0), await service.RaiseExpiryAlertsAsync());

        // The property of the second is deleted: its open item is closed, the run does not fail.
        await w.DeletePropertyAsync(w.PropertyB);
        Assert.Equal(new OwnerDocumentAlertResult(0, 1), await service.RaiseExpiryAlertsAsync());
        Assert.Equal(1, await w.Db.InternalActivities.CountAsync(a => a.CompletedAt == null && a.EntityId != null));

        // A document shared for the deleted property afterwards never gets an item (the creation would fail: no such property); the live one still does.
        var ofGoneToo = await service.ShareDocumentAsync(
            w.OrgId, new ShareOwnerDocument(w.OwnerB, w.PropertyB, OwnerDocumentType.Mandate, "Mandato", null, Today.AddDays(3)), Pdf("C"), Agent);
        var another = await service.ShareDocumentAsync(
            w.OrgId, new ShareOwnerDocument(w.OwnerA, w.PropertyA, OwnerDocumentType.Mandate, "Mandato Alfa", null, Today.AddDays(4)), Pdf("D"), Agent);
        Assert.Equal(new OwnerDocumentAlertResult(1, 0), await service.RaiseExpiryAlertsAsync());
        var versionOfAnother = another.Document.Versions.Single().Id;
        Assert.True(await w.Db.InternalActivities.AnyAsync(a => a.EntityId == versionOfAnother && a.CompletedAt == null));
        Assert.False(await w.Db.InternalActivities.AnyAsync(a => a.EntityId == ofGoneToo.Document.Versions.Single().Id));
        Assert.NotEqual(ofLive.Document.Id, ofGone.Document.Id);
    }

    /// <summary>
    /// The expiry alerts read every document that lapses, not the first 500 of a list without an order: more than 500 documents lapse, behind the history of
    /// documents that were renewed (their old versions lapsed long ago and are not the version in force), and every one gets its item once.
    /// </summary>
    [Fact]
    public async Task OwnerDocument_ExpiryAlerts_ReadEveryDocument_NotOnlyTheFirstFiveHundred()
    {
        var w = await World.CreateAsync();
        var service = w.Service();
        var now = Now.UtcDateTime;

        void Add(string title, int[] versionsExpiringIn)
        {
            var document = new OwnerDocument
            {
                OrgId = w.OrgId,
                PropertyOwnerId = w.OwnerA,
                PropertyId = w.PropertyA,
                Type = OwnerDocumentType.Insurance,
                Title = title,
                CreatedAt = now,
                CreatedByUserId = Agent,
                UpdatedAt = now,
            };
            w.Db.OwnerDocuments.Add(document);
            for (var number = 1; number <= versionsExpiringIn.Length; number++)
            {
                w.Db.OwnerDocumentVersions.Add(new OwnerDocumentVersion
                {
                    OrgId = w.OrgId,
                    OwnerDocumentId = document.Id,
                    VersionNumber = number,
                    StorageKey = $"owner-documents/{document.Id:N}/{number}.pdf",
                    FileName = "polizza.pdf",
                    ContentType = OwnerFileRules.Pdf,
                    SizeBytes = 1,
                    Sha256 = new string('a', 64),
                    ExpiresOn = Today.AddDays(versionsExpiringIn[number - 1]),
                    ScanEngine = "test",
                    UploadedAt = now,
                    UploadedByUserId = Agent,
                });
            }
        }

        // 300 documents renewed long ago (an old version that lapsed two years ago, the version in force beyond the window) and 520 that lapse in the next weeks.
        for (var i = 0; i < 300; i++)
            Add($"Rinnovata {i}", [-700, 200]);
        for (var i = 0; i < 520; i++)
            Add($"In scadenza {i}", [5 + (i % 20)]);
        await w.Db.SaveChangesAsync();

        Assert.Equal(new OwnerDocumentAlertResult(520, 0), await service.RaiseExpiryAlertsAsync());

        // Once per version: the next run finds them all raised.
        Assert.Equal(new OwnerDocumentAlertResult(0, 0), await service.RaiseExpiryAlertsAsync());
        Assert.Equal(520, await w.Db.InternalActivities.CountAsync(a => a.CompletedAt == null && a.EntityId != null));
    }

    /// <summary>
    /// PROOF TEST: privacy rule (AC-NF111-5). A conversation an owner opens can reference only what that owner holds: the property, the stay, the document or the
    /// published statement of ANOTHER owner (or one that does not exist) is the same 404, nothing is stored, and the agency cannot tie a conversation of an owner
    /// to the document of another owner either. What the owner holds opens.
    /// </summary>
    [Fact]
    public async Task OwnerInbox_AConversationCannotReferenceWhatAnotherOwnerHolds_Test()
    {
        var w = await World.CreateAsync();
        var service = w.Service();
        var ownerA = await w.AccessAsync(w.UserA);
        var ownerB = await w.AccessAsync(w.UserB);
        var documentA = await service.ShareDocumentAsync(w.OrgId, new ShareOwnerDocument(w.OwnerA, null, OwnerDocumentType.Mandate, "Mandato A", null, null), Pdf("A"), Agent);
        var documentB = await service.ShareDocumentAsync(w.OrgId, new ShareOwnerDocument(w.OwnerB, null, OwnerDocumentType.Mandate, "Mandato B", null, null), Pdf("B"), Agent);
        var statementA = await w.SeedStatementAsync(w.OwnerA);
        var statementB = await w.SeedStatementAsync(w.OwnerB);
        var stayA = await w.Stays().RequestAsync(ownerA, new OwnerStayRequestCommand(w.PropertyA, Today.AddDays(10), Today.AddDays(12)));
        var stayB = await w.Stays().RequestAsync(ownerB, new OwnerStayRequestCommand(w.PropertyB, Today.AddDays(10), Today.AddDays(12)));

        static StartOwnerConversation About(OwnerConversationTopic topic, Guid? property = null, Guid? stay = null, Guid? document = null, Guid? statement = null) =>
            new(null, null, topic, property, stay, document, statement, "Una domanda", "Il testo della domanda");

        // What another owner holds, or nothing: the same 404, nothing stored.
        foreach (var command in new[]
                 {
                     About(OwnerConversationTopic.Property, property: w.PropertyB),
                     About(OwnerConversationTopic.Property, property: Guid.NewGuid()),
                     About(OwnerConversationTopic.OwnerStay, stay: stayB.Id),
                     About(OwnerConversationTopic.OwnerStay, stay: Guid.NewGuid()),
                     About(OwnerConversationTopic.Document, document: documentB.Document.Id),
                     About(OwnerConversationTopic.StatementVersion, statement: statementB),
                 })
        {
            var refused = await Assert.ThrowsAsync<NotFoundException>(() => service.StartConversationByOwnerAsync(ownerA, command));
            Assert.Equal(OwnerCollaborationErrors.ConversationReferenceNotFound, refused.Code);
        }

        Assert.Empty(await w.Db.OwnerConversations.ToListAsync());

        // What the owner holds: opened, on its own owner profile.
        foreach (var command in new[]
                 {
                     About(OwnerConversationTopic.General),
                     About(OwnerConversationTopic.Property, property: w.PropertyA),
                     About(OwnerConversationTopic.OwnerStay, stay: stayA.Id),
                     About(OwnerConversationTopic.Document, document: documentA.Document.Id),
                     About(OwnerConversationTopic.StatementVersion, statement: statementA),
                 })
        {
            Assert.Equal(w.OwnerA, (await service.StartConversationByOwnerAsync(ownerA, command)).PropertyOwnerId);
        }

        // The agency ties a conversation of an owner to what that owner holds, in its own org: not to the document of another owner.
        var crossed = await Assert.ThrowsAsync<NotFoundException>(() => service.StartConversationByAgencyAsync(
            w.OrgId, new StartOwnerConversation(w.OwnerA, null, OwnerConversationTopic.Document, null, null, documentB.Document.Id, null, "Domanda", "Testo"), Agent));
        Assert.Equal(OwnerCollaborationErrors.ConversationReferenceNotFound, crossed.Code);
        await Assert.ThrowsAsync<NotFoundException>(() => service.StartConversationByAgencyAsync(
            w.OtherOrgId, new StartOwnerConversation(w.OwnerA, null, OwnerConversationTopic.General, null, null, null, null, "Domanda", "Testo"), "auth0|altra-agenzia"));
    }

    /// <summary>
    /// PROOF TEST: the file of a message is handed out only to who reads its conversation: the owner and the agency of that conversation do, another owner, another
    /// agency and a contact without <c>inbox.use</c> do not, and a message asked through another conversation is not found.
    /// </summary>
    [Fact]
    public async Task OwnerInbox_AnAttachmentIsDownloadedOnlyByWhoReadsItsConversation_Test()
    {
        var w = await World.CreateAsync();
        var service = w.Service();
        var ownerA = await w.AccessAsync(w.UserA);
        var general = new StartOwnerConversation(null, null, OwnerConversationTopic.General, null, null, null, null, "Visura", "Ecco la visura");
        var conversation = await service.StartConversationByOwnerAsync(ownerA, general);
        var message = await service.PostMessageByOwnerAsync(ownerA, conversation.Id, "In allegato", new OwnerFileUpload("visura.pdf", Pdf("V").Content));
        var elsewhere = await service.StartConversationByOwnerAsync(ownerA, general);

        var file = await service.OpenAttachmentForOwnerAsync(ownerA, conversation.Id, message.Id);
        await using (file.Content)
            Assert.Equal(("application/pdf", "visura.pdf"), (file.ContentType, file.FileName));
        var forAgency = await service.OpenAttachmentForAgencyAsync(w.OrgId, conversation.Id, message.Id);
        await forAgency.Content.DisposeAsync();

        var ownerB = await w.AccessAsync(w.UserB);
        await Assert.ThrowsAsync<NotFoundException>(() => service.OpenAttachmentForOwnerAsync(ownerB, conversation.Id, message.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => service.OpenAttachmentForAgencyAsync(w.OtherOrgId, conversation.Id, message.Id));
        Assert.Equal(
            OwnerCollaborationErrors.AttachmentNotFound,
            (await Assert.ThrowsAsync<NotFoundException>(() => service.OpenAttachmentForOwnerAsync(ownerA, elsewhere.Id, message.Id))).Code);

        // A contact with the calendar only has no inbox: nothing, and the message the owner wrote is not in its reach.
        await w.SeedActiveContactAsync(w.OwnerA, w.UserC, OwnerPortalPermission.CalendarRead);
        var contact = await w.AccessAsync(w.UserC);
        Assert.Equal(
            OwnerPortalErrors.PermissionMissing,
            (await Assert.ThrowsAsync<DomainForbiddenException>(() => service.OpenAttachmentForOwnerAsync(contact, conversation.Id, message.Id))).Code);
    }

    /// <summary>
    /// PROOF TEST: <c>accounting.read</c> is a permission of its own (AC-NF111-4). Without it a contact does not read the published statements of the owner
    /// accounting and does not open a document of type statement or invoice, nor a conversation about a statement; with it, and <c>documents.read</c>, it does; on
    /// its own it reads the statements and none of the documents.
    /// </summary>
    [Fact]
    public async Task OwnerContact_AccountingRead_GatesTheStatementsAndTheStatementAndInvoiceDocuments_Test()
    {
        var w = await World.CreateAsync();
        var service = w.Service();
        var owner = await w.AccessAsync(w.UserA);
        ShareOwnerDocument Document(string title, OwnerDocumentType type) => new(w.OwnerA, null, type, title, null, null);
        var mandate = await service.ShareDocumentAsync(w.OrgId, Document("Mandato", OwnerDocumentType.Mandate), Pdf("M"), Agent);
        var statement = await service.ShareDocumentAsync(w.OrgId, Document("Estratto di settembre", OwnerDocumentType.Statement), Pdf("S"), Agent);
        var invoice = await service.ShareDocumentAsync(w.OrgId, Document("Fattura 12/2026", OwnerDocumentType.Invoice), Pdf("I"), Agent);
        var published = await w.SeedStatementAsync(w.OwnerA);
        var contactId = await w.SeedActiveContactAsync(w.OwnerA, w.UserC, OwnerPortalPermission.DocumentsRead | OwnerPortalPermission.InboxUse);

        async Task ExpectAsync(string[] titles, int statements)
        {
            var read = await service.ListDocumentsForOwnerAsync(await w.AccessAsync(w.UserC));
            Assert.Equal(titles, read.Documents.Select(d => d.Title).Order().ToArray());
            Assert.Equal(statements, read.Statements.Count);
        }

        // documents.read only: the mandate, not the statement and the invoice, no statements.
        await ExpectAsync(["Mandato"], 0);
        var contact = await w.AccessAsync(w.UserC);
        await Assert.ThrowsAsync<NotFoundException>(() => service.OpenDocumentForOwnerAsync(contact, statement.Document.Id, null));
        await Assert.ThrowsAsync<NotFoundException>(() => service.OpenDocumentForOwnerAsync(contact, invoice.Document.Id, null));
        var file = await service.OpenDocumentForOwnerAsync(contact, mandate.Document.Id, null);
        await file.Content.DisposeAsync();
        var about = new StartOwnerConversation(null, null, OwnerConversationTopic.StatementVersion, null, null, null, published, "Estratto", "Una domanda");
        Assert.Equal(OwnerCollaborationErrors.ConversationReferenceNotFound, (await Assert.ThrowsAsync<NotFoundException>(() => service.StartConversationByOwnerAsync(contact, about))).Code);

        // With accounting.read too: all three documents, the statement, and a conversation about it.
        await service.SetContactPermissionsAsync(owner, contactId, [OwnerPortalPermissions.DocumentsRead, OwnerPortalPermissions.InboxUse, OwnerPortalPermissions.AccountingRead]);
        await ExpectAsync(["Estratto di settembre", "Fattura 12/2026", "Mandato"], 1);
        var withAccounting = await w.AccessAsync(w.UserC);
        var opened = await service.OpenDocumentForOwnerAsync(withAccounting, statement.Document.Id, null);
        await opened.Content.DisposeAsync();
        Assert.Equal(w.OwnerA, (await service.StartConversationByOwnerAsync(withAccounting, about)).PropertyOwnerId);

        // accounting.read on its own: the statements of the accounting, none of the documents.
        await service.SetContactPermissionsAsync(owner, contactId, [OwnerPortalPermissions.AccountingRead]);
        await ExpectAsync([], 1);
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

        public Guid PropertyB { get; private set; }

        /// <summary>The property is deleted (soft delete: the row stays, the global filter hides it).</summary>
        public async Task DeletePropertyAsync(Guid propertyId)
        {
            await using var db = NewDb();
            var property = await db.Properties.SingleAsync(p => p.Id == propertyId);
            property.IsDeleted = true;
            property.DeletedAt = Now.UtcDateTime;
            await db.SaveChangesAsync();
            Db.ChangeTracker.Clear();
        }

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

        /// <summary>A published statement version of the owner accounting (E06) of the owner profile, in the database of the world.</summary>
        public async Task<Guid> SeedStatementAsync(Guid propertyOwnerId)
        {
            await using var db = NewDb();
            var statement = new OwnerStatementVersion
            {
                OrgId = OrgId,
                PropertyOwnerId = propertyOwnerId,
                PeriodYear = 2026,
                PeriodMonth = 9,
                VersionNumber = 1,
                Status = OwnerStatementVersionStatus.Published,
                Currency = "EUR",
                CutoffAt = Now.UtcDateTime,
                EntriesSha256 = new string('a', 64),
                PublishedAt = Now.UtcDateTime,
                PublishedSha256 = new string('b', 64),
                CreatedAt = Now.UtcDateTime,
            };
            db.OwnerStatementVersions.Add(statement);
            await db.SaveChangesAsync();
            return statement.Id;
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
            (OrgId, OtherOrgId, PropertyA, PropertyB, OwnerA, OwnerB, UserA, UserB, UserC, AgencyEmail) =
                (org.Id, otherOrg.Id, propertyA.Id, propertyB.Id, owners[0].Id, owners[1].Id, userA.Id, userB.Id, userC.Id, org.ContactEmail);
        }
    }
}
