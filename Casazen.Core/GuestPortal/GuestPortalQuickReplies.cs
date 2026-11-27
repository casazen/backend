using Casazen.Core.Entities.Enums;

namespace Casazen.Core.GuestPortal;

/// <summary>Recognizes a complete practical question. Free text stays with the host.</summary>
public static class GuestPortalQuickReplies
{
    public static GuestPortalQuickReply? Answer(GuestHouseGuide guide, string? question, string language, bool canSeeReserved)
    {
        if (guide.AutoReplies is not { Enabled: true } settings || string.IsNullOrWhiteSpace(question))
            return null;

        var normalized = question.Trim().TrimEnd('?', ' ').TrimStart('¿').ToLowerInvariant().Replace('’', '\'');
        var (answer, situation, topic) = normalized switch
        {
            "a che ora posso fare il check-in" or "what time is check-in" or "à quelle heure est l'arrivée" or "a qué hora es el check-in" or "wann ist der check-in"
                when settings.CheckIn => (guide.CheckInHours, AssistantSituation.CheckInTime, KnowledgeTopic.CheckIn),
            "a che ora devo fare il check-out" or "what time is check-out" or "à quelle heure est le départ" or "a qué hora es el check-out" or "wann ist der check-out"
                when settings.CheckOut => (guide.CheckOutHours, AssistantSituation.EarlyArrivalOrLateCheckout, KnowledgeTopic.CheckOut),
            "dove posso parcheggiare" or "where can i park" or "où puis-je me garer" or "dónde puedo aparcar" or "wo kann ich parken"
                when settings.Parking => (guide.Parking, AssistantSituation.Parking, KnowledgeTopic.Parking),
            "qual è la password del wi-fi" or "what is the wi-fi password" or "what is the wifi password" or "quel est le mot de passe du wi-fi" or "cuál es la contraseña del wifi" or "wie lautet das wlan-passwort"
                when settings.Wifi && canSeeReserved && !string.IsNullOrWhiteSpace(guide.Wifi) => (WifiLocation(language), AssistantSituation.WiFi, KnowledgeTopic.WiFi),
            _ => (null, default, default),
        };
        if (string.IsNullOrWhiteSpace(answer))
            return null;

        var (intro, handoff) = language switch
        {
            "en" => ("Automatic reply from the house guide", "Choose “Write to the host” to speak to a person."),
            "es" => ("Respuesta automática de la guía de la casa", "Selecciona «Escribir al anfitrión» para hablar con una persona."),
            "fr" => ("Réponse automatique du guide du logement", "Sélectionnez « Écrire à l’hôte » pour parler à une personne."),
            "de" => ("Automatische Antwort aus dem Hausführer", "Wähle „Gastgeber kontaktieren“, um mit einer Person zu sprechen."),
            _ => ("Risposta automatica dalla guida della casa", "Seleziona «Scrivi all’host» per parlare con una persona."),
        };
        return new($"{intro}\n\n{answer.Trim()}\n\n{handoff}", situation, topic, answer.Trim());
    }

    public static string Acknowledgment(string language) => language switch
    {
        "en" => "Thank you for your message. I’ll check the details and get back to you here.",
        "de" => "Danke für Ihre Nachricht. Ich prüfe die Details und antworte Ihnen hier.",
        "fr" => "Merci pour votre message. Je vais vérifier les détails et vous répondre ici.",
        "es" => "Gracias por tu mensaje. Voy a comprobar los detalles y te responderé por aquí.",
        _ => "Grazie per il messaggio. Verifico i dettagli e ti rispondo qui.",
    };

    private static string WifiLocation(string language) => language switch
    {
        "en" => "You’ll find the network and password in the Wi-Fi section of the house guide.",
        "es" => "Encontrarás la red y la contraseña en la sección Wi-Fi de la guía del alojamiento.",
        "fr" => "Vous trouverez le réseau et le mot de passe dans la section Wi-Fi du guide du logement.",
        "de" => "Netzwerk und Passwort findest du im WLAN-Abschnitt des Hausführers.",
        _ => "Trovi la rete e la password nella sezione Wi-Fi della guida della casa.",
    };
}

public sealed record GuestPortalQuickReply(string Text, AssistantSituation Situation, KnowledgeTopic Topic, string ReviewedText);
