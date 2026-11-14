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
/// NF-83, ADR-019-R4 (E06/F01/US01/T01) on a real PostgreSQL database, where the transaction and the lock of the numbering really work: two credit notes asked at
/// the same moment (a double click) never credit more than the original, because the cap is decided under the lock of the series of the original (the second one
/// waits, sees the first and is refused, 422 <c>credit_amount_exceeds</c>, and the numbers stay consecutive); and an invoice refused because the official schema is
/// not installed leaves the draft a draft, the counter of the numbers where it was and no event recorded.
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
        Guid originalId;
        await using (var db = Context(database))
        {
            var bookingId = await SeedOrgAndBookingAsync(db, orgId);
            originalId = AddDocument(db, orgId, bookingId, FiscalDocumentStatus.Issued, number: 1);
            db.FiscalNumberSequences.Add(new FiscalNumberSequence { OrgId = orgId, Series = "2026", LastNumber = 1 });
            await db.SaveChangesAsync();
        }

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

    /// <summary>
    /// PROOF TEST: money and data rule. Without the official schema an invoice is refused (422 <c>fatturapa_schema_unavailable</c>): the draft stays a draft with no
    /// number, the counter of the series is not advanced (the number is rolled back with the transaction), the booking is not locked to an issuer and no event is
    /// recorded.
    /// </summary>
    [PostgresFact]
    public async Task FiscalDocument_InvoiceWithoutTheSchema_LeavesTheDraftTheCounterAndTheEventsAsTheyWere_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("fdxsd");
        var orgId = Guid.NewGuid();
        Guid draftId;
        await using (var db = Context(database))
        {
            var bookingId = await SeedOrgAndBookingAsync(db, orgId);
            draftId = AddDocument(db, orgId, bookingId, FiscalDocumentStatus.Draft, number: null);
            await db.SaveChangesAsync();
        }

        var builder = new Mock<IFatturaPaXmlBuilder>();
        builder.Setup(b => b.Build(It.IsAny<FatturaPaInput>())).Throws(new DomainRuleException(FatturaPaErrors.SchemaUnavailable, "FatturaPaSchemaUnavailable"));
        var events = new Mock<IDomainEventRecorder>();
        await using (var db = Context(database))
        {
            var refused = await Assert.ThrowsAsync<DomainRuleException>(() => Service(db, builder.Object, events.Object).IssueAsync(draftId, confirm: true));
            Assert.Equal(FatturaPaErrors.SchemaUnavailable, refused.Code);
        }

        await using var check = Context(database);
        var draft = await check.FiscalDocuments.AsNoTracking().SingleAsync(d => d.Id == draftId);
        Assert.Equal(FiscalDocumentStatus.Draft, draft.Status);
        Assert.Null(draft.Number);
        Assert.Null(draft.SequenceNumber);
        Assert.Empty(await check.FiscalNumberSequences.Where(s => s.OrgId == orgId).ToListAsync());
        Assert.Empty(await check.BookingDocumentIssuers.Where(i => i.OrgId == orgId).ToListAsync());
        events.VerifyNoOtherCalls();
    }

    /// <summary>
    /// PROOF TEST: data rule (E11/F01/US01/T01, E14). One live invoice to an owner for each period: a second invoice of the same owner and month is refused by
    /// the database (<c>UIX_FiscalDocuments_OrgId_RecipientOwnerId_Period</c>); a discarded draft, a credit note, another month and another owner do not collide,
    /// and an invoice credited in full (<c>CreditedInFull</c>) frees its owner and month for a new one.
    /// </summary>
    [PostgresFact]
    public async Task FiscalDocument_OwnerInvoice_OneLiveInvoicePerOwnerAndPeriod_Test()
    {
        await using var database = PostgresTestDatabase.CreateMigrated("fdowner");
        var orgId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var otherOwnerId = Guid.NewGuid();
        Guid firstId;
        await using (var db = Context(database))
        {
            await SeedOrgAndBookingAsync(db, orgId);
            db.PropertyOwners.AddRange(NewOwner(orgId, ownerId), NewOwner(orgId, otherOwnerId));
            var first = OwnerInvoice(orgId, ownerId, 10, FiscalDocumentStatus.Issued, 1);
            firstId = first.Id;
            db.FiscalDocuments.Add(first);
            await db.SaveChangesAsync();
        }

        // A second live invoice of the same owner and month: the unique index refuses it.
        await using (var db = Context(database))
        {
            db.FiscalDocuments.Add(OwnerInvoice(orgId, ownerId, 10, FiscalDocumentStatus.Issued, 2));
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains(FiscalDocument.OwnerPeriodIndexName, refused.InnerException?.Message, StringComparison.Ordinal);
        }

        // What does not hold the period: a discarded draft, a credit note, another month, another owner.
        await using (var db = Context(database))
        {
            var creditNote = OwnerInvoice(orgId, ownerId, 10, FiscalDocumentStatus.Issued, 3);
            creditNote.Kind = DocumentKind.CreditNote;
            creditNote.CreditedDocumentId = firstId;
            db.FiscalDocuments.AddRange(
                OwnerInvoice(orgId, ownerId, 10, FiscalDocumentStatus.Discarded, null),
                creditNote,
                OwnerInvoice(orgId, ownerId, 11, FiscalDocumentStatus.Issued, 4),
                OwnerInvoice(orgId, otherOwnerId, 10, FiscalDocumentStatus.Issued, 5));
            await db.SaveChangesAsync();
        }

        // An invoice credited in full frees its period: a new live invoice of the same owner and month is accepted, and then holds it again.
        await using (var db = Context(database))
        {
            (await db.FiscalDocuments.SingleAsync(d => d.Id == firstId)).CreditedInFull = true;
            await db.SaveChangesAsync();
            db.FiscalDocuments.Add(OwnerInvoice(orgId, ownerId, 10, FiscalDocumentStatus.Issued, 6));
            await db.SaveChangesAsync();
        }

        await using (var db = Context(database))
        {
            db.FiscalDocuments.Add(OwnerInvoice(orgId, ownerId, 10, FiscalDocumentStatus.Issued, 7));
            var refused = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Contains(FiscalDocument.OwnerPeriodIndexName, refused.InnerException?.Message, StringComparison.Ordinal);
        }
    }

    /// <summary>An invoice of the property manager to an owner for a month of 2026 (E14): issued with its number, or a draft without one.</summary>
    private static FiscalDocument OwnerInvoice(Guid orgId, Guid ownerId, int month, FiscalDocumentStatus status, int? number) => new()
    {
        OrgId = orgId,
        Purpose = FiscalDocumentPurpose.OwnerInvoice,
        RecipientOwnerId = ownerId,
        PeriodYear = 2026,
        PeriodMonth = month,
        Source = FiscalDocumentSource.CasaZen,
        Kind = DocumentKind.Invoice,
        Status = status,
        Series = number is null ? string.Empty : "PM2026",
        SequenceNumber = number,
        Number = number is null ? null : $"PM2026/{number}",
        Currency = "EUR",
        SdiStatus = FiscalDocumentSdiStatuses.NotApplicable,
    };

    private static PropertyOwner NewOwner(Guid orgId, Guid ownerId) => new() { Id = ownerId, OrgId = orgId, FirstName = "TEST", LastName = "Proprietario", Email = $"owner.{ownerId:N}@example.com" };

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

    /// <summary>An org with its issuer profile, a property, a guest and a direct booking that is checked out; returns the booking.</summary>
    private static async Task<Guid> SeedOrgAndBookingAsync(AppDbContext db, Guid orgId)
    {
        var propertyId = Guid.NewGuid();
        var guestId = Guid.NewGuid();
        var bookingId = Guid.NewGuid();
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
            IssuerMode = DocumentIssuerMode.CasaZen,
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
        await db.SaveChangesAsync();
        return bookingId;
    }

    /// <summary>An invoice of 122 (100 and 22 of VAT, one line) of the booking: issued with its number, or a draft without one.</summary>
    private static Guid AddDocument(AppDbContext db, Guid orgId, Guid bookingId, FiscalDocumentStatus status, int? number)
    {
        var id = Guid.NewGuid();
        var document = new FiscalDocument
        {
            Id = id,
            OrgId = orgId,
            BookingId = bookingId,
            Source = FiscalDocumentSource.CasaZen,
            Kind = DocumentKind.Invoice,
            Status = status,
            Series = number is null ? string.Empty : "2026",
            SequenceNumber = number,
            Number = number is null ? null : $"2026/{number}",
            IssueDate = number is null ? null : new DateOnly(2026, 11, 2),
            Currency = "EUR",
            TaxableBase = 100m,
            VatAmount = 22m,
            Total = 122m,
            VatRatePercent = 22m,
            SdiStatus = number is null ? FiscalDocumentSdiStatuses.NotApplicable : FiscalDocumentSdiStatuses.ToSend,
            RecipientKind = InvoiceRecipientKind.Company,
            RecipientName = "TEST Cliente Srl",
            RecipientVatNumber = "IT09876543217",
            RecipientAddressStreet = "Via del Cliente",
            RecipientPostalCode = "00100",
            RecipientCity = "Roma",
            RecipientCountryCode = "IT",
        };
        document.Lines.Add(new FiscalDocumentLine
        {
            OrgId = orgId,
            DocumentId = id,
            LineNumber = 1,
            Description = "TEST Soggiorno",
            Quantity = 1m,
            UnitPrice = 100m,
            TotalPrice = 100m,
            VatRatePercent = 22m,
        });
        db.FiscalDocuments.Add(document);
        return id;
    }

    private static FiscalDocumentService Service(AppDbContext db, IFatturaPaXmlBuilder? builder = null, IDomainEventRecorder? events = null)
    {
        var featureFlags = new TestFeatureFlags(FeatureFlags.FiscalDocumentIssuance);

        // The shipped catalog of the tax rules (FiscalDocuments:TaxRules): the Italian ordinary VAT rule in force at the check-out of the stay.
        var rules = TaxRuleCatalogs.Resolver();
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns("Test");
        var time = new FakeTimeProvider(Now);
        var issuers = new DocumentIssuerService(db, featureFlags, rules, environment.Object, time, NullLogger<DocumentIssuerService>.Instance);
        var xml = new Mock<IFatturaPaXmlBuilder>();
        xml.Setup(x => x.Build(It.IsAny<FatturaPaInput>())).Returns("<p:FatturaElettronica/>");
        return new FiscalDocumentService(
            db,
            issuers,
            Mock.Of<IInvoiceDataService>(),
            new FiscalNumberingService(db, time, NullLogger<FiscalNumberingService>.Instance),
            builder ?? xml.Object,
            events ?? Mock.Of<IDomainEventRecorder>(),
            new MigraDocPdfDocumentRenderer(),
            new ResxDocumentTexts(),
            rules,
            time,
            NullLogger<FiscalDocumentService>.Instance);
    }

    private static AppDbContext Context(PostgresTestDatabase database) => new(database.CreateOptions(), tenantContext: null, Provider);
}
