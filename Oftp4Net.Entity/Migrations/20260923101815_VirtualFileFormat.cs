using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oftp4Net.Entity.Migrations
{
    /// <inheritdoc />
    public partial class VirtualFileFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Format",
                table: "SendQueueItems",
                type: "character varying(1)",
                maxLength: 1,
                nullable: false,
                // Files queued so far are unstructured.
                defaultValue: "U");

            migrationBuilder.AddColumn<int>(
                name: "MaxRecordSize",
                table: "SendQueueItems",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Format",
                table: "ReceivedFiles",
                type: "character varying(1)",
                maxLength: 1,
                nullable: false,
                defaultValue: "U");

            migrationBuilder.AddColumn<int>(
                name: "MaxRecordSize",
                table: "ReceivedFiles",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "Records",
                table: "ReceivedFiles",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Format",
                table: "SendQueueItems");

            migrationBuilder.DropColumn(
                name: "MaxRecordSize",
                table: "SendQueueItems");

            migrationBuilder.DropColumn(
                name: "Format",
                table: "ReceivedFiles");

            migrationBuilder.DropColumn(
                name: "MaxRecordSize",
                table: "ReceivedFiles");

            migrationBuilder.DropColumn(
                name: "Records",
                table: "ReceivedFiles");
        }
    }
}
