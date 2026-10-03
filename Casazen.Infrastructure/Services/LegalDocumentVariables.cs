using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Casazen.Core.Options;
using Casazen.Core.Services;
using Casazen.Infrastructure.Email;
using Microsoft.Extensions.Configuration;

namespace Casazen.Infrastructure.Services;

/// <summary>
/// Values substituted in the legal documents (LEGAL-TEXTS): the facts the texts need and the code cannot invent. Each
/// token is resolved from the configuration of the running environment, never written in the text or in code:
/// <list type="bullet">
///   <item>the data of the controller (<c>Controller.*</c>, <c>Terms.GoverningCourt</c>): no default, a missing value
///   keeps every document that uses it unpublished (fail-closed, see <see cref="LegalDocumentTemplate"/>);</item>
///   <item>the negotiable terms (<c>Terms.*Days</c>, <c>Dpa.*</c>): a proposed default sits in <c>appsettings.json</c>
///   and is marked "to be confirmed by a lawyer" in the runbook;</item>
///   <item>the real catalogue (<c>Plans.*</c>, from <see cref="PlanCatalog"/> and <c>Billing:Display</c>), the grace period
///   of the billing (<c>Billing.PastDueGraceDays</c>) and the retention periods in force (<c>Retention.*</c>, from
///   <c>Gdpr:Retention</c>: absent when no period is applied, so the text can say so honestly).</item>
/// </list>
/// Text values are HTML-encoded here: the templates are HTML fragments. Runbook: <c>docs/runbooks/legal-documents.md</c>.
/// </summary>
public sealed partial class LegalVariables
{
    private const int MaxTextLength = 300;

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private LegalVariables()
    {
    }

    /// <summary>Every token the templates may use, resolved or not.</summary>
    public IReadOnlyCollection<string> Tokens => _entries.Keys;

    public bool IsKnown(string token) => _entries.ContainsKey(token);

    /// <summary>True when the token is known and has a usable value in this environment.</summary>
    public bool Has(string token) => _entries.TryGetValue(token, out var entry) && entry.Value is not null;

    /// <summary>The HTML-encoded value, or null when the token is unknown or not configured.</summary>
    public string? Get(string token) => _entries.TryGetValue(token, out var entry) ? entry.Value : null;

    /// <summary>
    /// The Railway variable to set for the token (<c>Legal__Controller__Name</c>), for the messages that name what is
    /// missing: never the value.
    /// </summary>
    public string ConfigurationName(string token) =>
        _entries.TryGetValue(token, out var entry) ? entry.Variable : token;

    /// <summary>
    /// The variables of the environment in <paramref name="language"/> (<c>it</c> or <c>en</c>): the language only
    /// changes the words and number formats of the computed values (plans, retention periods).
    /// </summary>
    public static LegalVariables Create(IConfiguration configuration, string language)
    {
        var variables = new LegalVariables();
        var english = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);

        foreach (var spec in ConfiguredSpecs)
            variables.Add(spec.Token, spec.Variable, spec.Read(configuration));

        variables.AddSite(configuration);
        variables.AddPlans(configuration, english);
        variables.AddRetention(configuration, english);
        return variables;
    }

    private void Add(string token, string variable, string? value) =>
        _entries[token] = new Entry(value is null ? null : WebUtility.HtmlEncode(value), variable);

    private void AddSite(IConfiguration configuration)
    {
        string? baseUrl = null;
        if (PublicSiteOptions.TryGetBaseUri(PublicSiteOptions.ResolveBaseUrl(configuration), out var uri))
            baseUrl = uri.ToString().TrimEnd('/');

        Add("Site.BaseUrl", "App__PublicSiteBaseUrl", baseUrl);
    }

    private void AddPlans(IConfiguration configuration, bool english)
    {
        foreach (var plan in PlanCatalog.All)
        {
            var prefix = $"Plans.{plan.Tier}";
            var name = Clean(configuration[$"Billing:Display:{plan.Tier}:Name"], MaxTextLength) ?? plan.DisplayName;
            Add($"{prefix}.Name", $"Billing__Display__{plan.Tier}__Name", name);

            var allowance = plan.MaxProperties == int.MaxValue
                ? (english ? "unlimited" : "illimitati")
                : plan.MaxProperties.ToString(CultureInfo.InvariantCulture);
            Add($"{prefix}.MaxProperties", $"Billing__Display__{plan.Tier}__MaxProperties", allowance);

            // No price (0, or not a number) is not an error: the text then sends the reader to the price shown at the
            // purchase, exactly like the plans page does.
            var price = configuration.GetValue<decimal>($"Billing:Display:{plan.Tier}:PriceMonthly", 0m);
            Add($"{prefix}.PriceMonthly", $"Billing__Display__{plan.Tier}__PriceMonthly", price > 0 ? FormatPrice(price, english) : null);
        }
    }

    private void AddRetention(IConfiguration configuration, bool english)
    {
        GdprRetentionOptions retention;
        try
        {
            retention = configuration.GetSection(GdprOptions.SectionName).Get<GdprOptions>()?.Retention ?? new GdprRetentionOptions();
        }
        catch (InvalidOperationException)
        {
            // A value that is not a number: the retention job cannot apply that category either, so no period is claimed.
            retention = new GdprRetentionOptions();
        }

        (string Name, RetentionPeriodOptions Period)[] periods =
        [
            (nameof(GdprRetentionOptions.DocumentScans), retention.DocumentScans),
            (nameof(GdprRetentionOptions.AlloggiatiData), retention.AlloggiatiData),
            (nameof(GdprRetentionOptions.Marketing), retention.Marketing),
            (nameof(GdprRetentionOptions.FiscalData), retention.FiscalData),
            (nameof(GdprRetentionOptions.LeaseParties), retention.LeaseParties),
        ];

        // Only a period the retention job really applies (an amount and a cited source) is a period: otherwise the token
        // is absent and the text says that no automatic deletion runs for the category.
        foreach (var (name, period) in periods)
            Add($"Retention.{name}", $"Gdpr__Retention__{name}__Years", period.IsConfigured ? FormatDuration(period, english) : null);
    }

    private static string FormatPrice(decimal price, bool english)
    {
        var amount = price.ToString("0.00", CultureInfo.InvariantCulture);
        return english ? $"€{amount}" : $"{amount.Replace('.', ',')} €";
    }

    private static string FormatDuration(RetentionPeriodOptions period, bool english)
    {
        var parts = new List<string>();
        AddPart(parts, period.Years, english ? "year" : "anno", english ? "years" : "anni");
        AddPart(parts, period.Months, english ? "month" : "mese", english ? "months" : "mesi");
        AddPart(parts, period.Days, english ? "day" : "giorno", english ? "days" : "giorni");
        return parts.Count > 0 ? string.Join(", ", parts) : (english ? "0 days" : "0 giorni");
    }

    private static void AddPart(List<string> parts, int? amount, string singular, string plural)
    {
        if (amount is null or 0)
            return;

        parts.Add($"{amount.Value.ToString(CultureInfo.InvariantCulture)} {(amount == 1 ? singular : plural)}");
    }

    /// <summary>Trimmed value, or null when empty, a placeholder, too long or with characters that cannot be in a text.</summary>
    internal static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength
            || trimmed.Any(char.IsControl)
            || trimmed.Contains("{{", StringComparison.Ordinal)
            || trimmed.Contains("}}", StringComparison.Ordinal)
            || IsPlaceholder(trimmed))
            return null;

        return trimmed;
    }

    /// <summary>Values committed as examples (<c>YOUR_…</c>, <c>TODO</c>, <c>[…]</c>, <c>…</c>) are not data of the controller.</summary>
    private static bool IsPlaceholder(string value) =>
        value.StartsWith('[')
        || value.StartsWith('<')
        || value.Contains("YOUR_", StringComparison.OrdinalIgnoreCase)
        || PlaceholderWordRegex().IsMatch(value)
        || value.Contains("xxxxxxxx", StringComparison.OrdinalIgnoreCase)
        || value.Contains("...", StringComparison.Ordinal)
        || value.Contains('…');

    private static string? CleanEmail(string? value)
    {
        var clean = Clean(value, 254);
        return clean is not null && EmailRegex().IsMatch(clean) ? clean : null;
    }

    private static string? CleanInteger(string? value, int min, int max) =>
        int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= min && number <= max
            ? number.ToString(CultureInfo.InvariantCulture)
            : null;

    /// <summary>Token, the Railway variable that sets it, and how its configuration is read and checked.</summary>
    private sealed record Spec(string Token, string Variable, Func<IConfiguration, string?> Read);

    private sealed record Entry(string? Value, string Variable);

    private static Spec Text(string token, string key, int maxLength = MaxTextLength, string? fallbackKey = null) =>
        new(token, Variable(key), c => Clean(c[key], maxLength) ?? (fallbackKey is null ? null : Clean(c[fallbackKey], maxLength)));

    private static Spec Email(string token, string key) => new(token, Variable(key), c => CleanEmail(c[key]));

    private static Spec Number(string token, string key, int min, int max, int? codeDefault = null) =>
        new(token, Variable(key), c => CleanInteger(c[key] ?? codeDefault?.ToString(CultureInfo.InvariantCulture), min, max));

    private static string Variable(string key) => key.Replace(":", "__", StringComparison.Ordinal);

    /// <summary>
    /// The configured tokens. <c>Billing.PastDueGraceDays</c> keeps the default of
    /// <c>EntitlementService</c> (7 days when not configured): the text states what the code does.
    /// </summary>
    private static readonly Spec[] ConfiguredSpecs =
    [
        Text("Controller.Name", "Legal:Controller:Name", 200),
        Text("Controller.Address", "Legal:Controller:Address"),
        Text("Controller.VatId", "Legal:Controller:VatId", 32, fallbackKey: "Billing:VatNumber"),
        Text("Controller.ReaNumber", "Legal:Controller:ReaNumber", 64),
        Email("Controller.Pec", "Legal:Controller:Pec"),
        Email("Controller.PrivacyEmail", "Legal:Controller:PrivacyEmail"),
        Email("Controller.DpoEmail", "Legal:Controller:DpoEmail"),
        Text("Terms.GoverningCourt", "Legal:Terms:GoverningCourt", 100),
        Number("Terms.ChangeNoticeDays", "Legal:Terms:ChangeNoticeDays", 1, 365),
        Number("Terms.TerminationNoticeDays", "Legal:Terms:TerminationNoticeDays", 1, 365),
        Number("Terms.LiabilityCapMonths", "Legal:Terms:LiabilityCapMonths", 1, 120),
        Number("Terms.DataReturnDays", "Legal:Terms:DataReturnDays", 1, 365),
        Number("Dpa.BreachNotificationHours", "Legal:Dpa:BreachNotificationHours", 1, 168),
        Number("Dpa.SubprocessorNoticeDays", "Legal:Dpa:SubprocessorNoticeDays", 1, 365),
        Number("Dpa.AuditNoticeDays", "Legal:Dpa:AuditNoticeDays", 1, 365),
        Number("Billing.PastDueGraceDays", "Billing:PastDueGraceDays", 0, 365, codeDefault: 7),
    ];

    [GeneratedRegex(@"^[^@\s<>""]+@[^@\s<>""]+\.[^@\s<>""]+$")]
    private static partial Regex EmailRegex();

    // Whole words only: a surname such as "Todone" is data, "TODO" is a note to self.
    [GeneratedRegex(@"\b(TODO|TBD|CHANGE_ME)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderWordRegex();
}
