namespace Casazen.Core.Regulatory.GuestRegistration;

public enum GuestRegistrationObligation
{
    ReportToAuthority = 1,

    KeepRegister = 2,

    Optional = 3,

    Unknown = 4,
}

public enum GuestRegistrationChannel
{
    PortalFileUpload = 1,

    PortalForm = 2,

    VendorInterface = 3,

    EmailFile = 4,

    Paper = 5,

    None = 6,
}

public enum GuestRegistrationDeadlineKind
{
    Hours24FromArrival = 1,

    DailyByTime = 2,

    AtArrival = 3,

    /// <summary>At least once a week (Nidwalden).</summary>
    Weekly = 4,

    None = 5,
}

public enum GuestRegistrationPrivateRentals
{
    Yes = 1,
    Partial = 2,
    No = 3,
    Unknown = 4,
}

public enum GuestRegistrationCertainty
{
    Verified = 1,
    Partial = 2,
    Unverified = 3,
}

public enum GuestRegistrationSupport
{
    FileExport = 1,

    GuestRegister = 2,

    AssistedNotification = 3,

    InformationOnly = 4,
}

public enum GuestRegistrationEffectiveDuty
{
    Required = 1,

    NotRequired = 2,

    Ask = 3,
}

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
    public required string Code { get; init; }

    public required string CountryCode { get; init; }

    public string? CantonCode { get; init; }

    public required GuestRegistrationObligation Obligation { get; init; }

    public required GuestRegistrationChannel Channel { get; init; }

    public required GuestRegistrationDeadlineKind DeadlineKind { get; init; }

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

    public string? PortalUrl { get; init; }

    public required IReadOnlyList<string> LegalReferences { get; init; }

    public required IReadOnlyList<GuestRegistrationSource> Sources { get; init; }

    public required DateOnly VerifiedOn { get; init; }

    public required GuestRegistrationCertainty Certainty { get; init; }

    public required GuestRegistrationSupport CasaZenSupport { get; init; }

    public bool RetainDocumentImages { get; init; }

    public required string SummaryKey { get; init; }

    public bool IsSwiss => string.Equals(CountryCode, "CH", StringComparison.Ordinal);
}
