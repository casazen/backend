using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Leases;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe.Checkout;
using StripeConfiguration = Stripe.StripeConfiguration;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Stripe Checkout for the Openapi filing fee. The amount is the existing registration-tax percentage of the annual
/// rent (minimum included). The session carries <c>kind=openapi-lease-registration</c> so it is not a ConcordatoFacile
/// payment. The return address is <c>ConcordatoFacile:BaseUrl</c>, the web app address that checkout already uses.
/// </summary>
public sealed class OpenapiRegistrationCheckoutService(
    AppDbContext db,
    IOptions<CedolareAdvisoryOptions> advisory,
    IOptions<ConcordatoFacileOptions> concordato,
    IConfiguration configuration,
    ILogger<OpenapiRegistrationCheckoutService> logger,
    Stripe.IStripeClient? stripeClient = null,
    TimeProvider? timeProvider = null) : IOpenapiRegistrationCheckoutService
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private Stripe.IStripeClient Client => stripeClient ?? StripeConfiguration.StripeClient;

    public async Task<CheckoutResult> CreateCheckoutSessionAsync(
        Guid leaseId,
        Guid orgId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        await db.EnsureLeaseOfOrgAsync(leaseId, orgId, cancellationToken);

        await using var transaction = await PostgresAdvisoryLocks.BeginLockedTransactionAsync(
            db,
            cancellationToken,
            (PostgresAdvisoryLocks.Scope.OpenapiLeaseRegistrationCheckout, leaseId.ToString("N")));

        var lease = await db.LeaseContracts.SingleAsync(l => l.Id == leaseId && l.OrgId == orgId, cancellationToken);
        if (!OpenapiRegistrationFee.Applies(lease.ContractType))
            throw new DomainConflictException(OpenapiRegistrationFee.NotThisContractCode, "OpenapiRegistrationConcordato");

        var quote = OpenapiRegistrationFee.Quote(lease.MonthlyRent, advisory.Value.Registro);
        if (quote.AmountCents <= 0)
            throw new DomainConflictException(OpenapiRegistrationFee.AlreadyPaidCode, "OpenapiRegistrationAlreadyPaid");

        var now = _clock.GetUtcNow().UtcDateTime;
        var existing = await db.OpenapiLeaseRegistrationPayments
            .Where(p => p.LeaseContractId == leaseId && p.OrgId == orgId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken);

        if (existing.Any(p => p.Status == OpenapiRegistrationPaymentStatus.Completed
                && p.AmountCents == quote.AmountCents
                && p.FeePercent == quote.Percent))
        {
            throw new DomainConflictException(OpenapiRegistrationFee.AlreadyPaidCode, "OpenapiRegistrationAlreadyPaid");
        }

        var reusable = existing.FirstOrDefault(p =>
            p.Status == OpenapiRegistrationPaymentStatus.Pending
            && p.AmountCents == quote.AmountCents
            && p.FeePercent == quote.Percent
            && p.CreatedAt >= now - SessionLifetime
            && !string.IsNullOrWhiteSpace(p.SessionUrl));
        if (reusable is not null)
        {
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return new CheckoutResult(reusable.StripeCheckoutSessionId, reusable.SessionUrl!, Reused: true);
        }

        foreach (var stale in existing.Where(p => p.Status == OpenapiRegistrationPaymentStatus.Pending))
            stale.Status = OpenapiRegistrationPaymentStatus.Failed;

        if (stripeClient is null)
            ConfigureStripe();

        var baseUrl = WebAppBaseUrl();
        var metadata = new Dictionary<string, string>
        {
            ["kind"] = OpenapiRegistrationFee.StripeKind,
            ["leaseId"] = leaseId.ToString(),
            ["orgId"] = orgId.ToString(),
            ["userId"] = userId,
        };
        var sessions = new SessionService(Client);
        var session = await sessions.CreateAsync(new SessionCreateOptions
        {
            Mode = "payment",
            ExpiresAt = now.Add(SessionLifetime),
            LineItems =
            [
                new SessionLineItemOptions
                {
                    Quantity = 1,
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = "eur",
                        UnitAmount = quote.AmountCents,
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = "Registrazione contratto di locazione",
                        },
                    },
                },
            ],
            SuccessUrl = $"{baseUrl}/app/long-rent/leases/{leaseId}?registrationCheckout=success",
            CancelUrl = $"{baseUrl}/app/long-rent/leases/{leaseId}?registrationCheckout=cancelled",
            Metadata = metadata,
            PaymentIntentData = new SessionPaymentIntentDataOptions { Metadata = metadata },
        }, cancellationToken: cancellationToken);

        var payment = new OpenapiLeaseRegistrationPayment
        {
            OrgId = orgId,
            LeaseContractId = leaseId,
            FeePercent = quote.Percent,
            AmountCents = quote.AmountCents,
            Currency = "eur",
            Status = OpenapiRegistrationPaymentStatus.Pending,
            StripeCheckoutSessionId = session.Id,
            SessionUrl = session.Url,
            CreatedAt = now,
        };
        db.OpenapiLeaseRegistrationPayments.Add(payment);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "OpenAPI registration checkout opened. LeaseId={LeaseId} OrgId={OrgId} FeePercent={FeePercent} AmountCents={AmountCents}",
            leaseId,
            orgId,
            quote.Percent,
            quote.AmountCents);

        return new CheckoutResult(session.Id, session.Url ?? throw new InvalidOperationException("Stripe checkout session URL missing"), Reused: false);
    }

    private string WebAppBaseUrl()
    {
        var configured = concordato.Value.BaseUrl;
        if (string.IsNullOrWhiteSpace(configured))
            configured = configuration["App:PublicSiteBaseUrl"];
        var trimmed = configured?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException(
                "ConcordatoFacile:BaseUrl is not configured (the web app address the registration checkout returns to).");
        }

        return trimmed;
    }

    private void ConfigureStripe()
    {
        var key = concordato.Value.StripeApiKey;
        if (!string.IsNullOrWhiteSpace(key))
            StripeConfiguration.ApiKey = key;
    }
}
