using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Services;
using Casazen.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Casazen.Tests.Unit.Infrastructure;

/// <summary>
/// AM-02c: the SQL of the page of the access of a member on the real provider (Npgsql, no server: <see cref="NpgsqlTranslationProbe"/>).
/// «Chi può accedere» counts the grants of the collaborators «Solo alcuni» that the area of each one reaches, in the same grouped
/// statement as before: a join to the property for its mode and the two memberships it is compared with, inside the statement, no
/// list read beforehand and no query per property. The values are asserted in <c>OrgMemberAreasTests</c> (InMemory).
/// </summary>
public class OrgPropertyAccessSqlShapeTests
{
    private static readonly Guid OrgId = Guid.Parse("7a3c1d52-9f0e-4b8a-8d65-2c4e1f0b9a11");

    private static int Count(string text, string part) => text.Split(part, StringSplitOptions.None).Length - 1;

    [Fact]
    public async Task TheView_IsFourStatementsWhateverTheNumberOfProperties_AndTheCountReadsTheAreaInsideItsStatement()
    {
        var statements = new List<string>();
        await using var db = NpgsqlTranslationProbe.NewContext(statements);
        var service = new OrgPropertyAccessService(
            db, Mock.Of<IUserAuthorizationCache>(), Mock.Of<IActivityLog>(), NullLogger<OrgPropertyAccessService>.Instance);
        var limited = new OrgMember
        {
            OrgId = OrgId,
            UserId = "auth0|collaboratore",
            Role = OrgRole.Collaborator,
            PropertyScope = PropertyScope.Selected,
        };

        await NpgsqlTranslationProbe.AssertTranslatesAsync(() => service.BuildViewAsync(OrgId, limited, CancellationToken.None));

        // The properties, the grants of this member, the members who reach everything, the grants of the others counted per property.
        Assert.Equal(4, statements.Count);
        var counted = Assert.Single(statements, s => s.Contains("GROUP BY", StringComparison.Ordinal));
        Assert.Contains("\"PropertyMemberAccesses\"", counted, StringComparison.Ordinal);
        Assert.Contains("\"OrgMembers\"", counted, StringComparison.Ordinal);
        // The mode of the property, and the two memberships of the person compared with it (the area of its mode, or no other).
        Assert.Contains("\"RentalMode\"", counted, StringComparison.Ordinal);
        Assert.Equal(2, Count(counted, "\"UserContextMemberships\""));
        Assert.DoesNotContain(" IN (", counted, StringComparison.Ordinal);
    }
}
