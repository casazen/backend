using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// E05/F07/US01/T01 (NF-71): the table of the scheduled messages. The privacy rule has its proof (the text a host wrote for a guest is a Data Protection payload at rest, with the purpose of the words of the
/// conversations); the others are the shape the brief asks for: one row for a booking, a routine and an action, the states that are open, the foreign keys that let go or cascade, the token of concurrency
/// the claim of a send relies on, and the organization that owns the row.
/// </summary>
public class ScheduledMessageEntityTests
{
    // One provider for the whole class: EF keeps one model per provider, and a provider per test would fill the cache the other tests share.
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    private static AppDbContext NewDb(string? name = null, ITenantContext? tenant = null) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(name ?? Guid.NewGuid().ToString()).Options, tenant, Provider);

    /// <summary>
    /// PROOF TEST: privacy rule. The edit a host makes to the text of a message (a Wi-Fi password or the code of a key box can be in it) is stored encrypted, with the purpose of the words of the conversations, and has
    /// no column length (the payload is longer than the text). What the column holds is a Data Protection payload with none of the words; only the converter of that purpose reads it back. Written, not run
    /// against PostgreSQL: the EF InMemory store keeps the values the model hands it, so the proof is on the model and its converter.
    /// </summary>
    [Fact]
    public void ScheduledMessage_TextOverride_IsEncryptedAtRest_Test()
    {
        const string words = "Il codice della cassetta è 4921, la rete è CasaZenOstuni con password Zefirino-2026";
        using var db = NewDb();

        Assert.Contains(
            EncryptedColumns.All,
            c => c.EntityType == typeof(ScheduledMessage) && c.Property == nameof(ScheduledMessage.TextOverride) && c.Purpose == EncryptedColumns.OtaMessageTextPurpose);
        var column = db.Model.FindEntityType(typeof(ScheduledMessage))!.FindProperty(nameof(ScheduledMessage.TextOverride))!;
        var converter = Assert.IsType<EncryptedStringConverter>(column.GetValueConverter());

        // No length on the column: the payload of a text of the longest size does not fit in a column of that size.
        Assert.Null(column.GetMaxLength());
        var stored = (string)converter.ConvertToProvider(words)!;

        // What the column holds: a payload, and nothing of the words in it.
        Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("4921", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("Zefirino", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("password", stored, StringComparison.OrdinalIgnoreCase);

        // Read back only through the decryption of its own purpose: the protector of another purpose cannot read it.
        Assert.Equal(words, converter.ConvertFromProvider(stored));
        Assert.ThrowsAny<CryptographicException>(() => Provider.CreateProtector(EncryptedColumns.AssistantContentPurpose).Unprotect(stored));
        Assert.True(EncryptedColumns.IsConfigured(db));
    }

    [Fact]
    public void TheTable_HoldsOneRowForABookingARoutineAndAnAction_AndTheStatesThatAreOpenAreIndexed()
    {
        using var db = NewDb();
        var entity = db.Model.FindEntityType(typeof(ScheduledMessage))!;

        var plan = Assert.Single(entity.GetIndexes(), i => i.GetDatabaseName() == ScheduledMessage.PlanIndexName);
        Assert.True(plan.IsUnique);
        Assert.Equal(
            [nameof(ScheduledMessage.BookingId), nameof(ScheduledMessage.RoutineId), nameof(ScheduledMessage.ActionIndex)],
            plan.Properties.Select(p => p.Name));

        // The sweep reads the open rows by their time: planned (1), blocked (4) and failed (5).
        var open = Assert.Single(entity.GetIndexes(), i => i.GetDatabaseName() == "IX_ScheduledMessages_Open");
        Assert.Equal([nameof(ScheduledMessage.Status), nameof(ScheduledMessage.PlannedAtUtc)], open.Properties.Select(p => p.Name));
        Assert.Equal("\"Status\" IN (1, 4, 5)", open.GetFilter());
        Assert.Equal(
            [ScheduledMessageStatus.Scheduled, ScheduledMessageStatus.Blocked, ScheduledMessageStatus.Failed],
            Enum.GetValues<ScheduledMessageStatus>().Where(s => new ScheduledMessage { Status = s }.IsOpen));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], Enum.GetValues<ScheduledMessageStatus>().Select(s => (int)s));
    }

    [Fact]
    public void TheForeignKeys_FollowTheBookingAndTheProperty_LetGoOfARoutineThatIsDeleted_AndNeverDeleteAnOrg()
    {
        using var db = NewDb();
        var entity = db.Model.FindEntityType(typeof(ScheduledMessage))!;

        var behaviours = entity.GetForeignKeys().ToDictionary(fk => fk.PrincipalEntityType.ClrType, fk => fk.DeleteBehavior);

        Assert.Equal(DeleteBehavior.Cascade, behaviours[typeof(Booking)]);
        Assert.Equal(DeleteBehavior.Cascade, behaviours[typeof(Property)]);
        Assert.Equal(DeleteBehavior.SetNull, behaviours[typeof(Routine)]);
        Assert.Equal(DeleteBehavior.Restrict, behaviours[typeof(OrgEntity)]);
        Assert.True(entity.FindProperty(nameof(ScheduledMessage.RoutineId))!.IsNullable);
    }

    [Fact]
    public void TheVersion_IsTheTokenOfConcurrencyTheClaimOfASendRelaysOn()
    {
        using var db = NewDb();
        var version = db.Model.FindEntityType(typeof(ScheduledMessage))!.FindProperty(nameof(ScheduledMessage.Version))!;

        Assert.True(version.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, version.ValueGenerated);
    }

    /// <summary>
    /// A row belongs to one organization: the tenant filter shows a caller its own rows only, so a message of another organization is never read by a query that forgot to name the org.
    /// </summary>
    [Fact]
    public async Task ScheduledMessages_BelongToOneOrganization_AndTheTenantFilterHidesTheOthers()
    {
        var name = Guid.NewGuid().ToString();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        await using (var seed = NewDb(name))
        {
            seed.ScheduledMessages.AddRange(Row(orgA), Row(orgB));
            await seed.SaveChangesAsync();
        }

        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.OrgId).Returns(orgA);
        tenant.SetupGet(t => t.FilterEnabled).Returns(true);
        await using var scoped = NewDb(name, tenant.Object);

        var visible = await scoped.ScheduledMessages.AsNoTracking().ToListAsync();

        Assert.Equal(orgA, Assert.Single(visible).OrgId);
        Assert.IsAssignableFrom<ITenantOwned>(new ScheduledMessage());
    }

    private static ScheduledMessage Row(Guid orgId) => new()
    {
        OrgId = orgId,
        PropertyId = Guid.NewGuid(),
        BookingId = Guid.NewGuid(),
        RoutineId = Guid.NewGuid(),
        RoutineName = "Messaggio di arrivo (TEST)",
        TemplateRef = Guid.NewGuid().ToString(),
        PlannedAtUtc = new DateTime(2026, 11, 19, 14, 0, 0, DateTimeKind.Utc),
    };
}
