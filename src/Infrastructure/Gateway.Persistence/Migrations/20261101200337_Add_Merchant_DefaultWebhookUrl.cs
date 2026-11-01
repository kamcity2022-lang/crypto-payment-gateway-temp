using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gateway.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Add_Merchant_DefaultWebhookUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DefaultWebhookUrl",
                table: "Merchants",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DefaultWebhookUrl",
                table: "Merchants");
        }
    }
}
