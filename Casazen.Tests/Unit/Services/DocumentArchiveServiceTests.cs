using Casazen.Core.Authorization;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// NF-99, E07/F03/US03/T01 and T02: the document archive over the three sources, run on the InMemory provider (the PostgreSQL translation and the
/// time zone of the day are proved by <c>DocumentArchiveSqlTests</c> and <c>DocumentArchivePostgresTests</c>). The InMemory database has no tenant
/// context, so what keeps the orgs and the properties apart here is the predicates of the sources themselves.
/// </summary>
public class DocumentArchiveServiceTests
{
    private static DocumentArchiveQuery All { get; } = new(null, null, null, null, null, null, 1, DocumentArchiveQuery.MaxPageSize);

    private static DocumentArchiveQuery With(
        ArchiveDocumentType[]? types = null,
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? propertyId = null,
        string? search = null,
        int page = 1,
        int pageSize = DocumentArchiveQuery.MaxPageSize) =>
        new(types, from, to, propertyId, null, search, page, pageSize);

    /// <summary>
    /// PROOF TEST: data rule (tenant and scope). The rows of another org never appear, whoever asks; a scope limited to some properties sees the
    /// documents of those properties only; and the tourist tax receipts, which belong to the org and to no property, only to a scope that reaches the
    /// whole org. The InMemory context here has no tenant filter: the explicit org predicate of each source is what is tested.
    /// </summary>
    [Fact]
    public async Task DocumentArchive_TenantAndScopeIsolation_Test()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        var service = world.Service();

        var wholeOrg = await service.ListAsync(world.Hosts.OrgWide, DocumentArchiveWorld.Everything, All);
        var limited = await service.ListAsync(world.Hosts.Restricted, DocumentArchiveWorld.Everything, All);
        var otherOrg = await service.ListAsync(new HostScope(world.Hosts.OtherOrgId), DocumentArchiveWorld.Everything, All);

        // The whole org: both properties' documents, both leases' files and the one receipt; nothing of the other org, no payment without a receipt.
        Assert.Equal(5, wholeOrg.TotalCount);
        Assert.Equal(
            new[]
            {
                world.GrantedPropertyDocument, world.HiddenPropertyDocument, world.GrantedLeaseDocument, world.HiddenLeaseDocument, world.TaxReceipt,
            }.Order(),
            wholeOrg.Items.Select(i => i.Id).Order());
        Assert.DoesNotContain(wholeOrg.Items, i => i.Id == world.TaxWithoutReceipt);

        // A collaborator «Solo alcuni»: the property it was given, its lease file, and no payment (it has no property).
        Assert.Equal(
            new[] { world.GrantedPropertyDocument, world.GrantedLeaseDocument }.Order(),
            limited.Items.Select(i => i.Id).Order());
        Assert.Equal(2, limited.TotalCount);

        // The other org sees its own three and none of the first org's.
        Assert.Equal(
            new[] { world.OtherOrgPropertyDocument, world.OtherOrgLeaseDocument, world.OtherOrgTaxReceipt }.Order(),
            otherOrg.Items.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task List_RowsCarryTheirPropertyTheirDayTheirSizeAndTheirContentType()
    {
        var world = await DocumentArchiveWorld.CreateAsync();

        var page = await world.Service().ListAsync(world.Hosts.OrgWide, DocumentArchiveWorld.Everything, All);

        var property = Assert.Single(page.Items, i => i.Id == world.GrantedPropertyDocument);
        Assert.Equal(ArchiveDocumentType.PropertyDocument, property.Type);
        Assert.Equal("APE-Trullo.pdf", property.Title);
        Assert.Equal(new DateOnly(2026, 10, 5), property.DocumentDate);
        Assert.Equal(world.Hosts.Granted.Id, property.PropertyId);
        Assert.Equal("Trullo", property.PropertyName);
        Assert.Null(property.BookingId);
        Assert.Null(property.SizeBytes);
        // The property document stores no MIME type: the service gives the one of its extension.
        Assert.Equal("application/pdf", property.ContentType);

        var lease = Assert.Single(page.Items, i => i.Id == world.GrantedLeaseDocument);
        Assert.Equal(ArchiveDocumentType.Contract, lease.Type);
        Assert.Equal(world.Hosts.Granted.Id, lease.PropertyId);
        Assert.Equal("application/pdf", lease.ContentType);
        Assert.Equal((long?)DocumentArchiveWorld.Pdf("contratto-trullo.pdf").Length, lease.SizeBytes);

        var tax = Assert.Single(page.Items, i => i.Id == world.TaxReceipt);
        Assert.Equal(ArchiveDocumentType.TouristTax, tax.Type);
        Assert.Equal("Ostuni", tax.Title);
        Assert.Equal(DocumentArchiveWorld.TaxReceiptDay, tax.DocumentDate);
        Assert.Null(tax.PropertyId);
        Assert.Equal("application/pdf", tax.ContentType);
    }

    [Fact]
    public async Task List_IsNewestDayFirst_AndPagedWithTheTotalOfTheWholeList()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        var service = world.Service();

        var first = await service.ListAsync(world.Hosts.OrgWide, DocumentArchiveWorld.Everything, With(pageSize: 2));
        var second = await service.ListAsync(world.Hosts.OrgWide, DocumentArchiveWorld.Everything, With(pageSize: 2, page: 2));
        var last = await service.ListAsync(world.Hosts.OrgWide, DocumentArchiveWorld.Everything, With(pageSize: 2, page: 3));
        var beyond = await service.ListAsync(world.Hosts.OrgWide, DocumentArchiveWorld.Everything, With(pageSize: 2, page: 4));

        Assert.All(new[] { first, second, last, beyond }, p => Assert.Equal(5, p.TotalCount));
        Assert.Equal([world.HiddenPropertyDocument, world.GrantedPropertyDocument], first.Items.Select(i => i.Id));
        Assert.Equal([world.HiddenLeaseDocument, world.GrantedLeaseDocument], second.Items.Select(i => i.Id));
        Assert.Equal([world.TaxReceipt], last.Items.Select(i => i.Id));
        Assert.Empty(beyond.Items);
        Assert.Equal(2, first.PageSize);
    }

    [Fact]
    public async Task List_FiltersByTypeDaysAndProperty()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        var service = world.Service();
        var scope = world.Hosts.OrgWide;

        var contracts = await service.ListAsync(scope, DocumentArchiveWorld.Everything, With([ArchiveDocumentType.Contract]));
        var inSeptember = await service.ListAsync(
            scope, DocumentArchiveWorld.Everything, With(from: new DateOnly(2026, 9, 1), to: new DateOnly(2026, 9, 30)));
        var oneDay = await service.ListAsync(
            scope, DocumentArchiveWorld.Everything, With(from: new DateOnly(2026, 10, 5), to: new DateOnly(2026, 10, 5)));
        var afterTheFifth = await service.ListAsync(scope, DocumentArchiveWorld.Everything, With(from: new DateOnly(2026, 10, 6)));
        var ofTheTrullo = await service.ListAsync(scope, DocumentArchiveWorld.Everything, With(propertyId: world.Hosts.Granted.Id));

        Assert.Equal([world.HiddenLeaseDocument, world.GrantedLeaseDocument], contracts.Items.Select(i => i.Id));
        Assert.Equal([world.HiddenLeaseDocument, world.GrantedLeaseDocument], inSeptember.Items.Select(i => i.Id));
        Assert.Equal([world.GrantedPropertyDocument], oneDay.Items.Select(i => i.Id));
        Assert.Equal([world.HiddenPropertyDocument], afterTheFifth.Items.Select(i => i.Id));
        // A property filter keeps the rows of that property and drops the payment, which has none.
        Assert.Equal(
            new[] { world.GrantedPropertyDocument, world.GrantedLeaseDocument }.Order(),
            ofTheTrullo.Items.Select(i => i.Id).Order());
    }

    [Fact]
    public async Task List_FiltersByAPropertyOutsideTheScope_FindsNothing_NotTheRowsOfThatProperty()
    {
        var world = await DocumentArchiveWorld.CreateAsync();

        var hidden = await world.Service().ListAsync(
            world.Hosts.Restricted, DocumentArchiveWorld.Everything, With(propertyId: world.Hosts.Hidden.Id));

        Assert.Empty(hidden.Items);
        Assert.Equal(0, hidden.TotalCount);
    }

    [Fact]
    public async Task List_SearchesTheTitleThePropertyNameAndTheCommune_WhateverTheCase()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        var service = world.Service();
        var scope = world.Hosts.OrgWide;

        var byTitle = await service.ListAsync(scope, DocumentArchiveWorld.Everything, With(search: "licenza"));
        var byProperty = await service.ListAsync(scope, DocumentArchiveWorld.Everything, With(search: "  CASA BIANCA "));
        var byCommune = await service.ListAsync(scope, DocumentArchiveWorld.Everything, With(search: "ostuni"));
        var nothing = await service.ListAsync(scope, DocumentArchiveWorld.Everything, With(search: "non-esiste"));

        Assert.Equal([world.HiddenPropertyDocument], byTitle.Items.Select(i => i.Id));
        Assert.Equal(
            new[] { world.HiddenPropertyDocument, world.HiddenLeaseDocument }.Order(),
            byProperty.Items.Select(i => i.Id).Order());
        Assert.Equal([world.TaxReceipt], byCommune.Items.Select(i => i.Id));
        Assert.Empty(nothing.Items);
    }

    [Fact]
    public async Task List_AnEmptyListOfTypes_SelectsNothing_NeverEverything()
    {
        var world = await DocumentArchiveWorld.CreateAsync();

        var none = await world.Service().ListAsync(world.Hosts.OrgWide, DocumentArchiveWorld.Everything, With(types: []));

        Assert.Empty(none.Items);
        Assert.Equal(0, none.TotalCount);
    }

    [Fact]
    public async Task List_ATypeTheCallerMayNotRead_IsNotInTheList_EvenWhenAskedFor()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        var accountant = new DocumentArchiveAccess(
            new HashSet<ArchiveDocumentType> { ArchiveDocumentType.PropertyDocument, ArchiveDocumentType.Contract });

        var all = await world.Service().ListAsync(world.Hosts.OrgWide, accountant, All);
        var asked = await world.Service().ListAsync(world.Hosts.OrgWide, accountant, With([ArchiveDocumentType.TouristTax]));
        var nobody = await world.Service().ListAsync(world.Hosts.OrgWide, DocumentArchiveAccess.None, All);

        Assert.DoesNotContain(all.Items, i => i.Type == ArchiveDocumentType.TouristTax);
        Assert.Equal(4, all.TotalCount);
        Assert.Empty(asked.Items);
        Assert.Empty(nobody.Items);
        Assert.Equal(0, nobody.TotalCount);
    }

    [Fact]
    public async Task List_ThePageSizeIsKeptBetweenOneAndAHundred_AndThePageFromOne()
    {
        var world = await DocumentArchiveWorld.CreateAsync();

        var tooBig = await world.Service().ListAsync(world.Hosts.OrgWide, DocumentArchiveWorld.Everything, With(pageSize: 5000, page: 0));
        var tooSmall = await world.Service().ListAsync(world.Hosts.OrgWide, DocumentArchiveWorld.Everything, With(pageSize: -3));

        Assert.Equal(DocumentArchiveQuery.MaxPageSize, tooBig.PageSize);
        Assert.Equal(1, tooBig.Page);
        Assert.Equal(1, tooSmall.PageSize);
        Assert.Single(tooSmall.Items);
    }

    [Fact]
    public async Task CountByType_GivesEveryTypeTheCallerMayRead_WithItsCount_AndIgnoresTheTypeFilter()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        var service = world.Service();

        var all = await service.CountByTypeAsync(world.Hosts.OrgWide, DocumentArchiveWorld.Everything, All);
        var limited = await service.CountByTypeAsync(world.Hosts.Restricted, DocumentArchiveWorld.Everything, With([ArchiveDocumentType.Contract]));
        var inSeptember = await service.CountByTypeAsync(
            world.Hosts.OrgWide, DocumentArchiveWorld.Everything, With(from: new DateOnly(2026, 9, 1), to: new DateOnly(2026, 9, 30)));
        var noContracts = await service.CountByTypeAsync(
            world.Hosts.OrgWide,
            new DocumentArchiveAccess(new HashSet<ArchiveDocumentType> { ArchiveDocumentType.PropertyDocument }),
            All);

        Assert.Equal(
            [(ArchiveDocumentType.TouristTax, 1), (ArchiveDocumentType.Contract, 2), (ArchiveDocumentType.PropertyDocument, 2)],
            all.Select(c => (c.Type, c.Count)));
        // The type filter is the one thing a count leaves out; the scope still applies, and the payments are not in a limited scope.
        Assert.Equal(
            [(ArchiveDocumentType.TouristTax, 0), (ArchiveDocumentType.Contract, 1), (ArchiveDocumentType.PropertyDocument, 1)],
            limited.Select(c => (c.Type, c.Count)));
        // A type with no document in the days is still offered, with zero.
        Assert.Equal(
            [(ArchiveDocumentType.TouristTax, 0), (ArchiveDocumentType.Contract, 2), (ArchiveDocumentType.PropertyDocument, 0)],
            inSeptember.Select(c => (c.Type, c.Count)));
        Assert.Equal([(ArchiveDocumentType.PropertyDocument, 2)], noContracts.Select(c => (c.Type, c.Count)));
    }

    // ---------------------------------------------------------------- the file of one document

    /// <summary>
    /// PROOF TEST: data rule. A document of another org answers 404 like one that does not exist, and so does one of a property outside the scope of
    /// the caller: the caller cannot tell them apart, and nothing is opened from the storage.
    /// </summary>
    [Fact]
    public async Task DocumentArchiveDownload_OtherOrgRow_NotFound_Test()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        var service = world.Service();

        foreach (var (type, id) in new[]
                 {
                     (ArchiveDocumentType.PropertyDocument, world.OtherOrgPropertyDocument),
                     (ArchiveDocumentType.Contract, world.OtherOrgLeaseDocument),
                     (ArchiveDocumentType.TouristTax, world.OtherOrgTaxReceipt),
                     (ArchiveDocumentType.PropertyDocument, Guid.NewGuid()),
                 })
        {
            var missing = await Assert.ThrowsAsync<NotFoundException>(
                () => service.OpenAsync(world.Hosts.OrgWide, DocumentArchiveWorld.Everything, type, id));
            Assert.Equal(DocumentArchiveService.DocumentNotFoundCode, missing.Code);
        }

        // A property of the org that the collaborator was not given: the same 404, and the payment of the org (no property) as well.
        foreach (var (type, id) in new[]
                 {
                     (ArchiveDocumentType.PropertyDocument, world.HiddenPropertyDocument),
                     (ArchiveDocumentType.Contract, world.HiddenLeaseDocument),
                     (ArchiveDocumentType.TouristTax, world.TaxReceipt),
                 })
        {
            var hidden = await Assert.ThrowsAsync<NotFoundException>(
                () => service.OpenAsync(world.Hosts.Restricted, DocumentArchiveWorld.Everything, type, id));
            Assert.Equal(DocumentArchiveService.DocumentNotFoundCode, hidden.Code);
        }
    }

    [Fact]
    public async Task Download_ATypeTheCallerMayNotRead_IsForbidden_EvenForARowThatExists()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        var propertiesOnly = new DocumentArchiveAccess(new HashSet<ArchiveDocumentType> { ArchiveDocumentType.PropertyDocument });

        var forbidden = await Assert.ThrowsAsync<DomainForbiddenException>(
            () => world.Service().OpenAsync(world.Hosts.OrgWide, propertiesOnly, ArchiveDocumentType.TouristTax, world.TaxReceipt));
        // A type with no source registered (a compliance proof, for now) has no row: a 404 even if the access names it.
        var noSource = await Assert.ThrowsAsync<NotFoundException>(
            () => world.Service().OpenAsync(world.Hosts.OrgWide, new DocumentArchiveAccess(new HashSet<ArchiveDocumentType> { ArchiveDocumentType.AlloggiatiReceipt }),
                ArchiveDocumentType.AlloggiatiReceipt, Guid.NewGuid()));

        Assert.Equal(DocumentArchiveService.TypeForbiddenCode, forbidden.Code);
        Assert.Equal(DocumentArchiveService.DocumentNotFoundCode, noSource.Code);
    }

    [Fact]
    public async Task Download_OpensTheOriginalFile_WithItsNameAndItsContentType()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        var service = world.Service();

        var property = await service.OpenAsync(
            world.Hosts.OrgWide, DocumentArchiveWorld.Everything, ArchiveDocumentType.PropertyDocument, world.GrantedPropertyDocument);
        var lease = await service.OpenAsync(
            world.Hosts.Restricted, DocumentArchiveWorld.Everything, ArchiveDocumentType.Contract, world.GrantedLeaseDocument);
        var tax = await service.OpenAsync(
            world.Hosts.OrgWide, DocumentArchiveWorld.Everything, ArchiveDocumentType.TouristTax, world.TaxReceipt);

        await using (property.Content)
        {
            Assert.Equal("APE-Trullo.pdf", property.FileName);
            Assert.Equal("application/pdf", property.ContentType);
            Assert.Equal(DocumentArchiveWorld.Pdf("APE-Trullo.pdf"), await ReadAllAsync(property.Content));
        }

        await using (lease.Content)
        {
            Assert.Equal("contratto-trullo.pdf", lease.FileName);
            Assert.Equal(DocumentArchiveWorld.Pdf("contratto-trullo.pdf"), await ReadAllAsync(lease.Content));
        }

        await using (tax.Content)
        {
            // The name the host sent, as the payment stores it.
            Assert.Equal("ricevuta-ostuni.pdf", tax.FileName);
            Assert.Equal("application/pdf", tax.ContentType);
            Assert.Equal(DocumentArchiveWorld.Pdf("ricevuta Ostuni"), await ReadAllAsync(tax.Content));
        }
    }

    [Fact]
    public async Task Download_ARowWithoutAFile_IsNotFoundWithTheCodeOfItsOwn()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        // The row is in the list, but the file is gone from the bucket.
        Assert.True(world.Storage.Remove(world.KeysByDocument[world.GrantedPropertyDocument]));

        var gone = await Assert.ThrowsAsync<NotFoundException>(
            () => world.Service().OpenAsync(
                world.Hosts.OrgWide, DocumentArchiveWorld.Everything, ArchiveDocumentType.PropertyDocument, world.GrantedPropertyDocument));

        Assert.Equal(DocumentArchiveService.DocumentHasNoFileCode, gone.Code);
    }

    [Fact]
    public async Task ThePathsOfADocument_ThatAreNotKeysOfTheBucket_AreNeverOpened()
    {
        var world = await DocumentArchiveWorld.CreateAsync();
        var document = await world.Db.PropertyDocuments.FindAsync(world.GrantedPropertyDocument);
        Assert.NotNull(document);
        // A legacy path (a local folder) or a key that climbs out of its folder is not a key of the private bucket.
        document.StorageUrl = "../../secrets/appsettings.json";
        await world.Db.SaveChangesAsync();
        world.Db.ChangeTracker.Clear();

        var refused = await Assert.ThrowsAsync<NotFoundException>(
            () => world.Service().OpenAsync(
                world.Hosts.OrgWide, DocumentArchiveWorld.Everything, ArchiveDocumentType.PropertyDocument, world.GrantedPropertyDocument));

        Assert.Equal(DocumentArchiveService.DocumentHasNoFileCode, refused.Code);
    }

    private static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy);
        return copy.ToArray();
    }
}
