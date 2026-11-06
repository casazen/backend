using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Options;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IDocumentIssuerService"/> (FIS-02, NF-82, PO-05, E06/F02/US02/T02): who issues the documents of an organization and of each booking.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The mode is only accepted when it can work.</b> CasaZen: the flag <c>Features:FiscalDocumentIssuance</c> on and the profile complete
/// (<see cref="FiscalDocumentRules.MissingIssuerFields"/>); Octorate: an active connection of the org in this environment; the accountant: nothing.
/// A refused save changes nothing.</item>
/// <item><b>One issuer for a booking.</b> The row of <see cref="BookingDocumentIssuer"/> is unique for a booking: two parallel locks make one row and the
/// loser reads it, so the kind that lost is a 409 and the same kind a success. CasaZen is never locked as the issuer of an OTA booking.</item>
/// <item><b>No second source of truth.</b> The VAT number and the fiscal code of the issuer are read from <c>Org</c>; the regime code is the profile's own
/// or the rule's.</item>
/// <item><b>Logs</b> carry ids, modes and field names: never a value of the profile.</item>
/// </list>
/// </remarks>
public sealed class DocumentIssuerService(
    AppDbContext db,
    IFeatureFlags featureFlags,
    IOptions<FiscalDocumentRulesOptions> options,
    IHostEnvironment hostEnvironment,
    TimeProvider timeProvider,
    ILogger<DocumentIssuerService> logger) : IDocumentIssuerService
{
    /// <summary>Attempts of a save when another writer made the same row at the same moment.</summary>
    private const int MaxAttempts = 3;

    /// <summary>A regime code of FatturaPA is letters and digits, at most 4 (<c>RF01</c>).</summary>
    private static readonly Regex RegimeCodePattern = new("^[A-Z0-9]{1,4}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    /// <summary>A series is written into the number of a document: letters, digits and a few separators.</summary>
    private static readonly Regex SeriesPattern = new("^[A-Za-z0-9._/-]{1,20}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<IssuerSettingsView> GetSettingsAsync(Guid orgId, CancellationToken ct = default)
    {
        var profile = await db.OrgFiscalIssuerProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.OrgId == orgId, ct);
        var identity = await OrgIdentityAsync(orgId, ct);
        var connected = await IsOctorateConnectedAsync(orgId, ct);
        return ViewOf(profile, identity, connected);
    }

    public async Task<IssuerSettingsView> SaveSettingsAsync(
        Guid orgId,
        SaveIssuerSettingsCommand command,
        string? updatedByUserId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var failures = new List<InvoiceDataFailure>();
        if (!Enum.IsDefined(command.Mode))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.Mode));
        if (command.Regime is { } regime && !Enum.IsDefined(regime))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.Regime));

        var data = Normalize(command, failures);
        if (failures.Count > 0)
            throw FieldsInvalid(failures);

        var identity = await OrgIdentityAsync(orgId, ct);
        var rule = command.Regime is { } chosen ? options.Value.RuleOf(chosen) : null;
        var effectiveRegimeCode = data.FatturaPaRegimeCode ?? Empty(rule?.FatturaPaRegimeCode);

        // The mode is accepted only when it can work; a refusal saves nothing.
        switch (command.Mode)
        {
            case DocumentIssuerMode.CasaZenDirectOnly:
                if (!featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance))
                    throw new DomainRuleException(DocumentIssuerErrorCodes.IssuanceNotAvailable, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.IssuanceNotAvailable));

                var missing = FiscalDocumentRules.MissingIssuerFields(data, command.Regime, effectiveRegimeCode, identity.VatNumber, identity.FiscalCode);
                if (missing.Count > 0)
                {
                    throw new DomainRuleException(DocumentIssuerErrorCodes.ProfileIncomplete, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.ProfileIncomplete))
                    {
                        Extensions = new Dictionary<string, object?> { ["fields"] = missing },
                    };
                }

                break;
            case DocumentIssuerMode.Octorate:
                if (!await IsOctorateConnectedAsync(orgId, ct))
                    throw new DomainRuleException(DocumentIssuerErrorCodes.OctorateNotConnected, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.OctorateNotConnected));

                break;
        }

        for (var attempt = 1; ; attempt++)
        {
            var profile = await db.OrgFiscalIssuerProfiles.FirstOrDefaultAsync(p => p.OrgId == orgId, ct);
            var created = profile is null;
            var now = UtcNow;
            if (profile is null)
            {
                profile = new OrgFiscalIssuerProfile { OrgId = orgId };
                db.OrgFiscalIssuerProfiles.Add(profile);
            }

            profile.IssuerMode = command.Mode;
            profile.DocumentRegime = command.Regime;
            profile.LegalName = data.LegalName;
            profile.AddressStreet = data.AddressStreet;
            profile.AddressCivic = data.AddressCivic;
            profile.PostalCode = data.PostalCode;
            profile.City = data.City;
            profile.Province = data.Province;
            profile.CountryCode = data.CountryCode;
            profile.FatturaPaRegimeCode = data.FatturaPaRegimeCode;
            profile.IssuerSdiRecipientCode = data.IssuerSdiRecipientCode;
            profile.IssuerPec = data.IssuerPec;
            profile.DefaultSeries = data.DefaultSeries;
            if (command.Confirmed)
                profile.ConfirmedAt = now;

            profile.UpdatedAt = now;
            profile.UpdatedByUserId = updatedByUserId is { Length: > 0 }
                ? updatedByUserId[..Math.Min(updatedByUserId.Length, OrgFiscalIssuerProfile.UpdatedByUserIdMaxLength)]
                : null;

            try
            {
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Issuer setting of org {OrgId} saved: mode {Mode}", orgId, command.Mode);
                return ViewOf(profile, identity, await IsOctorateConnectedAsync(orgId, ct));
            }
            catch (DbUpdateException ex) when (created && attempt < MaxAttempts && OrgTeamAccess.IsUniqueViolation(ex, OrgFiscalIssuerProfile.OrgIndexName))
            {
                // Another save made the profile of this org first: the same, as an update of the row it made.
                db.Entry(profile).State = EntityState.Detached;
            }
        }
    }

    public async Task<IssuerDecision> ResolveIssuerAsync(Guid bookingId, CancellationToken ct = default)
    {
        var booking = await BookingAsync(bookingId, ct);

        var locked = await db.BookingDocumentIssuers
            .AsNoTracking()
            .Where(i => i.BookingId == bookingId && i.OrgId == booking.OrgId)
            .Select(i => (BookingDocumentIssuerKind?)i.Issuer)
            .FirstOrDefaultAsync(ct);
        if (locked == BookingDocumentIssuerKind.Octorate)
            return new IssuerDecision(IssuerDecisionKind.Octorate, IssuerReasons.IssuerLockedOctorate);
        if (locked == BookingDocumentIssuerKind.CasaZen)
            return new IssuerDecision(IssuerDecisionKind.CasaZen, IssuerReasons.IssuerCasazen);

        var mode = await ModeOfAsync(booking.OrgId, ct);
        return mode switch
        {
            DocumentIssuerMode.Octorate => new IssuerDecision(IssuerDecisionKind.Octorate, IssuerReasons.IssuerOctorate),
            DocumentIssuerMode.CasaZenDirectOnly when !featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance) =>
                new IssuerDecision(IssuerDecisionKind.None, IssuerReasons.IssuanceOff),
            DocumentIssuerMode.CasaZenDirectOnly when !FiscalDocumentRules.IsDirectSource(booking.Source) =>
                new IssuerDecision(IssuerDecisionKind.None, IssuerReasons.BookingNotDirect),
            DocumentIssuerMode.CasaZenDirectOnly => new IssuerDecision(IssuerDecisionKind.CasaZen, IssuerReasons.IssuerCasazen),
            _ => new IssuerDecision(IssuerDecisionKind.None, IssuerReasons.IssuerNobody),
        };
    }

    public async Task<IssuerLock> LockIssuerAsync(
        Guid bookingId,
        BookingDocumentIssuerKind kind,
        Guid? documentId,
        bool save = true,
        CancellationToken ct = default)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an issuer of a booking.");

        for (var attempt = 1; ; attempt++)
        {
            var booking = await BookingAsync(bookingId, ct);

            var existing = await FindLockAsync(bookingId, ct);
            if (existing is not null)
            {
                if (existing.Issuer != kind)
                    throw IssuerConflict(existing.Issuer);

                // The same kind again: nothing changes, but the first document that locked it is remembered when the lock had none.
                if (existing.LockedByDocumentId is null && documentId is not null)
                {
                    existing.LockedByDocumentId = documentId;
                    if (save)
                        await db.SaveChangesAsync(ct);
                }

                return LockOf(existing);
            }

            if (kind == BookingDocumentIssuerKind.CasaZen)
                await RequireCasazenMayIssueAsync(booking, ct);

            var row = new BookingDocumentIssuer
            {
                OrgId = booking.OrgId,
                BookingId = bookingId,
                Issuer = kind,
                LockedAt = UtcNow,
                LockedByDocumentId = documentId,
            };
            db.BookingDocumentIssuers.Add(row);
            if (!save)
                return LockOf(row);

            try
            {
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Booking {BookingId} locked to the issuer {Issuer}", bookingId, kind);
                return LockOf(row);
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && OrgTeamAccess.IsUniqueViolation(ex, BookingDocumentIssuer.BookingIndexName))
            {
                // Another lock of this booking won: read it and answer as for a row that was there (the same kind is a success, the other a conflict).
                db.Entry(row).State = EntityState.Detached;
            }
        }
    }

    public async Task<FiscalDocumentRule> RequireRuleAsync(Guid orgId, CancellationToken ct = default)
    {
        var regime = await db.OrgFiscalIssuerProfiles
            .AsNoTracking()
            .Where(p => p.OrgId == orgId)
            .Select(p => p.DocumentRegime)
            .FirstOrDefaultAsync(ct);

        var rule = regime is { } known ? options.Value.RuleOf(known) : null;
        if (rule is null || !rule.IsConfigured)
        {
            logger.LogInformation("Org {OrgId}: the document rule of its regime is not configured, nothing is issued", orgId);
            throw new DomainRuleException(DocumentIssuerErrorCodes.RuleNotConfigured, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.RuleNotConfigured));
        }

        return rule;
    }

    /// <summary>CasaZen can be locked as the issuer only of a direct booking, while issuance is on and when the organization chose it.</summary>
    private async Task RequireCasazenMayIssueAsync(BookingFacts booking, CancellationToken ct)
    {
        if (!FiscalDocumentRules.IsDirectSource(booking.Source))
            throw new DomainRuleException(DocumentIssuerErrorCodes.BookingNotDirect, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.BookingNotDirect));

        if (!featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance))
            throw new DomainRuleException(DocumentIssuerErrorCodes.IssuanceNotAvailable, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.IssuanceNotAvailable));

        var mode = await ModeOfAsync(booking.OrgId, ct);
        if (mode != DocumentIssuerMode.CasaZenDirectOnly)
        {
            // The organization chose Octorate or the accountant (or nothing yet): another issuer than CasaZen.
            throw new DomainConflictException(DocumentIssuerErrorCodes.IssuerConflict, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.IssuerConflict))
            {
                Extensions = new Dictionary<string, object?> { ["mode"] = mode?.ToString() },
            };
        }
    }

    private async Task<BookingFacts> BookingAsync(Guid bookingId, CancellationToken ct) =>
        await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new BookingFacts(b.OrgId, b.Source))
            .FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException($"Booking {bookingId} was not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };

    private async Task<BookingDocumentIssuer?> FindLockAsync(Guid bookingId, CancellationToken ct)
    {
        var local = db.ChangeTracker.Entries<BookingDocumentIssuer>()
            .FirstOrDefault(e => e.Entity.BookingId == bookingId && e.State != EntityState.Detached);
        return local?.Entity ?? await db.BookingDocumentIssuers.FirstOrDefaultAsync(i => i.BookingId == bookingId, ct);
    }

    private async Task<DocumentIssuerMode?> ModeOfAsync(Guid orgId, CancellationToken ct) =>
        await db.OrgFiscalIssuerProfiles
            .AsNoTracking()
            .Where(p => p.OrgId == orgId)
            .Select(p => (DocumentIssuerMode?)p.IssuerMode)
            .FirstOrDefaultAsync(ct);

    private async Task<bool> IsOctorateConnectedAsync(Guid orgId, CancellationToken ct)
    {
        var environment = OctorateEnvironments.Of(hostEnvironment);
        return await db.OctorateConnections
            .AsNoTracking()
            .AnyAsync(c => c.OrgId == orgId && c.Environment == environment && c.Status == OctorateConnectionStatus.Connected, ct);
    }

    /// <summary>The VAT number and the fiscal code of the organization, the one place they live (<c>Org</c>); a missing org is a 404.</summary>
    private async Task<OrgIdentity> OrgIdentityAsync(Guid orgId, CancellationToken ct) =>
        await db.Orgs
            .AsNoTracking()
            .Where(o => o.Id == orgId)
            .Select(o => new OrgIdentity(o.HasPartitaIva ? o.PartitaIvaNumber : null, o.FiscalCode))
            .FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException($"Org {orgId} was not found");

    private IssuerSettingsView ViewOf(OrgFiscalIssuerProfile? profile, OrgIdentity identity, bool octorateConnected)
    {
        if (profile is null)
        {
            return new IssuerSettingsView(
                null, null, IssuerData.Empty, null, Empty(identity.VatNumber), Empty(identity.FiscalCode), null, octorateConnected,
                FiscalDocumentRules.MissingIssuerFields(IssuerData.Empty, null, null, identity.VatNumber, identity.FiscalCode), null);
        }

        var issuer = new IssuerData(
            profile.LegalName,
            profile.AddressStreet,
            profile.AddressCivic,
            profile.PostalCode,
            profile.City,
            profile.Province,
            profile.CountryCode,
            profile.FatturaPaRegimeCode,
            profile.IssuerSdiRecipientCode,
            profile.IssuerPec,
            profile.DefaultSeries);
        var rule = profile.DocumentRegime is { } regime ? options.Value.RuleOf(regime) : null;
        var effectiveRegimeCode = Empty(profile.FatturaPaRegimeCode) ?? Empty(rule?.FatturaPaRegimeCode);
        return new IssuerSettingsView(
            profile.IssuerMode,
            profile.DocumentRegime,
            issuer,
            effectiveRegimeCode,
            Empty(identity.VatNumber),
            Empty(identity.FiscalCode),
            profile.ConfirmedAt,
            octorateConnected,
            FiscalDocumentRules.MissingIssuerFields(issuer, profile.DocumentRegime, effectiveRegimeCode, identity.VatNumber, identity.FiscalCode),
            rule);
    }

    /// <summary>The data of the command written the way it is stored, and the failures of the values that are not valid (nothing is stored then).</summary>
    private static IssuerData Normalize(SaveIssuerSettingsCommand command, List<InvoiceDataFailure> failures)
    {
        var legalName = Text(command.LegalName, OrgFiscalIssuerProfile.LegalNameMaxLength, IssuerFields.LegalName, failures);
        var street = Text(command.AddressStreet, OrgFiscalIssuerProfile.AddressStreetMaxLength, IssuerFields.AddressStreet, failures);
        var civic = Text(command.AddressCivic, OrgFiscalIssuerProfile.AddressCivicMaxLength, IssuerFields.AddressCivic, failures);
        var city = Text(command.City, OrgFiscalIssuerProfile.CityMaxLength, IssuerFields.City, failures);

        var country = (Empty(command.CountryCode) ?? OrgFiscalIssuerProfile.DefaultCountryCode).ToUpperInvariant();
        if (country.Length != OrgFiscalIssuerProfile.CountryCodeMaxLength || !country.All(char.IsAsciiLetter))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.CountryCode));

        var province = Empty(command.Province)?.ToUpperInvariant();
        if (province is not null && (province.Length != OrgFiscalIssuerProfile.ProvinceMaxLength || !province.All(char.IsAsciiLetter)))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.Province));

        // An Italian postal code is five digits without spaces; another country's keeps its spaces.
        var postalCode = country == "IT"
            ? Empty(command.PostalCode)?.Replace(" ", string.Empty, StringComparison.Ordinal)
            : Collapse(command.PostalCode)?.ToUpperInvariant();
        if (postalCode is not null
            && (postalCode.Length > OrgFiscalIssuerProfile.PostalCodeMaxLength
                || (country == "IT" && (postalCode.Length != 5 || !postalCode.All(char.IsAsciiDigit)))))
        {
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.PostalCode));
        }

        var regimeCode = Empty(command.FatturaPaRegimeCode)?.ToUpperInvariant();
        if (regimeCode is not null && !RegimeCodePattern.IsMatch(regimeCode))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.FatturaPaRegimeCode));

        var sdiCode = Empty(command.IssuerSdiRecipientCode)?.ToUpperInvariant();
        if (sdiCode is not null && !InvoiceDataFormats.IsValidRecipientCode(sdiCode))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.IssuerSdiRecipientCode));

        var pec = Empty(command.IssuerPec)?.ToLowerInvariant();
        if (pec is not null && !InvoiceDataFormats.IsValidPec(pec))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.IssuerPec));

        var series = Empty(command.DefaultSeries);
        if (series is not null && !SeriesPattern.IsMatch(series))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.DefaultSeries));

        return new IssuerData(legalName, street, civic, postalCode, city, province, country, regimeCode, sdiCode, pec, series);
    }

    /// <summary>A text trimmed and collapsed, <c>null</c> when blank; longer than <paramref name="maxLength"/> is a failure.</summary>
    private static string? Text(string? value, int maxLength, string field, List<InvoiceDataFailure> failures)
    {
        var text = Collapse(value);
        if (text is not null && text.Length > maxLength)
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, field));

        return text;
    }

    /// <summary>Every run of white space becomes one space and the ends are trimmed; <c>null</c> when nothing is left.</summary>
    private static string? Collapse(string? value) =>
        Empty(value) is { } trimmed ? string.Join(' ', trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : null;

    private static DomainRuleException FieldsInvalid(IReadOnlyList<InvoiceDataFailure> failures) =>
        new(failures[0].Code, DocumentIssuerErrorCodes.MessageKeyOf(failures[0].Code))
        {
            Extensions = new Dictionary<string, object?>
            {
                ["field"] = failures[0].Field,
                ["errors"] = failures.Select(f => new { field = f.Field, code = f.Code }).ToList(),
            },
        };

    private static DomainConflictException IssuerConflict(BookingDocumentIssuerKind locked) =>
        new(DocumentIssuerErrorCodes.IssuerConflict, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.IssuerConflict))
        {
            Extensions = new Dictionary<string, object?> { ["issuer"] = locked.ToString() },
        };

    private static IssuerLock LockOf(BookingDocumentIssuer row) => new(row.Issuer, row.LockedAt, row.LockedByDocumentId);

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record BookingFacts(Guid OrgId, BookingSource Source);

    private sealed record OrgIdentity(string? VatNumber, string? FiscalCode);
}
