using System.Net;
using System.Net.Http.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Email.Templates;
using Casazen.Infrastructure.External;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Email;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Stripe;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-15b on real PostgreSQL: what the claim of the event id (<c>ProcessedStripeEvents</c>), the advisory lock
/// <c>ServiceRequestPayment</c> (key: the request id) and the unique indexes of the refunds are there for. The same event delivered
/// by several workers at once, or two events about one PaymentIntent, record the payment once and send one receipt; the webhook and
/// the sync job racing each other pay once; two refunds of the whole amount at once create one refund on Stripe; a refund racing the
/// <c>charge.refunded</c> event records one refund; two runs of the reminders send one email. Every worker has its own scope (its own
/// connection), as the Hangfire jobs have; Stripe is the fake gateway. Locally these are skipped (no PostgreSQL): the CI runs them.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class ServicePaymentsWebhookPostgresTests(ServicePaymentsFactory factory) : IClassFixture<ServicePaymentsFactory>
{
    private const string SupplierAccount = "acct_supplier_pg_webhook";

    // ─── The payment: events, the sync job, the lock ───

    [PostgresFact]
    public async Task Event_TheSameEventInParallel_RecordsThePaymentOnce_AndSendsOneReceipt()
    {
        var opened = await OpenedAsync();
        factory.Gateway.SetStatus(opened.IntentId, "succeeded");
        var stripeEvent = PaymentIntentEvent(opened, "payment_intent.succeeded", $"evt_{Guid.NewGuid():N}");

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => DeliverAsync(stripeEvent)));

        Assert.Equal(ServicePaymentStatus.Paid, (await PaymentAsync(opened.PaymentId)).Status);
        Assert.Equal(ServiceRequestStatus.Pagato, (await factory.LoadRequestAsync(opened.RequestId)).Status);
        Assert.Equal(1, await CountProcessedAsync(stripeEvent.Id));
        Assert.Single(ReceiptsOf(opened));
    }

    [PostgresFact]
    public async Task Event_TwoDifferentEventsAboutOnePaymentIntent_RecordThePaymentOnce_AndSendOneReceipt()
    {
        var opened = await OpenedAsync();
        factory.Gateway.SetStatus(opened.IntentId, "succeeded");
        var first = PaymentIntentEvent(opened, "payment_intent.succeeded", $"evt_{Guid.NewGuid():N}");
        var second = PaymentIntentEvent(opened, "payment_intent.succeeded", $"evt_{Guid.NewGuid():N}");

        await Task.WhenAll(DeliverAsync(first), DeliverAsync(second));

        var payment = await PaymentAsync(opened.PaymentId);
        Assert.Equal(ServicePaymentStatus.Paid, payment.Status);
        Assert.NotNull(payment.PaidAt);
        Assert.Equal(ServicePaymentChannel.Stripe, payment.PaidVia);
        // Both events were processed (each id is claimed), but only the first to take the lock paid: one receipt.
        Assert.Equal(1, await CountProcessedAsync(first.Id));
        Assert.Equal(1, await CountProcessedAsync(second.Id));
        Assert.Single(ReceiptsOf(opened));
    }

    [PostgresFact]
    public async Task Event_ParallelWithTheSyncJob_RecordsThePaymentOnce_AndSendsOneReceipt()
    {
        var opened = await OpenedAsync();
        // The debit is in flight: the payment is Processing, and the job will find the PaymentIntent succeeded.
        await DeliverAsync(PaymentIntentEvent(opened, "payment_intent.processing", $"evt_{Guid.NewGuid():N}"));
        Assert.Equal(ServicePaymentStatus.Processing, (await PaymentAsync(opened.PaymentId)).Status);
        factory.Gateway.SetStatus(opened.IntentId, "succeeded");
        var stripeEvent = PaymentIntentEvent(opened, "payment_intent.succeeded", $"evt_{Guid.NewGuid():N}");

        await Task.WhenAll(DeliverAsync(stripeEvent), SynchronizeAsync(), SynchronizeAsync());

        Assert.Equal(ServicePaymentStatus.Paid, (await PaymentAsync(opened.PaymentId)).Status);
        Assert.Single(ReceiptsOf(opened));
    }

    [PostgresFact]
    public async Task Event_WaitsForThePaymentLockOfItsRequest_AndTheEventOfAnotherRequestDoesNot()
    {
        var held = await OpenedAsync();
        var free = await OpenedAsync();
        factory.Gateway.SetStatus(held.IntentId, "succeeded");
        factory.Gateway.SetStatus(free.IntentId, "succeeded");

        await using var holder = await HoldTheLockAsync(held.RequestId);
        var blocked = Task.Run(() => DeliverAsync(PaymentIntentEvent(held, "payment_intent.succeeded", $"evt_{Guid.NewGuid():N}")));
        await WaitForAdvisoryLockWaitersAsync(1);
        await DeliverAsync(PaymentIntentEvent(free, "payment_intent.succeeded", $"evt_{Guid.NewGuid():N}"));

        Assert.Equal(ServicePaymentStatus.Paid, (await PaymentAsync(free.PaymentId)).Status);
        Assert.False(blocked.IsCompleted);
        Assert.Equal(ServicePaymentStatus.Requested, (await PaymentAsync(held.PaymentId)).Status);

        await holder.ReleaseAsync();
        await blocked;
        Assert.Equal(ServicePaymentStatus.Paid, (await PaymentAsync(held.PaymentId)).Status);
    }

    // ─── The refunds ───

    [PostgresFact]
    public async Task Refund_TwoParallelRefundsOfTheWholeAmount_CreateOneRefundOnStripe()
    {
        var paid = await PaidAsync();
        using var first = Admin();
        using var second = Admin();

        var responses = await Task.WhenAll(
            first.PostAsJsonAsync(RefundUrl(paid.PaymentId), new { }),
            second.PostAsJsonAsync(RefundUrl(paid.PaymentId), new { }));

        // One reserved everything; the other found nothing left to refund (422): never a second refund, never a 500.
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity }, responses.Select(r => r.StatusCode).Order().ToArray());
        Assert.Single(factory.Gateway.RefundRequests, r => r.PaymentIntentId == paid.IntentId);
        var payment = await PaymentAsync(paid.PaymentId);
        Assert.Equal(ServicePaymentStatus.Refunded, payment.Status);
        Assert.Equal(6_000, payment.RefundedCents);
        Assert.Single(await RefundsOfAsync(paid.PaymentId));
    }

    [PostgresFact]
    public async Task Refund_TwoParallelRefundsThatTogetherAreMoreThanWasPaid_RefundNoMoreThanWasPaid()
    {
        var paid = await PaidAsync();
        using var first = Admin();
        using var second = Admin();

        var responses = await Task.WhenAll(
            first.PostAsJsonAsync(RefundUrl(paid.PaymentId), new { amountCents = 4_000 }),
            second.PostAsJsonAsync(RefundUrl(paid.PaymentId), new { amountCents = 4_000 }));

        // The first reserves 4000 euro cents; the second asks for more than the 2000 left.
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity }, responses.Select(r => r.StatusCode).Order().ToArray());
        var rejected = responses.Single(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
        Assert.Equal("service_payment_refund_amount_exceeds", (await rejected.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString());
        var payment = await PaymentAsync(paid.PaymentId);
        Assert.Equal(4_000, payment.RefundedCents);
        Assert.Equal(ServicePaymentStatus.PartiallyRefunded, payment.Status);
        Assert.Single(factory.Gateway.RefundRequests, r => r.PaymentIntentId == paid.IntentId);
    }

    [PostgresFact]
    public async Task Refund_ParallelWithTheRefundEvent_RecordsOneRefund()
    {
        var paid = await PaidAsync();
        using var admin = Admin();

        // Both wait for the lock of the payment; whoever takes it first, the refund of Stripe is recorded once.
        await using var holder = await HoldTheLockAsync(paid.RequestId);
        var refund = Task.Run(() => admin.PostAsJsonAsync(RefundUrl(paid.PaymentId), new { amountCents = 2_000 }));
        await WaitForAdvisoryLockWaitersAsync(1);
        var refundedEvent = Task.Run(() => DeliverAsync(new Event
        {
            Id = $"evt_{Guid.NewGuid():N}",
            Type = "charge.refunded",
            Account = SupplierAccount,
            Created = DateTime.UtcNow,
            Data = new EventData { Object = new Charge { Id = $"ch_{Guid.NewGuid():N}", PaymentIntentId = paid.IntentId } },
        }));
        await WaitForAdvisoryLockWaitersAsync(2);
        await holder.ReleaseAsync();

        var response = await refund;
        await refundedEvent;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refunds = await RefundsOfAsync(paid.PaymentId);
        var recorded = Assert.Single(refunds);
        Assert.Equal(ServicePaymentRefundStatus.Succeeded, recorded.Status);
        Assert.Equal(2_000, recorded.AmountCents);
        Assert.NotNull(recorded.StripeRefundId);
        Assert.Single(factory.Gateway.RefundRequests, r => r.PaymentIntentId == paid.IntentId);
        Assert.Equal(2_000, (await PaymentAsync(paid.PaymentId)).RefundedCents);
    }

    [PostgresFact]
    public async Task Refund_MadeOnTheStripeDashboard_IsRecordedOnce_WhenTheEventArrivesTwiceInParallel()
    {
        var paid = await PaidAsync();
        factory.Gateway.AddExternalRefund(paid.IntentId, 1_500);
        var stripeEvent = new Event
        {
            Id = $"evt_{Guid.NewGuid():N}",
            Type = "charge.refunded",
            Account = SupplierAccount,
            Created = DateTime.UtcNow,
            Data = new EventData { Object = new Charge { Id = $"ch_{Guid.NewGuid():N}", PaymentIntentId = paid.IntentId } },
        };
        var other = new Event
        {
            Id = $"evt_{Guid.NewGuid():N}",
            Type = "charge.refunded",
            Account = SupplierAccount,
            Created = DateTime.UtcNow,
            Data = new EventData { Object = new Charge { Id = $"ch_{Guid.NewGuid():N}", PaymentIntentId = paid.IntentId } },
        };

        await Task.WhenAll(DeliverAsync(stripeEvent), DeliverAsync(stripeEvent), DeliverAsync(other));

        var refunds = await RefundsOfAsync(paid.PaymentId);
        var recorded = Assert.Single(refunds);
        Assert.Equal(ServicePaymentRefundOrigin.Stripe, recorded.Origin);
        Assert.Equal(1_500, recorded.AmountCents);
        var payment = await PaymentAsync(paid.PaymentId);
        Assert.Equal(1_500, payment.RefundedCents);
        Assert.Equal(ServicePaymentStatus.PartiallyRefunded, payment.Status);
    }

    // ─── The reminders ───

    [PostgresFact]
    public async Task Reminders_TwoParallelRuns_SendOneReminder()
    {
        var opened = await OpenedAsync();
        // Asked three days ago, one email so far: the first reminder is due.
        await ChangePaymentAsync(opened.PaymentId, p =>
        {
            p.RequestedAt = DateTime.UtcNow.AddDays(-3);
            p.LastSentAt = DateTime.UtcNow.AddDays(-3);
            p.SentCount = 1;
        });
        var before = RemindersOf(opened.PaymentId);

        await Task.WhenAll(RunRemindersAsync(), RunRemindersAsync());

        Assert.Equal(before + 1, RemindersOf(opened.PaymentId));
        Assert.Equal(2, (await PaymentAsync(opened.PaymentId)).SentCount);
    }

    [PostgresFact]
    public async Task Reminders_ParallelWithTheSupplierAskingAgain_SendOneEmail()
    {
        var opened = await OpenedAsync();
        await ChangePaymentAsync(opened.PaymentId, p =>
        {
            p.RequestedAt = DateTime.UtcNow.AddDays(-3);
            p.LastSentAt = DateTime.UtcNow.AddDays(-3);
            p.SentCount = 1;
        });
        var before = RemindersOf(opened.PaymentId);
        using var supplier = factory.Supplier(opened.World);

        var job = RunRemindersAsync();
        var ask = supplier.PostAsync($"/api/supplier/requests/{opened.RequestId}/payment-request", content: null);
        await Task.WhenAll(job, ask);

        // The job and the supplier both want to send: the payer gets one email, the other finds the day taken.
        Assert.Equal(before + 1, RemindersOf(opened.PaymentId));
        Assert.Contains((await ask).StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity });
    }

    // ─── Helpers ───

    private sealed record Opened(ServiceRequestWorld World, Guid RequestId, Guid PaymentId, string IntentId, string SupplierEmail);

    private HttpClient Admin() => factory.CreateAuthenticatedClient($"auth0|sp15b-admin-{Guid.NewGuid():N}", roles: "Admin");

    private static string RefundUrl(Guid paymentId) => $"/api/admin/service-payments/{paymentId}/refund";

    private async Task<ServiceRequestWorld> ReadyWorldAsync()
    {
        var world = await ServiceRequestWorlds.SeedAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var org = await db.Orgs.SingleAsync(o => o.Id == world.SupplierOrgId);
        org.StripeConnectedAccountId = SupplierAccount;
        org.ConnectChargesEnabled = true;
        org.ConnectPayoutsEnabled = true;
        await db.SaveChangesAsync();
        return world;
    }

    /// <summary>A request completed inside CasaZen whose payer opened the session: the payment has its PaymentIntent on the fake Stripe.</summary>
    private async Task<Opened> OpenedAsync()
    {
        var world = await ReadyWorldAsync();
        var id = await factory.CreateRequestAsync(world);
        using (var supplier = factory.Supplier(world))
        {
            (await supplier.PostAsync($"/api/service-requests/{id}/take", content: null)).EnsureSuccessStatusCode();
            (await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { finalAmountCents = 6_000 })).EnsureSuccessStatusCode();
        }

        Guid paymentId;
        string supplierEmail;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            paymentId = (await db.ServiceRequestPayments.AsNoTracking().SingleAsync(p => p.ServiceRequestId == id)).Id;
            supplierEmail = await db.SupplierProfiles.AsNoTracking().Where(sp => sp.OrgId == world.SupplierOrgId).Select(sp => sp.Email).SingleAsync();
        }

        var token = factory.Emails.RequestEmails()
            .Select(e => ServicePaymentTestSupport.LinkOf(e.Content))
            .Single(link => link.PaymentId == paymentId)
            .Token;
        using var anonymous = factory.CreateClient();
        (await anonymous.PostAsJsonAsync($"/api/public/service-payments/{paymentId}/payment-session", new { token })).EnsureSuccessStatusCode();

        var intentId = (await PaymentAsync(paymentId)).StripePaymentIntentId!;
        return new Opened(world, id, paymentId, intentId, supplierEmail);
    }

    /// <summary>A payment that was paid: the PaymentIntent succeeded and the event was delivered.</summary>
    private async Task<Opened> PaidAsync()
    {
        var opened = await OpenedAsync();
        factory.Gateway.SetStatus(opened.IntentId, "succeeded");
        await DeliverAsync(PaymentIntentEvent(opened, "payment_intent.succeeded", $"evt_{Guid.NewGuid():N}"));
        Assert.Equal(ServicePaymentStatus.Paid, (await PaymentAsync(opened.PaymentId)).Status);
        return opened;
    }

    /// <summary>The event Stripe sends about the PaymentIntent of the payment, on the Connect endpoint.</summary>
    private Event PaymentIntentEvent(Opened opened, string type, string eventId)
    {
        var intent = factory.Gateway.Intent(opened.IntentId);
        return new Event
        {
            Id = eventId,
            Type = type,
            Account = SupplierAccount,
            Created = DateTime.UtcNow,
            Data = new EventData
            {
                Object = new PaymentIntent
                {
                    Id = intent.Id,
                    Amount = intent.AmountCents,
                    AmountReceived = intent.AmountReceivedCents ?? intent.AmountCents,
                    Currency = intent.Currency,
                    ApplicationFeeAmount = intent.ApplicationFeeCents,
                    Metadata = new Dictionary<string, string>
                    {
                        ["kind"] = ServiceCharges.Kind,
                        [ServiceCharges.PaymentMetadataKey] = opened.PaymentId.ToString(),
                    },
                },
            },
        };
    }

    /// <summary>One delivery of an event: a worker of its own, with its own scope and connection, as a Hangfire job has.</summary>
    private async Task DeliverAsync(Event stripeEvent)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<StripeWebhookHandler>().HandleEventAsync(stripeEvent, WebhookSource.Connected);
    }

    private async Task SynchronizeAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISupplierPaymentJobService>().SynchronizeAsync();
    }

    private async Task RunRemindersAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ISupplierPaymentJobService>().RunRemindersAsync();
    }

    private List<(string? To, Casazen.Infrastructure.Email.EmailContent Content, string Template)> ReceiptsOf(Opened opened) =>
        factory.Emails.Snapshot()
            .Where(e => e.Template == EmailTemplates.Names.ServicePaymentReceived && e.To == opened.SupplierEmail)
            .ToList();

    private int RemindersOf(Guid paymentId) =>
        factory.Emails.ReminderEmails().Count(e => ServicePaymentTestSupport.LinkOf(e.Content).PaymentId == paymentId);

    private async Task<int> CountProcessedAsync(string eventId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ProcessedStripeEvents.CountAsync(e => e.EventId == eventId);
    }

    private async Task<ServiceRequestPayment> PaymentAsync(Guid paymentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequestPayments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
    }

    private async Task ChangePaymentAsync(Guid paymentId, Action<ServiceRequestPayment> change)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var payment = await db.ServiceRequestPayments.SingleAsync(p => p.Id == paymentId);
        change(payment);
        await db.SaveChangesAsync();
    }

    private async Task<List<ServiceRequestPaymentRefund>> RefundsOfAsync(Guid paymentId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequestPaymentRefunds.AsNoTracking().Where(r => r.ServiceRequestPaymentId == paymentId).OrderBy(r => r.Sequence).ToListAsync();
    }

    private async Task<string> ConnectionStringAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString()!;
    }

    /// <summary>Another connection holding the payment lock of a request, as a webhook or a refund that is writing its payment would.</summary>
    private async Task<LockHolder> HoldTheLockAsync(Guid requestId)
    {
        var connection = new NpgsqlConnection(await ConnectionStringAsync());
        await connection.OpenAsync();
        var transaction = await connection.BeginTransactionAsync();
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@scope, @key)", connection, transaction))
        {
            command.Parameters.AddWithValue("scope", (int)PostgresAdvisoryLocks.Scope.ServiceRequestPayment);
            command.Parameters.AddWithValue("key", PostgresAdvisoryLocks.Hash(requestId.ToString("N")));
            await command.ExecuteNonQueryAsync();
        }

        return new LockHolder(connection, transaction);
    }

    private async Task WaitForAdvisoryLockWaitersAsync(int count)
    {
        await using var connection = new NpgsqlConnection(await ConnectionStringAsync());
        await connection.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks l JOIN pg_database d ON d.oid = l.database " +
                "WHERE l.locktype = @kind AND NOT l.granted AND d.datname = current_database()",
                connection);
            command.Parameters.AddWithValue("kind", "advisory");
            if ((long)(await command.ExecuteScalarAsync())! >= count)
                return;

            await Task.Delay(25);
        }

        throw new TimeoutException($"Fewer than {count} sessions waited on the payment lock of the request.");
    }

    private sealed class LockHolder(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        private bool _released;

        public async Task ReleaseAsync()
        {
            if (_released)
                return;

            _released = true;
            await transaction.CommitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await ReleaseAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
