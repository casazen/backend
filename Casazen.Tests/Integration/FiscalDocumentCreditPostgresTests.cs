using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Documents;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit;
using Casazen.Tests.Unit.Services;
using Casazen.Web.Resources;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;
using DocumentKind = Casazen.Core.Entities.Enums.FiscalDocumentKind;
using RuleKind = Casazen.Core.Options.FiscalDocumentKind;

namespace Casazen.Tests.Integration;

/// <summary>
/// NF-83, ADR-019-R4 (E06/F01/US01/T01) on a real PostgreSQL database, where two requests really run together: two credit notes asked at the same moment (a
/// double click) never credit more than the original. The cap is decided under the lock of the series of the original, so the second one waits, sees the first
/// and is refused (422 <c>credit_amount_exceeds</c>), and the numbers stay consecutive.
/// </summary>
public class FiscalDocumentCreditPostgresTests
{
    private static readonly IDataProtectionProvider Provider = new EphemeralDataProtectionProvider();
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    /// <summary>PROOF TEST: money rule. An invoice of 122 and two parallel credit notes of 100: one is issued, the other refused, the total credited stays 100.</summary>
    [PostgresFact]
    public async Task FiscalDocument_ParallelCreditNotes_NeverCreditMoreThanTheOriginal_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("fdcred");
        var orgId = Guid.NewGuid();
        var originalId = await SeedAsync(database, orgId);

        var outcomes = await Task.WhenAll(CreditAsync(database, originalId), CreditAsync(database, originalId));

        Assert.Equal(1, outcomes.Count(o => o is null));
        var refused = Assert.Single(outcomes, o => o is not null);
        Assert.Equal(FiscalDocumentErrors.CreditAmountExceeds, Assert.IsType<DomainRuleException>(refused).Code);

        await using var check = Context(database);
        var notes = await check.FiscalDocuments.AsNoTracking()
            .Where(d => d.OrgId == orgId && d.Kind == DocumentKind.CreditNote && d.Status == FiscalDocumentStatus.Issued)
            .ToListAsync();
        Assert.Equal(100m, Assert.Single(notes).Total);
        Assert.Equal(2, notes[0].SequenceNumber);
        Assert.Equal(2, (await check.FiscalNumberSequences.SingleAsync(s => s.OrgId == orgId)).LastNumber);
    }

    /// <summary>One credit note as the endpoint asks for it: its own context and service, the exception (if any) returned.</summary>
    private static async Task<Exception?> CreditAsync(PostgresTestDatabase database, Guid originalId)
    {
        await using var db = Context(database);
        try
        {
            await Service(db).CreateCreditNoteAsync(originalId, 100m, "TEST rimborso", confirm: true, null);
            return null;
        }
        catch (DomainRuleException ex)
        {
            return ex;
        }
    }

    private static async Task<Guid> SeedAsync(PostgresTestDatabase database, Guid orgId)
    {
        await using var db = Context(database);
        var propertyId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
        var originalId = Guid.NewGuid();
        db.Orgs.Add(new OrgEntity
        {
            Id = orgId,
            Name = "TEST Casa Vacanze",
            Slug = $"fd-cred-{orgId:N}"[..28],
            DisplayName = "TEST Casa Vacanze",
            ContactEmail = "fd-cred@example.com",
            OrgType = OrgType.Host,
            PlanTier = PlanTier.Starter,
            HasPartitaIva = true,
            PartitaIvaNumber = "01234567897",
            FiscalCode = "01234567897",
        });
        db.OrgFiscalIssuerProfiles.Add(new OrgFiscalIssuerProfile
        {
            OrgId = orgId,
            IssuerMode = DocumentIssuerMode.CasaZenDirectOnly,
            DocumentRegime = DocumentRegime.VatOrdinary,
            LegalName = "TEST Casa Vacanze Srl",
            AddressStreet = "Via di Test",
            AddressCivic = "1",
            PostalCode = "00187",
            City = "Roma",
            Province = "RM",
            CountryCode = "IT",
        });
        db.Properties.Add(new Property { Id = propertyId, OrgId = orgId, OwnerId = "auth0|host", Name = "TEST Casa", Address = "Via 1", City = "Roma", Currency = "EUR" });
        db.Guests.Add(new Guest { Id = guestId, OrgId = orgId, FirstName = "Test", LastName = "Guest", Email = $"guest.{guestId:N}@example.com" });
        db.Bookings.Add(new Booking
        {
            Id = bookingId,
            OrgId = orgId,
            PropertyId = propertyId,
            GuestId = guestId,
            CheckInDate = new DateTime(2026, 11, 1),
            CheckOutDate = new DateTime(2026, 11, 4),
            NumberOfGuests = 2,
            Status = BookingStatus.CheckedOut,
            Source = BookingSource.Direct,
            BasePrice = 122m,
            TotalPrice = 122m,
        });
        var original = new FiscalDocument
        {
            Id = originalId,
            OrgId = orgId,
            BookingId = bookingId,
            Source = FiscalDocumentSource.CasaZen,
            Kind = DocumentKind.Invoice,
            Status = FiscalDocumentStatus.Issued,
            Series = "2026",
            SequenceNumber = 1,
            Number = "2026/1",
            IssueDate = new DateOnly(2026, 11, 2),
            Currency = "EUR",
            TaxableBase = 100m,
            VatAmount = 22m,
            Total = 122m,
            VatRatePercent = 22m,
            SdiStatus = FiscalDocumentSdiStatuses.ToSend,
            RecipientKind = InvoiceRecipientKind.Company,
            RecipientName = "TEST Cliente Srl",
            RecipientVatNumber = "IT09876543217",
            RecipientAddressStreet = "Via del Cliente",
            RecipientPostalCode = "00100",
            RecipientCity = "Roma",
            RecipientCountryCode = "IT",
        };
        original.Lines.Add(new FiscalDocumentLine
        {
            OrgId = orgId,
            DocumentId = originalId,
            LineNumber = 1,
            Description = "TEST Soggiorno",
            Quantity = 1m,
            UnitPrice = 100m,
            TotalPrice = 100m,
            VatRatePercent = 22m,
        });
        db.FiscalDocuments.Add(original);
        db.FiscalNumberSequences.Add(new FiscalNumberSequence { OrgId = orgId, Series = "2026", LastNumber = 1 });
        await db.SaveChangesAsync();
        return originalId;
    }

    private static FiscalDocumentService Service(AppDbContext db)
    {
        var featureFlags = new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance);
        var options = Options.Create(new FiscalDocumentRulesOptions
        {
            Rules =
            {
                [DocumentRegime.VatOrdinary] = new FiscalDocumentRule
                {
                    DocumentKind = RuleKind.Invoice,
                    FatturaPaRegimeCode = "RF01",
                    VatRatePercent = 22m,
                    TouristTaxNatureCode = "N1",
                },
            },
        });
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Test");
        var time = new FakeTimeProvider(Now);
        var issuers = new DocumentIssuerService(db, featureFlags, options, environment.Object, time, NullLogger<DocumentIssuerService>.Instance);
        var xml = new Mock<IFatturaPaXmlBuilder>();
        xml.Setup(x => x.Build(It.IsAny<FatturaPaInput>())).Returns("<p:FatturaElettronica/>");
        return new FiscalDocumentService(
            db,
            issuers,
            Mock.Of<IInvoiceDataService>(),
            new FiscalNumberingService(db, time, NullLogger<FiscalNumberingService>.Instance),
            xml.Object,
            Mock.Of<IDomainEventRecorder>(),
            new MigraDocPdfDocumentRenderer(),
            new ResxDocumentTexts(),
            options,
            time,
            NullLogger<FiscalDocumentService>.Instance);
    }

    private static AppDbContext Context(PostgresTestDatabase database) => new(database.CreateOptions(), tenantContext: null, Provider);
}
