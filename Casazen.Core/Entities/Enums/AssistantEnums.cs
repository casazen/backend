namespace Casazen.Core.Entities.Enums;

/// <summary>
/// What a guest's message is about, as the assistant recognizes it (E05/F05/US01/T01, NF-66): the 14 situations of the requirement plus the one that
/// matches none. Stored as an integer, <b>append only</b>: a value is never renumbered. The stable text code, the sensitive categories a situation carries
/// by nature and the modes it allows are in <c>AssistantSituationCatalog</c> (T03).
/// </summary>
public enum AssistantSituation
{
    CheckInTime = 1,
    EarlyArrivalOrLateCheckout = 2,
    Parking = 3,
    WiFi = 4,
    Directions = 5,
    Pets = 6,
    InvoiceRequest = 7,
    ThankYou = 8,
    Malfunction = 9,
    Noise = 10,
    DiscountRequest = 11,
    CancellationOrRefund = 12,
    Complaint = 13,
    Emergency = 14,

    /// <summary>The message matches none of the situations above: a person decides.</summary>
    Other = 99,
}

/// <summary>
/// What happens when a situation is recognized, and the trust level of NF-67 (one value for both): <see cref="Escalate"/> raises a "To do" for a
/// person, <see cref="Draft"/> (the default of every situation, PO-08) writes a draft that waits for approval, <see cref="Automatic"/> sends by itself
/// when every condition of the gate holds, <see cref="AutomaticWithExceptions"/> does too and escalates the exceptions. A situation with a sensitive
/// category never goes beyond <see cref="Draft"/>. Stored as an integer.
/// </summary>
public enum AssistantMode
{
    Escalate = 0,
    Draft = 1,
    Automatic = 2,
    AutomaticWithExceptions = 3,
}

/// <summary>How soon a person must look at a message, as the assistant judged it. Stored as an integer.</summary>
public enum AssistantUrgency
{
    Normal = 1,
    High = 2,
    Urgent = 3,
}

/// <summary>What the assistant did with an incoming message (the decision log). Stored as an integer.</summary>
public enum AssistantDecision
{
    /// <summary>A draft was written and waits for a person.</summary>
    Drafted = 1,

    /// <summary>The draft passed the gate and was sent by itself.</summary>
    AutoSent = 2,

    /// <summary>A "To do" was raised for a person; no draft.</summary>
    Escalated = 3,

    /// <summary>The message was not processed (assistant off, no property, too old, no AI): the code is in the log.</summary>
    NotHandled = 4,
}

/// <summary>
/// The categories of message the assistant never sends by itself (NF-67): money, cancellation and refund, complaint, damage, safety, legal matters and
/// requests for personal data. A set of bits: a situation or a message can carry several. Stored as an integer, <b>append only</b>.
/// </summary>
[Flags]
public enum SensitiveCategory
{
    None = 0,
    Money = 1,
    CancellationRefund = 2,
    Complaint = 4,
    Damage = 8,
    Safety = 16,
    Legal = 32,
    PersonalData = 64,
}

/// <summary>What a piece of knowledge of a property is about (E05/F05/US01/T01, NF-66). Stored as an integer, <b>append only</b>.</summary>
public enum KnowledgeTopic
{
    CheckIn = 1,
    CheckOut = 2,
    WiFi = 3,
    Parking = 4,
    HouseRules = 5,
    Hours = 6,
    Amenities = 7,
    Directions = 8,
    Pets = 9,
    Contacts = 10,
    Faq = 11,
}

/// <summary>The kind of an entry of the knowledge base a host wrote: a question with its answer, a fact, or a rule. Stored as an integer.</summary>
public enum KnowledgeEntryKind
{
    Faq = 1,
    Fact = 2,
    Rule = 3,
}

/// <summary>Where an entry of the knowledge base comes from: written by a person, or learnt from an approved lesson (E05/F05/US02). Stored as an integer.</summary>
public enum KnowledgeSource
{
    Custom = 1,
    Lesson = 2,
}

/// <summary>The tone of the drafts of the assistant (NF-66). Stored as an integer.</summary>
public enum AssistantTone
{
    Neutral = 1,
    Friendly = 2,
    Formal = 3,
}

/// <summary>What a lesson proposed from a correction of a draft says: a fact, or a rule (NF-66). Stored as an integer.</summary>
public enum LessonKind
{
    Fact = 1,
    Rule = 2,
}

/// <summary>Where a lesson stands: proposed by the assistant, then approved or rejected by a person; nothing enters without approval (AC-NF66-6). Stored as an integer.</summary>
public enum LessonStatus
{
    Proposed = 1,
    Approved = 2,
    Rejected = 3,
}

/// <summary>Where an approved lesson applies: the property it came from, every property, or the whole organization (NF-66). Stored as an integer.</summary>
public enum LessonScope
{
    Property = 1,
    AllProperties = 2,
    Organization = 3,
}

/// <summary>Where a change of trust level proposed by CasaZen stands: it changes only when a person accepts it (AC-NF67-5). Stored as an integer.</summary>
public enum TrustProposalStatus
{
    Proposed = 1,
    Accepted = 2,
    Dismissed = 3,
}
