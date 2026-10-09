using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Storage;
using Casazen.Tests.Unit.Email;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The service request graph of SP-04 and of the booking from the public showcase (SP-10) on one in-memory database: the real
/// <see cref="ServiceRequestService"/> with the real agenda (the slot planner, which counts the holds), catalog and photo storage (the
/// filesystem, under a folder of its own that <see cref="Dispose"/> removes), the real <see cref="ShowcaseBookingService"/>, and
/// the notifiers recording what they would queue. The unit tests of the service, of the lifecycle, of the batch, of the photos, of
/// the auto-cancel job and of the booking all build their service here, so they run the same code the API does.
/// </summary>
internal sealed class ServiceRequestTestKit : IDisposable
{
    /// <summary>The version of the privacy notice the kit's booking service asks for.</summary>
    public const string PrivacyNoticeVersion = "2026-11-test";

    private readonly string _storageRoot = Path.Combine(Path.GetTempPath(), "casazen-test-service-requests", Guid.NewGuid().ToString("N"));

    public ServiceRequestTestKit(
        AppDbContext db,
        IEmailQueue? emails = null,
        IPushNotificationService? push = null,
        TimeProvider? clock = null,
        ServiceRequestOptions? options = null,
        string? publicSiteBaseUrl = EmailTestHelpers.PublicSiteBaseUrl,
        ShowcaseBookingOptions? showcaseOptions = null)
    {
        Db = db;
        Clock = clock ?? TimeProvider.System;
        Options = options ?? new ServiceRequestOptions();
        ShowcaseOptions = showcaseOptions ?? new ShowcaseBookingOptions { PrivacyNoticeVersion = PrivacyNoticeVersion };
        Emails = emails ?? new RecordingEmailQueue();
        Push = push ?? Mock.Of<IPushNotificationService>();

        var storageOptions = Microsoft.Extensions.Options.Options.Create(new StorageOptions
        {
            Provider = StorageOptions.FileSystemProvider,
            PublicBaseUrl = "https://storage.test/public",
            FileSystem = new FileSystemStorageOptions { RootPath = _storageRoot },
        });
        Storage = new FileSystemFileStorage(storageOptions, NullLogger<FileSystemFileStorage>.Instance);
        Images = new ImageStorageService(Storage, storageOptions, NullLogger<ImageStorageService>.Instance);

        Reader = new SupplierServiceRequestReader(db, Clock);
        Holds = new ShowcaseHoldReader(db);
        Agenda = new SupplierAgendaService(db, Reader, Holds, NullLogger<SupplierAgendaService>.Instance, Clock);
        Catalog = new SupplierServiceCatalogService(db, Storage, Images, NullLogger<SupplierServiceCatalogService>.Instance, Clock);
        Customers = new ServiceCustomerReader(db);
        CustomerIndex = new ServiceCustomerIndex(
            Microsoft.Extensions.Options.Options.Create(new ServiceCustomerIndexOptions()),
            Mock.Of<IHostEnvironment>(environment => environment.EnvironmentName == "Testing"),
            NullLogger<ServiceCustomerIndex>.Instance);
        var links = EmailTestHelpers.Links(publicSiteBaseUrl);
        ShowcaseNotifier = new ShowcaseBookingNotifier(db, Customers, Emails, links, Push, NullLogger<ShowcaseBookingNotifier>.Instance);
        Notifier = new ServiceRequestNotifier(
            db,
            Emails,
            links,
            Push,
            ShowcaseNotifier,
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<ServiceRequestNotifier>.Instance);
        Matcher = ComuneTestServices.Matcher(db);
        Service = new ServiceRequestService(
            db,
            new ServiceRequestRepository(db),
            Notifier,
            Matcher,
            LegalTestServices.Legal(),
            Catalog,
            Agenda,
            Storage,
            Images,
            Microsoft.Extensions.Options.Options.Create(Options),
            Microsoft.Extensions.Options.Options.Create(ShowcaseOptions),
            NullLogger<ServiceRequestService>.Instance,
            Clock);
        Booking = new ShowcaseBookingService(
            db,
            Catalog,
            Agenda,
            Matcher,
            CustomerIndex,
            ShowcaseNotifier,
            Microsoft.Extensions.Options.Options.Create(ShowcaseOptions),
            NullLogger<ShowcaseBookingService>.Instance,
            Clock);
    }

    public AppDbContext Db { get; }

    public TimeProvider Clock { get; }

    public ServiceRequestOptions Options { get; }

    public ShowcaseBookingOptions ShowcaseOptions { get; }

    public IEmailQueue Emails { get; }

    public IPushNotificationService Push { get; }

    public FileSystemFileStorage Storage { get; }

    public ImageStorageService Images { get; }

    public SupplierServiceRequestReader Reader { get; }

    public ShowcaseHoldReader Holds { get; }

    public SupplierAgendaService Agenda { get; }

    public SupplierServiceCatalogService Catalog { get; }

    public ServiceCustomerReader Customers { get; }

    public ServiceCustomerIndex CustomerIndex { get; }

    public ISupplierComuneMatcher Matcher { get; }

    public ShowcaseBookingNotifier ShowcaseNotifier { get; }

    public ServiceRequestNotifier Notifier { get; }

    public ServiceRequestService Service { get; }

    public ShowcaseBookingService Booking { get; }

    public void Dispose()
    {
        if (Directory.Exists(_storageRoot))
            Directory.Delete(_storageRoot, recursive: true);
    }
}
