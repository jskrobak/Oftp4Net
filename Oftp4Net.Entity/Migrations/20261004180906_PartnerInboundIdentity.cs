using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oftp4Net.Entity.Migrations
{
    /// <inheritdoc />
    public partial class PartnerInboundIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InboundIdentityId",
                table: "Partners",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Partners_InboundIdentityId",
                table: "Partners",
                column: "InboundIdentityId");

            migrationBuilder.AddForeignKey(
                name: "FK_Partners_Identities_InboundIdentityId",
                table: "Partners",
                column: "InboundIdentityId",
                principalTable: "Identities",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Partners_Identities_InboundIdentityId",
                table: "Partners");

            migrationBuilder.DropIndex(
                name: "IX_Partners_InboundIdentityId",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "InboundIdentityId",
                table: "Partners");
        }
    }
}
