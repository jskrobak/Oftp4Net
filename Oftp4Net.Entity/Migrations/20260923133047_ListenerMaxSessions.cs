using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oftp4Net.Entity.Migrations
{
    /// <inheritdoc />
    public partial class ListenerMaxSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxSessions",
                table: "Listeners",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxSessions",
                table: "Listeners");
        }
    }
}
