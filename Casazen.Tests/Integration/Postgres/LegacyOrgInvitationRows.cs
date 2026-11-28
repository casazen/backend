using Casazen.Core.Entities;
using Casazen.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Casazen.Tests.Integration.Postgres;

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
