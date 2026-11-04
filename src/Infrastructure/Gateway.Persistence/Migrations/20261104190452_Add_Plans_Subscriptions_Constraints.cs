using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gateway.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Add_Plans_Subscriptions_Constraints : Migration
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

            migrationBuilder.AddColumn<Guid>(
                name: "MerchantId1",
                table: "Subscriptions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Plans",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(50)",
                oldMaxLength: 50);

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "Plans",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(255)",
                oldMaxLength: 255);

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_MerchantId",
                table: "Subscriptions",
                column: "MerchantId",
                unique: true,
                filter: "\"Status\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_MerchantId1",
                table: "Subscriptions",
                column: "MerchantId1");

            migrationBuilder.CreateIndex(
                name: "IX_Plans_IsDefault",
                table: "Plans",
                column: "IsDefault",
                unique: true,
                filter: "\"IsDefault\" = true");

            migrationBuilder.AddForeignKey(
                name: "FK_Subscriptions_Merchants_MerchantId1",
                table: "Subscriptions",
                column: "MerchantId1",
                principalTable: "Merchants",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Subscriptions_Merchants_MerchantId1",
                table: "Subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_MerchantId",
                table: "Subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_MerchantId1",
                table: "Subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Plans_IsDefault",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "MerchantId1",
                table: "Subscriptions");

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

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                table: "Plans",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AlterColumn<string>(
                name: "Description",
                table: "Plans",
                type: "character varying(255)",
                maxLength: 255,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

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
