using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-04e: the rules that tell whether the supplier may ask for the payment of a request, and whether it may record it as received
/// outside CasaZen, one rule at a time: a set of facts where both are allowed, and each fact turned the wrong way. That these are the
/// rules of the two endpoints is proved with the real services in <c>SupplierPaymentSummaryTests</c>.
/// </summary>
public class ServicePaymentActionsTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 10, 0, 0, DateTimeKind.Utc);

    /// <summary>A completed request paid inside CasaZen, asked 2 days ago, the supplier ready and able to act: everything allowed.</summary>
    private static ServicePaymentActionFacts Allowed() => new(
        ServiceRequestStatus.Completato,
        ServiceRequestPaymentMode.Online,
        FinalAmountCents: 6000,
        FinalAmountNeedsConfirmation: false,
        PaymentStatus: ServicePaymentStatus.Requested,
        PaymentLastSentAt: Now.AddDays(-2),
        OnlinePaymentsEnabled: true,
        SupplierMayAct: true,
        SupplierCanReceivePayments: true,
        PayerHasEmail: true,
        MinAmountCents: 50);

    [Fact]
    public void Allowed_Baseline_CanRequestAndCanRecordOffline()
    {
        Assert.True(ServicePaymentActions.CanRequest(Allowed(), Now));
        Assert.True(ServicePaymentActions.CanRecordOffline(Allowed()));
    }

    // ─── Ask for the payment ───

    [Fact]
    public void CanRequest_FlagOff_IsFalse_ButOfflineStaysPossible()
    {
        var facts = Allowed() with { OnlinePaymentsEnabled = false };

        Assert.False(ServicePaymentActions.CanRequest(facts, Now));
        Assert.True(ServicePaymentActions.CanRecordOffline(facts));
    }

    [Fact]
    public void CanRequest_ASupplierThatMayNotAct_IsFalse()
    {
        Assert.False(ServicePaymentActions.CanRequest(Allowed() with { SupplierMayAct = false }, Now));
    }

    [Fact]
    public void CanRequest_ARequestPaidOutsideCasaZen_IsFalse()
    {
        Assert.False(ServicePaymentActions.CanRequest(Allowed() with { PaymentMode = ServiceRequestPaymentMode.Manual }, Now));
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto)]
    [InlineData(ServiceRequestStatus.PresoInCarico)]
    [InlineData(ServiceRequestStatus.InCorso)]
    [InlineData(ServiceRequestStatus.Pagato)]
    [InlineData(ServiceRequestStatus.Rifiutato)]
    [InlineData(ServiceRequestStatus.Annullato)]
    public void CanRequest_OnlyACompletedRequest(ServiceRequestStatus status)
    {
        Assert.False(ServicePaymentActions.CanRequest(Allowed() with { RequestStatus = status, PaymentStatus = null }, Now));
    }

    [Fact]
    public void CanRequest_AnAmountTheHostHasToConfirmFirst_IsFalse()
    {
        Assert.False(ServicePaymentActions.CanRequest(Allowed() with { FinalAmountNeedsConfirmation = true }, Now));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0, false)]
    [InlineData(49, false)]
    [InlineData(50, true)]
    [InlineData(6000, true)]
    public void CanRequest_NeedsAnAmountOfAtLeastTheMinimum(int? amount, bool allowed)
    {
        Assert.Equal(allowed, ServicePaymentActions.CanRequest(Allowed() with { FinalAmountCents = amount }, Now));
    }

    [Fact]
    public void CanRequest_TheMinimumIsTheConfiguredOne()
    {
        var facts = Allowed() with { FinalAmountCents = 400 };

        Assert.True(ServicePaymentActions.CanRequest(facts with { MinAmountCents = 400 }, Now));
        Assert.False(ServicePaymentActions.CanRequest(facts with { MinAmountCents = 401 }, Now));
    }

    [Fact]
    public void CanRequest_ASupplierThatCannotBePaid_IsFalse()
    {
        Assert.False(ServicePaymentActions.CanRequest(Allowed() with { SupplierCanReceivePayments = false }, Now));
    }

    [Fact]
    public void CanRequest_AnOrgThatPaysWithoutAnAddress_IsFalse()
    {
        Assert.False(ServicePaymentActions.CanRequest(Allowed() with { PayerHasEmail = false }, Now));
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Requested, true)]
    [InlineData(ServicePaymentStatus.Failed, true)]
    [InlineData(ServicePaymentStatus.Processing, false)]
    [InlineData(ServicePaymentStatus.Paid, false)]
    [InlineData(ServicePaymentStatus.PartiallyRefunded, false)]
    [InlineData(ServicePaymentStatus.Refunded, false)]
    [InlineData(ServicePaymentStatus.NeedsReview, false)]
    public void CanRequest_AccordingToWhereThePaymentIs(ServicePaymentStatus status, bool allowed)
    {
        Assert.Equal(allowed, ServicePaymentActions.CanRequest(Allowed() with { PaymentStatus = status }, Now));
    }

    [Fact]
    public void CanRequest_WithNoPaymentYet_IsAllowed_BecauseTheRequestCreatesIt()
    {
        Assert.True(ServicePaymentActions.CanRequest(Allowed() with { PaymentStatus = null, PaymentLastSentAt = null }, Now));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(60, false)]
    [InlineData(23 * 60 + 59, false)]
    [InlineData(24 * 60, true)]
    [InlineData(24 * 60 + 1, true)]
    public void CanRequest_AtMostOneLinkADay(int minutesSinceTheLastLink, bool allowed)
    {
        var facts = Allowed() with { PaymentLastSentAt = Now.AddMinutes(-minutesSinceTheLastLink) };

        Assert.Equal(allowed, ServicePaymentActions.CanRequest(facts, Now));
    }

    [Fact]
    public void CanRequest_NeverSentYet_IsAllowed()
    {
        Assert.True(ServicePaymentActions.CanRequest(Allowed() with { PaymentLastSentAt = null }, Now));
    }

    // ─── Record it as paid outside CasaZen ───

    [Fact]
    public void CanRecordOffline_ASupplierThatMayNotAct_IsFalse()
    {
        Assert.False(ServicePaymentActions.CanRecordOffline(Allowed() with { SupplierMayAct = false }));
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto)]
    [InlineData(ServiceRequestStatus.PresoInCarico)]
    [InlineData(ServiceRequestStatus.InCorso)]
    [InlineData(ServiceRequestStatus.Pagato)]
    [InlineData(ServiceRequestStatus.Rifiutato)]
    [InlineData(ServiceRequestStatus.Annullato)]
    public void CanRecordOffline_OnlyACompletedRequest_BecausePagatoIsReachedFromCompletatoOnly(ServiceRequestStatus status)
    {
        Assert.False(ServicePaymentActions.CanRecordOffline(Allowed() with { RequestStatus = status, PaymentStatus = null }));
    }

    [Fact]
    public void CanRecordOffline_NeedsTheAmountTheSupplierDeclared()
    {
        Assert.False(ServicePaymentActions.CanRecordOffline(Allowed() with { FinalAmountCents = null }));
        Assert.True(ServicePaymentActions.CanRecordOffline(Allowed() with { FinalAmountCents = 1 }));
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Requested, true)]
    [InlineData(ServicePaymentStatus.Failed, true)]
    [InlineData(ServicePaymentStatus.Processing, false)]
    [InlineData(ServicePaymentStatus.Paid, false)]
    [InlineData(ServicePaymentStatus.PartiallyRefunded, false)]
    [InlineData(ServicePaymentStatus.Refunded, false)]
    [InlineData(ServicePaymentStatus.NeedsReview, false)]
    public void CanRecordOffline_AccordingToWhereThePaymentIs(ServicePaymentStatus status, bool allowed)
    {
        Assert.Equal(allowed, ServicePaymentActions.CanRecordOffline(Allowed() with { PaymentStatus = status }));
    }

    [Fact]
    public void CanRecordOffline_DoesNotDependOnTheFlagTheModeTheAccountTheAddressNorTheConfirmation()
    {
        // What the supplier needs when online payments are not available: a request paid by hand, a supplier with no Stripe account,
        // a host with no address, an amount nobody confirmed.
        var facts = Allowed() with
        {
            OnlinePaymentsEnabled = false,
            PaymentMode = ServiceRequestPaymentMode.Manual,
            SupplierCanReceivePayments = false,
            PayerHasEmail = false,
            FinalAmountNeedsConfirmation = true,
            PaymentStatus = null,
            PaymentLastSentAt = null,
        };

        Assert.True(ServicePaymentActions.CanRecordOffline(facts));
        Assert.False(ServicePaymentActions.CanRequest(facts, Now));
    }

    [Fact]
    public void Facts_AreRequired()
    {
        Assert.Throws<ArgumentNullException>(() => ServicePaymentActions.CanRequest(null!, Now));
        Assert.Throws<ArgumentNullException>(() => ServicePaymentActions.CanRecordOffline(null!));
    }
}
