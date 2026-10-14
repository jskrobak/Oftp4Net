using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Oftp4Net.Entity.Migrations
{
    /// <inheritdoc />
    public partial class CertificateChangesAndTokenScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Scopes",
                table: "ApiTokens",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Existing tokens keep what they could do: Outbox | Inbox | Certificates.
            migrationBuilder.Sql("""UPDATE "ApiTokens" SET "Scopes" = 7;""");

            migrationBuilder.CreateTable(
                name: "CertificateChanges",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PartnerId = table.Column<int>(type: "integer", nullable: true),
                    PartnerName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CertificateId = table.Column<int>(type: "integer", nullable: true),
                    Usage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActivateAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CertificateChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CertificateChanges_Certificate_CertificateId",
                        column: x => x.CertificateId,
                        principalTable: "Certificate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_CertificateChanges_Partners_PartnerId",
                        column: x => x.PartnerId,
                        principalTable: "Partners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CertificateChanges_CertificateId",
                table: "CertificateChanges",
                column: "CertificateId");

            migrationBuilder.CreateIndex(
                name: "IX_CertificateChanges_PartnerId",
                table: "CertificateChanges",
                column: "PartnerId");

            migrationBuilder.CreateIndex(
                name: "IX_CertificateChanges_Status_ActivateAt",
                table: "CertificateChanges",
                columns: new[] { "Status", "ActivateAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CertificateChanges");

            migrationBuilder.DropColumn(
                name: "Scopes",
                table: "ApiTokens");
        }
    }
}
