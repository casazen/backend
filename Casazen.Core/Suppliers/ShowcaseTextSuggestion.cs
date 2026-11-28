namespace Casazen.Core.Suppliers;

/// <summary>
/// The members of the showcase that the AI help of the editor may draft: the sentence in large type, the introduction, "chi siamo" and the
/// tips "prima che arriviamo" (the ones CasaZen already offers a static starter for, SP-13d). The guarantee, the people, the strengths, the
/// certifications and the questions are written by the supplier alone (VT-D20): the AI never drafts them and never adds an insurance or a
/// certification, so what is not written by the supplier is not on the page.
/// </summary>
public static class ShowcaseTextFields
{
    public const string Tagline = "tagline";
    public const string Intro = "intro";
    public const string Bio = "bio";
    public const string Prep = "prep";

    public static IReadOnlyList<string> All { get; } = [Tagline, Intro, Bio, Prep];

    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? requested)
    {
        var asked = requested?.Where(field => !string.IsNullOrWhiteSpace(field)).Select(field => field!.Trim().ToLowerInvariant()).ToHashSet();
        var known = asked is null ? new List<string>() : All.Where(asked.Contains).ToList();
        return known.Count == 0 ? All : known;
    }
}

public static class ShowcaseTextAnswerKeys
{
    public const string About = "about";
    public const string Years = "years";
    public const string Specialties = "specialties";
    public const string Differentiator = "differentiator";
}

/// <summary>
/// What the supplier typed about itself for the AI, all optional: "due righe su di te" and three quick questions. It is the <b>only</b> source
/// of a personal claim in a suggestion (years in business, specialities, what sets the supplier apart): the AI copies it faithfully and
/// writes nothing of that kind that is not here.
/// </summary>
/// <param name="About">Two lines about the supplier, in its own words.</param>
/// <param name="Years">How long it has been working, as typed ("dal 2015", "10 anni").</param>
/// <param name="Specialties">What it is specialised in.</param>
/// <param name="Differentiator">What sets it apart.</param>
public sealed record ShowcaseTextAnswers(string? About = null, string? Years = null, string? Specialties = null, string? Differentiator = null)
{
    public const int AboutMax = 400;
    public const int YearsMax = 60;
    public const int SpecialtiesMax = 200;
    public const int DifferentiatorMax = 200;

    public static ShowcaseTextAnswers None { get; } = new();

    public bool IsEmpty => About is null && Years is null && Specialties is null && Differentiator is null;

    public ShowcaseTextAnswers Cleaned() => new(
        Clean(About, AboutMax),
        Clean(Years, YearsMax),
        Clean(Specialties, SpecialtiesMax),
        Clean(Differentiator, DifferentiatorMax));

    public IReadOnlyList<string> Keys()
    {
        var keys = new List<string>();
        if (About is not null)
            keys.Add(ShowcaseTextAnswerKeys.About);
        if (Years is not null)
            keys.Add(ShowcaseTextAnswerKeys.Years);
        if (Specialties is not null)
            keys.Add(ShowcaseTextAnswerKeys.Specialties);
        if (Differentiator is not null)
            keys.Add(ShowcaseTextAnswerKeys.Differentiator);
        return keys;
    }

    private static string? Clean(string? value, int max)
    {
        var line = ShowcaseText.Line(value);
        return line is null ? null : line.Length <= max ? line : line[..max].TrimEnd();
    }
}

public sealed record ShowcaseTextCurrent(string? Tagline, string? Intro, string? Bio, IReadOnlyList<string> Prep)
{
    public static ShowcaseTextCurrent Empty { get; } = new(null, null, null, []);
}

/// <summary>
/// The facts the AI may use, all of them true and public on the showcase: the categories the supplier works in (codes of
/// <see cref="ServiceCategories"/>), the comuni it covers, the names it gave its services, and the texts it already wrote. Never a name of a
/// person, a contact, a price, a time, a count of jobs or a rating.
/// </summary>
public sealed record ShowcaseTextFacts(
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Zones,
    IReadOnlyList<string> Services,
    ShowcaseTextCurrent Current)
{
    public bool IsEmpty => Categories.Count == 0 && Zones.Count == 0 && Services.Count == 0;
}

public sealed record ShowcaseTextSuggestionRequest(
    IReadOnlyList<string>? Fields = null,
    ShowcaseTextAnswers? Answers = null,
    bool Regenerate = false);

public sealed record ShowcaseTextBasis(
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Zones,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Answers);

public sealed record ShowcaseTextSuggestion(
    string? Tagline,
    string? Intro,
    string? Bio,
    IReadOnlyList<string> Prep,
    ShowcaseTextBasis Basis,
    IReadOnlyList<string> Rejected,
    bool AiUsed = true);

public sealed record ShowcaseTextOutcome(
    string? Tagline,
    string? Intro,
    string? Bio,
    IReadOnlyList<string> Prep,
    IReadOnlyList<string> Rejected)
{
    public bool IsEmpty => Tagline is null && Intro is null && Bio is null && Prep.Count == 0;
}
