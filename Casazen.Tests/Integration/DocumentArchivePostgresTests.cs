using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Enums;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// NF-99, E07/F03/US03 on a real PostgreSQL database: what EF InMemory cannot prove. The day of an upload is the day in Europe/Rome worked out by
/// the server (an upload at 23:30 UTC is the next day in Rome, in winter and in summer), the filter by days and the order use that day, the
/// search is an ILIKE whose wildcards are escaped, the three sources are concatenated in one statement whose columns the database can type (a
/// column that is empty in every branch, like the booking, is filterable too), and the count by type groups the same union.
/// </summary>
public class DocumentArchivePostgresTests : IAsyncLifetime
{
    private PostgresTestDatabase? _database;
    private AppDbContext _db = null!;
    private HostScopeWorld _hosts = null!;
    private Guid _summerNight;
    private Guid _winterNight;
    private Guid _summerEvening;
    private Guid _payment;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateMigratedAsync();
        _db = _database.CreateContext();
        _hosts = await HostScopeScenario.SeedAsync(_db);

        // 2026-10-05 23:30 UTC is 01:30 of the 6th in Rome (summer time, +2); 2026-01-15 23:30 UTC is 00:30 of the 16th (winter time, +1);
        // 2026-10-05 21:30 UTC is 23:30 of the 5th, still the same day.
        _summerNight = AddPropertyDocument("notte-estate_50%.pdf", new DateTime(2026, 10, 5, 23, 30, 0, DateTimeKind.Utc));
        _winterNight = AddPropertyDocument("notte-inverno.pdf", new DateTime(2026, 1, 15, 23, 30, 0, DateTimeKind.Utc));
        _summerEvening = AddPropertyDocument("sera-estate.pdf", new DateTime(2026, 10, 5, 21, 30, 0, DateTimeKind.Utc));
        _payment = Guid.NewGuid();
        _db.TouristTaxRemittances.Add(new TouristTaxRemittance
        {
            Id = _payment,
            OrgId = _hosts.OrgId,
            Comune = "Ostuni",
            ComuneKey = "ostuni",
            PeriodFrom = new DateOnly(2026, 5, 1),
            PeriodTo = new DateOnly(2026, 5, 31),
            PaidAmount = 18.00m,
            PaidOn = new DateOnly(2026, 6, 10),
            ReceiptStorageKey = StorageKeys.TouristTaxReceipt(_hosts.OrgId, _payment, "ricevuta.pdf"),
            ReceiptFileName = "ricevuta.pdf",
            ReceiptContentType = "application/pdf",
            ReceiptSizeBytes = 1234,
            RecordedByUserId = "auth0|host",
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        if (_database is not null)
            await _database.DisposeAsync();
    }

    private Guid AddPropertyDocument(string fileName, DateTime uploadedAt)
    {
        var document = new PropertyDocument
        {
            PropertyId = _hosts.Granted.Id,
            OrgId = _hosts.OrgId,
            FileName = fileName,
            StorageUrl = StorageKeys.PropertyDocument(_hosts.Granted.Id, fileName),
            DocumentType = DocumentType.Other,
            UploadedBy = "auth0|host",
            UploadedAt = uploadedAt,
        };
        _db.PropertyDocuments.Add(document);
        return document.Id;
    }

    private DocumentArchiveService Service()
    {
        var storage = new InMemoryFileStorage();
        IDocumentArchiveSource[] sources =
        [
            new PropertyDocumentArchiveSource(_db, storage),
            new LeaseDocumentArchiveSource(_db, storage),
            new TouristTaxRemittanceArchiveSource(_db, storage),
        ];
        return new DocumentArchiveService(sources, _db, Options.Create(new DocumentArchiveOptions()), NullLogger<DocumentArchiveService>.Instance);
    }

    private static DocumentArchiveQuery Query(
        DateOnly? from = null, DateOnly? to = null, string? search = null, Guid? propertyId = null, Guid? bookingId = null, ArchiveDocumentType[]? types = null) =>
        new(types, from, to, propertyId, bookingId, search, 1, DocumentArchiveQuery.MaxPageSize);

    [PostgresFact]
    public async Task List_TheDayOfAnUpload_IsTheDayInRome_InWinterAndInSummer_AndTheOrderIsOnThatDay()
    {
        var page = await Service().ListAsync(_hosts.OrgWide, DocumentArchiveWorld.Everything, Query(types: [ArchiveDocumentType.PropertyDocument]));

        Assert.Equal(
            [
                (_summerNight, new DateOnly(2026, 10, 6)),
                (_summerEvening, new DateOnly(2026, 10, 5)),
                (_winterNight, new DateOnly(2026, 1, 16)),
            ],
            page.Items.Select(i => (i.Id, i.DocumentDate)));
    }

    [PostgresFact]
    public async Task List_TheFilterByDays_IsOnTheDayInRome()
    {
        var service = Service();
        var theSixth = await service.ListAsync(
            _hosts.OrgWide, DocumentArchiveWorld.Everything, Query(new DateOnly(2026, 10, 6), new DateOnly(2026, 10, 6)));
        var theSixteenth = await service.ListAsync(
            _hosts.OrgWide, DocumentArchiveWorld.Everything, Query(new DateOnly(2026, 1, 16), new DateOnly(2026, 1, 16)));
        var theFifteenth = await service.ListAsync(
            _hosts.OrgWide, DocumentArchiveWorld.Everything, Query(new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 15)));

        Assert.Equal([_summerNight], theSixth.Items.Select(i => i.Id));
        Assert.Equal([_winterNight], theSixteenth.Items.Select(i => i.Id));
        Assert.Empty(theFifteenth.Items);
    }

    [PostgresFact]
    public async Task List_TheSearch_IsCaseInsensitive_AndAWildcardIsTakenForTheCharacter()
    {
        var service = Service();

        var byWord = await service.ListAsync(_hosts.OrgWide, DocumentArchiveWorld.Everything, Query(search: "INVERNO"));
        var byPercent = await service.ListAsync(_hosts.OrgWide, DocumentArchiveWorld.Everything, Query(search: "50%"));
        var byUnderscore = await service.ListAsync(_hosts.OrgWide, DocumentArchiveWorld.Everything, Query(search: "estate_"));
        var byCommune = await service.ListAsync(_hosts.OrgWide, DocumentArchiveWorld.Everything, Query(search: "ostun"));

        Assert.Equal([_winterNight], byWord.Items.Select(i => i.Id));
        // 50% finds the title that has those characters, not every title that has "50" followed by anything; _ is not "any character".
        Assert.Equal([_summerNight], byPercent.Items.Select(i => i.Id));
        Assert.Equal([_summerNight], byUnderscore.Items.Select(i => i.Id));
        Assert.Equal([_payment], byCommune.Items.Select(i => i.Id));
    }

    [PostgresFact]
    public async Task List_TheThreeSourcesAreOneStatementThatTheDatabaseCanType_ABookingFilterIncluded()
    {
        var service = Service();

        var everything = await service.ListAsync(_hosts.OrgWide, DocumentArchiveWorld.Everything, Query());
        // The booking is empty in every branch: filtering by it is an empty answer, not an error of types.
        var byBooking = await service.ListAsync(_hosts.OrgWide, DocumentArchiveWorld.Everything, Query(bookingId: Guid.NewGuid()));
        var byProperty = await service.ListAsync(_hosts.OrgWide, DocumentArchiveWorld.Everything, Query(propertyId: _hosts.Granted.Id));

        // The three uploads and the payment with its receipt (the leases have no file here), newest day first: the 6th of October, the 5th, the payment
        // of June, the 16th of January.
        Assert.Equal(4, everything.TotalCount);
        Assert.Equal([_summerNight, _summerEvening, _payment, _winterNight], everything.Items.Select(i => i.Id));
        var receipt = everything.Items[2];
        Assert.Equal(ArchiveDocumentType.TouristTax, receipt.Type);
        Assert.Null(receipt.PropertyId);
        Assert.Equal((long?)1234, receipt.SizeBytes);
        Assert.Empty(byBooking.Items);
        Assert.Equal(3, byProperty.TotalCount);
    }

    [PostgresFact]
    public async Task CountByType_GroupsTheSameUnion()
    {
        var counts = await Service().CountByTypeAsync(
            _hosts.OrgWide, DocumentArchiveWorld.Everything, Query(from: new DateOnly(2026, 6, 1), types: [ArchiveDocumentType.Contract]));

        Assert.Equal(
            [(ArchiveDocumentType.TouristTax, 1), (ArchiveDocumentType.Contract, 0), (ArchiveDocumentType.PropertyDocument, 2)],
            counts.Select(c => (c.Type, c.Count)));
    }

    [PostgresFact]
    public async Task ACollaboratorSoloAlcuni_SeesOnlyItsProperties_AndNoReceipt_OnPostgreSql()
    {
        var otherProperty = new PropertyDocument
        {
            PropertyId = _hosts.Hidden.Id,
            OrgId = _hosts.OrgId,
            FileName = "nascosto.pdf",
            StorageUrl = StorageKeys.PropertyDocument(_hosts.Hidden.Id, "nascosto.pdf"),
            DocumentType = DocumentType.Other,
            UploadedBy = "auth0|host",
            UploadedAt = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc),
        };
        _db.PropertyDocuments.Add(otherProperty);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var page = await Service().ListAsync(_hosts.Restricted, DocumentArchiveWorld.Everything, Query());

        Assert.Equal(3, page.TotalCount);
        Assert.DoesNotContain(page.Items, i => i.Id == otherProperty.Id);
        Assert.DoesNotContain(page.Items, i => i.Type == ArchiveDocumentType.TouristTax);
    }
}
