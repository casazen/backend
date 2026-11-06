using System.Xml.Linq;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// The texts of the emails exist in both languages and once: <c>EmailTexts.resx</c> (Italian, the default) and <c>EmailTexts.en.resx</c>
/// have the same keys, none repeated; German, French and Spanish hold exactly the guest check-in emails (NF-40). A missing English key is not an error at run time (the resource manager falls back to the Italian
/// text) and a repeated key is only a warning of the build, so without this test an English email could go out in Italian, or a merge
/// of two branches that add the same key could pass unnoticed.
/// </summary>
public class EmailTextsParityTests
{
    private static List<string> Keys(string fileName)
    {
        var path = Path.Combine(SolutionRoot(), "Casazen.Infrastructure", "Email", "Templates", fileName);
        return XDocument.Load(path).Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToList();
    }

    [Fact]
    public void ItalianAndEnglish_HaveTheSameKeys()
    {
        var italian = Keys("EmailTexts.resx");
        var english = Keys("EmailTexts.en.resx");

        Assert.Empty(italian.Except(english));
        Assert.Empty(english.Except(italian));
    }

    /// <summary>
    /// NF-40 (AC-NF40-4): German, French and Spanish hold exactly the texts of the two guest check-in emails (the link and the reminder), every
    /// one of them, and nothing else: the other emails are never sent to a guest in these languages, and a German text of another email would
    /// be a half-translated email.
    /// </summary>
    [Theory]
    [InlineData("EmailTexts.de.resx")]
    [InlineData("EmailTexts.fr.resx")]
    [InlineData("EmailTexts.es.resx")]
    public void GuestLanguages_HoldExactlyTheGuestCheckInKeys(string fileName)
    {
        var guestCheckInKeys = Keys("EmailTexts.resx")
            .Where(k => k.StartsWith("GuestCheckInLink_", StringComparison.Ordinal)
                        || k.StartsWith("GuestCheckInReminder_", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(guestCheckInKeys);
        Assert.Contains("GuestCheckInReminder_Subject", guestCheckInKeys);
        Assert.Equal(guestCheckInKeys, Keys(fileName).Order(StringComparer.Ordinal).ToList());
    }

    [Theory]
    [InlineData("EmailTexts.resx")]
    [InlineData("EmailTexts.en.resx")]
    [InlineData("EmailTexts.de.resx")]
    [InlineData("EmailTexts.fr.resx")]
    [InlineData("EmailTexts.es.resx")]
    public void NoKeyIsRepeated(string fileName)
    {
        var keys = Keys(fileName);

        Assert.Empty(keys.GroupBy(k => k, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    /// <summary>
    /// Covers AC-NF28-1, AC-NF28-2, AC-NF28-3, AC-NF28-6: Backend email localization completeness audit.
    /// Verifies that all EmailTexts keys exist in both Italian (default) and English for email templates.
    /// This test provides traceability for the localization audit requirement (E14/F02/US01/T01).
    /// </summary>
    [Fact]
    public void BackendLocalizationAudit_AllEmailTextsKeysExistInBothLanguages()
    {
        var italian = Keys("EmailTexts.resx");
        var english = Keys("EmailTexts.en.resx");

        Assert.NotEmpty(italian);
        Assert.NotEmpty(english);
        Assert.Equal(italian.Count, english.Count);

        var missingInEnglish = italian.Except(english).ToList();
        var missingInItalian = english.Except(italian).ToList();

        Assert.Empty(missingInEnglish);
        Assert.Empty(missingInItalian);
    }

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
