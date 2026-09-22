using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oftp4Net.Entity.Migrations
{
    /// <inheritdoc />
    public partial class PartnerFileEncoding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing partners keep the behaviour they had so far: no conversion, files as stored (Windows-1252).
            migrationBuilder.AddColumn<int>(
                name: "AnsiCodePage",
                table: "Partners",
                type: "integer",
                nullable: false,
                defaultValue: 1252);

            migrationBuilder.AddColumn<bool>(
                name: "ConvertIncomingEbcdicToAnsi",
                table: "Partners",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "EbcdicCodePage",
                table: "Partners",
                type: "integer",
                nullable: false,
                defaultValue: 500);

            migrationBuilder.AddColumn<string>(
                name: "OutgoingEncoding",
                table: "Partners",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "ANSI");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AnsiCodePage",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "ConvertIncomingEbcdicToAnsi",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "EbcdicCodePage",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "OutgoingEncoding",
                table: "Partners");
        }
    }
}
