using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Events;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IInvoiceDataService"/> (FIS-01, NF-81, E06/F02/US01/T02): who an invoice of a booking is made out to, saved with the precedence of the
/// sources decided on the row.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The whole result is checked, nothing partial is saved.</b> The command is merged with the stored data into a copy, the copy is checked
/// (<see cref="InvoiceDataFormats.Validate"/>), and only a valid result is written: a 422 saves nothing. The identification of the recipient is
/// required of what a person typed (Host, CheckIn), not of an import, which often has a name and an address and no fiscal code.</item>
/// <item><b>Precedence.</b> When the source of the row outranks the incoming one (<see cref="InvoiceDataPrecedence"/>), the incoming data fills only
/// the fields that are empty and the row keeps its source; otherwise it overwrites the fields it sends and the row takes the incoming source. The
/// kind of recipient and the country have a default, so they count as empty only while the row holds no data of the recipient, or no address.</item>
/// <item><b>Two writers.</b> The row carries <c>xmin</c> as its concurrency token and a unique key on the booking: the writer that lost reads the row
/// the other one saved and applies the precedence to it, so a host's edit is never overwritten by an import that read the row before it.</item>
/// <item><b>Event.</b> A save that wrote something records <see cref="DomainEventType.InvoiceDataSaved"/> in the same unit of work (the recorder is
/// optional, so the tests that build the service by hand keep working; the container always provides it).</item>
/// <item><b>Logs</b> carry ids, sources and field names: never a value of the data.</item>
/// </list>
/// </remarks>
public sealed class InvoiceDataService(
    AppDbContext db,
    IDomainEventRecorder? recorder = null,
    TimeProvider? timeProvider = null,
    ILogger<InvoiceDataService>? logger = null) : IInvoiceDataService
{
    /// <summary>Attempts of a save when another writer saved the same row, or made it, at the same moment.</summary>
    private const int MaxAttempts = 3;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ILogger<InvoiceDataService> _logger = logger ?? NullLogger<InvoiceDataService>.Instance;

    public async Task<InvoiceDataView?> GetAsync(Guid bookingId, CancellationToken ct = default)
    {
        var row = await db.BookingInvoiceData.AsNoTracking().FirstOrDefaultAsync(d => d.BookingId == bookingId, ct);
        return row is null ? null : InvoiceDataView.Of(row);
    }

    public async Task<InvoiceDataView> SaveAsync(
        Guid bookingId,
        SaveInvoiceDataCommand command,
        InvoiceDataSource source,
        string? updatedByUserId = null,
        bool save = true,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Enum.IsDefined(source))
            throw new ArgumentOutOfRangeException(nameof(source), source, "Not a source of the invoice data.");

        var by = updatedByUserId is { Length: > 0 }
            ? updatedByUserId[..Math.Min(updatedByUserId.Length, BookingInvoiceData.UpdatedByUserIdMaxLength)]
            : null;

        for (var attempt = 1; ; attempt++)
        {
            var orgId = await OrgOfBookingAsync(bookingId, ct);
            var row = await FindTrackedAsync(bookingId, ct);
            var created = row is null;

            var current = row is null ? EmptyState : StateOf(row);
            var fillOnly = row is not null && InvoiceDataPrecedence.Outranks(row.Source, source);

            // The kind of recipient and the country have a default: a lower source may set them only while there is nothing they describe yet.
            var kindLocked = fillOnly && HasRecipient(current);
            var countryLocked = fillOnly && HasAddress(current);
            var sent = command with
            {
                RecipientKind = kindLocked ? null : command.RecipientKind,
                CountryCode = countryLocked ? null : command.CountryCode,
            };
            var incoming = InvoiceDataFormats.Normalize(sent, InvoiceDataFormats.EffectiveCountry(sent, current.CountryCode));

            var (merged, changed) = Merge(current, incoming, fillOnly);
            if (fillOnly && !changed)
                return InvoiceDataView.Of(row!);

            var failures = InvoiceDataFormats.Validate(merged, requireIdentification: source is InvoiceDataSource.Host or InvoiceDataSource.CheckIn);
            if (failures.Count > 0)
            {
                _logger.LogInformation(
                    "Invoice data of booking {BookingId} refused ({Source}): {Fields}",
                    bookingId,
                    source,
                    string.Join(',', failures.Select(f => f.Field)));
                throw InvoiceDataErrorCodes.ToException(failures);
            }

            var now = _clock.GetUtcNow().UtcDateTime;
            if (row is null)
            {
                row = new BookingInvoiceData { OrgId = orgId, BookingId = bookingId, CreatedAt = now };
                db.BookingInvoiceData.Add(row);
            }

            Copy(merged, row);
            if (!fillOnly)
            {
                row.Source = source;
                row.UpdatedByUserId = by;
            }

            row.UpdatedAt = now;
            recorder?.Record(
                DomainEventType.InvoiceDataSaved, bookingId, new InvoiceDataSavedPayload(bookingId, source), orgId: orgId);

            if (!save)
                return InvoiceDataView.Of(row);

            try
            {
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("Invoice data of booking {BookingId} saved ({Source}, fill only: {FillOnly})", bookingId, source, fillOnly);
                return InvoiceDataView.Of(row);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // Another writer saved the row after this one read it: read it again and apply the precedence to what was saved first.
                Release(row, bookingId);
            }
            catch (DbUpdateException ex) when (created && attempt < MaxAttempts && OrgTeamAccess.IsUniqueViolation(ex, BookingInvoiceData.BookingIndexName))
            {
                // Another writer made the row of this booking first: the same, as an update of the row it made.
                Release(row, bookingId);
            }
        }
    }

    public async Task MarkRequestedAsync(Guid bookingId, bool needInvoice, bool save = true, CancellationToken ct = default)
    {
        for (var attempt = 1; ; attempt++)
        {
            var row = await FindTrackedAsync(bookingId, ct);
            var created = false;
            var now = _clock.GetUtcNow().UtcDateTime;

            if (needInvoice)
            {
                if (row is { InvoiceRequested: true })
                    return;

                if (row is null)
                {
                    row = new BookingInvoiceData
                    {
                        OrgId = await OrgOfBookingAsync(bookingId, ct),
                        BookingId = bookingId,
                        Source = InvoiceDataSource.CheckIn,
                        CreatedAt = now,
                    };
                    db.BookingInvoiceData.Add(row);
                    created = true;
                }

                row.InvoiceRequested = true;
                row.InvoiceRequestedAt = now;
            }
            else
            {
                // A «no» makes no row; it only takes back a «yes».
                if (row is not { InvoiceRequested: true })
                    return;

                row.InvoiceRequested = false;
                row.InvoiceRequestedAt = null;
            }

            row.UpdatedAt = now;
            if (!save)
                return;

            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                Release(row, bookingId);
            }
            catch (DbUpdateException ex) when (created && attempt < MaxAttempts && OrgTeamAccess.IsUniqueViolation(ex, BookingInvoiceData.BookingIndexName))
            {
                Release(row, bookingId);
            }
        }
    }

    /// <summary>
    /// The org of the booking, through the tenant filter of the caller: a booking of another org is not found (404), exactly like a missing one.
    /// </summary>
    private async Task<Guid> OrgOfBookingAsync(Guid bookingId, CancellationToken ct)
    {
        var orgId = await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => (Guid?)b.OrgId)
            .FirstOrDefaultAsync(ct);

        return orgId ?? throw new NotFoundException($"Booking {bookingId} was not found")
        {
            Code = "booking_not_found",
            MessageKey = "BookingNotFound",
        };
    }

    /// <summary>The row of the booking to change: the one this unit of work already holds (staged or read), else the one in the database.</summary>
    private async Task<BookingInvoiceData?> FindTrackedAsync(Guid bookingId, CancellationToken ct)
    {
        var local = db.ChangeTracker.Entries<BookingInvoiceData>()
            .FirstOrDefault(e => e.Entity.BookingId == bookingId && e.State != EntityState.Detached);
        return local?.Entity ?? await db.BookingInvoiceData.FirstOrDefaultAsync(d => d.BookingId == bookingId, ct);
    }

    /// <summary>Takes back the row and the event of an attempt that lost to another writer, so the next attempt starts from what is saved.</summary>
    private void Release(BookingInvoiceData row, Guid bookingId)
    {
        db.Entry(row).State = EntityState.Detached;
        foreach (var entry in db.ChangeTracker.Entries<OutboxEvent>()
                     .Where(e => e.State == EntityState.Added
                                 && e.Entity.EntityId == bookingId
                                 && e.Entity.EventType == DomainEventType.InvoiceDataSaved)
                     .ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private static readonly SaveInvoiceDataCommand EmptyState = new()
    {
        RecipientKind = InvoiceRecipientKind.Guest,
        CountryCode = BookingInvoiceData.DefaultCountryCode,
    };

    private static SaveInvoiceDataCommand StateOf(BookingInvoiceData row) => new()
    {
        RecipientKind = row.RecipientKind,
        Name = row.Name,
        FiscalCode = row.FiscalCode,
        VatNumber = row.VatNumber,
        AddressStreet = row.AddressStreet,
        AddressCivic = row.AddressCivic,
        PostalCode = row.PostalCode,
        City = row.City,
        Province = row.Province,
        CountryCode = row.CountryCode,
        SdiRecipientCode = row.SdiRecipientCode,
        Pec = row.Pec,
    };

    private static void Copy(SaveInvoiceDataCommand state, BookingInvoiceData row)
    {
        row.RecipientKind = state.RecipientKind ?? InvoiceRecipientKind.Guest;
        row.Name = Empty(state.Name);
        row.FiscalCode = Empty(state.FiscalCode);
        row.VatNumber = Empty(state.VatNumber);
        row.AddressStreet = Empty(state.AddressStreet);
        row.AddressCivic = Empty(state.AddressCivic);
        row.PostalCode = Empty(state.PostalCode);
        row.City = Empty(state.City);
        row.Province = Empty(state.Province);
        row.CountryCode = Empty(state.CountryCode) ?? BookingInvoiceData.DefaultCountryCode;
        row.SdiRecipientCode = Empty(state.SdiRecipientCode);
        row.Pec = Empty(state.Pec);
    }

    /// <summary>
    /// The stored state with the incoming data applied, and whether anything differs. A member that is <c>null</c> was not sent. With
    /// <paramref name="fillOnly"/> only a field that is empty takes a value, and no value is ever cleared; otherwise a field that is sent takes its
    /// value, an empty one clearing it.
    /// </summary>
    private static (SaveInvoiceDataCommand State, bool Changed) Merge(SaveInvoiceDataCommand current, SaveInvoiceDataCommand incoming, bool fillOnly)
    {
        var changed = false;

        string? Pick(string? stored, string? sent)
        {
            if (sent is null)
                return stored;

            var value = Empty(sent);
            if (fillOnly && (!string.IsNullOrEmpty(stored) || value is null))
                return stored;

            if (!string.Equals(stored ?? string.Empty, value ?? string.Empty, StringComparison.Ordinal))
                changed = true;
            return value;
        }

        var kind = current.RecipientKind;
        if (incoming.RecipientKind is { } sentKind && sentKind != current.RecipientKind)
        {
            kind = sentKind;
            changed = true;
        }

        var state = current with
        {
            RecipientKind = kind,
            Name = Pick(current.Name, incoming.Name),
            FiscalCode = Pick(current.FiscalCode, incoming.FiscalCode),
            VatNumber = Pick(current.VatNumber, incoming.VatNumber),
            AddressStreet = Pick(current.AddressStreet, incoming.AddressStreet),
            AddressCivic = Pick(current.AddressCivic, incoming.AddressCivic),
            PostalCode = Pick(current.PostalCode, incoming.PostalCode),
            City = Pick(current.City, incoming.City),
            Province = Pick(current.Province, incoming.Province),
            SdiRecipientCode = Pick(current.SdiRecipientCode, incoming.SdiRecipientCode),
            Pec = Pick(current.Pec, incoming.Pec),
        };

        // The country is never empty: sending an empty one is asking for the default.
        if (incoming.CountryCode is { } sentCountry)
        {
            var country = Empty(sentCountry) ?? BookingInvoiceData.DefaultCountryCode;
            if (!string.Equals(country, current.CountryCode, StringComparison.Ordinal))
            {
                state = state with { CountryCode = country };
                changed = true;
            }
        }

        return (state, changed);
    }

    /// <summary>True when the state holds anything that says who the recipient is: the base of the kind of recipient.</summary>
    private static bool HasRecipient(SaveInvoiceDataCommand state) =>
        !string.IsNullOrEmpty(state.Name) || !string.IsNullOrEmpty(state.FiscalCode) || !string.IsNullOrEmpty(state.VatNumber);

    /// <summary>True when the state holds any part of the address: the base of the country.</summary>
    private static bool HasAddress(SaveInvoiceDataCommand state) =>
        !string.IsNullOrEmpty(state.AddressStreet)
        || !string.IsNullOrEmpty(state.AddressCivic)
        || !string.IsNullOrEmpty(state.PostalCode)
        || !string.IsNullOrEmpty(state.City)
        || !string.IsNullOrEmpty(state.Province);

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
