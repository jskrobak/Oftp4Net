using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Oftp4Net.Entity.Migrations
{
    /// <inheritdoc />
    public partial class FileLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CipherSuite",
                table: "SendQueueItems",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "ContentHash",
                table: "SendQueueItems",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SignedResponseRequested",
                table: "SendQueueItems",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CipherSuite",
                table: "ReceivedFiles",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Compressed",
                table: "ReceivedFiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<byte[]>(
                name: "ContentHash",
                table: "ReceivedFiles",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecurityLevel",
                table: "ReceivedFiles",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SignedResponseRequested",
                table: "ReceivedFiles",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CompressFiles",
                table: "Partners",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EncryptFiles",
                table: "Partners",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "FileCipherSuite",
                table: "Partners",
                type: "character varying(2)",
                maxLength: 2,
                nullable: false,
                // Existing partners get the AES-256 suite, mandatory for every OFTP2 node.
                defaultValue: "02");

            migrationBuilder.AddColumn<bool>(
                name: "RequestSignedEndResponse",
                table: "Partners",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SecurityCertificateId",
                table: "Partners",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "SignFiles",
                table: "Partners",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Partners_SecurityCertificateId",
                table: "Partners",
                column: "SecurityCertificateId");

            migrationBuilder.AddForeignKey(
                name: "FK_Partners_Certificate_SecurityCertificateId",
                table: "Partners",
                column: "SecurityCertificateId",
                principalTable: "Certificate",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Partners_Certificate_SecurityCertificateId",
                table: "Partners");

            migrationBuilder.DropIndex(
                name: "IX_Partners_SecurityCertificateId",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "CipherSuite",
                table: "SendQueueItems");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "SendQueueItems");

            migrationBuilder.DropColumn(
                name: "SignedResponseRequested",
                table: "SendQueueItems");

            migrationBuilder.DropColumn(
                name: "CipherSuite",
                table: "ReceivedFiles");

            migrationBuilder.DropColumn(
                name: "Compressed",
                table: "ReceivedFiles");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                table: "ReceivedFiles");

            migrationBuilder.DropColumn(
                name: "SecurityLevel",
                table: "ReceivedFiles");

            migrationBuilder.DropColumn(
                name: "SignedResponseRequested",
                table: "ReceivedFiles");

            migrationBuilder.DropColumn(
                name: "CompressFiles",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "EncryptFiles",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "FileCipherSuite",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "RequestSignedEndResponse",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "SecurityCertificateId",
                table: "Partners");

            migrationBuilder.DropColumn(
                name: "SignFiles",
                table: "Partners");
        }
    }
}
