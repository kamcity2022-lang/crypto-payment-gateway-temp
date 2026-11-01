using Gateway.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Gateway.Persistence;

public class GatewayDbContext : DbContext
{
    public GatewayDbContext(DbContextOptions<GatewayDbContext> options) : base(options)
    {
    }

    public DbSet<Merchant> Merchants => Set<Merchant>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<DerivedAddress> DerivedAddresses => Set<DerivedAddress>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<PaymentTransfer> PaymentTransfers => Set<PaymentTransfer>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Merchant mapping
        modelBuilder.Entity<Merchant>(b =>
        {
            b.HasKey(m => m.Id);
            b.Property(m => m.Name).HasMaxLength(150).IsRequired();
            b.Property(m => m.ApiKeyHash).HasMaxLength(256).IsRequired();
            b.Property(m => m.EncryptedApiKeySecret).IsRequired();
            b.Property(m => m.EncryptedWebhookSecret).IsRequired();
            b.HasIndex(m => m.ApiKeyHash).IsUnique();
        });

        // Wallet mapping
        modelBuilder.Entity<Wallet>(b =>
        {
            b.HasKey(w => w.Id);
            b.Property(w => w.Network).HasMaxLength(32).IsRequired();
            b.Property(w => w.AccountXPub).HasMaxLength(256).IsRequired();

            b.HasOne<Merchant>()
                .WithMany()
                .HasForeignKey(w => w.MerchantId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(w => new { w.MerchantId, w.Network }).IsUnique();
        });

        // DerivedAddress mapping
        modelBuilder.Entity<DerivedAddress>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.Address).HasMaxLength(64).IsRequired();
            b.Property(a => a.Network).HasMaxLength(32).IsRequired();

            b.HasOne<Wallet>()
                .WithMany(w => w.DerivedAddresses)
                .HasForeignKey(a => a.WalletId)
                .OnDelete(DeleteBehavior.Restrict);

            // Composite unique constraints
            b.HasIndex(a => new { a.WalletId, a.AddressIndex }).IsUnique();
            b.HasIndex(a => new { a.Network, a.Address }).IsUnique();
        });

        // Invoice mapping
        modelBuilder.Entity<Invoice>(b =>
        {
            b.HasKey(i => i.Id);
            b.Property(i => i.DepositAddress).HasMaxLength(64).IsRequired();
            b.Property(i => i.Currency).HasMaxLength(16).IsRequired();
            b.Property(i => i.Network).HasMaxLength(32).IsRequired();
            b.Property(i => i.FiatCurrency).HasMaxLength(8).IsRequired();
            b.Property(i => i.FiatAmount).HasPrecision(18, 2);
            b.Property(i => i.ExchangeRate).HasPrecision(18, 6);
            b.Property(i => i.CallbackUrl).HasMaxLength(512);
            b.Property(i => i.CheckoutToken).HasMaxLength(128).IsRequired();

            b.HasOne<Merchant>()
                .WithMany()
                .HasForeignKey(i => i.MerchantId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasOne<DerivedAddress>()
                .WithMany()
                .HasForeignKey(i => i.DerivedAddressId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasMany(i => i.Transfers)
                .WithOne()
                .HasForeignKey(t => t.InvoiceId)
                .OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(i => i.CheckoutToken).IsUnique();
            b.HasIndex(i => new { i.Status, i.ExpiresAtUtc });
        });

        // PaymentTransfer mapping
        modelBuilder.Entity<PaymentTransfer>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.Network).HasMaxLength(32).IsRequired();
            b.Property(t => t.TransactionHash).HasMaxLength(128).IsRequired();
            b.Property(t => t.TokenContractAddress).HasMaxLength(64).IsRequired();
            b.Property(t => t.FromAddress).HasMaxLength(64).IsRequired();
            b.Property(t => t.ToAddress).HasMaxLength(64).IsRequired();

            // Strict composite unique index to prevent duplicate transfer accounting
            b.HasIndex(t => new { t.Network, t.TransactionHash, t.TransferIndex }).IsUnique();
            b.HasIndex(t => t.ToAddress);
        });
        
        // OutboxEvent mapping
        modelBuilder.Entity<OutboxEvent>(b =>
        {
            b.HasKey(o => o.Id);
            b.Property(o => o.EventType).HasMaxLength(64).IsRequired();
            b.Property(o => o.DestinationUrl).HasMaxLength(512).IsRequired();
            b.Property(o => o.SecretKey).HasMaxLength(256).IsRequired();
            b.Property(o => o.Payload).IsRequired();
            b.HasIndex(o => new { o.IsProcessed, o.NextAttemptAtUtc });
        });
    }
}