using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Leases;

namespace Casazen.Infrastructure.External;

/// <summary>The verified DocuEngine registration schema, not a positional guess. Files are resolved by the preparation service.</summary>
public static class OpenapiLeaseSearch
{
    public static readonly string[] DefaultFieldOrder = OpenapiRegistrationSchema.Current.Fields.Select(f => f.Name).ToArray();

    public static IReadOnlyDictionary<string, string> Build(LeaseContract lease, ReadOnlyMemory<byte> signedContract,
        IReadOnlyList<string> fieldOrder, IReadOnlyDictionary<string, string>? files = null, OpenapiRegistrationData? data = null)
    {
        if (fieldOrder.Count > 0 && !fieldOrder.SequenceEqual(DefaultFieldOrder))
            throw new InvalidOperationException("LeaseFieldOrder does not match the verified DocuEngine registration schema.");
        var values = Prefill(lease);
        foreach (var item in (data ?? OpenapiRegistrationData.Read(lease.OpenapiRegistrationData)).Values)
            values[item.Key] = item.Value;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in OpenapiRegistrationSchema.Current.Fields)
        {
            if (field.Name == "contractFile")
            {
                if (!signedContract.IsEmpty) result[field.Key] = Convert.ToBase64String(signedContract.Span);
            }
            else if (field.Type == "file")
            {
                if (files?.TryGetValue(field.Name, out var content) == true) result[field.Key] = content;
            }
            else if (values.TryGetValue(field.Name, out var value) && !string.IsNullOrWhiteSpace(value))
                result[field.Key] = value;
        }
        return result;
    }

    public static Dictionary<string, string> Prefill(LeaseContract lease)
    {
        var p = lease.Property;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["rentalContractType"] = lease.ContractType == LeaseContractType.Concordato ? "L2" : "L1",
            ["contractStartDate"] = lease.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["contractEndDate"] = lease.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["contractDate"] = lease.StipulaDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            // Sub-year contracts need the consideration for the whole term, explicitly entered by the holder.
            ["annualFee"] = lease.EndDate.AddDays(1) >= lease.StartDate.AddYears(1)
                ? (lease.MonthlyRent * 12m).ToString("0.00", CultureInfo.InvariantCulture) : "",
            ["taxRegime"] = lease.TaxRegime switch { LeaseTaxRegime.CedolareSecca => "CEDOLARE", LeaseTaxRegime.Ordinario => "ORDINARIO", _ => "" },
            ["town"] = p?.City ?? "",
            ["address"] = p?.Address ?? "",
            ["cadastralSheet"] = p?.CadastralSheet ?? "",
            ["cadastralParcel"] = p?.CadastralParcel ?? "",
            ["cadastralSub"] = p?.CadastralSubaltern ?? "",
            ["cadastralCategory"] = p?.CadastralCategory?.Replace("/", "").Replace(" ", "").ToUpperInvariant() ?? "",
            ["cadastralIncome"] = p?.CadastralIncome?.ToString("0.00", CultureInfo.InvariantCulture) ?? "",
        };
        foreach (var role in new[] { PartyRole.Landlord, PartyRole.Tenant })
        {
            var parties = lease.Parties.Where(party => party.Role == role).OrderBy(party => party.Position).ToList();
            for (var i = 0; i < Math.Min(parties.Count, 3); i++)
            {
                var prefix = (i == 0 ? "" : i == 1 ? "second" : "third") + (role == PartyRole.Landlord ? "Landlord" : "Conductor");
                prefix = char.ToLowerInvariant(prefix[0]) + prefix[1..];
                values[prefix + "Name"] = (parties[i].FirstName + " " + parties[i].LastName).Trim();
                values[prefix + "TaxCode"] = parties[i].FiscalCode;
            }
            if (role == PartyRole.Landlord && parties.FirstOrDefault() is { } landlord)
            {
                values["applicantName"] = (landlord.FirstName + " " + landlord.LastName).Trim();
                values["applicantPhone"] = landlord.ContactPhone ?? "";
                values["applicantEmail"] = landlord.ContactEmail;
                values[landlord.FiscalCode.Length == 11 ? "legalApplicantTaxCode" : "privateApplicantTaxCode"] = landlord.FiscalCode;
            }
        }
        return values;
    }
}
