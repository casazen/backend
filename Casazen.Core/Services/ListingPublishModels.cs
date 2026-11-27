using Casazen.Core.Entities.Enums;

namespace Casazen.Core.Services;

/// <summary>
/// One channel of a publication, as the endpoint answers it and the status reads it (E02/F03/US01/T02): the code of the portal, where its content
/// stands and the stable code of what is wrong. No text of Octorate and no personal data.
/// </summary>
/// <param name="ChannelCode">The code of the portal (<c>booking_xml</c>).</param>
/// <param name="Status">Where the content stands on the channel.</param>
/// <param name="ErrorCode">A stable code of what went wrong (<see cref="ListingChannelErrors"/>); <c>null</c> while nothing did.</param>
public sealed record ListingChannelResult(string ChannelCode, ChannelPublishStatus Status, string? ErrorCode);

/// <summary>
/// The codes a channel row keeps in <c>ListingChannelState.ErrorCode</c> and the words that go with each (E02/F03/US01/T02, T07, NF-38): the host is
/// told what is wrong in simple words, never a generic «error». The message of a code is a key of <c>SharedResources.resx</c> (Italian and English);
/// the Italian default is stored next to the code in <c>ErrorMessage</c>, for the rows a reader without the resources reads.
/// </summary>
public static class ListingChannelErrors
{
    public const string CinMissing = "listing_cin_missing";

    public const string PhotosBelowMinimum = "listing_photos_below_minimum";

    public const string ContentRejected = "listing_content_rejected";

    public const string ChannelRefused = "listing_channel_refused";

    public const string ChannelPushFailed = "listing_channel_push_failed";

    public const string ChannelNotConnected = "listing_channel_not_connected";

    public const string RoomRequired = "octorate_room_required";

    private static readonly IReadOnlyDictionary<string, (string Key, string Italian)> Catalog = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        [CinMissing] = ("ListingChannelErrorCinMissing", "Per questo canale serve un CIN valido: inseriscilo nei dati dell'immobile."),
        [PhotosBelowMinimum] = ("ListingChannelErrorPhotosBelowMinimum", "Servono più foto per questo canale: aggiungile nel passaggio delle foto."),
        [ContentRejected] = ("ListingChannelErrorContentRejected", "Il channel manager ha rifiutato una parte dei contenuti: controlla i dati dell'annuncio e riprova."),
        [ChannelRefused] = ("ListingChannelErrorChannelRefused", "Il canale non ha accettato i contenuti: correggi quanto indicato e pubblica di nuovo."),
        [ChannelPushFailed] = ("ListingChannelErrorChannelPushFailed", "L'invio dei contenuti al canale non è riuscito: riprova tra poco."),
        [ChannelNotConnected] = ("ListingChannelErrorChannelNotConnected", "L'immobile non è collegato a questo canale: collegalo dalle impostazioni dei canali."),
        [RoomRequired] = ("ListingChannelErrorRoomRequired", "Scegli la camera dell'annuncio nel channel manager prima di pubblicare."),
        ["octorate_unavailable"] = ("OctorateUnavailable", "Il channel manager non risponde in questo momento: riprova tra poco."),
        ["octorate_rate_limited"] = ("OctorateRateLimited", "Troppe richieste al channel manager: riprova tra qualche minuto."),
        ["octorate_auth_failed"] = ("OctorateAuthFailed", "Il channel manager ha rifiutato l'accesso: collega di nuovo l'account dalle impostazioni dei canali."),
        ["octorate_not_connected"] = ("OctorateNotConnected", "Il collegamento con il channel manager non è attivo: collegalo di nuovo dalle impostazioni dei canali."),
        ["octorate_wrong_environment"] = ("OctorateWrongEnvironment", "Le credenziali del channel manager sono di un altro ambiente: collega di nuovo l'account."),
        ["octorate_not_found"] = ("OctorateNotFound", "Il channel manager non trova l'annuncio: controlla il collegamento dell'immobile."),
        ["octorate_conflict"] = ("OctorateConflict", "Il channel manager ha rifiutato la modifica perché contrasta con i suoi dati."),
        ["octorate_network_unknown"] = ("OctorateNetworkUnknown", "Il channel manager non riconosce la rete del tuo account: collega di nuovo l'account dalle impostazioni dei canali."),
        ["octorate_property_invalid"] = ("OctoratePropertyInvalid", "I dati dell'immobile non bastano per crearlo sul channel manager: completa i campi indicati."),
        ["octorate_owner_email_not_configured"] = ("OctorateOwnerEmailNotConfigured", "La creazione dell'immobile sul channel manager non è ancora attiva perché manca l'indirizzo di servizio: contatta l'assistenza."),
        ["octorate_mapping_unconfirmed"] = ("OctorateMappingUnconfirmed", "Conferma o rifiuta prima l'abbinamento di questo immobile a un annuncio già presente sul channel manager, poi riprova."),
        ["octorate_existing_inventory_must_import"] = ("OctorateExistingInventoryMustImport", "Gli annunci di questo immobile esistono già sul channel manager o sulle OTA: si importano e si abbinano, non si creano di nuovo."),
    };

    /// <summary>The key of <c>SharedResources</c> for a code, or <c>null</c> for a code with no words.</summary>
    public static string? MessageKey(string? code) => code is not null && Catalog.TryGetValue(code, out var entry) ? entry.Key : null;

    /// <summary>The Italian words of a code (what the row keeps); <c>null</c> for a code with no words.</summary>
    public static string? ItalianMessage(string? code) => code is not null && Catalog.TryGetValue(code, out var entry) ? entry.Italian : null;

    /// <summary>
    /// The code a row keeps for an error of the channel manager: the code of the typed error when it has words, <see cref="ContentRejected"/> for a
    /// refusal of the request itself, <see cref="ChannelPushFailed"/> for anything else.
    /// </summary>
    public static string CodeOfOctorateError(string? octorateCode) =>
        octorateCode == "octorate_request_rejected" ? ContentRejected
        : octorateCode is not null && Catalog.ContainsKey(octorateCode) ? octorateCode
        : ChannelPushFailed;
}
