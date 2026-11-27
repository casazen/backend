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
/// NF-81 (E06/F02/US01/T01) on a real PostgreSQL database, where it counts: who an invoice is made out to is personal data, so the name, the
/// fiscal code, the VAT number, the address and the PEC are stored encrypted — the table never holds any of them in clear — and read back through
/// EF. The code of the recipient on the SDI and the province are not personal data and stay readable. Same style as
/// <see cref="GuestContactEncryptionPostgresTests"/>.
/// </summary>
public class BookingInvoiceDataEncryptionPostgresTests
{
    private const string Name = "Ristorante Da Gino S.r.l.";
    private const string FiscalCode = "RSSMRA70A01H501X";
    private const string VatNumber = "IT12345678903";
    private const string Street = "Via del Sale Rosa";
    private const string Civic = "Interno 42/B";
    private const string PostalCode = "22100";
    private const string City = "Cernobbio";
    private const string Pec = "fatture.gino@pec.example.com";
    private const string SdiCode = "ABC1234";

    // The hash of the billing data last written to Octorate (E06/F02/US01/T04): a hash of the data above, stored encrypted like it.
    private const string BillingHash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    // Whole values and pieces of them that cannot appear by chance in a base64url payload (a dot, an at sign, a space, a slash, eleven digits or
    // nine letters in a row): a shorter piece could, by luck, and make the proof flaky.
    private static readonly string[] Secrets =
        [Name, "Ristorante", FiscalCode, VatNumber, "12345678903", Street, "Sale Rosa", Civic, PostalCode, City, Pec, "fatture.gino", BillingHash];

    /// <summary>
    /// PROOF TEST: privacy rule. The raw text of every personal column of the invoice data holds a Data Protection payload and none of the value
    /// or of a piece of it; the columns that are not personal (SDI code, province, country) are in clear, and EF reads the clear values back.
    /// </summary>
    [PostgresFact]
    public async Task BookingInvoiceData_PersonalData_EncryptedAtRest_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("invenc");
        var provider = new EphemeralDataProtectionProvider();
        var at = new DateTime(2026, 11, 6, 8, 30, 0, DateTimeKind.Utc);
        var orgId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();

        await using (var db = new AppDbContext(database.CreateOptions(), tenantContext: null, provider))
        {
            var propertyId = Guid.NewGuid();
            var guestId = Guid.NewGuid();
            db.Orgs.Add(new OrgEntity
            {
                Id = orgId,
                Name = "Casa Fatture",
                Slug = $"inv-enc-{orgId:N}"[..28],
                DisplayName = "Casa Fatture",
                ContactEmail = "inv-enc@example.com",
                OrgType = OrgType.Host,
                PlanTier = PlanTier.Starter,
            });
            db.Properties.Add(new Property
            {
                Id = propertyId,
                OrgId = orgId,
                OwnerId = "auth0|owner",
                Name = "Casa del Lago",
                Address = "Via del Lago 1",
                City = "Como",
            });
            db.Guests.Add(new Guest { Id = guestId, OrgId = orgId, FirstName = "Anna", LastName = "Verdi", Email = $"anna.{guestId:N}@example.com" });
            db.Bookings.Add(new Booking
            {
                Id = bookingId,
                OrgId = orgId,
                PropertyId = propertyId,
                GuestId = guestId,
                CheckInDate = at.AddDays(10),
                CheckOutDate = at.AddDays(12),
                NumberOfGuests = 2,
                Status = BookingStatus.Confirmed,
                Source = BookingSource.Direct,
                BasePrice = 200m,
                TotalPrice = 200m,
            });
            db.BookingInvoiceData.Add(new BookingInvoiceData
            {
                OrgId = orgId,
                BookingId = bookingId,
                InvoiceRequested = true,
                InvoiceRequestedAt = at,
                RecipientKind = InvoiceRecipientKind.Company,
                Name = Name,
                FiscalCode = FiscalCode,
                VatNumber = VatNumber,
                AddressStreet = Street,
                AddressCivic = Civic,
                PostalCode = PostalCode,
                City = City,
                Province = "CO",
                CountryCode = "IT",
                SdiRecipientCode = SdiCode,
                Pec = Pec,
                Source = InvoiceDataSource.Host,
                OctorateBillingHash = BillingHash,
                OctorateBillingSentAt = at,
                CreatedAt = at,
                UpdatedAt = at,
            });
            await db.SaveChangesAsync();
        }

        // The table itself, whatever EF would have made of it: a context without Data Protection reads the stored text.
        await using var stored = database.CreateContext();
        var raws = new[]
        {
            await stored.Database.SqlQueryRaw<string>("SELECT \"Name\" AS \"Value\" FROM \"BookingInvoiceData\"").SingleAsync(),
            await stored.Database.SqlQueryRaw<string>("SELECT \"FiscalCode\" AS \"Value\" FROM \"BookingInvoiceData\"").SingleAsync(),
            await stored.Database.SqlQueryRaw<string>("SELECT \"VatNumber\" AS \"Value\" FROM \"BookingInvoiceData\"").SingleAsync(),
            await stored.Database.SqlQueryRaw<string>("SELECT \"AddressStreet\" AS \"Value\" FROM \"BookingInvoiceData\"").SingleAsync(),
            await stored.Database.SqlQueryRaw<string>("SELECT \"AddressCivic\" AS \"Value\" FROM \"BookingInvoiceData\"").SingleAsync(),
            await stored.Database.SqlQueryRaw<string>("SELECT \"PostalCode\" AS \"Value\" FROM \"BookingInvoiceData\"").SingleAsync(),
            await stored.Database.SqlQueryRaw<string>("SELECT \"City\" AS \"Value\" FROM \"BookingInvoiceData\"").SingleAsync(),
            await stored.Database.SqlQueryRaw<string>("SELECT \"Pec\" AS \"Value\" FROM \"BookingInvoiceData\"").SingleAsync(),
            await stored.Database.SqlQueryRaw<string>("SELECT \"OctorateBillingHash\" AS \"Value\" FROM \"BookingInvoiceData\"").SingleAsync(),
        };
        Assert.Equal(9, raws.Length);
        Assert.All(raws, raw =>
        {
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, raw, StringComparison.Ordinal);
            foreach (var secret in Secrets)
                Assert.DoesNotContain(secret, raw, StringComparison.OrdinalIgnoreCase);
        });

        // What is not personal data is not encrypted: the SDI code of the recipient, the province and the country.
        var clear = await stored.Database.SqlQueryRaw<string>("SELECT \"SdiRecipientCode\" AS \"Value\" FROM \"BookingInvoiceData\"").SingleAsync();
        Assert.Equal(SdiCode, clear);

        // Read through EF with Data Protection the clear values come back.
        await using var reader = new AppDbContext(database.CreateOptions(), tenantContext: null, provider);
        var row = await reader.BookingInvoiceData.IgnoreQueryFilters().AsNoTracking().SingleAsync(d => d.BookingId == bookingId);
        Assert.Equal((Name, FiscalCode, VatNumber), (row.Name, row.FiscalCode, row.VatNumber));
        Assert.Equal((Street, Civic, PostalCode, City), (row.AddressStreet, row.AddressCivic, row.PostalCode, row.City));
        Assert.Equal(Pec, row.Pec);
        Assert.Equal(BillingHash, row.OctorateBillingHash);
        Assert.Equal(SdiCode, row.SdiRecipientCode);
    }
}
