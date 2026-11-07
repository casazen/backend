using System.Text;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Exceptions;
using Casazen.Core.Services;
using Casazen.Core.Utilities;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The owner portfolio of an agency (E10/F02/US02/T01, NF-109): the import of the owners and of their shares from a CSV, and the readiness of
/// every owner and property. The three proofs of the brief are the first three tests; the rest pin the rules around them (the shares, the periods
/// that are preserved, the secrets, the reach of a collaborator «Solo alcuni»).
/// </summary>
public class OwnerOnboardingServiceTests : IAsyncLifetime
{
    private const string Header = "propertyId,ownerKey,legalName,email,entityKind,taxResidence,fiscalCode,sharePercent,ownershipStart,ownershipEnd";
    private const string Admin = "auth0|admin";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private readonly CapturingLogger<OwnerOnboardingService> _log = new();
    private AppDbContext _db = null!;
    private HostScopeWorld _world = null!;

    private Guid Granted => _world.Granted.Id;

    private Guid Hidden => _world.Hidden.Id;

    public async Task InitializeAsync()
    {
        _db = NewDb();
        _world = await HostScopeScenario.SeedAsync(_db);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ---------------------------------------------------------------- the three proofs of the brief

    /// <summary>
    /// Proof (data, privacy): the import never sends an invitation and never merges two owners because they share an e-mail address. A row whose
    /// address is a stored owner's, with a key of its own, is a new owner; a row that has an address and nothing else to say who the owner is
    /// creates nothing; and no invitation exists after an import that wrote owners and periods.
    /// </summary>
    [Fact]
    public async Task OwnerImport_NeverInvitesOrMergesByEmailAlone_Test()
    {
        var existing = await AddOwnerAsync("Anna Verdi", "shared@example.com");

        // The address of another owner, a key of its own: a different person, created, not matched.
        var row = $"{Granted},NEW-1,Nuovo Proprietario,shared@example.com,person,IT,,100,2026-01-01,";
        var dry = await ImportAsync(Csv(Header, row), dryRun: true);
        var judged = Assert.Single(dry.Rows);
        Assert.Equal((OwnerImportRowCodes.Ok, OwnerImportActions.CreateOwner, (string?)null, (Guid?)null), (judged.Status, judged.OwnerAction, judged.MatchedBy, judged.OwnerId));
        Assert.Equal(1, await _db.PropertyOwners.CountAsync());

        // An address and a name, but no key and no fiscal identifier: nothing says who the owner is, so nothing is created.
        var emailOnly = await ImportAsync(Csv(Header, $"{Granted},,Solo Email,shared@example.com,person,IT,,100,2026-01-01,"), dryRun: false);
        var refused = Assert.Single(emailOnly.Rows);
        Assert.Equal((OwnerImportRowCodes.OwnerIdentityMissing, false), (refused.Status, refused.Applied));
        Assert.Equal(1, await _db.PropertyOwners.CountAsync());

        // The same address as the stored owner's, written as a different case, still identifies nobody.
        var applied = await ImportAsync(Csv(Header, row.Replace("shared@example.com", "SHARED@Example.com", StringComparison.Ordinal)), dryRun: false);
        Assert.Equal((OwnerImportRowCodes.Ok, true), (Assert.Single(applied.Rows).Status, Assert.Single(applied.Rows).Applied));

        var owners = await _db.PropertyOwners.AsNoTracking().ToListAsync();
        Assert.Equal(2, owners.Count);
        Assert.Equal(2, owners.Count(o => string.Equals(o.Email, "shared@example.com", StringComparison.OrdinalIgnoreCase)));
        var stored = owners.Single(o => o.Id == existing);
        Assert.Equal((null, null, null), (stored.ExternalKey, stored.LegalName, stored.FiscalCode));

        // Owners and periods were written, and not one invitation: the import has no way to send it.
        Assert.Single(await _db.PropertyOwnerships.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.OwnerInvitations.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Proof (money, data): the shares of a property add up to exactly 100.00 on every day. The readiness names the property and the kind of the
    /// fault (shortfall, excess, gap, an owner twice) and keeps the owners to complete; and the import writes none of the rows of a property whose
    /// shares would not be exactly 100.00 after the file.
    /// </summary>
    [Fact]
    public async Task OwnerReadiness_EffectiveSharesMustEqualOneHundred_Test()
    {
        var a = await AddOwnerAsync("Anna Verdi", "anna@example.com");
        var b = await AddOwnerAsync("Bruno Neri", "bruno@example.com");
        await AddPeriodAsync(Granted, a, 60m, new DateOnly(2025, 1, 1));
        await AddPeriodAsync(Granted, b, 30m, new DateOnly(2025, 1, 1));

        // 90%: the shares are short, the property is to complete, and the owner accounting is not ready.
        var report = await Service().GetReadinessAsync(_world.OrgWide, Ct);
        var property = Assert.Single(report.Properties, p => p.PropertyId == Granted);
        Assert.Equal([OwnerReadinessReasons.SharesShortfall], property.Reasons.Select(r => r.Code));
        Assert.Equal(90m, Assert.Single(property.Issues).TotalPercent);
        Assert.All(report.Owners.Where(o => o.OwnerId == a || o.OwnerId == b), o => Assert.False(o.Ready.Ownership));
        Assert.False(report.Summary.FinancialReady);

        // The import cannot make it worse or leave it as it is: 90 stays 90, and a row that tries to add 20 makes it 110.
        var csv = Csv(Header, $"{Granted},NEW-1,Carla Gialli,carla@example.com,person,IT,,20,2025-01-01,");
        var excess = await ImportAsync(csv, dryRun: false);
        var excessRow = Assert.Single(excess.Rows);
        Assert.Equal((OwnerImportRowCodes.OwnershipSharesInvalid, false), (excessRow.Status, excessRow.Applied));
        var issue = Assert.Single(Assert.Single(excess.PropertyIssues).Issues);
        Assert.Equal((OwnershipIssueKind.Excess, 110m), (issue.Kind, issue.TotalPercent));
        Assert.Equal(2, await _db.PropertyOwnerships.CountAsync());
        Assert.Equal(2, await _db.PropertyOwners.CountAsync());

        // 10 completes it to exactly 100.00: written, and the readiness of the ownership is ready for all three.
        var exact = await ImportAsync(Csv(Header, $"{Granted},NEW-1,Carla Gialli,carla@example.com,person,IT,,10,2025-01-01,"), dryRun: false);
        Assert.Equal((OwnerImportRowCodes.Ok, true), (Assert.Single(exact.Rows).Status, Assert.Single(exact.Rows).Applied));
        var ready = await Service().GetReadinessAsync(_world.OrgWide, Ct);
        Assert.DoesNotContain(ready.Properties, p => p.PropertyId == Granted);
        Assert.Equal(3, ready.Owners.Count(o => o.Ready.Ownership));

        // 33.33 + 33.33 + 33.33 is 99.99, not 100: never rounded.
        await AddPeriodAsync(Hidden, a, 33.33m, new DateOnly(2025, 1, 1));
        await AddPeriodAsync(Hidden, b, 33.33m, new DateOnly(2025, 1, 1));
        await AddPeriodAsync(Hidden, ownerId: await AddOwnerAsync("Carlo Blu", "carlo@example.com"), 33.33m, new DateOnly(2025, 1, 1));
        var thirds = await Service().GetReadinessAsync(_world.OrgWide, Ct);
        var hidden = Assert.Single(thirds.Properties, p => p.PropertyId == Hidden);
        Assert.Equal(OwnerReadinessReasons.SharesShortfall, Assert.Single(hidden.Reasons).Code);
        Assert.Equal(99.99m, Assert.Single(hidden.Issues).TotalPercent);
    }

    /// <summary>
    /// Proof (tenant isolation, permission): a row that names a property of another org, by id or by CIN, or one the caller does not reach, is
    /// <c>property_not_found</c>, the same answer as a property that does not exist; nothing is written for it, and the valid rows of the file still
    /// are.
    /// </summary>
    [Fact]
    public async Task OwnerImport_OtherOrganizationPropertyIsNotFound_Test()
    {
        const string foreignCin = "IT058091C27OTHERX";
        const string hiddenCin = "IT015146C21H2JKLMN";
        await SetCinAsync(_world.OtherOrgProperty.Id, foreignCin);
        await SetCinAsync(Hidden, hiddenCin);
        var nobody = Guid.NewGuid();
        var cinHeader = "propertyId,cinCode,ownerKey,legalName,sharePercent,ownershipStart";
        var csv = Csv(
            cinHeader,
            $"{_world.OtherOrgProperty.Id},,F-1,Altrui,100,2026-01-01",
            $",{foreignCin},F-2,Altrui,100,2026-01-01",
            $"{nobody},,F-3,Nessuno,100,2026-01-01",
            $"{Granted},,G-1,Nostro,100,2026-01-01");

        var result = await ImportAsync(csv, dryRun: false);

        // Another org's property, by id or by CIN, and one that does not exist: one and the same answer.
        Assert.All(result.Rows.Take(3), row => Assert.Equal((OwnerImportRowCodes.PropertyNotFound, false, false), (row.Status, row.PropertyId.HasValue, row.Applied)));
        Assert.Equal(result.Rows[0].Errors.Select(e => e.Column), result.Rows[2].Errors.Select(e => e.Column));
        Assert.Equal((OwnerImportRowCodes.Ok, true), (result.Rows[3].Status, result.Rows[3].Applied));

        // Nothing was written on the other org's property, and only the valid row created an owner.
        Assert.Empty(await _db.PropertyOwnerships.AsNoTracking().Where(p => p.PropertyId == _world.OtherOrgProperty.Id).ToListAsync());
        Assert.Equal(["G-1"], (await _db.PropertyOwners.AsNoTracking().ToListAsync()).Select(o => o.ExternalKey));

        // The collaborator «Solo alcuni» reaches Granted only: Hidden is not found for it, by id or by CIN.
        var restricted = await ImportAsync(
            Csv(cinHeader, $"{Hidden},,H-1,Nascosto,100,2026-01-01", $",{hiddenCin},H-2,Nascosto,100,2026-01-01", $"{Granted},,G-1,Nostro,100,2026-01-01"),
            dryRun: false,
            scope: _world.Restricted);
        Assert.Equal(
            [OwnerImportRowCodes.PropertyNotFound, OwnerImportRowCodes.PropertyNotFound, OwnerImportRowCodes.Ok],
            restricted.Rows.Select(r => r.Status));
        Assert.Empty(await _db.PropertyOwnerships.AsNoTracking().Where(p => p.PropertyId == Hidden).ToListAsync());
    }

    // ---------------------------------------------------------------- the import

    [Fact]
    public async Task Import_ADryRunWritesNothing_TheSameFileAppliedTwiceChangesNothingTheSecondTime()
    {
        var csv = Csv(
            Header,
            $"{Granted},A-1,Anna Verdi,anna@example.com,person,IT,RSSMRA85T10A562S,50,2026-01-01,",
            $"{Granted},B-1,Bruno Neri,bruno@example.com,person,IT,VRDGLI85B42F205E,50,2026-01-01,");

        var dry = await ImportAsync(csv, dryRun: true);
        Assert.Equal(
            (true, 2, 2, 0, 2, 0, 2),
            (dry.DryRun, dry.Summary.Rows, dry.Summary.Valid, dry.Summary.Applied, dry.Summary.OwnersToCreate, dry.Summary.OwnersMatched, dry.Summary.OwnershipsToCreate));
        Assert.Empty(await _db.PropertyOwners.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.PropertyOwnerships.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.OrgActivityEntries.AsNoTracking().ToListAsync());

        var first = await ImportAsync(csv, dryRun: false);
        Assert.Equal((2, 2, 2), (first.Summary.Rows, first.Summary.Valid, first.Summary.Applied));
        Assert.Equal(dry.Fingerprint, first.Fingerprint);
        Assert.Equal(2, await _db.PropertyOwners.CountAsync());
        Assert.Equal(2, await _db.PropertyOwnerships.CountAsync());

        // The second time every row finds what it says: matched, unchanged, nothing applied, no line in the log.
        var second = await ImportAsync(csv, dryRun: false);
        Assert.Equal((2, 0), (second.Summary.Valid, second.Summary.Applied));
        Assert.All(second.Rows, row => Assert.Equal((OwnerImportRowCodes.Ok, OwnerImportActions.MatchOwner, OwnerImportActions.Unchanged, false), (row.Status, row.OwnerAction, row.OwnershipAction, row.Applied)));
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(2, await _db.PropertyOwners.CountAsync());
        Assert.Equal(2, await _db.PropertyOwnerships.CountAsync());

        // One line for the import that changed something, with counts and a hash: no name, no code.
        var line = Assert.Single(await _db.OrgActivityEntries.AsNoTracking().ToListAsync());
        Assert.Equal((OrgActivityType.OwnerImportApplied, Admin), (line.Type, line.ActorUserId));
        Assert.DoesNotContain("Verdi", line.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain("RSSMRA", line.DetailsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_FiscalCodeAndIbanAreStoredEncrypted_AndAppearInNoAnswerAndNoLog()
    {
        const string Fiscal = "RSSMRA85T10A562S";
        const string Vat = "01234567897";
        const string Iban = "IT60X0542811101000000123456";
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"owners-secret-{Guid.NewGuid()}").Options;
        var provider = new EphemeralDataProtectionProvider();
        AppDbContext Keyed() => new(options, null, provider);
        HostScopeWorld world;
        await using (var seed = Keyed())
            world = await HostScopeScenario.SeedAsync(seed);

        const string Columns = "propertyId,ownerKey,legalName,email,entityKind,taxResidence,fiscalCode,vatNumber,iban,settlementCurrency,sharePercent,ownershipStart";
        var csv = Csv(Columns, $"{world.Granted.Id},A-1,Rossi Immobiliare,rossi@example.com,company,IT,{Fiscal},{Vat},{Iban},EUR,100,2026-01-01");

        OwnerImportResult result;
        await using (var db = Keyed())
            result = await Service(db).ImportAsync(world.OrgWide, Admin, "owners.csv", Encoding.UTF8.GetBytes(csv), dryRun: false, Ct);

        // The answer and the log hold the codes of the rows, never the identifiers or the bank data.
        var echoed = JsonSerializer.Serialize(result) + result;
        foreach (var secret in new[] { Fiscal, Vat, Iban })
        {
            Assert.DoesNotContain(secret, echoed, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, _log.AllOutput, StringComparison.Ordinal);
        }

        // Stored encrypted: the columns have the converter of the owner purpose, which writes a payload that is not the value and reads it back.
        await using var withKey = Keyed();
        var stored = Assert.Single(await withKey.PropertyOwners.AsNoTracking().ToListAsync());
        Assert.Equal((Fiscal, Vat, Iban), (stored.FiscalCode, stored.VatNumber, stored.Iban));
        foreach (var (property, value) in new[] { (nameof(PropertyOwner.FiscalCode), Fiscal), (nameof(PropertyOwner.VatNumber), Vat), (nameof(PropertyOwner.Iban), Iban) })
        {
            var converter = Assert.IsType<EncryptedStringConverter>(withKey.Model.FindEntityType(typeof(PropertyOwner))!.FindProperty(property)!.GetValueConverter());
            var payload = Assert.IsType<string>(converter.ConvertToProvider(value));
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, payload, StringComparison.Ordinal);
            Assert.DoesNotContain(value, payload, StringComparison.Ordinal);
            Assert.Equal(value, converter.ConvertFromProvider(payload));
        }
    }

    [Fact]
    public async Task Import_AFiscalCodeFindsTheOwner_AndTwoOwnersWithTheSameOneAreAmbiguousUntilTheRowSaysWhich()
    {
        var first = await AddOwnerAsync("Anna Verdi", "anna@example.com", fiscalCode: "RSSMRA85T10A562S", key: "A-1");
        var second = await AddOwnerAsync("Anna Verdi bis", "anna2@example.com", fiscalCode: "RSSMRA85T10A562S", key: "A-2");
        await AddOwnerAsync("Bruno Neri", "bruno@example.com", fiscalCode: "VRDGLI85B42F205E", key: "B-1");

        // One owner has this fiscal code: the row is matched by it (no key, no e-mail needed).
        var byFiscal = await ImportAsync(Csv(Header, $"{Granted},,Bruno N.,bruno@example.com,person,IT,VRDGLI85B42F205E,100,2026-01-01,"), dryRun: true);
        var matched = Assert.Single(byFiscal.Rows);
        Assert.Equal((OwnerImportActions.MatchOwner, OwnerMatchKinds.FiscalIdentifier), (matched.OwnerAction, matched.MatchedBy));

        // Two owners hold it: ambiguous, with both listed, and nothing is guessed.
        var ambiguous = await ImportAsync(Csv(Header, $"{Granted},,Anna,anna@example.com,person,IT,RSSMRA85T10A562S,100,2026-01-01,"), dryRun: false);
        var row = Assert.Single(ambiguous.Rows);
        Assert.Equal((OwnerImportRowCodes.OwnerAmbiguous, OwnerImportActions.Conflict, false), (row.Status, row.OwnerAction, row.Applied));
        Assert.Equal(new[] { first, second }.Order(), row.Candidates.Select(c => c.OwnerId).Order());
        Assert.Empty(await _db.PropertyOwnerships.AsNoTracking().ToListAsync());

        // The manual choice: the ownerId of the row.
        var chosen = await ImportAsync(
            Csv("propertyId,ownerId,sharePercent,ownershipStart", $"{Granted},{second},100,2026-01-01"),
            dryRun: false);
        var picked = Assert.Single(chosen.Rows);
        Assert.Equal((OwnerImportRowCodes.Ok, OwnerMatchKinds.OwnerId, second), (picked.Status, picked.MatchedBy, picked.OwnerId));
        Assert.Equal(second, Assert.Single(await _db.PropertyOwnerships.AsNoTracking().ToListAsync()).PropertyOwnerId);
    }

    [Fact]
    public async Task Import_AValueTheOwnerHoldsIsNeverOverwritten_AnEmptyOneIsFilled()
    {
        var owner = await AddOwnerAsync("Anna Verdi", "anna@example.com", key: "A-1", iban: "IT60X0542811101000000123456");

        // A different IBAN for the same key: a conflict for a person to decide, the stored one stays.
        var conflict = await ImportAsync(
            Csv("propertyId,ownerKey,iban,sharePercent,ownershipStart", $"{Granted},A-1,CH9300762011623852957,100,2026-01-01"),
            dryRun: false);
        var refused = Assert.Single(conflict.Rows);
        Assert.Equal(OwnerImportRowCodes.OwnerFieldConflict, refused.Status);
        Assert.Equal(OwnerImportColumns.Iban, Assert.Single(refused.Errors).Column);
        Assert.Equal("IT60X0542811101000000123456", (await _db.PropertyOwners.AsNoTracking().SingleAsync(o => o.Id == owner)).Iban);
        Assert.Empty(await _db.PropertyOwnerships.AsNoTracking().ToListAsync());

        // The same IBAN, and a residence the owner did not have: filled.
        var filled = await ImportAsync(
            Csv("propertyId,ownerKey,iban,taxResidence,sharePercent,ownershipStart", $"{Granted},A-1,IT60 X054 2811 1010 0000 0123 456,CH,100,2026-01-01"),
            dryRun: false);
        Assert.Equal((OwnerImportRowCodes.Ok, true), (Assert.Single(filled.Rows).Status, Assert.Single(filled.Rows).Applied));
        var stored = await _db.PropertyOwners.AsNoTracking().SingleAsync(o => o.Id == owner);
        Assert.Equal(("CH", "IT60X0542811101000000123456"), (stored.TaxResidenceCountry, stored.Iban));
    }

    [Fact]
    public async Task Import_APeriodThatEndedIsHistory_APeriodInForceCanOnlyBeClosed_AFutureOneCanChange()
    {
        var owner = await AddOwnerAsync("Anna Verdi", "anna@example.com", key: "A-1");
        var other = await AddOwnerAsync("Bruno Neri", "bruno@example.com", key: "B-1");
        // Today is 2026-10-09. A held the property until 2024; B holds it since 2025, in force.
        await AddPeriodAsync(Granted, owner, 100m, new DateOnly(2020, 1, 1), new DateOnly(2024, 12, 31));
        var inForce = await AddPeriodAsync(Granted, other, 100m, new DateOnly(2025, 1, 1));
        var columns = "propertyId,ownerKey,legalName,sharePercent,ownershipStart,ownershipEnd";

        // The ended period cannot be rewritten: a different share is a conflict and nothing changes.
        var history = await ImportAsync(Csv(columns, $"{Granted},A-1,,50,2020-01-01,2024-12-31"), dryRun: false);
        Assert.Equal(OwnerImportRowCodes.OwnershipPeriodConflict, Assert.Single(history.Rows).Status);
        Assert.Equal(100m, (await _db.PropertyOwnerships.AsNoTracking().SingleAsync(p => p.PropertyOwnerId == owner)).SharePercent);

        // A period in force keeps its share: changing it is a conflict.
        var share = await ImportAsync(Csv(columns, $"{Granted},B-1,,60,2025-01-01,"), dryRun: false);
        Assert.Equal(OwnerImportRowCodes.OwnershipPeriodConflict, Assert.Single(share.Rows).Status);

        // It cannot be ended in the past either (that would rewrite days already lived)...
        var past = await ImportAsync(Csv(columns, $"{Granted},B-1,,100,2025-01-01,2026-03-31"), dryRun: false);
        Assert.Equal(OwnerImportRowCodes.OwnershipPeriodConflict, Assert.Single(past.Rows).Status);

        // ...but it can be closed from today on, when someone takes over the day after.
        var close = await ImportAsync(
            Csv(columns, $"{Granted},B-1,,100,2025-01-01,2026-12-31", $"{Granted},C-1,Carla Gialli,100,2027-01-01,"),
            dryRun: false);
        Assert.Equal([OwnerImportRowCodes.Ok, OwnerImportRowCodes.Ok], close.Rows.Select(r => r.Status));
        Assert.Equal(OwnerImportActions.UpdateFuturePeriod, close.Rows[0].OwnershipAction);
        var closed = await _db.PropertyOwnerships.AsNoTracking().SingleAsync(p => p.Id == inForce);
        Assert.Equal(new DateOnly(2026, 12, 31), RomeCalendar.DateInRome(closed.OwnershipEnd!.Value));
    }

    [Fact]
    public async Task Import_TheSameOwnerOnTheSameDaysTwice_IsAnOverlapAndNothingIsWritten()
    {
        var owner = await AddOwnerAsync("Anna Verdi", "anna@example.com", key: "A-1");
        await AddPeriodAsync(Granted, owner, 100m, new DateOnly(2025, 1, 1));

        var result = await ImportAsync(Csv("propertyId,ownerKey,sharePercent,ownershipStart", $"{Granted},A-1,100,2026-03-01"), dryRun: false);

        var row = Assert.Single(result.Rows);
        Assert.Equal(OwnerImportRowCodes.OwnershipOverlap, row.Status);
        Assert.Single(await _db.PropertyOwnerships.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_ARowThatIsNotValid_NeverStopsTheValidRowsOfOtherProperties()
    {
        var columns = "propertyId,ownerKey,legalName,email,sharePercent,ownershipStart,iban";
        var csv = Csv(
            columns,
            $"{Granted},A-1,Anna Verdi,anna@example.com,100,2026-01-01,IT60X0542811101000000123456",
            $"{Hidden},B-1,Bruno Neri,not an address,100,2026-01-01,IT60X0542811101000000123457");

        var result = await ImportAsync(csv, dryRun: false);

        Assert.Equal([OwnerImportRowCodes.Ok, OwnerImportRowCodes.EmailInvalid], result.Rows.Select(r => r.Status));
        Assert.Equal(
            [OwnerImportColumns.Email, OwnerImportColumns.Iban],
            result.Rows[1].Errors.Select(e => e.Column));
        Assert.Equal(["A-1"], (await _db.PropertyOwners.AsNoTracking().ToListAsync()).Select(o => o.ExternalKey));
    }

    [Fact]
    public async Task Import_ARowWithNoPropertyCreatesTheOwnerWithoutAnOwnership()
    {
        var result = await ImportAsync(
            Csv("ownerKey,legalName,email,entityKind,taxResidence,fiscalCode", "A-1,Anna Verdi,anna@example.com,person,IT,RSSMRA85T10A562S"),
            dryRun: false);

        var row = Assert.Single(result.Rows);
        Assert.Equal((OwnerImportRowCodes.Ok, OwnerImportActions.CreateOwner, (string?)null, true), (row.Status, row.OwnerAction, row.OwnershipAction, row.Applied));
        var owner = Assert.Single(await _db.PropertyOwners.AsNoTracking().ToListAsync());
        Assert.Equal(("A-1", "Anna Verdi", OwnerEntityKind.NaturalPerson, "Anna", "Verdi"), (owner.ExternalKey, owner.LegalName, owner.EntityKind, owner.FirstName, owner.LastName));
        Assert.Equal(_world.OrgId, owner.OrgId);
        Assert.Empty(await _db.PropertyOwnerships.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_AShareAndADayWithNoProperty_IsPropertyNotFound()
    {
        var result = await ImportAsync(Csv("ownerKey,legalName,sharePercent,ownershipStart", "A-1,Anna Verdi,100,2026-01-01"), dryRun: false);

        Assert.Equal(OwnerImportRowCodes.PropertyNotFound, Assert.Single(result.Rows).Status);
        Assert.Empty(await _db.PropertyOwners.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_TwoRowsOfTheSameNewOwner_AreOneOwnerWithTwoShares()
    {
        var csv = Csv(
            Header,
            $"{Granted},A-1,Anna Verdi,anna@example.com,person,IT,RSSMRA85T10A562S,50,2026-01-01,",
            $"{Hidden},,,,,,RSSMRA85T10A562S,100,2026-01-01,",
            $"{Granted},B-1,Bruno Neri,bruno@example.com,person,IT,VRDGLI85B42F205E,50,2026-01-01,");

        var result = await ImportAsync(csv, dryRun: false);

        Assert.Equal([OwnerImportRowCodes.Ok, OwnerImportRowCodes.Ok, OwnerImportRowCodes.Ok], result.Rows.Select(r => r.Status));
        Assert.Equal(
            [OwnerImportActions.CreateOwner, OwnerImportActions.MatchOwner, OwnerImportActions.CreateOwner],
            result.Rows.Select(r => r.OwnerAction));
        Assert.Equal(2, await _db.PropertyOwners.CountAsync());
        Assert.Equal(3, await _db.PropertyOwnerships.CountAsync());
        Assert.Equal(2, (await _db.PropertyOwnerships.AsNoTracking().ToListAsync()).Count(p => p.PropertyOwnerId == result.Rows[0].OwnerId));
    }

    [Fact]
    public async Task Import_TheDuplicateRowOfTheSamePeriod_IsRefused()
    {
        var row = $"{Granted},A-1,Anna Verdi,anna@example.com,person,IT,,100,2026-01-01,";

        var result = await ImportAsync(Csv(Header, row, row), dryRun: false);

        Assert.Equal([OwnerImportRowCodes.Ok, OwnerImportRowCodes.DuplicateRow], result.Rows.Select(r => r.Status));
        Assert.Single(await _db.PropertyOwnerships.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Import_AMandateOfTheAgencyMustStayInsideThePeriod()
    {
        var owner = await AddOwnerAsync("Anna Verdi", "anna@example.com", key: "A-1");
        var period = await AddPeriodAsync(Granted, owner, 100m, new DateOnly(2027, 1, 1));
        _db.OwnerMandates.Add(new OwnerMandate
        {
            OrgId = _world.OrgId,
            PropertyOwnershipId = period,
            MandatePdfUrl = "mandates/a.pdf",
            EffectiveFrom = HostScopeScenario.Day(2027, 1, 1),
        });
        await _db.SaveChangesAsync();

        // The period would start after its mandate: the mandate is outside it.
        var late = await ImportAsync(Csv("propertyId,ownerKey,sharePercent,ownershipStart", $"{Granted},A-1,100,2027-01-01"), dryRun: false);
        Assert.Equal(OwnerImportActions.Unchanged, Assert.Single(late.Rows).OwnershipAction);
        var moved = await ImportAsync(Csv("propertyId,ownerKey,sharePercent,ownershipStart,ownershipEnd", $"{Granted},A-1,100,2027-01-01,2027-06-30"), dryRun: false);
        Assert.Equal(OwnerImportRowCodes.MandateOutsideOwnership, Assert.Single(moved.Rows).Status);
    }

    [Fact]
    public async Task Import_TheFileRules_ColumnsAndSizeAreTheOnesOfTheBulkImport()
    {
        // No column that identifies the owner.
        var noIdentity = await Assert.ThrowsAsync<DomainRuleException>(() => ImportAsync(Csv("legalName,email", "Anna,a@example.com"), dryRun: true));
        Assert.Equal(BulkImportCodes.ColumnsUnrecognized, noIdentity.Code);

        // Only the header.
        var empty = await Assert.ThrowsAsync<DomainRuleException>(() => ImportAsync(Csv(Header), dryRun: true));
        Assert.Equal(BulkImportCodes.FileEmpty, empty.Code);

        // A type that is not CSV or Excel.
        var wrongType = await Assert.ThrowsAsync<DomainUnsupportedMediaTypeException>(
            () => Service().ImportAsync(_world.OrgWide, Admin, "owners.pdf", Encoding.UTF8.GetBytes(Csv(Header)), dryRun: true, Ct));
        Assert.Equal(BulkImportCodes.FileTypeUnsupported, wrongType.Code);

        // More rows than the import takes.
        var many = Csv([Header, .. Enumerable.Range(0, BulkImportLimits.MaxRows + 1).Select(i => $"{Granted},K-{i},Nome {i},,,,,100,2026-01-01,")]);
        var tooMany = await Assert.ThrowsAsync<DomainRuleException>(() => ImportAsync(many, dryRun: true));
        Assert.Equal(BulkImportCodes.TooManyRows, tooMany.Code);

        // A file bigger than 2 MB.
        var big = new byte[BulkImportLimits.MaxFileBytes + 1];
        var tooLarge = await Assert.ThrowsAsync<DomainPayloadTooLargeException>(
            () => Service().ImportAsync(_world.OrgWide, Admin, "owners.csv", big, dryRun: true, Ct));
        Assert.Equal(BulkImportCodes.FileTooLarge, tooLarge.Code);
    }

    [Fact]
    public async Task Import_ACollaboratorWithSomePropertiesWritesNothingTiedToNoProperty_AndCannotCompleteAnOwnerOfPropertiesItDoesNotReach()
    {
        var shared = await AddOwnerAsync("Anna Verdi", "anna@example.com", key: "A-1");
        var alone = await AddOwnerAsync("Carla Gialli", "carla@example.com", key: "C-1");
        // Anna holds half of Granted (inside the reach) and the whole of Hidden (outside it); Carla only the other half of Granted.
        await AddPeriodAsync(Granted, shared, 50m, new DateOnly(2025, 1, 1));
        await AddPeriodAsync(Hidden, shared, 100m, new DateOnly(2025, 1, 1));
        await AddPeriodAsync(Granted, alone, 50m, new DateOnly(2025, 1, 1));
        var iban = "IT60X0542811101000000123456";

        var result = await ImportAsync(
            Csv(
                "propertyId,ownerKey,legalName,iban,sharePercent,ownershipStart",
                // No property: it would create an owner of the whole org.
                ",N-1,Nuovo Proprietario,,,",
                // Anna also holds Hidden, which the caller does not reach: the row may repeat her period, not give her an IBAN.
                $"{Granted},A-1,,{iban},50,2025-01-01",
                $"{Granted},A-1,,,50,2025-01-01",
                // Carla holds nothing outside the reach: she can be completed.
                $"{Granted},C-1,,{iban},50,2025-01-01"),
            dryRun: false,
            scope: _world.Restricted);

        Assert.Equal(
            [OwnerImportRowCodes.PropertyNotFound, OwnerImportRowCodes.OwnerSharedOutsideReach, OwnerImportRowCodes.Ok, OwnerImportRowCodes.Ok],
            result.Rows.Select(r => r.Status));
        Assert.Equal([false, false, false, true], result.Rows.Select(r => r.Applied));
        Assert.Null((await _db.PropertyOwners.AsNoTracking().SingleAsync(o => o.Id == shared)).Iban);
        Assert.Equal(iban, (await _db.PropertyOwners.AsNoTracking().SingleAsync(o => o.Id == alone)).Iban);
        Assert.Equal(2, await _db.PropertyOwners.CountAsync());
    }

    [Fact]
    public async Task Dtos_TheDaysAreFormattedAsDays_ForRealValues()
    {
        // A property whose only owner left in 2025: nobody holds it since 1 January 2026 (gap) and the days are strings of the API.
        var owner = await AddOwnerAsync("Anna Verdi", "anna@example.com", key: "A-1");
        await AddPeriodAsync(Granted, owner, 100m, new DateOnly(2024, 1, 1), new DateOnly(2025, 12, 31));

        var report = await Service().GetReadinessAsync(_world.OrgWide, Ct);
        var dto = Casazen.Web.DTOs.Onboarding.OwnerReadinessDto.From(report);

        Assert.Equal("2026-10-09", dto.AsOf);
        var issue = Assert.Single(Assert.Single(dto.Properties, p => p.PropertyId == Granted).Issues);
        Assert.Equal(("gap", "2026-01-01", "2026-10-09", 0m), (issue.Kind, issue.From, issue.To, issue.TotalPercent));

        // The import's shares issues travel the same way.
        var result = await ImportAsync(Csv("propertyId,ownerKey,legalName,sharePercent,ownershipStart", $"{Granted},B-1,Bruno Neri,60,2026-06-01"), dryRun: true);
        var imported = Casazen.Web.DTOs.Onboarding.OwnerImportResultDto.From(result);
        var shares = Assert.Single(Assert.Single(imported.PropertyIssues).Issues);
        Assert.Equal(("shortfall", "2026-06-01", 60m), (shares.Kind, shares.From, shares.TotalPercent));
    }

    // ---------------------------------------------------------------- the readiness

    [Fact]
    public async Task Readiness_NamesTheExactMissingFact_ForEachDimension_AndNeverBlocksThePortal()
    {
        var owner = await AddOwnerAsync("Anna Verdi", "anna@example.com", key: "A-1");
        await AddPeriodAsync(Granted, owner, 100m, new DateOnly(2025, 1, 1));

        var report = await Service().GetReadinessAsync(_world.OrgWide, Ct);

        var row = Assert.Single(report.Owners);
        // The registry lacks the kind; the mandate, the fiscal profile and the payment lack their facts; the ownership and the portal are ready.
        Assert.Equal(new OwnerReadinessFlags(false, true, false, false, false, true), row.Ready);
        Assert.False(row.AccountingReady);
        Assert.Equal(OwnerPortalStates.Ready, row.PortalState);
        Assert.Contains(row.Reasons, r => r is { Dimension: OwnerReadinessDimensions.Registry, Code: OwnerReadinessReasons.EntityKindMissing });
        Assert.Contains(row.Reasons, r => r is { Dimension: OwnerReadinessDimensions.Mandate, Code: OwnerReadinessReasons.MandateMissing } && r.PropertyId == Granted);
        Assert.Contains(row.Reasons, r => r is { Dimension: OwnerReadinessDimensions.FiscalProfile, Code: OwnerReadinessReasons.FiscalIdMissing });
        Assert.Contains(row.Reasons, r => r is { Dimension: OwnerReadinessDimensions.Payment, Code: OwnerReadinessReasons.IbanMissing });
        Assert.False(report.Summary.FinancialReady);
        Assert.Equal((0, 1), (report.Summary.AccountingReadyOwners, report.Summary.Owners - report.Summary.AccountingReadyOwners));

        // The answer has no value of the owner: no e-mail, no fiscal code, no IBAN.
        var json = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("anna@example.com", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_AnOwnerWithEverythingIsReadyForTheAccounting_AndTheFinancialReadinessFollows()
    {
        var owner = await AddOwnerAsync("Anna Verdi", "anna@example.com", key: "A-1", complete: true);
        var period = await AddPeriodAsync(Granted, owner, 100m, new DateOnly(2025, 1, 1));
        await AddPeriodAsync(Hidden, owner, 100m, new DateOnly(2025, 1, 1));
        _db.OwnerMandates.Add(new OwnerMandate
        {
            OrgId = _world.OrgId,
            PropertyOwnershipId = period,
            MandatePdfUrl = "mandates/a.pdf",
            EffectiveFrom = HostScopeScenario.Day(2025, 1, 1),
        });
        await _db.SaveChangesAsync();

        var partial = await Service().GetReadinessAsync(_world.OrgWide, Ct);

        // Hidden has no mandate: the owner is not ready, with the exact property named.
        var row = Assert.Single(partial.Owners);
        Assert.False(row.Ready.Mandate);
        Assert.Equal(Hidden, Assert.Single(row.Reasons, r => r.Code == OwnerReadinessReasons.MandateMissing).PropertyId);

        var hiddenPeriod = (await _db.PropertyOwnerships.AsNoTracking().SingleAsync(p => p.PropertyId == Hidden)).Id;
        _db.OwnerMandates.Add(new OwnerMandate
        {
            OrgId = _world.OrgId,
            PropertyOwnershipId = hiddenPeriod,
            MandatePdfUrl = "mandates/b.pdf",
            EffectiveFrom = HostScopeScenario.Day(2025, 1, 1),
        });
        await _db.SaveChangesAsync();

        var ready = await Service().GetReadinessAsync(_world.OrgWide, Ct);

        Assert.True(Assert.Single(ready.Owners).AccountingReady);
        Assert.Equal((true, 1, 1), (ready.Summary.FinancialReady, ready.Summary.AccountingReadyOwners, ready.Summary.Owners));
    }

    [Fact]
    public async Task Readiness_ACollaboratorWithSomePropertiesSeesOnlyTheirs_AndTheirOwners()
    {
        var onGranted = await AddOwnerAsync("Anna Verdi", "anna@example.com");
        var onHidden = await AddOwnerAsync("Bruno Neri", "bruno@example.com");
        await AddPeriodAsync(Granted, onGranted, 100m, new DateOnly(2025, 1, 1));
        await AddPeriodAsync(Hidden, onHidden, 100m, new DateOnly(2025, 1, 1));

        var orgWide = await Service().GetReadinessAsync(_world.OrgWide, Ct);
        var restricted = await Service().GetReadinessAsync(_world.Restricted, Ct);

        Assert.Equal(2, orgWide.Owners.Count);
        var visible = Assert.Single(restricted.Owners);
        Assert.Equal(onGranted, visible.OwnerId);
        Assert.Equal(1, restricted.Summary.Properties);
        Assert.DoesNotContain(restricted.Properties, p => p.PropertyId == Hidden);
    }

    [Fact]
    public async Task Readiness_AnOwnerOfAnotherOrgNeverAppears()
    {
        _db.PropertyOwners.Add(new PropertyOwner { OrgId = _world.OtherOrgId, FirstName = "Altro", LastName = "Org", Email = "altro@example.com", CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        var report = await Service().GetReadinessAsync(_world.OrgWide, Ct);

        Assert.Empty(report.Owners);
    }

    // ---------------------------------------------------------------- helpers

    private AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).Options);

    private OwnerOnboardingService Service(AppDbContext? db = null)
    {
        var context = db ?? NewDb();
        return new OwnerOnboardingService(context, new ActivityLog(context, _clock), _clock, _log);
    }

    /// <summary>One request: a context of its own on the same database, as the API gives each call.</summary>
    private async Task<OwnerImportResult> ImportAsync(string csv, bool dryRun, HostScope? scope = null)
    {
        await using var db = NewDb();
        return await Service(db).ImportAsync(scope ?? _world.OrgWide, Admin, "owners.csv", Encoding.UTF8.GetBytes(csv), dryRun, Ct);
    }

    private static string Csv(params string[] lines) => string.Join("\n", lines);

    private async Task SetCinAsync(Guid propertyId, string cin)
    {
        var property = await _db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == propertyId);
        property.CinCode = cin;
        await _db.SaveChangesAsync();
    }

    private async Task<Guid> AddOwnerAsync(string name, string email, string? fiscalCode = null, string? key = null, string? iban = null, bool complete = false)
    {
        var parts = name.Split(' ', 2);
        var owner = new PropertyOwner
        {
            OrgId = _world.OrgId,
            FirstName = parts[0],
            LastName = parts.Length > 1 ? parts[1] : string.Empty,
            Email = email,
            FiscalCode = fiscalCode,
            ExternalKey = key,
            Iban = iban,
            CreatedAt = DateTime.UtcNow,
        };
        if (complete)
        {
            owner.LegalName = name;
            owner.EntityKind = OwnerEntityKind.NaturalPerson;
            owner.TaxResidenceCountry = "IT";
            owner.FiscalCode = "RSSMRA85T10A562S";
            owner.BillingStreet = "Via Roma";
            owner.BillingCivic = "1";
            owner.BillingPostalCode = "00100";
            owner.BillingCity = "Roma";
            owner.BillingProvince = "RM";
            owner.BillingCountry = "IT";
            owner.Iban = "IT60X0542811101000000123456";
            owner.SettlementCurrency = "EUR";
        }

        _db.PropertyOwners.Add(owner);
        await _db.SaveChangesAsync();
        return owner.Id;
    }

    private async Task<Guid> AddPeriodAsync(Guid propertyId, Guid ownerId, decimal share, DateOnly start, DateOnly? end = null)
    {
        var period = new PropertyOwnership
        {
            OrgId = _world.OrgId,
            PropertyId = propertyId,
            PropertyOwnerId = ownerId,
            SharePercent = share,
            OwnershipStart = OwnerOnboardingService.StartInstant(start),
            OwnershipEnd = end is { } last ? OwnerOnboardingService.EndInstant(last) : null,
        };
        _db.PropertyOwnerships.Add(period);
        await _db.SaveChangesAsync();
        return period.Id;
    }
}
