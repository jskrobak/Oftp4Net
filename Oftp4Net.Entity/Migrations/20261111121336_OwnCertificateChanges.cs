using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oftp4Net.Entity.Migrations
{
    /// <inheritdoc />
    public partial class OwnCertificateChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "KeepPreviousUntil",
                table: "CertificateChanges",
                type: "timestamp without time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplacedCertificateId",
                table: "CertificateChanges",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Target",
                table: "CertificateChanges",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                // The changes so far are all changes of partners.
                defaultValue: "Partner");

            migrationBuilder.CreateIndex(
                name: "IX_CertificateChanges_ReplacedCertificateId",
                table: "CertificateChanges",
                column: "ReplacedCertificateId");

            migrationBuilder.AddForeignKey(
                name: "FK_CertificateChanges_Certificate_ReplacedCertificateId",
                table: "CertificateChanges",
                column: "ReplacedCertificateId",
                principalTable: "Certificate",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CertificateChanges_Certificate_ReplacedCertificateId",
                table: "CertificateChanges");

            migrationBuilder.DropIndex(
                name: "IX_CertificateChanges_ReplacedCertificateId",
                table: "CertificateChanges");

            migrationBuilder.DropColumn(
                name: "KeepPreviousUntil",
                table: "CertificateChanges");

            migrationBuilder.DropColumn(
                name: "ReplacedCertificateId",
                table: "CertificateChanges");

            migrationBuilder.DropColumn(
                name: "Target",
                table: "CertificateChanges");
        }
    }
}
