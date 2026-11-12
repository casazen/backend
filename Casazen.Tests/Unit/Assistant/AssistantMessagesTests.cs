using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Casazen.Tests.Unit.Assistant;

/// <summary>
/// E05/F05/US01 (the backend writes every text a user reads in Italian and in English, never inline): every message key the services and the controllers of the assistant raise exists in
/// <c>SharedResources.resx</c> and in its English twin, with a text of its own in each language and the same placeholders.
/// </summary>
public partial class AssistantMessagesTests
{
    /// <summary>The files that raise the keys: <c>DomainRuleException</c>, <c>DomainConflictException</c>, <c>NotFoundException.MessageKey</c> and <c>ApiProblem</c>.</summary>
    private static readonly string[] SourceFiles =
    [
        "Casazen.Core/Assistant/AssistantRulesValidator.cs",
        "Casazen.Core/Assistant/AssistantModeRules.cs",
        "Casazen.Infrastructure/Services/AssistantStore.cs",
        "Casazen.Infrastructure/Services/AssistantRulesService.cs",
        "Casazen.Infrastructure/Services/AssistantKnowledgeService.cs",
        "Casazen.Infrastructure/Services/AssistantSituationService.cs",
        "Casazen.Web/Controllers/AssistantKnowledgeController.cs",
        "Casazen.Web/Controllers/AssistantRulesController.cs",
        "Casazen.Web/Controllers/AssistantSituationsController.cs",
    ];

    [Fact]
    public void EveryMessageKeyOfTheAssistant_ExistsInBothLanguages_WithATextOfItsOwn()
    {
        var root = FindRepositoryRoot();
        var italian = Read(Path.Combine(root, "Casazen.Web", "Resources", "SharedResources.resx"));
        var english = Read(Path.Combine(root, "Casazen.Web", "Resources", "SharedResources.en.resx"));
        var keys = SourceFiles
            .SelectMany(file => KeysIn(File.ReadAllText(Path.Combine(root, file))))
            .Distinct(StringComparer.Ordinal)
            .Order()
            .ToList();

        // The scan is not hollow: it finds the keys the three services and the controllers raise.
        foreach (var expected in new[]
                 {
                     "AssistantRulesInvalid", "AssistantThresholdInvalid", "AssistantEntryInvalid", "AssistantEntryNotFound", "AssistantModeNotAllowed",
                     "AssistantTemplateInvalid", "AssistantSituationNotFound", "AssistantSituationInvalid", "AssistantConcurrentChange", "PropertyNotFound",
                     "NoOrganizationContext",
                 })
        {
            Assert.Contains(expected, keys);
        }

        var problems = new List<string>();
        foreach (var key in keys)
        {
            italian.TryGetValue(key, out var it);
            english.TryGetValue(key, out var en);
            if (string.IsNullOrWhiteSpace(it))
                problems.Add($"{key}: missing or empty in SharedResources.resx");
            if (string.IsNullOrWhiteSpace(en))
                problems.Add($"{key}: missing or empty in SharedResources.en.resx");
            if (!string.IsNullOrWhiteSpace(it) && string.Equals(it, en, StringComparison.Ordinal))
                problems.Add($"{key}: the Italian and the English text are the same");
            if (!string.IsNullOrWhiteSpace(it) && !string.IsNullOrWhiteSpace(en) && !Placeholders(it).SequenceEqual(Placeholders(en)))
                problems.Add($"{key}: the placeholders of the two languages differ");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void TheTemplateWithAnUnknownVariable_UsesTheMessageThatNamesTheVariable_AndAnswersWithTheCodeOfTheAssistant()
    {
        var root = FindRepositoryRoot();
        var service = File.ReadAllText(Path.Combine(root, "Casazen.Infrastructure", "Services", "AssistantSituationService.cs"));
        var italian = Read(Path.Combine(root, "Casazen.Web", "Resources", "SharedResources.resx"));
        var english = Read(Path.Combine(root, "Casazen.Web", "Resources", "SharedResources.en.resx"));

        // The code is the assistant's, the text is the one of the templates of the composer: it names the variable ({0}) and lists the valid ones.
        Assert.Contains("new DomainRuleException(TemplateInvalidCode, ex.MessageKey", service, StringComparison.Ordinal);
        Assert.Contains("{0}", italian["MessageTemplateVariableUnknown"], StringComparison.Ordinal);
        Assert.Contains("{0}", english["MessageTemplateVariableUnknown"], StringComparison.Ordinal);
    }

    private static IEnumerable<string> KeysIn(string source)
    {
        foreach (Match match in ExceptionKey().Matches(source))
            yield return match.Groups["key"].Value;
        foreach (Match match in MessageKeyMember().Matches(source))
            yield return match.Groups["key"].Value;
        foreach (Match match in ApiProblemKey().Matches(source))
            yield return match.Groups["key"].Value;
    }

    private static IEnumerable<string> Placeholders(string text) =>
        Placeholder().Matches(text).Select(match => match.Value).Distinct().Order();

    private static Dictionary<string, string> Read(string path) =>
        XDocument.Load(path).Root!
            .Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? string.Empty, StringComparer.Ordinal);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }

    // new DomainRuleException(SomeCode, "Key") of the exceptions of the domain, and the => new(SomeCode, "Key") helpers of the services.
    [GeneratedRegex(@"(?:DomainRuleException|DomainConflictException|DomainForbiddenException)\(\s*[^,()""]+,\s*""(?<key>[A-Za-z]+)""|=> new\(\s*[A-Za-z.]+Code,\s*""(?<key>[A-Za-z]+)""\)")]
    private static partial Regex ExceptionKey();

    // MessageKey = "Key"
    [GeneratedRegex(@"MessageKey\s*=\s*""(?<key>[A-Za-z]+)""")]
    private static partial Regex MessageKeyMember();

    // this.ApiProblem(StatusCodes.StatusNNN, "code", "Key"
    [GeneratedRegex(@"ApiProblem\(\s*StatusCodes\.[A-Za-z0-9]+,\s*""[a-z_]+"",\s*""(?<key>[A-Za-z]+)""")]
    private static partial Regex ApiProblemKey();

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex Placeholder();
}
