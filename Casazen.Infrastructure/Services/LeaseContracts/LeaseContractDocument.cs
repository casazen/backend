using System.Globalization;
using Casazen.Core.Documents;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Enums;
using Casazen.Core.Leases;

namespace Casazen.Infrastructure.Services.LeaseContracts;

/// <summary>
/// Builds the text of a lease contract from a template and the lease data (LT-03, A7-03). The clause texts come only
/// from the template file; this class adds the computed data, and in the preview the BOZZA markers and the
/// "missing" markers. The contract is an Italian document: its labels are Italian. The PDF is laid out by
/// <see cref="IPdfDocumentRenderer"/> (LT-09): A4, wrapped, paginated, no length limit.
/// </summary>
internal static class LeaseContractDocument
{
    public const string DraftMarker = "BOZZA - template non approvato";
    public const string ApprovedPreviewMarker = "ANTEPRIMA - documento non valido per la firma";
    public const string MissingClauseMarker = "[TESTO DELLA CLAUSOLA NON FORNITO]";
    public const string MissingTitleMarker = "[TITOLO DEL CONTRATTO NON FORNITO]";

    /// <summary>Watermark of every page of the preview of a template that is not approved.</summary>
    public const string DraftWatermark = "BOZZA";

    /// <summary>Watermark of every page of the preview of an approved template: still not the document to sign.</summary>
    public const string ApprovedPreviewWatermark = "ANTEPRIMA";

    private static readonly NumberFormatInfo ItalianNumbers = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = ".",
        NumberDecimalDigits = 2,
    };

    private static readonly IReadOnlyDictionary<string, string> DataLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [LeaseContractPlaceholders.Landlords] = "Locatori",
        [LeaseContractPlaceholders.Tenants] = "Conduttori",
        [LeaseContractPlaceholders.PropertyAddress] = "Indirizzo dell'immobile",
        [LeaseContractPlaceholders.PropertyComune] = "Comune",
        [LeaseContractPlaceholders.CadastralData] = "Dati catastali",
        [LeaseContractPlaceholders.ApeData] = "Estremi dell'APE",
        [LeaseContractPlaceholders.MonthlyRent] = "Canone mensile (euro)",
        [LeaseContractPlaceholders.AnnualRent] = "Canone annuo (euro)",
        [LeaseContractPlaceholders.SecurityDeposit] = "Deposito cauzionale (euro)",
        [LeaseContractPlaceholders.StartDate] = "Decorrenza",
        [LeaseContractPlaceholders.EndDate] = "Scadenza",
        [LeaseContractPlaceholders.Term] = "Durata",
    };

    /// <summary>
    /// Value of every placeholder for <paramref name="lease"/>; null when the lease has no such data. Cadastral data come
    /// from the property (sheet, parcel, category and income; subaltern when present), the APE identification from the
    /// latest APE document of the property (code and energy class, LT-10), the security deposit from the lease.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> ResolveData(LeaseContract lease)
    {
        var term = LeaseTerm.Between(lease.StartDate, lease.EndDate);
        var hasDates = lease.StartDate != default && lease.EndDate != default;
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [LeaseContractPlaceholders.Landlords] = FormatParties(lease.Parties, PartyRole.Landlord),
            [LeaseContractPlaceholders.Tenants] = FormatParties(lease.Parties, PartyRole.Tenant),
            [LeaseContractPlaceholders.PropertyAddress] = FormatAddress(lease.Property),
            [LeaseContractPlaceholders.PropertyComune] = NullIfBlank(lease.Property?.City),
            [LeaseContractPlaceholders.CadastralData] = FormatCadastralData(lease.Property),
            [LeaseContractPlaceholders.ApeData] = FormatApe(lease.Property),
            [LeaseContractPlaceholders.MonthlyRent] = lease.MonthlyRent > 0 ? FormatAmount(lease.MonthlyRent) : null,
            [LeaseContractPlaceholders.AnnualRent] = lease.MonthlyRent > 0 ? FormatAmount(lease.MonthlyRent * 12) : null,
            [LeaseContractPlaceholders.SecurityDeposit] = lease.SecurityDeposit is { } deposit ? FormatAmount(deposit) : null,
            [LeaseContractPlaceholders.StartDate] = hasDates ? FormatDate(lease.StartDate) : null,
            [LeaseContractPlaceholders.EndDate] = hasDates ? FormatDate(lease.EndDate) : null,
            [LeaseContractPlaceholders.Term] = hasDates && term is { } t && (t.Months > 0 || t.Days > 0) ? t.ToItalianText() : null,
        };
    }

    /// <summary>Placeholders used by the template that have no value for this lease.</summary>
    public static IReadOnlyList<string> MissingData(LeaseContractTemplateState state, IReadOnlyDictionary<string, string?> data) =>
        state.Sections
            .SelectMany(s => s.Placeholders)
            .Distinct(StringComparer.Ordinal)
            .Where(p => string.IsNullOrWhiteSpace(data.GetValueOrDefault(p)))
            .ToList();

    /// <summary>
    /// The final contract: the approved title, then only the sections of the approved template (heading and text, line
    /// breaks kept), with every placeholder filled.
    /// </summary>
    public static PdfDocumentContent BuildFinalDocument(LeaseContractTemplateState state, IReadOnlyDictionary<string, string?> data)
    {
        var blocks = new List<PdfBlock>();
        foreach (var section in state.Sections)
        {
            blocks.Add(new PdfHeading(Heading(state.Regime, section)));
            blocks.AddRange(PdfBlocks.FromPlainText(Fill(section.Text, data, missing => throw new InvalidOperationException(
                $"Placeholder {missing} has no value: check MissingData before building the final contract."))));
        }

        return new PdfDocumentContent(state.Title!, blocks);
    }

    /// <summary>
    /// The preview: state of the template, then every section (those of the file in their order, then the required ones
    /// the file lacks) with the missing texts and data marked, and a watermark on every page (<see cref="DraftWatermark"/>,
    /// or <see cref="ApprovedPreviewWatermark"/> for an approved template). Never sent to signature or registration.
    /// </summary>
    public static PdfDocumentContent BuildPreviewDocument(LeaseContractTemplateState state, IReadOnlyDictionary<string, string?> data)
    {
        var marker = state.IsApproved ? ApprovedPreviewMarker : DraftMarker;
        var status = new List<string>
        {
            string.Create(
                CultureInfo.InvariantCulture,
                $"Modello: {state.Regime}, versione {state.VersionId ?? "nessuna"}, stato: {StatusLabel(state.Status)}."),
        };
        if (state.MissingSections.Count > 0)
            status.Add($"Sezioni senza testo: {string.Join(", ", state.MissingSections)}.");
        if (state.Status == LeaseContractTemplateStatus.Incomplete && state.Issues.Count > 0)
            status.Add($"Problemi di formato del modello: {state.Issues.Count} (dettagli nei log dell'API).");

        var required = LeaseContractTemplateStructure.RequiredSections(state.Regime);
        var missingData = state.Sections.SelectMany(s => s.Placeholders)
            .Concat(required.Where(d => state.Sections.All(s => s.Id != d.Id)).SelectMany(d => d.DataPlaceholders))
            .Distinct(StringComparer.Ordinal)
            .Where(p => string.IsNullOrWhiteSpace(data.GetValueOrDefault(p)))
            .ToList();
        if (missingData.Count > 0)
            status.Add($"Dati mancanti: {string.Join(", ", missingData)}.");

        var blocks = new List<PdfBlock>
        {
            new PdfParagraph(string.Join('\n', status)),
            new PdfHeading(state.Title ?? MissingTitleMarker),
        };

        foreach (var section in state.Sections)
        {
            blocks.Add(new PdfHeading(Heading(state.Regime, section), Level: 3));
            if (section.Text.Length == 0)
            {
                var definition = required.FirstOrDefault(d => d.Id == section.Id);
                blocks.Add(MissingClause(definition?.DataPlaceholders ?? [], data));
            }
            else
            {
                blocks.AddRange(PdfBlocks.FromPlainText(Fill(section.Text, data, missing => $"[DATO MANCANTE: {missing}]")));
            }
        }

        foreach (var definition in required.Where(d => state.Sections.All(s => s.Id != d.Id)))
        {
            blocks.Add(new PdfHeading(definition.DefaultHeading, Level: 3));
            blocks.Add(MissingClause(definition.DataPlaceholders, data));
        }

        blocks.Add(new PdfParagraph(marker, Bold: true));
        return new PdfDocumentContent(marker, blocks)
        {
            Watermark = state.IsApproved ? ApprovedPreviewWatermark : DraftWatermark,
        };
    }

    private static PdfParagraph MissingClause(IEnumerable<string> dataPlaceholders, IReadOnlyDictionary<string, string?> data)
    {
        var lines = new List<string> { MissingClauseMarker };
        foreach (var placeholder in dataPlaceholders)
        {
            var value = data.GetValueOrDefault(placeholder);
            lines.Add($"- {DataLabels[placeholder]}: {(string.IsNullOrWhiteSpace(value) ? $"[DATO MANCANTE: {placeholder}]" : value)}");
        }

        return new PdfParagraph(string.Join('\n', lines));
    }

    private static string Heading(FiscalRegime regime, LeaseContractTemplateSection section) =>
        section.Heading
        ?? LeaseContractTemplateStructure.RequiredSections(regime).FirstOrDefault(d => d.Id == section.Id)?.DefaultHeading
        ?? section.Id;

    private static string Fill(string text, IReadOnlyDictionary<string, string?> data, Func<string, string> onMissing) =>
        LeaseContractTemplateParser.PlaceholderPattern().Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            var value = data.GetValueOrDefault(name);
            return string.IsNullOrWhiteSpace(value) ? onMissing(name) : value;
        });

    private static string StatusLabel(LeaseContractTemplateStatus status) => status switch
    {
        LeaseContractTemplateStatus.Missing => "modello assente",
        LeaseContractTemplateStatus.Incomplete => "modello incompleto",
        LeaseContractTemplateStatus.NotApproved => "modello completo ma non approvato",
        LeaseContractTemplateStatus.Approved => "modello approvato",
        _ => status.ToString(),
    };

    private static string? FormatParties(IEnumerable<Party>? parties, PartyRole role)
    {
        var matching = parties?.Where(p => p.Role == role).ToList() ?? [];
        if (matching.Count == 0)
            return null;

        return string.Join("; ", matching.Select(p =>
        {
            var name = $"{p.FirstName} {p.LastName}".Trim();
            return string.IsNullOrWhiteSpace(p.FiscalCode) ? name : $"{name} (C.F. {p.FiscalCode})";
        }));
    }

    private static string? FormatAddress(Property? property)
    {
        if (property is null || string.IsNullOrWhiteSpace(property.Address))
            return null;

        var locality = $"{property.PostalCode} {property.City}".Trim();
        return locality.Length == 0 ? property.Address.Trim() : $"{property.Address.Trim()}, {locality}";
    }

    /// <summary>"Foglio 12, particella 345, subalterno 6, categoria A/2, rendita catastale euro 512,30"; null when incomplete.</summary>
    private static string? FormatCadastralData(Property? property)
    {
        if (property is null || !property.HasCadastralData)
            return null;

        var parts = new List<string>
        {
            $"Foglio {property.CadastralSheet!.Trim()}",
            $"particella {property.CadastralParcel!.Trim()}",
        };
        if (!string.IsNullOrWhiteSpace(property.CadastralSubaltern))
            parts.Add($"subalterno {property.CadastralSubaltern.Trim()}");
        parts.Add($"categoria {property.CadastralCategory!.Trim()}");
        parts.Add($"rendita catastale euro {FormatAmount(property.CadastralIncome!.Value)}");
        return string.Join(", ", parts);
    }

    /// <summary>
    /// Code and energy class of the latest APE document of the property; null when there is none or its identification
    /// was not entered (the contract then waits for it).
    /// </summary>
    private static string? FormatApe(Property? property)
    {
        var ape = property?.PropertyDocuments?
            .Where(d => d.DocumentType == DocumentType.Ape)
            .OrderByDescending(d => d.UploadedAt)
            .FirstOrDefault();
        return ape is { HasApeIdentification: true }
            ? $"codice {ape.ApeCode!.Trim()}, classe energetica {ape.ApeEnergyClass!.Trim()}"
            : null;
    }

    private static string FormatAmount(decimal amount) => amount.ToString("N2", ItalianNumbers);

    private static string FormatDate(DateTime date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
