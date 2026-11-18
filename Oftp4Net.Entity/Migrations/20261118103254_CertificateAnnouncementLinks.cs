using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Oftp4Net.Entity.Migrations
{
    /// <inheritdoc />
    public partial class CertificateAnnouncementLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CertificateAnnouncementLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CertificateChangeId = table.Column<int>(type: "integer", nullable: false),
                    RecipientKey = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    PartnerIds = table.Column<string>(type: "jsonb", nullable: false),
                    Token = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    RespondedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RemoteAddress = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Takeover = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    PlannedAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    TlsTrust = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    FiltersByIp = table.Column<bool>(type: "boolean", nullable: true),
                    TheirIps = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ContactName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ContactEmail = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ContactPhone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ContactTakenOverAt = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CertificateAnnouncementLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CertificateAnnouncementLinks_CertificateChanges_Certificate~",
                        column: x => x.CertificateChangeId,
                        principalTable: "CertificateChanges",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CertificateAnnouncementLinks_CertificateChangeId_RecipientK~",
                table: "CertificateAnnouncementLinks",
                columns: new[] { "CertificateChangeId", "RecipientKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CertificateAnnouncementLinks_Token",
                table: "CertificateAnnouncementLinks",
                column: "Token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CertificateAnnouncementLinks");
        }
    }
}
