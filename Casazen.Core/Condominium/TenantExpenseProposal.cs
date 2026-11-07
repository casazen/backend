using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;

namespace Casazen.Core.Condominium;

/// <summary>
/// What the contract wizard shows as the condominium expense the tenant pays.
/// A confirmed budget, or the fixed advance the contract already states, wins over numbers typed in the wizard.
/// With nothing on the books, the new-year preventivo is split into installments and the previous consuntivo is kept
/// for the later settlement. A manual amount is the periodic advance and is settled when the consuntivo is confirmed.
/// </summary>
public static class TenantExpenseProposal
{
    public enum Source
    {
        FixedContract,
        ConfirmedBudget,
        Statements,
        Manual,
    }

    public sealed record Request(
        CondominiumAdvanceMode? AdvanceMode,
        decimal? ContractualAdvance,
        RentCadence? Cadence,
        decimal? PreviousConsuntivo,
        decimal? Preventivo,
        decimal? ManualAmount);

    public sealed record Result(
        Source Source,
        decimal InstallmentAmount,
        decimal LastInstallmentAmount,
        int Installments,
        decimal AnnualAmount,
        decimal? PreviousConsuntivo,
        decimal? Preventivo,
        bool SettledAtConsuntivo,
        Guid? PeriodId,
        string? PeriodLabel);

    public static Result Resolve(bool hasFiscalYear, Guid? periodId, string? label, decimal? recoverableAnnual, Request request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var installments = InstallmentCount(request.Cadence ?? RentCadence.Monthly);
        if (hasFiscalYear && request.AdvanceMode == CondominiumAdvanceMode.FixedContractAmount)
        {
            var advance = RequirePositive(request.ContractualAdvance);
            var annual = CondominiumMoney.Round(advance * installments);
            return new Result(
                Source.FixedContract, advance, advance, installments, annual, null, null, false, periodId, label);
        }

        if (hasFiscalYear && recoverableAnnual is > 0m)
        {
            return Split(Source.ConfirmedBudget, recoverableAnnual.Value, installments, false, null, recoverableAnnual, periodId, label);
        }

        if (request.ManualAmount is not null && (request.PreviousConsuntivo is not null || request.Preventivo is not null))
            throw new ArgumentException("A manual amount and the two statements are two different ways to propose the expense.");

        if (request.ManualAmount is not null)
        {
            var manual = RequirePositive(request.ManualAmount);
            var annual = CondominiumMoney.Round(manual * installments);
            return new Result(Source.Manual, manual, manual, installments, annual, null, null, true, null, null);
        }

        if (request.PreviousConsuntivo is not null || request.Preventivo is not null)
        {
            var consuntivo = RequireNonNegative(request.PreviousConsuntivo);
            var preventivo = RequirePositive(request.Preventivo);
            return Split(Source.Statements, preventivo, installments, true, consuntivo, preventivo, null, null);
        }

        throw new ArgumentException("A condominium expense needs a budget, a fixed advance, both statements, or a manual amount.");
    }

    public static int InstallmentCount(RentCadence cadence)
    {
        var months = RentInstallmentPlan.MonthsPer(cadence);
        if (12 % months != 0)
            throw new ArgumentOutOfRangeException(nameof(cadence), cadence, "The cadence does not divide the year.");
        return 12 / months;
    }

    private static Result Split(
        Source source,
        decimal annual,
        int installments,
        bool settledAtConsuntivo,
        decimal? consuntivo,
        decimal? preventivo,
        Guid? periodId,
        string? label)
    {
        var parts = CondominiumMoney.SplitEqually(annual, installments);
        return new Result(
            source,
            parts[0],
            parts[^1],
            installments,
            CondominiumMoney.Round(annual),
            consuntivo,
            preventivo,
            settledAtConsuntivo,
            periodId,
            label);
    }

    private static decimal RequirePositive(decimal? amount)
    {
        if (amount is not > 0m || !CondominiumMoney.InRange(amount.Value))
            throw new ArgumentOutOfRangeException(nameof(amount));
        return amount.Value;
    }

    private static decimal RequireNonNegative(decimal? amount)
    {
        if (amount is not >= 0m || !CondominiumMoney.InRange(amount.Value))
            throw new ArgumentOutOfRangeException(nameof(amount));
        return amount.Value;
    }
}
