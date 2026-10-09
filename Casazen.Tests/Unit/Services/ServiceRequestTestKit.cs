using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Storage;
using Casazen.Tests.Unit.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The service request graph of SP-04 on one in-memory database: the real <see cref="ServiceRequestService"/> with the real
/// agenda (the slot planner), catalog and photo storage (the filesystem, under a folder of its own that <see cref="Dispose"/>
/// removes), and the notifier recording what it would queue. The unit tests of the service, of the lifecycle, of the batch, of
/// the photos and of the auto-cancel job all build their service here, so they run the same code the API does. SP-15a adds the
/// real <see cref="SupplierPaymentService"/> over a <see cref="FakeSupplierPaymentGateway"/> (Stripe is never reached), with the
/// feature flag <see cref="Flags"/> off until a test switches it on and a platform commission of 10 %.
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
        string? publicSiteBaseUrl = EmailTestHelpers.PublicSiteBaseUrl,
        SupplierPaymentsOptions? paymentOptions = null,
        FakeSupplierPaymentGateway? gateway = null,
        TestFeatureFlags? flags = null,
        Microsoft.Extensions.Logging.ILogger<SupplierPaymentService>? paymentLogger = null)
    {
        Db = db;
        Clock = clock ?? TimeProvider.System;
        Options = options ?? new ServiceRequestOptions();
        PaymentOptions = paymentOptions ?? new SupplierPaymentsOptions { CommissionPercent = 10m };
        Gateway = gateway ?? new FakeSupplierPaymentGateway();
        Flags = flags ?? new TestFeatureFlags();
        var emailQueue = emails ?? new RecordingEmailQueue();

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
            emailQueue,
            EmailTestHelpers.Links(publicSiteBaseUrl),
            push ?? Mock.Of<IPushNotificationService>(),
            Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<ServiceRequestNotifier>.Instance);
        Payments = new SupplierPaymentService(
            db,
            Gateway,
            emailQueue,
            EmailTestHelpers.Links(publicSiteBaseUrl),
            Flags,
            Microsoft.Extensions.Options.Options.Create(PaymentOptions),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Stripe:PublishableKey"] = PublishableKey }).Build(),
            JobScheduler,
            paymentLogger ?? NullLogger<SupplierPaymentService>.Instance,
            Clock);
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
            Payments,
            NullLogger<ServiceRequestService>.Instance,
            Clock);
    }

    /// <summary>The publishable key the payment sessions of the kit carry.</summary>
    public const string PublishableKey = "pk_test_kit";

    public AppDbContext Db { get; }

    public TimeProvider Clock { get; }

    public ServiceRequestOptions Options { get; }

    public SupplierPaymentsOptions PaymentOptions { get; }

    public FakeSupplierPaymentGateway Gateway { get; }

    /// <summary>Records the supplier orgs whose pending payment requests were queued (SP-15b); nothing reaches Hangfire.</summary>
    public RecordingSupplierPaymentJobScheduler JobScheduler { get; } = new();

    public TestFeatureFlags Flags { get; }

    public FileSystemFileStorage Storage { get; }

    public ImageStorageService Images { get; }

    public SupplierServiceRequestReader Reader { get; }

    public SupplierAgendaService Agenda { get; }

    public SupplierServiceCatalogService Catalog { get; }

    public ServiceRequestNotifier Notifier { get; }

    public SupplierPaymentService Payments { get; }

    public ServiceRequestService Service { get; }

    public void Dispose()
    {
        if (Directory.Exists(_storageRoot))
            Directory.Delete(_storageRoot, recursive: true);
    }
}
