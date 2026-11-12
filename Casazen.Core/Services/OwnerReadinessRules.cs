using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>One ownership period of an owner on a property, in Rome days (<see cref="OwnerReadinessRules.Mandate"/>).</summary>
public sealed record OwnerOwnershipFact(Guid OwnershipId, Guid PropertyId, DateOnly Start, DateOnly? End, decimal SharePercent);

/// <summary>A mandate of the agency on one ownership, in Rome days; <paramref name="HasDocument"/> is true when the signed mandate is on file.</summary>
public sealed record OwnerMandateFact(Guid OwnershipId, DateOnly From, DateOnly? Until, bool HasDocument);

/// <summary>
/// What is missing on an owner of the registry, one rule for each dimension of the readiness (E10/F02/US02/T01, NF-109, AC-NF109-3): pure,
/// reading only what it is given, answering with the codes and the names of the fields and never a value. The owner accounting is ready when the
/// registry, the ownership, the mandate, the fiscal profile and the payment data are; the portal when the registry, the ownership and an e-mail
/// address are. None of it reads or changes the readiness of the OTA channels.
/// </summary>
public static class OwnerReadinessRules
{
    /// <summary>The legal name and the kind of the owner.</summary>
    public static IReadOnlyList<OwnerReadinessReason> Registry(PropertyOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var reasons = new List<OwnerReadinessReason>();
        if (string.IsNullOrWhiteSpace(OwnerFieldRules.DisplayName(owner)))
            reasons.Add(Reason(OwnerReadinessDimensions.Registry, OwnerReadinessReasons.LegalNameMissing, OwnerImportColumns.LegalName));

        if (owner.EntityKind is null)
            reasons.Add(Reason(OwnerReadinessDimensions.Registry, OwnerReadinessReasons.EntityKindMissing, OwnerImportColumns.EntityKind));

        return reasons;
    }

    /// <summary>
    /// The tax residence, the fiscal identifier the kind of the owner needs (the fiscal code of a person; the fiscal code or the VAT number of a
    /// legal entity) and the structured billing address (street, postal code, city and country, and the province in Italy).
    /// </summary>
    public static IReadOnlyList<OwnerReadinessReason> FiscalProfile(PropertyOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var reasons = new List<OwnerReadinessReason>();
        var residence = owner.TaxResidenceCountry;
        if (string.IsNullOrWhiteSpace(residence))
            reasons.Add(Reason(OwnerReadinessDimensions.FiscalProfile, OwnerReadinessReasons.TaxResidenceMissing, OwnerImportColumns.TaxResidence));

        var fiscalCode = OwnerFieldRules.NormalizeFiscalCode(owner.FiscalCode);
        var vat = OwnerFieldRules.NormalizeVat(owner.VatNumber);
        var isEntity = owner.EntityKind == OwnerEntityKind.LegalEntity;
        if (fiscalCode.Length == 0 && (!isEntity || vat.Length == 0))
        {
            reasons.Add(Reason(
                OwnerReadinessDimensions.FiscalProfile,
                OwnerReadinessReasons.FiscalIdMissing,
                isEntity ? OwnerImportColumns.VatNumber : OwnerImportColumns.FiscalCode));
        }

        if (fiscalCode.Length > 0 && !OwnerFieldRules.IsValidFiscalId(fiscalCode, residence))
            reasons.Add(Reason(OwnerReadinessDimensions.FiscalProfile, OwnerReadinessReasons.FiscalCodeInvalid, OwnerImportColumns.FiscalCode));

        if (vat.Length > 0 && !OwnerFieldRules.IsValidVat(vat, residence))
            reasons.Add(Reason(OwnerReadinessDimensions.FiscalProfile, OwnerReadinessReasons.VatNumberInvalid, OwnerImportColumns.VatNumber));

        foreach (var field in MissingAddressFields(owner))
            reasons.Add(Reason(OwnerReadinessDimensions.FiscalProfile, OwnerReadinessReasons.BillingAddressIncomplete, field));

        return reasons;
    }

    /// <summary>The IBAN (with its checksum), the BIC when the IBAN is outside the SEPA area, and the currency the owner is paid in.</summary>
    public static IReadOnlyList<OwnerReadinessReason> Payment(PropertyOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var reasons = new List<OwnerReadinessReason>();
        var iban = OwnerFieldRules.NormalizeIban(owner.Iban);
        if (iban.Length == 0)
        {
            reasons.Add(Reason(OwnerReadinessDimensions.Payment, OwnerReadinessReasons.IbanMissing, OwnerImportColumns.Iban));
        }
        else if (!OwnerFieldRules.IsValidIban(iban))
        {
            reasons.Add(Reason(OwnerReadinessDimensions.Payment, OwnerReadinessReasons.IbanInvalid, OwnerImportColumns.Iban));
        }

        var bic = OwnerFieldRules.NormalizeBic(owner.Bic);
        if (bic.Length > 0 && !OwnerFieldRules.IsValidBic(bic))
        {
            reasons.Add(Reason(OwnerReadinessDimensions.Payment, OwnerReadinessReasons.BicInvalid, OwnerImportColumns.Bic));
        }
        else if (bic.Length == 0 && iban.Length > 0 && !OwnerFieldRules.IsSepaIban(iban))
        {
            reasons.Add(Reason(OwnerReadinessDimensions.Payment, OwnerReadinessReasons.BicMissing, OwnerImportColumns.Bic));
        }

        if (string.IsNullOrWhiteSpace(owner.SettlementCurrency))
            reasons.Add(Reason(OwnerReadinessDimensions.Payment, OwnerReadinessReasons.SettlementCurrencyMissing, OwnerImportColumns.SettlementCurrency));

        return reasons;
    }

    /// <summary>
    /// For each ownership that has not ended by <paramref name="today"/>: a mandate of the agency in force on the day the ownership is judged on
    /// (today, or its first day when it has not started), with the signed document on file and inside the ownership period. An owner with no
    /// ownership in force or to come has no mandate to speak of: <see cref="OwnerReadinessReasons.MandateMissing"/> with no property.
    /// </summary>
    public static IReadOnlyList<OwnerReadinessReason> Mandate(
        IReadOnlyList<OwnerOwnershipFact> ownerships,
        IReadOnlyList<OwnerMandateFact> mandates,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(ownerships);
        ArgumentNullException.ThrowIfNull(mandates);

        var live = ownerships.Where(o => o.End is not { } end || end >= today).ToList();
        if (live.Count == 0)
            return [Reason(OwnerReadinessDimensions.Mandate, OwnerReadinessReasons.MandateMissing)];

        var reasons = new List<OwnerReadinessReason>();
        foreach (var ownership in live.OrderBy(o => o.PropertyId).ThenBy(o => o.Start))
        {
            var day = ownership.Start > today ? ownership.Start : today;
            var covering = mandates
                .Where(m => m.OwnershipId == ownership.OwnershipId && m.From <= day && (m.Until is not { } until || until >= day))
                .OrderByDescending(m => m.HasDocument)
                .ToList();
            if (covering.Count == 0)
            {
                reasons.Add(Reason(OwnerReadinessDimensions.Mandate, OwnerReadinessReasons.MandateMissing, propertyId: ownership.PropertyId));
                continue;
            }

            var mandate = covering[0];
            if (!mandate.HasDocument)
            {
                reasons.Add(Reason(OwnerReadinessDimensions.Mandate, OwnerReadinessReasons.MandateDocumentMissing, propertyId: ownership.PropertyId));
            }
            else if (IsOutside(mandate.From, mandate.Until, ownership.Start, ownership.End))
            {
                reasons.Add(Reason(OwnerReadinessDimensions.Mandate, OwnerReadinessReasons.MandateOutsideOwnership, propertyId: ownership.PropertyId));
            }
        }

        return reasons;
    }

    /// <summary>
    /// What stops the owner from being invited to the portal: a name to greet it by (<paramref name="registryReady"/>), an ownership that is ready, and an
    /// e-mail address that is missing or not an address. Nothing else: the portal does not wait for the kind of the owner, the fiscal profile, the payment
    /// data or the mandate.
    /// </summary>
    public static IReadOnlyList<OwnerReadinessReason> Portal(PropertyOwner owner, bool registryReady, bool ownershipReady)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var reasons = new List<OwnerReadinessReason>();
        if (!registryReady)
            reasons.Add(Reason(OwnerReadinessDimensions.Portal, OwnerReadinessReasons.RegistryNotReady));

        if (!ownershipReady)
            reasons.Add(Reason(OwnerReadinessDimensions.Portal, OwnerReadinessReasons.OwnershipNotReady));

        if (string.IsNullOrWhiteSpace(owner.Email))
        {
            reasons.Add(Reason(OwnerReadinessDimensions.Portal, OwnerReadinessReasons.EmailMissing, OwnerImportColumns.Email));
        }
        else if (!OwnerFieldRules.IsValidEmail(owner.Email))
        {
            reasons.Add(Reason(OwnerReadinessDimensions.Portal, OwnerReadinessReasons.EmailInvalid, OwnerImportColumns.Email));
        }

        return reasons;
    }

    /// <summary>The ownership reasons of an owner: one for each property whose shares are wrong, or <see cref="OwnerReadinessReasons.OwnershipMissing"/> when it holds none.</summary>
    public static IReadOnlyList<OwnerReadinessReason> Ownership(
        IReadOnlyList<OwnerOwnershipFact> ownerships,
        IReadOnlyDictionary<Guid, IReadOnlyList<OwnershipIssue>> issuesByProperty)
    {
        ArgumentNullException.ThrowIfNull(ownerships);
        ArgumentNullException.ThrowIfNull(issuesByProperty);

        if (ownerships.Count == 0)
            return [Reason(OwnerReadinessDimensions.Ownership, OwnerReadinessReasons.OwnershipMissing)];

        var reasons = new List<OwnerReadinessReason>();
        foreach (var propertyId in ownerships.Select(o => o.PropertyId).Distinct().Order())
        {
            if (!issuesByProperty.TryGetValue(propertyId, out var issues))
                continue;

            foreach (var code in issues.Select(IssueCode).Distinct(StringComparer.Ordinal))
                reasons.Add(Reason(OwnerReadinessDimensions.Ownership, code, propertyId: propertyId));
        }

        return reasons;
    }

    /// <summary>The readiness code of a timeline issue.</summary>
    public static string IssueCode(OwnershipIssue issue) => issue.Kind switch
    {
        OwnershipIssueKind.Gap => OwnerReadinessReasons.SharesGap,
        OwnershipIssueKind.Shortfall => OwnerReadinessReasons.SharesShortfall,
        OwnershipIssueKind.Excess => OwnerReadinessReasons.SharesExcess,
        OwnershipIssueKind.OwnerOverlap => OwnerReadinessReasons.OwnerOverlap,
        _ => throw new ArgumentOutOfRangeException(nameof(issue), issue.Kind, "Unknown ownership issue."),
    };

    /// <summary>True when the period [<paramref name="from"/>, <paramref name="until"/>] is not inside [<paramref name="start"/>, <paramref name="end"/>].</summary>
    public static bool IsOutside(DateOnly from, DateOnly? until, DateOnly start, DateOnly? end)
    {
        if (from < start)
            return true;

        if (end is not { } ownershipEnd)
            return false;

        return until is not { } mandateEnd || mandateEnd > ownershipEnd;
    }

    private static IEnumerable<string> MissingAddressFields(PropertyOwner owner)
    {
        if (string.IsNullOrWhiteSpace(owner.BillingStreet))
            yield return OwnerImportColumns.BillingStreet;

        if (string.IsNullOrWhiteSpace(owner.BillingPostalCode))
            yield return OwnerImportColumns.BillingPostalCode;

        if (string.IsNullOrWhiteSpace(owner.BillingCity))
            yield return OwnerImportColumns.BillingCity;

        if (string.IsNullOrWhiteSpace(owner.BillingCountry))
            yield return OwnerImportColumns.BillingCountry;

        if (string.Equals(owner.BillingCountry, OwnerFieldRules.Italy, StringComparison.Ordinal) && string.IsNullOrWhiteSpace(owner.BillingProvince))
            yield return OwnerImportColumns.BillingProvince;
    }

    private static OwnerReadinessReason Reason(string dimension, string code, string? field = null, Guid? propertyId = null) =>
        new(dimension, code, field, propertyId);
}
