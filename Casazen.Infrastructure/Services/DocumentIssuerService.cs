using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Octorate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// <see cref="IDocumentIssuerService"/> (FIS-02, NF-82, NF-112, NF-113, E11/F01/US01/T01): CasaZen issues the documents of every organization and of
/// every active booking; this keeps the profile it issues with.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>The only mode is CasaZen.</b> Octorate and accountant are refused (422 <c>issuer_mode_retired</c>) and nothing is saved.</item>
/// <item><b>Country and regime.</b> The country of the profile (IT or CH) decides the valid regimes (422 <c>issuer_regime_country_mismatch</c>); a Swiss
/// issuer gives its UID (format and check digit), an Italian one keeps the Italian checks. The IBAN is checked (mod 97), the QR-IBAN too and must be one.</item>
/// <item><b>Draft or confirmed.</b> A profile that cannot issue yet is saved as a draft (its <c>missingFields</c> say what is left); the wizard is
/// confirmed only with nothing missing (422 <c>issuer_profile_incomplete</c>). Every save needs <c>Features:FiscalDocumentIssuance</c> on.</item>
/// <item><b>One row for each document.</b> The rows of <see cref="BookingDocumentIssuer"/> are one for each document CasaZen issues for the booking (E17/F01/US01/T02:
/// the owner's and the agency's), written with CasaZen only; a booking a legacy row locked to Octorate keeps it (it was issued there) and is never issued again
/// by CasaZen.</item>
/// <item><b>No second source of truth.</b> The Italian VAT number and the fiscal code of the issuer are read from <c>Org</c>; the regime code is the
/// profile's own or the rule's.</item>
/// <item><b>Logs</b> carry ids, modes and field names: never a value of the profile.</item>
/// </list>
/// </remarks>
public sealed class DocumentIssuerService(
    AppDbContext db,
    IFeatureFlags featureFlags,
    IFiscalTaxRuleResolver taxRules,
    IHostEnvironment hostEnvironment,
    TimeProvider timeProvider,
    ILogger<DocumentIssuerService> logger) : IDocumentIssuerService
{
    private const int MaxAttempts = 3;

    private static readonly Regex RegimeCodePattern = new("^[A-Z0-9]{1,4}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private static readonly Regex SeriesPattern = new("^(?:[A-Za-z0-9._/-]|\\{year\\})+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private DateTime UtcNow => timeProvider.GetUtcNow().UtcDateTime;

    public async Task<IssuerSettingsView> GetSettingsAsync(Guid orgId, CancellationToken ct = default)
    {
        var profile = await db.OrgFiscalIssuerProfiles.AsNoTracking().OfOrganization(orgId).FirstOrDefaultAsync(ct);
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

        if (FiscalDocumentRules.IsRetiredMode(command.Mode))
        {
            logger.LogInformation("Issuer setting of org {OrgId} refused: the mode {Mode} is retired", orgId, command.Mode);
            throw new DomainRuleException(DocumentIssuerErrorCodes.ModeRetired, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.ModeRetired))
            {
                Extensions = new Dictionary<string, object?> { ["field"] = IssuerFields.Mode },
            };
        }

        var failures = new List<InvoiceDataFailure>();
        if (!Enum.IsDefined(command.Mode))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.Mode));
        if (command.Regime is { } regime && !Enum.IsDefined(regime))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.Regime));
        if (command.StayVatTreatment is { } stay && !Enum.IsDefined(stay))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.StayVatTreatment));
        if (command.CleaningFeeTreatment is { } cleaning && !Enum.IsDefined(cleaning))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.CleaningFeeTreatment));

        var data = Normalize(command, failures);
        if (failures.Count > 0)
            throw FieldsInvalid(failures);

        if (command.Regime is { } chosenRegime && !FiscalDocumentRules.IsRegimeOfCountry(chosenRegime, data.CountryCode))
        {
            throw new DomainRuleException(
                DocumentIssuerErrorCodes.RegimeCountryMismatch, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.RegimeCountryMismatch))
            {
                Extensions = new Dictionary<string, object?> { ["field"] = IssuerFields.Regime },
            };
        }

        if (!featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance))
            throw new DomainRuleException(DocumentIssuerErrorCodes.IssuanceNotAvailable, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.IssuanceNotAvailable));

        var identity = await OrgIdentityAsync(orgId, ct);
        var rule = RuleInForceToday(data.CountryCode, command.Regime, data.StayVatTreatment, data.CleaningFeeTreatment);
        var effectiveRegimeCode = data.FatturaPaRegimeCode ?? Empty(rule?.FatturaPaRegimeCode);
        var missing = FiscalDocumentRules.MissingIssuerFields(data, command.Regime, effectiveRegimeCode, identity.VatNumber, identity.FiscalCode);

        if (command.Confirmed && missing.Count > 0)
        {
            throw new DomainRuleException(DocumentIssuerErrorCodes.ProfileIncomplete, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.ProfileIncomplete))
            {
                Extensions = new Dictionary<string, object?> { ["fields"] = missing },
            };
        }

        for (var attempt = 1; ; attempt++)
        {
            var profile = await db.OrgFiscalIssuerProfiles.OfOrganization(orgId).FirstOrDefaultAsync(ct);
            var created = profile is null;
            var now = UtcNow;
            if (profile is null)
            {
                profile = new OrgFiscalIssuerProfile { OrgId = orgId };
                db.OrgFiscalIssuerProfiles.Add(profile);
            }

            profile.IssuerMode = DocumentIssuerMode.CasaZen;
            profile.DocumentRegime = command.Regime;
            Apply(profile, data);
            if (missing.Count > 0)
                profile.ConfirmedAt = null;
            else if (command.Confirmed)
                profile.ConfirmedAt = now;

            profile.UpdatedAt = now;
            profile.UpdatedByUserId = updatedByUserId is { Length: > 0 }
                ? updatedByUserId[..Math.Min(updatedByUserId.Length, OrgFiscalIssuerProfile.UpdatedByUserIdMaxLength)]
                : null;

            try
            {
                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "Issuer setting of org {OrgId} saved: regime {Regime}, {MissingCount} fields missing", orgId, command.Regime, missing.Count);
                return ViewOf(profile, identity, await IsOctorateConnectedAsync(orgId, ct));
            }
            catch (DbUpdateException ex) when (created && attempt < MaxAttempts && OrgTeamAccess.IsUniqueViolation(ex, OrgFiscalIssuerProfile.OrgIndexName))
            {
                db.Entry(profile).State = EntityState.Detached;
            }
        }
    }

    public async Task<IssuerDecision> ResolveIssuerAsync(Guid bookingId, CancellationToken ct = default)
    {
        var booking = await BookingAsync(bookingId, ct);

        // A booking a legacy row locked to Octorate was issued there: CasaZen never issues it again.
        var lockedToOctorate = await db.BookingDocumentIssuers
            .AsNoTracking()
            .AnyAsync(i => i.BookingId == bookingId && i.OrgId == booking.OrgId && i.Issuer == BookingDocumentIssuerKind.Octorate, ct);
        if (lockedToOctorate)
            return new IssuerDecision(IssuerDecisionKind.Octorate, IssuerReasons.IssuerLockedOctorate);

        return featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance)
            ? new IssuerDecision(IssuerDecisionKind.CasaZen, IssuerReasons.IssuerCasazen)
            : new IssuerDecision(IssuerDecisionKind.None, IssuerReasons.IssuanceOff);
    }

    public async Task<IssuerLock> LockIssuerAsync(
        Guid bookingId,
        BookingDocumentIssuerKind kind,
        Guid? documentId,
        bool save = true,
        CancellationToken ct = default)
    {
        // The rows are written with CasaZen only (2026-11-14): an imported document of Octorate never locks a booking.
        if (kind != BookingDocumentIssuerKind.CasaZen)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only CasaZen is locked as the issuer of a booking.");

        for (var attempt = 1; ; attempt++)
        {
            var booking = await BookingAsync(bookingId, ct);
            var rows = await RowsOfAsync(bookingId, ct);

            // A legacy row locked to Octorate keeps CasaZen off the booking.
            if (rows.FirstOrDefault(row => row.Issuer != kind) is { } other)
                throw IssuerConflict(other.Issuer);

            var existing = documentId is { } id
                ? rows.FirstOrDefault(row => row.LockedByDocumentId == id) ?? rows.FirstOrDefault(row => row.LockedByDocumentId is null)
                : rows.FirstOrDefault();
            if (existing is not null)
            {
                if (existing.LockedByDocumentId is null && documentId is not null)
                {
                    existing.LockedByDocumentId = documentId;
                    if (save)
                        await db.SaveChangesAsync(ct);
                }

                return LockOf(existing);
            }

            if (!featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance))
                throw new DomainRuleException(DocumentIssuerErrorCodes.IssuanceNotAvailable, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.IssuanceNotAvailable));

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
                logger.LogInformation("Booking {BookingId} locked to the issuer {Issuer} for document {DocumentId}", bookingId, kind, documentId);
                return LockOf(row);
            }
            catch (DbUpdateException ex) when (attempt < MaxAttempts && OrgTeamAccess.IsUniqueViolation(ex, BookingDocumentIssuer.DocumentIndexName))
            {
                db.Entry(row).State = EntityState.Detached;
            }
        }
    }

    private FiscalTaxRule? RuleInForceToday(string? countryCode, DocumentRegime? regime, StayVatTreatment stay, CleaningFeeTreatment cleaning) =>
        taxRules.TryResolve(countryCode, regime, stay, cleaning, timeProvider.TodayInRomeAsDateOnly());

    private async Task<BookingFacts> BookingAsync(Guid bookingId, CancellationToken ct) =>
        await db.Bookings
            .AsNoTracking()
            .Where(b => b.Id == bookingId)
            .Select(b => new BookingFacts(b.OrgId))
            .FirstOrDefaultAsync(ct)
        ?? throw new NotFoundException($"Booking {bookingId} was not found") { Code = "booking_not_found", MessageKey = "BookingNotFound" };

    private async Task<IReadOnlyList<BookingDocumentIssuer>> RowsOfAsync(Guid bookingId, CancellationToken ct)
    {
        var staged = db.ChangeTracker.Entries<BookingDocumentIssuer>()
            .Where(e => e.Entity.BookingId == bookingId && e.State != EntityState.Detached && e.State != EntityState.Deleted)
            .Select(e => e.Entity)
            .ToList();
        var stored = await db.BookingDocumentIssuers.Where(i => i.BookingId == bookingId).ToListAsync(ct);
        return staged.Concat(stored.Where(row => staged.All(s => s.Id != row.Id))).ToList();
    }

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

        var issuer = OwnerIssuerProfileRules.DataOf(profile);
        var rule = RuleInForceToday(profile.CountryCode, profile.DocumentRegime, profile.StayVatTreatment, profile.CleaningFeeTreatment);
        var effectiveRegimeCode = FiscalDocumentRules.IsSwiss(profile.CountryCode)
            ? null
            : Empty(profile.FatturaPaRegimeCode) ?? Empty(rule?.FatturaPaRegimeCode);
        return new IssuerSettingsView(
            DocumentIssuerMode.CasaZen,
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

    internal static void Apply(OrgFiscalIssuerProfile profile, IssuerData data)
    {
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
        profile.VatRegistered = data.VatRegistered;
        profile.SwissVatNumber = data.SwissVatNumber;
        profile.Iban = data.Iban;
        profile.QrIban = data.QrIban;
        profile.StayVatTreatment = data.StayVatTreatment;
        profile.CleaningFeeTreatment = data.CleaningFeeTreatment;
        profile.GuestDocumentSeries = data.GuestDocumentSeries;
        profile.OwnerInvoiceSeries = data.OwnerInvoiceSeries;
        profile.AutoIssueGuestDocuments = data.AutoIssueGuestDocuments;
        profile.EmailGuestDocuments = data.EmailGuestDocuments;

        if (data.PlatformCollectedTouristTaxChannelsConfirmed)
            profile.ConfirmPlatformCollectedTouristTaxChannels(data.PlatformCollectedTouristTaxChannels);

        profile.StampDutyReference = data.StampDutyReference;
        profile.ConservationAgreementConfirmed = data.ConservationAgreementConfirmed;
        profile.VirtualStampConfirmed = data.VirtualStampConfirmed;
    }

    internal static IssuerData Normalize(SaveIssuerSettingsCommand command, List<InvoiceDataFailure> failures)
    {
        var legalName = Text(command.LegalName, OrgFiscalIssuerProfile.LegalNameMaxLength, IssuerFields.LegalName, failures);
        var street = Text(command.AddressStreet, OrgFiscalIssuerProfile.AddressStreetMaxLength, IssuerFields.AddressStreet, failures);
        var civic = Text(command.AddressCivic, OrgFiscalIssuerProfile.AddressCivicMaxLength, IssuerFields.AddressCivic, failures);
        var city = Text(command.City, OrgFiscalIssuerProfile.CityMaxLength, IssuerFields.City, failures);

        var country = (Empty(command.CountryCode) ?? OrgFiscalIssuerProfile.DefaultCountryCode).ToUpperInvariant();
        if (country.Length != OrgFiscalIssuerProfile.CountryCodeMaxLength || !country.All(char.IsAsciiLetter))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.CountryCode));

        var swiss = FiscalDocumentRules.IsSwiss(country);

        var province = Empty(command.Province)?.ToUpperInvariant();
        if (province is not null && (province.Length != OrgFiscalIssuerProfile.ProvinceMaxLength || !province.All(char.IsAsciiLetter)))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.Province));

        var postalCode = country is FiscalDocumentRules.Italy or FiscalDocumentRules.Switzerland
            ? Empty(command.PostalCode)?.Replace(" ", string.Empty, StringComparison.Ordinal)
            : Collapse(command.PostalCode)?.ToUpperInvariant();
        var postalDigits = country switch
        {
            FiscalDocumentRules.Italy => 5,
            FiscalDocumentRules.Switzerland => 4,
            _ => 0,
        };
        if (postalCode is not null
            && (postalCode.Length > OrgFiscalIssuerProfile.PostalCodeMaxLength
                || (postalDigits > 0 && (postalCode.Length != postalDigits || !postalCode.All(char.IsAsciiDigit)))))
        {
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.PostalCode));
        }

        var regimeCode = swiss ? null : Empty(command.FatturaPaRegimeCode)?.ToUpperInvariant();
        if (regimeCode is not null && !RegimeCodePattern.IsMatch(regimeCode))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.FatturaPaRegimeCode));

        var sdiCode = swiss ? null : Empty(command.IssuerSdiRecipientCode)?.ToUpperInvariant();
        if (sdiCode is not null && !InvoiceDataFormats.IsValidRecipientCode(sdiCode))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.IssuerSdiRecipientCode));

        var pec = swiss ? null : Empty(command.IssuerPec)?.ToLowerInvariant();
        if (pec is not null && !InvoiceDataFormats.IsValidPec(pec))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.IssuerPec));

        string? swissVatNumber = null;
        if (swiss && Empty(command.SwissVatNumber) is { } uid)
        {
            swissVatNumber = SwissUid.Normalize(uid);
            if (swissVatNumber is null)
                failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.SwissVatNumber));
        }

        // The VAT registration follows the regime; without a regime it is what the issuer answered.
        var vatRegistered = command.VatRegistered ?? false;
        if (command.Regime is { } regime && Enum.IsDefined(regime))
        {
            var expected = FiscalDocumentRules.IsVatRegisteredRegime(regime);
            if (command.VatRegistered is { } said && said != expected)
                failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.VatRegistered));
            vatRegistered = expected;
        }

        string? iban = null;
        if (Empty(command.Iban) is { } typedIban)
        {
            iban = typedIban.Length <= OrgFiscalIssuerProfile.IbanMaxLength * 2 ? IssuerBankAccounts.NormalizeIban(typedIban) : null;
            if (iban is null || IssuerBankAccounts.IsQrIban(iban))
                failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.Iban));
        }

        string? qrIban = null;
        if (Empty(command.QrIban) is { } typedQrIban)
        {
            qrIban = typedQrIban.Length <= OrgFiscalIssuerProfile.IbanMaxLength * 2 ? IssuerBankAccounts.NormalizeIban(typedQrIban) : null;
            if (qrIban is null || !IssuerBankAccounts.IsQrIban(qrIban))
                failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.QrIban));
        }

        var guestSeries = Series(command.GuestDocumentSeries, IssuerFields.GuestDocumentSeries, failures);
        var ownerSeries = Series(command.OwnerInvoiceSeries, IssuerFields.OwnerInvoiceSeries, failures);

        var channels = new List<BookingSource>();
        foreach (var name in command.PlatformCollectedTouristTaxChannels ?? [])
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed)
                || trimmed.All(char.IsAsciiDigit)
                || !Enum.TryParse<BookingSource>(trimmed, ignoreCase: true, out var source)
                || !Enum.IsDefined(source))
            {
                failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.PlatformCollectedTouristTaxChannels));
                break;
            }

            if (!channels.Contains(source))
                channels.Add(source);
        }

        var stampDutyReference = Text(command.StampDutyReference, OrgFiscalIssuerProfile.StampDutyReferenceMaxLength, IssuerFields.StampDutyReference, failures);

        return new IssuerData(legalName, street, civic, postalCode, city, province, country, regimeCode, sdiCode, pec, guestSeries)
        {
            VatRegistered = vatRegistered,
            SwissVatNumber = swissVatNumber,
            Iban = iban,
            QrIban = qrIban,
            StayVatTreatment = command.StayVatTreatment ?? StayVatTreatment.Standard,
            CleaningFeeTreatment = command.CleaningFeeTreatment ?? CleaningFeeTreatment.FollowsStay,
            OwnerInvoiceSeries = ownerSeries,
            AutoIssueGuestDocuments = command.AutoIssueGuestDocuments,
            EmailGuestDocuments = command.EmailGuestDocuments,
            PlatformCollectedTouristTaxChannels = channels,
            PlatformCollectedTouristTaxChannelsConfirmed = command.PlatformCollectedTouristTaxChannels is not null,
            StampDutyReference = stampDutyReference,
            ConservationAgreementConfirmed = command.ConservationAgreementConfirmed,
            VirtualStampConfirmed = command.VirtualStampConfirmed,
        };
    }

    private static string? Series(string? value, string field, List<InvoiceDataFailure> failures)
    {
        var series = Empty(value);
        if (series is not null && (series.Length > OrgFiscalIssuerProfile.SeriesMaxLength || !SeriesPattern.IsMatch(series)))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, field));

        return series;
    }

    private static string? Text(string? value, int maxLength, string field, List<InvoiceDataFailure> failures)
    {
        var text = Collapse(value);
        if (text is not null && text.Length > maxLength)
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, field));

        return text;
    }

    private static string? Collapse(string? value) =>
        Empty(value) is { } trimmed ? string.Join(' ', trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) : null;

    internal static DomainRuleException FieldsInvalid(IReadOnlyList<InvoiceDataFailure> failures) =>
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

    private sealed record BookingFacts(Guid OrgId);

    private sealed record OrgIdentity(string? VatNumber, string? FiscalCode);
}
