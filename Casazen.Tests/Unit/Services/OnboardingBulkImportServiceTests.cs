using System.IO.Compression;
using System.Security;
using System.Text;
using System.Text.Json;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Core.Exceptions;
using Casazen.Core.Regulatory;
using Casazen.Core.Services;
using Casazen.Infrastructure.Data;
using Casazen.Infrastructure.Data.Encryption;
using Casazen.Infrastructure.Services;
using Casazen.Tests.Unit.Logging;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// The import of the data that changes per property from a CSV or an Excel file (NF-105, E10/F02/US01/T02): the file rules, the check of every
/// row, the writing of only the valid ones in one unit of work each, the Excel reader that uses the framework only and defends itself, and the
/// two proofs of the brief: the lock code is stored encrypted and never logged or echoed, and a row that names a property of another org is
/// «not found» and changes nothing.
/// </summary>
public class OnboardingBulkImportServiceTests : IAsyncLifetime
{
    private const string CinGranted = "IT058091C27G5FFZDZ";
    private const string CinHidden = "IT015146C21H2JKLMN";
    private const string CinShared = "IT001272B1AAAA";
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly string _database = Guid.NewGuid().ToString();
    private readonly SaveCounter _saves = new();
    private readonly Casazen.Tests.Unit.FakeTimeProvider _clock = new(HostScopeScenario.Now);
    private readonly CapturingLogger<OnboardingBulkImportService> _log = new();
    private AppDbContext _db = null!;
    private HostScopeWorld _world = null!;

    public async Task InitializeAsync()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_database).AddInterceptors(_saves).Options);
        _world = await HostScopeScenario.SeedAsync(_db);
        await UpdateAsync(_world.Granted.Id, p => p.CinCode = CinGranted);
        await UpdateAsync(_world.Hidden.Id, p =>
        {
            p.CinCode = CinHidden;
            p.ComuneIstatCode = "015146";
            p.RegionCode = "LOM";
        });
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    // ---------------------------------------------------------------- the two proofs of the brief

    /// <summary>
    /// Proof (privacy and data): the lock code of a row is stored in the encrypted <c>PropertyAccessCode</c> (Data Protection purpose of the access
    /// codes), appears in no answer and in no log line, and an invalid one is not echoed either; the answer says only <c>lockCodeProvided</c>. A
    /// dry run stores nothing.
    /// </summary>
    [Fact]
    public async Task BulkImport_LockCode_NeverLoggedOrEchoed_Test()
    {
        const string Secret = "7391#Zeta";
        const string WrongSecret = "q!";
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"lock-code-{Guid.NewGuid()}").Options;
        var provider = new EphemeralDataProtectionProvider();
        var orgId = Guid.NewGuid();
        var good = Guid.NewGuid();
        var bad = Guid.NewGuid();
        await using (var seed = new AppDbContext(options, null, provider))
        {
            seed.Properties.AddRange(
                NewProperty(orgId, good, "Con codice"),
                NewProperty(orgId, bad, "Codice sbagliato"));
            await seed.SaveChangesAsync();
        }

        var csv = Csv("propertyId,lockCode", $"{good},{Secret}", $"{bad},{WrongSecret}");

        // A dry run judges and stores nothing.
        await using (var db = new AppDbContext(options, null, provider))
        {
            var dry = await ServiceOn(db).ImportAsync(new HostScope(orgId), "auth0|host", "file.csv", csv, dryRun: true, Ct);
            Assert.Equal((true, false), (dry.Rows[0].LockCodeProvided, dry.Rows[0].Applied));
            Assert.Equal(BulkImportRowCodes.LockCodeInvalid, dry.Rows[1].Status);
            Assert.Empty(await db.PropertyAccessCodes.AsNoTracking().ToListAsync());
        }

        BulkImportResult result;
        await using (var db = new AppDbContext(options, null, provider))
            result = await ServiceOn(db).ImportAsync(new HostScope(orgId), "auth0|host", "file.csv", csv, dryRun: false, Ct);

        // The answer: whether there was a code, never the code, whatever the serializer prints.
        Assert.Equal((BulkImportRowCodes.Ok, true, true), (result.Rows[0].Status, result.Rows[0].LockCodeProvided, result.Rows[0].Applied));
        Assert.Equal((BulkImportRowCodes.LockCodeInvalid, true, false), (result.Rows[1].Status, result.Rows[1].LockCodeProvided, result.Rows[1].Applied));
        var echoed = JsonSerializer.Serialize(result) + result;
        Assert.DoesNotContain(Secret, echoed, StringComparison.Ordinal);
        Assert.DoesNotContain(WrongSecret, echoed, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, _log.AllOutput, StringComparison.Ordinal);
        Assert.DoesNotContain(WrongSecret, _log.AllOutput, StringComparison.Ordinal);

        // Stored encrypted: the code goes to the column of the access code, which the EF value converter of the access-code purpose encrypts on the
        // way to the database (the PostgreSQL tests of the encrypted columns prove the stored ciphertext; the InMemory store keeps the values of
        // the model, so here the converter itself is proved): what it writes is not the code, and the context with the key reads the code back.
        // Only the valid row stored a code.
        await using (var withKey = new AppDbContext(options, null, provider))
        {
            var stored = Assert.Single(await withKey.PropertyAccessCodes.AsNoTracking().ToListAsync());
            Assert.Equal((good, Secret), (stored.PropertyId, stored.EncryptedCode));

            var converter = Assert.IsType<EncryptedStringConverter>(
                withKey.Model.FindEntityType(typeof(PropertyAccessCode))!.FindProperty(nameof(PropertyAccessCode.EncryptedCode))!.GetValueConverter());
            var written = Assert.IsType<string>(converter.ConvertToProvider(Secret));
            Assert.StartsWith(EncryptedColumns.ProtectedPayloadPrefix, written, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, written, StringComparison.Ordinal);
            Assert.Equal(Secret, converter.ConvertFromProvider(written));
        }

        // The log says what happened, in counts.
        Assert.Contains(_log.Entries, e => e.Message.Contains("2 rows, 1 valid, 1 written", StringComparison.Ordinal));
    }

    /// <summary>
    /// Proof (permission and tenant isolation): a row that names a property of another org, by id or by CIN, or one the caller does not reach, is
    /// <c>property_not_found</c>, the same answer as a property that does not exist; nothing of the other org's data is written, and the file's
    /// valid rows are still applied.
    /// </summary>
    [Fact]
    public async Task BulkImport_ForeignOrgProperty_NotFound_Test()
    {
        var foreign = _world.OtherOrgProperty;
        await UpdateAsync(foreign.Id, p => p.CinCode = "IT058091C27OTHERX");
        var nobody = Guid.NewGuid();
        var csv = Csv(
            "propertyId,cinCode,comuneIstatCode,alloggiatiRoomNumbers,lockCode",
            $"{foreign.Id},,058091,101,5555",
            $",IT058091C27OTHERX,058091,102,6666",
            $"{nobody},,058091,103,7777",
            $"{_world.Granted.Id},,058091,104,8888");

        var result = await Service().ImportAsync(_world.OrgWide, _world.OwnerUserId, "file.csv", csv, dryRun: false, Ct);

        // The foreign property (by id, by CIN) and the id nobody has all answer the same.
        Assert.All(result.Rows.Take(3), row => Assert.Equal((BulkImportRowCodes.PropertyNotFound, false, false), (row.Status, row.PropertyId.HasValue, row.Applied)));
        Assert.Equal(result.Rows[0].Errors.Select(e => e.Code), result.Rows[2].Errors.Select(e => e.Code));
        Assert.Equal(result.Rows[0].Errors.Select(e => e.Column), result.Rows[2].Errors.Select(e => e.Column));
        Assert.Equal((BulkImportRowCodes.Ok, true), (result.Rows[3].Status, result.Rows[3].Applied));

        // Nothing of the other org changed: its property, its onboarding data, its access code.
        var theirs = await _db.Properties.AsNoTracking().IgnoreQueryFilters().SingleAsync(p => p.Id == foreign.Id);
        Assert.Null(theirs.ComuneIstatCode);
        Assert.Empty(await _db.PropertyOnboardingData.AsNoTracking().Where(d => d.PropertyId == foreign.Id).ToListAsync());
        Assert.Empty(await _db.PropertyAccessCodes.AsNoTracking().Where(a => a.PropertyId == foreign.Id).ToListAsync());
        Assert.Equal(_world.Granted.Id, Assert.Single(await _db.PropertyAccessCodes.AsNoTracking().ToListAsync()).PropertyId);

        // The collaborator «Solo alcuni» reaches Granted only: Hidden is not found for it, by id or by CIN.
        var restricted = await Service().ImportAsync(
            _world.Restricted,
            _world.CollaboratorId,
            "file.csv",
            Csv("propertyId,cinCode,lockCode", $"{_world.Hidden.Id},,1111", $",{CinHidden},2222", $"{_world.Granted.Id},,3333"),
            dryRun: false,
            Ct);
        Assert.Equal(
            [BulkImportRowCodes.PropertyNotFound, BulkImportRowCodes.PropertyNotFound, BulkImportRowCodes.Ok],
            restricted.Rows.Select(r => r.Status));
        Assert.Empty(await _db.PropertyAccessCodes.AsNoTracking().Where(a => a.PropertyId == _world.Hidden.Id).ToListAsync());
    }

    // ---------------------------------------------------------------- the check of every row

    /// <summary>
    /// The dry run answers every row on its own with its status and all its errors by column, plus the columns the import does not know (once), and
    /// writes nothing: no save at all.
    /// </summary>
    [Fact]
    public async Task DryRun_ChecksEveryRowOnItsOwn_ReportsTheColumnsOnce_AndWritesNothing()
    {
        var terza = await AddPropertyAsync("Terza", CinShared);
        var quarta = await AddPropertyAsync("Quarta", CinShared);
        var quinta = await AddPropertyAsync("Quinta");
        var sesta = await AddPropertyAsync("Sesta");
        var unknown = Guid.NewGuid();
        var twentyOne = string.Join(';', Enumerable.Range(1, 21));
        var csv = Csv(
            "propertyId,cinCode,comuneIstatCode,regionCode,alloggiatiRoomNumbers,lockCode,note,Region",
            $"{_world.Granted.Id},,058091,LAZ,\"101;102\",4821,hello,x", //                       2 ok
            $",it015146 c21 h2jklmn,015146,LAZ,,,,", //                                            3 region does not match the comune
            $"{unknown},,000000,,,,,", //                                                          4 unknown property, unknown comune
            $",{CinGranted},,,,,,", //                                                             5 Granted again
            $",{CinShared},,,\"A;B;A\",,,", //                                                     6 two properties with that CIN, repeated room
            $"{_world.OtherOrgProperty.Id},,,,,,,", //                                             7 another org
            $"{terza.Id},,,,{twentyOne},12,,", //                                                  8 21 rooms, a lock code of two characters
            $"{quarta.Id},,999001,,\"101-A;B2\",,,", //                                            9 a comune that is no longer active
            $"{quinta.Id},,,LAZ,,,,", //                                                          10 a region and no comune anywhere
            ",,058091,,,,,", //                                                                    11 names no property
            $"{sesta.Id},,058091,,\"x y\",,,");
        var saves = _saves.Count;

        var result = await Service().ImportAsync(_world.OrgWide, _world.OwnerUserId, "file.csv", csv, dryRun: true, Ct);

        Assert.True(result.DryRun);
        Assert.Equal(["note", "Region"], result.UnknownColumns);
        Assert.Equal(Enumerable.Range(2, 11), result.Rows.Select(r => r.RowNumber));
        Assert.Equal(
            [
                BulkImportRowCodes.Ok,
                BulkImportRowCodes.RegionMismatch,
                BulkImportRowCodes.PropertyNotFound,
                BulkImportRowCodes.DuplicateRow,
                BulkImportRowCodes.PropertyAmbiguous,
                BulkImportRowCodes.PropertyNotFound,
                BulkImportRowCodes.RoomNumbersInvalid,
                BulkImportRowCodes.ComuneUnknown,
                BulkImportRowCodes.RegionMismatch,
                BulkImportRowCodes.PropertyNotFound,
                BulkImportRowCodes.RoomNumbersInvalid,
            ],
            result.Rows.Select(r => r.Status));
        Assert.Equal((1, 10, 0), (result.Summary.Valid, result.Summary.Invalid, result.Summary.Applied));
        Assert.Equal(11, result.Summary.Rows);

        // Every error with its column.
        Assert.Equal(
            [(BulkImportColumns.PropertyId, BulkImportRowCodes.PropertyNotFound), (BulkImportColumns.ComuneIstatCode, BulkImportRowCodes.ComuneUnknown)],
            result.Rows[2].Errors.Select(e => (e.Column, e.Code)));
        Assert.Equal([(BulkImportColumns.CinCode, BulkImportRowCodes.DuplicateRow)], result.Rows[3].Errors.Select(e => (e.Column, e.Code)));
        Assert.Equal(
            [(BulkImportColumns.CinCode, BulkImportRowCodes.PropertyAmbiguous), (BulkImportColumns.AlloggiatiRoomNumbers, BulkImportRowCodes.RoomNumbersInvalid)],
            result.Rows[4].Errors.Select(e => (e.Column, e.Code)));
        Assert.Equal(
            [(BulkImportColumns.AlloggiatiRoomNumbers, BulkImportRowCodes.RoomNumbersInvalid), (BulkImportColumns.LockCode, BulkImportRowCodes.LockCodeInvalid)],
            result.Rows[6].Errors.Select(e => (e.Column, e.Code)));
        Assert.Equal([(BulkImportColumns.PropertyId, BulkImportRowCodes.PropertyNotFound)], result.Rows[9].Errors.Select(e => (e.Column, e.Code)));
        Assert.Equal(_world.Granted.Id, result.Rows[0].PropertyId);
        Assert.Equal(_world.Hidden.Id, result.Rows[1].PropertyId);
        Assert.Equal(CinHidden, result.Rows[1].PropertyRef);
        Assert.Null(result.Rows[4].PropertyId);
        Assert.Equal([true, false, false, false, false, false, true, false, false, false, false], result.Rows.Select(r => r.LockCodeProvided));
        Assert.Equal(saves, _saves.Count);
        Assert.Empty(await _db.PropertyAccessCodes.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.PropertyOnboardingData.AsNoTracking().ToListAsync());
        Assert.Null((await _db.Properties.AsNoTracking().SingleAsync(p => p.Id == _world.Granted.Id)).ComuneIstatCode);
    }

    /// <summary>
    /// The import writes ONLY the valid rows, each in one unit of work (one save for each), whatever the invalid ones do: the comune with the region
    /// that follows it (derived when the row has none), the room numbers as a JSON array, the lock code. A row that fails never stops another, and a
    /// second import of the same file changes nothing.
    /// </summary>
    [Fact]
    public async Task Import_WritesOnlyTheValidRows_EachInOneUnitOfWork()
    {
        var terza = await AddPropertyAsync("Terza");
        var quarta = await AddPropertyAsync("Quarta");
        var quinta = await AddPropertyAsync("Quinta");
        var csv = Csv(
            "propertyId,comuneIstatCode,regionCode,alloggiatiRoomNumbers,lockCode",
            $"{_world.Granted.Id},058091,LAZ,\"101;102\",4821", //  ok: comune, region, rooms, lock
            $"{_world.Hidden.Id},,,\"A1; A2\",", //                  ok: rooms only; its comune is untouched
            $"{terza.Id},015146,,,9999", //                         ok: the region follows the comune
            $"{quarta.Id},058091,LOM,101,", //                      region of another comune: not written
            $"{Guid.NewGuid()},058091,,,", //                       no such property
            $"{quinta.Id},,LAZ,102,"); //                           a region with no comune anywhere: not written
        var before = _saves.Count;

        var result = await Service().ImportAsync(_world.OrgWide, _world.OwnerUserId, "file.csv", csv, dryRun: false, Ct);

        Assert.False(result.DryRun);
        Assert.Equal((6, 3, 3, 3), (result.Summary.Rows, result.Summary.Valid, result.Summary.Invalid, result.Summary.Applied));
        Assert.Equal([true, true, true, false, false, false], result.Rows.Select(r => r.Applied));
        Assert.Equal(3, _saves.Count - before);

        var granted = await _db.Properties.AsNoTracking().SingleAsync(p => p.Id == _world.Granted.Id);
        Assert.Equal(("058091", "LAZ", "Ostuni"), (granted.ComuneIstatCode, granted.RegionCode, granted.City));
        var hidden = await _db.Properties.AsNoTracking().SingleAsync(p => p.Id == _world.Hidden.Id);
        Assert.Equal(("015146", "LOM"), (hidden.ComuneIstatCode, hidden.RegionCode));
        var third = await _db.Properties.AsNoTracking().SingleAsync(p => p.Id == terza.Id);
        Assert.Equal(("015146", "LOM"), (third.ComuneIstatCode, third.RegionCode));
        Assert.Null((await _db.Properties.AsNoTracking().SingleAsync(p => p.Id == quarta.Id)).ComuneIstatCode);
        Assert.Null((await _db.Properties.AsNoTracking().SingleAsync(p => p.Id == quinta.Id)).RegionCode);

        var data = (await _db.PropertyOnboardingData.AsNoTracking().ToListAsync()).ToDictionary(d => d.PropertyId);
        Assert.Equal("[\"101\",\"102\"]", data[_world.Granted.Id].AlloggiatiRoomNumbersJson);
        Assert.Equal("[\"A1\",\"A2\"]", data[_world.Hidden.Id].AlloggiatiRoomNumbersJson);
        Assert.Equal((_world.OrgId, _world.OwnerUserId), (data[_world.Granted.Id].OrgId, data[_world.Granted.Id].UpdatedByUserId));
        Assert.Equal(2, data.Count);
        var codes = (await _db.PropertyAccessCodes.AsNoTracking().ToListAsync()).ToDictionary(c => c.PropertyId);
        Assert.Equal(["4821", "9999"], new[] { codes[_world.Granted.Id].EncryptedCode, codes[terza.Id].EncryptedCode });
        Assert.Equal(2, codes.Count);

        // The same file again: the rows are valid again and the data is what it was; nothing is added twice, nothing is written (no save) and no
        // row is «applied», because none of them changed anything.
        var savedBefore = _saves.Count;
        var again = await Service().ImportAsync(_world.OrgWide, _world.OwnerUserId, "file.csv", csv, dryRun: false, Ct);
        Assert.Equal((3, 0), (again.Summary.Valid, again.Summary.Applied));
        Assert.All(again.Rows, row => Assert.False(row.Applied));
        Assert.Equal(savedBefore, _saves.Count);
        Assert.Equal(2, await _db.PropertyOnboardingData.CountAsync());
        Assert.Equal(2, await _db.PropertyAccessCodes.CountAsync());
        Assert.Equal("[\"101\",\"102\"]", (await _db.PropertyOnboardingData.AsNoTracking().SingleAsync(d => d.PropertyId == _world.Granted.Id)).AlloggiatiRoomNumbersJson);

        // A changed value replaces the stored one.
        await Service().ImportAsync(
            _world.OrgWide, _world.OwnerUserId, "file.csv", Csv("propertyId,alloggiatiRoomNumbers,lockCode", $"{_world.Granted.Id},201,0000"), dryRun: false, Ct);
        Assert.Equal("[\"201\"]", (await _db.PropertyOnboardingData.AsNoTracking().SingleAsync(d => d.PropertyId == _world.Granted.Id)).AlloggiatiRoomNumbersJson);
        Assert.Equal("0000", (await _db.PropertyAccessCodes.AsNoTracking().SingleAsync(a => a.PropertyId == _world.Granted.Id)).EncryptedCode);
    }

    /// <summary>
    /// PostgreSQL gives a <c>jsonb</c> array back normalized, with a space after each comma (<c>["101", "102"]</c>), which is not the text the import
    /// serializes (<c>["101","102"]</c>). The room numbers are compared as values, so a second import of the same file writes nothing: no save, and
    /// neither the date nor the author of the row moves (which would credit the importer with a change nobody made). Another list, or a stored value
    /// that is not an array of strings, is a change: it is written, by the importer, now.
    /// </summary>
    [Fact]
    public async Task Import_RoomNumbersStoredAsJsonbGivesThemBack_AreNotRewrittenByTheSameFile()
    {
        var earlier = new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc);
        _db.PropertyOnboardingData.AddRange(
            new PropertyOnboardingData
            {
                OrgId = _world.OrgId,
                PropertyId = _world.Granted.Id,
                AlloggiatiRoomNumbersJson = "[\"101\", \"102\"]",
                UpdatedAt = earlier,
                UpdatedByUserId = "auth0|earlier",
            },
            new PropertyOnboardingData
            {
                OrgId = _world.OrgId,
                PropertyId = _world.Hidden.Id,
                AlloggiatiRoomNumbersJson = "this is not json",
                UpdatedAt = earlier,
                UpdatedByUserId = "auth0|earlier",
            });
        await _db.SaveChangesAsync();
        var saves = _saves.Count;

        // The same numbers as stored: nothing to write.
        var same = await Service().ImportAsync(
            _world.OrgWide, _world.OwnerUserId, "file.csv", Csv("propertyId,alloggiatiRoomNumbers", $"{_world.Granted.Id},\"101;102\""), dryRun: false, Ct);
        Assert.Equal((BulkImportRowCodes.Ok, false), (Assert.Single(same.Rows).Status, same.Rows[0].Applied));
        Assert.Equal((1, 0), (same.Summary.Valid, same.Summary.Applied));
        Assert.Equal(saves, _saves.Count);
        var kept = await _db.PropertyOnboardingData.AsNoTracking().SingleAsync(d => d.PropertyId == _world.Granted.Id);
        Assert.Equal(("[\"101\", \"102\"]", earlier, "auth0|earlier"), (kept.AlloggiatiRoomNumbersJson, kept.UpdatedAt, kept.UpdatedByUserId));

        // Other numbers, and a stored value that is not an array: both are written, with the importer and the time of the import.
        var changed = await Service().ImportAsync(
            _world.OrgWide,
            _world.OwnerUserId,
            "file.csv",
            Csv("propertyId,alloggiatiRoomNumbers", $"{_world.Granted.Id},\"101;103\"", $"{_world.Hidden.Id},A1"),
            dryRun: false,
            Ct);
        Assert.Equal([true, true], changed.Rows.Select(r => r.Applied));
        Assert.Equal(saves + 2, _saves.Count);
        var rewritten = await _db.PropertyOnboardingData.AsNoTracking().ToDictionaryAsync(d => d.PropertyId);
        Assert.Equal("[\"101\",\"103\"]", rewritten[_world.Granted.Id].AlloggiatiRoomNumbersJson);
        Assert.Equal("[\"A1\"]", rewritten[_world.Hidden.Id].AlloggiatiRoomNumbersJson);
        Assert.All(
            rewritten.Values,
            data => Assert.Equal((HostScopeScenario.Now.UtcDateTime, _world.OwnerUserId), (data.UpdatedAt, data.UpdatedByUserId)));
    }

    /// <summary>The header goes by Italian or English names in any case, with accents or separators; the delimiter is found on the header; a quoted cell may hold the separator.</summary>
    [Fact]
    public async Task Columns_AreFoundByTheirItalianOrEnglishNames_AndTheDelimiterByTheHeader()
    {
        var csv = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(
                "ID Immobile;Codice ISTAT;REGIONE;Numeri camera Alloggiati;Codice serratura;Città\r\n"
                + $"{_world.Granted.Id};058091;lazio;\"101;102\";4821;Roma\r\n"
                + $"{_world.Hidden.Id};;;;;\r\n"))
            .ToArray();

        var result = await Service().ImportAsync(_world.OrgWide, _world.OwnerUserId, "file.CSV", csv, dryRun: true, Ct);

        Assert.Equal(["Città"], result.UnknownColumns);
        Assert.Equal(BulkImportRowCodes.RegionMismatch, result.Rows[0].Status); // «lazio» is not a region code (LAZ is)
        Assert.Equal(BulkImportRowCodes.Ok, result.Rows[1].Status);
        Assert.Equal(_world.Hidden.Id, result.Rows[1].PropertyId);

        // Written for real, a row with only its key is valid and changes nothing: it is not «applied» and nothing is saved.
        var saves = _saves.Count;
        var written = await Service().ImportAsync(_world.OrgWide, _world.OwnerUserId, "file.CSV", csv, dryRun: false, Ct);
        Assert.Equal((BulkImportRowCodes.Ok, false), (written.Rows[1].Status, written.Rows[1].Applied));
        Assert.Equal((1, 0), (written.Summary.Valid, written.Summary.Applied));
        Assert.Equal(saves, _saves.Count);

        var english = Csv("Property ID,Comune ISTAT Code,Region Code,Room Numbers,Lock Code", $"{_world.Granted.Id},058091,laz,\"A1;A2\",4821");
        var ok = await Service().ImportAsync(_world.OrgWide, _world.OwnerUserId, "x.csv", english, dryRun: true, Ct);
        Assert.Equal(BulkImportRowCodes.Ok, Assert.Single(ok.Rows).Status);
        Assert.Empty(ok.UnknownColumns);

        // Text that is not UTF-8 (the CSV of an Italian spreadsheet) is read as Windows-1252, and a code is never mangled.
        var latin1 = Encoding.Latin1.GetBytes($"propertyId,lockCode,Località\r\n{_world.Granted.Id},4821,Città di Perù\r\n");
        var fromLatin1 = await Service().ImportAsync(_world.OrgWide, _world.OwnerUserId, "x.csv", latin1, dryRun: true, Ct);
        Assert.Equal(BulkImportRowCodes.Ok, Assert.Single(fromLatin1.Rows).Status);
        Assert.Equal(["Località"], fromLatin1.UnknownColumns);
    }

    // ---------------------------------------------------------------- the file rules

    /// <summary>
    /// The rules of the file: a <c>.csv</c> or an <c>.xlsx</c> of at most 2 MB and 1,000 rows. Another type, or content that is not what the name says,
    /// is 415; a larger file 413; more rows 422; and the problems of the table itself are 422 with their own codes.
    /// </summary>
    [Fact]
    public async Task FileRules_TypeSizeRowsAndHeader()
    {
        var service = Service();
        var valid = Csv("propertyId,lockCode", $"{_world.Granted.Id},4821");

        async Task<DomainException> RefusedAsync(string name, byte[] content) =>
            await Assert.ThrowsAnyAsync<DomainException>(() => service.ImportAsync(_world.OrgWide, _world.OwnerUserId, name, content, true, Ct));

        // The type.
        foreach (var name in new[] { "file.xls", "file.xlsm", "file.pdf", "file.txt", "file", string.Empty, "file.csv.exe", "x.xlsb" })
        {
            var refused = Assert.IsType<DomainUnsupportedMediaTypeException>(await RefusedAsync(name, valid));
            Assert.Equal(BulkImportCodes.FileTypeUnsupported, refused.Code);
        }

        Assert.IsType<DomainUnsupportedMediaTypeException>(await RefusedAsync("renamed.csv", Workbook(Table(Row("propertyId", "lockCode")))));
        Assert.IsType<DomainUnsupportedMediaTypeException>(await RefusedAsync("binary.csv", [.. valid, 0, 1, 2]));
        Assert.IsType<DomainUnsupportedMediaTypeException>(await RefusedAsync("renamed.xlsx", valid));

        // The size (2 MB) and the rows (1,000).
        var large = Assert.IsType<DomainPayloadTooLargeException>(await RefusedAsync("big.csv", new byte[BulkImportLimits.MaxFileBytes + 1]));
        Assert.Equal(BulkImportCodes.FileTooLarge, large.Code);
        string Rows(int count) => string.Join("\r\n", Enumerable.Range(0, count).Select(_ => $"{Guid.NewGuid()},4821"));
        var full = Csv("propertyId,lockCode", Rows(BulkImportLimits.MaxRows));
        Assert.Equal(BulkImportLimits.MaxRows, (await service.ImportAsync(_world.OrgWide, _world.OwnerUserId, "full.csv", full, true, Ct)).Rows.Count);
        var tooMany = Assert.IsType<DomainRuleException>(
            await RefusedAsync("many.csv", Csv("propertyId,lockCode", Rows(BulkImportLimits.MaxRows + 1))));
        Assert.Equal(BulkImportCodes.TooManyRows, tooMany.Code);

        // The table.
        Assert.Equal(BulkImportCodes.FileEmpty, (await RefusedAsync("empty.csv", [])).Code);
        Assert.Equal(BulkImportCodes.FileEmpty, (await RefusedAsync("blank.csv", Csv(string.Empty, "  ,  "))).Code);
        Assert.Equal(BulkImportCodes.FileEmpty, (await RefusedAsync("header.csv", Csv("propertyId,lockCode"))).Code);
        Assert.Equal(BulkImportCodes.ColumnsUnrecognized, (await RefusedAsync("nokey.csv", Csv("lockCode,note", "4821,x"))).Code);
        Assert.Equal(BulkImportCodes.ColumnsUnrecognized, (await RefusedAsync("nodata.csv", Csv("propertyId,note", $"{_world.Granted.Id},x"))).Code);
        Assert.Equal(BulkImportCodes.FileUnreadable, (await RefusedAsync("quote.csv", Csv("propertyId,lockCode", $"\"{_world.Granted.Id},4821"))).Code);
        Assert.Empty(await _db.PropertyAccessCodes.AsNoTracking().ToListAsync());
    }

    /// <summary>The list of comuni is the one the property wizard uses: when it is not imported nothing can be judged on a comune, and the whole call says so.</summary>
    [Fact]
    public async Task ComuniListNotImported_IsAnErrorOfTheCall_NotOfTheRows()
    {
        var comuni = new Mock<IComuneDirectory>();
        comuni.Setup(c => c.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var service = Service(comuni.Object);

        var refused = await Assert.ThrowsAsync<DomainRuleException>(
            () => service.ImportAsync(_world.OrgWide, _world.OwnerUserId, "x.csv", Csv("propertyId,comuneIstatCode", $"{_world.Granted.Id},058091"), true, Ct));
        Assert.Equal(ComuneErrorCodes.DatasetUnavailable, refused.Code);

        // A file with no comune does not need it.
        var fine = await service.ImportAsync(_world.OrgWide, _world.OwnerUserId, "x.csv", Csv("propertyId,lockCode", $"{_world.Granted.Id},4821"), true, Ct);
        Assert.Equal(BulkImportRowCodes.Ok, Assert.Single(fine.Rows).Status);
    }

    // ---------------------------------------------------------------- Excel

    /// <summary>
    /// The Excel reader takes the FIRST sheet as the workbook says (not the one called «sheet1»), reads shared strings (rich text without the phonetic
    /// hints), inline strings and numbers, reads the cached value of a formula and never evaluates it, and leaves boolean and error cells empty. The
    /// import works on the result as on a CSV.
    /// </summary>
    [Fact]
    public async Task Xlsx_ReadsTheFirstSheetOfTheWorkbook_AndNeverEvaluatesAFormula()
    {
        var first = Table(
            Row("propertyId", "comuneIstatCode", "alloggiatiRoomNumbers", "lockCode", "regionCode"),
            Row(_world.Granted.Id.ToString(), "058091", "101;102", "=4821", "i:LAZ"),
            Row(_world.Hidden.Id.ToString(), null, "#201", "=", null));
        var second = Table(Row("not", "this", "sheet"), Row("x", "y", "z"));
        var content = Workbook(first, second, firstSheetIsTheSecondFile: true);

        var read = OnboardingXlsxReader.Read(content);

        // Header is row 1; a formula yields its cached value, or nothing; a number is its stored text.
        Assert.Equal([1, 2, 3], read.Select(r => r.Number));
        Assert.Equal(["propertyId", "comuneIstatCode", "alloggiatiRoomNumbers", "lockCode", "regionCode"], read[0].Cells);
        Assert.Equal([_world.Granted.Id.ToString(), "058091", "101;102", "4821", "LAZ"], read[1].Cells);
        Assert.Equal([_world.Hidden.Id.ToString(), string.Empty, "201", string.Empty], read[2].Cells.Take(4));

        var result = await Service().ImportAsync(_world.OrgWide, _world.OwnerUserId, "file.xlsx", content, dryRun: false, Ct);
        Assert.Equal((2, 2), (result.Summary.Valid, result.Summary.Applied));
        Assert.Equal("4821", (await _db.PropertyAccessCodes.AsNoTracking().SingleAsync(a => a.PropertyId == _world.Granted.Id)).EncryptedCode);
        Assert.Equal("[\"201\"]", (await _db.PropertyOnboardingData.AsNoTracking().SingleAsync(d => d.PropertyId == _world.Hidden.Id)).AlloggiatiRoomNumbersJson);
    }

    [Fact]
    public void Xlsx_RichTextBooleansErrorsEmptyInlineStringsAndGaps_AreReadWithoutLooping()
    {
        const string Sheet =
            "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>"
            + "<row r=\"3\"><c r=\"A3\" t=\"s\"><v>0</v></c><c r=\"C3\" t=\"inlineStr\"><is/></c><c r=\"D3\" t=\"b\"><v>1</v></c><c r=\"E3\" t=\"e\"><v>#N/A</v></c>"
            + "<c r=\"F3\" t=\"inlineStr\"><is><r><t>ab</t></r><r><t>cd</t></r><rPh><t>PHONETIC</t></rPh></is></c><c r=\"G3\"><f>HYPERLINK(\"http://example.invalid\")</f></c></row>"
            + "<row r=\"5\"/><row r=\"6\"><c r=\"B6\"><v> 42 </v></c></row></sheetData></worksheet>";
        const string Shared =
            "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><r><t>Ro</t></r><r><t>ma</t></r><rPh><t>IGNORED</t></rPh></si></sst>";

        var rows = OnboardingXlsxReader.Read(Workbook((Sheet, Shared)));

        Assert.Equal([3, 6], rows.Select(r => r.Number));
        Assert.Equal(["Roma", string.Empty, string.Empty, string.Empty, string.Empty, "abcd", string.Empty], rows[0].Cells);
        Assert.Equal([string.Empty, "42"], rows[1].Cells);
    }

    [Fact]
    public void Xlsx_ColumnIndex_IsTheLetterOfTheReference()
    {
        Assert.Equal([0, 1, 25, 26, 27, 701], new[] { "A1", "B2", "Z9", "AA10", "AB1", "ZZ3" }.Select(r => OnboardingXlsxReader.ColumnIndex(r)!.Value));
        Assert.Null(OnboardingXlsxReader.ColumnIndex(null));
        Assert.Null(OnboardingXlsxReader.ColumnIndex("12"));
        Assert.Null(OnboardingXlsxReader.ColumnIndex("ABCD1"));
    }

    /// <summary>
    /// A file that comes from outside cannot hurt the server: an archive that unzips to more than 20 MB is refused (413), one with more than 200
    /// entries is not read (422), a DTD or an external entity is never processed (422), and an archive with no workbook, or one that is damaged, is
    /// unreadable (422).
    /// </summary>
    [Fact]
    public async Task Xlsx_ZipBombsDtdsAndDamagedArchives_AreRefused()
    {
        var ok = Table(Row("propertyId", "lockCode"), Row(_world.Granted.Id.ToString(), "4821"));

        // Unzips to 25 MB: a few kilobytes of file.
        var bomb = Workbook(ok, extra: zip => AddEntry(zip, "xl/media/bomb.bin", new string('0', 25 * 1024 * 1024)));
        Assert.True(bomb.Length < BulkImportLimits.MaxFileBytes);
        var big = Assert.Throws<DomainPayloadTooLargeException>(() => OnboardingXlsxReader.Read(bomb));
        Assert.Equal(BulkImportCodes.FileTooLarge, big.Code);

        // The same, through the whole import, as the person would meet it.
        var viaImport = await Assert.ThrowsAsync<DomainPayloadTooLargeException>(
            () => Service().ImportAsync(_world.OrgWide, _world.OwnerUserId, "bomb.xlsx", bomb, true, Ct));
        Assert.Equal(BulkImportCodes.FileTooLarge, viaImport.Code);

        // More than 200 entries.
        var crowded = Workbook(ok, extra: zip =>
        {
            for (var i = 0; i < BulkImportLimits.MaxZipEntries + 1; i++)
                AddEntry(zip, $"xl/extra/{i}.xml", "<x/>");
        });
        Assert.Equal(BulkImportCodes.FileUnreadable, Assert.Throws<DomainRuleException>(() => OnboardingXlsxReader.Read(crowded)).Code);

        // A DOCTYPE is refused outright: an external entity can never be reached, whatever it points at.
        const string Xxe =
            "<?xml version=\"1.0\"?><!DOCTYPE worksheet [<!ENTITY xxe SYSTEM \"file:///etc/passwd\">]>"
            + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"1\"><c r=\"A1\" t=\"inlineStr\"><is><t>&xxe;</t></is></c></row></sheetData></worksheet>";
        Assert.Equal(BulkImportCodes.FileUnreadable, Assert.Throws<DomainRuleException>(() => OnboardingXlsxReader.Read(Workbook((Xxe, null)))).Code);

        // No workbook, a workbook with no sheet, a relationship that points nowhere, a part that is not XML, an archive cut short.
        var noWorkbook = Zip(zip => AddEntry(zip, "xl/worksheets/sheet1.xml", "<worksheet/>"));
        Assert.Equal(BulkImportCodes.FileUnreadable, Assert.Throws<DomainRuleException>(() => OnboardingXlsxReader.Read(noWorkbook)).Code);
        var noSheet = Zip(zip =>
        {
            AddEntry(zip, "xl/workbook.xml", "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheets/></workbook>");
            AddEntry(zip, "xl/_rels/workbook.xml.rels", "<Relationships/>");
        });
        Assert.Equal(BulkImportCodes.FileUnreadable, Assert.Throws<DomainRuleException>(() => OnboardingXlsxReader.Read(noSheet)).Code);
        var notXml = Workbook(("this is not xml", null));
        Assert.Equal(BulkImportCodes.FileUnreadable, Assert.Throws<DomainRuleException>(() => OnboardingXlsxReader.Read(notXml)).Code);
        var cut = Workbook(ok)[..60];
        Assert.Equal(BulkImportCodes.FileUnreadable, Assert.Throws<DomainRuleException>(() => OnboardingXlsxReader.Read(cut)).Code);

        // More than 1,000 rows of data.
        var rows = new[] { Row("propertyId", "lockCode") }
            .Concat(Enumerable.Range(0, BulkImportLimits.MaxRows + 1).Select(_ => Row(Guid.NewGuid().ToString(), "4821")))
            .ToArray();
        var tooMany = Workbook(Table(rows));
        Assert.Equal(BulkImportCodes.TooManyRows, Assert.Throws<DomainRuleException>(() => OnboardingXlsxReader.Read(tooMany)).Code);
    }

    // ---------------------------------------------------------------- helpers

    private OnboardingBulkImportService Service(IComuneDirectory? comuni = null) => ServiceOn(_db, comuni);

    private OnboardingBulkImportService ServiceOn(AppDbContext db, IComuneDirectory? comuni = null) =>
        new(db, comuni ?? Comuni(), _clock, _log);

    /// <summary>The official list, for the comuni the tests use: Roma (Lazio), Milano (Lombardia), and one that is no longer active.</summary>
    private static IComuneDirectory Comuni()
    {
        var known = new Dictionary<string, Comune>
        {
            ["058091"] = new() { IstatCode = "058091", Name = "Roma", RegionIstatCode = "12", IsActive = true },
            ["015146"] = new() { IstatCode = "015146", Name = "Milano", RegionIstatCode = "03", IsActive = true },
            ["999001"] = new() { IstatCode = "999001", Name = "Soppresso", RegionIstatCode = "03", IsActive = false },
        };
        var directory = new Mock<IComuneDirectory>();
        directory.Setup(c => c.IsAvailableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        directory
            .Setup(c => c.GetByIstatCodesAsync(It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IEnumerable<string> codes, CancellationToken _) =>
                (IReadOnlyDictionary<string, Comune>)known.Where(pair => codes.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value));
        return directory.Object;
    }

    private async Task<Property> AddPropertyAsync(string name, string? cin = null)
    {
        var property = HostScopeScenario.NewProperty(_world.OrgId, _world.OwnerUserId, name);
        property.CinCode = cin;
        _db.Properties.Add(property);
        await _db.SaveChangesAsync();
        return property;
    }

    private async Task UpdateAsync(Guid propertyId, Action<Property> change)
    {
        var property = await _db.Properties.IgnoreQueryFilters().SingleAsync(p => p.Id == propertyId);
        change(property);
        await _db.SaveChangesAsync();
    }

    private static Property NewProperty(Guid orgId, Guid id, string name)
    {
        var property = HostScopeScenario.NewProperty(orgId, "auth0|host", name);
        property.Id = id;
        return property;
    }

    private static byte[] Csv(params string[] lines) => Encoding.UTF8.GetBytes(string.Join("\r\n", lines));

    /// <summary>
    /// A sheet and its shared strings from rows of text: a plain text is a shared string, <c>i:text</c> an inline string, <c>#123</c> a number,
    /// <c>=123</c> a formula whose cached value is 123 (a bare <c>=</c> has none) and <c>null</c> no cell.
    /// </summary>
    private static (string Sheet, string Shared) Table(params string?[][] rows)
    {
        var shared = new List<string>();
        var sheet = new StringBuilder("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>");
        for (var r = 0; r < rows.Length; r++)
        {
            sheet.Append($"<row r=\"{r + 1}\">");
            for (var c = 0; c < rows[r].Length; c++)
            {
                var value = rows[r][c];
                var reference = $"{(char)('A' + c)}{r + 1}";
                if (value is null)
                    continue;

                if (value.StartsWith('#'))
                {
                    sheet.Append($"<c r=\"{reference}\"><v>{value[1..]}</v></c>");
                }
                else if (value.StartsWith('='))
                {
                    var cached = value.Length > 1 ? $"<v>{value[1..]}</v>" : string.Empty;
                    sheet.Append($"<c r=\"{reference}\"><f>SUM(1,2)</f>{cached}</c>");
                }
                else if (value.StartsWith("i:", StringComparison.Ordinal))
                {
                    sheet.Append($"<c r=\"{reference}\" t=\"inlineStr\"><is><t>{SecurityElement.Escape(value[2..])}</t></is></c>");
                }
                else
                {
                    shared.Add(value);
                    sheet.Append($"<c r=\"{reference}\" t=\"s\"><v>{shared.Count - 1}</v></c>");
                }
            }

            sheet.Append("</row>");
        }

        sheet.Append("</sheetData></worksheet>");
        var strings = string.Concat(shared.Select(s => $"<si><t>{SecurityElement.Escape(s)}</t></si>"));
        return (sheet.ToString(), $"<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">{strings}</sst>");
    }

    /// <summary>One row of <see cref="Table"/>.</summary>
    private static string?[] Row(params string?[] cells) => cells;

    /// <summary>A workbook of one sheet (or of two, the first listed by the workbook being the second file) with the parts a spreadsheet writes.</summary>
    private static byte[] Workbook(
        (string Sheet, string? Shared) first,
        (string Sheet, string? Shared)? second = null,
        bool firstSheetIsTheSecondFile = false,
        Action<ZipArchive>? extra = null)
    {
        const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        var firstFile = firstSheetIsTheSecondFile ? "worksheets/sheet2.xml" : "worksheets/sheet1.xml";
        var secondFile = firstSheetIsTheSecondFile ? "worksheets/sheet1.xml" : "worksheets/sheet2.xml";
        return Zip(zip =>
        {
            AddEntry(zip, "[Content_Types].xml", "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>");
            AddEntry(
                zip,
                "xl/workbook.xml",
                $"<workbook xmlns=\"{Main}\" xmlns:r=\"{Relationships}\"><sheets><sheet name=\"Dati\" sheetId=\"1\" r:id=\"rId7\"/>"
                + (second is null ? string.Empty : "<sheet name=\"Altro\" sheetId=\"2\" r:id=\"rId8\"/>")
                + "</sheets></workbook>");
            AddEntry(
                zip,
                "xl/_rels/workbook.xml.rels",
                $"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + $"<Relationship Id=\"rId7\" Type=\"{Relationships}/worksheet\" Target=\"{firstFile}\"/>"
                + (second is null ? string.Empty : $"<Relationship Id=\"rId8\" Type=\"{Relationships}/worksheet\" Target=\"{secondFile}\"/>")
                + "</Relationships>");
            AddEntry(zip, "xl/" + firstFile, first.Sheet);
            if (second is { } other)
                AddEntry(zip, "xl/" + secondFile, other.Sheet);
            if (first.Shared is not null)
                AddEntry(zip, "xl/sharedStrings.xml", first.Shared);
            extra?.Invoke(zip);
        });
    }

    private static byte[] Zip(Action<ZipArchive> fill)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            fill(zip);

        return stream.ToArray();
    }

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    /// <summary>Counts the saves of the unit of work.</summary>
    private sealed class SaveCounter : SaveChangesInterceptor
    {
        public int Count { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Count++;
            return base.SavingChanges(eventData, result);
        }
    }
}
