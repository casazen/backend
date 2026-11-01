using Casazen.Core.BankStatements;
using Xunit;

namespace Casazen.Tests.Unit.BankStatements;

/// <summary>
/// The rules that match a credit of the statement to a rent installment: the amount to the cent, then what the reason of the payment says
/// (the tenant's surname, the property, the month) and who paid this lease before, and how close the day is to the due date. Only the sure
/// ones are offered for the one-click confirmation; an installment goes to one credit only.
/// </summary>
public class RentBankMatcherTests
{
    private static readonly Guid Org = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateOnly Today = new(2026, 10, 9);
    private static readonly Guid VerdiLease = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid RossiLease = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid OtherRossiLease = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    private static readonly byte[] PayerKey = BankPayerHash.DeriveKey("a-long-random-secret-of-the-server")!;

    private static int _installmentCounter;

    private static RentBankCandidate Installment(
        Guid lease,
        string period,
        decimal amount = 900m,
        string? due = null,
        string property = "Bilocale Sparano",
        string address = "Via Lunga 12",
        params (string First, string Last)[] tenants)
    {
        var start = DateOnly.Parse($"{period}-01");
        var id = new Guid(Interlocked.Increment(ref _installmentCounter), 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);
        return new RentBankCandidate(
            id,
            lease,
            Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"),
            property,
            address,
            start,
            start.AddMonths(1).AddDays(-1),
            due is null ? start.AddDays(4) : DateOnly.Parse(due),
            amount,
            tenants.Length == 0 ? [new RentBankTenant("Giulia", "Verdi")] : tenants.Select(t => new RentBankTenant(t.First, t.Last)).ToList());
    }

    private static RentBankCredit Credit(string description, string date = "2026-10-03", decimal amount = 900m, string? payer = null, string? iban = null, string currency = "EUR")
    {
        var day = DateOnly.Parse(date);
        var movement = new BankMovement(day, day, amount, currency, description, payer, iban, null);
        return new RentBankCredit(
            BankMovementFingerprint.ForAll(Org, [movement])[0],
            movement,
            BankPayerHash.Iban(Org, iban, PayerKey),
            BankPayerHash.Name(Org, payer, PayerKey));
    }

    private static RentBankMatching Match(IReadOnlyList<RentBankCredit> credits, IReadOnlyList<RentBankCandidate> candidates, params RentBankPayer[] payers) =>
        RentBankMatcher.Match(credits, candidates, payers, Today);

    [Fact]
    public void AmountSurnameFirstNameAndMonth_AreSure_AndTheReasonsAreNamed()
    {
        var october = Installment(VerdiLease, "2026-10");

        var result = Match([Credit("BONIFICO A VOSTRO FAVORE DA VERDI GIULIA CANONE OTTOBRE 2026")], [october]);

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(october, suggestion.Installment);
        Assert.Equal(RentBankConfidence.High, suggestion.Confidence);
        Assert.Equal(
            new[] { RentBankReason.Amount, RentBankReason.Surname, RentBankReason.FirstName, RentBankReason.Month },
            suggestion.Reasons.ToArray());
        Assert.Empty(result.Unmatched);
    }

    [Fact]
    public void TheSurnameAlone_WithOneInstallmentToCollect_IsSure()
    {
        var result = Match([Credit("Bonifico Verdi")], [Installment(VerdiLease, "2026-10")]);

        Assert.Equal(RentBankConfidence.High, Assert.Single(result.Suggestions).Confidence);
    }

    [Fact]
    public void ThePayerNamedByTheBank_IsTheSurnameToo()
    {
        var result = Match([Credit("Canone", payer: "VERDI GIULIA")], [Installment(VerdiLease, "2026-10")]);

        Assert.Equal(RentBankConfidence.High, Assert.Single(result.Suggestions).Confidence);
    }

    [Fact]
    public void ASurnameWrittenWithoutItsApostrophe_IsFound()
    {
        var installment = Installment(VerdiLease, "2026-10", tenants: [("Anna", "D'Angelo")]);

        var result = Match([Credit("Bonifico DANGELO ANNA")], [installment]);

        Assert.Equal(RentBankConfidence.High, Assert.Single(result.Suggestions).Confidence);
    }

    [Fact]
    public void ASurnameThatIsTooShort_IsNotTakenForASignal()
    {
        var installment = Installment(VerdiLease, "2026-10", tenants: [("Wei", "Li")]);

        var result = Match([Credit("pagamento li canone")], [installment]);

        Assert.Equal(RentBankConfidence.Low, Assert.Single(result.Suggestions).Confidence);
    }

    [Fact]
    public void OnlyTheAmount_IsLow_AndTheOtherInstallmentsOfTheSameAmountAreTheOptions()
    {
        var verdi = Installment(VerdiLease, "2026-10");
        var rossi = Installment(RossiLease, "2026-10", tenants: [("Mario", "Rossi")]);

        var result = Match([Credit("BONIFICO")], [verdi, rossi]);

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(RentBankConfidence.Low, suggestion.Confidence);
        Assert.Equal([RentBankReason.Amount], suggestion.Reasons.ToArray());
        Assert.Single(suggestion.Alternatives);
        Assert.Equal(new[] { verdi.InstallmentId, rossi.InstallmentId }.Order(), new[] { suggestion.Installment.InstallmentId, suggestion.Alternatives[0].InstallmentId }.Order());
    }

    [Fact]
    public void TwoLeasesWhoseTenantsShareASurname_AreNotSure()
    {
        var mario = Installment(RossiLease, "2026-10", tenants: [("Mario", "Rossi")]);
        var anna = Installment(OtherRossiLease, "2026-10", tenants: [("Anna", "Rossi")]);

        var result = Match([Credit("Bonifico Rossi canone ottobre")], [mario, anna]);

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(RentBankConfidence.Medium, suggestion.Confidence);
        Assert.Single(suggestion.Alternatives);
    }

    [Fact]
    public void TheFirstNameDecidesBetweenTwoLeasesOnlyIfItSeparatesThemEnough_NotWithJustTenPoints()
    {
        var mario = Installment(RossiLease, "2026-10", tenants: [("Mario", "Rossi")]);
        var anna = Installment(OtherRossiLease, "2026-10", tenants: [("Anna", "Rossi")]);

        var result = Match([Credit("Bonifico Rossi Mario canone ottobre")], [mario, anna]);

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(mario.InstallmentId, suggestion.Installment.InstallmentId);
        Assert.Equal(RentBankConfidence.Medium, suggestion.Confidence);
    }

    [Fact]
    public void SeveralInstallmentsOfTheSameAmountAndNoMonth_TheNearestDayIsProposed_ButNotSure()
    {
        var august = Installment(VerdiLease, "2026-08");
        var september = Installment(VerdiLease, "2026-09");
        var october = Installment(VerdiLease, "2026-10");

        var result = Match([Credit("Bonifico Verdi", date: "2026-10-03")], [august, september, october]);

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(october.InstallmentId, suggestion.Installment.InstallmentId);
        Assert.Equal(RentBankConfidence.Medium, suggestion.Confidence);
        Assert.Equal(2, suggestion.Alternatives.Count);
    }

    [Fact]
    public void TheMonthTheReasonNames_PicksTheInstallment_AndMakesItSure()
    {
        var august = Installment(VerdiLease, "2026-08");
        var september = Installment(VerdiLease, "2026-09");
        var october = Installment(VerdiLease, "2026-10");

        var result = Match([Credit("Bonifico Verdi canone settembre", date: "2026-10-03")], [august, september, october]);

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(september.InstallmentId, suggestion.Installment.InstallmentId);
        Assert.Equal(RentBankConfidence.High, suggestion.Confidence);
        Assert.Contains(RentBankReason.Month, suggestion.Reasons);
    }

    [Fact]
    public void TheMonthOfTheDateOfThePayment_IsNotTheMonthItIsFor()
    {
        var september = Installment(VerdiLease, "2026-09");
        var october = Installment(VerdiLease, "2026-10");

        // «del 03/10/2026» is the day of the payment: it must not make October look like the month named.
        var result = Match([Credit("Bonifico del 03/10/2026 Verdi canone settembre", date: "2026-10-03")], [september, october]);

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(september.InstallmentId, suggestion.Installment.InstallmentId);
        Assert.Equal(RentBankConfidence.High, suggestion.Confidence);
    }

    [Fact]
    public void AMonthThatIsNotTheInstallments_MakesItNotSure_EvenWithTheSurname()
    {
        var result = Match([Credit("Bonifico Verdi canone settembre")], [Installment(VerdiLease, "2026-10")]);

        Assert.Equal(RentBankConfidence.Medium, Assert.Single(result.Suggestions).Confidence);
    }

    [Fact]
    public void APayerSeenOnAConfirmedMatch_RecognizesTheLeaseWithNoNameInTheReason()
    {
        const string iban = "DE89370400440532013000";
        var installment = Installment(VerdiLease, "2026-10");

        var result = Match(
            [Credit("Canone", iban: iban)],
            [installment],
            new RentBankPayer(VerdiLease, BankPayerHash.Iban(Org, iban, PayerKey), null));

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(RentBankConfidence.High, suggestion.Confidence);
        Assert.Contains(RentBankReason.PayerIban, suggestion.Reasons);
    }

    [Fact]
    public void APayerNameSeenBefore_IsAsGoodAsTheSurname()
    {
        var installment = Installment(VerdiLease, "2026-10");

        var result = Match(
            [Credit("Canone", payer: "Bianchi Marco")],
            [installment],
            new RentBankPayer(VerdiLease, null, BankPayerHash.Name(Org, "MARCO BIANCHI", PayerKey)));

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(RentBankConfidence.High, suggestion.Confidence);
        Assert.Contains(RentBankReason.PayerName, suggestion.Reasons);
    }

    [Fact]
    public void ThePayerOfAnotherLease_IsNotTheOneOfThisLease()
    {
        const string iban = "DE89370400440532013000";
        var verdi = Installment(VerdiLease, "2026-10");
        var rossi = Installment(RossiLease, "2026-10", tenants: [("Mario", "Rossi")]);

        var result = Match(
            [Credit("Canone", iban: iban)],
            [verdi, rossi],
            new RentBankPayer(RossiLease, BankPayerHash.Iban(Org, iban, PayerKey), null));

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(rossi.InstallmentId, suggestion.Installment.InstallmentId);
    }

    [Fact]
    public void ThePropertyInTheReason_IsNotEnoughToBeSure()
    {
        var result = Match([Credit("Affitto via Lunga 12 ottobre")], [Installment(VerdiLease, "2026-10")]);

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(RentBankConfidence.Medium, suggestion.Confidence);
        Assert.Contains(RentBankReason.Property, suggestion.Reasons);
    }

    [Fact]
    public void AnAmountThatDiffersByAnyCent_IsUnmatched()
    {
        var result = Match([Credit("Bonifico Verdi", amount: 899.99m)], [Installment(VerdiLease, "2026-10")]);

        Assert.Empty(result.Suggestions);
        Assert.Single(result.Unmatched);
    }

    [Fact]
    public void ACreditInAnotherCurrency_IsUnmatched()
    {
        var result = Match([Credit("Bonifico Verdi", currency: "USD")], [Installment(VerdiLease, "2026-10")]);

        Assert.Empty(result.Suggestions);
        Assert.Single(result.Unmatched);
    }

    [Theory]
    [InlineData("2024-10-05", "2026-10-03")] // paid two years after the due date
    [InlineData("2027-01-05", "2026-10-03")] // due more than 45 days after the payment
    public void APaymentFarFromTheDueDate_IsNotForThatInstallment(string due, string paid)
    {
        var result = Match([Credit("Bonifico Verdi", date: paid)], [Installment(VerdiLease, "2026-10", due: due)]);

        Assert.Empty(result.Suggestions);
        Assert.Single(result.Unmatched);
    }

    [Theory]
    [InlineData("2026-10-05", "2026-09-21")] // 14 days early
    [InlineData("2026-10-05", "2026-08-25")] // 41 days early: the next month paid in advance
    [InlineData("2026-10-05", "2026-10-05")]
    [InlineData("2026-03-05", "2026-10-03")] // arrears of seven months
    public void APaymentNearOrAfterTheDueDate_CanBeForTheInstallment(string due, string paid)
    {
        var result = Match([Credit("Bonifico Verdi", date: paid)], [Installment(VerdiLease, "2026-10", due: due)]);

        Assert.Single(result.Suggestions);
    }

    [Theory]
    [InlineData("2026-10-05", "2026-09-25", RentBankConfidence.High)] // ten days early: how a rent is paid
    [InlineData("2026-10-05", "2026-09-24", RentBankConfidence.Medium)] // eleven days early and no month named: it may be for the month before
    [InlineData("2026-11-05", "2026-10-03", RentBankConfidence.Medium)] // October declared by hand, the credit of October is not November's
    public void ACreditWellBeforeTheDueDate_IsNotSure_WhenTheReasonDoesNotNameTheMonth(string due, string paid, RentBankConfidence expected)
    {
        var result = Match([Credit("Bonifico Verdi", date: paid)], [Installment(VerdiLease, due[..7], due: due)]);

        Assert.Equal(expected, Assert.Single(result.Suggestions).Confidence);
    }

    [Fact]
    public void ACreditWellBeforeTheDueDate_IsSure_WhenTheReasonNamesTheMonth()
    {
        var result = Match([Credit("Bonifico Verdi canone novembre", date: "2026-10-03")], [Installment(VerdiLease, "2026-11", due: "2026-11-05")]);

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal(RentBankConfidence.High, suggestion.Confidence);
        Assert.Contains(RentBankReason.Month, suggestion.Reasons);
    }

    [Fact]
    public void AnArrearsOfMoreThanFourMonths_IsNeverSure()
    {
        var result = Match([Credit("Bonifico Verdi", date: "2026-10-03")], [Installment(VerdiLease, "2026-04", due: "2026-04-05")]);

        Assert.Equal(RentBankConfidence.Medium, Assert.Single(result.Suggestions).Confidence);
    }

    [Fact]
    public void AValueDateAfterToday_IsTreatedAsToday()
    {
        var result = Match([Credit("Bonifico Verdi", date: "2026-10-12")], [Installment(VerdiLease, "2026-10")]);

        Assert.Equal(RentBankConfidence.High, Assert.Single(result.Suggestions).Confidence);
    }

    [Fact]
    public void AnInstallment_GoesToOneCreditOnly_AndTheOtherIsLeftOver()
    {
        var october = Installment(VerdiLease, "2026-10");

        var result = Match([Credit("Bonifico Verdi", date: "2026-10-03"), Credit("Bonifico Verdi ancora", date: "2026-10-04")], [october]);

        var suggestion = Assert.Single(result.Suggestions);
        Assert.Equal("2026-10-03", suggestion.Credit.Movement.ValueDate.ToString("yyyy-MM-dd"));
        Assert.Single(result.Unmatched);
    }

    [Fact]
    public void ACreditWhoseInstallmentWentToASurerOne_FallsBackToTheNext_AndIsNeverSure()
    {
        var october = Installment(VerdiLease, "2026-10");
        var november = Installment(VerdiLease, "2026-11");

        var result = Match(
            [Credit("Bonifico Verdi ottobre", date: "2026-10-03"), Credit("Bonifico Verdi", date: "2026-10-04")],
            [october, november]);

        Assert.Equal(2, result.Suggestions.Count);
        var sure = result.Suggestions.Single(s => s.Credit.Movement.Description == "Bonifico Verdi ottobre");
        var displaced = result.Suggestions.Single(s => s.Credit.Movement.Description == "Bonifico Verdi");
        Assert.Equal(october.InstallmentId, sure.Installment.InstallmentId);
        Assert.Equal(RentBankConfidence.High, sure.Confidence);
        Assert.Equal(november.InstallmentId, displaced.Installment.InstallmentId);
        Assert.NotEqual(RentBankConfidence.High, displaced.Confidence);
    }

    [Fact]
    public void ALeaseWithNoNamedTenant_HasNoIdentityToRecognize()
    {
        var installment = Installment(VerdiLease, "2026-10", tenants: []);
        var anonymous = installment with { Tenants = [] };

        var result = Match([Credit("Bonifico Verdi")], [anonymous]);

        Assert.Equal(RentBankConfidence.Low, Assert.Single(result.Suggestions).Confidence);
    }

    [Fact]
    public void TheSuggestions_AreSureFirst_ThenByDate()
    {
        var verdi = Installment(VerdiLease, "2026-10");
        var rossi = Installment(RossiLease, "2026-10", tenants: [("Mario", "Rossi")]);

        var result = Match([Credit("Bonifico", date: "2026-10-01"), Credit("Bonifico Rossi ottobre", date: "2026-10-04")], [verdi, rossi]);

        Assert.Equal(
            new[] { RentBankConfidence.High, RentBankConfidence.Low },
            result.Suggestions.Select(s => s.Confidence).ToArray());
    }

    [Fact]
    public void NoInstallmentsAtAll_LeavesEveryCreditUnmatched()
    {
        var result = Match([Credit("Bonifico Verdi"), Credit("Altro", amount: 12m)], []);

        Assert.Empty(result.Suggestions);
        Assert.Equal(2, result.Unmatched.Count);
    }
}
