namespace Casazen.Core.BankStatements;

/// <summary>A tenant of a lease, by name: what a payment reason may name.</summary>
public sealed record RentBankTenant(string FirstName, string LastName);

/// <summary>
/// A rent installment the landlord has not collected (scheduled or failed) as the matcher sees it: where it is, what it asks and from whom.
/// </summary>
public sealed record RentBankCandidate(
    Guid InstallmentId,
    Guid LeaseId,
    Guid PropertyId,
    string PropertyName,
    string? PropertyAddress,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    DateOnly DueDate,
    decimal Amount,
    IReadOnlyList<RentBankTenant> Tenants);

/// <summary>A payer seen on a movement the landlord confirmed before: the lease it paid and the hashes of its IBAN and name (never the values).</summary>
public sealed record RentBankPayer(Guid LeaseId, string? IbanHash, string? NameHash);

/// <summary>A credit of the statement, with the hashes of its payer (null when the file has none).</summary>
public sealed record RentBankCredit(string Fingerprint, BankMovement Movement, string? IbanHash, string? NameHash);

/// <summary>How sure the matcher is. Only <see cref="High"/> is offered for the one-click confirmation; the others wait for the landlord's choice.</summary>
public enum RentBankConfidence
{
    /// <summary>The amount is the installment's and nothing else fits: the amount alone.</summary>
    Low = 0,

    /// <summary>The amount and some sign of who or when, but not enough to be sure which installment or which lease.</summary>
    Medium = 1,

    /// <summary>The amount, a tenant or payer the lease is known by, and no other lease or installment that fits as well.</summary>
    High = 2,
}

/// <summary>Why the matcher thinks so; the screen says it in words.</summary>
public enum RentBankReason
{
    /// <summary>The amount is exactly the installment's.</summary>
    Amount = 0,

    /// <summary>The surname of a tenant is in the reason or the payer.</summary>
    Surname = 1,

    /// <summary>The first name of the tenant is there too.</summary>
    FirstName = 2,

    /// <summary>The IBAN paid this lease before.</summary>
    PayerIban = 3,

    /// <summary>The payer's name paid this lease before.</summary>
    PayerName = 4,

    /// <summary>The name or the address of the property is in the reason.</summary>
    Property = 5,

    /// <summary>The reason names the month of the installment.</summary>
    Month = 6,
}

/// <summary>A credit with the installment the matcher proposes for it and the other ones that could be it (best first).</summary>
public sealed record RentBankSuggestion(
    RentBankCredit Credit,
    RentBankCandidate Installment,
    RentBankConfidence Confidence,
    IReadOnlyList<RentBankReason> Reasons,
    IReadOnlyList<RentBankCandidate> Alternatives);

/// <summary>The outcome: a suggestion for every credit that has at least one installment of its amount to settle, and the credits left over.</summary>
public sealed record RentBankMatching(IReadOnlyList<RentBankSuggestion> Suggestions, IReadOnlyList<RentBankCredit> Unmatched);

/// <summary>
/// Matches the credits of a bank statement to the rent installments still to collect. A pure function (no database, no clock): the amount
/// must be the installment's to the cent; what tells which installment it is — the tenant's surname, the payer seen on an earlier
/// confirmation, the name or address of the property, the month — and how close the day is to the due date, decide how sure the matcher is.
/// An installment is proposed for one credit at most. The rules are in docs/runbooks/rent-bank-import.md.
/// </summary>
public static class RentBankMatcher
{
    private const int SurnamePoints = 40;
    private const int FirstNamePoints = 10;
    private const int PayerIbanPoints = 50;
    private const int PayerNamePoints = 40;
    private const int PropertyPoints = 15;
    private const int MonthMatchPoints = 25;
    private const int MonthConflictPoints = -30;
    private const int StrongIdentity = 40;
    private const int LeaseMargin = 25;
    private const int MinSurnameLength = 3;
    private const int MinPropertyTokenLength = 4;
    private const int MaxAlternatives = 5;

    /// <summary>Days a payment may come before the due date and still be for that installment (the next month paid in advance).</summary>
    public const int DaysEarly = 45;

    /// <summary>Days a payment may come after the due date and still be for that installment (arrears).</summary>
    public const int DaysLate = 400;

    /// <summary>A sure match is never one that came more than this many days after the due date: a payment that late is the landlord's to place.</summary>
    private const int HighConfidenceDaysLate = 120;

    /// <summary>
    /// A sure match is never one that came more than this many days before the due date unless the reason names the month: with the rent of
    /// the month before declared by hand, a credit that was for that one would otherwise be offered, ticked, for the next.
    /// </summary>
    private const int HighConfidenceDaysEarly = 10;

    /// <summary>The only currency of a rent: a credit in another one never settles an installment.</summary>
    public const string Currency = "EUR";

    private static readonly HashSet<string> PropertyStopWords = new(StringComparer.Ordinal)
    {
        "via", "viale", "piazza", "piazzale", "corso", "largo", "vicolo", "strada", "contrada", "localita", "casa", "appartamento",
        "bilocale", "trilocale", "monolocale", "quadrilocale", "locale", "stanza", "camera", "immobile", "interno", "scala", "piano",
        "civico", "villa", "palazzo", "condominio", "residenza", "box", "garage", "posto", "auto", "cantina", "soffitta", "mansarda",
        "attico", "loft", "studio", "della", "delle", "degli", "dello", "dell", "nella", "nelle", "canone", "affitto", "locazione",
    };

    /// <summary>
    /// A suggestion for every credit that an open installment of the same amount (to the cent) can settle, in the order of the credits' dates.
    /// </summary>
    /// <param name="credits">The credits of the statement that were not imported before.</param>
    /// <param name="candidates">The installments to collect that the landlord reaches (scheduled or failed).</param>
    /// <param name="payers">Payers of the confirmations made before, for the leases the landlord reaches.</param>
    /// <param name="today">The day the matching runs (Europe/Rome): a payment is never dated after it.</param>
    public static RentBankMatching Match(
        IReadOnlyList<RentBankCredit> credits,
        IReadOnlyList<RentBankCandidate> candidates,
        IReadOnlyList<RentBankPayer> payers,
        DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(credits);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(payers);

        var profiles = candidates
            .GroupBy(candidate => candidate.LeaseId)
            .ToDictionary(group => group.Key, group => new LeaseProfile(group.First()));
        var byAmount = candidates.ToLookup(candidate => candidate.Amount);
        var payersByLease = payers.ToLookup(payer => payer.LeaseId);

        var drafts = new List<Draft>();
        var unmatched = new List<RentBankCredit>();
        foreach (var credit in credits)
        {
            var draft = Evaluate(credit, byAmount[credit.Movement.Amount].ToList(), profiles, payersByLease, today);
            if (draft is null)
                unmatched.Add(credit);
            else
                drafts.Add(draft);
        }

        return Assign(drafts, unmatched);
    }

    // ---------------------------------------------------------------- one credit

    private sealed class LeaseProfile
    {
        public LeaseProfile(RentBankCandidate sample)
        {
            Surnames = sample.Tenants
                .Select(tenant => BankValues.NormalizeText(tenant.LastName))
                .Where(surname => surname.Length >= MinSurnameLength)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            FirstNames = sample.Tenants
                .Select(tenant => BankValues.NormalizeText(tenant.FirstName))
                .Where(name => name.Length >= MinSurnameLength)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            PropertyTokens = BankValues.NormalizeText($"{sample.PropertyName} {sample.PropertyAddress}")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.Length >= MinPropertyTokenLength && !token.All(char.IsDigit) && !PropertyStopWords.Contains(token))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        public List<string> Surnames { get; }

        public List<string> FirstNames { get; }

        public List<string> PropertyTokens { get; }
    }

    /// <summary>What the text of one credit says about one lease.</summary>
    private sealed record Identity(int Points, List<RentBankReason> Reasons);

    private sealed record Scored(RentBankCandidate Candidate, int Score, bool MonthMatch, bool MonthConflict, int DaysAfterDue);

    private sealed class Draft
    {
        public required RentBankCredit Credit { get; init; }

        public required List<Scored> Ranked { get; init; }

        public required RentBankConfidence Confidence { get; init; }

        public required Dictionary<Guid, Identity> Identities { get; init; }

        public int Score => Ranked[0].Score;
    }

    private static Draft? Evaluate(
        RentBankCredit credit,
        List<RentBankCandidate> sameAmount,
        Dictionary<Guid, LeaseProfile> profiles,
        ILookup<Guid, RentBankPayer> payersByLease,
        DateOnly today)
    {
        if (sameAmount.Count == 0 || credit.Movement.Currency != Currency)
            return null;

        // A payment is never dated after today (the importer declares it paid on that day).
        var paidOn = credit.Movement.ValueDate > today ? today : credit.Movement.ValueDate;
        var text = BankValues.NormalizeText($"{credit.Movement.Description} {credit.Movement.CounterpartyName}");
        var padded = $" {text} ";
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var months = BankValues.ExtractMonths(text);

        var identities = new Dictionary<Guid, Identity>();
        var scored = new List<Scored>();
        foreach (var candidate in sameAmount)
        {
            var daysAfterDue = paidOn.DayNumber - candidate.DueDate.DayNumber;
            if (daysAfterDue < -DaysEarly || daysAfterDue > DaysLate)
                continue;

            if (!identities.TryGetValue(candidate.LeaseId, out var identity))
            {
                identity = ReadIdentity(credit, profiles[candidate.LeaseId], payersByLease[candidate.LeaseId], padded, tokens);
                identities[candidate.LeaseId] = identity;
            }

            var monthMatch = BankValues.ReferencesPeriod(months, candidate.PeriodStart, candidate.PeriodEnd);
            var monthConflict = months.Count > 0 && !monthMatch;
            var score = identity.Points
                + (monthMatch ? MonthMatchPoints : monthConflict ? MonthConflictPoints : 0)
                + DateFit(daysAfterDue);
            scored.Add(new Scored(candidate, score, monthMatch, monthConflict, daysAfterDue));
        }

        if (scored.Count == 0)
            return null;

        // The lease the credit is for first (what it names), then within it the installment the month, then the day, then the oldest points to.
        var ranked = scored
            .OrderByDescending(item => identities[item.Candidate.LeaseId].Points)
            .ThenByDescending(item => item.MonthMatch)
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.Candidate.DueDate)
            .ThenBy(item => item.Candidate.InstallmentId)
            .ToList();

        return new Draft
        {
            Credit = credit,
            Ranked = ranked,
            Confidence = Judge(ranked, identities),
            Identities = identities,
        };
    }

    private static Identity ReadIdentity(
        RentBankCredit credit,
        LeaseProfile profile,
        IEnumerable<RentBankPayer> payers,
        string paddedText,
        HashSet<string> tokens)
    {
        var points = 0;
        var reasons = new List<RentBankReason>();

        var surnameHit = profile.Surnames.Any(surname => Contains(paddedText, tokens, surname));
        if (surnameHit)
        {
            points += SurnamePoints;
            reasons.Add(RentBankReason.Surname);
            if (profile.FirstNames.Any(name => Contains(paddedText, tokens, name)))
            {
                points += FirstNamePoints;
                reasons.Add(RentBankReason.FirstName);
            }
        }

        var known = payers.ToList();
        if (credit.IbanHash is not null && known.Any(payer => payer.IbanHash == credit.IbanHash))
        {
            points += PayerIbanPoints;
            reasons.Add(RentBankReason.PayerIban);
        }

        if (credit.NameHash is not null && known.Any(payer => payer.NameHash == credit.NameHash))
        {
            points += PayerNamePoints;
            reasons.Add(RentBankReason.PayerName);
        }

        if (profile.PropertyTokens.Any(tokens.Contains))
        {
            points += PropertyPoints;
            reasons.Add(RentBankReason.Property);
        }

        return new Identity(points, reasons);
    }

    /// <summary>The words of a name or surname, together, in the text; «D'Angelo» written «DANGELO» counts.</summary>
    private static bool Contains(string paddedText, HashSet<string> tokens, string phrase)
    {
        if (paddedText.Contains($" {phrase} ", StringComparison.Ordinal))
            return true;
        var compact = phrase.Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact.Length != phrase.Length && tokens.Contains(compact);
    }

    private static int DateFit(int daysAfterDue)
    {
        var days = Math.Abs(daysAfterDue);
        return days <= 7 ? 15 : days <= 20 ? 10 : days <= 45 ? 5 : 0;
    }

    private static RentBankConfidence Judge(List<Scored> ranked, Dictionary<Guid, Identity> identities)
    {
        var best = ranked[0];
        var identity = identities[best.Candidate.LeaseId];

        // Is this lease the only one the credit can be for? Another lease that fits nearly as well is a doubt.
        var otherLeaseIdentity = ranked
            .Where(item => item.Candidate.LeaseId != best.Candidate.LeaseId)
            .Select(item => identities[item.Candidate.LeaseId].Points)
            .DefaultIfEmpty(-1)
            .Max();
        var leaseClear = identity.Points >= StrongIdentity && otherLeaseIdentity <= identity.Points - LeaseMargin;

        // Is this installment the only one of the lease it can be? Several of the same amount are told apart only by the month the reason names.
        var ofLease = ranked.Where(item => item.Candidate.LeaseId == best.Candidate.LeaseId).ToList();
        var installmentClear = !best.MonthConflict
            && (ofLease.Count == 1 || (best.MonthMatch && ofLease.Count(item => item.MonthMatch) == 1));

        // Is the day plausible? Not long after the due date, and not long before it unless the reason says which month it is for.
        var dayClear = best.DaysAfterDue <= HighConfidenceDaysLate && (best.DaysAfterDue >= -HighConfidenceDaysEarly || best.MonthMatch);

        if (leaseClear && installmentClear && dayClear)
            return RentBankConfidence.High;
        if (identity.Points > 0 || best.MonthMatch)
            return RentBankConfidence.Medium;
        return RentBankConfidence.Low;
    }

    // ---------------------------------------------------------------- one installment for one credit

    /// <summary>
    /// Gives each installment to one credit: the surest credits choose first, and a credit whose best installment was taken falls back to its
    /// next one with one level less of confidence. What no installment is left for is unmatched.
    /// </summary>
    private static RentBankMatching Assign(List<Draft> drafts, List<RentBankCredit> unmatched)
    {
        var taken = new HashSet<Guid>();
        var suggestions = new List<RentBankSuggestion>();
        var order = drafts
            .OrderByDescending(draft => draft.Confidence)
            .ThenByDescending(draft => draft.Score)
            .ThenBy(draft => draft.Credit.Movement.ValueDate)
            .ThenBy(draft => draft.Credit.Fingerprint, StringComparer.Ordinal);

        foreach (var draft in order)
        {
            var free = draft.Ranked.Where(item => !taken.Contains(item.Candidate.InstallmentId)).ToList();
            if (free.Count == 0)
            {
                unmatched.Add(draft.Credit);
                continue;
            }

            var chosen = free[0];
            taken.Add(chosen.Candidate.InstallmentId);
            var identity = draft.Identities[chosen.Candidate.LeaseId];
            var confidence = draft.Confidence;
            if (!ReferenceEquals(chosen, draft.Ranked[0]))
            {
                // Its own best installment went to a surer credit: what is left is judged on what it says about this one, and is never sure.
                var fallback = identity.Points > 0 || chosen.MonthMatch ? RentBankConfidence.Medium : RentBankConfidence.Low;
                confidence = (RentBankConfidence)Math.Min((int)fallback, (int)draft.Confidence);
            }

            var reasons = new List<RentBankReason> { RentBankReason.Amount };
            reasons.AddRange(identity.Reasons);
            if (chosen.MonthMatch)
                reasons.Add(RentBankReason.Month);

            suggestions.Add(new RentBankSuggestion(
                draft.Credit,
                chosen.Candidate,
                confidence,
                reasons,
                free.Skip(1).Take(MaxAlternatives).Select(item => item.Candidate).ToList()));
        }

        return new RentBankMatching(
            suggestions
                .OrderByDescending(s => s.Confidence)
                .ThenBy(s => s.Credit.Movement.ValueDate)
                .ThenBy(s => s.Credit.Fingerprint, StringComparer.Ordinal)
                .ToList(),
            unmatched.OrderBy(c => c.Movement.ValueDate).ThenBy(c => c.Fingerprint, StringComparer.Ordinal).ToList());
    }
}
