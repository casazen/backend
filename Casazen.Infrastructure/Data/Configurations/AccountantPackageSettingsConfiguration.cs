using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// How the package of the accountant is sent (E15/F01/US02/T01, NF-101): one row for each organization, so a second row of the same org is refused by the database, whoever writes it. The organization restricts
/// like every other <c>OrgId</c> foreign key (TN-2). The address gets its converter from <c>EncryptedColumns</c> (no length: the payload is longer than the text). The database refuses a day that is not 1 to 28,
/// a set of folders that is empty or holds a bit that is no folder, and a result that is not a known one, so no write can store what the settings refuse.
/// </summary>
internal sealed class AccountantPackageSettingsConfiguration : IEntityTypeConfiguration<AccountantPackageSettings>
{
    public void Configure(EntityTypeBuilder<AccountantPackageSettings> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(s => s.OrgId).IsUnique().HasDatabaseName(AccountantPackageSettings.OrgIndexName);

        entity.ToTable(table =>
        {
            table.HasCheckConstraint(
                "CK_AccountantPackageSettings_SendDay",
                $"\"SendDayOfMonth\" BETWEEN {AccountantPackageSettings.MinSendDay} AND {AccountantPackageSettings.MaxSendDay}");
            table.HasCheckConstraint(
                "CK_AccountantPackageSettings_Contents",
                $"\"Contents\" BETWEEN 1 AND {(int)AccountantPackageFolders.All}");
            table.HasCheckConstraint(
                "CK_AccountantPackageSettings_LastSendResult",
                $"\"LastSendResult\" IS NULL OR \"LastSendResult\" IN ({(int)AccountantPackageSendResult.Sent}, {(int)AccountantPackageSendResult.Failed})");
        });
    }
}
