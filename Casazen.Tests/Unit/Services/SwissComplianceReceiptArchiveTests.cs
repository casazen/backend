using System.Security.Cryptography;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Multitenancy;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public class SwissComplianceReceiptArchiveTests
{
    private static readonly DateTime ReportDate = new(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
    private static readonly byte[] Original = "%PDF-1.4 TEST receipt"u8.ToArray();

    [Fact]
    public async Task UploadKeepsEncryptedOriginalAndListsLegacyMetadataSeparately()
    {
        var options = Options();
        await using var db = new AppDbContext(options);
        var property = SeedProperty(db, Guid.NewGuid());
        var legacy = new SwissComplianceReceipt
        {
            OrgId = property.OrgId,
            PropertyId = property.Id,
            Type = "OTR",
            Reference = "legacy",
            ReportDate = ReportDate.AddDays(-1),
            UploadedFileHash = new string('0', 64),
        };
        db.SwissComplianceReceipts.Add(legacy);
        await db.SaveChangesAsync();
        var storage = new InMemoryFileStorage();
        var protection = new EphemeralDataProtectionProvider();
        var service = Service(db, storage, protection);

        using var input = new MemoryStream(Original);
        var row = await service.RecordAsync(property.Id, SwissComplianceFileType.Police, ReportDate, "TEST-only", input,
            "../notifica.pdf", "application/pdf");

        var key = StorageKeys.SwissComplianceReceipt(property.OrgId, property.Id, row.Id);
        await using var stored = Assert.IsAssignableFrom<Stream>(await storage.OpenReadAsync(StorageBucket.Private, key));
        using var content = new MemoryStream();
        await stored.CopyToAsync(content);
        Assert.NotEqual(Original, content.ToArray());
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Original)).ToLowerInvariant(), row.UploadedFileHash);

        await using var freshDb = new AppDbContext(options, new Tenant(property.OrgId));
        var archive = Service(freshDb, storage, protection);
        var page = await archive.ListAsync(property.Id, pageSize: 1);
        Assert.Equal(2, page.TotalCount);
        Assert.True(Assert.Single(page.Items).HasFile);
        Assert.False(Assert.Single((await archive.ListAsync(property.Id, page: 2, pageSize: 1)).Items).HasFile);
        await Assert.ThrowsAsync<NotFoundException>(() => archive.OpenFileAsync(property.Id, legacy.Id));

        var file = await archive.OpenFileAsync(property.Id, row.Id);
        await using var original = file.Content;
        using var output = new MemoryStream();
        await original.CopyToAsync(output);
        Assert.Equal(Original, output.ToArray());
        Assert.Equal("notifica.pdf", file.FileName);
        Assert.Equal("application/pdf", file.ContentType);
    }

    [Fact]
    public async Task DownloadRefusesAnotherPropertyAnotherTenantAndTamperedBytes()
    {
        var options = Options();
        await using var db = new AppDbContext(options);
        var property = SeedProperty(db, Guid.NewGuid());
        var sibling = SeedProperty(db, property.OrgId);
        await db.SaveChangesAsync();
        var storage = new InMemoryFileStorage();
        var protection = new EphemeralDataProtectionProvider();
        var service = Service(db, storage, protection);
        using var input = new MemoryStream(Original);
        var row = await service.RecordAsync(property.Id, SwissComplianceFileType.Police, ReportDate, "TEST-only", input);

        Assert.Empty((await service.ListAsync(sibling.Id)).Items);
        await Assert.ThrowsAsync<NotFoundException>(() => service.OpenFileAsync(sibling.Id, row.Id));

        await using var outsider = new AppDbContext(options, new Tenant(Guid.NewGuid()));
        var otherArchive = Service(outsider, storage, protection);
        await Assert.ThrowsAsync<NotFoundException>(() => otherArchive.ListAsync(property.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => otherArchive.OpenFileAsync(property.Id, row.Id));

        storage.Add(StorageKeys.SwissComplianceReceipt(property.OrgId, property.Id, row.Id), Original);
        var error = await Assert.ThrowsAsync<NotFoundException>(() => service.OpenFileAsync(property.Id, row.Id));
        Assert.Equal("swiss_receipt_file_not_found", error.Code);
    }

    [Fact]
    public async Task InvalidUploadAndFailedStorageDoNotLeaveMetadata()
    {
        await using var db = new AppDbContext(Options());
        var property = SeedProperty(db, Guid.NewGuid());
        await db.SaveChangesAsync();
        var storage = new Mock<IFileStorage>(MockBehavior.Strict);
        var service = Service(db, storage.Object, new EphemeralDataProtectionProvider());
        var invalid = await Assert.ThrowsAsync<DomainRuleException>(() => service.RecordAsync(property.Id,
            SwissComplianceFileType.Police, ReportDate, "TEST-only", Stream.Null));
        Assert.Equal("swiss_receipt_file_invalid", invalid.Code);
        storage.VerifyNoOtherCalls();

        storage.Setup(s => s.PutAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<Stream>(),
                "application/octet-stream", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Storage unavailable"));
        storage.Setup(s => s.DeleteAsync(StorageBucket.Private, It.IsAny<string>(), CancellationToken.None)).Returns(Task.CompletedTask);
        using var input = new MemoryStream(Original);
        await Assert.ThrowsAsync<IOException>(() => service.RecordAsync(property.Id, SwissComplianceFileType.Police, ReportDate, "TEST-only", input));
        await db.SaveChangesAsync();
        Assert.Empty(await db.SwissComplianceReceipts.ToListAsync());
        storage.Verify(s => s.DeleteAsync(StorageBucket.Private, It.IsAny<string>(), CancellationToken.None), Times.Once);
    }

    private static DbContextOptions<AppDbContext> Options() => new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;

    private static Property SeedProperty(AppDbContext db, Guid orgId)
    {
        var property = new Property { OrgId = orgId, Name = "TEST", Country = "CH", SwissMunicipalityCode = "5192" };
        db.Properties.Add(property);
        return property;
    }

    private static SwissComplianceReceiptService Service(AppDbContext db, IFileStorage storage, IDataProtectionProvider protection)
    {
        var ticino = new Mock<ITicinoNotificationService>();
        ticino.Setup(s => s.IsPropertyInTicinoAsync(It.IsAny<Guid>())).ReturnsAsync(true);
        return new SwissComplianceReceiptService(db, ticino.Object, NullLogger<SwissComplianceReceiptService>.Instance, storage, protection);
    }

    private sealed class Tenant(Guid id) : ITenantContext
    {
        public Guid? OrgId => id;
        public bool FilterEnabled => true;
    }
}
