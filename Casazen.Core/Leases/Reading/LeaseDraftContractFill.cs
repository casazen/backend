using System.Globalization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Regulatory;

namespace Casazen.Core.Leases.Reading;

/// <summary>What became of a value the contract states, for the person who looks at the draft afterwards.</summary>
public enum FieldReadingState
{
    /// <summary>The draft had nothing there: the value was written, and the screen says "read from the contract — check it".</summary>
    Read,

    /// <summary>The draft already had the same value (typed by the person, or chosen at the start): nothing to say.</summary>
    Agrees,

    /// <summary>The draft has another value, typed by the person: it stays, the contract says something else, and the screen says so.</summary>
    Conflict,

    /// <summary>The contract states it but the rules of the draft refuse it (a deposit above three rents, a term the type does not allow): it was not written and the field is asked.</summary>
    Unusable,
}

/// <summary>
/// One value of the contract and what became of it. Names the field and, for a person, the entry of the draft: never the value
/// (this is what is kept with the document and sent to the screen, and it must hold no personal data).
/// </summary>
/// <param name="Field"><c>contractType</c>, <c>taxRegime</c>, <c>monthlyRent</c>, <c>securityDeposit</c>, <c>startDate</c>, <c>endDate</c>,
/// <c>property</c> (the address of the contract against the property chosen), and for a person <c>firstName</c>, <c>lastName</c>,
/// <c>fiscalCode</c>, <c>residence</c>, <c>citizenship</c> or <c>party</c> (the person as a whole).</param>
public sealed record FieldReading(
    string Field, FieldReadingState State, int Confidence, bool Derived = false, Guid? PartyId = null, PartyRole? Role = null);

/// <summary>The values of the draft once the contract is read into them, and what became of each value read.</summary>
public sealed record LeaseDraftFill(LeaseDraftValues Values, IReadOnlyList<FieldReading> Fields, bool Changed);

/// <summary>
/// Puts what was read in a contract (<see cref="LeaseContractReading"/>) into the values of an imported draft (NL-DOCS). A pure
/// function: the caller holds the row lock of the draft and writes the result with the same validation and writer as a save.
/// </summary>
/// <remarks>
/// <b>Only what is still empty is filled, and nothing typed is ever overwritten</b>: a different value is a <see cref="FieldReadingState.Conflict"/>
/// to tell, not to write. A value the rules of the draft refuse (the same checks the flow makes: a valid fiscal code, a rent above zero, a
/// deposit of at most three rents, a term the type allows) is not written and stays missing. The day of the signature is not a value of
/// the draft: it goes with the import.
/// </remarks>
public static class LeaseDraftContractFill
{
    public static LeaseDraftFill Merge(LeaseDraftValues current, LeaseContractReading reading, LeaseOrigin origin, Func<Guid> newId)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(newId);

        var run = new Run(current);

        var type = FillType(run, reading, origin);
        FillRegime(run, reading);
        var rent = FillRent(run, reading);
        FillDeposit(run, reading, rent);
        var start = FillStart(run, reading);
        FillEnd(run, reading, origin, type, start);
        FillProperty(run, reading);

        run.Values = run.Values with
        {
            Landlords = FillRole(run, PartyRole.Landlord, run.Values.Landlords, run.Values.Tenants, reading, newId),
            Tenants = FillRole(run, PartyRole.Tenant, run.Values.Tenants, run.Values.Landlords, reading, newId),
        };

        return new LeaseDraftFill(run.Values, run.Fields, run.Changed);
    }

    /// <summary>The values being filled, what was written and what became of each value read.</summary>
    private sealed class Run(LeaseDraftValues start)
    {
        public LeaseDraftValues Values { get; set; } = start;

        public List<FieldReading> Fields { get; } = [];

        public bool Changed { get; set; }

        public void Add(string field, FieldReadingState state, int confidence, bool derived = false, Guid? partyId = null, PartyRole? role = null) =>
            Fields.Add(new FieldReading(field, state, confidence, derived, partyId, role));
    }

    private static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ---- the conditions ------------------------------------------------------------------------------------------------

    private static LeaseContractType? FillType(Run run, LeaseContractReading reading, LeaseOrigin origin)
    {
        var typed = LeaseDraftRules.ParseContractType(run.Values.ContractType);
        if (reading.ContractType is not { IsAccepted: true } read)
            return typed;

        if (!LongRentRules.IsAvailable(read.Value, origin))
        {
            run.Add("contractType", FieldReadingState.Unusable, read.Confidence);
            return typed;
        }

        if (LeaseDraftRules.Clean(run.Values.ContractType) is null)
        {
            run.Values = run.Values with { ContractType = read.Value.ToString() };
            run.Changed = true;
            run.Add("contractType", FieldReadingState.Read, read.Confidence, read.Derived);
            return read.Value;
        }

        run.Add("contractType", typed == read.Value ? FieldReadingState.Agrees : FieldReadingState.Conflict, read.Confidence, read.Derived);
        return typed;
    }

    private static void FillRegime(Run run, LeaseContractReading reading)
    {
        if (reading.TaxRegime is not { IsAccepted: true } read)
            return;

        if (LeaseDraftRules.Clean(run.Values.TaxRegime) is null)
        {
            run.Values = run.Values with { TaxRegime = read.Value.ToString() };
            run.Changed = true;
            run.Add("taxRegime", FieldReadingState.Read, read.Confidence);
            return;
        }

        run.Add("taxRegime", LeaseDraftRules.ParseTaxRegime(run.Values.TaxRegime) == read.Value ? FieldReadingState.Agrees : FieldReadingState.Conflict, read.Confidence);
    }

    private static decimal? FillRent(Run run, LeaseContractReading reading)
    {
        var typed = LeaseDraftRules.IsValidRent(run.Values.MonthlyRent) ? run.Values.MonthlyRent : null;
        if (reading.MonthlyRent is not { IsAccepted: true } read)
            return typed;

        if (!LeaseDraftRules.IsValidRent(read.Value))
        {
            run.Add("monthlyRent", FieldReadingState.Unusable, read.Confidence, read.Derived);
            return typed;
        }

        if (run.Values.MonthlyRent is null)
        {
            run.Values = run.Values with { MonthlyRent = read.Value };
            run.Changed = true;
            run.Add("monthlyRent", FieldReadingState.Read, read.Confidence, read.Derived);
            return read.Value;
        }

        run.Add("monthlyRent", run.Values.MonthlyRent == read.Value ? FieldReadingState.Agrees : FieldReadingState.Conflict, read.Confidence, read.Derived);
        return typed;
    }

    private static void FillDeposit(Run run, LeaseContractReading reading, decimal? rent)
    {
        if (reading.SecurityDeposit is not { IsAccepted: true } read)
            return;

        // The same limit the flow applies (deposit_too_high): more than three rents is far more likely a misreading than a deposit.
        var tooHigh = rent is { } monthly && read.Value > monthly * LongRentRules.DepositMaxRents;
        if (!LeaseDraftRules.IsValidDeposit(read.Value) || tooHigh)
        {
            run.Add("securityDeposit", FieldReadingState.Unusable, read.Confidence, read.Derived);
            return;
        }

        if (run.Values.SecurityDeposit is null)
        {
            run.Values = run.Values with { SecurityDeposit = read.Value };
            run.Changed = true;
            run.Add("securityDeposit", FieldReadingState.Read, read.Confidence, read.Derived);
            return;
        }

        run.Add("securityDeposit", run.Values.SecurityDeposit == read.Value ? FieldReadingState.Agrees : FieldReadingState.Conflict, read.Confidence, read.Derived);
    }

    // ---- the dates -----------------------------------------------------------------------------------------------------

    private static DateOnly? FillStart(Run run, LeaseContractReading reading)
    {
        var typed = LeaseDraftRules.ParseDate(run.Values.StartDate);
        if (reading.StartDate is not { IsAccepted: true } read)
            return typed;

        if (LeaseDraftRules.ParseDate(Day(read.Value)) is null)
        {
            run.Add("startDate", FieldReadingState.Unusable, read.Confidence);
            return typed;
        }

        if (LeaseDraftRules.Clean(run.Values.StartDate) is null)
        {
            run.Values = run.Values with { StartDate = Day(read.Value) };
            run.Changed = true;
            run.Add("startDate", FieldReadingState.Read, read.Confidence);
            return read.Value;
        }

        run.Add("startDate", typed == read.Value ? FieldReadingState.Agrees : FieldReadingState.Conflict, read.Confidence);
        return typed;
    }

    /// <summary>
    /// The last day of the first period, for a contract already signed. A term that follows its type needs no end in the draft (the draft
    /// computes it): the end is written when the contract states another one, or when the type has none of its own (the transitory one).
    /// </summary>
    private static void FillEnd(Run run, LeaseContractReading reading, LeaseOrigin origin, LeaseContractType? type, DateOnly? start)
    {
        if (origin != LeaseOrigin.Imported || reading.EndDate is not { IsAccepted: true } read)
            return;

        if (start is not { } from || read.Value <= from || LeaseDraftRules.ParseDate(Day(read.Value)) is null)
        {
            run.Add("endDate", FieldReadingState.Unusable, read.Confidence, read.Derived);
            return;
        }

        var knownType = type is { } named && LongRentRules.IsAvailable(named, origin) ? named : (LeaseContractType?)null;
        if (LeaseContractTerms.CheckTerm(knownType, LeaseDraftRules.ToStoredDate(from), LeaseDraftRules.ToStoredDate(read.Value)) is not null)
        {
            run.Add("endDate", FieldReadingState.Unusable, read.Confidence, read.Derived);
            return;
        }

        if (LeaseDraftRules.Clean(run.Values.EndDate) is not null)
        {
            run.Add("endDate", LeaseDraftRules.ParseDate(run.Values.EndDate) == read.Value ? FieldReadingState.Agrees : FieldReadingState.Conflict, read.Confidence, read.Derived);
            return;
        }

        if (knownType is { } own && LongRentRules.FirstTermMonths(own) is { } months
            && DateOnly.FromDateTime(LongRentRules.FirstTermEnd(LeaseDraftRules.ToStoredDate(from), months)) == read.Value)
        {
            run.Add("endDate", FieldReadingState.Agrees, read.Confidence, read.Derived);
            return;
        }

        run.Values = run.Values with { EndDate = Day(read.Value) };
        run.Changed = true;
        run.Add("endDate", FieldReadingState.Read, read.Confidence, read.Derived);
    }

    private static void FillProperty(Run run, LeaseContractReading reading)
    {
        if (reading.Property is { IsAccepted: true } read)
            run.Add("property", read.Value == PropertyAddressMatch.Match ? FieldReadingState.Agrees : FieldReadingState.Conflict, read.Confidence);
    }

    // ---- the people ------------------------------------------------------------------------------------------------------

    private static List<LeaseDraftParty> FillRole(
        Run run,
        PartyRole role,
        IReadOnlyList<LeaseDraftParty> existing,
        IReadOnlyList<LeaseDraftParty> otherRole,
        LeaseContractReading reading,
        Func<Guid> newId)
    {
        var list = existing.ToList();
        foreach (var read in reading.Parties.Where(p => p.Role == role))
        {
            if (read.RoleConfidence < LeaseContractTextParser.AcceptedConfidence
                || read.FiscalCodeConfidence < LeaseContractTextParser.AcceptedConfidence
                || !ItalianFiscalCode.IsValid(read.FiscalCode))
            {
                continue;
            }

            var code = ItalianFiscalCode.Normalize(read.FiscalCode);

            // The same person cannot be on both sides of the contract: the flow would refuse the code twice (party_fiscal_code_duplicate).
            if (otherRole.Any(o => ItalianFiscalCode.Normalize(o.FiscalCode) == code))
            {
                run.Add("party", FieldReadingState.Unusable, read.FiscalCodeConfidence, role: role);
                continue;
            }

            var index = list.FindIndex(e => ItalianFiscalCode.Normalize(e.FiscalCode) == code);
            if (index < 0)
                index = list.FindIndex(e => LeaseDraftRules.Clean(e.FiscalCode) is null && SameName(e, read));

            if (index >= 0)
            {
                list[index] = FillEntry(run, list[index], read, role);
                continue;
            }

            // Somebody else is already typed in this role: not replaced, not added to (it may be the wrong contract): the person is told.
            // An empty role gets the person of the contract.
            if (list.Count > 0 || list.Count >= LeasePartyRules.MaxPerRole)
            {
                run.Add("party", FieldReadingState.Conflict, read.FiscalCodeConfidence, role: role);
                continue;
            }

            list.Add(FillEntry(run, new LeaseDraftParty { Id = newId() }, read, role));
        }

        return list;
    }

    private static bool SameName(LeaseDraftParty entry, ReadParty read) =>
        LeaseDraftRules.Clean(entry.FirstName) is { } first && LeaseDraftRules.Clean(entry.LastName) is { } last
        && read.FirstName is not null && read.LastName is not null
        && string.Equals(first, read.FirstName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(last, read.LastName, StringComparison.OrdinalIgnoreCase);

    private static LeaseDraftParty FillEntry(Run run, LeaseDraftParty entry, ReadParty read, PartyRole role)
    {
        var next = entry;

        void Field(string name, string? typed, string? value, int confidence, Func<LeaseDraftParty, string, LeaseDraftParty> write, bool usable)
        {
            if (value is null || confidence < LeaseContractTextParser.AcceptedConfidence)
                return;

            if (!usable)
            {
                run.Add(name, FieldReadingState.Unusable, confidence, partyId: entry.Id, role: role);
            }
            else if (LeaseDraftRules.Clean(typed) is null)
            {
                next = write(next, value);
                run.Changed = true;
                run.Add(name, FieldReadingState.Read, confidence, partyId: entry.Id, role: role);
            }
            else
            {
                var same = string.Equals(LeaseDraftRules.Clean(typed), value, StringComparison.OrdinalIgnoreCase);
                run.Add(name, same ? FieldReadingState.Agrees : FieldReadingState.Conflict, confidence, partyId: entry.Id, role: role);
            }
        }

        var code = ItalianFiscalCode.Normalize(read.FiscalCode);
        Field("fiscalCode", ItalianFiscalCode.Normalize(entry.FiscalCode), code, read.FiscalCodeConfidence, (e, v) => e with { FiscalCode = v }, usable: true);
        Field("firstName", entry.FirstName, read.FirstName, read.NameConfidence, (e, v) => e with { FirstName = v }, usable: read.FirstName is { Length: <= 100 });
        Field("lastName", entry.LastName, read.LastName, read.NameConfidence, (e, v) => e with { LastName = v }, usable: read.LastName is { Length: <= 100 });
        Field("residence", entry.Residence, read.Residence, read.ResidenceConfidence, (e, v) => e with { Residence = v }, usable: read.Residence is { Length: <= Party.ResidenceMaxLength });
        Field("citizenship", entry.Citizenship, read.Citizenship, read.CitizenshipConfidence, (e, v) => e with { Citizenship = v }, usable: read.Citizenship is { Length: 2 });
        return next;
    }
}
