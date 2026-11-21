using System.Xml.Linq;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// The texts of the emails exist in both languages and once: <c>EmailTexts.resx</c> (Italian, the default) and <c>EmailTexts.en.resx</c>
/// have the same keys, none repeated; German, French and Spanish hold exactly the emails to a guest (NF-40, NF-155). A missing English key is not an error at run time (the resource manager falls back to the Italian
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
    /// NF-40 (AC-NF40-4), widened by E50/F01/US03/T02 (NF-155): German, French and Spanish hold exactly the texts of the e-mails to a guest, every one of them, and nothing else: the
    /// check-in e-mails (the link and the reminder; since E23/F01/US01/T01 (NF-122) the one message of the portal chat that carries the check-in link, <c>GuestCheckInChat_Body</c>; and
    /// since E22/F01/US01/T03 the proposal of a video call), and, since T02, the confirmation of a booking, the payment link, the cancellation link, the review invitation, the answer of the
    /// host and the document of the stay (<see cref="EmailTextsGuestParityTests.GuestKeyPrefixes"/>). The other e-mails (host, staff, suppliers) are never sent to a guest in these
    /// languages, and a German text of one of them would be a half-translated e-mail.
    /// </summary>
    [Theory]
    [InlineData("EmailTexts.de.resx")]
    [InlineData("EmailTexts.fr.resx")]
    [InlineData("EmailTexts.es.resx")]
    public void GuestLanguages_HoldExactlyTheGuestKeys(string fileName)
    {
        var guestKeys = Keys("EmailTexts.resx")
            .Where(k => EmailTextsGuestParityTests.GuestKeyPrefixes.Any(prefix => k.StartsWith(prefix, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(guestKeys);
        Assert.Contains("GuestCheckInReminder_Subject", guestKeys);
        Assert.Contains("GuestCheckInChat_Body", guestKeys);
        Assert.Contains("GuestVideoCall_Subject", guestKeys);
        Assert.Contains("GuestBookingConfirmed_Subject", guestKeys);
        Assert.Contains("FiscalDocumentDelivery_Subject", guestKeys);
        Assert.Equal(guestKeys, Keys(fileName).Order(StringComparer.Ordinal).ToList());
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
