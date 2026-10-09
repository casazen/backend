using System.Collections.Concurrent;
using Casazen.Core.Features;
using Casazen.Core.Services;
using Casazen.Infrastructure.Push;
using Casazen.Infrastructure.Services;
using Casazen.Web.BackgroundJobs;
using Casazen.Web.Extensions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Push;

/// <summary>
/// UI-12a: every push also queues an in-app notification, without the callers of <see cref="IPushNotificationService"/>
/// changing; with the flag off the decorator is a pass-through. The real wiring (<c>AddCasazenPush</c>) is proved here too.
/// </summary>
public class InAppNotificationPushDecoratorTests
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid BookingId = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");
    private static readonly Guid RequestId = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    private sealed class Rig
    {
        private readonly ConcurrentQueue<Job> _jobs = new();

        public Rig(bool flagOn, bool pushAnswer = true)
        {
            Inner.Setup(i => i.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.IsAny<PushNotificationPayload>()))
                .Returns(pushAnswer);
            Client
                .Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
                .Callback<Job, IState>((job, _) => _jobs.Enqueue(job))
                .Returns("job-1");
            Flags.Setup(f => f.IsEnabled(FeatureFlags.InAppNotifications)).Returns(flagOn);
            Decorator = new InAppNotificationPushDecorator(
                Inner.Object, Client.Object, Flags.Object, Clock, NullLogger<InAppNotificationPushDecorator>.Instance);
        }

        public Mock<IPushNotificationService> Inner { get; } = new(MockBehavior.Strict);

        public Mock<IBackgroundJobClient> Client { get; } = new();

        public Mock<IFeatureFlags> Flags { get; } = new();

        public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(Now));

        public InAppNotificationPushDecorator Decorator { get; }

        public IReadOnlyList<Job> Jobs => _jobs.ToList();
    }

    private static PushNotificationPayload Payload(
        string type = PushTypes.NewBooking,
        Guid? bookingId = null,
        Guid? serviceRequestId = null,
        string title = "Nuova prenotazione",
        string body = "Villa Test, dal 12 al 15 ottobre, 2 ospiti") =>
        new(title, body, type, bookingId, PushRoutes.Booking(BookingId), serviceRequestId);

    // ─── Flag off: a pass-through ───

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Enqueue_FlagOff_OnlyForwardsThePush_AndAnswersWhatTheQueueAnswers(bool pushAnswer)
    {
        var rig = new Rig(flagOn: false, pushAnswer);
        var payload = Payload(bookingId: BookingId);
        var audience = PushAudience.BookingHosts(BookingId);

        var answer = rig.Decorator.Enqueue("booking:x:new", audience, payload);

        Assert.Equal(pushAnswer, answer);
        rig.Inner.Verify(i => i.Enqueue("booking:x:new", audience, payload), Times.Once);
        Assert.Empty(rig.Jobs);
        rig.Client.VerifyNoOtherCalls();
    }

    // ─── Flag on ───

    [Fact]
    public void Enqueue_FlagOn_ForwardsThePushAndQueuesTheNotificationWithTheSameKeyAndAudience()
    {
        var rig = new Rig(flagOn: true);
        var payload = Payload(bookingId: BookingId);
        var audience = PushAudience.BookingHosts(BookingId);

        var answer = rig.Decorator.Enqueue("booking:x:new", audience, payload);

        Assert.True(answer);
        rig.Inner.Verify(i => i.Enqueue("booking:x:new", audience, payload), Times.Once);
        var job = Assert.Single(rig.Jobs);
        Assert.Equal(typeof(InAppNotificationJob), job.Type);
        Assert.Equal(nameof(InAppNotificationJob.CreateAsync), job.Method.Name);
        Assert.Equal("booking:x:new", job.Args[0]);
        var queued = Assert.IsType<QueuedInAppNotification>(job.Args[1]);
        Assert.Equal(PushAudienceKind.BookingHosts, queued.AudienceKind);
        Assert.Equal(BookingId, queued.AudienceId);
        Assert.Equal(PushTypes.NewBooking, queued.Type);
        Assert.Equal(BookingId, queued.EntityId);
        Assert.Equal(Now, queued.OccurredAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Enqueue_FlagOn_TheNotificationDoesNotDependOnThePushBeingQueued(bool pushAnswer)
    {
        // A push refused for its route, or a queue that failed, leaves the bell alone; and the answer stays the push's.
        var rig = new Rig(flagOn: true, pushAnswer);

        var answer = rig.Decorator.Enqueue("booking:x:new", PushAudience.BookingHosts(BookingId), Payload(bookingId: BookingId));

        Assert.Equal(pushAnswer, answer);
        Assert.Single(rig.Jobs);
    }

    [Theory]
    [InlineData(PushTypes.NewBooking, true, false, true)]
    [InlineData(PushTypes.ServiceRequestCompleted, true, true, false)]
    [InlineData(PushTypes.ServiceRequestCreated, false, true, false)]
    [InlineData(PushTypes.OtaStayReview, false, false, null)]
    public void Enqueue_FlagOn_TheEntityIsTheServiceRequestOfAServiceRequestTypeAndTheBookingOtherwise(
        string type, bool withBooking, bool withRequest, bool? expectsBooking)
    {
        var rig = new Rig(flagOn: true);
        var payload = Payload(type, withBooking ? BookingId : null, withRequest ? RequestId : null);

        rig.Decorator.Enqueue("key", PushAudience.PropertyHosts(Guid.NewGuid()), payload);

        var queued = Assert.IsType<QueuedInAppNotification>(Assert.Single(rig.Jobs).Args[1]);
        Guid? expected = expectsBooking switch
        {
            true => BookingId,
            false => RequestId,
            null => null,
        };
        Assert.Equal(expected, queued.EntityId);
    }

    [Fact]
    public void Enqueue_FlagOn_NothingOfThePushTextCrossesIntoTheJob()
    {
        var rig = new Rig(flagOn: true);

        rig.Decorator.Enqueue(
            "booking:x:new",
            PushAudience.BookingHosts(BookingId),
            Payload(bookingId: BookingId, title: "Mario Rossi ha prenotato", body: "Villa Segreta, 2 ospiti"));

        var arguments = InvocationData.SerializeJob(Assert.Single(rig.Jobs)).Arguments;
        Assert.DoesNotContain("Mario", arguments);
        Assert.DoesNotContain("Rossi", arguments);
        Assert.DoesNotContain("Villa Segreta", arguments);
        Assert.DoesNotContain("ospiti", arguments);
        Assert.DoesNotContain(PushRoutes.Booking(BookingId), arguments);
    }

    [Fact]
    public void Enqueue_FlagOn_TheArgumentsSurviveTheStorageOfHangfire()
    {
        var rig = new Rig(flagOn: true);
        rig.Decorator.Enqueue(
            "service-request:7c9e:Completato",
            PushAudience.SupplierOrg(BookingId),
            Payload(PushTypes.ServiceRequestCompleted, null, RequestId));

        var stored = InvocationData.SerializeJob(Assert.Single(rig.Jobs)).DeserializeJob();

        Assert.Equal("service-request:7c9e:Completato", stored.Args[0]);
        var queued = Assert.IsType<QueuedInAppNotification>(stored.Args[1]);
        Assert.Equal(PushAudienceKind.SupplierOrg, queued.AudienceKind);
        Assert.Equal(BookingId, queued.AudienceId);
        Assert.Equal(PushTypes.ServiceRequestCompleted, queued.Type);
        Assert.Equal(RequestId, queued.EntityId);
        Assert.Equal(Now, queued.OccurredAt);
        Assert.Equal(DateTimeKind.Utc, queued.OccurredAt.Kind);
    }

    [Fact]
    public void Enqueue_FlagOn_TheJobClientFails_NothingIsThrown_AndThePushAnswerStands()
    {
        var rig = new Rig(flagOn: true);
        rig.Client.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Throws(new InvalidOperationException("storage down"));

        var answer = rig.Decorator.Enqueue("booking:x:new", PushAudience.BookingHosts(BookingId), Payload(bookingId: BookingId));

        Assert.True(answer);
        rig.Inner.Verify(i => i.Enqueue(It.IsAny<string>(), It.IsAny<PushAudience>(), It.IsAny<PushNotificationPayload>()), Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Enqueue_FlagOn_ABlankKey_QueuesNoNotification_AndDoesNotThrow(string key)
    {
        var rig = new Rig(flagOn: true);

        rig.Decorator.Enqueue(key, PushAudience.BookingHosts(BookingId), Payload(bookingId: BookingId));

        Assert.Empty(rig.Jobs);
    }

    [Fact]
    public void Enqueue_FlagOn_AKeyOrATypeLongerThanTheColumns_QueuesNoNotification_AndDoesNotThrow()
    {
        var rig = new Rig(flagOn: true);

        rig.Decorator.Enqueue(new string('k', 201), PushAudience.BookingHosts(BookingId), Payload(bookingId: BookingId));
        rig.Decorator.Enqueue("booking:x:new", PushAudience.BookingHosts(BookingId), Payload(type: new string('t', 65), bookingId: BookingId));
        rig.Decorator.Enqueue("booking:x:new", PushAudience.BookingHosts(BookingId), Payload(type: " ", bookingId: BookingId));

        Assert.Empty(rig.Jobs);
    }

    [Fact]
    public void Enqueue_FlagOn_EveryTypeOfPushIsAType_NoneIsTooLongForTheColumn()
    {
        // The types are written as they are: one that did not fit would be silently skipped by the decorator.
        var types = typeof(PushTypes).GetFields()
            .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) })
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        Assert.NotEmpty(types);
        Assert.All(types, type => Assert.InRange(type.Length, 1, Casazen.Core.Entities.InAppNotification.TypeMaxLength));
    }

    // ─── The wiring of the application ───

    private static ServiceProvider Wire(bool flagOn, out ConcurrentQueue<Job> jobs)
    {
        var created = new ConcurrentQueue<Job>();
        jobs = created;
        var client = new Mock<IBackgroundJobClient>();
        client
            .Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((job, _) => created.Enqueue(job))
            .Returns("job-1");
        var flags = new Mock<IFeatureFlags>();
        flags.Setup(f => f.IsEnabled(FeatureFlags.InAppNotifications)).Returns(flagOn);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(client.Object);
        services.AddSingleton(flags.Object);
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(Now)));
        services.AddCasazenPush(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddCasazenPush_TheServicesAskForThePushQueueAndGetItWithTheNotificationDecorator()
    {
        using var provider = Wire(flagOn: true, out var jobs);
        using var scope = provider.CreateScope();

        var push = scope.ServiceProvider.GetRequiredService<IPushNotificationService>();
        var queued = push.Enqueue("booking:x:new", PushAudience.BookingHosts(BookingId), Payload(bookingId: BookingId));

        Assert.IsType<InAppNotificationPushDecorator>(push);
        Assert.True(queued);
        // One job for the phones, one for the bell: the callers asked for one thing and did not change.
        Assert.Equal(
            [typeof(PushDeliveryJob), typeof(InAppNotificationJob)],
            jobs.Select(j => j.Type));
        Assert.Equal("booking:x:new", jobs.Select(j => j.Args[0]).Distinct().Single());
    }

    [Fact]
    public void AddCasazenPush_FlagOff_TheOnlyJobIsTheOneOfThePhones()
    {
        using var provider = Wire(flagOn: false, out var jobs);
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IPushNotificationService>()
            .Enqueue("booking:x:new", PushAudience.BookingHosts(BookingId), Payload(bookingId: BookingId));

        Assert.Equal([typeof(PushDeliveryJob)], jobs.Select(j => j.Type));
    }

    [Fact]
    public void AddCasazenPush_RegistersTheServiceAndTheJobsOfTheBell()
    {
        var services = new ServiceCollection();

        services.AddCasazenPush(new ConfigurationBuilder().Build());

        Assert.Contains(services, d => d.ServiceType == typeof(InAppNotificationJob));
        Assert.Contains(services, d => d.ServiceType == typeof(InAppNotificationRetentionJob));
        var notifications = Assert.Single(services, d => d.ServiceType == typeof(IInAppNotificationService));
        Assert.Equal(typeof(InAppNotificationService), notifications.ImplementationType);
        Assert.Equal(ServiceLifetime.Scoped, notifications.Lifetime);
        // The one registration of IPushNotificationService is the decorated queue, never the bare queue.
        var push = Assert.Single(services, d => d.ServiceType == typeof(IPushNotificationService));
        Assert.Null(push.ImplementationType);
        Assert.NotNull(push.ImplementationFactory);
    }
}
