CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;
CREATE TABLE "CancellationPolicies" (
    "Id" uuid NOT NULL,
    "Name" character varying(50) NOT NULL,
    "Description" character varying(500) NOT NULL,
    "FullRefundHours" integer NOT NULL,
    "PartialRefundPercent" numeric(18,2) NOT NULL,
    "PartialRefundHours" integer NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_CancellationPolicies" PRIMARY KEY ("Id")
);

CREATE TABLE "Guests" (
    "Id" uuid NOT NULL,
    "FirstName" character varying(100) NOT NULL,
    "LastName" character varying(100) NOT NULL,
    "Email" character varying(255) NOT NULL,
    "PhoneNumber" character varying(20) NOT NULL,
    "Address" character varying(500) NOT NULL,
    "City" character varying(50) NOT NULL,
    "PostalCode" character varying(10) NOT NULL,
    "Country" character varying(100) NOT NULL,
    "DateOfBirth" timestamp with time zone,
    "PlaceOfBirth" character varying(100) NOT NULL,
    "Nationality" character varying(100) NOT NULL,
    "DocumentType" integer,
    "DocumentNumber" character varying(50) NOT NULL,
    "DocumentIssueDate" timestamp with time zone,
    "DocumentExpiryDate" timestamp with time zone,
    "DocumentIssuingCountry" character varying(100) NOT NULL,
    "DataProcessingConsentDate" timestamp with time zone,
    "ConsentIpAddress" character varying(50) NOT NULL,
    "DataRetentionExpiryDate" timestamp with time zone,
    "ErasureRequested" boolean NOT NULL,
    "ErasureRequestedDate" timestamp with time zone,
    "DataAnonymizedDate" timestamp with time zone,
    "Notes" character varying(1000) NOT NULL,
    "Gender" integer,
    "ConsentDate" timestamp with time zone,
    "ConsentVersion" character varying(50) NOT NULL,
    "MarketingConsent" boolean NOT NULL,
    "MarketingConsentDate" timestamp with time zone,
    "DataRetentionUntil" timestamp with time zone NOT NULL,
    "DataProcessingPurpose" character varying(200) NOT NULL,
    "IsDeleted" boolean NOT NULL,
    "DeletedAt" timestamp with time zone,
    "DeletionReason" character varying(500) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_Guests" PRIMARY KEY ("Id")
);

CREATE TABLE "TaxRates" (
    "Id" uuid NOT NULL,
    "Region" character varying(100) NOT NULL,
    "City" character varying(100) NOT NULL,
    "RatePerNight" numeric(18,2) NOT NULL,
    "MaxNights" integer,
    "EffectiveFrom" timestamp with time zone NOT NULL,
    "EffectiveTo" timestamp with time zone,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_TaxRates" PRIMARY KEY ("Id")
);

CREATE TABLE "TouristTaxRates" (
    "Id" uuid NOT NULL,
    "City" character varying(100) NOT NULL,
    "RegionCode" character varying(10) NOT NULL,
    "RatePerPersonPerNight" numeric(18,2) NOT NULL,
    "MaxNights" integer,
    "MinimumAge" integer NOT NULL,
    "IsActive" boolean NOT NULL,
    "EffectiveFrom" timestamp with time zone NOT NULL,
    "EffectiveTo" timestamp with time zone,
    "Notes" character varying(500) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_TouristTaxRates" PRIMARY KEY ("Id")
);

CREATE TABLE "Users" (
    "Id" character varying(255) NOT NULL,
    "Email" character varying(255) NOT NULL,
    "FirstName" character varying(100) NOT NULL,
    "LastName" character varying(100) NOT NULL,
    "PhoneNumber" character varying(20) NOT NULL,
    "Role" integer NOT NULL,
    "IsActive" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_Users" PRIMARY KEY ("Id")
);

CREATE TABLE "Properties" (
    "Id" uuid NOT NULL,
    "OwnerId" character varying(255) NOT NULL,
    "Name" character varying(100) NOT NULL,
    "Description" character varying(2000) NOT NULL,
    "Address" character varying(500) NOT NULL,
    "City" character varying(50) NOT NULL,
    "PostalCode" character varying(10) NOT NULL,
    "Latitude" numeric(18,2) NOT NULL,
    "Longitude" numeric(18,2) NOT NULL,
    "Bedrooms" integer NOT NULL,
    "Bathrooms" integer NOT NULL,
    "MaxGuests" integer NOT NULL,
    "NightlyRate" numeric(18,2) NOT NULL,
    "CleaningFee" numeric(18,2) NOT NULL,
    "DamageDeposit" numeric(18,2) NOT NULL,
    "Amenities" integer[] NOT NULL,
    "PhotoUrls" text[] NOT NULL,
    "HouseRules" character varying(1000) NOT NULL,
    "CinCode" character varying(25),
    "Timezone" character varying(50) NOT NULL,
    "CancellationPolicyId" uuid,
    "IsActive" boolean NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_Properties" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Properties_CancellationPolicies_CancellationPolicyId" FOREIGN KEY ("CancellationPolicyId") REFERENCES "CancellationPolicies" ("Id")
);

CREATE TABLE "Bookings" (
    "Id" uuid NOT NULL,
    "PropertyId" uuid NOT NULL,
    "GuestId" uuid NOT NULL,
    "CheckInDate" timestamp with time zone NOT NULL,
    "CheckOutDate" timestamp with time zone NOT NULL,
    "NumberOfGuests" integer NOT NULL,
    "Status" integer NOT NULL,
    "Source" integer NOT NULL,
    "ExternalId" character varying(500) NOT NULL,
    "BasePrice" numeric(18,2) NOT NULL,
    "TouristTax" numeric(18,2) NOT NULL,
    "TotalPrice" numeric(18,2) NOT NULL,
    "TouristTaxAmount" numeric(18,2) NOT NULL,
    "NumberOfAdults" integer NOT NULL,
    "NumberOfChildren" integer NOT NULL,
    "SpecialRequests" character varying(1000) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_Bookings" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Bookings_Guests_GuestId" FOREIGN KEY ("GuestId") REFERENCES "Guests" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_Bookings_Properties_PropertyId" FOREIGN KEY ("PropertyId") REFERENCES "Properties" ("Id") ON DELETE CASCADE
);

CREATE TABLE "LeaseContracts" (
    "Id" uuid NOT NULL,
    "PropertyId" uuid NOT NULL,
    "Status" integer NOT NULL,
    "FiscalRegime" integer NOT NULL,
    "StartDate" timestamp with time zone NOT NULL,
    "EndDate" timestamp with time zone NOT NULL,
    "MonthlyRent" numeric(18,2) NOT NULL,
    "RegistrationDeadline" timestamp with time zone NOT NULL,
    "ExternalSigningSessionId" character varying(500),
    "SignedPdfStoragePath" character varying(1000),
    "ErasureRequested" boolean NOT NULL,
    "DataRetentionUntil" timestamp with time zone NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_LeaseContracts" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_LeaseContracts_Properties_PropertyId" FOREIGN KEY ("PropertyId") REFERENCES "Properties" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "OtaIntegrations" (
    "Id" uuid NOT NULL,
    "PropertyId" uuid NOT NULL,
    "Platform" character varying(50) NOT NULL,
    "ExternalPropertyId" character varying(500) NOT NULL,
    "ApiKey" character varying(1000) NOT NULL,
    "ApiSecret" character varying(1000) NOT NULL,
    "IsActive" boolean NOT NULL,
    "SyncEnabled" boolean NOT NULL,
    "LastSyncAt" timestamp with time zone NOT NULL,
    "SyncStatus" character varying(50),
    "LastSyncError" character varying(2000),
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_OtaIntegrations" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_OtaIntegrations_Properties_PropertyId" FOREIGN KEY ("PropertyId") REFERENCES "Properties" ("Id") ON DELETE CASCADE
);

CREATE TABLE "OtaSyncLogs" (
    "Id" uuid NOT NULL,
    "PropertyId" uuid NOT NULL,
    "Platform" character varying(50) NOT NULL,
    "SyncStartedAt" timestamp with time zone NOT NULL,
    "SyncCompletedAt" timestamp with time zone,
    "Success" boolean NOT NULL,
    "BookingsCreated" integer NOT NULL,
    "BookingsUpdated" integer NOT NULL,
    "BookingsCancelled" integer NOT NULL,
    "ErrorMessage" character varying(2000) NOT NULL,
    "Details" character varying(5000) NOT NULL,
    CONSTRAINT "PK_OtaSyncLogs" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_OtaSyncLogs_Properties_PropertyId" FOREIGN KEY ("PropertyId") REFERENCES "Properties" ("Id") ON DELETE CASCADE
);

CREATE TABLE "PricingAdapterConfigs" (
    "Id" uuid NOT NULL,
    "PropertyId" uuid NOT NULL,
    "IsEnabled" boolean NOT NULL,
    "AdaptationFrequency" character varying(50) NOT NULL,
    "IncludeSeasonality" boolean NOT NULL,
    "IncludePublicHolidays" boolean NOT NULL,
    "LastAdaptedAt" timestamp with time zone,
    "NextScheduledRunAt" timestamp with time zone,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_PricingAdapterConfigs" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_PricingAdapterConfigs_Properties_PropertyId" FOREIGN KEY ("PropertyId") REFERENCES "Properties" ("Id") ON DELETE CASCADE
);

CREATE TABLE "PricingHistories" (
    "Id" uuid NOT NULL,
    "PropertyId" uuid NOT NULL,
    "AdaptationDate" timestamp with time zone NOT NULL,
    "PreviousPrice" numeric(18,2) NOT NULL,
    "NewPrice" numeric(18,2) NOT NULL,
    "ChangeReason" character varying(500) NOT NULL,
    "AiConfidence" numeric(5,4) NOT NULL,
    "OtasSynced" character varying(2000) NOT NULL,
    "SyncStatus" character varying(50) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_PricingHistories" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_PricingHistories_Properties_PropertyId" FOREIGN KEY ("PropertyId") REFERENCES "Properties" ("Id") ON DELETE CASCADE
);

CREATE TABLE "PropertyDocuments" (
    "Id" uuid NOT NULL,
    "PropertyId" uuid NOT NULL,
    "FileName" character varying(500) NOT NULL,
    "StorageUrl" character varying(2000) NOT NULL,
    "DocumentType" character varying(100) NOT NULL,
    "UploadedBy" character varying(255) NOT NULL,
    "UploadedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_PropertyDocuments" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_PropertyDocuments_Properties_PropertyId" FOREIGN KEY ("PropertyId") REFERENCES "Properties" ("Id") ON DELETE CASCADE
);

CREATE TABLE "AlloggiatiWebReports" (
    "Id" uuid NOT NULL,
    "BookingId" uuid NOT NULL,
    "GuestId" uuid NOT NULL,
    "ReportedAt" timestamp with time zone NOT NULL,
    "Status" integer NOT NULL,
    "ConfirmationNumber" character varying(100),
    "ErrorMessage" character varying(2000),
    "RetryCount" integer NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_AlloggiatiWebReports" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_AlloggiatiWebReports_Bookings_BookingId" FOREIGN KEY ("BookingId") REFERENCES "Bookings" ("Id") ON DELETE RESTRICT,
    CONSTRAINT "FK_AlloggiatiWebReports_Guests_GuestId" FOREIGN KEY ("GuestId") REFERENCES "Guests" ("Id") ON DELETE RESTRICT
);

CREATE TABLE "Payments" (
    "Id" uuid NOT NULL,
    "BookingId" uuid NOT NULL,
    "Amount" numeric(18,2) NOT NULL,
    "RefundedAmount" numeric(18,2) NOT NULL,
    "Status" integer NOT NULL,
    "Method" integer NOT NULL,
    "TransactionId" character varying(500) NOT NULL,
    "Description" character varying(1000) NOT NULL,
    "StripePaymentIntentId" character varying(255),
    "ProcessedAt" timestamp with time zone,
    "CreatedAt" timestamp with time zone NOT NULL,
    "UpdatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_Payments" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Payments_Bookings_BookingId" FOREIGN KEY ("BookingId") REFERENCES "Bookings" ("Id") ON DELETE CASCADE
);

CREATE TABLE "LeaseEvents" (
    "Id" uuid NOT NULL,
    "LeaseContractId" uuid NOT NULL,
    "EventType" integer NOT NULL,
    "OccurredAt" timestamp with time zone NOT NULL,
    "Payload" text,
    CONSTRAINT "PK_LeaseEvents" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_LeaseEvents_LeaseContracts_LeaseContractId" FOREIGN KEY ("LeaseContractId") REFERENCES "LeaseContracts" ("Id") ON DELETE CASCADE
);

CREATE TABLE "LeaseRegistrations" (
    "Id" uuid NOT NULL,
    "LeaseContractId" uuid NOT NULL,
    "Status" integer NOT NULL,
    "ExternalRegistrationId" character varying(200),
    "RegistrationCode" character varying(100),
    "ReceiptStoragePath" character varying(1000),
    "SubmittedAt" timestamp with time zone,
    "ConfirmedAt" timestamp with time zone,
    CONSTRAINT "PK_LeaseRegistrations" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_LeaseRegistrations_LeaseContracts_LeaseContractId" FOREIGN KEY ("LeaseContractId") REFERENCES "LeaseContracts" ("Id") ON DELETE CASCADE
);

CREATE TABLE "Parties" (
    "Id" uuid NOT NULL,
    "LeaseContractId" uuid NOT NULL,
    "Role" integer NOT NULL,
    "FirstName" character varying(100) NOT NULL,
    "LastName" character varying(100) NOT NULL,
    "FiscalCode" character varying(16) NOT NULL,
    "Citizenship" character varying(2) NOT NULL,
    "ContactEmail" character varying(255) NOT NULL,
    "IsExtraEU" boolean NOT NULL,
    CONSTRAINT "PK_Parties" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Parties_LeaseContracts_LeaseContractId" FOREIGN KEY ("LeaseContractId") REFERENCES "LeaseContracts" ("Id") ON DELETE CASCADE
);

CREATE INDEX "IX_AlloggiatiWebReports_BookingId" ON "AlloggiatiWebReports" ("BookingId");

CREATE INDEX "IX_AlloggiatiWebReports_GuestId" ON "AlloggiatiWebReports" ("GuestId");

CREATE INDEX "IX_Bookings_CheckInDate" ON "Bookings" ("CheckInDate");

CREATE INDEX "IX_Bookings_GuestId" ON "Bookings" ("GuestId");

CREATE INDEX "IX_Bookings_PropertyId" ON "Bookings" ("PropertyId");

CREATE INDEX "IX_Bookings_Status" ON "Bookings" ("Status");

CREATE INDEX "IX_Guests_Email" ON "Guests" ("Email");

CREATE INDEX "IX_LeaseContracts_PropertyId" ON "LeaseContracts" ("PropertyId");

CREATE INDEX "IX_LeaseContracts_Status" ON "LeaseContracts" ("Status");

CREATE INDEX "IX_LeaseEvents_LeaseContractId_OccurredAt" ON "LeaseEvents" ("LeaseContractId", "OccurredAt");

CREATE UNIQUE INDEX "IX_LeaseRegistrations_LeaseContractId" ON "LeaseRegistrations" ("LeaseContractId");

CREATE INDEX "IX_OtaIntegrations_PropertyId" ON "OtaIntegrations" ("PropertyId");

CREATE INDEX "IX_OtaSyncLogs_PropertyId" ON "OtaSyncLogs" ("PropertyId");

CREATE INDEX "IX_Parties_LeaseContractId_Role" ON "Parties" ("LeaseContractId", "Role");

CREATE INDEX "IX_Payments_BookingId" ON "Payments" ("BookingId");

CREATE UNIQUE INDEX "IX_PricingAdapterConfigs_PropertyId" ON "PricingAdapterConfigs" ("PropertyId");

CREATE INDEX "IX_PricingAdapterConfigs_PropertyId_IsEnabled" ON "PricingAdapterConfigs" ("PropertyId", "IsEnabled");

CREATE INDEX "IX_PricingHistories_PropertyId_AdaptationDate" ON "PricingHistories" ("PropertyId", "AdaptationDate");

CREATE UNIQUE INDEX "IX_Properties_Address_City_PostalCode_IsActive" ON "Properties" ("Address", "City", "PostalCode", "IsActive") WHERE "IsActive" = true;

CREATE INDEX "IX_Properties_CancellationPolicyId" ON "Properties" ("CancellationPolicyId");

CREATE INDEX "IX_Properties_OwnerId" ON "Properties" ("OwnerId");

CREATE INDEX "IX_PropertyDocuments_PropertyId" ON "PropertyDocuments" ("PropertyId");

CREATE INDEX "IX_TouristTaxRates_City" ON "TouristTaxRates" ("City");

CREATE INDEX "IX_TouristTaxRates_City_IsActive_EffectiveFrom" ON "TouristTaxRates" ("City", "IsActive", "EffectiveFrom");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260603080357_InitialCreate', '10.0.1');

COMMIT;

START TRANSACTION;
ALTER TABLE "Users" ADD "LastUsedContextKey" character varying(64);

CREATE TABLE "AppContexts" (
    "Key" character varying(64) NOT NULL,
    "DisplayName" character varying(128) NOT NULL,
    CONSTRAINT "PK_AppContexts" PRIMARY KEY ("Key")
);

CREATE TABLE "Roles" (
    "Id" integer GENERATED BY DEFAULT AS IDENTITY,
    "ContextKey" character varying(64) NOT NULL,
    "RoleKey" character varying(64) NOT NULL,
    CONSTRAINT "PK_Roles" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_Roles_AppContexts_ContextKey" FOREIGN KEY ("ContextKey") REFERENCES "AppContexts" ("Key") ON DELETE CASCADE
);

CREATE TABLE "RolePermissions" (
    "RoleId" integer NOT NULL,
    "PermissionKey" character varying(128) NOT NULL,
    CONSTRAINT "PK_RolePermissions" PRIMARY KEY ("RoleId", "PermissionKey"),
    CONSTRAINT "FK_RolePermissions_Roles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "Roles" ("Id") ON DELETE CASCADE
);

CREATE TABLE "UserContextMemberships" (
    "Id" uuid NOT NULL,
    "UserId" character varying(255) NOT NULL,
    "ContextKey" character varying(64) NOT NULL,
    "RoleId" integer NOT NULL,
    CONSTRAINT "PK_UserContextMemberships" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_UserContextMemberships_AppContexts_ContextKey" FOREIGN KEY ("ContextKey") REFERENCES "AppContexts" ("Key") ON DELETE CASCADE,
    CONSTRAINT "FK_UserContextMemberships_Roles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "Roles" ("Id") ON DELETE CASCADE,
    CONSTRAINT "FK_UserContextMemberships_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
);

INSERT INTO "AppContexts" ("Key", "DisplayName")
VALUES ('admin', 'Amministrazione');
INSERT INTO "AppContexts" ("Key", "DisplayName")
VALUES ('long-rent', 'Affitti lungo termine');
INSERT INTO "AppContexts" ("Key", "DisplayName")
VALUES ('short-rent', 'Affitti brevi');

INSERT INTO "Roles" ("Id", "ContextKey", "RoleKey")
VALUES (1, 'short-rent', 'property_owner');
INSERT INTO "Roles" ("Id", "ContextKey", "RoleKey")
VALUES (2, 'long-rent', 'long_term_landlord');
INSERT INTO "Roles" ("Id", "ContextKey", "RoleKey")
VALUES (3, 'admin', 'platform_admin');

INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('booking.read', 1);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('booking.write', 1);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('guest.read', 1);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('guest.write', 1);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('ota.read', 1);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('ota.write', 1);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('payment.read', 1);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('payment.write', 1);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('property.read', 1);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('property.write', 1);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('lease.create', 2);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('lease.read', 2);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('lease.register', 2);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('lease.sign', 2);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('admin.cin.read', 3);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('admin.jobs.read', 3);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('admin.stats.read', 3);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('admin.tax.manage', 3);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('admin.users.manage', 3);
INSERT INTO "RolePermissions" ("PermissionKey", "RoleId")
VALUES ('admin.users.read', 3);

INSERT INTO "UserContextMemberships" ("Id", "UserId", "ContextKey", "RoleId")
SELECT
    ('00000000-0000-0000-0000-' || substring(md5(u."Id" || ':short-rent') from 1 for 12))::uuid,
    u."Id",
    'short-rent',
    1
FROM "Users" u
WHERE u."Role" = 1
ON CONFLICT ("UserId", "ContextKey") DO NOTHING;

INSERT INTO "UserContextMemberships" ("Id", "UserId", "ContextKey", "RoleId")
SELECT
    ('00000000-0000-0000-0000-' || substring(md5(u."Id" || ':long-rent') from 1 for 12))::uuid,
    u."Id",
    'long-rent',
    2
FROM "Users" u
WHERE u."Role" = 5
ON CONFLICT ("UserId", "ContextKey") DO NOTHING;

INSERT INTO "UserContextMemberships" ("Id", "UserId", "ContextKey", "RoleId")
SELECT
    ('00000000-0000-0000-0000-' || substring(md5(u."Id" || ':admin') from 1 for 12))::uuid,
    u."Id",
    'admin',
    3
FROM "Users" u
WHERE u."Role" = 0
ON CONFLICT ("UserId", "ContextKey") DO NOTHING;

CREATE UNIQUE INDEX "IX_Roles_ContextKey_RoleKey" ON "Roles" ("ContextKey", "RoleKey");

CREATE INDEX "IX_UserContextMemberships_ContextKey" ON "UserContextMemberships" ("ContextKey");

CREATE INDEX "IX_UserContextMemberships_RoleId" ON "UserContextMemberships" ("RoleId");

CREATE UNIQUE INDEX "IX_UserContextMemberships_UserId_ContextKey" ON "UserContextMemberships" ("UserId", "ContextKey");

SELECT setval(
    pg_get_serial_sequence('"Roles"', 'Id'),
    GREATEST(
        (SELECT MAX("Id") FROM "Roles") + 1,
        nextval(pg_get_serial_sequence('"Roles"', 'Id'))),
    false);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260604193911_AddContextAuthorization', '10.0.1');

COMMIT;

