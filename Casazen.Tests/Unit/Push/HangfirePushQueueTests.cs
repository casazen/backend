using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Push;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Push;

/// <summary>MO-04 (A6-29): a push is queued as a Hangfire job, never sent by the caller.</summary>
public class HangfirePushQueueTests
{
    private static readonly Guid BookingId = Guid.Parse("3f2504e0-4f89-41d3-9a0c-0305e82c3301");

    [Fact]
    public void Enqueue_Push_CreatesADeliveryJobWithTheKeyAudienceAndPayload()
    {
        var pipeline = new PushPipeline();
        var requestId = Guid.NewGuid();
        var key = PushDeliveryKeys.ServiceRequestStatus(requestId, ServiceRequestStatus.Rifiutato);

        var queued = pipeline.Queue.Enqueue(
            key,
            PushAudience.PropertyHosts(BookingId),
            new PushNotificationPayload("Titolo", "Testo", PushTypes.ServiceRequestRejected, BookingId, PushRoutes.Booking(BookingId), requestId));

        Assert.True(queued);
        var job = Assert.Single(pipeline.Jobs);
        Assert.Equal(typeof(PushDeliveryJob), job.Type);
        Assert.Equal(nameof(PushDeliveryJob.SendAsync), job.Method.Name);
        Assert.Equal(key, job.Args[0]);
        var push = Assert.IsType<QueuedPush>(job.Args[1]);
        Assert.Equal(PushAudienceKind.PropertyHosts, push.AudienceKind);
        Assert.Equal(BookingId, push.AudienceId);
        Assert.Equal("Titolo", push.Title);
        Assert.Equal(PushTypes.ServiceRequestRejected, push.Type);
        Assert.Equal(requestId, push.ServiceRequestId);
        Assert.Empty(pipeline.Expo.SendRequests);
    }

    [Fact]
    public void Enqueue_RouteNotInTheApp_ReturnsFalseWithoutQueuing()
    {
        var pipeline = new PushPipeline();

        var queued = pipeline.Queue.Enqueue(
            "key",
            PushAudience.BookingHosts(BookingId),
            new PushNotificationPayload("t", "b", PushTypes.NewBooking, BookingId, $"/service-requests/{BookingId}"));

        Assert.False(queued);
        Assert.Empty(pipeline.Jobs);
    }

    [Fact]
    public void Enqueue_JobStorageFails_ReturnsFalseWithoutThrowing()
    {
        var client = new Mock<IBackgroundJobClient>();
        client.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>())).Throws(new InvalidOperationException("storage down"));
        var queue = new HangfirePushQueue(client.Object, NullLogger<HangfirePushQueue>.Instance);

        var queued = queue.Enqueue(
            "key",
            PushAudience.BookingHosts(BookingId),
            new PushNotificationPayload("t", "b", PushTypes.NewBooking, BookingId, PushRoutes.Booking(BookingId)));

        Assert.False(queued);
    }

    [Fact]
    public void QueuedPush_HangfireSerialization_RoundTripsTheArguments()
    {
        var pipeline = new PushPipeline();
        var requestId = Guid.NewGuid();
        pipeline.Queue.Enqueue(
            "service-request:x:created",
            PushAudience.SupplierOrg(BookingId),
            new PushNotificationPayload("Nuova richiesta", "Pulizie presso Villa.", PushTypes.ServiceRequestCreated, null, PushRoutes.Properties, requestId));

        var stored = InvocationData.SerializeJob(Assert.Single(pipeline.Jobs)).DeserializeJob();

        Assert.Equal("service-request:x:created", stored.Args[0]);
        var push = Assert.IsType<QueuedPush>(stored.Args[1]);
        Assert.Equal(PushAudienceKind.SupplierOrg, push.AudienceKind);
        Assert.Equal(BookingId, push.AudienceId);
        Assert.Equal("Pulizie presso Villa.", push.Body);
        Assert.Null(push.BookingId);
        Assert.Equal(requestId, push.ServiceRequestId);
        Assert.Equal(PushRoutes.Properties, push.Route);
    }

    [Theory]
    [InlineData(ServiceRequestStatus.PresoInCarico, "service-request-taken")]
    [InlineData(ServiceRequestStatus.Completato, "service-request-completed")]
    [InlineData(ServiceRequestStatus.Rifiutato, "service-request-rejected")]
    public void ForServiceRequestStatus_Status_HasItsOwnType(ServiceRequestStatus status, string expected)
    {
        Assert.Equal(expected, PushTypes.ForServiceRequestStatus(status));
    }

    [Fact]
    public void StayAlertKey_DateOrReminderChanges_GivesAnotherKey()
    {
        var day = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var key = PushDeliveryKeys.StayAlert(BookingId, StayAlertKind.AlloggiatiOverdue, day, 1);

        Assert.Equal(key, PushDeliveryKeys.StayAlert(BookingId, StayAlertKind.AlloggiatiOverdue, day, 1));
        Assert.NotEqual(key, PushDeliveryKeys.StayAlert(BookingId, StayAlertKind.AlloggiatiOverdue, day, 2));
        Assert.NotEqual(key, PushDeliveryKeys.StayAlert(BookingId, StayAlertKind.AlloggiatiOverdue, day.AddDays(1), 1));
        Assert.True(key.Length <= 200);
    }
}
