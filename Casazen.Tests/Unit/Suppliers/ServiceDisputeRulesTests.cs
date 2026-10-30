using System.Collections;
using System.Globalization;
using System.Resources;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Suppliers;
using Casazen.Web.Resources;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>
/// SP-15c-c (decision VT-D16): the pure rules of the complaint and the dispute about a job paid through CasaZen. State by action, the amounts of a
/// decision (the price, the commission, the supplier's part, the refund), the window in which a payer may complain and what the penalty is. No clock,
/// no database, no Stripe: every combination is a row.
/// </summary>
public class ServiceDisputeRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);

    private static DisputeReportFacts Facts(
        ServicePaymentStatus payment = ServicePaymentStatus.Held,
        ServiceRequestStatus request = ServiceRequestStatus.Completato,
        int releaseInMinutes = 30,
        bool confirmed = false,
        int? endedMinutesAgo = null,
        bool open = false) =>
        new(
            payment,
            request is ServiceRequestStatus.Completato or ServiceRequestStatus.Pagato ? Now.AddMinutes(releaseInMinutes) : null,
            confirmed ? Now.AddMinutes(-5) : null,
            request,
            endedMinutesAgo is { } ago ? Now.AddMinutes(-ago) : null,
            open);

    // ─── Whether a payer may complain ───

    [Fact]
    public void CanReport_ACompletedJob_InsideTheWindow_IsAllowed()
    {
        Assert.Equal(DisputeReportVerdict.Allowed, ServiceDisputeRules.CanReport(Facts(), Now));
        Assert.Equal(DisputeReportVerdict.Allowed, ServiceDisputeRules.CanReport(Facts(releaseInMinutes: 1), Now));
    }

    [Fact]
    public void CanReport_FromTheMomentTheMoneyIsDue_TheWindowIsClosed()
    {
        Assert.Equal(DisputeReportVerdict.WindowClosed, ServiceDisputeRules.CanReport(Facts(releaseInMinutes: 0), Now));
        Assert.Equal(DisputeReportVerdict.WindowClosed, ServiceDisputeRules.CanReport(Facts(releaseInMinutes: -30), Now));
    }

    [Fact]
    public void CanReport_AfterTheWordAllFine_TheWindowIsClosed_EvenIfTheTimeHasNotCome()
    {
        Assert.Equal(DisputeReportVerdict.WindowClosed, ServiceDisputeRules.CanReport(Facts(releaseInMinutes: 30, confirmed: true), Now));
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Released)]
    [InlineData(ServicePaymentStatus.TransferFailed)]
    public void CanReport_OnceTheMoneyWasReleasedOrIsBeingReleased_TheWindowIsClosed(ServicePaymentStatus payment)
    {
        Assert.Equal(DisputeReportVerdict.WindowClosed, ServiceDisputeRules.CanReport(Facts(payment), Now));
    }

    [Theory]
    [InlineData(ServicePaymentStatus.Requested)]
    [InlineData(ServicePaymentStatus.Authorized)]
    [InlineData(ServicePaymentStatus.Canceled)]
    [InlineData(ServicePaymentStatus.PartiallyRefunded)]
    [InlineData(ServicePaymentStatus.Refunded)]
    [InlineData(ServicePaymentStatus.NeedsReview)]
    [InlineData(ServicePaymentStatus.Disputed)]
    public void CanReport_WithNoMoneyHeldByCasaZenToStop_ThereIsNothingToReport(ServicePaymentStatus payment)
    {
        Assert.Equal(DisputeReportVerdict.NotReportable, ServiceDisputeRules.CanReport(Facts(payment), Now));
    }

    [Fact]
    public void CanReport_ADisputeAlreadyOpen_IsTheSameOne_WhateverElseIsTrue()
    {
        Assert.Equal(DisputeReportVerdict.AlreadyOpen, ServiceDisputeRules.CanReport(Facts(ServicePaymentStatus.Disputed, open: true), Now));
        Assert.Equal(DisputeReportVerdict.AlreadyOpen, ServiceDisputeRules.CanReport(Facts(releaseInMinutes: -60, open: true), Now));
    }

    [Theory]
    [InlineData(ServiceRequestStatus.PresoInCarico)]
    [InlineData(ServiceRequestStatus.InCorso)]
    public void CanReport_AJobNobodyMarkedCompleted_CanBeReportedOnlyOnceItsTimeHasEnded(ServiceRequestStatus request)
    {
        Assert.Equal(DisputeReportVerdict.TooEarly, ServiceDisputeRules.CanReport(Facts(request: request, endedMinutesAgo: -60), Now));
        Assert.Equal(DisputeReportVerdict.TooEarly, ServiceDisputeRules.CanReport(Facts(request: request), Now)); // no time was agreed
        Assert.Equal(DisputeReportVerdict.AllowedNoShow, ServiceDisputeRules.CanReport(Facts(request: request, endedMinutesAgo: 0), Now));
        Assert.Equal(DisputeReportVerdict.AllowedNoShow, ServiceDisputeRules.CanReport(Facts(request: request, endedMinutesAgo: 600), Now));
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto)]
    [InlineData(ServiceRequestStatus.Rifiutato)]
    [InlineData(ServiceRequestStatus.Annullato)]
    public void CanReport_ARequestThatWasNotTaken_HasNothingToReport(ServiceRequestStatus request)
    {
        Assert.Equal(DisputeReportVerdict.NotReportable, ServiceDisputeRules.CanReport(Facts(request: request, endedMinutesAgo: 600), Now));
    }

    [Fact]
    public void CanReport_APaidRequest_IsPastTheWindow()
    {
        Assert.Equal(DisputeReportVerdict.WindowClosed, ServiceDisputeRules.CanReport(Facts(request: ServiceRequestStatus.Pagato), Now));
    }

    [Fact]
    public void ReportableUntil_IsWhenTheMoneyIsDue_WhileTheJobIsCompletedAndNobodySaidAllFine()
    {
        Assert.Equal(Now.AddMinutes(30), ServiceDisputeRules.ReportableUntil(Facts()));
        Assert.Null(ServiceDisputeRules.ReportableUntil(Facts(confirmed: true)));
        Assert.Null(ServiceDisputeRules.ReportableUntil(Facts(open: true)));
        Assert.Null(ServiceDisputeRules.ReportableUntil(Facts(ServicePaymentStatus.Released)));
        Assert.Null(ServiceDisputeRules.ReportableUntil(Facts(request: ServiceRequestStatus.InCorso, endedMinutesAgo: 5)));
    }

    // ─── Which state allows which action ───

    [Theory]
    [InlineData(ServiceDisputeStatus.AwaitingSupplier, true, false, true, true, false)]
    [InlineData(ServiceDisputeStatus.AwaitingSupplier, true, true, false, true, false)]
    [InlineData(ServiceDisputeStatus.AwaitingDecision, true, false, true, true, true)]
    [InlineData(ServiceDisputeStatus.AwaitingDecision, true, true, false, true, true)]
    [InlineData(ServiceDisputeStatus.ResolvedForSupplier, false, false, false, false, false)]
    [InlineData(ServiceDisputeStatus.ResolvedForCustomer, false, false, false, false, false)]
    [InlineData(ServiceDisputeStatus.Withdrawn, false, false, false, false, false)]
    public void TheStateDecidesWhatCanBeDone(
        ServiceDisputeStatus status, bool open, bool replied, bool canReply, bool canWithdraw, bool canResolve)
    {
        Assert.Equal(open, ServiceDisputeRules.IsOpen(status));
        Assert.Equal(!open, ServiceDisputeRules.IsResolved(status));
        Assert.Equal(canReply && !replied && open, ServiceDisputeRules.CanReply(status, replied));
        Assert.Equal(canWithdraw && open, ServiceDisputeRules.CanWithdraw(status));
        Assert.Equal(canResolve, ServiceDisputeRules.CanResolve(status));
    }

    // ─── The money of a decision ───

    [Theory]
    [InlineData(5_400, 0, 0, 5_400)] // the price 6_000 minus the commission 600: the supplier's whole part
    [InlineData(5_400, 1_000, 0, 4_400)]
    [InlineData(5_400, 1_000, 400, 4_000)] // a refund on its way is spoken for
    [InlineData(5_400, 5_400, 0, 0)]
    [InlineData(5_400, 6_000, 0, 0)] // never negative
    public void RefundableToPayer_IsTheSuppliersPartThatIsLeft_NeverTheCommission(int net, int refunded, int inProgress, int expected)
    {
        Assert.Equal(expected, ServiceDisputeRules.RefundableToPayer(net, refunded, inProgress));
    }

    [Fact]
    public void DecideRefund_NothingAskedFor_IsEverythingThatIsLeft()
    {
        Assert.Equal(new DisputeRefundDecision(DisputeRefundKind.Refund, 5_400), ServiceDisputeRules.DecideRefund(null, 5_400));
    }

    [Theory]
    [InlineData(1, 5_400)]
    [InlineData(2_700, 5_400)]
    [InlineData(5_400, 5_400)]
    public void DecideRefund_AnAmountFromOneCentToTheSuppliersPart_IsGivenAsItIs(int asked, int left)
    {
        Assert.Equal(new DisputeRefundDecision(DisputeRefundKind.Refund, asked), ServiceDisputeRules.DecideRefund(asked, left));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-5_400)]
    public void DecideRefund_ZeroOrLess_IsNotAnAmount(int asked)
    {
        Assert.Equal(DisputeRefundKind.Invalid, ServiceDisputeRules.DecideRefund(asked, 5_400).Kind);
    }

    [Theory]
    [InlineData(5_401)]
    [InlineData(6_000)] // the whole price: the commission is never given back by a dispute
    public void DecideRefund_MoreThanTheSuppliersPart_IsRefused_AndTellsTheMost(int asked)
    {
        var decision = ServiceDisputeRules.DecideRefund(asked, 5_400);

        Assert.Equal(DisputeRefundKind.Exceeds, decision.Kind);
        Assert.Equal(5_400, decision.AmountCents);
    }

    [Fact]
    public void DecideRefund_WhenNothingIsLeft_TheDecisionStandsWithNoMoney_AndAnAmountIsRefused()
    {
        Assert.Equal(new DisputeRefundDecision(DisputeRefundKind.NothingLeft, 0), ServiceDisputeRules.DecideRefund(null, 0));
        Assert.Equal(DisputeRefundKind.Exceeds, ServiceDisputeRules.DecideRefund(100, 0).Kind);
    }

    [Theory]
    [InlineData(0, 0, 4_000, 4_000)]
    [InlineData(1_000, 0, 4_000, 5_000)] // a refund made before counts toward what the payment must have given back
    [InlineData(1_000, 400, 4_000, 5_400)]
    public void RefundTarget_IsWhatThePaymentMustHaveGivenBackForTheDecisionToBeDone(int refunded, int inProgress, int decided, int expected)
    {
        Assert.Equal(expected, ServiceDisputeRules.RefundTarget(refunded, inProgress, decided));
    }

    [Theory]
    [InlineData(4_000, 4_000, true)]
    [InlineData(4_001, 4_000, true)]
    [InlineData(3_999, 4_000, false)]
    [InlineData(0, 4_000, false)]
    public void RefundDone_WhenThePaymentHasGivenBackEverythingTheDecisionAsked(int refunded, int target, bool expected)
    {
        Assert.Equal(expected, ServiceDisputeRules.RefundDone(refunded, target));
    }

    // ─── The time ───

    [Fact]
    public void TheTimesAreCountedFromTheMomentTheyStart()
    {
        Assert.Equal(Now.AddHours(48), ServiceDisputeRules.SupplierReplyDueAt(Now, TimeSpan.FromHours(48)));
        Assert.Equal(Now.AddHours(72), ServiceDisputeRules.DecisionDueAt(Now, TimeSpan.FromHours(72)));
        Assert.Equal(Now.AddHours(48), ServiceDisputeRules.NoShowOpensAt(Now, TimeSpan.FromHours(48)));
    }

    [Theory]
    [InlineData(ServiceDisputeStatus.AwaitingSupplier, -1, true)]
    [InlineData(ServiceDisputeStatus.AwaitingSupplier, 0, true)]
    [InlineData(ServiceDisputeStatus.AwaitingSupplier, 1, false)]
    [InlineData(ServiceDisputeStatus.AwaitingDecision, -60, false)] // it went to the admins already
    [InlineData(ServiceDisputeStatus.ResolvedForSupplier, -60, false)]
    public void ReplyTermPassed_OnlyForADisputeStillWaitingForTheSupplier(ServiceDisputeStatus status, int dueInMinutes, bool expected)
    {
        Assert.Equal(expected, ServiceDisputeRules.ReplyTermPassed(status, Now.AddMinutes(dueInMinutes), Now));
    }

    [Fact]
    public void DecisionOverdue_IsTrueOnce_ForADisputeThatWaitsForAnAdmin()
    {
        Assert.True(ServiceDisputeRules.DecisionOverdue(ServiceDisputeStatus.AwaitingDecision, Now.AddMinutes(-1), null, Now));
        Assert.True(ServiceDisputeRules.DecisionOverdue(ServiceDisputeStatus.AwaitingDecision, Now, null, Now));
        Assert.False(ServiceDisputeRules.DecisionOverdue(ServiceDisputeStatus.AwaitingDecision, Now.AddMinutes(1), null, Now));
        Assert.False(ServiceDisputeRules.DecisionOverdue(ServiceDisputeStatus.AwaitingDecision, Now.AddMinutes(-1), Now.AddMinutes(-1), Now)); // told already
        Assert.False(ServiceDisputeRules.DecisionOverdue(ServiceDisputeStatus.AwaitingSupplier, Now.AddMinutes(-1), null, Now));
        Assert.False(ServiceDisputeRules.DecisionOverdue(ServiceDisputeStatus.AwaitingDecision, null, null, Now));
    }

    [Theory]
    [InlineData(ServiceDisputeStatus.ResolvedForCustomer, 40, null, null, 100, 4_000, 0, true)]
    [InlineData(ServiceDisputeStatus.ResolvedForCustomer, 10, null, null, 100, 4_000, 0, false)] // too soon
    [InlineData(ServiceDisputeStatus.ResolvedForCustomer, 40, 1, null, 100, 4_000, 0, false)] // settled
    [InlineData(ServiceDisputeStatus.ResolvedForCustomer, 40, null, 1, 100, 4_000, 0, false)] // told already
    [InlineData(ServiceDisputeStatus.ResolvedForCustomer, 40, null, null, 100, 4_000, 400, false)] // a refund is waiting for Stripe
    [InlineData(ServiceDisputeStatus.ResolvedForCustomer, 40, null, null, 4_000, 4_000, 0, false)] // done
    [InlineData(ServiceDisputeStatus.ResolvedForSupplier, 40, null, null, 0, 0, 0, false)]
    public void RefundAlertDue_OnlyWhenTheDecidedRefundDidNotGoThrough_AndNothingIsWaitingForStripe(
        ServiceDisputeStatus status,
        int resolvedMinutesAgo,
        int? settled,
        int? alerted,
        int refunded,
        int target,
        int inProgress,
        bool expected)
    {
        var result = ServiceDisputeRules.RefundAlertDue(
            status,
            Now.AddMinutes(-resolvedMinutesAgo),
            settled is null ? null : Now,
            alerted is null ? null : Now,
            refunded,
            target,
            inProgress,
            TimeSpan.FromMinutes(30),
            Now);

        Assert.Equal(expected, result);
    }

    // ─── The penalty ───

    [Theory]
    [InlineData(ServiceDisputeStatus.ResolvedForCustomer, false, SupplierDisputePenaltyKind.DisputeLost)]
    [InlineData(ServiceDisputeStatus.ResolvedForCustomer, true, SupplierDisputePenaltyKind.NoShow)]
    public void PenaltyFor_ADisputeDecidedForTheCustomer_CountsAgainstTheSupplier(ServiceDisputeStatus outcome, bool noShow, SupplierDisputePenaltyKind expected)
    {
        Assert.Equal(expected, ServiceDisputeRules.PenaltyFor(outcome, noShow));
    }

    [Theory]
    [InlineData(ServiceDisputeStatus.ResolvedForSupplier)]
    [InlineData(ServiceDisputeStatus.Withdrawn)]
    [InlineData(ServiceDisputeStatus.AwaitingDecision)]
    public void PenaltyFor_NothingElseCostsTheSupplierAnything(ServiceDisputeStatus outcome)
    {
        Assert.Null(ServiceDisputeRules.PenaltyFor(outcome, noShow: false));
        Assert.Null(ServiceDisputeRules.PenaltyFor(outcome, noShow: true));
    }

    // ─── Names, words ───

    [Theory]
    [InlineData("not_done", ServiceDisputeReason.NotDone)]
    [InlineData("incomplete", ServiceDisputeReason.Incomplete)]
    [InlineData("poor_quality", ServiceDisputeReason.PoorQuality)]
    [InlineData("not_as_described", ServiceDisputeReason.NotAsDescribed)]
    [InlineData("other", ServiceDisputeReason.Other)]
    [InlineData(" other ", ServiceDisputeReason.Other)]
    public void TryParseReason_ReadsTheFiveNamesOfTheApi(string name, ServiceDisputeReason expected)
    {
        Assert.True(ServiceDisputeRules.TryParseReason(name, out var reason));
        Assert.Equal(expected, reason);
        Assert.Equal(name.Trim(), ServiceDisputeRules.ReasonName(reason));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NotDone")]
    [InlineData("NOT_DONE")]
    [InlineData("0")]
    [InlineData("broken")]
    public void TryParseReason_AnythingElseIsNotAReason(string? name)
    {
        Assert.False(ServiceDisputeRules.TryParseReason(name, out _));
    }

    [Fact]
    public void TheReasonsAreExactlyTheFiveOfTheSpec()
    {
        Assert.Equal(new[] { "not_done", "incomplete", "poor_quality", "not_as_described", "other" }, ServiceDisputeRules.ReasonNames);
    }

    [Theory]
    [InlineData("for_supplier", ServiceDisputeStatus.ResolvedForSupplier, true)]
    [InlineData("for_customer", ServiceDisputeStatus.ResolvedForCustomer, true)]
    [InlineData(" for_customer ", ServiceDisputeStatus.ResolvedForCustomer, true)]
    [InlineData("withdrawn", default(ServiceDisputeStatus), false)]
    [InlineData("ForSupplier", default(ServiceDisputeStatus), false)]
    [InlineData("", default(ServiceDisputeStatus), false)]
    [InlineData(null, default(ServiceDisputeStatus), false)]
    public void TryParseOutcome_AnAdminDecidesForTheSupplierOrForTheCustomer_NothingElse(string? name, ServiceDisputeStatus expected, bool ok)
    {
        Assert.Equal(ok, ServiceDisputeRules.TryParseOutcome(name, out var outcome));
        if (ok)
            Assert.Equal(expected, outcome);
    }

    [Theory]
    [InlineData("  il lavoro non e stato fatto  ", "il lavoro non e stato fatto")]
    [InlineData("riga uno\r\nriga due", "riga uno\nriga due")]
    [InlineData("riga uno\rriga due", "riga uno\nriga due")]
    public void NormalizeText_TrimsAndKeepsTheLinesOfThePerson(string typed, string expected)
    {
        Assert.Equal(expected, ServiceDisputeRules.NormalizeText(typed, required: true, "text"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeText_NothingTyped_IsNullWhenOptional_AndTheFieldWhenRequired(string? typed)
    {
        Assert.Null(ServiceDisputeRules.NormalizeText(typed, required: false, "text"));

        var refusal = Assert.Throws<DomainRuleException>(() => ServiceDisputeRules.NormalizeText(typed, required: true, "text"));
        Assert.Equal("service_dispute_invalid", refusal.Code);
        Assert.Equal(new[] { "text" }, Assert.IsType<string[]>(refusal.Extensions["fields"]));
    }

    [Fact]
    public void NormalizeText_TheLongestTextIsAccepted_OneCharacterMoreIsRefused()
    {
        var longest = new string('a', ServiceDisputeLimits.TextMaxLength);

        Assert.Equal(longest, ServiceDisputeRules.NormalizeText(longest, required: false, "text"));
        var refusal = Assert.Throws<DomainRuleException>(() => ServiceDisputeRules.NormalizeText(longest + "a", required: false, "note"));
        Assert.Equal("service_dispute_invalid", refusal.Code);
        Assert.Equal(new[] { "note" }, Assert.IsType<string[]>(refusal.Extensions["fields"]));
    }

    [Theory]
    [InlineData("ciao\u0000")]
    [InlineData("ciao\u0007mondo")]
    [InlineData("ciao\u001b[31m")]
    public void NormalizeText_ControlCharactersAreRefused(string typed)
    {
        Assert.Throws<DomainRuleException>(() => ServiceDisputeRules.NormalizeText(typed, required: false, "text"));
    }

    [Fact]
    public void NormalizeText_TabsAreAllowed()
    {
        Assert.Equal("a\tb", ServiceDisputeRules.NormalizeText("a\tb", required: false, "text"));
    }

    // ─── The errors ───

    [Fact]
    public void TheCodesAreSnakeCase_AndEveryKeyOfTheAreaExistsInItalianAndEnglishWithDifferentText()
    {
        var italian = ReadEntries(CultureInfo.InvariantCulture);
        var english = ReadEntries(new CultureInfo("en"));

        var keys = ServiceDisputeErrors.MessageKeys;
        Assert.Equal(14, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.All(keys, key =>
        {
            Assert.True(italian.ContainsKey(key), $"{key} is missing from SharedResources.resx");
            Assert.True(english.ContainsKey(key), $"{key} is missing from SharedResources.en.resx");
            Assert.False(string.IsNullOrWhiteSpace(italian[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(english[key]), key);
            Assert.NotEqual(italian[key], english[key]);
        });

        var codes = new[]
        {
            ServiceDisputeErrors.NotFound, ServiceDisputeErrors.NotReportable, ServiceDisputeErrors.TooEarly, ServiceDisputeErrors.WindowClosed,
            ServiceDisputeErrors.Invalid, ServiceDisputeErrors.NotOpen, ServiceDisputeErrors.AlreadyReplied, ServiceDisputeErrors.AwaitingSupplier,
            ServiceDisputeErrors.StateChanged, ServiceDisputeErrors.PaymentChanged, ServiceDisputeErrors.RefundInvalid, ServiceDisputeErrors.RefundExceeds,
            ServiceDisputeErrors.OutcomeInvalid,
        };
        Assert.Equal(codes.Length, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches("^service_dispute(_[a-z]+)+$", code));
    }

    [Fact]
    public void TheErrorsCarryWhatTheClientNeedsToAct()
    {
        var closed = ServiceDisputeErrors.ReportWindowClosed(new DateTime(2026, 10, 30, 11, 0, 0, DateTimeKind.Unspecified));
        Assert.Equal(DateTimeKind.Utc, Assert.IsType<DateTime>(closed.Extensions["windowEndedAt"]).Kind);

        Assert.Empty(ServiceDisputeErrors.ReportWindowClosed(null).Extensions);
        Assert.Equal(5_400, ServiceDisputeErrors.RefundAmountExceeds(5_400).Extensions["maxRefundCents"]);
        Assert.True(ServiceDisputeErrors.SupplierStillHasTime(Now).Extensions.ContainsKey("supplierReplyDueAt"));
        Assert.Equal(new[] { "reason", "text" }, Assert.IsType<string[]>(ServiceDisputeErrors.InvalidFields("reason", "text").Extensions["fields"]));
    }

    [Fact]
    public void TheNamesOfTheOpenersAreLowercase()
    {
        Assert.Equal("customer", ServiceDisputeRules.OpenerName(ServiceDisputeOpener.Customer));
        Assert.Equal("host", ServiceDisputeRules.OpenerName(ServiceDisputeOpener.Host));
        Assert.Equal("system", ServiceDisputeRules.OpenerName(ServiceDisputeOpener.System));
    }

    private static Dictionary<string, string> ReadEntries(CultureInfo culture)
    {
        var manager = new ResourceManager(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly);
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException($"No SharedResources for {culture.Name}");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }
}
