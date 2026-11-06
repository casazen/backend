using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Regulatory;
using Casazen.Core.Utilities;

namespace Casazen.Core.Services;

/// <summary>
/// The compliance traffic light of the items of the cockpit (E03/F02/US01/T03, NF-52, NF-50, CHK-12): for the properties and stays a
/// caller reaches, whether the host is compliant, in progress or has to act, and why. It reads facts and never decides by itself: every
/// light comes from <see cref="ComplianceLightRules"/>, the one rule class, and <b>green exists only with an archived proof</b>.
/// </summary>
/// <remarks>
/// <para><b>What goes in.</b> Alloggiati Web, per stay (the receipt of the arrival day, a send entrusted to the channel manager, a stay
/// declared sent, a connection that is not active); the identity of the guest who leaves the document; the connection and the credentials
/// of the property; ISTAT and tourist tax, per property and month; the municipality tax rate; the CIN. <b>What does not</b>: an Octorate
/// handoff (starting it, or the host coming back from it) is never evidence. Where the outcome is held only by Octorate and CasaZen has no
/// proof, the light is yellow with <see cref="ComplianceReasonCodes.ExternalVerificationRequired"/> and the handoff topic; a deadline
/// CasaZen owns can still make it red, and a proof CasaZen holds can still make it green.</para>
/// <para><b>Scope.</b> Every read is of the properties and stays <paramref name="scope"/> reaches (<c>InScope</c>): a collaborator «Solo
/// alcuni» gets the lights of its own properties and nothing of the others. The service never checks roles.</para>
/// </remarks>
public interface IComplianceStatusService
{
    /// <summary>
    /// The items of the compliance status of the caller's reach, each with its light, a stable reason and the evidence behind it, and the
    /// stays, properties and months that were looked at (the universe the To do list is reconciled against).
    /// </summary>
    Task<ComplianceStatusResult> EvaluateAsync(HostScope scope, CancellationToken ct);
}

/// <summary>What an item of the compliance status is about.</summary>
public enum ComplianceItemKind
{
    /// <summary>The Alloggiati Web communication of a stay.</summary>
    Alloggiati = 1,

    /// <summary>The identity of the guest who leaves the document, for a stay.</summary>
    Identity = 2,

    /// <summary>The police send of the channel manager, for a property.</summary>
    Connection = 3,

    /// <summary>The police credentials kept in CasaZen, for a property.</summary>
    Credentials = 4,

    /// <summary>The ISTAT communication of a month, for a property.</summary>
    Istat = 5,

    /// <summary>The tourist tax declaration and payment of a month, for a property.</summary>
    TouristTax = 6,

    /// <summary>The tourist tax rate of the comune of a property.</summary>
    MunicipalityRate = 7,

    /// <summary>The CIN of a property.</summary>
    Cin = 8,
}

/// <summary>What backs the light of an item (the states of the compliance report of CHK-08).</summary>
public enum ComplianceEvidenceClass
{
    /// <summary>An archived proof CasaZen holds (a receipt, a declaration, a valid CIN, a passed verification): the only thing that makes an item green.</summary>
    OfficialProof = 1,

    /// <summary>The outcome is held only in the connected area of Octorate and CasaZen has no proof: the host checks it there (CHK-15).</summary>
    ExternalVerificationRequired = 2,

    /// <summary>There is no proof yet.</summary>
    Missing = 3,

    /// <summary>Nothing is owed.</summary>
    NotApplicable = 4,
}

/// <summary>
/// An archived proof: the reference of the row that holds it (a receipt, a manual upload, a verification log) and where it comes from. The
/// only input that turns a verdict green; both members are required.
/// </summary>
public sealed record ComplianceProof
{
    /// <summary>The proof <paramref name="reference"/> (a stable key of the row, never a name) from <paramref name="source"/> (a stable code).</summary>
    /// <exception cref="ArgumentException">The reference or the source is blank: there is no proof without them.</exception>
    public ComplianceProof(string reference, string source, DateTime? verifiedAt = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        Reference = reference;
        Source = source;
        VerifiedAt = verifiedAt;
    }

    /// <summary>The reference of the archived proof, e.g. <c>receipt:{id}</c> or <c>manual-proof:{id}</c>.</summary>
    public string Reference { get; }

    /// <summary>Where it comes from: <see cref="ComplianceEvidenceSources"/>.</summary>
    public string Source { get; }

    /// <summary>When the proof was archived or the verification passed (UTC); <c>null</c> when it has no date.</summary>
    public DateTime? VerifiedAt { get; }
}

/// <summary>The stable codes of <see cref="ComplianceProof.Source"/> and of the evidence source of an item.</summary>
public static class ComplianceEvidenceSources
{
    /// <summary>A receipt CasaZen downloaded itself (<see cref="AlloggiatiReceipt"/>, live mode).</summary>
    public const string CasazenReceipt = "casazen_receipt";

    /// <summary>A proof the host uploaded (<see cref="ManualComplianceProof"/>).</summary>
    public const string ManualUpload = "manual_upload";

    /// <summary>The CIN, checked against the official format (<c>CinFormat</c>, computed on read and never stored).</summary>
    public const string CinFormatCheck = "cin_format";

    /// <summary>A passed face comparison, from the audit log of the verification.</summary>
    public const string BiometricLog = "biometric_log";

    /// <summary>A decision of an operator on the verification.</summary>
    public const string OperatorDecision = "operator_decision";

    /// <summary>The outcome is held by Octorate (the connected area of the channel manager).</summary>
    public const string Octorate = "octorate";
}

/// <summary>
/// The verdict of a rule on one item: the light, a stable reason, what backs it. It cannot be built outside <see cref="ComplianceLightRules"/>
/// and a green one cannot be built without a <see cref="ComplianceProof"/>: <see cref="ProofReference"/> is set exactly when
/// <see cref="Light"/> is <see cref="ComplianceLight.Green"/>.
/// </summary>
public sealed record ComplianceVerdict
{
    private ComplianceVerdict(
        ComplianceLight light,
        string reason,
        ComplianceEvidenceClass evidenceClass,
        string? evidenceSource,
        string? proofReference,
        DateTime? lastVerifiedAt,
        OctorateHandoffTopic? handoffTopic)
    {
        Light = light;
        Reason = reason;
        EvidenceClass = evidenceClass;
        EvidenceSource = evidenceSource;
        ProofReference = proofReference;
        LastVerifiedAt = lastVerifiedAt;
        HandoffTopic = handoffTopic;
    }

    /// <summary>Green only with a proof, yellow while in progress, red when the host has to act.</summary>
    public ComplianceLight Light { get; }

    /// <summary>A stable snake_case code the clients translate (<see cref="ComplianceReasonCodes"/>).</summary>
    public string Reason { get; }

    /// <summary>What backs the light.</summary>
    public ComplianceEvidenceClass EvidenceClass { get; }

    /// <summary>The source of the evidence (<see cref="ComplianceEvidenceSources"/>); <c>null</c> when there is none.</summary>
    public string? EvidenceSource { get; }

    /// <summary>The reference of the archived proof; set only on a green verdict.</summary>
    public string? ProofReference { get; }

    /// <summary>When the proof was archived or the verification passed (UTC); <c>null</c> when there is none.</summary>
    public DateTime? LastVerifiedAt { get; }

    /// <summary>The Octorate handoff the host can use to check an outcome CasaZen cannot see; <c>null</c> when none applies.</summary>
    public OctorateHandoffTopic? HandoffTopic { get; }

    /// <summary>True when a person can act on it: anything that is not green.</summary>
    public bool NeedsAttention => Light != ComplianceLight.Green;

    internal static ComplianceVerdict Proven(ComplianceProof proof, string reason)
    {
        ArgumentNullException.ThrowIfNull(proof);
        return new ComplianceVerdict(
            ComplianceLight.Green,
            reason,
            ComplianceEvidenceClass.OfficialProof,
            proof.Source,
            proof.Reference,
            proof.VerifiedAt,
            null);
    }

    internal static ComplianceVerdict InProgress(
        string reason,
        ComplianceEvidenceClass evidence = ComplianceEvidenceClass.Missing,
        OctorateHandoffTopic? topic = null) =>
        new(
            ComplianceLight.Yellow,
            reason,
            evidence,
            evidence == ComplianceEvidenceClass.ExternalVerificationRequired ? ComplianceEvidenceSources.Octorate : null,
            null,
            null,
            topic);

    internal static ComplianceVerdict ActionNeeded(
        string reason,
        ComplianceEvidenceClass evidence = ComplianceEvidenceClass.Missing,
        OctorateHandoffTopic? topic = null) =>
        new(
            ComplianceLight.Red,
            reason,
            evidence,
            evidence == ComplianceEvidenceClass.ExternalVerificationRequired ? ComplianceEvidenceSources.Octorate : null,
            null,
            null,
            topic);
}

/// <summary>One item of the compliance status: what it is about, the action to take, its target and its verdict.</summary>
/// <param name="Kind">What the item is about.</param>
/// <param name="Action">The cockpit action that fixes it. Target: the booking (<paramref name="BookingId"/>) or the property.</param>
/// <param name="PropertyId">The property the item belongs to (the target itself for a property item).</param>
/// <param name="BookingId">The stay, for an item about a stay; <c>null</c> for the others.</param>
/// <param name="Period">The month (<c>yyyy-MM</c>) of an ISTAT or tourist tax item; <c>null</c> otherwise.</param>
/// <param name="Label">What the cockpit calls it: the guest of the stay (as the other items do) or the name of the property.</param>
/// <param name="Verdict">The light, the reason and the evidence.</param>
/// <param name="DueAt">When it has to be done by (UTC), when something fixes it; <c>null</c> otherwise.</param>
public sealed record ComplianceStatusItem(
    ComplianceItemKind Kind,
    ComplianceCockpitAction Action,
    Guid PropertyId,
    Guid? BookingId,
    string? Period,
    string Label,
    ComplianceVerdict Verdict,
    DateTime? DueAt)
{
    /// <summary>The id of the target of the action: the stay when there is one, the property otherwise.</summary>
    public Guid TargetId => BookingId ?? PropertyId;
}

/// <summary>
/// What an evaluation looked at: the stays, the properties and the months. A To do is reconciled only against these, so what the evaluation
/// did not look at (a stay older than the lookback, a property outside the caller's reach) is never touched.
/// </summary>
/// <param name="BookingIds">The stays whose Alloggiati status was evaluated: the ones whose arrival day has come, within the lookback.</param>
/// <param name="IdentityBookingIds">The stays whose identity verification was evaluated: the ones arriving soon, while the verification is on.</param>
/// <param name="PropertyIds">The properties whose connection, credentials, rate and periods were evaluated.</param>
/// <param name="Periods">The months (<c>yyyy-MM</c>) of the ISTAT and tourist tax periods that were evaluated.</param>
/// <param name="BookingWindowStartUtc">
/// The first arrival day the Alloggiati status looks at (the lookback, <c>Compliance:StatusLookbackDays</c>); <c>null</c> when the evaluation has none.
/// A stay that arrived before it is not evaluated any more, and nobody chases its To do here.
/// </param>
/// <param name="ConnectionNotReadPropertyIds">
/// The properties whose police send of the channel manager was <b>not read</b> in this evaluation (over the limit of live reads of a call, no answer in
/// memory): they have no connection item, and that is not because the connection is fine, so their To do about the connection is left alone.
/// </param>
public sealed record ComplianceEvaluatedUniverse(
    IReadOnlySet<Guid> BookingIds,
    IReadOnlySet<Guid> IdentityBookingIds,
    IReadOnlySet<Guid> PropertyIds,
    IReadOnlySet<string> Periods,
    DateTime? BookingWindowStartUtc = null,
    IReadOnlySet<Guid>? ConnectionNotReadPropertyIds = null)
{
    /// <summary>Nothing was looked at.</summary>
    public static ComplianceEvaluatedUniverse None { get; } =
        new(new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<string>(StringComparer.Ordinal));
}

/// <summary>The compliance status of a caller's reach.</summary>
/// <param name="Items">Every item evaluated, of every light.</param>
/// <param name="Evaluated">What was looked at.</param>
public sealed record ComplianceStatusResult(IReadOnlyList<ComplianceStatusItem> Items, ComplianceEvaluatedUniverse Evaluated)
{
    /// <summary>No item and nothing evaluated.</summary>
    public static ComplianceStatusResult Empty { get; } = new([], ComplianceEvaluatedUniverse.None);
}

/// <summary>
/// The stable reason codes of the compliance lights (snake_case): the clients translate them, and a code they do not know is shown with a
/// generic text, never raw. A code is never renamed.
/// </summary>
public static class ComplianceReasonCodes
{
    // Green.

    /// <summary>An archived proof exists (an Alloggiati receipt, an ISTAT proof, the tax receipts).</summary>
    public const string ReceiptArchived = "receipt_archived";

    /// <summary>The verification of the guest's identity passed (a face comparison or an operator).</summary>
    public const string IdentityVerified = "identity_verified";

    /// <summary>The CIN is in the official format.</summary>
    public const string CinValid = "cin_valid";

    // Yellow.

    /// <summary>The communication was sent or declared sent, and its receipt is awaited.</summary>
    public const string AwaitingReceipt = "awaiting_receipt";

    /// <summary>The guests were entrusted to the channel manager, which sends them, within the deadline: the receipt is awaited.</summary>
    public const string EntrustedToChannelManager = "entrusted_to_channel_manager";

    /// <summary>The outcome is held only by Octorate and CasaZen has no proof: the host checks it in the connected area.</summary>
    public const string ExternalVerificationRequired = "external_verification_required";

    /// <summary>The transmission is in progress.</summary>
    public const string AlloggiatiSending = "alloggiati_sending";

    /// <summary>The selfie of the guest is awaited before the arrival.</summary>
    public const string SelfieAwaited = "selfie_awaited";

    /// <summary>The period is open: its deadline has not passed.</summary>
    public const string PeriodOpen = "period_open";

    /// <summary>CasaZen cannot tell whether the send of the channel manager is active: the host confirms.</summary>
    public const string ConnectionUnverified = "connection_unverified";

    // Red.

    /// <summary>No proof is archived (a closed period without its declaration and payment).</summary>
    public const string EvidenceMissing = "evidence_missing";

    /// <summary>The deadline passed and nothing proves the communication.</summary>
    public const string DeadlineOverdue = "deadline_overdue";

    /// <summary>The communication was sent, entrusted or declared and no receipt is archived the day after.</summary>
    public const string ReceiptMissing = "receipt_missing";

    /// <summary>The communication is due and not sent.</summary>
    public const string AlloggiatiNotSent = "alloggiati_not_sent";

    /// <summary>The communication failed or was rejected.</summary>
    public const string AlloggiatiFailed = "alloggiati_failed";

    /// <summary>The service acquired some of the guests only: the stay is not complete.</summary>
    public const string AlloggiatiIncomplete = "alloggiati_incomplete";

    /// <summary>A stay over 30 nights, which the channel manager does not send by itself, is not sent.</summary>
    public const string OverThirtyNightsNotSent = "over_thirty_nights_not_sent";

    /// <summary>A short stay (6-hour term) whose guests were not entrusted to the channel manager.</summary>
    public const string ShortStayAtRisk = "short_stay_at_risk";

    /// <summary>The send of the channel manager is not active (Octorate says so).</summary>
    public const string ConnectionInactive = "connection_inactive";

    /// <summary>The arrival is close and the identity is not verified.</summary>
    public const string IdentityMissing = "identity_missing";

    /// <summary>An operator has to decide on the verification.</summary>
    public const string OperatorDecisionRequired = "operator_decision_required";

    /// <summary>The tourist tax rate of the comune is not known (never taken as zero).</summary>
    public const string MunicipalityRateMissing = "municipality_rate_missing";

    /// <summary>The police credentials kept in CasaZen were refused by the service.</summary>
    public const string CredentialsRefused = "credentials_refused";

    /// <summary>The property has no CIN.</summary>
    public const string CinMissing = "cin_missing";

    /// <summary>The CIN is not in the official format.</summary>
    public const string CinInvalid = "cin_invalid";

    // The items of the cockpit that exist today and are not computed from a proof: the host has something to do.

    /// <summary>The property is not active: the activation is incomplete.</summary>
    public const string ActivationIncomplete = "activation_incomplete";

    /// <summary>The data of the guests of the stay are incomplete.</summary>
    public const string GuestDataMissing = "guest_data_missing";

    /// <summary>The stay ends and the check-out is not closed.</summary>
    public const string CheckoutDue = "checkout_due";

    /// <summary>The property was not declared ready after the check-out.</summary>
    public const string PropertyReadyNotConfirmed = "property_ready_not_confirmed";

    /// <summary>The host has something to do and there is nothing more specific to say.</summary>
    public const string ActionRequired = "action_required";

    /// <summary>Every code, in the order of the lights.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        ReceiptArchived, IdentityVerified, CinValid,
        AwaitingReceipt, EntrustedToChannelManager, ExternalVerificationRequired, AlloggiatiSending, SelfieAwaited, PeriodOpen, ConnectionUnverified,
        EvidenceMissing, DeadlineOverdue, ReceiptMissing, AlloggiatiNotSent, AlloggiatiFailed, AlloggiatiIncomplete, OverThirtyNightsNotSent,
        ShortStayAtRisk, ConnectionInactive, IdentityMissing, OperatorDecisionRequired, MunicipalityRateMissing, CredentialsRefused, CinMissing,
        CinInvalid, ActivationIncomplete, GuestDataMissing, CheckoutDue, PropertyReadyNotConfirmed, ActionRequired,
    ];
}

/// <summary>The facts of the Alloggiati Web communication of one stay whose arrival day has come (a no-show owes none and is never asked).</summary>
public sealed record AlloggiatiFacts
{
    /// <summary>
    /// Who sends it. The channel manager only counts as the sender while the connector is on: otherwise nothing is entrusted to it and the
    /// host sends (the caller passes <see cref="ComplianceSenderType.Manual"/>).
    /// </summary>
    public required ComplianceSenderType Sender { get; init; }

    /// <summary>The status shown for the stay (<see cref="AlloggiatiStatusRules.Effective"/>).</summary>
    public required AlloggiatiWebStatus Status { get; init; }

    /// <summary>Nights of the stay (<see cref="AlloggiatiTerms.StayDays"/>).</summary>
    public required int StayNights { get; init; }

    /// <summary>The legal deadline of the communication (UTC, <see cref="AlloggiatiTerms.DeadlineUtc(DateTime?, DateTime, DateTime)"/>).</summary>
    public required DateTime DeadlineUtc { get; init; }

    /// <summary>Now (UTC).</summary>
    public required DateTime NowUtc { get; init; }

    /// <summary>Today in Europe/Rome.</summary>
    public required DateOnly TodayRome { get; init; }

    /// <summary>Every registered guest of the stay is on Octorate, sent for good (not as a draft): the stay is entrusted to the channel manager.</summary>
    public bool GuestsEntrusted { get; init; }

    /// <summary>When the communication was sent or declared sent (UTC); <c>null</c> when not known.</summary>
    public DateTime? CommunicatedAt { get; init; }

    /// <summary>The state of the send of the channel manager of the property, when it is the sender and it was read.</summary>
    public ConnectionHealthState? Connection { get; init; }

    /// <summary>The archived proof of the day of arrival: a receipt CasaZen downloaded, or one the host uploaded.</summary>
    public ComplianceProof? Proof { get; init; }
}

/// <summary>The facts of the identity verification of the guest who leaves the document, for a stay with a check-in session.</summary>
public sealed record IdentityFacts
{
    /// <summary>The start of the arrival day (UTC).</summary>
    public required DateTime ArrivalStartUtc { get; init; }

    /// <summary>Now (UTC).</summary>
    public required DateTime NowUtc { get; init; }

    /// <summary>The hours before the arrival from which a missing verification is red (<c>Compliance:IdentityRedHoursBeforeArrival</c>).</summary>
    public required int RedHoursBeforeArrival { get; init; }

    /// <summary>The proof that the verification passed (a face comparison that was accepted, or an operator's decision).</summary>
    public ComplianceProof? Proof { get; init; }

    /// <summary>An operator has to decide: the comparison asked for a review or the attempts are used, and nobody decided yet.</summary>
    public bool OperatorDecisionNeeded { get; init; }
}

/// <summary>The facts of the police send of the channel manager of a property.</summary>
public sealed record ConnectionFacts
{
    /// <summary>What CasaZen can tell (<see cref="IComplianceConnectionHealthService"/>).</summary>
    public required ConnectionHealthState State { get; init; }

    /// <summary>The host said it saw «Activated» in the Octorate panel: its word, not a verification, but the host did what it could.</summary>
    public bool DeclaredByHost { get; init; }
}

/// <summary>The facts of the police credentials kept in CasaZen.</summary>
public sealed record CredentialsFacts
{
    /// <summary>The outcome of the last verification with the service; <c>null</c> before the first (and always in this release).</summary>
    public bool? LastVerificationOk { get; init; }
}

/// <summary>The facts of the ISTAT communication of one month of a property.</summary>
public sealed record IstatFacts
{
    /// <summary>Who sends it (ISTAT follows Alloggiati).</summary>
    public required ComplianceSenderType Sender { get; init; }

    /// <summary>Today in Europe/Rome.</summary>
    public required DateOnly Today { get; init; }

    /// <summary>The first day of the month.</summary>
    public required DateOnly PeriodStart { get; init; }

    /// <summary>The day of the following month by which the region wants it; <c>null</c> = the region is «to define»: no item, never a guess.</summary>
    public int? DeadlineDay { get; init; }

    /// <summary>The archived proof of the communication of the period (an upload of the host).</summary>
    public ComplianceProof? Proof { get; init; }
}

/// <summary>The facts of the tourist tax of one month of a property.</summary>
public sealed record TaxPeriodFacts
{
    /// <summary>Today in Europe/Rome.</summary>
    public required DateOnly Today { get; init; }

    /// <summary>The first day of the month.</summary>
    public required DateOnly PeriodStart { get; init; }

    /// <summary>The archived receipt of the declaration of the period.</summary>
    public ComplianceProof? Declaration { get; init; }

    /// <summary>The archived receipt of the payment of the period.</summary>
    public ComplianceProof? Payment { get; init; }
}

/// <summary>A verdict on a stay together with the cockpit action that fixes it.</summary>
/// <param name="Action">The action of the item: the host sends, resolves a failure, sends by hand or uploads the receipt.</param>
/// <param name="Verdict">The light and the reason.</param>
public sealed record ComplianceStayVerdict(ComplianceCockpitAction Action, ComplianceVerdict Verdict);

/// <summary>
/// The one place where a compliance light is decided (E03/F02/US01/T03, AC-NF52-2, AC-NF52-14): pure functions from facts to a verdict.
/// <b>Green only ever comes from a <see cref="ComplianceProof"/></b>: a rule that has none returns yellow or red, and nothing in the facts
/// (an Octorate handoff, the user coming back from it, a declaration, a connection that is active) can make an item green. Where the outcome is
/// held only by Octorate, the verdict is yellow with <see cref="ComplianceReasonCodes.ExternalVerificationRequired"/> and the handoff topic,
/// never an inferred success or failure; a deadline CasaZen owns can still make it red.
/// </summary>
public static class ComplianceLightRules
{
    /// <summary>How many days ahead the identity of the arriving guests is looked at.</summary>
    public const int IdentityLookaheadDays = 7;

    /// <summary>
    /// A stay of this many nights or fewer has the 6-hour term (<see cref="AlloggiatiTerms.IsShortStay"/>, which counts the same nights): the
    /// daily run of the channel manager does not cover it unless its guests were entrusted before the arrival.
    /// </summary>
    private const int ShortStayNights = 1;

    /// <summary>
    /// The Alloggiati Web communication of a stay. In order: a failure is red whatever else exists; an archived proof of the arrival day is
    /// green; a communication sent, entrusted or declared with no receipt is yellow until the day after and red from then on; a send the
    /// channel manager cannot make (over 30 nights, a connection that is not active, a short stay not entrusted) is red with the action to
    /// send by hand; guests entrusted to the channel manager are yellow within the deadline and red after it.
    /// </summary>
    public static ComplianceStayVerdict Alloggiati(AlloggiatiFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (AlloggiatiStatusRules.IsFailure(facts.Status))
            return new(ComplianceCockpitAction.ResolveAlloggiatiFailure, ComplianceVerdict.ActionNeeded(ComplianceReasonCodes.AlloggiatiFailed));

        if (facts.Status == AlloggiatiWebStatus.PartiallyAccepted)
            return new(ComplianceCockpitAction.ResolveAlloggiatiFailure, ComplianceVerdict.ActionNeeded(ComplianceReasonCodes.AlloggiatiIncomplete));

        if (facts.Proof is { } proof)
            return new(ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceVerdict.Proven(proof, ComplianceReasonCodes.ReceiptArchived));

        var channelManager = facts.Sender == ComplianceSenderType.ChannelManager;
        var topic = channelManager ? OctorateHandoffTopic.Alloggiati : (OctorateHandoffTopic?)null;

        switch (facts.Status)
        {
            case AlloggiatiWebStatus.Sending:
                return new(ComplianceCockpitAction.UploadAlloggiatiReceipt, ComplianceVerdict.InProgress(ComplianceReasonCodes.AlloggiatiSending));

            case AlloggiatiWebStatus.ToVerify:
                // The outcome is unknown (timeout, connection lost): the agency checks it on the portal, nothing is inferred.
                return new(
                    ComplianceCockpitAction.UploadAlloggiatiReceipt,
                    ComplianceVerdict.InProgress(ComplianceReasonCodes.ExternalVerificationRequired, ComplianceEvidenceClass.ExternalVerificationRequired, OctorateHandoffTopic.Alloggiati));

            case AlloggiatiWebStatus.Inviato or AlloggiatiWebStatus.InviatoManualmente or AlloggiatiWebStatus.SentReceiptPending:
                // Sent, or declared sent, and no receipt is archived: yellow the day of the send, red from the day after.
                var sentOn = RomeCalendar.DateInRome(facts.CommunicatedAt ?? facts.NowUtc);
                return new(
                    ComplianceCockpitAction.UploadAlloggiatiReceipt,
                    facts.TodayRome > sentOn
                        ? ComplianceVerdict.ActionNeeded(ComplianceReasonCodes.ReceiptMissing, topic: topic)
                        : ComplianceVerdict.InProgress(ComplianceReasonCodes.AwaitingReceipt, topic: topic));
        }

        if (channelManager)
        {
            // What the channel manager does not send by itself: the host sends it by hand.
            if (facts.StayNights > AlloggiatiTerms.MaxStayDaysPerSchedina)
                return new(ComplianceCockpitAction.SendAlloggiatiManually, ComplianceVerdict.ActionNeeded(ComplianceReasonCodes.OverThirtyNightsNotSent));

            if (facts.Connection == ConnectionHealthState.Inactive)
                return new(ComplianceCockpitAction.SendAlloggiatiManually, ComplianceVerdict.ActionNeeded(ComplianceReasonCodes.ConnectionInactive));

            if (facts.GuestsEntrusted)
            {
                // Entrusted: the channel manager sends them. Yellow within the legal term, red when it passed without a receipt.
                return new(
                    ComplianceCockpitAction.UploadAlloggiatiReceipt,
                    facts.NowUtc > facts.DeadlineUtc
                        ? ComplianceVerdict.ActionNeeded(ComplianceReasonCodes.ReceiptMissing, topic: topic)
                        : ComplianceVerdict.InProgress(
                            ComplianceReasonCodes.EntrustedToChannelManager, ComplianceEvidenceClass.ExternalVerificationRequired, topic));
            }

            if (facts.StayNights <= ShortStayNights)
                return new(ComplianceCockpitAction.SendAlloggiatiManually, ComplianceVerdict.ActionNeeded(ComplianceReasonCodes.ShortStayAtRisk));
        }

        // Due and not sent: the host sends it on the portal.
        return new(
            ComplianceCockpitAction.SendAlloggiati,
            ComplianceVerdict.ActionNeeded(facts.NowUtc > facts.DeadlineUtc ? ComplianceReasonCodes.DeadlineOverdue : ComplianceReasonCodes.AlloggiatiNotSent));
    }

    /// <summary>
    /// The identity of the guest who leaves the document (NF-50, AC-NF50-8): green on a passed verification (a face comparison that was
    /// accepted, or an operator's decision), red when an operator has to decide or the arrival is within
    /// <see cref="IdentityFacts.RedHoursBeforeArrival"/> hours, yellow while the selfie is awaited.
    /// </summary>
    public static ComplianceVerdict Identity(IdentityFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Proof is { } proof)
            return ComplianceVerdict.Proven(proof, ComplianceReasonCodes.IdentityVerified);

        if (facts.OperatorDecisionNeeded)
            return ComplianceVerdict.ActionNeeded(ComplianceReasonCodes.OperatorDecisionRequired);

        return facts.NowUtc >= facts.ArrivalStartUtc.AddHours(-Math.Max(0, facts.RedHoursBeforeArrival))
            ? ComplianceVerdict.ActionNeeded(ComplianceReasonCodes.IdentityMissing)
            : ComplianceVerdict.InProgress(ComplianceReasonCodes.SelfieAwaited);
    }

    /// <summary>
    /// The police send of the channel manager of a property: red when Octorate says it does not send, yellow when CasaZen cannot tell and
    /// the host has not said it saw «Activated»; nothing when it is active (that is no proof of any communication, so never green here), when
    /// the host declared it, or when the host sends by hand.
    /// </summary>
    public static ComplianceVerdict? Connection(ConnectionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return facts.State switch
        {
            ConnectionHealthState.Inactive => ComplianceVerdict.ActionNeeded(ComplianceReasonCodes.ConnectionInactive),
            ConnectionHealthState.Unknown when !facts.DeclaredByHost => ComplianceVerdict.InProgress(ComplianceReasonCodes.ConnectionUnverified),
            _ => null,
        };
    }

    /// <summary>The police credentials kept in CasaZen: red when the service refused them. Nothing otherwise.</summary>
    public static ComplianceVerdict? Credentials(CredentialsFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        return facts.LastVerificationOk == false ? ComplianceVerdict.ActionNeeded(ComplianceReasonCodes.CredentialsRefused) : null;
    }

    /// <summary>
    /// The ISTAT communication of a closed month. A region without a deadline is «to define»: no item, never a guess. Green with the
    /// archived proof of the period; yellow until the region's deadline (when Octorate sends it, the outcome is Octorate's:
    /// <see cref="ComplianceReasonCodes.ExternalVerificationRequired"/> with the handoff topic); red after it.
    /// </summary>
    public static ComplianceVerdict? Istat(IstatFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.DeadlineDay is not { } day || day is < 1 or > 31)
            return null;

        if (facts.Proof is { } proof)
            return ComplianceVerdict.Proven(proof, ComplianceReasonCodes.ReceiptArchived);

        if (facts.Today > IstatDeadline(facts.PeriodStart, day))
            return ComplianceVerdict.ActionNeeded(ComplianceReasonCodes.DeadlineOverdue);

        return facts.Sender == ComplianceSenderType.ChannelManager
            ? ComplianceVerdict.InProgress(
                ComplianceReasonCodes.ExternalVerificationRequired, ComplianceEvidenceClass.ExternalVerificationRequired, OctorateHandoffTopic.Istat)
            : ComplianceVerdict.InProgress(ComplianceReasonCodes.PeriodOpen);
    }

    /// <summary>The deadline of the ISTAT communication of the month starting on <paramref name="periodStart"/>: day <paramref name="day"/> of the next month (the last day if it has fewer).</summary>
    public static DateOnly IstatDeadline(DateOnly periodStart, int day)
    {
        var next = periodStart.AddMonths(1);
        return new DateOnly(next.Year, next.Month, Math.Min(Math.Max(day, 1), DateTime.DaysInMonth(next.Year, next.Month)));
    }

    /// <summary>
    /// The tourist tax of a month. Nothing while the month is open. Closed: green with the declaration and the payment receipts both archived,
    /// yellow with one of them, red with none (never a presumed payment).
    /// </summary>
    public static ComplianceVerdict? TouristTaxPeriod(TaxPeriodFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var lastDay = facts.PeriodStart.AddMonths(1).AddDays(-1);
        if (facts.Today <= lastDay)
            return null;

        if (facts.Declaration is { } declaration && facts.Payment is { } payment)
        {
            var verifiedAt = declaration.VerifiedAt is { } d && payment.VerifiedAt is { } p ? (d > p ? d : p) : declaration.VerifiedAt ?? payment.VerifiedAt;
            return ComplianceVerdict.Proven(
                new ComplianceProof($"{declaration.Reference}+{payment.Reference}", ComplianceEvidenceSources.ManualUpload, verifiedAt),
                ComplianceReasonCodes.ReceiptArchived);
        }

        return facts.Declaration is not null || facts.Payment is not null
            ? ComplianceVerdict.InProgress(ComplianceReasonCodes.AwaitingReceipt)
            : ComplianceVerdict.ActionNeeded(ComplianceReasonCodes.EvidenceMissing);
    }

    /// <summary>The tourist tax rate of the comune: red when it is not known (never a presumed zero); nothing when it is.</summary>
    public static ComplianceVerdict? MunicipalityRate(bool rateKnown) =>
        rateKnown ? null : ComplianceVerdict.ActionNeeded(ComplianceReasonCodes.MunicipalityRateMissing);

    /// <summary>The CIN of a property (computed, never stored): green when <see cref="CinFormat"/> says it is valid, red otherwise.</summary>
    public static ComplianceVerdict Cin(string? cin) =>
        CinFormat.GetStatus(cin) switch
        {
            CinStatus.Valid => ComplianceVerdict.Proven(
                new ComplianceProof($"cin:{CinFormat.Normalize(cin)}", ComplianceEvidenceSources.CinFormatCheck), ComplianceReasonCodes.CinValid),
            CinStatus.Invalid => ComplianceVerdict.ActionNeeded(ComplianceReasonCodes.CinInvalid),
            _ => ComplianceVerdict.ActionNeeded(ComplianceReasonCodes.CinMissing),
        };

    /// <summary>
    /// The light of an item of the cockpit that exists today and is not computed from a proof (a property to activate, guest data to
    /// complete, a check-out, a turnover) or whose facts the status did not read: the host has something to do, so red, with the code that says
    /// what. Never green and never yellow without a reason.
    /// </summary>
    public static ComplianceVerdict OpenAction(ComplianceCockpitAction action) =>
        ComplianceVerdict.ActionNeeded(action switch
        {
            ComplianceCockpitAction.ActivateProperty => ComplianceReasonCodes.ActivationIncomplete,
            ComplianceCockpitAction.CompleteGuestCheckIn => ComplianceReasonCodes.GuestDataMissing,
            ComplianceCockpitAction.CheckOut => ComplianceReasonCodes.CheckoutDue,
            ComplianceCockpitAction.ConfirmPropertyReady => ComplianceReasonCodes.PropertyReadyNotConfirmed,
            ComplianceCockpitAction.SendAlloggiati => ComplianceReasonCodes.AlloggiatiNotSent,
            ComplianceCockpitAction.ResolveAlloggiatiFailure => ComplianceReasonCodes.AlloggiatiFailed,
            _ => ComplianceReasonCodes.ActionRequired,
        });

    /// <summary>The month of a date as the periods name it: <c>yyyy-MM</c>.</summary>
    public static string PeriodKey(DateOnly date) =>
        date.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
}
