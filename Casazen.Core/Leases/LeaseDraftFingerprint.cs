using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Leases;

public static class LeaseDraftFingerprint
{
    public static string Compute(LeaseDraftValues values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var text = new StringBuilder(512);
        Field(text, "property", values.PropertyId?.ToString("D"));
        Parties(text, "landlord", values.Landlords);
        Parties(text, "tenant", values.Tenants);
        Field(text, "type", LeaseDraftRules.ParseContractType(values.ContractType)?.ToString() ?? LeaseDraftRules.Clean(values.ContractType)?.ToLowerInvariant());
        Field(text, "regime", LeaseDraftRules.ParseTaxRegime(values.TaxRegime)?.ToString() ?? LeaseDraftRules.Clean(values.TaxRegime)?.ToLowerInvariant());
        Field(text, "rent", Number(values.MonthlyRent));
        Field(text, "deposit", Number(values.SecurityDeposit));
        // The characteristics of the unit are part of the contract only for a canone concordato: the columns of any other type keep none,
        // so a form that goes on sending them after the landlord chose the libero must not look like a change. The areas are kept to the
        // cents of their columns (10,2), so the value read back from the database is the same as the one typed.
        if (LeaseDraftRules.ParseContractType(values.ContractType) == LeaseContractType.Concordato && values.Concordato is { } c)
        {
            Field(text, "c.sqm", Cents(c.Sqm));
            Field(text, "c.counts", string.Join(',', c.TypeAElementCount, c.TypeBElementCount, c.TypeCElementCount, c.TypeDElementCount, c.QualifyingTypeDElementCount));
            Field(text, "c.flags", string.Join(',', c.IsFurnished, c.StoveHeating, c.AirConditioning));
            Field(text, "c.appurtenances", string.Join(',', Cents(c.GarageSqm), Cents(c.BalconySqm), Cents(c.OtherAppurtenanceSqm), Cents(c.PrivateGreenSqm)));
            Field(text, "c.zone", LeaseDraftRules.Clean(c.ZoneName));
            Field(text, "c.sheet", LeaseDraftRules.Clean(c.CadastralSheet));
        }

        Field(text, "start", LeaseDraftRules.ParseDate(values.StartDate)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? LeaseDraftRules.Clean(values.StartDate));

        if (LeaseDraftRules.ParseContractType(values.ContractType) == LeaseContractType.Transitorio)
        {
            Field(text, "end", LeaseDraftRules.ParseDate(values.EndDate)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? LeaseDraftRules.Clean(values.EndDate));
            Field(text, "t.party", LeaseDraftRules.ParseNeedParty(values.TransitoryParty)?.ToString() ?? LeaseDraftRules.Clean(values.TransitoryParty)?.ToLowerInvariant());
            Field(text, "t.need", LeaseDraftRules.Clean(values.TransitoryNeed));
            Field(text, "t.documentation", LeaseDraftRules.Clean(values.TransitoryDocumentation));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static void Parties(StringBuilder text, string role, IReadOnlyList<LeaseDraftParty> parties)
    {
        for (var i = 0; i < parties.Count; i++)
        {
            var p = parties[i];
            Field(text, $"{role}[{i}].name", $"{LeaseDraftRules.Clean(p.FirstName)}|{LeaseDraftRules.Clean(p.LastName)}");
            Field(text, $"{role}[{i}].fiscal", ItalianFiscalCode.Normalize(p.FiscalCode));
            Field(text, $"{role}[{i}].citizenship", LeaseDraftRules.Clean(p.Citizenship)?.ToUpperInvariant());
            Field(text, $"{role}[{i}].residence", LeaseDraftRules.Clean(p.Residence));
            Field(text, $"{role}[{i}].email", LeaseDraftRules.Clean(p.ContactEmail)?.ToLowerInvariant());
            Field(text, $"{role}[{i}].phone", LeaseDraftRules.Clean(p.ContactPhone) is { } phone ? LeaseDraftRules.NormalizePhone(phone) : null);
            Field(text, $"{role}[{i}].documents", LeaseDraftRules.ParseDate(p.DocumentsVerifiedOn)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? LeaseDraftRules.Clean(p.DocumentsVerifiedOn));
        }
    }

    private static void Field(StringBuilder text, string name, string? value) =>
        text.Append(name).Append('=').Append(value?.Length.ToString(CultureInfo.InvariantCulture) ?? "-").Append(':').Append(value).Append('\n');

    private static string? Number(decimal? value) => value?.ToString("0.########", CultureInfo.InvariantCulture);

    private static string Cents(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.########", CultureInfo.InvariantCulture);
}
