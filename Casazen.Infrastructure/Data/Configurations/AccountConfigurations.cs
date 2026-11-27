using Casazen.Core.Authorization;
using Casazen.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using AppContextEntity = Casazen.Core.Entities.AppContext;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class OrgConfiguration : IEntityTypeConfiguration<Org>
{
    public void Configure(EntityTypeBuilder<Org> entity)
    {
        // ─── Multi-tenant Org boundary (US-004) ──────────────────────────────────
        // Org tenant key with a unique Slug (AC1).
        entity.HasIndex(o => o.Slug)
            .IsUnique();

        entity.HasIndex(o => o.StripeCustomerId);

        // The language of the account (AM-08b-b): the orgs that exist when the column is added get Italian, the language they have always had.
        entity.Property(o => o.DefaultLanguage)
            .HasDefaultValue(Casazen.Core.OrgTeam.OrgInvitationRules.DefaultLanguage);
    }

    internal static void ConfigureCustomDomainIndex(EntityTypeBuilder<Org> entity)
    {
        entity.HasIndex(o => o.CustomDomain)
            .IsUnique()
            .HasFilter("\"CustomDomain\" IS NOT NULL AND \"DomainVerificationStatus\" = 1");

        entity.HasIndex(o => o.Subdomain)
            .IsUnique()
            .HasFilter("\"Subdomain\" IS NOT NULL");

        // Surfaces of the agency's own domain (E40/F01/US01): the guest area and the owner portal start off and the Auth0 state NotRequested
        // (the defaults of their types), the links follow the domain: on for the orgs that exist too, it takes effect only once the domain is
        // verified and a surface is on. EF always writes the value of the entity: left to generate it, an explicit false would be taken for
        // «not set» and the insert would write true in its place.
        entity.Property(o => o.UseOwnDomainForLinks)
            .HasDefaultValue(true)
            .ValueGeneratedNever();
    }

    internal static void ConfigureSupplierBackupsEnabled(EntityTypeBuilder<Org> entity)
    {
        // Whether an org's requests also go to backup suppliers: on for every org that exists (the rule is general), so the migration gives
        // the existing rows true. EF itself always writes the value of the entity: left to generate it, EF would take an explicit false for
        // «not set» and the insert would write the default (true) in its place, turning the rule back on for an org that switched it off.
        entity.Property(o => o.SupplierBackupsEnabled)
            .HasDefaultValue(true)
            .ValueGeneratedNever();
    }
}

internal sealed class OrgSlugAliasConfiguration : IEntityTypeConfiguration<OrgSlugAlias>
{
    public void Configure(EntityTypeBuilder<OrgSlugAlias> entity)
    {
        // Previous public slugs of an org (PL-04, A1-23): shared links keep resolving and the value stays reserved.
        entity.HasOne(a => a.Org)
            .WithMany()
            .HasForeignKey(a => a.OrgId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(a => a.OrgId);
    }
}

internal sealed class PlatformInvoiceConfiguration : IEntityTypeConfiguration<PlatformInvoice>
{
    public void Configure(EntityTypeBuilder<PlatformInvoice> entity)
    {
        entity.HasOne(i => i.Org)
            .WithMany()
            .HasForeignKey(i => i.OrgId)
            .OnDelete(DeleteBehavior.Restrict);

        entity.HasIndex(i => i.StripeInvoiceId)
            .IsUnique();

        entity.HasIndex(i => i.OrgId);

        entity.HasIndex(i => i.SdiStatus);

        entity.Property(i => i.AmountExVat)
            .HasPrecision(18, 2);

        entity.Property(i => i.VatAmount)
            .HasPrecision(18, 2);

        entity.Property(i => i.TotalAmount)
            .HasPrecision(18, 2);
    }
}

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> entity)
    {
        entity.HasIndex(u => u.OrgId);
    }

    internal static void ConfigureOrgIdForeignKey(EntityTypeBuilder<User> entity)
    {
        entity.HasOne(u => u.Org).WithMany().HasForeignKey(u => u.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AppContextEntityConfiguration : IEntityTypeConfiguration<AppContextEntity>
{
    public void Configure(EntityTypeBuilder<AppContextEntity> entity)
    {
        entity.HasKey(c => c.Key);
    }

    internal static void ConfigureSeedData(EntityTypeBuilder<AppContextEntity> entity)
    {
        entity.HasData(
            new AppContextEntity { Key = "short-rent", DisplayName = "Affitti brevi" },
            new AppContextEntity { Key = "long-rent", DisplayName = "Affitti lungo termine" },
            new AppContextEntity { Key = "admin", DisplayName = "Amministrazione" });
    }

    internal static void ConfigureSeedData2(EntityTypeBuilder<AppContextEntity> entity)
    {
        // AM-01: the account context and the roles of the org team. OrgRoleCatalog is the source (ids 4 to 12); the tests
        // compare this seed with it and with OrgOwnerRoles.
        entity.HasData(
            new AppContextEntity { Key = AccountContext.Key, DisplayName = AccountContext.DisplayName });
    }
}

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> entity)
    {
        entity.HasIndex(r => new { r.ContextKey, r.RoleKey })
            .IsUnique();

        entity.HasOne(r => r.Context)
            .WithMany(c => c.Roles)
            .HasForeignKey(r => r.ContextKey)
            .OnDelete(DeleteBehavior.Cascade);
    }

    internal static void ConfigureSeedData(EntityTypeBuilder<Role> entity)
    {
        entity.HasData(
            new Role { Id = 1, ContextKey = "short-rent", RoleKey = "property_owner" },
            new Role { Id = 2, ContextKey = "long-rent", RoleKey = "long_term_landlord" },
            new Role { Id = 3, ContextKey = "admin", RoleKey = "platform_admin" });
    }

    internal static void ConfigureSeedData2(EntityTypeBuilder<Role> entity)
    {
        entity.HasData(
            OrgRoleCatalog.SeededRoles.Select(r => new Role { Id = r.Id, ContextKey = r.ContextKey, RoleKey = r.RoleKey }));
    }
}

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> entity)
    {
        entity.HasKey(rp => new { rp.RoleId, rp.PermissionKey });

        entity.HasOne(rp => rp.Role)
            .WithMany(r => r.Permissions)
            .HasForeignKey(rp => rp.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    internal static void ConfigureSeedData(EntityTypeBuilder<RolePermission> entity)
    {
        entity.HasData(
            new RolePermission { RoleId = 1, PermissionKey = "property.read" },
            new RolePermission { RoleId = 1, PermissionKey = "property.write" },
            new RolePermission { RoleId = 1, PermissionKey = "booking.read" },
            new RolePermission { RoleId = 1, PermissionKey = "booking.write" },
            new RolePermission { RoleId = 1, PermissionKey = "payment.read" },
            new RolePermission { RoleId = 1, PermissionKey = "payment.write" },
            new RolePermission { RoleId = 1, PermissionKey = "ota.read" },
            new RolePermission { RoleId = 1, PermissionKey = "ota.write" },
            new RolePermission { RoleId = 1, PermissionKey = "guest.read" },
            new RolePermission { RoleId = 1, PermissionKey = "guest.write" },
            // AM-03: carved out of property.write, guest.write and booking.write; the owner keeps doing all of it.
            new RolePermission { RoleId = 1, PermissionKey = HostPermissions.ServiceRequestWrite },
            new RolePermission { RoleId = 1, PermissionKey = HostPermissions.GuestManage },
            new RolePermission { RoleId = 1, PermissionKey = HostPermissions.AlloggiatiSubmit },
            // E05/F04/US01/T01: the owner decides the items of the approval queue (AddApprovalDecisionAndOriginAction); the property manager's role follows the catalog.
            new RolePermission { RoleId = 1, PermissionKey = HostPermissions.ApprovalDecide },
            // E03/F04/US02/T01: the owner decides the identity verifications of its guests (AddIdentityVerificationFields).
            new RolePermission { RoleId = 1, PermissionKey = HostPermissions.VerificationDecision },
            // E50/F01/US01/T01: the owner always sets up its guest portal («Portale ospiti», AddGuestPortalLinks); the property manager's role follows the catalog.
            new RolePermission { RoleId = 1, PermissionKey = HostPermissions.GuestPortalManage },
            new RolePermission { RoleId = 1, PermissionKey = HostPermissions.Marketing },
            new RolePermission { RoleId = 2, PermissionKey = "property.read" },
            new RolePermission { RoleId = 2, PermissionKey = "property.write" },
            new RolePermission { RoleId = 2, PermissionKey = "lease.read" },
            new RolePermission { RoleId = 2, PermissionKey = "lease.create" },
            new RolePermission { RoleId = 2, PermissionKey = "lease.sign" },
            new RolePermission { RoleId = 2, PermissionKey = "lease.register" },
            new RolePermission { RoleId = 2, PermissionKey = "rent.read" },
            new RolePermission { RoleId = 2, PermissionKey = "rent.manage" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.stats.read" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.users.read" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.users.manage" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.cin.read" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.jobs.read" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.tax.manage" },
            new RolePermission { RoleId = 3, PermissionKey = "admin.seo.read" });
    }

    internal static void ConfigureSeedData2(EntityTypeBuilder<RolePermission> entity)
    {
        entity.HasData(
            OrgRoleCatalog.SeededRoles.SelectMany(r =>
                r.Permissions.Select(p => new RolePermission { RoleId = r.Id, PermissionKey = p })));
    }
}

internal sealed class UserContextMembershipConfiguration : IEntityTypeConfiguration<UserContextMembership>
{
    public void Configure(EntityTypeBuilder<UserContextMembership> entity)
    {
        entity.HasIndex(m => new { m.UserId, m.ContextKey })
            .IsUnique();

        entity.HasOne(m => m.User)
            .WithMany(u => u.ContextMemberships)
            .HasForeignKey(m => m.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(m => m.Context)
            .WithMany(c => c.Memberships)
            .HasForeignKey(m => m.ContextKey)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasOne(m => m.Role)
            .WithMany(r => r.Memberships)
            .HasForeignKey(m => m.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ConsentRecordConfiguration : IEntityTypeConfiguration<ConsentRecord>
{
    public void Configure(EntityTypeBuilder<ConsentRecord> entity)
    {
        entity.HasIndex(c => new { c.UserId, c.OrgId, c.Type });
    }
}

internal sealed class AgencyInvoiceConfiguration : IEntityTypeConfiguration<AgencyInvoice>
{
    public void Configure(EntityTypeBuilder<AgencyInvoice> entity)
    {
        // The invoices of the retired agency-to-owner generator stay readable and are never called compliant (E06/F01/US02/T05): the rows that exist
        // when the column is added get the legacy code.
        entity.Property(i => i.ComplianceStatus).HasDefaultValue(AgencyInvoiceCompliance.LegacyUnverified);
    }
}
