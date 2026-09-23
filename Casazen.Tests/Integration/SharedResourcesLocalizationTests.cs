using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Casazen.Web.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace Casazen.Tests.Integration;

/// <summary>
/// Guards the backend localization setup (R-07, A9-08): the resources must be found with the application's own
/// localization configuration, every key must exist in Italian and English, and no lookup may fall back to the raw key.
/// </summary>
public class SharedResourcesLocalizationTests : IClassFixture<CasazenWebApplicationFactory>
{
    private static readonly Regex[] MessageKeyPatterns =
    [
        new(@"new\s+Domain(?:Rule|Conflict)Exception\(\s*""[^""]*""\s*,\s*""(?<key>[^""]+)""", RegexOptions.Compiled),
        new(@"MessageKey\s*=\s*""(?<key>[^""]+)""", RegexOptions.Compiled),
        new(@"ApiProblem\(\s*[^,()]+,\s*[^,()]+,\s*""(?<key>[^""]+)""", RegexOptions.Compiled),
        new(@"[Ll]ocalizer\[\s*""(?<key>[^""]+)""", RegexOptions.Compiled),
    ];

    private readonly CasazenWebApplicationFactory _factory;

    public SharedResourcesLocalizationTests(CasazenWebApplicationFactory factory) => _factory = factory;

    public static TheoryData<string> Cultures => new() { "it-IT", "it", "en-US", "en" };

    [Theory]
    [MemberData(nameof(Cultures))]
    public void Localizer_EveryResourceKey_ReturnsValueDifferentFromKey(string culture)
    {
        var localizer = _factory.Services.GetRequiredService<IStringLocalizer<SharedResources>>();
        var keys = ReadKeys(CultureInfo.InvariantCulture);
        Assert.NotEmpty(keys);

        var unresolved = WithUiCulture(culture, () => keys
            .Where(key =>
            {
                var value = localizer[key];
                return value.ResourceNotFound || string.IsNullOrWhiteSpace(value.Value) || value.Value == key;
            })
            .ToList());

        Assert.True(unresolved.Count == 0, $"Keys returned as raw key in {culture}: {string.Join(", ", unresolved)}");
    }

    [Fact]
    public void Localizer_EnglishAcceptLanguageCulture_ReturnsEnglishText()
    {
        var localizer = _factory.Services.GetRequiredService<IStringLocalizer<SharedResources>>();

        var italian = WithUiCulture("it-IT", () => localizer["UnauthorizedDetail"].Value);
        var english = WithUiCulture("en", () => localizer["UnauthorizedDetail"].Value);

        Assert.StartsWith("È richiesta l'autenticazione", italian);
        Assert.StartsWith("Authentication is required", english);
    }

    [Fact]
    public void Resources_EnglishFile_HasSameKeysAsItalianAndTranslatedValues()
    {
        var italian = ReadEntries(CultureInfo.InvariantCulture);
        var english = ReadEntries(new CultureInfo("en"));

        Assert.Empty(italian.Keys.Except(english.Keys));
        Assert.Empty(english.Keys.Except(italian.Keys));
        var untranslated = italian.Where(entry => english[entry.Key] == entry.Value).Select(entry => entry.Key).ToList();
        Assert.True(untranslated.Count == 0, $"Same text in Italian and English: {string.Join(", ", untranslated)}");
    }

    [Fact]
    public void SourceCode_EveryLiteralMessageKey_ExistsInResources()
    {
        var keys = ReadKeys(CultureInfo.InvariantCulture);
        var root = FindRepositoryRoot();
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var project in new[] { "Casazen.Core", "Casazen.Infrastructure", "Casazen.Web" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                    continue;

                var source = File.ReadAllText(file);
                foreach (var pattern in MessageKeyPatterns)
                {
                    foreach (Match match in pattern.Matches(source))
                        used.Add(match.Groups["key"].Value);
                }
            }
        }

        Assert.NotEmpty(used);
        var missing = used.Where(key => !keys.Contains(key)).OrderBy(key => key).ToList();
        Assert.True(missing.Count == 0, $"Message keys missing from SharedResources.resx: {string.Join(", ", missing)}");
    }

    private static HashSet<string> ReadKeys(CultureInfo culture) => ReadEntries(culture).Keys.ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, string> ReadEntries(CultureInfo culture)
    {
        var manager = new ResourceManager(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly);
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
        Assert.NotNull(set);
        return set
            .Cast<DictionaryEntry>()
            .ToDictionary(entry => (string)entry.Key, entry => entry.Value as string ?? string.Empty, StringComparer.Ordinal);
    }

    private static T WithUiCulture<T>(string culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            return action();
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
