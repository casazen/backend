using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// Who an invoice of a booking is made out to (FIS-01, NF-81, E06/F02/US01/T02): reads and saves the <see cref="BookingInvoiceData"/> of a
/// booking, whoever the writer is (the host in the booking, the guest at the check-in, the channel manager's import), with the precedence of the
/// sources decided on the row.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Tenant.</b> The service works through the tenant query filter of its caller (a request, or an event handler under the org of its event) and
/// copies the org of the booking onto the row. A booking of another org is not found: <see cref="Casazen.Core.Exceptions.NotFoundException"/>
/// (404) for a save, <c>null</c> for a read. A caller that works for no tenant (the public check-in) names a booking it already authenticated.</item>
/// <item><b>Precedence</b> (<see cref="InvoiceDataPrecedence"/>): Host over CheckIn over Octorate. When the source of the row outranks the incoming
/// one, the incoming data fills only the empty fields and never overwrites a stored value; otherwise it overwrites the fields it sends and the row
/// takes the incoming source.</item>
/// <item><b>Event.</b> A save that wrote something records <c>InvoiceDataSaved</c> (the booking and the source, nothing personal) in the same unit of
/// work, so the push to the channel manager (E06/F02/US01/T04) sees it once the data is committed (ADR-022-R1).</item>
/// <item><b>No document, no Alloggiati data.</b> The service reads and copies nothing from the identity documents or the guests of the stay.</item>
/// </list>
/// </remarks>
public interface IInvoiceDataService
{
    /// <summary>The invoice data of a booking, or <c>null</c> when the booking has none (or is not visible to the caller).</summary>
    Task<InvoiceDataView?> GetAsync(Guid bookingId, CancellationToken ct = default);

    /// <summary>
    /// Saves <paramref name="command"/> for the booking (an upsert), validating the whole resulting data: 422 <see cref="Casazen.Core.Exceptions.DomainRuleException"/>
    /// with the code and the field of each failure (<see cref="Casazen.Core.Regulatory.InvoiceDataErrorCodes"/>) and nothing saved. The identification of
    /// the recipient (a fiscal code or a VAT number) is required of what a person typed (<see cref="InvoiceDataSource.Host"/>,
    /// <see cref="InvoiceDataSource.CheckIn"/>), not of the partial data of an import. A save that changes nothing (a fill-only save that fills nothing)
    /// writes nothing and records no event.
    /// </summary>
    /// <param name="bookingId">The booking.</param>
    /// <param name="command">What the source sends; a member that is <c>null</c> is not sent and keeps the stored value, an empty one clears it.</param>
    /// <param name="source">Who sends it: it decides, with the source of the row, whether the data overwrites or only fills.</param>
    /// <param name="updatedByUserId">The person who saved it (the host); <c>null</c> for the guest and the channel manager.</param>
    /// <param name="save">
    /// <c>true</c> (the default) saves at once and, when another writer won the row in the meantime, reads it again and applies the precedence to the
    /// version that was saved first. <c>false</c> only stages the row and its event in the unit of work of the caller, who saves them with its own
    /// change (the public check-in).
    /// </param>
    /// <param name="ct">Cancellation.</param>
    Task<InvoiceDataView> SaveAsync(
        Guid bookingId,
        SaveInvoiceDataCommand command,
        InvoiceDataSource source,
        string? updatedByUserId = null,
        bool save = true,
        CancellationToken ct = default);

    /// <summary>
    /// Answers the question «would this save be refused?» without writing anything: the same 422 <see cref="SaveAsync"/> would answer (the data of
    /// the command merged with the stored data and checked as a whole), or nothing when it would be accepted. A caller that saves the data with
    /// something else (the public check-in) asks it first, so a refusal leaves everything else unsaved. A booking that is not visible is a 404.
    /// </summary>
    Task EnsureValidAsync(Guid bookingId, SaveInvoiceDataCommand command, InvoiceDataSource source, CancellationToken ct = default);

    /// <summary>
    /// The guest's answer to «Ti serve la fattura?»: <c>true</c> sets <see cref="BookingInvoiceData.InvoiceRequested"/> and its time (the row is made when
    /// the booking has none, with the source <see cref="InvoiceDataSource.CheckIn"/>); <c>false</c> clears them, and makes no row. It does not touch the
    /// data of the recipient and records no event. <paramref name="save"/> as in <see cref="SaveAsync"/>.
    /// </summary>
    Task MarkRequestedAsync(Guid bookingId, bool needInvoice, bool save = true, CancellationToken ct = default);
}

/// <summary>
/// What a source sends of the invoice data of a booking (FIS-01). Every member is optional: <c>null</c> is «not sent» and keeps what is stored, an
/// empty text clears the field. The text is normalized by the service (<see cref="Casazen.Core.Regulatory.InvoiceDataFormats.Normalize"/>) before it is checked.
/// </summary>
public sealed record SaveInvoiceDataCommand
{
    public InvoiceRecipientKind? RecipientKind { get; init; }

    public string? Name { get; init; }

    public string? FiscalCode { get; init; }

    public string? VatNumber { get; init; }

    public string? AddressStreet { get; init; }

    public string? AddressCivic { get; init; }

    public string? PostalCode { get; init; }

    public string? City { get; init; }

    public string? Province { get; init; }

    public string? CountryCode { get; init; }

    public string? SdiRecipientCode { get; init; }

    public string? Pec { get; init; }

    /// <summary>True when the command sends at least one member with something in it (a text that is not only white space).</summary>
    public bool HasContent =>
        RecipientKind is not null
        || new[] { Name, FiscalCode, VatNumber, AddressStreet, AddressCivic, PostalCode, City, Province, CountryCode, SdiRecipientCode, Pec }
            .Any(text => !string.IsNullOrWhiteSpace(text));
}

/// <summary>The invoice data of a booking as the service gives it (FIS-01): who it is for, where it came from and whether the guest asked for it.</summary>
public sealed record InvoiceDataView(
    Guid BookingId,
    bool InvoiceRequested,
    DateTime? InvoiceRequestedAt,
    InvoiceRecipientKind RecipientKind,
    string? Name,
    string? FiscalCode,
    string? VatNumber,
    string? AddressStreet,
    string? AddressCivic,
    string? PostalCode,
    string? City,
    string? Province,
    string CountryCode,
    string? SdiRecipientCode,
    string? Pec,
    InvoiceDataSource Source,
    DateTime UpdatedAt)
{
    /// <summary>The view of <paramref name="row"/>.</summary>
    public static InvoiceDataView Of(BookingInvoiceData row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new InvoiceDataView(
            row.BookingId,
            row.InvoiceRequested,
            row.InvoiceRequestedAt,
            row.RecipientKind,
            row.Name,
            row.FiscalCode,
            row.VatNumber,
            row.AddressStreet,
            row.AddressCivic,
            row.PostalCode,
            row.City,
            row.Province,
            row.CountryCode,
            row.SdiRecipientCode,
            row.Pec,
            row.Source,
            row.UpdatedAt);
    }

    /// <summary>True when the row holds anything about the recipient (not only the guest's «yes»).</summary>
    public bool HasRecipientData =>
        new[] { Name, FiscalCode, VatNumber, AddressStreet, AddressCivic, PostalCode, City, Province, SdiRecipientCode, Pec }
            .Any(text => !string.IsNullOrWhiteSpace(text));
}
