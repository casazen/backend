using Casazen.Core.Entities;
using Casazen.Core.SiteProfile;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Property = Casazen.Core.Entities.Property;

namespace Casazen.Infrastructure.Data.Configurations;

internal sealed class PropertyActivationCredentialConfiguration : IEntityTypeConfiguration<PropertyActivationCredential>
{
    public void Configure(EntityTypeBuilder<PropertyActivationCredential> entity)
    {
        entity.HasIndex(p => new { p.OrgId, p.ExpiresAt });
    }
}

internal sealed class PropertyActivationCredentialAssignmentConfiguration : IEntityTypeConfiguration<PropertyActivationCredentialAssignment>
{
    public void Configure(EntityTypeBuilder<PropertyActivationCredentialAssignment> assignment)
    {
        assignment.HasOne<Property>().WithMany().HasForeignKey(a => a.PropertyId).OnDelete(DeleteBehavior.Cascade);
        assignment.HasOne<PropertyActivationCredential>().WithMany().HasForeignKey(a => a.AccountId).OnDelete(DeleteBehavior.Cascade);
        assignment.HasIndex(a => new { a.OrgId, a.PropertyId, a.Service }).IsUnique();
    }
}

internal sealed class PropertyImportReviewConfiguration : IEntityTypeConfiguration<PropertyImportReview>
{
    public void Configure(EntityTypeBuilder<PropertyImportReview> review)
    {
        review.HasOne<Property>().WithOne().HasForeignKey<PropertyImportReview>(p => p.Id).OnDelete(DeleteBehavior.Cascade);
        review.HasIndex(p => new { p.OrgId, p.Provider, p.SourceId }).IsUnique();
    }
}

internal sealed class PropertyConfiguration : IEntityTypeConfiguration<Property>
{
    public void Configure(EntityTypeBuilder<Property> entity)
    {
        entity.HasMany(p => p.Bookings)
            .WithOne(b => b.Property)
            .HasForeignKey(b => b.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasMany(p => p.OtaIntegrations)
            .WithOne(o => o.Property)
            .HasForeignKey(o => o.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    internal static void ConfigureGPSCoordinates(EntityTypeBuilder<Property> entity)
    {
        // GPS coordinates (PC-06, A2-33): 6 decimals are about 0.1 m, the old 2 decimals placed a house up to a kilometre
        // away on the map. numeric(9,6) holds -999.999999..999.999999: the API accepts only -90..90 and -180..180.
        entity.Property(p => p.Latitude)
            .HasPrecision(9, PropertyAddress.CoordinateScale);

        entity.Property(p => p.Longitude)
            .HasPrecision(9, PropertyAddress.CoordinateScale);

        // DB-03: the stay rules the public quote and checkout read (minimum stay, weekend surcharge). The host forms and the
        // property service validate them; the constraints keep any other writer honest. Both columns are nullable or 0 for
        // every row that existed before them, so the migration changes no behavior (docs/runbooks/direct-booking.md § 11).
        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_Properties_MinNights",
                $"\"MinNights\" IS NULL OR \"MinNights\" BETWEEN {PropertyStayRules.MinMinNights} AND {PropertyStayRules.MaxMinNights}");
            t.HasCheckConstraint(
                "CK_Properties_WeekendSurchargePercent",
                $"\"WeekendSurchargePercent\" BETWEEN 0 AND {(int)PropertyStayRules.MaxWeekendSurchargePercent}");
        });

        // SR-07a: the host's switch «Mostra la casa sul sito» starts true — in the migration, so every row that existed before the
        // column keeps being on the site — and it is one more condition of PublicListing.IsPublished. The default is for the writers
        // that do not know the column (the old release during a deploy, a raw INSERT); EF itself always writes the value of the
        // entity: left to generate it, EF would take an explicit false for «not set» and the insert would write the default (true)
        // in its place, putting back on the site a property its host hid.
        // The other fields of the wizard (type, beds, area, headline, times, rules, fee) are nullable: null = never said.
        entity.Property(p => p.PublishedOnSite)
            .HasDefaultValue(true)
            .ValueGeneratedNever();

        // DB-11a: the site profile (floor, renovation year, energy class and index, kind of place, children, latest arrival, video and tour
        // addresses, the JSON of the texts and of the photo alt texts). Every column is nullable: null = the host never said, which is
        // every property that existed before them. The host API validates (SitePropertyProfileRules); the constraints keep any other
        // writer honest. The ranges repeat the constants of SitePropertyProfileLimits.
        entity.ToTable(t =>
        {
            t.HasCheckConstraint(
                "CK_Properties_FloorLevel",
                $"\"FloorLevel\" IS NULL OR \"FloorLevel\" BETWEEN {SitePropertyProfileLimits.MinFloorLevel} AND {SitePropertyProfileLimits.MaxFloorLevel}");
            t.HasCheckConstraint(
                "CK_Properties_RenovatedYear",
                $"\"RenovatedYear\" IS NULL OR \"RenovatedYear\" >= {SitePropertyProfileLimits.MinRenovatedYear}");
            t.HasCheckConstraint(
                "CK_Properties_EnergyClass",
                "\"EnergyClass\" IS NULL OR \"EnergyClass\" IN ('A4', 'A3', 'A2', 'A1', 'A', 'B', 'C', 'D', 'E', 'F', 'G')");
            t.HasCheckConstraint(
                "CK_Properties_EnergyIndex",
                $"\"EnergyIndex\" IS NULL OR (\"EnergyClass\" IS NOT NULL AND \"EnergyIndex\" >= 0 AND \"EnergyIndex\" <= {SitePropertyProfileLimits.MaxEnergyIndex})");
        });

        // E29/F02/US01/T01 (NF-125): what the host decided about notifying the guests of a Swiss property, stored as the number of the enum;
        // null = follow the regime of the place (every row that existed before the column, and the default). The service validates it; the
        // constraint keeps any other writer honest.
        entity.ToTable(t => t.HasCheckConstraint(
            "CK_Properties_GuestNotificationOverride",
            $"\"GuestNotificationOverride\" IS NULL OR \"GuestNotificationOverride\" IN ({(int)Casazen.Core.Enums.GuestNotificationOverride.Required}, "
            + $"{(int)Casazen.Core.Enums.GuestNotificationOverride.NotRequired})"));

        // Indexes
        entity.HasIndex(p => p.OwnerId);

        // SU-04: the comune chosen from the official list; the region follows it.
        entity.HasIndex(p => p.ComuneIstatCode);

        // PM-01: the lists of the host areas filter the properties of an org by rental mode (GET /api/properties?mode=), and
        // the jobs and the public site read the short-rent ones; the column is stored as an integer (append only) and the
        // existing rows keep the default 0 = Short (migration AddPropertyRentalMode, docs/runbooks/property-rental-mode.md).
        entity.HasIndex(p => new { p.OrgId, p.RentalMode });

        // Unique address PER ORG and per unit (PC-06, A2-19). Before, the index was global: a host with two apartments in
        // the same building could not create the second, and a host whose address was already used by ANOTHER org got a
        // 409 that revealed a datum of that tenant. "AddressKey" is a stored generated column (shadow property): street,
        // city, postal code and unit with case and runs of spaces ignored, so "Via Roma  1" and "via roma 1" are the same
        // address; it is computed by the database, so every writer of the table is covered and the application never
        // builds the key (never check-then-insert: the loser of two parallel creates gets 23505 on this index).
        // A soft-deleted property (PC-05, IsDeleted) frees its address, so it can be re-created there. A paused one
        // (PC-03, IsPaused) keeps it: pausing is temporary and the property stays the host's.
        entity.Property<string>("AddressKey")
            .HasColumnType("text")
            .HasComputedColumnSql(
                "lower(regexp_replace(btrim(\"Address\"), '\\s+', ' ', 'g')) || '|' || "
                + "lower(regexp_replace(btrim(\"City\"), '\\s+', ' ', 'g')) || '|' || "
                + "lower(btrim(\"PostalCode\")) || '|' || "
                + "lower(regexp_replace(btrim(coalesce(\"Unit\", '')), '\\s+', ' ', 'g'))",
                stored: true);

        entity.HasIndex("OrgId", "AddressKey")
            .IsUnique()
            .HasDatabaseName(PropertyAddress.UniqueIndexName)
            .HasFilter("\"IsActive\" = true AND \"IsDeleted\" = false");

        entity.HasIndex(p => new { p.OrgId, p.Slug })
            .IsUnique()
            // A soft-deleted property (PC-05) frees its slug: SlugExistsInOrgAsync no longer sees it either.
            .HasFilter("\"Slug\" IS NOT NULL AND \"IsDeleted\" = false")
            .HasDatabaseName("UIX_Properties_OrgId_Slug");

        // E22/F01/US02/T01: the door token of the QR code printed for the door is unique in the whole table (the public door page finds the property by
        // it alone), and a property that never asked for the code has none. A soft-deleted property keeps its token: a printed code is never given to another.
        entity.HasIndex(p => p.DoorToken)
            .IsUnique()
            .HasFilter("\"DoorToken\" IS NOT NULL")
            .HasDatabaseName("UIX_Properties_DoorToken");
    }

    internal static void ConfigureOrgIdIndex(EntityTypeBuilder<Property> entity)
    {
        // OrgId indexes on the tenant-scoped tables + Users (AC2/AC9).
        entity.HasIndex(p => p.OrgId);
    }

    internal static void ConfigureOrgIdForeignKey(EntityTypeBuilder<Property> entity)
    {
        // OrgId FK constraints (AC2). Restrict: an Org can never be deleted while it still owns
        // tenant rows. The four tenant tables are required (Guid); User.OrgId is nullable (AC9).
        entity.HasOne(p => p.Org).WithMany().HasForeignKey(p => p.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    internal static void ConfigureMemberChargeProperty(EntityTypeBuilder<Property> entity)
    {
        // The member in charge of a property (AM-03): notified together with the org's administrators. The account can go;
        // the property keeps its history and is simply left without a named person.
        entity.HasOne<User>()
            .WithMany()
            .HasForeignKey(p => p.ResponsibleUserId)
            .OnDelete(DeleteBehavior.SetNull);
        entity.HasIndex(p => p.ResponsibleUserId)
            .HasDatabaseName("IX_Properties_ResponsibleUserId");
    }
}

internal sealed class PricingAdapterConfigConfiguration : IEntityTypeConfiguration<PricingAdapterConfig>
{
    public void Configure(EntityTypeBuilder<PricingAdapterConfig> entity)
    {
        // PricingAdapterConfig → Property (1-to-1)
        entity.HasOne(c => c.Property)
            .WithOne(p => p.PricingAdapterConfig)
            .HasForeignKey<PricingAdapterConfig>(c => c.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(c => new { c.PropertyId, c.IsEnabled });
    }

    internal static void ConfigureOrgIdIndex(EntityTypeBuilder<PricingAdapterConfig> entity)
    {
        entity.HasIndex(c => c.OrgId);
    }

    internal static void ConfigureOrgIdForeignKey(EntityTypeBuilder<PricingAdapterConfig> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(c => c.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PricingHistoryConfiguration : IEntityTypeConfiguration<PricingHistory>
{
    public void Configure(EntityTypeBuilder<PricingHistory> entity)
    {
        // PricingHistory → Property
        entity.HasOne(h => h.Property)
            .WithMany()
            .HasForeignKey(h => h.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(h => new { h.PropertyId, h.AdaptationDate });
    }

    internal static void ConfigureOrgIdIndex(EntityTypeBuilder<PricingHistory> entity)
    {
        entity.HasIndex(h => h.OrgId);
    }

    internal static void ConfigureOrgIdForeignKey(EntityTypeBuilder<PricingHistory> entity)
    {
        entity.HasOne<Org>().WithMany().HasForeignKey(h => h.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PropertyDocumentConfiguration : IEntityTypeConfiguration<PropertyDocument>
{
    public void Configure(EntityTypeBuilder<PropertyDocument> entity)
    {
        // PropertyDocument → Property
        entity.HasOne(d => d.Property)
            .WithMany(p => p.PropertyDocuments)
            .HasForeignKey(d => d.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);

        entity.HasIndex(d => d.PropertyId)
            .HasDatabaseName("IX_PropertyDocuments_PropertyId");

        entity.Property(d => d.DocumentType)
            .HasConversion<string>()
            .HasMaxLength(100);
    }

    internal static void ConfigureOrgIdIndex(EntityTypeBuilder<PropertyDocument> entity)
    {
        // TN-2: child rows that controllers expose carry their parent's OrgId.
        entity.HasIndex(d => d.OrgId);
    }

    internal static void ConfigureOrgIdForeignKey(EntityTypeBuilder<PropertyDocument> entity)
    {
        // TN-2: child rows copy the OrgId of their parent (property or booking) when they are created.
        // Restrict like every other OrgId FK; no navigation, the org is never loaded through a child.
        entity.HasOne<Org>().WithMany().HasForeignKey(d => d.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PropertyFiscalYearConfiguration : IEntityTypeConfiguration<PropertyFiscalYear>
{
    public void Configure(EntityTypeBuilder<PropertyFiscalYear> entity)
    {
        entity.HasOne(y => y.Org).WithMany().HasForeignKey(y => y.OrgId)
            .OnDelete(DeleteBehavior.Restrict);
        entity.HasOne(y => y.Property).WithMany().HasForeignKey(y => y.PropertyId)
            .OnDelete(DeleteBehavior.Cascade);
        entity.HasIndex(y => new { y.PropertyId, y.TaxYear })
            .IsUnique();
        // No unique "one primary per org and year" index any more: the 21% unit is one per taxpayer (CO-18), and one org
        // can manage several taxpayers. FiscalService checks it under the OrgFiscalRegime advisory lock.
        entity.HasIndex(y => new { y.OrgId, y.TaxYear });
        entity.HasIndex(y => y.OrgId);
    }
}
