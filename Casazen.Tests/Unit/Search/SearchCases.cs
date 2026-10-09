using System.Globalization;
using Casazen.Core.Authorization;
using Casazen.Core.Search;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Search;
using Microsoft.Extensions.Logging.Abstractions;

namespace Casazen.Tests.Unit.Search;

/// <summary>Who asks, in a <see cref="SearchCase"/>: the access of a kind of caller on the <see cref="SearchWorld"/>.</summary>
internal enum SearchCaller
{
    /// <summary>The owner: every permission, the whole org.</summary>
    Owner,

    /// <summary>The collaborator «Solo alcuni»: every permission of the short-rent kind, the first property only.</summary>
    Collaborator,

    /// <summary>An account in no team: the properties it created.</summary>
    AccountInNoTeam,

    /// <summary>The owner of the other org.</summary>
    OtherOrg,

    /// <summary>The owner holding only the permissions of the short-rent area.</summary>
    ShortRentOnly,

    /// <summary>The owner holding only the permissions of the long-term area.</summary>
    LongRentOnly,

    /// <summary>A collaborator given no property at all.</summary>
    NobodyGranted,

    /// <summary>A supplier: the plumbers (the inbox), no host access.</summary>
    Plumbers,

    /// <summary>A supplier: the cleaners (the inbox), no host access.</summary>
    Cleaners,
}

/// <summary>
/// One question to the global search and the answer it must give, as the ids found in each group (the groups not listed must not
/// be there). The same cases run on the in-memory provider (<c>GlobalSearchCasesTests</c>) and on PostgreSQL, with the real full-text
/// index and the real SQL of the scope (<c>GlobalSearchPostgresTests</c>): what is found must not depend on the provider.
/// </summary>
internal sealed record SearchCase(
    string Name,
    string Term,
    SearchCaller Caller,
    Func<SearchWorld, IReadOnlyDictionary<string, Guid[]>> Expected)
{
    public override string ToString() => Name;
}

internal static class SearchCases
{
    private static readonly CultureInfo Italian = CultureInfo.GetCultureInfo("it-IT");

    private static SearchCase Case(string name, string term, SearchCaller caller, Func<SearchWorld, (string Type, Guid[] Ids)[]> expected) =>
        new(name, term, caller, world => expected(world).ToDictionary(group => group.Type, group => group.Ids));

    private static (string, Guid[]) Property(params Guid[] ids) => (SearchTypes.Property, ids);

    private static (string, Guid[]) Booking(params Guid[] ids) => (SearchTypes.Booking, ids);

    private static (string, Guid[]) Guest(params Guid[] ids) => (SearchTypes.Guest, ids);

    private static (string, Guid[]) Lease(params Guid[] ids) => (SearchTypes.Lease, ids);

    private static (string, Guid[]) Request(params Guid[] ids) => (SearchTypes.ServiceRequest, ids);

    private static (string, Guid[]) Supplier(params Guid[] ids) => (SearchTypes.Supplier, ids);

    private static (string, Guid[]) Inbox(params Guid[] ids) => (SearchTypes.SupplierRequest, ids);

    public static IReadOnlyList<SearchCase> All { get; } =
    [
        // ─── The words ───────────────────────────────────────────────────────────────────────────────
        Case("property by the beginning of its name", "tru", SearchCaller.Owner, w => [Property(w.Trullo.Id)]),
        Case("property by a later word", "bianco", SearchCaller.Owner, w => [Property(w.Trullo.Id)]),
        Case("property by its city, accents ignored", "forli", SearchCaller.Owner, w => [Property(w.CasaBella.Id)]),
        Case("property by its city typed with the accent and in capitals", "FORLÌ", SearchCaller.Owner, w => [Property(w.CasaBella.Id)]),
        Case("property by the beginning of its CIN", "IT0720", SearchCaller.Owner, w => [Property(w.Trullo.Id)]),
        Case("property words in any order", "bella forli casa", SearchCaller.Owner, w => [Property(w.CasaBella.Id)]),
        Case("a word in the middle of a word finds nothing", "ullo", SearchCaller.Owner, _ => []),
        Case("a deleted property is never found", "cancellata", SearchCaller.Owner, _ => []),
        Case("a switched off property is never found", "rustico", SearchCaller.Owner, _ => []),
        Case("a term with no word of two characters", "a b", SearchCaller.Owner, _ => []),
        Case("the long-term property opens in its area", "loft", SearchCaller.Owner, w => [Property(w.LoftNavigli.Id)]),
        Case("the long-term property needs the long-term permission", "loft", SearchCaller.ShortRentOnly, _ => []),
        Case("the short-rent property needs the short-rent permission", "trullo", SearchCaller.LongRentOnly, _ => []),

        // ─── Guests and bookings ─────────────────────────────────────────────────────────────────────
        Case("guest by first name with the accent", "José", SearchCaller.Owner, w => [Booking(w.StayJose.Id), Guest(w.Jose.Id)]),
        Case("guest by surname typed without the diaeresis", "muller", SearchCaller.Owner, w => [Booking(w.StayJose.Id), Guest(w.Jose.Id)]),
        Case("guest by the beginning of the e-mail", "jose.muller@exam", SearchCaller.Owner, w => [Booking(w.StayJose.Id), Guest(w.Jose.Id)]),
        Case("two guests with one surname, and the supplier of that name", "rossi", SearchCaller.Owner, w =>
            [Booking(w.StayMarioAtCasaBella.Id, w.StayMarioAtTrullo.Id, w.StayMaria.Id), Guest(w.MariaRossi.Id, w.MarioRossi.Id), Supplier(w.PlumbersOrgId)]),
        Case("first name and surname as prefixes", "mar ros", SearchCaller.Owner, w =>
            [Booking(w.StayMarioAtCasaBella.Id, w.StayMarioAtTrullo.Id, w.StayMaria.Id), Guest(w.MariaRossi.Id, w.MarioRossi.Id)]),
        Case("an anonymized guest is not found", "anonymized", SearchCaller.Owner, _ => []),
        Case("a deleted guest is not found", "elena", SearchCaller.Owner, _ => []),
        Case("a guest with no stay is found by the whole org", "carla", SearchCaller.Owner, w => [Guest(w.WithoutStay.Id)]),
        Case("booking by the code shown to people", "7K3M9-PQ2XV", SearchCaller.Owner, w => [Booking(w.StayJose.Id)]),
        Case("booking by the beginning of its code", "7k3m", SearchCaller.Owner, w => [Booking(w.StayJose.Id)]),
        Case("booking of an anonymized guest, by code", "QWERT12345", SearchCaller.Owner, w => [Booking(w.StayAnonymized.Id)]),
        Case("a code with letters only is not looked up as a beginning", "zxcv", SearchCaller.Owner, _ => []),

        // ─── Leases, requests, suppliers ─────────────────────────────────────────────────────────────
        Case("lease by a tenant", "verdi", SearchCaller.Owner, w => [Lease(w.LeaseVerdi.Id)]),
        Case("lease by the first name of a tenant", "paolo", SearchCaller.Owner, w => [Lease(w.LeaseVerdi.Id)]),
        Case("an anonymized tenant is not found", "bianchi", SearchCaller.Owner, _ => []),
        Case("a landlord is not found", "proprietario", SearchCaller.Owner, _ => []),
        Case("request by the name of the service", "pulizia", SearchCaller.Owner, w => [Request(w.Cleaning.Id)]),
        Case("request by the category", "plumbing", SearchCaller.Owner, w => [Request(w.Plumbing.Id, w.SuspendedRequest.Id)]),
        Case("long-term request", "caldaia", SearchCaller.Owner, w => [Request(w.Boiler.Id)]),
        Case("the long-term request needs the long-term permission", "caldaia", SearchCaller.ShortRentOnly, _ => []),
        Case("an active supplier the org asked", "idraulica", SearchCaller.Owner, w => [Supplier(w.PlumbersOrgId)]),
        Case("a supplier never asked is not found", "giardini", SearchCaller.Owner, _ => []),
        Case("a supplier asked, by its name", "splendore", SearchCaller.Owner, w => [Supplier(w.CleanersOrgId)]),
        Case("suppliers need the short-rent permission", "splendore", SearchCaller.LongRentOnly, _ => []),

        // ─── The org boundary ────────────────────────────────────────────────────────────────────────
        Case("the other org finds its own property", "trullo", SearchCaller.OtherOrg, w => [Property(w.Foreign.Id)]),
        Case("the other org finds its own guest, stay and request", "maria", SearchCaller.OtherOrg, w => [Booking(w.StayForeign.Id), Guest(w.ForeignMaria.Id)]),
        Case("the other org finds its own lease", "verdi", SearchCaller.OtherOrg, w => [Lease(w.LeaseForeign.Id)]),
        Case("the other org does not find the code of a stay of this org", "7K3M9-PQ2XV", SearchCaller.OtherOrg, _ => []),
        Case("this org does not find the code of a stay of the other", "XYZ9876543", SearchCaller.Owner, _ => []),

        // ─── The collaborator limited to one property ────────────────────────────────────────────────
        Case("collaborator: its property", "trullo", SearchCaller.Collaborator, w => [Property(w.Trullo.Id)]),
        Case("collaborator: another property", "casa", SearchCaller.Collaborator, _ => []),
        Case("collaborator: a city of another property", "forli", SearchCaller.Collaborator, _ => []),
        Case("collaborator: guests of its property only", "rossi", SearchCaller.Collaborator, w => [Booking(w.StayMarioAtTrullo.Id), Guest(w.MarioRossi.Id)]),
        Case("collaborator: a guest of another property", "maria", SearchCaller.Collaborator, _ => []),
        Case("collaborator: a guest with no stay", "carla", SearchCaller.Collaborator, _ => []),
        Case("collaborator: a stay of another property, by code", "A1B2C3D4E5", SearchCaller.Collaborator, _ => []),
        Case("collaborator: a stay of its property, by code", "ZXCV12345K", SearchCaller.Collaborator, w => [Booking(w.StayMarioAtTrullo.Id)]),
        Case("collaborator: its lease", "neri", SearchCaller.Collaborator, w => [Lease(w.LeaseNeri.Id)]),
        Case("collaborator: a lease of another property", "verdi", SearchCaller.Collaborator, _ => []),
        Case("collaborator: its request and its supplier", "splendore", SearchCaller.Collaborator, w => [Supplier(w.CleanersOrgId)]),
        Case("collaborator: a request of another property", "caldaia", SearchCaller.Collaborator, _ => []),
        Case("collaborator: a supplier of another property", "idraulica", SearchCaller.Collaborator, _ => []),
        Case("a collaborator given nothing finds nothing (name)", "trullo", SearchCaller.NobodyGranted, _ => []),
        Case("a collaborator given nothing finds nothing (guest)", "rossi", SearchCaller.NobodyGranted, _ => []),
        Case("a collaborator given nothing finds nothing (code)", "7K3M9-PQ2XV", SearchCaller.NobodyGranted, _ => []),

        // ─── The account in no team ──────────────────────────────────────────────────────────────────
        Case("account in no team: its guests", "rossi", SearchCaller.AccountInNoTeam, w =>
            [Booking(w.StayMarioAtCasaBella.Id, w.StayMarioAtTrullo.Id, w.StayMaria.Id), Guest(w.MariaRossi.Id, w.MarioRossi.Id), Supplier(w.PlumbersOrgId)]),
        Case("account in no team: a guest with no stay", "carla", SearchCaller.AccountInNoTeam, _ => []),

        // ─── The supplier ────────────────────────────────────────────────────────────────────────────
        Case("supplier: its inbox by category", "plumbing", SearchCaller.Plumbers, w => [Inbox(w.Plumbing.Id)]),
        Case("supplier: its inbox by the name of the service", "caldaia", SearchCaller.Plumbers, w => [Inbox(w.Boiler.Id)]),
        Case("supplier: the inbox of another", "pulizia", SearchCaller.Plumbers, _ => []),
        Case("supplier: both hosts' requests it received", "pulizia", SearchCaller.Cleaners, w => [Inbox(w.Cleaning.Id, w.ForeignRequest.Id)]),
        Case("supplier: nothing of the host", "rossi", SearchCaller.Plumbers, _ => []),
        Case("supplier: nothing of the properties", "trullo", SearchCaller.Plumbers, _ => []),
    ];

    public static IEnumerable<object[]> Names() => All.Select(c => new object[] { c.Name });

    public static SearchCase Named(string name) => All.Single(c => c.Name == name);

    /// <summary>The request the case asks for: the access of the caller on the world, the term, the limit of the page.</summary>
    public static GlobalSearchRequest RequestOf(SearchCase searchCase, SearchWorld world, int limit = SearchLimits.MaxLimit)
    {
        var (host, supplier) = searchCase.Caller switch
        {
            SearchCaller.Owner => (world.Everything(), (Guid?)null),
            SearchCaller.Collaborator => (world.Everything(world.Collaborator), null),
            SearchCaller.AccountInNoTeam => (world.Everything(world.OwnedByOwner), null),
            SearchCaller.OtherOrg => (world.Everything(world.OtherOrg), null),
            SearchCaller.ShortRentOnly => (new HostSearchAccess(world.OrgWide, true, false, true, true, false), null),
            SearchCaller.LongRentOnly => (new HostSearchAccess(world.OrgWide, false, true, false, false, true), null),
            SearchCaller.NobodyGranted => (world.Everything(new HostScope(world.OrgId, GrantedToUserId: "auth0|nessun-immobile")), null),
            SearchCaller.Plumbers => ((HostSearchAccess?)null, world.PlumbersOrgId),
            SearchCaller.Cleaners => (null, (Guid?)world.CleanersOrgId),
            _ => throw new ArgumentOutOfRangeException(nameof(searchCase)),
        };

        return new GlobalSearchRequest(SearchText.Parse(searchCase.Term), limit, host, supplier, Italian);
    }

    /// <summary>
    /// Runs one case and says what differs from the expected answer, or null when it is the one. The ids of a group are compared
    /// as sets (some groups are ordered by a time several rows share).
    /// </summary>
    public static async Task<string?> RunAsync(AppDbContext db, SearchWorld world, SearchCase searchCase)
    {
        var request = RequestOf(searchCase, world);
        var result = await new GlobalSearchService(db, NullLogger<GlobalSearchService>.Instance).SearchAsync(request);

        var expected = searchCase.Expected(world);
        var actual = result.Groups.ToDictionary(g => g.Type, g => g.Items.Select(i => i.Id).ToArray());

        var differences = new List<string>();
        foreach (var type in expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal))
        {
            var want = expected.TryGetValue(type, out var w) ? w.ToHashSet() : [];
            var got = actual.TryGetValue(type, out var g) ? g.ToHashSet() : [];
            if (!want.SetEquals(got))
            {
                differences.Add($"{type}: expected {want.Count} [{string.Join(", ", want.Order())}], found {got.Count} [{string.Join(", ", got.Order())}]");
            }
            else if (actual.TryGetValue(type, out var ordered) && ordered.Length != ordered.Distinct().Count())
            {
                differences.Add($"{type}: a result is there twice");
            }
        }

        return differences.Count == 0 ? null : $"{searchCase.Name} ('{searchCase.Term}' as {searchCase.Caller}): {string.Join("; ", differences)}";
    }
}
