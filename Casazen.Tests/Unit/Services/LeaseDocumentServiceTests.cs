using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Services;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

public sealed class LeaseDocumentServiceTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private readonly Mock<IFileStorage> _storage = new();
    private readonly Dictionary<string, byte[]> _objects = [];
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\noriginal signed document\n%%EOF");
    private LeaseDocumentService Service => new(_db, _storage.Object, NullLogger<LeaseDocumentService>.Instance);

    public LeaseDocumentServiceTests()
    {
        // Initialize Hangfire with an in-memory storage for unit tests
        // The LeaseDocumentService enqueues extraction jobs using BackgroundJob.Enqueue
        GlobalConfiguration.Configuration.UseInMemoryStorage();

        _storage.Setup(s => s.PutAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<Stream>(), "application/pdf", It.IsAny<CancellationToken>()))
            .Callback<StorageBucket, string, Stream, string, CancellationToken>((_, key, stream, _, _) =>
            { using var copy = new MemoryStream(); stream.CopyTo(copy); _objects[key] = copy.ToArray(); }).Returns(Task.CompletedTask);
        _storage.Setup(s => s.OpenReadAsync(StorageBucket.Private, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((StorageBucket _, string key, CancellationToken _) => _objects.TryGetValue(key, out var bytes) ? new MemoryStream(bytes) : null);
    }

    [Fact]
    public async Task Upload_PersistsOnLease_NotProperty_AndDownloadsOriginalBytes()
    {
        var lease = await Seed();
        var saved = await Service.UploadAsync(lease.Id, File(Pdf), LeaseDocumentKind.Attestation, "owner");
        _db.ChangeTracker.Clear();
        var row = await _db.LeaseDocuments.SingleAsync();
        Assert.Equal(lease.Id, row.LeaseContractId);
        Assert.Equal(lease.OrgId, row.OrgId);
        Assert.StartsWith($"orgs/{lease.OrgId}/leases/{lease.Id}/documents/", row.StorageKey);
        Assert.Empty(await _db.PropertyDocuments.ToListAsync());
        var listed = await Service.ListAsync(lease.Id);
        Assert.Equal(saved.Id, Assert.Single(listed).Id);
        var download = await Service.DownloadAsync(lease.Id, saved.Id);
        Assert.NotNull(download);
        using var bytes = new MemoryStream();
        await download.Content.CopyToAsync(bytes);
        await download.Content.DisposeAsync();
        Assert.Equal(Pdf, bytes.ToArray());
    }

    [Fact]
    public async Task Documents_CannotBeListedOrDownloadedUnderAnotherLease()
    {
        var first = await Seed(); var second = await Seed();
        var saved = await Service.UploadAsync(first.Id, File(Pdf), LeaseDocumentKind.Contract, "owner");
        Assert.Empty(await Service.ListAsync(second.Id));
        Assert.Null(await Service.DownloadAsync(second.Id, saved.Id));
        _storage.Verify(s => s.OpenReadAsync(It.IsAny<StorageBucket>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("<html>not a PDF</html>")]
    [InlineData("")]
    public async Task InvalidPdf_NeverReachesStorageOrDatabase(string value)
    {
        var lease = await Seed();
        await Assert.ThrowsAsync<DomainRuleException>(() => Service.UploadAsync(lease.Id, File(Encoding.ASCII.GetBytes(value)), LeaseDocumentKind.Contract, "owner"));
        Assert.Empty(await _db.LeaseDocuments.ToListAsync()); Assert.Empty(_objects);
    }

    [Fact]
    public async Task FilenameTraversal_IsRemoved_AndUploadDoesNotMarkLeaseSignedOrRegistered()
    {
        var lease = await Seed();
        var file = File(Pdf, "../../receipt.pdf");
        var saved = await Service.UploadAsync(lease.Id, file, LeaseDocumentKind.Registration, "owner");
        Assert.Equal("receipt.pdf", saved.FileName);
        Assert.Equal(Casazen.Core.Entities.Enums.LeaseStatus.Draft, (await _db.LeaseContracts.SingleAsync(l => l.Id == lease.Id)).Status);
        Assert.Empty(await _db.LeaseRegistrations.ToListAsync());
    }

    [Fact]
    public async Task OversizedUpload_IsRejectedBeforeReadingOrStoring()
    {
        var lease = await Seed();
        var file = new FormFile(new MemoryStream(Pdf), 0, LeaseDocumentService.MaxBytes + 1, "file", "large.pdf");
        await Assert.ThrowsAsync<DomainRuleException>(() => Service.UploadAsync(lease.Id, file, LeaseDocumentKind.Contract, "owner"));
        Assert.Empty(_objects);
    }

    private async Task<LeaseContract> Seed()
    {
        var property = new Property { Id = Guid.NewGuid(), OrgId = Guid.NewGuid(), OwnerId = "owner", Name = "House" };
        var lease = new LeaseContract
        {
            Property = property,
            PropertyId = property.Id,
            OrgId = property.OrgId,
            MonthlyRent = 1000,
            StartDate = DateTime.UtcNow,
            EndDate = DateTime.UtcNow.AddYears(4)
        };
        _db.Properties.Add(property); _db.LeaseContracts.Add(lease); await _db.SaveChangesAsync(); return lease;
    }
    private static FormFile File(byte[] bytes, string name = "contract.pdf") => new(new MemoryStream(bytes), 0, bytes.Length, "file", name) { Headers = new HeaderDictionary(), ContentType = "application/pdf" };
    public void Dispose() => _db.Dispose();
}
