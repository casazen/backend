using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Options;

namespace Casazen.Infrastructure.External;

/// <summary>
/// Maps a CasaZen lease onto DocuEngine <c>search.field0</c>…<c>fieldN</c>.
/// Openapi does not publish the field order of the lease-registration service (it comes from <c>GET /documents</c>).
/// The default order is the set of facts the product page names, with the contract type CasaZen already stores.
/// <see cref="OpenapiOptions.LeaseFieldOrder"/> replaces it once that call has been confirmed.
/// </summary>
public static class OpenapiLeaseSearch
{
    public static readonly string[] DefaultFieldOrder =
    [
        "contractType",
        "taxRegime",
        "startDate",
        "endDate",
        "annualRent",
        "landlordFiscalCode",
        "tenantFiscalCode",
        "city",
        "address",
        "cadastralSheet",
        "cadastralParcel",
        "cadastralSubaltern",
        "cadastralCategory",
        "signedContract",
    ];

    public static IReadOnlyDictionary<string, string> Build(
        LeaseContract lease,
        ReadOnlyMemory<byte> signedContract,
        IReadOnlyList<string> fieldOrder)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var order = fieldOrder.Count > 0 ? fieldOrder : DefaultFieldOrder;
        var values = Values(lease, signedContract);
        var search = new Dictionary<string, string>(order.Count, StringComparer.Ordinal);
        var index = 0;
        foreach (var key in order)
        {
            if (!values.TryGetValue(key, out var value))
                continue;
            search["field" + index.ToString(CultureInfo.InvariantCulture)] = value;
            index++;
        }

        return search;
    }

    private static Dictionary<string, string> Values(LeaseContract lease, ReadOnlyMemory<byte> signedContract)
    {
        var landlord = lease.Parties?.Where(p => p.Role == PartyRole.Landlord).OrderBy(p => p.Position).FirstOrDefault();
        var tenant = lease.Parties?.Where(p => p.Role == PartyRole.Tenant).OrderBy(p => p.Position).FirstOrDefault();
        var annual = decimal.Round(lease.MonthlyRent * 12m, 2, MidpointRounding.AwayFromZero);
        var property = lease.Property;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contractType"] = lease.ContractType.ToString(),
            ["taxRegime"] = (lease.TaxRegime?.ToString() ?? lease.FiscalRegime.ToString()),
            ["startDate"] = lease.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["endDate"] = lease.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["annualRent"] = annual.ToString("0.00", CultureInfo.InvariantCulture),
            ["landlordFiscalCode"] = landlord?.FiscalCode ?? string.Empty,
            ["tenantFiscalCode"] = tenant?.FiscalCode ?? string.Empty,
            ["city"] = property?.City ?? string.Empty,
            ["address"] = property?.Address ?? string.Empty,
            ["cadastralSheet"] = property?.CadastralSheet ?? string.Empty,
            ["cadastralParcel"] = property?.CadastralParcel ?? string.Empty,
            ["cadastralSubaltern"] = property?.CadastralSubaltern ?? string.Empty,
            ["cadastralCategory"] = property?.CadastralCategory ?? string.Empty,
            ["signedContract"] = signedContract.IsEmpty ? string.Empty : Convert.ToBase64String(signedContract.Span),
        };
    }
}
