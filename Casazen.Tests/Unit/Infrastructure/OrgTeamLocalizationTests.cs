using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Xml.Linq;
using Casazen.Core.Services;
using Casazen.Web.DTOs.Orgs;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AM-02: every refusal of the org team has a stable code and a message in Italian and in English (the clients translate
/// the code, the API localizes the message). The table is the contract: a code without a message, a message that is the same
/// in both languages or a validation key that does not exist fails here, without starting the web host.
/// </summary>
public class OrgTeamLocalizationTests
{
    /// <summary>Every code of the team and the key of its message, written out by hand.</summary>
    public static TheoryData<string, string> CodesAndKeys => new()
    {
        { OrgInvitationErrors.Invalid, "InvitationInvalid" },
        { OrgInvitationErrors.Expired, "InvitationExpired" },
        { OrgInvitationErrors.Used, "InvitationUsed" },
        { OrgInvitationErrors.Revoked, "InvitationRevoked" },
        { OrgInvitationErrors.EmailMismatch, "InvitationEmailMismatch" },
        { OrgInvitationErrors.EmailNotVerified, "InvitationEmailNotVerified" },
        { OrgInvitationErrors.PlatformAdmin, "InvitationPlatformAdmin" },
        { OrgInvitationErrors.OwnerRequired, "OrgOwnerRequired" },
        { OrgInvitationErrors.UserHasOrganization, "InvitationUserHasOrganization" },
        { OrgInvitationErrors.AlreadyPending, "OrgInvitationAlreadyPending" },
        { OrgInvitationErrors.NotPending, "OrgInvitationNotPending" },
        { OrgInvitationErrors.NotFound, "OrgInvitationNotFound" },
        { OrgInvitationErrors.MemberNotFound, "OrgMemberNotFound" },
        { OrgSeatErrors.LimitReached, "OrgSeatLimitReached" },
        { OrgMembershipErrors.LastOwner, "OrgLastOwner" },
        { OrgMembershipErrors.OwnerNotAssignable, "OrgMemberOwnerNotAssignable" },
        { OrgMembershipErrors.AreaRequired, "OrgMemberAreaRequired" },
        { OrgMembershipErrors.AlreadyMember, "OrgMemberAlreadyMember" },
        { OrgMembershipErrors.OtherOrg, "OrgMemberOtherOrg" },
        { OrgMembershipErrors.ScopeNotSupported, "OrgMemberScopeNotSupported" },
        { OrgMembershipErrors.PropertyUnknown, "OrgMemberPropertyUnknown" },
        { PropertyResponsibleErrors.Invalid, "PropertyResponsibleInvalid" },
        { OrgAccessRequestErrors.AreaUnknown, "AccessRequestAreaUnknown" },
        { OrgAccessRequestErrors.LimitReached, "AccessRequestLimitReached" },
    };

    /// <summary>The messages of the 400 the activity log answers for a filter that makes no sense (no code of their own: the generic validation code).</summary>
    public static TheoryData<string> ActivityFilterKeys => new()
    {
        "OrgActivityTypeUnknown",
        "OrgActivityAreaUnknown",
        "OrgActivityRangeInvalid",
    };

    [Theory]
    [MemberData(nameof(ActivityFilterKeys))]
    public void EveryFilterOfTheActivityLog_HasAMessageInItalianAndADifferentOneInEnglish(string key)
    {
        var italian = Resources(english: false);
        var english = Resources(english: true);

        Assert.True(italian.TryGetValue(key, out var it) && !string.IsNullOrWhiteSpace(it), $"no Italian message '{key}'");
        Assert.True(english.TryGetValue(key, out var en) && !string.IsNullOrWhiteSpace(en), $"no English message '{key}'");
        Assert.NotEqual(it, en);
    }

    [Theory]
    [MemberData(nameof(CodesAndKeys))]
    public void EveryCode_HasAMessageInItalianAndADifferentOneInEnglish(string code, string key)
    {
        var italian = Resources(english: false);
        var english = Resources(english: true);

        Assert.True(italian.TryGetValue(key, out var it) && !string.IsNullOrWhiteSpace(it), $"{code}: no Italian message '{key}'");
        Assert.True(english.TryGetValue(key, out var en) && !string.IsNullOrWhiteSpace(en), $"{code}: no English message '{key}'");
        Assert.NotEqual(it, en);
        Assert.NotEqual(key, it);
        Assert.NotEqual(key, en);
    }

    [Fact]
    public void TheTable_CoversEveryCodeOfTheTeamAndNothingElse()
    {
        var inTable = CodesAndKeys.Select(row => (string)row[0]).ToHashSet();
        var declared = new[]
            {
                typeof(OrgInvitationErrors), typeof(OrgSeatErrors), typeof(OrgMembershipErrors), typeof(PropertyResponsibleErrors),
                typeof(OrgAccessRequestErrors),
            }
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) })
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();

        Assert.Empty(declared.Except(inTable));
        Assert.Empty(inTable.Except(declared));
    }

    [Fact]
    public void EveryCode_IsStableSnakeCase()
    {
        Assert.All(CodesAndKeys.Select(row => (string)row[0]), code => Assert.Matches("^[a-z]+(_[a-z]+)+$", code));
    }

    [Fact]
    public void TheValidationMessagesOfTheTeamDtos_AreResourceKeys()
    {
        var italian = Resources(english: false);
        var english = Resources(english: true);
        var keys = new[]
            {
                typeof(CreateOrgInvitationRequest), typeof(OrgInvitationLookupRequest), typeof(AcceptOrgInvitationRequest),
                typeof(SetOrgMemberPropertiesRequest), typeof(Casazen.Web.Controllers.SetPropertyResponsibleRequest),
                typeof(RequestOrgAccessRequest),
            }
            .SelectMany(t => t.GetProperties())
            .SelectMany(p => p.GetCustomAttributes<ValidationAttribute>())
            .Select(a => a.ErrorMessage)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct()
            .ToList();

        Assert.NotEmpty(keys);
        Assert.All(keys, key =>
        {
            Assert.True(italian.ContainsKey(key!), $"'{key}' is not a key of SharedResources.resx");
            Assert.True(english.ContainsKey(key!), $"'{key}' is not a key of SharedResources.en.resx");
            Assert.NotEqual(italian[key!], english[key!]);
        });
    }

    private static Dictionary<string, string> Resources(bool english)
    {
        var path = Path.Combine(FindRepositoryRoot(), "Casazen.Web", "Resources", english ? "SharedResources.en.resx" : "SharedResources.resx");
        return XDocument.Load(path).Root!
            .Elements("data")
            .ToDictionary(e => (string)e.Attribute("name")!, e => (string?)e.Element("value") ?? string.Empty, StringComparer.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Casazen.sln")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Casazen.sln not found above the test output folder.");
    }
}
