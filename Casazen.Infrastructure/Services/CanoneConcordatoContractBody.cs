using System.Globalization;
using System.Text;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;

namespace Casazen.Infrastructure.Services;

internal static class CanoneConcordatoContractBody
{
    public static string Build(LeaseContract lease, string versionId)
    {
        var city = lease.Property?.City ?? string.Empty;
        var address = lease.Property?.Address ?? string.Empty;
        var annual = lease.MonthlyRent * 12;

        var sb = new StringBuilder();
        sb.AppendLine("BOZZA - MODELLO DI LAVORO INTERNO. Non utilizzare per la stipula senza revisione di un legale o associazione di categoria.");
        sb.AppendLine("CasaZen non rilascia l'attestazione di conformita e non e intermediario abilitato (DPR 322/1998).");
        sb.AppendLine("Contratto di locazione ad uso abitativo a canone concordato.");
        sb.AppendLine("art. 2 comma 3 Legge 431/1998 - contratto tipo 3+2 - D.M. 16 gennaio 2017.");
        sb.AppendLine($"TemplateVersion: {versionId}");
        sb.AppendLine(CanoneConcordatoCopy.Disclaimer);
        AppendPartyLines(sb, "Locatore", lease.Parties, PartyRole.Landlord);
        AppendPartyLines(sb, "Conduttore", lease.Parties, PartyRole.Tenant);
        sb.AppendLine($"Comune: {city}");
        sb.AppendLine($"Indirizzo: {address}");
        sb.AppendLine($"Decorrenza: {lease.StartDate:yyyy-MM-dd} - {lease.EndDate:yyyy-MM-dd}");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Canone mensile: {lease.MonthlyRent:0.00} EUR"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Canone annuo indicativo: {annual:0.00} EUR"));
        sb.AppendLine("Art. 4 Canone determinato in conformita all'Accordo Territoriale vigente per il Comune.");
        sb.AppendLine("Art. 6 Regime fiscale canone concordato. Cedolare 10% solo se il Comune e ATA con verifica diretta.");
        sb.AppendLine("Art. 7 Attestazione di conformita: se il contratto non e assistito, le parti la acquisiscono prima della registrazione.");
        return sb.ToString();
    }

    public static string BuildGenericDraft(LeaseContract lease, string versionId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BOZZA - MODELLO DI LAVORO INTERNO. Da confermare con legale.");
        sb.AppendLine($"Regime fiscale: {lease.FiscalRegime}");
        sb.AppendLine($"TemplateVersion: {versionId}");
        AppendPartyLines(sb, "Locatore", lease.Parties, PartyRole.Landlord);
        AppendPartyLines(sb, "Conduttore", lease.Parties, PartyRole.Tenant);
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"Canone mensile: {lease.MonthlyRent:0.00} EUR"));
        sb.AppendLine($"Decorrenza: {lease.StartDate:yyyy-MM-dd} - {lease.EndDate:yyyy-MM-dd}");
        sb.AppendLine($"Comune: {lease.Property?.City}");
        return sb.ToString();
    }

    private static void AppendPartyLines(StringBuilder sb, string label, IEnumerable<Party>? parties, PartyRole role)
    {
        var matchingParties = parties?
            .Where(p => p.Role == role)
            .ToList() ?? [];

        if (matchingParties.Count == 0)
        {
            sb.AppendLine($"{label}: [da compilare]");
            return;
        }

        for (var i = 0; i < matchingParties.Count; i++)
        {
            var party = matchingParties[i];
            var suffix = matchingParties.Count == 1 ? string.Empty : $" {i + 1}";
            sb.AppendLine($"{label}{suffix}: {party.FirstName} {party.LastName} CF:{party.FiscalCode}");
        }
    }
}
