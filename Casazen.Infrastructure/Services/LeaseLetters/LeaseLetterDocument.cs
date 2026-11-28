using Casazen.Core.Documents;
using Casazen.Core.Leases;
using Casazen.Infrastructure.Services.LeaseContracts;

namespace Casazen.Infrastructure.Services.LeaseLetters;

internal static class LeaseLetterDocument
{
    public const string DraftWatermark = "BOZZA — testo non ancora approvato";

    public const string DraftMarker = "BOZZA - testo non ancora approvato: da far rivedere prima dell'invio";

    public const string ToComplete = "[da completare]";

    public static PdfDocumentContent Build(
        LeaseLetterState state, IReadOnlySet<string> flags, IReadOnlyDictionary<string, string?> values)
    {
        var definitions = LeaseLetterStructure.Sections(state.Kind);
        var blocks = new List<PdfBlock>();

        foreach (var section in state.Sections)
        {
            var definition = definitions.FirstOrDefault(d => d.Id == section.Id);
            if (definition?.Flag is { } flag && !flags.Contains(flag))
                continue;

            if (section.Heading is { } heading)
                blocks.Add(new PdfHeading(Fill(heading, values), Level: 3));
            blocks.AddRange(PdfBlocks.FromPlainText(Fill(section.Text, values)));
        }

        if (!state.IsApproved)
            blocks.Add(new PdfParagraph(DraftMarker, Bold: true));

        return new PdfDocumentContent(state.Title!, blocks)
        {
            Watermark = state.IsApproved ? null : DraftWatermark,
        };
    }

    private static string Fill(string text, IReadOnlyDictionary<string, string?> values) =>
        LeaseContractTemplateParser.PlaceholderPattern().Replace(text, match =>
        {
            var value = values.GetValueOrDefault(match.Groups[1].Value);
            return string.IsNullOrWhiteSpace(value) ? ToComplete : value;
        });
}
