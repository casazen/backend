using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Storage;
using Casazen.Tests.Unit.Email;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The service request graph of SP-04 on one in-memory database: the real <see cref="ServiceRequestService"/> with the real
/// agenda (the slot planner), catalog and photo storage (the filesystem, under a folder of its own that <see cref="Dispose"/>
/// removes), and the notifier recording what it would queue. The unit tests of the service, of the lifecycle, of the batch, of
/// the photos and of the auto-cancel job all build their service here, so they run the same code the API does.
/// </summary>
internal sealed class ServiceRequestTestKit : IDisposable
{
    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), "casazen-test-service-requests", Guid.NewGuid().ToString("N"));

    public ServiceRequestTestKit(
        AppDbContext db,
        IEmailQueue? emails = null,
        IPushNotificationService? push = null,
        TimeProvider? clock = null,
        ServiceRequestOptions? options = null,
        string? publicSiteBaseUrl = EmailTestHelpers.PublicSiteBaseUrl)
    {
        Db = db;
        Clock = clock ?? TimeProvider.System;
        Options = options ?? new ServiceRequestOptions();

        var storageOptions = Microsoft.Extensions.Options.Options.Create(new StorageOptions
        {
            Provider = StorageOptions.FileSystemProvider,
            PublicBaseUrl = "https://storage.test/public",
            FileSystem = new FileSystemStorageOptions { RootPath = _storageRoot },
        });
        Storage = new FileSystemFileStorage(storageOptions, NullLogger<FileSystemFileStorage>.Instance);
        Images = new ImageStorageService(Storage, storageOptions, NullLogger<ImageStorageService>.Instance);

        Reader = new SupplierServiceRequestReader(db, Clock);
        Agenda = new SupplierAgendaService(db, Reader, NullLogger<SupplierAgendaService>.Instance, Clock);
        Catalog = new SupplierServiceCatalogService(db, Storage, Images, NullLogger<SupplierServiceCatalogService>.Instance, Clock);
        Notifier = new ServiceRequestNotifier(
            db,
            emails ?? new RecordingEmailQueue(),
            EmailTestHelpers.Links(publicSiteBaseUrl),
            push ?? Mock.Of<IPushNotificationService>(),
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<ServiceRequestNotifier>.Instance);
        Service = new ServiceRequestService(
            db,
            new ServiceRequestRepository(db),
            Notifier,
            ComuneTestServices.Matcher(db),
            LegalTestServices.Legal(),
            Catalog,
            Agenda,
            Storage,
            Images,
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<ServiceRequestService>.Instance,
            Clock);
    }

    public AppDbContext Db { get; }

    public TimeProvider Clock { get; }

    public ServiceRequestOptions Options { get; }

    public FileSystemFileStorage Storage { get; }

    public ImageStorageService Images { get; }

    public SupplierServiceRequestReader Reader { get; }

    public SupplierAgendaService Agenda { get; }

    public SupplierServiceCatalogService Catalog { get; }

    public ServiceRequestNotifier Notifier { get; }

    public ServiceRequestService Service { get; }

    public void Dispose()
    {
        if (Directory.Exists(_storageRoot))
            Directory.Delete(_storageRoot, recursive: true);
    }
}
