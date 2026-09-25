using Casazen.Core.Repositories;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.External;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace Casazen.Tests.Integration;

/// <summary>
/// Builds the <see cref="CheckoutPaymentSettlementService"/> of a <c>StripeWebhookHandler</c> made by hand in tests, on
/// the same context and payment repository as the handler (BK-04).
/// </summary>
internal static class TestCheckoutPaymentSettlement
{
    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().AddInMemoryCollection().Build();

    public static CheckoutPaymentSettlementService Create(
        AppDbContext db,
        IPaymentRepository? paymentRepository = null,
        IStripeService? stripe = null,
        IEmailQueue? emails = null,
        IPaymentRefundRetryScheduler? retryScheduler = null,
        IConfiguration? configuration = null,
        TimeProvider? timeProvider = null)
    {
        var scheduler = retryScheduler ?? Mock.Of<IPaymentRefundRetryScheduler>();
        var emailQueue = emails ?? Mock.Of<IEmailQueue>();
        var refunds = new PaymentRefundService(
            db,
            stripe ?? Mock.Of<IStripeService>(),
            scheduler,
            emailQueue,
            NullLogger<PaymentRefundService>.Instance,
            timeProvider);
        return new CheckoutPaymentSettlementService(
            db,
            paymentRepository ?? new PaymentRepository(db),
            refunds,
            scheduler,
            new BookingNotifier(db, emailQueue, EmailTestHelpers.Links(), Mock.Of<IPushNotificationService>(), NullLogger<BookingNotifier>.Instance),
            configuration ?? EmptyConfiguration,
            NullLogger<CheckoutPaymentSettlementService>.Instance,
            timeProvider);
    }
}

/// <summary>
/// Builds the <see cref="DeferredChargeService"/> of a <c>StripeWebhookHandler</c> made by hand in tests, on the same
/// context as the handler (BK-08).
/// </summary>
internal static class TestDeferredCharges
{
    public const string PublicSiteBaseUrl = "https://casazen-app.test";

    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().AddInMemoryCollection().Build();

    public static DeferredChargeService Create(
        AppDbContext db,
        IStripeService? stripe = null,
        IEmailQueue? emails = null,
        IConfiguration? configuration = null,
        TimeProvider? timeProvider = null) =>
        new(
            db,
            stripe ?? Mock.Of<IStripeService>(),
            new BookingNotifier(
                db,
                emails ?? Mock.Of<IEmailQueue>(),
                new PublicSiteLinks(Options.Create(new PublicSiteOptions { PublicSiteBaseUrl = PublicSiteBaseUrl })),
                Mock.Of<IPushNotificationService>(),
                NullLogger<BookingNotifier>.Instance),
            configuration ?? EmptyConfiguration,
            NullLogger<DeferredChargeService>.Instance,
            timeProvider);
}
