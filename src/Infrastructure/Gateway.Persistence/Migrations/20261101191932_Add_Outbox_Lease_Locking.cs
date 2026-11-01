using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gateway.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Add_Outbox_Lease_Locking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "SecretKey",
                table: "OutboxEvents",
                newName: "EncryptedSecretKey");

            migrationBuilder.AddColumn<DateTime>(
                name: "LockedUntilUtc",
                table: "OutboxEvents",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LockedUntilUtc",
                table: "OutboxEvents");

            migrationBuilder.RenameColumn(
                name: "EncryptedSecretKey",
                table: "OutboxEvents",
                newName: "SecretKey");
        }
    }
}
