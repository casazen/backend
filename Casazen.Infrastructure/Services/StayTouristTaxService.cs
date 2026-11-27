using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.TouristTax;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc />
/// <remarks>
/// The tax is always calculated from the stay as it is now: the confirmed record (<see cref="StayTouristTax"/>) is what the host confirmed, and
/// <see cref="StayTouristTaxView.Stale"/> says that the stay changed since. The record also keeps the host's declaration of the collector of a
/// channel stay with no channel rule, which can come before any confirmation. No log carries a name, an age or a reason of exemption.
/// </remarks>
public sealed class StayTouristTaxService(
    AppDbContext db,
    IStayGuestService stayGuests,
    ITouristTaxQuoteService quotes,
    ILogger<StayTouristTaxService> logger,
    TimeProvider? timeProvider = null) : IStayTouristTaxService
{
    public const string NotCalculableCode = "tax_not_calculable";
    public const string ExemptionUnknownCode = "tax_exemption_unknown";
    public const string GuestUnknownCode = "tax_guest_unknown";
    public const string CollectorNotApplicableCode = "tax_collector_not_applicable";
    public const string RateOfficialExistsCode = "tax_rate_official_exists";
    public const string RateSourceRequiredCode = "tax_rate_source_required";
    public const string RateInvalidCode = "tax_rate_invalid";
    public const string ConfirmConflictCode = "tax_confirm_conflict";

    private static readonly TouristTaxQuoteStatus[] NotCalculable =
    [
        TouristTaxQuoteStatus.RateUnavailable,
        TouristTaxQuoteStatus.ChildAgesRequired,
        TouristTaxQuoteStatus.CategoryRequired,
        TouristTaxQuoteStatus.NightlyPriceRequired,
    ];

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<StayTouristTaxView> GetAsync(Guid bookingId, CancellationToken cancellationToken = default)
    {
        var booking = await LoadBookingAsync(bookingId, cancellationToken);
        return (await CalculateAsync(booking, cancellationToken)).View;
    }

    public async Task<StayTouristTaxView> SetExemptionsAsync(
        Guid bookingId,
        IReadOnlyList<GuestExemption> exemptions,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exemptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var booking = await LoadBookingAsync(bookingId, cancellationToken);
        var rows = await db.StayGuests.Where(s => s.BookingId == booking.Id).ToListAsync(cancellationToken);
        var choosable = (await ChoosableReasonsAsync(cancellationToken)).Select(r => r.Code).ToHashSet(StringComparer.Ordinal);

        // Every exemption is checked before any is written: the request is applied whole or not at all.
        var changes = new List<(StayGuest Row, string? Code)>(exemptions.Count);
        foreach (var exemption in exemptions)
        {
            var row = rows.FirstOrDefault(r => r.Position == exemption.Position)
                ?? throw new DomainRuleException(GuestUnknownCode, "TouristTaxGuestUnknown");
            var code = NormalizeCode(exemption.Code);
            if (code is not null && !choosable.Contains(code))
                throw new DomainRuleException(ExemptionUnknownCode, "TouristTaxExemptionUnknown");
            changes.Add((row, code));
        }

        var now = UtcNow();
        foreach (var (row, code) in changes)
        {
            if (row.TaxExemptionCode == code)
                continue;
            row.TaxExemptionCode = code;
            row.UpdatedAt = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Tourist tax exemptions of booking {BookingId} set by {UserId} for {Count} guests", booking.Id, userId, changes.Count);
        return (await CalculateAsync(booking, cancellationToken)).View;
    }

    public async Task<StayTouristTaxView> SetCollectorAsync(
        Guid bookingId,
        TouristTaxCollector? collector,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var booking = await LoadBookingAsync(bookingId, cancellationToken);
        var state = await CalculateAsync(booking, cancellationToken);

        // A declaration only where nothing else knows: a channel stay whose comune has no rule. Any other stay is the host's (or the rule's).
        if (!TouristTaxCollectorRules.IsChannelBooking(booking.Source) || state.Rule is not null)
            throw new DomainRuleException(CollectorNotApplicableCode, "TouristTaxCollectorNotApplicable");

        var now = UtcNow();
        var record = state.Record;
        if (record is null)
        {
            // A declaration before any confirmation: the row is created unconfirmed, with no line and no amount (only a row with ConfirmedAt is
            // a confirmed calculation).
            record = new StayTouristTax
            {
                OrgId = booking.OrgId,
                BookingId = booking.Id,
                Status = state.Breakdown.Status,
                Nights = state.Stay.Nights,
                TaxableNights = 0,
                Total = 0m,
                AmountDue = 0m,
                RetainUntil = StayTouristTax.RetentionFor(state.Stay.CheckOut),
                CreatedAt = now,
            };
            db.StayTouristTaxes.Add(record);
        }

        record.DeclaredCollector = collector;
        record.DeclaredByUserId = Truncate(userId, StayTouristTax.UserIdMaxLength);
        record.DeclaredAt = now;
        record.UpdatedAt = now;
        await SaveRecordAsync(cancellationToken);

        logger.LogInformation(
            "Tourist tax collector of booking {BookingId} declared by {UserId}: {Collector}", booking.Id, userId, collector);
        return (await CalculateAsync(booking, cancellationToken)).View;
    }

    public async Task<MunicipalityRateView> GetMunicipalityRateAsync(Guid propertyId, CancellationToken cancellationToken = default)
    {
        var property = await LoadPropertyAsync(propertyId, cancellationToken);
        var comune = TouristTaxComune.ForProperty(property);
        var official = await OfficialRateInForceAsync(comune, cancellationToken);
        var rate = comune.IsEmpty ? null : await FindDeclaredRateAsync(property.OrgId, comune, tracked: false, cancellationToken);
        return new MunicipalityRateView(property.Id, comune.IstatCode, comune.Name, official, rate);
    }

    public async Task<MunicipalityRateView> SaveMunicipalityRateAsync(
        Guid propertyId,
        MunicipalityRateInput input,
        string userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var property = await LoadPropertyAsync(propertyId, cancellationToken);
        var comune = TouristTaxComune.ForProperty(property);
        if (comune.IsEmpty)
            throw new DomainRuleException(RateInvalidCode, "TouristTaxRateInvalid");

        // The official rate applies wherever there is one: a host never declares over it (AC-NF46-6, ADR-037-R5).
        if (await OfficialRateInForceAsync(comune, cancellationToken))
            throw new DomainConflictException(RateOfficialExistsCode, "TouristTaxRateOfficialExists");

        var sourceNote = input.SourceNote?.Trim();
        if (string.IsNullOrEmpty(sourceNote))
            throw new DomainRuleException(RateSourceRequiredCode, "TouristTaxRateSourceRequired");

        var seasonStart = NullIfBlank(input.SeasonStart);
        var seasonEnd = NullIfBlank(input.SeasonEnd);
        if (input.RatePerPersonPerNight is < OrgTouristTaxRate.MinRate or > OrgTouristTaxRate.MaxRate
            || decimal.Round(input.RatePerPersonPerNight, 2) != input.RatePerPersonPerNight
            || input.MaxNights is < 1
            || input.MinimumAge is < 0 or > TouristTaxCalculator.AdultAge
            || !TouristTaxSeason.IsValidPair(seasonStart, seasonEnd)
            || sourceNote.Length > OrgTouristTaxRate.SourceNoteMaxLength)
        {
            throw new DomainRuleException(RateInvalidCode, "TouristTaxRateInvalid");
        }

        var now = UtcNow();
        var row = await FindDeclaredRateAsync(property.OrgId, comune, tracked: true, cancellationToken);
        if (row is null)
        {
            row = new OrgTouristTaxRate { OrgId = property.OrgId, CreatedAt = now };
            db.OrgTouristTaxRates.Add(row);
        }

        row.IstatCode = NullIfBlank(comune.IstatCode);
        row.City = Truncate(comune.Name?.Trim() ?? string.Empty, OrgTouristTaxRate.CityMaxLength);
        row.RatePerPersonPerNight = input.RatePerPersonPerNight;
        row.MaxNights = input.MaxNights;
        row.MinimumAge = input.MinimumAge;
        row.SeasonStart = seasonStart;
        row.SeasonEnd = seasonEnd;
        row.EffectiveFrom = input.EffectiveFrom ?? _clock.TodayInRomeAsDateOnly();
        row.SourceNote = sourceNote;
        row.EnteredByUserId = Truncate(userId, OrgTouristTaxRate.UserIdMaxLength);
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Tourist tax rate of comune {Comune} ({IstatCode}) declared for org {OrgId} by {UserId}",
            comune.Name, comune.IstatCode, property.OrgId, userId);
        return new MunicipalityRateView(property.Id, comune.IstatCode, comune.Name, OfficialRateInForce: false, row);
    }

    public async Task<StayTouristTaxView> ConfirmAsync(Guid bookingId, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var booking = await LoadBookingAsync(bookingId, cancellationToken);
        var state = await CalculateAsync(booking, cancellationToken);
        var breakdown = state.Breakdown;
        if (NotCalculable.Contains(breakdown.Status) || breakdown.Total is not { } total)
            throw new DomainRuleException(NotCalculableCode, "TouristTaxNotCalculable");

        // Idempotent: the same calculation confirmed again changes nothing (no second line, no new time). The same calculation is the same
        // hash and the same lines (the reasons of the exemptions are compared on the lines, not in the hash).
        var record = state.Record;
        if (record is { ConfirmedAt: not null } && !state.View.Stale)
            return state.View;

        var now = UtcNow();
        if (record is null)
        {
            record = new StayTouristTax { OrgId = booking.OrgId, BookingId = booking.Id, CreatedAt = now };
            db.StayTouristTaxes.Add(record);
        }

        record.Status = breakdown.Status;
        record.Nights = breakdown.Nights;
        record.TaxableNights = breakdown.TaxableNights;
        record.Total = total;
        record.AmountDue = state.Decision.AmountDue(total);
        record.Collector = state.Decision.Collector;
        record.ChannelRuleId = state.Decision.Source == TouristTaxCollectorSource.ChannelRule ? state.Rule?.Id : null;
        record.CalculationHash = state.Hash;
        record.ConfirmedAt = now;
        record.ConfirmedByUserId = Truncate(userId, StayTouristTax.UserIdMaxLength);
        record.RetainUntil = StayTouristTax.RetentionFor(state.Stay.CheckOut);
        record.UpdatedAt = now;
        ReplaceLines(record, breakdown.Guests);
        await SaveRecordAsync(cancellationToken);

        // No PII: the booking, the outcome and the counts only.
        logger.LogInformation(
            "Tourist tax of booking {BookingId} confirmed by {UserId}: {Guests} guests, collector {Collector}",
            booking.Id, userId, breakdown.Guests.Count, record.Collector);
        return (await CalculateAsync(booking, cancellationToken)).View;
    }

    /// <summary>The age in full years on <paramref name="checkIn"/>; null without a date of birth or with one after it.</summary>
    public static int? AgeAt(DateTime? dateOfBirth, DateOnly checkIn)
    {
        if (dateOfBirth is not { } birth)
            return null;

        var born = DateOnly.FromDateTime(birth);
        var age = checkIn.Year - born.Year;
        if (checkIn < born.AddYears(age))
            age--;
        return age < 0 ? null : age;
    }

    /// <summary>The stay as it is now, its breakdown, the collector, the record and the view.</summary>
    private async Task<StayTaxState> CalculateAsync(Booking booking, CancellationToken cancellationToken)
    {
        var guests = await stayGuests.GetForBookingAsync(booking, cancellationToken);
        var checkIn = RomeCalendar.DateInRome(booking.CheckInDate);
        var checkOut = RomeCalendar.DateInRome(booking.CheckOutDate);
        var nights = checkOut.DayNumber - checkIn.DayNumber;
        if (guests.Count == 0 || nights is <= 0 or > TouristTaxCalculator.MaxNights)
            throw new DomainRuleException(NotCalculableCode, "TouristTaxNotCalculable");

        var stay = new TouristTaxGuestStay(
            checkIn,
            checkOut,
            guests.Select(g => new TouristTaxGuest(g.Position, AgeAt(g.DateOfBirth, checkIn), g.TaxExemptionCode)).ToList(),
            AccommodationCategory: null,
            NightlyPrice: NightlyPrice(booking, nights));
        var comune = TouristTaxComune.ForProperty(booking.Property);
        var breakdown = await quotes.QuoteGuestsAsync(comune, stay, booking.OrgId, cancellationToken);

        var record = await db.StayTouristTaxes
            .Include(t => t.Guests)
            .FirstOrDefaultAsync(t => t.BookingId == booking.Id, cancellationToken);
        var rule = await FindChannelRuleAsync(booking, comune, cancellationToken);
        var decision = TouristTaxCollectorRules.Decide(booking.Source, rule, record?.DeclaredCollector);
        var hash = Hash(stay, breakdown, decision, rule);
        var reasons = await ChoosableReasonsAsync(cancellationToken);

        var calculated = breakdown.Status == TouristTaxQuoteStatus.Calculated && breakdown.Total is not null;
        var lines = calculated
            ? breakdown.Guests
                .Select(g => new StayTouristTaxGuestView(g.Position, g.Nights.Count, g.TaxableNights, g.Amount, g.ExemptionKind, g.ExemptionCode))
                .ToList()
            : stay.Guests
                .Select(g => new StayTouristTaxGuestView(
                    g.Position,
                    nights,
                    0,
                    null,
                    g.HostExemptionCode is null ? TouristTaxExemptionKind.None : TouristTaxExemptionKind.Host,
                    g.HostExemptionCode))
                .ToList();
        var rateSource = breakdown.AppliedRates.Any(r => r.SourceAuthority == OrgTouristTaxRate.HostAuthority)
            ? TouristTaxRateSource.HostDeclared
            : breakdown.Status == TouristTaxQuoteStatus.RateUnavailable ? TouristTaxRateSource.None : TouristTaxRateSource.Official;

        var view = new StayTouristTaxView(
            booking.Id,
            breakdown.Status,
            rateSource,
            MissingRate: breakdown.Status == TouristTaxQuoteStatus.RateUnavailable,
            nights,
            breakdown.TaxableNights,
            breakdown.AgeRulesApply,
            breakdown.Categories,
            calculated ? breakdown.Total : null,
            calculated ? decision.AmountDue(breakdown.Total!.Value) : null,
            decision.Collector,
            decision.Source,
            decision.CollectorNeeded,
            lines,
            reasons.Select(r => new TouristTaxExemptionReasonView(r.Code, r.LabelKey)).ToList(),
            record?.ConfirmedAt,
            Stale: record?.ConfirmedAt is not null
                && (!string.Equals(record.CalculationHash, hash, StringComparison.Ordinal) || !LinesMatch(record, breakdown)));

        return new StayTaxState(stay, breakdown, rule, decision, record, hash, view);
    }

    /// <summary>
    /// The lines of the record, updated in place by position (a line per guest, unique: the same guest is never taxed twice), the lines of the
    /// positions that are gone removed.
    /// </summary>
    private void ReplaceLines(StayTouristTax record, IReadOnlyList<TouristTaxGuestResult> guests)
    {
        foreach (var stale in record.Guests.Where(l => guests.All(g => g.Position != l.Position)).ToList())
        {
            record.Guests.Remove(stale);
            db.Remove(stale);
        }

        foreach (var guest in guests)
        {
            var line = record.Guests.FirstOrDefault(l => l.Position == guest.Position);
            if (line is null)
            {
                line = new StayTouristTaxGuest { StayTouristTaxId = record.Id, Position = guest.Position };
                record.Guests.Add(line);
            }

            line.ExemptionKind = guest.ExemptionKind;
            line.ExemptionCode = guest.ExemptionCode;
            line.TaxableNights = guest.TaxableNights;
            line.Amount = guest.Amount;
        }
    }

    /// <summary>
    /// The SHA-256 (hex) of what the tax of the stay depends on: the dates, the rates used, each guest's position, the amounts and the collector
    /// with its rule. A confirmed record whose hash differs from the current one is stale. Neither the age nor the reason of an exemption is in
    /// it: an unsalted hash of a reason (which can reveal a health condition) can be tried against the short list of reasons, even after the
    /// reason was erased. A change of age shows in the amounts, a change of reason in <see cref="LinesMatch"/>.
    /// </summary>
    private static string Hash(
        TouristTaxGuestStay stay,
        TouristTaxBreakdown breakdown,
        TouristTaxCollectorDecision decision,
        TouristTaxChannelRule? rule)
    {
        var text = new StringBuilder("stay-tourist-tax:v2");
        text.Append('|').Append(stay.CheckIn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append('|').Append(stay.CheckOut.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Append("|status:").Append((int)breakdown.Status);
        foreach (var rateId in breakdown.AppliedRates.Select(r => r.Id).Order())
            text.Append("|rate:").Append(rateId.ToString("N"));
        foreach (var guest in stay.Guests.OrderBy(g => g.Position))
            text.Append("|guest:").Append(guest.Position.ToString(CultureInfo.InvariantCulture));

        text.Append("|total:").Append(breakdown.Total?.ToString(CultureInfo.InvariantCulture) ?? "-");
        foreach (var guest in breakdown.Guests.OrderBy(g => g.Position))
        {
            text.Append("|amount:").Append(guest.Position.ToString(CultureInfo.InvariantCulture))
                .Append(':').Append(guest.Amount.ToString(CultureInfo.InvariantCulture));
        }

        text.Append("|collector:").Append(decision.Collector?.ToString() ?? "-")
            .Append("|rule:").Append(decision.Source == TouristTaxCollectorSource.ChannelRule ? rule?.Id.ToString("N") : "-");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// The lines of the confirmed record are still what the calculation gives now, guest by guest: the same kind of exemption and the same
    /// reason. The reason is compared here, on the (decrypted) lines, and not through the hash, so a change of one reason into another (both
    /// exempt, the same amounts) still makes the record stale.
    /// </summary>
    private static bool LinesMatch(StayTouristTax record, TouristTaxBreakdown breakdown) =>
        record.Guests.Count == breakdown.Guests.Count
        && breakdown.Guests.All(guest =>
            record.Guests.FirstOrDefault(line => line.Position == guest.Position) is { } stored
            && stored.ExemptionKind == guest.ExemptionKind
            && string.Equals(stored.ExemptionCode, guest.ExemptionCode, StringComparison.Ordinal));

    /// <summary>
    /// The price of a night of the booking, for a percentage rate: the lodging the booking recorded (its base price without the cleaning) over
    /// its nights, or the rate of the property when the booking has none.
    /// </summary>
    private static decimal NightlyPrice(Booking booking, int nights)
    {
        var lodging = booking.BasePrice - booking.CleaningFee;
        return nights > 0 && lodging > 0 ? lodging / nights : booking.Property.NightlyRate;
    }

    private async Task<Booking> LoadBookingAsync(Guid bookingId, CancellationToken cancellationToken) =>
        await db.Bookings
            .Include(b => b.Property)
            .Include(b => b.Guest)
            .FirstOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
        ?? throw new NotFoundException($"Booking {bookingId} not found") { MessageKey = "BookingNotFound" };

    private async Task<Property> LoadPropertyAsync(Guid propertyId, CancellationToken cancellationToken) =>
        await db.Properties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == propertyId, cancellationToken)
        ?? throw new NotFoundException($"Property {propertyId} not found") { MessageKey = "PropertyNotFound" };

    /// <summary>The active channel rule of the booking's channel in its comune (by ISTAT code); null for any other stay.</summary>
    private async Task<TouristTaxChannelRule?> FindChannelRuleAsync(
        Booking booking,
        TouristTaxComune comune,
        CancellationToken cancellationToken)
    {
        var istatCode = comune.IstatCode?.Trim();
        if (!TouristTaxCollectorRules.IsChannelBooking(booking.Source) || string.IsNullOrEmpty(istatCode))
            return null;

        return await db.TouristTaxChannelRules
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Channel == booking.Source && r.IstatCode == istatCode && r.IsActive, cancellationToken);
    }

    /// <summary>The rate <paramref name="orgId"/> declared for the comune (an explicit predicate on the org), the latest when several.</summary>
    private async Task<OrgTouristTaxRate?> FindDeclaredRateAsync(
        Guid orgId,
        TouristTaxComune comune,
        bool tracked,
        CancellationToken cancellationToken)
    {
        var query = db.OrgTouristTaxRates.Where(r => r.OrgId == orgId);
        var rows = tracked ? await query.ToListAsync(cancellationToken) : await query.AsNoTracking().ToListAsync(cancellationToken);
        return rows
            .Where(r => comune.Matches(r.ToTransientRate()))
            .OrderByDescending(r => r.EffectiveFrom)
            .ThenByDescending(r => r.UpdatedAt)
            .FirstOrDefault();
    }

    private async Task<bool> OfficialRateInForceAsync(TouristTaxComune comune, CancellationToken cancellationToken) =>
        !comune.IsEmpty && (await quotes.GetRatesInForceAsync(comune, _clock.TodayInRomeAsDateOnly(), cancellationToken)).Count > 0;

    private async Task<List<TouristTaxExemptionReason>> ChoosableReasonsAsync(CancellationToken cancellationToken) =>
        await db.TouristTaxExemptionReasons
            .AsNoTracking()
            .Where(r => r.IsActive && !r.IsAutomatic)
            .OrderBy(r => r.SortOrder)
            .ThenBy(r => r.Code)
            .ToListAsync(cancellationToken);

    /// <summary>Saves the record; two writers of the first record of the same booking at once: the second answers 409.</summary>
    private async Task SaveRecordAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DomainConflictException(ConfirmConflictCode, "TouristTaxConfirmConflict");
        }
    }

    private DateTime UtcNow() => _clock.GetUtcNow().UtcDateTime;

    private static string? NormalizeCode(string? code)
    {
        var value = code?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Truncate(string value, int maxLength) => value.Length > maxLength ? value[..maxLength] : value;

    private sealed record StayTaxState(
        TouristTaxGuestStay Stay,
        TouristTaxBreakdown Breakdown,
        TouristTaxChannelRule? Rule,
        TouristTaxCollectorDecision Decision,
        StayTouristTax? Record,
        string Hash,
        StayTouristTaxView View);
}
