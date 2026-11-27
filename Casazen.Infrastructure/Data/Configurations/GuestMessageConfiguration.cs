using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Casazen.Core.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class GuestMessageConfiguration : IEntityTypeConfiguration<GuestMessage>
{
    public void Configure(EntityTypeBuilder<GuestMessage> entity)
    {
        entity.HasOne(m => m.Thread)
            .WithMany()
            .HasForeignKey(m => m.ThreadId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(m => m.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        // Who wrote the message (E05/F05/US01/T01, NF-67): the guest unless the writer says otherwise. It is the value the migration gave every message that existed
        // (the messages of the host were then set to a person) and the one a row written without it reads as; the code that writes a message sets it.
        entity.Property(m => m.Origin).HasDefaultValue(MessageOrigin.Guest);

        entity.ToTable(t => t.HasCheckConstraint("CK_GuestMessages_Sender", "\"Sender\" IN (0, 1)"));
    }
}
