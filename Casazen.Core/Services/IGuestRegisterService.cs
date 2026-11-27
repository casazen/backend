namespace Casazen.Core.Services;

/// <summary>
/// The guest register of a Swiss property for a period (E31/F01/US01/T01, NF-130, decision D12b of <c>alloggiati-ch/analisi-tecnica.md</c>): the table of the guests of the stays that
/// arrived in it, which the host keeps and shows to the police where its canton only asks for a register, and copies from where its canton notifies on a portal. It is the same table for
/// every Swiss regime, Ticino included; an Italian property has none (its register is the Alloggiati file). CasaZen writes a table and gives no legal advice: the retention it says is the
/// hint of the regime (<see cref="Casazen.Core.Regulatory.GuestRegistration.GuestRegistrationRegime.RetentionYears"/>).
/// </summary>
/// <remarks>
/// <para><b>Which guests.</b> Every guest of the stays of the property that <b>arrived</b> in the period, by the stored arrival day, both ends counted: the check-in rows of the stay (adults and
/// minors) or, while it has none, the booker. A stay is counted only if it owes a police communication (<see cref="Casazen.Core.Regulatory.AlloggiatiStatusRules.ActiveBookingStatuses"/>,
/// the one definition: confirmed, checked in or checked out) and is not a no-show; a request, a hold, a cancelled booking and a no-show are never listed. Every read names the org of the
/// property, so a context with its tenant filter off never mixes organizations.</para>
/// <para><b>The period</b> is at most <see cref="Casazen.Core.Regulatory.GuestRegistration.GuestRegisterFile.MaxPeriodDays"/> days, both ends counted
/// (<c>swiss_period_invalid</c>, <c>swiss_period_too_long</c>: 422 from the service, 400 from the web layer, which says it before it asks).</para>
/// <para><b>Not stored, audited without personal data.</b> The file is rebuilt on every request. <see cref="GenerateAsync"/> writes one <c>SwissNotificationFileEvent</c> of format
/// <c>register</c> (the first day of the period, the number of lines, the hash of the file, the person who asked; no name, no document, no address, no code); a row that cannot be written fails
/// the request, so no register leaves unaudited. <see cref="GetInfoAsync"/> writes nothing.</para>
/// <para><b>The service never reads the caller or the flag</b>: the web layer decides who may ask (<c>guest.read</c>, the property in the caller's reach) and keeps both endpoints behind
/// <c>Features:GuestRegistrationRegimes</c>.</para>
/// </remarks>
public interface IGuestRegisterService
{
    /// <summary>
    /// What the register of a period holds, before it is downloaded: the number of lines, the retention hint of the regime and its code. Throws <c>property_not_found</c> (no such property in
    /// the org), <c>guest_register_not_available</c> (the property is not in Switzerland), <c>swiss_period_invalid</c> or <c>swiss_period_too_long</c>.
    /// </summary>
    Task<GuestRegisterInfo> GetInfoAsync(Guid propertyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    /// <summary>
    /// The register of a period as a file (CSV, UTF-8 with its byte order mark), rebuilt from the check-in of that moment and never stored; one audit row, no personal data. A period with no
    /// guest is the header alone. Throws what <see cref="GetInfoAsync"/> throws, and <c>csv_too_many_rows</c> when the period holds more lines than the writer takes.
    /// </summary>
    Task<GuestRegisterDocument> GenerateAsync(
        Guid propertyId,
        DateOnly from,
        DateOnly to,
        GuestRegisterRequest? request = null,
        CancellationToken cancellationToken = default);
}

/// <summary>Who asks for a register and in which language the words of the file are (<see cref="IGuestRegisterService.GenerateAsync"/>).</summary>
/// <param name="UserId">The person who asks (<c>User.Id</c>, the Auth0 subject), written on the audit row; <c>null</c> when no person asks.</param>
/// <param name="Language"><c>it</c> or <c>en</c> (anything else is Italian): the language of the header and of the words of the file.</param>
public sealed record GuestRegisterRequest(string? UserId = null, string? Language = null);

/// <summary>What the register of a period holds (<see cref="IGuestRegisterService.GetInfoAsync"/>). No guest data: counts, a number of years and a code.</summary>
/// <param name="PropertyId">The property.</param>
/// <param name="From">The first day of the period.</param>
/// <param name="To">The last day of the period.</param>
/// <param name="RowCount">The lines of the register: the guests of the stays that arrived in the period (the header is not counted).</param>
/// <param name="RetentionYears">The years the regime says the host keeps the register; <c>null</c> when the sources do not say, and no number is invented.</param>
/// <param name="RegimeCode">The code of the regime of the property (<c>CH-TI</c>, <c>CH-ZH</c>... or <c>CH-UNKNOWN</c> while its municipality is not known).</param>
public sealed record GuestRegisterInfo(Guid PropertyId, DateOnly From, DateOnly To, int RowCount, int? RetentionYears, string RegimeCode);

/// <summary>A register as a file (<see cref="IGuestRegisterService.GenerateAsync"/>).</summary>
/// <param name="Content">The bytes: CSV, UTF-8 with its byte order mark.</param>
/// <param name="FileName">Suggested name, e.g. <c>registro-ospiti-ch-2026-01-01-2026-03-31.csv</c>.</param>
/// <param name="ContentType"><c>text/csv; charset=utf-8</c>.</param>
/// <param name="FileHash">SHA-256 of <paramref name="Content"/>, lowercase hexadecimal: the one written on the audit row.</param>
/// <param name="Rows">The lines of the register, the header not counted.</param>
/// <param name="RetentionYears">The retention hint of the regime; <c>null</c> when the sources do not say (the header <c>X-Retention-Years</c> is then absent).</param>
/// <param name="RegimeCode">The code of the regime of the property.</param>
public sealed record GuestRegisterDocument(
    byte[] Content, string FileName, string ContentType, string FileHash, int Rows, int? RetentionYears, string RegimeCode);

/// <summary>The codes and message keys of the guest register.</summary>
public static class GuestRegisterErrorCodes
{
    /// <summary>404: the property is not in Switzerland (an Italian property's register is the Alloggiati file) or the catalog has no regime for its country.</summary>
    public const string NotAvailable = "guest_register_not_available";

    public const string NotAvailableMessageKey = "GuestRegisterNotAvailable";

    /// <summary>400: the format asked is not <c>csv</c>.</summary>
    public const string FormatInvalid = "guest_register_format_invalid";

    public const string FormatInvalidMessageKey = "GuestRegisterFormatInvalid";
}
