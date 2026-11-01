using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

// The AI help of the showcase editor (<c>api/supplier/showcase/texts/suggestion</c>): drafts for the sentence in large type, the introduction,
// "chi siamo" and the tips "prima che arriviamo". The editor sends only what the supplier typed for the AI; the data of the supplier is read on the server.

/// <summary>
/// Body of <c>POST api/supplier/showcase/texts/suggestion</c>: the members to draft (none = all of them: <c>tagline</c>, <c>intro</c>,
/// <c>bio</c>, <c>prep</c>), what the supplier typed about itself, and whether to write a new text even when the same inputs were answered
/// before.
/// </summary>
public class SuggestShowcaseTextsRequest
{
    /// <summary>The members to draft; a name that is not one of the four is ignored.</summary>
    [MaxLength(8)]
    public List<string>? Fields { get; set; }

    /// <summary>"Due righe su di te" and three quick questions, all optional: the only source of a personal claim in the text.</summary>
    public ShowcaseTextAnswersRequest? Answers { get; set; }

    /// <summary>Write a new text instead of answering again with the one kept for the same inputs.</summary>
    public bool Regenerate { get; set; }

    /// <summary>The request as the service takes it.</summary>
    public ShowcaseTextSuggestionRequest ToCommand() =>
        new(Fields, Answers?.ToAnswers(), Regenerate);
}

/// <summary>What the supplier typed about itself for the AI; every answer is optional and plain text.</summary>
public class ShowcaseTextAnswersRequest
{
    /// <summary>Two lines about the supplier, in its own words.</summary>
    [StringLength(ShowcaseTextAnswers.AboutMax)]
    public string? About { get; set; }

    /// <summary>How long it has been working, as typed ("dal 2015", "10 anni").</summary>
    [StringLength(ShowcaseTextAnswers.YearsMax)]
    public string? Years { get; set; }

    /// <summary>What it is specialised in.</summary>
    [StringLength(ShowcaseTextAnswers.SpecialtiesMax)]
    public string? Specialties { get; set; }

    /// <summary>What sets it apart.</summary>
    [StringLength(ShowcaseTextAnswers.DifferentiatorMax)]
    public string? Differentiator { get; set; }

    internal ShowcaseTextAnswers ToAnswers() => new ShowcaseTextAnswers(About, Years, Specialties, Differentiator).Cleaned();
}

/// <summary>
/// A suggestion: the members the AI wrote and that passed the checks (a member that is not there was not asked for or was dropped, see
/// <see cref="Rejected"/>), what it was based on, and <see cref="AiUsed"/>. It is a draft: nothing is public before the supplier publishes.
/// </summary>
public class ShowcaseTextSuggestionResponse
{
    /// <summary>The sentence in large type (at most 90 characters).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tagline { get; set; }

    /// <summary>The line under the title (at most 180 characters).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Intro { get; set; }

    /// <summary>"Chi siamo" (at most 700 characters).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Bio { get; set; }

    /// <summary>"Prima che arriviamo": up to six short tips; empty when not asked for or dropped.</summary>
    public IReadOnlyList<string> Prep { get; set; } = [];

    /// <summary>What the text was written from, so the editor can say it.</summary>
    public ShowcaseTextBasisDto Basis { get; set; } = new();

    /// <summary>The members the AI wrote but the checks dropped (over the limit, or a claim the supplier did not make).</summary>
    public IReadOnlyList<string> Rejected { get; set; } = [];

    /// <summary>Always <c>true</c> today: the text was written by an AI provider.</summary>
    public bool AiUsed { get; set; } = true;
}

/// <summary>What a suggestion was based on: category codes, comune names, the names of the services, and the answers used.</summary>
public class ShowcaseTextBasisDto
{
    /// <summary>Codes of the service categories (the client translates them).</summary>
    public IReadOnlyList<string> Categories { get; set; } = [];

    /// <summary>Names of the comuni the supplier covers.</summary>
    public IReadOnlyList<string> Zones { get; set; } = [];

    /// <summary>The names the supplier gave its services.</summary>
    public IReadOnlyList<string> Services { get; set; } = [];

    /// <summary>Which answers of the supplier were used: <c>about</c>, <c>years</c>, <c>specialties</c>, <c>differentiator</c>.</summary>
    public IReadOnlyList<string> Answers { get; set; } = [];
}

/// <summary>Whether the AI help can answer now (<c>GET api/supplier/showcase/texts/suggestion/status</c>): when it cannot, the editor shows none of it.</summary>
public class ShowcaseTextSuggestionStatusDto
{
    public bool Available { get; set; }
}

/// <summary>Maps the suggestion of the service to its answer.</summary>
public static class ShowcaseTextSuggestionMapper
{
    public static ShowcaseTextSuggestionResponse ToResponse(ShowcaseTextSuggestion suggestion)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        return new ShowcaseTextSuggestionResponse
        {
            Tagline = suggestion.Tagline,
            Intro = suggestion.Intro,
            Bio = suggestion.Bio,
            Prep = suggestion.Prep,
            Basis = new ShowcaseTextBasisDto
            {
                Categories = suggestion.Basis.Categories,
                Zones = suggestion.Basis.Zones,
                Services = suggestion.Basis.Services,
                Answers = suggestion.Basis.Answers,
            },
            Rejected = suggestion.Rejected,
            AiUsed = suggestion.AiUsed,
        };
    }
}
