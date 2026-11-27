namespace Casazen.Core.Suppliers;

public enum ShowcaseReadinessLevel
{
    Ok = 0,

    Warn = 1,

    Todo = 2,
}

public sealed record ShowcaseReadinessItem(string Code, ShowcaseReadinessLevel Level, bool Required, string Target);

public sealed record ShowcaseReadinessResult(IReadOnlyList<ShowcaseReadinessItem> Items)
{
    public bool CanPublish => Items.All(item => !item.Required || item.Level != ShowcaseReadinessLevel.Todo);

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

    private const string ServicesPath = "/app/supplier/services";
    private const string ProfilePath = "/app/supplier/profile";
    private const string AvailabilityPath = "/app/supplier/availability";
    private const string SettingsPath = "/app/supplier/settings";
    private const string StylePath = "/app/supplier/showcase?step=style";
    private const string WorksPath = "/app/supplier/showcase?step=works";
    private const string TextsPath = "/app/supplier/showcase?step=texts";

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

public static class ShowcaseBookingBlocks
{
    public const string NotPublished = "not_published";

    public const string Paused = "paused";

    public const string PaymentsOff = "payments_off";

    public const string AccountNotReady = "payments_account_not_ready";

    public const string SwitchOff = "bookings_off";
}

public sealed record ShowcaseBookings(bool Open, IReadOnlyList<string> BlockedBy)
{
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

    public static bool SwitchValue(bool published, bool paused, bool paymentsFlagOn, bool accountReady) =>
        published && !paused && paymentsFlagOn && accountReady;
}
