using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Casazen.Core.Options;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Push;
using Casazen.Infrastructure.Services;
using Casazen.Infrastructure.Services.ICal;
using Casazen.Infrastructure.Storage;
using Casazen.Web.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Casazen.Tests.Unit.Configuration;

/// <summary>
/// DEPLOY-CFG: <c>docs/runbooks/deploy-checklist.md</c> is the single list of the deploy configuration and must stay true
/// to the code. Three checks keep it that way:
/// <list type="number">
/// <item>every backend variable written in the checklist (<c>Section__Key</c>, with <c>{a,b}</c> alternatives and
/// <c>{Name}</c>/<c>*</c> wildcards) exists in the code: in <c>appsettings.json</c>, as a property of the options class bound to
/// its section, or as a literal key read by the code;</item>
/// <item>every key of the committed example files (<c>secrets/*.example.json</c>) is documented in the checklist;</item>
/// <item>every configuration section of <c>appsettings.json</c> and every options class with a <c>SectionName</c> is
/// mentioned in the checklist, so a new section cannot reach production undocumented.</item>
/// </list>
/// A failure names what to change. A new setting: add a row to the checklist (§ 2 for the backend), with its effect when
/// missing. A setting you removed: delete its row. Frontend and mobile variables are checked in their own repositories.
/// </summary>
public class DeployChecklistConsistencyTests
{
    private const string NotCheckedStart = "<!-- deploy-checklist:not-checked -->";
    private const string NotCheckedEnd = "<!-- /deploy-checklist:not-checked -->";

    /// <summary>Options classes bound to a section, to resolve <c>Section__Property__Sub</c> by reflection.</summary>
    private static readonly Dictionary<string, Type> OptionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Auth0"] = typeof(Auth0Options),
        ["Email"] = typeof(EmailOptions),
        ["App"] = typeof(PublicSiteOptions),
        ["Storage"] = typeof(StorageOptions),
        ["ESign"] = typeof(ESignOptions),
        ["Ai"] = typeof(AiOptions),
        ["Expo"] = typeof(ExpoPushOptions),
        ["Cin"] = typeof(CinOptions),
        ["Gdpr"] = typeof(GdprOptions),
        ["Compliance"] = typeof(ComplianceOptions),
        ["PublicHost"] = typeof(PublicHostOptions),
        ["CheckIn"] = typeof(GuestCheckInOptions),
        ["StayAlerts"] = typeof(StayAlertOptions),
        ["Suppliers"] = typeof(SupplierRegistrationOptions),
        ["ICalImport"] = typeof(ICalImportOptions),
        ["SafeExternalHttp"] = typeof(Casazen.Infrastructure.Http.SafeExternalHttpOptions),
        ["Rli"] = typeof(RliOptions),
        ["LeaseTemplates"] = typeof(LeaseTemplateOptions),
        ["ShortStayFiscal"] = typeof(ShortStayFiscalOptions),
        ["CedolareAdvisory"] = typeof(CedolareAdvisoryOptions),
        ["Cors"] = typeof(CorsOriginOptions),
        ["Seo"] = typeof(SeoBootstrapOptions),
        ["Vercel"] = typeof(VercelDomainsOptions),
    };

    /// <summary>Roots whose keys are composed at runtime (no literal in the code, not in <c>appsettings.json</c>), with why.</summary>
    private static readonly Dictionary<string, string> RuntimeComposedRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RateLimiting"] = "RateLimitingServiceCollectionExtensions reads $\"RateLimiting:{policy}\" for every policy of its list " +
                           "(docs/runbooks/proxy-ip.md); the policy names are code constants, not configuration literals.",
    };

    /// <summary>Plain environment variables (no <c>__</c>) the checklist names, each of which must still appear in the sources.</summary>
    private static readonly string[] EnvironmentVariablesInSources =
    [
        "RAILWAY_GIT_COMMIT_SHA", "GIT_COMMIT_SHA", "RAILWAY_PROJECT_ID", "RAILWAY_ENVIRONMENT_NAME", "RAILWAY_REPLICA_REGION",
        "CASAZEN_MIGRATION_TARGET",
    ];

    [Fact]
    public void Checklist_EveryDocumentedBackendVariable_ExistsInTheCode()
    {
        var corpus = SourceCorpus();
        var configuration = Appsettings();

        var unknown = DocumentedPatterns()
            .Where(IsBackendConfigurationName)
            .Where(pattern => !ExistsInCode(pattern.Split("__"), configuration, corpus))
            .Distinct()
            .Order()
            .ToList();

        Assert.True(
            unknown.Count == 0,
            "docs/runbooks/deploy-checklist.md documents variables that no code reads (typo, renamed or removed setting): " +
            string.Join(", ", unknown) + ". Fix or delete the row; a setting that is only committed, not read, belongs in the " +
            "'inert' table between the not-checked markers.");
    }

    [Fact]
    public void Checklist_EveryEnvironmentVariableItNames_AppearsInTheSources()
    {
        var corpus = SourceCorpus();
        var checklist = Checklist();

        foreach (var name in EnvironmentVariablesInSources)
        {
            Assert.True(checklist.Contains($"`{name}`", StringComparison.Ordinal), $"{name} is not documented in the checklist.");
            Assert.True(corpus.Contains(name, StringComparison.Ordinal), $"{name} is documented but no code reads it.");
        }

        var dockerfile = File.ReadAllText(Path.Combine(SolutionRoot(), "Dockerfile"));
        Assert.Contains("ASPNETCORE_URLS", dockerfile, StringComparison.Ordinal);
        Assert.Contains("PORT", dockerfile, StringComparison.Ordinal);
        Assert.Contains("`ASPNETCORE_URLS`", checklist, StringComparison.Ordinal);
        Assert.Contains("`PORT`", checklist, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("railway.test.variables.example.json")]
    [InlineData("railway.prod.variables.example.json")]
    [InlineData("vercel.preview.variables.example.json")]
    [InlineData("vercel.production.variables.example.json")]
    public void Examples_EveryKey_IsDocumentedInTheChecklist(string fileName)
    {
        var keys = JsonKeys(Path.Combine(SolutionRoot(), "secrets", fileName));

        AssertDocumented(fileName, keys);
    }

    [Fact]
    public void Examples_VercelMatrix_EveryKeyIsDocumentedInTheChecklist()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(SolutionRoot(), "secrets", "vercel.variables.example.json")));
        var keys = document.RootElement.GetProperty("variables").EnumerateArray()
            .Select(entry => entry.GetProperty("key").GetString()!)
            .ToList();

        AssertDocumented("vercel.variables.example.json", keys);
    }

    [Fact]
    public void Examples_RailwayTestAndProduction_DefineTheSameVariables()
    {
        // The two environments differ in values, never in which variables exist (a Production-only variable is a surprise
        // at the first production deploy); the preview pattern of the Vercel project is test only.
        var test = JsonKeys(Path.Combine(SolutionRoot(), "secrets", "railway.test.variables.example.json"));
        var production = JsonKeys(Path.Combine(SolutionRoot(), "secrets", "railway.prod.variables.example.json"));

        Assert.Equal(
            test.Where(key => key != "Cors__VercelPreviewPattern").Order(),
            production.Order());
    }

    [Fact]
    public void Examples_RailwayTest_DeclaresEveryVariableTheRailwayEnvironmentRequires()
    {
        // Startup requirements of docs/runbooks/deploy-checklist.md § 2 ("S" rows): without them the container exits.
        string[] startupRequired =
        [
            "ASPNETCORE_ENVIRONMENT", "ConnectionStrings__DefaultConnection", "Hangfire__Schema", "Auth0__Domain", "Auth0__Audience",
            "Email__Provider", "Email__ApiKey", "Email__FromAddress", "App__PublicSiteBaseUrl", "Cors__AllowedOrigins",
            "Storage__Provider", "Storage__PublicBaseUrl", "Storage__S3__ServiceUrl", "Storage__S3__Region",
            "Storage__S3__AccessKeyId", "Storage__S3__SecretAccessKey", "Storage__S3__PublicBucket", "Storage__S3__PrivateBucket",
            "DataProtection__CertificatePfxBase64", "DataProtection__CertificatePassword",
            "App__ApiBaseUrl", "Stripe__SecretKey", "Stripe__PublishableKey", "Stripe__WebhookSecret", "Stripe__ConnectWebhookSecret",
            "Billing__Prices__Starter", "Billing__Prices__Pro", "Billing__Prices__Scale",
            "Auth0__ManagementClientId", "Auth0__ManagementClientSecret",
        ];

        foreach (var file in new[] { "railway.test.variables.example.json", "railway.prod.variables.example.json" })
        {
            var keys = JsonKeys(Path.Combine(SolutionRoot(), "secrets", file));
            Assert.Empty(startupRequired.Except(keys));
        }
    }

    [Fact]
    public void Checklist_EveryConfigurationSection_IsMentioned()
    {
        var checklist = Checklist();
        var sections = Appsettings().GetChildren().Select(child => child.Key)
            .Concat(SectionNamesOfOptionClasses())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var missing = sections
            .Where(section => !checklist.Contains($"{section}__", StringComparison.Ordinal)
                              && !checklist.Contains($"`{section}`", StringComparison.Ordinal))
            .Order()
            .ToList();

        Assert.True(
            missing.Count == 0,
            "These configuration sections are in appsettings.json or bound to an options class but not documented in " +
            "docs/runbooks/deploy-checklist.md (add a row to § 2 with the variable, whether it is required, and what happens " +
            "without it): " + string.Join(", ", missing));
    }

    [Fact]
    public void Checklist_NotCheckedMarkers_AreBalanced()
    {
        var raw = File.ReadAllText(ChecklistPath());

        Assert.Equal(
            Regex.Matches(raw, Regex.Escape(NotCheckedStart)).Count,
            Regex.Matches(raw, Regex.Escape(NotCheckedEnd)).Count);
    }

    private static void AssertDocumented(string fileName, IEnumerable<string> keys)
    {
        var patterns = DocumentedPatterns().Select(PatternToRegex).ToList();
        var undocumented = keys
            .Where(key => !patterns.Any(pattern => pattern.IsMatch(key)))
            .ToList();

        Assert.True(
            undocumented.Count == 0,
            $"secrets/{fileName} has keys that docs/runbooks/deploy-checklist.md does not document: " +
            string.Join(", ", undocumented) + ". Add the variable to the checklist or remove it from the example.");
    }

    // ---- checklist parsing -------------------------------------------------------------------------------------------------

    private static string ChecklistPath() => Path.Combine(SolutionRoot(), "docs", "runbooks", "deploy-checklist.md");

    /// <summary>The checklist without the blocks that deliberately name keys the code does not read.</summary>
    private static string Checklist()
    {
        var text = File.ReadAllText(ChecklistPath());
        return Regex.Replace(
            text,
            $"{Regex.Escape(NotCheckedStart)}.*?{Regex.Escape(NotCheckedEnd)}",
            string.Empty,
            RegexOptions.Singleline);
    }

    /// <summary>
    /// Every backticked token of the checklist, expanded: <c>{a,b}</c> becomes alternatives, <c>{Name}</c> and <c>*</c> become
    /// the wildcard <c>*</c>, and a fragment such as <c>__Pro</c> continues the previous token of the same line (it replaces
    /// its last segment, or everything after the section when the fragment has several segments).
    /// </summary>
    private static IReadOnlyList<string> DocumentedPatterns()
    {
        var patterns = new List<string>();
        foreach (var line in Checklist().Split('\n'))
        {
            string? previous = null;
            foreach (Match match in Regex.Matches(line, "`([^`]+)`"))
            {
                var token = match.Groups[1].Value.Trim();
                if (token.StartsWith("__", StringComparison.Ordinal) && previous is not null)
                    token = Continue(previous, token);

                if (!Regex.IsMatch(token, @"^[A-Za-z_{][A-Za-z0-9_{},*]*$"))
                    continue;

                if (token.Contains("__", StringComparison.Ordinal) && !token.StartsWith("__", StringComparison.Ordinal))
                    previous = token;

                patterns.AddRange(Expand(token));
            }
        }

        return patterns.Distinct().ToList();
    }

    private static string Continue(string previous, string fragment)
    {
        var segments = previous.Split("__");
        var extra = fragment.TrimStart('_').Split("__");
        var kept = extra.Length == 1 ? segments.Take(segments.Length - 1) : segments.Take(1);
        return string.Join("__", kept.Concat(extra));
    }

    private static IEnumerable<string> Expand(string token)
    {
        var open = token.IndexOf('{');
        if (open < 0)
            return [token];

        var close = token.IndexOf('}', open);
        var inner = token[(open + 1)..close];
        var alternatives = inner.Contains(',') ? inner.Split(',') : ["*"];
        return alternatives.SelectMany(alternative => Expand(token[..open] + alternative + token[(close + 1)..]));
    }

    private static bool IsBackendConfigurationName(string pattern) =>
        Regex.IsMatch(pattern, @"^[A-Za-z][A-Za-z0-9]*(__[A-Za-z0-9*]+)+$");

    private static Regex PatternToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", "[^_].*") + "$", RegexOptions.CultureInvariant);

    // ---- existence in the code ---------------------------------------------------------------------------------------------

    private static bool ExistsInCode(string[] segments, IConfiguration appsettings, string corpus) =>
        InAppsettings(appsettings, segments)
        || InOptionType(segments)
        || InSourceLiterals(segments, corpus)
        || RuntimeComposedRoots.ContainsKey(segments[0]);

    private static bool InAppsettings(IConfiguration appsettings, string[] segments)
    {
        var section = appsettings.GetSection(segments[0]);
        return section.Exists() && Walk(section, segments[1..]);
    }

    private static bool Walk(IConfigurationSection node, string[] rest)
    {
        if (rest.Length == 0)
            return true;

        var children = node.GetChildren().ToList();
        if (rest[0] == "*")
        {
            // A wildcard under a node without children is a free-form key (a dictionary): accept the rest.
            return children.Count == 0 || children.Any(child => Walk(child, rest[1..]));
        }

        var next = children.FirstOrDefault(child => string.Equals(child.Key, rest[0], StringComparison.OrdinalIgnoreCase));
        return next is not null && Walk(next, rest[1..]);
    }

    private static bool InOptionType(string[] segments)
    {
        if (!OptionTypes.TryGetValue(segments[0], out var type))
            return false;

        return Resolve(type, segments.Skip(1).ToArray());
    }

    private static bool Resolve(Type type, string[] rest)
    {
        if (rest.Length == 0)
            return true;

        // Dictionary<string, T> / List<T> / T[]: the next segment is a key or an index.
        if (TryGetElementType(type, out var element, out var keyed))
        {
            var isKeyOrIndex = keyed || rest[0] == "*" || int.TryParse(rest[0], out _);
            return isKeyOrIndex && Resolve(element, rest[1..]);
        }

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (rest[0] == "*")
            return properties.Any(property => Resolve(property.PropertyType, rest[1..]));

        var match = properties.FirstOrDefault(property => string.Equals(property.Name, rest[0], StringComparison.OrdinalIgnoreCase));
        return match is not null && Resolve(match.PropertyType, rest[1..]);
    }

    private static bool TryGetElementType(Type type, out Type element, out bool keyed)
    {
        element = type;
        keyed = false;
        if (type.IsArray)
        {
            element = type.GetElementType()!;
            return true;
        }

        if (!type.IsGenericType)
            return false;

        var definition = type.GetGenericTypeDefinition();
        if (definition == typeof(Dictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
        {
            element = type.GetGenericArguments()[1];
            keyed = true;
            return true;
        }

        if (definition == typeof(List<>) || definition == typeof(IList<>) || definition == typeof(IReadOnlyList<>) || definition == typeof(IEnumerable<>))
        {
            element = type.GetGenericArguments()[0];
            return true;
        }

        return false;
    }

    private static bool InSourceLiterals(string[] segments, string corpus)
    {
        var colon = string.Join(":", segments.Select(segment => segment == "*" ? "[^:\"\\s]*" : Regex.Escape(segment)));
        return Regex.IsMatch(corpus, colon, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? _corpus;

    /// <summary>All C# sources of the three application projects (no tests, no generated code): where literal keys are looked up.</summary>
    private static string SourceCorpus()
    {
        if (_corpus is not null)
            return _corpus;

        var root = SolutionRoot();
        var files = new[] { "Casazen.Core", "Casazen.Infrastructure", "Casazen.Web" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            .Where(path =>
            {
                var relative = path.Replace('\\', '/');
                return !relative.Contains("/obj/") && !relative.Contains("/bin/") && !relative.Contains("/Migrations/");
            });
        return _corpus = string.Join('\n', files.Select(File.ReadAllText));
    }

    private static IEnumerable<string> SectionNamesOfOptionClasses()
    {
        var assemblies = new[] { typeof(CinOptions).Assembly, typeof(StorageOptions).Assembly, typeof(Auth0Options).Assembly }.Distinct();
        foreach (var type in assemblies.SelectMany(assembly => assembly.GetTypes()))
        {
            var field = type.GetField("SectionName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
            if (field is { IsLiteral: true } && field.FieldType == typeof(string) && field.GetRawConstantValue() is string name)
            {
                // "Billing:Prices" is documented under its first segment.
                yield return name.Split(':')[0];
            }
        }
    }

    // ---- files -------------------------------------------------------------------------------------------------------------

    private static IReadOnlyList<string> JsonKeys(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject().Select(property => property.Name).Where(key => !key.StartsWith('_')).ToList();
    }

    private static IConfiguration Appsettings() =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(SolutionRoot(), "Casazen.Web", "appsettings.json"), optional: false)
            .Build();

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
