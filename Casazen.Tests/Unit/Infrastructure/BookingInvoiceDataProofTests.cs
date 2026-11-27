using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Multitenancy;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// NF-81, E06/F02/US01/T01: who an invoice is made out to is data of one org and personal data. The two rules that cost if they fail: the row of
/// another org is never returned to a caller (the tenant filter of every <see cref="ITenantOwned"/> entity, TN-2), and the personal columns are
/// stored encrypted with a purpose of their own.
/// </summary>
public class BookingInvoiceDataProofTests
{
    // One provider for the whole class: EF keeps one model per provider, and a provider per test would fill the cache the other tests share.
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();

    private static readonly string[] PersonalColumns =
    [
        nameof(BookingInvoiceData.Name),
        nameof(BookingInvoiceData.FiscalCode),
        nameof(BookingInvoiceData.VatNumber),
        nameof(BookingInvoiceData.AddressStreet),
        nameof(BookingInvoiceData.AddressCivic),
        nameof(BookingInvoiceData.PostalCode),
        nameof(BookingInvoiceData.City),
        nameof(BookingInvoiceData.Pec),
    ];

    /// <summary>
    /// PROOF TEST: data rule (tenant isolation). Two orgs each have the invoice data of a booking. The caller of org A reads its own row only: the
    /// one of org B is neither in the list nor found by its booking (the 404 of the endpoints), and a caller with no org matches nothing.
    /// </summary>
    [Fact]
    public async Task BookingInvoiceData_OtherOrgRow_Invisible_Test()
    {
        var databaseName = $"booking-invoice-data-{Guid.NewGuid()}";
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var mine = NewRow(orgA, "Anna Verdi");
        var theirs = NewRow(orgB, "Bruno Neri");

        // The system writes both rows (no caller, so no filter), as a job or the seed would.
        await using (var system = NewDb(databaseName, tenant: null))
        {
            system.BookingInvoiceData.AddRange(mine, theirs);
            await system.SaveChangesAsync();
        }

        await using var asOrgA = NewDb(databaseName, new FixedTenantContext(orgA));
        var visible = await asOrgA.BookingInvoiceData.AsNoTracking().ToListAsync();

        var row = Assert.Single(visible);
        Assert.Equal(mine.Id, row.Id);
        Assert.Equal(orgA, row.OrgId);
        Assert.Equal("Anna Verdi", row.Name);

        // By the booking too: the row of the other org is not there.
        Assert.Null(await asOrgA.BookingInvoiceData.AsNoTracking().FirstOrDefaultAsync(d => d.BookingId == theirs.BookingId));
        Assert.False(await asOrgA.BookingInvoiceData.AsNoTracking().AnyAsync(d => d.OrgId == orgB));

        // A caller with no org is fail-closed: it matches nothing.
        await using var nobody = NewDb(databaseName, new FixedTenantContext(null));
        Assert.Empty(await nobody.BookingInvoiceData.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// PROOF TEST: privacy rule, on the model. The EF InMemory database keeps the values the model hands it and not the stored text, so the proof
    /// is on the model and its converters: each personal column has the converter of the purpose <c>Casazen.BookingInvoiceData</c>, no length
    /// (the payload is longer than the text), a payload with none of the value, and only the protector of that purpose reads it back. The raw
    /// SQL proof on PostgreSQL is <c>BookingInvoiceDataEncryptionPostgresTests</c>.
    /// </summary>
    [Fact]
    public void BookingInvoiceData_PersonalColumns_AreEncryptedByTheModel_Test()
    {
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            tenantContext: null,
            Provider);
        var entity = db.Model.FindEntityType(typeof(BookingInvoiceData))!;
        var otherPurpose = Provider.CreateProtector(EncryptedColumns.OtaSecretsPurpose);

        Assert.Equal("Casazen.BookingInvoiceData", EncryptedColumns.BookingInvoiceDataPurpose);
        foreach (var property in PersonalColumns)
        {
            Assert.Contains(
                EncryptedColumns.All,
                c => c.EntityType == typeof(BookingInvoiceData) && c.Property == property && c.Purpose == EncryptedColumns.BookingInvoiceDataPurpose);

            var column = entity.FindProperty(property)!;
            var converter = Assert.IsType<EncryptedStringConverter>(column.GetValueConverter());
            Assert.Null(column.GetMaxLength());

            const string value = "RSSMRA70A01H501X-Via del Sale Rosa 42";
            var stored = (string)converter.ConvertToProvider(value)!;

            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, stored, StringComparison.Ordinal);
            Assert.DoesNotContain(value, stored, StringComparison.Ordinal);
            Assert.Equal(value, converter.ConvertFromProvider(stored));
            Assert.ThrowsAny<CryptographicException>(() => otherPurpose.Unprotect(stored));
        }

        // The columns that are not personal data stay readable by the database: they are neither encrypted nor unbounded.
        foreach (var property in new[]
                 {
                     nameof(BookingInvoiceData.Province),
                     nameof(BookingInvoiceData.CountryCode),
                     nameof(BookingInvoiceData.SdiRecipientCode),
                 })
        {
            Assert.Null(entity.FindProperty(property)!.GetValueConverter());
            Assert.NotNull(entity.FindProperty(property)!.GetMaxLength());
        }

        Assert.True(EncryptedColumns.IsConfigured(db));
    }

    private static BookingInvoiceData NewRow(Guid orgId, string name) => new()
    {
        OrgId = orgId,
        BookingId = Guid.NewGuid(),
        RecipientKind = InvoiceRecipientKind.Guest,
        Name = name,
        CountryCode = "IT",
        Source = InvoiceDataSource.Host,
    };

    private static AppDbContext NewDb(string databaseName, ITenantContext? tenant) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(databaseName).Options, tenant);

    private sealed class FixedTenantContext(Guid? orgId) : ITenantContext
    {
        public Guid? OrgId { get; } = orgId;

        public bool FilterEnabled => true;
    }
}
