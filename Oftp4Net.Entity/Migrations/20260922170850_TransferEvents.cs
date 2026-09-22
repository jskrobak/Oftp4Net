using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Oftp4Net.Entity.Migrations
{
    /// <inheritdoc />
    public partial class TransferEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TransferEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Timestamp = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    Category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    PartnerId = table.Column<int>(type: "integer", nullable: true),
                    PartnerName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    RemoteEndPoint = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    VirtualFileName = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: true),
                    FileDate = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    FileTime = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    FileSize = table.Column<long>(type: "bigint", nullable: true),
                    SendQueueItemId = table.Column<int>(type: "integer", nullable: true),
                    ReceivedFileId = table.Column<int>(type: "integer", nullable: true),
                    Message = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    Details = table.Column<string>(type: "text", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransferEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TransferEvents_Category_IsArchived_Timestamp",
                table: "TransferEvents",
                columns: new[] { "Category", "IsArchived", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IX_TransferEvents_IsArchived_Timestamp",
                table: "TransferEvents",
                columns: new[] { "IsArchived", "Timestamp" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TransferEvents");
        }
    }
}
