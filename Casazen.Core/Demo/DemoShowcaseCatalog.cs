using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Demo;

/// <summary>
/// The showcase a client walks through. Every feature that adds a screen, an entity or a flow a host or a guest can
/// see adds a scenario here, in the same change, and extends <see cref="Scenarios"/>. The rule is in
/// <c>Sessions/specs/_TEMPLATE.md</c> (Demo seed) and <c>.claude/rules/demo-seed.md</c>.
/// </summary>
/// <remarks>
/// Places use the ISTAT codes the rest of the product already treats as examples (Roma <c>058091</c>) or the official
/// codes of Como and Milano. At reset the comuni table wins when the city is in it, so the region and the required
/// documents stay the ones of that comune.
/// </remarks>
public static class DemoShowcaseCatalog
{
    public const string Version = "2026-10-12.1";

    /// <summary>The body of a reset must carry this word. It is not a password: it only stops an accidental click.</summary>
    public const string ResetConfirmation = "RESET";

    public const string DraftKey = "monolocale-bozza";

    public static class Scenario
    {
        public const string PublishedLombardia = "short-stay-published-lombardia";
        public const string PublishedLazio = "short-stay-published-lazio";
        public const string ActivationPending = "short-stay-activation-pending";
        public const string LongStayCadastral = "long-stay-cadastral";
        public const string BookingUpcoming = "booking-upcoming";
        public const string BookingPast = "booking-past";
    }

    /// <summary>Scenarios the template must cover. A new feature appends its id and a property or booking that carries it.</summary>
    public static IReadOnlyList<string> Scenarios { get; } =
    [
        Scenario.PublishedLombardia,
        Scenario.PublishedLazio,
        Scenario.ActivationPending,
        Scenario.LongStayCadastral,
        Scenario.BookingUpcoming,
        Scenario.BookingPast,
    ];

    public static IReadOnlyList<DemoPropertyTemplate> Properties { get; } =
    [
        new(
            Key: "villa-lago",
            Name: "Villa del Lago (demo)",
            Place: new DemoPlace("Como", "013075", "LOM", "22100", "Via del Lungolago 12"),
            Description: "Annuncio di dimostrazione sul lago. Dati, CIN e documenti sono inventati e segnati come test.",
            Mode: RentalMode.Short,
            Publish: true,
            Bedrooms: 3,
            Bathrooms: 2,
            MaxGuests: 6,
            NightlyRate: 180m,
            CleaningFee: 40m,
            Cadastral: null,
            Scenarios: [Scenario.PublishedLombardia, Scenario.BookingUpcoming, Scenario.BookingPast],
            Bookings:
            [
                new("arrivo", "Giulia", "Bianchi", "ospite.demo.arrivo@example.com", 14, 4, BookingStatus.Confirmed, Scenario.BookingUpcoming),
                new("partito", "Marco", "Rossi", "ospite.demo.partito@example.com", -20, 4, BookingStatus.CheckedOut, Scenario.BookingPast),
            ]),
        new(
            Key: "appartamento-brera",
            Name: "Appartamento Brera (demo)",
            Place: new DemoPlace("Milano", "015146", "LOM", "20121", "Via Brera 18"),
            Description: "Annuncio di dimostrazione a Milano. Dati, CIN e documenti sono inventati e segnati come test.",
            Mode: RentalMode.Short,
            Publish: true,
            Bedrooms: 1,
            Bathrooms: 1,
            MaxGuests: 3,
            NightlyRate: 140m,
            CleaningFee: 30m,
            Cadastral: null,
            Scenarios: [Scenario.PublishedLombardia],
            Bookings: []),
        new(
            Key: "casa-trastevere",
            Name: "Casa Trastevere (demo)",
            Place: new DemoPlace("Roma", "058091", "LAZ", "00153", "Via della Lungaretta 42"),
            Description: "Annuncio di dimostrazione a Roma. Dati, CIN e documenti sono inventati e segnati come test.",
            Mode: RentalMode.Short,
            Publish: true,
            Bedrooms: 2,
            Bathrooms: 1,
            MaxGuests: 4,
            NightlyRate: 160m,
            CleaningFee: 35m,
            Cadastral: null,
            Scenarios: [Scenario.PublishedLazio],
            Bookings: []),
        new(
            Key: "bilocale-navigli",
            Name: "Bilocale Navigli (demo)",
            Place: new DemoPlace("Milano", "015146", "LOM", "20144", "Alzaia Naviglio Grande 14"),
            Description: "Immobile di dimostrazione in locazione lunga, con dati catastali inventati.",
            Mode: RentalMode.Long,
            Publish: false,
            Bedrooms: 1,
            Bathrooms: 1,
            MaxGuests: 0,
            NightlyRate: 0m,
            CleaningFee: 0m,
            Cadastral: new DemoCadastral("12", "345", "6", "A/2", 450m),
            Scenarios: [Scenario.LongStayCadastral],
            Bookings: []),
        new(
            Key: DraftKey,
            Name: "Monolocale in attivazione (demo)",
            Place: new DemoPlace("Como", "013075", "LOM", "22100", "Via Borgovico 4"),
            Description: "Annuncio lasciato in bozza per mostrare il percorso di attivazione. Manca il CIN, i documenti e la checklist.",
            Mode: RentalMode.Short,
            Publish: false,
            Bedrooms: 0,
            Bathrooms: 1,
            MaxGuests: 2,
            NightlyRate: 90m,
            CleaningFee: 20m,
            Cadastral: null,
            Scenarios: [Scenario.ActivationPending],
            Bookings: []),
    ];
}

public sealed record DemoPlace(string City, string IstatCode, string RegionCode, string PostalCode, string Address);

public sealed record DemoCadastral(
    string Sheet,
    string Parcel,
    string Subaltern,
    string Category,
    decimal Income);

public sealed record DemoBookingTemplate(
    string Key,
    string FirstName,
    string LastName,
    string Email,
    int CheckInOffsetDays,
    int Nights,
    BookingStatus Status,
    string Scenario);

public sealed record DemoPropertyTemplate(
    string Key,
    string Name,
    DemoPlace Place,
    string Description,
    RentalMode Mode,
    bool Publish,
    int Bedrooms,
    int Bathrooms,
    int MaxGuests,
    decimal NightlyRate,
    decimal CleaningFee,
    DemoCadastral? Cadastral,
    IReadOnlyList<string> Scenarios,
    IReadOnlyList<DemoBookingTemplate> Bookings);
