namespace Casazen.Core.Regulatory.GuestRegistration;

/// <summary>
/// What the law asks of whoever lodges guests, as far as the sources say it (E29/F02/US01/T01, NF-125, decisions D3 and D5). Numbered from 1 and
/// serialized by name; <see cref="Unknown"/> is the honest answer for a regime nobody read.
/// </summary>
public enum GuestRegistrationObligation
{
    /// <summary>The host transmits the data of the guests to an authority (the police) in a set way and time.</summary>
    ReportToAuthority = 1,

    /// <summary>The host keeps a register of the guests, to show to the police when it asks; nothing is transmitted.</summary>
    KeepRegister = 2,

    /// <summary>The register is a faculty of the host, not a duty (the cantonal law says «may»).</summary>
    Optional = 3,

    /// <summary>The sources do not say, or the place is not known.</summary>
    Unknown = 4,
}

/// <summary>How the data reach the authority, as the sources describe it. <see cref="None"/> where nothing is transmitted (a register kept at home).</summary>
public enum GuestRegistrationChannel
{
    /// <summary>A file uploaded by the host on the official portal (Ticino, Italy).</summary>
    PortalFileUpload = 1,

    /// <summary>A web portal where the host (or the guest) enters the data (a form, a platform of the canton or of its tourism body).</summary>
    PortalForm = 2,

    /// <summary>An interface of the management software of the host with a system of the canton: the specification is not public.</summary>
    VendorInterface = 3,

    /// <summary>A file sent by e-mail (Thurgau).</summary>
    EmailFile = 4,

    /// <summary>A paper form (the official one, or one the law prescribes).</summary>
    Paper = 5,

    /// <summary>No channel: nothing is transmitted, the host keeps the register.</summary>
    None = 6,
}

/// <summary>When the data are due, as the sources say it. <see cref="None"/> where there is no deadline or the sources do not give one.</summary>
public enum GuestRegistrationDeadlineKind
{
    /// <summary>Within 24 hours of the arrival (Ticino, Italy).</summary>
    Hours24FromArrival = 1,

    /// <summary>Every day, by a time where the source gives one (Schaffhausen) or with no cut-off time (Basel-Stadt, Neuchâtel).</summary>
    DailyByTime = 2,

    /// <summary>At the arrival of the guest (Zug), or as soon as the guest has taken the accommodation.</summary>
    AtArrival = 3,

    /// <summary>At least once a week (Nidwalden).</summary>
    Weekly = 4,

    /// <summary>No deadline, or none that the sources give: the note of the regime says which.</summary>
    None = 5,
}

/// <summary>Whether the rule covers who lets a private home on a platform or by himself: yes, in part (some guests, some hosts), no, or the sources do not say.</summary>
public enum GuestRegistrationPrivateRentals
{
    Yes = 1,
    Partial = 2,
    No = 3,
    Unknown = 4,
}

/// <summary>How far the sources of a regime are to be trusted (the scale of the research of 20 November 2026): read on an official source, reported by a summary or by a body that is not the authority, or not found.</summary>
public enum GuestRegistrationCertainty
{
    Verified = 1,
    Partial = 2,
    Unverified = 3,
}

/// <summary>What CasaZen does for a regime: the file for the portal, the register of the guests, the notification prepared for the host to make, or only the information.</summary>
public enum GuestRegistrationSupport
{
    /// <summary>CasaZen writes the file in the official format; the host uploads it.</summary>
    FileExport = 1,

    /// <summary>CasaZen keeps the data the register is made of and the host downloads it.</summary>
    GuestRegister = 2,

    /// <summary>CasaZen prepares the data and the deadline of a notification that the host makes itself.</summary>
    AssistedNotification = 3,

    /// <summary>CasaZen says what the rule is and nothing more.</summary>
    InformationOnly = 4,
}

/// <summary>What CasaZen treats as the duty of one property: raised (<see cref="Required"/>), not raised (<see cref="NotRequired"/>), or left to the host (<see cref="Ask"/>).</summary>
public enum GuestRegistrationEffectiveDuty
{
    /// <summary>A duty CasaZen raises: the regime is verified as applying to this kind of rental, or the host said so.</summary>
    Required = 1,

    /// <summary>A duty CasaZen does not raise: the host said so.</summary>
    NotRequired = 2,

    /// <summary>The sources do not settle it for a private rental: the host decides, and nothing is raised in red meanwhile.</summary>
    Ask = 3,
}

/// <summary>One source of a regime: the page or the text it was read on and its title.</summary>
/// <param name="Url">The address, https; a legal text, an official page or a publication of the authority.</param>
/// <param name="Title">The title of the page or of the text, as the source gives it (in its own language).</param>
public sealed record GuestRegistrationSource(string Url, string Title);

/// <summary>
/// The regime of one place for the registration of the guests (E29/F02/US01/T01): Italy, one of the 26 cantons or the Swiss place nobody knows. It is
/// data, never a decision: the effective duty of a property is made of it and of the host's setting (<see cref="GuestRegistrationDutyRules"/>).
/// </summary>
/// <remarks>
/// <para><b>One entry, copied from its source.</b> The cantons are filled from the research of 20 November 2026 (<c>alloggiati-ch/out/ricerca-*.md</c> and
/// <c>specifica-ticino.md</c>) and from nothing else: the obligation, the channel, the deadline, the retention, the certainty and the sources are what the
/// research says, with its doubts. What it calls «non verificato» is <see cref="GuestRegistrationCertainty.Unverified"/>, <see cref="GuestRegistrationObligation.Unknown"/>
/// or <see cref="GuestRegistrationPrivateRentals.Unknown"/> here, never a guess. An article number is never invented.</para>
/// <para><b>Certainty.</b> It is the certainty of the facts that make the entry (the obligation, who must comply, the channel, the deadline): it is
/// <see cref="GuestRegistrationCertainty.Verified"/> only when the research gives none of them as partial. Where a source leaves something open, the
/// summary text says it.</para>
/// <para><b>Texts.</b> The sentences for the user are resource keys of <c>SharedResources</c> (Italian and English): <see cref="SummaryKey"/> and, where
/// the deadline needs a sentence, <see cref="DeadlineNoteKey"/>. No supplier of software or of a cantonal system is named in them: the authorities and
/// their portals may be.</para>
/// </remarks>
public sealed record GuestRegistrationRegime
{
    /// <summary>The stable code: <c>IT</c>, <c>CH-TI</c>, ... <c>CH-ZH</c> (the two letters of the canton) or <c>CH-UNKNOWN</c>.</summary>
    public required string Code { get; init; }

    /// <summary>The country: <c>IT</c> or <c>CH</c>.</summary>
    public required string CountryCode { get; init; }

    /// <summary>The two letters of the canton; <c>null</c> for Italy and for <c>CH-UNKNOWN</c>.</summary>
    public string? CantonCode { get; init; }

    public required GuestRegistrationObligation Obligation { get; init; }

    public required GuestRegistrationChannel Channel { get; init; }

    public required GuestRegistrationDeadlineKind DeadlineKind { get; init; }

    /// <summary>The resource key of the note that says what the deadline is when the kind alone does not (a time, a doubt); <c>null</c> when it needs none.</summary>
    public string? DeadlineNoteKey { get; init; }

    /// <summary>
    /// The time of day, on the clock of the property, at which a <see cref="GuestRegistrationDeadlineKind.DailyByTime"/> deadline falls on the day after the arrival,
    /// where the sources give one (Schaffhausen: 02:00, and its brochure does not say which day the time refers to); <c>null</c> where they give none (Basel-Stadt and
    /// Neuchâtel say «every day» with no cut-off): the notification then has no deadline of its own and no alert, a time is never made up. Data copied from the source, never
    /// read for another kind of deadline (E31/F02/US01/T01).
    /// </summary>
    public TimeOnly? DeadlineTime { get; init; }

    /// <summary>The years the host keeps the registration data, where the sources give a number for it (a recommendation included, as the entry says); <c>null</c> otherwise, and also when the only number is how long a police system keeps the data (Thurgau, Zurich): that is not the host's duty.</summary>
    public int? RetentionYears { get; init; }

    public required GuestRegistrationPrivateRentals PrivateRentals { get; init; }

    /// <summary>The portal of the authority, https; <c>null</c> where there is none.</summary>
    public string? PortalUrl { get; init; }

    /// <summary>The laws and articles the sources cite, as written there (language of the law); empty when the sources cite none.</summary>
    public required IReadOnlyList<string> LegalReferences { get; init; }

    /// <summary>The pages and the texts the entry was read on: at least one for every regime.</summary>
    public required IReadOnlyList<GuestRegistrationSource> Sources { get; init; }

    /// <summary>The day the entry was read on its sources (the research of 20 November 2026 for the cantons).</summary>
    public required DateOnly VerifiedOn { get; init; }

    public required GuestRegistrationCertainty Certainty { get; init; }

    public required GuestRegistrationSupport CasaZenSupport { get; init; }

    /// <summary>
    /// CasaZen keeps the photograph of the identity document of the guests of this regime. <c>false</c> for every Swiss regime (decision D6: only the
    /// transcribed data are kept); <c>true</c> for Italy, where the existing retention of the documents applies unchanged.
    /// </summary>
    public bool RetainDocumentImages { get; init; }

    /// <summary>The resource key of the sentence that says what the rule is and what CasaZen does (Italian and English).</summary>
    public required string SummaryKey { get; init; }

    /// <summary>The regime is Swiss: its code starts with <c>CH-</c>.</summary>
    public bool IsSwiss => string.Equals(CountryCode, "CH", StringComparison.Ordinal);
}
