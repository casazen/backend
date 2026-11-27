using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Casazen.Core.Entities;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

/// <summary>
/// What the assistant does with each kind of item, as pure rules (no database, no clock of its own): the plan of the service only
/// gathers the facts and asks these. Every answer is a status and a reason (<see cref="AutopilotReasons"/>); what is not here the
/// assistant does not do.
/// </summary>
public static class AutopilotDecisions
{
    /// <summary>
    /// The data of the guests of a stay are still to complete. The assistant asks them when it may, nobody has answered, it has not asked
    /// too often (<see cref="AutopilotLimits.MaxRequestsPerStay"/>) or too lately (<see cref="AutopilotLimits.RequestCooldown"/>), and the
    /// guest can be reached; otherwise it says which of these is the reason.
    /// </summary>
    /// <param name="canWrite">The caller may write on the booking of the stay.</param>
    /// <param name="requests">Links already issued for the stay, by anyone (the host, the daily job, an earlier run).</param>
    /// <param name="lastRequestAt">When the last one was issued (UTC).</param>
    /// <param name="completed">The guest has already submitted the form: asking again changes nothing, the host corrects what is left.</param>
    /// <param name="emailConfigured">CasaZen can send e-mail.</param>
    public static (AutopilotItemStatus Status, string Reason, IReadOnlyList<string> Channels) GuestData(
        bool canWrite,
        int requests,
        DateTime? lastRequestAt,
        bool completed,
        Guest guest,
        bool emailConfigured,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(guest);

        if (!canWrite)
            return (AutopilotItemStatus.Host, AutopilotReasons.NoPermission, []);
        if (completed)
            return (AutopilotItemStatus.Host, AutopilotReasons.AlreadySubmitted, []);
        if (requests >= AutopilotLimits.MaxRequestsPerStay)
            return (AutopilotItemStatus.Host, AutopilotReasons.AskedEnough, []);

        var channels = ChannelsOf(guest, emailConfigured);
        if (channels.Count == 0)
            return (AutopilotItemStatus.Host, AutopilotReasons.NoContact, []);
        if (lastRequestAt is { } last && nowUtc - last < AutopilotLimits.RequestCooldown)
            return (AutopilotItemStatus.Waiting, AutopilotReasons.AskedRecently, []);

        return (AutopilotItemStatus.Ready, AutopilotReasons.AskGuests, channels);
    }

    /// <summary>The ways to reach the guest of a booking: the e-mail when CasaZen can send it and the address is one, the chat when the number can be written in international form.</summary>
    public static IReadOnlyList<string> ChannelsOf(Guest guest, bool emailConfigured)
    {
        ArgumentNullException.ThrowIfNull(guest);

        var channels = new List<string>(2);
        if (emailConfigured && AutopilotContacts.IsUsableEmail(guest.Email))
            channels.Add(AutopilotChannels.Email);
        if (AutopilotContacts.InternationalPhone(guest.PhoneNumber, guest.Country) is not null)
            channels.Add(AutopilotChannels.Chat);
        return channels;
    }

    /// <summary>
    /// A departure of the cockpit. The assistant closes only the stay it is sure is over: checked in (a confirmed one needs the host to say
    /// that the guest arrived) and whose last day is before today (today's guest may still be in the house). A stay whose guests are being
    /// asked for their data waits: the link of the guest works only while the stay is open, so closing it now would cut the answer.
    /// </summary>
    /// <param name="guestData">The status of the item of the same stay about the data of the guests, when it has one.</param>
    public static (AutopilotItemStatus Status, string Reason) Departure(
        bool canWrite,
        BookingStatus status,
        DateTime checkOutDate,
        DateTime todayInRome,
        AutopilotItemStatus? guestData)
    {
        if (!canWrite)
            return (AutopilotItemStatus.Host, AutopilotReasons.NoPermission);
        if (status != BookingStatus.CheckedIn)
            return (AutopilotItemStatus.Host, AutopilotReasons.ArrivalNotRegistered);
        if (checkOutDate.Date >= todayInRome.Date)
            return (AutopilotItemStatus.Host, AutopilotReasons.DepartureToday);
        if (guestData is AutopilotItemStatus.Ready or AutopilotItemStatus.Waiting)
            return (AutopilotItemStatus.Waiting, AutopilotReasons.AwaitingGuestData);

        return (AutopilotItemStatus.Ready, AutopilotReasons.CloseDeparture);
    }

    /// <summary>
    /// An Alloggiati communication whose guests the assistant is not asking: only the host can send it, the assistant says whether the
    /// data are ready for the portal and, when they are not, what stands in the way. It never fills a field, a code, a place.
    /// </summary>
    public static string Alloggiati(AlloggiatiGuestSummaryInfo summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        if (!summary.DataComplete)
            return AutopilotReasons.DataMissing;
        if (summary.StayDays is < 1 or > AlloggiatiTerms.MaxStayDaysPerSchedina)
            return AutopilotReasons.StayDaysInvalid;

        return summary.ExportReady ? AutopilotReasons.ReadyToUpload : AutopilotReasons.CodesToComplete;
    }

    /// <summary>The number of the message to a guest asked again: the first one, then the two reminders.</summary>
    public static int ReminderOf(int requestsAlready) => Math.Clamp(requestsAlready + 1, 1, AutopilotLimits.MaxRequestsPerStay);
}

/// <summary>
/// How much the assistant may do in one go, and how often it may ask the same guest. Fixed numbers of the rules, not settings: a limit
/// that can be raised from outside is a limit nobody trusts.
/// </summary>
public static class AutopilotLimits
{
    /// <summary>
    /// The most times the assistant (or the daily job, or the host) asks the guests of one stay for their data: the first message and two
    /// reminders. After that the stay is the host's to chase.
    /// </summary>
    public const int MaxRequestsPerStay = 3;

    /// <summary>The same guest is never asked twice in this time, whatever the number of runs.</summary>
    public static readonly TimeSpan RequestCooldown = TimeSpan.FromHours(24);

    /// <summary>Guests asked in one run (every one is an e-mail from CasaZen in the host's name).</summary>
    public const int MaxGuestRequestsPerRun = 20;

    /// <summary>Departures closed in one run.</summary>
    public const int MaxDeparturesPerRun = 30;
}

/// <summary>How a guest can be reached, from what the booking holds.</summary>
public static partial class AutopilotContacts
{
    /// <summary>An address that looks like one: something, an at sign, a domain with a dot. The provider is the one that knows if it exists.</summary>
    public static bool IsUsableEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email) && EmailShape().IsMatch(email.Trim());

    /// <summary>
    /// The number as WhatsApp wants it: digits with the country code first, no plus and no zeros in front (<c>393331234567</c>); null
    /// when it cannot be said without guessing. A number with <c>+</c> or <c>00</c> is read as given. A number with neither is taken for an
    /// Italian mobile only when the guest is Italian or has no country on file (the host is: his guests of a manual booking are mostly
    /// too); any other country asks for the prefix, which the host adds to the guest.
    /// </summary>
    public static string? InternationalPhone(string? phone, string? country)
    {
        if (string.IsNullOrWhiteSpace(phone))
            return null;

        var trimmed = phone.Trim();
        var digits = new string(trimmed.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0)
            return null;

        string international;
        if (trimmed.StartsWith('+'))
            international = digits;
        else if (digits.StartsWith("00", StringComparison.Ordinal))
            international = digits[2..];
        else if (digits[0] == '3' && digits.Length is >= 9 and <= 10 && AutopilotLanguages.IsItalian(country))
            international = "39" + digits;
        else
            return null;

        return international.Length is >= 8 and <= 15 && international[0] != '0' ? international : null;
    }

    /// <summary>The link that opens WhatsApp on a chat with the guest, the text already typed.</summary>
    public static string WhatsAppUrl(string internationalPhone, string text) =>
        $"https://wa.me/{internationalPhone}?text={Uri.EscapeDataString(text)}";

    /// <summary>
    /// The link that opens the messages app on a text to the guest, the text already typed. The <c>?&amp;body=</c> form is the one both Android
    /// and iOS read.
    /// </summary>
    public static string SmsUrl(string internationalPhone, string text) =>
        $"sms:+{internationalPhone}?&body={Uri.EscapeDataString(text)}";

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s.]{2,}$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailShape();
}

/// <summary>The language of a message to a guest: the guest's own when the booking says where he comes from.</summary>
public static class AutopilotLanguages
{
    public const string Italian = "it";
    public const string English = "en";
    public const string German = "de";
    public const string French = "fr";
    public const string Spanish = "es";

    /// <summary>The languages the plain texts exist in.</summary>
    public static readonly IReadOnlyList<string> Supported = [Italian, English, German, French, Spanish];

    private static readonly Dictionary<string, string> ByWord = Build();

    /// <summary>
    /// The language of the guest, from the country and the nationality on the booking (names or codes, in Italian or English, case and
    /// accents ignored): the first of the two that is known. Italian when there is nothing on file (the host's own language), English for
    /// any other place (the language that is understood everywhere), never a guess between two languages of one country.
    /// </summary>
    public static string Of(string? country, string? nationality)
    {
        var any = false;
        foreach (var value in new[] { country, nationality })
        {
            var word = Normalize(value);
            if (word.Length == 0)
                continue;

            any = true;
            if (ByWord.TryGetValue(word, out var language))
                return language;
        }

        return any ? English : Italian;
    }

    /// <summary>True when the country is Italy, or is not on file.</summary>
    public static bool IsItalian(string? country)
    {
        var word = Normalize(country);
        return word.Length == 0 || (ByWord.TryGetValue(word, out var language) && language == Italian);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var plain = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                plain.Append(c);
        }

        return plain.ToString().Normalize(NormalizationForm.FormC);
    }

    private static Dictionary<string, string> Build()
    {
        var words = new Dictionary<string, string>(StringComparer.Ordinal);
        void Add(string language, params string[] names)
        {
            foreach (var name in names)
                words[name] = language;
        }

        Add(Italian, "it", "ita", "italia", "italy", "italien", "italie", "italiana", "italiano", "italian");
        Add(German, "de", "deu", "ger", "germania", "germany", "deutschland", "allemagne", "alemania", "tedesca", "tedesco", "german", "deutsch",
            "at", "aut", "austria", "osterreich", "austriaca", "austriaco", "austrian");
        Add(French, "fr", "fra", "francia", "france", "francese", "french", "francais", "francaise");
        Add(Spanish, "es", "esp", "spagna", "spain", "espana", "spagnola", "spagnolo", "spanish", "espanol", "espanola");
        Add(English, "en", "gb", "gbr", "uk", "regno unito", "united kingdom", "inghilterra", "england", "inglese", "british", "english",
            "us", "usa", "stati uniti", "united states", "american", "americana", "americano",
            "ie", "irl", "irlanda", "ireland", "au", "aus", "australia", "ca", "can", "canada", "nz", "nuova zelanda", "new zealand");
        return words;
    }
}

/// <summary>
/// The chat message that asks a guest for the data of the stay: the plain texts, what an answer of the AI must look like to be used,
/// and how a text becomes the message of one guest. The text is a template with four placeholders (<c>{nome}</c>, <c>{struttura}</c>,
/// <c>{data}</c>, <c>{link}</c>): the AI never sees a name, a property or a link, and cannot write one.
/// </summary>
public static partial class AutopilotChatText
{
    public const string Name = "nome";
    public const string Property = "struttura";
    public const string Date = "data";
    public const string Link = "link";

    private static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal) { Name, Property, Date, Link };

    public const int MinLength = 30;
    public const int MaxLength = 420;

    /// <summary>The key of a text of the answer: <c>it-1</c> is the first message in Italian.</summary>
    public static string VariantKey(string language, int reminder) => $"{language}-{reminder}";

    /// <summary>The plain text of CasaZen for a language and a reminder (1 first message, 2 and 3 the reminders).</summary>
    public static string Fallback(string language, int reminder)
    {
        var texts = PlainTexts.GetValueOrDefault(language) ?? PlainTexts[AutopilotLanguages.English];
        return texts[Math.Clamp(reminder, 1, texts.Length) - 1];
    }

    /// <summary>
    /// A text the AI wrote, if it can be sent: on one line, between <see cref="MinLength"/> and <see cref="MaxLength"/> characters, with
    /// no digit, link, address, markup or mention of authorities, fines or sanctions (a message to a guest never threatens, and the legal
    /// reasons are the host's to explain), only the placeholders above, and exactly one <c>{link}</c> (put at the end when the AI forgot it).
    /// Null when it cannot: the plain text is used.
    /// </summary>
    public static string? Check(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var text = WhiteSpace().Replace(raw, " ").Trim();
        if (text.Length is < MinLength or > MaxLength || NotAllowed().IsMatch(text))
            return null;

        var found = PlaceholderToken().Matches(text).Select(m => m.Groups[1].Value).ToList();
        if (found.Any(name => !Placeholders.Contains(name)) || found.Count(name => name == Link) > 1)
            return null;

        var rest = PlaceholderToken().Replace(text, string.Empty);
        if (rest.Contains('{') || rest.Contains('}'))
            return null;

        return found.Contains(Link) ? text : $"{text} {{{Link}}}";
    }

    /// <summary>
    /// The message of one guest: the placeholders filled with his first name (left out, with the space before it, when he has none), the
    /// name of the property, the day of the arrival and the link of the check-in.
    /// </summary>
    public static string Fill(string template, string? firstName, string? propertyName, DateTime checkIn, string link)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentException.ThrowIfNullOrWhiteSpace(link);

        var name = Clean(firstName, 40);
        var property = Clean(propertyName, 60);
        var text = name.Length == 0
            ? template.Replace($" {{{Name}}}", string.Empty, StringComparison.Ordinal).Replace($"{{{Name}}}", string.Empty, StringComparison.Ordinal)
            : template.Replace($"{{{Name}}}", name, StringComparison.Ordinal);

        return text
            .Replace($"{{{Property}}}", property, StringComparison.Ordinal)
            .Replace($"{{{Date}}}", checkIn.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace($"{{{Link}}}", link, StringComparison.Ordinal);
    }

    /// <summary>A value typed by a person, fit to go in a message: one line, no braces (it cannot become a placeholder), cut.</summary>
    private static string Clean(string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var line = WhiteSpace().Replace(value, " ").Replace("{", string.Empty, StringComparison.Ordinal).Replace("}", string.Empty, StringComparison.Ordinal).Trim();
        return line.Length <= limit ? line : line[..limit].TrimEnd();
    }

    private static readonly IReadOnlyDictionary<string, string[]> PlainTexts = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [AutopilotLanguages.Italian] =
        [
            "Ciao {nome}, per completare la registrazione del tuo soggiorno a {struttura} (arrivo del {data}) servono i dati di tutti gli ospiti, con un documento d'identità. Bastano pochi minuti da qui: {link}",
            "Ciao {nome}, ti scrivo per un promemoria: per registrare il soggiorno a {struttura} mancano ancora i dati degli ospiti. Puoi inserirli in pochi minuti qui: {link}",
            "Ciao {nome}, ultimo promemoria: servono ancora i dati degli ospiti per registrare il soggiorno a {struttura}. Grazie per la collaborazione! {link}",
        ],
        [AutopilotLanguages.English] =
        [
            "Hi {nome}, to complete the registration of your stay at {struttura} (arrival on {data}) we need the details of all guests, with an ID document. It only takes a few minutes: {link}",
            "Hi {nome}, a gentle reminder: we still need the details of the guests to register your stay at {struttura}. You can enter them in a few minutes here: {link}",
            "Hi {nome}, last reminder: we still need the details of the guests to register your stay at {struttura}. Thank you for your help! {link}",
        ],
        [AutopilotLanguages.German] =
        [
            "Hallo {nome}, um die Anmeldung Ihres Aufenthalts in {struttura} (Anreise am {data}) abzuschließen, brauchen wir die Daten aller Gäste mit einem Ausweisdokument. Das dauert nur wenige Minuten: {link}",
            "Hallo {nome}, eine freundliche Erinnerung: Für die Anmeldung Ihres Aufenthalts in {struttura} fehlen uns noch die Daten der Gäste. In wenigen Minuten hier eintragen: {link}",
            "Hallo {nome}, letzte Erinnerung: Wir brauchen noch die Daten der Gäste, um Ihren Aufenthalt in {struttura} anzumelden. Vielen Dank! {link}",
        ],
        [AutopilotLanguages.French] =
        [
            "Bonjour {nome}, pour finaliser l'enregistrement de votre séjour à {struttura} (arrivée le {data}), nous avons besoin des informations de tous les voyageurs, avec une pièce d'identité. Cela ne prend que quelques minutes : {link}",
            "Bonjour {nome}, petit rappel : il manque encore les informations des voyageurs pour enregistrer votre séjour à {struttura}. Quelques minutes suffisent ici : {link}",
            "Bonjour {nome}, dernier rappel : nous avons encore besoin des informations des voyageurs pour enregistrer votre séjour à {struttura}. Merci beaucoup ! {link}",
        ],
        [AutopilotLanguages.Spanish] =
        [
            "Hola {nome}, para completar el registro de su estancia en {struttura} (llegada el {data}) necesitamos los datos de todos los huéspedes, con un documento de identidad. Solo lleva unos minutos: {link}",
            "Hola {nome}, un recordatorio amable: todavía faltan los datos de los huéspedes para registrar su estancia en {struttura}. Puede completarlos en unos minutos aquí: {link}",
            "Hola {nome}, último recordatorio: todavía necesitamos los datos de los huéspedes para registrar su estancia en {struttura}. ¡Muchas gracias! {link}",
        ],
    };

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"\{([^{}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderToken();

    /// <summary>
    /// What a message to a guest never holds: a digit (dates and amounts are not the AI's to write), a link or an address, a mark of
    /// markup or of a list, and the words of a threat or of the authorities.
    /// </summary>
    [GeneratedRegex(
        @"[0-9]|https?:|www\.|@|[*_`<>\[\]|#~\\]|\b(?:polizia|questura|carabinieri|multa|multe|sanzion\w*|denunc\w*|penale|penali|police|fined|penalt\w*|polizei|strafe\w*|amende\w*|polic[ií]a)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotAllowed();
}
