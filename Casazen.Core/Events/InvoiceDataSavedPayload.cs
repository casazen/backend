using System.Text.Json.Serialization;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Events;

/// <summary>
/// The payload of <see cref="DomainEventType.InvoiceDataSaved"/> (E06/F02/US01/T02, ADR-022-R21): the booking and the source of the save, ids and an
/// enum only. Never the name, the fiscal code, the VAT number, the address or the PEC: a consumer reads them from the booking by <c>EntityId</c>, in
/// its own org. The source is written by name, so a stored event stays readable if a source is added.
/// </summary>
public sealed record InvoiceDataSavedPayload(
    Guid BookingId,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] InvoiceDataSource Source);
