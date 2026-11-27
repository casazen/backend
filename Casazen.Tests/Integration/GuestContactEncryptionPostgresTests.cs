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
/// NF-89 (E07/F01/US01/T01) on a real PostgreSQL database, where it counts: the value of a CRM contact (an e-mail address or a phone number)
/// and its display name are stored encrypted — the table never holds either in clear — and read back through EF. What finds a contact (the
/// keyed hash of the value) is not encrypted. Same style as <see cref="ShowcaseBookingEncryptionPostgresTests"/>.
/// </summary>
public class GuestContactEncryptionPostgresTests
{
    private const string Email = "mario.rossi@example.com";
    private const string Phone = "+393331234567";
    private const string DisplayName = "Mario Rossi";

    // Whole values and pieces of them that cannot appear by chance in a base64url payload (a dot, a plus sign, a space, ten digits).
    private static readonly string[] Secrets = [Email, Phone, DisplayName, "mario.rossi", "3331234567"];

    [PostgresFact]
    public async Task GuestContact_Value_EncryptedAtRest_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("crmenc");
        var provider = new EphemeralDataProtectionProvider();
        var at = new DateTime(2026, 11, 6, 8, 30, 0, DateTimeKind.Utc);
        var emailHash = new string('a', GuestContact.ValueHashLength);
        var phoneHash = new string('b', GuestContact.ValueHashLength);

        Guid orgId;
        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, provider))
        {
            var org = new OrgEntity
            {
                Name = "Casa CRM",
                Slug = $"crm-enc-{Guid.NewGuid():N}"[..28],
                DisplayName = "Casa CRM",
                ContactEmail = "crm-enc@example.com",
                OrgType = OrgType.Host,
                PlanTier = PlanTier.Starter,
            };
            db.Orgs.Add(org);
            db.GuestContacts.AddRange(
                new GuestContact
                {
                    OrgId = org.Id,
                    Channel = GuestContactChannel.Email,
                    Value = Email,
                    ValueHash = emailHash,
                    DisplayName = DisplayName,
                    Language = "it",
                    Origin = GuestContactOrigin.CheckIn,
                    CreatedAt = at,
                    UpdatedAt = at,
                },
                new GuestContact
                {
                    OrgId = org.Id,
                    Channel = GuestContactChannel.Phone,
                    Value = Phone,
                    ValueHash = phoneHash,
                    Language = "it",
                    Origin = GuestContactOrigin.Manual,
                    CreatedAt = at,
                    UpdatedAt = at,
                });
            await db.SaveChangesAsync();
            orgId = org.Id;
        }

        // The table itself, whatever EF would have made of it: a context without Data Protection reads the stored text.
        await using var stored = database.CreateContext();
        var values = await stored.Database.SqlQueryRaw<string>("SELECT \"Value\" AS \"Value\" FROM \"GuestContacts\"").ToListAsync();
        var names = await stored.Database
            .SqlQueryRaw<string>("SELECT \"DisplayName\" AS \"Value\" FROM \"GuestContacts\" WHERE \"DisplayName\" IS NOT NULL")
            .ToListAsync();

        Assert.Equal(2, values.Count);
        Assert.Single(names);
        Assert.All(values.Concat(names), column =>
        {
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, column, StringComparison.Ordinal);
            foreach (var secret in Secrets)
                Assert.DoesNotContain(secret, column, StringComparison.OrdinalIgnoreCase);
        });

        // The same through the model of a context without Data Protection: the columns hold the payload, and the keyed hash is what finds the row.
        var rows = await stored.GuestContacts.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrgId == orgId).ToListAsync();
        Assert.All(rows, row => Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, row.Value, StringComparison.Ordinal));
        Assert.Equal(new[] { emailHash, phoneHash }, rows.Select(c => c.ValueHash).Order().ToArray());

        // Read through EF with Data Protection the clear values come back, found by the hash.
        await using var reader = new AppDbContext(database.CreateOptions(), tenantContext: null, provider);
        var email = await reader.GuestContacts.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.OrgId == orgId && c.Channel == GuestContactChannel.Email && c.ValueHash == emailHash);
        var phone = await reader.GuestContacts.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(c => c.OrgId == orgId && c.Channel == GuestContactChannel.Phone && c.ValueHash == phoneHash);
        Assert.Equal((Email, DisplayName), (email.Value, email.DisplayName));
        Assert.Equal(Phone, phone.Value);
        Assert.Null(phone.DisplayName);
    }
}
