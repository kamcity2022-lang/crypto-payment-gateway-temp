using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Gateway.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial_CorePaymentEngine_Hardened : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BlockchainScanCursors",
                columns: table => new
                {
                    Network = table.Column<string>(type: "text", nullable: false),
                    Address = table.Column<string>(type: "text", nullable: false),
                    LastProcessedBlockTimestamp = table.Column<long>(type: "bigint", nullable: false),
                    LastProcessedBlockNumber = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    LastFingerprint = table.Column<string>(type: "text", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BlockchainScanCursors", x => new { x.Network, x.Address });
                });

            migrationBuilder.CreateTable(
                name: "Invoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DerivedAddressId = table.Column<Guid>(type: "uuid", nullable: false),
                    DepositAddress = table.Column<string>(type: "text", nullable: false),
                    Currency = table.Column<string>(type: "text", nullable: false),
                    Network = table.Column<string>(type: "text", nullable: false),
                    FiatAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    FiatCurrency = table.Column<string>(type: "text", nullable: false),
                    ExchangeRate = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    ExchangeRateSource = table.Column<string>(type: "text", nullable: false),
                    ExchangeRateTimestampUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpectedAmountUnits = table.Column<long>(type: "bigint", nullable: false),
                    PaidAmountUnits = table.Column<long>(type: "bigint", nullable: false),
                    UnderpaymentThresholdUnits = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CallbackUrl = table.Column<string>(type: "text", nullable: false),
                    CheckoutToken = table.Column<string>(type: "text", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PaidAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "MerchantNonces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Nonce = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MerchantNonces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Merchants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    ApiKeyHash = table.Column<string>(type: "text", nullable: false),
                    EncryptedApiKeySecret = table.Column<string>(type: "text", nullable: false),
                    EncryptedWebhookSecret = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Merchants", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "OutboxEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AggregateId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    DestinationUrl = table.Column<string>(type: "text", nullable: false),
                    SecretKey = table.Column<string>(type: "text", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    IsProcessed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Wallets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MerchantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Network = table.Column<string>(type: "text", nullable: false),
                    AccountXPub = table.Column<string>(type: "text", nullable: false),
                    LastDerivationIndex = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Wallets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PaymentTransfers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Network = table.Column<string>(type: "text", nullable: false),
                    TransactionHash = table.Column<string>(type: "text", nullable: false),
                    TransferIndex = table.Column<int>(type: "integer", nullable: false),
                    TokenContractAddress = table.Column<string>(type: "text", nullable: false),
                    FromAddress = table.Column<string>(type: "text", nullable: false),
                    ToAddress = table.Column<string>(type: "text", nullable: false),
                    AmountUnits = table.Column<long>(type: "bigint", nullable: false),
                    BlockNumber = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    Confirmations = table.Column<decimal>(type: "numeric(20,0)", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ObservedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentTransfers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentTransfers_Invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalTable: "Invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DerivedAddresses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WalletId = table.Column<Guid>(type: "uuid", nullable: false),
                    AddressIndex = table.Column<long>(type: "bigint", nullable: false),
                    Address = table.Column<string>(type: "text", nullable: false),
                    Network = table.Column<string>(type: "text", nullable: false),
                    IsAllocated = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DerivedAddresses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DerivedAddresses_Wallets_WalletId",
                        column: x => x.WalletId,
                        principalTable: "Wallets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DerivedAddresses_Network_Address",
                table: "DerivedAddresses",
                columns: new[] { "Network", "Address" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DerivedAddresses_WalletId_AddressIndex",
                table: "DerivedAddresses",
                columns: new[] { "WalletId", "AddressIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_CheckoutToken",
                table: "Invoices",
                column: "CheckoutToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_Status_ExpiresAtUtc",
                table: "Invoices",
                columns: new[] { "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MerchantNonces_ExpiresAtUtc",
                table: "MerchantNonces",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_MerchantNonces_MerchantId_Nonce",
                table: "MerchantNonces",
                columns: new[] { "MerchantId", "Nonce" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Merchants_ApiKeyHash",
                table: "Merchants",
                column: "ApiKeyHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEvents_IsProcessed_NextAttemptAtUtc",
                table: "OutboxEvents",
                columns: new[] { "IsProcessed", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransfers_InvoiceId",
                table: "PaymentTransfers",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentTransfers_Network_TransactionHash_TransferIndex",
                table: "PaymentTransfers",
                columns: new[] { "Network", "TransactionHash", "TransferIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Wallets_MerchantId_Network",
                table: "Wallets",
                columns: new[] { "MerchantId", "Network" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BlockchainScanCursors");

            migrationBuilder.DropTable(
                name: "DerivedAddresses");

            migrationBuilder.DropTable(
                name: "MerchantNonces");

            migrationBuilder.DropTable(
                name: "Merchants");

            migrationBuilder.DropTable(
                name: "OutboxEvents");

            migrationBuilder.DropTable(
                name: "PaymentTransfers");

            migrationBuilder.DropTable(
                name: "Wallets");

            migrationBuilder.DropTable(
                name: "Invoices");
        }
    }
}
