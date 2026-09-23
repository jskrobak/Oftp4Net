using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Oftp4Net.Entity.Migrations
{
    /// <inheritdoc />
    public partial class PartnerDataExchange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DestinationSfid",
                table: "SendQueueItems",
                type: "character varying(25)",
                maxLength: 25,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DecidedDate",
                table: "ReceivedFiles",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Address",
                table: "Partners",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "Partners",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CompanyName",
                table: "Partners",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Contacts",
                table: "Partners",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "Partners",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Duns",
                table: "Partners",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InboundDsnPatterns",
                table: "Partners",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "InfoDocumentUrl",
                table: "Partners",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutboundDsnPatterns",
                table: "Partners",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<int>(
                name: "PreviousSecurityCertificateId",
                table: "Partners",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RequireCompressedFiles",
                table: "Partners",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireEncryptedFiles",
                table: "Partners",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "RequireSignedFiles",
                table: "Partners",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "SetupAppliedDate",
                table: "Partners",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SetupDocumentDate",
                table: "Partners",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SetupDocumentId",
                table: "Partners",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubStations",
                table: "Partners",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ZipCode",
                table: "Partners",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PartnerSetupDocuments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    PartnerId = table.Column<int>(type: "integer", nullable: true),
                    Ssid = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    StationName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReceivedFileId = table.Column<int>(type: "integer", nullable: true),
                    Signature = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DocId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ValidFrom = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Messages = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Changes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DecidedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AppliedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartnerSetupDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartnerSetupDocuments_Partners_PartnerId",
                        column: x => x.PartnerId,
                        principalTable: "Partners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PartnerSetupDocuments_ReceivedFiles_ReceivedFileId",
                        column: x => x.ReceivedFileId,
                        principalTable: "ReceivedFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Partners_PreviousSecurityCertificateId",
                table: "Partners",
                column: "PreviousSecurityCertificateId");

            migrationBuilder.CreateIndex(
                name: "IX_PartnerSetupDocuments_PartnerId",
                table: "PartnerSetupDocuments",
                column: "PartnerId");

            migrationBuilder.CreateIndex(
                name: "IX_PartnerSetupDocuments_ReceivedFileId",
                table: "PartnerSetupDocuments",
                column: "ReceivedFileId");

            migrationBuilder.CreateIndex(
                name: "IX_PartnerSetupDocuments_Status_ValidFrom",
                table: "PartnerSetupDocuments",
                columns: new[] { "Status", "ValidFrom" });

            migrationBuilder.AddForeignKey(
                name: "FK_Partners_Certificate_PreviousSecurityCertificateId",
                table: "Partners",
                column: "PreviousSecurityCertificateId",
                principalTable: "Certificate",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Partners_Certificate_PreviousSecurityCertificateId",
                table: "Partners");

            migrationBuilder.DropTable(
                name: "PartnerSetupDocuments");

            migrationBuilder.DropIndex(
                name: "IX_Partners_PreviousSecurityCertificateId",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "DestinationSfid",
                table: "SendQueueItems");

            migrationBuilder.DropColumn(
                name: "DecidedDate",
                table: "ReceivedFiles");

            migrationBuilder.DropColumn(
                name: "Address",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "City",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "CompanyName",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "Contacts",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "Country",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "Duns",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "InboundDsnPatterns",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "InfoDocumentUrl",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "OutboundDsnPatterns",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "PreviousSecurityCertificateId",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "RequireCompressedFiles",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "RequireEncryptedFiles",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "RequireSignedFiles",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "SetupAppliedDate",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "SetupDocumentDate",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "SetupDocumentId",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "SubStations",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "ZipCode",
                table: "Partners");
        }
    }
}
