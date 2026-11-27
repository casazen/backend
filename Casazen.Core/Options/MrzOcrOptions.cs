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

    /// <summary>OCR-B language data for the MRZ lines (<c>ocrb.traineddata</c>, or <c>mrz.traineddata</c> of tesseractMRZ with <see cref="MrzLanguage"/> <c>mrz</c>).</summary>
    public ModelFileOptions Mrz { get; set; } = new()
    {
        Name = "OCR-B",
        Version = "tessdata",
        License = "Apache-2.0",
        Provenance = "Language data for the OCR-B face of the MRZ, trained on synthetic text: no personal data; confirm in the DPIA (ADR-013 gate).",
        Path = "MlModels/ocr/ocrb.traineddata",
    };

    /// <summary>Language code of <see cref="Mrz"/> (the file name without <c>.traineddata</c>).</summary>
    public string MrzLanguage { get; set; } = "ocrb";

    /// <summary>Italian language data of the visual zone. The system package of the image puts it in <c>/usr/share/tesseract-ocr/5/tessdata</c>.</summary>
    public ModelFileOptions VisualZoneItalian { get; set; } = new()
    {
        Name = "ita",
        Version = "tessdata",
        License = "Apache-2.0",
        Provenance = "Tesseract language data (tessdata); no personal data.",
        Path = "/usr/share/tesseract-ocr/5/tessdata/ita.traineddata",
    };

    /// <summary>English language data of the visual zone; it sits in the same folder as <see cref="VisualZoneItalian"/>.</summary>
    public ModelFileOptions VisualZoneEnglish { get; set; } = new()
    {
        Name = "eng",
        Version = "tessdata",
        License = "Apache-2.0",
        Provenance = "Tesseract language data (tessdata); no personal data.",
        Path = "/usr/share/tesseract-ocr/5/tessdata/eng.traineddata",
    };

    /// <summary>Time one reading may take, in seconds, every attempt included. Past it the document is "not readable".</summary>
    public int TimeoutSeconds { get; set; } = 12;

    /// <summary>Readings at the same time per API process (an OCR engine is not thread safe, so each one has its own).</summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>Largest picture accepted, in bytes (the endpoint limit is 10 MB).</summary>
    public int MaxImageBytes { get; set; } = 12_000_000;

    /// <summary>Largest picture accepted, in pixels, checked before it is decoded (a small file can hold a huge picture).</summary>
    public int MaxImagePixels { get; set; } = 25_000_000;

    /// <summary>Longest side the picture is brought to before the MRZ is looked for, in pixels. Default 2000.</summary>
    public int MaxWorkingSide { get; set; } = 2000;

    /// <summary>
    /// Sharpness of the MRZ band above which it is "out of focus" (the share of the edge energy that survives a blur: 0.35 for crisp text, close to
    /// 1 when the text is already smeared). Provisional, to be calibrated with real photos. Default 0.6.
    /// </summary>
    public double BlurRatioThreshold { get; set; } = 0.6;

    /// <summary>Share of saturated pixels of the MRZ band above which a reflection hides the text (provisional). Default 0.04.</summary>
    public double GlareShareThreshold { get; set; } = 0.04;

    /// <summary>Configuration errors, empty when the section is usable. Names the setting, never a value.</summary>
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
