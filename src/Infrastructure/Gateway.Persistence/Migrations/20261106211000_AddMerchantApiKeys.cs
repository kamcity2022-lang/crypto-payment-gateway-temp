using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gateway.Persistence.Migrations
{
    public partial class AddMerchantApiKeys : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "MerchantApiKeys",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ApiKeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EncryptedSecret = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantApiKeys", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MerchantApiKeys_Merchants_MerchantId",
                        column: x => x.MerchantId,
                        principalTable: "Merchants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MerchantApiKeys_ApiKeyHash",
                table: "MerchantApiKeys", column: "ApiKeyHash", unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MerchantApiKeys_MerchantId",
                table: "MerchantApiKeys", column: "MerchantId");

            // Preserve existing deployed credentials. The old Merchant fields
            // remain for schema compatibility but are never consulted by HMAC.
            migrationBuilder.Sql(@"
                INSERT INTO ""MerchantApiKeys""
                    (""Id"", ""MerchantId"", ""Name"", ""ApiKeyHash"", ""EncryptedSecret"", ""CreatedAtUtc"", ""RevokedAtUtc"")
                SELECT gen_random_uuid(), ""Id"", 'Primary (migrated)',
                       ""ApiKeyHash"", ""EncryptedApiKeySecret"", ""CreatedAtUtc"", NULL
                FROM ""Merchants""
                WHERE LENGTH(""ApiKeyHash"") = 64
                  AND LENGTH(""EncryptedApiKeySecret"") > 0
            ");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "MerchantApiKeys");
        }
    }
}
