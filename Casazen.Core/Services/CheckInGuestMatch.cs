using Casazen.Core.Entities;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Services;

public static class CheckInGuestMatch
{
    public static bool SameName(string? booked, string? entered) =>
        string.Equals(AlloggiatiRecordRules.NormalizeDescription(booked), AlloggiatiRecordRules.NormalizeDescription(entered), StringComparison.Ordinal);

    // A single substitution can suggest a guest for confirmation, never establish identity.
    private static bool OneMisreadLetter(string booked, string read)
    {
        if (booked.Length < 3 || booked.Length != read.Length)
            return false;

        return booked.Zip(read).Count(pair => pair.First != pair.Second) == 1;
    }

    public static (int Position, bool Confirm)? Find(IReadOnlyList<StayGuest> roster, CheckInDocumentFields fields)
    {
        var first = AlloggiatiRecordRules.NormalizeDescription(fields.FirstName);
        var last = AlloggiatiRecordRules.NormalizeDescription(fields.LastName);
        if (first.Length == 0 || last.Length == 0)
            return null;

        var matches = new List<(int Position, bool Confirm)>();
        foreach (var row in roster)
        {
            var bookedFirst = AlloggiatiRecordRules.NormalizeDescription(row.FirstName);
            var bookedLast = AlloggiatiRecordRules.NormalizeDescription(row.LastName);
            if (first == bookedFirst && last == bookedLast)
                matches.Add((row.Position, false));
            else if ((last == bookedLast && OneMisreadLetter(bookedFirst, first)) ||
                (first == bookedFirst && OneMisreadLetter(bookedLast, last)))
                matches.Add((row.Position, true));
        }

        var candidates = matches.Any(m => !m.Confirm) ? matches.Where(m => !m.Confirm).ToList() : matches;
        return candidates.Count == 1 ? candidates[0] : null;
    }
}
