using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Leases;

/// <summary>One component of a rent installment before it is stored.</summary>
/// <param name="Kind">Rent, condominium advance, settlement or other.</param>
/// <param name="Amount">Euros, in cents. A settlement credit is negative.</param>
/// <param name="Description">Optional note. The kind is what the clients show.</param>
public sealed record RentComponentDraft(RentLedgerComponentKind Kind, decimal Amount, string? Description = null);

/// <summary>Cents arithmetic for rent and condominium amounts (EUR).</summary>
public static class RentLedgerMoney
{
    /// <summary>
    /// Splits <paramref name="total"/> into <paramref name="installments"/> amounts of whole cents. Every installment gets the
    /// same base share; the leftover cents go on the last installment, so the parts add up to <paramref name="total"/> exactly.
    /// </summary>
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

    /// <summary>The advance of one rent installment when the advance cadence divides the rent cadence with no leftover cents.</summary>
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

/// <summary>
/// The parts of one rent installment and the rule that <c>AmountDue</c> equals their sum.
/// </summary>
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

    /// <summary>Throws when <paramref name="amountDue"/> is not the sum of <paramref name="components"/>.</summary>
    public static void EnsureAmountDue(decimal amountDue, IReadOnlyList<RentComponentDraft> components)
    {
        var sum = Sum(components);
        if (amountDue != sum)
        {
            throw new InvalidOperationException(
                $"AmountDue {amountDue} must equal the sum of the components ({sum}).");
        }
    }

    /// <summary>Rent plus the contractual condominium advance (and any settlement or other charge that is not zero).</summary>
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

    /// <summary>A line that has no condominium breakdown yet: the whole amount due is rent.</summary>
    public static IReadOnlyList<RentComponentDraft> SingleRent(decimal amountDue) =>
        FixedContract(amountDue, 0m);

    /// <summary>
    /// The installment total after the condominium terms change. When <paramref name="currentTotal"/> is the rent, the rent
    /// plus <paramref name="previousAdvance"/>, or already the rent plus <paramref name="nextAdvance"/>, it becomes the rent
    /// plus <paramref name="nextAdvance"/>. A next advance of zero (no advance, or a clause that charges nothing) drops the
    /// previous one. Any other total was chosen by the landlord and stays.
    /// </summary>
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

/// <summary>One posted month of the rent ledger. Posting the same <see cref="PeriodStart"/> again returns this row.</summary>
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

/// <summary>
/// The months posted for one lease. Posting a month that is already there does nothing: the first line, with its components,
/// is the one that stays.
/// </summary>
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

/// <summary>The condominium terms the rent plan reads. A budget never replaces a confirmed fixed contractual advance.</summary>
public sealed record RentCondominiumTerms(
    CondominiumAdvanceMode AdvanceMode,
    decimal? AdvanceAmount,
    RentCadence? Cadence,
    bool Confirmed)
{
    /// <summary>Nothing under this confidence is proposed as a charge until a person confirms it.</summary>
    public const decimal MinimumProposalConfidence = 0.85m;

    /// <summary>
    /// The advance to charge for one period of the terms' own cadence. A confirmed fixed amount wins over
    /// <paramref name="budgetAmount"/>. Unconfirmed terms, <see cref="CondominiumAdvanceMode.None"/> and
    /// <see cref="CondominiumAdvanceMode.NeedsReview"/> charge nothing, budget included.
    /// </summary>
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

    /// <summary>The advance of one rent installment, when it scales onto <paramref name="rentCadence"/> in equal cents.</summary>
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
