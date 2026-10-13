using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.OrgTeam;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Casazen.Tests.Unit.Services;

/// <summary>
/// AM-02b: <c>IActivityLog.Record</c> only stages the line in the unit of work of the caller, so the line is written by the
/// save of the change it records or not at all. The transaction itself (a rollback takes the change and the line away together)
/// is proved on PostgreSQL by <c>OrgActivityPostgresTests</c>; here the staging, the instant, the catalog and the flag.
/// </summary>
public class ActivityLogTests
{
    private readonly OrgInvitationTestKit _kit = new();

    private static OrgActivity Event(Guid orgId, OrgActivityType type = OrgActivityType.MemberDeactivated) =>
        OrgActivity.Of(orgId, type, "auth0|owner", "auth0|anna", (OrgActivityDetailKeys.Role, "Collaborator"));

    [Fact]
    public async Task Record_StagesTheLine_AndSavesNothing()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await using var db = _kit.NewDb();

        _kit.Activity(db).Record(Event(org.Id));

        var staged = Assert.Single(db.ChangeTracker.Entries<OrgActivityEntry>());
        Assert.Equal(EntityState.Added, staged.State);
        Assert.Empty(await _kit.ReadActivityAsync(org.Id));
    }

    [Fact]
    public async Task Record_TheLineIsWrittenByTheSaveOfTheChangeItRecords()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await using (var db = _kit.NewDb())
        {
            var stored = await db.Orgs.SingleAsync(o => o.Id == org.Id);
            stored.Name = "Casa Bianca";
            _kit.Activity(db).Record(OrgActivity.Of(org.Id, OrgActivityType.OrgNameChanged, "auth0|owner", org.Id.ToString()));
            await db.SaveChangesAsync();
        }

        var line = Assert.Single(await _kit.ReadActivityAsync(org.Id));
        Assert.Equal(OrgActivityType.OrgNameChanged, line.Type);
        await using var verify = _kit.NewDb();
        Assert.Equal("Casa Bianca", (await verify.Orgs.SingleAsync(o => o.Id == org.Id)).Name);
    }

    [Fact]
    public async Task Record_ACallerThatNeverSaves_LeavesNoLine()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await using (var db = _kit.NewDb())
        {
            _kit.Activity(db).Record(Event(org.Id));
            // The change failed before its save: the unit of work is dropped with the context.
        }

        Assert.Empty(await _kit.ReadActivityAsync(org.Id));
    }

    [Fact]
    public async Task Record_TheInstantIsTheClocksToTheMicrosecond_SoItIsReadBackAsWritten()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        _kit.Clock.SetUtcNow(new DateTimeOffset(2026, 10, 9, 8, 7, 6, TimeSpan.Zero).AddTicks(1_234_567));

        await using (var db = _kit.NewDb())
        {
            _kit.Activity(db).Record(Event(org.Id));
            await db.SaveChangesAsync();
        }

        var line = Assert.Single(await _kit.ReadActivityAsync(org.Id));
        Assert.Equal(new DateTime(2026, 10, 9, 8, 7, 6, DateTimeKind.Utc).AddTicks(1_234_560), line.When);
        Assert.Equal(DateTimeKind.Utc, line.When.Kind);
    }

    [Fact]
    public async Task Record_TheAreaAndTheSubjectTypeFollowTheCatalog()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await using (var db = _kit.NewDb())
        {
            var activity = _kit.Activity(db);
            activity.Record(OrgActivity.Of(org.Id, OrgActivityType.MemberInvited, "auth0|owner", Guid.NewGuid().ToString()));
            activity.Record(OrgActivity.Of(org.Id, OrgActivityType.PlanChanged, null, org.Id.ToString()));
            activity.Record(OrgActivity.Of(org.Id, OrgActivityType.TrustedSupplierAdded, "auth0|owner", Guid.NewGuid().ToString()));
            await db.SaveChangesAsync();
        }

        var lines = (await _kit.ReadActivityAsync(org.Id)).ToDictionary(l => l.Type);
        Assert.Equal((OrgActivityArea.Account, OrgActivitySubjectType.Invitation), (lines[OrgActivityType.MemberInvited].Area, lines[OrgActivityType.MemberInvited].SubjectType));
        Assert.Equal((OrgActivityArea.Account, OrgActivitySubjectType.Org), (lines[OrgActivityType.PlanChanged].Area, lines[OrgActivityType.PlanChanged].SubjectType));
        Assert.Null(lines[OrgActivityType.PlanChanged].ActorUserId);
        Assert.Equal((OrgActivityArea.Supplier, OrgActivitySubjectType.Supplier), (lines[OrgActivityType.TrustedSupplierAdded].Area, lines[OrgActivityType.TrustedSupplierAdded].SubjectType));
    }

    [Fact]
    public async Task Record_ARecordedEventsAreaCanBeGivenByTheCaller_WhenTheTypeDoesNotFixIt()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await using (var db = _kit.NewDb())
        {
            _kit.Activity(db).Record(new OrgActivity(
                org.Id, OrgActivityType.PropertyModeChanged, null, Guid.NewGuid().ToString(), Area: OrgActivityArea.LongRent));
            await db.SaveChangesAsync();
        }

        Assert.Equal(OrgActivityArea.LongRent, Assert.Single(await _kit.ReadActivityAsync(org.Id)).Area);
    }

    [Fact]
    public async Task Record_TheDetailsAreStoredAsAnObjectOfCodes()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await using (var db = _kit.NewDb())
        {
            _kit.Activity(db).Record(OrgActivity.Of(
                org.Id,
                OrgActivityType.MemberRoleChanged,
                "auth0|owner",
                "auth0|anna",
                (OrgActivityDetailKeys.ToRole, "Admin"),
                (OrgActivityDetailKeys.FromRole, "Collaborator")));
            await db.SaveChangesAsync();
        }

        var line = Assert.Single(await _kit.ReadActivityAsync(org.Id));
        Assert.Equal("{\"fromRole\":\"Collaborator\",\"toRole\":\"Admin\"}", line.DetailsJson);
    }

    [Fact]
    public async Task Record_WithTheFlagOff_CollectsNothing_ButStillRefusesAWrongEvent()
    {
        _kit.OrgTeamFlag = false;
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await using (var db = _kit.NewDb())
        {
            var activity = _kit.Activity(db);
            activity.Record(Event(org.Id));
            Assert.Throws<ArgumentException>(() =>
                activity.Record(OrgActivity.Of(org.Id, OrgActivityType.MemberDeactivated, "auth0|a", "auth0|b", ("email", "anna@example.com"))));
            Assert.Empty(db.ChangeTracker.Entries<OrgActivityEntry>());
            await db.SaveChangesAsync();
        }

        Assert.Empty(await _kit.ReadActivityAsync(org.Id));
    }

    [Fact]
    public async Task Record_WhatTheCatalogRefuses_StagesNothing()
    {
        var (org, _) = await _kit.SeedOwnerOrgAsync();
        await using var db = _kit.NewDb();
        var activity = _kit.Activity(db);

        Assert.Throws<ArgumentException>(() =>
            activity.Record(OrgActivity.Of(org.Id, OrgActivityType.MemberDeactivated, "auth0|a", "auth0|b", (OrgActivityDetailKeys.Role, "Mario Rossi"))));
        Assert.Throws<ArgumentException>(() =>
            activity.Record(OrgActivity.Of(org.Id, OrgActivityType.MemberDeactivated, "auth0|a", "auth0|b", (OrgActivityDetailKeys.ToRole, "Admin"))));
        Assert.Throws<ArgumentException>(() =>
            activity.Record(OrgActivity.Of(Guid.Empty, OrgActivityType.MemberDeactivated, "auth0|a", "auth0|b")));

        Assert.Empty(db.ChangeTracker.Entries<OrgActivityEntry>());
    }

    [Fact]
    public async Task EveryLineIsOfItsOrg_NotOfTheOrgTheRequestIsIn()
    {
        var (orgA, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-a");
        var (orgB, _) = await _kit.SeedOwnerOrgAsync("auth0|owner-b");
        await using (var db = _kit.NewDb())
        {
            var activity = _kit.Activity(db);
            activity.Record(Event(orgA.Id));
            activity.Record(Event(orgB.Id, OrgActivityType.MemberReactivated));
            await db.SaveChangesAsync();
        }

        Assert.Equal([OrgActivityType.MemberDeactivated], (await _kit.ReadActivityAsync(orgA.Id)).Select(l => l.Type));
        Assert.Equal([OrgActivityType.MemberReactivated], (await _kit.ReadActivityAsync(orgB.Id)).Select(l => l.Type));
    }
}
