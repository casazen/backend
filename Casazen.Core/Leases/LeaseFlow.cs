using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Leases;

public enum LeaseStage
{
    Draft,

    DataPending,

    InReview,

    SentForSignature,

    Signed,

    Active,

    Expiring,

    Concluded,
}

public enum LeaseNextStepActor
{
    Landlord,
    Tenant,
    System,
}

public enum LeaseNextStepCode
{
    ChooseProperty,

    CompleteStep,

    ImportSignedContract,

    AwaitPartnerContract,

    ConfirmReview,

    DownloadForSignature,

    UploadSignedCopy,

    RegisterLease,

    /// <summary>In force, extra-EU tenant, Questura communication not declared, no delivery day recorded: record when the keys were handed over.</summary>
    RecordKeyHandover,

    QuesturaCommunication,

    /// <summary>Registered, rent past due: remind the tenant. Params: <c>count</c>, <c>amount</c> (what is overdue), <c>days</c> (since the oldest).</summary>
    SendRentReminder,

    ActivateRentPlan,

    DecideNotice,

    OrganizeEnd,

    AllGood,

    Archive,
}

public sealed record LeaseNextStep(
    LeaseNextStepCode Code,
    LeaseNextStepActor Actor,
    int Priority,
    DateOnly? DueOn = null,
    IReadOnlyDictionary<string, object?>? Params = null);

public enum LeaseProgressKind
{
    Setup,

    Term,

    End,
}

public enum LeaseProgressUnit
{
    Steps,

    Months,

    Years,
}

public sealed record LeaseProgress(LeaseProgressKind Kind, LeaseProgressUnit Unit, int Done, int Total, int Percent);

public sealed record LeaseFlowResult(LeaseStage Stage, LeaseNextStep NextStep, LeaseProgress Progress);

public sealed record LeaseFlowInput
{
    public required DateOnly Today { get; init; }

    public required LeaseStatus Status { get; init; }

    public LeaseOrigin Origin { get; init; } = LeaseOrigin.Created;

    public LeaseContractType ContractType { get; init; } = LeaseContractType.Libero;

    public DateOnly StartDate { get; init; }

    public DateOnly CurrentTermEndDate { get; init; }

    public DateOnly? NoticeOn { get; init; }

    /// <summary>The last day of a lease ended early, or recorded as concluded when it was brought in already over (LR-14); <c>null</c> otherwise.</summary>
    public DateOnly? TerminatedOn { get; init; }

    public DateOnly? RegistrationDeadline { get; init; }

    public DateOnly? PropertyDeliveryDate { get; init; }

    public DateOnly? QuesturaCommunicationDate { get; init; }

    /// <summary>A tenant is extra-EU: the Questura communication applies.</summary>
    public bool HasExtraEuTenant { get; init; }

    public DateOnly? SentForSignatureOn { get; init; }

    public bool ReviewConfirmed { get; init; }

    public LeaseDraftFlow? Draft { get; init; }

    /// <summary>
    /// The draft is one whose contract a partner prepares (ConcordatoFacile, <c>IsConcordatoFacileSource</c>, CF-DONE): its steps are never
    /// the data steps of the wizard, the step is <see cref="LeaseNextStepCode.AwaitPartnerContract"/>. The state of the partner's practice is
    /// read on the sheet of the lease, from the partner.
    /// </summary>
    public bool AwaitsPartnerContract { get; init; }

    public bool HasActiveRentPlan { get; init; }

    public int OverdueRentCount { get; init; }

    public decimal OverdueRentAmount { get; init; }

    public DateOnly? OldestOverdueRentDueDate { get; init; }

    public DateOnly? NextRentDueDate { get; init; }
}

/// <summary>
/// The path of a long-term lease (LR-01b): its stage among the eight of the design and the one step it is waiting for, derived on the server
/// from the facts of the lease in one place, so that the list, the overview and any other screen show the same answer and the interface
/// computes nothing (a duplicated rule diverges). A pure function of its input: stable codes, no sentence.
/// </summary>
/// <remarks>
/// <para><b>The stage</b>, in this order: a rejected lease is <see cref="LeaseStage.Concluded"/>; a lease signed by everyone is concluded
/// when it was ended early (<see cref="LeaseFlowInput.TerminatedOn"/>, from the day after) or when the period it is in is over and it does
/// not renew (a notice is recorded, or its type has no renewal); otherwise <see cref="LeaseStage.Active"/> or <see cref="LeaseStage.Expiring"/> if
/// registered, <see cref="LeaseStage.Signed"/> if not. <b>A lease that renews by itself is never concluded because its period is over</b>:
/// the renewal job moves the end forward within the hour (LR-14, decision D5), and until then it is still
/// <see cref="LeaseStage.Expiring"/>. A contract not signed yet is <see cref="LeaseStage.SentForSignature"/> once handed out,
/// <see cref="LeaseStage.InReview"/> when its data are complete (never for an imported one, which has no review), <see cref="LeaseStage.Draft"/> otherwise.</para>
/// <para><b>The step</b> is the most urgent one the lease is waiting for: lowest <see cref="LeaseNextStep.Priority"/>, then the earliest
/// due day, then the code. The priorities are the numbers of the demo (<c>lc-flow.js</c>) in one place, <see cref="Priority"/>: changing one is a
/// change here and nowhere else. Obligations that are not verified (communication of the registration details, attestation of conformity,
/// annual registry tax) are not steps (decision LR-D20); the deposit return needs a record of the return that does not exist yet.</para>
/// </remarks>
public static class LeaseFlow
{
    public static class Priority
    {
        /// <summary>Registration past its deadline; Questura communication past its deadline; rent past due.</summary>
        public const int Overdue = 0;

        public const int Soon = 1;

        public const int RegistrationLater = 2;

        /// <summary>Signed copy not back after <see cref="ChaseSignedCopyAfterDays"/> days; Questura or keys not late yet.</summary>
        public const int Chase = 3;

        public const int Review = 4;

        public const int Draft = 5;

        public const int Waiting = 7;

        public const int NoticeFar = 8;

        public const int AllGood = 9;

        public const int Archive = 10;
    }

    public const int RegistrationSoonDays = 7;

    public const int ChaseSignedCopyAfterDays = 3;

    public const int NoticeUrgentWithinDays = 60;

    public const int SetupSteps = 8;

    public static LeaseFlowResult Derive(LeaseFlowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var stage = StageOf(input);
        return new LeaseFlowResult(stage, NextStepOf(input, stage), ProgressOf(input, stage));
    }

    /// <summary>
    /// How far along the lease is. Before the contract runs it counts the steps of the path: the data steps of the draft that have nothing
    /// missing (0 to 5) while it is a draft, then the review (5, 6 once confirmed), the contract handed out for signature (6) and signed by
    /// everyone (7); the eighth, the registration, is the one that makes it run. A running lease is measured in the period it is in now
    /// (<see cref="LeaseFlowInput.CurrentTermEndDate"/>, so a lease that renewed counts its years from the start of the first period): the year
    /// it is in out of the years of the period, or the month out of the months when the period is shorter than a year, with the share of its
    /// days gone by. A concluded lease is whole.
    /// </summary>
    public static LeaseProgress ProgressOf(LeaseFlowInput input, LeaseStage stage)
    {
        ArgumentNullException.ThrowIfNull(input);

        switch (stage)
        {
            case LeaseStage.Concluded:
                return new LeaseProgress(LeaseProgressKind.End, LeaseProgressUnit.Steps, SetupSteps, SetupSteps, 100);
            case LeaseStage.Active or LeaseStage.Expiring:
                return TermProgress(input);
        }

        var done = stage switch
        {
            LeaseStage.Signed => 7,
            LeaseStage.SentForSignature => 6,
            LeaseStage.InReview => input.ReviewConfirmed ? 6 : LeaseDraftDefaults.LastDataStep,
            _ => input.Draft is { } draft ? draft.Steps.Take(LeaseDraftDefaults.LastDataStep).Count(s => s.Needs.Count == 0) : 0,
        };
        return new LeaseProgress(LeaseProgressKind.Setup, LeaseProgressUnit.Steps, done, SetupSteps, done * 100 / SetupSteps);
    }

    private static LeaseProgress TermProgress(LeaseFlowInput i)
    {
        var start = i.StartDate;
        var end = i.CurrentTermEndDate;
        var days = Math.Max(1, end.DayNumber - start.DayNumber + 1);
        var gone = Math.Clamp(i.Today.DayNumber - start.DayNumber + 1, 0, days);
        var percent = gone * 100 / days;

        var after = end.AddDays(1);
        var months = Math.Max(1, (after.Year - start.Year) * 12 + after.Month - start.Month);
        var run = Math.Max(0, (i.Today.Year - start.Year) * 12 + i.Today.Month - start.Month - (i.Today.Day < start.Day ? 1 : 0));

        if (months >= 12)
        {
            var years = (months + 11) / 12;
            return new LeaseProgress(LeaseProgressKind.Term, LeaseProgressUnit.Years, Math.Min(years, run / 12 + 1), years, percent);
        }

        return new LeaseProgress(LeaseProgressKind.Term, LeaseProgressUnit.Months, Math.Min(months, run + 1), months, percent);
    }

    public static LeaseStage StageOf(LeaseFlowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.Status == LeaseStatus.Rejected)
            return LeaseStage.Concluded;

        if (LeaseRenewal.IsInForce(input.Status))
        {
            if (HasEnded(input))
                return LeaseStage.Concluded;
            if (input.Status != LeaseStatus.Registered)
                return LeaseStage.Signed;
            return IsExpiring(input) ? LeaseStage.Expiring : LeaseStage.Active;
        }

        if (input.SentForSignatureOn is not null || input.Status is LeaseStatus.AwaitingSignature or LeaseStatus.PartiallySigned)
            return LeaseStage.SentForSignature;

        return input.Origin != LeaseOrigin.Imported && input.Draft is { } draft && OpenStep(draft) is null
            ? LeaseStage.InReview
            : LeaseStage.Draft;
    }

    private static bool HasEnded(LeaseFlowInput i) =>
        (i.TerminatedOn is { } terminated && terminated < i.Today)
        || (i.CurrentTermEndDate < i.Today && (i.NoticeOn is not null || !LeaseRenewal.Renews(i.ContractType)));

    private static bool IsExpiring(LeaseFlowInput i) =>
        i.NoticeOn is not null
        || i.TerminatedOn is not null
        || i.CurrentTermEndDate <= i.Today.AddMonths(LeaseListViews.ExpiringWithinMonths);

    private static LeaseFlowStep? OpenStep(LeaseDraftFlow draft) =>
        draft.Steps.Take(LeaseDraftDefaults.LastDataStep).FirstOrDefault(s => s.Needs.Count > 0);

    private static LeaseNextStep NextStepOf(LeaseFlowInput i, LeaseStage stage) => stage switch
    {
        LeaseStage.Draft or LeaseStage.DataPending => DraftStep(i),
        LeaseStage.InReview => new LeaseNextStep(
            i.ReviewConfirmed ? LeaseNextStepCode.DownloadForSignature : LeaseNextStepCode.ConfirmReview,
            LeaseNextStepActor.Landlord,
            Priority.Review),
        LeaseStage.SentForSignature => SignedCopyStep(i),
        LeaseStage.Concluded => new LeaseNextStep(LeaseNextStepCode.Archive, LeaseNextStepActor.System, Priority.Archive),
        _ => RunningStep(i, stage),
    };

    private static LeaseNextStep DraftStep(LeaseFlowInput i)
    {
        if (i.AwaitsPartnerContract)
            return new LeaseNextStep(LeaseNextStepCode.AwaitPartnerContract, LeaseNextStepActor.System, Priority.AllGood);

        if (i.Draft is not { } draft)
            return CompleteStep(1, []);

        if (OpenStep(draft) is { } open)
        {
            var needs = open.Needs.Select(n => n.Code).ToArray();
            return open.Step == 1 && needs.Contains(LeaseNeedCodes.PropertyMissing)
                ? new LeaseNextStep(LeaseNextStepCode.ChooseProperty, LeaseNextStepActor.Landlord, Priority.Draft)
                : CompleteStep(open.Step, needs);
        }

        return new LeaseNextStep(
            i.Origin == LeaseOrigin.Imported
                ? LeaseNextStepCode.ImportSignedContract
                : i.ReviewConfirmed ? LeaseNextStepCode.DownloadForSignature : LeaseNextStepCode.ConfirmReview,
            LeaseNextStepActor.Landlord,
            Priority.Review);
    }

    private static LeaseNextStep CompleteStep(int step, string[] needs) => new(
        LeaseNextStepCode.CompleteStep,
        LeaseNextStepActor.Landlord,
        Priority.Draft,
        Params: new Dictionary<string, object?> { ["step"] = step, ["needs"] = needs });

    private static LeaseNextStep SignedCopyStep(LeaseFlowInput i) => new(
        LeaseNextStepCode.UploadSignedCopy,
        LeaseNextStepActor.Landlord,
        i.SentForSignatureOn is { } sent && i.Today.DayNumber - sent.DayNumber >= ChaseSignedCopyAfterDays ? Priority.Chase : Priority.Waiting);

    private static LeaseNextStep RunningStep(LeaseFlowInput i, LeaseStage stage)
    {
        var candidates = new List<LeaseNextStep>(5);

        if (stage == LeaseStage.Signed)
            candidates.Add(RegisterStep(i));

        // The delivery day only starts the 48 hours of the Questura communication: with no extra-EU tenant nothing depends on it, so it is
        // no step; after the declaration it is locked (409 questura_delivery_date_locked) and is no step either.
        if (i.HasExtraEuTenant && i.QuesturaCommunicationDate is null)
        {
            if (i.PropertyDeliveryDate is null)
                candidates.Add(KeysStep(i));
            candidates.Add(QuesturaStep(i));
        }

        if (stage != LeaseStage.Signed && i.OverdueRentCount > 0)
            candidates.Add(RentReminderStep(i));

        if (stage == LeaseStage.Active && !i.HasActiveRentPlan)
            candidates.Add(new LeaseNextStep(LeaseNextStepCode.ActivateRentPlan, LeaseNextStepActor.Landlord, Priority.Review));

        // LR-TERM: no end to organize for a lease whose period is empty or a single day (its end is not after its start): the early termination
        // no longer produces one (it needs a day after the start), but a lease that was ended that way before has one, and the guide must not
        // tell the landlord to "organize the end" of a contract that never ran. Nothing is rewritten; the step is simply not proposed.
        if (stage == LeaseStage.Expiring && i.CurrentTermEndDate > i.StartDate)
            candidates.Add(EndStep(i));

        if (candidates.Count == 0)
            return new LeaseNextStep(LeaseNextStepCode.AllGood, LeaseNextStepActor.System, Priority.AllGood, i.NextRentDueDate);

        return candidates
            .OrderBy(s => s.Priority)
            .ThenBy(s => s.DueOn is null)
            .ThenBy(s => s.DueOn)
            .ThenBy(s => (int)s.Code)
            .First();
    }

    private static LeaseNextStep RegisterStep(LeaseFlowInput i)
    {
        var priority = i.RegistrationDeadline switch
        {
            null => Priority.RegistrationLater,
            { } deadline when deadline < i.Today => Priority.Overdue,
            { } deadline when deadline <= i.Today.AddDays(RegistrationSoonDays) => Priority.Soon,
            _ => Priority.RegistrationLater,
        };

        return new LeaseNextStep(
            LeaseNextStepCode.RegisterLease,
            LeaseNextStepActor.Landlord,
            priority,
            i.RegistrationDeadline,
            i.Origin == LeaseOrigin.Imported ? new Dictionary<string, object?> { ["imported"] = true } : null);
    }

    private static LeaseNextStep KeysStep(LeaseFlowInput i) => new(
        LeaseNextStepCode.RecordKeyHandover,
        LeaseNextStepActor.Landlord,
        i.StartDate < i.Today ? Priority.Soon : Priority.Chase,
        i.StartDate);

    private static LeaseNextStep QuesturaStep(LeaseFlowInput i)
    {
        var deadline = (i.PropertyDeliveryDate ?? i.StartDate).AddDays(QuesturaCommunicationDeadline.DaysFromDelivery);
        return new LeaseNextStep(
            LeaseNextStepCode.QuesturaCommunication,
            LeaseNextStepActor.Landlord,
            deadline < i.Today ? Priority.Overdue : Priority.Chase,
            deadline);
    }

    private static LeaseNextStep RentReminderStep(LeaseFlowInput i)
    {
        int? days = i.OldestOverdueRentDueDate is { } oldest ? i.Today.DayNumber - oldest.DayNumber : null;
        return new LeaseNextStep(
            LeaseNextStepCode.SendRentReminder,
            LeaseNextStepActor.Landlord,
            Priority.Overdue,
            i.OldestOverdueRentDueDate,
            new Dictionary<string, object?>
            {
                ["count"] = i.OverdueRentCount,
                ["amount"] = i.OverdueRentAmount,
                ["days"] = days,
            });
    }

    /// <summary>
    /// An expiring lease: with a notice or an early termination recorded, or one that does not renew, it only has an end to organize;
    /// otherwise the landlord has to decide about the notice, which is due on the last day to give it.
    /// </summary>
    private static LeaseNextStep EndStep(LeaseFlowInput i)
    {
        var lastDayForNotice = LongRentDeadlineRules.NoticeDate(i.ContractType, i.CurrentTermEndDate);
        if (i.NoticeOn is not null || i.TerminatedOn is not null || lastDayForNotice is not { } lastDay)
        {
            return new LeaseNextStep(
                LeaseNextStepCode.OrganizeEnd, LeaseNextStepActor.Landlord, Priority.Review, i.CurrentTermEndDate);
        }

        return new LeaseNextStep(
            LeaseNextStepCode.DecideNotice,
            LeaseNextStepActor.Landlord,
            lastDay <= i.Today.AddDays(NoticeUrgentWithinDays) ? Priority.Review : Priority.NoticeFar,
            lastDay);
    }
}
