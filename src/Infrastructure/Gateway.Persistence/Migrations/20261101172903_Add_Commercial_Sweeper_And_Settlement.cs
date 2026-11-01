using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gateway.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Add_Commercial_Sweeper_And_Settlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SettlementAddress",
                table: "Merchants",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SweepTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DerivedAddressId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAddress = table.Column<string>(type: "text", nullable: false),
                    DestinationAddress = table.Column<string>(type: "text", nullable: false),
                    TokenContractAddress = table.Column<string>(type: "text", nullable: false),
                    AmountUnits = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    FundingTxHash = table.Column<string>(type: "text", nullable: true),
                    SweepTxHash = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SweepTasks", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SweepTasks_DerivedAddressId",
                table: "SweepTasks",
                column: "DerivedAddressId");

            migrationBuilder.CreateIndex(
                name: "IX_SweepTasks_MerchantId_Status",
                table: "SweepTasks",
                columns: new[] { "MerchantId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SweepTasks");

            migrationBuilder.DropColumn(
                name: "SettlementAddress",
                table: "Merchants");
        }
    }
}
