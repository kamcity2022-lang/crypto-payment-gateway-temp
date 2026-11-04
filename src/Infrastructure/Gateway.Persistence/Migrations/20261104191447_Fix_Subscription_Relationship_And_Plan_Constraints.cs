using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gateway.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Fix_Subscription_Relationship_And_Plan_Constraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_MerchantId",
                table: "Subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_MerchantId_Status",
                table: "Subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Plans_IsActive",
                table: "Plans");

            migrationBuilder.DropIndex(
                name: "IX_Plans_IsDefault",
                table: "Plans");

            migrationBuilder.AlterColumn<decimal>(
                name: "CustomVolumeLimitUsd",
                table: "Subscriptions",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_MerchantId",
                table: "Subscriptions",
                column: "MerchantId",
                unique: true,
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Plans_IsDefault",
                table: "Plans",
                column: "IsDefault",
                unique: true,
                filter: "\"IsDefault\" = true");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_MerchantId",
                table: "Subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Plans_IsDefault",
                table: "Plans");

            migrationBuilder.AlterColumn<decimal>(
                name: "CustomVolumeLimitUsd",
                table: "Subscriptions",
                type: "numeric",
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_MerchantId",
                table: "Subscriptions",
                column: "MerchantId");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_MerchantId_Status",
                table: "Subscriptions",
                columns: new[] { "MerchantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Plans_IsActive",
                table: "Plans",
                column: "IsActive");

            migrationBuilder.CreateIndex(
                name: "IX_Plans_IsDefault",
                table: "Plans",
                column: "IsDefault");
        }
    }
}
