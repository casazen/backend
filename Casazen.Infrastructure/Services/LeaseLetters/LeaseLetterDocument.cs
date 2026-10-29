using Casazen.Core.Documents;
using Casazen.Core.Leases;
using Casazen.Infrastructure.Services.LeaseContracts;

namespace Casazen.Infrastructure.Services.LeaseLetters;

/// <summary>
/// Builds the PDF of a letter from the text of its file and the values of the lease (LR-OPS-1). The words come only from the file; this class
/// picks the sections of the case, fills the placeholders and marks the letter as a draft while its text is not approved. The letter is an
/// Italian document, laid out by <see cref="IPdfDocumentRenderer"/> (A4, wrapped, paginated).
/// </summary>
internal static class LeaseLetterDocument
{
    /// <summary>Watermark of every page of a letter whose text is not approved.</summary>
    public const string DraftWatermark = "BOZZA — testo non ancora approvato";

    /// <summary>Closing line of a letter whose text is not approved: what the watermark says, in the text, so it stays if the text is copied.</summary>
    public const string DraftMarker = "BOZZA - testo non ancora approvato: da far rivedere prima dell'invio";

    /// <summary>What stands in the place of a datum the contract does not have.</summary>
    public const string ToComplete = "[da completare]";

    /// <summary>
    /// The letter: the title of the file, then the sections of the case (a section with a flag is printed only when the case has it) in the
    /// order of the file, with every placeholder filled; a datum that is not known is <see cref="ToComplete"/>.
    /// </summary>
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
