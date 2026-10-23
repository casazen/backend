using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Leases;

/// <summary>
/// The eight stages a long-term lease goes through (LR-01b), in the order of the path. Serialized by name. <see cref="DataPending"/> exists
/// in the type for the day the tenant is invited to fill in their own data (phase 2, decision D1); the first release never produces it.
/// </summary>
public enum LeaseStage
{
    /// <summary>The landlord is still preparing the contract: something is missing from the first steps.</summary>
    Draft,

    /// <summary>Waiting for the tenant's own data (phase 2). Never produced.</summary>
    DataPending,

    /// <summary>All the data are there: the landlord checks the preview and confirms the review.</summary>
    InReview,

    /// <summary>The contract was handed out for the signature on paper: waiting for the signed copy.</summary>
    SentForSignature,

    /// <summary>Signed by everyone, the registration is still to be declared.</summary>
    Signed,

    /// <summary>Registered and running.</summary>
    Active,

    /// <summary>Registered and about to end: six months from the end of its period, or a notice or an early termination is recorded.</summary>
    Expiring,

    /// <summary>Over: the period ended with a notice or without renewal, the lease was ended early, or the contract was rejected.</summary>
    Concluded,
}

/// <summary>Who has to do the next step. Serialized by name. <see cref="Tenant"/> is for phase 2; the first release never produces it.</summary>
public enum LeaseNextStepActor
{
    Landlord,
    Tenant,
    System,
}

/// <summary>
/// What the next step of a lease is, as a stable code (LR-01b): the interface turns it into a sentence and a button, the server alone decides
/// which one it is. A code is never renamed: add a new one. Serialized by name.
/// </summary>
public enum LeaseNextStepCode
{
    /// <summary>Draft: the property of the draft cannot be found any more (deleted or out of reach): choose one.</summary>
    ChooseProperty,

    /// <summary>Draft: a data step is incomplete. Params: <c>step</c> (1 to 5) and <c>needs</c> (the codes of what is missing, <see cref="LeaseNeedCodes"/>).</summary>
    CompleteStep,

    /// <summary>Imported draft with all its data: upload the signed contract and the day it was signed (<c>POST api/leases/{id}/import</c>).</summary>
    ImportSignedContract,

    /// <summary>Draft prepared by a partner (ConcordatoFacile): nothing for the landlord to do in the general path. See <see cref="LeaseFlowInput.AwaitsPartnerContract"/>.</summary>
    AwaitPartnerContract,

    /// <summary>In review, not confirmed yet: read the preview and confirm.</summary>
    ConfirmReview,

    /// <summary>In review, confirmed: download the contract to have it signed on paper.</summary>
    DownloadForSignature,

    /// <summary>Handed out for signature: upload the signed copy.</summary>
    UploadSignedCopy,

    /// <summary>Signed: declare the registration. Due on the registration deadline. Param <c>imported</c> (true) for a contract brought in already signed.</summary>
    RegisterLease,

    /// <summary>In force, extra-EU tenant, Questura communication not declared, no delivery day recorded: record when the keys were handed over.</summary>
    RecordKeyHandover,

    /// <summary>In force, extra-EU tenant, communication not declared: declare it. Due 48 hours after the delivery.</summary>
    QuesturaCommunication,

    /// <summary>Registered, rent past due: remind the tenant. Params: <c>count</c>, <c>amount</c> (what is overdue), <c>days</c> (since the oldest).</summary>
    SendRentReminder,

    /// <summary>Active and no active rent plan: activate one.</summary>
    ActivateRentPlan,

    /// <summary>Expiring, nothing recorded: decide whether to give notice. Due on the last day to give it.</summary>
    DecideNotice,

    /// <summary>Expiring with a notice or an early termination (or a lease that does not renew): organize the end. Due on the last day.</summary>
    OrganizeEnd,

    /// <summary>Nothing for anybody to do. Due on the next rent installment, if any.</summary>
    AllGood,

    /// <summary>Concluded: nothing left to do, the lease can be archived.</summary>
    Archive,
}

/// <summary>
/// The one step a lease is waiting for. <see cref="Priority"/> goes from 0 (the most urgent) to 10; <see cref="DueOn"/> is the Europe/Rome day
/// it is due, when it has one; <see cref="Params"/> carry what a sentence needs (counts, amounts, codes, days: never a name or a datum of a
/// person).
/// </summary>
public sealed record LeaseNextStep(
    LeaseNextStepCode Code,
    LeaseNextStepActor Actor,
    int Priority,
    DateOnly? DueOn = null,
    IReadOnlyDictionary<string, object?>? Params = null);

/// <summary>The stage of a lease and the one step it is waiting for.</summary>
public sealed record LeaseFlowResult(LeaseStage Stage, LeaseNextStep NextStep);

/// <summary>
/// Everything <see cref="LeaseFlow.Derive"/> needs to know about a lease, as plain values: no entity, no database. Every date is a Europe/Rome
/// calendar day. The caller gathers it (<c>LeaseFlowReader</c>); the rules are not copied anywhere else.
/// </summary>
public sealed record LeaseFlowInput
{
    /// <summary>Today in Europe/Rome.</summary>
    public required DateOnly Today { get; init; }

    public required LeaseStatus Status { get; init; }

    public LeaseOrigin Origin { get; init; } = LeaseOrigin.Created;

    public LeaseContractType ContractType { get; init; } = LeaseContractType.Libero;

    public DateOnly StartDate { get; init; }

    /// <summary>Last day of the period the lease is in now (<see cref="Entities.LeaseContract.CurrentTermEndDate"/>, LR-14).</summary>
    public DateOnly CurrentTermEndDate { get; init; }

    /// <summary>The day a notice of non-renewal was given; <c>null</c> without one.</summary>
    public DateOnly? NoticeOn { get; init; }

    /// <summary>The last day of a lease ended early, or recorded as concluded when it was brought in already over (LR-14); <c>null</c> otherwise.</summary>
    public DateOnly? TerminatedOn { get; init; }

    /// <summary>The registration deadline as far as it is known (<see cref="RliRegistrationDeadline.Resolve(LeaseStatus, DateTime?, DateTime, DateTime)"/>); <c>null</c> while to be determined.</summary>
    public DateOnly? RegistrationDeadline { get; init; }

    /// <summary>The day the property was delivered, as the landlord declared it; <c>null</c> when not declared.</summary>
    public DateOnly? PropertyDeliveryDate { get; init; }

    /// <summary>The day the landlord declared the Questura communication; <c>null</c> until then.</summary>
    public DateOnly? QuesturaCommunicationDate { get; init; }

    /// <summary>A tenant is extra-EU: the Questura communication applies.</summary>
    public bool HasExtraEuTenant { get; init; }

    /// <summary>The day the contract was handed out for signature; <c>null</c> when it was not.</summary>
    public DateOnly? SentForSignatureOn { get; init; }

    /// <summary>The review of the draft is confirmed.</summary>
    public bool ReviewConfirmed { get; init; }

    /// <summary>
    /// The steps of the draft (<see cref="LeaseDraftProgress.Compute"/>), for a lease that is a draft. Without it a draft is taken to have
    /// something missing in its first step.
    /// </summary>
    public LeaseDraftFlow? Draft { get; init; }

    /// <summary>
    /// The draft is one whose contract a partner prepares (ConcordatoFacile): the general path has nothing for the landlord to do and shows a
    /// neutral step; the state of the partner's practice is another piece of information, kept apart. <b>Nothing sets it yet</b>: the
    /// integration with the partner does, where it builds this input (the partner and its practice are not recorded on the lease).
    /// </summary>
    public bool AwaitsPartnerContract { get; init; }

    /// <summary>The lease has a rent plan that is active.</summary>
    public bool HasActiveRentPlan { get; init; }

    /// <summary>Installments past due and not paid (<see cref="RentInstallmentRules"/>).</summary>
    public int OverdueRentCount { get; init; }

    public decimal OverdueRentAmount { get; init; }

    /// <summary>The due date of the oldest installment past due.</summary>
    public DateOnly? OldestOverdueRentDueDate { get; init; }

    /// <summary>The due date of the first installment still to be collected.</summary>
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
    /// <summary>The priorities of the steps: the lower the number, the more urgent.</summary>
    public static class Priority
    {
        /// <summary>Registration past its deadline; Questura communication past its deadline; rent past due.</summary>
        public const int Overdue = 0;

        /// <summary>Registration due within <see cref="RegistrationSoonDays"/>; delivery of the keys late.</summary>
        public const int Soon = 1;

        /// <summary>Registration due later or with no deadline known.</summary>
        public const int RegistrationLater = 2;

        /// <summary>Signed copy not back after <see cref="ChaseSignedCopyAfterDays"/> days; Questura or keys not late yet.</summary>
        public const int Chase = 3;

        /// <summary>Review to confirm or download for signature; import of the signed contract; rent plan to activate; notice or end to decide.</summary>
        public const int Review = 4;

        /// <summary>A step of the draft to complete.</summary>
        public const int Draft = 5;

        /// <summary>Signed copy handed out recently.</summary>
        public const int Waiting = 7;

        /// <summary>Notice far from its last day.</summary>
        public const int NoticeFar = 8;

        /// <summary>Nothing to do: every step of the landlord is done or neutral.</summary>
        public const int AllGood = 9;

        /// <summary>A concluded lease.</summary>
        public const int Archive = 10;
    }

    /// <summary>A registration due within this many days is more urgent than one due later.</summary>
    public const int RegistrationSoonDays = 7;

    /// <summary>A copy handed out for signature and not back after this many days is chased.</summary>
    public const int ChaseSignedCopyAfterDays = 3;

    /// <summary>A last day to give notice within this many days is urgent.</summary>
    public const int NoticeUrgentWithinDays = 60;

    /// <summary>The stage of the lease and the one step it is waiting for.</summary>
    public static LeaseFlowResult Derive(LeaseFlowInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var stage = StageOf(input);
        return new LeaseFlowResult(stage, NextStepOf(input, stage));
    }

    /// <summary>The stage of the lease.</summary>
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

        // An imported contract is already signed: it has no review to wait in, it is a draft until the signed copy is brought in.
        return input.Origin != LeaseOrigin.Imported && input.Draft is { } draft && OpenStep(draft) is null
            ? LeaseStage.InReview
            : LeaseStage.Draft;
    }

    /// <summary>
    /// A lease signed by everyone is over from the day after its last day: when it was ended early, or when its period is over and nothing
    /// renews it (a notice, or a type without renewal). A lease whose period is over and that renews by itself is not over.
    /// </summary>
    private static bool HasEnded(LeaseFlowInput i) =>
        (i.TerminatedOn is { } terminated && terminated < i.Today)
        || (i.CurrentTermEndDate < i.Today && (i.NoticeOn is not null || !LeaseRenewal.Renews(i.ContractType)));

    /// <summary>The same rule as the "Expiring" view of the lease list (<see cref="LeaseListViews"/>).</summary>
    private static bool IsExpiring(LeaseFlowInput i) =>
        i.NoticeOn is not null
        || i.TerminatedOn is not null
        || i.CurrentTermEndDate <= i.Today.AddMonths(LeaseListViews.ExpiringWithinMonths);

    /// <summary>The first of the data steps that has something missing, <c>null</c> when they are all complete.</summary>
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

    // ---- a contract not signed yet -------------------------------------------------------------------------------

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

        // All the data are there. An imported contract goes on with the signed copy it already has; one made in CasaZen with its review
        // (the stage is then InReview and this is not reached; the answer is the same, so the two never disagree).
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

    // ---- a contract signed by everyone, not over -----------------------------------------------------------------

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

        if (stage == LeaseStage.Expiring)
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
