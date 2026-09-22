using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oftp4Net.Entity.Migrations
{
    /// <inheritdoc />
    public partial class BufferCompressionAndRestart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RestartPosition",
                table: "SendQueueItems",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "RestartedFrom",
                table: "ReceivedFiles",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<bool>(
                name: "BufferCompression",
                table: "Partners",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Restart",
                table: "Partners",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RestartPosition",
                table: "SendQueueItems");

            migrationBuilder.DropColumn(
                name: "RestartedFrom",
                table: "ReceivedFiles");

            migrationBuilder.DropColumn(
                name: "BufferCompression",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "Restart",
                table: "Partners");
        }
    }
}
