namespace Casazen.Core.Suppliers;

/// <summary>How an item of the readiness stands: done, advised, or still to do.</summary>
public enum ShowcaseReadinessLevel
{
    /// <summary>Done.</summary>
    Ok = 0,

    /// <summary>Not done, and the showcase can be published without it (a piece of advice).</summary>
    Warn = 1,

    /// <summary>Not done, and the showcase cannot be published without it.</summary>
    Todo = 2,
}

/// <summary>
/// One line of the "what is missing" list of the editor (SP-13a).
/// </summary>
/// <param name="Code">Stable identifier (<see cref="ShowcaseReadiness.Services"/> and the others): the editor writes the text.</param>
/// <param name="Level">Done, advised, or still to do.</param>
/// <param name="Required">True when a <see cref="ShowcaseReadinessLevel.Todo"/> blocks the publication.</param>
/// <param name="Target">Where in the app the supplier fixes it (a path of the web app).</param>
public sealed record ShowcaseReadinessItem(string Code, ShowcaseReadinessLevel Level, bool Required, string Target);

/// <summary>What the readiness of a showcase says: every item, and whether the showcase can be published.</summary>
public sealed record ShowcaseReadinessResult(IReadOnlyList<ShowcaseReadinessItem> Items)
{
    /// <summary>No required item is still to do.</summary>
    public bool CanPublish => Items.All(item => !item.Required || item.Level != ShowcaseReadinessLevel.Todo);

    /// <summary>The codes of the required items that are still to do: what a publication is refused for.</summary>
    public IReadOnlyList<string> Missing { get; } =
        Items.Where(item => item.Required && item.Level == ShowcaseReadinessLevel.Todo).Select(item => item.Code).ToList();
}

/// <summary>
/// The facts about the supplier the readiness is judged on. Read by the service, so that the rules below stay pure.
/// </summary>
/// <param name="ShownActiveServices">How many active services the showcase would show (the services of the order, or every active one).</param>
/// <param name="HasZones">The supplier serves at least one comune.</param>
/// <param name="HoursConfigured">The supplier saved working hours with at least one band.</param>
/// <param name="Verified">"Verificato" (decision D11): an active profile with a VAT number and a payments account that can take charges and payouts.</param>
public sealed record ShowcaseReadinessFacts(int ShownActiveServices, bool HasZones, bool HoursConfigured, bool Verified);

/// <summary>
/// What the supplier must have, and should have, before it publishes its showcase (SP-13a, the list of the editor). The server judges
/// it again at every publication: a button that is disabled in the editor is a convenience, not a guard.
/// <list type="bullet">
/// <item><b>Required:</b> at least one active service shown (a published service always has a price or "su preventivo"), the zones
/// it serves, and a button color that reads with white text.</item>
/// <item><b>Advised:</b> working hours, a written presentation, three photos of jobs, three questions, "Verificato".</item>
/// </list>
/// </summary>
public static class ShowcaseReadiness
{
    public const string Services = "services";
    public const string Zones = "zones";
    public const string Color = "color";
    public const string Hours = "hours";
    public const string Presentation = "presentation";
    public const string Photos = "photos";
    public const string Faq = "faq";
    public const string Verified = "verified";

    /// <summary>Where the supplier fixes each item (paths of the web app; the editor steps are <c>?step=</c> of the showcase page).</summary>
    private const string ServicesPath = "/app/supplier/services";
    private const string ProfilePath = "/app/supplier/profile";
    private const string AvailabilityPath = "/app/supplier/availability";
    private const string SettingsPath = "/app/supplier/settings";
    private const string StylePath = "/app/supplier/showcase?step=style";
    private const string WorksPath = "/app/supplier/showcase?step=works";
    private const string TextsPath = "/app/supplier/showcase?step=texts";

    /// <summary>The readiness of the draft (cleaned of empty items) for a supplier with these facts.</summary>
    public static ShowcaseReadinessResult Evaluate(ShowcaseContentDocument draft, ShowcaseReadinessFacts facts)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(facts);

        var colorOk = draft.Color is null || (ShowcaseColor.TryNormalize(draft.Color, out var hex) && ShowcaseColor.IsReadableWithWhite(hex));
        var presentationOk = Length(draft.Tagline) >= ShowcaseContentLimits.RecommendedTaglineMin
                             && Length(draft.Bio) >= ShowcaseContentLimits.RecommendedBioMin;
        var photos = draft.Gallery.Count(work => !string.IsNullOrWhiteSpace(work.PhotoUrl));
        var questions = draft.Faq.Count(item => !string.IsNullOrWhiteSpace(item.Q) && !string.IsNullOrWhiteSpace(item.A));

        return new ShowcaseReadinessResult(
        [
            Required(Services, facts.ShownActiveServices > 0, ServicesPath),
            Required(Zones, facts.HasZones, ProfilePath),
            Required(Color, colorOk, StylePath),
            Advised(Hours, facts.HoursConfigured, AvailabilityPath),
            Advised(Presentation, presentationOk, TextsPath),
            Advised(Photos, photos >= ShowcaseContentLimits.RecommendedPhotos, WorksPath),
            Advised(Faq, questions >= ShowcaseContentLimits.RecommendedFaq, TextsPath),
            Advised(Verified, facts.Verified, SettingsPath),
        ]);
    }

    private static int Length(string? value) => value?.Trim().Length ?? 0;

    private static ShowcaseReadinessItem Required(string code, bool done, string target) =>
        new(code, done ? ShowcaseReadinessLevel.Ok : ShowcaseReadinessLevel.Todo, true, target);

    private static ShowcaseReadinessItem Advised(string code, bool done, string target) =>
        new(code, done ? ShowcaseReadinessLevel.Ok : ShowcaseReadinessLevel.Warn, false, target);
}

/// <summary>Why the bookings of a showcase are closed: the codes of <see cref="ShowcaseBookings.BlockedBy"/>.</summary>
public static class ShowcaseBookingBlocks
{
    /// <summary>Nothing is published yet.</summary>
    public const string NotPublished = "not_published";

    /// <summary>The supplier paused the showcase.</summary>
    public const string Paused = "paused";

    /// <summary>Payments inside CasaZen are not on (the flag <c>SupplierOnlinePayments</c>): the bookings open only with the payment.</summary>
    public const string PaymentsOff = "payments_off";

    /// <summary>The supplier's account for the payments cannot take charges and payouts yet.</summary>
    public const string AccountNotReady = "payments_account_not_ready";

    /// <summary>
    /// Everything is ready, but the switch is off: the account became ready after the publication. "Riattiva" (resume) turns it on.
    /// </summary>
    public const string SwitchOff = "bookings_off";
}

/// <summary>
/// Whether the showcase takes bookings, and if not why (SP-13a, decision VT-D7: the bookings open only with the payment, and until then
/// the showcase is read-only). <c>open = published ∧ not paused ∧ OnlineBookingEnabled ∧ payments account ready ∧ payments flag on</c>.
/// </summary>
/// <param name="Open">The showcase takes bookings now.</param>
/// <param name="BlockedBy">The reasons it does not, in a stable order; empty when it does.</param>
public sealed record ShowcaseBookings(bool Open, IReadOnlyList<string> BlockedBy)
{
    /// <summary>The derived rule.</summary>
    public static ShowcaseBookings Evaluate(
        bool published,
        bool paused,
        bool onlineBookingEnabled,
        bool paymentsFlagOn,
        bool accountReady)
    {
        var blocked = new List<string>(4);
        if (!published)
            blocked.Add(ShowcaseBookingBlocks.NotPublished);
        if (paused)
            blocked.Add(ShowcaseBookingBlocks.Paused);
        if (!paymentsFlagOn)
            blocked.Add(ShowcaseBookingBlocks.PaymentsOff);
        if (!accountReady)
            blocked.Add(ShowcaseBookingBlocks.AccountNotReady);
        if (blocked.Count == 0 && !onlineBookingEnabled)
            blocked.Add(ShowcaseBookingBlocks.SwitchOff);

        return new ShowcaseBookings(blocked.Count == 0, blocked);
    }

    /// <summary>
    /// The value <c>OnlineBookingEnabled</c> gets when the showcase is published or reopened: on only when it is published, not paused,
    /// the payments are on and the account is ready.
    /// </summary>
    public static bool SwitchValue(bool published, bool paused, bool paymentsFlagOn, bool accountReady) =>
        published && !paused && paymentsFlagOn && accountReady;
}
