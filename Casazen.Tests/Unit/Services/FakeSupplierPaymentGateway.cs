using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Stripe;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// A <see cref="ISupplierPaymentGateway"/> that never reaches Stripe (SP-15a). It behaves the way Stripe does where the service
/// depends on it: a retry with the same idempotency key returns the same PaymentIntent, an application fee that is not strictly
/// between zero and the amount is not kept, a PaymentIntent of another account is "not found", and a PaymentIntent that is paid or
/// in progress cannot be canceled (it is returned as it is). Tests move a PaymentIntent along with <see cref="SetStatus"/>, make a
/// call fail with <see cref="FailNextCreate"/> or <see cref="FailNextGet"/>, delete a connected account with <see cref="DeleteAccount"/>
/// and widen a race with <see cref="CreateDelay"/>.
/// </summary>
internal sealed class FakeSupplierPaymentGateway : ISupplierPaymentGateway
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ServiceChargeIntent> _intents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServiceChargeIntent> _byIdempotencyKey = new(StringComparer.Ordinal);
    private int _sequence;

    public List<ServiceChargeIntentRequest> Created { get; } = [];

    /// <summary>Every cancellation received: PaymentIntent, account and idempotency key.</summary>
    public List<(string PaymentIntentId, string AccountId, string IdempotencyKey)> Canceled { get; } = [];

    public int GetCount { get; private set; }

    private readonly Dictionary<string, ServiceChargeRefund> _refunds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServiceChargeRefund> _refundsByKey = new(StringComparer.Ordinal);
    private int _refundSequence;
    private Exception? _failNextRefund;
    private Exception? _failNextList;

    /// <summary>Every refund request received, in order (also the ones answered with an existing refund by the idempotency key).</summary>
    public List<ServiceChargeRefundRequest> RefundRequests { get; } = [];

    public int ListRefundsCount { get; private set; }

    public string NewRefundStatus { get; set; } = "succeeded";

    public IReadOnlyList<ServiceChargeRefund> Refunds
    {
        get
        {
            lock (_gate)
                return _refunds.Values.ToList();
        }
    }

    public void FailNextRefund(Exception exception) => _failNextRefund = exception;

    public void FailNextList(Exception exception) => _failNextList = exception;

    /// <summary>
    /// A refund the creation of which "succeeded" at Stripe although the answer never came back: it exists with its idempotency key
    /// and its metadata, and the next call finds it. Returns the refund.
    /// </summary>
    public ServiceChargeRefund CreateRefundSilently(ServiceChargeRefundRequest request)
    {
        lock (_gate)
            return StoreRefund(request, NewRefundStatus);
    }

    public ServiceChargeRefund SetRefundStatus(string refundId, string status, string? failureReason = null)
    {
        lock (_gate)
        {
            var updated = _refunds[refundId] with { Status = status, FailureReason = failureReason };
            _refunds[refundId] = updated;
            return updated;
        }
    }

    /// <summary>A refund made outside CasaZen (the Stripe Dashboard): it has no metadata and no idempotency key of ours.</summary>
    public ServiceChargeRefund AddExternalRefund(string paymentIntentId, long amountCents, string status = "succeeded")
    {
        lock (_gate)
        {
            var refund = new ServiceChargeRefund($"re_fake_{++_refundSequence:D4}", paymentIntentId, amountCents, status, null, new Dictionary<string, string>());
            _refunds[refund.Id] = refund;
            return refund;
        }
    }

    public Task<ServiceChargeRefund> CreateRefundAsync(ServiceChargeRefundRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            RefundRequests.Add(request);
            if (_failNextRefund is { } failure)
            {
                _failNextRefund = null;
                throw failure;
            }

            if (_deletedAccounts.Contains(request.ConnectedAccountId))
                throw AccountInvalid(request.ConnectedAccountId);

            // The same idempotency key is the same refund (Stripe keeps it for 24 hours).
            if (_refundsByKey.TryGetValue(request.IdempotencyKey, out var existing))
                return Task.FromResult(_refunds[existing.Id]);

            Find(request.PaymentIntentId, request.ConnectedAccountId);
            return Task.FromResult(StoreRefund(request, NewRefundStatus));
        }
    }

    public Task<IReadOnlyList<ServiceChargeRefund>> ListRefundsAsync(
        string paymentIntentId,
        string connectedAccountId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ListRefundsCount++;
            if (_failNextList is { } failure)
            {
                _failNextList = null;
                throw failure;
            }

            Find(paymentIntentId, connectedAccountId);
            IReadOnlyList<ServiceChargeRefund> refunds = _refunds.Values.Where(r => r.PaymentIntentId == paymentIntentId).ToList();
            return Task.FromResult(refunds);
        }
    }

    private ServiceChargeRefund StoreRefund(ServiceChargeRefundRequest request, string status)
    {
        var metadata = new Dictionary<string, string>(request.Metadata) { ["kind"] = ServiceCharges.RefundKind };
        var refund = new ServiceChargeRefund($"re_fake_{++_refundSequence:D4}", request.PaymentIntentId, request.AmountCents, status, null, metadata);
        _refunds[refund.Id] = refund;
        _refundsByKey[request.IdempotencyKey] = refund;
        return refund;
    }

    public IReadOnlyList<ServiceChargeIntent> Intents
    {
        get
        {
            lock (_gate)
                return _intents.Values.ToList();
        }
    }

    public TimeSpan CreateDelay { get; set; }

    public void FailNextCreate(Exception exception) => _failNextCreate = exception;

    private Exception? _failNextCreate;

    public void FailNextGet(Exception exception) => _failNextGet = exception;

    private Exception? _failNextGet;

    private readonly HashSet<string> _deletedAccounts = new(StringComparer.Ordinal);

    public void DeleteAccount(string accountId)
    {
        lock (_gate)
            _deletedAccounts.Add(accountId);
    }

    public void Reset()
    {
        lock (_gate)
        {
            _intents.Clear();
            _byIdempotencyKey.Clear();
            Created.Clear();
            Canceled.Clear();
            GetCount = 0;
            CreateDelay = TimeSpan.Zero;
            _failNextCreate = null;
            _failNextGet = null;
            _deletedAccounts.Clear();
            _refunds.Clear();
            _refundsByKey.Clear();
            RefundRequests.Clear();
            ListRefundsCount = 0;
            NewRefundStatus = "succeeded";
            _failNextRefund = null;
            _failNextList = null;
        }
    }

    public async Task<ServiceChargeIntent> CreateAsync(ServiceChargeIntentRequest request, CancellationToken cancellationToken = default)
    {
        if (CreateDelay > TimeSpan.Zero)
            await Task.Delay(CreateDelay, cancellationToken);

        lock (_gate)
        {
            Created.Add(request);
            if (_failNextCreate is { } failure)
            {
                _failNextCreate = null;
                throw failure;
            }

            if (_deletedAccounts.Contains(request.ConnectedAccountId))
                throw AccountInvalid(request.ConnectedAccountId);

            // The same idempotency key is the same PaymentIntent (Stripe keeps it for 24 hours).
            if (_byIdempotencyKey.TryGetValue(request.IdempotencyKey, out var existing))
                return existing;

            var id = $"pi_fake_{++_sequence:D4}";
            var fee = request.ApplicationFeeCents is { } value && SupplierCommission.IsSendable(value, request.AmountCents) ? value : (long?)null;
            var intent = new ServiceChargeIntent(
                id,
                "requires_payment_method",
                request.AmountCents,
                request.Currency,
                fee,
                $"{id}_secret_fake",
                request.ConnectedAccountId,
                LastErrorCode: null);
            _intents[id] = intent;
            _byIdempotencyKey[request.IdempotencyKey] = intent;
            return intent;
        }
    }

    public Task<ServiceChargeIntent> GetAsync(string paymentIntentId, string connectedAccountId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            GetCount++;
            if (_failNextGet is { } failure)
            {
                _failNextGet = null;
                throw failure;
            }

            return Task.FromResult(Find(paymentIntentId, connectedAccountId));
        }
    }

    public Task<ServiceChargeIntent> CancelAsync(
        string paymentIntentId,
        string connectedAccountId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Canceled.Add((paymentIntentId, connectedAccountId, idempotencyKey));
            var current = Find(paymentIntentId, connectedAccountId);
            if (!ServiceCharges.IsPayable(current.Status))
                return Task.FromResult(current);

            var canceled = current with { Status = "canceled" };
            _intents[paymentIntentId] = canceled;
            return Task.FromResult(canceled);
        }
    }

    public ServiceChargeIntent SetStatus(string paymentIntentId, string status, string? lastErrorCode = null)
    {
        lock (_gate)
        {
            var updated = _intents[paymentIntentId] with { Status = status, LastErrorCode = lastErrorCode };
            _intents[paymentIntentId] = updated;
            return updated;
        }
    }

    public ServiceChargeIntent UpdateIntent(string paymentIntentId, Func<ServiceChargeIntent, ServiceChargeIntent> change)
    {
        lock (_gate)
        {
            var updated = change(_intents[paymentIntentId]);
            _intents[paymentIntentId] = updated;
            return updated;
        }
    }

    public ServiceChargeIntent Intent(string paymentIntentId)
    {
        lock (_gate)
            return _intents[paymentIntentId];
    }

    private static StripeException AccountInvalid(string connectedAccountId) => new(
        System.Net.HttpStatusCode.Forbidden,
        new StripeError { Type = "invalid_request_error", Code = "account_invalid" },
        $"The provided key does not have access to account '{connectedAccountId}' (or that account does not exist).");

    private ServiceChargeIntent Find(string paymentIntentId, string connectedAccountId)
    {
        if (_deletedAccounts.Contains(connectedAccountId))
            throw AccountInvalid(connectedAccountId);

        if (_intents.TryGetValue(paymentIntentId, out var intent) && intent.ConnectedAccountId == connectedAccountId)
            return intent;

        throw new StripeException(
            System.Net.HttpStatusCode.NotFound,
            new StripeError { Type = "invalid_request_error", Code = "resource_missing" },
            $"No such payment_intent: '{paymentIntentId}'");
    }
}
