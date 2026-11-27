namespace Casazen.Core.BankStatements;

public sealed record RentBankTenant(string FirstName, string LastName);

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

public sealed record RentBankPayer(Guid LeaseId, string? IbanHash, string? NameHash);

public sealed record RentBankCredit(string Fingerprint, BankMovement Movement, string? IbanHash, string? NameHash);

public enum RentBankConfidence
{
    Low = 0,

    Medium = 1,

    High = 2,
}

public enum RentBankReason
{
    Amount = 0,

    Surname = 1,

    FirstName = 2,

    PayerIban = 3,

    PayerName = 4,

    Property = 5,

    Month = 6,
}

public sealed record RentBankSuggestion(
    RentBankCredit Credit,
    RentBankCandidate Installment,
    RentBankConfidence Confidence,
    IReadOnlyList<RentBankReason> Reasons,
    IReadOnlyList<RentBankCandidate> Alternatives);

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

    public const int DaysEarly = 45;

    public const int DaysLate = 400;

    private const int HighConfidenceDaysLate = 120;

    /// <summary>
    /// A sure match is never one that came more than this many days before the due date unless the reason names the month: with the rent of
    /// the month before declared by hand, a credit that was for that one would otherwise be offered, ticked, for the next.
    /// </summary>
    private const int HighConfidenceDaysEarly = 10;

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

        var otherLeaseIdentity = ranked
            .Where(item => item.Candidate.LeaseId != best.Candidate.LeaseId)
            .Select(item => identities[item.Candidate.LeaseId].Points)
            .DefaultIfEmpty(-1)
            .Max();
        var leaseClear = identity.Points >= StrongIdentity && otherLeaseIdentity <= identity.Points - LeaseMargin;

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
