using System.Globalization;
using System.Text.RegularExpressions;
using Casazen.Core.Exceptions;

namespace Casazen.Core.Services;

/// <summary>
/// What a template of the host can say about the conversation it is used in (E05/F02/US02/T02): each member is the value of one variable, or
/// <c>null</c> when this conversation has none (a request with no booking has no dates, a property with no check-in time has no time). The dates are
/// the Rome dates of the stay and the times are the ones on the clock of the property (<c>Property.CheckInFrom</c>, <c>CheckOutBy</c>).
/// </summary>
public sealed record MessageTemplateValues(
    string? GuestName,
    string? PropertyName,
    string? BookingCode,
    DateOnly? CheckInDate,
    DateOnly? CheckOutDate,
    TimeOnly? CheckInTime,
    TimeOnly? CheckOutTime,
    string? HostName);

/// <summary>
/// Replaces the variables of a template of the host with the values of a conversation (E05/F02/US02/T02, NF-63), pure: no database, no clock. A
/// variable is <c>{{name}}</c> (spaces inside the braces are allowed, the case of the name is not); the names are the eight of <see cref="Variables"/>.
/// </summary>
/// <remarks>
/// <para><b>Never a literal variable in the text.</b> A name that is not one of the eight, or a brace that opens or closes nothing, is 422
/// <c>template_variable_unknown</c>; a variable the conversation has no value for is 422 <c>template_variable_missing</c> naming it. The text that comes
/// out has no <c>{{</c> of the template left in it: the replacement is made in one pass, so a value that looks like a variable (a guest called
/// <c>{{host_name}}</c>) is not replaced again.</para>
/// <para><b>Plain text, at most <see cref="GuestMessageRules.TextMaxLength"/> characters.</b> The result is normalized like every message
/// (<see cref="GuestMessageRules.NormalizeText"/>); empty or too long is 422 <c>message_text_invalid</c>. The dates are written in the language of the
/// template (<c>d MMMM yyyy</c>) and the times as <c>HH:mm</c>.</para>
/// </remarks>
public static partial class MessageTemplateRenderer
{
    public const string GuestNameVariable = "guest_name";
    public const string PropertyNameVariable = "property_name";
    public const string BookingCodeVariable = "booking_code";
    public const string CheckInDateVariable = "check_in_date";
    public const string CheckOutDateVariable = "check_out_date";
    public const string CheckInTimeVariable = "check_in_time";
    public const string CheckOutTimeVariable = "check_out_time";
    public const string HostNameVariable = "host_name";

    /// <summary>The 422 of a name that is not one of the variables (or of a brace that opens or closes nothing).</summary>
    public const string UnknownCode = "template_variable_unknown";

    /// <summary>The 422 of a variable the conversation has no value for.</summary>
    public const string MissingCode = "template_variable_missing";

    /// <summary>The longest fragment of a template that an error repeats back (the host wrote it, but a message must stay short).</summary>
    private const int FragmentMaxLength = 40;

    /// <summary>The variables a template may use, in the order the screens list them.</summary>
    public static IReadOnlyList<string> Variables { get; } =
    [
        GuestNameVariable,
        PropertyNameVariable,
        BookingCodeVariable,
        CheckInDateVariable,
        CheckOutDateVariable,
        CheckInTimeVariable,
        CheckOutTimeVariable,
        HostNameVariable,
    ];

    /// <summary>
    /// The variables a body uses, each once, in the order they first appear, lower case. 422 <see cref="UnknownCode"/> for a name that is not one of
    /// <see cref="Variables"/> or a brace left alone: what a template is saved with is checked here, so a host learns of a typo when it writes the
    /// template and not in front of a guest.
    /// </summary>
    public static IReadOnlyList<string> VariablesOf(string body)
    {
        ArgumentNullException.ThrowIfNull(body);
        CheckBraces(body);

        var used = new List<string>();
        foreach (Match match in Variable().Matches(body))
        {
            var name = NameOf(match);
            if (!Variables.Contains(name, StringComparer.Ordinal))
                throw Unknown(match.Groups["name"].Value);

            if (!used.Contains(name, StringComparer.Ordinal))
                used.Add(name);
        }

        return used;
    }

    /// <summary>The text of <paramref name="body"/> with every variable replaced (see the remarks of the class for what is refused).</summary>
    /// <param name="culture">The language of the template: how the dates are written.</param>
    public static string Render(string body, MessageTemplateValues values, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(culture);
        CheckBraces(body);

        var text = Variable().Replace(body, match =>
        {
            var name = NameOf(match);
            if (!Variables.Contains(name, StringComparer.Ordinal))
                throw Unknown(match.Groups["name"].Value);

            var value = ValueOf(name, values, culture);
            return string.IsNullOrWhiteSpace(value) ? throw Missing(name) : value.Trim();
        });

        var clean = GuestMessageRules.NormalizeText(text);
        if (clean is null || clean.Length > GuestMessageRules.TextMaxLength)
            throw new DomainRuleException(MessageChannelRules.TextInvalidCode, "GuestMessageTextInvalid");

        return clean;
    }

    /// <summary>
    /// The text of <paramref name="body"/> with every variable that has a value replaced and <b>everything else between double braces taken out</b> (a variable the conversation has no
    /// value for, a name that is no variable, a brace left alone), with the space before a punctuation mark that follows it; <paramref name="unfilled"/> names what was taken out
    /// (<c>{{</c> for a brace). Normalized like a message, never refused: for a text the server did not write itself (the draft of the assistant, E05/F05/US02), which must never
    /// reach a guest with a literal variable in it. The length is the caller's to check.
    /// </summary>
    public static string FillKnown(string body, MessageTemplateValues values, CultureInfo culture, out IReadOnlyList<string> unfilled)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(culture);

        var missing = new List<string>();
        var text = Variable().Replace(body, match =>
        {
            var name = NameOf(match);
            var value = Variables.Contains(name, StringComparer.Ordinal) ? ValueOf(name, values, culture) : null;
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();

            missing.Add(name);
            return string.Empty;
        });

        if (text.Contains("{{", StringComparison.Ordinal) || text.Contains("}}", StringComparison.Ordinal))
        {
            missing.Add("{{");
            text = text.Replace("{{", string.Empty, StringComparison.Ordinal).Replace("}}", string.Empty, StringComparison.Ordinal);
        }

        unfilled = missing;
        text = SpaceBeforePunctuation().Replace(DoubleSpace().Replace(text, " "), "$1");
        return GuestMessageRules.NormalizeText(text) ?? string.Empty;
    }

    /// <summary>The value of a variable as it is written in the text; <c>null</c> when the conversation has none.</summary>
    private static string? ValueOf(string name, MessageTemplateValues values, CultureInfo culture) => name switch
    {
        GuestNameVariable => values.GuestName,
        PropertyNameVariable => values.PropertyName,
        BookingCodeVariable => values.BookingCode,
        CheckInDateVariable => Date(values.CheckInDate, culture),
        CheckOutDateVariable => Date(values.CheckOutDate, culture),
        CheckInTimeVariable => Time(values.CheckInTime),
        CheckOutTimeVariable => Time(values.CheckOutTime),
        HostNameVariable => values.HostName,
        _ => null,
    };

    // A DateOnly takes only the specifiers of a date: the day, the month by name and the year.
    private static string? Date(DateOnly? date, CultureInfo culture) => date?.ToString("d MMMM yyyy", culture);

    // A TimeOnly takes only the specifiers of a time.
    private static string? Time(TimeOnly? time) => time?.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string NameOf(Match match) => match.Groups["name"].Value.Trim().ToLowerInvariant();

    /// <summary>A brace that belongs to no variable is an unknown variable too: it would otherwise stay in the text as a literal.</summary>
    private static void CheckBraces(string body)
    {
        var rest = Variable().Replace(body, string.Empty);
        var open = rest.IndexOf("{{", StringComparison.Ordinal);
        var close = rest.IndexOf("}}", StringComparison.Ordinal);
        if (open >= 0 || close >= 0)
            throw Unknown(rest[Math.Min(open < 0 ? int.MaxValue : open, close < 0 ? int.MaxValue : close)..]);
    }

    private static DomainRuleException Unknown(string fragment) =>
        new(UnknownCode, "MessageTemplateVariableUnknown", Shown(fragment));

    private static DomainRuleException Missing(string name) =>
        new(MissingCode, "MessageTemplateVariableMissing", name);

    /// <summary>The start of a fragment of the template, on one line, short enough to repeat in an error.</summary>
    private static string Shown(string fragment)
    {
        var line = string.Join(' ', fragment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= FragmentMaxLength ? line : line[..FragmentMaxLength];
    }

    // {{ name }}: anything between the braces but a brace, so a name with a space or a sign is seen (and refused as unknown), not left in the text.
    [GeneratedRegex(@"\{\{(?<name>[^{}]*)\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex Variable();

    // Spaces or tabs before a punctuation mark, left where a variable was taken out («Ciao , grazie»).
    [GeneratedRegex(@"[ \t]+([,.;:!?])", RegexOptions.CultureInvariant)]
    private static partial Regex SpaceBeforePunctuation();

    // Two or more spaces or tabs in a row on one line.
    [GeneratedRegex(@"[ \t]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex DoubleSpace();
}
