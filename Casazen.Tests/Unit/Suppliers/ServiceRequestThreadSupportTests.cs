using System.Collections;
using System.Globalization;
using System.Resources;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Casazen.Web.Resources;
using Xunit;

namespace Casazen.Tests.Unit.Suppliers;

/// <summary>SP-10b: the codes and the texts (Italian and English) of the conversation of a request on measure, and its pure rules.</summary>
public class ServiceRequestThreadSupportTests
{
    [Fact]
    public void MessageKeys_EveryKeyOfTheConversation_ExistsInItalianAndEnglishWithDifferentText()
    {
        var italian = ReadEntries(CultureInfo.InvariantCulture);
        var english = ReadEntries(new CultureInfo("en"));

        Assert.Equal(ServiceRequestThreadErrors.MessageKeys.Count, ServiceRequestThreadErrors.MessageKeys.Distinct().Count());
        Assert.All(ServiceRequestThreadErrors.MessageKeys, key =>
        {
            Assert.True(italian.ContainsKey(key), $"{key} is missing from SharedResources.resx");
            Assert.True(english.ContainsKey(key), $"{key} is missing from SharedResources.en.resx");
            Assert.False(string.IsNullOrWhiteSpace(italian[key]), key);
            Assert.False(string.IsNullOrWhiteSpace(english[key]), key);
            Assert.NotEqual(italian[key], english[key]);
        });
    }

    [Theory]
    [InlineData(ServiceRequestStatus.Richiesto, true)]
    [InlineData(ServiceRequestStatus.PresoInCarico, true)]
    [InlineData(ServiceRequestStatus.InCorso, true)]
    [InlineData(ServiceRequestStatus.Completato, false)]
    [InlineData(ServiceRequestStatus.Pagato, false)]
    [InlineData(ServiceRequestStatus.Rifiutato, false)]
    [InlineData(ServiceRequestStatus.Annullato, false)]
    public void IsOpen_AConversationGoesOnWhileTheRequestIsNewTakenOrInProgress(ServiceRequestStatus status, bool open)
    {
        Assert.Equal(open, ServiceRequestThreadRules.IsOpen(status));
    }

    [Fact]
    public void ShouldNotify_TheFirstMessageAlways_TheNextOnesOnlyAfterTheQuietPeriod()
    {
        var now = new DateTime(2026, 10, 30, 9, 0, 0, DateTimeKind.Utc);

        Assert.True(ServiceRequestThreadRules.ShouldNotify(null, now));
        Assert.False(ServiceRequestThreadRules.ShouldNotify(now.AddMinutes(-1), now));
        Assert.False(ServiceRequestThreadRules.ShouldNotify(now.AddMinutes(-9), now));
        Assert.True(ServiceRequestThreadRules.ShouldNotify(now.AddMinutes(-10), now));
        Assert.True(ServiceRequestThreadRules.ShouldNotify(now.AddHours(-3), now));
    }

    private static Dictionary<string, string> ReadEntries(CultureInfo culture)
    {
        var manager = new ResourceManager(typeof(SharedResources).FullName!, typeof(SharedResources).Assembly);
        var set = manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
                  ?? throw new InvalidOperationException($"No SharedResources for {culture.Name}");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }
}
