using System.Net;
using Casazen.Core.Services;
using Stripe;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// An <see cref="IPlatformPaymentGateway"/> that never reaches Stripe (SP-15c-a). It behaves the way Stripe does where the service depends on it:
/// a retry with the same idempotency key returns the same PaymentIntent, refund or transfer; a block can be charged or canceled once and a
/// PaymentIntent in another state is returned as it is (<c>payment_intent_unexpected_state</c>); a refund and a transfer are listed after they are
/// made. Tests arrange a block with <see cref="AddBlock"/>, make a call fail with <see cref="FailNextCapture"/> / <see cref="FailNextTransfer"/>,
/// lose the answer of a transfer that was made with <see cref="LoseNextTransferAnswer"/>, and make the card charged just before a cancellation with
/// <see cref="ChargeOnNextCancel"/>.
/// </summary>
internal sealed class FakePlatformPaymentGateway : IPlatformPaymentGateway
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PlatformCharge> _intents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _intentByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ServiceChargeRefund> _refundsByKey = new(StringComparer.Ordinal);
    private readonly List<ServiceChargeRefund> _refunds = [];
    private readonly Dictionary<string, ServiceTransfer> _transfersByKey = new(StringComparer.Ordinal);
    private readonly List<ServiceTransfer> _transfers = [];
    private int _sequence;
    private Exception? _failNextCreate;
    private Exception? _failNextCapture;
    private Exception? _failNextTransfer;
    private Exception? _failNextList;
    private bool _loseNextTransferAnswer;
    private bool _chargeOnNextCancel;

    public List<PlatformChargeRequest> Created { get; } = [];

    /// <summary>Every capture received (also the repeated ones): PaymentIntent and key.</summary>
    public List<(string PaymentIntentId, string Key)> Captures { get; } = [];

    /// <summary>Every cancellation received: PaymentIntent and key.</summary>
    public List<(string PaymentIntentId, string Key)> Cancellations { get; } = [];

    /// <summary>Every refund request received (also the ones answered with an existing refund by the key).</summary>
    public List<PlatformRefundRequest> RefundRequests { get; } = [];

    /// <summary>Every transfer request received (also the ones answered with an existing transfer by the key).</summary>
    public List<ServiceTransferRequest> TransferRequests { get; } = [];

    /// <summary>The transfers that exist on this fake Stripe: one per key that was ever created.</summary>
    public IReadOnlyList<ServiceTransfer> Transfers
    {
        get
        {
            lock (_gate)
                return _transfers.ToList();
        }
    }

    public IReadOnlyList<ServiceChargeRefund> Refunds
    {
        get
        {
            lock (_gate)
                return _refunds.ToList();
        }
    }

    public int ListTransfersCount { get; private set; }

    /// <summary>A block on a card of the customer, ready to be charged: the PaymentIntent exists in <c>requires_capture</c>.</summary>
    public PlatformCharge AddBlock(string id, long amountCents, string transferGroup, IReadOnlyDictionary<string, string>? metadata = null)
    {
        lock (_gate)
        {
            var charge = new PlatformCharge(
                id,
                "requires_capture",
                amountCents,
                amountCents,
                0,
                "eur",
                null,
                $"{id}_secret",
                $"ch_{id}",
                null,
                transferGroup,
                null,
                metadata ?? new Dictionary<string, string>());
            _intents[id] = charge;
            return charge;
        }
    }

    public PlatformCharge SetStatus(string id, string status, long? received = null)
    {
        lock (_gate)
        {
            var current = _intents[id];
            var updated = current with
            {
                Status = status,
                AmountCapturableCents = status == "requires_capture" ? current.AmountCents : 0,
                AmountReceivedCents = status == "succeeded" ? received ?? current.AmountCents : current.AmountReceivedCents,

                // A PaymentIntent that was paid has a charge (the source of the transfer to the supplier).
                LatestChargeId = status == "succeeded" ? current.LatestChargeId ?? $"ch_{id}" : current.LatestChargeId,
            };
            _intents[id] = updated;
            return updated;
        }
    }

    public PlatformCharge Intent(string id)
    {
        lock (_gate)
            return _intents[id];
    }

    /// <summary>The next PaymentIntent cannot be created (Stripe is down): nothing exists on this fake Stripe afterwards.</summary>
    public void FailNextCreate(Exception exception) => _failNextCreate = exception;

    public void FailNextCapture(Exception exception) => _failNextCapture = exception;

    public void FailNextTransfer(Exception exception) => _failNextTransfer = exception;

    public void FailNextList(Exception exception) => _failNextList = exception;

    /// <summary>The next transfer is created on Stripe but the answer never comes back (a timeout): the next try must find it, not make another.</summary>
    public void LoseNextTransferAnswer() => _loseNextTransferAnswer = true;

    /// <summary>The next cancellation finds the card already charged (the supplier's capture came just before).</summary>
    public void ChargeOnNextCancel() => _chargeOnNextCancel = true;

    public Task<PlatformCharge> CreateAsync(PlatformChargeRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Created.Add(request);
            if (_failNextCreate is { } failure)
            {
                _failNextCreate = null;
                throw failure;
            }

            if (_intentByKey.TryGetValue(request.IdempotencyKey, out var existing))
                return Task.FromResult(_intents[existing]);

            var id = $"pi_fake_{++_sequence:D4}";
            var metadata = new Dictionary<string, string>(request.Metadata) { ["kind"] = "service-charge", ["funds"] = "platform-held" };
            var charge = new PlatformCharge(
                id, "requires_payment_method", request.AmountCents, 0, 0, request.Currency, null, $"{id}_secret", null, null,
                request.TransferGroup, null, metadata);
            _intents[id] = charge;
            _intentByKey[request.IdempotencyKey] = id;
            return Task.FromResult(charge);
        }
    }

    public Task<PlatformCharge> GetAsync(string paymentIntentId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult(Find(paymentIntentId));
    }

    public Task<PlatformCharge> CaptureAsync(string paymentIntentId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Captures.Add((paymentIntentId, idempotencyKey));
            if (_failNextCapture is { } failure)
            {
                _failNextCapture = null;
                throw failure;
            }

            var current = Find(paymentIntentId);
            if (current.Status != "requires_capture")
                return Task.FromResult(current);

            var charged = current with { Status = "succeeded", AmountCapturableCents = 0, AmountReceivedCents = current.AmountCents };
            _intents[paymentIntentId] = charged;
            return Task.FromResult(charged);
        }
    }

    public Task<PlatformCharge> CancelAsync(string paymentIntentId, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Cancellations.Add((paymentIntentId, idempotencyKey));
            var current = Find(paymentIntentId);

            if (_chargeOnNextCancel && current.Status == "requires_capture")
            {
                _chargeOnNextCancel = false;
                var charged = current with { Status = "succeeded", AmountCapturableCents = 0, AmountReceivedCents = current.AmountCents };
                _intents[paymentIntentId] = charged;
                return Task.FromResult(charged);
            }

            if (current.Status is "requires_payment_method" or "requires_confirmation" or "requires_action" or "requires_capture")
            {
                var canceled = current with { Status = "canceled", AmountCapturableCents = 0 };
                _intents[paymentIntentId] = canceled;
                return Task.FromResult(canceled);
            }

            // Charged or already canceled: Stripe says payment_intent_unexpected_state and the gateway returns the real state.
            return Task.FromResult(current);
        }
    }

    public Task<ServiceChargeRefund> CreateRefundAsync(PlatformRefundRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            RefundRequests.Add(request);
            if (_refundsByKey.TryGetValue(request.IdempotencyKey, out var existing))
                return Task.FromResult(existing);

            Find(request.PaymentIntentId);
            var metadata = new Dictionary<string, string>(request.Metadata) { ["kind"] = "service-charge-refund" };
            var refund = new ServiceChargeRefund($"re_fake_{++_sequence:D4}", request.PaymentIntentId, request.AmountCents, "succeeded", null, metadata);
            _refundsByKey[request.IdempotencyKey] = refund;
            _refunds.Add(refund);
            return Task.FromResult(refund);
        }
    }

    public Task<IReadOnlyList<ServiceChargeRefund>> ListRefundsAsync(string paymentIntentId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<ServiceChargeRefund> refunds = _refunds.Where(r => r.PaymentIntentId == paymentIntentId).ToList();
            return Task.FromResult(refunds);
        }
    }

    public Task<ServiceTransfer> CreateTransferAsync(ServiceTransferRequest request, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            TransferRequests.Add(request);
            if (_failNextTransfer is { } failure)
            {
                _failNextTransfer = null;
                throw failure;
            }

            // The same key is the same transfer (Stripe keeps a key for a day).
            if (_transfersByKey.TryGetValue(request.IdempotencyKey, out var existing))
                return Task.FromResult(existing);

            var metadata = new Dictionary<string, string>(request.Metadata) { ["kind"] = "service-transfer" };
            var transfer = new ServiceTransfer(
                $"tr_fake_{++_sequence:D4}", request.AmountCents, request.Currency, request.DestinationAccountId, request.SourceChargeId,
                request.TransferGroup, false, 0, metadata);
            _transfersByKey[request.IdempotencyKey] = transfer;
            _transfers.Add(transfer);

            if (_loseNextTransferAnswer)
            {
                _loseNextTransferAnswer = false;
                throw new HttpRequestException("The answer of Stripe never came back.");
            }

            return Task.FromResult(transfer);
        }
    }

    public Task<IReadOnlyList<ServiceTransfer>> ListTransfersAsync(string transferGroup, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ListTransfersCount++;
            if (_failNextList is { } failure)
            {
                _failNextList = null;
                throw failure;
            }

            IReadOnlyList<ServiceTransfer> transfers = _transfers.Where(t => t.TransferGroup == transferGroup).ToList();
            return Task.FromResult(transfers);
        }
    }

    private PlatformCharge Find(string paymentIntentId) =>
        _intents.TryGetValue(paymentIntentId, out var charge)
            ? charge
            : throw new StripeException(
                HttpStatusCode.NotFound,
                new StripeError { Type = "invalid_request_error", Code = "resource_missing" },
                "No such payment_intent");
}
