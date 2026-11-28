using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Casazen.Core.Suppliers;

namespace Casazen.Web.DTOs.Supplier;

public class SuggestShowcaseTextsRequest
{
    [MaxLength(8)]
    public List<string>? Fields { get; set; }

    public ShowcaseTextAnswersRequest? Answers { get; set; }

    /// <summary>Write a new text instead of answering again with the one kept for the same inputs.</summary>
    public bool Regenerate { get; set; }

    public ShowcaseTextSuggestionRequest ToCommand() =>
        new(Fields, Answers?.ToAnswers(), Regenerate);
}

public class ShowcaseTextAnswersRequest
{
    [StringLength(ShowcaseTextAnswers.AboutMax)]
    public string? About { get; set; }

    [StringLength(ShowcaseTextAnswers.YearsMax)]
    public string? Years { get; set; }

    [StringLength(ShowcaseTextAnswers.SpecialtiesMax)]
    public string? Specialties { get; set; }

    [StringLength(ShowcaseTextAnswers.DifferentiatorMax)]
    public string? Differentiator { get; set; }

    internal ShowcaseTextAnswers ToAnswers() => new ShowcaseTextAnswers(About, Years, Specialties, Differentiator).Cleaned();
}

public class ShowcaseTextSuggestionResponse
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Tagline { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Intro { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Bio { get; set; }

    public IReadOnlyList<string> Prep { get; set; } = [];

    public ShowcaseTextBasisDto Basis { get; set; } = new();

    public IReadOnlyList<string> Rejected { get; set; } = [];

    public bool AiUsed { get; set; } = true;
}

public class ShowcaseTextBasisDto
{
    public IReadOnlyList<string> Categories { get; set; } = [];

    public IReadOnlyList<string> Zones { get; set; } = [];

    public IReadOnlyList<string> Services { get; set; } = [];

    public IReadOnlyList<string> Answers { get; set; } = [];
}

public class ShowcaseTextSuggestionStatusDto
{
    public bool Available { get; set; }
}

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
