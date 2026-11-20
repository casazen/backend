namespace Casazen.Core.Regulatory.GuestRegistration;

/// <summary>
/// The catalog of the regimes of the registration of the guests (E29/F02/US01/T01, NF-125, decisions D3, D5, D6, D12 and D14 of
/// <c>alloggiati-ch/analisi-tecnica.md</c>): Italy, the 26 cantons and the Swiss place nobody knows. Static data in the code, read by the one resolver
/// (<see cref="Services.IGuestRegistrationRegimeResolver"/>); nothing here reads a property or the host's setting.
/// </summary>
/// <remarks>
/// <para><b>Source.</b> The cantons are filled from the research of 20 November 2026 (<c>alloggiati-ch/out/ricerca-ovest.md</c>, <c>ricerca-centro.md</c>,
/// <c>ricerca-nordest.md</c>, <c>ricerca-federale-integratori.md</c>) and from <c>alloggiati-ch/specifica-ticino.md</c> (Ticino), and from nothing else:
/// every obligation, channel, deadline, retention, certainty and source is what those files say, with their doubts. What they leave «non verificato»
/// is <see cref="GuestRegistrationCertainty.Unverified"/>, <see cref="GuestRegistrationObligation.Unknown"/> or
/// <see cref="GuestRegistrationPrivateRentals.Unknown"/>; an article number the research did not give is not written. Italy is the existing Alloggiati
/// flows (<c>.claude/context/regulations/alloggiati.md</c>), unchanged.</para>
/// <para><b>Updating it.</b> Change the entry and its sentence in <c>SharedResources.resx</c> and <c>.en.resx</c> together, move <c>VerifiedOn</c> to the day
/// the source was read again, and say what changed in <c>docs/compliance/guest-registration-switzerland.md</c>; the tests refuse an entry with no
/// source, no date or a sentence missing in one of the two languages. The effective duty (<see cref="GuestRegistrationDutyRules"/>) follows by itself:
/// it is <c>Required</c> by default only for a regime verified as applying to private rentals.</para>
/// <para><b>Texts.</b> The sentences are resource keys named after the code (<c>GuestRegime_CH_TI_Summary</c>); no supplier of software or of a cantonal
/// system is named in them or in the source titles.</para>
/// </remarks>
public static class GuestRegistrationRegimeCatalog
{
    /// <summary>The code of Italy: the existing Alloggiati flows.</summary>
    public const string ItalyCode = "IT";

    /// <summary>The code of Ticino: the one regime that raises a legal deadline by default (24 hours, the police portal).</summary>
    public const string TicinoCode = "CH-TI";

    /// <summary>The code of a Swiss property whose municipality is unknown or ambiguous.</summary>
    public const string UnknownSwissCode = "CH-UNKNOWN";

    /// <summary>The resource key of the note «the deadline is not verified: ask the cantonal police».</summary>
    public const string DeadlineNotVerifiedKey = "GuestRegimeDeadlineNotVerified";

    /// <summary>The resource key of the note «no deadline of transmission: the register is kept at the disposal of the police».</summary>
    public const string DeadlineNoSubmissionKey = "GuestRegimeDeadlineNoSubmission";

    private static readonly DateOnly ResearchDay = new(2026, 11, 20);

    private static readonly IReadOnlyList<GuestRegistrationRegime> Cantons =
    [
        Aargau(), AppenzellInnerrhoden(), AppenzellAusserrhoden(), Bern(), BaselLandschaft(), BaselStadt(), Fribourg(), Geneva(), Glarus(),
        Graubuenden(), Jura(), Lucerne(), Neuchatel(), Nidwalden(), Obwalden(), StGallen(), Schaffhausen(), Solothurn(), Schwyz(), Thurgau(),
        Ticino(), UriCanton(), Vaud(), Valais(), Zug(), Zurich(),
    ];

    // Declared before «All» on purpose: the static initializers run in this order, and the list holds the very instances the two properties give.

    /// <summary>The regime of Italy: the existing Alloggiati flows, nothing changes.</summary>
    public static GuestRegistrationRegime Italian { get; } = Italy();

    /// <summary>The regime of a Swiss property whose municipality is unknown or ambiguous: nothing is assumed about its canton.</summary>
    public static GuestRegistrationRegime UnknownSwissPlace { get; } = UnknownSwiss();

    /// <summary>Italy, the 26 cantons (by code) and the Swiss place nobody knows, in this order.</summary>
    public static IReadOnlyList<GuestRegistrationRegime> All { get; } = [Italian, .. Cantons, UnknownSwissPlace];

    /// <summary>The 26 cantons, by code.</summary>
    public static IReadOnlyList<GuestRegistrationRegime> SwissCantons => Cantons;

    /// <summary>The regime of a canton by its two letters (any case); the regime of the unknown Swiss place when the catalog has no such canton.</summary>
    public static GuestRegistrationRegime ForCanton(string? cantonCode)
    {
        var code = cantonCode?.Trim();
        return string.IsNullOrEmpty(code)
            ? UnknownSwissPlace
            : Cantons.FirstOrDefault(c => string.Equals(c.CantonCode, code, StringComparison.OrdinalIgnoreCase)) ?? UnknownSwissPlace;
    }

    /// <summary>The regime with this code (any case); <c>null</c> when the catalog has none.</summary>
    public static GuestRegistrationRegime? Find(string? code)
    {
        var wanted = code?.Trim();
        return string.IsNullOrEmpty(wanted) ? null : All.FirstOrDefault(r => string.Equals(r.Code, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The resource key of the sentence of a regime: <c>GuestRegime_CH_TI_Summary</c>.</summary>
    public static string SummaryKeyOf(string code) => $"GuestRegime_{code.Replace('-', '_')}_Summary";

    /// <summary>The resource key of the deadline note of a regime that has one of its own: <c>GuestRegime_CH_SH_Deadline</c>.</summary>
    public static string DeadlineKeyOf(string code) => $"GuestRegime_{code.Replace('-', '_')}_Deadline";

    // ---------------------------------------------------------------------------------------------------------------------------------------
    // Italy and the unknown Swiss place

    private static GuestRegistrationRegime Italy() => new()
    {
        Code = ItalyCode,
        CountryCode = "IT",
        Obligation = GuestRegistrationObligation.ReportToAuthority,
        Channel = GuestRegistrationChannel.PortalFileUpload,
        DeadlineKind = GuestRegistrationDeadlineKind.Hours24FromArrival,
        DeadlineNoteKey = DeadlineKeyOf(ItalyCode),
        PrivateRentals = GuestRegistrationPrivateRentals.Yes,
        PortalUrl = "https://alloggiatiweb.poliziadistato.it",
        LegalReferences = ["Art. 109 TULPS (R.D. 773/1931), comma 3", "D.M. Interno 7 gennaio 2013, modificato dal D.M. Interno 16 settembre 2021"],
        Sources = [new("https://alloggiatiweb.poliziadistato.it", "Alloggiati Web - Polizia di Stato")],
        VerifiedOn = new DateOnly(2026, 10, 1),
        Certainty = GuestRegistrationCertainty.Verified,
        CasaZenSupport = GuestRegistrationSupport.FileExport,
        RetainDocumentImages = true,
        SummaryKey = SummaryKeyOf(ItalyCode),
    };

    private static GuestRegistrationRegime UnknownSwiss() => new()
    {
        Code = UnknownSwissCode,
        CountryCode = "CH",
        Obligation = GuestRegistrationObligation.Unknown,
        Channel = GuestRegistrationChannel.None,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineNotVerifiedKey,
        PrivateRentals = GuestRegistrationPrivateRentals.Unknown,
        LegalReferences = ["LStrI art. 16", "OASA art. 18"],
        Sources =
        [
            new("https://www.bwo.admin.ch/fr/base-juridique-obligation-de-declarer-les-hotes-etrangers", "Base juridique de l'obligation de déclarer les hôtes étrangers - Office fédéral du logement"),
            new("https://www.admin.ch/de/newnsb/STwblroLhJ4-", "Digitalisierung des Meldewesens in der Beherbergung - Consiglio federale, 26.8.2026"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Unverified,
        CasaZenSupport = GuestRegistrationSupport.InformationOnly,
        SummaryKey = SummaryKeyOf(UnknownSwissCode),
    };

    // ---------------------------------------------------------------------------------------------------------------------------------------
    // The 26 cantons, by code. ricerca-ovest.md: FR, GE, JU, NE, VD, VS. ricerca-centro.md: BE, GL, LU, NW, OW, SZ, UR, ZG, ZH.
    // ricerca-nordest.md: AG, AI, AR, BL, BS, GR, SG, SH, SO, TG. specifica-ticino.md: TI.

    private static GuestRegistrationRegime Aargau() => new()
    {
        Code = "CH-AG",
        CountryCode = "CH",
        CantonCode = "AG",
        Obligation = GuestRegistrationObligation.KeepRegister,
        Channel = GuestRegistrationChannel.None,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineKeyOf("CH-AG"),
        RetentionYears = 5,
        PrivateRentals = GuestRegistrationPrivateRentals.Unknown,
        LegalReferences = ["Gastgewerbegesetz (GGG, SAR 970.100) § 7", "Gastgewerbeverordnung (GGV, SAR 970.111) § 21"],
        Sources =
        [
            new("https://www.ag.ch/de/themen/gesundheit/lebensmittel-und-umweltsicherheit/lebensmittel/gastgewerberecht", "Gastgewerberecht - Kanton Aargau"),
            new("https://gesetzessammlungen.ag.ch/app/de/texts_of_law/970.100", "Gastgewerbegesetz (GGG, SAR 970.100)"),
            new("https://gesetzessammlungen.ag.ch/app/de/texts_of_law/970.111", "Gastgewerbeverordnung (GGV, SAR 970.111)"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.GuestRegister,
        SummaryKey = SummaryKeyOf("CH-AG"),
    };

    private static GuestRegistrationRegime AppenzellInnerrhoden() => new()
    {
        Code = "CH-AI",
        CountryCode = "CH",
        CantonCode = "AI",
        Obligation = GuestRegistrationObligation.Optional,
        Channel = GuestRegistrationChannel.None,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineNoSubmissionKey,
        PrivateRentals = GuestRegistrationPrivateRentals.Yes,
        LegalReferences = ["Gastgewerbegesetz (GS 935.300) art. 40 cpv. 2", "LStrI art. 16"],
        Sources = [new("https://ai.clex.ch/api/de/versions/2108/pdf_file", "Gastgewerbegesetz Appenzello Interno (GS 935.300, stato 1.1.2021)")],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Verified,
        CasaZenSupport = GuestRegistrationSupport.GuestRegister,
        SummaryKey = SummaryKeyOf("CH-AI"),
    };

    private static GuestRegistrationRegime AppenzellAusserrhoden() => new()
    {
        Code = "CH-AR",
        CountryCode = "CH",
        CantonCode = "AR",
        Obligation = GuestRegistrationObligation.KeepRegister,
        Channel = GuestRegistrationChannel.None,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineNoSubmissionKey,
        RetentionYears = 5,
        PrivateRentals = GuestRegistrationPrivateRentals.Unknown,
        LegalReferences = ["Gastgewerbegesetz (bGS 955.11) art. 12", "LStrI art. 16"],
        Sources =
        [
            new(
                "https://ar.ch/verwaltung/departement-bau-und-volkswirtschaft/amt-fuer-wirtschaft-und-arbeit/tourismus/tourismusabgabe/",
                "Tourismusabgabe - Amt für Wirtschaft und Arbeit Appenzello Esterno"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.GuestRegister,
        SummaryKey = SummaryKeyOf("CH-AR"),
    };

    private static GuestRegistrationRegime Bern() => new()
    {
        Code = "CH-BE",
        CountryCode = "CH",
        CantonCode = "BE",
        Obligation = GuestRegistrationObligation.KeepRegister,
        Channel = GuestRegistrationChannel.None,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineNotVerifiedKey,
        RetentionYears = 5,
        PrivateRentals = GuestRegistrationPrivateRentals.Unknown,
        LegalReferences = ["Gastgewerbegesetz (GGG, BSG 935.11) art. 24", "Polizeigesetz (PolG, BSG 551.1) art. 129", "Polizeiverordnung (PolV, BSG 551.111) art. 58"],
        Sources =
        [
            new("https://www.belex.sites.be.ch/app/de/texts_of_law/935.11", "Gastgewerbegesetz (GGG, BSG 935.11)"),
            new("https://www.belex.sites.be.ch/app/de/texts_of_law/551.1", "Polizeigesetz (PolG, BSG 551.1)"),
            new("https://www.belex.sites.be.ch/app/de/texts_of_law/551.111/versions/2756?all_languages=true&diff=split", "Polizeiverordnung (PolV, BSG 551.111)"),
            new(
                "https://www.rrgr-service.apps.be.ch/api/rr/documents/document/fd0632bdbc6c43d2ba0d520f2b40cc64-332/2/Vortrag-16.08.2023-de.pdf",
                "Vortrag des Regierungsrates, 16.08.2023"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.GuestRegister,
        SummaryKey = SummaryKeyOf("CH-BE"),
    };

    private static GuestRegistrationRegime BaselLandschaft() => new()
    {
        Code = "CH-BL",
        CountryCode = "CH",
        CantonCode = "BL",
        Obligation = GuestRegistrationObligation.KeepRegister,
        Channel = GuestRegistrationChannel.Paper,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineNotVerifiedKey,
        RetentionYears = 10,
        PrivateRentals = GuestRegistrationPrivateRentals.Unknown,
        LegalReferences = ["Gastgewerbegesetz (SGS 540) § 17"],
        Sources =
        [
            new(
                "https://www.baselland-tourismus.ch/assets/content/files/informationen-gasttaxe/FAQ-TourX-und-Gasttaxengesetz-Baselland-Stand-13.11.24.pdf",
                "FAQ sulla tassa di soggiorno - Baselland Tourismus, 13.11.2024"),
            new("https://bl.clex.ch/app/de/texts_of_law/548", "Gasttaxengesetz (SGS 548)"),
            new("https://bl.clex.ch/api/de/versions/1009/pdf_file", "Testo di legge consolidato del 2014 (bl.clex.ch)"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.GuestRegister,
        SummaryKey = SummaryKeyOf("CH-BL"),
    };

    private static GuestRegistrationRegime BaselStadt() => new()
    {
        Code = "CH-BS",
        CountryCode = "CH",
        CantonCode = "BS",
        Obligation = GuestRegistrationObligation.ReportToAuthority,
        Channel = GuestRegistrationChannel.PortalForm,
        DeadlineKind = GuestRegistrationDeadlineKind.DailyByTime,
        DeadlineNoteKey = DeadlineKeyOf("CH-BS"),
        PrivateRentals = GuestRegistrationPrivateRentals.Unknown,
        LegalReferences = ["Gastgewerbegesetz (SG 563.100)", "LStrI art. 16"],
        Sources =
        [
            new(
                "https://www.bs.ch/medienmitteilungen/wsu/2021-elektronisches-logiernaechtemanagement-basel-stadt-erfolgreich-eingefuehrt",
                "Elektronisches Logiernächtemanagement Basel-Stadt erfolgreich eingeführt - comunicato del cantone, 2021"),
            new("https://www.gesetzessammlung.bs.ch/app/de/texts_of_law/563.100", "Gastgewerbegesetz (SG 563.100)"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.AssistedNotification,
        SummaryKey = SummaryKeyOf("CH-BS"),
    };

    private static GuestRegistrationRegime Fribourg() => new()
    {
        Code = "CH-FR",
        CountryCode = "CH",
        CantonCode = "FR",
        Obligation = GuestRegistrationObligation.ReportToAuthority,
        Channel = GuestRegistrationChannel.PortalForm,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineNotVerifiedKey,
        PrivateRentals = GuestRegistrationPrivateRentals.Unknown,
        PortalUrl = "https://www.checkin-fr.ch",
        LegalReferences = ["LEPu (RSF 952.1)", "Legge sul turismo (senza articolo)"],
        Sources =
        [
            new("https://www.checkin-fr.ch", "Piattaforma di registrazione dell'Unione fribourgeoise du tourisme"),
            new("https://fribourg.ch/fr/uft-ftv/checkin-fr-ts/", "Piattaforma di registrazione - Union fribourgeoise du tourisme"),
            new("https://fribourg.ch/fr/uft-ftv/faq/", "FAQ - Union fribourgeoise du tourisme"),
            new("https://fribourg.ch/fr/uft-ftv/base-legale/", "Base légale - Union fribourgeoise du tourisme"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.AssistedNotification,
        SummaryKey = SummaryKeyOf("CH-FR"),
    };

    private static GuestRegistrationRegime Geneva() => new()
    {
        Code = "CH-GE",
        CountryCode = "CH",
        CantonCode = "GE",
        Obligation = GuestRegistrationObligation.ReportToAuthority,
        Channel = GuestRegistrationChannel.PortalForm,
        DeadlineKind = GuestRegistrationDeadlineKind.AtArrival,
        DeadlineNoteKey = DeadlineKeyOf("CH-GE"),
        PrivateRentals = GuestRegistrationPrivateRentals.Partial,
        PortalUrl = "https://www.ge.ch/louer-mon-logement-plateforme-hebergement-type-airbnb/annoncer-hebergement-personne-etrangere",
        LegalReferences = ["LRDBHD (rsGE I 2 22) art. 32", "LStrI art. 16"],
        Sources =
        [
            new(
                "https://www.ge.ch/louer-mon-logement-plateforme-hebergement-type-airbnb/annoncer-hebergement-personne-etrangere",
                "Annoncer un hébergement à une personne étrangère - République et canton de Genève"),
            new("https://www.ge.ch/inscription-aux-e-demarches/inscription-particuliers", "Inscription aux e-démarches (particuliers)"),
            new("https://silgeneve.ch/legis/data/rsg_i2_22.htm", "LRDBHD (rsGE I 2 22)"),
            new("https://silgeneve.ch/legis/data/rsg_i2_22p01.htm", "Règlement d'application de la LRDBHD (rsGE I 2 22.01)"),
            new("https://www.bwo.admin.ch/fr/base-juridique-obligation-de-declarer-les-hotes-etrangers", "Base juridique de l'obligation de déclarer les hôtes étrangers - Office fédéral du logement"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.AssistedNotification,
        SummaryKey = SummaryKeyOf("CH-GE"),
    };

    private static GuestRegistrationRegime Glarus() => new()
    {
        Code = "CH-GL",
        CountryCode = "CH",
        CantonCode = "GL",
        Obligation = GuestRegistrationObligation.KeepRegister,
        Channel = GuestRegistrationChannel.None,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineNoSubmissionKey,
        RetentionYears = 3,
        PrivateRentals = GuestRegistrationPrivateRentals.Unknown,
        LegalReferences = ["Gastgewerbegesetz (GS IX B/22/1) art. 21", "Gastgewerbeverordnung (GGV, SBE 2023-04)"],
        Sources =
        [
            new("https://gesetze.gl.ch/app/de/texts_of_law/IX-B.22.1", "Gastgewerbegesetz (GS IX B/22/1)"),
            new("https://gesetze.gl.ch/app/de/change_documents/545", "Gastgewerbeverordnung (GGV)"),
            new(
                "https://www.glarus.ch/public/upload/assets/48353/2023_10_14_u_Merkblatt_Kurtaxe_Gemeinde_Glarus_aktualisiert.pdf?fp=1",
                "Merkblatt Kurtaxe - Gemeinde Glarus, 14.10.2023"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.GuestRegister,
        SummaryKey = SummaryKeyOf("CH-GL"),
    };

    private static GuestRegistrationRegime Graubuenden() => new()
    {
        Code = "CH-GR",
        CountryCode = "CH",
        CantonCode = "GR",
        Obligation = GuestRegistrationObligation.KeepRegister,
        Channel = GuestRegistrationChannel.Paper,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineNoSubmissionKey,
        RetentionYears = 1,
        PrivateRentals = GuestRegistrationPrivateRentals.Unknown,
        LegalReferences = ["Ausführungsbestimmungen zum Gastwirtschaftsgesetz (BR 945.110) art. 3, 5, 9 e 10"],
        Sources =
        [
            new("https://www.gr-lex.gr.ch/app/de/texts_of_law/945.110", "Ausführungsbestimmungen zum Gastwirtschaftsgesetz (BR 945.110)"),
            new("https://www.gr.ch/DE/Medien/Mitteilungen/MMStaka/2006/Seiten/DE_19649.aspx", "Comunicato del Governo dei Grigioni, 2006"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.GuestRegister,
        SummaryKey = SummaryKeyOf("CH-GR"),
    };

    private static GuestRegistrationRegime Jura() => new()
    {
        Code = "CH-JU",
        CountryCode = "CH",
        CantonCode = "JU",
        Obligation = GuestRegistrationObligation.KeepRegister,
        Channel = GuestRegistrationChannel.None,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineNotVerifiedKey,
        PrivateRentals = GuestRegistrationPrivateRentals.No,
        LegalReferences = ["RSJU 935.11 art. 31 e 32 (legge sull'albergheria, la ristorazione e il commercio di bevande)"],
        Sources =
        [
            new("https://rsju.jura.ch/fr/viewdocument.html?idn=20207&id=34161&v=1&Download=1", "Legge sull'albergheria, la ristorazione e il commercio di bevande (RSJU 935.11)"),
            new(
                "https://www.jura.ch/fr/Autorites/Administration/DSJP/POC/Contact-Annuaire-Bulletin-d-hotel-Amendes/Annuaire-de-la-police-cantonale-jurassienne.html",
                "Bulletin d'hôtel - Police cantonale jurassienne"),
            new(
                "https://www.jura.ch/Htdocs/Files/v/7307352009d063f7688bb2def599e77cf22c40e1a35b8be86245e063dfedebd7.pdf/Bulletins_Hotels.pdf",
                "Bulletin d'hôtel / Annonce d'hôtes (modulo)"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.GuestRegister,
        SummaryKey = SummaryKeyOf("CH-JU"),
    };

    private static GuestRegistrationRegime Lucerne() => new()
    {
        Code = "CH-LU",
        CountryCode = "CH",
        CantonCode = "LU",
        Obligation = GuestRegistrationObligation.KeepRegister,
        Channel = GuestRegistrationChannel.None,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineNoSubmissionKey,
        RetentionYears = 5,
        PrivateRentals = GuestRegistrationPrivateRentals.Partial,
        LegalReferences = ["Verordnung zum Gastgewerbegesetz (SRL 981) § 22a", "Gastgewerbegesetz (SRL 980) § 20"],
        Sources =
        [
            new("https://polizei.lu.ch/organisation/verwaltungspolizei/gastgewerbe_und_gewerbepolizei/Gaestekontrolle", "Gästekontrolle - Luzerner Polizei"),
            new("https://polizei.lu.ch/dienstleistungen/downloads/Downloads_Hotelmeldewesen", "Downloads Hotelmeldewesen - Luzerner Polizei"),
            new("https://www.lexfind.ch/tolv/57136/de", "Verordnung zum Gastgewerbegesetz (SRL 981)"),
            new("https://www.stadtluzern.ch/aktuelles/newslist/357914", "Online-Plattformen und Meldeschein - Stadt Luzern"),
            new("https://www.lu.ch/-/media/Polizei/Dokumente/05_Dienstleistungen/Downloads/Hotelmeldewesen/Hotelmeldeschein.pdf", "Meldeschein / Bulletin d'arrivée (modulo)"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.GuestRegister,
        SummaryKey = SummaryKeyOf("CH-LU"),
    };

    private static GuestRegistrationRegime Neuchatel() => new()
    {
        Code = "CH-NE",
        CountryCode = "CH",
        CantonCode = "NE",
        Obligation = GuestRegistrationObligation.ReportToAuthority,
        Channel = GuestRegistrationChannel.PortalForm,
        DeadlineKind = GuestRegistrationDeadlineKind.DailyByTime,
        DeadlineNoteKey = DeadlineKeyOf("CH-NE"),
        PrivateRentals = GuestRegistrationPrivateRentals.Partial,
        PortalUrl = "https://www.guichetunique.ch/",
        LegalReferences = ["RCom (RSN 941.010) art. 20 e 22", "Rapporto 23.003 del Consiglio di Stato (15.2.2023)"],
        Sources =
        [
            new("https://rsn.ne.ch/DATA/program/books/rsne/pdf/941010.pdf", "Regolamento di polizia del commercio (RCom, RSN 941.010)"),
            new("https://www.ne.ch/autorites/GC/objets/Documents/Rapports/2023/23003_CE.pdf", "Rapporto 23.003 del Consiglio di Stato, 15.2.2023"),
            new(
                "https://www.ppdt-june.ch/fr/Activites/Avis/Protection-des-donnees/2013/Recolte-des-fiches-d-hotel-20130514.html",
                "Avis sur la récolte des fiches d'hôtel - PPDT Jura-Neuchâtel, 2013"),
            new("https://www.guichetunique.ch/", "Guichet unique"),
            new("https://www.guichetunique.ch/public/Services/Professional/Hotel.aspx", "Guichet unique - Bulletins d'hôtel"),
            new("https://www.guichetunique.ch/public/Services/Professional/ProfessionalServices.aspx", "Guichet unique - Prestations pour les professionnels"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.AssistedNotification,
        SummaryKey = SummaryKeyOf("CH-NE"),
    };

    private static GuestRegistrationRegime Nidwalden() => new()
    {
        Code = "CH-NW",
        CountryCode = "CH",
        CantonCode = "NW",
        Obligation = GuestRegistrationObligation.ReportToAuthority,
        Channel = GuestRegistrationChannel.Paper,
        DeadlineKind = GuestRegistrationDeadlineKind.Weekly,
        DeadlineNoteKey = DeadlineKeyOf("CH-NW"),
        PrivateRentals = GuestRegistrationPrivateRentals.Partial,
        LegalReferences = ["Gastgewerbegesetz (GGG, NG 854.1) art. 29", "OASA art. 18", "Convenzione di applicazione dell'Accordo di Schengen art. 45"],
        Sources =
        [
            new("https://www.nidwalden.com/de/ueber-uns/partnerseite/checkliste-ferienwohnung-vermieten", "Checkliste Ferienwohnung vermieten - Nidwalden Tourismus"),
            new("https://www.nw.ch/_docn/153580/13_NG_854.1_Gastgewerbegesetz_definitiv.pdf", "Gastgewerbegesetz (GGG, NG 854.1)"),
            new("https://gesetze.nw.ch/app/de/texts_of_law/854.1", "Gastgewerbegesetz (GGG, NG 854.1) - raccolta delle leggi"),
            new("https://gesetze.nw.ch/app/de/texts_of_law/265.51", "Tariffa dei moduli di notifica (NG 265.51)"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.AssistedNotification,
        SummaryKey = SummaryKeyOf("CH-NW"),
    };

    private static GuestRegistrationRegime Obwalden() => new()
    {
        Code = "CH-OW",
        CountryCode = "CH",
        CantonCode = "OW",
        Obligation = GuestRegistrationObligation.KeepRegister,
        Channel = GuestRegistrationChannel.None,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineNotVerifiedKey,
        PrivateRentals = GuestRegistrationPrivateRentals.Unknown,
        LegalReferences = ["Tourismusgesetz (GDB 971.3) art. 22"],
        Sources =
        [
            new("https://www.ow.ch/_docn/455992/PolG_20260824_Aenderungsantraege_der_Redaktionskommission.pdf", "Revisione del Polizeigesetz: proposte della commissione di redazione, 24.8.2026"),
            new("https://gdb.ow.ch/app/de/texts_of_law/971.3", "Tourismusgesetz (GDB 971.3)"),
            new("https://gdb.ow.ch/app/de/texts_of_law/510.1/versions/921", "Polizeigesetz (GDB 510.1)"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.GuestRegister,
        SummaryKey = SummaryKeyOf("CH-OW"),
    };

    private static GuestRegistrationRegime StGallen() => new()
    {
        Code = "CH-SG",
        CountryCode = "CH",
        CantonCode = "SG",
        Obligation = GuestRegistrationObligation.KeepRegister,
        Channel = GuestRegistrationChannel.PortalForm,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineNoSubmissionKey,
        RetentionYears = 5,
        PrivateRentals = GuestRegistrationPrivateRentals.Unknown,
        LegalReferences = ["Polizeigesetz (PolG) art. 52bis", "LStrI art. 16", "OASA art. 18"],
        Sources = [new("https://partner.st.gallen-bodensee.ch/de/dienstleistungen/digitales-meldewesen.html", "Digitales Meldewesen - St.Gallen-Bodensee Tourismus")],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.GuestRegister,
        SummaryKey = SummaryKeyOf("CH-SG"),
    };

    private static GuestRegistrationRegime Schaffhausen() => new()
    {
        Code = "CH-SH",
        CountryCode = "CH",
        CantonCode = "SH",
        Obligation = GuestRegistrationObligation.ReportToAuthority,
        Channel = GuestRegistrationChannel.PortalForm,
        DeadlineKind = GuestRegistrationDeadlineKind.DailyByTime,
        DeadlineNoteKey = DeadlineKeyOf("CH-SH"),
        PrivateRentals = GuestRegistrationPrivateRentals.Yes,
        PortalUrl = "https://gast.sh.ch",
        LegalReferences = ["Gastgewerbegesetz (SHR 935.100) art. 18", "Registerverordnung (SHR 354.112) § 9", "LStrI art. 16"],
        Sources =
        [
            new("https://sh.ch/CMS/get/file/96725471-280e-4db5-9f69-bc493fbfa8b0", "Elektronisches Gästemeldewesen - brochure della Schaffhauser Polizei, 11/2023"),
            new("https://sh.ch/CMS/Webseite/Schaffhauser-Polizei-13815489-DE.html", "Schaffhauser Polizei"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Verified,
        CasaZenSupport = GuestRegistrationSupport.AssistedNotification,
        SummaryKey = SummaryKeyOf("CH-SH"),
    };

    private static GuestRegistrationRegime Solothurn() => new()
    {
        Code = "CH-SO",
        CountryCode = "CH",
        CantonCode = "SO",
        Obligation = GuestRegistrationObligation.KeepRegister,
        Channel = GuestRegistrationChannel.None,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineNoSubmissionKey,
        RetentionYears = 3,
        PrivateRentals = GuestRegistrationPrivateRentals.Partial,
        LegalReferences = ["Wirtschafts- und Arbeitsgesetz (WAG, BGS 940.11) § 18"],
        Sources =
        [
            new("https://so.ch/services/gastgewerbe-betriebsbewilligung-beantragen/", "Gastgewerbe: Betriebsbewilligung beantragen - Kanton Solothurn"),
            new("https://bgs.so.ch/app/de/texts_of_law/940.11", "Wirtschafts- und Arbeitsgesetz (WAG, BGS 940.11)"),
            new("https://so.ch/fileadmin/internet/vwd/vwd-awa/Arbeitsbedingungen/AI_doc/Meldeschein_fuer_Beherbergungsbetriebe.xlsx", "Meldeschein für Beherbergungsbetriebe (modello)"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.GuestRegister,
        SummaryKey = SummaryKeyOf("CH-SO"),
    };

    private static GuestRegistrationRegime Schwyz() => new()
    {
        Code = "CH-SZ",
        CountryCode = "CH",
        CantonCode = "SZ",
        Obligation = GuestRegistrationObligation.KeepRegister,
        Channel = GuestRegistrationChannel.None,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineNoSubmissionKey,
        RetentionYears = 2,
        PrivateRentals = GuestRegistrationPrivateRentals.Partial,
        LegalReferences = ["LStrI art. 16", "OASA art. 18"],
        Sources =
        [
            new("https://www.sz.ch/public/upload/assets/59716/I_AirBnB.pdf", "Risposta del Consiglio di Stato, RRB 144/2022"),
            new(
                "https://www.sz.ch/public/upload/assets/66363/Auslegungshilfe_Gastgewerbe_und_Handel_mit_alkoholischen_Getraenken_im_Kanton_Schwyz.pdf",
                "Auslegungshilfe Gastgewerbe und Handel mit alkoholischen Getränken im Kanton Schwyz"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.GuestRegister,
        SummaryKey = SummaryKeyOf("CH-SZ"),
    };

    private static GuestRegistrationRegime Thurgau() => new()
    {
        Code = "CH-TG",
        CountryCode = "CH",
        CantonCode = "TG",
        Obligation = GuestRegistrationObligation.ReportToAuthority,
        Channel = GuestRegistrationChannel.EmailFile,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineNotVerifiedKey,
        RetentionYears = 5,
        PrivateRentals = GuestRegistrationPrivateRentals.Unknown,
        LegalReferences = ["Gastgewerbegesetz (RB 554.51) § 18", "Ordinanza sul trattamento dei dati della polizia (RB 551.6) § 12"],
        Sources =
        [
            new("https://partner.thurgau-bodensee.ch/de/nachrichten/kurtaxe-meldepflicht.html", "Kurtaxe und Meldepflicht - Thurgau Bodensee Tourismus"),
            new("https://www.rechtsbuch.tg.ch/app/de/texts_of_law/554.51", "Gastgewerbegesetz (RB 554.51)"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.AssistedNotification,
        SummaryKey = SummaryKeyOf("CH-TG"),
    };

    private static GuestRegistrationRegime Ticino() => new()
    {
        Code = TicinoCode,
        CountryCode = "CH",
        CantonCode = "TI",
        Obligation = GuestRegistrationObligation.ReportToAuthority,
        Channel = GuestRegistrationChannel.PortalFileUpload,
        DeadlineKind = GuestRegistrationDeadlineKind.Hours24FromArrival,
        RetentionYears = 5,
        PrivateRentals = GuestRegistrationPrivateRentals.Yes,
        PortalUrl = "https://www.notifiche.ti.ch",
        LegalReferences = ["Legge sugli esercizi alberghieri e sulla ristorazione (LEAR)", "LTur art. 21a (numero identificativo della struttura)"],
        Sources =
        [
            new("https://www4.ti.ch/di/pol-new/servizi-online/notifiche-degli-ospiti-alla-polizia-no-lear", "Notifiche degli ospiti alla Polizia (NO LEAR) - Cantone Ticino"),
            new("https://www4.ti.ch/fileadmin/DI/POLIZIA/documentazione/autorizzazioni/autorizzazioni_e_permessi/Manuale_Utente.pdf", "Manuale utente «Notifiche» v1.4, 18.02.2025 - Centro sistemi informativi"),
            new("https://www4.ti.ch/index.php?id=145950", "Caricamento di massa e modelli - Polizia cantonale"),
            new("https://www4.ti.ch/di/pol-new/servizi-online/importare-i-pernottamenti", "Importare i pernottamenti - Polizia cantonale"),
            new("https://www.ticino.ch/it/partners/alloggi/notifica-numero-identificativo.html", "Numero identificativo delle strutture - Ticino Turismo"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Verified,
        CasaZenSupport = GuestRegistrationSupport.FileExport,
        SummaryKey = SummaryKeyOf(TicinoCode),
    };

    private static GuestRegistrationRegime UriCanton() => new()
    {
        Code = "CH-UR",
        CountryCode = "CH",
        CantonCode = "UR",
        Obligation = GuestRegistrationObligation.KeepRegister,
        Channel = GuestRegistrationChannel.None,
        DeadlineKind = GuestRegistrationDeadlineKind.AtArrival,
        PrivateRentals = GuestRegistrationPrivateRentals.Unknown,
        LegalReferences = ["Gastwirtschaftsgesetz (GWG, RB 70.2111) art. 9 cpv. 2", "GWG art. 22 lett. b"],
        Sources =
        [
            new("https://rechtsbuch.ur.ch/app/de/texts_of_law/3.9223", "Ordnungsbussenreglement (RB 3.9223)"),
            new("https://www.ur.ch/_docn/408382/LA.2020-0467_I._T%C3%A4tigkeitsbericht_Datenschutzstell.pdf", "Tätigkeitsbericht dell'ufficio per la protezione dei dati, 2020"),
            new("https://www.ur.ch/_docn/198146/Merkblatt_fur_Gastwirtschaftsbetriebe_Kt._Uri_Stand_13.12.2019_korr.pdf", "Merkblatt für Gastwirtschaftsbetriebe (stato 13.12.2019)"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.GuestRegister,
        SummaryKey = SummaryKeyOf("CH-UR"),
    };

    private static GuestRegistrationRegime Vaud() => new()
    {
        Code = "CH-VD",
        CountryCode = "CH",
        CantonCode = "VD",
        Obligation = GuestRegistrationObligation.KeepRegister,
        Channel = GuestRegistrationChannel.None,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineKeyOf("CH-VD"),
        PrivateRentals = GuestRegistrationPrivateRentals.Yes,
        LegalReferences = ["LEAE (BLV 930.01) art. 4a, 74c, 74d e 89", "RLPPPL art. 15 al. 2"],
        Sources =
        [
            new("https://www.vd.ch/economie/police-cantonale-du-commerce/informations-relatives-aux-locations-de-type-airbnb", "Informations relatives aux locations de courte durée - Police cantonale du commerce"),
            new("https://www.vd.ch/territoire-et-construction/logement/hebergement-airbnb", "Hébergement de courte durée - Logement, Canton de Vaud"),
            new("https://prestations.vd.ch/pub/blv-publication/actes/consolide/930.01", "Legge sull'esercizio delle attività economiche (LEAE, BLV 930.01)"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Verified,
        CasaZenSupport = GuestRegistrationSupport.GuestRegister,
        SummaryKey = SummaryKeyOf("CH-VD"),
    };

    private static GuestRegistrationRegime Valais() => new()
    {
        Code = "CH-VS",
        CountryCode = "CH",
        CantonCode = "VS",
        Obligation = GuestRegistrationObligation.KeepRegister,
        Channel = GuestRegistrationChannel.Paper,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineNotVerifiedKey,
        PrivateRentals = GuestRegistrationPrivateRentals.Partial,
        LegalReferences =
        [
            "LHR (RS 935.3) art. 15 e art. 3 al. 2 lett. a (legge sull'alloggio, la ristorazione e il commercio al dettaglio di bevande alcoliche)",
            "Legge sulla polizia del commercio (RS 930.1)",
            "Legge sul turismo (RS 935.1)",
        ],
        Sources =
        [
            new("https://lex.vs.ch/app/fr/texts_of_law/935.3", "Legge sull'alloggio, la ristorazione e il commercio al dettaglio di bevande alcoliche (LHR, RS 935.3)"),
            new("https://lex.vs.ch/api/fr/versions/3439/pdf_file", "LHR (RS 935.3), testo consolidato in PDF"),
            new("https://www.vs.ch/en/web/sict/lhr", "LHR - Service de l'industrie, du commerce et du travail"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.GuestRegister,
        SummaryKey = SummaryKeyOf("CH-VS"),
    };

    private static GuestRegistrationRegime Zug() => new()
    {
        Code = "CH-ZG",
        CountryCode = "CH",
        CantonCode = "ZG",
        Obligation = GuestRegistrationObligation.ReportToAuthority,
        Channel = GuestRegistrationChannel.PortalForm,
        DeadlineKind = GuestRegistrationDeadlineKind.AtArrival,
        DeadlineNoteKey = DeadlineKeyOf("CH-ZG"),
        PrivateRentals = GuestRegistrationPrivateRentals.Yes,
        PortalUrl = "https://gastmeldeschein.zg.ch/",
        LegalReferences = ["Gastgewerbegesetz (BGS 943.11) § 16", "Verordnung über Datenbearbeitungssysteme für die Polizei (BGS 512.15) § 11"],
        Sources =
        [
            new("https://zg.ch/de/sicherheit/polizei/dienstleistungen/gastmeldeschein", "Gastmeldeschein - Zuger Polizei"),
            new("https://gastmeldeschein.zg.ch/", "Gastmeldeschein - portale della Zuger Polizei"),
            new("https://bgs.zg.ch/app/de/texts_of_law/512.15", "Verordnung über Datenbearbeitungssysteme für die Polizei (BGS 512.15)"),
            new("https://bgs.zg.ch/app/de/texts_of_law/943.11", "Gastgewerbegesetz (BGS 943.11)"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Verified,
        CasaZenSupport = GuestRegistrationSupport.AssistedNotification,
        SummaryKey = SummaryKeyOf("CH-ZG"),
    };

    private static GuestRegistrationRegime Zurich() => new()
    {
        Code = "CH-ZH",
        CountryCode = "CH",
        CantonCode = "ZH",
        Obligation = GuestRegistrationObligation.ReportToAuthority,
        Channel = GuestRegistrationChannel.VendorInterface,
        DeadlineKind = GuestRegistrationDeadlineKind.None,
        DeadlineNoteKey = DeadlineNotVerifiedKey,
        RetentionYears = 3,
        PrivateRentals = GuestRegistrationPrivateRentals.Partial,
        LegalReferences = ["Polizeigesetz (PolG, LS 550.1) § 21 cpv. 4 e 5", "POLIS-Verordnung (LS 551.103) § 7b e § 18 cpv. 5 lett. r", "LStrI art. 16"],
        Sources =
        [
            new("https://www.bwo.admin.ch/dam/de/sd-web/4LKnAP3IVAhG/merkblatt-rechtliche-informationen-fuer-beherbergende.pdf", "Merkblatt: rechtliche Informationen für Beherbergende - Bundesamt für Wohnungswesen"),
            new("https://www.stadt-zuerich.ch/de/stadtleben/veranstaltungen-und-bewilligungen/gastgewerbe/fuer-beherbergende.html", "Für Beherbergende - Stadt Zürich"),
            new("https://www.zh.ch/bin/zhweb/publish/regierungsratsbeschluss-unterlagen./2012/315/4884_Polizeigesetz.pdf", "Polizeigesetz: Vorlage 4884 - Kanton Zürich"),
            new("https://www.notes.zh.ch/appl/zhlex_r.nsf/WebView/072E9C91CCE786F1C1257CB7001E24FC/$File/551.103.pdf", "POLIS-Verordnung (LS 551.103), versione 2014"),
        ],
        VerifiedOn = ResearchDay,
        Certainty = GuestRegistrationCertainty.Partial,
        CasaZenSupport = GuestRegistrationSupport.AssistedNotification,
        SummaryKey = SummaryKeyOf("CH-ZH"),
    };
}
