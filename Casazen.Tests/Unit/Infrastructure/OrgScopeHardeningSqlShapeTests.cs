using System.Text.RegularExpressions;
using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Casazen.Infrastructure.Repositories;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AM-03b: the SQL of the three queries that decide who sees what, on the real provider (Npgsql, no server). InMemory proves the
/// result sets (<c>HostNotificationAudienceReachTests</c>, <c>OrgExportScopeTests</c>); this proves that Npgsql <b>translates</b>
/// them, in one statement each, with the reach of the person inside the statement and every value a parameter: the audience of a
/// notification reads the grants with an <c>EXISTS</c>, the export of the org narrows its property sections with the grants or
/// the creator, and the release of a responsibility is one read of the properties the person is in charge of.
/// </summary>
public class OrgScopeHardeningSqlShapeTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");
    private static readonly Guid PropertyId = Guid.Parse("0f5b6b72-2f29-4a3e-9a1e-4b9f0a0f2a55");
    private const string Collaborator = "auth0|collaboratore";
    private const string Holder = "auth0|titolare";

    /// <summary>The statement without the header of parameters that <c>ToQueryString</c> prints above it.</summary>
    private static string Body(string sql) => sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..];

    private static int Count(string text, string part) => text.Split(part, StringSplitOptions.None).Length - 1;

    // --- Who is told ---------------------------------------------------------------------------------

    [Fact]
    public void UsersToTell_IsOneStatement_TheGrantIsReadInsideIt_AndEveryValueIsAParameter()
    {
        using var db = NpgsqlTranslationProbe.NewContext();

        var sql = HostNotificationAudience.UsersToTell(db, OrgId, PropertyId, Collaborator, Holder).ToQueryString();
        var body = Body(sql);

        // The grant of the person on this property is a subquery of the statement, not a list read beforehand.
        Assert.Equal(1, Count(body, "\"PropertyMemberAccesses\""));
        Assert.Contains("EXISTS (", body, StringComparison.Ordinal);
        Assert.DoesNotContain(" IN (@", body, StringComparison.Ordinal);
        // The people and the property are parameters, never text inside the statement.
        Assert.DoesNotContain(Collaborator, body, StringComparison.Ordinal);
        Assert.DoesNotContain(Holder, body, StringComparison.Ordinal);
        Assert.DoesNotContain(PropertyId.ToString(), body, StringComparison.Ordinal);
        Assert.Contains(PropertyId.ToString(), sql[..sql.IndexOf("SELECT", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void UsersToTell_ComposedWithTheDevicesAndTheEmails_IsTranslatedByNpgsql()
    {
        using var db = NpgsqlTranslationProbe.NewContext();

        // As PushDeliveryJob (the devices of the audience) and BookingNotifier (their addresses) use it.
        var devices = db.DeviceRegistrations
            .AsNoTracking()
            .Join(
                HostNotificationAudience.UsersToTell(db, OrgId, PropertyId, null, Holder),
                device => device.UserId,
                user => user.Id,
                (device, user) => device)
            .Where(device => device.OrgId == OrgId)
            .OrderBy(device => device.CreatedAt)
            .ToQueryString();
        var emails = HostNotificationAudience.UsersToTell(db, OrgId, PropertyId, Collaborator, Holder).Select(u => u.Email).ToQueryString();

        Assert.Contains("\"DeviceRegistrations\"", devices, StringComparison.Ordinal);
        Assert.Contains("\"PropertyMemberAccesses\"", devices, StringComparison.Ordinal);
        Assert.Contains("\"Email\"", emails, StringComparison.Ordinal);
    }

    // --- The export of the org -----------------------------------------------------------------------

    [Fact]
    public void Export_TheHoldersScope_AddsNothingToThePlainQueries()
    {
        using var db = NpgsqlTranslationProbe.NewContext();

        var (years, taxpayers) = GdprService.OrgPropertySections(db, OrgId, new HostScope(OrgId));

        foreach (var sql in new[] { years.ToQueryString(), taxpayers.ToQueryString() })
        {
            Assert.DoesNotContain("PropertyMemberAccesses", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("EXISTS (", sql, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Export_ACollaboratorSoloAlcuni_NarrowsBothSectionsByItsGrants_InTheSameStatement()
    {
        using var db = NpgsqlTranslationProbe.NewContext();

        var (years, taxpayers) = GdprService.OrgPropertySections(db, OrgId, new HostScope(OrgId, GrantedToUserId: Collaborator));

        foreach (var sql in new[] { years.ToQueryString(), taxpayers.ToQueryString() })
        {
            var body = Body(sql);
            Assert.Equal(1, Count(body, "\"PropertyMemberAccesses\""));
            Assert.DoesNotContain(" IN (@", body, StringComparison.Ordinal);
            Assert.DoesNotContain(Collaborator, body, StringComparison.Ordinal);
            Assert.Contains(Collaborator, sql[..sql.IndexOf("SELECT", StringComparison.Ordinal)], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Export_AnAccountOfBeforeTheTeam_NarrowsBothSectionsByTheCreator_WithoutTheGrantTable()
    {
        using var db = NpgsqlTranslationProbe.NewContext();

        var (years, taxpayers) = GdprService.OrgPropertySections(db, OrgId, new HostScope(OrgId, OwnerId: Holder));

        foreach (var sql in new[] { years.ToQueryString(), taxpayers.ToQueryString() })
        {
            var body = Body(sql);
            Assert.Matches("\"OwnerId\" = @", body);
            Assert.DoesNotContain("PropertyMemberAccesses", body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Export_KeepsTheSoftDeletedProperties_InEveryScope()
    {
        using var db = NpgsqlTranslationProbe.NewContext();

        foreach (var scope in new[]
                 {
                     new HostScope(OrgId),
                     new HostScope(OrgId, GrantedToUserId: Collaborator),
                     new HostScope(OrgId, OwnerId: Holder),
                 })
        {
            var (years, taxpayers) = GdprService.OrgPropertySections(db, OrgId, scope);

            // The soft-delete filter is lifted in the sections of every scope (the properties of a closed year still name their
            // fiscal record): no predicate on the flag in either statement (the column is only in the select list).
            foreach (var sql in new[] { years.ToQueryString(), taxpayers.ToQueryString() })
            {
                Assert.DoesNotMatch(new Regex("NOT \\(\\w+\\.\"IsDeleted\"\\)"), sql);
                Assert.DoesNotMatch(new Regex("\"IsDeleted\" ="), sql);
            }
        }
    }

    // --- The generic save of a property ---------------------------------------------------------------

    [Fact]
    public async Task UpdateAsync_TheGenericSave_NeverWritesTheColumnOfThePersonInCharge()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);
        var property = new Property
        {
            Id = PropertyId,
            OrgId = OrgId,
            OwnerId = Holder,
            ResponsibleUserId = Collaborator,
            Name = "Casa",
            Description = "Nuova descrizione",
            Address = "Via Roma 1",
            City = "Roma",
        };

        // No server: the UPDATE is built by Npgsql and caught before it is sent (the empty answer makes the save fail afterwards).
        await Assert.ThrowsAnyAsync<DbUpdateException>(() => new PropertyRepository(db).UpdateAsync(property));

        var update = Assert.Single(statements, sql => sql.StartsWith("UPDATE \"Properties\"", StringComparison.Ordinal));
        Assert.Contains("\"Description\" =", update, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ResponsibleUserId\"", update, StringComparison.Ordinal);
        // The same two columns the earlier tasks keep out of the generic save.
        Assert.DoesNotContain("\"RentalMode\"", update, StringComparison.Ordinal);
        Assert.DoesNotContain("\"PhotoUrls\"", update, StringComparison.Ordinal);
    }

    // --- The release of a responsibility -------------------------------------------------------------

    [Fact]
    public async Task ReleaseAsync_IsTranslatedByNpgsql_WithAndWithoutThePropertiesToKeep()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);

        await NpgsqlTranslationProbe.AssertTranslatesAsync(
            () => PropertyResponsibility.ReleaseAsync(db, OrgId, Collaborator, [PropertyId], DateTime.UtcNow, CancellationToken.None));
        await NpgsqlTranslationProbe.AssertTranslatesAsync(
            () => PropertyResponsibility.ReleaseAsync(db, OrgId, Collaborator, [], DateTime.UtcNow, CancellationToken.None));

        // One read each, of the properties of the org the person is in charge of; the keep list is a parameter.
        Assert.Equal(2, statements.Count);
        Assert.All(statements, sql =>
        {
            Assert.Contains("\"ResponsibleUserId\" = @", sql, StringComparison.Ordinal);
            Assert.DoesNotContain(Collaborator, sql, StringComparison.Ordinal);
        });
        Assert.Contains("= ANY (@", statements[0], StringComparison.Ordinal);
        Assert.DoesNotContain("= ANY (", statements[1], StringComparison.Ordinal);
    }
}
