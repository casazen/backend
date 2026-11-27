using System.Globalization;
using System.Net.Mail;
using System.Text.RegularExpressions;
using Casazen.Core.Leases;
using Casazen.Core.Regulatory;

namespace Casazen.Infrastructure.Services;

/// <summary>Validation of the verified 61-field service, including conditional groups from its validation expression.</summary>
public static class OpenapiRegistrationValidation
{
    public static IReadOnlyList<OpenapiRegistrationIssue> Validate(OpenapiRegistrationData data, bool signedPdf)
    {
        var issues = new List<OpenapiRegistrationIssue>();
        var fields = OpenapiRegistrationSchema.Current.Fields;
        string Value(string name) => data.Values.GetValueOrDefault(name)?.Trim() ?? "";
        bool Has(string name) => name == "contractFile" ? signedPdf :
            data.Documents.GetValueOrDefault(name) != Guid.Empty || Value(name).Length > 0;
        void Issue(string name, string text) { if (!issues.Any(i => i.Field == name)) issues.Add(new(name, text)); }
        void Required(string name) { if (!Has(name)) Issue(name, "Campo obbligatorio."); }
        foreach (var item in data.Values)
        {
            var field = fields.FirstOrDefault(f => f.Name == item.Key);
            if (field is null || field.Type == "file") Issue(item.Key, "Campo non ammesso.");
            else if ((item.Value?.Length ?? 0) > 500 || item.Value?.Any(char.IsControl) == true) Issue(item.Key, "Valore non valido (massimo 500 caratteri).");
        }
        foreach (var item in data.Documents)
            if (!fields.Any(f => f.Name == item.Key && f.Type == "file" && f.Name != "contractFile"))
                Issue(item.Key, "Allegato non ammesso.");
        foreach (var f in fields)
        {
            if (f.Required) Required(f.Name);
            var value = Value(f.Name);
            if (value.Length == 0 || f.Type == "file") continue;
            if (f.Options.Count > 0 && !f.Options.Any(o => o.Code == value)) Issue(f.Name, "Seleziona un valore previsto.");
            if (f.Type == "date" && !DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                Issue(f.Name, "Inserisci una data valida.");
            if (f.Type is "integer" or "float")
            {
                if (!decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)
                    || number < 0 || (f.Type == "integer" && (number < 1 || number != decimal.Truncate(number))))
                    Issue(f.Name, "Inserisci un numero valido.");
            }
            if (f.Type == "email" && (!MailAddress.TryCreate(value, out var email) || email.Address != value)) Issue(f.Name, "Inserisci un indirizzo email valido.");
            if (f.Type == "taxCode" && !ItalianFiscalCode.IsValid(value)) Issue(f.Name, "Codice fiscale non valido.");
            if (f.Name.EndsWith("Percentage", StringComparison.Ordinal)
                && decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var percentage)
                && (percentage <= 0 || percentage > 100)) Issue(f.Name, "La quota deve essere maggiore di 0 e non superiore a 100.");
        }
        if (Has("privateApplicantTaxCode") == Has("legalApplicantTaxCode"))
            Issue("privateApplicantTaxCode", "Indica un solo codice fiscale del richiedente: privato oppure persona giuridica.");
        var cedolare = Value("taxRegime") == "CEDOLARE";
        var ordinary = Value("taxRegime") == "ORDINARIO";
        if (ordinary)
        {
            Required("ibanCode"); Required("accountHolderTaxCode"); Required("taxPaymentType");
            if (Has("ibanCode") && !ValidIban(Value("ibanCode"))) Issue("ibanCode", "IBAN non valido.");
        }
        foreach (var role in new[] { "Landlord", "Conductor" })
        {
            var previous = true;
            decimal percentages = 0;
            foreach (var ordinal in new[] { "", "second", "third" })
            {
                var prefix = ordinal + role;
                prefix = char.ToLowerInvariant(prefix[0]) + prefix[1..];
                var names = new[] { prefix + "Name", prefix + "TaxCode", prefix + "Document" };
                var present = names.Any(Has) || (role == "Landlord" && Has(prefix + "Percentage"));
                if (present)
                {
                    foreach (var name in names) Required(name);
                    if (!previous) Issue(names[0], "Completa prima la parte precedente.");
                    if (role == "Landlord" && cedolare)
                    {
                        Required(prefix + "Percentage");
                        if (decimal.TryParse(Value(prefix + "Percentage"), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var share)) percentages += share;
                    }
                }
                previous = present;
            }
            if (role == "Landlord" && cedolare && percentages > 100) Issue("landlordPercentage", "La somma delle quote non può superare 100%.");
        }
        var previousProperty = true;
        foreach (var ordinal in new[] { "second", "third" })
        {
            var names = new[] { "CadastralSheet", "CadastralParcel", "CadastralSub", "CadastralIncome", "CadastralCategory" }.Select(n => ordinal + "Property" + n).ToArray();
            var present = names.Any(Has);
            if (present)
            {
                foreach (var name in names) Required(name);
                if (!previousProperty) Issue(names[0], "Completa prima il secondo immobile.");
            }
            previousProperty = present;
        }
        if (Value("feesPaymentGuarantees") == "SI") { Required("guaranteeProviderTaxCode"); Required("maximumGuaranteeAmount"); }
        if (DateOnly.TryParse(Value("contractStartDate"), out var start) && DateOnly.TryParse(Value("contractEndDate"), out var end) && end < start)
            Issue("contractEndDate", "La fine deve essere successiva all’inizio.");
        if (Has("province") && !Regex.IsMatch(Value("province"), "^[A-Z]{2}$", RegexOptions.CultureInvariant)) Issue("province", "Inserisci la sigla della provincia (es. MI).");
        if (Has("territorialOffice") && !Regex.IsMatch(Value("territorialOffice"), "^[A-Z0-9]{3}$", RegexOptions.CultureInvariant))
            Issue("territorialOffice", "Inserisci il codice di tre caratteri dell’ufficio territoriale dell’Agenzia delle Entrate.");
        return issues;
    }

    private static bool ValidIban(string value)
    {
        if (!Regex.IsMatch(value, "^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}$", RegexOptions.CultureInvariant)) return false;
        var remainder = 0;
        foreach (var c in value[4..] + value[..4])
        {
            if (char.IsDigit(c)) remainder = (remainder * 10 + c - '0') % 97;
            else { var n = c - 'A' + 10; remainder = (remainder * 100 + n) % 97; }
        }
        return remainder == 1;
    }
}
