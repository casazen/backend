using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Casazen.Infrastructure.Migrations
{
    /// <summary>
    /// CF-DONE. Data only, no change of the model. A canone concordato draft whose guided form was opened (the signed link leaves the
    /// event <c>ConcordatoFacileLinkGenerated</c>, 27) is the service's draft: an imported draft marked <c>IsConcordatoFacileSource</c>, which
    /// is what «Parto da zero» has made since CF-IMPORT. The drafts made before it (through the generic draft, origin Created, not marked)
    /// were asked for their data like any other draft, and the documents the service delivered were read the way a document of an existing
    /// lease is, which never fills a draft. A draft already reviewed or handed out for the signature in CasaZen is a contract of CasaZen and
    /// is not touched. Nothing else changes (not even <c>UpdatedAt</c>): the lease keeps its parties, its dates and its history.
    /// </summary>
    public partial class BackfillConcordatoFacileSourceDrafts : Migration
    {
        public const string BackfillSql = """
            UPDATE "LeaseContracts" AS l
            SET "IsConcordatoFacileSource" = TRUE, "Origin" = 1
            WHERE l."Status" = 0
              AND l."ContractType" = 1
              AND l."ReviewConfirmedAt" IS NULL
              AND l."SentForSignatureAt" IS NULL
              AND (l."IsConcordatoFacileSource" = FALSE OR l."Origin" <> 1)
              AND EXISTS (SELECT 1 FROM "LeaseEvents" AS e WHERE e."LeaseContractId" = l."Id" AND e."EventType" = 27);
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(BackfillSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A data correction: the draft is not given back its old origin, which was the mistake.
        }
    }
}
