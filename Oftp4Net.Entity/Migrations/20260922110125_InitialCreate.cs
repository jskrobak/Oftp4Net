using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Oftp4Net.Entity.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "__DataSeed",
                columns: table => new
                {
                    ProfileName = table.Column<string>(type: "character varying(250)", maxLength: 250, nullable: false),
                    Version = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DataSeed", x => x.ProfileName);
                });

            migrationBuilder.CreateTable(
                name: "Certificate",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Base64Data = table.Column<string>(type: "text", nullable: true),
                    ValidFrom = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    ValidTo = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    HasPrivateKey = table.Column<bool>(type: "boolean", nullable: false),
                    Password = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Certificate", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "GlobalSettings",
                columns: table => new
                {
                    Name = table.Column<string>(type: "text", nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GlobalSettings", x => x.Name);
                });

            migrationBuilder.CreateTable(
                name: "Identities",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SFID = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    SSID = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    Password = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Identities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    MustChangePassword = table.Column<bool>(type: "boolean", nullable: false),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastLogin = table.Column<DateTime>(type: "timestamp without time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Partners",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Host = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    UseTls = table.Column<bool>(type: "boolean", nullable: false),
                    Tls = table.Column<int>(type: "integer", nullable: false),
                    TrustedCertificateId = table.Column<int>(type: "integer", nullable: true),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    SFID = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    SSID = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    Password = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Partners", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Partners_Certificate_TrustedCertificateId",
                        column: x => x.TrustedCertificateId,
                        principalTable: "Certificate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Listeners",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    UseTls = table.Column<bool>(type: "boolean", nullable: false),
                    Tls = table.Column<int>(type: "integer", nullable: false),
                    ListenIPAddress = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    CertificateId = table.Column<int>(type: "integer", nullable: true),
                    IdentityId = table.Column<int>(type: "integer", nullable: true),
                    RequireClientCertificate = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Listeners", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Listeners_Certificate_CertificateId",
                        column: x => x.CertificateId,
                        principalTable: "Certificate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Listeners_Identities_IdentityId",
                        column: x => x.IdentityId,
                        principalTable: "Identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ReceivedFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    PartnerId = table.Column<int>(type: "integer", nullable: true),
                    VirtualFileName = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    FileDate = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    FileTime = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    UserData = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Originator = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    Destination = table.Column<string>(type: "character varying(25)", maxLength: 25, nullable: false),
                    Description = table.Column<string>(type: "character varying(999)", maxLength: 999, nullable: true),
                    FilePath = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ConfirmedDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceivedFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReceivedFiles_Partners_PartnerId",
                        column: x => x.PartnerId,
                        principalTable: "Partners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SendQueueItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PartnerId = table.Column<int>(type: "integer", nullable: false),
                    IdentityId = table.Column<int>(type: "integer", nullable: false),
                    VirtualFileName = table.Column<string>(type: "character varying(26)", maxLength: 26, nullable: false),
                    FilePath = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Description = table.Column<string>(type: "character varying(999)", maxLength: 999, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    FileDate = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    FileTime = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    SentDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    DeliveredDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp without time zone", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    LastErrorDate = table.Column<DateTime>(type: "timestamp without time zone", nullable: true),
                    RetryCount = table.Column<int>(type: "integer", nullable: false),
                    NextRetry = table.Column<DateTime>(type: "timestamp without time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SendQueueItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SendQueueItems_Identities_IdentityId",
                        column: x => x.IdentityId,
                        principalTable: "Identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SendQueueItems_Partners_PartnerId",
                        column: x => x.PartnerId,
                        principalTable: "Partners",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Listeners_CertificateId",
                table: "Listeners",
                column: "CertificateId");

            migrationBuilder.CreateIndex(
                name: "IX_Listeners_IdentityId",
                table: "Listeners",
                column: "IdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_Partners_TrustedCertificateId",
                table: "Partners",
                column: "TrustedCertificateId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedFiles_PartnerId",
                table: "ReceivedFiles",
                column: "PartnerId");

            migrationBuilder.CreateIndex(
                name: "IX_ReceivedFiles_VirtualFileName_FileDate_FileTime",
                table: "ReceivedFiles",
                columns: new[] { "VirtualFileName", "FileDate", "FileTime" });

            migrationBuilder.CreateIndex(
                name: "IX_SendQueueItems_IdentityId",
                table: "SendQueueItems",
                column: "IdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_SendQueueItems_PartnerId",
                table: "SendQueueItems",
                column: "PartnerId");

            migrationBuilder.CreateIndex(
                name: "IX_SendQueueItems_VirtualFileName_FileDate_FileTime",
                table: "SendQueueItems",
                columns: new[] { "VirtualFileName", "FileDate", "FileTime" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserName",
                table: "Users",
                column: "UserName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "__DataSeed");

            migrationBuilder.DropTable(
                name: "GlobalSettings");

            migrationBuilder.DropTable(
                name: "Listeners");

            migrationBuilder.DropTable(
                name: "ReceivedFiles");

            migrationBuilder.DropTable(
                name: "SendQueueItems");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "Identities");

            migrationBuilder.DropTable(
                name: "Partners");

            migrationBuilder.DropTable(
                name: "Certificate");
        }
    }
}
