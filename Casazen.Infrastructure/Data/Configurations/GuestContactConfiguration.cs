using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The CRM contacts (NF-88, NF-89): the pattern of <see cref="SuppressionListEntryConfiguration"/> (a keyed index that finds the row, unique
/// with the org) and of <c>ServiceCustomer</c> (the value encrypted at rest, declared in <c>EncryptedColumns</c>, the hash keyed).
/// </summary>
internal sealed class GuestContactConfiguration : IEntityTypeConfiguration<GuestContact>
{
    public void Configure(EntityTypeBuilder<GuestContact> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(c => c.OrgId).OnDelete(DeleteBehavior.Restrict);

        // The contacts go with their profile; a contact with no profile is a lead from the welcome page that has none yet.
        entity.HasOne<GuestCrmProfile>().WithMany().HasForeignKey(c => c.GuestCrmProfileId).OnDelete(DeleteBehavior.Cascade);

        // The guest the contact was given by: a guest without bookings can be deleted, and the contact stays with its profile.
        entity.HasOne<Guest>().WithMany().HasForeignKey(c => c.GuestId).OnDelete(DeleteBehavior.SetNull);

        // The columns say what they hold: Value and DisplayName are encrypted at rest through the value converter of EncryptedColumns, and the
        // keyed hash of the value is the only thing a contact is found by. One contact per org, channel and value.
        entity.HasIndex(c => new { c.OrgId, c.Channel, c.ValueHash })
            .IsUnique()
            .HasDatabaseName("UIX_GuestContacts_OrgId_Channel_ValueHash");
        entity.HasIndex(c => new { c.OrgId, c.GuestCrmProfileId })
            .HasDatabaseName("IX_GuestContacts_OrgId_GuestCrmProfileId");
        entity.HasIndex(c => new { c.OrgId, c.IsSuppressed })
            .HasDatabaseName("IX_GuestContacts_OrgId_IsSuppressed");
    }
}
