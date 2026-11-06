using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gateway.Persistence.Migrations
{
    public partial class AddMerchantTeamInvitationsAndDisable : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDisabled", table: "AspNetUsers",
                type: "boolean", nullable: false, defaultValue: false);
            migrationBuilder.AddColumn<string>(
                name: "InvitedEmail", table: "MerchantClaimTokens",
                type: "text", nullable: true);
            migrationBuilder.AddColumn<string>(
                name: "InvitedRole", table: "MerchantClaimTokens",
                type: "text", nullable: true);
            migrationBuilder.CreateIndex(
                name: "IX_MerchantClaimTokens_MerchantId_InvitedEmail_IsUsed",
                table: "MerchantClaimTokens",
                columns: new[] { "MerchantId", "InvitedEmail", "IsUsed" });
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_MerchantClaimTokens_MerchantId_InvitedEmail_IsUsed",
                table: "MerchantClaimTokens");
            migrationBuilder.DropColumn(name: "InvitedRole", table: "MerchantClaimTokens");
            migrationBuilder.DropColumn(name: "InvitedEmail", table: "MerchantClaimTokens");
            migrationBuilder.DropColumn(name: "IsDisabled", table: "AspNetUsers");
        }
    }
}
