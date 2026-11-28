using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases;

public sealed record RentComponentDraft(RentLedgerComponentKind Kind, decimal Amount, string? Description = null);

public static class RentLedgerMoney
{
    public static IReadOnlyList<decimal> SplitRemainderOnLast(decimal total, int installments)
    {
        if (installments < 1)
            throw new ArgumentOutOfRangeException(nameof(installments), installments, "At least one installment is required.");

        var cents = ToCents(total);
        if (cents < 0)
            throw new ArgumentOutOfRangeException(nameof(total), total, "The amount to split cannot be negative.");

        var baseCents = cents / installments;
        var remainder = cents - (baseCents * installments);
        var parts = new decimal[installments];
        for (var i = 0; i < installments; i++)
            parts[i] = FromCents(baseCents);

        parts[^1] = FromCents(baseCents + remainder);
        return parts;
    }

    public static decimal UniformAdvance(decimal amountPerAdvanceCadence, RentCadence advanceCadence, RentCadence rentCadence)
    {
        var advanceMonths = RentInstallmentPlan.MonthsPer(advanceCadence);
        var rentMonths = RentInstallmentPlan.MonthsPer(rentCadence);
        if (rentMonths == advanceMonths)
            return amountPerAdvanceCadence;

        if (rentMonths > advanceMonths && rentMonths % advanceMonths == 0)
        {
            var factor = rentMonths / advanceMonths;
            var cents = ToCents(amountPerAdvanceCadence) * factor;
            if (cents < 0)
                throw new OverflowException("The scaled advance is negative.");

            return FromCents(cents);
        }

        throw new ArgumentOutOfRangeException(
            nameof(rentCadence),
            rentCadence,
            "The advance cadence does not divide the rent cadence into equal cents. Split the advance with the remainder on the last installment.");
    }

    public static long ToCents(decimal amount)
    {
        if (amount != decimal.Round(amount, 2, MidpointRounding.AwayFromZero))
            throw new ArgumentException("The amount must be a whole number of cents.", nameof(amount));

        return decimal.ToInt64(amount * 100m);
    }

    public static decimal FromCents(long cents) => cents / 100m;
}

public static class RentLedgerLine
{
    public static decimal Sum(IReadOnlyList<RentComponentDraft> components)
    {
        ArgumentNullException.ThrowIfNull(components);
        if (components.Count == 0)
            throw new ArgumentException("A rent line needs at least one component.", nameof(components));

        decimal total = 0m;
        foreach (var component in components)
        {
            _ = RentLedgerMoney.ToCents(component.Amount);
            total += component.Amount;
        }

        return total;
    }

    public static void EnsureAmountDue(decimal amountDue, IReadOnlyList<RentComponentDraft> components)
    {
        var sum = Sum(components);
        if (amountDue != sum)
        {
            throw new InvalidOperationException(
                $"AmountDue {amountDue} must equal the sum of the components ({sum}).");
        }
    }

    public static IReadOnlyList<RentComponentDraft> FixedContract(
        decimal rent, decimal condominiumAdvance, decimal settlement = 0m, decimal other = 0m)
    {
        var components = new List<RentComponentDraft>(4) { new(RentLedgerComponentKind.Rent, rent) };
        if (condominiumAdvance != 0m)
            components.Add(new RentComponentDraft(RentLedgerComponentKind.CondominiumAdvance, condominiumAdvance));
        if (settlement != 0m)
            components.Add(new RentComponentDraft(RentLedgerComponentKind.CondominiumSettlement, settlement));
        if (other != 0m)
            components.Add(new RentComponentDraft(RentLedgerComponentKind.Other, other));

        _ = Sum(components);
        return components;
    }

    public static IReadOnlyList<RentComponentDraft> SingleRent(decimal amountDue) =>
        FixedContract(amountDue, 0m);

    public static decimal InstallmentTotalAfterTerms(
        decimal currentTotal, decimal rentPortion, decimal previousAdvance, decimal nextAdvance)
    {
        _ = RentLedgerMoney.ToCents(currentTotal);
        _ = RentLedgerMoney.ToCents(rentPortion);
        _ = RentLedgerMoney.ToCents(previousAdvance);
        _ = RentLedgerMoney.ToCents(nextAdvance);
        if (rentPortion < 0m || previousAdvance < 0m || nextAdvance < 0m)
            throw new ArgumentOutOfRangeException(nameof(rentPortion), "Rent and advances cannot be negative.");

        var nextTotal = rentPortion + nextAdvance;
        if (currentTotal == rentPortion
            || currentTotal == rentPortion + previousAdvance
            || currentTotal == nextTotal)
            return nextTotal;

        return currentTotal;
    }

    /// <summary>
    /// Fits <paramref name="rentPortion"/> and <paramref name="chargeableAdvance"/> into an installment whose total is already
    /// <paramref name="amountDue"/>. When the total is exactly rent plus advance, those two components are the line. A larger
    /// total keeps the difference as <see cref="RentLedgerComponentKind.Other"/>. A smaller total keeps the rent first and the
    /// advance only for what is left, so the stored total is never rewritten here.
    /// </summary>
    public static IReadOnlyList<RentComponentDraft> ForStoredTotal(
        decimal amountDue, decimal rentPortion, decimal chargeableAdvance)
    {
        _ = RentLedgerMoney.ToCents(amountDue);
        _ = RentLedgerMoney.ToCents(rentPortion);
        _ = RentLedgerMoney.ToCents(chargeableAdvance);
        if (chargeableAdvance < 0m)
            throw new ArgumentOutOfRangeException(nameof(chargeableAdvance), chargeableAdvance, "The advance cannot be negative.");

        if (chargeableAdvance == 0m)
            return SingleRent(amountDue);

        if (amountDue == rentPortion + chargeableAdvance)
            return FixedContract(rentPortion, chargeableAdvance);

        var remaining = amountDue;
        var rent = Take(ref remaining, rentPortion);
        var advance = Take(ref remaining, chargeableAdvance);
        var components = new List<RentComponentDraft> { new(RentLedgerComponentKind.Rent, rent) };
        if (advance != 0m)
            components.Add(new RentComponentDraft(RentLedgerComponentKind.CondominiumAdvance, advance));
        if (remaining != 0m)
            components.Add(new RentComponentDraft(RentLedgerComponentKind.Other, remaining));

        EnsureAmountDue(amountDue, components);
        return components;

        static decimal Take(ref decimal remaining, decimal wanted)
        {
            if (wanted <= 0m || remaining <= 0m)
                return 0m;

            var taken = wanted < remaining ? wanted : remaining;
            remaining -= taken;
            return taken;
        }
    }
}

public sealed class RentPostedMonth
{
    private RentPostedMonth(DateOnly periodStart, DateOnly periodEnd, decimal amountDue, IReadOnlyList<RentComponentDraft> components)
    {
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        AmountDue = amountDue;
        Components = components;
    }

    public DateOnly PeriodStart { get; }

    public DateOnly PeriodEnd { get; }

    public decimal AmountDue { get; }

    public IReadOnlyList<RentComponentDraft> Components { get; }

    public static RentPostedMonth Create(DateOnly periodStart, DateOnly periodEnd, IReadOnlyList<RentComponentDraft> components)
    {
        if (periodEnd < periodStart)
            throw new ArgumentException("The period ends before it starts.", nameof(periodEnd));

        var amountDue = RentLedgerLine.Sum(components);
        RentLedgerLine.EnsureAmountDue(amountDue, components);
        return new RentPostedMonth(periodStart, periodEnd, amountDue, components);
    }
}

public sealed class RentMonthLedger
{
    private readonly Dictionary<DateOnly, RentPostedMonth> _months = new();

    public int Count => _months.Count;

    public RentPostedMonth Post(DateOnly periodStart, DateOnly periodEnd, IReadOnlyList<RentComponentDraft> components)
    {
        if (_months.TryGetValue(periodStart, out var existing))
            return existing;

        var posted = RentPostedMonth.Create(periodStart, periodEnd, components);
        _months.Add(periodStart, posted);
        return posted;
    }
}

public sealed record RentCondominiumTerms(
    CondominiumAdvanceMode AdvanceMode,
    decimal? AdvanceAmount,
    RentCadence? Cadence,
    bool Confirmed)
{
    public const decimal MinimumProposalConfidence = 0.85m;

    public decimal ChargeableAdvance(decimal? budgetAmount)
    {
        if (!Confirmed || AdvanceMode is CondominiumAdvanceMode.None or CondominiumAdvanceMode.NeedsReview)
            return 0m;

        if (AdvanceMode == CondominiumAdvanceMode.FixedContractAmount)
            return AdvanceAmount ?? throw new InvalidOperationException("A fixed contractual advance needs an amount.");

        if (AdvanceMode == CondominiumAdvanceMode.BudgetAllocation)
            return budgetAmount ?? 0m;

        return 0m;
    }

    public decimal UniformAdvanceFor(RentCadence rentCadence)
    {
        var perCadence = ChargeableAdvance(budgetAmount: null);
        if (perCadence == 0m)
            return 0m;

        if (Cadence is not { } advanceCadence)
            throw new InvalidOperationException("A chargeable advance needs a cadence.");

        return RentLedgerMoney.UniformAdvance(perCadence, advanceCadence, rentCadence);
    }
}
