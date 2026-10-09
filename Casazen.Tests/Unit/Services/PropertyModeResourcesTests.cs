using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Xml.Linq;
using Casazen.Core.Services;
using Casazen.Web.Resources;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// PM-02: every message of the scheduled change of rental mode is a key of <c>SharedResources</c>, with a real text in both
/// languages, and the messages that carry a day show it in the language of the request.
/// </summary>
public class PropertyModeResourcesTests
{
    private static readonly string[] ValidationKeys = ["PropertyModeTargetRequired", "PropertyModeDateRequired"];

    private static IEnumerable<string> ErrorMessageKeys() =>
        typeof(PropertyModeErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true } && f.Name.EndsWith("MessageKey", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void ErrorMessageKeys_AreTheOnesOfTheTaskAndMore()
    {
        var keys = ErrorMessageKeys().ToList();

        // The four keys the task names, plus the rest of the cases of the rules.
        Assert.Contains("PropertyModeBlockedByLease", keys);
        Assert.Contains("PropertyModeBlockedByBookings", keys);
        Assert.Contains("PropertyModeDateTooEarly", keys);
        Assert.Contains("PropertyModeChangeExists", keys);
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Theory]
    [InlineData("SharedResources.resx")]
    [InlineData("SharedResources.en.resx")]
    public void ResourceFiles_EveryKeyOfTheChange_IsThereWithReadableText(string file)
    {
        var texts = XDocument.Load(Path.Combine(FindRepositoryRoot(), "Casazen.Web", "Resources", file)).Root!
            .Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);

        var keys = ErrorMessageKeys().Concat(ValidationKeys).Append(ManualBlockErrorCodes.HeldByModeChangeMessageKey).Distinct();
        foreach (var key in keys)
        {
            Assert.True(texts.TryGetValue(key, out var text), $"{key} is missing from {file}");
            Assert.True(text!.Length > 15 && text != key, $"{key} has no real text in {file}");
        }
    }

    [Fact]
    public void ResourceFiles_ItalianAndEnglishDiffer_ForEveryKeyOfTheChange()
    {
        var italian = Load("SharedResources.resx");
        var english = Load("SharedResources.en.resx");

        foreach (var key in ErrorMessageKeys().Concat(ValidationKeys).Append(ManualBlockErrorCodes.HeldByModeChangeMessageKey))
            Assert.NotEqual(italian[key], english[key]);
    }

    [Theory]
    [InlineData("it-IT", "1 dicembre 2026")]
    [InlineData("it", "1 dicembre 2026")]
    [InlineData("en", "1 December 2026")]
    [InlineData("en-US", "1 December 2026")]
    public void MessagesWithADay_ShowItInTheLanguageOfTheRequest(string culture, string expectedDay)
    {
        var manager = new ResourceManager(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly);
        var info = CultureInfo.GetCultureInfo(culture);
        var day = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);

        foreach (var key in new[]
                 {
                     PropertyModeErrorCodes.DateTooEarlyMessageKey,
                     PropertyModeErrorCodes.DateTooFarMessageKey,
                     PropertyModeErrorCodes.BlockedByBookingsMessageKey,
                     PropertyModeErrorCodes.BlockedByLeaseMessageKey,
                 })
        {
            var text = string.Format(info, manager.GetString(key, info)!, day);

            Assert.Contains(expectedDay, text);
            Assert.DoesNotContain("{0", text);
        }
    }

    [Fact]
    public void MessagesWithoutADay_AreFormattedWithoutArguments()
    {
        var manager = new ResourceManager(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly);
        foreach (var culture in new[] { "it-IT", "en" })
        {
            var info = CultureInfo.GetCultureInfo(culture);
            foreach (var key in new[]
                     {
                         PropertyModeErrorCodes.AlreadyInModeMessageKey,
                         PropertyModeErrorCodes.BlockedByDraftLeaseMessageKey,
                         PropertyModeErrorCodes.ChangeExistsMessageKey,
                         PropertyModeErrorCodes.ChangeNotFoundMessageKey,
                         PropertyModeErrorCodes.ChangeNotScheduledMessageKey,
                         PropertyModeErrorCodes.TargetInvalidMessageKey,
                         ManualBlockErrorCodes.HeldByModeChangeMessageKey,
                     })
            {
                Assert.DoesNotContain("{", string.Format(info, manager.GetString(key, info)!));
            }
        }
    }

    private static Dictionary<string, string> Load(string file) =>
        XDocument.Load(Path.Combine(FindRepositoryRoot(), "Casazen.Web", "Resources", file)).Root!
            .Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string)e.Element("value")!);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
