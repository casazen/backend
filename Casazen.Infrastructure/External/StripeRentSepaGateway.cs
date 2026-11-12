using Casazen.Core.Services;
using Microsoft.Extensions.Configuration;
using Stripe;

namespace Casazen.Infrastructure.External;

public interface IStripeRentSepaGateway
{
    Task<SetupIntent> SetupAsync(string account, Guid leaseId, string userId, string email, string key, CancellationToken ct);
    Task<SetupIntent> ReadSetupAsync(string account, string id, CancellationToken ct);
    Task<PaymentIntent> CreateAsync(string account, Guid leaseId, Guid installmentId, long amount,
        string key, CancellationToken ct);
    Task<PaymentIntent> AttachAsync(string id, string customer, string method, CancellationToken ct);
    Task<PaymentIntent> ConfirmAsync(string account, string id, string customer, string method, CancellationToken ct);
}

public sealed class StripeRentSepaGateway(IConfiguration config, IStripeClient? stripeClient = null) : IStripeRentSepaGateway
{
    private readonly IStripeClient client = stripeClient ?? new StripeClient(config["Stripe:SecretKey"]);
    private static RequestOptions Request(string account, string? key = null)
    {
        var options = new RequestOptions { IdempotencyKey = key };
        StripeBankLinkGateway.PinSessionApiVersion(options);
        return options;
    }

    public async Task<SetupIntent> SetupAsync(string account, Guid leaseId, string userId, string email, string key, CancellationToken ct)
    {
        var customer = await new CustomerService(client).CreateAsync(new CustomerCreateOptions { Email = email },
            Request(account, key + ":customer"), ct);
        var options = new SetupIntentCreateOptions
        {
            Customer = customer.Id,
            Usage = "off_session",
            OnBehalfOf = account,
            Metadata = new() { ["kind"] = "tenant-rent-sepa", ["leaseId"] = leaseId.ToString(), ["userId"] = userId },
        };
        options.AddExtraParam("allowed_payment_method_types", new[] { "sepa_debit" });
        return await new SetupIntentService(client).CreateAsync(options, Request(account, key), ct);
    }

    public Task<SetupIntent> ReadSetupAsync(string account, string id, CancellationToken ct) =>
        new SetupIntentService(client).GetAsync(id, new SetupIntentGetOptions { Expand = ["payment_method"] }, Request(account), ct);

    public async Task<PaymentIntent> CreateAsync(string account, Guid leaseId, Guid installmentId, long amount, string key, CancellationToken ct)
    {
        // Destination charge: Stripe bills processing fees to CasaZen; the full due is sent to the landlord.
        var options = new PaymentIntentCreateOptions
        {
            Amount = amount,
            Currency = "eur",
            ApplicationFeeAmount = 100,
            OnBehalfOf = account,
            TransferData = new PaymentIntentTransferDataOptions { Destination = account },
            Metadata = new()
            {
                ["kind"] = RentCharges.Kind,
                [RentCharges.InstallmentMetadataKey] = installmentId.ToString(),
                ["leaseId"] = leaseId.ToString(),
                ["transactionFeeCents"] = "100"
            },
        };
        options.AddExtraParam("allowed_payment_method_types", new[] { "sepa_debit" });
        return await new PaymentIntentService(client).CreateAsync(options, Request(account, key), ct);
    }

    public Task<PaymentIntent> AttachAsync(string id, string customer, string method, CancellationToken ct) =>
        new PaymentIntentService(client).UpdateAsync(id,
            new PaymentIntentUpdateOptions { Customer = customer, PaymentMethod = method },
            Request("", id + ":saved-method:" + method), ct);

    public async Task<PaymentIntent> ConfirmAsync(string account, string id, string customer, string method, CancellationToken ct)
    {
        await new PaymentIntentService(client).UpdateAsync(id, new PaymentIntentUpdateOptions { Customer = customer }, Request(account, id + ":customer"), ct);
        return await new PaymentIntentService(client).ConfirmAsync(id,
            new PaymentIntentConfirmOptions { PaymentMethod = method, OffSession = true }, Request(account, id + ":auto-confirm"), ct);
    }
}
