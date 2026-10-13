using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Infrastructure.Data;
using Casazen.Tests.Integration.Postgres;
using Casazen.Tests.Unit.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// SP-15a on real PostgreSQL: what the advisory lock <c>ServiceRequestPayment</c> (key: the request id) and the unique index "one
/// payment per request that is not canceled" are there for. Two sessions of the same payment create one PaymentIntent; a session
/// waits for the lock another connection holds, and one of another request does not; two asks for the payment send one email; a
/// session racing the supplier's offline record never leaves a PaymentIntent the payer could still pay; two confirmations of the
/// amount create one payment. The HTTP pipeline is the real one and Stripe is the fake gateway (it answers like Stripe, a retry
/// with the same idempotency key is the same PaymentIntent). Locally these are skipped (no PostgreSQL): the CI runs them.
/// </summary>
[Collection(SupplierCatalogHostsCollection.Name)]
public class ServicePaymentsPostgresTests(ServicePaymentsFactory factory) : IClassFixture<ServicePaymentsFactory>
{
    private const string SupplierAccount = "acct_supplier_pg";

    [PostgresFact]
    public async Task Session_TwoParallelRequestsOfTheSamePayment_CreateOnePaymentIntent()
    {
        var (_, requestId, paymentId, token) = await CompletedWithLinkAsync();
        factory.Gateway.CreateDelay = TimeSpan.FromMilliseconds(500);
        try
        {
            using var first = factory.CreateClient();
            using var second = factory.CreateClient();

            var responses = await Task.WhenAll(
                first.PostAsJsonAsync($"/api/public/service-payments/{paymentId}/payment-session", new { token }),
                second.PostAsJsonAsync($"/api/public/service-payments/{paymentId}/payment-session", new { token }));

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            var secrets = await Task.WhenAll(responses.Select(async r => (await Json(r)).GetProperty("clientSecret").GetString()));
            // One PaymentIntent for the two: the second request waited for the lock and found the one the first had made.
            Assert.Equal(secrets[0], secrets[1]);
            Assert.Single(factory.Gateway.Created, c => c.Metadata[ServiceCharges.PaymentMetadataKey] == paymentId.ToString());
            var payment = await PaymentAsync(paymentId);
            Assert.Equal(1, payment.PaymentIntentCount);
            Assert.NotNull(payment.StripePaymentIntentId);
            Assert.Equal(requestId, payment.ServiceRequestId);
        }
        finally
        {
            factory.Gateway.CreateDelay = TimeSpan.Zero;
        }
    }

    [PostgresFact]
    public async Task Session_ParallelFromTheLinkAndFromTheConsole_CreateOnePaymentIntent()
    {
        var (world, requestId, paymentId, token) = await CompletedWithLinkAsync();
        factory.Gateway.CreateDelay = TimeSpan.FromMilliseconds(500);
        try
        {
            using var anonymous = factory.CreateClient();
            using var host = factory.Host(world);

            var responses = await Task.WhenAll(
                anonymous.PostAsJsonAsync($"/api/public/service-payments/{paymentId}/payment-session", new { token }),
                host.PostAsync($"/api/service-requests/{requestId}/payment-session", content: null));

            Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
            Assert.Single(factory.Gateway.Created, c => c.Metadata[ServiceCharges.PaymentMetadataKey] == paymentId.ToString());
        }
        finally
        {
            factory.Gateway.CreateDelay = TimeSpan.Zero;
        }
    }

    [PostgresFact]
    public async Task Session_WaitsForTheLockOfItsRequest_AndTheSessionOfAnotherRequestDoesNot()
    {
        var (_, requestId, paymentId, token) = await CompletedWithLinkAsync();
        var (_, _, otherPaymentId, otherToken) = await CompletedWithLinkAsync();
        using var anonymous = factory.CreateClient();

        await using var holder = await HoldTheLockAsync(requestId);
        var blocked = Task.Run(() => anonymous.PostAsJsonAsync($"/api/public/service-payments/{paymentId}/payment-session", new { token }));
        await WaitForAdvisoryLockWaitersAsync(1);
        var free = await anonymous.PostAsJsonAsync($"/api/public/service-payments/{otherPaymentId}/payment-session", new { token = otherToken });

        // The other request is not held up by it, and the held one has not reached Stripe yet.
        Assert.Equal(HttpStatusCode.OK, free.StatusCode);
        Assert.False(blocked.IsCompleted);
        Assert.DoesNotContain(factory.Gateway.Created, c => c.Metadata[ServiceCharges.PaymentMetadataKey] == paymentId.ToString());

        await holder.ReleaseAsync();
        var released = await blocked;
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        Assert.Single(factory.Gateway.Created, c => c.Metadata[ServiceCharges.PaymentMetadataKey] == paymentId.ToString());
    }

    [PostgresFact]
    public async Task PaymentRequest_TwoParallelAsks_SendOneEmail_AndTheOtherIsTheDailyLimit()
    {
        var (world, requestId, paymentId, _) = await CompletedWithLinkAsync();
        await ChangePaymentAsync(paymentId, p => p.LastSentAt = DateTime.UtcNow.AddHours(-25));
        var remindersBefore = factory.Emails.ReminderEmails().Count(e => ServicePaymentTestSupport.LinkOf(e.Content).PaymentId == paymentId);

        using var first = factory.Supplier(world);
        using var second = factory.Supplier(world);
        var responses = await Task.WhenAll(
            first.PostAsync($"/api/supplier/requests/{requestId}/payment-request", content: null),
            second.PostAsync($"/api/supplier/requests/{requestId}/payment-request", content: null));

        var statuses = responses.Select(r => r.StatusCode).Order().ToList();
        Assert.Equal(new[] { HttpStatusCode.OK, HttpStatusCode.UnprocessableEntity }, statuses);
        var rejected = responses.Single(r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
        Assert.Equal("service_payment_request_too_soon", (await Json(rejected)).GetProperty("code").GetString());
        var remindersAfter = factory.Emails.ReminderEmails().Count(e => ServicePaymentTestSupport.LinkOf(e.Content).PaymentId == paymentId);
        Assert.Equal(remindersBefore + 1, remindersAfter);
        Assert.Equal(2, (await PaymentAsync(paymentId)).SentCount);
    }

    [PostgresFact]
    public async Task Offline_ParallelWithASession_NeverLeavesAPaymentIntentThePayerCouldStillPay()
    {
        var (world, requestId, paymentId, token) = await CompletedWithLinkAsync();
        factory.Gateway.CreateDelay = TimeSpan.FromMilliseconds(300);
        try
        {
            using var anonymous = factory.CreateClient();
            using var supplier = factory.Supplier(world);

            var results = await Task.WhenAll(
                anonymous.PostAsJsonAsync($"/api/public/service-payments/{paymentId}/payment-session", new { token }),
                supplier.PostAsJsonAsync($"/api/supplier/requests/{requestId}/payment/offline", new { reason = "Pagato a mano" }));

            // Whoever got the lock first: the offline record always ends up paid, and the session is either served before it
            // (and its PaymentIntent canceled by the record) or refused after it. Never a 500, never both payable.
            Assert.Equal(HttpStatusCode.OK, results[1].StatusCode);
            Assert.Contains(results[0].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
            Assert.Equal(ServiceRequestStatus.Pagato, (await factory.LoadRequestAsync(requestId)).Status);
            var payments = await PaymentsOfRequestAsync(requestId);
            Assert.Single(payments, p => p.Status == ServicePaymentStatus.Paid && p.PaidVia == ServicePaymentChannel.Offline);
            Assert.Equal(ServicePaymentStatus.Canceled, payments.Single(p => p.Id == paymentId).Status);
            foreach (var intentId in payments.Select(p => p.StripePaymentIntentId).Where(id => id is not null))
                Assert.Equal("canceled", factory.Gateway.Intents.Single(i => i.Id == intentId).Status);
        }
        finally
        {
            factory.Gateway.CreateDelay = TimeSpan.Zero;
        }
    }

    [PostgresFact]
    public async Task Confirm_TwoParallelConfirmations_CreateOnePayment_AndNeverA500()
    {
        var (world, requestId) = await TakenOnlineAsync();
        using var supplier = factory.Supplier(world);
        await supplier.PostAsJsonAsync($"/api/service-requests/{requestId}/complete", new { finalAmountCents = 8_000 });
        using var first = factory.Host(world);
        using var second = factory.Host(world);

        var responses = await Task.WhenAll(
            first.PostAsync($"/api/service-requests/{requestId}/final-amount/confirm", content: null),
            second.PostAsync($"/api/service-requests/{requestId}/final-amount/confirm", content: null));

        // One of them confirmed; the other found it confirmed (200) or lost the race on the request (409): never a 500.
        Assert.Contains(HttpStatusCode.OK, responses.Select(r => r.StatusCode));
        Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }));
        foreach (var conflict in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
            Assert.Equal("service_request_state_changed", (await Json(conflict)).GetProperty("code").GetString());

        var payment = Assert.Single(await PaymentsOfRequestAsync(requestId));
        Assert.Equal(8_000, payment.AmountCents);
        Assert.Single(factory.Emails.RequestEmails(), e => ServicePaymentTestSupport.LinkOf(e.Content).PaymentId == payment.Id);
    }

    [PostgresFact]
    public async Task TheIndex_OnePaymentPerRequestThatIsNotCanceled_IsEnforcedByTheDatabase()
    {
        var (_, requestId, paymentId, _) = await CompletedWithLinkAsync();
        var existing = await PaymentAsync(paymentId);

        // A second live payment for the request is refused by the unique index, whatever the status of the new one.
        foreach (var status in new[] { ServicePaymentStatus.Requested, ServicePaymentStatus.Paid, ServicePaymentStatus.Failed })
        {
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => InsertAsync(Copy(existing, requestId, status)));
            var postgres = Assert.IsType<PostgresException>(ex.InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
            Assert.Equal("UIX_ServiceRequestPayments_ServiceRequestId_Live", postgres.ConstraintName);
        }

        // Once the first is canceled the request is free for a new one, and only one live payment exists at a time.
        await ChangePaymentAsync(paymentId, p => { p.Status = ServicePaymentStatus.Canceled; p.CanceledAt = DateTime.UtcNow; });
        await InsertAsync(Copy(existing, requestId, ServicePaymentStatus.Requested));
        await InsertAsync(Copy(existing, requestId, ServicePaymentStatus.Canceled));
        var all = await PaymentsOfRequestAsync(requestId);
        Assert.Equal(3, all.Count);
        Assert.Single(all, p => p.Status == ServicePaymentStatus.Requested);
    }

    [PostgresFact]
    public async Task ThePaymentIntentOfAPayment_BelongsToThatPaymentOnly()
    {
        var (_, requestId, paymentId, token) = await CompletedWithLinkAsync();
        using var anonymous = factory.CreateClient();
        await anonymous.PostAsJsonAsync($"/api/public/service-payments/{paymentId}/payment-session", new { token });
        var existing = await PaymentAsync(paymentId);
        var (_, otherRequestId, _, _) = await CompletedWithLinkAsync();

        var duplicate = Copy(existing, otherRequestId, ServicePaymentStatus.Canceled);
        duplicate.StripePaymentIntentId = existing.StripePaymentIntentId;

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => InsertAsync(duplicate));
        Assert.Equal("UIX_ServiceRequestPayments_StripePaymentIntentId", Assert.IsType<PostgresException>(ex.InnerException).ConstraintName);
        Assert.Equal(requestId, existing.ServiceRequestId);
    }

    // ─── Helpers ───

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

    private async Task<(ServiceRequestWorld World, Guid RequestId)> TakenOnlineAsync()
    {
        var world = await ReadyWorldAsync();
        var id = await factory.CreateRequestAsync(world);
        using var supplier = factory.Supplier(world);
        var taken = await supplier.PostAsync($"/api/service-requests/{id}/take", content: null);
        Assert.Equal(HttpStatusCode.OK, taken.StatusCode);
        return (world, id);
    }

    private async Task<(ServiceRequestWorld World, Guid RequestId, Guid PaymentId, string Token)> CompletedWithLinkAsync()
    {
        var (world, id) = await TakenOnlineAsync();
        using var supplier = factory.Supplier(world);
        var completed = await supplier.PostAsJsonAsync($"/api/service-requests/{id}/complete", new { finalAmountCents = 6_000 });
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        var payment = Assert.Single(await PaymentsOfRequestAsync(id));
        var link = factory.Emails.RequestEmails()
            .Select(e => ServicePaymentTestSupport.LinkOf(e.Content))
            .Single(l => l.PaymentId == payment.Id);
        return (world, id, payment.Id, link.Token);
    }

    private async Task<List<ServiceRequestPayment>> PaymentsOfRequestAsync(Guid requestId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ServiceRequestPayments.AsNoTracking().Where(p => p.ServiceRequestId == requestId).OrderBy(p => p.CreatedAt).ToListAsync();
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

    private async Task InsertAsync(ServiceRequestPayment payment)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.ServiceRequestPayments.Add(payment);
        await db.SaveChangesAsync();
    }

    /// <summary>A new row like <paramref name="source"/> for <paramref name="requestId"/> in <paramref name="status"/> (a paid one carries when and how).</summary>
    private static ServiceRequestPayment Copy(ServiceRequestPayment source, Guid requestId, ServicePaymentStatus status) => new()
    {
        ServiceRequestId = requestId,
        SupplierOrgId = source.SupplierOrgId,
        PayerKind = source.PayerKind,
        PayerOrgId = source.PayerOrgId,
        AmountCents = source.AmountCents,
        CommissionPercent = source.CommissionPercent,
        ApplicationFeeCents = source.ApplicationFeeCents,
        NetCents = source.NetCents,
        Status = status,
        PaidAt = status == ServicePaymentStatus.Paid ? DateTime.UtcNow : null,
        PaidVia = status == ServicePaymentStatus.Paid ? ServicePaymentChannel.Offline : null,
        CanceledAt = status == ServicePaymentStatus.Canceled ? DateTime.UtcNow : null,
    };

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private async Task<string> ConnectionStringAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString()!;
    }

    /// <summary>Another connection holding the payment lock of a request, as a webhook or a refund that is writing its payment would (SP-15b).</summary>
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
                "WHERE l.locktype = 'advisory' AND NOT l.granted AND d.datname = current_database()",
                connection);
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
