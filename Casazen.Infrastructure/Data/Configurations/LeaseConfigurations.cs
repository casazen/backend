using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class LeaseDocumentConfiguration : IEntityTypeConfiguration<LeaseDocument>
{
    public void Configure(EntityTypeBuilder<LeaseDocument> entity)
    {
        entity.HasOne(d => d.LeaseContract).WithMany()
            .HasForeignKey(d => d.LeaseContractId).OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(d => new { d.LeaseContractId, d.UploadedAt });
        entity.HasIndex(d => d.OrgId);
    }
}

internal sealed class LeaseContractConfiguration : IEntityTypeConfiguration<LeaseContract>
{
    public void Configure(EntityTypeBuilder<LeaseContract> entity)
    {
        // LeaseContract → Property (restrict to preserve history)
        entity.HasOne(l => l.Property)
            .WithMany()
            .HasForeignKey(l => l.PropertyId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.Property(l => l.MonthlyRent)
            .HasPrecision(18, 2);

        // Canone concordato characteristics and range of the lease (LT-10): same table, optional.
        entity.OwnsOne(l => l.ConcordatoAssessment);
        entity.Navigation(l => l.ConcordatoAssessment).IsRequired(false);

        entity.HasIndex(l => l.PropertyId);
        entity.HasIndex(l => l.Status);

        // LR-14: what is asked of the leases of an org is asked of the end of the period they are in now (the lists, the deadlines, the
        // occupancy of a property, the tacit renewal job), never of the end the contract states.
        entity.HasIndex(l => new { l.OrgId, l.CurrentTermEndDate });
    }

    internal static void ConfigureOrgIdIndex(EntityTypeBuilder<LeaseContract> entity)
    {
        entity.HasIndex(l => l.OrgId);
    }

    internal static void ConfigureOrgIdForeignKey(EntityTypeBuilder<LeaseContract> entity)
    {
        entity.HasOne(l => l.Org).WithMany().HasForeignKey(l => l.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PartyConfiguration : IEntityTypeConfiguration<Party>
{
    public void Configure(EntityTypeBuilder<Party> entity)
    {
        // Party → LeaseContract (cascade)
        entity.HasOne(p => p.LeaseContract)
            .WithMany(l => l.Parties)
            .HasForeignKey(p => p.LeaseContractId)
            .OnDelete(DeleteBehavior.Cascade);

        // LT-14: several parties per role, in the order entered.
        entity.HasIndex(p => new { p.LeaseContractId, p.Role, p.Position })
            .IsUnique();
    }
}

internal sealed class LeaseRegistrationConfiguration : IEntityTypeConfiguration<LeaseRegistration>
{
    public void Configure(EntityTypeBuilder<LeaseRegistration> entity)
    {
        // LeaseRegistration → LeaseContract (1-to-1, cascade)
        entity.HasOne(r => r.LeaseContract)
            .WithOne(l => l.Registration)
            .HasForeignKey<LeaseRegistration>(r => r.LeaseContractId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(r => r.LeaseContractId)
            .IsUnique();

        // LT-01 (A7-01): "Registered" only with the official receipt stored, never as a simulation.
        entity.ToTable(t => t.HasCheckConstraint(
                "CK_LeaseRegistrations_RegisteredRequiresReceipt",
                $"\"Status\" <> {(int)RegistrationStatus.Registered} OR btrim(coalesce(\"ReceiptStoragePath\", '')) <> ''"));
    }
}

internal sealed class LeaseEventConfiguration : IEntityTypeConfiguration<LeaseEvent>
{
    public void Configure(EntityTypeBuilder<LeaseEvent> entity)
    {
        // LeaseEvent → LeaseContract (cascade)
        entity.HasOne(e => e.LeaseContract)
            .WithMany(l => l.Events)
            .HasForeignKey(e => e.LeaseContractId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(e => new { e.LeaseContractId, e.OccurredAt });
    }
}

internal sealed class LeaseSignerConfiguration : IEntityTypeConfiguration<LeaseSigner>
{
    public void Configure(EntityTypeBuilder<LeaseSigner> entity)
    {
        // LeaseSigner (LT-02, A7-16): one row per party and lease, removed with the lease or the party.
        entity.HasOne(s => s.LeaseContract)
            .WithMany(l => l.Signers)
            .HasForeignKey(s => s.LeaseContractId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(s => s.Party)
            .WithMany()
            .HasForeignKey(s => s.PartyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(s => s.Org)
            .WithMany()
            .HasForeignKey(s => s.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(s => new { s.LeaseContractId, s.PartyId })
            .IsUnique();
    }
}

internal sealed class LeaseRegistrationAuthorizationConfiguration : IEntityTypeConfiguration<LeaseRegistrationAuthorization>
{
    public void Configure(EntityTypeBuilder<LeaseRegistrationAuthorization> entity)
    {
        entity.HasOne(a => a.LeaseContract)
            .WithMany()
            .HasForeignKey(a => a.LeaseContractId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(a => a.Org)
            .WithMany()
            .HasForeignKey(a => a.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(a => a.LeaseContractId);
        entity.HasIndex(a => a.OrgId);
    }
}

internal sealed class OpenapiLeaseRegistrationPaymentConfiguration : IEntityTypeConfiguration<OpenapiLeaseRegistrationPayment>
{
    public void Configure(EntityTypeBuilder<OpenapiLeaseRegistrationPayment> entity)
    {
        entity.HasOne(p => p.LeaseContract)
            .WithMany()
            .HasForeignKey(p => p.LeaseContractId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne(p => p.Org)
            .WithMany()
            .HasForeignKey(p => p.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.Property(p => p.FeePercent)
            .HasPrecision(7, 4);
        entity.HasIndex(p => p.StripeCheckoutSessionId)
            .IsUnique();
        entity.HasIndex(p => new { p.OrgId, p.LeaseContractId });
    }
}

internal sealed class LeaseMaintenanceRequestConfiguration : IEntityTypeConfiguration<LeaseMaintenanceRequest>
{
    public void Configure(EntityTypeBuilder<LeaseMaintenanceRequest> entity)
    {
        entity.HasOne(r => r.LeaseContract)
            .WithMany()
            .HasForeignKey(r => r.LeaseContractId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne<Org>()
            .WithMany()
            .HasForeignKey(r => r.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(r => new { r.OrgId, r.LeaseContractId, r.CreatedAt });
    }
}

internal sealed class LeaseCondominiumTermsConfiguration : IEntityTypeConfiguration<LeaseCondominiumTerms>
{
    public void Configure(EntityTypeBuilder<LeaseCondominiumTerms> entity)
    {
        // One current condominium clause per lease. The source document is optional and cleared when that document goes.
        entity.HasOne(t => t.LeaseContract)
            .WithOne(l => l.CondominiumTerms)
            .HasForeignKey<LeaseCondominiumTerms>(t => t.LeaseContractId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(t => t.Org)
            .WithMany()
            .HasForeignKey(t => t.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasOne(t => t.SourceDocument)
            .WithMany()
            .HasForeignKey(t => t.SourceDocumentId)
            .OnDelete(DeleteBehavior.SetNull);

        entity.Property(t => t.AdvanceAmount)
            .HasPrecision(18, 2);

        entity.Property(t => t.ExtractionConfidence)
            .HasPrecision(5, 4);

        entity.Property(t => t.AdvanceMode)
            .HasConversion<int>();

        entity.HasIndex(t => t.LeaseContractId)
            .IsUnique();

        entity.HasIndex(t => t.OrgId);

        entity.ToTable(t => t.HasCheckConstraint(
                "CK_LeaseCondominiumTerms_Confidence",
                "\"ExtractionConfidence\" IS NULL OR (\"ExtractionConfidence\" >= 0 AND \"ExtractionConfidence\" <= 1)"));
    }
}
