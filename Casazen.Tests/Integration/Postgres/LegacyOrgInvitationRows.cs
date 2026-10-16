using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Integration.Postgres;

/// <summary>
/// Invitation rows for migration tests that seed the schema right before <c>AddOrgTeamCompletions</c> (AM-02c). The current
/// <c>OrgInvitation</c> entity has two columns that table does not have yet (<c>LastSentAt</c>, <c>SendLog</c>), so the model
/// cannot write it there; the columns of the first <c>OrgInvitations</c> table are enough. Same rule as the siblings
/// (<see cref="LegacyOrgRows"/> and the others): <c>LegacyRowsSchemaTests</c> proves, without a database, that the statement fits
/// the schema at the migration point where a test uses it.
/// </summary>
internal static class LegacyOrgInvitationRows
{
    public static Task InsertAsync(AppDbContext db, OrgInvitation invitation) =>
        db.Database.ExecuteSqlInterpolatedAsync(Statement(invitation));

    internal static FormattableString Statement(OrgInvitation invitation) => $"""
        INSERT INTO "OrgInvitations" (
            "Id", "OrgId", "Email", "Name", "Role", "Areas", "PropertyScope", "TokenHash", "Status", "ExpiresAt",
            "ReminderSentAt", "InvitedByUserId", "Language", "AcceptedAt", "AcceptedByUserId", "ClosedAt", "CreatedAt", "UpdatedAt")
        VALUES ({invitation.Id}, {invitation.OrgId}, {invitation.Email}, {invitation.Name}, {(int)invitation.Role}, {invitation.Areas},
            {(int)invitation.PropertyScope}, {invitation.TokenHash}, {(int)invitation.Status}, {invitation.ExpiresAt},
            {invitation.ReminderSentAt}, {invitation.InvitedByUserId}, {invitation.Language}, {invitation.AcceptedAt},
            {invitation.AcceptedByUserId}, {invitation.ClosedAt}, {invitation.CreatedAt}, {invitation.UpdatedAt});
        """;
}
