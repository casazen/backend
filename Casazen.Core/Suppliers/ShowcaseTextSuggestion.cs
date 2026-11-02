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

    /// <summary>Every member that can be drafted, in the order of the editor.</summary>
    public static IReadOnlyList<string> All { get; } = [Tagline, Intro, Bio, Prep];

    /// <summary>
    /// The members of <paramref name="requested"/> that can be drafted, once each and in the order of <see cref="All"/>; a name that is not a
    /// member is ignored, and every member is drafted when the request names none that is.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? requested)
    {
        var asked = requested?.Where(field => !string.IsNullOrWhiteSpace(field)).Select(field => field!.Trim().ToLowerInvariant()).ToHashSet();
        var known = asked is null ? new List<string>() : All.Where(asked.Contains).ToList();
        return known.Count == 0 ? All : known;
    }
}

/// <summary>The keys that name the answers of <see cref="ShowcaseTextAnswers"/> in the basis of a suggestion.</summary>
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

    /// <summary>No answer at all.</summary>
    public static ShowcaseTextAnswers None { get; } = new();

    /// <summary>True when the supplier typed nothing.</summary>
    public bool IsEmpty => About is null && Years is null && Specialties is null && Differentiator is null;

    /// <summary>The answers as one line each (<see cref="ShowcaseText.Line"/>) and cut at their limit; a blank answer is <c>null</c>.</summary>
    public ShowcaseTextAnswers Cleaned() => new(
        Clean(About, AboutMax),
        Clean(Years, YearsMax),
        Clean(Specialties, SpecialtiesMax),
        Clean(Differentiator, DifferentiatorMax));

    /// <summary>The keys (<see cref="ShowcaseTextAnswerKeys"/>) of the answers that are there.</summary>
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

/// <summary>The texts the supplier already has in the draft: they are not rewritten, they only tell the AI which tone to keep.</summary>
public sealed record ShowcaseTextCurrent(string? Tagline, string? Intro, string? Bio, IReadOnlyList<string> Prep)
{
    /// <summary>No text written yet.</summary>
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
    /// <summary>True when there is nothing true to write from: no category, no zone, no service.</summary>
    public bool IsEmpty => Categories.Count == 0 && Zones.Count == 0 && Services.Count == 0;
}

/// <summary>Body of the request of a suggestion.</summary>
/// <param name="Fields">The members to draft (<see cref="ShowcaseTextFields"/>); none = every member.</param>
/// <param name="Answers">What the supplier typed about itself, if anything.</param>
/// <param name="Regenerate">Write a new text even when the same inputs were answered before.</param>
public sealed record ShowcaseTextSuggestionRequest(
    IReadOnlyList<string>? Fields = null,
    ShowcaseTextAnswers? Answers = null,
    bool Regenerate = false);

/// <summary>What a suggestion was really based on, so the editor can say it: categories (codes), zones, services, and the answers used.</summary>
public sealed record ShowcaseTextBasis(
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> Zones,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Answers);

/// <summary>
/// A suggestion: the members the AI wrote and that passed the checks (<see cref="ShowcaseTextRules"/>), what it was based on, and the members
/// it wrote but that were dropped (<see cref="Rejected"/>: over the limit, or with a claim the supplier did not make). It is a draft: the
/// supplier reads it, changes it and publishes it, and nothing is public before.
/// </summary>
public sealed record ShowcaseTextSuggestion(
    string? Tagline,
    string? Intro,
    string? Bio,
    IReadOnlyList<string> Prep,
    ShowcaseTextBasis Basis,
    IReadOnlyList<string> Rejected,
    bool AiUsed = true);

/// <summary>What <see cref="ShowcaseTextRules.Check"/> keeps of the answer of the model.</summary>
public sealed record ShowcaseTextOutcome(
    string? Tagline,
    string? Intro,
    string? Bio,
    IReadOnlyList<string> Prep,
    IReadOnlyList<string> Rejected)
{
    /// <summary>True when no requested member survived the checks.</summary>
    public bool IsEmpty => Tagline is null && Intro is null && Bio is null && Prep.Count == 0;
}
