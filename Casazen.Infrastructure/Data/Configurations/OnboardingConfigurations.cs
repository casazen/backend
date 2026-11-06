using Casazen.Core.Entities;
using Casazen.Core.Entities.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Property = Casazen.Core.Entities.Property;

namespace Casazen.Infrastructure.Data.Configurations;

/// <summary>
/// The state of one part of the Octorate activation plan (NF-104, E10/F01/US01/T01): one row per org and part (a concurrent first
/// «Do it later» of the same part loses on the unique index), carrying the org (TN-2, restrict like every other <c>OrgId</c> foreign key).
/// The part and the status are stored as their numbers.
/// </summary>
internal sealed class OnboardingPartStateConfiguration : IEntityTypeConfiguration<OnboardingPartState>
{
    public void Configure(EntityTypeBuilder<OnboardingPartState> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(s => new { s.OrgId, s.PartKey })
            .IsUnique()
            .HasDatabaseName(OnboardingPartState.OrgPartIndexName);
    }
}

/// <summary>
/// The activation of a flow on a property (NF-106): one row per property and flow, going with the property (cascade) and carrying its org
/// (TN-2, restrict). An activation after a deactivation reuses the row, so the unique index is also what lets two activations of the same
/// flow end on one row.
/// </summary>
internal sealed class PropertyFlowActivationConfiguration : IEntityTypeConfiguration<PropertyFlowActivation>
{
    public void Configure(EntityTypeBuilder<PropertyFlowActivation> entity)
    {
        entity.HasOne<Property>().WithMany().HasForeignKey(a => a.PropertyId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(a => a.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(a => new { a.PropertyId, a.Flow })
            .IsUnique()
            .HasDatabaseName(PropertyFlowActivation.PropertyFlowIndexName);
        entity.HasIndex(a => a.OrgId);
    }
}

/// <summary>
/// A guided step of the Octorate panel (NF-107): one row per org, property, kind and channel. The unique index treats two NULLs as equal
/// (<c>NULLS NOT DISTINCT</c>), so the steps with no property or no channel are held once too. The row goes with its property (cascade, when
/// it has one) and carries the org (TN-2, restrict).
/// </summary>
internal sealed class OnboardingPanelStepConfiguration : IEntityTypeConfiguration<OnboardingPanelStep>
{
    public void Configure(EntityTypeBuilder<OnboardingPanelStep> entity)
    {
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(s => s.PropertyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(s => s.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(s => new { s.OrgId, s.PropertyId, s.Kind, s.ChannelCode })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasDatabaseName(OnboardingPanelStep.StepIndexName);
        entity.HasIndex(s => s.PropertyId);
    }
}

/// <summary>
/// A «Do it for me» request (NF-107, UX-15): going with its property (cascade, when it has one) and carrying the org (TN-2, restrict). The
/// partial unique index holds one OPEN request (Requested or InProgress) for each org, part and property, with null-equal semantics, so a
/// request about the whole org is held once too; a closed request (Done, Cancelled) is left out of it and a new one can follow. The filter
/// repeats the numbers of <see cref="OnboardingAssistStatus"/>, which never change.
/// </summary>
internal sealed class OnboardingAssistRequestConfiguration : IEntityTypeConfiguration<OnboardingAssistRequest>
{
    public void Configure(EntityTypeBuilder<OnboardingAssistRequest> entity)
    {
        entity.HasOne<Property>()
            .WithMany()
            .HasForeignKey(r => r.PropertyId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(r => r.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(r => new { r.OrgId, r.PartKey, r.PropertyId })
            .IsUnique()
            .AreNullsDistinct(false)
            .HasFilter(
                $"\"Status\" IN ({(int)OnboardingAssistStatus.Requested}, {(int)OnboardingAssistStatus.InProgress})")
            .HasDatabaseName(OnboardingAssistRequest.OpenRequestIndexName);
        entity.HasIndex(r => r.OrgId);
        entity.HasIndex(r => r.PropertyId);
    }
}

/// <summary>
/// A property of the pilot group (NF-105, H10): one row per property (unique index), going with the property (cascade) and carrying its org
/// (TN-2, restrict).
/// </summary>
internal sealed class OnboardingPilotMemberConfiguration : IEntityTypeConfiguration<OnboardingPilotMember>
{
    public void Configure(EntityTypeBuilder<OnboardingPilotMember> entity)
    {
        entity.HasOne<Property>().WithMany().HasForeignKey(m => m.PropertyId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(m => m.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(m => m.PropertyId)
            .IsUnique()
            .HasDatabaseName(OnboardingPilotMember.PropertyIndexName);
        entity.HasIndex(m => m.OrgId);
    }
}

/// <summary>
/// The per-property data of the activation (NF-105): one row per property (unique index), going with the property (cascade) and carrying its
/// org (TN-2, restrict).
/// </summary>
internal sealed class PropertyOnboardingDataConfiguration : IEntityTypeConfiguration<PropertyOnboardingData>
{
    public void Configure(EntityTypeBuilder<PropertyOnboardingData> entity)
    {
        entity.HasOne<Property>().WithMany().HasForeignKey(d => d.PropertyId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Org>().WithMany().HasForeignKey(d => d.OrgId).OnDelete(DeleteBehavior.Restrict);
        entity.HasIndex(d => d.PropertyId)
            .IsUnique()
            .HasDatabaseName(PropertyOnboardingData.PropertyIndexName);
        entity.HasIndex(d => d.OrgId);
    }
}
