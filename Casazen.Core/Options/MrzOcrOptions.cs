namespace Casazen.Core.Options;

/// <summary>
/// Section <c>MrzOcr</c>: the reading of the identity document of the guest check-in (E03/F04/US01/T02, ADR-013-R6, R8 and R10, NF-24, NF-51).
/// The MRZ band is read with the OCR-B model and the printed fields with the Italian and English models of Tesseract, in process through the
/// C interface of the library installed in the image (the same package as the energy-certificate reader), reading the picture from memory:
/// a document image never touches the disk (ADR-013-R6). On Railway the variables are <c>MrzOcr__Mrz__Sha256</c>, and so on.
/// <para>
/// The language files are external model files with a pinned SHA-256, like the biometric models: a file that is missing, not pinned or
/// different from its hash is not used, and the reading answers "nothing read" (the guided manual entry applies), never a guess. Nothing here
/// is a secret. Operator steps: <c>docs/TECHNICAL.md</c> (Identity document reading).
/// </para>
/// </summary>
public class MrzOcrOptions
{
    public const string SectionName = "MrzOcr";

    public ModelFileOptions Mrz { get; set; } = new()
    {
        Name = "OCR-B",
        Version = "tessdata",
        License = "Apache-2.0",
        Provenance = "Language data for the OCR-B face of the MRZ, trained on synthetic text: no personal data; confirm in the DPIA (ADR-013 gate).",
        Path = "MlModels/ocr/ocrb.traineddata",
    };

    public string MrzLanguage { get; set; } = "ocrb";

    public ModelFileOptions VisualZoneItalian { get; set; } = new()
    {
        Name = "ita",
        Version = "tessdata",
        License = "Apache-2.0",
        Provenance = "Tesseract language data (tessdata); no personal data.",
        Path = "/usr/share/tesseract-ocr/5/tessdata/ita.traineddata",
    };

    public ModelFileOptions VisualZoneEnglish { get; set; } = new()
    {
        Name = "eng",
        Version = "tessdata",
        License = "Apache-2.0",
        Provenance = "Tesseract language data (tessdata); no personal data.",
        Path = "/usr/share/tesseract-ocr/5/tessdata/eng.traineddata",
    };

    public int TimeoutSeconds { get; set; } = 12;

    /// <summary>Readings at the same time per API process (an OCR engine is not thread safe, so each one has its own).</summary>
    public int MaxConcurrency { get; set; } = 2;

    public int MaxImageBytes { get; set; } = 12_000_000;

    public int MaxImagePixels { get; set; } = 25_000_000;

    public int MaxWorkingSide { get; set; } = 2000;

    public double BlurRatioThreshold { get; set; } = 0.6;

    public double GlareShareThreshold { get; set; } = 0.04;

    public IReadOnlyList<string> Validate()
    {
        var failures = new List<string>();
        failures.AddRange(Mrz.Validate($"{SectionName}:Mrz"));
        failures.AddRange(VisualZoneItalian.Validate($"{SectionName}:VisualZoneItalian"));
        failures.AddRange(VisualZoneEnglish.Validate($"{SectionName}:VisualZoneEnglish"));
        if (string.IsNullOrWhiteSpace(MrzLanguage) || MrzLanguage.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            failures.Add($"{SectionName}:MrzLanguage must be a language code (letters, digits, underscore).");
        if (TimeoutSeconds is < 2 or > 120)
            failures.Add($"{SectionName}:TimeoutSeconds must be between 2 and 120.");
        if (MaxConcurrency is < 1 or > 8)
            failures.Add($"{SectionName}:MaxConcurrency must be between 1 and 8.");
        if (MaxImageBytes is < 1000 or > 100_000_000)
            failures.Add($"{SectionName}:MaxImageBytes must be between 1000 and 100000000.");
        if (MaxImagePixels is < 10_000 or > 100_000_000)
            failures.Add($"{SectionName}:MaxImagePixels must be between 10000 and 100000000.");
        if (MaxWorkingSide is < 600 or > 4000)
            failures.Add($"{SectionName}:MaxWorkingSide must be between 600 and 4000.");
        if (double.IsNaN(BlurRatioThreshold) || BlurRatioThreshold <= 0 || BlurRatioThreshold >= 1)
            failures.Add($"{SectionName}:BlurRatioThreshold must be between 0 and 1 (exclusive).");
        if (double.IsNaN(GlareShareThreshold) || GlareShareThreshold <= 0 || GlareShareThreshold >= 1)
            failures.Add($"{SectionName}:GlareShareThreshold must be between 0 and 1 (exclusive).");
        return failures;
    }
}
