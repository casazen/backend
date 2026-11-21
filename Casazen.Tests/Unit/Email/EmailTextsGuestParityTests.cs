using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Casazen.Core.Entities.Enums;
using Casazen.Infrastructure.Email;
using Casazen.Infrastructure.Email.Templates;
using Xunit;

namespace Casazen.Tests.Unit.Email;

/// <summary>
/// E50/F01/US03/T02 (NF-155, AC-NF155-3): every e-mail to a guest exists in Italian, English, German, French and Spanish, with the same keys in the five files. The guest e-mails are the
/// ones the keys of <see cref="GuestKeyPrefixes"/> belong to; the host, staff, supplier and push texts stay in Italian and English (the other languages fall back to the Italian
/// resource and are never sent to a guest). A key that is missing from a file is not an error at run time (the resource manager answers with the Italian text), so without these tests a
/// guest could receive an e-mail that is half in German and half in Italian.
/// </summary>
public class EmailTextsGuestParityTests
{
    private static readonly string[] Files =
    [
        "EmailTexts.resx",
        "EmailTexts.en.resx",
        "EmailTexts.de.resx",
        "EmailTexts.fr.resx",
        "EmailTexts.es.resx",
    ];

    /// <summary>
    /// The prefixes of the keys of the guest e-mails: the layout of a guest e-mail, the keys the guest templates share (<c>Guest_</c>, <c>Booking_</c>) and the ones of the confirmation, the check-in link and its reminder,
    /// the check-in message of the chat, the video call of the verification, the payment link, the cancellation link, the review invitation, the answer of the host, the document of
    /// the stay, the e-mail that sends the stay link again and the one that lets a guest find its link. A new guest template adds its prefix here, and its texts to the five files.
    /// </summary>
    public static readonly string[] GuestKeyPrefixes =
    [
        "Layout_PoweredBy",
        "Guest_",
        "Booking_",
        "GuestCheckInLink_",
        "GuestCheckInReminder_",
        "GuestCheckInChat_",
        "GuestVideoCall_",
        "GuestBookingConfirmed_",
        "BookingPaymentLink_",
        "GuestCancelLink_",
        "ReviewInvite_",
        "HostReplyToGuest_",
        "FiscalDocumentDelivery_",
        "GuestPortalLinkRequested_",
    ];

    /// <summary>
    /// PROOF TEST (what a guest reads in its own language): for every prefix of a guest e-mail the set of keys is the same in the five files, and a key that is missing fails the test
    /// with its name and the file it is missing from.
    /// </summary>
    [Fact]
    public void GuestTemplates_HaveSameKeysInAllFiveLanguages()
    {
        var missing = new List<string>();
        foreach (var prefix in GuestKeyPrefixes)
        {
            var italian = GuestKeys("EmailTexts.resx", prefix);
            foreach (var file in Files.Skip(1))
            {
                var keys = GuestKeys(file, prefix);
                missing.AddRange(italian.Except(keys).Select(key => $"{file} lacks {key}"));
                missing.AddRange(keys.Except(italian).Select(key => $"{file} has {key}, which the Italian file does not"));
            }
        }

        Assert.True(missing.Count == 0, string.Join("; ", missing));

        // The guest e-mails are there at all (a prefix that matches nothing would pass for ever).
        Assert.True(GuestKeys("EmailTexts.resx", "GuestBookingConfirmed_").Count >= 10);
        Assert.True(GuestKeys("EmailTexts.resx", "Booking_").Count >= 10);
        Assert.Contains("FiscalDocumentDelivery_Kind_CreditNote", GuestKeys("EmailTexts.resx", "FiscalDocumentDelivery_"));
    }

    /// <summary>A translation is a translation: no German, French or Spanish guest text is the Italian one (a text pasted in the wrong file), but the brand line.</summary>
    [Theory]
    [InlineData("EmailTexts.de.resx")]
    [InlineData("EmailTexts.fr.resx")]
    [InlineData("EmailTexts.es.resx")]
    public void GuestTexts_AreTranslated_NotCopiedFromItalian(string fileName)
    {
        var italian = Values("EmailTexts.resx");
        var own = Values(fileName);

        var same = own
            .Where(pair => pair.Key != "Layout_PoweredBy" && italian.TryGetValue(pair.Key, out var text) && text == pair.Value)
            .Select(pair => pair.Key)
            .ToList();

        Assert.True(same.Count == 0, $"{fileName} holds the Italian text of {string.Join(", ", same)}");
    }

    /// <summary>
    /// No text of a guest e-mail names a technical supplier (A16: never Stripe, Octorate, the AI provider). The German, French and Spanish texts and the texts of this slice are checked
    /// in full; the <c>BookingPaymentLink_Secure</c> text of the Italian and English files, written before the portal, still names the payment provider (it is left as it is: with the portal off
    /// that e-mail must stay byte for byte what it was).
    /// </summary>
    [Fact]
    public void GuestTexts_NameNoTechnicalSupplier()
    {
        string[] banned = ["stripe", "octorate", "deepseek", "openai", "anthropic", "chatgpt", "claude", "gemini"];
        var found = new List<string>();
        foreach (var file in Files)
        {
            var legacy = file is "EmailTexts.resx" or "EmailTexts.en.resx";
            foreach (var (key, value) in Values(file))
            {
                if (!IsGuestKey(key) || (legacy && key == "BookingPaymentLink_Secure"))
                    continue;

                var lower = value.ToLowerInvariant();
                found.AddRange(banned.Where(lower.Contains).Select(name => $"{file}:{key} names {name}"));
            }
        }

        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    /// <summary>
    /// PROOF TEST (AC1: no missing key, no unfilled placeholder): every template of a guest renders, subject and body, in each of the five languages with every one of its branches, and
    /// leaves no <c>{0}</c>, no empty text and no key name behind.
    /// </summary>
    [Theory]
    [InlineData("it")]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("fr")]
    [InlineData("es")]
    public void EveryGuestTemplate_RendersInTheLanguage_WithNoUnfilledPlaceholder(string language)
    {
        var culture = language == "it" ? CultureInfo.GetCultureInfo("it-IT") : CultureInfo.GetCultureInfo(language);
        var checkIn = new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc);
        var expires = new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);
        var summary = new BookingEmailSummary("ABC123", "Villa Rosa", checkIn, checkIn.AddDays(3), 2, 300m, 50m, 12m, 362m, "EUR");
        var host = new BookingHostContact("Casa Aurora", "host@aurora.test");
        var branding = new GuestEmailBranding("Casa Aurora", "https://cdn.casazen.test/logo.png", "#336699", "host@aurora.test");
        const string url = "https://app.casazen.test/x";

        var rendered = new List<(string Name, EmailContent Content)>();
        foreach (var kind in Enum.GetValues<BookingConfirmationKind>())
        {
            foreach (var notice in new BookingCheckInNotice?[]
                     {
                         null,
                         BookingCheckInNotice.WithLink(url, expires),
                         BookingCheckInNotice.OnDay(checkIn),
                         BookingCheckInNotice.RequestOnly,
                     })
            {
                rendered.Add(($"confirmed-{kind}", EmailTemplates.GuestBookingConfirmed(culture, "Anna", summary, kind, 362m, checkIn, host, url, notice, branding)));
            }

            rendered.Add(($"confirmed-{kind}-no-date", EmailTemplates.GuestBookingConfirmed(culture, "Anna", summary, kind, 0m, null, BookingHostContact.None, url)));
        }

        rendered.Add(("confirmed-late", EmailTemplates.GuestBookingConfirmed(culture, "Anna", summary, BookingConfirmationKind.PaidOnlineLate, 362m, null, new BookingHostContact(null, "host@aurora.test"), url)));
        rendered.Add(("checkin-link", EmailTemplates.GuestCheckInLink(culture, "Anna", "Villa Rosa", checkIn, url, expires, branding)));
        rendered.Add(("checkin-reminder", EmailTemplates.GuestCheckInReminder(culture, "Anna", "Villa Rosa", checkIn, url, expires, branding)));
        rendered.Add(("payment-link", EmailTemplates.BookingPaymentLink(culture, "Anna", "Villa Rosa", checkIn, checkIn.AddDays(3), 120.5m, "EUR", url, branding)));
        rendered.Add(("cancel-link", EmailTemplates.GuestCancelLink(culture, "Anna", "Villa Rosa", checkIn, checkIn.AddDays(3), url, expires, branding)));
        rendered.Add(("review-invite", EmailTemplates.ReviewInvite(culture, "Anna", "Villa Rosa", checkIn, checkIn.AddDays(3), url, expires, branding)));
        rendered.Add(("reply", EmailTemplates.HostReplyToGuest(culture, "Anna", "Casa Aurora", "Villa Rosa", url, branding)));
        rendered.Add(("reply-no-link", EmailTemplates.HostReplyToGuest(culture, "Anna", "Casa Aurora", "Villa Rosa", null)));
        rendered.Add(("video-call", EmailTemplates.GuestVideoCall(culture, "Anna", "Villa Rosa", checkIn, url, branding)));
        foreach (var kind in Enum.GetValues<FiscalDocumentKind>())
        {
            rendered.Add(($"document-{kind}", EmailTemplates.FiscalDocumentDelivery(culture, "Anna", "Casa Aurora", kind, "2026/7", checkIn, url, expires, branding)));
            rendered.Add(($"document-{kind}-nameless", EmailTemplates.FiscalDocumentDelivery(culture, " ", "Casa Aurora", kind, "2026/7", checkIn, url, expires)));
        }

        var problems = new List<string>();
        foreach (var (name, content) in rendered)
        {
            if (string.IsNullOrWhiteSpace(content.Subject))
                problems.Add($"{name}: empty subject");

            foreach (var text in new[] { content.Subject, content.HtmlBody })
            {
                if (Regex.IsMatch(text, @"\{\d+\}"))
                    problems.Add($"{name}: an unfilled placeholder");
                if (Regex.IsMatch(text, @"\b[A-Z][A-Za-z]+_[A-Z][A-Za-z_]+\b"))
                    problems.Add($"{name}: the name of a key in the text");
            }

            if (!content.HtmlBody.Contains($"<html lang=\"{language}\">", StringComparison.Ordinal))
                problems.Add($"{name}: not written in {language}");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems.Distinct()));
    }

    /// <summary>The message of the chat with the check-in link exists in the five languages too.</summary>
    [Theory]
    [InlineData("it", "Buongiorno")]
    [InlineData("en", "Hello")]
    [InlineData("de", "Guten Tag")]
    [InlineData("fr", "Bonjour")]
    [InlineData("es", "Hola")]
    public void CheckInChatMessage_ExistsInTheLanguage(string language, string greeting)
    {
        var culture = language == "it" ? CultureInfo.GetCultureInfo("it-IT") : CultureInfo.GetCultureInfo(language);

        var text = EmailTemplates.GuestCheckInChatMessage(
            culture, "Trullo", new DateTime(2026, 10, 10, 0, 0, 0, DateTimeKind.Utc), "https://app.casazen.test/checkin/abc", new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc));

        Assert.Contains(greeting, text, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\{\d+\}", text);
    }

    private static bool IsGuestKey(string key) => GuestKeyPrefixes.Any(prefix => key.StartsWith(prefix, StringComparison.Ordinal));

    private static List<string> GuestKeys(string fileName, string prefix) =>
        AllKeys(fileName).Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();

    private static List<string> AllKeys(string fileName) =>
        Load(fileName).Root!.Elements("data").Select(e => (string)e.Attribute("name")!).ToList();

    private static Dictionary<string, string> Values(string fileName) =>
        Load(fileName).Root!.Elements("data").ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? string.Empty);

    private static XDocument Load(string fileName) =>
        XDocument.Load(Path.Combine(SolutionRoot(), "Casazen.Infrastructure", "Email", "Templates", fileName));

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
