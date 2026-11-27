using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Tests.Integration.Postgres;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// ADR-032-R3 (E05/F01/US01/T01) on a real PostgreSQL database, where it counts: the words of a conversation with a guest are stored as a Data
/// Protection payload and the table never holds them in clear, and no plaintext column for them exists. Read through EF with Data Protection the
/// clear text comes back. Same style as <see cref="GuestContactEncryptionPostgresTests"/>.
/// </summary>
public class OtaMessageEncryptionPostgresTests
{
    private const string GuestWords = "Buongiorno, arriviamo alle 22 con due bambini e un cane piccolo";
    private const string HostWords = "Benvenuti! Vi aspettiamo, il codice della cassetta e nel messaggio di domani";

    // Whole texts and pieces of them that cannot appear by chance in a base64url payload (a comma, a space, a long word).
    private static readonly string[] Secrets = [GuestWords, HostWords, "Buongiorno,", "arriviamo alle", "bambini e un cane", "Benvenuti!", "cassetta"];

    [PostgresFact]
    public async Task OtaMessage_Text_NotPlaintextInDatabase_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("otamsgenc");
        var provider = new EphemeralDataProtectionProvider();
        var at = new DateTime(2026, 11, 6, 9, 15, 0, DateTimeKind.Utc);

        Guid orgId;
        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, provider))
        {
            var org = new OrgEntity
            {
                Name = "Casa Messaggi",
                Slug = $"ota-msg-enc-{Guid.NewGuid():N}"[..28],
                DisplayName = "Casa Messaggi",
                ContactEmail = "ota-msg-enc@example.com",
                OrgType = OrgType.Host,
                PlanTier = PlanTier.Starter,
            };
            db.Orgs.Add(org);
            var thread = new OtaMessageThread
            {
                OrgId = org.Id,
                OctorateThreadId = "9001",
                OctorateAccommodationId = "16997",
                ChannelSource = OtaChannel.Airbnb,
                Kind = OtaThreadKind.Request,
                LastMessageAt = at,
                LastMessageDirection = OtaMessageDirection.Outbound,
                CreatedAt = at,
                UpdatedAt = at,
            };
            db.OtaMessageThreads.Add(thread);
            db.OtaMessages.AddRange(
                new OtaMessage
                {
                    OrgId = org.Id,
                    ThreadId = thread.Id,
                    OctorateMessageId = "70001",
                    Direction = OtaMessageDirection.Inbound,
                    SenderName = "Mario Rossi",
                    Text = GuestWords,
                    SentAt = at,
                    DeliveryState = OtaMessageDeliveryState.Sent,
                    Origin = MessageOrigin.Guest,
                    CreatedAt = at,
                },
                new OtaMessage
                {
                    OrgId = org.Id,
                    ThreadId = thread.Id,
                    OctorateMessageId = "70002",
                    Direction = OtaMessageDirection.Outbound,
                    SenderName = "Casa Messaggi",
                    Text = HostWords,
                    SentAt = at.AddMinutes(5),
                    DeliveryState = OtaMessageDeliveryState.Sent,
                    Origin = MessageOrigin.Person,
                    CreatedAt = at.AddMinutes(5),
                });
            await db.SaveChangesAsync();
            orgId = org.Id;
        }

        // The table itself, whatever EF would have made of it: a context without Data Protection reads the stored text.
        await using var stored = database.CreateContext();
        var texts = await stored.Database.SqlQueryRaw<string>("SELECT \"TextEncrypted\" AS \"Value\" FROM \"OtaMessages\"").ToListAsync();

        Assert.Equal(2, texts.Count);
        Assert.All(texts, text =>
        {
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, text, StringComparison.Ordinal);
            foreach (var secret in Secrets)
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        });

        // No plaintext column for the words exists: the only one is the encrypted one.
        var columns = await stored.Database
            .SqlQueryRaw<string>("SELECT column_name AS \"Value\" FROM information_schema.columns WHERE table_name = 'OtaMessages'")
            .ToListAsync();
        Assert.Contains("TextEncrypted", columns);
        Assert.DoesNotContain("Text", columns);
        Assert.DoesNotContain("MessageText", columns);

        // The same through the model of a context without Data Protection: the property holds the payload.
        var rows = await stored.OtaMessages.IgnoreQueryFilters().AsNoTracking().Where(m => m.OrgId == orgId).ToListAsync();
        Assert.All(rows, row => Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, row.Text, StringComparison.Ordinal));

        // Read through EF with Data Protection the clear texts come back.
        await using var reader = new AppDbContext(database.CreateOptions(), tenantContext: null, provider);
        var readBack = await reader.OtaMessages.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.OrgId == orgId)
            .OrderBy(m => m.SentAt)
            .Select(m => m.Text)
            .ToListAsync();
        Assert.Equal([GuestWords, HostWords], readBack);
    }
}
