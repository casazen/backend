using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Features;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Casazen.Infrastructure.Services;

/// <inheritdoc cref="IOwnerIssuerProfileService"/>
/// <remarks>
/// <list type="bullet">
/// <item><b>One validation.</b> The data an owner shares with an organization (the legal name, the address, the regime and its code, the series, the SDI code, the
/// PEC, the UID, the stamp reference) is checked and normalized by the issuer setting itself (<see cref="DocumentIssuerService.Normalize"/>): one rule for both.
/// The owner's own fiscal code (<see cref="ItalianFiscalCode"/>) and VAT number (<see cref="InvoiceDataFormats.IsValidVatNumber"/>, Italian) are checked
/// here.</item>
/// <item><b>One row for each owner.</b> The partial unique index on the owner refuses a second profile; two saves at the same moment make one (the second is an
/// update of the row the first made).</item>
/// <item><b>Logs</b> carry ids, the regime and counts: never a value of the profile.</item>
/// </list>
/// </remarks>
public sealed class OwnerIssuerProfileService(
    AppDbContext db,
    IFeatureFlags featureFlags,
    IFiscalTaxRuleResolver taxRules,
    TimeProvider timeProvider,
    ILogger<OwnerIssuerProfileService> logger) : IOwnerIssuerProfileService
{
    /// <summary>Attempts of a save when another writer made the same row at the same moment.</summary>
    private const int MaxAttempts = 3;

    public async Task<OwnerIssuerProfileView> GetAsync(HostScope scope, Guid ownerId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        await RequireOwnerInReachAsync(scope, ownerId, ct);

        var profile = await db.OrgFiscalIssuerProfiles.AsNoTracking()
            .FirstOrDefaultAsync(p => p.OrgId == scope.OrgId && p.PropertyOwnerId == ownerId, ct);
        return ViewOf(ownerId, profile);
    }

    public async Task<OwnerIssuerProfileView> SaveAsync(
        Guid orgId,
        Guid ownerId,
        SaveOwnerIssuerProfileCommand command,
        string? userId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        var failures = new List<InvoiceDataFailure>();
        if (command.Regime is { } regime && !Enum.IsDefined(regime))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.Regime));
        if (command.StayVatTreatment is { } stay && !Enum.IsDefined(stay))
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.StayVatTreatment));

        // What an owner shares with an organization is checked by the issuer setting itself; the rest of the setting (bank accounts, automation, the owner
        // invoices, the reminders of the wizard) is not an owner's and stays empty.
        var data = DocumentIssuerService.Normalize(
            new SaveIssuerSettingsCommand
            {
                Regime = command.Regime is { } chosen && Enum.IsDefined(chosen) ? chosen : null,
                LegalName = command.LegalName,
                AddressStreet = command.AddressStreet,
                AddressCivic = command.AddressCivic,
                PostalCode = command.PostalCode,
                City = command.City,
                Province = command.Province,
                CountryCode = command.CountryCode,
                FatturaPaRegimeCode = command.FatturaPaRegimeCode,
                IssuerSdiRecipientCode = command.IssuerSdiRecipientCode,
                IssuerPec = command.IssuerPec,
                SwissVatNumber = command.SwissVatNumber,
                StayVatTreatment = command.StayVatTreatment is { } treatment && Enum.IsDefined(treatment) ? treatment : null,
                GuestDocumentSeries = command.GuestDocumentSeries,
                StampDutyReference = command.StampDutyReference,
            },
            failures);

        var swiss = FiscalDocumentRules.IsSwiss(data.CountryCode);
        var fiscalCode = swiss ? null : FiscalCodeOf(command.FiscalCode, failures);
        var vatNumber = swiss || command.Regime == DocumentRegime.PrivatePerson ? null : VatNumberOf(command.VatNumber, failures);
        if (failures.Count > 0)
            throw DocumentIssuerService.FieldsInvalid(failures);

        if (command.Regime is { } owned && !FiscalDocumentRules.IsRegimeOfCountry(owned, data.CountryCode))
        {
            throw new DomainRuleException(
                DocumentIssuerErrorCodes.RegimeCountryMismatch, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.RegimeCountryMismatch))
            {
                Extensions = new Dictionary<string, object?> { ["field"] = IssuerFields.Regime },
            };
        }

        if (!featureFlags.IsEnabled(FeatureFlags.FiscalDocumentIssuance))
            throw new DomainRuleException(DocumentIssuerErrorCodes.IssuanceNotAvailable, DocumentIssuerErrorCodes.MessageKeyOf(DocumentIssuerErrorCodes.IssuanceNotAvailable));

        await RequireOwnerOfOrgAsync(orgId, ownerId, ct);

        for (var attempt = 1; ; attempt++)
        {
            var profile = await db.OrgFiscalIssuerProfiles.FirstOrDefaultAsync(p => p.OrgId == orgId && p.PropertyOwnerId == ownerId, ct);
            var created = profile is null;
            if (profile is null)
            {
                profile = new OrgFiscalIssuerProfile { OrgId = orgId, PropertyOwnerId = ownerId };
                db.OrgFiscalIssuerProfiles.Add(profile);
            }

            var now = timeProvider.GetUtcNow().UtcDateTime;
            profile.IssuerMode = DocumentIssuerMode.CasaZen;
            profile.DocumentRegime = command.Regime;
            DocumentIssuerService.Apply(profile, data);
            profile.FiscalCode = fiscalCode;
            profile.VatNumber = vatNumber;
            profile.StayIssuedByOwner = swiss && command.StayIssuedByOwner;
            profile.UpdatedAt = now;
            profile.UpdatedByUserId = userId is { Length: > 0 }
                ? userId[..Math.Min(userId.Length, OrgFiscalIssuerProfile.UpdatedByUserIdMaxLength)]
                : null;

            // Complete or not, the profile is saved (a draft says what is missing); it is ready from the moment nothing is missing.
            var missing = MissingFieldsOf(profile);
            profile.ConfirmedAt = missing.Count == 0 ? profile.ConfirmedAt ?? now : null;

            try
            {
                await db.SaveChangesAsync(ct);
                logger.LogInformation(
                    "Issuer profile of owner {OwnerId} in org {OrgId} saved by {UserId}: regime {Regime}, {MissingCount} fields missing",
                    ownerId, orgId, userId, command.Regime, missing.Count);
                return ViewOf(ownerId, profile);
            }
            catch (DbUpdateException ex) when (created && attempt < MaxAttempts && OrgTeamAccess.IsUniqueViolation(ex, OrgFiscalIssuerProfile.OwnerIndexName))
            {
                // Another save made the profile of this owner first: the same, as an update of the row it made.
                db.Entry(profile).State = EntityState.Detached;
            }
        }
    }

    /// <summary>The view of the profile of an owner; the empty one (no id, everything missing) while it has none.</summary>
    private OwnerIssuerProfileView ViewOf(Guid ownerId, OrgFiscalIssuerProfile? profile)
    {
        if (profile is null)
        {
            return new OwnerIssuerProfileView(
                ownerId, null, null, IssuerData.Empty, null, null, false, null,
                FiscalDocumentRules.MissingIssuerFields(IssuerData.Empty, null, null, null, null), null, null);
        }

        var rule = RuleInForceToday(profile);
        var swiss = FiscalDocumentRules.IsSwiss(profile.CountryCode);
        var effectiveRegimeCode = swiss ? null : Empty(profile.FatturaPaRegimeCode) ?? Empty(rule?.FatturaPaRegimeCode);
        return new OwnerIssuerProfileView(
            ownerId,
            profile.Id,
            profile.DocumentRegime,
            OwnerIssuerProfileRules.DataOf(profile),
            profile.FiscalCode,
            profile.VatNumber,
            profile.StayIssuedByOwner,
            effectiveRegimeCode,
            OwnerIssuerProfileRules.MissingFields(profile, rule?.FatturaPaRegimeCode),
            rule,
            profile.UpdatedAt);
    }

    private IReadOnlyList<string> MissingFieldsOf(OrgFiscalIssuerProfile profile) =>
        OwnerIssuerProfileRules.MissingFields(profile, RuleInForceToday(profile)?.FatturaPaRegimeCode);

    /// <summary>The tax rule in force today (a Europe/Rome calendar day) for the country, the regime and the stay treatment of the owner; the cleaning follows the stay.</summary>
    private FiscalTaxRule? RuleInForceToday(OrgFiscalIssuerProfile profile) =>
        taxRules.TryResolve(profile.CountryCode, profile.DocumentRegime, profile.StayVatTreatment, CleaningFeeTreatment.FollowsStay, timeProvider.TodayInRomeAsDateOnly());

    /// <summary>
    /// The owner is an owner of the org of the scope and in its reach: the whole org, or a partial scope that reaches every property the owner has (or had) an
    /// ownership of; an owner without any ownership is in no partial reach. 404 <c>owner_not_found</c> otherwise, the same answer as an owner that does not exist.
    /// </summary>
    private async Task RequireOwnerInReachAsync(HostScope scope, Guid ownerId, CancellationToken ct)
    {
        var exists = await db.PropertyOwners.AsNoTracking().AnyAsync(o => o.Id == ownerId && o.OrgId == scope.OrgId, ct);
        if (!exists)
            throw OwnerNotFound(ownerId);
        if (scope.IsOrgWide)
            return;

        var reachable = db.Properties.Where(p => p.OrgId == scope.OrgId).InScope(scope).Select(p => p.Id);
        var periods = await db.PropertyOwnerships.AsNoTracking()
            .Where(po => po.OrgId == scope.OrgId && po.PropertyOwnerId == ownerId)
            .Select(po => reachable.Contains(po.PropertyId))
            .ToListAsync(ct);
        if (periods.Count == 0 || !periods.All(inside => inside))
            throw OwnerNotFound(ownerId);
    }

    /// <summary>The owner exists in the org and is not anonymized (an anonymized owner issues nothing any more): 404 <c>owner_not_found</c> otherwise.</summary>
    private async Task RequireOwnerOfOrgAsync(Guid orgId, Guid ownerId, CancellationToken ct)
    {
        if (!await db.PropertyOwners.AsNoTracking().AnyAsync(o => o.Id == ownerId && o.OrgId == orgId && o.AnonymizedAt == null, ct))
            throw OwnerNotFound(ownerId);
    }

    /// <summary>The Italian fiscal code of the owner, normalized; a code that is not one (of a person or of a company) is a failure on <c>fiscalCode</c>.</summary>
    private static string? FiscalCodeOf(string? value, List<InvoiceDataFailure> failures)
    {
        if (Empty(value) is null)
            return null;

        var code = ItalianFiscalCode.Normalize(value);
        if (code.Length > OrgFiscalIssuerProfile.FiscalCodeMaxLength || !ItalianFiscalCode.IsValid(code))
        {
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.FiscalCode));
            return null;
        }

        return code;
    }

    /// <summary>The Italian VAT number of the owner as its 11 digits (the <c>IT</c> prefix taken off); anything else is a failure on <c>vatNumber</c>.</summary>
    private static string? VatNumberOf(string? value, List<InvoiceDataFailure> failures)
    {
        if (Empty(value) is not { } typed)
            return null;

        var compact = new string(typed.Where(c => !char.IsWhiteSpace(c) && c is not '.' and not '-' and not '/').ToArray()).ToUpperInvariant();
        var digits = compact.StartsWith(FiscalDocumentRules.Italy, StringComparison.Ordinal) ? compact[2..] : compact;
        if (digits.Length != 11 || !digits.All(char.IsAsciiDigit) || !InvoiceDataFormats.IsValidVatNumber(digits))
        {
            failures.Add(new InvoiceDataFailure(DocumentIssuerErrorCodes.FieldInvalid, IssuerFields.VatNumber));
            return null;
        }

        return digits;
    }

    private static NotFoundException OwnerNotFound(Guid ownerId) =>
        new($"Owner {ownerId} was not found") { Code = OwnerFiscalErrors.OwnerNotFound, MessageKey = "OwnerNotFound" };

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
